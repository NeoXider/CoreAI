#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL
using System;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Llm;
using Cysharp.Threading.Tasks;
using LLMUnity;
using UnityEngine;
using VContainer.Unity;

namespace CoreAI.Composition
{
    /// <summary>
    /// Starts LLMUnity models during application startup when configured.
    /// </summary>
    public sealed class LlmUnityAutostartEntryPoint : IStartable, IDisposable
    {
        private readonly CoreAISettingsAsset _settings;
        private readonly IGameLogger _logger;
        private readonly ILlmAgentProvider _agentProvider;
        private readonly ILlmEndpointReadinessProbe _readinessProbe;
        private readonly CancellationTokenSource _lifetime = new();

        public LlmUnityAutostartEntryPoint(
            CoreAISettingsAsset settings,
            IGameLogger logger,
            ILlmAgentProvider agentProvider,
            ILlmEndpointReadinessProbe readinessProbe)
        {
            _settings = settings;
            _logger = logger ?? GameLoggerUnscopedFallback.Instance;
            _agentProvider = agentProvider ?? throw new ArgumentNullException(nameof(agentProvider));
            _readinessProbe = readinessProbe ?? throw new ArgumentNullException(nameof(readinessProbe));
        }

        /// <inheritdoc />
        public void Start()
        {
            if (_lifetime.IsCancellationRequested)
            {
                return;
            }

            if (_settings == null || !_settings.LlmUnityAutostartLocalServer || !_settings.UseLlmUnity)
            {
                return;
            }

            LLMAgent agent = _agentProvider.Resolve(_settings.LlmUnityAgentName);
            if (agent == null)
            {
                return;
            }

            LLM llm = agent.llm != null ? agent.llm : agent.GetComponent<LLM>();
            if (llm == null)
            {
                return;
            }

            LlmUnityHostConfigurator.ApplyFromSettings(llm, agent, _settings, _logger);

            if (string.IsNullOrWhiteSpace(llm.model))
            {
                return;
            }

            if (llm.started)
            {
                _logger.LogInfo(GameLogFeature.Llm, "LLMUnity autostart: server already started, nothing to do.");
                return;
            }

            LlmUnityActivationLogContext logContext = new(
                "legacy-autostart",
                "LLMUnity autostart",
                llm.model,
                agent.gameObject.name,
                llm.port);
            long nativeStart = LlmUnityActivationLog.StartTimer();
            _logger.LogInfo(GameLogFeature.Llm, LlmUnityActivationLog.NativeStarted(logContext));
            try
            {
                agent.Start();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(GameLogFeature.Llm,
                    $"LLMUnity autostart: agent.Start() threw - {ex}");
            }

            WarmupAsync(llm, logContext, nativeStart).Forget();
        }

        private async UniTaskVoid WarmupAsync(
            LLM llm,
            LlmUnityActivationLogContext logContext,
            long nativeStart)
        {
            float timeout = _settings.LlmUnityStartupTimeoutSeconds;
            float start = Time.realtimeSinceStartup;
            while (!llm.started && !llm.failed)
            {
                if (Time.realtimeSinceStartup - start > timeout)
                {
                    _logger.LogWarning(GameLogFeature.Llm,
                        LlmUnityActivationLog.NativeFailed(
                            logContext,
                            LlmUnityActivationLog.ElapsedMilliseconds(nativeStart),
                            new TimeoutException(
                                $"Warmup timed out after {timeout:0.#}s; native server did not start.")));
                    return;
                }

                try
                {
                    await UniTask.Delay(
                        TimeSpan.FromMilliseconds(200),
                        DelayType.Realtime,
                        PlayerLoopTiming.Update,
                        _lifetime.Token);
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                    return;
                }
            }

            if (llm.failed)
            {
                _logger.LogWarning(
                    GameLogFeature.Llm,
                    LlmUnityActivationLog.NativeFailed(
                        logContext,
                        LlmUnityActivationLog.ElapsedMilliseconds(nativeStart),
                        new InvalidOperationException("Native server reported startup failure.")));
                return;
            }

            _logger.LogInfo(
                GameLogFeature.Llm,
                LlmUnityActivationLog.NativeSucceeded(
                    logContext,
                    LlmUnityActivationLog.ElapsedMilliseconds(nativeStart)));

            if (!_lifetime.IsCancellationRequested)
            {
                await WaitForOpenAiServerReadyAsync(logContext);
            }
        }

        // WHY: llm.started only means the native process launched, so CoreAI also probes its HTTP route.
        // WHY: Only a handler-level response proves readiness; auth, missing-route, and server failures do not.
        private async UniTask WaitForOpenAiServerReadyAsync(LlmUnityActivationLogContext logContext)
        {
            int port = _settings.LlmUnityServerPort;
            string baseUrl = $"http://localhost:{port}/v1";

            float timeout = _settings.LlmUnityStartupTimeoutSeconds;
            float start = Time.realtimeSinceStartup;
            long readinessStart = LlmUnityActivationLog.StartTimer();
            _logger.LogInfo(GameLogFeature.Llm, LlmUnityActivationLog.ReadinessStarted(logContext));

            while (Time.realtimeSinceStartup - start < timeout)
            {
                LlmEndpointReadinessResult result;
                try
                {
                    result = await _readinessProbe.ProbeAsync(
                        new LlmEndpointReadinessRequest
                        {
                            BaseUrl = baseUrl,
                            TimeoutSeconds = 5,
                            Mode = LlmEndpointReadinessMode.CompletionsOnly
                        },
                        _lifetime.Token);
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception)
                {
                    result = new LlmEndpointReadinessResult
                    {
                        IsReady = false,
                        StatusCode = 0,
                        Error = "Endpoint readiness probe threw a transport exception."
                    };
                }

                if (result.IsReady)
                {
                    _logger.LogInfo(
                        GameLogFeature.Llm,
                        LlmUnityActivationLog.ReadinessSucceeded(
                            logContext,
                            LlmUnityActivationLog.ElapsedMilliseconds(readinessStart)) +
                        $" httpStatus={result.StatusCode}");
                    return;
                }

                if (result.StatusCode > 0)
                {
                    _logger.LogWarning(
                        GameLogFeature.Llm,
                        LlmUnityActivationLog.ReadinessFailed(
                            logContext,
                            LlmUnityActivationLog.ElapsedMilliseconds(readinessStart),
                            new InvalidOperationException(result.Error)));
                    return;
                }

                try
                {
                    await UniTask.Delay(
                        TimeSpan.FromMilliseconds(200),
                        DelayType.Realtime,
                        PlayerLoopTiming.Update,
                        _lifetime.Token);
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                    return;
                }
            }

            _logger.LogWarning(
                GameLogFeature.Llm,
                LlmUnityActivationLog.ReadinessFailed(
                    logContext,
                    LlmUnityActivationLog.ElapsedMilliseconds(readinessStart),
                    new TimeoutException(
                        $"OpenAI server readiness timed out after {timeout:0.#}s; port {port} did not respond.")));
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (!_lifetime.IsCancellationRequested)
            {
                _lifetime.Cancel();
            }

            _lifetime.Dispose();
        }
    }
}
#endif
