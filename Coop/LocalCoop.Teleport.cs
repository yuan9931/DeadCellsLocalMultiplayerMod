using dc.en;
using dc.hxd;
using dc.libs.heaps;
using dc.pr;

namespace DeadCellsLocalMultiplayerMod;

/// <summary>
/// Part of <see cref="LocalCoop"/>: the manual teleport that brings a hero to its partner, and back.
/// <para>
/// A hold moves the holder onto its partner; the next hold (within
/// <see cref="ReturnWindowSeconds"/>) brings **only that hero** back to the spot it left — the
/// partner is never teleported by someone else's return. Whatever geometry separated the two heroes
/// is irrelevant, because the move is a plain reposition that crosses solid barriers — the same
/// <see cref="PlaceNextTo"/> primitive the rest of the mod places hero two with. A hero using an
/// in-level teleporter pad also drags the partner along (see <c>OnHeroTeleportation</c> in
/// <c>LocalCoop.Hooks.cs</c>).
/// </para>
/// <para>
/// Binding: **hold the left-stick click (L3)** on that player's own controller, or **hold T** for
/// the player on the keyboard, for <see cref="HoldSeconds"/> seconds. The hero whose player held it
/// is the one that moves, so the stranded player always holds the button that frees them. The hold
/// is deliberate: a tap must be free to brush past without teleporting anyone.
/// </para>
/// </summary>
internal static partial class LocalCoop
{
    // ---------------------------------------------------------------------------------------
    // Timing
    // ---------------------------------------------------------------------------------------

    /// <summary>How long the button must be held before the teleport fires.</summary>
    private const double HoldSeconds = 1.0;

    /// <summary>
    /// How long the return trip stays on offer after a teleport. Past that the remembered spot is
    /// dropped and the next hold is an ordinary forward teleport again — a return that stayed armed
    /// forever would silently disagree with where the player thinks they are after a while.
    /// </summary>
    private const double ReturnWindowSeconds = 10.0;

    // ---------------------------------------------------------------------------------------
    // Device constants
    // ---------------------------------------------------------------------------------------

    // SDL controller button 7 (LEFTSTICK, "L3"). hxd's Pad fills its buttons/values arrays in
    // _Pad.initPad as SDL button + 6 — indices 0..5 are the analog axes and 6 is SDL button 0 — so
    // L3 lands on index 13, which is the slot both initPad and the button event write.
    private const int L3ValueIndex = 13;

    // The code the engine indexes Key.keyPressed with is NOT the raw SDL code. Window.onEvent()
    // maps every key through Window.CODEMAP first, and _Window fills that table with
    // `CODEMAP[97 + i] = 65 + i` for the 26 letters — 'a'..'z' (97..122) become 'A'..'Z' (65..90).
    // So T is 84, not 116: passing the lowercase code looks up a slot nothing ever writes and the
    // key silently never registers (which is exactly how the first attempt failed).
    private const int KeyT = 84; // 'T'

    // ---------------------------------------------------------------------------------------
    // State
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Where a hero stood when it last teleported, so the next hold returns it there.
    /// </summary>
    private struct TeleportReturn
    {
        public bool Has;
        public Level? Level;    // a recorded cell only means something on the level it came from
        public int Cx;
        public int Cy;
        public double Xr;
        public double Yr;
        public double RemainingSeconds;
    }

    private static TeleportReturn _return1;
    private static TeleportReturn _return2;

    // Hold-to-fire state, one set per player. The button is a level, not a press, so the hold has to
    // be timed: a short tap must NOT teleport (that is what makes the button safe to brush against
    // while walking), and one hold must fire exactly once.
    private static double _teleportHold1;
    private static double _teleportHold2;
    private static bool _teleportFired1;
    private static bool _teleportFired2;

    // ---------------------------------------------------------------------------------------
    // Per-frame entry point
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Handles the teleport button for both players. Called every frame from
    /// <see cref="OnFrameUpdate"/>.
    /// </summary>
    private static void WatchTeleportKey(Hero hero1, Hero hero2, double dt)
    {
        // Age the return windows even on a frame where a hero is dead or down, so a window cannot
        // be kept alive by the very guard below.
        TickReturn(ref _return1, dt);
        TickReturn(ref _return2, dt);

        if (!IsAlive(hero1) && !IsAlive(hero2))
        {
            // Nobody left standing can ask for a teleport (a destroyed hero ends the run anyway).
            ResetTeleportState();
            return;
        }

        if (hero1.destroyed || hero2.destroyed)
        {
            // A destroyed partner (shared health: the death removes hero two) is not a destination;
            // the run is ending and teleport state must not keep aiming at a corpse.
            ResetTeleportState();
            return;
        }

        // A downed hero cannot act — its input controller is parked — so its own button is dropped.
        if (!IsAlive(hero1))
        {
            _teleportHold1 = 0.0;
            _teleportFired1 = false;
        }
        if (!IsAlive(hero2))
        {
            _teleportHold2 = 0.0;
            _teleportFired2 = false;
        }

        if (IsAlive(hero1)
            && PollTeleportHold(Player1, _pad1, dt, ref _teleportHold1, ref _teleportFired1))
        {
            ToggleTeleport(hero1, hero2, "P1", ref _return1);
            return; // one teleport per frame
        }
        if (IsAlive(hero2)
            && PollTeleportHold(Player2, _pad2, dt, ref _teleportHold2, ref _teleportFired2))
        {
            ToggleTeleport(hero2, hero1, "P2", ref _return2);
        }
    }

    /// <summary>Forgets all teleport state: any hold in progress and both remembered spots.</summary>
    private static void ResetTeleportState()
    {
        _teleportHold1 = 0.0;
        _teleportHold2 = 0.0;
        _teleportFired1 = false;
        _teleportFired2 = false;
        _return1.Has = false;
        _return2.Has = false;
    }

    // ---------------------------------------------------------------------------------------
    // Hold detection
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Times one player's hold and reports the single frame on which it reaches
    /// <see cref="HoldSeconds"/>. Returns false from then on until the button is released, so a
    /// long hold fires once rather than every frame.
    /// </summary>
    private static bool PollTeleportHold(
        InputBinding binding, GamePad? pad, double dt, ref double held, ref bool fired)
    {
        if (!TeleportButtonDown(binding, pad))
        {
            held = 0.0;
            fired = false;
            return false;
        }

        if (IsUsableDt(dt))
        {
            held += dt; // a paused or bogus frame must not count towards the hold
        }

        if (fired || held < HoldSeconds)
        {
            return false;
        }

        fired = true;
        return true;
    }

    /// <summary>
    /// Whether that player's teleport button is down right now: L3 on their pad, T on the keyboard.
    /// Both are read as levels (held), not as presses, because the teleport is hold-to-fire.
    /// </summary>
    private static bool TeleportButtonDown(InputBinding binding, GamePad? pad)
    {
        return binding.IsKeyboard ? RawKeyDown(KeyT) : RawPadButton(pad, L3ValueIndex);
    }

    /// <summary>
    /// Whether a keyboard key is currently held, read straight from <c>Key.keyPressed</c>.
    /// <para>
    /// The engine stores <c>+(frame + 1)</c> on key-down and <c>-(frame + 1)</c> on key-up, so a
    /// positive entry means the key is held — exactly what the game's own <c>Key.isDown</c> checks.
    /// Doing it by hand keeps this symmetric with the pad side (which reads <c>Pad.values</c> by
    /// hand for the same reason) and avoids depending on which proxy member happens to be bound.
    /// Note it must be the level, not <c>Key.isPressed</c>: that one is only true on the single
    /// frame the key went down, so it can never time a hold.
    /// </para>
    /// </summary>
    private static bool RawKeyDown(int code)
    {
        try
        {
            var table = Key.Class.keyPressed;
            if (table == null || code < 0 || code >= table.length)
            {
                return false;
            }
            return System.Runtime.InteropServices.Marshal.ReadInt32(table.bytes, code * 4) > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// A pad's raw button value, straight from <c>Pad.values</c>.
    /// <para>
    /// Read raw rather than through <c>Controller.padIsPressed</c> because that one indexes the
    /// game's own button ids, which come from a data table the mod does not have: the ids the engine
    /// switches on in <c>GamePad.update</c> put the triggers at 6–7 and the stick directions at
    /// 17–27, so the id for a stick click is not knowable from the code. The hxd pad's own array is
    /// unambiguous — index 13 is SDL button 7 — and <c>values</c> holds a 0/1 double per button,
    /// written both by <c>initPad</c> and by every button event.
    /// </para>
    /// </summary>
    private static bool RawPadButton(GamePad? pad, int index)
    {
        try
        {
            var device = pad?.device;
            if (device == null)
            {
                return false;
            }
            var values = device.values;
            if (values == null || index < 0 || index >= values.length)
            {
                return false;
            }

            // ArrayBytes_Float is a double array (the engine reads it with a << 3 stride).
            var raw = System.Runtime.InteropServices.Marshal.ReadInt64(values.bytes, index * 8);
            return System.BitConverter.Int64BitsToDouble(raw) > 0.5;
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------------------------------
    // Return window
    // ---------------------------------------------------------------------------------------

    /// <summary>Runs a return window down, dropping the spot once it expires.</summary>
    private static void TickReturn(ref TeleportReturn back, double dt)
    {
        if (!back.Has || !IsUsableDt(dt))
        {
            return; // nothing armed, or a paused/bogus frame that must not eat the window
        }

        back.RemainingSeconds -= dt;
        if (back.RemainingSeconds <= 0.0)
        {
            back.Has = false;
        }
    }

    /// <summary>
    /// Whether a frame's delta can be counted against a timer. A paused or bogus frame (zero,
    /// negative, NaN, infinity) must not age a hold or a return window.
    /// </summary>
    private static bool IsUsableDt(double dt)
    {
        return !double.IsNaN(dt) && !double.IsInfinity(dt) && dt > 0.0;
    }

    // ---------------------------------------------------------------------------------------
    // The teleport itself
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// First hold: remember where the hero stood, then move it onto its partner. Second hold (on
    /// the same level, with the spot still remembered and the window still open): put it back. See
    /// the type summary.
    ///
    /// Both moves are plain repositions, so they cross a closed door or a force field without any
    /// geometry test — that is the whole point of doing this on a button.
    /// </summary>
    private static void ToggleTeleport(Hero mover, Hero partner, string who, ref TeleportReturn back)
    {
        if (mover._level == null || partner._level == null || mover._level != partner._level)
        {
            if (Logging)
            {
                Log($"[LocalCoop] {who} asked to teleport, but the two heroes are on different levels");
            }
            return;
        }

        // A remembered cell only means something on the level it was recorded on.
        if (back.Has && !ReferenceEquals(back.Level, mover._level))
        {
            back.Has = false;
        }

        // Second hold: go back to where this hero came from.
        if (back.Has)
        {
            var cx = back.Cx;
            var cy = back.Cy;
            var xr = back.Xr;
            var yr = back.Yr;
            back.Has = false;

            // The return moves ONLY the hero that held the button — the partner is never
            // teleported by someone else's return (by request).
            mover.setPosCase(cx, cy, xr, yr);
            mover.onScreenRecent = mover.frameCountThresholdForOutOfGame;

            if (Logging)
            {
                Log($"[LocalCoop] {who} teleported back to {cx},{cy} " +
                    $"(P1 {_hero1?.cx},{_hero1?.cy} P2 {_hero2?.cx},{_hero2?.cy})");
            }
            return;
        }

        // First hold: remember this spot (and arm the return window), then move to the partner.
        // With the partner DOWNED the destination is where its body fell — its live position no
        // longer means that (a downed player one is re-placed on the survivor every frame for the
        // positional-audio listener), and teleporting onto the partner's "position" would be the
        // mover's own spot. No recorded fall on this level: nothing sensible to jump to.
        bool partnerDown = IsDown(partner);
        TeleportReturn deathSpot = default;
        if (partnerDown && !TryGetDeathSpot(partner, mover._level, out deathSpot))
        {
            if (Logging)
            {
                Log($"[LocalCoop] {who} asked to teleport to the downed partner, but its fall spot is not on this level");
            }
            return;
        }
        if (!partnerDown && (partner.cx < 0 || partner.cy < 0))
        {
            return;
        }

        back.Has = true;
        back.Level = mover._level;
        back.Cx = mover.cx;
        back.Cy = mover.cy;
        back.Xr = mover.xr;
        back.Yr = mover.yr;
        back.RemainingSeconds = ReturnWindowSeconds;

        if (partnerDown)
        {
            mover.setPosCase(deathSpot.Cx, deathSpot.Cy, deathSpot.Xr, deathSpot.Yr);
        }
        else
        {
            PlaceNextTo(mover, partner);
        }
        mover.onScreenRecent = mover.frameCountThresholdForOutOfGame;

        // The hint above this hero becomes "hold to go back" for the length of the return window,
        // which is exactly what the next hold will do.
        ShowReturnHint(mover);

        if (Logging)
        {
            Log($"[LocalCoop] {who} teleported to the partner{(partnerDown ? "'s death spot" : string.Empty)}, return armed for " +
                $"{ReturnWindowSeconds:0}s (P1 {_hero1?.cx},{_hero1?.cy} P2 {_hero2?.cx},{_hero2?.cy})");
        }
    }
}