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
| Example game | [Assets/_exampleGame/README.md](../Assets/_exampleGame/README.md) | You need the RogueliteArena sample, scene setup, progression, or AI wave planning notes. |
| NeoxiderTools | [Assets/NeoxiderTools/Docs/README.md](../Assets/NeoxiderTools/Docs/README.md) | You need shared utility/toolkit documentation used by this Unity project. |

## Common Reading Paths

Use these paths when you have a concrete task:

| Goal | Read In This Order |
|---|---|
| Install or wire CoreAI in a Unity scene | CoreAiUnity docs index -> quick start -> settings/runtime architecture |
| Debug LLM requests, streaming, or WebGL SSE | CoreAiUnity streaming docs -> CoreAI routing docs -> transport-specific source comments |
| Add a new LLM tool | Tool-calling best practices -> AgentBuilder -> relevant tests |
| Expose Lua or AI-authored scripts | Lua sandbox security -> runtime binding code -> sandbox tests |
| Decide what to build next | Module audit -> TODO -> orchestration plan |
| Think about packaging or monetization | Local business plans -> module audit -> public README positioning |

## Product And Planning

| Document | Purpose |
|---|---|
| [TODO.md](../TODO.md) | Live backlog, completed archive, and remaining feature-level debt. |
| [TODO/MultiAgent_Orchestration_v2.0.md](../TODO/MultiAgent_Orchestration_v2.0.md) | Multi-agent orchestration plan. |

## Business Plans

Files under `Docs/LocalBusinessPlans/` are intentionally local/ignored planning
material. They are useful for monetization and packaging strategy, but they are not
part of the public package documentation surface.

If a business-plan conclusion becomes product guidance, copy the stable decision
into a public package document instead of linking private planning notes from
user-facing docs.

## Documentation Rules

- Keep detailed package docs in English unless the file is explicitly marked `_RU`.
- Keep changelogs historical; move current user guidance into stable docs.
- Link every new stable guide from the nearest package index and, when it is
  repository-wide, from this file.
- Keep XML documentation in source concise and contract-focused; avoid mechanical
  summaries such as "Executes X API operation".
