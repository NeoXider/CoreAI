using System.IO;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.Editor
{
    /// <summary>
    /// Standalone "Lua Script Viewer" window: pick or drag-drop any <c>.lua</c>/<c>.luau</c>/<c>.txt</c>
    /// file (on disk, not necessarily a tracked project asset) and view it syntax-highlighted, with an
    /// adjustable font size and quick copy-path/reveal actions. Built entirely in UI Toolkit
    /// (<see cref="CreateGUI"/>).
    /// </summary>
    /// <remarks>Read-only for MVP1 — editing stays in external IDEs.</remarks>
    public sealed class LuaScriptViewerWindow : EditorWindow
    {
        // TODO: in-place editing with a "Save" action once this window hosts a real text-editing
        // control instead of a rich-text Label (which cannot host a caret).

        private const string FontSizePrefKey = "CoreAI.LuaScriptViewer.FontSize";

        private string _filePath = "";
        private float _fontSize = LuaSyntaxHighlightView.DefaultFontSize;

        private LuaSyntaxHighlightView _view;
        private Label _pathLabel;
        private Label _emptyHint;
        private Button _copyPathButton;
        private Button _revealButton;

        [MenuItem("CoreAI/Lua Script Viewer")]
        private static void Open()
        {
            LuaScriptViewerWindow window = GetWindow<LuaScriptViewerWindow>("Lua Script Viewer");
            window.minSize = new Vector2(420f, 300f);
            window.Show();
        }

        private void CreateGUI()
        {
            _fontSize = EditorPrefs.GetFloat(FontSizePrefKey, LuaSyntaxHighlightView.DefaultFontSize);

            VisualElement root = rootVisualElement;

            Toolbar toolbar = new();

            ToolbarButton openButton = new(OpenFilePicker) { text = "Open..." };
            toolbar.Add(openButton);

            _copyPathButton = new ToolbarButton(CopyPathToClipboard) { text = "Copy Path" };
            toolbar.Add(_copyPathButton);

            _revealButton = new ToolbarButton(RevealFile) { text = "Reveal" };
            toolbar.Add(_revealButton);

            VisualElement spacer = new();
            spacer.style.flexGrow = 1f;
            toolbar.Add(spacer);

            Label fontLabel = new("Font");
            fontLabel.style.unityTextAlign = TextAnchor.MiddleLeft;
            fontLabel.style.marginRight = 4f;
            toolbar.Add(fontLabel);

            Label fontValue = new(Mathf.RoundToInt(_fontSize).ToString());
            fontValue.style.unityTextAlign = TextAnchor.MiddleLeft;
            fontValue.style.marginLeft = 4f;
            fontValue.style.minWidth = 20f;

            SliderInt fontSlider = new((int)LuaSyntaxHighlightView.MinFontSize, (int)LuaSyntaxHighlightView.MaxFontSize)
            {
                value = Mathf.RoundToInt(_fontSize)
            };
            fontSlider.style.width = 120f;
            fontSlider.RegisterValueChangedCallback(evt =>
            {
                _fontSize = evt.newValue;
                EditorPrefs.SetFloat(FontSizePrefKey, _fontSize);
                _view.SetFontSize(_fontSize);
                fontValue.text = evt.newValue.ToString();
            });
            toolbar.Add(fontSlider);
            toolbar.Add(fontValue);

            root.Add(toolbar);

            _pathLabel = new Label { enableRichText = false };
            _pathLabel.style.display = DisplayStyle.None;
            _pathLabel.style.paddingLeft = 4f;
            _pathLabel.style.paddingTop = 2f;
            _pathLabel.style.paddingBottom = 2f;
            _pathLabel.style.opacity = 0.75f;
            _pathLabel.style.whiteSpace = WhiteSpace.Normal;
            root.Add(_pathLabel);

            _emptyHint = new Label(
                "Pick a .lua/.luau file, or drag one onto this window from the Project view or the OS file explorer.");
            _emptyHint.style.whiteSpace = WhiteSpace.Normal;
            _emptyHint.style.paddingLeft = 8f;
            _emptyHint.style.paddingRight = 8f;
            _emptyHint.style.paddingTop = 8f;
            _emptyHint.style.paddingBottom = 8f;
            root.Add(_emptyHint);

            _view = new LuaSyntaxHighlightView(_fontSize);
            _view.Root.style.display = DisplayStyle.None;
            root.Add(_view.Root);

            // WHY: DragAndDrop.paths carries both Project-view assets and OS file-explorer files, so the
            // viewer opens scripts that are not tracked project assets — not just draggable ObjectFields.
            root.RegisterCallback<DragUpdatedEvent>(OnDragUpdated);
            root.RegisterCallback<DragPerformEvent>(OnDragPerform);

            UpdateActionButtons();
        }

        private void OpenFilePicker()
        {
            string picked = EditorUtility.OpenFilePanelWithFilters(
                "Open Lua Script", "", new[] { "Lua/Luau", "lua,luau,txt", "All files", "*" });
            if (!string.IsNullOrEmpty(picked))
            {
                LoadFile(picked);
            }
        }

        private void CopyPathToClipboard()
        {
            if (!string.IsNullOrEmpty(_filePath))
            {
                EditorGUIUtility.systemCopyBuffer = _filePath;
            }
        }

        private void RevealFile()
        {
            if (!string.IsNullOrEmpty(_filePath))
            {
                EditorUtility.RevealInFinder(_filePath);
            }
        }

        private void OnDragUpdated(DragUpdatedEvent evt)
        {
            if (DragAndDrop.paths != null && DragAndDrop.paths.Length > 0)
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            }
        }

        private void OnDragPerform(DragPerformEvent evt)
        {
            if (DragAndDrop.paths == null || DragAndDrop.paths.Length == 0)
            {
                return;
            }

            DragAndDrop.AcceptDrag();
            string path = DragAndDrop.paths[0];
            if (File.Exists(path))
            {
                LoadFile(path);
            }
        }

        private void LoadFile(string path)
        {
            _filePath = path;
            _view.SetSource(File.ReadAllText(path));
            _view.Root.style.display = DisplayStyle.Flex;
            _emptyHint.style.display = DisplayStyle.None;
            _pathLabel.text = _filePath;
            _pathLabel.style.display = DisplayStyle.Flex;
            UpdateActionButtons();
        }

        private void UpdateActionButtons()
        {
            bool hasFile = !string.IsNullOrEmpty(_filePath);
            _copyPathButton?.SetEnabled(hasFile);
            _revealButton?.SetEnabled(hasFile);
        }
    }
}
