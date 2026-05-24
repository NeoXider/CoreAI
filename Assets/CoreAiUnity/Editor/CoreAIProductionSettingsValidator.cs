#if UNITY_EDITOR
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace CoreAI.Editor
{
    /// <summary>
    /// Editor and build-time validation for production LLM settings.
    /// </summary>
    public sealed class CoreAIProductionSettingsValidator : IPreprocessBuildWithReport
    {
        /// <inheritdoc />
        public int callbackOrder => 0;

        /// <inheritdoc />
        public void OnPreprocessBuild(BuildReport report)
        {
            CoreAISettingsAsset settings = LoadSettings();
            if (settings == null)
            {
                return;
            }

            string warning = GetWebGlClientKeyWarning(settings, report.summary.platform == BuildTarget.WebGL);
            if (!string.IsNullOrEmpty(warning))
            {
                Debug.LogWarning(warning);
            }
        }

        /// <summary>
        /// Menu command for manually validating production configuration.
        /// </summary>
        [MenuItem("CoreAI/Validate Production Settings")]
        public static void ValidateProductionSettings()
        {
            CoreAISettingsAsset settings = LoadSettings();
            if (settings == null)
            {
                EditorUtility.DisplayDialog("CoreAI Production Settings", "CoreAISettings asset was not found.", "OK");
                return;
            }

            bool webGl = EditorUserBuildSettings.activeBuildTarget == BuildTarget.WebGL;
            string warning = GetWebGlClientKeyWarning(settings, webGl);
            if (string.IsNullOrEmpty(warning))
            {
                EditorUtility.DisplayDialog(
                    "CoreAI Production Settings",
                    "No production LLM warnings found for the active build target.",
                    "OK");
                return;
            }

            Debug.LogWarning(warning);
            EditorUtility.DisplayDialog("CoreAI Production Settings", warning, "OK");
        }

        /// <summary>
        /// Returns a warning when WebGL is configured with a provider key in client-side execution modes,
        /// when ServerManagedApi has a leftover non-empty <c>ApiKey</c>, or when streaming is requested
        /// without the native fetch bridge enabled.
        /// </summary>
        public static string GetWebGlClientKeyWarning(CoreAISettingsAsset settings, bool webGlBuild)
        {
            if (settings == null || !webGlBuild)
            {
                return "";
            }

            bool keyPresent = !string.IsNullOrWhiteSpace(settings.ApiKey);

            if (settings.ExecutionMode == LlmExecutionMode.ClientOwnedApi && keyPresent)
            {
                return "[CoreAI] WebGL build is configured with ClientOwnedApi and a non-empty API key. " +
                       "Public WebGL builds expose client assets; use ServerManagedApi with a backend proxy instead.";
            }

            if (settings.ExecutionMode == LlmExecutionMode.ClientLimited && keyPresent)
            {
                return "[CoreAI] WebGL build is configured with ClientLimited and a non-empty API key. " +
                       "Local request limits do not protect the key from public WebGL bundles; switch to ServerManagedApi.";
            }

            if (settings.ExecutionMode == LlmExecutionMode.ServerManagedApi && keyPresent)
            {
                return "[CoreAI] WebGL build with ServerManagedApi has a non-empty ApiKey. " +
                       "ServerManagedApi authorizes via ServerManagedAuthorization (JWT/Bearer at runtime); " +
                       "remove the static ApiKey to avoid leaking it into the public bundle.";
            }

            if (settings.EnableStreaming && !settings.WebGlNativeStreaming &&
                settings.ExecutionMode != LlmExecutionMode.Offline &&
                settings.ExecutionMode != LlmExecutionMode.LocalModel)
            {
                return "[CoreAI] WebGL build: EnableStreaming is ON but WebGlNativeStreaming is OFF. " +
                       "In the player, CoreAiChatService forces non-streaming HTTP (no token-by-token UI). " +
                       "Enable WebGlNativeStreaming (CoreAiSseFetch.jslib) on CoreAISettingsAsset, or disable EnableStreaming.";
            }

            return "";
        }

        private static CoreAISettingsAsset LoadSettings()
        {
            string[] guids = AssetDatabase.FindAssets("t:CoreAISettingsAsset");
            if (guids == null || guids.Length == 0)
            {
                return CoreAISettingsAsset.Instance;
            }

            string path = AssetDatabase.GUIDToAssetPath(guids[0]);
            return AssetDatabase.LoadAssetAtPath<CoreAISettingsAsset>(path);
        }
    }
}
#endif