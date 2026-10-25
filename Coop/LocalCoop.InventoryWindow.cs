using dc.en;
using dc.h2d;
using dc.pr;
using dc.ui;
using dc.ui.pause;
using HaxeProxy.Runtime;

namespace DeadCellsLocalMultiplayerMod;

/// <summary>
/// Part of <see cref="LocalCoop"/>: the pause / inventory window shows the equipment of whichever
/// hero the players choose.
/// <para>
/// <b>Which windows these are.</b> The pause screen is <c>dc.ui.pause.DefaultPause</c> (a
/// <c>dc.ui.Pause</c>, itself a <c>dc.ui.Process</c>) — it lists both weapons, both skills, the
/// perks and the backpack. <c>dc.ui.DebugPopUp</c> is a different, much smaller window (the
/// item-swap popup); both are handled here, because both read <c>Game.ME.hero</c>.
/// </para>
/// <para>
/// <b>Why the hero has to be swapped.</b> Those windows are global singletons and read everything
/// from <c>Game.ME.hero</c> — which this mod deliberately keeps as player one, so without this
/// they always show player one's gear. Neither window carries a "who opened me" field, and the
/// input that opens them arrives with no hero identity at all. With a keyboard on one side the
/// opener would not be recoverable even in principle: keyboard state is global.
/// </para>
/// <para>
/// So the choice is explicit instead of inferred: each player presses a button on <b>their own
/// device</b> to show their own hero, which sidesteps the keyboard-opener ambiguity entirely. The
/// picker overlay that advertises and reads those presses is in
/// <c>LocalCoop.InventoryPicker.cs</c>.
/// </para>
/// <para>
/// While the window updates, <c>Game.ME.hero</c> is pointed at the chosen hero (save / point / run
/// / restore, nest-safe) so everything the window reads — item lists, the wielded weapon, the
/// tiers — comes from that hero. The global is put back the moment the window's frame ends, so
/// nothing else in the game ever sees the swapped value. It is the same technique
/// <c>RunAsOwningHero</c> uses for the detached head.
/// </para>
/// </summary>
internal static partial class LocalCoop
{
    /// <summary>Hero whose backpack the open window is showing, or null outside one.</summary>
    private static Hero? _inventoryWindowHero;

    /// <summary>The <c>DebugPopUp</c> item-swap window, when one is open.</summary>
    private static dc.ui.DebugPopUp? _inventoryWindow;

    /// <summary>The pause screen, when one is open.</summary>
    private static DefaultPause? _pauseWindow;

    /// <summary>Hero whose items the open pause screen was last re-pointed at.</summary>
    private static Hero? _pauseAppliedHero;

    /// <summary>True while <see cref="RunWindowAs"/> has <c>Game.ME.hero</c> pointed away from P1.</summary>
    private static bool _windowHeroSwapped;


    /// <summary>
    /// Points <c>Game.ME.hero</c> at <paramref name="hero"/> for the duration of
    /// <paramref name="body"/>, then restores it. Nested calls are safe: the outermost swap wins
    /// and the innermost restore puts back what the outer one saved.
    /// </summary>
    private static void RunWindowAs(Hero? hero, Action body)
    {
        var game = Game.Class.ME;
        var swap = hero != null
            && game != null
            && !hero.destroyed
            && !ReferenceEquals(game.hero, hero)
            && (ReferenceEquals(hero, _hero1) || ReferenceEquals(hero, _hero2));

        var previousHero = game?.hero;
        var wasSwapped = _windowHeroSwapped;
        _windowHeroSwapped = swap;
        try
        {
            if (swap)
            {
                game!.hero = hero;
            }

            body();
        }
        finally
        {
            if (swap)
            {
                game!.hero = previousHero;
            }

            _windowHeroSwapped = wasSwapped;
        }
    }

    /// <summary>
    /// Which hero the open window should show. Defaults to player one, because that is who the
    /// global already is and what the window was showing before the picker existed.
    /// </summary>
    private static Hero? ResolveInventoryWindowHero()
    {
        if (_inventoryWindowHero != null && !_inventoryWindowHero.destroyed)
        {
            return _inventoryWindowHero;
        }

        return _hero1 ?? _hero2;
    }

    /// <summary>True while either pickable window is open.</summary>
    private static bool InventoryWindowOpen => _pauseWindow != null || _inventoryWindow != null;

    /// <summary>
    /// The game-owned object the picker overlay should hang off: the open window's own background,
    /// so the overlay follows that window and is drawn in its layer. Colors on the pause screen are
    /// covered by <c>dc.ui.BG</c>, a full-screen <c>h2d.Layers</c>.
    /// </summary>
    private static dc.h2d.Object? InventoryWindowOverlayParent()
    {
        var pause = _pauseWindow;
        if (pause != null)
        {
            try
            {
                if (pause.bg != null)
                {
                    return pause.bg;
                }
            }
            catch
            {
                // Fall through to the process root below.
            }

            try
            {
                if (pause.root != null)
                {
                    return pause.root;
                }
            }
            catch
            {
                // Fall through to the DebugPopUp below.
            }
        }

        var popup = _inventoryWindow;
        if (popup != null)
        {
            try
            {
                if (popup.bg != null)
                {
                    return popup.bg;
                }
            }
            catch
            {
                // No usable anchor; the picker simply stays hidden.
            }
        }

        return null;
    }

    // ---------------------------------------------------------------- pause screen

    /// <summary>
    /// Points the global at the chosen hero while the pause screen is <b>created</b>, so the item
    /// descriptions and backpack name it builds in its constructor belong to that hero. The
    /// per-frame hook below covers everything it re-reads later.
    /// </summary>
    private static void OnGamePause(Hook_Game.orig_pause orig, dc.pr.Game self)
    {
        if (!Enabled || _hero1 == null || _hero2 == null)
        {
            orig(self);
            return;
        }

        RunWindowAs(ResolveInventoryWindowHero(), () => orig(self));
    }

    /// <summary>
    /// Watches the pause screen's per-frame update. While it is open the global hero is pointed at
    /// the chosen hero, so the weapons / skills / backpack it draws come from that hero.
    /// </summary>
    private static void OnPauseUpdate(Hook_DefaultPause.orig_update orig, DefaultPause self)
    {
        if (!Enabled || _hero1 == null || _hero2 == null)
        {
            orig(self);
            return;
        }

        var hero = ResolveInventoryWindowHero();
        if (hero == null || hero.destroyed)
        {
            orig(self);
            return;
        }

        _pauseWindow = self;
        _inventoryWindowHero ??= hero;

        // The window's own update runs with the global pointed at the chosen hero, then the item
        // descriptions are re-pointed at that hero explicitly (the window does not re-read the
        // global for them, which is why the swap alone left the gear unchanged), and the
        // diagnostic runs last so it reports what is actually on screen.
        RunWindowAs(hero, () =>
        {
            orig(self);
            ApplyPauseWindowHero(self, hero);
            LogPauseWindow(self);
        });
    }

    /// <summary>
    /// Watches the pause screen's <b>postUpdate</b>, where the flows are actually laid out.
    /// <para>
    /// The item descriptions are re-pointed explicitly, so they do not care, but the mutation bar does:
    /// <c>reflowPerks()</c> marks the flow dirty and the real layout runs later in the frame. By then
    /// <see cref="OnPauseUpdate"/> has already restored the global hero to player one, so the bar was
    /// laid out from player one's mutations — which is exactly what the player saw ("it still shows
    /// P1's, and nothing when P1 has none").
    /// </para>
    /// <para>
    /// Keeping the global on the chosen hero here too is what makes the layout agree with the list it
    /// was given.
    /// </para>
    /// </summary>
    private static void OnPausePostUpdate(Hook_DefaultPause.orig_postUpdate orig, DefaultPause self)
    {
        var hero = Enabled ? ResolveInventoryWindowHero() : null;
        if (hero == null || hero.destroyed)
        {
            orig(self);
            return;
        }

        RunWindowAs(hero, () =>
        {
            orig(self);

            // The mutation bar has to be rebuilt here, in the layout pass, not only when the choice
            // changes in update(): clearing and refilling it works (verified - icons 1 -> 0 -> 1) but
            // the game redraws the bar every frame, so a one-shot rebuild is immediately replaced by
            // one built from the global hero. Doing it here keeps it on the chosen hero.
            //
            // Guarded by the hero it was last built for, so it is not rebuilt 60 times a second.
            if (!ReferenceEquals(_pausePerkBarHero, hero)
                || (self.flowPerks?.children?.length ?? 0) == 0)
            {
                _pausePerkBarHero = hero;
                RebuildPauseMutationBar(self, hero);
            }
        });
    }

    /// <summary>
    /// The hero the pause screen's mutation bar was last built for. Cleared when the window closes
    /// and when the choice changes, so the bar follows the switch without being rebuilt every frame.
    /// </summary>
    private static Hero? _pausePerkBarHero;

    /// <summary>
    /// Re-points the pause screen's four item descriptions and its backpack slot at
    /// <paramref name="hero"/>.
    /// <para>
    /// Pointing <c>Game.ME.hero</c> at the chosen hero is not enough: the screen builds its item
    /// descriptions once and only refreshes the one the cursor is on, so the gear on display kept
    /// belonging to player one. <c>dc.ui.NewItemDesc.setItem()</c> and <c>hud.Skill.useItem()</c>
    /// are the game's own populate calls, so they rebuild the icon, the name, the level and the
    /// numbers exactly as the menu does it itself.
    /// </para>
    /// <para>
    /// Only runs when the chosen hero changes, because a paused game cannot change its own gear and
    /// <c>setItem</c> rebuilds a whole layer tree.
    /// </para>
    /// </summary>
    private static void ApplyPauseWindowHero(DefaultPause pause, Hero hero)
    {
        if (ReferenceEquals(_pauseAppliedHero, hero))
        {
            return;
        }

        _pauseAppliedHero = hero;

        try
        {
            var inventory = hero.inventory;
            if (inventory == null)
            {
                return;
            }

            SetPauseItem(pause.weaLeft, inventory.nbWeapons > 0 ? inventory.getEquippedWeaponOn(0) : null);
            SetPauseItem(pause.weaRight, inventory.nbWeapons > 1 ? inventory.getEquippedWeaponOn(1) : null);
            SetPauseItem(pause.skillLeft, inventory.nbActives > 0 ? inventory.getActiveOn(0) : null);
            SetPauseItem(pause.skillRight, inventory.nbActives > 1 ? inventory.getActiveOn(1) : null);

            try
            {
                pause.skillBackpack?.useItem(inventory.getBackpackWeapon());
            }
            catch (Exception ex)
            {
                LogWarn("[LocalCoop] pause backpack slot refused an item: {Message}", ex.Message);
            }

            // The talisman (ring) slot. It is a separate description (`pause.amulet`) and was simply
            // missed before, which is why a ring picked up by player two showed on player one's bag
            // but never in this window. `getTalisman()` is the game's own lookup — the ring never gets
            // a `posID`, so it cannot be found through the weapon/skill slots.
            try
            {
                SetPauseItem(pause.amulet, inventory.getTalisman());
            }
            catch (Exception ex)
            {
                LogWarn("[LocalCoop] pause ring slot refused an item: {Message}", ex.Message);
            }

            // (The mutation bar is deliberately not rebuilt here. It has to be rebuilt in the layout
            // pass - OnPausePostUpdate - because the game redraws it every frame from the global hero,
            // so a one-shot rebuild here would be immediately replaced. Clearing the guard in
            // SelectInventoryWindowHero is what makes the next layout pass rebuild it.)
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not re-point the pause window's items: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Rebuilds the pause screen's mutation bar for <paramref name="hero"/>.
    /// <para>
    /// The icons are put into the flow by a closure the screen hangs on
    /// <c>flowPerks.onBeforeReflow</c> (<c>__inst_construct___L287_LC270</c> in the decompiled
    /// <c>DefaultPause</c>): it calls <c>reflowPerks()</c> and then walks <c>perks</c>, adding each
    /// icon to the flow. <c>Flow.reflow()</c> invokes that closure on its very first line — and it
    /// resolves the hero through the <b>global</b>, not through the list. That is why every earlier
    /// attempt failed: the list was right, but the builder reading it was not the one being driven.
    /// </para>
    /// <para>
    /// So this empties the flow and then runs a full <c>reflow()</c> — which invokes that closure —
    /// with the global on the chosen hero (<see cref="RunWindowAs"/>). Done right here rather than
    /// marked dirty and left to the engine, because by the time the engine reflows it the global is
    /// player one again.
    /// </para>
    /// </summary>
    private static void RebuildPauseMutationBar(DefaultPause pause, Hero hero)
    {
        try
        {
            RunWindowAs(hero, () =>
            {
                var flow = pause.flowPerks;
                if (flow == null)
                {
                    return;
                }

                // Drop the icons already there: reflow() re-lays-out what it holds and then adds the
                // newly built ones, so the previous hero's would stay on screen alongside them.
                flow.removeChildren();
                pause.perkPage = 0;

                // Flow.reflow() runs onBeforeReflow on its first line, which is the closure that
                // rebuilds the bar and adds the icons. Calling it directly — rather than marking the
                // flow dirty and waiting — is what makes it happen while the global is still the
                // chosen hero, because that closure reads the global.
                flow.reflow();
            });
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not rebuild the pause mutation bar: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Sets one pause item description. A null item is passed through on purpose: that is how the
    /// menu clears a slot the hero has nothing in, and leaving it alone would keep showing the
    /// other hero's item.
    /// </summary>
    private static void SetPauseItem(dc.ui.NewItemDesc? description, dc.tool.InventItem? item)
    {
        if (description == null)
        {
            return;
        }

        try
        {
            description.setItem(item);
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] pause item description refused an item: {Message}", ex.Message);
        }
    }

    /// <summary>Forgets the pause screen and restores the global hero when it closes.</summary>
    private static void OnPauseDispose(Hook_DefaultPause.orig_onDispose orig, DefaultPause self)
    {
        try
        {
            if (ReferenceEquals(_pauseWindow, self))
            {
                CloseInventoryWindow();
            }
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not clear the pause window state: {Message}", ex.Message);
        }

        orig(self);
    }

    // ---------------------------------------------------------------- DebugPopUp

    /// <summary>Same as <see cref="OnPauseUpdate"/>, for the item-swap popup.</summary>
    private static void OnInventoryWindowUpdate(Hook_DebugPopUp.orig_update orig, dc.ui.DebugPopUp self)
    {
        if (!Enabled || _hero1 == null || _hero2 == null)
        {
            orig(self);
            return;
        }

        var hero = ResolveInventoryWindowHero();
        if (hero == null || hero.destroyed)
        {
            orig(self);
            return;
        }

        if (!ReferenceEquals(_inventoryWindow, self))
        {
            _inventoryWindow = self;
            _inventoryWindowHero ??= hero;
            LogInventoryWindowOnce(self);
        }

        RunWindowAs(hero, () => orig(self));
    }

    /// <summary>Forgets the popup and restores the global hero when it closes.</summary>
    private static void OnInventoryWindowClose(Hook_DebugPopUp.orig_close orig, dc.ui.DebugPopUp self)
    {
        try
        {
            if (ReferenceEquals(_inventoryWindow, self))
            {
                CloseInventoryWindow();
            }
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not close the inventory window state: {Message}", ex.Message);
        }

        orig(self);
    }

    /// <summary>Same as <see cref="OnInventoryWindowClose"/>, for the dispose path.</summary>
    private static void OnInventoryWindowDispose(Hook_DebugPopUp.orig_onDispose orig, dc.ui.DebugPopUp self)
    {
        try
        {
            if (ReferenceEquals(_inventoryWindow, self))
            {
                CloseInventoryWindow();
            }
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not dispose the inventory window state: {Message}", ex.Message);
        }

        orig(self);
    }

    // ---------------------------------------------------------------- shared

    /// <summary>
    /// Drops the remembered windows. The chosen hero is deliberately kept: the picker is an
    /// explicit choice, so opening the window again should show the same hero until the player
    /// changes it (or a new run starts).
    /// </summary>
    private static void CloseInventoryWindow()
    {
        _inventoryWindow = null;
        _pauseWindow = null;
        _pauseAppliedHero = null;
        // The bar is rebuilt as the chosen hero on the next open, so the guard must not carry over.
        _pausePerkBarHero = null;
    }

    /// <summary>
    /// Picks which hero the open window shows. Used by the mod-drawn picker overlay.
    /// </summary>
    internal static void SelectInventoryWindowHero(Hero hero)
    {
        if (hero == null || hero.destroyed)
        {
            return;
        }

        if (ReferenceEquals(_inventoryWindowHero, hero))
        {
            return;
        }

        _inventoryWindowHero = hero;
        // Restart the scrolling text from its first item, so the newly shown hero's gear and
        // mutations are read from the beginning instead of from wherever the previous one had
        // scrolled to.
        ResetBackpackScroll();
        // Drop the guard so the mutation bar is rebuilt for the newly chosen hero on the next layout
        // pass; without this it would keep the previous hero's icons.
        _pausePerkBarHero = null;
        Log($"[LocalCoop] window now shows {(ReferenceEquals(hero, _hero2) ? "P2" : "P1")}");
    }
}
