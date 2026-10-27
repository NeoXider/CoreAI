#if COREAI_HAS_HUB
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CoreAI.Ai;
using CoreAI.Ai.Hub;
using CoreAI.Ai.Hub.CodeEditing;
using CoreAI.CodeEditing;
using CoreAI.Infrastructure.Lua;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace CoreAI.Tests.EditMode
{
    /// <summary>Bounds real attached-panel editor changes and keeps source editing separate from execution.</summary>
    public sealed class HubModEditorTypingRegressionTests
    {
        private sealed class TypingHostWindow : EditorWindow { }

        private TypingHostWindow _window;
        private HubModEditorPage _page;
        private TextField _code;
        private Label _overlay;
        private ChangeCeiling _changes;
        private TypingServiceSpy _service;
        private MemoryPreferences _preferences;
        private string _previousClipboard;
        private int _queriesAtBuild;

        [TearDown]
        public void TearDown()
        {
            _page?.OnDestroyed();
            if (_window != null)
            {
                if (_changes != null)
                    _window.rootVisualElement.UnregisterCallback<ChangeEvent<string>>(_changes.Observe, TrickleDown.TrickleDown);
                _window.Close();
            }
            if (_previousClipboard != null) GUIUtility.systemCopyBuffer = _previousClipboard;
            _window = null;
            _page = null;
        }

        [UnityTest]
        public IEnumerator AttachedTyping_AndCaretKeepRawLuaAndNeverRunOrPersist()
        {
            yield return AttachEditor();
            const string source = "local n = 1";
            string typed = "";
            foreach (char character in source)
            {
                _changes.Reset();
                SendKey(KeyCode.None, character);
                typed += character;
                yield return WaitForHighlight(typed);
                AssertEditor(typed);
                Assert.Greater(_changes.SourceChanges, 0,
                    "Native key input must produce a change targeted at the actual attached TextField.");
            }

            _changes.Reset();
            _code.SelectRange(0, 0);
            SendKey(KeyCode.RightArrow, '\0');
            AssertEditor(source);
            Assert.AreEqual(0, _changes.SourceChanges, "Moving the caret must not edit the source.");
            AssertNoServiceEffects();
        }

        [UnityTest]
        public IEnumerator AttachedPaste_UnicodeMarkupAndBoundedLargeDraftStayRawWithoutExecution()
        {
            yield return AttachEditor();
            const string source = "-- brackets [] and <tag>\n\tlocal text = \"\u03bb \U0001F600 \u00a0 <color=red>\"\nlocal value = [=[a\tb\u00a0c < b]=]";
            yield return PasteThroughPage(source);
            yield return WaitForHighlight(source);
            AssertEditor(source);
            Assert.Greater(_changes.SourceChanges, 0);

            StringBuilder large = new();
            for (int index = 0; index < 128; index++) large.Append("local n = 1 -- [] <> comment\n");
            string largeSource = large.ToString();
            Assert.LessOrEqual(largeSource.Length, 8192, "The fault-control draft must remain bounded.");
            yield return PasteThroughPage(largeSource);
            yield return WaitForHighlight(largeSource);
            AssertEditor(largeSource);
            Assert.Greater(_changes.SourceChanges, 0);
            AssertNoServiceEffects();
        }

        private IEnumerator AttachEditor()
        {
            _previousClipboard = GUIUtility.systemCopyBuffer ?? "";
            _service = new TypingServiceSpy();
            _preferences = new MemoryPreferences();
            _page = new HubModEditorPage(_service, "typing-probe", "", null, _preferences);
            _window = ScriptableObject.CreateInstance<TypingHostWindow>();
            _window.titleContent = new GUIContent("Hub editor typing regression");
            _window.position = new Rect(100, 100, 720, 600);
            _window.Show();
            _window.rootVisualElement.Add(_page.Build());
            _code = _window.rootVisualElement.Q<TextField>("coreai-hub-mod-code");
            _overlay = _window.rootVisualElement.Q<Label>("coreai-hub-mod-code-highlight");
            Assert.IsNotNull(_code);
            Assert.IsNotNull(_overlay);
            Assert.IsFalse(_code.Contains(_overlay), "Rich text must be a sibling of the editable source.");
            Assert.AreSame(_code.parent, _overlay.parent);
            _changes = new ChangeCeiling(_code, _overlay);
            _window.rootVisualElement.RegisterCallback<ChangeEvent<string>>(_changes.Observe, TrickleDown.TrickleDown);
            for (int frame = 0; frame < 30 && _code.panel == null; frame++) yield return null;
            Assert.IsNotNull(_code.panel, "Detached field setters are not attached-panel input evidence.");
            Assert.AreSame(_window.rootVisualElement.panel, _code.panel);
            _window.Focus();
            _code.Focus();
            yield return null;
            _queriesAtBuild = _service.Queries;
            _changes.Reset();
        }

        private void SendKey(KeyCode keyCode, char character)
        {
            VisualElement input = _code.Q("unity-text-input");
            Assert.IsNotNull(input);
            TextElement editable = input.Query<TextElement>().ToList().FirstOrDefault(element =>
                !ReferenceEquals(element, _overlay) && !_overlay.Contains(element));
            Assert.IsNotNull(editable, "The real editable text element must receive the native key event.");
            editable.Focus();
            using KeyDownEvent key = KeyDownEvent.GetPooled(new Event
                { type = EventType.KeyDown, keyCode = keyCode, character = character });
            editable.SendEvent(key);
        }

        private IEnumerator PasteThroughPage(string source)
        {
            Button paste = _window.rootVisualElement.Query<Button>().ToList().Single(button => button.text == "Paste");
            Assert.IsTrue(paste.focusable);
            _window.Focus();
            paste.Focus();
            yield return null;
            Assert.AreSame(paste, paste.panel.focusController.focusedElement,
                "Navigation submit must address the focused Paste button rather than the source editor.");
            GUIUtility.systemCopyBuffer = source;
            Assert.AreEqual(source, GUIUtility.systemCopyBuffer, "The exact test clipboard must be available before native paste.");
            int clicks = 0;
            Action observedClick = () => clicks++;
            paste.clicked += observedClick;
            try
            {
                _changes.Reset();
                using (NavigationSubmitEvent submit = NavigationSubmitEvent.GetPooled())
                {
                    submit.target = paste;
                    paste.SendEvent(submit);
                }
                for (int frame = 0; frame < 5 && clicks == 0; frame++) yield return null;
                Assert.AreEqual(1, clicks, "The actual page Paste action must run exactly once through Button.clicked.");
                Assert.AreEqual(source, GUIUtility.systemCopyBuffer, "Paste must consume the unchanged exact clipboard draft.");
            }
            finally
            {
                paste.clicked -= observedClick;
            }
        }

        private void AssertEditor(string raw)
        {
            Assert.IsFalse(_changes.CeilingReached,
                "Descendant label changes must not recursively re-enter highlighting; the trickle guard stopped the runaway.");
            Assert.Less(_changes.Total, ChangeCeiling.Maximum);
            string context = $"Field={_code.value?.Length}, draft={_page.DraftSource?.Length}, overlay={_overlay.text?.Length}, events={_changes.Total}, sourceEvents={_changes.SourceChanges}; labels=" +
                string.Join(" | ", _window.rootVisualElement.Query<Label>().ToList().Select(label => label.text));
            Assert.AreEqual(raw, _code.value, "Raw attached field: " + context);
            Assert.AreEqual(raw, _page.DraftSource, "Page draft: " + context);
            string rendered = RenderedHighlight();
            Assert.AreEqual(ExpectedHighlight(raw), rendered, "Highlight overlay: " + context);
            Assert.LessOrEqual(rendered.Length, Math.Max(1024, raw.Length * 32),
                "Overlay markup must remain bounded by the original raw draft.");
            AssertNoServiceEffects();
        }

        private string RenderedHighlight() => string.Join("\n", _overlay.Query<TextElement>(className: "coreai-code-editor__line")
            .ToList().Select(line => line.text));

        private static string ExpectedHighlight(string raw) =>
            string.Join("\n", new LuaCodeLanguageProfile().HighlightLines(raw.Split('\n')));

        private IEnumerator WaitForHighlight(string raw)
        {
            string expected = ExpectedHighlight(raw);
            double deadline = EditorApplication.timeSinceStartup + 2;
            while (RenderedHighlight() != expected && EditorApplication.timeSinceStartup < deadline)
            {
                _window.Repaint();
                yield return null;
            }
            Assert.AreEqual(expected, RenderedHighlight(), "The deferred bounded presentation must settle on the exact raw draft.");
        }

        private void AssertNoServiceEffects()
        {
            Assert.AreEqual(0, _service.SaveOrReloadCalls);
            Assert.AreEqual(0, _service.RuntimeCalls);
            Assert.AreEqual(0, _service.PersistenceCalls);
            Assert.AreEqual(0, _service.InventoryMutations);
            Assert.AreEqual(_queriesAtBuild, _service.Queries, "Draft edits must not refresh or query the runtime inventory.");
            Assert.AreEqual("local stored = 7", _service.StoredSource);
            Assert.AreEqual(0, _preferences.Writes);
        }

        private sealed class ChangeCeiling
        {
            internal const int Maximum = 8;
            private readonly TextField _source;
            private readonly VisualElement _decoration;
            internal int Total;
            internal int SourceChanges;
            internal bool CeilingReached;
            internal ChangeCeiling(TextField source, VisualElement decoration) { _source = source; _decoration = decoration; }
            internal void Reset() { Total = 0; SourceChanges = 0; CeilingReached = false; }
            internal void Observe(ChangeEvent<string> change)
            {
                if (change.target is not VisualElement target ||
                    (!ReferenceEquals(target, _source) && !_source.Contains(target) &&
                     !ReferenceEquals(target, _decoration) && !_decoration.Contains(target))) return;
                Total++;
                if (ReferenceEquals(target, _source)) SourceChanges++;
                if (Total >= Maximum)
                {
                    CeilingReached = true;
                    change.StopImmediatePropagation();
                }
            }
        }

        private sealed class MemoryPreferences : IHubModEditorPreferences
        {
            private bool _keep;
            internal int Writes;
            public bool KeepObjectsOnSave { get => _keep; set { _keep = value; Writes++; } }
        }

        private sealed class TypingServiceSpy : IHubModService
        {
            internal int Queries;
            internal int SaveOrReloadCalls;
            internal int RuntimeCalls;
            internal int PersistenceCalls;
            internal int InventoryMutations;
            internal string StoredSource = "local stored = 7";
            public bool IsSupported => true;
            public string NewModTemplate => "local stored = 7";
            public event Action ModsChanged { add { } remove { } }
            public event Action LogsChanged { add { } remove { } }
            public IReadOnlyList<HubModRecord> ListMods() { Queries++; return Array.Empty<HubModRecord>(); }
            public bool TryGetSource(string id, out string source) { Queries++; source = StoredSource; return true; }
            public bool IsLoaded(string id) { Queries++; return false; }
            public void SaveOrReload(string id, string code) { SaveOrReloadCalls++; Mutate(); StoredSource = code; }
            public HubModSaveResult SaveOrReload(string id, string code, ModReloadMode mode)
            { SaveOrReload(id, code); return new HubModSaveResult(id, false, mode, null); }
            public void Enable(string id) => Mutate();
            public void Disable(string id) => Mutate();
            public bool Delete(string id) { Mutate(); return true; }
            public IReadOnlyList<LuaScriptRevision> ListModVersions(string id) { Queries++; return Array.Empty<LuaScriptRevision>(); }
            public bool TryRevertMod(string id, int revisionIndex, out string source) { Mutate(); source = StoredSource; return true; }
            public string ExportMod(string id) { Queries++; return ""; }
            public bool ImportMod(string json) { Mutate(); return true; }
            public bool ApplyBundledUpdate(string id) { Mutate(); return true; }
            public string RecentErrors(string id) { Queries++; return ""; }
            public IReadOnlyList<LuaModHandlerError> RecentErrorEntries(string id = null)
            { Queries++; return Array.Empty<LuaModHandlerError>(); }
            public IReadOnlyList<LuaModReport> RecentReports(string id = null) { Queries++; return Array.Empty<LuaModReport>(); }
            public void ClearReports() => Mutate();
            public void ClearErrors() => Mutate();
            public bool GetReportLoggingEnabled(string id) { Queries++; return false; }
            public bool SetReportLoggingEnabled(string id, bool enabled) { Mutate(); return true; }
            private void Mutate() { RuntimeCalls++; PersistenceCalls++; InventoryMutations++; }
        }
    }
}
#endif
