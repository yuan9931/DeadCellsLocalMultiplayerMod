using dc;
using dc.en;
using dc.libs.heaps;
using dc.pr;
using ModCore.Utilities;

namespace DeadCellsLocalMultiplayerMod;

/// <summary>
/// Part of <see cref="LocalCoop"/>: the floating hint above each hero that tells its player how to
/// use the manual teleport.
/// <para>
/// <b>What it shows, and when.</b> On every level change each hero gets a hint above its head for
/// <see cref="HintSeconds"/>: "hold T" for the player on the keyboard, "hold L3" for the player on a
/// pad — the button is per-player, so a hint that named one key for both would be wrong for one of
/// them. After that hero teleports onto its partner, the hint changes for
/// <see cref="ReturnHintSeconds"/> to the "hold to go back" wording, which matches the round trip the
/// teleport actually implements (a second hold inside the return window puts the hero back).
/// </para>
/// <para>
/// <b>Why not <c>Entity.popText</c> / <c>dc.ui.PopText</c>.</b> That is the game's own floating text,
/// and it is tempting — it even tracks a head. But its constructor hard-codes the whole animation and
/// the lifetime: it floats up, fades, and destroys itself after <c>1200 + len*50</c> ms. Five and ten
/// seconds are not expressible through it, and the float-up is the wrong motion for a standing hint.
/// So the hint is a plain <c>Text</c> the mod owns, positioned from the hero's head every frame.
/// </para>
/// <para>
/// <b>Same construction rules as the other overlays in this mod.</b> The label comes from
/// <c>Assets.makeText</c> and hangs off a game-owned parent (the HUD root, whose origin is the
/// screen's (0,0)); a C#-built <c>dc.h2d.Object</c> never renders, and a flow child would join the
/// layout. Text goes through <c>set_text()</c>, never field assignment. Every game call is wrapped:
/// this runs per frame, so a transient level state must never take the run down.
/// </para>
/// </summary>
internal static partial class LocalCoop
{
    // ---------------------------------------------------------------------------------------
    // Timing
    // ---------------------------------------------------------------------------------------

    /// <summary>How long the "hold to teleport" hint stays up after a level change.</summary>
    private const double HintSeconds = 5.0;

    /// <summary>How long the "hold to go back" hint stays up after a teleport.</summary>
    private const double ReturnHintSeconds = 10.0;

    /// <summary>
    /// Pixels above the hero's head the hint is drawn at. Player one's hint sits here; player two's
    /// is stacked one <see cref="HintStackStep"/> higher (see <see cref="FollowHead"/>), so the two
    /// hints stay readable when the heroes stand on top of each other.
    /// </summary>
    private const double HintOffsetY = 26.0;

    /// <summary>
    /// Extra height for the second hint. The heroes can stand in the same cell — the mod even has a
    /// walk-push that lets one carry the other — and two labels on the same head would be drawn on
    /// top of each other, leaving only the later one legible. Stacking the second label a line higher
    /// (and nudging it sideways, see <see cref="HintSideStep"/>) keeps both readable at any distance,
    /// and is invisible when the heroes are apart.
    /// </summary>
    private const double HintStackStep = 22.0;

    /// <summary>
    /// Horizontal nudge applied to the second hint, opposite the first, so the two labels never sit
    /// in the same column even before they separate vertically.
    /// </summary>
    private const double HintSideStep = 34.0;

    /// <summary>
    /// Pixels the label is pulled left of the head so a short hint reads as centred on it. A fixed
    /// nudge rather than half a measured width — see <see cref="FollowHead"/> — sized for the two
    /// short strings this shows ("Hold T to teleport" / "Hold T to go back"), which are the same
    /// length in both directions.
    /// </summary>
    private const double HintCentrePull = 58.0;

    /// <summary>Colour of the hint, matching the mod's other overlay text.</summary>
    private const int HintColor = 0xF4F8FF;

    // ---------------------------------------------------------------------------------------
    // Per-hero hint state
    // ---------------------------------------------------------------------------------------

    /// <summary>One hero's hint: the label, the text it currently shows, and its countdown.</summary>
    private sealed class TeleportHint
    {
        public dc.ui.Text? Label;
        public string Shown = string.Empty;
        public double Left;
        public bool IsReturn;

        /// <summary>
        /// Which of the two hints this is (0 = player one, 1 = player two). Used to stack the labels
        /// apart so they stay readable when the heroes overlap — see <see cref="HintStackStep"/>.
        /// </summary>
        public int Slot;

        /// <summary>
        /// The wording built last time, with the two inputs it was built from. Building it means a
        /// localization lookup, a <c>string.Format</c> and a Haxe string conversion — per hero per
        /// frame while a hint is up — for a string that only changes when the hint flips between
        /// "teleport" and "go back" or the player's device changes.
        /// </summary>
        public string CachedText = string.Empty;
        public bool CachedReturning;
        public bool CachedKeyboard;
        public bool HasCachedText;

        /// <summary>
        /// Whether the label is currently drawn. Both the show and the hide path used to write
        /// <c>visible</c> unconditionally every frame — and a hint is hidden for most of a level, so
        /// that was a Haxe property write per hero per frame for no change at all.
        /// </summary>
        public bool Visible;
    }

    private static readonly TeleportHint _hint1 = new() { Slot = 0 };
    private static readonly TeleportHint _hint2 = new() { Slot = 1 };

    /// <summary>The game-owned object both hints hang off (the HUD root).</summary>
    private static dc.h2d.Object? _hintParent;

    /// <summary>Which HUD the hints were built into, so a rebuilt HUD makes new labels.</summary>
    private static dc.ui.HUD? _hintOwner;

    /// <summary>
    /// Set while the hints are armed for a level: the level they belong to, so the level-change
    /// detection fires exactly once per entry instead of every frame.
    /// </summary>
    private static Level? _hintLevel;

    // ---------------------------------------------------------------------------------------
    // Arm / update
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Arms both hints after a level change, and refreshes them when the heroes or the HUD change.
    /// Called every frame from <see cref="OnFrameUpdate"/>; the per-level arming is guarded by
    /// <see cref="_hintLevel"/> so entering a level shows the hint once.
    /// </summary>
    private static void UpdateTeleportHints(Hero? hero1, Hero? hero2)
    {
        if (!Enabled)
        {
            ClearTeleportHints();
            return;
        }

        var level = hero1?._level ?? hero2?._level;
        if (level == null)
        {
            return;
        }

        // A new level (or the first frame of a run) arms the "hold to teleport" hint on both heroes.
        if (!ReferenceEquals(level, _hintLevel))
        {
            _hintLevel = level;
            ArmTeleportHint(_hint1, returning: false);
            ArmTeleportHint(_hint2, returning: false);
        }

        AdvanceHint(_hint1, hero1, Player1);
        AdvanceHint(_hint2, hero2, Player2);
    }

    /// <summary>
    /// Starts one hero's hint: the wording depends on that player's device and on whether this is the
    /// forward trip or the return.
    /// </summary>
    private static void ArmTeleportHint(TeleportHint hint, bool returning)
    {
        hint.IsReturn = returning;
        hint.Left = returning ? ReturnHintSeconds : HintSeconds;
        hint.Shown = string.Empty; // force a repaint with the right wording
    }

    /// <summary>
    /// Called when a hero teleports onto its partner: swaps that hero's hint to the return wording,
    /// for exactly as long as the return window stays open.
    /// </summary>
    private static void ShowReturnHint(Hero mover)
    {
        if (ReferenceEquals(mover, _hero1))
        {
            ArmTeleportHint(_hint1, returning: true);
        }
        else if (ReferenceEquals(mover, _hero2))
        {
            ArmTeleportHint(_hint2, returning: true);
        }
    }

    /// <summary>
    /// Counts one hint down, (re)builds its label, repaints it when the wording changes, and keeps it
    /// on its hero's head. A hint that has run out hides its label.
    /// </summary>
    private static void AdvanceHint(TeleportHint hint, Hero? hero, InputBinding binding)
    {
        try
        {
            if (hint.Left <= 0.0 || hero == null || hero.destroyed || hero._level == null)
            {
                HideHintLabel(hint);
                return;
            }

            var parent = ResolveHintParent();
            if (parent == null)
            {
                return;
            }

            EnsureHintLabel(hint, parent);

            var text = HintText(hint, binding);
            if (text != hint.Shown)
            {
                hint.Shown = text;
                hint.Label?.set_text(text.AsHaxeString());
            }

            if (!hint.Visible)
            {
                hint.Label?.set_visible(true);
                hint.Visible = true;
            }

            FollowHead(hint.Label, hero, hint.Slot);
        }
        catch (Exception ex)
        {
            // Never let a hint take the run down; drop it and let the next arming try again.
            hint.Left = 0.0;
            LogWarn("[LocalCoop] teleport hint failed: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Ages both hints. Split out so the countdown can run from the per-frame `dt` in
    /// <see cref="OnFrameUpdate"/> — a paused or bogus frame must not consume the hint's time.
    /// </summary>
    private static void TickTeleportHints(double dt)
    {
        if (!IsUsableDt(dt))
        {
            return;
        }

        if (_hint1.Left > 0.0)
        {
            _hint1.Left -= dt;
        }
        if (_hint2.Left > 0.0)
        {
            _hint2.Left -= dt;
        }
    }

    /// <summary>
    /// The wording for the "how to teleport" hint, and for the return hint.
    /// <para>
    /// Both name <b>this player's own button</b>, picked from the binding: the keyboard player holds
    /// T, the pad player holds L3. That is the button the teleport actually reads for that player
    /// (<c>TeleportButtonDown</c> makes the same choice), so a hint that named the other device would
    /// simply be wrong. The two differ only in the verb — "to teleport" for the forward trip,
    /// "to go back" for the return window.
    /// </para>
    /// <para>
    /// Both strings are localized: the key is the English source text in <c>main.zh.po</c>, and
    /// <c>GetText</c> returns it unchanged when the game is not in a language the mod ships.
    /// </para>
    /// <para>
    /// Cached per hint (see <see cref="TeleportHint.CachedText"/>): this is called from the per-frame
    /// hint pass, and the answer only changes when the hint flips between forward and return, or
    /// when the player's device changes.
    /// </para>
    /// </summary>
    private static string HintText(TeleportHint hint, InputBinding binding)
    {
        if (hint.HasCachedText
            && hint.CachedReturning == hint.IsReturn
            && hint.CachedKeyboard == binding.IsKeyboard)
        {
            return hint.CachedText;
        }

        hint.CachedText = Wording(binding, hint.IsReturn);
        hint.CachedReturning = hint.IsReturn;
        hint.CachedKeyboard = binding.IsKeyboard;
        hint.HasCachedText = true;
        return hint.CachedText;
    }

    private static string Wording(InputBinding binding, bool returning)
    {
        var button = binding.IsKeyboard ? "T" : "L3";
        return returning
            ? string.Format(L("Hold {0} to go back"), button)
            : string.Format(L("Hold {0} to teleport"), button);
    }

    /// <summary>
    /// Looks a hint string up in the mod's own localization, falling back to the English source when
    /// the lookup is unavailable (a language the mod does not ship, or the pak not loaded yet).
    /// </summary>
    private static string L(string key)
    {
        try
        {
            // Fully qualified: `using ModCore.Modules` would make `Game` ambiguous with dc.pr.Game,
            // which this file uses for the HUD lookup.
            return ModCore.Modules.GetText.Instance.GetString(key).ToString();
        }
        catch
        {
            return key;
        }
    }

    // ---------------------------------------------------------------------------------------
    // Label plumbing
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The game-owned object the hints hang off: the HUD's root (a full-screen <c>Layers</c> whose
    /// origin is the screen's (0,0), so the head position needs no offset).
    /// </summary>
    private static dc.h2d.Object? ResolveHintParent()
    {
        var hud = Game.Class.ME?.hud;
        if (hud == null)
        {
            return null;
        }

        if (!ReferenceEquals(hud, _hintOwner))
        {
            // The HUD was rebuilt (level transition, run start): the old labels belong to it.
            ClearTeleportHints();
            _hintOwner = hud;
        }

        if (_hintParent != null)
        {
            return _hintParent;
        }

        try
        {
            _hintParent = hud.root;
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not read the HUD root for the hints: {Message}", ex.Message);
        }

        return _hintParent;
    }

    /// <summary>Creates one hint label through the game's text factory, parented to the HUD root.</summary>
    private static void EnsureHintLabel(TeleportHint hint, dc.h2d.Object parent)
    {
        if (hint.Label != null)
        {
            return;
        }

        var label = dc.Assets.Class.makeText(
            "".AsHaxeString(),
            dc.ui.Text.Class.COLORS.get("WO".AsHaxeString()),
            false,
            parent);
        label.textColor = HintColor;
        label.customScale = 0.8;
        label.onResize();

        hint.Label = label;
        hint.Shown = string.Empty;
    }

    /// <summary>
    /// Puts the label on the hero's head. <c>get_headX/Y</c> are in level coordinates, so they go
    /// through the level's own global transform — the same pair <c>dc.ui.PopText</c> uses.
    /// <para>
    /// The label is left-aligned on the head rather than centred: <c>dc.ui.Text</c> does not expose
    /// a measured width (<c>textWidth</c> belongs to <c>dc.h2d.Text</c>, which the proxy gives this
    /// class no path to), and guessing half of a string's width would drift with the wording and the
    /// language. A small fixed pull to the left keeps it visually centred for the two short strings
    /// this shows, without pretending to measure them.
    /// </para>
    /// <para>
    /// <b>The two hints are stacked apart.</b> The heroes can occupy the same cell — the mod's own
    /// walk-push lets one carry the other — and two labels drawn at the same offset would overlap into
    /// an unreadable smear. Player two's label therefore sits one <see cref="HintStackStep"/> higher
    /// and <see cref="HintSideStep"/> to the other side of its head, so both stay legible whether the
    /// heroes touch or stand apart. The offset is constant rather than distance-dependent on purpose:
    /// a hint that drifted as the players walked would be more distracting than a slightly higher
    /// second line.
    /// </para>
    /// </summary>
    private static void FollowHead(dc.ui.Text? label, Hero hero, int slot)
    {
        if (label == null)
        {
            return;
        }

        try
        {
            var level = hero._level;
            if (level == null)
            {
                return;
            }

            // Player one's label is pulled left to centre it on the head; player two's is pushed
            // further left again, so the two strings never share a column.
            var side = slot == 0 ? -HintCentrePull : -HintCentrePull - HintSideStep;
            var lift = HintOffsetY + (slot * HintStackStep);

            label.x = level.toGlobalX(hero.get_headX()) + side;
            label.y = level.toGlobalY(hero.get_headY()) - lift;
        }
        catch
        {
            // A transient level state: leave the label where it was for this frame.
        }
    }

    private static void HideHintLabel(TeleportHint hint)
    {
        if (hint.Label == null || !hint.Visible)
        {
            return;
        }

        try
        {
            hint.Label.set_visible(false);
            hint.Visible = false;
        }
        catch
        {
            // Transient: the next frame retries (the flag is only cleared once the write worked).
        }
    }

    /// <summary>
    /// Removes both hint labels and forgets the level they were armed for, so the next level change
    /// shows them again. Called from the run teardown and when the session is disabled.
    /// </summary>
    private static void ClearTeleportHints()
    {
        // Two explicit calls, not `foreach (var hint in new[] { _hint1, _hint2 })`: this also runs
        // every frame while PvP is active, and the array was allocated each time.
        ClearTeleportHint(_hint1);
        ClearTeleportHint(_hint2);

        _hintParent = null;
        _hintOwner = null;
        _hintLevel = null;
    }

    /// <summary>Removes one hint's label and resets its state.</summary>
    private static void ClearTeleportHint(TeleportHint hint)
    {
        try
        {
            hint.Label?.remove();
        }
        catch
        {
        }

        hint.Label = null;
        hint.Shown = string.Empty;
        hint.Left = 0.0;
        hint.IsReturn = false;
        hint.Visible = false;
        hint.HasCachedText = false;
        hint.CachedText = string.Empty;
    }
}
