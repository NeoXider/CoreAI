using System;
using System.Collections.Generic;
using CoreAI.Hub;
using UnityEngine.UIElements;

namespace CoreAI.Hub.UI
{
    /// <summary>
    /// A top-level <see cref="IHubPage"/> that groups several child pages under one tab as sub-tabs, via
    /// <see cref="HubSubTabView"/>. Each child's <see cref="IHubPage.CreatePageContent"/> becomes a sub-tab
    /// content factory, and the active child receives <see cref="IHubPage.OnActivated"/> /
    /// <see cref="IHubPage.OnDeactivated"/> so pages that subscribe to live data (e.g. the settings page)
    /// keep working unchanged inside a sub-tab.
    /// <para>
    /// This is the reusable way to add sub-tabs: register ONE <see cref="HubSubTabPage"/> instead of N
    /// separate top-level pages, e.g.
    /// <c>new HubSubTabPage("coreai.hub.aisettings", "AI Settings", 100, settingsPage, tokenBudgetPage, statsPage)</c>.
    /// </para>
    /// </summary>
    public sealed class HubSubTabPage : HubPageBase
    {
        private readonly List<IHubPage> _children = new();
        private HubSubTabView _view;
        private IHubPage _activeChild;

        /// <summary>Creates a grouping page from its ordered child pages (first child shown by default).</summary>
        public HubSubTabPage(string pageId, string displayName, int order, params IHubPage[] children)
            : base(pageId, displayName, order)
        {
            if (children != null)
            {
                foreach (IHubPage child in children)
                {
                    if (child != null)
                    {
                        _children.Add(child);
                    }
                }
            }
        }

        /// <inheritdoc />
        public override Func<object> CreatePageContent => BuildContent;

        private object BuildContent()
        {
            List<HubSubTabView.SubTab> tabs = new();
            foreach (IHubPage child in _children)
            {
                IHubPage captured = child;
                tabs.Add(new HubSubTabView.SubTab(child.DisplayName, () => BuildChild(captured)));
            }

            _view = new HubSubTabView(tabs);
            return _view;
        }

        private VisualElement BuildChild(IHubPage child)
        {
            // WHY: mirror the host's per-page lifecycle at the sub-tab level so a child that subscribes to
            // live data on activation still gets its callback. The previously-active child is deactivated
            // first; sub-tab content is cached by HubSubTabView, so each child builds at most once.
            if (!ReferenceEquals(_activeChild, child))
            {
                _activeChild?.OnDeactivated();
                _activeChild = child;
                child.OnActivated();
            }

            object content = child.CreatePageContent != null ? child.CreatePageContent() : null;
            return content as VisualElement ?? new Label($"'{child.DisplayName}' produced no UI Toolkit content.");
        }

        /// <inheritdoc />
        public override void OnActivated()
        {
            _activeChild?.OnActivated();
        }

        /// <inheritdoc />
        public override void OnDeactivated()
        {
            _activeChild?.OnDeactivated();
        }

        /// <inheritdoc />
        public override void OnDestroyed()
        {
            foreach (IHubPage child in _children)
            {
                child.OnDestroyed();
            }
        }
    }
}
