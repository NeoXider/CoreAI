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

## Test integrity rules

PlayMode tests must be fair diagnostics, not coached completions.

- Prompts describe the user goal. Do not include exact tool JSON, exact Lua code, exact item names, exact expected wording, or "retry with this exact call" unless the test is explicitly a parser/repair fixture.
- Do not add corrective retries that tell the model which tool and arguments to use after it failed. A second turn is allowed only if it is a realistic user turn and the assertion still checks final runtime state.
- Prefer assertions on game state, memory state, emitted commands, tool traces, and non-empty UI output. For real LLM text, assert semantic content rather than exact phrasing.
- If a scenario claims to validate a tool-backed feature (Lua execution, memory persistence, inventory purchase, world command, etc.), the test must assert the corresponding completed tool trace or resulting runtime state. Do not let the scenario pass from prose, memory-only text, or a command-shaped final answer when the actual tool was skipped.
- Use forced tool choice only for narrow mechanics tests whose subject is the bound tool execution path itself. Do not use forced tool choice in tests whose subject is autonomous model tool selection.
- Keep the mandatory full PlayMode suite focused on one strongest representative live-model path per behaviour. Long stochastic duplicates, narrow regression variants, and expensive exploratory probes should be `[Explicit]` targeted tests with a clear reason, not hidden full-suite gates.
- Use **120s** for medium single-turn live-model prompts and **240s** for complex tool/SkillSet/crafting/Lua/multi-agent turns. Do not exceed **600s** without documenting the failure mode. A timeout means inspect prompt, tool schema, routing, cancellation, reasoning mode, and mechanics before increasing limits.
- Keep deterministic exact-string fixtures in EditMode or stubbed PlayMode tests, not in live-model verification.

## Docs

Crafting workflows: **`Scenarios/CraftingMemory_README.md`**. Scenario readme in **`Scenarios/Complex/README.md`**.
