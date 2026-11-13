using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.Mods.Folders
{
    /// <summary>Look and behaviour settings of a <see cref="ModFolderTreeView"/>.</summary>
    public sealed class ModFolderTreeViewOptions
    {
        /// <summary>Row height in pixels; every row has this height, which is what makes the list virtualizable cheaply.</summary>
        public float RowHeight = 26f;

        /// <summary>Indent per folder level, in pixels.</summary>
        public float IndentWidth = 16f;

        /// <summary>Debounce between the last keystroke in the search field and the search, in milliseconds.</summary>
        public long SearchDebounceMilliseconds = 200;

        /// <summary>Whether the view shows its own search field (a host with its own field calls <see cref="ModFolderTreeView.SetSearch"/>).</summary>
        public bool ShowSearchField = true;

        /// <summary>Whether the view shows its "Expand all" and "Collapse all" buttons.</summary>
        public bool ShowFoldButtons = true;

        /// <summary>Placeholder of the search field.</summary>
        public string SearchPlaceholder = "Search name / folder / tags";

        /// <summary>Text shown when the tree holds nothing.</summary>
        public string EmptyText = "No mods yet.";

        /// <summary>Text shown when a search matches nothing.</summary>
        public string NoMatchText = "No mods match the search.";

        /// <summary>Buttons on every mod row; each raises <see cref="ModFolderTreeView.ModActionRequested"/> with its label.</summary>
        public IReadOnlyList<string> ModActions = new[] { "Edit" };

        /// <summary>Buttons on every folder row; each raises <see cref="ModFolderTreeView.FolderActionRequested"/> with its label.</summary>
        public IReadOnlyList<string> FolderActions = new[] { "Rename", "Delete" };

        /// <summary>
        /// Whether a mod row shows the action with the given label (for example an update button only when an
        /// update exists); null shows every action. Runs on every bind, so it must not allocate.
        /// </summary>
        public Func<ModFolderModEntry, string, bool> ModActionFilter;

        /// <summary>
        /// The muted text after a mod's name (for example its status line); null shows the id when it differs
        /// from the name. Runs on every bind: return a string the host cached, not a new one.
        /// </summary>
        public Func<ModFolderModEntry, string> ModDetail;

        /// <summary>Main text colour.</summary>
        public Color TextColor = new(0.86f, 0.88f, 0.92f, 1f);

        /// <summary>Secondary text colour (counts, ids, notes).</summary>
        public Color MutedColor = new(0.55f, 0.58f, 0.64f, 1f);

        /// <summary>Highlight colour (folder names, the move banner).</summary>
        public Color AccentColor = new(0.36f, 0.72f, 1f, 1f);
    }

    /// <summary>A "Move to" choice made in a <see cref="ModFolderTreeView"/>: what moves and the folder it goes into.</summary>
    public readonly struct ModFolderMoveRequest
    {
        /// <summary>Creates a request.</summary>
        public ModFolderMoveRequest(ModFolderRowKind kind, string modId, ModFolderPath folder, ModFolderPath target)
        {
            Kind = kind;
            ModId = modId ?? "";
            Folder = folder;
            Target = target;
        }

        /// <summary>A mod or a folder moves.</summary>
        public ModFolderRowKind Kind { get; }

        /// <summary>The moving mod (for <see cref="ModFolderRowKind.Mod"/>).</summary>
        public string ModId { get; }

        /// <summary>The moving folder (for <see cref="ModFolderRowKind.Folder"/>).</summary>
        public ModFolderPath Folder { get; }

        /// <summary>The folder chosen as the new home; the top level is <see cref="ModFolderPath.Root"/>.</summary>
        public ModFolderPath Target { get; }
    }

    /// <summary>
    /// A collapsible, virtualized mod tree for UI Toolkit, driven only by a <see cref="ModFolderTree"/>: a
    /// <see cref="ListView"/> with fixed-height rows over the tree's flat visible rows, so opening or
    /// scrolling a thousand mods never walks the whole tree and binding a row allocates nothing. Rows show a
    /// fold arrow, an on/off toggle (mixed for a partly enabled folder), the name, the branch counts, a "Move"
    /// button and the host's own actions.
    /// <para>
    /// The view changes nothing by itself: folding and searching change the tree; every edit is raised as an
    /// event (<see cref="ModEnableRequested"/>, <see cref="BranchEnableRequested"/>, <see cref="MoveRequested"/>,
    /// <see cref="ModActionRequested"/>, <see cref="FolderActionRequested"/>) for the host to carry out through
    /// <see cref="ModFolderService"/> and then hand the rebuilt tree back with <see cref="SetTree"/>.
    /// </para>
    /// <para>
    /// "Move to": a row's Move button enters move mode; folder rows that can take the item show "Here", the
    /// banner offers the top level and Cancel, and the choice is raised as <see cref="MoveRequested"/>.
    /// </para>
    /// </summary>
    public sealed class ModFolderTreeView : VisualElement
    {
        /// <summary>USS class of the view.</summary>
        public const string UssClassName = "coreai-mod-tree";

        /// <summary>USS class of every row.</summary>
        public const string RowUssClassName = UssClassName + "__row";

        /// <summary>USS class added to folder rows.</summary>
        public const string FolderRowUssClassName = RowUssClassName + "--folder";

        /// <summary>USS class added to mod rows.</summary>
        public const string ModRowUssClassName = RowUssClassName + "--mod";

        /// <summary>USS class added to disabled mod rows.</summary>
        public const string OffRowUssClassName = RowUssClassName + "--off";

        private const string ExpandedArrow = "▾";
        private const string CollapsedArrow = "▸";

        private readonly ModFolderTreeViewOptions _options;
        private readonly RowList _rows = new();
        private readonly ListView _listView;
        private readonly Label _note;
        private readonly VisualElement _moveBanner;
        private readonly Label _moveLabel;
        private readonly TextField _searchField;
        private readonly IVisualElementScheduledItem _searchItem;
        private readonly VisualElement _prompt;
        private readonly Label _promptLabel;
        private readonly TextField _promptField;
        private Action<string> _promptAccepted;

        private ModFolderTree _tree;
        private string _search = "";
        private string _pendingSearch = "";
        private bool _moving;
        private ModFolderRowKind _movingKind;
        private string _movingModId = "";
        private ModFolderPath _movingFolder;
        private ModFolderPath _movingFrom;
        private readonly List<RowElements> _madeRows = new();
        private bool _twoLineRows;

        /// <param name="options">Look and behaviour; null uses the defaults.</param>
        public ModFolderTreeView(ModFolderTreeViewOptions options = null)
        {
            _options = options ?? new ModFolderTreeViewOptions();
            AddToClassList(UssClassName);
            style.flexGrow = 1f;
            style.flexDirection = FlexDirection.Column;

            VisualElement toolbar = new() { name = "coreai-mod-tree-toolbar" };
            toolbar.style.flexDirection = FlexDirection.Row;
            toolbar.style.flexShrink = 0f;
            toolbar.style.marginBottom = 4f;
            if (_options.ShowSearchField)
            {
                _searchField = new TextField { name = "coreai-mod-tree-search" };
                _searchField.style.flexGrow = 1f;
                _searchField.style.minWidth = 120f;
                _searchField.textEdition.placeholder = _options.SearchPlaceholder;
                _searchField.RegisterValueChangedCallback(OnSearchFieldChanged);
                toolbar.Add(_searchField);
                _searchItem = schedule.Execute(ApplyPendingSearch);
                _searchItem.Pause();
            }

            if (_options.ShowFoldButtons)
            {
                toolbar.Add(MakeButton("Expand all", () => SetAllExpanded(true), "Open every folder."));
                toolbar.Add(MakeButton("Collapse all", () => SetAllExpanded(false), "Close every folder."));
            }

            if (toolbar.childCount > 0)
            {
                Add(toolbar);
            }

            _moveBanner = new VisualElement { name = "coreai-mod-tree-move" };
            _moveBanner.style.flexDirection = FlexDirection.Row;
            _moveBanner.style.alignItems = Align.Center;
            _moveBanner.style.flexShrink = 0f;
            _moveBanner.style.display = DisplayStyle.None;
            _moveLabel = new Label();
            _moveLabel.style.flexGrow = 1f;
            _moveLabel.style.color = _options.AccentColor;
            _moveBanner.Add(_moveLabel);
            _moveBanner.Add(MakeButton("Top level", () => CompleteMoveTo(ModFolderPath.Root),
                "Move to the top level, outside every folder."));
            _moveBanner.Add(MakeButton("Cancel", CancelMove, "Leave the move without moving anything."));
            Add(_moveBanner);

            _prompt = new VisualElement { name = "coreai-mod-tree-prompt" };
            _prompt.style.flexDirection = FlexDirection.Row;
            _prompt.style.alignItems = Align.Center;
            _prompt.style.flexShrink = 0f;
            _prompt.style.display = DisplayStyle.None;
            _promptLabel = new Label();
            _promptLabel.style.color = _options.AccentColor;
            _promptLabel.style.marginRight = 6f;
            _prompt.Add(_promptLabel);
            _promptField = new TextField { name = "coreai-mod-tree-prompt-field" };
            _promptField.style.flexGrow = 1f;
            _promptField.style.minWidth = 120f;
            _promptField.RegisterCallback<KeyDownEvent>(OnPromptKeyDown);
            _prompt.Add(_promptField);
            _prompt.Add(MakeButton("OK", AcceptPrompt, "Use this name."));
            _prompt.Add(MakeButton("Cancel", CancelPrompt, "Close without changing anything."));
            Add(_prompt);

            _note = new Label { name = "coreai-mod-tree-note" };
            _note.style.color = _options.MutedColor;
            _note.style.display = DisplayStyle.None;
            Add(_note);

            _listView = new ListView
            {
                name = "coreai-mod-tree-list",
                fixedItemHeight = _options.RowHeight,
                virtualizationMethod = CollectionVirtualizationMethod.FixedHeight,
                selectionType = SelectionType.None,
                makeItem = MakeRow,
                bindItem = BindRow,
                itemsSource = _rows
            };
            _listView.style.flexGrow = 1f;
            _listView.RegisterCallback<GeometryChangedEvent>(_ => UpdateTwoLineRows());
            Add(_listView);
        }

        /// <summary>Raised when a mod's toggle asks to switch it on (true) or off.</summary>
        public event Action<ModFolderModEntry, bool> ModEnableRequested;

        /// <summary>Raised when a folder's toggle asks to switch its whole branch on (true) or off.</summary>
        public event Action<ModFolderNode, bool> BranchEnableRequested;

        /// <summary>Raised when a "Move to" choice is made.</summary>
        public event Action<ModFolderMoveRequest> MoveRequested;

        /// <summary>Raised by a mod row's host action button, with the button's label.</summary>
        public event Action<ModFolderModEntry, string> ModActionRequested;

        /// <summary>Raised by a folder row's host action button, with the button's label.</summary>
        public event Action<ModFolderNode, string> FolderActionRequested;

        /// <summary>Raised after a fold changed outside a search, so the host can persist <see cref="ModFolderTree.FoldState"/>.</summary>
        public event Action FoldChanged;

        /// <summary>The tree shown; null before the first <see cref="SetTree"/>.</summary>
        public ModFolderTree Tree => _tree;

        /// <summary>The applied search text.</summary>
        public string SearchText => _search;

        /// <summary>True while a "Move to" choice is pending.</summary>
        public bool IsMoving => _moving;

        /// <summary>True while a name prompt is open.</summary>
        public bool IsPrompting => _promptAccepted != null;

        /// <summary>The name prompt's text field.</summary>
        internal TextField PromptField => _promptField;

        /// <summary>
        /// Opens an inline one-line prompt (for a new folder or a rename) above the list. OK or Enter hands
        /// the typed text to <paramref name="accepted"/> unchanged; the host validates it through
        /// <see cref="ModFolderService"/> and shows the named error. Cancel or Escape closes it.
        /// </summary>
        public void BeginNamePrompt(string label, string initialText, Action<string> accepted)
        {
            _promptAccepted = accepted ?? throw new ArgumentNullException(nameof(accepted));
            _promptLabel.text = label ?? "";
            _promptField.SetValueWithoutNotify(initialText ?? "");
            _prompt.style.display = DisplayStyle.Flex;
            _promptField.Focus();
        }

        /// <summary>Closes the name prompt and hands its text to the waiting callback.</summary>
        public void AcceptPrompt()
        {
            Action<string> accepted = _promptAccepted;
            if (accepted == null)
            {
                return;
            }

            string text = _promptField.value ?? "";
            ClosePrompt();
            accepted(text);
        }

        /// <summary>Closes the name prompt without calling back.</summary>
        public void CancelPrompt()
        {
            ClosePrompt();
        }

        /// <summary>The list that virtualizes the rows.</summary>
        internal ListView ListView => _listView;

        /// <summary>The view's own search field; null when <see cref="ModFolderTreeViewOptions.ShowSearchField"/> is off.</summary>
        internal TextField SearchField => _searchField;

        /// <summary>
        /// Shows <paramref name="tree"/> with the current search applied, keeping the move in progress when the
        /// moving item still exists.
        /// </summary>
        public void SetTree(ModFolderTree tree)
        {
            _tree = tree;
            _tree?.SetSearch(_search);
            if (_moving && !MovingItemExists())
            {
                EndMove();
            }

            Refresh();
        }

        /// <summary>Applies a search now (the search field debounces into this).</summary>
        public void SetSearch(string text)
        {
            _search = (text ?? "").Trim();
            _tree?.SetSearch(_search);
            Refresh();
        }

        /// <summary>Starts a "Move to" choice for a mod.</summary>
        public void BeginMoveMod(string modId)
        {
            if (_tree == null || !_tree.TryFindMod(modId, out int index))
            {
                return;
            }

            _moving = true;
            _movingKind = ModFolderRowKind.Mod;
            _movingModId = _tree.GetMod(index).Id;
            _movingFrom = _tree.GetFolder(_tree.GetModFolderIndex(index)).Path;
            _movingFolder = ModFolderPath.Root;
            _moveLabel.text = "Moving mod '" + _tree.GetMod(index).Name + "': choose a folder (Here) or the top level.";
            ShowMoveBanner();
        }

        /// <summary>Starts a "Move to" choice for a folder.</summary>
        public void BeginMoveFolder(ModFolderPath folder)
        {
            if (_tree == null || folder.IsRoot || !_tree.TryFindFolder(folder.Value, out int index))
            {
                return;
            }

            ModFolderNode node = _tree.GetFolder(index);
            _moving = true;
            _movingKind = ModFolderRowKind.Folder;
            _movingModId = "";
            _movingFolder = node.Path;
            _movingFrom = node.Path.Parent;
            _moveLabel.text = "Moving folder '" + node.Path.Value + "': choose a folder (Here) or the top level.";
            ShowMoveBanner();
        }

        /// <summary>Leaves move mode without moving anything.</summary>
        public void CancelMove()
        {
            if (_moving)
            {
                EndMove();
                Refresh();
            }
        }

        /// <summary>
        /// Finishes the pending "Move to" with <paramref name="target"/>: raises <see cref="MoveRequested"/> when
        /// the target can take the item and leaves move mode. Returns false (and stays in move mode) for a target
        /// that cannot (the moving folder itself or one of its subfolders).
        /// </summary>
        public bool CompleteMoveTo(ModFolderPath target)
        {
            if (!_moving || !CanTakeMovingItem(target))
            {
                return false;
            }

            ModFolderMoveRequest request = new(_movingKind, _movingModId, _movingFolder, target);
            EndMove();
            Refresh();
            MoveRequested?.Invoke(request);
            return true;
        }

        /// <summary>Opens or closes a folder.</summary>
        public void ToggleFolder(int folderIndex)
        {
            if (_tree == null)
            {
                return;
            }

            _tree.ToggleExpanded(folderIndex);
            Refresh();
            if (!_tree.IsSearching)
            {
                FoldChanged?.Invoke();
            }
        }

        /// <summary>Opens the folders above a mod and scrolls to it.</summary>
        public void RevealMod(string modId)
        {
            if (_tree == null || !_tree.TryFindMod(modId, out int index))
            {
                return;
            }

            int before = _tree.FoldState.Version;
            _tree.Reveal(_tree.GetModFolderIndex(index));
            _tree.SetExpanded(_tree.GetModFolderIndex(index), true);
            Refresh();
            int row = _tree.FindVisibleRow(ModFolderRowKind.Mod, index);
            if (row >= 0)
            {
                _listView.ScrollToItem(row);
            }

            if (_tree.FoldState.Version != before)
            {
                FoldChanged?.Invoke();
            }
        }

        /// <summary>Re-reads the tree's visible rows into the list (after the tree changed outside the view).</summary>
        public void Refresh()
        {
            int count = _tree?.VisibleRowCount ?? 0;
            _rows.SetCount(count);
            _listView.RefreshItems();
            if (count > 0)
            {
                _note.style.display = DisplayStyle.None;
                _listView.style.display = DisplayStyle.Flex;
                return;
            }

            _note.text = _tree != null && _tree.IsSearching && (_tree.ModCount > 0 || _tree.FolderCount > 1)
                ? _options.NoMatchText
                : _options.EmptyText;
            _note.style.display = DisplayStyle.Flex;
            _listView.style.display = DisplayStyle.None;
        }

        /// <summary>Creates one pooled row element; called by the list only for the rows on screen.</summary>
        internal VisualElement MakeRow()
        {
            RowElements row = new();
            VisualElement root = row.Root;
            root.AddToClassList(RowUssClassName);
            root.userData = row;
            root.style.flexDirection = FlexDirection.Row;
            root.style.alignItems = Align.Center;
            root.style.height = _options.RowHeight;
            root.style.overflow = Overflow.Hidden;

            row.Indent = new VisualElement();
            row.Indent.style.flexShrink = 0f;
            root.Add(row.Indent);

            row.Fold = MakeButton(CollapsedArrow, () => OnFoldClicked(row), "Open or close this folder.");
            row.Fold.style.width = 22f;
            row.Fold.style.flexShrink = 0f;
            root.Add(row.Fold);

            row.Toggle = new Toggle();
            row.Toggle.style.flexShrink = 0f;
            row.Toggle.style.marginRight = 4f;
            row.Toggle.RegisterValueChangedCallback(evt => OnToggleChanged(row, evt.newValue));
            root.Add(row.Toggle);

            // WHY the name and the detail shrink and the buttons do not: a long status line ("id: ... caps: ... stored
            // (disabled) [bundled]") used to keep its whole width and push the row's last buttons past the panel's edge,
            // where the row clips them. The labels now give way, cut with an ellipsis, and show the whole text as a tooltip.
            row.Name = new Label();
            row.Name.style.flexGrow = 1f;
            row.Name.style.flexShrink = 1f;
            row.Name.style.minWidth = NameMinWidth;
            row.Name.style.overflow = Overflow.Hidden;
            row.Name.style.whiteSpace = WhiteSpace.NoWrap;
            row.Name.style.textOverflow = TextOverflow.Ellipsis;
            row.Name.style.unityTextAlign = TextAnchor.MiddleLeft;
            root.Add(row.Name);

            row.Detail = new Label();
            row.Detail.style.flexShrink = DetailShrink;
            row.Detail.style.minWidth = 0f;
            row.Detail.style.overflow = Overflow.Hidden;
            row.Detail.style.whiteSpace = WhiteSpace.NoWrap;
            row.Detail.style.textOverflow = TextOverflow.Ellipsis;
            row.Detail.style.marginLeft = 6f;
            row.Detail.style.marginRight = 6f;
            row.Detail.style.color = _options.MutedColor;
            root.Add(row.Detail);

            row.Actions = new VisualElement { name = "coreai-mod-tree-row-actions" };
            row.Actions.style.flexDirection = FlexDirection.Row;
            row.Actions.style.alignItems = Align.Center;
            row.Actions.style.flexShrink = 0f;
            root.Add(row.Actions);

            row.Here = MakeButton("Here", () => OnHereClicked(row), "Move the chosen item into this folder.");
            row.Actions.Add(row.Here);

            row.Move = MakeButton("Move", () => OnMoveClicked(row), "Move to another folder.");
            row.Actions.Add(row.Move);

            IReadOnlyList<string> folderActions = _options.FolderActions ?? Array.Empty<string>();
            row.FolderActions = new Button[folderActions.Count];
            for (int i = 0; i < folderActions.Count; i++)
            {
                string action = folderActions[i];
                row.FolderActions[i] = MakeButton(action, () => OnFolderAction(row, action), null);
                row.Actions.Add(row.FolderActions[i]);
            }

            IReadOnlyList<string> modActions = _options.ModActions ?? Array.Empty<string>();
            row.ModActions = new Button[modActions.Count];
            row.ModActionLabels = new string[modActions.Count];
            for (int i = 0; i < modActions.Count; i++)
            {
                string action = modActions[i];
                row.ModActionLabels[i] = action;
                row.ModActions[i] = MakeButton(action, () => OnModAction(row, action), null);
                row.Actions.Add(row.ModActions[i]);
            }

            row.Actions.RegisterCallback<GeometryChangedEvent>(_ => UpdateTwoLineRows());
            // WHY followed: a list rebuild drops its rows, and a row kept here would never be freed.
            root.RegisterCallback<DetachFromPanelEvent>(_ => _madeRows.Remove(row));
            root.RegisterCallback<AttachToPanelEvent>(_ =>
            {
                if (!_madeRows.Contains(row))
                {
                    _madeRows.Add(row);
                    ApplyRowLayout(row);
                }
            });
            ApplyRowLayout(row);
            _madeRows.Add(row);
            return root;
        }

        /// <summary>The narrowest a row's name gets before its buttons move to a second line.</summary>
        internal const float NameMinWidth = 48f;

        /// <summary>How much faster the status line gives way than the name when a row is short of room.</summary>
        internal const float DetailShrink = 4f;

        /// <summary>How much wider than it needs the view must be before two-line rows go back to one line.</summary>
        private const float OneLineHysteresis = 16f;

        /// <summary>
        /// True while the rows are two lines high, the buttons on the second: the view is narrower than the widest row's
        /// fixed parts, buttons and <see cref="NameMinWidth"/> together.
        /// </summary>
        internal bool UsesTwoLineRows => _twoLineRows;

        /// <summary>
        /// Whether rows should be two lines high in a view <paramref name="available"/> wide when one line needs
        /// <paramref name="required"/>; <paramref name="twoLine"/> is the current choice, kept within the hysteresis.
        /// </summary>
        internal static bool NeedsTwoLineRows(float available, float required, bool twoLine)
        {
            if (float.IsNaN(available) || float.IsNaN(required) || available <= 0f || required <= 0f)
            {
                return twoLine;
            }

            return twoLine ? available < required + OneLineHysteresis : available < required;
        }

        /// <summary>
        /// Puts every row on two lines (the buttons under the name) or back on one. WHY two lines of a fixed height and
        /// not wrapping rows: the list stays virtualized with fixed-height rows, and a narrow Hub still reaches every
        /// button.
        /// </summary>
        internal void SetTwoLineRows(bool twoLine)
        {
            if (_twoLineRows == twoLine)
            {
                return;
            }

            _twoLineRows = twoLine;
            _listView.fixedItemHeight = twoLine ? _options.RowHeight * 2f : _options.RowHeight;
            for (int i = 0; i < _madeRows.Count; i++)
            {
                ApplyRowLayout(_madeRows[i]);
            }

            _listView.RefreshItems();
        }

        private void ApplyRowLayout(RowElements row)
        {
            VisualElement root = row.Root;
            root.style.height = _twoLineRows ? _options.RowHeight * 2f : _options.RowHeight;
            root.style.flexWrap = _twoLineRows ? Wrap.Wrap : Wrap.NoWrap;
            root.style.alignContent = Align.Stretch;
            row.Actions.style.width = _twoLineRows ? Length.Percent(100f) : new StyleLength(StyleKeyword.Auto);
            row.Actions.style.paddingLeft = _twoLineRows ? 22f : 0f;
        }

        /// <summary>Measures what one line needs against the view's width and picks one- or two-line rows.</summary>
        private void UpdateTwoLineRows()
        {
            float required = 0f;
            for (int i = 0; i < _madeRows.Count; i++)
            {
                RowElements row = _madeRows[i];
                if (row.Root.panel == null || row.Root.resolvedStyle.display == DisplayStyle.None)
                {
                    continue;
                }

                float width = OuterWidth(row.Indent) + OuterWidth(row.Fold) + OuterWidth(row.Toggle) + NameMinWidth;
                for (int child = 0; child < row.Actions.childCount; child++)
                {
                    width += OuterWidth(row.Actions[child]);
                }

                if (width > required)
                {
                    required = width;
                }
            }

            SetTwoLineRows(NeedsTwoLineRows(_listView.contentRect.width, required, _twoLineRows));
        }

        private static float OuterWidth(VisualElement element)
        {
            if (element == null || element.resolvedStyle.display == DisplayStyle.None)
            {
                return 0f;
            }

            float width = element.layout.width;
            if (float.IsNaN(width))
            {
                return 0f;
            }

            return width + element.resolvedStyle.marginLeft + element.resolvedStyle.marginRight;
        }

        /// <summary>Binds a pooled row element to visible row <paramref name="index"/>; allocates nothing.</summary>
        internal void BindRow(VisualElement element, int index)
        {
            if (!(element.userData is RowElements row) || _tree == null || index < 0 || index >= _tree.VisibleRowCount)
            {
                return;
            }

            ModFolderRow data = _tree.GetVisibleRow(index);
            row.Kind = data.Kind;
            row.Index = data.Index;
            row.Indent.style.width = data.Depth * _options.IndentWidth;
            if (data.IsFolder)
            {
                BindFolder(row, data.Index);
            }
            else
            {
                BindMod(row, data.Index);
            }
        }

        private void BindFolder(RowElements row, int folderIndex)
        {
            ModFolderNode node = _tree.GetFolder(folderIndex);
            VisualElement root = row.Root;
            root.AddToClassList(FolderRowUssClassName);
            root.RemoveFromClassList(ModRowUssClassName);
            root.RemoveFromClassList(OffRowUssClassName);

            bool hasChildren = node.SubfolderCount > 0 || node.DirectModCount > 0;
            row.Fold.style.visibility = hasChildren ? Visibility.Visible : Visibility.Hidden;
            row.Fold.text = _tree.IsExpanded(folderIndex) ? ExpandedArrow : CollapsedArrow;

            row.Toggle.SetEnabled(node.ModCount > 0 && !_moving);
            row.Toggle.showMixedValue = node.EnabledModCount > 0 && node.EnabledModCount < node.ModCount;
            row.Toggle.SetValueWithoutNotify(node.ModCount > 0 && node.EnabledModCount == node.ModCount);

            row.Name.text = node.Name;
            row.Name.tooltip = node.Name;
            row.Name.style.color = _options.AccentColor;
            row.Detail.text = _tree.GetCountText(folderIndex);
            row.Detail.tooltip = row.Detail.text;

            bool here = _moving && CanTakeMovingItem(node.Path) && !IsMovingFrom(node.Path);
            SetShown(row.Here, here);
            SetShown(row.Move, !_moving);
            SetShownAll(row.FolderActions, !_moving);
            SetShownAll(row.ModActions, false);
        }

        private void BindMod(RowElements row, int modIndex)
        {
            ModFolderModEntry mod = _tree.GetMod(modIndex);
            VisualElement root = row.Root;
            root.AddToClassList(ModRowUssClassName);
            root.RemoveFromClassList(FolderRowUssClassName);
            root.EnableInClassList(OffRowUssClassName, !mod.Enabled);

            row.Fold.style.visibility = Visibility.Hidden;
            row.Toggle.SetEnabled(!_moving);
            row.Toggle.showMixedValue = false;
            row.Toggle.SetValueWithoutNotify(mod.Enabled);

            row.Name.text = mod.Name;
            row.Name.style.color = mod.Enabled ? _options.TextColor : _options.MutedColor;
            row.Name.tooltip = mod.Name;
            row.Detail.text = _options.ModDetail != null
                ? _options.ModDetail(mod) ?? ""
                : string.Equals(mod.Name, mod.Id, StringComparison.Ordinal) ? "" : mod.Id;
            row.Detail.tooltip = row.Detail.text;

            bool moving = _moving && _movingKind == ModFolderRowKind.Mod &&
                          string.Equals(_movingModId, mod.Id, StringComparison.Ordinal);
            SetShown(row.Here, false);
            SetShown(row.Move, !_moving);
            SetShownAll(row.FolderActions, false);
            Func<ModFolderModEntry, string, bool> filter = _options.ModActionFilter;
            for (int i = 0; i < row.ModActions.Length; i++)
            {
                SetShown(row.ModActions[i], !_moving && (filter == null || filter(mod, row.ModActionLabels[i])));
            }

            row.Name.style.unityFontStyleAndWeight = moving ? FontStyle.Bold : FontStyle.Normal;
        }

        internal void OnFoldClicked(RowElements row)
        {
            if (row.Kind == ModFolderRowKind.Folder)
            {
                ToggleFolder(row.Index);
            }
        }

        internal void OnToggleChanged(RowElements row, bool value)
        {
            if (_tree == null || _moving)
            {
                return;
            }

            if (row.Kind == ModFolderRowKind.Folder)
            {
                BranchEnableRequested?.Invoke(_tree.GetFolder(row.Index), value);
            }
            else
            {
                ModEnableRequested?.Invoke(_tree.GetMod(row.Index), value);
            }
        }

        internal void OnMoveClicked(RowElements row)
        {
            if (_tree == null)
            {
                return;
            }

            if (row.Kind == ModFolderRowKind.Folder)
            {
                BeginMoveFolder(_tree.GetFolder(row.Index).Path);
            }
            else
            {
                BeginMoveMod(_tree.GetMod(row.Index).Id);
            }
        }

        internal void OnHereClicked(RowElements row)
        {
            if (_tree != null && row.Kind == ModFolderRowKind.Folder)
            {
                CompleteMoveTo(_tree.GetFolder(row.Index).Path);
            }
        }

        internal void OnFolderAction(RowElements row, string action)
        {
            if (_tree != null && row.Kind == ModFolderRowKind.Folder)
            {
                FolderActionRequested?.Invoke(_tree.GetFolder(row.Index), action);
            }
        }

        internal void OnModAction(RowElements row, string action)
        {
            if (_tree != null && row.Kind == ModFolderRowKind.Mod)
            {
                ModActionRequested?.Invoke(_tree.GetMod(row.Index), action);
            }
        }

        private void OnSearchFieldChanged(ChangeEvent<string> evt)
        {
            _pendingSearch = evt.newValue ?? "";
            if (_options.SearchDebounceMilliseconds <= 0)
            {
                ApplyPendingSearch();
                return;
            }

            _searchItem.ExecuteLater(_options.SearchDebounceMilliseconds);
        }

        private void ApplyPendingSearch()
        {
            _searchItem?.Pause();
            SetSearch(_pendingSearch);
        }

        private void SetAllExpanded(bool expanded)
        {
            if (_tree == null)
            {
                return;
            }

            _tree.SetAllExpanded(expanded);
            Refresh();
            if (!_tree.IsSearching)
            {
                FoldChanged?.Invoke();
            }
        }

        private bool CanTakeMovingItem(ModFolderPath target)
        {
            if (!_moving)
            {
                return false;
            }

            return _movingKind == ModFolderRowKind.Mod || !target.IsSameOrDescendantOf(_movingFolder);
        }

        private bool IsMovingFrom(ModFolderPath folder)
        {
            return folder == _movingFrom;
        }

        private bool MovingItemExists()
        {
            if (_tree == null)
            {
                return false;
            }

            return _movingKind == ModFolderRowKind.Mod
                ? _tree.TryFindMod(_movingModId, out _)
                : _tree.TryFindFolder(_movingFolder.Value, out _);
        }

        private void OnPromptKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
            {
                AcceptPrompt();
                evt.StopPropagation();
            }
            else if (evt.keyCode == KeyCode.Escape)
            {
                CancelPrompt();
                evt.StopPropagation();
            }
        }

        private void ClosePrompt()
        {
            _promptAccepted = null;
            _prompt.style.display = DisplayStyle.None;
        }

        private void ShowMoveBanner()
        {
            _moveBanner.style.display = DisplayStyle.Flex;
            Refresh();
        }

        private void EndMove()
        {
            _moving = false;
            _movingModId = "";
            _movingFolder = ModFolderPath.Root;
            _movingFrom = ModFolderPath.Root;
            _moveBanner.style.display = DisplayStyle.None;
        }

        private static void SetShown(VisualElement element, bool shown)
        {
            element.style.display = shown ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private static void SetShownAll(Button[] buttons, bool shown)
        {
            for (int i = 0; i < buttons.Length; i++)
            {
                SetShown(buttons[i], shown);
            }
        }

        private static Button MakeButton(string text, Action onClick, string tooltip)
        {
            Button button = new(onClick) { text = text };
            if (!string.IsNullOrEmpty(tooltip))
            {
                button.tooltip = tooltip;
            }

            button.style.flexShrink = 0f;
            return button;
        }

        /// <summary>The child elements of one pooled row and the tree item it is bound to.</summary>
        internal sealed class RowElements
        {
            public readonly VisualElement Root = new();
            public VisualElement Indent;
            public Button Fold;
            public Toggle Toggle;
            public Label Name;
            public Label Detail;
            public VisualElement Actions;
            public Button Here;
            public Button Move;
            public Button[] FolderActions;
            public Button[] ModActions;
            public string[] ModActionLabels;
            public ModFolderRowKind Kind;
            public int Index;
        }

        /// <summary>
        /// The list's items source: only a count. The list binds rows by index and the view reads the tree, so
        /// each slot answers one reused object instead of boxing a row.
        /// </summary>
        private sealed class RowList : IList
        {
            private static readonly object Slot = new();
            private int _count;

            public int Count => _count;

            public bool IsReadOnly => true;

            public bool IsFixedSize => true;

            public bool IsSynchronized => false;

            public object SyncRoot => this;

            public object this[int index]
            {
                get
                {
                    if ((uint)index >= (uint)_count)
                    {
                        throw new ArgumentOutOfRangeException(nameof(index));
                    }

                    return Slot;
                }
                set => throw new NotSupportedException();
            }

            public void SetCount(int count)
            {
                _count = count < 0 ? 0 : count;
            }

            public int Add(object value)
            {
                throw new NotSupportedException();
            }

            public void Clear()
            {
                throw new NotSupportedException();
            }

            public bool Contains(object value)
            {
                return _count > 0 && ReferenceEquals(value, Slot);
            }

            public int IndexOf(object value)
            {
                return _count > 0 && ReferenceEquals(value, Slot) ? 0 : -1;
            }

            public void Insert(int index, object value)
            {
                throw new NotSupportedException();
            }

            public void Remove(object value)
            {
                throw new NotSupportedException();
            }

            public void RemoveAt(int index)
            {
                throw new NotSupportedException();
            }

            public void CopyTo(Array array, int index)
            {
                for (int i = 0; i < _count; i++)
                {
                    array.SetValue(Slot, index + i);
                }
            }

            public IEnumerator GetEnumerator()
            {
                for (int i = 0; i < _count; i++)
                {
                    yield return Slot;
                }
            }
        }
    }
}
