using System.IO;
using CoreAI.LuaAssets;
using UnityEditor;
using UnityEngine;

namespace CoreAI.Editor
{
    /// <summary>
    /// Inspector for <see cref="TextAsset"/>: renders syntax-highlighted, read-only Lua/Luau source for
    /// <c>.lua</c>, <c>.luau</c>, and legacy <c>.lua.txt</c> assets. Any other <see cref="TextAsset"/>
    /// (<c>.json</c>, <c>.xml</c>, plain <c>.txt</c>, ...) falls back to a plain read-only text view so
    /// this editor — which necessarily claims the whole <see cref="TextAsset"/> type — never regresses
    /// the inspector for unrelated text assets elsewhere in the project.
    /// </summary>
    [CustomEditor(typeof(TextAsset))]
    public sealed class LuaTextAssetEditor : UnityEditor.Editor
    {
        private const float ViewHeight = 420f;

        private Vector2 _scroll;
        private Vector2 _plainScroll;

        public override void OnInspectorGUI()
        {
            TextAsset textAsset = (TextAsset)target;
            string assetPath = AssetDatabase.GetAssetPath(textAsset);

            if (!LuaAssetPaths.HasLuaExtension(assetPath))
            {
                DrawPlainTextAsset(textAsset);
                return;
            }

            EditorGUILayout.LabelField(Path.GetFileName(assetPath), EditorStyles.boldLabel);
            _scroll = LuaSyntaxHighlightView.Draw(textAsset.text, _scroll, LuaSyntaxHighlightView.DefaultFontSize, ViewHeight);
        }

        // WHY: mirrors Unity's built-in TextAsset inspector closely enough (a disabled, scrollable text
        // area) without depending on Unity's internal TextAssetInspector type.
        private void DrawPlainTextAsset(TextAsset textAsset)
        {
            _plainScroll = EditorGUILayout.BeginScrollView(_plainScroll, GUILayout.Height(ViewHeight));
            GUI.enabled = false;
            EditorGUILayout.TextArea(textAsset.text, GUILayout.ExpandHeight(true));
            GUI.enabled = true;
            EditorGUILayout.EndScrollView();
        }
    }
}
