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

Bind the effective actor and partition once before admission. The frozen binding includes an empty provider result; changing a mutable provider later does not redirect an admitted turn. `ResolveActorContext` resolves identity only. The queue and direct orchestrator bind persistence before execution, and `CoreAiChatService` binds before its first await or stream enumeration.

```csharp
IAiOrchestrationService orchestrator = container.Resolve<IAiOrchestrationService>();
IActorIdentityProvider identity = connectionIdentity;
IAgentMemoryScopeProvider partitions = hostPartitions;
const string role = "Teacher";
ActorContext actor = AgentMemoryActorScope.Bind(identity.GetActorContext(role), partitions, role);
AiTaskRequest request = new AiTaskRequest
{
    RoleId = role,
    Hint = "Explain this puzzle",
    ActorContext = actor,
    CancellationScope = "puzzle-view"
};
string answer = await orchestrator.RunTaskAsync(request, connectionCancellationToken);
```

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

`CoreAiChatService` assigns a missing `AiTaskRequest.TraceId` before dispatch and preserves an explicit caller trace. Its nonstreaming idle deadline accepts tool progress only when the event's admitted `ActorId`, trace and role all match that turn. Custom tool-event publishers must carry those identities; role-only or unattributed global events cannot rearm a turn. Keep an explicit trace unique to the actor's turn and propagate it consistently through that turn's retries. Streaming admission copies the bound request before enumeration; attachment lists/bytes still follow their documented borrowed lifetime.

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

## Chat service ownership and replacement

`ActorKeyedInGameLlmChatServiceFactory` retains one `IInGameLlmChatService` per durable actor, with a set of active connection ids. Resolving the same actor from two connections shares its service, history and service-level limits. Releasing one connection preserves the service; the final session release evicts and disposes it. Resolving the same connection repeatedly does not add a reference count. The default capacity is 256 retained actors, and capacity exhaustion throws before another service is created.

A game that needs independent per-connection view state supplies its own `IInGameLlmChatServiceFactory`. Register it before `RegisterCorePortable`; the installer preserves that registration.

`Resolve(actor)` selects a service and tracks its lifetime; it does not bind that actor to later method calls. Supply the admitted `ActorContext` in each `AiTaskRequest` and in history, clear and stop operations. The simple text/role overloads use the service's injected identity provider. Use a host factory that supplies a connection-bound provider when those overloads should select the connection automatically.

```csharp
builder.RegisterInstance<IInGameLlmChatServiceFactory>(sessionViewFactory);
builder.RegisterCorePortable();
```

The replacement factory owns its keying, bounds and disposal policy. It may keep presentation state per connection while composing a shared durable memory store. Factory ownership does not impose network message order. The legacy TMP chat panel resolves a service during startup; recreate it at a host identity transition.

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

World save/reset outcomes are available through optional `IWorldStateWriteResults`. `WorldStateWriteResult.Status` reports the local operation; `IsConfirmed` requires local success and automatic persistence arming. A custom legacy manager without the optional interface is unconfirmed. `ConfirmDurability` observes arming only and cannot upgrade a failed local operation. On WebGL, true never acknowledges an IndexedDB transaction. Reset can already have cleared the in-memory world when local deletion fails; no rollback is implied.

Curated component commands default to at most 4096 LineRenderer positions. Configure a different budget by passing `CoreAiComponentCommandOptions` to `CoreAiComponentCommandExecutor` or registering those options in the host's executor composition. Negative, fractional, nonfinite or above-budget position counts are rejected before component creation or mutation.

The static `CoreAi` facade follows its resolved lifetime scope/container and re-resolves after that scope is destroyed. It selects one scene scope; it is not a room/player router. Multi-room hosts use their injected services directly. Manual `Invalidate()` retires service caches; new Unity play generations additionally clear static tool subscribers/history and backend subscribers.
