using System;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Messaging;
using CoreAI.Messaging;
using CoreAI.Sandbox;
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

        private sealed class EmptyLuaBindings : IGameLuaRuntimeBindings
        {
            public void RegisterGameplayApis(LuaApiRegistry registry)
            {
            }
        }

        private sealed class ListCommandSink : IAiGameCommandSink
        {
            public void Publish(ApplyAiGameCommand command)
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
            public string[] LastListedAnimations { get; } = System.Array.Empty<string>();
            public System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object>> LastListedObjects { get; } = new();

            public bool TryExecute(ApplyAiGameCommand cmd)
            {
                return false;
            }
        }

        [UnityTest]
        public IEnumerator Router_CommandReceived_OnMainThread_WhenSubscribeInvokedFromThreadPool()
        {
            yield return null;

            int mainThreadId = Thread.CurrentThread.ManagedThreadId;

            ThreadPoolDeliverySubscriber subscriber = new();
            LuaAiEnvelopeProcessor lua = new(
                new SecureLuaEnvironment(),
                new EmptyLuaBindings(),
                new ListCommandSink(),
                () => null,
                new NullLuaExecutionObserver(),
                new NullLuaScriptVersionStore());
            AiGameCommandRouter router = new(subscriber, new NoOpGameLogger(), lua, new NullWorldExecutor());

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
            TestAgentPolicyDefaults.ApplyToolsAndChatWithMemory(memPolicy);
            AiOrchestrator inner = new(
                host,
                new StubLlmClient(),
                mpSink,
                telemetry,
                composer,
                new NullAgentMemoryStore(),
                memPolicy,
                new NoOpRoleStructuredResponsePolicy(),
                new NullAiOrchestrationMetrics(), UnityEngine.ScriptableObject.CreateInstance<CoreAI.Infrastructure.Llm.CoreAISettingsAsset>());
            QueuedAiOrchestrator queued = new(inner, new AiOrchestrationQueueOptions { MaxConcurrent = 2 });

            LuaAiEnvelopeProcessor lua = new(
                new SecureLuaEnvironment(),
                new EmptyLuaBindings(),
                new ListCommandSink(),
                () => queued,
                new NullLuaExecutionObserver(),
                new NullLuaScriptVersionStore());
            AiGameCommandRouter router = new(bus, new NoOpGameLogger(), lua, new NullWorldExecutor());

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


