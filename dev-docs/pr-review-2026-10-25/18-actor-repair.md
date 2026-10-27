# Actor/API repair implementation receipt

Status: implementation in progress on released freeze; runtime and tests edited under lease. No git mutation performed.

## Continuation status
- Edited leased runtime: AgentMemoryScope.cs, IAgentMemoryScopeProvider.cs, ScopedAgentMemoryStoreDecorator.cs, AiOrchestrator.cs, QueuedAiOrchestrator.cs, CoreAiChatService.cs, CoreAi.cs, CoreAiComponentCommandExecutor.cs.
- Migrated leased tests: ExecutionAdmissionRegressionTests.cs, MultiplayerSessionIntegrationTests.cs.
- Static verification: dotnet CoreAI.Core/CoreAI.Source/CoreAI.Tests compile only pre-existing FileConversationSummaryStore.cs errors; no errors in leased files.
- Not run: Unity EditMode target filter `PrReviewCompositionEditModeTests;ExecutionAdmissionRegressionTests;MultiplayerSessionIntegrationTests;CoreAiChatServiceEditModeTests;UnityHostRegressionTests`.

Review baseline: `bc3fd74f0a889bfe1b1e7babc6bc02ed48a7c05e`, branch `demo`, actual checkout `D:/Git/CoreAI`. Root owns Git, builds, tests, Unity/MCP and final acceptance. Original reports 01/04/06 and design 16 are frozen.

## Goal and approved decisions

Before planning: repair PR-EXEC-001 and PR-HOST-001/002 coherently for manual core/chat/queue composition, exact legacy API signatures/defaults and truthful progress correlation. Preserve trusted actor identity, actor/connection/role cancellation, frozen Empty and explicit nonempty scopes, existing normal DI, borrowed attachments and WebGL-safe async. Unknown custom providers keep explicitly weaker legacy unbound semantics; they must not acquire a fake Empty snapshot.

Before delegation: this is one exclusive actor lease, with CoreAiChatService owned as a whole. No subagents, Git mutation, builds/tests, Unity/MCP, network or asset/metadata writes are allowed. No C# writes before root releases the freeze. Before accepting: root must inspect the real diff and actual red/green results; authored tests and static checks are not a pass.

Root approved the optional generic provider-source capability; discovery from known stores/inner wrappers; legacy optional constructors plus full required extended constructors; a named explicit-provider factory/conveniences; migration of candidate-only shorthand syntax; and exact-arity reflection/delegates rather than unqualified GetMethod-name uniqueness.

The root-owned optional-overload probe was read: it reports CS0121 for overlapping legacy/extended optional constructor suffixes and ClearContext defaults. The repair will not repeat that overload set.

## Exact runtime lease

1. `Assets/CoreAI/Runtime/Core/Features/AgentMemory/IAgentMemoryScopeProvider.cs`
2. `Assets/CoreAI/Runtime/Core/Features/AgentMemory/ScopedAgentMemoryStoreDecorator.cs`
3. `Assets/CoreAI/Runtime/Core/Features/AgentMemory/AgentMemoryScope.cs`
4. `Assets/CoreAI/Runtime/Core/Features/Orchestration/AiOrchestrator.cs`
5. `Assets/CoreAI/Runtime/Core/Features/Orchestration/QueuedAiOrchestrator.cs`
6. `Assets/CoreAiUnity/Runtime/Source/Features/Chat/CoreAiChatService.cs`
7. `Assets/CoreAiUnity/Runtime/Source/Api/CoreAi.cs`
8. `Assets/CoreAiUnity/Runtime/Source/Features/World/Infrastructure/CoreAiComponentCommandExecutor.cs`

Test lease under `Assets/CoreAiUnity/Tests/EditMode/`: `ExecutionAdmissionRegressionTests.cs`, `MultiplayerSessionIntegrationTests.cs`, `CoreAiChatServiceEditModeTests.cs`, `UnityHostRegressionTests.cs`, and root's new `PrReviewCompositionEditModeTests.cs`. Preserve the red fixture assertions. This receipt is the only documentation write; root's documentation owner handles guides, TODO/PLAN and changelogs.

## Prepared implementation boundaries

- Known store provider: expose existing `IAgentMemoryScopeProvider`; wrapper provider sources expose the provider without querying its current scope. Explicit provider wins. An opaque supplied chat store blocks inference from an unrelated inner service; a chat service without a store may discover its inner provider.
- Known binding: trust-check, retain an already-bound actor, preserve explicit nonempty actor scope, query a known provider once for normalized role, otherwise leave an empty unknown actor unbound. Preserve public Bind(null) behavior. History/clear enter the selected actor without rebinding through a nullable provider.
- Actorless queue: distinguish an actually captured provider scope from Unknown. Unknown neither dereferences null nor pushes an authoritative Empty frame. Keep role/logical cancellation semantics and unstarted-turn ownership intact.
- API: restore baseline 16-argument core and 8-argument chat constructors with original names/order/defaults; retain candidate full 17/9 argument signatures with required parameters. Add CreateWithMemoryScope for convenient explicit-provider construction. Restore StopAgent/ClearContext/history exact legacy methods and retain full actor-aware calls; use non-overlapping forwarding/convenience overloads. Restore the one-argument component constructor; preserve existing options-aware DI behavior.
- Progress: capture immutable raw and existing core-trimmed trace forms, exact actor and normalized role. Accept only those two trace forms; reject foreign/missing identity, normalize nonempty forwarded roles, preserve original request metadata, and keep caller cancellation separate from deadlines. Root documentation should require canonical-trim uniqueness for concurrent explicit traces of one actor/role.

## Preparation evidence and remaining work

- Read frozen actor plan, source findings and root's new composition fixture. HEAD still matches the baseline. Existing unrelated QualitySettings/ETFX/pipeline files and other root-owned red fixtures are untouched.
- The composition fixture covers manual core/chat scope isolation, exact method/delegate and constructor signatures, raw forwarded trace progress and foreign progress rejection. Its assertions will be retained and extended only after release.
- Candidate shorthand call sites and additional boundary tests are being mapped read-only. Any required migration outside the exact lease will be reported to root before editing.
- Not run by this worker: compile, tests, Unity, player/WebGL, provider/network or Git mutations. Waiting for explicit root C# release; elapsed time is not authorization.
