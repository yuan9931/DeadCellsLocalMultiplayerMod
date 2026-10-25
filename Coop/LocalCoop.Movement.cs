using dc;
using dc.en;
using dc.h2d;
using dc.hl.types;
using dc.hxd;
using dc.libs.heaps;
using dc.pr;
using dc.tool;

namespace DeadCellsLocalMultiplayerMod;

/// <summary>
/// Part of <see cref="LocalCoop"/>: detects and repairs a hero that is airborne but never descends
/// (floats in place), e.g. player two after picking up a power scroll.
///
/// Root cause (confirmed by log): picking up the scroll puts the hero into the <c>carryItem</c>
/// animation, whose state sets <c>Entity.hasGravity = false</c> so the hero does not fall while it
/// holds the item. For the second hero that state is not always completed — the carry anim ends
/// (idle / jump) but <c>hasGravity</c> is never restored, so the hero can still impulse-jump upward
/// yet gravity never pulls it back down: it floats until something (a wall / chain climb) overwrites
/// the movement state. This watchdog restores <c>hasGravity</c> as soon as the carry anim is over
/// while gravity is still off, and also acts as a safety net for any other "jumped but never fell"
/// float. The repair runs every frame regardless of logging; the log lines only print when
/// <see cref="LocalCoop.Logging"/> is on.
/// </summary>
internal static partial class LocalCoop
{
    // Per-hero watchdog state. Index 0 = P1, 1 = P2.
    private static readonly double[] _airStuckTimer = new double[2];
    private static readonly double[] _airFlagLogTimer = new double[2];
    private static readonly bool[] _airFell = new bool[2];
    private static readonly bool[] _airWatching = new bool[2];
    private static readonly bool[] _airWasCarrying = new bool[2];

    /// <summary>
    /// The current animation name, sampled rather than read every frame. Reading it means a
    /// <c>spr.groupName</c> interop call plus a <c>ToString()</c> allocation per hero per frame, and
    /// the only things it feeds are the carry-state repair and the diagnostic lines — neither of
    /// which can tell the difference between a 16 ms and a 100 ms old animation name.
    /// </summary>
    private static readonly string[] _airAnimName = { "(none)", "(none)" };

    /// <summary>Time since each hero's animation name was last sampled.</summary>
    private static readonly double[] _airAnimSample = { AirAnimSampleSeconds, AirAnimSampleSeconds };

    /// <summary>How often the animation name is sampled.</summary>
    private const double AirAnimSampleSeconds = 0.1;

    /// <summary>
    /// Two things:
    /// <list type="number">
    /// <item><b>Repair</b> the carry-state gravity bug — once the carry anim is no longer playing
    /// but <c>hasGravity</c> is still off, set it back on. Guarded by "the hero was carrying", so
    /// unrelated no-gravity states (water, a hook, flight skills) are never touched.</item>
    /// <item><b>Safety net</b> — if a hero jumped (<c>dy &lt; -0.25</c>) but never gained a downward
    /// velocity (<c>dy</c> never &gt; 0.3) for ~0.8s, gravity is stuck off: restore it.</item>
    /// </list>
    /// <paramref name="index"/> is 0 for P1, 1 for P2.
    /// </summary>
    private static void WatchStuckAir(Hero? hero, double dt, string who, int index)
    {
        // A downed hero is intentionally frozen (isFreeze) - that is not a stuck-in-air bug.
        if (hero == null || hero.destroyed || IsDown(hero))
        {
            return;
        }

        try
        {
            var isFreeze = hero.isFreeze;
            var hasGravity = hero.hasGravity;
            var gravity = hero.gravity;
            var dx = hero.dx;
            var dy = hero.dy;
            var cx = hero.cx;
            var cy = hero.cy;
            var xr = hero.xr;
            var yr = hero.yr;
            var ctrl = index == 0 ? _controller1 : _controller2;

            _airAnimSample[index] += dt;
            if (_airAnimSample[index] >= AirAnimSampleSeconds)
            {
                _airAnimSample[index] = 0;
                _airAnimName[index] = ReadAnimName(hero);
            }
            var anim = _airAnimName[index];

            // Repair 1: the carry state disabled gravity; once the carry anim is over but gravity is
            // still off, restore it. "was carrying" gates this to the carry bug only.
            var carrying = anim.IndexOf("carry", System.StringComparison.OrdinalIgnoreCase) >= 0;
            if (carrying)
            {
                _airWasCarrying[index] = true;
            }
            else
            {
                if (_airWasCarrying[index] && !hasGravity)
                {
                    hero.hasGravity = true;
                    LogVerbose($"[LocalCoop] {who} carry ended but gravity was still off " +
                        $"(anim={anim} case={cx},{cy} yr={yr:F2}), restored hasGravity");
                }
                _airWasCarrying[index] = false;
            }

            // Repair 2 / diagnostic: a jump that never descends. Start watching at the upward impulse.
            if (dy < -0.25)
            {
                _airWatching[index] = true;
                _airFell[index] = false;
                _airStuckTimer[index] = 0;
            }
            else if (_airWatching[index])
            {
                if (dy > 0.3)
                {
                    // Gained downward velocity: falling normally, this jump is resolved.
                    _airFell[index] = true;
                    _airWatching[index] = false;
                    _airStuckTimer[index] = 0;
                }
                else if (!_airFell[index])
                {
                    // Vertical velocity pinned near zero while still airborne.
                    _airStuckTimer[index] = System.Math.Abs(dy) < 0.1 ? _airStuckTimer[index] + dt : 0;

                    if (_airStuckTimer[index] >= 0.8)
                    {
                        if (!hasGravity)
                        {
                            hero.hasGravity = true;
                            LogVerbose($"[LocalCoop] {who} jumped but never fell with gravity off " +
                                $"(anim={anim} case={cx},{cy} yr={yr:F2}), restored hasGravity");
                        }
                        else
                        {
                            LogWarn($"[LocalCoop] {who} STUCK IN AIR (jumped but never fell, gravity on) " +
                                $"isFreeze={isFreeze} gravity={gravity:F3} dx={dx:F2} dy={dy:F2} " +
                                $"case={cx},{cy} xr={xr:F2} yr={yr:F2} anim={anim}");
                        }
                        _airStuckTimer[index] = 0;
                        _airWatching[index] = false;
                    }
                }
            }

            // Diagnostic only: abnormal physics flags (logging-only, throttled).
            if (isFreeze || !hasGravity || gravity == 0)
            {
                _airFlagLogTimer[index] += dt;
                if (_airFlagLogTimer[index] >= 0.5)
                {
                    _airFlagLogTimer[index] = 0;
                    LogVerbose($"[LocalCoop] {who} ABNORMAL PHYSICS isFreeze={isFreeze} hasGravity={hasGravity} " +
                        $"gravity={gravity:F3} dx={dx:F2} dy={dy:F2} case={cx},{cy} xr={xr:F2} yr={yr:F2} " +
                        $"anim={anim} ctrlMode={ctrl?.mode} pad={(ctrl?.gc?.device?.index ?? -1)}");
                }
            }
            else
            {
                _airFlagLogTimer[index] = 0;
            }
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] stuck-air watchdog failed for {Who}: {Message}", who, ex.Message);
        }
    }

    /// <summary>The hero's current animation group name, or "(none)" when it has no sprite.</summary>
    private static string ReadAnimName(Hero hero)
    {
        try
        {
            var spr = hero.spr;
            if (spr == null)
            {
                return "(none)";
            }

            var name = spr.groupName;
            return name == null ? "(none)" : name.ToString();
        }
        catch
        {
            return "(none)";
        }
    }
}
