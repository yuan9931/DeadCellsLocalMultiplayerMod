using dc;
using dc.en;
using dc.pr;

namespace DeadCellsLocalMultiplayerMod;

/// <summary>
/// Part of <see cref="LocalCoop"/>: the "down" state used when health is independent
/// (<see cref="SharedHealth"/> off). A hero whose life hits 0 is not removed and does not end the
/// run while a teammate is still alive — it is frozen and hidden ("down") and stays in the level,
/// and revives at 1 HP on the next level. The run only ends when every hero is down.
/// </summary>
internal static partial class LocalCoop
{
    private static bool _hero1Down;
    private static bool _hero2Down;

    /// <summary>
    /// Where each hero fell, remembered for the surviving partner's teleport key
    /// (<see cref="ToggleTeleport"/> in Coop/LocalCoop.Teleport.cs). The downed hero's LIVE
    /// position stops being "the body's place" the moment it goes down: a downed player one is
    /// moved onto the survivor every frame for the positional-audio listener, and on a level
    /// change either hero relocates with the pair. So the spot must be taken at the down itself,
    /// and it only ever matches the level the hero fell on.
    /// </summary>
    private static TeleportReturn _deathSpot1;
    private static TeleportReturn _deathSpot2;

    /// <summary>Records where this hero fell and drops its own stale return window.</summary>
    private static void RecordDeathSpot(Hero hero)
    {
        var spot = new TeleportReturn
        {
            Has = hero._level != null && hero.cx >= 0 && hero.cy >= 0,
            Level = hero._level,
            Cx = hero.cx,
            Cy = hero.cy,
            Xr = hero.xr,
            Yr = hero.yr,
            RemainingSeconds = 0.0,
        };
        if (ReferenceEquals(hero, _hero1))
        {
            _deathSpot1 = spot;
            _return1.Has = false;
        }
        else if (ReferenceEquals(hero, _hero2))
        {
            _deathSpot2 = spot;
            _return2.Has = false;
        }
        if (Verbose)
        {
            Log($"[LocalCoop] death spot recorded for {(ReferenceEquals(hero, _hero1) ? "P1" : "P2")}: " +
                $"{(spot.Has ? $"{spot.Cx},{spot.Cy}" : "unusable (hero had no cell)")}");
        }
    }

    /// <summary>
    /// The remembered fall spot of a <b>downed</b> hero, when it lies on <paramref name="level"/>
    /// (the level the survivor is standing on). A hero that fell on a previous level leaves no
    /// usable spot: its cell means nothing on this one.
    /// </summary>
    private static bool TryGetDeathSpot(Hero? downed, Level? level, out TeleportReturn spot)
    {
        var s = ReferenceEquals(downed, _hero1) ? _deathSpot1
            : ReferenceEquals(downed, _hero2) ? _deathSpot2
            : default;
        spot = s;
        return s.Has && level != null && ReferenceEquals(s.Level, level);
    }

    /// <summary>
    /// The life a downed hero is held at. Deliberately <b>1, not 0</b>: <c>Mob.fixedUpdate</c> clears
    /// its <c>nemesisTarget</c> as soon as that target's <c>life <= 0</c> (Mob.hx:3665-3666),
    /// and <c>TimeKeeper.behaviourAi</c> then dereferences <c>((Hero)nemesisTarget).collisionMode</c>
    /// with <b>no null guard</b> (TimeKeeper.hx:1065, the <c>!isInDanger</c> branch) — so a downed
    /// hero left in the level at exactly 0 HP crashes the game with
    /// <c>Null access .collisionMode</c> as soon as the boss's AI runs. The vanilla game never hits
    /// that path because a hero at 0 HP is dead and the run is over; independent health keeps the
    /// hero alive in the level, so we keep it at 1 HP instead. The visible "empty bar" is produced
    /// by the P2 bar (and reads 0 for a downed hero), not by the hero's own life value.
    /// </summary>
    private const int DownedLife = 1;

    /// <summary>Throttle for the audio-state diagnostic while a hero is down.</summary>
    private static double _audioStateLogTimer;

    /// <summary>
    /// Dumps the game's audio manager state. Only meaningful with logging on; used to pin down what
    /// exactly gets muted/ducked when the global hero goes down (the "P1 death breaks sound" bug).
    /// </summary>
    private static void LogAudioState(string when)
    {
        try
        {
            var a = dc.Audio.Class?.ME;
            if (a == null)
            {
                Log($"[LocalCoop][audio] {when}: (no Audio.ME)");
                return;
            }
            Log($"[LocalCoop][audio] {when}: cineMute={a.keyFrameCineMute} " +
                $"musicVolOverride={a.musicVolumeOverride:F2} reverbMix={a.reverbMix:F2} " +
                $"modal={a.modalEffectMix:F2} jingle={a.jingleEffectMix:F2} time={a.timeEffectMix:F2} " +
                $"music={(a.music != null)} overrideMusic={(a.musicOverride != null)}");
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop][audio] could not read the audio state: {Message}", ex.Message);
        }
    }

    /// <summary>Whether the given hero is currently down (independent health, life 0, run continues).</summary>
    internal static bool IsDown(Hero? hero)
    {
        if (hero == null)
        {
            return false;
        }
        if (ReferenceEquals(hero, _hero1))
        {
            return _hero1Down;
        }
        if (ReferenceEquals(hero, _hero2))
        {
            return _hero2Down;
        }
        return false;
    }

    /// <summary>
    /// A hero that is still in the fight: exists, is not destroyed and is not down. Deliberately does
    /// <b>not</b> consult <c>life</c> — a downed hero legitimately sits at 0 HP now that down no longer
    /// clamps it to 1, so "life &gt; 0" would wrongly call a downed hero alive (or a team with no
    /// living member still alive). The down flag is the authority; <see cref="IsDown"/> owns it.
    /// </summary>
    private static bool IsAlive(Hero? hero)
    {
        return hero != null && !hero.destroyed && !IsDown(hero);
    }

    /// <summary>
    /// Puts a hero into the down state: hidden <b>visually</b> (sprite + head batches only — never
    /// <c>Entity.visible</c> / <c>isFreeze</c>, which silence the game audio on the global hero) and
    /// kept in the level at 0 HP, with its input controller parked so it cannot act. Revived on
    /// the next forge level. Does NOT run the game's death flow.
    /// </summary>
    private static void MarkDown(Hero hero)
    {
        try
        {
            // IMPORTANT: do NOT set isFreeze on a downed hero. isFreeze skips the hero's update(),
            // and for the *global* hero (Game.ME.hero) that also stops whatever the game updates
            // from that hero every frame — which silences the game audio for the still-alive player
            // two. The log confirmed the death was *prevented* (no death sequence) and no audio mute
            // flag was set, so the frozen global hero itself is what breaks the audio. The input
            // controller is parked instead (MirrorGating), which is what actually stops
            // move / jump / roll / turn / attack.
            //
            // Hold the hero at DownedLife (1), NOT at 0. A downed hero stays in the level, and
            // Mob.fixedUpdate drops its nemesisTarget the moment that target's life <= 0 — after
            // which the boss AI dereferences it without a null check (see DownedLife). Returning
            // true from tryToPreventDeath is what actually stops the death flow, so the hero never
            // dies from this value; the "empty bar" the player sees comes from the P2 bar, which
            // reports 0 for a downed hero regardless of the real life value.
            HideHero(hero, true);
            if (hero.life <= 0)
            {
                hero.life = DownedLife;
            }

            // A downed hero must stop drawing aggro. It is hidden, inert and held at 1 HP, and the
            // game's own "drop the target" rule only fires at life <= 0 (Mob.fixedUpdate) — which
            // is exactly the value a downed hero cannot have (see DownedLife). So mobs kept
            // walking to the spot player two fell instead of fighting the player who is still
            // standing. Clearing the targetable flag keeps the hero out of every target scan
            // (Mob.setNemesisTarget and the AI's searches all check it) without touching its team
            // or its quad-tree registration — the level still tracks it, which the on-screen
            // bookkeeping relies on. A downed P1 looked "fine" only because it is moved onto the
            // survivor for the audio listener, so its aggro landed next to the living player.
            hero._targetable = false;
            // Targetable only stops the *search* for a nemesis. Detection is a second gate:
            // `canBeDetected()` answers false only for a destroyed or invisible entity, and a
            // downed hero is merely hidden (its sprite is off, not its detection), so mobs that
            // already had it as their ATTACK target kept swinging at the spot it fell. This flag
            // is the game's own "do not see me" (masks, cats, bosses during cutscenes use it), so
            // those mobs drop the attack target and re-search.
            hero.ignoreDetection = true;

            if (ReferenceEquals(hero, _hero1))
            {
                _hero1Down = true;
            }
            else if (ReferenceEquals(hero, _hero2))
            {
                _hero2Down = true;
            }
            // Remember the fall spot NOW: from the next frame on, a downed player one is dragged
            // onto the survivor for the audio listener, and its live position stops being the body's
            // place. The survivor's teleport key needs where the hero actually went down.
            RecordDeathSpot(hero);
            PersistDownState();
            Log($"[LocalCoop] {(ReferenceEquals(hero, _hero1) ? "P1" : "P2")} is down (run continues), "
                + $"life={hero.life} (held at DownedLife={DownedLife})");
            LogAudioState("after down");
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not mark a hero down: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Revives every downed hero at 1 HP: sets life first, then unfreezes and shows. Called on a
    /// level transition. Idempotent (a hero that is not down is skipped).
    /// </summary>
    private static void ReviveDownHeroes()
    {
        if (!Enabled)
        {
            return;
        }
        var forge = LevelHasForge();
        if (Verbose)
        {
            Log($"[LocalCoop] ReviveDownHeroes: P1 down={_hero1Down} life={_hero1?.life} | " +
                $"P2 down={_hero2Down} life={_hero2?.life} | forge={forge}");
        }
        // Downed heroes only revive where mutations are chosen (a level containing the PerkMaster
        // NPC). Every other level transition leaves them down, so dying has consequences for the
        // rest of the biome.
        if (!forge)
        {
            return;
        }
        TryReviveDown(_hero1, ref _hero1Down, "P1");
        TryReviveDown(_hero2, ref _hero2Down, "P2");
    }

    /// <summary>
    /// True when the current level contains the mutation-selection NPC (<c>PerkMaster</c>). Gates
    /// the revive: a downed hero stays down across normal level transitions and only comes back
    /// where mutations are chosen.
    /// </summary>
    private static bool LevelHasForge()
    {
        try
        {
            var level = (_hero1 ?? _hero2)?._level;
            var entities = level?.entities;
            if (entities == null)
            {
                return false;
            }
            for (var i = 0; i < entities.length; i++)
            {
                if (entities.getDyn(i) is dc.en.inter.npc.PerkMaster)
                {
                    return true;
                }
            }
        }
        catch
        {
            // Never let the revive-point probe break a transition.
        }
        return false;
    }

    private static void TryReviveDown(Hero? hero, ref bool downFlag, string who)
    {
        if (hero == null || hero.destroyed || !downFlag)
        {
            return;
        }
        try
        {
            var before = hero.life;

            // A genuinely downed hero is at DownedLife (1) or below; only then set the revived HP.
            // A *stale* down flag on a healthy hero must not drag its life down — that was the
            // "P2's health also drops to 1 on the new level" bug (the flag survived while the game
            // had already restored life). Comparing against DownedLife rather than 0 keeps that
            // guard meaningful now that a downed hero is held at 1 instead of 0.
            if (before <= DownedLife)
            {
                // life before anything else: at the downed floor the next update could re-trigger
                // death, so give it HP first and only then show / restore collision.
                hero.life = 1;
            }

            HideHero(hero, false);
            // Back in the fight: mobs may target and detect it again.
            hero._targetable = true;
            hero.ignoreDetection = false;
            // Restore collision now that the hero is back in the fight (FollowAliveHero turned it off).
            hero.hasRepelling = true;
            downFlag = false;
            PersistDownState();
            // The hero is standing again: the fall spot it left behind is stale and must not be a
            // teleport destination any more.
            if (ReferenceEquals(hero, _hero1))
            {
                _deathSpot1.Has = false;
            }
            else if (ReferenceEquals(hero, _hero2))
            {
                _deathSpot2.Has = false;
            }
            Log($"[LocalCoop] {who} revived on the new level (life {before} -> {hero.life})");
            LogAudioState("after revive");
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not revive {Who}: {Message}", who, ex.Message);
        }
    }

    /// <summary>Clears the down flags and the fall spots; a new run starts with nobody down.</summary>
    private static void ResetDownState()
    {
        _hero1Down = false;
        _hero2Down = false;
        _deathSpot1.Has = false;
        _deathSpot2.Has = false;
    }

    /// <summary>
    /// Re-applies the down state every frame: the game can re-show a hero or restore its life on its
    /// own (e.g. an equipment-change sync touching the downed hero). Keeps it hidden, at 1 HP, and
    /// clears any audio mute left behind, until <see cref="ReviveDownHeroes"/> clears the flag.
    /// </summary>
    private static void ReassertDownState()
    {
        if (!Enabled)
        {
            return;
        }
        TryReassertDown(_hero1, ref _hero1Down);
        TryReassertDown(_hero2, ref _hero2Down);

        // The game spatialises its positional audio around the *global* hero (player one). A downed
        // player one parked at the death spot makes everything around the surviving player two (who
        // may be far away) inaudible — the "P1 dies and the sound goes away" bug — and a level change
        // fixes it only because player one respawns next to player two. Keep the (invisible) downed
        // global hero on the living one so the listener follows the action. Only P1 matters: a downed
        // P2 leaves the global hero (P1) untouched, so its audio is fine.
        if (_hero1Down && _hero2 != null && !_hero2.destroyed && _hero2._level != null)
        {
            FollowAliveHero(_hero1, _hero2);
        }

        if (_hero1Down || _hero2Down)
        {
            ClearDeathAudioMute();
            _audioStateLogTimer += 1;
            if (_audioStateLogTimer >= 180)
            {
                _audioStateLogTimer = 0;
                LogAudioState("while down");
            }
        }
        else
        {
            _audioStateLogTimer = 0;
        }
    }

    /// <summary>
    /// Moves the downed hero onto the living one so the game's positional audio listener (which
    /// follows the global hero) stays with the player who is actually playing. The downed hero is
    /// invisible, so this is not visible; repelling is turned off so it does not shove the living
    /// hero around.
    /// </summary>
    private static void FollowAliveHero(Hero? downed, Hero alive)
    {
        if (downed == null || downed.destroyed)
        {
            return;
        }
        try
        {
            downed.hasRepelling = false;
            PlaceNextTo(downed, alive);
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not keep the downed hero on the listener: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// The hero that is still standing, when <paramref name="downed"/> is the one that fell.
    /// <c>null</c> when both are down (the run is ending) or when the partner is gone.
    /// </summary>
    private static Hero? LivingPartner(Hero? downed)
    {
        if (downed == null)
        {
            return null;
        }

        var partner = ReferenceEquals(downed, _hero1) ? _hero2 : _hero1;
        if (partner == null || partner.destroyed || IsDown(partner))
        {
            return null;
        }

        return partner;
    }

    /// <summary>
    /// Whether an entity is one of the two heroes and is currently down. Used by the mob tick to
    /// drop a downed hero as its aggro target — the game only clears a target at <c>life <= 0</c>,
    /// and a downed hero is held at <see cref="DownedLife"/> precisely because 0 crashes the boss
    /// AI (see <see cref="MarkDown"/>).
    /// </summary>
    /// <summary>
    /// Whether a hero was down when the run was last saved — the down state itself is runtime-only,
    /// so this is what carries it across a quit-and-continue (see <see cref="TryRestorePersistedDown"/>).
    /// Written only by <see cref="PersistDownState"/> and cleared on revive / session off / a stale
    /// marker, never by the ordinary run teardown (which must not wipe it).
    /// </summary>
    private static bool _persistedHero1Down;

    private static bool _persistedHero2Down;

    /// <summary>Copies the runtime down flags into the persisted ones and writes the settings file.</summary>
    private static void PersistDownState()
    {
        _persistedHero1Down = _hero1Down;
        _persistedHero2Down = _hero2Down;
        SaveSettings();
    }

    /// <summary>Forgets the persisted down state (a session that is switched off has no run to keep down).</summary>
    private static void ClearPersistedDownState()
    {
        _persistedHero1Down = false;
        _persistedHero2Down = false;
    }

    /// <summary>
    /// Restores a down state that was persisted when the run was saved. A quit-and-continue reloads
    /// the level from the save, which only knows the hero at <see cref="DownedLife"/> — the runtime
    /// flags died with the process — so without this the hero came back alive at 1 HP, which the
    /// design forbids (only the forge revives). The life check is what makes it safe: a brand-new
    /// run starts at full life, so a stale marker can never hide a healthy hero.
    /// </summary>
    private static void TryRestorePersistedDown(Hero? hero, bool persisted, string who)
    {
        if (hero == null || hero.destroyed || !persisted)
        {
            return;
        }

        try
        {
            if (hero.life > DownedLife)
            {
                // Stale: the hero is healthy, so this is a new run (or the marker outlived the run
                // it described). Drop it rather than hide a living hero.
                ClearPersistedDownState();
                SaveSettings();
                Log($"[LocalCoop] dropped a stale {who} down marker (hero is at {hero.life} HP)");
                return;
            }

            Log($"[LocalCoop] {who} was down when the run was saved; keeping it down");
            MarkDown(hero);
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not restore {Who}'s down state: {Message}", who, ex.Message);
        }
    }

    private static bool IsDownedHero(dc.Entity? entity)
    {
        if (entity == null)
        {
            return false;
        }

        return (ReferenceEquals(entity, _hero1) && _hero1Down)
            || (ReferenceEquals(entity, _hero2) && _hero2Down);
    }

    private static void TryReassertDown(Hero? hero, ref bool downFlag)
    {
        if (hero == null || hero.destroyed)
        {
            return;
        }

        if (!downFlag)
        {
            // A hero that is not down must be a normal target again. These two flags are set only
            // while down, and a run reloaded from a save can hand back a hero that still carries
            // them — which is why a reloaded hero was invisible to mobs (they never attacked it).
            try
            {
                if (!hero._targetable)
                {
                    hero._targetable = true;
                }
                if (hero.ignoreDetection)
                {
                    hero.ignoreDetection = false;
                }
            }
            catch (Exception ex)
            {
                LogVerbose($"[LocalCoop] could not clear a stale down flag: {ex.Message}");
            }

            return;
        }

        try
        {
            // Keep the hero at DownedLife (1) and hidden. Do NOT freeze it — isFreeze on the global
            // hero silences the game audio (see MarkDown); the parked controller keeps it inert.
            // Never push it down to 0: a hero sitting at 0 in the level makes Mob.fixedUpdate drop
            // its nemesisTarget and the boss AI then crashes (see DownedLife). This re-assert exists
            // because the game can restore life on its own (an equipment-change sync touching the
            // downed hero), not to lift it off the floor value.
            if (hero.life <= 0)
            {
                hero.life = DownedLife;
            }
            HideHero(hero, true);
            // The game hands these flags back on its own (a respawn, an equipment sync), and a
            // downed hero that can be targeted or detected draws aggro to a body nobody is
            // playing (see MarkDown).
            hero._targetable = false;
            hero.ignoreDetection = true;
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not re-assert a downed hero: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Hides a hero <b>visually only</b>. Never touches <c>Entity.visible</c> or <c>isFreeze</c>:
    /// those affect how the entity is processed, and on the global hero that is what silenced the
    /// game audio. The body sprite (<c>spr.visible</c> / <c>sprAlpha</c>) and the separate head
    /// sprite batches (<c>headNormalSb</c> / <c>headAddSb</c>, the floating "soul") are hidden
    /// instead, while the entity keeps updating normally.
    /// </summary>
    private static void HideHero(Hero hero, bool hidden)
    {
        try
        {
            var spr = hero.spr;
            if (spr != null)
            {
                spr.visible = !hidden;
            }
            hero.sprAlpha = hidden ? 0.0 : 1.0;
            var head = hero.heroHead;
            if (head != null)
            {
                if (head.headNormalSb != null)
                {
                    head.headNormalSb.visible = !hidden;
                }
                if (head.headAddSb != null)
                {
                    head.headAddSb.visible = !hidden;
                }
            }
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not {What} a hero: {Message}", hidden ? "hide" : "show", ex.Message);
        }
    }

    /// <summary>
    /// Undo the cinematic audio mute the game's death flow leaves behind. A hero death sets
    /// <c>Audio.ME.keyFrameCineMute</c> (and fades the music) but we skip the death, so it is never
    /// restored — all SFX stay silent until a level change rebuilds the audio manager. Clearing the
    /// flag while a hero is down restores the audio immediately.
    /// </summary>
    private static void ClearDeathAudioMute()
    {
        try
        {
            var audio = dc.Audio.Class?.ME;
            if (audio != null && audio.keyFrameCineMute)
            {
                audio.keyFrameCineMute = false;
                Log("[LocalCoop] cleared the cinematic audio mute left by the death flow");
            }
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not clear the death audio mute: {Message}", ex.Message);
        }
    }

}
