# Agent roles and profiles

**Role catalog, placement, models:** [AI_AGENT_ROLES.md](../AI_AGENT_ROLES.md).

**Prompts and built-in IDs in code:** [DEVELOPER_GUIDE.md §5](../DEVELOPER_GUIDE.md); constants **`BuiltInAgentRoleIds`**; tests **`AgentRolesAndPromptsTests`**.

**Practice:** new roles — stable string id, system prompt in **`Resources/AgentPrompts/System/<Id>.txt`** or in the manifest (**Create → CoreAI → Agent Prompts Manifest**).

**Debugging:** role appears in **`[ToolCall]`** diagnostic lines (via `ILog` / `Log.Instance`), in **`[Llm]`** request/response logs, and in **`ApplyAiGameCommand`**; end-to-end **`traceId`** — see [DEVELOPER_GUIDE §3–4](../DEVELOPER_GUIDE.md). Since v1.5.0, tool-call events also publish to MessagePipe (`LlmToolCallStarted/Completed/Failed`), see §3.3.
