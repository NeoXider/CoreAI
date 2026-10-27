using System;
using System.Collections.Generic;
using CoreAI.CodeEditing;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.Ai.Hub.CodeEditing
{
    /// <summary>Hosts the ported editor with independent language documents and selection.</summary>
    public sealed class CodeEditorSurface : IDisposable
    {
        public const string MirrorName = "coreai-hub-mod-code-highlight";
        private readonly Dictionary<string, ICodeLanguageProfile> _profiles = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Vector2> _scrollOffsets = new(StringComparer.Ordinal);
        private readonly List<string> _ids = new();
        private readonly EventCallback<ChangeEvent<string>> _onTextChanged;
        private readonly EventCallback<ChangeEvent<string>> _onLanguageChanged;
        private readonly ScrollView _scroll;
        private readonly string _initialPython;
        private IVisualElementScheduledItem _scrollRestore;
        private bool _switchingDocument;
        private bool _disposed;
        private int _generation;

        public CodeEditorSurface(string documentId, string luaSource, IEnumerable<ICodeLanguageProfile> profiles = null)
        {
            string normalized = CodeEditorTextUtility.NormalizeEditorText(luaSource ?? "");
            if (normalized.Length > CodeEditorLimits.MaximumDocumentCharacters)
                throw new ArgumentException("The source exceeds the editor document limit.", nameof(luaSource));
            Document = new CodeEditorDocument(documentId, normalized);
            Document.SelectLanguage("python");
            _initialPython = Document.Text;
            Document.SelectLanguage("lua");
            foreach (ICodeLanguageProfile profile in profiles ?? new ICodeLanguageProfile[] { new LuaCodeLanguageProfile(), new PythonCodeLanguageProfile() })
            {
                if (profile == null || (profile.LanguageId != "lua" && profile.LanguageId != "python") || string.IsNullOrEmpty(profile.DisplayName))
                    throw new ArgumentException("A Lua or Python editor language profile is required.", nameof(profiles));
                if (_profiles.ContainsKey(profile.LanguageId))
                    throw new ArgumentException("Editor language ids must be unique.", nameof(profiles));
                foreach (ICodeLanguageProfile existing in _profiles.Values)
                    if (existing.DisplayName == profile.DisplayName)
                        throw new ArgumentException("Editor language labels must be unique.", nameof(profiles));
                _profiles.Add(profile.LanguageId, profile);
                _ids.Add(profile.LanguageId);
            }
            if (!_profiles.ContainsKey("lua") || !_profiles.ContainsKey("python"))
                throw new ArgumentException("Lua and the explicit Python stub profiles are required.", nameof(profiles));
            Root = new VisualElement { name = "coreai-code-editor-surface" };
            Root.AddToClassList("coreai-code-editor");
            Root.style.flexDirection = FlexDirection.Column;
            Root.style.flexShrink = 0f;
            Root.style.height = 380f;
            Root.style.minHeight = 280f;
            Root.style.marginTop = Root.style.marginBottom = 6f;
            Root.style.backgroundColor = new Color(0.035f, 0.06f, 0.10f);
            List<string> labels = new();
            foreach (string id in _ids) labels.Add(_profiles[id].DisplayName);
            LanguageDropdown = new DropdownField("Language", labels, _ids.IndexOf("lua")) { name = "coreai-code-editor-language" };
            LanguageDropdown.style.flexShrink = 0f;
            LanguageDropdown.style.color = HubModWidgets.Text;
            VisualElement dropdownInput = LanguageDropdown.Q(className: "unity-base-popup-field__input");
            if (dropdownInput != null)
            {
                dropdownInput.style.backgroundColor = new Color(0.05f, 0.10f, 0.17f);
                dropdownInput.style.color = HubModWidgets.Text;
            }
            Root.Add(LanguageDropdown);
            VisualElement row = new();
            row.AddToClassList("coreai-code-editor__row");
            row.style.flexDirection = FlexDirection.Row;
            row.style.flexGrow = 1f;
            row.style.flexBasis = 0f;
            row.style.minHeight = 0f;
            row.style.overflow = Overflow.Hidden;
            Label gutter = new() { name = "coreai-code-editor-gutter", pickingMode = PickingMode.Ignore };
            gutter.AddToClassList("coreai-code-editor__gutter");
            gutter.style.width = 48f;
            gutter.style.flexShrink = 0f;
            gutter.style.paddingTop = 8f;
            gutter.style.paddingRight = 5f;
            gutter.style.fontSize = 13f;
            gutter.style.color = HubModWidgets.Muted;
            gutter.style.whiteSpace = WhiteSpace.Pre;
            gutter.style.unityTextAlign = TextAnchor.UpperRight;
            _scroll = new ScrollView(ScrollViewMode.VerticalAndHorizontal) { name = "coreai-code-editor-scroll" };
            _scroll.AddToClassList("coreai-code-editor__scroll");
            _scroll.style.flexGrow = 1f;
            _scroll.style.minWidth = 0f;
            _scroll.style.width = 0f;
            _scroll.style.height = new Length(100f, LengthUnit.Percent);
            _scroll.contentContainer.style.flexShrink = 0f;
            _scroll.contentContainer.style.alignItems = Align.FlexStart;
            VisualElement stack = new();
            stack.AddToClassList("coreai-code-editor__field-stack");
            stack.style.position = Position.Relative;
            stack.style.flexGrow = 1f;
            stack.style.flexShrink = 0f;
            stack.style.minWidth = new Length(100f, LengthUnit.Percent);
            Label mirror = new() { name = MirrorName, enableRichText = true, pickingMode = PickingMode.Ignore };
            mirror.AddToClassList("coreai-code-editor__mirror");
            mirror.style.flexShrink = 0f;
            mirror.style.marginLeft = mirror.style.marginRight = mirror.style.marginTop = mirror.style.marginBottom = 0f;
            mirror.style.paddingLeft = 10f;
            mirror.style.paddingTop = 8f;
            mirror.style.paddingRight = 26f;
            mirror.style.paddingBottom = 24f;
            mirror.style.fontSize = 13f;
            mirror.style.whiteSpace = WhiteSpace.Pre;
            mirror.style.color = HubModWidgets.Text;
            TextField = new TextField { name = "coreai-hub-mod-code", multiline = true, maxLength = CodeEditorLimits.MaximumDocumentCharacters };
            TextField.AddToClassList("coreai-code-editor__field");
            TextField.style.position = Position.Absolute;
            TextField.style.top = TextField.style.left = TextField.style.right = TextField.style.bottom = 0f;
            TextField.style.marginLeft = TextField.style.marginRight = TextField.style.marginTop = TextField.style.marginBottom = 0f;
            TextField.style.fontSize = 13f;
            TextField.style.flexShrink = 0f;
            VisualElement input = TextField.Q("unity-text-input");
            if (input != null)
            {
                input.style.backgroundColor = Color.clear;
                input.style.borderLeftWidth = input.style.borderRightWidth = input.style.borderTopWidth = input.style.borderBottomWidth = 0f;
                input.style.color = Color.clear;
                input.style.whiteSpace = WhiteSpace.Pre;
                input.style.paddingLeft = input.style.paddingRight = 10f;
                input.style.paddingTop = input.style.paddingBottom = 8f;
                input.style.unityTextAlign = TextAnchor.UpperLeft;
                TextElement editable = input.Q<TextElement>();
                if (editable != null) editable.enableRichText = false;
            }
            if (TextField.textSelection != null)
                TextField.textSelection.selectionColor = new Color(0.3f, 0.8f, 0.88f, 0.35f);
            stack.Add(mirror);
            stack.Add(TextField);
            _scroll.Add(stack);
            row.Add(gutter);
            row.Add(_scroll);
            Root.Add(row);
            View = new CodeEditorView(TextField, gutter, mirror, _scroll, _profiles["lua"]);
            View.SetText(normalized, documentId + ":lua");
            _onTextChanged = evt =>
            {
                // WHY: decorative string events never own document updates.
                if (_disposed || _switchingDocument || !ReferenceEquals(evt.target, TextField)) return;
                CaptureCurrent();
                if (Document.LanguageId == "python")
                    HasPythonDraftChanges = !string.Equals(Document.Text, _initialPython, StringComparison.Ordinal);
                DraftChanged?.Invoke();
            };
            _onLanguageChanged = evt =>
            {
                if (_disposed || !ReferenceEquals(evt.target, LanguageDropdown)) return;
                int index = LanguageDropdown.choices.IndexOf(evt.newValue);
                if (index >= 0) SelectLanguage(_ids[index]);
            };
            TextField.RegisterValueChangedCallback(_onTextChanged);
            LanguageDropdown.RegisterValueChangedCallback(_onLanguageChanged);
        }
        public VisualElement Root { get; }
        public TextField TextField { get; }
        public DropdownField LanguageDropdown { get; }
        public CodeEditorView View { get; }
        public CodeEditorDocument Document { get; }
        public bool HasPythonDraftChanges { get; private set; }
        public event Action DraftChanged;
        public event Action LanguageChanged;
        /// <summary>Lets a host dismiss only this surface's current editor transient.</summary>
        public bool TryHandleEscape() => !_disposed && View.TryHandleEscape();
        public string LuaDraft { get { CaptureCurrent(); return Document.LuaText; } }
        public void CaptureCurrent()
        {
            if (!_disposed && !_switchingDocument) Document.Update(TextField.value, View.Selection);
        }
        public void SelectLanguage(string languageId)
        {
            if (_disposed || Document.LanguageId == languageId) return;
            if (!_profiles.TryGetValue(languageId ?? "", out ICodeLanguageProfile profile))
                throw new ArgumentException("Unknown editor language.", nameof(languageId));
            CaptureCurrent();
            _scrollOffsets[Document.LanguageId] = _scroll.scrollOffset;
            _switchingDocument = true;
            int generation = ++_generation;
            try
            {
                Document.SelectLanguage(languageId);
                View.SetLanguage(profile);
                View.SetText(Document.Text, Document.DocumentId + ":" + languageId);
                View.Selection = Document.Selection;
                LanguageDropdown.SetValueWithoutNotify(profile.DisplayName);
                Vector2 offset = _scrollOffsets.TryGetValue(languageId, out Vector2 retained) ? retained : Vector2.zero;
                _scroll.scrollOffset = offset;
                _scrollRestore?.Pause();
                _scrollRestore = Root.schedule.Execute(() =>
                {
                    if (!_disposed && generation == _generation) _scroll.scrollOffset = offset;
                });
            }
            finally { _switchingDocument = false; }
            LanguageChanged?.Invoke();
        }
        public void Dispose()
        {
            if (_disposed) return;
            CaptureCurrent();
            _disposed = true;
            _generation++;
            _scrollRestore?.Pause();
            TextField.UnregisterValueChangedCallback(_onTextChanged);
            LanguageDropdown.UnregisterValueChangedCallback(_onLanguageChanged);
            View.Dispose();
            DraftChanged = null;
            LanguageChanged = null;
        }
    }
}
