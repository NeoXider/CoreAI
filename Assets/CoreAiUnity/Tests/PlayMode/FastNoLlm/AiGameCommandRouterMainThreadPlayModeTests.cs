using System;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Messaging;
using CoreAI.Messaging;
using CoreAI.Session;
using MessagePipe;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static CoreAI.Messaging.AiGameCommandTypeIds;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Play Mode: ,  <see cref="AiGameCommandRouter"/> 
    /// <see cref="AiGameCommandRouter.CommandReceived"/>    Unity,   MessagePipe
    ///      (  <c>ConfigureAwait(false)</c>  <see cref="QueuedAiOrchestrator"/>).
    /// </summary>
    public sealed class AiGameCommandRouterMainThreadPlayModeTests
    {
        private sealed class NoOpGameLogger : IGameLogger
        {
            public void LogDebug(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogInfo(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogWarning(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogError(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }
        }

        /// <summary>
        ///  pub/sub: <see cref="Publish"/>      ,   
        /// ( <see cref="MessageBrokerCore{TMessage}.Publish"/>),  DI MessagePipe.
        /// </summary>
        private sealed class CurrentThreadPublishBus : IPublisher<ApplyAiGameCommand>, ISubscriber<ApplyAiGameCommand>
        {
            private IMessageHandler<ApplyAiGameCommand> _handler;

            public void Publish(ApplyAiGameCommand message)
            {
                _handler?.Handle(message);
            }

            public IDisposable Subscribe(IMessageHandler<ApplyAiGameCommand> handler,
                params MessageHandlerFilter<ApplyAiGameCommand>[] filters)
            {
                _handler = handler;
                return new Unsubscribe(() => _handler = null);
            }

            private sealed class Unsubscribe : IDisposable
            {
                private Action _onDispose;

                public Unsubscribe(Action onDispose)
                {
                    _onDispose = onDispose;
                }

                public void Dispose()
                {
                    Interlocked.Exchange(ref _onDispose, null)?.Invoke();
                }
            }
        }

        /// <summary>      (  async    main thread).</summary>
        private sealed class ThreadPoolDeliverySubscriber : ISubscriber<ApplyAiGameCommand>
        {
            private IMessageHandler<ApplyAiGameCommand> _handler;

            public IDisposable Subscribe(IMessageHandler<ApplyAiGameCommand> handler,
                params MessageHandlerFilter<ApplyAiGameCommand>[] filters)
            {
                _handler = handler;
                return new Unsubscribe(() => _handler = null);
            }

            public void DeliverFromThreadPool(ApplyAiGameCommand cmd)
            {
                IMessageHandler<ApplyAiGameCommand> h = _handler ??
                                                        throw new InvalidOperationException(
                                                            "Subscribe before DeliverFromThreadPool.");
                _ = Task.Run(() => h.Handle(cmd));
            }

            private sealed class Unsubscribe : IDisposable
            {
                private Action _onDispose;

                public Unsubscribe(Action onDispose)
                {
                    _onDispose = onDispose;
                }

                public void Dispose()
                {
                    Interlocked.Exchange(ref _onDispose, null)?.Invoke();
                }
            }
        }

        private static ApplyAiGameCommand SampleEnvelope()
        {
            return new ApplyAiGameCommand
            {
                CommandTypeId = Envelope,
                JsonPayload = "{}",
                SourceRoleId = BuiltInAgentRoleIds.Creator,
                SourceTaskHint = "main_thread_test",
                TraceId = "main-thread-test"
            };
        }

        private sealed class NullWorldExecutor : Infrastructure.World.ICoreAiWorldCommandExecutor
        {
            public string[] LastListedAnimations { get; } = Array.Empty<string>();

            public System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object>>
                LastListedObjects { get; } = new();

            public bool TryExecute(ApplyAiGameCommand cmd)
            {
                return false;
            }
        }

        [Test]
        public void ResetStatics_ClearsSubscribersAndActiveRouterCount()
        {
            CurrentThreadPublishBus bus = new();
            _ = new AiGameCommandRouter(bus, new NoOpGameLogger(), new NullWorldExecutor());
            AiGameCommandRouter.CommandReceived += _ => { };

            AiGameCommandRouter.ResetStatics();

            System.Reflection.FieldInfo countField = typeof(AiGameCommandRouter).GetField(
                "_activeRouterCount",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            System.Reflection.FieldInfo eventField = typeof(AiGameCommandRouter).GetField(
                "CommandReceived",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(countField);
            Assert.NotNull(eventField);
            Assert.AreEqual(0, countField.GetValue(null));
            Assert.IsNull(eventField.GetValue(null));
        }

        [UnityTest]
        public IEnumerator Router_CommandReceived_OnMainThread_WhenSubscribeInvokedFromThreadPool()
        {
            yield return null;

            int mainThreadId = Thread.CurrentThread.ManagedThreadId;

            ThreadPoolDeliverySubscriber subscriber = new();
            AiGameCommandRouter router = new(subscriber, new NoOpGameLogger(), new NullWorldExecutor());

            bool received = false;
            int receivedThreadId = -1;

            void OnCommandReceived(ApplyAiGameCommand _)
            {
                receivedThreadId = Thread.CurrentThread.ManagedThreadId;
                received = true;
            }

            AiGameCommandRouter.CommandReceived += OnCommandReceived;
            try
            {
                router.Start();
                subscriber.DeliverFromThreadPool(SampleEnvelope());

                float deadline = Time.realtimeSinceStartup + 8f;
                while (!received && Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                }

                Assert.IsTrue(received, "CommandReceived     .");
                Assert.AreEqual(
                    mainThreadId,
                    receivedThreadId,
                    "CommandReceived      Unity  SwitchToMainThread.");
            }
            finally
            {
                AiGameCommandRouter.CommandReceived -= OnCommandReceived;
                router.Dispose();
            }
        }

        /// <summary>
        /// Regression coverage for the stale-subscriber audit finding: <see cref="AiGameCommandRouter.CommandReceived"/>
        /// is process-global (static), so a scene-scoped subscriber that misses its own unsubscribe used to
        /// survive a scene reload and receive commands routed by the next scene's router (duplicate world
        /// mutations against destroyed objects). Disposing the router (scope teardown) must clear the event.
        /// </summary>
        [UnityTest]
        public IEnumerator Router_Dispose_ClearsStaleStaticSubscribers_FromPreviousScope()
        {
            yield return null;

            CurrentThreadPublishBus oldBus = new();
            AiGameCommandRouter oldRouter = new(oldBus, new NoOpGameLogger(), new NullWorldExecutor());
            CurrentThreadPublishBus newBus = new();
            AiGameCommandRouter newRouter = null;

            int staleCalls = 0;
            bool freshReceived = false;

            void OnStaleCommand(ApplyAiGameCommand _)
            {
                staleCalls++;
            }

            void OnFreshCommand(ApplyAiGameCommand _)
            {
                freshReceived = true;
            }

            try
            {
                oldRouter.Start();
                AiGameCommandRouter.CommandReceived += OnStaleCommand;

                // Scene reload: the old scope tears down while its subscriber never unsubscribed.
                oldRouter.Dispose();

                newRouter = new AiGameCommandRouter(newBus, new NoOpGameLogger(), new NullWorldExecutor());
                newRouter.Start();
                AiGameCommandRouter.CommandReceived += OnFreshCommand;

                newBus.Publish(SampleEnvelope());

                float deadline = Time.realtimeSinceStartup + 8f;
                while (!freshReceived && Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                }

                Assert.IsTrue(freshReceived,
                    "A subscriber attached after the new router started must receive the command.");
                Assert.AreEqual(0, staleCalls,
                    "A subscriber left over from a disposed router's scope must not receive commands routed by the next scope.");
            }
            finally
            {
                AiGameCommandRouter.CommandReceived -= OnStaleCommand;
                AiGameCommandRouter.CommandReceived -= OnFreshCommand;
                oldRouter.Dispose();
                newRouter?.Dispose();
            }
        }

        /// <summary>
        /// Verifies that disposing one router does not clear subscribers while another scope's router remains alive.
        /// </summary>
        [UnityTest]
        public IEnumerator Router_Dispose_OldScope_PreservesSubscriberFromCoexistingScope()
        {
            yield return null;

            CurrentThreadPublishBus oldBus = new();
            AiGameCommandRouter oldRouter = new(oldBus, new NoOpGameLogger(), new NullWorldExecutor());
            CurrentThreadPublishBus newBus = new();
            AiGameCommandRouter newRouter = new(newBus, new NoOpGameLogger(), new NullWorldExecutor());
            bool newScopeReceived = false;

            void OnNewScopeCommand(ApplyAiGameCommand _)
            {
                newScopeReceived = true;
            }

            try
            {
                oldRouter.Start();
                newRouter.Start();
                AiGameCommandRouter.CommandReceived += OnNewScopeCommand;

                oldRouter.Dispose();
                newBus.Publish(SampleEnvelope());

                float deadline = Time.realtimeSinceStartup + 8f;
                while (!newScopeReceived && Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                }

                Assert.IsTrue(newScopeReceived,
                    "Disposing the old router must not remove a subscriber owned by a coexisting live scope.");
            }
            finally
            {
                AiGameCommandRouter.CommandReceived -= OnNewScopeCommand;
                oldRouter.Dispose();
                newRouter.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator Pipeline_QueuedOrchestrator_Publish_CommandReceived_OnMainThread()
        {
            yield return null;

            int mainThreadId = Thread.CurrentThread.ManagedThreadId;

            CurrentThreadPublishBus bus = new();
            MessagePipeAiCommandSink mpSink = new(bus);
            SoloAuthorityHost host = new();
            SessionTelemetryCollector telemetry = new();
            AiPromptComposer composer = new(
                new BuiltInDefaultAgentSystemPromptProvider(),
                new NoAgentUserPromptTemplateProvider(),
                new NullLuaScriptVersionStore());
            AgentMemoryPolicy memPolicy = new();
            string creator = BuiltInAgentRoleIds.Creator;
            AgentConfig toolMemCfg = new AgentBuilder(creator)
                .WithMode(AgentMode.ToolsAndChat)
                .WithMemory(MemoryToolAction.Append)
                .Build();
            toolMemCfg.ApplyToPolicy(memPolicy);
            AiOrchestrator inner = new(
                host,
                new StubLlmClient(),
                mpSink,
                telemetry,
                composer,
                new NullAgentMemoryStore(),
                memPolicy,
                new NoOpRoleStructuredResponsePolicy(),
                new NullAiOrchestrationMetrics(),
                ScriptableObject.CreateInstance<Infrastructure.Llm.CoreAISettingsAsset>());
            QueuedAiOrchestrator queued = new(inner, new AiOrchestrationQueueOptions { MaxConcurrent = 2 });

            AiGameCommandRouter router = new(bus, new NoOpGameLogger(), new NullWorldExecutor());

            bool received = false;
            int receivedThreadId = -1;

            void OnCommandReceived(ApplyAiGameCommand _)
            {
                receivedThreadId = Thread.CurrentThread.ManagedThreadId;
                received = true;
            }

            AiGameCommandRouter.CommandReceived += OnCommandReceived;
            try
            {
                router.Start();

                Task run = queued.RunTaskAsync(new AiTaskRequest
                {
                    RoleId = BuiltInAgentRoleIds.Creator,
                    Hint = "pipeline_main_thread_test"
                });

                float deadline = Time.realtimeSinceStartup + 15f;
                while (!run.IsCompleted && Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                }

                Assert.IsTrue(run.IsCompleted, "      .");
                Assert.IsFalse(run.IsFaulted, run.Exception?.ToString());

                deadline = Time.realtimeSinceStartup + 8f;
                while (!received && Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                }

                Assert.IsTrue(received, "CommandReceived       .");
                Assert.AreEqual(
                    mainThreadId,
                    receivedThreadId,
                    " QueuedAiOrchestrator  CommandReceived     .");
            }
            finally
            {
                AiGameCommandRouter.CommandReceived -= OnCommandReceived;
                router.Dispose();
            }
        }
    }
}
