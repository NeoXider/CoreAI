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
        /// Returns a warning when WebGL is configured with a client-owned provider key.
        /// </summary>
        public static string GetWebGlClientKeyWarning(CoreAISettingsAsset settings, bool webGlBuild)
        {
            if (settings == null || !webGlBuild)
            {
                return "";
            }

            if (settings.ExecutionMode == LlmExecutionMode.ClientOwnedApi &&
                !string.IsNullOrWhiteSpace(settings.ApiKey))
            {
                return "[CoreAI] WebGL build is configured with ClientOwnedApi and a non-empty API key. " +
                       "Public WebGL builds expose client assets; use ServerManagedApi with a backend proxy instead.";
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
