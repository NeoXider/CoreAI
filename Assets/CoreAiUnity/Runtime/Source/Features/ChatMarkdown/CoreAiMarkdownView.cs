using System;
using System.Collections.Generic;
using CoreAI.ChatOverlay;
using CoreAI.Infrastructure.Logging;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.ChatMarkdown
{
    /// <summary>
    /// UI Toolkit element that shows Markdown written by a model: the one seam any CoreAI or host UI uses to show
    /// Markdown (a chat answer, a mod description, a report). With a backend in
    /// <see cref="CoreAiMarkdownRendererRegistry"/> it renders headings, lists, quotes, code blocks with highlighting
    /// and pipe tables; without one it shows the same text as plain words (<see cref="ChatMarkdownText.ToPlainText"/>),
    /// so a caller never needs to know whether the optional package is installed.
    /// </summary>
    /// <remarks>
    /// <para>Links are inert unless <see cref="LinkHandler"/> is set. Images and videos are shown as links unless
    /// <see cref="AllowMedia"/>. Ctrl/Cmd+C with nothing selected copies <see cref="Markdown"/>, the original
    /// Markdown; a selection inside the view copies the selected visible text as any label does.</para>
    /// <para>Style it through the classes below; the backend's style sheet is added to the view itself.</para>
    /// </remarks>
    [UxmlElement]
    public partial class CoreAiMarkdownView : VisualElement
    {
        /// <summary>Class of every view.</summary>
        public const string UssClassName = "coreai-markdown";

        /// <summary>Added while the view shows a streaming answer.</summary>
        public const string StreamingUssClassName = UssClassName + "--streaming";

        /// <summary>A rendered piece: the whole text once finished, stable blocks and the live tail while streaming.</summary>
        public const string PartUssClassName = UssClassName + "__part";

        /// <summary>Each rendered Markdown block group or table inside a part.</summary>
        public const string SegmentUssClassName = UssClassName + "__segment";

        /// <summary>The last segment of the view, so its bottom margin can be dropped.</summary>
        public const string LastSegmentUssClassName = SegmentUssClassName + "--last";

        /// <summary>The plain-text label shown when no backend is installed or rendering failed.</summary>
        public const string PlainUssClassName = UssClassName + "__plain";

        /// <summary>A pipe table.</summary>
        public const string TableUssClassName = UssClassName + "__table";

        /// <summary>A table row.</summary>
        public const string TableRowUssClassName = UssClassName + "__table-row";

        /// <summary>The header row of a table.</summary>
        public const string TableHeaderRowUssClassName = TableRowUssClassName + "--header";

        /// <summary>A table cell.</summary>
        public const string TableCellUssClassName = UssClassName + "__table-cell";

        private static readonly Func<double> DefaultClock = () => Time.realtimeSinceStartupAsDouble;
        private static bool _renderFailureLogged;

        private readonly ICoreAiMarkdownRenderer _renderer;
        private readonly Func<double> _clock;
        private readonly Action<string> _linkActivated;
        private readonly Action _refreshStreaming;
        private string _markdown = "";
        private bool _streaming;
        private int _stableLength;
        private VisualElement _tail;
        private string _tailPrepared;
        private string _pendingStreamText;
        private double _lastStreamRenderAt;
        private IVisualElementScheduledItem _refresh;
        private VisualElement _lastSegment;
        private StyleSheet _appliedStyleSheet;

        /// <summary>An empty view that renders through the registered backend.</summary>
        public CoreAiMarkdownView() : this(null, null, null)
        {
        }

        /// <summary>
        /// A view showing <paramref name="markdown"/>, rendered by <paramref name="renderer"/> or, when it is null, by
        /// the backend registered at render time.
        /// </summary>
        public CoreAiMarkdownView(string markdown, ICoreAiMarkdownRenderer renderer = null) : this(markdown, renderer,
            null)
        {
        }

        internal CoreAiMarkdownView(string markdown, ICoreAiMarkdownRenderer renderer, Func<double> clock)
        {
            _renderer = renderer;
            _clock = clock ?? DefaultClock;
            _linkActivated = target => ActivateLink(target);
            _refreshStreaming = FlushStreaming;
            AddToClassList(UssClassName);
            focusable = true;
            RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
            if (!string.IsNullOrEmpty(markdown))
            {
                Markdown = markdown;
            }
        }

        /// <summary>The Markdown shown (the original text, before any sanitising). Setting it renders it once.</summary>
        [UxmlAttribute("markdown")]
        public string Markdown
        {
            get => _markdown;
            set
            {
                StopStreaming();
                _markdown = value ?? "";
                RenderFinished();
            }
        }

        /// <summary>What activating a link does; null (the default) leaves links inert.</summary>
        public ICoreAiMarkdownLinkHandler LinkHandler { get; set; }

        /// <summary>
        /// When true, images and videos are rendered (the backend may fetch their URLs); when false (the default)
        /// they are shown as links. Applies from the next render, and never to a streaming preview.
        /// </summary>
        public bool AllowMedia { get; set; }

        /// <summary>True when the last render produced Markdown elements; false for the plain-text fallback.</summary>
        public bool IsRenderedAsMarkdown { get; private set; }

        /// <summary>True while the view previews an answer that is still streaming.</summary>
        public bool IsStreaming => _streaming;

        /// <summary>Finished renders so far; a streamed bubble gets exactly one.</summary>
        internal int FinishedRenderCount { get; private set; }

        /// <summary>Streaming preview passes so far.</summary>
        internal int StreamingRenderCount { get; private set; }

        /// <summary>Copies <see cref="Markdown"/> to the system clipboard; false when there is nothing to copy.</summary>
        public bool CopyMarkdownToClipboard()
        {
            if (string.IsNullOrEmpty(_markdown))
            {
                return false;
            }

            CoreAiMarkdownClipboard.Write(_markdown);
            return true;
        }

        /// <summary>Hands an activated link to <see cref="LinkHandler"/>; false when no handler took it.</summary>
        internal bool ActivateLink(string target)
        {
            ICoreAiMarkdownLinkHandler handler = LinkHandler;
            if (handler == null || string.IsNullOrEmpty(target))
            {
                return false;
            }

            handler.OnLinkActivated(target);
            return true;
        }

        /// <summary>True for the copy shortcut: C with the platform action key (Ctrl, or Cmd on macOS).</summary>
        internal static bool IsCopyShortcut(KeyCode keyCode, bool actionKey)
        {
            return actionKey && keyCode == KeyCode.C;
        }

        /// <summary>
        /// Shows <paramref name="text"/> as a streaming answer: complete blocks are rendered once each
        /// (<see cref="ChatMarkdownText.FindStablePrefixLength"/>) and only the open tail is re-rendered, at most once
        /// per <see cref="ChatMarkdownText.StreamingRefreshSeconds"/>; a skipped update is rendered when that pause
        /// ends. Call it on the main thread for every new piece of a streaming answer, then set
        /// <see cref="Markdown"/> (or call <see cref="CompleteStreaming"/>) once with the final text.
        /// </summary>
        public void UpdateStreaming(string text)
        {
            text ??= "";
            if (!_streaming)
            {
                _streaming = true;
                ClearRendering();
                StreamingRenderCount = 0;
                AddToClassList(StreamingUssClassName);
            }

            _markdown = text;
            double now = _clock();
            double pause = ChatMarkdownText.StreamingRefreshSeconds(text.Length - _stableLength);
            if (StreamingRenderCount == 0 || now - _lastStreamRenderAt >= pause)
            {
                RenderStreaming(text, now);
                return;
            }

            _pendingStreamText = text;
            long delay = (long)Math.Ceiling(Math.Max(0d, _lastStreamRenderAt + pause - now) * 1000d);
            _refresh ??= schedule.Execute(_refreshStreaming);
            _refresh.ExecuteLater(Math.Max(1L, delay));
        }

        /// <summary>Renders a skipped streaming update now (the end of the refresh pause, or a test).</summary>
        internal void FlushStreaming()
        {
            if (_streaming && _pendingStreamText != null)
            {
                RenderStreaming(_pendingStreamText, _clock());
            }
        }

        /// <summary>Ends streaming and renders <paramref name="text"/> once as a finished answer.</summary>
        public void CompleteStreaming(string text)
        {
            Markdown = text;
        }

        private void StopStreaming()
        {
            if (!_streaming)
            {
                return;
            }

            _streaming = false;
            _pendingStreamText = null;
            _refresh?.Pause();
            RemoveFromClassList(StreamingUssClassName);
        }

        private ICoreAiMarkdownRenderer ResolveRenderer()
        {
            return _renderer ?? CoreAiMarkdownRendererRegistry.Current;
        }

        private void RenderFinished()
        {
            ClearRendering();
            FinishedRenderCount++;
            if (string.IsNullOrEmpty(_markdown))
            {
                return;
            }

            ICoreAiMarkdownRenderer renderer = ResolveRenderer();
            if (renderer == null)
            {
                ShowPlain(_markdown);
                return;
            }

            ApplyStyleSheet(renderer);
            string prepared = ChatMarkdownText.PrepareForRender(_markdown, false, AllowMedia);
            Add(ComposePart(renderer, prepared, true));
            IsRenderedAsMarkdown = true;
            MarkLastSegment();
        }

        private void RenderStreaming(string text, double now)
        {
            StreamingRenderCount++;
            _pendingStreamText = null;
            _lastStreamRenderAt = now;
            ICoreAiMarkdownRenderer renderer = ResolveRenderer();
            if (renderer == null)
            {
                ShowPlain(text);
                return;
            }

            if (!IsRenderedAsMarkdown)
            {
                ClearRendering();
            }

            ApplyStyleSheet(renderer);
            int stable = ChatMarkdownText.FindStablePrefixLength(text);
            if (stable < _stableLength)
            {
                ClearRendering();
            }

            if (stable > _stableLength)
            {
                string block = text.Substring(_stableLength, stable - _stableLength);
                VisualElement part = ComposePart(renderer, ChatMarkdownText.PrepareForRender(block, false), false);
                if (_tail != null)
                {
                    Insert(IndexOf(_tail), part);
                }
                else
                {
                    Add(part);
                }

                _stableLength = stable;
            }

            string prepared = ChatMarkdownText.PrepareForRender(text.Substring(_stableLength), true);
            if (_tail == null || !string.Equals(prepared, _tailPrepared, StringComparison.Ordinal))
            {
                VisualElement tail = ComposePart(renderer, prepared, false);
                if (_tail != null)
                {
                    int index = IndexOf(_tail);
                    _tail.RemoveFromHierarchy();
                    Insert(index, tail);
                }
                else
                {
                    Add(tail);
                }

                _tail = tail;
                _tailPrepared = prepared;
            }

            IsRenderedAsMarkdown = true;
            MarkLastSegment();
        }

        private void ClearRendering()
        {
            Clear();
            _tail = null;
            _tailPrepared = null;
            _stableLength = 0;
            _lastSegment = null;
            IsRenderedAsMarkdown = false;
        }

        private void ShowPlain(string text)
        {
            ClearRendering();
            Label label = new(ChatMarkdownText.ToPlainText(text)) { enableRichText = false };
            label.style.whiteSpace = WhiteSpace.Normal;
            label.AddToClassList(PlainUssClassName);
            MakeSelectable(label);
            Add(label);
        }

        private void ApplyStyleSheet(ICoreAiMarkdownRenderer renderer)
        {
            StyleSheet sheet = renderer.StyleSheet;
            if (sheet == null || sheet == _appliedStyleSheet)
            {
                return;
            }

            if (_appliedStyleSheet != null)
            {
                styleSheets.Remove(_appliedStyleSheet);
            }

            styleSheets.Add(sheet);
            _appliedStyleSheet = sheet;
        }

        private VisualElement ComposePart(ICoreAiMarkdownRenderer renderer, string prepared, bool finished)
        {
            VisualElement part = new();
            part.AddToClassList(PartUssClassName);
            try
            {
                IReadOnlyList<ChatMarkdownSegment> segments = ChatMarkdownText.SplitTables(prepared);
                foreach (ChatMarkdownSegment segment in segments)
                {
                    VisualElement element = segment.IsTable
                        ? BuildTable(segment)
                        : renderer.Render(segment.Markdown, _linkActivated);
                    if (element == null)
                    {
                        continue;
                    }

                    element.AddToClassList(SegmentUssClassName);
                    part.Add(element);
                }
            }
            catch (Exception exception)
            {
                part.Clear();
                Label fallback = new(ChatMarkdownText.ToPlainText(prepared)) { enableRichText = false };
                fallback.style.whiteSpace = WhiteSpace.Normal;
                fallback.AddToClassList(PlainUssClassName);
                fallback.AddToClassList(SegmentUssClassName);
                part.Add(fallback);
                ReportRenderFailure(exception);
            }

            if (finished)
            {
                part.Query<Label>().ForEach(MakeSelectable);
            }

            return part;
        }

        private static VisualElement BuildTable(ChatMarkdownSegment segment)
        {
            VisualElement table = new();
            table.AddToClassList(TableUssClassName);
            table.Add(BuildRow(segment.Header, segment.Alignments, true));
            foreach (IReadOnlyList<string> row in segment.Rows)
            {
                table.Add(BuildRow(row, segment.Alignments, false));
            }

            return table;
        }

        private static VisualElement BuildRow(IReadOnlyList<string> cells,
            IReadOnlyList<ChatMarkdownTableAlignment> alignments, bool header)
        {
            VisualElement row = new();
            row.AddToClassList(TableRowUssClassName);
            if (header)
            {
                row.AddToClassList(TableHeaderRowUssClassName);
            }

            row.style.flexDirection = FlexDirection.Row;
            for (int index = 0; index < cells.Count; index++)
            {
                Label cell = new(ChatMarkdownText.ToPlainText(cells[index])) { enableRichText = false };
                cell.AddToClassList(TableCellUssClassName);
                ChatMarkdownTableAlignment alignment =
                    index < alignments.Count ? alignments[index] : ChatMarkdownTableAlignment.None;
                if (alignment == ChatMarkdownTableAlignment.Center || alignment == ChatMarkdownTableAlignment.Right)
                {
                    cell.AddToClassList(TableCellUssClassName + "--" +
                                        (alignment == ChatMarkdownTableAlignment.Center ? "center" : "right"));
                }

                cell.style.flexGrow = 1f;
                cell.style.flexShrink = 1f;
                cell.style.flexBasis = 0f;
                cell.style.whiteSpace = WhiteSpace.Normal;
                row.Add(cell);
            }

            return row;
        }

        private void MarkLastSegment()
        {
            VisualElement last = null;
            for (int index = childCount - 1; index >= 0 && last == null; index--)
            {
                VisualElement part = this[index];
                if (part.childCount > 0)
                {
                    last = part[part.childCount - 1];
                }
            }

            if (ReferenceEquals(last, _lastSegment))
            {
                return;
            }

            _lastSegment?.RemoveFromClassList(LastSegmentUssClassName);
            last?.AddToClassList(LastSegmentUssClassName);
            _lastSegment = last;
        }

        private void OnKeyDown(KeyDownEvent evt)
        {
            if (!IsCopyShortcut(evt.keyCode, evt.actionKey) || HasSelectedText())
            {
                return;
            }

            if (CopyMarkdownToClipboard())
            {
                evt.StopPropagation();
            }
        }

        private bool HasSelectedText()
        {
            bool selected = false;
            this.Query<Label>().ForEach(label =>
            {
                if (!selected && label.selection.isSelectable && label.selection.HasSelection())
                {
                    selected = true;
                }
            });
            return selected;
        }

        private static void MakeSelectable(Label label)
        {
            label.selection.isSelectable = true;
            label.selection.doubleClickSelectsWord = true;
            label.selection.tripleClickSelectsLine = true;
            label.focusable = true;
        }

        private static void ReportRenderFailure(Exception exception)
        {
            if (_renderFailureLogged)
            {
                return;
            }

            _renderFailureLogged = true;
            GameLoggerUnscopedFallback.Instance.LogWarning(GameLogFeature.Core,
                "[CoreAiMarkdownView] Markdown rendering failed; the text is shown as plain words. Later failures are " +
                "not logged. " + exception);
        }
    }
}
