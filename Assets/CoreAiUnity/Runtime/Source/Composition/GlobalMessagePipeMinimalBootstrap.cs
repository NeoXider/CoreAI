using CoreAI.Infrastructure.Llm;
using CoreAI.Messaging;
using MessagePipe;
using MessagePipe.VContainer;
using VContainer;

namespace CoreAI.Composition
{
    /// <summary>
    /// Поднимает <see cref="GlobalMessagePipe"/> с брокерами LLM/tool событий, если провайдер ещё не задан.
    /// Нужно для кода (например <see cref="ToolExecutionPolicy"/>), который публикует
    /// <see cref="LlmToolCallCompleted"/> только через <see cref="GlobalMessagePipe"/>:
    /// без провайдера публикации тихо пропускаются.
    /// </summary>
    /// <remarks>
    /// Полноценная игра вызывает <see cref="CoreServicesInstaller.RegisterCore"/> — там же выставляется провайдер.
    /// Минимальные PlayMode/EditMode сетапы без <c>CoreAILifetimeScope</c> могут вызвать
    /// <see cref="EnsureInitializedForLlmDiagnostics"/> один раз на процесс.
    /// Смена провайдера — при следующем полном <c>RegisterCore</c> / перезагрузке домена Unity.
    /// </remarks>
    public static class GlobalMessagePipeMinimalBootstrap
    {
        /// <summary>
        /// Регистрирует те же брокеры LLM/tool, что <see cref="CoreServicesInstaller.RegisterCore"/> для шины событий
        /// (без логгеров и <see cref="ApplyAiGameCommand"/>).
        /// </summary>
        public static void EnsureInitializedForLlmDiagnostics()
        {
            if (GlobalMessagePipe.IsInitialized)
            {
                return;
            }

            var builder = new ContainerBuilder();
            MessagePipeOptions opts = builder.RegisterMessagePipe();
            builder.RegisterMessageBroker<LlmBackendSelected>(opts);
            builder.RegisterMessageBroker<LlmRequestStarted>(opts);
            builder.RegisterMessageBroker<LlmRequestCompleted>(opts);
            builder.RegisterMessageBroker<LlmUsageReported>(opts);
            builder.RegisterMessageBroker<LlmToolCallStarted>(opts);
            builder.RegisterMessageBroker<LlmToolCallCompleted>(opts);
            builder.RegisterMessageBroker<LlmToolCallFailed>(opts);

            IObjectResolver resolver = builder.Build();
            GlobalMessagePipe.SetProvider(resolver.AsServiceProvider());
        }
    }
}
