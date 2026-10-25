using dc;
using dc.en;
using dc.en.inter;
using dc.en.inter.door;
using dc.en.inter.elevator;
using dc.en.inter.npc;
using dc.pr;
using dc.ui;
using dc.tool;
using HaxeProxy.Runtime;
using ModCore.Utilities;

namespace DeadCellsLocalMultiplayerMod;

/// <summary>
/// Part of <see cref="LocalCoop"/>: the interactive-object half of the "the game resolves the
/// player through <c>Game.ME.hero</c>" audit.
/// <para>
/// <b>The activation path itself is sound</b> — <c>Hero.updateInteraction()</c> tests
/// <c>canBeActivated(this)</c>, reads <c>this.controller</c> for the button, and
/// <c>activateFocus</c> calls <c>entity.onActivate(this, longPress)</c>. Player two can focus and
/// use shops, chests, portals and plates normally. The bug surface is exclusively objects that,
/// INSIDE their own logic, re-resolve <c>_level.game.hero</c> instead of using the passed
/// <c>by</c>/<c>self</c>. The fix mirrors the mob-AI wrapper: run the game's own call with the
/// global pointed at the acting/nearby hero, save/restore in a finally, exceptions never escape.
/// </para>
/// <para>
/// The severity split (from the audit) is real: the position/life/identity sites (fountain gate,
/// scratchable floors, elevator doors, keyhole consumption, NPC auto-activation, auto-pickup)
/// break TODAY with shared equipment; the bag-read sites (key checks, conditional unlockables,
/// hidden blocks) are latent until independent equipment returns — both are fixed here.
/// </para>
/// </summary>
internal static partial class LocalCoop
{
    /// <summary>Registers the interactive hooks. Called from <see cref="InstallEntityHooks"/>.</summary>
    internal static void InstallInteractableHooks()
    {
        // 1. The healing fountain asks "may I drink / is anyone hurt" of the GLOBAL hero while the
        // heal itself goes to `by`: hero1 at full HP + hero2 hurt => hero2 is refused ("I don't need
        // this") and the fountain is consumed. Run the decision as the drinker, with a teammate
        // check so a full-HP hero still yields when someone else actually needs it.
        Hook_Fountain.onActivate += (orig, self, by, lp) =>
        {
            RunAsTouchedHero(by, () =>
            {
                var partner = PartnerOf(by);
                var swapForPartner = partner != null
                    && !partner.destroyed
                    && partner.life < partner.maxLife
                    && by != null && by.life >= by.maxLife;
                if (swapForPartner)
                {
                    RunAsTouchedHero(partner, () => orig(self, by, lp));
                }
                else
                {
                    orig(self, by, lp);
                }
            });
        };

        Hook_Fountain.onFocus += OnFountainFocus;
        Hook_Fountain.fixedUpdate += OnFountainFixedUpdate;

        // 2. Scratchable floor tiles: the break gate (alive + canBreakTiles + facing + same row) and
        // the hit itself (createFromHeroAndHit) all use the global — hero2 can never break a tile
        // and hero1's standing breaks tiles hero2 touched.
        Hook_BreakableTile.fixedUpdate += (orig, self) =>
            RunAsHero(NearbyHeroFor(self, radiusPx: 48.0), () => orig(self));
        Hook_BreakableTile.isBreakableByHero += (orig, self) =>
        {
            var answer = false;
            RunAsHero(NearbyHeroFor(self, radiusPx: 48.0), () => answer = orig(self));
            return answer;
        };

        // 3. The keyhole checks AND spends keys from the GLOBAL hero's bag, ignoring `by`: a door
        // won't open when only player two holds the key, and every unlock silently eats P1's key.
        Hook_LockedDoorButton.hasKey += (orig, self) =>
        {
            var hero = FocusedOurHero() ?? _hero1;
            var answer = false;
            RunAsHero(hero, () => answer = orig(self));
            return answer;
        };
        Hook_LockedDoorButton.onActivate += (orig, self, by, lp) =>
            RunAsTouchedHero(by, () => orig(self, by, lp));
        Hook_LockedDoorButton.onFocus += OnLockedDoorButtonFocus;

        // 4/5. The elevator and its shaft doors decide auto-open, ride state and the tip from
        // hero1's cell — player two standing at the shaft gets nothing.
        Hook_ElevatorDoor.fixedUpdate += (orig, self) =>
            RunAsHero(NearbyHeroFor(self, radiusPx: 96.0), () => orig(self));
        Hook_ElevatorDoor.postUpdate += (orig, self) =>
            RunAsHero(NearbyHeroFor(self, radiusPx: 96.0), () => orig(self));
        Hook_ElevatorDoor.onActivate += (orig, self, by, lp) =>
            RunAsTouchedHero(by, () => orig(self, by, lp));
        Hook_ElevatorDoor.onFocus += (orig, self) =>
            RunAsHero(NearbyHeroFor(self, radiusPx: 96.0), () => orig(self));
        Hook_MultiFloorElevator.fixedUpdate += (orig, self) =>
            RunAsHero(RidingHeroOrNearby(self), () => orig(self));
        Hook_MultiFloorElevator.postUpdate += (orig, self) =>
            RunAsHero(RidingHeroOrNearby(self), () => orig(self));
        Hook_MultiFloorElevator.callFromLevel += (orig, self, triggerCy) =>
            RunAsHero(NearbyHeroFor(self, radiusPx: 96.0), () => orig(self, triggerCy));

        // 6. NPCs auto-activate from proximity AS hero1 (forge, tailor, knight, crypt demon...).
        // The anvil/forge auto-open also feeds the mod's own revive-at-forge flow, so proximity is
        // answered per hero: if hero2 is the one standing there, the game's own auto-activate runs
        // as hero2.
        Hook_Blacksmith.fixedUpdate += (orig, self) =>
            RunAsHero(NearbyHeroFor(self, radiusPx: 60.0), () => orig(self));
        Hook_SmallBlacksmith.fixedUpdate += (orig, self) =>
            RunAsHero(NearbyHeroFor(self, radiusPx: 60.0), () => orig(self));
        Hook_Knight.fixedUpdate += (orig, self) =>
            RunAsHero(NearbyHeroFor(self, radiusPx: 60.0), () => orig(self));
        Hook_Tailor.fixedUpdate += (orig, self) =>
            RunAsHero(NearbyHeroFor(self, radiusPx: 60.0), () => orig(self));
        Hook_ChallengeGuy.fixedUpdate += (orig, self) =>
            RunAsHero(NearbyHeroFor(self, radiusPx: 60.0), () => orig(self));
        Hook_CryptDemon.fixedUpdate += (orig, self) =>
            RunAsHero(NearbyHeroFor(self, radiusPx: 60.0), () => orig(self));
        Hook_GlitchedKnight.fixedUpdate += (orig, self) =>
            RunAsHero(NearbyHeroFor(self, radiusPx: 60.0), () => orig(self));

        // 7. pickOnTouch collectibles auto-pick when the GLOBAL hero is close — hero2 walking over
        // a potion picks nothing, hero1 standing by a drop collects it every frame. Run the scan
        // as the hero actually on top of it.
        Hook_ItemDrop.fixedUpdate += (orig, self) =>
            RunAsHero(NearbyHeroFor(self, radiusPx: 20.0), () => orig(self));

        // 8. Hidden-trigger gates (the ShipwreckKey check among them) read the GLOBAL bag even
        // though `trigger(by)` knows who fired it.
        Hook_HiddenTrigger.trigger += (orig, self, by) =>
            RunAsTouchedHero(by, () => orig(self, by));

        // 9. Conditional unlockables (isUnlocked = the GLOBAL hero holds the key item).
        Hook_BreakableGroundConditional.get_isUnlocked += (orig, self) =>
        {
            var answer = false;
            RunAsHero(_hero1, () => answer |= orig(self));
            if (!answer)
            {
                RunAsHero(_hero2, () => answer |= orig(self));
            }
            return answer;
        };

        // 10. The necromancer barrier wall opens on the GLOBAL hero's vertical position — a literal
        // P1-keyed wall.
        Hook_NecromantForceField.fixedUpdate += (orig, self) =>
            RunAsHero(NearbyHeroFor(self, radiusPx: 240.0), () => orig(self));

        // 11. Deployed-item buffs (Entity base): the buff requires the GLOBAL hero alive, checks
        // that hero's P_DeployedDmg, measures distance to that hero, and grants the affect to that
        // hero. Run the pass as each hero so whoever deployed the entity gets its own buff maths.
        Hook_Entity.updateDeployedBuffs += (orig, self) =>
        {
            var owner = OwnerHero(self);
            if (owner != null)
            {
                RunAsHero(owner, () => orig(self));
                return;
            }

            RunAsHero(_hero1, () => orig(self));
            RunAsHero(_hero2, () => orig(self));
        };
    }

    private static Hero? PartnerOf(Hero? hero)
    {
        if (hero == null)
        {
            return null;
        }

        return ReferenceEquals(hero, _hero1) ? _hero2 : (ReferenceEquals(hero, _hero2) ? _hero1 : null);
    }

    /// <summary>
    /// The hero the object should answer for when its own logic reads the global: the nearest
    /// detectable of our two heroes within <paramref name="radiusPx"/>, ties going to player one.
    /// Null keeps today's behaviour.
    /// </summary>
    private static Hero? NearbyHeroFor(Entity self, double radiusPx)
    {
        try
        {
            var hero1 = _hero1;
            var hero2 = _hero2;
            if (!Enabled || self.destroyed || (hero1 == null && hero2 == null))
            {
                return null;
            }

            var d1 = hero1 != null && !hero1.destroyed ? WorldDistance(self, hero1) : double.PositiveInfinity;
            var d2 = hero2 != null && !hero2.destroyed ? WorldDistance(self, hero2) : double.PositiveInfinity;

            if (d2 < d1 - 1.0 && d2 <= radiusPx)
            {
                return hero2;
            }

            if (d1 <= radiusPx)
            {
                return hero1;
            }

            return d2 <= radiusPx ? hero2 : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The elevator picks its subject: a hero riding inside, else a hero at the shaft.</summary>
    private static Hero? RidingHeroOrNearby(MultiFloorElevator elevator)
    {
        try
        {
            if (IsOurHero(_hero1) && !_hero1!.destroyed && elevator.isInElevator(_hero1))
            {
                return _hero1;
            }

            if (IsOurHero(_hero2) && !_hero2!.destroyed && elevator.isInElevator(_hero2))
            {
                return _hero2;
            }
        }
        catch
        {
            // Fall through to the proximity answer.
        }

        return NearbyHeroFor(elevator, radiusPx: 96.0);
    }

    /// <summary>The hero whose focus should drive a focus-time read (fountain tip, keyhole colour).</summary>
    private static void OnFountainFocus(Hook_Fountain.orig_onFocus orig, Fountain self)
    {
        // onFocus fires for the hero that has focus in the mod's own per-hero HUD path; run it as
        // each hero and let the game's checks answer per hero (a no-op for a hero it refuses).
        RunAsHero(_hero2, () => RunAsHero(_hero1, () => orig(self)));
    }

    private static void OnFountainFixedUpdate(Hook_Fountain.orig_fixedUpdate orig, Fountain self)
    {
        RunAsHero(NearbyHeroFor(self, radiusPx: 48.0), () => orig(self));
    }

    private static void OnLockedDoorButtonFocus(Hook_LockedDoorButton.orig_onFocus orig, LockedDoorButton self)
    {
        RunAsHero(FocusedOurHero() ?? _hero1, () => orig(self));
    }

    /// <summary>
    /// Which of our heroes currently has an interaction focus at all — Hero keeps a per-hero
    /// `focused`, but the objects read the global's, so the game's focus callbacks should run for
    /// the hero that is actually looking at them. Falls back to player one's focus.
    /// </summary>
    private static Hero? FocusedOurHero()
    {
        try
        {
            var hero1 = _hero1;
            if (hero1 != null && !hero1.destroyed && hero1.focused != null)
            {
                return hero1;
            }

            var hero2 = _hero2;
            if (hero2 != null && !hero2.destroyed && hero2.focused != null)
            {
                return hero2;
            }
        }
        catch
        {
            // No focus is a fine answer.
        }

        return null;
    }

    /// <summary>The hero that owns a deployed entity, when its parent chain says so.</summary>
    private static Hero? OwnerHero(Entity self)
    {
        try
        {
            var e = self;
            for (var hops = 0; e != null && hops < 4; hops++)
            {
                if (IsOurHero(e))
                {
                    return (Hero)e;
                }

                e = e.parent;
            }
        }
        catch
        {
            // Unresolved ownership falls back to the per-hero passes.
        }

        return null;
    }
}
