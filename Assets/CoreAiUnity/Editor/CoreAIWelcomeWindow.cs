using System.IO;
using CoreAI.Infrastructure.Llm;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace CoreAI.Editor
{
    /// <summary>
    /// One-stop "Getting Started" window: the single entry point a new user sees. It surfaces the fastest
    /// path to a running scene, a one-click launcher for every bundled demo (with a one-line description and
    /// whether it needs a live model), the current backend status, and links to the key docs — so the 10+
    /// demos are discoverable instead of buried under menus. Opens automatically on first import and via
    /// <c>CoreAI → Getting Started</c>.
    /// </summary>
    public sealed class CoreAIWelcomeWindow : EditorWindow
    {
        private const string AutoShownKey = "CoreAI.Welcome.AutoShown.v1";
        private const string DemosRoot = "Assets/CoreAI.Demos/";
        private const string DocsRoot = "Assets/CoreAiUnity/Docs/";

        private Vector2 _scroll;

        private readonly struct Demo
        {
            /// <summary>Scene asset name WITHOUT extension — resolved by AssetDatabase so it works whether the
            /// demos live under Assets/ (dev monorepo) or Packages/ (UPM install) or an imported Sample.</summary>
            public readonly string SceneName;
            public readonly string Title;
            public readonly string Blurb;
            public readonly bool NeedsLlm;
            public readonly bool Featured;

            public Demo(string title, string sceneName, string blurb, bool needsLlm, bool featured)
            {
                Title = title;
                SceneName = sceneName;
                Blurb = blurb;
                NeedsLlm = needsLlm;
                Featured = featured;
            }
        }

        // Descriptions mirror Assets/CoreAI.Demos/README.md. Featured = the strongest WOW demos, shown first.
        // Scenes are referenced by NAME (no path) so the launcher is package-path-safe.
        private static readonly Demo[] Demos =
        {
            new("🔥 Live Mechanics — AI changes the game from chat",
                "LiveMechanicsDemo",
                "A real LLM rewrites gameplay live: the Programmer role writes Lua through execute_lua → logic slots / mods / world commands.",
                needsLlm: true, featured: true),
            new("🔥 Moddable Units — a whole game built from mods",
                "ModdableUnitsDemo",
                "Mods define new unit types and armies (forge_define / forge_spawn) and drive the fight via hooks; the host just runs the auto-battle.",
                needsLlm: true, featured: true),
            new("🔥 Hub — drop-in AI control panel",
                "CoreAiHubDemo",
                "A ready UI Toolkit Hub with Chat, Settings, Statistics, Mods, and World State pages — the fastest way to feel the whole stack.",
                needsLlm: true, featured: true),
            new("Skills — agents load tools on demand",
                "SkillsDemo",
                "SkillSet + AgentBuilder: a game-master agent with crafting/combat skills via read_skill / call_skill_tool.",
                needsLlm: true, featured: false),
            new("Full Access — Programmer inspects & moves the scene",
                "FullAccessDemo",
                "Opt-in full-tier unity_* access: inspect objects/components/transforms, then move/rotate/parent from Lua.",
                needsLlm: true, featured: false),
            new("Mini RPG — first-person world + Hub chat",
                "MiniRpgModsDemo",
                "A small first-person environment wired to the Hub chat with mod-ready prompts.",
                needsLlm: true, featured: false),
            new("Wave Auto-Battler — rules changed by mods",
                "WaveAutoBattlerModsDemo",
                "A playable wave loop whose rules and rewards are edited by persistent Lua mods (F9 mods panel, F10 usage overlay).",
                needsLlm: true, featured: false),
            new("Live Mechanics Mods Chat — persistent manage_mods",
                "LiveMechanicsModsChatDemo",
                "Chat-driven load/reload/unload of Lua mods that persist and autoload on the next scene start.",
                needsLlm: true, featured: false),
            new("Lua Mods — hooks, timers, events (offline)",
                "LuaModsDemo",
                "Lua mod hooks/timers/events/store + capability tiers; override the damage formula from Lua. Runs with no model.",
                needsLlm: false, featured: false),
            new("World Commands — the AI command pipeline (offline)",
                "WorldCommandsDemo",
                "IAiGameCommandSink → AiGameCommandRouter → world executor: the same path LLM agents and Lua use. Runs with no model.",
                needsLlm: false, featured: false),
        };

        /// <summary>Resolves a scene asset path by name across Assets/ and Packages/, or null if not found.</summary>
        private static string ResolveScenePath(string sceneName)
        {
            string[] guids = AssetDatabase.FindAssets($"{sceneName} t:Scene");
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (Path.GetFileNameWithoutExtension(path) == sceneName)
                {
                    return path;
                }
            }

            return null;
        }

        [MenuItem("CoreAI/Getting Started", priority = 0)]
        public static void Open()
        {
            CoreAIWelcomeWindow window = GetWindow<CoreAIWelcomeWindow>(true, "CoreAI — Getting Started");
            window.minSize = new Vector2(520, 560);
            window.Show();
        }

        /// <summary>Shows the window once per project on load, so a fresh import lands on a friendly start page.</summary>
        [InitializeOnLoadMethod]
        private static void MaybeAutoShow()
        {
            EditorApplication.delayCall += () =>
            {
                if (EditorPrefs.GetBool(AutoShownKey, false) || Application.isBatchMode)
                {
                    return;
                }

                EditorPrefs.SetBool(AutoShownKey, true);
                Open();
            };
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            DrawHeader();
            DrawBackendStatus();
            DrawQuickStart();
            DrawDemos();
            DrawDocs();

            EditorGUILayout.Space(8);
            EditorGUILayout.EndScrollView();
        }

        private static void DrawHeader()
        {
            EditorGUILayout.Space(6);
            GUIStyle title = new(EditorStyles.boldLabel) { fontSize = 17 };
            EditorGUILayout.LabelField("CoreAI — LLM agents that play your game", title);
            EditorGUILayout.LabelField(
                "Function calling, tools, memory and runtime Lua — on a local model or any OpenAI-compatible API.",
                EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space(6);
        }

        private void DrawBackendStatus()
        {
            CoreAISettingsAsset settings = CoreAISettingsAsset.Instance;
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Backend", EditorStyles.boldLabel);
                if (settings == null)
                {
                    EditorGUILayout.HelpBox(
                        "No CoreAISettings found yet. Click \"Create Chat Demo Scene\" below — it creates the " +
                        "default settings for you. Set a local LM Studio / LLMUnity model or an OpenAI-compatible " +
                        "API in CoreAI → Settings to bring the AI demos to life.",
                        MessageType.Info);
                }
                else
                {
                    EditorGUILayout.LabelField("Mode / backend", settings.BackendType.ToString());
                    string model = string.IsNullOrWhiteSpace(settings.ModelName) ? "(unset)" : settings.ModelName;
                    EditorGUILayout.LabelField("Model", model);
                    EditorGUILayout.HelpBox(
                        "Every demo OPENS without a model. Demos marked \"needs model\" only come alive with a " +
                        "configured backend (local GGUF or OpenAI-compatible API).",
                        MessageType.None);
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Open Settings"))
                    {
                        EditorApplication.ExecuteMenuItem("CoreAI/Settings");
                    }

                    if (GUILayout.Button("Recommended models (docs)"))
                    {
                        OpenDoc("LLMUNITY_SETUP_AND_MODELS", DocsRoot + "LLMUNITY_SETUP_AND_MODELS.md");
                    }
                }
            }

            EditorGUILayout.Space(4);
        }

        private static void DrawQuickStart()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("30-second start", EditorStyles.boldLabel);
                EditorGUILayout.LabelField(
                    "Create a working streaming chat scene, press Play, and type.",
                    EditorStyles.wordWrappedLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Create Chat Demo Scene", GUILayout.Height(28)))
                    {
                        EditorApplication.ExecuteMenuItem("CoreAI/Setup/Create Chat Demo Scene");
                    }

                    if (GUILayout.Button("Validate Scene", GUILayout.Height(28)))
                    {
                        EditorApplication.ExecuteMenuItem("CoreAI/Setup/Validate Scene");
                    }
                }
            }

            EditorGUILayout.Space(4);
        }

        private void DrawDemos()
        {
            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("Demos — open in one click", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "★ = best first impression. Each scene is self-contained.",
                EditorStyles.miniLabel);
            EditorGUILayout.Space(2);

            for (int i = 0; i < Demos.Length; i++)
            {
                DrawDemoRow(Demos[i]);
            }
        }

        private void DrawDemoRow(Demo demo)
        {
            string scenePath = ResolveScenePath(demo.SceneName);
            bool exists = !string.IsNullOrEmpty(scenePath);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    string label = (demo.Featured ? "★ " : "") + demo.Title;
                    EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.LabelField(
                        demo.NeedsLlm ? "needs model" : "offline",
                        EditorStyles.miniLabel, GUILayout.Width(80));
                }

                EditorGUILayout.LabelField(demo.Blurb, EditorStyles.wordWrappedMiniLabel);

                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(!exists))
                    {
                        if (GUILayout.Button(exists ? "Open scene" : "Scene not found", GUILayout.Width(140)))
                        {
                            OpenScene(scenePath);
                        }
                    }

                    // Sibling README, resolved relative to wherever the scene actually resolved.
                    string readme = exists ? Path.GetDirectoryName(scenePath) + "/README.md" : null;
                    bool hasReadme = readme != null && AssetDatabase.LoadAssetAtPath<TextAsset>(readme) != null;
                    using (new EditorGUI.DisabledScope(!hasReadme))
                    {
                        if (GUILayout.Button("README", GUILayout.Width(90)))
                        {
                            AssetDatabase.OpenAsset(AssetDatabase.LoadAssetAtPath<TextAsset>(readme));
                        }
                    }
                }
            }
        }

        private static void DrawDocs()
        {
            EditorGUILayout.Space(4);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Learn more", EditorStyles.boldLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Quick Start"))
                    {
                        OpenDoc("QUICK_START", DocsRoot + "QUICK_START.md");
                    }

                    if (GUILayout.Button("Full Walkthrough"))
                    {
                        OpenDoc("QUICK_START_FULL", DocsRoot + "QUICK_START_FULL.md");
                    }

                    if (GUILayout.Button("All demos (README)"))
                    {
                        OpenDoc("README", DemosRoot + "README.md");
                    }
                }
            }
        }

        private static void OpenScene(string scenePath)
        {
            if (string.IsNullOrEmpty(scenePath) || AssetDatabase.LoadAssetAtPath<SceneAsset>(scenePath) == null)
            {
                CoreAIEditorLog.LogError($"Demo scene not found: {scenePath}");
                return;
            }

            if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                EditorSceneManager.OpenScene(scenePath);
            }
        }

        /// <summary>
        /// Opens a Markdown doc, resolving it by asset NAME across Assets/ and Packages/ so it works in a UPM
        /// install. <paramref name="preferredPath"/> is tried first (fast path in the dev monorepo).
        /// </summary>
        private static void OpenDoc(string docName, string preferredPath = null)
        {
            string path = preferredPath;
            if (string.IsNullOrEmpty(path) || AssetDatabase.LoadAssetAtPath<TextAsset>(path) == null)
            {
                path = null;
                string bare = Path.GetFileNameWithoutExtension(docName);
                string[] guids = AssetDatabase.FindAssets($"{bare} t:TextAsset");
                for (int i = 0; i < guids.Length; i++)
                {
                    string candidate = AssetDatabase.GUIDToAssetPath(guids[i]);
                    if (Path.GetFileName(candidate).Equals(docName + ".md", System.StringComparison.OrdinalIgnoreCase)
                        || Path.GetFileNameWithoutExtension(candidate).Equals(bare, System.StringComparison.OrdinalIgnoreCase))
                    {
                        path = candidate;
                        break;
                    }
                }
            }

            TextAsset doc = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<TextAsset>(path);
            if (doc != null)
            {
                AssetDatabase.OpenAsset(doc);
            }
            else
            {
                CoreAIEditorLog.LogError($"Doc not found: {docName}");
            }
        }
    }
}
