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
        [SerializeField] private string _roleId = "PlayerChat";

        [Header("UI — Заголовок")]
        [SerializeField] private string _headerTitle = "AI Chat";

        [Header("UI — Приветствие")]
        [Tooltip("Сообщение, показываемое при открытии чата. Пустая строка = без приветствия.")]
        [TextArea(2, 4)]
        [SerializeField] private string _welcomeMessage = "Привет! Чем могу помочь?";

        [Header("UI — Иконки")]
        [Tooltip("Иконка AI-аватара (опционально).")]
        [SerializeField] private Sprite _aiAvatarIcon;
        [Tooltip("Иконка пользователя (опционально).")]
        [SerializeField] private Sprite _userAvatarIcon;

        [Header("Streaming")]
        [Tooltip("Если true, ответ AI показывается по мере генерации (streaming). Если false — ждёт полный ответ.")]
        [SerializeField] private bool _enableStreaming = true;

        [Header("UI — Индикатор набора")]
        [Tooltip("Префикс перед анимированными точками (например, \"Печатает\" → \"Печатает...\"). " +
                 "Оставьте пустым чтобы показывать только анимированные точки \"...\".")]
        [SerializeField] private string _typingIndicatorText = "";

        [Header("UI — Размеры")]
        [SerializeField] private int _chatWidth = 500;
        [SerializeField] private int _chatHeight = 700;

        [Header("Ввод")]
        [Tooltip("Если true — Shift+Enter отправляет сообщение. Если false — Enter отправляет.")]
        [SerializeField] private bool _sendOnShiftEnter = true;

        [Tooltip("Максимальная длина сообщения (0 = без лимита).")]
        [SerializeField] private int _maxMessageLength = 2000;

        [Header("Ошибки")]
        [SerializeField] private string _errorMessagePrefix = "Error: ";
        [SerializeField] private string _timeoutMessage = "Request timeout.";
        [SerializeField] private string _noResponseMessage = "Не удалось получить ответ. Попробуйте ещё раз.";

        // === Public API ===

        public string RoleId => _roleId;
        public string HeaderTitle => _headerTitle;
        public string WelcomeMessage => _welcomeMessage;
        public Sprite AiAvatarIcon => _aiAvatarIcon;
        public Sprite UserAvatarIcon => _userAvatarIcon;
        public bool EnableStreaming => _enableStreaming;
        public string TypingIndicatorText => _typingIndicatorText;
        public int ChatWidth => _chatWidth;
        public int ChatHeight => _chatHeight;
        public bool SendOnShiftEnter => _sendOnShiftEnter;
        public int MaxMessageLength => _maxMessageLength;
        public string ErrorMessagePrefix => _errorMessagePrefix;
        public string TimeoutMessage => _timeoutMessage;
        public string NoResponseMessage => _noResponseMessage;
    }
}
