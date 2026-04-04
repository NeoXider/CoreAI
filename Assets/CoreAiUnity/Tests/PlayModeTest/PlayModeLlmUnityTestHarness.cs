#if !COREAI_NO_LLM
using System;
using System.Reflection;
using System.Threading.Tasks;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Llm;
using LLMUnity;
using UnityEngine;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Play Mode: поднимает <see cref="LLM"/> + <see cref="LLMAgent"/> без сцены с префабом.
    /// Manually invokes Awake()/Start() via reflection since Unity PlayMode tests don't always trigger them.
    /// </summary>
    internal static class PlayModeLlmUnityTestHarness
    {
        private static readonly MethodInfo s_LlmAwakeMethod;
        private static readonly MethodInfo s_LlmClientAwakeMethod;
        private static readonly MethodInfo s_LlmClientStartMethod;

        static PlayModeLlmUnityTestHarness()
        {
            s_LlmAwakeMethod = typeof(LLM).GetMethod("Awake",
                                   BindingFlags.Public | BindingFlags.Instance) ??
                               typeof(LLM).GetMethod("Awake",
                                   BindingFlags.NonPublic | BindingFlags.Instance);

            s_LlmClientAwakeMethod = typeof(LLMClient).GetMethod("Awake",
                                         BindingFlags.Public | BindingFlags.Instance) ??
                                     typeof(LLMClient).GetMethod("Awake",
                                         BindingFlags.NonPublic | BindingFlags.Instance);

            s_LlmClientStartMethod = typeof(LLMClient).GetMethod("Start",
                                         BindingFlags.Public | BindingFlags.Instance) ??
                                     typeof(LLMClient).GetMethod("Start",
                                         BindingFlags.NonPublic | BindingFlags.Instance);
        }

        public static GameObject CreateRuntimeLlmAndAgent(out LLM llm, out LLMAgent agent)
        {
            llm = null;
            agent = null;

            GameObject go = new("CoreAI_PlayModeTest_LLMUnity");
            go.SetActive(false);

            llm = go.AddComponent<LLM>();
            agent = go.AddComponent<LLMAgent>();
            agent.remote = false;
            agent.llm = llm;

            IGameLogger log = GameLoggerUnscopedFallback.Instance;
            bool assigned = LlmUnityModelBootstrap.TryAssignModelMatchingFilename(llm, log, "qwen", "2b")
                            || LlmUnityModelBootstrap.TryAutoAssignResolvableModel(llm, log);

            if (!assigned || string.IsNullOrWhiteSpace(llm.model))
            {
                UnityEngine.Object.Destroy(go);
                llm = null;
                agent = null;
                return null;
            }

            llm.enabled = true;
            agent.enabled = true;
            llm.dontDestroyOnLoad = false;

            Debug.Log("[TestHarness] GameObject created, model: " + llm.model);

            go.SetActive(true);

            if (!llm.started && s_LlmAwakeMethod != null)
            {
                Debug.Log("[TestHarness] Manually invoking LLM.Awake()...");
                try
                {
                    s_LlmAwakeMethod.Invoke(llm, null);
                    Debug.Log("[TestHarness] LLM.Awake() invoked");
                }
                catch (Exception ex)
                {
                    Debug.LogError("[TestHarness] Failed LLM.Awake(): " + ex.InnerException?.Message ?? ex.Message);
                }
            }

            if (s_LlmClientAwakeMethod != null)
            {
                Debug.Log("[TestHarness] Manually invoking LLMAgent.Awake()...");
                try
                {
                    s_LlmClientAwakeMethod.Invoke(agent, null);
                    Debug.Log("[TestHarness] LLMAgent.Awake() invoked");
                }
                catch (Exception ex)
                {
                    Debug.LogError("[TestHarness] Failed LLMAgent.Awake(): " + ex.InnerException?.Message ??
                                   ex.Message);
                }
            }

            if (s_LlmClientStartMethod != null)
            {
                Debug.Log("[TestHarness] Manually invoking LLMAgent.Start()...");
                try
                {
                    object result = s_LlmClientStartMethod.Invoke(agent, null);
                    if (result is Task task)
                    {
                        Debug.Log("[TestHarness] Waiting for LLMAgent.Start() async...");
                        task.Wait(TimeSpan.FromSeconds(30));
                        Debug.Log("[TestHarness] LLMAgent.Start() completed");
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError("[TestHarness] Failed LLMAgent.Start(): " + ex.InnerException?.Message ??
                                   ex.Message);
                }
            }

            return go;
        }

        public static void TriggerAwakeIfNeeded(LLM llmComponent)
        {
            if (llmComponent == null || llmComponent.started)
            {
                return;
            }

            if (s_LlmAwakeMethod != null)
            {
                try
                {
                    Debug.Log("[TestHarness] Triggering Awake on existing LLM...");
                    s_LlmAwakeMethod.Invoke(llmComponent, null);
                }
                catch (Exception ex)
                {
                    Debug.LogError("[TestHarness] Failed to trigger Awake: " + ex.InnerException?.Message ??
                                   ex.Message);
                }
            }
        }
    }
}
#endif