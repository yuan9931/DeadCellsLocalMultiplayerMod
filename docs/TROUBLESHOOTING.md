# Troubleshooting — DeadCellsLocalMultiplayerMod

<div align="center">

English • [Chinese](TROUBLESHOOTING_zh.md)

</div>

Common problems and how to diagnose them. Turn logging on first — it answers most questions:

```powershell
$env:DEADCELLSLOCALMULTIPLAYERMOD_LOG = "1"   # then start the game
```

## The mod does not appear / does not load

- The folder must be named exactly `coremod` (singular, lowercase) in the game root. `coremods` makes the launcher exit silently.
- The mod folder inside `coremod/mods/DeadCellsLocalMultiplayerMod/` must contain `modinfo.json` directly (not in a subfolder).
- Launch the game via `coremod/core/host/startup/DeadCellsModding.exe`, not `deadcells.exe`.
- Check the DCCM log for load errors mentioning `DeadCellsLocalMultiplayerMod`.

## Build fails

- **"The installed Dead Cells Core Modding MDK was not found"** — `DCCM_MDK_ROOT` is not set. Run `build.cmd` (it downloads the MDK and sets it), or set `DCCM_MDK_ROOT` to a folder that contains `build\build.props`.
- **`dotnet` not found / wrong SDK** — install the .NET 10 SDK.
- Rate-limit when downloading the MDK — set `GH_TOKEN` (a GitHub PAT) and retry, or drop a local `dccm.zip` in the repo root.

## Second hero never spawns

- The session must be active: title menu → **Local Co-op** → pick two different devices → **Start local co-op**.
- Devices must not clash: keyboard can be used once, and two players cannot share one pad. The menu enforces this.
- If player one has no valid cell for ~300 frames after a run starts, placement is skipped on purpose. Check the log for `Second hero spawned`.
- A run **restored from a save** is adopted via `AdoptExistingHeroes` instead of `initHero`; check the log for `adopted the second hero`.

## Second hero is invisible or stuck at -1,-1

- The level can drop it while streaming; the runtime re-places it every 0.5s (`RepairSecondHero`). Give it a moment.
- If it stays gone, check the log for `hero two was off level, placed back`.

## One pad drives both heroes

- Each hero must have its own `Controller` with `mode`/`gc` pinned. The mod re-pins every frame (`MirrorGating`), but confirm the two players are on **different** slots in the menu.
- A pad appearing as an unexpected index (Steam Remote Play) is matched by connection order, not `device.index`.

## Shared UI (map / inventory / pause) ignores the pad

- `MirrorGlobalDevice` keeps the global controller answering both keyboard and pads. If it still ignores the pad, check the log for `global controller mode ->` lines.

## Camera does not follow both heroes

- Shared camera is on by default; toggle it in the menu (`Camera` page). The log line `[SharedCamera] zoom ...` shows the current zoom and separation.
- With the camera off, the view follows player one only.

## Settings do not survive a restart

- Devices/camera persist via DCCM `Config<T>` (JSON in the DCCM config folder). The skin id is resolved after the game's skin data loads (`TryResolvePendingSkin`); if the menu still shows "same as P1", open the menu once after the title screen loads.

## Crash when entering a cell door / sub-level

- This was the stale `lastRoomId` bug, fixed by resetting `hero2.lastRoomId = -1` on relocation. If you still see `Null access .rType`, paste the full log (with `DEADCELLSLOCALMULTIPLAYERMOD_LOG=1`) and the crash location.

## No log lines appear

- Logging is off by default. Set `DEADCELLSLOCALMULTIPLAYERMOD_LOG` to any non-`0` value **before** starting the game (it is read once at startup).
