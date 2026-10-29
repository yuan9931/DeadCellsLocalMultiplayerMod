using System;
using dc;
using dc.en;
using dc.en.inter;
using dc.pr;
using dc.tool;
using dc.ui;

namespace DeadCellsLocalMultiplayerMod;

/// <summary>
/// Part of <see cref="LocalCoop"/>: which hero a replaced item is dropped at the feet of.
/// </summary>
/// <remarks>
/// Replacing an equipped item puts the old one back on the ground. The vanilla game builds that drop
/// in two different places, and only one of them knows who acted:
/// <list type="bullet">
/// <item><c>Hero.pickItem(source, item, cb)</c> — the funnel every pickup goes through
/// (<c>ItemDrop.onActivate</c>, the shop / altar / timed-item booths, ...) — spawns the drop at
/// <c>this.cx/cy</c>, i.e. the hero that pressed interact. Per hero, correct on its own.</item>
/// <item>the replace panel <c>pickItem</c> opens when the inventory needs a decision
/// (<c>ui.ReplaceItem</c>, <c>ui.ReplaceSingleItem</c>, <c>ui.ReplaceItemWithDual</c>) spawns it in
/// <c>onValidate()</c> at <c>Game.ME.hero.cx/cy</c> — the <b>global</b> hero, which in this mod is
/// always player one. So player two's replacement dropped the old item at player one's feet, while
/// the very same replacement without a panel dropped it at the acting hero's feet.</item>
/// </list>
/// The panel is a global UI with no hero of its own, so the acting hero is remembered when the pickup
/// starts (<see cref="OnHeroPickItem"/> — every panel is built synchronously inside <c>pickItem</c>,
/// and no other pickup can be started while a panel waits for input) and the panel's
/// <c>onValidate()</c> is then run with <c>game.hero</c> pointed at that hero, exactly like
/// <see cref="RunAsOwningHero"/> does for a detached head. The panel path then agrees with the inline
/// path: the drop lands at the feet of the hero who did the replacing, and <c>dropAndUpdateItem</c> /
/// <c>onEquipedItemsChange</c> run on that hero (the equipment mirror keeps both heroes in sync).
/// <para>
/// Not covered: the pause menu's backpack swap (<c>DefaultPause.startSwapBackpack</c>) also reads
/// <c>Game.ME.hero</c>, but the shared pause menu is opened through the global controller, so which
/// player asked for the swap cannot be told from there; it keeps dropping at player one.
/// </para>
/// </remarks>
internal static partial class LocalCoop
{
    /// <summary>The hero that started the pickup whose replace panel may be waiting for input.</summary>
    private static Hero? _dropActor;

    /// <summary>When that pickup started (<see cref="System.Environment.TickCount64"/>, in ms).</summary>
    private static long _dropActorAt;

    /// <summary>
    /// How long a remembered pickup stays usable. A panel is confirmed within seconds; this only
    /// bounds the record left behind by a panel the player closed without choosing anything.
    /// </summary>
    private const long DropActorWindowMs = 120000;

    /// <summary>
    /// The hero the replace panel that is currently open belongs to.
    /// <para>
    /// This is the panel's own owner, captured when the panel was <b>constructed</b> (which always
    /// happens inside <c>Hero.pickItem</c>, so the picking hero is still known there), and it is what
    /// the panel's <c>onValidate</c> must run as. The older "last pickup anywhere" record
    /// (<see cref="_dropActor"/>) is only a fallback: it can have been overwritten by another pickup
    /// or have aged out, and a panel that drops the replaced item from the <b>wrong hero's</b>
    /// inventory leaves the real owner's bag untouched — which is exactly how the game then refuses
    /// the replacement ("you can't stock Weapon … in full backpack") and takes the run down.
    /// </para>
    /// </summary>
    private static Hero? _panelOwner;

    /// <summary>
    /// Set while a pickup is inside <c>Hero.pickItem</c>, i.e. while the panel it may open is being
    /// constructed: the panel constructor hooks read it and store it as <see cref="_panelOwner"/>.
    /// </summary>
    private static Hero? _pendingPanelOwner;

    /// <summary>The backpack slot id `Inventory.putInBackpack` itself writes (<c>i.posID = 2</c>).</summary>
    private const int BackpackPosId = 2;

    /// <summary>
    /// Registers the drop hooks: the pickup funnel that records the acting hero and the three replace
    /// panels that drop the replaced item. Called from <c>Install</c>.
    /// </summary>
    internal static void InstallDropHooks()
    {
        // Every pickup goes through here, and the replace panel (which drops the old item later on) is
        // built inside it — so this is where the acting hero is still known.
        Hook_Hero.pickItem += OnHeroPickItem;

        // The panels themselves: their vanilla validate() drops at Game.ME.hero (always player one).
        Hook_ReplaceItem.onValidate += OnReplaceItemValidate;
        Hook_ReplaceSingleItem.onValidate += OnReplaceSingleItemValidate;
        Hook_ReplaceItemWithDual.onValidate += OnReplaceItemWithDualValidate;

        // The panels' own constructors, which is the only place that ties a panel to the hero whose
        // pickup opened it (see _panelOwner)...
        Hook__ReplaceItem.__constructor__ += OnReplaceItemCtor;
        Hook__ReplaceSingleItem.__constructor__ += OnReplaceSingleItemCtor;
        Hook__ReplaceItemWithDual.__constructor__ += OnReplaceItemWithDualCtor;

        // ...and their per-frame work, which reads the global hero just as much: while a multi-slot
        // panel is up, `ReplaceItem.update()` builds its item list from `Game.ME.hero.inventory`,
        // decides whether the backpack choice is offered with `hero.inventory.canPutInBackpack(...)`
        // and, when the player picks the backpack, hands the nested single-item panel the item to
        // replace — `Game.ME.hero.inventory.getBackpackWeapon()`. Left on the global (always player
        // one) that produced a panel describing player one's bag: player two saw player one's
        // backpack item offered, and when player one's backpack was empty the nested panel was built
        // with "no item to replace" while player two's backpack was in fact occupied — the stock then
        // hit the game's "full backpack" refusal and took the run down.
        Hook_ReplaceItem.update += (orig, self) => RunAsPanelOwner(() => orig(self));
        Hook_ReplaceItemWithDual.update += (orig, self) => RunAsPanelOwner(() => orig(self));
        Hook_ReplaceSingleItem.update += (orig, self) => RunAsPanelOwner(() => orig(self));
        Hook_ReplaceItem.postUpdate += (orig, self) => RunAsPanelOwner(() => orig(self));
        Hook_ReplaceItemWithDual.postUpdate += (orig, self) => RunAsPanelOwner(() => orig(self));

        // Last line of defence for the backpack stock itself (see OnInventoryPutInBackpack).
        Hook_Inventory.putInBackpack += OnInventoryPutInBackpack;
    }

    /// <summary>
    /// Runs a replace panel's per-frame work with the global hero on the panel's owner, so every
    /// <c>Game.ME.hero.inventory</c> it reads is the right bag. This is a plain swap — unlike
    /// <see cref="RunAsDropActor"/> it does not rebuild the HUD or consume the pickup record, because
    /// it runs on every frame the panel is open.
    /// </summary>
    private static void RunAsPanelOwner(Action body)
    {
        var owner = Enabled ? PanelOwner() : null;
        var game = owner?._level?.game;
        if (owner == null || game == null || ReferenceEquals(game.hero, owner))
        {
            body();
            return;
        }

        var previous = game.hero;
        try
        {
            game.hero = owner;
            body();
        }
        catch (Exception ex)
        {
            // A panel's own update must never be able to take the run down.
            LogWarn("[LocalCoop] a replace panel's update failed: {Message}", ex.Message);
        }
        finally
        {
            game.hero = previous;
        }
    }

    private static void OnReplaceItemCtor(
        Hook__ReplaceItem.orig___constructor__ orig,
        ReplaceItem self,
        Entity source,
        InventItem item,
        HaxeProxy.Runtime.HlAction<bool> cb)
    {
        CapturePanelOwner();
        orig(self, source, item, cb);
    }

    private static void OnReplaceSingleItemCtor(
        Hook__ReplaceSingleItem.orig___constructor__ orig,
        ReplaceSingleItem self,
        Entity source,
        InventItem newItem,
        InventItem oldItem,
        HaxeProxy.Runtime.HlAction<bool> cb,
        HaxeProxy.Runtime.Ref<bool> isBackpack)
    {
        CapturePanelOwner();
        orig(self, source, newItem, oldItem, cb, isBackpack);
    }

    private static void OnReplaceItemWithDualCtor(
        Hook__ReplaceItemWithDual.orig___constructor__ orig,
        ReplaceItemWithDual self,
        Entity source,
        InventItem firstItem,
        InventItem secondItem,
        HaxeProxy.Runtime.HlAction<bool> cb)
    {
        CapturePanelOwner();
        orig(self, source, firstItem, secondItem, cb);
    }

    /// <summary>
    /// Stores the picking hero as the owner of the panel being constructed.
    /// <para>
    /// A panel built while a pickup is in progress takes that pickup's hero. A panel built with no
    /// pickup in flight is the nested single-item panel a multi-slot panel creates when the player
    /// picks the backpack (it is built from inside that outer panel's <c>update</c>), so it inherits
    /// the owner already recorded — the same player, one screen further in.
    /// </para>
    /// </summary>
    private static void CapturePanelOwner()
    {
        var pending = _pendingPanelOwner;
        if (pending != null && !pending.destroyed)
        {
            _panelOwner = pending;
        }
    }

    /// <summary>
    /// The pickup funnel, which does two jobs.
    /// <para>
    /// First, it remembers which hero is picking an item up. The replace panel it may open is a
    /// global UI that resolves the dropper through <c>Game.ME.hero</c>, so this is the only place
    /// where the hero that acted can still be seen.
    /// </para>
    /// <para>
    /// Second, it keeps the pickup off player one's HUD. Picking an item up rebuilds the HUD item
    /// slots, and from the <b>global</b> HUD's point of view "the hero" is always player one, so
    /// with independent equipment player two's pickup must not repaint player one's slots — its own
    /// readout already shows what it holds. The pickup itself is never dropped, only the HUD part:
    /// the original runs as normal and then the HUD is immediately repainted from player one's
    /// inventory, so player two's new item never appears on player one's backpack screen.
    /// </para>
    /// </summary>
    /// <summary>
    /// Guards against the pickup re-entering itself. A replace panel's <c>onValidate()</c> runs a
    /// pickup, which can run another panel's validate, which runs another pickup — the crash log
    /// showed three nested <c>pickItem</c> frames. Depth is tracked so the nested calls take the
    /// plain path instead of repeating the HUD work.
    /// </summary>
    private static int _pickItemDepth;

    private static void OnHeroPickItem(
        Hook_Hero.orig_pickItem orig,
        Hero self,
        Entity source,
        InventItem item,
        HaxeProxy.Runtime.HlAction<bool> endCb)
    {
        // A pickup can start another pickup: the replace panel's onValidate() runs one, and that can
        // open another panel. Only the outermost call does the bookkeeping; nested calls just run the
        // game's own code, which is what keeps this from compounding.
        if (_pickItemDepth > 0)
        {
            orig(self, source, item, endCb);
            return;
        }

        _pickItemDepth++;
        try
        {
            PickItemCore(orig, self, source, item, endCb);
        }
        finally
        {
            _pickItemDepth--;
        }
    }


    /// <summary>
    /// Rewrites an inventory's weapon/active counts from what it actually holds.
    /// <para>
    /// <c>Inventory.unserializeInit()</c> seeds both counts with <c>99999</c> and only the managers
    /// replace them — <c>HeroWeaponsManager.init()</c> assigns <c>inventory.nbWeapons =
    /// mainWeapons.length</c>, and the skills manager does the equivalent for actives. Player two's
    /// cloned bag is never put through that, so it keeps the sentinel.
    /// </para>
    /// <para>
    /// The sentinel is not harmless: <c>Inventory.add()</c> sets <c>posID = -1</c> and then calls
    /// <c>equip()</c>, which needs a real count to choose a free slot. The item then keeps
    /// <c>posID = -1</c>, and <c>HUD.onItemPick</c> throws <c>unexpected posId</c> on a negative id —
    /// which is the pickup crash. It also breaks the cooldown/ammo display, which reads the same
    /// slots.
    /// </para>
    /// </summary>
    private static void RepairInventoryCounts(Hero? hero, string where)
    {
        if (hero == null || hero.destroyed)
        {
            return;
        }

        try
        {
            var inventory = hero.inventory;
            if (inventory == null)
            {
                return;
            }

            var weapons = hero.weaponsManager?.mainWeapons?.length ?? 0;
            var actives = hero.activeSkillsManager?.activeSkills?.length ?? 0;

            var fixedW = inventory.nbWeapons != weapons;
            var fixedA = inventory.nbActives != actives;

            if (fixedW)
            {
                inventory.nbWeapons = weapons;
            }
            if (fixedA)
            {
                inventory.nbActives = actives;
            }

            if (fixedW || fixedA)
            {
                // Only when something actually changed, so the per-frame re-assertion stays quiet.
                Log($"[LocalCoop] repaired player two's inventory counts ({where}): "
                    + $"nbWeapons={inventory.nbWeapons}, nbActives={inventory.nbActives}");
            }
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not repair the inventory counts: {Message}", ex.Message);
        }
    }

    private static void PickItemCore(
        Hook_Hero.orig_pickItem orig,
        Hero self,
        Entity source,
        InventItem item,
        HaxeProxy.Runtime.HlAction<bool> endCb)
    {
        try
        {
            RememberDropActor(self);
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] drop actor hook failed: {Message}", ex.Message);
        }

        // Independent equipment: rebuild the HUD's slots from the hero that is picking up.
        //
        // This is the real fix, and it is what makes player two behave exactly like player one.
        // `HUD.initItems` builds `skillWeapons` / `skillPowers` from **`game.hero`** (HUD.cs:1927 and
        // :1974 read `game.hero.weaponsManager` / `game.hero.activeSkillsManager`), while
        // `HUD.onItemPick` indexes those arrays by the picked item's `posID` — an id that belongs to
        // the inventory the item came from. Player one works because both sides agree: the HUD was
        // built from player one's bag and the item's id comes from the same bag. With independent
        // equipment player two's bag is a clone numbered on its own, so an item picked up by player
        // two is indexed against arrays built from player one's gear and the id lands out of range —
        // `unexpected posId`.
        //
        // Pointing `game.hero` at the picking hero for the duration makes both sides come from the
        // same inventory again, which is precisely player one's situation. Restored in a `finally`.
        var game = Game.Class.ME;
        var switchedHero = false;
        Hero? previousHero = null;

        // Repair player two's inventory counts before the pickup.
        //
        // The trace showed `P2W=99999 P2A=99999` and `item=Active@-1 posID=-1`. 99999 is the sentinel
        // `Inventory.unserializeInit()` writes, and it is only replaced when the manager re-inits
        // (`HeroWeaponsManager.init()` does `inventory.nbWeapons = mainWeapons.length`). Player two's
        // cloned bag never went through that, so it still reads the sentinel.
        //
        // That is what crashes the pickup: `Inventory.add()` sets `posID = -1` and then calls
        // `equip()`, which needs a sane weapon/active count to find a free slot. With the sentinel in
        // place the item never gets a slot, `posID` stays -1, and `HUD.onItemPick` rejects a negative
        // posID outright (`if (posID < 0) throw "unexpected posId"`).
        if (!SharedEquipment)
        {
            RepairInventoryCounts(_hero2, "pickup");
        }

        if (game != null && !SharedEquipment && ReferenceEquals(self, _hero2)
            && _hero1 != null && !_hero1.destroyed)
        {
            try
            {
                previousHero = game.hero;
                if (!ReferenceEquals(previousHero, self))
                {
                    game.hero = self;
                    switchedHero = true;

                    // Rebuild now, so the arrays the pickup will index are player two's.
                    game.hud?.initItems(self);
                }
            }
            catch (Exception ex)
            {
                LogWarn("[LocalCoop] could not rebuild the HUD for P2's pickup: {Message}", ex.Message);
            }
        }

        try
        {
            // The panels this pickup may open are constructed inside `orig`, and their constructors
            // read this to learn whose pickup they belong to.
            _pendingPanelOwner = _panelOwner = null;
            _pendingPanelOwner = self;
            orig(self, source, item, endCb);
        }
        catch (Exception ex)
        {
            // Last line of defence: the pickup has already been applied by this point, so swallowing
            // the HUD-side failure keeps the run alive and the repaint below still runs.
            LogWarn("[LocalCoop] pickup failed while rebuilding the HUD: {Message}", ex.Message);
        }
        finally
        {
            _pendingPanelOwner = null;
            if (switchedHero)
            {
                try
                {
                    game!.hero = previousHero!;
                }
                catch (Exception ex)
                {
                    LogWarn("[LocalCoop] could not restore the global hero after a pickup: {Message}", ex.Message);
                }
            }
        }

        // Make the new item actually count for player two.
        //
        // The pickup runs as player one (see above), so the game's own post-pickup work —
        // `computeTiers()` among it — ran on player one's inventory. Player two's copy therefore has
        // the item in `items` but none of its effects: a talisman is the clearest case, because
        // `Inventory.equip()` returns early for it (`group == 7` never gets a slot, so `posID` stays
        // -1) and its contribution only lands through `computeTiers()` reading the whole bag.
        // Recomputing for player two is what makes the ring (and any other pickup) take effect.
        if (!SharedEquipment && ReferenceEquals(self, _hero2) && _hero2 != null && !_hero2.destroyed)
        {
            try
            {
                RepairInventoryCounts(_hero2, "post-pickup");
                _hero2.computeTiers();

                // Skills key off the item instances they were built from, so a bag change needs them
                // rebuilt or the new item never fires.
                RefreshSecondHeroSkills(_hero2);
            }
            catch (Exception ex)
            {
                LogWarn("[LocalCoop] could not apply P2's pickup to its own gear: {Message}", ex.Message);
            }
        }

        if (P2InventoryMayWriteSharedHud(self))
        {
            return;
        }

        try
        {
            var hud = Game.Class.ME?.hud;
            hud?.initItems(_hero1);
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not repaint P1's HUD after P2 picked up an item: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Records the hero a pickup belongs to. A hero that is not one of ours (and a session that is
    /// off) clears the record instead, so the panels keep the vanilla behaviour there.
    /// </summary>
    private static void RememberDropActor(Hero? hero)
    {
        if (!Enabled || hero == null || hero.destroyed
            || (!ReferenceEquals(hero, _hero1) && !ReferenceEquals(hero, _hero2)))
        {
            DropActorUsed();
            return;
        }

        _dropActor = hero;
        _dropActorAt = System.Environment.TickCount64;
    }

    /// <summary>The remembered pickup hero, if it is still one of our heroes and recent enough.</summary>
    private static Hero? DropActor()
    {
        var actor = _dropActor;
        if (actor == null || actor.destroyed
            || (!ReferenceEquals(actor, _hero1) && !ReferenceEquals(actor, _hero2)))
        {
            DropActorUsed();
            return null;
        }

        if (System.Environment.TickCount64 - _dropActorAt > DropActorWindowMs)
        {
            DropActorUsed();
            return null;
        }

        return actor;
    }

    /// <summary>Forgets the remembered pickup (it has been consumed, or it was not usable).</summary>
    private static void DropActorUsed()
    {
        _dropActor = null;
        _dropActorAt = 0;
    }

    /// <summary>
    /// Runs a replace panel's <c>onValidate()</c> with <c>game.hero</c> pointed at the hero that
    /// started the pickup, so the replaced item is dropped from <b>that</b> hero's bag and at its
    /// feet — the panel itself drops through <c>Game.ME.hero</c>, which this mod keeps on player one.
    /// The swap is restored in a <c>finally</c>, so a failure inside the panel cannot leave the global
    /// hero pointing at player two.
    /// <para>
    /// <b>The swap is what keeps the two halves of the panel consistent, and getting it wrong is what
    /// crashed the game.</b> A panel drops the replaced item (through the global) and then runs the
    /// pickup continuation (bound to the hero that opened it) — and the "full backpack" guard between
    /// them reads the bag of the hero being stocked. If the drop misses the stocking hero's bag, the
    /// replaced weapon is still there when the new one is stocked and the game throws
    /// <c>you can't stock Weapon … in full backpack</c> out of <c>Inventory.putInBackpack</c>, which
    /// was fatal here. The swap therefore has to happen, and it has to happen on the panel's own owner
    /// rather than on a record of "the last pickup", which can be minutes stale by the time the player
    /// confirms a panel — see <see cref="_panelOwner"/>.
    /// </para>
    /// <para>
    /// The HUD rebuild below is only for the pickup the panel runs: it indexes the HUD's slot arrays
    /// by each item's <c>posID</c>, and player two's bag numbers its items on its own. It is wrapped,
    /// so an out-of-range id costs a log line rather than the run.
    /// </para>
    /// </summary>
    private static void RunAsDropActor(Action body, InventItem? itemHint = null)
    {
        // The panel's own owner wins: it was recorded when the panel was built, inside that hero's
        // pickup, so it cannot be a stale "last pickup" record (see _panelOwner). When that record is
        // missing, the item the panel is about to replace identifies its owner just as well.
        var actor = Enabled ? (PanelOwner() ?? OwnerOfItem(itemHint) ?? DropActor()) : null;
        var game = actor?._level?.game ?? _hero1?._level?.game;
        if (actor == null || game == null)
        {
            LogVerbose("[LocalCoop][drop] replace panel: no remembered pickup, keeping the global hero");
            body();
            return;
        }

        if (ReferenceEquals(game.hero, actor))
        {
            LogVerbose($"[LocalCoop][drop] replace panel: {HeroName(actor)} drops at own feet "
                + $"(case {actor.cx},{actor.cy})");
            body();
            DropActorUsed();
            return;
        }

        var previous = game.hero;
        try
        {
            Log($"[LocalCoop][drop] replace panel: running as {HeroName(actor)} "
                + $"(global hero was {HeroName(previous)}), drop at case {actor.cx},{actor.cy}");
            game.hero = actor;

            // Rebuild the slots from the acting hero, for the same reason the pickup hook does: the
            // panel's own onValidate() runs a pickup, which indexes the arrays by the item's posID
            // from this hero's inventory. Without this the arrays still describe the previous hero
            // and the index can be out of range.
            try
            {
                game.hud?.initItems(actor);
            }
            catch (Exception ex)
            {
                LogWarn("[LocalCoop] could not rebuild the HUD for the replace panel: {Message}", ex.Message);
            }

            body();
        }
        finally
        {
            game.hero = previous;
            DropActorUsed();
        }
    }

    private static void OnReplaceItemValidate(Hook_ReplaceItem.orig_onValidate orig, ReplaceItem self)
        => RunPanelValidate(() => orig(self), self);

    private static void OnReplaceSingleItemValidate(
        Hook_ReplaceSingleItem.orig_onValidate orig,
        ReplaceSingleItem self)
        => RunPanelValidate(() => orig(self), self, self.oldItem);

    private static void OnReplaceItemWithDualValidate(
        Hook_ReplaceItemWithDual.orig_onValidate orig,
        ReplaceItemWithDual self)
        => RunPanelValidate(() => orig(self), self);

    /// <summary>
    /// Runs a replace panel's confirm as the hero that opened it, and never lets it take the run down.
    /// <para>
    /// The panel's <c>onValidate</c> both drops the replaced item and runs the pickup that stocks the
    /// new one, so a game-side refusal raised in the middle of it (the "you can't stock … in full
    /// backpack" error, which the game throws instead of handling) used to escape this hook — and an
    /// exception thrown inside a hook is fatal to the game. That is precisely what a player-two
    /// backpack replacement hit. The panel is closed and the game resumed instead, so the worst case
    /// is a replacement that did not happen rather than a lost run.
    /// </para>
    /// </summary>
    private static void RunPanelValidate(Action body, dc.libs.Process panel, InventItem? itemHint = null)
    {
        LogPanelState(itemHint);
        try
        {
            RunAsDropActor(body, itemHint);
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] the replace panel's confirm failed; closing it to keep the run alive: {Message}",
                ex.Message);
            ClosePanelAfterFailure(panel);
        }
    }

    /// <summary>
    /// Logs what a replace panel is about to do: whose bag it belongs to, the item it replaces and
    /// the item it stocks, with both ids and the state of the backpack slot. A replacement that goes
    /// wrong (an item that vanishes instead of dropping, a stock the game refuses) is decided here,
    /// and this line is what says which of those happened.
    /// </summary>
    private static void LogPanelState(InventItem? oldItem)
    {
        if (!Logging)
        {
            return;
        }

        try
        {
            var owner = PanelOwner() ?? OwnerOfItem(oldItem) ?? DropActor();
            var bag = owner?.inventory;
            var occupant = bag?.getBackpackWeapon();
            Log($"[LocalCoop][drop] panel confirm: owner={HeroName(owner)}, "
                + $"old={ItemName(oldItem)}@{oldItem?.posID ?? -1}, "
                + $"backpack={(bag?.hasWeaponInBackpack() == true ? ItemName(occupant) : "-")}, "
                + $"nbW={bag?.nbWeapons ?? -1}, nbA={bag?.nbActives ?? -1}");
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not describe the replace panel: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Best-effort undo of the modal state a replace panel leaves behind when its confirm threw: the
    /// panel is opened with <c>modalPause</c> and a hidden HUD, and its own tail (which would resume
    /// the game and restore the HUD) never ran because the exception escaped <c>onValidate</c>.
    /// </summary>
    private static void ClosePanelAfterFailure(dc.libs.Process panel)
    {
        // Dispose the panel first: its own onDispose releases the controller access it took and shows
        // the HUD again (it was hidden by the panel's constructor, together with the modal pause).
        try
        {
            panel.disposeImmediately();
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not dispose the failed replace panel: {Message}", ex.Message);
            try
            {
                panel.destroyed = true;
            }
            catch (Exception ex2)
            {
                LogWarn("[LocalCoop] could not close the failed replace panel: {Message}", ex2.Message);
            }
        }

        try
        {
            Game.Class.ME?.resume();
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not resume the game after the replace panel failed: {Message}", ex.Message);
        }

        _panelOwner = null;
    }

    /// <summary>
    /// The hero a replace panel is currently owned by, if it is still one of ours and alive.
    /// </summary>
    private static Hero? PanelOwner()
    {
        var owner = _panelOwner;
        if (owner == null || owner.destroyed
            || (!ReferenceEquals(owner, _hero1) && !ReferenceEquals(owner, _hero2)))
        {
            _panelOwner = null;
            return null;
        }

        return owner;
    }

    /// <summary>
    /// The hero whose bag holds <paramref name="item"/>, or null when neither does.
    /// <para>
    /// <c>Inventory.isEquiped</c> is the game's own identity test (it compares the item against the
    /// equipped weapons, the backpack and the actives), so this does not depend on comparing Haxe
    /// proxies from C#. With shared equipment both heroes hold the same bag and player one answers
    /// first, which is the right owner for a shared purse.
    /// </para>
    /// </summary>
    private static Hero? OwnerOfItem(InventItem? item)
    {
        if (!Enabled || item == null)
        {
            return null;
        }

        foreach (var hero in new[] { _hero1, _hero2 })
        {
            if (hero == null || hero.destroyed)
            {
                continue;
            }

            try
            {
                if (hero.inventory?.isEquiped(item) == true)
                {
                    return hero;
                }
            }
            catch (Exception ex)
            {
                LogWarn("[LocalCoop] could not ask a bag about the replaced item: {Message}", ex.Message);
            }
        }

        return null;
    }

    /// <summary>
    /// Keeps the backpack stock from turning into a fatal error, without ever destroying an item.
    /// <para>
    /// <c>Inventory.putInBackpack</c> refuses outright whenever the backpack slot is still occupied —
    /// every branch of its guard throws — and it is only reached when the incoming item has already
    /// been assigned the backpack slot (<c>posID == 2</c>), i.e. by the replace flow the player
    /// already confirmed. If the replaced weapon is still in the bag at that moment (the panel's own
    /// drop ran somewhere else, or the slot holds a leftover), the occupant is <b>dropped on the
    /// ground</b> the way the replace panel itself drops a replaced item — never just deleted, which
    /// is what made player two's old backpack weapon vanish instead of landing at its feet.
    /// </para>
    /// <para>
    /// Only an occupant whose <c>posID</c> is the backpack's own id is touched, so an equipped weapon
    /// can never be dropped by this.
    /// </para>
    /// </summary>
    private static InventItem OnInventoryPutInBackpack(
        Hook_Inventory.orig_putInBackpack orig,
        Inventory self,
        InventItem item)
    {
        try
        {
            if (Enabled && item != null && item.posID == BackpackPosId && self.hasWeaponInBackpack())
            {
                var occupant = self.getBackpackWeapon();
                if (occupant != null && !ReferenceEquals(occupant, item) && occupant.posID == BackpackPosId)
                {
                    Log($"[LocalCoop][drop] the backpack still held {ItemName(occupant)}; dropping it so "
                        + $"{ItemName(item)} could take the slot");
                    DropOccupant(self, occupant);
                }
                else if (occupant != null && !ReferenceEquals(occupant, item))
                {
                    LogWarn("[LocalCoop] the backpack guard is reading slot {Slot} (an equipped weapon), "
                        + "not the backpack slot; the stock may still fail", self.nbWeapons);
                }
            }
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not clear the backpack occupant: {Message}", ex.Message);
        }

        return orig(self, item);
    }

    /// <summary>
    /// Takes a leftover backpack occupant out of its bag and puts it on the ground, mirroring the
    /// replace panel's own drop (<c>Hero.dropAndUpdateItem</c> + a ground <see cref="ItemDrop"/>) so
    /// the managers / HUD follow and the player keeps the item.
    /// </summary>
    private static void DropOccupant(Inventory bag, InventItem occupant)
    {
        var hero = HeroOf(bag);
        if (hero == null || hero._level == null)
        {
            // No hero to drop for: at least take it out of the bag, which is what the game's own
            // remove does (it clears the id too, so the stock can proceed).
            bag.remove(occupant);
            return;
        }

        var dropped = hero.dropAndUpdateItem(occupant);
        if (dropped == null)
        {
            return;
        }

        var inArmory = HaxeProxy.Runtime.Ref<bool>.DontCare;
        new ItemDrop(hero._level, hero.cx, hero.cy, dropped, false, inArmory).init();
    }

    /// <summary>The hero whose bag this is, or null when it belongs to neither of ours.</summary>
    private static Hero? HeroOf(Inventory bag)
    {
        foreach (var hero in new[] { _hero1, _hero2 })
        {
            if (hero != null && !hero.destroyed && ReferenceEquals(hero.inventory, bag))
            {
                return hero;
            }
        }

        return null;
    }

    /// <summary>An item's display name for the log, never throwing.</summary>
    private static string ItemName(InventItem? item)
    {
        if (item == null)
        {
            return "-";
        }

        try
        {
            var neutral = false;
            var name = item.getOnlyName(ref neutral);
            var text = name?.ToString();
            return string.IsNullOrWhiteSpace(text) ? "?" : text;
        }
        catch
        {
            return "?";
        }
    }
}
