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

## Current Architecture in the CoreAI Repository

This example is already wired up in the monorepo. The pieces below are in place today:

1. **CoreAI** is available via UPM **`com.neoxider.coreai`** (`Assets/CoreAI` in the monorepo; external project - Git URL **`?path=Assets/CoreAI`**) for AI orchestration, Lua sandbox, and events; the **`CoreAiUnity`** host provides tests, prompts in **Resources**, and the **`_mainCoreAI`** scene.
2. **VContainer + MessagePipe + R3** are already dependencies in **`Packages/manifest.json`** (a minimal set compared to Last-War). Composition uses `RogueliteArenaLifetimeScope`.
3. Playable scenes live in **`Assets/_exampleGame/Scenes/`**: **`RogueliteArena.unity`** (main arena, entry via `ExampleRogueliteEntry`), **`SymbiosisArena.unity`** (symbiosis mode), and **`New Scene.unity`** (scratch). See [UNITY_SETUP.md](UNITY_SETUP.md) for how the `RogueliteArena` hierarchy and `CoreAILifetimeScope` are set up.
4. The prototype loop (spawn wave -> damage -> run loot/currency -> death screen -> hub with unlocks) runs locally without networking. Progression/meta details: [ARENA_PROGRESSION.md](ARENA_PROGRESSION.md).

### Not Yet Implemented

- **Co-op / networking:** the architecture separates an **AuthoritativeHost** role from a **ClientPresentationOnly** role (see [ARENA_ARCHITECTURE_AND_AI.md](ARENA_ARCHITECTURE_AND_AI.md)), but a concrete stack (**Netcode for GameObjects** or another option) is not yet integrated. Rule-change authority is designed to move to the host when it is.
