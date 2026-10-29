namespace DeadCellsLocalMultiplayerMod;

/// <summary>
/// One authoritative identity for packaging and log diagnostics. Keep <see cref="Version"/> in
/// sync with the csproj <c>Version</c> (which drives the generated <c>modinfo.json</c>).
/// </summary>
internal static class BuildInfo
{
    public const string Version = "0.1.29";
}
