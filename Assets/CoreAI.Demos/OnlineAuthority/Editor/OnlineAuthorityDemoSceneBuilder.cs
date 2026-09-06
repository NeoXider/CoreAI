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
using Object = UnityEngine.Object;

namespace CoreAI.Demos.OnlineAuthority.Editor
{
    /// <summary>
    /// Builds the MVP11/12 authority demo: a statue, a guest with no rights, and a host who can
    /// change that and change it back.
    /// </summary>
    /// <remarks>
    /// WHY the scene still carries the standard composition even though the demo asks nothing of a
    /// language model: every other published demo does, the shared settings default to the Offline
    /// backend so nothing here needs a model installed, and a scene without the container is a
    /// scene where backend-state discovery and every container-provided service behave differently
    /// from the other sixteen. It was built without one at first, and the demo-scene smoke caught
    /// exactly that.
    /// </remarks>
    public static class OnlineAuthorityDemoSceneBuilder
    {
        private const string ScenePath =
            "Assets/CoreAI.Demos/OnlineAuthority/OnlineAuthorityDemo.unity";

        private const string SettingsPath = "Assets/Resources/CoreAISettings.asset";
        private const string LogSettingsPath = "Assets/CoreAiUnity/Settings/GameLogSettings.asset";
        private const string PromptsPath = "Assets/CoreAiUnity/Settings/AgentPromptsManifest.asset";
        private const string RoutingPath = "Assets/CoreAiUnity/Settings/LlmRoutingManifest.asset";
        private const string PrefabRegistryPath =
            "Assets/CoreAiUnity/Settings/CoreAiPrefabRegistry.asset";

        /// <summary>Creates or replaces the ready-to-play demo scene in one editor action.</summary>
        [MenuItem("CoreAI/Demos/Build Online Authority Demo", priority = 31)]
        public static void BuildScene()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return;
            }

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            CreateStage();
            RbxWorldHost worldHost = new GameObject("CoreAI_RbxWorld")
                .AddComponent<RbxWorldHost>();
            CreateComposition(worldHost);
            CreateUi(worldHost);

            EditorSceneManager.MarkSceneDirty(scene);
            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath) ?? ".");
            if (!EditorSceneManager.SaveScene(scene, ScenePath))
            {
                Debug.LogError("[OnlineAuthorityDemo] Failed to save " + ScenePath + ".");
                return;
            }

            AssetDatabase.Refresh();
            Debug.Log("[OnlineAuthorityDemo] Built " + ScenePath + ".");
        }

        /// <summary>
        /// Builds the standard CoreAI container: the core scope, the Lua/world module and the
        /// actor-scoped mods scope bound to this scene's world host.
        /// </summary>
        private static void CreateComposition(RbxWorldHost worldHost)
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
                storeId.stringValue = "online-authority-demo";
            }

            modsSerialized.ApplyModifiedPropertiesWithoutUndo();

            coreObject.SetActive(true);
            EditorUtility.SetDirty(coreScope);
            EditorUtility.SetDirty(luaModule);
            EditorUtility.SetDirty(modsScope);
        }

        private static void SetAssetReference(SerializedObject serialized, string field,
            string assetPath)
        {
            Assign(serialized, field, AssetDatabase.LoadMainAssetAtPath(assetPath));
        }

        private static void CreateStage()
        {
            GameObject light = new("Directional Light");
            Light sun = light.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.1f;
            light.transform.rotation = Quaternion.Euler(50f, 24f, 0f);

            GameObject camera = new("Main Camera");
            camera.tag = "MainCamera";
            Camera view = camera.AddComponent<Camera>();
            view.clearFlags = CameraClearFlags.SolidColor;
            view.backgroundColor = new Color(0.07f, 0.06f, 0.10f);
            camera.transform.position = new Vector3(0f, 3f, -8f);
            camera.transform.rotation = Quaternion.Euler(12f, 0f, 0f);

            GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = "Stage Floor";
            floor.transform.localScale = new Vector3(4f, 1f, 4f);
        }

        private static void CreateUi(RbxWorldHost worldHost)
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
            TextMeshProUGUI title = CreateLabel(panel, "MVP11 / MVP12 — WHO MAY CHANGE THE WORLD",
                -28f, 30f, new Color(0.66f, 0.84f, 1f));
            title.fontStyle = FontStyles.Bold;
            CreateLabel(panel,
                "The guest asks. The host decides, and can change its mind.\n"
                + "Every refusal below is the shipped rule, printed with its reason.",
                -72f, 19f, new Color(0.74f, 0.78f, 0.85f));
            TextMeshProUGUI grants = CreateLabel(panel, "Guest grants: none", -142f, 20f,
                new Color(0.98f, 0.82f, 0.45f));
            TextMeshProUGUI status = CreateLabel(panel, "", -186f, 20f,
                new Color(0.92f, 0.94f, 0.97f));

            Button guestMove = CreateButton(panel, "Guest: move statue", new Vector2(-150f, -470f));
            Button grant = CreateButton(panel, "Host: grant", new Vector2(30f, -470f));
            Button revoke = CreateButton(panel, "Host: revoke", new Vector2(210f, -470f));
            Button hostMove = CreateButton(panel, "Host: move statue", new Vector2(-150f, -530f));

            OnlineAuthorityDemoController controller = new GameObject("CoreAI_DemoController")
                .AddComponent<OnlineAuthorityDemoController>();
            SerializedObject serialized = new(controller);
            Assign(serialized, "_worldHost", worldHost);
            Assign(serialized, "_statusLabel", status);
            Assign(serialized, "_grantLabel", grants);
            Assign(serialized, "_guestMoveButton", guestMove);
            Assign(serialized, "_grantButton", grant);
            Assign(serialized, "_revokeButton", revoke);
            Assign(serialized, "_hostMoveButton", hostMove);
            serialized.ApplyModifiedPropertiesWithoutUndo();
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
            background.color = new Color(0.05f, 0.05f, 0.09f, 0.88f);
            RectTransform rect = panelObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(40f, -40f);
            rect.sizeDelta = new Vector2(680f, 600f);
            return rect;
        }

        private static TextMeshProUGUI CreateLabel(Transform parent, string text, float y,
            float size, Color color)
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
            rect.anchoredPosition = new Vector2(26f, y);
            rect.sizeDelta = new Vector2(624f, size * 10f);
            return label;
        }

        private static Button CreateButton(Transform parent, string caption, Vector2 position)
        {
            GameObject buttonObject = new("Button " + caption);
            buttonObject.transform.SetParent(parent, false);
            Image background = buttonObject.AddComponent<Image>();
            background.color = caption.StartsWith("Host", System.StringComparison.Ordinal)
                ? new Color(0.18f, 0.42f, 0.30f)
                : new Color(0.36f, 0.24f, 0.52f);
            Button button = buttonObject.AddComponent<Button>();
            RectTransform rect = buttonObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(position.x + 180f, position.y);
            rect.sizeDelta = new Vector2(170f, 50f);

            GameObject captionObject = new("Caption");
            captionObject.transform.SetParent(buttonObject.transform, false);
            TextMeshProUGUI label = captionObject.AddComponent<TextMeshProUGUI>();
            label.text = caption;
            label.fontSize = 18f;
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
