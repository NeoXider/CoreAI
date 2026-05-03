# CoreAI portable docs (`Assets/CoreAI/Docs`)

Markdown in this folder documents **host-agnostic** contracts and MEAI integration. **Canonical language:** English (this package ships as `com.nexoider.coreai`). Unity-specific setup lives under [`Assets/CoreAiUnity/Docs/`](../../CoreAiUnity/Docs/). A few legacy Russian-only notes remain as clearly marked `_RU` stubs or plans.

| File | Topic |
|------|--------|
| [AGENT_BUILDER.md](AGENT_BUILDER.md) | Fluent `AgentBuilder`: tools, modes, memory, recipes |
| [ENGINE_AGNOSTIC_TOOLS.md](ENGINE_AGNOSTIC_TOOLS.md) | Tools and prompts without Unity APIs |
| [LESSON_ORCHESTRATION.md](LESSON_ORCHESTRATION.md) | Lesson/practice hooks: runtime context, tool policy, tests |
| [LLM_ROUTING.md](LLM_ROUTING.md) | Execution modes, portable routing contracts, usage sinks, timeouts |
| [MEAI_TOOL_CALLING.md](MEAI_TOOL_CALLING.md) | MEAI pipeline: `ILlmTool` → `AIFunction`, forced tool modes |
| [MEAI_TOKENS_FACT_VS_ESTIMATE.md](MEAI_TOKENS_FACT_VS_ESTIMATE.md) | Provider `usage` vs client estimates; SSE `include_usage`; HTTP vs orchestrator timeouts |
| [MEAI_TOKENS_FACT_VS_ESTIMATE_RU.md](MEAI_TOKENS_FACT_VS_ESTIMATE_RU.md) | **(RU)** Redirect only — see English doc above |
| [WEBGL_SERVER_MANAGED_PLAN_RU.md](WEBGL_SERVER_MANAGED_PLAN_RU.md) | **(RU)** WebGL / server-managed proxy notes (plan) |

**Also:** root [README.md](../../../README.md) / [README_RU.md](../../../README_RU.md) and [DOCS_INDEX.md](../../CoreAiUnity/Docs/DOCS_INDEX.md) link here where relevant.
