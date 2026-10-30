using dc;
using dc.en;
using dc.pr;
using dc.tool;
using dc.tool.atk;
using dc.ui;
using HaxeProxy.Runtime;
using ModCore.Utilities;

namespace DeadCellsLocalMultiplayerMod;

/// <summary>
/// Part of <see cref="LocalCoop"/>: hero event hooks — death, cells, curse and infection
/// redirection, friendly fire, and equipment sync.
/// </summary>
internal static partial class LocalCoop
{
    /// <summary>
    /// Handles a hero whose life hit 0. Damage is no longer redirected to player one: the hero
    /// that is actually hit runs its own <c>onDamage</c>, so its hit reaction plays on the right
    /// hero and the shared life pool is converged by <see cref="SyncSharedLife"/>.
    /// <para>
    /// <b>Shared health</b>: either hero's death ends the run. Player two's death is redirected to
    /// player one so the single run ends, and player two is removed right away (its postUpdate
    /// would crash on a null sprite once player one's death tears the level's graphics down).
    /// </para>
    /// <para>
    /// <b>Independent health</b>: a hero whose life hits 0 goes "down" (frozen and hidden, kept in
    /// the level) while a teammate is still alive, and the run continues. Only when every hero is
    /// down does the run end — through the global hero (player one) so the death screen behaves
    /// normally. Down heroes revive at 1 HP on the next level (see <see cref="ReviveDownHeroes"/>).
    /// </para>
    /// </summary>
    private static void OnHeroDie(Hook_Hero.orig_onDie orig, Hero self)
    {
        if (!Enabled)
        {
            orig(self);
            return;
        }

        // PvP: reaching the death flow at all means a path that skipped the tryToPreventDeath gate
        // (a direct kill()). It is tallied exactly the same way — hold the hero at the floor value,
        // mark it down, score the round — because a hero silently going down with no point awarded is
        // how a match stalls. The tally itself is guarded against double-counting by IsDown inside
        // ScoreRoundLoss' caller and by the !IsDown check below, not by a separate "ending" flag:
        // the mod never ends this run itself (training mode respawns killed heroes; see
        // LocalCoop.Pvp.cs for why).
        if (IsPvp)
        {
            if (!IsDown(self))
            {
                try
                {
                    self.life = DownedLife;
                }
                catch (Exception ex)
                {
                    LogWarn("[PvP] could not hold a dying hero at the floor value: {Message}", ex.Message);
                }

                // Same consumption as the tryToPreventDeath gate: a direct kill() on a cursed hero
                // must also spend the curse rather than leave it on the pair.
                ConsumeCurseOnPreventedDeath(self);

                MarkDown(self);
                if (Pvp == PvpState.Fighting)
                {
                    ScoreRoundLoss(self);
                }
            }

            return;
        }

        if (!PvpHealthIndependent)
        {
            var redirected = ReferenceEquals(self, _hero2) && _hero1 != null && !_hero1.destroyed;
            var target = redirected ? _hero1 : self;
            orig(target);
            if (ReferenceEquals(self, _hero1) || redirected)
            {
                RemoveSecondHero(redirected ? "player two died" : "player one died");
            }
            return;
        }

        // Independent health: down while a teammate is alive, run continues.
        var other = ReferenceEquals(self, _hero1) ? _hero2 : _hero1;
        if (IsAlive(other))
        {
            // Same consumption as the tryToPreventDeath gate: a death that reached onDie without
            // passing the gate (a direct kill()) must still spend the cursed hero's curse.
            ConsumeCurseOnPreventedDeath(self);

            MarkDown(self);
            return;
        }

        // Everyone is down: end the run through the global hero (player one). Unfreeze it first if
        // it was the one that went down.
        var endHero = _hero1 != null && !_hero1.destroyed ? _hero1 : self;
        try
        {
            if (IsDown(endHero))
            {
                endHero.life = 0;
                endHero.isFreeze = false;
            }
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not unfreeze the last hero: {Message}", ex.Message);
        }
        orig(endHero);
        RemoveSecondHero("all heroes down");
    }

    /// <summary>
    /// The vanilla "survive a lethal hit" gate (<c>Entity.tryToPreventDeath</c>): it is called when
    /// an attack would bring a hero to 0 HP, and returning <c>true</c> prevents the death. With
    /// independent health, a hero whose teammate is still alive must not die — but simply skipping
    /// <c>onDie</c> in <see cref="OnHeroDie"/> is not enough: the game has already *started* its
    /// death sequence, which fades/mutes the audio (<c>Audio.ME.keyFrameCineMute</c>) and never
    /// restores it, so every SFX stayed silent until a level change rebuilt the audio manager — the
    /// "P1 dies and the sound breaks while P2 is still alive" bug (only P1, the global hero, showed
    /// it). Preventing the hit here means the death sequence never starts, the audio is untouched,
    /// and the hero simply stays at 0 HP for the per-frame down logic to keep it there and hide it.
    /// </summary>
    private static bool OnHeroTryToPreventDeath(
        Hook_Hero.orig_tryToPreventDeath orig,
        Hero self,
        AttackData a,
        double prevLife)
    {
        if (Enabled && self != null
            && (ReferenceEquals(self, _hero1) || ReferenceEquals(self, _hero2)))
        {
            // PvP: a lethal hit is not a down-and-wait — it is the round. Hold the hero at the floor
            // value and mark it down so the game's death flow never starts (returning true is what
            // stops it, and stopping it is what leaves the audio manager untouched). The point is
            // only tallied while a round is actually being fought: a hero finished off by leftover
            // damage-over-time during the break must not gift a second point, and
            // StartRound revives it anyway.
            if (IsPvp && !IsDown(self))
            {
                try
                {
                    self.life = DownedLife;
                }
                catch (Exception ex)
                {
                    LogWarn("[PvP] could not hold a dying hero at the floor value: {Message}", ex.Message);
                }

                // The cursed hero pays here: this gate answers before the vanilla "cursed: the
                // next hit kills" refusal, so consume the curse or it would linger and later kill
                // the last hero standing through the vanilla path.
                ConsumeCurseOnPreventedDeath(self);

                MarkDown(self);
                if (Pvp == PvpState.Fighting)
                {
                    ScoreRoundLoss(self);
                }

                return true;
            }

            // Independent health (coop): down while a teammate is alive, run continues.
            if (PvpHealthIndependent)
            {
                var other = ReferenceEquals(self, _hero1) ? _hero2 : _hero1;
                if (IsAlive(other))
                {
                    // The game's OWN "survive a lethal hit" answers first. Everything a player brings
                    // for exactly this moment lives in the vanilla gate: the YOLO mutation
                    // (`tryToApplyYoloPerk`, the `P_Yolo` perk), the one-hit protection,
                    // `P_DamnedVigor`, `P_Disengage`, the EvilSword, the Queen's mercy. Answering the
                    // hit here before that gate ever ran is what made them silently do nothing for
                    // the hero that still had a living teammate: player one took the hit, went down
                    // and the mutation was left unspent — while player two, who died last (no living
                    // teammate, so this branch was skipped and `orig` ran), was saved by the very
                    // same mutation. Honouring `orig` first means a mutation willing to pay for the
                    // hit pays it, and only a hit the game itself refuses to prevent becomes a down.
                    //
                    // Safe to call: `tryToPreventDeath` is a question, not the death flow. A `false`
                    // answer leaves nothing running — the sequence only starts once the caller sees
                    // no prevention, and we answer `true` below, so it never does.
                    if (orig(self, a, prevLife))
                    {
                        Log($"[LocalCoop] {HeroName(self)} was saved by the game's own death prevention");
                        return true;
                    }

                    try
                    {
                        // Hold the hero at DownedLife (1), never at 0. A downed hero stays in the level,
                        // and Mob.fixedUpdate clears its nemesisTarget as soon as that target's
                        // life <= 0 — after which the boss AI (TimeKeeper.behaviourAi, and Queen too)
                        // dereferences nemesisTarget with no null check and crashes the game with
                        // "Null access .collisionMode" (see LocalCoop.Down.cs / DownedLife).
                        // Returning true below is what stops the death flow, so the hero never dies from
                        // this value; the empty bar the player sees comes from the P2 bar, which reports
                        // 0 for a downed hero whatever the real life is.
                        self.life = DownedLife;
                    }
                    catch (Exception ex)
                    {
                        LogWarn("[LocalCoop] could not drop a prevented death to 0 HP: {Message}", ex.Message);
                    }

                    // The cursed hero pays here: this gate answers before the vanilla "cursed: the
                    // next hit kills" refusal, so consume the curse or it would linger on this hero
                    // (and its mirrored carrier) and later kill the last hero standing through the
                    // vanilla path — the "P2's curse did nothing, P1 died of the curse" bug.
                    ConsumeCurseOnPreventedDeath(self);

                    MarkDown(self);
                    Log($"[LocalCoop] {HeroName(self)} would die but a teammate is alive: death prevented (down)");
                    return true;
                }
            }
        }

        return orig(self, a, prevLife);
    }

    /// <summary>
    /// Friendly fire. <c>canBeHitBy</c> is the game's gate before damage is applied. For a hit
    /// where both the target and the source are the two heroes, honour the <see cref="FriendlyFire"/>
    /// setting: allow it when on, block it when off (the vanilla same-team check already blocks it,
    /// but forcing the answer keeps the behaviour explicit). Any other source falls through to the
    /// game's own logic.
    /// <para>
    /// The source <c>by</c> is not always the partner hero — a melee weapon belongs to its hero,
    /// shares its team, and is passed as <c>by</c> when the weapon hits. So we accept any
    /// source whose team is <c>teamHero</c>, which catches both hero-on-hero (ranged projectiles,
    /// homunculus), melee weapon-on-hero and head-on-hero hits. A downed hero returns false
    /// (cannot be hit while waiting to revive).
    /// </para>
    /// </summary>
    private static bool OnHeroCanBeHitBy(Hook_Hero.orig_canBeHitBy orig, Hero self, Entity by)
    {
        // A downed hero is frozen and out of the fight; nothing can hit it (this also stops a
        // stray hit from re-triggering its death while it waits to revive).
        if (IsDown(self))
        {
            return false;
        }

        if (!Enabled || _hero1 == null || _hero2 == null || by == null
            || (!ReferenceEquals(self, _hero1) && !ReferenceEquals(self, _hero2)))
        {
            return orig(self, by);
        }

        // A hero never targets itself (this hook is asked with the hero on both sides).
        if (ReferenceEquals(self, by))
        {
            return false;
        }

        // Friendly fire (targeting gate). When on, a partner's bullet / thrown head / item entity has
        // to be allowed to reach this hero — but this hero's OWN ones must not (the old "using an item
        // hurts its user" bug). The team system cannot tell those apart, so ownership is read from the
        // entity itself: OwningHero resolves a projectile's firing hero from its AttackData, and a hit
        // owned by this hero is refused before the game applies any damage. An entity whose owner cannot
        // be resolved is left to `FriendlyFire` as before, so nothing that used to work regresses.
        if (IsFriendlyFireTarget(self, by))
        {
            // PvP: friendly fire is always on, and a hit is refused only during a round's grace
            // window so nobody can be scored against on the spawn. Co-op keeps the menu setting.
            // One predicate (FriendlyFireActive) for all three gates, so no path can disagree.
            var hits = ReferenceEquals(OwningHero(by), self) ? false : FriendlyFireActive;
            if (Verbose)
            {
                Log($"[LocalCoop] FF canBeHitBy {HeroName(self)} by {Describe(by)} -> {hits}");
            }
            return hits;
        }

        return orig(self, by);
    }

    /// <summary>
    /// Friendly fire (the team gate). <c>isOpponent</c> is the game's team check that decides whether
    /// damage between two entities is applied — for a bullet it is the gate that actually lets the hit
    /// land, which is why <c>canBeHitBy</c> alone was never enough.
    /// <para>
    /// Both heroes share <c>teamHero</c>, so by default neither is an opponent of the other nor of the
    /// partner's projectile. The old design made <c>teamHero</c> oppose itself to open that up, but the
    /// cost was that a hero's OWN bullet / item entity also counted as its opponent — "using an item
    /// hurts its user". <c>SyncFriendlyFireTeams</c> stopped doing that (removing self-opposition), which
    /// fixed the self-harm but silently killed ranged friendly fire, because a partner's bullet now had
    /// no path to be an opponent of a hero.
    /// </para>
    /// <para>
    /// The fix resolves ownership instead of trusting the team relation: a hero-team entity whose owner
    /// is the OTHER hero is an opponent (partner's ranged hit lands); one whose owner is THIS hero is
    /// not (no self-harm). OwningHero knows bullets for certain (via their AttackData source); other
    /// hero-team entities fall through to the game's own answer, which keeps them as inert as they were
    /// before — so ranged weapons are what this restores, nothing regresses.
    /// </para>
    /// </summary>
    private static bool OnEntityIsOpponent(Hook_Entity.orig_isOpponent orig, Entity self, Entity other)
    {
        if (!Enabled || !FriendlyFireActive || _hero1 == null || _hero2 == null
            || self == null || other == null)
        {
            return orig(self, other);
        }

        // Block self-vs-self regardless (the gate is symmetric and the game's own code can ask it).
        if (ReferenceEquals(self, other))
        {
            return false;
        }

        var isSelfHero = ReferenceEquals(self, _hero1) || ReferenceEquals(self, _hero2);
        var isOtherHero = ReferenceEquals(other, _hero1) || ReferenceEquals(other, _hero2);

        // Direct hero-vs-hero: partners, so they are opponents of each other.
        if (isSelfHero && isOtherHero)
        {
            return true;
        }

        // A hero on one side and a hero-team projectile on the other: admit it only when the
        // projectile belongs to the *other* hero. Ownership of an entity this hero does not own is
        // read as the partner's; an entity this hero owns (its own bullet) is refused — self-harm.
        if (isSelfHero || isOtherHero)
        {
            var heroSide = isSelfHero ? self : other;
            var entity = isSelfHero ? other : self;

            if (IsHeroTeam(heroSide) && IsHeroTeam(entity))
            {
                var owner = OwningHero(entity);
                if (owner != null && ReferenceEquals(owner, heroSide))
                {
                    return false;
                }
                if (owner != null)
                {
                    return true;
                }
            }
        }

        return orig(self, other);
    }

    /// <summary>
    /// The hero that a damaging entity actually belongs to, or null when it belongs to neither.
    /// <para>
    /// This is what the team system cannot tell and is the only thing that separates a partner's
    /// bullet (friendly fire should land) from a hero's own projectile / thrown head / item entity
    /// (which must never hurt its user). A hero is its own owner; a bullet carries the
    /// <c>AttackData</c> whose <c>source</c> is the hero that fired it (the game resolves the same
    /// way in <c>Hero.onDamage</c>, via <c>a.carrier ?? a.source</c>); anything else has no owner a
    /// hero can be blamed for.
    /// </para>
    /// </summary>
    private static Hero? OwningHero(Entity? e)
    {
        if (e == null)
        {
            return null;
        }

        if (ReferenceEquals(e, _hero1))
        {
            return _hero1;
        }
        if (ReferenceEquals(e, _hero2))
        {
            return _hero2;
        }

        try
        {
            // A projectile records the attack that spawned it; its source is the firing hero.
            if (e is dc.en.Bullet bullet)
            {
                var atk = bullet.atk;
                var src = atk?.source;
                if (ReferenceEquals(src, _hero1))
                {
                    return _hero1;
                }
                if (ReferenceEquals(src, _hero2))
                {
                    return _hero2;
                }
            }
        }
        catch
        {
            // A transient/destroyed entity: treat it as unowned rather than crash a hook.
        }

        return null;
    }

    /// <summary>
    /// True if <paramref name="other"/> can be a friendly-fire source for <paramref name="self"/>.
    /// <para>
    /// This is the targeting half of friendly fire and it deliberately accepts <i>any</i> hero-team
    /// entity, not just the other hero: a partner's bullet, weapon, thrown head or item entity has to
    /// be allowed to hit a hero, or ranged friendly fire cannot happen at all. Whether such a source
    /// actually belongs to <paramref name="self"/> (and so must be refused to stop "using an item
    /// hurts its user") is decided by the caller via <see cref="OwningHero"/>, because this predicate
    /// cannot see an entity's owner — the earlier attempt to decide it here is what forced a choice
    /// between "a hero's own items hurt it" and "a partner's ranged weapon does nothing".
    /// </para>
    /// </summary>
    private static bool IsFriendlyFireTarget(Hero self, Entity other)
    {
        if (other == null || !IsHeroTeam(self) || !IsHeroTeam(other))
        {
            return false;
        }

        // Direct hero→hero hit, and only the *other* hero counts: a hero must never be a
        // friendly-fire source for itself.
        if (ReferenceEquals(other, _hero1) || ReferenceEquals(other, _hero2))
        {
            return !ReferenceEquals(other, self);
        }

        // Any other hero-team entity — a projectile, a weapon, a thrown head, a homunculus — is
        // allowed here; ownership is checked against the AttackData when the damage lands.
        return true;
    }

    /// <summary>True when <paramref name="e"/>'s team equals <c>teamHero</c> of the current level.</summary>
    private static bool IsHeroTeam(Entity e)
    {
        try
        {
            var owner = _hero1 != null ? _hero1
                : _hero2 != null ? _hero2
                : null;
            var levelTeam = owner?._level?.teamHero;
            if (levelTeam == null)
            {
                return false;
            }
            // Haxe binds Entity.team to the C# `_team` property (double-underscore Haxe proxy
            // convention); the single-underscore setter we use elsewhere (hero2.set_team(...)) is
            // the corresponding write hook on Hero. Reading the property is enough to compare
            // identity with _level.teamHero.
            var entityTeam = e._team;
            return entityTeam != null && ReferenceEquals(entityTeam, levelTeam);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Short label for a participant in FF logs (P1 / P2 / class name).</summary>
    private static string Describe(Entity e)
    {
        if (e == null)
        {
            return "(null)";
        }
        if (ReferenceEquals(e, _hero1))
        {
            return "P1";
        }
        if (ReferenceEquals(e, _hero2))
        {
            return "P2";
        }
        try
        {
            return e._team?.ToString() ?? "(entity)";
        }
        catch
        {
        }
        return "(entity)";
    }

    /// <summary>
    /// Friendly fire for <b>melee</b>. The game's melee target collection is team-filtered, so the
    /// partner (same <c>teamHero</c>) is never a candidate and the per-weapon
    /// <c>hitFromWeapon</c> — the method that actually applies the damage, overridden per weapon so
    /// the base hook cannot observe it — is never called for the partner. <c>doAreaEffect</c> *is*
    /// called for every swing, so from here, when friendly fire is on and the partner stands inside
    /// the swing's area, we invoke the weapon's own <c>hitFromWeapon(partner)</c> directly. That
    /// bypasses the team-filtered collection but reuses the weapon's real damage code (damage,
    /// crit, hit reaction), so nothing is faked.
    /// </summary>
    private static void OnWeaponDoAreaEffect(
        Hook_Weapon.orig_doAreaEffect orig,
        Weapon self,
        Area area)
    {
        orig(self, area);

        if (!Enabled || !FriendlyFireActive || _hero1 == null || _hero2 == null || self == null)
        {
            return;
        }

        try
        {
            var owner = self.owner;
            var partner = ReferenceEquals(owner, _hero1) ? _hero2
                : ReferenceEquals(owner, _hero2) ? _hero1
                : null;
            if (partner == null || partner.destroyed || IsDown(partner) || IsDown(owner))
            {
                return;
            }
            if (!WithinMeleeReach(partner, owner))
            {
                return;
            }

            int cycle = 0;
            self.hitFromWeapon(partner, ref cycle);
            if (Verbose)
            {
                Log($"[LocalCoop][FF] melee FF: weapon={self.GetType().Name} owner={HeroName(owner)} " +
                    $"-> hitFromWeapon({HeroName(partner)})");
            }
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] melee friendly-fire injection failed: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// True when <paramref name="target"/> is within melee reach of <paramref name="owner"/> — the
    /// swing is centred on the owner, so a short sprite-distance check is a reliable proxy (and
    /// avoids the pixel/case coordinate mismatch: <c>Entity</c> only exposes case coords, while the
    /// sprite carries the pixel position). Falls back to case-coordinate proximity when a sprite is
    /// unavailable.
    /// </summary>
    private static bool WithinMeleeReach(Entity target, Entity owner)
    {
        try
        {
            var ts = target.spr;
            var os = owner.spr;
            if (ts != null && os != null)
            {
                double dx = ts.x - os.x;
                double dy = ts.y - os.y;
                return dx * dx + dy * dy <= 64.0 * 64.0;
            }
        }
        catch
        {
            // fall through to case proximity
        }

        return System.Math.Abs(target.cx - owner.cx) <= 2
            && System.Math.Abs(target.cy - owner.cy) <= 1;
    }

    /// <summary>Registers the entity-level hooks (friendly fire, plant trap). Called from <c>Install</c>.</summary>
    internal static void InstallEntityHooks()
    {
        // Team gate: the two heroes are made opponents when friendly fire is on.
        Hook_Entity.isOpponent += OnEntityIsOpponent;
        // Interaction attribution: remember which of our heroes last activated an interactable.
        // Curse sources are hard-coded onto the global hero, so with independent health the
        // curse this activation raises must be attributed back to the hero that used the chest /
        // door (see CurseAttributionHero).
        Hook_Entity.onActivate += OnEntityOnActivate;
        // Melee: the team-filtered target collection never considers the partner, so from the
        // swing's area hook we inject the weapon's own hitFromWeapon(partner) instead.
        Hook_Weapon.doAreaEffect += OnWeaponDoAreaEffect;
        // Plant trap: the game's own victim resolution is the global hero, so player two never
        // triggered one. See Coop/LocalCoop.Plant.cs.
        InstallPlantHooks();
        // The other traps that resolve their victim through the global hero (retractable spikes,
        // the crumbling floors, the swinging blade, the pulley, the flipping platform). See
        // Coop/LocalCoop.Traps.cs.
        InstallTrapHooks();
        // The mob/boss half of the global-hero audit: every flagged mob's own AI tick, hit and
        // death body runs with Game.ME.hero pointed at ITS target, retiring the aim/victim/buff
        // from-the-global family. See Coop/LocalCoop.MobAi.cs.
        InstallMobAiHooks();
        // The interactive-object half: fountains, scratchable floors, elevators, keyholes, NPC
        // auto-activation, auto-pickup, gates and deployed buffs answer to the acting hero.
        // See Coop/LocalCoop.Interactables.cs.
        InstallInteractableHooks();
    }

    /// <summary>
    /// The HUD has a single hero life bar bound to the global hero (player one), and every hero's
    /// <c>updateLifeBar()</c> writes to it. Hero two is a real Hero entity, so its own update would
    /// overwrite that bar with player two's life — once the two heroes' health differs (independent
    /// health), the bar renders as a red sliver + white block (its fill and recover segments fight
    /// over two different values).
    /// <para>
    /// Player two's write is therefore redirected instead of dropped: it goes to the clone's own
    /// bar (see <c>Coop/LocalCoop.P2HudClone.cs</c>), which is the same widget class fed the same
    /// values, so player two gets the vanilla fill, the delayed recover segment and the heal /
    /// damage feedback. When the clone is not up (shared health — nothing to show — or the clone
    /// latched its error flag) the write is simply skipped, exactly as before.
    /// </para>
    /// </summary>
    private static void OnHeroUpdateLifeBar(Hook_Hero.orig_updateLifeBar orig, Hero self)
    {
        if (Enabled && ReferenceEquals(self, _hero2))
        {
            PaintP2CloneLife(self);
            return;
        }

        // A downed hero is deliberately held in the level at `DownedLife` (1 HP) so the boss AI
        // cannot dereference a cleared nemesisTarget (see Coop/LocalCoop.Down.cs) — but the bar
        // must still read empty, because "1 HP and cannot move" reads as a bug to the player
        // while "0" reads as "down". `updateLifeBar` only *reads* life, so point it at 0 for the
        // duration of the call and put the real value straight back: the game's own logic (and
        // the crash it avoids) still sees the hero at 1 HP.
        if (Enabled && IsDown(self) && self.life > 0)
        {
            var held = self.life;
            try
            {
                self.life = 0;
                orig(self);
            }
            finally
            {
                self.life = held;
            }
            return;
        }

        orig(self);
    }

    /// <summary>
    /// Drinking a flask heals and then repaints the flask counter on the HUD. The counter is part of
    /// the inventory, so with independent equipment player two's flask must stay off player one's
    /// HUD — its own count is drawn by the P2 readout.
    /// <para>
    /// <b>The heal is never dropped.</b> <c>Hero.heal()</c> <i>is</i> the heal — skipping the call to
    /// keep the HUD clean also kept player two from healing at all (its life stayed flat while
    /// player one's flask counter moved). So the call always runs and the shared counter is put back
    /// to player one's own numbers afterwards.
    /// </para>
    /// </summary>
    private static void OnHeroHeal(Hook_Hero.orig_heal orig, Hero self, int v)
    {
        var blocked = !P2FlaskMayWriteSharedHud(self);
        orig(self, v);
        if (blocked)
        {
            RestoreSharedFlaskCounter();
        }
    }

    /// <summary>Same as <see cref="OnHeroHeal"/>, for a full heal.</summary>
    private static void OnHeroFullHeal(Hook_Hero.orig_fullHeal orig, Hero self)
    {
        var blocked = !P2FlaskMayWriteSharedHud(self);
        orig(self);
        if (blocked)
        {
            RestoreSharedFlaskCounter();
        }
    }

    /// <summary>
    /// Puts player one's own flask charges back on the shared HUD after an event that was not
    /// player one's. The counter belongs to the hero's <c>Heal</c> main skill, so the numbers are
    /// read from player one's own skill rather than cached.
    /// </summary>
    private static void RestoreSharedFlaskCounter()
    {
        try
        {
            var hud = Game.Class.ME?.hud;
            var hero1 = _hero1;
            if (hud == null || hero1 == null || hero1.destroyed)
            {
                return;
            }

            var (cur, max) = ReadFlaskCharges(hero1);
            hud.setHealings(cur, max);
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not restore player one's flask counter: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Last line of defence for the shared flask counter: the game writes it through
    /// <c>HUD.setHealings</c>, which the skill code can call directly (bypassing
    /// <c>Hero.heal</c>). With independent equipment a write carrying player two's count is replaced
    /// by player one's, so player one's bar can never show player two's flasks. Writes that already
    /// match player one are passed through untouched, so player one's own flasks still update.
    /// </summary>
    private static void OnHudSetHealings(Hook_HUD.orig_setHealings orig, dc.ui.HUD self, int cur, int max)
    {
        if (Enabled && !SharedEquipment && _hero1 != null && !_hero1.destroyed && _hero2 != null)
        {
            try
            {
                var (ownCur, ownMax) = ReadFlaskCharges(_hero1);
                if (cur != ownCur)
                {
                    var (otherCur, _) = ReadFlaskCharges(_hero2);
                    if (cur == otherCur)
                    {
                        orig(self, ownCur, ownMax > 0 ? ownMax : max);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                LogWarn("[LocalCoop] could not check the flask counter write: {Message}", ex.Message);
            }
        }

        orig(self, cur, max);
    }

    /// <summary>
    /// Infection is a run-wide system, but the hero-side refresh repaints the global bar.
    /// With independent equipment each hero's infection is its own, so player two's refresh
    /// must not repaint player one's bar.
    /// </summary>
    private static void OnHeroRefreshInfectionHud(Hook_Hero.orig_refreshInfectionHud orig, Hero self)
    {
        if (!P2InventoryMayWriteSharedHud(self))
        {
            return;
        }

        orig(self);
    }

    /// <summary>Same gate as <see cref="OnHeroRefreshInfectionHud"/>.</summary>
    private static void OnHeroUpdateInfection(Hook_Hero.orig_updateInfection orig, Hero self)
    {
        if (!P2InventoryMayWriteSharedHud(self))
        {
            return;
        }

        orig(self);
    }


    /// <summary>
    /// Hides the global HUD's item slots (weapons, powers, potion, backpack) for the duration of a
    /// call, so the vanilla HUD refresh inside it finds nothing to repaint and leaves player one's
    /// slots alone. Restores them afterwards, in reverse order.
    /// <para>
    /// Used when player two picks an item up with independent equipment: the pickup itself must
    /// run (it inserts into player two's inventory), but its HUD side-effect would repaint player
    /// one's slots with player two's gear.
    /// </para>
    /// </summary>
    private static HideGlobalItemHudScope SuppressGlobalItemHud()
    {
        return new HideGlobalItemHudScope();
    }

    /// <summary>An <see cref="IDisposable"/> that empties the global HUD's item slots.</summary>
    private readonly struct HideGlobalItemHudScope : IDisposable
    {
        private readonly dc.ui.HUD? _hud;
        private readonly (dc.hl.types.ArrayObj? Array, int Count)[] _slots;

        public HideGlobalItemHudScope()
        {
            var hud = Game.Class.ME?.hud;
            _hud = hud;
            _slots = new (dc.hl.types.ArrayObj?, int)[4];

            if (hud == null)
            {
                return;
            }

            Hide(hud.skillWeapons, 0);
            Hide(hud.skillPowers, 1);

            // The potion and the backpack are single skills, not arrays: hide them by clearing the
            // icon so the refresh has nothing to draw.
            HideSingle(hud.skillPotion, 2);
            HideSingle(hud.skillBackpack, 3);
        }

        private void Hide(dc.hl.types.ArrayObj? array, int index)
        {
            if (array == null)
            {
                return;
            }
            _slots[index] = (array, array.length);
            array.length = 0;
        }

        private void HideSingle(dc.ui.hud.Skill? skill, int index)
        {
            if (skill == null)
            {
                return;
            }
            _slots[index] = (null, 1);
            skill.visible = false;
        }

        public void Dispose()
        {
            for (var i = _slots.Length - 1; i >= 0; i--)
            {
                var (array, count) = _slots[i];
                if (array != null)
                {
                    // The refresh may have shrunk it further; only restore if it is still empty.
                    if (array.length == 0 && count > 0)
                    {
                        array.length = count;
                    }
                    continue;
                }

                if (count > 0)
                {
                    RestoreSingle(i);
                }
            }
        }

        private void RestoreSingle(int index)
        {
            var hud = _hud;
            if (hud == null)
            {
                return;
            }

            var skill = index switch
            {
                2 => hud.skillPotion,
                3 => hud.skillBackpack,
                _ => null,
            };
            if (skill != null)
            {
                skill.visible = true;
            }
        }
    }

    /// <summary>
    /// A scroll level changes the tier counter on the HUD. Tiers are derived from the
    /// inventory, so they follow <see cref="SharedEquipment"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>orig</c> must always run.</b> It does two jobs: it stores the new tier on the hero
    /// (<c>self.brutalityTier</c>) and it repaints the shared HUD. Skipping the whole call to keep
    /// player two off player one's HUD also skipped the store, so player two's tier field stayed at
    /// its old value — and the panel, which reads that field, kept showing the old scroll count
    /// until the next <c>computeTiers()</c> (a save reload). Player one was unaffected because the
    /// gate never fires for it.
    /// </para>
    /// <para>
    /// So the call always runs and the HUD is put back on player one's own numbers right after,
    /// which is the same "run it, then restore" shape as <see cref="OnHudSetHealings"/>.
    /// </para>
    /// </remarks>
    private static void OnHeroSetBrutalityTier(Hook_Hero.orig_setBrutalityTier orig, Hero self, int v)
    {
        orig(self, v);
        RestoreSharedTiers(self);
    }

    /// <inheritdoc cref="OnHeroSetBrutalityTier"/>
    private static void OnHeroSetTacticTier(Hook_Hero.orig_setTacticTier orig, Hero self, int v)
    {
        orig(self, v);
        RestoreSharedTiers(self);
    }

    /// <inheritdoc cref="OnHeroSetBrutalityTier"/>
    private static void OnHeroSetSurvivalTier(Hook_Hero.orig_setSurvivalTier orig, Hero self, int v)
    {
        orig(self, v);
        RestoreSharedTiers(self);
    }

    /// <summary>
    /// Puts player one's three tier counters back on the shared HUD after player two's tier setter
    /// repainted it. A no-op for player one's own calls (and while equipment is shared, where the
    /// two heroes' tiers are the same number anyway).
    /// </summary>
    /// <remarks>
    /// Re-entering the setters for player one is deliberate: those calls pass the gate (the gate
    /// only ever fires for player two), so they run <c>orig</c> for player one and that is what
    /// writes player one's numbers back. <see cref="_restoringTiers"/> stops the restore from
    /// recursing.
    /// </remarks>
    private static void RestoreSharedTiers(Hero self)
    {
        if (_restoringTiers || !Enabled || SharedEquipment)
        {
            return;
        }

        if (!ReferenceEquals(self, _hero2) || _hero1 == null || _hero1.destroyed)
        {
            return;
        }

        _restoringTiers = true;
        try
        {
            _hero1.setBrutalityTier(_hero1.brutalityTier);
            _hero1.setTacticTier(_hero1.tacticTier);
            _hero1.setSurvivalTier(_hero1.survivalTier);
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not restore player one's tiers on the HUD: {Message}", ex.Message);
        }
        finally
        {
            _restoringTiers = false;
        }
    }

    /// <summary>Guards <see cref="RestoreSharedTiers"/> against re-entering itself.</summary>
    private static bool _restoringTiers;

    /// <summary>
    /// Friendly fire (team gate). Attacks collect their targets from the team system, and both
    /// heroes share <c>teamHero</c> — so hero one's attacks never even consider hero two as a
    /// candidate.
    /// <para>
    /// This used to add <c>teamHero</c> to its own <c>opposingTeams</c> so the pair became
    /// candidates for each other. That made <b>every</b> hero-team entity hostile to every other
    /// one, including a hero's <b>own</b> item entities — and neither <c>canBeHitBy</c> nor
    /// <c>isOpponent</c> can tell an item's owner apart (that lives in
    /// <c>AttackData.carrier ?? source</c>, which those hooks do not receive). The result was
    /// "using an item hurts the hero that used it". So the team is <b>never</b> made to oppose
    /// itself; friendly fire is granted instead by <see cref="OnHeroCanBeHitBy"/> and
    /// <see cref="OnEntityIsOpponent"/>, which answer for the two hero entities specifically.
    /// </para>
    /// <para>
    /// The call stays because it also <b>cleans up</b> a self-opposition an older build inserted:
    /// a run loaded straight from such a save would otherwise keep self-harming.
    /// </para>
    /// <para>
    /// <b>Frequency.</b> Teams are rebuilt per level, so one pass per level is what the cleanup
    /// needs; the slow safety net below only covers a team mutated mid-level. It used to run every
    /// frame, which meant an <c>ArrayObj.indexOf</c> (a Haxe call walking the array) per frame for a
    /// list that only ever changes on a level change.
    /// </para>
    /// </summary>
    private static void SyncFriendlyFireTeams(double dt)
    {
        if (!Enabled || _hero2 == null || _hero2.destroyed || _hero2._level == null)
        {
            _friendlyFireTimer = 0;
            _friendlyFireLevel = null;
            return;
        }

        _friendlyFireTimer += dt;
        var levelChanged = !ReferenceEquals(_hero2._level, _friendlyFireLevel);
        if (!levelChanged && _friendlyFireTimer < FriendlyFireSafetySeconds)
        {
            return;
        }

        _friendlyFireTimer = 0;
        _friendlyFireLevel = _hero2._level;

        try
        {
            var teamHero = _hero2._level.teamHero;
            if (teamHero?.opposingTeams is not dc.hl.types.ArrayObj opp)
            {
                return;
            }

            // Always remove: the team must never oppose itself (see the summary).
            var idx = opp.indexOf(teamHero, null);
            if (idx >= 0)
            {
                opp.remove(teamHero);
                LogVerbose("[LocalCoop] hero team self-opposition removed");
            }
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not sync friendly-fire teams: {Message}", ex.Message);
        }
    }

    /// <summary>Seconds between two friendly-fire cleanup passes on an unchanged level.</summary>
    private const double FriendlyFireSafetySeconds = 1.0;

    /// <summary>Time since the last friendly-fire pass, and the level it ran on.</summary>
    private static double _friendlyFireTimer;
    private static dc.pr.Level? _friendlyFireLevel;

    private static void OnHeroKill(Hook_Hero.orig_kill orig, Hero self)
    {
        // PvP: never let a hero through the game's own kill(). The arena is a training-mode level,
        // and Hero.kill() there branches to deathRespawn() (decompiled dc/en/Hero.cs:33035-33048),
        // which hunts for a ZDoor/TrainingDoor entity to respawn at — neither exists in the single
        // arena room, so the respawn path reads a null entity array. The match instead holds the
        // hero at the floor value, downs it and tallies the round, which is what the lethal-hit
        // gate already does.
        if (Enabled && IsPvp && (ReferenceEquals(self, _hero1) || ReferenceEquals(self, _hero2)))
        {
            if (!IsDown(self))
            {
                try
                {
                    self.life = DownedLife;
                }
                catch (Exception ex)
                {
                    LogWarn("[PvP] could not hold a killed hero at the floor value: {Message}", ex.Message);
                }

                MarkDown(self);
                if (Pvp == PvpState.Fighting)
                {
                    ScoreRoundLoss(self);
                }
            }

            return;
        }

        if (Enabled && PvpHealthIndependent && ReferenceEquals(self, _hero2))
        {
            // Independent health: player two's kill() is P2's own death — it must NEVER be
            // redirected to player one. The redirect below is the shared-health behaviour (one
            // pool, either death ends the run); with independent health it was killing player one
            // whenever a kill()-style hit (hazards, DoT paths that skip tryToPreventDeath) landed
            // on player two — the "P2 is hit, P1 dies" bug. Mirror OnHeroDie's independent branch:
            // down while a teammate is alive, end the run through the global hero otherwise.
            var teammate = _hero1;
            if (teammate != null && !teammate.destroyed && IsAlive(teammate) && !IsDown(self))
            {
                try
                {
                    self.life = DownedLife;
                }
                catch (Exception ex)
                {
                    LogWarn("[LocalCoop] could not hold a killed player two at the floor value: {Message}", ex.Message);
                }

                // Same consumption as the death gate: a kill()-style death on a cursed hero must
                // still spend the curse rather than leave it on the pair.
                ConsumeCurseOnPreventedDeath(self);

                MarkDown(self);
                return;
            }

            if (IsDown(self))
            {
                return;
            }

            // Everyone is down (or the teammate is gone): end the run through the global hero,
            // the same way OnHeroDie does.
            var endHero = teammate != null && !teammate.destroyed ? teammate : self;
            try
            {
                if (IsDown(endHero))
                {
                    endHero.life = 0;
                    endHero.isFreeze = false;
                }
            }
            catch (Exception ex)
            {
                LogWarn("[LocalCoop] could not unfreeze the last hero (kill path): {Message}", ex.Message);
            }
            orig(endHero);
            RemoveSecondHero("all heroes down");
            return;
        }

        if (Enabled && ReferenceEquals(self, _hero2) && _hero1 != null && !_hero1.destroyed)
        {
            orig(_hero1);
            return;
        }
        orig(self);
    }

    /// <summary>
    /// Whoever teleports (a same-level teleporter pad) drags the partner along, so the pair stays
    /// together — restored by request after 0.1.18 had removed it. Level changes do not go through
    /// this; they are handled by the initHero placement.
    /// </summary>
    private static void OnHeroTeleportation(Hook_Hero.orig_onTeleportation orig, Hero self)
    {
        orig(self);

        if (!Enabled)
        {
            return;
        }

        try
        {
            var isP1 = ReferenceEquals(self, _hero1);
            var other = isP1 ? _hero2 : (ReferenceEquals(self, _hero2) ? _hero1 : null);
            if (other == null || other.destroyed || self.destroyed)
            {
                return;
            }
            if (self._level == null || self.cx < 0 || self.cy < 0)
            {
                return;
            }
            // Cross-level moves are the level streaming's business; the repair loop and the
            // initHero placement already put the partner back next to player one there.
            if (other._level != self._level)
            {
                return;
            }

            PlaceNextTo(other, self);
            other.onScreenRecent = other.frameCountThresholdForOutOfGame;
            Log($"[LocalCoop] {(isP1 ? "P1" : "P2")} teleported, partner moved to case {other.cx},{other.cy}");
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not follow the teleportation: {Message}", ex.Message);
        }
    }

    private static void OnHeroAddCells(Hook_Hero.orig_addCells orig, Hero self, int v, HaxeProxy.Runtime.Ref<bool> noStats)
    {
        if (Enabled && ReferenceEquals(self, _hero2) && _hero1 != null && !_hero1.destroyed)
        {
            orig(_hero1, v, noStats);
            return;
        }
        orig(self, v, noStats);
    }

    private static void OnHeroCurse(
        Hook_Hero.orig_curse orig,
        Hero self,
        int count,
        dc.String reason,
        HaxeProxy.Runtime.Ref<bool> hidePopup,
        HaxeProxy.Runtime.Ref<bool> useAltSound)
    {
        // Shared health: the curse is one shared pair — the event runs normally on the hero the
        // game addressed (its own counter, its own "cursed" affect, its own head label) and the
        // resulting count is mirrored onto the partner. No second popup, no second jingle. The
        // mirror must not be gated on the equipment mode, because most curse sources (cursed
        // chests, money doors, gifts) hard-code `_level.game.hero` = player one: a curse player
        // two triggered then arrives on player one's Hero.curse() and, without the mirror, would
        // only ever appear on player one.
        //
        // Independent health: the curse is per-hero (by request) — no mirror at all. One
        // correction only: a global-source curse that the game hard-codes onto player one is
        // redirected to the hero that unambiguously triggered it — the last activator recorded by
        // the onActivate hook (the chest/door curse fires inside its activation), falling back to
        // a sole interaction focus (see CurseAttributionHero). A curse the game already addressed
        // correctly (a Curser hit, a CursingSkill) lands on the hero that was hit and stays there.
        if (Enabled && _hero1 != null && _hero2 != null
            && (ReferenceEquals(self, _hero1) || ReferenceEquals(self, _hero2)))
        {
            if (CurseShared)
            {
                var sharedPartner = ReferenceEquals(self, _hero1) ? _hero2 : _hero1;
                if (!sharedPartner.destroyed)
                {
                    orig(self, count, reason, hidePopup, useAltSound);
                    MirrorCurseCounter(sharedPartner, self);
                    return;
                }
            }
            else
            {
                var owner = CurseAttributionHero(self);
                if (owner != null && !ReferenceEquals(owner, self) && !owner.destroyed)
                {
                    orig(owner, count, reason, hidePopup, useAltSound);
                    return;
                }
            }
        }
        orig(self, count, reason, hidePopup, useAltSound);
    }

    private static void OnHeroEndCurse(Hook_Hero.orig_endCurse orig, Hero self)
    {
        // Shared health: a curse lifted by the game (reduceCurse draining the counter to zero,
        // a direct game call) ends on both carriers. Independent health: each hero's curse ends
        // on its own (by request) — no mirror.
        if (Enabled && CurseShared && _hero1 != null && _hero2 != null
            && (ReferenceEquals(self, _hero1) || ReferenceEquals(self, _hero2)))
        {
            var partner = ReferenceEquals(self, _hero1) ? _hero2 : _hero1;
            if (!partner.destroyed)
            {
                orig(self);
                MirrorCurseEnd(partner);
                return;
            }
        }
        orig(self);
    }

    private static void OnHeroReduceCurse(Hook_Hero.orig_reduceCurse orig, Hero self, int n)
    {
        // Shared health: let the game's own mob-death flow run — it walks EVERY hero in the level
        // and reduces each by one, which with mirrored counters is exactly the shared behaviour,
        // and both labels/counters update by themselves.
        //
        // Independent health: the same loop would reduce BOTH heroes per kill, which makes player
        // two's kills pay off player one's curse. Restrict the reduction to the hero that
        // actually killed the mob (its lastHitHero, falling back to the mob's target): the other
        // hero keeps its own curse and must lift it with its own kills.
        if (Enabled && !CurseShared && IsOurHero(self) && _dyingMob != null)
        {
            var killer = MobKillerHero(_dyingMob);
            if (killer != null && !ReferenceEquals(killer, self))
            {
                // A mob someone else killed must not lift this hero's curse.
                return;
            }
        }
        orig(self, n);
    }

    /// <summary>
    /// The mob currently running its <c>onDie</c> (the curse-reduction loop lives inside it), or
    /// null outside a mob death. Set by <see cref="OnMobDie"/> in <see cref="LocalCoop.MobAi.cs"/>.
    /// </summary>
    private static Mob? _dyingMob;

    /// <summary>
    /// The hero credited with killing the dying mob: its last hit hero, falling back to the hero
    /// the mob was fighting. Null when neither resolves.
    /// </summary>
    private static Hero? MobKillerHero(Mob mob)
    {
        try
        {
            var last = mob.lastHitHero;
            if (IsOurHero(last) && !last!.destroyed)
            {
                return last;
            }

            var fighter = MobHero(mob);
            if (fighter != null)
            {
                return fighter;
            }
        }
        catch
        {
            // No attribution is a fine answer.
        }
        return null;
    }

    /// <summary>
    /// The hero that last activated an interactable, with the time it happened. Curse sources
    /// fire <em>during</em> <c>onActivate</c> (the chest raises its curse before the use returns),
    /// so this record is the exact attribution for them. <see cref="OnEntityOnActivate"/> writes it.
    /// </summary>
    private static Hero? _lastActivator;
    private static long _lastActivatorAt;

    /// <summary>How long a recorded activation stays attributable (the curse fires within it).</summary>
    private const long ActivatorWindowMs = 2000;

    private static void OnEntityOnActivate(Hook_Entity.orig_onActivate orig, Entity self, Hero by, bool longPress)
    {
        try
        {
            if (IsOurHero(by) && !by.destroyed)
            {
                _lastActivator = by;
                _lastActivatorAt = System.Environment.TickCount64;
            }
        }
        catch
        {
            // Attribution is best-effort; never break the activation.
        }

        orig(self, by, longPress);
    }

    /// <summary>
    /// Who a curse event belongs to when the game hard-coded it onto the global hero and the
    /// health mode is independent. Primary answer: the hero that just activated an interactable
    /// (the chest/door curse fires inside its <c>onActivate</c>). Fallback: the hero that is
    /// unambiguously mid-interaction (exactly one hero holds a focus). Null means "keep
    /// <paramref name="self"/>" — the game's own target stands.
    /// </summary>
    private static Hero? CurseAttributionHero(Hero self)
    {
        try
        {
            var now = System.Environment.TickCount64;
            var activator = _lastActivator;
            if (activator != null && !activator.destroyed
                && now - _lastActivatorAt <= ActivatorWindowMs)
            {
                return activator;
            }

            var focused1 = _hero1 != null && !_hero1.destroyed && _hero1.focused != null;
            var focused2 = _hero2 != null && !_hero2.destroyed && _hero2.focused != null;
            if (focused1 != focused2)
            {
                return focused2 ? _hero2 : _hero1;
            }
        }
        catch
        {
            // No attribution is a fine answer.
        }
        return null;
    }

    /// <summary>
    /// Copies the partner's curse count onto <paramref name="target"/> after the target's own
    /// <c>curse()</c> has run: counter, high-water mark, the "cursed" affect (32 — the next hit
    /// kills) and the head label. Written as fields, never through <c>curse()</c>, so the partner
    /// gets no second modal popup and no second jingle — a curse is information the partner also
    /// lives under, not an event to replay at them.
    /// </summary>
    private static void MirrorCurseCounter(Hero target, Hero source)
    {
        try
        {
            var count = source.curseCounter;
            if (target.curseCounter == count)
            {
                return;
            }

            target.curseCounter = count;
            if (count > target.curCurseMaxReached)
            {
                target.curCurseMaxReached = count;
            }

            RefreshCurseLabel(target, count);

            // The "cursed" affect (32) is what turns the next hit lethal. The game re-asserts it
            // on its own damage path, but applying it here makes the state immediate on the
            // mirrored hero.
            if (count > 0 && !target.hasAffect(32))
            {
                target.setAffectS(32, 99999.0, HaxeProxy.Runtime.Ref<double>.Null, null);
            }
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not mirror the curse to the partner: {Message}", ex.Message);
        }
    }

    /// <summary>Clears the curse on the mirrored hero: counter, label and the affect.</summary>
    private static void MirrorCurseEnd(Hero target)
    {
        try
        {
            target.curseCounter = 0;
            target.curCurseMaxReached = 0;
            RefreshCurseLabel(target, 0);
            if (target.hasAffect(32))
            {
                target.removeAllAffects(32);
            }
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not clear the curse on the partner: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Rebuilds the curse label above a hero's head, exactly the way <c>Hero.curse()</c> and
    /// <c>reduceCurse()</c> do it: create the LightTip on first use, rewrite the count text while
    /// cursed, destroy it at zero.
    /// </summary>
    private static void RefreshCurseLabel(Hero hero, int count)
    {
        try
        {
            if (count > 0)
            {
                var label = hero.curseLabel;
                if (label == null)
                {
                    label = new dc.ui.LightTip(hero, new dc.Direction.Up());
                    hero.curseLabel = label;
                }

                label.clear();
                var text = $"{count}{{iconCursed@img}}".AsHaxeString();
                label.addText(text, null);
            }
            else if (hero.curseLabel != null)
            {
                var label = hero.curseLabel;
                label.destroyed = true;
                hero.curseLabel = null;
            }
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not refresh a curse label: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// The curse takes its toll on the hero that carries it: when a cursed hero's lethal hit is
    /// turned into a down (the mod's <see cref="OnHeroTryToPreventDeath"/> answers before the
    /// vanilla gate, so the vanilla "cursed: the next hit kills" refusal never runs), the curse is
    /// consumed here — counter zeroed, affect 32 removed, head label destroyed — <b>on this hero
    /// only</b>. The partner's own curse is untouched (by request): with independent health each
    /// hero pays for its own curse, so a prevented death on player two must not clear the curse
    /// player one is still carrying. Without this consumption the cursed hero would go down for
    /// free with its curse still armed.
    /// </summary>
    private static void ConsumeCurseOnPreventedDeath(Hero self)
    {
        try
        {
            if (self.curseCounter <= 0 && !self.hasAffect(32))
            {
                return;
            }

            Log($"[LocalCoop] {HeroName(self)}'s curse took its toll: consumed on the prevented death");
            MirrorCurseEnd(self);
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not consume a curse on a prevented death: {Message}", ex.Message);
        }
    }

    private static void OnHeroAddInfection(Hook_Hero.orig_addInfection orig, Hero self, double n, bool? notify, bool? forced)
    {
        if (Enabled && !SharedEquipment && ReferenceEquals(self, _hero2) && _hero1 != null && !_hero1.destroyed)
        {
            orig(_hero1, n, notify, forced);
            return;
        }
        orig(self, n, notify, forced);
    }

    private static void OnHeroRemoveInfection(Hook_Hero.orig_removeInfection orig, Hero self, double n, bool? notify, bool? fromMobKill)
    {
        if (Enabled && !SharedEquipment && ReferenceEquals(self, _hero2) && _hero1 != null && !_hero1.destroyed)
        {
            orig(_hero1, n, notify, fromMobKill);
            return;
        }
        orig(self, n, notify, fromMobKill);
    }

    private static void OnHeroSetInfection(Hook_Hero.orig_setInfection orig, Hero self, double n, bool? notify)
    {
        if (Enabled && !SharedEquipment && ReferenceEquals(self, _hero2) && _hero1 != null && !_hero1.destroyed)
        {
            orig(_hero1, n, notify);
            return;
        }
        orig(self, n, notify);
    }

    private static void OnEquipedItemsChange(
        Hook_Hero.orig_onEquipedItemsChange orig,
        Hero self,
        HaxeProxy.Runtime.Ref<bool> updateHUD,
        HaxeProxy.Runtime.Ref<bool> duringHeroInit,
        HaxeProxy.Runtime.Ref<bool> duringItemTransform)
    {
        // Mirroring is what keeps the partner's wielded weapon entities and skill slots in step
        // with the shared inventory. With independent equipment the two backpacks are separate, so
        // a change on one hero must not touch the other — the game's own hook has already rebuilt
        // the hero that actually changed gear.
        if (!Enabled || _mirroringEquip || !SharedEquipment)
        {
            orig(self, updateHUD, duringHeroInit, duringItemTransform);
            // Hero.onEquipedItemsChange ends with activeSkillsManager.onEquippedItemsUpdated(),
            // which is what binds the *global* HUD's skill slots (see RestoreSharedSkillHud). So a
            // player-two equipment change hands player one's cooldown display to player two even
            // on this early-return path — with independent equipment that is the only path there
            // is. Put the HUD back on player one.
            RestoreSharedSkillHud();
            return;
        }

        Hero? other = ReferenceEquals(self, _hero1) ? _hero2
            : ReferenceEquals(self, _hero2) ? _hero1
            : null;

        if (other == null || other.destroyed)
        {
            orig(self, updateHUD, duringHeroInit, duringItemTransform);
            RestoreSharedSkillHud();
            return;
        }

        _mirroringEquip = true;
        try
        {
            orig(self, updateHUD, duringHeroInit, duringItemTransform);
        }
        finally
        {
            _mirroringEquip = false;
        }

        // Hero two only needs its per-slot skill objects refreshed from the shared inventory.
        // It must NOT go through Hero.onEquipedItemsChange / activeSkillsManager
        // .onEquippedItemsUpdated: those write the **global** HUD skill slots
        // (hudGetSkillPower -> HUD.Class.ME.skillPowers), so hero two would take over the
        // cooldown and ammo display that belongs to player one — which looks exactly like a
        // cooldown shared between the two players.
        RefreshSecondHeroSkills(other);

        // The two heroes each own a separate HeroWeaponsManager with its own mainWeapons and
        // backpackWeapons entity lists. The game only (re)builds those entities for the hero that
        // actually changed equipment (self); the partner's *wielded* weapon entities go stale the
        // instant the other hero picks up or swaps a weapon — even though the shared Inventory
        // already carries the item. Run the same reconcile the game runs for self, on the partner,
        // so both heroes end up wielding the same weapon entities built from the shared inventory.
        // duringHeroInit is passed straight through: onEquippedItemsUpdated null-checks it exactly
        // like Hero.onEquipedItemsChange does, so a null ref from the Haxe side is harmless.
        // The _mirroringEquip guard is re-armed in case onEquippedItemsUpdated re-enters the hook.
        try
        {
            _mirroringEquip = true;
            other.weaponsManager?.onEquippedItemsUpdated(duringHeroInit);
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not sync the partner's weapon entities: {Message}", ex.Message);
        }
        finally
        {
            _mirroringEquip = false;
        }

        // NOTE: the global HUD's skill slots are re-bound to player one by
        // RefreshSecondHeroSkills itself (which is what runs last here), so nothing else is
        // needed — the restore has to cover every path, not just this one.

        if (Verbose)
        {
            Log($"[LocalCoop] equip self={(ReferenceEquals(self, _hero1) ? "P1" : "P2")} " +
                $"P1 mainW={_hero1?.weaponsManager?.mainWeapons?.length ?? -1} " +
                $"packW={_hero1?.weaponsManager?.backpackWeapons?.length ?? -1} | " +
                $"P2 mainW={_hero2?.weaponsManager?.mainWeapons?.length ?? -1} " +
                $"packW={_hero2?.weaponsManager?.backpackWeapons?.length ?? -1}");
        }
    }

    // RestoreSharedSkillHud lives in Coop/LocalCoop.SharedState.cs, next to
    // RefreshSecondHeroSkills that calls it.
}
