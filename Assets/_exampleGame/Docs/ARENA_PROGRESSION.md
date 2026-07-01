# Arena: Progression (Meta + Session)

Implementation in the Vampire Survivors style: **session** XP/level and card drafts, plus **meta** between runs through `Neo.Save.SaveProvider`. Waves are handled by `ArenaSurvival.UseCases.ArenaSurvivalDirector`; phase 2: the coordinator is not hardwired into the director.

## Folder Structure (Clean Architecture)

**Example root** `Assets/_exampleGame/RogueliteArena/`:

- `Composition/` - application composition: `RogueliteArenaLifetimeScope` (VContainer)

Each feature under `Features/<Name>/` is split into layers (folders = layers):

| Feature | Domain | UseCases | Presenter | View | Infrastructure |
|------|--------|----------|-----------|------|----------------|
| **ArenaSurvival** | session contracts (`IArenaSessionView`), `ArenaSimulationRole` | wave orchestration (`ArenaSurvivalDirector`) | - | `ArenaSurvivalHud` | `ArenaSurvivalSession`, `ArenaSurvivalProceduralSetup` |
| **ArenaWaves** | `IArenaWaveSchedule`, plans, difficulty curve (model) | `ArenaLocalWavePlanner`, `ArenaWavePlanValidator` | - | - | Creator/parser, `ArenaLinearWaveSchedule`, preset SOs and VS curve |
| **ArenaCombat** | - | - | - | - | player, enemy, companion, AINpc listener |
| **ArenaCamera** | - | - | - | - | `ArenaFollowCamera` |
| **ArenaAi** | constants `ArenaAiSourceTags` | - | - | - | task bus, triggers, aux LLM |
| **ArenaBootstrap** | - | - | - | - | `ExampleRogueliteEntry`, LLM/Lua hotkeys |

**Progression** `Features/ArenaProgression/`:

- `Domain` - state, enum, `IArenaCombatStats`
- `UseCases` - use cases for meta save, XP, rolling, and applying upgrades
- `Presenter` - `ArenaUpgradeDraftPresenter`, placeholder `ArenaWaveUpgradeCoordinator`
- `View` - `ArenaUpgradeChoiceView`, `ArenaUpgradeCardWidget` (TMP + UI)
- `Infrastructure` - ScriptableObject and content (`ArenaProgressionContent`, configs, `ArenaUpgradeDefinition`), runtime services (roll, save, Lua, session host, hub, combat model, companion brain)

Namespaces mirror the layer: `CoreAI.ExampleGame.<Feature>.<Domain|UseCases|Presenter|View|Infrastructure>`.

## ScriptableObjects

Create default assets from the menu **CoreAI Example -> Arena -> Generate Progression Assets (Defaults)** (writes to `Assets/_exampleGame/Settings/Progression/`).

> **Note:** progression is **opt-in and generated on demand**. The `Assets/_exampleGame/Settings/` folder is not checked in, and in the shipped `RogueliteArena.unity` scene the `Arena Progression Content` and `Arena Unit Baseline Config` fields are **unassigned** - so `ArenaProgressionSessionHost` (XP/level, meta-save, draft UI) is **not active** by default. Run the menu above and assign the generated assets to enable it.

- `ArenaUnitBaselineConfig` - starting stats for the player and companion
- `ArenaRunBalanceConfig` - references to `LevelCurveDefinition` (session + meta), XP per kill, team split, rarity multipliers, card limits
- `ArenaProgressionContent` - upgrade registry and references to `ChanceData` (rarity, categories by rarity, stat-pool weights)
- `ArenaUpgradeDefinition` - id, title, description, kind, rarity, statDelta
- `ArenaUpgradePresentationConfig` - frame sprites/materials by rarity
- `ArenaPersistenceConfig` - meta key for SaveProvider (optional; otherwise the gateway uses the default key in code)

Neoxider: [Random / ChanceData](https://github.com/NeoXider/NeoxiderTools/tree/main/Assets/Neoxider/Docs/Tools/Random), [Progression / LevelCurveDefinition](https://github.com/NeoXider/NeoxiderTools/tree/main/Assets/Neoxider/Docs/Progression).

### ChanceData Indexes

- **Rarity:** 0 Common, 1 Rare, 2 Epic, 3 Legendary
- **Common/Rare categories:** 0 = Stat
- **Epic:** 0 Stat, 1 PassiveSlot
- **Legendary:** ChanceData indexes `0` Stat, `1` OfferExtraChoices, `2` LegendaryDoublePick (mapping in `ArenaUpgradeRollService.TryMapCategory`)
- **StatUpgradeWeights:** index = order of stat upgrades in the `ArenaProgressionContent.upgrades` list (the first N are stat kinds only in the default generation)

## Scene and Wiring

On `ArenaSurvivalProceduralSetup`, assign **Arena Progression Content** and **Arena Unit Baseline Config**. At startup it creates `ArenaProgressionSessionHost` (XP per kill, meta load/save, Lua).

Draft UI prefab: add `ArenaUpgradeChoiceView` + a pool of five `ArenaUpgradeCardWidget` instances, then assign the reference in `ArenaProgressionSessionHost.draftView`. Without draft UI, Lua can still call it (`arena_open_draft_debug`), but the view will not open if view is null.

**Debug:** `ArenaProgressionDebugHotkey` - key **L** (default) opens the draft.

### Wave Difficulty Curve (VS-style)

Nonlinear Vampire Survivors-style difficulty: **overall harder toward the end of the run** (ramp by wave progress), while **individual waves can be softer** because of sine waves over enemy count and stats.

- **Assignment:** **Wave Difficulty Profile** field on the `ArenaDirectorSettings` asset (referenced by `ArenaSurvivalProceduralSetup.directorSettings`). `ArenaSurvivalDirector.Init` reads it from `directorSettings.WaveDifficultyProfile`; if it is empty, only the plan / linear schedule is used.
- **Asset:** `ArenaVsStyleWaveDifficulty` - menu **Assets -> Create -> CoreAI Example -> Arena -> VS-style Wave Difficulty**, or **CoreAI Example -> Arena -> Generate VS Wave Difficulty Asset** (writes to `Assets/_exampleGame/Settings/Arena/ArenaVsWaveDifficulty.asset`).
- Multipliers are applied on top of the Creator plan / local plan / linear schedule (enemy count, HP, damage, speed, spawn interval). Telemetry keys are `arena.wave.vs.*_mult`.

## Lua API (Creator and Programmer)

Registered through `GameLuaBindingsExtensibility` + `ArenaProgressionLuaBindings` (the core aggregates them in `AggregatingGameLuaRuntimeBindings`).

| Function | Purpose |
|--------|------------|
| `arena_add_session_xp(n)` | Session XP (team split is handled through the live team member hub) |
| `arena_add_meta_xp(n)` | Meta-XP + level recalculation through the meta curve |
| `arena_save_meta()` / `arena_load_meta()` | SaveProvider JSON for meta |
| `arena_apply_upgrade_id("id")` | Apply an upgrade by id from the content |
| `arena_open_draft_debug()` | Open the choice screen (requires View) |

## Networking and Authority

Run mutations only when `IArenaSessionAuthority.IsAuthoritativeSimulation`. XP for enemy death is awarded on the authoritative node in `ArenaEnemyBrain.Die`.

## Phase 2

Insert `ArenaWaveUpgradeCoordinator` between waves in `ArenaSurvivalDirector.RunWaves` as a separate planned task.
