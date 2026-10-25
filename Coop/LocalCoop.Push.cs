using System;
using dc;
using dc.en;

namespace DeadCellsLocalMultiplayerMod;

/// <summary>
/// Part of <see cref="LocalCoop"/>: the walk-push between the two heroes — walking into each other
/// must shove the other hero, in <b>both</b> directions, instead of one hero being movable and the
/// other immovable.
/// </summary>
/// <remarks>
/// The engine's own repelling cannot deliver this. Measured in game with it live: player one walking
/// into player two carries player two along (sprite x 1437 → 1413 → 1296 → 1177, gap held at ~20 px),
/// while player two walking into player one is refused and the engine pushes player two *back* — with
/// every per-hero weight input identical and <c>getAdjustedWeightFactor</c> reading 1.00 for both at
/// the moment of contact. The engine never calls <c>Hero.bump</c> for any of it (<c>Hook_Hero.bump</c>
/// logged nothing while the heroes were shoving, while <c>Hook_Hero.onTouch</c> fired on the mover
/// only), so there is no "push" call to intercept and no callback where pointing <c>game.hero</c> at
/// the partner turned the shove around. So the hero pair is taken out of the engine's pass entirely
/// (<see cref="OnCanHaveRepellingWith"/>), and the shove is applied here instead: the hero walking into
/// the other hands it its own speed as a bump (<see cref="ApplyWalkPush"/>). That is symmetric by
/// construction — either hero can shove the other — while the walker keeps walking and the partner is
/// carried along.
/// <para>
/// A roll is not a walk: while either hero rolls the shove is off entirely (<see cref="IsRolling"/>),
/// so a roll passes over the partner instead of launching them.
/// </para>
/// </remarks>
internal static partial class LocalCoop
{
    /// <summary>Registers the walk-push: the pair rule, the roll pass-through and the (diagnostic)
    /// contact log. Called from <c>Install</c>.</summary>
    internal static void InstallWalkPush()
    {
        // The pair rule that takes P1/P2 out of the engine's own repelling. `Hero` does not override
        // `canHaveRepellingWith` (only mobs do), so this Entity-level hook does fire for heroes.
        Hook_Entity.canHaveRepellingWith += OnCanHaveRepellingWith;
        // Diagnostic only: hero-vs-hero contact runs through the Hero-level `onTouch` (`Hero`
        // overrides `onTouch`, so the Entity-level hook is silent for heroes) — that is how the
        // engine's one-way shove was identified.
        Hook_Hero.onTouch += OnHeroOnTouch;

        // A roll: the dodge roll is a main skill (`tool.mainSkills.Dodge`, with `DodgeRoll` for the
        // normal hero and `RichterDodge` for Richter), so its start / end / cancel are exactly the
        // moments a hero is rolling. All three classes are hooked because a subclass that overrides
        // `onStart` would otherwise bypass a base-class hook; marking is idempotent, so it does not
        // matter when more than one of them fires for the same roll.
        dc.tool.mainSkills.Hook_DodgeRoll.onStart += OnRollStart;
        dc.tool.mainSkills.Hook_DodgeRoll.onEnd += OnRollEnd;
        dc.tool.mainSkills.Hook_DodgeRoll.onCancel += OnRollCancel;
        dc.tool.mainSkills.Hook_Dodge.onStart += OnRollStart;
        dc.tool.mainSkills.Hook_Dodge.onCancel += OnRollCancel;
        dc.tool.mainSkills.Hook_RichterDodge.onStart += OnRollStart;
        dc.tool.mainSkills.Hook_RichterDodge.onEnd += OnRollEnd;
        dc.tool.mainSkills.Hook_RichterDodge.onCancel += OnRollCancel;
    }

    /// <summary>
    /// Hook body for a roll event: runs the vanilla call plus this mod's bookkeeping, and logs a
    /// failure instead of throwing it (an exception thrown inside a hook is fatal to the game).
    /// </summary>
    private static void RollHook(Action body)
    {
        try
        {
            body();
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] roll hook failed: {Message}", ex.Message);
        }
    }

    /// <summary>Roll started (the normal dodge roll).</summary>
    private static void OnRollStart(
        dc.tool.mainSkills.Hook_DodgeRoll.orig_onStart orig,
        dc.tool.mainSkills.DodgeRoll self)
        => RollHook(() =>
        {
            orig(self);
            MarkRoll(self?.hero, "DodgeRoll");
        });

    /// <summary>Roll started (the base dodge skill, in case a character's roll is not a DodgeRoll).</summary>
    private static void OnRollStart(
        dc.tool.mainSkills.Hook_Dodge.orig_onStart orig,
        dc.tool.mainSkills.Dodge self)
        => RollHook(() =>
        {
            orig(self);
            MarkRoll(self?.hero, "Dodge");
        });

    /// <summary>Roll started for Richter's roll.</summary>
    private static void OnRollStart(
        dc.tool.mainSkills.Hook_RichterDodge.orig_onStart orig,
        dc.tool.mainSkills.RichterDodge self)
        => RollHook(() =>
        {
            orig(self);
            MarkRoll(self?.hero, "RichterDodge");
        });

    /// <summary>Roll ended on its own.</summary>
    private static void OnRollEnd(
        dc.tool.mainSkills.Hook_DodgeRoll.orig_onEnd orig,
        dc.tool.mainSkills.DodgeRoll self)
        => RollHook(() =>
        {
            orig(self);
            ClearRoll(self?.hero);
        });

    /// <summary>Roll ended on its own (Richter's roll).</summary>
    private static void OnRollEnd(
        dc.tool.mainSkills.Hook_RichterDodge.orig_onEnd orig,
        dc.tool.mainSkills.RichterDodge self)
        => RollHook(() =>
        {
            orig(self);
            ClearRoll(self?.hero);
        });

    /// <summary>Roll cancelled (a hit, another skill, a level change).</summary>
    private static void OnRollCancel(
        dc.tool.mainSkills.Hook_DodgeRoll.orig_onCancel orig,
        dc.tool.mainSkills.DodgeRoll self)
        => RollHook(() =>
        {
            orig(self);
            ClearRoll(self?.hero);
        });

    /// <summary>Roll cancelled (the base dodge skill — it overrides `onCancel`, not `onEnd`).</summary>
    private static void OnRollCancel(
        dc.tool.mainSkills.Hook_Dodge.orig_onCancel orig,
        dc.tool.mainSkills.Dodge self)
        => RollHook(() =>
        {
            orig(self);
            ClearRoll(self?.hero);
        });

    /// <summary>Roll cancelled (Richter's roll).</summary>
    private static void OnRollCancel(
        dc.tool.mainSkills.Hook_RichterDodge.orig_onCancel orig,
        dc.tool.mainSkills.RichterDodge self)
        => RollHook(() =>
        {
            orig(self);
            ClearRoll(self?.hero);
        });

    /// <summary>
    /// <c>false</c> for the pair of the mod's two heroes: the engine's repelling ("bump") pass has
    /// nothing to resolve for that pair, so the engine no longer shoves player two around. Every other
    /// pair keeps the vanilla answer (hero-vs-mob, mob-vs-mob, a hero shoving a mob, mobs shoving a
    /// hero), so enemy pushing and every other collision in the game is untouched. The hero-vs-hero
    /// shove is done by <see cref="ApplyWalkPush"/> instead.
    /// </summary>
    /// <remarks>
    /// The count is diagnostic only (nothing in the mod calls this method, so every hit is the engine
    /// asking): if the engine never asks about the hero pair, the pair rule is not what keeps the
    /// engine's shove away.
    /// </remarks>
    private static bool OnCanHaveRepellingWith(
        Hook_Entity.orig_canHaveRepellingWith orig,
        Entity self,
        Entity entity)
    {
        if (!Enabled || !IsOurHero(self) || !IsOurHero(entity))
        {
            return orig(self, entity);
        }

        if (Verbose)
        {
            _pairAskedCount++;
            _pairAskedTick = System.Environment.TickCount64;
        }

        return false;
    }

    /// <summary>How often the engine asked whether the two heroes may repel each other (diagnostic).</summary>
    private static int _pairAskedCount;

    /// <summary>Tick of the last such question, or 0 when there never was one (diagnostic).</summary>
    private static long _pairAskedTick;

    /// <summary>Contact callback for a hero.
    /// <c>Hero</c> overrides <c>onTouch</c>, so the Entity-level hook is silent for heroes; logged
    /// only, to tell whether hero-vs-hero contact runs through the engine's repelling path.</summary>
    private static void OnHeroOnTouch(Hook_Hero.orig_onTouch orig, Hero self, Entity e)
    {
        orig(self, e);

        if (Enabled && Verbose && IsOurHero(e) && PushContactDue())
        {
            try
            {
                Log($"[LocalCoop][push] onTouch self={Tag(self)} e={Tag(e)}");
            }
            catch
            {
                // Diagnostics only.
            }
        }
    }

    /// <summary>Short "P1"/"P2" tag for the push diagnostic lines.</summary>
    private static string Tag(Entity e)
    {
        if (ReferenceEquals(e, _hero1))
        {
            return "P1";
        }

        return ReferenceEquals(e, _hero2) ? "P2" : "other";
    }

    /// <summary>True when <paramref name="e"/> is one of the mod's two heroes.</summary>
    private static bool IsOurHero(Entity? e)
        => e != null && (ReferenceEquals(e, _hero1) || ReferenceEquals(e, _hero2));

    /// <summary>Smallest own horizontal speed that counts as walking. Velocities are in the game's own
    /// small units: a walking hero reads about ±0.3, the online mod shoves a body with ±0.18.</summary>
    private const double PushMinSpeed = 0.02;

    /// <summary>Fastest own speed still read as walking. This is a <b>reject</b>, not a clamp: a hero
    /// moving faster than this — a roll, a knockback, a launched hero — hands nothing over, so a roll
    /// past the partner cannot become a full-strength shove.</summary>
    private const double PushMaxSpeed = 1.0;

    /// <summary>How long a roll counts as running since its last event (ms). A roll lasts well under a
    /// second, so this is only a deadline: a roll whose end was never reported (a hero dying mid-roll,
    /// a level change) can never leave the walk-push switched off for long.</summary>
    private const long RollPassMs = 1200;

    /// <summary>When player one / player two last started a roll (TickCount64; 0 = not rolling).</summary>
    private static long _rollTick1;
    private static long _rollTick2;

    /// <summary>Remembers that a hero started rolling, and logs it (the roll's speed would otherwise
    /// look like a walk in the <c>[push] shove</c> line).</summary>
    private static void MarkRoll(Hero? hero, string skill)
    {
        var now = System.Environment.TickCount64;
        if (ReferenceEquals(hero, _hero1))
        {
            _rollTick1 = now;
        }
        else if (ReferenceEquals(hero, _hero2))
        {
            _rollTick2 = now;
        }
        else
        {
            return;
        }

        if (Verbose)
        {
            Log($"[LocalCoop][push] roll start {Tag(hero!)} ({skill}) — shove off while it lasts");
        }
    }

    /// <summary>Roll over (ended or cancelled).</summary>
    private static void ClearRoll(Hero? hero)
    {
        if (ReferenceEquals(hero, _hero1))
        {
            _rollTick1 = 0;
        }
        else if (ReferenceEquals(hero, _hero2))
        {
            _rollTick2 = 0;
        }
    }

    /// <summary>True while a hero is rolling (its dodge roll is running, or its end was never
    /// reported and the roll's deadline has not passed). A rolling hero must be able to pass over the
    /// partner instead of shoving them.</summary>
    private static bool IsRolling(Hero? hero)
    {
        var tick = ReferenceEquals(hero, _hero1)
            ? _rollTick1
            : ReferenceEquals(hero, _hero2)
                ? _rollTick2
                : 0;

        return tick != 0 && System.Environment.TickCount64 - tick < RollPassMs;
    }

    /// <summary>Frames to leave the heroes alone, e.g. after a spawn, a level change or a teleport.</summary>
    private static int _pushCooldownFrames;

    /// <summary>The bump this mod handed player one / player two, so it can be dropped again without
    /// touching a bump the engine set (a mob shove, a knockback).</summary>
    private static double _pushSet1;
    private static double _pushSet2;

    /// <summary>
    /// Ignores the heroes for a few frames, so the placement of a spawn / level change / teleport is
    /// never mistaken for two heroes walking into each other (the level puts hero two 0.12 cells beside
    /// hero one, i.e. closer than the two radii) and any bump this mod set is dropped.
    /// </summary>
    internal static void ResetWalkPush(int cooldownFrames)
    {
        _pushCooldownFrames = cooldownFrames;
        _pushSet1 = 0;
        _pushSet2 = 0;
    }

    /// <summary>
    /// Symmetric walk-push, run once per frame. Whoever is walking <b>into</b> the other pushes them:
    /// the walker's own horizontal speed is handed to the partner as a bump, so the partner is carried
    /// along at the walker's speed while the walker keeps walking normally — and because it is measured
    /// in both directions, either hero can shove the other (the engine's own pass only ever shoved
    /// player two). Two heroes walking into each other push each other, so they stop instead of passing
    /// through.
    /// </summary>
    /// <remarks>
    /// Nothing is written to a hero's position. An earlier version displaced both heroes with
    /// <c>Hero.setPosPixel</c> and made both heroes play the airborne (jump) animation — the hero's own
    /// state machine reacts badly to a position written from outside it. A bump is a velocity, so the
    /// engine integrates it like any other movement and the heroes' animation state stays intact: the
    /// hero being pushed keeps its standing animation (its own input is untouched) and the walker keeps
    /// its run animation. The push is horizontal only, like the engine's own shove, so nobody is shoved
    /// into the floor or the ceiling.
    /// </remarks>
    internal static void ApplyWalkPush(Hero hero1, Hero hero2)
    {
        try
        {
            if (!Enabled
                || IsDown(hero1)
                || IsDown(hero2)
                || !ReferenceEquals(hero1._level, hero2._level))
            {
                DropWalkPushBumps(hero1, hero2);
                return;
            }

            if (_pushCooldownFrames > 0)
            {
                _pushCooldownFrames--;
                DropWalkPushBumps(hero1, hero2);
                return;
            }

            // A roll is not a walk. While either hero rolls the shove is off for the pair, so rolling
            // into the partner passes *over* them; the engine's own repelling is already out for the
            // pair, so nothing else holds the roll back either. The roller also never hands their
            // (much bigger) roll speed over as a shove.
            if (IsRolling(hero1) || IsRolling(hero2))
            {
                DropWalkPushBumps(hero1, hero2);
                if (Verbose && PushShoveDue())
                {
                    Log($"[LocalCoop][push] roll pass-through P1={IsRolling(hero1)} " +
                        $"P2={IsRolling(hero2)}");
                }

                return;
            }

            var s1 = hero1.spr;
            var s2 = hero2.spr;
            if (s1 == null || s2 == null || s1 == s2)
            {
                DropWalkPushBumps(hero1, hero2);
                return;
            }

            // Contact test on the sprite distance (the sprites track the hero positions).
            var sepX = s2.x - s1.x;
            var sepY = s2.y - s1.y;
            var dist = System.Math.Sqrt((sepX * sepX) + (sepY * sepY));
            var reach = hero1.radius + hero2.radius;
            if (dist <= 0.01 || dist >= reach || System.Math.Abs(sepX) < 0.01)
            {
                DropWalkPushBumps(hero1, hero2);
                return;
            }

            // `dx` is the hero's own velocity; a bump lives separately in `bdx` (the engine's own
            // repel feeds it the same way), so the two never leak into each other.
            var dir = sepX > 0 ? 1.0 : -1.0; // from player one towards player two
            var v1 = hero1.dx;
            var v2 = hero2.dx;

            // Player one pushes player two when its own velocity points at the partner, and the other
            // way round. Neither walking into the other means there is nothing to push, so the bump
            // this mod set is dropped again and the hero stops instead of sliding on. Both walking into
            // each other means each hands the other its speed, so on top of its own velocity each is
            // held exactly still — they stop each other. A speed above `PushMaxSpeed` is not walking
            // (a roll, a knockback, a launched hero) and hands nothing over.
            var p1Pushes = v1 * dir > PushMinSpeed && System.Math.Abs(v1) <= PushMaxSpeed;
            var p2Pushes = v2 * dir < -PushMinSpeed && System.Math.Abs(v2) <= PushMaxSpeed;

            if (p1Pushes)
            {
                SetBump(hero2, v1, ref _pushSet2);
            }
            else
            {
                DropOwnBump(hero2, ref _pushSet2);
            }

            if (p2Pushes)
            {
                SetBump(hero1, v2, ref _pushSet1);
            }
            else
            {
                DropOwnBump(hero1, ref _pushSet1);
            }

            if ((p1Pushes || p2Pushes) && Verbose && PushShoveDue())
            {
                Log($"[LocalCoop][push] shove d={dist:F1} reach={reach:F1} "
                    + $"P1 v={v1:F2} bdx={hero1.bdx:F2} | P2 v={v2:F2} bdx={hero2.bdx:F2}"
                    + (p1Pushes ? " P1>P2" : string.Empty)
                    + (p2Pushes ? " P2>P1" : string.Empty));
            }
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] walk-push failed: {Message}", ex.Message);
        }
    }

    /// <summary>Drops the bumps this mod set on both heroes (contact ended, a hero is down, the level
    /// changed), so a hero never keeps sliding on a push that is over.</summary>
    private static void DropWalkPushBumps(Hero hero1, Hero hero2)
    {
        DropOwnBump(hero1, ref _pushSet1);
        DropOwnBump(hero2, ref _pushSet2);
    }

    /// <summary>
    /// Hands <paramref name="v"/> (the pusher's own horizontal walking speed) to a hero as a bump — the
    /// game's own push channel (<c>bdx</c>, the one the engine's repelling feeds and the online mod
    /// shoves a body with), integrated as normal movement. Only speeds within the walking range get
    /// here (see <see cref="PushMaxSpeed"/>).
    /// </summary>
    private static void SetBump(Hero hero, double v, ref double mine)
    {
        hero.bdx = v;
        mine = v;
    }

    /// <summary>
    /// Drops the bump this mod set on a hero — and only that one, so a bump the engine set (a mob
    /// shove, a knockback) is left alone. Without it the pushed hero would keep sliding after the
    /// pusher stopped.
    /// </summary>
    private static void DropOwnBump(Hero hero, ref double mine)
    {
        if (mine == 0)
        {
            return;
        }

        if (System.Math.Abs(hero.bdx - mine) < 1e-6 && !hero.destroyed)
        {
            hero.bdx = 0;
        }

        mine = 0;
    }

    /// <summary>Throttles the <c>[push] shove</c> log to one line per quarter second (its own throttle,
    /// so a shove line cannot starve the contact line or the other way round).</summary>
    private static long _pushShoveTick;

    private static bool PushShoveDue()
    {
        var now = System.Environment.TickCount64;
        if (now - _pushShoveTick < 250)
        {
            return false;
        }

        _pushShoveTick = now;
        return true;
    }
}
