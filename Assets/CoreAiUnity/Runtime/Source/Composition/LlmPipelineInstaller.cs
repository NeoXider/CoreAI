using CoreAI.Ai;
using CoreAI.Infrastructure.Ai;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Llm;
using CoreAI.Messaging;
using MessagePipe;
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
                    new RoutingLlmClient(
                        c.Resolve<ILlmClientRegistry>(),
                        c.Resolve<IPublisher<LlmBackendSelected>>(),
                        c.Resolve<IPublisher<LlmRequestStarted>>(),
                        c.Resolve<IPublisher<LlmRequestCompleted>>(),
                        c.Resolve<IPublisher<LlmUsageReported>>()),
                    c.Resolve<IGameLogger>(),
                    llmTimeout,
                    settings != null ? settings.MaxLlmRequestRetries : 0), Lifetime.Singleton);

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
        /// Resolves the global fallback LLM client from the configured execution mode.
        /// </summary>
        internal static ILlmClient ResolveLlmClient(
            CoreAISettingsAsset settings,
            IGameLogger logger,
            IAgentMemoryStore memoryStore,
            ILlmAgentProvider agentProvider)
        {
#if UNITY_WEBGL
            LlmExecutionMode webGlMode = settings != null ? settings.ExecutionMode : LlmExecutionMode.Auto;
            if (IsHttpMode(webGlMode))
            {
                return BuildHttpClient(settings, webGlMode, memoryStore);
            }

            if (webGlMode == LlmExecutionMode.Offline)
            {
                return BuildOfflineClient(settings);
            }

            return TryResolveHttpApiClient(settings, LlmExecutionMode.Auto, memoryStore) ?? BuildOfflineClient(settings);
#endif
#if COREAI_NO_LLM
            if (settings != null && settings.ExecutionMode == LlmExecutionMode.Offline)
            {
                return BuildOfflineClient(settings);
            }

            return new StubLlmClient();
#else
            if (settings != null)
            {
                switch (settings.ExecutionMode)
                {
                    case LlmExecutionMode.ClientOwnedApi:
                    case LlmExecutionMode.ClientLimited:
                    case LlmExecutionMode.ServerManagedApi:
                        return BuildHttpClient(settings, settings.ExecutionMode, memoryStore);
                    case LlmExecutionMode.Offline:
                        return BuildOfflineClient(settings);
                    case LlmExecutionMode.Auto:
                        return TryResolveAutoClient(settings, logger, memoryStore, agentProvider);
                    case LlmExecutionMode.LocalModel:
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
            ILlmClient http = TryResolveHttpApiClient(settings, LlmExecutionMode.Auto, memoryStore);
            return http ?? BuildOfflineClient(settings);
#else
            bool httpFirst = settings != null && settings.AutoPriority == LlmAutoPriority.HttpFirst;

            if (httpFirst)
            {
                ILlmClient httpClient = TryResolveHttpApiClient(settings, LlmExecutionMode.Auto, memoryStore);
                if (httpClient != null) return httpClient;

                ILlmClient llmUnityClient = TryResolveLlmUnityClient(settings, logger, memoryStore, agentProvider);
                if (llmUnityClient != null) return llmUnityClient;

                return BuildOfflineClient(settings);
            }
            else
            {
                ILlmClient llmUnityClient = TryResolveLlmUnityClient(settings, logger, memoryStore, agentProvider);
                if (llmUnityClient != null) return llmUnityClient;

                ILlmClient httpClient2 = TryResolveHttpApiClient(settings, LlmExecutionMode.Auto, memoryStore);
                if (httpClient2 != null) return httpClient2;

                return BuildOfflineClient(settings);
            }
#endif
        }

        private static ILlmClient TryResolveHttpApiClient(CoreAISettingsAsset settings, LlmExecutionMode mode, IAgentMemoryStore memoryStore = null)
        {
#if COREAI_NO_LLM
            return null;
#else
            if (settings != null && !string.IsNullOrEmpty(settings.ApiBaseUrl) &&
                !string.IsNullOrEmpty(settings.ModelName))
            {
                return BuildHttpClient(settings, mode == LlmExecutionMode.Auto ? settings.ExecutionMode : mode, memoryStore);
            }

            return null;
#endif
        }

        internal static ILlmClient BuildHttpClient(CoreAISettingsAsset settings, LlmExecutionMode mode, IAgentMemoryStore memoryStore = null)
        {
#if COREAI_NO_LLM
            return new StubLlmClient();
#else
            if (mode == LlmExecutionMode.ServerManagedApi)
            {
                return new ServerManagedLlmClient(
                    new ServerManagedCoreSettingsAdapter(settings),
                    settings,
                    GameLoggerUnscopedFallback.Instance,
                    memoryStore);
            }

            ILlmClient client = new OpenAiChatLlmClient(settings, memoryStore);
            return mode == LlmExecutionMode.ClientLimited
                ? new ClientLimitedLlmClientDecorator(
                    client,
                    settings != null ? settings.MaxClientLimitedRequestsPerSession : 0,
                    settings != null ? settings.MaxClientLimitedPromptChars : 0)
                : client;
#endif
        }

        internal static bool IsHttpMode(LlmExecutionMode mode)
        {
            return mode == LlmExecutionMode.ClientOwnedApi ||
                   mode == LlmExecutionMode.ClientLimited ||
                   mode == LlmExecutionMode.ServerManagedApi;
        }

        private static ILlmClient BuildOfflineClient(CoreAISettingsAsset settings)
        {
            return settings != null ? new OfflineLlmClient(settings) : new StubLlmClient();
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

#if !COREAI_NO_LLM
        private sealed class ServerManagedCoreSettingsAdapter : IOpenAiHttpSettings
        {
            private readonly CoreAISettingsAsset _settings;

            public ServerManagedCoreSettingsAdapter(CoreAISettingsAsset settings)
            {
                _settings = settings;
            }

            public string ApiBaseUrl => _settings.ApiBaseUrl;
            public string ApiKey => _settings.ApiKey;
            public string AuthorizationHeader => "";
            public string Model => _settings.ModelName;
            public float Temperature => _settings.Temperature;
            public int RequestTimeoutSeconds => _settings.RequestTimeoutSeconds;
            public int MaxTokens => _settings.MaxTokens;
            public bool LogLlmInput => _settings.LogLlmInput;
            public bool LogLlmOutput => _settings.LogLlmOutput;
            public bool EnableHttpDebugLogging => _settings.EnableHttpDebugLogging;
        }
#endif
    }
}
