using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI;
using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;
using CoreAI.Threading;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.Chat
{
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
        /// Monotonic counter incremented at the start of each agent turn. Turn code captures the value
        /// at start and compares it before mutating shared UI/busy state, so a superseded turn that is
        /// still unwinding (e.g. after an agent switch) can never write into the newer turn's bubbles.
        /// </summary>
        private int _currentTurnGeneration;

        /// <summary>
        /// When an assistant turn streams prose, then a tool round runs, then more prose arrives,
        /// the post-tool prose must land in a NEW bubble (claude/cursor behaviour). This flag marks
        /// that the in-flight streaming bubble was sealed at a tool-round boundary; the next visible
        /// prose chunk opens a fresh bubble instead of appending to the old (now-resolved) one.
        /// </summary>
        private bool _streamingBubbleSealed;

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
        private bool _streamingStartedVisible; // WHY: true while streaming assistant output is currently visible.
        private bool _nonStreamAssistantOutputStarted; // WHY: true while non-stream assistant output has started.

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

        private IVisualElementScheduledItem _typingAnimation;
        private int _typingDotCount;

        protected CoreAiChatService _chatService;

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

            VisualElement chatRoot;
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

            // WHY: .coreai-chat-embedded strips the floating border/radius. The container's fixed 650×910 and
            // bottom-right anchoring resist USS/inline overrides in the cascade on this Unity version, so we
            // pin it absolute top-left and drive its exact pixel size from the wrapper's resolved geometry.
            // Absolute + left/top:0 guarantees alignment (no residual right-anchor shift → left-clip); the
            // geometry sync guarantees the size (and tracks resizes).
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
            CancelActiveRequestOnDisable();

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
        }

        /// <summary>
        /// Cancels the in-flight request when the panel component is disabled, so a hidden/disabled
        /// standalone panel never keeps a zombie streaming turn alive. The Hub collapse path is
        /// unaffected: collapsing only toggles a USS class and never disables the panel GameObject,
        /// so generation intentionally keeps running while the Hub is collapsed.
        /// </summary>
        private void CancelActiveRequestOnDisable()
        {
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

            // WHY: every chat agent (including one switched to at runtime) gets the camera tool when enabled.
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

            if (TypingIndicator != null)
            {
                if (IsElementReadyForStyle(TypingIndicator))
                {
                    TypingIndicator.style.display = DisplayStyle.None;
                }
            }

            ApplyClearButtonVisibility();
            UpdateSendButtonVisualState();
            ApplyShortcutTooltips();
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
            HeaderTitle = null;
            HeaderIcon = null;
            _examplesButton = null;
            _agentDropdown = null;
            _apiProfileDropdown = null;
            _apiProfileToggle = null;
            _longRequestHint = null;
            _streamingLabel = null;
            // WHY: pending scheduler jobs die with the old visual tree; a stuck flag would block every
            // scroll after a UI rebuild.
            _scrollToBottomScheduled = false;
            _streamingScrollScheduled = false;
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

            if (ChatContainer != null)
            {
                if (collapsed)
                {
                    ChatContainer.AddToClassList(CollapsedClassName);
                }
                else
                {
                    ChatContainer.RemoveFromClassList(CollapsedClassName);
                }
            }

            if (FabButton != null && IsElementReadyForStyle(FabButton))
            {
                FabButton.style.display = collapsed ? DisplayStyle.Flex : DisplayStyle.None;
            }

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
            _chatService = CoreAiChatService.TryCreateFromScene();
            if (_chatService == null)
            {
                Logger.LogWarning(GameLogFeature.Core,
                    "[CoreAiChatPanel] CoreAiChatService not available (no CoreAILifetimeScope on scene?).");
                return;
            }

            EnsureCameraToolForActiveRole();
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
            if (MessageScroll != null)
            {
                MessageScroll.Clear();
            }

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
            // WHY: mark busy before the first await so TrySendInput/Stop sees IsRequestInProgress even
            // if the backend completes the first streaming iteration synchronously (stub / zero-delay mock).
            _isSending = true;
            _stopRequestedByUser = false;
            UpdateSendButtonVisualState();
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

            if (!_isSending)
            {
                _isSending = true;
                UpdateSendButtonVisualState();
            }

            _stopRequestedByUser = false;
            CancellationTokenSource requestCts =
                CancellationTokenSource.CreateLinkedTokenSource(GetOrCreateCancellationTokenSource().Token,
                    cancellationToken);
            _activeRequestCts = requestCts;

            try
            {
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

                return await SendNonStreamingAsync(request, requestCts.Token);
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
                Logger.LogError(GameLogFeature.Core, $"[CoreAiChatPanel] Error: {ex}");
                if (!IsStaleTurn(turnGeneration, _currentTurnGeneration))
                {
                    FinishStreaming();
                    AddMessage(Options.ErrorMessagePrefix + ex.Message, false);
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
            return new AiTaskRequest
            {
                RoleId = roleId,
                RoutingProfileId = ResolveSelectedProfileId(),
                Hint = userText,
                SourceTag = "Chat",
                CancellationScope = roleId
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

            // WHY: yield so the UI thread can repaint (stop affordance) before ultra-fast stubs finish
            // the enumerator.
            await Task.Yield();
            _isStreaming = true;
            UpdateSendButtonVisualState();

            string fullResponse = "";
            DateTime lastChunkAt = DateTime.UtcNow;
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

                        AddMessage(Options.ErrorMessagePrefix + chunk.Error, false);
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
                            if (!_streamingStartedVisible || _streamingBubbleSealed)
                            {
                                _streamingStartedVisible = true;
                                StartStreaming();
                            }

                            // WHY: fullResponse keeps the complete text for history/handlers; only the
                            // rendered streaming label is capped (see AppendToStreaming).
                            string formatted = FormatResponseText(visible);
                            fullResponse += formatted;
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
                    ScrollToBottom();
                }
            }
        }

        private async Task<string?> SendNonStreamingAsync(AiTaskRequest request, CancellationToken ct)
        {
            ShowTypingIndicator();
            _nonStreamAssistantOutputStarted = false;

            try
            {
                string response = await _chatService.SendMessageAsync(request, ct);
                await CoreAiWebGlUiThreadMarshaling.SwitchToMainThreadForUiOptional(CancellationToken.None);
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

                HideTypingIndicator();
                ResetLongRequestHint();
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

        private VisualElement CreateMessageBubbleRow(bool isUser)
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

            if (row is CoreAiChatMessageBubbleElement bubble)
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

            // WHY: WebGL GPU-buffer backstop: cap an oversized assistant dump before it becomes a giant bubble
            // (user input is bounded by the input field). Hosts may cap earlier; this protects every
            // consumer of the package. See ClampAssistantForRender.
            if (!isUser)
            {
                text = ClampAssistantForRender(text);
            }

            AppendMessageBubble(text, isUser);
            RecordRoleTranscriptMessage(ActiveRoleId, text, isUser);
        }

        /// <summary>
        /// Renders one message bubble. Split out of <see cref="AddMessage"/> so per-role cache restore
        /// (<see cref="TryRestoreRoleTranscriptFromCache"/>) can re-render already-recorded, already-filtered
        /// text without appending duplicate entries back into <see cref="_roleTranscriptCache"/>.
        /// </summary>
        private void AppendMessageBubble(string text, bool isUser)
        {
            HideTypingIndicator();

            VisualElement bubble = CreateMessageBubble(text, isUser);
            MessageScroll.Add(bubble);
            ScrollToBottom();
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
                    await CoreAiWebGlUiThreadMarshaling.SwitchToMainThreadForUiOptional(
                        GetOrCreateCancellationTokenSource().Token);
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
            MessageScroll.Add(row);
            ScrollToBottom();
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
            if (TypingIndicator == null || !IsElementReadyForStyle(TypingIndicator))
            {
                return;
            }

            TypingIndicator.style.display = DisplayStyle.Flex;
            _typingDotCount = 0;

            string baseText = Options.TypingIndicatorText ?? string.Empty;
            _typingAnimation = TypingIndicator.schedule.Execute(() =>
            {
                _typingDotCount =
                    _typingDotCount % 3 + 1; // WHY: animation advances 1 -> 2 -> 3 -> 1 for visual typing feedback.
                if (TypingLabel == null || !IsElementReadyForStyle(TypingLabel))
                {
                    return;
                }

                string dots = new('.', _typingDotCount);
                string pad = new(' ', 3 - _typingDotCount);
                TypingLabel.text = baseText + dots + pad;
            }).Every(400);
        }

        private void ApplyStreamingToolProgressTypingHint()
        {
            if (TypingLabel == null || !IsElementReadyForStyle(TypingLabel))
            {
                return;
            }

            StopTypingAnimation();
            string fromConfig = Options.StreamingToolProgressHint;
            TypingLabel.text = string.IsNullOrWhiteSpace(fromConfig)
                ? DefaultStreamingToolProgressHint
                : fromConfig.Trim();
        }

        public void HideTypingIndicator()
        {
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
        }

        private void StartStreaming()
        {
            HideTypingIndicator();
            ResetLongRequestHint();
            _isStreaming = true;
            RaiseBusyStateChangedIfChanged();
            _streamingLabel = null;
            _streamingBubbleSealed = false;
            _streamingRenderCapReached = false;

            if (MessageScroll == null)
            {
                return;
            }

            VisualElement templateRow = CreateMessageBubbleRow(false);
            VisualElement avatar = templateRow.Q<VisualElement>("coreai-message-avatar");
            VisualElement contentSlot = templateRow.Q<VisualElement>("coreai-message-content-slot");
            ApplyAvatarSprite(avatar);

            _streamingLabel = new Label(string.Empty);
            _streamingLabel.style.whiteSpace = WhiteSpace.Normal;
            _streamingLabel.AddToClassList("coreai-chat-message");
            _streamingLabel.AddToClassList("coreai-ai-message");
            _streamingLabel.AddToClassList("coreai-streaming-active");
            MakeTextSelectable(_streamingLabel);

            AddBubbleContent(templateRow, contentSlot, _streamingLabel);
            MessageScroll.Add(templateRow);
            ScrollToBottom();
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

            _streamingLabel.text =
                AppendStreamingChunkForRender(_streamingLabel.text, chunk, out bool cappedAtLimit);
            _streamingRenderCapReached = cappedAtLimit;
            ScheduleStreamingScrollToBottom();
        }

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

            _streamingLabel.RemoveFromClassList("coreai-streaming-active");
            _streamingLabel = null;
            _streamingBubbleSealed = true;
        }

        private void FinishStreaming()
        {
            _streamingScrollScheduled = false;
            if (_streamingLabel != null)
            {
                _streamingLabel.RemoveFromClassList("coreai-streaming-active");
            }

            _isStreaming = false;
            _streamingLabel = null;
            _streamingRenderCapReached = false;
            RaiseBusyStateChangedIfChanged();
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
            MessageScroll.schedule.Execute(() =>
            {
                _scrollToBottomScheduled = false;
                SnapScrollToBottom();
                MessageScroll.schedule.Execute(SnapScrollToBottom);
                MessageScroll.schedule.Execute(SnapScrollToBottom).StartingIn(80);
                MessageScroll.schedule.Execute(SnapScrollToBottom).StartingIn(200);
                MessageScroll.schedule.Execute(SnapScrollToBottom).StartingIn(500);
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
            MessageScroll.schedule.Execute(() =>
            {
                _streamingScrollScheduled = false;
                SnapScrollToBottom();
                MessageScroll.schedule.Execute(SnapScrollToBottom);
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
            bool wasInProgress = IsRequestInProgress;
            StopActiveGeneration();

            if (wasInProgress)
            {
                FinishStreaming();
                HideTypingIndicator();
                _isSending = false;

                UpdateSendButtonVisualState();
            }
        }
    }
}
