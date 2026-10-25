using dc;
using ModCore.Utilities;

namespace DeadCellsLocalMultiplayerMod;

/// <summary>
/// Part of <see cref="LocalCoop"/>: the PvP score panel and the winner banner.
/// <para>
/// <b>Same construction rule as the hero readouts, and for the same reason.</b> Every label comes
/// from the game's own <c>Assets.makeText</c> factory and is parented to a game-owned object; a
/// <c>dc.h2d.Object</c> built from C# is not reliably registered in the Haxe heap, so it measures
/// correctly and never draws. The parent is the HUD's flow (the life bar's parent) and each label is
/// marked absolute inside it, otherwise it joins the layout and shoves the rest of the HUD around.
/// Text is always written through <c>set_text()</c>: the proxy exposes the Haxe setter as a method
/// and field assignment is silently ignored. Teardown goes through <c>label.remove()</c>, the same
/// way the hero readouts remove theirs, so the labels never outlive the HUD they were built into.
/// </para>
/// <para>
/// The panel is the score and the round status at the top centre of the screen, plus a banner line
/// under them for the winner. It exists only while a match is running: in co-op the vanilla HUD and
/// the hero readouts are left exactly as they were.
/// </para>
/// </summary>
internal static partial class LocalCoop
{
    private static class PvpHud
    {
        // textColor is an int on the proxy, so these are int literals, not uint.
        private const int PanelColor = 0xFFD166;
        private const int BannerColor = 0x7CF765;
        private const int MatchStartColor = 0xFFFFFF;
        private const double PanelScale = 1.1;

        private static dc.ui.Text? _score;
        private static dc.ui.Text? _round;
        private static dc.ui.Text? _banner;
        private static dc.ui.Text? _matchStart;
        private static dc.ui.HUD? _owner;

        /// <summary>The game-owned object the labels hang off (the HUD root).</summary>
        private static dc.h2d.Object? _parent;
        private static bool _broken;
        private static string _lastScore = string.Empty;
        private static string _lastRound = string.Empty;
        private static string _lastBanner = string.Empty;
        private static double _matchStartLeft;

        /// <summary>Called every frame while a match is on. Creates the labels on first use.</summary>
        internal static void Update()
        {
            if (_broken)
            {
                return;
            }

            try
            {
                var hud = dc.pr.Game.Class.ME?.hud;
                var lifebar = hud?.heroLifeBar;
                if (hud == null || lifebar == null)
                {
                    return;
                }

                Ensure(hud, lifebar);
                Paint();
                TickMatchStart();
            }
            catch (Exception ex)
            {
                _broken = true;
                Remove();
                LogWarn("[PvP] score panel disabled after an error: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// Builds the three labels once per HUD.
        /// <para>
        /// <b>They hang off the HUD's own root, not off the life bar's parent flow.</b> Every HUD flow
        /// is a child of <c>HUD.root</c> (a full-screen <c>Layers</c> — the game builds them as
        /// <c>new Flow(root)</c> in <c>dc/ui/_HUD.cs:205</c>), and that root's origin is the screen's
        /// (0,0). An h2d <c>y</c> is <b>parent-local</b>, so the same number on a flow that itself sits
        /// at the bottom of the screen lands ~590px lower — which is why the panel overlapped the HUD
        /// twice before this change. Parenting to the root makes the constants below plain screen
        /// coordinates, the same way the pause window's picker overlay does with its own full-screen
        /// background.
        /// </para>
        /// <para>
        /// The panel sits in the <b>top-left</b> corner: the top-centre placement was requested first
        /// and every attempt to derive a centre put it off screen or behind the HUD, so this follows
        /// the picker overlay's proven recipe instead — a fixed small offset from a screen-rooted
        /// parent. A C#-constructed <c>dc.h2d.Object</c> never renders, so the labels come from
        /// <c>Assets.makeText</c> and the parent is a game-owned object either way.
        /// </para>
        /// </summary>
        private static void Ensure(dc.ui.HUD hud, dc.ui.hud.LifeBar lifebar)
        {
            if (ReferenceEquals(_owner, hud) && _score != null)
            {
                return;
            }

            Remove();
            _owner = hud;

            // HUD.root is the full-screen Layers every HUD flow is built on. The life bar's parent
            // flow is the fallback: it still renders, it just needs the panel pushed up by its own
            // offset, which the anchor below accounts for.
            dc.h2d.Object? parent = null;
            try
            {
                parent = hud.root;
            }
            catch (Exception ex)
            {
                LogWarn("[PvP] could not read the HUD root: {Message}", ex.Message);
            }

            var rooted = parent != null;
            parent ??= (dc.h2d.Object?)lifebar.parent ?? lifebar;
            _parent = parent;

            _score = MakeLine(parent, PanelColor, PanelScale);
            _round = MakeLine(parent, PanelColor, 0.85);
            _banner = MakeLine(parent, BannerColor, 1.3);
            _matchStart = MakeLine(parent, MatchStartColor, 1.6);

            // Where the parent's origin sits on screen. On the HUD root that is (0,0); on the fallback
            // flow it is wherever the game parked that flow, so it must be subtracted to keep the panel
            // at the top-left of the SCREEN rather than at the top-left of that flow.
            double originX = 0.0;
            double originY = 0.0;
            if (!rooted)
            {
                try
                {
                    originX = parent.absX;
                    originY = parent.absY;
                }
                catch (Exception ex)
                {
                    LogWarn("[PvP] could not read the fallback parent origin: {Message}", ex.Message);
                }
            }

            // Top-left corner, matching the picker overlay's own constants (x 24, y 20, step 20).
            const double Left = 24.0;
            const double Top = 20.0;
            const double Step = 22.0;
            _score.x = Left - originX;
            _round.x = Left - originX;
            _banner.x = Left - originX;
            if (_matchStart != null)
            {
                _matchStart.x = Left - originX;
                _matchStart.y = Top - originY;
            }
            _score.y = Top - originY;
            _round.y = Top + Step - originY;
            _banner.y = Top + (Step * 2) - originY;

            // A mis-placed panel is a silent failure, so record what was actually built, once per HUD.
            try
            {
                Log($"[PvP] score panel anchored: parent={parent.GetType().Name} rooted={rooted} "
                    + $"origin=({originX:F0},{originY:F0}) local=({_score.x:F0},{_score.y:F0}) "
                    + $"screen=({originX + _score.x:F0},{originY + _score.y:F0})");
            }
            catch
            {
                // Logging must never break the panel.
            }
        }

        private static dc.ui.Text MakeLine(dc.h2d.Object parent, int color, double scale)
        {
            var text = dc.Assets.Class.makeText(
                "".AsHaxeString(),
                dc.ui.Text.Class.COLORS.get("WO".AsHaxeString()),
                false,
                parent);
            text.textColor = color;
            text.customScale = scale;
            text.onResize();
            return text;
        }

        /// <summary>
        /// Writes the score and the round line. Each is only touched when its text changed, so the
        /// panel costs a few string comparisons per frame rather than a reflow. The round line says
        /// who took the last round while the break counts down, which is what
        /// <see cref="LastRoundLoser"/> is kept for.
        /// </summary>
        private static void Paint()
        {
            var score = $"P1  {ScoreP1} : {ScoreP2}  P2";
            if (score != _lastScore)
            {
                _lastScore = score;
                _score?.set_text(score.AsHaxeString());
            }

            var loser = LastRoundLoser;
            var round = Pvp switch
            {
                PvpState.Starting => "ARENA LOADING",
                PvpState.Fighting when _freezeLeft > 0 => $"ROUND {RoundNumber}   GET READY",
                PvpState.Fighting => $"ROUND {RoundNumber}   FIGHT   (first to {RoundsToWin})",
                PvpState.RoundBreak => $"{(ReferenceEquals(loser, _hero1) ? "P2" : "P1")} TOOK ROUND {RoundNumber - 1}   NEXT IN {RoundBreakLeft:0}s",
                _ => string.Empty,
            };
            if (round != _lastRound)
            {
                _lastRound = round;
                _round?.set_text(round.AsHaxeString());
            }
        }

        /// <summary>Shows the winner banner. It stays up until the session is left or reset.</summary>
        internal static void ShowWinner(int winner, int scoreP1, int scoreP2)
        {
            _lastBanner = $"PLAYER {winner} WINS  {scoreP1} : {scoreP2}";
            _banner?.set_text(_lastBanner.AsHaxeString());
        }

        /// <summary>Clears the banner between matches.</summary>
        internal static void HideWinner()
        {
            if (_lastBanner.Length == 0)
            {
                return;
            }

            _lastBanner = string.Empty;
            _banner?.set_text(string.Empty.AsHaxeString());
        }

        /// <summary>Shows a screen-centered "Match starts" prompt after round 1.</summary>
        internal static void ShowMatchStart()
        {
            try
            {
                EnsurePanelReady();
                if (_matchStart == null || _parent == null)
                {
                    return;
                }

                var text = "Match starts".AsHaxeString();
                _matchStart.set_text(text);

                double cx = 0.0;
                double cy = 0.0;
                try
                {
                    var hero = _hero1 ?? _hero2;
                    var level = hero?._level;
                    var viewport = level?.viewport;
                    if (viewport != null)
                    {
                        var stageW = viewport.wid * viewport.zoom;
                        var stageH = viewport.hei * viewport.zoom;
                        if (stageW > 0)
                        {
                            cx = stageW * 0.5;
                        }
                        if (stageH > 0)
                        {
                            cy = stageH * 0.5;
                        }
                    }
                }
                catch
                {
                    // fall back to center of screen if viewport not accessible
                }

                if (cx <= 0.0 || cy <= 0.0)
                {
                    try
                    {
                        var hud = dc.pr.Game.Class.ME?.hud;
                        var lifebar = hud?.heroLifeBar;
                        if (hud != null)
                        {
                            var w = 0;
                            var h = 0;
                            try
                            {
                                w = 854;
                                h = 480;
                            }
                            catch { }
                            if (w > 0 && h > 0)
                            {
                                // rough fallback; prefer viewport
                                cx = w * 0.5;
                                cy = h * 0.5;
                            }
                            else if (lifebar != null)
                            {
                                cx = 240;
                                cy = 135;
                            }
                        }
                    }
                    catch { }
                }

                double tw = 0;
                try
                {
                    tw = _matchStart.textWidth;
                }
                catch { }
                var scale = _matchStart.customScale > 0 ? _matchStart.customScale : 1.0;
                var textW = tw > 0 ? tw * scale : 200 * scale;
                var originX = 0.0;
                var originY = 0.0;
                var rooted = false;
                try
                {
                    rooted = _parent == _owner?.root;
                }
                catch { }
                if (!rooted)
                {
                    try
                    {
                        originX = _parent.absX;
                        originY = _parent.absY;
                    }
                    catch { }
                }

                _matchStart.x = cx - (textW * 0.5) - originX;
                _matchStart.y = cy - originY;
                _matchStart.visible = true;
                _matchStartLeft = 1.5;
            }
            catch (Exception ex)
            {
                LogWarn("[PvP] could not show match start prompt: {Message}", ex.Message);
            }
        }

        private static void EnsurePanelReady()
        {
            try
            {
                var hud = dc.pr.Game.Class.ME?.hud;
                var lifebar = hud?.heroLifeBar;
                if (hud == null || lifebar == null)
                {
                    return;
                }
                if (ReferenceEquals(_owner, hud) && _matchStart != null)
                {
                    return;
                }
                Ensure(hud, lifebar);
            }
            catch { }
        }

        private static void TickMatchStart()
        {
            if (_matchStart == null)
            {
                return;
            }
            if (_matchStartLeft <= 0)
            {
                if (_matchStart.visible)
                {
                    try
                    {
                        _matchStart.visible = false;
                        _matchStart.set_text("".AsHaxeString());
                    }
                    catch { }
                }
                return;
            }
            try
            {
                _matchStartLeft -= 0.016;
            }
            catch
            {
            }
            if (_matchStartLeft <= 0)
            {
                try
                {
                    _matchStart.visible = false;
                    _matchStart.set_text("".AsHaxeString());
                }
                catch { }
            }
        }

        internal static void HideMatchStart()
        {
            try
            {
                if (_matchStart == null) return;
                _matchStartLeft = 0;
                _matchStart.visible = false;
                _matchStart.set_text("".AsHaxeString());
            }
            catch { }
        }

        /// <summary>
        /// Removes the labels, the same way the hero readouts remove theirs: through the game's own
        /// <c>remove()</c>, so nothing is left dangling in the HUD flow when the match (or the HUD)
        /// goes away.
        /// </summary>
        private static void Remove()
        {
            foreach (var line in new dc.ui.Text?[] { _score, _round, _banner, _matchStart })
            {
                if (line == null)
                {
                    continue;
                }

                try
                {
                    line.remove();
                }
                catch (Exception ex)
                {
                    LogWarn("[PvP] could not remove a score panel line: {Message}", ex.Message);
                }
            }

            _score = null;
            _round = null;
            _banner = null;
            _matchStart = null;
            _owner = null;
            _parent = null;
            _lastScore = string.Empty;
            _lastRound = string.Empty;
            _matchStartLeft = 0;
        }

        /// <summary>
        /// Drops the panel when the match ends, and clears the error latch so a later match rebuilds
        /// it even if an earlier one tripped the latch.
        /// </summary>
        internal static void Reset()
        {
            _broken = false;
            _lastBanner = string.Empty;
            _matchStartLeft = 0;
            Remove();
        }
    }
}
