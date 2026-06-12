# Play Mode tests (`CoreAI`)

Play Mode assemblies live under **`Assets/CoreAiUnity/Tests/PlayMode/`** and replace the legacy single **`PlayModeTest`** DLL.

## Layout

| Folder | Assembly | Purpose |
|--------|-----------|---------|
| **`FastNoLlm/`** | `CoreAI.Tests.PlayMode.FastNoLlm` | Fast checks with **stub** LLMs / orchestrator-only — no model load, suitable for CI smoke. Includes **`UnityMainThreadLlmAsyncMarshalerPlayModeTests`** (Play **`isPlaying`**: **`SwitchToThreadPool`** then marshaler restores main **`ManagedThreadId`**). Companion **Edit Mode** regression: **`UnityMainThreadLlmAsyncMarshalerEditModeTests`** (`Tests/EditMode/`, **`!isPlaying`** inline path). |

`UnityMainThreadLlmAsyncMarshalerPlayModeTests` intentionally covers full-suite state leakage: before it switches to the ThreadPool, it refreshes the Editor Play Mode mirror from the player-loop main thread so stale state from previous PlayMode fixtures cannot make tool bodies run inline off-thread.
| **`LlmVerification/`** | `CoreAI.Tests.PlayMode.LlmVerification` | Narrow **live-model** probes (streaming, HTTP, memory, pipelines, tooling). **`Assert.Ignore`** when no backend is configured. Includes **`MultiToolChainPlayModeTests`** (optional second task if the first hop omits the memory marker). |
| **`Scenarios/`** | `CoreAI.Tests.PlayMode.Scenarios` | Longer **game-style flows** (multi-agent crafting, merchants, deterministic craft memory). Requires LLM / env per test docs. |

### Support DLLs

| Folder | Assembly |
|--------|----------|
| **`Shared/`** | `CoreAI.Tests.PlayMode.Shared` — `PlayModeTestAwait`, `AiOrchestratorBuiltInRolesPlayModeHarness` |
| **`LlmInfra/`** | `CoreAI.Tests.PlayMode.LlmInfra` — `SharedLlmUnity`, `PlayModeProductionLikeLlmFactory`, `TestAgentSetup`, global LLM teardown |

In the Unity Test Framework, filter by **`Assembly`** to run **Fast vs LlmVerification vs Scenarios** separately.

## Docs

Crafting workflows: **`Scenarios/CraftingMemory_README.md`**. Scenario readme in **`Scenarios/Complex/README.md`**.
