# Independent adversarial review and narrow compatibility follow-up

Date: 2026-10-25. Model: `gpt-6.1-sol`; reasoning effort: `max` (root-pinned).
Checkout: `D:/Git/CoreAI`; baseline `69d5bcc37502cd4bb464a3bf9ac0c53b1624b804`.
This is an additional review of the integrated repair source, not a replacement audit or an executable acceptance receipt.

## Goal, boundaries and ownership

The goal is to verify supplied-context isolation, terminal finality, bounded buffering, cancellation/teardown stability, persistence truthfulness and endpoint lifetime at the repaired runtime joins. Existing interfaces, optional-package combinations, WebGL main-thread behavior, restricted grants and attachment borrowing remain invariant. Hosts own account authentication, identity issuance, matchmaking, connection policy and independent-world storage composition. Those policies are not automatic framework defects.

Read repository instructions, normative architecture rules, the execution/memory/provider/host/Mods/Rbx/Audit/Mirror work contracts and actual working-tree runtime diffs. Reviewed orchestration admission/finality/buffering/cancellation; summary transactions/snapshot ownership/CAS/key migration/cache leases; provider failure/replay/SSE/effective tool lists/registry leases; Rbx HTTP lifetime/trusted iteration/mutation gates; Audit coordination/pump/rotation; and Mirror disconnect reentry. Hub and aggregate documentation remain with their active owners.

Initially leased only the five NUnit-compatibility fixture files below and this report. Root accepted two additional framework findings and granted the exact chat/registry production, regression and guide paths recorded below. No worker Git mutation, build, test execution, Unity/MCP, package installation, `.meta`, assembly definition, scene, prefab, version or aggregate planning edit is authorized. Root owns snapshots, all executable checks and independent acceptance of this worker's repairs.

Before delegation/implementation: the likely regression is treating metadata resolution as request admission, losing the per-invocation release count, accepting a foreign same-role heartbeat, or changing cancellation attribution. The chosen repairs must use existing gates/events/test clocks, preserve explicit caller traces and refuse stale endpoints before invocation. No new authority or authentication seam is required.

## NUnit compatibility correction

The actual Unity 6000.6 compiler reports missing `NonParallelizableAttribute` in `com.unity.ext.nunit`. Reused the explanation in `LuaScriptVersionStoreEditModeTests` and Unity's sequential EditMode execution rather than adding a shim or changing NUnit dependencies.

- Guarded the portable-relevant attributes with `#if !UNITY_5_3_OR_NEWER` in `MemoryContractRegressionTests.cs`, `ProviderContractRegressionTests.cs` and `Audit/AuditLogWriterResilienceEditModeTests.cs`.
- Removed the attributes from Unity-only `RbxApi/Unity/RbxProceduralProjectionRenderEditModeTests.cs` and `RbxApi/Unity/RbxMaterialLifetimeRegressionTests.cs`.
- Preserved assertions, fixture cleanup and runtime code. Source read-back confirms all three remaining attribute usages are guarded and neither Unity-only fixture retains one. Root was notified immediately for recompilation.

## Additional confirmed findings

These findings are additional to the original 89 canonical intake findings. They concern framework contracts rather than game account policy. No P1 finding was confirmed in this review.

### ADV-001 — P2: foreign same-role tool events extend a stalled turn's idle deadline

Pre-follow-up source: `Assets/CoreAiUnity/Runtime/Source/Features/Chat/CoreAiChatService.cs:307` through the three tool event handlers. The static global events are matched by role only, with blank role accepted. `LlmToolCallInfo` already supplies `ActorId` and `TraceId`.

Trigger: actor A stalls while actor B uses the same role and emits tool starts/completions/failures more frequently than A's idle window. Each B event rearms A's timer, so a dead A request can retain its concurrency slot indefinitely. Two different turns for one actor also cross-rearm when only the role is compared. This violates the service's idle/no-progress budget and supplied-context isolation.

Root accepted the finding and leased `CoreAiChatService.cs`, `CoreAiChatServiceEditModeTests.cs`, `MultiplayerSessionIntegrationTests.cs` and relevant `Docs/CoreAI/MULTIPLAYER_SESSIONS.md` guidance. Required behavior: generate a trace once when absent before dispatch; retain an explicit caller trace; snapshot the admitted actor/trace/normalized role; accept progress only when all supplied event identities match. Missing event identity cannot identify a turn. Caller versus deadline cancellation attribution is unchanged.

Regressions authored: six virtual-clock mismatch cases (actor, trace, role and each missing identity); three exact-match event controls (start/completed/failed); task/stream trace assignment and preservation. Existing multi-step progress fake now emits the admitted request metadata instead of invented trace/empty actor. The streaming admission fixture now mutates the original actor, payload and trace before enumeration and requires the originally admitted history partition/payload. No sleeps determine correctness; the existing virtual scheduler controls time.

Source correction delivered: assign a missing trace during binding, capture the admitted actor/normalized trace/role into locals for the nonstreaming event handlers, and clone the bound streaming request before returning its iterator. Borrowed attachments remain borrowed. Explicit trace text and existing cancellation attribution remain unchanged. Final source anchors: `CoreAiChatService.cs:272`, `:309`, `:490` and `:678`.

Status: source-final; root executable receipts and independent acceptance remain pending.

### ADV-002 — P2: resolved endpoint handles can admit after their owned host retires

Pre-follow-up source: `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/LlmClientRegistry.cs:211`, `:429` and `:1393`. A resolved `TrackedEndpointClient` is intentionally metadata-only, but its `RequestLease` increments unconditionally without checking retirement or generation. The drain can observe zero, release the host, and then a previously returned handle starts a task or first stream iteration against that client. A concurrent increment after the zero observation can also begin while host release is already underway.

Trigger: resolve a ready endpoint handle without invoking it, deactivate/remove/replace/dispose that endpoint (host release sees zero), then invoke the saved handle or first `MoveNextAsync` of a previously created stream. The old backend is called after release. Reactivation of the same mutable runtime object additionally requires a generation check.

Root accepted the finding and leased `LlmClientRegistry.cs` plus `ProviderContractRegressionTests.cs`. Chosen design: synchronize actual request admission with retirement under the registry's existing gate; validate published endpoint reference, captured generation, active/ready state, registry lifetime and retirement; capture the admitted client in a per-operation idempotent lease. Refuse stale admission with typed `BackendUnavailable`. Metadata resolution/disposal must still take zero leases, and existing admitted operations must finish before ordinary owned-host drain completes.

Regressions authored: task and lazy-stream stale admission after deactivation, removal, replacement and registry disposal; stale generation refusal after reactivation with a fresh-handle success control; two active-operation drain controls; and synchronous release-callback reentry requiring the successor factory to wait for the predecessor release. Existing concurrent completion/stream lease tests remain unchanged in meaning. There are 13 new registry cases in total.

Source correction delivered: the per-operation lease validates and increments under `_gate`, captures the admitted client, and releases once. Metadata handles capture generation/client without taking a lease. Deactivation publishes retirement under that same gate before another caller can reactivate the mutable endpoint. `HostReleaseTask` is a promise published before invoking any host release callback, so synchronous reentry also waits for release completion. No worker, timer or public API was added. Final source anchors: `LlmClientRegistry.cs:217`, `:263`, `:450`, `:904` and `:1381`.

Status: source-final; root executable receipts and independent acceptance remain pending. Old saved runtime files remain source-compatible with all added tests/callers for root's controlled negative run.

### ADV-003 — P2: partial append failure can corrupt the shared Audit chain on retry

Source: `Assets/CoreAiUnity/Runtime/Source/Features/Audit/AuditLogWriter.cs:723` and append helper at `:776` (pre-additional-follow-up line anchors). `AppendLinesToFile` appends with `StreamWriter`; a write or dispose failure can occur after at least one complete line reached disk. `FlushBatchCore` then requeues the entire batch without truncating that prefix, resuming its actual tail or marking the path unrecoverable. The shared sequence/hash remains at the old head. A subsequent successful retry appends duplicate sequence numbers and an incorrect predecessor after the persisted prefix.

Meaningful regression: deterministically inject an exception after the first persisted line of a two-entry batch; retry and reopen; require one ordered verifier-valid chain with no duplicated sequence/entry. Existing `SimulateWriteFailureForTesting` runs before every disk mutation and does not exercise this failure mode.

Status: root requested the design below for a separately assigned owner; no Audit production correction lease or executed reproduction claimed by this worker. That owner's Audit runtime edits were visible during the final source handoff, so the pre-follow-up finding/line anchors above are historical and do not assert that its new transaction implementation still has this defect. The new implementation needs separate root review and executable evidence.

Minimal proposed repair (design only; a separate owner implements it):

1. Treat each append as an in-process file transaction while the existing shared path gate is held. Capture the pre-append file length before writing a missing newline or any batch line. Use a file stream plus a leave-open text writer; ensure the writer is fully disposed before truncation so no buffered characters can flush after rollback. The exception boundary must include flush/dispose, not only `WriteLine`.
2. On append failure, restore that exact original length and settle the file stream. Only after successful rollback may the caller retain the old shared sequence/hash and requeue the entire batch. A failed persistence-arming answer remains separately observable; it must not convert an already committed append into a replay.
3. If rollback itself fails, quarantine the shared path: retain a shared recovery-required state, preserve bytes and original/rollback errors, block all writers' append/rotation/retry before disk mutation and deliver a bounded diagnostic. Do not silently reset the hash head or blindly replay. Keep the quarantined registry state after the last owner retires; later same-process writers must refuse until explicit verified recovery. A mere retry, new instance or new owner is not recovery.
4. Process restart must validate the existing current file before append (including its hash links and partial trailing line); a corrupt/partial chain cannot be made accepted by reading one parseable tail or appending a ChainReset. Refuse append and preserve the file for host/operator repair. A fully valid persisted prefix may resume at its real sequence/hash; no crashed in-memory queue survives and no exactly-once crash durability is promised.
5. Add a narrow internal append/fault seam or injected file port to deterministically fail after a complete line and separately fail rollback; tests must exercise the real writer's commit/requeue path. Required cases: successful rollback + retry has one valid chain; rollback refusal blocks peer writers and reopens without additional mutation; restart with torn/duplicate tail refuses while a valid prefix resumes; rotation-marker append obeys the same transaction boundary; persistence refusal after successful append never duplicates that batch. Keep existing format, main-thread diagnostics, entry bounds and WebGL automatic persistence semantics.

This intentionally chooses refusal after an unrepairable rollback over a more complex partial-prefix reconciliation algorithm. It needs no public account/session API and no cross-process lock. Root owns the exact final recovery API/lease and independent review.

## Other review conclusions and limits

- Bound memory frames retain explicit Empty instead of rereading mutable providers. Direct streaming reenters the actor scope around each inner `MoveNextAsync` and disposal. Summary preparation transfers or discards generation ownership; snapshot disposal defers lease release until admitted/queued commits settle. File states retain waiters and prepared generations, avoiding eviction ABA. Conservative legacy identity migration and strict UTF-8 hashing preserve ambiguity rather than merging identities.
- Effective role-bound skill proxies feed both native function creation and policy in Meai. Failure receipts remain visible to replay suppression, auth refresh does not replay an already committed turn, and code-only failures remain failures. Root's date-like SSE content correction was read in the actual parser diff. Schema/line/response-size hardening beyond the admitted contracts is not promoted without a confirmed trigger.
- HTTP requests use per-script/generation lifetime cancellation through DNS/transport and skip late canceled completions. The existing trusted iterator returns iterator/state/control without exposing protected raw metatables and is restored at source ingestion. Scoped version-store mutation requires the explicit host policy and WorldEdit. Intended FullAccess remains an explicit privileged composition, not an automatic bypass finding.
- Mirror detaches its entry snapshot of pending requests, invokes every peer listener before their completions, contains completion failures, and guards same-connection notification reentry. No additional confirmed P1/P2 was identified in the inspected teardown paths. Real transport/session behavior remains unverified here.
- Actor-keyed chat factory selection is a lifetime policy, not an implicit actor binding; the guide already requires passing the admitted context. Default shared world storage, multi-process/symlink coordination and custom-store weaker transaction contracts remain documented composition boundaries. Legacy source-store `Save` is best effort; callers needing truth consume optional `SaveStrict` or report Unconfirmed. No login manager or automatic account detection is requested.

## Verification evidence

No build, test or Unity operation was run by this worker. Source checks and reading existing receipts do not establish runtime acceptance.

Read current root-produced TRX files directly by `UnitTestResult` rows:

| Receipt | Passed | Failed | NotExecuted | Rows |
| --- | ---: | ---: | ---: | ---: |
| `artifacts/fix-wave-20261025/core-results/core.trx` | 2833 | 3 | 3 | 2839 |
| `artifacts/fix-wave-20261025/lua-full-results/lua-full.trx` | 1925 | 3 | 41 | 1969 |

These receipts precede this worker's additional corrections and regressions. Root's newer source fixes require fresh applicable results. Unity source compilation is a separate gate; a stripped-NUnit source correction is not a Unity test pass. No player/WebGL/browser/IL2CPP/device/network/load/performance/live-model or publication gate is claimed.

Root verification filters after preserving a failing-before snapshot:

```text
CoreAI.Tests.EditMode.CoreAiChatServiceEditModeTests
CoreAI.Tests.EditMode.MultiplayerSessionIntegrationTests
CoreAI.Tests.EditMode.ProviderContractRegressionTests
CoreAI.Tests.EditMode.LlmClientRegistryLeaseEditModeTests
CoreAI.Tests.EditMode.RuntimeEndpointRequestSettingsEditModeTests
```

Require positive case presence, raw outcome accounting and failure-before/passing-after evidence for ADV-001/002. Root independently reviews the actual production/test diff; this review cannot self-accept its new implementation.

Root's first two actual Unity negative-run attempts stopped at unrelated fixture compilation before any tests: `adversarial-red.log` reports missing `IConfirmedWorldMutationGate` in `LuaModsLlmToolEditModeTests.cs:635`; `adversarial-red2.log` reports two missing Hub fake constructor arguments in `CoreAiModsHubBinderFullTierEditModeTests.cs:1555,1592`. Neither is a failing ADV product-test receipt. Root explicitly deferred negative execution, released the runtime hold, and will use the immutable old runtime copies after a clean compile, restoring the final files in `finally` with no active writers. No additional executable check was run by this worker.

## Milestone state

- Compatibility milestone complete at source level; immediately handed to root for compilation.
- Independent review found ADV-001/002, both accepted by root for a narrow repair. Failing-before fixture checkpoint authored while those runtime files remained unchanged. Root owns snapshot capture/execution.
- Root saved both pre-follow-up runtime files and all three fixture files with SHA-256 receipts in `artifacts/fix-wave-20261025/adversarial-red-snapshot/receipt.json`. The later streaming caller-mutation, active-drain and release-reentry cases postdate that initial fixture snapshot; their execution evidence must be recorded separately or with a new final-fixture snapshot.
- ADV-003 reported separately; no unauthorized Audit source mutation.
- Source milestone complete: reviewed both actual runtime follow-up diffs against the immutable copies; checked new test signatures/guards and `AiTaskRequest.Clone` field coverage; inspected old/new generation, metadata-only, lazy admission, active drain, release reentry and caller/deadline paths. Static diff/whitespace checks pass. No out-of-scope production file was changed. Root alone accepts these new implementations after actual checks.

## Source-final inventory

Exactly these ten source/test/guide paths plus this report were written by this worker. The initial five-file compatibility correction and later approved lease are both included. SHA-256 values are exact final bytes; later changes invalidate this checkpoint.

```text
61e24a700dae1adcaab11cbe66299cba1e9de9e09a9815577def80974039d129  Assets/CoreAiUnity/Tests/EditMode/MemoryContractRegressionTests.cs
0a4a5ff45f4155b8c1841a0ecc370395c1ae1c76813891de6b303370fe67fa6d  Assets/CoreAiUnity/Tests/EditMode/ProviderContractRegressionTests.cs
92a36532e43f0568d2f87f74dcd3879c8d02d835df865ca1b9a314bcfd799bf6  Assets/CoreAiUnity/Tests/EditMode/Audit/AuditLogWriterResilienceEditModeTests.cs
2ec29138c9d60c272379d748cfad04e9820d166a183b0b6296c53abd7386e9e8  Assets/CoreAIMods/Tests/EditMode/RbxApi/Unity/RbxProceduralProjectionRenderEditModeTests.cs
8fe2e3eaa1203e40b97e10018b401cd0c71cc655fc0704b3b557bffe57767747  Assets/CoreAIMods/Tests/EditMode/RbxApi/Unity/RbxMaterialLifetimeRegressionTests.cs
b148d1218bda84768a72d41e94045827245bd10c5a4a5ec361f9f3ee89c1f386  Assets/CoreAiUnity/Runtime/Source/Features/Chat/CoreAiChatService.cs
7ff8d07ce07145ae045c61124e5b7aee352e7ffc5d3a65bdae4d67556eb86446  Assets/CoreAiUnity/Tests/EditMode/CoreAiChatServiceEditModeTests.cs
6c85511ef79712efa336e25f2cda159c8257f484fb5f95f1eced17eadea372bf  Assets/CoreAiUnity/Tests/EditMode/MultiplayerSessionIntegrationTests.cs
fc5877a9a5faecf302f4c4d4da6278cf45121e3d8a8ad4e49838ec48fa487a63  Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/LlmClientRegistry.cs
7747001871906f8f6da2b58005fa54cda91852b5f3baf8c2a9c18424d75d7fd2  Docs/CoreAI/MULTIPLAYER_SESSIONS.md
```

CHANGED: NUnit compatibility; exact chat progress correlation and streaming request capture; atomic registry generation admission/retirement/release publication; regression fixtures; developer integration guidance; this review/design report.

VERIFY OUTPUT: scoped `git diff --check` exit 0 without diagnostics; untracked fixture/report whitespace scan found zero trailing-whitespace lines; new Provider/Multiplayer fixture scan found no implicit `var` or Cyrillic prose. Read saved root receipts as documented above. These are static/read-only checks, not C# execution.

NOT DONE: no worker build/test/Unity/Git; no executable ADV failing-before/green claim; no Audit production fix; no root independent acceptance; no platform or publication claim.

CONCERNS: require the controlled negative run plus final focused Unity tests with positive fixture presence. The retirement promise deliberately avoids asynchronous-continuation flags and keeps existing player-loop lifecycle; WebGL runtime behavior still needs its own gate. Audit rollback/quarantine must be implemented and reviewed by its named owner. No pending question or active wait remains in this worker.

## Root executable acceptance - 2026-10-25

The controlled pre-follow-up chat/registry snapshot run is `artifacts/fix-wave-20261025/unity-adversarial-red-final.xml`: 49 actual cases, 30 Passed and 19 Failed. Eighteen failures reproduce ADV-001 (five) and ADV-002 (thirteen); the remaining failure diagnoses three destroyed test-panel subscribers. The other selected cases are controls. Both final runtime files were restored byte-identically in `finally`; `adversarial-red-final-source.json` records both hashes and `unity-adversarial-red-final-exit.json` records restoration.

Final Windows EditMode `unity-editmode-final5.xml` has 7,917 Passed, zero Failed and 13 Skipped. All nine progress-correlation cases, thirteen retirement cases and twenty-seven audit-writer resilience cases passed. ADV-003 implementation and recovery contracts are recorded by its separate owner in `workdoc04-audit-log.md`. Root accepts these measured source/integration joins; none establishes built-player, browser durability, real-network or live-provider acceptance. The later Windows benchmark PlayMode follow-up remains a separate gate.
