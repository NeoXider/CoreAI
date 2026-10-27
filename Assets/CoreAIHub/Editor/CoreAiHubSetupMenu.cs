using System;
using CoreAI.Chat;
using CoreAI.Hub.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.Hub.UI.Editor
{
    /// <summary>
    /// <c>CoreAI → Setup → Add Hub</c>: drops the CoreAI Hub window into the open scene. On first use it
    /// authors a project-owned prefab under <c>Assets/CoreAIGenerated/Hub</c>. Package resources are
    /// resolved from their installed package paths, or their source layout under Assets. If the Lua mods module
    /// (<c>com.neoxider.coreaimods</c>) is installed, the Mods tab is lit up by adding its
    /// <c>CoreAiModsHubBinder</c> to the instance — features light up when packages appear.
    /// </summary>
    public static class CoreAiHubSetupMenu
    {
        private const string PrefabPath = "Assets/CoreAIGenerated/Hub/CoreAiHub.prefab";
        private const string ModsBinderType = "CoreAI.Ai.Hub.CoreAiModsHubBinder, CoreAI.Mods.Hub";
        private const string WorldStateBinderType = "CoreAI.Ai.Hub.WorldStateHubBinder, CoreAI.Mods.Hub";

        [MenuItem("CoreAI/Setup/Add Hub", priority = 12)]
        public static void AddHub()
        {
            GameObject authored = null;
            try
            {
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
                if (prefab == null)
                {
                    EnsureGeneratedFolder();
                    authored = BuildHub();
                    prefab = PrefabUtility.SaveAsPrefabAsset(authored, PrefabPath);
                    if (prefab == null)
                    {
                        throw new InvalidOperationException("Could not save the generated Hub prefab at " + PrefabPath + ".");
                    }

                    Debug.Log($"[CoreAI] Authored the Hub module prefab at {PrefabPath}.");
                }

                GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                if (instance == null)
                {
                    throw new InvalidOperationException("Could not instantiate the Hub prefab.");
                }

                Undo.RegisterCreatedObjectUndo(instance, "Add CoreAI Hub");
                // WHY: the optional Mods assembly cannot be a dependency of the Hub package.
                Type modsBinder = Type.GetType(ModsBinderType);
                if (modsBinder != null && instance.GetComponent(modsBinder) == null)
                {
                    instance.AddComponent(modsBinder);
                }

                // WHY: World-state integration is also optional and lives in the Mods assembly; the shipped
                // prefab carried this binder, so the generated composition must retain it. It registers the
                // World page only when a world manager resolves at runtime.
                Type worldStateBinder = Type.GetType(WorldStateBinderType);
                if (worldStateBinder != null && instance.GetComponent(worldStateBinder) == null)
                {
                    instance.AddComponent(worldStateBinder);
                }

                Selection.activeGameObject = instance;
                EditorSceneManager.MarkSceneDirty(instance.scene);
                Debug.Log("[CoreAI] Added CoreAI Hub to the scene. Make sure the scene has a CoreAILifetimeScope " +
                          "(and, for the Mods tab, a CoreAiModsLifetimeScope child).");
            }
            catch (Exception ex)
            {
                Debug.LogError("[CoreAI] Could not add Hub: " + ex.Message);
            }
            finally
            {
                if (authored != null)
                {
                    UnityEngine.Object.DestroyImmediate(authored);
                }
            }
        }

        private static GameObject BuildHub()
        {
            PanelSettings panel = LoadRequired<PanelSettings>(typeof(CoreAiHubWindow),
                "Assets/CoreAIHub", "Runtime/CoreAiHubPanelSettings.asset");
            VisualTreeAsset shell = LoadRequired<VisualTreeAsset>(typeof(CoreAiHubWindow),
                "Assets/CoreAIHub", "Runtime/CoreAiHub.uxml");
            VisualTreeAsset chatUxml = LoadRequired<VisualTreeAsset>(typeof(CoreAiChatPanel),
                "Assets/CoreAiUnity", "Runtime/Source/Features/Chat/UI/CoreAiChat.uxml");
            StyleSheet chatUss = LoadRequired<StyleSheet>(typeof(CoreAiChatPanel),
                "Assets/CoreAiUnity", "Runtime/Source/Features/Chat/UI/CoreAiChat.uss");
            GameObject go = new("CoreAiHub");

            UIDocument document = go.AddComponent<UIDocument>();
            document.panelSettings = panel;

            CoreAiHubWindow window = go.AddComponent<CoreAiHubWindow>();
            SerializedObject wso = new(window);
            SerializedProperty shellProp = wso.FindProperty("shellUxml");
            if (shellProp != null && shell != null)
            {
                shellProp.objectReferenceValue = shell;
                wso.ApplyModifiedPropertiesWithoutUndo();
            }

            CoreAiHubDemo bootstrap = go.AddComponent<CoreAiHubDemo>();
            SerializedObject so = new(bootstrap);
            if (chatUxml != null)
            {
                so.FindProperty("chatTemplate").objectReferenceValue = chatUxml;
            }

            if (chatUss != null)
            {
                so.FindProperty("chatStyleSheet").objectReferenceValue = chatUss;
            }

            so.ApplyModifiedPropertiesWithoutUndo();
            // WHY: bind the authored object before it is saved as a prefab, not only its live instance.
            foreach (string typeName in new[] { ModsBinderType, WorldStateBinderType })
            {
                Type binderType = Type.GetType(typeName);
                if (binderType != null && go.GetComponent(binderType) == null) go.AddComponent(binderType);
            }
            return go;
        }

        private static T LoadRequired<T>(Type assemblyOwner, string sourceRoot, string relativePath)
            where T : UnityEngine.Object
        {
            UnityEditor.PackageManager.PackageInfo package =
                UnityEditor.PackageManager.PackageInfo.FindForAssembly(assemblyOwner.Assembly);
            string path = ResolveSourcePath(package?.assetPath, sourceRoot, relativePath);
            T asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null)
            {
                throw new InvalidOperationException("Missing required Hub resource: " + path + ".");
            }

            return asset;
        }

        internal static string ResolveSourcePath(string packageAssetPath, string sourceRoot, string relativePath)
        {
            string root = string.IsNullOrEmpty(packageAssetPath) ? sourceRoot : packageAssetPath;
            return root.TrimEnd('/', '\\') + "/" + relativePath.TrimStart('/', '\\');
        }

        private static void EnsureGeneratedFolder()
        {
            string parent = "Assets";
            foreach (string segment in new[] { "CoreAIGenerated", "Hub" })
            {
                string path = parent + "/" + segment;
                if (!AssetDatabase.IsValidFolder(path) &&
                    string.IsNullOrEmpty(AssetDatabase.CreateFolder(parent, segment)))
                {
                    throw new InvalidOperationException("Could not create the generated Hub folder: " + path + ".");
                }

                parent = path;
            }
        }
    }
}
