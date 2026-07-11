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
    ///
    /// The shell (root/tab bar/content/collapse button) is declared in <see cref="shellUxml"/>, not
    /// built as a C# VisualElement tree. UIDocument (pre-6.5, no PanelRenderer) can rebuild its
    /// rootVisualElement out from under a MonoBehaviour, so every bind goes through the idempotent
    /// <see cref="Rebuild"/>/<see cref="Unwire"/> pair rather than a one-shot OnEnable build.
    /// </remarks>
    [RequireComponent(typeof(UIDocument))]
    public class CoreAiHubWindow : MonoBehaviour
    {
        private const string RootName = "coreai-hub-root";
        private const string TabBarName = "coreai-hub-tabbar";
        private const string ContentName = "coreai-hub-content";
        private const string EmptyName = "coreai-hub-empty";
        private const string CollapseName = "coreai-hub-collapse";
        private const string CollapsedClassName = "coreai-hub--collapsed";
        private const string TabClassName = "coreai-hub-tab";
        private const string TabActiveClassName = "coreai-hub-tab-active";

        [Header("Structure")]
        [Tooltip("UXML shell (root/tab bar/content/collapse button). The window clones this into the " +
                 "UIDocument's rootVisualElement instead of building the tree in C#.")]
        [SerializeField]
        private VisualTreeAsset shellUxml;

        [Header("Style (optional)")]
        [Tooltip("Optional stylesheet layered on top of the shell's built-in styles (via the UXML's own " +
                 "<Style> reference). Leave empty to use the defaults.")]
        [SerializeField]
        private StyleSheet styleSheet;

        [Tooltip("Text shown in the content area when no page is registered.")] [SerializeField]
        private string emptyStateText = "No Hub pages registered.";

        private UIDocument _document;
        private VisualElement _boundUiRoot;
        private VisualElement _root;
        private VisualElement _tabBar;
        private VisualElement _content;
        private Label _emptyLabel;
        private Button _collapseButton;

        private HubPageRegistry _registry;
        private bool _uiReady;
        private bool _collapsed;
        private bool _missingShellWarned;

        // Cached page instances and their created content, keyed by page id. These survive UI rebuilds —
        // only the visual tree gets recreated, never the pages themselves.
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
                // rootVisualElement can be null until the panel is ready; retried from Update().
                return;
            }

            EnsureUi(uiRoot);
        }

        protected virtual void OnDisable()
        {
            UnsubscribeRegistry();
            DestroyAllPages();
            TeardownUi();
        }

        private void Update()
        {
            // The UIDocument's rootVisualElement can be rebuilt after our OnEnable (e.g. another UIDocument
            // sharing the PanelSettings re-inits the panel, or the panel comes up a frame late), which
            // orphans our cloned tree and leaves the Hub invisible. EnsureUi cheaply detects both "never
            // built" and "tree was (re)created" and re-runs the full bind in either case.
            if (_document == null)
            {
                _document = GetComponent<UIDocument>();
            }

            VisualElement uiRoot = _document != null ? _document.rootVisualElement : null;
            if (uiRoot == null)
            {
                return;
            }

            EnsureUi(uiRoot);
        }

        private void EnsureUi(VisualElement uiRoot)
        {
            bool detached = _uiReady && (_root == null || _root.parent != uiRoot);
            if (!_uiReady || _boundUiRoot != uiRoot || detached)
            {
                Rebuild(uiRoot);
            }
        }

        /// <summary>
        /// Idempotent (re)build: clones <see cref="shellUxml"/> into <paramref name="uiRoot"/>, re-queries
        /// the named elements, and rewires everything. Safe to call whenever the panel's rootVisualElement
        /// changes identity, not just once from OnEnable.
        /// </summary>
        private void Rebuild(VisualElement uiRoot)
        {
            Unwire();

            if (shellUxml == null)
            {
                if (!_missingShellWarned)
                {
                    Debug.LogWarning("[CoreAiHubWindow] shellUxml is not assigned; the Hub UI cannot be built.");
                    _missingShellWarned = true;
                }

                return;
            }

            // Guard against double-adding if a stale clone is still parented under uiRoot.
            uiRoot.Q<VisualElement>(RootName)?.RemoveFromHierarchy();
            shellUxml.CloneTree(uiRoot);

            _root = uiRoot.Q<VisualElement>(RootName);
            if (_root == null)
            {
                Debug.LogError($"[CoreAiHubWindow] shellUxml is missing the '{RootName}' element.");
                return;
            }

            _tabBar = _root.Q<VisualElement>(TabBarName);
            _content = _root.Q<VisualElement>(ContentName);
            _emptyLabel = _content?.Q<Label>(EmptyName);
            _collapseButton = _root.Q<Button>(CollapseName);
            if (_collapseButton != null)
            {
                _collapseButton.clicked += ToggleCollapsed;
            }

            if (styleSheet != null)
            {
                _root.styleSheets.Add(styleSheet);
            }

            ApplyCollapsedState();

            _boundUiRoot = uiRoot;
            _uiReady = true;

            RebuildTabs();
        }

        /// <summary>Collapses the Hub to just its toggle button, or restores the full window.</summary>
        public void ToggleCollapsed()
        {
            SetCollapsed(!_collapsed);
        }

        /// <summary>Hides the tabs + content when collapsed (leaving the toggle), restores them when expanded.</summary>
        public void SetCollapsed(bool collapsed)
        {
            _collapsed = collapsed;
            ApplyCollapsedState();
        }

        private void ApplyCollapsedState()
        {
            if (_root == null)
            {
                return;
            }

            _root.EnableInClassList(CollapsedClassName, _collapsed);

            if (_collapseButton != null)
            {
                _collapseButton.text = _collapsed ? "+" : "–";
            }
        }

        private void Unwire()
        {
            if (_collapseButton != null)
            {
                _collapseButton.clicked -= ToggleCollapsed;
            }

            _tabButtons.Clear();
            _root = null;
            _tabBar = null;
            _content = null;
            _emptyLabel = null;
            _collapseButton = null;
            _uiReady = false;
        }

        private void TeardownUi()
        {
            _root?.RemoveFromHierarchy();
            Unwire();

            _pageContent.Clear();
            _activePageId = null;
            _boundUiRoot = null;
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
            _root.schedule.Execute(() =>
            {
                // A re-registered id may point to a NEW factory (e.g. a DI binder upgrading the
                // built-in Settings/Statistics pages with live sources after the DI-free bootstrap
                // registered them with null sources). We cache page instances at metadata-peek time,
                // so drop any cached instance/content for a still-present id and let the new factory
                // build it on next activation.
                if (pageId != null && _registry != null && _registry.TryGet(pageId, out _))
                {
                    bool wasActive = string.Equals(pageId, _activePageId, StringComparison.Ordinal);
                    DestroyPage(pageId);
                    if (wasActive)
                    {
                        _activePageId = null;
                    }
                }

                RebuildTabs();
            });
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
                tab.focusable = false;
                _tabBar.Add(tab);
                _tabButtons[pageId] = tab;
            }

            if (pages.Count == 0)
            {
                ShowEmptyState();
                _activePageId = null;
                return;
            }

            // Preserve the active page if it still exists; otherwise fall back to the first tab. Either
            // way the active content must be re-parented into _content, since a rebuild may have replaced
            // it with a fresh (empty) container.
            bool activeStillPresent = _activePageId != null && _tabButtons.ContainsKey(_activePageId);
            if (activeStillPresent)
            {
                RefreshActiveContent();
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
                RefreshActiveContent();
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
            ApplyFullBleed(page is IHubFullBleedPage);
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

        /// <summary>Re-parents the active page's cached content into <see cref="_content"/> without
        /// touching page lifecycle (used when the same page is already active, incl. after a UI rebuild).</summary>
        private void RefreshActiveContent()
        {
            if (_content == null)
            {
                return;
            }

            _content.Clear();
            if (_activePageId != null && _pageContent.TryGetValue(_activePageId, out VisualElement content) &&
                content != null)
            {
                _content.Add(content);
            }
            else
            {
                ShowEmptyState();
            }
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
                kvp.Value.EnableInClassList(TabActiveClassName, isActive);
            }
        }

        /// <summary>Drops the content area's padding for a full-bleed page (e.g. the embedded chat) so it
        /// reaches all four edges; restores the stylesheet padding for normal pages.</summary>
        private void ApplyFullBleed(bool fullBleed)
        {
            if (_content == null)
            {
                return;
            }

            if (fullBleed)
            {
                _content.style.paddingLeft = 0f;
                _content.style.paddingRight = 0f;
                _content.style.paddingTop = 0f;
                _content.style.paddingBottom = 0f;
            }
            else
            {
                _content.style.paddingLeft = StyleKeyword.Null;
                _content.style.paddingRight = StyleKeyword.Null;
                _content.style.paddingTop = StyleKeyword.Null;
                _content.style.paddingBottom = StyleKeyword.Null;
            }
        }

        private void ShowEmptyState()
        {
            if (_content == null)
            {
                return;
            }

            _content.Clear();
            if (_emptyLabel != null)
            {
                _emptyLabel.text = emptyStateText;
                _content.Add(_emptyLabel);
            }
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
    }
}