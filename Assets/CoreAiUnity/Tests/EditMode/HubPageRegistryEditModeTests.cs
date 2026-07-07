using System;
using System.Collections.Generic;
using CoreAI.Hub;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    public sealed class HubPageRegistryEditModeTests
    {
        [Test]
        public void Register_ListContainsPage()
        {
            HubPageRegistry registry = new();

            registry.Register("settings", () => new TestHubPage("settings", "Settings", 20), 20);

            IReadOnlyList<(string pageId, int order)> pages = registry.List();
            Assert.AreEqual(1, pages.Count);
            Assert.AreEqual("settings", pages[0].pageId);
            Assert.AreEqual(20, pages[0].order);
        }

        [Test]
        public void Register_SameId_ReplacesFactoryAndKeepsSingleEntry()
        {
            HubPageRegistry registry = new();
            TestHubPage first = new("chat", "Chat v1", 10);
            TestHubPage second = new("chat", "Chat v2", 5);

            registry.Register("chat", () => first, 10);
            registry.Register("chat", () => second, 5);

            Assert.IsTrue(registry.TryGet("chat", out Func<IHubPage> factory));
            Assert.AreSame(second, factory());
            IReadOnlyList<(string pageId, int order)> pages = registry.List();
            Assert.AreEqual(1, pages.Count);
            Assert.AreEqual("chat", pages[0].pageId);
            Assert.AreEqual(5, pages[0].order);
        }

        [Test]
        public void Unregister_RemovesPageAndFiresEvent()
        {
            HubPageRegistry registry = new();
            List<string> unregistered = new();
            registry.PageUnregistered += unregistered.Add;
            registry.Register("mods", () => new TestHubPage("mods", "Mods", 30), 30);

            bool removed = registry.Unregister("mods");

            Assert.IsTrue(removed);
            Assert.IsFalse(registry.TryGet("mods", out _));
            Assert.AreEqual(0, registry.List().Count);
            CollectionAssert.AreEqual(new[] { "mods" }, unregistered);
        }

        [Test]
        public void List_OrdersByOrderThenPageId()
        {
            HubPageRegistry registry = new();
            registry.Register("zeta", () => new TestHubPage("zeta", "Zeta", 20), 20);
            registry.Register("alpha", () => new TestHubPage("alpha", "Alpha", 10), 10);
            registry.Register("beta", () => new TestHubPage("beta", "Beta", 10), 10);

            IReadOnlyList<(string pageId, int order)> pages = registry.List();

            CollectionAssert.AreEqual(new[] { "alpha", "beta", "zeta" },
                new[] { pages[0].pageId, pages[1].pageId, pages[2].pageId });
        }

        [Test]
        public void Register_FiresEventForRegisterAndReplace()
        {
            HubPageRegistry registry = new();
            List<string> registered = new();
            registry.PageRegistered += registered.Add;

            registry.Register("chat", () => new TestHubPage("chat", "Chat", 0));
            registry.Register("chat", () => new TestHubPage("chat", "Chat 2", 1), 1);

            CollectionAssert.AreEqual(new[] { "chat", "chat" }, registered);
        }

        private sealed class TestHubPage : IHubPage
        {
            public TestHubPage(string pageId, string displayName, int order)
            {
                PageId = pageId;
                DisplayName = displayName;
                Order = order;
            }

            public string PageId { get; }
            public string DisplayName { get; }
            public int Order { get; }
            public Func<object> CreatePageContent => () => new object();

            public void OnActivated()
            {
            }

            public void OnDeactivated()
            {
            }

            public void OnDestroyed()
            {
            }
        }
    }
}
