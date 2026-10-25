using dc.en;
using dc.h2d;
using dc.hxd;
using dc.libs.heaps;
using HaxeProxy.Runtime;
using ModCore.Utilities;

namespace DeadCellsLocalMultiplayerMod;

/// <summary>
/// Part of <see cref="LocalCoop"/>: the device-isolated hero picker shown while the pause /
/// inventory window is open.
/// <para>
/// The pause screen (<c>dc.ui.pause.DefaultPause</c>) and the item-swap popup
/// (<c>dc.ui.DebugPopUp</c>) are global singletons that read <c>Game.ME.hero</c>, so they always
/// render whoever that global points at — and the mod keeps the global on player one. There is no
/// field on either window saying who opened it (the open input arrives with no hero identity, and
/// keyboard state is global so the opener could not be recovered anyway). So instead of inferring,
/// the picker is an explicit, device-isolated choice: press a button on the device that belongs to
/// the hero you want to inspect.
/// </para>
/// <para>
/// P1's device selects player one, P2's device selects player two. Because each player's device is
/// known to the mod, this sidesteps the global-keyboard ambiguity that made "show whoever opened
/// it" impossible. Selecting calls <see cref="SelectInventoryWindowHero"/>, which re-points the
/// global for the window's next frame; the window already re-runs as that hero, so its contents
/// rebuild automatically.
/// </para>
/// <para>
/// <b>Text only, and only game-created objects.</b> An earlier version built its own
/// <c>dc.h2d.Object</c> plus a <c>Graphics</c> from C# and never appeared — objects constructed in
/// C# are not reliably registered with the Haxe heap (a C#-built <c>ArrayObj</c> crashes on
/// <c>push()</c> for the same reason). The labels here come from <c>dc.Assets.makeText()</c> and are
/// parented to the open window's own background, so they render and follow that window.
/// </para>
/// <para>
/// The picker key is distinct from the teleport key (T / L3) and from the menu back action (action
/// 16): player one uses Q on the keyboard or a click of the right stick (R3) on its pad, player two
/// the right-stick click on its own pad. It is edge-triggered so a held stick does not flip the
/// selection every frame.
/// </para>
/// </summary>
internal static partial class LocalCoop
{
    // SDL keycode for Q, as Key.isPressed() indexes it — the game's CODEMAP maps letters to
    // uppercase, which is why the teleport key is 84 ('T') and not 116.
    private const int KeyPicker = 81;

    // SDL controller button 8 (RIGHTSTICK, "R3"). hxd's Pad fills its buttons/values arrays as
    // SDL button + 6, so R3 lands on index 14 — distinct from L3 (index 13), which teleports.
    private const int R3ValueIndex = 14;

    /// <summary>The two picker lines: line 0 the header, line 1 the scrolling bag.</summary>
    private const int PickerLineCount = 2;
    private static readonly dc.ui.Text?[] _pickerLines = new dc.ui.Text?[PickerLineCount];

    /// <summary>The game-owned object the picker lines hang off (the open window's background).</summary>
    private static dc.h2d.Object? _pickerParent;

    private const double PickerScale = 0.85;
    private const double PickerX = 24.0;
    private const double PickerY = 20.0;
    private const double PickerStep = 20.0;
    private const int PickerColor = 0xF4F8FF;

    /// <summary>Index of the scrolling bag line: line 0 is the header, 1 the bag.</summary>
    private const int BagLineIndex = 1;

    /// <summary>
    /// Wrap budget for the picker lines. The pause window's text column is narrower than this, so a
    /// line that would have run off the background wraps instead of being clipped.
    /// </summary>
    private const double PickerMaxWidth = 420.0;

    // Edge detection for the picker press, per player device.
    private static bool _pickerHeld1;
    private static bool _pickerHeld2;

    /// <summary>
    /// Drives the picker while a pickable window is open: builds the overlay the first time,
    /// repaints it every frame, listens for a device press to switch heroes, and tears it down once
    /// the window is gone. Called every frame from <see cref="OnFrameUpdate"/>.
    /// </summary>
    private static void WatchInventoryPicker()
    {
        var parent = Enabled && _hero1 != null && _hero2 != null
            ? InventoryWindowOverlayParent()
            : null;

        if (parent == null)
        {
            RemovePickerOverlay();
            _pickerHeld1 = false;
            _pickerHeld2 = false;
            return;
        }

        EnsurePickerOverlay(parent);
        UpdatePickerOverlay();

        if (PressedPicker(Player1, _pad1, ref _pickerHeld1))
        {
            SelectInventoryWindowHero(_hero1!);
        }
        if (PressedPicker(Player2, _pad2, ref _pickerHeld2))
        {
            SelectInventoryWindowHero(_hero2!);
        }
    }

    /// <summary>Builds the picker labels once, parented to the window's own background.</summary>
    private static void EnsurePickerOverlay(dc.h2d.Object parent)
    {
        if (ReferenceEquals(_pickerParent, parent) && _pickerLines[0] != null)
        {
            return;
        }

        RemovePickerOverlay();
        _pickerParent = parent;

        for (var i = 0; i < PickerLineCount; i++)
        {
            var text = dc.Assets.Class.makeText(
                "".AsHaxeString(),
                dc.ui.Text.Class.COLORS.get("WO".AsHaxeString()),
                false,
                parent);
            text.textColor = PickerColor;
            text.customScale = PickerScale;
            text.onResize();
            text.x = PickerX;
            text.y = PickerY + i * PickerStep;

            // Constrain the informational lines so a long one wraps instead of running off the window.
            //
            // The BAG row is deliberately excluded: its length is already governed by the scroll
            // window, and adding a wrap width on top of that made it wrap onto a second line — which
            // is exactly what the scroll was meant to avoid.
            if (i != BagLineIndex)
            {
                try
                {
                    text.set_maxWidth(PickerMaxWidth);
                }
                catch
                {
                    // Text layer refused the constraint: leave it unconstrained.
                }
            }

            _pickerLines[i] = text;
        }

        LogPickerOnce(parent);
    }

    /// <summary>
    /// Repaints the selection header and the chosen hero's backpack line.
    /// <para>
    /// The gear and mutation text that used to sit here are gone on purpose: the pause window's own
    /// descriptions now follow the switch (the equipment through <c>ApplyPauseWindowHero</c>, the
    /// mutation bar through <c>RebuildPauseMutationBar</c>), so repeating them as text overlapped the
    /// window's icons for no gain. What remains is the line the window has no equivalent for — the
    /// reserve backpack, which is too long to list and scrolls.
    /// </para>
    /// </summary>
    private static void UpdatePickerOverlay()
    {
        var hero = ResolveInventoryWindowHero();
        var shown = ReferenceEquals(hero, _hero2) ? "P2" : "P1";

        SetPickerLine(0, $"< {shown} >   P1 device Q / R3      P2 pad R3");

        if (hero == null || hero.destroyed)
        {
            for (var i = 1; i < PickerLineCount; i++)
            {
                SetPickerLine(i, "");
            }
            return;
        }

        SetPickerLine(BagLineIndex, $"BAG {BackpackScrollingSummary(hero)}");
    }

    /// <summary>
    /// The reserve items as one line that scrolls itself, so a full bag can be read in a window too
    /// narrow to hold it all.
    /// <para>
    /// A window of fixed character width slides along the joined list and wraps around; the offset is
    /// driven by the clock, so it keeps moving while the pause menu is open (the game keeps ticking
    /// there — the picker is repainted every frame from <see cref="WatchInventoryPicker"/>). The whole
    /// list is still shown, just not all at once, which is what the clipped version failed to do.
    /// </para>
    /// </summary>
    private static string BackpackScrollingSummary(Hero hero)
    {
        var text = BackpackSummary(hero, int.MaxValue);
        if (text.Length <= BackpackWindow)
        {
            // Everything fits: nothing to scroll, and a static line is easier to read.
            ResetBackpackScroll();
            return text;
        }

        var now = System.Environment.TickCount64;
        if (_backpackScrollStartedAt == 0)
        {
            _backpackScrollStartedAt = now;
        }

        // A gap keeps the end and the start of the list visually separate as it wraps.
        var looped = text + BackpackScrollGap;
        var offset = (int)((now - _backpackScrollStartedAt) / BackpackScrollMsPerChar) % looped.Length;
        if (offset < 0)
        {
            offset = 0;
        }

        var slice = looped.Length >= BackpackWindow
            ? looped.Substring(offset, System.Math.Min(BackpackWindow, looped.Length - offset))
            : looped;

        // Wrap-around: take the missing tail from the front so the window is always full.
        if (slice.Length < BackpackWindow)
        {
            slice += looped.Substring(0, System.Math.Min(BackpackWindow - slice.Length, looped.Length));
        }

        return slice;
    }

    /// <summary>Clears the scroll clock so the next long list starts from its first item.</summary>
    private static void ResetBackpackScroll()
    {
        _backpackScrollStartedAt = 0;
    }

    /// <summary>
    /// Characters of the BAG line shown at once. The line has no wrap width (see
    /// <see cref="EnsurePickerOverlay"/>), so this window is the only thing keeping it on one line:
    /// it has to stay comfortably inside the pause window's text column at <see cref="PickerScale"/>.
    /// </summary>
    private const int BackpackWindow = 28;

    /// <summary>Milliseconds per character of scroll; slower is easier to read.</summary>
    private const int BackpackScrollMsPerChar = 220;

    /// <summary>Separator inserted between the end and the start of the looped list.</summary>
    private const string BackpackScrollGap = "     ";

    private static long _backpackScrollStartedAt;

    /// <summary>The items the hero carries in reserve (everything equipped is skipped), as names.</summary>
    private static string BackpackSummary(Hero hero, int max)
    {
        try
        {
            var inventory = hero.inventory;
            var items = inventory?.items;
            if (inventory == null || items == null)
            {
                return "-";
            }

            // `max` may be effectively unbounded (the scrolling line wants the whole list), so the
            // capacity is capped rather than trusted: `new List<string>(int.MaxValue)` throws.
            var names = new List<string>(System.Math.Min(max, 32));
            var more = false;

            for (var i = 0; i < items.length; i++)
            {
                if (items.getDyn(i) is not dc.tool.InventItem item)
                {
                    continue;
                }
                if (inventory.isEquiped(item))
                {
                    continue;
                }

                if (names.Count >= max)
                {
                    // One over the budget: report the remainder instead of silently dropping it, so
                    // the line never looks like the bag holds less than it does.
                    more = true;
                    break;
                }

                names.Add(Trim(ReadItemName(item), 18));
            }

            if (names.Count == 0)
            {
                return "-";
            }

            return string.Join(", ", names) + (more ? ", …" : "");
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] picker could not read the backpack: {Message}", ex.Message);
            return "-";
        }
    }

    private static void SetPickerLine(int index, string value)
    {
        var line = index >= 0 && index < PickerLineCount ? _pickerLines[index] : null;
        if (line == null)
        {
            return;
        }

        try
        {
            line.set_text(value.AsHaxeString());
            line.onResize();
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] picker could not set its text: {Message}", ex.Message);
        }
    }

    /// <summary>One-shot diagnostic for the picker, so a missing overlay is explainable.</summary>
    private static bool _pickerLogged;

    private static void LogPickerOnce(dc.h2d.Object parent)
    {
        if (!Logging || _pickerLogged)
        {
            return;
        }
        _pickerLogged = true;
        Log($"[LocalCoop] picker overlay parented to {parent.GetType().Name}");
    }

    /// <summary>Removes the picker labels, if any.</summary>
    private static void RemovePickerOverlay()
    {
        for (var i = 0; i < PickerLineCount; i++)
        {
            var line = _pickerLines[i];
            if (line == null)
            {
                continue;
            }

            try
            {
                line.remove();
            }
            catch (Exception ex)
            {
                LogWarn("[LocalCoop] could not remove a picker line: {Message}", ex.Message);
            }

            _pickerLines[i] = null;
        }

        _pickerParent = null;
    }

    /// <summary>
    /// Whether that player asked to switch the window's hero this frame: the pad player by clicking
    /// the right stick (R3), the keyboard player by pressing Q. Edge-triggered for the pad (a held
    /// stick would otherwise flip the selection every frame).
    /// </summary>
    private static bool PressedPicker(InputBinding binding, GamePad? pad, ref bool held)
    {
        bool down;
        if (binding.IsKeyboard)
        {
            try
            {
                down = Key.Class.isPressed.Invoke(KeyPicker);
            }
            catch
            {
                down = false;
            }
        }
        else
        {
            down = RawPadButton(pad, R3ValueIndex);
        }

        var pressed = down && !held;
        held = down;
        return pressed;
    }
}
