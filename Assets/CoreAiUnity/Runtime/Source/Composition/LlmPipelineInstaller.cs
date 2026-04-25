using CoreAI.Ai;
using CoreAI.Infrastructure.Ai;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Llm;
#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL
using LLMUnity;
#endif
using UnityEngine;
using VContainer;

namespace CoreAI.Composition
{
    /// <summary>
    /// Регистрация LLM pipeline: клиент, маршрутизация, декоратор логирования, метрики оркестратора.
    /// </summary>
    public static class LlmPipelineInstaller
    {
        /// <summary>
        /// Регистрирует <see cref="ILlmClient"/>, <see cref="ILlmClientRegistry"/>,
        /// <see cref="ILlmAgentProvider"/>, очередь оркестратора и метрики.
        /// </summary>
        public static void RegisterLlmPipeline(
            this IContainerBuilder builder,
            CoreAISettingsAsset settings,
            LlmRoutingManifest routingManifest)
        {
            float llmTimeout = settings != null ? settings.LlmRequestTimeoutSeconds : 15f;

            // Lazy provider вместо FindFirstObjectByType в composition root
            builder.Register<SceneLlmAgentProvider>(Lifetime.Singleton).As<ILlmAgentProvider>();

            builder.Register(c =>
            {
                LlmClientRegistry reg = new(c.Resolve<IGameLogger>(), settings);
                reg.SetLegacyFallback(
                    ResolveLlmClient(settings, c.Resolve<IGameLogger>(), c.Resolve<IAgentMemoryStore>(),
                        c.Resolve<ILlmAgentProvider>()));
                reg.ApplyManifest(routingManifest);
                return reg;
            }, Lifetime.Singleton).As<ILlmClientRegistry>().As<ILlmRoutingController>();

            builder.Register<ILlmClient>(c =>
                new LoggingLlmClientDecorator(
                    new RoutingLlmClient(c.Resolve<ILlmClientRegistry>()),
                    c.Resolve<IGameLogger>(),
                    llmTimeout), Lifetime.Singleton);

            // Orchestrator настройки
            int maxConcurrent = settings != null ? settings.MaxConcurrentOrchestrations : 2;
            builder.RegisterInstance(new AiOrchestrationQueueOptions
            {
                MaxConcurrent = maxConcurrent < 1 ? 1 : maxConcurrent
            });

            bool logMetrics = settings != null && settings.LogOrchestrationMetrics;
            if (logMetrics)
            {
                builder.Register<IAiOrchestrationMetrics>(c =>
                        new LoggingAiOrchestrationMetrics(c.Resolve<IGameLogger>(), c.Resolve<IGameLogSettings>()),
                    Lifetime.Singleton);
            }
            else
            {
                builder.Register<IAiOrchestrationMetrics, NullAiOrchestrationMetrics>(Lifetime.Singleton);
            }
        }

        /// <summary>
        /// Порядок выбора: CoreAISettingsAsset (Auto/LlmUnity/OpenAiHttp/NoLlm) → LLMUnity → Stub.
        /// </summary>
        internal static ILlmClient ResolveLlmClient(
            CoreAISettingsAsset settings,
            IGameLogger logger,
            IAgentMemoryStore memoryStore,
            ILlmAgentProvider agentProvider)
        {
#if UNITY_WEBGL
            // WebGL: no local native LLMUnity backend. Allow HTTP/Offline only.
            if (settings != null && settings.BackendType == LlmBackendType.OpenAiHttp)
            {
                return new OpenAiChatLlmClient(settings);
            }

            if (settings != null && settings.BackendType == LlmBackendType.Offline)
            {
                return new OfflineLlmClient(settings);
            }

            // Auto/LlmUnity/default → Stub in WebGL
            return new StubLlmClient();
#endif
#if COREAI_NO_LLM
            // Build without any external LLM dependencies (HTTP / LLMUnity).
            // Keep the pipeline alive for UI smoke tests.
            if (settings != null && settings.BackendType == LlmBackendType.Offline)
            {
                return new OfflineLlmClient(settings);
            }

            return new StubLlmClient();
#else
            if (settings != null)
            {
                switch (settings.BackendType)
                {
                    case LlmBackendType.OpenAiHttp:
                        return new OpenAiChatLlmClient(settings);
                    case LlmBackendType.Offline:
                        return new OfflineLlmClient(settings);
                    case LlmBackendType.Auto:
                        return TryResolveAutoClient(settings, logger, memoryStore, agentProvider);
                    case LlmBackendType.LlmUnity:
                        return ResolveLlmUnityClient(settings, logger, memoryStore, agentProvider);
                }
            }

#if COREAI_HAS_LLMUNITY
            return ResolveLlmUnityClient(settings, logger, memoryStore, agentProvider);
#else
            return new StubLlmClient();
#endif
#endif
        }

        private static ILlmClient TryResolveAutoClient(
            CoreAISettingsAsset settings,
            IGameLogger logger,
            IAgentMemoryStore memoryStore,
            ILlmAgentProvider agentProvider)
        {
#if UNITY_WEBGL
            // WebGL: try HTTP only, otherwise Offline.
            ILlmClient http = TryResolveHttpApiClient(settings);
            return http ?? new OfflineLlmClient(settings);
#else
            bool httpFirst = settings != null && settings.AutoPriority == LlmAutoPriority.HttpFirst;

            if (httpFirst)
            {
                ILlmClient httpClient = TryResolveHttpApiClient(settings);
                if (httpClient != null) return httpClient;

                ILlmClient llmUnityClient = TryResolveLlmUnityClient(settings, logger, memoryStore, agentProvider);
                if (llmUnityClient != null) return llmUnityClient;

                return new OfflineLlmClient(settings);
            }
            else
            {
                ILlmClient llmUnityClient = TryResolveLlmUnityClient(settings, logger, memoryStore, agentProvider);
                if (llmUnityClient != null) return llmUnityClient;

                ILlmClient httpClient2 = TryResolveHttpApiClient(settings);
                if (httpClient2 != null) return httpClient2;

                return new OfflineLlmClient(settings);
            }
#endif
        }

        private static ILlmClient TryResolveHttpApiClient(CoreAISettingsAsset settings)
        {
#if COREAI_NO_LLM
            return null;
#else
            if (settings != null && settings.UseHttpApi && !string.IsNullOrEmpty(settings.ApiBaseUrl) &&
                !string.IsNullOrEmpty(settings.ModelName))
            {
                return new OpenAiChatLlmClient(settings);
            }

            return null;
#endif
        }

        private static ILlmClient TryResolveLlmUnityClient(
            CoreAISettingsAsset settings,
            IGameLogger logger,
            IAgentMemoryStore memoryStore,
            ILlmAgentProvider agentProvider)
        {
#if !COREAI_HAS_LLMUNITY || UNITY_WEBGL
            return null;
#else
            LLMAgent agent = agentProvider?.Resolve(settings?.LlmUnityAgentName);
            if (agent == null) return null;

            LLM llm = agent.GetComponent<LLM>();
            if (llm != null && settings != null && settings.LlmUnityDontDestroyOnLoad)
            {
                llm.dontDestroyOnLoad = true;
            }

            if (llm != null)
            {
                LlmUnityModelBootstrap.TryAutoAssignResolvableModel(llm, logger);
            }

            if (llm != null && string.IsNullOrWhiteSpace(llm.model))
            {
                return null;
            }

            return new MeaiLlmUnityClient(agent, settings, logger, memoryStore);
#endif
        }

        private static ILlmClient ResolveLlmUnityClient(
            CoreAISettingsAsset settings,
            IGameLogger logger,
            IAgentMemoryStore memoryStore,
            ILlmAgentProvider agentProvider)
        {
            ILlmClient client = TryResolveLlmUnityClient(settings, logger, memoryStore, agentProvider);
            return client ?? new StubLlmClient();
        }
    }
}
