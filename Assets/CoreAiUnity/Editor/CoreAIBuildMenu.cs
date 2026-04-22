using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using CoreAI.Composition;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Prompts;
using CoreAI.Infrastructure.World;
using CoreAI.Presentation.AiDashboard;

namespace CoreAI.Editor
{
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

        [MenuItem("CoreAI/Settings", priority = 1)]
        public static void OpenSettings()
        {
            EnsureFolder("Assets/Resources");
            CoreAISettingsAsset settings = EnsureAsset<CoreAISettingsAsset>(CoreAiSettingsPath);
            Selection.activeObject = settings;
            EditorGUIUtility.PingObject(settings);
        }

        [InitializeOnLoadMethod]
        private static void AutoCreateDefaultAssetsOnLoad()
        {
            // Auto-create on first plugin load if missing
            if (AssetDatabase.LoadAssetAtPath<CoreAISettingsAsset>(CoreAiSettingsPath) == null)
            {
                CreateDefaultAssets();
            }
        }

        [MenuItem("CoreAI/Setup/Create Default Assets", priority = 2)]
        public static void CreateDefaultAssets()
        {
            EnsureFolder(SettingsRoot);
            EnsureFolder("Assets/Resources");

            GameLogSettingsAsset logSettings = EnsureAsset<GameLogSettingsAsset>(LogSettingsPath);
            CoreAISettingsAsset coreAiSettings = EnsureAsset<CoreAISettingsAsset>(CoreAiSettingsPath);
            AgentPromptsManifest prompts = EnsureAsset<AgentPromptsManifest>(PromptsManifestPath);
            CoreAiPrefabRegistryAsset prefabs = EnsureAsset<CoreAiPrefabRegistryAsset>(PrefabRegistryPath);
            AiPermissionsAsset permissions = EnsureAsset<AiPermissionsAsset>(AiPermissionsPath);
            LlmRoutingManifest routing = EnsureAsset<LlmRoutingManifest>(LlmRoutingPath);

            TryAssignToScope(logSettings, coreAiSettings, prompts, prefabs);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            CoreAIEditorLog.Log("Default CoreAI assets auto-generated and configured.");
        }

        [MenuItem("CoreAI/Setup/Validate Scene", priority = 3)]
        public static void ValidateScene()
        {
            CoreAILifetimeScope scope = Object.FindFirstObjectByType<CoreAILifetimeScope>();
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

            SerializedProperty world = so.FindProperty("worldPrefabRegistry");
            if (world == null || world.objectReferenceValue == null)
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

        [MenuItem("CoreAI/Create Scene Setup", priority = 4)]
        public static void CreateSceneSetup()
        {
            // 1. Проверка: не дублировать CoreAILifetimeScope
            CoreAILifetimeScope existingScope = Object.FindFirstObjectByType<CoreAILifetimeScope>();
            if (existingScope != null)
            {
                if (!EditorUtility.DisplayDialog(
                        "CoreAI — Scene Setup",
                        "CoreAILifetimeScope уже существует на сцене.\nОткрыть его в Inspector?",
                        "Открыть", "Отмена"))
                {
                    return;
                }

                Selection.activeGameObject = existingScope.gameObject;
                EditorGUIUtility.PingObject(existingScope);
                return;
            }

            // 2. Гарантируем наличие ассетов
            CreateDefaultAssets();

            // 3. Создаём GameObject с CoreAILifetimeScope
            GameObject scopeGo = new("CoreAILifetimeScope");
            Undo.RegisterCreatedObjectUndo(scopeGo, "Create CoreAI Scene Setup");
            CoreAILifetimeScope scope = scopeGo.AddComponent<CoreAILifetimeScope>();

            // 4. Назначаем ассеты
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
            SetPropertyIfExists(so, "worldPrefabRegistry", prefabs);
            SetPropertyIfExists(so, "llmRoutingManifest", routing);
            so.ApplyModifiedPropertiesWithoutUndo();

            // 5. Если бэкенд = LlmUnity (или Auto с LlmUnityFirst) — создать LLM + LLMAgent
            bool needsLlmUnity = false;
            if (coreAiSettings != null)
            {
                LlmBackendType backend = coreAiSettings.BackendType;
                needsLlmUnity = backend == LlmBackendType.LlmUnity
                                || (backend == LlmBackendType.Auto
                                    && coreAiSettings.AutoPriority == LlmAutoPriority.LlmUnityFirst);
            }

            if (needsLlmUnity)
            {
                TryCreateLlmUnityObjects(scopeGo);
            }

            // 6. Финализация
            EditorUtility.SetDirty(scope);
            EditorSceneManager.MarkSceneDirty(scopeGo.scene);
            Selection.activeGameObject = scopeGo;
            EditorGUIUtility.PingObject(scopeGo);

            CoreAIEditorLog.Log(
                "Scene Setup: CoreAILifetimeScope создан" +
                (needsLlmUnity ? " + LLM + LLMAgent." : "."));
        }

        private static void SetPropertyIfExists(SerializedObject so, string propertyName, Object value)
        {
            SerializedProperty prop = so.FindProperty(propertyName);
            if (prop != null)
            {
                prop.objectReferenceValue = value;
            }
        }

        /// <summary>Добавить LLM и LLMAgent на сцену (без compile-time зависимости через #if).</summary>
        private static void TryCreateLlmUnityObjects(GameObject parentScope)
        {
#if !COREAI_NO_LLM
            try
            {
                // Проверяем, нет ли уже LLM на сцене
                if (TryFindMonoBehaviourByTypeName("LLM") != null)
                {
                    CoreAIEditorLog.Log("Scene Setup: LLM уже есть на сцене, пропускаем создание.");
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

                // Попробовать назначить модель из настроек
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
                CoreAIEditorLog.Log($"Scene Setup: LLM + LLMAgent созданы (модель: {llm.model ?? "не назначена"}).");
            }
            catch (System.Exception ex)
            {
                CoreAIEditorLog.LogWarning($"Scene Setup: не удалось создать LLM объекты: {ex.Message}");
            }
#else
            CoreAIEditorLog.LogWarning(
                "Scene Setup: LLMUnity не установлен (COREAI_NO_LLM). LLM и LLMAgent не созданы.");
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
                    CoreAIEditorLog.Log($"{labelForLog} уже первая в Build Settings.");
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
            CoreAIEditorLog.Log($"Build Settings: первая сцена — {labelForLog}.");
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

        private static T EnsureAsset<T>(string assetPath) where T : ScriptableObject
        {
            T existing = AssetDatabase.LoadAssetAtPath<T>(assetPath);
            if (existing != null)
            {
                return existing;
            }

            T created = ScriptableObject.CreateInstance<T>();
            AssetDatabase.CreateAsset(created, assetPath);
            return created;
        }

        /// <summary>Optional LLMUnity integration: no compile-time reference to the package.</summary>
        private static MonoBehaviour TryFindMonoBehaviourByTypeName(string typeName)
        {
            foreach (MonoBehaviour mb in Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include,
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
            CoreAILifetimeScope scope = Object.FindFirstObjectByType<CoreAILifetimeScope>();
            if (scope == null)
            {
                return;
            }

            SerializedObject so = new(scope);
            so.FindProperty("gameLogSettings").objectReferenceValue = logSettings;

            // Note: openAiHttpLlmSettings might still be the property name if not updated, but we don't strictly need to assign it since CoreAI prefers the singleton now.
            SerializedProperty legacyOpenAiProp = so.FindProperty("openAiHttpLlmSettings");
            if (legacyOpenAiProp != null)
            {
                legacyOpenAiProp.objectReferenceValue = null;
            }

            so.FindProperty("agentPromptsManifest").objectReferenceValue = prompts;
            so.FindProperty("worldPrefabRegistry").objectReferenceValue = prefabs;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(scope);
            if (scope.gameObject.scene.IsValid())
            {
                EditorSceneManager.MarkSceneDirty(scope.gameObject.scene);
            }
        }
    }
}