using dc;
using dc.level;
using dc.libs;
using dc.hl.types;
using Hashlink.Virtuals;
using ModCore.Utilities;

namespace DeadCellsLocalMultiplayerMod;

/// <summary>
/// The PvP arena's level structure: a small, closed, mob-free graph built from the game's own
/// training-room templates.
/// <para>
/// <b>Why a subclass of <see cref="LevelStruct"/> and not a new <c>data.cdb</c> level.</b> The
/// framework reaches a level's generator through <c>LevelStruct.get(user, levelInfo, rng)</c>, which
/// is a hard-coded chain of string comparisons over the CDB <c>level.id</c> — there is no registry.
/// So a mod that wants its own generator either adds a new CDB row plus a hook on that factory, or
/// <b>replaces the generator of an id that already exists</b>. The arena takes the second road:
/// <see cref="Install"/> hooks <c>Hook__LevelStruct.get</c> and swaps this class in for the vanilla
/// training level's id, and only while a match has been armed. Registering a new level would also
/// need a world-map logo entry (<c>LevelLogos</c>), a biome row and hand-authored <c>.tmx</c> room
/// files — none of which PvP needs, because the rooms it uses already ship with the game.
/// </para>
/// <para>
/// <b>Which rooms.</b> Every edge below is an edge the game's own training structure uses
/// (decompiled <c>dc/level/struct/TrainingDojo.cs</c>): <c>TrainingEntrance</c> → <c>TrainingArmory</c>
/// → <c>TrainingDojoDoor</c> → <c>TrainingDojoTransition</c> → <c>TrainingDojoBig</c>. Reusing a
/// proven edge list is deliberate: room templates connect through their <c>lnk</c> tile layer, and an
/// edge that does not exist between two templates aborts level generation
/// (<c>invalid … link group</c> / <c>not enough exits for template</c>). So the arena is the same
/// corridor the game walks, minus the boss doors, the extra arenas and the training NPCs.
/// </para>
/// <para>
/// <b>No biome override.</b> The <c>Training</c> room group was authored for this level's biome
/// (<c>PrisonStart</c>), so its tile sets only look right under it. Forcing another biome through
/// <c>addGenData(specificBiome)</c> — the trick the official tutorial uses — would draw those rooms
/// with the wrong atlas. Left alone on purpose.
/// </para>
/// </summary>
internal sealed class PvpArenaStruct : LevelStruct
{
    private const string LevelId = "TrainingDojo";

    /// <summary>
    /// The level id the arena impersonates. Exposed so the state machine can tell "we are standing on
    /// the arena" from "some other training run", and the menu can show what it is overriding.
    /// </summary>
    internal static string ArenaLevelId => LevelId;

    /// <summary>
    /// The room the fight happens in. Kept as data so the panel can say where the heroes are, and so
    /// the corridor before it stays the natural "walk to the arena" intro.
    /// </summary>
    internal const string ArenaRoomTemplate = "TrainingDojoBig";

    private static bool _installed;

    public PvpArenaStruct(
        User user,
        virtual_baseLootLevel_biome_bonusTripleScrollAfterBC_cellBonus_dlc_doubleUps_eliteRoomChance_eliteWanderChance_flagsProps_group_icon_id_index_loreDescriptions_mapDepth_minGold_mobDensity_mobs_name_nextLevels_parallax_props_quarterUpsBC3_quarterUpsBC4_specificLoots_specificSubBiome_transitionTo_tripleUps_worldDepth_ level,
        Rand rng)
        : base(user, level, rng)
    {
    }

    /// <summary>
    /// Installs the factory hook once. Registered from <see cref="LocalCoop.Install"/> so a single
    /// hook governs both modes: outside an armed match it falls straight through to the game's own
    /// chain, which leaves vanilla training mode exactly as it was.
    /// </summary>
    internal static void Install()
    {
        if (_installed)
        {
            return;
        }

        _installed = true;

        Hook__LevelStruct.get += (orig, user, l, rng) =>
        {
            try
            {
                // IsPvp, not PvpEnabled: the substitution belongs to an armed match only. A player
                // who leaves the mode switched on but plays an ordinary co-op run that wanders into
                // the game's own training door must get the vanilla dojo, not the arena.
                if (l != null && LocalCoop.IsPvp && l.id == ArenaLevelId.AsHaxeString())
                {
                    LocalCoop.Log("[PvP] arena structure substituted for the training level");
                    return new PvpArenaStruct(user, l, rng);
                }
            }
            catch (Exception ex)
            {
                // An exception inside a hook is fatal to the game: fall through to vanilla instead.
                LocalCoop.LogWarn("[PvP] arena factory failed: {Message}", ex.Message);
            }

            return orig(user, l, rng);
        };
    }

    /// <summary>
    /// The arena: one room, nothing else. This is the shape the game's own <c>QueenArena</c>
    /// structure uses — a single forced-template node, no <c>Entrance</c> node, no
    /// <c>createExit</c>, empty <c>addTeleports</c> (decompiled dc/level/struct/QueenArena.cs:19-35) —
    /// and it is the right shape for a duel: a multi-room graph would leave player two stranded at
    /// the door while player one is scripted to walk in, and every extra room is a place a stray
    /// entity or a loot table can come from.
    /// </summary>
    public override RoomNode buildMainRooms()
    {
        try
        {
            return createNode(null, ArenaRoomTemplate.AsHaxeString(), null, "arena".AsHaxeString());
        }
        catch (Exception ex)
        {
            // Never leave generation with nothing to build on: hand back the training entrance, which
            // is a room that always exists, so the run can still be abandoned from the pause menu.
            LocalCoop.LogWarn("[PvP] arena room failed, falling back to the entrance: {Message}", ex.Message);
            return createNode(null, "TrainingEntrance".AsHaxeString(), null, "start".AsHaxeString());
        }
    }

    // ---- Everything that would put entities, exits or side content in the arena is switched off.
    // Each of these is a virtual on LevelStruct that the game's own LevelGen.generateGraph calls in
    // sequence; empty overrides are how the arena stays a closed room with nothing in it but the
    // two players. ----

    /// <summary>No side rooms: no shops, no treasure, no extra combat rooms.</summary>
    public override void buildSecondaryRooms()
    {
    }

    /// <summary>No timed doors, so no round can be lost to a closing gate.</summary>
    public override void buildTimedDoors()
    {
    }

    /// <summary>No triggered (kill-everything) doors.</summary>
    public override void buildTriggeredDoors(ArrayObj combatRooms)
    {
    }

    /// <summary>No Z-challenge branches.</summary>
    public override void buildZChallengeDoors()
    {
    }

    /// <summary>No elite rooms — elite mobs would be a third party in a duel.</summary>
    public override void buildEliteRooms()
    {
    }

    /// <summary>No teleports: an exit that bypasses the arena is an exit from the match.</summary>
    public override void addTeleports()
    {
    }

    /// <summary>No lore rooms.</summary>
    public override void buildLoreRooms()
    {
    }

    /// <summary>No essential lore rooms either, for the fixed-seed path.</summary>
    public override void buildEssentialLoreRooms()
    {
    }

    /// <summary>
    /// No difficulty pass. The arena must not gain mobs or scaling loot from the game's difficulty
    /// tables, so this stays empty rather than calling the base implementation.
    /// </summary>
    public override void applyDifficulty()
    {
    }

    /// <summary>
    /// The vanilla training structure pushes <c>NpcId.TrainingKnight</c> onto the entrance and the
    /// mobs room here. That is exactly what must not appear in a duel — a sparring NPC is a third
    /// combatant that can land the killing blow and score a bogus round — so this override is
    /// deliberately empty.
    /// </summary>
    public override void finalize()
    {
    }
}
