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

        [Test]
        public void OwnedRegistration_ReplacementAndRemovalReleaseOnlyTheirOwnLifetimeOnce()
        {
            HubPageRegistry registry = new();
            CountingLifetime first = new();
            CountingLifetime second = new();
            Func<IHubPage> firstFactory = () => new TestHubPage("mods", "First", 0);
            Func<IHubPage> secondFactory = () => new TestHubPage("mods", "Second", 0);
            registry.RegisterOwned("mods", firstFactory, first);
            registry.RegisterOwned("mods", secondFactory, second);

            Assert.AreEqual(1, first.Disposals);
            Assert.AreEqual(0, second.Disposals);
            Assert.IsFalse(registry.Unregister("mods", firstFactory),
                "An old binder cannot remove a replacement registration.");
            Assert.IsTrue(registry.TryGet("mods", out Func<IHubPage> retained));
            Assert.AreSame(secondFactory, retained);
            Assert.AreEqual(0, second.Disposals);
            Assert.IsTrue(registry.Unregister("mods", secondFactory));
            Assert.IsFalse(registry.Unregister("mods", secondFactory));
            Assert.AreEqual(1, second.Disposals);
        }

        [Test]
        public void OwnedRegistration_TransferringSameLifetimeDoesNotDisposeItPrematurely()
        {
            HubPageRegistry registry = new();
            CountingLifetime lifetime = new();
            registry.RegisterOwned("mods", () => new TestHubPage("mods", "Old", 0), lifetime);
            registry.RegisterOwned("mods", () => new TestHubPage("mods", "New", 0), lifetime);

            Assert.AreEqual(0, lifetime.Disposals);
            registry.Unregister("mods");
            Assert.AreEqual(1, lifetime.Disposals);
        }

        [Test]
        public void OwnedRegistration_DisposalFailureStillPublishesTheReplacementAndRemoval()
        {
            HubPageRegistry registry = new();
            CountingLifetime first = new() { Failure = new InvalidOperationException("dispose refused") };
            CountingLifetime second = new() { Failure = new InvalidOperationException("dispose refused") };
            registry.RegisterOwned("mods", () => new TestHubPage("mods", "Old", 0), first);
            int replacements = 0;
            int removals = 0;
            registry.PageRegistered += _ => replacements++;
            registry.PageUnregistered += _ => removals++;
            Func<IHubPage> replacement = () => new TestHubPage("mods", "New", 0);

            Assert.Throws<InvalidOperationException>(() => registry.RegisterOwned("mods", replacement, second));
            Assert.IsTrue(registry.TryGet("mods", out Func<IHubPage> actual));
            Assert.AreSame(replacement, actual);
            Assert.AreEqual(1, replacements);
            Assert.Throws<InvalidOperationException>(() => registry.Unregister("mods", replacement));
            Assert.AreEqual(1, removals);
            Assert.IsFalse(registry.TryGet("mods", out _));
            Assert.AreEqual(1, first.Disposals);
            Assert.AreEqual(1, second.Disposals);
        }

        [Test]
        public void OrdinaryRegistration_DoesNotAcquireOrDisposeThePageInstancesService()
        {
            HubPageRegistry registry = new();
            TestHubPage page = new("shared", "Shared", 0);
            registry.Register("shared", () => page);
            registry.Unregister("shared");
            Assert.AreEqual(0, page.Destructions,
                "The registry owns explicit registration lifetimes; windows own resolved page lifetimes.");
        }

        private sealed class CountingLifetime : IDisposable
        {
            public int Disposals { get; private set; }
            public Exception Failure { get; set; }
            public void Dispose()
            {
                Disposals++;
                if (Failure != null)
                {
                    throw Failure;
                }
            }
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
            public int Destructions { get; private set; }
            public Func<object> CreatePageContent => () => new object();

            public void OnActivated()
            {
            }

            public void OnDeactivated()
            {
            }

            public void OnDestroyed()
            {
                Destructions++;
            }
        }
    }
}
