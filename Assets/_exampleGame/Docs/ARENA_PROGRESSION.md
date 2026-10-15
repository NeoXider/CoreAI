# Arena: Progression (Meta + Session)

Implementation in the Vampire Survivors style: **session** XP/level and card drafts, plus **meta** between runs through `Neo.Save.SaveProvider` (NeoxiderTools, `com.neoxider.tools`). Waves are handled by `ArenaSurvival.UseCases.ArenaSurvivalDirector`; the between-wave upgrade coordinator (`ArenaWaveUpgradeCoordinator`) exists but is not wired into the director.

## Folder Structure (Clean Architecture)

**Example root** `Assets/_exampleGame/RogueliteArena/`:

- `Composition/` - `RogueliteArenaLifetimeScope` (VContainer): an optional child scope for example-only registrations. It registers nothing yet and is not placed in the shipped scene; progression is wired by `ArenaSurvivalProceduralSetup`.

Each feature under `Features/<Name>/` is split into layers (folders = layers):

| Feature | Domain | UseCases | Presenter | View | Infrastructure |
|------|--------|----------|-----------|------|----------------|
| **ArenaSurvival** | session contracts (`IArenaSessionView`), `ArenaSimulationRole`, `ArenaDirectorSettings` | wave orchestration (`ArenaSurvivalDirector`) | - | `ArenaSurvivalHud` | `ArenaSurvivalSession`, `ArenaSurvivalProceduralSetup`, `ArenaNavMeshRuntimeBake` |
| **ArenaWaves** | `IArenaWaveSchedule`, plans, difficulty curve (model) | `ArenaLocalWavePlanner`, `ArenaWavePlanValidator` | - | - | Creator/parser, `ArenaLinearWaveSchedule`, preset SOs and VS curve |
| **ArenaCombat** | - | - | - | - | player, enemy, companion, AINpc listener |
| **ArenaCamera** | - | - | - | - | `ArenaFollowCamera` |
| **ArenaAi** | constants `ArenaAiSourceTags` | - | - | - | task bus, triggers, aux LLM |
| **ArenaBootstrap** | - | - | - | - | `ExampleRogueliteEntry`, LLM/Lua hotkeys |

**Progression** `Features/ArenaProgression/`:

- `Domain` - state, enums, `IArenaCombatStats`
- `UseCases` - use cases for meta save, XP, rolling, and applying upgrades
- `Presenter` - `ArenaUpgradeDraftPresenter`, placeholder `ArenaWaveUpgradeCoordinator`
- `View` - `ArenaUpgradeChoiceView`, `ArenaUpgradeCardWidget` (TMP + UI)
- `Infrastructure` - ScriptableObject and content (`ArenaProgressionContent`, configs, `ArenaUpgradeDefinition`), runtime services (roll, save, Lua bindings, session host, combat model, companion brain)

Namespaces mirror the layer: `CoreAI.ExampleGame.<Feature>.<Domain|UseCases|Presenter|View|Infrastructure>`.

## ScriptableObjects

Create default assets from the menu **CoreAI Example -> Arena -> Generate Progression Assets (Defaults)** (writes to `Assets/_exampleGame/Settings/Progression/`). Single assets can also be created from **Assets -> Create -> CoreAI Example -> Arena -> ...**.

> **Note:** progression is **opt-in and generated on demand**. The `Assets/_exampleGame/Settings/` folder is not checked in, and in the shipped `RogueliteArena.unity` scene the `Arena Progression Content` and `Arena Unit Baseline Config` fields are **unassigned** - so `ArenaProgressionSessionHost` (XP/level, meta-save, draft UI) is **not active** by default. Run the menu above and assign the generated assets to enable it.

- `ArenaUnitBaselineConfig` - starting stats for the player and companion
- `ArenaRunBalanceConfig` - references to `LevelCurveDefinition` (session + meta), XP per kill, team split, rarity multipliers, card limits
- `ArenaProgressionContent` - upgrade registry and references to `ChanceData` (rarity, categories by rarity, stat-pool weights)
- `ArenaUpgradeDefinition` - id, title, description, icon, kind, rarity, statDelta
- `ArenaUpgradePresentationConfig` - frame sprites/materials by rarity
- `ArenaPersistenceConfig` - meta key for SaveProvider (optional; otherwise the gateway uses the default key in code)

Neoxider: [Random / ChanceData](https://github.com/NeoXider/NeoxiderTools/tree/main/Assets/Neoxider/Docs/Tools/Random), [Progression / LevelCurveDefinition](https://github.com/NeoXider/NeoxiderTools/tree/main/Assets/Neoxider/Docs/Progression).

### ChanceData Indexes

The generator fills the `ChanceData` assets in this order:

- **Rarity** (`Chance_Rarity`): 0 Common, 1 Rare, 2 Epic, 3 Legendary
- **Common/Rare categories:** 0 = Stat
- **Epic:** 0 Stat, 1 PassiveSlot
- **Legendary:** 0 Stat, 1 OfferExtraChoices, 2 LegendaryDoublePick
- **StatUpgradeWeights:** index = order of stat upgrades in the `ArenaProgressionContent.upgrades` list (the first N are stat kinds only in the default generation)

`ArenaUpgradeRollService` currently uses only the rarity roll (`ArenaRarityLuckModifier` moves weight from Common to the rarer tiers as the session level grows) and draws the offered upgrades uniformly from the upgrade list; the category and stat-weight assets are generated and referenced but not read yet.

## Scene and Wiring

On `ArenaSurvivalProceduralSetup`, assign **Arena Progression Content** and **Arena Unit Baseline Config**. At startup it creates `ArenaProgressionSessionHost` (XP per kill, meta load/save, Lua bindings object) and adds `ArenaProgressionDebugHotkey`.

Draft UI prefab: add `ArenaUpgradeChoiceView` + a pool of five `ArenaUpgradeCardWidget` instances, then assign the reference in `ArenaProgressionSessionHost.draftView`. Without a view the draft does not open.

**Debug:** `ArenaProgressionDebugHotkey` - key **L** opens the draft.

### Wave Difficulty Curve (VS-style)

Nonlinear Vampire Survivors-style difficulty: **overall harder toward the end of the run** (ramp by wave progress), while **individual waves can be softer** because of sine waves over enemy count and stats.

- **Assignment:** **Wave Difficulty Profile** field on the `ArenaDirectorSettings` asset (referenced by `ArenaSurvivalProceduralSetup.directorSettings`). `ArenaSurvivalDirector.Init` reads it from `directorSettings.WaveDifficultyProfile`; if it is empty, only the plan / linear schedule is used.
- **Asset:** `ArenaVsStyleWaveDifficulty` - menu **Assets -> Create -> CoreAI Example -> Arena -> VS-style Wave Difficulty**, or **CoreAI Example -> Arena -> Generate VS Wave Difficulty Asset** (writes to `Assets/_exampleGame/Settings/Arena/ArenaVsWaveDifficulty.asset`).
- Multipliers are applied on top of the Creator plan / local plan / linear schedule (enemy count, HP, damage, speed, spawn interval). Telemetry keys are `arena.wave.vs.*_mult`.

## Lua API (Reference Surface)

`ArenaProgressionLuaBindings` (an `ILuaCsGameRuntimeBindings`) defines these functions. `ArenaProgressionSessionHost` constructs it, but the example does not register it with a Lua runtime yet, so the functions are not callable from Lua in the shipped setup. A host that builds its own Lua surface can register it like any other gameplay binding (see [TROUBLESHOOTING.md, Lua type 2](../../CoreAiUnity/Docs/TROUBLESHOOTING.md#-problem-lua-failed)).

| Function | Purpose |
|--------|------------|
| `arena_add_session_xp(n)` | Session XP (split across the alive team members by `ArenaKillXpService`) |
| `arena_add_meta_xp(n)` | Meta-XP + level recalculation through the meta curve |
| `arena_save_meta()` / `arena_load_meta()` | SaveProvider JSON for meta |
| `arena_apply_upgrade_id("id")` | Apply an upgrade by id from the content |
| `arena_open_draft_debug()` | Open the choice screen (requires the view) |

## Networking and Authority

Run mutations only when `IArenaSessionAuthority.IsAuthoritativeSimulation`. XP for enemy death is awarded on the authoritative node in `ArenaEnemyBrain.Die`.
