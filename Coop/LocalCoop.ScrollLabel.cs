using System.Runtime.CompilerServices;
using dc.ui;
using dc.ui.hud;
using dc.h2d;
using HaxeProxy.Runtime;
using ModCore.Utilities;

namespace DeadCellsLocalMultiplayerMod;

/// <summary>
/// The "whose turn is it" label drawn over a scroll promotion screen, in its top-left corner.
/// <para>
/// The scroll promotion screen is the same one for both heroes — the game cannot tell them apart —
/// so without this the player has to remember whose pass they are on, which reads exactly like "the
/// character I did not pick is getting the point". The label is one line of the game's own text,
/// created with <c>dc.Assets.makeText()</c> so it is registered in the Haxe heap like every other
/// piece of the HUD.
/// </para>
/// <para>
/// It is drawn on the promotion screen's own root object and marked absolute, so it sits over the
/// window instead of joining any layout, and it is removed when the screen goes away.
/// </para>
/// <para>
/// <b>One label per screen, not one label per mod.</b> The chain keeps two screens alive for a
/// moment: the first one validates, the second is constructed from inside that <c>validate</c>, and
/// the first only disposes a tween later. A single <c>(label, owner)</c> pair therefore could not
/// say "this screen already has a label" — whichever screen ticked last stole the slot, and the next
/// pass on the other screen built a <i>second</i> line on top of the first (or lost the tracking and
/// left a stale one behind). That is exactly the "sometimes the label is wrong / doubled / missing"
/// symptom. Keying the label on its own screen fixes it by construction, and the weak table means a
/// screen that dies without its dispose hook reaching us cannot leak the line.
/// </para>
/// </summary>
internal static partial class LocalCoop
{
    /// <summary>Colour of the "whose turn" line: player one's own label colour (white).</summary>
    private const int ScrollLabelColorP1 = 0xF4F8FF;

    /// <summary>Player two's label, tinted so the two passes are told apart at a glance.</summary>
    private const int ScrollLabelColorP2 = 0x9BD7FF;

    /// <summary>
    /// The label each promotion screen carries, keyed on the screen itself. A screen that already has
    /// an entry is labelled and is never labelled twice; the weak key means a screen the game tears
    /// down without our dispose hook cannot keep the line alive.
    /// </summary>
    private static readonly ConditionalWeakTable<TierSelect, dc.ui.Text> _scrollLabels = new();

    /// <summary>
    /// Shows the "whose turn" label on the given promotion screen, once.
    /// </summary>
    /// <param name="screen">The screen that just opened.</param>
    /// <param name="playerTwo">True for player two's pass, false for player one's.</param>
    private static void AttachScrollLabel(TierSelect screen, bool playerTwo)
    {
        // Called every frame while the screen is open, so an already-labelled screen is a no-op
        // instead of a pile of duplicate lines.
        if (_scrollLabels.TryGetValue(screen, out _))
        {
            return;
        }

        try
        {
            var root = screen.root;
            if (root == null)
            {
                // Not laid out yet: the next frame retries, and nothing was recorded as done.
                return;
            }

            var text = dc.Assets.Class.makeText(
                (playerTwo ? "P2 选择" : "P1 选择").AsHaxeString(),
                dc.ui.Text.Class.COLORS.get("WO".AsHaxeString()),
                false,
                root);

            text.textColor = playerTwo ? ScrollLabelColorP2 : ScrollLabelColorP1;
            text.customScale = 1.0;

            // Absolute in the screen's root: the root is a Layers container, which lays its children
            // out; absolute takes this line out of that so it owns its position and cannot shove the
            // promotion screen's own contents.
            text.x = 8;
            text.y = 8;

            text.onResize();

            _scrollLabels.Add(screen, text);
        }
        catch (Exception ex)
        {
            // The label is a convenience, never a reason to drop the promotion.
            LogWarn("[LocalCoop] [scroll] could not draw the whose-turn label: {Message}", ex.Message);
        }
    }

    /// <summary>Removes the label belonging to the screen that is going away.</summary>
    private static void DetachScrollLabel(TierSelect screen)
    {
        if (!_scrollLabels.TryGetValue(screen, out var text))
        {
            return;
        }

        _scrollLabels.Remove(screen);
        RemoveScrollLabel(text);
    }

    /// <summary>Removes every label outright (new run, level change).</summary>
    private static void ForgetScrollLabel()
    {
        // Collect first: removing entries while enumerating a ConditionalWeakTable is not supported.
        // No `Count` guard — the table exposes Count only as a LINQ extension, and the enumeration
        // below is already the cheap path when it is empty.
        List<dc.ui.Text>? doomed = null;
        List<TierSelect>? screens = null;

        foreach (var pair in _scrollLabels)
        {
            (doomed ??= []).Add(pair.Value);
            (screens ??= []).Add(pair.Key);
        }

        if (doomed == null || screens == null)
        {
            return;
        }

        foreach (var text in doomed)
        {
            RemoveScrollLabel(text);
        }

        // The keys go with the labels; a screen still on screen would simply be relabelled.
        foreach (var screen in screens)
        {
            _scrollLabels.Remove(screen);
        }
    }

    private static void RemoveScrollLabel(dc.ui.Text text)
    {
        try
        {
            text.parent?.removeChild(text);
        }
        catch
        {
            // The screen it belonged to may already be gone; nothing to remove then.
        }
    }
}
