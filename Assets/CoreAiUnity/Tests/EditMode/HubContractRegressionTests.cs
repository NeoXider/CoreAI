using System;
using System.Collections;
using System.Reflection;
using CoreAI.Hub;
using CoreAI.Hub.UI;
using CoreAI.Infrastructure.World;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace CoreAI.Tests.EditMode
{
    /// <summary>Hub view ownership, keyboard input, and operation-receipt regressions.</summary>
    public sealed class HubContractRegressionTests
    {
        private sealed class PanelHost : EditorWindow
        {
        }

        private sealed class TestWindow : CoreAiHubWindow
        {
            public void EnableView() => OnEnable();
            public void DisableView() => OnDisable();
            public void DestroyView() => OnDestroy();
        }

        private sealed class RecordingPage : HubPageBase
        {
            public RecordingPage(string id) : base(id, id, 0)
            {
            }

            public int Builds { get; private set; }
            public int Activations { get; private set; }
            public int Deactivations { get; private set; }
            public int Destructions { get; private set; }
            public override Func<object> CreatePageContent => () =>
            {
                Builds++;
                return new Label(PageId) { name = "content-" + PageId };
            };
            public override void OnActivated() => Activations++;
            public override void OnDeactivated() => Deactivations++;
            public override void OnDestroyed() => Destructions++;
        }

        private sealed class WindowHarness : IDisposable
        {
            private readonly GameObject _owner;
            public PanelHost Host { get; }
            public TestWindow Window { get; }

            public WindowHarness(HubPageRegistry registry)
            {
                Host = ScriptableObject.CreateInstance<PanelHost>();
                Host.Show();
                _owner = new GameObject("HubContractRegression");
                _owner.SetActive(false);
                _owner.AddComponent<UIDocument>();
                Window = _owner.AddComponent<TestWindow>();
                string shellPath = "Assets/CoreAIHub/Runtime/CoreAiHub.uxml";
                UnityEditor.PackageManager.PackageInfo package =
                    UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(CoreAiHubWindow).Assembly);
                if (package != null)
                {
                    shellPath = package.assetPath + "/Runtime/CoreAiHub.uxml";
                }

                VisualTreeAsset shell = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(shellPath);
                Assert.IsNotNull(shell, "The Hub test requires the installed shell resource.");
                Set(Window, "shellUxml", shell);
                Set(Window, "requireVisibleCursor", false);
                Window.Registry = registry;
                Resume();
            }

            public void Resume()
            {
                Window.EnableView();
                Invoke(Window, "Rebuild", Host.rootVisualElement);
            }

            public void Dispose()
            {
                Window.DestroyView();
                UnityEngine.Object.DestroyImmediate(_owner);
                UnityEngine.Object.DestroyImmediate(Host);
            }
        }

        [Test]
        public void Window_DisableReenable_PreservesPageAndDraftContentUntilFinalDestruction()
        {
            HubPageRegistry registry = new();
            RecordingPage page = new("draft");
            registry.Register("draft", () => page);
            using (WindowHarness harness = new(registry))
            {
                Label content = harness.Host.rootVisualElement.Q<Label>("content-draft");
                content.text = "unsaved draft";
                harness.Window.DisableView();
                Assert.AreEqual(0, page.Destructions);
                Assert.AreEqual(1, page.Deactivations);

                harness.Resume();

                Assert.AreSame(content, harness.Host.rootVisualElement.Q<Label>("content-draft"));
                Assert.AreEqual("unsaved draft", content.text);
                Assert.AreEqual(1, page.Builds);
                Assert.AreEqual(2, page.Activations);
            }

            Assert.AreEqual(1, page.Destructions);
        }

        [UnityTest]
        public IEnumerator Window_EscapeOptOut_DoesNotCollapseAndKeepsFocus()
        {
            HubPageRegistry registry = new();
            registry.Register("first", () => new RecordingPage("first"), 0);
            using (WindowHarness harness = new(registry))
            {
                VisualElement root = harness.Host.rootVisualElement;
                Button tab = root.Q<Button>("coreai-hub-tab-first");
                Assert.IsNotNull(tab);
                tab.Focus();
                FieldInfo escapeField = typeof(CoreAiHubWindow).GetField(
                    "escapeCollapses", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(escapeField);
                escapeField.SetValue(harness.Window, false);

                Assert.IsFalse(Get<bool>(harness.Window, "_collapsed"));
                Focusable focusedBefore = root.focusController.focusedElement;
                SendKey(tab, KeyCode.Escape);

                Assert.IsFalse(Get<bool>(harness.Window, "_collapsed"),
                    "escapeCollapses=false must not collapse the Hub.");
                Assert.AreSame(focusedBefore, root.focusController.focusedElement,
                    "Escape must remain available to the focused element.");
                yield return null;
            }
        }

        /// <summary>
        /// PR-09-002 through the real root KeyDown handler: an Escape the Hub does not handle must keep
        /// bubbling to the host, and an Escape that collapses the Hub must stop there.
        /// <para>
        /// WHY an ancestor observer rather than the handler's return value: the original defect consumed
        /// Escape in <c>OnRootKeyDown</c> even when nothing handled it, and a test of the private handler's
        /// result stays green if the consume moves back into the event callback.
        /// </para>
        /// </summary>
        [Test]
        public void Window_RootKeyDown_ConsumesEscapeOnlyWhenTheHubHandlesIt([Values(false, true)] bool collapses)
        {
            HubPageRegistry registry = new();
            registry.Register("first", () => new RecordingPage("first"), 0);
            using (WindowHarness harness = new(registry))
            {
                VisualElement host = harness.Host.rootVisualElement;
                Button tab = host.Q<Button>("coreai-hub-tab-first");
                Assert.IsNotNull(tab);
                tab.Focus();
                Set(harness.Window, "escapeCollapses", collapses);
                int bubbledToHost = 0;
                EventCallback<KeyDownEvent> observer = evt =>
                {
                    if (evt.keyCode == KeyCode.Escape)
                    {
                        bubbledToHost++;
                    }
                };
                host.RegisterCallback(observer);
                try
                {
                    SendKey(tab, KeyCode.Escape);
                }
                finally
                {
                    host.UnregisterCallback(observer);
                }

                Assert.AreEqual(collapses, Get<bool>(harness.Window, "_collapsed"));
                Assert.AreEqual(collapses ? 0 : 1, bubbledToHost,
                    collapses
                        ? "An Escape that collapsed the Hub is handled and must not reach the host as well."
                        : "An Escape the Hub did not handle must stay available to the host and focused UI.");
            }
        }

        [UnityTest]
        public IEnumerator Window_RegistrySwapWithSameId_FencesAlreadyQueuedOldNotification()
        {
            HubPageRegistry first = new();
            HubPageRegistry second = new();
            RecordingPage oldPage = new("shared");
            RecordingPage newPage = new("shared");
            first.Register("shared", () => oldPage);
            second.Register("shared", () => newPage);
            using (WindowHarness harness = new(first))
            {
                first.Register("shared", () => new RecordingPage("shared"));
                harness.Window.Registry = second;
                yield return null;
                yield return null;

                Assert.AreEqual(1, oldPage.Deactivations);
                Assert.AreEqual(1, oldPage.Destructions);
                Assert.AreEqual(1, newPage.Builds);
                Assert.AreEqual(1, newPage.Activations);
                Assert.AreEqual(0, newPage.Destructions,
                    "A notification queued by the old registry must not destroy its replacement.");
                Assert.AreEqual("shared", harness.Window.ActivePageId);
            }
        }

        [UnityTest]
        public IEnumerator Window_KeyboardMovesTabs_AndEscapeThenSubmitReopensWithRestoredFocus()
        {
            HubPageRegistry registry = new();
            registry.Register("first", () => new RecordingPage("first"), 0);
            registry.Register("second", () => new RecordingPage("second"), 1);
            using (WindowHarness harness = new(registry))
            {
                VisualElement root = harness.Host.rootVisualElement;
                Button first = root.Q<Button>("coreai-hub-tab-first");
                Button second = root.Q<Button>("coreai-hub-tab-second");
                Assert.IsTrue(first.focusable);
                first.Focus();
                SendKey(first, KeyCode.RightArrow);
                Assert.AreEqual("second", harness.Window.ActivePageId);
                Assert.AreSame(second, root.focusController.focusedElement);

                SendKey(second, KeyCode.Escape);
                Button toggle = root.Q<Button>("coreai-hub-collapse");
                Assert.IsTrue(Get<bool>(harness.Window, "_collapsed"));
                Assert.IsTrue(toggle.focusable);
                Assert.AreEqual("Expand CoreAI Hub", toggle.tooltip);
                Assert.AreSame(toggle, root.focusController.focusedElement);
                using (NavigationSubmitEvent submit = NavigationSubmitEvent.GetPooled())
                {
                    toggle.SendEvent(submit);
                }

                yield return null;
                Assert.IsFalse(Get<bool>(harness.Window, "_collapsed"));
                Assert.AreSame(second, root.focusController.focusedElement);
                Assert.AreEqual("Collapse CoreAI Hub", toggle.tooltip);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void WorldPage_LegacyManager_CannotPromoteGlobalArmingToAnOperationReceipt(bool reset)
        {
            LegacyManager manager = new();
            WorldStateHubPage page = new(manager);
            VisualElement root = (VisualElement)page.CreatePageContent();
            try
            {
                Invoke(page, reset ? "OnResetClicked" : "OnSaveClicked");
                Assert.AreEqual(1, manager.WriteCalls);
                Assert.AreEqual(0, manager.ConfirmationCalls);
                StringAssert.Contains("unconfirmed", root.Q<Label>("coreai-hub-worldstate-durability").text);
            }
            finally
            {
                page.OnDestroyed();
            }
        }

        [TestCase(WorldStateWriteStatus.Failed, true, "unconfirmed")]
        [TestCase(WorldStateWriteStatus.Cancelled, true, "cancelled")]
        [TestCase(WorldStateWriteStatus.Unsupported, true, "unconfirmed")]
        [TestCase(WorldStateWriteStatus.Succeeded, false, "NOT armed")]
        [TestCase(WorldStateWriteStatus.Succeeded, true, "Browser completion is not confirmed")]
        public void WorldPage_UsesThisOperationsReceipt_WithoutClaimingBrowserCommit(
            WorldStateWriteStatus status, bool armed, string expected)
        {
            ReceiptManager manager = new()
            {
                Result = new WorldStateWriteResult(status, armed, "test receipt")
            };
            WorldStateHubPage page = new(manager);
            VisualElement root = (VisualElement)page.CreatePageContent();
            try
            {
                Invoke(page, "OnSaveClicked");
                string text = root.Q<Label>("coreai-hub-worldstate-durability").text;
                StringAssert.Contains(expected, text);
                StringAssert.DoesNotContain("survives a reload", text);
                Assert.AreEqual(1, manager.ReceiptCalls);
                Assert.AreEqual(0, manager.WriteCalls);
                Assert.AreEqual(0, manager.ConfirmationCalls);
                Assert.IsTrue(root.Q<Button>("coreai-hub-worldstate-save").enabledSelf);
            }
            finally
            {
                page.OnDestroyed();
            }
        }

        [Test]
        public void WorldPage_LocalExceptionOrCancellation_RemainsRetryableAndNeverCallsGlobalArming()
        {
            ReceiptManager manager = new() { Failure = new InvalidOperationException("write refused") };
            WorldStateHubPage page = new(manager);
            VisualElement root = (VisualElement)page.CreatePageContent();
            try
            {
                Invoke(page, "OnResetClicked");
                StringAssert.Contains("write refused", root.Q<Label>("coreai-hub-worldstate-durability").text);
                manager.Failure = new OperationCanceledException();
                Invoke(page, "OnSaveClicked");
                StringAssert.Contains("cancelled", root.Q<Label>("coreai-hub-worldstate-durability").text);
                manager.Failure = null;
                manager.Result = new WorldStateWriteResult(WorldStateWriteStatus.Succeeded, true);
                Invoke(page, "OnSaveClicked");
                Assert.AreEqual(WorldStateHubPage.FlushConfirmedText,
                    root.Q<Label>("coreai-hub-worldstate-durability").text);
                Assert.AreEqual(0, manager.ConfirmationCalls);
            }
            finally
            {
                page.OnDestroyed();
            }
        }

        [TestCase(null, "Assets/CoreAIHub/Runtime/CoreAiHub.uxml")]
        [TestCase("Packages/com.neoxider.coreaihub", "Packages/com.neoxider.coreaihub/Runtime/CoreAiHub.uxml")]
        public void Setup_ResourceResolutionSupportsSourceAndInstalledUpmPaths(string packagePath, string expected)
        {
            Type menu = Type.GetType("CoreAI.Hub.UI.Editor.CoreAiHubSetupMenu, CoreAI.Hub.UI.Editor", true);
            MethodInfo resolve = menu.GetMethod("ResolveSourcePath", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.AreEqual(expected, resolve.Invoke(null, new object[]
            {
                packagePath, "Assets/CoreAIHub", "Runtime/CoreAiHub.uxml"
            }));
        }

        [Test]
        public void Setup_BuildHub_ComposesTheCoreRuntimeComponents()
        {
            Type menu = Type.GetType("CoreAI.Hub.UI.Editor.CoreAiHubSetupMenu, CoreAI.Hub.UI.Editor", true);
            MethodInfo build = menu.GetMethod("BuildHub", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(build, "BuildHub");

            GameObject authored = (GameObject)build.Invoke(null, null);
            try
            {
                Assert.IsNotNull(authored.GetComponent<UIDocument>(), "UIDocument");
                Assert.IsNotNull(authored.GetComponent<CoreAiHubWindow>(), "CoreAiHubWindow");
                Assert.IsNotNull(authored.GetComponent<CoreAiHubDemo>(), "CoreAiHubDemo");
                Type worldBinder = Type.GetType("CoreAI.Ai.Hub.WorldStateHubBinder, CoreAI.Mods.Hub", false);
                if (worldBinder != null)
                {
                    Assert.IsNotNull(authored.GetComponent(worldBinder),
                        "The setup-created Hub must bind the available runtime world-state page.");
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(authored);
            }
        }

        private class LegacyManager : IWorldStateManager
        {
            public int WriteCalls { get; private set; }
            public int ConfirmationCalls { get; private set; }
            public bool HasSavedState => false;
            public bool WorldRestoreCompleted => true;
            public event Action StateReset { add { } remove { } }
            public event Action RestoreCompleted { add { } remove { } }
            public void Save() => WriteCalls++;
            public void Reset() => WriteCalls++;
            public bool TryLoad(string sceneName = null) => false;
            public void StartAutoSave(float intervalSeconds) { }
            public void ConfirmDurability(Action<bool> onConfirmed)
            {
                ConfirmationCalls++;
                onConfirmed(true);
            }
        }

        private sealed class ReceiptManager : LegacyManager, IWorldStateWriteResults
        {
            public WorldStateWriteResult Result { get; set; }
            public Exception Failure { get; set; }
            public int ReceiptCalls { get; private set; }
            public WorldStateWriteResult SaveWithResult() => WriteReceipt();
            public WorldStateWriteResult ResetWithResult() => WriteReceipt();
            private WorldStateWriteResult WriteReceipt()
            {
                ReceiptCalls++;
                if (Failure != null)
                {
                    throw Failure;
                }

                return Result;
            }
        }

        private static void SendKey(VisualElement target, KeyCode key)
        {
            using (KeyDownEvent input = KeyDownEvent.GetPooled(new Event { type = EventType.KeyDown, keyCode = key }))
            {
                target.SendEvent(input);
            }
        }

        private static void Set(object target, string name, object value)
        {
            FieldInfo field = typeof(CoreAiHubWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, name);
            field.SetValue(target, value);
        }

        private static T Get<T>(object target, string name)
        {
            return (T)typeof(CoreAiHubWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
        }

        private static void Invoke(object target, string name, params object[] arguments)
        {
            Type owner = target is CoreAiHubWindow ? typeof(CoreAiHubWindow) : target.GetType();
            MethodInfo method = owner.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(method, name);
            method.Invoke(target, arguments);
        }
    }
}
