# AI orchestration

**Current code map:** [DEVELOPER_GUIDE.md §3](../DEVELOPER_GUIDE.md) (flow `RunTaskAsync` → `ILlmClient` → `ApplyAiGameCommand` → Lua).

**Normative:** [DGF_SPEC.md §3.4](../DGF_SPEC.md) — **`QueuedAiOrchestrator`**, priorities (**`AiTaskRequest.Priority`**), **`CancellationScope`**, LLM timeout, **`LlmRoutingManifest`**, metrics (**`GameLogFeature.Metrics`**).

**Extension points in code today:**

- Task dispatch: **`IAiOrchestrationService`**, **`AiTaskRequest`** (role, hint, Lua repair fields, optional **`TraceId`**).
- Model calls: the container resolves **`ILlmClient`** as **`LoggingLlmClientDecorator`** (portable, `CoreAI.Core` since v1.5.0; uses **`ILog`** instead of `IGameLogger`); inside: LLMUnity / OpenAI HTTP / stub; timeout and **`[Llm]`** logs — see [DEVELOPER_GUIDE §3–4](../DEVELOPER_GUIDE.md), [LLMUNITY_SETUP_AND_MODELS §1](../LLMUNITY_SETUP_AND_MODELS.md). Streaming is the default execution path: when **`ICoreAISettings.EnableStreaming`** is on, `RunTaskAsync` obtains each completion via **`CompleteStreamingAsync`** (through a `CompleteForTaskAsync` helper) so non-interactive tasks share the execute-as-you-stream tool path with chat; it falls back to non-streaming **`CompleteAsync`** only when streaming is disabled.
- Tool-call lifecycle: **`ToolExecutionPolicy`** (portable, `CoreAI.Core`) publishes via **`IToolCallEventPublisher`** → **`MessagePipeToolCallEventPublisher`** (Unity adapter) → `GlobalMessagePipe`. Per-call `[ToolCall]` diagnostic line is written via **`ILog`** (`Log.Instance`). See [DEVELOPER_GUIDE §3.3–3.4](../DEVELOPER_GUIDE.md).
- After the model responds: **`ApplyAiGameCommand`** (`AiEnvelope` + **`TraceId`**, then on Lua success/failure — **`LuaExecutionSucceeded`** / **`LuaExecutionFailed`** with the same trace).
- Roles and prompts: **`BuiltInAgentRoleIds`**, chain in **`AgentPromptsInstaller`**.

Next for your title: a queue/budget wrapper over **`IAiOrchestrationService`**, per-role rate policy — see [AI_AGENT_ROLES.md §6](../AI_AGENT_ROLES.md).
