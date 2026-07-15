#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL
using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
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
    public sealed class LlmUnityAutostartEntryPoint : IStartable
    {
        private readonly CoreAISettingsAsset _settings;
        private readonly IGameLogger _logger;
        private readonly ILlmAgentProvider _agentProvider;

        public LlmUnityAutostartEntryPoint(
            CoreAISettingsAsset settings,
            IGameLogger logger,
            ILlmAgentProvider agentProvider)
        {
            _settings = settings;
            _logger = logger ?? GameLoggerUnscopedFallback.Instance;
            _agentProvider = agentProvider ?? throw new ArgumentNullException(nameof(agentProvider));
        }

        /// <inheritdoc />
        public void Start()
        {
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

                await UniTask.Delay(TimeSpan.FromMilliseconds(200), DelayType.Realtime, PlayerLoopTiming.Update);
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

            await WaitForOpenAiServerReadyAsync(logContext);
        }

        // WHY: llm.started only means the native process launched, so CoreAI also probes its HTTP route.
        // WHY: Only a handler-level response proves readiness; auth, missing-route, and server failures do not.
        private async UniTask WaitForOpenAiServerReadyAsync(LlmUnityActivationLogContext logContext)
        {
            int port = _settings.LlmUnityServerPort;
            string url = $"http://localhost:{port}/v1/chat/completions";

            float timeout = _settings.LlmUnityStartupTimeoutSeconds;
            float start = Time.realtimeSinceStartup;
            long readinessStart = LlmUnityActivationLog.StartTimer();
            _logger.LogInfo(GameLogFeature.Llm, LlmUnityActivationLog.ReadinessStarted(logContext));

            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(5) };

            while (Time.realtimeSinceStartup - start < timeout)
            {
                try
                {
                    using HttpContent content = new StringContent("{}", Encoding.UTF8, "application/json");
                    using HttpResponseMessage response = await client.PostAsync(url, content);
                    long status = (long)response.StatusCode;
                        if (!LlmEndpointReadinessPolicy.IsLlmUnityHandlerReached(status))
                    {
                        _logger.LogWarning(
                            GameLogFeature.Llm,
                            LlmUnityActivationLog.ReadinessFailed(
                                logContext,
                                LlmUnityActivationLog.ElapsedMilliseconds(readinessStart),
                                new InvalidOperationException(
                                    $"LLMUnity readiness probe failed ({status}).")));
                        return;
                    }

                    _logger.LogInfo(
                        GameLogFeature.Llm,
                        LlmUnityActivationLog.ReadinessSucceeded(
                            logContext,
                            LlmUnityActivationLog.ElapsedMilliseconds(readinessStart)) +
                        $" httpStatus={status}");
                    return;
                }
                catch (HttpRequestException)
                {
                    // WHY: Connection refused/reset - server socket isn't bound yet, keep polling.
                }
                catch (TaskCanceledException)
                {
                    // WHY: Per-request timeout - server may be slow to respond, keep polling.
                }

                await UniTask.Delay(TimeSpan.FromMilliseconds(200), DelayType.Realtime, PlayerLoopTiming.Update);
            }

            _logger.LogWarning(
                GameLogFeature.Llm,
                LlmUnityActivationLog.ReadinessFailed(
                    logContext,
                    LlmUnityActivationLog.ElapsedMilliseconds(readinessStart),
                    new TimeoutException(
                        $"OpenAI server readiness timed out after {timeout:0.#}s; port {port} did not respond.")));
        }

    }
}
#endif
