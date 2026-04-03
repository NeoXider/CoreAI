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
        /// <summary>
        /// Регистрирует оркестратор, очередь, песочницу Lua, телеметрию, память и чат — вызывать из Unity scope после инфраструктуры.
        /// </summary>
        public static void RegisterCorePortable(this IContainerBuilder builder)
        {
            builder.Register<SecureLuaEnvironment>(Lifetime.Singleton);
            builder.Register<Func<IAiOrchestrationService>>(c =>
            {
                var r = c;
                return () => r.Resolve<IAiOrchestrationService>();
            }, Lifetime.Singleton);
            builder.Register<LuaAiEnvelopeProcessor>(Lifetime.Singleton);

            builder.Register<SessionTelemetryCollector>(Lifetime.Singleton).As<ISessionTelemetryProvider>();
            builder.Register<NullLuaScriptVersionStore>(Lifetime.Singleton).As<ILuaScriptVersionStore>();
            builder.Register<NullDataOverlayVersionStore>(Lifetime.Singleton).As<IDataOverlayVersionStore>();
            builder.Register<AiPromptComposer>(Lifetime.Singleton);
            builder.Register<CodeRefinerStub>(Lifetime.Singleton).As<ICodeRefiner>();
            builder.Register<AgentMemoryPolicy>(Lifetime.Singleton);
            builder.Register<NullAgentMemoryStore>(Lifetime.Singleton).As<IAgentMemoryStore>();
            builder.Register<IRoleStructuredResponsePolicy, NoOpRoleStructuredResponsePolicy>(Lifetime.Singleton);
            builder.Register<AiOrchestrator>(Lifetime.Singleton);
            builder.Register<IAiOrchestrationService>(c =>
                    new QueuedAiOrchestrator(c.Resolve<AiOrchestrator>(), c.Resolve<AiOrchestrationQueueOptions>()),
                Lifetime.Singleton);
            builder.Register<InGameLlmChatService>(Lifetime.Singleton).As<IInGameLlmChatService>();
        }
    }
}
