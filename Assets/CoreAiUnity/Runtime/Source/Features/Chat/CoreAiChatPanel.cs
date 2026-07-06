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

        // === UI Elements ===
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

        // === Streaming state ===
        private Label _streamingLabel;
        private bool _isStreaming;
        private bool _isSending; // prevents Shift+Enter sending while AI is busy
        private bool _stopRequestedByUser;
        private bool _isStopping;
        private bool _isClearing;

        private bool _lastPublishedBusy;

        // Monotonic counter incremented at the start of each agent turn.
        private int _currentTurnGeneration;

        // Tracks the in-flight tool round so we can emit ToolRoundStarted with the last tool name.
        private string _lastToolNameInTurn;
        private int _toolRoundIterationInTurn; // 1-based iteration index inside current turn

        // Stream-gap diagnostic: if the orchestrator goes silent for > this many seconds between chunks,
        // log a single Info line so the host can tell "model slow" from "UI lost a chunk".
        private const double StreamGapWarnSeconds = 5.0;

        /// <summary>Runtime overrides for hotkeys. <c>null</c> = follow <see cref="config"/> (or built-in defaults if config is null).</summary>
        private bool? _runtimeOverrideOpenChatShortcutEnabled;

        private KeyCode? _runtimeOverrideOpenChatHotkey;
        private bool? _runtimeOverrideEscapeChatShortcuts;

        // === Think-block filter state machine (shared stateful filter) ===
        private readonly ThinkBlockStreamFilter _thinkFilter = new();
        private bool _streamingStartedVisible; // True while streaming assistant output is currently visible.
        private bool _nonStreamAssistantOutputStarted; // True while non-stream assistant output has started.

        /// <summary>
        /// Prevents duplicate deferred scroll jobs; nested <c>schedule.Execute</c> calls can
        /// destabilize ScrollView layout and leave the scrollbar at an old position.
        /// </summary>
        private bool _streamingScrollScheduled;

        // === Typing animation ===
        private IVisualElementScheduledItem _typingAnimation;
        private int _typingDotCount;

        // === Service ===
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

        // ===================== Lifecycle =====================

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
            TrySetWebGlCaptureAllKeyboardInput(false);
#endif
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        // UnityEngine.WebGLInput is a type forwarded to the UnityEngine.WebGLModule assembly. When a WebGL
        // player is compiled from a command-line build whose Editor active target is not WebGL, that module
        // is not in the reference set and a direct reference fails to compile (CS0103). Reflection resolves
        // the type at runtime inside the actual WebGL player, where the module is always present, so the
        // build compiles regardless of the Editor's active target while behaving identically at runtime.
        private static System.Reflection.PropertyInfo _webGlCaptureAllKeyboardInputProperty;
        private static bool _webGlCaptureAllKeyboardInputResolved;

        private static System.Reflection.PropertyInfo ResolveWebGlCaptureAllKeyboardInputProperty()
        {
            if (_webGlCaptureAllKeyboardInputResolved)
            {
                return _webGlCaptureAllKeyboardInputProperty;
            }

            _webGlCaptureAllKeyboardInputResolved = true;
            System.Type webGlInputType = System.Type.GetType("UnityEngine.WebGLInput, UnityEngine.WebGLModule");
            _webGlCaptureAllKeyboardInputProperty = webGlInputType?.GetProperty(
                "captureAllKeyboardInput",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            return _webGlCaptureAllKeyboardInputProperty;
        }

        private static void TrySetWebGlCaptureAllKeyboardInput(bool value)
        {
            System.Reflection.PropertyInfo property = ResolveWebGlCaptureAllKeyboardInputProperty();
            if (property != null && property.CanWrite)
            {
                property.SetValue(null, value);
            }
        }

        private static bool GetWebGlCaptureAllKeyboardInput()
        {
            System.Reflection.PropertyInfo property = ResolveWebGlCaptureAllKeyboardInputProperty();
            return property != null && property.CanRead && (bool)property.GetValue(null);
        }
#endif

        /// <summary>
        /// Polls keyboard shortcuts, updates long-request feedback, and reapplies responsive layout.
        /// </summary>
        protected virtual void Update()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (GetWebGlCaptureAllKeyboardInput())
            {
                TrySetWebGlCaptureAllKeyboardInput(false);
            }
#endif
            PollChatToggleShortcuts();
            TickLongRequestHint();
        }

        protected virtual void OnEnable()
        {
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
            // Resolve and cache required local values.
            bool defaultCollapsed = IsMobileScreen();
            bool collapsed = PlayerPrefs.GetInt(CollapsedPrefsKey, defaultCollapsed ? 1 : 0) == 1;
            SetCollapsed(collapsed, false);
        }

        protected virtual void OnDisable()
        {
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

        protected virtual void OnDestroy()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _activeRequestCts?.Cancel();
            _activeRequestCts?.Dispose();
        }

        // ===================== Agent switching =====================

        private DropdownField _agentDropdown;
        private string _activeRoleId;
        private bool _agentSwitchingEnabled;

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

            var roles = new System.Collections.Generic.List<string>(BuiltInAgentRoleIds.AllBuiltInRoles);
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

            _activeRoleId = evt.newValue;
            Debug.Log($"[CoreAiChatPanel] Active agent role -> {_activeRoleId}");
        }

        // ===================== UI Binding =====================

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
            _longRequestHint = null;
            _streamingLabel = null;
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

            // Resolve and cache required local values.
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

        // ===================== Collapse / FAB =====================

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

        /// <summary>Clears runtime hotkey overrides so the panel uses configuration defaults again.</summary>
        public void ClearRuntimeHotkeyOverrides()
        {
            _runtimeOverrideOpenChatShortcutEnabled = null;
            _runtimeOverrideOpenChatHotkey = null;
            _runtimeOverrideEscapeChatShortcuts = null;
            ApplyShortcutTooltips();
        }

        protected virtual void InitService()
        {
            _chatService = CoreAiChatService.TryCreateFromScene();
            if (_chatService == null)
            {
                Logger.LogWarning(GameLogFeature.Core,
                    "[CoreAiChatPanel] CoreAiChatService not available (no CoreAILifetimeScope on scene?).");
            }
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

            ICoreAiChatOptions options = Options;
            if (string.IsNullOrEmpty(options.WelcomeMessage))
            {
                return;
            }

            AddMessage(options.WelcomeMessage, false);
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

            string roleId = options.RoleId ?? BuiltInAgentRoleIds.SmartChat;
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

                AddMessage(text.TrimEnd(), isUser);
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
            catch
            {
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

        // ===================== Input Handling =====================

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
            // Resolve and cache required local values.
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
                if (IsRequestInProgress())
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
                // Resolve and cache required local values.
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
                        if (IsRequestInProgress())
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
            catch (InvalidOperationException)
            {
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

        private bool IsRequestInProgress()
        {
            // `_isSending` covers the whole agent turn (RunAgentTurnAsync `finally`).
            // `_isStreaming` stays true until the streaming enumerator fully completes
            // (LLM chunks + orchestrator post-work); do not clear it on `chunk.IsDone` alone.
            return _isSending || _isStreaming;
        }

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
            if (IsRequestInProgress())
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

            // Even if the button is disabled, TextField key events can still fire.
            // Prevent sending while an AI request/stream is in progress.
            if (IsActionInProgress() || (SendButton != null && !SendButton.enabledSelf))
            {
                return;
            }

            if (InputField == null || string.IsNullOrWhiteSpace(InputField.text))
            {
                return;
            }

            string text = InputField.text.Trim();

            // Max length check
            int maxMessageLength = Options.MaxMessageLength;
            if (maxMessageLength > 0 && text.Length > maxMessageLength)
            {
                text = text.Substring(0, maxMessageLength);
            }

            InputField.value = string.Empty;
            InputField.schedule.Execute(FocusInputField);

            // Hook: before sending
            text = OnMessageSending(text);
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            AddMessage(text, true);
            OnUserMessageSent?.Invoke(text);

            SendToAI(text);
        }

        // ===================== AI Communication =====================

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
            // Mark busy before the first await so TrySendInput/Stop sees IsRequestInProgress even if the
            // backend completes the first streaming iteration synchronously (e.g. stub / zero-delay mock).
            _isSending = true;
            _stopRequestedByUser = false;
            UpdateSendButtonVisualState();
            try
            {
                await RunAgentTurnAsync(userText, null, GetOrCreateCancellationTokenSource().Token);
            }
            catch (OperationCanceledException)
            {
                // User stop/cancel is handled inside RunAgentTurnAsync when possible. Keep this
                // fire-and-forget entry point from surfacing an unobserved task exception on WebGL.
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogError(GameLogFeature.Core, $"[CoreAiChatPanel] SendToAI: {ex}");
            }
        }

        /// <param name="userTextForModel">The user text for model value.</param>
        /// <param name="simulatedAssistantReply">The simulated assistant reply value.</param>
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
            // can compare values across awaits to detect "a newer turn is already in flight".
            Interlocked.Increment(ref _currentTurnGeneration);
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
                    return await SendStreamingAsync(request, requestCts.Token);
                }

                return await SendNonStreamingAsync(request, requestCts.Token);
            }
            catch (OperationCanceledException)
            {
                await CoreAiWebGlUiThreadMarshaling.SwitchToMainThreadForUiOptional(CancellationToken.None);
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
                FinishStreaming();
                Logger.LogError(GameLogFeature.Core, $"[CoreAiChatPanel] Error: {ex}");
                AddMessage(Options.ErrorMessagePrefix + ex.Message, false);
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

                FinishStreaming();
                HideTypingIndicator();
                _isSending = false;
                _stopRequestedByUser = false;
                ResetLongRequestHint();
                if (ReferenceEquals(_activeRequestCts, requestCts))
                {
                    _activeRequestCts = null;
                }

                _lastToolNameInTurn = null;

                requestCts.Dispose();
                UpdateSendButtonVisualState();
                InputField?.schedule.Execute(FocusInputField);
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

        private async Task<string?> SendStreamingAsync(AiTaskRequest request, CancellationToken ct)
        {
            ShowTypingIndicator();
            ResetThinkFilter();
            _streamingStartedVisible = false;

            // Yield so the UI thread can repaint (stop affordance) before ultra-fast stubs finish the enumerator.
            await Task.Yield();
            _isStreaming = true;
            UpdateSendButtonVisualState();

            string fullResponse = "";
            DateTime lastChunkAt = DateTime.UtcNow;
            try
            {
                await foreach (LlmStreamChunk chunk in _chatService.SendMessageStreamingAsync(request, ct))
                {
                    // Stream-gap diagnostic: helps tell "model is slow" from "UI lost a chunk".
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

                    // LLM/orchestrator stack uses ConfigureAwait(false); UITK must be touched on the main thread
                    await CoreAiWebGlUiThreadMarshaling.SwitchToMainThreadForUiOptional(ct);
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
                        // Heuristic for tool-round boundary: the orchestrator only emits a tool-progress hint
                        // mid-stream when an LLM round just produced tool calls and a new round is about to
                        // start. If prose has already streamed in this turn (`_streamingStartedVisible`),
                        // want to show "tool X (k/N)" badges without reflection.
                        if (chunk.BufferedStreamingUseToolProgressHint && _streamingStartedVisible)
                        {
                            _toolRoundIterationInTurn++;
                            RaiseToolRoundStarted(_toolRoundIterationInTurn, _lastToolNameInTurn);
                        }

                        if (chunk.BufferedStreamingUseToolProgressHint)
                        {
                            // Mid-turn: prose may already be streaming (typing row was hidden); show typing again.
                            if (_streamingStartedVisible)
                            {
                                ShowTypingIndicator();
                            }

                            ApplyStreamingToolProgressTypingHint();
                        }
                        else
                        {
                            // ApplyStreamingToolProgressTypingHint (StopTypingAnimation) even before any prose.
                            ShowTypingIndicator();
                        }

                        continue;
                    }

                    if (!string.IsNullOrEmpty(chunk.Text))
                    {
                        // Resolve and cache required local values.
                        string visible = FilterStreamChunk(chunk.Text);
                        if (fullResponse.Length == 0)
                        {
                            visible = NormalizeAssistantDisplayText(visible);
                        }

                        if (!string.IsNullOrEmpty(visible))
                        {
                            if (!_streamingStartedVisible)
                            {
                                _streamingStartedVisible = true;
                                StartStreaming();
                            }

                            string formatted = FormatResponseText(visible);
                            fullResponse += formatted;
                            AppendToStreaming(formatted);
                        }
                    }
                }

                if (string.IsNullOrEmpty(fullResponse))
                {
                    AddMessage(Options.NoResponseMessage ?? "No response.", false);
                    return null;
                }

                await CoreAiWebGlUiThreadMarshaling.SwitchToMainThreadForUiOptional(ct);
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

                FinishStreaming();
                HideTypingIndicator();
                UpdateSendButtonVisualState();
                ScrollToBottom();
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

        // ===================== Think-Block Filter =====================

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

        // ===================== Virtual Extension Points =====================

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

            // A truncation inside a ``` block leaves the fence open and breaks markdown layout; close it.
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
            label.focusable = true; // required so Ctrl/Cmd+C copies the active selection
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

        // ===================== UI Helpers =====================

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

            // WebGL GPU-buffer backstop: cap an oversized assistant dump before it becomes a giant bubble
            // (user input is bounded by the input field). Hosts may cap earlier; this protects every
            // consumer of the package. See ClampAssistantForRender.
            if (!isUser)
            {
                text = ClampAssistantForRender(text);
            }

            HideTypingIndicator();

            VisualElement bubble = CreateMessageBubble(text, isUser);
            MessageScroll.Add(bubble);
            ScrollToBottom();
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
            // Always subscribe: the handler also tracks `_lastToolNameInTurn` for the
            // ToolRoundStarted public event, independent of `ShowToolCallsInChat`.
            // The bubble-rendering branch inside the handler is still gated by runtime options.
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
            string panelRole = string.IsNullOrEmpty(options.RoleId) ? BuiltInAgentRoleIds.SmartChat : options.RoleId;
            // listeners want the name regardless of whether the bubble is rendered.
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
            if (_isStopping || !IsRequestInProgress())
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
                string roleId = Options.RoleId ?? BuiltInAgentRoleIds.SmartChat;
                CancellationTokenSource activeRequestCts = _activeRequestCts;

                // Cancel orchestrator tasks first
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

                // Cancel the active HTTP/streaming request. On WebGL, cancellation callbacks can
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
            // Single funnel for busy-state changes: every flag mutation in the panel
            // already calls UpdateSendButtonVisualState() to refresh the send/stop affordance,
            // so emitting the public BusyStateChanged event here keeps the contract consistent
            // without sprinkling RaiseBusyStateChangedIfChanged() everywhere.
            RaiseBusyStateChangedIfChanged();

            if (SendButton == null)
            {
                return;
            }

            bool isBusy = IsRequestInProgress();
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
            // While a request is running the button is the stop control, so it must stay clickable.
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

            // Resolve and cache required local values.
            string baseText = Options.TypingIndicatorText ?? string.Empty;
            _typingAnimation = TypingIndicator.schedule.Execute(() =>
            {
                _typingDotCount =
                    _typingDotCount % 3 + 1; // Animation advances 1 -> 2 -> 3 -> 1 for visual typing feedback.
                if (TypingLabel == null || !IsElementReadyForStyle(TypingLabel))
                {
                    return;
                }

                string dots = new('.', _typingDotCount);
                // Resolve and cache required local values.
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

        private void AppendToStreaming(string chunk)
        {
            if (_streamingLabel == null || !_isStreaming)
            {
                return;
            }

            _streamingLabel.text = (_streamingLabel.text ?? "") + chunk;
            ScheduleStreamingScrollToBottom();
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
            RaiseBusyStateChangedIfChanged();
        }

        protected void ScrollToBottom()
        {
            if (MessageScroll == null)
            {
                return;
            }

            //
            MessageScroll.schedule.Execute(() =>
            {
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

                string roleId = Options.RoleId ?? BuiltInAgentRoleIds.SmartChat;

                // Wrap the following block with exception-safe behavior.
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
            bool wasInProgress = IsRequestInProgress();
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