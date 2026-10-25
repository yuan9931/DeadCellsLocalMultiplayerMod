using dc.en;
using dc.libs.heaps;
using dc.pr;
using dc.tool;
using dc.ui;
using dc.ui.pause;

namespace DeadCellsLocalMultiplayerMod;

/// <summary>
/// One local player's input device. Keyboard is a single device, controllers are numbered 1..4.
/// </summary>
public readonly record struct InputBinding(bool IsKeyboard, int ControllerNumber)
{
    public static InputBinding Keyboard => new(true, 0);
    public static InputBinding Controller(int number) => new(false, number);

    public string Display => IsKeyboard ? "Keyboard" : $"Controller {ControllerNumber}";

    /// <summary>0 for the keyboard, 1..4 for controllers. Used to cycle through the device list.</summary>
    public int Slot => IsKeyboard ? 0 : ControllerNumber;

    public static InputBinding FromSlot(int slot) => slot <= 0 ? Keyboard : Controller(slot);
}

/// <summary>
/// Local two-player runtime: spawns a second hero, gives each player an isolated input
/// device and keeps both heroes on the same health, inventory and progression.
/// </summary>
internal static partial class LocalCoop
{
    internal static bool Enabled {
        get; private set;
    }

    internal static InputBinding Player1 {
        get; private set;
    } = InputBinding.Keyboard;

    internal static InputBinding Player2 {
        get; private set;
    } = InputBinding.Controller(1);

    /// <summary>
    /// Skin player two wears, or <c>null</c> to share player one's skin (the default). Kept as the
    /// game's own <c>dc.String</c> so it can be handed straight to <c>user.heroSkin</c> and
    /// <c>Hero.applySkin()</c> without converting anything.
    /// </summary>
    internal static dc.String? Player2Skin {
        get; private set;
    }

    /// <summary>
    /// Whether both heroes share one health pool (the default). When off, each hero has its own
    /// life; a hero whose life hits 0 is "down" (frozen and hidden) while a teammate is still
    /// alive, and the run only ends when every hero is down.
    /// </summary>
    internal static bool SharedHealth { get; private set; } = true;

    /// <summary>Whether the two heroes can damage each other (friendly fire). Off by default.</summary>
    internal static bool FriendlyFire { get; private set; }

    internal static void SetSharedHealth(bool value)
    {
        SharedHealth = value;
        SaveSettings();
        Log($"[LocalCoop] shared health -> {value}");
    }

    internal static void SetFriendlyFire(bool value)
    {
        FriendlyFire = value;
        SaveSettings();
        Log($"[LocalCoop] friendly fire -> {value}");
    }

    /// <summary>
    /// Whether both heroes share one <c>Inventory</c> instance (the default). Everything the bag holds
    /// — weapons, skills, the reserve backpack, the ring and the mutations — is then one shared set,
    /// and the per-frame convergence in <see cref="OnFrameUpdate"/> extends that to the two counters
    /// that live on the hero instead of the bag: the flask charges and the scroll tiers. Skill
    /// cooldowns converge too, so an item used by either player is on cooldown for both.
    /// With it off, player two carries its own bag: own weapons, skills, flask charges, scroll tiers,
    /// curse and infection. Blueprints, gold and cells are shared in either mode — gold and cells live
    /// on the <c>Hero</c>, not in the inventory, so splitting the backpack does not split the economy.
    /// </summary>
    internal static bool SharedEquipment { get; private set; } = true;

    internal static void SetSharedEquipment(bool value)
    {
        SharedEquipment = value;
        SaveSettings();
        ApplyEquipmentMode();
        Log($"[LocalCoop] shared equipment -> {value}");
    }

    /// <summary>
    /// A life count is never negative: life, its cap and the bonus cap all clamp to zero. Used on
    /// the shared-health copy, where a negative value from the game would otherwise be propagated
    /// onto the other hero and drawn as a bar that runs backwards.
    /// </summary>
    private static int ClampLife(int value) => value < 0 ? 0 : value;

    /// <summary>
    /// Clamps every life-related count on a hero to a sane range, in place. Run every frame for
    /// both heroes, in **both** health modes: player one's bar is drawn by the game from these
    /// fields, so a negative value is visible on it, and with independent health none of the
    /// shared-health copy runs — this is then the only guard.
    /// </summary>
    private static void ClampLifeCounts(Hero hero)
    {
        if (hero.life < 0)
        {
            // Zero, not a negative: the game treats 0 as "dead", and a negative would be drawn.
            hero.life = 0;
        }
        if (hero.maxLife < 0)
        {
            hero.maxLife = 0;
        }
        if (hero.bonusLife < 0)
        {
            hero.bonusLife = 0;
        }
        if (hero.curRally < 0)
        {
            hero.curRally = 0;
        }
    }

    /// <summary>The same skin as a plain id, for labels and logs.</summary>
    internal static string? Player2SkinId => Player2Skin?.ToString();

    private static List<dc.String>? _skinIds;

    /// <summary>Set by <see cref="ModEntry"/> so the runtime can use the mod's logger.</summary>
    internal static Action<string>? Info;

    internal static Action<string, object?[]>? Warn;

    private static bool _installed;

    private static Game? _game;

    private static Hero? _hero1;

    private static Hero? _hero2;

    /// <summary>
    /// The hero that owns the detached head whose code is currently running, or null outside of
    /// <see cref="RunAsOwningHero"/>. Used to keep that head's effects on its own hero.
    /// </summary>
    private static Hero? _homunculusOwner;

    /// <summary>Heroes whose head is currently out, tracked from the head's own lifecycle.</summary>
    private static readonly List<Hero> HeadOutHeroes = [];

    private const string HeadPopLoopGroup = "headPopLoop";

    /// <summary>Class id the level indexes <c>en.Homunculus</c> entities under.</summary>
    private const int HomunculusClid = 17969;

    /// <summary>
    /// Class id the level indexes player heroes under, i.e. the bucket that holds <c>en.Hero</c>
    /// (and so <c>en.hero.Beheaded</c>) entities. The game itself reads it this way.
    /// </summary>
    private const int HeroClid = 60929;

    /// <summary>Player one's hero, or null before the run spawns it.</summary>
    internal static Hero? Hero1 => _hero1;

    /// <summary>Player two's hero, or null before it is spawned.</summary>
    internal static Hero? Hero2 => _hero2;

    // (The boss-intro position sync that used to live in LocalCoop.Boss.cs is gone: with the manual
    // teleport key, dragging a hero into the arena automatically is exactly what the player does
    // themselves now. Nothing else ever moved a hero between the door and the arena.)

    private static bool _pendingSpawn;

    private static Level? _pendingLevel;

    private static Level? _pendingOldLevel;

    private static int _pendingFrames;

    private static int _readyFrames;

    private static Controller? _globalController;

    private static Controller? _controller1;

    private static Controller? _controller2;

    private static GamePad? _pad1;

    private static GamePad? _pad2;

    private static double _pinLogTimer;

    private static double _padRefreshTimer;

    private static readonly List<GamePad> Pads = [];

    private static bool _mirroringEquip;

    private static double _diagTimer;

    private static double _headLogTimer;

    private static double _repairTimer;

    private static int _sharedLife = -1;

    private static double _chargeTimer1;

    private static double _chargeTimer2;

    /// <summary>
    /// Seconds since each hero's "is it charging a skill" sample was last taken — see
    /// <see cref="WatchStuckSkills"/>. The watchdog looks for a charge that never ends, so sampling
    /// it a few times a second is indistinguishable from checking every frame, and it keeps
    /// <c>Hero.isChargingSkill()</c> (which walks both skill managers) off the per-frame path.
    /// </summary>
    private static double _chargeSample1;

    private static double _chargeSample2;

    /// <summary>
    /// Per hero: the level / cell its room-revealed flag was last written for, so the per-frame pass
    /// only calls <c>LevelMap.revealRoomAt</c> when the answer can have changed (see RevealRoomFor).
    /// </summary>
    private static readonly dc.pr.Level?[] _revealLevel = new dc.pr.Level?[2];
    private static readonly int[] _revealCx = { int.MinValue, int.MinValue };
    private static readonly int[] _revealCy = { int.MinValue, int.MinValue };

    private static double _entityTimer;

    /// <summary>
    /// Whether the mod prints anything. Logging is **off by default**, so a normal run is silent,
    /// and it is switched on at runtime by setting the <c>DEADCELLSLOCALMULTIPLAYERMOD_LOG</c> environment
    /// variable (any value except "0") before the game starts. One build then serves both playing
    /// and diagnosing.
    /// </summary>
    internal static bool Logging { get; } = ReadLoggingFlag() || ReadVerboseFlag();

    /// <summary>
    /// Whether the mod also prints its <b>diagnostics</b>: the periodic state dumps and the
    /// per-event traces of behaviour that already works (a push contact, a re-pinned controller,
    /// a replaced drop, a homunculus detach, an equipment sync, ...). Those traces are what found
    /// the bugs, so they stay in the build, but they are off unless asked for — set
    /// <c>DEADCELLSLOCALMULTIPLAYERMOD_LOG=2</c> (or <c>verbose</c> / <c>all</c>), or put anything
    /// but "0" in <c>DEADCELLSLOCALMULTIPLAYERMOD_LOG_VERBOSE</c>. Milestones, rare repairs and
    /// every warning print at the normal level.
    /// </summary>
    internal static bool Verbose { get; } = ReadVerboseFlag();

    private static bool ReadLoggingFlag()
    {
        try
        {
            var value = System.Environment.GetEnvironmentVariable("DEADCELLSLOCALMULTIPLAYERMOD_LOG");
            return !string.IsNullOrEmpty(value) && value != "0";
        }
        catch
        {
            return false;
        }
    }

    private static bool ReadVerboseFlag()
    {
        try
        {
            var value = System.Environment.GetEnvironmentVariable("DEADCELLSLOCALMULTIPLAYERMOD_LOG")?.Trim().ToLowerInvariant();
            if (value is "2" or "verbose" or "all" or "debug" or "v")
            {
                return true;
            }

            var extra = System.Environment.GetEnvironmentVariable("DEADCELLSLOCALMULTIPLAYERMOD_LOG_VERBOSE");
            return !string.IsNullOrEmpty(extra) && extra != "0";
        }
        catch
        {
            return false;
        }
    }

    internal static void Log(string message)
    {
        if (Logging)
        {
            Info?.Invoke(message);
        }
    }

    /// <summary>
    /// Diagnostics only (see <see cref="Verbose"/>). Use it for anything that reports behaviour
    /// that already works; keep <see cref="Log"/> for milestones, setting changes and rare repairs,
    /// and <see cref="LogWarn"/> for everything that went wrong or had to be worked around.
    /// </summary>
    internal static void LogVerbose(string message)
    {
        if (Verbose)
        {
            Info?.Invoke(message);
        }
    }

    internal static void LogWarn(string message, params object?[] args)
    {
        if (Logging)
        {
            Warn?.Invoke(message, args);
        }
    }

    internal static void Activate(InputBinding player1, InputBinding player2)
    {
        Player1 = player1;
        Player2 = player2;
        Enabled = true;
        SaveSettings();
        Log($"[LocalCoop] session active: P1={player1.Display}, P2={player2.Display}");
    }

    internal static void Deactivate()
    {
        Enabled = false;
        ResetRunState();
        // A session that is switched off has no run to keep down: drop the persisted markers so the
        // next session does not start with a hero hidden at 1 HP.
        ClearPersistedDownState();
        SaveSettings();
        Log("[LocalCoop] session disabled, vanilla single player restored");
    }

    /// <summary>
    /// Installs every hook once. Handlers call the original game code untouched while the
    /// session is disabled, so a failed co-op attempt never breaks the vanilla game.
    /// </summary>
    internal static void Install()
    {
        if (_installed)
        {
            return;
        }
        _installed = true;

        try
        {
            var location = System.Reflection.Assembly.GetExecutingAssembly().Location;
            LogVerbose($"[LocalCoop] assembly {location}, built {System.IO.File.GetLastWriteTime(location):yyyy-MM-dd HH:mm:ss}");
        }
        catch
        {
            // Diagnostics only.
        }

        Hook_Game.initHero += OnInitHero;

        // PvP: swap the training level's generator for the arena while a match is armed. A no-op
        // outside a match (the hook falls through to the game's own factory).
        PvpArenaStruct.Install();

        // Diagnostic only: capture the exact level + room state when Level.update throws, so the
        // cell-door (sub-level) "Null access .rType" crash can be pinned down precisely. Inert in
        // normal play (only installed when DEADCELLSLOCALMULTIPLAYERMOD_LOG is on) and only logs on a crash.
        if (Logging)
        {
            InstallLevelUpdateHook();
        }

        Hook_Hero.onDie += OnHeroDie;
        // Independent health: prevent a lethal hit (rather than skip the death flow after it starts)
        // so the game's death sequence — and its audio fade/mute — never runs.
        Hook_Hero.tryToPreventDeath += OnHeroTryToPreventDeath;
        Hook_Hero.canBeHitBy += OnHeroCanBeHitBy;
        Hook_Hero.updateLifeBar += OnHeroUpdateLifeBar;
        InstallEntityHooks();
        InstallWalkPush();
        InstallDropHooks();
        Hook_Hero.kill += OnHeroKill;
        // One hero on a teleporter pad drags the partner along (restored by request).
        Hook_Hero.onTeleportation += OnHeroTeleportation;
        Hook_Hero.addCells += OnHeroAddCells;
        Hook_Hero.curse += OnHeroCurse;
        Hook_Hero.endCurse += OnHeroEndCurse;
        Hook_Hero.reduceCurse += OnHeroReduceCurse;
        Hook_Hero.addInfection += OnHeroAddInfection;
        Hook_Hero.removeInfection += OnHeroRemoveInfection;
        Hook_Hero.setInfection += OnHeroSetInfection;
        Hook_Hero.onEquipedItemsChange += OnEquipedItemsChange;
        Hook_Hero.setHeadMode += OnHeroSetHeadMode;
        Hook_Hero.getSkinInfo += OnHeroGetSkinInfo;

        // HUD-write gates (Coop/LocalCoop.P2HudGuard.cs): with independent health or equipment,
        // player two's events must stop repainting player one's HUD. These mirror
        // OnHeroUpdateLifeBar, which already does it for the life bar.
        Hook_Hero.heal += OnHeroHeal;
        Hook_Hero.fullHeal += OnHeroFullHeal;
        Hook_Hero.refreshInfectionHud += OnHeroRefreshInfectionHud;
        Hook_Hero.updateInfection += OnHeroUpdateInfection;
        // pickItem is registered by InstallDropHooks (Coop/LocalCoop.Drops.cs): that hook also
        // remembers the hero that acted, so the two jobs share one registration.
        Hook_Hero.setBrutalityTier += OnHeroSetBrutalityTier;
        Hook_Hero.setTacticTier += OnHeroSetTacticTier;
        Hook_Hero.setSurvivalTier += OnHeroSetSurvivalTier;

        // The flask counter itself, in case a code path writes the shared HUD without going through
        // Hero.heal: with independent equipment a write carrying player two's count is replaced by
        // player one's (see OnHudSetHealings).
        Hook_HUD.setHealings += OnHudSetHealings;

        // Player two's own HUD widgets (Coop/LocalCoop.P2HudClone.cs): a second Flow with its own
        // life bar, tier bar, flask and weapon/skill slots. The hooks below feed it from the game's
        // own write paths — the slot build, the per-slot closure binding and the ammo/fade
        // repaints — so player two's gear and life stop landing on player one's HUD at all.
        InstallP2HudCloneHooks();

        // Pause / inventory windows (Coop/LocalCoop.InventoryWindow.cs + LocalCoop.InventoryPicker.cs):
        // both windows are global singletons that read Game.ME.hero, so they always show player one's
        // gear. The pause screen is dc.ui.pause.DefaultPause (which also lists weapons, skills, perks
        // and the backpack); dc.ui.DebugPopUp is the item-swap popup. These hooks re-point that global
        // at the hero the player picks (via the picker overlay) for the window's own update, then put
        // it back, and restore the default when the window closes.
        Hook_DefaultPause.update += OnPauseUpdate;
        Hook_DefaultPause.postUpdate += OnPausePostUpdate;
        Hook_DefaultPause.onDispose += OnPauseDispose;
        Hook_Game.pause += OnGamePause;
        Hook_DebugPopUp.update += OnInventoryWindowUpdate;
        Hook_DebugPopUp.close += OnInventoryWindowClose;
        Hook_DebugPopUp.onDispose += OnInventoryWindowDispose;

        // A scroll's promotion screen (dc.ui.TierSelect, the same screen a level-up uses) is built for
        // one hero, so the other player was never asked for their scroll point. See
        // Coop/LocalCoop.TierSelect.cs.
        Hook_TierSelect.validate += OnTierSelectValidate;
        Hook_TierSelect.onDispose += OnTierSelectOnDispose;
        Hook_TierSelect.update += OnTierSelectUpdate;

        Hook_Controller.onAnyPadEnabled += OnAnyPadEnabled;
        Hook_Controller.onAnyPadDisabled += OnAnyPadDisabled;

        // Every entry point that can reach hero state goes through RunAsOwningHero: the head
        // resolves the body it belongs to through the global Game.ME.hero, and the call sites are
        // spread over far more methods than just the per-frame ones.
        Hook_Homunculus.init += OnHomunculusInit;
        Hook_Homunculus.onRelocate += OnHomunculusOnRelocate;
        Hook_Homunculus.comeBack += OnHomunculusComeBack;
        Hook_Homunculus.detachFromHero += OnHomunculusDetachFromHero;
        Hook_Homunculus.controlsToHero += OnHomunculusControlsToHero;
        Hook_Homunculus.controlsToMe += OnHomunculusControlsToMe;
        Hook_Homunculus.dash += OnHomunculusDash;
        Hook_Homunculus.onLand += OnHomunculusOnLand;
        Hook_Homunculus.startClimbing += OnHomunculusStartClimbing;
        Hook_Homunculus.stopClimbing += OnHomunculusStopClimbing;
        Hook_Homunculus.releaseItem += OnHomunculusReleaseItem;
        Hook_Homunculus.updateFocus += OnHomunculusUpdateFocus;
        Hook_Homunculus.initGfx += OnHomunculusInitGfx;
        Hook_Homunculus.preUpdate += OnHomunculusPreUpdate;
        Hook_Homunculus.fixedUpdate += OnHomunculusFixedUpdate;
        Hook_Homunculus.postUpdate += OnHomunculusPostUpdate;
        Hook_Homunculus.onDie += OnHomunculusOnDie;
        Hook_Homunculus.dispose += OnHomunculusDispose;

        dc.tool.mainSkills.Hook_Homunculus.onStart += OnHomunculusSkillStart;
        dc.tool.mainSkills.Hook_Homunculus.prepare += OnHomunculusPrepare;
        dc.tool.mainSkills.Hook_Homunculus.isUsingInputs += OnHomunculusIsUsingInputs;
        dc.tool.mainSkills.Hook_Homunculus.onOwnerDamaged += OnHomunculusOnOwnerDamaged;
        dc.tool.mainSkills.Hook_Homunculus.onEnd += OnHomunculusSkillEnd;

        LogVerbose("[LocalCoop] hooks installed");
    }

    /// <summary>Runs one setup step, reporting a failure without skipping the steps after it.</summary>
    private static void RunGuarded(string what, Action body)
    {
        try
        {
            body();
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not {What}: {Message}", what, ex.Message);
        }
    }

    internal static void OnFrameUpdate(double dt)
    {
        // Pads are tracked even before the session starts: the setup menu lists them by name, and
        // a pad streamed in by Steam Remote Play Together only exists while the stream runs.
        RefreshPads(dt);

        if (!Enabled)
        {
            return;
        }

        // A run restored from a save never calls Game.initHero(), so the hook the mod normally
        // learns a run from never fires. Adopt the heroes the level already has before anything
        // else looks at them.
        //
        // Inlined rather than run through RunGuarded: that helper takes an Action, so the call
        // allocated a closure on every single frame just to be thrown away.
        var game = Game.Class.ME;
        if (game != null)
        {
            try
            {
                AdoptExistingHeroes(game);
            }
            catch (Exception ex)
            {
                LogWarn("[LocalCoop] could not adopt the running level's heroes: {Message}", ex.Message);
            }
        }

        TrySyncHeroes();

        var hero1 = _hero1;
        var hero2 = _hero2;
        if (hero1 == null || hero2 == null)
        {
            // Self-heal: a setup step that threw (a save-loaded hero refusing applySkin, say) left
            // the controllers uncreated, and nothing else ever retried. Both heroes then sat on
            // the global controller — so neither pad moved the right hero — and the shared camera
            // had nothing to follow. Only worth doing once there is somebody to bind.
            if (hero1 != null || hero2 != null)
            {
                EnsureControllers();
            }

            return;
        }

        if (hero1.destroyed || hero2.destroyed)
        {
            // Safety net: the run already ended before the die hook could react.
            //
            // Player one alive means it is NOT the end of the run: a level transition can destroy
            // hero two underneath the mod (the forge levels tear the old level down before
            // `initHero` runs, so there is nothing to move). Keep the bag it was carrying so the
            // respawn — AdoptExistingHeroes re-arms the pending spawn on the next frame — hands it
            // back instead of leaving player two with the copy of player one's gear a freshly built
            // hero starts with.
            var hero2Only = !hero1.destroyed;
            if (hero2Only)
            {
                RememberHero2Bag(hero2, hero1);
                Log("[LocalCoop] hero two was destroyed by the level change; keeping its bag for the respawn");
            }

            RemoveSecondHero(hero1.destroyed ? "player one is gone" : "hero two is gone", keepHero2State: hero2Only);
            return;
        }

        try
        {
            // Remember hero two's own life so a level-transition re-spawn can restore it: a fresh
            // Hero inherits the global hero's (P1's) life, which is how P2 used to come back at
            // P1's HP after a transition (see SpawnSecondHero).
            _savedHero2Life = hero2.life;
            _savedHero2MaxLife = hero2.maxLife;
            _savedHero2BonusLife = hero2.bonusLife;
            // The flask charges live on the Heal main skill, not in the bag, and a re-created
            // hero's board seeds from the run-global `game.data.healings` — so they need the same
            // remembering the life does.
            var (flaskCur, _) = ReadFlaskCharges(hero2);
            _savedHero2Flask = flaskCur;
            _hasSavedHero2State = true;

            // Independent health: keep downed heroes down. The game can clear isFreeze / show a
            // hero on its own (e.g. an equipment sync touching the downed hero), which would
            // wrongly revive it; re-assert before anything else this frame.
            ReassertDownState();

            // Friendly fire: the two heroes share teamHero, so by default each other's attacks
            // never even consider the partner as a target. Add/remove teamHero from its own
            // opposing teams to make the two heroes candidate targets of each other when the
            // setting is on. Teams are rebuilt per level, so the pass runs on a level change and
            // then only as a slow safety net — see SyncFriendlyFireTeams.
            SyncFriendlyFireTeams(dt);

            // PvP: the match countdowns and the arena-opening check. Before the health sync so a
            // round that just ended is already reflected in what the panel and the gates read.
            TickPvp(dt);

            // Diagnostic only (logging-gated): the 3s periodic line never lands on the frame where
            // the two heroes walk into each other, which is the only state where the engine's
            // bump weight factor differs. Print the exact contact frame while it lasts.
            LogPushContact();

            // The per-frame rules read the PvP-aware predicates: a match forces independent health and
            // friendly fire, so nothing below may look at the raw menu settings.
            // Shared health: converge both heroes onto one life pool. Health is shared, but
            // copying player one onto hero two every frame silently erased damage that landed on
            // hero two, so propagate whichever hero lost health and let healing raise both. When
            // health is independent (SharedHealth off) each hero keeps its own life/maxLife and
            // none of this runs.
            if (!PvpHealthIndependent)
            {
                SyncSharedLife(hero1, hero2);
                // Life, its cap and the bonus cap are counts: a negative one is meaningless and
                // would draw a bar that runs backwards (the panel divides by maxLife, and a
                // negative bonus shows as a negative-length segment). Clamp on the way in.
                hero2.maxLife = ClampLife(hero1.maxLife);
                hero2.bonusLife = ClampLife(hero1.bonusLife);
                // Rally belongs to whoever took the hit. Converge on the larger value so a hit on
                // player two is not erased by copying player one's (possibly zero) value over it.
                var sharedRally = System.Math.Max(hero1.curRally, hero2.curRally);
                hero1.curRally = sharedRally;
                hero2.curRally = sharedRally;
            }

            // Life, its cap, the bonus cap and the rally are counts: a negative one is meaningless
            // and shows on player one's bar as a number below zero (the game draws hero one's bar
            // itself, so the value has to be right on the hero). Clamp every frame, in both health
            // modes — with independent health none of the copy above runs, so this is the only
            // guard those heroes get.
            ClampLifeCounts(hero1);
            ClampLifeCounts(hero2);

            // Shared health only: the curse is one shared pair mirrored on every curse/endCurse
            // event (see OnHeroCurse in LocalCoop.Hooks.cs), so a mismatch means a direct game
            // write dropped one side — lift it back up while both heroes are on their feet.
            // Independent health: the curse is per-hero (taken, consumed and lifted per hero), so
            // the counters legitimately differ and this sync must not run at all.
            if (CurseShared && hero2.curseCounter != hero1.curseCounter
                && !IsDown(hero1) && !IsDown(hero2))
            {
                if (hero2.curseCounter == 0 && hero1.curseCounter > 0)
                {
                    hero2.curseCounter = hero1.curseCounter;
                }
                else if (hero1.curseCounter == 0 && hero2.curseCounter > 0)
                {
                    hero1.curseCounter = hero2.curseCounter;
                }
            }

            // Everything else the shared backpack implies, converged onto one shared set: the flask
            // charges (owned by each hero's Heal skill, not the bag), the scroll tiers (derived from
            // the bag, but only recomputed for the hero that picked the scroll up) and the item
            // cooldowns (one timer per hero). All three return immediately when gear is independent.
            SyncSharedFlask(hero1, hero2);
            SyncSharedTiers(hero1, hero2);
            SyncSkillCooldowns(hero1, hero2);

            // While equipment is shared, the backpack must stay one shared instance. Sharing it
            // once at spawn is not enough: if the game ever swaps player one's inventory (a
            // reload, a level change) hero two silently keeps the old one and the two backpacks
            // drift apart. Re-assert it every frame, the same way the input parents are
            // re-asserted. With independent equipment hero two keeps its own — the same re-assert
            // would silently merge the two backpacks back into one.
            if (SharedEquipment && !ReferenceEquals(hero2.inventory, hero1.inventory))
            {
                hero2.inventory = hero1.inventory;
                _hero2OwnInventory = null;
                Log("[LocalCoop] hero two inventory was replaced, re-shared player one's");
            }
            // Blueprints are run-wide progress, never gear: shared in both modes.
            if (!ReferenceEquals(hero2.blueprints, hero1.blueprints))
            {
                hero2.blueprints = hero1.blueprints;
            }

            // The level only refreshes onScreenRecent for entities in its quad-tree, so without
            // this the second hero is treated as off-screen and its sprite is hidden after ~2s.
            hero2.onScreenRecent = hero2.frameCountThresholdForOutOfGame;

            // A downed hero sits at the death spot while the other player runs away; once it is
            // off-screen the level would mark it out-of-game and skip its update — and skipping the
            // *global* hero's update is what silences the audio. Keep it "on screen recent" so it
            // keeps updating (it is invisible anyway).
            if (IsDown(hero1))
            {
                hero1.onScreenRecent = hero1.frameCountThresholdForOutOfGame;
            }

            // Each hero's head must follow its own body. HeroHead.init() binds the head to the
            // global hero (player one), so re-point both heads every frame — this also re-fixes
            // hero two after the level transitions that re-run initGfx().
            BindHeroHead(hero1);
            BindHeroHead(hero2);

            // The waiting-for-head crouch is global; keep it on the owner only.
            ApplyHeadPopLoopPriority(hero1, 0);
            ApplyHeadPopLoopPriority(hero2, 1);

            // Manual teleport: L3 on that player's pad (or T on the keyboard) moves the pressing
            // hero onto the other one. Nothing is carried automatically — doors, force fields and
            // boss cinematics are all left to the game, and the player decides when to regroup.
            // (The two automatic regrouping mechanisms this used to sit next to — the level-entry
            // follow glue and the boss-intro position sync — were removed: the button is the only
            // way the pair gets back together.)
            // Skipped in PvP: regrouping on demand is exactly what a duel must not allow (it would
            // defeat the mirrored start spots and let one player close the gap whenever they like).
            if (!IsPvp)
            {
                WatchTeleportKey(hero1, hero2, dt);
            }

            // The "hold T / L3 to teleport" hint above each hero: armed on every level change for
            // 5s, and swapped to the "hold to go back" wording for 10s after that hero teleports.
            // PvP has no manual teleport, so it shows no hint.
            TickTeleportHints(dt);
            if (!IsPvp)
            {
                UpdateTeleportHints(hero1, hero2);
            }
            else
            {
                ClearTeleportHints();
            }

            RepairSecondHero(hero1, hero2, dt);

            // Map discovery: the game only reveals the room of the hero it tracks, but while the
            // shared camera drives the minimap it nulls followEntity, so neither hero is tracked.
            // Write the Room.revealed data flag under BOTH heroes so the Explorer's Instinct and the
            // other room-state reads are correct for either player. This is deliberately NOT the
            // fog of war: the fog is the MapFow texture and only MiniMap.revealRoom() punches holes
            // in it, which is what SharedCamera.RevealFogFor does.
            //
            // Only when the hero actually changed cell (or level): the call resolves the room at a
            // cell and sets a flag that stays set, so repeating it every frame for a standing hero
            // is pure interop — two map lookups per frame for an answer that cannot have changed.
            // A hero that moves within one room still triggers it (cell changed) and the flag is
            // already set there, which is the cheap `isRoomRevealedAt` early-out inside the game.
            RevealRoomFor(hero1, 0);
            RevealRoomFor(hero2, 1);

            MirrorGating(hero1, hero2);
            // Walking into each other shoves the partner (see Coop/LocalCoop.Push.cs): the engine's own
            // repelling is disabled for the hero pair, so the shove is applied here — in both
            // directions.
            ApplyWalkPush(hero1, hero2);
            BindMainSkills(dt);
            CleanOrphanSkillEntities(hero2, dt);
            WatchStuckSkills(hero1, dt, ref _chargeTimer1, ref _chargeSample1, "P1");
            WatchStuckSkills(hero2, dt, ref _chargeTimer2, ref _chargeSample2, "P2");
            WatchStuckAir(hero1, dt, "P1", 0);
            WatchStuckAir(hero2, dt, "P2", 1);
            UpdateHeroPanels(dt);
            if (IsPvp)
            {
                PvpHud.Update();
            }

            WatchInventoryPicker();
            LogDiagnostics(dt, hero2);
            LogHeads(dt, hero1, hero2);
            _pinLogTimer = System.Math.Max(0, _pinLogTimer - dt);
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] per-frame sync failed: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Writes the <c>Room.revealed</c> data flag for the room a hero is standing in — but only when
    /// that hero changed cell (or level) since the last write.
    /// <para>
    /// The flag stays set once written and the game's own <c>revealRoomAt</c> early-outs on an
    /// already-revealed room, so calling it for a standing hero every frame was two map lookups per
    /// hero per frame for an answer that cannot change. This is a data flag only (Explorer's
    /// Instinct and other room-state reads); the visible fog is the <c>MapFow</c> texture, which
    /// <c>SharedCamera.RevealFogFor</c> punches.
    /// </para>
    /// <para>
    /// The cache is keyed on the level object too, so a new level (whose flags are all cleared)
    /// reveals immediately even if the hero enters on the very cell it left the old one at.
    /// </para>
    /// </summary>
    private static void RevealRoomFor(Hero? hero, int index)
    {
        if (hero == null || hero.destroyed)
        {
            return;
        }

        try
        {
            var level = hero._level;
            var cx = hero.cx;
            var cy = hero.cy;
            if (level == null || cx < 0 || cy < 0 || IsDown(hero))
            {
                return;
            }

            if (ReferenceEquals(level, _revealLevel[index]) && cx == _revealCx[index] && cy == _revealCy[index])
            {
                return;
            }

            level.map.revealRoomAt(cx, cy);

            // Only remember after the call: a transient level state that threw must be retried, not
            // recorded as done.
            _revealLevel[index] = level;
            _revealCx[index] = cx;
            _revealCy[index] = cy;
        }
        catch
        {
            // Best-effort: the next cell change (or the next frame) tries again.
        }
    }

    /// <summary>
    /// Whether the player picked player two's skin in the menu. Until then the mod defaults it to
    /// something other than player one's skin, so the two heroes are distinguishable at a glance.
    /// </summary>
    private static bool _player2SkinChosen;

    /// <summary>
    /// Drops every run reference. <paramref name="keepHero2State"/> is set when the teardown is only
    /// player two's hero going away while the run continues (a level transition destroyed it): the
    /// bag it carried and its own life then survive, to be handed back to the hero that replaces it.
    /// </summary>
    private static void ResetRunState(bool keepHero2State = false)
    {
        // The snapshot is deliberately NOT dropped here. This runs whenever the heroes go away, and
        // quitting to the main menu destroys them — so clearing it here threw away exactly the gear a
        // following load was supposed to restore (observed as `bytes=0` on restore while the capture
        // itself had logged 744 bytes). The per-frame capture in OnFrameUpdate keeps it current, and
        // it is cleared only when the session itself ends (Deactivate) or the mode changes to shared.

        _hero1 = null;
        _hero2 = null;
        // The teleport hints belong to the heroes and the HUD that hosted them, both of which are
        // going away here. Dropping the armed level as well means the next run's first level shows
        // the hint again rather than being mistaken for the one already announced.
        ClearTeleportHints();
        // A match belongs to the run it was played in, so tearing the heroes down drops it — with
        // one exception: while Starting, this teardown is the *previous* run being destroyed underneath
        // a match that was just armed (a rematch calls StartPvpSession, which arms the state and then
        // launches a new run, and the old heroes' destruction lands in between). Ending there would
        // disarm the match before its arena ever generated.
        if (Pvp != PvpState.Starting)
        {
            EndPvpSession("run torn down");
        }
        // The fog of war texture belongs to the level, and the next run builds a brand new one, so
        // forget which rooms we already punched (see SharedCamera.RevealFogFor).
        SharedCamera.ForgetRevealedRooms();
        HeadOutHeroes.Clear();
        _controller1 = null;
        _controller2 = null;
        _pad1 = null;
        _pad2 = null;
        _pinLogTimer = 0;
        _pendingSpawn = false;
        _readyFrames = 0;
        _pendingFrames = 0;
        _repairTimer = 0;
        ResetTeleportState();
        ResetTierSelectState();
        _chargeTimer1 = 0;
        _chargeTimer2 = 0;
        _chargeSample1 = 0;
        _chargeSample2 = 0;
        // The sampled passes run on their first frame after a teardown instead of waiting out their
        // interval: a new hero's bag / skills / animation state is exactly what they must fix.
        _bindMainSkillsTimer = BindMainSkillsSeconds;
        _friendlyFireTimer = 0;
        _friendlyFireLevel = null;
        _airAnimName[0] = "(none)";
        _airAnimName[1] = "(none)";
        _airAnimSample[0] = AirAnimSampleSeconds;
        _airAnimSample[1] = AirAnimSampleSeconds;
        _revealLevel[0] = null;
        _revealLevel[1] = null;
        _revealCx[0] = int.MinValue;
        _revealCx[1] = int.MinValue;
        _revealCy[0] = int.MinValue;
        _revealCy[1] = int.MinValue;
        _entityTimer = 0;
        _airStuckTimer[0] = 0;
        _airStuckTimer[1] = 0;
        _airFlagLogTimer[0] = 0;
        _airFlagLogTimer[1] = 0;
        _airFell[0] = false;
        _airFell[1] = false;
        _airWatching[0] = false;
        _airWatching[1] = false;
        _airWasCarrying[0] = false;
        _airWasCarrying[1] = false;
        _padRefreshTimer = 0;
        _diagTimer = 0;
        _headLogTimer = 0;
        _mirroringEquip = false;
        // Hero two's own inventory: kept only when this teardown is *hero two alone* going away
        // mid-run (a level transition destroyed it while player one lives on — the forge levels do
        // that), because the respawn that follows hands it straight back (see RestoreOwnInventory).
        // Everywhere else the reference is dropped: the clone is a Haxe object that belonged to the
        // old run, and keeping it across a real teardown and handing it to a rebuilt hero produced a
        // corpse inventory (`nbWeapons`/`nbActives` read 99999) — see GiveOwnInventory.
        if (!keepHero2State)
        {
            _hero2OwnInventory = null;
        }
        _hero2FreshlySpawned = false;
        _sharedLife = -1;
        _sharedFlask = -1;
        // Traps track their live instances through init/destroy hooks; drop them on a run reset so a
        // teardown that skipped a destroy cannot leave a corpse platform in the list.
        ForgetTrapInstances();
        ForgetPlantInstances();
        ForgetMobAiState();
        ResetDownState();
        ResetHeroPanels();
        // Same rule as the bag above: player two's own life is kept when only its hero is going away
        // mid-run, so the hero that replaces it does not come back at player one's HP (the fresh
        // hero inherits the global hero's life — see SpawnSecondHero).
        if (!keepHero2State)
        {
            _hasSavedHero2State = false;
            _savedHero2Life = 0;
            _savedHero2Flask = -1;
            _savedHero2MaxLife = 0;
            _savedHero2BonusLife = 0;
        }
        _inventoryWindow = null;
        _pauseWindow = null;
        _inventoryWindowHero = null;
    }
}
