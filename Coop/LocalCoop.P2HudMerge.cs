using dc;
using dc.en;
using dc.h2d;
using dc.pr;
using dc.tool;
using dc.ui;
using dc.ui.hud;
using HaxeProxy.Runtime;
using ModCore.Utilities;

namespace DeadCellsLocalMultiplayerMod;

/// <summary>
/// Part of <see cref="LocalCoop"/>: player two's HUD, MERGED into the vanilla HUD's own layout.
/// <para>
/// <b>Why merge instead of a second column.</b> An earlier build drew player two's widgets in a
/// private column beside the vanilla one, positioned by hand against the vanilla geometry. Every
/// part of that had to be measured, frozen and re-checked — the icon row's width, the bar's box,
/// the vanilla column's right edge — because the vanilla HUD re-lays itself on equipment changes,
/// level changes and HUD-size changes, and a hand-placed column drifts every time. Merging removes
/// the whole problem: the vanilla flows lay the widgets out, so player two's pieces sit exactly
/// where the game puts them and move with it.
/// </para>
/// <para>
/// <b>The layout.</b> The vanilla left column is two rows inside <c>leftFlow</c>:
/// <c>leftFlowT</c> — a horizontal row with the flask, the weapons, the active skills and the
/// backpack — over <c>leftFlowB</c> — another horizontal row with the scroll icons to the left of
/// the life bar. Player two's slots are appended to <c>leftFlowT</c> after player one's, and
/// player two's tier bar and life bar live in a third row added to <c>leftFlow</c> under the
/// vanilla one. So the icon row reads [P1 … P2 …] and the bars read as two stacked rows.
/// </para>
/// <para>
/// <b>Rebuilding.</b> <c>HUD.initLeftFlowT</c> throws both vanilla rows away and builds new ones on
/// every <c>initHero</c>, which orphans anything parented to the old rows — so the widgets are
/// (re)built from a hook on that method, plus a per-frame fallback for runs restored from a save
/// (which never call <c>Game.initHero</c>). Equipment changes only re-map the items onto the
/// existing slots: the slots themselves stay where the vanilla row put them.
/// </para>
/// </summary>
internal static partial class LocalCoop
{
    /// <summary>How often the clone's slots, flask and tiers are re-read (0.1 s, the cooldown step).</summary>
    private const double P2CloneInterval = 0.1;

    /// <summary>Seconds since the slots were last read.</summary>
    private static double _p2SlotTimer;

    /// <summary>Seconds since the last slot-build retry (see UpdateP2HudClone).</summary>
    private static double _p2SlotRetry;

    /// <summary>
    /// The row holding player two's tier bar and life bar, a child of the vanilla <c>leftFlow</c>
    /// (so it stacks under the vanilla bar row). Null when it is not built.
    /// </summary>
    private static Flow? _p2BarRow;

    private static dc.ui.hud.LifeBar? _p2Life;

    private static TierBar? _p2Tiers;

    private static dc.ui.hud.Skill? _p2Flask;

    /// <summary>The clone's backpack slot (the reserve), or null when it is not built.</summary>
    private static dc.ui.hud.Skill? _p2Backpack;

    /// <summary>
    /// Player two's mutation icons, a row appended to the bar row. The vanilla stock
    /// (<c>HUD.perkStock</c>) is filled from the GLOBAL hero's bag only, so player two's mutations
    /// would otherwise never show anywhere.
    /// </summary>
    private static Flow? _p2PerkStock;

    /// <summary>Hash of the perks last painted, so the row is rebuilt only when they change.</summary>
    private static int _p2PerkSignature = int.MinValue;

    private static readonly dc.ui.hud.Skill?[] _p2Weapons = new dc.ui.hud.Skill?[2];

    private static readonly dc.ui.hud.Skill?[] _p2Powers = new dc.ui.hud.Skill?[4];

    /// <summary>The HUD the widgets were built for, so a rebuilt HUD rebuilds them with it.</summary>
    private static HUD? _p2HudOwner;

    private static bool _p2HudBroken;

    private static bool _p2SlotsBuilt;

    /// <summary>
    /// Whether the widgets were successfully attached (the bar row exists). Without it a HUD whose
    /// <c>leftFlow</c> cannot be read would be rebuilt every single frame.
    /// </summary>
    private static bool _p2WidgetsBuilt;

    private static int _p2FlaskCur = -1;

    private static int _p2FlaskMax = -1;

    private static int _p2TierBrutality = -1;

    private static int _p2TierTactic = -1;

    private static int _p2TierSurvival = -1;

    /// <summary>Signature of the bag the slots were last mapped from, so a swap self-heals.</summary>
    private static int _p2BagSignature = int.MinValue;

    /// <summary>Whether player two's own life bar is up and describing player two.</summary>
    private static bool P2CloneLifeActive => _p2Life != null && !_p2HudBroken && _p2WantedLife;

    /// <summary>Whether player two's flask / weapon / skill slots are up.</summary>
    private static bool P2CloneSlotsActive => _p2SlotsBuilt && !_p2HudBroken && _p2WantedSlots;

    private static bool _p2WantedLife;

    private static bool _p2WantedSlots;

    /// <summary>
    /// The vanilla life bar's frozen width and the height it was measured at (0 = not frozen).
    /// The vanilla bar's box is part of the HUD column's layout, so a drifting width moves the
    /// icon row above it; freezing it (and mirroring it onto player two's bar) keeps the merged
    /// layout still.
    /// </summary>
    private static int _vanillaBarWidth;

    private static int _vanillaBarHeight;

    /// <summary>
    /// Registers the hooks that feed the merged HUD. Called once from <see cref="Install"/>; every
    /// handler falls through to the original while the session is off or player two is not
    /// involved, so the vanilla game is untouched.
    /// </summary>
    private static void InstallP2HudCloneHooks()
    {
        // The vanilla rebuilds both of its rows on every initHero, which orphans anything parented
        // to them — so player two's widgets are (re)attached right after each rebuild.
        Hook_HUD.initLeftFlowT += OnHudInitLeftFlowT;

        // The slot build: `Hero.hudInitItems()` runs it for whichever hero changed gear. For
        // player two (independent bag) the shared slots must not be repainted with player two's
        // items — the merged row's own slots are re-mapped instead.
        Hook_HUD.initItems += OnHudInitItemsForMerge;

        // The per-slot closure binding. `HeroActiveSkillsManager` resolves its HUD slot through
        // this for the cooldown highlight, the ammo closures and the cooldown pop; for player
        // two's manager it has to resolve to the merged row's slot or player two's item use
        // paints player one's HUD.
        dc.tool.hero.Hook_HeroActiveSkillsManager.hudGetSkillPower += OnActiveHudGetSkillPower;

        // The repaint paths the weapons manager fires on its own hero's behalf.
        Hook_HUD.updateAmmo += OnHudUpdateAmmoForMerge;
        Hook_HUD.setWeaponFade += OnHudSetWeaponFadeForMerge;
        Hook_HUD.setPowerFade += OnHudSetPowerFadeForMerge;
        Hook_HUD.setSkillFade += OnHudSetSkillFadeForMerge;

        // The lock fades. `addWeaponLock` / `removeSkillLock` and friends do two jobs in one
        // call: they record the lock in the manager's own map (gameplay — a locked weapon really
        // is unusable) AND repaint the HUD slot. The gameplay half must always run, so the
        // original is never skipped here; instead the manager hooks below mark which hero is
        // writing, and the HUD-level fade hooks read that mark to keep player two's locks off
        // player one's slots.
        dc.tool.hero.Hook_HeroWeaponsManager.addWeaponLock += OnWeaponsAddLockForMerge;
        dc.tool.hero.Hook_HeroWeaponsManager.removeWeaponLock += OnWeaponsRemoveLockForMerge;
        dc.tool.hero.Hook_HeroWeaponsManager.applyHudLockFade += OnWeaponsApplyHudLockFadeForMerge;
        dc.tool.hero.Hook_HeroActiveSkillsManager.addSkillLock += OnSkillsAddLockForMerge;
        dc.tool.hero.Hook_HeroActiveSkillsManager.removeSkillLock += OnSkillsRemoveLockForMerge;
        dc.tool.hero.Hook_HeroActiveSkillsManager.applyHudLockFade += OnSkillsApplyHudLockFadeForMerge;

        // The two remaining shared-HUD writes that carry no hero at all: the pickup animation
        // (keyed on the item, and player two's items are not in the shared slots) and the
        // "your weapon is weak here" buff (keyed on the hero it is shown for).
        Hook_HUD.onItemPick += OnHudOnItemPickForMerge;
        Hook_HUD.showBuffFor += OnHudShowBuffForForMerge;

        // The vanilla life bar's width curve, and the one place player two's bar is sized: the
        // vanilla width is frozen (a drifting box moves the merged layout) and player two's bar
        // mirrors it.
        Hook_HUD.updateLifeSize += OnHudUpdateLifeSizeForMerge;

        // The one shared-slot writer that carries no hero at all (see OnHudUpdateSkillBgsForMerge).
        Hook_HUD.updateSkillBgs += OnHudUpdateSkillBgsForMerge;

        LogVerbose("[LocalCoop] P2 HUD merge hooks installed");
    }

    /// <summary>
    /// <c>HUD.initLeftFlowT</c> — the vanilla just threw both of its rows away and built new ones.
    /// Player two's widgets belong to those rows, so they are rebuilt into the new ones here; the
    /// bar row is re-added to <c>leftFlow</c> (and moved to the end, because the vanilla's
    /// remove-then-add leaves it above the vanilla rows otherwise).
    /// </summary>
    private static void OnHudInitLeftFlowT(Hook_HUD.orig_initLeftFlowT orig, HUD self)
    {
        orig(self);

        if (!Enabled)
        {
            return;
        }

        // The wanted flags are normally set by the per-frame pass, which has not run yet when the
        // first level builds its HUD — they start false, and a rebuild with them false would skip
        // the slots entirely (leaving only the bar row, which then reads as "no icons"). They are
        // pure settings, so refresh them here too.
        _p2WantedLife = !SharedHealth;
        _p2WantedSlots = !SharedEquipment;

        try
        {
            RebuildP2Widgets(self);
        }
        catch (Exception ex)
        {
            _p2HudBroken = true;
            DestroyP2Widgets();
            LogWarn("[LocalCoop] P2 HUD merge disabled after an error: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Per-frame driver: builds the widgets the first time they are seen, feeds the slots, the
    /// flask, the tiers and the bar, and tears everything down when the mode no longer wants it.
    /// Called from <c>UpdateHeroPanels</c>.
    /// </summary>
    private static void UpdateP2HudClone(double dt)
    {
        if (!Enabled || _p2HudBroken)
        {
            if (_p2BarRow != null || _p2Life != null || _p2Flask != null)
            {
                DestroyP2Widgets();
            }

            return;
        }

        HUD? hud;
        Hero? hero2;
        try
        {
            hud = Game.Class.ME?.hud;
            hero2 = _hero2;
        }
        catch
        {
            return;
        }

        if (hud == null || hud.heroLifeBar == null || hero2 == null || hero2.destroyed)
        {
            DestroyP2Widgets();
            return;
        }

        // What player two needs of its own: a life bar whenever the health pool is not shared
        // (with shared health the vanilla bar already says both), and the flask / weapon / skill
        // slots whenever the backpack is not shared (with a shared bag the vanilla slots already
        // describe both heroes' gear).
        _p2WantedLife = !SharedHealth;
        _p2WantedSlots = !SharedEquipment;

        if (!_p2WantedLife && !_p2WantedSlots)
        {
            DestroyP2Widgets();
            return;
        }

        try
        {
            // Rebuild when the HUD changed or the attach never succeeded. When only the slots are
            // missing — a rebuild that ran from the initLeftFlowT hook before hero two existed, or
            // a slot build that threw — build just the slots, at most once a second: a build that
            // keeps failing must not spam the log (or churn the row) every frame.
            if (!ReferenceEquals(_p2HudOwner, hud) || !_p2WidgetsBuilt)
            {
                RebuildP2Widgets(hud);
            }
            else if (_p2WantedSlots && !_p2SlotsBuilt)
            {
                _p2SlotRetry += dt;
                if (_p2SlotRetry >= 1.0)
                {
                    _p2SlotRetry = 0.0;
                    TryBuildP2Slots(hud);
                }
            }

            if (_p2Life != null)
            {
                _p2Life.visible = _p2WantedLife;
            }

            if (_p2Tiers != null)
            {
                _p2Tiers.visible = _p2WantedSlots;
            }

            // The low-life pulse is a per-frame animation (see LifeBar.postUpdate), and the
            // redirect in OnHeroUpdateLifeBar only runs when the value changes.
            _p2Life?.postUpdate();

            _p2SlotTimer += dt;
            if (_p2SlotTimer >= P2CloneInterval)
            {
                _p2SlotTimer = 0;
                if (_p2WantedSlots)
                {
                    RefreshP2Slots(hero2);
                    PaintP2Flask(hero2);
                    PaintP2Tiers(hero2);
                    EnsureP2PerkStock(hud);
                    AlignP2PerkStock(hud);
                    PaintP2Perks(hero2);
                }
            }
        }
        catch (Exception ex)
        {
            // Latch and drop: the callers fall back to the readouts, so player two keeps a
            // readout either way.
            _p2HudBroken = true;
            DestroyP2Widgets();
            LogWarn("[LocalCoop] P2 HUD merge disabled after an error: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Builds (or rebuilds) player two's widgets into the vanilla HUD: the slots at the end of the
    /// vanilla icon row, the tier bar and life bar in a new row under the vanilla bar row.
    /// </summary>
    private static void RebuildP2Widgets(HUD hud)
    {
        DestroyP2Widgets();
        _p2HudOwner = hud;
        _p2HudBroken = false;

        var icons = hud.leftFlowT;
        if (icons == null)
        {
            return;
        }

        TryBuildP2Slots(hud);
        EnsureP2BarRow(hud);
        _p2WidgetsBuilt = _p2BarRow != null;
    }

    /// <summary>
    /// Creates player two's bar row inside the vanilla <c>leftFlow</c> — or moves the existing one
    /// to the end of it, because the vanilla's own rebuild (remove-then-add of its two rows)
    /// leaves an earlier-added child above them.
    /// </summary>
    private static void EnsureP2BarRow(HUD hud)
    {
        var parent = hud.leftFlow;
        if (parent == null)
        {
            return;
        }

        if (_p2BarRow == null || _p2Life == null || _p2Tiers == null)
        {
            var row = new Flow(parent);
            row.set_isVertical(false);
            // The vanilla bar row centres its children vertically, which is what puts the shorter
            // scroll icons on the life bar's own line; the merged row does the same.
            row.set_verticalAlign(new FlowAlign.Middle());
            _p2BarRow = row;

            var ctrlBg = true;
            var showTier = true;
            var tiers = new TierBar(row, 18, 14, true);
            tiers.get_pixelScale = () => P2PixelScale();
            _p2Tiers = tiers;

            var life = new dc.ui.hud.LifeBar(new LifeBarColorMode.Normal(), row);
            life.get_pixelScale = () => P2PixelScale();
            life.enableText();
            _p2Life = life;



            // The tier icons want the same look as player one's; the bar's box is sized by
            // OnHudUpdateLifeSizeForMerge (it mirrors the vanilla bar's frozen width).
            _ = ctrlBg;
            _ = showTier;
        }
        else if (!ReferenceEquals(_p2BarRow.parent, parent))
        {
            _p2BarRow.parent?.removeChild(_p2BarRow);
            parent.addChild(_p2BarRow);
        }
        else
        {
            // Same parent, but the vanilla's rebuild re-added its rows after us: move to the end
            // so player two's row stays UNDER player one's.
            parent.removeChild(_p2BarRow);
            parent.addChild(_p2BarRow);
        }

        // Sizing: the vanilla bar's box is frozen by the updateLifeSize hook; mirror it here too,
        // in case this row was built before that hook first ran.
        SizeP2LifeBar(hud);

        EnsureP2PerkStock(hud);
    }

    /// <summary>
    /// Creates player two's mutation row INSIDE the vanilla mutation row (<c>HUD.perkStock</c>),
    /// so it is laid out by the same flow as player one's icons: same line, same scale, right
    /// after them — which puts it above player two's icons. The vanilla stock only ever walks the
    /// global hero's bag, so player two's mutations would otherwise show nowhere.
    /// <para>
    /// It has to be re-attached rather than positioned: <c>HUD.initItems</c> calls
    /// <c>perkStock.removeChildren()</c> on every equipment change, which drops our row as well.
    /// The parent check below re-adds it (at the end, so the order stays player one then player
    /// two), and it runs on the 0.1 s beat because that rebuild is the game's, not ours.
    /// </para>
    /// </summary>
    private static void EnsureP2PerkStock(HUD hud)
    {
        var vanilla = hud.perkStock;
        if (vanilla == null)
        {
            return;
        }

        if (_p2PerkStock != null && ReferenceEquals(_p2PerkStock.parent, vanilla))
        {
            return;
        }

        RemoveP2PerkStock();

        try
        {
            var stock = new Flow(vanilla);
            stock.set_isVertical(false);
            stock.set_verticalAlign(new FlowAlign.Middle());
            // Absolute: a normal child is laid out AFTER player one's mutation icons, and the
            // alignment below then pushed it further right. Absolute keeps our own x/y, which is
            // what lets the icons start above player two's flask.
            vanilla.getProperties(stock).set_isAbsolute(true);
            _p2PerkStock = stock;
            _p2PerkSignature = int.MinValue;
            vanilla.reflow();
        }
        catch (Exception ex)
        {
            LogVerbose($"[LocalCoop] could not create the P2 mutation row: {ex.Message}");
        }
    }

    /// <summary>
    /// Aligns player two's mutation icons with player two's own flask. The mutation row and the
    /// icon row are both children of the vanilla column, so their x axes agree — the flask's
    /// offset inside the icon row is exactly the left padding our row needs to start above it.
    /// </summary>
    private static void AlignP2PerkStock(HUD hud)
    {
        var stock = _p2PerkStock;
        var flask = _p2Flask;
        var icons = hud.leftFlowT;
        var vanilla = hud.perkStock;
        if (stock == null || flask == null || icons == null || vanilla == null)
        {
            return;
        }

        try
        {
            // Where player two's mutation icons start, in the mutation row's own space.
            //
            // The flask's x inside the icon row is the distance to cover, but it is expressed in
            // the icon row's units while this row's units are smaller by the HUD scale: the
            // mutation row is scaled by `pixelScale * hudSize` (its icons are drawn at the tile's
            // natural size and scaled up to match player one's), so setting the raw distance put
            // the icons at twice it — six slots too far right. Dividing by that scale lands them
            // on the flask, and it is a computed value, not a measurement.
            var rowScale = P2PixelScale();
            if (rowScale <= 0.01)
            {
                rowScale = 1.0;
            }

            stock.x = (flask.x + icons.x - vanilla.x) / rowScale;

            // Vertically: centred on the mutation line's own height.
            var rowHeight = vanilla.get_outerHeight();
            var ownHeight = stock.get_outerHeight();
            stock.y = ownHeight > 0 && rowHeight > ownHeight ? (rowHeight - ownHeight) * 0.5 : 0.0;
        }
        catch (Exception ex)
        {
            LogVerbose($"[LocalCoop] could not align the P2 mutation row: {ex.Message}");
        }
    }

    /// <summary>Drops player two's mutation row.</summary>
    private static void RemoveP2PerkStock()
    {
        var stock = _p2PerkStock;
        if (stock != null)
        {
            try
            {
                stock.parent?.removeChild(stock);
            }
            catch (Exception ex)
            {
                LogVerbose($"[LocalCoop] could not remove the P2 mutation row: {ex.Message}");
            }
        }

        _p2PerkStock = null;
        _p2PerkSignature = int.MinValue;
    }

    /// <summary>
    /// Sizes player two's bar to match player one's: the same (frozen) width and the same height.
    /// Called after the vanilla freeze is established, and never again unless the HUD is rescaled
    /// — the row it lives in is laid out by the game, so a drifting box would move the row.
    /// </summary>
    private static void SizeP2LifeBar(HUD hud)
    {
        var life = _p2Life;
        var bar = hud.heroLifeBar;
        if (life == null || bar?.curState == null)
        {
            return;
        }

        var width = _vanillaBarWidth > 0 ? _vanillaBarWidth : bar.curState.outerWid;
        var height = bar.curState.outerHei;
        if (life.curState != null && life.curState.outerWid == width && life.curState.outerHei == height)
        {
            return;
        }

        life.setSize(width, height);
        if (life.oldState != null)
        {
            // Both states: the width is interpolated between them, and an animating box would
            // re-lay the row it sits in.
            life.oldState.outerWid = width;
            life.oldState.outerHei = height;
        }
    }

    /// <summary>Drops every widget. Safe to call when nothing is built.</summary>
    private static void DestroyP2Widgets()
    {
        var row = _p2BarRow;
        if (row != null)
        {
            try
            {
                row.parent?.removeChild(row);
            }
            catch (Exception ex)
            {
                LogWarn("[LocalCoop] could not remove the P2 HUD row: {Message}", ex.Message);
            }
        }

        _p2BarRow = null;
        _p2Life = null;
        _p2Tiers = null;
        RemoveP2PerkStock();

        // The slots are children of the vanilla icon row, which the game never rebuilds on our
        // behalf — so they have to be taken OUT of it, or a rebuild leaves the old ones behind as
        // duplicates that keep drawing stale items.
        RemoveP2Slot(_p2Flask);
        RemoveP2Slot(_p2Backpack);
        foreach (var slot in _p2Weapons)
        {
            RemoveP2Slot(slot);
        }

        foreach (var slot in _p2Powers)
        {
            RemoveP2Slot(slot);
        }

        _p2Flask = null;
        _p2Backpack = null;
        for (var i = 0; i < _p2Weapons.Length; i++)
        {
            _p2Weapons[i] = null;
        }

        for (var i = 0; i < _p2Powers.Length; i++)
        {
            _p2Powers[i] = null;
        }

        _p2SlotsBuilt = false;
        _p2WidgetsBuilt = false;
        _p2SlotRetry = 0.0;
        _p2HudOwner = null;
        _p2WantedLife = false;
        _p2WantedSlots = false;
        _p2FlaskCur = -1;
        _p2FlaskMax = -1;
        _p2TierBrutality = -1;
        _p2TierTactic = -1;
        _p2TierSurvival = -1;
        _p2BagSignature = int.MinValue;
    }

    /// <summary>Takes one of the merged slots out of the vanilla icon row.</summary>
    private static void RemoveP2Slot(dc.ui.hud.Skill? slot)
    {
        if (slot == null)
        {
            return;
        }

        try
        {
            slot.parent?.removeChild(slot);
        }
        catch (Exception ex)
        {
            LogVerbose($"[LocalCoop] could not remove a P2 HUD slot: {ex.Message}");
        }
    }

    /// <summary>
    /// The HUD's own pixel scale: <c>Main.pixelScale * options.hudSize</c>, the same value
    /// <c>HUD.get_pixelScale()</c> returns. The widgets are laid out in "HUD pixels" and scaled by
    /// it, so the merged row has to use the game's own scale to line up with the vanilla HUD.
    /// </summary>
    private static double P2PixelScale()
    {
        try
        {
            var main = Main.Class.ME;
            if (main == null)
            {
                return 1.0;
            }

            var options = main.options;
            return main.pixelScale * (options?.hudSize ?? 1.0);
        }
        catch
        {
            return 1.0;
        }
    }

    /// <summary>
    /// Creates player two's slots at the end of the vanilla icon row and maps the bag onto them.
    /// The slots are children of the vanilla row, so the vanilla flow lays them out — they simply
    /// continue the row after player one's icons.
    /// </summary>
    private static void BuildP2Slots(Flow icons, Hero hero)
    {
        var ctrlBg = true;
        var showTier = true;

        LogVerbose("[LocalCoop] P2 merge: creating the flask slot");
        // The flask first, like the vanilla row (it is the left-most icon).
        var flask = new dc.ui.hud.Skill(7, icons, Ref<bool>.From(ref ctrlBg), Ref<bool>.From(ref showTier));
        flask.get_pixelScale = () => P2PixelScale();
        flask.setButton(7);
        _p2Flask = flask;

        // The backpack slot, in the same place the vanilla puts it: `HUD.initLeftFlowT` creates it
        // between the weapons and the powers when the "backpack next to weapon" option is on, and
        // last when it is off.
        var backpackNextToWeapon = IsBackpackNextToWeapon();
        if (backpackNextToWeapon)
        {
            BuildP2Backpack(icons, ref ctrlBg, ref showTier);
        }

        // One slot per weapon input, exactly like `HUD.initLeftFlowT` builds them from
        // `game.hero.weaponsManager.weaponInputs` — the input id is what draws the pad button on
        // the slot.
        LogVerbose("[LocalCoop] P2 merge: creating the weapon slots");
        var inputs = hero.weaponsManager?.weaponInputs;
        var weaponCount = inputs != null ? System.Math.Min(inputs.length, _p2Weapons.Length) : 0;
        for (var i = 0; i < weaponCount; i++)
        {
            var act = System.Runtime.InteropServices.Marshal.ReadInt32(inputs!.bytes, i * 4);
            var slot = new dc.ui.hud.Skill((int?)act, icons, Ref<bool>.From(ref ctrlBg), Ref<bool>.From(ref showTier));
            slot.get_pixelScale = () => P2PixelScale();
            _p2Weapons[i] = slot;
        }

        // One slot per active skill. The vanilla pushes them in manager order and then REVERSES the
        // array — so `skillPowers[idx]` is the widget built for `activeSkills[count-1-idx]`.
        // Creating them in that same reversed order keeps the icons where player one's are, and
        // keeps `_p2Powers[idx]` answering to the same array index the game maps items by.
        LogVerbose("[LocalCoop] P2 merge: creating the skill slots");
        var actives = hero.activeSkillsManager?.activeSkills;
        var powerCount = actives != null ? System.Math.Min(actives.length, _p2Powers.Length) : 0;
        for (var created = 0; created < powerCount; created++)
        {
            var idx = powerCount - 1 - created;
            var key = 0;
            try
            {
                key = (actives!.getDyn(idx) as dc.tool.hero.HeroActiveSkill)?.key ?? 0;
            }
            catch
            {
                key = 0;
            }

            var slot = new dc.ui.hud.Skill((int?)key, icons, Ref<bool>.From(ref ctrlBg), Ref<bool>.From(ref showTier));
            slot.get_pixelScale = () => P2PixelScale();
            _p2Powers[idx] = slot;
        }

        if (!backpackNextToWeapon)
        {
            BuildP2Backpack(icons, ref ctrlBg, ref showTier);
        }

        // The vanilla HUD runs `HUD.onResize()` over every slot right after building them, and that
        // pass is what sizes a slot: `Skill.onResize()` is the only caller of `mainBg.onResize(w, h)`
        // and the only place the icon and the button glyph are positioned. Without it a slot keeps
        // the zero-sized box its constructor left and draws nothing at all.
        LogVerbose("[LocalCoop] P2 merge: resizing and reflowing the row");
        ResizeP2Slots();
        icons.reflow();

        LogVerbose("[LocalCoop] P2 merge: mapping the bag onto the slots");
        MapP2Items(hero);
        _p2SlotsBuilt = true;
        _p2FlaskCur = -1;
        _p2BagSignature = ReadP2BagSignature(hero);
        LogVerbose($"[LocalCoop] P2 HUD merged into the vanilla icon row: weapons={weaponCount} "
            + $"powers={powerCount} backpack={(_p2Backpack != null ? 1 : 0)} rowChildren={icons.children.length}");
    }

    /// <summary>
    /// Builds the merged slots if the mode wants them and hero two exists. A failure is a warning,
    /// never a latch: without the slots player two's gear is carried by the text readouts (the
    /// behaviour the merge replaced), and the bar row is worth keeping whatever happened here.
    /// </summary>
    private static void TryBuildP2Slots(HUD hud)
    {
        if (!_p2WantedSlots || _p2SlotsBuilt)
        {
            return;
        }

        var hero2 = _hero2;
        var icons = hud.leftFlowT;
        if (hero2 == null || hero2.destroyed || icons == null)
        {
            return;
        }

        try
        {
            BuildP2Slots(icons, hero2);
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] P2 HUD merge could not build the icon slots: {Message}", ex.Message);
        }
    }

    /// <summary>Whether the vanilla HUD puts the backpack slot next to the weapons (an option).</summary>
    private static bool IsBackpackNextToWeapon()
    {
        try
        {
            return Main.Class.ME?.options?.backpackNextToWeapon ?? false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Creates the merged backpack slot: no button (it is opened with the backpack key), the
    /// vanilla <c>btnBackpack</c> icon centred on it, and the vanilla's backpack alpha. It also
    /// receives the weapon that does not fit a main slot — the same overflow rule
    /// <c>HUD.initItems</c> applies (<c>posID == skillWeapons.length</c>).
    /// </summary>
    private static void BuildP2Backpack(Flow icons, ref bool ctrlBg, ref bool showTier)
    {
        var slot = new dc.ui.hud.Skill(null, icons, Ref<bool>.From(ref ctrlBg), Ref<bool>.From(ref showTier));
        slot.get_pixelScale = () => P2PixelScale();

        try
        {
            var tile = Assets.Class.ui.getTile("btnBackpack".AsHaxeString(),
                Ref<int>.Null, Ref<double>.Null, Ref<double>.Null, null);
            if (tile != null)
            {
                var dx = -(int)(0.5 * tile.width);
                tile.dx = dx;
                var dy = -(int)(0.5 * tile.height);
                tile.dy = dy;
                slot.setCustomIcon(tile);
            }
        }
        catch (Exception ex)
        {
            LogVerbose($"[LocalCoop] could not set the P2 backpack icon: {ex.Message}");
        }

        try
        {
            slot.alpha = Main.Class.ME?.options?.backpackAlpha ?? 1.0;
        }
        catch
        {
            // Keep the default alpha.
        }

        _p2Backpack = slot;
    }

    /// <summary>
    /// Maps player two's bag onto the merged slots: weapons and active skills by <c>posID</c>, the
    /// same rule <c>HUD.initItems</c> uses. Slots without an item are emptied.
    /// </summary>
    private static void MapP2Items(Hero hero)
    {
        // The flask is a MAIN SKILL, not a bag item: the vanilla `HUD.initItems` never touches
        // `skillPotion`, and `useItem(null)` clears the icon the flask paints for itself — which
        // is why a pickup (anything that re-maps the bag) made player two's flask vanish until the
        // charge count happened to change. Only its cooldown is cancelled here.
        _p2Flask?.cancelCooldown();
        _p2Backpack?.cancelCooldown();
        _p2Backpack?.useItem(null);
        foreach (var slot in _p2Weapons)
        {
            slot?.cancelCooldown();
            slot?.useItem(null);
        }

        foreach (var slot in _p2Powers)
        {
            slot?.cancelCooldown();
            slot?.useItem(null);
        }

        var items = hero.inventory?.items;
        if (items == null)
        {
            return;
        }

        for (var i = 0; i < items.length && i < 64; i++)
        {
            var item = items.getDyn(i) as InventItem;
            if (item == null)
            {
                continue;
            }

            var pos = item.posID;
            if (item.kind is InventItemKind.Weapon)
            {
                if (pos >= 0 && pos < _p2Weapons.Length && _p2Weapons[pos] != null)
                {
                    _p2Weapons[pos]!.useItem(item);
                }
                else if (pos == _p2Weapons.Length && _p2Backpack != null)
                {
                    // The weapon that does not fit a main slot sits in the backpack, exactly like
                    // `HUD.initItems` routes it.
                    _p2Backpack.useItem(item);
                }
            }
            else if (item.kind is InventItemKind.Active)
            {
                if (pos >= 0 && pos < _p2Powers.Length && _p2Powers[pos] != null)
                {
                    _p2Powers[pos]!.useItem(item);
                }
            }
        }

        // Repaint the flask on the next beat whatever happened above: its icon is drawn from the
        // charge count, and a stale cache would leave it blank.
        _p2FlaskCur = -1;
    }

    /// <summary>
    /// Runs the vanilla <c>HUD.onResize()</c> pass over the merged slots: <c>Skill.onResize()</c>
    /// sizes the slot's background box and positions its icon, tier icon and button glyph. Called
    /// after the slots are built, after every bag mapping and whenever the HUD is rescaled.
    /// </summary>
    private static void ResizeP2Slots()
    {
        try
        {
            _p2Flask?.onResize();
            _p2Backpack?.onResize();
            foreach (var slot in _p2Weapons)
            {
                slot?.onResize();
            }

            foreach (var slot in _p2Powers)
            {
                slot?.onResize();
            }

            _p2Tiers?.onResize();
        }
        catch (Exception ex)
        {
            LogVerbose($"[LocalCoop] could not resize the P2 HUD slots: {ex.Message}");
        }
    }

    /// <summary>
    /// Writes one hero's life into player two's bar — the body of <c>Hero.updateLifeBar</c> against
    /// our own bar instead of the shared one, so player two gets the same fill, the same delayed
    /// "recover" segment, the same bonus segment and the same heal/damage feedback the vanilla bar
    /// gives player one.
    /// </summary>
    private static void PaintP2CloneLife(Hero hero)
    {
        var bar = _p2Life;
        if (bar == null || bar.curState == null || bar.oldState == null)
        {
            return;
        }

        // A downed hero is held at 1 HP (see Coop/LocalCoop.Down.cs) but must read empty.
        var life = IsDown(hero) ? 0 : System.Math.Max(0, hero.life);
        var maxLife = System.Math.Max(0, hero.maxLife);
        var recover = System.Math.Max(0, hero.curRally - life);
        var bonus = System.Math.Max(0, hero.bonusLife);

        if (bar.curState.maxLife != maxLife)
        {
            bar.curState.maxLife = maxLife;
            bar.updateContent();
        }

        if (bar.curState.life != life)
        {
            var old = bar.oldState.life;
            var previous = (bar.curState.life - old) * bar.stateFade + old;
            bar.curState.life = life;
            bar.updateContent();
            if (!bar.isInit)
            {
                var current = (bar.curState.life - old) * bar.stateFade + old;
                if (previous < current)
                {
                    bar.onHeal(previous, current);
                }
                else if (current < previous)
                {
                    bar.onDamage(previous, current);
                }
            }
        }

        if (bar.curState.recover != recover)
        {
            bar.curState.recover = recover;
            bar.updateContent();
        }

        if (bar.curState.bonusLife != bonus)
        {
            var old = bar.oldState.bonusLife;
            var previous = (bar.curState.bonusLife - old) * bar.stateFade + old;
            bar.curState.bonusLife = bonus;
            bar.updateContent();
            if (!bar.isInit)
            {
                var current = (bar.curState.bonusLife - old) * bar.stateFade + old;
                if (current < previous)
                {
                    bar.onDamageBonus(previous, current);
                }
            }
        }

        // The vanilla bar's low-life flag is set by HUD.postUpdate from the global hero; ours is
        // set here, from player two's own ratio.
        bar.lowLife = maxLife > 0 && life / (double)maxLife < 0.33;
    }

    /// <summary>
    /// Re-reads the merged slots on the 0.1 s beat: the charge count the vanilla
    /// <c>HUD.update()</c> reads from the item's own countdown closure, and the cooldown overlay
    /// the Beheaded manager paints for its hero (the same formula, read from player two's own
    /// skill objects).
    /// </summary>
    private static void RefreshP2Slots(Hero hero)
    {
        // The bag can change without any hook the mod sees (a transform, a reload); re-map when
        // the item set does not match what the slots were built from.
        var signature = ReadP2BagSignature(hero);
        if (signature != _p2BagSignature)
        {
            MapP2Items(hero);
            _p2BagSignature = signature;
            ResizeP2Slots();
        }

        for (var i = 0; i < _p2Weapons.Length; i++)
        {
            RefreshSlotCount(_p2Weapons[i]);
        }

        for (var i = 0; i < _p2Powers.Length; i++)
        {
            RefreshSlotCount(_p2Powers[i]);
        }

        // Cooldown overlay for the active skills.
        for (var i = 0; i < _p2Powers.Length; i++)
        {
            var slot = _p2Powers[i];
            if (slot == null)
            {
                continue;
            }

            var item = SlotItem(slot);
            if (item == null)
            {
                slot.setCooldown(0.0, 0.0);
                continue;
            }

            var active = hero.activeSkillsManager?.getActiveSkill(item);
            var skill = active?.skill;
            if (skill == null)
            {
                slot.setCooldown(0.0, 0.0);
                continue;
            }

            var coolDownF = skill.coolDownF;
            if (coolDownF <= 0.0)
            {
                slot.setCooldown(0.0, 0.0);
                continue;
            }

            var usesLeft = 0;
            try
            {
                usesLeft = active!.get_usesLeft();
            }
            catch
            {
                // Only charge-based skills answer this; a plain cooldown then shows.
            }

            var seconds = skill.framesToSec(coolDownF);
            var ratio = skill.coolDownMaxF > 0.0 ? coolDownF / skill.coolDownMaxF : 0.0;
            if (usesLeft == 0)
            {
                slot.setCooldown(seconds, ratio);
            }
            else
            {
                slot.setCooldown(0.0, 0.0);
            }
        }
    }

    /// <summary>Mirrors <c>HUD.update()</c>'s per-slot countdown read for one of the merged slots.</summary>
    private static void RefreshSlotCount(dc.ui.hud.Skill? slot)
    {
        if (slot == null)
        {
            return;
        }

        var item = SlotItem(slot);
        if (item == null)
        {
            return;
        }

        try
        {
            var countdown = item.getCountdown();
            if (countdown != null)
            {
                slot.setCount(countdown.i, countdown.max);
            }
            else
            {
                slot.disableCount();
            }
        }
        catch
        {
            // A countdown closure the item does not carry is normal; leave the slot as it is.
        }
    }

    /// <summary>The item a slot currently shows, or null. Never throws.</summary>
    private static InventItem? SlotItem(dc.ui.hud.Skill slot)
    {
        try
        {
            return slot.getItem();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Paints the merged flask icon from player two's own charge count — the same rule
    /// <c>HUD.setHealings</c> uses (the flask group's frame for the charge count, the special
    /// flask overriding it), because the game's own write path goes through
    /// <c>HUD.setHealings</c> and cannot say which hero asked.
    /// </summary>
    private static void PaintP2Flask(Hero hero)
    {
        var flask = _p2Flask;
        if (flask == null)
        {
            return;
        }

        var (cur, max) = ReadFlaskCharges(hero);
        if (cur == _p2FlaskCur && max == _p2FlaskMax)
        {
            return;
        }

        _p2FlaskCur = cur;
        _p2FlaskMax = max;

        try
        {
            var inventory = hero.inventory;
            if (inventory != null && inventory.hasItem("SpecialFlask".AsHaxeString()))
            {
                flask.setIcon(Assets.Class.getItem("SpecialFlask".AsHaxeString()));
                return;
            }

            var group = max switch
            {
                0 => null,
                1 => "flaskA",
                2 => "flaskB",
                3 => "flaskC",
                _ => "flaskD",
            };

            if (group == null)
            {
                flask.setIcon(null);
                return;
            }

            var ui = Assets.Class.ui;
            var key = group.AsHaxeString();
            if (!ui.groups.exists(key))
            {
                return;
            }

            var frames = ((dc.libs.heaps.slib.LibGroup)ui.groups.get(key)).frames;
            if (frames.length <= 0)
            {
                return;
            }

            var frame = System.Math.Clamp(cur, 0, frames.length - 1);
            flask.setIcon(ui.getTile(key, Ref<int>.From(ref frame), Ref<double>.Null, Ref<double>.Null, null));
        }
        catch (Exception ex)
        {
            LogVerbose($"[LocalCoop] could not paint the P2 flask icon: {ex.Message}");
        }
    }

    /// <summary>
    /// Paints player two's mutation icons — one <c>Icon</c> per <c>Perk</c> in its bag, dimmed
    /// while that mutation is on cooldown, exactly like the vanilla stock does for the global hero
    /// (which is why player two's were invisible: <c>HUD.perkStock</c> only ever walks
    /// <c>game.hero.inventory</c>). Rebuilt only when the perk set or a cooldown flag changes.
    /// </summary>
    private static void PaintP2Perks(Hero hero)
    {
        var stock = _p2PerkStock;
        if (stock == null)
        {
            return;
        }

        var signature = ReadP2PerkSignature(hero);
        if (signature == _p2PerkSignature)
        {
            return;
        }

        _p2PerkSignature = signature;

        try
        {
            stock.removeChildren();

            var items = hero.inventory?.items;
            if (items == null)
            {
                return;
            }

            var maxAlpha = 1.0;
            try
            {
                maxAlpha = Game.Class.ME?.hud?.get_maxAlpha() ?? 1.0;
            }
            catch
            {
                // Keep the default alpha.
            }

            for (var i = 0; i < items.length && i < 64; i++)
            {
                if (items.getDyn(i) is not InventItem item || item.kind is not InventItemKind.Perk)
                {
                    continue;
                }

                // The variant's payload is the item-kind string the icon factory wants; `Index`
                // is the C# enum tag, not the string.
                var kind = ((InventItemKind.Perk)item.kind).Param0;
                if (kind == null)
                {
                    continue;
                }

                var icon = dc.ui.icon.Icon.Class.createItemIcon(kind, stock);

                var onCooldown = false;
                try
                {
                    onCooldown = hero.hasPerkCooldownFor(null, kind);
                }
                catch
                {
                    // A perk the hero does not track: draw it at full alpha.
                }

                icon.alpha = onCooldown ? 0.25 : maxAlpha;
            }
        }
        catch (Exception ex)
        {
            LogVerbose($"[LocalCoop] could not paint the P2 mutation icons: {ex.Message}");
        }
    }

    /// <summary>
    /// A hash of player two's perk list plus their cooldown flags — the input to
    /// <see cref="PaintP2Perks"/>. Uses the item uids so no strings are built on the beat.
    /// </summary>
    private static int ReadP2PerkSignature(Hero hero)
    {
        try
        {
            var items = hero.inventory?.items;
            if (items == null)
            {
                return 0;
            }

            unchecked
            {
                var hash = 17;
                for (var i = 0; i < items.length && i < 64; i++)
                {
                    if (items.getDyn(i) is not InventItem item || item.kind is not InventItemKind.Perk)
                    {
                        continue;
                    }

                    hash = (hash * 31) + item.__uid;
                    try
                    {
                        var kind = ((InventItemKind.Perk)item.kind).Param0;
                        if (kind != null)
                        {
                            hash = (hash * 31) + (hero.hasPerkCooldownFor(null, kind) ? 1 : 0);
                        }
                    }
                    catch
                    {
                        // No cooldown answer: the uid alone still catches the list changing.
                    }
                }

                return hash;
            }
        }
        catch
        {
            return _p2PerkSignature;
        }
    }

    /// <summary>Paints the merged tier bar from player two's own scroll tiers.</summary>
    private static void PaintP2Tiers(Hero hero)
    {
        var tiers = _p2Tiers;
        if (tiers == null)
        {
            return;
        }

        var brutality = hero.brutalityTier;
        var tactic = hero.tacticTier;
        var survival = hero.survivalTier;

        if (brutality != _p2TierBrutality)
        {
            _p2TierBrutality = brutality;
            tiers.brutalityTier.setValue(brutality, false);
        }

        if (tactic != _p2TierTactic)
        {
            _p2TierTactic = tactic;
            tiers.tacticTier.setValue(tactic, false);
        }

        if (survival != _p2TierSurvival)
        {
            _p2TierSurvival = survival;
            tiers.survivalTier.setValue(survival, false);
        }
    }

    /// <summary>
    /// A cheap signature of the bag the slots were mapped from: the item count plus every item's
    /// posID and identity. Used only to notice "the bag changed and no hook told us", so it runs
    /// on the 0.1 s beat, not per frame.
    /// </summary>
    private static int ReadP2BagSignature(Hero hero)
    {
        try
        {
            var items = hero.inventory?.items;
            if (items == null)
            {
                return 0;
            }

            unchecked
            {
                var hash = items.length * 397;
                for (var i = 0; i < items.length && i < 64; i++)
                {
                    var item = items.getDyn(i) as InventItem;
                    if (item == null)
                    {
                        continue;
                    }

                    hash = (hash * 31) + item.posID;
                    try
                    {
                        hash = (hash * 31) + item.__uid;
                    }
                    catch
                    {
                        // Older proxy without the field: the posID alone still catches a swap.
                    }
                }

                return hash;
            }
        }
        catch
        {
            return _p2BagSignature;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // The redirects. Each one falls through to the original unless the session is on, player two
    // is the hero involved, and the merged row can take the write.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>HUD.initItems(hero)</c> — the slot build. For player two with its own bag the shared
    /// slots must not be repainted with player two's items (they are player one's), so the merged
    /// row's slots are re-mapped instead and the shared build is skipped. The slots themselves are
    /// NOT rebuilt here: they live in the vanilla icon row, which lays them out, and rebuilding
    /// them on every equipment change is what made the row twitch.
    /// </summary>
    private static void OnHudInitItemsForMerge(Hook_HUD.orig_initItems orig, HUD self, Hero hero)
    {
        if (Enabled && !SharedEquipment && _hero2 != null && !_hero2.destroyed
            && ReferenceEquals(hero, _hero2))
        {
            // Player two's bag is its own: the shared slots must NEVER be painted with it, because
            // the ids are numbered per bag — the original would put player two's weapon into
            // player one's first slot. So the original is skipped even when the merged slots are
            // not up yet (they are built on the spot below, or by the per-frame retry), and even
            // when the re-map throws: player one's slots then keep player one's items, which is
            // stale but correct, and player two's gear is carried by the text readouts.
            if (!_p2HudBroken)
            {
                try
                {
                    if (!_p2SlotsBuilt)
                    {
                        TryBuildP2Slots(self);
                    }

                    MapP2Items(hero);
                    ResizeP2Slots();
                }
                catch (Exception ex)
                {
                    LogWarn("[LocalCoop] P2 HUD merge could not re-map the slots: {Message}", ex.Message);
                }
            }

            return;
        }

        orig(self, hero);
    }

    /// <summary>
    /// <c>HeroActiveSkillsManager.hudGetSkillPower(idx)</c> — where a hero's skill manager finds
    /// its HUD slot. Player two's manager resolves to the merged row's slot, so its cooldown
    /// highlight, ammo closures and cooldown pop land on player two's own icons instead of player
    /// one's.
    /// </summary>
    private static dc.ui.hud.Skill? OnActiveHudGetSkillPower(
        dc.tool.hero.Hook_HeroActiveSkillsManager.orig_hudGetSkillPower orig,
        dc.tool.hero.HeroActiveSkillsManager self,
        int idx)
    {
        if (Enabled && !_p2HudBroken && idx >= 0 && idx < _p2Powers.Length && _p2Powers[idx] != null
            && ManagerBelongsToHero2(self))
        {
            return _p2Powers[idx];
        }

        return orig(self, idx);
    }

    /// <summary>Whether a skill manager is player two's.</summary>
    private static bool ManagerBelongsToHero2(dc.tool.hero.HeroActiveSkillsManager manager)
    {
        var hero2 = _hero2;
        if (hero2 == null || hero2.destroyed)
        {
            return false;
        }

        try
        {
            var own = hero2.activeSkillsManager;
            return own != null && ReferenceEquals(own, manager);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Whether a weapons manager is player two's.</summary>
    private static bool IsP2WeaponsManager(dc.tool.hero.HeroWeaponsManager manager)
    {
        var hero2 = _hero2;
        if (hero2 == null || hero2.destroyed)
        {
            return false;
        }

        try
        {
            var own = hero2.weaponsManager;
            return own != null && ReferenceEquals(own, manager);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Marks player two as the hero whose manager is about to write the shared HUD, for the
    /// duration of the call. The original always runs — the lock it records is gameplay state.
    /// </summary>
    private static void BeginP2HudWriter(bool isPlayerTwo)
    {
        if (isPlayerTwo)
        {
            _p2HudWriteHero = _hero2;
        }
    }

    private static void EndP2HudWriter(bool isPlayerTwo)
    {
        if (isPlayerTwo)
        {
            _p2HudWriteHero = null;
        }
    }

    /// <summary>
    /// Which hero is currently inside a manager method that writes the shared HUD, or null outside
    /// one. Set by the weapon/skill lock hooks so the HUD-level fade hooks can tell player two's
    /// lock from player one's — the HUD methods themselves carry no hero.
    /// </summary>
    private static Hero? _p2HudWriteHero;

    private static void OnWeaponsAddLockForMerge(
        dc.tool.hero.Hook_HeroWeaponsManager.orig_addWeaponLock orig,
        dc.tool.hero.HeroWeaponsManager self, dc.String str, Ref<int> id)
    {
        var mark = Enabled && !SharedEquipment && IsP2WeaponsManager(self);
        BeginP2HudWriter(mark);
        try
        {
            orig(self, str, id);
        }
        finally
        {
            EndP2HudWriter(mark);
        }
    }

    private static void OnWeaponsRemoveLockForMerge(
        dc.tool.hero.Hook_HeroWeaponsManager.orig_removeWeaponLock orig,
        dc.tool.hero.HeroWeaponsManager self, dc.String str, Ref<int> id)
    {
        var mark = Enabled && !SharedEquipment && IsP2WeaponsManager(self);
        BeginP2HudWriter(mark);
        try
        {
            orig(self, str, id);
        }
        finally
        {
            EndP2HudWriter(mark);
        }
    }

    private static void OnWeaponsApplyHudLockFadeForMerge(
        dc.tool.hero.Hook_HeroWeaponsManager.orig_applyHudLockFade orig,
        dc.tool.hero.HeroWeaponsManager self)
    {
        var mark = Enabled && !SharedEquipment && IsP2WeaponsManager(self);
        BeginP2HudWriter(mark);
        try
        {
            orig(self);
        }
        finally
        {
            EndP2HudWriter(mark);
        }
    }

    private static void OnSkillsAddLockForMerge(
        dc.tool.hero.Hook_HeroActiveSkillsManager.orig_addSkillLock orig,
        dc.tool.hero.HeroActiveSkillsManager self, dc.String str, Ref<int> id)
    {
        var mark = Enabled && !SharedEquipment && ManagerBelongsToHero2(self);
        BeginP2HudWriter(mark);
        try
        {
            orig(self, str, id);
        }
        finally
        {
            EndP2HudWriter(mark);
        }
    }

    private static void OnSkillsRemoveLockForMerge(
        dc.tool.hero.Hook_HeroActiveSkillsManager.orig_removeSkillLock orig,
        dc.tool.hero.HeroActiveSkillsManager self, dc.String str, Ref<int> id)
    {
        var mark = Enabled && !SharedEquipment && ManagerBelongsToHero2(self);
        BeginP2HudWriter(mark);
        try
        {
            orig(self, str, id);
        }
        finally
        {
            EndP2HudWriter(mark);
        }
    }

    private static void OnSkillsApplyHudLockFadeForMerge(
        dc.tool.hero.Hook_HeroActiveSkillsManager.orig_applyHudLockFade orig,
        dc.tool.hero.HeroActiveSkillsManager self)
    {
        var mark = Enabled && !SharedEquipment && ManagerBelongsToHero2(self);
        BeginP2HudWriter(mark);
        try
        {
            orig(self);
        }
        finally
        {
            EndP2HudWriter(mark);
        }
    }

    /// <summary>Whether the shared HUD write currently running belongs to player two.</summary>
    private static bool WritingAsPlayerTwo()
    {
        return Enabled && !SharedEquipment && _p2HudWriteHero != null
            && ReferenceEquals(_p2HudWriteHero, _hero2);
    }

    /// <summary>
    /// <c>HUD.updateAmmo(item)</c> — fired by the weapons manager when an item's ammo changes.
    /// Player one's slots are updated by the original; the merged slot holding the same item is
    /// refreshed here, so player two's ammo pips follow player two's weapon.
    /// </summary>
    private static void OnHudUpdateAmmoForMerge(Hook_HUD.orig_updateAmmo orig, HUD self, InventItem i)
    {
        orig(self, i);

        if (!Enabled || _p2HudBroken || i == null)
        {
            return;
        }

        try
        {
            foreach (var slot in _p2Weapons)
            {
                if (slot == null)
                {
                    continue;
                }

                var shown = SlotItem(slot);
                if (shown != null && ReferenceEquals(shown, i))
                {
                    slot.updateAmmo();
                }
            }
        }
        catch (Exception ex)
        {
            LogVerbose($"[LocalCoop] could not refresh the P2 ammo pips: {ex.Message}");
        }
    }

    /// <summary>
    /// <c>HUD.setWeaponFade(id, fade)</c> — a weapon the hero may not use is drawn faded. Player
    /// one's slot is faded by the original; the merged slot with the same id is faded here, so
    /// player two's forbidden weapon reads faded on player two's row too.
    /// </summary>
    private static void OnHudSetWeaponFadeForMerge(Hook_HUD.orig_setWeaponFade orig, HUD self, int id, bool fade)
    {
        if (WritingAsPlayerTwo())
        {
            // Player two's own weapon lock: the shared slots are player one's, so the original is
            // skipped and the merged slot is faded instead.
            FadeP2Slot(_p2Weapons, id, fade, "weapon");
            return;
        }

        orig(self, id, fade);

        if (!Enabled || _p2HudBroken || id < 0 || id >= _p2Weapons.Length)
        {
            return;
        }

        try
        {
            _p2Weapons[id]?.setFade(fade);
        }
        catch (Exception ex)
        {
            LogVerbose($"[LocalCoop] could not fade the P2 weapon slot: {ex.Message}");
        }
    }

    /// <summary>
    /// <c>HUD.setPowerFade(id, fade)</c> — the active-skill equivalent of
    /// <see cref="OnHudSetWeaponFadeForMerge"/>.
    /// </summary>
    private static void OnHudSetPowerFadeForMerge(Hook_HUD.orig_setPowerFade orig, HUD self, int id, bool fade)
    {
        if (WritingAsPlayerTwo())
        {
            // Player two's own skill lock: same rule as the weapon fade.
            FadeP2Slot(_p2Powers, id, fade, "skill");
            return;
        }

        orig(self, id, fade);

        if (!Enabled || _p2HudBroken || id < 0 || id >= _p2Powers.Length)
        {
            return;
        }

        try
        {
            _p2Powers[id]?.setFade(fade);
        }
        catch (Exception ex)
        {
            LogVerbose($"[LocalCoop] could not fade the P2 skill slot: {ex.Message}");
        }
    }

    /// <summary>Fades one of the merged slots by index; a missing slot is not an error.</summary>
    private static void FadeP2Slot(dc.ui.hud.Skill?[] slots, int id, bool fade, string what)
    {
        if (id < 0 || id >= slots.Length || slots[id] == null)
        {
            return;
        }

        try
        {
            slots[id]!.setFade(fade);
        }
        catch (Exception ex)
        {
            LogVerbose($"[LocalCoop] could not fade the P2 {what} slot: {ex.Message}");
        }
    }

    /// <summary>
    /// <c>HUD.setSkillFade(item, fade)</c> — the same fade keyed on the item rather than the slot
    /// id, so the merged slot holding that item is faded with it.
    /// </summary>
    private static void OnHudSetSkillFadeForMerge(Hook_HUD.orig_setSkillFade orig, HUD self, InventItem i, bool fade)
    {
        orig(self, i, fade);

        if (!Enabled || _p2HudBroken || i == null)
        {
            return;
        }

        try
        {
            foreach (var slot in _p2Weapons)
            {
                if (slot == null)
                {
                    continue;
                }

                var shown = SlotItem(slot);
                if (shown != null && ReferenceEquals(shown, i))
                {
                    slot.setFade(fade);
                }
            }

            foreach (var slot in _p2Powers)
            {
                if (slot == null)
                {
                    continue;
                }

                var shown = SlotItem(slot);
                if (shown != null && ReferenceEquals(shown, i))
                {
                    slot.setFade(fade);
                }
            }
        }
        catch (Exception ex)
        {
            LogVerbose($"[LocalCoop] could not fade the P2 slot: {ex.Message}");
        }
    }

    /// <summary>Whether an item sits in player two's own bag.</summary>
    private static bool ItemBelongsToHero2(InventItem? item)
    {
        if (item == null)
        {
            return false;
        }

        try
        {
            var items = _hero2?.inventory?.items;
            if (items == null)
            {
                return false;
            }

            for (var i = 0; i < items.length && i < 64; i++)
            {
                if (ReferenceEquals(items.getDyn(i), item))
                {
                    return true;
                }
            }
        }
        catch
        {
            // Best effort: a bag that cannot be read falls through to the original.
        }

        return false;
    }

    /// <summary>
    /// <c>HUD.showBuffFor(hero, tier)</c> — the "your weapon is weak in this biome" buff icons.
    /// It reads the given hero's bag but paints the shared slots, so player two's call would mark
    /// player one's weapons. Skipped for player two (the merged slots keep their own colours from
    /// <c>useItem</c>).
    /// </summary>
    private static void OnHudShowBuffForForMerge(Hook_HUD.orig_showBuffFor orig, HUD self, Hero hero, dc.String tier)
    {
        if (Enabled && !SharedEquipment && _hero2 != null && ReferenceEquals(hero, _hero2))
        {
            return;
        }

        orig(self, hero, tier);
    }

    /// <summary>
    /// <c>HUD.updateLifeSize(overrideMaxLife)</c> — two jobs. It freezes the vanilla bar's width
    /// outright (not just its growth curve), and it sizes player two's bar to match.
    /// <para>
    /// <b>The freeze.</b> The vanilla width is recomputed from the tier icons' room on every call,
    /// and the calls are not rare: <c>Hero.updateMaxLife</c> sizes it whenever the pool changes,
    /// and <c>Hero.hudUpdatePerkStock</c> (a mutation pickup) pairs <c>perkStock.set_minHeight(24)</c>
    /// with a full <c>HUD.onResize()</c> — the whole left column re-lays. The bar's box is part of
    /// that column's layout, so every width change moves the icon row above it. Worse, the width is
    /// INTERPOLATED between <c>oldState</c> and <c>curState</c> (<c>updateSize</c> reads the blended
    /// value), so even a one-off change animates the box for a few frames and drags the layout with
    /// it. Measuring once and then holding BOTH states stops all of that. The only thing allowed to
    /// re-measure is a change of the bar's height, which is the HUD size option (the height is
    /// <c>pixelScale * hudSize * 14</c>).
    /// </para>
    /// <para>
    /// <b>The growth curve.</b> The vanilla width also grows with max life (a log curve from the
    /// 100-life width up to ~350 px). Forcing the max-life parameter to 100 makes the curve
    /// evaluate to its base, so the box is fixed whatever the pool does.
    /// </para>
    /// </summary>
    private static void OnHudUpdateLifeSizeForMerge(Hook_HUD.orig_updateLifeSize orig, HUD self, double? overrideMaxLife)
    {
        orig(self, Enabled ? 100.0 : overrideMaxLife);

        if (!Enabled)
        {
            return;
        }

        try
        {
            var bar = self.heroLifeBar;
            if (bar?.curState == null)
            {
                return;
            }

            var height = bar.curState.outerHei;
            if (_vanillaBarWidth <= 0 || height != _vanillaBarHeight)
            {
                _vanillaBarHeight = height;
                _vanillaBarWidth = bar.curState.outerWid;
                Log($"[LocalCoop] vanilla life bar width frozen at {_vanillaBarWidth} "
                    + $"(height {_vanillaBarHeight}; only a HUD size change re-measures)");
            }

            if (bar.curState.outerWid != _vanillaBarWidth)
            {
                bar.setSize(_vanillaBarWidth, height);
            }

            // Both states: a stale oldState would animate the box towards the new width, and an
            // animating box re-lays the column.
            if (bar.oldState != null && bar.oldState.outerWid != _vanillaBarWidth)
            {
                bar.oldState.outerWid = _vanillaBarWidth;
            }

            // Player two's bar mirrors the vanilla one: the row it lives in is laid out by the
            // game, so a drifting box would move the row.
            SizeP2LifeBar(self);

            // A HUD resize re-sizes every vanilla slot; the merged ones need the same pass.
            ResizeP2Slots();
        }
        catch (Exception ex)
        {
            LogVerbose($"[LocalCoop] could not freeze the vanilla life bar: {ex.Message}");
        }
    }

    /// <summary>Forgets the frozen vanilla bar width (a run teardown, so the next one measures fresh).</summary>
    private static void ResetVanillaBarWidth()
    {
        _vanillaBarWidth = 0;
        _vanillaBarHeight = 0;
    }

    /// <summary>
    /// <c>HUD.updateSkillBgs()</c> — the one shared-slot writer that carries NO hero: it walks
    /// <c>game.hero.inventory</c> and maps every item onto <c>skillWeapons</c> / <c>skillPowers</c>
    /// by <c>posID</c>. The vanilla assumes one hero, so the ids agree; with two bags they do not,
    /// and the global hero is player two for the whole pickup window (see PickItemCore, which
    /// points it there so the pickup runs against player two's bag). A mutation pickup reaches it
    /// through <c>Hero.hudUpdatePerkStock</c> → <c>HUD.onResize()</c>, which is how player two's
    /// gear ended up painted into player one's slots. Skipped while the global hero is player two;
    /// player two's own slots are re-mapped by the initItems redirect.
    /// </summary>
    private static void OnHudUpdateSkillBgsForMerge(Hook_HUD.orig_updateSkillBgs orig, HUD self)
    {
        if (Enabled && !SharedEquipment && IsGlobalHeroPlayerTwo())
        {
            return;
        }

        orig(self);
    }

    /// <summary>Whether the global hero is player two (the pickup window, or a stray re-point).</summary>
    private static bool IsGlobalHeroPlayerTwo()
    {
        var hero2 = _hero2;
        if (hero2 == null || hero2.destroyed)
        {
            return false;
        }

        try
        {
            var global = Game.Class.ME?.hero;
            return global != null && ReferenceEquals(global, hero2);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// <c>HUD.onItemPick(item, x, y)</c> — the fly-to-slot pickup animation, and the scroll branch
    /// of it repaints the shared tier counters. Gated on the item being player two's OR the global
    /// hero being player two (the pickup window): either way the shared slots must not move.
    /// </summary>
    private static void OnHudOnItemPickForMerge(Hook_HUD.orig_onItemPick orig, HUD self, InventItem item, double fromX, double fromY)
    {
        if (Enabled && !SharedEquipment && (IsGlobalHeroPlayerTwo() || ItemBelongsToHero2(item)))
        {
            return;
        }

        orig(self, item, fromX, fromY);
    }
}
