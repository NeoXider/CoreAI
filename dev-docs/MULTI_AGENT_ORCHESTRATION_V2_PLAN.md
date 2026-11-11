# Orchestrator mode (P14) — design against the current tree

> **Status (2026-11-11): core, request hooks, chat driver, HUD and idle-deadline progress landed; the settings
> toggle, the scope registration and per-conversation history are root's.** The portable core lives in
> `Assets/CoreAI/Runtime/Core/Features/Orchestration/SubAgents/` with tests in
> `Assets/CoreAI/Tests/EditMode/SubAgents/` (also linked into `tools/portable/Tests`); the player page is
> `Assets/CoreAiUnity/Docs/ORCHESTRATOR_MODE.md`. Package P14 in
> `Docs/CoreAIMods/ROBLOX_API_ROADMAP.md`; TODO.md [R9]. This note replaces the 2026-10 "v2" plan, whose
> statics on `CoreAISettings`, fixed 60 s agent timeout and `AgentLlmTool`/`IAgentRegistry` shapes are
> superseded (§9).

## 1. Goal

Opt-in orchestrator mode for any app or game on the framework, in a built player and in WebGL:

- a **lead** agent plans and hands each task to a focused **sub-agent** that starts with a clean context;
- the sub-agent's answer returns to the lead as the **tool result** of `delegate_task`;
- a new player instruction during a plan **joins a queue** and never rewrites running work;
- **Stop** cancels the lead and everything the plan started, through one scope;
- per-plan **budgets** (calls, tokens, parallelism; parallelism 1 by default on one local endpoint);
- one **status source** for the HUD line, the in-world chat line and the completion notification;
- **off by default**: with the mode off nothing is registered and a single-agent turn is byte-for-byte what
  it was.

Non-goals here: the lead role's prompt text (P15 role data), the HUD skin and the "make me a game" flow
(flagship content), true parallel inference on one local model.

## 2. Shape

```
player text ──► OrchestratorSession.Submit(text, parent)
                 ├─ no plan running ─► new OrchestrationPlan (scope, budget ledger, admission gate)
                 │                     host runs the LEAD turn:
                 │                       AiTaskRequest { RoleId = lead, RequestTools = [session.DelegateTool],   (I2)
                 │                                       TraceId = parent.LeadTraceId }
                 │                       token = Link(request token, plan.Scope.Token)
                 │                     lead calls delegate_task(subagent, task, context?, task_id?)
                 │                       └─► SubAgentDelegator ─► IAiTaskResultService (AiOrchestrator, BELOW the queue)
                 │                              fresh request: Hint = task + context, allowlisted tools,
                 │                              HistoryMode = None (I1), own trace + scope, deadline
                 │                       ◄── {"ok":true,"task_id":"t1","subagent":"builder","result":"..."}
                 │                     lead turn returns ─► session.CompletePlan(outcome) ─► session.TryStartNext(...)
                 └─ plan running ─► PlayerInstructionQueue (text only; promote / cancel)
Stop ──► session.Stop() ─► plan.Scope.Cancel(PlayerStop) ─► every linked token (lead + sub-agents + waiters)
HUD  ──► IOrchestrationStatusSource.Changed ─► OrchestrationStatusText.FormatLine ("builder: walls 2/5")
```

## 3. Decisions, with what could go wrong and what the code says

Line numbers are as of this tree; other agents are editing these files, so search by the quoted symbol.

### 3.1 Options are a DI-registered instance

`OrchestratorModeOptions` (new) is registered with `builder.RegisterInstance(...)`, the same shape as
`AiOrchestrationQueueOptions` (`LlmPipelineInstaller.cs:173-177`), not statics on `CoreAISettings` (the old
plan). *Risk:* a mid-plan change half-applies. *Answer:* `OrchestratorSession.TryBeginPlanCore` copies the
budget (`CreateBudget()`) into the plan; a change applies from the next plan. Defaults: `Enabled = false`,
`MaxSubAgentCallsPerPlan = 8`, `MaxTokensPerPlan = 0` (no cap), `MaxParallelSubAgents = 1`,
`MaxContextChars = 4000`, `MaxQueuedInstructions = 8`, `DelegateToolName = "delegate_task"`.

### 3.2 Deadlines follow the current model, not a fixed 60 s

The current model (`ICoreAISettings.cs:44-48`): `LlmRequestTimeoutSeconds` (total ceiling, default `0` =
off) and `LlmResponseWaitTimeoutSeconds` (180 s wait for the first byte or between deltas, enforced per HTTP
response in `MeaiOpenAiChatClient.cs:833`), plus the per-tool `DefaultToolTimeoutMs = 30000`
(`ICoreAISettings.cs:227`). What each one does to a delegation:

| Deadline | Where | Effect on `delegate_task` | Answer |
|---|---|---|---|
| Per-tool 30 s | `ToolExecutionPolicy.cs:825` reads `ILlmTool.ToolTimeoutMsOverride` | Would cut every delegation (sub-agent turns take minutes) | `DelegateTaskTool.ToolTimeoutMsOverride = 0`; the delegator enforces the definition's own `Deadline` from the moment the run is admitted (waiting for a slot does not count) through `ILlmAsyncMarshaler.DelayAsync` |
| Response wait 180 s | per HTTP response | None: the lead has no HTTP request open while its tool body runs | none needed |
| Total ceiling, non-streaming | `TimeoutLlmClientDecorator.CompleteAsync` (`:600`) bounds the whole call | A sub-agent turn (non-streaming, one `CompleteAsync` over its whole tool loop) is cut at the ceiling when a host sets one | effective sub-agent deadline = min(definition, ceiling); documented |
| Total ceiling, streaming lead | idle window re-armed per chunk (`TimeoutLlmClientDecorator.cs:911`) and, on Unity, `CoreAiChatService` re-armed on tool events matching actor + trace + role (`CoreAiChatService.cs:333-367`) | The lead streams nothing and its trace/role never match a sub-agent's events, so a delegation longer than the window ends the lead when the ceiling is set | I5 (landed): the lead request carries `OrchestrationPlan.Progress`; both the core decorator and `CoreAiChatService` re-arm on its reports |
| Mid-stream-abort drain | `MeaiLlmClient.cs:1073` passes `CancellationToken.None` on purpose | The delegate body would get no cancellation at all | the run token is always linked to the plan scope and the definition deadline, and `CompletePlan` cancels the scope when the lead turn returns |

### 3.3 Actor and session scopes; cancellation scope keys

*Risk:* `QueuedAiOrchestrator` treats a cancellation scope as latest-wins (`QueuedAiOrchestrator.cs:1018-1048`):
an admitted request with the same scope key cancels the previous one. The legacy key ignores the role
(`ResolveLegacyCancellationScopeKey`, `:1230-1251`), the actor key includes it (`:1219-1228`). A sub-agent
sharing the lead's scope would cancel the lead; two parallel sub-agents sharing one would cancel each other.
*Answer:* `SubAgentRequestFactory` gives every run its own scope `"{leadScope|orchestrator}/{planId}/sub/{taskId}.{attempt}"`
and trace `"{leadTrace}.sub.{taskId}.{attempt}"`; the run carries the lead's trusted `ActorContext` (memory
partition and connection identity stay the player's), so `CancelTasks(sessionId)` still matches it
(`:814`) if a host routes sub-agents through the queue. Tests: `EveryRun_GetsItsOwnTraceAndCancellationScope`.

### 3.4 Sub-agents run below the admission queue

*Risk:* the lead's turn holds one of `MaxConcurrent = 2` slots (`AiOrchestrationQueueOptions.cs:10`, pump at
`QueuedAiOrchestrator.cs:314`) while it waits for the tool result. A queued sub-agent needs a second slot;
with two players' leads holding both slots, both sub-agents wait forever (until a deadline) — a distributed
deadlock, and with `MaxConcurrent = 1` a certain one. *Answer:* the runner is `IAiTaskResultService`
(`IAiOrchestrationService.cs:12-19`) and the composition injects the `AiOrchestrator` registered AsSelf
(`CorePortableInstaller.cs:112-130`), not the queued service (`:131-136`). The sub-agent borrows the lead's
slot — correct, because the lead makes no provider call while its tool body runs. Parallelism above 1 then
exceeds the queue cap by at most `MaxParallelSubAgents - 1` per plan; that is the explicit cloud opt-in.

### 3.5 Both tool loops

The lead usually runs on the Unity streaming loop (`MeaiLlmClient.CompleteStreamingAsync`), a sub-agent on
the core non-streaming loop (`SmartToolCallingChatClient` via `MeaiLlmClient.CompleteAsync`). Both execute
tools through `ToolExecutionPolicy`, which resolves metadata from the request's tool list (`ToolNameIndex`,
`ToolExecutionPolicy.cs:130`), so a request-scoped tool (I2) is honoured by both. Contract of
`DelegateTaskTool`:

- `IAIFunctionLlmTool` built with `AIFunctionFactory.Create`, body returned through
  `MeaiToolTaskBridge.Publish` (WebGL: MEAI awaits with `ConfigureAwait(false)` inside its binary);
- `IsMutating = false`: mutating tools are chained one at a time (`ToolExecutionPolicy.cs:1460`); declared
  read-only, the policy starts a batch's delegations together (up to `MaxParallelToolCalls = 4`,
  `:1424`) and the plan gate decides how many run. Effective parallelism = min(`MaxParallelSubAgents`,
  `MaxParallelToolCalls`);
- `AllowDuplicates = true`: a retried identical delegation is legitimate; the call budget bounds a loop;
- not `IJsonInvocableLlmTool`, so the `call_skill_tool` proxy has no JSON entry point into it; a proxy that
  falls back to the AIFunction binding is still refused by the runtime guard (§3.7, layer 3);
- result JSON with `ok` first: `MaxToolResultChars = 8000` (`ICoreAISettings.cs:220`) soft-truncates the
  tail, and `IsToolResultSuccess` (`ToolExecutionPolicy.cs:2145`) reads `ok:false`/`error` as a failed
  call. Definitions cap results at 4000 chars by default.
- Three failed tool calls in a row end the lead's tool loop (`ToolExecutionPolicy.IsMaxErrorsReached`,
  `:167`, default 3); three refused delegations after a spent budget therefore stop the lead, which is the
  desired end, but a lead that retries a busy task three times is stopped too (the refusal text tells it
  to wait for the result).

WebGL: the sub-agent's non-streaming request does not use the single SSE bridge the streaming lead uses, and
the lead's SSE stream is idle between roundtrips, so they do not interleave.

### 3.6 Context isolation and conversation identity

*Risk (checked):* `AiOrchestrator.BuildChatHistoryAsync` reads `GetChatHistory(roleId, ...)` (`:1683`) and the
teardown appends by role (`:2107`, `:2306`, `:2325`). Every built-in role except Programmer has chat history
on by default (`AgentMemoryPolicy.cs:609-616`). A sub-agent on Builder would read the player's direct chat
with Builder plus every earlier sub-agent turn, and write its own turn into it; a sub-agent on the lead's role
would read the lead's whole conversation. `AiTaskRequest` has no way to ask for a history-free turn.
*Answer:* the core builds a request that carries only `Hint = task (+ "Context from the lead:" + context)`,
no attachments, no `SystemPrompt`, definition instructions in the cache-safe `RequestSystemInstructions`
tail, `SourceTag = "SubAgent"` (never `"Chat"`, which turns history on, `AiOrchestrator.cs:2893`), and never
the plan goal or a sibling's result. Integration change **I1** (landed) adds `AiTaskRequest.HistoryMode = None`,
honoured in `ResolveRoleConfigForRequest`, which both the read and every append already pass through; the
test `RoleWithChatHistoryOn_SubAgentNeitherReadsNorExtendsIt` holds it.

What a sub-agent still gets from its role, on purpose: the role's system prompt and response policy and the
runtime world context. Durable memory stays out (`MemoryMode = None`, owner decision D2) unless the
definition opts in with `useRoleMemory: true`.

Conversation identity: two conversations on one role must not share history (MULTI_CHAT_PLAN §4). The core
already keys sessions by conversation: `OrchestratorSessionRegistry.ResolveConversationKey(conversationId,
actor, role)` falls back from the conversation id to `actor:{id}/{session}/{role}` to the role, the same chain
`ConversationId` history keys will use (I8). Sub-agents need no conversation id: with I1 each delegation is
an ephemeral conversation that reads and writes nothing.

### 3.7 No nested delegation — three layers

1. Data: `SubAgentDefinition` refuses `delegate_task` in its allowlist (any case); `SubAgentRegistry`
   refuses a renamed delegate tool too.
2. Request: the allowlist is never `null` (`FilterToolsForRequest` treats `null` as every role tool,
   `AiOrchestrator.cs:2513`) and the factory strips the delegate name again; the tool itself is offered on
   the lead's request only (I2), never on a role. The real-orchestrator test registers the delegate tool on
   the sub-agent's role by mistake and asserts the provider request still lacks it.
3. Runtime: an instance `AsyncLocal<int>` depth in `SubAgentDelegator`, set inside the run, flows into the
   sub-agent turn and any tool it calls; a re-entry by any route (a skill proxy that allowlists the name, a
   host registration) is refused with `nested_delegation_refused` and never reaches the runner. Siblings
   started by the policy run in separate execution contexts and do not see each other's mark. The guard
   relies on the execution context flowing, which holds for the direct path of §3.4; a host that routes
   sub-agents through the queue keeps layers 1 and 2.

### 3.8 Budgets per plan

`OrchestrationBudgetLedger`: a call is reserved **before** waiting for a slot, so the call past the budget is
refused at once (no wait); a reservation that never runs (cancelled while waiting, refused at admission) is
refunded. Tokens are summed from `LlmCompletionResult.TotalTokens` (or prompt + completion); the run that
crosses the cap finishes and the next is refused; runs whose provider reported no usage are counted
separately (`CallsWithoutUsage`) so a HUD can say "tokens undercounted". When a budget runs out the plan goes
to `BudgetExhausted` with the typed reason (`call_budget_exhausted` / `token_budget_exhausted`), refuses
every further delegation, and the lead keeps its turn to report.

### 3.9 Cancellation fan-out through one scope

`OrchestrationScope` is one `CancellationTokenSource` with a typed first reason (`PlayerStop`,
`PlanCompleted`, `LeadFailed`, `SessionDisposed`), cancelled but never disposed (the queue's own lifetime
source makes the same choice, `QueuedAiOrchestrator.cs:41-45`). Every run's token links the scope, the lead's
tool invocation token and the definition deadline; a waiter's token links the scope and the invocation.
Cancelling the scope therefore reaches every running and waiting sub-agent synchronously, with no child list.
The host links the lead request's token to the same scope, so `session.Stop()` also stops the lead; the
existing panel Stop path (`CoreAiChatPanel.StopActiveGeneration`, `:4313`, → `StopActorWork`, `:5651`) keeps
cancelling the lead through the queue as today. The lead's own cancellation rethrows
`OperationCanceledException` from the tool body; a scope-only cancellation returns a typed `plan_cancelled`
result. A cancelled sub-agent's partial world changes stay behind the pre-mutation autosave
(`ConfirmedWorldMutationGate`, `AGENT_ROLES_AND_TOOLS.md` §3).

### 3.10 Where the HUD status and the task queue get their data

From `OrchestratorSession` (an `IOrchestrationStatusSource`), never from the static tool-call events
(`CoreAi.cs:858-867`, role/trace keyed and process-wide). `OrchestrationStatus` = plan snapshot (tasks with
state, attempts, steps, first-line result summary; completed/active/total; budget snapshot; stop reason) +
queue snapshot + a revision. `Changed` is raised outside every lock on the thread that changed the state; a
throwing observer is logged and isolated, because observers run inside slot-release bookkeeping. Text:
`OrchestrationStatusText.FormatLine` → `builder: walls 2/5`, `planning 1/3`, `done 5/5`, `stopped 2/5`,
`budget exhausted (calls) 3/5`; `FormatNextLine` → `next: add a moat (+1)`. Step counts come from the host
forwarding sub-agent tool events: `plan.TryRecordActivity(evt.TraceId)` (I7). The queue holds text only and
has no reference to a plan or a request; `Promote` moves an entry to the front, `Cancel` drops it,
`Stop()` clears it by default (`Stop(clearQueue: false)` keeps it; decision D4).

### 3.11 WebGL and the built player

No thread, timer or blocking wait: the admission gate is FIFO over `TaskCompletionSource` created without
`RunContinuationsAsynchronously`, completed outside its lock; deadlines go through
`ILlmAsyncMarshaler.DelayAsync` (Unity passes `UnityMainThreadLlmAsyncMarshaler.Instance`,
`LlmPipelineInstaller.cs:44`); the tool body goes through `MeaiToolTaskBridge.Publish`. A host delay that
faults (a cancelled UniTask bridged into a Task faults) is not treated as an expired deadline. The fitness
test scans the folder for the primitives `WebGlUnsafeAsyncPrimitivesEditModeTests` forbids, for
`UnityEngine`, static settings/singletons and `var`.

### 3.12 Side effects of a sub-agent turn (checked)

- `AiOrchestrator.SanitizeAndPublish` publishes an `ApplyAiGameCommand` envelope for every turn
  (`:2330-2342`) with the sub-agent's role and `SourceTag = "SubAgent"`.
- `LuaCsAiEnvelopeProcessor.Process` executes Lua found in an envelope's text (`:78`) and, for the Programmer,
  schedules repair turns through the queued orchestrator with no token, no actor and no plan (`:141-147`,
  `:197-226`): outside the plan's scope and budget, not stopped by Stop. I6 skips envelope Lua for sub-agent
  turns; a sub-agent acts through tools (`execute_lua` is gated by `ConfirmedWorldMutationGate` and runs
  under the plan's token).
- Structured response policies of the sub-agent's role apply (`IRoleStructuredResponsePolicy`): a definition
  should name a role whose policy accepts a prose report.

### 3.13 Mode off

Nothing is registered (I3 runs only when `Enabled`), no role gains a tool, `AiTaskRequest` gains two fields
whose defaults reproduce today's behaviour (I1 `RoleDefault`, I2 `null`). Existing single-agent tests stay
unchanged by construction; this task edits no existing runtime file.

## 4. Core types (new, engine-free, `CoreAI.Ai.SubAgents`)

| File | Type | Role |
|---|---|---|
| `SubAgentDefinition.cs` | `SubAgentDefinition` | validated data: id, role, description, allowlist (never the delegate tool), routing profile, output / roundtrip / result caps, deadline, instructions |
| `SubAgentRegistry.cs` | `ISubAgentRegistry`, `SubAgentRegistry` | definitions by id; refuses duplicates and a renamed delegate tool |
| `OrchestratorModeOptions.cs` | `OrchestratorModeOptions` | DI options (§3.1) |
| `OrchestrationBudget.cs` | `OrchestrationBudget`, `OrchestrationBudgetSnapshot`, internal ledger | §3.8 |
| `OrchestrationScope.cs` | `OrchestrationScope`, `OrchestrationStopReason` | §3.9 |
| `SubAgentAdmissionGate.cs` | internal FIFO gate | parallelism (§3.11) |
| `OrchestrationPlan.cs` | `OrchestrationPlan`, snapshots, states | tasks, progress, `DeclareTask`, `TryRecordActivity` |
| `PlayerInstructionQueue.cs` | `PlayerInstructionQueue` | enqueue / promote / cancel / dequeue |
| `OrchestrationStatus.cs` | `IOrchestrationStatusSource`, `OrchestrationStatus`, `OrchestrationStatusText` | §3.10 |
| `SubAgentRequestFactory.cs` | `SubAgentParentContext`, `SubAgentRequestFactory` | the isolated sub-agent request |
| `SubAgentDelegationResult.cs` | `SubAgentErrorCode`, wire names, request/result DTOs | typed outcome and tool-result JSON |
| `SubAgentDelegator.cs` | `SubAgentDelegator` | the `delegate_task` body |
| `DelegateTaskTool.cs` | `DelegateTaskTool` | the MEAI tool over the delegator |
| `OrchestratorSession.cs` | `OrchestratorSession` | per-conversation plan + queue + bound tool; the status source |
| `OrchestratorSessionRegistry.cs` | `OrchestratorSessionRegistry` | one session per conversation key; the container singleton |

## 5. Host loop (what the integration does with the core)

1. `OrchestratorSession session = registry.GetOrCreate(ResolveConversationKey(conversationId, actor, leadRole))`.
2. `InstructionSubmission s = session.Submit(text, new SubAgentParentContext(leadRole, traceId, scope, actor, priority))`.
3. `Started`: run `session.CreateLeadRequest(hostRequest, s.Plan)` (lead tools, lead instructions, lead
   trace) with a token linked to `s.Plan.Scope.Token`; then `session.CompletePlan(ok ? Completed : Failed)`; then loop on
   `session.TryStartNext(newParent, out plan, out next)`.
4. `Queued`: show `OrchestrationStatusText.FormatNextLine(session.Current)`; start nothing.
5. Stop: `session.Stop()` plus the existing lead stop.

## 6. Integration changes for root (exact, minimal)

**Landed (2026-11-11): I1, I2, I3, I6, I9.**
- **I1 + memory decision.** `AiTaskRequest.HistoryMode` (`AiTaskHistoryMode.RoleDefault` / `None`) and
  `AiTaskRequest.MemoryMode` (`AiTaskMemoryMode.RoleDefault` / `None`), both copied by `Clone()`. The rules
  live in one internal helper, `AiTaskRequestScopeRules` (in `AiTaskRequest.cs`), used by the orchestrator
  and the inspector. `AiOrchestrator.ResolveRoleConfigForRequest` turns history off (read and append) for
  `None` before the chat source rule; the memory snapshot is loaded only when `AllowsDurableMemory(task)`.
  `SubAgentRequestFactory` sets `HistoryMode = None` and `MemoryMode = None` unless the definition opts in
  (`SubAgentDefinition.UseRoleMemory`, constructor `useRoleMemory: true`); writing memory still needs the
  memory tool in the allowlist. The pinned-gap test is inverted
  (`RoleWithChatHistoryOn_SubAgentNeitherReadsNorExtendsIt`).
- **I2.** `AiTaskRequest.RequestTools`: merged after the role's tools in `AiOrchestrator` (`:209`) and in the
  live `AgentSessionInspector.Inspect`; a request tool replaces a role tool of the same name; the merged list
  then passes `ForcedToolMode` / `AllowedToolNames` like role tools. The serialized-inputs inspector path has
  no request, so nothing to merge there.
- **I3.** `Assets/CoreAiUnity/Runtime/Source/Composition/OrchestratorModeInstaller.cs`:
  `bool RegisterOrchestratorMode(this IContainerBuilder, OrchestratorModeOptions, IEnumerable<SubAgentDefinition>, ILeadInstructionsProvider = null)`
  registers nothing and returns false unless `options.Enabled`; otherwise the options instance,
  `ISubAgentRegistry`, `ILeadInstructionsProvider` and the singleton `OrchestratorSessionRegistry` over
  `c.Resolve<AiOrchestrator>()` (below the queue), `UnityMainThreadLlmAsyncMarshaler.Instance` and
  `c.Resolve<ILog>()`. Root adds one call in `CoreAILifetimeScope.Configure` after `RegisterCore()`.
- **I6.** `LuaCsAiEnvelopeProcessor.Process` returns before parsing Lua when
  `SubAgentRequestFactory.IsSubAgentSourceTag(cmd.SourceTag)`.
- **I9 (core part).** `DeclareTasksTool` (`declare_tasks`, one task per line as `subagent_id: task`, at most
  20 per call, all or nothing) over `OrchestrationPlan.DeclareTask`; `ILeadInstructionsProvider` with
  `DefaultLeadInstructionsProvider` (plan, declare, delegate with full context, order, check, verify, report,
  the call budget and the sub-agent list; batching is suggested only when parallelism is above 1);
  `OrchestratorSession.LeadTools` (delegate + declare) and `OrchestratorSession.CreateLeadRequest(hostRequest, plan)`,
  which copies the host request, adds the lead tools (also to a host allowlist), appends the lead
  instructions to `RequestSystemInstructions` and takes the plan's lead trace when the request has none.
  The lead role's own prompt and per-game lead voices stay P15 role data.

**Landed (2026-11-11, second stage): I4, I5, I7, I8 (as far as the leased files reach).**
- **I4 — chat driver.** `OrchestratorChatDriver` (core, `SubAgents/`) takes a chat turn (source tag `Chat`,
  not a sub-agent tag, non-blank hint, driver active): it starts a plan, or, when the conversation runs one,
  queues the instruction and waits (cancellable) until the plan before it ends, then runs it as its own lead
  turn in its own caller's flow; the lead request is `session.CreateLeadRequest`, run with a token linked to
  the plan's scope; `EndTurn` completes the plan and starts the next waiting turn (FIFO kept even when a turn
  arrives between a plan's end and the start of the next). `Stop(roleId, actor)` empties the queue, cancels the
  waiting turns and then stops the plans (in that order, outside the driver's lock, so a lead that unwinds
  inline cannot start a queued plan). `IsActive` is the player's switch. `CoreAiChatService` gets
  `OrchestratorMode` (set by `TryCreateFromScope` and by the overlay installer when the mode is registered):
  `SendMessageAsync`, `SendMessageResultAsync` and `SendMessageStreamingAsync` route through
  `driver.RunAsync` / `RunStreamingAsync`, and `StopAgent(roleId, actor)` calls `driver.Stop` before the queue
  cancellation. Both chat surfaces end in that `StopAgent`, so the panel needed no edit.
- **I5 — idle deadline.** `LlmRequestProgress` (core, `Features/Llm/`): an allocation-free, never-throwing
  report signal. `AiTaskRequest.Progress` and `LlmCompletionRequest.Progress` (copied in
  `AiOrchestrator.BuildCompletionRequest`). `TimeoutLlmClientDecorator` subscribes its idle deadline's `Touch`
  on both paths, so a buffered request whose owner reports progress is no longer bounded wall-clock, and a
  streaming one is re-armed between chunks; without reports both behave exactly as before. The plan reports
  `OrchestrationPlan.Progress` on every change; the lead request carries it; each sub-agent run carries its own,
  reported on its tool activity (`TryRecordActivity`). Unity side: `CoreAiChatService`'s idle deadline also
  subscribes `request.Progress` (buffered and streaming).
- **I7 — HUD.** `OrchestrationChatOverlayBridge` (core): the overlay's status line shows the plan line of its own
  lead turn (`waiting: ...` while queued), the feed gets one line per task start and finish and one when a plan
  ends (for every conversation), and `/orchestrator [on|off|status]` (alias `/lead`) switches the mode. The
  overlay gained one member in its engine-free core: `AgentStatusTracker.Detail` / `SetDetail` (replaces the
  thinking and running-tool text while set; cleared with the turn). `OrchestratorModeChatPresenter` (Unity,
  `IStartable`/`ITickable`): forwards `CoreAi` tool-call events to `driver.RecordActivity` and ticks the bridge
  when the overlay is registered. The installer registers the driver and the presenter.
- **I8 — conversation id.** `AiTaskRequest.ConversationId` (empty = the role's single conversation) + `Clone`;
  the driver keys sessions by it (falling back to actor session + role). The history keys in the memory store,
  the orchestrator reads/appends, the inspector read, the tool-event info and the two chat surfaces are listed
  for root in the stage report (files outside this lease).
- **Role data (from the roles agent).** `ResolveRoleConfigForRequest` no longer turns chat history on for a
  role enforced as ToolsOnly; a request without a routing profile uses the role definition's
  (`AgentMemoryPolicy.TryGetRoutingProfile`) everywhere the orchestrator resolves one.

**Still pending.**
- The settings toggle and the lifetime-scope registration (root applies them).
- I8 in the memory store and the files that consume it (history per conversation, tool bubbles per
  conversation); the chat panel and the overlay setting a conversation id.
- A model-driven run (MS) building a small game through the mode.

## 7. Tests

Landed (EditMode, also linked into `tools/portable/Tests`): isolation of the captured request (task + passed
context only; no goal, no attachments, no sibling result; own trace and scope; lead actor), no nested
delegation (direct re-entry and through the MEAI binding; sequential lead calls are not mistaken for
nesting), parallelism 1 in call order and 2 at most, call and token budgets with typed refusals and their
twins, refund of a cancelled waiter, the result as the tool result (and `IsToolResultSuccess` agreeing on
success and failure), clipping of results and context, Stop cancelling every in-flight request and the
waiter within one second, isolation of two conversations, `CompletePlan` stopping an orphan run, lead
cancellation rethrowing, deadline expiry and release, refusals (no plan, unknown sub-agent, blank task, busy
task), queue promote/cancel/full/blank, queue never touching a running request, next plan only after the
current, status line and next line, revisions, an isolated throwing observer, activity steps, the session
registry and conversation keys, definition/registry/options validation, wire names, a real-`AiOrchestrator`
run whose provider request carries no lead history and no delegate tool, and an architecture-fitness scan.
With the hooks: a sub-agent on a history-on role neither reads nor extends it; durable memory out by default
and in on opt-in; `HistoryMode` deciding a Chat turn's history (both ways); request tools reaching the
provider, replacing a same-name role tool and passing the allowlist; `Clone` carrying the new fields; the
inspector honouring all three; a lead request through the real orchestrator offering both lead tools and
the lead instructions; `declare_tasks` (prefixes, list markers, empty, cap, no plan, MEAI binding, declared
id run by a delegation); default and replaced lead instructions; the envelope guard and its twin
(`Assets/CoreAIMods/Tests/EditMode/SubAgentEnvelopeLuaEditModeTests.cs`, in `tools/portable/LuaTests`).

Second stage: progress and the decorator with silent twins (buffered and streaming), the driver (three-task
plan through the real orchestrator, queue and FIFO, caller cancel, Stop mid-plan with nothing further running,
pass-through, full queue, two conversations, activity and progress), the overlay bridge (status line, feed
lines, command, `SetDetail`), the role-data hooks with twins, and the PlayMode `FastNoLlm` fixture
`OrchestratorModeChatPlayModeTests` through `CoreAiChatService`.

To do: EM for I8 (history keys and fallbacks, two same-role conversations,
tool-bubble filtering), PM for a scripted three-task plan end to end with HUD updates and Stop mid-plan, MS
for a real model building a small game through the mode (transcript and HUD captured).

## 8. Rejected alternatives

- Statics on `CoreAISettings` for limits — rejected: the architecture rules forbid new statics; options are an instance.
- A fixed 60 s agent timeout — rejected: the tree has a ceiling plus a response wait, and minutes-long sub-agent runs need their own deadline counted from admission.
- Running sub-agents through the queued orchestrator — rejected: the lead holds a slot while waiting, so sub-agents can deadlock the queue (§3.4).
- Registering `delegate_task` on the lead role — rejected: a role-wide tool is shared by every conversation and actor and cannot be bound to one plan; the request-scoped tool (I2) is exact.
- Finding the plan through `LlmRequestContext` in the tool body — rejected: that AsyncLocal frame is pushed inside async iterators and does not survive across yields on the streaming path (`AiOrchestrator.cs:718` notes the same for memory scope).
- A new `IAgentOrchestrator`/`ISubAgentRunner` interface — rejected: `IAiTaskResultService` already is the typed seam and the real orchestrator implements it.
- Passing a trimmed parent history to the sub-agent — rejected: a clean context is the point; the lead passes facts explicitly in `context`.
- An `action: call | parallel | update_prompt | add_tool` tool schema — rejected: parallelism comes from the tool policy running a batch; prompt and tool mutation do not belong in a delegation tool.
- A plan wall-clock ceiling in the core — rejected for now: calls × per-run deadline / parallelism already bounds a plan, and the lead's own ceiling applies; can be a budget field later.
- Sub-agent results as streamed chat text — rejected: they are tool results for the lead; the player sees the status line and the lead's report.
- Stop keeping queued instructions by default — rejected: a player who presses Stop expects silence; `Stop(clearQueue: false)` and the returned list allow a "restore" UI.

## 9. What the older v2 plan said and what changed

| Old plan | Now |
|---|---|
| `SubAgentDefinition` with `ILlmTool` list and model name | definition holds tool **names** (allowlist) and a routing profile; tools stay owned by roles |
| `AgentLlmTool` registered via `AgentMemoryPolicy.GetToolsForRole` | `DelegateTaskTool` bound to one session, offered on the lead request only (I2) |
| `CoreAISettings.MaxParallelAgents = 3`, `AgentCallTimeoutSeconds = 60` | `OrchestratorModeOptions` instance; parallelism 1 by default; per-definition deadline |
| `ExecuteSubAgentsParallelAsync` | parallel delegations are separate tool calls of one batch, bounded by the plan gate |
| version bump v0.8 → v0.9 | the package is at 7.56.0; releases go through `tools/bump_version.py` |

## 10. Owner decisions (2026-11-11)

- D1. The default token budget per plan stays `0` (no cap); presets may set one.
- D2. A sub-agent never sees durable memory by default, also when it shares the lead's role; a definition
  opts in with `useRoleMemory: true` (landed with I1).
- D3. With a positive total request ceiling the lead's idle deadline learns about plan progress, and a
  sub-agent's about its own tool activity (landed with I5: `LlmRequestProgress`, re-armed in
  `TimeoutLlmClientDecorator` and `CoreAiChatService`; a request nobody reports for still times out).
- D4. Stop clears queued instructions (the current default).
- D5. No automatic rollback of a cancelled sub-agent's world changes; the player's undo covers it.
- D6. Envelope Lua from sub-agent turns is skipped (I6, landed).
