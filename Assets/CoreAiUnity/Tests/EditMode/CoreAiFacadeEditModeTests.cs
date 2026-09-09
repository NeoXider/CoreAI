using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Composition;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using CoreAI.Messaging;
using NUnit.Framework;
using CoreAI;
using UnityEngine;
using VContainer;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode coverage for the static <see cref="CoreAi"/> facade when no
    /// <c>CoreAILifetimeScope</c> is present in the scene.
    /// </summary>
    public sealed class CoreAiFacadeEditModeTests
    {
        private SynchronizationContext _previousSynchronizationContext;

        /// <summary>
        /// WHY this fixture detaches: it asserts through Assert.ThrowsAsync/CatchAsync, which BLOCK
        /// the calling thread until the awaited delegate finishes — being inside an async test does
        /// not change that. Under Unity's SynchronizationContext the delegate's continuation is
        /// posted back to that same blocked thread, and the editor deadlocks with no results file.
        /// </summary>
        [SetUp]
        public void DetachSynchronizationContext()
        {
            _previousSynchronizationContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void RestoreSynchronizationContext()
        {
            SynchronizationContext.SetSynchronizationContext(_previousSynchronizationContext);
        }

        [SetUp]
        public void ResetFacade()
        {
            CoreAi.Invalidate();
        }

        [Test]
        public void IsReady_WithoutLifetimeScope_ReturnsFalse()
        {
            Assert.IsFalse(CoreAi.IsReady, "Без CoreAILifetimeScope в сцене фасад не должен считаться готовым");
        }

        [Test]
        public void IsReady_ConcurrentWithResolverMutation_DoesNotThrowOrDeadlock()
        {
            // IsReady mutates shared resolver state via TryResolve; it must take SyncRoot like every
            // other resolver entry point so it cannot race a concurrent SetResolver and corrupt the
            // cached fields. A stub resolver keeps TryResolve off the Unity-main-thread scene lookup.
            CoreAi.SetResolver(() => new TestStubOrchestrator());

            Exception captured = null;
            Task[] workers = new Task[8];
            for (int i = 0; i < workers.Length; i++)
            {
                bool mutator = i % 2 == 0;
                workers[i] = Task.Run(() =>
                {
                    try
                    {
                        for (int n = 0; n < 500; n++)
                        {
                            if (mutator)
                            {
                                CoreAi.SetResolver(() => new TestStubOrchestrator());
                            }
                            else
                            {
                                _ = CoreAi.IsReady;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Interlocked.CompareExchange(ref captured, ex, null);
                    }
                });
            }

            Assert.IsTrue(Task.WaitAll(workers, TimeSpan.FromSeconds(10)),
                "Concurrent IsReady/SetResolver workers should finish without deadlocking.");
            Assert.IsNull(captured, $"IsReady must acquire SyncRoot so it is thread-safe: {captured}");
        }

        [Test]
        public void Invalidate_DoesNotThrow_WhenCalledMultipleTimes()
        {
            Assert.DoesNotThrow(() => CoreAi.Invalidate());
            Assert.DoesNotThrow(() => CoreAi.Invalidate());
            Assert.DoesNotThrow(() => CoreAi.Invalidate());
        }

        [Test]
        public void GetSettings_WithoutLifetimeScope_ReturnsNull()
        {
            ICoreAISettings settings = CoreAi.GetSettings();
            Assert.IsNull(settings,
                "Без scope GetSettings возвращает null (caller должен сам использовать CoreAISettings.Instance)");
        }

        [Test]
        public void GetChatService_WithoutLifetimeScope_ThrowsInvalidOperation()
        {
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => CoreAi.GetChatService());
            StringAssert.Contains("CoreAILifetimeScope", ex.Message,
                "Исключение должно подсказывать, где искать проблему");
        }

        [Test]
        public void TryGetChatService_WithoutLifetimeScope_ReturnsFalse()
        {
            Assert.IsFalse(CoreAi.TryGetChatService(out _),
                "Без scope TryGet не бросает исключение и возвращает false");
        }

        [Test]
        public void GetOrchestrator_WithoutLifetimeScope_ThrowsInvalidOperation()
        {
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => CoreAi.GetOrchestrator());
            StringAssert.Contains("IAiOrchestrationService", ex.Message,
                "Исключение должно объяснять, что не зарегистрирован оркестратор");
        }

        [Test]
        public void TryGetOrchestrator_WithoutLifetimeScope_ReturnsFalse()
        {
            Assert.IsFalse(CoreAi.TryGetOrchestrator(out _));
        }

        [Test]
        public void SetResolver_OverridesOrchestratorResolution_ForTesting()
        {
            TestStubOrchestrator resolverOrchestrator = new();
            CoreAi.SetResolver(() => resolverOrchestrator);

            IAiOrchestrationService resolved = CoreAi.GetOrchestrator();

            Assert.AreSame(resolverOrchestrator, resolved);
        }

        [Test]
        public async Task OrchestrateAsync_ProductionComposition_UsesActorDurableKeyAndSessionCancellation()
        {
            const string RoleId = "ActorWiringRegression";
            LocalActorIdentityProvider actorIdentityProvider = new(
                "facade-actor",
                "facade-session",
                "",
                ActorGrantSet.None,
                AgentMemoryScope.Empty);
            RecordingMemoryStore backingStore = new();
            FixedMemoryScopeProvider legacyScopeProvider = new();
            ScopedAgentMemoryStoreDecorator memoryStore = new(backingStore, legacyScopeProvider);
            BlockingFirstLlmClient llmClient = new();
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            settings.ConfigureOffline();

            ContainerBuilder builder = new();
            builder.RegisterInstance<IActorIdentityProvider>(actorIdentityProvider);
            builder.Register<DefaultGameLogSettings>(Lifetime.Singleton).As<IGameLogSettings>();
            builder.RegisterCore();
            builder.RegisterInstance<ICoreAISettings, CoreAISettingsAsset>(settings);
            builder.RegisterAgentPrompts(null);
            builder.RegisterInstance<ILlmClient>(llmClient);
            builder.RegisterInstance<IAiOrchestrationMetrics>(new NullAiOrchestrationMetrics());
            builder.RegisterInstance(new AiOrchestrationQueueOptions { MaxConcurrent = 1 });
            builder.RegisterInstance<IAuthorityHost>(new SoloAuthorityHost());
            builder.RegisterInstance<IAgentMemoryScopeProvider>(legacyScopeProvider);
            builder.RegisterInstance<IAgentMemoryStore>(memoryStore);
            builder.RegisterCorePortable(
                suppressDefaultConversationSummaryStore: false,
                suppressDefaultAgentMemoryStore: true);

            try
            {
                using IObjectResolver container = builder.Build();
                IAiOrchestrationService orchestrator = container.Resolve<IAiOrchestrationService>();
                CoreAi.SetResolver(() => orchestrator);

                AiTaskRequest firstRequest = new()
                {
                    RoleId = RoleId,
                    Hint = "first",
                    CancellationScope = "legacy-first"
                };
                Task<string> first = CoreAi.OrchestrateAsync(firstRequest);

                Task firstStarted = llmClient.FirstCallStarted.Task;
                Task startWinner = await Task.WhenAny(firstStarted, Task.Delay(TimeSpan.FromSeconds(5)));
                Assert.AreSame(firstStarted, startWinner, "The first production request did not reach the LLM.");
                await firstStarted;

                AiTaskRequest secondRequest = new()
                {
                    RoleId = RoleId,
                    Hint = "second",
                    CancellationScope = "legacy-second"
                };
                Task<string> second = CoreAi.OrchestrateAsync(secondRequest);
                Task secondWinner = await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(5)));

                Assert.AreSame(second, secondWinner,
                    "Different legacy scopes must still be latest-wins within the actor SessionId.");
                Assert.AreEqual("ok", await second);
                // WHY: TaskCanceledException derives from OperationCanceledException, and
                // ThrowsAsync matches the exact type — CatchAsync accepts either.
                Assert.CatchAsync<OperationCanceledException>(async () =>
                {
                    await first;
                });

                ActorContext expectedActor = actorIdentityProvider.GetActorContext(RoleId);
                // WHY: AgentMemoryScopeKey.Resolve(ActorContext, roleId) does not replicate the
                // host-scope-provider fallback the real in-turn path uses, so it cannot predict this
                // key independently - assert the property it stands for directly instead: one single
                // key was used, and it is actor-derived, not the bare RoleId legacy key.
                Assert.AreEqual(1, backingStore.SeenKeys.Length,
                    "Both requests of the same actor/session must resolve to one durable memory key.");
                string firstActorKey = backingStore.SeenKeys[0];
                StringAssert.IsMatch("^actor-v1-[0-9a-f]{64}$", firstActorKey,
                    "The scoped production memory store must receive the ActorId-derived durable key.");
                Assert.AreEqual(expectedActor.ActorId, firstRequest.ActorContext?.ActorId);
                Assert.AreEqual(expectedActor.SessionId, firstRequest.ActorContext?.SessionId);
                Assert.AreEqual(expectedActor.ActorId, secondRequest.ActorContext?.ActorId);
                Assert.AreEqual(expectedActor.SessionId, secondRequest.ActorContext?.SessionId);

                // WHY (F14): a key matching "^actor-v1-[0-9a-f]{64}$" alone proves nothing - any SHA-256 of
                // anything satisfies it. Run a second, different actor through the SAME long-lived
                // memoryStore/backingStore and assert its key differs from the first actor's: this is the
                // value assertion that would have caught F1 (an ActorId-agnostic pin merging two actors'
                // memory onto one key).
                LocalActorIdentityProvider otherActorIdentityProvider = new(
                    "facade-actor-2",
                    "facade-session-2",
                    "",
                    ActorGrantSet.None,
                    AgentMemoryScope.Empty);
                SequentialLlmClient otherLlmClient = new();
                CoreAISettingsAsset otherSettings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
                otherSettings.ConfigureOffline();
                try
                {
                    ContainerBuilder otherBuilder = new();
                    otherBuilder.RegisterInstance<IActorIdentityProvider>(otherActorIdentityProvider);
                    otherBuilder.Register<DefaultGameLogSettings>(Lifetime.Singleton).As<IGameLogSettings>();
                    otherBuilder.RegisterCore();
                    otherBuilder.RegisterInstance<ICoreAISettings, CoreAISettingsAsset>(otherSettings);
                    otherBuilder.RegisterAgentPrompts(null);
                    otherBuilder.RegisterInstance<ILlmClient>(otherLlmClient);
                    otherBuilder.RegisterInstance<IAiOrchestrationMetrics>(new NullAiOrchestrationMetrics());
                    otherBuilder.RegisterInstance(new AiOrchestrationQueueOptions { MaxConcurrent = 1 });
                    otherBuilder.RegisterInstance<IAuthorityHost>(new SoloAuthorityHost());
                    otherBuilder.RegisterInstance<IAgentMemoryScopeProvider>(legacyScopeProvider);
                    otherBuilder.RegisterInstance<IAgentMemoryStore>(memoryStore);
                    otherBuilder.RegisterCorePortable(
                        suppressDefaultConversationSummaryStore: false,
                        suppressDefaultAgentMemoryStore: true);

                    using IObjectResolver otherContainer = otherBuilder.Build();
                    CoreAi.SetResolver(() => otherContainer.Resolve<IAiOrchestrationService>());

                    AiTaskRequest otherActorRequest = new()
                    {
                        RoleId = RoleId,
                        Hint = "other-actor"
                    };
                    Assert.AreEqual("reply-1", await CoreAi.OrchestrateAsync(otherActorRequest));
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(otherSettings);
                }

                Assert.AreEqual(2, backingStore.SeenKeys.Length,
                    "A different actor must resolve to its own durable memory key.");
                Assert.IsFalse(
                    string.Equals(backingStore.SeenKeys[0], backingStore.SeenKeys[1], StringComparison.Ordinal),
                    "Two different actors must never collide on the same durable memory key.");
            }
            finally
            {
                CoreAi.Invalidate();
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public async Task OrchestrateAsync_ProductionComposition_ReconnectResumesOneDurableMemoryWithoutFork()
        {
            const string RoleId = "ReconnectMemoryRegression";
            // WHY: a genuine reconnect resumes the SAME lesson - the host looks tenant/user/topic up from
            // durable per-actor state, it does not re-roll them per socket. Only the low-level connection
            // id (ActorContext.SessionId, "used only for cancellation" - not part of AgentMemoryScope, so
            // not part of the durable key) changes between connections. Fixing this at the source (a stable
            // AgentMemoryScope across reconnects) is what keeps the actor on one durable key - no
            // process-wide pin in ScopedAgentMemoryStoreDecorator required, and no such pin can distinguish
            // this from a genuine tenant/topic change (see AgentMemoryActorScopeKeyEditModeTests'
            // OneLongLivedDecorator tests for the fork case).
            AgentMemoryScope stableLessonScope = new AgentMemoryScope("school-tenant", "learner-1", "lesson-7", "unit-3");
            ReconnectingIdentityProvider identityProvider = new ReconnectingIdentityProvider(
                "durable-reconnect-actor",
                "session-before",
                stableLessonScope);
            RecordingMemoryStore backingStore = new RecordingMemoryStore();
            FixedMemoryScopeProvider legacyScopeProvider = new FixedMemoryScopeProvider();
            ScopedAgentMemoryStoreDecorator memoryStore = new ScopedAgentMemoryStoreDecorator(
                backingStore, legacyScopeProvider);
            SequentialLlmClient llmClient = new SequentialLlmClient();
            ContainerBuilder builder = new ContainerBuilder();
            builder.RegisterInstance<IActorIdentityProvider>(identityProvider);
            builder.RegisterInstance<ICoreAISettings>(new ProductionTestSettings());
            builder.RegisterInstance<ILlmClient>(llmClient);
            builder.RegisterInstance<IAuthorityHost>(new SoloAuthorityHost());
            builder.RegisterInstance<IAiGameCommandSink>(new NoopCommandSink());
            builder.RegisterInstance<IAgentSystemPromptProvider>(new StubPromptProvider());
            builder.RegisterInstance<IAgentUserPromptTemplateProvider>(
                new NoAgentUserPromptTemplateProvider());
            builder.RegisterInstance<IAiOrchestrationMetrics>(new NullAiOrchestrationMetrics());
            builder.RegisterInstance(new AiOrchestrationQueueOptions { MaxConcurrent = 1 });
            builder.RegisterInstance<IAgentMemoryScopeProvider>(legacyScopeProvider);
            builder.RegisterInstance<IAgentMemoryStore>(memoryStore);
            builder.RegisterCorePortable(
                suppressDefaultConversationSummaryStore: false,
                suppressDefaultAgentMemoryStore: true);

            using IObjectResolver container = builder.Build();
            IAiOrchestrationService orchestrator = container.Resolve<IAiOrchestrationService>();
            CoreAi.SetResolver(() => orchestrator);
            try
            {
                AiTaskRequest firstRequest = new AiTaskRequest
                {
                    RoleId = RoleId,
                    Hint = "first-turn"
                };
                Assert.AreEqual("reply-1", await CoreAi.OrchestrateAsync(firstRequest));
                ActorContext firstActor = firstRequest.ActorContext.Value;

                identityProvider.Reconnect("session-after", stableLessonScope);

                AiTaskRequest secondRequest = new AiTaskRequest
                {
                    RoleId = RoleId,
                    Hint = "second-turn"
                };
                Assert.AreEqual("reply-2", await CoreAi.OrchestrateAsync(secondRequest));
                ActorContext secondActor = secondRequest.ActorContext.Value;

                Assert.AreEqual(firstActor.ActorId, secondActor.ActorId);
                Assert.AreNotEqual(firstActor.SessionId, secondActor.SessionId);
                Assert.AreEqual(firstActor.MemoryScope.TenantId, secondActor.MemoryScope.TenantId);
                Assert.AreEqual(firstActor.MemoryScope.UserId, secondActor.MemoryScope.UserId);
                Assert.AreEqual(firstActor.MemoryScope.TopicId, secondActor.MemoryScope.TopicId);
                // WHY: the low-level connection SessionId differs (a genuine reconnect), but the durable
                // key must come out identical because it is derived from ActorId + AgentMemoryScope only -
                // never from ActorContext.SessionId. Proves the real production behavior directly: exactly
                // one durable key was ever written to, with no pin anywhere in the memory decorator.
                Assert.AreEqual(1, backingStore.SeenKeys.Length,
                    "A reconnect that resumes the same AgentMemoryScope must never fork a durable actor " +
                    "into a second memory key.");
                string durableKey = backingStore.SeenKeys[0];
                StringAssert.IsMatch("^actor-v1-[0-9a-f]{64}$", durableKey);

                ChatMessage[] history = backingStore.GetChatHistory(durableKey);
                Assert.AreEqual(4, history.Length);
                StringAssert.Contains("first-turn", history[0].Content);
                Assert.AreEqual("reply-1", history[1].Content);
                StringAssert.Contains("second-turn", history[2].Content);
                Assert.AreEqual("reply-2", history[3].Content);
            }
            finally
            {
                CoreAi.Invalidate();
            }
        }

        private sealed class TestStubOrchestrator : IAiOrchestrationService
        {
            public Task<string> RunTaskAsync(AiTaskRequest task, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(string.Empty);
            }

            public async IAsyncEnumerable<LlmStreamChunk> RunStreamingAsync(
                AiTaskRequest task,
                [EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                yield return new LlmStreamChunk { IsDone = true };
                await Task.CompletedTask;
            }

            public void CancelTasks(string cancellationScope)
            {
            }
        }

        private sealed class BlockingFirstLlmClient : ILlmClient
        {
            private int _callCount;

            public TaskCompletionSource<bool> FirstCallStarted { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public async Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                int call = Interlocked.Increment(ref _callCount);
                if (call == 1)
                {
                    FirstCallStarted.TrySetResult(true);
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }

                return new LlmCompletionResult { Ok = true, Content = "ok" };
            }
        }

        private sealed class SequentialLlmClient : ILlmClient
        {
            private int _callCount;

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                int call = Interlocked.Increment(ref _callCount);
                return Task.FromResult(new LlmCompletionResult
                {
                    Ok = true,
                    Content = $"reply-{call}"
                });
            }
        }

        private sealed class RecordingMemoryStore : IAgentMemoryStore
        {
            private readonly ConcurrentDictionary<string, byte> _seenKeys = new(StringComparer.Ordinal);
            private readonly InMemoryAgentMemoryStore _inner = new InMemoryAgentMemoryStore();

            public string[] SeenKeys => new List<string>(_seenKeys.Keys).ToArray();

            public bool TryLoad(string roleId, out AgentMemoryState state)
            {
                Record(roleId);
                return _inner.TryLoad(roleId, out state);
            }

            public void Save(string roleId, AgentMemoryState state)
            {
                Record(roleId);
                _inner.Save(roleId, state);
            }

            public void Clear(string roleId)
            {
                Record(roleId);
                _inner.Clear(roleId);
            }

            public void ClearChatHistory(string roleId)
            {
                Record(roleId);
                _inner.ClearChatHistory(roleId);
            }

            public void AppendChatMessage(
                string roleId,
                string role,
                string content,
                bool persistToDisk = true)
            {
                Record(roleId);
                _inner.AppendChatMessage(roleId, role, content, persistToDisk);
            }

            public ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0)
            {
                Record(roleId);
                return _inner.GetChatHistory(roleId, maxMessages);
            }

            private void Record(string roleId)
            {
                _seenKeys.TryAdd(roleId ?? "", 0);
            }
        }

        private sealed class ReconnectingIdentityProvider : IActorIdentityProvider
        {
            private readonly string _actorId;
            private LocalActorIdentityProvider _current;

            public ReconnectingIdentityProvider(
                string actorId,
                string sessionId,
                AgentMemoryScope memoryScope)
            {
                _actorId = actorId;
                Reconnect(sessionId, memoryScope);
            }

            public ActorContext GetActorContext(string roleId)
            {
                return _current.GetActorContext(roleId);
            }

            public void Reconnect(string sessionId, AgentMemoryScope memoryScope)
            {
                _current = new LocalActorIdentityProvider(
                    _actorId,
                    sessionId,
                    "world",
                    ActorGrantSet.None,
                    memoryScope);
            }
        }

        private sealed class StubPromptProvider : IAgentSystemPromptProvider
        {
            public bool TryGetSystemPrompt(string roleId, out string prompt)
            {
                prompt = "system";
                return true;
            }
        }

        private sealed class NoopCommandSink : IAiGameCommandSink
        {
            public void Publish(ApplyAiGameCommand command)
            {
            }
        }

        private sealed class ProductionTestSettings : ICoreAISettings
        {
            public int MaxLuaRepairRetries => 0;
            public bool EnableMeaiDebugLogging => false;
            public float LlmRequestTimeoutSeconds => 5f;
            public int MaxLlmRequestRetries => 0;
            public bool EnableHttpDebugLogging => false;
            public bool LogTokenUsage => false;
            public bool LogLlmLatency => false;
            public bool LogLlmConnectionErrors => false;
            public int ContextWindowTokens => 8192;
            public string UniversalSystemPromptPrefix => "";
            public float Temperature => 0f;
            public int MaxToolCallRetries => 0;
            public bool LogToolCalls => false;
            public bool LogToolCallArguments => false;
            public bool LogToolCallResults => false;
            public bool LogMeaiToolCallingSteps => false;
            public bool AllowDuplicateToolCalls => false;
            public bool EnableStreaming => false;
        }

        private sealed class FixedMemoryScopeProvider : IAgentMemoryScopeProvider
        {
            public AgentMemoryScope GetScope(string roleId)
            {
                return new AgentMemoryScope("legacy-tenant", "legacy-user", "legacy-session", "");
            }
        }
    }
}
