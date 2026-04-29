using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Messaging;
using CoreAI.Logging;
using CoreAI.Messaging;
using CoreAI.Unity.Logging;
using MessagePipe;
using MessagePipe.VContainer;
using VContainer;

namespace CoreAI.Composition
{
    /// <summary>
    /// Регистрация инфраструктуры: единый логгер (<see cref="ILog"/> + <see cref="IGameLogger"/>),
    /// MessagePipe + <see cref="GlobalMessagePipe"/>.
    /// Перед вызовом в контейнере должен быть зарегистрирован <see cref="IGameLogSettings"/>.
    /// </summary>
    public static class CoreServicesInstaller
    {
        /// <summary>Логгер Unity, брокер <see cref="CoreAI.Messaging.ApplyAiGameCommand"/>, глобальный провайдер MessagePipe.</summary>
        public static void RegisterCore(this IContainerBuilder builder)
        {
            builder.Register<UnityGameLogSink>(Lifetime.Singleton);
            builder.Register<FilteringGameLogger>(Lifetime.Singleton).As<IGameLogger>();

            // Единый логгер: ILog (DI) + Log.Instance (статика)
            builder.Register<UnityLog>(Lifetime.Singleton).As<ILog>();

            MessagePipeOptions opts = builder.RegisterMessagePipe();
            builder.RegisterMessageBroker<ApplyAiGameCommand>(opts);
            builder.RegisterMessageBroker<LlmBackendSelected>(opts);
            builder.RegisterMessageBroker<LlmRequestStarted>(opts);
            builder.RegisterMessageBroker<LlmRequestCompleted>(opts);
            builder.RegisterMessageBroker<LlmUsageReported>(opts);
            builder.RegisterMessageBroker<LlmToolCallStarted>(opts);
            builder.RegisterMessageBroker<LlmToolCallCompleted>(opts);
            builder.RegisterMessageBroker<LlmToolCallFailed>(opts);

            // WebGL/IL2CPP: VContainer's TypeAnalyzer may fail on constructor metadata for
            // MessagePipeAiCommandSink; explicit factory matches QueuedAiOrchestrator registration.
            builder.Register<IAiGameCommandSink>(c =>
                    new MessagePipeAiCommandSink(c.Resolve<IPublisher<ApplyAiGameCommand>>()),
                Lifetime.Singleton);

            builder.RegisterBuildCallback(static resolver =>
            {
                // Устанавливаем статический логгер для удобного доступа из Core
                Log.Instance = resolver.Resolve<ILog>();
                GlobalMessagePipe.SetProvider(resolver.AsServiceProvider());
            });
        }
    }
}