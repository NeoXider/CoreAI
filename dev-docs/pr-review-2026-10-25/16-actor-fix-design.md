# Actor and API fix design handoff

Status: DESIGN ONLY, frozen. Runtime remains the original review head `bc3fd74f0a889bfe1b1e7babc6bc02ed48a7c05e`; original reports 01 and 04 are unchanged. No implementation, builds, tests, Git mutation, Unity, provider or network calls were performed.

Detailed plan: `artifacts/pr-review-20261025/actor-fix-plan.md`.

Before planning: repair the actual scope/API/trace regressions together while preserving old manual composition, bound Empty, actor authority and normal DI. Before delegation: this is a design leaf and no additional agent or implementation is authorized. Before accepting: root must prove the constructor/method overload matrix and the real direct/chat/queued regressions; the design is not test evidence.

## Concrete recommendation

- Expose `ScopedAgentMemoryStoreDecorator`'s existing provider through optional `IAgentMemoryScopeProvider`. Discover it in both AiOrchestrator and chat when their new provider argument is omitted; explicit providers still win.
- Add one optional generic `IAgentMemoryScopeProviderSource.TryGetMemoryScopeProvider` capability in Core for core/queue wrappers. This is needed to discover an inner known provider without falsely returning Empty for Unknown, and to capture before an outer queue waits or a chat stream is enumerated. It introduces no Source dependency into Core.
- Keep Unknown as null. An internal `CaptureKnownActorScope` leaves an empty unknown actor unbound, preserving an opaque custom store's legacy fallback without claiming a frozen partition. Known providers capture once; explicit nonempty scopes and already-bound contexts, including Empty, win. Public Bind behavior remains unchanged. Chat history/clear enter the already selected actor instead of rebinding through a null/default provider.
- Restore baseline StopAgent, ClearContext and persisted-history method signatures. Keep full actor-aware overloads with their actor argument required. Restore all three changed legacy constructors: AiOrchestrator, CoreAiChatService and CoreAiComponentCommandExecutor, with original parameter order/names/defaults.
- Avoid overlapping optional constructor suffixes: preserve the legacy optional constructor and make the extended same-order constructor full-arity/required. Keep current full DI calls. Use a distinctly named factory for explicit-provider convenience and migrate this PR's short named-provider constructor calls if root accepts that tradeoff. The detailed plan lists every old signature and defaults.
- Correlate progress against exact actor + normalized nonempty role + either the captured raw trace or its existing core-trimmed alias. Preserve request metadata; do not normalize arbitrary incoming trace aliases. Document canonical-trim uniqueness for concurrent explicit traces of one actor/role, because the existing core producer already removes surrounding whitespace.

## Exact proposed source lease

One coordinated owner for eight runtime files: Core `IAgentMemoryScopeProvider.cs`, `ScopedAgentMemoryStoreDecorator.cs`, `AgentMemoryScope.cs`, `AiOrchestrator.cs`, `QueuedAiOrchestrator.cs`; Unity `CoreAiChatService.cs`, `CoreAi.cs`, `CoreAiComponentCommandExecutor.cs`. The artifact contains their complete paths and responsibilities. Do not split CoreAiChatService across workers.

Four existing test files: `ExecutionAdmissionRegressionTests.cs`, `MultiplayerSessionIntegrationTests.cs`, `CoreAiChatServiceEditModeTests.cs`, `UnityHostRegressionTests.cs`. The last three are Unity host proofs where applicable. Root owns serialized builds/tests and exact-arity reflection/consumer compile proof. CorePortableInstaller and scope-based chat construction already use full arity and should not require an implementation edit under this proposal.

Documentation changes after adjudication: multiplayer guide, chat README and provider capability XML; root owns TODO/PLAN/changelogs and final acceptance. No changes to reports 01/04, assets, metadata, scenes or prefabs.

## Required proofs and open points

Root must prove: old direct and chat constructors with a scoped store and no provider; queued admission/cancellation after a provider change; delayed chat enumeration; frozen Empty and narrowed grants; opaque custom-store fallback; normal DI; old delegates, reflected signatures/defaults and both old/new call matrices; raw and trimmed self-progress with foreign-turn rejection and correct caller/deadline attribution. Preserve existing attachment and actor cancellation regressions.

Open points, explicit rather than hidden: candidate-only short `scopeProvider:` constructor syntax and `CoreAi.ClearContext(role, actorContext: actor)` shorthand need either migration to a full call/distinct convenience or a separately approved larger overload set. Unqualified reflection by method name alone becomes ambiguous once same-name actor overloads exist; exact-arity reflection can be preserved. Root must approve documenting trimmed trace uniqueness, since whitespace-distinct traces cannot be isolated after the current core producer has already trimmed them. Rejecting opaque custom stores would be stricter than the proposed legacy-unbound fallback and would need an explicit compatibility decision.
