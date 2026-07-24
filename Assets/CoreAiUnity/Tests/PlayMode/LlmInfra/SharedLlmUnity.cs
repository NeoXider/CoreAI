#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL
using System.Collections;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Llm;
using LLMUnity;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    ///   LLM + LLMAgent    PlayMode .
    ///        <see cref="EnsureInitialized"/>,
    ///   DontDestroyOnLoad     <see cref="Cleanup"/>
    /// (  <see cref="LlmUnityGlobalSetup.OneTimeTearDown"/>).
    /// </summary>
    public static class SharedLlmUnity
    {
        private static GameObject _rootGo;
        private static LLM _llm;
        private static LLMAgent _agent;
        private static bool _initialized;
        private static bool _initializing;
        private static string _error;

        /// <summary>Upper bound for waiting on an initialization already running on another test.</summary>
        private const float ConcurrentInitializationTimeoutSeconds = 300f;

        public static bool IsReady => _initialized && _agent != null && _llm != null && _llm.started;
        public static string Error => _error;
        public static LLMAgent Agent => _agent;
        public static LLM Llm => _llm;

        /// <summary>
        ///  <see cref="ILlmClient"/>   <see cref="IAgentMemoryStore"/>.
        ///         LLMAgent.
        /// </summary>
        public static ILlmClient CreateClientWithMemoryStore(IAgentMemoryStore store)
        {
            if (!IsReady)
            {
                return null;
            }

            CoreAISettingsAsset settings = CoreAISettingsAsset.Instance;
            string model = _llm != null && !string.IsNullOrWhiteSpace(_llm.model) ? _llm.model : "local";
            return new OpenAiChatLlmClient(
                new LlmUnityServerHttpSettings(settings, settings.LlmUnityServerPort, model, ""),
                settings,
                GameLoggerUnscopedFallback.Instance,
                store);
        }

        /// <summary>
        ///   .    no-op (yield break).
        /// </summary>
        public static IEnumerator EnsureInitialized()
        {
            //  Unity   DontDestroyOnLoad    PlayMode,
            //      (   Domain Reload).
            //   "" LLM.
            if (_initialized && (_llm == null || !_llm.started))
            {
                _initialized = false;
            }

            if (_initialized)
            {
                yield break;
            }

            if (_initializing)
            {
                //
                float waitStarted = Time.realtimeSinceStartup;
                while (_initializing)
                {
                    if (Time.realtimeSinceStartup - waitStarted > ConcurrentInitializationTimeoutSeconds)
                    {
                        // WHY: a cancelled or crashed initializer leaves _initializing set, and an unbounded
                        // wait then hangs every later LLM test until the whole run is killed.
                        _initializing = false;
                        _error = $"[SharedLlmUnity] A concurrent initialization did not finish within " +
                                 $"{ConcurrentInitializationTimeoutSeconds:0}s.";
                        Assert.Fail(_error);
                        yield break;
                    }

                    yield return null;
                }

                yield break;
            }

            _initializing = true;
            Debug.Log("[SharedLlmUnity] Initializing shared LLM instance...");

            CoreAISettingsAsset settings = CoreAISettingsAsset.Instance;
            string agentName = settings?.LlmUnityAgentName;
            string ggufPath = settings?.GgufModelPath;
            int numGpuLayers = settings != null ? settings.NumGPULayers : 99;

            _rootGo = PlayModeLlmUnityTestHarness.CreateRuntimeLlmAndAgent(
                agentName, ggufPath, numGpuLayers, out _llm, out _agent);

            if (_rootGo == null || _agent == null || _llm == null)
            {
                _error = "   LLM+LLMAgent   GGUF.";
                Debug.LogError("[SharedLlmUnity] " + _error);
                _initializing = false;
                yield break;
            }

            // DontDestroyOnLoad:     
            Object.DontDestroyOnLoad(_rootGo);

            //    tool calling
            if (settings != null && settings.OverrideTemperature)
            {
                _agent.temperature = settings.Temperature;
            }

            Debug.Log($"[SharedLlmUnity] Waiting for model: {_llm.model}");

            float timeout = 600f;
            float startTime = Time.realtimeSinceStartup;
            float lastLog = startTime;

            while (!_llm.started && !_llm.failed)
            {
                float elapsed = Time.realtimeSinceStartup - startTime;
                if (elapsed > timeout)
                {
                    _error = $"Model did not load within {timeout}s";
                    Debug.LogError("[SharedLlmUnity] " + _error);
                    _initializing = false;
                    yield break;
                }

                if (Time.realtimeSinceStartup - lastLog > 5f)
                {
                    Debug.Log($"[SharedLlmUnity] Waiting... {elapsed:F1}s, started={_llm.started}");
                    lastLog = Time.realtimeSinceStartup;
                }

                yield return new WaitForSecondsRealtime(1f);
            }

            if (_llm.failed)
            {
                _error = "Model failed to load";
                Debug.LogError("[SharedLlmUnity] " + _error);
                _initializing = false;
                yield break;
            }

            // Enable llama.cpp reasoning only when the operator explicitly asks for it.
            if (settings != null && settings.ReasoningMode == LlmReasoningMode.Enabled)
            {
                _llm.reasoning = true;
                Debug.Log("[SharedLlmUnity] Reasoning (think mode) enabled.");
            }

            _initialized = true;
            _initializing = false;
            Debug.Log($"[SharedLlmUnity] Ready! Model: {_llm.model}");
        }

        /// <summary>
        ///  :  llama.cpp   GameObject.
        ///     <see cref="LlmUnityGlobalSetup.OneTimeTearDown"/>.
        /// </summary>
        public static void Cleanup()
        {
            Debug.Log("[SharedLlmUnity] Cleaning up...");

            if (_agent != null)
            {
                _agent.CancelRequests();
            }

            if (_llm != null)
            {
                _llm.Destroy();
            }

            if (_rootGo != null)
            {
                Object.DestroyImmediate(_rootGo);
                _rootGo = null;
            }

            _llm = null;
            _agent = null;
            _initialized = false;
            _initializing = false;
            _error = null;
        }
    }
}
#endif
