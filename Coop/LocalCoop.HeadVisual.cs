using dc.en;

namespace DeadCellsLocalMultiplayerMod;

/// <summary>
/// Part of <see cref="LocalCoop"/>: visual head binding and head-out animation state.
/// </summary>
internal static partial class LocalCoop
{
    /// <summary>
    /// <c>HeroHead.init()</c> binds the head to <c>Game.ME.hero</c> — the single global hero, i.e.
    /// player one — so player two's head would follow player one instead of its own body. Re-point
    /// each hero's head at that hero. Called every frame so it also survives the level transitions
    /// that re-run <c>initGfx()</c> (and thus re-bind the head to the global hero).
    /// </summary>
    private static void BindHeroHead(Hero? hero)
    {
        if (hero == null || hero.destroyed)
        {
            return;
        }

        var head = hero.heroHead;
        if (head != null)
        {
            head.hero = hero;
        }
    }

    /// <summary>
    /// Tracks which heroes currently have their head out, from the head's own lifecycle
    /// (created in <c>initGfx</c>, gone again on <c>comeBack</c>/<c>onDie</c>/<c>dispose</c>).
    /// </summary>
    private static void SetHeadOut(Hero? hero, bool value)
    {
        if (hero == null)
        {
            return;
        }

        var idx = HeadOutHeroes.FindIndex(h => ReferenceEquals(h, hero));
        if (value && idx < 0)
        {
            HeadOutHeroes.Add(hero);
        }
        else if (!value && idx >= 0)
        {
            HeadOutHeroes.RemoveAt(idx);
        }
    }

    private static bool IsHeadOut(Hero? hero)
    {
        if (hero == null)
        {
            return false;
        }

        // A plain loop, not `List.Exists(h => ...)`: this runs twice per frame and the lambda
        // allocated a closure every time.
        for (var i = 0; i < HeadOutHeroes.Count; i++)
        {
            if (ReferenceEquals(HeadOutHeroes[i], hero))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The "waiting for the head to come back" crouch is the <c>headPopLoop</c> state animation,
    /// and its condition is global: <c>_level.entitiesByClass[Homunculus].length &gt; 0</c> — so
    /// <em>every</em> hero on the level crouches when any one player throws a head. The
    /// condition itself is a Haxe closure that cannot be hooked, and
    /// <c>AnimManager.applyStateAnims()</c> picks the <b>first</b> state animation (sorted by
    /// priority at registration) whose condition holds — so instead the entry is repositioned
    /// per hero: first for the hero whose head is out (vanilla crouch), last for everyone else
    /// so the always-true <c>idle</c> wins.
    /// <para>
    /// <b>Cached.</b> Finding the entry used to be a linear scan of the anim manager's state-anim
    /// list with a <c>group.ToString()</c> and a string compare per entry, twice per frame. The
    /// entry and its index are remembered instead, and the scan only runs again when the list
    /// itself is gone or has changed shape (a skin change or a level transition rebuilds the anim
    /// manager, which is the only thing that can replace those entries).
    /// </para>
    /// </summary>
    private static void ApplyHeadPopLoopPriority(Hero hero, int index)
    {
        var list = hero.spr?.get_anim()?.stateAnims;
        if (list == null)
        {
            _headPopEntry[index] = null;
            _headPopList[index] = null;
            _headPopListLen[index] = -1;
            _headPopIndex[index] = -1;
            return;
        }

        var length = list.length;
        var entry = _headPopEntry[index];

        if (entry == null
            || !ReferenceEquals(list, _headPopList[index])
            || length != _headPopListLen[index])
        {
            entry = FindHeadPopEntry(list);
            _headPopEntry[index] = entry;
            _headPopList[index] = list;
            _headPopListLen[index] = length;
            _headPopIndex[index] = entry == null ? -1 : list.indexOf(entry, null);
        }

        if (entry == null || length <= 0)
        {
            return;
        }

        var idx = _headPopIndex[index];
        var want = IsHeadOut(hero) ? 0 : length - 1;
        if (idx == want)
        {
            return;
        }

        list.remove(entry);
        if (want == 0)
        {
            list.insert(0, entry);
        }
        else
        {
            list.push(entry);
        }

        _headPopIndex[index] = want;
    }

    /// <summary>Locates the <c>headPopLoop</c> entry in a hero's state-animation list (slow path).</summary>
    private static object? FindHeadPopEntry(dc.hl.types.ArrayObj list)
    {
        for (var i = 0; i < list.length; i++)
        {
            if (list.getDyn(i) is dc.libs.heaps.slib._AnimManager.StateAnim sa
                && string.Equals(sa.group?.ToString(), HeadPopLoopGroup, System.StringComparison.Ordinal))
            {
                return sa;
            }
        }

        return null;
    }

    /// <summary>Per hero: the state-anim entry that drives the crouch, and where it was last seen.</summary>
    private static readonly object?[] _headPopEntry = new object?[2];
    private static readonly object?[] _headPopList = new object?[2];
    private static readonly int[] _headPopListLen = { -1, -1 };
    private static readonly int[] _headPopIndex = { -1, -1 };
}
