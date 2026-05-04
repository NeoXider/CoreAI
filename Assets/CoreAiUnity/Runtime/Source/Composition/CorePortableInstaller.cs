using System;
using CoreAI.Ai;
using CoreAI;
using CoreAI.Authority;
using CoreAI.Config;
using CoreAI.Messaging;
using CoreAI.Session;
using CoreAI.Sandbox;
using VContainer;

namespace CoreAI.Composition
{
    /// <summary>
    /// Registers portable orchestration services (CoreAI.Core).
    /// Registers <see cref="InMemoryConversationSummaryStore"/> as <see cref="IConversationSummaryStore"/> by default so
    /// deterministic context compaction persists across turns for each role until the host overrides it or clears summaries.
    /// Pass <paramref name="suppressDefaultConversationSummaryStore"/> when the host registers its own implementation first
    /// (Unity <see cref="CoreAILifetimeScope"/> uses <see cref="FileConversationSummaryStore"/> this way).
    /// Pass <paramref name="suppressDefaultAgentMemoryStore"/> when the host registers <see cref="IAgentMemoryStore"/>
    /// (e.g. <see cref="CoreAI.Infrastructure.AiMemory.FileAgentMemoryStore"/> on all Unity players, including WebGL)
    /// after <c>RegisterCorePortable</c> — otherwise VContainer sees two singletons for the same contract.
    /// </summary>
    public static class CorePortableInstaller
    {
        /// <summary>
        /// Регистрация портабельных сервисов оркестрации. По умолчанию включает
        /// <see cref="InMemoryConversationSummaryStore"/> — накопление свёрток истории между ходами;
        /// для Unity с файловым store вызовите с <paramref name="suppressDefaultConversationSummaryStore"/><c>true</c>
        /// после регистрации своего <see cref="IConversationSummaryStore"/>.
        /// Если хост регистрирует свой <see cref="IAgentMemoryStore"/> (как <see cref="CoreAILifetimeScope"/>),
        /// передайте <paramref name="suppressDefaultAgentMemoryStore"/><c>true</c>, иначе VContainer увидит два singleton
        /// на один контракт.
        /// </summary>
        public static void RegisterCorePortable(this IContainerBuilder builder,
            bool suppressDefaultConversationSummaryStore = false,
            bool suppressDefaultAgentMemoryStore = false)
        {
            if (!suppressDefaultConversationSummaryStore)
            {
                builder.Register<InMemoryConversationSummaryStore>(Lifetime.Singleton).As<IConversationSummaryStore>();
            }
            builder.Register<SecureLuaEnvironment>(Lifetime.Singleton);
            builder.Register<Func<IAiOrchestrationService>>(c =>
            {
                IObjectResolver r = c;
                return () => r.Resolve<IAiOrchestrationService>();
            }, Lifetime.Singleton);
            builder.Register<LuaAiEnvelopeProcessor>(Lifetime.Singleton);

            builder.Register<SessionTelemetryCollector>(Lifetime.Singleton).As<ISessionTelemetryProvider>();
            builder.Register<NullLuaScriptVersionStore>(Lifetime.Singleton).As<ILuaScriptVersionStore>();
            builder.Register<NullDataOverlayVersionStore>(Lifetime.Singleton).As<IDataOverlayVersionStore>();
            builder.Register<AiPromptComposer>(Lifetime.Singleton);
            builder.Register<AgentMemoryPolicy>(Lifetime.Singleton);
            builder.Register<DefaultAgentMemoryScopeProvider>(Lifetime.Singleton).As<IAgentMemoryScopeProvider>();
            builder.Register<DefaultContextBudgetPolicy>(Lifetime.Singleton).As<IContextBudgetPolicy>();
            builder.Register<HeuristicTokenEstimator>(Lifetime.Singleton).As<ITokenEstimator>();
            builder.Register<DefaultConversationCompactionCoordinator>(Lifetime.Singleton).As<IConversationCompactionCoordinator>();

            builder.Register<IConversationContextManager>(c =>
                    ConversationContextManagerFactories.Create(
                        c.Resolve<ICoreAISettings>().EnableLlmContextCompaction,
                        c.Resolve<IConversationSummaryStore>(),
                        c.Resolve<ITokenEstimator>(),
                        c.Resolve<ILlmClient>(),
                        null),
                Lifetime.Singleton);
            builder.Register<NullLlmUsageSink>(Lifetime.Singleton).As<ILlmUsageSink>();
            builder.Register<AllowAllLlmEntitlementPolicy>(Lifetime.Singleton).As<ILlmEntitlementPolicy>();
            builder.Register<InMemoryLlmToolCallHistory>(Lifetime.Singleton).As<ILlmToolCallHistory>();
            builder.Register<NullAgentTurnTraceSink>(Lifetime.Singleton).As<IAgentTurnTraceSink>();
            if (!suppressDefaultAgentMemoryStore)
            {
                builder.Register<NullAgentMemoryStore>(Lifetime.Singleton).As<IAgentMemoryStore>();
            }

            builder.Register<CompositeRoleStructuredResponsePolicy>(Lifetime.Singleton);
            builder.Register<IRoleStructuredResponsePolicy>(c => c.Resolve<CompositeRoleStructuredResponsePolicy>(),
                Lifetime.Singleton);
            builder.Register<NullGameConfigStore>(Lifetime.Singleton).As<IGameConfigStore>();
            builder.Register<GameConfigPolicy>(Lifetime.Singleton);
            builder.Register<AiOrchestrator>(Lifetime.Singleton);
            builder.Register<IAiOrchestrationService>(c =>
                    new QueuedAiOrchestrator(c.Resolve<AiOrchestrator>(), c.Resolve<AiOrchestrationQueueOptions>()),
                Lifetime.Singleton);
            builder.Register<InGameLlmChatService>(Lifetime.Singleton).As<IInGameLlmChatService>();
        }
    }
}