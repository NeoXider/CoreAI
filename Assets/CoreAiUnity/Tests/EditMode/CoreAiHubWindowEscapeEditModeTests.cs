using System;
using System.Collections;
using System.Reflection;
using CoreAI.Hub;
using CoreAI.Hub.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode coverage for <see cref="CoreAiHubWindow"/>'s Escape/hotkey routing: the pure
    /// cursor-gating and "what should Escape do" decisions, plus the glue that asks the active page
    /// first via <see cref="IHubEscapeHandler"/> before collapsing the Hub.
    /// </summary>
    [TestFixture]
    public sealed class CoreAiHubWindowEscapeEditModeTests
    {
        [Test]
        public void IsHubInputAllowed_TruthTable()
        {
            Assert.IsTrue(CoreAiHubWindow.IsHubInputAllowed(false, true, CursorLockMode.None));
            Assert.IsTrue(CoreAiHubWindow.IsHubInputAllowed(false, false, CursorLockMode.Locked));

            Assert.IsTrue(CoreAiHubWindow.IsHubInputAllowed(true, true, CursorLockMode.None));
            Assert.IsTrue(CoreAiHubWindow.IsHubInputAllowed(true, true, CursorLockMode.Confined));
            Assert.IsFalse(CoreAiHubWindow.IsHubInputAllowed(true, true, CursorLockMode.Locked));
            Assert.IsFalse(CoreAiHubWindow.IsHubInputAllowed(true, false, CursorLockMode.None));
            Assert.IsFalse(CoreAiHubWindow.IsHubInputAllowed(true, false, CursorLockMode.Locked));
            Assert.IsFalse(CoreAiHubWindow.IsHubInputAllowed(true, false, CursorLockMode.Confined));
        }

        [Test]
        public void ShouldCollapseOnEscape_ActivePageHandles_DoesNotCollapse()
        {
            Assert.IsFalse(CoreAiHubWindow.ShouldCollapseOnEscape(
                true, true, true));
        }

        [Test]
        public void ShouldCollapseOnEscape_EscapeCollapsesDisabled_DoesNotCollapse()
        {
            Assert.IsFalse(CoreAiHubWindow.ShouldCollapseOnEscape(
                false, true, false));
        }

        [Test]
        public void ShouldCollapseOnEscape_NotExpanded_DoesNotCollapse()
        {
            Assert.IsFalse(CoreAiHubWindow.ShouldCollapseOnEscape(
                true, false, false));
        }

        [Test]
        public void ShouldCollapseOnEscape_ExpandedAndUnhandled_Collapses()
        {
            Assert.IsTrue(CoreAiHubWindow.ShouldCollapseOnEscape(
                true, true, false));
        }

        [Test]
        public void TryActivePageHandleEscape_PageImplementsHandler_ReturnsHandlerResultAndInvokesIt()
        {
            GameObject go = new("CoreAiHubWindow_TryActivePageHandleEscape_Test");
            try
            {
                go.AddComponent<UIDocument>();
                CoreAiHubWindow window = go.AddComponent<CoreAiHubWindow>();
                FakeEscapeHandlerPage page = new(true);
                RegisterActivePage(window, page);

                bool result = InvokePrivate<bool>(window, "TryActivePageHandleEscape");

                Assert.IsTrue(result);
                Assert.IsTrue(page.WasCalled);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void TryActivePageHandleEscape_PageWithoutHandler_ReturnsFalse()
        {
            GameObject go = new("CoreAiHubWindow_TryActivePageHandleEscape_NoHandler_Test");
            try
            {
                go.AddComponent<UIDocument>();
                CoreAiHubWindow window = go.AddComponent<CoreAiHubWindow>();
                RegisterActivePage(window, new PlainPage());

                bool result = InvokePrivate<bool>(window, "TryActivePageHandleEscape");

                Assert.IsFalse(result);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void HandleEscapeRequested_ActivePageHandlesEscape_DoesNotCollapseHub()
        {
            GameObject go = new("CoreAiHubWindow_HandleEscapeRequested_PageHandles_Test");
            try
            {
                go.AddComponent<UIDocument>();
                CoreAiHubWindow window = go.AddComponent<CoreAiHubWindow>();
                FakeEscapeHandlerPage page = new(true);
                RegisterActivePage(window, page);
                SetPrivateField(window, "requireVisibleCursor", false);
                SetPrivateField(window, "_uiReady", true);
                SetPrivateField(window, "_collapsed", false);

                InvokePrivate(window, "HandleEscapeRequested");

                Assert.IsTrue(page.WasCalled);
                Assert.IsFalse(GetPrivateField<bool>(window, "_collapsed"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void HandleEscapeRequested_NoPageHandler_CollapsesHub()
        {
            GameObject go = new("CoreAiHubWindow_HandleEscapeRequested_Collapses_Test");
            try
            {
                go.AddComponent<UIDocument>();
                CoreAiHubWindow window = go.AddComponent<CoreAiHubWindow>();
                RegisterActivePage(window, new PlainPage());
                SetPrivateField(window, "requireVisibleCursor", false);
                SetPrivateField(window, "_uiReady", true);
                SetPrivateField(window, "_collapsed", false);

                InvokePrivate(window, "HandleEscapeRequested");

                Assert.IsTrue(GetPrivateField<bool>(window, "_collapsed"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void HandleEscapeRequested_EscapeCollapsesDisabled_DoesNotCollapseHub()
        {
            GameObject go = new("CoreAiHubWindow_HandleEscapeRequested_Disabled_Test");
            try
            {
                go.AddComponent<UIDocument>();
                CoreAiHubWindow window = go.AddComponent<CoreAiHubWindow>();
                RegisterActivePage(window, new PlainPage());
                SetPrivateField(window, "requireVisibleCursor", false);
                SetPrivateField(window, "escapeCollapses", false);
                SetPrivateField(window, "_uiReady", true);
                SetPrivateField(window, "_collapsed", false);

                InvokePrivate(window, "HandleEscapeRequested");

                Assert.IsFalse(GetPrivateField<bool>(window, "_collapsed"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        private sealed class PlainPage : HubPageBase
        {
            public PlainPage() : base("plain", "Plain", 0)
            {
            }

            public override Func<object> CreatePageContent => () => null;
        }

        private sealed class FakeEscapeHandlerPage : HubPageBase, IHubEscapeHandler
        {
            private readonly bool _handlesEscape;

            public FakeEscapeHandlerPage(bool handlesEscape) : base("fake-escape", "Fake", 0)
            {
                _handlesEscape = handlesEscape;
            }

            public bool WasCalled { get; private set; }

            public override Func<object> CreatePageContent => () => null;

            public bool TryHandleEscape()
            {
                WasCalled = true;
                return _handlesEscape;
            }
        }

        private static void RegisterActivePage(CoreAiHubWindow window, IHubPage page)
        {
            FieldInfo pagesField =
                typeof(CoreAiHubWindow).GetField("_pages", BindingFlags.Instance | BindingFlags.NonPublic);
            IDictionary pages = (IDictionary)pagesField.GetValue(window);
            pages[page.PageId] = page;

            SetPrivateField(window, "_activePageId", page.PageId);
        }

        private static void SetPrivateField<T>(CoreAiHubWindow window, string fieldName, T value)
        {
            typeof(CoreAiHubWindow)
                .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(window, value);
        }

        private static T GetPrivateField<T>(CoreAiHubWindow window, string fieldName)
        {
            return (T)typeof(CoreAiHubWindow)
                .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(window);
        }

        private static void InvokePrivate(CoreAiHubWindow window, string methodName)
        {
            typeof(CoreAiHubWindow)
                .GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
                ?.Invoke(window, null);
        }

        private static T InvokePrivate<T>(CoreAiHubWindow window, string methodName)
        {
            return (T)typeof(CoreAiHubWindow)
                .GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
                ?.Invoke(window, null);
        }
    }
}
