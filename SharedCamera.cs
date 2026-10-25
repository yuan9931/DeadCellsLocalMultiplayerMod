using dc;
using dc.en;
using dc.level;
using dc.pr;
using dc.ui.hud;

namespace DeadCellsLocalMultiplayerMod;

/// <summary>
/// One window, both heroes visible: the game camera follows the midpoint between the two players
/// and zooms out just far enough to keep both on screen.
/// </summary>
/// <remarks>
/// This drives <see cref="Viewport"/> — the game's own camera — instead of scaling the world:
/// <list type="bullet">
/// <item><description><c>Viewport.goTo(x, y, immediate)</c> clears <c>tracked</c> and aims the camera
/// at a point, which is exactly what a two-player camera needs. The game only re-tracks a hero from
/// its <c>"ViewportTrack"</c> event, so nothing fights us frame by frame;</description></item>
/// <item><description><c>Viewport.updateSizes()</c> derives <c>wid</c>/<c>hei</c> from
/// <c>zoom</c> (<c>wid = windowWidth / (pixelScale * zoom)</c>). That is the whole reason the zoom
/// goes through the viewport and not through a scene node: the level culls, streams and refreshes
/// entities against those two fields, so scaling a node instead would leave holes where the game
/// still thinks nothing is visible;</description></item>
/// <item><description>the camera keeps the game's own smoothing, clamping to the level bounds and
/// shake, because we only set its target.</description></item>
/// </list>
/// </remarks>
internal static class SharedCamera
{
    /// <summary>Follow both heroes (zoom out) instead of only player one.</summary>
    internal static bool Enabled { get; set; } = true;

    /// <summary>Never zoom out further than this, so the game stays readable.</summary>
    internal static double MinZoom { get; set; } = 0.20;

    /// <summary>World pixels kept between a hero and the edge of the screen.</summary>
    internal static double Margin { get; set; } = 220;

    private static readonly double[] MinZoomPresets = [ 0.05, 0.1, 0.15, 0.20, 0.25, 0.30, 0.35, 0.40, 0.45, 0.50, 0.55, 0.60, 0.65, 0.70, 0.75, 0.80, 0.85, 0.90, 0.95, 1.00 ];

    /// <summary>Cycles how far the camera may zoom out: 5~100 %.</summary>
    internal static void CycleMinZoom()
    {
        var index = System.Array.IndexOf(MinZoomPresets, MinZoom);
        MinZoom = MinZoomPresets[(index + 1 + MinZoomPresets.Length) % MinZoomPresets.Length];
        LocalCoop.SaveSettings();
        Log($"[SharedCamera] zoom-out limit {MinZoom:P0}");
    }

    internal static Action<string>? Info;
    internal static Action<string, object?[]>? Warn;

    private static bool _installed;
    private static bool _driving;

    /// <summary>
    /// True while a cinematic is running and this class is deliberately not touching the viewport.
    /// Suppresses the per-frame zoom restore and the tracked-entity safety net, both of which would
    /// fight the cutscene's own camera script.
    /// </summary>
    private static bool _cineFrozen;

    private static double _zoom = 1.0;
    private static double _diagTimer;
    private static Viewport? _viewport;

    // Lifted out of the per-frame scope so the corner minimap can follow the heroes.
    // _midX/_midY are the world-pixel centre the main camera aims at (midpoint of the alive heroes).
    // The minimap does NOT mirror the main-camera zoom: it shows the heroes' vicinity and only
    // zooms out when the two heroes no longer fit, so it keeps its own cell-based midpoint/separation.
    private static double _midX;
    private static double _midY;

    // Cell-based midpoint and separation of the alive heroes (world pixels / 24), lifted out so the
    // minimap hook can centre on the heroes' vicinity in the minimap's own coordinate units.
    private static double _midCellX;
    private static double _midCellY;
    private static double _sepCellX;
    private static double _sepCellY;

    // Room ids whose fog this mod already punched on the current level (see RevealFogFor). The fog is
    // a texture with no "is this rect clear" query, so the only way to keep the texture writes rare is
    // to remember which rooms we lit. Cleared per level/run by ForgetRevealedRooms below.
    private static readonly HashSet<int> _fogRevealed = new();

    /// <summary>
    /// Forgets which rooms' fog was already punched, so a new level/run starts fully fogged and the
    /// heroes re-reveal as they explore.
    /// </summary>
    internal static void ForgetRevealedRooms()
    {
        _fogRevealed.Clear();
    }

    internal static void Install()
    {
        if (_installed)
        {
            return;
        }
        _installed = true;
        Hook_MiniMap.postUpdate += OnMiniMapPostUpdate;
        LogVerbose($"[SharedCamera] installed ({(Enabled ? "following both heroes" : "player one only")}, " +
            $"min zoom {MinZoom:P0}, margin {Margin:F0})");
    }

    // Switched by the same runtime flag as LocalCoop's logging (see LocalCoop.Logging).
    private static void Log(string message)
    {
        if (LocalCoop.Logging)
        {
            Info?.Invoke(message);
        }
    }

    private static void LogWarn(string message, params object?[] args)
    {
        if (LocalCoop.Logging)
        {
            Warn?.Invoke(message, args);
        }
    }

    /// <summary>Diagnostics only — see <see cref="LocalCoop.Verbose"/>.</summary>
    private static void LogVerbose(string message)
    {
        if (LocalCoop.Verbose)
        {
            Info?.Invoke(message);
        }
    }

    /// <summary>Called once per frame; cheap when the option is off.</summary>
    internal static void OnFrameUpdate(double dt)
    {
        var hero1 = LocalCoop.Hero1;
        var hero2 = LocalCoop.Hero2;

        // True when this frame aimed the camera itself (goTo), which clears Viewport.tracked. If we
        // do NOT aim it, the tracked entity has to be re-pointed before the frame ends — see
        // EnsureTracked for why.
        var aimedThisFrame = false;

        try
        {
            if (!Enabled || !LocalCoop.Enabled || hero1 == null || hero2 == null)
            {
                Release(hero1, hero2);
                return;
            }

            if (hero1.destroyed || hero2.destroyed || hero1._level == null || hero2._level != hero1._level)
            {
                // Not both on the same level: the single camera cannot show both, so leave the game's
                // own camera alone.
                Release(hero1, hero2);
                return;
            }

            var viewport = hero1._level.viewport;
            if (viewport == null)
            {
                return;
            }

            // A running cinematic owns the camera: the game's own cutscene code moves the viewport
            // (goTo / track / zoom) throughout the scene, and our per-frame aim fights it for every
            // one of those fields — the two writers alternate and the view flickers. Freeze
            // instead: on the transition into the cinematic hand the view off silently (no zoom
            // restore, no re-track — both would fight the cutscene too), then touch nothing until
            // the cinematic ends. The frozen view is exactly what the scene scripted.
            var cine = Game.Class.ME?.curCine;
            if (cine != null && !cine.destroyed)
            {
                if (_driving)
                {
                    _driving = false;
                    LogVerbose("[SharedCamera] cinematic running — camera handed to the cutscene");
                }

                _cineFrozen = true;
                _viewport = viewport;
                return;
            }

            _cineFrozen = false;
            _viewport = viewport;
            _driving = true;

            // Independent health: a downed hero is out of the fight, so follow only the alive
            // hero(es). With one hero alive the view locks onto it at full zoom; with both alive it
            // keeps the midpoint + zoom-out behaviour.
            //
            // Written out with plain locals rather than collected into Lists: this runs every frame
            // and there are at most two heroes, so the two lists (and their backing arrays) were
            // pure garbage — the kind of per-frame allocation that turns into stutter later on.
            double x1 = 0, y1 = 0, x2 = 0, y2 = 0;
            var alive1 = !LocalCoop.IsDown(hero1);
            var alive2 = !LocalCoop.IsDown(hero2);

            if (!alive1 && !alive2)
            {
                // Run is ending (all heroes down): leave the camera alone.
                Release(hero1, hero2);
                return;
            }

            var has1 = alive1 && TryWorldPosition(hero1, out x1, out y1);
            var has2 = alive2 && TryWorldPosition(hero2, out x2, out y2);
            if (!has1 && !has2)
            {
                // Somebody is alive but no position resolved (a transient level state): leave the
                // camera alone for this frame.
                return;
            }

            double midX;
            double midY;
            double separationX;
            double separationY;
            if (has1 && has2)
            {
                midX = (x1 + x2) * 0.5;
                midY = (y1 + y2) * 0.5;
                separationX = System.Math.Abs(x1 - x2);
                separationY = System.Math.Abs(y1 - y2);
            }
            else if (has1)
            {
                midX = x1;
                midY = y1;
                separationX = 0;
                separationY = 0;
            }
            else
            {
                midX = x2;
                midY = y2;
                separationX = 0;
                separationY = 0;
            }

            _midX = midX;
            _midY = midY;

            // Cell-based midpoint/separation for the minimap: world pixels / 24. The minimap shows the
            // heroes' vicinity and uses its own independent zoom (never the main-camera _zoom), so it
            // pans in the minimap's level-cell units rather than in world pixels.
            const double pixelsPerCell = 24.0;
            _midCellX = midX / pixelsPerCell;
            _midCellY = midY / pixelsPerCell;
            _sepCellX = separationX / pixelsPerCell;
            _sepCellY = separationY / pixelsPerCell;

            // wid = windowWidth / (pixelScale * zoom), so wid * zoom is the stage size in logical
            // pixels — constant across zoom levels, and exactly the number the fit has to satisfy:
            //   wid = (wid * zoom) / zoom  >=  separation + 2 * margin
            var stageW = viewport.wid * viewport.zoom;
            var stageH = viewport.hei * viewport.zoom;
            var targetZoom = _zoom;
            if (stageW > 0 && stageH > 0)
            {
                targetZoom = System.Math.Min(1.0, stageW / (separationX + Margin * 2));
                targetZoom = System.Math.Min(targetZoom, stageH / (separationY + Margin * 2));
                targetZoom = System.Math.Clamp(targetZoom, MinZoom, 1.0);
            }

            // Ease towards it, so walking apart does not snap the view.
            var step = System.Math.Min(1.0, System.Math.Max(0.0, dt) * 4.0);
            _zoom += (targetZoom - _zoom) * step;
            _zoom = System.Math.Clamp(_zoom, MinZoom, 1.0);

            // Only when it actually moved: writing the same zoom back and re-running the viewport's
            // size pass every frame was work with no effect (the heroes stand together most of the
            // time, so the eased zoom has already settled).
            if (System.Math.Abs(viewport.zoom - _zoom) > 1e-5)
            {
                viewport.zoom = _zoom;
                viewport.updateSizes();
            }

            // Aim at the midpoint of the alive heroes. goTo() clears the tracked entity, so the
            // game's camera stops chasing player one and keeps its own smoothing towards this point.
            // The hero whose height the aim point is raised by: player one whenever it is in the
            // fight (the two are the same height), otherwise the surviving player two.
            var aimHero = has1 ? hero1 : hero2;
            viewport.goTo(midX, midY - HeroHalfHeight(aimHero), null);
            aimedThisFrame = true;

            LogDiagnostics(dt, viewport, separationX, separationY, targetZoom);
        }
        catch (Exception ex)
        {
            LogWarn("[SharedCamera] frame failed, falling back to the normal camera: {Message}", ex.Message);
            Enabled = false;
            Release(hero1, hero2);
        }
        finally
        {
            // Runs on every path above, including the early returns that never aimed the camera.
            // While a cinematic owns the view, both helpers would fight the cutscene's own camera
            // script (a zoom restore and a re-track respectively), so they stay off.
            if (!_cineFrozen)
            {
                RestoreZoomIfIdle(aimedThisFrame);
                EnsureTracked(hero1, hero2);
            }
        }
    }

    /// <summary>
    /// The corner minimap follows the heroes independently of the main camera: it is centred on the
    /// two heroes' midpoint and uses the vanilla *minimum* zoom (the same floor the unmodified game
    /// uses), so it looks exactly like the original minimap — not an over-zoomed tiny area, not a
    /// forced whole-level fit. The vanilla minimap otherwise only ever follows player one. Both
    /// heroes reveal the unexplored map.
    /// </summary>
    /// <remarks>
    /// Runs from <see cref="Hook_MiniMap.postUpdate"/>. The minimap's pan (<c>vpX</c>/<c>vpY</c>) is in
    /// level *cells*, not world pixels, and its own zoom (<c>baseScale</c>) has a completely different
    /// valid range from the main camera's <c>Viewport.zoom</c> (<c>[MinZoom, 1]</c>) — the game clamps
    /// it to <c>[minScale, 3.5]</c>, where <c>minScale</c> is the scale at which the whole level fits the
    /// map window (capped at 0.5 for the corner map, 3.5 for the fullscreen one) — so the two zooms must
    /// never be mapped 1:1.
    /// <para>
    /// We feed the game's own update valid inputs and let its own clamp and container positioning run
    /// unchanged. The chosen <c>baseScale</c> is the vanilla corner-map resting zoom, <c>0.6</c>
    /// (<c>MiniMap.restore()</c> → <c>setSize(w, h, 0.6)</c>) — never a whole-level fit, which on any
    /// level wider than the tiny corner window collapses to a fraction of that (0.24 measured on a
    /// large level) and, because the pan clamp width
    /// (<c>innerWid - map.wid * baseScale * pixelScale</c>) is negative at such a scale, crushes the
    /// pan to the level centre instead of the heroes' midpoint. The pan (<c>vpX</c>/<c>vpY</c>) is the
    /// heroes' midpoint in cells — the same unit the game uses for its own pan clamp, so the pair stays
    /// centred. We null <c>followEntity</c> so the game's per-frame ease toward the tracked hero (which
    /// would pull the pan off the midpoint) is skipped; the hero blips are drawn from the heroes' own
    /// cell positions inside the map container, so they are unaffected. When the shared camera is not
    /// driving we restore the vanilla follow target instead.
    /// </para>
    /// <para>
    /// Fog of war: the unexplored areas are a texture (<c>MapFow</c>), and the only thing that lights
    /// them up is <see cref="MiniMap.revealRoom(Room)"/> — <c>Room.revealed</c> is a data flag that
    /// <c>MapPainter.getColorFor</c> never reads, so writing it and repainting changes nothing visible.
    /// Since we drive the minimap (and null <c>followEntity</c>), the game's own reveal path
    /// (<c>scrollThroughFocusPoints</c>) never runs, so <see cref="RevealFogFor"/> calls it for each
    /// hero's own room — either player exploring lights up the room, exactly like the vanilla reveal.
    /// </para>
    /// </remarks>
    private static void OnMiniMapPostUpdate(Hook_MiniMap.orig_postUpdate orig, MiniMap self)
    {
        // Remembered across the call: the minimap owns no reference to the run heroes, and the
        // per-frame camera pass already resolved them.
        var hero1 = LocalCoop.Hero1;
        var hero2 = LocalCoop.Hero2;
        try
        {
            if (!_driving || !Enabled || !LocalCoop.Enabled)
            {
                // Not driving: hand the minimap back to the game's own follow logic (player one).
                // followEntity was nulled while we were driving, so restore it from the hero tracker
                // before the game's update re-runs its ease toward the tracked hero.
                if (self.followEntity == null)
                {
                    self.followEntity = self.heroTracker?.e;
                }

                orig(self);
                return;
            }

            // Only the CORNER minimap is driven. The full-screen map (the one opened with the map
            // key) is the same MiniMap object with isFullscreen set, and it must stay vanilla: it is
            // a browse-the-whole-level screen the player pans and zooms themselves, so forcing our
            // midpoint pan and a fixed 0.6 scale onto it fights the player's own navigation. Hand it
            // straight back to the game and restore the follow target we null while driving.
            if (self.isFullscreen)
            {
                if (self.followEntity == null)
                {
                    self.followEntity = self.heroTracker?.e;
                }

                orig(self);
                return;
            }

            // Zoom: the vanilla corner minimap rests at baseScale 0.6 -- MiniMap.restore() calls
            // setSize(w, h, 0.6), and the game's per-frame clamp is [minScale, 3.5] where minScale is
            // the whole-level fit (capped at 0.5 for the corner map). 0.6 is therefore already the most
            // zoomed-out value the corner map ever shows, and we must NOT compute a whole-level fit
            // ourselves: min(0.6, fit) collapses to fit on any level wider than the tiny corner window
            // (0.24 on a large level). That is both a huge zoom-out AND a loss of centring, because
            // the pan clamp width (innerWid - map.wid * baseScale * pixelScale) is negative at that
            // scale, which crushes vpX/vpY to the level centre instead of the heroes' midpoint.
            // Keep the vanilla 0.6 and let the game's own clamp decide: on a small level it still
            // shows the whole level, exactly like the original minimap.
            const double next = 0.6; // vanilla corner-map resting zoom

            // Feed the game's own update valid inputs; let it clamp baseScale to [minScale, 3.5],
            // clamp the pan to the level, and position the map container. vpX/vpY are the heroes'
            // midpoint in level *cells* — the same unit LevelMap.wid/hei and the pan clamp use — so
            // the pair stays centred instead of being pulled to the level centre.
            self.followEntity = null;
            self.vpX = _midCellX;
            self.vpY = _midCellY;
            self.baseScale = next;

            // Fog of war. NOT the Room.revealed data flag: the fog is the MapFow texture, and the only
            // thing that ever punches a hole in it is MiniMap.revealRoom(Room) (a red rect drawn with
            // fow.createGraphics). Writing Room.revealed (LevelMap.revealRoomAt) and repainting with
            // forceRenderRooms() lights nothing up -- MapPainter.getColorFor never reads that flag, and
            // the repaint rebuilds the tile grid, not the fog. Call the game's own reveal for each
            // hero's room, so either player lights up its room like the vanilla reveal does.
            RevealFogFor(self, hero1, hero2);

            orig(self);
        }
        catch (Exception ex)
        {
            LogWarn("[SharedCamera] minimap mirror failed: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Punches the fog of war (the <c>MapFow</c> texture) for each hero's room, with the game's own
    /// <c>MiniMap.revealRoom(Room)</c> — the only thing that ever lights up unexplored areas.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The minimap hides two different "reveals", and only one of them is visible:
    /// <list type="bullet">
    /// <item><description><c>Room.revealed</c> / <c>LevelMap.revealRoomAt()</c> is a pure data flag
    /// (read by Explorer's Instinct, and reset per run) — <c>MapPainter.getColorFor</c> never reads
    /// it, so repainting with it changes nothing on screen;</description></item>
    /// <item><description><see cref="MiniMap.revealRoom(Room)"/> draws a red rect into the fow
    /// texture at <c>(r.x+1, r.y+1, r.wid-2, r.hei-2)</c> — that rect is what removes the fog.</description></item>
    /// </list>
    /// The vanilla game calls it from <c>scrollThroughFocusPoints</c>, i.e. whenever a room gets a
    /// focus point. While we drive the minimap we null <c>followEntity</c> and never let the game's
    /// own reveal run, so we must call it ourselves for *both* heroes' rooms — that is what makes
    /// either player's exploring light up the level.
    /// </para>
    /// <para>
    /// **Why the room must be tracked by us and not by <c>Room.revealed</c>:** that flag is written by
    /// <see cref="LocalCoop.OnFrameUpdate"/> for *both* heroes on every frame (it is the data flag the
    /// Explorer's Instinct needs), so it is already <c>true</c> by the time we get here and it cannot
    /// tell us "this room's fog is still on". The reveal is therefore remembered per room in
    /// <c>_fogRevealed</c> and redone whenever that set forgets it — one hash lookup per hero per
    /// frame, which is what the throttle below is for. A room the player leaves without the fog ever
    /// being punched (a spawn placed on the level edge, a level entry) is revealed again on the next
    /// pass, because <c>revealRoom</c> only ever *adds* fog holes — it is idempotent.
    /// </para>
    /// <para>
    /// The set is cleared whenever the run or the level changes (see <see cref="ResetRunState"/>), so a
    /// new run always starts fully fogged.
    /// </para>
    /// </remarks>
    private static void RevealFogFor(MiniMap self, Hero? hero1, Hero? hero2)
    {
        if (!Enabled || !LocalCoop.Enabled)
        {
            return;
        }

        var map = self.level?.map;
        if (map == null || self.fow == null)
        {
            return;
        }

        // Two explicit calls rather than `foreach (var hero in new[] { hero1, hero2 })`: that array
        // was allocated on every minimap update, i.e. once per frame, for a two-element loop.
        RevealFogForHero(self, map, hero1);
        RevealFogForHero(self, map, hero2);
    }

    /// <summary>Punches the fog for one hero's room, when that room was not revealed yet.</summary>
    private static void RevealFogForHero(MiniMap self, dc.level.LevelMap map, Hero? hero)
    {
        if (hero == null || LocalCoop.IsDown(hero))
        {
            return;
        }

        var level = hero._level;
        if (level == null || level.map != map)
        {
            return; // a different level than the one this minimap shows
        }

        if (hero.cx < 0 || hero.cy < 0)
        {
            return;
        }

        Room room;
        int roomId;
        try
        {
            room = map.getRoomAt(hero.cx, hero.cy);
            if (room == null)
            {
                return; // corridors and level edges have no room of their own
            }

            roomId = room.id;
        }
        catch (Exception ex)
        {
            LogWarn("[SharedCamera] minimap fog lookup failed: {Message}", ex.Message);
            return;
        }

        // Already punched for this room on this level? The fow is a texture, so there is no
        // cheap "is this rect already clear" test; remembering it here is the only way to keep
        // the texture writes rare. Ids are per level, so the set is reset per level.
        if (!_fogRevealed.Add(roomId))
        {
            return;
        }

        try
        {
            self.revealRoom(room);
            LogVerbose($"[SharedCamera] minimap fog revealed at cell {hero.cx},{hero.cy} (room {roomId})");
        }
        catch (Exception ex)
        {
            LogWarn("[SharedCamera] minimap fog reveal failed: {Message}", ex.Message);
        }
    }

    internal static void GetCameraState(out double midX, out double midY, out double zoom, out bool driving)
    {
        midX = _midX;
        midY = _midY;
        zoom = _zoom;
        driving = _driving;
    }

    /// <summary>
    /// Hands the camera back to the game (it follows a hero again instead of the shared midpoint).
    /// </summary>
    /// <remarks>
    /// This must never leave <c>Viewport.tracked</c> null. <c>Viewport.track()</c> sets it and
    /// <c>stopTracking()</c> clears it, and <c>Viewport.update</c> reads <c>tracked.collisionMode</c>
    /// with <b>no null guard</b> (Viewport.hx:476, reached only while the level has a boss) — so a
    /// null tracked entity crashes the game on the very next frame. Keeping a track always set is
    /// what makes the shared camera safe to switch off mid-run; <see cref="EnsureTracked"/> is the
    /// per-frame net around it.
    /// </remarks>
    private static void Release(Hero? hero1, Hero? hero2)
    {
        if (!_driving)
        {
            return;
        }

        _driving = false;
        RestoreZoom();
        _midX = 0;
        _midY = 0;
        try
        {
            var viewport = _viewport;
            if (viewport == null)
            {
                return;
            }

            // Prefer the global hero (the one every other system reads), then anything still usable.
            // Only fall back to stopTracking() when there is genuinely nothing left to follow.
            var target = FirstTrackable(hero1, hero2);
            if (target != null)
            {
                viewport.track(target, null);
                LogVerbose($"[SharedCamera] camera handed back to {HeroLabel(target)}");
                return;
            }

            viewport.stopTracking();
            LogVerbose("[SharedCamera] no hero left to follow; camera tracking stopped");
        }
        catch (Exception ex)
        {
            LogWarn("[SharedCamera] could not hand the camera back: {Message}", ex.Message);
        }
    }

    /// <summary>The first hero that can still be tracked, preferring player one (the global hero).</summary>
    private static Hero? FirstTrackable(Hero? hero1, Hero? hero2)
    {
        foreach (var h in new[] { hero1, hero2 })
        {
            if (IsTrackable(h))
            {
                return h;
            }
        }
        return null;
    }

    private static bool IsTrackable(Hero? hero)
    {
        try
        {
            return hero != null && !hero.destroyed;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Per-frame safety net: the game crashes with <c>Null access .collisionMode</c> whenever it
    /// reaches <c>Viewport.update</c> with a null <c>tracked</c> entity (Viewport.hx:476 dereferences
    /// <c>tracked.collisionMode</c> unguarded, and that branch is only taken while the level has a
    /// boss — which is why this surfaced specifically when TimeKeeper killed its target).
    /// <para>
    /// Two situations leave <c>tracked</c> null: <c>Viewport.goTo()</c> (which this class calls every
    /// frame it aims the camera, and whose very first act is <c>tracked = null</c>), and the frames
    /// in between, where either nothing aimed the camera or a hero had just been destroyed / sent to
    /// another level. So: whenever this frame did <b>not</b> aim the camera itself, make sure the
    /// viewport is still tracking something valid. This is deliberately per-frame and self-healing —
    /// the crash needs only one bad frame.
    /// </para>
    /// </summary>
    private static void EnsureTracked(Hero? hero1, Hero? hero2)
    {
        // While the shared camera is driving, the aim below re-establishes the target every frame;
        // nothing can go stale.
        if (_driving)
        {
            return;
        }

        var viewport = _viewport;
        if (viewport == null)
        {
            return;
        }

        try
        {
            // ReferenceEquals against the tracked entity: comparing raw Haxe proxies is not reliable.
            var tracked = viewport.tracked;
            if (tracked != null && !tracked.destroyed)
            {
                return;
            }

            var target = FirstTrackable(hero1, hero2);
            if (target == null)
            {
                return;
            }

            viewport.track(target, null);
            Log($"[SharedCamera] viewport had nothing to track; re-pointed at {HeroLabel(target)}");
        }
        catch (Exception ex)
        {
            // Never let the repair take the game down with it.
            LogWarn("[SharedCamera] could not restore the tracked entity: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Puts the game's zoom back when this mod is not driving the view. Skipped on the frames where
    /// <see cref="OnFrameUpdate"/> aimed the camera itself (it owns the zoom then) and once in
    /// <see cref="Release"/>, so a shared run never fights over one value.
    /// </summary>
    private static void RestoreZoomIfIdle(bool aimedThisFrame)
    {
        if (aimedThisFrame || _driving)
        {
            return;
        }

        RestoreZoom();
    }

    private static void RestoreZoom()
    {
        if (_zoom == 1.0)
        {
            return;
        }

        _zoom = 1.0;
        try
        {
            _viewport?.updateSizes();
        }
        catch
        {
            // The next frame's repair will try again.
        }
    }

    /// <summary>The hero's position in world pixels, as the game's own camera reads it.</summary>
    private static bool TryWorldPosition(Hero hero, out double x, out double y)
    {
        x = 0;
        y = 0;

        var spr = hero.spr;
        if (spr != null && spr.x != 0)
        {
            x = spr.x;
            y = spr.y;
            return true;
        }

        if (hero.cx < 0 || hero.cy < 0)
        {
            return false;
        }

        x = (hero.cx + hero.xr) * 24;
        y = (hero.cy + hero.yr) * 24;
        return true;
    }

    private static double HeroHalfHeight(Hero hero)
    {
        try
        {
            return hero.hei * 0.5;
        }
        catch
        {
            return 0;
        }
    }

    private static void LogDiagnostics(double dt, Viewport viewport, double sepX, double sepY, double targetZoom)
    {
        if (!LocalCoop.Verbose)
        {
            return;
        }

        _diagTimer += dt;
        if (_diagTimer < 3)
        {
            return;
        }
        _diagTimer = 0;

        // If the camera is not where we pointed it, the game is steering it as well: that shows up
        // here as a large error rather than as a mystery on screen.
        var errorX = System.Math.Abs(viewport.realX - viewport.tx);
        var errorY = System.Math.Abs(viewport.realY - viewport.ty);

        Log($"[SharedCamera] zoom {_zoom:F2} (target {targetZoom:F2}), heroes {sepX:F0}x{sepY:F0} apart, " +
            $"view {viewport.wid}x{viewport.hei} world px, tracked={viewport.tracked != null}, " +
            $"camera error ({errorX:F0},{errorY:F0}) px");
    }

    /// <summary>Short label for a hero, used only in diagnostics.</summary>
    private static string HeroLabel(Hero? hero)
    {
        if (hero == null)
        {
            return "none";
        }
        if (ReferenceEquals(hero, LocalCoop.Hero1))
        {
            return "P1";
        }
        if (ReferenceEquals(hero, LocalCoop.Hero2))
        {
            return "P2";
        }
        return hero.ToString() ?? "hero";
    }
}
