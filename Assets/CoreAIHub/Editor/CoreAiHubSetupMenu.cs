using System;
using CoreAI.Hub.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.Hub.UI.Editor
{
    /// <summary>
    /// <c>CoreAI → Setup → Add Hub</c>: drops the CoreAI Hub window into the open scene. On first use it
    /// also authors the reusable module prefab (<c>Assets/CoreAIHub/Runtime/CoreAiHub.prefab</c>) so the Hub
    /// package ships a ready prefab; later invocations just instance it. If the Lua mods module
    /// (<c>com.neoxider.coreaimods</c>) is installed, the Mods tab is lit up by adding its
    /// <c>CoreAiModsHubBinder</c> to the instance — features light up when packages appear.
    /// </summary>
    public static class CoreAiHubSetupMenu
    {
        private const string PrefabPath = "Assets/CoreAIHub/Runtime/CoreAiHub.prefab";
        private const string PanelSettingsPath = "Assets/CoreAIHub/Runtime/CoreAiHubPanelSettings.asset";
        private const string ChatUxmlPath = "Assets/CoreAiUnity/Runtime/Source/Features/Chat/UI/CoreAiChat.uxml";
        private const string ChatUssPath = "Assets/CoreAiUnity/Runtime/Source/Features/Chat/UI/CoreAiChat.uss";
        private const string ModsBinderType = "CoreAI.Ai.Hub.CoreAiModsHubBinder, CoreAI.Mods";

        [MenuItem("CoreAI/Setup/Add Hub", priority = 12)]
        public static void AddHub()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefab == null)
            {
                GameObject authored = BuildHub();
                prefab = PrefabUtility.SaveAsPrefabAsset(authored, PrefabPath);
                UnityEngine.Object.DestroyImmediate(authored);
                Debug.Log($"[CoreAI] Authored the Hub module prefab at {PrefabPath}.");
            }

            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);

            // Light up the Mods tab when the Lua mods module is present (its component lives in CoreAI.Mods,
            // which the Hub package does not reference, so resolve it by name).
            Type modsBinder = Type.GetType(ModsBinderType);
            if (modsBinder != null && instance.GetComponent(modsBinder) == null)
            {
                instance.AddComponent(modsBinder);
            }

            Undo.RegisterCreatedObjectUndo(instance, "Add CoreAI Hub");
            Selection.activeGameObject = instance;
            EditorSceneManager.MarkSceneDirty(instance.scene);
            Debug.Log("[CoreAI] Added CoreAI Hub to the scene. Make sure the scene has a CoreAILifetimeScope " +
                      "(and, for the Mods tab, a CoreAiModsLifetimeScope child).");
        }

        private static GameObject BuildHub()
        {
            GameObject go = new("CoreAiHub");

            UIDocument document = go.AddComponent<UIDocument>();
            // Dedicated Hub PanelSettings so the Hub renders on its own panel and never contends with the
            // embedded chat's UIDocument (sharing one PanelSettings orphaned the Hub root — the flicker bug).
            PanelSettings panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelSettingsPath);
            if (panel == null)
            {
                string[] found = AssetDatabase.FindAssets("t:PanelSettings");
                if (found.Length > 0)
                {
                    panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(AssetDatabase.GUIDToAssetPath(found[0]));
                }
            }

            document.panelSettings = panel;

            CoreAiHubWindow window = go.AddComponent<CoreAiHubWindow>();
            // Assign the shell UXML (the window builds its structure from it, not from C#).
            VisualTreeAsset shell =
                AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/CoreAIHub/Runtime/CoreAiHub.uxml");
            SerializedObject wso = new(window);
            SerializedProperty shellProp = wso.FindProperty("shellUxml");
            if (shellProp != null && shell != null)
            {
                shellProp.objectReferenceValue = shell;
                wso.ApplyModifiedPropertiesWithoutUndo();
            }

            // The built-in About/Chat/Settings/Statistics tabs come from this bootstrap; wire the chat UXML so
            // the Chat tab embeds the real CoreAiChatPanel instead of showing a setup note.
            CoreAiHubDemo bootstrap = go.AddComponent<CoreAiHubDemo>();
            SerializedObject so = new(bootstrap);
            VisualTreeAsset chatUxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(ChatUxmlPath);
            StyleSheet chatUss = AssetDatabase.LoadAssetAtPath<StyleSheet>(ChatUssPath);
            if (chatUxml != null)
            {
                so.FindProperty("chatTemplate").objectReferenceValue = chatUxml;
            }

            if (chatUss != null)
            {
                so.FindProperty("chatStyleSheet").objectReferenceValue = chatUss;
            }

            so.ApplyModifiedPropertiesWithoutUndo();
            return go;
        }
    }
}
