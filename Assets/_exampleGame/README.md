# Example Game - CoreAI Template Demo

**CoreAI template author:** **Neoxider** (nickname **neoxider**) - [github.com/NeoXider](https://github.com/NeoXider).

The example game in **`Assets/_exampleGame`** uses the **UPM** package **`com.neoxider.coreai`** (code in **`Assets/CoreAI`**) and the host **`Assets/CoreAiUnity`** (docs, **`Resources/AgentPrompts`**): a procedural wave arena, DI (**`CoreAILifetimeScope`**), **Creator** calls for every wave (**`ArenaCreatorWavePlanner`**), and a **Programmer** demo on **F9** (**`CoreAiLuaHotkey`**). Core logs: **`[Llm]`** + **`traceId`** in **`ApplyAiGameCommand`** - see [LLMUNITY_SETUP_AND_MODELS.md](../CoreAiUnity/Docs/LLMUNITY_SETUP_AND_MODELS.md).

**Step-by-step Unity setup (scene, LLM, HTTP):** [`Docs/UNITY_SETUP.md`](Docs/UNITY_SETUP.md). **Arena architecture, multiplayer, AI (waves / player analysis):** [`Docs/ARENA_ARCHITECTURE_AND_AI.md`](Docs/ARENA_ARCHITECTURE_AND_AI.md). Editor menu: **CoreAI -> Development -> Example Game -> Open RogueliteArena scene** (and an option to make the scene first in Build Settings). General repository quick start: [`../CoreAiUnity/Docs/QUICK_START.md`](../CoreAiUnity/Docs/QUICK_START.md). Template-code onboarding: [`../CoreAiUnity/Docs/DEVELOPER_GUIDE.md`](../CoreAiUnity/Docs/DEVELOPER_GUIDE.md).

Detailed run and meta gameplay concept: [`Docs/ROGUELITE_PLAYBOOK.md`](Docs/ROGUELITE_PLAYBOOK.md).

---

## About the Game

**Genre:** roguelite arena / survival with **meta-progression**.

**Session (run):** a short run of **about 10-25 minutes** - enemy waves in an arena, loot and upgrades inside the run, win/lose condition (for example, core/arena health, timer).

**After defeat or completion:** results screen -> **base / hub**, where currency is spent and **unlocks** are opened (passives, weapons, characters).

**Solo:** one player, one authoritative rules stream (locally, the same "host" model as in multiplayer).

**Co-op:** several players in the same arena; rule changes, waves, and AI calls are owned by the **host**; clients receive agreed events and state. Meta-progression can be shared, personal, or mixed (decided during save-system design).

**Why this example works for CoreAI:** little unique art, many **numbers, affixes, waves** - convenient for attaching **procedural logic and AI** (weekly affixes, wave composition, surprise rounds) without constant manual content work.

---

## Stack (Example Game + CoreAI Repository)

### Already in the CoreAI Project (`Packages/manifest.json`)

| Component | Purpose |
|-----------|------------|
| **Unity 6 + URP** | Rendering, project template |
| **Input System** | Input |
| **VContainer** (`jp.hadashikick.vcontainer`, as in Last-War) | DI, `LifetimeScope` |
| **MessagePipe** + **MessagePipe.VContainer** | Message bus + container registration |
| **R3** (`com.cysharp.r3`) | Reactivity for UI and state |
| **UniTask** | Async without extra allocations |
| **MoonSharp** (`org.moonsharp.moonsharp`) | Lua sandbox for scripts / use cases (together with **CoreAI**) |
| **AI Navigation** | Agents on grids/navmeshes (as needed) |
| **UGUI / UI Toolkit** (through Unity modules) | Hub and run interfaces |
| **Test Framework** | Tests |

Plugins in `Assets/Plugins` (for example debug utilities) are included as they exist in the repository; the core README does not treat them as a required part of the **template**.

### Package **`Assets/CoreAI`** and Host **`Assets/CoreAiUnity`** (in This Repository)

| Component | Purpose |
|-----------|------------|
| **LLMUnity** + **OpenAI-compatible HTTP** | Implementations of **`ILlmClient`**; see [`LLMUNITY_SETUP_AND_MODELS.md`](../CoreAiUnity/Docs/LLMUNITY_SETUP_AND_MODELS.md) |
| **Orchestration** | **`IAiOrchestrationService`** / **`AiOrchestrator`**, roles from **`BuiltInAgentRoleIds`** |
| **Lua** | **`LuaAiEnvelopeProcessor`**, MoonSharp sandbox, Programmer repair on error |

The example game **depends** on the public **CoreAI** API (**`com.neoxider.coreai`**), not the other way around: `_exampleGame` contains only game-specific scenes, prefabs, presenters, and use cases for the "arena + hub" mode.

---

## CoreAI Template SPEC (Condensed Summary)

Normative document: **`Assets/CoreAiUnity/Docs/DGF_SPEC.md`**.

1. **Boundaries:** the core provides DI, events, Lua sandbox, LLM facade, and orchestrator; the game provides content, prefabs, balance, and mode rules.
2. **Security:** Lua only through a whitelist API, instruction/time limits, dry-run when needed; the client does not execute raw LLM output as truth in multiplayer.
3. **Networking:** AI and run "law" changes happen on the **host**; final events and parameters are replicated.
4. **Layers (guideline):** Domain -> UseCases -> Presentation; infrastructure (save, network) is behind interfaces - in the spirit of [GameDev-Last-War](D:\Git\GameDev-Last-War), but without copying the whole monolith.
5. **Observability:** AI decision logs, optional developer panel (request queue, active agents).

Root product idea of the repository: [README.md](../../README.md) at the CoreAI root.

---

## Development Policy: Use Existing Solutions First, Custom Logic Last

**Required order** when adding any nontrivial capability (networking, DI, pools, UI patterns, saves, enemy waves, meta inventory, etc.):

1. **Search for an existing solution on GitHub** (UPM package, proven repository, official Unity/Cysharp documentation, and so on).
2. **Search for and adapt patterns in the reference project [GameDev-Last-War](D:\Git\GameDev-Last-War)** - it already has production-level VContainer, MessagePipe, R3, UniTask, feature splitting, ECS for heavy areas, and integrations. Take **ideas and approach fragments**, not the entire repository, when the task is narrow.
3. **Only if** no suitable open solution or close Last-War analogue is found, write a **custom** implementation from scratch (or minimal glue code).

**Goal:** fewer bugs, faster iteration, consistency with the template stack. Custom code is a deliberate exception, not the first reaction.

---

## `_exampleGame` Folder Structure

| Path | Purpose |
|------|------------|
| `RogueliteArena/` | Example code (`CoreAI.ExampleGame`), scene bootstrap |
| `RogueliteArena/Features/` | **Example** features (waves, hub, run UI) - their own installers / child `LifetimeScope` |
| `Docs/` | Game concept and notes (`ROGUELITE_PLAYBOOK.md`) |

Entry point: `RogueliteArena/Features/ArenaBootstrap/ExampleRogueliteEntry.cs` (arena + **`CoreAiLuaHotkey`**). Scene **`RogueliteArena`**: **`CompositionRoot`** has **`CoreAILifetimeScope`** + **`ExampleRogueliteEntry`**. Run state: **`ArenaSurvivalSession`** (no singleton), waves: **`ArenaSurvivalDirector`** + **`IArenaWaveSchedule`**, node role: **`ArenaSimulationRole`**. See [`../CoreAiUnity/README.md`](../CoreAiUnity/README.md) and [`../CoreAI/Docs/README.md`](../CoreAI/Docs/README.md) (UPM).

Pattern: root `CoreAILifetimeScope` (CoreAI core) +, if needed, a child `LifetimeScope` in this folder only for roguelite-example code.
