using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Chat;
using CoreAI.Composition;
using NUnit.Framework;
using VContainer;

namespace CoreAI.Tests.EditMode
{
    public sealed class MultiplayerSessionIntegrationTests
    {
        private const string Role = "SharedTeacher";

        [TestCase(false)]
        [TestCase(true)]
        public async Task ChatAdmission_AssignsDistinctTracesBeforeDispatchAndPreservesExplicitTraces(bool streaming)
        {
            InMemoryAgentMemoryStore memory = new InMemoryAgentMemoryStore();
            CoreAiChatService service = new(new RecordingOrchestrator(memory),
                actorIdentityProvider: Identity("trace-player", "connection", "campaign"));
            AiTaskRequest first = new() { RoleId = Role, Hint = "first" };
            AiTaskRequest second = new() { RoleId = Role, Hint = "second", TraceId = "   " };
            AiTaskRequest explicitTrace = new() { RoleId = Role, Hint = "third", TraceId = " caller-trace " };
            foreach (AiTaskRequest request in new[] { first, second, explicitTrace })
            {
                if (streaming)
                {
                    IAsyncEnumerable<LlmStreamChunk> stream = service.SendMessageStreamingAsync(request);
                    Assert.IsFalse(string.IsNullOrWhiteSpace(request.TraceId), "Streaming admission binds correlation before enumeration.");
                    await foreach (LlmStreamChunk chunk in stream) { }
                }
                else await service.SendMessageAsync(request);
            }

            Assert.IsFalse(string.IsNullOrWhiteSpace(first.TraceId));
            Assert.IsFalse(string.IsNullOrWhiteSpace(second.TraceId));
            Assert.AreNotEqual(first.TraceId, second.TraceId, "Separate turns need separate generated traces.");
            Assert.AreEqual(" caller-trace ", explicitTrace.TraceId);
        }

        [Test]
        public async Task ActorStop_CancelsOnlyMatchingConnectionWithSameRoleAndEmptyLogicalScope()
        {
            MutableScopeProvider provider = new MutableScopeProvider();
            InMemoryAgentMemoryStore backing = new InMemoryAgentMemoryStore();
            ScopedAgentMemoryStoreDecorator memory = new ScopedAgentMemoryStoreDecorator(backing, provider);
            IActorIdentityProvider identityA = Identity("player-a", "connection-a", "campaign");
            IActorIdentityProvider identityB = Identity("player-b", "connection-b", "campaign");
            BlockingOrchestrator inner = new BlockingOrchestrator(memory);
            using QueuedAiOrchestrator queue = new QueuedAiOrchestrator(inner,
                new AiOrchestrationQueueOptions { MaxConcurrent = 2 }, provider);
            CoreAiChatService serviceA = new CoreAiChatService(queue, null, null, memory, null, null, null,
                identityA, provider);
            CoreAiChatService serviceB = new CoreAiChatService(queue, null, null, memory, null, null, null,
                identityB, provider);
            AiTaskRequest requestA = new AiTaskRequest
            {
                RoleId = Role, Hint = "a history", CancellationScope = "",
                ActorContext = identityA.GetActorContext(Role)
            };
            AiTaskRequest requestB = new AiTaskRequest
            {
                RoleId = Role, Hint = "b history", CancellationScope = "",
                ActorContext = identityB.GetActorContext(Role)
            };
            Task<string> first = serviceA.SendMessageAsync(requestA);
            Task<string> second = serviceB.SendMessageAsync(requestB);
            try
            {
                await RequireCompletion(inner.BothStarted.Task);
                serviceA.StopAgent(Role, requestA.ActorContext);
                await RequireCompletion(first);
                Assert.IsTrue(first.IsCanceled, "Actor A's admitted request must observe connection cancellation.");
                Assert.IsFalse(second.IsCompleted, "Another actor using the same role must remain active.");
                Assert.AreEqual(0, inner.GlobalCancellationCalls);

                serviceA.ClearContext(Role, true, false, requestA.ActorContext);
                Assert.IsFalse(serviceA.TryGetPersistedChatHistory(Role, out _, maxMessages: 0, actorContext: requestA.ActorContext));
                Assert.IsTrue(serviceB.TryGetPersistedChatHistory(Role, out ChatMessage[] history,
                    maxMessages: 0, actorContext: requestB.ActorContext));
                Assert.AreEqual("b history", history[0].Content);

                inner.Release.TrySetResult(true);
                Assert.AreEqual("done", await second);
            }
            finally
            {
                inner.Release.TrySetResult(true);
                queue.Dispose();
                await Observe(first);
                await Observe(second);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task AdmittedChat_BindsProviderOnceIncludingAnEmptySnapshot(bool emptyAtAdmission)
        {
            MutableScopeProvider provider = new MutableScopeProvider
            {
                Current = emptyAtAdmission ? AgentMemoryScope.Empty : new AgentMemoryScope("tenant", "", "episode-a", "")
            };
            IActorIdentityProvider identity = Identity("provider-player", "connection", "");
            ScopedAgentMemoryStoreDecorator memory = new ScopedAgentMemoryStoreDecorator(
                new InMemoryAgentMemoryStore(), provider);
            RecordingOrchestrator inner = new RecordingOrchestrator(memory);
            using QueuedAiOrchestrator queue = new QueuedAiOrchestrator(inner,
                new AiOrchestrationQueueOptions { MaxConcurrent = 1 }, provider);
            CoreAiChatService service = new CoreAiChatService(queue, null, null, memory, null, null, null,
                identity, provider);
            AiTaskRequest request = service.CreateTaskRequest("frozen history", Role);
            int callsAtAdmission = provider.Calls;
            provider.Current = new AgentMemoryScope("tenant", "", "episode-b", "");

            Assert.AreEqual("done", await service.SendMessageAsync(request));
            Assert.AreEqual(callsAtAdmission, provider.Calls, "A bound empty snapshot must not reread mutable provider state.");
            Assert.IsTrue(service.TryGetPersistedChatHistory(Role, out ChatMessage[] admitted,
                maxMessages: 0, actorContext: request.ActorContext));
            Assert.AreEqual("frozen history", admitted[0].Content);
            Assert.IsFalse(service.TryGetPersistedChatHistory(Role, out _, maxMessages: 0, actorContext: identity.GetActorContext(Role)),
                "A fresh provider-selected partition must not read an earlier admitted partition.");
        }

        [Test]
        public async Task StreamingAdmission_FreezesProviderBeforeTheConsumerStartsEnumeration()
        {
            MutableScopeProvider provider = new MutableScopeProvider
            {
                Current = new AgentMemoryScope("tenant", "", "episode-a", "")
            };
            IActorIdentityProvider identity = Identity("stream-player", "connection", "");
            ScopedAgentMemoryStoreDecorator memory = new ScopedAgentMemoryStoreDecorator(
                new InMemoryAgentMemoryStore(), provider);
            RecordingOrchestrator inner = new RecordingOrchestrator(memory);
            using QueuedAiOrchestrator queue = new QueuedAiOrchestrator(inner,
                new AiOrchestrationQueueOptions { MaxConcurrent = 1 }, provider);
            CoreAiChatService service = new CoreAiChatService(queue, null, null, memory, null, null, null,
                identity, provider);
            AiTaskRequest request = new AiTaskRequest
            {
                RoleId = Role, Hint = "stream history", ActorContext = identity.GetActorContext(Role)
            };
            IAsyncEnumerable<LlmStreamChunk> stream = service.SendMessageStreamingAsync(request);
            int callsAtAdmission = provider.Calls;
            ActorContext admittedActor = request.ActorContext.Value;
            provider.Current = new AgentMemoryScope("tenant", "", "episode-b", "");
            request.ActorContext = Identity("other-player", "other-connection", "episode-b").GetActorContext(Role);
            request.Hint = "caller replacement";
            request.TraceId = "caller replacement trace";
            await foreach (LlmStreamChunk chunk in stream)
            {
                Assert.IsTrue(string.IsNullOrEmpty(chunk.Error), chunk.Error);
            }

            Assert.AreEqual(callsAtAdmission, provider.Calls);
            Assert.IsTrue(service.TryGetPersistedChatHistory(Role, out ChatMessage[] history,
                maxMessages: 0, actorContext: admittedActor));
            Assert.AreEqual("stream history", history[0].Content);
            Assert.IsFalse(service.TryGetPersistedChatHistory(Role, out _, maxMessages: 0, actorContext: request.ActorContext),
                "Mutating the caller request cannot replace the already admitted actor or payload.");
        }

        [Test]
        public void HistoryHydrationAndClear_UseTheSuppliedActorInsteadOfServiceDefault()
        {
            MutableScopeProvider provider = new MutableScopeProvider();
            ScopedAgentMemoryStoreDecorator memory = new ScopedAgentMemoryStoreDecorator(
                new InMemoryAgentMemoryStore(), provider);
            IActorIdentityProvider first = Identity("player-a", "first", "campaign");
            IActorIdentityProvider second = Identity("player-b", "second", "campaign");
            ActorContext actorA = first.GetActorContext(Role);
            ActorContext actorB = second.GetActorContext(Role);
            using (AgentMemoryActorScope.Enter(actorA, provider, Role))
                memory.AppendChatMessage(Role, "user", "a");
            using (AgentMemoryActorScope.Enter(actorB, provider, Role))
                memory.AppendChatMessage(Role, "user", "b");
            CoreAiChatService service = new CoreAiChatService(new RecordingOrchestrator(memory), null, null, memory,
                null, null, null, first, provider);

            Assert.IsTrue(service.TryGetPersistedChatHistory(Role, out ChatMessage[] visible, maxMessages: 0, actorContext: actorB));
            Assert.AreEqual("b", visible[0].Content);
            service.ClearContext(Role, true, false, actorB);
            Assert.IsFalse(service.TryGetPersistedChatHistory(Role, out _, maxMessages: 0, actorContext: actorB));
            Assert.IsTrue(service.TryGetPersistedChatHistory(Role, out ChatMessage[] retained, maxMessages: 0, actorContext: actorA));
            Assert.AreEqual("a", retained[0].Content);
        }

        [Test]
        public void ReconnectingActor_RetainsHistoryWhenHostReusesItsDurablePartition()
        {
            MutableScopeProvider provider = new MutableScopeProvider();
            ScopedAgentMemoryStoreDecorator memory = new ScopedAgentMemoryStoreDecorator(
                new InMemoryAgentMemoryStore(), provider);
            ActorContext first = Identity("player", "old-connection", "campaign").GetActorContext(Role);
            ActorContext replacement = Identity("player", "new-connection", "campaign").GetActorContext(Role);
            using (AgentMemoryActorScope.Enter(first, provider, Role))
                memory.AppendChatMessage(Role, "user", "retained");
            using (AgentMemoryActorScope.Enter(replacement, provider, Role))
                Assert.AreEqual("retained", memory.GetChatHistory(Role)[0].Content);
        }

        [Test]
        public void ActorKeyedFactory_SharesUntilFinalSessionReleaseAndAcceptsHostReplacement()
        {
            List<FakeChat> created = new List<FakeChat>();
            using ActorKeyedInGameLlmChatServiceFactory factory = new ActorKeyedInGameLlmChatServiceFactory(() =>
            {
                FakeChat chat = new FakeChat();
                created.Add(chat);
                return chat;
            });
            ActorContext first = Identity("actor", "connection-a", "campaign").GetActorContext(Role);
            ActorContext second = Identity("actor", "connection-b", "campaign").GetActorContext(Role);
            IInGameLlmChatService shared = factory.Resolve(first);
            Assert.AreSame(shared, factory.Resolve(second));
            Assert.IsTrue(factory.ReleaseActor(first));
            Assert.IsFalse(created[0].Disposed);
            Assert.AreSame(shared, factory.Resolve(second));
            Assert.IsFalse(factory.ReleaseActor(first), "Repeated release of one session is idempotent.");
            Assert.IsTrue(factory.ReleaseActor(second));
            Assert.IsTrue(created[0].Disposed);

            ContainerBuilder builder = new ContainerBuilder();
            builder.RegisterInstance<IInGameLlmChatServiceFactory>(factory);
            builder.RegisterCorePortable();
            using IObjectResolver container = builder.Build();
            Assert.AreSame(factory, container.Resolve<IInGameLlmChatServiceFactory>(),
                "Host registrations must replace the default actor-keyed ownership policy.");
        }

        private static IActorIdentityProvider Identity(string actor, string connection, string partition)
        {
            AgentMemoryScope scope = string.IsNullOrEmpty(partition)
                ? AgentMemoryScope.Empty
                : new AgentMemoryScope("tenant", "", partition, "chat");
            return new LocalActorIdentityProvider(actor, connection, "", ActorGrantSet.None, scope);
        }

        private static async Task RequireCompletion(Task task)
        {
            Task winner = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.AreSame(task, winner, "The deterministic session fixture did not finish within its hang guard.");
        }

        private static async Task Observe(Task task)
        {
            try { await task; }
            catch (OperationCanceledException) { }
        }

        private sealed class MutableScopeProvider : IAgentMemoryScopeProvider
        {
            public AgentMemoryScope Current = AgentMemoryScope.Empty;
            public int Calls;
            public AgentMemoryScope GetScope(string roleId) { Calls++; return Current; }
        }

        private class RecordingOrchestrator : IAiOrchestrationService, IAiActorContextResolver
        {
            protected readonly IAgentMemoryStore Memory;
            public RecordingOrchestrator(IAgentMemoryStore memory) { Memory = memory; }
            public ActorContext ResolveActorContext(AiTaskRequest request)
            {
                if (request.ActorContext == null) throw new InvalidOperationException("The fixture requires a supplied actor.");
                request.ActorContext.Value.AssertTrusted();
                return request.ActorContext.Value;
            }

            public virtual Task<string> RunTaskAsync(AiTaskRequest request, CancellationToken cancellationToken = default)
            {
                using (AgentMemoryActorScope.Enter(request.ActorContext.Value))
                    Memory.AppendChatMessage(request.RoleId, "user", request.Hint);
                return Task.FromResult("done");
            }

            public int GlobalCancellationCalls { get; private set; }
            public void CancelTasks(string scope) { GlobalCancellationCalls++; }
        }

        private sealed class BlockingOrchestrator : RecordingOrchestrator
        {
            private int _started;
            public TaskCompletionSource<bool> BothStarted { get; } =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<bool> Release { get; } =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public BlockingOrchestrator(IAgentMemoryStore memory) : base(memory) { }
            public override async Task<string> RunTaskAsync(AiTaskRequest request, CancellationToken cancellationToken = default)
            {
                await base.RunTaskAsync(request, cancellationToken);
                if (Interlocked.Increment(ref _started) == 2) BothStarted.TrySetResult(true);
                TaskCompletionSource<bool> interrupted =
                    new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (cancellationToken.Register(() => interrupted.TrySetCanceled(cancellationToken)))
                {
                    Task winner = await Task.WhenAny(Release.Task, interrupted.Task);
                    await winner;
                    cancellationToken.ThrowIfCancellationRequested();
                }

                return "done";
            }
        }

        private sealed class FakeChat : IInGameLlmChatService, IDisposable
        {
            public bool Disposed;
            public int HistoryPairCount => 0;
            public Task<LlmCompletionResult> SendPlayerMessageAsync(string message, CancellationToken cancellationToken = default)
                => Task.FromResult(new LlmCompletionResult { Ok = true, Content = "done" });
            public void ClearHistory() { }
            public void Dispose() { Disposed = true; }
        }
    }
}
