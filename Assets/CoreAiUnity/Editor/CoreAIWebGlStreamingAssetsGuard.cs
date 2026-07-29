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

        /// <summary>File name of the on-disk restore manifest, stored inside the backup root itself.</summary>
        internal const string ManifestFileName = "manifest.json";

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
        private static void Initialize()
        {
            // WHY: a build that crashed or was cancelled never reaches OnPostprocessBuild, so the moved
            // folders survive only as a manifest. Restore on domain load and, as a second net, when the
            // editor is closing — otherwise the next Library wipe deletes the user's native binaries.
            EditorApplication.quitting -= RestoreOnEditorQuitting;
            EditorApplication.quitting += RestoreOnEditorQuitting;

            if (BuildPipeline.isBuildingPlayer)
            {
                return;
            }

            RestoreMovedFoldersIfAny(true);
        }

        private static void RestoreOnEditorQuitting()
        {
            // WHY: no AssetDatabase.Refresh during shutdown — the folders only need to be back on disk.
            RestoreMovedFoldersIfAny(false);
        }

        public void OnPreprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.WebGL)
            {
                return;
            }

            string streamingAssetsAbsolute = Path.Combine(Application.dataPath, "StreamingAssets");
            string backupRoot = GetBackupRoot();

            // WHY: entries left over from an interrupted build are still parked in the backup root; carry
            // them into the new manifest instead of overwriting (and thereby orphaning) them.
            List<MovedFolderEntry> moved = LoadPendingEntries(backupRoot);
            int carriedOver = moved.Count;

            if (!Directory.Exists(streamingAssetsAbsolute))
            {
                if (carriedOver == 0)
                {
                    EraseManifest(backupRoot);
                }

                return;
            }

            Directory.CreateDirectory(backupRoot);

            try
            {
                string[] subDirectories =
                    Directory.GetDirectories(streamingAssetsAbsolute, "*", SearchOption.TopDirectoryOnly);
                foreach (string sourceAbs in subDirectories)
                {
                    string folderName = Path.GetFileName(sourceAbs);
                    if (!ShouldGuardFolder(folderName))
                    {
                        continue;
                    }

                    string backupAbs = Path.Combine(backupRoot, folderName);
                    if (Directory.Exists(backupAbs))
                    {
                        Directory.Delete(backupAbs, true);
                    }

                    Directory.Move(sourceAbs, backupAbs);
                    MoveMetaIfExists(sourceAbs, backupAbs);

                    moved.RemoveAll(e =>
                        string.Equals(e.backupAbsolutePath, backupAbs, StringComparison.OrdinalIgnoreCase));
                    moved.Add(new MovedFolderEntry
                    {
                        sourceAbsolutePath = sourceAbs,
                        backupAbsolutePath = backupAbs
                    });

                    // WHY: persist after EVERY move. A manifest written only after the loop means an
                    // IOException halfway through leaves already-moved folders with no record of where
                    // they went, and Library/ is a directory users delete freely.
                    PersistManifest(backupRoot, moved);
                }
            }
            catch (Exception ex)
            {
                CoreAIEditorLog.LogWarning(
                    $"StreamingAssets guard: excluding folders failed ({ex.Message}); restoring what was moved.");
                RestoreMovedFoldersIfAny(true);
                throw;
            }

            if (moved.Count == 0)
            {
                EraseManifest(backupRoot);
                return;
            }

            int newlyMoved = moved.Count - carriedOver;
            if (newlyMoved <= 0)
            {
                return;
            }

            AssetDatabase.Refresh();

            CoreAIEditorLog.Log(
                $"WebGL build: temporarily excluded {newlyMoved} StreamingAssets folder(s) from build output.");
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.WebGL)
            {
                return;
            }

            if (report.summary.result != BuildResult.Succeeded)
            {
                CoreAIEditorLog.LogWarning(
                    $"WebGL build finished with result '{report.summary.result}'; restoring excluded StreamingAssets folders.");
            }

            RestoreMovedFoldersIfAny(true);
        }

        private static void RestoreMovedFoldersIfAny(bool refreshAssetDatabase)
        {
            string backupRoot = GetBackupRoot();
            List<MovedFolderEntry> entries = LoadManifest(backupRoot);
            if (entries.Count == 0)
            {
                EraseManifest(backupRoot);
                return;
            }

            List<MovedFolderEntry> remaining = new();
            int restored = 0;
            foreach (MovedFolderEntry entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.sourceAbsolutePath) ||
                    string.IsNullOrWhiteSpace(entry.backupAbsolutePath))
                {
                    continue;
                }

                if (!Directory.Exists(entry.backupAbsolutePath))
                {
                    continue;
                }

                if (Directory.Exists(entry.sourceAbsolutePath))
                {
                    CoreAIEditorLog.LogWarning(
                        $"StreamingAssets guard restore skipped: destination already exists ({entry.sourceAbsolutePath}).");
                    remaining.Add(entry);
                    continue;
                }

                try
                {
                    Directory.Move(entry.backupAbsolutePath, entry.sourceAbsolutePath);
                    MoveMetaIfExists(entry.backupAbsolutePath, entry.sourceAbsolutePath);
                    restored++;
                }
                catch (Exception ex)
                {
                    // WHY: keep the entry in the manifest so the next domain load / editor quit retries it.
                    CoreAIEditorLog.LogWarning(
                        $"StreamingAssets guard restore failed for '{entry.backupAbsolutePath}': {ex.Message}");
                    remaining.Add(entry);
                }
            }

            if (remaining.Count == 0)
            {
                EraseManifest(backupRoot);
            }
            else
            {
                PersistManifest(backupRoot, remaining);
            }

            if (restored == 0)
            {
                return;
            }

            if (refreshAssetDatabase)
            {
                AssetDatabase.Refresh();
            }

            CoreAIEditorLog.Log($"WebGL build: restored {restored} excluded StreamingAssets folder(s).");
        }

        /// <summary>Manifest entries whose backup folder is still parked in the backup root.</summary>
        private static List<MovedFolderEntry> LoadPendingEntries(string backupRoot)
        {
            List<MovedFolderEntry> pending = new();
            foreach (MovedFolderEntry entry in LoadManifest(backupRoot))
            {
                if (!string.IsNullOrWhiteSpace(entry.backupAbsolutePath) &&
                    Directory.Exists(entry.backupAbsolutePath))
                {
                    pending.Add(entry);
                }
            }

            return pending;
        }

        private static string GetBackupRoot()
        {
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? string.Empty;
            return Path.Combine(projectRoot, "Library", "CoreAI", "WebGlBuildBackup");
        }

        /// <summary>Absolute path of the restore manifest for the given backup root.</summary>
        internal static string GetManifestPath(string backupRoot)
        {
            return Path.Combine(backupRoot, ManifestFileName);
        }

        /// <summary>
        /// Writes the restore manifest next to the backed-up folders.
        /// </summary>
        /// <remarks>
        /// WHY: <see cref="SessionState"/> alone is not durable — it is wiped when the editor closes, so a
        /// build that failed before <see cref="OnPostprocessBuild"/> used to leave the moved folders
        /// unrecoverable once Unity restarted.
        /// </remarks>
        internal static void WriteManifestFile(string backupRoot, List<MovedFolderEntry> entries)
        {
            Directory.CreateDirectory(backupRoot);
            MovedFoldersManifest manifest = new() { entries = entries?.ToArray() ?? Array.Empty<MovedFolderEntry>() };
            File.WriteAllText(GetManifestPath(backupRoot), JsonUtility.ToJson(manifest));
        }

        /// <summary>Reads the restore manifest, returning an empty list when it is missing or unreadable.</summary>
        internal static List<MovedFolderEntry> ReadManifestFile(string backupRoot)
        {
            string path = GetManifestPath(backupRoot);
            if (!File.Exists(path))
            {
                return new List<MovedFolderEntry>();
            }

            try
            {
                return ParseManifest(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                CoreAIEditorLog.LogWarning($"StreamingAssets guard: cannot read restore manifest: {ex.Message}");
                return new List<MovedFolderEntry>();
            }
        }

        /// <summary>Deserializes a manifest payload, returning an empty list for unusable json.</summary>
        internal static List<MovedFolderEntry> ParseManifest(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new List<MovedFolderEntry>();
            }

            MovedFoldersManifest manifest;
            try
            {
                manifest = JsonUtility.FromJson<MovedFoldersManifest>(json);
            }
            catch (Exception ex)
            {
                CoreAIEditorLog.LogWarning($"StreamingAssets guard: cannot parse restore manifest: {ex.Message}");
                return new List<MovedFolderEntry>();
            }

            return manifest?.entries == null
                ? new List<MovedFolderEntry>()
                : new List<MovedFolderEntry>(manifest.entries);
        }

        private static void PersistManifest(string backupRoot, List<MovedFolderEntry> entries)
        {
            try
            {
                WriteManifestFile(backupRoot, entries);
            }
            catch (Exception ex)
            {
                CoreAIEditorLog.LogWarning($"StreamingAssets guard: cannot write restore manifest: {ex.Message}");
            }

            // WHY: SessionState stays as a fast in-session cache; the file is the source of truth.
            MovedFoldersManifest manifest = new() { entries = entries?.ToArray() ?? Array.Empty<MovedFolderEntry>() };
            SessionState.SetString(SessionStateKey, JsonUtility.ToJson(manifest));
        }

        private static List<MovedFolderEntry> LoadManifest(string backupRoot)
        {
            List<MovedFolderEntry> fromFile = ReadManifestFile(backupRoot);
            if (fromFile.Count > 0)
            {
                return fromFile;
            }

            return ParseManifest(SessionState.GetString(SessionStateKey, string.Empty));
        }

        private static void EraseManifest(string backupRoot)
        {
            SessionState.EraseString(SessionStateKey);
            try
            {
                string path = GetManifestPath(backupRoot);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                CoreAIEditorLog.LogWarning($"StreamingAssets guard: cannot delete restore manifest: {ex.Message}");
            }
        }

        private static void MoveMetaIfExists(string fromPathWithoutMeta, string toPathWithoutMeta)
        {
            string fromMeta = fromPathWithoutMeta + ".meta";
            string toMeta = toPathWithoutMeta + ".meta";
            if (!File.Exists(fromMeta))
            {
                return;
            }

            if (File.Exists(toMeta))
            {
                File.Delete(toMeta);
            }

            File.Move(fromMeta, toMeta);
        }

        internal static bool ShouldGuardFolder(string folderName)
        {
            if (string.IsNullOrWhiteSpace(folderName))
            {
                return false;
            }

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
        internal sealed class MovedFoldersManifest
        {
            public MovedFolderEntry[] entries;
        }

        [Serializable]
        internal sealed class MovedFolderEntry
        {
            public string sourceAbsolutePath;
            public string backupAbsolutePath;
        }
    }
}
