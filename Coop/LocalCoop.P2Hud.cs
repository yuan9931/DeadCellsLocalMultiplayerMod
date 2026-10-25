using dc.en;
using dc.h2d;
using dc.pr;
using dc.ui;
using HaxeProxy.Runtime;
using ModCore.Utilities;

namespace DeadCellsLocalMultiplayerMod;

/// <summary>
/// Part of <see cref="LocalCoop"/>: the two hero readouts, drawn as plain text lines on the HUD.
/// <para>
/// <b>Why text instead of the vanilla HUD.</b> The game has exactly one HUD, and every piece of it
/// describes the global hero — <c>updateLifeBar</c> writes player one's life, the item slots bind
/// their cooldown and ammo callbacks to whichever hero last ran
/// <c>HeroActiveSkillsManager.onEquippedItemsUpdated</c>, and the tier / infection / curse widgets
/// are written by the same single-hero events. With two heroes that is either wrong for one of them
/// or a race the mod cannot win. So the vanilla life bar, stat bar and item slots are hidden and
/// replaced by one text block per hero, each read from that hero's own objects.
/// </para>
/// <para>
/// <b>Why <c>makeText</c> and not a hand-built panel.</b> An earlier version built its own
/// <c>dc.h2d.Object</c> from C# and drew a <c>dc.h2d.Graphics</c> into it. That panel was created
/// with correct geometry (the layout dump proved it) yet never appeared on screen: objects
/// constructed in C# are not reliably registered with the Haxe heap, which is the same reason a
/// C#-built <c>ArrayObj</c> crashes the game on <c>push()</c>. <c>dc.Assets.makeText()</c> creates
/// the label on the Haxe side, so it shows up.
/// </para>
/// <para>
/// <b>Why absolute in the parent.</b> The parent is <c>heroLifeBar.parent</c>, the HUD <c>Flow</c>.
/// A plain flow child joins the layout: a taller sibling changes the row's height and the flow
/// re-centres everything else, which once shoved player one's whole HUD upwards. Marking each label
/// absolute takes it out of that layout, so it owns its x/y and cannot disturb the game's HUD — and
/// the two blocks are placed by hand so they never overlap each other.
/// </para>
/// <para>
/// Text always goes through <c>set_text()</c>, never <c>.text = …</c>: the Haxe proxy exposes Haxe
/// property setters as methods, so field assignment is silently ignored and the label keeps whatever
/// <c>makeText</c> was given.
/// </para>
/// </summary>
internal static partial class LocalCoop
{
    /// <summary>Lines per hero: health/flask, weapons, skills, curse/tiers, infection.</summary>
    private const int HeroHudLines = 5;

    /// <summary>Index 0 is player one, index 1 is player two.</summary>
    private const int HeroCount = 2;

    /// <summary>Per hero: the five labels, created on the Haxe side by <c>makeText</c>.</summary>
    private static readonly dc.ui.Text?[][] _heroHudLines =
    {
        new dc.ui.Text?[HeroHudLines],
        new dc.ui.Text?[HeroHudLines],
    };

    /// <summary>Per hero: the signature last painted, so a steady frame does no work.</summary>
    private static readonly string?[] _heroHudSignature = new string?[HeroCount];

    /// <summary>The game-owned object the lines are parented to (the HUD flow).</summary>
    private static dc.h2d.Object? _heroHudParent;

    /// <summary>The HUD the lines belong to, so a rebuilt HUD gets fresh labels.</summary>
    private static HUD? _heroHudOwner;

    private static bool _heroHudBroken;

    /// <summary>Vertical step between two lines, measured from the game's own font.</summary>
    private static double _heroLineStep = 19.0;

    private const double HeroHudTextScale = 0.85;

    private const int HeroHudColor = 0xF4F8FF;

    /// <summary>Gap below the readout's last line, so it does not touch the screen edge.</summary>
    private const double HeroHudBottomMargin = 8.0;

    /// <summary>Vanilla HUD objects we forced visible = false.</summary>
    private static readonly System.Collections.Generic.List<dc.h2d.Object> _hiddenVanillaHud = new();

    /// <summary>
    /// HUD objects the readouts fade out (alpha 0) instead of hiding, with the alpha they had before.
    /// Fading is for the pieces whose <b>layout slot</b> must stay: the mutation stock is a flow child
    /// and an invisible flow child is skipped by the layout pass, which would move the rest of the HUD.
    /// </summary>
    private static readonly System.Collections.Generic.Dictionary<dc.h2d.Object, double> _fadedHud = new();

    private static bool _vanillaHudHidden;

    /// <summary>
    /// True while the text readouts are actually drawn (independent equipment). Used to tear them down
    /// — and put the vanilla HUD back — the moment equipment becomes shared again.
    /// </summary>
    private static bool _heroHudActive;

    /// <summary>
    /// Creates / updates both heroes' readouts for this frame, and keeps the vanilla life bar, stat
    /// bar and item slots hidden. Called every frame from <see cref="OnFrameUpdate"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The text readouts are only for independent equipment.</b> While <see cref="SharedEquipment"/>
    /// is on, the two heroes hold the same gear, flask, scroll tiers and curse, so the vanilla
    /// icon HUD already describes both players exactly — replacing it with five text lines per hero
    /// is pure loss: the icons (item art, cooldown sweep, flask pips) are the game's own and read far
    /// faster. So in shared mode this whole pass is a no-op and the vanilla HUD stays on screen.
    /// </para>
    /// <para>
    /// The text readout exists for the case the vanilla HUD cannot express: two separate bags. Then
    /// player one's icons are only player one's, and player two needs its own block, so the vanilla
    /// HUD is hidden and both heroes get text.
    /// </para>
    /// <para>
    /// <b>Frame budget.</b> The two halves of this pass run at different rates on purpose:
    /// the <i>hiding</i> part re-asserts the game's own visibility every frame (a rebuilt item slot
    /// must not stay on screen for a single frame), while the <i>reading</i> part is throttled to
    /// <see cref="HeroHudInterval"/>. One snapshot walks two inventories, two skill managers, the
    /// main-skill list and the whole bag (the talisman scan) — a few hundred Haxe calls — and the
    /// only thing it feeds is text whose fastest-moving column is a cooldown printed to one decimal.
    /// Ten reads a second therefore look identical to sixty and cost a sixth of the interop.
    /// </para>
    /// </remarks>
    private static void UpdateHeroPanels(double dt)
    {
        if (!Enabled)
        {
            ResetHeroPanels();
            return;
        }

        // Shared equipment: the vanilla icon HUD describes both players, so leave it alone and draw
        // no text. Any readout left over from a previous independent-equipment run is torn down
        // here — before the clone runs, so the teardown cannot drop a clone this same frame is
        // about to rebuild.
        if (SharedEquipment)
        {
            if (_heroHudActive || _vanillaHudHidden)
            {
                ResetHeroPanels();
            }

            // Player two's own HUD widgets (Coop/LocalCoop.P2HudClone.cs): a second Flow with a
            // real life bar, tier bar, flask and weapon/skill slots, fed by the game's own write
            // paths. In this mode only the life bar can be wanted (the bag is shared, so the
            // vanilla slots already describe both heroes' gear).
            UpdateP2HudClone(dt);

            // …with one exception: with INDEPENDENT health the vanilla life bar describes only the
            // global hero (player one), so player two's own life gets a readout of its own. The
            // clone's bar is that readout when it is up; the drawn life line is the fallback.
            // Shared health converges both heroes onto one pool, so the bar already says both.
            if (!SharedHealth)
            {
                if (!P2CloneLifeActive)
                {
                    UpdateP2LifeLine();
                }
                else
                {
                    RemoveP2LifeLine();
                }
            }
            else
            {
                RemoveP2LifeLine();
            }

            return;
        }

        // Independent equipment: the clone carries player two's readout when it is up (its slots
        // replace player two's text block, see the paint loop below), and the text readouts stay
        // as the fallback for when it is not.
        UpdateP2HudClone(dt);

        // Independent equipment paints the full text readouts, life included, so the single shared-
        // mode line is not needed here.
        RemoveP2LifeLine();

        if (_heroHudBroken)
        {
            return;
        }

        try
        {
            var hud = Game.Class.ME?.hud;
            var lifebar = hud?.heroLifeBar;
            if (hud == null || lifebar == null)
            {
                return;
            }

            // The vanilla HUD is ALWAYS given back, merge or not. The redirects (see
            // Coop/LocalCoop.P2HudMerge.cs) keep player two's writes off the shared slots and bar,
            // so the vanilla icons and life bar describe player one correctly either way — and
            // hiding them when the merge is not up (as the text-readout era did) leaves the player
            // with NO icons at all, which is the worst possible fallback. Player two's readout is
            // then either the merged row or the text block below, never the vanilla HUD.
            RestoreVanillaHeroHud();
            RestoreHudEffects();

            EnsureHeroHud(hud, lifebar);
            AnchorHeroHud(lifebar);
            _heroHudActive = true;

            _heroHudTimer += dt;
            if (_heroHudTimer < HeroHudInterval)
            {
                return;
            }

            _heroHudTimer = 0;

            // Player two's cloned bag keeps the 99999 sentinel `Inventory.unserializeInit()` writes,
            // because only the managers ever replace it. Left alone it makes `equip()` unable to place
            // a picked-up item, so the item's `posID` stays -1 and `HUD.onItemPick` throws
            // `unexpected posId`. Re-asserted on the readout's own beat: the pickup path repairs it
            // itself right after the item lands (see LocalCoop.Drops.cs), so this is the safety net
            // for a rebuild or a reload, not the live repair.
            RepairInventoryCounts(_hero2, "frame");

            for (var i = 0; i < HeroCount; i++)
            {
                var hero = i == 0 ? _hero1 : _hero2;
                if (hero == null || hero.destroyed)
                {
                    // Clear that block rather than leave stale numbers behind.
                    PaintBlank(i);
                    continue;
                }

                if (P2CloneSlotsActive)
                {
                    // Both blocks stay blank while the merge is up: the vanilla HUD now carries
                    // both players' icons and bars, and the text blocks are anchored on the
                    // vanilla bar's row — where the merged bar row now sits, so painting anything
                    // there would draw over it.
                    if (_heroHudSignature[i] != "(merged)")
                    {
                        _heroHudSignature[i] = "(merged)";
                        PaintBlank(i);
                    }

                    continue;
                }

                var snapshot = ReadP2HudSnapshot(hero);
                if (snapshot.Signature != _heroHudSignature[i])
                {
                    _heroHudSignature[i] = snapshot.Signature;
                    PaintHeroHud(i, snapshot);
                }
            }
        }
        catch (Exception ex)
        {
            _heroHudBroken = true;
            RemoveHeroHud();
            LogWarn("[LocalCoop] hero readouts disabled after an error: {Message}", ex.Message);
        }
    }

    /// <summary>How often the readouts are re-read and repainted (0.1 s, the cooldown column's own step).</summary>
    private const double HeroHudInterval = 0.1;

    /// <summary>Time since the readouts were last read.</summary>
    private static double _heroHudTimer;

    /// <summary>Last geometry the readout block was placed for, so a steady HUD does no layout work.</summary>
    private static bool _heroHudAnchorValid;

    /// <summary>Whether the clone was up the last time the blocks were placed (it moves P2's block).</summary>
    private static bool _heroHudAnchorClone;
    private static bool _heroHudAnchorIsBar;
    private static double _heroHudAnchorParentW;
    private static double _heroHudAnchorOriginX;
    private static double _heroHudAnchorTopY;

    /// <summary>
    /// Hides every vanilla HUD piece that describes the single global hero: the life bar, the stat
    /// (tier) bar and the four item slots. Hidden rather than destroyed, so the game keeps writing to
    /// them harmlessly and the player can get the vanilla HUD back.
    /// <para>
    /// <b>This runs every frame, and must.</b> <c>Hero.hudInitItems()</c> calls
    /// <c>HUD.initItems(hero)</c>, which rebuilds the item slots — and it runs on every pickup, equipment
    /// change and scroll. Hiding once was therefore undone the first time anybody picked anything up:
    /// the slots, and with them the flask, came back. Re-asserting the hidden state each frame costs a
    /// handful of visibility writes and cannot be undone by the game.
    /// </para>
    /// <para>
    /// The original visibility is only recorded the first time an object is seen, so restoring later
    /// puts back what the game had, not "hidden".
    /// </para>
    /// </summary>
    private static void HideVanillaHeroHud(HUD hud)
    {
        Hide(hud.heroLifeBar);
        Hide(hud.tierBar);
        Hide(hud.skillPotion);
        Hide(hud.skillBackpack);

        // The mutation stock. It is the same single HUD for both heroes and it is rebuilt from the
        // global hero's bag, so with independent equipment it can only ever show player one's
        // mutations — the other player's are listed on their own readout instead.
        FadePerkStock(hud);

        HideArray(hud.skillWeapons);
        HideArray(hud.skillPowers);

        _vanillaHudHidden = _hiddenVanillaHud.Count > 0;

        // The stock is FADED, not hidden. `Flow.reflow()` skips an invisible child entirely
        // (`if (!obj.visible)` at several points in h2d/Flow.cs), so `visible = false` on this one
        // flow took it out of the HUD's layout and moved every other piece - which is what pushed the
        // text readouts off the life bar's row. `alpha = 0` keeps the child laid out and only makes it
        // draw nothing. Re-asserted every frame because the game writes its own value back
        // (`HUD.showRightAndPerks` does `perkStock.alpha = get_maxAlpha()`).
        void FadePerkStock(HUD owner)
        {
            try
            {
                var stock = owner.perkStock;
                if (stock == null)
                {
                    return;
                }

                if (!_fadedHud.TryGetValue(stock, out var original))
                {
                    original = stock.alpha;
                    _fadedHud[stock] = original;
                }

                stock.alpha = 0;
            }
            catch (Exception ex)
            {
                LogVerbose($"[LocalCoop] could not fade the mutation stock: {ex.Message}");
            }
        }

        void HideArray(dc.hl.types.ArrayObj? slots)
        {
            if (slots == null)
            {
                return;
            }

            for (var i = 0; i < slots.length && i < 8; i++)
            {
                Hide(slots.getDyn(i) as dc.h2d.Object);
            }
        }

        void Hide(dc.h2d.Object? obj)
        {
            if (obj == null)
            {
                return;
            }

            try
            {
                // Record the pre-hidden state once per object; the game clears it on a rebuild, so
                // only an object we have not seen (or one the game re-showed) needs recording.
                if (!_hiddenVanillaHud.Contains(obj))
                {
                    _hiddenVanillaHud.Add(obj);
                }

                if (obj.visible)
                {
                    obj.set_visible(false);
                }
            }
            catch (Exception ex)
            {
                LogWarn("[LocalCoop] could not hide a vanilla HUD element: {Message}", ex.Message);
            }
        }
    }

    /// <summary>Puts the vanilla HUD pieces back (the mod is off, or the session ended).</summary>
    private static void RestoreVanillaHeroHud()
    {
        if (!_vanillaHudHidden)
        {
            return;
        }

        foreach (var obj in _hiddenVanillaHud)
        {
            try
            {
                obj.set_visible(true);
            }
            catch
            {
                // The HUD may already be gone; nothing to restore then.
            }
        }

        _hiddenVanillaHud.Clear();

        foreach (var (obj, original) in _fadedHud)
        {
            try
            {
                obj.alpha = original;
            }
            catch
            {
                // The HUD may already be gone; nothing to restore then.
            }
        }

        _fadedHud.Clear();
        _vanillaHudHidden = false;
    }

    /// <summary>
    /// Hides the cooldown overlay visually, and hides the HUD's particle layer.
    /// <para>
    /// <b>The overlay is made transparent, never invisible — and that distinction was the bug.</b>
    /// <c>setCooldown</c> shows the overlay whenever the item is still recharging (<c>t != 0</c>) and
    /// hides it once it is ready; the visible→hidden transition calls <c>onCooldownReady()</c>, which
    /// plays the ready sound and draws a flash. An earlier version hid the overlay every frame, which
    /// fought that: the game re-showed it on the very next frame, so every frame produced another
    /// visible→hidden transition — and with two heroes holding items that oscillation never stopped,
    /// giving the repeating cooldown sound. Setting only <c>alpha = 0</c> leaves <c>visible</c>
    /// entirely to the game, so the transition happens once per cooldown, as intended, and the sound
    /// plays once.
    /// </para>
    /// <para>
    /// <b>Particles.</b> Everything the slots draw around a use or a pickup — the ready flash, the
    /// highlight ring, the pickup sparkle — is allocated as <c>HParticle</c>s on the HUD's own
    /// <c>ParticlePool</c> and rendered through two sprite batches, not through scene-graph children of
    /// the slots, so hiding the slots cannot hide them. Both batches are hidden and the live particles
    /// are blanked for as long as the readouts are in use. Sounds are untouched (they come from
    /// <c>Audio</c>), and world/combat effects use the level's own batches.
    /// </para>
    /// </summary>
    private static void SuppressHudEffects(HUD hud)
    {
        try
        {
            HideCooldownOverlay(hud.skillPotion);
            HideCooldownOverlay(hud.skillBackpack);
            HideCooldownOverlayArray(hud.skillWeapons);
            HideCooldownOverlayArray(hud.skillPowers);

            hud.pbatchFront?.set_visible(false);
            hud.pbatchBack?.set_visible(false);
            _hudBatchesHidden = true;

            // The batches above are what stop new particles from being *drawn*; this walk only
            // catches the ones already in flight when the readouts took over, and it costs a walk of
            // two live lists per call. Doing it every sixth frame (10 Hz at 60 fps) is enough for a
            // pool that is hidden anyway, and keeps the two interop reads off the per-frame path
            // while the pool is empty — which is the normal case.
            if (++_particleBlankTick >= 6)
            {
                _particleBlankTick = 0;
                BlankLiveParticles(hud);
            }
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not suppress the HUD effects: {Message}", ex.Message);
        }

        void HideCooldownOverlayArray(dc.hl.types.ArrayObj? slots)
        {
            if (slots == null)
            {
                return;
            }

            for (var i = 0; i < slots.length && i < 8; i++)
            {
                HideCooldownOverlay(slots.getDyn(i) as dc.ui.hud.Skill);
            }
        }

        static void HideCooldownOverlay(dc.ui.hud.Skill? skill)
        {
            if (skill == null)
            {
                return;
            }

            try
            {
                // Pinned visible AND transparent.
                //
                // `onCooldownReady()` — the ready sound and its flash — runs on the overlay's
                // visible→hidden transition, so pinning it visible removes the transition and with it
                // the repeated sound. Alpha 0 keeps it invisible.
                if (skill.cdOvl != null)
                {
                    skill.cdOvl.set_visible(true);
                    skill.cdOvl.alpha = 0;
                }
                if (skill.cdTxt != null)
                {
                    skill.cdTxt.set_visible(true);
                    skill.cdTxt.alpha = 0;
                }

                // The ready sound is guarded by `if (ii != null)` at the top of `onCooldownReady()`,
                // so clearing `ii` on these hidden slots is what actually silences it. This is the
                // case the vanilla game never has: one slot per item is fine, but with two heroes
                // sharing the single global HUD the slot is fed by both heroes' skill managers, and
                // the sound fires continuously. The slot is hidden and no longer draws anything, so
                // `getItem()` returning null costs nothing visible — the text readout carries the
                // item name and its remaining seconds.
                skill.ii = null;
            }
            catch
            {
                // Slot being torn down.
            }
        }
    }

    /// <summary>Whether the HUD's particle batches are currently hidden.</summary>
    private static bool _hudBatchesHidden;

    /// <summary>Frame counter behind the particle-blanking throttle (see SuppressHudEffects).</summary>
    private static int _particleBlankTick;

    /// <summary>
    /// Zeroes the alpha of every particle alive in the HUD's pool.
    /// <para>
    /// A particle already in flight keeps drawing through its batch, and there is no
    /// <c>Hook_HUD.createPart</c> to intercept new ones, so walking the two alive lists is the only
    /// way to stop the ones that are already on screen.
    /// </para>
    /// </summary>
    private static void BlankLiveParticles(HUD hud)
    {
        try
        {
            var pool = hud.ppool;
            if (pool == null)
            {
                return;
            }

            Blank(pool.busyList_DontKillEarly);
            Blank(pool.busyList_Killable);
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not blank the HUD particles: {Message}", ex.Message);
        }

        static void Blank(dc.libs.heaps._HParticle.HParticleList? list)
        {
            if (list == null)
            {
                return;
            }

            var node = list.head;
            var guard = 0;
            while (node != null && guard++ < 512)
            {
                try
                {
                    if (node.p != null)
                    {
                        node.p.maxAlpha = 0;
                    }
                }
                catch
                {
                    // Particle already recycled.
                }

                node = node.next;
            }
        }
    }

    /// <summary>Shows the HUD's particle batches again (the readouts are gone).</summary>
    private static void RestoreHudEffects()
    {
        if (!_hudBatchesHidden)
        {
            return;
        }

        try
        {
            var hud = Game.Class.ME?.hud;
            hud?.pbatchFront?.set_visible(true);
            hud?.pbatchBack?.set_visible(true);
        }
        catch
        {
            // The HUD may already be gone.
        }

        _hudBatchesHidden = false;

        // The cooldown overlays need no explicit restore: setCooldown drives their visibility every
        // frame, so the next tick after this puts them back to whatever the cooldown state says.
    }

    /// <summary>Creates the two blocks of labels the first time, or after the HUD was rebuilt.</summary>
    private static void EnsureHeroHud(HUD hud, dc.ui.hud.LifeBar lifebar)
    {
        if (ReferenceEquals(_heroHudOwner, hud) && _heroHudLines[0][0] != null)
        {
            return;
        }

        RemoveHeroHud();
        _heroHudOwner = hud;

        // Parent to a game-owned container (see the class summary: a C#-built h2d.Object never
        // showed up). The life bar's parent is the HUD flow that used to hold P1's pieces.
        var parent = (dc.h2d.Object?)lifebar.parent ?? lifebar;
        _heroHudParent = parent;

        for (var hero = 0; hero < HeroCount; hero++)
        {
            for (var line = 0; line < HeroHudLines; line++)
            {
                _heroHudLines[hero][line] = MakeLine(parent);
            }
        }

        if (parent is Flow flow)
        {
            try
            {
                foreach (var lines in _heroHudLines)
                {
                    foreach (var line in lines)
                    {
                        if (line != null)
                        {
                            flow.getProperties(line).set_isAbsolute(true);
                        }
                    }
                }
                flow.set_needReflow(true);
            }
            catch (Exception ex)
            {
                LogWarn("[LocalCoop] could not take the readouts out of the HUD layout: {Message}", ex.Message);
            }
        }

        for (var i = 0; i < HeroCount; i++)
        {
            _heroHudSignature[i] = null;
        }

        MeasureLineStep();
    }

    /// <summary>Creates one label through the game's own text factory.</summary>
    private static dc.ui.Text MakeLine(dc.h2d.Object parent)
    {
        var text = dc.Assets.Class.makeText(
            "".AsHaxeString(),
            dc.ui.Text.Class.COLORS.get("WO".AsHaxeString()),
            false,
            parent);
        text.textColor = HeroHudColor;
        text.customScale = HeroHudTextScale;
        text.onResize();
        return text;
    }

    /// <summary>
    /// The graphical P2 health bar, drawn while equipment is SHARED but health is INDEPENDENT.
    /// <para>
    /// In that combination the vanilla icon HUD stays on screen (it describes the one shared bag
    /// correctly), but its life bar is the global hero's — player one's — so player two's own life
    /// has no readout at all. This is the bar the mod already had in 0.1.11 (before the text blocks
    /// replaced it): a panel sibling of the life bar, drawn with <c>Graphics</c> as shell + dark
    /// inner + percent-coloured fill + top highlight, with the life/maxLife number centred on it.
    /// It sits to the RIGHT of P1's bar, vertically centred against it, and follows P1's geometry
    /// every frame. The full five-line readout blocks belong to independent equipment and are not
    /// drawn here.
    /// </para>
    /// <para>
    /// <b>Why the panel is a plain <c>h2d.Object</c> child of the life bar's parent.</b> The 0.1.11
    /// version established it: the parent already places P1, a FlowBox would overwrite our x/y, and
    /// the bar must not join the HUD flow's layout. The label inside is still <c>makeText</c> — the
    /// C#-constructed-object lesson applies to the text layer, not to a Graphics host node.
    /// </para>
    /// </summary>
    private static dc.h2d.Object? _p2BarPanel;

    /// <summary>The Graphics the bar itself is drawn with (a child of <see cref="_p2BarPanel"/>).</summary>
    private static Graphics? _p2Bar;

    /// <summary>The life/maxLife number centred on the bar.</summary>
    private static dc.ui.Text? _p2BarLabel;

    /// <summary>The HUD the bar belongs to, so a rebuilt HUD gets a fresh one.</summary>
    private static HUD? _p2BarOwner;

    /// <summary>Last drawn life / maxLife, so a steady frame does no layout work.</summary>
    private static int _p2BarLastLife = int.MinValue;
    private static int _p2BarLastMaxLife = int.MinValue;

    // Panel-local geometry (the number sits centred on the bar, like the vanilla life bar).
    // The shell border is 1px on every side — the 0.1.11 version's 6px read as far too thick.
    private const double BarShellX = 0.0;
    private const double BarShellY = 0.0;
    private const double BarShellW = 210.0;
    private const double BarShellH = 18.0;
    private const double BarInnerX = 1.0;
    private const double BarInnerY = 1.0;
    private const double BarInnerW = 208.0;
    private const double BarInnerH = 16.0;
    private const double BarP1Gap = 8.0;

    private static void UpdateP2LifeLine()
    {
        var hud = Game.Class.ME?.hud;
        var lifebar = hud?.heroLifeBar;
        var hero2 = _hero2;
        if (hud == null || lifebar == null || lifebar.curState == null
            || hero2 == null || hero2.destroyed)
        {
            RemoveP2LifeLine();
            return;
        }

        try
        {
            if (!ReferenceEquals(_p2BarOwner, hud) || _p2BarPanel == null)
            {
                RemoveP2LifeLine();
                _p2BarOwner = hud;

                // Sibling of heroLifeBar inside the same parent: the parent is already responsible
                // for placing P1, and a FlowBox would overwrite our x/y, so a plain h2d.Object is
                // the host. Fall back to the bar itself if it has no parent.
                var parent = (dc.h2d.Object?)lifebar.parent ?? lifebar;

                var panel = new dc.h2d.Object(null);
                parent.addChild(panel);

                _p2Bar = new Graphics(panel);
                _p2BarLabel = dc.Assets.Class.makeText(
                    "P2".AsHaxeString(),
                    dc.ui.Text.Class.COLORS.get("WO".AsHaxeString()),
                    false,
                    panel);
                _p2BarLabel.textColor = HeroHudColor;
                _p2BarLabel.customScale = 0.6;
                _p2BarLabel.onResize();

                _p2BarPanel = panel;
            }

            var panelObj = _p2BarPanel;
            if (panelObj == null)
            {
                return;
            }

            // (Re)anchor to P1 each frame: right of P1's bar, vertically centred against it. If the
            // panel is parented to the life bar itself (the fallback), its origin is relative to
            // heroLifeBar, so skip the bar's own x/y.
            var parentIsLifebar = ReferenceEquals(panelObj.parent, lifebar);
            var p1x = parentIsLifebar ? 0.0 : lifebar.x;
            var p1y = parentIsLifebar ? 0.0 : lifebar.y;
            var p1w = lifebar.curState.outerWid;
            var p1h = lifebar.curState.outerHei;

            panelObj.x = p1x + p1w + BarP1Gap;
            panelObj.y = p1y + (p1h - BarShellH) * 0.5;

            var maxLife = hero2.maxLife > 0 ? hero2.maxLife : 1;
            // A downed hero is held at DownedLife (1) so the boss AI stays happy, but the bar must
            // read EMPTY for the player — report 0 while down, and clamp overdamage to empty too.
            var life = IsDown(hero2) ? 0 : System.Math.Max(0, hero2.life);
            var ratio = life / (double)maxLife;
            if (ratio < 0)
            {
                ratio = 0;
            }
            else if (ratio > 1)
            {
                ratio = 1;
            }
            var percent = (int)System.Math.Round(ratio * 100.0);

            // Draw the bar in panel-local coordinates.
            var g = _p2Bar;
            if (g != null)
            {
                g.clear();
                double a = 1.0;

                // Shell.
                int shell = 0xC98A4B;
                g.beginFill(Ref<int>.From(ref shell), Ref<double>.From(ref a));
                g.drawRect(BarShellX, BarShellY, BarShellW, BarShellH);
                g.endFill();

                // Dark inner bar.
                int panelColor = 0x14161F;
                g.beginFill(Ref<int>.From(ref panelColor), Ref<double>.From(ref a));
                g.drawRect(BarInnerX, BarInnerY, BarInnerW, BarInnerH);
                g.endFill();

                // HP fill + top highlight, coloured by percent.
                var fillW = BarInnerW * ratio;
                if (fillW > 0)
                {
                    int fill = percent <= 25 ? 0xC94040 : percent <= 50 ? 0xD89036 : 0x4CBB5E;
                    g.beginFill(Ref<int>.From(ref fill), Ref<double>.From(ref a));
                    g.drawRect(BarInnerX, BarInnerY, fillW, BarInnerH);
                    g.endFill();

                    int hi = percent <= 25 ? 0xE87B6E : percent <= 50 ? 0xF0BC6E : 0x7FDD82;
                    g.beginFill(Ref<int>.From(ref hi), Ref<double>.From(ref a));
                    g.drawRect(BarInnerX, BarInnerY, fillW, 2);
                    g.endFill();
                }
            }

            // Number centred on the bar, only re-laid-out when the value changes. set_text, never
            // `.text = …` — the proxy exposes Haxe property setters as methods only.
            if (_p2BarLabel != null && (life != _p2BarLastLife || maxLife != _p2BarLastMaxLife))
            {
                _p2BarLastLife = life;
                _p2BarLastMaxLife = maxLife;
                var label = $"P2  {life} / {maxLife}";
                _p2BarLabel.set_text(label.AsHaxeString());
                _p2BarLabel.onResize();

                double tw;
                try
                {
                    tw = _p2BarLabel.textWidth * _p2BarLabel.scaleX;
                }
                catch
                {
                    tw = 0;
                }
                if (tw <= 0)
                {
                    tw = label.Length * 10.0;
                }
                _p2BarLabel.x = System.Math.Max(BarInnerX + 2, BarInnerX + (BarInnerW - tw) * 0.5);
                _p2BarLabel.y = BarInnerY + 1;
            }
        }
        catch (Exception ex)
        {
            LogVerbose($"[LocalCoop] could not draw the P2 life bar: {ex.Message}");
        }
    }

    /// <summary>Drops the shared-equipment life bar (mode changed, or the HUD went away).</summary>
    private static void RemoveP2LifeLine()
    {
        var panel = _p2BarPanel;
        if (panel != null)
        {
            try
            {
                panel.parent?.removeChild(panel);
            }
            catch (Exception ex)
            {
                LogWarn("[LocalCoop] could not remove the P2 life bar: {Message}", ex.Message);
            }
        }

        _p2BarPanel = null;
        _p2Bar = null;
        _p2BarLabel = null;
        _p2BarOwner = null;
        _p2BarLastLife = int.MinValue;
        _p2BarLastMaxLife = int.MinValue;
    }

    /// <summary>
    /// The y (in the labels' parent space) of the life bar's top edge — the row the readout is
    /// anchored to.
    /// <para>
    /// The bar is hidden but never moved, so its geometry still describes the bottom HUD row. Unlike
    /// the flow's <c>calculatedHeight</c> (content height, mistaken for screen height once and it
    /// pushed the block off screen), <c>lifebar.y</c> is a real position in the same space the labels
    /// live in.
    /// </para>
    /// </summary>
    private static double ReadBottomAnchorY(dc.ui.hud.LifeBar lifebar, bool parentIsBar)
    {
        if (parentIsBar)
        {
            // The parent is the bar itself: its own top edge is y = 0 in its space.
            return 0.0;
        }

        try
        {
            return lifebar.y;
        }
        catch
        {
            return 0.0;
        }
    }

    /// <summary>
    /// Places the two blocks side by side along the bottom of the screen: player one's on the left
    /// half, player two's on the right, each <c>HeroHudLines</c> lines tall.
    /// </summary>
    private static void AnchorHeroHud(dc.ui.hud.LifeBar lifebar)
    {
        if (_heroHudParent == null || _heroHudLines[0][0] == null)
        {
            return;
        }

        var parentIsBar = ReferenceEquals(_heroHudParent, lifebar);

        // Container size. `calculatedHeight` is the flow's *content* height, not the screen, so it
        // must not be used as a screen bound — doing that pushed the block off the bottom of the
        // screen and made the readout disappear entirely. Width is fine to read from the flow; the
        // vertical anchor comes from the life bar's own position, which is honest HUD geometry and
        // stays correct because the bar is only hidden, never moved.
        var parentW = 0.0;
        try
        {
            if (_heroHudParent is Flow flow)
            {
                parentW = flow.calculatedWidth;
            }
        }
        catch
        {
            // Unlaid-out HUD.
        }

        var originX = parentIsBar ? 0.0 : lifebar.x;

        if (parentW <= 0)
        {
            parentW = originX + System.Math.Max(320.0, ReadBarWidth(lifebar)) + 640.0;
        }
        if (parentW <= 0)
        {
            parentW = 1920.0;
        }

        var halfW = parentW * 0.5;
        var blockW = System.Math.Max(320.0, halfW - 16.0);

        // Anchored on the life bar's own band — the readout replaces the bottom HUD row, so it has to
        // sit where that row is, not float above it.
        //
        // TOP-aligned against the bar: the first line starts on the bar's top edge and the block grows
        // downward over the (hidden) HUD row. Measured in game, not assumed: the bar sits at the very
        // TOP of the HUD flow (`bar.y = 0`, flow absY 590, bar 28px tall) and the block is ~122px, so
        // both earlier recipes floated it — centring pushed `topY` to −47, bottom-aligning pushed it
        // to −94, and either way the last line ended at the bar's top edge, i.e. the whole block sat
        // above the bar. Growing downward covers the bar's row and still fits the 720px screen.
        var anchorY = ReadBottomAnchorY(lifebar, parentIsBar);
        var topY = anchorY;

        // The geometry only changes when the HUD is rebuilt or the window is resized, but the
        // placement below writes two properties and a max width on all ten labels — per frame, in
        // the mode that has the readouts at all. Absolute children keep their own x/y, so when the
        // computed geometry is the same as last time the labels are already where they belong.
        // With the clone up, player one's block is blank and player two's extras move to the
        // left half — the clone's own column has the right half.
        var cloneUp = P2CloneSlotsActive;
        if (_heroHudAnchorValid
            && _heroHudAnchorIsBar == parentIsBar
            && _heroHudAnchorParentW == parentW
            && _heroHudAnchorOriginX == originX
            && _heroHudAnchorTopY == topY
            && _heroHudAnchorClone == cloneUp)
        {
            return;
        }

        _heroHudAnchorValid = true;
        _heroHudAnchorIsBar = parentIsBar;
        _heroHudAnchorParentW = parentW;
        _heroHudAnchorOriginX = originX;
        _heroHudAnchorTopY = topY;
        _heroHudAnchorClone = cloneUp;

        // Player one's block is blank while the merged HUD is up (the vanilla HUD, which now
        // carries both players' icons and bars, says everything the text block would); player
        // two's extras-only block keeps the right half it always had.
        PlaceBlock(0, originX + 8.0);
        PlaceBlock(1, originX + halfW + 8.0);

        void PlaceBlock(int hero, double x)
        {
            for (var line = 0; line < HeroHudLines; line++)
            {
                var label = _heroHudLines[hero][line];
                if (label == null)
                {
                    continue;
                }

                label.x = x;
                label.y = topY + line * _heroLineStep;
                label.set_maxWidth(blockW);
            }
        }
    }

    /// <summary>The vanilla life bar's width, which stands in for the HUD block's width.</summary>
    private static double ReadBarWidth(dc.ui.hud.LifeBar lifebar)
    {
        try
        {
            if (lifebar.curState != null)
            {
                return lifebar.curState.outerWid;
            }
        }
        catch
        {
            // Unlaid-out HUD.
        }

        return 318.0;
    }


    /// <summary>Fills one hero's five lines from its snapshot.</summary>
    private static void PaintHeroHud(int index, P2HudSnapshot snapshot)
    {
        var who = index == 0 ? "P1" : "P2";
        var life = System.Math.Max(0, snapshot.Life);
        var maxLife = System.Math.Max(1, snapshot.MaxLife);

        SetLine(index, 0, $"{who}  {life}/{maxLife}   F{snapshot.FlaskCur}/{snapshot.FlaskMax}");
        SetLine(index, 1, $"W  {SlotLabel(snapshot.Weapon0)}   {SlotLabel(snapshot.Weapon1)}");
        SetLine(index, 2, $"S  {Detail(snapshot.Skill0)}   {Detail(snapshot.Skill1)}");
        SetLine(index, 3, $"CURSE {snapshot.Curse}   B{snapshot.Brutality} T{snapshot.Tactic} S{snapshot.Survival}");
        SetLine(index, 4, $"INF {snapshot.InfectionMax}   tier {snapshot.InfectionTier}/{snapshot.InfectionTierCount}   RING {snapshot.Talisman}");
    }

    /// <summary>Empties one hero's block (that hero is gone for this frame).</summary>
    private static void PaintBlank(int index)
    {
        if (_heroHudSignature[index] == null)
        {
            return; // already blank
        }

        _heroHudSignature[index] = null;
        for (var line = 0; line < HeroHudLines; line++)
        {
            SetLine(index, line, "");
        }
    }

    /// <summary>One weapon slot as a compact label, or "-".</summary>
    private static string SlotLabel(P2SlotInfo slot) => slot.HasItem ? Trim(slot.Label, 30) : "-";

    /// <summary>A skill slot as name plus its numbers, cooldown included; "-" when empty.</summary>
    private static string Detail(P2SlotInfo slot)
    {
        if (!slot.HasItem)
        {
            return "-";
        }

        var detail = slot.Detail;
        var name = Trim(slot.Name, 18);
        return detail.Length > 0 ? $"{name} {detail}" : name;
    }

    private static string Trim(string value, int max) =>
        value.Length <= max ? value : value[..System.Math.Max(1, max - 1)] + "+";

    private static void SetLine(int hero, int index, string value)
    {
        if (hero < 0 || hero >= HeroCount || index < 0 || index >= HeroHudLines)
        {
            return;
        }

        var line = _heroHudLines[hero][index];
        if (line == null)
        {
            return;
        }

        ApplyText(line, value);
    }

    /// <summary>
    /// Measures the game's UI font by putting a probe string through a label and reading the
    /// rendered height, then uses that as the step between lines. Measured rather than assumed, so
    /// the lines cannot overlap whatever that font turns out to be.
    /// </summary>
    private static void MeasureLineStep()
    {
        try
        {
            var probe = _heroHudLines[0][1] ?? _heroHudLines[0][0];
            if (probe == null)
            {
                return;
            }

            ApplyText(probe, "Ag");
            var height = probe.textHeight * probe.scaleY;
            if (height > 0)
            {
                _heroLineStep = height * 1.2;
            }
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] hero readouts could not measure their font: {Message}", ex.Message);
        }
    }

    private static bool _heroHudLogged;

    private static void LogHeroHudOnce()
    {
        if (_heroHudLogged || !Logging)
        {
            return;
        }

        _heroHudLogged = true;
        Log($"[LocalCoop] hero readouts: {HeroCount} blocks x {HeroHudLines} lines, step " +
            $"{_heroLineStep:0.0}px, parent={(ReferenceEquals(_heroHudParent, Game.Class.ME?.hud?.heroLifeBar) ? "lifeBar" : "flow")}");
    }

    /// <summary>Replaces a label's text; <c>set_text</c> is the only way the proxy accepts it.</summary>
    private static void ApplyText(dc.ui.Text label, string value)
    {
        label.set_text(value.AsHaxeString());
        label.onResize();
    }

    /// <summary>Removes both blocks' labels (run end, HUD rebuild, mod disabled).</summary>
    private static void RemoveHeroHud()
    {
        foreach (var lines in _heroHudLines)
        {
            foreach (var label in lines)
            {
                if (label == null)
                {
                    continue;
                }

                try
                {
                    label.remove();
                }
                catch (Exception ex)
                {
                    LogWarn("[LocalCoop] could not remove a hero readout line: {Message}", ex.Message);
                }
            }
        }

        for (var hero = 0; hero < HeroCount; hero++)
        {
            for (var line = 0; line < HeroHudLines; line++)
            {
                _heroHudLines[hero][line] = null;
            }
            _heroHudSignature[hero] = null;
        }

        // Fresh labels start wherever the layout puts them, so the placement cache must be dropped.
        _heroHudAnchorValid = false;

        _heroHudParent = null;
        _heroHudOwner = null;
        _heroHudLogged = false;

        // The shared-mode life line hangs off the same HUD; drop it with the rest so a rebuilt HUD
        // or a teardown never leaves it behind.
        RemoveP2LifeLine();
    }

    /// <summary>
    /// Drops the readouts and the error latch, and puts the vanilla HUD back. Called from the
    /// session teardown and the mod being disabled.
    /// </summary>
    private static void ResetHeroPanels()
    {
        RemoveHeroHud();
        RestoreVanillaHeroHud();
        RestoreHudEffects();
        // Player two's merged widgets and the frozen vanilla bar width belong to the run that is
        // going away; a rebuilt HUD rebuilds the widgets with it (see RebuildP2Widgets).
        DestroyP2Widgets();
        ResetVanillaBarWidth();
        _heroHudActive = false;
        _heroHudBroken = false;
        // Paint on the very next frame: a freshly built readout is empty, and waiting out the
        // throttle would leave a blank block on screen.
        _heroHudTimer = HeroHudInterval;
    }
}
