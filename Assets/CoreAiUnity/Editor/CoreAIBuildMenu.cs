using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using CoreAI.Composition;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Prompts;
using CoreAI.Infrastructure.World;

namespace CoreAI.Editor
{
    public static class CoreAIBuildMenu
    {
        const string MainCoreAiScene = "Assets/CoreAiUnity/Scenes/_mainCoreAI.unity";
        const string RogueliteArenaScene = "Assets/_exampleGame/Scenes/RogueliteArena.unity";
        const string SettingsRoot = "Assets/CoreAiUnity/Settings";
        const string LogSettingsPath = SettingsRoot + "/GameLogSettings.asset";
        const string OpenAiSettingsPath = SettingsRoot + "/OpenAiHttpLlmSettings.asset";
        const string PromptsManifestPath = SettingsRoot + "/AgentPromptsManifest.asset";
        const string PrefabRegistryPath = SettingsRoot + "/CoreAiPrefabRegistry.asset";

        [MenuItem("CoreAI/Development/Set _mainCoreAI as first build scene")]
        public static void SetMainCoreAiFirstInBuild()
        {
            MoveSceneFirstInBuild(MainCoreAiScene, "_mainCoreAI");
        }

        [MenuItem("CoreAI/Development/Open _mainCoreAI scene")]
        public static void OpenMainCoreAiScene()
        {
            EditorSceneManager.OpenScene(MainCoreAiScene);
        }

        [MenuItem("CoreAI/Development/Example Game/Open RogueliteArena scene")]
        public static void OpenRogueliteArenaScene()
        {
            EditorSceneManager.OpenScene(RogueliteArenaScene);
        }

        [MenuItem("CoreAI/Development/Example Game/Set RogueliteArena as first build scene")]
        public static void SetRogueliteArenaFirstInBuild()
        {
            MoveSceneFirstInBuild(RogueliteArenaScene, "RogueliteArena");
        }

        [MenuItem("CoreAI/Setup/Create Default Assets")]
        public static void CreateDefaultAssets()
        {
            EnsureFolder(SettingsRoot);
            var logSettings = EnsureAsset<GameLogSettingsAsset>(LogSettingsPath);
            var openAi = EnsureAsset<OpenAiHttpLlmSettings>(OpenAiSettingsPath);
            var prompts = EnsureAsset<AgentPromptsManifest>(PromptsManifestPath);
            var prefabs = EnsureAsset<CoreAiPrefabRegistryAsset>(PrefabRegistryPath);
            TryAssignToScope(logSettings, openAi, prompts, prefabs);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            CoreAIEditorLog.Log("Default assets are created and assigned (if CoreAILifetimeScope exists).");
        }

        [MenuItem("CoreAI/Setup/Validate Scene")]
        public static void ValidateScene()
        {
            var scope = Object.FindFirstObjectByType<CoreAILifetimeScope>();
            if (scope == null)
            {
                CoreAIEditorLog.LogError("Validate Scene: CoreAILifetimeScope is missing in scene.");
                return;
            }

            var so = new SerializedObject(scope);
            var issues = 0;

            var log = so.FindProperty("gameLogSettings");
            if (log == null || log.objectReferenceValue == null)
            {
                issues++;
                CoreAIEditorLog.LogWarning("Validate Scene: Game Log Settings not assigned.");
            }

            var world = so.FindProperty("worldPrefabRegistry");
            if (world == null || world.objectReferenceValue == null)
            {
                issues++;
                CoreAIEditorLog.LogWarning("Validate Scene: World Prefab Registry not assigned.");
            }

            var openAiRef = so.FindProperty("openAiHttpLlmSettings");
            var hasLlmUnityAgent = TryFindMonoBehaviourByTypeName("LLMAgent") != null;
            if ((openAiRef == null || openAiRef.objectReferenceValue == null) && !hasLlmUnityAgent)
            {
                issues++;
                CoreAIEditorLog.LogWarning(
                    "Validate Scene: neither OpenAI HTTP settings nor LLMAgent found (will fallback to StubLlmClient).");
            }

            if (issues == 0)
                CoreAIEditorLog.Log("Validate Scene: OK. CoreAILifetimeScope configuration looks good.");
            else
                CoreAIEditorLog.LogWarning(
                    $"Validate Scene: found {issues} issue(s). Use CoreAI/Setup/Create Default Assets.");
        }

        static void MoveSceneFirstInBuild(string path, string labelForLog)
        {
            var scenes = EditorBuildSettings.scenes;
            var found = false;
            for (var i = 0; i < scenes.Length; i++)
            {
                if (scenes[i].path != path)
                    continue;
                found = true;
                if (i == 0)
                {
                    CoreAIEditorLog.Log($"{labelForLog} уже первая в Build Settings.");
                    return;
                }

                var first = scenes[0];
                scenes[0] = scenes[i];
                scenes[i] = first;
                break;
            }

            if (!found)
            {
                var list = new EditorBuildSettingsScene[scenes.Length + 1];
                list[0] = new EditorBuildSettingsScene(path, true);
                for (var i = 0; i < scenes.Length; i++)
                    list[i + 1] = scenes[i];
                scenes = list;
            }

            EditorBuildSettings.scenes = scenes;
            CoreAIEditorLog.Log($"Build Settings: первая сцена — {labelForLog}.");
        }

        static void EnsureFolder(string folderPath)
        {
            if (AssetDatabase.IsValidFolder(folderPath))
                return;
            var parts = folderPath.Split('/');
            var current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                var next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        static T EnsureAsset<T>(string assetPath) where T : ScriptableObject
        {
            var existing = AssetDatabase.LoadAssetAtPath<T>(assetPath);
            if (existing != null)
                return existing;
            var created = ScriptableObject.CreateInstance<T>();
            AssetDatabase.CreateAsset(created, assetPath);
            return created;
        }

        /// <summary>Optional LLMUnity integration: no compile-time reference to the package.</summary>
        static MonoBehaviour TryFindMonoBehaviourByTypeName(string typeName)
        {
            foreach (var mb in Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (mb != null && mb.GetType().Name == typeName)
                    return mb;
            }

            return null;
        }

        static void TryAssignToScope(
            GameLogSettingsAsset logSettings,
            OpenAiHttpLlmSettings openAi,
            AgentPromptsManifest prompts,
            CoreAiPrefabRegistryAsset prefabs)
        {
            var scope = Object.FindFirstObjectByType<CoreAILifetimeScope>();
            if (scope == null)
                return;
            var so = new SerializedObject(scope);
            so.FindProperty("gameLogSettings").objectReferenceValue = logSettings;
            so.FindProperty("openAiHttpLlmSettings").objectReferenceValue = openAi;
            so.FindProperty("agentPromptsManifest").objectReferenceValue = prompts;
            so.FindProperty("worldPrefabRegistry").objectReferenceValue = prefabs;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(scope);
            if (scope.gameObject.scene.IsValid())
                EditorSceneManager.MarkSceneDirty(scope.gameObject.scene);
        }
    }
}
