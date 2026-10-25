using dc;
using dc.en;
using dc.level;
using dc.libs;
using dc.pr;
using dc.tool;
using HaxeProxy.Runtime;
using ModCore.Utilities;

namespace DeadCellsLocalMultiplayerMod;

/// <summary>
/// Part of <see cref="LocalCoop"/>: the PvP match state machine.
/// <para>
/// <b>What a match is.</b> Both heroes are dropped into an arena with their own health and their own
/// gear and fight until one of them wins <see cref="RoundsToWin"/> rounds. There are no mobs, no loot
/// and no exit: a lethal hit scores a round for the other player, then both heroes are restored and
/// put back on their start spots for the next round.
/// </para>
/// <para>
/// <b>Which level.</b> The arena is the game's own training level (<c>TrainingDojo</c>) entered
/// through the game's own training launch, not a new <c>data.cdb</c> row. Its room graph comes from
/// <see cref="PvpArenaStruct"/>, substituted for the training generator by a hook on the level-struct
/// factory. That keeps this feature out of the data table, out of the world map and out of the
/// hand-authored <c>.tmx</c> pipeline, and it inherits training mode's plumbing for free —
/// achievements are suppressed, the run is not saved as a normal one, and a killed hero respawns
/// rather than ending anything.
/// </para>
/// <para>
/// <b>How the rules hook in.</b> PvP registers no new hero hooks; it answers the existing ones.
/// <see cref="PvpHealthIndependent"/> forces independent health and <see cref="FriendlyFireActive"/>
/// forces friendly fire while a match is on, which is exactly the PvP condition and reuses the
/// friendly-fire paths already debugged for co-op (ranged through <c>canBeHitBy</c>/<c>isOpponent</c>
/// with owner resolution, melee through <c>Weapon.doAreaEffect</c>). A lethal hit is prevented and
/// turned into a scored round by <see cref="OnHeroTryToPreventDeath"/>.
/// </para>
/// <para>
/// <b>Why nothing here ends the run.</b> In training mode <c>Hero.kill()</c> routes to
/// <c>deathRespawn()</c> instead of the death flow (decompiled dc/en/Hero.cs:33035-33048), so a
/// "kill a hero to finish the match" design would respawn the loser and silently restart the fight
/// instead of showing a result. The match therefore stops at the banner
/// (<see cref="PvpState.MatchOver"/>), and leaving it is the vanilla pause menu's job — which already
/// works, because that is how a player leaves training mode today.
/// <see cref="StartPvpSession"/> starts another one from the title menu.
/// </para>
/// </summary>
internal static partial class LocalCoop
{
    /// <summary>Phase of the PvP session. <see cref="PvpState.Idle"/> means the co-op rules apply.</summary>
    internal enum PvpState
    {
        /// <summary>No match: normal local co-op.</summary>
        Idle,

        /// <summary>Set up: the run is launching and the heroes are on their way to the arena.</summary>
        Starting,

        /// <summary>Fighting: a lethal hit on either hero scores a round for the other.</summary>
        Fighting,

        /// <summary>Round over: counting down to the next round's restart.</summary>
        RoundBreak,

        /// <summary>Match over: the winner banner is up. A terminal state — see the class summary.</summary>
        MatchOver,
    }

    internal static PvpState Pvp { get; private set; } = PvpState.Idle;

    /// <summary>Whether the heroes are currently playing a match rather than co-op.</summary>
    internal static bool IsPvp => Pvp != PvpState.Idle;

    /// <summary>
    /// Whether PvP is switched on in the menu. It changes nothing about the rules until a match is
    /// actually armed, so toggling it mid-run cannot half-apply a rule set.
    /// </summary>
    internal static bool PvpEnabled { get; private set; }

    /// <summary>Rounds a player must win to take the match. Cycled in the menu (1–10).</summary>
    internal static int RoundsToWin { get; private set; } = 3;

    /// <summary>
    /// The multipliers the menu offers for a duel's life pool. 1× is the vanilla pool; the larger
    /// ones make rounds last longer, which is what a duel between two equally geared heroes needs —
    /// at 1× a single solid hit often ends the round before either player has engaged.
    /// </summary>
    private static readonly int[] HealthMultipliers = [1, 2, 5, 10];

    /// <summary>
    /// How many times the normal life pool a match is fought with. Applies to <b>both</b> heroes, so
    /// the duel stays symmetrical; it is a multiplier on the pool they enter the arena with, not a
    /// flat value, so gear and scrolls still matter.
    /// </summary>
    internal static int PvpHealthMultiplier { get; private set; } = 1;

    /// <summary>Whether a multiplier is one the menu offers (used to validate a loaded config).</summary>
    private static bool IsValidHealthMultiplier(int value) => System.Array.IndexOf(HealthMultipliers, value) >= 0;

    /// <summary>Steps to the next offered multiplier, wrapping back to 1×.</summary>
    internal static void CyclePvpHealthMultiplier()
    {
        var index = System.Array.IndexOf(HealthMultipliers, PvpHealthMultiplier);
        PvpHealthMultiplier = HealthMultipliers[(index + 1) % HealthMultipliers.Length];
        SaveSettings();
        Log($"[PvP] health multiplier -> {PvpHealthMultiplier}x");
    }

    /// <summary>The multiplier as it should read in the menu ("1x", "10x").</summary>
    internal static string PvpHealthMultiplierLabel => $"{PvpHealthMultiplier}x";

    /// <summary>Rounds won by player one / player two in the current match.</summary>
    internal static int ScoreP1 { get; private set; }

    internal static int ScoreP2 { get; private set; }

    /// <summary>The hero that lost the round currently being tallied, or null.</summary>
    internal static Hero? LastRoundLoser { get; private set; }

    /// <summary>The player that won the match (1 or 2), once <see cref="PvpState.MatchOver"/> is set.</summary>
    internal static int MatchWinner { get; private set; }

    /// <summary>Seconds until the next round starts, ticking in <see cref="TickPvp"/>.</summary>
    internal static double RoundBreakLeft { get; private set; }

    /// <summary>
    /// Whether a match has finished and the banner is up. The panel holds the result on screen until
    /// the player leaves through the pause menu or starts another match from the title.
    /// </summary>
    internal static bool IsMatchOver => Pvp == PvpState.MatchOver;

    /// <summary>Seconds a round waits before hits count, so nobody is scored against on the spawn.</summary>
    private static double _freezeLeft;

    /// <summary>Frames spent waiting for the heroes to land in the arena room before opening round one.</summary>
    private static int _arenaWaitFrames;

    /// <summary>How long to wait for the arena room (~20s at 60fps) before opening the match anyway.</summary>
    private const int ArenaWaitFrames = 1200;

    /// <summary>Round the match is on (1-based), for the panel and the log.</summary>
    internal static int RoundNumber { get; private set; }

    /// <summary>
    /// Start spots for the two players, in level cells, captured once when the arena is reached
    /// (see <see cref="CaptureStartSpots"/>). Every round restart puts the heroes back here.
    /// </summary>
    private static int _startCx1 = -1;
    private static int _startCy1 = -1;
    private static int _startCx2 = -1;
    private static int _startCy2 = -1;

    /// <summary>Grace at the start of a round: hits are refused so neither player dies on spawn.</summary>
    private const double StartFreezeSeconds = 1.5;

    /// <summary>Break between rounds.</summary>
    private const double RoundBreakSeconds = 3.0;

    /// <summary>
    /// Whether the co-op health rule should be read as "independent": a match always is, and outside
    /// a match the menu setting decides. Every place that used to read <see cref="SharedHealth"/>
    /// reads this instead, so a match cannot be half-applied.
    /// </summary>
    internal static bool PvpHealthIndependent => IsPvp || !SharedHealth;

    /// <summary>
    /// Whether a curse is one shared counter for both heroes. The curse follows the <b>health</b>
    /// mode (by request), not the equipment mode: with shared health the two heroes are one life
    /// pool, so the curse that raises the stakes on that pool must be one counter — a curse taken by
    /// either player lands on player one (the global hero the game damages), and either player's
    /// kills reduce it. With independent health each hero carries its own curse.
    /// </summary>
    internal static bool CurseShared => !PvpHealthIndependent;

    /// <summary>
    /// Whether hero-vs-hero damage can land <em>right now</em>. Both friendly-fire gates and the
    /// melee injection read this one predicate instead of <see cref="FriendlyFire"/>, so the three
    /// paths cannot disagree — one gate allowing hits while another refuses them is how a "PvP with
    /// no damage" bug hides. In a match damage is mandatory but round-scoped (see
    /// <see cref="CanDealPvpDamage"/>); outside one, the menu setting rules.
    /// </summary>
    internal static bool FriendlyFireActive => IsPvp ? CanDealPvpDamage : FriendlyFire;

    /// <summary>
    /// A round is being fought and its grace window has passed. The countdown itself lives in
    /// <see cref="TickPvp"/>; this is what the damage gates read.
    /// </summary>
    private static bool CanDealPvpDamage => Pvp == PvpState.Fighting && _freezeLeft <= 0;

    /// <summary>
    /// Turns PvP on or off from the title menu. Turning it off also drops any leftover match state, so
    /// a following co-op run cannot inherit a live scoreboard.
    /// </summary>
    internal static void SetPvpEnabled(bool value)
    {
        PvpEnabled = value;
        if (!value)
        {
            EndPvpSession("mode turned off");
        }

        SaveSettings();
        Log($"[PvP] mode -> {value}");
    }

    /// <summary>Cycles the win target 1..10. A single round is a valid "sudden death" setting.</summary>
    internal static void CycleRoundsToWin()
    {
        RoundsToWin = RoundsToWin >= 10 ? 1 : RoundsToWin + 1;
        SaveSettings();
        Log($"[PvP] rounds to win -> {RoundsToWin}");
    }

    /// <summary>
    /// Arms a match: resets the score and puts the state machine in <see cref="PvpState.Starting"/>.
    /// Refuses to re-arm while a match is live, so the caller must clear it first
    /// (<see cref="StartPvpSession"/> does).
    /// </summary>
    internal static void MaybeBeginMatch()
    {
        if (!PvpEnabled || IsPvp)
        {
            return;
        }

        Pvp = PvpState.Starting;
        ScoreP1 = 0;
        ScoreP2 = 0;
        RoundNumber = 0;
        MatchWinner = 0;
        LastRoundLoser = null;
        _arenaWaitFrames = 0;
        _freezeLeft = StartFreezeSeconds;
        // A new match captures its heroes' natural pools afresh: a fresh run brings fresh heroes with
        // their own maxLife, and reusing the previous match's numbers would scale from a stale base.
        _pvpNaturalMaxLife.Clear();
        try
        {
            PvpHud.HideMatchStart();
        }
        catch
        {
        }
        Log($"[PvP] match armed (first to {RoundsToWin}, {PvpHealthMultiplier}x health)");
    }

    /// <summary>
    /// Starts a match the way the game starts its own training session: a fresh
    /// <c>LaunchMode.NewTrainingGame</c> through <c>Main.launchGame</c>. That is the game's supported
    /// "open a standalone short run" entry point — <c>TrainingDoor</c> uses exactly this call
    /// (decompiled dc/en/inter/TrainingDoor.cs:848-851) — and it is what lets the arena inherit
    /// training mode's behaviour instead of reimplementing a run lifecycle.
    /// </summary>
    internal static void StartPvpSession()
    {
        try
        {
            var main = Main.Class.ME;
            if (main == null)
            {
                LogWarn("[PvP] Main.ME is not available yet; cannot start a match");
                return;
            }

            // Drop any finished match first: MaybeBeginMatch deliberately refuses to re-arm while a
            // match is live, so a rematch would otherwise keep the old score and the old state.
            EndPvpSession("new match");

            // StartPvpSession is the one entry point that must work however it is reached (menu
            // button, future hotkey): the mode flag is what MaybeBeginMatch checks, so set it here
            // rather than relying on the caller. Without this, a caller that skipped the toggle
            // would launch a training run that is *not* a match — a dead half-state.
            PvpEnabled = true;

            // A match needs two heroes, and the second hero only exists once the local-co-op
            // session is active. Arm it with the saved devices rather than asking the player to
            // have toggled anything first — "Start a match" must work from a cold title screen.
            if (!Enabled)
            {
                Activate(Player1, Player2);
            }

            MaybeBeginMatch();
            SaveSettings();

            var mode = new LaunchMode.NewTrainingGame(false);
            main.launchGame(mode, (bool?)null, (double?)0.5);
            Log("[PvP] launched a training-mode run as the arena");
        }
        catch (Exception ex)
        {
            LogWarn("[PvP] could not launch the arena: {Message}", ex.Message);
            EndPvpSession("launch failed");
        }
    }

    /// <summary>
    /// Clears the match state and hands the rules back to the co-op settings. Called when a match is
    /// abandoned (mode switched off, a new match armed) — never by the win condition, because the
    /// banner is meant to stay up.
    /// </summary>
    internal static void EndPvpSession(string reason)
    {
        if (Pvp == PvpState.Idle)
        {
            return;
        }

        Pvp = PvpState.Idle;
        _freezeLeft = 0;
        RoundBreakLeft = 0;
        _arenaWaitFrames = 0;
        LastRoundLoser = null;
        _startCx1 = _startCy1 = _startCx2 = _startCy2 = -1;
        PvpHud.Reset();
        Log($"[PvP] session ended ({reason})");
    }

    /// <summary>
    /// Per-frame match work: the grace countdown, the round-break countdown, and the arena-opening
    /// check. Runs from <see cref="OnFrameUpdate"/> once both heroes are known to exist.
    /// </summary>
    private static void TickPvp(double dt)
    {
        if (!IsPvp)
        {
            return;
        }

        if (_freezeLeft > 0)
        {
            _freezeLeft = System.Math.Max(0, _freezeLeft - dt);
        }

        switch (Pvp)
        {
            case PvpState.Starting:
                // The arena only opens once both heroes are actually standing in the arena room: on a
                // level entry the game scripts player one to auto-walk from the door cell, and an
                // earlier check would capture the corridor's cells as the start spots.
                TryOpenArena();
                break;

            case PvpState.RoundBreak:
                RoundBreakLeft = System.Math.Max(0, RoundBreakLeft - dt);
                if (RoundBreakLeft <= 0)
                {
                    StartRound();
                }

                break;

            case PvpState.MatchOver:
                // Terminal on purpose. See the class summary: the arena is a training-mode level, so
                // there is no safe "finish the run" call to make, and auto-relaunching a whole new run
                // from a frame tick would fight a player who is trying to stop.
                break;
        }
    }

    /// <summary>
    /// Opens round one once both heroes' cells resolve to the arena room
    /// (<see cref="PvpArenaStruct.ArenaRoomTemplate"/>). A generous timeout keeps a hero that never
    /// makes it there from hanging the match in <see cref="PvpState.Starting"/>: when it expires the
    /// arena opens wherever the heroes stand, which is still playable.
    /// </summary>
    private static void TryOpenArena()
    {
        var hero1 = _hero1;
        var hero2 = _hero2;
        if (hero1 == null || hero2 == null || hero1.destroyed || hero2.destroyed)
        {
            return;
        }

        _arenaWaitFrames++;

        if (!InArenaRoom(hero1) || !InArenaRoom(hero2))
        {
            if (_arenaWaitFrames > ArenaWaitFrames)
            {
                LogWarn($"[PvP] arena room not reached in {ArenaWaitFrames} frames; opening where the heroes are");
                OnArenaReady();
            }

            return;
        }

        OnArenaReady();
    }

    /// <summary>
    /// The arena room template as a Haxe string. Built <b>lazily</b>, not in a static initializer: a
    /// type's static fields run the first time the type is touched, which can be before the Hashlink
    /// VM is ready to allocate a Haxe string, and constructing Haxe objects off the VM's thread is
    /// what crashes the game. <see cref="InArenaRoom"/> only runs inside a level, so by then it is
    /// safe — and the value is cached because that check runs every frame.
    /// </summary>
    private static dc.String? _arenaRoomString;

    private static dc.String ArenaRoomString => _arenaRoomString ??= PvpArenaStruct.ArenaRoomTemplate.AsHaxeString();

    /// <summary>
    /// Whether this hero's current cell is inside a room built from the arena's room template. False
    /// for anything unreadable, so a transient level state just means "not ready yet".
    /// </summary>
    private static bool InArenaRoom(Hero hero)
    {
        try
        {
            var level = hero._level;
            if (level == null || hero.cx < 0 || hero.cy < 0 || !hero.awake)
            {
                return false;
            }

            var room = level.map.getRoomAt(hero.cx, hero.cy);
            return room != null && room.rTemplate == ArenaRoomString;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Records the start spots and opens round one. Idempotent — it only acts while
    /// <see cref="PvpState.Starting"/>.
    /// </summary>
    private static void OnArenaReady()
    {
        if (Pvp != PvpState.Starting)
        {
            return;
        }

        CaptureStartSpots();
        // Separate the heroes for round one. Hero two spawns beside hero one (the co-op placement),
        // and without this pull apart a match would open with both heroes sharing a cell, which is
        // an immediate mutual-hit situation the freeze window would only paper over.
        if (_hero1 != null && _hero2 != null)
        {
            RestoreToStartSpots(_hero1, _hero2);
        }

        // Round one needs the health multiplier applied here too: it is normally applied by
        // StartRound -> ReviveForNewRound, which only runs from round two onwards. Without this the
        // first round of every match was fought at the vanilla pool whatever the setting said.
        if (_hero1 != null)
        {
            ApplyHealthMultiplier(_hero1);
        }
        if (_hero2 != null)
        {
            ApplyHealthMultiplier(_hero2);
        }

        RoundNumber = 1;
        Pvp = PvpState.Fighting;
        _freezeLeft = StartFreezeSeconds;
        Log($"[PvP] round {RoundNumber} begins");
    }

    /// <summary>
    /// A lethal hit landed on <paramref name="loser"/>: score the round for the other player and
    /// decide whether the match is over. The caller (<see cref="OnHeroTryToPreventDeath"/>) has
    /// already held the hero at the floor value and marked it down, which is the only way to keep a
    /// "dead" hero in the level without crashing the mob AI (see LocalCoop.Down.cs / DownedLife).
    /// <para>
    /// Round 1 is a non-scoring warm-up: no point is awarded, no match progress is made, and
    /// after round 1 ends the match starts from round 2 with scoring and multiplied health.
    /// </para>
    /// </summary>
    private static void ScoreRoundLoss(Hero loser)
    {
        var hero1 = _hero1;
        var hero2 = _hero2;
        if (hero1 == null || hero2 == null)
        {
            return;
        }

        LastRoundLoser = loser;

        if (RoundNumber == 1)
        {
            Log($"[PvP] warm-up round {RoundNumber} ended -> match starts next");
            RoundNumber = 2;
            Pvp = PvpState.RoundBreak;
            RoundBreakLeft = RoundBreakSeconds;
            try
            {
                PvpHud.ShowMatchStart();
            }
            catch
            {
            }
            return;
        }

        var p1Lost = ReferenceEquals(loser, hero1);
        if (p1Lost)
        {
            ScoreP2++;
        }
        else
        {
            ScoreP1++;
        }

        Log($"[PvP] {(p1Lost ? "P1" : "P2")} lost round {RoundNumber} -> score {ScoreP1}:{ScoreP2}");

        if (ScoreP1 >= RoundsToWin || ScoreP2 >= RoundsToWin)
        {
            MatchWinner = ScoreP1 >= RoundsToWin ? 1 : 2;
            Pvp = PvpState.MatchOver;
            Log($"[PvP] match over: P{MatchWinner} wins {ScoreP1}:{ScoreP2}");
            PvpHud.ShowWinner(MatchWinner, ScoreP1, ScoreP2);
            return;
        }

        RoundNumber++;
        Pvp = PvpState.RoundBreak;
        RoundBreakLeft = RoundBreakSeconds;
    }

    /// <summary>
    /// Restores both heroes for the next round: full health, refilled ammo, back on their start
    /// spots, loser un-downed. Never destroys or re-creates heroes — a round restart is a reposition,
    /// which is what keeps the graphics, the input bindings and the equipment intact.
    /// </summary>
    private static void StartRound()
    {
        var hero1 = _hero1;
        var hero2 = _hero2;
        if (hero1 == null || hero2 == null)
        {
            EndPvpSession("heroes are gone");
            return;
        }

        try
        {
            PvpHud.HideMatchStart();
        }
        catch
        {
        }

        Pvp = PvpState.Fighting;
        RoundBreakLeft = 0;
        _freezeLeft = StartFreezeSeconds;
        LastRoundLoser = null;
        PvpHud.HideWinner();

        ReviveForNewRound(hero1, ref _hero1Down);
        ReviveForNewRound(hero2, ref _hero2Down);
        RestoreToStartSpots(hero1, hero2);
        Log($"[PvP] round {RoundNumber} begins");
    }

    /// <summary>
    /// Brings one hero back for a round: clear its down flag first (so the per-frame re-assert does
    /// not immediately re-hide it), scale its life pool to the match's multiplier, then restore
    /// health before showing it again — the same order <c>ReviveDownHeroes</c> uses, because a hero
    /// made visible at the floor value would take the next frame's lethal check again.
    /// </summary>
    private static void ReviveForNewRound(Hero hero, ref bool downFlag)
    {
        downFlag = false;

        ApplyHealthMultiplier(hero);

        try
        {
            hero.life = System.Math.Max(hero.life, hero.maxLife);
        }
        catch (Exception ex)
        {
            LogWarn("[PvP] could not restore life for a round: {Message}", ex.Message);
        }

        try
        {
            hero.fullHeal();
        }
        catch (Exception ex)
        {
            LogWarn("[PvP] fullHeal failed at round start: {Message}", ex.Message);
        }

        try
        {
            hero.refillAllAmmo();
        }
        catch (Exception ex)
        {
            LogWarn("[PvP] refillAllAmmo failed at round start: {Message}", ex.Message);
        }

        try
        {
            HideHero(hero, false);
            hero.hasRepelling = true;
        }
        catch (Exception ex)
        {
            LogWarn("[PvP] could not show a hero for the next round: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Scales one hero's life pool to <see cref="PvpHealthMultiplier"/>.
    /// <para>
    /// The multiplier is applied <b>once per hero</b>, tracked in <see cref="_pvpPoolAppliedTo"/>: the
    /// value is relative to the hero's natural pool, so re-applying it every round would compound
    /// (2× → 4× → 8×). The natural pool is remembered the first time a hero is seen
    /// (<see cref="_pvpNaturalMaxLife"/>) and every round recomputes <c>natural × multiplier</c> from
    /// it, which also means changing the setting between rounds takes effect cleanly.
    /// </para>
    /// <para>
    /// <c>bonusLife</c> is scaled by the same factor: in Dead Cells the drawn bar is
    /// <c>maxLife + bonusLife</c>, so scaling only one of them would leave the bar and the real pool
    /// disagreeing.
    /// </para>
    /// </summary>
    private static void ApplyHealthMultiplier(Hero hero)
    {
        try
        {
            var key = ReferenceEquals(hero, _hero1) ? 1 : 2;

            if (!_pvpNaturalMaxLife.TryGetValue(key, out var naturalMax) || naturalMax <= 0)
            {
                // First sight of this hero in a match: its pool is whatever it brought in. Guard
                // against a hero whose maxLife has not been initialised yet.
                naturalMax = hero.maxLife;
                if (naturalMax <= 0)
                {
                    return;
                }

                _pvpNaturalMaxLife[key] = naturalMax;
            }

            var multiplier = PvpHealthMultiplier <= 0 ? 1 : PvpHealthMultiplier;
            var targetMax = naturalMax * multiplier;

            if (hero.maxLife == targetMax)
            {
                return;
            }

            // Use the game's own setter: `overrideMaxLife` rescales `life`, `curRally` and
            // `bonusLife` proportionally, which is exactly the intent here (a bigger pool, same
            // fraction filled). Assigning `maxLife` directly would leave the current life and the
            // rally at their old absolute values, so a hero at full health would suddenly read as
            // half dead — and the drawn bar is `maxLife + bonusLife`, so it would disagree with the
            // pool as well.
            hero.overrideMaxLife(targetMax);

            Log($"[PvP] P{key} life pool {naturalMax} -> {targetMax} at {multiplier}x " +
                $"(life {hero.life}/{hero.maxLife}, bonus {hero.bonusLife})");
        }
        catch (Exception ex)
        {
            LogWarn("[PvP] could not scale a hero's life pool: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Each hero's natural (pre-multiplier) pool, captured the first time the hero is seen in a
    /// match, keyed 1/2. Cleared per match so a new run starts from its own numbers.
    /// </summary>
    private static readonly Dictionary<int, int> _pvpNaturalMaxLife = [];

    /// <summary>
    /// Puts both heroes back on their captured start spots, falling back to the co-op placement
    /// (<see cref="PlaceNextTo"/>) when the spots were never captured, so a round can never start
    /// with a hero in the void.
    /// </summary>
    private static void RestoreToStartSpots(Hero hero1, Hero hero2)
    {
        if (_startCx1 >= 0 && _startCy1 >= 0)
        {
            SafePlace(hero1, _startCx1, _startCy1);
        }

        if (_startCx2 >= 0 && _startCy2 >= 0)
        {
            SafePlace(hero2, _startCx2, _startCy2);
        }
        else
        {
            PlaceNextTo(hero2, hero1);
        }
    }

    /// <summary>
    /// A reposition that never lets a bad cell take the run down: the game's own <c>setPosCase</c>
    /// with a mid-cell offset, wrapped, with the co-op placement as the fallback for hero two.
    /// </summary>
    private static void SafePlace(Hero hero, int cx, int cy)
    {
        try
        {
            hero.setPosCase(cx, cy, 0.5, 0.5);
            hero.lastStablePt = new CPoint(cx, cy);
            hero.cancelVelocities();
        }
        catch (Exception ex)
        {
            LogWarn($"[PvP] could not place at {cx},{cy}: {ex.Message}");
            if (ReferenceEquals(hero, _hero2) && _hero1 != null)
            {
                PlaceNextTo(hero, _hero1);
            }
        }
    }

    /// <summary>
    /// Captures the two start spots. The arena deliberately does <b>not</b> read the level's tile
    /// grid: no <c>getCell</c>-style query is exposed through the Haxe proxy, and guessing floor
    /// cells from room bounds is how a hero ends up inside geometry. Instead player one's
    /// <em>actual</em> standing cell is the reference, and player two's spot is that cell mirrored
    /// across the room's centre (so the separation is symmetric by construction), clamped inside the
    /// room and one cell higher so it drops onto the floor instead of clipping into it.
    /// </summary>
    private static void CaptureStartSpots()
    {
        var hero1 = _hero1;
        var level = hero1?._level;
        if (hero1 == null || level == null || hero1.cx < 0 || hero1.cy < 0)
        {
            return;
        }

        try
        {
            _startCx1 = hero1.cx;
            _startCy1 = hero1.cy;

            var room = level.map.getRoomAt(hero1.cx, hero1.cy);
            if (room == null || room.wid < 6)
            {
                // Too narrow to mirror across: keep player two on the co-op placement.
                _startCx2 = _startCy2 = -1;
                Log($"[PvP] room too narrow to split ({room?.wid ?? -1}); both start together");
                return;
            }

            var centre = room.x + room.wid / 2;
            var mirrored = (2 * centre) - hero1.cx;
            // Clamp inside the room so the mirror can never land past its wall.
            mirrored = System.Math.Clamp(mirrored, room.x + 1, room.x + room.wid - 2);
            // If the mirror collapses onto player one's own column, take the far wall instead.
            if (System.Math.Abs(mirrored - hero1.cx) < 3)
            {
                mirrored = hero1.cx <= centre ? room.x + room.wid - 2 : room.x + 1;
            }

            _startCx2 = mirrored;
            _startCy2 = System.Math.Max(room.y + 1, hero1.cy - 1);
            Log($"[PvP] start spots ({_startCx1},{_startCy1}) / ({_startCx2},{_startCy2})");
        }
        catch (Exception ex)
        {
            LogWarn("[PvP] could not capture the start spots: {Message}", ex.Message);
            _startCx2 = _startCy2 = -1;
        }
    }

    /// <summary>Score line for the panel and the menu summary.</summary>
    internal static string PvpScoreText => IsPvp ? $"{ScoreP1} : {ScoreP2}" : string.Empty;
}
