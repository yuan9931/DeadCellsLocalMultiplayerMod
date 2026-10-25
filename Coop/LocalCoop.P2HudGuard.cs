using dc.en;
using dc.pr;
using HaxeProxy.Runtime;

namespace DeadCellsLocalMultiplayerMod;

/// <summary>
/// Part of <see cref="LocalCoop"/>: keeps player two's run state off player one's HUD.
/// <para>
/// The game's own HUD is written from hero-side events — <c>updateLifeBar</c>,
/// <c>heal</c>, <c>refreshInfectionHud</c>, <c>pickItem</c>, the tier setters — and it
/// always writes the <b>global</b> HUD, because the game has exactly one hero. With the
/// default settings that is right: player one is that hero, and shared health / shared
/// equipment mean player two's numbers *are* player one's numbers, so letting them
/// through keeps both HUDs in sync for free.
/// </para>
/// <para>
/// With **independent health** or **independent equipment** it is wrong: player two
/// drinking a flask would repaint the flask counter on player one's HUD, and player two
/// picking up a weapon would repaint player one's skill slots. Those writes are dropped
/// here, the same way <see cref="OnHeroUpdateLifeBar"/> already drops the life-bar
/// write. Player two's own numbers are drawn by its panel instead
/// (<see cref="UpdateHeroPanels"/>), which reads the hero directly.
/// </para>
/// <para>
/// So the rule is: <b>a player-two event only touches the shared HUD when the value it
/// carries is shared</b>. Health is shared iff <see cref="SharedHealth"/>; the flask is part of the
/// equipment, so it follows <see cref="SharedEquipment"/>; tiers, infections and
/// equipment always follow <see cref="SharedEquipment"/>.
/// </para>
/// </summary>
internal static partial class LocalCoop
{
    /// <summary>
    /// Whether the event currently running may write the shared HUD on player two's
    /// behalf. Used by the hooks in <see cref="LocalCoop.Hooks.cs"/> that feed the vanilla
    /// HUD, so a player-two event with an independent value is dropped instead of
    /// repainting player one's HUD.
    /// </summary>
    private static bool P2MayWriteSharedHud(Hero self, bool valueIsShared)
    {
        return valueIsShared || !Enabled || !ReferenceEquals(self, _hero2) || _hero1 == null || _hero1.destroyed;
    }

    /// <summary>
    /// Whether player two's health event may reach the shared life bar. Shared health
    /// drives player one's HUD (it is the same pool); independent health must not.
    /// </summary>
    private static bool P2HealthMayWriteSharedHud(Hero self) =>
        P2MayWriteSharedHud(self, !PvpHealthIndependent);

    /// <summary>
    /// Whether player two's flask event may reach the shared HUD. The flask lives in the
    /// inventory, so it follows <see cref="SharedEquipment"/>, <b>not</b> the health mode:
    /// with shared health but independent equipment, player two's flask count is its own
    /// and must stay off player one's bar.
    /// </summary>
    private static bool P2FlaskMayWriteSharedHud(Hero self) =>
        P2MayWriteSharedHud(self, SharedEquipment);

    /// <summary>
    /// Infection, tiers and equipment changes all live in the inventory, so they
    /// follow <see cref="SharedEquipment"/> — the same gate as the flask. The curse has no gate
    /// here on purpose: its visual is the per-hero LightTip over the cursed hero, not a shared HUD
    /// element, and where the counter itself lives is decided by the <c>curse</c>/<c>endCurse</c>
    /// hooks in <see cref="LocalCoop.Hooks.cs"/> (mirrored in shared health, per-hero in
    /// independent health).
    /// </summary>
    private static bool P2InventoryMayWriteSharedHud(Hero self) =>
        P2MayWriteSharedHud(self, SharedEquipment);
}
