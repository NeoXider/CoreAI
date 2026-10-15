# Agent roles and profiles

**Built-in roles and their default tools:** [AGENT_ROLES_AND_TOOLS.md](../../../../Docs/CoreAI/AGENT_ROLES_AND_TOOLS.md). **Role design, placement, models:** [AI_AGENT_ROLES.md](../AI_AGENT_ROLES.md).

**Prompts and built-in IDs in code:** [DEVELOPER_GUIDE.md §5](../DEVELOPER_GUIDE.md); constants **`BuiltInAgentRoleIds`**; tests **`AgentRolesAndPromptsTests`**. The typed **`RoleId`** struct (implicit `string` conversions, statics like `RoleId.SmartChat`) is preferred over inline role-string literals.

**Practice:** new roles — stable string id, system prompt in **`Resources/AgentPrompts/System/<Id>.txt`** or in the manifest (**Create → CoreAI → Agent Prompts Manifest**), or in code with `AgentBuilder.WithSystemPrompt(...)`.

**Debugging:** role appears in **`[ToolCall]`** diagnostic lines (via `ILog` / `Log.Instance`), in **`[Llm]`** request/response logs, and in **`ApplyAiGameCommand`**; end-to-end **`traceId`** — see [DEVELOPER_GUIDE §3–4](../DEVELOPER_GUIDE.md). Tool-call events are also published to MessagePipe (`LlmToolCallStarted/Completed/Failed`), see DEVELOPER_GUIDE §3.3.
