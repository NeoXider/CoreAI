using System;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Session;
using CoreAI.Sandbox;
using VContainer;

namespace CoreAI.Composition
{
    /// <summary>
    /// Регистрация портативных сервисов (сборка CoreAI.Core).
    /// </summary>
    public static class CorePortableInstaller
    {
        public static void RegisterCorePortable(this IContainerBuilder builder)
        {
            builder.Register<SecureLuaEnvironment>(Lifetime.Singleton);
            builder.Register<Func<IAiOrchestrationService>>(c =>
            {
                var r = c;
                return () => r.Resolve<IAiOrchestrationService>();
            }, Lifetime.Singleton);
            builder.Register<LuaAiEnvelopeProcessor>(Lifetime.Singleton);

            builder.Register<SoloAuthorityHost>(Lifetime.Singleton).As<IAuthorityHost>();
            builder.Register<SessionTelemetryCollector>(Lifetime.Singleton).As<ISessionTelemetryProvider>();
            builder.Register<AiPromptComposer>(Lifetime.Singleton);
            builder.Register<CodeRefinerStub>(Lifetime.Singleton).As<ICodeRefiner>();
            builder.Register<AiOrchestrator>(Lifetime.Singleton).As<IAiOrchestrationService>();
            builder.Register<InGameLlmChatService>(Lifetime.Singleton).As<IInGameLlmChatService>();
        }
    }
}
