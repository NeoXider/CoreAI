# CoreAI Portable Documentation

This folder documents the host-agnostic CoreAI layer: agents, prompts, tools,
LLM routing, MEAI integration, memory, Lua safety, and runtime contracts that do
not depend on Unity scene objects.

Canonical language is English because this package ships as `com.nexoider.coreai`.
Unity-specific setup lives under [`Assets/CoreAiUnity/Docs/`](../../CoreAiUnity/Docs/).
Russian files are kept only when the filename is explicitly marked `_RU`.

## Pick A Path

| If You Need To | Start With |
|---|---|
| Build or configure an agent | [AGENT_BUILDER.md](AGENT_BUILDER.md) |
| Add reliable tools for an LLM role | [TOOL_CALLING_BEST_PRACTICES.md](TOOL_CALLING_BEST_PRACTICES.md) |
| Understand how tools reach MEAI | [MEAI_TOOL_CALLING.md](MEAI_TOOL_CALLING.md) |
| Route requests across local, HTTP, or Unity hosts | [LLM_ROUTING.md](LLM_ROUTING.md) |
| Expose AI-authored Lua safely | [LUA_SANDBOX_SECURITY.md](LUA_SANDBOX_SECURITY.md) |
| Game Lua API, mods, Full mode | [LUA_GAME_API.md](LUA_GAME_API.md) |
| Lua do's and don'ts | [LUA_BEST_PRACTICES.md](LUA_BEST_PRACTICES.md) |
| Keep tool logic free of Unity APIs | [ENGINE_AGNOSTIC_TOOLS.md](ENGINE_AGNOSTIC_TOOLS.md) |

## File Index

| File | Topic |
|------|--------|
| [AGENT_BUILDER.md](AGENT_BUILDER.md) | Fluent `AgentBuilder`: tools, modes, memory, recipes |
| [ENGINE_AGNOSTIC_TOOLS.md](ENGINE_AGNOSTIC_TOOLS.md) | Tools and prompts without Unity APIs |
| [LESSON_ORCHESTRATION.md](LESSON_ORCHESTRATION.md) | Lesson/practice hooks: runtime context, tool policy, tests |
| [LLM_ROUTING.md](LLM_ROUTING.md) | Execution modes, portable routing contracts, usage sinks, timeouts |
| [LUA_SANDBOX_SECURITY.md](LUA_SANDBOX_SECURITY.md) | Lua sandbox boundary, removed APIs, execution limits, binding rules, and escape-test checklist |
| [LUA_GAME_API.md](LUA_GAME_API.md) | Game Lua API reference: capabilities, mods, world, Full, LLM tools |
| [LUA_BEST_PRACTICES.md](LUA_BEST_PRACTICES.md) | Best practices and anti-patterns for Lua in games |
| [MOONSHARP_NATIVE_APIS.md](MOONSHARP_NATIVE_APIS.md) | MoonSharp native APIs vs CoreAI wrappers |
| [LUA_ACCESS_MODES_AUDIT.md](LUA_ACCESS_MODES_AUDIT.md) | AI access modes: Read through Full audit |
| [TOOL_CALLING_BEST_PRACTICES.md](TOOL_CALLING_BEST_PRACTICES.md) | Tool schema, idempotency, duplicate calls, SkillSet organization, result sizing, and tests |
| [MEAI_TOOL_CALLING.md](MEAI_TOOL_CALLING.md) | MEAI pipeline: `ILlmTool` to `AIFunction`, forced tool modes |
| [MEAI_TOKENS_FACT_VS_ESTIMATE.md](MEAI_TOKENS_FACT_VS_ESTIMATE.md) | Provider `usage` vs client estimates; SSE `include_usage`; HTTP vs orchestrator timeouts |
| [SERVER_MANAGED_PROTOCOL.md](SERVER_MANAGED_PROTOCOL.md) | Server-managed API contract, auth flow, request shape, and response handling |

## Maintenance Notes

- Keep this index updated whenever a stable CoreAI guide is added.
- Put short decision rules near the top of each guide; detailed reference material
  should follow after the reader knows when it matters.
- Keep XML documentation concise and contract-oriented. Explain behavior,
  ownership, inputs, outputs, and failure modes; avoid repeating method names.

Related entry points: root [README.md](../../../README.md)
and [CoreAiUnity DOCS_INDEX.md](../../CoreAiUnity/Docs/DOCS_INDEX.md).
