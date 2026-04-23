using System.IO;
using CoreAI.Chat;
using CoreAI.Composition;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Prompts;
using CoreAI.Infrastructure.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace CoreAI.Editor
{
    /// <summary>
    /// Создаёт готовую демо-сцену <c>CoreAiChatDemo</c> с подключённым
    /// <see cref="CoreAILifetimeScope"/>, UIDocument c UXML/USS чата и
    /// компонентом <see cref="CoreAiChatPanel"/>. Позволяет быстро проверить
    /// работу стриминга и чата без ручной сборки сцены.
    /// <para>Меню: <c>CoreAI → Setup → Create Chat Demo Scene</c>.</para>
    /// </summary>
    public static class CoreAIChatDemoSceneCreator
    {
        private const string ScenePath = "Assets/CoreAiUnity/Scenes/CoreAiChatDemo.unity";
        private const string DemoConfigPath = "Assets/CoreAiUnity/Scenes/CoreAiChatConfig_Demo.asset";
        private const string DemoPanelSettingsPath = "Assets/CoreAiUnity/Scenes/CoreAiChatPanelSettings_Demo.asset";

        private const string UxmlPath = "Assets/CoreAiUnity/Runtime/Source/Features/Chat/UI/CoreAiChat.uxml";
        private const string UssPath = "Assets/CoreAiUnity/Runtime/Source/Features/Chat/UI/CoreAiChat.uss";

        private const string SettingsRoot = "Assets/CoreAiUnity/Settings";
        private const string LogSettingsPath = SettingsRoot + "/GameLogSettings.asset";
        private const string CoreAiSettingsPath = "Assets/Resources/CoreAISettings.asset";
        private const string PromptsManifestPath = SettingsRoot + "/AgentPromptsManifest.asset";
        private const string PrefabRegistryPath = SettingsRoot + "/CoreAiPrefabRegistry.asset";
        private const string LlmRoutingPath = SettingsRoot + "/LlmRoutingManifest.asset";

        [MenuItem("CoreAI/Setup/Create Chat Demo Scene", priority = 10)]
        public static void CreateChatDemoScene()
        {
            if (File.Exists(ScenePath))
            {
                if (!EditorUtility.DisplayDialog(
                        "CoreAI — Chat Demo Scene",
                        $"Сцена уже существует: {ScenePath}\nПересоздать?",
                        "Пересоздать", "Отмена"))
                {
                    EditorSceneManager.OpenScene(ScenePath);
                    return;
                }

                AssetDatabase.DeleteAsset(ScenePath);
            }

            CoreAIBuildMenu.CreateDefaultAssets();

            EnsureFolder("Assets/CoreAiUnity/Scenes");
            CoreAiChatConfig config = EnsureDemoChatConfig();
            PanelSettings panelSettings = EnsureDemoPanelSettings();

            UnityEngine.SceneManagement.Scene scene = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene,
                NewSceneMode.Single);

            CreateCamera();
            CreateLight();
            CreateEventSystem();
            CreateCoreAILifetimeScope();
            CreateChatCanvas(config, panelSettings);

            bool saved = EditorSceneManager.SaveScene(scene, ScenePath);
            if (!saved)
            {
                CoreAIEditorLog.LogError("Не удалось сохранить Chat Demo сцену.");
                return;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            CoreAIEditorLog.Log($"Chat Demo сцена создана: {ScenePath}");
            EditorUtility.FocusProjectWindow();
            Selection.activeObject = AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath);
            EditorGUIUtility.PingObject(Selection.activeObject);
        }

        [MenuItem("CoreAI/Setup/Open Chat Demo Scene", priority = 11)]
        public static void OpenChatDemoScene()
        {
            if (!File.Exists(ScenePath))
            {
                CreateChatDemoScene();
                return;
            }

            EditorSceneManager.OpenScene(ScenePath);
        }

        private static void CreateCamera()
        {
            GameObject cameraGo = new("Main Camera");
            cameraGo.tag = "MainCamera";
            Camera cam = cameraGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.12f, 0.13f, 0.16f, 1f);
            cam.orthographic = false;
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = 1000f;
            cameraGo.AddComponent<AudioListener>();
            cameraGo.transform.position = new Vector3(0, 1, -10);
        }

        private static void CreateLight()
        {
            GameObject lightGo = new("Directional Light");
            Light light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1f;
            lightGo.transform.rotation = Quaternion.Euler(50, -30, 0);
        }

        private static void CreateEventSystem()
        {
            GameObject esGo = new("EventSystem");
            esGo.AddComponent<EventSystem>();
            esGo.AddComponent<StandaloneInputModule>();
        }

        private static void CreateCoreAILifetimeScope()
        {
            GameObject scopeGo = new("CoreAILifetimeScope");
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
            SetProp(so, "gameLogSettings", logSettings);
            SetProp(so, "coreAiSettings", coreAiSettings);
            SetProp(so, "agentPromptsManifest", prompts);
            SetProp(so, "worldPrefabRegistry", prefabs);
            SetProp(so, "llmRoutingManifest", routing);
            so.ApplyModifiedPropertiesWithoutUndo();

            EditorUtility.SetDirty(scope);
        }

        private static void CreateChatCanvas(CoreAiChatConfig config, PanelSettings panelSettings)
        {
            GameObject uiGo = new("CoreAiChatUI");
            UIDocument doc = uiGo.AddComponent<UIDocument>();

            VisualTreeAsset uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UxmlPath);
            StyleSheet uss = AssetDatabase.LoadAssetAtPath<StyleSheet>(UssPath);

            if (uxml == null)
            {
                CoreAIEditorLog.LogWarning($"Chat UXML не найден: {UxmlPath}");
            }
            else
            {
                doc.visualTreeAsset = uxml;
            }

            doc.panelSettings = panelSettings;

            if (uss != null)
            {
                SerializedObject docSo = new(doc);
                docSo.ApplyModifiedPropertiesWithoutUndo();
            }

            CoreAiChatPanel panel = uiGo.AddComponent<CoreAiChatPanel>();
            SerializedObject panelSo = new(panel);
            SetProp(panelSo, "config", config);
            SetProp(panelSo, "customStyleSheet", uss);
            panelSo.ApplyModifiedPropertiesWithoutUndo();

            EditorUtility.SetDirty(panel);
        }

        private static CoreAiChatConfig EnsureDemoChatConfig()
        {
            CoreAiChatConfig existing = AssetDatabase.LoadAssetAtPath<CoreAiChatConfig>(DemoConfigPath);
            if (existing != null) return existing;

            CoreAiChatConfig asset = ScriptableObject.CreateInstance<CoreAiChatConfig>();
            AssetDatabase.CreateAsset(asset, DemoConfigPath);
            AssetDatabase.SaveAssets();
            return asset;
        }

        private static PanelSettings EnsureDemoPanelSettings()
        {
            PanelSettings existing = AssetDatabase.LoadAssetAtPath<PanelSettings>(DemoPanelSettingsPath);
            if (existing != null) return existing;

            PanelSettings asset = ScriptableObject.CreateInstance<PanelSettings>();
            asset.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            asset.referenceResolution = new Vector2Int(1920, 1080);
            asset.match = 0.5f;
            asset.sortingOrder = 100;
            AssetDatabase.CreateAsset(asset, DemoPanelSettingsPath);
            AssetDatabase.SaveAssets();
            return asset;
        }

        private static void EnsureFolder(string folderPath)
        {
            if (AssetDatabase.IsValidFolder(folderPath)) return;

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

        private static void SetProp(SerializedObject so, string name, Object value)
        {
            SerializedProperty p = so.FindProperty(name);
            if (p != null) p.objectReferenceValue = value;
        }
    }
}
