# Mirror binding-generation follow-up

Status: implementation and static review complete; C# edits frozen for root verification. Implements the two root-accepted findings from frozen report 35. No compilation or Unity test outcome is claimed by this worker.

Scope: `MirrorNetworkBridge.cs`, `CoreAiMirrorSessionHost.cs`, `MirrorSessionHostEditModeTests.cs`, `MirrorBridgeRulesEditModeTests.cs` and this report only. Public binding/session override APIs and the legacy integer release event remain compatible.

Goal: identify each actual binding by an internal monotonic generation, capture that generation before disconnect callbacks, preserve replacement sessions even when peer values repeat, and deliver captured release subscribers despite reentrant disposal. Host connection entries retain context and generation together. No caller session uniqueness or new authentication policy is introduced.

No Git mutation, build/test execution, Unity/MCP, network/provider calls, metadata changes, shared trackers or additional agents are authorized for this worker.

## Design and invariants

- Existing peer/session strings remain host data with their existing override semantics. They are not treated as unique binding tokens, rewritten, validated more narrowly or compared with the admission context to decide cleanup ownership.
- Each bridge binding publication receives a checked, monotonically increasing `long` generation. Its current generation is removed with the peer mapping; disposal clears current mappings without resetting the counter.
- The host's one connection dictionary now stores a private `ConnectionSession` value containing both the admitted `ActorContext` and captured binding generation. No parallel host generation map or global registry was added.
- The active-disconnect marker now retains the generation captured before public peer callbacks. Its lookup remains available to later subscribers even if an earlier subscriber disposes the bridge; the existing `finally` removes it when that notification unwinds. This is bounded active callback state, distinct from the cleared binding map.
- Public `BindConnection` overloads, `Admit` signature, `PeerDisconnected` event order/signature and legacy `BindingReleased(int)` event remain intact. The unshipped internal companion now carries `(connectionId, capturedPeer, bindingGeneration)`.

## CHANGED

### Runtime

- `MirrorNetworkBridge.cs`: adds current binding generations and a monotonic counter. `NotifyDisconnected` captures the departing generation before callbacks, exposes it internally to the host for the active notification, and releases a remaining peer only if that exact generation is still current. Identical actor/session/connection values no longer make a replacement look like the departing binding.
- Both direct and delayed release paths use captured legacy/companion delegate lists. A legacy callback can dispose the bridge or replace its live event fields without erasing the already captured companion delivery. Individual subscriber exceptions still enter the existing contained diagnostic path.
- Direct release defers notification only when the active disconnect captured that same generation. If a replacement is itself released during the old callback, it receives its own immediate release notification; the old notification remains delayed for its own captured generation.
- `CoreAiMirrorSessionHost.cs`: compares binding generations for both `OnPeerDisconnected` and the companion release handler. An earlier public peer listener can release/admit a new same-id/same-actor session before the host subscriber runs without the old callback retiring that replacement.
- Host release captures its connection record and does not unregister a different binding published during a world callback. Admission retains its existing connect-before-publication sequence, but verifies that the captured generation still exists before publishing the host record after the connect callback returns. A callback-retired binding is not reintroduced as a live host session.
- Existing actor carry-over, failure containment, pending-request completion and diagnostic helper bodies remain in place. The generic `InvokeSubscribers<T>` declaration and implementation remain unchanged.

### Real-fixture regressions

Added nine host cases and four bridge cases, using the existing fixtures and actual bridge/host paths:

| Fixture method | Cases | Protected behavior |
| --- | ---: | --- |
| `WorldRelease_ClearsTheAdmittedGeneration_RegardlessOfSessionOverride` | 3 | Null, empty and distinct explicit overrides all release host session/identity; the id admits another actor afterward; no duplicate world disconnect. |
| `WorldRelease_LegacySubscriberDisposesBridge_StillForgetsHostSession` | 1 | A disposing legacy subscriber does not erase the host's captured release notification; repeat cleanup is harmless. |
| `Admission_ConnectCallbackRetiresBinding_DoesNotPublishTheRetiredSession` | 1 | A connect callback that releases its binding cannot leave a newly published stale host entry or identity. |
| `PeerDisconnect_ReusedOverrideReplacementSurvivesBothListenerOrders` | 4 | Fresh admitted contexts with identical empty/reused peer overrides survive both earlier and later public listener order; routing and replacement identity remain live, then its later disconnect occurs exactly once. |
| `NET001_BindingRelease_CapturedCompanionSurvivesLegacyDisposalAndFailures` | 1 | Both captured event lists complete despite disposal and separate legacy/companion failures; peer and generation metadata are retained and diagnostics remain observable. |
| `NET001_Disconnect_IdenticalPeerReplacementKeepsItsGenerationAndRequests` | 2 | Replacement bindings with exactly reused peer values retain their fresh generation; old requests fail, new requests complete, and later cleanup releases the replacement. |
| `NET001_Disconnect_ReentrantReplacementReleaseNotifiesBothGenerations` | 1 | A replacement's own world release during an older active disconnect notifies its own generation immediately and the old generation afterward. |

The existing same-id replacement regression from the initial repair, different-id reconnect/actor carry-over cases, normal disconnect/kick/refusal tests and NET001 callback/completion cases remain present. Host setup was factored into a private helper with the same recorded connect/disconnect callbacks, allowing subscription order to vary in the new regression. The admission helper gained an optional context-session argument; its existing callers retain their original data.

## VERIFY OUTPUT

Static only. Read the actual runtime/fixture diffs and affected bind, notify, direct release, disposal, host admission/release and subscriber paths. A source-body comparison against checkout HEAD reported:

```text
MirrorSessionHostEditModeTests.cs: preserved baseline public method bodies=16
MirrorBridgeRulesEditModeTests.cs: preserved baseline public method bodies=46
MirrorNetworkBridge.cs: explicit local types retained
CoreAiMirrorSessionHost.cs: explicit local types retained
MirrorSessionHostEditModeTests.cs: explicit local types retained
MirrorBridgeRulesEditModeTests.cs: explicit local types retained
Static preservation only; no builds, tests or Unity execution
```

The host comparison excludes `CreateHost`, whose existing callbacks were moved without behavior changes into `CreateSessionHost`. The new initial-repair same-id test is also preserved by direct diff review; it is absent from HEAD and therefore not counted by the baseline comparison above.

All `BindingReleasedWithPeer` declarations, subscriptions and calls were searched: runtime and added fixture subscribers use the three-argument companion. `SamePeer` remains used for its other existing value-based protocol checks, while disconnect cleanup ownership uses the opaque generation.

## Root verification filters

Run the real Unity EditMode selection for these exact fully qualified fixtures, then broaden to the full Mirror selection if they pass:

```text
CoreAI.Net.Mirror.Tests.MirrorSessionHostEditModeTests
CoreAI.Net.Mirror.Tests.MirrorBridgeRulesEditModeTests
```

The new cases have no timing waits, provider calls or socket setup. They protect deterministic lifecycle behavior; they do not establish real transport behavior.

## NOT DONE

- Compilation, targeted/full Unity EditMode, PlayMode, live Mirror/network, player/IL2CPP/WebGL/browser/device, load/capacity and performance gates remain unrun by this worker. Root owns them and final acceptance.
- No Git mutation, build/test command, Unity/MCP, provider/network call, metadata/asset/scene/prefab edit, additional agent or shared tracker change occurred. Frozen report 35 and prior repair reports remain untouched.

## CONCERNS

- New source and regressions have only static review until root executes the filters above. Existing previously passing counts are not claimed for this revision.
- Generation allocation and callback ownership follow the bridge's existing in-process synchronous dictionary model; this repair does not introduce a new thread-safety or transport-ordering guarantee.
- The active-disconnect generation entry intentionally survives reentrant bridge disposal only until the notification's existing `finally`. It is not a retained connection or reusable identity token.

## QUESTIONS

None. The implementation follows the root-approved generation design without changing the caller's session override or authentication policy.
