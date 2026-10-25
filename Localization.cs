using HaxeProxy.Runtime;
using ModCore.Events;
using ModCore.Events.Interfaces.Game;
using ModCore.Modules;
using ModCore.Utilities;

namespace DeadCellsLocalMultiplayerMod;

/// <summary>
/// Registers this mod's localization namespace and loads the packed resource
/// archive (res.pak) that holds Resourcefile/lang/*.mo. GetText resolves the
/// .mo entries from that pak at runtime, so the co-op menu can be shown in the
/// game's current language (e.g. Simplified Chinese from main.zh.mo).
///
/// Mirrors DeadCellsMultiplayerMod/Tools/MultiplayerUILang.cs: RegisterMod in the
/// constructor, then loadPak("res.pak") from the mod root on game init.
/// </summary>
public sealed class LocalCoopUILang : IEventReceiver, IOnGameEndInit
{
    private readonly ModEntry _entry;

    public LocalCoopUILang(ModEntry entry)
    {
        _entry = entry;
        EventSystem.AddReceiver(this);
        GetText.Instance.RegisterMod("DeadCellsLocalMultiplayerModLang");
    }

    void IOnGameEndInit.OnGameEndInit()
    {
        var modRoot = _entry.Info.ModRoot;
        if (modRoot is null)
        {
            _entry.Logger.Warning("[DeadCellsLocalMultiplayerMod] localization skipped: mod root is null");
            return;
        }

        var resPath = modRoot.GetFilePath("res.pak");
        if (resPath is null)
        {
            _entry.Logger.Warning("[DeadCellsLocalMultiplayerMod] localization skipped: res.pak not found at mod root");
            return;
        }

        FsPak.Instance.FileSystem.loadPak(resPath.AsHaxeString());
    }
}
