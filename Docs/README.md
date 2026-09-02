# CoreAI Documentation

This is the front door for the repository. Start here when you need to understand
where CoreAI lives, which package owns a feature, or which document answers a
specific integration question.

The short version: CoreAI is split into a portable AI/runtime layer, a Unity-facing
package, an example game, and planning material. Each package keeps its own deep
index; this page points you to the right one before you spend time reading the
wrong folder.

## Start Here

| Area | Entry Point | Use When |
|---|---|---|
| CoreAI Unity package | [Assets/CoreAiUnity/Docs/DOCS_INDEX.md](../Assets/CoreAiUnity/Docs/DOCS_INDEX.md) | You need setup, chat UI, streaming, settings, architecture, tests, or Unity integration docs. |
| Portable CoreAI package | [Assets/CoreAI/Docs/README.md](../Assets/CoreAI/Docs/README.md) | You need host-agnostic contracts: agents, tools, routing, MEAI, Lua sandbox, and tool-calling rules. |
| CoreAI Mods (Lua, optional) | [INSTALL.md §3](../INSTALL.md#3-mods-module-lua) · [LUA_SANDBOX_SECURITY.md](../Assets/CoreAI/Docs/LUA_SANDBOX_SECURITY.md) | You need the `com.neoxider.coreaimods` package: Lua sandbox, `execute_lua`/`manage_mods` tools, mod runtime. |
| CoreAI Hub (UI Toolkit, optional) | [INSTALL.md §4](../INSTALL.md#4-hub-module-ui-toolkit) | You need the `com.neoxider.coreaihub` tabbed Hub window and its page registry. |
| CoreAI Benchmark (dev-only) | [Assets/CoreAIBenchmark/README.md](../Assets/CoreAIBenchmark/README.md) | You need the `com.neoxider.coreaibenchmark` LLM game-creation benchmark harness. |
| Example game | [Assets/_exampleGame/README.md](../Assets/_exampleGame/README.md) | You need the RogueliteArena sample, scene setup, progression, or AI wave planning notes. |
| NeoxiderTools | [Assets/NeoxiderTools/Docs/README.md](../Assets/NeoxiderTools/Docs/README.md) | You need shared utility/toolkit documentation used by this Unity project. |

The full five-package dependency graph and install profiles (Base / +Mods / +Hub / Full) live in
[INSTALL.md](../INSTALL.md).

## Common Reading Paths

Use these paths when you have a concrete task:

| Goal | Read In This Order |
|---|---|
| Install or wire CoreAI in a Unity scene | CoreAiUnity docs index -> quick start -> settings/runtime architecture |
| Debug LLM requests, streaming, or WebGL SSE | CoreAiUnity streaming docs -> CoreAI routing docs -> transport-specific source comments |
| Add a new LLM tool | Tool-calling best practices -> AgentBuilder -> relevant tests |
| Expose Lua or AI-authored scripts | Lua sandbox security -> runtime binding code -> sandbox tests |
| Decide what to build next | Module audit -> TODO -> orchestration plan |

## Roblox-style mod API (Rbx)

The Lua surface a mod builds worlds with, and the artifacts it produces:

| Document | Purpose |
|---|---|
| [Assets/CoreAI/Docs/RBX_API.md](../Assets/CoreAI/Docs/RBX_API.md) | User-facing reference: `Instance.new`, datatypes, services, `BasePart.Material` and `Part.Color`, saving/loading a world, the documented `RemoteFunction` timeout deviation. |
| [CoreAIMods/WORLD_PACKAGE.md](CoreAIMods/WORLD_PACKAGE.md) | The `.world` package format, validation limits, manual slots vs. the autosave ring, the confirm/reject load flow, and runtime session replacement. |
| [Assets/CoreAIMods/Runtime/RbxApi/Unity/PROCEDURAL_MATERIALS.md](../Assets/CoreAIMods/Runtime/RbxApi/Unity/PROCEDURAL_MATERIALS.md) | The procedural shader catalog behind `Enum.Material`: it defines all 45 items and the magenta diagnostic fallback; six of them are overridden by the texture catalog at runtime. |
| [Assets/CoreAIMods/Runtime/RbxApi/Unity/TEXTURE_MATERIALS.md](../Assets/CoreAIMods/Runtime/RbxApi/Unity/TEXTURE_MATERIALS.md) | Catalog-driven texture materials: six packaged CC0 sets, project-local override catalog for any of the 45 items (ambientCG downloader, Quixel Bridge / Fab importer), projection and tint rules. |
| [CoreAIMods/RBX_API_SKILL.md](CoreAIMods/RBX_API_SKILL.md) | How the agent-facing `Rbx API` skill text is produced and kept in sync. |
| [CoreAIMods/ROBLOX_API_ROADMAP.md](CoreAIMods/ROBLOX_API_ROADMAP.md) | The definitive MVP0–MVP17 ladder and every locked API decision. |

## Product And Planning

| Document | Purpose |
|---|---|
| [TODO.md](../TODO.md) | Live backlog, completed archive, and remaining feature-level debt. Open audit findings (CI, package isolation, durability, full-tier query) live here. |
| [BACKLOG.md](../Assets/CoreAiUnity/Docs/BACKLOG.md) | Non-blocking future work and feature ideas (director-AI scene, replay, mod gallery, and more). |
| [TODO/MultiAgent_Orchestration_v2.0.md](../TODO/MultiAgent_Orchestration_v2.0.md) | Multi-agent orchestration plan. |
| [BENCHMARK_LEADERBOARD.md](BENCHMARK_LEADERBOARD.md) | Public community leaderboard for the Game-Creation Benchmark: ranked results, suite versioning, and the score-submission workflow. |
| [CoreAIMods/MOD_SHARING.md](CoreAIMods/MOD_SHARING.md) | Shareable mod bundle format, Hub export/import flow, import safety, and the proposed community mod gallery process. |

## Private planning material

Files under `Docs/LocalBusinessPlans/` are intentionally local/untracked (gitignored) and
are **not** part of the public package documentation surface. Do not link them from
user-facing docs; if a planning conclusion becomes product guidance, copy the stable
technical decision into a public package document instead.

## Documentation Rules

- Keep detailed package docs in English unless the file is explicitly marked `_RU`.
- Keep changelogs historical; move current user guidance into stable docs.
- Link every new stable guide from the nearest package index and, when it is
  repository-wide, from this file.
- Keep XML documentation in source concise and contract-focused; avoid mechanical
  summaries such as "Executes X API operation".
