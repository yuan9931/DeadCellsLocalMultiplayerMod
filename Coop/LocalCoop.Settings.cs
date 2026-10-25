using ModCore.Storage;

namespace DeadCellsLocalMultiplayerMod;

/// <summary>
/// Part of <see cref="LocalCoop"/>: persisted setup. The devices, camera and player-two skin are
/// written to DCCM's config folder (a JSON file) whenever they change and reloaded on boot, so
/// restarting the game does not force re-picking them.
/// </summary>
internal static partial class LocalCoop
{
    private sealed class CoopSettingsData
    {
        public bool Enabled { get; set; }
        public int Player1Slot { get; set; }
        public int Player2Slot { get; set; } = 1;
        public bool CameraEnabled { get; set; } = true;
        public double CameraMinZoom { get; set; } = 0.20;
        public bool SharedHealth { get; set; } = true;
        public bool FriendlyFire { get; set; }
        // Shared by default: one backpack, and the flask / tiers / cooldowns converge onto it.
        // Independent gear is the opt-in.
        public bool SharedEquipment { get; set; } = true;
        public string? Player2SkinId { get; set; }
        public bool Player2SkinChosen { get; set; }

        // PvP: the mode switch and the win target. Not the match state — a match never survives a
        // game restart, and resuming one would need the arena re-entered on purpose.
        public bool PvpEnabled { get; set; }
        public int RoundsToWin { get; set; } = 3;

        // PvP health multiplier: how many times the normal life pool a duel is fought with. 1 = the
        // vanilla pool. Clamped on load to the values the menu offers.
        public int PvpHealthMultiplier { get; set; } = 1;

        // Which heroes were down when the run was last saved. The game's save only records a
        // downed hero at DownedLife (1 HP) — the down state itself is runtime-only — so this is
        // what keeps a quit-and-continue from reviving it. Cleared on revive, on a session that is
        // turned off, and whenever the hero turns out to be healthy (a new run).
        public bool Player1Down { get; set; }

        public bool Player2Down { get; set; }
    }

    private static Config<CoopSettingsData>? _settingsConfig;
    private static Config<CoopSettingsData> SettingsConfig => _settingsConfig ??= new Config<CoopSettingsData>("DeadCellsLocalMultiplayerMod.Coop");

    /// <summary>
    /// Loads the persisted setup on boot and applies it. Safe to call every boot; a missing or
    /// broken file just leaves the defaults in place.
    /// </summary>
    internal static void LoadSettings()
    {
        try
        {
            var d = SettingsConfig.Value;

            Player1 = InputBinding.FromSlot(ClampSlot(d.Player1Slot));
            Player2 = InputBinding.FromSlot(ClampSlot(d.Player2Slot));

            SharedCamera.Enabled = d.CameraEnabled;
            SharedCamera.MinZoom = d.CameraMinZoom;

            SharedHealth = d.SharedHealth;
            FriendlyFire = d.FriendlyFire;
            SharedEquipment = d.SharedEquipment;

            SetPlayer2SkinById(d.Player2SkinId, d.Player2SkinChosen);

            // PvP: the mode switch and the win target. Clamp the target rather than trust the file —
            // a hand-edited config must not be able to set an unreachable win condition.
            PvpEnabled = d.PvpEnabled;
            RoundsToWin = d.RoundsToWin is >= 1 and <= 10 ? d.RoundsToWin : 3;
            // Only the multipliers the menu offers are honoured, so a hand-edited config cannot ask
            // for a pool the arena was never balanced around.
            PvpHealthMultiplier = IsValidHealthMultiplier(d.PvpHealthMultiplier) ? d.PvpHealthMultiplier : 1;

            // Which heroes were down when the run was last saved. Read into the persisted fields
            // (not the runtime ones): the down state is only re-applied when a continued run adopts
            // a hero that is still sitting at DownedLife.
            _persistedHero1Down = d.Player1Down;
            _persistedHero2Down = d.Player2Down;

            if (d.Enabled)
            {
                Activate(Player1, Player2);
            }

            Log($"[LocalCoop] settings loaded: P1={Player1.Display}, P2={Player2.Display}, " +
                $"enabled={Enabled}, camera={SharedCamera.Enabled}, skin={Player2SkinLabel}");
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not load persisted settings: {Message}", ex.Message);
        }
    }

    /// <summary>Writes the current setup to disk. Called whenever a setting changes.</summary>
    internal static void SaveSettings()
    {
        try
        {
            SettingsConfig.Value = new CoopSettingsData
            {
                Enabled = Enabled,
                Player1Slot = Player1.Slot,
                Player2Slot = Player2.Slot,
                CameraEnabled = SharedCamera.Enabled,
                CameraMinZoom = SharedCamera.MinZoom,
                SharedHealth = SharedHealth,
                FriendlyFire = FriendlyFire,
                SharedEquipment = SharedEquipment,
                Player2SkinId = Player2SkinId,
                Player2SkinChosen = _player2SkinChosen,
                PvpEnabled = PvpEnabled,
                RoundsToWin = RoundsToWin,
                PvpHealthMultiplier = PvpHealthMultiplier,
                Player1Down = _persistedHero1Down,
                Player2Down = _persistedHero2Down,
            };
            SettingsConfig.Save();
        }
        catch (Exception ex)
        {
            LogWarn("[LocalCoop] could not save settings: {Message}", ex.Message);
        }
    }

    private static int ClampSlot(int slot) => slot is >= 0 and <= 4 ? slot : 0;
}
