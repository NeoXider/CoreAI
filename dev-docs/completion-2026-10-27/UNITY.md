# Independent Unity host review, 2026-10-27

## Scope and evidence

Read-only review of the complete current tracked diff in `Assets/CoreAiUnity/Runtime/Source`, plus the untracked Gateway installer and both untracked Unity Gateway adapters. Relevant interfaces, production callers, identity/context flow, endpoint factory contracts, and regression fixtures were inspected. This review did not independently audit the queue or active-world facade previously implemented by this worker.

No C# fix, build/test, Unity/MCP operation, Git mutation, asset/meta write, or inference call was performed. The root reported final portable Core 2,954 passed / 3 not executed and 111 affected tests in each of Debug/Release; those are integration-owner receipts, not new evidence produced by this review. Native compilation and attached-player behavior were not independently verified here. No assumption about a repeated RoleId, game topology, or player identity was used as a defect.

## Confirmed remaining finding

### UNITY-REVIEW-001 [P2] A throwing activation cancellation callback prevents owned host release during registry disposal

- Site: `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/LlmClientRegistry.cs:310`, especially the cancellation loop at lines 310-315 and release scheduling after that loop.
- Contract/caller evidence: `ILlmEndpointClientFactory.ActivateAsync` receives the registry-owned cancellation token. `LlmEndpointClientActivation.ReleaseOwnedHostAsync` explicitly transfers a host-release obligation to the registry. Factories are replaceable; a token callback can throw. The current registry already treats callback/reentrant cleanup as a supported risk in its deactivation and Gateway retirement paths.
- Trigger: Endpoint A is ready and its activation returned a counting `ReleaseOwnedHostAsync` callback. Endpoint B is still activating and its factory registered a callback on the supplied token that throws. Call `registry.Dispose()`.
- Actual control flow: Dispose marks the registry disposed, then calls `ActivationCancellation.Cancel()` under `_gate`. `Cancel()` invokes all callbacks and throws an `AggregateException` when one fails. The exception exits before `RequestOwnedHostRelease(endpoint)` is reached for any ready endpoint. The `finally` retires only explicitly owned Gateway clients; it does not invoke A's separate host-release callback. A second Dispose returns immediately because `_disposed` is already true.
- Impact: The ready endpoint's owned native/server host can remain allocated after scope teardown; one faulty activation callback prevents cleanup of unrelated endpoints. Cancellation callbacks also still run while the mutation lock is held on this path.
- Meaningful regression: Use a controlled endpoint factory: A returns a ready borrowed client plus a host-release counter; B returns a pending task and registers a throwing cancellation callback. Dispose once, settle B, and assert A's host release ran exactly once, every activation was cancelled/settled, no cleanup callback ran under the registry mutation lock, and repeated Dispose is safe. Do not infer disposal of borrowed clients/transports.
- Required direction: Capture cancellation and release work under the lock, then perform every cancellation and release outside it with exception isolation/final settlement. A failure in one cancellation must not skip another endpoint's cleanup.
- Provenance: The unsafe cancellation loop was present before this diff. This is a confirmed remaining issue in the currently touched Dispose/lifecycle path, explicitly separated from a newly introduced PR regression. It was sent promptly to root for scope adjudication. The reproduction is source-backed and was not executed by this reviewer.

No additional confirmed new host/Gateway correctness finding was established in the reviewed changes.

## Reviewed behavior and rejected hypotheses

- Public constructor and method compatibility overloads retain the older callable arities. Newly added optional host seams remain configurable rather than inferred from game-specific roles.
- Gateway opt-in composition is scope-owned and additive. Legacy server-managed construction remains available. `IOwnedGatewayClient` is an explicit capability; arbitrary borrowed `IDisposable` clients/providers/transports are not automatically retired.
- Gateway request context is copied before invocation-owned route/model metadata is applied. Actor/trace/idempotency metadata follows the real request context. Portable Gateway implementation remains in Core; Unity authorization/transport adapters are host integrations rather than a portability requirement on Core.
- Registry replacement retires captured owned leaves outside the mutation lock. Late unpublished owned activation results are retired, while borrowed results remain borrowed. Existing Gateway composition tests include replacement, deactivation, late activation, failed pipeline construction, and asynchronous host release; their presence is not substituted for executing them here.
- Host chat binds an actual trusted actor and a known memory scope before memory/cancellation operations. Trace correlation accepts the admitted raw/trimmed trace forms. Reusing RoleId across actors was not treated as a game-policy bug.
- File memory changes release both instance/path locks before persistence settlement, track migration side effects, retain the primary failure, preserve corrupted sources, and retain explicit disposal/lifetime leases. Synchronous best-effort and async observable-failure boundaries were checked separately.
- Audit normalization replaces only unpaired UTF-16 surrogates before both preimage hashing and serialization, preserving valid pairs and the verifier's stored preimage.

## Source snapshot (SHA-256)

```text
AB966EB004CC8364D0DFA50BB187C2BB22D9991372FBCD33159BD10813AFC505  Assets/CoreAiUnity/Runtime/Source/Api/CoreAi.cs
CC1E492CCB8583E57EF03415589AFB2B1F1EEDF1CEF07F3F4B5B297A5CA655FB  Assets/CoreAiUnity/Runtime/Source/Api/CoreAiBackend.cs
0C85F86F4CD4E173B8D14C7CF8ED573A1C621420EC8D77433663D2685AC1EBBC  Assets/CoreAiUnity/Runtime/Source/Composition/LlmPipelineInstaller.cs
2CD76B00FFC1F1DD12D6D99571B8F41B5C7D97E509A39A128BD93C70AF824A28  Assets/CoreAiUnity/Runtime/Source/Composition/CoreAiGatewayClientInstaller.cs
75073ACC6F9695C1BF027785DFC6AE2E22971B3AEEF165934435E8785ECA0C69  Assets/CoreAiUnity/Runtime/Source/Features/AgentMemory/Infrastructure/FileAgentMemoryStore.cs
BC1A8ADD5D13FFC3CA9EBB78EB729FE09345329BFE92E28A2B46D1E226778073  Assets/CoreAiUnity/Runtime/Source/Features/Audit/AuditLogWriter.cs
EF811640918F3815D37BDB35E3C637AD6758C78A6CC1A274A767838E5E95A393  Assets/CoreAiUnity/Runtime/Source/Features/Chat/CoreAiChatService.cs
F16A48027355915A44FBAD06E7C1A2508D8FF2217C776BB77EC2C6FBE8D74CCF  Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/LlmClientRegistry.cs
A9CF87B9A1EF0844B963E0B1AA412D414B6713312D071C65B64E3978884CAC17  Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/MeaiLlmClient.cs
EE38C87142C0F5AF08082E8D57B214AE3C49490AA9345EAA3AB7D494C18771AE  Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/ServerManagedLlmClient.cs
8820E7A333D08F639F5981AD91C184B9D676B6E42943265F1BDB005AF61892F0  Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/Gateway/UnityGatewayAuthorizationAdapters.cs
1C8BD67675DF1F7B0F2B2C51307289381C20DF41ADCE678B8C19965BD8144546  Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/Gateway/UnityWebRequestGatewayTransport.cs
82F0BD1872900626B6E53A4C28ABFB85EAFBA77504245E72FD8BC1C004B8D3F9  Assets/CoreAiUnity/Runtime/Source/Features/World/Infrastructure/CoreAiComponentCommandExecutor.cs
```

## Handoff

### Accepted candidate follow-up (staged only)

Root accepted UNITY-REVIEW-001 as in scope, including its baseline provenance, and authorized mirrored candidates while the production Assets tree remained frozen. The registry candidate now captures all activation cancellation sources and publishes every host-release obligation under the lock, clears the activation pointers, then cancels each source outside the lock with isolated failure logging before executing every release. Explicit Gateway ownership retirement remains unchanged.

The staged `RegistryDisposal_ThrowingActivationCallbackCannotSkipOtherCancellationsOrOwnedHostReleases` regression uses a ready owned activation A, a pending activation B with a throwing token callback, and a healthy pending C. It checks callback lock ownership, all three cancellation signals, observable warning, exactly-once ready/late host releases, exactly-once owned leaf disposal, repeated Dispose, and borrowed-provider/transport retention.

Candidates, byte-exact originals, original/candidate SHA manifest, and review diff are under `C:/AI/work/coreai-completion-20261027/editor-host-candidates/`. This worker has not promoted them, compiled them, or executed the tests. The final candidate still requires root review, independent final review, and root-run native/portable gates; the prior independent pass covered the baseline and rejected the first language candidate before its correction, not the final merged candidate set.

- CHANGED: This review document and the subsequently authorized mirrored registry/test candidates; production Assets remain unchanged.
- VERIFY OUTPUT: Actual tracked diffs, untracked source, call sites, contracts, and relevant tests read; exact input hashes recorded.
- NOT DONE: Candidate promotion, reproduction execution, native/player/WebGL validation, and root integration acceptance.
- CONCERNS: One baseline lifecycle defect remains in a touched method; root must adjudicate and verify it. No broader game-policy claim is made.
- QUESTIONS: None.
