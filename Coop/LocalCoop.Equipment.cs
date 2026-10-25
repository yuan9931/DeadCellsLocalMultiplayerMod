using dc.en;
using dc.pr;
using dc.tool;
using HaxeProxy.Runtime;
using ModCore.Utilities;

namespace DeadCellsLocalMultiplayerMod;

/// <summary>
/// Part of <see cref="LocalCoop"/>: shared versus independent equipment.
/// <para>
/// By default both heroes share one <c>Inventory</c> instance, which is what makes weapons,
/// skills, flask charges, scroll tiers and unlocks identical. With <see cref="SharedEquipment"/>
/// off, player two carries its own inventory instead, so each hero has its own weapons, skills,
/// flask charges, scroll tiers, curse and infection.
/// </para>
/// <para>
/// Two things stay shared in both modes: <c>blueprints</c> (unlocks are run-wide progress, not
/// gear) and gold / cells — those live on the <c>Hero</c>, not in the inventory, so splitting the
/// backpack does not split the economy.
/// </para>
/// </summary>
internal static partial class LocalCoop
{
    /// <summary>
    /// Player two's own inventory while <see cref="SharedEquipment"/> is off, or null when it is
    /// sharing player one's. Kept so re-applying the mode is idempotent — cloning twice would
    /// throw the gear player two picked up in between away.
    /// </summary>
    private static Inventory? _hero2OwnInventory;

    /// <summary>
    /// Set while player two is the hero <see cref="SpawnSecondHero"/> just built, and cleared once
    /// the equipment mode has been applied to it.
    /// <para>
    /// It exists because of where a fresh hero's bag comes from: <c>Hero</c>'s constructor assigns
    /// <c>game.data.inventory.clone()</c>, and <c>game.data.inventory</c> is itself a clone of
    /// <b>player one's</b> bag (<c>GameData.sync</c>). So a hero the mod just created carries a copy
    /// of player one's gear — never player two's — and that copy must not be mistaken for a bag
    /// restored from a save (see <see cref="AdoptLoadedInventory"/>).
    /// </para>
    /// </summary>
    private static bool _hero2FreshlySpawned;

    /// <summary>
    /// Adopts the inventory a hero loaded from a save already carries, for independent equipment.
    /// <para>
    /// The game serializes each hero's <c>inventory</c> (and its weapon/active managers) as part of
    /// the hero entity, with identity de-duplication. That means a saved run already restores player
    /// two's own, correctly-numbered bag when the level deserialises the second hero — the adoption
    /// path in <c>AdoptExistingHeroes</c> finds it there already. Nothing needs to be rebuilt or
    /// re-serialised; the mod only has to *stop overwriting it*.
    /// </para>
    /// <para>
    /// An earlier design did overwrite it: it captured the bag through the game's save serialiser
    /// (<c>dc.hxbit.Serializer</c>) every frame and replayed it on load. That caused two failures.
    /// Calling the save serialiser on live game objects every frame poisoned the global class-id /
    /// uid state the real save relies on, so the next <c>Save.tryLoad</c> mis-read a class id and
    /// crashed with <c>Can't cast tool.InventItem to Entity</c> (a stack with no mod frames in it).
    /// And because the bytes were keyed by save slot, a *new* game started in a slot that previously
    /// held a co-op run restored that old run's gear. Both are gone now that the snapshot does not
    /// exist.
    /// </para>
    /// <para>
    /// Returns true when a distinct loaded bag was adopted (so the caller must not clone over it).
    /// </para>
    /// </summary>
    private static bool AdoptLoadedInventory(Hero hero1, Hero hero2)
    {
        // A hero this mod just constructed is not carrying a saved bag: its constructor filled the
        // bag with a clone of `game.data.inventory`, i.e. of player one's current gear. Adopting
        // that is what silently gave player two player one's equipment after a level transition
        // that destroys hero two (the forge levels do) — see GiveOwnInventory.
        if (_hero2FreshlySpawned)
        {
            LogVerbose("[LocalCoop] not adopting player two's bag: the hero was just re-created, "
                + "so its bag is a copy of player one's");
            return false;
        }

        var bag = hero2.inventory;

        // No bag, or still the same object player one holds (a genuinely shared instance, not an
        // independent one): nothing to adopt, let the caller fall back to the clone path.
        if (bag == null || ReferenceEquals(bag, hero1.inventory))
        {
            return false;
        }

        _hero2OwnInventory = bag;

        // A loaded bag's counts are written by the game's own reader, but repair anyway: it is
        // idempotent, and a stale 99999 sentinel here would crash the first pickup.
        RepairInventoryCounts(hero2, "adopted bag");

        Log($"[LocalCoop] adopted player two's own bag from the save ({bag.items?.length ?? -1} items)");
        return true;
    }

    /// <summary>
    /// Puts hero two's inventory in the state <see cref="SharedEquipment"/> asks for. Called when
    /// the setting is toggled and when hero two is set up. Does nothing when the heroes already
    /// hold the right instances, and nothing at all outside a run (the mode is then applied when
    /// hero two is spawned or adopted).
    /// </summary>
    internal static void ApplyEquipmentMode()
    {
        var hero1 = _hero1;
        var hero2 = _hero2;
        if (hero1 == null || hero2 == null || hero1.destroyed || hero2.destroyed)
        {
            return;
        }

        if (SharedEquipment)
        {
            ShareInventory(hero1, hero2);
        }
        else
        {
            GiveOwnInventory(hero1, hero2);
        }
    }

    /// <summary>Hands player one's inventory instance back to hero two.</summary>
    private static void ShareInventory(Hero hero1, Hero hero2)
    {
        if (hero1.inventory == null)
        {
            return;
        }
        if (ReferenceEquals(hero2.inventory, hero1.inventory))
        {
            return;
        }

        try
        {
            hero2.inventory = hero1.inventory;
            hero2.blueprints = hero1.blueprints;
            _hero2OwnInventory = null;
            RebuildSecondHeroLoadout(hero2);
            Log("[LocalCoop] hero two shares player one's inventory again");
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not re-share player one's inventory: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Gives hero two its own inventory: the bag it already had when the level transition re-created
    /// the hero, the bag a save restored, or — on a fresh run — a clone of player one's, so player
    /// two starts with a copy of the starting gear rather than bare-handed. Clones are made on the
    /// Haxe side (<c>Inventory.clone()</c>) — C# must never construct Haxe collections here.
    /// </summary>
    private static void GiveOwnInventory(Hero hero1, Hero hero2)
    {
        try
        {
            GiveOwnInventoryCore(hero1, hero2);
        }
        finally
        {
            // Consumed: whatever this hero's bag turned out to be, it is settled now.
            _hero2FreshlySpawned = false;
        }
    }

    private static void GiveOwnInventoryCore(Hero hero1, Hero hero2)
    {
        if (_hero2OwnInventory != null && ReferenceEquals(hero2.inventory, _hero2OwnInventory))
        {
            return;
        }

        // Hero two was re-created (a level transition destroyed it and SpawnSecondHero built a new
        // one): hand back the bag it had, instead of the copy of player one's gear its constructor
        // put there. This is the "independent equipment, and entering the forge turned player two's
        // gear into player one's" bug: the forge transition destroys hero two, the fresh hero gets
        // `game.data.inventory.clone()` (= P1's bag), and the adopt path below accepted that copy as
        // if it had been restored from a save.
        if (RestoreOwnInventory(hero2))
        {
            return;
        }

        // A loaded run's hero already carries its own bag AND its own backpack/weapon/skill
        // managers — the game serializes each hero's `inventory`, `weaponsManager` and
        // `activeSkillsManager` with per-object identity de-duplication, and in independent-equipment
        // mode those are distinct objects for the two heroes, so the save restores both the equipped
        // slots (mainWeapons / activeSkills) and the backpack slot (backpackWeapons) exactly as that
        // run left them. Adopt them as-is. Rebuilding here (`RebuildSecondHeroLoadout`) would run
        // `killEntities` + `initSkills` over managers the game just correctly restored — the same
        // kind of extra step that previously produced a corrupt bag — so it is deliberately skipped.
        // A fresh run has no distinct bag yet and falls through to the clone of player one's below.
        if (AdoptLoadedInventory(hero1, hero2))
        {
            return;
        }

        // Only reached when there is no loaded bag to adopt: a fresh run. Both heroes then start from
        // the same cloned bag and diverge from there.
        LogVerbose("[LocalCoop] no gear to restore for player two; cloning player one's bag");

        try
        {
            var clone = hero1.inventory?.clone();
            if (clone == null)
            {
                LogWarn("[LocalCoop] inventory clone returned nothing; hero two keeps sharing player one's");
                return;
            }

            hero2.inventory = clone;
            // Unlocks are not gear: both heroes keep the same blueprint set.
            hero2.blueprints = hero1.blueprints;
            _hero2OwnInventory = clone;
            RebuildSecondHeroLoadout(hero2);
            Log("[LocalCoop] hero two got its own inventory (cloned from player one's)");
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not give hero two its own inventory: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Remembers the bag hero two is carrying, so the hero that replaces it can be handed the same
    /// gear. Called from the level-change safety net right before the run state is dropped — a
    /// transition can destroy hero two while the run goes on, and a freshly built hero would
    /// otherwise start from a copy of player one's bag (see <see cref="RestoreOwnInventory"/>).
    /// </summary>
    private static void RememberHero2Bag(Hero hero2, Hero hero1)
    {
        try
        {
            var bag = hero2.inventory;
            if (bag == null || ReferenceEquals(bag, hero1.inventory))
            {
                // Shared bag: there is nothing of player two's own to keep.
                return;
            }

            _hero2OwnInventory = bag;
            Log($"[LocalCoop] kept player two's bag for the respawn ({bag.items?.length ?? -1} items)");
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not remember player two's bag: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Hands player two the bag it was carrying before the level transition destroyed its hero, and
    /// rebuilds its weapon/skill runtime from it.
    /// <para>
    /// <b>Why this is needed at all:</b> a level change usually moves hero two (see
    /// <c>MoveSecondHero</c>), but some transitions destroy it instead and the mod re-creates it
    /// (<c>SpawnSecondHero</c>). The forge levels do this — the boss-room forge is entered through a
    /// transition that tears the old level down before <c>initHero</c> runs, so hero two is gone by
    /// the time the mod can move it. A re-created <c>Hero</c> does not inherit anything: its
    /// constructor assigns <c>game.data.inventory.clone()</c>, and <c>game.data.inventory</c> is a
    /// clone of <b>player one's</b> bag (<c>GameData.sync</c>). Player two therefore came back
    /// holding player one's weapons, skills and scroll tiers.
    /// </para>
    /// <para>
    /// The old bag itself survives: <c>Hero.dispose()</c> disposes the controllers and the three
    /// managers but never the inventory, and <c>_hero2OwnInventory</c> still points at it. Handing
    /// it back (then rebuilding the loadout, exactly like the clone path does) restores player two's
    /// own gear. It is validated first — a bag that no longer reads sanely is dropped rather than
    /// handed to a live hero.
    /// </para>
    /// </summary>
    private static bool RestoreOwnInventory(Hero hero2)
    {
        var bag = _hero2OwnInventory;
        if (bag == null || ReferenceEquals(bag, hero2.inventory))
        {
            return false;
        }

        if (!InventoryLooksUsable(bag))
        {
            LogWarn("[LocalCoop] player two's remembered bag no longer reads sanely; "
                + "falling back to a copy of player one's gear");
            _hero2OwnInventory = null;
            return false;
        }

        try
        {
            hero2.inventory = bag;
            // The bag is the live one player two was already using, so its own counts are correct —
            // only the runtime built around it has to be rebuilt (this is the same reconcile the
            // clone path runs).
            RebuildSecondHeroLoadout(hero2);
            Log($"[LocalCoop] gave player two its own bag back after the hero was re-created "
                + $"({bag.items?.length ?? -1} items)");
            return true;
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not hand player two its own bag back: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// A cheap sanity read of a bag the mod has held on to across a hero's destruction: it must
    /// still list real items and carry plausible weapon/active counts. <c>99999</c> is the sentinel
    /// <c>Inventory.unserializeInit</c> leaves behind (see <see cref="RepairInventoryCounts"/>), so a
    /// bag showing it was never finished being built and is not safe to reuse.
    /// </summary>
    private static bool InventoryLooksUsable(Inventory bag)
    {
        try
        {
            var items = bag.items;
            if (items == null)
            {
                return false;
            }

            if (bag.nbWeapons < 0 || bag.nbActives < 0 || bag.nbWeapons > 64 || bag.nbActives > 64)
            {
                return false;
            }

            for (var i = 0; i < items.length; i++)
            {
                if (items.getDyn(i) is not InventItem)
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not read player two's remembered bag: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Rebuilds hero two's per-hero runtime from the inventory it now holds.
    /// <para>
    /// <c>HeroActiveSkillsManager.init()</c> creates one <c>OldSkill</c> per hero per slot, and
    /// the manager keys them by the item instances it was built from — so a new inventory needs
    /// the skill objects rebuilt, otherwise <c>getActiveSkill()</c> keeps returning the ones bound
    /// to the old items (or nothing at all) and player two's skills never fire.
    /// </para>
    /// <para>
    /// Deliberately does <b>not</b> go through <c>Hero.onEquipedItemsChange</c>: that writes the
    /// global HUD skill slots, which would hand player one's cooldown and ammo display over to
    /// player two.
    /// </para>
    /// </summary>
    private static void RebuildSecondHeroLoadout(Hero hero2)
    {
        var manager = hero2.activeSkillsManager;
        if (manager != null)
        {
            try
            {
                manager.killEntities();
            }
            catch (Exception ex)
            {
                LogWarn("[LocalCoop] could not drop hero two's skill entities: {Message}", ex.Message);
            }

            try
            {
                manager.initSkills();
            }
            catch (Exception ex)
            {
                LogWarn("[LocalCoop] could not re-init hero two's skills: {Message}", ex.Message);
            }
        }

        RefreshSecondHeroSkills(hero2);

        try
        {
            hero2.weaponsManager?.onEquippedItemsUpdated(Ref<bool>.DontCare);
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not rebuild hero two's weapon entities: {Message}", ex.Message);
        }

        try
        {
            // Scroll tiers are derived from the inventory, so they change with it.
            hero2.computeTiers();
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not recompute hero two's tiers: {Message}", ex.Message);
        }
    }
}
