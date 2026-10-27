# Host transport and benchmark delta audit

Status: complete static review of the seven assigned paths and all 31 changed hunks. Two source-confirmed P2 findings in the Mirror repair; no additional MCP or benchmark finding established. Executable checks and final acceptance remain root-owned.

Checkout HEAD at review: `bc3fd74f0a889bfe1b1e7babc6bc02ed48a7c05e` (`demo`). The reviewed repair is the uncommitted working-tree delta against that HEAD, not a new committed revision.

Scope: `McpHttpServer.cs`, `McpHttpServerIntegrationEditModeTests.cs`, `MirrorNetworkBridge.cs`, `CoreAiMirrorSessionHost.cs`, `MirrorSessionHostEditModeTests.cs`, `GameCreationBenchmarkHarness.cs` and `GameCreationBenchmarkPlayModeTests.cs`, with original reports 10/11/12 and repair reports 25/26/27 as context. Only this report may be written.

Review goal: verify teardown ownership, callback reentry and replacement isolation in MCP/Mirror, plus per-turn benchmark time attribution and result sealing. Tests and prior worker statements are evidence to inspect; neither substitutes for the actual runtime diff or an executable result.

No runtime/code/test edits, Git mutation, builds, tests, Unity/MCP, provider or network calls, or extra agents are authorized for this audit.

## Findings

### DELTA-NET-001 [P2] Match release against the session actually bound by Admit

- Changed anchor: `Assets/CoreAIMirror/Runtime/CoreAiMirrorSessionHost.cs:259-261`, the newly added comparison of `releasedPeer.SessionId` with `context.SessionId`.
- Valid trigger: a caller uses the existing public `Admit(connectionId, admission, sessionId)` with a trusted admission whose context has session `session-actor-a`, and supplies the explicit bridge session `session-a`. This is the shape already used throughout `MirrorSessionHostEditModeTests`; the helper at lines 410-420 issues `session-` plus the actor id, while calls pass separate session strings. No forged context or new authentication requirement is involved.
- Expected: when the world ends this actor through `bridge.UnregisterActor("actor-a")`, the matching released binding removes the session-host entry and identity. A reused transport id can subsequently admit another actor.
- Actual causal path: `Admit` sends `sessionId ?? context.SessionId` to `BindConnection` at lines 141-143, but stores the original `admission.Context` at line 169. The new release predicate compares two values that need not be the same even for the original, current binding. The ordinary `ReleaseBinding` notification carries `session-a`, the stored context carries `session-actor-a`, and the host returns without removing either its session or identity. The bridge has removed the peer, but the host still reports the connection occupied and refuses admission of a different actor under that id.
- Introduced cause: the old integer notification removed the entry for this ordinary release. The new generation filter adds an equality requirement without preserving the actual bound session it needs to compare. The production bridge provider passes null today, so its default route uses matching values; that does not remove the supported explicit-override route from the public session-host contract.
- Minimal root regression: extend the real `MirrorSessionHostEditModeTests` with null-session and distinct-explicit-session cases. Admit the actor, call `UnregisterActor`, assert `LiveSessionCount == 0`, no retained identity, and successful fresh admission of a different actor on the same integer id. Retain a separate same-id replacement test using genuinely different admitted context sessions. Track/compare the bound peer generation rather than silently narrowing the existing override API.
- Test limitation: the new `PeerDisconnect_SameIdReplacement_SurvivesDelayedBindingRelease` does not catch this. Its admission helper issues the same context session for both actor-a admissions while the supplied bridge session strings differ; the new predicate ignores the old notification, but would also ignore a normal release of either such binding. The test later uses `NotifyDisconnected`, whose earlier peer listener removes the host entry before this predicate runs.
- Confidence: source control-flow proof only. No Unity fixture or transport reproduction was executed by this reviewer.

### DELTA-NET-002 [P2] Capture the companion release subscribers before legacy callbacks can clear them

- Changed anchor: `Assets/CoreAIMirror/Runtime/MirrorNetworkBridge.cs:2732`, reading `BindingReleasedWithPeer` after invoking the legacy `BindingReleased` callbacks at line 2731.
- Valid trigger: compose a real session host and bridge, admit with `sessionId: null` to isolate this from DELTA-NET-001, and attach a legacy release subscriber that calls `bridge.Dispose()`. Then end the world actor via `bridge.UnregisterActor`. Reentrant disposal from this event is an existing supported fixture path: `NET001_Unregister_ContainsReentrantBindingAndCompletionSubscriberFailures` already disposes and throws from a legacy release subscriber.
- Expected: the host still receives the release captured for that departing peer, forgets its live session/identity, and cleanup continues despite callback disposal/failure.
- Actual causal path: direct `ReleaseBinding` removes the peer, invokes the legacy event, then reads the companion event from the mutable field. Reentrant `Dispose` clears `BindingReleasedWithPeer` at line 1516. The newly added invocation therefore sees null and never reaches `CoreAiMirrorSessionHost.OnBindingReleased`, leaving the host's dictionaries stale. Before the repair, the host was itself an earlier subscriber of the legacy event and was included in that event's captured invocation list.
- Introduced cause: moving the host to a separate event changes its delivery order and loses the old invocation-list snapshot guarantee. The `NotifyDisconnected` path correctly snapshots both delegates before callbacks at lines 956-957; direct `ReleaseBinding` does not.
- Minimal root regression: in the real host fixture, admit with matching/default context and bridge sessions, subscribe a legacy callback that disposes the bridge, call `UnregisterActor`, and assert the host session and identity are gone. Repeat cleanup and retain the existing throwing subscriber/completion assertions. Snapshot the companion delegate before invoking any legacy callback; do not suppress the legacy callback or dispose the host as a substitute for delivering its release.
- Confidence: source control-flow proof only. The existing bridge-only reentrant test has no session host and cannot detect the lost companion consumer.

## Repair assessment and evidence boundaries

### MCP

- Read all three `McpHttpServer` delta hunks and the full affected start/stop/dispose, accept-loop and request-finalization methods. `Stop` now closes/releases the old loopback listener before `_cts.Cancel`, so a cancellation callback can bind a new transport on that same prefix. Context completion remains an asynchronous `Task.WhenAll`; handler finalization removes only that transport's context and releases its slot.
- Read `CoreAiMcpServer.StopListening`, startup-failure identity checks and static restart as adjacent context. The host detaches `_server`, registry/auth state and the active generation before invoking `stopped.Dispose`. The stopped transport is captured locally, and no replacement host fields are overwritten after that disposal. This host code has no additional working-tree diff in this audit.
- The new real HTTP regression starts a blocking tool through an actual `HttpClient` request, starts a distinct replacement `McpHttpServer` on the same port synchronously from cancellation, waits for old `Completion`, then verifies that the replacement serves a real initialize request. It does not replace the prefix with a fake or switch ports. The restart-after-stop control also uses the same real server object and loopback prefix. Initial URL ACL refusal is optional infrastructure; a replacement bind failure is not skipped.
- Read the existing automatic-host reentrant residency test as separate context. It checks generation/registry/auth/queued-call ownership using `InMemoryTransport`. The new real HTTP test checks the transport boundary directly, with session-optional POSTs and no configured token; it does not itself boot the Unity automatic host or exercise a new authenticated queued tool call after reentry. Those limits remain explicit rather than combining distinct tests into an unperformed end-to-end claim.
- No additional introduced MCP defect was established. Same-object restart from inside its own transport cancellation callback, arbitrary throwing log sinks and real-socket capacity are not established by this audit or the added tests.

### Mirror

- The captured departing `RbxNetworkPeer` is the right data needed to distinguish a delayed release from a replacement. `NotifyDisconnected` captures both event lists before invoking arbitrary peer listeners, preserves same-peer cleanup checks, retains nested cleanup/finalization and attempts the captured companion subscribers even when a callback clears the live event fields.
- The new helper individually invokes companion subscribers and records failures. The generic `InvokeSubscribers<T>(Action<T>, T, ref List<Exception>)` declaration and body are present and intact in actual source. Existing diagnostic failure handling and multicast completion isolation remain present; no missing-signature conclusion is based on an intermediate worker edit.
- The legacy `Action<int>` event remains available, and bridge disposal clears both events. The new host subscription/unsubscription is consistent. DELTA-NET-001 and DELTA-NET-002 are the two remaining mismatches between those lifecycle seams.
- The new same-id regression verifies replacement identity, live session count, server-event routing and later normal disconnect. Its callback-only/offline transport composition does not prove sockets, replicated world teardown, room capacity or separate-process behavior.

### Benchmark

- Read all 13 harness hunks and the complete new two-test block, plus turn start/stream recording/seal/end paths and the actual `RunScenario` call site.
- `_currentTurnStartedUtc` is separate from `_lastProviderActivityUtc`, which still resets between turns so prior generation activity is not reused as new evidence. The new clock seam defaults to UTC time and supports deterministic tests. `Seal` snapshots the start timestamp under the same lock as response/timeout/activity evidence; late recording cannot change the exposed sealed baseline.
- Actual source exposes only `CurrentTurnStartedUtc`; speculative `CurrentTurnStartUtc` and `TurnStartedUtc` aliases are absent. `RunScenario` passes that value to the real gap helper.
- `ProviderActivityGapSeconds` selects the latest provider activity, tool activity or current-turn start. With a quiet continuation starting at 520 seconds, the controlled 570-second end yields a 50-second gap; a 700-second end yields 180 seconds. The real `BenchmarkProviderFailurePolicy` retains its 120-second default, Model/Environment classification and Environment-only retry policy.
- The two new tests exercise the real capture and policy with a controlled clock, preserve null generation activity for the quiet turn, and assert Model/no retry below threshold versus Environment/retry beyond it. Existing response-reset/seal tests check that late response and timeout writes do not alter frozen evidence. These are PlayMode-assembly tests and were not part of the communicated EditMode selection.
- No additional benchmark source defect was established. Report `27-benchmark-repair.md` is stale about the removed aliases. Its narrative also says the first turn is active until 520 seconds, whereas the actual new fixture completes that first turn at clock zero and advances the clock before starting the second. The fixture still exercises the missing current-turn baseline, but source and fixture timing govern this assessment.

## Complete hunk and path ledger

All seven assigned paths and **31 hunks, +385/-32 lines** were read against HEAD. Headers below are the complete zero-context old-to-current ranges; a missing count means one line. Surrounding code reads above resolve the affected contracts; this is not a claim that every untouched package line was audited.

| Path | Hunks | + / - | Complete reviewed ranges |
| --- | ---: | ---: | --- |
| `Assets/CoreAIMcp/Runtime/Server/McpHttpServer.cs` | 3 | 13 / 8 | `-159,8 -> +158,0; -167,0 -> +160,4; -181,0 -> +178,9` |
| `Assets/CoreAIMcp/Tests/EditMode/McpHttpServerIntegrationEditModeTests.cs` | 3 | 143 / 0 | `-222,0 -> +223,101; -307,0 -> +409,18; -344,0 -> +464,24` |
| `Assets/CoreAIMirror/Runtime/CoreAiMirrorSessionHost.cs` | 4 | 14 / 3 | `-66 -> +66; -211 -> +211; -248 -> +248; -254,0 -> +255,11` |
| `Assets/CoreAIMirror/Runtime/MirrorNetworkBridge.cs` | 6 | 35 / 0 | `-686,0 -> +687,7; -949,0 -> +957; -986,0 -> +995,2; -1505,0 -> +1516; -2720,0 -> +2732; -2786,0 -> +2799,23` |
| `Assets/CoreAIMirror/Tests/EditMode/MirrorSessionHostEditModeTests.cs` | 1 | 53 / 0 | `-102,0 -> +103,53` |
| `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/GameCreationBenchmarkHarness.cs` | 13 | 52 / 21 | `-233,11 -> +233,14; -287,0 -> +291,9; -296 -> +308; -300,0 -> +313,5; -303,0 -> +321; -412 -> +430; -414 -> +432; -487,0 -> +506; -501,0 -> +521; -609 -> +629; -2212,2 -> +2232,3; -2226,3 -> +2247,13; -2523 -> +2554` |
| `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/GameCreationBenchmarkPlayModeTests.cs` | 1 | 75 / 0 | `-665,0 -> +666,75` |

Working-file SHA-256 values at the reviewed source checkpoint:

| File | SHA-256 |
| --- | --- |
| `McpHttpServer.cs` | `F9DD633BDBC233C25DA651F39B8893A352C3939D579E104A99C77551C52653C4` |
| `McpHttpServerIntegrationEditModeTests.cs` | `C4AB387DAECE33D67266838F3973DD5D3910EE94C616FAE48978B50E61F5ECC8` |
| `CoreAiMirrorSessionHost.cs` | `440E19E79E603F4663DEEFC3349AA35EBE3CD97C9EB72C95CFEB132FCB468296` |
| `MirrorNetworkBridge.cs` | `8415FF66DB4443161F33077E08F1F6A2D0E739DC7B631FF1766F5AB9356D2E5A` |
| `MirrorSessionHostEditModeTests.cs` | `DFECFB8EAE654A936F832889A01FF597092F91983969BC5CA1D450EF34F8F5CC` |
| `GameCreationBenchmarkHarness.cs` | `2C35B89D864FEE5A62CB4512F3429CBA4F0E50DC1E75D699753E2F3E41A85098` |
| `GameCreationBenchmarkPlayModeTests.cs` | `ACC82765A841833CE0E4F1A457FC78FF5B78D725C766EE94B7220076C5FE1AA1` |

Adjacent read-only context: MCP host generation and residency tests; Mirror world unregister, admission provider call site, actual fixture admission helper, bridge-only callback/disposal/completion regressions; benchmark response-evidence fake and failure policy in `GoalScore.cs`. Original reports 10/11/12 supplied the original finding scenarios; repair reports 25/26/27 were checked as implementation claims, not accepted proof. No Gateway staging feature or host authentication/account/world policy was audited.

## Verification receipts and unrun gates

- Direct static commands: `git rev-parse HEAD`, assigned `git diff --no-ext-diff` with contextual and zero-context hunks, `git diff --numstat`, targeted `rg`/source reads, and read-only `Get-FileHash`. These establish the source checkpoint, ledger and causal paths only.
- Root communicated an earlier focused Unity selection of 432 cases, 429 Passed and 3 Failed, with all selected MCP/Mirror tests passing; the failures were two Hub settings cases and a skill cancellation subtype case. Root subsequently communicated two repairs and a focused 27-of-28 passing selection, with the remaining Hub case instrumented separately. This reviewer did not open those result files or rerun the selection, so these are root-reported receipts, not independently verified execution.
- Root also reported zero compile errors for Core/Source/Mods.Hub and an earlier portable Core result of 2,853 Passed/3 NotExecuted before the latest new case. Those scopes do not establish execution of the benchmark PlayMode cases or the two new Mirror scenarios identified here.
- The benchmark `ProviderInactivityGap_` tests remain pending in the supplied root status. No real provider latency/quality measurement, browser/player/device result, socket capacity, replicated room behavior or publication result is inferred.

## Root handoff and repair recommendation

Root accepted both Mirror findings while this source domain remained frozen. That acceptance is adjudication, not execution of either reproduction.

No existing exact, stable binding-generation token was found in the reviewed bridge/host state. `_peersByConnection` holds a value peer, the connection handle is a reusable integer string, the optional authenticator admission object is not present in every composition, and `JoiningConnection` exists only while readiness is pending. None reliably identifies every published binding across empty or reused explicit session overrides.

Use an internal opaque token associated with the actual binding publication and with the host's corresponding admission record. Carry the captured token in the internal companion notification while preserving the public `Admit`/`BindConnection` APIs and legacy integer event. Snapshot both event lists before any legacy callback can dispose or replace state. The outer `NotifyDisconnected` replacement-cleanup guard must also compare the captured binding token: `SamePeer` value equality alone cannot distinguish a replacement that deliberately reuses the same actor, explicit session value and connection id.

Root regression controls should include a distinct explicit override versus the context session, empty and reused explicit overrides during same-id replacement, and a legacy release subscriber that disposes the bridge. These controls must preserve the override's existing meaning; adding a new uniqueness rule or refusing the supported override would narrow the host contract rather than repair binding ownership.

## CHANGED

Only this report: initial in-progress header, complete source/diff ledger, two Mirror findings, repair assessments and explicit execution boundaries. Runtime, tests, prior reports and shared trackers remain unchanged by this audit.

## VERIFY OUTPUT

Static inventory: seven assigned paths; 31 hunks; 385 added and 32 deleted lines; two source-confirmed P2 findings; zero counted P1/P3 findings. Final read-only checks found all seven reviewed source hashes unchanged, seven primary ledger rows, two P2 finding headings and no trailing whitespace or Cyrillic prose. Actual source retains the generic Mirror subscriber helper and only the canonical benchmark turn-start property. No executable verification was performed by this reviewer.

## NOT DONE

No code/test repair, Git mutation, build, test run, Unity/MCP, provider/network call, extra agent, player/IL2CPP/WebGL/browser/device, load/performance or publication operation. Root owns regression implementation and executable acceptance of the two Mirror findings and pending benchmark cases.

## CONCERNS

The source checkpoint still has two introduced Mirror cleanup paths that can leave host sessions/identities stale. Passing existing selected tests does not cover the explicit-session ordinary-release case or the session host behind a disposing legacy subscriber. Separate timing/source assertions and historical worker completion comments are not full runtime acceptance.

## QUESTIONS

None block the read-only handoff. Root has the exact changed anchors, valid triggers and real-fixture regression plans for both findings.
