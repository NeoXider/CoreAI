using CoreAI.Infrastructure.Logging;
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

            builder.RegisterMessagePipe();

            builder.RegisterBuildCallback(static resolver =>
            {
                GlobalMessagePipe.SetProvider(resolver.AsServiceProvider());
            });
        }
    }
}
