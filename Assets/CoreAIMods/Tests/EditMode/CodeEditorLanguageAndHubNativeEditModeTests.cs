#if COREAI_HAS_HUB && COREAI_LUA
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CoreAI.Ai;
using CoreAI.Ai.Hub;
using CoreAI.Ai.Hub.CodeEditing;
using CoreAI.Ai.LuaCs;
using CoreAI.CodeEditing;
using CoreAI.Composition;
using CoreAI.Hub;
using CoreAI.Hub.UI;
using CoreAI.Logging;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace CoreAI.Tests.EditMode
{
    /// <summary>Exercises language policies and actual grouped Mods/Hub Escape routing on an attached native panel.</summary>
    public sealed class CodeEditorLanguageAndHubNativeEditModeTests
    {
        private sealed class EditorHostWindow : EditorWindow { }
        private EditorHostWindow _window;
        private CodeEditorSurface _surface;
        private GameObject _hubObject;
        private CoreAiHubWindow _hub;
        private LuaCsModStack _stack;
        private LuaCsModRuntimeHubService _service;

        [TearDown]
        public void TearDown()
        {
            _surface?.Dispose();
            if (_hub != null) _hub.Registry = null;
            if (_hubObject != null) UnityEngine.Object.DestroyImmediate(_hubObject);
            _service?.Dispose();
            _stack?.Runtime.ShutdownWithoutPersistence();
            if (_window != null) _window.Close();
            _surface = null;
            _hubObject = null;
            _hub = null;
            _service = null;
            _stack = null;
            _window = null;
        }

        [UnityTest]
        public IEnumerator NativeLuaEnter_InsideLongStringPreservesLiteralAndSingleUndoRedo()
        {
            const string initial = "local value = [[\nthen\n]]";
            yield return AttachSurface(initial);
            int caret = initial.IndexOf("then", StringComparison.Ordinal) + 4;
            _surface.View.Selection = new CodeSelection(caret, caret);
            SourceKey(KeyCode.Return, '\n');
            const string expected = "local value = [[\nthen\n\n]]";
            yield return SettleText(expected);
            SourceKey(KeyCode.Z, '\0', EventModifiers.Control);
            yield return SettleText(initial);
            SourceKey(KeyCode.Y, '\0', EventModifiers.Control);
            yield return SettleText(expected);
        }

        [UnityTest]
        public IEnumerator NativePairs_UseLuaLengthOperatorAndRetainPythonCommentPolicy()
        {
            const string lua = "local n = #f";
            yield return AttachSurface(lua);
            _surface.View.Selection = new CodeSelection(lua.Length, lua.Length);
            SourceKey(KeyCode.None, '(');
            yield return SettleText(lua + "()");
            Assert.AreEqual(lua.Length + 1, _surface.View.Selection.Caret);

            _surface.SelectLanguage("python");
            const string python = "# comment";
            _surface.View.SetText(python, "native-python-comment");
            _surface.View.Selection = new CodeSelection(python.Length, python.Length);
            SourceKey(KeyCode.None, '(');
            yield return SettleText(python + "(");
        }

        [UnityTest]
        public IEnumerator ActualGroupedModsHub_EscapeDismissesCompletionBeforeCollapsing()
        {
            yield return AttachActualGroupedModsHub();
            _surface.View.SetText("loc", "hosted-completion");
            _surface.View.Selection = new CodeSelection(3, 3);
            SourceKey(KeyCode.Space, ' ', EventModifiers.Control);
            VisualElement popup = null;
            for (int frame = 0; frame < 30; frame++)
            {
                popup = _surface.Root.Q("editor-completion");
                if (popup != null && popup.style.display == DisplayStyle.Flex) break;
                yield return null;
            }
            Assert.IsNotNull(popup);
            Assert.AreEqual(DisplayStyle.Flex, popup.style.display.value);
            SourceKey(KeyCode.Escape, (char)27);
            yield return null;
            Assert.IsFalse(Read<bool>(_hub, "_collapsed"));
            Assert.AreEqual(DisplayStyle.None, popup.style.display.value);
            SourceKey(KeyCode.Escape, (char)27);
            Assert.IsTrue(Read<bool>(_hub, "_collapsed"), "The next unhandled Escape must retain the configured Hub policy.");
        }

        [UnityTest]
        public IEnumerator ActualGroupedModsHub_EscapeDismissesGoto_LogsAndDestroyedPagesRemainUnhandled()
        {
            yield return AttachActualGroupedModsHub();
            SourceKey(KeyCode.G, '\0', EventModifiers.Control);
            TextField prompt = null;
            for (int frame = 0; frame < 30; frame++)
            {
                VisualElement root = _surface.Root.Q(className: "coreai-code-goto");
                prompt = root?.Q<TextField>();
                if (prompt != null && prompt.panel?.focusController.focusedElement != null) break;
                yield return null;
            }
            Assert.IsNotNull(prompt);
            SendKey(prompt.Q("unity-text-input").Query<TextElement>().ToList().First(), KeyCode.Escape, (char)27);
            yield return null;
            Assert.IsFalse(Read<bool>(_hub, "_collapsed"));
            Assert.IsNull(_surface.Root.Q(className: "coreai-code-goto"));

            Dictionary<string, IHubPage> pages = Read<Dictionary<string, IHubPage>>(_hub, "_pages");
            HubSubTabPage grouped = (HubSubTabPage)pages[HubModsPages.ModsPageId];
            HubSubTabView tabs = _window.rootVisualElement.Q<HubSubTabView>();
            tabs.Select("Logs");
            Assert.IsFalse(((IHubEscapeHandler)grouped).TryHandleEscape());
            grouped.OnDestroyed();
            Assert.IsFalse(((IHubEscapeHandler)grouped).TryHandleEscape());
        }

        private IEnumerator AttachSurface(string source)
        {
            CreateWindow();
            _surface = new CodeEditorSurface("native-language", source);
            _window.rootVisualElement.Add(_surface.Root);
            for (int frame = 0; frame < 30 && _surface.TextField.panel == null; frame++) yield return null;
            Assert.IsNotNull(_surface.TextField.panel);
            _surface.TextField.Focus();
            yield return null;
        }

        private IEnumerator AttachActualGroupedModsHub()
        {
            CreateWindow();
            _hubObject = new GameObject("CoreAI grouped Mods Escape regression");
            _hubObject.SetActive(false);
            _hubObject.AddComponent<UIDocument>();
            _hub = _hubObject.AddComponent<CoreAiHubWindow>();
            VisualTreeAsset shell = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/CoreAIHub/Runtime/CoreAiHub.uxml");
            Assert.IsNotNull(shell);
            Write(_hub, "shellUxml", shell);
            Write(_hub, "requireVisibleCursor", false);
            _stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
                { Log = NullLog.Instance, AutoPersistMods = false, RegisterWorldEditBuildBindings = false });
            _service = new LuaCsModRuntimeHubService(_stack.Runtime,
                CoreServicesInstaller.DefaultLocalHostIdentityProvider.GetActorContext(BuiltInAgentRoleIds.Programmer), null);
            HubPageRegistry registry = new HubPageRegistry();
            HubModsPages.Register(registry, _service);
            _hub.Registry = registry;
            typeof(CoreAiHubWindow).GetMethod("Rebuild", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(_hub, new object[] { _window.rootVisualElement });
            Assert.AreEqual(HubModsPages.ModsPageId, _hub.ActivePageId);
            Assert.IsNotNull(_window.rootVisualElement.Q<HubSubTabView>());
            Button add = _window.rootVisualElement.Query<Button>().ToList().Single(button => button.text == "Add");
            Assert.IsTrue(add.focusable);
            add.Focus();
            yield return null;
            Assert.AreSame(add, add.panel.focusController.focusedElement,
                "The actual registered Mods Add button must own native navigation submit.");
            int clicks = 0;
            Action observeClick = () => clicks++;
            add.clicked += observeClick;
            try
            {
                using NavigationSubmitEvent submit = NavigationSubmitEvent.GetPooled();
                submit.target = add;
                add.SendEvent(submit);
                Assert.AreEqual(1, clicks, "Native submit must invoke the production Mods Add action exactly once.");
            }
            finally
            {
                add.clicked -= observeClick;
            }
            Dictionary<string, IHubPage> pages = Read<Dictionary<string, IHubPage>>(_hub, "_pages");
            HubSubTabPage grouped = (HubSubTabPage)pages[HubModsPages.ModsPageId];
            HubModsPage mods = (HubModsPage)Read<IHubPage>(grouped, "_activeChild");
            HubModEditorPage editor = Read<HubModEditorPage>(mods, "_editor");
            Assert.IsNotNull(editor);
            _surface = Read<CodeEditorSurface>(editor, "_editor");
            for (int frame = 0; frame < 30 && _surface.TextField.panel == null; frame++) yield return null;
            Assert.IsNotNull(_surface.TextField.panel);
            _surface.TextField.Focus();
            yield return null;
        }

        private void CreateWindow()
        {
            _window = ScriptableObject.CreateInstance<EditorHostWindow>();
            _window.titleContent = new GUIContent("CoreAI host/editor regression");
            _window.position = new Rect(100, 100, 900, 720);
            _window.Show();
            _window.Focus();
        }

        private void SourceKey(KeyCode code, char character, EventModifiers modifiers = EventModifiers.None)
        {
            TextElement input = _surface.TextField.Q("unity-text-input").Query<TextElement>().ToList().First();
            SendKey(input, code, character, modifiers);
        }

        private static void SendKey(VisualElement target, KeyCode code, char character,
            EventModifiers modifiers = EventModifiers.None)
        {
            target.Focus();
            using KeyDownEvent key = KeyDownEvent.GetPooled(new Event
                { type = EventType.KeyDown, keyCode = code, character = character, modifiers = modifiers });
            target.SendEvent(key);
        }

        private IEnumerator SettleText(string expected)
        {
            double deadline = EditorApplication.timeSinceStartup + 2;
            while (_surface.TextField.value != expected && EditorApplication.timeSinceStartup < deadline)
            {
                _window.Repaint();
                yield return null;
            }
            Assert.AreEqual(expected, _surface.TextField.value);
            yield return null;
        }

        private static T Read<T>(object target, string name) =>
            (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(target);

        private static void Write(object target, string name, object value) =>
            target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(target, value);
    }
}
#endif
