using CoreAI.Ai;
using CoreAI.Composition;
using UnityEngine;
using UnityEngine.UI;

namespace CoreAI.Presentation.PlayerChat
{
    /// <summary>
    /// Простая панель «чат с GPT» в игре: привяжите UI InputField / Text / Button.
    /// </summary>
    public sealed class InGameChatPanel : MonoBehaviour
    {
        [Tooltip("Поле ввода сообщения игрока.")]
        [SerializeField]
        private InputField inputField;

        [Tooltip("Куда выводить историю диалога.")]
        [SerializeField]
        private Text outputText;

        [Tooltip("Отправить текущий текст в LLM.")]
        [SerializeField]
        private Button sendButton;

        [Tooltip("Очистить историю на стороне сервиса чата.")]
        [SerializeField]
        private Button clearHistoryButton;

        private IInGameLlmChatService _chat;
        private CoreAILifetimeScope _scope;

        private void Awake()
        {
            _scope = FindFirstObjectByType<CoreAILifetimeScope>();
        }

        private void OnEnable()
        {
            if (sendButton != null)
                sendButton.onClick.AddListener(OnSendClicked);
            if (clearHistoryButton != null)
                clearHistoryButton.onClick.AddListener(OnClearClicked);
        }

        private void OnDisable()
        {
            if (sendButton != null)
                sendButton.onClick.RemoveListener(OnSendClicked);
            if (clearHistoryButton != null)
                clearHistoryButton.onClick.RemoveListener(OnClearClicked);
        }

        private void Start()
        {
            if (_scope != null)
                _chat = (IInGameLlmChatService)_scope.Container.Resolve(typeof(IInGameLlmChatService));
        }

        private async void OnSendClicked()
        {
            if (_chat == null)
            {
                AppendLine("[CoreAI] Чат: контейнер не готов (нет CoreAILifetimeScope?).");
                return;
            }

            var msg = inputField != null ? inputField.text.Trim() : string.Empty;
            if (string.IsNullOrEmpty(msg))
                return;

            AppendLine("You: " + msg);
            if (inputField != null)
                inputField.text = string.Empty;

            var result = await _chat.SendPlayerMessageAsync(msg);
            if (result.Ok)
                AppendLine("Assistant: " + result.Content);
            else
                AppendLine("[error] " + result.Error);
        }

        private void OnClearClicked()
        {
            _chat?.ClearHistory();
            if (outputText != null)
                outputText.text = string.Empty;
        }

        private void AppendLine(string line)
        {
            if (outputText == null)
                return;
            outputText.text += line + "\n";
        }
    }
}
