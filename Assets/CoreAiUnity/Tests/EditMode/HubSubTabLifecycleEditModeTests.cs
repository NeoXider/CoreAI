using System;
using System.Collections.Generic;
using CoreAI.Hub;
using CoreAI.Hub.UI;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace CoreAI.Tests.EditMode
{
    public sealed class HubSubTabLifecycleEditModeTests
    {
        [Test]
        public void SubTabPage_FirstSelection_ActivatesFirstChildOnly()
        {
            RecordingPage first = new("a", "A");
            RecordingPage second = new("b", "B");
            HubSubTabPage page = new("group", "Group", 0, first, second);

            page.CreatePageContent();

            CollectionAssert.AreEqual(new[] { "activated", "content" }, first.Events);
            CollectionAssert.IsEmpty(second.Events);
        }

        [Test]
        public void SubTabPage_RevisitingATab_StillFiresLifecycleOnTheRightChild()
        {
            RecordingPage first = new("a", "A");
            RecordingPage second = new("b", "B");
            HubSubTabPage page = new("group", "Group", 0, first, second);
            HubSubTabView view = (HubSubTabView)page.CreatePageContent();
            first.Events.Clear();
            second.Events.Clear();

            view.Select(1);
            view.Select(0);

            // The A → B → A round trip must deactivate/activate both children each way; content is cached,
            // so the second visit to A builds nothing but must still receive OnActivated.
            CollectionAssert.AreEqual(new[] { "deactivated", "activated" }, first.Events);
            CollectionAssert.AreEqual(new[] { "activated", "content", "deactivated" }, second.Events);
        }

        [Test]
        public void SubTabPage_ForwardsTopTabLifecycleToTheVisibleChild()
        {
            RecordingPage first = new("a", "A");
            RecordingPage second = new("b", "B");
            HubSubTabPage page = new("group", "Group", 0, first, second);
            HubSubTabView view = (HubSubTabView)page.CreatePageContent();
            view.Select(1);
            first.Events.Clear();
            second.Events.Clear();

            page.OnDeactivated();
            page.OnActivated();

            CollectionAssert.IsEmpty(first.Events);
            CollectionAssert.AreEqual(new[] { "deactivated", "activated" }, second.Events);
        }

        private sealed class RecordingPage : HubPageBase
        {
            public RecordingPage(string pageId, string displayName)
                : base(pageId, displayName, 0)
            {
            }

            public List<string> Events { get; } = new();

            public override Func<object> CreatePageContent => Build;

            public override void OnActivated()
            {
                Events.Add("activated");
            }

            public override void OnDeactivated()
            {
                Events.Add("deactivated");
            }

            private object Build()
            {
                Events.Add("content");
                return new Label(DisplayName);
            }
        }
    }
}
