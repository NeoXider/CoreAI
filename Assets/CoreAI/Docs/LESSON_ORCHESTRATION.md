# CoreAI Lesson Orchestration

`CoreAI.Core` provides the generic orchestration hooks needed by lesson/practice games while domain concepts stay in the host project.

## Runtime Context

Use `AgentMemoryPolicy.SetRuntimeContextProvider(roleId, provider)` for role-specific context such as the current lesson slot, practice attempt, topic id, and allowed pedagogical behavior. The provider output is appended to the system prompt under `## Runtime Context`.

Global `IAiPromptContextProvider` registrations still work and run after the per-role provider.

## Tool Policy

Use `AiTaskRequest.AllowedToolNames` to narrow the role's registered tools for one turn:

- `null` or empty: all role tools are available.
- `ForcedToolMode = None`: no tools are sent.
- `ForcedToolMode = RequireAny`: the request expects at least one available tool.
- `ForcedToolMode = RequireSpecific`: set `RequiredToolName` and include that tool in the allowlist.

RedoSchool can use this to keep theory turns text-only and require concrete practice tools for check/quiz slots.

## Deterministic Tests

`ScriptedLlmClient` is a deterministic `ILlmClient` for EditMode/PlayMode tests. Add context-marker rules with `WhenContextContains(...)` and return stable replies without provider keys, network, or local models.

`ILlmToolCallHistory` records started/completed/failed tool lifecycle events by `LlmToolCallInfo.CallId`, so tests can assert that the expected tool was or was not called.

## Structured Tool Results

`LlmToolResultEnvelope` is a portable JSON envelope for tool outcomes:

- `ToolName`
- `Action`
- `Success`
- `Score`
- `Summary`
- `PayloadJson`

This lets projects replace free-form result strings with stable machine-readable feedback.

## Trace Hooks

`IAgentTurnTraceSink` receives `AgentTurnTrace` records containing prompt preview, user payload, assistant response, model/profile metadata, token usage, and errors. The default sink is no-op; tests can use `InMemoryAgentTurnTraceSink`.
