using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Composition;
using CoreAI.Config;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Llm;
using CoreAI.Logging;
using CoreAI.Messaging;
using CoreAI.Mods.Rbx.Instances.Scheduling;
using UnityEngine;
using VContainer;

namespace CoreAI.Diagnostics.G10
{
    /// <summary>Thread-safe in-memory mod store used by the measurement workload.</summary>
    public sealed class G10MemoryLuaModStore : ILuaModStore
    {
        private readonly ConcurrentDictionary<string, string> _values =
            new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        private readonly ConcurrentQueue<G10ModStoreWrite> _writes =
            new ConcurrentQueue<G10ModStoreWrite>();
        private long _writeSequence;

        public string Get(string modId, string key)
        {
            return _values.TryGetValue(BuildKey(modId, key), out string value) ? value : "";
        }

        public void Set(string modId, string key, string value)
        {
            string storageKey = BuildKey(modId, key);
            if (value == null)
            {
                _values.TryRemove(storageKey, out string _);
                return;
            }

            _values[storageKey] = value;
            _writes.Enqueue(new G10ModStoreWrite(
                Interlocked.Increment(ref _writeSequence),
                modId,
                key,
                value));
        }

        public void Clear(string modId)
        {
            string prefix = (modId ?? "") + "\n";
            foreach (KeyValuePair<string, string> pair in _values)
            {
                if (pair.Key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    _values.TryRemove(pair.Key, out string _);
                }
            }
        }

        /// <summary>Captures externally visible Lua store writes for independent workload verification.</summary>
        public IReadOnlyList<G10ModStoreWrite> SnapshotWrites()
        {
            return _writes.ToArray();
        }

        private static string BuildKey(string modId, string key)
        {
            return (modId ?? "") + "\n" + (key ?? "");
        }
    }

    /// <summary>One externally visible write performed by a Lua mod.</summary>
    public readonly struct G10ModStoreWrite
    {
        public G10ModStoreWrite(long sequence, string modId, string key, string value)
        {
            Sequence = sequence;
            ModId = modId ?? "";
            Key = key ?? "";
            Value = value ?? "";
        }

        public long Sequence { get; }

        public string ModId { get; }

        public string Key { get; }

        public string Value { get; }
    }

    /// <summary>Atomic implementation of the production Lua observability port.</summary>
    public sealed class G10RuntimeObservability : IRbxRuntimeObservabilitySink
    {
        private long _guardedInstructionSteps;
        private long _threadResumes;
        private long _eventsDelivered;
        private long _completedOperations;
        private readonly ConcurrentQueue<long> _guardedInstructionStepSamples =
            new ConcurrentQueue<long>();

        public bool IsEnabled => true;

        public long GuardedInstructionSteps => Interlocked.Read(ref _guardedInstructionSteps);

        public long ThreadResumes => Interlocked.Read(ref _threadResumes);

        public long EventsDelivered => Interlocked.Read(ref _eventsDelivered);

        public long CompletedOperations => Interlocked.Read(ref _completedOperations);

        public void RecordGuardedInstructionSteps(long count)
        {
            Interlocked.Add(ref _guardedInstructionSteps, count);
            _guardedInstructionStepSamples.Enqueue(count);
        }

        public void RecordThreadResumes(long count)
        {
            Interlocked.Add(ref _threadResumes, count);
        }

        public void RecordEventsDelivered(long count)
        {
            Interlocked.Add(ref _eventsDelivered, count);
        }

        public void RecordCompletedOperations(long count)
        {
            Interlocked.Add(ref _completedOperations, count);
        }

        /// <summary>Captures an atomic-enough aggregate snapshot for phase deltas.</summary>
        public G10RuntimeCounterSnapshot Snapshot()
        {
            return new G10RuntimeCounterSnapshot(
                GuardedInstructionSteps,
                ThreadResumes,
                EventsDelivered,
                CompletedOperations);
        }

        /// <summary>Captures individual guard deltas reported by production execution paths.</summary>
        public IReadOnlyList<long> SnapshotGuardedInstructionStepSamples()
        {
            return _guardedInstructionStepSamples.ToArray();
        }
    }

    /// <summary>One aggregate snapshot of production Lua counters.</summary>
    public readonly struct G10RuntimeCounterSnapshot
    {
        public G10RuntimeCounterSnapshot(
            long guardedInstructionSteps,
            long threadResumes,
            long eventsDelivered,
            long completedOperations)
        {
            GuardedInstructionSteps = guardedInstructionSteps;
            ThreadResumes = threadResumes;
            EventsDelivered = eventsDelivered;
            CompletedOperations = completedOperations;
        }

        public long GuardedInstructionSteps { get; }

        public long ThreadResumes { get; }

        public long EventsDelivered { get; }

        public long CompletedOperations { get; }
    }

    /// <summary>Measured lifecycle of one provider invocation.</summary>
    public sealed class G10ProviderObservation
    {
        public string TraceId { get; set; } = "";

        public long StartedTimestamp { get; set; }

        public long CompletedTimestamp { get; set; }

        public bool Succeeded { get; set; }

        public bool Cancelled { get; set; }

        public string Error { get; set; } = "";

        public double LatencyMilliseconds =>
            CompletedTimestamp <= StartedTimestamp
                ? 0d
                : (CompletedTimestamp - StartedTimestamp) * 1000d / Stopwatch.Frequency;
    }

    /// <summary>Thread-safe provider timing and outcome recorder keyed by trace id.</summary>
    public sealed class G10ProviderProbe
    {
        private readonly ConcurrentDictionary<string, G10ProviderObservation> _observations =
            new ConcurrentDictionary<string, G10ProviderObservation>(StringComparer.Ordinal);

        public void RecordStarted(string traceId)
        {
            string key = traceId ?? "";
            _observations[key] = new G10ProviderObservation
            {
                TraceId = key,
                StartedTimestamp = Stopwatch.GetTimestamp()
            };
        }

        public void RecordCompleted(string traceId, bool succeeded, bool cancelled, string error)
        {
            string key = traceId ?? "";
            G10ProviderObservation observation = _observations.GetOrAdd(
                key,
                _ => new G10ProviderObservation { TraceId = key, StartedTimestamp = Stopwatch.GetTimestamp() });
            observation.Succeeded = succeeded;
            observation.Cancelled = cancelled;
            observation.Error = error ?? "";
            observation.CompletedTimestamp = Stopwatch.GetTimestamp();
        }

        public bool TryGet(string traceId, out G10ProviderObservation observation)
        {
            return _observations.TryGetValue(traceId ?? "", out observation);
        }

        public IReadOnlyList<G10ProviderObservation> Snapshot()
        {
            return new List<G10ProviderObservation>(_observations.Values);
        }
    }

    /// <summary>Provider decorator that measures the exact production provider boundary.</summary>
    public sealed class G10MeasuredLlmClient : ILlmClient
    {
        private readonly ILlmClient _inner;
        private readonly G10ProviderProbe _probe;

        public G10MeasuredLlmClient(ILlmClient inner, G10ProviderProbe probe)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        }

        public bool SupportsNativeToolCalling => _inner.SupportsNativeToolCalling;

        public bool SupportsNativeToolCallingForRole(string agentRoleId)
        {
            return _inner.SupportsNativeToolCallingForRole(agentRoleId);
        }

        public bool SupportsNativeToolCallingForRole(string agentRoleId, string routingProfileId)
        {
            return _inner.SupportsNativeToolCallingForRole(agentRoleId, routingProfileId);
        }

        public int? ResolveContextWindowTokensForRole(string agentRoleId, string routingProfileId)
        {
            return _inner.ResolveContextWindowTokensForRole(agentRoleId, routingProfileId);
        }

        public void SetTools(IReadOnlyList<ILlmTool> tools)
        {
            _inner.SetTools(tools);
        }

        public async Task<LlmCompletionResult> CompleteAsync(
            LlmCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            string traceId = request?.TraceId ?? "";
            _probe.RecordStarted(traceId);
            try
            {
                LlmCompletionResult result = await _inner.CompleteAsync(request, cancellationToken);
                bool cancelled = result?.ErrorCode == LlmErrorCode.Cancelled;
                bool succeeded = result != null && result.Ok && !string.IsNullOrWhiteSpace(result.Content);
                if (cancelled)
                {
                    throw new OperationCanceledException(result?.Error ?? "cancelled", cancellationToken);
                }

                _probe.RecordCompleted(traceId, succeeded, false, result?.Error ?? "");
                return result;
            }
            catch (OperationCanceledException ex)
            {
                _probe.RecordCompleted(traceId, false, true, ex.Message);
                throw;
            }
            catch (Exception ex)
            {
                _probe.RecordCompleted(traceId, false, false, ex.Message);
                throw;
            }
        }
    }

    /// <summary>Fixed-delay scripted provider used to isolate queue latency.</summary>
    public sealed class G10ScriptedLlmClient : ILlmClient
    {
        private readonly int _latencyMilliseconds;

        public G10ScriptedLlmClient(int latencyMilliseconds)
        {
            if (latencyMilliseconds < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(latencyMilliseconds));
            }

            _latencyMilliseconds = latencyMilliseconds;
        }

        public async Task<LlmCompletionResult> CompleteAsync(
            LlmCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            if (_latencyMilliseconds > 0)
            {
                await Task.Delay(_latencyMilliseconds, cancellationToken);
            }

            return new LlmCompletionResult
            {
                Ok = true,
                Content = "g10-scripted-response:" + (request?.TraceId ?? "")
            };
        }
    }

    /// <summary>Production-composed services used by one G10 arrival-pattern run.</summary>
    public sealed class G10MeasurementSession : IDisposable
    {
        private readonly IObjectResolver _container;

        internal G10MeasurementSession(
            IObjectResolver container,
            IAiOrchestrationService orchestrator,
            ILuaModRuntime modRuntime,
            LuaCsModStack modStack,
            IActorIdentityProvider actorIdentityProvider,
            G10MemoryLuaModStore modStore,
            G10RuntimeObservability observability,
            G10ProviderProbe providerProbe)
        {
            _container = container;
            Orchestrator = orchestrator;
            ModRuntime = modRuntime;
            ModStack = modStack;
            ActorIdentityProvider = actorIdentityProvider;
            ModStore = modStore;
            Observability = observability;
            ProviderProbe = providerProbe;
        }

        public IAiOrchestrationService Orchestrator { get; }

        public ILuaModRuntime ModRuntime { get; }

        public LuaCsModStack ModStack { get; }

        public IActorIdentityProvider ActorIdentityProvider { get; }

        public G10MemoryLuaModStore ModStore { get; }

        public G10RuntimeObservability Observability { get; }

        public G10ProviderProbe ProviderProbe { get; }

        public void Dispose()
        {
            _container.Dispose();
        }
    }

    /// <summary>Builds the harness through the shipped portable and Mods installers.</summary>
    public static class G10MeasurementComposition
    {
        /// <summary>Creates one isolated production composition for a required arrival pattern.</summary>
        public static G10MeasurementSession Compose(G10MeasurementConfiguration configuration)
        {
            return Compose(configuration, null, null);
        }

        /// <summary>Creates production composition with optional deterministic diagnostics overrides.</summary>
        public static G10MeasurementSession Compose(
            G10MeasurementConfiguration configuration,
            ILlmClient providerOverride,
            IAiOrchestrationMetrics metricsOverride)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            IReadOnlyList<string> errors = configuration.Validate();
            if (errors.Count > 0)
            {
                throw new InvalidOperationException(string.Join("; ", errors));
            }

            G10ProviderConfiguration providerConfiguration = configuration.Provider;
            CoreAISettingsOptions settings = BuildCoreSettings(providerConfiguration);
            G10SilentGameLogger logger = new G10SilentGameLogger();
            G10ProviderProbe providerProbe = new G10ProviderProbe();
            ILlmClient provider = providerOverride ?? BuildProvider(providerConfiguration, settings, logger);
            G10MeasuredLlmClient measuredProvider = new G10MeasuredLlmClient(provider, providerProbe);
            G10MemoryLuaModStore modStore = new G10MemoryLuaModStore();
            G10RuntimeObservability observability = new G10RuntimeObservability();

            ContainerBuilder builder = new ContainerBuilder();
            builder.RegisterInstance<IGameLogger>(logger);
            builder.RegisterInstance<ILog>(NullLog.Instance);
            builder.RegisterInstance<ICoreAISettings>(settings);
            builder.RegisterInstance<ILlmClient>(measuredProvider);
            builder.RegisterInstance<IAiOrchestrationMetrics>(
                metricsOverride ?? new NullAiOrchestrationMetrics());
            builder.RegisterInstance<IAuthorityHost>(new SoloAuthorityHost());
            builder.RegisterInstance<IAiGameCommandSink>(new G10NoopCommandSink());
            builder.RegisterInstance<IAgentSystemPromptProvider>(new G10PromptProvider());
            builder.RegisterInstance<IAgentUserPromptTemplateProvider>(new G10PromptProvider());
            builder.RegisterInstance(new AiOrchestrationQueueOptions
            {
                MaxConcurrent = providerConfiguration.OrchestratorConcurrency.Value,
                MaxPending = 64
            });
            builder.Register(_ => new LuaGenerationRateLimiter(), Lifetime.Singleton);
            builder.RegisterInstance<IRbxRuntimeObservabilitySink>(observability);
            builder.RegisterCorePortable();
            builder.RegisterCoreAiMods(
                modStoreId: "g10-measurement",
                applicationIsPlayingProvider: () => false,
                skillTextProvider: _ => null);
            builder.RegisterInstance<ILuaModStore>(modStore);
            builder.RegisterInstance<ILuaModSourceStore>(NullLuaModSourceStore.Instance);

            IObjectResolver container = builder.Build();
            IAiOrchestrationService orchestrator = container.Resolve<IAiOrchestrationService>();
            if (!(orchestrator is QueuedAiOrchestrator))
            {
                container.Dispose();
                throw new InvalidOperationException(
                    "Production composition did not resolve QueuedAiOrchestrator for IAiOrchestrationService.");
            }

            return new G10MeasurementSession(
                container,
                orchestrator,
                container.Resolve<ILuaModRuntime>(),
                container.Resolve<LuaCsModStack>(),
                container.Resolve<IActorIdentityProvider>(),
                modStore,
                observability,
                providerProbe);
        }

        private static CoreAISettingsOptions BuildCoreSettings(G10ProviderConfiguration configuration)
        {
            CoreAISettingsOptions settings = new CoreAISettingsOptions
            {
                EnableStreaming = false,
                EnableLlmContextCompaction = false,
                EnableConversationHistorySummarization = false,
                EnableTokenCalibration = false,
                UniversalSystemPromptPrefix = "",
                MaxContextOverflowRetries = 0,
                MaxTokens = configuration.ProviderMode == G10ProviderMode.RealProvider
                    ? configuration.OutputCapTokens.Value
                    : 0,
                ContextWindowTokens = configuration.ProviderMode == G10ProviderMode.RealProvider
                    ? configuration.ContextCapTokens.Value
                    : CoreAISettings.UnlimitedContextWindowTokens,
                LlmRequestTimeoutSeconds = configuration.RequestTimeoutSeconds.HasValue
                    ? configuration.RequestTimeoutSeconds.Value
                    : 120f,
                Temperature = configuration.Temperature,
                OverrideTemperature = true
            };
            return settings;
        }

        private static ILlmClient BuildProvider(
            G10ProviderConfiguration configuration,
            CoreAISettingsOptions settings,
            IGameLogger logger)
        {
            if (configuration.ProviderMode == G10ProviderMode.ScriptedStub)
            {
                return new G10ScriptedLlmClient(configuration.StubLatencyMilliseconds.Value);
            }

            G10OpenAiHttpSettings httpSettings = new G10OpenAiHttpSettings(configuration);
            return MeaiLlmClient.CreateHttp(httpSettings, settings, logger);
        }

        private sealed class G10OpenAiHttpSettings : IOpenAiHttpSettings
        {
            private readonly G10ProviderConfiguration _configuration;

            public G10OpenAiHttpSettings(G10ProviderConfiguration configuration)
            {
                _configuration = configuration;
            }

            public string ApiBaseUrl => _configuration.Endpoint;

            public string ApiKey => _configuration.ApiKey ?? "";

            public string AuthorizationHeader => "";

            public string Model => _configuration.ModelId;

            public LlmExecutionMode ExecutionMode => LlmExecutionMode.ClientOwnedApi;

            public float Temperature => _configuration.Temperature;

            public int RequestTimeoutSeconds => _configuration.RequestTimeoutSeconds.Value;

            public int MaxTokens => _configuration.OutputCapTokens.Value;

            public string ExtraBodyJson => _configuration.ExtraBodyJson ?? "";

            public bool LogLlmInput => false;

            public bool LogLlmOutput => false;

            public bool EnableHttpDebugLogging => false;

            public IRequestHeaderProvider HeaderProvider => null;
        }

        private sealed class G10PromptProvider : IAgentSystemPromptProvider, IAgentUserPromptTemplateProvider
        {
            public bool TryGetSystemPrompt(string roleId, out string prompt)
            {
                prompt = "Return one short acknowledgement for the G10 capacity measurement.";
                return true;
            }

            public bool TryGetUserTemplate(string roleId, out string template)
            {
                template = "{hint}";
                return true;
            }
        }

        private sealed class G10NoopCommandSink : IAiGameCommandSink
        {
            public void Publish(ApplyAiGameCommand command)
            {
            }
        }

        private sealed class G10SilentGameLogger : IGameLogger
        {
            public void LogDebug(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogInfo(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogWarning(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogError(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }
        }
    }
}
