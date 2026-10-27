# Gateway Client staged implementation

Status: initial implementation and accepted review repairs authored; final staging handoff ready, 2026-10-26. This worker wrote only artifacts/server-gateway-20261026/staging/** and this report. The read-only ASSESSMENT.md remains frozen. No live source, RedoSchool source, Git state, package version, asset or metadata was changed.

## Implemented scope

The staged feature runs in CoreAI.Core through portable MEAI.IChatClient middleware with eleven BCL-only gateway files. It reuses the actual MeaiOpenAiChatClient/IOpenAiHttpTransport HTTP, SSE, image and native tool protocol. Optional v1 entitlement/usage services are disabled by default. A real policy consumer runs before buffered/streaming dispatch, and a real usage consumer reports provider-measured usage once per raw invocation/roundtrip. SmartToolCallingChatClient remains outside the gateway for existing buffered tool execution. Portable streaming forwards native calls; no streamed tool-loop parity or backend server was added.

GatewayAuthorizationSettings implements the root-approved optional IOpenAiAuthContextSettings. Both authorization fallback and identity metadata honor the scoped provider; explicit null suppresses legacy global fallback. Custom/correlation/idempotency metadata is captured once per logical raw invocation; auth can rotate on one safe raw auth replay. Actual materialized messages and explicit correlation context feed GatewayRequestContext. No identity is inferred from prompts.

Valid preflight denials map to existing non-retryable categories. Unknown or transient-looking denial reasons use PermanentProviderError; quota/auth/model reasons retain suitable permanent classifications. Original reason_code/message/retry_after_seconds remain in existing ProviderErrorBody. Unavailable/invalid preflight follows configurable Allow/Deny, default Allow. Deny availability uses BackendUnavailable rather than pretending a business denial. Cancellation before/during preflight propagates and prevents dispatch. The optional service policy uses an existing LlmClientException for unavailable Deny mode, avoiding a new exception family.

Optional telemetry uses nonblocking finite concurrency admission and an awaited configurable deadline. Failure/timeout/saturation does not replace primary success, and logs expose no exception/body/token text. A bounded cleanup observer retains its slot until a non-cooperative injected sink finishes, so repeated timeouts cannot create unbounded work. There is no queue/background scheduler/usage retry. Host lifetime cancellation, early stream disposal and client disposal are covered by authored tests.

Unity has a thin main-thread UnityWebRequest side-call transport with bounded download storage, redirect suppression and player-loop deadline; scoped binding/authorization adapters; and an opt-in installer. The same binding reaches primary, manifest-profile and CoreAiBackend hot-swap construction. Existing constructors/static auth fallback remain; the new path does not stack a second Source refresh or usage event forwarder. The binding marks itself disposed before cancellation callbacks and owns a lifetime CTS, without retaining an ever-growing client list or disposing caller-owned providers/transports.

Root's final option decision supersedes the assessment's narrower loopback recommendation: new AllowInsecureHttp defaults false and explicitly permits a host-selected HTTP gateway, including LAN/private hosts. HTTPS remains default, URIs reject embedded credentials/query/fragment, no credential/destination value is echoed in validation failures, and the host owns HTTP confidentiality. Old staging-only AllowInsecureLoopback and CLI spelling were removed without aliases.

## Provenance and baselines

Source behavior: owner's Neoxider CoreAiPro 0.1.0, RedoSchool HEAD f69a0e38624cb91be4b7a50bb0908163f6777622, package tree 52ba740d6de5df1830594cb2d4bd0642333e9843. No student/session/lesson/account/login implementation, config asset or secret was copied. The feature document retains the original proprietary notice and source provenance; no silent relicensing claim or new publication approval gate was introduced.

staging/staging-manifest.json records eleven exact live-original baseline SHA256 values, final candidate SHA256 values, current live SHA256 and whether each still matches its baseline. At the final static review pass all eleven live originals matched (drift count zero). Root must recheck before integration and merge reviewed deltas if a baseline changes; never overwrite a newer accepted fix with a candidate copy.

## Static verification performed

- Read every production candidate delta against its original; focused edits only in the six copied production files. Public Source client/MEAI factory/registry constructor compatibility is retained through overloads or unchanged optional construction paths.
- Both new portable test fixtures are explicitly included in the candidate tools/portable/Tests/CoreAI.Portable.Tests.csproj. Core source remains under its existing source glob. Staging is a mirror of intended repository-relative paths, not a standalone build checkout.
- Eleven new portable source files contain zero UnityEngine/Cysharp/UniTask/VContainer/MessagePipe imports and no var, Task.Run, ThreadPool, ContinueWith, ConfigureAwait or old option-name matches.
- No .meta/asmdef/scene/prefab files were authored. Candidate text has no trailing whitespace. New authored prose/code contains no Cyrillic. The MeaiLlmClient candidate retains the live baseline's existing quoted observed Russian output at its shifted line 642; that evidence was not rewritten.
- Read-only SHA/status/file/regex/XML-text inspection only. No compile, build, test execution, Unity/MCP operation, network/provider request, auth warmup, remote query or Git mutation was run by this worker.

Authored fixtures cover actual fake-wire JSON/SSE/UTF-8 parsing, model omission, scoped identities with poisoned globals, dynamic bearer/one refresh, stable custom headers, native stream/tool side-effect replay suppression, early stream disposal, measured usage footer and extra counters, bounded non-cooperative telemetry, primary-success preservation, host/client cancellation, real buffered C# tool composition, image wire reuse, optional v1 HTTP/status/body bounds/ownership, AllowInsecureHttp LAN/default refusal, denial/availability/cancellation, actual Source fallback secondary dispatch zero, shared hot-swap builder, profile selection and scope teardown. These are authored cases, not executed passes.

## Root verification and integration still required

1. Review candidate source/test/docs and current baseline hashes; integrate deltas only after root's existing fixes are ready, or create an isolated overlay checkout for validation.
2. Compile actual CoreAI.Core netstandard2.1 and .NET console sample; run/discover named GatewayClientEditModeTests and GatewayServiceClientEditModeTests through the candidate harness and record TRX UnitTestResult outcomes. Run affected existing HTTP/SSE/provider/retry suites. No zero-test or portable-stub pass counts as acceptance.
3. Compile/import in Unity with COREAI_LLM on/off and run GatewayClientCompositionEditModeTests plus affected tests. The shared rebuild/profile tests exercise the actual constructors/builder/fallback class; the static CoreAiBackend facade and live UI hot-swap still need runtime verification.
4. Verify player/WebGL streaming/buffered modes, CORS/custom headers, main-thread calls, cancellation, auth/logout, no-domain-reload lifecycle and existing secret/build guards. Live backend/provider compatibility, IL2CPP and browser behavior remain unverified.
5. Root owns any needed test fixes, .meta import, package changelogs/PLAN/TODO/index updates, version/release gates and Git review/commit/push. No co-authorship metadata was added.

## Exact staged candidate inventory

All paths below are relative to D:/Git/CoreAI/artifacts/server-gateway-20261026/staging/. There are 29 intended repository files plus staging-manifest.json.

- Assets/CoreAI/Runtime/Core/Features/Llm/Gateway/GatewayAuthorizationSettings.cs
- Assets/CoreAI/Runtime/Core/Features/Llm/Gateway/GatewayChatClient.cs
- Assets/CoreAI/Runtime/Core/Features/Llm/Gateway/GatewayClientFactory.cs
- Assets/CoreAI/Runtime/Core/Features/Llm/Gateway/GatewayClientOptions.cs
- Assets/CoreAI/Runtime/Core/Features/Llm/Gateway/GatewayEntitlementPolicy.cs
- Assets/CoreAI/Runtime/Core/Features/Llm/Gateway/GatewayRequestContext.cs
- Assets/CoreAI/Runtime/Core/Features/Llm/Gateway/GatewayServiceClient.cs
- Assets/CoreAI/Runtime/Core/Features/Llm/Gateway/GatewayUsageSink.cs
- Assets/CoreAI/Runtime/Core/Features/Llm/Gateway/HttpClientGatewayTransport.cs
- Assets/CoreAI/Runtime/Core/Features/Llm/Gateway/IGatewayAuthRefresher.cs
- Assets/CoreAI/Runtime/Core/Features/Llm/Gateway/IGatewayHttpTransport.cs
- Assets/CoreAI/Runtime/Core/Features/Llm/MeaiOpenAiChatClient.cs
- Assets/CoreAI/Tests/EditMode/GatewayClientEditModeTests.cs
- Assets/CoreAI/Tests/EditMode/GatewayServiceClientEditModeTests.cs
- Assets/CoreAI/README.md
- Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/Gateway/UnityGatewayAuthorizationAdapters.cs
- Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/Gateway/UnityWebRequestGatewayTransport.cs
- Assets/CoreAiUnity/Runtime/Source/Composition/CoreAiGatewayClientInstaller.cs
- Assets/CoreAiUnity/Runtime/Source/Composition/LlmPipelineInstaller.cs
- Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/LlmClientRegistry.cs
- Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/MeaiLlmClient.cs
- Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/ServerManagedLlmClient.cs
- Assets/CoreAiUnity/Runtime/Source/Api/CoreAiBackend.cs
- Assets/CoreAiUnity/Tests/EditMode/GatewayClientCompositionEditModeTests.cs
- Assets/CoreAiUnity/README.md
- tools/portable/Tests/CoreAI.Portable.Tests.csproj
- examples/dotnet/Program.cs
- examples/dotnet/README.md
- Docs/CoreAI/GATEWAY_CLIENT.md

CHANGED: the 29 staged candidates, manifest and this report.
VERIFY OUTPUT: static inspection and hashes only; all executed-proof gates are NOT RUN.
NOT DONE: root integration, compile/test/runtime/platform acceptance, changelog/tracker/release/Git work.
CONCERNS: injected policy/refresher/transport cancellation remains an interface contract; non-cooperative telemetry is bounded/observed but cannot be forcibly killed. Retain provenance/notices and recheck baseline drift before merging.
QUESTIONS: none. All material API/lifecycle/HTTP/denial choices were resolved with root.


## Review repair milestones

Root accepted GW-001 through GW-005 from frozen REVIEW.md. Repair lease remains staging-only. Prior root receipts (54 portable passes, sample build/help and staged Source compilation) describe the pre-repair candidates; they are not proof of the new cross-product cases. Current work covers invocation-owned ID/model snapshots, owned gateway retirement, unspecified denial preservation and throwing-cancellation cleanup. No tests/builds/Unity/network/Git operations are authorized for this worker.

Repair milestone 1: authored GW-001/GW-002/GW-003/GW-005 production changes and new real factory/wire regressions for provider ID precedence/shared-provider snapshots/reused host contexts/auth retry, configured versus ChatOptions model in both modes with held preflight mutation, bare/blank quota-free service denials and throwing HTTP cancellation cleanup. Gateway invocation request/profile copies leave caller-owned metadata unchanged. Model and selected IDs are frozen in one AsyncLocal frame. Owned Unity chain plumbing is authored; actual registry retirement regressions are next. No compile/test was run.

Repair milestone 2: authored explicit owned-leaf plumbing through MeaiLlmClient and ServerManagedLlmClient, an owning chain across the existing fallback decorator, and registry primary/manifest/runtime retirement/scope disposal outside its locks. Legacy public constructors remain borrowed; only the internal ownership capability is retired. Added actual Source primary rebuild, optional fallback, admitted buffered/stream cancellation/stale handle, manifest replacement/disable, runtime deactivate/remove/replace, exact-once leaf and borrowed-resource controls. Creation/installation failures close newly created owned chains. Prior root 86 portable passes are provisional earlier-repair evidence; worker still ran no compile/test.


## Final review repair handoff

Status: repair authoring complete; same 29 candidate paths and zero new/unleased candidate files. REVIEW.md and ASSESSMENT.md remain frozen. Sixteen existing candidates changed in this repair wave; staging-manifest.json records each pre-repair candidate SHA and its final SHA.

| Accepted finding | Authored repair | Authored regression |
|---|---|---|
| GW-001 | Invocation-owned request/profile copies; explicit/ambient IDs, captured provider IDs, then generation; shared provider captured once; no selected/generated IDs retained on reused host objects. | Actual factory/wire buffered and streamed tests for provider/ambient/explicit precedence, shared provider reads, 401 retry snapshots, rotated later IDs and reused untouched context. |
| GW-002 | Bare/blank valid denial retains its original reason and generic permanent category; actual quota is typed only when reported. | Service -> policy -> actual middleware in both modes, zero sends, exact retained body/category and quota control. |
| GW-003 | Configured wire Model frozen in the same invocation frame, independent of ignored ChatOptions.ModelId; default and custom context copies normalized without host mutation. | Null/different options, default/custom context, allowed/denied model, backend-selected empty model and held-preflight A -> B mutation with actual wire capture. |
| GW-004 | Explicit owned-resource capability in scoped Meai/ServerManaged chains; owner retained across fallback; primary/profile/runtime retirement and scope disposal release owned leaves outside registry locks. In-flight owned work is cancelled, stale handles refused, borrowed legacy paths/resources untouched. | Real Source registry/primary rebuild with and without fallback, admitted buffered/stream cancellation, exact-once leaf closure, profile replacement/disable/scope teardown, runtime deactivate/remove/replace and borrowed legacy controls. |
| GW-005 | Nested finally guarantees owned HttpClient and lifetime CTS cleanup even if cancellation notification throws; idempotent repeated Dispose; borrowed client stays caller-owned. | Fake pending HttpMessageHandler registers a throwing callback; owned handler disposed once, borrowed client usable, late pending task explicitly observed. |

No new public aliases, exception family, provider/backend implementation, thread scheduler, package, scene/prefab/asmdef/.meta or live source edit was added. The side-response nullable warning was resolved by capturing the validated body into a nonnull local before parse; schema/body-limit validation remains unchanged. Source public legacy constructors remain available and borrowed; explicit new factory results alone transfer gateway ownership. Raw MEAI completion transport ownership was not changed.

Root evidence is separate from the final candidate revision: the provisional repaired-Core receipt artifacts/server-gateway-20261026/gateway-staged2-results/gateway-staged2.trx contains 86 UnitTestResult records, all Passed, SHA256 BE52186623A3DBD68E584EB582C32BFF2CA7DC97BC814A6E832DB765DE863932. This worker verified that saved XML only; it did not run the tests. Root also reported stagedSourcebuild3 against real Unity references with zero errors and prior sample build/offline help success. These receipts do not establish execution of the new Source retirement tests or actual Unity import/Play Mode/player/WebGL behavior. Final hashes and baseline checks are in the manifest, rather than being inferred from a preceding proof run.

Final worker verification: static source/test/doc inspection, XML harness inclusion (two portable fixtures), inventory equality (29), no forbidden portable dependencies/thread APIs, and eleven live baseline SHA comparisons. No build/test/Unity/MCP/network/provider/Git mutation by this worker. The candidate inventory above remains exact and complete.

Remaining for root: review/integrate the frozen deltas without overwriting accepted concurrent work; rerun final Core/source/test compile and focused fixtures, including Source retirement cases; actual Unity on/off import, DI/hot-swap/Play Mode, player/WebGL/CORS/build guards; package changelog/tracker/version/release/Git gates. No further implementation question blocks handoff.

## Remaining GW-004 follow-up repair milestones

Root accepted only GW-FU-001/GW-FU-002/GW-FU-003 from frozen REVIEW_FOLLOWUP.md. The other four closures stay unchanged. Source edits split release publication from execution, detach activation cancellation until after the outer mutation lock, capture the retired activation task/client for reactivation safety, dispose the unpublished bootstrap registry on initialization failure without replacing the original exception, and retain/dispose actual untransferred activation results outside the gate. The existing Monitor.IsEntered/Task.Yield activation epilogue guard is unchanged. Regression authoring follows; no live source or tests/builds/Unity were run.


## Final GW-004 follow-up handoff

Status: the three remaining concrete GW-004 paths are repaired in staging and have authored regressions. The four previously accepted closures are unchanged. Only these three existing candidates changed in this follow-up:

- Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/LlmClientRegistry.cs
- Assets/CoreAiUnity/Runtime/Source/Composition/LlmPipelineInstaller.cs
- Assets/CoreAiUnity/Tests/EditMode/GatewayClientCompositionEditModeTests.cs

GW-FU-001: release publication and execution are separate. The mutation lock publishes retirement/HostReleaseTask atomically and detaches cancellation sources; cancellation and owned cleanup run after the outer lock exits. Private release work captures the retired client/activation task so a later reactivation cannot make old release wait on the new generation. The existing Monitor.IsEntered/Task.Yield activation epilogue guard is untouched. Authored completed-factory and pending-replacement controls make an owned Dispose callback wait boundedly for a worker GetEndpoints query, assert completion outside the callback, stale-generation refusal, exact-once cleanup and a usable next activation.

GW-FU-002: the real registry-registration factory disposes its unpublished registry on initialization failure and preserves the original exception even if cleanup fails. An approved optional INTERNAL ILlmEndpointRegistryStore input on RegisterLlmPipelineForPlatform keeps its default File store unchanged while the regression uses an empty in-memory store. The real container-resolution test creates owned primary/early profile, throws at the later profile, uses throwing cleanup callbacks, verifies original-cause propagation and exact cleanup, then retries with a corrected fresh container. It does not restore or activate persisted endpoints, models or real network resources.

GW-FU-003: activation publication retains the actual returned client until transfer. Unpublished/inactive/cancelled/disposed-registry results retire that exact owned capability outside the lock rather than losing it behind runtime.Client's stub; borrowed results are unchanged. Controlled late-result tests cover deactivate/remove/replace with owned and borrowed results, null release delegate, usable next generation, and a separate asynchronous host-release callback control. All controlled awaits are bounded.

The complete inventory remains the same 29 repository candidates plus manifest; zero new candidate paths, assets, metadata, public policies/aliases, exception families or production background queues. The prior 86 portable passes and Source compilation receipts are pre-follow-up evidence only. This worker ran no builds/tests/Unity/MCP/Git/network/provider calls and changed no live source. Root owns final compilation and execution of these new Source lifecycle tests, integration and runtime/platform acceptance. Eleven live-baseline and final per-candidate hashes are refreshed in the manifest. No material design question remains.
