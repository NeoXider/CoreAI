using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using CoreAI.Composition;
using CoreAI.Infrastructure;
using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Prompts;
using CoreAI.Infrastructure.World;
using CoreAI.Presentation.AiDashboard;

namespace CoreAI.Editor
{
    /// <summary>
    /// Unity editor menu commands for creating CoreAI assets, opening demo scenes,
    /// validating scene setup, and managing CoreAI persistent data.
    /// </summary>
    public static class CoreAIBuildMenu
    {
        private const string MainCoreAiScene = "Assets/CoreAiUnity/Scenes/_mainCoreAI.unity";
        private const string RogueliteArenaScene = "Assets/_exampleGame/Scenes/RogueliteArena.unity";
        private const string SettingsRoot = "Assets/CoreAiUnity/Settings";
        private const string LogSettingsPath = SettingsRoot + "/GameLogSettings.asset";
        private const string CoreAiSettingsPath = "Assets/Resources/CoreAISettings.asset";
        private const string PromptsManifestPath = SettingsRoot + "/AgentPromptsManifest.asset";
        private const string PrefabRegistryPath = SettingsRoot + "/CoreAiPrefabRegistry.asset";
        private const string AiPermissionsPath = SettingsRoot + "/AiPermissions.asset";
        private const string LlmRoutingPath = SettingsRoot + "/LlmRoutingManifest.asset";

        /// <summary>Moves the packaged `_mainCoreAI` scene to the first build-settings slot.</summary>
        [MenuItem("CoreAI/Development/Set _mainCoreAI as first build scene")]
        public static void SetMainCoreAiFirstInBuild()
        {
            MoveSceneFirstInBuild(MainCoreAiScene, "_mainCoreAI");
        }

        /// <summary>Opens the packaged `_mainCoreAI` scene in the editor.</summary>
        [MenuItem("CoreAI/Development/Open _mainCoreAI scene")]
        public static void OpenMainCoreAiScene()
        {
            EditorSceneManager.OpenScene(MainCoreAiScene);
        }

        /// <summary>Opens the RogueliteArena example scene in the editor.</summary>
        [MenuItem("CoreAI/Development/Example Game/Open RogueliteArena scene")]
        public static void OpenRogueliteArenaScene()
        {
            EditorSceneManager.OpenScene(RogueliteArenaScene);
        }

        /// <summary>Moves the RogueliteArena example scene to the first build-settings slot.</summary>
        [MenuItem("CoreAI/Development/Example Game/Set RogueliteArena as first build scene")]
        public static void SetRogueliteArenaFirstInBuild()
        {
            MoveSceneFirstInBuild(RogueliteArenaScene, "RogueliteArena");
        }

        /// <summary>
        /// Deletes all CoreAI files under <see cref="Application.persistentDataPath"/> after user confirmation.
        /// </summary>
        [MenuItem("CoreAI/Delete All Persistent Saves...", false, 60)]
        public static void DeleteAllPersistentSaves()
        {
            if (EditorApplication.isPlaying)
            {
                EditorUtility.DisplayDialog(
                    "CoreAI",
                    "Stored data cannot be deleted while Play Mode is running. Stop playback and try again.",
                    "OK");
                return;
            }

            string root = Path.Combine(Application.persistentDataPath, CoreAiPersistentPaths.RootFolderName);
            if (!EditorUtility.DisplayDialog(
                    "CoreAI - Delete Persistent Saves",
                    "Delete all CoreAI files in persistentDataPath?\n\n" +
                    "- AgentMemory: agent memory and chat history\n" +
                    "- ConversationSummaries: compacted conversation summaries\n" +
                    "- LuaScriptVersions: Lua Programmer versions\n" +
                    "- DataOverlayVersions: data overlay versions\n\n" +
                    root,
                    "Delete",
                    "Cancel"))
            {
                return;
            }

            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                    CoreAIEditorLog.Log($"Persistent saves deleted: {root}");
                    EditorUtility.DisplayDialog("CoreAI", "Folder deleted:\n" + root, "OK");
                }
                else
                {
                    CoreAIEditorLog.Log($"CoreAI persistent folder not found (nothing to delete): {root}");
                    EditorUtility.DisplayDialog("CoreAI", "Folder not found; nothing to delete:\n" + root, "OK");
                }
            }
            catch (Exception ex)
            {
                CoreAIEditorLog.LogError($"Failed to delete persistent saves: {ex.Message}");
                EditorUtility.DisplayDialog("CoreAI", "Failed to delete folder:\n" + ex.Message, "OK");
            }
        }

        /// <summary>Opens or creates the project-level CoreAI settings asset.</summary>
        [MenuItem("CoreAI/Settings", priority = 1)]
        public static void OpenSettings()
        {
            EnsureFolder("Assets/Resources");
            CoreAISettingsAsset settings = EnsureAsset<CoreAISettingsAsset>(CoreAiSettingsPath);
            Selection.activeObject = settings;
            EditorGUIUtility.PingObject(settings);
        }

        /// <summary>
        /// Creates missing default CoreAI settings, prompt, logging, permission, routing, and prefab assets.
        /// Explicit by design: importing the package must never write into the consumer's <c>Assets/</c>,
        /// least of all a <c>Resources/CoreAISettings.asset</c> that then ships inside their player.
        /// </summary>
        [MenuItem("CoreAI/Setup/Create Default Assets", priority = 2)]
        public static void CreateDefaultAssets()
        {
            EnsureFolder(SettingsRoot);
            EnsureFolder("Assets/Resources");

            GameLogSettingsAsset logSettings = EnsureAsset<GameLogSettingsAsset>(LogSettingsPath);

            CoreAISettingsAsset coreAiSettings = Resources.Load<CoreAISettingsAsset>("CoreAISettings");
            if (coreAiSettings == null)
            {
                coreAiSettings = EnsureAsset<CoreAISettingsAsset>(CoreAiSettingsPath);
            }

            AgentPromptsManifest prompts = EnsureAsset<AgentPromptsManifest>(PromptsManifestPath);
            CoreAiPrefabRegistryAsset prefabs = EnsureAsset<CoreAiPrefabRegistryAsset>(PrefabRegistryPath);
            AiPermissionsAsset permissions = EnsureAsset<AiPermissionsAsset>(AiPermissionsPath);
            LlmRoutingManifest routing = EnsureAsset<LlmRoutingManifest>(LlmRoutingPath);

            TryAssignToScope(logSettings, coreAiSettings, prompts, prefabs);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            CoreAIEditorLog.Log("Default CoreAI assets auto-generated and configured.");
        }

        /// <summary>Validates the active scene for the minimum CoreAI runtime setup.</summary>
        [MenuItem("CoreAI/Setup/Validate Scene", priority = 3)]
        public static void ValidateScene()
        {
            CoreAILifetimeScope scope = UnityEngine.Object.FindFirstObjectByType<CoreAILifetimeScope>();
            if (scope == null)
            {
                CoreAIEditorLog.LogError("Validate Scene: CoreAILifetimeScope is missing in scene.");
                return;
            }

            SerializedObject so = new(scope);
            int issues = 0;

            SerializedProperty log = so.FindProperty("gameLogSettings");
            if (log == null || log.objectReferenceValue == null)
            {
                issues++;
                CoreAIEditorLog.LogWarning("Validate Scene: Game Log Settings not assigned.");
            }

            CoreAiLuaWorldModule luaWorldModule = scope.LuaWorldModule;
            SerializedProperty world = so.FindProperty("worldPrefabRegistry");
            if ((luaWorldModule == null || luaWorldModule.WorldPrefabRegistry == null) &&
                (world == null || world.objectReferenceValue == null))
            {
                issues++;
                CoreAIEditorLog.LogWarning("Validate Scene: World Prefab Registry not assigned.");
            }

            SerializedProperty openAiRef = so.FindProperty("openAiHttpLlmSettings");
            bool hasLlmUnityAgent = TryFindMonoBehaviourByTypeName("LLMAgent") != null;
            if ((openAiRef == null || openAiRef.objectReferenceValue == null) && !hasLlmUnityAgent)
            {
                issues++;
                CoreAIEditorLog.LogWarning(
                    "Validate Scene: neither OpenAI HTTP settings nor LLMAgent found (will fallback to StubLlmClient).");
            }

            if (issues == 0)
            {
                CoreAIEditorLog.Log("Validate Scene: OK. CoreAILifetimeScope configuration looks good.");
            }
            else
            {
                CoreAIEditorLog.LogWarning(
                    $"Validate Scene: found {issues} issue(s). Use CoreAI/Setup/Create Default Assets.");
            }
        }

        /// <summary>Creates a minimal CoreAI lifetime-scope scene setup without the demo chat UI.</summary>
        [MenuItem("CoreAI/Setup/Create Bare Scene (advanced)", priority = 8)]
        public static void CreateSceneSetup()
        {
            CoreAILifetimeScope existingScope = UnityEngine.Object.FindFirstObjectByType<CoreAILifetimeScope>();
            if (existingScope != null)
            {
                if (!EditorUtility.DisplayDialog(
                        "CoreAI - Bare Scene Setup",
                        "CoreAILifetimeScope already exists in this scene.\nSelect it in the Inspector?",
                        "Select", "Cancel"))
                {
                    return;
                }

                Selection.activeGameObject = existingScope.gameObject;
                EditorGUIUtility.PingObject(existingScope);
                return;
            }

            CreateDefaultAssets();

            GameObject scopeGo = new("CoreAILifetimeScope");
            Undo.RegisterCreatedObjectUndo(scopeGo, "Create CoreAI Bare Scene Setup");
            CoreAILifetimeScope scope = scopeGo.AddComponent<CoreAILifetimeScope>();

            GameLogSettingsAsset logSettings =
                AssetDatabase.LoadAssetAtPath<GameLogSettingsAsset>(LogSettingsPath);
            CoreAISettingsAsset coreAiSettings =
                AssetDatabase.LoadAssetAtPath<CoreAISettingsAsset>(CoreAiSettingsPath);
            AgentPromptsManifest prompts =
                AssetDatabase.LoadAssetAtPath<AgentPromptsManifest>(PromptsManifestPath);
            CoreAiPrefabRegistryAsset prefabs =
                AssetDatabase.LoadAssetAtPath<CoreAiPrefabRegistryAsset>(PrefabRegistryPath);
            LlmRoutingManifest routing =
                AssetDatabase.LoadAssetAtPath<LlmRoutingManifest>(LlmRoutingPath);

            SerializedObject so = new(scope);
            SetPropertyIfExists(so, "gameLogSettings", logSettings);
            SetPropertyIfExists(so, "coreAiSettings", coreAiSettings);
            SetPropertyIfExists(so, "agentPromptsManifest", prompts);
            SetPropertyIfExists(so, "llmRoutingManifest", routing);
            so.ApplyModifiedPropertiesWithoutUndo();
            EnsureLuaWorldModule(scope, prefabs);

            bool needsLlmUnity = NeedsLlmUnity(coreAiSettings);
            if (needsLlmUnity)
            {
                TryCreateLlmUnityObjects(scopeGo);
            }

            EditorUtility.SetDirty(scope);
            EditorSceneManager.MarkSceneDirty(scopeGo.scene);
            Selection.activeGameObject = scopeGo;
            EditorGUIUtility.PingObject(scopeGo);

            CoreAIEditorLog.Log(
                "Bare scene: CoreAILifetimeScope created" +
                (needsLlmUnity ? " + LLM + LLMAgent." : "."));
        }

        /// <summary>
        /// Explicitly creates the <c>LLM</c> + <c>LLMAgent</c> objects in the current scene, regardless
        /// of what <see cref="CoreAISettingsAsset.ExecutionMode"/>/<see cref="CoreAISettingsAsset.AutoPriority"/>
        /// currently say. Use this when you want a local LLMUnity host without recreating the whole scene
        /// (e.g. after switching an existing scene's backend to LLMUnity by hand).
        /// </summary>
        [MenuItem("CoreAI/Setup/Create LLMUnity Objects (LLM + LLMAgent)", priority = 9)]
        public static void CreateLlmUnityObjectsMenuItem()
        {
            if (TryFindMonoBehaviourByTypeName("LLM") != null)
            {
                EditorUtility.DisplayDialog(
                    "CoreAI - LLMUnity Setup",
                    "LLM already exists in this scene.",
                    "OK");
                return;
            }

            CoreAILifetimeScope existingScope = UnityEngine.Object.FindFirstObjectByType<CoreAILifetimeScope>();
            TryCreateLlmUnityObjects(existingScope != null ? existingScope.gameObject : null);

            MonoBehaviour created = TryFindMonoBehaviourByTypeName("LLM");
            if (created != null)
            {
                Selection.activeGameObject = created.gameObject;
                EditorGUIUtility.PingObject(created.gameObject);
            }
        }

        /// <summary>
        /// True when <paramref name="settings"/> selects an execution mode that needs a local
        /// LLMUnity host in the scene (<see cref="LlmExecutionMode.LocalModel"/>, or
        /// <see cref="LlmExecutionMode.Auto"/> with <see cref="LlmAutoPriority.LlmUnityFirst"/>).
        /// Shared by every scene creator so "does this scene need LLM + LLMAgent" has one answer.
        /// </summary>
        internal static bool NeedsLlmUnity(CoreAISettingsAsset settings)
        {
            if (settings == null)
            {
                return false;
            }

            LlmExecutionMode mode = settings.ExecutionMode;
            return mode == LlmExecutionMode.LocalModel
                   || (mode == LlmExecutionMode.Auto && settings.AutoPriority == LlmAutoPriority.LlmUnityFirst);
        }

        private static void SetPropertyIfExists(SerializedObject so, string propertyName, UnityEngine.Object value)
        {
            SerializedProperty prop = so.FindProperty(propertyName);
            if (prop != null)
            {
                prop.objectReferenceValue = value;
            }
        }

        /// <summary>
        /// Creates LLMAgent scene objects needed for LLMUnity integration when they are missing.
        /// Internal (not private) so other scene creators in this namespace (e.g. the Chat Demo
        /// scene) can reuse the same "does this scene need LLMUnity objects" + creation logic
        /// instead of duplicating it.
        /// </summary>
        internal static void TryCreateLlmUnityObjects(GameObject parentScope)
        {
#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL
            try
            {
                if (TryFindMonoBehaviourByTypeName("LLM") != null)
                {
                    CoreAIEditorLog.Log("Scene Setup: LLM already exists in the scene; skipping creation.");
                    return;
                }

                GameObject llmGo = new("CoreAI_LLM");
                Undo.RegisterCreatedObjectUndo(llmGo, "Create LLM + LLMAgent");

                LLMUnity.LLM llm = llmGo.AddComponent<LLMUnity.LLM>();
                LLMUnity.LLMAgent agent = llmGo.AddComponent<LLMUnity.LLMAgent>();

                llm.dontDestroyOnLoad = false;
                llm.flashAttention = true;
                llm.numGPULayers = 99;

                agent.remote = false;
                agent.llm = llm;

                // CoreAI builds the whole prompt and calls Chat(addToHistory: false), so LLMUnity's own
                // context-overflow handling never has history to act on. Force it to None (mirrors the
                // runtime LlmUnityHostConfigurator) so the Inspector doesn't imply LLMUnity manages the
                // context and it can never truncate/summarize behind CoreAI's back.
                agent.overflowStrategy = UndreamAI.LlamaLib.ContextOverflowStrategy.None;

                CoreAISettingsAsset settings = AssetDatabase.LoadAssetAtPath<CoreAISettingsAsset>(CoreAiSettingsPath);
                if (settings != null)
                {
                    string gguf = settings.GgufModelPath;
                    if (!string.IsNullOrWhiteSpace(gguf))
                    {
                        IGameLogger log = GameLoggerUnscopedFallback.Instance;
                        LlmUnityModelBootstrap.TryAssignModelMatchingFilename(llm, log, gguf);
                    }
                }

                EditorUtility.SetDirty(llmGo);
                CoreAIEditorLog.Log($"Scene Setup: LLM + LLMAgent created (model: {llm.model ?? "not assigned"}).");
            }
            catch (Exception ex)
            {
                CoreAIEditorLog.LogWarning($"Scene Setup: failed to create LLM objects: {ex.Message}");
            }
#else
            CoreAIEditorLog.LogWarning(
                "Scene Setup: LLMUnity is unavailable (package not installed or UNITY_WEBGL). LLM and LLMAgent were not created.");
#endif
        }

        private static void MoveSceneFirstInBuild(string path, string labelForLog)
        {
            EditorBuildSettingsScene[] scenes = EditorBuildSettings.scenes;
            bool found = false;
            for (int i = 0; i < scenes.Length; i++)
            {
                if (scenes[i].path != path)
                {
                    continue;
                }

                found = true;
                if (i == 0)
                {
                    CoreAIEditorLog.Log($"{labelForLog} is already first in Build Settings.");
                    return;
                }

                EditorBuildSettingsScene first = scenes[0];
                scenes[0] = scenes[i];
                scenes[i] = first;
                break;
            }

            if (!found)
            {
                EditorBuildSettingsScene[] list = new EditorBuildSettingsScene[scenes.Length + 1];
                list[0] = new EditorBuildSettingsScene(path, true);
                for (int i = 0; i < scenes.Length; i++)
                {
                    list[i + 1] = scenes[i];
                }

                scenes = list;
            }

            EditorBuildSettings.scenes = scenes;
            CoreAIEditorLog.Log($"Build Settings: first scene is {labelForLog}.");
        }

        private static void EnsureFolder(string folderPath)
        {
            if (AssetDatabase.IsValidFolder(folderPath))
            {
                return;
            }

            string[] parts = folderPath.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, parts[i]);
                }

                current = next;
            }
        }

        /// <summary>
        /// <see cref="ScriptableObject.CreateInstance{T}"/> does not always run <c>Reset()</c>; ensure new
        /// <see cref="CoreAISettingsAsset"/> files get global streaming + WebGL fetch SSE on by default.
        /// </summary>
        private static void ApplyCoreAiSettingsStreamingDefaultsIfApplicable(ScriptableObject created)
        {
            if (created is not CoreAISettingsAsset)
            {
                return;
            }

            SerializedObject so = new(created);
            SerializedProperty enable = so.FindProperty("enableStreaming");
            if (enable != null && enable.propertyType == SerializedPropertyType.Boolean)
            {
                enable.boolValue = true;
            }

            SerializedProperty webGl = so.FindProperty("webGlNativeStreaming");
            if (webGl != null && webGl.propertyType == SerializedPropertyType.Boolean)
            {
                webGl.boolValue = true;
            }

            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static T EnsureAsset<T>(string assetPath) where T : ScriptableObject
        {
            T existing = AssetDatabase.LoadAssetAtPath<T>(assetPath);
            if (existing != null)
            {
                return existing;
            }

            T created = ScriptableObject.CreateInstance<T>();
            ApplyCoreAiSettingsStreamingDefaultsIfApplicable(created);
            AssetDatabase.CreateAsset(created, assetPath);
            return created;
        }

        /// <summary>Optional LLMUnity integration: no compile-time reference to the package.</summary>
        private static MonoBehaviour TryFindMonoBehaviourByTypeName(string typeName)
        {
            foreach (MonoBehaviour mb in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(
                         FindObjectsInactive.Include,
                         FindObjectsSortMode.None))
            {
                if (mb != null && mb.GetType().Name == typeName)
                {
                    return mb;
                }
            }

            return null;
        }

        private static void TryAssignToScope(
            GameLogSettingsAsset logSettings,
            CoreAISettingsAsset coreAiSettings,
            AgentPromptsManifest prompts,
            CoreAiPrefabRegistryAsset prefabs)
        {
            CoreAILifetimeScope scope = UnityEngine.Object.FindFirstObjectByType<CoreAILifetimeScope>();
            if (scope == null)
            {
                return;
            }

            SerializedObject so = new(scope);
            so.FindProperty("gameLogSettings").objectReferenceValue = logSettings;

            // WHY: Older settings assets may still expose the retired HTTP settings reference.
            SerializedProperty legacyOpenAiProp = so.FindProperty("openAiHttpLlmSettings");
            if (legacyOpenAiProp != null)
            {
                legacyOpenAiProp.objectReferenceValue = null;
            }

            so.FindProperty("agentPromptsManifest").objectReferenceValue = prompts;
            so.ApplyModifiedPropertiesWithoutUndo();
            EnsureLuaWorldModule(scope, prefabs);
            EditorUtility.SetDirty(scope);
            if (scope.gameObject.scene.IsValid())
            {
                EditorSceneManager.MarkSceneDirty(scope.gameObject.scene);
            }
        }

        private static CoreAiLuaWorldModule EnsureLuaWorldModule(
            CoreAILifetimeScope scope,
            CoreAiPrefabRegistryAsset prefabs)
        {
            CoreAiLuaWorldModule module = scope.LuaWorldModule;
            if (module == null)
            {
                GameObject child = new("Lua and World Commands");
                Undo.RegisterCreatedObjectUndo(child, "Add CoreAI Lua module");
                child.transform.SetParent(scope.transform, false);
                module = Undo.AddComponent<CoreAiLuaWorldModule>(child);
                scope.CopyLegacyLuaWorldConfigurationTo(module);
            }

            SerializedObject moduleSo = new(module);
            moduleSo.FindProperty("worldPrefabRegistry").objectReferenceValue = prefabs;
            moduleSo.ApplyModifiedPropertiesWithoutUndo();
            scope.SetLuaWorldModuleForMigration(module);
            EditorUtility.SetDirty(module);
            return module;
        }
    }

    /// <summary>
    /// Repeatable batch-mode entry point for the G11 WebGL browser-gate player.
    /// </summary>
    public static class CoreAIG11WebGlBuild
    {
        internal const string RelativeOutputPath = "artifacts/G11-WebGL";

        private static readonly string[] FrozenScenePaths =
        {
            "Assets/CoreAI.Demos/FullAccess/FullAccessDemo.unity",
            "Assets/CoreAI.Demos/Hub/CoreAiHubDemo.unity",
            "Assets/CoreAiUnity/Scenes/CoreAiChatDemo.unity"
        };

        /// <summary>
        /// Builds the frozen G11 scene set into <c>artifacts/G11-WebGL</c> without prompts.
        /// Launch Unity with <c>-buildTarget WebGL</c> because batch mode cannot switch targets while
        /// an execute method is running.
        /// </summary>
        public static void Build()
        {
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.WebGL)
            {
                throw new BuildFailedException(
                    "G11 WebGL build requires an active WebGL target. Relaunch Unity with '-buildTarget WebGL'.");
            }

            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string outputPath = GetOutputPath(projectRoot);
            ValidateScenes(projectRoot);
            PrepareOutputDirectory(projectRoot, outputPath);

            BuildPlayerOptions options = CreateBuildPlayerOptions(outputPath);
            CoreAIEditorLog.Log(
                $"G11 WebGL build started: {FrozenScenePaths.Length} scenes -> {outputPath}");
            BuildReport report = BuildPipeline.BuildPlayer(options);
            if (report.summary.result != BuildResult.Succeeded)
            {
                throw new BuildFailedException(
                    $"G11 WebGL build failed with result '{report.summary.result}', " +
                    $"{report.summary.totalErrors} error(s), and {report.summary.totalWarnings} warning(s).");
            }

            CoreAIEditorLog.Log(
                $"G11 WebGL build succeeded: {report.summary.totalSize} bytes in {report.summary.totalTime}.");
        }

        /// <summary>Returns an isolated copy of the frozen G11 scene list.</summary>
        internal static string[] GetFrozenScenePaths()
        {
            return (string[])FrozenScenePaths.Clone();
        }

        /// <summary>Creates the fully explicit player-build request used by the entry point.</summary>
        internal static BuildPlayerOptions CreateBuildPlayerOptions(string outputPath)
        {
            return new BuildPlayerOptions
            {
                scenes = GetFrozenScenePaths(),
                locationPathName = outputPath,
                target = BuildTarget.WebGL,
                targetGroup = BuildTargetGroup.WebGL,
                options = BuildOptions.CleanBuildCache | BuildOptions.StrictMode
            };
        }

        /// <summary>Returns the fixed G11 output directory for a project root.</summary>
        internal static string GetOutputPath(string projectRoot)
        {
            return Path.GetFullPath(Path.Combine(projectRoot, RelativeOutputPath));
        }

        /// <summary>Removes stale G11 output and recreates the exact fixed directory.</summary>
        internal static void PrepareOutputDirectory(string projectRoot, string outputPath)
        {
            string expectedOutputPath = GetOutputPath(projectRoot);
            StringComparison comparison = Path.DirectorySeparatorChar == '\\'
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!string.Equals(Path.GetFullPath(outputPath), expectedOutputPath, comparison))
            {
                throw new BuildFailedException(
                    $"Refusing to clean unexpected G11 output path '{outputPath}'. Expected '{expectedOutputPath}'.");
            }

            if (Directory.Exists(expectedOutputPath))
            {
                Directory.Delete(expectedOutputPath, true);
            }

            Directory.CreateDirectory(expectedOutputPath);
        }

        private static void ValidateScenes(string projectRoot)
        {
            foreach (string scenePath in FrozenScenePaths)
            {
                string absoluteScenePath = Path.GetFullPath(Path.Combine(projectRoot, scenePath));
                if (!File.Exists(absoluteScenePath))
                {
                    throw new BuildFailedException($"G11 WebGL scene is missing: '{scenePath}'.");
                }
            }
        }
    }
}
