using System;
using System.Threading;
using CoreAI.Ai;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.Chat
{
    /// <summary>
    /// Универсальная панель чата CoreAI на UI Toolkit.
    /// Работает из коробки — достаточно добавить на GameObject с UIDocument
    /// и назначить <see cref="CoreAiChatConfig"/> в Inspector.
    ///
    /// Расширение: наследуйтесь и переопределяйте virtual-методы:
    /// <see cref="OnMessageSending"/>, <see cref="OnResponseReceived"/>,
    /// <see cref="CreateMessageBubble"/>, <see cref="FormatResponseText"/>.
    /// </summary>
    public class CoreAiChatPanel : MonoBehaviour
    {
        private const string MobileClassName = "coreai-mobile";
        private const string CollapsedClassName = "coreai-collapsed";
        private const string CollapsedPrefsKey = "CoreAI.Chat.Collapsed";
        private const string SendButtonStopClassName = "coreai-chat-send-button-stop";

        [Header("Config")]
        [Tooltip("Конфигурация чата (Assets → Create → CoreAI → Chat Config).")]
        [SerializeField] protected CoreAiChatConfig config;

        [Header("Custom USS (optional)")]
        [Tooltip("Дополнительные стили поверх дефолтных. Оставьте пустым для стандартной темы.")]
        [SerializeField] protected StyleSheet customStyleSheet;

        // === UI Elements ===
        protected VisualElement  Root;
        protected VisualElement  ChatContainer;
        protected ScrollView     MessageScroll;
        protected TextField      InputField;
        protected Button         SendButton;
        protected Button         ClearButton;
        protected Button         StopButton;
        protected Button         CollapseButton;
        protected Button         FabButton;
        protected VisualElement  TypingIndicator;
        protected VisualElement  TypingAvatar;
        protected Label          TypingLabel;
        protected Label          HeaderTitle;
        protected VisualElement  HeaderIcon;

        /// <summary>Whether the chat panel is currently collapsed into the FAB.</summary>
        public bool IsCollapsed { get; private set; }

        // === Streaming state ===
        private Label _streamingLabel;
        private bool  _isStreaming;
        private bool  _isSending; // prevents Shift+Enter sending while AI is busy
        private bool _stopRequestedByUser;
        private bool _isStopping;
        private bool _isClearing;

        /// <summary>Runtime overrides for hotkeys. <c>null</c> = follow <see cref="config"/> (or built-in defaults if config is null).</summary>
        private bool? _runtimeOverrideOpenChatShortcutEnabled;

        private KeyCode? _runtimeOverrideOpenChatHotkey;
        private bool? _runtimeOverrideEscapeChatShortcuts;

        // === Think-block filter state machine (shared stateful filter) ===
        private readonly ThinkBlockStreamFilter _thinkFilter = new();
        private bool _streamingStartedVisible; // true после первого видимого текста

        // === Typing animation ===
        private IVisualElementScheduledItem _typingAnimation;
        private int _typingDotCount;

        // === Service ===
        private CoreAiChatService _chatService;
        private CancellationTokenSource _cts;
        private CancellationTokenSource _activeRequestCts;

        /// <summary>Событие: пользователь отправил сообщение (после добавления в UI).</summary>
        public event Action<string> OnUserMessageSent;

        /// <summary>Событие: AI ответил (полный текст после стриминга).</summary>
        public event Action<string> OnAiResponseCompleted;

        // ===================== Lifecycle =====================

        protected virtual void Awake()
        {
            _cts = new CancellationTokenSource();
            ConfigureWebGlKeyboardInput();
        }

        /// <summary>
        /// В WebGL по умолчанию <c>WebGLInput.captureAllKeyboardInput = true</c>:
        /// Unity перехватывает все клавиатурные события у браузера и пересылает их
        /// в Input Manager. UI Toolkit-овские TextField в runtime-панели при этом
        /// нередко «не видят» фокус корректно — фокус слетает, часть клавиш теряется.
        /// Отключаем capture, чтобы native-фокус HTML/UITK работал штатно.
        /// </summary>
        private static void ConfigureWebGlKeyboardInput()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            WebGLInput.captureAllKeyboardInput = false;
#endif
        }

        /// <summary>
        /// WebGL: удерживаем <c>WebGLInput.captureAllKeyboardInput = false</c> (см. <see cref="ConfigureWebGlKeyboardInput"/>).
        /// Все платформы: глобальные горячие клавиши чата (C / Esc), когда фокус не на UI Toolkit.
        /// </summary>
        protected virtual void Update()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (WebGLInput.captureAllKeyboardInput)
            {
                WebGLInput.captureAllKeyboardInput = false;
            }
#endif
            PollChatToggleShortcuts();
        }

        protected virtual void OnEnable()
        {
            var uiDoc = GetComponent<UIDocument>();
            if (uiDoc == null)
            {
                Debug.LogError("[CoreAiChatPanel] UIDocument component not found on this GameObject!");
                return;
            }

            Root = uiDoc.rootVisualElement;
            if (customStyleSheet != null)
            {
                Root.styleSheets.Add(customStyleSheet);
            }

            BindUI();
            ApplyConfig();
            InitService();
        }

        protected virtual void OnDisable()
        {
            if (SendButton != null) SendButton.UnregisterCallback<ClickEvent>(OnSendClicked);
            if (ClearButton != null) ClearButton.UnregisterCallback<ClickEvent>(OnClearClicked);
            if (StopButton != null) StopButton.UnregisterCallback<ClickEvent>(OnStopClicked);
            if (InputField != null)
            {
                InputField.UnregisterCallback<KeyDownEvent>(OnInputKeyDown, TrickleDown.TrickleDown);
            }
            Root?.UnregisterCallback<KeyDownEvent>(OnRootKeyDown, TrickleDown.TrickleDown);
            if (CollapseButton != null) CollapseButton.UnregisterCallback<ClickEvent>(OnCollapseClicked);
            if (FabButton != null) FabButton.UnregisterCallback<ClickEvent>(OnFabClicked);
            StopTypingAnimation();
        }

        protected virtual void OnDestroy()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _activeRequestCts?.Cancel();
            _activeRequestCts?.Dispose();
        }

        // ===================== UI Binding =====================

        protected virtual void BindUI()
        {
            ChatContainer    = Root.Q<VisualElement>("coreai-chat-root");
            MessageScroll    = Root.Q<ScrollView>("coreai-chat-scroll");
            InputField       = Root.Q<TextField>("coreai-chat-input");
            SendButton       = Root.Q<Button>("coreai-chat-send");
            ClearButton      = Root.Q<Button>("coreai-chat-clear");
            StopButton       = Root.Q<Button>("coreai-chat-stop");
            CollapseButton   = Root.Q<Button>("coreai-chat-collapse");
            FabButton        = Root.Q<Button>("coreai-chat-fab");
            TypingIndicator  = Root.Q<VisualElement>("coreai-typing-indicator");
            TypingAvatar     = Root.Q<VisualElement>("coreai-typing-avatar");
            TypingLabel      = Root.Q<Label>("coreai-typing-label");
            HeaderTitle      = Root.Q<Label>("coreai-chat-header-title");
            HeaderIcon       = Root.Q<VisualElement>("coreai-chat-header-icon");

            if (SendButton != null)
            {
                SendButton.RegisterCallback<ClickEvent>(OnSendClicked);
                // Кнопка не должна перехватывать клавиатурный фокус: иначе после
                // клика следующие нажатия клавиш уходят в button, а не в input,
                // и пользователь теряет первые буквы следующего сообщения.
                SendButton.focusable = false;
            }
            if (ClearButton != null)
            {
                ClearButton.RegisterCallback<ClickEvent>(OnClearClicked);
                ClearButton.focusable = false;
            }
            if (StopButton != null)
            {
                StopButton.RegisterCallback<ClickEvent>(OnStopClicked);
                StopButton.focusable = false;
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
                // TrickleDown: перехватываем KeyDown ДО того как multiline TextField
                // обработает Enter как newline. Иначе в фазе Bubble символ уже
                // вставлен в текст, и нажатие "отправить по Shift+Enter"
                // выглядело бы для пользователя как ничего не делающее.
                InputField.RegisterCallback<KeyDownEvent>(OnInputKeyDown, TrickleDown.TrickleDown);
            }
            Root?.RegisterCallback<KeyDownEvent>(OnRootKeyDown, TrickleDown.TrickleDown);

            // Соседние элементы не должны воровать фокус у инпута при клике по ним.
            // Принудительный Focus() вешать нельзя — UITK в WebGL по pointer-событиям
            // начинает «моргать» фокусом между внешним TextField и внутренним
            // unity-text-input, из-за чего пропадают каретка и клавиши.
            if (MessageScroll != null) MessageScroll.focusable = false;
            if (HeaderTitle != null)  HeaderTitle.focusable = false;
            if (HeaderIcon != null)   HeaderIcon.focusable = false;

            if (TypingIndicator != null) TypingIndicator.style.display = DisplayStyle.None;
            UpdateSendButtonVisualState();
            ApplyShortcutTooltips();
        }

        protected virtual void ApplyConfig()
        {
            if (config == null) return;

            if (HeaderTitle != null) HeaderTitle.text = config.HeaderTitle;

            if (HeaderIcon != null && config.AiAvatarIcon != null)
            {
                HeaderIcon.style.backgroundImage = Background.FromSprite(config.AiAvatarIcon);
            }

            if (TypingAvatar != null && config.AiAvatarIcon != null)
            {
                TypingAvatar.style.backgroundImage = Background.FromSprite(config.AiAvatarIcon);
            }

            // Размеры
            if (ChatContainer != null)
            {
                ApplyResponsiveSize(ChatContainer);
            }

            // Приветствие
            if (!string.IsNullOrEmpty(config.WelcomeMessage))
            {
                AddMessage(config.WelcomeMessage, isUser: false);
            }

            // По умолчанию на мобильных экранах чат стартует свёрнутым,
            // чтобы не перекрывать игровой мир. Пользовательский выбор
            // перекрывает это значение через PlayerPrefs.
            bool defaultCollapsed = IsMobileScreen();
            bool collapsed = PlayerPrefs.GetInt(CollapsedPrefsKey, defaultCollapsed ? 1 : 0) == 1;
            SetCollapsed(collapsed, persist: false);

            ApplyShortcutTooltips();
        }

        /// <summary>
        /// Подсказки FAB / сворачивания и буква на FAB согласно <see cref="CoreAiChatConfig"/> (горячие клавиши).
        /// </summary>
        private void ApplyShortcutTooltips()
        {
            if (FabButton != null)
            {
                FabButton.tooltip = IsOpenChatKeyboardShortcutEnabled()
                    ? $"Открыть чат ({FormatHotkeyForTooltip(ResolvedOpenChatHotkey())})"
                    : "Открыть чат";
            }

            if (CollapseButton != null)
            {
                CollapseButton.tooltip = IsEscapeChatShortcutEnabled()
                    ? "Свернуть чат (Esc)"
                    : "Свернуть чат";
            }

            var fabIcon = Root?.Q<Label>("coreai-chat-fab-icon");
            if (fabIcon != null)
            {
                fabIcon.text = IsOpenChatKeyboardShortcutEnabled()
                    ? FormatHotkeyFabGlyph(ResolvedOpenChatHotkey())
                    : "…";
            }
        }

        private bool IsOpenChatKeyboardShortcutEnabled()
        {
            if (_runtimeOverrideOpenChatShortcutEnabled.HasValue)
            {
                return _runtimeOverrideOpenChatShortcutEnabled.Value;
            }

            return config == null || config.EnableOpenChatKeyboardShortcut;
        }

        private bool IsEscapeChatShortcutEnabled()
        {
            if (_runtimeOverrideEscapeChatShortcuts.HasValue)
            {
                return _runtimeOverrideEscapeChatShortcuts.Value;
            }

            return config == null || config.EnableEscapeChatShortcuts;
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

            return key == KeyCode.None ? "…" : (key.ToString().Length <= 3 ? key.ToString() : "…");
        }

        /// <summary>
        /// Подгоняет окно чата под экран устройства.
        /// На маленьких экранах (телефоны/WebGL mobile) ограничивает размеры,
        /// чтобы панель не выходила за границы viewport.
        /// </summary>
        private void ApplyResponsiveSize(VisualElement container)
        {
            // Базовые размеры из ScriptableObject-конфига.
            float configuredWidth = config.ChatWidth;
            float configuredHeight = config.ChatHeight;

            // Runtime размеры рендера (в WebGL соответствуют текущему canvas viewport).
            float screenWidth = Mathf.Max(1f, Screen.width);
            float screenHeight = Mathf.Max(1f, Screen.height);

            const float margin = 12f;
            float maxWidth = Mathf.Max(280f, screenWidth - margin * 2f);
            float maxHeight = Mathf.Max(320f, screenHeight - margin * 2f);

            // На маленьких устройствах панель занимает почти весь экран
            // с безопасным отступом, чтобы не обрезаться по правому краю.
            bool useFullScreenLikeLayout = IsMobileScreen();

            if (useFullScreenLikeLayout)
            {
                container.AddToClassList(MobileClassName);
                container.style.left = margin;
                container.style.right = margin;
                container.style.top = margin;
                container.style.bottom = margin;
                container.style.width = StyleKeyword.Auto;
                container.style.height = StyleKeyword.Auto;
                return;
            }

            // Обычный desktop/tablet режим: сохраняем "плавающее" окно справа снизу,
            // но не даём ему выйти за экран, если конфиг слишком большой.
            container.RemoveFromClassList(MobileClassName);
            container.style.left = StyleKeyword.Auto;
            container.style.top = StyleKeyword.Auto;
            container.style.right = 24f;
            container.style.bottom = 24f;
            container.style.width = Mathf.Min(configuredWidth, maxWidth);
            container.style.height = Mathf.Min(configuredHeight, maxHeight);
        }

        // ===================== Collapse / FAB / Stop =====================

        private static bool IsMobileScreen()
        {
            float w = Mathf.Max(1f, Screen.width);
            float h = Mathf.Max(1f, Screen.height);
            return w <= 720f || h <= 560f;
        }

        private void OnClearClicked(ClickEvent _) => ClearChat();

        private void OnStopClicked(ClickEvent _) => StopAgent();

        private void OnCollapseClicked(ClickEvent _) => SetCollapsed(true, persist: true);

        private void OnFabClicked(ClickEvent _) => SetCollapsed(false, persist: true);

        /// <summary>
        /// Сворачивает/разворачивает чат. В свёрнутом состоянии контейнер
        /// скрыт через USS-класс <c>coreai-collapsed</c>, вместо него
        /// показывается плавающая круглая кнопка (<c>coreai-chat-fab</c>).
        /// </summary>
        public void SetCollapsed(bool collapsed, bool persist = true)
        {
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

            if (FabButton != null)
            {
                FabButton.style.display = collapsed ? DisplayStyle.Flex : DisplayStyle.None;
            }

            if (persist)
            {
                PlayerPrefs.SetInt(CollapsedPrefsKey, collapsed ? 1 : 0);
            }

            if (!collapsed)
            {
                // После разворачивания возвращаем фокус в поле ввода,
                // чтобы пользователь сразу мог продолжить печатать.
                InputField?.schedule.Execute(FocusInputField);
            }

            OnCollapsedStateChanged(collapsed);
        }

        /// <summary>
        /// Вызывается после каждого изменения свёрнутости панели (в т.ч. из <see cref="SetCollapsed"/>).
        /// Наследники могут синхронизировать FPS-управление, курсор и т.п.
        /// </summary>
        /// <param name="collapsed"><c>true</c> — панель свернута в FAB; <c>false</c> — развёрнута.</param>
        protected virtual void OnCollapsedStateChanged(bool collapsed)
        {
        }

        // ===================== Hotkeys — runtime API (поверх CoreAiChatConfig) =====================

        /// <summary>Эффективное значение после слияния <see cref="config"/> и runtime-переопределений.</summary>
        public bool EffectiveOpenChatKeyboardShortcutEnabled => IsOpenChatKeyboardShortcutEnabled();

        /// <summary>Эффективная клавиша открытия свёрнутого чата.</summary>
        public KeyCode EffectiveOpenChatHotkey => ResolvedOpenChatHotkey();

        /// <summary>Эффективно ли панель обрабатывает Esc (стоп / сворачивание).</summary>
        public bool EffectiveEscapeChatShortcutsEnabled => IsEscapeChatShortcutEnabled();

        /// <summary>
        /// Переопределить, разрешено ли открывать чат с клавиатуры в свёрнутом виде. <c>null</c> — снова из <see cref="config"/>.
        /// </summary>
        public void SetRuntimeOpenChatKeyboardShortcutEnabled(bool? enabled)
        {
            _runtimeOverrideOpenChatShortcutEnabled = enabled;
            ApplyShortcutTooltips();
        }

        /// <summary>
        /// Переопределить клавишу открытия (свёрнутый чат). <c>null</c> — снова из <see cref="config"/> (или <see cref="KeyCode.C"/>).
        /// </summary>
        public void SetRuntimeOpenChatHotkey(KeyCode? hotkey)
        {
            _runtimeOverrideOpenChatHotkey = hotkey;
            ApplyShortcutTooltips();
        }

        /// <summary>
        /// Переопределить обработку Esc развёрнутой панелью. <c>false</c> — Esc не трогает чат (удобно для FPS). <c>null</c> — из <see cref="config"/>.
        /// </summary>
        public void SetRuntimeEscapeChatShortcutsEnabled(bool? enabled)
        {
            _runtimeOverrideEscapeChatShortcuts = enabled;
            ApplyShortcutTooltips();
        }

        /// <summary>Сбросить все runtime-переопределения горячих клавиш.</summary>
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
                Debug.LogWarning("[CoreAiChatPanel] CoreAiChatService not available (no CoreAILifetimeScope on scene?).");
            }
        }

        // ===================== Input Handling =====================

        private void OnSendClicked(ClickEvent evt) => TrySendInput();

        private void OnInputKeyDown(KeyDownEvent evt)
        {
            // Esc обрабатывается в OnRootKeyDown (фаза TrickleDown с корня), чтобы один раз
            // свернуть чат / остановить генерацию и не дублировать логику здесь.

            // В UI Toolkit multi-line TextField на Shift+Enter иногда приходит
            // character = '\n', а keyCode = None. Поэтому проверяем оба поля.
            bool isEnter =
                evt.keyCode == KeyCode.Return ||
                evt.keyCode == KeyCode.KeypadEnter ||
                evt.character == '\n' ||
                evt.character == '\r';

            if (!isEnter) return;

            bool requireShift = config == null || config.SendOnShiftEnter;
            bool shouldSend = requireShift ? evt.shiftKey : !evt.shiftKey;

            if (!shouldSend) return;

            // Останавливаем распространение и отменяем default-поведение
            // (добавление newline в multi-line TextField).
            evt.StopImmediatePropagation();
            evt.PreventDefault();

            TrySendInput();
        }

        private void OnRootKeyDown(KeyDownEvent evt)
        {
            if (IsCollapsed && IsOpenChatKeyboardShortcutEnabled() &&
                IsOpenChatHotkeyFromKeys(ResolvedOpenChatHotkey(), evt.keyCode, evt.character, evt.ctrlKey, evt.commandKey, evt.altKey))
            {
                evt.StopImmediatePropagation();
                evt.PreventDefault();
                SetCollapsed(false, persist: true);
                return;
            }

            if (!IsCollapsed && IsEscapeChatShortcutEnabled() && IsEscape(evt))
            {
                evt.StopImmediatePropagation();
                evt.PreventDefault();
                if (IsRequestInProgress())
                {
                    StopActiveGeneration();
                }
                else
                {
                    SetCollapsed(true, persist: true);
                }
            }
        }

        /// <summary>Сопоставление нажатия с выбранной в конфиге клавишей открытия (без модификаторов).</summary>
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
        /// Legacy Input: C / Esc когда фокус не на UITK (например, управление персонажем).
        /// При активном только New Input System вызов тихо пропускается — тогда работают события <see cref="OnRootKeyDown"/>.
        /// </summary>
        private void PollChatToggleShortcuts()
        {
            try
            {
                // IPanel не всегда имеет focusedElement — используем FocusController с корня UIDocument.
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
                        SetCollapsed(false, persist: true);
                    }
                }
                else if (IsEscapeChatShortcutEnabled())
                {
                    if (Input.GetKeyDown(KeyCode.Escape))
                    {
                        if (IsRequestInProgress())
                        {
                            StopActiveGeneration();
                        }
                        else
                        {
                            SetCollapsed(true, persist: true);
                        }
                    }
                }
            }
            catch (InvalidOperationException)
            {
                // Player Settings: только New Input System — Input.* недоступен.
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
            return _isSending || _isStreaming;
        }

        private bool IsActionInProgress()
        {
            return IsChatInputLocked(_isSending, _isStreaming, _isStopping, _isClearing);
        }

        internal static bool IsChatInputLocked(bool isSending, bool isStreaming, bool isStopping, bool isClearing)
        {
            return isSending || isStreaming || isStopping || isClearing;
        }

        private void TrySendInput()
        {
            if (IsActionInProgress())
            {
                return;
            }

            if (IsRequestInProgress())
            {
                StopActiveGeneration();
                return;
            }

            // Even if the button is disabled, TextField key events can still fire.
            // Prevent sending while an AI request/stream is in progress.
            if (_isSending || _isStreaming || IsActionInProgress() || (SendButton != null && !SendButton.enabledSelf))
            {
                return;
            }

            if (InputField == null || string.IsNullOrWhiteSpace(InputField.text)) return;

            string text = InputField.text.Trim();

            // Max length check
            if (config != null && config.MaxMessageLength > 0 && text.Length > config.MaxMessageLength)
            {
                text = text.Substring(0, config.MaxMessageLength);
            }

            InputField.value = string.Empty;
            InputField.schedule.Execute(FocusInputField);

            // Hook: before sending
            text = OnMessageSending(text);
            if (string.IsNullOrEmpty(text)) return;

            AddMessage(text, isUser: true);
            OnUserMessageSent?.Invoke(text);

            SendToAI(text);
        }

        // ===================== AI Communication =====================

        private async void SendToAI(string userText)
        {
            if (_chatService == null)
            {
                AddMessage(config?.ErrorMessagePrefix + "AI сервис не подключён.", isUser: false);
                return;
            }

            string roleId = config?.RoleId ?? "PlayerChat";
            _isSending = true;
            _stopRequestedByUser = false;
            UpdateSendButtonVisualState();
            _activeRequestCts?.Dispose();
            _activeRequestCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);

            try
            {
                // Subclasses may override BuildAiTaskRequest to inject ForcedToolMode,
                // SourceTag, Priority etc. Default builds a minimal Chat request.
                AiTaskRequest request = BuildAiTaskRequest(userText, roleId);

                // Эффективный флаг стриминга:
                //   UI (CoreAiChatConfig.EnableStreaming) → per-agent (AgentMemoryPolicy)
                //   → глобальный (CoreAISettings.EnableStreaming).
                bool uiStreaming = config == null || config.EnableStreaming;
                bool useStreaming = _chatService != null
                    ? _chatService.IsStreamingEnabled(roleId, uiStreaming)
                    : uiStreaming;

                if (useStreaming)
                {
                    await SendStreamingAsync(request, _activeRequestCts.Token);
                }
                else
                {
                    await SendNonStreamingAsync(request, _activeRequestCts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                FinishStreaming();
                HideTypingIndicator();
                if (_stopRequestedByUser)
                {
                    AddMessage(config?.ErrorMessagePrefix + "Генерация остановлена.", isUser: false);
                }
                else
                {
                    AddMessage(config?.TimeoutMessage ?? "Timeout.", isUser: false);
                }
            }
            catch (Exception ex)
            {
                FinishStreaming();
                Debug.LogError($"[CoreAiChatPanel] Error: {ex.Message}");
                AddMessage((config?.ErrorMessagePrefix ?? "Error: ") + ex.Message, isUser: false);
            }
            finally
            {
                _isSending = false;
                _stopRequestedByUser = false;
                _activeRequestCts?.Dispose();
                _activeRequestCts = null;
                UpdateSendButtonVisualState();
                // Вернуть фокус во input после завершения AI-ответа,
                // чтобы пользователь мог сразу печатать следующее сообщение.
                InputField?.schedule.Execute(FocusInputField);
            }
        }

        /// <summary>
        /// Устанавливает фокус на TextField штатным способом.
        /// Вызывается ТОЛЬКО после программной очистки поля (TrySendInput/SendToAI),
        /// чтобы пользователь мог сразу печатать следующее сообщение.
        /// В обычных взаимодействиях UI Toolkit сам ставит фокус по клику —
        /// любые наши форсированные Focus() на pointer/focus событиях
        /// вызывают «моргание» фокуса и пропажу каретки.
        /// </summary>
        private void FocusInputField()
        {
            InputField?.Focus();
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
                SourceTag = "Chat"
            };
        }

        private System.Threading.Tasks.Task SendStreamingAsync(string userText, string roleId, CancellationToken ct)
        {
            return SendStreamingAsync(new AiTaskRequest { RoleId = roleId, Hint = userText, SourceTag = "Chat" }, ct);
        }

        private async System.Threading.Tasks.Task SendStreamingAsync(AiTaskRequest request, CancellationToken ct)
        {
            ShowTypingIndicator();
            ResetThinkFilter();
            _streamingStartedVisible = false;

            string fullResponse = "";
            await foreach (LlmStreamChunk chunk in _chatService.SendMessageStreamingAsync(request, ct))
            {
                if (!string.IsNullOrEmpty(chunk.Error))
                {
                    FinishStreaming();
                    HideTypingIndicator();
                    AddMessage((config?.ErrorMessagePrefix ?? "Error: ") + chunk.Error, isUser: false);
                    return;
                }

                if (!string.IsNullOrEmpty(chunk.Text))
                {
                    // Чанки из MeaiLlmClient уже отфильтрованы от <think>-блоков,
                    // но на случай, если service-layer отдал сырой поток
                    // (например, мок, прямой MEAI клиент), прогоняем ещё раз.
                    string visible = FilterStreamChunk(chunk.Text);
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

                if (chunk.IsDone)
                {
                    FinishStreaming();
                    HideTypingIndicator();
                }
            }

            if (string.IsNullOrEmpty(fullResponse))
            {
                FinishStreaming();
                HideTypingIndicator();
                AddMessage(config?.NoResponseMessage ?? "No response.", isUser: false);
            }
            else
            {
                OnResponseReceived(fullResponse);
                OnAiResponseCompleted?.Invoke(fullResponse);
            }
        }

        private System.Threading.Tasks.Task SendNonStreamingAsync(string userText, string roleId, CancellationToken ct)
        {
            return SendNonStreamingAsync(new AiTaskRequest { RoleId = roleId, Hint = userText, SourceTag = "Chat" }, ct);
        }

        private async System.Threading.Tasks.Task SendNonStreamingAsync(AiTaskRequest request, CancellationToken ct)
        {
            ShowTypingIndicator();

            string response = await _chatService.SendMessageAsync(request, ct);
            HideTypingIndicator();

            if (string.IsNullOrEmpty(response))
            {
                AddMessage(config?.NoResponseMessage ?? "No response.", isUser: false);
            }
            else
            {
                // Убираем <think> блоки из финального ответа
                response = StripThinkBlocks(response);
                string formatted = FormatResponseText(response);
                AddMessage(formatted, isUser: false);
                OnResponseReceived(formatted);
                OnAiResponseCompleted?.Invoke(formatted);
            }
        }

        // ===================== Think-Block Filter =====================

        /// <summary>Сбросить состояние фильтра think-блоков.</summary>
        private void ResetThinkFilter() => _thinkFilter.Reset();

        /// <summary>
        /// Фильтрует стриминговый чанк через общий stateful-фильтр.
        /// Возвращает только видимый текст (вне <c>&lt;think&gt;</c>-блоков).
        /// </summary>
        private string FilterStreamChunk(string chunk) => _thinkFilter.ProcessChunk(chunk);

        /// <summary>Убирает <c>&lt;think&gt;...&lt;/think&gt;</c> блоки из финального текста (non-streaming).</summary>
        private static string StripThinkBlocks(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return System.Text.RegularExpressions.Regex.Replace(
                text, @"<think>[\s\S]*?</think>\s*", "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
        }

        // ===================== Virtual Extension Points =====================

        /// <summary>
        /// Вызывается перед отправкой сообщения в AI.
        /// Можно модифицировать текст или вернуть null/empty для отмены.
        /// </summary>
        protected virtual string OnMessageSending(string text) => text;

        /// <summary>
        /// Вызывается после получения полного ответа от AI.
        /// Переопределите для пост-обработки (аналитика, логирование, etc).
        /// </summary>
        protected virtual void OnResponseReceived(string fullResponse) { }

        /// <summary>
        /// Форматирование текста ответа (каждого чанка при стриминге).
        /// Переопределите для markdown-рендеринга, emoji и т.д.
        /// </summary>
        protected virtual string FormatResponseText(string rawText) => rawText;

        /// <summary>
        /// Создание визуального элемента для сообщения.
        /// Переопределите для полностью кастомной вёрстки.
        /// </summary>
        protected virtual VisualElement CreateMessageBubble(string text, bool isUser)
        {
            var row = new VisualElement();
            row.AddToClassList("coreai-message-row");
            row.AddToClassList(isUser ? "coreai-user-row" : "coreai-ai-row");

            if (!isUser)
            {
                var avatar = new VisualElement();
                avatar.AddToClassList("coreai-avatar");
                avatar.AddToClassList("coreai-ai-avatar");

                if (config?.AiAvatarIcon != null)
                {
                    avatar.style.backgroundImage = Background.FromSprite(config.AiAvatarIcon);
                }

                var bubble = new Label(text);
                bubble.style.whiteSpace = WhiteSpace.Normal;
                bubble.AddToClassList("coreai-chat-message");
                bubble.AddToClassList("coreai-ai-message");

                row.Add(avatar);
                row.Add(bubble);
            }
            else
            {
                var bubble = new Label(text);
                bubble.style.whiteSpace = WhiteSpace.Normal;
                bubble.AddToClassList("coreai-chat-message");
                bubble.AddToClassList("coreai-user-message");

                row.Add(bubble);
            }

            return row;
        }

        // ===================== UI Helpers =====================

        public void AddMessage(string text, bool isUser)
        {
            if (MessageScroll == null) return;
            HideTypingIndicator();

            VisualElement bubble = CreateMessageBubble(text, isUser);
            MessageScroll.Add(bubble);
            ScrollToBottom();
        }

        public void SetSendEnabled(bool enabled)
        {
            if (SendButton != null) SendButton.SetEnabled(enabled);
        }

        private void StopActiveGeneration()
        {
            // Reentrance guard — prevents double-stop from concurrent Escape + button click.
            if (_isStopping || !IsRequestInProgress())
            {
                return;
            }

            _isStopping = true;
            UpdateControlButtonsState();
            UpdateSendButtonVisualState();
            try
            {
                _stopRequestedByUser = true;
                string roleId = config?.RoleId ?? "PlayerChat";

                // Cancel orchestrator tasks first
                try
                {
                    CoreAi.StopAgent(roleId);
                }
                catch
                {
                    _chatService?.StopAgent(roleId);
                }

                // Cancel the active HTTP/streaming request
                _activeRequestCts?.Cancel();
            }
            finally
            {
                _isStopping = false;
                UpdateControlButtonsState();
                UpdateSendButtonVisualState();
            }
        }

        private void UpdateControlButtonsState()
        {
            bool actionInProgress = IsActionInProgress();
            if (ClearButton != null) ClearButton.SetEnabled(!actionInProgress);
            if (StopButton != null) StopButton.SetEnabled(!actionInProgress);
        }

        private void UpdateSendButtonVisualState()
        {
            if (SendButton == null)
            {
                return;
            }

            bool isBusy = IsRequestInProgress();
            SendButton.text = GetSendButtonText(isBusy);
            SendButton.tooltip = GetSendButtonTooltip(isBusy);
            SendButton.SetEnabled(!IsActionInProgress());

            if (isBusy)
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
            return isBusy ? "X" : ">";
        }

        internal static string GetSendButtonTooltip(bool isBusy)
        {
            return isBusy ? "Остановить генерацию (Esc)" : "Отправить сообщение";
        }

        public void ShowTypingIndicator()
        {
            if (TypingIndicator == null) return;
            TypingIndicator.style.display = DisplayStyle.Flex;
            _typingDotCount = 0;

            // Пустая строка → чистая анимация точек (". → .. → ... → .).
            // Непустая строка → классический "Печатает." / "Typing..".
            string baseText = config?.TypingIndicatorText ?? string.Empty;
            _typingAnimation = TypingIndicator.schedule.Execute(() =>
            {
                _typingDotCount = _typingDotCount % 3 + 1; // 1 → 2 → 3 → 1 (точки не пропадают полностью)
                if (TypingLabel == null) return;

                string dots = new('.', _typingDotCount);
                // Паддинг пробелами чтобы ширина бабла не «скакала» между кадрами.
                string pad = new(' ', 3 - _typingDotCount);
                TypingLabel.text = baseText + dots + pad;
            }).Every(400);
        }

        public void HideTypingIndicator()
        {
            if (TypingIndicator != null) TypingIndicator.style.display = DisplayStyle.None;
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
            _isStreaming = true;
            _streamingLabel = null;

            if (MessageScroll == null) return;

            var row = new VisualElement();
            row.AddToClassList("coreai-message-row");
            row.AddToClassList("coreai-ai-row");

            var avatar = new VisualElement();
            avatar.AddToClassList("coreai-avatar");
            avatar.AddToClassList("coreai-ai-avatar");
            if (config?.AiAvatarIcon != null)
            {
                avatar.style.backgroundImage = Background.FromSprite(config.AiAvatarIcon);
            }

            _streamingLabel = new Label(string.Empty);
            _streamingLabel.style.whiteSpace = WhiteSpace.Normal;
            _streamingLabel.AddToClassList("coreai-chat-message");
            _streamingLabel.AddToClassList("coreai-ai-message");
            _streamingLabel.AddToClassList("coreai-streaming-active");

            row.Add(avatar);
            row.Add(_streamingLabel);
            MessageScroll.Add(row);
            ScrollToBottom();
        }

        private void AppendToStreaming(string chunk)
        {
            if (_streamingLabel == null || !_isStreaming) return;
            _streamingLabel.text = (_streamingLabel.text ?? "") + chunk;
            ScrollToBottom();
        }

        private void FinishStreaming()
        {
            if (_streamingLabel != null)
            {
                _streamingLabel.RemoveFromClassList("coreai-streaming-active");
            }
            _isStreaming = false;
            _streamingLabel = null;
        }

        protected void ScrollToBottom()
        {
            MessageScroll?.schedule.Execute(() =>
            {
                if (MessageScroll?.verticalScroller != null)
                {
                    MessageScroll.verticalScroller.value = MessageScroll.verticalScroller.highValue;
                }
            });
        }

        /// <summary>
        /// Очистить все сообщения из UI и сбросить контекст агента.
        /// По умолчанию очищает только краткосрочную историю чата.
        /// Для точечного сброса используйте <see cref="ClearChat(bool,bool)"/>.
        /// </summary>
        public void ClearChat() => ClearChat(clearChatHistory: true, clearLongTermMemory: false);

        /// <summary>
        /// Очистить все сообщения из UI и сбросить контекст агента с детальным управлением.
        /// </summary>
        /// <param name="clearChatHistory">Очищать ли историю чата (контекст сессии).</param>
        /// <param name="clearLongTermMemory">Очищать ли долговременную память (стэйт/факты агента).</param>
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
                // Любая очистка контекста должна останавливать активную генерацию.
                StopActiveGeneration();

                if (MessageScroll != null) MessageScroll.Clear();

                string roleId = config?.RoleId ?? "PlayerChat";

                // Используем универсальный API для очистки контекста (если scope доступен)
                try
                {
                    CoreAi.ClearContext(roleId, clearChatHistory, clearLongTermMemory);
                }
                catch
                {
                    // Fallback: очищаем только через локальный ChatService (если CoreAi scope не доступен)
                    if (clearChatHistory) _chatService?.ClearHistory(roleId);
                }

                if (config != null && !string.IsNullOrEmpty(config.WelcomeMessage))
                {
                    AddMessage(config.WelcomeMessage, isUser: false);
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
        /// Остановить генерацию текущего ответа и отменить выполняемые задачи агента.
        /// Delegates to the unified <see cref="StopActiveGeneration"/> path and
        /// additionally forces immediate UI cleanup when a request is in progress.
        /// NOTE: The stop message is NOT added here — the OperationCanceledException
        /// handler in SendToAI adds it when _stopRequestedByUser is true.
        /// </summary>
        public void StopAgent()
        {
            bool wasInProgress = IsRequestInProgress();
            StopActiveGeneration();

            if (wasInProgress)
            {
                // Force-reset the root CTS so any linked tokens are fully dead.
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = new CancellationTokenSource();

                FinishStreaming();
                HideTypingIndicator();
                _isSending = false;

                // No AddMessage here — SendToAI's OperationCanceledException handler
                // will display the stop message based on _stopRequestedByUser flag.
                UpdateSendButtonVisualState();
            }
        }
    }
}
