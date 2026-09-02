using System;
using System.IO;
using CoreAI.Ai;
using CoreAI.Chat;
using CoreAI.Infrastructure.Ai;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Llm;
using CoreAI.Logging;
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
    /// Registers LLM clients, routing, tool calling, and orchestration services.
    /// </summary>
    public static class LlmPipelineInstaller
    {
        /// <summary>
        /// Registers llm pipeline.
        /// </summary>
        public static void RegisterLlmPipeline(
            this IContainerBuilder builder,
            CoreAISettingsAsset settings,
            LlmRoutingManifest routingManifest)
        {
            RegisterLlmPipelineForPlatform(builder, settings, routingManifest, Application.platform);
        }

        internal static void RegisterLlmPipelineForPlatform(
            IContainerBuilder builder,
            CoreAISettingsAsset settings,
            LlmRoutingManifest routingManifest,
            RuntimePlatform platform)
        {
            float llmTimeout = settings != null ? settings.LlmRequestTimeoutSeconds : 15f;
            ILlmAsyncMarshaler asyncMarshaler = UnityMainThreadLlmAsyncMarshaler.Instance;

#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL
            if (ShouldRegisterLocalModelProvider(platform))
            {
                builder.Register<ConfigurableLlmAgentProvider>(Lifetime.Singleton).As<ILlmAgentProvider>();
            }
#endif

#if COREAI_LLM
            builder.Register<UnityWebRequestOpenAiReadinessProbe>(Lifetime.Singleton)
                .As<ILlmEndpointReadinessProbe>();
#endif

            builder.Register(c =>
                {
#if !COREAI_LLM
                // WHY: the probe implementations are stripped with the LLM module; the endpoint
                // factory rejects HTTP/LLMUnity activation before any probe would be consulted.
                ILlmEndpointReadinessProbe readinessProbe = null;
#else
                    ILlmEndpointReadinessProbe readinessProbe = c.Resolve<ILlmEndpointReadinessProbe>();
#endif
                    LlmClientRegistry reg = new(
                        c.Resolve<IGameLogger>(),
                        settings,
                        c.Resolve<IAgentMemoryStore>(),
                        new FileLlmEndpointRegistryStore(),
                        new LlmEndpointClientFactory(
                            settings,
                            c.Resolve<IGameLogger>(),
                            c.Resolve<IAgentMemoryStore>(),
                            readinessProbe));
                    ILlmAgentProvider agentProvider = null;
#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL
                    if (ShouldRegisterLocalModelProvider(platform))
                    {
                        agentProvider = c.Resolve<ILlmAgentProvider>();
                    }
#endif
                    ILlmClient primaryClient = BuildRoutedPrimaryClientForPlatform(
                        settings,
                        c.Resolve<IGameLogger>(),
                        c.Resolve<IAgentMemoryStore>(),
                        agentProvider,
                        c.Resolve<ILog>(),
                        platform);

                    reg.SetLegacyFallback(primaryClient);
                    reg.ApplyManifest(routingManifest);
                    return reg;
                }, Lifetime.Singleton)
                .As<ILlmClientRegistry>()
                .As<ILlmRoutingController>()
                .As<ILlmEndpointRegistry>()
                .AsSelf();

            builder.Register<CoreAiRoutingUiAttachment>(Lifetime.Singleton).AsSelf();
            builder.RegisterBuildCallback(container => container.Resolve<CoreAiRoutingUiAttachment>());

            int maxRetries = settings != null ? settings.MaxLlmRequestRetries : 0;
#if !COREAI_LLM
            // WHY: the timeout/streaming-retry decorators are stripped with the LLM module; routing
            // still resolves (to stubs/offline clients), so keep logging + routing only.
            builder.Register<ILlmClient>(c =>
                new LoggingLlmClientDecorator(
                    new RoutingLlmClient(
                        c.Resolve<ILlmClientRegistry>(),
                        c.Resolve<IPublisher<LlmBackendSelected>>(),
                        c.Resolve<IPublisher<LlmRequestStarted>>(),
                        c.Resolve<IPublisher<LlmRequestCompleted>>(),
                        c.Resolve<IPublisher<LlmUsageReported>>()),
                    c.Resolve<ILog>(),
                    llmTimeout,
                    maxRetries,
                    settings == null || settings.LogLlmInput,
                    settings == null || settings.LogLlmOutput,
                    asyncMarshaler), Lifetime.Singleton);
#else
            builder.Register<ILlmClient>(c =>
                // WHY: Unity injects its PlayerLoop delay into every retry/timeout decorator so the
                // portable pipeline remains live on WebGL without System.Threading timers.
                new TimeoutLlmClientDecorator(
                    new LoggingLlmClientDecorator(
                        // WHY: Streaming-path retry (pre-commit only) — the logging decorator's HTTP retry covers
                        // the non-streaming path; this closes the streaming single-shot gap.
                        new RetryingStreamingLlmClientDecorator(
                            new RoutingLlmClient(
                                c.Resolve<ILlmClientRegistry>(),
                                c.Resolve<IPublisher<LlmBackendSelected>>(),
                                c.Resolve<IPublisher<LlmRequestStarted>>(),
                                c.Resolve<IPublisher<LlmRequestCompleted>>(),
                                c.Resolve<IPublisher<LlmUsageReported>>()),
                            maxRetries,
                            attempt => TimeSpan.FromSeconds(Math.Min(1 << attempt, 8)),
                            msg => c.Resolve<ILog>().Warn(msg, LogTag.Llm),
                            asyncMarshaler),
                        c.Resolve<ILog>(),
                        llmTimeout,
                        maxRetries,
                        settings == null || settings.LogLlmInput,
                        settings == null || settings.LogLlmOutput,
                        asyncMarshaler),
                    () => settings != null ? settings.LlmRequestTimeoutSeconds : 0f,
                    asyncMarshaler), Lifetime.Singleton);
#endif

            int maxConcurrent = settings != null ? settings.MaxConcurrentOrchestrations : 4;
            builder.RegisterInstance(new AiOrchestrationQueueOptions
            {
                MaxConcurrent = maxConcurrent < 4 ? 4 : maxConcurrent
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
                // WHY: Record metrics in-memory even without the logging sink, so the Hub's Statistics page can
                // surface live completion/latency/per-role stats. Exposed AsSelf for that concrete resolve.
                // WHY the factory: the type also has a bounded-limits constructor, and VContainer picks the
                // longest one — it then fails resolving its int parameters. CoreAI.Core references nothing,
                // so [Inject] is unavailable there (gate G9); naming the constructor here is the fix.
                builder.Register(_ => new InMemoryAiOrchestrationMetrics(), Lifetime.Singleton)
                    .AsImplementedInterfaces()
                    .AsSelf();
            }
        }

        /// <summary>
        /// Builds the primary (legacy-fallback) client exactly as bootstrap does: execution-mode
        /// resolution plus the optional secondary-backend <see cref="FallbackLlmClientDecorator"/>.
        /// Shared by the container registration above and by <see cref="CoreAiBackend"/>'s runtime
        /// backend switching, so a hot-swapped client has identical semantics to a bootstrapped one.
        /// </summary>
        internal static ILlmClient BuildRoutedPrimaryClient(
            CoreAISettingsAsset settings,
            IGameLogger logger,
            IAgentMemoryStore memoryStore,
            ILlmAgentProvider agentProvider,
            ILog log)
        {
            return BuildRoutedPrimaryClientForPlatform(
                settings,
                logger,
                memoryStore,
                agentProvider,
                log,
                Application.platform);
        }

        internal static ILlmClient BuildRoutedPrimaryClientForPlatform(
            CoreAISettingsAsset settings,
            IGameLogger logger,
            IAgentMemoryStore memoryStore,
            ILlmAgentProvider agentProvider,
            ILog log,
            RuntimePlatform platform)
        {
            ILlmClient primaryClient = ResolveLlmClientForPlatform(
                settings,
                logger,
                memoryStore,
                agentProvider,
                platform);

            if (settings != null && settings.HasValidFallbackBackend)
            {
                ILlmClient secondaryClient = BuildSecondaryHttpClient(settings);
                primaryClient = new FallbackLlmClientDecorator(primaryClient, secondaryClient, log);
            }

            return primaryClient;
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
            return ResolveLlmClientForPlatform(
                settings,
                logger,
                memoryStore,
                agentProvider,
                Application.platform);
        }

        internal static ILlmClient ResolveLlmClientForPlatform(
            CoreAISettingsAsset settings,
            IGameLogger logger,
            IAgentMemoryStore memoryStore,
            ILlmAgentProvider agentProvider,
            RuntimePlatform platform)
        {
            if (!LocalModelPlatformSupport.IsSupported(platform))
            {
                LlmExecutionMode unsupportedMode =
                    settings != null ? settings.ExecutionMode : LlmExecutionMode.Auto;
                if (IsHttpMode(unsupportedMode))
                {
                    return BuildHttpClient(settings, unsupportedMode, memoryStore);
                }

                if (unsupportedMode == LlmExecutionMode.Offline)
                {
                    return BuildOfflineClient(settings);
                }

                if (unsupportedMode == LlmExecutionMode.LocalModel)
                {
                    return new UnsupportedLocalModelLlmClient(platform);
                }

                return TryResolveHttpApiClient(settings, LlmExecutionMode.Auto, memoryStore) ??
                       BuildOfflineClient(settings);
            }

#if !COREAI_LLM
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

        internal static bool ShouldRegisterLocalModelProvider(RuntimePlatform platform)
        {
#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL
            return LocalModelPlatformSupport.IsSupported(platform);
#else
            return false;
#endif
        }

        private static ILlmClient TryResolveAutoClient(
            CoreAISettingsAsset settings,
            IGameLogger logger,
            IAgentMemoryStore memoryStore,
            ILlmAgentProvider agentProvider)
        {
#if UNITY_WEBGL
            ILlmClient http = TryResolveHttpApiClient(settings, LlmExecutionMode.Auto, memoryStore);
            return http ?? BuildOfflineClient(settings);
#else
            bool httpFirst = settings != null && settings.AutoPriority == LlmAutoPriority.HttpFirst;

            if (httpFirst)
            {
                ILlmClient httpClient = TryResolveHttpApiClient(settings, LlmExecutionMode.Auto, memoryStore);
                if (httpClient != null)
                {
                    return httpClient;
                }

                ILlmClient llmUnityClient = TryResolveLlmUnityClient(settings, logger, memoryStore, agentProvider);
                if (llmUnityClient != null)
                {
                    return llmUnityClient;
                }

                return BuildOfflineClient(settings);
            }
            else
            {
                ILlmClient llmUnityClient = TryResolveLlmUnityClient(settings, logger, memoryStore, agentProvider);
                if (llmUnityClient != null)
                {
                    return llmUnityClient;
                }

                ILlmClient httpClient2 = TryResolveHttpApiClient(settings, LlmExecutionMode.Auto, memoryStore);
                if (httpClient2 != null)
                {
                    return httpClient2;
                }

                return BuildOfflineClient(settings);
            }
#endif
        }

        private static ILlmClient TryResolveHttpApiClient(CoreAISettingsAsset settings, LlmExecutionMode mode,
            IAgentMemoryStore memoryStore = null)
        {
#if !COREAI_LLM
            return null;
#else
            if (settings != null && !string.IsNullOrEmpty(settings.ApiBaseUrl) &&
                !string.IsNullOrEmpty(settings.ModelName))
            {
                return BuildHttpClient(settings, mode == LlmExecutionMode.Auto ? settings.ExecutionMode : mode,
                    memoryStore);
            }

            return null;
#endif
        }

        internal static ILlmClient BuildHttpClient(CoreAISettingsAsset settings, LlmExecutionMode mode,
            IAgentMemoryStore memoryStore = null)
        {
#if !COREAI_LLM
            return new StubLlmClient();
#else
            if (mode == LlmExecutionMode.ServerManagedApi)
            {
                ILlmClient serverClient = new ServerManagedLlmClient(
                    new ServerManagedCoreSettingsAdapter(settings),
                    settings,
                    GameLoggerUnscopedFallback.Instance,
                    memoryStore);
                return new RefreshOnUnauthorizedDecorator(serverClient);
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

        /// <summary>
        /// Builds an <see cref="ILlmClient"/> for the secondary (fallback) backend from <see cref="CoreAISettingsAsset"/>.
        /// </summary>
        private static ILlmClient BuildSecondaryHttpClient(CoreAISettingsAsset settings)
        {
#if !COREAI_LLM
            return new StubLlmClient();
#else
            return new OpenAiChatLlmClient(
                new SecondarySettingsAdapter(settings),
                settings,
                GameLoggerUnscopedFallback.Instance,
                null);
#endif
        }

#if COREAI_LLM
        /// <summary>Adapts secondary backend fields to <see cref="IOpenAiHttpSettings"/>.</summary>
        private sealed class SecondarySettingsAdapter : IOpenAiHttpSettings
        {
            private readonly CoreAISettingsAsset _s;

            public SecondarySettingsAdapter(CoreAISettingsAsset s)
            {
                _s = s;
            }

            public string ApiBaseUrl => _s.SecondaryApiBaseUrl;
            public string ApiKey => _s.SecondaryApiKey;
            public string AuthorizationHeader => "";
            public string Model => _s.SecondaryModelName;
            public float Temperature => _s.Temperature;
            public int RequestTimeoutSeconds => _s.EffectiveHttpRequestTimeoutSeconds;
            public int MaxTokens => _s.MaxTokens;
            public string ExtraBodyJson => "";
            public LlmReasoningMode ReasoningMode => _s.ReasoningMode;
            public int ThinkingBudgetTokens => _s.ThinkingBudgetTokens;
            public bool LogLlmInput => _s.LogLlmInput;
            public bool LogLlmOutput => _s.LogLlmOutput;
            public bool EnableHttpDebugLogging => _s.EnableHttpDebugLogging;
            public IRequestHeaderProvider? HeaderProvider => null;
        }
#endif

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
#if !COREAI_HAS_LLMUNITY || UNITY_WEBGL || !COREAI_LLM
            return null;
#else
            LLMAgent agent = agentProvider?.Resolve(settings?.LlmUnityAgentName);
            if (agent == null)
            {
                return null;
            }

            LLM llm = agent.llm != null ? agent.llm : agent.GetComponent<LLM>();
            if (llm != null && settings != null)
            {
                LlmUnityHostConfigurator.ApplyFromSettings(llm, agent, settings, logger);
            }

            if (llm != null && string.IsNullOrWhiteSpace(llm.model))
            {
                return null;
            }

            if (settings == null)
            {
                return null;
            }

            string modelName = llm != null && !string.IsNullOrWhiteSpace(llm.model)
                ? llm.model
                : Path.GetFileName(settings.GgufModelPath);
            if (string.IsNullOrWhiteSpace(modelName))
            {
                modelName = "local";
            }

            LlmUnityServerHttpSettings adapter = new(settings, settings.LlmUnityServerPort, modelName, "");
            return new OpenAiChatLlmClient(adapter, settings, logger, memoryStore);
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

#if COREAI_LLM
        private sealed class ServerManagedCoreSettingsAdapter : IOpenAiHttpSettings
        {
            private readonly CoreAISettingsAsset _settings;

            public ServerManagedCoreSettingsAdapter(CoreAISettingsAsset settings)
            {
                _settings = settings;
            }

            public string ApiBaseUrl => ResolveSameOriginBaseUrl(_settings.ApiBaseUrl);
            public string ApiKey => _settings.ApiKey;
            public string AuthorizationHeader => "";
            public string Model => _settings.ModelName;

            /// <summary>
            /// Always ServerManagedApi: this adapter exists only for that path, and the HTTP client reads
            /// the mode to decide whether an empty <see cref="Model"/> means "the backend picks" or is a
            /// configuration error.
            /// </summary>
            public LlmExecutionMode ExecutionMode => LlmExecutionMode.ServerManagedApi;

            public float Temperature => _settings.Temperature;
            public int RequestTimeoutSeconds => _settings.EffectiveHttpRequestTimeoutSeconds;
            public int MaxTokens => _settings.MaxTokens;
            public string ExtraBodyJson => "";
            public LlmReasoningMode ReasoningMode => _settings.ReasoningMode;
            public int ThinkingBudgetTokens => _settings.ThinkingBudgetTokens;
            public bool LogLlmInput => _settings.LogLlmInput;
            public bool LogLlmOutput => _settings.LogLlmOutput;
            public bool EnableHttpDebugLogging => _settings.EnableHttpDebugLogging;

            public IRequestHeaderProvider? HeaderProvider => null;

            /// <summary>
            /// Expands a relative <c>/api/llm/v1</c>-style base URL against <see cref="UnityEngine.Application.absoluteURL"/>
            /// when running in the WebGL player. Same-origin deployment (game + LLM proxy on one host) becomes
            /// transparent: editor/standalone keep absolute URLs unchanged.
            /// </summary>
            private static string ResolveSameOriginBaseUrl(string configured)
            {
                if (string.IsNullOrWhiteSpace(configured))
                {
                    return configured ?? "";
                }

                string trimmed = configured.Trim();
                bool isRelative = trimmed.StartsWith("/", StringComparison.Ordinal)
                                  && !trimmed.StartsWith("//", StringComparison.Ordinal);
                if (!isRelative)
                {
                    return trimmed;
                }

                string host = Application.absoluteURL;
                if (string.IsNullOrEmpty(host))
                {
                    return trimmed;
                }

                try
                {
                    Uri baseUri = new(host);
                    Uri resolved = new(baseUri, trimmed);
                    return resolved.ToString().TrimEnd('/');
                }
                catch
                {
                    return trimmed;
                }
            }
        }
#endif
    }
}
