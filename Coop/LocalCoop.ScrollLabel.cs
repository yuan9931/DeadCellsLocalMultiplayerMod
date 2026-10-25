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
/// </summary>
internal static partial class LocalCoop
{
    /// <summary>Colour of the "whose turn" line: player one's own label colour (white).</summary>
    private const int ScrollLabelColorP1 = 0xF4F8FF;

    /// <summary>Player two's label, tinted so the two passes are told apart at a glance.</summary>
    private const int ScrollLabelColorP2 = 0x9BD7FF;

    /// <summary>The label currently shown, so it can be removed when its screen goes away.</summary>
    private static dc.ui.Text? _scrollLabel;

    /// <summary>The screen the label belongs to. A label for a screen that is gone is dropped.</summary>
    private static TierSelect? _scrollLabelOwner;

    /// <summary>
    /// Shows the "whose turn" label on the given promotion screen.
    /// </summary>
    /// <param name="screen">The screen that just opened.</param>
    /// <param name="playerTwo">True for player two's pass, false for player one's.</param>
    private static void AttachScrollLabel(TierSelect screen, bool playerTwo)
    {
        // Called every frame while the screen is open, so an already-labelled screen is a no-op
        // instead of a pile of duplicate lines.
        if (_scrollLabelOwner == screen)
        {
            return;
        }

        try
        {
            // Only one promotion screen is ever open at a time, so the previous label (if any) is
            // already gone with its screen.
            var root = screen.root;
            if (root == null)
            {
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

            _scrollLabel = text;
            _scrollLabelOwner = screen;
        }
        catch (Exception ex)
        {
            // The label is a convenience, never a reason to drop the promotion.
            LogWarn("[LocalCoop] [scroll] could not draw the whose-turn label: {Message}", ex.Message);
        }
    }

    /// <summary>Removes the label, if its screen is the one that is going away.</summary>
    private static void DetachScrollLabel(TierSelect screen)
    {
        if (_scrollLabelOwner == null || !ReferenceEquals(_scrollLabelOwner, screen))
        {
            return;
        }

        ForgetScrollLabel();
    }

    /// <summary>Removes the label outright (new run, level change).</summary>
    private static void ForgetScrollLabel()
    {
        var text = _scrollLabel;
        _scrollLabel = null;
        _scrollLabelOwner = null;

        if (text == null)
        {
            return;
        }

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
