using System;
using System.Collections.Generic;
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
/// <b>Why the sharing is done at the query primitives.</b> Every one of those gates — and the shop,
/// shrine, ladder, teleporter and level-generation checks next to them — resolves down to two
/// <c>Inventory</c> methods, <c>hasItem</c> and <c>countItemKind</c>, and consumes through a third,
/// <c>removeOne</c>. Widening those three reaches every site at once, including ones the mod does not
/// hook and ones inside Haxe closures it cannot address, with no per-site code to keep in step.
/// </para>
/// <para>
/// <b>Only keys are widened.</b> Sharing <i>every</i> id would break independent equipment outright:
/// mutations and aspects are ordinary inventory items too, and <c>hasItem("P_Health")</c> must keep
/// answering for one hero. The gate is the game's own notion of a key — <c>InventItem.hasTag("Key")</c>,
/// which is exactly what <c>HUD</c> walks the bag with when it fills <c>keyStock</c> — so the answer is
/// cached per id and asked once.
/// </para>
/// <para>
/// <b>Nothing is duplicated.</b> The key stays in the bag it was picked up into; the other hero simply
/// sees it, and when it is spent it is taken from whichever bag holds it. Mirroring the item into both
/// bags was the tempting alternative and was rejected: there is no clean "add to bag" entry point
/// (only <c>remove</c> / <c>removeAll</c> / <c>removeOne</c> exist; adding means the pickup path with its
/// <c>posID</c> assignment and HUD slot rebuild), and a duplicated key would let one key open two locks
/// unless every spend site were patched as well.
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
    /// Installs the shared-key widening. Called once from <see cref="Install"/>.
    /// </summary>
    private static void InstallKeySharingHooks()
    {
        Hook_Inventory.hasItem += OnSharedKeyHasItem;
        Hook_Inventory.countItemKind += OnSharedKeyCountItemKind;
        Hook_Inventory.removeOne += OnSharedKeyRemoveOne;
    }

    /// <summary>
    /// The other hero's bag, or null when there is nothing to widen.
    /// <para>
    /// Null is the answer in every case the widening must not apply: the session is off, one of the
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

    /// <summary>
    /// Spends the key from whichever bag holds it. The bag it was picked up into is not necessarily
    /// the bag of the hero who used the door, and a key that survives its own unlock would open the
    /// next lock too.
    /// </summary>
    private static bool OnSharedKeyRemoveOne(Hook_Inventory.orig_removeOne orig, Inventory self, dc.String k)
    {
        if (orig(self, k))
        {
            return true;
        }

        var partner = PartnerInventoryFor(self);
        if (partner == null || !IsKeyId(k))
        {
            return false;
        }

        // `orig` is the original method, not this hook, so asking the partner's bag directly cannot
        // recurse (the same shape as the mirrored `orig(_hero1, ...)` calls in LocalCoop.Hooks.cs).
        var removed = orig(partner, k);
        if (removed)
        {
            Log($"[LocalCoop] shared key '{k}' spent from the partner's bag");
        }

        return removed;
    }
}