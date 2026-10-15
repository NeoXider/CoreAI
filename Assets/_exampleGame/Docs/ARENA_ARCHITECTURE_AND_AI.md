# Arena Example: Architecture for Multiplayer and AI Roles

## 1. Design Rules

- **No global game-host singleton.** Run state is one `ArenaSurvivalSession` per match / scene, consumed
  through interfaces; one static "game host" per process would not work for co-op and networking.
- **Explicit dependencies.** `ArenaSurvivalDirector` hands each spawned enemy its session through
  `ArenaEnemyBrain.Configure(session, killXp)`; enemies never search the scene for it.
- **A node role for the simulation.** `ArenaSimulationRole` separates an **authoritative host** from a
  **presentation client**, so the same code can run solo or behind a network stack.

## 2. Current Model

| Component | Purpose |
|-----------|------------|
| **`IArenaSessionView` / `IArenaSessionAuthority`** | Run state contract: wave, alive enemies, run end. UI and director depend on an **interface**, not a singleton. |
| **`ArenaSurvivalSession`** | Scene session implementation. Field **`ArenaSimulationRole`**: **AuthoritativeHost** (solo / listen server) or **ClientPresentationOnly** (client without wave simulation and enemy AI). |
| **`ArenaSurvivalDirector`** | Starts the wave coroutine **only** when `IsAuthoritativeSimulation`. Spawn: `Instantiate` -> `ArenaEnemyBrain.Configure(session, killXp)` -> `SetActive(true)`. Reads its tuning from an **`ArenaDirectorSettings`** asset (`directorSettings` on `ArenaSurvivalProceduralSetup`); without it the director logs `ArenaDirectorSettings is missing!` and starts no waves. Waits for a **Creator** plan up to **`CreatorPlanWaitSeconds`** (default 14), otherwise uses the fallback **`ArenaLinearWaveSchedule`** (`WaveSchedule` on the same asset). When **`LoggingLlmClientDecorator.Unwrap(ILlmClient)`** is **`StubLlmClient`**, it plans waves locally with `ArenaLocalWavePlanner` instead. |
| **`ArenaEnemyBrain`** | Movement, player damage, death (and kill XP) - only under **authority**. No `Find*`. |
| **`IArenaWaveSchedule` / `ArenaLinearWaveSchedule`** | Rule for "how many enemies in a wave"; the fallback when no Creator plan is available. |
| **`ArenaCreatorWavePlanner`** | Before a wave, calls **`RunTaskAsync`** with the **Creator** role; parses the response into **`ArenaWavePlan`**; stores plans **by wave number** (LLM responses can arrive late). An invalid plan logs a console warning; repeated invalid plans switch to the linear schedule (`ForceLinearWavePlans`). |
| **`ArenaSurvivalProceduralSetup`** | Builds the arena at `Start`. Option **`logOnStartRoles`**: one-time log of which LLM roles the example actually calls. |

### Integrating Netcode for GameObjects (NGO) or an Equivalent

`RogueliteArena` has no network stack. The only network-aware code is in `ArenaSurvivalDirector`: when an NGO
`NetworkManager` is listening (found by reflection), the wave's enemy count is multiplied by the number of
connected clients. The separate **`SymbiosisArena`** scene uses NGO directly (`SymbiosisGhostPlayer` and
`SymbiosisSkeletonCompanion` are `NetworkBehaviour`s). To put the arena behind a network stack:

1. **Wave simulation and spawning** - only on the **server** (`IsServer` + role as **AuthoritativeHost**). Client: **ClientPresentationOnly** + `NetworkObject` / `NetworkTransform` on enemy copies, spawned through `NetworkManager.Spawn`.
2. **Session state** - replicate the minimum: `CurrentWave`, `RunEnded`, `PlayerWon`, alive-enemy counter (or expose it from the server via a "wave cleared" event).
3. **Damage** - server authority: `ServerRpc` from local input, or prediction + rollback (title policy).
4. **AI (LLM)** - only on the **host**; clients receive already validated **commands / wave parameters**, not raw model text (see DGF_SPEC and DEVELOPER_GUIDE).

---

## 3. What AI Agents Do in This Game

The example calls these roles from the core (**`BuiltInAgentRoleIds`**) and never executes raw model text
in gameplay. Every request carries a `SourceTag` from `ArenaAiSourceTags`.

### 3.1. Creator - Wave Plans

Before each wave (`arena_director:wave_start`) and as a prefetch for wave N+1 when few enemies remain
(`arena_director:pre_next_wave`), `ArenaCreatorWavePlanner` asks Creator for one JSON envelope
(`commandType: "ArenaWavePlan"`). The game publishes its state as `arena.*` / `player.hp.*` telemetry keys
in `SessionTelemetryCollector` before the request. `ArenaWavePlanValidator` checks ranges and the wave
index; a valid plan sets enemy count, HP / damage / speed multipliers, spawn interval and radius for that
wave. **F1** requests a plan by hand (`hotkey:F1`). The contract is in
[CREATOR_WAVE_CONTEXT.md](CREATOR_WAVE_CONTEXT.md).

**Why not "just Lua":** for wave balance, **JSON + strict schema** is enough; Lua (**Programmer**) is kept
for narrow scenarios and tools.

### 3.2. Analyzer - Run Reflection

- After each wave, when `RunPostWaveAnalyzer` is on and the LLM is not a stub, a low-priority Analyzer task
  (`arena_post_wave:{n}`) writes a short difficulty analysis to the log; it issues no game commands.
- Every N waves (`everyNWaves`, default 3) `ArenaAuxLlmEveryNWaves` sends an Analyzer task
  (`arena_aux:every_n_waves:analyzer`).
- `ArenaAiTaskBus` sends an Analyzer task when the session reports a boss defeat
  (`arena_event:boss_defeated`); the shipped arena has no boss, so this hook is idle.

### 3.3. AINpc - Lines and Companion Stance

- `ArenaAiTaskBus` asks AINpc for a line when the player's HP drops below the critical ratio
  (`arena_event:player_hp_critical`) and when an `ArenaAiRoomEnterTrigger` fires
  (`arena_event:room_enter:<id>`; the shipped arena has no room triggers).
- Every N waves `ArenaAuxLlmEveryNWaves` also sends an AINpc task (`arena_aux:every_n_waves:ainpc`).
- **F2** (`hotkey:F2`) asks AINpc for a companion stance; `ArenaCompanionAiListener` parses the reply and
  applies it to the companion bot.

### 3.4. Programmer - Tools and Debugging

**F9** (`CoreAiLuaHotkey`) sends a Programmer task that asks for Lua calling `report(...)`. The shipped scene
has no `CoreAiModsLifetimeScope`, so the Lua is not executed there; see
[UNITY_SETUP.md](UNITY_SETUP.md#step-6-gameplay-and-ai-check).

### 3.5. Not Used by the Example

**CoreMechanicAI**, **PlainChat** and **SmartChat** are not called by the arena. They fit a hub or post-run
chat (**SmartChat** has the memory tool by default).

---

**Related documents:** [../../CoreAiUnity/Docs/DEVELOPER_GUIDE.md](../../CoreAiUnity/Docs/DEVELOPER_GUIDE.md), [../../CoreAiUnity/Docs/AI_AGENT_ROLES.md](../../CoreAiUnity/Docs/AI_AGENT_ROLES.md), [../../CoreAiUnity/Docs/DGF_SPEC.md](../../CoreAiUnity/Docs/DGF_SPEC.md).
