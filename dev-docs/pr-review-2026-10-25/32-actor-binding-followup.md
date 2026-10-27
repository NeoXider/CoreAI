# Actor and binding follow-up implementation

Status: implementation complete; execution pending root. This follows the accepted `DELTA-ACTOR-001` in frozen report 31. No build, test, Unity, MCP, provider/network or Git mutation was run by this worker.

## Decisions and scope

Before planning: remove only the newly invented history convenience that makes an existing default-literal consumer ambiguous. Preserve the exact legacy `(string, out ChatMessage[], int = 0)` ABI/default and full actor-aware `(string, out ChatMessage[], int, ActorContext?)` ABI. Add meaningful controls for the previously untested discovery/Unknown joins and shared bound descriptors without changing those runtime designs.

Before delegation: this is an exclusive implementation leaf; no further agent, Unity operation or execution lane is authorized. The writable lease contains only `CoreAiChatService.cs`, `PrReviewCompositionEditModeTests.cs`, `MultiplayerSessionIntegrationTests.cs`, `SkillContractRegressionTests.cs`, and this report. Root owns execution, shared trackers, acceptance and Git.

Before accepting: the actual source/test changes were read back, the caller migration and method set were searched, and scoped whitespace/prose checks were run. Existing assertions remain; new tests are implemented but unexecuted. English prose and explicit C# types are retained. Report 31 is unchanged; its SHA-256 is still `C39DFF14AE5EEB82EE17BD2DE8DBD55654858B1457E4680134FEED0E51212C41`.

## Implemented changes

| File | Actual follow-up |
| --- | --- |
| `Assets/CoreAiUnity/Runtime/Source/Features/Chat/CoreAiChatService.cs` | Removed the five-line three-argument `ActorContext?` history convenience. The legacy history3/default and actor history4 remain, with no new alias or overload. No other runtime behavior was changed by this follow-up. |
| `Assets/CoreAiUnity/Tests/EditMode/MultiplayerSessionIntegrationTests.cs` | Migrated all nine named actor-only history calls to the full actor overload using `maxMessages: 0`. Actor isolation, frozen scope, clear and history assertions remain. |
| `Assets/CoreAiUnity/Tests/EditMode/PrReviewCompositionEditModeTests.cs` | Added a compile-visible `default` history call, optional/integer calls, exact old/new history delegates, existing reflected invocation/default metadata checks, compile-visible stop/clear delegates, the missing chat ClearContext delegate case, and old optional versus extended full-arity constructor metadata controls. Added seven admission cases below. Reused the real core/queue, scoped in-memory store, identity, and recording provider fakes. |
| `Assets/CoreAiUnity/Tests/EditMode/SkillContractRegressionTests.cs` | Added two bound-proxy tests for shared factories/restrictions and live replacement/removal/captured calls, using the real proxy/resolver/call implementations and a counting function tool. Existing denied-memory and skill tests remain unchanged. |

## New admission and binding controls

- `EXEC001_QueueDiscoversInnerScopeBeforeWaiting(false/true)`: the queue and real core omit explicit providers. The first completion is held while a second turn captures either a nonempty scope or Empty. The provider changes before execution resumes. Assertions cover one admission query, waiting before the first release, original prompt/history partition, no later-partition write, trusted captured scope and retained narrowed grants. A five-second completion bound, cancellation-aware fake gate, explicit release, queue disposal and task observation cover teardown.
- `EXEC001_ChatWithoutStoreCapturesInnerBeforeDeferredStream(false/true)`: chat has no store/provider, and its queue discovers the core's scoped-store capability. Admission binds the nonempty/Empty scope before enumeration and before any provider request. After provider change, the real stream must use and persist the original partition without recapture. Stream draining has a five-second completion bound and the queue is disposed by the fixture.
- `EXEC001_OpaqueChatStoreSuppressesUnrelatedInnerProvider`: a supplied wrapper intentionally exposes only the memory-store contract. Its own scoped store contains history, while the inner real core has an unrelated known provider. Request creation must leave the actor unbound, history and clear must follow the opaque store's legacy provider, and the unrelated provider must receive zero queries. This is a behavioral control, not a requirement for a host to disclose its provider.
- `EXEC001_ActorlessUnknownQueuePreservesLegacyMemoryResolution(false/true)`: the real queue wraps an actorless service with no provider capability. Its body appends through a scoped store. Without an ambient frame, append/history must use that store's provider; with an ambient scope, both must retain that scope. Assertions check the actual history effect and prove no fake bound Empty frame was installed. Completion is bounded at five seconds.
- `BoundSkillProxies_ShareOneDescriptorFactoryPerRoleBinding`: one paired bound read/call list creates one descriptor factory per binding despite repeated reads/resolution and later restrictions. Discovery is callable, execution reaches the described body, restricted read/call remain denied, the call proxy reuses its root function, and a second role obtains its own descriptor cache.
- `BoundLiveSkillProxies_RefreshReplacementAndRemovalWhileKeepingCapturedInvocations`: one bound pair observes replacement instructions/descriptors and invokes the replacement body, while a previously captured call still invokes the original. Removal hides both discovery and new resolution, a captured replacement remains invocable, and a cancelled captured call enters no body. Factory counts assert one descriptor build for each bound immutable skill version.

## Static verification

- Scoped `git diff --check` on the four source/test files exited 0.
- Search finds exactly the intended two public history signatures, at `CoreAiChatService.cs:666` and `:671`; no actor-only history convenience remains.
- A multiline search of the multiplayer fixture finds nine actor history calls, each now supplying `maxMessages: 0` and `actorContext:`.
- Read-back scans show 26 declared NUnit cases in the composition fixture and eight in the skill-contract fixture; these are source inventory counts, not execution outcomes. Both files have zero `var` declarations and zero Cyrillic lines.
- Existing project wiring was read: the real generated `CoreAI.Tests.csproj` includes composition, multiplayer and skill fixtures. The portable Core test project includes `SkillContractRegressionTests.cs`; it does not include the Unity composition fixture. A portable zero-match filter cannot prove the host controls.
- Frozen report 31 hash is unchanged. No source outside the assigned lease, metadata, asset, settings, shared tracker or Git index was edited by this worker.

## Exact root filters and expected checks

For the portable lane, use `FullyQualifiedName~CoreAI.Tests.EditMode.SkillContractRegressionTests` (eight declared cases), or just the two new cases with `FullyQualifiedName~BoundSkillProxies_ShareOneDescriptorFactoryPerRoleBinding|FullyQualifiedName~BoundLiveSkillProxies_RefreshReplacementAndRemovalWhileKeepingCapturedInvocations`. Expected: the named tests are discovered and pass, with no zero-match acceptance.

For Unity EditMode, select these exact fixture names through root's existing runner:

- `CoreAI.Tests.EditMode.PrReviewCompositionEditModeTests` (26 declared cases).
- `CoreAI.Tests.EditMode.MultiplayerSessionIntegrationTests` (existing fixture; all current history/clear/actor assertions retained).
- `CoreAI.Tests.EditMode.SkillContractRegressionTests` (eight declared cases).
- Existing complementary controls: `CoreAI.Tests.EditMode.ExecutionAdmissionRegressionTests`, `CoreAI.Tests.EditMode.PrReviewToolBindingEditModeTests`, `CoreAI.Tests.EditMode.CoreAiChatServiceEditModeTests`.

The host test assembly must compile the literal `chat.TryGetPersistedChatHistory(Role, out ChatMessage[] defaultLiteral, default)` and both exact history delegates. The removed convenience should reproduce CS0121 if temporarily restored by root in an isolated proof; this worker did not restore it or execute such a comparison. Compile failure or missing named tests is not a passing result.

## Completion accounting

- CHANGED: exactly the four assigned source/test files plus this new report. Report 31 remains historical and unchanged.
- VERIFY OUTPUT: static checks above; no compiler, test runner or runtime was executed.
- NOT DONE: all root compile/test/Unity/player/WebGL/model/fault/load gates. This follow-up does not add a new omitted-provider pending-cancellation test or bound-pair test through each MEAI streaming/text protocol; existing explicit-provider cancellation and MEAI tests remain complementary. No general adversarial factory-reentry/parallel-catalog stress was added or claimed.
- CONCERNS: root must inspect the final diff and obtain real host and portable results before acceptance. The prior release red baseline and older portable results do not validate these new controls.
- QUESTIONS: none. The implementation lease is ready to return to root.
