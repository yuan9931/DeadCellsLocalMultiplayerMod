<div align="center">

English • [Chinese](CONTRIBUTING_zh.md)

</div>
# Contributing to DeadCellsLocalMultiplayerMod

Thank you for your interest in improving DeadCellsLocalMultiplayerMod. This document describes how to build the project and what we expect from contributions.

## Prerequisites

- **Windows** (the project targets `net10.0` with `SupportedOSPlatform` Windows).
- **.NET SDK** compatible with **.NET 10** (see `TargetFramework` in `DeadCellsLocalMultiplayerMod.csproj`).
- **Dead Cells** with **DCCM (Dead Cells Core Modding API)** installed for local testing.
- **`DCCM_MDK_ROOT`** environment variable pointing at your DCCM MDK folder (the folder that contains `build\build.props`). `build.cmd` / `build.ps1` download and set this for you automatically.

## Build

From the repository root, the easiest way:

```powershell
build.cmd
```

This downloads the DCCM MDK (latest `win-x64.zip` from the `dead-cells-core-modding/core` releases, or a local `dccm.zip` if present), sets `DCCM_MDK_ROOT`, adds the local NuGet source, and runs `dotnet build -c Release`.

Or build directly if `DCCM_MDK_ROOT` is already set:

```powershell
dotnet build -c Release
```

Output artifacts are produced under `bin/Release/net10.0/win-x64/` (the packaged mod layout under `output/DeadCellsLocalMultiplayerMod/`, as configured by the DCCM MDK targets).

## Project layout (high level)

- `ModEntry.cs` — mod entry point, title-menu flow, session activation, per-frame forwarding.
- `Coop/LocalCoop*.cs` — second-hero runtime (one `partial` class): spawning, per-player input, shared state, detached head, skin, hooks, diagnostics, persisted settings.
- `SharedCamera.cs` — shared camera (follows both heroes, zooms out).
- `AGENTS.md` — architecture, build, logging and in-game validation notes (start here).
- `PLAN.md` — implementation plan and the core APIs it was based on.

## How to contribute

1. **Open an issue** or discuss a **small, scoped** change before large refactors.
2. **Fork** the repository and create a **branch** focused on one feature or fix.
3. **Keep pull requests focused** — avoid unrelated formatting, renames, or drive-by cleanups in files you are not changing for the task.
4. **Match existing style** — naming, patterns, and comment density should match surrounding code.
5. **Build** in Release before submitting; fix any new compiler warnings relevant to your change.
6. **Describe** what changed and **why** in the PR description (plain language, complete sentences).

## Code review expectations

- Changes should be **minimal** and **directly related** to the stated goal.
- Do **not** delete unrelated comments or rewrite large sections without need.
- Prefer **one clear code path** over many special cases when possible.

## Testing

There is no automated test suite in this repository. For gameplay changes:

- Run the game **through DCCM** with the mod loaded.
- Verify **both heroes**, per-player input isolation, the **shared camera**, **level transitions** (including cell doors and hidden levels), and that **either hero dying ends the run**.

## Questions

For DCCM installation and API documentation, see the official DCCM docs and GitHub:

- [DCCM install (Steam Workshop)](https://dead-cells-core-modding.github.io/docs/docs/tutorial/install-workshop/)
- [dead-cells-core-modding/core](https://github.com/dead-cells-core-modding/core)
