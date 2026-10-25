using dc.en;
using dc.hl.types;
using dc.pr;
using dc.tool;
using dc.tool.atk;

namespace DeadCellsLocalMultiplayerMod;

/// <summary>
/// Part of <see cref="LocalCoop"/>: detached head (Homunculus): owner swap and hooks.
/// </summary>
internal static partial class LocalCoop
{
    /// <summary>
    /// The detached head (<c>en.Homunculus</c>) resolves nearly everything through
    /// <c>_level.game.hero</c> — the single global hero, i.e. player one:
    /// <list type="bullet">
    /// <item><c>controlsToMe()</c> / <c>controlsToHero()</c> lock and unlock
    /// <c>_level.game.hero.controller</c>, i.e. player one's pad, so player two never loses
    /// control of its body and the head is driven by the wrong pad;</item>
    /// <item><c>get_headX()/get_headY()</c> decide where the head spawns and returns;</item>
    /// <item><c>UsableBody.createFromExistingHero(_level.game.hero)</c> picks the body left
    /// behind.</item>
    /// </list>
    /// Make <c>game.hero</c> mean the owning hero while the head's own code runs, then restore
    /// it, so each head uses its own hero's pad and body.
    /// </summary>
    private static void RunAsOwningHero(Homunculus self, Action body)
    {
        var owner = self.sourceSkill?.hero;
        var game = self._level?.game;
        var swap = owner != null
            && game != null
            && !owner.destroyed
            && !ReferenceEquals(game.hero, owner)
            && (ReferenceEquals(owner, _hero1) || ReferenceEquals(owner, _hero2));

        var previousHero = game?.hero;
        var previousOwner = _homunculusOwner;
        _homunculusOwner = owner;
        try
        {
            if (swap)
            {
                game!.hero = owner;
            }

            body();
        }
        finally
        {
            if (swap)
            {
                game!.hero = previousHero;
            }

            _homunculusOwner = previousOwner;
        }
    }

    /// <summary>
    /// The head creates its own <c>ControllerAccess</c> from the global
    /// <c>Boot.ME.controller</c>, so it reads player one's pad. Point it at the controller the
    /// owning hero uses instead, so player two's head answers player two's pad.
    /// </summary>
    private static void BindHomunculusController(Homunculus self)
    {
        var owner = self.sourceSkill?.hero;
        var access = self.controller;
        if (owner?.controller == null || access == null || owner.destroyed)
        {
            return;
        }

        var target = owner.controller.parent;
        if (target == null || ReferenceEquals(access.parent, target))
        {
            return;
        }

        access.parent = target;
        LogVerbose($"[LocalCoop] homunculus head bound to {(ReferenceEquals(owner, _hero2) ? "P2" : "P1")} " +
            $"pad {target.gc?.device?.index}");
    }

    private static void OnHomunculusInitGfx(Hook_Homunculus.orig_initGfx orig, Homunculus self)
    {
        // controlsToMe() is called from inside initGfx() and locks
        // _level.game.hero.controller, so the owning hero must already be the global hero here —
        // otherwise player two throws its head and *player one's* body freezes instead.
        RunAsOwningHero(self, () =>
        {
            try
            {
                orig(self);
                BindHomunculusController(self);
                SetHeadOut(self.sourceSkill?.hero, true);
            }
            catch (Exception ex)
            {
                LogWarn("[LocalCoop] homunculus init failed: {Message}", ex.Message);
            }
        });
    }

    /// <summary>
    /// While a head is out, <c>Homunculus.preUpdate()</c> walks every <c>Hero</c> on the level
    /// (<c>_level.entitiesByClass[Hero]</c>) and pushes <c>HeadMode.None</c> on each one, so both
    /// heroes lose their heads no matter who threw it. Head-mode changes issued while a head is
    /// out belong to the hero that owns that head.
    /// </summary>
    private static void OnHeroSetHeadMode(
        Hook_Hero.orig_setHeadMode orig,
        Hero self,
        HeadMode m,
        double durationS,
        int? id)
    {
        var owner = _homunculusOwner;
        if (owner != null && !ReferenceEquals(self, owner))
        {
            return;
        }

        orig(self, m, durationS, id);
    }

    private static void OnHomunculusInit(Hook_Homunculus.orig_init orig, Homunculus self)
        => RunAsOwningHero(self, () => orig(self));

    private static void OnHomunculusOnRelocate(Hook_Homunculus.orig_onRelocate orig, Homunculus self)
        => RunAsOwningHero(self, () => orig(self));

    private static void OnHomunculusComeBack(Hook_Homunculus.orig_comeBack orig, Homunculus self)
    {
        var owner = self.sourceSkill?.hero;
        RunAsOwningHero(self, () => WithOwnHeadOnly(owner, () => { orig(self); return true; }));
        SetHeadOut(owner, false);
    }

    /// <summary>
    /// Turns the hero into a slumped <c>UsableBody</c> and hides the hero itself. Called from
    /// outside the per-frame methods, so it needs the owner swap too — otherwise player two
    /// throwing its head collapsed *player one's* body onto the floor.
    /// </summary>
    private static UsableBody OnHomunculusDetachFromHero(
        Hook_Homunculus.orig_detachFromHero orig,
        Homunculus self)
    {
        var owner = self.sourceSkill?.hero;
        UsableBody? body = null;
        RunAsOwningHero(self, () => body = orig(self));
        return body!;
    }

    /// <summary>
    /// Runs <paramref name="body"/> with the heads belonging to the *other* hero temporarily
    /// removed from <c>entitiesByClass[Homunculus]</c>, restoring them afterwards.
    /// <para>
    /// Four skill/entity methods look at "the head on the level" without checking whose it is,
    /// taking <c>arr[0]</c> or killing the whole bucket: the skill's <c>prepare()</c> (press again
    /// to recall), <c>isUsingInputs()</c> (reads that head's controller and pad),
    /// <c>onOwnerDamaged()</c> (damage recalls the head) and the skill's <c>onEnd()</c> (kills
    /// EVERY head in the bucket before creating the new one — without the filter, the second
    /// player's throw recalled the first player's head). The entity methods themselves resolve
    /// their owner through <c>sourceSkill.hero</c> and go through <see cref="RunAsOwningHero"/>
    /// instead.
    /// </para>
    /// </summary>
    private static T WithOwnHeadOnly<T>(Hero? owner, Func<T> body)
    {
        if (owner == null)
        {
            return body();
        }

        var raw = owner._level?.entitiesByClass?.get(HomunculusClid);
        if (raw == null)
        {
            return body();
        }

        // Level entity buckets are ArrayObj, whose length is a plain field. Other Haxe arrays
        // expose get_length() as an accessor, which the dynamic binder refuses to call — so read
        // the count from the concrete type, and skip the fix rather than risk a cast failure.
        if (raw is not ArrayObj list)
        {
            return body();
        }

        var count = list.length;
        var hidden = new List<(int Index, object Item)>();
        for (var i = count - 1; i >= 0; i--)
        {
            var item = list.getDyn(i);
            if (item is Homunculus head && !ReferenceEquals(head.sourceSkill?.hero, owner))
            {
                hidden.Insert(0, (i, item));
                list.remove(item);
            }
        }

        if (hidden.Count == 0)
        {
            return body();
        }

        try
        {
            return body();
        }
        finally
        {
            for (var i = hidden.Count - 1; i >= 0; i--)
            {
                list.insert(hidden[i].Index, hidden[i].Item);
            }
        }
    }

    private static bool OnHomunculusPrepare(
        dc.tool.mainSkills.Hook_Homunculus.orig_prepare orig,
        dc.tool.mainSkills.Homunculus self)
    {
        var owner = self.hero; // skill-side hook: HeroMainSkill.hero
        // prepare() reads entitiesByClass[Homunculus] without checking whose head it is:
        // a non-empty bucket means "recall arr[0]", so the other player's out head must be
        // hidden here or this press recalls it instead of throwing a new head. The kill-all
        // in the skill's onEnd() is filtered the same way (OnHomunculusSkillEnd).
        return WithOwnHeadOnly(owner, () => orig(self));
    }

    private static bool OnHomunculusIsUsingInputs(
        dc.tool.mainSkills.Hook_Homunculus.orig_isUsingInputs orig,
        dc.tool.mainSkills.Homunculus self)
        => WithOwnHeadOnly(self.hero, () => orig(self));

    private static void OnHomunculusOnOwnerDamaged(
        dc.tool.mainSkills.Hook_Homunculus.orig_onOwnerDamaged orig,
        dc.tool.mainSkills.Homunculus self,
        AttackData attack)
        => WithOwnHeadOnly(self.hero, () => { orig(self, attack); return true; });

    private static void OnHomunculusSkillStart(
        dc.tool.mainSkills.Hook_Homunculus.orig_onStart orig,
        dc.tool.mainSkills.Homunculus self)
        => WithOwnHeadOnly(self.hero, () => { orig(self); return true; });

    /// <summary>
    /// The skill's <c>onEnd()</c> runs when the throw animation finishes and creates the head
    /// entity. Its first step kills EVERY <c>en.Homunculus</c> in the level bucket — the other
    /// player's out head included. Hide the other owners' heads for its duration so the kill
    /// loop only ever sees (and kills) this owner's own head.
    /// </summary>
    private static void OnHomunculusSkillEnd(
        dc.tool.mainSkills.Hook_Homunculus.orig_onEnd orig,
        dc.tool.mainSkills.Homunculus self)
        => WithOwnHeadOnly(self.hero, () => { orig(self); return true; });

    private static string HeroName(Hero? hero)
        => ReferenceEquals(hero, _hero1) ? "P1"
            : ReferenceEquals(hero, _hero2) ? "P2"
            : hero == null ? "none" : "other";

    private static void OnHomunculusControlsToHero(
        Hook_Homunculus.orig_controlsToHero orig,
        Homunculus self)
        => RunAsOwningHero(self, () => orig(self));

    private static void OnHomunculusControlsToMe(Hook_Homunculus.orig_controlsToMe orig, Homunculus self)
        => RunAsOwningHero(self, () => orig(self));

    private static void OnHomunculusDash(Hook_Homunculus.orig_dash orig, Homunculus self, int dir)
        => RunAsOwningHero(self, () => orig(self, dir));

    private static void OnHomunculusOnLand(Hook_Homunculus.orig_onLand orig, Homunculus self, double floors)
        => RunAsOwningHero(self, () => orig(self, floors));

    private static void OnHomunculusStartClimbing(Hook_Homunculus.orig_startClimbing orig, Homunculus self)
        => RunAsOwningHero(self, () => orig(self));

    private static void OnHomunculusStopClimbing(Hook_Homunculus.orig_stopClimbing orig, Homunculus self)
        => RunAsOwningHero(self, () => orig(self));

    private static void OnHomunculusReleaseItem(
        Hook_Homunculus.orig_releaseItem orig,
        Homunculus self,
        bool autoPick)
        => RunAsOwningHero(self, () => orig(self, autoPick));

    private static void OnHomunculusUpdateFocus(Hook_Homunculus.orig_updateFocus orig, Homunculus self)
        => RunAsOwningHero(self, () => orig(self));

    private static void OnHomunculusPreUpdate(Hook_Homunculus.orig_preUpdate orig, Homunculus self)
        => RunAsOwningHero(self, () => orig(self));

    private static void OnHomunculusFixedUpdate(Hook_Homunculus.orig_fixedUpdate orig, Homunculus self)
        => RunAsOwningHero(self, () => orig(self));

    private static void OnHomunculusPostUpdate(Hook_Homunculus.orig_postUpdate orig, Homunculus self)
        => RunAsOwningHero(self, () => orig(self));

    private static void OnHomunculusOnDie(Hook_Homunculus.orig_onDie orig, Homunculus self)
    {
        RunAsOwningHero(self, () => orig(self));
        SetHeadOut(self.sourceSkill?.hero, false);
    }

    private static void OnHomunculusDispose(Hook_Homunculus.orig_dispose orig, Homunculus self)
    {
        RunAsOwningHero(self, () => orig(self));
        SetHeadOut(self.sourceSkill?.hero, false);
    }
}
