# PR-14-001 multiplayer documentation repair

Status: implemented within the four-file lease; compilation and execution remain root-owned and unrun by this worker.

Dispatch: native subagent. Root requested model `gpt-6.1-sol` and reasoning effort `max`; this report does not independently certify dispatch metadata.

## Goal and review decisions

- Real goal: make the supplied-player integration expressible through the existing public contracts, without expanding either chat API.
- Scope: the Unity package README, multiplayer session guide, one compile-example C# file and this worker report. Host authentication, account/connection/view transitions, room/world policy and the RedoSchool migration remain outside this lease.
- Invariants: shared roles remain legitimate; factory keying governs ownership; explicit actor operations use a trusted admitted actor and a frozen host partition; caller cancellation is propagated; stop remains actor-specific and optional; opaque provider discovery must not imply a captured empty partition.
- Most likely mistake: documenting orchestration operations as members of `IInGameLlmChatService`, or inferring a provider through an opaque supplied store. Both are called out explicitly.
- Acceptance review: read both actual documentation diffs and the complete new source; compare both guide class blocks with the source; inspect the current public signatures and discovery branches. No runtime-isolation or compilation result is inferred from this source review.

## CHANGED

- `Assets/CoreAiUnity/README.md`: names the factory's legacy return contract and its supported send/clear/metric surface, distinguishes `CoreAI.Chat.CoreAiChatService`, and preserves host-owned identity transitions and shared roles.
- `Docs/CoreAI/MULTIPLAYER_SESSIONS.md`: separates legacy ownership from orchestration operations; documents the host-written adapter required to bridge them; removes the undeclared container example; adds two complete classes whose methods receive already-composed dependencies.
- The guide records explicit-provider precedence; store/inner-orchestrator discovery through `IAgentMemoryScopeProvider` or `IAgentMemoryScopeProviderSource`; rejection of inconsistent capability results; no inner fallback past an opaque supplied store; frozen known-empty results; and the legacy unbound behavior of an empty actor when a custom dependency hides its provider.
- The guide also records canonical trimmed trace uniqueness within an actor/role's active turns, raw/canonical event matching without caller-metadata rewriting, the full nine/seventeen-argument scope-aware constructors, and `AiOrchestrator.CreateWithMemoryScope` optional policy dependencies.
- `Assets/CoreAiUnity/Tests/EditMode/MultiplayerDocumentationCompileExamples.cs`: mirrors the two guide examples under the existing `CoreAI.Tests` assembly. Legacy chat uses only resolve/clear/send plus the caller token. Orchestration chat binds the admitted actor, selects clear options, sends a request with a fresh trace, requests actor-specific stop on connection cancellation, and returns persisted history with the same binding. Both reject an already-cancelled caller before mutation. No NUnit test scaffolding or provider execution is added.
- This report is retained because the user explicitly requested each worker's Markdown report, overriding the repository's general no-audit-report default for this task.

## VERIFY OUTPUT

Static inspection only; no build, test or provider command was run.

Public contracts checked against current source:

- `IInGameLlmChatService.cs` and `InGameLlmChatServiceFactory.cs`: resolve returns `IInGameLlmChatService`; supported methods are `SendPlayerMessageAsync`, `ClearHistory` and the two metric surfaces.
- `InGameLlmChatService.cs` and `CorePortableInstaller.cs`: stock legacy composition calls `ILlmClient` directly and accepts no identity provider.
- `CoreAiChatService.cs`: legacy and full-arity constructors, supplied-store discovery precedence, `SendMessageAsync(AiTaskRequest, CancellationToken)`, actor-aware `ClearContext`, `TryGetPersistedChatHistory` and `StopAgent`, and raw/canonical progress correlation.
- `AgentMemoryScope.cs`, `IAgentMemoryScopeProvider.cs`, `ScopedAgentMemoryStoreDecorator.cs`, `AiOrchestrator.cs` and `QueuedAiOrchestrator.cs`: optional capability shape, known versus unknown capture, explicit-provider precedence and the optional composition factory.
- `Assets/CoreAiUnity/Tests/CoreAI.Tests.asmdef`: existing Editor test assembly owns the new source directory and already references CoreAI.Core and CoreAI.Source.

A read-only PowerShell class-block comparison extracted the two named classes from the guide and C# source, compared them after trimming indentation, and rejected implicit `var` or NUnit test scaffolding. Output:

```text
LegacyChatExample: guide and C# source match, ignoring indentation
ActorOrchestrationChatExample: guide and C# source match, ignoring indentation
Compile examples: explicit local types; no NUnit test scaffolding
Compile source exists: D:\Git\CoreAI\Assets\CoreAiUnity\Tests\EditMode\MultiplayerDocumentationCompileExamples.cs
```

Read-only `git diff --no-ext-diff -- Assets/CoreAiUnity/README.md Docs/CoreAI/MULTIPLAYER_SESSIONS.md` was inspected in full. No Git mutation was performed.

## NOT DONE

- Compilation, EditMode/PlayMode tests, Unity import/MCP, live providers, network, browser/player/device and performance validation were not run. Root owns those gates and final acceptance.
- No `.meta`, generated project file, runtime API, asset, setting, scene, prefab, shared tracker, changelog, Git commit or RedoSchool file was changed.

## CONCERNS

- Unity automatically includes the new C# file under its existing assembly on import. An already-generated `.csproj` with an explicit source list may need regeneration or a root-owned temporary compile inclusion before it can prove the new examples compile.
- These methods are compile examples, not runtime tests. A successful compile would establish signature compatibility only; it would not establish live identity, persistence, cancellation or network behavior.
- The orchestration example expects the same host partition policy in its service/stores and rejects a missing explicit provider. Custom stores remain responsible for honoring the framework's actor-scope contract.

## QUESTIONS

None. Root can review the four leased files and perform the compilation/test gates without further implementation decisions from this worker.
