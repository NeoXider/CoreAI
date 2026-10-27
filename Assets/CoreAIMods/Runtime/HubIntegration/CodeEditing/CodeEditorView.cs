using System;
using System.Collections.Generic;
using System.Globalization;
using CoreAI.CodeEditing;
using CoreAI.LuaAssets;
using UnityEngine;
using UnityEngine.UIElements;
namespace CoreAI.Ai.Hub.CodeEditing
{
    public sealed class CodeEditorView : ICodeEditorView, ICodeEditorErrorView, IDisposable
    {
        private ICodeLanguageProfile _language;
        public const int MaximumDecoratedLines = 1024;
        private const int MaximumGuideBars = 2048;
        private bool _plainTextMode;
        private int _documentGeneration;
        private IVisualElementScheduledItem _presentation;
        private IVisualElementScheduledItem _newLineSchedule;
        private IVisualElementScheduledItem _completionSchedule;
        private CodeSelection? _pendingFocusSelection;
        private string _pendingFocusSource;
        private int _pendingFocusGeneration;
        private string _completionSource;
        private int _completionGeneration;
        private int _completionCaret;
        public bool IsPlainTextMode => _plainTextMode;
        public bool IsReleased => _treeReleased;
        private readonly TextField _field;
        private readonly Label _gutter;
        private readonly Label _mirror;
        private readonly ScrollView _scroll;
        private int? _markerLine;
        private int? _errorLine;
        private bool _focused;
        private CodeEditorHistory _history = new();
        private readonly Dictionary<string, CodeEditorHistory> _documentHistories = new(StringComparer.Ordinal);
        private const int DocumentHistoryLimit = 2;
        private bool _suppressHistory;
        private const int IndentSpaces = 4;
        internal const string GuideClass = "coreai-code-editor__guide";
        internal const string ActiveGuideClass = "coreai-code-editor__guide--active";
        internal const string StepLineClass = "coreai-code-editor__step-line";
        internal const string CaretLineClass = "coreai-code-editor__caret-line";
        internal const string ErrorLineClass = "coreai-code-editor__error-line";
        internal const string ErrorLabelClass = "coreai-code-editor__error-label";
        internal const string HintLineClass = "coreai-code-editor__hint-line";
        internal const string HintLabelClass = "coreai-code-editor__hint-label";
        internal const string CompletionClass = "coreai-code-completion";
        internal const string CompletionPopupName = "editor-completion";
        private VisualElement _guideLayer;
        private readonly List<VisualElement> _guideBars = new();
        private readonly List<Vector3> _guideGeometry = new();
        private readonly List<(int Row, int Level)> _guideKeys = new();
        internal const string LineClass = "coreai-code-editor__line";
        private LineRun _mirrorLines;
        private LineRun _gutterLines;
        private readonly List<string> _gutterTexts = new();
        private VisualElement[] _bracketBoxes;
        private int _surfacePointerId = PointerId.invalidPointerId;
        private int[] _guideLevels = Array.Empty<int>();
        private static readonly CustomStyleProperty<Color> CaretColorProperty = new("--coreai-code-caret");

        private const float CaretWidth = 4f;
        private const float CaretScrollMargin = 48f;
        private const long CaretPollMs = 60;
        private const int CaretBlinkTicks = 9;
        private VisualElement _caretBar;
        private IVisualElementScheduledItem _caretPoll;
        private int _caretLastIndex = -1;
        private int _caretLastSelectIndex = -1;
        private int _caretLastTextLength = -1;
        private int _caretBlinkTick;
        private bool _revealPending;
        private const int MaxRevealRetries = 20;
        private const long RevealRetryDelayMs = 16;
        private IVisualElementScheduledItem _revealRetry;
        private int _revealRetries;
        private int _revealCalls;
        private string _revealNote = "no reveal requested";
        private int _revealedIndex = -1;
        private int _revealedSelectIndex = -1;
        private int _revealedTextLength = -1;
        private VisualElement _currentLineBand;
        private VisualElement _caretLineBand;
        private VisualElement _errorBand;
        private Label _errorLabel;
        private string _errorMessage;
        private VisualElement _hintLayer;
        private readonly List<VisualElement> _hintBands = new();
        private Label _hintLabel;
        private IReadOnlyList<int> _hintLines = Array.Empty<int>();
        private readonly HashSet<int> _hintLineSet = new();
        private string _hintText;
        private bool _hintEnterPending;
        private string _hintLabelText;
        private int _hintLabelRow = -1;
        private int _hintLabelLineEnd;
        private readonly CodeEditorTextUtility.AutoCloseTracker _autoClose = new();
        private string _quoteInsertionSource;
        private string _quoteInsertionExpected;
        private int _quoteInsertionIndex = -1;
        private int _quoteInsertionGeneration;
        private VisualElement _bracketLayer;
        private readonly CodeEditorTextUtility.CodeMaskCache _bracketMask = new();
        private int? _caretLine;
        private static readonly CustomStyleProperty<Color> GutterMarkerColorProperty =
            new("--coreai-station-gutter-marker");
        private static readonly CustomStyleProperty<Color> GutterErrorColorProperty =
            new("--coreai-station-gutter-error");
        private static readonly CustomStyleProperty<Color> GutterCaretColorProperty =
            new("--coreai-station-gutter-caret");
        private static readonly CustomStyleProperty<Color> GutterTeacherColorProperty =
            new("--coreai-station-teacher-gutter");
        public static bool CompletionEnabled = true;
        internal static bool IsAnyCompletionPopupOpen { get; private set; }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            CompletionEnabled = true;
            IsAnyCompletionPopupOpen = false;
        }
        private readonly CodeCompletionEngine _completion = new();
        private VisualElement _completionPopup;
        private CodeCompletionResult _completionResult;
        private int _completionSelected;
        private bool _completionNavigated;
        private bool _completionExplicit;
        private VisualElement _gotoPrompt;
        private TextField _gotoField;
        private Action _releaseTree;
        private bool _treeReleased;
        public CodeEditorView(TextField field, Label gutter, Label mirror, ScrollView scroll, ICodeLanguageProfile language)
        {
            _language = language ?? throw new ArgumentNullException(nameof(language));
            _completion.Profile = _language;
            _field = field ?? throw new ArgumentNullException(nameof(field));
            _field.maxLength = CodeEditorLimits.MaximumDocumentCharacters;
            _gutter = gutter;
            _mirror = mirror;
            _scroll = scroll;
            if (_scroll != null)
            {
                Action<float> followScroll = _ =>
                {
                    SyncGutterOffset();
                    RepositionOpenCompletion();
                };
                Action<float> followHorizontalScroll = _ => RepositionOpenCompletion();
                EventCallback<GeometryChangedEvent> resyncGutter = _ =>
                {
                    SyncGutterOffset();
                    RetryPendingReveal();
                };
                EventCallback<GeometryChangedEvent> contentResized = _ => RetryPendingReveal();
                _scroll.verticalScroller.valueChanged += followScroll;
                _scroll.horizontalScroller.valueChanged += followHorizontalScroll;
                _scroll.RegisterCallback(resyncGutter);
                _scroll.contentContainer.RegisterCallback(contentResized);
                _releaseTree += () =>
                {
                    _scroll.verticalScroller.valueChanged -= followScroll;
                    _scroll.horizontalScroller.valueChanged -= followHorizontalScroll;
                    _scroll.UnregisterCallback(resyncGutter);
                    _scroll.contentContainer.UnregisterCallback(contentResized);
                };
            }
            if (_mirror != null)
            {
                _mirror.text = string.Empty;
                _mirrorLines = new LineRun(_mirror);
                _currentLineBand = CreateLineBand(StepLineClass);
                _mirror.parent?.Insert(0, _currentLineBand);
                _caretLineBand = CreateLineBand(CaretLineClass);
                _mirror.parent?.Insert(0, _caretLineBand);
                _guideLayer = new VisualElement { pickingMode = PickingMode.Ignore };
                _guideLayer.style.position = Position.Absolute;
                _guideLayer.style.top = 0;
                _guideLayer.style.left = 0;
                _guideLayer.style.right = 0;
                _guideLayer.style.bottom = 0;
                _mirror.parent?.Insert(2, _guideLayer);
                _bracketLayer = new VisualElement { pickingMode = PickingMode.Ignore };
                _bracketLayer.style.position = Position.Absolute;
                _bracketLayer.style.top = 0;
                _bracketLayer.style.left = 0;
                _bracketLayer.style.right = 0;
                _bracketLayer.style.bottom = 0;
                _mirror.parent?.Insert(3, _bracketLayer);
                _errorBand = CreateLineBand(ErrorLineClass);
                _mirror.parent?.Insert(0, _errorBand);
                _errorLabel = new Label { pickingMode = PickingMode.Ignore, enableRichText = false };
                _errorLabel.style.position = Position.Absolute;
                _errorLabel.AddToClassList(ErrorLabelClass);
                _errorLabel.style.display = DisplayStyle.None;
                _mirror.parent?.Insert(_mirror.parent.IndexOf(_mirror) + 1, _errorLabel);
                _hintLayer = new VisualElement { pickingMode = PickingMode.Ignore };
                _hintLayer.style.position = Position.Absolute;
                _hintLayer.style.top = 0;
                _hintLayer.style.left = 0;
                _hintLayer.style.right = 0;
                _hintLayer.style.bottom = 0;
                _mirror.parent?.Insert(0, _hintLayer);
                _hintLabel = new Label { pickingMode = PickingMode.Ignore, enableRichText = false };
                _hintLabel.style.position = Position.Absolute;
                _hintLabel.AddToClassList(HintLabelClass);
                _hintLabel.style.display = DisplayStyle.None;
                _mirror.parent?.Insert(_mirror.parent.IndexOf(_mirror) + 1, _hintLabel);
                _caretBar = new VisualElement { pickingMode = PickingMode.Ignore };
                _caretBar.style.position = Position.Absolute;
                _caretBar.style.width = CaretWidth;
                _caretBar.style.borderTopLeftRadius = CaretWidth / 2f;
                _caretBar.style.borderTopRightRadius = CaretWidth / 2f;
                _caretBar.style.borderBottomLeftRadius = CaretWidth / 2f;
                _caretBar.style.borderBottomRightRadius = CaretWidth / 2f;
                _caretBar.AddToClassList("coreai-code-editor__caret");
                _caretBar.style.backgroundColor = HubModWidgets.Accent;
                _caretBar.style.display = DisplayStyle.None;
                _mirror.parent?.Add(_caretBar);
                EventCallback<GeometryChangedEvent> mirrorResized = _ =>
                {
                    ApplyLineHeights();
                    RefreshGuides();
                    RefreshCurrentLineBand();
                    RefreshErrorLine();
                    RefreshHintLines();
                    bool reveal = _revealPending || CaretChangedSinceRefresh();
                    _revealPending = false;
                    RefreshCaret(true, reveal);
                    SyncGutterOffset();
                };
                _mirror.RegisterCallback(mirrorResized);
                VisualElement[] overlays =
                {
                    _currentLineBand, _caretLineBand, _guideLayer, _bracketLayer, _caretBar, _errorBand, _errorLabel,
                    _hintLayer, _hintLabel,
                };
                _releaseTree += () =>
                {
                    _mirror.UnregisterCallback(mirrorResized);
                    for (int i = 0; i < overlays.Length; i++)
                    {
                        overlays[i]?.RemoveFromHierarchy();
                    }
                    _mirrorLines?.Release();
                    _mirrorLines = null;
                    _guideBars.Clear();
                    _guideGeometry.Clear();
                    _guideKeys.Clear();
                    _bracketBoxes = null;
                    _currentLineBand = null;
                    _caretLineBand = null;
                    _guideLayer = null;
                    _bracketLayer = null;
                    _caretBar = null;
                    _errorBand = null;
                    _errorLabel = null;
                    _hintBands.Clear();
                    _hintLayer = null;
                    _hintLabel = null;
                };
            }
            if (_gutter != null)
            {
                _gutter.text = string.Empty;
                _gutterLines = new LineRun(_gutter);
                EventCallback<CustomStyleResolvedEvent> gutterPaletteResolved = _ => RefreshGutter();
                _gutter.RegisterCallback(gutterPaletteResolved);
                _releaseTree += () =>
                {
                    _gutter.UnregisterCallback(gutterPaletteResolved);
                    _gutterLines?.Release();
                    _gutterLines = null;
                };
            }
            RegisterSurfaceClicks();
            if (_field != null)
            {
                EventCallback<ChangeEvent<string>> onValueChanged = evt =>
                {
                    // WHY: only source changes belong to this editor; decoration events may bubble.
                    if (_treeReleased || !ReferenceEquals(evt.target, _field)) return;
                    CancelFocusSelection();
                    string source = _field.value ?? "";
                    if (CodeEditorTextUtility.NeedsEditorNormalization(source))
                    {
                        string normalized = CodeEditorTextUtility.NormalizeEditorText(source,
                            _field.cursorIndex, out int normalizedCaret);
                        CodeEditorTextUtility.NormalizeEditorText(source, _field.selectIndex, out int normalizedAnchor);
                        if (normalized.Length > CodeEditorLimits.MaximumDocumentCharacters)
                            normalized = normalized.Substring(0, CodeEditorLimits.MaximumDocumentCharacters);
                        _field.SetValueWithoutNotify(normalized);
                        Selection = new CodeSelection(Math.Min(normalizedCaret, normalized.Length), Math.Min(normalizedAnchor, normalized.Length));
                        source = normalized;
                    }
                    if (source.Length > CodeEditorLimits.MaximumDocumentCharacters)
                    {
                        source = source.Substring(0, CodeEditorLimits.MaximumDocumentCharacters);
                        _field.SetValueWithoutNotify(source);
                        SetFieldSelection(_field, _field.cursorIndex, _field.selectIndex);
                    }
                    // WHY: synchronous caret/gutter refresh must not decorate an unbounded source.
                    SetPlainTextMode(source.Length > CodeEditorLimits.MaximumAnalysisCharacters
                        || CodeEditorTextUtility.CountLines(source) > MaximumDecoratedLines);
                    TrackTextChange(evt.previousValue, source);
                    if (_errorLine.HasValue)
                    {
                        _errorLine = null;
                        _errorMessage = null;
                    }
                    if (!_suppressHistory)
                    {
                        _markerLine = null;
                        ClearHintLines();
                        if (!string.Equals(source, _history.CurrentText, StringComparison.Ordinal))
                            _history.Push(source, _field.cursorIndex, NowMs(), anchorIndex: _field.selectIndex);
                    }
                    string acceptedText = _acceptedCompletionText;
                    _acceptedCompletionText = null;
                    if (_suppressHistory || string.Equals(source, acceptedText, StringComparison.Ordinal)) CloseCompletion();
                    SchedulePresentation();
                };
                EventCallback<FocusOutEvent> closeCompletionOnFocusOut = _ => CloseCompletion();
                EventCallback<AttachToPanelEvent> reapplyCaretColour = _ =>
                {
                    if (_treeReleased) return;
                    ApplyCaretColor();
                    SchedulePresentation();
                };
                EventCallback<DetachFromPanelEvent> pauseOnDetach = _ =>
                {
                    InvalidateDocumentWork();
                    _focused = false;
                    StopCaret();
                    CloseGotoLinePrompt();
                };
                EventCallback<FocusInEvent> onFocusIn = _ =>
                {
                    // WHY: focus can rebuild Unity's native cache; retain the request until the next input also synchronizes it.
                    RestorePendingFocusSelection();
                    _focused = true;
                    ApplyCaretColor();
                    StartCaret();
                };
                EventCallback<FocusOutEvent> onFocusOut = _ =>
                {
                    ClearQuoteInsertion();
                    _focused = false;
                    StopCaret();
                };
                EventCallback<PointerDownEvent> cancelFocusSelectionOnPointerDown = _ =>
                {
                    CancelFocusSelection();
                    ClearQuoteInsertion();
                };
                EventCallback<ExecuteCommandEvent> synchronizeSelectionBeforeCommand = _ => PrepareSelectionForInput();
                if (_field.textSelection != null)
                {
                    _field.textSelection.selectAllOnFocus = false;
                    _field.textSelection.selectAllOnMouseUp = false;
                }
                _field.RegisterValueChangedCallback(onValueChanged);
                _field.RegisterCallback(closeCompletionOnFocusOut);
                _field.RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
                _field.RegisterCallback<NavigationMoveEvent>(OnNavigationMove, TrickleDown.TrickleDown);
                _field.RegisterCallback(reapplyCaretColour);
                _field.RegisterCallback(pauseOnDetach);
                _field.RegisterCallback(onFocusIn);
                _field.RegisterCallback(onFocusOut);
                _field.RegisterCallback(cancelFocusSelectionOnPointerDown, TrickleDown.TrickleDown);
                _field.RegisterCallback(synchronizeSelectionBeforeCommand, TrickleDown.TrickleDown);
                _releaseTree += () =>
                {
                    _field.UnregisterValueChangedCallback(onValueChanged);
                    _field.UnregisterCallback(closeCompletionOnFocusOut);
                    _field.UnregisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
                    _field.UnregisterCallback<NavigationMoveEvent>(
                        OnNavigationMove, TrickleDown.TrickleDown);
                    _field.UnregisterCallback(reapplyCaretColour);
                    _field.UnregisterCallback(pauseOnDetach);
                    _field.UnregisterCallback(onFocusIn);
                    _field.UnregisterCallback(onFocusOut);
                    _field.UnregisterCallback(cancelFocusSelectionOnPointerDown, TrickleDown.TrickleDown);
                    _field.UnregisterCallback(synchronizeSelectionBeforeCommand, TrickleDown.TrickleDown);
                    _revealRetry?.Pause();
                    _revealRetry = null;
                    _revealPending = false;
                };
                ApplyCaretColor();
            }
            RefreshGutter();
            RefreshHighlight();
            _history.Push(_field?.value ?? "", 0, NowMs(), true);
        }
        private static VisualElement CreateLineBand(string className)
        {
            VisualElement band = new() { pickingMode = PickingMode.Ignore };
            band.style.position = Position.Absolute;
            band.style.left = 0;
            band.style.right = 0;
            band.style.height = 0;
            band.AddToClassList(className);
            band.style.backgroundColor = className == ErrorLineClass ? new Color(0.95f, 0.25f, 0.2f, 0.14f)
                : new Color(0.2f, 0.65f, 0.8f, 0.10f);
            band.style.display = DisplayStyle.None;
            return band;
        }
        private static long NowMs() =>
            System.Diagnostics.Stopwatch.GetTimestamp() / (System.Diagnostics.Stopwatch.Frequency / 1000);
        private void SchedulePresentation()
        {
            if (_treeReleased) return;
            if (_field.panel == null) { RefreshPresentation(); return; }
            _presentation ??= _field.schedule.Execute(RefreshPresentation);
            _presentation.ExecuteLater(16);
        }
        private void RefreshPresentation()
        {
            if (_treeReleased) return;
            string source = _field.value ?? "";
            SetPlainTextMode(source.Length > CodeEditorLimits.MaximumAnalysisCharacters
                || CodeEditorTextUtility.CountLines(source) > MaximumDecoratedLines);
            RefreshGutter();
            RefreshHighlight();
            RefreshGuides();
            RefreshCurrentLineBand();
            RefreshErrorLine();
            RefreshHintLines();
            RefreshCaret(true);
            if (_focused && !_field.isReadOnly) RefreshCompletion();
        }
        private void SetPlainTextMode(bool plain)
        {
            if (_plainTextMode == plain) return;
            _plainTextMode = plain;
            _field.EnableInClassList("coreai-code-editor__field--plain", plain);
            _field.style.position = plain ? Position.Relative : Position.Absolute;
            if (_mirror != null) _mirror.style.display = plain ? DisplayStyle.None : DisplayStyle.Flex;
            if (_gutter != null) _gutter.style.display = plain ? DisplayStyle.None : DisplayStyle.Flex;
            VisualElement input = _field.Q("unity-text-input");
            if (input != null) input.style.color = plain ? HubModWidgets.Text : Color.clear;
            if (plain)
            {
                _mirrorLines?.ApplyChangedRange(Array.Empty<string>());
                _gutterLines?.ApplyByIndex(Array.Empty<string>());
                _guideLevels = Array.Empty<int>();
                TrimGuides(0);
                StopCaret();
                CloseCompletion();
                if (_currentLineBand != null) _currentLineBand.style.display = DisplayStyle.None;
                if (_errorBand != null) _errorBand.style.display = DisplayStyle.None;
                if (_errorLabel != null) _errorLabel.style.display = DisplayStyle.None;
                if (_hintLayer != null) _hintLayer.style.display = DisplayStyle.None;
                if (_hintLabel != null) _hintLabel.style.display = DisplayStyle.None;
            }
            else
            {
                if (_hintLayer != null) _hintLayer.style.display = DisplayStyle.Flex;
                if (_focused) StartCaret();
            }
            ApplyCaretColor();
        }
        private void RefreshHighlight()
        {
            if (_mirror == null || _plainTextMode)
            {
                return;
            }
            string text = _field?.value ?? "";
            string[] lines = text.Length == 0 ? new[] { "" } : text.Split('\n');
            string[] highlighted = text.Length <= CodeEditorLimits.MaximumAnalysisCharacters
                ? _language.HighlightLines(lines) : CodeEditorMarkup.EscapeLines(lines);
            _mirrorLines?.ApplyChangedRange(highlighted);
            ApplyLineHeights();
        }
        private void ApplyLineHeights()
        {
            if (_mirror == null || _plainTextMode)
            {
                return;
            }
            float lineHeight = MeasureLineHeight();
            float lastLineHeight = MeasureSingleLineHeight();
            if (lineHeight <= 0.5f || lastLineHeight <= 0.5f)
            {
                return;
            }
            _mirrorLines?.ApplyHeights(lineHeight, lastLineHeight);
            _gutterLines?.ApplyHeights(lineHeight, lastLineHeight);
        }
        private void RefreshGuides()
        {
            if (_guideLayer == null || _mirror == null || _plainTextMode)
            {
                return;
            }
            string text = _field?.value ?? "";
            float unit = text.Length == 0 ? 0f : MeasureIndentUnitWidth();
            float lineHeight = text.Length == 0 ? 0f : MeasureLineHeight();
            if (unit <= 0.5f || lineHeight <= 0.5f)
            {
                _guideLevels = Array.Empty<int>();
                TrimGuides(0);
                return;
            }
            string[] lines = text.Split('\n');
            _guideLevels = ComputeGuideLevels(lines);
            float padLeft = MirrorPadLeft();
            float padTop = MirrorPadTop();
            int used = 0;
            for (int row = 0; row < lines.Length; row++)
            {
                int barCount = Math.Min(CodeEditorTextUtility.GuideBarCount(_guideLevels[row]), 16);
                for (int level = 0; level < barCount; level++)
                {
                    if (used >= MaximumGuideBars) break;
                    PlaceGuide(used++, row, level,
                        new Vector3(padLeft + level * unit, padTop + row * lineHeight, lineHeight));
                }
            }
            TrimGuides(used);
            UpdateActiveGuide();
        }
        private void PlaceGuide(int index, int row, int level, Vector3 geometry)
        {
            if (index == _guideBars.Count)
            {
                VisualElement created = new() { pickingMode = PickingMode.Ignore };
                created.style.position = Position.Absolute;
                created.AddToClassList(GuideClass);
                created.style.width = 1f;
                created.style.backgroundColor = new Color(0.3f, 0.7f, 0.8f, 0.18f);
                _guideLayer.Add(created);
                _guideBars.Add(created);
                _guideGeometry.Add(new Vector3(float.NaN, float.NaN, float.NaN));
                _guideKeys.Add((-1, -1));
            }
            VisualElement bar = _guideBars[index];
            _guideKeys[index] = (row, level);
            if (_guideGeometry[index] != geometry)
            {
                _guideGeometry[index] = geometry;
                bar.style.left = geometry.x;
                bar.style.top = geometry.y;
                bar.style.height = geometry.z;
            }
        }
        private void TrimGuides(int count)
        {
            for (int i = _guideBars.Count - 1; i >= count; i--)
            {
                _guideBars[i].RemoveFromHierarchy();
                _guideBars.RemoveAt(i);
                _guideGeometry.RemoveAt(i);
                _guideKeys.RemoveAt(i);
            }
        }
        private static int[] ComputeGuideLevels(string[] lines)
        {
            int[] levels = new int[lines.Length];
            int[] ownLevel = new int[lines.Length];
            bool[] blank = new bool[lines.Length];
            for (int i = 0; i < lines.Length; i++)
            {
                blank[i] = lines[i].TrimEnd().Length == 0;
                ownLevel[i] = LeadingSpaces(lines[i]) / IndentSpaces;
            }
            int prev = 0;
            int[] prevNonBlank = new int[lines.Length];
            for (int i = 0; i < lines.Length; i++)
            {
                prevNonBlank[i] = prev;
                if (!blank[i])
                {
                    prev = ownLevel[i];
                }
            }
            int next = 0;
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                if (blank[i])
                {
                    levels[i] = Math.Min(prevNonBlank[i], next);
                }
                else
                {
                    levels[i] = ownLevel[i];
                    next = ownLevel[i];
                }
            }
            return levels;
        }
        private void UpdateActiveGuide()
        {
            if (_guideLayer == null || _field == null || _guideLevels.Length == 0)
            {
                return;
            }
            (int caretRow, float _) = MeasureCaretColumn(_field.cursorIndex);
            int activeLevel = caretRow < _guideLevels.Length ? _guideLevels[caretRow] - 1 : -1;
            int from = caretRow, to = caretRow;
            if (activeLevel >= 0)
            {
                while (from > 0 && _guideLevels[from - 1] > activeLevel)
                {
                    from--;
                }
                while (to < _guideLevels.Length - 1 && _guideLevels[to + 1] > activeLevel)
                {
                    to++;
                }
            }
            for (int i = 0; i < _guideBars.Count; i++)
            {
                VisualElement bar = _guideBars[i];
                (int row, int level) = _guideKeys[i];
                bool active = activeLevel >= 0 && level == activeLevel && row >= from && row <= to;
                bar.EnableInClassList(ActiveGuideClass, active);
            }
        }
        private static int LeadingSpaces(string line)
        {
            int count = 0;
            while (count < line.Length && line[count] == ' ')
            {
                count++;
            }
            return count;
        }
        private float MeasureIndentUnitWidth()
        {
            if (_mirror?.panel == null) return 0f;
            string spaces = new(' ', IndentSpaces * 4);
            float withSpaces = _mirror.MeasureTextSize("A" + spaces + "A", 0, VisualElement.MeasureMode.Undefined, 0, VisualElement.MeasureMode.Undefined).x;
            float withoutSpaces = _mirror.MeasureTextSize("AA", 0, VisualElement.MeasureMode.Undefined, 0, VisualElement.MeasureMode.Undefined).x;
            return (withSpaces - withoutSpaces) / 4f;
        }
        private float MeasureSingleLineHeight() =>
            _mirror?.panel == null ? 0f : _mirror.MeasureTextSize("A", 0, VisualElement.MeasureMode.Undefined, 0, VisualElement.MeasureMode.Undefined).y;
        private const int LineStepProbeLines = 257;
        private static readonly string LineStepProbe =
            new System.Text.StringBuilder().Insert(0, "A\n", LineStepProbeLines - 1).Append('A').ToString();
        private float MeasureLineHeight()
        {
            if (_mirror?.panel == null) return 0f;
            float many = _mirror.MeasureTextSize(LineStepProbe, 0, VisualElement.MeasureMode.Undefined, 0, VisualElement.MeasureMode.Undefined).y;
            float one = _mirror.MeasureTextSize("A", 0, VisualElement.MeasureMode.Undefined, 0, VisualElement.MeasureMode.Undefined).y;
            return (many - one) / (LineStepProbeLines - 1);
        }
        private float MirrorPadLeft() => ResolvedOrZero(_mirror?.resolvedStyle.paddingLeft);
        private float MirrorPadTop() => ResolvedOrZero(_mirror?.resolvedStyle.paddingTop);
        private static float ResolvedOrZero(float? value) =>
            value.HasValue && !float.IsNaN(value.Value) ? value.Value : 0f;
        private void ApplyCaretColor()
        {
            if (_field?.textSelection != null)
            {
                Color caretColor = _field.customStyle.TryGetValue(CaretColorProperty, out Color themedColor)
                    ? themedColor
                    : HubModWidgets.Accent;
                _field.textSelection.cursorColor = _caretBar != null && !_plainTextMode
                    ? new Color(0f, 0f, 0f, 0f)
                    : caretColor;
            }
        }
        private void EnforceNativeCaretHidden()
        {
            if (_field != null && _field.textSelection != null && !_plainTextMode)
            {
                _field.textSelection.cursorColor = new Color(0f, 0f, 0f, 0f);
            }
        }
        private void StartCaret()
        {
            if (_caretBar == null || _plainTextMode || _treeReleased)
            {
                return;
            }
            _caretBlinkTick = 0;
            RefreshCaret(true);
            _caretBar.style.display = DisplayStyle.Flex;
            _caretPoll ??= _field.schedule.Execute(TickCaret).Every(CaretPollMs);
            _caretPoll.Resume();
        }
        private void StopCaret()
        {
            _caretPoll?.Pause();
            if (_caretBar != null)
            {
                _caretBar.style.display = DisplayStyle.None;
            }
            if (_caretLineBand != null) _caretLineBand.style.display = DisplayStyle.None;
            UpdateCaretGutterLine(null);
            HideBracketBoxes();
        }
        private bool CaretChangedSinceRefresh() =>
            _field != null
            && (_field.cursorIndex != _caretLastIndex
                || _field.selectIndex != _caretLastSelectIndex
                || (_field.value?.Length ?? 0) != _caretLastTextLength);
        private void RetryPendingReveal()
        {
            if (!_revealPending)
            {
                return;
            }
            _revealPending = false;
            RefreshCaret(true, reveal: true);
        }
        private void TickCaret()
        {
            if (_caretBar == null || _field == null || _plainTextMode || _treeReleased)
            {
                return;
            }
            EnforceNativeCaretHidden();
            if (CaretChangedSinceRefresh())
            {
                _autoClose.Prune(_field.cursorIndex);
                RefreshCaret(false, reveal: true);
                UpdateActiveGuide();
                if (IsCompletionOpen)
                {
                    RefreshCompletion();
                }
                _caretBlinkTick = 0;
            }
            else
            {
                _caretBlinkTick++;
            }
            _caretBar.style.visibility = _caretBlinkTick / CaretBlinkTicks % 2 == 0
                ? Visibility.Visible
                : Visibility.Hidden;
        }
        private void RefreshCaret(bool force, bool reveal = false)
        {
            if (_caretBar == null || _field == null || _plainTextMode || _treeReleased)
            {
                return;
            }
            int caret = _field.cursorIndex;
            int selection = _field.selectIndex;
            int textLength = _field.value?.Length ?? 0;
            bool unrevealed = _focused
                              && (caret != _revealedIndex
                                  || selection != _revealedSelectIndex
                                  || textLength != _revealedTextLength);
            if (!force
                && !unrevealed
                && caret == _caretLastIndex
                && selection == _caretLastSelectIndex
                && textLength == _caretLastTextLength)
            {
                return;
            }
            _caretLastIndex = caret;
            _caretLastSelectIndex = selection;
            _caretLastTextLength = textLength;
            float lineHeight = MeasureLineHeight();
            if (lineHeight <= 0.5f)
            {
                _caretBar.style.visibility = Visibility.Hidden;
                _revealPending |= reveal;
                return;
            }
            (int row, float x) = MeasureCaretColumn(caret);
            float top = MirrorPadTop() + row * lineHeight;
            _caretBar.style.left = x - CaretWidth / 2f;
            _caretBar.style.top = top;
            _caretBar.style.height = lineHeight;
            RefreshCaretLineBand(row, lineHeight);
            UpdateCaretGutterLine(row + 1);
            RefreshBracketPair();
            if (reveal || unrevealed)
            {
                EnsureCaretVisible(x, top, lineHeight);
            }
        }
        private void RefreshCaretLineBand(int row, float lineHeight)
        {
            if (_caretLineBand == null)
            {
                return;
            }
            bool hasSelection = _field != null && _field.cursorIndex != _field.selectIndex;
            if (!_focused || _markerLine.HasValue || _errorLine.HasValue || hasSelection || lineHeight <= 0.5f)
            {
                _caretLineBand.style.display = DisplayStyle.None;
                return;
            }
            _caretLineBand.style.top = MirrorPadTop() + row * lineHeight;
            _caretLineBand.style.height = lineHeight;
            _caretLineBand.style.display = DisplayStyle.Flex;
        }
        private void EnsureCaretVisible(float caretX, float caretTop, float lineHeight)
        {
            VisualElement stack = _mirror?.parent;
            if (!_focused || _scroll == null || stack == null || stack.panel == null)
            {
                _revealPending = false;
                _revealNote = $"reveal #{++_revealCalls}";
                return;
            }
            VisualElement content = _scroll.contentContainer;
            float viewWidth = _scroll.contentViewport.layout.width;
            float viewHeight = _scroll.contentViewport.layout.height;
            if (!TryLayoutOffset(stack, content, out Vector2 stackOrigin)
                || float.IsNaN(viewWidth) || float.IsNaN(viewHeight) || viewWidth <= 1f || viewHeight <= 1f)
            {
                _revealPending = true;
                _revealNote = $"reveal #{++_revealCalls}";
                ScheduleRevealRetry();
                return;
            }
            _revealedIndex = _field.cursorIndex;
            _revealedSelectIndex = _field.selectIndex;
            _revealedTextLength = _field.value?.Length ?? 0;
            Vector2 topLeft = stackOrigin + new Vector2(caretX, caretTop);
            Vector2 offset = _scroll.scrollOffset;
            if (topLeft.x - CaretScrollMargin < offset.x)
            {
                offset.x = topLeft.x - CaretScrollMargin;
            }
            else if (topLeft.x + CaretWidth + CaretScrollMargin > offset.x + viewWidth)
            {
                offset.x = topLeft.x + CaretWidth + CaretScrollMargin - viewWidth;
            }
            if (topLeft.y < offset.y)
            {
                offset.y = topLeft.y;
            }
            else if (topLeft.y + lineHeight > offset.y + viewHeight)
            {
                offset.y = topLeft.y + lineHeight - viewHeight;
            }
            float desiredY = offset.y;
            float contentWidth = Mathf.Max(content.layout.width, stackOrigin.x + stack.layout.width);
            float contentHeight = Mathf.Max(content.layout.height, stackOrigin.y + stack.layout.height);
            bool layoutBehind = topLeft.y + lineHeight > contentHeight + 0.5f;
            offset.x = Mathf.Clamp(offset.x, 0f, Mathf.Max(0f, contentWidth - viewWidth));
            offset.y = Mathf.Clamp(offset.y, 0f, Mathf.Max(0f, contentHeight - viewHeight));
            if (!Mathf.Approximately(offset.x, _scroll.scrollOffset.x)
                || !Mathf.Approximately(offset.y, _scroll.scrollOffset.y))
            {
                _scroll.scrollOffset = offset;
            }
            _revealPending = layoutBehind
                             || !Mathf.Approximately(offset.x, _scroll.scrollOffset.x)
                             || !Mathf.Approximately(offset.y, _scroll.scrollOffset.y);
            _revealNote = $"reveal #{++_revealCalls}";
            if (_revealPending)
            {
                ScheduleRevealRetry();
            }
            else
            {
                _revealRetries = 0;
            }
        }
        private void ScheduleRevealRetry()
        {
            if (_field == null || _revealRetries >= MaxRevealRetries)
            {
                return;
            }
            _revealRetries++;
            _revealRetry ??= _field.schedule.Execute(RetryPendingReveal);
            _revealRetry.ExecuteLater(RevealRetryDelayMs);
        }
        internal string RevealDiagnostics =>
            $"{_revealNote}; pending={_revealPending}; caret={_revealedIndex}";
        private bool TryLayoutOffset(VisualElement descendant, VisualElement ancestor, out Vector2 offset)
        {
            offset = Vector2.zero;
            for (VisualElement current = descendant; current != null; current = current.hierarchy.parent)
            {
                if (current == ancestor)
                {
                    return !float.IsNaN(offset.x) && !float.IsNaN(offset.y);
                }
                offset += current.layout.position;
                if (_scroll != null && current == _scroll.contentContainer)
                {
                    offset -= _scroll.scrollOffset;
                }
                else
                {
                    Vector3 translate = current.resolvedStyle.translate;
                    offset += new Vector2(translate.x, translate.y);
                }
            }
            offset = Vector2.zero;
            return false;
        }
        private void SyncGutterOffset()
        {
            if (_gutter == null || _scroll == null)
            {
                return;
            }
            _gutter.style.translate = new Translate(0f, -_scroll.scrollOffset.y);
        }
        private VisualElement Surface => _scroll?.parent ?? _mirror?.parent;
        private void RegisterSurfaceClicks()
        {
            VisualElement surface = Surface;
            if (surface == null || _field == null)
            {
                return;
            }
            EventCallback<PointerDownEvent> onDown = evt =>
            {
                _surfacePointerId = evt.button == 0 && IsSurfaceTarget(evt.target as VisualElement)
                    ? evt.pointerId
                    : PointerId.invalidPointerId;
            };
            EventCallback<PointerUpEvent> onUp = evt =>
            {
                if (evt.pointerId != _surfacePointerId)
                {
                    return;
                }
                _surfacePointerId = PointerId.invalidPointerId;
                if (evt.currentTarget is VisualElement current)
                {
                    PlaceCaretAtSurfacePoint(current, evt.localPosition);
                }
            };
            EventCallback<PointerCancelEvent> onCancel = _ => _surfacePointerId = PointerId.invalidPointerId;
            surface.RegisterCallback(onDown);
            surface.RegisterCallback(onUp);
            surface.RegisterCallback(onCancel);
            _releaseTree += () =>
            {
                surface.UnregisterCallback(onDown);
                surface.UnregisterCallback(onUp);
                surface.UnregisterCallback(onCancel);
                _surfacePointerId = PointerId.invalidPointerId;
            };
        }
        private bool IsSurfaceTarget(VisualElement target)
        {
            if (target == null)
            {
                return false;
            }
            VisualElement input = _field.Q(className: TextField.inputUssClassName);
            TextElement nativeText = input?.Q<TextElement>();
            return !IsInside(target, nativeText)
                   && !IsInside(target, _completionPopup)
                   && !IsInside(target, _gotoPrompt)
                   && !IsInside(target, _scroll?.verticalScroller)
                   && !IsInside(target, _scroll?.horizontalScroller);
        }
        private static bool IsInside(VisualElement element, VisualElement ancestor)
        {
            if (ancestor == null)
            {
                return false;
            }
            for (VisualElement current = element; current != null; current = current.hierarchy.parent)
            {
                if (current == ancestor)
                {
                    return true;
                }
            }
            return false;
        }
        private void PlaceCaretAtSurfacePoint(VisualElement surface, Vector2 local)
        {
            VisualElement stack = _mirror?.parent;
            if (_field == null || stack == null || stack.panel == null
                || !TryLayoutOffset(stack, surface, out Vector2 stackOrigin))
            {
                return;
            }
            int caret = CaretIndexAtStackPoint(local - stackOrigin);
            FocusSelection(caret, caret);
            RefreshCaret(true, reveal: true);
        }
        internal int CaretIndexAtStackPoint(Vector2 point)
        {
            string text = _field?.value ?? "";
            if (_mirror == null || _plainTextMode)
            {
                return text.Length;
            }
            float bounds = _mirror.MeasureTextSize("AA", 0,
                VisualElement.MeasureMode.Undefined, 0, VisualElement.MeasureMode.Undefined).x;
            return CodeEditorTextUtility.CaretIndexAtPoint(
                text,
                point.x - MirrorPadLeft(),
                point.y - MirrorPadTop(),
                MeasureLineHeight(),
                prefix => _mirror.MeasureTextSize("A" + prefix + "A", 0,
                    VisualElement.MeasureMode.Undefined, 0, VisualElement.MeasureMode.Undefined).x - bounds);
        }
        private (int row, float x) MeasureCaretColumn(int caret)
        {
            string text = _field.value ?? "";
            caret = Mathf.Clamp(caret, 0, text.Length);
            int lineStart = CodeEditorTextUtility.LineStartIndex(text, caret);
            int row = 0;
            for (int i = 0; i < lineStart; i++)
            {
                if (text[i] == '\n')
                {
                    row++;
                }
            }
            float x = MirrorPadLeft();
            if (caret > lineStart)
            {
                string beforeCaret = text.Substring(lineStart, caret - lineStart);
                float with = _mirror.MeasureTextSize("A" + beforeCaret + "A", 0,
                    VisualElement.MeasureMode.Undefined, 0, VisualElement.MeasureMode.Undefined).x;
                float bounds = _mirror.MeasureTextSize("AA", 0,
                    VisualElement.MeasureMode.Undefined, 0, VisualElement.MeasureMode.Undefined).x;
                x += with - bounds;
            }
            return (row, x);
        }
        public string GetText() => _field?.value ?? "";
        public void SetText(string text) => SetText(text, null);
        public void SetText(string text, string documentKey)
        {
            InvalidateDocumentWork();
            if (_treeReleased) return;
            string value = CodeEditorTextUtility.NormalizeEditorText(text ?? "");
            if (value.Length > CodeEditorLimits.MaximumDocumentCharacters)
                throw new ArgumentException("The document exceeds the editor source limit.", nameof(text));
            _autoClose.Clear();
            if (_field != null)
            {
                _suppressHistory = true;
                try
                {
                    _field.value = value;
                }
                finally
                {
                    _suppressHistory = false;
                }
            }
            SelectDocumentHistory(documentKey, value);
            RefreshPresentation();
            ClearHintLines();
            RefreshGutter();
        }
        public void ReplaceTextUndoable(string text, int insertedAtLine = 0, int insertedLines = 0)
        {
            if (_treeReleased) return;
            ClearQuoteInsertion();
            string value = CodeEditorTextUtility.NormalizeEditorText(text ?? "");
            if (value.Length > CodeEditorLimits.MaximumDocumentCharacters)
                throw new ArgumentException("The document exceeds the editor source limit.", nameof(text));
            _autoClose.Clear();
            if (_field == null)
            {
                return;
            }
            string before = _field.value ?? "";
            if (!string.Equals(_history.CurrentText, before, StringComparison.Ordinal))
            {
                _history.Push(before, _field.cursorIndex, NowMs(), true);
            }
            _suppressHistory = true;
            try
            {
                _field.value = value;
            }
            finally
            {
                _suppressHistory = false;
            }
            _history.Push(value, 0, NowMs(), true);
            if (insertedLines > 0 && insertedAtLine > 0)
            {
                ShiftHintLines(insertedAtLine, insertedLines);
            }
            else
            {
                ClearHintLines();
            }
            RefreshGutter();
        }
        private void ShiftHintLines(int fromLine, int delta)
        {
            if (_hintLines.Count == 0)
            {
                return;
            }
            List<int> shifted = CodeLineSelection.ShiftFrom(_hintLines, fromLine, delta);
            _hintLines = shifted;
            _hintLineSet.Clear();
            _hintLineSet.UnionWith(shifted);
            RefreshHintLines();
        }
        private void SelectDocumentHistory(string documentKey, string text)
        {
            if (documentKey != null
                && _documentHistories.TryGetValue(documentKey, out CodeEditorHistory kept)
                && string.Equals(kept.CurrentText, text, StringComparison.Ordinal))
            {
                _history = kept;
                return;
            }
            _history = new CodeEditorHistory();
            _history.Push(text, _field?.cursorIndex ?? 0, NowMs(), true);
            if (documentKey == null)
            {
                return;
            }
            if (_documentHistories.Count >= DocumentHistoryLimit && !_documentHistories.ContainsKey(documentKey))
            {
                _documentHistories.Clear();
            }
            _documentHistories[documentKey] = _history;
        }
        public void SetCurrentLineMarker(int? line)
        {
            _markerLine = line;
            RefreshGutter();
            RefreshCurrentLineBand();
            RefreshErrorLine();
            RefreshHintLines();
            if (_caretBar != null)
            {
                RefreshCaret(true);
            }
            else
            {
                if (_caretLineBand != null) _caretLineBand.style.display = DisplayStyle.None;
            }
        }
        public void SetErrorLine(int? line)
        {
            _errorMessage = null;
            ApplyErrorLine(line);
        }
        public void SetHintLines(IReadOnlyList<int> lines, string hint)
        {
            HashSet<int> unique = new();
            if (lines != null)
            {
                foreach (int line in lines)
                {
                    if (line >= 1 && line <= MaximumDecoratedLines && unique.Count < MaximumDecoratedLines)
                    {
                        unique.Add(line);
                    }
                }
            }
            if (unique.Count == 0)
            {
                ClearHintLines();
                return;
            }
            List<int> sorted = new(unique);
            sorted.Sort();
            _hintLines = sorted;
            _hintLineSet.Clear();
            _hintLineSet.UnionWith(sorted);
            _hintText = CodeEditorTextUtility.ShortErrorMessage(hint);
            RefreshGutter();
            RefreshHintLines(true);
            ScrollRowIntoView(sorted[0] - 1);
        }
        public void ClearHintLines()
        {
            if (_hintLines.Count == 0 && _hintText == null)
            {
                return;
            }
            _hintLines = Array.Empty<int>();
            _hintLineSet.Clear();
            _hintText = null;
            _hintEnterPending = false;
            _hintLabelText = null;
            _hintLabelRow = -1;
            for (int i = 0; i < _hintBands.Count; i++)
            {
                _hintBands[i].style.display = DisplayStyle.None;
            }
            if (_hintLabel != null) _hintLabel.style.display = DisplayStyle.None;
            RefreshGutter();
        }
        private void RefreshHintLines(bool playEnter = false)
        {
            if (_hintLayer == null || _plainTextMode)
            {
                return;
            }
            float lineHeight = _hintLines.Count == 0 ? 0f : MeasureLineHeight();
            if (_hintLines.Count == 0 || lineHeight <= 0.5f)
            {
                _hintEnterPending |= playEnter && _hintLines.Count > 0;
                HideHintElements(0);
                return;
            }
            bool animate = playEnter || _hintEnterPending;
            _hintEnterPending = false;
            string text = _field?.value ?? "";
            int totalLines = CodeEditorTextUtility.CountLines(text);
            float padTop = MirrorPadTop();
            List<(int Start, int End)> ranges = CodeLineSelection.ToRanges(_hintLines);
            int used = 0;
            foreach ((int start, int end) in ranges)
            {
                if (start > totalLines)
                {
                    break;
                }
                VisualElement band = HintBand(used++);
                float top = padTop + (start - 1) * lineHeight;
                float height = (Math.Min(end, totalLines) - start + 1) * lineHeight;
                bool alreadyThere = band.style.display != DisplayStyle.None
                                    && Mathf.Approximately(band.style.top.value.value, top)
                                    && Mathf.Approximately(band.style.height.value.value, height);
                band.style.top = top;
                band.style.height = height;
                band.style.display = DisplayStyle.Flex;
            }
            HideHintElements(used);
            RefreshHintLabel(ranges, totalLines, lineHeight, text, animate);
        }
        private void RefreshHintLabel(
            List<(int Start, int End)> ranges, int totalLines, float lineHeight, string text, bool animate)
        {
            if (_hintLabel == null)
            {
                return;
            }
            int row = ranges[0].Start - 1;
            bool errorLabelOnRow = ErrorBandShown && _errorLine.Value - 1 == row && !string.IsNullOrEmpty(_errorMessage);
            if (string.IsNullOrEmpty(_hintText) || row >= totalLines || errorLabelOnRow || _field == null)
            {
                _hintLabel.style.display = DisplayStyle.None;
                return;
            }
            (int _, float endX) = MeasureCaretColumn(HintLabelLineEnd(text, row));
            float left = endX + MeasureIndentUnitWidth();
            float top = MirrorPadTop() + row * lineHeight;
            bool alreadyThere = _hintLabel.style.display != DisplayStyle.None
                                && string.Equals(_hintLabel.text, _hintText, StringComparison.Ordinal)
                                && Mathf.Approximately(_hintLabel.style.left.value.value, left)
                                && Mathf.Approximately(_hintLabel.style.top.value.value, top);
            _hintLabel.text = _hintText;
            _hintLabel.style.left = left;
            _hintLabel.style.top = top;
            _hintLabel.style.height = lineHeight;
            _hintLabel.style.display = DisplayStyle.Flex;
        }
        private int HintLabelLineEnd(string text, int row)
        {
            if (_hintLabelRow != row || !ReferenceEquals(_hintLabelText, text))
            {
                int lineStart = CodeEditorTextUtility.CaretIndexForLine(text, row + 1);
                int newline = text.IndexOf('\n', lineStart);
                _hintLabelLineEnd = newline < 0 ? text.Length : newline;
                _hintLabelRow = row;
                _hintLabelText = text;
            }
            return _hintLabelLineEnd;
        }
        private VisualElement HintBand(int index)
        {
            while (_hintBands.Count <= index)
            {
                VisualElement created = CreateLineBand(HintLineClass);
                _hintLayer.Add(created);
                _hintBands.Add(created);
            }
            return _hintBands[index];
        }
        private void HideHintElements(int firstUnused)
        {
            for (int i = firstUnused; i < _hintBands.Count; i++)
            {
                _hintBands[i].style.display = DisplayStyle.None;
            }
            if (firstUnused == 0)
            {
                if (_hintLabel != null) _hintLabel.style.display = DisplayStyle.None;
            }
        }
        private void RefreshCurrentLineBand()
        {
            if (_currentLineBand == null || _plainTextMode)
            {
                return;
            }
            int row = _markerLine.HasValue && !ErrorBandShown ? _markerLine.Value - 1 : -1;
            float lineHeight = row < 0 ? 0f : MeasureLineHeight();
            if (row < 0 || lineHeight <= 0.5f)
            {
                _currentLineBand.style.display = DisplayStyle.None;
                return;
            }
            _currentLineBand.style.top = MirrorPadTop() + row * lineHeight;
            _currentLineBand.style.height = lineHeight;
            _currentLineBand.style.display = DisplayStyle.Flex;
        }
        private void RefreshErrorLine()
        {
            if (_errorBand == null || _plainTextMode)
            {
                return;
            }
            string text = _field?.value ?? "";
            int row = ErrorBandShown ? _errorLine.Value - 1 : -1;
            float lineHeight = row < 0 || row >= CodeEditorTextUtility.CountLines(text) ? 0f : MeasureLineHeight();
            if (lineHeight <= 0.5f)
            {
                _errorBand.style.display = DisplayStyle.None;
                if (_errorLabel != null) _errorLabel.style.display = DisplayStyle.None;
                return;
            }
            float top = MirrorPadTop() + row * lineHeight;
            _errorBand.style.top = top;
            _errorBand.style.height = lineHeight;
            _errorBand.style.display = DisplayStyle.Flex;
            if (_errorLabel == null || string.IsNullOrEmpty(_errorMessage) || _field == null)
            {
                if (_errorLabel != null) _errorLabel.style.display = DisplayStyle.None;
                return;
            }
            int lineStart = CodeEditorTextUtility.CaretIndexForLine(text, row + 1);
            int lineEnd = text.IndexOf('\n', lineStart);
            (int _, float endX) = MeasureCaretColumn(lineEnd < 0 ? text.Length : lineEnd);
            _errorLabel.text = _errorMessage;
            _errorLabel.style.left = endX + MeasureIndentUnitWidth();
            _errorLabel.style.top = top;
            _errorLabel.style.height = lineHeight;
            _errorLabel.style.display = DisplayStyle.Flex;
        }
        private bool ErrorBandShown =>
            _errorLine.HasValue && (!_markerLine.HasValue || _markerLine.Value == _errorLine.Value);
        private void ApplyErrorLine(int? line)
        {
            _errorLine = line;
            RefreshGutter();
            RefreshErrorLine();
            RefreshCurrentLineBand();
            RefreshHintLines();
            if (_caretBar != null)
            {
                RefreshCaret(true);
            }
            else
            {
                if (_caretLineBand != null) _caretLineBand.style.display = DisplayStyle.None;
            }
        }
        public void ShowError(CodeErrorLocation location)
        {
            _errorMessage = CodeEditorTextUtility.ShortErrorMessage(location.Message);
            ApplyErrorLine(location.Line);
            if (!location.Line.HasValue || _field == null)
            {
                return;
            }
            if (_focused)
            {
                int caret = CodeEditorTextUtility.CaretIndexForError(_field.value, location.Line.Value, location.Column);
                FocusSelection(caret, caret);
                RefreshCaret(true, reveal: true);
            }
            else
            {
                ScrollRowIntoView(location.Line.Value - 1);
            }
        }
        private void ScrollRowIntoView(int row)
        {
            VisualElement stack = _mirror?.parent;
            if (_scroll == null || stack?.panel == null)
            {
                return;
            }
            VisualElement content = _scroll.contentContainer;
            float lineHeight = MeasureLineHeight();
            float viewHeight = _scroll.contentViewport.layout.height;
            if (lineHeight <= 0.5f || float.IsNaN(viewHeight) || viewHeight <= 1f
                || !TryLayoutOffset(stack, content, out Vector2 origin))
            {
                return;
            }
            float top = origin.y + MirrorPadTop() + row * lineHeight;
            Vector2 offset = _scroll.scrollOffset;
            if (top < offset.y)
            {
                offset.y = top;
            }
            else if (top + lineHeight > offset.y + viewHeight)
            {
                offset.y = top + lineHeight - viewHeight;
            }
            else
            {
                return;
            }
            float contentHeight = Mathf.Max(content.layout.height, origin.y + stack.layout.height);
            offset.y = Mathf.Clamp(offset.y, 0f, Mathf.Max(0f, contentHeight - viewHeight));
            _scroll.scrollOffset = offset;
        }
        public void GoToLine(int line)
        {
            if (_field == null)
            {
                return;
            }
            int caret = CodeEditorTextUtility.CaretIndexForLine(_field.value, line);
            FocusSelection(caret, caret);
            RefreshCaret(true, reveal: true);
        }
        public void SetEditable(bool editable)
        {
            if (_field != null)
            {
                _field.isReadOnly = !editable;
                if (!editable) CloseCompletion();
            }
        }
        private void OnKeyDown(KeyDownEvent evt)
        {
            if (_treeReleased || _field == null || _field.isReadOnly)
            {
                return;
            }
            PrepareSelectionForInput();
            if (SwallowAcceptPairHalf(evt))
            {
                return;
            }
            if (evt.altKey && HandleLineMotion(evt))
            {
                return;
            }
            if (IsCompletionOpen)
            {
                AcceptKey acceptKey = ClassifyAcceptKey(evt, out KeyHalf arrived);
                if (acceptKey == AcceptKey.Enter
                    && !CodeCompletionEngine.EnterAccepts(_completionResult, _completionNavigated))
                {
                    CloseCompletion();
                }
                else if (acceptKey != AcceptKey.None)
                {
                    if (AcceptCompletion(_completionSelected, acceptKey, arrived))
                    {
                        SwallowKey(evt);
                        return;
                    }
                }
                else
                {
                    switch (evt.keyCode)
                    {
                        case KeyCode.DownArrow:
                        case KeyCode.UpArrow:
                            evt.StopImmediatePropagation();
                            evt.PreventDefault();
                            return;
                        case KeyCode.Escape:
                            CloseCompletion();
                            evt.StopImmediatePropagation();
                            evt.PreventDefault();
                            return;
                    }
                    if (MovesCaretAwayFromCompletion(evt))
                    {
                        CloseCompletion();
                    }
                }
            }
            if ((evt.ctrlKey || evt.actionKey) && !evt.altKey && !evt.shiftKey
                && (evt.keyCode == KeyCode.Space || (evt.keyCode == KeyCode.None && evt.character == ' ')))
            {
                ApplyOperation(evt, _field.value, _field.selectIndex, _field.cursorIndex);
                RequestCompletion();
                return;
            }
            if ((evt.ctrlKey || evt.commandKey) && evt.keyCode == KeyCode.None && evt.character == '\t')
            {
                ApplyOperation(evt, _field.value, _field.selectIndex, _field.cursorIndex);
                return;
            }
            if (evt.actionKey)
            {
                if (evt.keyCode == KeyCode.Z && !evt.shiftKey)
                {
                    if (TryRestore(_history.Undo(_field.value, _field.cursorIndex, _field.selectIndex)))
                    {
                        evt.StopImmediatePropagation();
                        evt.PreventDefault();
                    }
                    return;
                }
                if (evt.keyCode == KeyCode.Y
                    || (evt.keyCode == KeyCode.Z && evt.shiftKey))
                {
                    if (TryRestore(_history.Redo()))
                    {
                        evt.StopImmediatePropagation();
                        evt.PreventDefault();
                    }
                    return;
                }
                if (HandleActionKeyEditing(evt))
                {
                    return;
                }
            }
            if (evt.keyCode == KeyCode.Home && !evt.actionKey)
            {
                int home = CodeEditorTextUtility.SmartHomeCaret(_field.value, _field.cursorIndex);
                ApplyOperation(evt, _field.value, evt.shiftKey ? _field.selectIndex : home, home);
                return;
            }
            if (evt.keyCode == KeyCode.Backspace
                && _field.cursorIndex == _field.selectIndex
                && CodeEditorTextUtility.TryBackspacePair(_field.value, _field.cursorIndex,
                    out string pairBackspaceText, out int pairBackspaceCaret))
            {
                ApplyOperation(evt, pairBackspaceText, pairBackspaceCaret, pairBackspaceCaret);
                return;
            }
            if (evt.keyCode == KeyCode.Backspace
                && _field.cursorIndex == _field.selectIndex
                && CodeEditorTextUtility.TryBackspaceUnindent(_field.value, _field.cursorIndex,
                    out string unindentedText, out int unindentedCaret))
            {
                ApplyEdit(unindentedText, unindentedCaret);
                evt.StopImmediatePropagation();
                evt.PreventDefault();
                return;
            }
            bool printable = !evt.actionKey && !evt.altKey && evt.character != '\0' && !char.IsControl(evt.character);
            if (printable && _language.LanguageId == "python" && evt.character == ':'
                && CodeEditorTextUtility.TryAlignBlockContinuation(_field.value, _field.selectIndex,
                    _field.cursorIndex, out string alignedText, out int alignedCaret))
            {
                ApplyOperation(evt, alignedText, alignedCaret, alignedCaret);
                return;
            }
            if (printable && !_plainTextMode && TryAutoClose(evt))
            {
                return;
            }
            if (evt.keyCode == KeyCode.Tab)
            {
                int selA = Mathf.Min(_field.selectIndex, _field.cursorIndex);
                int selB = Mathf.Max(_field.selectIndex, _field.cursorIndex);
                bool multiLine = selB > selA
                                 && (_field.value ?? "").IndexOf('\n', selA, selB - selA) >= 0;
                if (evt.shiftKey)
                {
                    string updated = CodeEditorTextUtility.UnindentSelection(
                        _field.value, selA, selB, out int ns, out int ne);
                    ApplyOperation(evt, updated, ns, ne);
                    return;
                }
                if (multiLine)
                {
                    string updated = CodeEditorTextUtility.IndentSelection(
                        _field.value, selA, selB, out int ns, out int ne);
                    ApplyOperation(evt, updated, ns, ne);
                    return;
                }
                string tabbed = CodeEditorTextUtility.InsertTab(_field.value, selA, selB, out int caret);
                ApplyOperation(evt, tabbed, caret, caret, typing: true);
                return;
            }
            if (evt.keyCode is KeyCode.Return or KeyCode.KeypadEnter)
            {
                CodeEnterPlan plan = _language.PlanEnter(
                    _field.value, _field.selectIndex, _field.cursorIndex);
                if (!plan.IsEmpty)
                {
                    CodeEditorHistory history = _history;
                    int version = history.Version;
                    int generation = _documentGeneration;
                    string before = _field.value ?? "";
                    int from = Math.Min(_field.cursorIndex, _field.selectIndex);
                    int to = Math.Max(_field.cursorIndex, _field.selectIndex);
                    string expected = before.Remove(from, to - from).Insert(from, "\n");
                    _newLineSchedule?.Pause();
                    _newLineSchedule = _field.schedule.Execute(() =>
                    {
                        if (!_treeReleased && generation == _documentGeneration && ReferenceEquals(history, _history)
                            && string.Equals(_field.value, expected, StringComparison.Ordinal))
                            CompleteNativeNewLine(plan, history, version);
                    });
                }
                RefreshGutter();
            }
        }
        private void CompleteNativeNewLine(CodeEnterPlan plan, CodeEditorHistory history, int historyVersion)
        {
            if (_field == null || _treeReleased)
            {
                return;
            }
            string current = _field.value ?? "";
            int caret = _field.cursorIndex;
            if (caret <= 0 || caret > current.Length || current[caret - 1] != '\n' || caret != _field.selectIndex)
            {
                return;
            }
            string updated = CodeEditorTextUtility.ApplyEnterPlan(current, caret, plan, out int newCaret);
            _suppressHistory = true;
            try
            {
                _field.value = updated;
            }
            finally
            {
                _suppressHistory = false;
            }
            Selection = new CodeSelection(newCaret, newCaret);
            bool newLineStep = ReferenceEquals(_history, history)
                               && _history.Version == historyVersion + 1
                               && string.Equals(_history.CurrentText, current, StringComparison.Ordinal);
            if (newLineStep)
            {
                _history.AmendTop(updated, newCaret);
            }
            else
            {
                _history.Push(updated, newCaret, NowMs(), true);
            }
            RefreshGutter();
        }
        private bool IsCodeAtCaret() => IsCodeAtPosition(_field.value ?? "", _field.cursorIndex);
        private bool IsCodeAtPosition(string source, int caret) => _language is ICodeLanguageContext context
            ? context.IsCodeAt(source, caret)
            : CodeEditorTextUtility.IsCodeAt(source, caret);
        private bool FindBracketPair(string source, int caret, out int first, out int second) =>
            _language is ICodeLanguageContext context
                ? context.TryFindBracketPair(source, caret, out first, out second)
                : CodeEditorTextUtility.TryFindBracketPair(source, caret, out first, out second, _bracketMask);
        private bool TryAutoClose(KeyDownEvent evt)
        {
            string text = _field.value ?? "";
            int caret = _field.cursorIndex;
            bool codeAtInsertion = IsCodeAtCaret();
            bool tracked = _field.selectIndex == caret && caret >= 0 && caret < text.Length && text[caret] == evt.character
                && _autoClose.IsAutoClosed(text, caret);
            bool typeOver = tracked && IsTrackedCloserContext(text, caret, evt.character);
            if (tracked && (evt.character is '\'' or '"') && !typeOver)
            {
                // WHY: identical adjacent quotes make a text-only diff lose which quote was inserted by native input.
                _quoteInsertionSource = text;
                _quoteInsertionExpected = text.Insert(caret, evt.character.ToString());
                _quoteInsertionIndex = caret;
                _quoteInsertionGeneration = _documentGeneration;
                return false;
            }
            if (!codeAtInsertion && !typeOver) return false;
            CodeEditorTextUtility.AutoCloseEdit edit = CodeEditorTextUtility.AutoClosePair(
                evt.character, text, _field.selectIndex, caret, typeOver, codeAtInsertion,
                out string pairedText, out int pairedStart, out int pairedEnd);
            if (edit == CodeEditorTextUtility.AutoCloseEdit.None)
            {
                return false;
            }
            ApplyOperation(evt, pairedText, pairedStart, pairedEnd, typing: true);
            int closers = edit switch
            {
                CodeEditorTextUtility.AutoCloseEdit.Inserted => 1,
                CodeEditorTextUtility.AutoCloseEdit.InsertedTriple => 3,
                _ => 0
            };
            for (int i = 0; i < closers; i++)
            {
                _autoClose.Track(pairedStart - 1, pairedStart + i, pairedText[pairedStart + i], pairedText);
            }
            return true;
        }
        private bool IsTrackedCloserContext(string text, int caret, char closer)
        {
            if (text.Length > CodeEditorLimits.MaximumAnalysisCharacters) return false;
            // WHY: the previous character can be a string's closing quote while the tracked bracket itself is code.
            if (closer is not ('\'' or '"')) return IsCodeAtPosition(text, caret + 1);
            if (IsEscapedQuote(text, caret)) return false;
            if (_language.LanguageId != "lua") return IsCodeAtPosition(text, caret + 1);
            // WHY: an unescaped newline can move a tracked quote into a comment even though its old opener remains code.
            foreach (LuaToken token in LuaTokenizer.Tokenize(text))
            {
                int end = token.Start + token.Length;
                if (caret >= end) continue;
                return token.Kind == LuaTokenKind.String && token.Length > 1 && text[token.Start] == closer
                    && end == caret + 1;
            }
            return false;
        }
        private static bool IsEscapedQuote(string text, int index)
        {
            int backslashes = 0;
            for (int previous = index - 1; previous >= 0 && text[previous] == '\\'; previous--) backslashes++;
            return (backslashes & 1) != 0;
        }
        private void TrackTextChange(string previous, string next)
        {
            bool exactInsertion = _quoteInsertionIndex >= 0 && _quoteInsertionGeneration == _documentGeneration
                && string.Equals(previous, _quoteInsertionSource, StringComparison.Ordinal)
                && string.Equals(next, _quoteInsertionExpected, StringComparison.Ordinal);
            int insertionIndex = _quoteInsertionIndex;
            ClearQuoteInsertion();
            if (exactInsertion) _autoClose.OnTextChanged(previous, next, insertionIndex);
            else _autoClose.OnTextChanged(previous, next);
        }
        private void ClearQuoteInsertion()
        {
            _quoteInsertionSource = null;
            _quoteInsertionExpected = null;
            _quoteInsertionIndex = -1;
        }
        private void RequestCompletion()
        {
            int generation = _documentGeneration;
            _completionSchedule?.Pause();
            _completionSchedule = _field?.schedule.Execute(() =>
            {
                if (!_treeReleased && generation == _documentGeneration && _field?.panel != null && IsFocusWithin(_field) && !_field.isReadOnly)
                {
                    RefreshCompletion(explicitRequest: true);
                }
            }).StartingIn(1);
        }
        private static bool MovesCaretAwayFromCompletion(KeyDownEvent evt) =>
            evt.keyCode is KeyCode.LeftArrow or KeyCode.RightArrow or KeyCode.Home or KeyCode.End
                or KeyCode.PageUp or KeyCode.PageDown;
        private enum AcceptKey
        {
            None,
            Enter,
            Tab
        }
        [Flags]
        private enum KeyHalf
        {
            None = 0,
            Code = 1,
            Character = 2,
            Both = Code | Character
        }
        private AcceptKey _acceptPairKey;
        private KeyHalf _acceptPairAwaited;
        private string _acceptedCompletionText;
        private static AcceptKey ClassifyAcceptKey(KeyDownEvent evt, out KeyHalf halves)
        {
            halves = KeyHalf.None;
            AcceptKey key;
            switch (evt.keyCode)
            {
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    key = AcceptKey.Enter;
                    halves = KeyHalf.Code;
                    break;
                case KeyCode.Tab:
                    key = AcceptKey.Tab;
                    halves = KeyHalf.Code;
                    break;
                case KeyCode.None:
                    key = AcceptKey.None;
                    break;
                default:
                    return AcceptKey.None;
            }
            AcceptKey byCharacter = evt.ctrlKey || evt.commandKey
                ? AcceptKey.None
                : evt.character switch
                {
                    '\n' or '\r' or '\u0003' => AcceptKey.Enter,
                    '\t' => AcceptKey.Tab,
                    _ => AcceptKey.None
                };
            if (byCharacter != AcceptKey.None && (key == AcceptKey.None || key == byCharacter))
            {
                key = byCharacter;
                halves |= KeyHalf.Character;
            }
            return key;
        }
        private void AwaitAcceptPair(AcceptKey key, KeyHalf arrived)
        {
            KeyHalf awaited = KeyHalf.Both & ~arrived;
            _acceptPairKey = key == AcceptKey.None || awaited == KeyHalf.None ? AcceptKey.None : key;
            _acceptPairAwaited = awaited;
        }
        private bool SwallowAcceptPairHalf(KeyDownEvent evt)
        {
            if (_acceptPairKey == AcceptKey.None)
            {
                return false;
            }
            AcceptKey key = ClassifyAcceptKey(evt, out KeyHalf halves);
            bool pair = key == _acceptPairKey && (halves & _acceptPairAwaited) != 0;
            _acceptPairKey = AcceptKey.None;
            if (!pair)
            {
                return false;
            }
            SwallowKey(evt);
            return true;
        }
        private void SwallowKey(EventBase evt)
        {
            evt.StopImmediatePropagation();
#pragma warning disable CS0618
            evt.PreventDefault();
#pragma warning restore CS0618
            _field?.focusController?.IgnoreEvent(evt);
        }
        private bool HandleLineMotion(KeyDownEvent evt)
        {
            if (evt.keyCode is not (KeyCode.UpArrow or KeyCode.DownArrow))
            {
                return false;
            }
            bool up = evt.keyCode == KeyCode.UpArrow;
            int selA = Mathf.Min(_field.selectIndex, _field.cursorIndex);
            int selB = Mathf.Max(_field.selectIndex, _field.cursorIndex);
            int start;
            int end;
            string updated = evt.shiftKey
                ? CodeEditorTextUtility.DuplicateLines(_field.value, selA, selB, up, out start, out end)
                : CodeEditorTextUtility.MoveLines(_field.value, selA, selB, up, out start, out end);
            ApplyOperation(evt, updated, start, end);
            return true;
        }
        private bool HandleActionKeyEditing(KeyDownEvent evt)
        {
            int selA = Mathf.Min(_field.selectIndex, _field.cursorIndex);
            int selB = Mathf.Max(_field.selectIndex, _field.cursorIndex);
            switch (evt.keyCode)
            {
                case KeyCode.Slash:
                case KeyCode.KeypadDivide:
                {
                    string commented = CodeEditorTextUtility.ToggleLineComment(
                        _field.value, selA, selB, out int start, out int end, _language.LineCommentToken);
                    ApplyOperation(evt, commented, start, end);
                    return true;
                }
                case KeyCode.K when evt.shiftKey:
                {
                    string shortened = CodeEditorTextUtility.DeleteLines(
                        _field.value, selA, selB, out int caret);
                    ApplyOperation(evt, shortened, caret, caret);
                    return true;
                }
                case KeyCode.L when !evt.shiftKey:
                {
                    CodeEditorTextUtility.SelectLines(_field.value, selA, selB, out int start, out int end);
                    ApplyOperation(evt, _field.value, start, end);
                    return true;
                }
                case KeyCode.G when !evt.shiftKey:
                {
                    OpenGotoLinePrompt();
                    evt.StopImmediatePropagation();
                    evt.PreventDefault();
                    return true;
                }
                case KeyCode.I when !evt.shiftKey:
                {
                    ApplyOperation(evt, _field.value, _field.selectIndex, _field.cursorIndex);
                    RequestCompletion();
                    return true;
                }
                case KeyCode.RightBracket when !evt.shiftKey:
                {
                    string indented = CodeEditorTextUtility.IndentLines(
                        _field.value, selA, selB, out int start, out int end);
                    ApplyOperation(evt, indented, start, end);
                    return true;
                }
                case KeyCode.LeftBracket when !evt.shiftKey:
                {
                    string outdented = CodeEditorTextUtility.UnindentSelection(
                        _field.value, selA, selB, out int start, out int end);
                    ApplyOperation(evt, outdented, start, end);
                    return true;
                }
                case KeyCode.Home:
                {
                    ApplyOperation(evt, _field.value, evt.shiftKey ? _field.selectIndex : 0, 0);
                    return true;
                }
                case KeyCode.Backspace when selA == selB
                                            && CodeEditorTextUtility.TryDeleteWordBefore(
                                                _field.value, selA, out string beforeText, out int beforeCaret):
                {
                    ApplyOperation(evt, beforeText, beforeCaret, beforeCaret);
                    return true;
                }
                case KeyCode.Delete when selA == selB
                                         && CodeEditorTextUtility.TryDeleteWordAfter(
                                             _field.value, selA, out string afterText, out int afterCaret):
                {
                    ApplyOperation(evt, afterText, afterCaret, afterCaret);
                    return true;
                }
                default:
                    return false;
            }
        }
        private void ApplyOperation(KeyDownEvent evt, string text, int selStart, int selEnd, bool typing = false)
        {
            bool changed = !string.Equals(_field.value, text, StringComparison.Ordinal);
            if (changed)
            {
                _suppressHistory = true;
                try
                {
                    _field.value = text;
                }
                finally
                {
                    _suppressHistory = false;
                }
            }
            int length = _field.value?.Length ?? 0;
            int start = Mathf.Clamp(selStart, 0, length);
            int end = Mathf.Clamp(selEnd, 0, length);
            Selection = new CodeSelection(end, start);
            if (changed)
            {
                _history.Push(_field.value, end, NowMs(), !typing, start);
                RefreshGutter();
            }
            SwallowKey(evt);
            RefreshCaret(true);
        }
        private void OpenGotoLinePrompt()
        {
            VisualElement host = _scroll?.parent ?? _mirror?.parent;
            if (host == null || _field == null)
            {
                return;
            }
            CloseGotoLinePrompt();
            _gotoPrompt = new VisualElement();
            _gotoPrompt.AddToClassList("coreai-code-goto");
            Label caption = new("Line:") { pickingMode = PickingMode.Ignore };
            caption.AddToClassList("coreai-code-goto__label");
            _gotoPrompt.Add(caption);
            _gotoField = new TextField();
            _gotoField.AddToClassList("coreai-code-goto__field");
            _gotoField.RegisterCallback<KeyDownEvent>(OnGotoLineKeyDown, TrickleDown.TrickleDown);
            _gotoField.RegisterCallback<FocusOutEvent>(_ => CloseGotoLinePrompt());
            _gotoPrompt.Add(_gotoField);
            host.Add(_gotoPrompt);
            _gotoField.SetValueWithoutNotify(CodeEditorTextUtility
                .LineFromCaret(_field.value, _field.cursorIndex)
                .ToString(CultureInfo.InvariantCulture));
            TextField promptField = _gotoField;
            promptField.schedule.Execute(() =>
            {
                if (!_treeReleased && ReferenceEquals(promptField, _gotoField) && promptField.panel != null)
                {
                    promptField.Focus();
                    promptField.SelectAll();
                }
            }).StartingIn(1);
        }
        private void OnGotoLineKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode is KeyCode.Return or KeyCode.KeypadEnter)
            {
                if (int.TryParse(_gotoField.value, NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out int line))
                {
                    CloseGotoLinePrompt();
                    GoToLine(line);
                }
                else
                {
                    CloseGotoLinePrompt();
                    _field.Focus();
                }
                evt.StopImmediatePropagation();
                evt.PreventDefault();
                return;
            }
            if (evt.keyCode == KeyCode.Escape)
            {
                CloseGotoLinePrompt();
                _field.Focus();
                evt.StopImmediatePropagation();
                evt.PreventDefault();
            }
        }
        private void CloseGotoLinePrompt()
        {
            if (_gotoPrompt == null)
            {
                return;
            }
            VisualElement prompt = _gotoPrompt;
            _gotoPrompt = null;
            _gotoField = null;
            prompt.RemoveFromHierarchy();
        }
        /// <summary>Dismisses this editor's active transient UI before a host handles Escape.</summary>
        public bool TryHandleEscape()
        {
            if (_treeReleased) return false;
            if (_gotoPrompt != null)
            {
                CloseGotoLinePrompt();
                _field?.Focus();
                return true;
            }
            if (!IsCompletionOpen) return false;
            CloseCompletion();
            _field?.Focus();
            return true;
        }

        public void Dispose() => ReleaseTree();
        public CodeSelection Selection
        {
            get => new CodeSelection(_field?.cursorIndex ?? 0, _field?.selectIndex ?? 0);
            set
            {
                if (_field == null || _treeReleased) return;
                CancelFocusSelection();
                string source = _field.value ?? "";
                CodeSelection selected = value.Clamp(source.Length);
                if (selected.Caret != _field.cursorIndex || selected.Anchor != _field.selectIndex)
                    _completionSchedule?.Pause();
                // WHY: explicit selection can invalidate a popup before the deferred presentation observes the new caret.
                if (IsCompletionOpen && (_completionGeneration != _documentGeneration || _completionCaret != selected.Caret
                    || selected.Anchor != selected.Caret || !string.Equals(_completionSource, source, StringComparison.Ordinal)))
                    CloseCompletion();
                // WHY: native cache rebuilds can follow focus or layout; the next input must consume this exact request.
                _pendingFocusSelection = selected;
                _pendingFocusSource = source;
                _pendingFocusGeneration = _documentGeneration;
                SetFieldSelection(_field, selected.Caret, selected.Anchor);
            }
        }
        public void SetLanguage(ICodeLanguageProfile language)
        {
            InvalidateDocumentWork();
            _language = language ?? throw new ArgumentNullException(nameof(language));
            _completion.Profile = _language;
            CloseCompletion();
            RefreshHighlight();
        }
        private void InvalidateDocumentWork()
        {
            _documentGeneration++;
            ClearQuoteInsertion();
            _acceptPairKey = AcceptKey.None;
            _acceptPairAwaited = KeyHalf.None;
            _acceptedCompletionText = null;
            _presentation?.Pause();
            _newLineSchedule?.Pause();
            _completionSchedule?.Pause();
            CancelFocusSelection();
            CloseCompletion();
        }
        public void ReleaseTree()
        {
            if (_treeReleased) return;
            InvalidateDocumentWork();
            _treeReleased = true;
            CloseGotoLinePrompt();
            StopCaret();
            CloseCompletion();
            _completionPopup?.RemoveFromHierarchy();
            _completionPopup = null;
            _caretPoll = null;
            _releaseTree?.Invoke();
            _releaseTree = null;
            _history.Clear();
            _documentHistories.Clear();
        }
        private bool TryRestore(CodeEditorSnapshot? snapshot)
        {
            if (snapshot == null || _field == null)
            {
                return false;
            }
            CodeEditorSnapshot state = snapshot.Value;
            _autoClose.Clear();
            _suppressHistory = true;
            try
            {
                _field.value = state.Text;
            }
            finally
            {
                _suppressHistory = false;
            }
            int caret = Mathf.Clamp(state.CaretIndex, 0, _field.value.Length);
            Selection = new CodeSelection(caret, Mathf.Clamp(state.AnchorIndex, 0, _field.value.Length));
            ClearHintLines();
            RefreshGutter();
            return true;
        }
        private void OnNavigationMove(NavigationMoveEvent evt)
        {
            if (_treeReleased || _field == null || _field.isReadOnly)
            {
                return;
            }
            PrepareSelectionForInput();
            if (IsCompletionOpen
                && evt.direction is NavigationMoveEvent.Direction.Up or NavigationMoveEvent.Direction.Down)
            {
                MoveCompletionSelection(evt.direction == NavigationMoveEvent.Direction.Down ? 1 : -1);
                evt.StopImmediatePropagation();
                evt.PreventDefault();
                return;
            }
            if (evt.direction is NavigationMoveEvent.Direction.Next or NavigationMoveEvent.Direction.Previous)
            {
                evt.StopImmediatePropagation();
                evt.PreventDefault();
            }
        }
        private void ApplyEdit(string text, int caret)
        {
            _field.value = text;
            Selection = new CodeSelection(caret, caret);
            RefreshGutter();
        }
        private void RefreshGutter()
        {
            if (_gutter?.panel == null || _plainTextMode || _treeReleased)
            {
                return;
            }
            int lines = CodeEditorTextUtility.CountLines(_field?.value);
            CodeGutterPalette palette = ReadGutterPalette();
            _gutterTexts.Clear();
            for (int number = 1; number <= lines; number++)
            {
                _gutterTexts.Add(CodeEditorTextUtility.BuildGutterLine(
                    number, _markerLine, _errorLine, _caretLine, palette, _hintLineSet.Contains(number)));
            }
            _gutterLines?.ApplyByIndex(_gutterTexts);
            ApplyLineHeights();
        }
        private CodeGutterPalette ReadGutterPalette() =>
            new(
                markerHex: ReadGutterColour(GutterMarkerColorProperty),
                errorHex: ReadGutterColour(GutterErrorColorProperty),
                caretHex: ReadGutterColour(GutterCaretColorProperty),
                hintHex: ReadGutterColour(GutterTeacherColorProperty));
        private string ReadGutterColour(CustomStyleProperty<Color> property) =>
            _gutter != null && _gutter.customStyle.TryGetValue(property, out Color colour)
                ? "#" + ToRichTextHex(colour)
                : null;
        private static string ToRichTextHex(Color colour) =>
            colour.a >= 1f
                ? ColorUtility.ToHtmlStringRGB(colour)
                : ColorUtility.ToHtmlStringRGBA(colour);
        private void UpdateCaretGutterLine(int? line)
        {
            int? next = _focused ? line : null;
            if (_caretLine == next)
            {
                return;
            }
            _caretLine = next;
            RefreshGutter();
        }
        private void RefreshBracketPair()
        {
            if (_bracketLayer == null || _field == null || _mirror == null || _plainTextMode)
            {
                return;
            }
            string text = _field.value ?? "";
            float lineHeight = MeasureLineHeight();
            if (!_focused
                || lineHeight <= 0.5f
                || !FindBracketPair(text, _field.cursorIndex, out int first, out int second))
            {
                HideBracketBoxes();
                return;
            }
            PlaceBracketBox(0, text, first, lineHeight);
            PlaceBracketBox(1, text, second, lineHeight);
        }
        private void HideBracketBoxes()
        {
            if (_bracketBoxes == null)
            {
                return;
            }
            for (int i = 0; i < _bracketBoxes.Length; i++)
            {
                _bracketBoxes[i].style.display = DisplayStyle.None;
            }
        }
        private void PlaceBracketBox(int slot, string text, int index, float lineHeight)
        {
            if (_bracketBoxes == null)
            {
                _bracketBoxes = new VisualElement[2];
                for (int i = 0; i < _bracketBoxes.Length; i++)
                {
                    VisualElement created = new() { pickingMode = PickingMode.Ignore };
                    created.style.position = Position.Absolute;
                    created.AddToClassList("coreai-code-editor__bracket");
                    created.style.display = DisplayStyle.None;
                    _bracketLayer.Add(created);
                    _bracketBoxes[i] = created;
                }
            }
            VisualElement box = _bracketBoxes[slot];
            (int row, float x) = MeasureCaretColumn(index);
            float width = MeasureCharWidth(text[index]);
            if (width <= 0.5f)
            {
                box.style.display = DisplayStyle.None;
                return;
            }
            box.style.left = x;
            box.style.top = MirrorPadTop() + row * lineHeight;
            box.style.width = width;
            box.style.height = lineHeight;
            box.style.display = DisplayStyle.Flex;
        }
        private float MeasureCharWidth(char symbol)
        {
            float with = _mirror.MeasureTextSize("A" + symbol + "A", 0,
                VisualElement.MeasureMode.Undefined, 0, VisualElement.MeasureMode.Undefined).x;
            float bounds = _mirror.MeasureTextSize("AA", 0,
                VisualElement.MeasureMode.Undefined, 0, VisualElement.MeasureMode.Undefined).x;
            return with - bounds;
        }
        private bool IsCompletionOpen =>
            _completionPopup != null && _completionPopup.style.display == DisplayStyle.Flex;
        private void RefreshCompletion(bool explicitRequest = false)
        {
            if (_treeReleased || _plainTextMode || !CompletionEnabled || _mirror?.parent == null || _field == null)
            {
                return;
            }
            if (!IsCodeAtCaret()) { CloseCompletion(); return; }
            bool explicitMode = explicitRequest || (IsCompletionOpen && _completionExplicit);
            _completionSource = _field.value ?? "";
            _completionGeneration = _documentGeneration;
            _completionCaret = _field.cursorIndex;
            _completionResult = _completion.GetCompletions(_completionSource, _completionCaret, explicitMode);
            if (!_completionResult.HasItems)
            {
                CloseCompletion();
                return;
            }
            RefreshCaret(false, reveal: true);
            EnsureCompletionPopup();
            _completionSelected = 0;
            _completionNavigated = false;
            _completionExplicit = explicitMode;
            RebuildCompletionItems();
            PositionCompletionPopup();
            _completionPopup.style.display = DisplayStyle.Flex;
            IsAnyCompletionPopupOpen = true;
        }
        private VisualElement CompletionHost => _scroll?.parent ?? _mirror?.parent;
        private void EnsureCompletionPopup()
        {
            if (_completionPopup != null)
            {
                return;
            }
            _completionPopup = new VisualElement { name = CompletionPopupName };
            _completionPopup.AddToClassList(CompletionClass);
            _completionPopup.style.position = Position.Absolute;
            _completionPopup.style.backgroundColor = new Color(0.04f, 0.08f, 0.14f);
            _completionPopup.style.minWidth = 160f;
            _completionPopup.style.maxWidth = 300f;
            _completionPopup.style.paddingTop = _completionPopup.style.paddingBottom = 4f;
            _completionPopup.style.overflow = Overflow.Hidden;
            _completionPopup.style.display = DisplayStyle.None;
            _completionPopup.RegisterCallback<GeometryChangedEvent>(_ => RepositionOpenCompletion());
            CompletionHost.Add(_completionPopup);
        }
        private void RebuildCompletionItems()
        {
            _completionPopup.Clear();
            for (int i = 0; i < _completionResult.Items.Count; i++)
            {
                int index = i;
                Label item = new(_completionResult.Items[i]);
                item.AddToClassList("coreai-code-completion__item");
                item.style.fontSize = 13f;
                item.style.paddingLeft = item.style.paddingRight = 8f;
                item.style.paddingTop = item.style.paddingBottom = 3f;
                item.style.flexShrink = 0f;
                item.style.color = HubModWidgets.Text;
                item.style.backgroundColor = i == _completionSelected ? new Color(0.1f, 0.4f, 0.5f) : Color.clear;
                if (i == _completionSelected)
                {
                    item.AddToClassList("coreai-code-completion__item--selected");
                }
                item.RegisterCallback<PointerDownEvent>(evt =>
                {
                    if (AcceptCompletion(index)) _field?.focusController?.IgnoreEvent(evt);
                    evt.StopPropagation();
                });
                _completionPopup.Add(item);
            }
        }
        private void MoveCompletionSelection(int delta)
        {
            int count = _completionResult.Items.Count;
            if (count == 0)
            {
                return;
            }
            _completionSelected = (_completionSelected + delta + count) % count;
            _completionNavigated = true;
            for (int i = 0; i < _completionPopup.childCount; i++)
            {
                _completionPopup[i].style.backgroundColor = i == _completionSelected ? new Color(0.1f, 0.4f, 0.5f) : Color.clear;
                _completionPopup[i].EnableInClassList("coreai-code-completion__item--selected",
                    i == _completionSelected);
            }
        }
        private bool AcceptCompletion(int index, AcceptKey key = AcceptKey.None, KeyHalf arrived = KeyHalf.None)
        {
            if (_treeReleased || _field == null || index < 0 || index >= _completionResult.Items.Count
                || _completionGeneration != _documentGeneration || _completionCaret != _field.cursorIndex
                || !string.Equals(_completionSource, _field.value, StringComparison.Ordinal))
            {
                CloseCompletion();
                return false;
            }
            string word = _completionResult.Items[index];
            string text = _field.value ?? "";
            int start = Mathf.Clamp(_completionResult.ReplaceStart, 0, text.Length);
            int prefixLength = Mathf.Clamp(_field.cursorIndex - start, 0, text.Length - start);
            int caret = start + word.Length;
            string completedText = text.Remove(start, prefixLength).Insert(start, word);
            CloseCompletion();
            AwaitAcceptPair(key, arrived);
            _acceptedCompletionText = completedText;
            ApplyEdit(completedText, caret);
            FocusSelection(caret, caret);
            return true;
        }
        internal static void SetFieldSelection(TextField field, int cursor, int select)
        {
            ITextSelection selection = field?.textSelection;
            if (selection == null)
            {
                return;
            }
            int length = field.value?.Length ?? 0;
            cursor = Mathf.Clamp(cursor, 0, length);
            select = Mathf.Clamp(select, 0, length);
#if UNITY_6000_6_OR_NEWER
            // WHY: SelectRange otherwise clamps against stale native text after value/layout changes in Unity 6.6.
            if (field.panel != null) selection.GetCursorPositionFromStringIndex(cursor);
#endif
            field.SelectRange(cursor, select);
        }
        private void FocusSelection(int cursor, int anchor)
        {
            if (_field == null || _treeReleased) return;
            Selection = new CodeSelection(cursor, anchor);
            _field.Focus();
            SetFieldSelection(_field, cursor, anchor);
        }
        private void RestorePendingFocusSelection()
        {
            CodeSelection? pending = _pendingFocusSelection;
            if (!pending.HasValue) return;
            bool valid = !_treeReleased && _pendingFocusGeneration == _documentGeneration
                && string.Equals(_field.value, _pendingFocusSource, StringComparison.Ordinal);
            if (valid) SetFieldSelection(_field, pending.Value.Caret, pending.Value.Anchor);
        }
        private void PrepareSelectionForInput()
        {
            ClearQuoteInsertion();
            RestorePendingFocusSelection();
            CancelFocusSelection();
        }
        private void CancelFocusSelection()
        {
            _pendingFocusSelection = null;
            _pendingFocusSource = null;
        }
        internal static void FocusFieldWithSelection(TextField field, int cursor, int select)
        {
            if (field == null) return;
            field.Focus();
            SetFieldSelection(field, cursor, select);
        }
        private static bool IsFocusWithin(VisualElement element)
        {
            VisualElement focused = element?.panel?.focusController?.focusedElement as VisualElement;
            return focused != null && (focused == element || element.Contains(focused));
        }
        private void ApplyCaret(int selStart, int selEnd)
        {
            SetFieldSelection(_field, selEnd, selStart);
        }
        private void CloseCompletion()
        {
            if (_completionPopup != null)
            {
                _completionPopup.style.display = DisplayStyle.None;
            }
            _completionNavigated = false;
            _completionExplicit = false;
            IsAnyCompletionPopupOpen = false;
        }
        private void RepositionOpenCompletion()
        {
            if (IsCompletionOpen)
            {
                PositionCompletionPopup();
            }
        }
        private void PositionCompletionPopup()
        {
            VisualElement stack = _mirror?.parent;
            VisualElement host = _completionPopup?.parent;
            if (stack == null || host == null || _field == null)
            {
                return;
            }
            (int row, float x) = MeasureCaretColumn(_field.cursorIndex);
            float lineHeight = Mathf.Max(MeasureLineHeight(), 1f);
            float lineTop = MirrorPadTop() + row * lineHeight;
            CompletionPlacement placement = CompletionPlacement.Compute(
                ToHostPadding(stack, host, new Vector2(x, lineTop)),
                lineHeight,
                NaturalCompletionHeight(),
                ResolvedOrZero(_completionPopup.resolvedStyle.width),
                HostInnerSize(host, stack));
            _completionPopup.style.left = placement.Left;
            _completionPopup.style.top = placement.Top;
            _completionPopup.style.maxHeight = placement.MaxHeight.HasValue
                ? new StyleLength(placement.MaxHeight.Value)
                : new StyleLength(StyleKeyword.Null);
        }
        private Vector2 ToHostPadding(VisualElement stack, VisualElement host, Vector2 point)
        {
            if (ReferenceEquals(stack, host) || stack.panel == null
                || !TryLayoutOffset(stack, host, out Vector2 stackOrigin))
            {
                return point;
            }
            Vector2 local = stackOrigin + point;
            IResolvedStyle hostStyle = host.resolvedStyle;
            return new Vector2(
                local.x - ResolvedOrZero(hostStyle.borderLeftWidth),
                local.y - ResolvedOrZero(hostStyle.borderTopWidth));
        }
        private static Vector2? HostInnerSize(VisualElement host, VisualElement stack)
        {
            if (ReferenceEquals(host, stack))
            {
                return null;
            }
            Rect rect = host.layout;
            IResolvedStyle hostStyle = host.resolvedStyle;
            float width = rect.width
                          - ResolvedOrZero(hostStyle.borderLeftWidth)
                          - ResolvedOrZero(hostStyle.borderRightWidth);
            float height = rect.height
                           - ResolvedOrZero(hostStyle.borderTopWidth)
                           - ResolvedOrZero(hostStyle.borderBottomWidth);
            if (float.IsNaN(width) || float.IsNaN(height) || width <= 1f || height <= 1f)
            {
                return null;
            }
            return new Vector2(width, height);
        }
        private float NaturalCompletionHeight()
        {
            IResolvedStyle popup = _completionPopup.resolvedStyle;
            float height = ResolvedOrZero(popup.paddingTop) + ResolvedOrZero(popup.paddingBottom)
                           + ResolvedOrZero(popup.borderTopWidth) + ResolvedOrZero(popup.borderBottomWidth);
            bool measured = false;
            for (int i = 0; i < _completionPopup.childCount; i++)
            {
                VisualElement item = _completionPopup[i];
                float itemHeight = ResolvedOrZero(item.layout.height);
                measured |= itemHeight > 0.5f;
                height += itemHeight
                          + ResolvedOrZero(item.resolvedStyle.marginTop)
                          + ResolvedOrZero(item.resolvedStyle.marginBottom);
            }
            return measured ? height : 0f;
        }
        private sealed class LineRun
        {
            private readonly VisualElement _host;
            private readonly List<TextElement> _elements = new();
            private readonly List<string> _texts = new();
            private readonly List<float> _heights = new();
            public LineRun(VisualElement host)
            {
                _host = host;
                List<VisualElement> strays = new();
                host.Query(className: LineClass).ForEach(strays.Add);
                for (int i = 0; i < strays.Count; i++)
                {
                    strays[i].RemoveFromHierarchy();
                }
            }
            public void ApplyChangedRange(IReadOnlyList<string> texts)
            {
                CodeEditorTextUtility.ChangedLineRange(
                    _texts, texts, out int prefix, out int oldMiddle, out int newMiddle);
                int reused = Math.Min(oldMiddle, newMiddle);
                for (int i = 0; i < reused; i++)
                {
                    SetText(prefix + i, texts[prefix + i]);
                }
                for (int i = reused; i < newMiddle; i++)
                {
                    Insert(prefix + i, texts[prefix + i]);
                }
                for (int i = reused; i < oldMiddle; i++)
                {
                    RemoveAt(prefix + reused);
                }
            }
            public void ApplyByIndex(IReadOnlyList<string> texts)
            {
                int shared = Math.Min(_texts.Count, texts.Count);
                for (int i = 0; i < shared; i++)
                {
                    SetText(i, texts[i]);
                }
                for (int i = shared; i < texts.Count; i++)
                {
                    Insert(i, texts[i]);
                }
                for (int i = _texts.Count - 1; i >= texts.Count; i--)
                {
                    RemoveAt(i);
                }
            }
            public void ApplyHeights(float lineHeight, float lastLineHeight)
            {
                for (int i = 0; i < _elements.Count; i++)
                {
                    float height = i == _elements.Count - 1 ? lastLineHeight : lineHeight;
                    if (!Mathf.Approximately(_heights[i], height))
                    {
                        _heights[i] = height;
                        _elements[i].style.height = height;
                    }
                }
            }
            public void Release()
            {
                for (int i = 0; i < _elements.Count; i++)
                {
                    _elements[i].RemoveFromHierarchy();
                }
                _elements.Clear();
                _texts.Clear();
                _heights.Clear();
            }
            private void SetText(int index, string text)
            {
                if (string.Equals(_texts[index], text, StringComparison.Ordinal))
                {
                    return;
                }
                _texts[index] = text;
                ((INotifyValueChanged<string>)_elements[index]).SetValueWithoutNotify(text);
            }
            private void Insert(int index, string text)
            {
                TextElement line = new() { pickingMode = PickingMode.Ignore, enableRichText = true, text = text };
                line.AddToClassList(LineClass);
                line.style.marginLeft = line.style.marginRight = line.style.marginTop = line.style.marginBottom = 0f;
                line.style.paddingLeft = line.style.paddingRight = line.style.paddingTop = line.style.paddingBottom = 0f;
                line.style.whiteSpace = WhiteSpace.Pre;
                line.style.flexShrink = 0f;
                _host.Insert(index, line);
                _elements.Insert(index, line);
                _texts.Insert(index, text);
                _heights.Insert(index, float.NaN);
            }
            private void RemoveAt(int index)
            {
                _elements[index].RemoveFromHierarchy();
                _elements.RemoveAt(index);
                _texts.RemoveAt(index);
                _heights.RemoveAt(index);
            }
        }
    }
}
