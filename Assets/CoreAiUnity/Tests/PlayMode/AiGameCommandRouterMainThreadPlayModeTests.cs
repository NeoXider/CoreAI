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
    /// Play Mode: проверка, что <see cref="AiGameCommandRouter"/> доставляет
    /// <see cref="AiGameCommandRouter.CommandReceived"/> на главный поток Unity, даже если MessagePipe
    /// вызывает подписчика с пула потоков (как после <c>ConfigureAwait(false)</c> в <see cref="QueuedAiOrchestrator"/>).
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
        /// Минимальный pub/sub: <see cref="Publish"/> вызывает обработчик в том же потоке, что и вызов
        /// (как <see cref="MessageBrokerCore{TMessage}.Publish"/>), без DI MessagePipe.
        /// </summary>
        private sealed class CurrentThreadPublishBus : IPublisher<ApplyAiGameCommand>, ISubscriber<ApplyAiGameCommand>
        {
            private IMessageHandler<ApplyAiGameCommand> _handler;

            public void Publish(ApplyAiGameCommand message) => _handler?.Handle(message);

            public IDisposable Subscribe(IMessageHandler<ApplyAiGameCommand> handler,
                params MessageHandlerFilter<ApplyAiGameCommand>[] filters)
            {
                _handler = handler;
                return new Unsubscribe(() => _handler = null);
            }

            private sealed class Unsubscribe : IDisposable
            {
                private Action _onDispose;

                public Unsubscribe(Action onDispose) => _onDispose = onDispose;

                public void Dispose() => Interlocked.Exchange(ref _onDispose, null)?.Invoke();
            }
        }

        /// <summary>Имитирует доставку сообщения шины с пула (как после async без возврата на main thread).</summary>
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
                var h = _handler ?? throw new InvalidOperationException("Subscribe before DeliverFromThreadPool.");
                _ = Task.Run(() => h.Handle(cmd));
            }

            private sealed class Unsubscribe : IDisposable
            {
                private Action _onDispose;

                public Unsubscribe(Action onDispose) => _onDispose = onDispose;

                public void Dispose() => Interlocked.Exchange(ref _onDispose, null)?.Invoke();
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

        private sealed class NullWorldExecutor : CoreAI.Infrastructure.World.ICoreAiWorldCommandExecutor
        {
            public bool TryExecute(ApplyAiGameCommand cmd) => false;
        }

        [UnityTest]
        public IEnumerator Router_CommandReceived_OnMainThread_WhenSubscribeInvokedFromThreadPool()
        {
            yield return null;

            var mainThreadId = Thread.CurrentThread.ManagedThreadId;

            var subscriber = new ThreadPoolDeliverySubscriber();
            var lua = new LuaAiEnvelopeProcessor(
                new SecureLuaEnvironment(),
                new EmptyLuaBindings(),
                new ListCommandSink(),
                () => null,
                new NullLuaExecutionObserver(),
                new NullLuaScriptVersionStore());
            var router = new AiGameCommandRouter(subscriber, new NoOpGameLogger(), lua, new NullWorldExecutor());

            var received = false;
            var receivedThreadId = -1;
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

                var deadline = Time.realtimeSinceStartup + 8f;
                while (!received && Time.realtimeSinceStartup < deadline)
                    yield return null;

                Assert.IsTrue(received, "CommandReceived не вызван за отведённое время.");
                Assert.AreEqual(
                    mainThreadId,
                    receivedThreadId,
                    "CommandReceived должен выполняться на главном потоке Unity после SwitchToMainThread.");
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

            var mainThreadId = Thread.CurrentThread.ManagedThreadId;

            var bus = new CurrentThreadPublishBus();
            var mpSink = new MessagePipeAiCommandSink(bus);
            var host = new SoloAuthorityHost();
            var telemetry = new SessionTelemetryCollector();
            var composer = new AiPromptComposer(
                new BuiltInDefaultAgentSystemPromptProvider(),
                new NoAgentUserPromptTemplateProvider(),
                new NullLuaScriptVersionStore());
            var inner = new AiOrchestrator(
                host,
                new StubLlmClient(),
                mpSink,
                telemetry,
                composer,
                new NullAgentMemoryStore(),
                new AgentMemoryPolicy(),
                new NoOpRoleStructuredResponsePolicy(),
                new NullAiOrchestrationMetrics());
            var queued = new QueuedAiOrchestrator(inner, new AiOrchestrationQueueOptions { MaxConcurrent = 2 });

            var lua = new LuaAiEnvelopeProcessor(
                new SecureLuaEnvironment(),
                new EmptyLuaBindings(),
                new ListCommandSink(),
                () => queued,
                new NullLuaExecutionObserver(),
                new NullLuaScriptVersionStore());
            var router = new AiGameCommandRouter(bus, new NoOpGameLogger(), lua, new NullWorldExecutor());

            var received = false;
            var receivedThreadId = -1;
            void OnCommandReceived(ApplyAiGameCommand _)
            {
                receivedThreadId = Thread.CurrentThread.ManagedThreadId;
                received = true;
            }

            AiGameCommandRouter.CommandReceived += OnCommandReceived;
            try
            {
                router.Start();

                var run = queued.RunTaskAsync(new AiTaskRequest
                {
                    RoleId = BuiltInAgentRoleIds.Creator,
                    Hint = "pipeline_main_thread_test"
                });

                var deadline = Time.realtimeSinceStartup + 15f;
                while (!run.IsCompleted && Time.realtimeSinceStartup < deadline)
                    yield return null;

                Assert.IsTrue(run.IsCompleted, "Оркестратор не завершил задачу за отведённое время.");
                Assert.IsFalse(run.IsFaulted, run.Exception?.ToString());

                deadline = Time.realtimeSinceStartup + 8f;
                while (!received && Time.realtimeSinceStartup < deadline)
                    yield return null;

                Assert.IsTrue(received, "CommandReceived не вызван после публикации из очереди оркестратора.");
                Assert.AreEqual(
                    mainThreadId,
                    receivedThreadId,
                    "После QueuedAiOrchestrator колбэк CommandReceived должен быть на главном потоке.");
            }
            finally
            {
                AiGameCommandRouter.CommandReceived -= OnCommandReceived;
                router.Dispose();
            }
        }
    }
}
