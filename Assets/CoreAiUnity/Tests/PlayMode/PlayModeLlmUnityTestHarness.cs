#if !COREAI_NO_LLM
using CoreAI.Infrastructure.Llm;
using LLMUnity;
using UnityEngine;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Play Mode: поднимает <see cref="LLM"/> + <see cref="LLMAgent"/> без сцены с префабом.
    /// Предпочитает GGUF с «qwen» и «0.8» в имени (Qwen3.5 0.8B), иначе <see cref="LlmUnityModelBootstrap.TryAutoAssignResolvableModel"/>.
    /// </summary>
    internal static class PlayModeLlmUnityTestHarness
    {
        public static GameObject CreateRuntimeLlmAndAgent(out LLM llm, out LLMAgent agent)
        {
            llm = null;
            agent = null;

            var go = new GameObject("CoreAI_PlayModeTest_LLMUnity");
            go.SetActive(false);

            llm = go.AddComponent<LLM>();
            agent = go.AddComponent<LLMAgent>();
            agent.remote = false;
            agent.llm = llm;

            var assigned = LlmUnityModelBootstrap.TryAssignModelMatchingFilename(llm, "qwen", "0.8")
                           || LlmUnityModelBootstrap.TryAutoAssignResolvableModel(llm);

            if (!assigned || string.IsNullOrWhiteSpace(llm.model))
            {
                Object.Destroy(go);
                llm = null;
                agent = null;
                return null;
            }

            llm.enabled = true;
            agent.enabled = true;
            go.SetActive(true);
            return go;
        }
    }
}
#endif
