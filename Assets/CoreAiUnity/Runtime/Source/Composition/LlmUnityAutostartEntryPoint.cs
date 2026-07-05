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

            try
            {
                agent.Start();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(GameLogFeature.Llm,
                    $"LLMUnity autostart: agent.Start() threw - {ex}");
            }

            WarmupAsync(llm).Forget();
        }

        private async UniTaskVoid WarmupAsync(LLM llm)
        {
            float timeout = _settings.LlmUnityStartupTimeoutSeconds;
            float start = Time.realtimeSinceStartup;
            while (!llm.started && !llm.failed)
            {
                if (Time.realtimeSinceStartup - start > timeout)
                {
                    _logger.LogWarning(GameLogFeature.Llm,
                        $"LLMUnity autostart: warmup timed out after {timeout:0.#}s (server not started yet).");
                    return;
                }

                await UniTask.Delay(TimeSpan.FromMilliseconds(200), DelayType.Realtime, PlayerLoopTiming.Update);
            }

            if (llm.failed)
            {
                _logger.LogWarning(GameLogFeature.Llm, "LLMUnity autostart: server reported failure during startup.");
                return;
            }

            _logger.LogInfo(GameLogFeature.Llm, "LLMUnity autostart: server started successfully.");

            await WaitForOpenAiServerReadyAsync();
        }

        // llm.started only means the native process launched - CoreAI talks to it over HTTP, so we
        // additionally poll the OpenAI-compatible endpoint until it accepts a request. Any HTTP
        // response (including an error status) proves the socket is bound and serving; a connection
        // failure means the server isn't listening yet. HttpClient's async calls never block the
        // calling thread, so this stays off the main thread and cannot deadlock Unity's sync context.
        private async UniTask WaitForOpenAiServerReadyAsync()
        {
            int port = _settings.LlmUnityServerPort;
            string url = $"http://localhost:{port}/v1/chat/completions";

            float timeout = _settings.LlmUnityStartupTimeoutSeconds;
            float start = Time.realtimeSinceStartup;

            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(5) };

            while (Time.realtimeSinceStartup - start < timeout)
            {
                try
                {
                    using HttpContent content = new StringContent("{}", Encoding.UTF8, "application/json");
                    using HttpResponseMessage response = await client.PostAsync(url, content);
                    _logger.LogInfo(GameLogFeature.Llm,
                        $"LLMUnity OpenAI server ready on port {port} (HTTP {(int)response.StatusCode}).");
                    return;
                }
                catch (HttpRequestException)
                {
                    // Connection refused/reset - server socket isn't bound yet, keep polling.
                }
                catch (TaskCanceledException)
                {
                    // Per-request timeout - server may be slow to respond, keep polling.
                }

                await UniTask.Delay(TimeSpan.FromMilliseconds(200), DelayType.Realtime, PlayerLoopTiming.Update);
            }

            _logger.LogWarning(GameLogFeature.Llm,
                $"LLMUnity autostart: OpenAI server readiness check timed out after {timeout:0.#}s " +
                $"(port {port} not responding).");
        }
    }
}
#endif