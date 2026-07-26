using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.Hub.UI
{
    /// <summary>
    /// Reusable secondary-navigation container: a row of sub-tab pills over a content host, so any
    /// top-level Hub page can host several sub-pages without each one needing its own top tab. Content is
    /// built lazily on first selection and cached, and each sub-tab's <see cref="SubTab.Content"/> factory
    /// returns a fresh <see cref="VisualElement"/> (UI Toolkit hosts return VisualElements from page
    /// content, so this stays framework-consistent with <c>IHubPage.CreatePageContent</c>).
    /// <para>
    /// Usage: return <c>new HubSubTabView(new[] { new HubSubTabView.SubTab("Settings", BuildSettings), ... })</c>
    /// from a page's <c>CreatePageContent</c>. The sub-tab bar reuses the Hub tab USS classes so it reads as
    /// the same design one level down.
    /// </para>
    /// </summary>
    public sealed class HubSubTabView : VisualElement
    {
        /// <summary>One sub-tab: a display title and a lazy content factory (built once, then cached).</summary>
        public readonly struct SubTab
        {
            public SubTab(string title, Func<VisualElement> content)
            {
                Title = title;
                Content = content;
            }

            /// <summary>Pill label.</summary>
            public string Title { get; }

            /// <summary>Builds this sub-tab's content; invoked at most once and cached.</summary>
            public Func<VisualElement> Content { get; }
        }

        private readonly List<SubTab> _tabs = new();
        private readonly List<Button> _buttons = new();
        private readonly VisualElement _contentHost;
        private readonly VisualElement _bar;
        private readonly Dictionary<int, VisualElement> _cache = new();
        private int _active = -1;

        /// <summary>
        /// Raised with the newly selected index whenever the active sub-tab changes, including when the
        /// content was already built and served from the cache. Hosts use it to run per-sub-tab lifecycle
        /// (activate/deactivate) independently of content construction.
        /// </summary>
        public event Action<int> SelectionChanged;

        /// <summary>Creates the view from an ordered set of sub-tabs; the first is selected by default.</summary>
        public HubSubTabView(IEnumerable<SubTab> tabs)
        {
            style.flexGrow = 1f;

            _bar = new VisualElement { name = "coreai-hub-subtabbar" };
            _bar.style.flexDirection = FlexDirection.Row;
            _bar.style.flexWrap = Wrap.Wrap;
            _bar.style.marginBottom = 8f;
            Add(_bar);

            _contentHost = new VisualElement { name = "coreai-hub-subtab-content" };
            _contentHost.style.flexGrow = 1f;
            Add(_contentHost);

            if (tabs != null)
            {
                foreach (SubTab tab in tabs)
                {
                    AddTab(tab);
                }
            }

            if (_tabs.Count > 0)
            {
                Select(0);
            }
        }

        /// <summary>Selects a sub-tab by its title (case-sensitive); no-op when not found.</summary>
        public void Select(string title)
        {
            for (int i = 0; i < _tabs.Count; i++)
            {
                if (string.Equals(_tabs[i].Title, title, StringComparison.Ordinal))
                {
                    Select(i);
                    return;
                }
            }
        }

        /// <summary>Selects a sub-tab by index; builds its content on first activation and caches it.</summary>
        public void Select(int index)
        {
            if (index < 0 || index >= _tabs.Count || index == _active)
            {
                return;
            }

            _active = index;
            for (int i = 0; i < _buttons.Count; i++)
            {
                _buttons[i].EnableInClassList("coreai-hub-tab-active", i == index);
            }

            SelectionChanged?.Invoke(index);

            if (!_cache.TryGetValue(index, out VisualElement content))
            {
                content = SafeBuild(_tabs[index]);
                _cache[index] = content;
            }

            _contentHost.Clear();
            if (content != null)
            {
                _contentHost.Add(content);
            }
        }

        private void AddTab(SubTab tab)
        {
            int index = _tabs.Count;
            _tabs.Add(tab);

            Button button = new(() => Select(index)) { text = tab.Title ?? "" };
            button.AddToClassList("coreai-hub-tab");
            // WHY: one level down should read a touch lighter than the top tab bar without a USS change.
            button.style.height = 28f;
            button.style.fontSize = 13f;
            _buttons.Add(button);
            _bar.Add(button);
        }

        private static VisualElement SafeBuild(SubTab tab)
        {
            try
            {
                return tab.Content?.Invoke();
            }
            catch (Exception ex)
            {
                Logging.Log.Instance.Error(
                    $"[HubSubTabView] Sub-tab '{tab.Title}' content factory threw: {ex}");
                Label error = new($"'{tab.Title}' failed to load: {ex.Message}");
                error.style.color = new Color(1f, 0.5f, 0.5f, 1f);
                error.style.whiteSpace = WhiteSpace.Normal;
                return error;
            }
        }
    }
}
