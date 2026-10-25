using dc;
using dc.en;
using dc.en.inter;
using dc.en.ltrap;
using dc.pr;
using dc.ui;
using HaxeProxy.Runtime;
using ModCore.Utilities;

namespace DeadCellsLocalMultiplayerMod;

/// <summary>
/// Part of <see cref="LocalCoop"/>: the level traps that resolve their victim through the global
/// hero, so player two either never triggers them or gets player one hurt instead.
/// <para>
/// The plant trap has its own file (<c>LocalCoop.Plant.cs</c>) because its gate is an early
/// <c>return</c> on the touching entity. These are the rest of the audit over <c>dc.en.ltrap</c> /
/// <c>dc.en.inter</c> — every trap class that reads <c>Game.ME.hero</c> for <b>who</b> triggered
/// or is hit. None of them re-runs the game's update a second time: <c>Entity.fixedUpdate</c>
/// ticks the trap's own cooldowns, so a double call would advance every timer twice per frame.
/// Each hook instead answers the trap's hero question for player two <b>without</b> re-entering
/// the update — by re-asking a side-effect-free predicate, or by calling the trap's own public
/// <c>open()</c> after reproducing its gate, or by picking the right global before a single call.
/// </para>
/// <list type="number">
/// <item><description>
/// <b>RetractableSpikes</b> (the spikes that hide while a hero stands on them): its whole
/// <c>fixedUpdate</c> is gated by <c>checkHeroPositionInCase()</c>, which only ever inspects the
/// global's cell. That predicate is side-effect-free, so the hook asks it once normally (player
/// one) and, when it says no, again with the global on player two — the single update then does
/// the retract work for whichever hero is actually standing there.</description></item>
/// <item><description>
/// <b>UnstablePF</b> (crumbling platform) and <b>UnstableGround</b> (crumbling floor): their
/// <c>fixedUpdate</c> reads the global's cell inline and calls <c>open()</c> when a hero stands
/// on the trap mid-air. The hook runs the game's update untouched (player one's case), then
/// reproduces the same gate for player two and calls the trap's own <c>open()</c> — which is
/// idempotent through its cooldown, exactly like the vanilla path.</description></item>
/// <item><description>
/// <b>TriggerTrap</b> (the swinging blade): its per-entity loop does the reach check per entity
/// but then replaces the victim with <c>_level.game.hero</c> — so player two walking into the
/// blade hurts player one. The hook reproduces the trap's reach test (ball centre from
/// <c>ang</c>/<c>radius</c>, hit when <c>bRadius + entity.radius</c> exceeds the centre distance)
/// and runs the single game update with the global on player two only when player two is in reach
/// and player one is not; when player one is in reach the vanilla victim choice is already
/// correct, and the per-victim hit cooldown handles the both-in-reach case as vanilla does.</description></item>
/// <item><description>
/// <b>PulleyAttachPoint</b> (the rope pulley): the pressure plate knows who touched it
/// (<c>trigger(by)</c>), but <c>Pulley.trigger</c> then does <c>Game.ME.hero.setPosPixel(...)</c>
/// + <c>startHoldPull()</c> — player two jumping onto the pulley teleported player one onto the
/// rope. Re-pointing the global at <c>by</c> for the duration of the single call fixes it at the
/// one place the victim is resolved.</description></item>
/// <item><description>
/// <b>FlipPF</b> (the flipping platform): it subscribes <c>Game.ME.hero.heroSignals.landSignal</c>
/// at init, so only player one's landing ever reaches <c>onHeroLand</c> — which reads the global's
/// cell anyway. The fix tracks the live platforms (init/destroy hooks) and calls their
/// <c>onHeroLand</c> directly when player two lands, with the global pointed at player two. It is
/// deliberately NOT a signal broadcast: the player's own skills (DodgeRoll, Wings, SeismicStomp,
/// DiveAttack) subscribe the same signal, so a broadcast would hand player one phantom dodge
/// charges and wing refills on every landing by player two.</description></item>
/// </list>
/// <para>
/// Not here because not broken: the damage traps (<c>Lava</c>, <c>Spikes</c>, <c>RotatingAxes</c>,
/// <c>RotatingBall</c>, <c>LightningWall</c>, the shooters, the bombs) all hit through
/// <c>_AttackUtils.hit(attackData, entity)</c> per entity, and <c>AutoRetractableSpikes</c> reads
/// the global only for a volume-by-distance effect on its sound. Lava's other <c>game.hero</c>
/// uses are the Terraria blueprint easter-egg and a screen-shake anchor.
/// </para>
/// </summary>
internal static partial class LocalCoop
{
    /// <summary>Registers the trap hooks. Called from <see cref="InstallEntityHooks"/>.</summary>
    internal static void InstallTrapHooks()
    {
        Hook_RetractableSpikes.checkHeroPositionInCase += OnRetractableSpikesCheckHeroPosition;
        Hook_UnstablePF.fixedUpdate += OnUnstablePFFixedUpdate;
        Hook_UnstableGround.fixedUpdate += OnUnstableGroundFixedUpdate;
        Hook_TriggerTrap.fixedUpdate += OnTriggerTrapFixedUpdate;
        Hook_PulleyAttachPoint.trigger += OnPulleyAttachPointTrigger;
        Hook_FlipPF.init += OnFlipPFInit;
        Hook_FlipPF.destroy += OnFlipPFDestroy;
        Hook_Hero.onLand += OnHeroLandBroadcast;
    }

    /// <summary>
    /// True when a second hero's answer has to be asked at all: a live co-op run with both heroes
    /// in play, and the global currently on player one (a swap already in flight — the head, a
    /// window — must not be second-guessed).
    /// </summary>
    private static bool TrapsShouldAskHero2()
    {
        var game = Game.Class.ME;
        return Enabled
            && game != null && ReferenceEquals(game.hero, _hero1)
            && _hero1 != null && !_hero1.destroyed
            && _hero2 != null && !_hero2.destroyed;
    }

    /// <summary>
    /// Retractable spikes: ask the game's own position predicate with the global on player two
    /// when player one is not on the case. The predicate only reads hero geometry, so calling it
    /// twice is safe; the single <c>fixedUpdate</c> that consumes the answer runs once.
    /// </summary>
    private static bool OnRetractableSpikesCheckHeroPosition(
        Hook_RetractableSpikes.orig_checkHeroPositionInCase orig, RetractableSpikes self)
    {
        if (orig(self))
        {
            return true;
        }

        if (!TrapsShouldAskHero2())
        {
            return false;
        }

        var answer = false;
        RunAsTouchedHero(_hero2, () => answer = orig(self));
        return answer;
    }

    /// <summary>
    /// Unstable platform: the game's update handles player one; this adds player two's case. The
    /// gate is the trap's own — <c>ready</c>, hero on the trap's cell, hero one cell above and
    /// more than 60% into it — and <c>open()</c> is the trap's own trigger, idempotent through its
    /// cooldown.
    /// </summary>
    private static void OnUnstablePFFixedUpdate(Hook_UnstablePF.orig_fixedUpdate orig, UnstablePF self)
    {
        orig(self);

        if (!TrapsShouldAskHero2() || !self.ready)
        {
            return;
        }

        var hero2 = _hero2!;
        if (hero2.cx == self.cx && hero2.cy == self.cy - 1 && hero2.yr > 0.6)
        {
            try
            {
                self.open();
            }
            catch (Exception ex)
            {
                LogWarn("[LocalCoop] the unstable platform refused player two: {Message}", ex.Message);
            }
        }
    }

    /// <summary>
    /// Unstable ground: same shape as the platform — the trap's own gate is
    /// <c>ready &amp;&amp; unstable &amp;&amp; hero.cx in [cx, cx+width) &amp;&amp; hero.yr &gt; 0.6</c>.
    /// </summary>
    private static void OnUnstableGroundFixedUpdate(Hook_UnstableGround.orig_fixedUpdate orig, UnstableGround self)
    {
        orig(self);

        if (!TrapsShouldAskHero2() || !self.ready || !self.unstable)
        {
            return;
        }

        var hero2 = _hero2!;
        if (hero2.cx >= self.cx && hero2.cx < self.cx + self.width && hero2.yr > 0.6)
        {
            try
            {
                self.open();
            }
            catch (Exception ex)
            {
                LogWarn("[LocalCoop] the unstable ground refused player two: {Message}", ex.Message);
            }
        }
    }

    /// <summary>
    /// The swinging blade: one game update, but with the global on player two when player two is
    /// in reach and player one is not — that is the only case where the game's victim choice
    /// (<c>_level.game.hero</c>) would hurt the wrong hero. Re-running the update is not an
    /// option: it advances the pendulum and ticks cooldowns.
    /// </summary>
    private static void OnTriggerTrapFixedUpdate(Hook_TriggerTrap.orig_fixedUpdate orig, TriggerTrap self)
    {
        if (!TrapsShouldAskHero2() || !self.triggered || self.isBallInCollision())
        {
            orig(self);
            return;
        }

        var p1In = WithinBladeReach(self, _hero1!);
        var p2In = WithinBladeReach(self, _hero2!);

        if (p2In && !p1In)
        {
            RunAsTouchedHero(_hero2, () => orig(self));
            return;
        }

        orig(self);
    }

    /// <summary>
    /// The blade's own reach test: ball centre is the trap's centre offset by <c>ang * radius</c>,
    /// and the game hits when <c>bRadius + entity.radius</c> exceeds the distance between centres.
    /// </summary>
    private static bool WithinBladeReach(TriggerTrap trap, Hero hero)
    {
        try
        {
            var ballX = ((double)trap.cx + trap.xr) * 24.0
                + System.Math.Cos(trap.ang) * trap.radius;
            var ballY = ((double)trap.cy + trap.yr) * 24.0 - trap.hei * 0.5
                + System.Math.Sin(trap.ang) * trap.radius;
            var heroX = ((double)hero.cx + hero.xr) * 24.0;
            var heroY = ((double)hero.cy + hero.yr) * 24.0 - hero.hei * 0.5;
            var dx = heroX - ballX;
            var dy = heroY - ballY;
            var dist = System.Math.Sqrt(dx * dx + dy * dy);
            return trap.bRadius + hero.radius > dist;
        }
        catch
        {
            // An un-laid-out trap: fall through to the vanilla path.
            return false;
        }
    }

    /// <summary>
    /// The pulley: the plate knows who touched it, but <c>Pulley.trigger</c> puts
    /// <c>Game.ME.hero</c> on the rope. Run the single game call with the global on the toucher.
    /// </summary>
    private static void OnPulleyAttachPointTrigger(Hook_PulleyAttachPoint.orig_trigger orig, PulleyAttachPoint self, Entity by)
    {
        RunAsTouchedHero(by, () => orig(self, by));
    }

    /// <summary>
    /// The flipping platform subscribes the GLOBAL hero's <c>landSignal</c> at init, so a landing by
    /// player two never reaches <c>onHeroLand</c> — which reads the global's cell anyway.
    /// <para>
    /// The fix is deliberately NOT to dispatch player one's land signal on player two's behalf: the
    /// proxy audit of <c>landSignal.add</c> shows the player's own skills subscribe it too
    /// (DodgeRoll, Wings, SeismicStomp, DiveAttack), so a broadcast would hand player one a phantom
    /// dodge charge or wing refill every time player two lands. Instead the mod tracks the live
    /// platforms here and calls <c>onHeroLand</c> on each one directly, with the global pointed at
    /// player two — only the platform sees the landing, and its own 10s cooldown keeps the semantics
    /// "one platform, one flip per landing window".
    /// </para>
    /// </summary>
    private static readonly System.Collections.Generic.List<FlipPF> _flipPlatforms = new();

    private static void OnFlipPFInit(Hook_FlipPF.orig_init orig, FlipPF self)
    {
        orig(self);
        if (!_flipPlatforms.Contains(self))
        {
            _flipPlatforms.Add(self);
        }
    }

    private static void OnFlipPFDestroy(Hook_FlipPF.orig_destroy orig, FlipPF self)
    {
        orig(self);
        _flipPlatforms.Remove(self);
    }

    /// <summary>
    /// Drops the tracked trap instances. Called on a run reset: a level teardown that destroyed a
    /// platform without running its <c>destroy</c> hook would otherwise leave a corpse in the list.
    /// </summary>
    private static void ForgetTrapInstances()
    {
        _flipPlatforms.Clear();
    }

    private static void OnHeroLandBroadcast(Hook_Hero.orig_onLand orig, Hero self, double height)
    {
        orig(self, height);

        if (!Enabled || !ReferenceEquals(self, _hero2) || self.destroyed
            || _flipPlatforms.Count == 0)
        {
            return;
        }

        // The global must be player one's view of the world for the platforms' own logic; the swap
        // is per call so a platform throwing cannot strand the global on player two.
        for (var i = _flipPlatforms.Count - 1; i >= 0; i--)
        {
            var platform = _flipPlatforms[i];
            if (platform == null || platform.destroyed)
            {
                _flipPlatforms.RemoveAt(i);
                continue;
            }

            try
            {
                RunAsTouchedHero(self, () => platform.onHeroLand(height));
            }
            catch (Exception ex)
            {
                LogVerbose($"[LocalCoop] a flipping platform refused player two's landing: {ex.Message}");
            }
        }
    }
}
