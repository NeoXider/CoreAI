using System;
using CoreAI.Composition;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace CoreAI.Editor
{
    /// <summary>
    /// <c>CoreAI/Setup/Create Play + Create World</c>: adds one <see cref="CoreAiPlayWorldBootstrap"/> to the open
    /// scene with its four slots filled (controls and local player, look, default world content, overlay chat), so
    /// Play gives a walkable, AI-editable world with no manual wiring. The menu only adds runtime components;
    /// everything they compose is built in the Editor and in a player alike.
    /// </summary>
    public static class CoreAiPlayWorldSetupMenu
    {
        /// <summary>The menu path; also what documentation and welcome screens point at.</summary>
        public const string MenuPath = "CoreAI/Setup/Create Play + Create World";

        /// <summary>The name of the GameObject the menu adds.</summary>
        public const string ObjectName = "CoreAI Play World";

        // WHY type names: this assembly references neither VContainer nor CoreAI.RbxApi.Binding, so the scope
        // and world host types cannot be named here without breaking a project that does not reference them.
        private const string CoreScopeTypeName = "CoreAI.Composition.CoreAILifetimeScope";
        private const string ModsScopeTypeName = "CoreAI.Composition.CoreAiModsLifetimeScope";
        private const string WorldHostTypeName = "CoreAI.Mods.Rbx.Binding.RbxWorldHost";

        [MenuItem(MenuPath, priority = 13)]
        public static void CreatePlayWorldMenuItem()
        {
            GameObject created = CreatePlayWorld();
            Selection.activeGameObject = created;
            EditorGUIUtility.PingObject(created);
        }

        /// <summary>
        /// Adds the bootstrap and its default modules to the active scene and returns their GameObject, or returns
        /// the existing one when the scene already has a bootstrap. A CoreAI scope, mods scope or world host already
        /// in the scene is assigned to the bootstrap instead of being created a second time.
        /// </summary>
        public static GameObject CreatePlayWorld()
        {
            CoreAiPlayWorldBootstrap existing = UnityEngine.Object.FindFirstObjectByType<CoreAiPlayWorldBootstrap>();
            if (existing != null)
            {
                return existing.gameObject;
            }

            GameObject root = new(ObjectName);
            Undo.RegisterCreatedObjectUndo(root, "Create CoreAI Play World");
            CoreAiPlayWorldBootstrap bootstrap = root.AddComponent<CoreAiPlayWorldBootstrap>();
            SerializedObject serialized = new(bootstrap);
            AdoptSceneComponent(serialized, "coreScope", CoreScopeTypeName);
            AdoptSceneComponent(serialized, "modsScope", ModsScopeTypeName);
            AdoptSceneComponent(serialized, "worldHost", WorldHostTypeName);
            FillSlot(serialized, "controlsModule", root.AddComponent<CoreAiPlayerControlsModule>());
            FillSlot(serialized, "lookModule", root.AddComponent<CoreAiPlayWorldLookModule>());
            FillSlot(serialized, "worldContentModule", root.AddComponent<CoreAiDefaultWorldContent>());
            FillSlot(serialized, "overlayChatModule", root.AddComponent<CoreAiOverlayChatModule>());
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(bootstrap);
            if (root.scene.IsValid())
            {
                EditorSceneManager.MarkSceneDirty(root.scene);
            }

            Debug.Log("[CoreAI] " + ObjectName + " added. Press Play: it composes the CoreAI scope, the mods "
                      + "scope, the world host, a baseplate and your character at runtime, in the Editor and in a "
                      + "built player. T or Enter opens the chat.");
            return root;
        }

        private static void FillSlot(SerializedObject bootstrap, string field, MonoBehaviour module)
        {
            SerializedProperty property = bootstrap.FindProperty(field);
            if (property != null && property.objectReferenceValue == null)
            {
                property.objectReferenceValue = module;
            }
        }

        private static void AdoptSceneComponent(SerializedObject bootstrap, string field, string typeName)
        {
            MonoBehaviour found = FindSceneComponent(typeName);
            SerializedProperty property = bootstrap.FindProperty(field);
            if (found != null && property != null && property.objectReferenceValue == null)
            {
                property.objectReferenceValue = found;
            }
        }

        private static MonoBehaviour FindSceneComponent(string fullTypeName)
        {
            MonoBehaviour[] behaviours =
                UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (MonoBehaviour behaviour in behaviours)
            {
                if (behaviour != null
                    && string.Equals(behaviour.GetType().FullName, fullTypeName, StringComparison.Ordinal))
                {
                    return behaviour;
                }
            }

            return null;
        }
    }
}
