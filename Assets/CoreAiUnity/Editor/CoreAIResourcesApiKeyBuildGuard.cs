#if UNITY_EDITOR
using System.Collections.Generic;
using CoreAI.Infrastructure.Llm;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace CoreAI.Editor
{
    /// <summary>
    /// Build-time guard that WARNS (does not fail the build) when a <see cref="CoreAISettingsAsset"/> that
    /// ships inside a <c>Resources</c> folder carries a non-empty <c>apiKey</c> or <c>secondaryApiKey</c>.
    /// <para>
    /// Anything under a <c>Resources/</c> folder is packed into the player and is trivially recoverable from
    /// the shipped build (strings are not encrypted). A committed key in such an asset is therefore an
    /// exposed secret. Prefer environment variables or secure runtime storage and inject the key at runtime.
    /// </para>
    /// This is intentionally a warning, not a hard block: a harmless local placeholder (e.g. an LM Studio
    /// key the server ignores) should not stop a build. The console warning still flags a real key so it is
    /// not shipped by accident.
    /// </summary>
    public sealed class CoreAIResourcesApiKeyBuildGuard : IPreprocessBuildWithReport
    {
        /// <inheritdoc />
        public int callbackOrder => 0;

        /// <inheritdoc />
        public void OnPreprocessBuild(BuildReport report)
        {
            foreach (CoreAISettingsAsset asset in FindResourcesSettingsAssets())
            {
                if (asset == null)
                {
                    continue;
                }

                bool hasPrimary = !string.IsNullOrEmpty(asset.ApiKey);
                bool hasSecondary = !string.IsNullOrEmpty(asset.SecondaryApiKey);
                if (!hasPrimary && !hasSecondary)
                {
                    continue;
                }

                string path = AssetDatabase.GetAssetPath(asset);
                string which = hasPrimary && hasSecondary
                    ? "apiKey and secondaryApiKey"
                    : hasPrimary
                        ? "apiKey"
                        : "secondaryApiKey";

                Debug.LogWarning(
                    $"[CoreAI] The CoreAISettings asset at '{path}' lives under a Resources folder and has a " +
                    $"non-empty {which}. Resources assets are packed into the build and the key is recoverable " +
                    "from the shipped player. If this is a real secret, clear it on the committed Resources asset " +
                    "and supply it at runtime from an environment variable or secure storage (e.g. " +
                    "CoreAISettings.Instance / a local-only config). Building anyway.");
            }
        }

        /// <summary>
        /// Returns every <see cref="CoreAISettingsAsset"/> whose asset path contains a <c>Resources</c>
        /// folder segment (case-insensitive), i.e. the ones that actually ship inside the player.
        /// </summary>
        private static IEnumerable<CoreAISettingsAsset> FindResourcesSettingsAssets()
        {
            string[] guids = AssetDatabase.FindAssets("t:CoreAISettingsAsset");
            if (guids == null)
            {
                yield break;
            }

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path) || !IsUnderResources(path))
                {
                    continue;
                }

                CoreAISettingsAsset asset = AssetDatabase.LoadAssetAtPath<CoreAISettingsAsset>(path);
                if (asset != null)
                {
                    yield return asset;
                }
            }
        }

        private static bool IsUnderResources(string assetPath)
        {
            string normalized = assetPath.Replace('\\', '/');
            return normalized.Contains("/Resources/") ||
                   normalized.StartsWith("Resources/", System.StringComparison.Ordinal);
        }
    }
}
#endif
