#if !COREAI_NO_LLM && !UNITY_WEBGL
using System.Collections;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Llm;
using LLMUnity;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Р•РґРёРЅС‹Р№ СЌРєР·РµРјРїР»СЏСЂ LLM + LLMAgent РЅР° РІСЃСЋ СЃРµСЃСЃРёСЋ PlayMode С‚РµСЃС‚РѕРІ.
    /// РњРѕРґРµР»СЊ Р·Р°РіСЂСѓР¶Р°РµС‚СЃСЏ РѕРґРёРЅ СЂР°Р· РїСЂРё РїРµСЂРІРѕРј РІС‹Р·РѕРІРµ <see cref="EnsureInitialized"/>,
    /// Р¶РёРІС‘С‚ С‡РµСЂРµР· DontDestroyOnLoad Рё СѓРЅРёС‡С‚РѕР¶Р°РµС‚СЃСЏ С‚РѕР»СЊРєРѕ РІ <see cref="Cleanup"/>
    /// (РІС‹Р·С‹РІР°РµС‚СЃСЏ РёР· <see cref="LlmUnityGlobalSetup.OneTimeTearDown"/>).
    /// </summary>
    public static class SharedLlmUnity
    {
        private static GameObject _rootGo;
        private static LLM _llm;
        private static LLMAgent _agent;
        private static bool _initialized;
        private static bool _initializing;
        private static string _error;

        public static bool IsReady => _initialized && _agent != null && _llm != null && _llm.started;
        public static string Error => _error;
        public static LLMAgent Agent => _agent;
        public static LLM Llm => _llm;

        /// <summary>
        /// РЎРѕР·РґР°С‚СЊ <see cref="ILlmClient"/> СЃ РєРѕРЅРєСЂРµС‚РЅС‹Рј <see cref="IAgentMemoryStore"/>.
        /// РљР°Р¶РґС‹Р№ С‚РµСЃС‚ РїРѕР»СѓС‡Р°РµС‚ СЃРІРѕР№ Р»С‘РіРєРёР№ РєР»РёРµРЅС‚ РІРѕРєСЂСѓРі РѕР±С‰РµРіРѕ LLMAgent.
        /// </summary>
        public static ILlmClient CreateClientWithMemoryStore(IAgentMemoryStore store)
        {
            if (!IsReady)
            {
                return null;
            }

            return new MeaiLlmClient(
                new LlmUnityMeaiChatClient(_agent, GameLoggerUnscopedFallback.Instance),
                GameLoggerUnscopedFallback.Instance,
                UnityEngine.ScriptableObject.CreateInstance<CoreAI.Infrastructure.Llm.CoreAISettingsAsset>(),
                store);
        }

        /// <summary>
        /// РРЅРёС†РёР°Р»РёР·РёСЂРѕРІР°С‚СЊ РѕРґРёРЅ СЂР°Р·. РџРѕРІС‚РѕСЂРЅС‹Рµ РІС‹Р·РѕРІС‹ вЂ” no-op (yield break).
        /// </summary>
        public static IEnumerator EnsureInitialized()
        {
            // РЈ Unity РѕР±СЉРµРєС‚С‹ РІ DontDestroyOnLoad СѓРЅРёС‡С‚РѕР¶Р°СЋС‚СЃСЏ РїСЂРё РѕСЃС‚Р°РЅРѕРІРєРµ PlayMode,
            // РЅРѕ СЃС‚Р°С‚РёС‡РµСЃРєРёРµ РїРµСЂРµРјРµРЅРЅС‹Рµ СЃРѕС…СЂР°РЅСЏСЋС‚ Р·РЅР°С‡РµРЅРёСЏ (РµСЃР»Рё РЅРµ Р±С‹Р»Рѕ Domain Reload).
            // Р—Р°С‰РёС‚Р° РѕС‚ "РїРѕС‚РµСЂСЏРЅРЅРѕРіРѕ" LLM.
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
                // Р”СЂСѓРіРѕР№ С‚РµСЃС‚ СѓР¶Рµ РёРЅРёС†РёР°Р»РёР·РёСЂСѓРµС‚ вЂ” Р¶РґС‘Рј
                while (_initializing)
                {
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
                _error = "РќРµ СѓРґР°Р»РѕСЃСЊ СЃРѕР·РґР°С‚СЊ LLM+LLMAgent РёР»Рё РЅР°Р·РЅР°С‡РёС‚СЊ GGUF.";
                Debug.LogError("[SharedLlmUnity] " + _error);
                _initializing = false;
                yield break;
            }

            // DontDestroyOnLoad: Р¶РёРІС‘Рј РґРѕ РєРѕРЅС†Р° РІСЃРµС… С‚РµСЃС‚РѕРІ
            Object.DontDestroyOnLoad(_rootGo);

            // РўРµРјРїРµСЂР°С‚СѓСЂР° РґР»СЏ СЃС‚Р°Р±РёР»СЊРЅРѕРіРѕ tool calling
            if (settings != null && settings.Temperature > 0f)
            {
                _agent.temperature = settings.Temperature;
            }
            else
            {
                _agent.temperature = 0.2f;
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

            // РџСЂРёРјРµРЅСЏРµРј reasoning РџРћРЎР›Р• СЃС‚Р°СЂС‚Р° llmService (РЅРµР»СЊР·СЏ СЂР°РЅСЊС€Рµ вЂ” C++ РµС‰С‘ РЅРµ РіРѕС‚РѕРІ)
            if (settings != null && settings.EnableReasoning)
            {
                _llm.reasoning = true;
                Debug.Log("[SharedLlmUnity] Reasoning (think mode) enabled.");
            }

            _initialized = true;
            _initializing = false;
            Debug.Log($"[SharedLlmUnity] Ready! Model: {_llm.model}");
        }

        /// <summary>
        /// РџРѕР»РЅР°СЏ РѕС‡РёСЃС‚РєР°: РѕСЃС‚Р°РЅРѕРІРёС‚СЊ llama.cpp Рё СѓРЅРёС‡С‚РѕР¶РёС‚СЊ GameObject.
        /// Р’С‹Р·С‹РІР°РµС‚СЃСЏ РћР”РРќ СЂР°Р· РёР· <see cref="LlmUnityGlobalSetup.OneTimeTearDown"/>.
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