using dc;
using dc.en;
using dc.en.mob;
using dc.en.mob.boss;
using dc.en.mob.boss.death;
using dc.en.mob.boss.gardener;
using dc.en.mob.boss.giant;
using dc.pr;
using dc.ui;
using dc.tool.atk;
using HaxeProxy.Runtime;
using ModCore.Utilities;

namespace DeadCellsLocalMultiplayerMod;

/// <summary>
/// Part of <see cref="LocalCoop"/>: the mob/boss half of the "the game resolves the player through
/// <c>Game.ME.hero</c>" audit (the entity side of the same problem is
/// <c>Coop/LocalCoop.Interactables.cs</c>; the traps are <c>LocalCoop.Plant.cs</c> /
/// <c>LocalCoop.Traps.cs</c>).
/// <para>
/// <b>What the audit found.</b> The mobs' target-selection machinery is sound for co-op:
/// <c>Mob.updateThreat()</c> walks <c>_team.opponentsIterator</c> per entity, so hero2 gets aggro,
/// threat and <c>aTarget</c> correctly. Every real bug is an exception — a site that bypasses the
/// target and re-resolves the victim/aim/beneficiary through the global hero instead. Full-codebase
/// audit: 23 mob-class findings plus ~25 sites inside <c>Mob</c> itself.
/// </para>
/// <para>
/// <b>The unified fix.</b> The global read almost always happens <em>while a specific mob is
/// running its own code</em> (its AI tick, its skill callbacks inside that tick, its death body).
/// That mob already knows the right answer through <c>aTarget</c>/<c>nemesisTarget</c>. So the hook
/// re-points <c>Game.ME.hero</c> at the mob's own target for the duration of that mob's own
/// update/die/hit calls — the mirror of <c>RunAsTouchedHero</c> for the attacking side. This one
/// wrapper per mob retires the whole "aim/teleport/facing/victim/buff from the global" family
/// (Death's hover/choke/chain/soul-ultimate, DookuBeast's grab/fire-pillars, Collector/Queen
/// dash-hits, Beholder/MamaTick aiming, Amazon shot victims and dive hits, Medusa's gaze,
/// HauntedArmor/ShopMimic/Screamer wake-aggro, the aggressive-mobs hard-lock in
/// <c>Mob.fixedUpdate</c>, and the on-kill/on-hit perk beneficiaries inside <c>Mob.onDie</c> and
/// <c>Mob.onDirectHitFromHero</c>).
/// </para>
/// <para>
/// <b>Two safety rules.</b> (1) When the mob has an explicit target that is one of our heroes,
/// that hero is used, <em>in either direction</em> — a mob targeting P1 must tick with the global on
/// P1 even when P1 is already the global (no-op) and vice versa; never a blind global. (2) When
/// the mob has no target, the fallback picks the nearest detectable hero only if it is strictly
/// closer than the other by more than <see cref="EquidistantMarginPx"/> pixels — an equidistant
/// untargated mob keeps today's vanilla answer (hero1) so idle distant mobs cannot churn the global
/// every frame or re-steal aggro the mod's per-hero input pinning would not notice.
/// </para>
/// <para>
/// Hooks are registered per leaf class <em>and</em> on the <c>Mob</c> base because the proxy's
/// inheritance interception cannot be assumed: every class the audit flagged gets its own
/// registration on its own <c>fixedUpdate</c>/<c>postUpdate</c>/<c>preUpdate</c>. The depth counter
/// (<see cref="_mobTickDepth"/>) makes the double registration cheap: only the outermost wrapper
/// for a mob resolves and swaps. Explicit method hooks cover the few flows that can run outside a
/// wrapped tick (Death's choke pair gets a sticky victim field, Decoy detonation, Golem's
/// <c>setAttackTarget</c> override which overwrites <c>nemesisTarget = game.hero</c>, the crow's
/// captured hero field). Deferred (deliberately, each would be a partial or speculative fix): the
/// scripted cinematic closures in Death/DookuBeast (soul orbs, camera flights), Collector's
/// on-last-hit relocation (custom enum delegate return), the parry/attack-result staggers
/// (<c>applyAttackResult</c> fires with the VICTIM as self — swapping the global there would
/// redirect the stagger, but the attacker-owner resolution there is a second project), and
/// grenade chicken ownership (no owner field is exposed on the Grenade proxy).
/// </para>
/// </summary>
internal static partial class LocalCoop
{
    /// <summary>Closer-than-this (pixels) is the only way an untargated mob may prefer the other hero.</summary>
    private const double EquidistantMarginPx = 12.0;

    /// <summary>Wrapper nesting: only the outermost registration resolves and swaps for a mob.</summary>
    private static int _mobTickDepth;

    /// <summary>
    /// Death's choke grabs "the hero" for a scripted sequence: the target can change between the
    /// grab and the release, so the chosen victim is remembered until the choke ends.
    /// </summary>
    private static Hero? _deathChokeVictim;

    /// <summary>Registers the mob-AI hooks. Called from <see cref="InstallEntityHooks"/>.</summary>
    internal static void InstallMobAiHooks()
    {
        // The base bodies — the kill/hit perk beneficiaries and the aggressive-mobs hard-lock live
        // HERE, and run for every mob that does not override the method.
        Hook_Mob.fixedUpdate += OnMobFixedUpdate;
        Hook_Mob.postUpdate += OnMobPostUpdate;
        Hook_Mob.preUpdate += OnMobPreUpdate;
        Hook_Mob.onDie += OnMobDie;
        Hook_Mob.onDirectHitFromHero += OnMobDirectHitFromHero;

        // The hero side of the same family: self-perks the hero's own update/onDamage read from the
        // global (P_DmgRevenge, P_NoMobAround) are applied to `Game.ME.hero`. Ticking a hero as
        // itself re-points the global for the duration of its own code.
        Hook_Hero.fixedUpdate += OnHeroFixedUpdateAsSelf;
        Hook_Hero.postUpdate += OnHeroPostUpdateAsSelf;
        Hook_Hero.onDamage += OnHeroOnDamageAsSelf;

        // Cinematic invincibility (the intro's "no damage for the player") is applied to the global —
        // run it as each hero so BOTH are invulnerable during the intro, not only player one.
        Hook_Hero.toggleFullInvincibility += OnHeroToggleFullInvincibility;

        // Leaf registrations: every class the audit flagged, on the ticks it overrides itself. The
        // proxy's base hook only runs when the override calls super (the melee weapon
        // doAreaEffect lesson), so each leaf gets its own registration on its own delegate type;
        // the bodies stay shared through WrapMobTick, and the depth counter keeps the base+leaf
        // double registration cheap: only the outermost wrapper for a mob resolves and swaps.
        Hook_Death.fixedUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_Death.postUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_Collector.fixedUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_Collector.postUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_Queen.fixedUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_Queen.postUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_DookuBeast.fixedUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_DookuBeast.postUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_MamaTick.fixedUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_MamaTick.postUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_Beholder.fixedUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_Beholder.postUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_GardenerBoss.fixedUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_GardenerBoss.postUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_GardenerBoss.preUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        // The gardener's mushrooms are Interactives, not Mobs — same shape, proximity answer.
        Hook_Mushroom.fixedUpdate += (orig, self) =>
            RunAsHero(NearbyHeroFor(self, radiusPx: 120.0), () => orig(self));
        Hook_Mushroom.postUpdate += (orig, self) =>
            RunAsHero(NearbyHeroFor(self, radiusPx: 120.0), () => orig(self));
        Hook_Medusa.fixedUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_Medusa.postUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_HauntedArmor.fixedUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_HauntedArmor.postUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_Screamer.fixedUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_Screamer.postUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_Screamer.onDamage += (orig, self, a) => RunAsHero(AttackerHero(a) ?? MobHero(self), () => orig(self, a));
        Hook_ShopMimic.preUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_ShopMimic.fixedUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_ShopMimic.postUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_Giant.fixedUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_Giant.postUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_Giant.preUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_KingsHand.fixedUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_KingsHand.postUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_KingsHand.preUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_HandBase.fixedUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_HandBase.postUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_HandBase.preUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_Decoy.fixedUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_Decoy.postUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_Decoy.detonate += (orig, self) => RunAsHero(MobHero(self), () => orig(self));
        Hook_Earthquaker.fixedUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_Earthquaker.postUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_Imp.fixedUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_Imp.postUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_Morpher.fixedUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_FlyingCrow.fixedUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_FlyingCrow.postUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_MariaCat.fixedUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_MariaCat.postUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_AggressiveZombie.fixedUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_AggressiveZombie.postUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_SpikedSatyr.fixedUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_SpikedSatyr.postUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_BoilerRoom.fixedUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_BoilerRoom.postUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_AmazonBase.fixedUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_AmazonBase.postUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_AmazonBrutal.fixedUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_AmazonBrutal.postUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_AmazonBrutal.preUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_AmazonTactic.fixedUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_AmazonTactic.postUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_AmazonTactic.preUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_AmazonTactic.checkDiveAttackHit += (orig, self) => RunAsHero(MobHero(self), () => orig(self));
        Hook_AmazonSurvival.fixedUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_AmazonSurvival.postUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_AmazonSurvival.preUpdate += (orig, self) => WrapMobTick(self, () => orig(self));
        Hook_AmazonSurvival.touchHeroWreckingBall += (orig, self) =>
        {
            var touched = false;
            RunAsHero(MobHero(self), () => touched = orig(self));
            return touched;
        };
        Hook_AmazonSurvival.pullBack += (orig, self, atkType) => RunAsHero(MobHero(self), () => orig(self, atkType));
        Hook_DookuBeast.createFirePillar += (orig, self) => RunAsHero(MobHero(self), () => orig(self));
        Hook_Collector.checkDashHit += (orig, self) => RunAsHero(MobHero(self), () => orig(self));
        Hook_Queen.checkDashHit += (orig, self) => RunAsHero(MobHero(self), () => orig(self));
        Hook_Medusa.medusaLookUpdate += (orig, self) => RunAsHero(MobHero(self), () => orig(self));
        Hook_Medusa.tryStopFrenzyAttack += (orig, self) => RunAsHero(MobHero(self), () => orig(self));
        Hook_HauntedArmor.endAwake += (orig, self) => RunAsHero(MobHero(self), () => orig(self));
        Hook_Golem.postUpdate += (orig, self) => WrapMobTick(self, () => orig(self));

        // Flows that can run outside a wrapped tick, or that need more than the swap:
        Hook_Death.startHeroChoke += OnDeathStartHeroChoke;
        Hook_Death.endHeroChoke += OnDeathEndHeroChoke;
        Hook_Death.onHeroCaughtByChain += OnDeathHeroCaughtByChain;
        Hook_Golem.setAttackTarget += OnGolemSetAttackTarget;
        Hook_FlyingCrow.init += OnFlyingCrowInit;
    }

    /// <summary>Drops the scripted-sequence state. Called from the run reset.</summary>
    private static void ForgetMobAiState()
    {
        _deathChokeVictim = null;
        // The per-mob fallback targets belong to the level that just went away; drop them so a new
        // level starts with fresh distances (weak keys would collect them anyway, this is explicit).
        _mobTargetCache.Clear();
    }

    // ────────────────────────── who is "the hero" for this mob ──────────────────────────

    /// <summary>
    /// The hero a mob's own code should be answering to: its attack target (or nemesis) when that
    /// is one of our heroes; otherwise the nearest detectable hero, but only when it is strictly
    /// closer than the other by <see cref="EquidistantMarginPx"/>; otherwise null, meaning "the
    /// global already says the right thing".
    /// </summary>
    private static Hero? MobHero(Mob mob)
    {
        try
        {
            var game = Game.Class.ME;
            if (game == null || mob == null || mob.destroyed)
            {
                return null;
            }

            // An explicit target is read fresh every time: it is one field pair, and it is what makes
            // a mob that has just been hit aim at the hero that hit it on the very next tick.
            var t = mob.aTarget as Hero ?? mob.nemesisTarget as Hero;
            if (IsOurHero(t) && !t!.destroyed)
            {
                return t;
            }

            // The fallback below is the expensive half (two world distances plus two
            // `canBeDetected()` calls) and it is what every *idle* mob in the level runs on every
            // pre/fixed/post tick — dozens of mobs, several times a frame. Which hero a mob that has
            // no target yet would wake at cannot meaningfully change within a tenth of a second, so
            // the answer is cached per mob for that long.
            var now = System.Environment.TickCount64;
            if (_mobTargetCache.TryGetValue(mob, out var cached) && now < cached.Expires)
            {
                return cached.Hero;
            }

            var chosen = NearestHeroFor(mob);

            var entry = _mobTargetCache.GetValue(mob, MobTargetChoiceFactory);
            entry.Hero = chosen;
            entry.Expires = now + MobTargetCacheMs;
            return chosen;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The hero an untargeted mob would wake at: the strictly nearer detectable one, or null when the
    /// two are within <see cref="EquidistantMarginPx"/> of each other (the vanilla global answer).
    /// </summary>
    private static Hero? NearestHeroFor(Mob mob)
    {
        var hero1 = _hero1;
        var hero2 = _hero2;
        if (hero1 == null || hero2 == null || hero1.destroyed || hero2.destroyed)
        {
            return null;
        }

        // Fallback only for an idle mob: an un-targeted mob aims/wakes at whichever hero is
        // genuinely nearer. Equidistant keeps the global's answer (hero1 = vanilla).
        var d1 = WorldDistance(mob, hero1);
        var d2 = WorldDistance(mob, hero2);
        var p1Ok = IsHeroUsableTarget(hero1);
        var p2Ok = IsHeroUsableTarget(hero2);

        if (p2Ok && (!p1Ok || d2 < d1 - EquidistantMarginPx))
        {
            return hero2;
        }

        if (p1Ok && (!p2Ok || d1 < d2 - EquidistantMarginPx))
        {
            return hero1;
        }

        return null;
    }

    /// <summary>How long an untargeted mob keeps the hero the fallback picked.</summary>
    private const long MobTargetCacheMs = 100;

    /// <summary>One mob's cached fallback target (weak keys, so the entries die with the mobs).</summary>
    private sealed class MobTargetChoice
    {
        public Hero? Hero;
        public long Expires;
    }

    /// <summary>
    /// Per-mob cache behind <see cref="MobHero"/>'s idle-mob fallback. A weak table because mobs come
    /// and go all the time and nothing here may keep one alive (or grow with the level).
    /// </summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Mob, MobTargetChoice>
        _mobTargetCache = new();

    /// <summary>Entry factory for <see cref="_mobTargetCache"/> (a cached delegate, not a per-call lambda).</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Mob, MobTargetChoice>.CreateValueCallback
        MobTargetChoiceFactory = _ => new MobTargetChoice();

    private static bool IsHeroUsableTarget(Hero hero)
    {
        try
        {
            return !hero.destroyed && hero.life > 0 && hero.canBeDetected();
        }
        catch
        {
            return false;
        }
    }

    private static double WorldDistance(Entity a, Entity b)
    {
        var dx = (((double)a.cx + a.xr) - ((double)b.cx + b.xr)) * 24.0;
        var dy = (((double)a.cy + a.yr) - ((double)b.cy + b.yr)) * 24.0;
        return System.Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// Bidirectional global re-point around <paramref name="body"/>: swaps in <paramref name="hero"/>
    /// whenever it is one of our heroes and not already the global, restores in a finally, and an
    /// exception never escapes a hook. Unlike <c>RunAsTouchedHero</c> this may point the global at
    /// player one too (a mob whose target is P1 must answer with P1 even when the current global was
    /// left on P2 by an outer wrapper).
    /// </summary>
    private static void RunAsHero(Hero? hero, Action body)
    {
        var game = Game.Class.ME;
        if (!Enabled || game == null || hero == null || ReferenceEquals(game.hero, hero))
        {
            body();
            return;
        }

        var previous = game.hero;
        try
        {
            game.hero = hero;
            body();
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] a global-hero re-point hook failed: {Message}", ex.Message);
        }
        finally
        {
            game.hero = previous;
        }
    }

    /// <summary>Outermost wrapper only: nested leaf/base registrations run straight through.</summary>
    private static void WrapMobTick(Mob self, Action orig)
    {
        if (_mobTickDepth > 0)
        {
            orig();
            return;
        }

        // A downed hero must not keep a mob's aggro: the game only drops the target at life <= 0,
        // and a downed hero is held at 1 (see DownedLife), so a mob that locked on before the fall
        // kept walking to the spot it fell instead of fighting the player still standing — the
        // "downed P2 still draws aggro" bug. P1 looked fine only because a downed P1 is moved onto
        // the survivor for the audio listener, so its aggro landed next to the living player.
        //
        // The target is HANDED OVER rather than cleared: a cleared nemesisTarget is what makes the
        // boss AI dereference null (the `collisionMode` crash documented in Down.cs), and a mob
        // with a valid target simply walks to the wrong hero instead of re-searching.
        try
        {
            var current = self.nemesisTarget;
            if (IsDownedHero(current))
            {
                var survivor = LivingPartner(current as Hero);
                if (survivor != null)
                {
                    self.nemesisTarget = survivor;
                }
            }
        }
        catch (Exception ex)
        {
            LogVerbose($"[LocalCoop] could not hand a downed hero's aggro to the survivor: {ex.Message}");
        }

        var hero = MobHero(self);
        _mobTickDepth++;
        try
        {
            RunAsHero(hero, orig);
        }
        finally
        {
            _mobTickDepth--;
        }
    }

    // ────────────────────────── mob ticks and hit/death bodies ──────────────────────────

    private static void OnMobFixedUpdate(Hook_Mob.orig_fixedUpdate orig, Mob self)
    {
        WrapMobTick(self, () => orig(self));
    }

    private static void OnMobPostUpdate(Hook_Mob.orig_postUpdate orig, Mob self)
    {
        WrapMobTick(self, () => orig(self));
    }

    private static void OnMobPreUpdate(Hook_Mob.orig_preUpdate orig, Mob self)
    {
        WrapMobTick(self, () => orig(self));
    }

    /// <summary>
    /// A mob's death body hands the on-kill rewards (heals, affects, cooldown resets, infection
    /// reduction, execution perks) to <c>Game.ME.hero</c> and gates them on "is the hero alive" —
    /// with the mob's target as the global, the killer's side of that bookkeeping lands on the hero
    /// that actually drove the fight. The dying mob is also remembered for the duration so the
    /// curse-reduction calls inside it (<c>reduceCurse</c>, see <see cref="OnHeroReduceCurse"/>)
    /// can attribute the kill when health — and with it the curse — is independent.
    /// </summary>
    private static void OnMobDie(Hook_Mob.orig_onDie orig, Mob self)
    {
        var hero = MobHero(self);
        _dyingMob = self;
        try
        {
            RunAsHero(hero, () => orig(self));
        }
        finally
        {
            _dyingMob = null;
        }
    }

    /// <summary>The hero that just landed a hit on this mob — the beneficiary of on-hit perks.</summary>
    private static Hero? AttackerHero(AttackData? a)
    {
        try
        {
            if (a == null)
            {
                return null;
            }

            var e = a.carrier ?? a.source;
            var hero = e as Hero;
            return hero != null && IsOurHero(hero) ? hero : null;
        }
        catch
        {
            return null;
        }
    }

    private static void OnMobDirectHitFromHero(Hook_Mob.orig_onDirectHitFromHero orig, Mob self, AttackData a)
    {
        RunAsHero(AttackerHero(a) ?? MobHero(self), () => orig(self, a));
    }

    /// <summary>Same re-point for the mob's onDamage (the Screamer's aggro-forcing lives in its body).</summary>
    private static void OnMobOnDamage(Hook_Mob.orig_onDamage orig, Mob self, AttackData a)
    {
        RunAsHero(AttackerHero(a) ?? MobHero(self), () => orig(self, a));
    }

    // ────────────────────────── the hero's own global reads ──────────────────────────

    /// <summary>
    /// <c>Hero.fixedUpdate</c>/<c>postUpdate</c>/<c>onDamage</c> read the global for self-perks
    /// (<c>P_DmgRevenge</c>, <c>P_NoMobAround</c>): the buff check is <c>this.inventory</c> but the
    /// <c>setAffectS</c> lands on <c>Game.ME.hero</c>, so player two's revenge buff went to player
    /// one. Run the hero's own code as itself.
    /// </summary>
    private static void OnHeroFixedUpdateAsSelf(Hook_Hero.orig_fixedUpdate orig, Hero self)
    {
        RunAsHero(IsOurHero(self) ? self : null, () => orig(self));
    }

    private static void OnHeroPostUpdateAsSelf(Hook_Hero.orig_postUpdate orig, Hero self)
    {
        RunAsHero(IsOurHero(self) ? self : null, () => orig(self));
    }

    private static void OnHeroOnDamageAsSelf(Hook_Hero.orig_onDamage orig, Hero self, AttackData a)
    {
        RunAsHero(IsOurHero(self) ? self : null, () => orig(self, a));
    }

    /// <summary>
    /// <c>toggleFullInvincibility</c> writes the no-damage affect (and the mob-side stun) onto the
    /// GLOBAL hero — the boss intro made only player one invulnerable. Run it once per hero so the
    /// pair is covered; the second pass is the whole point.
    /// </summary>
    private static void OnHeroToggleFullInvincibility(Hook_Hero.orig_toggleFullInvincibility orig, Hero self, bool on)
    {
        if (!Enabled || !IsOurHero(self) || self.destroyed)
        {
            orig(self, on);
            return;
        }

        var other = ReferenceEquals(self, _hero1) ? _hero2 : _hero1;

        RunAsHero(self, () => orig(self, on));

        if (other != null && !other.destroyed)
        {
            RunAsHero(other, () => orig(self, on));
        }
    }

    // ────────────────────────── explicit per-site hooks ──────────────────────────

    /// <summary>Death's choke: pick a victim now and hold it through the whole scripted sequence.</summary>
    private static void OnDeathStartHeroChoke(Hook_Death.orig_startHeroChoke orig, Death self)
    {
        _deathChokeVictim = MobHero(self);
        RunAsHero(_deathChokeVictim, () => orig(self));
    }

    private static void OnDeathEndHeroChoke(Hook_Death.orig_endHeroChoke orig, Death self)
    {
        var victim = _deathChokeVictim;
        _deathChokeVictim = null;
        RunAsHero(victim ?? MobHero(self), () => orig(self));
    }

    /// <summary>The chain-grab reaction reads the global for "who got caught" — it is the chain's victim.</summary>
    private static bool OnDeathHeroCaughtByChain(Hook_Death.orig_onHeroCaughtByChain orig, Death self)
    {
        var victim = _deathChokeVictim ?? MobHero(self);
        var answer = false;
        RunAsHero(victim, () => answer = orig(self));
        return answer;
    }

    /// <summary>
    /// <c>Golem.setAttackTarget</c> overrides and then overwrites <c>nemesisTarget = game.hero</c>,
    /// undoing a correct target. Run the game's call and repair the nemesis afterwards when the
    /// chosen target was one of our heroes.
    /// </summary>
    private static void OnGolemSetAttackTarget(Hook_Golem.orig_setAttackTarget orig, Golem self, Entity t)
    {
        orig(self, t);

        try
        {
            if (IsOurHero(t) && !ReferenceEquals(self.nemesisTarget, t))
            {
                self.nemesisTarget = t;
            }
        }
        catch (Exception ex)
        {
            LogVerbose($"[LocalCoop] could not keep the golem's nemesis on the chosen hero: {ex.Message}");
        }
    }

    /// <summary>
    /// <c>FlyingCrow.init</c> captures <c>this.hero = game.hero</c> once — the crow was permanently
    /// bound to player one (it only ever wakes/crosses for P1). Point the captured field at player
    /// two after the game's own init so its per-frame logic answers to P2; the mob-tick wrapper
    /// covers the global reads the crow still makes.
    /// </summary>
    private static void OnFlyingCrowInit(Hook_FlyingCrow.orig_init orig, FlyingCrow self)
    {
        orig(self);

        try
        {
            if (Enabled && _hero2 != null && !_hero2.destroyed)
            {
                self.hero = _hero2;
            }
        }
        catch (Exception ex)
        {
            LogVerbose($"[LocalCoop] could not rebind the crow's hero: {ex.Message}");
        }
    }
}
