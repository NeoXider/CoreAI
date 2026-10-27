# Multiplayer sessions and host integration

CoreAI accepts host-issued actor contexts and composes replaceable orchestration, memory, chat and world blocks. The game owns authentication, account switching, matchmaking, room membership, transport ordering and the lifetimes of connections and views. CoreAI does not infer a login, logout or reconnect from a panel being disabled.

## Identity and persistence

| Value | Meaning | Host policy |
| --- | --- | --- |
| `ActorContext.ActorId` | Durable principal used for memory, ownership, quotas and audit | Keep stable when the same player reconnects. Different players have different ids. |
| `ActorContext.SessionId` | Connection identity used for cancellation | Issue a fresh value for each replacement connection. Retain the admitted value when stopping old work. |
| `AgentMemoryScope.SessionId` | Opaque durable persistence partition | Select a campaign, lesson, conversation or other game-defined partition. It need not equal the connection id. |
| `RoleId` | Agent behavior and role configuration | Multiple players may use the same role. A role is not a player identity. |
| `AiTaskRequest.CancellationScope` | Optional logical supersession/cancellation group | Use for a view or workflow. It may be empty and need not equal either session value. |

`IActorIdentityProvider` supplies trusted immutable `ActorContext` values. A transport adapter obtains these from the host's admission and authority composition. Untrusted client input must not choose grants or impersonate another principal. `LocalActorIdentityProvider` is a synthetic local identity provider; creating one is not a network authentication mechanism.

`IAgentMemoryScopeProvider` supplies the persistence partition when the actor does not already carry one. Keep its tenant/user/topic fields stable according to the game's storage policy. The default local actor and empty scope preserve legacy role-only storage. A named actor participates in the scoped memory key even when its explicit memory scope is empty.

Bind the effective actor and partition once before admission with `AgentMemoryActorScope.Bind(actor, partitions, roleId)`, using a trusted actor from the host's identity provider and the same partition provider as its scoped stores. An already-bound actor keeps its partition. A known provider result of `AgentMemoryScope.Empty` is also frozen; changing that provider later does not redirect an admitted turn. `AiOrchestrator.ResolveActorContext` resolves identity only. Queue actor resolution/admission captures a known partition; the direct orchestrator captures it before execution, and `CoreAiChatService` captures it before its first await or stream enumeration. The orchestration example below shows an explicit binding.

Provider discovery is an optional capability, not a search through arbitrary custom dependencies:

| Service | Provider selection when the actor is not already bound |
| --- | --- |
| `AiOrchestrator` | An explicit `scopeProvider` wins; otherwise discover a provider from its supplied memory store. |
| `QueuedAiOrchestrator` | An explicit `scopeProvider` wins; otherwise discover a provider exposed by the inner orchestrator. |
| `CoreAiChatService` | An explicit `scopeProvider` wins. Otherwise, if a memory store was supplied, discover only from that store. With no supplied store, discover from the orchestrator. An opaque supplied store blocks fallback to the orchestrator's provider. |

Discovery accepts `IAgentMemoryScopeProvider` directly, as exposed by `ScopedAgentMemoryStoreDecorator`, or the optional `IAgentMemoryScopeProviderSource` capability. The latter must return `true` with a nonnull provider, or `false` with null; an inconsistent result is rejected. The built-in direct and queued orchestrators expose this capability without querying the provider's current partition.

An empty, unbound actor remains on legacy unbound resolution when an opaque custom store or orchestrator hides its provider. Admission cannot promise a frozen partition in that composition. Supply the provider explicitly, expose the capability, or pass an actor already bound with the host's provider when a frozen partition is required. An actor carrying a nonempty explicit memory scope can also capture that scope without discovering a provider.

Keep the request and its attachments unchanged until completion or stream disposal. Preserve the admitted actor alongside the connection's work; consulting a newly selected account when cancelling an older turn selects the wrong connection.

## Stop, disconnect and supersession

`IActorAiTaskCancellation.CancelActorTasks(actor, roleId)` cancels queued and running work matching both the durable actor id and connection session id. Omit the role to stop that connection's roles together. This includes requests with an empty logical cancellation scope and streaming requests.

`IAiOrchestrationService.CancelTasks(scope)`, `IScopedAiTaskCancellation.CancelTasks(scope, role)` and `IRoleAiTaskCancellation.CancelRoleTasks(role)` are administrative operations. Actor-aware work may be affected across actor connections by those operations. User stop controls use the actor-specific capability. `CoreAiChatService.StopAgent` and `CoreAi.StopAgent` follow that rule; an orchestrator without the optional capability does not receive a global fallback cancellation. Pass a caller-owned token on every turn so local cancellation remains available with a custom orchestrator.

```csharp
try
{
    if (orchestrator is IActorAiTaskCancellation cancellation)
    {
        cancellation.CancelActorTasks(admittedActor);
    }
}
finally
{
    try
    {
        connectionCancellationSource.Cancel();
    }
    finally
    {
        chatFactory.ReleaseActor(admittedActor);
    }
}
```

Call connection cleanup once under host ownership, and put view disposal in a further `finally` if host cancellation callbacks can throw. Factory session release is idempotent: a repeated release returns false. The factory does not own the transport, connection cancellation source or panel. Disposal of one factory releases all services retained by that factory, so a shared factory belongs to the enclosing room/server scope.

Cancellation is cooperative. It cannot roll back a world command, tool side effect or file write that already committed. Reject a disconnected connection before admitting new work. Define transport ordering and retry/idempotency keys at the host boundary; CoreAI does not provide transactional rollback or network request deduplication.

`CoreAiChatService` assigns a missing or whitespace-only `AiTaskRequest.TraceId` before dispatch and preserves a nonblank caller trace exactly. Its nonstreaming idle deadline accepts tool progress only when the event's admitted `ActorId`, trace and role all match that turn. Custom tool-event publishers must carry those identities; role-only or unattributed global events cannot rearm a turn. An event trace may match either the original caller trace or its trimmed canonical value. This compatibility is for event correlation only: it does not rewrite caller metadata. Keep the canonical trimmed trace unique within each actor/role's active turns and propagate the same turn trace through retries; traces that differ only in surrounding whitespace are not distinct correlation identities. Streaming admission copies the bound request before enumeration; attachment lists/bytes still follow their documented borrowed lifetime.

## History and view state

History hydration and clearing outside a turn must enter the same actor/provider binding as admission. A scoped decorator by itself cannot know which connection a caller represents.

```csharp
using (AgentMemoryActorScope.Enter(admittedActor, hostPartitions, role))
{
    ChatMessage[] history = scopedMemory.GetChatHistory(role, 40);
}
```

`CoreAiChatService.TryGetPersistedChatHistory(role, out history, maxMessages, actorContext)` and `ClearContext(role, clearChatHistory, clearLongTermMemory, actorContext)` do this internally. Supplying the actor overrides the service's default identity for that operation. Clearing persisted chat also clears its derived summary through the scoped store. Clearing long-term memory is a separate choice.

`CoreAiChatPanel` uses its supplied identity for history and clear operations and retains the admitted actor for stopping active work. Its per-role presentation caches belong to that panel instance. The host must recreate the panel when changing an account/session ownership boundary, or explicitly retire every cached role and all host-added UI state. `ClearChat(false, false)` clears the visible active role without deleting persisted chat or long-term memory; it does not clear every cached role. Disable/enable alone preserves view state.

The panel's turn-generation checks discard late UI results after a stop or superseding turn. They do not establish the game's account-transition policy. Finish cancellation and view retirement before admitting the next account's work.

## Legacy chat factory ownership and replacement

`ActorKeyedInGameLlmChatServiceFactory` retains one `IInGameLlmChatService` per durable actor, with a set of active connection ids. Resolving the same actor from two connections shares its service, history and service-level limits. Releasing one connection preserves the service; the final session release evicts and disposes it. Resolving the same connection repeatedly does not add a reference count. The default capacity is 256 retained actors, and capacity exhaustion throws before another service is created.

A game that needs independent per-connection service state supplies its own `IInGameLlmChatServiceFactory`. Register it before `RegisterCorePortable`; the installer preserves that registration.

The resolved legacy interface exposes `SendPlayerMessageAsync(string, CancellationToken)`, `ClearHistory()`, `HistoryPairCount` and `GetRateLimiterMetrics()`. Its stock `InGameLlmChatService` keeps in-memory conversation history and calls `ILlmClient` directly. It accepts no actor/role per turn, identity provider, persisted-history restore or stop operation. Its caller token controls cancellation. `Resolve(actor)` keys service ownership; it does not attach that actor to provider requests or enter actor-aware orchestration.

This example receives a factory and an actor already admitted by the host. Clearing affects the retained service shared by that actor's active connections. Connection cleanup releases the actor separately, as shown above.

```csharp
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Authority;

public static class LegacyChatExample
{
    public static Task<LlmCompletionResult> SendLegacyChatAsync(
        IInGameLlmChatServiceFactory chatFactory,
        ActorContext admittedActor,
        string message,
        bool clearHistory,
        CancellationToken connectionCancellationToken)
    {
        connectionCancellationToken.ThrowIfCancellationRequested();
        IInGameLlmChatService service = chatFactory.Resolve(admittedActor);
        if (clearHistory)
        {
            service.ClearHistory();
        }

        return service.SendPlayerMessageAsync(message, connectionCancellationToken);
    }
}
```

The replacement factory owns its keying, bounds and disposal policy. It may keep presentation state per connection while composing a shared durable memory store. Factory ownership does not impose network message order. The legacy TMP chat panel resolves a service during startup; recreate it at a host identity transition.

## Actor-aware orchestration chat

`CoreAI.Chat.CoreAiChatService` is a separate application service; it does not implement `IInGameLlmChatService`. It sends `AiTaskRequest` turns through an `IAiOrchestrationService` and provides actor-aware persisted history, clear options and stop operations. Its simple text/role overloads resolve the actor through its injected `IActorIdentityProvider`. Explicit actors override that default for the requested operation.

The following method receives an already-composed chat service, a trusted admitted actor and the host's partition provider. The service and its stores must use that same partition policy. It freezes the partition before clearing, sending or loading history, and supplies the captured actor to every operation. Registering stop against the caller token requests actor-specific queue cancellation while that turn is active; the token is also passed to the send for custom orchestrators without that optional capability. Clear options are host choices and the returned history is a snapshot for the host's view.

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Chat;

public static class ActorOrchestrationChatExample
{
    public static async Task<(string Answer, ChatMessage[] History)> SendOrchestratedChatAsync(
        CoreAiChatService chat,
        ActorContext admittedActor,
        IAgentMemoryScopeProvider hostPartitions,
        string roleId,
        string message,
        bool clearChatHistory,
        bool clearLongTermMemory,
        CancellationToken connectionCancellationToken)
    {
        connectionCancellationToken.ThrowIfCancellationRequested();
        if (hostPartitions == null)
        {
            throw new ArgumentNullException(nameof(hostPartitions));
        }

        ActorContext boundActor = AgentMemoryActorScope.Bind(admittedActor, hostPartitions, roleId);
        if (clearChatHistory || clearLongTermMemory)
        {
            chat.ClearContext(roleId, clearChatHistory, clearLongTermMemory, boundActor);
        }

        AiTaskRequest request = new AiTaskRequest
        {
            RoleId = roleId,
            Hint = message,
            ActorContext = boundActor,
            TraceId = Guid.NewGuid().ToString("N"),
            CancellationScope = "puzzle-view"
        };

        using (CancellationTokenRegistration stopRegistration = connectionCancellationToken.Register(
                   () => chat.StopAgent(roleId, boundActor)))
        {
            string answer = await chat.SendMessageAsync(request, connectionCancellationToken);
            chat.TryGetPersistedChatHistory(roleId, out ChatMessage[] history, 40, boundActor);
            return (answer, history);
        }
    }
}
```

The matching sources in [`MultiplayerDocumentationCompileExamples.cs`](../../Assets/CoreAiUnity/Tests/EditMode/MultiplayerDocumentationCompileExamples.cs) belong to the existing Unity EditMode test assembly and are included when Unity compiles that assembly. They are compile examples; including them does not execute provider calls or tests.

A host bridging these chat surfaces writes an `IInGameLlmChatService` adapter and owns its actor capture, role selection, result mapping, cancellation and history semantics. Replacing the factory alone preserves the legacy return contract and does not expose the orchestration service's richer methods.

For manual composition, the existing `CoreAiChatService` constructor retains its optional legacy dependencies. Selecting its explicit `scopeProvider` overload requires all nine arguments; the corresponding `AiOrchestrator` constructor requires all seventeen arguments. `AiOrchestrator.CreateWithMemoryScope(...)` accepts the explicit provider while keeping its context, trace, budget, token-estimator and compaction dependencies optional. No factory identity-provider overload is implied by any of these constructors.

## Composition choices and limits

| Host | Composition |
| --- | --- |
| Solo game | Use the default local identity and empty memory scope, one lifetime scope and one authoritative world manager. |
| Embedded AI | Register host identity, partition and storage interfaces before portable services; construct `CoreAiChatService` with those same instances or use the scope-based resolver. |
| Player-hosted co-op | Admit each connection at the authoritative host, issue its context and route cancellation through that admitted connection. Clients do not mint authoritative grants. |
| Dedicated server | Own services at room/server lifetime, enforce admission/rate limits and observe queue refusals. Size concurrency for the actual backend. |

Mirror is an optional transport adapter, not a required account, room or persistence service. In-process tests with fake identity/storage/transport do not establish room capacity, replication correctness or real network behavior.

The one-shot camera/image follow-up helpers call `ILlmClient` directly. Their caller token owns cancellation; they do not enter the actor-aware queue or persist conversation history. Send images through a normal `AiTaskRequest.Attachments` turn when the host requires orchestration admission, memory and actor-specific queue cancellation.

`AiOrchestrationQueueOptions` bounds concurrency, pending admissions and buffered stream backlog. Defaults are 2 concurrent tasks, 64 pending items, 64 buffered chunks per active stream and a 1 MiB retained-string accounting budget per stream. These are admission/backlog limits, not a total process-memory or provider-payload limit. Hosts must handle queue refusal and consumer cancellation/disposal. Raising the limits does not prove a 100-player capacity target.

Resolving an LLM endpoint handle for metadata does not pin its host. Each actual completion or first stream iteration admits a separate lease against the current endpoint generation. A handle retained across deactivation, replacement, removal or registry disposal can refuse with typed `BackendUnavailable`; resolve a fresh route before admitting new work. Ordinary retirement waits for admitted operations, subject to the existing 120-second owned-host drain limit. This does not authorize replaying a turn whose tools already committed effects.

The default `WorldStateManager` captures tracked objects across loaded scenes and uses one local `world_state.json`. Multiple default managers do not provide per-player world ownership or distinct storage namespaces. Use one world authority for that preset. A host needing independent worlds omits the default world block and supplies its own `IWorldStateManager`, startup and ownership/storage policy; actor memory isolation does not route world objects or replicate snapshots.

When composing `WorldStateManager` directly, its four-argument constructor accepts an absolute snapshot file path before `Initialize`. Separate paths isolate snapshot files; the host still owns scene/object partitioning, authorization, and routing between independent worlds.

World save/reset outcomes are available through optional `IWorldStateWriteResults`. `WorldStateWriteResult.Status` reports the local operation; `IsConfirmed` requires local success and automatic persistence arming. A custom legacy manager without the optional interface is unconfirmed. `ConfirmDurability` observes arming only and cannot upgrade a failed local operation. On WebGL, true never acknowledges an IndexedDB transaction. Reset can already have cleared the in-memory world when local deletion fails; no rollback is implied.

Curated component commands default to at most 4096 LineRenderer positions. Configure a different budget by passing `CoreAiComponentCommandOptions` to `CoreAiComponentCommandExecutor` or registering those options in the host's executor composition. Negative, fractional, nonfinite or above-budget position counts are rejected before component creation or mutation.

The static `CoreAi` facade follows its resolved lifetime scope/container and re-resolves after that scope is destroyed. It selects one scene scope; it is not a room/player router. Multi-room hosts use their injected services directly. Manual `Invalidate()` retires service caches; new Unity play generations additionally clear static tool subscribers/history and backend subscribers.
