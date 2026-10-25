using dc.en;
using dc.pr;

namespace DeadCellsLocalMultiplayerMod;

/// <summary>
/// Part of <see cref="LocalCoop"/>: shared health, skill cooldowns and equipment refresh.
/// </summary>
internal static partial class LocalCoop
{
    /// <summary>
    /// Keeps one shared health value. Damage can land on either hero (mobs hit hero two
    /// directly), so the hero that dropped below the last known value wins; when neither
    /// dropped, the higher value is a heal and applies to both.
    /// <para>
    /// The result is clamped to zero: a negative life is drawn straight onto player one's bar
    /// (the game owns that bar) as a number below zero. The clamp goes on the converged value,
    /// never on the individual reads — "who dropped" has to stay detectable or damage would be
    /// lost again.
    /// </para>
    /// </summary>
    private static void SyncSharedLife(Hero hero1, Hero hero2)
    {
        var life1 = hero1.life;
        var life2 = hero2.life;

        int shared;
        if (_sharedLife < 0)
        {
            // First frame of the pool: take the lower of the two so a half-dead hero is not
            // resurrected by the sync itself. Clamp first, so a negative start is not adopted.
            shared = System.Math.Min(System.Math.Max(0, life1), System.Math.Max(0, life2));
        }
        else if (life1 < _sharedLife)
        {
            shared = life1;
        }
        else if (life2 < _sharedLife)
        {
            shared = life2;
        }
        else
        {
            shared = System.Math.Max(life1, life2);
        }

        if (shared < 0)
        {
            shared = 0;
        }

        if (life1 != shared)
        {
            hero1.life = shared;
        }
        if (life2 != shared)
        {
            hero2.life = shared;
        }
        _sharedLife = shared;
    }

    /// <summary>
    /// A throwable skill can be left inside its charging state when its input state is
    /// disturbed: the charge FX and the looping charge sound then keep running while the
    /// cooldown (which starts on the press) counts down normally, so the skill only looks
    /// "stuck". No real charge lasts this long, so interrupt it.
    /// <para>
    /// <b>Sampled, not measured.</b> <c>Hero.isChargingSkill()</c> walks the weapon list and the
    /// whole skill manager, so calling it twice per frame was one of the more expensive things in
    /// the per-frame pass — for a watchdog whose whole job is to notice a charge that has lasted six
    /// seconds. It is read every <see cref="ChargeSampleSeconds"/> instead and the elapsed time is
    /// added only while the sample says "charging", which is the same answer within one sample.
    /// </para>
    /// </summary>
    private static void WatchStuckSkills(
        Hero hero,
        double dt,
        ref double chargeTimer,
        ref double sampleTimer,
        string who)
    {
        sampleTimer += dt;
        if (sampleTimer < ChargeSampleSeconds)
        {
            return;
        }

        var elapsed = sampleTimer;
        sampleTimer = 0;

        bool charging;
        try
        {
            charging = hero.isChargingSkill();
        }
        catch
        {
            return;
        }

        if (!charging)
        {
            chargeTimer = 0;
            return;
        }

        chargeTimer += elapsed;
        if (chargeTimer < 6)
        {
            return;
        }
        chargeTimer = 0;

        try
        {
            hero.interruptHeroSkills();
            Log($"[LocalCoop] {who} was stuck charging a skill for 6s, interrupted it");
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not interrupt the stuck skill of {Who}: {Message}", who, ex.Message);
        }
    }

    /// <summary>How often the stuck-charge watchdog reads <c>isChargingSkill()</c>.</summary>
    private const double ChargeSampleSeconds = 0.2;

    /// <summary>
    /// While the backpack is shared, an item used by either player goes on cooldown for both. The game
    /// keeps one timer per hero (<c>OldSkill.coolDownF</c>), so converge every slot onto the larger
    /// remaining time: whoever just used the skill has the larger value. Skills with per-use charges
    /// (<c>DoubleUse</c>) build their own timers and are left alone.
    /// <para>
    /// Converging is only correct while the gear is one shared item. With independent equipment each
    /// hero holds its own copy and its own timer, so the pass returns immediately — merging those would
    /// hand player one's cooldown to player two (and the other way round), and it fought the HUD: see
    /// <see cref="RestoreSharedSkillHud"/>.
    /// </para>
    /// </summary>
    private static void SyncSkillCooldowns(Hero hero1, Hero hero2)
    {
        if (!SharedEquipment)
        {
            return;
        }

        var inventory = hero1.inventory;
        var manager1 = hero1.activeSkillsManager;
        var manager2 = hero2.activeSkillsManager;
        if (inventory == null || manager1 == null || manager2 == null)
        {
            return;
        }

        var slots = inventory.nbActives;
        for (var i = 0; i < slots && i < 8; i++)
        {
            var item = inventory.getActiveOn(i);
            if (item == null)
            {
                continue;
            }

            var active1 = manager1.getActiveSkill(item);
            var active2 = manager2.getActiveSkill(item);
            var skill1 = active1?.skill;
            var skill2 = active2?.skill;
            if (skill1 == null || skill2 == null)
            {
                continue;
            }

            // Skills with several charges build their own per-use timers; leave them alone.
            if ((active1?.hasMultipleUses ?? false) || (active2?.hasMultipleUses ?? false))
            {
                continue;
            }

            var shared = System.Math.Max(skill1.coolDownF, skill2.coolDownF);
            if (skill1.coolDownF != shared)
            {
                skill1.coolDownF = shared;
            }
            if (skill2.coolDownF != shared)
            {
                skill2.coolDownF = shared;
            }
        }
    }

    /// <summary>
    /// While the backpack is shared, the flask charges are one pool. They are not on the inventory —
    /// the <c>Heal</c> main skill owns them per hero — so converge them the same way the life pool is
    /// converged: whoever spent a charge leads, and a charge picked up by either hero raises both.
    /// <para>
    /// Convergence is by difference against the last known shared count rather than a copy of player
    /// one's value, because each hero's own skill is what spends and refills it: a plain copy would
    /// silently undo a drink taken by player two on the very next frame.
    /// </para>
    /// </summary>
    private static void SyncSharedFlask(Hero hero1, Hero hero2)
    {
        if (!SharedEquipment)
        {
            return;
        }

        var (cur1, max1) = ReadFlaskCharges(hero1);
        var (cur2, _) = ReadFlaskCharges(hero2);
        if (max1 <= 0)
        {
            // Player one has no readable flask skill, so there is no pool to converge onto.
            return;
        }

        int shared;
        if (_sharedFlask < 0)
        {
            shared = System.Math.Min(cur1, cur2);
        }
        else if (cur1 < _sharedFlask)
        {
            shared = cur1;
        }
        else if (cur2 < _sharedFlask)
        {
            shared = cur2;
        }
        else
        {
            // Nothing was spent: the higher value is a charge one of them picked up.
            shared = System.Math.Max(cur1, cur2);
        }

        shared = System.Math.Clamp(shared, 0, max1);
        _sharedFlask = shared;

        if (cur1 != shared)
        {
            SetFlaskCharges(hero1, shared);
        }
        if (cur2 != shared)
        {
            SetFlaskCharges(hero2, shared);
        }

        // The game only rewrites the HUD counter from a heal event, so a charge moved by this
        // convergence would leave the bar one drink behind. Push the shared count back through the
        // game's own setter (which the HUD gate lets through in shared mode, the value being shared).
        if (cur1 != shared || cur2 != shared)
        {
            try
            {
                Game.Class.ME?.hud?.setHealings(shared, max1);
            }
            catch (Exception ex)
            {
                LogVerbose($"[LocalCoop] could not repaint the shared flask counter: {ex.Message}");
            }
        }
    }

    /// <summary>Writes one hero's flask charges through its own <c>Heal</c> skill.</summary>
    private static void SetFlaskCharges(Hero hero, int value)
    {
        try
        {
            var skills = hero.mainSkillsManager?.skills;
            if (skills == null)
            {
                return;
            }

            for (var i = 0; i < skills.length && i < 16; i++)
            {
                if (skills.getDyn(i) is dc.tool.mainSkills.Heal heal)
                {
                    heal.set_healings(value);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not converge the shared flask: {Message}", ex.Message);
        }
    }

    /// <summary>The last known shared flask count, or -1 before the first frame of the pool.</summary>
    private static int _sharedFlask = -1;

    /// <summary>
    /// While the backpack is shared, the scroll tiers are one set. They are derived from the shared
    /// inventory, so <c>computeTiers()</c> would give both heroes the same answer — but the game only
    /// recomputes it for the hero that picked the scroll up, so the other hero keeps a stale number on
    /// its own fields (which the panel and the damage maths read). Recompute for player two.
    /// </summary>
    private static void SyncSharedTiers(Hero hero1, Hero hero2)
    {
        if (!SharedEquipment)
        {
            return;
        }

        if (hero2.brutalityTier == hero1.brutalityTier
            && hero2.tacticTier == hero1.tacticTier
            && hero2.survivalTier == hero1.survivalTier)
        {
            return;
        }

        try
        {
            hero2.computeTiers();
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not converge the shared scroll tiers: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// A skill entity spawned by hero two can end up registered on a level that is no longer
    /// current (hero two's level pointer is repaired a moment later), which leaves it out of
    /// the update loop: it stays on screen, frozen, with its looping sound still playing.
    /// The game's own cleanup destroys exactly those entities, so drop them here as well.
    /// </summary>
    private static void CleanOrphanSkillEntities(Hero hero2, double dt)
    {
        _entityTimer += dt;
        if (_entityTimer < 0.5)
        {
            return;
        }
        _entityTimer = 0;

        try
        {
            var current = Game.Class.ME?.curLevel;
            var manager = hero2.activeSkillsManager;
            var inventory = hero2.inventory;
            if (current == null || manager == null || inventory == null)
            {
                return;
            }

            var slots = inventory.nbActives;
            for (var i = 0; i < slots && i < 8; i++)
            {
                var item = inventory.getActiveOn(i);
                if (item == null)
                {
                    continue;
                }

                var entries = manager.getActiveSkill(item)?.entities;
                if (entries == null)
                {
                    continue;
                }

                for (var e = 0; e < entries.length; e++)
                {
                    if (entries.getDyn(e) is not Active active || active.destroyed)
                    {
                        continue;
                    }
                    if (ReferenceEquals(active._level, current))
                    {
                        continue;
                    }

                    active.destroy();
                    LogVerbose($"[LocalCoop] removed an orphaned {active.GetType().Name} of hero two " +
                        $"(slot {i}, not on the current level)");
                }
            }
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not clean hero two's skill entities: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Re-reads hero two's active skill slots from the inventory it holds without leaving the
    /// global HUD pointing at hero two. Mirrors the part of
    /// <c>HeroActiveSkillsManager.onEquippedItemsUpdated</c> that matters for hero two: drop its
    /// live skill entities and re-arm each slot from its item.
    /// <para>
    /// The HUD is why this ends with <see cref="RestoreSharedSkillHud"/> — see that method.
    /// </para>
    /// </summary>
    private static void RefreshSecondHeroSkills(Hero hero2)
    {
        try
        {
            hero2.computeTiers();

            var manager = hero2.activeSkillsManager;
            var inventory = hero2.inventory;
            if (manager == null || inventory == null)
            {
                return;
            }

            manager.killEntities();

            var slots = inventory.nbActives;
            for (var i = 0; i < slots && i < 8; i++)
            {
                var item = inventory.getActiveOn(i);
                if (item == null)
                {
                    continue;
                }
                manager.getActiveSkill(item)?.useActiveItem(item);
            }
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not refresh hero two's skills: {Message}", ex.Message);
        }

        RestoreSharedSkillHud();
    }

    /// <summary>
    /// Re-points the global HUD's skill slots at player one.
    /// <para>
    /// There is exactly one HUD (the game has one hero), and <c>HUD.skillPowers</c> holds
    /// <c>dc.ui.hud.Skill</c> objects whose <c>getAmmo</c> / <c>hasAmmo</c> callbacks are bound —
    /// through <c>HeroActiveSkillsManager.hudGetSkillPower</c> — to whichever hero's
    /// <c>HeroActiveSkill</c> ran last. Those callbacks are what the HUD reads to draw the cooldown
    /// overlay and the ammo (<c>getAmmo</c> is <c>usesLeft + getNextUseCooldownRatio()</c>).
    /// </para>
    /// <para>
    /// So anything that rebuilds hero two's skills hands player one's cooldown display to player
    /// two: the HUD then shows player two's (fresh, empty) cooldown, which reads as "P1 used an
    /// item and no cooldown appeared" — and the other way round, player two using an item paints
    /// <b>its</b> cooldown onto player one's HUD. Running player one's own
    /// <c>onEquippedItemsUpdated</c> puts the binding back; it is the same call the game makes for
    /// the hero that changed gear, and it only re-binds closures.
    /// </para>
    /// <para>
    /// It hangs off <see cref="RefreshSecondHeroSkills"/> rather than the equipment hook, because
    /// that is the one path every mode goes through — including independent equipment, where the
    /// hook returns early.
    /// </para>
    /// </summary>
    private static void RestoreSharedSkillHud()
    {
        if (!Enabled || _hero1 == null || _hero1.destroyed)
        {
            return;
        }

        try
        {
            _mirroringEquip = true;
            // duringItemTransform = true is what makes this safe: the plain
            // `onEquippedItemsUpdated(false, false)` path starts with killEntities(), which destroys
            // player one's live skill entities — and the HUD's getAmmo (usesLeft +
            // getNextUseCooldownRatio) is read off those very entities, so it wiped the cooldown the
            // item had just started. With duringItemTransform set, the method re-binds the closures
            // (which is all this needs) and never kills anything.
            _hero1.activeSkillsManager?.onEquippedItemsUpdated(false, true);
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not restore P1's HUD skill slots: {Message}", ex.Message);
        }
        finally
        {
            _mirroringEquip = false;
        }
    }

    private static int _mainSkillRebindLogs;

    /// <summary>
    /// Makes every main skill (the flask among them) act on the hero that owns it.
    /// <para>
    /// <c>HeroMainSkill</c> keeps the hero it operates on in its <c>hero</c> field, and everything
    /// it does — spending a flask charge, applying the heal, asking whether it may be used — goes
    /// through that field. The skills are built once, while the global <c>Game.ME.hero</c> is
    /// player one, so player two's skills captured player one: pressing the flask on player two's
    /// pad spent <b>player one's</b> charge and healed <b>player one</b>, which is why player two's
    /// life never moved and player one's flask counter changed. It is the same shape of bug as
    /// <c>HeroHead.init()</c> capturing the global hero.
    /// </para>
    /// <para>
    /// Fixed by re-pointing, not by duplicating: the same one-field repair used for the head. Cheap
    /// enough to run every frame (a handful of references per hero) and it also re-fixes the field
    /// after a run start, a reload or an independent-equipment rebuild rebuilds the skills.
    /// </para>
    /// </summary>
    private static void BindMainSkills(double dt)
    {
        // A repair, not a driver: the field only goes wrong when the game (re)builds a manager —
        // spawn, level change, equipment rebuild — so re-checking both heroes' whole skill lists
        // sixty times a second was ~64 interop calls per frame for an answer that changes a handful
        // of times per run. Sampled instead; the worst case is one flask press inside a
        // BindMainSkillsSeconds window landing on the partner, which is not reachable in practice
        // (the rebuild happens while the pause/equipment screen is open).
        _bindMainSkillsTimer += dt;
        if (_bindMainSkillsTimer < BindMainSkillsSeconds)
        {
            return;
        }

        _bindMainSkillsTimer = 0;

        BindMainSkillsOf(_hero1);
        BindMainSkillsOf(_hero2);
    }

    /// <summary>How often the main-skill owner binding is re-checked.</summary>
    private const double BindMainSkillsSeconds = 0.1;

    /// <summary>Time since the last main-skill binding pass.</summary>
    private static double _bindMainSkillsTimer;

    private static void BindMainSkillsOf(Hero? hero)
    {
        if (hero == null || hero.destroyed)
        {
            return;
        }

        try
        {
            var skills = hero.mainSkillsManager?.skills;
            if (skills == null)
            {
                return;
            }

            for (var i = 0; i < skills.length && i < 32; i++)
            {
                if (skills.getDyn(i) is not dc.tool.HeroMainSkill skill)
                {
                    continue;
                }
                if (ReferenceEquals(skill.hero, hero))
                {
                    continue;
                }

                skill.hero = hero;

                // A handful of lines at most: this only fires when the game (re)built a manager
                // with the wrong hero, not once per frame.
                if (_mainSkillRebindLogs < 12)
                {
                    _mainSkillRebindLogs++;
                    Log($"[LocalCoop] main skill '{skill.GetType().Name}' re-bound to " +
                        $"{(ReferenceEquals(hero, _hero2) ? "P2" : "P1")}");
                }
            }
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not bind a hero's main skills: {Message}", ex.Message);
        }
    }
}
