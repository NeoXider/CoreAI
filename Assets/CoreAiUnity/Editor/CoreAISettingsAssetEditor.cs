#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace CoreAI.Infrastructure.Llm.Editor
{
    /// <summary>
    /// Custom inspector for CoreAI settings assets.
    /// </summary>
    [CustomEditor(typeof(CoreAISettingsAsset))]
    public sealed class CoreAISettingsAssetEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.LabelField("CoreAI Settings", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Project-wide LLM configuration", EditorStyles.miniLabel);
            EditorGUILayout.Space();

            DrawDefaultInspector();

            EditorGUILayout.Space();
            DrawActions((CoreAISettingsAsset)target);

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawActions(CoreAISettingsAsset settings)
        {
            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Copy API Key", GUILayout.Height(24)))
            {
                EditorGUIUtility.systemCopyBuffer = settings.ApiKey ?? string.Empty;
                Debug.Log("[CoreAI] API key copied to clipboard.");
            }

            if (GUILayout.Button("Reset", GUILayout.Height(24)))
            {
                if (EditorUtility.DisplayDialog("Reset CoreAI Settings", "Reset all settings to default values?", "Reset", "Cancel"))
                {
                    settings.ConfigureAuto();
                    settings.ConfigureHttpApi("http://localhost:1234/v1", string.Empty, "gpt-4o-mini");
                    settings.ConfigureLlmUnity();
                    EditorUtility.SetDirty(target);
                }
            }

            EditorGUILayout.EndHorizontal();
        }
    }
}
#endif
