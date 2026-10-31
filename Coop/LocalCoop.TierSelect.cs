using dc;
using dc.en;
using dc.pr;
using dc.tool;
using dc.ui;

namespace DeadCellsLocalMultiplayerMod;

/// <summary>
/// Part of <see cref="LocalCoop"/>: a **scroll** pickup asks <b>both</b> players to choose — whoever
/// picked it up first, the other hero second — and each screen says whose turn it is.
/// <para>
/// The promotion screen a scroll opens is <see cref="TierSelect"/> — the three brutality / tactics /
/// survival columns, which is also what a level-up uses. It is built for one hero, so the tier lands
/// on that hero and the other player is never asked; the two heroes' scroll tiers then diverge even
/// though a scroll should give each of them one point.
/// </para>
/// <para>
/// So after the game's own confirm, a <b>second</b> screen is opened for the other hero: whoever
/// walked over the scroll chooses first, and their partner second. Each pass grants one tier. Each
/// screen also carries a label saying whose turn it is, so nobody has to remember whose pass they
/// are on.
/// </para>
/// <para>
/// <b>Only a scroll chains.</b> The level-up screen is the very same <see cref="TierSelect"/> and
/// must <b>not</b> repeat — the game already grants the level-up to the hero it belongs to. A scroll's
/// window carries the scroll item in <c>ii</c>; a level-up's does not. That is read straight off the
/// window in <see cref="IsScrollScreen"/>, which is also where the chain is armed — because a scroll on
/// the ground never reaches <c>Hero.pickItem</c> (the log shows <c>validate</c> firing with no pickup
/// at all), so the pickup hook cannot be what records it.
/// </para>
/// <para>
/// <b>The chain is one remembered screen, not a countdown.</b> An earlier version kept an
/// <c>int</c> of "passes still owed" and cleared it on the second confirm. That loses whenever the
/// second screen is <b>closed instead of confirmed</b>: the counter stayed at 1, and the next scroll's
/// first screen then spent the stale pass — it returned before opening its own second screen, so
/// player two silently got no point on that scroll. Remembering *which screen* is the pending second
/// pass (and dropping it when that screen disposes, however it went away) cannot get out of step:
/// the state is tied to the screen's lifetime rather than to a number that has to be decremented.
/// </para>
/// </summary>
internal static partial class LocalCoop
{
    /// <summary>
    /// The second-pass screen this mod opened and is still waiting on, or null when no chain is
    /// pending. Set when the partner's screen is created; cleared on its confirm and on its dispose,
    /// so a screen the player backs out of cannot leave the chain armed.
    /// </summary>
    private static TierSelect? _scrollSecondScreen;

    /// <summary>
    /// Whether this screen is a scroll's promotion screen (and so worth chaining), as opposed to the
    /// level-up screen that uses the same class. A scroll carries the scroll item in <c>ii</c>; a
    /// level-up does not.
    /// </summary>
    private static bool IsScrollScreen(TierSelect self) => self.ii != null && self.sourceEnt != null;

    /// <summary>
    /// Whether a scroll should chain at all: only while each hero has its own bag.
    /// <para>
    /// With a shared backpack the scroll item lands in the one inventory both heroes read, so the
    /// first choice already raised the other hero's tier — a second choice would add a SECOND scroll
    /// to the shared bag.
    /// </para>
    /// </summary>
    private static bool ScrollChainWanted => !SharedEquipment;

    /// <summary>
    /// A promotion screen was confirmed. The game's own confirm runs first — it applies the tier and
    /// starts closing the screen — then, when this was a scroll's first pass, the same screen is
    /// opened for the hero that has not answered yet, with a label saying whose turn it is.
    /// </summary>
    private static void OnTierSelectValidate(Hook_TierSelect.orig_validate orig, TierSelect self)
    {
        try
        {
            // <b>Read the chain state BEFORE calling orig.</b> `orig` is what closes the screen, and
            // a `Process` closes before it validates, so by the time it returns this screen's
            // `onDispose` has already run and cleared the pending pass. Deciding after the call made
            // every confirm look like a first pass — that opened another screen from inside the
            // confirm, whose confirm opened another, i.e. the "validate / opened repeating once a
            // second" loop this file has been burned by before. Snapshot first, then decide.
            var wasSecondPass = ReferenceEquals(self, _scrollSecondScreen);
            var chainWanted = ScrollChainWanted;
            var isScroll = IsScrollScreen(self);

            // The game's own confirm: applies the chosen tier to `self.hero` and starts the close.
            orig(self);

            // This screen was the pending second pass: both heroes have answered, the chain is spent.
            if (wasSecondPass)
            {
                _scrollSecondScreen = null;
                return;
            }

            // A screen that is not the pending second pass is a first pass — but only a scroll
            // chains, and only while the bags are independent.
            if (!chainWanted || !isScroll)
            {
                return;
            }

            var partner = ReferenceEquals(self.hero, _hero1) ? _hero2 : _hero1;
            var item = self.ii;
            var source = self.sourceEnt;

            // A missing piece just ends the chain after the first choice, which is the vanilla
            // behaviour and always safe.
            if (partner == null || partner.destroyed || item == null || source == null)
            {
                LogVerbose("[LocalCoop] [scroll] no second pass: the partner or the scroll item is gone");
                return;
            }

            var next = new TierSelect(partner, source, item);
            _scrollSecondScreen = next;

            // The constructor builds its own root inside `Main.ME.root` (it calls
            // createRootInLayers), so the screen is already attached where the player can see it.
            // The closing screen's parent is a fallback for a build where that changes.
            try
            {
                var parent = self.parent;
                if (parent != null && next.parent == null)
                {
                    parent.addChild(next);
                }
            }
            catch (Exception ex)
            {
                LogVerbose($"[LocalCoop] [scroll] could not re-parent the second screen ({ex.Message})");
            }

            try
            {
                if (next.parent == null)
                {
                    Game.Class.ME?.addChild(next);
                }
            }
            catch
            {
                // The screen is already built; attaching is a bonus either way.
            }

            AttachScrollLabel(next, ReferenceEquals(partner, _hero2));
            LogVerbose($"[LocalCoop] [scroll] second screen opened for " +
                $"{(ReferenceEquals(partner, _hero2) ? "P2" : "P1")}");
        }
        catch (Exception ex)
        {
            _scrollSecondScreen = null;
            LogWarn("[LocalCoop] [scroll] promotion chain failed: {Message}", ex.Message);
        }
    }

    /// <summary>Forgets the armed chain (the scroll is spent, or the run moved on).</summary>
    private static void ForgetScrollPromotion()
    {
        _scrollSecondScreen = null;
    }

    /// <summary>Drops the chain state on a new run / level change.</summary>
    private static void ResetTierSelectState()
    {
        ForgetScrollPromotion();
        ForgetScrollLabel();
    }

    /// <summary>
    /// A promotion screen is ticking. The first screen is the one the game opened and the mod never
    /// sees its constructor — so this is where it gets its label. The second is labelled when it is
    /// opened, and <c>AttachScrollLabel</c> is a no-op for a screen that already has one.
    /// <para>
    /// Whose turn it is comes from the screen's own <c>hero</c>, never from the chain state: the chain
    /// only knows about the <i>pending</i> pass, so using it here left the first screen permanently
    /// unlabelled (the chain is armed by that screen's own confirm, i.e. after it is already on
    /// screen) and could label a screen with the other player's colour.
    /// </para>
    /// </summary>
    private static void OnTierSelectUpdate(Hook_TierSelect.orig_update orig, TierSelect self)
    {
        orig(self);

        if (!ScrollChainWanted || !IsScrollScreen(self))
        {
            return;
        }

        AttachScrollLabel(self, ReferenceEquals(self.hero, _hero2));
    }

    /// <summary>
    /// A promotion screen was torn down. Its own label goes with it.
    /// <para>
    /// If it was the pending second pass, the chain is dropped: the player closed the partner's
    /// screen instead of confirming it, so that pass is abandoned rather than carried into the next
    /// scroll. This is the fix for "the next scroll skipped player two's choice".
    /// </para>
    /// </summary>
    private static void OnTierSelectOnDispose(Hook_TierSelect.orig_onDispose orig, TierSelect self)
    {
        try
        {
            orig(self);
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] [scroll] promotion screen dispose failed: {Message}", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(self, _scrollSecondScreen))
            {
                _scrollSecondScreen = null;
                LogVerbose("[LocalCoop] [scroll] the second pass was closed without a choice");
            }

            DetachScrollLabel(self);
        }
    }
}
