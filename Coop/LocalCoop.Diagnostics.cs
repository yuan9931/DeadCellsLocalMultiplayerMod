using dc;
using dc.en;
using dc.pr;

namespace DeadCellsLocalMultiplayerMod;

/// <summary>
/// Part of <see cref="LocalCoop"/>: periodic diagnostic log lines.
/// </summary>
internal static partial class LocalCoop
{
    /// <summary>Compact per-slot skill state, used to tell a stuck charge from a stuck entity.</summary>
    private static string DescribeSkills(Hero? hero)
    {
        if (hero == null || hero.destroyed)
        {
            return "n/a";
        }

        try
        {
            var text = new System.Text.StringBuilder();
            text.Append("charging=").Append(hero.isChargingSkill());
            var inventory = hero.inventory;
            var manager = hero.activeSkillsManager;
            var current = Game.Class.ME?.curLevel;
            var count = inventory?.nbActives ?? 0;
            for (var i = 0; i < count && i < 8; i++)
            {
                var item = inventory!.getActiveOn(i);
                var active = item == null ? null : manager?.getActiveSkill(item);
                var skill = active?.skill;
                text.Append(" slot").Append(i)
                    .Append("[charge=").Append(skill?.chargeF.ToString("F2") ?? "-")
                    .Append(" cd=").Append(skill?.coolDownF.ToString("F2") ?? "-")
                    .Append(" ent=").Append(active?.countEntities() ?? -1);

                // Name the first entity so a frozen throw can be told from a normal one.
                if (active?.entities != null && active.entities.length > 0)
                {
                    if (active.entities.getDyn(0) is Active entity)
                    {
                        text.Append(' ').Append(entity.GetType().Name)
                            .Append(entity.destroyed ? "(dead)" : "(alive)")
                            .Append(ReferenceEquals(entity._level, current) ? " sameLevel" : " OTHERLEVEL");
                    }
                }
                text.Append(']');
            }
            return text.ToString();
        }
        catch (Exception ex)
        {
            return $"error: {ex.Message}";
        }
    }

    /// <summary>
    /// The numbers that decide whether the two heroes really hold the same gear: the shared item
    /// list and slot counts, and each hero's **own** weapon entities. `items`/`nbW`/`nbA` are the
    /// shared `Inventory`; `mainW`/`packW` are `HeroWeaponsManager.mainWeapons`/`backpackWeapons`,
    /// which are per hero — a mismatch there is exactly what "the other hero never got the weapon"
    /// looks like.
    /// </summary>
    private static string DescribeEquipment(Hero? hero)
    {
        if (hero == null || hero.destroyed)
        {
            return "n/a";
        }

        try
        {
            var inventory = hero.inventory;
            var weapons = hero.weaponsManager;
            return $"items={inventory?.items?.length ?? -1} nbW={inventory?.nbWeapons ?? -1} " +
                $"nbA={inventory?.nbActives ?? -1} mainW={weapons?.mainWeapons?.length ?? -1} " +
                $"packW={weapons?.backpackWeapons?.length ?? -1}";
        }
        catch (Exception ex)
        {
            return $"error: {ex.Message}";
        }
    }
    
    private static void LogDiagnostics(double dt, Hero hero2)
    {
        if (!Verbose)
        {
            return;
        }

        _diagTimer += dt;
        if (_diagTimer < 3)
        {
            return;
        }
        _diagTimer = 0;

        var spr = hero2.spr;
        Log($"[LocalCoop] P1 case={_hero1?.cx},{_hero1?.cy} | P2 awake={hero2.awake} destroyed={hero2.destroyed} " +
            $"life={hero2.life} case={hero2.cx},{hero2.cy} xr={hero2.xr} yr={hero2.yr} " +
            $"isFreeze={hero2.isFreeze} hasGravity={hero2.hasGravity} gravity={hero2.gravity:F3} " +
            $"dx={hero2.dx:F2} dy={hero2.dy:F2} " +
            $"level={(hero2._level == null ? "none" : (hero2._level == _hero1?._level ? "same as P1" : "other"))} " +
            $"visible={hero2.visible} alpha={hero2.sprAlpha} onScreenRecent={hero2.onScreenRecent} " +
            $"sprVisible={spr?.visible} sprPos={spr?.x},{spr?.y}");
        Log($"[LocalCoop] skills P1 {DescribeSkills(_hero1)} | P2 {DescribeSkills(hero2)}");
        Log($"[LocalCoop] equipment P1 {DescribeEquipment(_hero1)} | P2 {DescribeEquipment(hero2)}");
        Log($"[LocalCoop] input P1 mode={_controller1?.mode} gc={_controller1?.gc?.device?.index} " +
            $"locked={_controller1?.isLocked} | P2 mode={_controller2?.mode} gc={_controller2?.gc?.device?.index} " +
            $"locked={_controller2?.isLocked}");

        // The UI (map, inventory, pause) reads controllers parented to the global one. This line
        // says whether that chain can still see a pad.
        var ui = Game.Class.ME?.controller;
        var uiParent = ui?.parent;
        var pads = _globalController?.activeGamepads;
        var padCount = pads == null ? -1 : pads.length;
        Log($"[LocalCoop] ui access manual={ui?.manualLock} parentLocked={uiParent?.isLocked} " +
            $"parentMode={uiParent?.mode} parentPad={uiParent?.gc?.device?.index} " +
            $"globalPads={padCount}");

        // Whether each hero is actually parented to its own controller. When a setup step threw
        // on a save-loaded run the controllers were never created, both heroes stayed on the
        // global one, and every pad moved the same hero — this line makes that visible directly.
        Log($"[LocalCoop] bindings P1 own={_controller1 != null} " +
            $"parentIsOwn={ReferenceEquals(_hero1?.controller?.parent, _controller1)} " +
            $"| P2 own={_controller2 != null} " +
            $"parentIsOwn={ReferenceEquals(hero2.controller?.parent, _controller2)}");

        // Entity-vs-entity pushing ("bump"). The raw collision/weight state is dumped for both
        // heroes so a hero that lost its setup (weight, repelling, colLayer) is distinguishable
        // from a live-call asymmetry. The pair factor below is a state dump only: it was measured
        // at 1.00 for *both* heroes on the very frame player one shoved player two, so it is not
        // the gate. The pair rule that decides the walk-push is `canHaveRepellingWith`; the
        // `engineAsked` field on the `[push] contact` line shows whether the engine consults it for
        // the hero pair at all.
        Log($"[LocalCoop] push P1 {DescribePush(_hero1)}");
        Log($"[LocalCoop] push P2 {DescribePush(hero2)}");
        Log($"[LocalCoop] push factor P1->P2={DescribePushFactor(_hero1, hero2)} " +
            $"P2->P1={DescribePushFactor(hero2, _hero1)}");

        // How much of a frame the mod's own per-frame entry points cost (see
        // ModEntry.OnFrameUpdate). The per-hook work — mob AI wrappers, traps, interactables — runs
        // inside the game's own calls and is not included, so a low number here with a low frame rate
        // means the cost is in the hooks, not in this pass.
        var avgCost = _modCostFrames > 0 ? _modCostTotalMs / _modCostFrames : 0;
        Log($"[LocalCoop] cost mod={avgCost:F2}ms avg {_modCostPeakMs:F2}ms peak " +
            $"over {_modCostFrames} frames");
        _modCostTotalMs = 0;
        _modCostPeakMs = 0;
        _modCostFrames = 0;
    }

    /// <summary>Accumulated cost of the mod's own per-frame pass, reported by <see cref="LogDiagnostics"/>.</summary>
    private static double _modCostTotalMs;
    private static double _modCostPeakMs;
    private static int _modCostFrames;

    /// <summary>Adds one frame's mod cost. Only called while <see cref="Verbose"/> is on.</summary>
    internal static void RecordModFrameCost(long startedTicks)
    {
        try
        {
            var ms = (System.Diagnostics.Stopwatch.GetTimestamp() - startedTicks)
                * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            _modCostTotalMs += ms;
            _modCostFrames++;
            if (ms > _modCostPeakMs)
            {
                _modCostPeakMs = ms;
            }
        }
        catch
        {
            // Diagnostics only.
        }
    }

    /// <summary>
    /// The collision/weight state that decides whether an entity can be pushed and whether it
    /// pushes others: <c>get_weight()</c> (base weight scaled by buffs), <c>canApplyRepelling()</c>
    /// (may this entity repel others at all) plus the raw fields behind them, and
    /// <c>colLayer</c>. Comparing P1 and P2 here separates "player two never got set up" from "the
    /// engine special-cases the global hero".
    /// </summary>
    private static string DescribePush(Hero? hero)
    {
        if (hero == null || hero.destroyed)
        {
            return "n/a";
        }

        try
        {
            return $"weight={hero.get_weight():F2} base={hero.baseWeight:F2} " +
                $"circular={hero.circularRepel:F2} hasRepel={hero.hasRepelling} " +
                $"bumpRes={hero.bumpResistance:F2} rectHitbox={hero.useRectHitbox} " +
                $"canRepel={hero.canApplyRepelling()} colLayer={hero.colLayer} " +
                $"radius={hero.radius:F2} hei={hero.hei:F2} " +
                $"team={(hero._team == null ? "null" : hero._team.ToString())}";
        }
        catch (Exception ex)
        {
            return $"error: {ex.Message}";
        }
    }

    /// <summary>
    /// <c>getAdjustedWeightFactor(against)</c> for a hero pair — a state dump only. It read
    /// <b>1.00 for both heroes</b> at the exact moment player one shoved player two (~20px apart,
    /// gap held), so it does not decide the walk-push; the pair rule is
    /// <c>canHaveRepellingWith</c> (see <see cref="InstallWalkPush"/>).
    /// </summary>
    private static string DescribePushFactor(Hero? hero, Hero? against)
    {
        if (hero == null || against == null || hero.destroyed || against.destroyed)
        {
            return "n/a";
        }

        try
        {
            return hero.getAdjustedWeightFactor(against).ToString("F2");
        }
        catch (Exception ex)
        {
            return $"error: {ex.Message}";
        }
    }

    /// <summary>Throttles the contact-time <c>[push] contact</c> log to one line per quarter second.</summary>
    private static long _pushContactTick;

    private static bool PushContactDue()
    {
        var now = System.Environment.TickCount64;
        if (now - _pushContactTick < 250)
        {
            return false;
        }
        _pushContactTick = now;
        return true;
    }

    /// <summary>
    /// Diagnostic only (never changes behaviour). The 3s periodic line always lands on a frame where
    /// both heroes stand still, so it never caught the "walking into each other" state — which is the
    /// only state where the engine's bump behaviour differs. This runs every frame and only prints
    /// while the two heroes are within touching distance (~1.5 cells), so the contact frame is
    /// captured. It dumps, per hero: velocity, facing and sprite x (to see who actually moves) and
    /// every input the repelling system reads, plus whether the two heroes are actually closer than
    /// <c>radius1 + radius2</c> — i.e. whether they ever overlap enough for a push at all.
    /// <c>engineAsked</c> counts how often the engine itself asked
    /// <see cref="OnCanHaveRepellingWith"/> about the P1/P2 pair (the mod never calls it any more),
    /// with the age of the last question: a small age while the heroes shove each other means the
    /// engine asks the pair rule and ignores the <c>false</c>; <c>never</c>/a large age means the
    /// shove is decided where that question is never asked.
    /// </summary>
    internal static void LogPushContact()
    {
        if (!Enabled || !Verbose)
        {
            return;
        }

        var h1 = _hero1;
        var h2 = _hero2;
        try
        {
            if (h1 == null || h2 == null || h1.destroyed || h2.destroyed)
            {
                return;
            }

            if (!ReferenceEquals(h1._level, h2._level))
            {
                return;
            }

            var s1 = h1.spr;
            var s2 = h2.spr;
            if (s1 == null || s2 == null)
            {
                return;
            }

            var dxp = s2.x - s1.x;
            var dyp = s2.y - s1.y;
            var dist = System.Math.Sqrt(dxp * dxp + dyp * dyp);
            if (dist > 56.0 || !PushContactDue())
            {
                return;
            }

            var reach = h1.radius + h2.radius;
            var w12 = h1.getAdjustedWeightFactor(h2);
            var w21 = h2.getAdjustedWeightFactor(h1);
            var bumpRes1 = h1.getBumpResistanceFactor();
            var bumpRes2 = h2.getBumpResistanceFactor();

            var askedAgo = _pairAskedTick == 0
                ? "never"
                : $"{System.Environment.TickCount64 - _pairAskedTick}ms ago";

            Log($"[LocalCoop][push] contact d={dist:F1} (overlap<{reach:F1}? {dist < reach}) " +
                $"P1 dx={h1.dx:F2} dy={h1.dy:F2} xr={h1.xr:F3} sprX={s1.x:F1} dir={h1.dir} " +
                $"| P2 dx={h2.dx:F2} dy={h2.dy:F2} xr={h2.xr:F3} sprX={s2.x:F1} dir={h2.dir}");
            Log($"[LocalCoop][push] contact w12(P1->P2)={w12:F2} w21(P2->P1)={w21:F2} " +
                $"repel1={h1.canApplyRepelling()} repel2={h2.canApplyRepelling()} " +
                $"bumpRes1={bumpRes1:F2} bumpRes2={bumpRes2:F2} " +
                $"pairRule=no-repel engineAsked={_pairAskedCount} ({askedAgo})");
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] push contact diagnostic failed: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Per-hero head diagnostics. Each hero's <c>HeroHead</c> must follow its own body; the head
    /// aims at <c>heroHead.hero</c> (rebound to the owning hero by <see cref="BindHeroHead"/>).
    /// </summary>
    private static void LogHeads(double dt, Hero hero1, Hero hero2)
    {
        if (!Verbose)
        {
            return;
        }

        _headLogTimer += dt;
        if (_headLogTimer < 3)
        {
            return;
        }
        _headLogTimer = 0;

        Log($"[LocalCoop] head P1 {DescribeHead(hero1)} | P2 {DescribeHead(hero2)}");
    }

    private static string DescribeHead(Hero? hero)
    {
        if (hero == null)
        {
            return "none";
        }

        var head = hero.heroHead;
        if (head == null)
        {
            return "no-head";
        }

        var target = head.hero;
        var aim = target == null ? "null" : $"{target.cx},{target.cy}";

        var sb = head.headNormalSb;
        var sbDesc = sb == null
            ? "sb=none"
            : $"sb={sb.x:F0},{sb.y:F0} sbVis={sb.visible} sbPar={(sb.parent != null)}";

        var access = hero.controller;
        var lockDesc = access == null
            ? "lock=none"
            : $"lock=manual:{access.manualLock} parent:{access.parent?.isLocked}";

        var mode = head.get_headMode();
        var modeDesc = $"mode={(mode == null ? "null" : mode.GetType().Name)} stack={head.headModes?.length ?? -1}";

        var spr = hero.spr;
        var anim = spr?.get_anim();
        var animDesc = spr == null
            ? "anim=none"
            : $"anim={spr.groupName} susp={(anim?.suspended ?? false)}";

        return $"selfCase={hero.cx},{hero.cy} boundSelf={ReferenceEquals(target, hero)} aimCase={aim} " +
            $"forced={head.forcedPos != null} moving={head.isMoving} skinHasHead={head.heroHasHead} " +
            $"{modeDesc} heroX={hero.spr.x:F0},{hero.spr.y:F0} {sbDesc} {lockDesc} {animDesc}";
    }

    private static bool _hudLayoutLogged;

    /// <summary>
    /// One-shot dump of where the vanilla HUD actually puts its pieces, in absolute pixels, so the
    /// player-two panel can be laid out against the real thing instead of against a guess. Runs
    /// only with logging on, and only once per session.
    /// </summary>
    private static void LogHudLayoutOnce()
    {
        if (!Logging || _hudLayoutLogged)
        {
            return;
        }
        _hudLayoutLogged = true;

        try
        {
            var hud = Game.Class.ME?.hud;
            if (hud == null)
            {
                return;
            }

            var text = new System.Text.StringBuilder();
            AppendHudBox(text, "lifeBar", hud.heroLifeBar);
            AppendHudBox(text, "tierBar", hud.tierBar);
            AppendHudBox(text, "potion", hud.skillPotion);
            AppendHudBox(text, "backpack", hud.skillBackpack);
            AppendHudArray(text, "weapons", hud.skillWeapons);
            AppendHudArray(text, "powers", hud.skillPowers);

            // The life bar also gives us the row height the panel borrows.
            var lifebar = hud.heroLifeBar;
            if (lifebar?.curState != null)
            {
                text.Append(" lifeSize=").Append(lifebar.curState.outerWid)
                    .Append('x').Append(lifebar.curState.outerHei);
            }
            text.Append(" parentType=").Append(lifebar?.parent?.GetType().Name ?? "null");
            text.Append(" panelLineStep=").Append(_heroLineStep.ToString("F1"));

            Log($"[LocalCoop] hud layout{text}");
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not dump the HUD layout: {Message}", ex.Message);
        }
    }

    private static int _popupDumpCount;

    /// <summary>
    /// Dumps what an open inventory window is actually showing, so the hero picker can be built
    /// against the real thing instead of a guess. Runs at most a few times per window (the first
    /// frames after it opens), with logging on only.
    /// <para>
    /// The line answers the question the whole feature turns on: does the window read
    /// <c>Game.ME.hero</c> per frame (so pointing the global at another hero re-renders it), or did
    /// it snapshot the hero when it opened (so it has to be rebuilt)? <c>elements</c> is the
    /// window's row list, and its count plus a sample of the row labels shows both.
    /// </para>
    /// </summary>
    private static void LogInventoryWindowOnce(dc.ui.DebugPopUp window)
    {
        if (!Logging || _popupDumpCount >= 6)
        {
            return;
        }
        _popupDumpCount++;

        try
        {
            var text = new System.Text.StringBuilder();
            text.Append("elements=").Append(window.elements?.length ?? -1);
            text.Append(" controller=").Append(window.controller == null ? "null" : "set");
            text.Append(" globalHero=").Append(DescribeHero(Game.Class.ME?.hero));
            text.Append(" p1=").Append(DescribeHero(_hero1));
            text.Append(" p2=").Append(DescribeHero(_hero2));

            // A few row labels, so it is visible which hero's gear is on screen.
            var elements = window.elements;
            if (elements != null)
            {
                var shown = 0;
                for (var i = 0; i < elements.length && shown < 6; i++)
                {
                    if (elements.getDyn(i) is not dc.h2d.Object row)
                    {
                        continue;
                    }
                    text.Append(" row").Append(i).Append('=').Append(row.GetType().Name);
                    shown++;
                }
            }

            Log($"[LocalCoop] inv window{text}");
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not dump the inventory window: {Message}", ex.Message);
        }
    }

    /// <summary>Short label for a hero, or "null".</summary>
    private static string DescribeHero(Hero? hero)
    {
        if (hero == null)
        {
            return "null";
        }
        if (hero.destroyed)
        {
            return "destroyed";
        }
        return ReferenceEquals(hero, _hero1) ? "P1"
            : ReferenceEquals(hero, _hero2) ? "P2"
            : "other";
    }

    /// <summary>Appends one HUD element's absolute position, or "null".</summary>
    private static void AppendHudBox(System.Text.StringBuilder text, string name, dc.h2d.Object? element)
    {
        if (element == null)
        {
            text.Append(' ').Append(name).Append("=null");
            return;
        }

        text.Append(' ').Append(name)
            .Append("=(").Append(element.absX.ToString("F0"))
            .Append(',').Append(element.absY.ToString("F0")).Append(')');
    }

    /// <summary>Appends the first few elements of a HUD array (the item slots).</summary>
    private static void AppendHudArray(System.Text.StringBuilder text, string name, dc.hl.types.ArrayObj? array)
    {
        if (array == null)
        {
            text.Append(' ').Append(name).Append("=null");
            return;
        }

        for (var i = 0; i < array.length && i < 4; i++)
        {
            AppendHudBox(text, $"{name}[{i}]", array.getDyn(i) as dc.h2d.Object);
        }
    }

    private static int _pauseDumpCount;
    private static Hero? _pauseDumpHero;

    /// <summary>
    /// Dumps what the open pause screen is actually showing, with logging on only. This is the line
    /// that proves the hero picker works: it reports the hero the window ran as, the hero that was
    /// chosen, and the items the window's own description panels hold *after* the mod re-pointed
    /// them — so "the label changed" and "the gear changed" are distinguishable.
    /// <para>
    /// Logged for the first couple of frames of every open, and again whenever the chosen hero
    /// changes, so a picker press is always captured.
    /// </para>
    /// </summary>
    private static void LogPauseWindow(dc.ui.pause.DefaultPause pause)
    {
        if (!Logging)
        {
            return;
        }

        var chosen = _inventoryWindowHero;
        var heroChanged = !ReferenceEquals(_pauseDumpHero, chosen);
        if (!heroChanged && _pauseDumpCount >= 6)
        {
            return;
        }

        if (heroChanged)
        {
            _pauseDumpHero = chosen;
            _pauseDumpCount = 0;
        }
        _pauseDumpCount++;

        try
        {
            var text = new System.Text.StringBuilder();
            text.Append(" ranAs=").Append(DescribeHero(Game.Class.ME?.hero));
            text.Append(" chosen=").Append(DescribeHero(chosen));
            text.Append(" p1=").Append(DescribeHero(_hero1));
            text.Append(" p2=").Append(DescribeHero(_hero2));

            // What the window's own description panels ended up holding (the mod's setItem result).
            text.Append(" desc.W1=").Append(ItemLabel(pause.weaLeft?.ii));
            text.Append(" desc.W2=").Append(ItemLabel(pause.weaRight?.ii));
            text.Append(" desc.S1=").Append(ItemLabel(pause.skillLeft?.ii));
            text.Append(" desc.S2=").Append(ItemLabel(pause.skillRight?.ii));
            text.Append(" desc.bag=").Append(ItemLabel(pause.skillBackpack?.ii));

            // What the chosen hero actually holds, to compare against the above.
            var inventory = chosen?.inventory;
            if (inventory != null)
            {
                text.Append(" hero.W1=").Append(ItemLabel(inventory.nbWeapons > 0 ? inventory.getEquippedWeaponOn(0) : null));
                text.Append(" hero.W2=").Append(ItemLabel(inventory.nbWeapons > 1 ? inventory.getEquippedWeaponOn(1) : null));
                text.Append(" hero.S1=").Append(ItemLabel(inventory.nbActives > 0 ? inventory.getActiveOn(0) : null));
                text.Append(" hero.S2=").Append(ItemLabel(inventory.nbActives > 1 ? inventory.getActiveOn(1) : null));
            }

            Log($"[LocalCoop] pause window{text}");
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not dump the pause window: {Message}", ex.Message);
        }
    }

    /// <summary>An item's display name, or "-" when the slot is empty or unreadable.</summary>
    private static string ItemLabel(dc.tool.InventItem? item)
    {
        if (item == null)
        {
            return "-";
        }

        try
        {
            return ReadItemName(item);
        }
        catch
        {
            return "?";
        }
    }
}
