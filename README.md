<div align="center">

English • [Chinese](README_zh.md)

</div>
# DeadCellsLocalMultiplayerMod

**DeadCellsLocalMultiplayerMod** is a **local two-player co-op mod** for *Dead Cells*, built on the **DCCM (Dead Cells Core Modding API)**.

It adds a **second hero** on a single PC and gives each player their **own input device**; both heroes **share one camera window** (no split screen).

## Project layout

| File | Role |
|---|---|
| `ModEntry.cs` | Menu flow, session activation, per-frame forwarding |
| `Coop/LocalCoop*.cs` | Second-hero runtime (one `partial` class): spawning, per-player input, shared state, detached head, skin, hooks, diagnostics |
| `Pvp/LocalCoop*.cs` | PvP arena (same `partial` class): match state machine, score panel |
| `Pvp/PvpArenaStruct.cs` | The arena's room graph, substituted for the game's training level |
| `SharedCamera.cs` | Shared camera: follows both heroes and zooms out so they stay in one window |

## Features

- ✅ A new **Local Co-op** entry on the title menu; the menu UI is localized (includes Simplified Chinese, shown according to the game's language).
- ✅ Each player picks their own input device: keyboard or controller 1–4. Pads may repeat — two pads on one controller is a legitimate way to play (both heroes respond to that device; the isolation is per controller object, not per device).
- ✅ Once confirmed, the session stays active — every run started from the vanilla menu afterwards plays with two heroes, until you disable it manually.
- ✅ Both heroes **share health, inventory and attributes by default**, and **blueprints always stay shared**; health / equipment / friendly fire can each be toggled in the Rules menu. With shared health either hero dying ends the run; with **independent health** a hero at 0 HP goes **down** — hidden and inert while its teammate is still standing — and revives at the forge (the mutation NPC).
- ✅ **Player two gets a HUD of its own, merged into the vanilla one.** With independent equipment its flask, weapons, active skills and backpack continue player one's icon row, its scroll tiers and life bar sit in a second bar row underneath, and its mutations share the vanilla mutation line — all laid out by the game's own HUD, so the column stays put on equipment changes, level changes and HUD-size changes. With shared gear the vanilla HUD already describes both players and is left alone.
- ✅ The camera follows both heroes and automatically zooms out to keep them on screen as they separate — no split screen.
- ✅ **Manual regroup teleport**: hold **T** (keyboard) or the pad's **left-stick click (L3)** for one second and that hero jumps onto its partner — through closed doors, force fields and any other geometry, because it is a reposition, not a walk. Hold again within 10 s and **that hero** jumps back to where it came from (the partner never moves on someone else's return). With independent health a **downed partner is a destination too**: the teleport lands where they fell. One hero riding an in-level **teleporter pad drags the partner along**, so the pair moves together. The door-carry and boss-intro drags stay gone — the button and the pads are the only regrouping, and each hero gets a "hold T / hold L3 to teleport" hint over its head.
- ✅ **Both players can have their detached homunculus heads out at the same time.** Each head is fully independent — its own pad, its own body lock, its own recall — and one player throwing or recalling a head no longer touches the other's.
- ✅ Traps, mobs and interactables serve **both** heroes: the plant trap bites whoever stands in its mouth, spikes / crumbling floors / swinging blades react to player two, mobs aim and take damage as their own target, and fountains, elevators, keyholes, shops and the forge answer to either player.
- ✅ **PvP arena**: a local two-player deathmatch on the first row of the mod's menu — each hero has its own health, friendly fire is forced on, and a lethal hit scores a round in a closed, mob-free arena (the first to N rounds wins; leave with the pause menu). The **first round is a warm-up** (nobody scores, a "Match starts" banner follows it) and the match rules page offers a **health multiplier** (1× / 2× / 5× / 10×) so a round lasts long enough to actually fight. See `docs/PVP_ARENA.md`.
- ✅ Settings (devices, camera, the Rules options — shared health / equipment / friendly fire — PvP mode, rounds to win and the health multiplier, and the player-two skin) **persist across game restarts**.

## Known issues

- With the **shared camera off**, the view follows player one only, so player two can leave the screen.
- There is **no split screen** (the shared camera keeps both heroes in the same window). Player two's HUD lives inside the vanilla one, so its icons and bars share player one's column rather than getting a block of their own; with **shared health** only the icon row and the tier bar are player two's (the vanilla life bar already says both), and the talisman is not shown for player two.
- Both players may share one device (two pads on one controller, or even the same keyboard) — both heroes then respond to it; the isolation is per controller object.
- Independent pad behaviour, level transitions and sub-levels still need more in-game validation.

## Roadmap

Possible future work (not committed):

- Optional split screen / second window as an alternative to the shared camera.
- Support for more than two players.

## Support the project

If you find this project interesting:
- ⭐ Star the repository
- 🍴 Fork it and tinker on your own

Any feedback helps improve the local co-op experience for Dead Cells.

## Requirements

- **Dead Cells (PC)**
- **DCCM (Dead Cells Core Modding API)**
- **[.NET 10 SDK / Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0)**
- **[Microsoft Visual C++ Redistributable 2015–2022 (x64)](https://aka.ms/vs/17/release/vc_redist.x64.exe)** (required by DCCM)

## Installation

1. Subscribe DCCM Core Modding API (https://steamcommunity.com/sharedfiles/filedetails/?id=3633185550)
2. Subscribe DeadCellsLocalMultiplayerMod (https://steamcommunity.com/sharedfiles/filedetails/?id=3820679837)
3. Refer to `https://dead-cells-core-modding.github.io/docs/docs/tutorial/install-workshop`, replace `deadcells.exe`。

## How to play

> 🌐 The menu UI is localized: when the game language is set to **Simplified Chinese** it shows Chinese, when set to English it shows English (other languages fall back to English).

1. Launch the game via steam.
2. On the title menu, choose **Local Co-op**.
3. Pick a device for **Player 1** and **Player 2** (keyboard / controller 1–4; pads may repeat).
4. Adjust the camera (follow both and zoom out / follow P1 only) and the player-two skin as desired.
5. Choose **Start local co-op** (session active).
6. Start a run normally from the title menu — both heroes appear on screen and each is controlled with their own device.
7. To regroup, the player who is stuck **holds T (keyboard) or L3 (pad) for one second** — the hint above the hero says exactly which. Hold again within 10 s to jump back. With independent health, holding it teleports you onto where your downed partner fell.

> 💡 Tip: both heroes share health and inventory by default — health, equipment and friendly fire are each a toggle on the **Rules** page. With shared health either hero dying ends the run; with independent health a fallen hero waits at its death spot for its partner until the pair reaches a forge. The session stays active until you choose **Disable local co-op** in the menu.

### ⚔️ PvP arena

1. Title menu → **Local Co-op** → **PvP arena** (the first row).
2. Optional: toggle **PvP mode**, open **Match rules** and set the win target and the **health multiplier** (1×–10×, for longer rounds).
3. Choose **Start a match** — it launches the arena run straight away (activating the co-op session with your saved devices if needed), so there is no need to start a normal run first.
4. Inside the arena each hero has their own health and damage always lands: the **first round is a warm-up** (nobody scores; a "Match starts" banner follows it), then killing the other player wins them the round, both heroes are restored and returned to their start spots, and the first side to reach the target wins the match. The score and result sit at the top of the screen.
5. Leave with the **pause menu**; start another match from the menu's **Start a match** entry. A match never ends the run itself — the arena reuses the game's own training mode.

> ⚠️ In a match mobs, loot, side rooms, the vanilla sparring NPC and the manual regroup teleport (L3 / T) are all off — each would break a duel.

## Build

One-click build from the repo root (automatically downloads the DCCM MDK and sets `DCCM_MDK_ROOT`):

```powershell
build.cmd
```

Or, with `DCCM_MDK_ROOT` already set:

```powershell
dotnet build -c Release
```

In Git Bash use the `-p:` form (MSYS turns `/p:` into a path):
```bash
dotnet build -c Release -p:DCCM_MDK_ROOT="$DCCM_MDK_ROOT"
```

Artifacts are in `bin/Release/net10.0/win-x64/`; the packed mod layout is in `output/DeadCellsLocalMultiplayerMod/`.

> A successful build does not prove in-game behaviour — see `AGENTS.md` for the validation checklist.

## Logging

Logging is **off by default**. Set the environment variable `DEADCELLSLOCALMULTIPLAYERMOD_LOG` to any non-`0` value before launching the game to enable the mod log:

```powershell
$env:DEADCELLSLOCALMULTIPLAYERMOD_LOG = "1"   # then launch the game
```

Having issues? See the [Troubleshooting Guide](docs/TROUBLESHOOTING.md).

## Acknowledgements

- **Dead Cells Core Modding API (DCCM)** — https://github.com/dead-cells-core-modding/core

<!--
Keywords: Dead Cells co-op mod, Dead Cells split screen, Dead Cells local multiplayer, DCCM mod, Dead Cells local two-player
-->
