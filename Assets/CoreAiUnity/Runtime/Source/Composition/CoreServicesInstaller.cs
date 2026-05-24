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
    ///
    /// MessagePipe + <see cref="GlobalMessagePipe"/>.
    ///
    /// </summary>
    public static class CoreServicesInstaller
    {
        /// <summary>Registers CoreAI domain services with the dependency injection container.</summary>
        public static void RegisterCore(this IContainerBuilder builder)
        {
            builder.Register<UnityGameLogSink>(Lifetime.Singleton);
            builder.Register<FilteringGameLogger>(Lifetime.Singleton).As<IGameLogger>();

            // No-op guard before a conditional operation.
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
                // No-op guard before a conditional operation.
                Log.Instance = resolver.Resolve<ILog>();
                GlobalMessagePipe.SetProvider(resolver.AsServiceProvider());
            });
        }
    }
}
