using System;
using System.Collections.Generic;
using CoreAI.Hub;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.Ai.Hub
{
    /// <summary>
    /// UI Toolkit Hub page that replaces the old IMGUI mod panel. Lists every known mod grouped into a
    /// category tree with a name/category/tags search box; each row exposes an enable/disable toggle,
    /// Edit (opens the inline <see cref="HubModEditorPage"/>), Delete, and live status (loaded,
    /// handlers, timers, errors). An "Add" button opens the editor with a starter template and "Paste"
    /// creates a mod from the Lua in <see cref="GUIUtility.systemCopyBuffer"/>. The list live-refreshes
    /// on <see cref="IHubModService.ModsChanged"/> (fired when a mod is loaded/unloaded/deleted — by the
    /// UI or the AI's manage_mods tool) and via a manual Refresh button. Content is built lazily in
    /// <see cref="CreatePageContent"/>; everything is retained-mode (no OnGUI).
    /// </summary>
    public sealed class HubModsPage : IHubPage
    {
        /// <summary>Default registry id for the Mods page.</summary>
        public const string DefaultPageId = "coreai.hub.mods";

        private readonly IHubModService _service;

        private VisualElement _root;
        private VisualElement _listRoot;
        private VisualElement _editorRoot;
        private ScrollView _treeScroll;
        private Label _status;
        private TextField _searchField;
        private string _search = "";
        private bool _subscribed;
        private bool _editorOpen;

        // TODO: OPT#7: ListMods() re-parses every mod's @coreai header, so it is cached and only reloaded on
        // Refresh / ModsChanged / a mutating action — typing in the search box just re-filters the cache.
        private IReadOnlyList<HubModRecord> _modsCache;
        private string _modsLoadError;
        private IVisualElementScheduledItem _searchDebounce;

        /// <param name="service">VM-agnostic mod CRUD/query surface (built by <see cref="HubModsPages"/>).</param>
        /// <param name="order">Sort priority for the Hub tab (defaults to 300).</param>
        public HubModsPage(IHubModService service, int order = 300)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            Order = order;
        }

        /// <inheritdoc />
        public string PageId => DefaultPageId;

        /// <inheritdoc />
        public string DisplayName => "Mods";

        /// <inheritdoc />
        public int Order { get; }

        /// <inheritdoc />
        public Func<object> CreatePageContent => Build;

        /// <inheritdoc />
        public void OnActivated()
        {
            Subscribe();
            RefreshList();
        }

        /// <inheritdoc />
        public void OnDeactivated()
        {
            Unsubscribe();
        }

        /// <inheritdoc />
        public void OnDestroyed()
        {
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
            SetPlaceholder(_searchField, "Search name / category / tags");
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
            listRoot.Add(toolbar);

            _status = HubModWidgets.MakeStatus();
            listRoot.Add(_status);

            _treeScroll = new ScrollView(ScrollViewMode.Vertical) { name = "coreai-hub-mods-tree" };
            _treeScroll.style.flexGrow = 1f;
            listRoot.Add(_treeScroll);

            return listRoot;
        }

        /// <summary>Reloads the mods cache from the service, then re-renders. Use after any mutation or
        /// external change (ModsChanged); the search box itself only calls <see cref="RenderTree"/>.</summary>
        private void RefreshList()
        {
            if (_root == null)
            {
                return;
            }

            ReloadModsCache();
            RenderTree();
        }

        private void ReloadModsCache()
        {
            try
            {
                _modsCache = _service.ListMods();
                _modsLoadError = null;
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
            _searchDebounce = _searchField.schedule.Execute(RenderTree).StartingIn(250);
        }

        /// <summary>Filters the cached mod list and rebuilds the tree. Never calls the service — callers
        /// that need fresh data should call <see cref="RefreshList"/> instead.</summary>
        private void RenderTree()
        {
            if (_treeScroll == null)
            {
                return;
            }

            _treeScroll.Clear();

            if (_modsLoadError != null)
            {
                _treeScroll.Add(HubModWidgets.MakeNote($"Failed to list mods: {_modsLoadError}"));
                return;
            }

            IReadOnlyList<HubModRecord> mods = _modsCache ?? Array.Empty<HubModRecord>();
            List<HubModRecord> filtered = new();
            foreach (HubModRecord mod in mods)
            {
                if (Matches(mod, _search))
                {
                    filtered.Add(mod);
                }
            }

            if (filtered.Count == 0)
            {
                _treeScroll.Add(HubModWidgets.MakeNote(
                    mods.Count == 0 ? "No mods yet. Use Add or Paste to create one." : "No mods match the search."));
                return;
            }

            // WHY: Group into a category tree. Foldouts keyed by the manifest Category (or "Uncategorized").
            SortedDictionary<string, List<HubModRecord>> groups =
                new(StringComparer.OrdinalIgnoreCase);
            foreach (HubModRecord mod in filtered)
            {
                string category = HubModServiceBase.CategoryOrDefault(mod.Category);
                if (!groups.TryGetValue(category, out List<HubModRecord> bucket))
                {
                    bucket = new List<HubModRecord>();
                    groups[category] = bucket;
                }

                bucket.Add(mod);
            }

            foreach (KeyValuePair<string, List<HubModRecord>> group in groups)
            {
                Foldout foldout = new() { text = $"{group.Key}  ({group.Value.Count})", value = true };
                foldout.style.marginBottom = 4f;
                HubModWidgets.StyleFoldoutTitle(foldout);
                foreach (HubModRecord mod in group.Value)
                {
                    foldout.Add(BuildModRow(mod));
                }

                _treeScroll.Add(foldout);
            }
        }

        private VisualElement BuildModRow(HubModRecord mod)
        {
            VisualElement panel = HubModWidgets.MakePanel();

            VisualElement top = new();
            top.style.flexDirection = FlexDirection.Row;
            top.style.alignItems = Align.Center;

            Toggle toggle = new() { value = mod.IsLoaded };
            toggle.tooltip = "Enable / disable (load / unload) the mod.";
            toggle.style.marginRight = 4f;
            toggle.SetEnabled(_service.IsSupported);
            toggle.RegisterValueChangedCallback(evt => ToggleMod(mod.Id, evt.newValue));
            top.Add(toggle);

            // WHY: a bare checkbox did not read as "on/off" at a glance (the state only showed on hover
            // or in the meta line), so label it explicitly next to the toggle.
            Label state = HubModWidgets.MakeFieldLabel(mod.IsLoaded ? "On" : "Off");
            state.style.unityFontStyleAndWeight = FontStyle.Bold;
            state.style.color = mod.IsLoaded ? HubModWidgets.Accent : HubModWidgets.Muted;
            state.style.width = 30f;
            state.style.marginRight = 8f;
            top.Add(state);

            VisualElement nameCol = new();
            nameCol.style.flexGrow = 1f;
            nameCol.style.flexShrink = 1f;

            Label name = HubModWidgets.MakeFieldLabel(
                string.IsNullOrWhiteSpace(mod.Name) ? mod.Id : mod.Name);
            name.style.unityFontStyleAndWeight = FontStyle.Bold;
            nameCol.Add(name);

            Label meta = HubModWidgets.MakeMutedLabel(BuildMetaLine(mod));
            meta.style.fontSize = 11f;
            nameCol.Add(meta);
            top.Add(nameCol);

            top.Add(HubModWidgets.MakeButton("Edit", () => OpenEditEditor(mod.Id)));
            top.Add(HubModWidgets.MakeButton("Export", () => ExportMod(mod.Id)));
            top.Add(HubModWidgets.MakeDangerButton("Delete", () => DeleteMod(mod.Id)));

            panel.Add(top);

            if (!string.IsNullOrWhiteSpace(mod.Description))
            {
                Label desc = HubModWidgets.MakeMutedLabel(mod.Description);
                desc.style.fontSize = 11f;
                desc.style.marginTop = 2f;
                panel.Add(desc);
            }

            if (mod.UpdateAvailable)
            {
                VisualElement updateRow = new();
                updateRow.style.flexDirection = FlexDirection.Row;
                updateRow.style.alignItems = Align.Center;
                updateRow.style.marginTop = 2f;

                string versionSuffix = string.IsNullOrWhiteSpace(mod.SeededVersion) ? "" : $" (v{mod.SeededVersion})";
                Label badge = HubModWidgets.MakeMutedLabel($"Update available{versionSuffix}");
                badge.style.color = HubModWidgets.Accent;
                badge.style.unityFontStyleAndWeight = FontStyle.Bold;
                badge.style.marginRight = 6f;
                updateRow.Add(badge);

                updateRow.Add(HubModWidgets.MakeButton("Apply update", () => ApplyUpdate(mod.Id)));
                panel.Add(updateRow);
            }

            return panel;
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
            return id + version + caps + status + bundled;
        }

        private static bool Matches(HubModRecord mod, string search)
        {
            if (string.IsNullOrWhiteSpace(search))
            {
                return true;
            }

            string needle = search.Trim();
            return Contains(mod.Name, needle) ||
                   Contains(mod.Id, needle) ||
                   Contains(mod.Category, needle) ||
                   Contains(mod.Tags, needle);
        }

        private static bool Contains(string haystack, string needle)
        {
            return !string.IsNullOrEmpty(haystack) &&
                   haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void ToggleMod(string id, bool enable)
        {
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
            catch (Exception ex)
            {
                SetStatus($"Failed to toggle '{id}': {ex.Message}", true);
            }

            RefreshList();
        }

        private void DeleteMod(string id)
        {
            try
            {
                _service.Delete(id);
                SetStatus($"Deleted '{id}'.", false);
            }
            catch (Exception ex)
            {
                SetStatus($"Failed to delete '{id}': {ex.Message}", true);
            }

            RefreshList();
        }

        private void ApplyUpdate(string id)
        {
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
            OpenEditor(new HubModEditorPage(_service, null, HubModEditorPage.NewModTemplate, CloseEditor));
        }

        private void OpenEditEditor(string id)
        {
            OpenEditor(new HubModEditorPage(_service, id, null, CloseEditor));
        }

        private void PasteFromClipboard()
        {
            string clip = GUIUtility.systemCopyBuffer ?? "";
            if (string.IsNullOrWhiteSpace(clip))
            {
                SetStatus("Clipboard is empty — copy a Lua mod first.", true);
                return;
            }

            OpenEditor(new HubModEditorPage(_service, null, clip, CloseEditor));
        }

        private void OpenEditor(HubModEditorPage editor)
        {
            if (_editorRoot == null)
            {
                return;
            }

            _editorRoot.Clear();
            _editorRoot.Add(editor.Build());
            _editorRoot.style.display = DisplayStyle.Flex;
            _listRoot.style.display = DisplayStyle.None;
            _editorOpen = true;
        }

        private void CloseEditor()
        {
            if (_editorRoot == null)
            {
                return;
            }

            _editorRoot.Clear();
            _editorRoot.style.display = DisplayStyle.None;
            _listRoot.style.display = DisplayStyle.Flex;
            _editorOpen = false;
            RefreshList();
        }

        private void OnModsChanged()
        {
            // WHY: Only refresh the list when it is the visible view; the editor manages its own state.
            if (_editorOpen)
            {
                return;
            }

            RefreshList();
        }

        private void Subscribe()
        {
            if (_subscribed)
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
