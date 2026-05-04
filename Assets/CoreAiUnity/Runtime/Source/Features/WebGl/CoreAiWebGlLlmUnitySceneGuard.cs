#if COREAI_HAS_LLMUNITY && UNITY_WEBGL && !UNITY_EDITOR
using LLMUnity;
using UnityEngine;

namespace CoreAI.WebGl
{
    /// <summary>
    /// Turns off all <see cref="LLM"/> hosts so Undream LlamaLib never calls <c>GetPlatform()</c> on WebGL
    /// (browser reports an unsupported OS string → <c>ArgumentException: Unknown platform …</c>).
    /// </summary>
    internal static class CoreAiWebGlLlmUnityNativeDisable
    {
        internal static void DisableAllLlmHostsInLoadedScenes()
        {
            LLM[] llms = Object.FindObjectsByType<LLM>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < llms.Length; i++)
            {
                LLM llm = llms[i];
                if (llm != null)
                {
                    llm.gameObject.SetActive(false);
                }
            }
        }
    }

    /// <summary>
    /// Runs even if the scene has no <see cref="CoreAiWebGlLlmUnitySceneGuard"/> component — before any <c>Awake</c>.
    /// </summary>
    internal static class CoreAiWebGlLlmUnityPlayerInit
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void DisableLlmHostsBeforeSceneAwake()
        {
            CoreAiWebGlLlmUnityNativeDisable.DisableAllLlmHostsInLoadedScenes();
        }
    }

    /// <summary>
    /// Disable LLMUnity GGUF hosts on WebGL **before** native LlamaLib initializes.
    /// Covers <see cref="LLM"/> instances spawned after the first scene load (additive scenes, runtime).
    /// </summary>
    [DefaultExecutionOrder(-5000)]
    public sealed class CoreAiWebGlLlmUnitySceneGuard : MonoBehaviour
    {
        private void Awake()
        {
            CoreAiWebGlLlmUnityNativeDisable.DisableAllLlmHostsInLoadedScenes();
        }
    }
}
#endif
