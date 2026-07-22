using System.IO;
using CoreAI.LuaAssets;
using UnityEditor;
using UnityEngine;

namespace CoreAI.Editor
{
    /// <summary>
    /// Standalone "Lua Script Viewer" window: pick or drag-drop any <c>.lua</c>/<c>.luau</c>/<c>.txt</c>
    /// file (on disk, not necessarily a tracked project asset) and view it syntax-highlighted, with an
    /// adjustable font size and quick copy-path/reveal actions.
    /// </summary>
    /// <remarks>Read-only for MVP1 — editing stays in external IDEs.</remarks>
    public sealed class LuaScriptViewerWindow : EditorWindow
    {
        // TODO: in-place editing with a "Save" action once this window hosts a real text-editing
        // control instead of a rich-text Label (which cannot host a caret).

        private const string FontSizePrefKey = "CoreAI.LuaScriptViewer.FontSize";

        private string _filePath = "";
        private string _source = "";
        private Vector2 _scroll;
        private float _fontSize = LuaSyntaxHighlightView.DefaultFontSize;

        [MenuItem("CoreAI/Lua Script Viewer")]
        private static void Open()
        {
            LuaScriptViewerWindow window = GetWindow<LuaScriptViewerWindow>("Lua Script Viewer");
            window.minSize = new Vector2(420f, 300f);
            window.Show();
        }

        private void OnEnable()
        {
            _fontSize = EditorPrefs.GetFloat(FontSizePrefKey, LuaSyntaxHighlightView.DefaultFontSize);
        }

        private void OnGUI()
        {
            DrawToolbar();
            HandleDragAndDrop();

            if (string.IsNullOrEmpty(_filePath))
            {
                EditorGUILayout.HelpBox(
                    "Pick a .lua/.luau file, or drag one onto this window from the Project view or the OS file explorer.",
                    MessageType.Info);
                return;
            }

            _scroll = LuaSyntaxHighlightView.Draw(_source, _scroll, _fontSize, position.height - ToolbarHeight());
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            if (GUILayout.Button("Open...", EditorStyles.toolbarButton, GUILayout.Width(70)))
            {
                string picked = EditorUtility.OpenFilePanelWithFilters(
                    "Open Lua Script", "", new[] { "Lua/Luau", "lua,luau,txt", "All files", "*" });
                if (!string.IsNullOrEmpty(picked))
                {
                    LoadFile(picked);
                }
            }

            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(_filePath)))
            {
                if (GUILayout.Button("Copy Path", EditorStyles.toolbarButton, GUILayout.Width(80)))
                {
                    EditorGUIUtility.systemCopyBuffer = _filePath;
                }

                if (GUILayout.Button("Reveal", EditorStyles.toolbarButton, GUILayout.Width(60)))
                {
                    EditorUtility.RevealInFinder(_filePath);
                }
            }

            GUILayout.FlexibleSpace();
            GUILayout.Label("Font", GUILayout.Width(30));

            EditorGUI.BeginChangeCheck();
            float newSize = GUILayout.HorizontalSlider(
                _fontSize, LuaSyntaxHighlightView.MinFontSize, LuaSyntaxHighlightView.MaxFontSize, GUILayout.Width(100));
            if (EditorGUI.EndChangeCheck())
            {
                _fontSize = newSize;
                EditorPrefs.SetFloat(FontSizePrefKey, _fontSize);
            }

            EditorGUILayout.LabelField(Mathf.RoundToInt(_fontSize).ToString(), GUILayout.Width(20));

            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(_filePath))
            {
                EditorGUILayout.LabelField(_filePath, EditorStyles.miniLabel);
            }
        }

        private void HandleDragAndDrop()
        {
            Event evt = Event.current;
            if (evt.type != EventType.DragUpdated && evt.type != EventType.DragPerform)
            {
                return;
            }

            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;

            if (evt.type == EventType.DragPerform && DragAndDrop.paths.Length > 0)
            {
                DragAndDrop.AcceptDrag();
                string path = DragAndDrop.paths[0];
                if (File.Exists(path))
                {
                    LoadFile(path);
                }

                evt.Use();
            }
            else if (evt.type == EventType.DragUpdated)
            {
                evt.Use();
            }
        }

        private void LoadFile(string path)
        {
            _filePath = path;
            _source = File.ReadAllText(path);
            _scroll = Vector2.zero;
            Repaint();
        }

        private static float ToolbarHeight() => 44f;
    }
}
