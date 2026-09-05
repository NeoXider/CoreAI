#if UNITY_EDITOR
using System.IO;
using CoreAI.Composition;
using CoreAI.Mods.Rbx.Binding;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using VContainer.Unity;

namespace CoreAI.Demos.GameplayServices.Editor
{
    /// <summary>
    /// Builds the MVP8 gameplay-services demo scene: production composition, a lit stage, and a
    /// uGUI panel that narrates what the Lua tour is doing.
    /// </summary>
    /// <remarks>
    /// WHY the scene is generated rather than committed by hand: a scene edited in the editor
    /// records whatever happened to be selected, dirty or missing that day, and the diff is
    /// unreadable. Generated, the scene is a function of this file — reviewable, and rebuildable
    /// after any refactor that renames a component.
    /// </remarks>
    public static class GameplayServicesDemoSceneBuilder
    {
        private const string ScenePath =
            "Assets/CoreAI.Demos/GameplayServices/GameplayServicesDemo.unity";
        private const string ModPath =
            "Assets/CoreAI.Demos/GameplayServices/Mods/GameplayServicesTour.lua.txt";
        private const string SettingsPath = "Assets/Resources/CoreAISettings.asset";
        private const string LogSettingsPath = "Assets/CoreAiUnity/Settings/GameLogSettings.asset";
        private const string PromptsPath = "Assets/CoreAiUnity/Settings/AgentPromptsManifest.asset";
        private const string RoutingPath = "Assets/CoreAiUnity/Settings/LlmRoutingManifest.asset";
        private const string PrefabRegistryPath =
            "Assets/CoreAiUnity/Settings/CoreAiPrefabRegistry.asset";

        /// <summary>Creates or replaces the ready-to-play demo scene in one editor action.</summary>
        [MenuItem("CoreAI/Demos/Build Gameplay Services Demo", priority = 30)]
        public static void BuildScene()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return;
            }

            Object settings = AssetDatabase.LoadMainAssetAtPath(SettingsPath);
            if (settings == null)
            {
                Debug.LogError("[GameplayServicesDemo] Missing " + SettingsPath + ".");
                return;
            }

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            CreateStage();
            RbxWorldHost worldHost = CreateWorldHost();
            CoreAILifetimeScope coreScope = CreateComposition(worldHost);
            GameplayServicesDemoController controller = CreateUi(worldHost, coreScope);
            controller.name = "CoreAI_GameplayServicesDemo";

            EditorSceneManager.MarkSceneDirty(scene);
            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath) ?? ".");
            if (!EditorSceneManager.SaveScene(scene, ScenePath))
            {
                Debug.LogError("[GameplayServicesDemo] Failed to save " + ScenePath + ".");
                return;
            }

            AssetDatabase.Refresh();
            Debug.Log("[GameplayServicesDemo] Built " + ScenePath + ".");
        }

        private static void CreateStage()
        {
            GameObject light = new("Directional Light");
            Light sun = light.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.15f;
            sun.shadows = LightShadows.Soft;
            light.transform.rotation = Quaternion.Euler(48f, 32f, 0f);

            GameObject camera = new("Main Camera");
            camera.tag = "MainCamera";
            Camera view = camera.AddComponent<Camera>();
            view.clearFlags = CameraClearFlags.SolidColor;
            view.backgroundColor = new Color(0.06f, 0.07f, 0.10f);
            camera.transform.position = new Vector3(0f, 3.2f, -7.5f);
            camera.transform.rotation = Quaternion.Euler(14f, 0f, 0f);

            // A floor for the dropped block to land on, in metres: the Rbx layer works in studs and
            // converts, so the stage is authored in Unity's own units like any host scene.
            GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = "Stage Floor";
            floor.transform.position = new Vector3(0f, -0.05f, 0f);
            floor.transform.localScale = new Vector3(4f, 1f, 4f);
        }

        private static RbxWorldHost CreateWorldHost()
        {
            GameObject host = new("CoreAI_RbxWorld");
            RbxWorldHost worldHost = host.AddComponent<RbxWorldHost>();
            return worldHost;
        }

        private static CoreAILifetimeScope CreateComposition(RbxWorldHost worldHost)
        {
            // WHY the scope object starts inactive: VContainer builds the container in Awake, and a
            // scope that wakes before its serialized references are assigned resolves a half-wired
            // graph — which surfaces later as a null service rather than as a build error.
            GameObject coreObject = new("CoreAI Production Services");
            coreObject.SetActive(false);
            CoreAILifetimeScope coreScope = coreObject.AddComponent<CoreAILifetimeScope>();

            SerializedObject coreSerialized = new(coreScope);
            SetAssetReference(coreSerialized, "coreAiSettings", SettingsPath);
            SetAssetReference(coreSerialized, "gameLogSettings", LogSettingsPath);
            SetAssetReference(coreSerialized, "agentPromptsManifest", PromptsPath);
            SetAssetReference(coreSerialized, "llmRoutingManifest", RoutingPath);

            GameObject luaModuleObject = new("Lua and World Commands");
            luaModuleObject.transform.SetParent(coreObject.transform, false);
            CoreAiLuaWorldModule luaModule = luaModuleObject.AddComponent<CoreAiLuaWorldModule>();
            SerializedObject moduleSerialized = new(luaModule);
            SetAssetReference(moduleSerialized, "worldPrefabRegistry", PrefabRegistryPath);
            moduleSerialized.ApplyModifiedPropertiesWithoutUndo();
            Assign(coreSerialized, "luaWorldModule", luaModule);
            coreSerialized.ApplyModifiedPropertiesWithoutUndo();

            GameObject modsObject = new("Actor-Scoped Mods Production Scope");
            modsObject.transform.SetParent(coreObject.transform, false);
            CoreAiModsLifetimeScope modsScope = modsObject.AddComponent<CoreAiModsLifetimeScope>();
            modsScope.parentReference = ParentReference.Create<CoreAILifetimeScope>();
            SerializedObject modsSerialized = new(modsScope);
            Assign(modsSerialized, "robloxWorldHost", worldHost);
            SerializedProperty storeId = modsSerialized.FindProperty("storeId");
            if (storeId != null)
            {
                storeId.stringValue = "gameplay-services-demo";
            }

            modsSerialized.ApplyModifiedPropertiesWithoutUndo();

            coreObject.SetActive(true);
            EditorUtility.SetDirty(coreScope);
            EditorUtility.SetDirty(luaModule);
            EditorUtility.SetDirty(modsScope);
            return coreScope;
        }

        private static void SetAssetReference(SerializedObject serialized, string field,
            string assetPath)
        {
            Assign(serialized, field, AssetDatabase.LoadMainAssetAtPath(assetPath));
        }

        private static GameplayServicesDemoController CreateUi(RbxWorldHost worldHost,
            CoreAILifetimeScope coreScope)
        {
            GameObject canvasObject = new("CoreAI_DemoCanvas");
            Canvas canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            CanvasScaler scaler = canvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            canvasObject.AddComponent<GraphicRaycaster>();

            if (Object.FindFirstObjectByType<UnityEngine.EventSystems.EventSystem>() == null)
            {
                GameObject events = new("EventSystem");
                events.AddComponent<UnityEngine.EventSystems.EventSystem>();
                events.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
            }

            RectTransform panel = CreatePanel(canvasObject.transform);
            TextMeshProUGUI title = CreateLabel(panel, "MVP8 — GAMEPLAY SERVICES",
                new Vector2(0f, -28f), 34f, new Color(0.62f, 0.82f, 1f));
            title.fontStyle = FontStyles.Bold;
            TextMeshProUGUI hint = CreateLabel(panel, "", new Vector2(0f, -78f), 20f,
                new Color(0.75f, 0.79f, 0.85f));
            TextMeshProUGUI status = CreateLabel(panel, "", new Vector2(0f, -150f), 22f,
                new Color(0.92f, 0.94f, 0.97f));
            status.alignment = TextAlignmentOptions.TopLeft;

            Button run = CreateButton(panel, "Run tour", new Vector2(-190f, -420f));
            Button drop = CreateButton(panel, "Drop a block", new Vector2(0f, -420f));
            Button gravity = CreateButton(panel, "Toggle gravity", new Vector2(190f, -420f));

            GameObject controllerObject = new("CoreAI_DemoController");
            GameplayServicesDemoController controller =
                controllerObject.AddComponent<GameplayServicesDemoController>();

            SerializedObject serialized = new(controller);
            Assign(serialized, "_coreAiScope", coreScope);
            Assign(serialized, "_worldHost", worldHost);
            Assign(serialized, "_tourMod", AssetDatabase.LoadAssetAtPath<TextAsset>(ModPath));
            Assign(serialized, "_statusLabel", status);
            Assign(serialized, "_hintLabel", hint);
            Assign(serialized, "_runButton", run);
            Assign(serialized, "_dropButton", drop);
            Assign(serialized, "_lowGravityButton", gravity);
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return controller;
        }

        private static void Assign(SerializedObject serialized, string field, Object value)
        {
            SerializedProperty property = serialized.FindProperty(field);
            if (property != null)
            {
                property.objectReferenceValue = value;
            }
        }

        private static RectTransform CreatePanel(Transform parent)
        {
            GameObject panelObject = new("Panel");
            panelObject.transform.SetParent(parent, false);
            Image background = panelObject.AddComponent<Image>();
            background.color = new Color(0.05f, 0.06f, 0.09f, 0.86f);
            RectTransform rect = panelObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(40f, -40f);
            rect.sizeDelta = new Vector2(620f, 500f);
            return rect;
        }

        private static TextMeshProUGUI CreateLabel(Transform parent, string text,
            Vector2 position, float size, Color color)
        {
            GameObject labelObject = new("Label");
            labelObject.transform.SetParent(parent, false);
            TextMeshProUGUI label = labelObject.AddComponent<TextMeshProUGUI>();
            label.text = text;
            label.fontSize = size;
            label.color = color;
            label.alignment = TextAlignmentOptions.TopLeft;
            RectTransform rect = label.rectTransform;
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(28f, position.y);
            rect.sizeDelta = new Vector2(560f, size * 9f);
            return label;
        }

        private static Button CreateButton(Transform parent, string caption, Vector2 position)
        {
            GameObject buttonObject = new("Button " + caption);
            buttonObject.transform.SetParent(parent, false);
            Image background = buttonObject.AddComponent<Image>();
            background.color = new Color(0.16f, 0.34f, 0.58f, 1f);
            Button button = buttonObject.AddComponent<Button>();
            RectTransform rect = buttonObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = position;
            rect.sizeDelta = new Vector2(180f, 52f);

            GameObject captionObject = new("Caption");
            captionObject.transform.SetParent(buttonObject.transform, false);
            TextMeshProUGUI label = captionObject.AddComponent<TextMeshProUGUI>();
            label.text = caption;
            label.fontSize = 20f;
            label.color = Color.white;
            label.alignment = TextAlignmentOptions.Center;
            RectTransform captionRect = label.rectTransform;
            captionRect.anchorMin = Vector2.zero;
            captionRect.anchorMax = Vector2.one;
            captionRect.offsetMin = Vector2.zero;
            captionRect.offsetMax = Vector2.zero;
            return button;
        }
    }
}
#endif
