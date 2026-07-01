# Arena Example: Architecture for Multiplayer and AI Roles

## 1. What Was Wrong in the First Prototype

- **Global singleton** `ArenaSurvivalGameHost.Instance` - in co-op and networking, one static "game host" per process is not viable; the game needs an instance per **match / room** and explicit references.
- **Hidden dependencies** - `ArenaEnemyBrain` found the session through `FindFirstObjectByType`, which breaks initialization order and testability.
- **Simulation without a node role** - all code revolved around "one local player", without separating an **authoritative host** from a **presentation client**.

## 2. Current Model (After Refactoring)

| Component | Purpose |
|-----------|------------|
| **`IArenaSessionView` / `IArenaSessionAuthority`** | Run state contract: wave, alive enemies, run end. UI and director depend on an **interface**, not a singleton. |
| **`ArenaSurvivalSession`** | Scene session implementation. Field **`ArenaSimulationRole`**: **AuthoritativeHost** (solo / listen server) or **ClientPresentationOnly** (client without wave simulation and enemy AI). |
| **`ArenaSurvivalDirector`** | Starts the wave coroutine **only** when `IsAuthoritativeSimulation`. Spawn: `Instantiate` -> `ArenaEnemyBrain.Configure(session)` -> `SetActive(true)`. Waits for a **Creator** plan up to **`creatorPlanWaitSeconds`** (inspector), otherwise uses the fallback plan from **`ArenaLinearWaveSchedule`**. Uses "local planner only" mode when **`LoggingLlmClientDecorator.Unwrap(ILlmClient)`** is **`StubLlmClient`**. |
| **`ArenaEnemyBrain`** | Movement, player damage, death - only under **authority**. No `Find*`. |
| **`IArenaWaveSchedule` / `ArenaLinearWaveSchedule`** | Rule for "how many enemies in a wave". Linear schedule is the default; later it is replaced by a **validated** descriptor from AI (or a table from the server). |
| **`ArenaCreatorWavePlanner`** | Before a wave, calls **`RunTaskAsync`** with the **Creator** role; parses the response into **`ArenaWavePlan`**; stores plans **by wave number** (LLM responses can arrive late). Invalid plan - console warning. |
| **`ArenaSurvivalProceduralSetup`** | Option **`logOnStartRoles`**: one-time log of which LLM roles the example actually calls (Creator for waves, Programmer on F9; **AINpc** is not connected; the companion is a bot without LLM). |

### Integrating Netcode for GameObjects (NGO) or an Equivalent

1. **Wave simulation and spawning** - only on the **server** (`IsServer` + role as **AuthoritativeHost**). Client: **ClientPresentationOnly** + `NetworkObject` / `NetworkTransform` on enemy copies, spawned through `NetworkManager.Spawn`.
2. **Session state** - replicate the minimum: `CurrentWave`, `RunEnded`, `PlayerWon`, alive-enemy counter (or expose it from the server via a "wave cleared" event).
3. **Damage** - server authority: `ServerRpc` from local input, or prediction + rollback (title policy).
4. **AI (LLM)** - only on the **host**; clients receive already validated **commands / wave parameters**, not raw model text (see DGF_SPEC and DEVELOPER_GUIDE).

---

## 3. Proposal: What AI Agents Do in This Game

Below are practical roles from the core (**`BuiltInAgentRoleIds`**) and how to connect them to the arena **without** executing raw text in gameplay.

### 3.1. Creator - **Wave and Run "Law" Design**

**Task:** from a concise **session snapshot** (current wave, wave time, arena tag, weekly flags), propose a **structured** plan for the next wave or modifiers.

**Example outputs (after parser/validation):**

- Enemy count, **types** (fast / tanks / ranged), speed multiplier, **spawn radius**, mini-boss on the Nth wave.
- **Wave affix** ("increased contact damage", "enemies are twice as slow") - as an ID from a table, not free text.
- Rare **surprise round** (elite, pause, music state change) - also as enum / bool flags.

**Code pipeline:**

1. Host builds `ArenaTelemetrySnapshot` (extend `SessionTelemetryCollector` / separate builder).
2. `RunTaskAsync` with role **`Creator`**, `Hint` = serialized snapshot + difficulty budget.
3. Response: schema JSON -> **validator** (min/max limits, type whitelist) -> if OK, **`IArenaWaveSchedule`** for the next interval or one-time parameter override in `ArenaSurvivalDirector` (parsed into `ArenaWavePlan` and applied between waves).

**Why not "just Lua":** for wave balance, **JSON + strict schema** is usually enough; keep Lua (**Programmer**) for narrow scenarios and tools.

### 3.2. Analyzer - **Player and Pace Analysis**

**Task:** evaluate "how the run is going" from **anonymized statistics** (not PII) and produce a **classification** for the director and Creator.

**Example input metrics:**

- Average HP percent after a wave, wave clear time, hit/miss frequency (if telemetry exists), number of "near deaths".
- Rolling **pace mood**: too boring / normal / overloaded.

**Output (structured again):**

- `skill_band`: novice / comfortable / expert (boundaries in code).
- `recommended_pressure`: -1...+1 -> mapped to an enemy-count multiplier or delay between waves.
- `flags`: for example `player_turtling`, `aggressive_melee` - for Creator enemy-type selection.

**Pipeline:** call **`Analyzer`** once per wave or once every N minutes; place the result into a **hint queue** for Creator / `CoreMechanicAI`, not directly into Transform.

### 3.3. CoreMechanicAI - **Combat Rules and Fairness**

**Task:** keep the **bounds** (damage cap, anti-exploit, "no more than X elites in a row"), and suggest **table adjustments** when Analyzer reports imbalance.

**Output:** again JSON/flags applied to **config** (ScriptableObject / key-value), not arbitrary C#.

### 3.4. Programmer - **Tools and Debugging**

Already used in the template (**F9**, Lua + `report`). In the arena: generate **one-off** debug scenarios and telemetry exports, not the main wave balance.

### 3.5. AINpc / PlainChat / SmartChat - **Optional**

- **AINpc:** commentator lines / "the arena comes alive" - text in UI, with no effect on HP.
- **PlainChat / SmartChat:** player advice if chat is needed in the hub or after a run (**SmartChat** has MemoryTool by default).

---

## 4. Implementation Priority

1. Lock down a **JSON schema** for one wave and a validator in **CoreAI.ExampleGame** (without LLM).
2. Connect **Creator** to filling `ArenaWavePlan` between waves (host).
3. Add **Analyzer** with 3-5 metrics from `ArenaSurvivalSession` + wave timers.
4. Then NGO: replicate the wave descriptor and `NetworkObject` on enemies.

**Related documents:** [../../CoreAiUnity/Docs/DEVELOPER_GUIDE.md](../../CoreAiUnity/Docs/DEVELOPER_GUIDE.md), [../../CoreAiUnity/Docs/AI_AGENT_ROLES.md](../../CoreAiUnity/Docs/AI_AGENT_ROLES.md), [../../CoreAiUnity/Docs/DGF_SPEC.md](../../CoreAiUnity/Docs/DGF_SPEC.md).

**Version:** 1.1 (April 2026) - Creator planner, LLM wait, stub unwrap, role log.
