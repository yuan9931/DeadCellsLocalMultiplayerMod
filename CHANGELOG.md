# Changelog

All notable changes to this mod are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the
project uses a `MAJOR.MINOR.PATCH` scheme where the PATCH digit moves for every
user-visible fix or feature.

## [0.1.28] — 2026-10-27

### Fixed

- **Player two can use the reforge again (independent equipment).** The underground forge
  (`dc.ui.ForgeUnderground`) builds its whole reforge list from `Game.ME.hero.inventory`
  (`getEquippedWeaponOn` / `getBackpackWeapon` / `getActiveOn`) and pushes a rerolled item back
  through that same hero's `onEquipedItemsChange`. The mod keeps the global hero on player one, so
  player two's forge listed player one's gear — nothing of player two's to pick, and a reroll would
  have re-rolled the wrong hero's item. The hero that opened the forge is now recorded from
  `SmallBlacksmith.onActivate` and the screen's own work (list build, `reforge`, item description,
  validate, update) runs as that hero. Gold and cells need no special case: gold lives on
  `Game.data.money` and the forge spends only gold, so the one shared purse pays either way.

## [0.1.27] — 2026-10-27

### Fixed

- **The full-screen map can be panned again.** `MiniMap.update()` treats a non-null
  `followEntity` as "follow mode" and dispatches the pan only behind that test, so a follow target
  makes the map re-centre on it and never reach `handleWorldMapMovements()` — the player's stick or
  arrow input is silently dropped and the map looks frozen. The minimap hook restored
  `followEntity` on the full-screen branch (to hand the corner map back to the game), which
  re-armed follow mode on every frame of the browse screen. The full-screen branch now leaves the
  field exactly as the game set it and just runs the game's own update — follow mode there is the
  player's own toggle, and the game re-sets `followEntity` itself when the map closes. The
  full-screen check also moved ahead of the "camera off" path, which restored the follow target on
  the way past and re-locked the map for anyone playing without the shared camera.

## [0.1.26] — 2026-10-25

### Fixed

- **A downed hero no longer draws aggro.** With independent health a hero at 0 HP is held at 1 and
  kept in the level (0 would crash the boss AI), so the game's own "drop the target" rule never
  fires for it — mobs kept walking to the spot player two fell instead of fighting the player still
  standing. A downed hero is now neither targetable nor detectable, and a mob that was already
  locked on hands its aggro to the surviving partner. Player one only looked fine because a downed
  P1 is moved onto the survivor for the audio listener.
- **Mobs attack normally again after a quit-and-continue.** The "do not target me" flags set while
  a hero was down survived the reload on the hero object, while the down state itself did not — a
  living hero that nothing would attack. A hero that is not down clears both flags every frame.
- **A downed hero stays down across a quit-and-continue.** The save only records the hero at 1 HP,
  so reloading used to revive it; the down state is now remembered in the mod's own settings file
  and re-applied to a continued run (and dropped as stale when the hero is healthy, so a new run
  never inherits it). Only the forge revives.

## [0.1.25] — 2026-10-25

### Changed

- **Player two's HUD is merged into the vanilla one.** Instead of a readout of its own, player
  two's flask, weapons, active skills and backpack continue player one's icon row, and its scroll
  tiers and life bar sit in a second bar row underneath — laid out by the game's own HUD flows, so
  the column no longer shifts on equipment changes, level changes or HUD-size changes. Player two's
  mutations get icons of their own on the same line as player one's, starting above player two's
  flask.
- **The vanilla HUD stays visible with independent equipment.** The redirects keep player two's
  writes off the shared slots and life bar, so the vanilla icons and bar describe player one
  correctly; the text readouts are gone (both blocks stay blank while the merged HUD is up).
- **The vanilla life bar no longer grows with max life**, and it never reaches past the last icon.

### Fixed

- Player two's gear no longer appears in player one's slots. A mutation pickup repaints the shared
  slots from `game.hero.inventory`, and the global hero is player two for the whole pickup window.
- Player two's flask icon no longer disappears when it picks something up.
- Player two's flask charges survive a level change (a re-created hero's flask board seeds itself
  from the run-global count, which is player one's).
- Player two's mutation icons are shown at all — the vanilla stock only walks the global hero's bag.

## [0.1.24] — 2026-10-25

### Fixed

- **No more camera flicker during cutscenes.** The shared camera aimed the viewport every frame
  while the game's cinematic script moved the same camera (its own `goTo`/`track`/zoom) — two
  writers alternating on one view, which showed up as flicker for the whole cutscene. While a
  cinematic is running (`Game.curCine`) the shared camera now stands down entirely: it hands the
  view off once on the transition and touches nothing — no aim, no zoom write, no re-track, and
  its per-frame zoom-restore and tracked-entity safety nets are suppressed — until the cutscene
  ends, then resumes following the heroes. The frozen view is exactly what the scene scripted.

## [0.1.23] — 2026-10-25

### Changed

- **The curse now follows the health mode.** Shared health means one curse on **both** heroes: the
  event runs normally on the hero the game addressed (its own counter, its own "next hit kills"
  affect and its own head label) and the resulting count is mirrored onto the partner — counter,
  affect and label, with no second modal popup and no second jingle — and either player's kills
  reduce it (the game's mob-death flow already reduces every hero in the level). The mirror is not
  gated on the equipment mode because most curse sources (cursed chests, money doors, gifts)
  hard-code `_level.game.hero` = player one: a curse player two triggered used to appear only on
  player one. Independent health means the curse is **per-hero**: no mirror — the curse belongs to
  whoever the game addressed (for the hard-coded global-hero sources, whoever is unambiguously
  mid-interaction: the mod remembers which hero activated the chest/door via
  `Hook_Entity.onActivate`), a mob death lifts only the **killer's** curse (`reduceCurse` is gated
  by the dying mob's `lastHitHero`), and a prevented lethal death consumes only the dead hero's own
  curse. The per-frame `curseCounter` copy in `OnFrameUpdate` runs only in shared health.

### Fixed

- **Independent health: a cursed hero's curse now takes effect instead of killing the wrong hero.**
  The mod answers a lethal hit on a hero before the game's own gate (to turn it into a down while a
  teammate is alive), which silently swallowed the vanilla "cursed: the next hit kills" refusal —
  a cursed player two just went down with the curse still armed, and player one then died to it
  through the vanilla gate once player two was down. A prevented lethal hit on a cursed hero now
  consumes that hero's curse (counter, affect and label), so the curse costs the cursed hero its
  standing. The last hero standing still dies to the curse through the vanilla gate, as in the base
  game.
- **Independent health: player two's `kill()` is no longer redirected to player one.** The
  `kill` hook still carried the shared-health behaviour (either death ends the run, so a player-two
  kill was replayed on the global hero) — with independent health, any kill()-style death aimed at
  player two (hazards, damage paths that skip the death gate) killed **player one** instead. It now
  downs player two while a teammate is alive (consuming its curse like every other death gate) and
  ends the run through the global hero only when everyone is down.

## [0.1.22] — 2026-10-22

### Fixed

- **Independent equipment: player two keeps its own gear when a level transition re-creates it.**
  Entering a forge level (the mutation/blacksmith room after a boss) turned player two's whole
  backpack into player one's — weapons, skills, flask and scroll tiers. The transition tears the old
  level down *before* `Game.initHero` runs, so there is nothing for the mod to move: hero two is
  destroyed, the per-frame safety net saw a destroyed hero two and reset the run state (bag
  included), and the hero was then rebuilt by `SpawnSecondHero`. A freshly constructed `Hero` does
  not inherit anything — its constructor assigns `game.data.inventory.clone()`, and
  `game.data.inventory` is a clone of **player one's** bag (`GameData.sync`) — and the adopt path
  then accepted that copy as if it had been restored from a save. Now the safety net recognises
  "player two's hero is gone, player one is alive" as a mid-run level change, keeps the bag and
  player two's own life across the teardown, and the respawn hands the bag straight back
  (`RestoreOwnInventory`, with the loadout rebuilt the same way the clone path does it). A hero the
  mod just built is also no longer mistaken for a save-restored one, so its `game.data` copy can
  never be adopted as player two's own gear.

### Changed

- **Frame-time pass: the per-frame work is now sampled where a one-frame delay cannot be seen, and
  the per-frame path allocates nothing.** Every Haxe call costs far more than the C# around it, and
  the mod was making thousands of them per frame for answers that only change a few times per run:
  - The hero readouts (independent equipment) are re-read at 10 Hz instead of 60 — one snapshot
    walks two inventories, two skill managers, the main-skill list and the whole bag for the
    talisman, and the fastest-moving thing it prints is a cooldown rounded to one decimal. The
    vanilla-HUD hiding and the cooldown-overlay pinning still run every frame, because those must
    not lag by even one frame.
  - The idle-mob fallback target is cached per mob for 100 ms. Every mob's pre/fixed/post tick goes
    through the mod's wrapper, and for a mob with no target that wrapper measured two world
    distances plus two `canBeDetected()` calls — several times per frame per mob, for a choice that
    cannot change that fast. A mob with an explicit target is still read fresh, so a mob that has
    just been hit aims at the hero that hit it on the next tick.
  - `Hero.isChargingSkill()` (walks both skill managers) is sampled at 5 Hz for the stuck-charge
    watchdog, the animation name behind the carry-state repair at 10 Hz, `BindMainSkills` at 10 Hz,
    and the friendly-fire team cleanup on a level change plus a 1 s safety net instead of every
    frame.
  - The `headPopLoop` state-animation lookup is cached per hero instead of scanning the anim list
    and comparing strings twice per frame; the teleport-hint wording is cached per hint instead of
    going through the localization lookup every frame; the room-reveal flag is written on a
    cell/level change instead of every frame; the HUD particle blanking runs every sixth frame; the
    readout block is only re-placed when its geometry actually changed.
  - `SharedCamera` no longer allocates two `List`s and an array per frame, and only writes the
    viewport zoom when it actually moved. `UpdateTeleportHints`, `IsHeadOut` and the per-frame
    adoption call no longer allocate closures.

### Added

- **A frame-cost diagnostic.** With `DEADCELLSLOCALMULTIPLAYERMOD_LOG=2` the periodic diagnostics
  now print `[LocalCoop] cost mod=…ms avg …ms peak over N frames` — the cost of the mod's own
  per-frame pass. If the frame rate is still low while that number is small, the cost is inside a
  game hook (mob AI, traps) rather than in the per-frame pass.

## [0.1.21] — 2026-10-21

### Changed

- **Teleporter pads move the pair again.** One hero riding an in-level teleporter pad drags the
  partner along (`Hook_Hero.onTeleportation`, restored after 0.1.18 had removed it with the other
  automatic carries). The teleport key itself stays a one-hero move: the forward hold brings the
  holder onto its partner, and the return hold brings **only the holder** back to the spot it left —
  the partner is never teleported by someone else's return.
## [0.1.20] — 2026-10-21

### Added

- **Both players can have their detached homunculus heads out at the same time.** Each head is
  fully independent — its own pad, its own body lock, its own recall — so one player throwing or
  recalling a head no longer touches the other's. The block was the *skill's* `onEnd()`, not
  `prepare()`: `onEnd()` runs when the throw animation finishes — hundreds of milliseconds after
  the press — and its first act is a `while` loop that kills **every** `en.Homunculus` in the
  level bucket before creating the new one. Hiding the other player's head for the duration of
  `prepare()` therefore protected nothing. `OnHomunculusSkillEnd` (hooked on
  `dc.tool.mainSkills.Hook_Homunculus.onEnd`, which is as hookable as `prepare`) now wraps `onEnd`
  in the same `WithOwnHeadOnly` filter, so the kill loop only ever sees the throwing player's own
  old head.

## [0.1.19] — 2026-10-21

### Added

- **PvP matches have a health multiplier.** The `PvP arena` page gained a `Match rules` sub-page
  holding `Rounds to win` and the new `Health: 1x / 2x / 5x / 10x` cycler. At 1× a single solid hit
  often ends a round before either player has engaged, so the larger pools let a duel between two
  equally geared heroes actually play out. The multiplier scales **both** heroes' pools from what
  they enter the arena with — never a flat value — so gear and scrolls still matter and the duel
  stays symmetrical. Only the four offered values are honoured when the config is loaded, so a
  hand-edited file cannot ask for a pool the arena was never balanced around.
- **Round one of a match is a warm-up**: it does not score, and a `Match starts` banner appears
  after it, so neither player loses the match to a stray hit before the arena has even been read.
  The multiplier is applied to round one explicitly (`StartRound` → `ReviveForNewRound` only runs
  from round two onwards, which had left every first round at the vanilla pool whatever the setting
  said).

### Notes

- In-game verified this session: the warm-up round, the `Match starts` banner, and the health
  multiplier taking effect on round one.

## [0.1.18] — 2026-10-21

Everything below is the delta since **0.1.17**.

### Changed

- **The teleport key is now the only way the two heroes regroup — every automatic carry is gone.**
  Three mechanisms still moved a hero behind the players' backs, all left over from before the
  button existed (`Coop/LocalCoop.Teleport.cs` is the replacement for all of them):
  - **The level-entry follow glue.** For up to 360 frames after a level entry it re-placed hero two
    on player one every frame, so hero two "rode" the game's scripted door-to-entry walk-in. Hero
    two now lands at the door and stays there: player two walks it in, or uses the button. This was
    the one players noticed — it was why P2 always ended up next to P1 on a new level.
  - **The boss-intro position sync** (`Coop/LocalCoop.Boss.cs`, file deleted). At fight start, or
    when a pre-fight cinematic ended, it dragged the hero that was outside the arena into it.
    Walking into the arena is now the stranded player's own choice — and the teleport crosses the
    sealed door anyway, which the drag never did.
  - **The teleporter-pad drag** (`Hook_Hero.onTeleportation`). Whoever used a same-level teleporter
    pad yanked the partner onto the destination with it. A pad now moves one hero alone, exactly
    like the vanilla single hero.
  - What deliberately **stays**: the level-transition relocation (`RelocateSecondHero` — hero two
    does not exist on the new level until it is placed there, so that is spawning, not carrying)
    and the downed hero's per-frame audio repositioning (`FollowAliveHero` — the game spatialises
    positional audio around the global hero, and a downed hero is not being controlled anyway).
- **A downed partner is a teleport destination, not a blocker.** With independent health the button
  used to switch itself off whenever either hero was down — which stranded the survivor far from
  the body it had to walk back to, and left the pair split for the rest of the biome. A hold now
  moves the survivor onto **where the partner fell** (second hold still returns it, 10 s window).

### Fixed

- **Teleporting to a downed partner lands on the body's real spot, not wherever the corpse is
  standing.** The fall spot is recorded inside `MarkDown` (level + cell + in-cell offsets), because
  a downed hero's live position stops meaning "where it died" the moment it goes down: a downed
  player one is re-placed on the survivor every frame for the positional-audio listener, and either
  hero relocates on a level change. The naive destination (`PlaceNextTo(partner)`) would have put
  the mover on its *own* position in exactly that case. A fall on another level has no usable spot,
  so the hold logs and does nothing; the downed hero's own button is dropped (its controller is
  parked) and its stale return window dies with the down; a destroyed partner — a real death under
  shared health — resets the whole teleport state, so nothing ever aims at a corpse.

### Notes

- In-game verified this session: plant-trap damage on player two, teleport with a downed partner
  (destination and return), the entry-follow and pad-drag removals.

## [0.1.17] — 2026-10-21

Everything below is the delta since **0.1.16**.

### Fixed

- **The plant trap (Chompoo) now fully works for player two** — bite, damage, bounce, the lot.
  It took three rounds of field reports to see the whole machine: first P2 didn't trigger it at
  all (`onTouch`'s gate drops any hero that is not `Game.ME.hero`, and `Hero` is not `Mob`);
  wrapping the touch entries then made P2 walk onto an open mouth and get bounced without ever
  being bitten — the per-frame half (`fixedUpdate`'s `heroWasInside` scan, its `close()` trigger,
  its leave-check) also addresses the global. The last round found the actual bite path:
  `open()` schedules a **delayed "close" cooldown callback** (~1.25 s) that the level ticks
  *outside* the plant's `fixedUpdate` — so even the wrapped tick never carried the damage.
  `Coop/LocalCoop.Plant.cs` now runs the whole state machine with the global pointed at the
  remembered hero inside the mouth, all six entries covered (`onTouch`, `bumpEntity`,
  `fixedUpdate`, `postUpdate`, `close`, `open`/`onCooldownEnd`). Verified in game with a one-shot
  log: the damaging close now runs as `global=P2`, P2 248→174, P1 untouched.
- **The other six global-hero traps see player two too** (`Coop/LocalCoop.Traps.cs`): retractable
  spikes (P2 standing on them never made them behave), the crumbling platform and crumbling floor
  (P2 standing on them never crumbled them), the swinging blade (P2 touching it **hurt P1**), the
  pulley (P2 jumping on **teleported P1** onto the rope) and the flipping platform (P2's landing
  never flipped it — fixed by calling the tracked live platforms' own `onHeroLand`, deliberately
  *not* by broadcasting the land signal, which the players' own skills also subscribe).
- **Mobs, bosses and interactables stop resolving player two through the global**
  (`Coop/LocalCoop.MobAi.cs`, `Coop/LocalCoop.Interactables.cs`) — a full-codebase audit (284
  files, 1824 `Game.ME.hero` references) found every site that bypasses the game's own
  per-entity target machinery, which is sound for co-op. Mob side: each flagged mob's AI tick /
  hit / death body now runs with the global pointed at **the mob's own target** (or the
  strictly-nearer hero), which retires the aim/victim/buff-from-the-global family in one pattern:
  Death's hover, choke, chains and soul-ultimate (which could `kill()` P1 directly), DookuBeast's
  grab and fire pillars, Collector/Queen stomp knock-offs, Beholder/MamaTick aiming, the three
  Amazons' shot victims and dive hits, Medusa's gaze, the haunted armour / shop mimic / screamer
  wake-aggro, the aggressive-mobs mutator hard-lock, and on-kill/on-hit perk beneficiaries. Plus:
  the intro invincibility now covers both heroes, `Golem.setAttackTarget`'s nemesis overwrite is
  repaired, the crow's captured hero rebinds to P2, and hero-side self-perks
  (`P_DmgRevenge`-type buffs) land on the hero that earned them. Interactable side: the healing
  fountain's "may I drink / is anyone hurt" gate (P2 was refused while P1 was at full HP),
  scratchable tiles (only P1 could break them), the keyhole (checked *and spent* keys from P1's
  bag), elevator doors and ride state (P2 alone at the shaft got nothing), NPC proximity
  auto-activation including the forge (the revive-at-forge flow depends on it), `pickOnTouch`
  auto-collect, hidden-trigger key gates, conditional unlockables (either hero may hold the key
  item), the necromancer wall and deployed-item buff ownership. Verified in game: P2 now takes
  plant damage. The mob/interactable re-pointing is new at this build — reports welcome.

### Notes

- **Deferred (documented in code)** rather than half-fixed: scripted cinematic closures,
  Collector's on-last-hit relocation, parry stagger ownership, grenade chicken/counter ownership
  (the proxy exposes no owner field). These need a second, narrower project each.

## [0.1.16] — 2026-10-21

Everything below is the delta since **0.1.15**.

### Added

- **A teleport hint above each hero.** On every level change each hero shows, above its head, which
  button that player holds to reach the partner — **"Hold T to teleport"** for the player on the
  keyboard, **"Hold L3 to teleport"** for the player on a pad — for 5 s. After that hero teleports,
  its hint changes to **"Hold T / L3 to go back"** for 10 s, which is exactly the length of the
  teleport's return window, so the reminder disappears when the return trip stops being available.
  (`Coop/LocalCoop.TeleportHint.cs`.)
  - The button named is the one that player's teleport actually reads (`TeleportButtonDown` makes the
    same keyboard/pad choice), so the hint cannot disagree with the key that works.
  - **The two hints are stacked apart** — player two's label sits one line higher and further to the
    side — because the heroes can occupy the same cell (the mod's walk-push even lets one carry the
    other) and two labels at one offset would overlap into an unreadable smear.
  - The labels are `dc.ui.Text` from `Assets.makeText`, parented to the HUD root and repositioned
    from `get_headX/Y` through `Level.toGlobalX/Y` every frame. **Not `dc.ui.PopText`**: its
    constructor hard-codes a float-up animation and a `1200 + len*50` ms lifetime, so 5/10 s are not
    expressible through it.
  - Skipped in PvP, which has no manual teleport (it would defeat the arena's mirrored start spots).
  - New strings localized (Simplified Chinese, `.mo` catalogs regenerated from the `.po`).

## [0.1.15] — 2026-10-21

Everything below is the delta since **0.1.14**.

### Added

- **PvP arena — a local two-player deathmatch** (`Pvp/LocalCoop.Pvp.cs`,
  `Pvp/PvpArenaStruct.cs`, `Pvp/LocalCoop.PvpHud.cs`). Both players fight in a closed arena with
  **their own health** and **forced friendly fire**; a lethal hit scores a round for the other
  player, both heroes are restored and put back on their start spots, and the first to
  `Rounds to win` takes the match. The score, the round state and the winner banner are drawn in
  the **top-left corner** of the screen.
  - **The arena is the game's own training level, not a new CDB level.** The mod launches a
    training-mode run through `Main.launchGame(LaunchMode.NewTrainingGame)` — the same call
    `TrainingDoor` makes — and substitutes the level's room graph by hooking the level-struct
    factory (`Hook__LevelStruct.get`) with `PvpArenaStruct` **while a match is armed**. That
    factory is a hard-coded chain of id comparisons with no registry (decompiled
    `dc/level/_LevelStruct.cs`), so replacing an existing level's generator is what avoids a new
    `data.cdb` row, a world-map logo entry and hand-authored `.tmx` files entirely.
  - **One room, nothing else** — the shape the game's own `QueenArena` structure uses (single
    forced-template node, no `Entrance`, no `createExit`, empty `addTeleports`). Every optional
    generator stage (`buildSecondaryRooms`, `buildTriggeredDoors`, `buildTimedDoors`,
    `buildZChallengeDoors`, `buildEliteRooms`, `buildLoreRooms`, `applyDifficulty`, `finalize`)
    is overridden to empty, so there are no mobs, no loot, no side rooms and — unlike the vanilla
    training dojo — **no sparring NPC** that could land the killing blow and score a bogus round.
    The room reused is the game's `TrainingDojoBig`, so no art is added.
  - **A match never ends the run, on purpose.** In training mode `Hero.kill()` branches to
    `deathRespawn()` instead of the death flow (decompiled `dc/en/Hero.cs:33035-33048`), and that
    respawn looks for a `ZDoor`/`TrainingDoor` entity which does not exist in the single arena
    room — so a "kill somebody to finish" design would crash or silently restart the fight.
    Instead a lethal hit becomes a down plus a point (held at `DownedLife`, since a hero left at
    0 HP in a level crashes the mob AI), `Hook_Hero.kill` is gated in PvP for the same reason,
    and leaving a match is the vanilla pause menu's job. Another match starts from the menu.
- **PvP reuses the co-op rule gates rather than registering a second set of hooks.** The three
  friendly-fire paths (the `canBeHitBy` targeting gate, the `Entity.isOpponent` team gate and the
  melee `Weapon.doAreaEffect` injection) all read one predicate, `FriendlyFireActive`, and
  everything that used to read `SharedHealth` reads `PvpHealthIndependent` — so a match cannot be
  half-enforced (one gate allowing damage while another refuses it is exactly how a "PvP with no
  damage" bug hides). Damage is mandatory but round-scoped: refused outside a live round and
  during each round's 1.5 s grace window, so nobody can be scored against on the spawn.
- **PvP takes the top row of the mod's menu.** `PvP arena` replaced the old title line on the root
  page, with `Start local co-op` still on the second row. The PvP page holds the mode switch, the
  round target (1–10) and `Start a match`, which arms the mode and the session and launches the
  arena in one press; the same row reports the previous match's result so the page stays inside
  the five entries the title menu can show. **PvP mode** and **Rounds to win** persist with the
  rest of the setup. `Mod info` folded into the Mod settings header (version line) to keep every
  page within that limit.
- New menu strings localized (Simplified Chinese, `.mo` catalogs regenerated from the `.po`).

### Changed

- **Hero two is no longer glued to player one while a match is on.** The level-entry follow window
  (which exists so the second hero rides player one's scripted walk-in instead of stranding at the
  door) is skipped in PvP: the two players must start on opposite sides of the arena. Their start
  cells are captured from player one's *actual* standing cell and mirrored across the room's centre
  — deliberately not derived from the tile grid, which the Haxe proxy does not expose; guessing
  floor cells from room bounds is how a hero ends up inside geometry.
- **Tearing the heroes down no longer disarms an armed match.** Starting a match launches a new
  run, and the previous run's hero destruction lands in between; the reset now keeps a match in
  `Starting` alive instead of clearing the one that was just armed.
- **PvP does not touch the equipment setting.** A match follows the **shared equipment** toggle
  like any other run: with it on, the two heroes hold one bag (so the duel is a pure mirror), and
  with it off each carries their own. Only health and friendly fire are forced, because those are
  the two rules a deathmatch actually needs.

### Fixed

- **The PvP score panel drew over the bottom HUD instead of the top of the screen** (found on the
  first in-game look). The labels were positioned in **flow-local** coordinates, but their host was
  the HUD flow, whose own `absY` is ≈ 590 — so `y = 8` landed at screen `y ≈ 598`. The horizontal
  placement was derived from `Flow.calculatedWidth`, which is the flow's *content* width and pulls
  the centre sideways whenever the HUD content is narrower than the window. The panel now hangs off
  the **HUD's own root** (`hud.root`, the full-screen `Layers` every HUD flow is built on, whose
  origin is the screen's (0,0)) and uses plain screen constants, the same recipe the pause window's
  picker overlay uses with its own full-screen background. It also logs the geometry it was built
  from once per HUD, so a mis-placed panel cannot fail silently again.

## [0.1.14] — 2026-10-20

Everything below is the delta since **0.1.13**.

### Added

- **Shared equipment is back, and it really means everything** (`SharedEquipment`,
  default **shared** again). 0.1.13 had retired the shared-backpack mode to a constant;
  it is a setting again (Rules page toggle, persisted value honoured on load). With it on,
  the two heroes hold one `Inventory` — weapons, skills, reserve items, the ring and the
  mutations are one shared set — and the per-frame pass extends that to the three things
  that live on the hero instead of the bag: `SyncSharedFlask` converges the flask charges
  (each hero's own `Heal` main skill owns them, so they sync by difference against the
  last shared count, like the life pool — a plain copy would undo a drink player two took;
  the HUD counter is repainted after a convergence), `SyncSharedTiers` recomputes player
  two's scroll tiers from the shared bag (the game only recomputes for the hero that
  picked the scroll up), and `SyncSkillCooldowns` converges both heroes onto the larger
  remaining cooldown (multi-charge skills keep their own timers).
- **P2's graphical health bar for shared gear + independent health**
  (`Coop/LocalCoop.P2Hud.cs`, `UpdateP2LifeLine`). That combination had no P2 life readout
  at all: the text blocks are independent-equipment only, and the vanilla life bar
  describes only the global hero. The bar is the one the mod already had in 0.1.11 (a
  plain `h2d.Object` panel sibling of `heroLifeBar` — a FlowBox would overwrite its x/y —
  with `Graphics` shell + dark inner + percent-coloured fill + top highlight and a
  centred life/maxLife number), anchored to the right of P1's bar and vertically centred
  against it, re-read from `lifebar.curState` every frame. A downed hero reads 0, like the
  full readout. Its shell is 1px (the 0.1.11 original's 6px border reads far too thick
  next to the vanilla bar).

### Changed

- **Both players may pick the same input device.** The device cycle no longer skips the
  other player's choice, and the menu hint says so. Two pads on one controller is a
  legitimate way to play (both heroes respond to that device); the isolation is per
  `Controller` object, not per device.
- **A scroll asks only one player while equipment is shared**: the scroll lands in the one
  shared inventory and raises both heroes, so the double-pick chain would add a *second*
  scroll (`ArmFromScreen` returns early in shared mode). The picker-first chain stays for
  independent gear.

### Notes

- In-game verified this session: shared-equipment restore, the P2 graphical bar in
  shared-gear + independent-health, the 1px shell, repeated devices, the shared-mode scroll
  single pick.

[0.1.21]: https://github.com/yuan9931/DeadCellsLocalMultiplayerMod/compare/0.1.20...0.1.21
[0.1.20]: https://github.com/yuan9931/DeadCellsLocalMultiplayerMod/compare/0.1.19...0.1.20
[0.1.19]: https://github.com/yuan9931/DeadCellsLocalMultiplayerMod/compare/0.1.18...0.1.19
[0.1.18]: https://github.com/yuan9931/DeadCellsLocalMultiplayerMod/compare/0.1.17...0.1.18
[0.1.17]: https://github.com/yuan9931/DeadCellsLocalMultiplayerMod/compare/0.1.16...0.1.17
[0.1.16]: https://github.com/yuan9931/DeadCellsLocalMultiplayerMod/compare/0.1.15...0.1.16
[0.1.15]: https://github.com/yuan9931/DeadCellsLocalMultiplayerMod/compare/0.1.14...0.1.15
[0.1.14]: https://github.com/yuan9931/DeadCellsLocalMultiplayerMod/compare/0.1.13...0.1.14

## [0.1.13] — 2026-10-20
