#if COREAI_HAS_LLMUNITY && UNITY_WEBGL && !UNITY_EDITOR
using LLMUnity;
using UnityEngine;

namespace CoreAI.WebGl
{
    /// <summary>
    /// Disable LLMUnity GGUF hosts on WebGL **before** native LlamaLib initializes.
    /// Add to an always-active bootstrap GameObject (execution order must run before default).
    /// </summary>
    [DefaultExecutionOrder(-5000)]
    public sealed class CoreAiWebGlLlmUnitySceneGuard : MonoBehaviour
    {
        private void Awake()
        {
            LLM[] llms = FindObjectsByType<LLM>(FindObjectsInactive.Include, FindObjectsSortMode.None);
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
}
#endif
