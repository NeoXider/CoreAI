using System;
using System.Collections.Generic;
using System.IO;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Composition;
using CoreAI.Config;
using CoreAI.Diagnostics.G10;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Lua;
using CoreAI.Mods.WorldPackages;
using CoreAI.Logging;
using CoreAI.Messaging;
using CoreAI.Mods.Rbx.Instances.Networking;
using CoreAI.Mods.Rbx.Instances.Scheduling;
using VContainer;

namespace CoreAI.Tools.Scale
{
    /// <summary>Production-composed services plus the harness observers for one staircase step.</summary>
    public sealed class ScaleSession : IDisposable
    {
        private readonly IObjectResolver _container;
        private readonly string _scratchDirectory;

        internal ScaleSession(
            IObjectResolver container,
            string scratchDirectory,
            IAiOrchestrationService orchestrator,
            ILuaModRuntime modRuntime,
            LuaCsModStack modStack,
            IActorIdentityProvider hostIdentityProvider,
            ScaleMemoryLuaModStore modStore,
            ScaleObservabilitySink observability,
            ScaleLoopbackBridge bridge,
            G10ProviderProbe providerProbe,
            InMemoryAiOrchestrationMetrics metrics)
        {
            _container = container;
            _scratchDirectory = scratchDirectory;
            Orchestrator = orchestrator;
            ModRuntime = modRuntime;
            ModStack = modStack;
            HostIdentityProvider = hostIdentityProvider;
            ModStore = modStore;
            Observability = observability;
            Bridge = bridge;
            ProviderProbe = providerProbe;
            Metrics = metrics;
        }

        public IAiOrchestrationService Orchestrator { get; }

        public ILuaModRuntime ModRuntime { get; }

        public LuaCsModStack ModStack { get; }

        public IActorIdentityProvider HostIdentityProvider { get; }

        public ScaleMemoryLuaModStore ModStore { get; }

        public ScaleObservabilitySink Observability { get; }

        public ScaleLoopbackBridge Bridge { get; }

        public G10ProviderProbe ProviderProbe { get; }

        public InMemoryAiOrchestrationMetrics Metrics { get; }

        public LuaCsRbxApiBindings RbxApi => ModStack.GameplayBindings.RbxApi;

        public void Dispose()
        {
            _container.Dispose();
            try
            {
                if (Directory.Exists(_scratchDirectory))
                {
                    Directory.Delete(_scratchDirectory, true);
                }
            }
            catch
            {
                // WHY: scratch persistence is best-effort; a locked file must not fail the run.
            }
        }
    }

    /// <summary>
    /// Builds the harness through the shipped <c>RegisterCorePortable</c> + <c>RegisterCoreAiMods</c>
    /// installers, mirroring <see cref="G10MeasurementComposition"/>; nothing production-relevant is
    /// constructed by hand. Only the provider (fixed-latency scripted stub), the loopback bridge
    /// decorator, the observability sink, and the in-memory stores are harness-owned.
    /// </summary>
    public static class ScaleComposition
    {
        public static ScaleSession Compose(ScaleWorkload workload)
        {
            if (workload == null)
            {
                throw new ArgumentNullException(nameof(workload));
            }

            CoreAISettingsOptions settings = new CoreAISettingsOptions
            {
                EnableStreaming = false,
                EnableLlmContextCompaction = false,
                EnableConversationHistorySummarization = false,
                EnableTokenCalibration = false,
                UniversalSystemPromptPrefix = "",
                MaxContextOverflowRetries = 0,
                MaxTokens = 0,
                ContextWindowTokens = CoreAISettings.UnlimitedContextWindowTokens,
                LlmRequestTimeoutSeconds = 120f,
                Temperature = 0f,
                OverrideTemperature = true
            };
            G10ProviderProbe providerProbe = new G10ProviderProbe();
            ILlmClient provider = new G10MeasuredLlmClient(
                new G10ScriptedLlmClient(workload.Chat.StubLatencyMilliseconds),
                providerProbe);
            ScaleMemoryLuaModStore modStore = new ScaleMemoryLuaModStore();
            ScaleObservabilitySink observability = new ScaleObservabilitySink();
            ScaleLoopbackBridge bridge = new ScaleLoopbackBridge(
                workload.Network.LoopbackMaxClientRequestsPerSecond);
            InMemoryAiOrchestrationMetrics metrics = new InMemoryAiOrchestrationMetrics();
            // WHY: the production file stores default to Application.persistentDataPath, which only
            // exists inside Unity; pointing them at a scratch directory keeps the real store classes.
            string scratchDirectory = Path.Combine(Path.GetTempPath(), "coreai-scale",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratchDirectory);
            FileLuaModSourceStore sourceStore = new FileLuaModSourceStore(
                Path.Combine(scratchDirectory, "mods"), NullLog.Instance, "scale-staircase");

            ContainerBuilder builder = new ContainerBuilder();
            builder.RegisterInstance<IGameLogger>(new ScaleSilentGameLogger());
            builder.RegisterInstance<ILog>(NullLog.Instance);
            builder.RegisterInstance<ICoreAISettings>(settings);
            builder.RegisterInstance<ILlmClient>(provider);
            builder.RegisterInstance<IAiOrchestrationMetrics>(metrics);
            builder.RegisterInstance<IAuthorityHost>(new SoloAuthorityHost());
            builder.RegisterInstance<IAiGameCommandSink>(new ScaleNoopCommandSink());
            ScalePromptProvider prompts = new ScalePromptProvider();
            builder.RegisterInstance<IAgentSystemPromptProvider>(prompts);
            builder.RegisterInstance<IAgentUserPromptTemplateProvider>(prompts);
            builder.RegisterInstance(new AiOrchestrationQueueOptions
            {
                MaxConcurrent = workload.Chat.OrchestratorMaxConcurrent,
                MaxPending = workload.Chat.OrchestratorMaxPending
            });
            builder.Register(_ => new LuaGenerationRateLimiter(), Lifetime.Singleton);
            builder.RegisterInstance<IRbxRuntimeObservabilitySink>(observability);
            builder.RegisterInstance<INetworkBridge>(bridge);
            builder.RegisterCorePortable();
            builder.RegisterCoreAiMods(
                modStoreId: "scale-staircase",
                applicationIsPlayingProvider: () => false,
                skillTextProvider: _ => null,
                worldSessionSourceStore: sourceStore);
            builder.RegisterInstance<ILuaModStore>(modStore);
            builder.RegisterInstance<ILuaModSourceStore>(sourceStore);
            builder.RegisterInstance<IRbxWorldPackageStore>(new ScaleWorldPackageStore());

            IObjectResolver container = builder.Build();
            IAiOrchestrationService orchestrator = container.Resolve<IAiOrchestrationService>();
            if (!(orchestrator is QueuedAiOrchestrator))
            {
                container.Dispose();
                throw new InvalidOperationException(
                    "Production composition did not resolve QueuedAiOrchestrator for IAiOrchestrationService.");
            }

            LuaCsModStack stack = container.Resolve<LuaCsModStack>();
            if (!ReferenceEquals(stack.GameplayBindings.RbxApi.NetworkBridge, bridge))
            {
                container.Dispose();
                throw new InvalidOperationException(
                    "Production composition did not route the Rbx remote surface through the registered loopback bridge.");
            }

            return new ScaleSession(
                container,
                scratchDirectory,
                orchestrator,
                container.Resolve<ILuaModRuntime>(),
                stack,
                container.Resolve<IActorIdentityProvider>(),
                modStore,
                observability,
                bridge,
                providerProbe,
                metrics);
        }
    }
}
