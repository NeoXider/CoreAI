#if UNITY_EDITOR
using System.IO;
using CoreAI.Ai.Hub;
using CoreAI.Composition;
using CoreAI.Hub.UI;
using CoreAI.Mods.Rbx.Binding;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VContainer.Unity;

namespace CoreAI.Demos.Editor
{
    /// <summary>Builds the complete production-composed MVP2 multiplayer-foundation demo scene.</summary>
    public static class MultiplayerFoundationDemoSceneBuilder
    {
        private const string ScenePath =
            "Assets/CoreAI.Demos/MultiplayerFoundation/MultiplayerFoundationDemo.unity";
        private const string HubPrefabPath = "Assets/CoreAIHub/Runtime/CoreAiHub.prefab";
        private const string SettingsPath = "Assets/Resources/CoreAISettings.asset";
        private const string LogSettingsPath = "Assets/CoreAiUnity/Settings/GameLogSettings.asset";
        private const string PromptsPath = "Assets/CoreAiUnity/Settings/AgentPromptsManifest.asset";
        private const string RoutingPath = "Assets/CoreAiUnity/Settings/LlmRoutingManifest.asset";
        private const string PrefabRegistryPath = "Assets/CoreAiUnity/Settings/CoreAiPrefabRegistry.asset";

        /// <summary>Creates or replaces the ready-to-play demo scene in one editor action.</summary>
        [MenuItem("CoreAI/Demos/Build Multiplayer Foundation Demo", priority = 40)]
        public static void BuildScene()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return;
            }

            if (File.Exists(ScenePath) &&
                !EditorUtility.DisplayDialog(
                    "CoreAI - Multiplayer Foundation",
                    "Rebuild the generated multiplayer-foundation demo scene?",
                    "Rebuild",
                    "Open Existing"))
            {
                EditorSceneManager.OpenScene(ScenePath);
                return;
            }

            GameObject hubPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(HubPrefabPath);
            Object settings = AssetDatabase.LoadMainAssetAtPath(SettingsPath);
            if (hubPrefab == null || settings == null)
            {
                Debug.LogError(
                    "[MultiplayerFoundationBuilder] Required assets are missing. Expected " +
                    HubPrefabPath + " and " + SettingsPath + ".");
                return;
            }

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            CreateEnvironment();
            RbxWorldHost worldHost = CreateSharedWorldStage();
            CreateProductionComposition(worldHost, out CoreAILifetimeScope coreScope,
                out CoreAiModsLifetimeScope modsScope);
            CreateHub(hubPrefab, coreScope, modsScope);

            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene, ScenePath))
            {
                Debug.LogError("[MultiplayerFoundationBuilder] Failed to save " + ScenePath + ".");
                return;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            SceneAsset sceneAsset = AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath);
            Selection.activeObject = sceneAsset;
            EditorGUIUtility.PingObject(sceneAsset);
            Debug.Log(
                "[MultiplayerFoundationBuilder] Ready. Press Play: the Multiplayer Proof tab runs " +
                "the four-actor production-path proof automatically.");
        }

        private static void CreateEnvironment()
        {
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.28f, 0.35f, 0.48f, 1f);

            GameObject cameraObject = new GameObject("Main Camera");
            cameraObject.tag = "MainCamera";
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.018f, 0.035f, 0.07f, 1f);
            camera.fieldOfView = 48f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 100f;
            cameraObject.AddComponent<AudioListener>();
            cameraObject.transform.position = new Vector3(0f, 4.4f, -7.5f);
            cameraObject.transform.LookAt(new Vector3(0f, 0.55f, -0.8f));

            GameObject keyLightObject = new GameObject("Key Light");
            Light keyLight = keyLightObject.AddComponent<Light>();
            keyLight.type = LightType.Directional;
            keyLight.color = new Color(0.78f, 0.9f, 1f, 1f);
            keyLight.intensity = 1.25f;
            keyLightObject.transform.rotation = Quaternion.Euler(48f, -32f, 0f);

            GameObject accentLightObject = new GameObject("Cyan Accent Light");
            Light accentLight = accentLightObject.AddComponent<Light>();
            accentLight.type = LightType.Point;
            accentLight.color = new Color(0.15f, 0.82f, 1f, 1f);
            accentLight.intensity = 5f;
            accentLight.range = 12f;
            accentLightObject.transform.position = new Vector3(0f, 4f, -1f);
        }

        private static RbxWorldHost CreateSharedWorldStage()
        {
            GameObject stage = new GameObject("Shared Production Rbx World");
            RbxWorldHost host = stage.AddComponent<RbxWorldHost>();

            GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = "Shared World Floor";
            floor.transform.SetParent(stage.transform, false);
            floor.transform.localScale = new Vector3(1.2f, 1f, 1.2f);
            return host;
        }

        private static void CreateProductionComposition(
            RbxWorldHost worldHost,
            out CoreAILifetimeScope coreScope,
            out CoreAiModsLifetimeScope modsScope)
        {
            GameObject coreObject = new GameObject("CoreAI Production Services");
            coreObject.SetActive(false);
            coreScope = coreObject.AddComponent<CoreAILifetimeScope>();

            SerializedObject coreSerialized = new SerializedObject(coreScope);
            SetObjectReference(coreSerialized, "coreAiSettings", SettingsPath);
            SetObjectReference(coreSerialized, "gameLogSettings", LogSettingsPath);
            SetObjectReference(coreSerialized, "agentPromptsManifest", PromptsPath);
            SetObjectReference(coreSerialized, "llmRoutingManifest", RoutingPath);

            GameObject luaModuleObject = new GameObject("Lua and World Commands");
            luaModuleObject.transform.SetParent(coreObject.transform, false);
            CoreAiLuaWorldModule luaModule = luaModuleObject.AddComponent<CoreAiLuaWorldModule>();
            SerializedObject moduleSerialized = new SerializedObject(luaModule);
            SetObjectReference(moduleSerialized, "worldPrefabRegistry", PrefabRegistryPath);
            moduleSerialized.ApplyModifiedPropertiesWithoutUndo();
            SetObjectReference(coreSerialized, "luaWorldModule", luaModule);
            coreSerialized.ApplyModifiedPropertiesWithoutUndo();

            GameObject modsObject = new GameObject("Actor-Scoped Mods Production Scope");
            modsObject.transform.SetParent(coreObject.transform, false);
            modsScope = modsObject.AddComponent<CoreAiModsLifetimeScope>();
            modsScope.parentReference = ParentReference.Create<CoreAILifetimeScope>();
            SerializedObject modsSerialized = new SerializedObject(modsScope);
            SetObjectReference(modsSerialized, "robloxWorldHost", worldHost);
            SetString(modsSerialized, "storeId", "multiplayer-foundation-demo");
            SetBool(modsSerialized, "enableFullLuaAccess", false);
            SetBool(modsSerialized, "enableFullLuaPrivateAccess", false);
            modsSerialized.ApplyModifiedPropertiesWithoutUndo();

            coreObject.SetActive(true);
            EditorUtility.SetDirty(coreScope);
            EditorUtility.SetDirty(luaModule);
            EditorUtility.SetDirty(modsScope);
        }

        private static void CreateHub(
            GameObject hubPrefab,
            CoreAILifetimeScope coreScope,
            CoreAiModsLifetimeScope modsScope)
        {
            GameObject hub = PrefabUtility.InstantiatePrefab(hubPrefab) as GameObject;
            if (hub == null)
            {
                throw new UnityException("The CoreAI Hub prefab could not be instantiated.");
            }

            hub.name = "MVP2 Multiplayer Proof Board";
            if (hub.GetComponent<CoreAiModsHubBinder>() == null)
            {
                hub.AddComponent<CoreAiModsHubBinder>();
            }

            MultiplayerFoundationDemoController controller =
                hub.GetComponent<MultiplayerFoundationDemoController>();
            if (controller == null)
            {
                controller = hub.AddComponent<MultiplayerFoundationDemoController>();
            }

            SerializedObject controllerSerialized = new SerializedObject(controller);
            SerializedProperty actorCount = controllerSerialized.FindProperty("actorCount");
            if (actorCount != null)
            {
                actorCount.intValue = 4;
            }

            SetObjectReference(controllerSerialized, "coreAiScope", coreScope);
            SetObjectReference(controllerSerialized, "modsScope", modsScope);
            controllerSerialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(controller);
        }

        private static void SetObjectReference(
            SerializedObject serializedObject,
            string propertyName,
            string assetPath)
        {
            Object asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            SetObjectReference(serializedObject, propertyName, asset);
        }

        private static void SetObjectReference(
            SerializedObject serializedObject,
            string propertyName,
            Object value)
        {
            SerializedProperty property = serializedObject.FindProperty(propertyName);
            if (property != null)
            {
                property.objectReferenceValue = value;
            }
        }

        private static void SetString(
            SerializedObject serializedObject,
            string propertyName,
            string value)
        {
            SerializedProperty property = serializedObject.FindProperty(propertyName);
            if (property != null)
            {
                property.stringValue = value;
            }
        }

        private static void SetBool(
            SerializedObject serializedObject,
            string propertyName,
            bool value)
        {
            SerializedProperty property = serializedObject.FindProperty(propertyName);
            if (property != null)
            {
                property.boolValue = value;
            }
        }
    }
}
#endif
