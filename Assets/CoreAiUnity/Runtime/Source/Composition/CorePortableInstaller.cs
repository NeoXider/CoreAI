using System;
using CoreAI.Ai;
using CoreAI;
using CoreAI.Authority;
using CoreAI.Config;
using CoreAI.Diagnostics;
using CoreAI.Messaging;
using CoreAI.Session;
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
    /// </summary>
    public static class CorePortableInstaller
    {
        /// <summary>
        /// Registers portable CoreAI services into the host container.
        /// </summary>
        public static void RegisterCorePortable(this IContainerBuilder builder,
            bool suppressDefaultConversationSummaryStore = false,
            bool suppressDefaultAgentMemoryStore = false,
            bool suppressDefaultTokenCalibrationStore = false)
        {
            if (!suppressDefaultConversationSummaryStore)
            {
                builder.Register<InMemoryConversationSummaryStore>(Lifetime.Singleton).As<IConversationSummaryStore>();
            }

            builder.Register<Func<IAiOrchestrationService>>(c =>
            {
                IObjectResolver r = c;
                return () => r.Resolve<IAiOrchestrationService>();
            }, Lifetime.Singleton);

            builder.Register<SessionTelemetryCollector>(Lifetime.Singleton).As<ISessionTelemetryProvider>();
            // WHY: portable Null defaults must not shadow a host-registered real version store (the host
            // registers File*VersionStore before RegisterCorePortable). Guard like IGameConfigStore below —
            // unconditional registration made the Null store win the single resolve (and hard-conflict when
            // the host registered the same Null concrete type in a test).
            if (!builder.Exists(typeof(ILuaScriptVersionStore), true))
            {
                builder.Register<NullLuaScriptVersionStore>(Lifetime.Singleton).As<ILuaScriptVersionStore>();
            }

            if (!builder.Exists(typeof(IDataOverlayVersionStore), true))
            {
                builder.Register<NullDataOverlayVersionStore>(Lifetime.Singleton).As<IDataOverlayVersionStore>();
            }

            builder.Register<AiPromptComposer>(Lifetime.Singleton);
            builder.Register<AgentMemoryPolicy>(Lifetime.Singleton);
            builder.Register<AgentSessionInspector>(Lifetime.Singleton);
            // WHY: Unity and other multi-tenant hosts may register a live scope provider before the portable
            // graph is added. An unconditional default silently replaced that host boundary with role-only
            // memory, so sequential users could read the same local chat history.
            if (!builder.Exists(typeof(IAgentMemoryScopeProvider), true))
            {
                builder.Register<DefaultAgentMemoryScopeProvider>(Lifetime.Singleton)
                    .As<IAgentMemoryScopeProvider>();
            }

            builder.Register<DefaultContextBudgetPolicy>(Lifetime.Singleton).As<IContextBudgetPolicy>();
            if (!suppressDefaultTokenCalibrationStore)
            {
                builder.RegisterInstance<ITokenCalibrationStore>(NullTokenCalibrationStore.Instance);
            }

            builder.Register<CalibratingTokenEstimator>(Lifetime.Singleton)
                .As<ITokenEstimator>()
                .As<ICalibratingTokenEstimator>();
            builder.Register<DefaultConversationCompactionCoordinator>(Lifetime.Singleton)
                .As<IConversationCompactionCoordinator>();

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
            // WHY: includeInterfaceTypes — the host registers the real store via .As<IGameConfigStore>(),
            // so a registration's ImplementationType is UnityGameConfigStore, not the interface. Without
            // this flag Exists always returned false and the Null default was registered unconditionally,
            // shadowing the host store on resolve (the guard was a silent no-op).
            if (!builder.Exists(typeof(IGameConfigStore), true))
            {
                builder.Register<NullGameConfigStore>(Lifetime.Singleton).As<IGameConfigStore>();
            }

            builder.Register<GameConfigPolicy>(Lifetime.Singleton);
            builder.Register<AiOrchestrator>(Lifetime.Singleton);
            builder.Register<IAiOrchestrationService>(c =>
                    new QueuedAiOrchestrator(
                        c.Resolve<AiOrchestrator>(),
                        c.Resolve<AiOrchestrationQueueOptions>(),
                        c.Resolve<IAgentMemoryScopeProvider>()),
                Lifetime.Singleton);
            builder.Register<InGameLlmChatService>(Lifetime.Singleton).As<IInGameLlmChatService>();
        }
    }
}
