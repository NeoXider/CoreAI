using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;
#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL && COREAI_LLM
using LLMUnity;
using UnityEngine;
#endif

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>Ready client returned by an endpoint activation.</summary>
    public sealed class LlmEndpointClientActivation
    {
        public ILlmClient Client { get; set; }
        public LlmExecutionMode Mode { get; set; }

        /// <summary>
        /// Releases a host activation owned by this activation. Null means the host was already active
        /// or the backend has no host lifecycle to release.
        /// </summary>
        public Func<Task> ReleaseOwnedHostAsync { get; set; }
    }

    /// <summary>Host adapter that creates and readies endpoint-specific clients.</summary>
    public interface ILlmEndpointClientFactory
    {
        Task<LlmEndpointClientActivation> ActivateAsync(
            LlmEndpointDescriptor descriptor,
            string sessionApiKey,
            CancellationToken cancellationToken);
    }

    /// <summary>Default runtime endpoint factory for HTTP, Offline, and LLMUnity backends.</summary>
    public sealed class LlmEndpointClientFactory : ILlmEndpointClientFactory
    {
        private readonly ICoreAISettings _settings;
        private readonly CoreAISettingsAsset _unitySettings;
        private readonly IGameLogger _logger;
        private readonly IAgentMemoryStore _memoryStore;
        private readonly ILlmEndpointReadinessProbe _readinessProbe;

        public LlmEndpointClientFactory(
            ICoreAISettings settings,
            IGameLogger logger,
            IAgentMemoryStore memoryStore = null,
            ILlmEndpointReadinessProbe readinessProbe = null)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _unitySettings = settings as CoreAISettingsAsset;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _memoryStore = memoryStore;
#if !COREAI_LLM
            // WHY: without COREAI_LLM the probe implementations are absent; HTTP/LLMUnity activation throws
            // before any probe is consulted, so no default instance is required.
            _readinessProbe = readinessProbe;
#else
            _readinessProbe = readinessProbe ?? new UnityWebRequestOpenAiReadinessProbe();
#endif
        }

        public async Task<LlmEndpointClientActivation> ActivateAsync(
            LlmEndpointDescriptor descriptor,
            string sessionApiKey,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (descriptor.Kind)
            {
                case LlmEndpointKind.HttpOpenAi:
#if !COREAI_LLM
                    throw new PlatformNotSupportedException(
                        "HTTP LLM endpoints are unavailable: enable the COREAI_LLM module.");
#else
                    await EnsureReadyAsync(
                        descriptor.BaseUrl,
                        sessionApiKey,
                        LlmEndpointReadinessMode.ModelsThenCompletions,
                        cancellationToken);
                    return BuildHttp(descriptor, sessionApiKey);
#endif
                case LlmEndpointKind.Offline:
                    return new LlmEndpointClientActivation
                    {
                        Client = _unitySettings != null
                            ? new OfflineLlmClient(_unitySettings)
                            : new StubLlmClient(),
                        Mode = LlmExecutionMode.Offline
                    };
                case LlmEndpointKind.LlmUnity:
                    return await ActivateLlmUnityAsync(descriptor, sessionApiKey, cancellationToken);
                default:
                    throw new ArgumentOutOfRangeException(nameof(descriptor.Kind));
            }
        }

#if COREAI_LLM
        private LlmEndpointClientActivation BuildHttp(LlmEndpointDescriptor descriptor, string sessionApiKey)
        {
            OpenAiHttpOptions options = BuildHttpOptions(descriptor, sessionApiKey, _settings);
            return new LlmEndpointClientActivation
            {
                Client = new OpenAiChatLlmClient(options, _settings, _logger, _memoryStore),
                Mode = LlmExecutionMode.ClientOwnedApi
            };
        }

        internal static OpenAiHttpOptions BuildHttpOptions(
            LlmEndpointDescriptor descriptor,
            string sessionApiKey,
            ICoreAISettings settings)
        {
            // WHY: descriptor-level behavior settings (max tokens, reasoning, extra body) must survive
            // a switch from the legacy backend to a runtime endpoint, otherwise the same model silently
            // changes behavior after routing.
            return new OpenAiHttpOptions
            {
                UseOpenAiCompatibleHttp = true,
                ExecutionMode = LlmExecutionMode.ClientOwnedApi,
                ApiBaseUrl = descriptor.BaseUrl.Trim(),
                ApiKey = sessionApiKey ?? "",
                Model = string.IsNullOrWhiteSpace(descriptor.Model) ? "default" : descriptor.Model.Trim(),
                RequestTimeoutSeconds = Math.Max(1, (int)settings.LlmRequestTimeoutSeconds),
                MaxTokens = Math.Max(0, descriptor.MaxTokens),
                ReasoningMode = descriptor.ReasoningMode,
                ThinkingBudgetTokens = Math.Max(0, descriptor.ThinkingBudgetTokens),
                ExtraBodyJson = descriptor.ExtraBodyJson ?? ""
            };
        }
#endif

        private async Task<LlmEndpointClientActivation> ActivateLlmUnityAsync(
            LlmEndpointDescriptor descriptor,
            string sessionApiKey,
            CancellationToken cancellationToken)
        {
#if !COREAI_HAS_LLMUNITY || UNITY_WEBGL || !COREAI_LLM
            await Task.Yield();
#if UNITY_WEBGL
            throw new PlatformNotSupportedException(
                LocalModelPlatformSupport.GetUnavailableMessage(UnityEngine.Application.platform));
#else
            throw new PlatformNotSupportedException(LocalModelPlatformSupport.IntegrationUnavailableMessage);
#endif
#else
            if (!LocalModelPlatformSupport.IsSupported(Application.platform))
            {
                throw new PlatformNotSupportedException(
                    LocalModelPlatformSupport.GetUnavailableMessage(Application.platform));
            }

            LLMAgent agent = ResolveAgent(descriptor.UnityAgentName);
            if (agent == null)
            {
                throw new InvalidOperationException("LLMUnity endpoint has no matching LLMAgent.");
            }

            await LlmUnityActivationCoordinator.WaitAsync(agent, cancellationToken);
            try
            {
                return await ActivateOwnedLlmUnityAsync(descriptor, sessionApiKey, cancellationToken, agent);
            }
            finally
            {
                LlmUnityActivationCoordinator.Release(agent);
            }
#endif
        }

#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL && COREAI_LLM
        private async Task<LlmEndpointClientActivation> ActivateOwnedLlmUnityAsync(
            LlmEndpointDescriptor descriptor,
            string sessionApiKey,
            CancellationToken cancellationToken,
            LLMAgent agent)
        {
            LLM llm = agent.llm != null ? agent.llm : agent.GetComponent<LLM>();
            if (llm == null)
            {
                throw new InvalidOperationException("LLMUnity endpoint agent has no LLM host.");
            }

            string model = string.IsNullOrWhiteSpace(descriptor.LocalModelPath)
                ? descriptor.Model?.Trim()
                : descriptor.LocalModelPath.Trim();
            int requestedPort = descriptor.Port > 0 ? descriptor.Port : llm.port;
            LlmUnityActivationLogContext logContext = new(
                descriptor.EndpointId,
                descriptor.DisplayName,
                model,
                agent.gameObject.name,
                requestedPort);
            bool ownsHostActivation = !agent.gameObject.activeInHierarchy;
            if (ownsHostActivation && agent.gameObject.activeSelf)
            {
                throw new InvalidOperationException(
                    "LLMUnity endpoint agent is inactive through its parent. CoreAI only activates the exact host " +
                    "GameObject and never takes ownership of an external parent hierarchy.");
            }

            bool fingerprintChanged = !NativeConfigurationMatches(llm, descriptor, model);
            if (fingerprintChanged)
            {
                if (!ownsHostActivation)
                {
                    throw new InvalidOperationException(
                        "Cannot reconfigure an already-active LLMUnity host without racing native startup or " +
                        "interrupting published requests. Configure an inactive exact LLMAgent, or use a different " +
                        "LLMAgent name and port for zero-downtime switching.");
                }
            }

            Func<Task> releaseOwnedHostAsync;
            if (ownsHostActivation)
            {
                bool requiresManualRestart = LlmUnityOwnedHostLeases.RequiresManualRestart(agent);
                ApplyNativeConfiguration(llm, descriptor, model);
                agent.gameObject.SetActive(true);
                if (!agent.gameObject.activeInHierarchy)
                {
                    agent.gameObject.SetActive(false);
                    throw new InvalidOperationException("LLMUnity endpoint host could not be activated.");
                }

                if (requiresManualRestart)
                {
                    llm.Awake();
                }
            }

            releaseOwnedHostAsync = LlmUnityOwnedHostLeases.Acquire(agent, llm, ownsHostActivation);

            try
            {
                if (!llm.started)
                {
                    long nativeStart = LlmUnityActivationLog.StartTimer();
                    _logger.LogInfo(GameLogFeature.Llm, LlmUnityActivationLog.NativeStarted(logContext));
                    try
                    {
                        await WaitUntilReadyAsync(llm, cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!llm.started || llm.failed)
                        {
                            throw new InvalidOperationException("LLMUnity failed to reach native readiness.");
                        }

                        _logger.LogInfo(
                            GameLogFeature.Llm,
                            LlmUnityActivationLog.NativeSucceeded(
                                logContext,
                                LlmUnityActivationLog.ElapsedMilliseconds(nativeStart)));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            GameLogFeature.Llm,
                            LlmUnityActivationLog.NativeFailed(
                                logContext,
                                LlmUnityActivationLog.ElapsedMilliseconds(nativeStart),
                                ex));
                        throw;
                    }
                }

                if (!llm.started || llm.failed)
                {
                    throw new InvalidOperationException("LLMUnity failed to reach native readiness.");
                }

                int port = descriptor.Port > 0 ? descriptor.Port : llm.port;
                string modelName = string.IsNullOrWhiteSpace(descriptor.Model)
                    ? Path.GetFileName(llm.model)
                    : descriptor.Model.Trim();
                IOpenAiHttpSettings http = _unitySettings != null
                    ? new LlmUnityServerHttpSettings(_unitySettings, port, modelName, sessionApiKey)
                    : new OpenAiHttpOptions
                    {
                        UseOpenAiCompatibleHttp = true,
                        ApiBaseUrl = $"http://localhost:{port}/v1",
                        ApiKey = sessionApiKey ?? "",
                        Model = modelName,
                        MaxTokens = 0
                    };
                long readinessStart = LlmUnityActivationLog.StartTimer();
                _logger.LogInfo(GameLogFeature.Llm, LlmUnityActivationLog.ReadinessStarted(logContext));
                try
                {
                    await EnsureReadyAsync(
                        http.ApiBaseUrl,
                        sessionApiKey,
                        LlmEndpointReadinessMode.CompletionsOnly,
                        cancellationToken);
                    _logger.LogInfo(
                        GameLogFeature.Llm,
                        LlmUnityActivationLog.ReadinessSucceeded(
                            logContext,
                            LlmUnityActivationLog.ElapsedMilliseconds(readinessStart)));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        GameLogFeature.Llm,
                        LlmUnityActivationLog.ReadinessFailed(
                            logContext,
                            LlmUnityActivationLog.ElapsedMilliseconds(readinessStart),
                            ex));
                    throw;
                }

                return new LlmEndpointClientActivation
                {
                    Client = new OpenAiChatLlmClient(http, _settings, _logger, _memoryStore),
                    Mode = LlmExecutionMode.LocalModel,
                    ReleaseOwnedHostAsync = releaseOwnedHostAsync
                };
            }
            catch
            {
                if (releaseOwnedHostAsync != null)
                {
                    _ = releaseOwnedHostAsync();
                }

                throw;
            }
        }

        private static async Task WaitUntilReadyAsync(LLM llm, CancellationToken cancellationToken)
        {
            // WHY: an await-based wait instead of a Task.Yield poll — polling hot-spins a thread-pool
            // worker for the whole native model load when resumed off the Unity main thread.
            Task readiness = llm.WaitUntilReady();
            if (cancellationToken.CanBeCanceled && !readiness.IsCompleted)
            {
                TaskCompletionSource<bool> cancelled = new(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                using CancellationTokenRegistration registration =
                    cancellationToken.Register(() => cancelled.TrySetResult(true));
                await Task.WhenAny(readiness, cancelled.Task);
                cancellationToken.ThrowIfCancellationRequested();
            }

            await readiness;
            cancellationToken.ThrowIfCancellationRequested();
        }

        internal static bool NativeConfigurationMatches(
            LLM llm,
            LlmEndpointDescriptor descriptor,
            string model)
        {
            return (string.IsNullOrWhiteSpace(model) ||
                    string.Equals(llm.model ?? "", model, StringComparison.Ordinal)) &&
                   (descriptor.Port <= 0 || llm.port == descriptor.Port) &&
                   llm.numGPULayers == descriptor.GpuLayers &&
                   llm.flashAttention == descriptor.FlashAttention &&
                   llm.parallelPrompts == Math.Max(1, descriptor.ParallelSlots) &&
                   llm.contextSize == Math.Max(256, descriptor.ContextWindowTokens);
        }

        internal static void ApplyNativeConfiguration(
            LLM llm,
            LlmEndpointDescriptor descriptor,
            string model)
        {
            llm.remote = true;
            if (descriptor.Port > 0)
            {
                llm.port = descriptor.Port;
            }

            llm.numGPULayers = descriptor.GpuLayers;
            llm.flashAttention = descriptor.FlashAttention;
            llm.parallelPrompts = Math.Max(1, descriptor.ParallelSlots);
            llm.contextSize = Math.Max(256, descriptor.ContextWindowTokens);
            if (!string.IsNullOrWhiteSpace(model))
            {
                llm.SetModel(model);
            }
        }
#endif

#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL && COREAI_LLM
        private static LLMAgent ResolveAgent(string name)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                string exactName = name.Trim();
                LLMAgent[] namedAgents = UnityEngine.Object.FindObjectsByType<LLMAgent>(
                    FindObjectsInactive.Include,
                    FindObjectsSortMode.None);
                LLMAgent match = null;
                foreach (LLMAgent candidate in namedAgents)
                {
                    if (!string.Equals(candidate.gameObject.name, exactName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (match != null)
                    {
                        return null;
                    }

                    match = candidate;
                }

                return match;
            }

            LLMAgent[] agents = UnityEngine.Object.FindObjectsByType<LLMAgent>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
            return agents.Length == 1 ? agents[0] : null;
        }
#endif

#if COREAI_LLM
        private async Task EnsureReadyAsync(
            string baseUrl,
            string apiKey,
            LlmEndpointReadinessMode mode,
            CancellationToken cancellationToken)
        {
            LlmEndpointReadinessResult result = await _readinessProbe.ProbeAsync(
                new LlmEndpointReadinessRequest
                {
                    BaseUrl = baseUrl,
                    ApiKey = apiKey ?? "",
                    TimeoutSeconds = 5,
                    Mode = mode
                },
                cancellationToken);
            if (!result.IsReady)
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(result.Error)
                        ? $"Endpoint readiness probe failed ({result.StatusCode})."
                        : result.Error);
            }
        }
#endif
    }

#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL && COREAI_LLM
    internal static class LlmUnityActivationCoordinator
    {
        private static readonly ConditionalWeakTable<LLMAgent, SemaphoreSlim> Gates = new();

        public static Task WaitAsync(LLMAgent agent, CancellationToken cancellationToken)
        {
            return Gates.GetValue(agent, _ => new SemaphoreSlim(1, 1)).WaitAsync(cancellationToken);
        }

        public static void Release(LLMAgent agent)
        {
            Gates.GetValue(agent, _ => new SemaphoreSlim(1, 1)).Release();
        }
    }

    internal static class LlmUnityOwnedHostLeases
    {
        private sealed class LeaseState
        {
            public int Count;
            public bool RestartRequired;
            public bool Cleaning;
        }

        private static readonly object Gate = new();
        private static readonly ConditionalWeakTable<LLMAgent, LeaseState> States = new();

        public static bool RequiresManualRestart(LLMAgent agent)
        {
            lock (Gate)
            {
                return States.TryGetValue(agent, out LeaseState state) &&
                       state.Count == 0 && !state.Cleaning && state.RestartRequired;
            }
        }

        public static Func<Task> Acquire(LLMAgent agent, LLM llm, bool newlyActivatedByCoreAi)
        {
            lock (Gate)
            {
                if (!States.TryGetValue(agent, out LeaseState state))
                {
                    if (!newlyActivatedByCoreAi)
                    {
                        return null;
                    }

                    state = States.GetValue(agent, _ => new LeaseState());
                }
                else if (!newlyActivatedByCoreAi && state.Count == 0 && !state.Cleaning)
                {
                    States.Remove(agent);
                    return null;
                }

                state.Count++;
                state.RestartRequired = false;
            }

            int released = 0;
            return async () =>
            {
                if (Interlocked.Exchange(ref released, 1) != 0)
                {
                    return;
                }

                LeaseState ownedState;
                lock (Gate)
                {
                    if (!States.TryGetValue(agent, out ownedState))
                    {
                        return;
                    }

                    ownedState.Count--;
                    if (ownedState.Count > 0)
                    {
                        return;
                    }

                    ownedState.Cleaning = true;
                }

                if (agent != null)
                {
                    try
                    {
                        await llm.WaitUntilReady();
                    }
                    catch
                    {
                    }

                    bool deactivate;
                    lock (Gate)
                    {
                        deactivate = ownedState.Count == 0;
                        ownedState.Cleaning = false;
                        ownedState.RestartRequired = deactivate;
                    }

                    if (deactivate)
                    {
                        llm.Destroy();
                        agent.gameObject.SetActive(false);
                    }
                }
            };
        }
    }
#endif

    internal readonly struct LlmUnityActivationLogContext
    {
        public LlmUnityActivationLogContext(
            string endpointId,
            string displayName,
            string model,
            string agentName,
            int port)
        {
            EndpointId = endpointId;
            DisplayName = displayName;
            Model = model;
            AgentName = agentName;
            Port = port;
        }

        public string EndpointId { get; }
        public string DisplayName { get; }
        public string Model { get; }
        public string AgentName { get; }
        public int Port { get; }
    }

    internal static class LlmUnityActivationLog
    {
        public static long StartTimer()
        {
            return Stopwatch.GetTimestamp();
        }

        public static long ElapsedMilliseconds(long startTimestamp)
        {
            double ticks = Stopwatch.GetTimestamp() - startTimestamp;
            return Math.Max(0L, (long)Math.Round(ticks * 1000d / Stopwatch.Frequency));
        }

        public static string NativeStarted(LlmUnityActivationLogContext context)
        {
            return Format("native_startup", "started", context, null, null);
        }

        public static string NativeSucceeded(LlmUnityActivationLogContext context, long durationMs)
        {
            return Format("native_startup", "succeeded", context, durationMs, null);
        }

        public static string NativeFailed(
            LlmUnityActivationLogContext context,
            long durationMs,
            Exception error)
        {
            return Format("native_startup", "failed", context, durationMs, error);
        }

        public static string ReadinessStarted(LlmUnityActivationLogContext context)
        {
            return Format("http_readiness", "started", context, null, null);
        }

        public static string ReadinessSucceeded(LlmUnityActivationLogContext context, long durationMs)
        {
            return Format("http_readiness", "succeeded", context, durationMs, null);
        }

        public static string ReadinessFailed(
            LlmUnityActivationLogContext context,
            long durationMs,
            Exception error)
        {
            return Format("http_readiness", "failed", context, durationMs, error);
        }

        private static string Format(
            string phase,
            string status,
            LlmUnityActivationLogContext context,
            long? durationMs,
            Exception error)
        {
            string message =
                "[CoreAI.LLMUnity] phase=" + phase +
                " status=" + status +
                " endpointId=\"" + Safe(context.EndpointId) + "\"" +
                " endpoint=\"" + Safe(context.DisplayName) + "\"" +
                " model=\"" + SafeModel(context.Model) + "\"" +
                " agent=\"" + Safe(context.AgentName) + "\"" +
                " port=" + context.Port.ToString(CultureInfo.InvariantCulture);
            if (durationMs.HasValue)
            {
                message += " durationMs=" + durationMs.Value.ToString(CultureInfo.InvariantCulture);
            }

            if (error != null)
            {
                message +=
                    " errorType=\"" + Safe(error.GetType().Name) + "\"" +
                    " error=\"" + SafeError(error.Message, context.Model) + "\"";
            }

            return message;
        }

        private static string SafeModel(string value)
        {
            string safe = Safe(value);
            int separator = Math.Max(safe.LastIndexOf('/'), safe.LastIndexOf('\\'));
            return separator >= 0 && separator + 1 < safe.Length ? safe.Substring(separator + 1) : safe;
        }

        private static string SafeError(string value, string model)
        {
            string safe = Safe(value);
            string modelPath = Safe(model);
            if (!string.IsNullOrWhiteSpace(modelPath))
            {
                safe = safe.Replace(modelPath, SafeModel(model));
            }

            return safe;
        }

        private static string Safe(string value)
        {
            return (value ?? "")
                .Trim()
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Replace('\t', ' ')
                .Replace('"', '\'');
        }
    }
}
