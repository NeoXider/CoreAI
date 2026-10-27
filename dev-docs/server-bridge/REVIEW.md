# Gateway Client staged feature review

Status: independent static review complete. Five actionable P2 findings; root owns adjudication, fixes and executable acceptance. Staging remains unchanged.

## Review contract and decisions

Before planning: the goal is a substantial review of all 29 staged Gateway Client candidates against the approved BCL Core plus opt-in Unity design. Required invariants are real policy/usage consumption, valid-denial versus unavailability semantics, accurate request/route metadata, scoped authorization with explicit-null global suppression, one safe raw auth retry, stable invocation headers, bounded telemetry/body/timeout behavior, cancellation and owned cleanup, unchanged legacy construction, standalone usability and explicit streamed native-tool limitations. Server login/accounts/students/billing, backend implementation, new registries/packages and streamed tool-execution parity are outside scope.

Before delegation: this is an exclusive read-only leaf; no additional agents are authorized. The sole writable path is this review. `ASSESSMENT.md`, `IMPLEMENTATION.md` and the staging manifest were read. All 29 candidates and all deltas of the eleven copied files were inspected. No live/staged source, tests, metadata, Git state, build, test runner, Unity/MCP, network or provider was changed or invoked.

Before accepting: findings require a valid public/composition trigger, changed causal path, observable wrong behavior, a meaningful regression and stated limits. Existing baseline files were compared read-only with the staged copies. Candidate SHA-256 values match all 29 manifest entries, and all eleven current live originals still match their recorded baselines: zero candidate mismatches and zero baseline drift. Baseline HEAD is `bc3fd74f0a889bfe1b1e7babc6bc02ed48a7c05e` in `D:/Git/CoreAI`, branch `demo`.

All finding paths below are relative to `D:/Git/CoreAI/artifacts/server-gateway-20261026/staging/`. Accepted by root during review: GW-001 provider-ID precedence and GW-004 owned-leaf retirement. Proposed source-confirmed findings awaiting root adjudication: GW-002 unspecified denial reason, GW-003 policy model context, and GW-005 throwing-cancellation cleanup. No executable reproduction was run by this auditor.

## Findings

### GW-001 [P2] Honor provider fallback IDs before generating invocation IDs

**Anchor:** `Assets/CoreAI/Runtime/Core/Features/Llm/Gateway/GatewayRequestContext.cs:30-31`, `GatewayChatClient.cs:220-223`, and `GatewayAuthorizationSettings.cs:90-93` in the same Gateway folder.

**Trigger:** construct the portable factory with an `IRequestHeaderProvider` exposing a nonempty `IdempotencyKey` and `RequestId`, either on endpoint settings or in the factory argument. Do not supply an explicit `LlmRequestContext` or explicit context-factory IDs. This is precisely the host fallback usage documented by the existing header-provider and ambient-context contracts.

**Expected/actual and cause:** the supplied provider IDs should be captured once and reach the completion wire, remaining stable over an auth replay. Instead, `FromMessages` generates GUIDs before the header snapshot exists, and `Context` also fills blank context-factory IDs. The snapshot's `First` then always prefers those generated IDs over provider IDs. `Correlation(context)` installs the generated values into `LlmRequestContext`, which the real MEAI header builder prioritizes. Provider fallback ID properties are therefore ineffective on the normal factory path; the endpoint sends random identifiers in place of the caller's deduplication/correlation values.

**Repair/regression:** explicit call/context IDs must win, followed by one invocation snapshot of provider IDs, followed by generated defaults. Keep selected IDs invocation-owned; `Context` currently mutates a caller-owned context-factory request, so retaining a generated/selected value there can also make a reused request suppress the next provider snapshot. Exercise the real factory/wire with nonempty endpoint/host provider IDs, a 401 then successful raw refresh, and another invocation after provider rotation. Assert exact wire IDs, one snapshot during retry, fresh provider values on the later invocation, and unchanged caller-owned reusable request IDs. Add the explicit-context precedence control. The existing `MutableHeaders` fake always returns empty IDs and misses this path.

Related snapshot control: if endpoint and host arguments reference the same provider instance, `SnapshotHeaderProvider.Begin` currently calls `GetHeaders` twice at lines 88-89. Deduplicate that instance and verify a stateful provider is sampled once, so fields of one logical request do not come from two consumed snapshots.

### GW-002 [P2] Preserve an unspecified denial reason instead of fabricating quota exhaustion

**Anchor:** `Assets/CoreAI/Runtime/Core/Features/Llm/Gateway/GatewayEntitlementPolicy.cs:36-41`, specifically line 39.

**Trigger:** the optional service returns a successful, valid `{"allowed":false}` response with no `reason_code`, or with a blank reason. The service correctly marks this as an available explicit denial.

**Expected/actual and cause:** the request must be denied with a generic permanent classification, preserving the server's unspecified reason. The adapter rewrites the blank reason to `quota_exceeded`; the middleware consequently surfaces `QuotaExceeded` and places that invented reason in `ProviderErrorBody`. The server never reported a quota condition. This contradicts the approved known-code-only classification and original reason preservation. No fallback bypass is claimed: the fabricated quota category is still permanent, but its business meaning and receipt are wrong.

**Regression:** drive the default service adapter plus actual Gateway middleware in buffered and streaming modes with bare/blank-reason denials. Assert zero completion sends, `PermanentProviderError`, retained empty/unspecified original reason and generic user-safe message; retain the named quota-denial control. `ValidDenial_NeverBecomesAllow` already uses a bare denial but checks only the policy boolean, so the typed middleware/body defect is unrepresented.

### GW-003 [P2] Give portable preflight the model selected by the actual wire client

**Anchor:** `Assets/CoreAI/Runtime/Core/Features/Llm/Gateway/GatewayRequestContext.cs:37-40` and the unamended default factory path at `GatewayClientFactory.cs:61-64`.

**Trigger:** use the portable factory with `settings.Model = "configured-model"`, enabled custom entitlement policy, and null chat options, or options with a different `ModelId`. A custom policy is allowed to inspect the supplied route/profile to decide whether a configured model is permitted.

**Expected/actual and cause:** preflight must describe the model that the reused client will send. The default context reports empty model for null options, or the unrelated `ChatOptions.ModelId`. Actual `MeaiOpenAiChatClient.AddRequestModel` reads only `_settings.Model` and writes `configured-model` at lines 213-219; it does not use `ChatOptions.ModelId` for that selection. Thus a model-sensitive policy evaluates different metadata from the ensuing wire request. The Unity adapter explicitly sets `context.Profile.Model = http.Model` at its line 72; the portable default lacks that correction.

**Regression:** supply a fake policy that records/denies the configured model and a real fake completion transport. Parameterize null options and a distinct options ModelId. Assert that recorded profile matches the actual configured wire model, denial prevents dispatch, and a permitted configured model sends the same model represented to policy. Preserve omitted-model server selection as empty/unknown rather than inventing a name. A host can supply a context factory today, but the default advertised portable composition must not silently report a different selected model.

### GW-004 [P2] Retire and dispose owned Gateway leaves through the Unity wrapper chain

**Anchor:** `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/Gateway/UnityGatewayAuthorizationAdapters.cs:57-74`, `.../MeaiLlmClient.cs:153-156`, and `.../ServerManagedLlmClient.cs:17,54-61`.

**Trigger:** one long-lived opted-in Unity scope repeatedly changes its primary server endpoint through the supported CoreAiBackend hot-swap builder, or replaces/disables server manifest profiles. Each construction creates a new GatewayChatClient linked to the same binding lifetime.

**Expected/actual and cause:** a retired owned leaf must release its own lifetime registration/resources without disposing caller-owned providers or transports. The new chain is `ServerManagedLlmClient -> MeaiLlmClient -> GatewayChatClient`; neither outer wrapper implements or forwards disposal. GatewayChatClient links a CTS to the binding token at Core `GatewayChatClient.cs:58`, and only its Dispose at lines 203-212 cancels/disposes that source and owned optional service. Registry fallback replacement at `LlmClientRegistry.cs:359-368`, manifest replacement/disable at lines 391-439, and registry Dispose do not close these new owned leaves. Linked cancellation registrations therefore accumulate until whole binding teardown. There need not be an explicit expanding client list for this retention to occur.

**Scope of claim:** retained linked CTS/registration state and missing owned-leaf disposal are established by the construction/retirement path. This is not a claim that every whole Gateway object or borrowed transport remains rooted, nor a request to dispose arbitrary borrowed clients supplied under old registry semantics. The raw MEAI adapter's Dispose is empty, preserving injected completion-transport ownership; that fact does not dispose the newly created Gateway lifetime.

**Regression/fix direction:** add explicit owned-leaf disposal plumbing through the newly constructed Unity path and registry retirement boundaries, preserving old constructor/borrowed ownership semantics and safe in-flight teardown. Use a counting owned leaf/binding with the real primary/profile replacement paths: old owned leaf is cancelled/disposed exactly once on hot swap, manifest replacement/disable and owning scope disposal; replacement remains usable; borrowed provider/transport disposals stay zero. Include an admitted request during retirement and stale-handle behavior per the chosen existing retirement contract. Do not solve the leak with an unbounded binding client list. Current composition tests count creates/dispatch and cancel at whole-scope teardown; they never assert retirement disposal.

### GW-005 [P2] Finish owned side-transport cleanup when a cancellation callback throws

**Anchor:** `Assets/CoreAI/Runtime/Core/Features/Llm/Gateway/HttpClientGatewayTransport.cs:79-84`.

**Trigger:** use the supported injected HttpClient constructor with `ownsClient: true`. Its fake/custom handler registers a callback on the request cancellation token which throws, while SendAsync is active. Dispose the transport. CancellationTokenSource.Cancel is allowed to surface callback failures as AggregateException.

**Expected/actual and cause:** owned HttpClient and lifetime CTS cleanup must run exactly once even if cancellation notification fails. The method latches `_disposed = 1`, calls `_lifetime.Cancel()` without a finally, and only then disposes the owned client/source. The AggregateException skips both disposals. A second Dispose returns at the latch and cannot recover cleanup. GatewayChatClient already guards its own cancellation callbacks; the newly added side transport omits equivalent owned-cleanup protection.

**Regression:** a fake HttpMessageHandler holds a bounded in-flight request, registers a throwing cancellation callback and counts Dispose. Verify owned handler/client cleanup exactly once regardless of whether Dispose surfaces or contains the callback exception, and ensure request cleanup is observed. Repeat borrowed-client ownership to prove it remains caller-owned/usable. Keep cancellation/response-body disposal tests. No network is needed. The current ownership test disposes an idle transport after a body-cap failure and has no throwing cancellation callback.

## Investigated and verification gaps

- Optional endpoints are off by default and actual preflight/sink consumers are present in both Gateway completion paths. Available denial is outside the raw auth-refresh catch and transient-looking decision codes are made permanent; Source scoped construction avoids the legacy second refresh. Existing fallback uses a transient whitelist, so the inspected valid-denial categories do not dispatch a fallback. The unspecified-reason classification in GW-002 remains a separate semantic defect.
- Scoped identity selection, including explicit null, overrides legacy identity metadata in both modified MEAI joins. Custom header values and retry IDs are captured within an invocation, with dynamic bearer on an eligible refresh. Ordinary interleaved clients have separate AsyncLocal snapshots. Metadata-provider field rotation during a retry is not called a host account/session bug; its desired snapshot contract should be stated and tested if stronger guarantees are claimed.
- Native streaming marks visible or non-text/native-call content committed and avoids auth replay after it. Buffered auth retry stays inside the existing outer SmartToolCalling loop, so it does not replay an entire executed tool turn. Actual byte-fragmented SSE, usage footer, early stream disposal and buffered tool composition tests were read. Full malformed/premature SSE and affected existing retry/provider suites still require the existing test lanes, not a parser rewrite.
- Telemetry admission has finite slots, no queue, a finite awaited deadline and a bounded observer retaining a non-cooperative task's slot. Failure is isolated from the primary response. Custom sink/policy/refresher/transport cooperation remains an interface contract; a synchronously blocking injected implementation cannot be forcibly interrupted without changing the approved WebGL model. No unbounded telemetry queue or compulsory side call was found. Cross-client churn disposal remains GW-004.
- Side responses are capped in bytes; default HttpClient redirects are disabled and injected clients explicitly require that configuration. The final-URI check happens after an injected redirect, so it is not a replacement for that documented host precondition. Unity uses bounded download storage, zero redirect limit and its existing main-thread await/abort helper. Cancellation/timeout versus completion races, callback exceptions and real UnityWebRequest/built-player behavior are not proved by portable tests.
- Service Retry-After currently parses delta seconds only; an HTTP-date value is dropped. Malformed optional integer fields can turn a response with a boolean `allowed` into unavailable via the broad catch, and malformed successful JSON loses already observed status/retry metadata in that catch. The precise optional-field validity contract needs root definition and tests before stronger denial/diagnostic preservation is claimed; these are not counted as additional findings here. Non-success service HTTP responses are explicitly documented as unavailable, so they are not reinterpreted as verified business denials by this review.
- Old public Source constructors/factory arities are preserved through forwarding overloads, while binding constructors are internal and full-arity where required. COREAI_LLM-off references were inspected: the small Unity binding interface remains available outside the feature guard; concrete adapters stay guarded. Actual on/off Unity compilation, real container disposal/registration order and hot-swap UI runtime remain root gates.
- The standalone sample retains the existing read-only skill/tool loop, no-network help/no-argument path, explicit HTTPS/HTTP opt-in, dynamic host token and optional omitted model. No account/auth stub, secret asset, backend implementation, new execution mode, eighth package, extra parser or claimed streamed auto-execution was added. Root later supplied sample compilation and offline help success; this auditor did not run them.
- Provenance and the original proprietary notice are retained in the feature guide; entry points link the feature. The owner already authorized migration/publication. No new publication permission gate or relicensing claim is inferred.

## Complete candidate coverage

All 29 manifest candidates were inspected: all 49 changed hunks across eleven copied baseline files, and all 18 new files in full (2,689 lines). Existing large copied files were reviewed at every changed hunk plus affected callers/contracts; untouched bodies are not claimed exhaustive. Ranges use read-only `git diff --no-index --unified=0 <live-baseline> <staged-candidate>`; baseline hash equality was independently checked before using that comparison.

| Candidate path | Read coverage |
| --- | --- |
| `Assets/CoreAI/Runtime/Core/Features/Llm/Gateway/GatewayAuthorizationSettings.cs` | New, full `1-151`; scoped auth, header snapshot/precedence, reserved fields and restoration. |
| `Assets/CoreAI/Runtime/Core/Features/Llm/Gateway/GatewayChatClient.cs` | New, full `1-421`; buffered/streamed admission, refresh safety, denial classification, usage slots/deadlines/observation and disposal. |
| `Assets/CoreAI/Runtime/Core/Features/Llm/Gateway/GatewayClientFactory.cs` | New, full `1-68`; optional service creation/ownership and raw reused client composition. |
| `Assets/CoreAI/Runtime/Core/Features/Llm/Gateway/GatewayClientOptions.cs` | New, full `1-78`; defaults/snapshot/bounds/HTTPS and explicit HTTP validation. |
| `Assets/CoreAI/Runtime/Core/Features/Llm/Gateway/GatewayEntitlementPolicy.cs` | New, full `1-46`; availability versus actual denial, cancellation and reason projection. |
| `Assets/CoreAI/Runtime/Core/Features/Llm/Gateway/GatewayRequestContext.cs` | New, full `1-45`; actual messages, metadata and default profile. |
| `Assets/CoreAI/Runtime/Core/Features/Llm/Gateway/GatewayServiceClient.cs` | New, full `1-175`; optional v1 URLs/methods/schema/status/body and retry data. |
| `Assets/CoreAI/Runtime/Core/Features/Llm/Gateway/GatewayUsageSink.cs` | New, full `1-29`; supported measured v1 projection. |
| `Assets/CoreAI/Runtime/Core/Features/Llm/Gateway/HttpClientGatewayTransport.cs` | New, full `1-88`; deadlines, body bounds, redirects, borrowed/owned clients and cleanup. |
| `Assets/CoreAI/Runtime/Core/Features/Llm/Gateway/IGatewayAuthRefresher.cs` | New, full `1-14`; cooperative host-owned refresh contract. |
| `Assets/CoreAI/Runtime/Core/Features/Llm/Gateway/IGatewayHttpTransport.cs` | New, full `1-37`; request/response/ownership/bound contracts. |
| `Assets/CoreAI/Runtime/Core/Features/Llm/MeaiOpenAiChatClient.cs` | 3/3: `-14,0 +15; -1053 +1054,3; -1255,0 +1259,5`; header/auth joins plus actual model/transport/empty Dispose context. |
| `Assets/CoreAI/Tests/EditMode/GatewayClientEditModeTests.cs` | New, full `1-625`, including fakes; actual wire, policy/usage/auth/tools/lifetime assertions checked. |
| `Assets/CoreAI/Tests/EditMode/GatewayServiceClientEditModeTests.cs` | New, full `1-308`, including fake handlers/streams; optional protocol, invalid/denied/unavailable, UTF-8 bounds and ownership assertions checked. |
| `Assets/CoreAI/README.md` | 1/1: `-258,0 +259,4`; feature entry point and portable scope. |
| `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/Gateway/UnityGatewayAuthorizationAdapters.cs` | New, full `1-104`; binding, leaf creation/profile metadata, lifetime and legacy adapter. |
| `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/Gateway/UnityWebRequestGatewayTransport.cs` | New, full `1-94`; main-thread dispatch/deadline/abort, bounded download and no redirects. |
| `Assets/CoreAiUnity/Runtime/Source/Composition/CoreAiGatewayClientInstaller.cs` | New, full `1-31`; opt-in guarded registration, explicit dependencies and owner lifetime. |
| `Assets/CoreAiUnity/Runtime/Source/Composition/LlmPipelineInstaller.cs` | 13/13: `-7,0 +8; -69,0 +71; -79 +81,3; -93 +97,2; -191 +196,2; -199 +205,2; -208 +215,2; -215 +223,2; -248 +257,2; -256 +266; -288 +298; -380 +390; -386,0 +397,5`. |
| `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/LlmClientRegistry.cs` | 5/5: `-10,0 +11; -39,0 +41; -334,0 +337,9; -342,0 +354; -1712,2 +1724,5`; profile construction plus replacement/disposal ownership context. |
| `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/MeaiLlmClient.cs` | 2/2: `-104,0 +105,12; -141 +153,3`; old factory forwarding, platform transport and Gateway leaf retention. |
| `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/ServerManagedLlmClient.cs` | 6/6: `-8,0 +9; -18 +19; -52,0 +54,10; -73 +84; -85 +96; -96 +107,9`; construction/header/capability forwarding with relevant legacy snapshot context. |
| `Assets/CoreAiUnity/Runtime/Source/Api/CoreAiBackend.cs` | 3/3: `-10,0 +11; -608,0 +610; -611 +613`; actual hot-swap binding lookup and shared builder call. |
| `Assets/CoreAiUnity/Tests/EditMode/GatewayClientCompositionEditModeTests.cs` | New, full `1-227`; actual builders/registry/fallback/binding plus all fakes; whole-scope cancellation versus missing retirement assertions distinguished. |
| `Assets/CoreAiUnity/README.md` | 1/1: `-259,0 +260,4`; scoped opt-in entry point. |
| `tools/portable/Tests/CoreAI.Portable.Tests.csproj` | 1/1: `-34,0 +35,2`; both portable fixtures explicitly linked. |
| `examples/dotnet/Program.cs` | 13/13: `-8,0 +9; -22 +23; -23,0 +25,2; -27,0 +31,9; -32 +44; -34 +46; -37,0 +50,10; -44 +66; -46 +68; -66 +88,2; -80 +103,6; -96 +124; -119,0 +148,13`; complete current 161-line sample also read. |
| `examples/dotnet/README.md` | 1/1: `-39,0 +40,14`; complete 53-line sample guide also read. |
| `Docs/CoreAI/GATEWAY_CLIENT.md` | New, full `1-148`; public defaults/composition/status/ownership/tools/validation/provenance claims matched against code. |

Supporting read-only evidence includes existing request/header/auth contracts, default MEAI wire/model/header/disposal semantics, Source fallback transient whitelist and HTTP await/abort helper, registry replacement/drain/owner seams, Core/Source/test assembly definitions, existing public constructors, assessment/design prose and the manifest. No unrelated live repair is included as Gateway implementation credit.

## Completion accounting

- CHANGED: this report only. All 29 staged candidates and live originals remain untouched by this auditor.
- VERIFY OUTPUT: 29/29 candidate hashes match; 11/11 baseline hashes match current live originals; 29-file/49-hunk plus full-new-file read coverage above. Direct review check: 29 candidate rows, five findings, zero trailing-whitespace lines and zero Cyrillic lines; scoped `git diff --check` exited 0, with direct content checks covering this untracked report. Five source-based P2 findings; no counted P1/P3. No build/test/runtime result is claimed from this review.
- NOT DONE: root integration, new regression implementation/execution, affected existing Core/Source suites, Unity on/off import/compile matrix, DI and hot-swap runtime, player/IL2CPP/WebGL/CORS, real provider/server and publication gates. Root supplied staged netstandard2.1/C#9 build, 54 Passed / 0 Failed portable cases, sample build/offline-help success, and staged Source compilation against real Unity references with zero errors. Those receipts do not cover the missing ID, model/denial classification, owned retirement or throwing-cancellation controls, and Source reference compilation is not a live Unity import/runtime result.
- CONCERNS: resolve GW-001 through GW-005 before acceptance; keep the remaining gaps separate from verified portable scope. Root must merge reviewed deltas instead of overwriting newer live fixes if hashes drift.
- QUESTIONS: none block this completed read-only review. No fixes were made; await root's implementation lease.
