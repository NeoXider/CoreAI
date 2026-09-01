using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>Defines the player platforms on which CoreAI may construct an in-process local model.</summary>
    public static class LocalModelPlatformSupport
    {
        /// <summary>Actionable explanation returned when a browser player selects a local model.</summary>
        public const string BrowserUnavailableMessage =
            "Local models are not available in the browser. Use an OpenAI-compatible HTTP endpoint or Offline mode.";

        /// <summary>Actionable explanation returned when the optional LLMUnity integration is absent.</summary>
        public const string IntegrationUnavailableMessage =
            "Local models are not available because the LLMUnity integration is not enabled for this player.";

        private static bool warningLogged;

        /// <summary>True when the platform can host LLMUnity's native in-process model runtime.</summary>
        public static bool IsSupported(RuntimePlatform platform)
        {
            switch (platform)
            {
                case RuntimePlatform.WindowsEditor:
                case RuntimePlatform.WindowsPlayer:
                case RuntimePlatform.WindowsServer:
                case RuntimePlatform.OSXEditor:
                case RuntimePlatform.OSXPlayer:
                case RuntimePlatform.OSXServer:
                case RuntimePlatform.LinuxEditor:
                case RuntimePlatform.LinuxPlayer:
                case RuntimePlatform.LinuxServer:
                case RuntimePlatform.Android:
                case RuntimePlatform.IPhonePlayer:
                case RuntimePlatform.VisionOS:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Returns the player-facing alternative for an unsupported local-model platform.</summary>
        public static string GetUnavailableMessage(RuntimePlatform platform)
        {
            return platform == RuntimePlatform.WebGLPlayer
                ? BrowserUnavailableMessage
                : $"Local models are not available on {platform}. Use an OpenAI-compatible HTTP endpoint or Offline mode.";
        }

        internal static void LogUnavailableOnce(RuntimePlatform platform)
        {
            if (IsSupported(platform) || warningLogged)
            {
                return;
            }

            warningLogged = true;
            Debug.LogWarning("[CoreAI.LocalModel] " + GetUnavailableMessage(platform));
        }
    }

    /// <summary>Fails a local-model request with a stable, player-facing platform limitation.</summary>
    internal sealed class UnsupportedLocalModelLlmClient : ILlmClient
    {
        private readonly string message;
        private readonly RuntimePlatform? platform;

        internal UnsupportedLocalModelLlmClient(RuntimePlatform platform)
        {
            this.platform = platform;
            message = LocalModelPlatformSupport.GetUnavailableMessage(platform);
        }

        internal UnsupportedLocalModelLlmClient(string message)
        {
            this.message = string.IsNullOrWhiteSpace(message)
                ? LocalModelPlatformSupport.IntegrationUnavailableMessage
                : message;
        }

        public Task<LlmCompletionResult> CompleteAsync(
            LlmCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            if (platform.HasValue)
            {
                LocalModelPlatformSupport.LogUnavailableOnce(platform.Value);
            }

            return Task.FromResult(new LlmCompletionResult
            {
                Ok = false,
                Error = message,
                ErrorCode = LlmErrorCode.RoutingError
            });
        }
    }
}

#if COREAI_HAS_LLMUNITY
namespace CoreAI.WebGl
{
    using CoreAI.Infrastructure.Llm;

    /// <summary>Installs the runtime containment pass for local-model components on unsupported players.</summary>
    internal static class CoreAiLocalModelPlayerInit
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void InstallUnsupportedLocalModelGuard()
        {
            if (LocalModelPlatformSupport.IsSupported(Application.platform))
            {
                return;
            }

            LocalModelPlatformSupport.LogUnavailableOnce(Application.platform);
            CoreAiWebGlLlmUnitySceneGuard existing =
                Object.FindAnyObjectByType<CoreAiWebGlLlmUnitySceneGuard>(FindObjectsInactive.Include);
            if (existing != null)
            {
                return;
            }

            GameObject host = new("CoreAI Unsupported Local Model Guard")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            Object.DontDestroyOnLoad(host);
            host.AddComponent<CoreAiWebGlLlmUnitySceneGuard>();
        }
    }

    /// <summary>
    /// Provides post-Awake containment for loaded LLMUnity behaviours and rescans later frames.
    /// </summary>
    [DefaultExecutionOrder(-5000)]
    public sealed class CoreAiWebGlLlmUnitySceneGuard : MonoBehaviour
    {
        private void Awake()
        {
            if (LocalModelPlatformSupport.IsSupported(Application.platform))
            {
                enabled = false;
                return;
            }

            Object.DontDestroyOnLoad(gameObject);
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            SceneManager.sceneLoaded += HandleSceneLoaded;
            LocalModelPlatformSupport.LogUnavailableOnce(Application.platform);
            DisableLoadedLlmUnityBehaviours();
        }

        private void Update()
        {
            DisableLoadedLlmUnityBehaviours();
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;
        }

        private static void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            DisableLoadedLlmUnityBehaviours();
        }

        internal static int DisableLoadedLlmUnityBehaviours()
        {
            MonoBehaviour[] behaviours =
                Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            return DisableLlmUnityBehaviours(behaviours);
        }

        internal static int DisableLlmUnityBehaviours(MonoBehaviour[] behaviours)
        {
            if (behaviours == null || behaviours.Length == 0)
            {
                return 0;
            }

            int disabled = 0;
            foreach (MonoBehaviour behaviour in behaviours)
            {
                if (behaviour == null || behaviour is CoreAiWebGlLlmUnitySceneGuard || !behaviour.enabled)
                {
                    continue;
                }

                string assemblyName = behaviour.GetType().Assembly.GetName().Name;
                if (!string.Equals(assemblyName, "undream.llmunity.Runtime", System.StringComparison.Ordinal))
                {
                    continue;
                }

                behaviour.enabled = false;
                disabled++;
            }

            return disabled;
        }
    }
}
#endif
