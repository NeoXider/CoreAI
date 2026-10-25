# Core execution audit

Audit date: 2026-10-23. Source baseline: `69d5bcc37502cd4bb464a3bf9ac0c53b1624b804` (`HEAD` and `origin/main` verified equal).

Status: complete source audit. No implementation changes, builds, provider calls, or Unity operations were performed by this worker.

## Goal and acceptance criteria

Audit engine-free agent execution, turn construction, orchestration, admission, cancellation, and shared contracts. The required behavior includes correct async ordering, bounded queues and response buffering, caller cancellation, failure isolation, scoped state restoration, replaceable policies, and truthful outcomes. Runtime/player and WebGL remain separate verification gates; portable source or test evidence cannot establish device behavior.

The audit follows `AGENTS.md`, `Docs/ARCHITECTURE_RULES.md`, and `Assets/CoreAI/README.md`. The user's requested audit Markdown overrides the repository's general rule against retaining audit reports. Only this file is owned by this worker.

## Confirmed findings

All findings below are established from the implementation and its callers at the audited SHA. The proposed regressions have not been added or executed. No finding depends on a live provider or on claiming that portable behavior proves a player/device result. There are no confirmed P0 or P1 findings in this scope.

### CE-01 — P2 — The failed-completion streaming adapter discards side-effect evidence and permits an unsafe context retry

**Confidence:** high. **Backlog status:** new; related to the existing broader typed-result preservation work at `TODO.md:3426`.

**Evidence:** `Assets/CoreAI/Runtime/Core/Features/Orchestration/ILlmClient.cs:955-965` maps a failed `LlmCompletionResult` to a terminal chunk containing only error/status/model fields. It omits `Content`, `ExecutedToolCalls`, and all usage fields. The successful branch carries those fields at `:969-981`; `LlmCompletionResult.ExecutedToolCalls` is a turn-level contract at `:700-705`, including calls made before a later failure. `CoreAISettingsOptions.cs:43` enables streaming by default, so `AiOrchestrator.CompleteForTaskAsync` consumes this adapter even for an ordinary task (`AiOrchestrator.cs:1277-1284,1300-1304,1390-1393`). The whole-turn context-overflow replay guard requires empty content and no executed calls (`:393-401`).

**Trigger and impact:** plug in a valid buffered `ILlmClient` that implements `CompleteAsync` and inherits the default streaming implementation. Its turn executes a mutating tool, then returns `Ok=false`, `ContextLengthExceeded`, and a nonempty executed-call list after a follow-up request overflows. The adapter drops the list. The orchestrator receives a failure that appears to have performed no work and can replay the entire turn, repeating the mutation. Partial text and usage are also lost on other failure codes. Clients with their own correct streaming override are not affected by this adapter defect.

**Fix direction:** use one result-to-chunk projection that preserves visible partial content, executed calls, model, and usage on both success and failure; keep hidden reasoning separate. Preserve the retry guard against replay after either visible output or side effects.

**Regression:** a buffered-only fake increments a mutation counter and returns a context-overflow failure with one successful tool trace. Run through the real orchestrator with streaming enabled and overflow retries enabled; assert one provider invocation, one mutation, failed typed outcome, and retained tool/usage metadata. Add a second case with partial text and no tools.

### CE-02 — P2 — A successful terminal chunk escapes before validation and turn publication finish

**Confidence:** high. **Backlog status:** new. **Cross-module owner:** Unity API consumers should be reviewed with the core fix.

**Evidence:** `AiOrchestrator.cs:944-968` forwards the backend's ordinary successful `IsDone` chunk. It then releases the backend iterator (`:971-988`), flushes any held text (`:1044-1056`), validates structured content (`:1071-1079`), and persists/publishes (`:1097-1099`, `:2175-2219`). Only the special empty tool-only terminal is deferred (`:956-965,1142-1145`). The public chunk contract says `IsDone` means the stream has finished (`ILlmClient.cs:776-777`). Actual host consumers stop there: `Assets/CoreAiUnity/Runtime/Source/Api/CoreAi.cs:444-446,601-603`. The main chat panel explicitly continues to EOF (`CoreAiChatPanel.cs:2331-2336`), so this is not a claim that every panel path loses post-work.

**Trigger and impact:** directly enumerate a normal `AiOrchestrator` stream and break on its successful terminal chunk, as the public contract permits. Iterator disposal skips the code after the yielded terminal: the user-turn teardown still runs, but the assistant record, command publication, and final trace do not. For an invalid structured response, a consumer that does drain to EOF instead receives a successful terminal followed by a second terminal carrying `InvalidRequest`. Finality therefore has two conflicting meanings and a consumer can accept a turn before its real outcome is known. Through the queue, early consumer abandonment additionally races producer post-work; the direct-core case is deterministic without that race.

**Fix direction:** retain the provider terminal, finish iterator release, flush visible text, validate, persist, and publish, then emit exactly one orchestration terminal. Do not move essential completion work after the public terminal. Preserve text carried by the provider's terminal when doing so.

**Regression:** use a fake that returns visible text and a successful terminal, enumerate only through the terminal, dispose, and assert the full user/assistant history plus one publication and one completed trace. With a rejecting policy, assert exactly one terminal and that it is `InvalidRequest`. Repeat through `QueuedAiOrchestrator` and the facade consumer.

### CE-03 — P2 — Replacing a role cannot clear its previous prompt or restore the universal prefix

**Confidence:** high. **Backlog status:** new.

**Evidence:** `AgentMemoryPolicy.ApplyPreparedRole` only adds the prefix opt-out when it is true (`Assets/CoreAI/Runtime/Core/Features/AgentMemory/AgentMemoryPolicy.cs:107-110`) and only writes a nonempty additional prompt (`:121-124`). There are no clearing branches. `AgentBuilder.WithOverrideUniversalPrefix(false)` stores an explicit false (`AgentBuilder.cs:505-508`), and prepared publication carries both fields (`:965-967`). Both synchronous and asynchronous configuration apply use this publisher (`:814,855-862`). The public policy setters already express the missing removal behavior (`AgentMemoryPolicy.cs:953-971,995-1013`). `AiPromptComposer` reads the surviving opt-out and prompt (`AiPromptComposer.cs:45-52,68-74`).

**Trigger and impact:** apply a role with a custom prompt and `WithOverrideUniversalPrefix(true)`. Replace that role with `WithSystemPrompt("")` and `WithOverrideUniversalPrefix(false)`, using either apply API. Replacement succeeds and returns a config describing the new values, while actual requests still use the old instructions and continue omitting the global prefix. Restoring shared policy instructions through the supported configuration seam silently fails.

**Fix direction:** make successful prepared publication assign every owned field, including removal on false/empty. Keep the existing failure/cancellation atomicity. If omission is intended to preserve a field, represent that separately from explicit false/empty.

**Regression:** sync and async parameterized replacement tests must assert no additional prompt and no prefix override after the second successful apply, then inspect the composed request. Existing `AgentBuilderEditModeTests.cs:555-595,599-649` cover preservation on failed/canceled replacement, not clearing on success.

### CE-04 — P2 — Role-wide cancellation misses every request without a logical cancellation scope

**Confidence:** high. **Backlog status:** a new gap in the cancellation capability; related to known item A3 at `TODO.md:1218-1222`.

**Evidence:** the public `IRoleAiTaskCancellation` contract promises to cancel every queued or running turn for a role (`IAiOrchestrationService.cs:98-102`). `QueuedAiOrchestrator.CancelRoleTasks` obtains candidates exclusively from `_scopeTokens` (`QueuedAiOrchestrator.cs:693-713`). Requests with null/empty/whitespace `CancellationScope` produce no scope key (`:1103-1115`) and enter no `_scopeTokens` entry (`:919-938,963-971`; streaming `:1020-1039,1064-1072`). The engine-free one-line task helpers create just such requests (`AiOrchestrationServiceExtensions.cs:102-110`).

**Trigger and impact:** start a role task through the prompt-only helper or a normal `AiTaskRequest` that leaves the optional cancellation scope unset; queue another turn of the same role. Call `CancelRoleTasks(roleId)`. Neither active nor pending work is selected, so provider generation and queued work continue despite role stop. This also applies when the requests carry trusted actor contexts: scope-key construction returns early before inspecting the actor.

**Fix direction:** track every admitted work item's role and independent cancellation source, including unscoped items, and select role-wide cancellation from that complete set. Preserve latest-wins logical-scope behavior as a separate concern.

**Regression:** extend `QueuedAiOrchestratorEditModeTests.CancelRoleTasks_CancelsAllRunningAndQueuedRoleTurnsAcrossScopes` to cross task/stream, actor/no-actor, and empty/nonempty scope. The current request helper always fills the scope (`QueuedAiOrchestratorEditModeTests.cs:1319-1328`), leaving this gap untested. Verify a different role survives and each never-started user turn is recorded once.

### CE-05 — P2 — Cancellation callback failures can abort queue teardown before pending work is settled

**Confidence:** high for the control-flow defect; whether a pending caller stays blocked depends on the active client's subsequent completion. **Backlog status:** new; distinct from the known pending-task cancellation-vs-disposal exception race at `TODO.md:3235-3237`.

**Evidence:** `QueuedAiOrchestrator.Dispose` marks the object disposed, then calls `_lifetimeCts.Cancel()` before taking/draining pending work (`QueuedAiOrchestrator.cs:1513-1544`). Cancellation invokes registered callbacks synchronously and can throw their aggregate failure. There is no protected cleanup boundary around this call; a later `Dispose()` returns immediately because `_disposed` is already true. `SafeCancel` only suppresses `ObjectDisposedException` (`:1418-1431`), so the same failure can interrupt scope cancellation/replacement before pending items are completed and pumping resumes (`:810-817,983-987,1096-1100`).

**Trigger and impact:** a replaceable inner service/tool registers a throwing callback on its operation token while another request waits. Dispose the queue or replace the scoped request. The callback exception exits the queue's control path before its promised teardown runs. Pending callers then rely on the active client eventually returning and freeing a slot; a slow or stuck client can leave them unresolved, and a second disposal cannot repair the skipped drain. This is an exception-isolation failure, not proof that cancellation can forcibly terminate an uncooperative client.

**Fix direction:** ensure pending settlement, scope release, and pump/teardown bookkeeping run in `finally` regardless of callback faults. Aggregate or log callback failures after completing mandatory cleanup. Do not silently reinterpret a callback fault as successful cancellation.

**Regression:** an inner fake exposes a gate and registers a throwing cancellation callback. With task and stream work pending, verify that disposal still settles all pending work within a bounded wait and leaves no pending registration; repeat disposal. Separately verify scope replacement starts/settles its new work even when the old callback faults.

### CE-06 — P2 — Active streams have unbounded producer-to-consumer buffering

**Confidence:** high. **Backlog status:** new; `TODO.md:2990-2991` mentions a streaming-buffer performance suite but does not track this missing bound.

**Evidence:** each public queue stream starts an independent producer (`QueuedAiOrchestrator.cs:190-207,227,324`). The producer reads the entire inner stream and performs a synchronous enqueue for each chunk (`:535-540`). `AsyncChunkQueue` wraps an unbounded `ConcurrentQueue<LlmStreamChunk>` and `Write` never waits or checks occupancy (`:1593-1606`). Queue options expose only request concurrency and pending-request count (`AiOrchestrationQueueOptions.cs:9-28`). There is no active-stream byte/chunk limit. Consumer abandonment is handled (`QueuedAiOrchestrator.cs:239-259`), but an enumerator that remains alive while reading slowly does not trigger that path. `MaxResponseChars` defaults to zero (`ICoreAISettings.cs:229-233`, `CoreAISettingsOptions.cs:67`); an optional text cap also cannot bound the number of progress/reasoning-only chunks retained by this queue.

**Trigger and impact:** a provider or custom inner service emits faster than a consumer processes chunks, including a consumer paused on UI or network work. Producer progress is independent of consumer demand, so one active request can retain arbitrarily many chunks despite `MaxPending` and `MaxConcurrent`. A large finite response causes a memory spike; an indefinitely producing stream can exhaust process or WebGL memory.

**Fix direction:** add a configurable bounded async stream buffer with awaited writes and cancellation-aware admission, or an explicitly documented bounded coalescing/failure policy. Retain terminal/error/usage ordering and use a WebGL-compatible async wait without threads or blocking.

**Regression:** a gated consumer and bounded fake producer should prove that upstream production pauses at the configured byte/chunk budget, resumes in order when consumed, preserves terminal metadata, and releases promptly on cancellation/disposal. This can be tested with small deterministic counts; no large benchmark is needed.

### CE-07 — P2 — Built-in structured validators accept malformed JSON and reject valid numeric JSON

**Confidence:** high. **Backlog status:** new; the provider-enforced schema roadmap at `TODO.md:3187-3192` does not cover incorrect local validation.

**Evidence:** `CreatorResponsePolicy.cs:23-50` accepts anything bounded by braces, including `{not json}`, without parsing. `CoreMechanicResponsePolicy.cs:25-51` combines brace detection with a positive-digit regex and does not parse JSON either. For example, `{"gravity": -9.81}` has a legitimate numeric field but fails its `"key":digits` test at `:41-47`; malformed `{"damage": 1 garbage}` passes. `AnalyzerResponsePolicy.cs:23-63` likewise checks brace boundaries and text fragments rather than parsed property keys. Successful validation reaches history and `ApplyAiGameCommand.JsonPayload` (`AiOrchestrator.cs:459-507,2175-2216`).

**Trigger and impact:** a model returns malformed brace-shaped content to Creator, or a mechanics object whose only numeric fields are negative. The first is treated as a successful structured turn and published without a repair attempt; the second spends an unnecessary retry and can finish as invalid even when it met the documented numeric requirement. Text inside JSON values can also satisfy the analyzer's key-name heuristic. This concerns structural validation, not inventing a game-specific command schema in the framework.

**Fix direction:** normalize any explicitly supported fence/prose wrapper, parse exactly one JSON object with the existing JSON dependency, and inspect actual property/value token types. Accept all valid numeric representations, including negative values. Leave game-specific schema requirements behind the injected policy seam.

**Regression:** malformed braces/trailing garbage, escaped quotes, braces inside strings, negative/zero/exponent numeric values, and metric words in values without metric properties. Keep existing valid/fenced cases. The current `RoleStructuredResponsePolicyEditModeTests.cs:63-83,91-112,120-148` only checks well-formed positive cases and obvious prose/missing-field refusals.

### CE-08 — P3 — Whitespace around a wildcard changes route precedence

**Confidence:** high. **Backlog status:** new.

**Evidence:** `LlmRouteResolver` orders equal-sort rules by `Specificity(rule.RolePattern)` (`LlmRouteResolver.cs:20-25`), whose classification does not trim (`:72-79`). Actual matching does trim (`:50-66`). `LlmRouteTable.Validate` accepts any non-whitespace pattern (`LlmRouteTable.cs:44-47`).

**Trigger and impact:** put `" * "` before `"Teacher"` with equal sort order, as in the existing wildcard-first precedence fixture but with surrounding whitespace. Both rules are classified as exact for sorting; stable source order leaves the wildcard first. Matching trims it to `*`, and Teacher resolves to the fallback endpoint despite the promised exact-role precedence. Configuration whitespace therefore changes the selected model/profile instead of being normalized consistently.

**Fix direction:** normalize a pattern once before both ordering and matching, or trim inside `Specificity`. Preserve documented sort-order priority.

**Regression:** extend `LlmExecutionModesEditModeTests.RouteResolver_ChoosesExactRoleBeforeWildcard` (`:109-130`) with whitespace-wrapped wildcard and prefix patterns. That fixture is not linked into the portable test project; move/add the pure resolver cases into a linked engine-free fixture.

## Known issues and cross-module boundaries

- **Known A3 remains partly valid:** actor-aware queue entries replace the logical `CancellationScope` with `ActorContext.SessionId` (`QueuedAiOrchestrator.cs:1188-1190`). `CancelTasks("lesson-panel")` cannot find a real actor-aware request that used that logical scope. The passing custom-scope test uses a fake inner without actor resolution (`QueuedAiOrchestratorEditModeTests.cs:1333-1369`). The new role-wide capability does repair the facade's scoped role stop (`CoreAi.cs:835-839`), so the old claim that every default role stop cancels nothing is too broad for this SHA. CE-04 records its remaining unscoped gap separately.
- **Known A7, mode enforcement:** mode reaches only streaming-default selection in prepared construction (`AgentBuilder.cs:876-881`); it does not strip tools for `ChatOnly` or turn history off for `ToolsOnly` (`:923-968`). Tracked at `TODO.md:1238-1243`; do not duplicate as a new finding.
- **Known global-composition limitations:** `AgentConfig.AskAsync(config, suppliedOrchestrator, ...)` still resolves policy and actor identity from `CoreAIAgent` (`AgentConfigExtensions.cs:48-73,89-105`). An explicit orchestrator alone does not make that convenience API independent of process-global state. Related to C6 (`TODO.md:1332-1335`) and the architecture's existing static-facade debt. Plain request entry points remain usable without this helper.
- **Known synchronous persistence and storage races:** request construction and teardown still call synchronous memory APIs (`AiOrchestrator.cs:135-143,1559,1981-1982,2180-2200`). `TODO.md:3424-3425` already assigns the async end-to-end contour to root/memory/skills owners; this report does not claim that changing only one adapter removes blocking I/O. Persistence implementation and summary concurrency belong to audit 02.
- **Known successful-result mutation:** `result.Content`, stream text, and boundary flags are still mutated in place (`AiOrchestrator.cs:387,508,839,866,946-954`), matching `TODO.md:1192-1195`. Failure reclassification uses copies; do not report it as entirely missing.
- Provider HTTP/SSE error handling, retries/fallback implementations, and endpoint registry lifecycle belong to audit 03. CE-01 is the shared default-interface projection and its orchestration effect, not a provider transport finding. Actual side-effect authorization belongs in the tools/Mods/host boundaries; a role's offered-tool list is not by itself a security boundary.

## Hypotheses and unexecuted runtime gates

These are not counted as confirmed findings:

- A caller cancellation between `Enqueue`'s early token check and insertion can run `CancelPending` before the item is present (`QueuedAiOrchestrator.cs:897-915,1203-1220`). A contended-queue test with a controlled interleaving is needed to establish delayed cancellation and its duration.
- Prepared role publication uses one writer lock, but turn construction obtains prompt, tools, and config through separate reads. A concurrent replacement can plausibly mix generations in one request; no controlled proof or supported hot-replacement timing contract was executed in this audit.
- A direct abandoned stream with partial text can reach the metrics `Succeeded` branch in iterator teardown because it tests content and reported error, not EOF (`AiOrchestrator.cs:990-1011`). Existing abandonment coverage asserts user-history cleanup only (`AiOrchestratorHistoryEditModeTests.cs:1082-1106`). A targeted metric assertion should establish the intended distinction between a completed provider attempt and a closed orchestration stream before assigning severity.
- WebGL's reusable `IValueTaskSource` signaling, cancellation teardown, async configuration hydration, and a future bounded buffer require a real built-player responsiveness check. Portable NUnit success does not establish browser scheduling, IL2CPP preservation, frame time, or device memory limits.
- Caller timeouts and noncooperative-client cleanup require host integration checks. No source audit can prove forced termination of an inner operation that ignores cancellation.

## Coverage and exclusions

The real goal was reliable reusable execution, not a fixed issue count. Implied requirements considered: cancellation attribution and ordering, safe retries after side effects, single finality, queue and stream memory bounds, replacement semantics, identity scope, failure isolation, replaceable host seams, and truthful diagnostics. The most likely audit error was treating a fake-inner test as proof of production actor behavior; A3 and CE-04 were traced through actual actor admission and key construction.

Substantive source coverage:

- `Features/Orchestration/QueuedAiOrchestrator.cs`: entire queue, admission/pump, fairness bookkeeping, task/stream execution, logical/role cancellation, unstarted persistence, token release, disposal, and reusable chunk signaling.
- `Features/Orchestration/AiOrchestrator.cs`: construction and actor resolution; full request construction; task and streaming execution; retry guards; structured retry; history construction/commit; user-turn teardown/resend detection; prompt hash; trace projection; tool filtering; sanitization/publication; per-call overrides. Tail clipping/formatting and overflow parsing utilities received targeted rather than exhaustive branch review.
- `AiTaskRequest`, `IAiOrchestrationService`/typed/cancellation capabilities, `AiOrchestrationServiceExtensions`, `ILlmClient` result/chunk/default-streaming contracts, `AiOrchestrationQueueOptions`, `StreamedMessageJoiner`.
- `AgentBuilder`/`AgentConfig`: build/apply/readiness-related entry points and prepared-role construction; `AgentConfigExtensions` including registration ownership/wait cancellation; `AgentMemoryPolicy` publication, prompt/streaming getters/setters, and tool/config access. Skill hydration/storage internals are excluded primary ownership.
- `AiPromptComposer`: entire composition/template/runtime-context/mutation-context path; Creator/CoreMechanic/Analyzer/Programmer/composite structured policies and their contracts.
- Authority/actor issuance and grant contracts; network execution policy/authority; in-game chat service/factory; selected metric/trace retention and projection paths; engine-free assembly and settings declarations. Metric collector internals were not exhaustively branch-audited.
- Existing `AgentBuilderEditModeTests`, `QueuedAiOrchestratorEditModeTests`, `AiOrchestratorHistoryEditModeTests`, `AiOrchestratorRefactorEditModeTests`, `AiOrchestratorStreamingEditModeTests`, `RoleStructuredResponsePolicyEditModeTests`, `OrchestrationResilienceEditModeTests`: targeted tests/fakes around reported behaviors, not every assertion in every fixture.
- Selected read-only host call sites: `Api/CoreAi.cs`, `Features/Chat/CoreAiChatService.cs`, `CoreAiChatPanel.cs`; source reads only establish consumer behavior.

Documentation/configuration read: `AGENTS.md`; `Docs/ARCHITECTURE_RULES.md`; CoreAI README and agent/routing/orchestration guides; portable README and test project; relevant TODO sections; source/test inventories. The portable project explicitly links the seven proposed execution fixtures; the pure route tests currently live in a Unity fixture that is not linked.

Excluded primary ownership: memory/RAG/storage/skills/tool registry; provider HTTP/SSE and transport; Unity composition/UI; Mods; Hub; MCP; Mirror; benchmarks; demos and repository tooling. Cross-module observations name the responsible area.

## Verification ledger

Executed: read-only source and documentation inspection using PowerShell, `rg --files`, `rg -n`, numbered `Get-Content` slices, and `git status --short`; `git rev-parse HEAD origin/main` returned the baseline above. Initial status contained one unrelated untracked ETFX `.meta`; it is untouched. Some initial inventory searches used nonexistent directory names or literal wildcard path arguments on Windows and returned path errors; these were replaced by discovered real paths and `rg -g` filters. They are not verification evidence.

Report checks passed: `git diff --check -- dev-docs/audits/2026-10-23/01-core-execution.md` returned exit 0 with no output. Because this new report is untracked, it was additionally checked directly: no trailing whitespace or Cyrillic prose, and 48 file-qualified citation ranges resolved to existing source lines. Shorthand continuation references were checked during full readback. Final `HEAD` and `origin/main` remain the baseline SHA; concurrent root changes to PLAN/documentation and other auditors' reports were left untouched.

Not run: build, test runner, Editor, PlayMode, WebGL/player/device, provider/network requests, load/fault benchmarks. Shared build outputs belong to the root auditor.

Suggested bounded regression command for the root-owned portable build/test gate:

```powershell
dotnet test tools/portable/Tests/CoreAI.Portable.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~QueuedAiOrchestratorEditModeTests|FullyQualifiedName~AgentBuilderEditModeTests|FullyQualifiedName~AiOrchestratorHistoryEditModeTests|FullyQualifiedName~AiOrchestratorRefactorEditModeTests|FullyQualifiedName~AiOrchestratorStreamingEditModeTests|FullyQualifiedName~RoleStructuredResponsePolicyEditModeTests|FullyQualifiedName~OrchestrationResilienceEditModeTests"
```

Expected: the current fixtures complete with zero failures; that baseline does not establish the missing regressions described above. Root is running the portable suite separately. This worker has no result from that run to claim.

## Final acceptance review

The review asked: what was missed by the happy-path tests? Checked failures after executed tools, consumer termination at `IsDone`, successful replacement with cleared values, absent optional cancellation scopes, exceptions during cancellation callbacks, and a slow-but-live stream consumer. Existing tests cover many neighboring positive cases and cleanup races; they do not establish the missing cases above.

The complete report was read back and its citations checked against the source; the helper-construction citation was corrected to its actual lines. No source, tests, TODO, PLAN, package metadata, Git index, or commits were changed. Actual fixes and their new regressions remain outside this audit-only assignment.
