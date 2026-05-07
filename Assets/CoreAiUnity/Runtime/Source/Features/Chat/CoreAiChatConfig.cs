using UnityEngine;

namespace CoreAI.Chat
{
    /// <summary>
    /// Конфигурация универсального чата CoreAI.
    /// Назначается в Inspector на <see cref="CoreAiChatPanel"/>.
    /// Создать: Assets → Create → CoreAI → Chat Config
    /// </summary>
    [CreateAssetMenu(fileName = "CoreAiChatConfig", menuName = "CoreAI/Chat Config")]
    public class CoreAiChatConfig : ScriptableObject
    {
        [Header("Agent")]
        [Tooltip("ID роли агента (AgentBuilder roleId). Используется для маршрутизации промптов и chat history.")]
        [SerializeField] private string _roleId = "SmartChat";

        [Header("UI — Заголовок")]
        [SerializeField] private string _headerTitle = "AI Chat";

        [Header("UI — Приветствие")]
        [Tooltip("Сообщение, показываемое при открытии чата. Пустая строка = без приветствия.")]
        [TextArea(2, 4)]
        [SerializeField] private string _welcomeMessage = "Привет! Чем могу помочь?";

        [Header("Сессия / история")]
        [Tooltip(
            "При включении панели подгружать в UI сохранённую историю чата из IAgentMemoryStore для RoleId " +
            "(файл под persistentDataPath, если у роли включён persist chat в AgentMemoryPolicy). " +
            "Если история непустая — приветствие не дублируется.")]
        [SerializeField]
        private bool _loadPersistedChatOnStartup = true;

        [Tooltip("Максимум последних сообщений для отображения при подгрузке (0 = все сохранённые).")]
        [SerializeField]
        private int _maxPersistedMessagesForUi = 0;

        [Header("UI — Иконки")]
        [Tooltip("Иконка AI-аватара (опционально).")]
        [SerializeField] private Sprite _aiAvatarIcon;
        [Tooltip("Иконка пользователя (опционально).")]
        [SerializeField] private Sprite _userAvatarIcon;

        [Header("Streaming")]
        [Tooltip("Если true, ответ AI показывается по мере генерации (streaming). Если false — ждёт полный ответ.")]
        [SerializeField] private bool _enableStreaming = true;

        [Header("UI — Диагностика")]
        [Tooltip(
            "Показывать в ленте чата строки о вызовах инструментов (native tool), когда модель их выполняет. " +
            "Только для текущей сессии UI — не сохраняется в IAgentMemoryStore. Фильтр по RoleId панели.")]
        [SerializeField]
        private bool _showToolCallsInChat;

        [Header("UI — Индикатор набора")]
        [Tooltip("Префикс перед анимированными точками (например, \"Печатает\" → \"Печатает...\"). " +
                 "Оставьте пустым чтобы показывать только анимированные точки \"...\".")]
        [SerializeField] private string _typingIndicatorText = "";

        [Header("UI — Стриминг: инструмент / буфер")]
        [Tooltip(
            "Короткая строка в индикаторе набора при действии агента (вызов инструмента: native или text-shaped) " +
            "или при удержании tool-json в hybrid-стриме. Пустая строка — встроенный дефолт панели. " +
            "Ожидание шага без вызова инструмента (маркер без второго флага в потоке) — обычная анимация «...».")]
        [TextArea(1, 2)]
        [SerializeField]
        private string _streamingToolProgressHint = "Действие…";

        [Header("UI — Долгий ход (подсказка под набором)")]
        [Tooltip(
            "Строка под `#coreai-typing-indicator` после ~3 с с момента **старта запроса** к LLM в этом ходу " +
            "(индикатор набора, стриминг или ожидание полного ответа). Плейсхолдер: **{elapsed}** — секунды (целое). Пустая строка — не показывать.")]
        [TextArea(1, 3)]
        [SerializeField]
        private string _longRequestHintFormat = "⌛ Ответ формируется… ~{elapsed} с";

        [Header("UI — Размеры")]
        [CoreAiChatLayoutOption]
        [Tooltip(
            "Если включено, панель растягивается почти на весь экран (отступы от краёв). " +
            "По умолчанию выключено: плавающее окно справа снизу по ширине/высоте ниже.")]
        [SerializeField]
        private bool _useFullscreenChat;

        [Tooltip("Ширина плавающего окна чата (px), когда fullscreen выключен. Дефолт пакета: 650 (≈ +30% к прежним 500; совпадает с `CoreAiChat.uss`).")]
        [SerializeField] private int _chatWidth = 650;
        [Tooltip("Высота плавающего окна чата (px), когда fullscreen выключен. Дефолт пакета: 910 (≈ +30% к прежним 700; совпадает с `CoreAiChat.uss`).")]
        [SerializeField] private int _chatHeight = 910;

        [Header("Ввод")]
        [Tooltip("Если true — Shift+Enter отправляет сообщение. Если false — Enter отправляет, а Shift+Enter вставляет перенос строки.")]
        [SerializeField] private bool _sendOnShiftEnter = false;

        [Tooltip("Максимальная длина сообщения (0 = без лимита).")]
        [SerializeField] private int _maxMessageLength = 2000;

        [Header("Горячие клавиши")]
        [Tooltip(
            "Пока чат свёрнут (FAB), разрешить открытие с клавиатуры (UI Toolkit + опрос Legacy Input, когда фокус не на UITK). Выключите, если клавиша конфликтует с управлением в игре.")]
        [SerializeField]
        private bool _enableOpenChatKeyboardShortcut = true;

        [Tooltip("Клавиша открытия свёрнутого чата (без Ctrl / Cmd / Alt). Игнорируется, если открытие с клавиатуры выключено.")]
        [SerializeField]
        private KeyCode _openChatHotkey = KeyCode.C;

        [Tooltip(
            "Пока чат развёрнут: Esc останавливает генерацию (если идёт) или сворачивает панель. Выключите, если Esc нужен только игроку.")]
        [SerializeField]
        private bool _enableEscapeChatShortcuts = true;

        [Header("Ошибки")]
        [SerializeField] private string _errorMessagePrefix = "Error: ";
        [SerializeField] private string _timeoutMessage = "Request timeout.";
        [SerializeField] private string _noResponseMessage = "Не удалось получить ответ. Попробуйте ещё раз.";

        // === Public API ===

        public string RoleId => _roleId;
        public string HeaderTitle => _headerTitle;
        public string WelcomeMessage => _welcomeMessage;
        public bool LoadPersistedChatOnStartup => _loadPersistedChatOnStartup;
        public int MaxPersistedMessagesForUi => _maxPersistedMessagesForUi < 0 ? 0 : _maxPersistedMessagesForUi;
        public Sprite AiAvatarIcon => _aiAvatarIcon;
        public Sprite UserAvatarIcon => _userAvatarIcon;
        public bool EnableStreaming => _enableStreaming;
        public bool ShowToolCallsInChat => _showToolCallsInChat;
        public string TypingIndicatorText => _typingIndicatorText;
        public string StreamingToolProgressHint => _streamingToolProgressHint ?? string.Empty;
        public string LongRequestHintFormat => _longRequestHintFormat ?? string.Empty;
        public bool UseFullscreenChat => _useFullscreenChat;
        public int ChatWidth => _chatWidth;
        public int ChatHeight => _chatHeight;
        public bool SendOnShiftEnter => _sendOnShiftEnter;
        public int MaxMessageLength => _maxMessageLength;
        public bool EnableOpenChatKeyboardShortcut => _enableOpenChatKeyboardShortcut;
        public KeyCode OpenChatHotkey => _openChatHotkey;
        public bool EnableEscapeChatShortcuts => _enableEscapeChatShortcuts;
        public string ErrorMessagePrefix => _errorMessagePrefix;
        public string TimeoutMessage => _timeoutMessage;
        public string NoResponseMessage => _noResponseMessage;
    }
}
