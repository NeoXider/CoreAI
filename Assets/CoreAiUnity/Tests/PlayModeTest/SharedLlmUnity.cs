#if !COREAI_NO_LLM
using System.Collections;
using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Llm;
using LLMUnity;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    public static class SharedLlmUnity
    {
        private static PlayModeProductionLikeLlmHandle _handle;
        private static bool _initialized;
        private static string _error;
        private static bool _initializing;

        public static bool IsReady => _initialized && _handle?.Client != null && string.IsNullOrEmpty(_error);
        public static string Error => _error;
        public static ILlmClient Client => _handle?.Client;

        public static IEnumerator Initialize()
        {
            if (_initialized || _initializing)
            {
                yield break;
            }

            _initializing = true;
            Debug.Log("[SharedLlmUnity] Initializing...");

            if (!PlayModeProductionLikeLlmFactory.TryCreate(
                    PlayModeProductionLikeLlmBackend.LlmUnity,
                    0f,
                    300,
                    out _handle,
                    out _error))
            {
                Debug.LogError("[SharedLlmUnity] Failed to create LLM: " + _error);
                _initializing = false;
                yield break;
            }

            LlmUnityLlmClient llmClient = _handle.Client as LlmUnityLlmClient;
            if (llmClient == null)
            {
                _error = "Could not get LLM client";
                Debug.LogError("[SharedLlmUnity] " + _error);
                _initializing = false;
                yield break;
            }

            LLM llm = llmClient.LLM;
            if (llm == null)
            {
                _error = "LLM component is null";
                Debug.LogError("[SharedLlmUnity] " + _error);
                _initializing = false;
                yield break;
            }

            Debug.Log("[SharedLlmUnity] Waiting for model: " + llm.model);

            float timeout = 600f;
            float startTime = Time.realtimeSinceStartup;
            float lastLog = startTime;

            while (!llm.started && !llm.failed)
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
                    Debug.Log($"[SharedLlmUnity] Waiting... {elapsed:F1}s, started={llm.started}");
                    lastLog = Time.realtimeSinceStartup;
                }

                yield return new WaitForSecondsRealtime(1f);
            }

            if (llm.failed)
            {
                _error = "Model failed to load";
                Debug.LogError("[SharedLlmUnity] " + _error);
                _initializing = false;
                yield break;
            }

            _initialized = true;
            _initializing = false;
            Debug.Log("[SharedLlmUnity] Ready! Model: " + llm.model);
        }

        public static void Cleanup()
        {
            if (_handle != null)
            {
                Debug.Log("[SharedLlmUnity] Cleaning up...");
                _handle.Dispose();
                _handle = null;
            }

            _initialized = false;
            _initializing = false;
            _error = null;
        }
    }
}
#endif