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

        [Header("Config")]
        [Tooltip("Конфигурация чата (Assets → Create → CoreAI → Chat Config).")]
        [SerializeField] protected CoreAiChatConfig config;

        [Header("Custom USS (optional)")]
        [Tooltip("Дополнительные стили поверх дефолтных. Оставьте пустым для стандартной темы.")]
        [SerializeField] protected StyleSheet customStyleSheet;

        // === UI Elements ===
        protected VisualElement  Root;
        protected ScrollView     MessageScroll;
        protected TextField      InputField;
        protected Button         SendButton;
        protected VisualElement  TypingIndicator;
        protected Label          TypingLabel;
        protected Label          HeaderTitle;
        protected VisualElement  HeaderIcon;

        // === Streaming state ===
        private Label _streamingLabel;
        private bool  _isStreaming;
        private bool  _isSending; // prevents Shift+Enter sending while AI is busy

        // === Think-block filter state machine (shared stateful filter) ===
        private readonly ThinkBlockStreamFilter _thinkFilter = new();
        private bool _streamingStartedVisible; // true после первого видимого текста

        // === Typing animation ===
        private IVisualElementScheduledItem _typingAnimation;
        private int _typingDotCount;

        // === Service ===
        private CoreAiChatService _chatService;
        private CancellationTokenSource _cts;

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
            if (InputField != null)
            {
                InputField.UnregisterCallback<KeyDownEvent>(OnInputKeyDown, TrickleDown.TrickleDown);
            }
            StopTypingAnimation();
        }

        protected virtual void OnDestroy()
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }

        // ===================== UI Binding =====================

        protected virtual void BindUI()
        {
            MessageScroll    = Root.Q<ScrollView>("coreai-chat-scroll");
            InputField       = Root.Q<TextField>("coreai-chat-input");
            SendButton       = Root.Q<Button>("coreai-chat-send");
            TypingIndicator  = Root.Q<VisualElement>("coreai-typing-indicator");
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
            if (InputField != null)
            {
                // TrickleDown: перехватываем KeyDown ДО того как multiline TextField
                // обработает Enter как newline. Иначе в фазе Bubble символ уже
                // вставлен в текст, и нажатие "отправить по Shift+Enter"
                // выглядело бы для пользователя как ничего не делающее.
                InputField.RegisterCallback<KeyDownEvent>(OnInputKeyDown, TrickleDown.TrickleDown);
            }

            // Соседние элементы не должны воровать фокус у инпута при клике по ним.
            // Принудительный Focus() вешать нельзя — UITK в WebGL по pointer-событиям
            // начинает «моргать» фокусом между внешним TextField и внутренним
            // unity-text-input, из-за чего пропадают каретка и клавиши.
            if (MessageScroll != null) MessageScroll.focusable = false;
            if (HeaderTitle != null)  HeaderTitle.focusable = false;
            if (HeaderIcon != null)   HeaderIcon.focusable = false;

            if (TypingIndicator != null) TypingIndicator.style.display = DisplayStyle.None;
        }

        protected virtual void ApplyConfig()
        {
            if (config == null) return;

            if (HeaderTitle != null) HeaderTitle.text = config.HeaderTitle;

            if (HeaderIcon != null && config.AiAvatarIcon != null)
            {
                HeaderIcon.style.backgroundImage = Background.FromSprite(config.AiAvatarIcon);
            }

            // Размеры
            var container = Root.Q<VisualElement>("coreai-chat-root");
            if (container != null)
            {
                ApplyResponsiveSize(container);
            }

            // Приветствие
            if (!string.IsNullOrEmpty(config.WelcomeMessage))
            {
                AddMessage(config.WelcomeMessage, isUser: false);
            }
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
            bool useFullScreenLikeLayout = screenWidth <= 720f || screenHeight <= 560f;

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

        private void TrySendInput()
        {
            // Even if the button is disabled, TextField key events can still fire.
            // Prevent sending while an AI request/stream is in progress.
            if (_isSending || _isStreaming || (SendButton != null && !SendButton.enabledSelf))
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
            SetSendEnabled(false);

            try
            {
                // Эффективный флаг стриминга:
                //   UI (CoreAiChatConfig.EnableStreaming) → per-agent (AgentMemoryPolicy)
                //   → глобальный (CoreAISettings.EnableStreaming).
                bool uiStreaming = config == null || config.EnableStreaming;
                bool useStreaming = _chatService != null
                    ? _chatService.IsStreamingEnabled(roleId, uiStreaming)
                    : uiStreaming;

                if (useStreaming)
                {
                    await SendStreamingAsync(userText, roleId);
                }
                else
                {
                    await SendNonStreamingAsync(userText, roleId);
                }
            }
            catch (OperationCanceledException)
            {
                FinishStreaming();
                AddMessage(config?.TimeoutMessage ?? "⏳ Timeout.", isUser: false);
            }
            catch (Exception ex)
            {
                FinishStreaming();
                Debug.LogError($"[CoreAiChatPanel] Error: {ex.Message}");
                AddMessage((config?.ErrorMessagePrefix ?? "Error: ") + ex.Message, isUser: false);
            }
            finally
            {
                SetSendEnabled(true);
                _isSending = false;
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

        private async System.Threading.Tasks.Task SendStreamingAsync(string userText, string roleId)
        {
            ShowTypingIndicator();
            ResetThinkFilter();
            _streamingStartedVisible = false;

            string fullResponse = "";
            await foreach (LlmStreamChunk chunk in _chatService.SendMessageStreamingAsync(userText, roleId, _cts.Token))
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

        private async System.Threading.Tasks.Task SendNonStreamingAsync(string userText, string roleId)
        {
            ShowTypingIndicator();

            string response = await _chatService.SendMessageAsync(userText, roleId, _cts.Token);
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

        /// <summary>Очистить все сообщения и историю.</summary>
        public void ClearChat()
        {
            if (MessageScroll != null) MessageScroll.Clear();
            _chatService?.ClearHistory(config?.RoleId ?? "PlayerChat");

            if (config != null && !string.IsNullOrEmpty(config.WelcomeMessage))
            {
                AddMessage(config.WelcomeMessage, isUser: false);
            }
        }
    }
}
