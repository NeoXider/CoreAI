#if UNITY_EDITOR
using System.Collections.Generic;
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
            bool webGl = report.summary.platform == BuildTarget.WebGL;
            foreach (CoreAISettingsAsset settings in LoadAllSettings())
            {
                string blocker = GetWebGlClientKeyBuildBlocker(settings, webGl);
                if (!string.IsNullOrEmpty(blocker))
                {
                    throw new BuildFailedException(
                        $"{blocker} (asset: '{AssetDatabase.GetAssetPath(settings)}')");
                }

                string warning = GetWebGlClientKeyWarning(settings, webGl);
                if (!string.IsNullOrEmpty(warning))
                {
                    Debug.LogWarning(warning);
                }
            }
        }

        /// <summary>
        /// Menu command for manually validating production configuration.
        /// </summary>
        [MenuItem("CoreAI/Validate Production Settings")]
        public static void ValidateProductionSettings()
        {
            List<CoreAISettingsAsset> assets = LoadAllSettings();
            if (assets.Count == 0)
            {
                EditorUtility.DisplayDialog("CoreAI Production Settings", "CoreAISettings asset was not found.", "OK");
                return;
            }

            bool webGl = EditorUserBuildSettings.activeBuildTarget == BuildTarget.WebGL;
            List<string> warnings = new();
            foreach (CoreAISettingsAsset asset in assets)
            {
                string assetWarning = GetWebGlClientKeyWarning(asset, webGl);
                if (!string.IsNullOrEmpty(assetWarning))
                {
                    warnings.Add($"{assetWarning} (asset: '{AssetDatabase.GetAssetPath(asset)}')");
                }
            }

            string warning = string.Join("\n\n", warnings);
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

        /// <summary>
        /// Returns the subset of <see cref="GetWebGlClientKeyWarning"/> findings that would ship a provider
        /// key inside a public WebGL bundle, i.e. the ones that must fail the build. Empty when the build
        /// may proceed.
        /// </summary>
        public static string GetWebGlClientKeyBuildBlocker(CoreAISettingsAsset settings, bool webGlBuild)
        {
            if (settings == null || !webGlBuild || string.IsNullOrWhiteSpace(settings.ApiKey))
            {
                return "";
            }

            bool keyLeakingMode = settings.ExecutionMode is LlmExecutionMode.ClientOwnedApi
                or LlmExecutionMode.ClientLimited
                or LlmExecutionMode.ServerManagedApi;
            return keyLeakingMode ? GetWebGlClientKeyWarning(settings, true) : "";
        }

        /// <summary>
        /// Returns every <see cref="CoreAISettingsAsset"/> in the project so validation cannot silently
        /// pass by inspecting an arbitrary one when several exist.
        /// </summary>
        private static List<CoreAISettingsAsset> LoadAllSettings()
        {
            List<CoreAISettingsAsset> assets = new();
            string[] guids = AssetDatabase.FindAssets("t:CoreAISettingsAsset");
            if (guids != null)
            {
                foreach (string guid in guids)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    CoreAISettingsAsset asset = string.IsNullOrEmpty(path)
                        ? null
                        : AssetDatabase.LoadAssetAtPath<CoreAISettingsAsset>(path);
                    if (asset != null)
                    {
                        assets.Add(asset);
                    }
                }
            }

            if (assets.Count == 0 && CoreAISettingsAsset.Instance != null)
            {
                assets.Add(CoreAISettingsAsset.Instance);
            }

            return assets;
        }
    }
}
#endif
