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
/// window in <see cref="ArmFromScreen"/>, which is also where the chain is armed — because a scroll on
/// the ground never reaches <c>Hero.pickItem</c> (the log shows <c>validate</c> firing with no pickup
/// at all), so the pickup hook cannot be what records it.
/// </para>
/// </summary>
internal static partial class LocalCoop
{
    /// <summary>
    /// Passes the armed scroll still owes. A scroll costs one pass per hero, so the first screen
    /// leaves exactly one owed.
    /// </summary>
    private static int _scrollPassesLeft;

    /// <summary>
    /// Arms the chain from the screen that just confirmed — the first pass. Whoever that screen
    /// belongs to answers first, and their partner second.
    /// </summary>
    /// <remarks>
    /// A scroll is armed here rather than in the pickup hook because a scroll on the ground never
    /// reaches <c>Hero.pickItem</c>: the log shows <c>validate</c> firing with no pickup at all. So
    /// the pickup hook could never arm the chain, which is exactly what the failing runs showed
    /// ("validate fired" followed by "no chain armed").
    /// </remarks>
    private static bool ArmFromScreen(TierSelect self)
    {
        if (!Enabled || _hero1 == null || _hero1.destroyed || _hero2 == null || _hero2.destroyed)
        {
            return false;
        }

        // Only worth chaining while each hero has its own bag. With a shared backpack the scroll item
        // lands in the one inventory both heroes read, so the first choice already raised the other
        // hero's tier; a second choice would add a SECOND scroll to the shared bag.
        if (SharedEquipment)
        {
            return false;
        }

        // A level-up screen has no scroll item in `ii`; a scroll's does. That is what keeps the
        // level-up screen single-pass, and it is read straight off the window so no pickup record is
        // needed.
        if (self.ii == null || self.sourceEnt == null)
        {
            return false;
        }

        // Nothing has to be remembered: the second screen is built from the screen that is closing,
        // and its own hero/source/item are read below.

        // One pass owed: this screen is the first pass and is closing right now.
        _scrollPassesLeft = 1;
        return true;
    }

    /// <summary>
    /// A promotion screen was confirmed. The game's own confirm runs first — it applies the tier and
    /// closes the screen — then, while the armed scroll still owes a pass, the same screen is opened
    /// for the hero that has not answered yet, with a label saying whose turn it is.
    /// </summary>
    private static void OnTierSelectValidate(Hook_TierSelect.orig_validate orig, TierSelect self)
    {
        try
        {
            orig(self);

            // Nothing armed yet: this is the first pass. Arm it from the screen that just confirmed,
            // because a scroll's pickup never reaches Hero.pickItem (the ground item is handled
            // elsewhere) — so the pickup hook cannot be what records the chain. Arming leaves one
            // pass owed, and the fall-through opens it.
            if (_scrollPassesLeft <= 0)
            {
                if (!ArmFromScreen(self))
                {
                    return;
                }
            }
            else
            {
                _scrollPassesLeft--;
            }

            if (_scrollPassesLeft <= 0)
            {
                // Every hero has answered; the scroll is spent.
                ForgetScrollPromotion();
                return;
            }

            // The hero that has not answered yet — the partner of whoever this screen belongs to.
            var nextHero = ReferenceEquals(self.hero, _hero1) ? _hero2 : _hero1;
            var item = self.ii;
            var source = self.sourceEnt;

            // Read what the second screen needs off the screen that is closing, so nothing has to be
            // remembered across the pickup. A missing piece just ends the chain after the first
            // choice, which is the vanilla behaviour and always safe.
            if (nextHero == null || nextHero.destroyed || item == null || source == null)
            {
                ForgetScrollPromotion();
                return;
            }

            var next = new TierSelect(nextHero, source, item);

            // The constructor takes (hero, source, item) with no parent, so it attaches the screen to
            // whatever UI root the game resolves internally — which is the same root the screen that
            // is closing used. Copying that parent is what puts the new screen where the player can
            // see it; a root this proxy cannot read simply leaves the game's own choice in place.
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

            // Fall back to the game's own root when the closing screen had none to copy.
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

            AttachScrollLabel(next, ReferenceEquals(nextHero, _hero2));
            LogVerbose($"[LocalCoop] [scroll] second screen opened for " +
                $"{(ReferenceEquals(nextHero, _hero2) ? "P2" : "P1")}");
        }
        catch (Exception ex)
        {
            ForgetScrollPromotion();
            LogWarn("[LocalCoop] [scroll] promotion chain failed: {Message}", ex.Message);
        }
    }

    /// <summary>Forgets the armed scroll (it is spent, or the run moved on).</summary>
    private static void ForgetScrollPromotion()
    {
        _scrollPassesLeft = 0;
    }

    /// <summary>Drops the chain state on a new run / level change.</summary>
    private static void ResetTierSelectState()
    {
        ForgetScrollPromotion();
        ForgetScrollLabel();
    }

    /// <summary>
    /// A promotion screen is ticking. The first screen is the one the game opened, and the mod never
    /// sees its constructor — so this is where that one gets its label. The second is labelled when it
    /// is opened, and <c>AttachScrollLabel</c> is a no-op for a screen that already has one.
    /// </summary>
    private static void OnTierSelectUpdate(Hook_TierSelect.orig_update orig, TierSelect self)
    {
        orig(self);

        if (_scrollPassesLeft <= 0)
        {
            return;
        }

        // Whose turn this screen is, read off the screen itself: while the chain still owes its one
        // pass, the screen on screen belongs to the hero that has not answered yet.
        AttachScrollLabel(self, _scrollPassesLeft > 0 && ReferenceEquals(self.hero, _hero2));
    }

    /// <summary>
    /// A promotion screen was torn down. The label that belonged to it goes with it; the chain state
    /// is deliberately left alone, because the next screen of the same scroll is about to open and
    /// still needs to know how many passes it owes and to which heroes.
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
            DetachScrollLabel(self);
        }
    }
}
