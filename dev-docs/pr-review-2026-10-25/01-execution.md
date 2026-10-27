# Execution and orchestration PR audit

Status: complete static PR review. One source-confirmed compatibility regression is recorded; root adjudication and runtime proof are pending.

## Review contract

- Baseline: `69d5bcc37502cd4bb464a3bf9ac0c53b1624b804`.
- Candidate: `bc3fd74f0a889bfe1b1e7babc6bc02ed48a7c05e`.
- Workspace: `D:/Git/CoreAI`; review uses the immutable Git objects.
- Write scope: this report only. No source changes, commits, builds, tests, Unity, MCP, or provider calls.
- Runtime scope: changed `ActorContext.cs` and orchestration files, except provider-owned `ILlmClient.cs`; agent builder and memory policy remain memory-owner scope.
- Test scope: changed execution, streaming, queue, actor admission, and structured policy tests; relevant adjacent contracts and callers are read-only supporting evidence.

## Review questions and decisions

Before planning: the goal is an independent PR review of introduced execution regressions, not a repeat of the previous module audit. Applicable requirements are immutable admission, actor/connection isolation, cancellation, completion ordering, bounded lossless streaming, cleanup, reentry, exception isolation, WebGL compatibility, and public API compatibility. Host account/login/matchmaking policy is outside Core's ownership. Repository conventions were read in `AGENTS.md` and `Docs/ARCHITECTURE_RULES.md`; the explicit assigned report overrides the default prohibition on retained audit reports.

Before delegation: this is an exclusive leaf audit and further delegation is prohibited. The exact scope, invariants, acceptance evidence, and write target are supplied by the root. No implementation decisions are delegated.

Before accepting: each finding must identify a changed line, a concrete trigger, expected versus actual behavior, a source-based causal path, and a minimal regression plan. Static source inspection is the only available verification here; no runtime acceptance is claimed.

## Confirmed findings

### PR-EXEC-001 [P1] Preserve scoped memory when the new constructor argument is omitted

**Target:** `Assets/CoreAI/Runtime/Core/Features/Orchestration/AiOrchestrator.cs:332-335` (also the new fallback at line 95 and the streaming equivalent at lines 593-595).

**Trigger:** an existing direct embedder constructs `AiOrchestrator` with a `ScopedAgentMemoryStoreDecorator(backing, hostProvider)`, the default local identity provider, and the previously supported constructor arguments. The host provider returns a nonempty tenant/user/session/topic scope. The embedder omits the newly added optional `scopeProvider` argument because that argument did not exist at the baseline. This is independent of login policy or in-flight account switching; even one stable nonempty scope is affected.

**Expected versus actual:** the turn must read the scoped history that the same decorator exposes outside the turn, and append its user/assistant records there. At the baseline it does. The candidate instead reads and writes the bare role partition. An out-of-turn hydration still reads the nonempty provider partition, so the turn appears to disappear; separate provider-selected users can also consume the shared bare-role history during their turns.

**Causal path:** the optional constructor argument defaults to a new `DefaultAgentMemoryScopeProvider` at line 95. The added capture at line 332 chooses that provider's `Empty` value for the default local actor and marks it bound; line 335 pushes this bound-empty context around every memory operation. `AgentMemoryScopeKey.Resolve` now honors a bound-empty context and deliberately skips the scoped decorator's own provider fallback (`AgentMemoryScope.cs:69-92`). At the baseline direct orchestration did not push a frame here, and the decorator resolved its own host provider. `CorePortableInstaller.cs:112-129` explicitly supplies the provider, so the standard container path avoids this regression; source-compatible manual construction remains affected. The repair receipt explicitly says optional injection preserves existing call sites, but the runtime semantics change.

**Minimal regression plan:** build the real direct core with a scoped in-memory store and a stable nonempty provider, while omitting only the new optional constructor argument. Seed scoped history; run task and stream variants with a deterministic fake provider; assert the seeded content reaches the provider and that out-of-turn scoped history contains both new records. Then use a second nonempty scope with a fresh request and assert no first-scope turn appears in its prompt. Assert the unscoped backing role key remains empty. This test should pass at the baseline and fail at the candidate.

**Fix direction:** the store and admission boundary need one effective scope-resolution seam so existing scoped manual composition cannot silently acquire an unrelated default provider. Preserve explicit bound-empty behavior and avoid restoring mutable per-operation fallback inside an admitted turn.

**Known uncertainty:** source inspection only; the worker was prohibited from running a proof. The normal installer was checked and is not claimed broken. Hosts already passing the same provider at every admission boundary, or actors carrying an explicit nonempty scope, are not affected by this exact trigger.

## Investigated, not counted

- Both core and queue stream methods remain deferred async iterators, so their new local snapshots occur on first enumeration. The orchestration interface promises deltas but does not promise call-time admission; the public chat service separately freezes its request eagerly and has a dedicated pre-enumeration regression test. Deferred enumeration alone is not counted as a PR bug without a stronger direct-core contract.
- Retargeting a bound actor to another role keeps its already captured durable scope. The binding API promises reuse of that scope; role-specific fallback expectations are an ambiguity rather than a confirmed regression. The memory owner/root owns any further adjudication.
- The queue forwards an inner service's own terminal before advancing/disposal of that inner iterator, and suppresses a later exception once `terminalWritten` is true. The concrete `AiOrchestrator` now finishes its provider disposal and publication before producing that terminal; no new conforming production caller was established that makes the remaining generic-wrapper concern an introduced bug. A replaceable-inner test could document this boundary, but it is not counted here.

## Repair checks and test gaps

- Core finality: the complete stream body, terminal aggregation, provider disposal, validation, user-turn latch, and success publication paths were read. The added direct/queued tests cover stopping at `IsDone`, terminal text exactly once, model/usage retention, and structured rejection. Failure-path tracing remains distinct from successful publication.
- Cancellation: the complete queue was read. Independent tracked sets cover scoped and unscoped task/stream items; actor cancellation compares actor plus connection and optional normalized role. Logical scopes remain independent and use an injective actor/session/role/scope encoding; the changed global/admin selection is documented policy, not a player-isolation finding. Pending settlement precedes replaceable cancellation callbacks. Admission checks disposal and cancellation again under the queue lock.
- Buffering: reads covered count/byte admission, writer cancellation, normal drain, oversized handoff, cancellation-versus-handoff ownership, completion, and consumer abandonment. The documented one-upstream-item exception is not treated as a total-heap guarantee. No additional confirmed introduced defect was found in those paths.
- Structured policies: all parser/token checks and their added test cases were read. Strict object parsing, wrapper handling, actual property names, and negative/exponent/zero numeric values address the old heuristic defects without adding a game schema.
- History snapshot disposal was checked against `ConversationContextSnapshot.Dispose`: it clears commit ownership while preserving projection properties, so the returned diagnostic snapshot is not invalidated by the new `using` statement. Shared tool token estimation was traced to its existing policy seam.
- The new memory-snapshot tests pass the new scope provider explicitly; they therefore do not exercise the old direct-constructor composition in PR-EXEC-001. No current test result is used as proof that this compatibility path works.

## Read coverage

All 16 primary paths in `coverage.json` owner `01` were read. All 177 zero-context diff hunks were inspected against the immutable range; counts below use `git diff --unified=0` and are read coverage, not tests or semantic coverage. Runtime source was reviewed with adjacent callers/contracts rather than relying on the historical reports. No primary ownership gap remains.

| Primary path | Hunks read | Actual read depth |
| --- | ---: | --- |
| `Assets/CoreAI/Runtime/Core/Authority/ActorContext.cs` | 5/5 | Entire candidate type and grant/issuance context; binding marker and narrowing changes. |
| `Assets/CoreAI/Runtime/Core/Features/Orchestration/AiOrchestrationQueueOptions.cs` | 1/1 | Entire candidate file; new budget documentation/defaults and validation caller. |
| `Assets/CoreAI/Runtime/Core/Features/Orchestration/AiOrchestrator.cs` | 18/18 | Every diff hunk; full candidate admission and streaming execution through finality, request/history construction, snapshot lifetime, user/assistant persistence and publication, shared estimator call. Unchanged formatting/overflow utility tails were not exhaustively reread. |
| `Assets/CoreAI/Runtime/Core/Features/Orchestration/AiTaskRequest.cs` | 1/1 | Entire candidate request/clone contract, including explicitly shared attachments/tool-name/sampling references. |
| `Assets/CoreAI/Runtime/Core/Features/Orchestration/AnalyzerResponsePolicy.cs` | 8/8 | Complete changed validation/property predicate. |
| `Assets/CoreAI/Runtime/Core/Features/Orchestration/CoreMechanicResponsePolicy.cs` | 7/7 | Complete changed validation/token check. |
| `Assets/CoreAI/Runtime/Core/Features/Orchestration/CreatorResponsePolicy.cs` | 11/11 | Complete changed validation, JSON parser/wrapper normalization, recursive property traversal. |
| `Assets/CoreAI/Runtime/Core/Features/Orchestration/IAiOrchestrationService.cs` | 4/4 | Entire candidate contract, typed DIM adapter and all optional cancellation capabilities. |
| `Assets/CoreAI/Runtime/Core/Features/Orchestration/QueuedAiOrchestrator.cs` | 111/111 | Entire candidate file, lines 1-1891; every task/stream admission, claim, execution, cancellation, scope key, settlement, disposal and bounded-buffer branch. |
| `Assets/CoreAiUnity/Tests/EditMode/AiOrchestratorStreamingEditModeTests.cs` | 2/2 | All new lines/imports and complete new helpers/fakes; contextual surrounding fixture contract. |
| `Assets/CoreAiUnity/Tests/EditMode/ExecutionAdmissionRegressionTests.cs` | 1/1 | Entire added fixture, lines 1-455, including helpers/fakes. |
| `Assets/CoreAiUnity/Tests/EditMode/ExecutionAdmissionRegressionTests.cs.meta` | 1/1 | Entire two-line metadata file; GUID search found only this companion metadata. |
| `Assets/CoreAiUnity/Tests/EditMode/QueuedAiOrchestratorEditModeTests.cs` | 4/4 | All added/changed hunks, complete role-stop body and related recording/cancellation/persistence fakes; unchanged fixture not claimed exhaustive. |
| `Assets/CoreAiUnity/Tests/EditMode/RoleStructuredResponsePolicyEditModeTests.cs` | 1/1 | Entire added malformed/numeric/wrapper test block with policy implementations. |
| `dev-docs/audits/2026-10-23/01-core-execution.md` | 1/1 | Entire added historical report, lines 1-171; treated as old evidence and claims, not current findings. |
| `dev-docs/fix-wave-2026-10-24/01-execution.md` | 1/1 | Entire added repair contract/receipt, lines 1-114; checked intended behavior against current implementation, without adopting old green/red claims. |

Supporting read-only seams: `AgentMemoryScope.cs` (capture/key resolver, bound-empty behavior, public scope entry, AsyncLocal frame/lease); `ScopedAgentMemoryStoreDecorator.cs`; `IAgentMemoryScopeProvider.cs`; `DefaultAgentMemoryScopeProvider.cs`; `IActorIdentityProvider.cs`; `ConversationContextSnapshot.cs`; `CorePortableInstaller.cs` constructor composition; shared `ILlmClient.cs` chunk/finality/metadata contract only; relevant `MultiplayerSessionIntegrationTests.cs` admission/isolation cases. These cross-reads do not claim another owner's primary scope.

## Verification and limitations

- Read-only `git diff`, `git show`, repository searches, and line-numbered source reads only.
- The final read-only `git rev-parse HEAD` returned the candidate SHA above; scoped status showed only this untracked report and no runtime/test modifications.
- Direct report checks: all 16 owner paths are present, zero trailing-whitespace lines, zero Cyrillic prose lines, one finding. These are document checks only.
- No test, build, Unity Editor, built-player, WebGL/browser/device, network, or live provider validation was run.
- Existing unrelated workspace modifications are outside this audit.
- Final acceptance question: what did the repair tests miss? The existing constructor shape, with the provider owned only by the scoped decorator, is omitted. This is the single confirmed finding; other investigated concerns remain explicitly uncounted.
