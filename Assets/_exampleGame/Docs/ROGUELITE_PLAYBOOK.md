# Example: Roguelite Arena (CoreAI `_exampleGame`)

## Session Concept

- **Run duration:** 10-25 minutes.
- **Defeat:** death / failed objective (core destroyed, timer expired) -> results screen -> **base/hub**.
- **Meta-progression:** unlock passives, weapons, and characters (run currency + persistent currency).
- **Solo:** one player, one rules stream.
- **Co-op:** the same arena; meta-progression is configurable (shared / personal / mixed).

## Why This Fits the CoreAI Template

- Little unique art: 1-3 arena presets, primitive enemies.
- **Affixes, waves, "weekly" modifiers** are data + procedural logic; later, LLM under host authority.
- Telemetry for AI: wave, DPS, deaths, selected upgrades, session time.

**Code and AI roles (waves, player analysis, multiplayer):** see [ARENA_ARCHITECTURE_AND_AI.md](ARENA_ARCHITECTURE_AND_AI.md).

## Lessons from GameDev-Last-War (Reference Architecture)

The `D:\Git\GameDev-Last-War` project is a large production codebase: **Clean Architecture**, **VContainer**, **MessagePipe**, **R3**, **UniTask**, **ECS (Entities)** for heavy visual parts, **gRPC / MagicOnion**, **PlayFab**, **SQLite**, **Serilog/ZLogger** logging, and **NSubstitute** tests.

For a **lightweight** roguelite example in CoreAI, pulling in the whole stack is **not required**: it is enough to reuse the **layering idea** (Domain -> UseCases -> Presentation) and DI, then add networking/backend pieces only when needed.

## Next Steps in the CoreAI Repository

1. Connect **CoreAI**: UPM **`com.nexoider.coreai`** (`Assets/CoreAI` in the monorepo; external project - Git URL **`?path=Assets/CoreAI`**) for AI orchestration, Lua sandbox, and events; copy the **`CoreAiUnity`** host if needed (tests, prompts in **Resources**, **`_mainCoreAI`** scene).
2. Add **VContainer + MessagePipe + R3** to the example (as in Last-War, but with the minimal set).
3. Scene `RogueliteBootstrap` with `ExampleRogueliteEntry` + later `LifetimeScope`.
4. Prototype loop: spawn wave -> damage -> run loot/currency -> death screen -> hub with unlocks (without networking).
5. Co-op: choose the stack (**Netcode for GameObjects** or another option) and move rule-change authority to the host.
