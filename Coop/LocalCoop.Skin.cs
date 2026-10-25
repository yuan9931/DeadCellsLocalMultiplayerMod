using dc;
using dc.en;
using dc.pr;

// The castle-db row type a hero skin resolves to (returned by Hero.getSkinInfo()).
using SkinInfo = Hashlink.Virtuals.virtual_colorMap_consoleCmdId_glowData_group_head_incompatibleHeads_item_model_onlyDefaultHead_scarfBlendMode_scarfs_;

namespace DeadCellsLocalMultiplayerMod;

/// <summary>
/// Part of <see cref="LocalCoop"/>: player two's skin.
/// </summary>
internal static partial class LocalCoop
{
    /// <summary>Label for the setup menu: the chosen skin id, or "same as P1".</summary>
    internal static string Player2SkinLabel
    {
        get
        {
            if (Player2Skin != null)
            {
                return Player2SkinId!;
            }

            return _player2SkinChosen ? "same as P1" : "auto (differs from P1)";
        }
    }

    /// <summary>
    /// The default for player two: the first wearable skin that is not the one player one is
    /// currently wearing.
    /// </summary>
    private static dc.String? PickDefaultSkin(dc.String? player1Skin)
    {
        var current = player1Skin?.ToString();
        foreach (var id in SkinIds())
        {
            if (!string.Equals(id.ToString(), current, System.StringComparison.Ordinal))
            {
                return id;
            }
        }

        return null;
    }

    /// <summary>
    /// Every skin a playable character can wear, read from the game's own skin sheet
    /// (<c>Data.Class.skin.all</c>) — the same source the vanilla skin selector uses. Only rows
    /// flagged <c>head</c> are real outfits; the others (boss scarves, internal skins such as
    /// <c>KingsHandBoss</c>) are rejected by <c>Hero.applySkin()</c> with "There is no Playable
    /// Characters skin data", which would leave the hero's graphics torn down.
    /// </summary>
    private static List<dc.String> SkinIds()
    {
        if (_skinIds != null)
        {
            return _skinIds;
        }

        var ids = new List<dc.String>();
        try
        {
            dynamic all = Data.Class.skin.all;
            var count = all.get_length();
            for (var i = 0; i < count; i++)
            {
                var raw = all.getDyn(i);
                if (raw is null)
                {
                    continue;
                }

                dynamic row = raw;
                if (row.head != true)
                {
                    continue;
                }

                var id = (dc.String?)row.item;
                if (id != null)
                {
                    ids.Add(id);
                }
            }

            LogVerbose($"[LocalCoop] {ids.Count} wearable skin id(s) discovered from the game data");
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not read the game's skin list: {Message}", ex.Message);
        }

        // Only cache a usable list: the data may not be loaded yet the first time this runs.
        if (ids.Count > 0)
        {
            _skinIds = ids;
        }

        return ids;
    }

    /// <summary>
    /// Cycles player two's skin through the full ramp: <c>auto (differs from P1)</c> first, then
    /// every wearable skin, then an explicit <c>same as P1</c>, and back to <c>auto</c>.
    /// </summary>
    /// <remarks>
    /// <b>The auto state has to be a stop in the cycle, not just the initial value.</b> It is
    /// represented by <c>_player2SkinChosen == false</c> with no explicit skin, and the earlier
    /// version set the flag to true on every press, so once a player touched this entry auto could
    /// never be selected again — the only way back was to delete the settings file. The ramp now
    /// runs: auto → skin[0] … skin[n-1] → same as P1 → auto.
    /// </remarks>
    internal static void CyclePlayer2Skin()
    {
        var ids = SkinIds();
        if (ids.Count == 0)
        {
            return;
        }

        // Three states: auto (not chosen, no skin), a specific skin, or same-as-P1 (chosen, no skin).
        var current = Player2SkinId;
        if (!_player2SkinChosen)
        {
            // auto -> first skin
            _player2SkinChosen = true;
            Player2Skin = ids[0];
        }
        else if (current == null)
        {
            // same as P1 -> back to auto
            _player2SkinChosen = false;
            Player2Skin = null;
        }
        else
        {
            // a specific skin -> the next one, or "same as P1" past the end
            var index = ids.FindIndex(s => string.Equals(s.ToString(), current, System.StringComparison.Ordinal));
            index++;
            Player2Skin = index >= ids.Count ? null : ids[index];
        }

        _pendingSkinId = null;

        ApplyPlayer2Skin(_hero2);
        SaveSettings();
        Log($"[LocalCoop] player two skin -> {Player2SkinLabel}");
    }

    /// <summary>
    /// Applies player two's skin through the vanilla <c>Hero.applySkin()</c>. That method writes
    /// the id into the single global <c>user.heroSkin</c> before rebuilding the graphics, so the
    /// global is restored right after — player one keeps wearing its own skin, and the per-hero
    /// <see cref="OnHeroGetSkinInfo"/> swap keeps answering with player two's skin for every later
    /// <c>initGfx()</c> (level transitions).
    /// </summary>
    // The last skin applied to hero two, so ApplyPlayer2Skin can skip the (expensive) applySkin
    // call when the skin has not changed. Hero.applySkin() does disposeGfx() + initGfx() on every
    // call with no "unchanged" short-circuit, so a per-frame call tears down and rebuilds hero
    // two's whole graphics every frame — that is the start-of-run freeze. Reset on level
    // transitions (RelocateSecondHero) so a rebuilt hero re-applies its own skin.
    private static dc.String? _appliedSkin;
    private static Hero? _appliedSkinHero;

    // A persisted skin id that could not be resolved to a dc.String at load (the skin data was not
    // loaded yet), kept for a later retry once the game's skin list is available.
    private static string? _pendingSkinId;

    /// <summary>
    /// Sets player two's skin from a persisted id (a plain string), resolving it back to the game's
    /// own <c>dc.String</c> from the skin list. Called when loading persisted settings; also clears
    /// the applied-skin cache so the next run re-applies it.
    /// </summary>
    internal static void SetPlayer2SkinById(string? id, bool chosen)
    {
        _player2SkinChosen = chosen;
        Player2Skin = null;
        _appliedSkin = null;
        _appliedSkinHero = null;
        _pendingSkinId = null;

        if (string.IsNullOrEmpty(id))
        {
            return;
        }

        // Resolve now if the skin list is ready; otherwise keep the id for TryResolvePendingSkin.
        foreach (var s in SkinIds())
        {
            if (string.Equals(s.ToString(), id, System.StringComparison.Ordinal))
            {
                Player2Skin = s;
                return;
            }
        }
        _pendingSkinId = id;
    }

    /// <summary>
    /// Resolves a persisted skin id that could not be resolved at load (the skin data was not ready
    /// yet). Called when the title menu is built and again when a run starts, so by then the game's
    /// skin list is loaded and the id resolves to the game's own <c>dc.String</c>.
    /// </summary>
    internal static void TryResolvePendingSkin()
    {
        if (_pendingSkinId == null || Player2Skin != null)
        {
            return;
        }

        foreach (var s in SkinIds())
        {
            if (string.Equals(s.ToString(), _pendingSkinId, System.StringComparison.Ordinal))
            {
                Player2Skin = s;
                _pendingSkinId = null;
                _appliedSkin = null;
                _appliedSkinHero = null;
                LogVerbose($"[LocalCoop] player two skin resolved from persisted id: {Player2SkinLabel}");
                return;
            }
        }
    }

    /// <summary>
    /// Resolves player two's skin (picking a default different from player one's if none was
    /// chosen yet) and returns it, or null when there is no skin to apply. Split out of
    /// <see cref="ApplyPlayer2Skin"/> so <c>SpawnSecondHero</c> can build player two with the right
    /// skin in <c>wakeup()</c>'s single initGfx instead of rebuilding the graphics a second time.
    /// </summary>
    private static dc.String? ResolvePlayer2Skin(Hero? hero2)
    {
        if (hero2 == null || hero2.destroyed)
        {
            return null;
        }

        // Resolve a persisted skin id that was waiting for the game's skin data to load.
        TryResolvePendingSkin();

        if (!_player2SkinChosen && Player2Skin == null)
        {
            Player2Skin = PickDefaultSkin(hero2._level?.game?.user?.heroSkin);
            if (Player2Skin != null)
            {
                Log($"[LocalCoop] player two skin defaults to {Player2SkinLabel} (differs from P1)");
            }
        }
        else if (!_player2SkinChosen)
        {
            // A persisted skin that was never explicitly chosen can still end up equal to player
            // one's — the save was written by a build that defaulted differently, or player one
            // switched to it since. The whole point of the default is that the two heroes are
            // distinguishable, so re-pick when the auto default has collapsed onto P1.
            var p1 = hero2._level?.game?.user?.heroSkin?.ToString();
            if (Player2Skin != null && string.Equals(Player2Skin.ToString(), p1, System.StringComparison.Ordinal))
            {
                var replacement = PickDefaultSkin(hero2._level?.game?.user?.heroSkin);
                if (replacement != null)
                {
                    Player2Skin = replacement;
                    Log($"[LocalCoop] player two skin was the same as P1 after load; re-picked " +
                        $"{Player2SkinLabel}");
                }
            }
        }

        return Player2Skin;
    }

    private static void ApplyPlayer2Skin(Hero? hero2)
    {
        if (hero2 == null || hero2.destroyed)
        {
            return;
        }

        var desired = ResolvePlayer2Skin(hero2);
        if (desired == null)
        {
            return;
        }

        // Idempotency: skip the expensive applySkin() when this hero already wears this skin.
        if (ReferenceEquals(_appliedSkinHero, hero2) && _appliedSkin != null
            && string.Equals(_appliedSkin.ToString(), desired.ToString(), System.StringComparison.Ordinal))
        {
            return;
        }

        var user = hero2._level?.game?.user;
        var saved = user?.heroSkin;
        try
        {
            hero2.applySkin(desired);
            hero2.visible = true;
            hero2.sprAlpha = 1.0;
            _appliedSkin = desired;
            _appliedSkinHero = hero2;
        }
        catch (Exception ex)
        {
            // applySkin() tears the graphics down before it can fail, so the hero is left with no
            // sprite — put player one's skin back on it to bring it back.
            LogWarn("[LocalCoop] player two skin {Id} refused: {Message}", Player2SkinLabel, ex.Message);
            Player2Skin = null;
            _appliedSkin = null;
            _appliedSkinHero = null;
            try
            {
                if (saved != null)
                {
                    hero2.applySkin(saved);
                }
            }
            catch (Exception ex2)
            {
                LogWarn("[LocalCoop] player two skin recovery failed: {Message}", ex2.Message);
            }
        }
        finally
        {
            if (user != null && saved != null)
            {
                user.heroSkin = saved;
            }
        }
    }

    /// <summary>
    /// <c>Hero.getSkinInfo()</c> resolves the skin from the single global <c>user.heroSkin</c>, so
    /// hero two would always read player one's skin. Point the global at player two's skin for
    /// the duration of the read, then put it back — the vanilla resolver does the rest.
    /// </summary>
    private static SkinInfo OnHeroGetSkinInfo(Hook_Hero.orig_getSkinInfo orig, Hero self)
    {
        var skin = Player2Skin;
        if (skin == null || !ReferenceEquals(self, _hero2))
        {
            return orig(self);
        }

        var user = self._level?.game?.user ?? Game.Class.ME?.user;
        if (user == null)
        {
            return orig(self);
        }

        var saved = user.heroSkin;
        user.heroSkin = skin;
        try
        {
            return orig(self);
        }
        finally
        {
            user.heroSkin = saved;
        }
    }
}
