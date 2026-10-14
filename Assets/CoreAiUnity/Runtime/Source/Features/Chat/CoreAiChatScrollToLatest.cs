using System;
using UnityEngine.UIElements;

namespace CoreAI.Chat
{
    /// <summary>
    /// Counts assistant messages that arrived while the reader stood away from the bottom of the feed.
    /// Restored history and the reader's own messages are never observed, so they never become unread.
    /// </summary>
    public sealed class CoreAiChatUnreadCounter
    {
        /// <summary>The badge stops growing here; a two-digit badge is enough to say "a lot".</summary>
        public const int MaxCount = 99;

        /// <summary>Messages that arrived since the reader last stood at the bottom.</summary>
        public int Count { get; private set; }

        /// <summary>One new assistant message. Counted only when the reader is not at the bottom.</summary>
        public void Observe(bool readerAtBottom)
        {
            if (!readerAtBottom && Count < MaxCount)
            {
                Count++;
            }
        }

        /// <summary>The reader reached the bottom: everything is read.</summary>
        public void MarkRead() => Count = 0;
    }

    /// <summary>
    /// The "jump to latest" affordance of the chat feed: a button that is shown while the reader stands
    /// away from the bottom, and an unread badge that counts assistant messages which arrived meanwhile.
    /// <para>
    /// It is the companion of <see cref="ChatScrollAnchor.KeepPosition"/>: when the feed never moves on its
    /// own, a new answer below the edge of the viewport is only visible through this button and its badge.
    /// The feature is opt-in by markup: a host whose UXML has no element named <see cref="ButtonName"/>
    /// simply has no button, and nothing else changes.
    /// </para>
    /// <para>
    /// Showing and hiding go through a callback, so a host can animate them its own way; the default
    /// (<see cref="ApplyVisibleClass"/>) toggles <see cref="VisibleClassName"/> and leaves the animation to
    /// USS transitions.
    /// </para>
    /// </summary>
    public sealed class CoreAiChatScrollToLatest
    {
        /// <summary>UXML name of the button.</summary>
        public const string ButtonName = "coreai-chat-scroll-to-latest";

        /// <summary>UXML name of the unread badge (a <see cref="Label"/>).</summary>
        public const string BadgeName = "coreai-chat-unread-count";

        /// <summary>USS class the default visibility callback puts on a shown element.</summary>
        public const string VisibleClassName = "coreai-chat-scroll-to-latest--visible";

        private readonly CoreAiChatUnreadCounter _counter = new();
        private readonly Func<bool> _isReaderAtBottom;
        private readonly Action _scrollToLatest;
        private readonly Action<VisualElement, bool> _applyVisibility;

        private ScrollView _view;
        private Scroller _scroller;
        private VisualElement _content;
        private VisualElement _viewport;
        private Button _button;
        private Label _badge;
        private IVisualElementScheduledItem _deferredRefresh;

        // WHY: the feed may leave its panel for good (a reassembled document, its allocation released) or only
        // for a while (a host tab switch that re-inserts the same tree without calling BindUI). While detached
        // nothing here touches the tree; the references are kept so the same tree works again on re-attach.
        private bool _detached;

        /// <param name="isReaderAtBottom">The panel's own "feed stands at the bottom" rule, so the button and
        /// the auto-scroll never disagree about where the bottom is.</param>
        /// <param name="scrollToLatest">What a click does (the panel scrolls to the bottom and follows again).</param>
        /// <param name="applyVisibility">Shows or hides one element; <c>null</c> = <see cref="ApplyVisibleClass"/>.</param>
        public CoreAiChatScrollToLatest(
            Func<bool> isReaderAtBottom,
            Action scrollToLatest,
            Action<VisualElement, bool> applyVisibility = null)
        {
            _isReaderAtBottom = isReaderAtBottom ?? throw new ArgumentNullException(nameof(isReaderAtBottom));
            _scrollToLatest = scrollToLatest ?? throw new ArgumentNullException(nameof(scrollToLatest));
            _applyVisibility = applyVisibility ?? ApplyVisibleClass;
        }

        /// <summary>Unread assistant messages right now.</summary>
        public int UnreadCount => _counter.Count;

        /// <summary>Whether a button was found in the current tree.</summary>
        public bool IsBound => _button != null && _view != null;

        /// <summary>The bound feed left its panel and has not come back yet.</summary>
        public bool IsDetached => _detached;

        /// <summary>The button is shown exactly while the reader is away from the bottom.</summary>
        public static bool ShouldShowButton(bool readerAtBottom) => !readerAtBottom;

        /// <summary>The badge needs something to count, and is pointless at the bottom.</summary>
        public static bool ShouldShowBadge(bool readerAtBottom, int unreadCount) =>
            !readerAtBottom && unreadCount > 0;

        /// <summary>Default visibility: a USS class plus picking, so a hidden button never eats a click.</summary>
        public static void ApplyVisibleClass(VisualElement element, bool visible)
        {
            if (element == null)
            {
                return;
            }

            element.EnableInClassList(VisibleClassName, visible);
            element.pickingMode = visible && element is Button ? PickingMode.Position : PickingMode.Ignore;
        }

        /// <summary>
        /// Resolves the button and badge in <paramref name="root"/> and starts tracking <paramref name="view"/>.
        /// Re-binding to a rebuilt tree first releases the previous one.
        /// </summary>
        public void Bind(VisualElement root, ScrollView view)
        {
            Unbind(true);
            if (root == null || view == null)
            {
                return;
            }

            _button = root.Q<Button>(ButtonName);
            if (_button == null)
            {
                return;
            }

            _badge = root.Q<Label>(BadgeName);
            _view = view;
            _scroller = view.verticalScroller;
            _content = view.contentContainer;
            _viewport = view.contentViewport;

            _button.focusable = false;
            _button.RegisterCallback<ClickEvent>(OnButtonClicked);
            if (_scroller != null)
            {
                _scroller.valueChanged += OnScrollValueChanged;
            }

            _content?.RegisterCallback<GeometryChangedEvent>(OnGeometryChanged);
            _viewport?.RegisterCallback<GeometryChangedEvent>(OnGeometryChanged);
            _view.RegisterCallback<DetachFromPanelEvent>(OnViewDetached);
            _view.RegisterCallback<AttachToPanelEvent>(OnViewAttached);
            Refresh();
        }

        /// <summary>
        /// Releases the current tree and hides its button and badge (a tree that outlives the panel's
        /// OnDisable must not keep a clickable button without a handler). Safe to call on a tree the panel
        /// has already dropped: a detached tree is only forgotten, never touched, and any call on a released
        /// element is swallowed when <paramref name="ignoreReleasedElements"/> is set.
        /// </summary>
        public void Unbind(bool ignoreReleasedElements)
        {
            if (_scroller != null)
            {
                _scroller.valueChanged -= OnScrollValueChanged;
            }

            if (_detached)
            {
                Forget();
                return;
            }

            try
            {
                _deferredRefresh?.Pause();
                if (_button != null)
                {
                    _applyVisibility(_button, false);
                }

                if (_badge != null)
                {
                    _applyVisibility(_badge, false);
                }

                _button?.UnregisterCallback<ClickEvent>(OnButtonClicked);
                _content?.UnregisterCallback<GeometryChangedEvent>(OnGeometryChanged);
                _viewport?.UnregisterCallback<GeometryChangedEvent>(OnGeometryChanged);
                _view?.UnregisterCallback<DetachFromPanelEvent>(OnViewDetached);
                _view?.UnregisterCallback<AttachToPanelEvent>(OnViewAttached);
            }
            catch (InvalidOperationException) when (ignoreReleasedElements)
            {
            }

            Forget();
        }

        /// <summary>
        /// The feed left its panel: stop touching the tree - its allocation may already be released (a
        /// scheduled item paused on such a tree reaches into the dead element). Only the managed scroller event
        /// is unsubscribed; the references stay for <see cref="OnViewAttached"/>.
        /// </summary>
        private void OnViewDetached(DetachFromPanelEvent evt)
        {
            if (!ReferenceEquals(evt.target, _view))
            {
                return;
            }

            if (_scroller != null)
            {
                _scroller.valueChanged -= OnScrollValueChanged;
            }

            _deferredRefresh = null;
            _detached = true;
        }

        /// <summary>The same feed came back to a panel (a host re-inserted the tree without BindUI).</summary>
        private void OnViewAttached(AttachToPanelEvent evt)
        {
            if (!_detached || !ReferenceEquals(evt.target, _view))
            {
                return;
            }

            _detached = false;
            if (_scroller != null)
            {
                _scroller.valueChanged -= OnScrollValueChanged;
                _scroller.valueChanged += OnScrollValueChanged;
            }

            // WHY: attach arrives before layout, so the scroller range still describes the tree as it left.
            // Rows added while detached would read as "at the bottom" and be marked read unseen; marking read
            // waits for the geometry pass that those rows trigger.
            Apply(false);
        }

        private void Forget()
        {
            _detached = false;
            _deferredRefresh = null;
            _view = null;
            _scroller = null;
            _content = null;
            _viewport = null;
            _button = null;
            _badge = null;
        }

        /// <summary>
        /// A new assistant message is about to be appended. <paramref name="readerSeesIt"/>: the reader stood
        /// at the bottom BEFORE the append AND the feed will move to show the message. Under
        /// <see cref="ChatScrollAnchor.KeepPosition"/> the feed does not move, so a message that lands below
        /// the edge is unread even for a reader who was at the bottom.
        /// <para>
        /// Nothing is marked read here: the layout has not run yet, so "at the bottom" still describes the
        /// old content. The geometry pass that follows calls <see cref="Refresh"/>, which marks everything
        /// read if the message turned out to be on screen after all.
        /// </para>
        /// </summary>
        public void ObserveAssistantMessage(bool readerSeesIt)
        {
            // WHY: without a bound button nothing can ever mark the count read, so it must not grow either.
            if (_view == null)
            {
                return;
            }

            _counter.Observe(readerSeesIt);
            Apply(false);
        }

        /// <summary>Re-applies the button and badge to the current scroll position; at the bottom everything is read.</summary>
        public void Refresh() => Apply(true);

        private void Apply(bool markReadAtBottom)
        {
            if (_view == null || _detached)
            {
                return;
            }

            bool atBottom = _isReaderAtBottom();
            if (atBottom && markReadAtBottom)
            {
                _counter.MarkRead();
            }

            _applyVisibility(_button, ShouldShowButton(atBottom));
            if (_badge != null)
            {
                _badge.text = _counter.Count.ToString();
                _applyVisibility(_badge, ShouldShowBadge(atBottom, _counter.Count));
            }
        }

        private void OnButtonClicked(ClickEvent evt)
        {
            if (_view != null && !_detached && ReferenceEquals(evt.target, _button))
            {
                _scrollToLatest();
            }
        }

        private void OnScrollValueChanged(float value) => Refresh();

        private void OnGeometryChanged(GeometryChangedEvent evt)
        {
            if (_view == null || _detached ||
                (!ReferenceEquals(evt.currentTarget, _content) && !ReferenceEquals(evt.currentTarget, _viewport)))
            {
                return;
            }

            // WHY: the scroller range can still lag this geometry event (ScrollView may defer its update to the
            // next layout pass), and a stale range says "at the bottom" for a row that just landed below the
            // edge. The immediate pass only re-applies visibility; marking read waits for the deferred one.
            Apply(false);
            if (_deferredRefresh == null)
            {
                _deferredRefresh = _view.schedule.Execute(Refresh);
            }
            else
            {
                _deferredRefresh.ExecuteLater(0);
            }
        }
    }
}
