using dc;
using dc.en;
using dc.h2d;
using dc.hl.types;
using dc.hxd;
using dc.libs.heaps;
using dc.pr;
using dc.tool;

namespace DeadCellsLocalMultiplayerMod;

/// <summary>
/// Part of <see cref="LocalCoop"/>: per-player controllers, pad discovery and gating.
/// </summary>
internal static partial class LocalCoop
{
    /// <summary>Cycles player one to the next device. Both players may hold the same device.</summary>
    internal static void CyclePlayer1()
    {
        Player1 = NextDevice(Player1);
        SaveSettings();
    }

    /// <summary>Cycles player two to the next device. Both players may hold the same device.</summary>
    internal static void CyclePlayer2()
    {
        Player2 = NextDevice(Player2);
        SaveSettings();
    }

    private static InputBinding NextDevice(InputBinding current)
    {
        // Devices are 0 = keyboard, 1..4 = controller, and the cycle may land on the device the
        // other player already picked: two pads on one controller is a legitimate way to play (both
        // heroes then share that stick), and the isolation is per Controller object, not per device.
        return InputBinding.FromSlot((current.Slot + 1) % 5);
    }

    /// <summary>
    /// True on the frame the menu "back" action is pressed — gamepad B / circle, or Esc. Action 16
    /// is the game's back/cancel action (the title control label calls it "Retour"). The button is
    /// read from the global controller's bindings so a remapped back button still works, and
    /// <see cref="Controller.padIsPressed"/> walks every active pad when the global is unpinned,
    /// so either player can go back. Used by the setup menu's back handling (see
    /// <see cref="ModEntry"/>).
    /// </summary>
    internal static bool IsMenuBackPressed()
    {
        var controller = Boot.Class.ME?.controller;
        if (controller == null)
        {
            return false;
        }

        var bindings = controller.useCustomBindings ? controller.customBindings : controller.normalBindings;
        if (bindings == null)
        {
            return false;
        }

        const int backAct = 16;
        // Gamepad B / circle: padA is the primary binding, padB/padC the alternates.
        if (IsPadBindingPressed(controller, bindings.padA, backAct)) return true;
        if (IsPadBindingPressed(controller, bindings.padB, backAct)) return true;
        if (IsPadBindingPressed(controller, bindings.padC, backAct)) return true;

        // Keyboard (Esc): primary/secondary/third, only while the controller takes keyboard input.
        if ((controller.mode & Controller.Class.ENABLE_KEY) != 0)
        {
            if (IsKeyBindingPressed(bindings.primary, backAct)) return true;
            if (IsKeyBindingPressed(bindings.secondary, backAct)) return true;
            if (IsKeyBindingPressed(bindings.third, backAct)) return true;
        }

        return false;
    }

    private static bool IsPadBindingPressed(Controller controller, ArrayBytes_Int binding, int act)
    {
        var button = ReadBindingButton(binding, act);
        return button >= 0 && controller.padIsPressed(button);
    }

    private static bool IsKeyBindingPressed(ArrayBytes_Int binding, int act)
    {
        var key = ReadBindingButton(binding, act);
        return key >= 0 && Key.Class.isPressed.Invoke(key);
    }

    private static int ReadBindingButton(ArrayBytes_Int binding, int act)
    {
        if (binding == null || act < 0 || act >= binding.length)
        {
            return -1;
        }
        return System.Runtime.InteropServices.Marshal.ReadInt32(binding.bytes, act * 4);
    }

    /// <summary>
    /// Creates the per-player controllers and binds the heroes to them, unless that already
    /// happened. Safe to call every frame: it is the self-heal for a setup step that failed once,
    /// which is what left "continue from a save" with dead input and no shared camera. The
    /// per-frame re-assertion of the parents lives in <see cref="MirrorGating"/>, so this only
    /// has to do anything the first time.
    /// </summary>
    private static void EnsureControllers()
    {
        if (!Enabled || (_controller1 != null && _controller2 != null))
        {
            return;
        }

        try
        {
            if (_controller1 == null || _controller2 == null)
            {
                CreatePlayerControllers();
            }

            if (_controller1 != null || _controller2 != null)
            {
                ApplyControllers();
            }
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not set up the controllers: {Message}", ex.Message);
        }
    }

    private static void CreatePlayerControllers()
    {
        var boot = Boot.Class.ME;
        if (boot == null)
        {
            LogWarn("[LocalCoop] Boot instance is unavailable, input isolation skipped");
            return;
        }

        var global = boot.controller;
        if (global == null)
        {
            LogWarn("[LocalCoop] global controller is unavailable, input isolation skipped");
            return;
        }

        _globalController = global;
        CollectPads(global, true);

        _controller1 = CreatePlayerController(global, boot.s2d, Player1, 1);
        _controller2 = CreatePlayerController(global, boot.s2d, Player2, 2);
    }

    private static Controller CreatePlayerController(Controller global, Scene scene, InputBinding binding, int player)
    {
        var controller = new Controller(scene);

        // Reuse the player's own bindings instead of the defaults of a fresh controller.
        controller.normalBindings = global.normalBindings;
        controller.customBindings = global.customBindings;
        controller.useCustomBindings = global.useCustomBindings;
        controller.triggerDeadzone = global.triggerDeadzone;

        GamePad? pad = null;
        if (binding.IsKeyboard)
        {
            // Keyboard only: padIsDown() checks ENABLE_PAD, so pads can no longer drive this hero.
            controller.mode = Controller.Class.ENABLE_KEY;
            controller.gc = null!;
        }
        else
        {
            pad = FindPad(binding.ControllerNumber);
            controller.mode = Controller.Class.ENABLE_PAD;
            controller.gc = pad!;
            if (pad == null)
            {
                LogWarn("[LocalCoop] Player {Player} asked for {Device} but that pad is not connected",
                    player, binding.Display);
            }
        }

        // NOTE: do not try to build a per-player ArrayObj here. `new ArrayObj()` followed by
        // push() crashes the game with 0xC0000005 inside ArrayObj.push. Isolation is done by
        // mode + gc instead, plus re-asserting the parents every frame.
        controller.allGamepads = global.allGamepads;
        controller.activeGamepads = global.activeGamepads;

        // Remember the pad so it can be re-pinned every frame (see EnforceInput).
        if (player == 1)
        {
            _pad1 = pad;
        }
        else
        {
            _pad2 = pad;
        }

        return controller;
    }

    private static void ApplyControllers()
    {
        if (_hero1?.controller != null && _controller1 != null)
        {
            _hero1.controller.parent = _controller1;
        }
        var hero2 = _hero2;
        if (hero2?.controller != null && _controller2 != null)
        {
            hero2.controller.parent = _controller2;
        }

        Log($"[LocalCoop] Input isolated: P1={Player1.Display}, P2={Player2.Display}");
    }

    private static GamePad? FindPad(int number)
    {
        // Connection order comes first. The setup menu talks about "Pad 1..4", and a Steam
        // virtual pad (Remote Play Together) reports an arbitrary device index — a guest pad
        // often shows up as index 2 or higher — so index matching can only be a fallback.
        var slot = number - 1;
        if (slot >= 0 && slot < Pads.Count)
        {
            return Pads[slot];
        }

        foreach (var pad in Pads)
        {
            var device = pad.device;
            if (device != null && device.index == slot)
            {
                return pad;
            }
        }
        return null;
    }

    /// <summary>Any pad that the other player is not already using.</summary>
    private static GamePad? FirstFreePad(GamePad? taken)
    {
        foreach (var pad in Pads)
        {
            if (!ReferenceEquals(pad, taken))
            {
                return pad;
            }
        }
        return null;
    }

    /// <summary>Device description for the setup menu, including the real pad name.</summary>
    internal static string DescribeDevice(InputBinding binding)
    {
        if (binding.IsKeyboard)
        {
            return "Keyboard";
        }

        var pad = FindPad(binding.ControllerNumber);
        return pad == null
            ? $"Pad {binding.ControllerNumber} (not connected)"
            : $"Pad {binding.ControllerNumber} {DescribePad(pad)}";
    }

    /// <summary>One line listing every detected controller, for the setup menu.</summary>
    internal static string DeviceSummary()
    {
        return Pads.Count == 0
            ? "No controller detected - plug one in, or start Steam Remote Play Together."
            : $"{Pads.Count} controller(s): {string.Join(", ", Pads.Select(DescribePad))}";
    }

    private static void CollectPads(Controller global, bool verbose)
    {
        var pads = global.allGamepads;
        if (pads == null)
        {
            return;
        }

        var before = Pads.Count;
        for (var i = 0; i < pads.length; i++)
        {
            try
            {
                object? element = pads.getDyn(i);
                if (element is GamePad pad)
                {
                    var device = pad.device;
                    if (device == null || !device.connected)
                    {
                        // The pool has a fixed set of slots; most of them have no pad attached.
                        continue;
                    }
                    if (!Pads.Any(p => ReferenceEquals(p, pad)))
                    {
                        Pads.Add(pad);
                        LogVerbose($"[LocalCoop] game pad detected: {DescribePad(pad)}");
                    }
                }
                else if (verbose)
                {
                    LogVerbose($"[LocalCoop] game pad slot {i} held {element?.GetType().Name ?? "null"}");
                }
            }
            catch (Exception ex)
            {
                LogWarn("[LocalCoop] could not read game pad slot {Index}: {Message}", i, ex.Message);
            }
        }

        // Steam's virtual pads (Remote Play Together) come and go while the game runs, so forget
        // the ones that went away instead of keeping a player pinned to a dead device.
        for (var i = Pads.Count - 1; i >= 0; i--)
        {
            var device = Pads[i].device;
            if (device == null || !device.connected)
            {
                LogVerbose($"[LocalCoop] game pad disconnected: {DescribePad(Pads[i])}");
                Pads.RemoveAt(i);
            }
        }

        if (verbose || Pads.Count != before)
        {
            Log($"[LocalCoop] {Pads.Count} game pad(s) ready");
        }
    }

    /// <summary>Pad name and index, so a Steam virtual pad can be told from a real one.</summary>
    private static string DescribePad(GamePad? pad)
    {
        var device = pad?.device;
        if (device == null)
        {
            return "no pad";
        }

        try
        {
            return $"\"{device.get_name()}\" index {device.index}";
        }
        catch
        {
            return $"index {device.index}";
        }
    }

    /// <summary>
    /// Re-scans the pad pool every two seconds. A pad that is plugged in — or streamed in by
    /// Steam Remote Play Together — after the session started is picked up here; the per-frame
    /// pin in MirrorGating then binds the player to it automatically.
    /// </summary>
    private static void RefreshPads(double dt)
    {
        _padRefreshTimer += dt;
        if (_padRefreshTimer < 2)
        {
            return;
        }
        _padRefreshTimer = 0;

        // Also works on the title screen, where _globalController is not set yet: the setup menu
        // lists the pads, and Steam's streamed-in pad only exists while the stream runs.
        var global = _globalController ?? Boot.Class.ME?.controller;
        if (global == null)
        {
            return;
        }

        try
        {
            CollectPads(global, false);
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not refresh the game pad list: {Message}", ex.Message);
        }
    }

    private static void OnAnyPadEnabled(Hook_Controller.orig_onAnyPadEnabled orig, Controller self, GamePad pad)
    {
        orig(self, pad);

        if (pad != null && !Pads.Any(p => ReferenceEquals(p, pad)))
        {
            Pads.Add(pad);
        }
    }

    private static void OnAnyPadDisabled(Hook_Controller.orig_onAnyPadDisabled orig, Controller self, GamePad pad)
    {
        orig(self, pad);

        if (pad == null)
        {
            return;
        }
        for (var i = Pads.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(Pads[i], pad))
            {
                Pads.RemoveAt(i);
            }
        }
    }

    private static void MirrorGating(Hero? hero1, Hero? hero2)
    {
        var global = _globalController;
        if (global == null)
        {
            return;
        }

        Enforce(_controller1, Player1, ref _pad1, global, "P1", IsDown(hero1));
        Enforce(_controller2, Player2, ref _pad2, global, "P2", IsDown(hero2));

        // The heroes no longer feed the global controller — their input now goes through the
        // per-player controllers — so the global never flips to pad mode on its own and stays on
        // the keyboard. Everything the game parents to it (Game.controller, the UI manager: map,
        // inventory, pause) then ignores the pad entirely. The UI is *shared*, so it must answer
        // the keyboard and either player's pad: mode is a bitmask (the game itself ORs
        // ENABLE_PAD | ENABLE_KEY) and Controller.padIsPressed() walks activeGamepads when gc is
        // null, which is exactly what lets both pads drive the one UI.
        MirrorGlobalDevice(global, _controller1, _controller2, Player1, Player2);

        // The game can rebuild a hero's ControllerAccess (level transitions, pause menus,
        // pad confirmations), which silently snaps it back to the global controller and
        // puts one stick on both heroes again. Re-assert the parents every frame.
        Reassign(hero1, _controller1, "P1");
        Reassign(hero2, _controller2, "P2");

        static void Enforce(Controller? target, InputBinding binding, ref GamePad? pad, Controller global, string who, bool down)
        {
            if (target == null)
            {
                return;
            }

            // Independent health: a downed hero must not read any input. Movement is skipped by
            // isFreeze + the fixedUpdate hook, but turn / jump / roll / attack are driven by the
            // controller itself — outside update() and fixedUpdate() — so the only reliable way to
            // stop them is to park the controller (mode 0, no pad). The shared map / bag / pause UI
            // is driven by the *global* controller, which still walks every active pad, so the
            // downed player can still open it (see MirrorGlobalDevice).
            if (down)
            {
                if (target.mode != 0)
                {
                    target.mode = 0;
                    PinLog($"{who} is down, input parked");
                }
                if (target.gc != null)
                {
                    target.gc = null!;
                }
                target.isLocked = global.isLocked;
                target.exclusiveId = global.exclusiveId;
                target.suspendTimer = global.suspendTimer;
                return;
            }

            // The vanilla game flips a controller into pad mode as soon as any pad button
            // (e.g. A) is pressed. Both player controllers share the pad pool with the
            // global one, so one stick then drives both heroes. Pin mode and gc back to
            // the player's own device every frame.
            if (binding.IsKeyboard)
            {
                if (target.mode != Controller.Class.ENABLE_KEY)
                {
                    target.mode = Controller.Class.ENABLE_KEY;
                    PinLog($"{who} controller mode was flipped to pad, re-pinned to keyboard");
                }
                if (target.gc != null)
                {
                    target.gc = null!;
                    PinLog($"{who} keyboard controller was given a pad, removed it");
                }
            }
            else
            {
                // Re-resolve whenever the bound pad went away (Steam's virtual pads come and go)
                // or when "Controller N" now points at a different device.
                var resolved = FindPad(binding.ControllerNumber);
                if (resolved == null && pad == null)
                {
                    // Nothing connected for this slot yet and the other player is on the keyboard:
                    // take whatever pad shows up, so a guest pad that reports an unexpected index
                    // (Steam Remote Play) still works. The pick sticks until that pad goes away,
                    // and it never steals from another pad player.
                    var otherIsKeyboard = who == "P1" ? Player2.IsKeyboard : Player1.IsKeyboard;
                    resolved = otherIsKeyboard ? FirstFreePad(null) : null;
                    if (resolved != null)
                    {
                        LogVerbose($"[LocalCoop] {who} picked up {DescribePad(resolved)} automatically");
                    }
                }

                if (pad?.device?.connected != true || (resolved != null && !ReferenceEquals(resolved, pad)))
                {
                    if (!ReferenceEquals(pad, resolved))
                    {
                        PinLog($"{who} now uses {DescribePad(resolved)}");
                    }
                    pad = resolved;
                }

                if (pad != null)
                {
                    if (target.mode != Controller.Class.ENABLE_PAD)
                    {
                        target.mode = Controller.Class.ENABLE_PAD;
                        PinLog($"{who} controller mode was flipped, re-pinned to pad");
                    }
                    if (!ReferenceEquals(target.gc, pad))
                    {
                        target.gc = pad;
                        PinLog($"{who} controller pad was changed, re-pinned");
                    }
                }
                else
                {
                    // No pad for this player right now: leave the controller inert instead of
                    // pinning ENABLE_PAD with a null pad, which the game would dereference.
                    if (target.mode != 0)
                    {
                        target.mode = 0;
                        PinLog($"{who} has no {binding.Display} connected yet, input parked");
                    }
                    if (target.gc != null)
                    {
                        target.gc = null!;
                    }
                }
            }

            target.isLocked = global.isLocked;
            target.exclusiveId = global.exclusiveId;
            target.suspendTimer = global.suspendTimer;
        }

        static void MirrorGlobalDevice(
            Controller global,
            Controller? player1,
            Controller? player2,
            InputBinding binding1,
            InputBinding binding2)
        {
            var wantKey = binding1.IsKeyboard || binding2.IsKeyboard;
            // Base the pad bit on the *bindings*, not the live per-player controllers: a downed
            // hero's controller is parked (gc cleared), but its pad must still drive the shared
            // map / inventory / pause UI through the global controller.
            var wantPad = !binding1.IsKeyboard || !binding2.IsKeyboard;

            var mode = 0;
            if (wantKey)
            {
                mode |= Controller.Class.ENABLE_KEY;
            }

            if (wantPad)
            {
                mode |= Controller.Class.ENABLE_PAD;
            }

            if (mode == 0)
            {
                return;
            }

            if (global.mode != mode)
            {
                global.mode = mode;
                PinLog($"global controller mode -> key:{wantKey} pad:{wantPad}");
            }

            // A pinned gc makes the controller read that single pad only; clearing it makes it walk
            // every active pad, so either player can open the shared map / inventory / pause UI.
            // (Every use of gc in Controller is null-guarded.)
            if (global.gc != null)
            {
                global.gc = null!;
                PinLog("global controller unpinned from one pad (UI answers both players)");
            }
        }

        static void Reassign(Hero? hero, Controller? controller, string who)
        {
            if (hero?.controller == null || controller == null)
            {
                return;
            }
            if (!ReferenceEquals(hero.controller.parent, controller))
            {
                hero.controller.parent = controller;
                LogVerbose($"[LocalCoop] {who} controller access was reset, re-bound to its own device");
            }
        }
    }

    /// <summary>The game can flip a pinned value every frame while a pad button is held,
    /// so re-pin logs are throttled to once every two seconds.</summary>
    private static void PinLog(string message)
    {
        if (!Verbose)
        {
            return;
        }

        if (_pinLogTimer > 0)
        {
            return;
        }
        _pinLogTimer = 2;
        Log($"[LocalCoop] {message}");
    }
}
