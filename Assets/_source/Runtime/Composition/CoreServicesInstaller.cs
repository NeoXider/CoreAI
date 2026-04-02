using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Messaging;
using CoreAI.Messaging;
using MessagePipe;
using MessagePipe.VContainer;
using VContainer;

namespace CoreAI.Composition
{
    /// <summary>
    /// Регистрация инфраструктуры: логгер с фильтром по фичам, MessagePipe + <see cref="GlobalMessagePipe"/>.
    /// Перед вызовом в контейнере должен быть зарегистрирован <see cref="IGameLogSettings"/>.
    /// </summary>
    public static class CoreServicesInstaller
    {
        public static void RegisterCore(this IContainerBuilder builder)
        {
            builder.Register<UnityGameLogSink>(Lifetime.Singleton);
            builder.Register<FilteringGameLogger>(Lifetime.Singleton).As<IGameLogger>();

            var opts = builder.RegisterMessagePipe();
            builder.RegisterMessageBroker<ApplyAiGameCommand>(opts);
            builder.Register<MessagePipeAiCommandSink>(Lifetime.Singleton).As<IAiGameCommandSink>();

            builder.RegisterBuildCallback(static resolver =>
            {
                GlobalMessagePipe.SetProvider(resolver.AsServiceProvider());
            });
        }
    }
}
