using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Composition;
using CoreAI.Infrastructure.Logging;
using CoreAI.Threading;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.Chat
{
    /// <summary>
    /// Where the chat scrolls when a NEW assistant message appears.
    /// <para>
    /// <see cref="Bottom"/> — classic chat: every message and every streamed chunk pins the view to the
    /// bottom, so a long answer is read from its END. <see cref="AssistantMessageStart"/> — the
    /// ChatGPT/DeepSeek pattern: the first assistant row of a turn is pinned with its TOP at the top of
    /// the viewport and the view does not follow the streamed tail. <see cref="KeepPosition"/> — the
    /// view is never moved for assistant content at all: whatever the reader is looking at stays under
    /// their eyes and new messages simply grow below, exactly like a channel feed. User messages and
    /// history restore keep scrolling to the bottom in every mode — that scroll is the reader's own
    /// action, not an interruption.
    /// </para>
    /// <para>
    /// <see cref="KeepPosition"/> exists because even a top-pinned new answer is an interruption when
    /// the reader deliberately scrolled UP: a learner re-reading the theory above a quiz was dragged
    /// down by every message the teacher wrote and had to scroll back by hand.
    /// </para>
    /// </summary>
    public enum ChatScrollAnchor
    {
        Bottom = 0,
        AssistantMessageStart = 1,
        KeepPosition = 2,

        /// <summary>
        /// Поведение мессенджера: лента едет за новым содержимым, ПОКА читатель стоит у низа, и
        /// перестаёт, как только он ушёл вверх. Безусловное <see cref="KeepPosition"/> оказалось
        /// слишком строгим: ученик, только что отправивший сообщение, стоит внизу, и ответ вместе с
        /// карточкой задания появлялся у него под кромкой экрана — то есть его будто и не было.
        /// </summary>
        FollowIfAtBottom = 3,
    }

    /// <summary>
    /// UI Toolkit chat panel that sends player text through <see cref="CoreAiChatService"/>,
    /// renders streamed or buffered assistant responses, and exposes overridable hooks for
    /// request construction, message formatting, tool-call display, and error text.
    /// </summary>
    /// <remarks>
    /// Override <see cref="OnMessageSending"/>, <see cref="OnResponseReceived"/>,
    /// <see cref="CreateMessageBubble"/>, <see cref="FormatResponseText"/>,
    /// <see cref="FormatToolExecutedForChat"/>, or <see cref="ResolveTimeoutMessage"/>
    /// to customize behaviour without replacing the whole panel.
    /// </remarks>
    public class CoreAiChatPanel : MonoBehaviour
    {
        private const string MobileClassName = "coreai-mobile";
        private const string FullscreenLayoutClassName = "coreai-chat-fullscreen";
        private const string CollapsedClassName = "coreai-collapsed";
        private const string CollapsedPrefsKey = "CoreAI.Chat.Collapsed";
        private const string SendButtonStopClassName = "coreai-chat-send-button-stop";
        private const string TypingDotsElementName = "coreai-typing-dots";
        private const string TypingDotsClassName = "coreai-typing-dots";
        private const string TypingDotsPulseClassName = "coreai-typing-dots--pulse";
        private const string TypingDotClassName = "coreai-typing-dot";
        private const string TypingDotsStyleSheetResourcePath = "CoreAI/UI/CoreAiChatTypingDots";
        private const int TypingDotCount = 3;

        /// <summary>
        /// Period of the single class flip that drives the typing dots. Must not be SHORTER than the USS
        /// <c>transition-duration</c> plus the last dot's <c>transition-delay</c>, otherwise the flip
        /// reverses a wave that is still travelling and the third dot never arrives. Equality is the
        /// intended tuning: the wave lands exactly as it turns around.
        /// <para>
        /// The two halves of that invariant live in different files, so
        /// <c>CoreAiChatPanelRenderPathsEditModeTests.TypingDotsPulseInterval_CoversTheStyleSheetWave</c>
        /// parses <c>CoreAiChatTypingDots.uss</c> and fails when either side drifts.
        /// </para>
        /// </summary>
        private const int TypingDotsPulseIntervalMilliseconds = 700;

        [Header("Config")]
        [Tooltip("Chat configuration asset (Assets -> Create -> CoreAI -> Chat Config).")]
        [SerializeField]
        protected CoreAiChatConfig config;

        private static readonly CoreAiChatOptions DefaultOptions = CoreAiChatOptions.CreateDefault();
        private ICoreAiChatOptions _runtimeOptions;
#if UNITY_6000_5_OR_NEWER
        private PanelRenderer _panelRenderer;
#endif

        /// <summary>
        /// When embedded, the panel renders into a caller-supplied VisualElement instead of its own
        /// UIDocument/PanelRenderer. The chat UXML is cloned into that host once the host's panel is
        /// ready, then the panel binds to it exactly like the UIDocument path.
        /// </summary>
        private bool _embeddedHostMode;

        private VisualElement _embeddedHost;
        private VisualTreeAsset _embeddedChatTemplate;
        private StyleSheet _embeddedStyleSheet;
        private bool _embeddedTreeBuilt;

        /// <summary>
        /// The chat tree cloned into <see cref="_embeddedHost"/>. Kept across a disable/enable cycle so
        /// re-enabling rebinds the existing tree instead of stacking a second clone under the host.
        /// </summary>
        private VisualElement _embeddedChatRoot;

        /// <summary>Project game logger (shared fallback when no scoped logger is available).</summary>
        protected static IGameLogger Logger => GameLoggerUnscopedFallback.Instance;

        [Header("Custom USS (optional)")]
        [Tooltip("Optional stylesheet layered on top of the default theme. Leave empty to use the standard style.")]
        [SerializeField]
        protected StyleSheet customStyleSheet;

        [Header("UI Templates (optional)")]
        [Tooltip(
            "Optional message bubble template. If empty, CoreAiChatPanel creates the default bubble element in code.")]
        [SerializeField]
        protected VisualTreeAsset messageBubbleTemplate;

        protected VisualElement Root;
        protected VisualElement ChatContainer;
        protected ScrollView MessageScroll;
        protected TextField InputField;
        protected Button SendButton;
        protected Button ClearButton;
        protected Button CollapseButton;
        protected Button FabButton;
        protected VisualElement TypingIndicator;
        protected VisualElement TypingAvatar;
        protected Label TypingLabel;
        protected Label HeaderTitle;
        protected VisualElement HeaderIcon;
        private Label _longRequestHint;
        private float _longRequestHintArmedSince = float.NaN;
        private const float LongRequestHintMinSeconds = 3f;
        private const string DefaultStreamingToolProgressHint = "Processing...";

        /// <summary>Whether the chat panel is currently collapsed into the FAB.</summary>
        public bool IsCollapsed { get; private set; }

        private Label _streamingLabel;
        private bool _isStreaming;

        /// <summary>
        /// True once the streaming bubble's rendered text hit <see cref="MaxAssistantRenderChars"/>;
        /// later chunks still reach the full-response accumulator but are no longer rendered.
        /// </summary>
        private bool _streamingRenderCapReached;

        private bool _isSending; // WHY: prevents Shift+Enter sending while AI is busy
        private bool _stopRequestedByUser;
        private bool _isStopping;
        private bool _isClearing;

        private bool _lastPublishedBusy;

        /// <summary>
        /// True only while this component owns an enabled Unity lifecycle. Set before any OnEnable work and
        /// cleared before any OnDisable cancellation/busy callback so reentrant submits cannot cross trees.
        /// </summary>
        private bool _lifecycleActive;

        /// <summary>
        /// Monotonic counter incremented when turn/lifecycle ownership changes: start, Stop/abandon, or
        /// panel disable. Turn code captures the value at start and compares it before mutating shared
        /// UI/busy state, so a superseded turn that is still unwinding (e.g. after an agent switch or an
        /// immediate disable/enable cycle) can never write into the newer turn's bubbles.
        /// </summary>
        private int _currentTurnGeneration;

        /// <summary>
        /// When an assistant turn streams prose, then a tool round runs, then more prose arrives,
        /// the post-tool prose must land in a NEW bubble (claude/cursor behaviour). This flag marks
        /// that the in-flight streaming bubble was sealed at a tool-round boundary; the next visible
        /// prose chunk opens a fresh bubble instead of appending to the old (now-resolved) one.
        /// </summary>
        private bool _streamingBubbleSealed;

        /// <summary>
        /// Prose bubbles opened during the current turn, in order. A turn is split into several bubbles
        /// when tool rounds run between prose (see <see cref="_streamingBubbleSealed"/>), and only this
        /// class knows where those boundaries are: <see cref="OnResponseReceived"/> receives the whole
        /// turn CONCATENATED, with nothing to say which part landed in which bubble.
        /// <para>
        /// Without this list a host that post-processes bubbles (markdown rendering, syntax highlighting,
        /// per-bubble actions) has to rediscover them from the visual tree by CSS class and text
        /// matching — brittle guesswork that silently breaks exactly when a turn is split. A shipping
        /// host hit that: the whole response was re-rendered into the LAST bubble, so the prose before
        /// the tool call was shown twice and the sealed bubble stayed unrendered.
        /// </para>
        /// </summary>
        private readonly List<Label> _turnStreamingBubbles = new();

        /// <summary>
        /// Prose bubbles of the current (or last completed) turn, in the order they were opened. A turn
        /// split by tool rounds has more than one, and each holds only its own segment of the answer.
        /// Valid from the first streamed chunk until the next turn starts, so it can be read from
        /// <see cref="OnResponseReceived"/> and from work scheduled shortly after it.
        /// </summary>
        protected IReadOnlyList<Label> TurnStreamingBubbles => _turnStreamingBubbles;

        /// <summary>Tracks the in-flight tool round so ToolRoundStarted can carry the last tool name.</summary>
        private string _lastToolNameInTurn;

        /// <summary>1-based iteration index inside the current turn.</summary>
        private int _toolRoundIterationInTurn;

        /// <summary>
        /// Stream-gap diagnostic: if the orchestrator goes silent for more than this many seconds
        /// between chunks, log one Info line so the host can tell "model slow" from "UI lost a chunk".
        /// </summary>
        private const double StreamGapWarnSeconds = 5.0;

        /// <summary>Runtime overrides for hotkeys. <c>null</c> = follow <see cref="config"/> (or built-in defaults if config is null).</summary>
        private bool? _runtimeOverrideOpenChatShortcutEnabled;

        private KeyCode? _runtimeOverrideOpenChatHotkey;
        private bool? _runtimeOverrideEscapeChatShortcuts;
        private bool? _runtimeOverrideChatRequiresVisibleCursor;

        /// <summary>Tracks the previous frame's cursor-gated input-allowed state so <see cref="Update"/>
        /// can detect the allowed → blocked transition and release keyboard focus exactly once.</summary>
        private bool? _wasChatInputAllowed;

        private readonly ThinkBlockStreamFilter _thinkFilter = new();
        private bool _streamingStartedVisible;
        private bool _nonStreamAssistantOutputStarted;

        /// <summary>
        /// Prevents duplicate deferred scroll jobs; nested <c>schedule.Execute</c> calls can
        /// destabilize ScrollView layout and leave the scrollbar at an old position.
        /// </summary>
        private bool _streamingScrollScheduled;

        /// <summary>
        /// At most one pending <see cref="ScrollToBottom"/> chain per burst of appended messages, so a
        /// flood of <see cref="AddMessage"/> calls does not stack five scheduler jobs per message.
        /// </summary>
        private bool _scrollToBottomScheduled;

        // WHY: the row the current turn is anchored to (first assistant row, or the latest revealed card),
        // and how — pinned by its top or revealed with the minimal scroll. Reset at turn start and on
        // every user message so the next answer anchors again. Only used when ScrollAnchor is not Bottom.
        private VisualElement _turnAnchorRow;
        private TurnAnchorMode _turnAnchorMode;

        private enum TurnAnchorMode
        {
            Start,
            Reveal,
        }

        private IVisualElementScheduledItem _typingAnimation;
        private VisualElement _typingDots;

        /// <summary>
        /// Typing-row state kept OUTSIDE the visual tree, so a <c>PanelRenderer</c> rebuild mid-turn can
        /// be restored by <see cref="ApplyPanelStateToTree"/> instead of silently dropping the indicator.
        /// </summary>
        private bool _typingIndicatorVisible;

        private bool _typingToolProgressHintActive;

        protected CoreAiChatService _chatService;
        private IActorIdentityProvider _actorIdentityProvider;

        public virtual CoreAiChatService ChatService
        {
            get => _chatService;
            set
            {
                _chatService = value;
                if (isActiveAndEnabled && Root != null)
                {
                    HydrateStartupMessagesFromStore();
                    TryRegisterToolCallChatDisplay();
                }
            }
        }

        /// <summary>Receives the actor identity provider owned by host composition.</summary>
        internal void SetActorIdentityProvider(IActorIdentityProvider actorIdentityProvider)
        {
            _actorIdentityProvider = actorIdentityProvider ??
                                     throw new ArgumentNullException(nameof(actorIdentityProvider));
        }

        private CancellationTokenSource _cts;
        private CancellationTokenSource _activeRequestCts;

        /// <summary>On user message sent event.</summary>
        public event Action<string> OnUserMessageSent;

        /// <summary>On ai response completed event.</summary>
        public event Action<string> OnAiResponseCompleted;

        /// <summary>
        /// Raised when the chat panel enters or leaves a busy state.
        /// </summary>
        public event Action<bool> BusyStateChanged;

        /// <summary>
        /// Raised when int.
        /// </summary>
        public event Action<int, string> ToolRoundStarted;

        /// <summary>Is busy.</summary>
        public bool IsBusy => _isSending || _isStreaming || _isStopping || _isClearing;

        /// <summary>
        /// Current turn generation.
        /// </summary>
        public int CurrentTurnGeneration => _currentTurnGeneration;

        private CoreAi.ToolExecutedHandler? _toolExecutedChatHandler;

        private ICoreAiChatOptions Options => _runtimeOptions ?? (config != null ? config : DefaultOptions);
        private ICoreAiChatTextOptions TextOptions => Options as ICoreAiChatTextOptions ?? DefaultOptions;

        private bool IsWorldSpacePanelRenderer()
        {
#if UNITY_6000_5_OR_NEWER
            return _panelRenderer != null &&
                   _panelRenderer.panelSettings != null &&
                   _panelRenderer.panelSettings.renderMode == PanelRenderMode.WorldSpace;
#else
            return false;
#endif
        }

        private bool IsElementReadyForStyle(VisualElement element)
        {
            if (element == null || element.panel == null)
            {
                return false;
            }

            return Root == null || Root.panel == null || element.panel == Root.panel;
        }

        public void SetRuntimeOptions(ICoreAiChatOptions options)
        {
            _runtimeOptions = options;
            if (isActiveAndEnabled && Root != null)
            {
                ApplyConfig();
                HydrateStartupMessagesFromStore();
                ApplyShortcutTooltips();
            }
        }

        public void ClearRuntimeOptions()
        {
            SetRuntimeOptions(null);
        }

        protected virtual void Awake()
        {
            _cts = new CancellationTokenSource();
            ConfigureWebGlKeyboardInput();
        }

        /// <summary>
        /// Releases global WebGL keyboard capture so browser input fields and the chat panel receive text.
        /// </summary>
        private static void ConfigureWebGlKeyboardInput()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            WebGLInput.captureAllKeyboardInput = false;
#endif
        }

        /// <summary>
        /// Polls keyboard shortcuts, updates long-request feedback, and reapplies responsive layout.
        /// </summary>
        protected virtual void Update()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (WebGLInput.captureAllKeyboardInput)
            {
                WebGLInput.captureAllKeyboardInput = false;
            }
#endif
            TickChatInputCursorGating();
            PollChatToggleShortcuts();
            TickLongRequestHint();
        }

        protected virtual void OnEnable()
        {
            _lifecycleActive = true;

            if (_embeddedHostMode)
            {
                InitializeEmbeddedHost();
                return;
            }

#if UNITY_6000_5_OR_NEWER
            _panelRenderer = GetComponent<PanelRenderer>();
            if (_panelRenderer != null)
            {
                _panelRenderer.RegisterUIReloadCallback(OnPanelRendererUiReloaded);
                return;
            }
#endif

            UIDocument uiDoc = GetComponent<UIDocument>();
            if (uiDoc == null)
            {
#if UNITY_6000_5_OR_NEWER
                Logger.LogError(GameLogFeature.Core,
                    "[CoreAiChatPanel] PanelRenderer or UIDocument component not found on this GameObject!");
#else
                Logger.LogError(GameLogFeature.Core,
                    "[CoreAiChatPanel] UIDocument component not found on this GameObject!");
#endif
                return;
            }

            InitializeUiRoot(uiDoc.rootVisualElement);
        }

#if UNITY_6000_5_OR_NEWER
        private void OnPanelRendererUiReloaded(PanelRenderer renderer, VisualElement rootElement)
        {
            InitializeUiRoot(rootElement);
        }
#endif

        private void InitializeUiRoot(VisualElement rootElement)
        {
            if (rootElement == null)
            {
                Logger.LogError(GameLogFeature.Core,
                    "[CoreAiChatPanel] UI root is not ready.");
                return;
            }

            UnbindUiCallbacks(true);
            StopTypingAnimation();
            ResetUiReferences();

            Root = rootElement;
            ApplyFixedWorldSpaceSize(Root);
            if (customStyleSheet != null)
            {
                Root.styleSheets.Add(customStyleSheet);
            }

            BindUI();
            InitService();
            ApplyConfig();
            HydrateStartupMessagesFromStore();
            TryRegisterToolCallChatDisplay();
        }

        /// <summary>
        /// Creates a chat panel that renders into a caller-supplied <see cref="VisualElement"/> instead of
        /// a <see cref="UIDocument"/>/<c>PanelRenderer</c>, so the whole chat can be embedded inside another
        /// UI Toolkit surface (e.g. a CoreAI Hub tab). The panel lives on a dedicated hidden
        /// <see cref="GameObject"/>; destroy it via <see cref="UnityEngine.Object.Destroy(UnityEngine.Object)"/>
        /// on <see cref="Component.gameObject"/> when the host element goes away.
        /// </summary>
        /// <param name="host">Container the chat UI is instantiated into. Must be non-null.</param>
        /// <param name="chatTemplate">
        /// Chat UXML (e.g. <c>CoreAiChat.uxml</c>) cloned into <paramref name="host"/>. When null the panel
        /// binds directly to <paramref name="host"/>, which only works if it already contains the chat tree.
        /// </param>
        /// <param name="chatStyleSheet">Optional stylesheet added to the cloned chat root.</param>
        /// <param name="chatConfig">Optional chat configuration asset.</param>
        /// <param name="gameObjectName">Name for the backing GameObject.</param>
        public static CoreAiChatPanel CreateEmbedded(
            VisualElement host,
            VisualTreeAsset chatTemplate,
            StyleSheet chatStyleSheet = null,
            CoreAiChatConfig chatConfig = null,
            string gameObjectName = "CoreAiChatPanel (Embedded)")
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            GameObject go = new(gameObjectName);
            go.SetActive(false); // WHY: configure the panel before OnEnable runs
            CoreAiChatPanel panel = go.AddComponent<CoreAiChatPanel>();
            panel._embeddedHostMode = true;
            panel._embeddedHost = host;
            panel._embeddedChatTemplate = chatTemplate;
            panel._embeddedStyleSheet = chatStyleSheet;
            if (chatConfig != null)
            {
                panel.config = chatConfig;
            }

            go.SetActive(true); // WHY: triggers Awake + OnEnable -> InitializeEmbeddedHost
            return panel;
        }

        private void InitializeEmbeddedHost()
        {
            if (_embeddedHost == null)
            {
                Logger.LogError(GameLogFeature.Core,
                    "[CoreAiChatPanel] Embedded host mode requested without a host element.");
                return;
            }

            // WHY: the chat tree must be attached to a live panel before styles/layout apply, mirroring
            // the UIDocument path where rootVisualElement is already panel-attached in OnEnable.
            _embeddedHost.RegisterCallback<AttachToPanelEvent>(OnEmbeddedHostAttached);
            if (_embeddedHost.panel != null)
            {
                BuildEmbeddedChatTree();
            }
        }

        private void OnEmbeddedHostAttached(AttachToPanelEvent evt)
        {
            BuildEmbeddedChatTree();
        }

        private void BuildEmbeddedChatTree()
        {
            if (_embeddedTreeBuilt || _embeddedHost == null || _embeddedHost.panel == null)
            {
                return;
            }

            _embeddedTreeBuilt = true;

            // WHY: OnDisable drops the UI references but leaves the already-inserted tree parented to the
            // host, so a re-enable must rebind that tree. Cloning a second one would stack two chats.
            VisualElement chatRoot = _embeddedChatRoot;
            if (chatRoot == null || (chatRoot != _embeddedHost && chatRoot.parent != _embeddedHost))
            {
                if (_embeddedChatTemplate != null)
                {
                    chatRoot = _embeddedChatTemplate.CloneTree();
                    chatRoot.style.flexGrow = 1f;
                    _embeddedHost.Add(chatRoot);
                    NeutralizeFloatingContainer(chatRoot);
                }
                else
                {
                    chatRoot = _embeddedHost;
                }

                _embeddedChatRoot = chatRoot;
            }

            if (_embeddedStyleSheet != null && !chatRoot.styleSheets.Contains(_embeddedStyleSheet))
            {
                chatRoot.styleSheets.Add(_embeddedStyleSheet);
            }

            InitializeUiRoot(chatRoot);
        }

        /// <summary>
        /// The standalone chat container (<c>coreai-chat-root</c> / <c>.coreai-chat-container</c>) is a
        /// floating panel: <c>position: absolute</c>, a fixed 650×910, anchored bottom-right. Embedded in a
        /// host that clips overflow (e.g. a Hub tab), that fixed height overflows and the top of the panel —
        /// the header with the clear / collapse / agent controls — is clipped away. In embedded mode we
        /// override those styles so the container flows inside and fills the host instead of floating.
        /// </summary>
        private static void NeutralizeFloatingContainer(VisualElement chatRoot)
        {
            if (chatRoot == null)
            {
                return;
            }

            chatRoot.style.flexGrow = 1f;
            chatRoot.style.width = Length.Percent(100);
            chatRoot.style.height = Length.Percent(100);

            VisualElement container = chatRoot.Q(className: "coreai-chat-container");
            if (container == null)
            {
                return;
            }

            // WHY: .coreai-chat-embedded strips the floating border/radius, but the container's fixed 650×910
            // and bottom-right anchoring resist USS/inline overrides on this Unity version, so pin it absolute
            // top-left and drive its exact pixel size from the wrapper's resolved geometry to avoid left-clip.
            container.AddToClassList("coreai-chat-embedded");

            // WHY: re-assert position AND size on every layout pass. Doing it inside the GeometryChanged callback
            // (post-layout) makes it stick where a one-shot pre-layout set was being clobbered back to the
            // container's floating right/bottom:24 anchor (it resolved to a −24 offset on all sides).
            void Sync()
            {
                container.style.position = Position.Absolute;
                container.style.left = 0f;
                container.style.top = 0f;
                container.style.right = 0f;
                container.style.bottom = 0f;

                float w = chatRoot.resolvedStyle.width;
                float h = chatRoot.resolvedStyle.height;
                if (w > 1f)
                {
                    container.style.width = w;
                }

                if (h > 1f)
                {
                    container.style.height = h;
                }
            }

            chatRoot.RegisterCallback<GeometryChangedEvent>(_ => Sync());
            Sync();
        }

#if UNITY_6000_5_OR_NEWER
        private void ApplyFixedWorldSpaceSize(VisualElement rootElement)
        {
            if (_panelRenderer == null ||
                rootElement == null ||
                rootElement.panel == null ||
                _panelRenderer.panelSettings == null ||
                _panelRenderer.panelSettings.renderMode != PanelRenderMode.WorldSpace ||
                _panelRenderer.worldSpaceSizeMode != WorldSpaceSizeMode.Fixed)
            {
                return;
            }

            Vector2 size = _panelRenderer.worldSpaceSize;
            if (size.x <= 0f || size.y <= 0f)
            {
                return;
            }

            rootElement.style.width = size.x;
            rootElement.style.height = size.y;
            rootElement.style.minWidth = size.x;
            rootElement.style.minHeight = size.y;
            rootElement.style.maxWidth = size.x;
            rootElement.style.maxHeight = size.y;
            rootElement.style.flexGrow = 0;
            rootElement.style.flexShrink = 0;
        }
#else
        private void ApplyFixedWorldSpaceSize(VisualElement rootElement)
        {
        }
#endif

        protected virtual void Start()
        {
            if (_embeddedHostMode)
            {
                // WHY: a Hub tab always shows the expanded chat; the FAB/collapse affordance and the
                // persisted collapsed pref are meaningless inside an embedded surface.
                SetCollapsed(false, false);
                return;
            }

            bool defaultCollapsed = IsMobileScreen();
            bool collapsed = PlayerPrefs.GetInt(CollapsedPrefsKey, defaultCollapsed ? 1 : 0) == 1;
            SetCollapsed(collapsed, false);
        }

        protected virtual void OnDisable()
        {
            // WHY: BusyStateChanged(false) is raised later by teardown and handlers may submit reentrantly.
            // Publish lifecycle ownership first so that submit is rejected before it can reach a provider.
            _lifecycleActive = false;
            bool invalidatedActiveTurn = InvalidateTurnOwnershipOnDisable();
            CancelActiveRequestOnDisable();
            if (invalidatedActiveTurn)
            {
                // WHY: the stale turn is no longer allowed to clear these flags in its own finally. Reset
                // them here, under lifecycle ownership, so an immediate OnEnable can start a successor.
                ResetBusyStateWithoutCancellation();
            }

            if (_embeddedHost != null)
            {
                _embeddedHost.UnregisterCallback<AttachToPanelEvent>(OnEmbeddedHostAttached);
            }

#if UNITY_6000_5_OR_NEWER
            if (_panelRenderer != null)
            {
                _panelRenderer.UnregisterUIReloadCallback(OnPanelRendererUiReloaded);
            }
#endif

            TryUnregisterToolCallChatDisplay();
            UnbindUiCallbacks(true);
            StopTypingAnimation();
            ResetUiReferences();

            // WHY: ResetUiReferences() nulls Root/SendButton/…, so the tree must be re-bound on the next
            // OnEnable. Leaving the flag set made BuildEmbeddedChatTree() return early, InitializeUiRoot()
            // never ran again and the embedded chat rendered but answered nothing.
            _embeddedTreeBuilt = false;
        }

        /// <summary>
        /// Transfers turn ownership away from the lifecycle being disabled before the panel drops its UI tree.
        /// </summary>
        private bool InvalidateTurnOwnershipOnDisable()
        {
            bool hasActiveTurn = IsRequestInProgress || IsCancellationSourceActive(_activeRequestCts);
            // WHY: cancellation is cooperative and its continuation may run after an immediate OnEnable.
            // Move generation on EVERY disable, even if stop already cleared the visible busy flags/CTS,
            // so every continuation born in the old lifecycle sees itself as stale against a rebound tree.
            Interlocked.Increment(ref _currentTurnGeneration);
            return hasActiveTurn;
        }

        /// <summary>
        /// Отменять ли активный запрос, когда панель выключается.
        /// <para>
        /// Пакетное значение — <c>true</c>: панель исчезла, ответ показывать некому. Но у хоста,
        /// где чат — часть урока, выключение панели значит лишь «человек вышел из фокуса»
        /// (в RedoSchool это Esc). Обрывать на этом ход учителя нельзя: собеседник ничего не
        /// прерывал, а по возвращении он видел «ничего не ответили». Хост, у которого история
        /// живёт вне панели, переопределяет свойство и получает ход, доигранный до конца.
        /// </para>
        /// </summary>
        protected virtual bool CancelsActiveRequestOnDisable => true;

        /// <summary>
        /// Cancels the in-flight request when the panel component is disabled, so a hidden/disabled
        /// standalone panel never keeps a zombie streaming turn alive. The Hub collapse path is
        /// unaffected: collapsing only toggles a USS class and never disables the panel GameObject,
        /// so generation intentionally keeps running while the Hub is collapsed. Hosts that own the
        /// transcript outside the panel opt out via <see cref="CancelsActiveRequestOnDisable"/>.
        /// </summary>
        private void CancelActiveRequestOnDisable()
        {
            if (!CancelsActiveRequestOnDisable)
            {
                return;
            }

            CancellationTokenSource active = _activeRequestCts;
            if (!IsCancellationSourceActive(active))
            {
                return;
            }

            try
            {
                active.Cancel();
            }
            catch (Exception ex)
            {
                Logger.LogWarning(GameLogFeature.Core,
                    $"[CoreAiChatPanel] OnDisable: active request cancel failed: {ex.Message}");
            }
        }

        protected virtual void OnDestroy()
        {
            _lifecycleActive = false;
            CoreAiRoutingUi.ControllerChanged -= HandleRoutingControllerChanged;
            AttachRoutingController(null);
            _cts?.Cancel();
            _cts?.Dispose();
            _activeRequestCts?.Cancel();
            _activeRequestCts?.Dispose();
        }

        private Button _examplesButton;
        private DropdownField _agentDropdown;
        private DropdownField _apiProfileDropdown;
        private Button _apiProfileToggle;
        private Label _apiProfileStatus;
        private string _activeRoleId;
        private bool _agentSwitchingEnabled;
        private bool _apiSwitchingEnabled;
        private bool _examplesEnabled;
        private bool _apiSelectorExpanded;
        private ICoreAiRoutingUiController _routingUiController;
        private readonly Dictionary<string, string> _profileIdByLabel = new(StringComparer.Ordinal);
        private const string AutomaticApiProfileLabel = "Automatic / agent default";

        /// <summary>
        /// Per-role rendered transcript, kept in memory regardless of store persistence. Switching agents
        /// clears <see cref="MessageScroll"/> and reloads only from the persisted store, which is per-role
        /// opt-in (<see cref="ICoreAiChatOptions.LoadPersistedChatOnStartup"/>); without this cache a
        /// live, unpersisted conversation is lost the moment the user switches away and back.
        /// </summary>
        private readonly Dictionary<string, List<(string Text, bool IsUser)>> _roleTranscriptCache = new();

        /// <summary>The role currently driving the chat — the runtime-switched role when agent switching is
        /// active, otherwise the configured role. Used for history hydration, tool-call display, and stop/clear
        /// so switching agents re-targets the whole panel, not just outgoing requests.</summary>
        private string ActiveRoleId => _activeRoleId ?? Options?.RoleId ?? BuiltInAgentRoleIds.SmartChat;

        /// <summary>
        /// Enables the agent/role dropdown at runtime (e.g. from a demo controller). Safe to call before or
        /// after the UI is built; the dropdown appears once the chat header exists.
        /// </summary>
        public void EnableAgentSwitching()
        {
            _agentSwitchingEnabled = true;
            _activeRoleId ??= Options?.RoleId ?? BuiltInAgentRoleIds.SmartChat;
            TryBuildAgentDropdown();
        }

        /// <summary>
        /// Enables the compact "≡" example-prompts menu at runtime (e.g. from a demo controller). Off by
        /// default so the base chat ships without demo-flavoured example prompts; safe to call before or
        /// after the UI is built.
        /// </summary>
        public void EnableExamplePrompts()
        {
            _examplesEnabled = true;
            TryBuildExamplesButton();
        }

        /// <summary>
        /// Enables the optional API profile control. The selector stays collapsed until the user opens it.
        /// </summary>
        public void EnableApiSwitching(bool expanded = false)
        {
            _apiSwitchingEnabled = true;
            _apiSelectorExpanded = expanded;
            CoreAiRoutingUi.ControllerChanged -= HandleRoutingControllerChanged;
            CoreAiRoutingUi.ControllerChanged += HandleRoutingControllerChanged;
            AttachRoutingController(CoreAiRoutingUi.Controller);
            TryBuildApiProfileControls();
        }

        /// <summary>Whether the optional API profile selector is currently expanded.</summary>
        public bool IsApiSelectorExpanded => _apiSelectorExpanded;

        /// <summary>Selected routing profile id for the active agent role.</summary>
        public string SelectedRoutingProfileId => ResolveSelectedProfileId();

        private void TryBuildApiProfileControls()
        {
            if (!_apiSwitchingEnabled || _apiProfileToggle != null || HeaderTitle == null)
            {
                return;
            }

            VisualElement header = HeaderTitle.parent;
            if (header == null)
            {
                return;
            }

            int insertIndex = _agentDropdown != null
                ? header.IndexOf(_agentDropdown) + 1
                : header.IndexOf(HeaderTitle) + 1;

            _apiProfileToggle = new Button(ToggleApiProfileSelector)
            {
                text = "API",
                tooltip = "Choose an API profile for the active agent"
            };
            _apiProfileToggle.AddToClassList("coreai-chat-api-toggle");

            _apiProfileDropdown = new DropdownField();
            _apiProfileDropdown.AddToClassList("coreai-chat-api-dropdown");
            _apiProfileDropdown.RegisterValueChangedCallback(OnApiProfileChanged);

            _apiProfileStatus = new Label();
            _apiProfileStatus.AddToClassList("coreai-chat-api-status");

            header.Insert(insertIndex, _apiProfileToggle);
            header.Insert(insertIndex + 1, _apiProfileDropdown);
            header.Insert(insertIndex + 2, _apiProfileStatus);
            RefreshApiProfileControls();
        }

        private void ToggleApiProfileSelector()
        {
            _apiSelectorExpanded = !_apiSelectorExpanded;
            RefreshApiProfileControls();
        }

        private void OnApiProfileChanged(ChangeEvent<string> evt)
        {
            if (_routingUiController == null || string.IsNullOrWhiteSpace(evt.newValue) ||
                !_profileIdByLabel.TryGetValue(evt.newValue, out string profileId))
            {
                return;
            }

            CoreAiRoutingUiResult result = _routingUiController.AssignProfileToRole(ActiveRoleId, profileId);
            if (!result.Ok)
            {
                Logger.LogWarning(GameLogFeature.Core,
                    "[CoreAiChatPanel] API profile assignment failed: " + result.Message);
                RefreshApiProfileControls();
            }
            else if (string.IsNullOrEmpty(profileId))
            {
                _apiProfileStatus.text = "Automatic routing uses the active agent's configured default.";
            }
        }

        private void HandleRoutingControllerChanged()
        {
            AttachRoutingController(CoreAiRoutingUi.Controller);
            RefreshApiProfileControls();
        }

        private void AttachRoutingController(ICoreAiRoutingUiController controller)
        {
            if (ReferenceEquals(_routingUiController, controller))
            {
                return;
            }

            if (_routingUiController != null)
            {
                _routingUiController.Changed -= RefreshApiProfileControls;
            }

            _routingUiController = controller;
            if (_routingUiController != null)
            {
                _routingUiController.Changed += RefreshApiProfileControls;
            }
        }

        private void RefreshApiProfileControls()
        {
            if (_apiProfileToggle == null || _apiProfileDropdown == null)
            {
                return;
            }

            _profileIdByLabel.Clear();
            _profileIdByLabel[AutomaticApiProfileLabel] = "";
            List<string> labels = new() { AutomaticApiProfileLabel };
            IReadOnlyList<LlmRuntimeProfile> profiles = _routingUiController?.GetProfiles();
            IReadOnlyList<LlmEndpointSnapshot> endpoints = _routingUiController?.GetEndpoints();
            if (profiles != null)
            {
                foreach (LlmRuntimeProfile profile in profiles)
                {
                    if (profile == null || string.IsNullOrWhiteSpace(profile.ProfileId))
                    {
                        continue;
                    }

                    string baseLabel = string.IsNullOrWhiteSpace(profile.DisplayName)
                        ? profile.ProfileId
                        : profile.DisplayName;
                    baseLabel += " — " + EndpointStateLabel(profile.EndpointId, endpoints);
                    string label = baseLabel;
                    int suffix = 2;
                    while (_profileIdByLabel.ContainsKey(label))
                    {
                        label = baseLabel + " (" + suffix++ + ")";
                    }

                    _profileIdByLabel[label] = profile.ProfileId;
                    labels.Add(label);
                }
            }

            _apiProfileDropdown.choices = labels;
            string selectedId = _routingUiController?.GetProfileForRole(ActiveRoleId) ?? "";
            string selectedLabel = FindProfileLabel(selectedId);
            _apiProfileDropdown.SetValueWithoutNotify(selectedLabel);
            _apiProfileDropdown.style.display = _apiSelectorExpanded ? DisplayStyle.Flex : DisplayStyle.None;
            _apiProfileDropdown.SetEnabled(_routingUiController != null);
            _apiProfileToggle.SetEnabled(_routingUiController != null);
            _apiProfileToggle.EnableInClassList("coreai-chat-api-toggle-active", _apiSelectorExpanded);
            _apiProfileToggle.text = profiles == null || profiles.Count == 0 ? "API · Auto" : "API";
            _apiProfileToggle.tooltip = profiles == null || profiles.Count == 0
                ? "No API profiles. Create one in Hub Settings; Automatic/default routing is active."
                : "Choose an API profile for the active agent";
            if (_apiProfileStatus != null)
            {
                _apiProfileStatus.text = profiles == null || profiles.Count == 0
                    ? "No API profiles yet. Create one in Settings."
                    : selectedLabel;
                _apiProfileStatus.style.display = _apiSelectorExpanded ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }

        private string ResolveSelectedProfileId()
        {
            return _apiProfileDropdown != null &&
                   _profileIdByLabel.TryGetValue(_apiProfileDropdown.value ?? "", out string profileId)
                ? profileId
                : "";
        }

        private string FindProfileLabel(string profileId)
        {
            foreach (KeyValuePair<string, string> pair in _profileIdByLabel)
            {
                if (string.Equals(pair.Value, profileId, StringComparison.Ordinal))
                {
                    return pair.Key;
                }
            }

            return AutomaticApiProfileLabel;
        }

        private static string EndpointStateLabel(
            string endpointId,
            IReadOnlyList<LlmEndpointSnapshot> endpoints)
        {
            if (endpoints != null)
            {
                foreach (LlmEndpointSnapshot snapshot in endpoints)
                {
                    if (string.Equals(snapshot?.Descriptor?.EndpointId, endpointId, StringComparison.Ordinal))
                    {
                        return snapshot.State.ToString();
                    }
                }
            }

            return "Unavailable";
        }

        private void TryBuildAgentDropdown()
        {
            if (!_agentSwitchingEnabled || _agentDropdown != null || HeaderTitle == null)
            {
                return;
            }

            VisualElement header = HeaderTitle.parent;
            if (header == null)
            {
                return;
            }

            List<string> roles = new(BuiltInAgentRoleIds.AllBuiltInRoles);
            int index = Mathf.Max(0, roles.IndexOf(_activeRoleId ?? Options?.RoleId ?? BuiltInAgentRoleIds.SmartChat));

            _agentDropdown = new DropdownField(roles, index);
            _agentDropdown.AddToClassList("coreai-chat-agent-dropdown");
            _agentDropdown.RegisterValueChangedCallback(OnAgentDropdownChanged);
            header.Insert(header.IndexOf(HeaderTitle) + 1, _agentDropdown);
        }

        private void OnAgentDropdownChanged(ChangeEvent<string> evt)
        {
            if (string.IsNullOrEmpty(evt.newValue))
            {
                return;
            }

            string previousRole = _activeRoleId;
            _activeRoleId = evt.newValue;

            EnsureCameraToolForActiveRole();

            // WHY: re-target the panel to the newly selected role. Stop any in-flight turn for the
            // *previous* role so its late streaming output does not bleed into the newly selected
            // role's transcript, then reload this role's chat history.
            if (isActiveAndEnabled)
            {
                if (!string.IsNullOrEmpty(previousRole))
                {
                    try
                    {
                        CoreAi.StopAgent(previousRole);
                    }
                    catch (Exception facadeEx)
                    {
                        // WHY: best-effort stop — the facade may have no live scope during teardown;
                        // fall back to the chat service, and never let the dropdown handler throw.
                        try
                        {
                            _chatService?.StopAgent(previousRole);
                        }
                        catch (Exception serviceEx)
                        {
                            Logger.LogWarning(GameLogFeature.Core,
                                $"[CoreAiChatPanel] StopAgent('{previousRole}') failed during agent switch. " +
                                $"CoreAi: {facadeEx.Message}; ChatService: {serviceEx.Message}");
                        }
                    }
                }

                StopActiveGeneration();
                HydrateStartupMessagesFromStore();
            }

            RefreshApiProfileControls();
        }

        protected virtual void BindUI()
        {
            ChatContainer = ResolveChatContainer(Root);
            MessageScroll = Root.Q<ScrollView>("coreai-chat-scroll");
            InputField = Root.Q<TextField>("coreai-chat-input");
            SendButton = Root.Q<Button>("coreai-chat-send");
            ClearButton = Root.Q<Button>("coreai-chat-clear");
            CollapseButton = Root.Q<Button>("coreai-chat-collapse");
            FabButton = Root.Q<Button>("coreai-chat-fab");
            TypingIndicator = Root.Q<VisualElement>("coreai-typing-indicator");
            TypingAvatar = Root.Q<VisualElement>("coreai-typing-avatar");
            TypingLabel = Root.Q<Label>("coreai-typing-label");
            _typingDots = ResolveOrCreateTypingDots();
            HeaderTitle = Root.Q<Label>("coreai-chat-header-title");
            HeaderIcon = Root.Q<VisualElement>("coreai-chat-header-icon");
            _longRequestHint = Root.Q<Label>("coreai-long-request-hint");

            _activeRoleId ??= Options?.RoleId ?? BuiltInAgentRoleIds.SmartChat;
            if (Options != null && Options.AllowAgentSwitching)
            {
                _agentSwitchingEnabled = true;
            }

            TryBuildAgentDropdown();
            TryBuildApiProfileControls();

            if (_longRequestHint != null)
            {
                _longRequestHint.focusable = false;
            }

            if (SendButton != null)
            {
                SendButton.RegisterCallback<ClickEvent>(OnSendClicked);
                SendButton.focusable = false;
            }

            if (ClearButton != null)
            {
                ClearButton.RegisterCallback<ClickEvent>(OnClearClicked);
                ClearButton.focusable = false;
            }

            if (CollapseButton != null)
            {
                CollapseButton.RegisterCallback<ClickEvent>(OnCollapseClicked);
                CollapseButton.focusable = false;
            }

            if (FabButton != null)
            {
                FabButton.RegisterCallback<ClickEvent>(OnFabClicked);
                FabButton.focusable = false;
            }

            if (InputField != null)
            {
                InputField.RegisterCallback<KeyDownEvent>(OnInputKeyDown, TrickleDown.TrickleDown);
            }

            TryBuildExamplesButton();

            Root?.RegisterCallback<KeyDownEvent>(OnRootKeyDown, TrickleDown.TrickleDown);

            if (MessageScroll != null)
            {
                MessageScroll.focusable = false;
            }

            if (HeaderTitle != null)
            {
                HeaderTitle.focusable = false;
            }

            if (HeaderIcon != null)
            {
                HeaderIcon.focusable = false;
            }

            ApplyPanelStateToTree();
            ApplyShortcutTooltips();
        }

        /// <summary>
        /// Pushes every piece of panel state that lives in the visual tree - USS classes and inline
        /// display - onto the elements <see cref="BindUI"/> just resolved.
        /// <para>
        /// The tree is not permanent. <c>PanelRenderer</c> rebuilds it (<c>RegisterUIReloadCallback</c>),
        /// and each rebuild runs <see cref="ResetUiReferences"/> + <see cref="BindUI"/> against brand-new
        /// elements that carry only what the UXML declares. Everything the panel had toggled since the
        /// last build was therefore dropped on the floor: most visibly the collapsed class, so
        /// <see cref="IsCollapsed"/> kept reporting "collapsed" while a fully expanded, history-filled
        /// chat was on screen - and whether it happened at all depended on whether the rebuild landed
        /// before or after <see cref="SetCollapsed"/>.
        /// </para>
        /// <para>
        /// Every state setter and the end of <see cref="BindUI"/> funnel through this one method, so a
        /// third path that applies state without re-applying it after a rebuild cannot appear.
        /// </para>
        /// </summary>
        protected void ApplyPanelStateToTree()
        {
            ChatContainer?.EnableInClassList(CollapsedClassName, IsCollapsed);

            if (FabButton != null && IsElementReadyForStyle(FabButton))
            {
                FabButton.style.display = IsCollapsed ? DisplayStyle.Flex : DisplayStyle.None;
            }

            // WHY: a rebuild in the middle of a turn must not erase the "assistant is answering"
            // affordance - the request keeps running, so the indicator has to come back with the tree.
            if (_typingIndicatorVisible)
            {
                ShowTypingIndicator();
                if (_typingToolProgressHintActive)
                {
                    ApplyStreamingToolProgressTypingHint();
                }
            }
            else
            {
                HideTypingIndicator();
            }

            ApplyClearButtonVisibility();
            UpdateSendButtonVisualState();
        }

        /// <summary>
        /// Adds a compact "Examples" menu button to the input row. Clicking it opens a UITK dropdown of the
        /// built-in <see cref="CoreAiChatExamples"/>; picking one INSERTS its text into the chat input (it is
        /// not auto-sent — the player presses send). Kept small so it does not clutter the default layout.
        /// </summary>
        private void TryBuildExamplesButton()
        {
            // WHY: opt-in (EnableExamplePrompts) so the base/production chat has no example menu — the
            // built-in examples are demo content and should only surface in the demo scenes.
            if (!_examplesEnabled || _examplesButton != null || InputField == null)
            {
                return;
            }

            VisualElement inputContainer = InputField.parent;
            if (inputContainer == null || CoreAiChatExamples.All.Count == 0)
            {
                return;
            }

            _examplesButton = new Button(ShowExamplesMenu)
            {
                // WHY: "≡" reads as a compact menu affordance and is a common glyph in the default font;
                // plain text so no image asset is required.
                text = "≡",
                tooltip = "Insert an example prompt"
            };
            _examplesButton.AddToClassList("coreai-chat-examples-button");
            _examplesButton.focusable = false;
            inputContainer.Insert(0, _examplesButton);
        }

        private void ShowExamplesMenu()
        {
            if (_examplesButton == null)
            {
                return;
            }

            GenericDropdownMenu menu = new();
            foreach (CoreAiChatExample example in CoreAiChatExamples.All)
            {
                CoreAiChatExample captured = example;
                menu.AddItem(captured.Title, false, () => InsertExampleIntoInput(captured));
            }

            menu.DropDown(_examplesButton.worldBound, _examplesButton, false);
        }

        /// <summary>
        /// Puts the example text into the input field without sending it, then focuses the input so the
        /// player can review or edit before pressing send.
        /// </summary>
        private void InsertExampleIntoInput(CoreAiChatExample example)
        {
            if (InputField == null || string.IsNullOrEmpty(example.Message))
            {
                return;
            }

            InputField.value = example.Message;
            InputField.schedule.Execute(FocusInputField);
        }

        private static VisualElement ResolveChatContainer(VisualElement root)
        {
            if (root == null)
            {
                return null;
            }

            return root.name == "coreai-chat-root"
                ? root
                : root.Q<VisualElement>("coreai-chat-root");
        }

        private void UnbindUiCallbacks(bool ignoreReleasedElements)
        {
            TryUnregisterCallback<ClickEvent>(SendButton, OnSendClicked, ignoreReleasedElements);
            TryUnregisterCallback<ClickEvent>(ClearButton, OnClearClicked, ignoreReleasedElements);
            TryUnregisterCallback<ClickEvent>(CollapseButton, OnCollapseClicked, ignoreReleasedElements);
            TryUnregisterCallback<ClickEvent>(FabButton, OnFabClicked, ignoreReleasedElements);
            TryUnregisterCallback<KeyDownEvent>(InputField, OnInputKeyDown, ignoreReleasedElements,
                TrickleDown.TrickleDown);
            TryUnregisterCallback<KeyDownEvent>(Root, OnRootKeyDown, ignoreReleasedElements, TrickleDown.TrickleDown);
        }

        private static void TryUnregisterCallback<TEventType>(
            VisualElement element,
            EventCallback<TEventType> callback,
            bool ignoreReleasedElements,
            TrickleDown useTrickleDown = TrickleDown.NoTrickleDown)
            where TEventType : EventBase<TEventType>, new()
        {
            if (element == null)
            {
                return;
            }

            try
            {
                element.UnregisterCallback(callback, useTrickleDown);
            }
            catch (InvalidOperationException) when (ignoreReleasedElements)
            {
            }
        }

        private void ResetUiReferences()
        {
            // WHY: the dot animation belongs to the tree being dropped; leaving it running would keep
            // flipping a class on detached elements until the scheduler collected them.
            StopTypingAnimation();
            // WHY: embedded trees remain parented across OnDisable. Release before losing the reference so
            // host queries cannot keep seeing a dead stream as active until the next UI bind.
            ReleaseActiveStreamingBubble();
            Root = null;
            ChatContainer = null;
            MessageScroll = null;
            InputField = null;
            SendButton = null;
            ClearButton = null;
            CollapseButton = null;
            FabButton = null;
            TypingIndicator = null;
            TypingAvatar = null;
            TypingLabel = null;
            _typingDots = null;
            HeaderTitle = null;
            HeaderIcon = null;
            _examplesButton = null;
            _agentDropdown = null;
            _apiProfileDropdown = null;
            _apiProfileToggle = null;
            _longRequestHint = null;
            // WHY: those bubbles belonged to the old visual tree; keeping them would hand a host
            // detached elements to post-process.
            _turnStreamingBubbles.Clear();
            // WHY: pending scheduler jobs do NOT die here — they keep firing on the old visual tree until
            // Unity detaches it, which is why every one of them is queued through
            // ScheduleOnMessageScroll and re-checks these fields before touching them. Clearing the flags
            // is only about the next bind: a stuck flag would block every scroll after a UI rebuild.
            _scrollToBottomScheduled = false;
            _streamingScrollScheduled = false;
            _turnAnchorRow = null;
        }

        protected virtual void ApplyConfig()
        {
            ICoreAiChatOptions options = Options;

            if (HeaderTitle != null)
            {
                HeaderTitle.text = options.HeaderTitle;
            }

            if (HeaderIcon != null && config?.AiAvatarIcon != null && IsElementReadyForStyle(HeaderIcon))
            {
                HeaderIcon.style.backgroundImage = Background.FromSprite(config.AiAvatarIcon);
            }

            if (TypingAvatar != null && config?.AiAvatarIcon != null && IsElementReadyForStyle(TypingAvatar))
            {
                TypingAvatar.style.backgroundImage = Background.FromSprite(config.AiAvatarIcon);
            }

            if (ChatContainer != null)
            {
                ApplyResponsiveSize(ChatContainer);
            }

            ApplyStaticControlTexts();
            ApplyClearButtonVisibility();
            ApplyShortcutTooltips();
        }

        private void ApplyStaticControlTexts()
        {
            ICoreAiChatTextOptions textOptions = TextOptions;

            if (ClearButton != null)
            {
                ClearButton.text = TextOrDefault(textOptions.ClearButtonText, CoreAiChatOptions.DefaultClearButtonText);
                ClearButton.tooltip =
                    TextOrDefault(textOptions.ClearButtonTooltip, CoreAiChatOptions.DefaultClearButtonTooltip);
            }

            if (CollapseButton != null)
            {
                CollapseButton.text =
                    TextOrDefault(textOptions.CollapseButtonText, CoreAiChatOptions.DefaultCollapseButtonText);
            }

            UpdateSendButtonVisualState();
        }

        private void ApplyClearButtonVisibility()
        {
            if (ClearButton == null)
            {
                return;
            }

            if (!IsElementReadyForStyle(ClearButton))
            {
                return;
            }

            ClearButton.style.display = Options.ShowClearButton ? DisplayStyle.Flex : DisplayStyle.None;
        }

        /// <summary>
        /// Refreshes button tooltips and FAB glyphs from the effective hotkey settings.
        /// </summary>
        private void ApplyShortcutTooltips()
        {
            if (FabButton != null)
            {
                FabButton.tooltip = IsOpenChatKeyboardShortcutEnabled()
                    ? FormatTemplate(
                        TextOptions.OpenChatWithHotkeyTooltipFormat,
                        CoreAiChatOptions.DefaultOpenChatWithHotkeyTooltipFormat,
                        "{hotkey}",
                        FormatHotkeyForTooltip(ResolvedOpenChatHotkey()))
                    : TextOrDefault(TextOptions.OpenChatTooltip, CoreAiChatOptions.DefaultOpenChatTooltip);
            }

            if (CollapseButton != null)
            {
                CollapseButton.tooltip = IsEscapeChatShortcutEnabled()
                    ? TextOrDefault(TextOptions.CollapseButtonWithEscTooltip,
                        CoreAiChatOptions.DefaultCollapseButtonWithEscTooltip)
                    : TextOrDefault(TextOptions.CollapseButtonTooltip, CoreAiChatOptions.DefaultCollapseButtonTooltip);
            }

            Label fabIcon = Root?.Q<Label>("coreai-chat-fab-icon");
            if (fabIcon != null)
            {
                fabIcon.text = IsOpenChatKeyboardShortcutEnabled()
                    ? FormatHotkeyFabGlyph(ResolvedOpenChatHotkey())
                    : TextOrDefault(TextOptions.FabFallbackText, CoreAiChatOptions.DefaultFabFallbackText);
            }
        }

        private static string TextOrDefault(string text, string fallback)
        {
            return text ?? fallback;
        }

        private static string FormatTemplate(string template, string fallback, string token, string value)
        {
            return TextOrDefault(template, fallback).Replace(token, value ?? string.Empty);
        }

        private bool IsOpenChatKeyboardShortcutEnabled()
        {
            if (_runtimeOverrideOpenChatShortcutEnabled.HasValue)
            {
                return _runtimeOverrideOpenChatShortcutEnabled.Value;
            }

            return Options.EnableOpenChatKeyboardShortcut;
        }

        private bool IsEscapeChatShortcutEnabled()
        {
            if (_runtimeOverrideEscapeChatShortcuts.HasValue)
            {
                return _runtimeOverrideEscapeChatShortcuts.Value;
            }

            return Options.EnableEscapeChatShortcuts;
        }

        private bool IsChatRequiresVisibleCursorEnabled()
        {
            if (_runtimeOverrideChatRequiresVisibleCursor.HasValue)
            {
                return _runtimeOverrideChatRequiresVisibleCursor.Value;
            }

            return Options.ChatRequiresVisibleCursor;
        }

        /// <summary>
        /// Whether chat hotkeys (open + Escape) should react right now, given cursor visibility gating.
        /// Pure so EditMode tests can cover every lock-mode/visibility combination without a live cursor.
        /// </summary>
        internal static bool IsChatInputAllowed(bool requiresVisibleCursor, bool cursorVisible, CursorLockMode lockMode)
        {
            return !requiresVisibleCursor || (cursorVisible && lockMode != CursorLockMode.Locked);
        }

        private bool IsChatInputAllowedNow()
        {
            return IsChatInputAllowed(
                IsChatRequiresVisibleCursorEnabled(), UnityEngine.Cursor.visible, UnityEngine.Cursor.lockState);
        }

        /// <summary>
        /// Detects the allowed → blocked cursor-gating transition (cursor just got locked/hidden) and
        /// releases chat keyboard focus so movement keys never type into the chat input. Does not
        /// auto-refocus on the reverse transition — the player has to click/press the open hotkey again.
        /// </summary>
        private void TickChatInputCursorGating()
        {
            bool allowedNow = IsChatInputAllowedNow();
            if (_wasChatInputAllowed == true && !allowedNow)
            {
                ReleaseChatKeyboardFocus();
            }

            _wasChatInputAllowed = allowedNow;
        }

        private KeyCode ResolvedOpenChatHotkey()
        {
            if (_runtimeOverrideOpenChatHotkey.HasValue)
            {
                return _runtimeOverrideOpenChatHotkey.Value;
            }

            return config != null ? config.OpenChatHotkey : KeyCode.C;
        }

        private static string FormatHotkeyForTooltip(KeyCode key)
        {
            if (key >= KeyCode.A && key <= KeyCode.Z)
            {
                return ((char)('A' + (key - KeyCode.A))).ToString();
            }

            return key.ToString();
        }

        private static string FormatHotkeyFabGlyph(KeyCode key)
        {
            if (key >= KeyCode.A && key <= KeyCode.Z)
            {
                return ((char)('A' + (key - KeyCode.A))).ToString();
            }

            return key == KeyCode.None ? "..." : key.ToString().Length <= 3 ? key.ToString() : "...";
        }

        /// <summary>
        /// Applies desktop, mobile, or fullscreen sizing constraints to the chat container.
        /// </summary>
        private void ApplyResponsiveSize(VisualElement container)
        {
            if (!IsElementReadyForStyle(container) || IsWorldSpacePanelRenderer())
            {
                return;
            }

            ICoreAiChatOptions options = Options;
            float configuredWidth = options.ChatWidth;
            float configuredHeight = options.ChatHeight;

            float screenWidth = Mathf.Max(1f, Screen.width);
            float screenHeight = Mathf.Max(1f, Screen.height);

            const float margin = 12f;
            float maxWidth = Mathf.Max(280f, screenWidth - margin * 2f);
            float maxHeight = Mathf.Max(320f, screenHeight - margin * 2f);

            bool useStretchLayout = options.UseFullscreenChat || IsMobileScreen();

            if (useStretchLayout)
            {
                if (options.UseFullscreenChat)
                {
                    container.AddToClassList(FullscreenLayoutClassName);
                }
                else
                {
                    container.RemoveFromClassList(FullscreenLayoutClassName);
                }

                if (IsMobileScreen())
                {
                    container.AddToClassList(MobileClassName);
                }
                else
                {
                    container.RemoveFromClassList(MobileClassName);
                }

                container.style.left = margin;
                container.style.right = margin;
                container.style.top = margin;
                container.style.bottom = margin;
                container.style.width = StyleKeyword.Auto;
                container.style.height = StyleKeyword.Auto;
                return;
            }

            container.RemoveFromClassList(FullscreenLayoutClassName);
            container.RemoveFromClassList(MobileClassName);

            container.style.left = StyleKeyword.Auto;
            container.style.top = StyleKeyword.Auto;
            container.style.right = 24f;
            container.style.bottom = 24f;
            container.style.width = Mathf.Min(configuredWidth, maxWidth);
            container.style.height = Mathf.Min(configuredHeight, maxHeight);
        }

        private static bool IsMobileScreen()
        {
            float w = Mathf.Max(1f, Screen.width);
            float h = Mathf.Max(1f, Screen.height);
            return w <= 720f || h <= 560f;
        }

        private void OnClearClicked(ClickEvent _)
        {
            ClearChat();
        }

        private void OnCollapseClicked(ClickEvent _)
        {
            SetCollapsed(true, true);
        }

        private void OnFabClicked(ClickEvent _)
        {
            SetCollapsed(false, true);
        }

        /// <summary>
        /// Switches the panel between expanded chat mode and the collapsed floating action button.
        /// </summary>
        public void SetCollapsed(bool collapsed, bool persist = true)
        {
            bool changed = IsCollapsed != collapsed;
            IsCollapsed = collapsed;

            ApplyPanelStateToTree();

            if (persist)
            {
                PlayerPrefs.SetInt(CollapsedPrefsKey, collapsed ? 1 : 0);
            }

            if (collapsed)
            {
                ReleaseChatKeyboardFocus();
                Root?.schedule.Execute(ReleaseChatKeyboardFocus);
            }
            else
            {
                InputField?.schedule.Execute(FocusInputField);
            }

            if (changed)
            {
                OnCollapsedStateChanged(collapsed);
            }
        }

        /// <summary>
        /// Called after the panel changes between expanded chat and collapsed FAB modes.
        /// </summary>
        /// <param name="collapsed">Whether the panel is now collapsed.</param>
        protected virtual void OnCollapsedStateChanged(bool collapsed)
        {
        }


        /// <summary>Resolved open-chat keyboard shortcut state after runtime overrides.</summary>
        public bool EffectiveOpenChatKeyboardShortcutEnabled => IsOpenChatKeyboardShortcutEnabled();

        /// <summary>Resolved open-chat hotkey after runtime overrides.</summary>
        public KeyCode EffectiveOpenChatHotkey => ResolvedOpenChatHotkey();

        /// <summary>Resolved escape-shortcut state after runtime overrides.</summary>
        public bool EffectiveEscapeChatShortcutsEnabled => IsEscapeChatShortcutEnabled();

        /// <summary>Resolved cursor-gating state after runtime overrides.</summary>
        public bool EffectiveChatRequiresVisibleCursor => IsChatRequiresVisibleCursorEnabled();

        /// <summary>
        /// Overrides whether the runtime open-chat keyboard shortcut is enabled.
        /// </summary>
        public void SetRuntimeOpenChatKeyboardShortcutEnabled(bool? enabled)
        {
            _runtimeOverrideOpenChatShortcutEnabled = enabled;
            ApplyShortcutTooltips();
        }

        /// <summary>
        /// Overrides the runtime hotkey used to open chat.
        /// </summary>
        public void SetRuntimeOpenChatHotkey(KeyCode? hotkey)
        {
            _runtimeOverrideOpenChatHotkey = hotkey;
            ApplyShortcutTooltips();
        }

        /// <summary>
        /// Overrides whether Escape can blur or collapse the chat panel.
        /// </summary>
        public void SetRuntimeEscapeChatShortcutsEnabled(bool? enabled)
        {
            _runtimeOverrideEscapeChatShortcuts = enabled;
            ApplyShortcutTooltips();
        }

        /// <summary>
        /// Overrides whether chat hotkeys require a visible, unlocked mouse cursor (see
        /// <see cref="ICoreAiChatOptions.ChatRequiresVisibleCursor"/>).
        /// </summary>
        public void SetRuntimeChatRequiresVisibleCursor(bool? requiresVisibleCursor)
        {
            _runtimeOverrideChatRequiresVisibleCursor = requiresVisibleCursor;
        }

        /// <summary>Clears runtime hotkey overrides so the panel uses configuration defaults again.</summary>
        public void ClearRuntimeHotkeyOverrides()
        {
            _runtimeOverrideOpenChatShortcutEnabled = null;
            _runtimeOverrideOpenChatHotkey = null;
            _runtimeOverrideEscapeChatShortcuts = null;
            _runtimeOverrideChatRequiresVisibleCursor = null;
            ApplyShortcutTooltips();
        }

        protected virtual void InitService()
        {
            TryResolveActorIdentityProviderFromScene();

            _chatService = CoreAiChatService.TryCreateFromScene();
            if (_chatService == null)
            {
                Logger.LogWarning(GameLogFeature.Core,
                    "[CoreAiChatPanel] CoreAiChatService not available (no CoreAILifetimeScope on scene?).");
                return;
            }

            EnsureCameraToolForActiveRole();
        }

        private bool TryResolveActorIdentityProviderFromScene()
        {
            CoreAILifetimeScope lifetimeScope =
                UnityEngine.Object.FindAnyObjectByType<CoreAILifetimeScope>(FindObjectsInactive.Include);
            if (lifetimeScope?.Container == null)
            {
                return false;
            }

            try
            {
                SetActorIdentityProvider(
                    (IActorIdentityProvider)lifetimeScope.Container.Resolve(typeof(IActorIdentityProvider)));
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(GameLogFeature.Core,
                    $"[CoreAiChatPanel] Resolve IActorIdentityProvider: {ex.Message}");
                return false;
            }
        }

        private IActorIdentityProvider ResolveActorIdentityProvider()
        {
            if (_actorIdentityProvider == null)
            {
                TryResolveActorIdentityProviderFromScene();
            }

            _actorIdentityProvider ??= CoreServicesInstaller.DefaultLocalHostIdentityProvider;
            return _actorIdentityProvider;
        }

        /// <summary>
        /// Gives the active chat agent the runtime camera tool when <see cref="ICoreAiChatOptions.EnableCameraTool"/>
        /// is on. No-op when the service or agent-vision wiring is absent (silent degrade for text-only /
        /// headless scenes). Called on service init and whenever the agent is switched.
        /// </summary>
        private void EnsureCameraToolForActiveRole()
        {
            if (_chatService == null || !Options.EnableCameraTool)
            {
                return;
            }

            _chatService.TryEnsureCameraToolForRole(ActiveRoleId, true);
        }

        /// <summary>
        /// Rebuilds the visible startup transcript from persisted chat history or the welcome message.
        /// </summary>
        protected virtual void HydrateStartupMessagesFromStore()
        {
            if (MessageScroll == null)
            {
                return;
            }

            MessageScroll.Clear();

            TryAppendPersistedChatHistoryFromStore();
            if (GetMessageScrollChildCount() > 0)
            {
                ScrollToBottom();
                return;
            }

            // WHY: the store may be unavailable or per-role opted out of LoadPersistedChatOnStartup — the
            // in-memory cache still has this role's live session dialogue from before the last switch.
            if (TryRestoreRoleTranscriptFromCache(ActiveRoleId))
            {
                ScrollToBottom();
                return;
            }

            ICoreAiChatOptions options = Options;
            if (string.IsNullOrEmpty(options.WelcomeMessage))
            {
                return;
            }

            // WHY: render-only — the welcome placeholder is not real conversation, so it must not be
            // recorded into the per-role transcript cache.
            AppendMessageBubble(options.WelcomeMessage, false);
        }

        /// <summary>
        /// Attempts to append persisted chat history from store and returns whether the operation succeeded.
        /// </summary>
        protected virtual void TryAppendPersistedChatHistoryFromStore()
        {
            if (MessageScroll == null)
            {
                return;
            }

            ICoreAiChatOptions options = Options;
            if (!options.LoadPersistedChatOnStartup)
            {
                return;
            }

            if (_chatService == null)
            {
                return;
            }

            string roleId = ActiveRoleId;
            int max = options.MaxPersistedMessagesForUi;
            bool ok = _chatService.TryGetPersistedChatHistory(roleId, out ChatMessage[] history, max);
            if (!ok)
            {
                return;
            }

            foreach (ChatMessage msg in history)
            {
                if (!ShouldRenderPersistedMessageForUi(msg, options.ShowToolCallsInChat))
                {
                    continue;
                }

                bool isUser = string.Equals(msg.Role, "user", StringComparison.OrdinalIgnoreCase);
                string text = FormatPersistedMessageForUi(msg.Content ?? "", isUser);
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                // WHY: render-only — persisted messages must NOT feed the per-role cache, or every
                // re-hydrate on a role switch re-appends the whole store into the cache (N -> 2N -> 3N).
                AppendMessageBubble(text.TrimEnd(), isUser);
            }
        }

        internal static bool ShouldRenderPersistedMessageForUi(ChatMessage message, bool showToolCallsInChat)
        {
            string role = message.Role?.Trim();
            if (string.Equals(role, "user", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase))
            {
                return showToolCallsInChat || !IsToolLifecycleNotification(message.Content);
            }

            return false;
        }

        internal static string FormatPersistedMessageForUi(string content, bool isUser)
        {
            if (!isUser || string.IsNullOrWhiteSpace(content))
            {
                return content ?? "";
            }

            string trimmed = content.TrimStart();
            if (!trimmed.StartsWith("{", StringComparison.Ordinal))
            {
                return content;
            }

            try
            {
                string hint = JObject.Parse(trimmed)["hint"]?.ToString();
                return string.IsNullOrWhiteSpace(hint) ? content : hint;
            }
            catch (Exception ex)
            {
                // WHY: user text that merely starts with '{' is not guaranteed to be composer JSON;
                // rendering the raw content is the correct fallback, but never fail silently.
                Logger.LogDebug(GameLogFeature.Core,
                    $"[CoreAiChatPanel] Persisted message hint parse failed; rendering raw content: {ex.Message}");
                return content;
            }
        }

        /// <summary>Number of visual children currently hosted by the message scroll view.</summary>
        protected int GetMessageScrollChildCount()
        {
            if (MessageScroll == null)
            {
                return 0;
            }

            VisualElement content = MessageScroll.contentContainer;
            return content != null ? content.childCount : MessageScroll.childCount;
        }

        private static void MarkInputEventHandled(EventBase evt)
        {
            evt.StopImmediatePropagation();

            if (evt.target is VisualElement targetElement && targetElement.focusController != null)
            {
                targetElement.focusController.IgnoreEvent(evt);
                return;
            }

            if (evt.currentTarget is VisualElement currentElement && currentElement.focusController != null)
            {
                currentElement.focusController.IgnoreEvent(evt);
            }
        }

        private void OnSendClicked(ClickEvent evt)
        {
            MarkInputEventHandled(evt);

            try
            {
                TrySendInput(true);
            }
            catch (Exception ex)
            {
                Logger.LogError(GameLogFeature.Core,
                    $"[CoreAiChatPanel] Send/stop button handler failed: {ex}");
                ResetBusyStateWithoutCancellation();
            }
        }

        private void OnInputKeyDown(KeyDownEvent evt)
        {
            bool isEnter =
                evt.keyCode == KeyCode.Return ||
                evt.keyCode == KeyCode.KeypadEnter ||
                evt.character == '\n' ||
                evt.character == '\r';

            if (!isEnter)
            {
                return;
            }

            bool sendOnShiftEnter = Options.SendOnShiftEnter;
            bool shouldSend = ShouldSubmitOnEnter(sendOnShiftEnter, evt.shiftKey);

            if (!shouldSend)
            {
                return;
            }

            MarkInputEventHandled(evt);

            TrySendInput(false);
        }

        private void OnRootKeyDown(KeyDownEvent evt)
        {
            if (!IsChatInputAllowedNow())
            {
                return;
            }

            if (IsCollapsed && IsOpenChatKeyboardShortcutEnabled() &&
                IsOpenChatHotkeyFromKeys(ResolvedOpenChatHotkey(), evt.keyCode, evt.character, evt.ctrlKey,
                    evt.commandKey, evt.altKey))
            {
                MarkInputEventHandled(evt);
                SetCollapsed(false, true);
                return;
            }

            if (!IsCollapsed && IsEscapeChatShortcutEnabled() && IsEscape(evt))
            {
                MarkInputEventHandled(evt);
                if (IsRequestInProgress)
                {
                    if (Options.EnableStopGeneration)
                    {
                        StopActiveGeneration();
                    }
                }
                else
                {
                    SetCollapsed(true, true);
                }
            }
        }

        /// <summary>Returns whether the supplied key state matches the configured open-chat shortcut.</summary>
        internal static bool IsOpenChatHotkeyFromKeys(
            KeyCode openHotkey,
            KeyCode keyCode,
            char character,
            bool ctrlHeld,
            bool commandHeld,
            bool altHeld)
        {
            if (ctrlHeld || commandHeld || altHeld)
            {
                return false;
            }

            if (openHotkey == KeyCode.None)
            {
                return false;
            }

            if (keyCode == openHotkey)
            {
                return true;
            }

            if (openHotkey >= KeyCode.A && openHotkey <= KeyCode.Z)
            {
                char expectedLower = (char)('a' + (openHotkey - KeyCode.A));
                return char.ToLowerInvariant(character) == expectedLower;
            }

            return false;
        }

        /// <summary>
        /// After a few seconds of visible assistant output in this turn, shows the configured hint under the typing row.
        /// </summary>
        private void TickLongRequestHint()
        {
            if (_longRequestHint == null || !IsElementReadyForStyle(_longRequestHint))
            {
                return;
            }

            bool busy = IsLongRequestHintBusy();
            if (!busy)
            {
                ResetLongRequestHint();
                return;
            }

            if (float.IsNaN(_longRequestHintArmedSince))
            {
                _longRequestHintArmedSince = Time.realtimeSinceStartup;
            }

            float elapsed = Time.realtimeSinceStartup - _longRequestHintArmedSince;
            if (elapsed < LongRequestHintMinSeconds)
            {
                _longRequestHint.style.display = DisplayStyle.None;
                return;
            }

            string tpl = Options.LongRequestHintFormat;
            if (string.IsNullOrWhiteSpace(tpl))
            {
                ResetLongRequestHint();
                return;
            }

            int sec = Mathf.Max((int)LongRequestHintMinSeconds, Mathf.FloorToInt(elapsed));
            _longRequestHint.text = tpl.Replace("{elapsed}", sec.ToString());
            _longRequestHint.style.display = DisplayStyle.Flex;
        }

        /// <summary>
        /// Long-hint while the panel is busy on an LLM turn (typing, streaming, or non-stream wait).
        /// The timer starts when the request is sent, not after the first streamed character,
        /// because fast replies often never reach the hint threshold.
        /// </summary>
        private bool IsLongRequestHintBusy()
        {
            return _isSending && !_isStreaming;
        }

        private void ResetLongRequestHint()
        {
            _longRequestHintArmedSince = float.NaN;
            if (_longRequestHint == null || !IsElementReadyForStyle(_longRequestHint))
            {
                return;
            }

            _longRequestHint.style.display = DisplayStyle.None;
            _longRequestHint.text = string.Empty;
        }

        /// <summary>
        /// Handles keyboard shortcuts for opening, collapsing, and stopping the chat panel.
        /// </summary>
        private void PollChatToggleShortcuts()
        {
            try
            {
                if (!IsChatInputAllowedNow())
                {
                    return;
                }

                bool noUitkKeyboardFocus =
                    Root == null ||
                    Root.focusController == null ||
                    Root.focusController.focusedElement == null;

                if (!noUitkKeyboardFocus)
                {
                    return;
                }

                if (IsCollapsed)
                {
                    if (IsOpenChatKeyboardShortcutEnabled() &&
                        Input.GetKeyDown(ResolvedOpenChatHotkey()) &&
                        !Input.GetKey(KeyCode.LeftControl) &&
                        !Input.GetKey(KeyCode.RightControl) &&
                        !Input.GetKey(KeyCode.LeftCommand) &&
                        !Input.GetKey(KeyCode.RightCommand) &&
                        !Input.GetKey(KeyCode.LeftAlt) &&
                        !Input.GetKey(KeyCode.RightAlt))
                    {
                        SetCollapsed(false, true);
                    }
                }
                else if (IsEscapeChatShortcutEnabled())
                {
                    if (Input.GetKeyDown(KeyCode.Escape))
                    {
                        if (IsRequestInProgress)
                        {
                            if (Options.EnableStopGeneration)
                            {
                                StopActiveGeneration();
                            }
                        }
                        else
                        {
                            SetCollapsed(true, true);
                        }
                    }
                }
            }
            catch (InvalidOperationException ex)
            {
                // WHY: legacy Input can throw during editor teardown / input-system switches; polling
                // resumes next frame, but keep a debug trace instead of swallowing silently.
                Logger.LogDebug(GameLogFeature.Core,
                    $"[CoreAiChatPanel] PollChatToggleShortcuts skipped this frame: {ex.Message}");
            }
        }

        private static bool IsEscape(KeyDownEvent evt)
        {
            return IsEscapeKey(evt.keyCode, evt.character);
        }

        internal static bool IsEscapeKey(KeyCode keyCode, char character)
        {
            return keyCode == KeyCode.Escape || character == 27;
        }

        /// <summary>
        /// Whether an AI request/stream is currently in progress for this panel. `_isSending` covers the
        /// whole agent turn (RunAgentTurnAsync `finally`); `_isStreaming` stays true until the streaming
        /// enumerator fully completes (LLM chunks + orchestrator post-work) — do not clear it on
        /// `chunk.IsDone` alone. Public so host UI (e.g. the CoreAI Hub) can decide what Escape does
        /// without duplicating the panel's busy bookkeeping.
        /// </summary>
        public bool IsRequestInProgress => _isSending || _isStreaming;

        /// <summary>Effective "Enable Stop Generation" setting after config/runtime options.</summary>
        public bool EffectiveEnableStopGeneration => Options.EnableStopGeneration;

        /// <summary>
        /// Fires <see cref="BusyStateChanged"/> whenever <see cref="IsBusy"/> transitions.
        /// Call right after any mutation of <c>_isSending</c>/<c>_isStreaming</c>/<c>_isStopping</c>/<c>_isClearing</c>.
        /// </summary>
        private void RaiseBusyStateChangedIfChanged()
        {
            bool current = IsBusy;
            if (current == _lastPublishedBusy)
            {
                return;
            }

            _lastPublishedBusy = current;
            try
            {
                BusyStateChanged?.Invoke(current);
            }
            catch (Exception ex)
            {
                Logger.LogError(GameLogFeature.Core, $"[CoreAiChatPanel] BusyStateChanged handler error: {ex}");
            }
        }

        /// <summary>
        /// Resets busy state without cancellation to its default state.
        /// </summary>
        /// <remarks>
        /// WHEN TO USE THIS vs <see cref="AbandonCurrentTurn"/>: this method only clears the four busy
        /// flags and the typing/streaming UI — it does NOT move <see cref="_currentTurnGeneration"/> and
        /// does NOT cancel the in-flight request. Use it when the turn itself is already finished or
        /// already being torn down by its own code path (e.g. a host reacting to
        /// <see cref="BusyStateChanged"/> after the turn's own <c>finally</c> already ran) and only the
        /// UI affordance needs a nudge.
        /// <para>
        /// Do NOT use this alone as a "the host gave up waiting" watchdog action: the turn keeps running
        /// underneath, <see cref="IsStaleTurn"/> still reports it as current, and when it eventually
        /// completes or fails it will append its own transcript/error bubble on top of whatever the host
        /// already told the player — a second, redundant message for one failure. For that case call
        /// <see cref="AbandonCurrentTurn"/> instead, which moves the turn generation AND cancels the
        /// request AND resets busy state (by calling this method) in one safe operation.
        /// </para>
        /// </remarks>
        public void ResetBusyStateWithoutCancellation()
        {
            HideTypingIndicator();
            _isSending = false;
            _isStreaming = false;
            _isStopping = false;
            _isClearing = false;
            FinishStreaming();
            UpdateSendButtonVisualState();
            RaiseBusyStateChangedIfChanged();
        }

        /// <summary>
        /// Lets a caller-owned watchdog (e.g. a host's own request timeout, independent of this panel's
        /// internal cancellation) honestly give up on the current turn instead of only hiding the busy
        /// UI while the turn keeps running underneath.
        /// </summary>
        /// <remarks>
        /// WHY this exists: a host may run its own timeout shorter than the actual HTTP timeout (e.g. it
        /// warns the player "teacher didn't answer" after 60s against a 160s HTTP timeout) and then calls
        /// <see cref="ResetBusyStateWithoutCancellation"/> to unlock its UI. That alone does not move
        /// <see cref="_currentTurnGeneration"/> and does not cancel the request, so when the real call
        /// later completes or fails, <see cref="IsStaleTurn"/> still reports the turn as CURRENT and its
        /// own error handling appends a second, redundant bubble on top of the message the host already
        /// showed. This method fixes that at the source instead of asking every such host to suppress its
        /// own second message:
        /// <list type="number">
        /// <item>Bumps <see cref="_currentTurnGeneration"/> the same way the start of a turn does (see
        /// <see cref="RunAgentTurnAsync"/>). The in-flight turn becomes stale by construction, so every
        /// <see cref="IsStaleTurn"/> check already inside it stops touching the transcript/busy state on
        /// its own — nothing is suppressed after the fact, the turn is honestly marked abandoned.</item>
        /// <item>Cancels the in-flight request the same way <see cref="StopActiveGeneration"/> does
        /// (same active-request-CTS handling, same <c>CoreAi.StopAgent</c> / <see cref="_chatService"/>
        /// fallback, same root-CTS replace): keeping the call alive would only burn provider tokens for a
        /// result nobody will read, since it is now guaranteed to be discarded as stale.</item>
        /// <item>Resets busy state via <see cref="ResetBusyStateWithoutCancellation"/> so the UI is
        /// immediately usable again.</item>
        /// </list>
        /// WHY main-thread only: like <see cref="GetOrCreateCancellationTokenSource"/> (see its own
        /// remarks on the same race), this method cancels, disposes, and replaces
        /// <see cref="_activeRequestCts"/> / <see cref="_cts"/>. Calling it off the main thread risks the
        /// same use-after-dispose race as calling that mutator off the main thread — call it from the
        /// main/Unity thread only.
        /// </remarks>
        /// <returns>
        /// <c>true</c> if a turn was in flight and got abandoned; <c>false</c> if there was nothing to
        /// abandon (nothing was cancelled, but busy state is still safely reset).
        /// </returns>
        public bool AbandonCurrentTurn()
        {
            bool wasInProgress = IsRequestInProgress;

            // WHY: bump first and unconditionally. Every IsStaleTurn check the (possibly still unwinding)
            // in-flight turn runs from this point on compares against a generation that has already moved
            // on, so it stops touching the transcript/busy state under its own logic — this is what makes
            // the "second error message" defect impossible rather than merely suppressed.
            Interlocked.Increment(ref _currentTurnGeneration);

            string roleId = ActiveRoleId;
            CancellationTokenSource activeRequestCts = _activeRequestCts;

            // WHY: same facade/service fallback chain as StopActiveGeneration - the CoreAi facade may have
            // no live scope (teardown/tests), so fall back to the chat service rather than leaving the
            // orchestrator scope's queued work running for this role.
            try
            {
                CoreAi.StopAgent(roleId);
            }
            catch (Exception coreAiEx)
            {
                try
                {
                    _chatService?.StopAgent(roleId);
                }
                catch (Exception chatServiceEx)
                {
                    Logger.LogWarning(GameLogFeature.Core,
                        $"[CoreAiChatPanel] AbandonCurrentTurn: StopAgent fallback failed. CoreAi: {coreAiEx.Message}; ChatService: {chatServiceEx.Message}");
                }
            }

            // WHY: cancel the active HTTP/streaming request directly, exactly like StopActiveGeneration.
            // On WebGL, cancellation callbacks can surface browser/JS-side exceptions; abandon must never
            // throw back into the caller (typically a host's Update loop / watchdog tick).
            if (IsCancellationSourceActive(activeRequestCts))
            {
                try
                {
                    activeRequestCts.Cancel();
                }
                catch (Exception cancelEx)
                {
                    Logger.LogWarning(GameLogFeature.Core,
                        $"[CoreAiChatPanel] AbandonCurrentTurn: request cancel failed: {cancelEx.Message}");
                }
            }

            if (ReferenceEquals(_activeRequestCts, activeRequestCts))
            {
                _activeRequestCts = null;
            }

            CancelAndReplaceRootCancellationSource("AbandonCurrentTurn");

            ResetBusyStateWithoutCancellation();

            return wasInProgress;
        }

        private void RaiseToolRoundStarted(int iteration, string lastToolName)
        {
            try
            {
                ToolRoundStarted?.Invoke(iteration, lastToolName);
            }
            catch (Exception ex)
            {
                Logger.LogError(GameLogFeature.Core, $"[CoreAiChatPanel] ToolRoundStarted handler error: {ex}");
            }
        }

        private bool IsActionInProgress()
        {
            return IsChatInputLocked(_isSending, _isStreaming, _isStopping, _isClearing);
        }

        private bool CanStartAgentTurn()
        {
            return _lifecycleActive;
        }

        private bool OwnsActiveTurn(int turnGeneration, CancellationTokenSource requestCts)
        {
            return CanStartAgentTurn() &&
                   !IsStaleTurn(turnGeneration, _currentTurnGeneration) &&
                   ReferenceEquals(_activeRequestCts, requestCts) &&
                   IsCancellationSourceActive(requestCts);
        }

        internal static bool ShouldSubmitOnEnter(bool sendOnShiftEnter, bool shiftHeld)
        {
            return sendOnShiftEnter ? shiftHeld : !shiftHeld;
        }

        internal static bool IsChatInputLocked(bool isSending, bool isStreaming, bool isStopping, bool isClearing)
        {
            return isSending || isStreaming || isStopping || isClearing;
        }

        private void TrySendInput(bool stopIfBusy)
        {
            if (IsRequestInProgress)
            {
                if (stopIfBusy && Options.EnableStopGeneration)
                {
                    StopActiveGeneration();
                }

                return;
            }

            if (IsActionInProgress())
            {
                return;
            }

            // WHY: even if the button is disabled, TextField key events can still fire — never send
            // while an AI request/stream is in progress.
            if (IsActionInProgress() || (SendButton != null && !SendButton.enabledSelf))
            {
                return;
            }

            if (InputField == null || string.IsNullOrWhiteSpace(InputField.text))
            {
                return;
            }

            string text = InputField.text.Trim();

            int maxMessageLength = Options.MaxMessageLength;
            if (maxMessageLength > 0 && text.Length > maxMessageLength)
            {
                text = text.Substring(0, maxMessageLength);
            }

            InputField.value = string.Empty;
            InputField.schedule.Execute(FocusInputField);

            text = OnMessageSending(text);
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            AddMessage(text, true);
            OnUserMessageSent?.Invoke(text);

            SendToAI(text);
        }

        /// <summary>
        /// Submits a message from gameplay/test code through the same turn pipeline used by the UI.
        /// </summary>
        /// <returns>
        /// Final assistant text, simulated text, or <c>null</c> when the panel is busy, canceled, or given empty input.
        /// </returns>
        public async Task<string?> SubmitMessageFromExternalAsync(
            string messageText,
            CoreAiChatExternalSubmitOptions options = null,
            CancellationToken cancellationToken = default)
        {
            options ??= new CoreAiChatExternalSubmitOptions();

            if (!CanStartAgentTurn())
            {
                Logger.LogWarning(GameLogFeature.Core,
                    "[CoreAiChatPanel] SubmitMessageFromExternalAsync: ignored (panel inactive).");
                return null;
            }

            if (IsActionInProgress())
            {
                Logger.LogWarning(GameLogFeature.Core,
                    "[CoreAiChatPanel] SubmitMessageFromExternalAsync: ignored (chat busy).");
                return null;
            }

            if (string.IsNullOrWhiteSpace(messageText))
            {
                return null;
            }

            string text = messageText.Trim();
            int maxMessageLength = Options.MaxMessageLength;
            if (maxMessageLength > 0 && text.Length > maxMessageLength)
            {
                text = text.Substring(0, maxMessageLength);
            }

            text = OnMessageSending(text);
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            if (options.AppendUserMessageToChat)
            {
                AddMessage(text, true);
                OnUserMessageSent?.Invoke(text);
            }

            using CancellationTokenSource linked =
                CancellationTokenSource.CreateLinkedTokenSource(GetOrCreateCancellationTokenSource().Token,
                    cancellationToken);
            return await RunAgentTurnAsync(text, options.SimulatedAssistantReply, linked.Token);
        }

        private CancellationTokenSource GetOrCreateCancellationTokenSource()
        {
            if (_cts == null || _cts.IsCancellationRequested)
            {
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = new CancellationTokenSource();
            }

            return _cts;
        }

        /// <summary>Submits the user text to the AI chat service from the panel UI.</summary>
        private void SendToAI(string userText)
        {
            _ = SendToAIFromUiAsync(userText);
        }

        private async Task SendToAIFromUiAsync(string userText)
        {
            try
            {
                await RunAgentTurnAsync(userText, null, GetOrCreateCancellationTokenSource().Token);
            }
            catch (OperationCanceledException)
            {
                // WHY: user stop/cancel is handled inside RunAgentTurnAsync when possible. Keep this
                // fire-and-forget entry point from surfacing an unobserved task exception on WebGL.
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogError(GameLogFeature.Core, $"[CoreAiChatPanel] SendToAI: {ex}");
            }
        }

        /// <summary>
        /// Runs one full agent turn (streaming or buffered) and returns the final assistant text, or
        /// <c>null</c> on cancel/error. A non-empty <paramref name="simulatedAssistantReply"/> bypasses
        /// the AI service and renders the given reply directly (test/demo path).
        /// </summary>
        private async Task<string?> RunAgentTurnAsync(
            string userTextForModel,
            string simulatedAssistantReply,
            CancellationToken cancellationToken)
        {
            if (!CanStartAgentTurn())
            {
                Logger.LogWarning(GameLogFeature.Core,
                    "[CoreAiChatPanel] RunAgentTurnAsync: ignored (panel inactive).");
                return null;
            }

            if (!string.IsNullOrWhiteSpace(simulatedAssistantReply))
            {
                ResetLongRequestHint();
                string raw = simulatedAssistantReply.Trim();
                string stripped = StripThinkBlocks(raw);
                string formatted = FormatResponseText(string.IsNullOrEmpty(stripped) ? raw : stripped);
                AddMessage(formatted, false);
                OnResponseReceived(formatted);
                OnAiResponseCompleted?.Invoke(formatted);
                return formatted;
            }

            if (_chatService == null)
            {
                _isSending = false;
                ResetLongRequestHint();
                UpdateSendButtonVisualState();
                AddMessage(Options.ErrorMessagePrefix + "AI service is not connected.", false);
                return null;
            }

            string roleId = _activeRoleId ?? Options.RoleId ?? BuiltInAgentRoleIds.SmartChat;
            // WHY: capture this turn's generation so code after awaits can detect "a newer turn is
            // already in flight" and drop stale UI appends/finishes (see IsStaleTurn).
            int turnGeneration = Interlocked.Increment(ref _currentTurnGeneration);
            _toolRoundIterationInTurn = 1;
            _stopRequestedByUser = false;
            CancellationTokenSource requestCts =
                CancellationTokenSource.CreateLinkedTokenSource(GetOrCreateCancellationTokenSource().Token,
                    cancellationToken);
            _activeRequestCts = requestCts;

            try
            {
                if (!OwnsActiveTurn(turnGeneration, requestCts))
                {
                    return null;
                }

                if (!_isSending)
                {
                    // WHY: generation and the request CTS are installed before publishing busy=true.
                    // A synchronous handler may StopAgent; the ownership check immediately below then
                    // prevents this turn from ever entering the provider.
                    _isSending = true;
                    UpdateSendButtonVisualState();
                }

                if (!OwnsActiveTurn(turnGeneration, requestCts))
                {
                    return null;
                }

                ResetLongRequestHint();
                _nonStreamAssistantOutputStarted = false;
                _streamingStartedVisible = false;
                AiTaskRequest request = BuildAiTaskRequest(userTextForModel, roleId);
                bool uiStreaming = Options.EnableStreaming;
                bool useStreaming = ShouldUseStreamingForRole(roleId, uiStreaming) &&
                                    _chatService.IsStreamingEnabled(roleId, uiStreaming);

                if (useStreaming)
                {
                    return await SendStreamingAsync(request, turnGeneration, requestCts.Token);
                }

                return await SendNonStreamingAsync(request, turnGeneration, requestCts.Token);
            }
            catch (OperationCanceledException)
            {
                await CoreAiWebGlUiThreadMarshaling.SwitchToMainThreadForUiOptional(CancellationToken.None);
                if (IsStaleTurn(turnGeneration, _currentTurnGeneration))
                {
                    return null;
                }

                FinishStreaming();
                HideTypingIndicator();
                ResetLongRequestHint();
                if (!_stopRequestedByUser)
                {
                    string bubble = ResolveTimeoutMessage(false);
                    if (!string.IsNullOrEmpty(bubble))
                    {
                        AddMessage(bubble, false);
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                await CoreAiWebGlUiThreadMarshaling.SwitchToMainThreadForUiOptional(CancellationToken.None);
                // WHY: two audiences, two strings. The log keeps EVERYTHING (typed code, HTTP status,
                // retry hint, raw provider body, stack trace); the transcript gets one readable
                // sentence. Pasting `ex.Message` into the bubble used to show players
                // `HTTP error 403: {"error":{...}}` — noise to them, and the body never reached the log.
                Logger.LogError(
                    GameLogFeature.Core,
                    $"[CoreAiChatPanel] Error: {LlmErrorPresentation.ToDiagnosticText(ex)} | {ex}");
                if (!IsStaleTurn(turnGeneration, _currentTurnGeneration))
                {
                    FinishStreaming();
                    AddMessage(Options.ErrorMessagePrefix + ResolveErrorMessage(ex), false);
                }

                return null;
            }
            finally
            {
                try
                {
                    await CoreAiWebGlUiThreadMarshaling.SwitchToMainThreadForUiOptional(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(GameLogFeature.Core,
                        $"[CoreAiChatPanel] RunAgentTurnAsync finally: SwitchToMainThread: {ex.Message}");
                }

                // WHY: when a newer turn superseded this one, its busy flags/streaming bubble/typing row
                // belong to that turn — a stale unwind must only release its own cancellation source.
                if (!IsStaleTurn(turnGeneration, _currentTurnGeneration))
                {
                    FinishStreaming();
                    HideTypingIndicator();
                    _isSending = false;
                    _stopRequestedByUser = false;
                    ResetLongRequestHint();
                    _lastToolNameInTurn = null;
                }

                if (ReferenceEquals(_activeRequestCts, requestCts))
                {
                    _activeRequestCts = null;
                }

                requestCts.Dispose();
                if (!IsStaleTurn(turnGeneration, _currentTurnGeneration))
                {
                    UpdateSendButtonVisualState();
                    InputField?.schedule.Execute(FocusInputField);
                }
            }
        }

        /// <summary>
        /// Controls whether automatic focus-steering to the chat input field is active.
        /// Returns <c>true</c> by default so screen-space chat behaviour is unchanged. Override and
        /// return <c>false</c> in world-space / gaze UIs where stealing keyboard focus after every
        /// AI turn (or message send / panel expand) is harmful.
        /// </summary>
        protected virtual bool AutoFocusInputFieldEnabled => true;

        /// <summary>
        /// Moves keyboard focus back to the chat input field when the panel is active.
        /// No-op when <see cref="AutoFocusInputFieldEnabled"/> is <c>false</c>.
        /// </summary>
        private void FocusInputField()
        {
            if (!AutoFocusInputFieldEnabled)
            {
                return;
            }

            InputField?.Focus();
        }

        private void ReleaseChatKeyboardFocus()
        {
            Root?.panel?.focusController?.focusedElement?.Blur();
        }

        /// <summary>
        /// Build the <see cref="AiTaskRequest"/> for the next chat message. Default returns
        /// a minimal request (RoleId + Hint + SourceTag="Chat"). Override in subclasses to
        /// inject extra fields, e.g. <see cref="AiTaskRequest.ForcedToolMode"/> for
        /// deterministic tool-calling driven by an intent classifier.
        /// </summary>
        protected virtual AiTaskRequest BuildAiTaskRequest(string userText, string roleId)
        {
            IActorIdentityProvider actorIdentityProvider = ResolveActorIdentityProvider();
            ActorContext actorContext = actorIdentityProvider.GetActorContext(roleId);
            return new AiTaskRequest
            {
                RoleId = roleId,
                RoutingProfileId = ResolveSelectedProfileId(),
                Hint = userText,
                SourceTag = "Chat",
                ActorContext = actorContext,
                CancellationScope = actorContext.SessionId
            };
        }

        /// <summary>
        /// Override in a subclass to add extra streaming gates per role. Default returns the chat UI flag only;
        /// WebGL incremental SSE, global settings, and per-role overrides are enforced in
        /// <see cref="CoreAiChatService"/>.
        /// This keeps panel-level policy separate from runtime settings when
        /// <see cref="CoreAISettingsAsset.Instance"/> differs from the DI-registered <see cref="ICoreAISettings"/> asset).
        /// </summary>
        protected virtual bool ShouldUseStreamingForRole(string roleId, bool uiConfigWantsStreaming)
        {
            _ = roleId;
            return uiConfigWantsStreaming;
        }

        private async Task<string?> SendStreamingAsync(
            AiTaskRequest request,
            int turnGeneration,
            CancellationToken ct)
        {
            ShowTypingIndicator();
            ResetThinkFilter();
            _streamingStartedVisible = false;
            _streamingBubbleSealed = false;
            // WHY: bubbles of the PREVIOUS turn must not leak into this one — a host reading
            // TurnStreamingBubbles would re-render already finished bubbles with the new answer.
            _turnStreamingBubbles.Clear();
            _turnAnchorRow = null;

            // WHY: yield so the UI thread can repaint (stop affordance) before ultra-fast stubs finish
            // the enumerator.
            await Task.Yield();
            if (IsStaleTurn(turnGeneration, _currentTurnGeneration))
            {
                return null;
            }

            _isStreaming = true;
            UpdateSendButtonVisualState();

            string fullResponse = "";
            DateTime lastChunkAt = DateTime.UtcNow;
            // WHY: the bubbles THIS turn opened, tracked locally because _turnStreamingBubbles is reset by
            // whichever turn starts next — and an abandoned turn unwinds after that reset.
            List<Label> ownStreamingBubbles = new();
            try
            {
                await foreach (LlmStreamChunk chunk in _chatService.SendMessageStreamingAsync(request, ct))
                {
                    // WHY: stream-gap diagnostic: helps tell "model is slow" from "UI lost a chunk".
                    TimeSpan gap = DateTime.UtcNow - lastChunkAt;
                    if (gap.TotalSeconds > StreamGapWarnSeconds)
                    {
                        Logger.LogWarning(GameLogFeature.Core,
                            $"[CoreAiChatPanel] Stream gap {gap.TotalSeconds:F1}s before chunk: " +
                            $"BufferedNoToolBinding={chunk.BufferedStreamingNoToolBinding}, " +
                            $"ToolHint={chunk.BufferedStreamingUseToolProgressHint}, " +
                            $"TextLen={chunk.Text?.Length ?? 0}, IsDone={chunk.IsDone}");
                    }

                    lastChunkAt = DateTime.UtcNow;

                    // WHY: LLM/orchestrator stack uses ConfigureAwait(false); UITK must be touched on
                    // the main thread.
                    await CoreAiWebGlUiThreadMarshaling.SwitchToMainThreadForUiOptional(ct);

                    // WHY: a newer turn may have started while this one awaited (agent switch / stop +
                    // resend); stop touching the UI — the new turn owns the transcript and busy state.
                    if (IsStaleTurn(turnGeneration, _currentTurnGeneration))
                    {
                        return null;
                    }

                    if (!string.IsNullOrEmpty(chunk.Error))
                    {
                        if (_stopRequestedByUser &&
                            string.Equals(chunk.Error, "cancelled", StringComparison.OrdinalIgnoreCase))
                        {
                            return null;
                        }

                        Logger.LogError(GameLogFeature.Core, $"[CoreAiChatPanel] Stream error: {chunk.Error}");
                        AddMessage(Options.ErrorMessagePrefix + ResolveStreamErrorMessage(chunk.Error), false);
                        return null;
                    }

                    if (chunk.BufferedStreamingNoToolBinding)
                    {
                        // WHY: heuristic for tool-round boundary: the orchestrator only emits a tool-progress hint
                        // mid-stream when an LLM round just produced tool calls and a new round is about to
                        // start. If prose has already streamed in this turn (`_streamingStartedVisible`),
                        // want to show "tool X (k/N)" badges without reflection.
                        if (chunk.BufferedStreamingUseToolProgressHint && _streamingStartedVisible)
                        {
                            _toolRoundIterationInTurn++;
                            RaiseToolRoundStarted(_toolRoundIterationInTurn, _lastToolNameInTurn);

                            // WHY: close the in-flight prose bubble at the tool-round boundary so the assistant
                            // answer after the tools lands in its own bubble below the tool-call bubbles
                            // (matches claude/cursor behaviour) instead of being appended to the bubble that
                            // was opened before the tools (which would leave tools below the answer).
                            SealStreamingBubbleIfAny();
                        }

                        if (chunk.BufferedStreamingUseToolProgressHint)
                        {
                            // WHY: mid-turn prose may already be streaming (typing row was hidden); show typing again.
                            if (_streamingStartedVisible)
                            {
                                ShowTypingIndicator();
                            }

                            ApplyStreamingToolProgressTypingHint();
                        }
                        else
                        {
                            // WHY: restart the dot animation that ApplyStreamingToolProgressTypingHint
                            // paused (StopTypingAnimation), even before any prose has streamed.
                            ShowTypingIndicator();
                        }

                        continue;
                    }

                    if (!string.IsNullOrEmpty(chunk.Text))
                    {
                        string visible = FilterStreamChunk(chunk.Text);
                        if (fullResponse.Length == 0)
                        {
                            visible = NormalizeAssistantDisplayText(visible);
                        }

                        if (!string.IsNullOrEmpty(visible))
                        {
                            // Явная граница из клиента: началась СЛЕДУЮЩАЯ реплика того же потока.
                            // Раньше границу ловила только эвристика по tool-progress подсказке
                            // (см. BufferedStreamingUseToolProgressHint выше), а её нет на нативном
                            // tool-calling — вторая реплика дописывалась в конец первой, и ученик
                            // читал слипшееся «Проверь себя:**Ход завершён…**» одним пузырём.
                            if (chunk.StartsNewMessage)
                            {
                                SealStreamingBubbleIfAny();
                            }

                            if (!_streamingStartedVisible || _streamingBubbleSealed)
                            {
                                _streamingStartedVisible = true;
                                Label opened = StartStreaming();
                                if (opened != null)
                                {
                                    ownStreamingBubbles.Add(opened);
                                }
                            }

                            // WHY: fullResponse keeps the complete text for history/handlers; only the
                            // rendered streaming label is capped (see AppendToStreaming).
                            string formatted = FormatResponseText(visible);
                            // Пузыри разъехались, но fullResponse уходит в историю и обработчикам
                            // одной строкой — там граница обязана остаться пустой строкой, иначе
                            // склейка вернётся при следующем показе той же истории.
                            fullResponse = AppendStreamedMessage(fullResponse, formatted, chunk.StartsNewMessage);
                            AppendToStreaming(formatted, turnGeneration);
                        }
                    }
                }

                await CoreAiWebGlUiThreadMarshaling.SwitchToMainThreadForUiOptional(ct);
                if (IsStaleTurn(turnGeneration, _currentTurnGeneration))
                {
                    return null;
                }

                if (string.IsNullOrEmpty(fullResponse))
                {
                    AddMessage(Options.NoResponseMessage ?? "No response.", false);
                    return null;
                }

                // WHY: the streamed reply renders through the streaming label, not AddMessage, so record it
                // into the per-role transcript cache here — otherwise switching agent and back restores the
                // user's turns with no assistant answers whenever the store is off/opted-out (HIGH #2).
                RecordRoleTranscriptMessage(ActiveRoleId, fullResponse, false);
                OnResponseReceived(fullResponse);
                OnAiResponseCompleted?.Invoke(fullResponse);
                return fullResponse;
            }
            finally
            {
                try
                {
                    await CoreAiWebGlUiThreadMarshaling.SwitchToMainThreadForUiOptional(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(GameLogFeature.Core,
                        $"[CoreAiChatPanel] SendStreamingAsync finally: SwitchToMainThread: {ex.Message}");
                }

                if (!IsStaleTurn(turnGeneration, _currentTurnGeneration))
                {
                    FinishStreaming();
                    HideTypingIndicator();
                    UpdateSendButtonVisualState();
                    ScrollToTurnAnchor();
                }
                else
                {
                    SealAbandonedTurnBubbles(ownStreamingBubbles);
                }
            }
        }

        private async Task<string?> SendNonStreamingAsync(
            AiTaskRequest request,
            int turnGeneration,
            CancellationToken ct)
        {
            ShowTypingIndicator();
            _nonStreamAssistantOutputStarted = false;

            try
            {
                string response = await _chatService.SendMessageAsync(request, ct);
                await CoreAiWebGlUiThreadMarshaling.SwitchToMainThreadForUiOptional(CancellationToken.None);
                if (IsStaleTurn(turnGeneration, _currentTurnGeneration))
                {
                    return null;
                }

                HideTypingIndicator();

                if (string.IsNullOrEmpty(response))
                {
                    AddMessage(Options.NoResponseMessage ?? "No response.", false);
                    return null;
                }

                response = StripThinkBlocks(response);
                string formatted = FormatResponseText(response);
                if (string.IsNullOrEmpty(formatted))
                {
                    AddMessage(Options.NoResponseMessage ?? "No response.", false);
                    return null;
                }

                _nonStreamAssistantOutputStarted = true;
                AddMessage(formatted, false);
                OnResponseReceived(formatted);
                OnAiResponseCompleted?.Invoke(formatted);
                return formatted;
            }
            finally
            {
                try
                {
                    await CoreAiWebGlUiThreadMarshaling.SwitchToMainThreadForUiOptional(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(GameLogFeature.Core,
                        $"[CoreAiChatPanel] SendNonStreamingAsync finally: UI thread hop: {ex.Message}");
                }

                if (!IsStaleTurn(turnGeneration, _currentTurnGeneration))
                {
                    HideTypingIndicator();
                    ResetLongRequestHint();
                }
            }
        }

        /// <summary>Resets state used to remove hidden think blocks from model output.</summary>
        private void ResetThinkFilter()
        {
            _thinkFilter.Reset();
        }

        /// <summary>
        /// Removes hidden thought text from one streamed model chunk.
        /// </summary>
        private string FilterStreamChunk(string chunk)
        {
            return _thinkFilter.ProcessChunk(chunk);
        }

        /// <summary>Removes hidden think blocks from a complete model response string.</summary>
        private static string StripThinkBlocks(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }

            return System.Text.RegularExpressions.Regex.Replace(
                text, @"<think>[\s\S]*?</think>\s*", "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
        }

        /// <summary>
        /// Hook for subclasses to validate, rewrite, or cancel outgoing user text.
        /// </summary>
        protected virtual string OnMessageSending(string text)
        {
            return text;
        }

        /// <summary>
        /// Hook called after a full assistant response has been received.
        /// </summary>
        protected virtual void OnResponseReceived(string fullResponse)
        {
        }

        /// <summary>
        /// Builds assistant bubble text when <see cref="RunAgentTurnAsync"/> ends with
        /// <see cref="OperationCanceledException"/> (explicit user stop vs timeout / cancel).
        /// Return <c>null</c> or empty to skip <see cref="AddMessage"/> when the host already posted diagnostics.
        /// </summary>
        /// <param name="stopRequestedByUser">True when the user pressed stop.</param>
        protected virtual string ResolveTimeoutMessage(bool stopRequestedByUser)
        {
            if (stopRequestedByUser)
            {
                return null;
            }

            return Options.TimeoutMessage ?? "Timeout.";
        }

        /// <summary>
        /// Player-facing text for a failed turn (shown after <see cref="CoreAiChatConfig.ErrorMessagePrefix"/>).
        ///
        /// Default: a message authored by the backend for the player wins; otherwise a phrase for the
        /// typed <see cref="LlmErrorCode"/> — see <see cref="LlmErrorPresentation"/>. The technical
        /// detail (status, provider body, stack) is logged separately, so overriding this never hides
        /// diagnostics. Override to localize or to react to a specific error code.
        /// </summary>
        protected virtual string ResolveErrorMessage(Exception exception)
        {
            return LlmErrorPresentation.ToUserMessage(exception);
        }

        /// <summary>
        /// Player-facing text for an error reported inside a stream chunk (<see cref="LlmStreamChunk.Error"/>),
        /// where there is no exception to inspect — transport noise like the <c>HTTP error 500:</c>
        /// prefix is stripped, the rest is shown as-is.
        /// </summary>
        protected virtual string ResolveStreamErrorMessage(string chunkError)
        {
            string stripped = LlmErrorPresentation.StripHttpErrorPrefix(chunkError);
            return string.IsNullOrWhiteSpace(stripped) ? LlmErrorPresentation.DefaultUserMessage : stripped;
        }

        /// <summary>
        /// Formats assistant text before it is displayed in the chat transcript.
        /// </summary>
        protected virtual string FormatResponseText(string rawText)
        {
            return rawText;
        }

        /// <summary>
        /// Formats an executed tool call for optional display inside the chat transcript.
        /// </summary>
        protected virtual string FormatToolExecutedForChat(
            string roleId,
            string toolName,
            IDictionary<string, object?>? arguments,
            object? result)
        {
            _ = roleId;
            return CoreAiToolCallChatFormatter.BuildDisplayText(toolName, arguments, result);
        }

        internal static string NormalizeAssistantDisplayText(string text)
        {
            return string.IsNullOrEmpty(text) ? text : text.TrimStart();
        }

        /// <summary>
        /// Max characters of assistant text rendered into a single bubble. A backstop against the model
        /// occasionally emitting a very long dump (a real incident leaked ~16 000 chars of reasoning).
        /// </summary>
        internal const int MaxAssistantRenderChars = 4000;

        private const string CodeFence = "```";

        /// <summary>
        /// WebGL safety cap. Rendering one oversized message into a single bubble overflows UI Toolkit's
        /// GPU vertex/index buffer in WebGL (<c>GfxDevice::CopyBufferRanges: range reads out of bounds</c>
        /// → <c>memory access out of bounds</c> → app crash). Assistant text is hard-capped here as a
        /// package-level backstop (a host may also cap earlier). RENDER-ONLY: the full message still lives
        /// in chat memory/history; only what is drawn is bounded. Pure string logic — safe on Unity 6.0+.
        /// </summary>
        internal static string ClampAssistantForRender(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= MaxAssistantRenderChars)
            {
                return text;
            }

            string clipped = text.Substring(0, MaxAssistantRenderChars);

            // WHY: a truncation inside a ``` block leaves the fence open and breaks markdown layout; close it.
            int fences = 0;
            for (int i = clipped.IndexOf(CodeFence, StringComparison.Ordinal);
                 i >= 0;
                 i = clipped.IndexOf(CodeFence, i + CodeFence.Length, StringComparison.Ordinal))
            {
                fences++;
            }

            if ((fences & 1) == 1)
            {
                clipped += "\n" + CodeFence;
            }

            return clipped + "\n\n…";
        }

        /// <summary>
        /// Whether a turn that captured <paramref name="turnGeneration"/> at its start has been
        /// superseded by a newer turn. Stale turns must not mutate transcript or busy state.
        /// </summary>
        internal static bool IsStaleTurn(int turnGeneration, int currentTurnGeneration)
        {
            return turnGeneration != currentTurnGeneration;
        }

        /// <summary>
        /// Render-only append for one streamed chunk. Below <see cref="MaxAssistantRenderChars"/> the
        /// chunk is appended verbatim; the append that crosses the cap seals the bubble via
        /// <see cref="ClampAssistantForRender"/> (clamped, open code fence closed, ellipsis marker) and
        /// sets <paramref name="cappedAtLimit"/> so callers stop rendering later chunks. The full
        /// response text is accumulated separately and still reaches history/persistence untouched.
        /// </summary>
        internal static string AppendStreamingChunkForRender(
            string renderedText,
            string chunk,
            out bool cappedAtLimit)
        {
            renderedText ??= string.Empty;
            if (renderedText.Length >= MaxAssistantRenderChars)
            {
                cappedAtLimit = true;
                return renderedText;
            }

            if (string.IsNullOrEmpty(chunk))
            {
                cappedAtLimit = false;
                return renderedText;
            }

            string combined = renderedText + chunk;
            if (combined.Length <= MaxAssistantRenderChars)
            {
                cappedAtLimit = false;
                return combined;
            }

            cappedAtLimit = true;
            return ClampAssistantForRender(combined);
        }

        /// <summary>
        /// Creates message bubble.
        /// </summary>
        protected virtual VisualElement CreateMessageBubble(string text, bool isUser)
        {
            VisualElement row = CreateMessageBubbleRow(isUser);
            VisualElement avatar = row.Q<VisualElement>("coreai-message-avatar");
            VisualElement contentSlot = row.Q<VisualElement>("coreai-message-content-slot");

            if (!isUser)
            {
                Label bubble = new(NormalizeAssistantDisplayText(text));
                bubble.style.whiteSpace = WhiteSpace.Normal;
                bubble.AddToClassList("coreai-chat-message");
                bubble.AddToClassList("coreai-ai-message");
                MakeTextSelectable(bubble);

                ApplyAvatarSprite(avatar);
                AddBubbleContent(row, contentSlot, bubble);
            }
            else
            {
                avatar?.RemoveFromHierarchy();

                Label bubble = new(text);
                bubble.style.whiteSpace = WhiteSpace.Normal;
                bubble.AddToClassList("coreai-chat-message");
                bubble.AddToClassList("coreai-user-message");
                MakeTextSelectable(bubble);

                AddBubbleContent(row, contentSlot, bubble);
            }

            return row;
        }

        /// <summary>
        /// Lets the user highlight and copy (Ctrl/Cmd+C) part of a chat message. UI Toolkit
        /// <see cref="Label"/>s are not selectable by default, so message bubbles could not be copied.
        /// </summary>
        private static void MakeTextSelectable(Label label)
        {
            label.selection.isSelectable = true;
            label.selection.doubleClickSelectsWord = true;
            label.selection.tripleClickSelectsLine = true;
            label.focusable = true; // WHY: required so Ctrl/Cmd+C copies the active selection
        }

        /// <summary>
        /// Builds the bare message row (template clone or the built-in
        /// <see cref="CoreAiChatMessageBubbleElement"/>) with its side classes applied and no content.
        /// Exposed to subclasses so a host that only replaces the BUBBLE CONTENT (markdown, custom
        /// widgets) does not have to copy the row scaffolding — override
        /// <see cref="CreateMessageBubble"/>, call this, then fill <c>coreai-message-content-slot</c>.
        /// </summary>
        /// <param name="isUser">True for a user row, false for an assistant row.</param>
        protected VisualElement CreateMessageBubbleRow(bool isUser)
        {
            VisualElement row = null;
            if (messageBubbleTemplate != null)
            {
                TemplateContainer container = messageBubbleTemplate.CloneTree();
                row = container.Q<VisualElement>("coreai-message-row");
                if (row != null)
                {
                    row.RemoveFromHierarchy();
                }
            }

            if (row == null)
            {
                row = new CoreAiChatMessageBubbleElement();
            }

            row.RemoveFromClassList("coreai-user-row");
            row.RemoveFromClassList("coreai-ai-row");
            row.AddToClassList("coreai-message-row");
            row.AddToClassList(isUser ? "coreai-user-row" : "coreai-ai-row");

            // WHY: matched by INTERFACE, not by the concrete package element — a host whose bubble
            // template instantiates its own side-aware element still gets IsUser applied here.
            if (row is ICoreAiChatMessageBubble bubble)
            {
                bubble.IsUser = isUser;
            }

            return row;
        }

        private void ApplyAvatarSprite(VisualElement avatar)
        {
            if (avatar == null || config?.AiAvatarIcon == null)
            {
                return;
            }

            avatar.style.backgroundImage = Background.FromSprite(config.AiAvatarIcon);
        }

        private static void AddBubbleContent(VisualElement row, VisualElement contentSlot, VisualElement content)
        {
            if (contentSlot == null)
            {
                row.Add(content);
                return;
            }

            contentSlot.Clear();
            contentSlot.Add(content);
        }

        public void AddMessage(string text, bool isUser)
        {
            if (MessageScroll == null)
            {
                return;
            }

            if (!isUser && !Options.ShowToolCallsInChat && IsToolLifecycleNotification(text))
            {
                return;
            }

            AppendMessageBubble(text, isUser);
            RecordRoleTranscriptMessage(ActiveRoleId, text, isUser);
        }

        /// <summary>
        /// Renders one message bubble. Split out of <see cref="AddMessage"/> so per-role cache restore
        /// (<see cref="TryRestoreRoleTranscriptFromCache"/>) can re-render already-recorded, already-filtered
        /// text without appending duplicate entries back into <see cref="_roleTranscriptCache"/>.
        /// </summary>
        /// <remarks>
        /// WHY: the WebGL GPU-buffer backstop lives here because this is the single render entry point for
        /// message bubbles. Clamping only in <see cref="AddMessage"/> left two paths uncapped: the streaming
        /// reply records the full response straight into the per-role cache, and persisted history is
        /// rehydrated directly — so restoring either could still overflow the vertex buffer and crash WebGL.
        /// Render-only: the per-role cache and chat history keep the untruncated text.
        /// </remarks>
        private void AppendMessageBubble(string text, bool isUser)
        {
            HideTypingIndicator();

            // WHY: user input is bounded by the input field; only assistant text can be arbitrarily large.
            if (!isUser)
            {
                text = ClampAssistantForRender(text);
            }

            VisualElement bubble = CreateMessageBubble(text, isUser);
            RememberReaderPositionBeforeAppend();
            MessageScroll.Add(bubble);
            if (isUser)
            {
                // WHY: the learner's own message always lands at the bottom; the answer that follows
                // starts a fresh anchor. Sending is also a deliberate return to the bottom, so the
                // reader is following again — whatever they were reading before, they left it.
                _turnAnchorRow = null;
                _readerFollowsBottom = true;
                ScrollToBottom();
            }
            else
            {
                ScrollForNewAssistantRow(bubble);
            }
        }

        private void RecordRoleTranscriptMessage(string roleId, string text, bool isUser)
        {
            if (string.IsNullOrEmpty(roleId))
            {
                return;
            }

            if (!_roleTranscriptCache.TryGetValue(roleId, out List<(string Text, bool IsUser)> messages))
            {
                messages = new List<(string Text, bool IsUser)>();
                _roleTranscriptCache[roleId] = messages;
            }

            messages.Add((text, isUser));
        }

        /// <summary>
        /// Re-renders <paramref name="roleId"/>'s cached transcript (if any) into the already-cleared
        /// <see cref="MessageScroll"/>. Returns false when there is nothing cached, so callers can fall
        /// back to the welcome message.
        /// </summary>
        private bool TryRestoreRoleTranscriptFromCache(string roleId)
        {
            if (MessageScroll == null || string.IsNullOrEmpty(roleId))
            {
                return false;
            }

            if (!_roleTranscriptCache.TryGetValue(roleId, out List<(string Text, bool IsUser)> messages) ||
                messages.Count == 0)
            {
                return false;
            }

            foreach ((string text, bool isUser) in messages)
            {
                AppendMessageBubble(text, isUser);
            }

            return true;
        }

        private static bool IsToolLifecycleNotification(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            string value = text.TrimStart();
            return value.StartsWith("Tool call completed:", StringComparison.OrdinalIgnoreCase) ||
                   value.StartsWith("Tool calls completed:", StringComparison.OrdinalIgnoreCase) ||
                   value.StartsWith("Tool call failed:", StringComparison.OrdinalIgnoreCase) ||
                   value.StartsWith("Tool calls failed:", StringComparison.OrdinalIgnoreCase) ||
                   value.StartsWith("Tool call started:", StringComparison.OrdinalIgnoreCase) ||
                   value.StartsWith("Tool calls started:", StringComparison.OrdinalIgnoreCase) ||
                   value.StartsWith("[Tool]", StringComparison.OrdinalIgnoreCase);
        }

        private void TryRegisterToolCallChatDisplay()
        {
            TryUnregisterToolCallChatDisplay();
            // WHY: always subscribe — the handler also tracks `_lastToolNameInTurn` for the
            // ToolRoundStarted public event, independent of `ShowToolCallsInChat`. The bubble-rendering
            // branch inside the handler is still gated by runtime options.
            _toolExecutedChatHandler = OnToolExecutedChatDisplay;
            CoreAi.OnToolExecuted += _toolExecutedChatHandler;
        }

        private void TryUnregisterToolCallChatDisplay()
        {
            if (_toolExecutedChatHandler == null)
            {
                return;
            }

            CoreAi.OnToolExecuted -= _toolExecutedChatHandler;
            _toolExecutedChatHandler = null;
        }

        private void OnToolExecutedChatDisplay(
            string roleId,
            string toolName,
            IDictionary<string, object?>? arguments,
            object? result)
        {
            ICoreAiChatOptions options = Options;
            string panelRole = ActiveRoleId;
            // WHY: ToolRoundStarted listeners want the name regardless of whether the bubble is rendered.
            if (string.Equals(roleId, panelRole, StringComparison.Ordinal))
            {
                _lastToolNameInTurn = toolName;
            }

            if (!options.ShowToolCallsInChat)
            {
                return;
            }

            if (!string.Equals(roleId, panelRole, StringComparison.Ordinal))
            {
                return;
            }

            UniTask.Void(async () =>
            {
                try
                {
                    // WHY: CoreAi.OnToolExecuted fires on a threadpool thread and
                    // GetOrCreateCancellationTokenSource() is a mutator (Cancel + Dispose + replace), so
                    // calling it here raced the Stop button into an ObjectDisposedException or a lost
                    // cancellation. Hop to the main thread first; the checks below already drop the bubble
                    // when the panel went away.
                    await CoreAiWebGlUiThreadMarshaling.SwitchToMainThreadForUiOptional(CancellationToken.None);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (this == null || !isActiveAndEnabled || MessageScroll == null)
                {
                    return;
                }

                string line = FormatToolExecutedForChat(roleId, toolName, arguments, result);
                if (string.IsNullOrWhiteSpace(line))
                {
                    return;
                }

                AppendToolCallBubble(line);
            });
        }

        private void AppendToolCallBubble(string text)
        {
            if (MessageScroll == null)
            {
                return;
            }

            HideTypingIndicator();

            VisualElement row = new();
            row.AddToClassList("coreai-message-row");
            row.AddToClassList("coreai-tool-call-row");

            Label label = new(text.Trim());
            label.style.whiteSpace = WhiteSpace.Normal;
            label.AddToClassList("coreai-chat-message");
            label.AddToClassList("coreai-tool-call-message");

            row.Add(label);
            RememberReaderPositionBeforeAppend();
            MessageScroll.Add(row);
            // WHY: a tool-call line is assistant content like any other, so it obeys the same scroll
            // policy — an unconditional jump to the bottom here would take the view away from a reader
            // who is deliberately looking further up.
            ScrollForNewAssistantRow(row);
        }

        public void SetSendEnabled(bool enabled)
        {
            if (SendButton != null)
            {
                SendButton.SetEnabled(enabled);
            }
        }

        private void StopActiveGeneration()
        {
            if (_isStopping || !IsRequestInProgress)
            {
                return;
            }

            // WHY: Stop transfers turn ownership before any cancellation/busy callbacks. A reentrant
            // successor started by BusyStateChanged(false) receives a newer generation and cannot be
            // mistaken for the turn being stopped.
            Interlocked.Increment(ref _currentTurnGeneration);
            _isStopping = true;
            ResetLongRequestHint();
            UpdateControlButtonsState();
            UpdateSendButtonVisualState();
            try
            {
                _stopRequestedByUser = true;
                string roleId = ActiveRoleId;
                CancellationTokenSource activeRequestCts = _activeRequestCts;

                try
                {
                    CoreAi.StopAgent(roleId);
                }
                catch (Exception coreAiEx)
                {
                    try
                    {
                        _chatService?.StopAgent(roleId);
                    }
                    catch (Exception chatServiceEx)
                    {
                        Logger.LogWarning(GameLogFeature.Core,
                            $"[CoreAiChatPanel] StopAgent fallback failed. CoreAi: {coreAiEx.Message}; ChatService: {chatServiceEx.Message}");
                    }
                }

                // WHY: cancel the active HTTP/streaming request. On WebGL, cancellation callbacks can
                // surface browser/JS-side exceptions; stop must never throw back into the UI loop.
                if (IsCancellationSourceActive(activeRequestCts))
                {
                    try
                    {
                        activeRequestCts.Cancel();
                        if (ReferenceEquals(_activeRequestCts, activeRequestCts))
                        {
                            _activeRequestCts = null;
                        }
                    }
                    catch (Exception cancelEx)
                    {
                        if (ReferenceEquals(_activeRequestCts, activeRequestCts))
                        {
                            _activeRequestCts = null;
                        }

                        Logger.LogWarning(GameLogFeature.Core,
                            $"[CoreAiChatPanel] StopActiveGeneration: request cancel failed: {cancelEx.Message}");
                    }
                }
                else if (ReferenceEquals(_activeRequestCts, activeRequestCts))
                {
                    _activeRequestCts = null;
                }

                CancelAndReplaceRootCancellationSource("StopActiveGeneration");
                FinishStreaming();
                HideTypingIndicator();
                _isSending = false;
            }
            finally
            {
                _isStopping = false;
                UpdateControlButtonsState();
                UpdateSendButtonVisualState();
            }
        }

        private static bool IsCancellationSourceActive(CancellationTokenSource source)
        {
            if (source == null)
            {
                return false;
            }

            try
            {
                _ = source.Token;
                return !source.IsCancellationRequested;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        private void CancelAndReplaceRootCancellationSource(string context)
        {
            CancellationTokenSource root = _cts;
            if (root != null)
            {
                try
                {
                    root.Cancel();
                }
                catch (Exception cancelEx)
                {
                    Logger.LogWarning(GameLogFeature.Core,
                        $"[CoreAiChatPanel] {context}: root cancel failed: {cancelEx.Message}");
                }

                try
                {
                    root.Dispose();
                }
                catch (Exception disposeEx)
                {
                    Logger.LogWarning(GameLogFeature.Core,
                        $"[CoreAiChatPanel] {context}: root dispose failed: {disposeEx.Message}");
                }
            }

            if (ReferenceEquals(_cts, root) || _cts == null || IsCancellationRequested(_cts))
            {
                _cts = new CancellationTokenSource();
            }
        }

        private static bool IsCancellationRequested(CancellationTokenSource source)
        {
            if (source == null)
            {
                return false;
            }

            try
            {
                return source.IsCancellationRequested;
            }
            catch (ObjectDisposedException)
            {
                return true;
            }
        }

        private void UpdateControlButtonsState()
        {
            bool actionInProgress = IsActionInProgress();
            if (ClearButton != null)
            {
                ClearButton.SetEnabled(!actionInProgress);
            }
        }

        private void UpdateSendButtonVisualState()
        {
            // WHY: single funnel for busy-state changes — every flag mutation in the panel already
            // calls UpdateSendButtonVisualState() to refresh the send/stop affordance, so emitting the
            // public BusyStateChanged event here keeps the contract consistent without sprinkling
            // RaiseBusyStateChangedIfChanged() everywhere.
            RaiseBusyStateChangedIfChanged();

            if (SendButton == null)
            {
                return;
            }

            bool isBusy = IsRequestInProgress;
            bool stopEnabled = Options.EnableStopGeneration;
            ICoreAiChatTextOptions textOptions = TextOptions;
            SendButton.text =
                GetSendButtonText(isBusy, stopEnabled, textOptions.SendButtonText, textOptions.StopButtonText);
            SendButton.tooltip = GetSendButtonTooltip(
                isBusy,
                stopEnabled,
                textOptions.SendButtonTooltip,
                textOptions.StopButtonTooltip);
            SendButton.SetEnabled(ShouldSendButtonBeEnabled(
                _isSending,
                _isStreaming,
                _isStopping,
                _isClearing,
                stopEnabled));

            if (isBusy && stopEnabled)
            {
                SendButton.AddToClassList(SendButtonStopClassName);
            }
            else
            {
                SendButton.RemoveFromClassList(SendButtonStopClassName);
            }
        }

        internal static string GetSendButtonText(bool isBusy)
        {
            return GetSendButtonText(isBusy, true);
        }

        internal static string GetSendButtonText(bool isBusy, bool stopGenerationEnabled)
        {
            return GetSendButtonText(
                isBusy,
                stopGenerationEnabled,
                CoreAiChatOptions.DefaultSendButtonText,
                CoreAiChatOptions.DefaultStopButtonText);
        }

        internal static string GetSendButtonText(
            bool isBusy,
            bool stopGenerationEnabled,
            string sendText,
            string stopText)
        {
            return isBusy && stopGenerationEnabled
                ? TextOrDefault(stopText, CoreAiChatOptions.DefaultStopButtonText)
                : TextOrDefault(sendText, CoreAiChatOptions.DefaultSendButtonText);
        }

        internal static string GetSendButtonTooltip(bool isBusy)
        {
            return GetSendButtonTooltip(isBusy, true);
        }

        internal static string GetSendButtonTooltip(bool isBusy, bool stopGenerationEnabled)
        {
            return GetSendButtonTooltip(
                isBusy,
                stopGenerationEnabled,
                CoreAiChatOptions.DefaultSendButtonTooltip,
                CoreAiChatOptions.DefaultStopButtonTooltip);
        }

        internal static string GetSendButtonTooltip(
            bool isBusy,
            bool stopGenerationEnabled,
            string sendTooltip,
            string stopTooltip)
        {
            return isBusy && stopGenerationEnabled
                ? TextOrDefault(stopTooltip, CoreAiChatOptions.DefaultStopButtonTooltip)
                : TextOrDefault(sendTooltip, CoreAiChatOptions.DefaultSendButtonTooltip);
        }

        internal static bool ShouldSendButtonBeEnabled(bool isSending, bool isStreaming, bool isStopping,
            bool isClearing)
        {
            return ShouldSendButtonBeEnabled(isSending, isStreaming, isStopping, isClearing, true);
        }

        internal static bool ShouldSendButtonBeEnabled(
            bool isSending,
            bool isStreaming,
            bool isStopping,
            bool isClearing,
            bool stopGenerationEnabled)
        {
            // WHY: while a request is running the button is the stop control, so it must stay clickable.
            if (isStopping || isClearing)
            {
                return false;
            }

            if (!stopGenerationEnabled && (isSending || isStreaming))
            {
                return false;
            }

            return true;
        }

        public void ShowTypingIndicator()
        {
            // WHY: recorded BEFORE the readiness guard - the panel must know the indicator is due even
            // when the current tree cannot show it yet, so ApplyPanelStateToTree can restore it on the
            // next bind instead of leaving a running turn with no visible progress.
            _typingIndicatorVisible = true;
            _typingToolProgressHintActive = false;

            if (TypingIndicator == null || !IsElementReadyForStyle(TypingIndicator))
            {
                return;
            }

            // WHY: streaming calls this on every chunk. Without stopping the previous scheduled item first,
            // each call leaked another repeating job — hundreds of live jobs per turn, and they fought the
            // tool-progress hint into a flicker.
            StopTypingAnimation();

            TypingIndicator.style.display = DisplayStyle.Flex;

            if (TypingLabel != null && IsElementReadyForStyle(TypingLabel))
            {
                TypingLabel.text = Options.TypingIndicatorText ?? string.Empty;
            }

            // WHY: a host can hand the panel its typing row outside BindUI (embedded trees, tests), so the
            // dots are re-resolved whenever they are missing or no longer part of a live tree.
            if (_typingDots == null || _typingDots.parent == null)
            {
                _typingDots = ResolveOrCreateTypingDots();
            }

            if (_typingDots == null || !IsElementReadyForStyle(_typingDots))
            {
                return;
            }

            VisualElement dots = _typingDots;
            dots.style.display = DisplayStyle.Flex;
            // WHY: the only code left in this animation is one state flip — UI Toolkit has no @keyframes,
            // so a looping indicator needs a toggle to transition against. Duration, easing and the
            // per-dot stagger (transition-delay) live in CoreAiChatTypingDots.uss. The job holds the
            // element it animates (not the field), so a UI rebuild that clears the field cannot make a
            // still-draining job throw.
            _typingAnimation = dots.schedule
                .Execute(() => dots.ToggleInClassList(TypingDotsPulseClassName))
                .Every(TypingDotsPulseIntervalMilliseconds);
        }

        private void ApplyStreamingToolProgressTypingHint()
        {
            _typingToolProgressHintActive = true;

            if (TypingLabel == null || !IsElementReadyForStyle(TypingLabel))
            {
                return;
            }

            StopTypingAnimation();
            // WHY: the hint is a STATIC status line ("running tools…"), so the dots must not keep
            // pulsing next to it — the same intent the old code had when it stopped the text animation.
            SetTypingDotsVisible(false);
            string fromConfig = Options.StreamingToolProgressHint;
            TypingLabel.text = string.IsNullOrWhiteSpace(fromConfig)
                ? DefaultStreamingToolProgressHint
                : fromConfig.Trim();
        }

        public void HideTypingIndicator()
        {
            _typingIndicatorVisible = false;
            _typingToolProgressHintActive = false;

            if (TypingIndicator != null && IsElementReadyForStyle(TypingIndicator))
            {
                TypingIndicator.style.display = DisplayStyle.None;
            }

            StopTypingAnimation();
        }

        private void StopTypingAnimation()
        {
            _typingAnimation?.Pause();
            _typingAnimation = null;
            _typingDots?.RemoveFromClassList(TypingDotsPulseClassName);
        }

        private void SetTypingDotsVisible(bool visible)
        {
            if (_typingDots == null || !IsElementReadyForStyle(_typingDots))
            {
                return;
            }

            _typingDots.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        }

        /// <summary>
        /// Returns the three-dot element of the typing row, creating it when the host's chat UXML predates
        /// it. The package stylesheet that animates the dots is attached to the chat root here as well, so a
        /// host with its own UXML/USS gets a working indicator without copying any rules.
        /// </summary>
        private VisualElement ResolveOrCreateTypingDots()
        {
            if (TypingIndicator == null)
            {
                return null;
            }

            EnsureTypingDotsStyleSheet();

            VisualElement existing = TypingIndicator.Q<VisualElement>(TypingDotsElementName);
            if (existing != null)
            {
                return existing;
            }

            VisualElement dots = new() { name = TypingDotsElementName, pickingMode = PickingMode.Ignore };
            dots.AddToClassList(TypingDotsClassName);
            for (int index = 1; index <= TypingDotCount; index++)
            {
                VisualElement dot = new() { pickingMode = PickingMode.Ignore };
                dot.AddToClassList(TypingDotClassName);
                dot.AddToClassList($"{TypingDotClassName}--{index}");
                dots.Add(dot);
            }

            VisualElement host = TypingLabel?.parent ?? TypingIndicator;
            host.Add(dots);
            return dots;
        }

        private void EnsureTypingDotsStyleSheet()
        {
            if (Root == null)
            {
                return;
            }

            StyleSheet sheet = Resources.Load<StyleSheet>(TypingDotsStyleSheetResourcePath);
            if (sheet == null || Root.styleSheets.Contains(sheet))
            {
                return;
            }

            Root.styleSheets.Add(sheet);
        }

        /// <summary>
        /// USS class marking the ONE bubble a turn is currently streaming into. Hosts may probe the
        /// transcript for it to tell "a turn is producing text right now" from "the transcript is settled".
        /// </summary>
        public const string StreamingActiveUssClassName = "coreai-streaming-active";

        /// <summary>
        /// Takes the streaming affordance off the currently open bubble and lets go of it.
        /// </summary>
        private void ReleaseActiveStreamingBubble()
        {
            if (_streamingLabel == null)
            {
                return;
            }

            _streamingLabel.RemoveFromClassList(StreamingActiveUssClassName);
            _streamingLabel = null;
        }

        /// <summary>
        /// Opens a fresh assistant bubble for the in-flight turn and returns it (null when there is no
        /// message container to render into).
        /// </summary>
        private Label StartStreaming()
        {
            HideTypingIndicator();
            ResetLongRequestHint();
            _isStreaming = true;
            RaiseBusyStateChangedIfChanged();
            ReleaseActiveStreamingBubble();
            _streamingBubbleSealed = false;
            _streamingRenderCapReached = false;

            if (MessageScroll == null)
            {
                return null;
            }

            VisualElement templateRow = CreateMessageBubbleRow(false);
            VisualElement avatar = templateRow.Q<VisualElement>("coreai-message-avatar");
            VisualElement contentSlot = templateRow.Q<VisualElement>("coreai-message-content-slot");
            ApplyAvatarSprite(avatar);

            _streamingLabel = new Label(string.Empty);
            _streamingLabel.style.whiteSpace = WhiteSpace.Normal;
            _streamingLabel.AddToClassList("coreai-chat-message");
            _streamingLabel.AddToClassList("coreai-ai-message");
            _streamingLabel.AddToClassList(StreamingActiveUssClassName);
            MakeTextSelectable(_streamingLabel);

            AddBubbleContent(templateRow, contentSlot, _streamingLabel);
            // WHY: record the bubble in open order — after the turn a host needs EVERY segment, not
            // just the last one (see TurnStreamingBubbles).
            _turnStreamingBubbles.Add(_streamingLabel);
            RememberReaderPositionBeforeAppend();
            MessageScroll.Add(templateRow);
            ScrollForNewAssistantRow(templateRow);
            return _streamingLabel;
        }

        /// <summary>
        /// Appends one streamed chunk to the in-flight bubble's rendered text. Stale turns are dropped
        /// (defense-in-depth behind the <see cref="IsRequestInProgress"/> gate), and the rendered text
        /// is hard-capped at <see cref="MaxAssistantRenderChars"/> — the WebGL GPU-buffer backstop that
        /// <see cref="AddMessage"/> applies to non-streamed bubbles (full text still reaches history).
        /// </summary>
        private void AppendToStreaming(string chunk, int turnGeneration)
        {
            if (_streamingLabel == null || !_isStreaming || _streamingRenderCapReached)
            {
                return;
            }

            if (IsStaleTurn(turnGeneration, _currentTurnGeneration))
            {
                return;
            }

            // WHY: следование за хвостом переоценивается на КАЖДОМ чанке. Ученик, отошедший вверх
            // посреди ответа, тем самым перестал следовать — и лента обязана его отпустить.
            if (ScrollAnchor == ChatScrollAnchor.FollowIfAtBottom && _readerFollowsBottom)
            {
                _readerFollowsBottom = IsReaderAtBottom();
            }

            _streamingLabel.text =
                AppendStreamingChunkForRender(_streamingLabel.text, chunk, out bool cappedAtLimit);
            _streamingRenderCapReached = cappedAtLimit;
            if (ScrollAnchor == ChatScrollAnchor.Bottom
                || (ScrollAnchor == ChatScrollAnchor.FollowIfAtBottom && _readerFollowsBottom))
            {
                ScheduleStreamingScrollToBottom();
            }
        }

        /// <summary>
        /// Дописывает очередной кусок речи в полный ответ хода. Само правило разделения реплик общее
        /// для всех накопителей и живёт в <see cref="StreamedMessageJoiner"/> — здесь только вызов.
        /// <para>
        /// Пузыри на экране уже разъехались, но <c>fullResponse</c> уходит ОДНОЙ строкой в историю
        /// роли и обработчикам ответа, поэтому разделитель нужен и тут: иначе склейка «Проверь
        /// себя:**Ход завершён…**» всплывёт снова при следующем показе той же истории.
        /// </para>
        /// </summary>
        internal static string AppendStreamedMessage(string fullResponse, string formatted, bool startsNewMessage) =>
            StreamedMessageJoiner.Append(fullResponse, formatted, startsNewMessage);

        /// <summary>
        /// Closes the in-flight streaming bubble at a tool-round boundary so subsequent prose opens a
        /// fresh bubble below the tool-call bubbles (claude/cursor behaviour), instead of being appended
        /// to the bubble that was opened before the tools ran.
        /// </summary>
        private void SealStreamingBubbleIfAny()
        {
            if (_streamingLabel == null || !_isStreaming)
            {
                return;
            }

            ReleaseActiveStreamingBubble();
            _streamingBubbleSealed = true;
        }

        /// <summary>
        /// Brings the bubbles an ABANDONED turn opened to their terminal look.
        /// </summary>
        private void SealAbandonedTurnBubbles(List<Label> bubbles)
        {
            if (bubbles == null)
            {
                return;
            }

            for (int i = 0; i < bubbles.Count; i++)
            {
                Label bubble = bubbles[i];
                if (bubble == null)
                {
                    continue;
                }

                if (ReferenceEquals(bubble, _streamingLabel))
                {
                    // WHY: the live turn has not opened a bubble of its own yet (StartStreaming reassigns
                    // this field), so the reference still points at OUR dead bubble — release it properly
                    // instead of leaving the next turn to orphan it.
                    ReleaseActiveStreamingBubble();
                    continue;
                }

                bubble.RemoveFromClassList(StreamingActiveUssClassName);
            }

            bubbles.Clear();
        }

        private void FinishStreaming()
        {
            _streamingScrollScheduled = false;
            ReleaseActiveStreamingBubble();
            _isStreaming = false;
            _streamingRenderCapReached = false;
            RaiseBusyStateChangedIfChanged();
        }

        /// <summary>
        /// Queues <paramref name="job"/> on the message ScrollView's scheduler, guarded against the panel
        /// being torn down before the job runs.
        /// <para>
        /// Every deferred scroll job MUST go through here. <see cref="ResetUiReferences"/> nulls the UI
        /// fields, but jobs queued earlier keep firing on the still-live visual tree until Unity detaches
        /// it, so a job landing in that window reads a null field — the scroll settle chain used to
        /// dereference <see cref="MessageScroll"/> there and threw out of
        /// <c>BaseRuntimePanel.Update</c>.
        /// </para>
        /// </summary>
        /// <param name="job">Work to run on the scheduler once the panel is confirmed alive.</param>
        /// <param name="delayMilliseconds">Delay before the first run; 0 runs on the next scheduler pass.</param>
        private void ScheduleOnMessageScroll(Action job, long delayMilliseconds = 0)
        {
            ScrollView scroll = MessageScroll;
            if (scroll == null)
            {
                return;
            }

            IVisualElementScheduledItem item = scroll.schedule.Execute(() => RunMessageScrollJob(job));
            if (delayMilliseconds > 0)
            {
                item.StartingIn(delayMilliseconds);
            }
        }

        /// <summary>
        /// Runs a scheduled scroll job unless the panel or its UI references went away first. Named (not
        /// inlined into the closure) so the guard itself is reachable from a regression test.
        /// </summary>
        private void RunMessageScrollJob(Action job)
        {
            if (this == null || MessageScroll == null)
            {
                return;
            }

            job();
        }

        /// <summary>
        /// Requests a scroll to the newest message. Coalesced: a burst of appended messages schedules a
        /// single settle chain (immediate + delayed snaps for late layout passes) instead of five
        /// scheduler jobs per message.
        /// </summary>
        protected void ScrollToBottom()
        {
            if (MessageScroll == null || _scrollToBottomScheduled)
            {
                return;
            }

            _scrollToBottomScheduled = true;
            ScheduleOnMessageScroll(() =>
            {
                _scrollToBottomScheduled = false;
                SnapScrollToBottom();
                ScheduleOnMessageScroll(SnapScrollToBottom);
                ScheduleOnMessageScroll(SnapScrollToBottom, 80);
                ScheduleOnMessageScroll(SnapScrollToBottom, 200);
                ScheduleOnMessageScroll(SnapScrollToBottom, 500);
            });
        }

        /// <summary>
        /// At most one pending scroll pass per streaming burst (see <see cref="AppendToStreaming"/>).
        /// </summary>
        private void ScheduleStreamingScrollToBottom()
        {
            if (MessageScroll == null)
            {
                return;
            }

            if (_streamingScrollScheduled)
            {
                return;
            }

            _streamingScrollScheduled = true;
            ScheduleOnMessageScroll(() =>
            {
                _streamingScrollScheduled = false;
                SnapScrollToBottom();
                ScheduleOnMessageScroll(SnapScrollToBottom);
            });
        }

        private void SnapScrollToBottom()
        {
            if (MessageScroll?.verticalScroller == null)
            {
                return;
            }

            Scroller vs = MessageScroll.verticalScroller;
            vs.value = vs.highValue;
        }

        [Header("Scroll")]
        [SerializeField]
        [Tooltip(
            "Where the chat scrolls when the assistant writes. Bottom — classic chat that follows the " +
            "answer. AssistantMessageStart — the new answer is pinned by its top. KeepPosition — the " +
            "view is never moved, new messages grow below.")]
        private ChatScrollAnchor scrollAnchor = ChatScrollAnchor.Bottom;

        /// <summary>
        /// Scroll policy for NEW assistant messages. Comes from the inspector; a host that owns the
        /// policy in code overrides this property — then the inspector value is ignored.
        /// </summary>
        protected virtual ChatScrollAnchor ScrollAnchor => scrollAnchor;

        /// <summary>
        /// Does this policy move the view when the ASSISTANT produces content? Pure and public so the
        /// rule is testable and states itself in one place: <see cref="ChatScrollAnchor.KeepPosition"/>
        /// never moves the view, <see cref="ChatScrollAnchor.FollowIfAtBottom"/> moves it only while
        /// the reader is standing at the bottom, the other modes always do.
        /// </summary>
        /// <param name="readerAtBottom">Where the reader stood BEFORE the content was appended.</param>
        public static bool MovesViewForAssistantContent(ChatScrollAnchor anchor, bool readerAtBottom) =>
            anchor switch
            {
                ChatScrollAnchor.KeepPosition => false,
                ChatScrollAnchor.FollowIfAtBottom => readerAtBottom,
                _ => true,
            };

        /// <summary>Порог «лента у низа» в единицах прокрутки (пиксели контента).</summary>
        private const float AtBottomEpsilon = 8f;

        // WHY: где читатель стоял ДО вставки. После вставки высота контента уже выросла, и отличить
        // «он внизу» от «он ушёл вверх» по самому скроллеру уже нельзя.
        private bool _readerFollowsBottom = true;

        /// <summary>Стоит ли лента у низа прямо сейчас.</summary>
        protected bool IsReaderAtBottom()
        {
            Scroller vs = MessageScroll?.verticalScroller;
            if (vs == null)
            {
                return true;
            }

            return vs.highValue <= 0f || vs.value >= vs.highValue - AtBottomEpsilon;
        }

        /// <summary>
        /// Запоминает место читателя перед добавлением строки. Зовётся ДО <c>MessageScroll.Add</c>:
        /// после вставки ответ на этот вопрос уже недоступен.
        /// </summary>
        private void RememberReaderPositionBeforeAppend() => _readerFollowsBottom = IsReaderAtBottom();

        /// <summary>
        /// Учащийся действовал прямо в ленте — ответил на встроенную карточку, нажал кнопку внутри
        /// сообщения, — и ждёт продолжения так же, как после отправки своего сообщения.
        /// <para>
        /// Такое действие не создаёт пузыря пользователя, поэтому обычный путь
        /// <c>AppendMessageBubble(isUser: true)</c> его не ловит: флаг следования оставался тем, каким
        /// был во время чтения карточки. Карточка выше ленты на целый экран — читатель почти никогда
        /// не «у низа» в момент нажатия, и ответ приходил за пределами видимой области.
        /// </para>
        /// </summary>
        public void FollowFeedAfterLearnerAction()
        {
            _turnAnchorRow = null;
            _readerFollowsBottom = true;
            ScrollToBottom();
        }

        /// <summary>Двигать ли вид под содержимое ассистента прямо сейчас.</summary>
        private bool ShouldMoveViewForAssistantContent() =>
            MovesViewForAssistantContent(ScrollAnchor, _readerFollowsBottom);

        /// <summary>
        /// Scrolls for a row that was just appended for the assistant. In <see cref="ChatScrollAnchor.Bottom"/>
        /// this is <see cref="ScrollToBottom"/>; in <see cref="ChatScrollAnchor.KeepPosition"/> nothing
        /// moves at all. Otherwise the FIRST assistant row of the turn becomes the anchor and is pinned by
        /// its top; later rows of the same turn do not move the view — the reader must not lose their
        /// place mid-answer.
        /// </summary>
        protected void ScrollForNewAssistantRow(VisualElement row)
        {
            if (!ShouldMoveViewForAssistantContent())
            {
                return;
            }

            if (ScrollAnchor == ChatScrollAnchor.Bottom
                || ScrollAnchor == ChatScrollAnchor.FollowIfAtBottom)
            {
                ScrollToBottom();
                return;
            }

            if (row == null || (_turnAnchorRow != null && _turnAnchorRow.parent != null))
            {
                return;
            }

            _turnAnchorRow = row;
            _turnAnchorMode = TurnAnchorMode.Start;
            ScheduleAnchorSettleChain(row, TurnAnchorMode.Start);
        }

        /// <summary>
        /// Reveals a row (a card, an interactive block) with the MINIMAL scroll: nothing moves when it is
        /// already fully visible; a row taller than the viewport is pinned by its top. The row becomes the
        /// turn anchor, so later re-snaps (<see cref="ScrollToTurnAnchor"/>) keep it in view instead of
        /// jumping back to the message start. Falls back to <see cref="ScrollToBottom"/> in Bottom mode,
        /// and moves nothing in <see cref="ChatScrollAnchor.KeepPosition"/> — a card the reader has not
        /// scrolled to yet is still not a reason to take the view away from them.
        /// </summary>
        protected void ScrollToRevealRow(VisualElement row)
        {
            if (!ShouldMoveViewForAssistantContent())
            {
                return;
            }

            if (ScrollAnchor == ChatScrollAnchor.Bottom
                || ScrollAnchor == ChatScrollAnchor.FollowIfAtBottom
                || row == null)
            {
                ScrollToBottom();
                return;
            }

            _turnAnchorRow = row;
            _turnAnchorMode = TurnAnchorMode.Reveal;
            ScheduleAnchorSettleChain(row, TurnAnchorMode.Reveal);
        }

        /// <summary>
        /// Re-applies the current turn anchor after the content height changed (markdown replaced a
        /// streamed label, a card was rebuilt). Bottom mode, or no anchor yet — <see cref="ScrollToBottom"/>;
        /// <see cref="ChatScrollAnchor.KeepPosition"/> — nothing moves.
        /// </summary>
        protected void ScrollToTurnAnchor()
        {
            if (!ShouldMoveViewForAssistantContent())
            {
                return;
            }

            if (ScrollAnchor == ChatScrollAnchor.Bottom
                || ScrollAnchor == ChatScrollAnchor.FollowIfAtBottom
                || _turnAnchorRow == null
                || _turnAnchorRow.parent == null)
            {
                ScrollToBottom();
                return;
            }

            ScheduleAnchorSettleChain(_turnAnchorRow, _turnAnchorMode);
        }

        /// <summary>
        /// Same settle chain as <see cref="ScrollToBottom"/> (immediate + next pass + 80/200/500 ms):
        /// row heights keep changing for a few layout passes after an append, so a single snap lands
        /// on a stale position.
        /// </summary>
        private void ScheduleAnchorSettleChain(VisualElement row, TurnAnchorMode mode)
        {
            if (MessageScroll == null || row == null)
            {
                return;
            }

            ScheduleOnMessageScroll(() =>
            {
                SnapScrollToAnchor(row, mode);
                ScheduleOnMessageScroll(() => SnapScrollToAnchor(row, mode));
                ScheduleOnMessageScroll(() => SnapScrollToAnchor(row, mode), 80);
                ScheduleOnMessageScroll(() => SnapScrollToAnchor(row, mode), 200);
                ScheduleOnMessageScroll(() => SnapScrollToAnchor(row, mode), 500);
            });
        }

        private void SnapScrollToAnchor(VisualElement row, TurnAnchorMode mode)
        {
            // WHY: a newer anchor (next turn, a revealed card) supersedes a chain still in flight.
            if (MessageScroll?.verticalScroller == null || row == null || row.parent == null ||
                !ReferenceEquals(row, _turnAnchorRow))
            {
                return;
            }

            Scroller vs = MessageScroll.verticalScroller;
            Rect layout = row.layout;
            float viewportHeight = MessageScroll.contentViewport?.layout.height ?? 0f;
            vs.value = mode == TurnAnchorMode.Reveal
                ? ResolveRevealScrollValue(layout.yMin, layout.yMax, viewportHeight, vs.value, vs.lowValue, vs.highValue)
                : ResolveRowStartScrollValue(layout.yMin, vs.lowValue, vs.highValue);
        }

        /// <summary>Scroller value that puts <paramref name="rowTop"/> (content-space) at the top of the viewport.</summary>
        public static float ResolveRowStartScrollValue(float rowTop, float lowValue, float highValue)
        {
            float min = Mathf.Min(lowValue, highValue);
            float max = Mathf.Max(lowValue, highValue);
            if (float.IsNaN(rowTop))
            {
                return max;
            }

            return Mathf.Clamp(rowTop, min, max);
        }

        /// <summary>
        /// Minimal scroll that shows the whole row: unchanged when it already fits in view, its top when
        /// it is above the view or taller than the viewport, otherwise its bottom aligned to the bottom.
        /// </summary>
        public static float ResolveRevealScrollValue(
            float rowTop,
            float rowBottom,
            float viewportHeight,
            float currentValue,
            float lowValue,
            float highValue)
        {
            float min = Mathf.Min(lowValue, highValue);
            float max = Mathf.Max(lowValue, highValue);
            if (float.IsNaN(rowTop) || float.IsNaN(rowBottom))
            {
                return Mathf.Clamp(currentValue, min, max);
            }

            float rowHeight = rowBottom - rowTop;
            float target;
            if (viewportHeight <= 0f || rowHeight >= viewportHeight || rowTop < currentValue)
            {
                target = rowTop;
            }
            else if (rowBottom > currentValue + viewportHeight)
            {
                target = rowBottom - viewportHeight;
            }
            else
            {
                target = currentValue;
            }

            return Mathf.Clamp(target, min, max);
        }

        /// <summary>
        /// Clears chat.
        /// </summary>
        public void ClearChat()
        {
            ClearChat(true, false);
        }

        /// <summary>
        /// Clears chat.
        /// </summary>
        /// <param name="clearChatHistory">The clear chat history value.</param>
        /// <param name="clearLongTermMemory">The clear long term memory value.</param>
        public void ClearChat(bool clearChatHistory, bool clearLongTermMemory)
        {
            if (_isClearing)
            {
                return;
            }

            _isClearing = true;
            UpdateControlButtonsState();
            UpdateSendButtonVisualState();
            try
            {
                StopActiveGeneration();

                if (MessageScroll != null)
                {
                    MessageScroll.Clear();
                }

                // WHY: the bubbles were just removed from the tree; a host must not be handed
                // detached elements to post-process.
                _turnStreamingBubbles.Clear();
                _turnAnchorRow = null;

                string roleId = ActiveRoleId;
                // WHY: keep the in-memory cache consistent with the now-empty scroll, otherwise switching
                // away and back would resurrect the cleared conversation.
                _roleTranscriptCache.Remove(roleId);

                // WHY: the CoreAi facade may have no live scope; fall back to clearing history through
                // the chat service so the visible transcript and persisted history stay in sync.
                try
                {
                    CoreAi.ClearContext(roleId, clearChatHistory, clearLongTermMemory);
                }
                catch
                {
                    if (clearChatHistory)
                    {
                        _chatService?.ClearHistory(roleId);
                    }
                }

                if (!string.IsNullOrEmpty(Options.WelcomeMessage))
                {
                    AddMessage(Options.WelcomeMessage, false);
                }
            }
            finally
            {
                _isClearing = false;
                UpdateControlButtonsState();
                UpdateSendButtonVisualState();
            }
        }

        /// <summary>
        /// Stops the active generation request and immediately restores the chat controls.
        /// The unified stop path leaves already streamed assistant text in place and does not append
        /// an extra cancellation message.
        /// </summary>
        public void StopAgent()
        {
            StopActiveGeneration();
        }
    }
}
