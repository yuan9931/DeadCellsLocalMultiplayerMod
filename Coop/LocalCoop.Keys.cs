using System;
using System.Collections.Generic;
using dc.en;
using dc.pr;
using dc.tool;
using ModCore.Utilities;

namespace DeadCellsLocalMultiplayerMod;

/// <summary>
/// Part of <see cref="LocalCoop"/>: <b>keys are shared between the two heroes</b> even while their
/// equipment is independent — gold, cells and blueprints already are, and a key is the same kind of
/// party-wide resource.
/// <para>
/// <b>The problem.</b> With independent equipment the two heroes carry different bags, and every key
/// gate in the game asks a single bag. <c>LockedDoorButton.hasKey</c> and
/// <c>CemeteryGraveCave.hasKey</c> read <c>_level.game.hero.inventory</c> (the global — player one),
/// <c>HiddenTrigger</c>'s ShipwreckKey check reads the bag of whoever fired it, and the key <b>spend</b>
/// is <c>_level.game.hero.inventory.removeOne(keyKind)</c>. The mod already answers those checks for
/// the hero that is actually at the door, which left the mirror image broken: a key held by the
/// <i>other</i> hero was invisible to the door, so the pair could stand at a keyhole, hold the key
/// between them, and be refused.
/// </para>
/// <para>
/// <b>The fix: one key, two bags.</b> Every key the game puts into either hero's bag is mirrored into
/// the other's, and every key the game spends is taken from both. The mirroring sits at
/// <c>Inventory.add</c> — the game's own entry point (a pickup routes through
/// <c>Hero.applyItemPickEffect</c> → <c>inventory.add</c>) — and the spending at the two primitives the
/// doors and shrines consume through: <c>removeOne</c> (a keyhole door at
/// <c>LockedDoorButton.cs:1246</c>, the cavern at <c>CemeteryGraveCave.cs:1193</c>) and
/// <c>removeAll</c> (the Shrine keys at <c>Hero.cs:12008-12014</c> and <c>:12735-12741</c> — they are
/// spent with <c>removeAll</c>, not <c>removeOne</c>, so mirroring only the latter would leave the
/// other bag holding a copy the sync would keep putting back).
/// </para>
/// <para>
/// <b>Why a real copy rather than only a read-side union.</b> Plenty of key logic does not <i>ask</i>
/// the bag, it <i>walks</i> it. The bank keycard is recognised by a loop over the global hero's items
/// that sets <c>cardLockedDoor.heroHasCard</c> (<c>Hero.cs:17329</c>) and never touches
/// <c>hasItem</c>/<c>countItemKind</c>, and the HUD key bar is the same: it is rebuilt from
/// <c>game.hero.inventory.items</c> in three places — <c>HUD.initItems</c>,
/// <c>Hero.hudUpdateKeyStock</c>, and the pickup tween's end callback
/// (<c>onItemPickContext_5474385.ArrowFunctionEntry_HUD_L1163_LC879</c>, which this proxy cannot hook).
/// A key that is genuinely <i>in</i> the hero's bag is drawn by all of them and found by every walker,
/// so both the bar and the keycard cases come right for free.
/// </para>
/// <para>
/// <b>The read-side union stays as a safety net.</b> <c>hasItem</c> / <c>countItemKind</c> still answer
/// over both bags: it covers a key that entered a bag before the mirroring existed (a continued run
/// loaded from such a save), and if a mirror ever fails — <c>add</c> throwing, the partner being gone —
/// the door still opens and the spend still lands on the bag that really holds it.
/// </para>
/// <para>
/// <b>Only ids the game calls a key are mirrored.</b> The test is the game's own
/// <c>InventItem.hasTag("Key")</c>, the one <c>HUD</c> walks the bag with when it fills
/// <c>keyStock</c>, and the answer is cached per id. Sharing mutations or aspects would break
/// independent equipment outright — they are ordinary bag items too.
/// </para>
/// <para>
/// The mirrored copy is a fresh <c>InventItem</c> of the same kind id (the way the game's own
/// <c>dropKey</c> cinematics build one), never the same object, so the two bags do not share an
/// identity the save or the HUD slot indexing could trip over; and the mirror skips a bag that already
/// holds that key, which is what keeps a run saved by this build from ending up with two copies after
/// load. In shared-equipment mode both heroes use one <c>Inventory</c>, so every hook here
/// short-circuits and vanilla behaviour is untouched.
/// </para>
/// </summary>
internal static partial class LocalCoop
{
    /// <summary>
    /// Whether each item id is a key, as answered by the game's own <c>hasTag("Key")</c>. The item
    /// database is static, so an id's answer never changes and is asked once per session.
    /// </summary>
    private static readonly Dictionary<string, bool> _keyIdCache = new(StringComparer.Ordinal);

    /// <summary>The <c>"Key"</c> tag, created lazily (the Haxe runtime must be up for it).</summary>
    private static dc.String? _keyTag;

    /// <summary>
    /// Installs the shared-key mechanism: mirror on add, spend from both, and the read-side union.
    /// Called once from <see cref="Install"/>.
    /// </summary>
    private static void InstallKeySharingHooks()
    {
        Hook_Inventory.add += OnSharedKeyAdded;
        Hook_Inventory.removeOne += OnSharedKeyRemoveOne;
        Hook_Inventory.removeAll += OnSharedKeyRemovedAll;
        Hook_Inventory.hasItem += OnSharedKeyHasItem;
        Hook_Inventory.countItemKind += OnSharedKeyCountItemKind;
    }

    /// <summary>
    /// The other hero's bag, or null when nothing must be widened.
    /// <para>
    /// Null is the answer in every case the mechanism must not apply: the session is off, one of the
    /// heroes is gone, the bag belongs to neither of them (a shop's, a mob's), or — the common one —
    /// both heroes already share one bag, where the question already answered itself and this must
    /// cost nothing at all.
    /// </para>
    /// </summary>
    private static Inventory? PartnerInventoryFor(Inventory self)
    {
        if (!Enabled)
        {
            return null;
        }

        var hero1 = _hero1;
        var hero2 = _hero2;
        if (hero1 == null || hero1.destroyed || hero2 == null || hero2.destroyed)
        {
            return null;
        }

        var bag1 = hero1.inventory;
        var bag2 = hero2.inventory;
        if (bag1 == null || bag2 == null || ReferenceEquals(bag1, bag2))
        {
            return null; // one shared bag: nothing to widen
        }

        if (ReferenceEquals(self, bag1))
        {
            return bag2;
        }

        return ReferenceEquals(self, bag2) ? bag1 : null;
    }

    /// <summary>
    /// Whether an item id is a key, per the game's own <c>hasTag("Key")</c> test (the one
    /// <c>HUD</c> fills <c>keyStock</c> with). Cached per id; any failure answers "not a key", so a
    /// bad lookup can only leave vanilla behaviour in place.
    /// </summary>
    private static bool IsKeyId(dc.String? id)
    {
        if (id == null)
        {
            return false;
        }

        try
        {
            var text = id.ToString();
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            if (_keyIdCache.TryGetValue(text, out var known))
            {
                return known;
            }

            _keyTag ??= "Key".AsHaxeString();
            // The proxy exposes the companion's static `fromItem` as a delegate on the class object.
            var item = InventItem.Class.fromItem(id, null);
            var isKey = item != null && item.hasTag(_keyTag);
            _keyIdCache[text] = isKey;
            LogVerbose($"[LocalCoop] '{text}' is {(isKey ? "" : "not ")}a key");
            return isKey;
        }
        catch (Exception ex)
        {
            LogVerbose($"[LocalCoop] could not tell whether an item is a key: {ex.Message}");
            return false;
        }
    }

    // ---- mirror: a key in one bag is a key in both --------------------------------------------

    private static InventItem OnSharedKeyAdded(Hook_Inventory.orig_add orig, Inventory self, InventItem i)
    {
        var added = orig(self, i);
        try
        {
            MirrorKeyIntoPartner(orig, self, added);
        }
        catch (Exception ex)
        {
            LogVerbose($"[LocalCoop] could not mirror a shared key: {ex.Message}");
        }

        return added;
    }

    /// <summary>
    /// Puts the partner's copy of a freshly added key into the partner's bag, through the game's own
    /// <c>add</c> so the item is wired exactly the way the game wires one (its <c>_itemData</c> is
    /// resolved, its <c>posID</c> assigned, <c>equip</c> given its turn).
    /// </summary>
    private static void MirrorKeyIntoPartner(
        Hook_Inventory.orig_add orig,
        Inventory self,
        InventItem? item)
    {
        if (item == null)
        {
            return;
        }

        var id = KindIdOf(item.kind);
        if (id == null || !IsKeyId(id))
        {
            return;
        }

        var partner = PartnerInventoryFor(self);
        if (partner == null)
        {
            return;
        }

        var text = id.ToString();
        if (BagHasKind(partner, text))
        {
            return; // already there (a save made by this build, or a double add)
        }

        var fresh = InventItem.Class.fromItem(id, null);
        if (fresh == null)
        {
            return;
        }

        orig(partner, fresh);
        Log($"[LocalCoop] shared key '{text}' mirrored into the partner's bag");

        // The key bar is rebuilt from the GLOBAL hero's bag, and the game only rebuilds it when it
        // thinks a key went in. A pickup by player two does not run that for the bar the players
        // look at, so ask the hero whose bag the bar reads to rebuild it now (the same call the
        // game makes: `Hero.hudUpdateKeyStock`, Hero.cs:82831, reads HUD.Class.ME and
        // game.hero.inventory — player one — whatever hero it is called on).
        _hero1?.hudUpdateKeyStock();
    }

    /// <summary>
    /// Spends the key from whichever bag holds it — and, since it may hold a mirrored copy, from
    /// the other one too. A key that survives its own unlock would open the next lock, and a stale
    /// mirror would keep showing on the bar.
    /// </summary>
    private static bool OnSharedKeyRemoveOne(Hook_Inventory.orig_removeOne orig, Inventory self, dc.String k)
    {
        var removed = orig(self, k);
        try
        {
            var partner = PartnerInventoryFor(self);
            if (partner != null && IsKeyId(k))
            {
                // `orig` is the original method, not this hook, so asking the partner's bag directly
                // cannot recurse (the same shape as the mirrored `orig(_hero1, ...)` calls in
                // LocalCoop.Hooks.cs).
                var also = orig(partner, k);
                if (also && !removed)
                {
                    Log($"[LocalCoop] shared key '{k}' spent from the partner's bag");
                }

                removed |= also;
                if (removed)
                {
                    _hero1?.hudUpdateKeyStock();
                }
            }
        }
        catch (Exception ex)
        {
            LogVerbose($"[LocalCoop] could not spend a shared key from both bags: {ex.Message}");
        }

        return removed;
    }

    /// <summary>
    /// The same for <c>removeAll</c>, which is how the Shrine keys are actually spent
    /// (<c>Hero.cs:12008</c> / <c>:12735</c>).
    /// </summary>
    private static void OnSharedKeyRemovedAll(Hook_Inventory.orig_removeAll orig, Inventory self, dc.String k)
    {
        orig(self, k);
        try
        {
            var partner = PartnerInventoryFor(self);
            if (partner != null && IsKeyId(k))
            {
                orig(partner, k);
                _hero1?.hudUpdateKeyStock();
            }
        }
        catch (Exception ex)
        {
            LogVerbose($"[LocalCoop] could not clear a shared key from both bags: {ex.Message}");
        }
    }

    // ---- the read-side union (safety net for bags the mirror never saw) ------------------------

    private static bool OnSharedKeyHasItem(Hook_Inventory.orig_hasItem orig, Inventory self, dc.String k)
    {
        if (orig(self, k))
        {
            return true;
        }

        var partner = PartnerInventoryFor(self);
        return partner != null && IsKeyId(k) && orig(partner, k);
    }

    private static int OnSharedKeyCountItemKind(Hook_Inventory.orig_countItemKind orig, Inventory self, dc.String ik)
    {
        var mine = orig(self, ik);
        if (mine > 0)
        {
            // This bag answered. For a key the count is only ever read as "any at all", so there is
            // nothing to add and no reason to pay for the key test on the hot path.
            return mine;
        }

        var partner = PartnerInventoryFor(self);
        return partner != null && IsKeyId(ik) ? orig(partner, ik) : mine;
    }

    // ---- small helpers -------------------------------------------------------------------------

    /// <summary>
    /// Whether this bag already holds an item of that kind id. Reads the bag directly instead of
    /// <c>countItemKind</c> because that one is hooked too — a duplicate check must ask the bag
    /// itself, not the widened answer.
    /// </summary>
    private static bool BagHasKind(Inventory bag, string id)
    {
        var items = bag.items;
        if (items == null)
        {
            return false;
        }

        for (var i = 0; i < items.length && i < 256; i++)
        {
            if (items.getDyn(i) is not InventItem item)
            {
                continue;
            }

            var kind = KindIdOf(item.kind);
            if (kind != null && kind.ToString() == id)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The item id an <c>InventItemKind</c> carries. Each variant keeps it in its own <c>Param0</c>
    /// — that is the string the game hands the icon factory and <c>fromItem</c> (its own code reads
    /// the same field; the decompiler prints the variant tag as <c>Index</c>, which is the C# enum,
    /// not the id). <c>__D_Passive</c> is the one variant without one.
    /// </summary>
    private static dc.String? KindIdOf(InventItemKind? kind) => kind switch
    {
        InventItemKind.Weapon k => k.Param0,
        InventItemKind.Active k => k.Param0,
        InventItemKind.Talisman k => k.Param0,
        InventItemKind.BagItem k => k.Param0,
        InventItemKind.Consumable k => k.Param0,
        InventItemKind.PreciousLoot k => k.Param0,
        InventItemKind.Meta k => k.Param0,
        InventItemKind.Perk k => k.Param0,
        InventItemKind.Skin k => k.Param0,
        InventItemKind.Head k => k.Param0,
        InventItemKind.Aspect k => k.Param0,
        InventItemKind.BossRushStatueUnlock k => k.Param0,
        _ => null,
    };
}
