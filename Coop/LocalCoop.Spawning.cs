using dc;
using dc.en;
using dc.en.hero;
using dc.hl.types;
using dc.pr;

namespace DeadCellsLocalMultiplayerMod;

/// <summary>
/// Part of <see cref="LocalCoop"/>: second hero spawn, adoption, placement and repair.
/// </summary>
internal static partial class LocalCoop
{
    // Hero two's own life state, remembered every frame. On a level transition hero two is
    // sometimes destroyed by the old level's teardown and re-created by SpawnSecondHero — and a
    // fresh Hero inherits the *global* hero's (player one's) life, which is why P2 used to come
    // back at P1's HP (or at 1 when P1 had just been revived at 1). Restoring these after the
    // spawn keeps P2's own health.
    private static int _savedHero2Life;
    private static int _savedHero2MaxLife;
    private static int _savedHero2BonusLife;

    /// <summary>
    /// Player two's flask charges, remembered every frame like its life. They are NOT in the bag —
    /// the <c>Heal</c> main skill owns them — and a re-created hero's <c>HealBlackBoard</c> seeds
    /// itself from <c>game.data.healings</c>, the run-global value the game syncs from the global
    /// hero. So without this, a level-transition re-spawn handed player two player one's flask
    /// count (and the level change is exactly when the board is rebuilt).
    /// </summary>
    private static int _savedHero2Flask;
    private static bool _hasSavedHero2State;

    private static Hero OnInitHero(
        Hook_Game.orig_initHero orig,
        Game self,
        Level lvl,
        int cx,
        int cy,
        UsableBody from,
        bool fromDeadBody,
        Level oldLevel)
    {
        var hero = orig(self, lvl, cx, cy, from, fromDeadBody, oldLevel);

        if (!Enabled)
        {
            return hero;
        }

        try
        {
            PrepareHeroes(self, hero, lvl, oldLevel);

            // Cell-door / level-transition fast path. Game.initHero is called *from inside*
            // Level.update (the door entity triggers it), so the level's own update keeps running in
            // the very same call and can trip on a null room if hero two is still parked on the old,
            // being-torn-down level. A deferred move (TrySyncHeroes, next frame) is far too late for
            // that window, which is the cell-door "Null access .rType" crash. Relocate hero two
            // synchronously, here in the hook, mirroring what the game just did for player one.
            // The first spawn is excluded from this fast path on purpose: at run start the level is
            // still streaming and placing hero two immediately dropped it on cell -1,-1, so it keeps
            // waiting for player one to settle (the deferred SpawnSecondHero path).
            // (There is deliberately no entry-follow glue any more: hero two lands at the door and
            // stays where its player walks it. The manual teleport is the only regrouping — see
            // Coop/LocalCoop.Teleport.cs.)
            if (_hero2 != null && !_hero2.destroyed
                && hero != null && !hero.destroyed
                && hero.awake && hero._level != null && hero.cx >= 0 && hero.cy >= 0)
            {
                RunGuarded("move the second hero (synchronous)", () => MoveSecondHero(hero, oldLevel));
                // Fast path succeeded: drop the deferred move so it does not fire a second time.
                // If RunGuarded swallowed a failure, _pendingSpawn stays set and TrySyncHeroes
                // retries next frame.
                _pendingSpawn = false;
            }

            // Independent health: revive any hero that went down on the previous level, now that
            // both heroes are on the new level. (No-op at run start and in shared-health mode.)
            ReviveDownHeroes();
        }
        catch (Exception ex)
        {
            // A failure here must still leave the vanilla hero playable.
            LogWarn("[LocalCoop] could not prepare the second hero: {Message}", ex.Message);
        }

        // Game.initHero always returns a live hero in Haxe, so assert non-null for the compiler.
        return hero!;
    }

    /// <summary>
    /// Marks that the heroes need to be (re)placed. The work itself is deferred to the next
    /// frame: the coordinates passed to initHero are not necessarily where player one ends
    /// up, so the second hero is placed relative to player one once player one is awake and
    /// standing on a real cell.
    /// </summary>
    private static void PrepareHeroes(Game game, Hero hero1, Level lvl, Level? oldLevel)
    {
        _game = game;
        _hero1 = hero1;
        _pendingLevel = lvl;
        _pendingOldLevel = oldLevel;
        _pendingSpawn = true;
        _pendingFrames = 0;

        // New level: rebuild the P2 bar from scratch. Its old panel was parented to the previous
        // level's HUD objects, and a transient error during the transition must not leave the bar
        // permanently disabled (the _heroHudBroken latch).
        ResetHeroPanels();

        // The level places hero two 0.12 cells beside hero one — closer than the two radii — so the
        // walk-push (LocalCoop.Push.cs) must not read that placement as a shove and must drop any bump
        // it set before the level changed.
        ResetWalkPush(30);
    }

    private static void TrySyncHeroes()
    {
        if (!_pendingSpawn)
        {
            return;
        }

        var hero1 = _hero1;
        var game = _game;
        if (hero1 == null || game == null || hero1.destroyed)
        {
            return;
        }

        // Player one has to be standing on a real cell for a while before hero two is created:
        // level streaming still moves player one around right after initHero, and spawning
        // during that window is what left hero two off-level (case -1,-1).
        var ready = hero1.awake && hero1._level != null && hero1.cx >= 0 && hero1.cy >= 0;
        if (ready)
        {
            _readyFrames++;
        }
        else
        {
            _readyFrames = 0;
        }

        _pendingFrames++;
        if (_readyFrames < 90)
        {
            if (_pendingFrames < 900)
            {
                return;
            }
            // Never place a hero on an invalid cell: that is what parked it at -1,-1 before. But
            // do not give up for good — loading a save streams the level in much later than a
            // fresh run does, so player one can still be unsettled at 900 frames and settle fine
            // a second later. Keep waiting (with a warning) and let it recover on its own.
            if (_pendingFrames % 900 == 0)
            {
                LogVerbose($"[LocalCoop] player one still not settled after {_pendingFrames} frames " +
                    $"(awake={hero1.awake} level={(hero1._level == null ? "none" : "ok")} " +
                    $"case={hero1.cx},{hero1.cy}), still waiting");
            }

            return;
        }

        LogVerbose($"[LocalCoop] player one ready at case {hero1.cx},{hero1.cy}");

        _pendingSpawn = false;

        // Each step gets its own guard. They used to share one try block, so anything that threw
        // along the way — most easily ShareProgression() calling applySkin() on a save-loaded
        // hero — silently skipped the controller setup below, and the run then continued with
        // both heroes on the global controller (input does nothing useful) and no camera work,
        // which is exactly what "continue" from a save looked like.
        if (_hero2 == null || _hero2.destroyed)
        {
            RunGuarded("spawn the second hero", () => SpawnSecondHero(game, hero1, _pendingOldLevel));
            // (No entry-follow glue any more: at the first spawn of a run hero two lands at the
            // door and its player walks it in; the teleport key is the only regrouping.)
        }
        else
        {
            RunGuarded("move the second hero", () => MoveSecondHero(hero1, _pendingOldLevel));
        }

        RunGuarded("share progression", () => ShareProgression(game, hero1));
        ReviveDownHeroes();
        EnsureControllers();
    }

    /// <summary>
    /// Adopts the heroes the level already holds, instead of assuming the mod created them.
    ///
    /// A run restored from a save never goes through <c>Game.initHero()</c>: the save is
    /// deserialised straight into the game (<c>Game.unserialize()</c> assigns <c>this.hero</c>),
    /// and a save written during a co-op session carries the second hero along as an ordinary
    /// level entity. <c>OnInitHero</c> therefore never runs, the mod kept no references at all,
    /// and "continue" came up with both heroes simply standing there: neither pad isolated, no
    /// shared camera, and the mod's own logs silent. Adopt whatever the level already has, so a
    /// continued run is set up exactly like a fresh one.
    /// </summary>
    private static void AdoptExistingHeroes(Game game)
    {
        // A downed hero (independent health) sits at 0 HP while still being alive, so 0 HP no longer
        // means "this run has no usable player one" — only a destroyed hero or one with no level does.
        // (Previously a downed hero was clamped to 1 HP, which hid this distinction.)
        var hero1 = game.hero;
        if (hero1 == null || hero1.destroyed || hero1._level == null)
        {
            return;
        }

        if (!ReferenceEquals(_hero1, hero1))
        {
            _game = game;
            _hero1 = hero1;
            _sharedLife = -1;
            _sharedFlask = -1;

            // Only a run the mod did not spawn gets here, so this is a continued run.
            Log($"[LocalCoop] adopted the running hero as player one at case {hero1.cx},{hero1.cy}");

            // The mod did not create this run, so it never scheduled the second hero either.
            // Point the normal placement at it, the same way OnInitHero would have.
            _pendingLevel = hero1._level;
            _pendingOldLevel = null;
            _pendingSpawn = true;
            _pendingFrames = 0;
            _readyFrames = 0;

            // A continued run: the save only knows a downed hero at DownedLife, so the marker in
            // the settings file is what keeps it down (only the forge revives). The check inside
            // rejects the marker when the hero is healthy, so a new run can never inherit it.
            TryRestorePersistedDown(hero1, _persistedHero1Down, "P1");
        }

        if (_hero2 != null && !_hero2.destroyed)
        {
            return;
        }

        // The saved level can already contain player two (it was saved as an ordinary entity).
        // Adopting it beats spawning a third hero next to it, and it keeps the saved position.
        // Checked every frame, because the level's entities are still being restored during the
        // first frames after the load.
        var other = FindOtherHero(hero1._level, hero1);
        if (other != null)
        {
            _hero2 = other;
            _pendingSpawn = false;
            BindHeroHead(other);
            Log($"[LocalCoop] adopted the second hero already in the level at case {other.cx},{other.cy}");
            RunGuarded("share progression", () => ShareProgression(game, hero1));

            // The adopted hero came straight out of the save, which stores a single hero's gear. With
            // independent equipment it must not stay on player one's inventory — without this, a
            // continued run came back with both backpacks shared whatever the setting said, because
            // nothing re-applied the mode to a hero the mod did not spawn.
            RunGuarded("apply the equipment mode to the adopted hero", ApplyEquipmentMode);
            TryRestorePersistedDown(other, _persistedHero2Down, "P2");
        }

        EnsureControllers();
    }

    /// <summary>The other player hero on a level, i.e. player two, if there is one.</summary>
    private static Hero? FindOtherHero(Level level, Hero hero1)
    {
        try
        {
            var raw = level.entitiesByClass?.get(HeroClid);
            if (raw is not ArrayObj bucket)
            {
                return null;
            }

            for (var i = 0; i < bucket.length; i++)
            {
                // Only a hero of player one's own class counts: the level also holds Hero
                // subclasses that are not a player (dc.cine.DoubleHero, WreckingBallHero), and
                // adopting one of those as player two would hand the mod an entity the game
                // owns and drives itself.
                if (bucket.getDyn(i) is Hero hero
                    && hero.GetType() == hero1.GetType()
                    && !hero.destroyed
                    && !ReferenceEquals(hero, hero1)
                    && ReferenceEquals(hero._level, level))
                {
                    return hero;
                }
            }
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not look for a second hero: {Message}", ex.Message);
        }

        return null;
    }

    private static void SpawnSecondHero(Game game, Hero hero1, Level? oldLevel)
    {
        Hero? hero2 = null;
        try
        {
            hero2 = (Hero?)Activator.CreateInstance(hero1.GetType(), game);
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not build a {Type}, falling back to Beheaded: {Message}",
                hero1.GetType().Name, ex.Message);
        }

        hero2 ??= new Beheaded(game);

        // Build player two with its own skin in a single initGfx: point the global skin at player
        // two's before wakeup() so wakeup's initGfx uses it, then restore. Without this, wakeup
        // builds with player one's skin and ShareProgression's ApplyPlayer2Skin immediately tears
        // the graphics down and rebuilds them (a second initGfx) — the visible start-of-run freeze.
        var desiredSkin = ResolvePlayer2Skin(hero1);
        var user = hero1._level?.game?.user;
        var savedSkin = user?.heroSkin;
        if (desiredSkin != null && user != null)
        {
            user.heroSkin = desiredSkin;
        }

        // wakeup() runs set_level() before init(), which is the order the game itself uses.
        hero2.wakeup(hero1._level, hero1.cx, hero1.cy);

        // A fresh Hero inherits the global hero's (P1's) life. Put hero two's own life back so a
        // level-transition re-spawn does not silently copy P1's HP onto P2 (or clamp a downed P2
        // back to 1). maxLife/bonusLife are restored first so life is clamped against the right cap.
        if (_hasSavedHero2State)
        {
            if (_savedHero2MaxLife > 0)
            {
                hero2.maxLife = _savedHero2MaxLife;
            }
            hero2.bonusLife = _savedHero2BonusLife;
            hero2.life = _savedHero2Life;
        }

        // Same reasoning for the flask: the fresh hero's Heal board seeded itself from
        // `game.data.healings` (the run-global count, which the game keeps on the global hero), so
        // player two would come back holding player one's charges. Give it its own back.
        if (_hasSavedHero2State && _savedHero2Flask >= 0)
        {
            SetFlaskCharges(hero2, _savedHero2Flask);
        }

        if (user != null && savedSkin != null)
        {
            user.heroSkin = savedSkin;
        }
        if (desiredSkin != null)
        {
            // wakeup() already built hero two with this skin, so mark it applied and let the
            // per-frame ApplyPlayer2Skin skip its own (second) applySkin().
            _appliedSkin = desiredSkin;
            _appliedSkinHero = hero2;
        }

        hero2.minimapTracking();
        hero2.sprAlpha = 1.0;
        hero2.refillAllAmmo();
        hero2.onLevelChanged(oldLevel);
        PlaceNextTo(hero2, hero1);

        // wakeup() ran initGfx(), which ran HeroHead.init() — that binds the head to the global
        // hero (player one). Point hero two's head at hero two before anything reads it.
        BindHeroHead(hero2);

        _hero2 = hero2;
        // This hero was built from scratch, so the bag its constructor put in it is a clone of
        // `game.data.inventory` (= player one's gear), not player two's own. GiveOwnInventory must
        // not adopt it — see RestoreOwnInventory.
        _hero2FreshlySpawned = true;
        Log($"[LocalCoop] Second hero spawned at case {hero2.cx},{hero2.cy} ({hero2.GetType().Name}), " +
            $"same level as P1: {hero2._level == hero1._level}");

        // There is deliberately no entry-follow glue armed here any more: hero two lands at the
        // first level's door and player two walks it in, exactly like every later level. The
        // manual teleport (Coop/LocalCoop.Teleport.cs) is the only regrouping.
    }

    /// <summary>
    /// Puts hero two on player one's cell with a small in-cell offset so the two heroes
    /// neither land inside geometry nor sit perfectly on top of each other.
    /// </summary>
    private static void PlaceNextTo(Hero hero2, Hero hero1)
    {
        var xr = hero1.xr + 0.3;
        if (xr > 0.95)
        {
            xr = hero1.xr - 0.3;
        }
        hero2.setPosCase(hero1.cx, hero1.cy, xr, hero1.yr);
    }

    private static void MoveSecondHero(Hero hero1, Level? oldLevel)
    {
        var hero2 = _hero2;
        if (hero2 == null)
        {
            return;
        }

        RelocateSecondHero(hero2, hero1, oldLevel);
        Log($"[LocalCoop] Second hero moved to case {hero2.cx},{hero2.cy}");
    }

    /// <summary>
    /// Moves hero two to player one's level the way <c>Game.initHero</c> relocates the global hero.
    ///
    /// Re-pointing the level alone is not enough. Teams are per level, so a hero that does not change
    /// team stays in the old level's member list; <c>entitiesLinked</c> (pets, a thrown head) must
    /// come along or it keeps updating on a level that is being torn down; and an in-progress main
    /// skill has to be cancelled exactly as the game does for player one, or its entities are left
    /// behind on the old level. Those stale references are what leaves <c>Level.update</c> tripping
    /// over a null entity after a level change (the "Null access .rType" crash when entering a cell
    /// door or a hidden level).
    /// </summary>
    private static void RelocateSecondHero(Hero hero2, Hero hero1, Level? oldLevel)
    {
        var level = hero1._level;
        if (level == null)
        {
            return;
        }

        if (Verbose)
        {
            var linkedLen = 0;
            try { linkedLen = (int)hero2.entitiesLinked.length; } catch { linkedLen = -1; }
            Log($"[LocalCoop] RelocateSecondHero: P1 case={hero1.cx},{hero1.cy} onLevel={(hero1._level != null)} | " +
                $"P2 case={hero2.cx},{hero2.cy} sameLevelAsP1={ReferenceEquals(hero2._level, hero1._level)} linked={linkedLen}");
        }

        // Mirror Game.initHero's awake branch exactly. The order matters: the game relocates the
        // hero, cancels any in-progress main skill, moves the linked entities (pets, the detached
        // head), and only then calls onLevelChanged — at the very end, once all that state is
        // consistent. Calling onLevelChanged too early (before cancelMainSkills / MoveLinkedEntities)
        // is what left a half-initialised hero for Level.update to trip over.
        if (hero2._level != level)
        {
            hero2.set_level(level);
        }

        hero2.set_team(level.teamHero);
        hero2.initGfx();
        hero2.visible = true;
        PlaceNextTo(hero2, hero1);

        // CRITICAL FIX for the cell-door (sub-level) "Null access .rType" crash.
        // set_level() + setPosCase() move hero two onto the new level but leave its cached room
        // index (lastRoomId) pointing at the level it just left. On a tiny sub-level (2-4 rooms)
        // that stale index is >= the new room count, so the next Entity.fixedUpdate room transition
        // hits `if (lastRoomId >= rooms.length) onLeaveRoom(null)` and Hero.onLeaveRoom dereferences
        // null.rType. Reset to -1 ("no current room"); fixedUpdate then re-derives the room from the
        // hero's new cell via getRoomAt() and calls onEnterRoom() correctly. (Normal levels have
        // enough rooms that a stale index stayed in range, which is why only sub-levels crashed.)
        hero2.lastRoomId = -1;

        try
        {
            hero2.lastStablePt = new dc.tool.CPoint(hero1.cx, hero1.cy);
            hero2.cancelVelocities();
            hero2.cancelMainSkills(null);
            MoveLinkedEntities(hero2, level, hero1);
            hero2.minimapTracking();
            hero2.sprAlpha = 1.0;
            hero2.refillAllAmmo();
            hero2.onLevelChanged(oldLevel);
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] hero two relocation extras failed: {Message}", ex.Message);
        }

        // The relocate rebuilt hero two's graphics (initGfx above reads the global skin), so force
        // ApplyPlayer2Skin to re-apply hero two's own skin instead of skipping it as already-applied.
        _appliedSkin = null;
        _appliedSkinHero = null;
    }

    /// <summary>

    /// <summary>
    /// Brings hero two's linked entities (pets, the detached head) to its level, the same way
    /// <c>Game.initHero</c> moves <c>hero.entitiesLinked</c> for player one.
    /// </summary>
    private static void MoveLinkedEntities(Hero hero2, Level level, Hero hero1)
    {
        var linked = hero2.entitiesLinked;
        if (linked == null)
        {
            return;
        }

        for (var i = 0; i < linked.length; i++)
        {
            if (linked.getDyn(i) is not Entity linkedEntity || linkedEntity.destroyed)
            {
                continue;
            }

            if (linkedEntity._level != level)
            {
                linkedEntity.set_level(level);
            }
            // Same stale-index crash as the hero above, for pets / the detached head, etc.
            linkedEntity.lastRoomId = -1;
            linkedEntity.initGfx();
            linkedEntity.setPosCase(hero1.cx, hero1.cy, null, null);
        }
    }

    private static void ShareProgression(Game game, Hero hero1)
    {
        var hero2 = _hero2;
        if (hero2 == null)
        {
            return;
        }

        // Sharing the instances is what makes inventory, attributes and unlocks identical.
        // With independent equipment player two gets its own copy instead — cloned from player
        // one's so it starts with a copy of the starting gear rather than bare-handed.
        if (SharedEquipment)
        {
            hero2.inventory = hero1.inventory;
            hero2.blueprints = hero1.blueprints;
        }
        else
        {
            hero2.blueprints = hero1.blueprints;
            GiveOwnInventory(hero1, hero2);
        }

        // The bag question is settled for this hero (shared mode never asks it), so the
        // "just constructed" mark must not leak into a later, unrelated equipment decision.
        _hero2FreshlySpawned = false;

        // Player two wears its own skin (defaulting to one different from player one's), applied
        // by ApplyPlayer2Skin — which is idempotent, so this is cheap when nothing changed. Do NOT
        // call hero2.applySkin() here every frame: Hero.applySkin() does disposeGfx()+initGfx() on
        // every call, so a per-frame call tears down and rebuilds hero two's whole graphics every
        // frame (the start-of-run freeze), and applying player one's skin here fights the own-skin
        // ApplyPlayer2Skin just applied.
        ApplyPlayer2Skin(hero2);
    }

    /// <summary>
    /// The second hero can be pulled out of its level again after spawning (level streaming,
    /// sub-level activation), which parks it on the invalid -1,-1 cell and makes it invisible.
    /// Put it back next to player one whenever that happens.
    /// </summary>
    private static void RepairSecondHero(Hero hero1, Hero hero2, double dt)
    {
        _repairTimer += dt;
        if (_repairTimer < 0.5)
        {
            return;
        }

        if (hero1._level == null || hero1.cx < 0 || hero1.cy < 0)
        {
            return;
        }
        if (hero2._level == hero1._level && hero2.cx >= 0 && hero2.cy >= 0)
        {
            return;
        }

        _repairTimer = 0;
        try
        {
            RelocateSecondHero(hero2, hero1, hero1._level);
            // initGfx() rebuilt the graphics from the global skin, so re-apply player two's own.
            ApplyPlayer2Skin(hero2);
            LogVerbose($"[LocalCoop] hero two was off level, placed back at case {hero2.cx},{hero2.cy}");
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not place hero two back: {Message}", ex.Message);
        }

        // (No entry-follow re-arm: the glue is gone. A hero two parked at a level's door is now a
        // legitimate position — player two walks it in or uses the teleport key.)
    }

    /// <summary>
    /// Destroys the second hero and drops every run reference. Without this the orphaned hero
    /// keeps updating after the run ends and crashes the game on a null sprite.
    /// <para>
    /// <paramref name="keepHero2State"/> says the run continues without this hero (a level transition
    /// destroyed it, player one is alive): its own bag and life are then kept so the hero that
    /// replaces it gets them back — see <c>RestoreOwnInventory</c>.
    /// </para>
    /// </summary>
    private static void RemoveSecondHero(string reason, bool keepHero2State = false)
    {
        var hero2 = _hero2;
        if (hero2 == null || hero2.destroyed)
        {
            ResetRunState(keepHero2State);
            return;
        }

        try
        {
            hero2.destroy();
            Log($"[LocalCoop] removed the second hero ({reason})");
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not destroy the second hero: {Message}", ex.Message);
        }

        ResetRunState(keepHero2State);
    }

    // -------------------------------------------------------------------------
    // Diagnostic: pin down the cell-door (sub-level) "Null access .rType" crash.
    //
    // Level.update crashes dereferencing a null Room. Static analysis shows update() does not
    // iterate map.rooms per frame, so the null is a *single* room (current room / getRoomAt(hero))
    // that is still null during a sub-level transition. This hook wraps Level.update, and on a throw
    // captures which level it was, whether its room array holds a null, and where both heroes sit.
    // It is only installed when DEADCELLSLOCALMULTIPLAYERMOD_LOG is on, and only logs on a crash, so normal play
    // is untouched.
    // -------------------------------------------------------------------------
    private delegate void LevelOrigUpdate(dc.pr.Level self);

    private static void InstallLevelUpdateHook()
    {
        try
        {
            ModCore.Modules.HashlinkHooks.Instance.CreateHook(
                "pr.Level", "update",
                new Action<LevelOrigUpdate, dc.pr.Level>(OnLevelUpdate),
                true);
            LogVerbose("[LocalCoop] installed Level.update diagnostic hook");
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not install Level.update diagnostic hook: {Message}", ex.Message);
        }
    }

    private static void OnLevelUpdate(LevelOrigUpdate orig, dc.pr.Level self)
    {
        // Normal path when not logging: identical behaviour, no overhead.
        if (!Logging)
        {
            orig(self);
            return;
        }

        try
        {
            orig(self);
        }
        catch (Exception ex)
        {
            try
            {
                int roomsLen = -1;
                int roomsNulls = -1;
                string? nullIndices = null;
                var roomReport = new System.Text.StringBuilder();

                var map = self?.map;
                if (map != null)
                {
                    var rooms = map.rooms;
                    if (rooms != null)
                    {
                        roomsLen = rooms.length;
                        var sb = new System.Text.StringBuilder();
                        int seen = 0;
                        for (int i = 0; i < roomsLen && seen < 12; i++)
                        {
                            if (rooms.getDyn(i) == null)
                            {
                                if (seen > 0)
                                {
                                    sb.Append(',');
                                }
                                sb.Append(i);
                                seen++;
                            }
                        }
                        roomsNulls = seen;
                        nullIndices = sb.Length > 0 ? sb.ToString() : "none";

                        // Suspect #1: a Room whose .parent is null -> "room.parent.rType" crash at
                        // level_pseudo.cs:22793 (BossDoor handling in spawnEntitiesFromMarkers, inlined
                        // into Level.update for sub-levels). Report any such room.
                        for (int i = 0; i < roomsLen && i < 16; i++)
                        {
                            var room = rooms.getDyn(i) as dc.level.Room;
                            if (room == null)
                            {
                                continue;
                            }
                            string? rt = room.rType == null ? "null" : room.rType.ToString();
                            bool parentNull = room.parent == null;
                            if (rt == "null" || parentNull)
                            {
                                roomReport.Append($" [{i}]rType={rt} parentNull={parentNull}");
                            }
                        }
                    }
                }

                var p1 = Game.Class.ME?.hero;
                var p2 = _hero2;

                // Suspect #2: getRoomAt(hero.cx, hero.cy) returns null -> a hero parked on a cell
                // with no room, which later derefs .rType. Report what each hero resolves to.
                string g1 = "n/a", g2 = "n/a";
                if (map != null && p1 != null)
                {
                    try
                    {
                        var rr = map.getRoomAt(p1.cx, p1.cy);
                        g1 = rr == null ? "NULL" : $"rType={rr.rType} parentNull={rr.parent == null}";
                    }
                    catch (Exception ge) { g1 = "ERR:" + ge.Message; }
                }
                if (map != null && p2 != null)
                {
                    try
                    {
                        var rr = map.getRoomAt(p2.cx, p2.cy);
                        g2 = rr == null ? "NULL" : $"rType={rr.rType} parentNull={rr.parent == null}";
                    }
                    catch (Exception ge) { g2 = "ERR:" + ge.Message; }
                }

                LogWarn("[LocalCoop][DIAG] Level.update threw | type={Type} sub={Sub} rooms(len={Rooms} nulls={Nulls} @[{NullIdx}])",
                    self?.GetType()?.Name, self?.isSubLevel, roomsLen, roomsNulls, nullIndices);
                LogWarn("[LocalCoop][DIAG] suspicious rooms:{Suspicious}", roomReport.Length > 0 ? roomReport.ToString() : "none");
                LogWarn("[LocalCoop][DIAG] getRoomAt P1={G1} | P2={G2}", g1, g2);
                LogWarn("[LocalCoop][DIAG] P1 case={P1cx},{P1cy} onThis={P1on} | P2 case={P2cx},{P2cy} onThis={P2on} | {Msg}",
                    p1?.cx, p1?.cy, ReferenceEquals(p1?._level, self),
                    p2?.cx, p2?.cy, ReferenceEquals(p2?._level, self),
                    ex.Message);
                LogWarn("[LocalCoop][DIAG] exception: {Ex}", ex.ToString());
            }
            catch (Exception ex2)
            {
                LogWarn("[LocalCoop][DIAG] diagnostic capture failed: {Message}", ex2.Message);
            }

            // Re-throw so the game still surfaces the original fatal error (with its own stack).
            throw;
        }
    }
}
