# AI orchestration

**Current code map:** [DEVELOPER_GUIDE.md §3](../DEVELOPER_GUIDE.md) (flow `RunTaskAsync` → `ILlmClient` → `ApplyAiGameCommand` → Lua).

**Normative:** [DGF_SPEC.md §3.4](../DGF_SPEC.md) — **`QueuedAiOrchestrator`**, priorities (**`AiTaskRequest.Priority`**), **`CancellationScope`**, LLM timeout, **`LlmRoutingManifest`**, metrics (**`GameLogFeature.Metrics`**).

**Extension points in code today:**

- Task dispatch: **`IAiOrchestrationService`**, **`AiTaskRequest`** (role, hint, Lua repair fields, optional **`TraceId`**).
- Model calls: the container resolves **`ILlmClient`** as **`LoggingLlmClientDecorator`** (inside: LLMUnity / OpenAI HTTP / stub); timeout and **`[Llm]`** logs — see [DEVELOPER_GUIDE §3–4](../DEVELOPER_GUIDE.md), [LLMUNITY_SETUP_AND_MODELS §1](../LLMUNITY_SETUP_AND_MODELS.md).
- After the model responds: **`ApplyAiGameCommand`** (`AiEnvelope` + **`TraceId`**, then on Lua success/failure — **`LuaExecutionSucceeded`** / **`LuaExecutionFailed`** with the same trace).
- Roles and prompts: **`BuiltInAgentRoleIds`**, chain in **`AgentPromptsInstaller`**.

Next for your title: a queue/budget wrapper over **`IAiOrchestrationService`**, per-role rate policy — see [AI_AGENT_ROLES.md §6](../AI_AGENT_ROLES.md).
