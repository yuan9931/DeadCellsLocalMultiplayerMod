using System.Reflection;
using dc.hxd;
using dc.hxd.res;
using dc.pr;
using HaxeProxy.Runtime;
using ModCore.Events.Interfaces.Game;
using ModCore.Modules;
using ModCore.Mods;
using ModCore.Utilities;

namespace DeadCellsLocalMultiplayerMod;

public sealed class ModEntry : ModBase, IOnGameEndInit, IOnFrameUpdate
{
    private static ModEntry? _instance;
    private static bool _hookInstalled;
    private static TitleScreen? _screen;

    // Which page of the mod's own setup menu is showing, plus the stack of pages visited so the
    // gamepad back button (B / circle) / Esc can pop one level exactly like the vanilla "Back"
    // entries do.
    private enum MenuState { None, Root, Settings, Devices, Camera, Rules, Pvp, PvpRules, Summary }
    private static MenuState _menuState = MenuState.None;
    private static readonly Stack<MenuState> _menuStack = new();

    // Tracks the first selectable (button) entry while a page is built, so it can be
    // default-selected once the page is up. Info lines are not selectable, so "first" means the
    // first button, not the first row.
    private static int _firstSelectable;
    private static int _menuIndex;

    // Switched by the same runtime flag as LocalCoop's logging (see LocalCoop.Logging).
    private static void LogInfo(string message)
    {
        if (LocalCoop.Logging)
        {
            _instance?.Logger.Information(message);
        }
    }

    /// <summary>Diagnostics only — see <see cref="LocalCoop.Verbose"/>.</summary>
    private static void LogVerbose(string message)
    {
        if (LocalCoop.Verbose)
        {
            _instance?.Logger.Information(message);
        }
    }

    private static void LogWarn(string message, params object?[] args)
    {
        if (LocalCoop.Logging)
        {
            _instance?.Logger.Warning(message, args);
        }
    }

    public ModEntry(ModInfo info) : base(info)
    {
        _instance = this;
        new LocalCoopUILang(this);
    }

    public void OnGameEndInit()
    {
        LocalCoop.Info = message => Logger.Information(message);
        LocalCoop.Warn = (message, args) => Logger.Warning(message, args);
        LocalCoop.Install();
        LocalCoop.LoadSettings();
        LogInfo($"[DeadCellsLocalMultiplayerMod] v{BuildInfo.Version}{(LocalCoop.Verbose ? " (verbose log)" : string.Empty)}");

        SharedCamera.Info = message => Logger.Information(message);
        SharedCamera.Warn = (message, args) => Logger.Warning(message, args);
        SharedCamera.Install();

        if (_hookInstalled)
        {
            return;
        }

        Hook_TitleScreen.mainMenu += HookMainMenu;
        _hookInstalled = true;
        LogVerbose("[DeadCellsLocalMultiplayerMod] Title menu hook installed");
    }

    public void OnFrameUpdate(double dt)
    {
        // Verbose only: how much of a frame the mod itself costs, so "the frame rate dropped" can be
        // told apart from "the mod is the reason". Both halves are measured because they are the
        // mod's entire per-frame entry points; the per-hook work (mob AI wrappers, traps) is inside
        // the game's own calls and shows up as frame time, not here.
        var measuring = LocalCoop.Verbose;
        var started = measuring ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;

        TryMenuBack();
        LocalCoop.OnFrameUpdate(dt);
        SharedCamera.OnFrameUpdate(dt);

        if (measuring)
        {
            LocalCoop.RecordModFrameCost(started);
        }
    }

    /// <summary>
    /// Gamepad back button (B / circle) / Esc pops one level of the mod's setup menu, retreating
    /// through the recorded menu stack: a sub-page returns to the root, and the root (or the ready
    /// summary) returns to the title. The vanilla title screen ignores the back action at the main
    /// menu (curMenu stays 0), and the mod's pages never change curMenu, so without this the button
    /// does nothing inside the mod's menu.
    /// </summary>
    private static void TryMenuBack()
    {
        if (_menuState == MenuState.None)
        {
            return;
        }

        try
        {
            if (LocalCoop.IsMenuBackPressed())
            {
                GoBack();
            }
        }
        catch (Exception ex)
        {
            LogWarn("[DeadCellsLocalMultiplayerMod] menu back failed: {Message}", ex.Message);
        }
    }

    private static string T(string key) => GetText.Instance.GetString(key).ToString();

    /// <summary>
    /// Player two's skin label for the menu, localized. <see cref="LocalCoop.Player2SkinLabel"/>
    /// itself stays English because it also goes into the log, where the raw id / state is what is
    /// useful; the two fixed states are translated here.
    /// </summary>
    private static string SkinLabelLocalized()
    {
        var label = LocalCoop.Player2SkinLabel;
        return label switch
        {
            "auto (differs from P1)" => T("auto (differs from P1)"),
            "same as P1" => T("same as P1"),
            _ => label, // a real skin id — the game's own identifier, shown as-is
        };
    }

    private static void HookMainMenu(Hook_TitleScreen.orig_mainMenu orig, TitleScreen self)
    {
        orig(self);
        _screen = self;
        _menuState = MenuState.None;
        _menuStack.Clear();
        // The title menu is up, so the game's skin data is loaded — resolve a persisted player-two
        // skin that could not be resolved at boot (SetPlayer2SkinById left it pending).
        LocalCoop.TryResolvePendingSkin();
        AddMainMenuEntry(self);
    }

    private static void AddMainMenuEntry(TitleScreen screen)
    {
        try
        {
            var label = T("Local Co-op").AsHaxeString();
            var help = LocalCoop.Enabled
                ? T("Two heroes, one screen - session active").AsHaxeString()
                : T("Play together on this computer").AsHaxeString();
            var colorValue = 0x59D5FF;
            var color = Ref<int>.From(ref colorValue);
            screen.addMenu(label, new HlAction(() => ShowMenu(screen, MenuState.Root, push: true)), help, null, color);
        }
        catch (Exception ex)
        {
            // The title screen is rebuilt by the game. If its menu API changes, leave vanilla usable.
            LogWarn("[DeadCellsLocalMultiplayerMod] Could not add title menu entry: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Rebuilds the given page and, when <paramref name="push"/> is set, records it on the menu
    /// stack (forward navigation). Rebuilds after an in-place change (cycling a device, toggling
    /// the camera) pass <c>push: false</c> so the stack is not duplicated. Every page
    /// default-selects its first button afterwards.
    /// </summary>
    private static void ShowMenu(TitleScreen screen, MenuState state, bool push, bool preserveCursor = false)
    {
        _screen = screen;
        _menuState = state;
        // For an in-place change (cycling a value on the same page) keep the cursor on the button
        // that was clicked instead of jumping back to the first entry.
        var savedCursor = preserveCursor ? ReadCursor(screen) : -1;
        _firstSelectable = -1;
        _menuIndex = 0;
        screen.clearMenu();
        BuildMenu(screen, state);

        if (push)
        {
            _menuStack.Push(state);
        }

        if (savedCursor >= 0)
        {
            TrySelectIndex(screen, savedCursor);
        }
        else
        {
            TrySelectFirst(screen);
        }
    }

    private static int ReadCursor(TitleScreen screen)
    {
        try
        {
            return screen.curMenu;
        }
        catch
        {
            return -1;
        }
    }

    private static void TrySelectIndex(TitleScreen screen, int index)
    {
        try
        {
            var count = screen.menuItems?.length ?? 0;
            if (count <= 0)
            {
                return;
            }
            if (index < 0)
            {
                index = 0;
            }
            else if (index >= count)
            {
                index = count - 1;
            }
            screen.select(index, Ref<bool>.DontCare);
        }
        catch
        {
        }
    }

    /// <summary>
    /// The setup menu is split into short pages. The title menu clips anything past the bottom of
    /// the window, so every page keeps at most five entries.
    /// </summary>
    private static void BuildMenu(TitleScreen screen, MenuState state)
    {
        switch (state)
        {
            case MenuState.Devices:
                AddInfo(screen, T("LOCAL CO-OP / INPUT DEVICES"), 0xF7FC65);
                AddInfo(screen, T("Both players may pick the same device."), 0xB7BEC9);
                AddButton(screen, string.Format(T("Player 1: {0}   < Change >"), LocalCoop.DescribeDevice(LocalCoop.Player1)), () =>
                {
                    LocalCoop.CyclePlayer1();
                    ShowMenu(screen, MenuState.Devices, push: false, preserveCursor: true);
                }, T("Player one's input device."));
                AddButton(screen, string.Format(T("Player 2: {0}   < Change >"), LocalCoop.DescribeDevice(LocalCoop.Player2)), () =>
                {
                    LocalCoop.CyclePlayer2();
                    ShowMenu(screen, MenuState.Devices, push: false, preserveCursor: true);
                }, LocalCoop.DeviceSummary());
                AddButton(screen, T("Back"), () => GoBack());
                break;

            case MenuState.Camera:
                AddInfo(screen, T("LOCAL CO-OP / CAMERA"), 0xF7FC65);
                var camToggle = SharedCamera.Enabled ? T("both heroes (zoom out)") : T("player 1 only");
                AddButton(screen, string.Format(T("Camera: {0}   < Toggle >"), camToggle), () =>
                {
                    SharedCamera.Enabled = !SharedCamera.Enabled;
                    LocalCoop.SaveSettings();
                    ShowMenu(screen, MenuState.Camera, push: false, preserveCursor: true);
                }, T("Follow both heroes and zoom the one window so both stay visible, or follow player 1 only."));
                AddButton(screen, string.Format(T("Zoom out limit: {0}   < Change >"), SharedCamera.MinZoom.ToString("P0")), () =>
                {
                    SharedCamera.CycleMinZoom();
                    ShowMenu(screen, MenuState.Camera, push: false, preserveCursor: true);
                }, T("How far the camera may zoom out to keep both heroes on screen."));
                AddButton(screen, T("Back"), () => GoBack());
                break;

            case MenuState.Rules:
                AddInfo(screen, T("LOCAL CO-OP / RULES"), 0xF7FC65);
                var healthToggle = LocalCoop.SharedHealth ? T("shared") : T("independent");
                AddButton(screen, string.Format(T("Shared health: {0}   < Toggle >"), healthToggle), () =>
                {
                    LocalCoop.SetSharedHealth(!LocalCoop.SharedHealth);
                    ShowMenu(screen, MenuState.Rules, push: false, preserveCursor: true);
                }, T("Share one health pool between both heroes, or give each hero its own life. Independent: a downed hero stays down (run continues) and revives at 1 HP on the next level; the run ends only when all heroes are down."));
                // Equipment mode. Shared (the default) is one backpack for both heroes, and the flask
                // charges, scroll tiers and skill cooldowns converge onto it too, so everything the
                // gear does is one shared set. Independent gives each hero its own bag.
                var equipToggle = LocalCoop.SharedEquipment ? T("shared") : T("independent");
                AddButton(screen, string.Format(T("Equipment: {0}   < Toggle >"), equipToggle), () =>
                {
                    LocalCoop.SetSharedEquipment(!LocalCoop.SharedEquipment);
                    ShowMenu(screen, MenuState.Rules, push: false, preserveCursor: true);
                }, T("Share one backpack between both heroes, or give each hero its own weapons, skills, scroll tiers and flask. Shared: the flask, the tiers, the ring, the mutations and the item cooldowns are one shared set. Independent: gold, cells and unlocks stay shared."));
                var ffToggle = LocalCoop.FriendlyFire ? T("on") : T("off");
                AddButton(screen, string.Format(T("Friendly fire: {0}   < Toggle >"), ffToggle), () =>
                {
                    LocalCoop.SetFriendlyFire(!LocalCoop.FriendlyFire);
                    ShowMenu(screen, MenuState.Rules, push: false, preserveCursor: true);
                }, T("Let the two heroes damage each other. In a PvP match this is forced on."));
                AddButton(screen, T("Back"), () => GoBack());
                break;

            case MenuState.Pvp:
                AddInfo(screen, T("PVP ARENA"), 0xF7FC65);
                var pvpToggle = LocalCoop.PvpEnabled ? T("on") : T("off");
                AddButton(screen, string.Format(T("PvP mode: {0}   < Toggle >"), pvpToggle), () =>
                {
                    LocalCoop.SetPvpEnabled(!LocalCoop.PvpEnabled);
                    ShowMenu(screen, MenuState.Pvp, push: false, preserveCursor: true);
                }, T("Off: the two heroes cooperate as usual. On: the arena is played, where each hero has its own health and they always damage each other."));
                // The match rules live on their own page: the win target and the health multiplier are
                // both values the player cycles, and adding them here would push this page past the
                // five entries the title menu can show.
                AddButton(screen, T("Match rules"), () => ShowMenu(screen, MenuState.PvpRules, push: true),
                    T("How long a match lasts, and how much health each hero has."));
                // One action row that both starts a match and reports the previous one.
                var startLabel = LocalCoop.IsMatchOver
                    ? string.Format(T("Start a match   (last: P{0} {1})"), LocalCoop.MatchWinner, LocalCoop.PvpScoreText)
                    : T("Start a match now");
                AddButton(screen, startLabel, () =>
                {
                    // Starting a match launches a fresh training-mode run, so this leaves the title
                    // screen. StartPvpSession arms the mode and the co-op session itself, so the
                    // button works even when the player never touched the toggle above.
                    LocalCoop.StartPvpSession();
                }, T("Opens the arena: both players fight in an empty training hall — no mobs, no loot, no exit. Leave with the pause menu; the score panel sits in the top-left corner. Gear follows the Rules setting."));
                AddButton(screen, T("Back"), () => GoBack());
                break;

            case MenuState.PvpRules:
                AddInfo(screen, T("PVP ARENA / MATCH RULES"), 0xF7FC65);
                AddButton(screen, string.Format(T("Rounds to win: {0}   < Change >"), LocalCoop.RoundsToWin), () =>
                {
                    LocalCoop.CycleRoundsToWin();
                    ShowMenu(screen, MenuState.PvpRules, push: false, preserveCursor: true);
                }, T("How many rounds a player must take to win the match."));
                AddButton(screen, string.Format(T("Health: {0}   < Change >"), LocalCoop.PvpHealthMultiplierLabel), () =>
                {
                    LocalCoop.CyclePvpHealthMultiplier();
                    ShowMenu(screen, MenuState.PvpRules, push: false, preserveCursor: true);
                }, T("How much health each hero has in a match: the normal pool, or 2x / 5x / 10x it, so a round lasts longer. Applies to both heroes, so the duel stays even."));
                AddInfo(screen, string.Format(T("Both heroes enter with {0} their normal life pool."),
                    LocalCoop.PvpHealthMultiplierLabel), 0xB7BEC9);
                AddButton(screen, T("Back"), () => GoBack());
                break;

            case MenuState.Summary:
                AddInfo(screen, T("LOCAL CO-OP READY"), 0xF7FC65);
                AddInfo(screen, string.Format(T("P1: {0}   P2: {1}"), LocalCoop.DescribeDevice(LocalCoop.Player1), LocalCoop.DescribeDevice(LocalCoop.Player2)), 0xFFFFFF);
                var camDesc = SharedCamera.Enabled
                    ? string.Format(T("Camera follows both heroes and zooms out to {0}"), SharedCamera.MinZoom.ToString("P0"))
                    : T("Camera follows player 1");
                AddInfo(screen, string.Format(T("Camera: {0} | Health: {1}"), camDesc,
                    LocalCoop.SharedHealth ? T("shared") : T("independent")), 0xFFFFFF);
                AddInfo(screen, string.Format(T("Equipment: {0} | Friendly fire: {1}"),
                    LocalCoop.SharedEquipment ? T("shared") : T("independent"),
                    LocalCoop.FriendlyFire ? T("on") : T("off")), 0xFFFFFF);
                AddButton(screen, T("Input devices"), () => ShowMenu(screen, MenuState.Devices, push: true),
                    T("Start a run from the main menu to play together. Either hero dying ends the run."));
                AddButton(screen, T("Back to title"), () => ReturnToTitle(screen));
                break;

            case MenuState.Settings:
                // Version folded into the header so the page keeps five rows; "Mod info" no longer has
                // a page of its own (with PvP on the root's first row, both pages sit at the limit).
                AddInfo(screen, string.Format(T("MOD SETTINGS   v{0}"), BuildInfo.Version), 0xF7FC65);
                AddButton(screen, T("Camera"), () => ShowMenu(screen, MenuState.Camera, push: true),
                    T("One window for both heroes: follow both and zoom out, or follow player 1 only."));
                AddButton(screen, T("Rules"), () => ShowMenu(screen, MenuState.Rules, push: true),
                    T("Shared health, equipment and friendly fire."));
                AddButton(screen, string.Format(T("Player 2 skin: {0}   < Change >"), SkinLabelLocalized()), () =>
                {
                    LocalCoop.CyclePlayer2Skin();
                    ShowMenu(screen, MenuState.Settings, push: false, preserveCursor: true);
                }, T("Player two's outfit. Defaults to a skin different from player one's; cycles every wearable skin in the game, then 'same as P1'. Takes effect on the next run."));
                AddButton(screen, T("Back"), () => GoBack());
                break;

            default:
                // No title line: the first row is PvP, the second is still the co-op session switch.
                AddButton(screen, T("PvP arena"), () => ShowMenu(screen, MenuState.Pvp, push: true),
                    T("Two players, one arena: own health, forced friendly fire, first to the round target wins."));
                AddButton(screen, LocalCoop.Enabled ? T("Disable local co-op") : T("Start local co-op"), () =>
                {
                    if (LocalCoop.Enabled)
                    {
                        LocalCoop.Deactivate();
                        ShowMenu(screen, MenuState.Root, push: false, preserveCursor: true);
                    }
                    else
                    {
                        Activate(screen);
                    }
                }, LocalCoop.Enabled
                    ? T("Turn the session off; new runs are single-player again.")
                    : T("Start a run from the main menu to play together."));
                AddButton(screen, T("Mod settings"), () => ShowMenu(screen, MenuState.Settings, push: true),
                    T("Camera and the run rules: shared health, equipment, friendly fire, player two's skin."));
                AddButton(screen, T("Input devices"), () => ShowMenu(screen, MenuState.Devices, push: true),
                    LocalCoop.Enabled
                        ? T("Session active: every new run uses two heroes.")
                        : T("Two heroes share health, inventory and attributes. Pick a device for each player."));
                AddButton(screen, T("Back"), () => GoBack());
                break;
        }
    }

    private static void Activate(TitleScreen screen)
    {
        LocalCoop.Activate(LocalCoop.Player1, LocalCoop.Player2);
        ShowMenu(screen, MenuState.Summary, push: true);
    }

    /// <summary>
    /// Pops the menu stack one level: a sub-page returns to the page below it, and the root returns
    /// to the title. Plays the vanilla menu-back sound. Called by the back button (B / circle),
    /// Esc, and the in-menu "Back" entries.
    /// </summary>
    private static void GoBack()
    {
        var screen = _screen;
        if (screen == null)
        {
            return;
        }

        PlayBackSound();

        if (_menuStack.Count > 1)
        {
            _menuStack.Pop();
            ShowMenu(screen, _menuStack.Peek(), push: false);
        }
        else
        {
            ReturnToTitle(screen);
        }
    }

    /// <summary>Plays the vanilla menu-back sound; a load failure must never block going back.</summary>
    private static void PlayBackSound()
    {
        try
        {
            var loader = Res.Class.get_loader.Invoke();
            var snd = (Sound)loader.loadCache("sfx/ui/menu_back.wav".AsHaxeString(), Sound.Class);
            dc.Audio.Class.ME.playUIEvent(snd, null);
        }
        catch
        {
        }
    }

    /// <summary>Default-selects the first selectable entry of the page just built.</summary>
    private static void TrySelectFirst(TitleScreen screen)
    {
        if (_firstSelectable < 0)
        {
            return;
        }

        try
        {
            screen.select(_firstSelectable, Ref<bool>.DontCare);
        }
        catch
        {
        }
    }

    private static void ReturnToTitle(TitleScreen screen)
    {
        _menuState = MenuState.None;
        _menuStack.Clear();
        try
        {
            SetIsMainMenu(screen, false);
            screen.clearMenu();
            screen.mainMenu();
        }
        catch
        {
            // The native main menu will be rebuilt on the next title-screen lifecycle callback.
        }
    }

    private static void SetIsMainMenu(TitleScreen screen, bool value)
    {
        try
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
            var type = screen.GetType();
            var property = type.GetProperty("isMainMenu", flags);
            if (property?.CanWrite == true)
            {
                property.SetValue(screen, value);
            }
            else
            {
                type.GetField("isMainMenu", flags)?.SetValue(screen, value);
            }
        }
        catch { }
    }

    private static void AddButton(TitleScreen screen, string label, Action onClick, string? help = null)
    {
        if (_firstSelectable < 0)
        {
            _firstSelectable = _menuIndex;
        }

        var colorValue = 0xFFFFFF;
        var color = Ref<int>.From(ref colorValue);
        screen.addMenu(label.AsHaxeString(), new HlAction(onClick), (help ?? string.Empty).AsHaxeString(), null, color);
        _menuIndex++;
    }

    private static void AddInfo(TitleScreen screen, string label, int colorValue)
    {
        var color = Ref<int>.From(ref colorValue);
        screen.addMenu(label.AsHaxeString(), new HlAction(() => { }), "".AsHaxeString(), false, color);
        _menuIndex++;
    }
}
