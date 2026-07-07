using System;
using System.Collections.Generic;
using CoreAI.Hub;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.Hub.UI
{
    /// <summary>
    /// Runtime UI Toolkit window that surfaces pages registered into a <see cref="HubPageRegistry"/>
    /// as a tab bar plus a lazily populated content container.
    /// </summary>
    /// <remarks>
    /// Attach this component to a GameObject that also has a <see cref="UIDocument"/>. Assign the
    /// registry through <see cref="Registry"/> (via DI, a demo controller, or the inspector-less
    /// setter). The window subscribes to <see cref="HubPageRegistry.PageRegistered"/> and
    /// <see cref="HubPageRegistry.PageUnregistered"/> to rebuild its tab bar live, creates each
    /// page's content on first activation, and forwards
    /// <see cref="IHubPage.OnActivated"/>/<see cref="IHubPage.OnDeactivated"/> as tabs change.
    ///
    /// The core assembly is UI-framework-free, so <see cref="IHubPage.CreatePageContent"/> returns
    /// an <see cref="object"/>; this window casts it to <see cref="VisualElement"/>.
    /// </remarks>
    [RequireComponent(typeof(UIDocument))]
    public class CoreAiHubWindow : MonoBehaviour
    {
        private const string RootClassName = "coreai-hub-root";
        private const string TabBarClassName = "coreai-hub-tabbar";
        private const string TabClassName = "coreai-hub-tab";
        private const string TabActiveClassName = "coreai-hub-tab-active";
        private const string ContentClassName = "coreai-hub-content";
        private const string EmptyClassName = "coreai-hub-empty";

        [Header("Style (optional)")]
        [Tooltip("Optional stylesheet layered on top of the built-in inline styles. Leave empty to use the defaults.")]
        [SerializeField]
        private StyleSheet styleSheet;

        [Tooltip("Text shown in the content area when no page is registered.")]
        [SerializeField]
        private string emptyStateText = "No Hub pages registered.";

        private UIDocument _document;
        private VisualElement _root;
        private VisualElement _tabBar;
        private VisualElement _content;

        private HubPageRegistry _registry;
        private bool _uiReady;

        // Cached page instances and their created content, keyed by page id.
        private readonly Dictionary<string, IHubPage> _pages = new(StringComparer.Ordinal);
        private readonly Dictionary<string, VisualElement> _pageContent = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Button> _tabButtons = new(StringComparer.Ordinal);

        private string _activePageId;

        /// <summary>
        /// The page registry this window renders. Setting a new registry rewires event
        /// subscriptions and rebuilds the tab bar. May be set before or after the UI is built.
        /// </summary>
        public HubPageRegistry Registry
        {
            get => _registry;
            set
            {
                if (ReferenceEquals(_registry, value))
                {
                    return;
                }

                UnsubscribeRegistry();
                _registry = value;
                SubscribeRegistry();

                if (_uiReady)
                {
                    RebuildTabs();
                }
            }
        }

        /// <summary>Currently active page id, or <c>null</c> when nothing is active.</summary>
        public string ActivePageId => _activePageId;

        protected virtual void OnEnable()
        {
            _document = GetComponent<UIDocument>();
            VisualElement uiRoot = _document != null ? _document.rootVisualElement : null;
            if (uiRoot == null)
            {
                // rootVisualElement can be null until the panel is ready; retry on the next editor/player tick.
                return;
            }

            BuildUi(uiRoot);
        }

        protected virtual void OnDisable()
        {
            UnsubscribeRegistry();
            DestroyAllPages();
            TeardownUi();
        }

        private void BuildUi(VisualElement uiRoot)
        {
            if (_uiReady)
            {
                return;
            }

            _root = new VisualElement { name = "coreai-hub-root" };
            _root.AddToClassList(RootClassName);
            ApplyRootInlineStyles(_root);

            _tabBar = new VisualElement { name = "coreai-hub-tabbar" };
            _tabBar.AddToClassList(TabBarClassName);
            ApplyTabBarInlineStyles(_tabBar);
            _root.Add(_tabBar);

            _content = new VisualElement { name = "coreai-hub-content" };
            _content.AddToClassList(ContentClassName);
            _content.style.flexGrow = 1f;
            _content.style.paddingTop = 16f;
            _content.style.paddingBottom = 16f;
            _content.style.paddingLeft = 16f;
            _content.style.paddingRight = 16f;
            _root.Add(_content);

            if (styleSheet != null)
            {
                _root.styleSheets.Add(styleSheet);
            }

            uiRoot.Add(_root);
            _uiReady = true;

            RebuildTabs();
        }

        private void TeardownUi()
        {
            _tabButtons.Clear();
            _pageContent.Clear();
            _activePageId = null;

            _root?.RemoveFromHierarchy();
            _root = null;
            _tabBar = null;
            _content = null;
            _uiReady = false;
        }

        // ===================== Registry wiring =====================

        private void SubscribeRegistry()
        {
            if (_registry == null)
            {
                return;
            }

            _registry.PageRegistered += OnRegistryChanged;
            _registry.PageUnregistered += OnRegistryChanged;
        }

        private void UnsubscribeRegistry()
        {
            if (_registry == null)
            {
                return;
            }

            _registry.PageRegistered -= OnRegistryChanged;
            _registry.PageUnregistered -= OnRegistryChanged;
        }

        private void OnRegistryChanged(string pageId)
        {
            if (!_uiReady || _root == null)
            {
                return;
            }

            // Registry events may arrive off the UI thread; marshal the rebuild onto the panel's
            // scheduler so VisualElement mutations always run on the main thread.
            _root.schedule.Execute(RebuildTabs);
        }

        // ===================== Tab bar =====================

        private void RebuildTabs()
        {
            if (!_uiReady || _tabBar == null)
            {
                return;
            }

            _tabBar.Clear();
            _tabButtons.Clear();

            IReadOnlyList<(string pageId, int order)> pages =
                _registry != null ? _registry.List() : Array.Empty<(string, int)>();

            // Drop cached content/instances for pages that no longer exist.
            PruneRemovedPages(pages);

            foreach ((string pageId, int _) in pages)
            {
                string displayName = ResolveDisplayName(pageId);
                Button tab = new(() => ActivatePage(pageId))
                {
                    text = displayName,
                    name = "coreai-hub-tab-" + pageId
                };
                tab.AddToClassList(TabClassName);
                ApplyTabInlineStyles(tab);
                tab.focusable = false;
                _tabBar.Add(tab);
                _tabButtons[pageId] = tab;
            }

            // Preserve the active page if it still exists; otherwise fall back to the first tab.
            if (pages.Count == 0)
            {
                ShowEmptyState();
                _activePageId = null;
                return;
            }

            bool activeStillPresent = _activePageId != null && _tabButtons.ContainsKey(_activePageId);
            if (activeStillPresent)
            {
                HighlightActiveTab();
            }
            else
            {
                ActivatePage(pages[0].pageId);
            }
        }

        private void PruneRemovedPages(IReadOnlyList<(string pageId, int order)> pages)
        {
            HashSet<string> present = new(StringComparer.Ordinal);
            foreach ((string pageId, int _) in pages)
            {
                present.Add(pageId);
            }

            List<string> stale = null;
            foreach (KeyValuePair<string, IHubPage> kvp in _pages)
            {
                if (!present.Contains(kvp.Key))
                {
                    (stale ??= new List<string>()).Add(kvp.Key);
                }
            }

            if (stale == null)
            {
                return;
            }

            foreach (string pageId in stale)
            {
                DestroyPage(pageId);
                if (string.Equals(pageId, _activePageId, StringComparison.Ordinal))
                {
                    _activePageId = null;
                }
            }
        }

        private string ResolveDisplayName(string pageId)
        {
            // Reuse an already-created page's display name; otherwise use the id until first activation.
            if (_pages.TryGetValue(pageId, out IHubPage cached) && cached != null)
            {
                return string.IsNullOrEmpty(cached.DisplayName) ? pageId : cached.DisplayName;
            }

            // Peek the factory to read metadata without mounting content.
            if (_registry != null && _registry.TryGet(pageId, out Func<IHubPage> factory) && factory != null)
            {
                try
                {
                    IHubPage page = factory();
                    if (page != null)
                    {
                        _pages[pageId] = page;
                        return string.IsNullOrEmpty(page.DisplayName) ? pageId : page.DisplayName;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[CoreAiHubWindow] Page factory '{pageId}' threw during metadata resolve: {ex}");
                }
            }

            return pageId;
        }

        // ===================== Page activation =====================

        /// <summary>Activates the page with the given id, lazily creating its content on first use.</summary>
        public void ActivatePage(string pageId)
        {
            if (!_uiReady || _content == null || string.IsNullOrEmpty(pageId))
            {
                return;
            }

            if (string.Equals(pageId, _activePageId, StringComparison.Ordinal) &&
                _pageContent.ContainsKey(pageId))
            {
                HighlightActiveTab();
                return;
            }

            // Deactivate the outgoing page.
            if (_activePageId != null && _pages.TryGetValue(_activePageId, out IHubPage previous) && previous != null)
            {
                try
                {
                    previous.OnDeactivated();
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[CoreAiHubWindow] Page '{_activePageId}' OnDeactivated threw: {ex}");
                }
            }

            IHubPage page = ResolvePage(pageId);
            if (page == null)
            {
                ShowEmptyState();
                _activePageId = null;
                return;
            }

            VisualElement content = ResolvePageContent(pageId, page);

            _content.Clear();
            if (content != null)
            {
                _content.Add(content);
            }
            else
            {
                ShowEmptyState();
            }

            _activePageId = pageId;

            try
            {
                page.OnActivated();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CoreAiHubWindow] Page '{pageId}' OnActivated threw: {ex}");
            }

            HighlightActiveTab();
        }

        private IHubPage ResolvePage(string pageId)
        {
            if (_pages.TryGetValue(pageId, out IHubPage existing) && existing != null)
            {
                return existing;
            }

            if (_registry == null || !_registry.TryGet(pageId, out Func<IHubPage> factory) || factory == null)
            {
                return null;
            }

            try
            {
                IHubPage page = factory();
                if (page != null)
                {
                    _pages[pageId] = page;
                }

                return page;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CoreAiHubWindow] Page factory '{pageId}' threw: {ex}");
                return null;
            }
        }

        private VisualElement ResolvePageContent(string pageId, IHubPage page)
        {
            if (_pageContent.TryGetValue(pageId, out VisualElement cached) && cached != null)
            {
                return cached;
            }

            VisualElement content = null;
            try
            {
                object created = page.CreatePageContent != null ? page.CreatePageContent() : null;
                content = created as VisualElement;
                if (created != null && content == null)
                {
                    Debug.LogError(
                        $"[CoreAiHubWindow] Page '{pageId}' returned {created.GetType().Name}, expected a VisualElement.");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CoreAiHubWindow] Page '{pageId}' CreatePageContent threw: {ex}");
            }

            if (content != null)
            {
                _pageContent[pageId] = content;
            }

            return content;
        }

        private void HighlightActiveTab()
        {
            foreach (KeyValuePair<string, Button> kvp in _tabButtons)
            {
                bool isActive = string.Equals(kvp.Key, _activePageId, StringComparison.Ordinal);
                if (isActive)
                {
                    kvp.Value.AddToClassList(TabActiveClassName);
                    ApplyTabActiveInlineStyles(kvp.Value);
                }
                else
                {
                    kvp.Value.RemoveFromClassList(TabActiveClassName);
                    ApplyTabInlineStyles(kvp.Value);
                }
            }
        }

        private void ShowEmptyState()
        {
            if (_content == null)
            {
                return;
            }

            _content.Clear();
            Label label = new(emptyStateText) { name = "coreai-hub-empty" };
            label.AddToClassList(EmptyClassName);
            label.style.flexGrow = 1f;
            label.style.color = new Color(0.77f, 0.86f, 0.91f, 0.6f);
            label.style.unityTextAlign = TextAnchor.MiddleCenter;
            label.style.whiteSpace = WhiteSpace.Normal;
            _content.Add(label);
        }

        // ===================== Page lifecycle cleanup =====================

        private void DestroyAllPages()
        {
            List<string> ids = new(_pages.Keys);
            foreach (string id in ids)
            {
                DestroyPage(id);
            }

            _pages.Clear();
            _pageContent.Clear();
            _activePageId = null;
        }

        private void DestroyPage(string pageId)
        {
            if (_pages.TryGetValue(pageId, out IHubPage page) && page != null)
            {
                try
                {
                    if (string.Equals(pageId, _activePageId, StringComparison.Ordinal))
                    {
                        page.OnDeactivated();
                    }

                    page.OnDestroyed();
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[CoreAiHubWindow] Page '{pageId}' teardown threw: {ex}");
                }
            }

            _pages.Remove(pageId);
            if (_pageContent.TryGetValue(pageId, out VisualElement content))
            {
                content?.RemoveFromHierarchy();
                _pageContent.Remove(pageId);
            }
        }

        // ===================== Inline style fallbacks =====================
        // Applied so the Hub renders correctly even when the optional stylesheet is not assigned.

        private static void ApplyRootInlineStyles(VisualElement root)
        {
            root.style.position = Position.Absolute;
            root.style.top = 24f;
            root.style.left = 24f;
            root.style.width = 720f;
            root.style.height = 640f;
            root.style.backgroundColor = new Color(0.024f, 0.055f, 0.118f, 0.93f);
            root.style.borderTopLeftRadius = 16f;
            root.style.borderTopRightRadius = 16f;
            root.style.borderBottomLeftRadius = 16f;
            root.style.borderBottomRightRadius = 16f;
            SetBorderWidth(root, 2f);
            SetBorderColor(root, new Color(0.067f, 0.627f, 0.71f, 0.35f));
            root.style.flexDirection = FlexDirection.Column;
            root.style.overflow = Overflow.Hidden;
        }

        private static void ApplyTabBarInlineStyles(VisualElement tabBar)
        {
            tabBar.style.flexDirection = FlexDirection.Row;
            tabBar.style.flexWrap = Wrap.Wrap;
            tabBar.style.flexShrink = 0f;
            tabBar.style.backgroundColor = new Color(0.039f, 0.086f, 0.176f, 0.95f);
            tabBar.style.borderBottomWidth = 1f;
            tabBar.style.borderBottomColor = new Color(0.067f, 0.627f, 0.71f, 0.3f);
            tabBar.style.paddingTop = 8f;
            tabBar.style.paddingBottom = 8f;
            tabBar.style.paddingLeft = 8f;
            tabBar.style.paddingRight = 8f;
        }

        private static void ApplyTabInlineStyles(Button tab)
        {
            tab.style.height = 34f;
            tab.style.marginRight = 8f;
            tab.style.marginBottom = 4f;
            tab.style.paddingLeft = 16f;
            tab.style.paddingRight = 16f;
            tab.style.backgroundColor = new Color(0.067f, 0.627f, 0.71f, 0.15f);
            SetBorderWidth(tab, 1f);
            SetBorderColor(tab, new Color(0.302f, 0.816f, 0.882f, 0.35f));
            SetBorderRadius(tab, 17f);
            tab.style.color = new Color(0.77f, 0.86f, 0.91f, 1f);
            tab.style.fontSize = 15f;
            tab.style.unityFontStyleAndWeight = FontStyle.Bold;
        }

        private static void ApplyTabActiveInlineStyles(Button tab)
        {
            tab.style.backgroundColor = new Color(0.302f, 0.816f, 0.882f, 0.85f);
            SetBorderColor(tab, new Color(0.784f, 0.941f, 1f, 0.9f));
            tab.style.color = new Color(0.024f, 0.055f, 0.118f, 1f);
        }

        private static void SetBorderWidth(VisualElement el, float width)
        {
            el.style.borderTopWidth = width;
            el.style.borderBottomWidth = width;
            el.style.borderLeftWidth = width;
            el.style.borderRightWidth = width;
        }

        private static void SetBorderColor(VisualElement el, Color color)
        {
            el.style.borderTopColor = color;
            el.style.borderBottomColor = color;
            el.style.borderLeftColor = color;
            el.style.borderRightColor = color;
        }

        private static void SetBorderRadius(VisualElement el, float radius)
        {
            el.style.borderTopLeftRadius = radius;
            el.style.borderTopRightRadius = radius;
            el.style.borderBottomLeftRadius = radius;
            el.style.borderBottomRightRadius = radius;
        }
    }
}
