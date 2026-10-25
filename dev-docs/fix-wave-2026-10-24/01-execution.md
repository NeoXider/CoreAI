# Execution repair contract and receipt

Date: 2026-10-24. Owner: execution worker, `gpt-6.1-sol`, reasoning effort `max`.
Baseline: `69d5bcc37502cd4bb464a3bf9ac0c53b1624b804`, branch `fix/coreai-7.54.0`.
Status: source revalidation, immutable red receipt and implementation checkpoint delivered. Green verification is pending and lead-owned.

## Goal and constraints

Repair reachable execution contracts while preserving replaceable policies, existing public entry points, actor authority and host-owned identity. Implied requirements are exactly one truthful terminal outcome, essential completion work before finality, bounded active-stream buffering, cancellation of every admitted role request, callback failure isolation, immutable admission-time memory partitions, and consistent route precedence.

Exclusive scope: orchestration runtime except `ILlmClient.cs`; `ActorContext.cs`; route resolver/table; assigned execution test fixtures; this work receipt. Change nothing else. No Git mutation, shared build/test run, Unity/MCP, asset/meta, version or global planning edits. The lead reviews diffs and runs all gates. Pure runtime code stays C# 9, explicit types, English and WebGL-safe async; no threads or blocking waits are introduced.

## Per-finding dispositions and acceptance

| ID | Current verdict and contract | Required regression |
| --- | --- | --- |
| CE-01 | Alias of provider-owned LLM-002; not repaired here. Failed-result projection must retain side effects and usage. | Provider owner verifies failed buffered-only adapter plus overflow replay guard. |
| CE-02 | Confirmed: ordinary provider success terminal currently precedes structured validation and publication. Public `IsDone` must follow provider disposal, text flushing, validation, user/assistant persistence, command publication and final trace; emit exactly one terminal. | Stop at `IsDone` for direct and queued real orchestrators; full history/publication/trace already exist. Invalid structured response yields one failing terminal, no success publication, and terminal text/usage survive. |
| CE-03 | Confirmed source observation; delegated to memory owner. | Replacement false/empty clears previous prompt/prefix policy without weakening failure atomicity. |
| CE-04 | Confirmed: role cancellation selects only optional logical scopes. Every admitted task/stream needs independent cancellation tracking, scoped or not. Preserve other roles and existing latest-wins semantics. | Task/stream, actor/no-actor, empty/nonempty scope; running and pending target-role work stops, other role completes, unstarted record exactly once. |
| CE-05 | Confirmed: synchronous callback faults can skip pending settlement and prevent retrying disposal. Mandatory queue cleanup and pumping must survive cancellation callback failures; diagnostics retain callback fault. | Throwing callback plus pending task/stream: disposal settles both, second disposal is safe. Scope replacement still pumps new work; role cancellation continues across all targets. |
| CE-06 | Confirmed: producer buffer is unbounded. Add configurable chunk and retained-byte budgets with awaited cancellable writes; preserve terminal/effect metadata and ordering. | Small paused-consumer producer blocks at count/byte budget, resumes in order, retains terminal metadata, wakes on cancel/abandon/dispose. Oversized chunk has explicit bounded failure rather than silent dropping. |
| CE-07 | Confirmed: brace/regex/substring checks accept malformed data and reject negative numeric values. Parse exactly one JSON object after supported wrapper normalization; inspect token types and property keys. No game schema added. | Malformed/trailing JSON, escapes/braces in strings, negative/zero/exponent values and metric words only in values; retain valid/fenced/prose wrapper compatibility. |
| CE-08 | Confirmed: matching trims patterns while specificity does not. Normalize consistently while preserving sort-order priority. | Whitespace-wrapped exact/prefix/global patterns choose exact before prefix before fallback. Add pure resolver cases to an already portable-linked fixture. |
| MEM-001 | Confirmed actor-aware path. Effective memory scope must be captured before first await at queue and direct admission and preserved inside the real inner orchestrator, including bound Empty. | Mutable host scope changes while a real actor-resolving request waits/suspends; admitted turn stays in its captured partition for prompt reads/history writes/unstarted recording. Narrowing grants preserves binding and authority. |
| CARRY-EXEC-A3 | Lead admitted for repair: actor-aware admission currently replaces the caller's logical cancellation scope with its session id. Independent logical views of one actor/role must remain independent. | Same actor/session/role, two logical scopes: neither supersedes the other, stopping one logical scope leaves the other alive. Existing session-id cancellation remains supported. |
| ACTOR-CANCEL | Lead-approved optional public `IActorAiTaskCancellation.CancelActorTasks(ActorContext actorContext, string roleId = null)`. Assert trust, match durable actor id plus connection session id, optionally role, for every scoped/unscoped task/stream. | Concurrent two-actor same-role work: cancelling actor A's connection settles its pending/running work while B survives. Default/untrusted context is refused. No identity issuance or account policy. |

## Shared memory contract

Execution owner adds internal `ActorContext.HasBoundMemoryScope` and `BindMemoryScope(AgentMemoryScope)`; binding preserves trusted identity, session, role, world and grants. `NarrowGrants` preserves the binding marker. No public authority-forging setter.

Memory owner adds internal `AgentMemoryScopeKey.CaptureActorScope(ActorContext actor, IAgentMemoryScopeProvider provider, string roleId)` and frozen-empty frame/resolver support. Already-bound actors are unchanged; otherwise choose a nonempty actor scope or query the provider exactly once, even if Empty. Queue and direct admission invoke this helper and pass the bound actor to work and turn execution. Optional constructor injection for the direct orchestrator will preserve existing call sites.

`ResolveActorContext` remains identity-only. Queue admission binds with its explicitly supplied provider; direct task/stream admission binds with its own injected provider. This prevents a queue's real inner resolver from freezing an empty default before the queue can use the host's actual provider. An already-bound context wins at every later boundary.

## Approved buffering and cancellation behavior

The lead approved a default queued backlog budget of 64 chunks and 1 MiB, with positive configuration validation. Byte accounting charges retained UTF-16 strings plus fixed chunk/tool-record overhead. Both budgets apply to queued chunks. They do not promise a total process heap cap, provider response cap, or bound on a single payload already allocated by a replacement upstream.

An upstream chunk larger than the byte budget waits until the ordinary backlog drains, then uses a single direct reader rendezvous. The producer cannot advance until that item is handed off, and at most one upstream item can wait outside the queued backlog. Cancellation/disposal releases the producer while preserving any unhanded chunk's terminal/tool/usage evidence in the final outcome. This exception is explicit and lossless; it must not become a misleading empty failure.

Actor-aware latest-wins keys combine length-prefixed actor id, connection session id, role and logical scope. Empty logical scopes admit independently. `CancelTasks(scope[,role])` is an explicit global/admin logical-scope operation for actor-aware requests and preserves the existing session-id alias. Legacy actorless callers retain their current role/provider-partition selection. Player/session UI uses `CancelActorTasks` to stop its own connection and optional role; it must not call a global role/admin operation to stop one player. These capabilities consume trusted host context and do not issue it or infer login/logout.

## Verification plan

The lead runs the focused existing portable-linked fixtures before and after the repairs:

```powershell
dotnet test tools/portable/Tests/CoreAI.Portable.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~QueuedAiOrchestratorEditModeTests|FullyQualifiedName~AiOrchestratorStreamingEditModeTests|FullyQualifiedName~AiOrchestratorHistoryEditModeTests|FullyQualifiedName~RoleStructuredResponsePolicyEditModeTests|FullyQualifiedName~OrchestrationResilienceEditModeTests" --logger "trx;LogFileName=execution-regressions.trx"
```

Expected: deterministic new cases fail on the audited source, then pass with zero failures after the final checkpoint. Count actual TRX cases and require positive fixture presence. Worker runs only scoped source/diff/static checks; results will be recorded below. Built-player, WebGL responsiveness/IL2CPP and live facade integration remain separate lead-owned gates.

## Milestones and actual receipts

- Intake: read actual source, audit 01/MEM-001, architecture and fixture linkage. Actor-aware queue currently captures an empty actor scope and the inner service rereads provider state; direct orchestration currently lacks a scope-provider constructor seam.
- No build/test/runtime result is claimed at this checkpoint.
- Red checkpoint: new cases in the existing portable-linked queue, streaming and structured-policy fixtures; execution runtime remains at audited behavior except the source-coherence ActorContext binding seam. The lead owns the actual failing receipt.
- Additional unlinked `ExecutionAdmissionRegressionTests.cs` covers the new actor cancellation and buffer seams plus real actor-resolving queued/direct memory snapshots. The serialized portable project-file owner must link this fixture after runtime implementation; its current absence is not a passing test result.

### Immutable red evidence

The lead saved `artifacts/fix-wave-20261024/execution-red-baseline`, containing the audited HEAD runtime and exact new regression fixtures. `snapshot.json` records baseline and fixture SHA256 values. The worker read the actual TRX and console after the lead ran it: **38 UnitTestResult rows: 26 Failed, 12 Passed, 0 skipped; process exit 1; duration 20 seconds**. Receipt: `artifacts/fix-wave-20261024/execution-red-results/execution-red.trx`; console: `artifacts/fix-wave-20261024/execution-red-baseline-console.log`.

Failures include unscoped role stop, throwing-callback replacement, direct structured finality, exact-route whitespace precedence, negative/exponent mechanics values and malformed JSON. The three current fixture hashes still match the immutable snapshot at handoff. These are genuine baseline failures; no green result is inferred from them.

### Delivered source behavior

- `ActorContext` stores a private binding marker. Internal binding preserves trust/identity/session/role/world/grants, and `NarrowGrants` preserves the marker.
- Queue and direct admission bind effective scope before awaiting. Both reuse the existing `AiTaskRequest.Clone()` after binding so later caller mutation cannot replace the admitted context. Direct streaming pushes the bound actor around each inner `MoveNextAsync` and `DisposeAsync`; an AsyncLocal lease spanning `yield return` alone cannot preserve identity when a reader resumes under another context.
- Public streaming retains internal terminals, delivers their text once as visible deltas, drains provider/inner cleanup, completes validation/history/publication/trace, then exposes one public terminal. The core emits its aggregate final outcome, including failure/usage/tool/model evidence. Structured rejection now records a failed trace.
- Every accepted task/stream owns a cancellation source and belongs to a complete tracked set independent of logical scope. Role stop and trusted actor/session stop select these sets. Logical scope keys include actor/session/role/scope; actor and legacy key encodings have distinct prefixes. Scope replacement, cancellation and disposal settle detached pending callers before invoking replaceable cancellation callbacks. Callback aggregate failures are logged, and disposal/admission share the queue lock.
- Stream producers await count/byte capacity. The normal reader retains its reusable ValueTask signal; writer promises allocate only while parked. Oversized upstream chunks use the approved one-item rendezvous. Cancellation retains an unhanded chunk's text/tool/usage in a failing terminal and releases the producer; it does not erase side-effect evidence.
- Creator/mechanics/analyzer validators parse one strict JSON object with the already referenced `System.Text.Json` dependency. Supported JSON fences and prose prefixes are normalized; trailing content, comments, trailing commas, unquoted/single-quoted keys and NaN are rejected. Mechanics checks numeric property token kinds recursively; Analyzer checks actual property names, including nested properties, rather than words in values.
- Route specificity trims patterns consistently with matching. `LlmRouteTable` required no change. The duplicate orchestration tool estimator delegates to the memory owner's shared callable-expanded estimator using the request-filtered tool list (MEM-012 integration).
- Root-requested MEM-006/MEM-011 integration: `BuildHistoryContext` owns a `using ConversationContextSnapshot snapshotLifetime = snapshot` after its null guard. Disposal runs after the awaited commit settles and after request projection/return evaluation, including error/cancellation. This depends on the memory owner's new `ConversationContextSnapshot : IDisposable` discard implementation; public projection fields must remain readable after disposal.

### Verification output and remaining work

Worker executed scoped `git diff --check` after the final edits: **exit 0, no diagnostics**. Changed source/test files use LF as required by `.gitattributes`, retaining their original BOM state. New fixture scan found no `var` locals or Cyrillic prose. The runtime changes and added test diffs were read back; no shared build/test, Git index/commit, Unity/MCP or asset/meta operation was performed by this worker.

The lead's first current green attempt stopped before tests on unrelated Rbx-owner `RbxUDim`/`RbxError` CS0103 compilation failures. This establishes neither a green result nor an execution failure. The lead will retry after that owner repairs compilation, link the new fixture and return exact execution failures if any. No execution test result on repaired source is claimed at this handoff.

Required filter after linking the new fixture:

```powershell
dotnet test tools/portable/Tests/CoreAI.Portable.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~ExecutionAdmissionRegressionTests|FullyQualifiedName~QueuedAiOrchestratorEditModeTests|FullyQualifiedName~AiOrchestratorStreamingEditModeTests|FullyQualifiedName~AiOrchestratorHistoryEditModeTests|FullyQualifiedName~AiOrchestratorRefactorEditModeTests|FullyQualifiedName~RoleStructuredResponsePolicyEditModeTests|FullyQualifiedName~OrchestrationResilienceEditModeTests" --logger "trx;LogFileName=execution-green.trx"
```

The new fixture covers queued/suspended actor-aware scope capture, caller request mutation, pending cancellation history, interleaved direct task/stream actors, frozen Empty, ambient restoration after completion/failure/cancellation, grant narrowing, two-actor unscoped cancellation, rejected untrusted contexts, positive limit validation, byte/chunk backpressure, ordering, terminal metadata, oversized handoff, cancellation/disposal and consumer abandonment. Lead verification must confirm positive case presence, not only a passing exit code. Built-player/WebGL/IL2CPP, facade integration and independent architecture/adversarial review remain separate gates.

### Source checkpoint (SHA256)

Paths below are relative to `D:/Git/CoreAI`; no other file was written by this worker.

| File | SHA256 |
| --- | --- |
| `Assets/CoreAI/Runtime/Core/Authority/ActorContext.cs` | `1E5EC93C0EC951C36AE52BBE08261C26D802F6E1E20DB0C1D7450C2DF2C6F2E6` |
| `Assets/CoreAI/Runtime/Core/Features/LlmRouting/LlmRouteResolver.cs` | `D5A146613CFDD24F73EF02A16E2723143674C63A101DA5A17BB3DC3560531B81` |
| `Assets/CoreAI/Runtime/Core/Features/Orchestration/AiOrchestrationQueueOptions.cs` | `72393AC12005AD11B77A4D4607BFC2E3D0B3D7503BAC76089D0E8BF9F3475A34` |
| `Assets/CoreAI/Runtime/Core/Features/Orchestration/AiOrchestrator.cs` | `EBA117CFDF0D62CCCDDFD989ADC06093DFE4D1A11E534F7D7831A6AA9C40F039` |
| `Assets/CoreAI/Runtime/Core/Features/Orchestration/AiTaskRequest.cs` | `D7ECCFC03507F9C376F739F92030704AF5BE666C0E945AB7B5271F60A8EFD2EF` |
| `Assets/CoreAI/Runtime/Core/Features/Orchestration/AnalyzerResponsePolicy.cs` | `588BEE31E019A5B162CF526484B2D7BA30C656FF07AD3407FCEAEFEBB21376D0` |
| `Assets/CoreAI/Runtime/Core/Features/Orchestration/CoreMechanicResponsePolicy.cs` | `A3A89F1D86AA1E0939AF49156582762ACA4202AA803AD4364B895EE67BC7A672` |
| `Assets/CoreAI/Runtime/Core/Features/Orchestration/CreatorResponsePolicy.cs` | `ACC44DB87D49DBCD3930BDCB041EA1136451EF54EB2E0A9D7A223BE4C729AD7F` |
| `Assets/CoreAI/Runtime/Core/Features/Orchestration/IAiOrchestrationService.cs` | `C421DAE990B9391108AF3258CDE6439842A83966DC4476ED8A9FB6A11EBB9538` |
| `Assets/CoreAI/Runtime/Core/Features/Orchestration/QueuedAiOrchestrator.cs` | `0AB2A58D101FD01B74956E6EAB232F28615CAA7497B14ECA28460FED4220AF2F` |
| `Assets/CoreAiUnity/Tests/EditMode/QueuedAiOrchestratorEditModeTests.cs` | `5BAB6CD92D1A7B28E3DE7E73A9CEF403EAEE094C727467FBED87545F6902657E` |
| `Assets/CoreAiUnity/Tests/EditMode/AiOrchestratorStreamingEditModeTests.cs` | `493A211CFBB3DDBF77B475826ED80CB8076316877A8605A7404B2DC16A2346E3` |
| `Assets/CoreAiUnity/Tests/EditMode/RoleStructuredResponsePolicyEditModeTests.cs` | `880EB4DABEE8674284C7375D444C2291D398179CFE8F65851B791067F6F80196` |
| `Assets/CoreAiUnity/Tests/EditMode/ExecutionAdmissionRegressionTests.cs` | `7B8D675349C4E9D3E56F04A30A3DBEC8B2149FBE84F0570CF3038C8E7D003FEE` |

This Markdown is the only additional worker-owned file. CE-01/LLM-002 and CE-03 remain their named owners' work; ActorMemoryScope helper/frame implementation belongs to the memory owner. The lead owns compilation, positive fixture linkage, final review, platform verification, release metadata and Git publication.
