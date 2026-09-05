using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Shrinking ratchet against IMGUI in CoreAI runtime and demo UI. Runtime and demo surfaces are
    /// meant to render through UITK / uGUI, not the immediate-mode <c>OnGUI</c> pipeline. This fitness
    /// test scans the runtime + demo source trees for IMGUI tokens and FAILS on any file that is not on
    /// an explicit whitelist. The whitelist is seeded from today's real offenders so the test passes now;
    /// it may only SHRINK — every migration deletes an entry, nothing is ever added.
    ///
    /// Editor windows and custom inspectors are legitimately IMGUI, so <c>Editor/</c> folders are NOT
    /// failed here — a separate soft check just REPORTS the remaining editor IMGUI so the migration
    /// (the Lua editor windows already moved to UITK) can be tracked without blocking CI.
    /// </summary>
    [TestFixture]
    public sealed class ImguiBanRatchetEditModeTests
    {
        private static readonly Regex ImguiToken = new(
            @"OnGUI\(|OnInspectorGUI\(|GUILayout\.|EditorGUILayout\.|GUIStyle|GUI\.",
            RegexOptions.Compiled);

        // WHY: relative to Application.dataPath (the "Assets" folder). Strict scope = package runtime +
        // demos. CoreAiUnity keeps the runtime overlays; the *.Demos folder holds every demo controller.
        private static readonly string[] StrictScanRoots =
        {
            "CoreAI/Runtime",
            "CoreAIHub/Runtime",
            "CoreAIMods/Runtime",
            "CoreAiUnity/Runtime",
            "CoreAI.Demos"
        };

        private static readonly string[] EditorScanRoots =
        {
            "CoreAI/Editor",
            "CoreAIHub/Editor",
            "CoreAIMods/Editor",
            "CoreAiUnity/Editor"
        };

        // ------------------------------------------------------------------------------------------
        // IMGUI WHITELIST — THIS LIST MAY ONLY SHRINK.
        //
        // Each entry is a runtime/demo file (path relative to Assets/) that still uses IMGUI today.
        // When a file is migrated to UITK / uGUI, DELETE its line here. NEVER add a new line: a brand
        // new IMGUI file must turn this test red so the ratchet holds. Seeded from a real grep of the
        // tree on 2026-07-22 (tokens: OnGUI( / GUILayout. / GUIStyle / GUI. etc.).
        // ------------------------------------------------------------------------------------------
        private static readonly HashSet<string> Whitelist = new(System.StringComparer.OrdinalIgnoreCase)
        {
            // --- CoreAI.Demos: legacy OnGUI demo controllers (to be rebuilt on the shared DemoPanel) ---

            // --- CoreAiUnity runtime diagnostics overlays (dev-only OnGUI HUDs) ---
            "CoreAiUnity/Runtime/Source/Features/Dashboard/Presentation/AiDashboardPresenter.cs", // OnGUI GUI.* dashboard presenter
            "CoreAiUnity/Runtime/Source/Features/Diagnostics/CoreAiTokenBudgetOverlay.cs", // OnGUI GUILayout token-budget overlay
            "CoreAiUnity/Runtime/Source/Features/Diagnostics/OrchestrationDashboard.cs" // OnGUI GUILayout orchestration dashboard
        };

        [Test]
        public void RuntimeAndDemos_HaveNoUnwhitelistedImgui()
        {
            List<string> offenders = new();
            foreach (string root in StrictScanRoots)
            {
                foreach ((string relative, _) in EnumerateImguiFiles(root))
                {
                    if (!Whitelist.Contains(relative))
                    {
                        offenders.Add(relative);
                    }
                }
            }

            offenders.Sort();
            Assert.IsEmpty(offenders,
                "New IMGUI in runtime/demo code is banned — build the UI with UITK / uGUI instead. " +
                "If IMGUI here is genuinely unavoidable, that is a design smell, not a whitelist entry:\n" +
                string.Join("\n", offenders));
        }

        [Test]
        public void Whitelist_OnlyShrinks_NoStaleOrMissingEntries()
        {
            List<string> stale = new();
            foreach (string relative in Whitelist)
            {
                string full = Path.Combine(Application.dataPath, relative.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(full))
                {
                    stale.Add($"{relative}  (file no longer exists)");
                    continue;
                }

                if (!ImguiToken.IsMatch(File.ReadAllText(full)))
                {
                    stale.Add($"{relative}  (no IMGUI left — migrated, delete this entry)");
                }
            }

            stale.Sort();
            Assert.IsEmpty(stale,
                "The IMGUI whitelist may only shrink. Delete these stale entries — the files were migrated " +
                "or removed:\n" + string.Join("\n", stale));
        }

        [Test]
        public void EditorImgui_IsReportedButNotFailed()
        {
            List<string> editorImgui = new();
            foreach (string root in EditorScanRoots)
            {
                foreach ((string relative, _) in EnumerateImguiFiles(root))
                {
                    editorImgui.Add(relative);
                }
            }

            editorImgui.Sort();
            if (editorImgui.Count > 0)
            {
                Debug.Log($"[ImguiBanRatchet] Editor IMGUI remaining (allowed, not failed) — " +
                          $"{editorImgui.Count} file(s):\n{string.Join("\n", editorImgui)}");
            }

            Assert.Pass($"Editor IMGUI is allowed; {editorImgui.Count} file(s) reported.");
        }

        /// <summary>
        /// Yields (relativePath, fullPath) for every .cs file under <paramref name="root"/> (relative to
        /// Assets/) whose text contains an IMGUI token. Missing roots yield nothing.
        /// </summary>
        private static IEnumerable<(string relative, string full)> EnumerateImguiFiles(string root)
        {
            string rootFull = Path.Combine(Application.dataPath, root.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(rootFull))
            {
                yield break;
            }

            string assetsFull = Path.GetFullPath(Application.dataPath).Replace('\\', '/').TrimEnd('/') + "/";
            foreach (string file in Directory.GetFiles(rootFull, "*.cs", SearchOption.AllDirectories))
            {
                if (!ImguiToken.IsMatch(File.ReadAllText(file)))
                {
                    continue;
                }

                string normalized = Path.GetFullPath(file).Replace('\\', '/');
                string relative = normalized.StartsWith(assetsFull, System.StringComparison.OrdinalIgnoreCase)
                    ? normalized.Substring(assetsFull.Length)
                    : normalized;
                yield return (relative, normalized);
            }
        }
    }
}
