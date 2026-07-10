using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace CoreAI.Editor
{
    /// <summary>
    /// One-click installer for the Git dependencies CoreAI needs in <c>Packages/manifest.json</c>.
    /// Existing dependency entries are left unchanged so projects that pin a branch,
    /// tag, or commit keep their selected version.
    /// </summary>
    public static class CoreAIDependencyInstaller
    {
        private const string MenuPath = "CoreAI/Setup/Install Git Dependencies";
        private const int MenuPriority = 0;

        /// <summary>
        /// Dependencies inserted into the manifest when the project does not already declare them.
        /// Order matches the README quick start so the install log is easy to skim.
        /// </summary>
        private static readonly (string Key, string Url)[] RequiredDependencies =
        {
            ("jp.hadashikick.vcontainer",
                "https://github.com/hadashiA/VContainer.git?path=VContainer/Assets/VContainer#1.17.0"),
            ("com.cysharp.messagepipe",
                "https://github.com/Cysharp/MessagePipe.git?path=src/MessagePipe.Unity/Assets/Plugins/MessagePipe"),
            ("com.cysharp.messagepipe.vcontainer",
                "https://github.com/Cysharp/MessagePipe.git?path=src/MessagePipe.Unity/Assets/Plugins/MessagePipe.VContainer"),
            ("com.cysharp.unitask",
                "https://github.com/Cysharp/UniTask.git?path=src/UniTask/Assets/Plugins/UniTask"),
            ("ai.undream.llm",
                "https://github.com/undreamai/LLMUnity.git")
        };

        [MenuItem(MenuPath, priority = MenuPriority)]
        public static void InstallDependencies()
        {
            string manifestPath = Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? string.Empty,
                "Packages", "manifest.json");
            if (!File.Exists(manifestPath))
            {
                EditorUtility.DisplayDialog("CoreAI - Install Git Dependencies",
                    $"Packages/manifest.json was not found:\n{manifestPath}",
                    "OK");
                return;
            }

            string original;
            try
            {
                original = File.ReadAllText(manifestPath, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("CoreAI - Install Git Dependencies",
                    $"Failed to read manifest.json:\n{ex.Message}", "OK");
                return;
            }

            DependencySectionLocator locator = DependencySectionLocator.TryLocate(original);
            if (!locator.IsValid)
            {
                EditorUtility.DisplayDialog("CoreAI - Install Git Dependencies",
                    "Could not find the \"dependencies\" object in manifest.json. Edit it manually using the README quick-start table.",
                    "OK");
                return;
            }

            List<string> added = new();
            List<string> alreadyPresent = new();
            string updated = original;

            foreach ((string key, string url) in RequiredDependencies)
            {
                if (ManifestContainsKey(updated, key))
                {
                    alreadyPresent.Add(key);
                    continue;
                }

                updated = InsertDependency(updated, key, url);
                added.Add(key);
            }

            if (added.Count == 0)
            {
                EditorUtility.DisplayDialog("CoreAI - Install Git Dependencies",
                    BuildSummary(added, alreadyPresent),
                    "OK");
                return;
            }

            // Preview before writing.
            bool confirmed = EditorUtility.DisplayDialog(
                "CoreAI - Install Git Dependencies",
                BuildSummary(added, alreadyPresent) +
                "\n\nWrite these entries to Packages/manifest.json now? Unity will then resolve and recompile.",
                "Apply", "Cancel");
            if (!confirmed)
            {
                return;
            }

            try
            {
                File.WriteAllText(manifestPath, updated, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("CoreAI - Install Git Dependencies",
                    $"Failed to write manifest.json:\n{ex.Message}", "OK");
                return;
            }

            CoreAIEditorLog.Log($"manifest.json updated. Added: {string.Join(", ", added)}.");

            // Triggers UPM resolution.
            UnityEditor.PackageManager.Client.Resolve();
            AssetDatabase.Refresh();
        }

        [MenuItem(MenuPath, validate = true)]
        private static bool ValidateInstallDependencies()
        {
            return !EditorApplication.isCompiling && !EditorApplication.isUpdating;
        }

        private static bool ManifestContainsKey(string manifestText, string key)
        {
            // Match `"key":` on a key boundary so `com.cysharp.messagepipe` does not match
            // `com.cysharp.messagepipe.vcontainer` and vice versa.
            string needle = "\"" + key + "\"";
            int idx = manifestText.IndexOf(needle, StringComparison.Ordinal);
            if (idx < 0)
            {
                return false;
            }

            // Resolve and cache required local values.
            int probe = idx + needle.Length;
            while (probe < manifestText.Length && char.IsWhiteSpace(manifestText[probe]))
            {
                probe++;
            }

            return probe < manifestText.Length && manifestText[probe] == ':';
        }

        /// <summary>
        /// Inserts <c>"key": "url",</c> as the first entry inside the dependencies object.
        /// Preserves indentation by reading the leading whitespace of the next line.
        /// </summary>
        private static string InsertDependency(string manifestText, string key, string url)
        {
            DependencySectionLocator locator = DependencySectionLocator.TryLocate(manifestText);
            if (!locator.IsValid)
            {
                return manifestText;
            }

            string indent = locator.DetectIndent(manifestText);
            string inserted = $"{indent}\"{key}\": \"{url}\",\n";
            return manifestText.Insert(locator.InsertOffset, inserted);
        }

        private static string BuildSummary(List<string> added, List<string> alreadyPresent)
        {
            StringBuilder sb = new();
            if (added.Count == 0)
            {
                sb.AppendLine("All required dependencies are already present in Packages/manifest.json.");
            }
            else
            {
                sb.AppendLine($"Will add {added.Count} dependency(ies):");
                foreach (string key in added)
                {
                    sb.AppendLine($"  - {key}");
                }
            }

            if (alreadyPresent.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"Already present (left untouched):");
                foreach (string key in alreadyPresent)
                {
                    sb.AppendLine($"  - {key}");
                }
            }

            return sb.ToString();
        }

        private readonly struct DependencySectionLocator
        {
            public bool IsValid { get; }
            public int InsertOffset { get; }
            public int DependenciesOpenBraceIndex { get; }

            private DependencySectionLocator(int insertOffset, int braceIndex)
            {
                IsValid = true;
                InsertOffset = insertOffset;
                DependenciesOpenBraceIndex = braceIndex;
            }

            public static DependencySectionLocator TryLocate(string manifestText)
            {
                int depIdx = manifestText.IndexOf("\"dependencies\"", StringComparison.Ordinal);
                if (depIdx < 0)
                {
                    return default;
                }

                int braceIdx = manifestText.IndexOf('{', depIdx);
                if (braceIdx < 0)
                {
                    return default;
                }

                // Insert just after the opening brace + newline so we end up on a fresh line.
                int insertOffset = braceIdx + 1;
                if (insertOffset < manifestText.Length && manifestText[insertOffset] == '\r')
                {
                    insertOffset++;
                }

                if (insertOffset < manifestText.Length && manifestText[insertOffset] == '\n')
                {
                    insertOffset++;
                }

                return new DependencySectionLocator(insertOffset, braceIdx);
            }

            public string DetectIndent(string manifestText)
            {
                // Find the indent of the first existing dependency entry; default to 4 spaces.
                int probe = InsertOffset;
                StringBuilder indent = new();
                while (probe < manifestText.Length)
                {
                    char c = manifestText[probe];
                    if (c == ' ' || c == '\t')
                    {
                        indent.Append(c);
                        probe++;
                        continue;
                    }

                    break;
                }

                return indent.Length > 0 ? indent.ToString() : "    ";
            }
        }
    }
}