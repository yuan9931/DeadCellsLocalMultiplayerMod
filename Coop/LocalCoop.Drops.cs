using System;
using dc;
using dc.en;
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
    /// started the pickup, so the replaced item is dropped at <b>that</b> hero's feet instead of at
    /// player one's — the panels read <c>Game.ME.hero</c>, which is always player one here. The swap
    /// is restored in a <c>finally</c>, so a failure inside the panel cannot leave the global hero
    /// pointing at player two.
    /// <para>
    /// <b>Independent equipment is the exception, and it is what crashed the game.</b> The panel's
    /// <c>onValidate()</c> runs a pickup, which ends in <c>HUD.initItems(hero)</c> — and that walks the
    /// hero's inventory indexing the HUD's slot arrays by each item's <c>posID</c>, throwing
    /// <c>unexpected posId</c> when the index is out of range. Player two's cloned inventory numbers
    /// its items on its own, so pointing the global at player two for the panel made that rebuild
    /// index player one's arrays out of range. With two backpacks the global therefore stays on player
    /// one here; the drop position is the only thing that would have benefited from the swap, and a
    /// crash is far worse than an item landing a cell or two off.
    /// </para>
    /// </summary>
    private static void RunAsDropActor(Action body)
    {
        var actor = Enabled ? DropActor() : null;
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
        => RunAsDropActor(() => orig(self));

    private static void OnReplaceSingleItemValidate(
        Hook_ReplaceSingleItem.orig_onValidate orig,
        ReplaceSingleItem self)
        => RunAsDropActor(() => orig(self));

    private static void OnReplaceItemWithDualValidate(
        Hook_ReplaceItemWithDual.orig_onValidate orig,
        ReplaceItemWithDual self)
        => RunAsDropActor(() => orig(self));
}
