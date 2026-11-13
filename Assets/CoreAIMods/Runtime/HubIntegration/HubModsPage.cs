using System;
using System.Collections.Generic;
using System.Threading;
using CoreAI.ChatMarkdown;
using CoreAI.Hub;
using CoreAI.Infrastructure.Lua;
using CoreAI.Mods.Folders;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.Ai.Hub
{
    /// <summary>
    /// UI Toolkit Hub page that replaces the old IMGUI mod panel. Lists every known mod in a virtualized
    /// folder tree (<see cref="ModFolderTreeView"/> over <see cref="ModFolderTree"/>; a mod's folder is its
    /// <c>category</c> path) with a name/folder/tags search that keeps matching folders open, remembered
    /// folds, folder toggles that switch a whole branch, "Move" and folder actions (new folder, rename,
    /// delete an empty one) run through <see cref="ModFolderService"/>; each mod row exposes an on/off
    /// toggle, About (only for a mod with a description: opens a panel under the tree that shows the
    /// description as Markdown through <see cref="CoreAiMarkdownView"/>, plain text without a renderer
    /// package), Edit (opens the inline <see cref="HubModEditorPage"/>), Export, Apply update, Delete, and
    /// its live status (loaded, handlers, timers, errors). An "Add" button opens the editor with a starter template and "Paste"
    /// creates a mod from the Lua in <see cref="GUIUtility.systemCopyBuffer"/>. The list live-refreshes
    /// on <see cref="IHubModService.ModsChanged"/> (fired when a mod is loaded/unloaded/deleted — by the
    /// UI or the AI's manage_mods tool) and via a manual Refresh button. Content is built lazily in
    /// <see cref="CreatePageContent"/>; everything is retained-mode (no OnGUI).
    /// </summary>
    public sealed class HubModsPage : IHubPage, IHubEscapeHandler, CoreAI.Hub.UI.IHubSubTabRoot
    {
        /// <summary>Default registry id for the Mods page.</summary>
        public const string DefaultPageId = "coreai.hub.mods";

        private readonly IHubModService _service;
        private readonly Action<Action> _deferToPanel;
        private readonly IHubModEditorPreferences _editorPreferences;

        private VisualElement _root;
        private VisualElement _listRoot;
        private VisualElement _editorRoot;
        private HubModEditorPage _editor;
        private ModFolderTreeView _treeView;
        private ModFolderTree _tree;
        private ModFolderFoldState _foldState;
        private int _savedFoldVersion;
        private IVisualElementScheduledItem _foldSave;
        private readonly ModFolderService _folderService;
        private readonly IModFolderListStore _folderList;
        private readonly IHubModFolderPreferences _folderPreferences;

        // WHY cached per refresh: the tree view asks for a row's status line on every bind while scrolling,
        // so building it there would allocate per frame.
        private readonly Dictionary<string, string> _modDetails = new(StringComparer.Ordinal);
        private Label _status;
        private VisualElement _about;
        private Label _aboutTitle;
        private CoreAiMarkdownView _aboutView;
        private string _aboutModId;
        private TextField _searchField;
        private string _search = "";
        private bool _subscribed;
        private bool _editorOpen;
        private bool _destroyed;
        private int _refreshQueued;

        // WHY: ListMods() re-parses every mod's @coreai header, so results are cached here and only
        // reloaded on Refresh / ModsChanged / a mutating action — typing in the search box just
        // re-filters the cache instead of re-parsing.
        private IReadOnlyList<HubModRecord> _modsCache;
        private string _modsLoadError;
        private IVisualElementScheduledItem _searchDebounce;

        /// <param name="service">VM-agnostic mod CRUD/query surface (built by <see cref="HubModsPages"/>).</param>
        /// <param name="order">Sort priority for the Hub tab (defaults to 300).</param>
        public HubModsPage(IHubModService service, int order = 300)
            : this(service, order, null)
        {
        }

        /// <param name="service">VM-agnostic mod CRUD/query surface.</param>
        /// <param name="order">Sort priority for the Hub tab.</param>
        /// <param name="deferToPanel">
        /// Runs an action on the panel's next update; null uses the page root's scheduler. Tests pass a
        /// recorder, since a page that is not attached to a panel never runs its scheduled work.
        /// </param>
        /// <param name="editorPreferences">
        /// Hub-wide editor preferences handed to every editor this page opens; null uses
        /// <see cref="PlayerPrefsHubModEditorPreferences.Instance"/>.
        /// </param>
        /// <param name="service">VM-agnostic mod CRUD/query surface.</param>
        /// <param name="order">Sort priority for the Hub tab.</param>
        /// <param name="folderList">
        /// Where the persisted (created or emptied) folders are kept; null keeps them in
        /// <paramref name="folderPreferences"/>.
        /// </param>
        /// <param name="folderPreferences">
        /// Where the viewer's fold state (and, without <paramref name="folderList"/>, the folders) are kept;
        /// null uses <see cref="PlayerPrefsHubModFolderPreferences.Instance"/>.
        /// </param>
        public HubModsPage(IHubModService service, int order, IModFolderListStore folderList,
            IHubModFolderPreferences folderPreferences)
            : this(service, order, null, null, folderList, folderPreferences)
        {
        }

        internal HubModsPage(IHubModService service, int order, Action<Action> deferToPanel,
            IHubModEditorPreferences editorPreferences = null, IModFolderListStore folderList = null,
            IHubModFolderPreferences folderPreferences = null)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            Order = order;
            _deferToPanel = deferToPanel;
            _editorPreferences = editorPreferences;
            _folderPreferences = folderPreferences ?? PlayerPrefsHubModFolderPreferences.Instance;
            IHubModFolderPreferences preferences = _folderPreferences;
            _folderList = folderList ?? new TextModFolderListStore(() => preferences.FoldersText,
                text => preferences.FoldersText = text);
            _folderService = new ModFolderService(new HubModFolderStore(_service, _folderList));
        }

        /// <summary>The folder tree view (null before the page content is built).</summary>
        internal ModFolderTreeView TreeView => _treeView;

        /// <summary>The tree shown (null before the first refresh).</summary>
        internal ModFolderTree Tree => _tree;

        /// <summary>The folder operations the page runs.</summary>
        internal ModFolderService FolderService => _folderService;

        /// <summary>The viewer's fold state (null before the page content is built).</summary>
        internal ModFolderFoldState FoldState => _foldState;

        /// <summary>The About panel under the tree (null before the page content is built).</summary>
        internal VisualElement AboutPanel => _about;

        /// <summary>The description view of the About panel (null before the page content is built).</summary>
        internal CoreAiMarkdownView AboutView => _aboutView;

        /// <summary>The mod whose description the About panel shows; null while the panel is closed.</summary>
        internal string AboutModId => _aboutModId;

        /// <inheritdoc />
        public string PageId => DefaultPageId;

        /// <inheritdoc />
        public string DisplayName => "Mods";

        /// <inheritdoc />
        public int Order { get; }

        /// <inheritdoc />
        public Func<object> CreatePageContent => Build;

        /// <summary>Whether the mod editor replaces the list (false before the page content is built).</summary>
        internal bool IsEditorOpen => _editorOpen;

        /// <inheritdoc />
        public bool TryHandleEscape() => !_destroyed && _editorOpen && (_editor?.TryHandleEscape() ?? false);

        /// <summary>
        /// The "Mods" sub-tab was pressed: closes the About panel and the editor and shows the list. An editor with an
        /// unsaved draft stays open and offers its own "Discard draft and go back", as its Back button does.
        /// </summary>
        public void ReturnToRoot()
        {
            if (_destroyed || _root == null)
            {
                return;
            }

            CloseAbout();
            if (_editorOpen)
            {
                _editor?.RequestClose();
            }
        }

        /// <summary>Opens the editor on a new mod, as the "Add" button does.</summary>
        internal void OpenNewEditorForTests()
        {
            OpenNewEditor();
        }

        /// <summary>Opens the editor on mod <paramref name="id"/>, as its Edit button does.</summary>
        internal void OpenEditEditorForTests(string id)
        {
            OpenEditEditor(id);
        }

        /// <inheritdoc />
        public void OnActivated()
        {
            Subscribe();
            RefreshList();
        }

        /// <inheritdoc />
        public void OnDeactivated()
        {
            SaveFoldStateIfChanged();
            Unsubscribe();
        }

        /// <inheritdoc />
        public void OnDestroyed()
        {
            SaveFoldStateIfChanged();
            _destroyed = true;
            _editor?.OnDestroyed();
            _root?.SetEnabled(false);
            _searchDebounce?.Pause();
            Unsubscribe();
        }

        private object Build()
        {
            _root = new VisualElement { name = "coreai-hub-mods" };
            _root.style.flexGrow = 1f;
            _root.style.flexDirection = FlexDirection.Column;

            _listRoot = BuildListRoot();
            _editorRoot = new VisualElement { name = "coreai-hub-mods-editor-host" };
            _editorRoot.style.flexGrow = 1f;
            _editorRoot.style.display = DisplayStyle.None;

            _root.Add(_listRoot);
            _root.Add(_editorRoot);

            Subscribe();
            RefreshList();
            return _root;
        }

        private VisualElement BuildListRoot()
        {
            VisualElement listRoot = new() { name = "coreai-hub-mods-list" };
            listRoot.style.flexGrow = 1f;
            listRoot.style.flexDirection = FlexDirection.Column;

            listRoot.Add(HubModWidgets.MakeTitle("Mods"));

            if (!_service.IsSupported)
            {
                listRoot.Add(HubModWidgets.MakeNote(
                    "The Lua sandbox is not supported on this platform, so mods cannot be loaded here."));
            }

            VisualElement toolbar = new();
            toolbar.style.flexDirection = FlexDirection.Row;
            toolbar.style.flexWrap = Wrap.Wrap;
            toolbar.style.marginBottom = 6f;

            _searchField = new TextField { name = "coreai-hub-mods-search" };
            _searchField.value = _search;
            _searchField.style.flexGrow = 1f;
            _searchField.style.minWidth = 140f;
            _searchField.style.marginRight = 6f;
            SetPlaceholder(_searchField, "Search name / folder / tags");
            _searchField.RegisterValueChangedCallback(evt =>
            {
                _search = evt.newValue ?? "";
                ScheduleSearchRender();
            });
            toolbar.Add(_searchField);

            toolbar.Add(HubModWidgets.MakeButton("Refresh", RefreshList));
            toolbar.Add(HubModWidgets.MakeButton("Add", OpenNewEditor));
            toolbar.Add(HubModWidgets.MakeButton("Paste", PasteFromClipboard));
            toolbar.Add(HubModWidgets.MakeButton("Import", ImportFromClipboard));
            toolbar.Add(HubModWidgets.MakeButton("New folder", () => PromptNewFolder(ModFolderPath.Root)));
            listRoot.Add(toolbar);

            _status = HubModWidgets.MakeStatus();
            listRoot.Add(_status);

            _foldState ??= ModFolderStateCodec.DecodeFoldState(SafeFoldStateText()).State;
            _savedFoldVersion = _foldState.Version;
            _treeView = new ModFolderTreeView(new ModFolderTreeViewOptions
            {
                ShowSearchField = false,
                EmptyText = "No mods yet. Use Add or Paste to create one.",
                NoMatchText = "No mods match the search.",
                ModActions = new[] { AboutAction, EditAction, ExportAction, UpdateAction, DeleteAction },
                FolderActions = new[] { NewFolderAction, RenameAction, DeleteAction },
                ModActionFilter = IsModActionShown,
                ModDetail = DetailOf,
                TextColor = HubModWidgets.Text,
                MutedColor = HubModWidgets.Muted,
                AccentColor = HubModWidgets.Accent
            }) { name = "coreai-hub-mods-tree" };
            _treeView.ModEnableRequested += (mod, enable) => ToggleMod(mod.Id, enable);
            _treeView.BranchEnableRequested += OnBranchEnableRequested;
            _treeView.MoveRequested += OnMoveRequested;
            _treeView.ModActionRequested += OnModAction;
            _treeView.FolderActionRequested += OnFolderAction;
            _treeView.FoldChanged += ScheduleFoldSave;
            listRoot.Add(_treeView);
            listRoot.Add(BuildAboutPanel());

            return listRoot;
        }

        private VisualElement BuildAboutPanel()
        {
            _about = HubModWidgets.MakePanel();
            _about.name = "coreai-hub-mods-about";
            _about.style.flexShrink = 0f;
            _about.style.marginTop = 6f;
            _about.style.display = DisplayStyle.None;

            VisualElement header = new();
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            _aboutTitle = HubModWidgets.MakeFieldLabel("");
            _aboutTitle.style.flexGrow = 1f;
            _aboutTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
            header.Add(_aboutTitle);
            header.Add(HubModWidgets.MakeButton("Close", CloseAbout));
            _about.Add(header);

            _aboutView = new CoreAiMarkdownView { name = "coreai-hub-mods-about-text" };
            _aboutView.style.color = HubModWidgets.Text;
            _aboutView.style.marginTop = 4f;
            _about.Add(_aboutView);
            return _about;
        }

        /// <summary>
        /// Opens the About panel on <paramref name="modId"/>'s description, or closes it when it already shows
        /// that mod or the mod has no description.
        /// </summary>
        internal void ToggleAbout(string modId)
        {
            if (_about == null || _destroyed)
            {
                return;
            }

            if (string.Equals(_aboutModId, modId, StringComparison.Ordinal))
            {
                CloseAbout();
                return;
            }

            ShowAbout(FindCachedMod(modId));
        }

        private void ShowAbout(HubModRecord mod)
        {
            if (mod == null || string.IsNullOrWhiteSpace(mod.Description))
            {
                CloseAbout();
                return;
            }

            _aboutModId = mod.Id;
            _aboutTitle.text = string.IsNullOrWhiteSpace(mod.Name) ? mod.Id : mod.Name;
            if (!string.Equals(_aboutView.Markdown, mod.Description, StringComparison.Ordinal))
            {
                _aboutView.Markdown = mod.Description;
            }

            _about.style.display = DisplayStyle.Flex;
        }

        private void CloseAbout()
        {
            _aboutModId = null;
            if (_about != null)
            {
                _about.style.display = DisplayStyle.None;
            }
        }

        /// <summary>Follows a refresh: the open About panel shows the mod's new description or closes when it is gone.</summary>
        private void RefreshAbout()
        {
            if (_aboutModId != null)
            {
                ShowAbout(FindCachedMod(_aboutModId));
            }
        }

        private HubModRecord FindCachedMod(string modId)
        {
            foreach (HubModRecord mod in _modsCache ?? Array.Empty<HubModRecord>())
            {
                if (mod != null && string.Equals(mod.Id, modId, StringComparison.Ordinal))
                {
                    return mod;
                }
            }

            return null;
        }

        /// <summary>Reloads the mods cache from the service, then re-renders. Use after any mutation or
        /// external change (ModsChanged); the search box itself only calls <see cref="RenderTree"/>.</summary>
        private void RefreshList()
        {
            if (_root == null || _destroyed)
            {
                return;
            }

            ReloadModsCache();
            RenderTree();
            RefreshAbout();
        }

        private void ReloadModsCache()
        {
            try
            {
                _modsCache = _service.ListMods();
                _modsLoadError = null;
                CacheModDetails();
            }
            catch (Exception ex)
            {
                _modsCache = Array.Empty<HubModRecord>();
                _modsLoadError = ex.Message;
            }
        }

        /// <summary>Debounces search-box re-renders (~250ms) so fast typing does not rebuild the tree
        /// on every keystroke; cancels any pending render before scheduling a new one.</summary>
        private void ScheduleSearchRender()
        {
            _searchDebounce?.Pause();
            _searchDebounce = _searchField.schedule.Execute(ApplySearch).StartingIn(250);
        }

        /// <summary>
        /// Builds the folder tree from the cached mod list and the persisted folders and shows it with the
        /// current search. Never calls the service's listing; callers that need fresh data call
        /// <see cref="RefreshList"/> instead.
        /// </summary>
        private void RenderTree()
        {
            if (_treeView == null)
            {
                return;
            }

            IReadOnlyList<string> folders;
            try
            {
                folders = _folderList.Load();
            }
            catch (Exception ex)
            {
                folders = Array.Empty<string>();
                SetStatus("Could not read the mod folders: " + ex.Message, true);
            }

            IReadOnlyList<HubModRecord> mods = _modsLoadError != null
                ? Array.Empty<HubModRecord>()
                : _modsCache ?? Array.Empty<HubModRecord>();
            _tree = ModFolderTree.Build(HubModFolderStore.ToEntries(mods), folders, _foldState);
            _treeView.SetTree(_tree);
            _treeView.SetSearch(_search);
            if (_modsLoadError != null)
            {
                SetStatus($"Failed to list mods: {_modsLoadError}", true);
            }
        }

        /// <summary>Applies the search box to the current tree without rebuilding it.</summary>
        private void ApplySearch()
        {
            _treeView?.SetSearch(_search);
        }

        private static string BuildMetaLine(HubModRecord mod)
        {
            string id = $"id: {mod.Id}";
            string caps = string.IsNullOrWhiteSpace(mod.Capabilities) ? "" : $"  caps: {mod.Capabilities}";
            string status = mod.IsLoaded
                ? $"  loaded  handlers: {mod.Handlers}  timers: {mod.Timers}  errors: {mod.Errors}"
                : mod.IsStored
                    ? "  stored (disabled)"
                    : "";
            string version = string.IsNullOrWhiteSpace(mod.Version) ? "" : $"  v{mod.Version}";
            string bundled = string.IsNullOrWhiteSpace(mod.Origin) ? "" : "  [bundled]";
            return id + version + caps + status + bundled + SuspensionNote(mod);
        }

        /// <summary>
        /// The meta-line note for a mod the runtime suspended after repeated budget trips; empty for any
        /// other mod.
        /// </summary>
        internal static string SuspensionNote(HubModRecord mod)
        {
            return mod != null && mod.SuspendedAfterBudgetTrips
                ? "  suspended after repeated budget trips — start it manually"
                : "";
        }

        private void ToggleMod(string id, bool enable)
        {
            if (_destroyed)
            {
                return;
            }

            try
            {
                if (enable)
                {
                    _service.Enable(id);
                    SetStatus($"Enabled '{id}'.", false);
                }
                else
                {
                    _service.Disable(id);
                    SetStatus($"Disabled '{id}'.", false);
                }
            }
            catch (OperationCanceledException)
            {
                SetStatus($"Toggle of '{id}' cancelled; live state may already have changed.", true);
            }
            catch (Exception ex)
            {
                SetStatus($"Failed to toggle '{id}': {ex.Message}", true);
            }

            RefreshList();
        }

        private void DeleteMod(string id)
        {
            if (_destroyed)
            {
                return;
            }

            try
            {
                bool deleted = _service.Delete(id);
                SetStatus(deleted ? $"Deleted '{id}'." : $"Deletion of '{id}' was not confirmed. Retry after recovery.",
                    !deleted);
            }
            catch (OperationCanceledException)
            {
                SetStatus($"Deletion of '{id}' cancelled; live state may already have changed.", true);
            }
            catch (LuaModSourcePersistenceException ex) when (ex.LocalCommitCompleted)
            {
                SetStatus($"Deleted '{id}' locally, but automatic persistence is NOT armed. Retry the deletion.", true);
            }
            catch (Exception ex)
            {
                SetStatus($"Failed to delete '{id}': {ex.Message}", true);
            }

            RefreshList();
        }

        private void ApplyUpdate(string id)
        {
            if (_destroyed)
            {
                return;
            }

            try
            {
                if (_service.ApplyBundledUpdate(id))
                {
                    SetStatus($"Updated '{id}' from the bundled version.", false);
                }
                else
                {
                    SetStatus($"No bundled update available for '{id}'.", true);
                }
            }
            catch (Exception ex)
            {
                SetStatus($"Failed to update '{id}': {ex.Message}", true);
            }

            RefreshList();
        }

        private void ExportMod(string id)
        {
            try
            {
                string bundle = _service.ExportMod(id);
                if (string.IsNullOrEmpty(bundle))
                {
                    SetStatus($"Nothing to export for '{id}'.", true);
                    return;
                }

                GUIUtility.systemCopyBuffer = bundle;
                SetStatus($"Copied '{id}' export bundle to clipboard.", false);
            }
            catch (Exception ex)
            {
                SetStatus($"Failed to export '{id}': {ex.Message}", true);
            }
        }

        private void ImportFromClipboard()
        {
            if (_destroyed)
            {
                return;
            }

            string clip = GUIUtility.systemCopyBuffer ?? "";
            if (string.IsNullOrWhiteSpace(clip))
            {
                SetStatus("Clipboard is empty — copy an exported mod bundle first.", true);
                return;
            }

            try
            {
                if (_service.ImportMod(clip))
                {
                    SetStatus("Imported mod bundle from clipboard.", false);
                }
                else
                {
                    SetStatus("Import failed: the clipboard does not contain a valid mod bundle.", true);
                }
            }
            catch (Exception ex)
            {
                SetStatus($"Import failed: {ex.Message}", true);
            }

            RefreshList();
        }

        private void OpenNewEditor()
        {
            OpenEditor(new HubModEditorPage(_service, null, NewModSource(), CloseEditor, _editorPreferences));
        }

        /// <summary>The source the "Add" button opens: the service's template for its composition.</summary>
        private string NewModSource()
        {
            string template = _service.NewModTemplate;
            return string.IsNullOrWhiteSpace(template) ? HubModTemplates.Legacy : template;
        }

        private void OpenEditEditor(string id)
        {
            try
            {
                OpenEditor(new HubModEditorPage(_service, id, null, CloseEditor, _editorPreferences));
            }
            catch (Exception ex)
            {
                SetStatus("Could not open the mod source: " + ex.Message, true);
            }
        }

        private void PasteFromClipboard()
        {
            string clip = GUIUtility.systemCopyBuffer ?? "";
            if (string.IsNullOrWhiteSpace(clip))
            {
                SetStatus("Clipboard is empty — copy a Lua mod first.", true);
                return;
            }

            OpenEditor(new HubModEditorPage(_service, null, clip, CloseEditor, _editorPreferences));
        }

        private void OpenEditor(HubModEditorPage editor)
        {
            if (_destroyed || _editorRoot == null)
            {
                return;
            }

            _editor?.OnDestroyed();
            _editor = editor;
            _editorRoot.Clear();
            _editorRoot.Add(editor.Build());
            _editorRoot.style.display = DisplayStyle.Flex;
            _listRoot.style.display = DisplayStyle.None;
            _editorOpen = true;
        }

        private void CloseEditor()
        {
            if (_destroyed || _editorRoot == null)
            {
                return;
            }

            _editor?.OnDestroyed();
            _editor = null;
            _editorRoot.Clear();
            _editorRoot.style.display = DisplayStyle.None;
            _listRoot.style.display = DisplayStyle.Flex;
            _editorOpen = false;
            RefreshList();
        }

        private void OnModsChanged()
        {
            // WHY: Only refresh the list when it is the visible view; the editor manages its own state.
            if (_destroyed || _editorOpen || _root == null)
            {
                return;
            }

            // WHY deferred: ModsChanged is raised on the thread that loaded or unloaded the mod (the
            // manage_mods tool included), and VisualElement mutations must run on the main thread, as
            // the Logs page and the Hub window already marshal their own events.
            // WHY at most one queued: the runtime raises one event per mod, so a rehydrate of N stored
            // mods raised N events and each re-read every mod's source and header and rebuilt the whole
            // tree (30 mods: 30 rebuilds reading 900 records); the one queued rebuild reads the end state.
            if (Interlocked.Exchange(ref _refreshQueued, 1) != 0)
            {
                return;
            }

            if (_deferToPanel != null)
            {
                _deferToPanel(RunQueuedRefresh);
            }
            else
            {
                _root.schedule.Execute(RunQueuedRefresh);
            }
        }

        private void RunQueuedRefresh()
        {
            Interlocked.Exchange(ref _refreshQueued, 0);

            // WHY re-checked: the editor may have opened after the change was queued; closing it refreshes.
            if (_editorOpen || _destroyed)
            {
                return;
            }

            RefreshList();
        }

        private void Subscribe()
        {
            if (_destroyed || _subscribed)
            {
                return;
            }

            _service.ModsChanged += OnModsChanged;
            _subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!_subscribed)
            {
                return;
            }

            _service.ModsChanged -= OnModsChanged;
            _subscribed = false;
        }

        private const string AboutAction = "About";
        private const string EditAction = "Edit";
        private const string ExportAction = "Export";
        private const string UpdateAction = "Apply update";
        private const string DeleteAction = "Delete";
        private const string NewFolderAction = "New folder";
        private const string RenameAction = "Rename";

        private void CacheModDetails()
        {
            _modDetails.Clear();
            foreach (HubModRecord mod in _modsCache ?? Array.Empty<HubModRecord>())
            {
                if (mod != null && !string.IsNullOrEmpty(mod.Id))
                {
                    _modDetails[mod.Id] = BuildMetaLine(mod);
                }
            }
        }

        private string DetailOf(ModFolderModEntry mod)
        {
            return _modDetails.TryGetValue(mod.Id, out string detail) ? detail : "";
        }

        internal static bool IsModActionShown(ModFolderModEntry mod, string action)
        {
            switch (action)
            {
                case UpdateAction:
                    return mod.Payload is HubModRecord updatable && updatable.UpdateAvailable;
                case AboutAction:
                    return mod.Payload is HubModRecord described && !string.IsNullOrWhiteSpace(described.Description);
                default:
                    return true;
            }
        }

        private void OnModAction(ModFolderModEntry mod, string action)
        {
            switch (action)
            {
                case AboutAction:
                    ToggleAbout(mod.Id);
                    break;
                case EditAction:
                    OpenEditEditor(mod.Id);
                    break;
                case ExportAction:
                    ExportMod(mod.Id);
                    break;
                case UpdateAction:
                    ApplyUpdate(mod.Id);
                    break;
                case DeleteAction:
                    DeleteMod(mod.Id);
                    break;
            }
        }

        private void OnFolderAction(ModFolderNode folder, string action)
        {
            switch (action)
            {
                case NewFolderAction:
                    PromptNewFolder(folder.Path);
                    break;
                case RenameAction:
                    ModFolderPath renamed = folder.Path;
                    _treeView?.BeginNamePrompt("Rename '" + renamed.Value + "' to", folder.Name,
                        name => RunFolderOperation(() => _folderService.RenameFolder(renamed.Value, name), true));
                    break;
                case DeleteAction:
                    RunFolderOperation(() => _folderService.DeleteFolder(folder.Path.Value), true);
                    break;
            }
        }

        private void PromptNewFolder(ModFolderPath parent)
        {
            string label = parent.IsRoot ? "New folder" : "New folder in '" + parent.Value + "'";
            _treeView?.BeginNamePrompt(label, "", name =>
                RunFolderOperation(() => _folderService.CreateFolder(parent.IsRoot ? name : parent.Value + "/" + name),
                    false));
        }

        private void OnBranchEnableRequested(ModFolderNode folder, bool enable)
        {
            RunFolderOperation(() => _folderService.SetBranchEnabled(folder.Path.Value, enable), false);
        }

        private void OnMoveRequested(ModFolderMoveRequest request)
        {
            ModFolderResult result = RunFolderOperation(() => request.Kind == ModFolderRowKind.Mod
                ? _folderService.MoveMod(request.ModId, request.Target.Value)
                : _folderService.MoveFolder(request.Folder.Value, request.Target.Value),
                request.Kind == ModFolderRowKind.Folder);
            if (result != null && result.Succeeded && request.Kind == ModFolderRowKind.Mod)
            {
                _treeView?.RevealMod(request.ModId);
            }
        }

        /// <summary>
        /// Runs one folder operation, keeps the fold state on the moved, renamed or deleted branch when
        /// <paramref name="followsFolder"/>, reports the result (the stable code on failure) and refreshes.
        /// </summary>
        internal ModFolderResult RunFolderOperation(Func<ModFolderResult> operation, bool followsFolder)
        {
            if (_destroyed)
            {
                return null;
            }

            ModFolderResult result;
            try
            {
                result = operation();
            }
            catch (OperationCanceledException)
            {
                SetStatus("The folder change was cancelled; some mods may already have changed.", true);
                RefreshList();
                return null;
            }

            if (result.Succeeded && followsFolder && _foldState != null && !result.PreviousPath.IsRoot)
            {
                if (result.Path.IsRoot && result.ModsChanged == 0 && !result.CreatedFolder)
                {
                    _foldState.Forget(result.PreviousPath);
                }
                else if (!result.Path.IsRoot)
                {
                    _foldState.Rebase(result.PreviousPath, result.Path);
                }

                ScheduleFoldSave();
            }

            SetStatus(result.Succeeded
                ? result.Message + (result.Warning.Length == 0 ? "" : " " + result.Warning)
                : result.Code + ": " + result.Message, !result.Succeeded);
            RefreshList();
            return result;
        }

        private void ScheduleFoldSave()
        {
            if (_destroyed || _root == null)
            {
                return;
            }

            _foldSave?.Pause();
            _foldSave = _root.schedule.Execute(SaveFoldStateIfChanged).StartingIn(500);
        }

        /// <summary>Writes the fold state when it changed since the last write.</summary>
        internal void SaveFoldStateIfChanged()
        {
            if (_foldState == null || _foldState.Version == _savedFoldVersion)
            {
                return;
            }

            try
            {
                _folderPreferences.FoldStateText = ModFolderStateCodec.EncodeFoldState(_foldState);
                _savedFoldVersion = _foldState.Version;
            }
            catch (Exception ex)
            {
                SetStatus("Could not save which folders are open: " + ex.Message, true);
            }
        }

        private string SafeFoldStateText()
        {
            try
            {
                return _folderPreferences.FoldStateText ?? "";
            }
            catch (Exception)
            {
                return "";
            }
        }

        private void SetStatus(string message, bool error)
        {
            if (_status == null)
            {
                return;
            }

            _status.text = message;
            _status.style.color = error ? HubModWidgets.Danger : HubModWidgets.Accent;
        }

        private static void SetPlaceholder(TextField field, string placeholder)
        {
            // WHY: UI Toolkit's TextField has no native placeholder, so fake one: show greyed hint text
            // whenever the field is empty and unfocused, swap to the real (initially empty) value and
            // normal color on focus, and restore the hint if the field is left empty again.
            bool showingPlaceholder = string.IsNullOrEmpty(field.value);

            void ShowPlaceholder()
            {
                showingPlaceholder = true;
                field.SetValueWithoutNotify(placeholder);
                field.style.color = HubModWidgets.Muted;
            }

            void ShowRealValue()
            {
                showingPlaceholder = false;
                field.SetValueWithoutNotify("");
                field.style.color = HubModWidgets.Text;
            }

            if (showingPlaceholder)
            {
                ShowPlaceholder();
            }

            field.RegisterCallback<FocusInEvent>(_ =>
            {
                if (showingPlaceholder)
                {
                    ShowRealValue();
                }
            });

            field.RegisterCallback<FocusOutEvent>(_ =>
            {
                if (string.IsNullOrEmpty(field.value))
                {
                    ShowPlaceholder();
                }
            });
        }
    }
}
