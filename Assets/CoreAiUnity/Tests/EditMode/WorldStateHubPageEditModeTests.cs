using System;
using System.Collections.Generic;
using System.Reflection;
using CoreAI.Hub;
using CoreAI.Hub.UI;
using CoreAI.Infrastructure.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Covers the Hub event-subscription lifecycle fixes: <see cref="WorldStateHubPage"/> must not
    /// leak a <c>StateReset</c> handler per content rebuild (and must unsubscribe on
    /// <c>OnDestroyed</c>), and <see cref="CoreAiHubWindow"/> must re-subscribe to its registry when
    /// re-enabled after a disable.
    /// </summary>
    public sealed class WorldStateHubPageEditModeTests
    {
        private sealed class StubWorldStateManager : IWorldStateManager
        {
            private Action _stateReset;

            public int StateResetSubscriberCount { get; private set; }

            public bool HasSavedState => false;

            public bool WorldRestoreCompleted => true;

            public event Action StateReset
            {
                add
                {
                    _stateReset += value;
                    StateResetSubscriberCount++;
                }
                remove
                {
                    _stateReset -= value;
                    StateResetSubscriberCount--;
                }
            }

            public event Action RestoreCompleted
            {
                add { }
                remove { }
            }

            public void Save()
            {
            }

            public bool TryLoad(string sceneName = null)
            {
                return false;
            }

            public void Reset()
            {
                _stateReset?.Invoke();
            }

            public void StartAutoSave(float intervalSeconds)
            {
            }

            public void ConfirmDurability(Action<bool> onConfirmed)
            {
                onConfirmed?.Invoke(true);
            }
        }

        [Test]
        public void CreatePageContent_CalledRepeatedly_SubscribesToStateResetOnlyOnce()
        {
            StubWorldStateManager manager = new();
            WorldStateHubPage page = new(manager);

            page.CreatePageContent();
            page.CreatePageContent();
            page.CreatePageContent();

            Assert.AreEqual(1, manager.StateResetSubscriberCount,
                "Rebuilding the page content must not add another StateReset handler each time.");
        }

        [Test]
        public void OnDestroyed_UnsubscribesFromStateReset()
        {
            StubWorldStateManager manager = new();
            WorldStateHubPage page = new(manager);
            page.CreatePageContent();
            Assert.AreEqual(1, manager.StateResetSubscriberCount);

            page.OnDestroyed();

            Assert.AreEqual(0, manager.StateResetSubscriberCount,
                "Page teardown must release the StateReset handler so the DI-singleton manager " +
                "does not pin the dead VisualElement tree.");
        }

        [Test]
        public void OnDestroyed_ThenRebuilt_SubscribesAgainExactlyOnce()
        {
            StubWorldStateManager manager = new();
            WorldStateHubPage page = new(manager);
            page.CreatePageContent();
            page.OnDestroyed();

            page.CreatePageContent();

            Assert.AreEqual(1, manager.StateResetSubscriberCount,
                "A page rebuilt after teardown must re-subscribe exactly once.");
        }

        private sealed class TestHubWindow : CoreAiHubWindow
        {
            public void InvokeOnEnable()
            {
                OnEnable();
            }

            public void InvokeOnDisable()
            {
                OnDisable();
            }
        }

        private sealed class ThrowingDeactivationPage : IHubPage
        {
            public string PageId => "throwing";

            public string DisplayName => "Throwing";

            public int Order => 0;

            public Func<object> CreatePageContent => () => new VisualElement();

            public bool Destroyed { get; private set; }

            public void OnActivated()
            {
            }

            public void OnDeactivated()
            {
                throw new InvalidOperationException("Expected deactivation failure.");
            }

            public void OnDestroyed()
            {
                Destroyed = true;
            }
        }

        [Test]
        public void HubWindow_OnEnableAfterOnDisable_ResubscribesToTheSameRegistry()
        {
            GameObject go = new("hub-window-lifecycle-test");
            // WHY: keep the GameObject inactive so Unity never runs the MonoBehaviour callbacks on
            // its own — the test drives OnEnable/OnDisable explicitly for a deterministic order.
            go.SetActive(false);
            try
            {
                go.AddComponent<UIDocument>();
                TestHubWindow window = go.AddComponent<TestHubWindow>();
                HubPageRegistry registry = new();

                window.Registry = registry;
                Assert.IsFalse(IsSubscribed(registry, window),
                    "Assigning Registry while disabled must defer subscription until OnEnable.");

                window.InvokeOnDisable();
                Assert.IsFalse(IsSubscribed(registry, window), "OnDisable should unsubscribe.");

                window.InvokeOnEnable();
                Assert.IsTrue(IsSubscribed(registry, window),
                    "OnEnable must re-subscribe: the Registry setter's ReferenceEquals guard skips " +
                    "re-wiring, so a hidden+re-shown window would never see new tabs otherwise.");

                window.InvokeOnEnable();
                Assert.AreEqual(1, SubscriptionCount(registry, window),
                    "A repeated OnEnable must not double-subscribe the same handler.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void HubWindow_RegistryAssignedWhileDisabled_DestroyLeavesNoRegistryHandler()
        {
            GameObject go = new("hub-window-disabled-destroy-test");
            go.SetActive(false);
            HubPageRegistry registry = new();
            TestHubWindow window = null;
            try
            {
                go.AddComponent<UIDocument>();
                window = go.AddComponent<TestHubWindow>();
                window.Registry = registry;

                Assert.IsFalse(IsSubscribed(registry, window));
                UnityEngine.Object.DestroyImmediate(go);
                go = null;

                Assert.IsFalse(IsSubscribed(registry, window),
                    "Destroying a disabled window must not leave a registry event handler.");
            }
            finally
            {
                if (go != null)
                {
                    UnityEngine.Object.DestroyImmediate(go);
                }
            }
        }

        [Test]
        public void HubWindow_DestroyPage_WhenOnDeactivatedThrows_StillCallsOnDestroyed()
        {
            GameObject go = new("hub-window-page-teardown-test");
            go.SetActive(false);
            try
            {
                go.AddComponent<UIDocument>();
                TestHubWindow window = go.AddComponent<TestHubWindow>();
                ThrowingDeactivationPage page = new();
                FieldInfo pagesField = typeof(CoreAiHubWindow).GetField(
                    "_pages", BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo activePageField = typeof(CoreAiHubWindow).GetField(
                    "_activePageId", BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo destroyPage = typeof(CoreAiHubWindow).GetMethod(
                    "DestroyPage", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(pagesField);
                Assert.IsNotNull(activePageField);
                Assert.IsNotNull(destroyPage);
                Dictionary<string, IHubPage> pages =
                    (Dictionary<string, IHubPage>)pagesField.GetValue(window);
                pages.Add(page.PageId, page);
                activePageField.SetValue(window, page.PageId);
                LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(
                    "Expected deactivation failure\\."));

                destroyPage.Invoke(window, new object[] { page.PageId });

                Assert.IsTrue(page.Destroyed, "OnDestroyed must run even when OnDeactivated throws.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        private static bool IsSubscribed(HubPageRegistry registry, object target)
        {
            return SubscriptionCount(registry, target) > 0;
        }

        /// <summary>
        /// Counts registry <c>PageRegistered</c> handlers bound to <paramref name="target"/> via the
        /// compiler-generated event backing field (the event exposes no public subscriber list).
        /// </summary>
        private static int SubscriptionCount(HubPageRegistry registry, object target)
        {
            FieldInfo field = typeof(HubPageRegistry).GetField(
                "PageRegistered", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, "HubPageRegistry.PageRegistered backing field not found.");

            if (field.GetValue(registry) is not Delegate handler)
            {
                return 0;
            }

            int count = 0;
            foreach (Delegate d in handler.GetInvocationList())
            {
                if (ReferenceEquals(d.Target, target))
                {
                    count++;
                }
            }

            return count;
        }
    }
}
