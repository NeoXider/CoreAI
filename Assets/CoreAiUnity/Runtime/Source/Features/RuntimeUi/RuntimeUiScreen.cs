using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.RuntimeUi
{
    /// <summary>
    /// One runtime screen: a UI Toolkit tree built from UXML text (or element by element), styled by the
    /// interpreter from USS text plus the host theme, and kept correct while the game runs. Replacing the
    /// USS restyles the same tree in place; replacing the UXML rebuilds it and carries over the values
    /// and handle state of elements whose names match. Nothing here runs per frame: styles change only
    /// on a replace, an API call or an interaction-state event.
    /// </summary>
    public sealed class RuntimeUiScreen
    {
        /// <summary>Class on every screen root.</summary>
        public const string ScreenClass = "cai-screen";

        /// <summary>Class on every list view row the host creates.</summary>
        public const string ListItemClass = "cai-list-item";

        private static readonly EventCallback<PointerEnterEvent, RuntimeUiNodeRecord> PointerEnterCallback =
            OnPointerEnter;

        private static readonly EventCallback<PointerLeaveEvent, RuntimeUiNodeRecord> PointerLeaveCallback =
            OnPointerLeave;

        private static readonly EventCallback<PointerDownEvent, RuntimeUiNodeRecord> PointerDownCallback =
            OnPointerDown;

        private static readonly EventCallback<PointerUpEvent, RuntimeUiNodeRecord> PointerUpCallback = OnPointerUp;

        private static readonly EventCallback<PointerCancelEvent, RuntimeUiNodeRecord> PointerCancelCallback =
            OnPointerCancel;

        private static readonly EventCallback<PointerCaptureOutEvent, RuntimeUiNodeRecord> CaptureOutCallback =
            OnCaptureOut;

        private static readonly EventCallback<FocusInEvent, RuntimeUiNodeRecord> FocusInCallback = OnFocusIn;

        private static readonly EventCallback<FocusOutEvent, RuntimeUiNodeRecord> FocusOutCallback = OnFocusOut;

        private static readonly EventCallback<ChangeEvent<bool>, RuntimeUiNodeRecord> CheckedCallback =
            OnCheckedChange;

        private readonly RuntimeUiHost _host;
        private readonly VisualElement _container;
        private readonly List<UssStyleSheet> _sheets = new(3);
        private readonly Dictionary<VisualElement, RuntimeUiNodeRecord> _byElement = new();
        private readonly List<RuntimeUiNodeRecord> _authored = new();
        private readonly List<RuntimeUiElement> _handles = new();
        private readonly Dictionary<string, UssComplexSelector> _selectorCache = new(StringComparer.Ordinal);
        private readonly UssResolvedStyle _scratch = new();
        private readonly List<string> _applyFailures = new();
        private readonly Dictionary<VisualElement, Label> _listRowLabels = new();
        private readonly HashSet<ListView> _configuredLists = new();
        private RuntimeUiDiagnosticList _diagnostics;
        private bool _buildingListRow;
        private UssStyleSheet _sheet = UssStyleSheet.Empty;
        private UxmlDocument _document;
        private UssStyleResolver _resolver;
        private EventCallback<GeometryChangedEvent> _onGeometryChanged;
        private Action<RuntimeUiScreen> _resized;

        internal RuntimeUiScreen(RuntimeUiHost host, string id, RuntimeUiScreenOptions options, long sequence)
        {
            _host = host;
            Id = id;
            Owner = options?.Owner;
            SortOrder = options?.SortOrder ?? 0;
            Sequence = sequence;
            _container = new VisualElement { name = "cai-screen-layer-" + id, pickingMode = PickingMode.Ignore };
            _container.style.position = UnityEngine.UIElements.Position.Absolute;
            _container.style.left = 0f;
            _container.style.top = 0f;
            _container.style.right = 0f;
            _container.style.bottom = 0f;
            Root = new VisualElement { name = id, pickingMode = PickingMode.Ignore };
            Root.AddToClassList(ScreenClass);
            _container.Add(Root);
            IsVisible = options?.Visible ?? true;
            _container.style.display = IsVisible ? DisplayStyle.Flex : DisplayStyle.None;
            _diagnostics = new RuntimeUiDiagnosticList(host.Limits.MaxDiagnostics);
            _resolver = new UssStyleResolver(_diagnostics);
        }

        /// <summary>Raised after a UXML replace rebuilt the tree (handles of matching names are already rebound).</summary>
        public event Action<RuntimeUiScreen> Rebuilt;

        /// <summary>Raised once when the screen is destroyed.</summary>
        public event Action<RuntimeUiScreen> Destroyed;

        /// <summary>
        /// Raised when the panel lays the screen root out at a new size (the panel was resized). Layers that
        /// place elements in pixels themselves (the Roblox GUI classes) lay out again on it.
        /// </summary>
        public event Action<RuntimeUiScreen> Resized
        {
            add
            {
                if (value == null || IsDestroyed)
                {
                    return;
                }

                bool first = _resized == null;
                _resized += value;
                if (first)
                {
                    _onGeometryChanged ??= OnRootGeometryChanged;
                    Root.RegisterCallback(_onGeometryChanged);
                }
            }
            remove
            {
                if (value == null || _resized == null)
                {
                    return;
                }

                _resized -= value;
                if (_resized == null)
                {
                    Root.UnregisterCallback(_onGeometryChanged);
                }
            }
        }

        /// <summary>The width of the screen root in panel pixels after the panel's last layout; 0 before it.</summary>
        public float Width => IsDestroyed ? 0f : FiniteOrZero(Root.layout.width);

        /// <summary>The height of the screen root in panel pixels after the panel's last layout; 0 before it.</summary>
        public float Height => IsDestroyed ? 0f : FiniteOrZero(Root.layout.height);

        /// <summary>Screen id, unique in its host.</summary>
        public string Id { get; }

        /// <summary>Owner tag given at creation (a mod id, say), or null.</summary>
        public string Owner { get; }

        /// <summary>Draw order against other screens; higher is on top.</summary>
        public int SortOrder { get; private set; }

        /// <summary>True while shown.</summary>
        public bool IsVisible { get; private set; }

        /// <summary>True after <see cref="Destroy"/>.</summary>
        public bool IsDestroyed { get; private set; }

        /// <summary>The styled root of the screen (class <see cref="ScreenClass"/>, matched by <c>:root</c>).</summary>
        public VisualElement Root { get; }

        /// <summary>The handle of the screen root, to create elements under it or style it.</summary>
        public RuntimeUiElement RootElement => RootRecord == null ? null : GetHandle(RootRecord);

        /// <summary>The current UXML text.</summary>
        public string UxmlText { get; private set; } = string.Empty;

        /// <summary>The current USS text.</summary>
        public string UssText { get; private set; } = string.Empty;

        /// <summary>Reports of the last replace (parsers and cascade) plus API reports since.</summary>
        public IReadOnlyList<RuntimeUiDiagnostic> Diagnostics => _diagnostics.Items;

        /// <summary>Authored elements (markup and API-created), the root excluded.</summary>
        public int ElementCount => _authored.Count;

        /// <summary>Increments on every rebuild of the tree.</summary>
        public int BuildVersion { get; private set; }

        /// <summary>Increments on every full restyle.</summary>
        public int StyleVersion { get; private set; }

        internal long Sequence { get; }

        internal int StackIndex { get; set; } = -1;

        internal bool HiddenByStack { get; set; }

        internal VisualElement Container => _container;

        internal RuntimeUiNodeRecord RootRecord { get; private set; }

        internal RuntimeUiElementFactory Factory => _host.Factory;

        internal int LiveCallbackCount { get; private set; }

        internal int LiveHandleRegistrationCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < _handles.Count; i++)
                {
                    if (_handles[i].HasRegisteredEvents)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        internal int LiveSubscriberCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < _handles.Count; i++)
                {
                    count += _handles[i].SubscriberCount;
                }

                return count;
            }
        }

        /// <summary>Replaces the USS: the same tree is restyled; element state, classes and handles are untouched.</summary>
        public RuntimeUiResult ReplaceUss(string uss)
        {
            if (IsDestroyed)
            {
                return DestroyedResult();
            }

            if (!_host.CheckTextSize(uss, out RuntimeUiResult refused))
            {
                return refused;
            }

            ResetDiagnostics();
            _sheet = UssStyleSheet.Parse(uss, _host.Limits);
            UssText = uss ?? string.Empty;
            _diagnostics.AddRange(_document?.Diagnostics);
            _diagnostics.AddRange(_sheet.Diagnostics);
            RebuildSheetList();
            RefreshTrackingAll();
            RestyleAll();
            _host.LogOperation(this, "ReplaceUss", _diagnostics);
            return RuntimeUiResult.Ok(this, _diagnostics.ToArray());
        }

        /// <summary>Replaces the UXML: the tree is rebuilt, values and handles of matching names carry over.</summary>
        public RuntimeUiResult ReplaceUxml(string uxml)
        {
            return Replace(uxml, null, false);
        }

        /// <summary>Replaces both texts in one rebuild.</summary>
        public RuntimeUiResult Replace(string uxml, string uss)
        {
            return Replace(uxml, uss, true);
        }

        /// <summary>The first authored element matching a selector (document order), or null.</summary>
        public RuntimeUiElement Query(string selector)
        {
            return RootRecord == null ? null : QueryFrom(RootRecord, selector, true);
        }

        /// <summary>Appends every authored element matching a selector to <paramref name="results"/>; returns how many.</summary>
        public int QueryAll(string selector, List<RuntimeUiElement> results)
        {
            if (results == null || RootRecord == null || !TryGetSelector(selector, out UssComplexSelector parsed))
            {
                return 0;
            }

            int before = results.Count;
            CollectMatches(RootRecord, parsed, results, true);
            return results.Count - before;
        }

        /// <summary>The first authored element with a name, or null.</summary>
        public RuntimeUiElement GetElement(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }

            for (int i = 0; i < _authored.Count; i++)
            {
                if (_authored[i].Name == name)
                {
                    return GetHandle(_authored[i]);
                }
            }

            return null;
        }

        /// <summary>
        /// Creates an element of a kind under <paramref name="parent"/> (the screen root when null) without
        /// any markup. It is styled by the current USS and theme like a markup element; a UXML replace
        /// rebuilds the tree from the markup, so API-created elements live until the next one.
        /// </summary>
        /// <returns>The handle, or null when the screen is full, destroyed or the parent is detached (reported).</returns>
        public RuntimeUiElement CreateElement(RuntimeUiElementKind kind, RuntimeUiElement parent = null,
            string name = null)
        {
            if (IsDestroyed || RootRecord == null)
            {
                return null;
            }

            RuntimeUiNodeRecord parentRecord = parent == null ? RootRecord : parent.Record;
            if (parentRecord == null || parentRecord.Screen != this)
            {
                AddDiagnostic(RuntimeUiDiagnosticCodes.UiElementDetached, RuntimeUiSource.Api,
                    "CreateElement: the parent " + parent + " is not a live element of screen '" + Id + "'");
                return null;
            }

            if (kind == RuntimeUiElementKind.Placeholder || kind == RuntimeUiElementKind.TemplateContainer)
            {
                AddDiagnostic(RuntimeUiDiagnosticCodes.UiUnsupportedOperation, RuntimeUiSource.Api,
                    "CreateElement: " + kind + " is created by markup only");
                return null;
            }

            if (_authored.Count >= _host.Options.MaxElementsPerScreen)
            {
                AddDiagnostic(RuntimeUiDiagnosticCodes.UiElementLimit, RuntimeUiSource.Api,
                    "screen '" + Id + "' already holds " + _authored.Count + " elements");
                return null;
            }

            VisualElement element = Factory.Create(kind);
            if (!string.IsNullOrEmpty(name))
            {
                element.name = name;
            }

            parentRecord.Element.Add(element);
            VisualElement physicalParent = element.hierarchy.parent;
            if (physicalParent == null || !_byElement.TryGetValue(physicalParent, out RuntimeUiNodeRecord recordParent))
            {
                recordParent = parentRecord;
            }

            Dictionary<VisualElement, RuntimeUiAuthoredElement> authored = new()
            {
                { element, new RuntimeUiAuthoredElement(kind, null, null) }
            };
            RuntimeUiNodeRecord record = AddRecord(element, recordParent, authored);
            record.NameOccurrence = CountNamed(record.Name, record);
            RefreshTrackingSubtree(record);
            Restyle(record, true);
            return GetHandle(record);
        }

        /// <summary>Removes an element and its subtree; the screen root cannot be removed.</summary>
        public bool RemoveElement(RuntimeUiElement element)
        {
            RuntimeUiNodeRecord record = element?.Record;
            if (IsDestroyed || record == null || record.Screen != this || record == RootRecord)
            {
                return false;
            }

            ReleaseSubtree(record, true);
            record.Parent?.Children.Remove(record);
            record.Element.RemoveFromHierarchy();
            return true;
        }

        /// <summary>Shows the screen.</summary>
        public void Show()
        {
            SetVisible(true);
        }

        /// <summary>Hides the screen (it keeps its state and stays queryable).</summary>
        public void Hide()
        {
            SetVisible(false);
        }

        /// <summary>Changes the draw order against other screens.</summary>
        public void SetSortOrder(int sortOrder)
        {
            if (IsDestroyed || SortOrder == sortOrder)
            {
                return;
            }

            SortOrder = sortOrder;
            _host.SortScreens();
        }

        /// <summary>Destroys the screen: callbacks, handles and elements are released.</summary>
        public void Destroy()
        {
            if (!IsDestroyed)
            {
                _host.DestroyScreen(this);
            }
        }

        internal void InitialBuild(string uxml, string uss)
        {
            Replace(uxml, uss, true);
        }

        internal RuntimeUiResult Replace(string uxml, string uss, bool replaceUss)
        {
            if (IsDestroyed)
            {
                return DestroyedResult();
            }

            if (!_host.CheckTextSize(uxml, out RuntimeUiResult refused)
                || (replaceUss && !_host.CheckTextSize(uss, out refused)))
            {
                return refused;
            }

            UxmlDocument document = UxmlParser.Parse(uxml, new UxmlParseOptions
            {
                Limits = _host.Limits,
                TemplateResolver = _host.Templates
            });
            if (document.ElementCount > _host.Options.MaxElementsPerScreen)
            {
                return RuntimeUiResult.Fail(RuntimeUiDiagnosticCodes.UiElementLimit,
                    "the markup builds " + document.ElementCount + " elements; a screen holds at most " +
                    _host.Options.MaxElementsPerScreen, this, document.Diagnostics);
            }

            ResetDiagnostics();
            if (replaceUss)
            {
                _sheet = UssStyleSheet.Parse(uss, _host.Limits);
                UssText = uss ?? string.Empty;
            }

            _document = document;
            UxmlText = uxml ?? string.Empty;
            _diagnostics.AddRange(document.Diagnostics);
            _diagnostics.AddRange(_sheet.Diagnostics);
            RebuildSheetList();
            Rebuild(document);
            _host.LogOperation(this, replaceUss ? "Replace" : "ReplaceUxml", _diagnostics);
            Rebuilt?.Invoke(this);
            return RuntimeUiResult.Ok(this, _diagnostics.ToArray());
        }

        internal void OnThemeChanged()
        {
            if (IsDestroyed)
            {
                return;
            }

            RebuildSheetList();
            RefreshTrackingAll();
            RestyleAll();
        }

        internal void SetStackPosition(int index)
        {
            StackIndex = index;
        }

        internal void SetVisible(bool visible)
        {
            if (IsDestroyed || IsVisible == visible)
            {
                return;
            }

            IsVisible = visible;
            _container.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            if (!visible)
            {
                _host.ReleaseFocusWithin(_container);
            }
        }

        internal void DestroyInternal()
        {
            if (IsDestroyed)
            {
                return;
            }

            _host.ReleaseFocusWithin(_container);
            if (_resized != null)
            {
                Root.UnregisterCallback(_onGeometryChanged);
                _resized = null;
            }

            for (int i = 0; i < _handles.Count; i++)
            {
                _handles[i].Detach(true);
            }

            _handles.Clear();
            if (RootRecord != null)
            {
                ReleaseSubtree(RootRecord, false);
            }

            _configuredLists.Clear();
            _authored.Clear();
            _byElement.Clear();
            _listRowLabels.Clear();
            RootRecord = null;
            Root.Clear();
            _container.RemoveFromHierarchy();
            IsDestroyed = true;
            Rebuilt = null;
            Action<RuntimeUiScreen> destroyed = Destroyed;
            Destroyed = null;
            destroyed?.Invoke(this);
        }

        internal RuntimeUiElement GetHandle(RuntimeUiNodeRecord record)
        {
            if (record.Handle != null)
            {
                return record.Handle;
            }

            RuntimeUiElement handle = new(this, record);
            record.Handle = handle;
            _handles.Add(handle);
            return handle;
        }

        internal RuntimeUiElement QueryFrom(RuntimeUiNodeRecord start, string selector)
        {
            return QueryFrom(start, selector, false);
        }

        internal void AddDiagnostic(string code, RuntimeUiSource source, string message)
        {
            _diagnostics.Add(code, RuntimeUiDiagnosticSeverity.Warning, source, 0, 0, message);
        }

        internal void AddDiagnostics(RuntimeUiDiagnosticList reports)
        {
            _diagnostics.AddRange(reports.Items);
        }

        internal void Raise(Action<RuntimeUiElement> handlers, RuntimeUiElement element, string eventName)
        {
            if (handlers == null)
            {
                return;
            }

            Delegate[] list = handlers.GetInvocationList();
            for (int i = 0; i < list.Length; i++)
            {
                try
                {
                    ((Action<RuntimeUiElement>)list[i])(element);
                }
                catch (Exception exception)
                {
                    _host.ReportHandlerException(this, element, eventName, exception);
                }
            }
        }

        internal void RestyleSubtree(RuntimeUiNodeRecord record)
        {
            Restyle(record, true);
        }

        internal void RestyleAfterOverride(RuntimeUiNodeRecord record, bool subtree)
        {
            Restyle(record, subtree);
        }

        internal void RefreshTrackingAndRestyle(RuntimeUiNodeRecord record)
        {
            RefreshTracking(record);
            Restyle(record, true);
        }

        internal void OnCheckedChanged(RuntimeUiNodeRecord record, bool isChecked)
        {
            if ((record.TrackedStates & UssPseudoState.Checked) == 0)
            {
                return;
            }

            SetState(record, UssPseudoState.Checked, isChecked);
        }

        internal bool TryResolveStyle(RuntimeUiNodeRecord record, UssPropertyId property, out UssValue value)
        {
            UssCustomProperties inherited = record.Parent?.Scope ?? _host.Theme.Scope;
            _resolver.Resolve(_sheets, 2, record, inherited, record.Authored?.InlineStyle,
                record.Handle?.StyleOverrides, _scratch);
            return _scratch.TryGet(property, out value);
        }

        internal void SortSiblings(RuntimeUiNodeRecord record)
        {
            VisualElement parent = record.Element.hierarchy.parent;
            if (parent == null)
            {
                return;
            }

            int count = parent.hierarchy.childCount;
            List<KeyValuePair<int, VisualElement>> order = new(count);
            for (int i = 0; i < count; i++)
            {
                VisualElement child = parent.hierarchy[i];
                int z = _byElement.TryGetValue(child, out RuntimeUiNodeRecord childRecord) && childRecord.Handle != null
                    ? childRecord.Handle.GetZOrderOrDefault()
                    : 0;
                order.Add(new KeyValuePair<int, VisualElement>(z, child));
            }

            for (int pass = 0; pass < order.Count; pass++)
            {
                for (int i = order.Count - 1; i > pass; i--)
                {
                    if (order[i - 1].Key > order[i].Key)
                    {
                        KeyValuePair<int, VisualElement> swap = order[i - 1];
                        order[i - 1] = order[i];
                        order[i] = swap;
                    }
                }
            }

            for (int i = 0; i < order.Count; i++)
            {
                order[i].Value.BringToFront();
            }
        }

        internal void ConfigureListView(RuntimeUiNodeRecord record)
        {
            if (record.Element is not ListView list || _configuredLists.Contains(list))
            {
                return;
            }

            UxmlNode template = record.Authored?.Node?.ItemTemplate;
            _configuredLists.Add(list);
            list.makeItem = () => MakeListItem(record, template);
            list.bindItem = (item, index) => BindListItem(record, item, index);
            list.destroyItem = item => DestroyListItem(item);
        }

        /// <summary>Drives an interaction state the way the pointer and focus callbacks do (tests only).</summary>
        internal void SetPseudoStateForTesting(RuntimeUiElement element, UssPseudoState state, bool on)
        {
            if (element?.Record != null && element.Record.Screen == this)
            {
                SetState(element.Record, state, on);
            }
        }

        internal static bool IsFocusWithin(RuntimeUiNodeRecord record, VisualElement focused)
        {
            if (record == null || focused == null)
            {
                return false;
            }

            VisualElement element = record.Element;
            if (focused == element)
            {
                return true;
            }

            bool composite = element is TextField || element is Slider || element is Toggle;
            return composite && element.Contains(focused);
        }

        private void OnRootGeometryChanged(GeometryChangedEvent evt)
        {
            if (evt.oldRect.width.Equals(evt.newRect.width) && evt.oldRect.height.Equals(evt.newRect.height))
            {
                return;
            }

            _resized?.Invoke(this);
        }

        private static float FiniteOrZero(float value)
        {
            return float.IsNaN(value) || float.IsInfinity(value) ? 0f : value;
        }

        private RuntimeUiResult DestroyedResult()
        {
            return RuntimeUiResult.Fail(RuntimeUiDiagnosticCodes.UiScreenDestroyed,
                "the screen '" + Id + "' was destroyed");
        }

        private void ResetDiagnostics()
        {
            _diagnostics = new RuntimeUiDiagnosticList(_host.Limits.MaxDiagnostics);
            _resolver = new UssStyleResolver(_diagnostics);
        }

        private void RebuildSheetList()
        {
            _sheets.Clear();
            _sheets.Add(_host.HostBaseSheet);
            _sheets.Add(_host.Theme.BaseSheet);
            _sheets.Add(_sheet);
        }

        private void Rebuild(UxmlDocument document)
        {
            Dictionary<string, List<SavedState>> saved = SnapshotState();
            RuntimeUiPropertyMask rootApplied = default;
            if (RootRecord != null)
            {
                rootApplied = RootRecord.Applied;
                ReleaseSubtree(RootRecord, false);
            }

            _authored.Clear();
            _byElement.Clear();
            _listRowLabels.Clear();
            _configuredLists.Clear();
            Root.Clear();

            Dictionary<VisualElement, RuntimeUiAuthoredElement> authored = new();
            authored[Root] = new RuntimeUiAuthoredElement(RuntimeUiElementKind.VisualElement, null, null);
            for (int i = 0; i < document.Root.Children.Count; i++)
            {
                Root.Add(Factory.Build(document.Root.Children[i], authored, _diagnostics));
            }

            // WHY: the root element survives the rebuild with the inline styles of the last pass, so its
            // record must inherit that mask - otherwise a rule dropped by the new texts would linger on it.
            RootRecord = new RuntimeUiNodeRecord(this, Root, null, authored[Root])
            {
                PseudoStates = UssPseudoState.Root,
                Applied = rootApplied
            };
            _byElement[Root] = RootRecord;
            BuildChildRecords(RootRecord, authored);
            AssignNameOccurrences();
            RebindHandles();
            RestoreState(saved);
            for (int i = 0; i < _authored.Count; i++)
            {
                if (_authored[i].Kind == RuntimeUiElementKind.ListView)
                {
                    ConfigureListView(_authored[i]);
                }
            }

            RefreshTrackingAll();
            RestyleAll();
            for (int i = 0; i < _handles.Count; i++)
            {
                if (_handles[i].HasZOrder && _handles[i].Record != null)
                {
                    SortSiblings(_handles[i].Record);
                }
            }

            BuildVersion++;
        }

        private void BuildChildRecords(RuntimeUiNodeRecord parent,
            Dictionary<VisualElement, RuntimeUiAuthoredElement> authored)
        {
            VisualElement element = parent.Element;
            int count = element.hierarchy.childCount;
            for (int i = 0; i < count; i++)
            {
                AddRecord(element.hierarchy[i], parent, authored);
            }
        }

        private RuntimeUiNodeRecord AddRecord(VisualElement element, RuntimeUiNodeRecord parent,
            Dictionary<VisualElement, RuntimeUiAuthoredElement> authored)
        {
            authored.TryGetValue(element, out RuntimeUiAuthoredElement info);
            RuntimeUiNodeRecord record = new(this, element, parent, info);
            parent.Children.Add(record);
            _byElement[element] = record;
            if (info != null && !_buildingListRow)
            {
                _authored.Add(record);
            }

            BuildChildRecords(record, authored);
            return record;
        }

        private void AssignNameOccurrences()
        {
            Dictionary<string, int> counts = new(StringComparer.Ordinal);
            for (int i = 0; i < _authored.Count; i++)
            {
                string name = _authored[i].Name;
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                counts.TryGetValue(name, out int occurrence);
                _authored[i].NameOccurrence = occurrence;
                counts[name] = occurrence + 1;
            }
        }

        private int CountNamed(string name, RuntimeUiNodeRecord except)
        {
            if (string.IsNullOrEmpty(name))
            {
                return 0;
            }

            int count = 0;
            for (int i = 0; i < _authored.Count; i++)
            {
                if (_authored[i] != except && _authored[i].Name == name)
                {
                    count++;
                }
            }

            return count;
        }

        private void RebindHandles()
        {
            for (int i = _handles.Count - 1; i >= 0; i--)
            {
                RuntimeUiElement handle = _handles[i];
                RuntimeUiNodeRecord target = null;
                if (handle.Record != null && handle.Record.Element == Root)
                {
                    target = RootRecord;
                }
                else if (handle.BindName != null)
                {
                    target = FindNamed(handle.BindName, handle.BindOccurrence);
                    if (target != null && target.Kind != handle.Kind)
                    {
                        target = null;
                    }
                }

                if (target == null || target.Handle != null)
                {
                    handle.Detach(true);
                    _handles.RemoveAt(i);
                    continue;
                }

                handle.Retarget(target);
            }
        }

        private RuntimeUiNodeRecord FindNamed(string name, int occurrence)
        {
            for (int i = 0; i < _authored.Count; i++)
            {
                RuntimeUiNodeRecord record = _authored[i];
                if (record.Name == name && record.NameOccurrence == occurrence)
                {
                    return record;
                }
            }

            return null;
        }

        private Dictionary<string, List<SavedState>> SnapshotState()
        {
            Dictionary<string, List<SavedState>> saved = new(StringComparer.Ordinal);
            for (int i = 0; i < _authored.Count; i++)
            {
                RuntimeUiNodeRecord record = _authored[i];
                string name = record.Name;
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                SavedState state = new() { Kind = record.Kind, Occurrence = record.NameOccurrence };
                switch (record.Element)
                {
                    case Toggle toggle:
                        state.Bool = toggle.value;
                        break;
                    case Slider slider:
                        state.Number = slider.value;
                        break;
                    case TextField field:
                        state.Text = field.value;
                        break;
                    case ScrollView view:
                        state.Scroll = view.scrollOffset;
                        break;
                    default:
                        continue;
                }

                if (!saved.TryGetValue(name, out List<SavedState> list))
                {
                    list = new List<SavedState>();
                    saved[name] = list;
                }

                list.Add(state);
            }

            return saved;
        }

        private void RestoreState(Dictionary<string, List<SavedState>> saved)
        {
            if (saved.Count == 0)
            {
                return;
            }

            for (int i = 0; i < _authored.Count; i++)
            {
                RuntimeUiNodeRecord record = _authored[i];
                string name = record.Name;
                if (string.IsNullOrEmpty(name) || !saved.TryGetValue(name, out List<SavedState> states))
                {
                    continue;
                }

                for (int s = 0; s < states.Count; s++)
                {
                    SavedState state = states[s];
                    if (state.Occurrence != record.NameOccurrence || state.Kind != record.Kind)
                    {
                        continue;
                    }

                    switch (record.Element)
                    {
                        case Toggle toggle:
                            toggle.SetValueWithoutNotify(state.Bool);
                            break;
                        case Slider slider:
                            slider.SetValueWithoutNotify(state.Number);
                            break;
                        case TextField field:
                            field.SetValueWithoutNotify(state.Text ?? string.Empty);
                            break;
                        case ScrollView view:
                            view.scrollOffset = state.Scroll;
                            break;
                    }

                    break;
                }
            }
        }

        private void RefreshTrackingAll()
        {
            if (RootRecord != null)
            {
                RefreshTrackingSubtree(RootRecord);
            }
        }

        private void RefreshTrackingSubtree(RuntimeUiNodeRecord record)
        {
            RefreshTracking(record);
            for (int i = 0; i < record.Children.Count; i++)
            {
                RefreshTrackingSubtree(record.Children[i]);
            }
        }

        private void RefreshTracking(RuntimeUiNodeRecord record)
        {
            UssPseudoState tracked = UssPseudoState.None;
            bool affectsDescendants = false;
            for (int i = 0; i < _sheets.Count; i++)
            {
                tracked |= _sheets[i].TrackedStatesFor(record, out bool affects);
                affectsDescendants |= affects;
            }

            record.StateAffectsDescendants = affectsDescendants;
            if (tracked == record.TrackedStates && record.CallbacksRegistered == (tracked != UssPseudoState.None))
            {
                return;
            }

            UnregisterStateCallbacks(record);
            record.TrackedStates = tracked;
            RegisterStateCallbacks(record);
        }

        private void RegisterStateCallbacks(RuntimeUiNodeRecord record)
        {
            UssPseudoState tracked = record.TrackedStates;
            if (tracked == UssPseudoState.None)
            {
                return;
            }

            VisualElement element = record.Element;
            if ((tracked & (UssPseudoState.Hover | UssPseudoState.Active)) != 0)
            {
                element.RegisterCallback(PointerEnterCallback, record);
                element.RegisterCallback(PointerLeaveCallback, record);
                LiveCallbackCount += 2;
            }

            if ((tracked & UssPseudoState.Active) != 0)
            {
                element.RegisterCallback(PointerDownCallback, record, TrickleDown.TrickleDown);
                element.RegisterCallback(PointerUpCallback, record, TrickleDown.TrickleDown);
                element.RegisterCallback(PointerCancelCallback, record);
                element.RegisterCallback(CaptureOutCallback, record);
                LiveCallbackCount += 4;
            }

            if ((tracked & UssPseudoState.Focus) != 0)
            {
                element.RegisterCallback(FocusInCallback, record);
                element.RegisterCallback(FocusOutCallback, record);
                LiveCallbackCount += 2;
            }

            if ((tracked & UssPseudoState.Checked) != 0 && element is Toggle toggle)
            {
                element.RegisterCallback(CheckedCallback, record);
                LiveCallbackCount++;
                record.PseudoStates = toggle.value
                    ? record.PseudoStates | UssPseudoState.Checked
                    : record.PseudoStates & ~UssPseudoState.Checked;
            }

            record.CallbacksRegistered = true;
        }

        private void UnregisterStateCallbacks(RuntimeUiNodeRecord record)
        {
            if (!record.CallbacksRegistered)
            {
                return;
            }

            UssPseudoState tracked = record.TrackedStates;
            VisualElement element = record.Element;
            if ((tracked & (UssPseudoState.Hover | UssPseudoState.Active)) != 0)
            {
                element.UnregisterCallback(PointerEnterCallback);
                element.UnregisterCallback(PointerLeaveCallback);
                LiveCallbackCount -= 2;
            }

            if ((tracked & UssPseudoState.Active) != 0)
            {
                element.UnregisterCallback(PointerDownCallback, TrickleDown.TrickleDown);
                element.UnregisterCallback(PointerUpCallback, TrickleDown.TrickleDown);
                element.UnregisterCallback(PointerCancelCallback);
                element.UnregisterCallback(CaptureOutCallback);
                LiveCallbackCount -= 4;
            }

            if ((tracked & UssPseudoState.Focus) != 0)
            {
                element.UnregisterCallback(FocusInCallback);
                element.UnregisterCallback(FocusOutCallback);
                LiveCallbackCount -= 2;
            }

            if ((tracked & UssPseudoState.Checked) != 0 && element is Toggle)
            {
                element.UnregisterCallback(CheckedCallback);
                LiveCallbackCount--;
            }

            record.CallbacksRegistered = false;
            record.PseudoStates &= UssPseudoState.Root | UssPseudoState.Disabled;
        }

        private void ReleaseSubtree(RuntimeUiNodeRecord record, bool detachHandles)
        {
            UnregisterStateCallbacks(record);
            if (record.Handle != null && detachHandles)
            {
                RuntimeUiElement handle = record.Handle;
                handle.Detach(true);
                _handles.Remove(handle);
            }

            if (detachHandles)
            {
                _authored.Remove(record);
                _byElement.Remove(record.Element);
                _listRowLabels.Remove(record.Element);
            }

            if (record.Element is ListView list && _configuredLists.Remove(list))
            {
                list.itemsSource = null;
            }

            for (int i = 0; i < record.Children.Count; i++)
            {
                ReleaseSubtree(record.Children[i], detachHandles);
            }
        }

        private void RestyleAll()
        {
            if (RootRecord == null)
            {
                return;
            }

            Restyle(RootRecord, true);
            StyleVersion++;
        }

        private void Restyle(RuntimeUiNodeRecord record, bool subtree)
        {
            UssCustomProperties before = record.Scope;
            UssCustomProperties inherited = record.Parent?.Scope ?? _host.Theme.Scope;
            bool disabled = !record.Element.enabledInHierarchy;
            record.PseudoStates = disabled
                ? record.PseudoStates | UssPseudoState.Disabled
                : record.PseudoStates & ~UssPseudoState.Disabled;
            _resolver.Resolve(_sheets, 2, record, inherited, record.Authored?.InlineStyle,
                record.Handle?.StyleOverrides, _scratch);
            _applyFailures.Clear();
            record.DisplayNone = _host.Applier.Apply(record.Element, _scratch, ref record.Applied, _applyFailures);
            for (int i = 0; i < _applyFailures.Count; i++)
            {
                _diagnostics.Add(RuntimeUiDiagnosticCodes.UssUnresolvedAsset, RuntimeUiDiagnosticSeverity.Warning,
                    RuntimeUiSource.Uss, 0, 0, _applyFailures[i]);
            }

            record.Scope = _scratch.CustomProperties;
            if (!subtree && ReferenceEquals(before, record.Scope))
            {
                return;
            }

            for (int i = 0; i < record.Children.Count; i++)
            {
                Restyle(record.Children[i], true);
            }
        }

        private void SetState(RuntimeUiNodeRecord record, UssPseudoState state, bool on)
        {
            UssPseudoState next = on ? record.PseudoStates | state : record.PseudoStates & ~state;
            if (next == record.PseudoStates)
            {
                return;
            }

            record.PseudoStates = next;
            Restyle(record, record.StateAffectsDescendants);
        }

        private RuntimeUiElement QueryFrom(RuntimeUiNodeRecord start, string selector, bool includeStart)
        {
            if (!TryGetSelector(selector, out UssComplexSelector parsed))
            {
                return null;
            }

            RuntimeUiNodeRecord match = FindFirst(start, parsed, includeStart);
            return match == null ? null : GetHandle(match);
        }

        private bool TryGetSelector(string selector, out UssComplexSelector parsed)
        {
            parsed = null;
            if (string.IsNullOrWhiteSpace(selector))
            {
                return false;
            }

            if (_selectorCache.TryGetValue(selector, out parsed))
            {
                return parsed != null;
            }

            if (!UssComplexSelector.TryParse(selector, 0, 0, out parsed, out string error))
            {
                AddDiagnostic(RuntimeUiDiagnosticCodes.UiInvalidSelector, RuntimeUiSource.Api,
                    "'" + selector + "': " + error);
                parsed = null;
            }

            if (_selectorCache.Count >= 128)
            {
                _selectorCache.Clear();
            }

            _selectorCache[selector] = parsed;
            return parsed != null;
        }

        private RuntimeUiNodeRecord FindFirst(RuntimeUiNodeRecord record, UssComplexSelector selector, bool includeSelf)
        {
            if (record.IsListItem)
            {
                return null;
            }

            if (includeSelf && record.IsAuthored && record != RootRecord && UssSelectorMatcher.Matches(selector, record))
            {
                return record;
            }

            for (int i = 0; i < record.Children.Count; i++)
            {
                RuntimeUiNodeRecord found = FindFirst(record.Children[i], selector, true);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private void CollectMatches(RuntimeUiNodeRecord record, UssComplexSelector selector,
            List<RuntimeUiElement> results, bool includeSelf)
        {
            if (record.IsListItem)
            {
                return;
            }

            if (includeSelf && record.IsAuthored && record != RootRecord && UssSelectorMatcher.Matches(selector, record))
            {
                results.Add(GetHandle(record));
            }

            for (int i = 0; i < record.Children.Count; i++)
            {
                CollectMatches(record.Children[i], selector, results, true);
            }
        }

        private VisualElement MakeListItem(RuntimeUiNodeRecord listRecord, UxmlNode template)
        {
            VisualElement row;
            Dictionary<VisualElement, RuntimeUiAuthoredElement> authored = new();
            if (template != null)
            {
                row = Factory.Build(template, authored, _diagnostics);
            }
            else
            {
                row = new Label();
                authored[row] = new RuntimeUiAuthoredElement(RuntimeUiElementKind.Label, null, null);
            }

            row.AddToClassList(ListItemClass);
            if (IsDestroyed || listRecord.Screen != this || !_byElement.ContainsKey(listRecord.Element))
            {
                return row;
            }

            authored.TryGetValue(row, out RuntimeUiAuthoredElement rowInfo);
            RuntimeUiNodeRecord rowRecord = new(this, row, listRecord, rowInfo) { IsListItem = true };
            listRecord.Children.Add(rowRecord);
            _byElement[row] = rowRecord;

            // WHY: rows belong to the list view, not to the screen's markup: they are pooled and recycled by
            // the list, so they neither count toward the element limit nor show up in queries.
            _buildingListRow = true;
            BuildChildRecords(rowRecord, authored);
            _buildingListRow = false;
            _listRowLabels[row] = row as Label ?? FirstLabel(row);
            RefreshTrackingSubtree(rowRecord);
            Restyle(rowRecord, true);
            return row;
        }

        private static Label FirstLabel(VisualElement element)
        {
            int count = element.hierarchy.childCount;
            for (int i = 0; i < count; i++)
            {
                VisualElement child = element.hierarchy[i];
                if (child is Label label)
                {
                    return label;
                }

                Label nested = FirstLabel(child);
                if (nested != null)
                {
                    return nested;
                }
            }

            return null;
        }

        private void BindListItem(RuntimeUiNodeRecord listRecord, VisualElement row, int index)
        {
            IReadOnlyList<string> items = listRecord.Handle?.ListItems;
            if (items == null || index < 0 || index >= items.Count)
            {
                return;
            }

            if (_listRowLabels.TryGetValue(row, out Label label) && label != null)
            {
                label.text = items[index];
            }
        }

        private void DestroyListItem(VisualElement row)
        {
            if (!_byElement.TryGetValue(row, out RuntimeUiNodeRecord record))
            {
                return;
            }

            ReleaseSubtree(record, true);
            record.Parent?.Children.Remove(record);
        }

        private static void OnPointerEnter(PointerEnterEvent evt, RuntimeUiNodeRecord record)
        {
            record.Screen.SetState(record, UssPseudoState.Hover, true);
        }

        private static void OnPointerLeave(PointerLeaveEvent evt, RuntimeUiNodeRecord record)
        {
            UssPseudoState next = record.PseudoStates & ~(UssPseudoState.Hover | UssPseudoState.Active);
            if (next == record.PseudoStates)
            {
                return;
            }

            record.PseudoStates = next;
            record.Screen.Restyle(record, record.StateAffectsDescendants);
        }

        private static void OnPointerDown(PointerDownEvent evt, RuntimeUiNodeRecord record)
        {
            record.Screen.SetState(record, UssPseudoState.Active, true);
        }

        private static void OnPointerUp(PointerUpEvent evt, RuntimeUiNodeRecord record)
        {
            record.Screen.SetState(record, UssPseudoState.Active, false);
        }

        private static void OnPointerCancel(PointerCancelEvent evt, RuntimeUiNodeRecord record)
        {
            record.Screen.SetState(record, UssPseudoState.Active, false);
        }

        private static void OnCaptureOut(PointerCaptureOutEvent evt, RuntimeUiNodeRecord record)
        {
            record.Screen.SetState(record, UssPseudoState.Active, false);
        }

        private static void OnFocusIn(FocusInEvent evt, RuntimeUiNodeRecord record)
        {
            if (IsFocusWithin(record, evt.target as VisualElement))
            {
                record.Screen.SetState(record, UssPseudoState.Focus, true);
            }
        }

        private static void OnFocusOut(FocusOutEvent evt, RuntimeUiNodeRecord record)
        {
            if (!IsFocusWithin(record, evt.relatedTarget as VisualElement))
            {
                record.Screen.SetState(record, UssPseudoState.Focus, false);
            }
        }

        private static void OnCheckedChange(ChangeEvent<bool> evt, RuntimeUiNodeRecord record)
        {
            if (evt.target == record.Element)
            {
                record.Screen.SetState(record, UssPseudoState.Checked, evt.newValue);
            }
        }

        private struct SavedState
        {
            public RuntimeUiElementKind Kind;
            public int Occurrence;
            public bool Bool;
            public float Number;
            public string Text;
            public Vector2 Scroll;
        }
    }
}
