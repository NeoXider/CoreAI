using CoreAI.Composition;
using UnityEditor;
using UnityEngine;

namespace CoreAI.Editor
{
    /// <summary>Inspector for the root CoreAI scope and its optional child modules.</summary>
    [CustomEditor(typeof(CoreAILifetimeScope))]
    public sealed class CoreAILifetimeScopeEditor : UnityEditor.Editor
    {
        /// <inheritdoc />
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            DrawPropertiesExcluding(
                serializedObject,
                "m_Script",
                "worldPrefabRegistry",
                "legacyLuaAllowedScenes",
                "legacyEnableFullLuaAccess",
                "legacyEnableFullLuaPrivateAccess");
            serializedObject.ApplyModifiedProperties();

            CoreAILifetimeScope scope = (CoreAILifetimeScope)target;
            CoreAiLuaWorldModule module = scope.LuaWorldModule;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Lua / World Commands", EditorStyles.boldLabel);

            if (module == null)
            {
                EditorGUILayout.HelpBox(
                    "Optional. Add a child module to configure Lua capabilities, spawn prefabs, and scene access. Existing serialized settings are copied during migration.",
                    MessageType.Info);
                if (GUILayout.Button("Add Lua / World Commands Module"))
                {
                    CreateModule(scope);
                }

                return;
            }

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.ObjectField("Active module", module, typeof(CoreAiLuaWorldModule), true);
            }

            if (GUILayout.Button("Select Lua / World Commands Module"))
            {
                Selection.activeObject = module.gameObject;
            }
        }

        private static void CreateModule(CoreAILifetimeScope scope)
        {
            GameObject child = new("Lua and World Commands");
            Undo.RegisterCreatedObjectUndo(child, "Add CoreAI Lua module");
            child.transform.SetParent(scope.transform, false);
            CoreAiLuaWorldModule module = Undo.AddComponent<CoreAiLuaWorldModule>(child);
            scope.CopyLegacyLuaWorldConfigurationTo(module);
            Undo.RecordObject(scope, "Assign CoreAI Lua module");
            scope.SetLuaWorldModuleForMigration(module);
            EditorUtility.SetDirty(scope);
            EditorUtility.SetDirty(module);
            Selection.activeObject = child;
        }
    }
}
