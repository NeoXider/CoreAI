using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
                if (DependenciesSectionContainsKey(updated, key))
                {
                    alreadyPresent.Add(key);
                    continue;
                }

                if (!TryAddDependency(updated, key, url, out string next))
                {
                    EditorUtility.DisplayDialog("CoreAI - Install Git Dependencies",
                        $"Adding '{key}' would produce invalid JSON in Packages/manifest.json. " +
                        "Nothing was written; add the entries manually using the README quick-start table.",
                        "OK");
                    return;
                }

                updated = next;
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

        /// <summary>
        /// True when <paramref name="key"/> is declared inside the <c>dependencies</c> object of
        /// <paramref name="manifestText"/>. Scoped to that object so a same-named entry under
        /// <c>testables</c> or <c>scopedRegistries</c> is not mistaken for an installed dependency.
        /// </summary>
        public static bool DependenciesSectionContainsKey(string manifestText, string key)
        {
            if (string.IsNullOrEmpty(manifestText) || string.IsNullOrEmpty(key))
            {
                return false;
            }

            DependencySectionLocator locator = DependencySectionLocator.TryLocate(manifestText);
            if (!locator.IsValid)
            {
                return false;
            }

            // Match `"key":` on a key boundary so `com.cysharp.messagepipe` does not match
            // `com.cysharp.messagepipe.vcontainer` and vice versa.
            string needle = "\"" + key + "\"";
            int scanFrom = locator.DependenciesOpenBraceIndex;
            while (true)
            {
                int idx = manifestText.IndexOf(needle, scanFrom, StringComparison.Ordinal);
                if (idx < 0 || idx >= locator.DependenciesCloseBraceIndex)
                {
                    return false;
                }

                int probe = idx + needle.Length;
                while (probe < manifestText.Length && char.IsWhiteSpace(manifestText[probe]))
                {
                    probe++;
                }

                if (probe < manifestText.Length && manifestText[probe] == ':')
                {
                    return true;
                }

                scanFrom = idx + needle.Length;
            }
        }

        /// <summary>
        /// Inserts <c>"key": "url"</c> as the first entry inside the <c>dependencies</c> object of
        /// <paramref name="manifestText"/>, writing the result to <paramref name="updated"/>. Returns
        /// <c>false</c> (leaving <paramref name="updated"/> equal to the input) when no dependencies
        /// object exists or the edited text would not parse as JSON. When the key is already declared the
        /// text is returned unchanged and the result is <c>true</c>.
        /// </summary>
        public static bool TryAddDependency(string manifestText, string key, string url, out string updated)
        {
            updated = manifestText;
            if (string.IsNullOrEmpty(manifestText) || string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            if (DependenciesSectionContainsKey(manifestText, key))
            {
                return true;
            }

            DependencySectionLocator locator = DependencySectionLocator.TryLocate(manifestText);
            if (!locator.IsValid)
            {
                return false;
            }

            string indent = locator.DetectIndent(manifestText);
            string entry = $"\"{key}\": \"{url}\"";
            string inserted;
            if (locator.IsSectionEmpty(manifestText))
            {
                // WHY: a trailing comma after the only entry makes manifest.json unparseable and Unity
                // then refuses to open the project.
                string lineBreak = manifestText[locator.InsertOffset - 1] == '\n' ? "" : "\n";
                inserted = lineBreak + indent + entry + "\n";
            }
            else
            {
                inserted = indent + entry + ",\n";
            }

            string candidate = manifestText.Insert(locator.InsertOffset, inserted);
            if (!IsParseableJson(candidate))
            {
                return false;
            }

            updated = candidate;
            return true;
        }

        private static bool IsParseableJson(string text)
        {
            try
            {
                return JsonConvert.DeserializeObject<JObject>(text) != null;
            }
            catch (JsonException)
            {
                return false;
            }
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
            public int DependenciesCloseBraceIndex { get; }

            private DependencySectionLocator(int insertOffset, int braceIndex, int closeBraceIndex)
            {
                IsValid = true;
                InsertOffset = insertOffset;
                DependenciesOpenBraceIndex = braceIndex;
                DependenciesCloseBraceIndex = closeBraceIndex;
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

                if (!TryFindMatchingBrace(manifestText, braceIdx, out int closeBraceIdx))
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

                return new DependencySectionLocator(insertOffset, braceIdx, closeBraceIdx);
            }

            /// <summary>True when the dependencies object holds nothing but whitespace.</summary>
            public bool IsSectionEmpty(string manifestText)
            {
                for (int i = DependenciesOpenBraceIndex + 1; i < DependenciesCloseBraceIndex; i++)
                {
                    if (!char.IsWhiteSpace(manifestText[i]))
                    {
                        return false;
                    }
                }

                return true;
            }

            private static bool TryFindMatchingBrace(string text, int openBraceIndex, out int closeBraceIndex)
            {
                closeBraceIndex = -1;
                int depth = 0;
                bool inString = false;
                bool escaped = false;
                for (int i = openBraceIndex; i < text.Length; i++)
                {
                    char c = text[i];
                    if (inString)
                    {
                        if (escaped)
                        {
                            escaped = false;
                        }
                        else if (c == '\\')
                        {
                            escaped = true;
                        }
                        else if (c == '"')
                        {
                            inString = false;
                        }

                        continue;
                    }

                    if (c == '"')
                    {
                        inString = true;
                    }
                    else if (c == '{')
                    {
                        depth++;
                    }
                    else if (c == '}')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            closeBraceIndex = i;
                            return true;
                        }
                    }
                }

                return false;
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
