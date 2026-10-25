# CoreAI Documentation

This is the front door for the repository. Start here when you need to understand
where CoreAI lives, which package owns a feature, or which document answers a
specific integration question.

The short version: CoreAI is split into a portable AI/runtime layer, a Unity-facing
package, optional packages (Mods, Hub, MCP, Mirror, Benchmark), and an example game.
Each package keeps its own deep index; this page points you to the right one before
you spend time reading the wrong folder. Developer material (plans, backlog, release
process, research notes) is kept apart in [`dev-docs/`](../dev-docs/README.md).

[7.54.0 acceptance evidence](../dev-docs/fix-wave-2026-10-24/README.md) records passing selected Windows EditMode/hermetic PlayMode and portable checks, exact exceptions, raw historical negatives and unrun platform/quality limits. Publication uses the [Git tag and GitHub release](https://github.com/NeoXider/CoreAI/releases/tag/v7.54.0).

## Start Here

| Area | Entry Point | Use When |
|---|---|---|
| Supplied multiplayer sessions | [MULTIPLAYER_SESSIONS.md](CoreAI/MULTIPLAYER_SESSIONS.md) | You need trusted per-operation actor context, persistence partitions, actor-specific stop, chat-service ownership or host integration limits. |
| CoreAI Unity package | [Assets/CoreAiUnity/Docs/DOCS_INDEX.md](../Assets/CoreAiUnity/Docs/DOCS_INDEX.md) | You need setup, chat UI, streaming, settings, architecture, tests, or Unity integration docs. |
| Portable CoreAI package | [Assets/CoreAI/Docs/README.md](../Assets/CoreAI/Docs/README.md) | You need host-agnostic contracts: agents, tools, routing, MEAI, Lua sandbox, and tool-calling rules. |
| CoreAI Mods (Lua, optional) | [Assets/CoreAIMods/README.md](../Assets/CoreAIMods/README.md) · [INSTALL.md §3](../INSTALL.md#3-mods-module-lua) | You need the `com.neoxider.coreaimods` package: Lua sandbox, `execute_lua`/`manage_mods` tools, mod runtime, Roblox-style world API. |
| CoreAI Hub (UI Toolkit, optional) | [Assets/CoreAIHub/README.md](../Assets/CoreAIHub/README.md) · [INSTALL.md §4](../INSTALL.md#4-hub-module-ui-toolkit) | You need the `com.neoxider.coreaihub` tabbed Hub window and its page registry. |
| CoreAI MCP (optional) | [Assets/CoreAIMcp/README.md](../Assets/CoreAIMcp/README.md) | You need the `com.neoxider.coreaimcp` in-game MCP server so an external agent can drive a running game. |
| CoreAI Mirror (optional) | [Assets/CoreAIMirror/README.md](../Assets/CoreAIMirror/README.md) | You need the `com.neoxider.coreaimirror` Mirror transport for online worlds. |
| CoreAI Benchmark (dev-only) | [Assets/CoreAIBenchmark/README.md](../Assets/CoreAIBenchmark/README.md) | You need the `com.neoxider.coreaibenchmark` LLM game-creation benchmark harness. |
| Example game | [Assets/_exampleGame/README.md](../Assets/_exampleGame/README.md) | You need the RogueliteArena sample, scene setup, progression, or AI wave planning notes. |
| Demo scenes | [Assets/CoreAI.Demos/README.md](../Assets/CoreAI.Demos/README.md) | You want a runnable scene for one feature (chat, skills, Lua mods, world commands, multiplayer, materials). |
| NeoxiderTools | [NeoxiderTools repository](https://github.com/NeoXider/NeoxiderTools) | You need the shared utility toolkit (`com.neoxider.tools`, a Git dependency in `Packages/manifest.json`, used by the example game). `Assets/NeoxiderTools/Docs/` is an old vendored snapshot (v7.13.19) of its docs; the installed package is authoritative. |

The full seven-package dependency graph and install profiles (Base / +Mods / +Hub / Full) live in
[INSTALL.md](../INSTALL.md).

## Common Reading Paths

Use these paths when you have a concrete task:

| Goal | Read In This Order |
|---|---|
| Install or wire CoreAI in a Unity scene | CoreAiUnity docs index -> quick start -> settings/runtime architecture |
| Debug LLM requests, streaming, or WebGL SSE | CoreAiUnity streaming docs -> CoreAI routing docs -> transport-specific source comments |
| Add a new LLM tool | Tool-calling best practices -> AgentBuilder -> relevant tests |
| Expose Lua or AI-authored scripts | Lua sandbox security -> runtime binding code -> sandbox tests |
| Decide what to build next | [Roadmap](ROADMAP.md) (§4 ladder) -> [PLAN.md](../PLAN.md) (current status) -> [TODO.md](../TODO.md) (open work by rung) |

## Roblox-style mod API (Rbx)

The Lua surface a mod builds worlds with, and the artifacts it produces:

| Document | Purpose |
|---|---|
| [Assets/CoreAI/Docs/RBX_API.md](../Assets/CoreAI/Docs/RBX_API.md) | User-facing reference: `Instance.new`, datatypes, services, `BasePart.Material`/`MaterialVariant`, `MaterialService`, and `Part.Color`, saving/loading a world, the documented `RemoteFunction` timeout deviation. |
| [CoreAIMods/WORLD_PACKAGE.md](CoreAIMods/WORLD_PACKAGE.md) | The `.world` package format, validation limits, manual slots vs. the autosave ring, the confirm/reject load flow, and runtime session replacement. |
| [Assets/CoreAIMods/Runtime/RbxApi/Unity/PROCEDURAL_MATERIALS.md](../Assets/CoreAIMods/Runtime/RbxApi/Unity/PROCEDURAL_MATERIALS.md) | The procedural shader catalog behind `Enum.Material`: it defines all 45 items and the magenta diagnostic fallback; the packaged texture catalog overrides thirty-six of them at runtime, and the other nine stay procedural. |
| [Assets/CoreAIMods/Runtime/RbxApi/Unity/TEXTURE_MATERIALS.md](../Assets/CoreAIMods/Runtime/RbxApi/Unity/TEXTURE_MATERIALS.md) | Catalog-driven texture materials: thirty-six packaged CC0 sets, project-local override catalog for any of the 45 items (ambientCG downloader, Quixel Bridge / Fab importer), projection and tint rules. |
| [CoreAIMods/RBX_API_SKILL.md](CoreAIMods/RBX_API_SKILL.md) | How the agent-facing `Rbx API` skill text is produced and kept in sync. |
| [CoreAIMods/ROBLOX_API_ROADMAP.md](CoreAIMods/ROBLOX_API_ROADMAP.md) | The one MVP ladder of record (MVP0–MVP19, renumbered 2026-09-24 with an old → new table) and every locked API decision. |
| [CoreAIMods/mod-authoring.md](CoreAIMods/mod-authoring.md) | Writing a CoreAI Lua mod: the `@coreai` header, the always-available mod API, capability tiers, Roblox-style signals and services, the per-resume execution budget, bundled mods. |
| [CoreAIMods/CHARACTER_MOTOR_BRIDGE.md](CoreAIMods/CHARACTER_MOTOR_BRIDGE.md) | Driving Rbx characters with the game's own character controller: `IRbxCharacterMotor` / `IRbxCharacterMotorProvider` and the rules a bridge must keep. |
| [CoreAIMods/SCRIPT_ENGINE_SEAM.md](CoreAIMods/SCRIPT_ENGINE_SEAM.md) | The engine-neutral scripting contracts and the Lua-CSharp adapter layer behind them. |
| [CoreAIMods/mod-system.md](CoreAIMods/mod-system.md) | Mod system design spec: the manifest header, bundled sources and the seeder, the Hub Mods tab, the quarantine policy. |
| [01](CoreAIMods/RobloxReference/01_SCRIPTS_AND_SCHEDULER.md) · [02](CoreAIMods/RobloxReference/02_MULTIPLAYER_REPLICATION.md) · [03](CoreAIMods/RobloxReference/03_SERVICES_AND_DATA.md) | The normative Roblox behaviour rulebooks the roadmap cites: scripts and scheduler, multiplayer and replication, services and data. |

## Agents

| Document | Purpose |
|---|---|
| [CoreAI/AGENT_ROLES_AND_TOOLS.md](CoreAI/AGENT_ROLES_AND_TOOLS.md) | The built-in agent roles, the tools each one has wired by default, and what that lets it do; every claim names the type or method behind it. |
| [CoreAI/agent-vision.md](CoreAI/agent-vision.md) | Agent vision: capturing what a camera renders for the LLM, and the rule that the player's camera is never hijacked. |

## Product And Planning

| Document | Purpose |
|---|---|
| [ROADMAP.md](ROADMAP.md) | Where CoreAI is going: the MVP ladder, what has shipped, what is next. |
| [BENCHMARK_LEADERBOARD.md](BENCHMARK_LEADERBOARD.md) | Public community leaderboard for the Game-Creation Benchmark: ranked results, suite versioning, and the score-submission workflow. |
| [CoreAIMods/MOD_SHARING.md](CoreAIMods/MOD_SHARING.md) | Shareable mod bundle format, Hub export/import flow, import safety, and the proposed community mod gallery process. |
| [ARCHITECTURE_RULES.md](ARCHITECTURE_RULES.md) | Normative architecture rules for the Roblox-API track and every new CoreAI module. |

## Developer Documents

Not user documentation; listed so maintainers can find them.

| Document | Purpose |
|---|---|
| [TODO.md](../TODO.md) | Live priority tracker: open work by rung, verification gates, completed archive. |
| [PLAN.md](../PLAN.md) | Current status and the next steps. |
| [dev-docs/README.md](../dev-docs/README.md) | Index of design notes, plans, research, the non-blocking backlog and the release checklist. |
| [CONTRIBUTING.md](../CONTRIBUTING.md) | Git hooks, CI jobs, code style, the pre-commit checklist. |

## Private planning material

Files under `Docs/LocalBusinessPlans/` are intentionally local/untracked (gitignored) and
are **not** part of the public package documentation surface. Do not link them from
user-facing docs; if a planning conclusion becomes product guidance, copy the stable
technical decision into a public package document instead.

## Documentation Rules

- Write all documentation in English; quoted model inputs may stay verbatim and are marked as quoted.
- Keep changelogs historical; move current user guidance into stable docs.
- Plans, audits, backlogs, research and process notes go in `dev-docs/`, never in `Docs/` or a package
  `Docs/` folder.
- Link every new stable guide from the nearest package index and, when it is
  repository-wide, from this file.
- Keep XML documentation in source concise and contract-focused; avoid mechanical
  summaries such as "Executes X API operation".
