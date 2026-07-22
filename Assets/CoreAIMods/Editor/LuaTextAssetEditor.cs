using System.IO;
using CoreAI.LuaAssets;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.Editor
{
    /// <summary>
    /// Inspector for <see cref="TextAsset"/>: renders syntax-highlighted, read-only Lua/Luau source for
    /// <c>.lua</c>, <c>.luau</c>, and legacy <c>.lua.txt</c> assets. Any other <see cref="TextAsset"/>
    /// (<c>.json</c>, <c>.xml</c>, plain <c>.txt</c>, ...) falls back to a plain read-only text view so
    /// this editor — which necessarily claims the whole <see cref="TextAsset"/> type — never regresses
    /// the inspector for unrelated text assets elsewhere in the project. Built in UI Toolkit
    /// (<see cref="CreateInspectorGUI"/>).
    /// </summary>
    [CustomEditor(typeof(TextAsset))]
    public sealed class LuaTextAssetEditor : UnityEditor.Editor
    {
        private const float ViewHeight = 420f;

        public override VisualElement CreateInspectorGUI()
        {
            var textAsset = (TextAsset)target;
            string assetPath = AssetDatabase.GetAssetPath(textAsset);

            var root = new VisualElement();

            if (!LuaAssetPaths.HasLuaExtension(assetPath))
            {
                root.Add(BuildPlainTextView(textAsset));
                return root;
            }

            var title = new Label(Path.GetFileName(assetPath));
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.marginBottom = 4f;
            root.Add(title);

            var view = new LuaSyntaxHighlightView();
            view.SetSource(textAsset.text);
            view.Root.style.height = ViewHeight;
            view.Root.style.flexGrow = 0f;
            root.Add(view.Root);

            return root;
        }

        // WHY: mirrors Unity's built-in TextAsset inspector closely enough (a read-only, scrollable text
        // area) without depending on Unity's internal TextAssetInspector type.
        private static VisualElement BuildPlainTextView(TextAsset textAsset)
        {
            var field = new TextField { multiline = true, value = textAsset.text, isReadOnly = true };
            field.style.height = ViewHeight;
            field.style.whiteSpace = WhiteSpace.Normal;

            var input = field.Q(className: "unity-text-field__input");
            if (input != null)
            {
                input.style.flexGrow = 1f;
            }

            return field;
        }
    }
}
