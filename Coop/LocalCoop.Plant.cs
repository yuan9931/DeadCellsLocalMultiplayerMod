using dc;
using dc.en;
using dc.en.ltrap;
using dc.pr;
using dc.ui;
using HaxeProxy.Runtime;
using ModCore.Utilities;

namespace DeadCellsLocalMultiplayerMod;

/// <summary>
/// Part of <see cref="LocalCoop"/>: the carnivorous plant trap (Chompoo) for both heroes.
/// <para>
/// <b>The trap resolves its victim through the global hero.</b> <c>CarnivorousPlant.onTouch(e)</c>
/// starts with <c>Hero hero = Game.ME.hero; if (e != hero &amp;&amp; !(e is Mob)) return;</c> — and
/// <c>Hero : Entity</c>, NOT <c>Mob</c> (verified in the decompiled <c>en/Hero.cs</c>), so a hero
/// that is not the global one is dropped on the first line. The rest of the machine is the same
/// shape: <c>bumpEntity</c>'s guard, and <c>close()</c>'s bite — <c>_AttackUtils.hit(attackData,
/// Game.ME.hero)</c> plus the cancelVelocities / cancelMainSkills / stun / per-hero cooldown writes
/// at CarnivorousPlant.cs:3108-3184 — all resolve the victim through the global.
/// </para>
/// <para>
/// <b>Wrapping <c>onTouch</c>/<c>bumpEntity</c> alone is not enough, and the field report proved
/// it:</b> "P2 walks onto the plant and is bounced straight out, never bitten." The opening works
/// (the plant really opens under P2 — the touch reaches <c>heroWasInside = true</c> inside the
/// swap), but the per-frame half of the machine never sees P2: <c>fixedUpdate</c>'s scan consumes
/// <c>heroWasInside</c> against <c>Game.ME.hero</c>'s hitbox (IL_0297), fires <c>close()</c> from
/// that same check (IL_0300), and does its "hero left the plant" check as <c>onTouch(Game.ME.hero)</c>
/// (IL_039a) — with the global on P1 standing elsewhere, the plant's mouth never matches P2, so
/// <c>close()</c> never bites P2. Then the physics keeps touching the open plant: the first
/// touch-while-open records P2 in the per-entity bounce cooldown, the next one meets
/// <c>if (fastCheck2.exists(201326592)) ... bumpEntity(e)</c> and spits P2 out. A closed-plant
/// cooldown callback (<c>onCooldownEnd("close")</c>) would also run <c>close()</c> with the global
/// on P1 wherever P1 happens to be — biting P1 through a plant P2 triggered.
/// </para>
/// <para>
/// <b>The fix: the whole state machine runs "as" the hero inside.</b> The touch hooks keep their
/// per-call swap and additionally remember which of our heroes is on the plant; the
/// <c>fixedUpdate</c>/<c>postUpdate</c> hooks run the game's own frame for that plant with
/// <c>Game.ME.hero</c> pointed at the remembered hero when it is player two. The remembered hero
/// is validated every frame (same level, not destroyed, actually in the mouth area), so the swap
/// can never outlive the situation. The <c>close</c>/<c>open</c>/<c>onCooldownEnd</c> entries are
/// wrapped too — and that was the missing half: the plant's REAL bite comes from the delayed
/// "close" cooldown callback <c>open()</c> schedules (~1.25s), which the level ticks OUTSIDE the
/// plant's <c>fixedUpdate</c>, so the wrapped tick alone fixed the mouth but never the damage
/// ("bites now, still no damage" — a one-shot in-game log proved it: the damaging close only
/// showed <c>global=P2</c> once those three late entries were wrapped). Player one's path never
/// swaps anything: <c>RunAsTouchedHero</c> no-ops when the global is already the right hero.
/// </para>
/// </summary>
internal static partial class LocalCoop
{
    /// <summary>Which of our heroes the plant last felt under its leaves, per live plant.</summary>
    private static readonly System.Collections.Generic.Dictionary<CarnivorousPlant, Hero> _plantInside = new();

    /// <summary>Registers the plant hooks. Called from <see cref="InstallEntityHooks"/>.</summary>
    internal static void InstallPlantHooks()
    {
        Hook_CarnivorousPlant.onTouch += OnCarnivorousPlantTouch;
        Hook_CarnivorousPlant.bumpEntity += OnCarnivorousPlantBumpEntity;
        Hook_CarnivorousPlant.fixedUpdate += OnCarnivorousPlantFixedUpdate;
        Hook_CarnivorousPlant.postUpdate += OnCarnivorousPlantPostUpdate;
        // The real bite does NOT come from the fixedUpdate scan: open() schedules a delayed "close"
        // cooldown (~1.25s) and the LEVEL ticks that callback outside this plant's fixedUpdate, so
        // close() ran with the global on P1 and P2 took nothing ("bites but no damage" — confirmed
        // by a one-shot in-game log: the damaging close showed global=P2 inside=P2 only once these
        // three late entries were wrapped; P2 248->174). Wrap close/open/onCooldownEnd as the
        // remembered inside-hero too; RunAsTouchedHero no-ops for P1.
        Hook_CarnivorousPlant.close += (orig, self) =>
            RunAsTouchedHero(ValidatePlantInside(self), () => orig(self));
        Hook_CarnivorousPlant.open += (orig, self) =>
            RunAsTouchedHero(ValidatePlantInside(self), () => orig(self));
        Hook_CarnivorousPlant.onCooldownEnd += (orig, self, k, sub) =>
            RunAsTouchedHero(ValidatePlantInside(self), () => orig(self, k, sub));
    }

    /// <summary>Drops remembered plants. Called from the run reset, like the other trap lists.</summary>
    private static void ForgetPlantInstances()
    {
        _plantInside.Clear();
    }

    /// <summary>The plant was touched by an entity. Run the game's own logic as the touched hero.</summary>
    private static void OnCarnivorousPlantTouch(Hook_CarnivorousPlant.orig_onTouch orig, CarnivorousPlant self, Entity e)
    {
        TrackPlantTouchedBy(self, e);
        RunAsTouchedHero(e, () => orig(self, e));
    }

    /// <summary>The plant is bumping an entity. Same re-pointing: its area check reads the global.</summary>
    private static void OnCarnivorousPlantBumpEntity(Hook_CarnivorousPlant.orig_bumpEntity orig, CarnivorousPlant self, Entity e)
    {
        RunAsTouchedHero(e, () => orig(self, e));
    }

    /// <summary>
    /// The plant's per-frame machine: its scan of <c>heroWasInside</c>, its <c>close()</c> trigger
    /// and its leave-check all address <c>Game.ME.hero</c>. Run it as the hero inside when that is
    /// player two, so a plant opened by P2 bites P2 instead of looking for P1.
    /// </summary>
    private static void OnCarnivorousPlantFixedUpdate(Hook_CarnivorousPlant.orig_fixedUpdate orig, CarnivorousPlant self)
    {
        var inside = ValidatePlantInside(self);
        RunAsTouchedHero(inside, () => orig(self));
    }

    /// <summary>Same for the graphics half: its "hero left" reset reads the global's position.</summary>
    private static void OnCarnivorousPlantPostUpdate(Hook_CarnivorousPlant.orig_postUpdate orig, CarnivorousPlant self)
    {
        var inside = ValidatePlantInside(self);
        RunAsTouchedHero(inside, () => orig(self));
    }

    /// <summary>
    /// Remembers our heroes touching this plant. Mobs and items are not tracked: the game's own
    /// machine only ever asks about a hero, and a stale hero reference is dropped by the per-frame
    /// validation below.
    /// </summary>
    private static void TrackPlantTouchedBy(CarnivorousPlant self, Entity e)
    {
        if (!Enabled || self.destroyed)
        {
            return;
        }

        if (e is Hero hero && (ReferenceEquals(hero, _hero1) || ReferenceEquals(hero, _hero2)))
        {
            _plantInside[self] = hero;
        }
    }

    /// <summary>
    /// Returns the remembered hero while it is genuinely on this plant — same level, alive, inside
    /// the mouth box — and forgets the plant otherwise. Returning null is the common case: most
    /// plants are idle, and an idle plant must run with the global untouched.
    /// </summary>
    private static Hero? ValidatePlantInside(CarnivorousPlant self)
    {
        if (self.destroyed || !_plantInside.TryGetValue(self, out var hero))
        {
            return null;
        }

        var keep = hero != null
            && !hero.destroyed
            && !self.destroyed
            && ReferenceEquals(hero._level, self._level)
            && (ReferenceEquals(hero, _hero1) || ReferenceEquals(hero, _hero2))
            && WithinMouth(self, hero!);

        if (!keep)
        {
            _plantInside.Remove(self);
            return null;
        }

        return hero;
    }

    /// <summary>
    /// The plant's mouth is roughly a 3-tile-wide box around its graphic. A loose box is fine: the
    /// remembered hero is only swapped in for this plant's own calls, and the game's own hitbox and
    /// cooldown checks still decide whether anything happens.
    /// </summary>
    private static bool WithinMouth(CarnivorousPlant plant, Hero hero)
    {
        try
        {
            var plantX = ((double)plant.cx + plant.xr) * 24.0;
            var plantY = ((double)plant.cy + plant.yr) * 24.0 - plant.hei * 0.5;
            var heroX = ((double)hero.cx + hero.xr) * 24.0;
            var heroY = ((double)hero.cy + hero.yr) * 24.0 - hero.hei * 0.5;
            var dx = System.Math.Abs(heroX - plantX);
            var dy = System.Math.Abs(heroY - plantY);
            return dx <= 48.0 && dy <= 60.0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Runs <paramref name="body"/> with <c>Game.ME.hero</c> pointed at <paramref name="e"/> when
    /// that is one of our two heroes and not already the global. Anything else (player one, a mob,
    /// a thrown item) runs untouched, so the vanilla path is never wrapped needlessly.
    /// <para>
    /// Deliberately its own save/restore rather than <see cref="RunWindowAs"/>: that helper also
    /// drives the pause-window picker state, which has nothing to do with a combat entity, and a
    /// swap inside a hook must never leak — the restore is in a finally.
    /// </para>
    /// </summary>
    private static void RunAsTouchedHero(Entity? e, Action body)
    {
        var game = Game.Class.ME;
        var hero = e as Hero;
        var swap = Enabled
            && game != null
            && hero != null
            && !hero.destroyed
            && !ReferenceEquals(game.hero, hero)
            && (ReferenceEquals(hero, _hero1) || ReferenceEquals(hero, _hero2));

        if (!swap)
        {
            body();
            return;
        }

        var previous = game!.hero;
        try
        {
            game.hero = hero;
            body();
        }
        catch (Exception ex)
        {
            // An exception escaping a hook is fatal to the game; the trap failing is not.
            LogWarn("[LocalCoop] the plant trap hook failed: {Message}", ex.Message);
        }
        finally
        {
            game.hero = previous;
        }
    }
}
