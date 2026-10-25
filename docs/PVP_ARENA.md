# PvP Arena — design notes

The local two-player deathmatch added in 0.1.14. Code lives in `Pvp/` and reuses the co-op base in
`Coop/`. The user-facing description is in `CHANGELOG.md` / `CHANGELOG_zh.md`; this file records *why*
it is built the way it is, so the next change does not redo the research.

Every claim about the game below was read out of the decompiled sources
(`C:\Games\Github\decompiled\src\dc`, game v35) or verified by compiling against the MDK reference
assemblies. **Nothing in this feature has been run in-game yet** — see §6.

---

## 1. Why it lives inside this mod

A standalone PvP mod would need the whole second-hero stack: spawn + relocation, per-player input
isolation, friendly fire, per-hero HUD, down handling, skin swap. That is ~90% of this repository.
So PvP is a **mode of the existing session**, not a separate mod:

| Reused as-is | Forced by PvP |
|---|---|
| Second hero spawn / relocate / repair | health independence |
| Per-player controllers + `MirrorGlobalDevice` | friendly fire always on |
| Friendly-fire paths (ranged gates + melee injection) | no follow-through-entry glue |
| Down state machine (`MarkDown` / `ReassertDownState`) | no run-ending death |
| Per-hero text HUD panels | an extra score panel on top |
| Skin choice, settings persistence, title menu | a `PvP arena` menu page |

---

## 2. The arena level: replace a generator, don't add a level

`dc.level.LevelStruct.get(user, levelInfo, rng)` (`dc/level/_LevelStruct.cs:49-2311`) is a **hard-coded
chain of ~120 string comparisons** over `level.id` — there is no registry and no reflection. An
unknown id falls off the end into `Unsafe.SkipInit(out LevelStruct result); return result;` —
an uninitialized value — and the next line in `LevelGen.generateGraph` calls `buildMainRooms()` on
it. So "just add a `level` row with id `PvpArena`" cannot work on its own.

Two ways out, both viable:

- **(a) New CDB row + hook the factory** — needs `data.cdb` (via `GenerateDiffCDB`), a world-map logo
  entry (the `LevelLogos.getLevelLogo` hook, because an id missing from
  `textureCoordinateByLevelKind` throws — `dc/ui/hud/LevelLogos.cs:54`), and hand-authored `.tmx`
  room files. This is the path documented for "adding new levels".
- **(b) Reuse an existing id and substitute its generator** — hook the same factory but *replace*
  the struct for a level that already exists in the table and already has rooms. No CDB, no logo,
  no TMX.

PvP takes **(b)**, with `TrainingDojo` as the host id.

### Why `TrainingDojo` specifically

It is the game's own "standalone short run" level: reached through
`LaunchMode.NewTrainingGame` (decompiled `dc/Main.cs:1081-1089`), whose level row has `mobs: []`,
`mobDensity: 0`, `nextLevels: []` and no exit. Its biome is `PrisonStart` and its rooms live in the
`Training` group (group index 100, i.e. `tiled/tmx/Training/`). Being training mode also buys
behaviour the arena wants and would otherwise have to imitate: achievements are suppressed
(`dc/_Achievements.cs:309`), and the HUD hides the score pieces (`dc/ui/_HUD.cs:462`).

Launching is one line, and it is the *same* line the game's own `TrainingDoor` uses
(`dc/en/inter/TrainingDoor.cs:848-851`):

```csharp
Main.Class.ME.launchGame(new LaunchMode.NewTrainingGame(false), (bool?)null, (double?)0.5);
```

The substitution is armed only while a match is live (`LocalCoop.IsPvp`), **not** merely because
the mode is switched on — a player who leaves PvP enabled and plays an ordinary co-op run that
wanders into the training door must get the vanilla dojo
(`Pvp/PvpArenaStruct.cs`, `Install`).

---

## 3. The graph: one room

`PvpArenaStruct.buildMainRooms` returns a single forced-template node, mirroring the game's
`QueenArena` structure (`dc/level/struct/QueenArena.cs:19-30`), which is the shortest precedent in
the shipped game for a closed arena — no `Entrance` node, no `createExit`, empty `addTeleports`.
`LevelGen.generateGraph` walks children from whatever node is returned
(`dc/level/LevelGen.cs:434-` ), so an `Entrance` is not required.

The room is `TrainingDojoBig` — a wide hall, already authored, already registered, already
`active: true` in the `room` sheet. Room ids usable for a duel:

| room | group | note |
|---|---|---|
| `TrainingDojoBig` | `Training` | wide hall, chosen |
| `TrainingDojoClassic` / `TrainingDojoDuel` / `TrainingDojoLong` | `Training` | alternates |
| `Arena1`…`Arena6` | `Common` | cross-biome, untested under `PrisonStart` |

Every optional generator stage is overridden **empty**, because each one is a way for a third party
or an exit to appear:

`buildSecondaryRooms` (side rooms/shops/treasure) · `buildTriggeredDoors` · `buildTimedDoors` ·
`buildZChallengeDoors` · `buildEliteRooms` · `addTeleports` · `buildLoreRooms` ·
`buildEssentialLoreRooms` · `applyDifficulty` · `finalize`.

`finalize` matters most: the vanilla `TrainingDojo.finalize` pushes `NpcId.TrainingKnight` onto two
rooms (`dc/level/struct/TrainingDojo.cs:220-228`). A sparring NPC in a duel is a third combatant
that can land the killing blow and score a bogus round.

Two deliberate non-changes:

- **No biome override.** The `Training` rooms were authored for `PrisonStart`; forcing another
  biome via `addGenData(specificBiome)` — the official tutorial's trick — would draw them with the
  wrong atlas.
- **No new mobs/loot config.** The host level row already has none, and the empty overrides keep
  the generator from adding any.

---

## 4. Rules: answer the existing hooks, don't add a second set

Damage and death already have gates built and debugged for co-op. PvP only changes what they
*answer*, so the two modes cannot drift apart:

| Read | Co-op | PvP |
|---|---|---|
| `PvpHealthIndependent` (`IsPvp \|\| !SharedHealth`) | menu setting | always independent |
| `FriendlyFireActive` (`IsPvp ? CanDealPvpDamage : FriendlyFire`) | menu setting | on, but **round-scoped** |

`CanDealPvpDamage` is `Pvp == Fighting && _freezeLeft <= 0`: outside a live round and during each
round's 1.5 s grace window, damage is refused, so nobody is scored against on the spawn.

Three gates consume `FriendlyFireActive`, and they must agree — one allowing hits while another
refuses is how "PvP with no damage" hides:

- `Hook_Hero.canBeHitBy` (targeting) — refuses a hit whose owner *is* the target (no self-harm).
- `Hook_Entity.isOpponent` (team) — the gate that actually lets a bullet land.
- `Hook_Weapon.doAreaEffect` (melee) — melee target collection is team-filtered upstream, so the
  partner is never a candidate and the per-weapon `hitFromWeapon` never runs for it; this
  injection calls the weapon's own `hitFromWeapon(partner)`.

Both heroes stay on `teamHero`; the team is **never** made to oppose itself (that variant let a
hero's own item damage it — see `AGENTS.md`, "Friendly fire"). Ownership is what separates
"partner's bullet" from "my own bullet".

### Death, and why the match never ends the run

A lethal hit is converted at `Hook_Hero.tryToPreventDeath` — **prevented before the death flow
starts**, because starting it fades/mutes audio that never comes back (the historical "P1 dies,
sound breaks" bug). The hero is held at `DownedLife = 1`, never 0: `Mob.fixedUpdate` clears
`nemesisTarget` at `life <= 0` and the boss AI dereferences it without a null check. Hiding is
visual only (`HideHero`), never `isFreeze` / `Entity.visible`; the input controller is parked
instead. All of this is the existing down machinery, reused.

Then why not just kill the loser and show a result screen? **Because in training mode that does
not end the run.** `Hero.kill()` branches to `deathRespawn()` when `game.isTraining()`
(`dc/en/Hero.cs:33035-33048`), and that respawn reads the level's `ZDoor` / `TrainingDoor` entity
arrays to pick a respawn point (`dc/cine/HeroDeathRespawn.cs:300-320, 369-390`) — in a single-room
arena with no doors, that is a null dereference. So:

- `Hook_Hero.kill` is gated in PvP: hold at 1, down, score, and **do not** call `orig`.
- `Hook_Hero.onDie` likewise never reaches the game's death flow while a match is live.
- `PvpState.MatchOver` is **terminal**: the banner stays up, the players leave through the vanilla
  pause menu (which already works in training mode — it is how one quits training today), and
  another match starts from the title menu.

This is also why `RoundBreak` / `MatchOver` score nothing: only a round being *fought* awards a
point, so leftover damage-over-time during a break cannot gift a second one.

---

## 5. Start spots: mirror, don't guess

The level's tile grid is **not reachable** from C#: no `Level.getCell`-style member exists in the
proxy (only `dc.h2d.col.Cell` ships, and nothing hands one out). Deriving floor cells from room
bounds is how a hero ends up inside geometry, so the arena does not try:

1. Take player one's **actual** standing cell (`hero.cx/cy` once it is in the arena room).
2. Mirror it across the room's centre for player two, clamped inside the room, one row higher.
3. If the mirror collapses onto P1's column, take the far wall.
4. Round restarts re-place both from these captured cells; hero two falls back to the co-op
   placement (`PlaceNextTo`) if nothing was captured, so a round can never start in the void.

The room the arena opens on is detected through `level.map.getRoomAt(cx, cy).rTemplate` compared
against a **lazily built** `dc.String`: constructing a Haxe string from a static field initializer
can run before the VM is ready to allocate one, which is the class of thing that crashes the game.

The arena also refuses to open early. `TryOpenArena` waits until *both* heroes' cells resolve to
the arena room, with a ~20 s timeout that opens the match wherever they are rather than hanging in
`Starting`. Reason: on a level entry the game scripts player one to auto-walk from the door cell,
so reading the start spots earlier would capture the corridor.

---

## 6. Verification status

**Compiled, not played.** `dotnet build -c Release` is clean (0 errors, 0 warnings) and the mod
packages (`res.pak` regenerated with the new locale entries). What compiling proves: every API
signature, type name and hook delegate used here exists in the shipped MDK proxy — the same method
that caught several errors in the official level-modding tutorial (see
`docs/DCCM_NEW_LEVELS.md` §5).

What it does **not** prove, and what to watch on the first run:

1. **The factory substitution actually fires.** `Hook__LevelStruct.get` is a *static* Haxe method;
   its existence in the proxy is verified, its runtime detour is not. Log line to look for:
   `[PvP] arena structure substituted for the training level`. Absent it, the run shows the vanilla
   dojo with training NPCs — and the NPCs can score rounds, so this must be confirmed first.
2. **The second hero appears on the training level.** Co-op spawns it from the `Game.initHero`
   hook; training mode is a launch path that code has never seen. Watch for
   `[LocalCoop] P2 is down` with no P2 visible, or only one hero in the room.
3. **`launchGame` from the title screen** while this mod's menu owns it — the fade/transition is
   the game's, but the call happens from inside a menu callback.
4. **Melee weapons that override `doAreaEffect` themselves** (Katana, Spear) bypass the base hook,
   so friendly fire may not apply for those. Known co-op limitation, inherited here.
5. **Down-based round end vs. an actual death screen**: no death cinematic should ever play.

Recommended first test with `DEADCELLSLOCALMULTIPLAYERMOD_LOG=1`, checking the log for the
`[PvP]` lines in order: `match armed` → `arena structure substituted` → `start spots (…)` →
`round 1 begins`.

---

## 7. Menu and panel constraints

The title menu clips anything past the bottom of the window, so every page keeps at most five
entries; the PvP page holds title + mode toggle + rounds + start + back = 5, and the previous
match's result is folded into the start row instead of taking a sixth. Strings go through the
mod's own localization (`Resourcefile/lang/main.zh.po` is the single source of truth;
`Tools/gen_mo.py` regenerates the `.mo`/`.en.po`/`.pot` — never edit those by hand).

The score panel follows the HUD rules this repository learned the hard way: labels only from
`Assets.makeText` parented to a game-owned object (a C#-built `dc.h2d.Object` never renders),
each marked absolute in the HUD flow so it cannot shove the rest of the HUD around, text written
with `set_text()` only (field assignment is silently ignored), and teardown through `remove()`.

### 7.1 The panel must be placed in screen space, not flow space

**Reported after the first build: "比分与结果显示在屏幕底部和 HUD 重合了".** That was real and it was
mine: the labels were given flow-local `y = 8 / 34 / 64`, but the host is the **HUD flow**, whose own
`absY` is ≈ 590 (the same number the hero readouts' anchor notes record). A flow-local `y = 8` is
therefore screen `y ≈ 598` — straight into the bottom HUD row.

Two further traps in the same lines, both now fixed:

- `Flow.calculatedWidth` is the flow's **content** width, not the stage width. With narrow HUD
  content it pulls the panel's horizontal centre off the middle of the screen.
- `dc.h2d.Window.getInstance` is not callable — the instance lives on `dc.hxd.Window.Class.inst`,
  and the stage size is its `windowWidth` / `windowHeight`.

So every coordinate is now `flow.absX/absY + screen-space offset`, with the stage width from the
window. `Ensure` also logs the geometry it actually built the panel from once per HUD
(`score panel anchored: flow=(x,y) stage=… panel y=…`), because a panel that lands in the wrong
place is silent — a run reporting panel `y ≈ 598` is this bug.

---

## 8. File map

| File | Role |
|---|---|
| `Pvp/LocalCoop.Pvp.cs` | State machine: arm/launch/rounds/score/rematch, start-spot capture, the two rule predicates |
| `Pvp/PvpArenaStruct.cs` | `LevelStruct` subclass for the arena + the factory hook that installs it |
| `Pvp/LocalCoop.PvpHud.cs` | Score / round / winner-banner labels, anchored in **screen space** (§7.1) |
| `Coop/LocalCoop.Hooks.cs` | `onDie`, `tryToPreventDeath`, `kill`, both friendly-fire gates now PvP-aware |
| `Coop/LocalCoop.cs` | `Install` (arena hook), frame order (`TickPvp`), follow-entry skip, teardown guard |
| `Coop/LocalCoop.P2HudGuard.cs` | Health HUD gate reads `PvpHealthIndependent` |
| `Coop/LocalCoop.Settings.cs` | `PvpEnabled` / `RoundsToWin` persistence |
| `ModEntry.cs` | `MenuState.Pvp` page + the `Rules` page entry |

## 9. Likely follow-ups

- **Round wins by anything but the partner** (a self-inflicted fall, the 999-style hazards in the
  hall) currently award the point to the other player unconditionally. Distinguishing "killed by
  partner" from "died to the level" needs the damage source at the death gate
  (`AttackData.carrier ?? source`), which `tryToPreventDeath` does receive.
- **No time limit / no double-KO handling**: simultaneous deaths score both.
- **Only one match at a time, no side betting/round picking**; a map vote would need a second room
  template, which the structure makes a one-line change (`ArenaRoomTemplate`).
- **Split-screen or per-player camera for PvP** — the shared camera currently follows both heroes,
  which is arguably wrong for a duel (it reveals both positions). Turning it off in PvP is one
  condition in `SharedCamera`, not a rewrite.
