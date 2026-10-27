# Gateway Client repair closure review

Status: targeted static closure review complete. GW-001, GW-002, GW-003 and GW-005 are repaired in source with meaningful authored regressions. GW-004 remains open on three concrete lifecycle paths below. No final revision runtime pass is claimed; staging and original REVIEW remain unchanged.

## Revision and review scope

- Final manifest SHA-256: `1BA9BFAE398C18C19F12DE33BF7E9A19907D7AB56B9CD7621FF928DC01E2730C`, matching root's pinned revision.
- All 29 candidate hashes match the manifest. All eleven recorded live-original baseline hashes still match: zero candidate mismatches and zero baseline drift. Sixteen candidate hashes differ from their `preRepairSha256`; the targeted coverage below names all sixteen. The other thirteen retain the prior review's source coverage rather than being re-audited.
- Goal: close the five accepted findings through actual current functions and authored regressions, especially source ownership, retirement publication, callbacks outside all registry locks and admitted-request safety. This is not a new policy/authentication/backend design review.
- Before delegation: this is a leaf read-only review; only this report is writable. No source/test/manifest edit, extra agent, compile/test, Unity/MCP, provider/network call or Git mutation was run. Root's live FullAccess Play session was not touched.
- Before accepting: full changed metadata/cleanup functions and all affected ownership caller paths were read. Root's provisional 86 portable passes and staged Source3 zero-error compile precede this final revision and are not its acceptance. No test execution is inferred from authored case names.

All source/test paths below are relative to `D:/Git/CoreAI/artifacts/server-gateway-20261026/staging/`.

## Closure matrix

| Finding | Source adjudication | Evidence and required final proof |
| --- | --- | --- |
| GW-001 provider ID precedence/snapshots | Closed in source; execution pending. | `GatewayRequestContext.FromMessages` leaves absent IDs empty; `Snapshot` copies host request/profile and takes explicit IDs before ambient IDs. `GatewayAuthorizationSettings.Begin` captures endpoint/host providers once, deduplicates the same instance, and selects explicit/ambient IDs, provider IDs, then GUIDs. `GatewayChatClient.BindInvocation` writes selected values only into that invocation-owned copy before correlation/preflight. `ProviderIds_DefaultFactorySnapshotsSharedProviderOnce`, `ProviderIds_AuthRetryAndReusedContextRetainOnlyInvocationSnapshot`, and `ExplicitAndAmbientIds_WinOverProviderFallback` assert actual wire IDs, both modes, retry/later-call rotation, provider read counts and untouched reusable host objects. Rerun these final cases; the old 54-case receipt was insufficient. |
| GW-002 unspecified denial reason | Closed in source; execution pending. | `GatewayEntitlementPolicy.cs:39` retains the original reason rather than supplying `quota_exceeded`. The existing permanent-denial classifier handles unspecified/whitespace reasons without inventing a quota condition. `ActualServiceDenial_PreservesOriginalReasonAndCategory` uses the actual service -> middleware -> wire path in both modes, covers bare/empty/whitespace reasons plus actual quota, and asserts exact ProviderErrorBody and zero completion sends. |
| GW-003 approved/wire model match | Closed in source; execution pending. | `GatewayAuthorizationSettings.cs:92-100` captures the configured trimmed model in the invocation frame; its `Model` property reads that frame. `BindInvocation` gives policy that same configured value. Raw MEAI wire uses these settings, including after a held preflight. `PolicyModel_MatchesConfiguredWireForDefaultAndExplicitContexts`, `PolicyModel_DeniedConfiguredModelNeverSendsDifferentOptionsModel`, `PolicyModel_BackendSelectedStaysUnknownDespiteChatOptions`, and `HeldPreflight_ModelMutationCannotChangeApprovedWireInvocation` cover null/different options, custom/default context, denial, omitted model, A-to-B settings mutation and unchanged host metadata. |
| GW-004 owned leaf retirement | Partially repaired; not closed. | Explicit internal ownership now flows Gateway -> Meai -> ServerManaged and across the owning fallback chain. Ordinary primary/profile/runtime retirement closes owned leaves once and keeps borrowed resources untouched. New Source tests cover these normal paths and admitted buffered/stream cancellation. However deactivation still invokes cleanup under an outer registry lock (FU-001), an unpublished bootstrap registry can leak its owned primary (FU-002), and a late/unpublished activation result is never retired (FU-003). These are integration blockers for the ownership repair. |
| GW-005 throwing-cancellation cleanup | Closed in source; execution pending. | `HttpClientGatewayTransport.cs:79-87` uses nested finally blocks so owned HttpClient and lifetime CTS cleanup runs after cancellation callback failure; its disposal latch remains idempotent. `ThrowingCancellation_StillCleansOwnedClientAndPreservesBorrowedClient` covers both ownership modes with a pending fake handler, one throwing callback, exact dispose counts, repeated Dispose, rejection of new transport sends, continued borrowed-client use and observed late completion. |

## Remaining concrete GW-004 defects

### GW-FU-001 [P2] Defer owned retirement past the outer endpoint-state lock

**Anchor:** `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/LlmClientRegistry.cs:910-945`, calling newly added owned cleanup at `:1437`.

**Trigger:** an active runtime endpoint owns a Gateway leaf. Call `SetEndpointActiveAsync(id, false)`. The retiring owned client's Dispose/cancellation callback performs cleanup by waiting for a worker that reads registry endpoints/state. Such callbacks are a supported replaceable ownership boundary; no game authentication/session policy is needed.

**Causal path/actual result:** SetEndpointActiveAsync takes `_gate` at line 910 and calls `RequestOwnedHostRelease` at lines 944-945 before leaving that lock. The helper takes/releases its own reentrant `_gate`, then invokes `RetireOwnedGatewayClient(runtime.Client)` at line 1437. Its outer caller still holds `_gate`, so a callback waiting for another thread to acquire the registry cannot finish. This can deadlock an ordinary endpoint deactivation. Moving cleanup after the helper's own lock did not put it outside every registry lock.

**Fix invariant and exact regression:** publish the generation/retirement latch and HostReleaseTask atomically under the registry lock, then execute owned callbacks after the outermost mutation lock is released. Do not simply move publication outside the lock: the existing comment explicitly protects deactivation versus reactivation. Extend the real `RuntimeEndpointRetirement_DisposesOnlyExplicitGatewayOwnership("deactivate")` path with a bounded Dispose/cancellation callback that starts a worker registry query and records whether it completed. Assert outside the callback that the query completed before callback return, retirement completed, the old leaf closed once, no stale generation was admitted and a replacement remains usable. Cover deactivate plus a pending replacement; retain admitted-request and borrowed-resource controls.

**Correction to preliminary chat hypothesis:** the synchronous-activation publication epilogue is already guarded by `Monitor.IsEntered(_gate)` and `await Task.Yield()` at lines 1337-1343. That suspected inline epilogue path was withdrawn after full-function inspection. The direct deactivation path above is the confirmed finding and does not depend on that hypothesis.

### GW-FU-002 [P2] Dispose an owned primary when bootstrap fails before publishing its registry

**Anchor:** `Assets/CoreAiUnity/Runtime/Source/Composition/LlmPipelineInstaller.cs:62-103`, particularly `:100-102`.

**Trigger:** the real container registration factory successfully builds an owned primary Gateway chain, installs it into its local registry, then ApplyManifest fails while constructing a later invalid/unavailable custom profile binding. For example a later Gateway profile has invalid endpoint settings or its replaceable binding throws. Configuration failure must not retain newly constructed owned state.

**Expected/actual and cause:** the unpublished registry must release the primary it already owns. The registration factory calls `reg.SetLegacyFallback(primaryClient)` then `reg.ApplyManifest(routingManifest)` with no failure cleanup. ApplyRouteTable's new catch retires newly staged profile clients, but not that registry's installed primary. The factory throws before returning the registry, so the container has no successfully registered instance to dispose; initialization retry can create another linked lifetime without closing the first. The new hot-swap path has an explicit installation-failure cleanup catch, while bootstrap lacks one.

**Fix/test:** wrap construction/installation in an ownership-safe failure boundary and dispose the unpublished registry/new owned primary outside locks if it cannot be returned. Preserve borrowed legacy semantics and the original configuration exception. Through the real pipeline registration/resolution, use a counting binding where primary and an early profile succeed and a later profile throws. Assert all newly owned leaves close exactly once, borrowed transports/providers remain untouched, the original failure propagates, and a corrected resolution retry creates a usable fresh chain without retaining the first registration. Existing Source tests exercise successful registration/builders, not this post-primary bootstrap failure.

### GW-FU-003 [P2] Retire a produced owned activation result that is never published

**Anchor:** `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/LlmClientRegistry.cs:1349-1407`, with cleanup at `:1437,:1469`.

**Trigger:** a controlled endpoint factory produces an owned Gateway activation result as deactivation/removal/replacement wins publication. The result can complete just after cancellation; real completion/cancellation races are valid even for cooperative factories. Use an owned HTTP-style client with `ReleaseOwnedHostAsync == null` so client lifetime is not accidentally covered by a native-host delegate.

**Causal path/actual result:** ActivateRuntimeAsync stores `activation.ReleaseOwnedHostAsync` but assigns `activation.Client` into `runtime.Client` only in the success/publication branch at line 1390. The unpublished/inactive branches at lines 1357-1384 set `releaseActivation` without retaining or disposing that local produced client. RequestOwnedHostRelease and ReleaseOwnedHostAfterDrain retire `runtime.Client`, which is still the old/stub value, so the actual owned result never receives Dispose. Registry scope disposal also collects runtime.Client rather than the lost activation.Client. Its linked host registration therefore survives a failed activation publication.

**Fix/test:** retain ownership of the local produced result until transfer into the published runtime succeeds; otherwise retire that exact owned result outside registry locks. Do not infer ownership for ordinary borrowed factory results. Add a gated factory test: start activation, deactivate/remove or supersede before releasing the result, release an owned result, await both activation and retirement, and assert the produced leaf closes exactly once with borrowed resources at zero disposals. Reopen/activate a new generation and assert it stays usable. The current runtime retirement test awaits Ready before retiring and thus cannot catch this case.

## Targeted coverage and safeguards

The sixteen changed candidates were inspected at their full affected functions and call paths; eleven baseline comparisons and all 29 hashes were rechecked. This targeted pass does not claim a second exhaustive audit of unrelated HTTP/SSE/parser code already covered in REVIEW.

| Changed candidate | Read scope |
| --- | --- |
| `Assets/CoreAI/Runtime/Core/Features/Llm/Gateway/GatewayAuthorizationSettings.cs` | Full 177 lines: invocation frame, provider capture/dedup/ID precedence, model snapshot and restoration. |
| `Assets/CoreAI/Runtime/Core/Features/Llm/Gateway/GatewayRequestContext.cs` | Full 95 lines: absent defaults and invocation-owned request/profile copy. |
| `Assets/CoreAI/Runtime/Core/Features/Llm/Gateway/GatewayChatClient.cs` | Buffered/streamed admission through snapshot/bind/correlation; complete context/binding helpers and existing denial/disposal/telemetry joins. |
| `Assets/CoreAI/Runtime/Core/Features/Llm/Gateway/GatewayEntitlementPolicy.cs` | Full 46 lines; exact unspecified reason preservation and generic permanent middleware projection. |
| `Assets/CoreAI/Runtime/Core/Features/Llm/Gateway/HttpClientGatewayTransport.cs` | Full 91 lines; request cancellation and nested owned/borrowed cleanup. |
| `Assets/CoreAI/Runtime/Core/Features/Llm/Gateway/GatewayServiceClient.cs` | Validated nonnull response-body local/parse change and unchanged status/denial projection. |
| `Assets/CoreAI/Tests/EditMode/GatewayClientEditModeTests.cs` | All new ID/model/denial test blocks and associated mutable provider, held policy, service/wire/invocation helpers; prior cases retained as complementary coverage. |
| `Assets/CoreAI/Tests/EditMode/GatewayServiceClientEditModeTests.cs` | Full new throwing-cancellation ownership case/handler and retained cancellation/body ownership controls. |
| `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/Gateway/UnityGatewayAuthorizationAdapters.cs` | Full 181 lines: ownership capability/chain, cancellation, idempotent disposal, scope binding and host context copy. |
| `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/MeaiLlmClient.cs` | Complete old/internal constructors, explicit factory ownership, retired admission and disposal; original borrowed semantics retained. |
| `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/ServerManagedLlmClient.cs` | Complete old/internal constructors, owned flag/disposal, task/stream/header admission and forwarding. |
| `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/LlmClientRegistry.cs` | Complete Dispose, SetLegacyFallback, ApplyRouteTable, RetireOwnedGatewayClient, AddOrUpdate/SetActive/remove callers, activation publication, RequestOwnedHostRelease and drain/cleanup paths with request-lease guards. |
| `Assets/CoreAiUnity/Runtime/Source/Composition/LlmPipelineInstaller.cs` | Registry registration factory and owning primary/fallback construction/failure cleanup. |
| `Assets/CoreAiUnity/Runtime/Source/Api/CoreAiBackend.cs` | Complete modified hot-swap install/failure-cleanup path. |
| `Assets/CoreAiUnity/Tests/EditMode/GatewayClientCompositionEditModeTests.cs` | New primary/fallback retirement, admitted task/stream cancellation, manifest replacement/disable/scope cleanup, runtime actions and legacy borrowed cases; full ownership/factory/leaf/resource fakes. Missing callback-lock and unpublished-result controls are explicit findings above. |
| `Docs/CoreAI/GATEWAY_CLIENT.md` | Updated ID/model snapshots, unspecified denial, transferred versus borrowed ownership and retirement claims checked against implementation. |

Positive ownership findings: cleanup of a retired primary/profile is normally outside the respective helper's own lock; manifest failed-build clients are collected and retired in an outer finally; owning fallback wrapper cancels admitted calls before disposing its primary leaf; stale owned calls refuse; Meai legacy public constructors carry no owned resource; generic borrowed IDisposable is not treated as owned. RequestLease still checks retirement/generation under the registry gate before admission. These hold locally but do not close FU-001 through FU-003.

## Execution and handoff

Root's final rerun must discover the new Core ID/model/denial/cancellation cases and the Source ownership cases. Exact fixtures are `CoreAI.Tests.EditMode.GatewayClientEditModeTests`, `CoreAI.Tests.EditMode.GatewayServiceClientEditModeTests` and `CoreAI.Tests.EditMode.GatewayClientCompositionEditModeTests`. Add the three concrete lifecycle controls above before treating GW-004 closed. Preserve actual named-case/TRX or Unity XML accounting and borrowed resource assertions. Final revision on/off Source compile, real Unity import/DI/hot-swap/Play Mode, player/WebGL/CORS/build-guard and live gateway behavior remain separate gates.

CHANGED: this report only; original REVIEW, all staged/live source/tests, manifest and Editor session are untouched.
VERIFY OUTPUT: pinned manifest hash matches; 29/29 candidates match; 11/11 live baselines match; sixteen repair-changed paths reviewed. Direct report check found sixteen coverage rows, three finding headings, zero trailing-whitespace and zero Cyrillic lines; scoped diff check exited 0 with direct content checks covering the untracked report. Four original findings are source-closed; GW-004 remains open on three concrete P2 lifecycle defects. No test/build/runtime result was produced by this auditor.
NOT DONE: fixes, new regression authoring/execution and final root verification/integration. Provisional 86 Passed and Source3 zero-error receipts do not establish this final manifest's execution.
CONCERNS: keep integration blocked on the three bounded owned-lifecycle repairs; do not reopen unrelated policy or platform scope. The author can implement them within the current staged Source/test inventory.
QUESTIONS: none block this completed targeted review. Await root before any fixes.
