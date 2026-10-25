using dc.en;
using dc.pr;
using dc.tool;
using HaxeProxy.Runtime;

namespace DeadCellsLocalMultiplayerMod;

/// <summary>
/// One equipment slot of player two, as the panel needs it. Plain C# data: it is never handed
/// back to Haxe, so building it cannot corrupt a game array.
/// </summary>
internal readonly record struct P2SlotInfo(
    bool HasItem,
    string Name,
    int Level,
    double CooldownRemaining,
    double CooldownRatio,
    int UsesLeft,
    int Ammo,
    int MaxAmmo)
{
    public static P2SlotInfo Empty => new(false, "-", 0, 0, 0, 0, 0, 0);

    /// <summary>
    /// The numbers that go with the item: upgrade level, ammo, remaining charges, cooldown. Kept
    /// apart from <see cref="Name"/> so the panel can shorten the name to fit and still show these
    /// in full — a cut-off item name is readable, a cut-off ammo count is not.
    /// </summary>
    public string Detail
    {
        get
        {
            if (!HasItem)
            {
                return "";
            }

            var parts = new List<string>(4);
            if (Level > 0)
            {
                parts.Add($"L{Level}");
            }
            if (MaxAmmo > 0)
            {
                parts.Add($"{Ammo}/{MaxAmmo}");
            }
            if (UsesLeft > 1)
            {
                parts.Add($"x{UsesLeft}");
            }
            if (CooldownRemaining > 0.05)
            {
                parts.Add($"{CooldownRemaining:0.0}s");
            }
            return string.Join("   ", parts);
        }
    }

    /// <summary>Compact one-line label (name plus numbers), used for the change signature.</summary>
    public string Label
    {
        get
        {
            if (!HasItem)
            {
                return "-";
            }

            var detail = Detail;
            return detail.Length > 0 ? $"{Name}   {detail}" : Name;
        }
    }
}

/// <summary>
/// Everything the player-two panel draws, read once per frame. Pure reads with null guards:
/// a missing piece of state leaves its field at the default instead of throwing.
/// </summary>
internal readonly record struct P2HudSnapshot(
    int Life,
    int MaxLife,
    P2SlotInfo Weapon0,
    P2SlotInfo Weapon1,
    P2SlotInfo Skill0,
    P2SlotInfo Skill1,
    int FlaskCur,
    int FlaskMax,
    int Brutality,
    int Tactic,
    int Survival,
    int InfectionMax,
    int InfectionTier,
    int InfectionTierCount,
    int Curse,
    string Talisman)
{
    /// <summary>
    /// Change key for the panel: the panel repaints only when this differs from the last
    /// frame's, so a steady run costs one string build and no Graphics work.
    /// </summary>
    public string Signature =>
        $"{Life}/{MaxLife}|{Weapon0.Label}|{Weapon1.Label}|{Skill0.Label}|{Skill1.Label}" +
        $"|{FlaskCur}/{FlaskMax}|{Brutality}/{Tactic}/{Survival}" +
        $"|{InfectionMax}/{InfectionTier}/{InfectionTierCount}|{Curse}|{Talisman}";
}

/// <summary>
/// Part of <see cref="LocalCoop"/>: the read side of the player-two panel. Every value comes from
/// player two's own objects — its <c>Inventory</c>, its skill manager and the hero itself — so the
/// panel keeps showing player two's gear when the two backpacks are independent.
/// </summary>
internal static partial class LocalCoop
{
    /// <summary>
    /// Reads player two's panel state. Never throws: each block is guarded and simply leaves its
    /// values at the default, because a broken panel must not take the game down.
    /// </summary>
    private static P2HudSnapshot ReadP2HudSnapshot(Hero hero)
    {
        var life = 0;
        var maxLife = 0;
        var flaskCur = 0;
        var flaskMax = 0;
        var infectionMax = 0;
        var infectionTier = 0;
        var infectionTierCount = 0;
        var curse = 0;
        var brutality = 0;
        var tactic = 0;
        var survival = 0;
        var weapon0 = P2SlotInfo.Empty;
        var weapon1 = P2SlotInfo.Empty;
        var skill0 = P2SlotInfo.Empty;
        var skill1 = P2SlotInfo.Empty;
        var talisman = "-";

        try
        {
            // Life and its cap are counts: clamp them here so every consumer downstream (the bar,
            // the centred number, the signature) sees a value that cannot go below zero.
            // A downed hero is deliberately held in the level at `DownedLife` (1 HP) — see
            // Coop/LocalCoop.Down.cs — but the readout must still say 0, or the player sees 1 HP
            // on a hero that cannot move.
            life = IsDown(hero) ? 0 : System.Math.Max(0, hero.life);
            maxLife = System.Math.Max(0, hero.maxLife);
            curse = System.Math.Max(0, hero.curseCounter);
            infectionMax = System.Math.Max(0, hero.infectionMax);
            infectionTierCount = System.Math.Max(0, hero.infectionTierCount);

            // Infection is a run-wide system: the live tier lives on the game, not on the hero,
            // which only carries its own maximum and tier count.
            var infection = Game.Class.ME?.infection;
            if (infection != null)
            {
                infectionTier = infection.infectionTier;
            }
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] P2 panel could not read the hero state: {Message}", ex.Message);
        }

        var inventory = hero.inventory;
        if (inventory != null)
        {
            try
            {
                // Show the hero's attribute **tiers**, not the raw scroll counts.
                //
                // The game keeps these on the hero and computes them in `computeTiers()` as
                // `1 + countBrutalityUp(...)` — so the number a player sees on the vanilla HUD starts
                // at 1 and every scroll raises it. Reading the raw counts here started the readout at
                // 0 and left it one behind everywhere.
                //
                // The counts are still the source when the tier fields have not been computed yet (a
                // hero that has not run `computeTiers()` reads 0), in which case the raw count + 1 is
                // the same number the tier would be.
                brutality = hero.brutalityTier;
                tactic = hero.tacticTier;
                survival = hero.survivalTier;

                if (brutality <= 0)
                {
                    brutality = inventory.countBrutalityUp(
                        ref System.Runtime.CompilerServices.Unsafe.NullRef<bool>()) + 1;
                }
                if (tactic <= 0)
                {
                    tactic = inventory.countTacticUp(
                        ref System.Runtime.CompilerServices.Unsafe.NullRef<bool>()) + 1;
                }
                if (survival <= 0)
                {
                    survival = inventory.countSurvivalUp(
                        ref System.Runtime.CompilerServices.Unsafe.NullRef<bool>()) + 1;
                }
            }
            catch (Exception ex)
            {
                LogWarn("[LocalCoop] P2 panel could not read the scroll counts: {Message}", ex.Message);
            }

            try
            {
                weapon0 = ReadEquippedWeapon(inventory, 0);
                weapon1 = ReadEquippedWeapon(inventory, 1);
            }
            catch (Exception ex)
            {
                LogWarn("[LocalCoop] P2 panel could not read the weapons: {Message}", ex.Message);
            }

            try
            {
                // The talisman (ring) is not a slot item: `Inventory.equip()` returns early for its
                // group, so it never gets a `posID` and nothing that walks the weapon/skill slots can
                // see it. `getTalisman()` scans the whole bag for `group == 7` instead, which is the
                // same call the game uses for its effect — so this is the only way to show it.
                var ring = inventory.getTalisman();
                talisman = ring != null ? Trim(ReadItemName(ring), 24) : "-";
            }
            catch (Exception ex)
            {
                LogWarn("[LocalCoop] P2 panel could not read the talisman: {Message}", ex.Message);
            }

            try
            {
                skill0 = ReadActiveSkill(hero, inventory, 0);
                skill1 = ReadActiveSkill(hero, inventory, 1);
            }
            catch (Exception ex)
            {
                LogWarn("[LocalCoop] P2 panel could not read the skills: {Message}", ex.Message);
            }
        }

        try
        {
            (flaskCur, flaskMax) = ReadFlaskCharges(hero);
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] P2 panel could not read the flask charges: {Message}", ex.Message);
        }

        return new P2HudSnapshot(life, maxLife, weapon0, weapon1, skill0, skill1,
            flaskCur, flaskMax, brutality, tactic, survival,
            infectionMax, infectionTier, infectionTierCount, curse, talisman);
    }

    /// <summary>Reads one equipped weapon slot (0 = main hand, 1 = off hand).</summary>
    private static P2SlotInfo ReadEquippedWeapon(Inventory inventory, int slot)
    {
        if (slot >= inventory.nbWeapons)
        {
            return P2SlotInfo.Empty;
        }

        var item = inventory.getEquippedWeaponOn(slot);
        if (item == null)
        {
            return P2SlotInfo.Empty;
        }

        var ammo = 0;
        var maxAmmo = 0;
        try
        {
            maxAmmo = item.getMaxAmmo();
            ammo = item.ammo;
        }
        catch
        {
            // Melee weapons have no ammo at all; leave both at zero.
        }

        return new P2SlotInfo(true, ReadItemName(item), ReadItemLevel(item),
            0, 0, 0, ammo, maxAmmo);
    }

    /// <summary>
    /// Reads one active-skill slot, including its live cooldown: the timer belongs to player two's
    /// own skill manager (<c>OldSkill</c> is built per hero), so it shows player two's cooldown.
    /// </summary>
    private static P2SlotInfo ReadActiveSkill(Hero hero, Inventory inventory, int slot)
    {
        if (slot >= inventory.nbActives)
        {
            return P2SlotInfo.Empty;
        }

        var item = inventory.getActiveOn(slot);
        if (item == null)
        {
            return P2SlotInfo.Empty;
        }

        var remaining = 0.0;
        var ratio = 0.0;
        var usesLeft = 0;

        try
        {
            var active = hero.activeSkillsManager?.getActiveSkill(item);
            if (active != null)
            {
                try
                {
                    usesLeft = active.get_usesLeft();
                }
                catch
                {
                    // Only charge-based skills answer this; harmless otherwise.
                }

                try
                {
                    ratio = active.getNextUseCooldownRatio();
                }
                catch
                {
                    ratio = 0;
                }

                var skill = active.skill;
                if (skill != null)
                {
                    remaining = skill.coolDownF;
                    // Non charge-based skills report no per-use ratio, so fall back to the
                    // plain timer the skill manager ticks every frame.
                    if (ratio <= 0 && skill.coolDownMaxF > 0)
                    {
                        ratio = skill.coolDownF / skill.coolDownMaxF;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] P2 panel could not read a skill cooldown: {Message}", ex.Message);
        }

        if (ratio < 0)
        {
            ratio = 0;
        }
        else if (ratio > 1)
        {
            ratio = 1;
        }

        return new P2SlotInfo(true, ReadItemName(item), ReadItemLevel(item),
            remaining, ratio, usesLeft, 0, 0);
    }

    /// <summary>
    /// Reads the flask charges. They are not on the inventory: the <c>Heal</c> main skill owns
    /// them, so the manager's skill list is searched for it.
    /// </summary>
    private static (int Cur, int Max) ReadFlaskCharges(Hero hero)
    {
        var skills = hero.mainSkillsManager?.skills;
        if (skills == null)
        {
            return (0, 0);
        }

        for (var i = 0; i < skills.length && i < 16; i++)
        {
            if (skills.getDyn(i) is not dc.tool.mainSkills.Heal heal)
            {
                continue;
            }
            return (heal.get_healings(), heal.get_maxHealings());
        }

        return (0, 0);
    }

    /// <summary>
    /// The item's display name. <c>getOnlyName</c> takes the "neutral" flag by reference and
    /// writes back through it, so it is given a throwaway local rather than a shared ref.
    /// </summary>
    private static string ReadItemName(InventItem item)
    {
        try
        {
            var neutral = false;
            var name = item.getOnlyName(ref neutral);
            var text = name?.ToString();
            return string.IsNullOrWhiteSpace(text) ? "?" : text;
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] P2 panel could not read an item name: {Message}", ex.Message);
            return "?";
        }
    }

    /// <summary>The item's upgrade level, or 0 when it has none.</summary>
    private static int ReadItemLevel(InventItem item)
    {
        try
        {
            return item.getUpgradeLevel();
        }
        catch
        {
            return 0;
        }
    }
}
