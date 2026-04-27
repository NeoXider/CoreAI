using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace CoreAI.Editor
{
    /// <summary>
    /// Temporarily excludes heavy LLMUnity-related StreamingAssets folders from WebGL builds,
    /// then restores them after build completion.
    /// </summary>
    /// <remarks>
    /// <see cref="callbackOrder"/> must run <b>after</b> LLMUnity's <c>LLMBuildProcessor</c> (default ~0),
    /// which calls <c>Directory.GetDirectories</c> on <c>StreamingAssets/LlamaLib*</c>. If we move those
    /// folders first, WebGL preprocess fails with <see cref="DirectoryNotFoundException"/>.
    /// </remarks>
    internal sealed class CoreAIWebGlStreamingAssetsGuard : IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        private const string SessionStateKey = "CoreAI.WebGlStreamingAssetsGuard.Manifest";

        /// <summary>
        /// Late preprocess: after undream/LLMUnity (and similar) have consumed StreamingAssets.
        /// </summary>
        private const int LateBuildCallbackOrder = 100_000;

        // Common folder prefixes produced by local LLM/LLMUnity setups.
        private static readonly string[] GuardedFolderPrefixes =
        {
            "LlamaLib",
            "LLMUnity",
            "LLMUnityBuild"
        };

        public int callbackOrder => LateBuildCallbackOrder;

        [InitializeOnLoadMethod]
        private static void TryRestoreAfterInterruptedBuild()
        {
            if (BuildPipeline.isBuildingPlayer) return;
            RestoreMovedFoldersIfAny();
        }

        public void OnPreprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.WebGL) return;

            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? string.Empty;
            string streamingAssetsAbsolute = Path.Combine(Application.dataPath, "StreamingAssets");
            if (!Directory.Exists(streamingAssetsAbsolute))
            {
                SessionState.EraseString(SessionStateKey);
                return;
            }

            string backupRoot = Path.Combine(projectRoot, "Library", "CoreAI", "WebGlBuildBackup");
            Directory.CreateDirectory(backupRoot);

            var moved = new List<MovedFolderEntry>();
            string[] subDirectories = Directory.GetDirectories(streamingAssetsAbsolute, "*", SearchOption.TopDirectoryOnly);
            foreach (string sourceAbs in subDirectories)
            {
                string folderName = Path.GetFileName(sourceAbs);
                if (!ShouldGuardFolder(folderName)) continue;

                string backupAbs = Path.Combine(backupRoot, folderName);
                if (Directory.Exists(backupAbs))
                {
                    Directory.Delete(backupAbs, true);
                }

                Directory.Move(sourceAbs, backupAbs);
                MoveMetaIfExists(sourceAbs, backupAbs);

                moved.Add(new MovedFolderEntry
                {
                    sourceAbsolutePath = sourceAbs,
                    backupAbsolutePath = backupAbs
                });
            }

            if (moved.Count == 0)
            {
                SessionState.EraseString(SessionStateKey);
                return;
            }

            var manifest = new MovedFoldersManifest { entries = moved.ToArray() };
            SessionState.SetString(SessionStateKey, JsonUtility.ToJson(manifest));
            AssetDatabase.Refresh();

            CoreAIEditorLog.Log(
                $"WebGL build: temporarily excluded {moved.Count} StreamingAssets folder(s) from build output.");
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.WebGL) return;
            RestoreMovedFoldersIfAny();
        }

        private static void RestoreMovedFoldersIfAny()
        {
            string json = SessionState.GetString(SessionStateKey, string.Empty);
            if (string.IsNullOrWhiteSpace(json)) return;

            MovedFoldersManifest manifest;
            try
            {
                manifest = JsonUtility.FromJson<MovedFoldersManifest>(json);
            }
            catch (Exception ex)
            {
                CoreAIEditorLog.LogWarning($"StreamingAssets guard: cannot parse restore manifest: {ex.Message}");
                SessionState.EraseString(SessionStateKey);
                return;
            }

            if (manifest?.entries == null || manifest.entries.Length == 0)
            {
                SessionState.EraseString(SessionStateKey);
                return;
            }

            foreach (MovedFolderEntry entry in manifest.entries)
            {
                if (string.IsNullOrWhiteSpace(entry.sourceAbsolutePath) ||
                    string.IsNullOrWhiteSpace(entry.backupAbsolutePath))
                {
                    continue;
                }

                if (!Directory.Exists(entry.backupAbsolutePath)) continue;
                if (Directory.Exists(entry.sourceAbsolutePath))
                {
                    CoreAIEditorLog.LogWarning(
                        $"StreamingAssets guard restore skipped: destination already exists ({entry.sourceAbsolutePath}).");
                    continue;
                }

                Directory.Move(entry.backupAbsolutePath, entry.sourceAbsolutePath);
                MoveMetaIfExists(entry.backupAbsolutePath, entry.sourceAbsolutePath);
            }

            SessionState.EraseString(SessionStateKey);
            AssetDatabase.Refresh();
            CoreAIEditorLog.Log("WebGL build: restored excluded StreamingAssets folders.");
        }

        private static void MoveMetaIfExists(string fromPathWithoutMeta, string toPathWithoutMeta)
        {
            string fromMeta = fromPathWithoutMeta + ".meta";
            string toMeta = toPathWithoutMeta + ".meta";
            if (!File.Exists(fromMeta)) return;

            if (File.Exists(toMeta))
            {
                File.Delete(toMeta);
            }

            File.Move(fromMeta, toMeta);
        }

        private static bool ShouldGuardFolder(string folderName)
        {
            if (string.IsNullOrWhiteSpace(folderName)) return false;

            foreach (string prefix in GuardedFolderPrefixes)
            {
                if (folderName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        [Serializable]
        private sealed class MovedFoldersManifest
        {
            public MovedFolderEntry[] entries;
        }

        [Serializable]
        private sealed class MovedFolderEntry
        {
            public string sourceAbsolutePath;
            public string backupAbsolutePath;
        }
    }
}
