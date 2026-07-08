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
                RefreshTree();
            });
            toolbar.Add(_searchField);

            toolbar.Add(HubModWidgets.MakeButton("Refresh", RefreshList));
            toolbar.Add(HubModWidgets.MakeButton("Add", OpenNewEditor));
            toolbar.Add(HubModWidgets.MakeButton("Paste", PasteFromClipboard));
            listRoot.Add(toolbar);

            _status = HubModWidgets.MakeStatus();
            listRoot.Add(_status);

            _treeScroll = new ScrollView(ScrollViewMode.Vertical) { name = "coreai-hub-mods-tree" };
            _treeScroll.style.flexGrow = 1f;
            listRoot.Add(_treeScroll);

            return listRoot;
        }

        private void RefreshList()
        {
            if (_root == null)
            {
                return;
            }

            RefreshTree();
        }

        private void RefreshTree()
        {
            if (_treeScroll == null)
            {
                return;
            }

            _treeScroll.Clear();

            IReadOnlyList<HubModRecord> mods;
            try
            {
                mods = _service.ListMods();
            }
            catch (Exception ex)
            {
                _treeScroll.Add(HubModWidgets.MakeNote($"Failed to list mods: {ex.Message}"));
                return;
            }

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

            // Group into a category tree. Foldouts keyed by the manifest Category (or "Uncategorized").
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
                foldout.style.color = HubModWidgets.Accent;
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
            toggle.style.marginRight = 6f;
            toggle.SetEnabled(_service.IsSupported);
            toggle.RegisterValueChangedCallback(evt => ToggleMod(mod.Id, evt.newValue));
            top.Add(toggle);

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
            top.Add(HubModWidgets.MakeDangerButton("Delete", () => DeleteMod(mod.Id)));

            panel.Add(top);

            if (!string.IsNullOrWhiteSpace(mod.Description))
            {
                Label desc = HubModWidgets.MakeMutedLabel(mod.Description);
                desc.style.fontSize = 11f;
                desc.style.marginTop = 2f;
                panel.Add(desc);
            }

            return panel;
        }

        private static string BuildMetaLine(HubModRecord mod)
        {
            string id = $"id: {mod.Id}";
            string caps = string.IsNullOrWhiteSpace(mod.Capabilities) ? "" : $"  caps: {mod.Capabilities}";
            string status = mod.IsLoaded
                ? $"  loaded  handlers: {mod.Handlers}  timers: {mod.Timers}  errors: {mod.Errors}"
                : (mod.IsStored ? "  stored (disabled)" : "");
            string version = string.IsNullOrWhiteSpace(mod.Version) ? "" : $"  v{mod.Version}";
            return id + version + caps + status;
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

            RefreshTree();
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

            RefreshTree();
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
            RefreshTree();
        }

        private void OnModsChanged()
        {
            // Only refresh the list when it is the visible view; the editor manages its own state.
            if (_editorOpen)
            {
                return;
            }

            RefreshTree();
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
            // Lightweight placeholder: shows greyed hint text until the user types.
            field.tooltip = placeholder;
        }
    }
}
