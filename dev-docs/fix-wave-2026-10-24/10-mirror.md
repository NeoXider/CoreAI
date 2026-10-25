# NET001 Mirror repair work log

Date: 2026-10-25. Final source review: `gpt-6.1-sol`; reasoning effort: `max`.
Checkout: `D:/Git/CoreAI`; reviewed baseline: `69d5bcc37502cd4bb464a3bf9ac0c53b1624b804`.
Status: **source-final; root-owned Mirror executable acceptance is pending**. This receipt supersedes the earlier partial-worker progress state.

## Scope and acceptance

- Goal: preserve mandatory peer/world teardown when a pending completion or lifecycle subscriber throws.
- Exclusive writes: `Assets/CoreAIMirror/**` except metadata, package manifests and changelogs; this work log only outside that directory. No delegation, git mutations, Unity/MCP, dependencies, builds or executable test runs by this worker.
- Preserve public signatures, C# 9 compatibility, optional `MIRROR`/test defines, request ordering and removal before callbacks, reentrant cleanup, exactly-once peer disconnect and binding release. Host authentication/account/session policy is out of scope.
- Reuse: repair the existing bridge/session lifecycle and extend the existing OfflineMirror fixtures; no new transport, service or dependency is needed.

## Finding review

| Finding | Reproduce and guard review | Disposition | Regression acceptance | Proposed files |
| --- | --- | --- | --- | --- |
| NET001 / NET-001 | Confirmed original call chain: an uncontained pending completion ran before `PeerDisconnected`, skipping the session host's world teardown while binding release removed the session. The repair now detaches the departing pending batch before peer notifications, completes it afterward, and defers binding notification during that same disconnect. | Framework fix implemented and independently source-reviewed. No further confirmed P1/P2 in the inspected teardown paths. Runtime closure requires the root's fresh Mirror receipt. | Actual-world OfflineMirror regressions cover Player, signal and chat teardown, both pending completions, empty owned state and no later duplicates. Additional cases cover throwing/multicast lifecycle subscribers, reentrant cleanup, replacement sessions, client cleanup and timeout. These are authored acceptance assertions, not an executed pass. | `Runtime/MirrorNetworkBridge.cs`, `Tests/EditMode/MirrorKickEditModeTests.cs`, `Tests/EditMode/MirrorBridgeRulesEditModeTests.cs`, `README.md`. No session-host source change. |

## Design review answers

- Real requirement: an arbitrary callback must not suppress framework-owned teardown or other outstanding completions. Failure isolation must retain useful diagnostics.
- Relevant invariants: removal before invoking external code; no mutation-during-enumeration; no double completion/release under reentrancy; bounded existing collections; ordinary exception containment with observable errors; no new threading or blocking. Existing callback seam provides no cancellation handle, so cancellation semantics are unchanged.
- Likely mistake: catching only at the provider, or catching an entire multicast invocation, leaves later mandatory subscribers uncalled. Isolate individual callbacks/subscribers after removing owned state.
- Acceptance review: inspect every leased diff and static checks; root owns real compile/test results and final independent QA. No pass will be inferred from source inspection.

## Completed source review and README readback

- Read the actual full diff in all four leased package files, repository and architecture rules, original audit, session-host teardown, adjacent bridge binding/request/disposal paths, and the independent review's Mirror conclusion in `13-adversarial-review.md`. Earlier implementation workers are settled; this closure pass changed only this work log.
- The entry batch is removed before arbitrary peer code runs. Every peer subscriber is attempted before those pending completions. Binding-release subscribers are captured and delayed while the same connection is being notified, including when an earlier peer subscriber unregisters or disposes the bridge. The session host therefore retains the information required to remove the actual Player.
- Completion and binding-listener failures are contained per subscriber and sent to the existing log seam. A throwing log seam falls back to a Unity `AggregateException` containing both failures. Peer failures surface after cleanup: one preserves its original exception/stack; multiple failures aggregate. The scene provider retains its existing catch/report boundary. No logger-throw executable regression is credited by this review.
- Same-connection notification reentry is refused. A new request to a still-bound tearing-down connection fails immediately. Pending failure uses the old batch, so a completion that admits a replacement on another connection does not fail that replacement's new request. Late disconnects of an already unbound connection do nothing.
- Public signatures, optional assembly constraints, transport/dependency composition and host authentication policy are unchanged by the repair. Existing client admission-loss, connection-replacement, disposal and timeout paths now isolate all outstanding completion subscribers too.
- README lines 130-141 describe the reviewed ordering, reentry, diagnostics and failure behavior. Its existing optional `MIRROR`/`UNITY_INCLUDE_TESTS` setup, unsupported host mode, absent world-state replication and unproven capacity remain explicit. This repair supplies no game login, account or credential policy and establishes no scale result.

## Authored regression matrix

There are 17 new NUnit cases across eight methods; this is a source inventory, not a runner result.

| Fixture and `NET001_` method suffix | Cases | Required behavior |
| --- | ---: | --- |
| `MirrorKickEditModeTests.TransportDrop_ThrowingCompletionDoesNotSkipWorldCleanup` | 3 | Actual admitted Player, signals and chat resources are removed once; both pending callbacks run in order despite throw/unregister/dispose; maps empty; no later timeout or extra transport drop. |
| `MirrorKickEditModeTests.ThrowingPeerListeners_StillDisconnectTheActualWorldBeforeErrorsSurface` | 5 | None/notify/unregister/disconnect/dispose reentry cannot suppress the actual-world listener or its binding-release snapshot; both peer errors remain observable after cleanup. |
| `MirrorKickEditModeTests.ThrowingCompletionThatAdmitsAReplacement_PreservesTheNewWorldSession` | 1 | Old Player teardown finishes before replacement admission; replacement session and request survive the old batch and stale disconnect. |
| `MirrorBridgeRulesEditModeTests.Disconnect_PreservesThePeerFailureAfterEverySubscriberAndCleanup` | 1 | All peer/completion subscribers run, request during teardown fails at once, original peer exception survives, late responses are orphaned and no timeout duplicates occur. |
| `MirrorBridgeRulesEditModeTests.Unregister_ContainsReentrantBindingAndCompletionSubscriberFailures` | 1 | Multicast binding/completion failures and disposal reentry cannot interrupt remaining subscribers or double release. |
| `MirrorBridgeRulesEditModeTests.BindingRelease_AReplacementKeepsItsRegistrationAndNewRequest` | 1 | Replacement from a binding listener preserves registration and its new request while old callbacks fail independently. |
| `MirrorBridgeRulesEditModeTests.ClientCleanup_FailsEverySubscriberAndRequestDespiteThrowingReentry` | 4 | Forget admission, disconnected pump, replaced connection and disposal complete every entry/subscriber once despite a throwing reentrant callback. |
| `MirrorBridgeRulesEditModeTests.Timeout_ContainsAThrowingSubscriberAndCompletesEveryExpiredRequest` | 1 | One throwing completion subscriber cannot suppress another subscriber or expired request; no later duplicate. |

## Exact source receipt

All paths below are repository relative. SHA-256 values are from the final source readback on 2026-10-25. Package manifests/version/changelog/metadata/assembly definitions are outside this worker's write lease; the visible manifest change belongs to the root's release operation.

| Changed package file | Added / removed lines | SHA-256 |
| --- | ---: | --- |
| `Assets/CoreAIMirror/Runtime/MirrorNetworkBridge.cs` | 136 / 41 | `5581dbabb7a29c10598274994725d3365d475261f736fb1942224b3d810f4236` |
| `Assets/CoreAIMirror/Tests/EditMode/MirrorBridgeRulesEditModeTests.cs` | 270 / 1 | `ce47ecdb9348727b533f044d5bf506e91194a8de602bcd901f76e2ba1fa3059f` |
| `Assets/CoreAIMirror/Tests/EditMode/MirrorKickEditModeTests.cs` | 294 / 1 | `7d6647af58ffbba096d26243875df54d4bf9c7576d478d4a65d09831ca8359e2` |
| `Assets/CoreAIMirror/README.md` | 12 / 0 | `a15a52b39a48275b403fe866c2ad25f087b56bbf6148041e5c0bef5660b8a831` |

Read-only verification completed by this closure worker:

- `git rev-parse HEAD origin/main`: both `69d5bcc37502cd4bb464a3bf9ac0c53b1624b804`. No fetch or remote-current claim.
- `git diff --check --` followed by the four exact files above: exit 0, no findings.
- Direct scans of the four package files: zero trailing-whitespace or Cyrillic-prose findings. Added C# lines: zero `var` usages. Final work-log whitespace, English-prose and Markdown-fence checks also completed without findings.
- Final source/test diff readback and SHA-256 capture: complete. No production/test source changed during this closure pass.

## Root-owned executable acceptance

The root reports an ignored, verification-only installation of official Mirror v96.11.3 at commit `70969f5e29a1f5c6d63d4cd402329c34a02065f0`. Its import/compile and Unity EditMode run are in progress at this handoff. This worker did not install or modify Mirror, project settings, packages, scenes or metadata. No Mirror runner pass has been supplied to this closure pass.

Use the root's installed Unity executable with `-batchmode -projectPath D:/Git/CoreAI -runTests -testPlatform EditMode -assemblyNames CoreAI.Net.Mirror.Tests -testResults <fresh-root-owned-path>`. For an initial focused runner pass, the exact fixture filter is `CoreAI.Net.Mirror.Tests.MirrorKickEditModeTests;CoreAI.Net.Mirror.Tests.MirrorBridgeRulesEditModeTests`; require the 17 new `NET001_` cases to appear, not only a successful launch or exit code. Then require the complete `CoreAI.Net.Mirror.Tests` assembly to run with a positive test count and zero failures. Compile failure, zero tests or missing conditional assembly is an incomplete gate.

The root's portable core receipt (2,850 passed / 0 failed, as supplied at delegation) and skipped optional source transcriptions do not execute Mirror. No build or test was run by this closure worker. Actual Unity compilation/tests, controlled failing-before evidence, player/PlayMode, sockets/two-process transport, WebGL/browser/device, IL2CPP, load/performance, clean console and publication remain outside this worker's proof. Keep those gates distinct in the root's final acceptance.

Final disposition: NET001 is source-final with regressions and README complete. The independent source readback found no additional confirmed P1/P2 in scope. Root runtime acceptance and release/push remain pending; no host-mode, world-replication, protocol-negotiation or room-capacity feature is claimed.
