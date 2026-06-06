using CoreAI.Ai;
using CoreAI.Composition;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CoreAI.Presentation.PlayerChat
{
    /// <summary>
    /// Unity UI panel for in-game player chat.
    /// </summary>
    public sealed class InGameChatPanel : MonoBehaviour
    {
        [Tooltip("Player message input field (TextMeshPro).")] [SerializeField]
        private TMP_InputField inputField;

        [Tooltip("Conversation output text area (TextMeshPro).")] [SerializeField]
        private TMP_Text outputText;

        [Tooltip("Sends the current text to the LLM.")] [SerializeField]
        private Button sendButton;

        [Tooltip("Clears chat history on the chat service side.")] [SerializeField]
        private Button clearHistoryButton;

        private IInGameLlmChatService _chat;
        private CoreAILifetimeScope _scope;

        private void Awake()
        {
            _scope = FindAnyObjectByType<CoreAILifetimeScope>();
        }

        private void OnEnable()
        {
            if (sendButton != null)
            {
                sendButton.onClick.AddListener(OnSendClicked);
            }

            if (clearHistoryButton != null)
            {
                clearHistoryButton.onClick.AddListener(OnClearClicked);
            }
        }

        private void OnDisable()
        {
            if (sendButton != null)
            {
                sendButton.onClick.RemoveListener(OnSendClicked);
            }

            if (clearHistoryButton != null)
            {
                clearHistoryButton.onClick.RemoveListener(OnClearClicked);
            }
        }

        private void Start()
        {
            if (_scope != null)
            {
                _chat = (IInGameLlmChatService)_scope.Container.Resolve(typeof(IInGameLlmChatService));
            }
        }

        private async void OnSendClicked()
        {
            try
            {
                if (_chat == null)
                {
                    AppendLine("[CoreAI] Chat service is not ready (missing CoreAILifetimeScope?).");
                    return;
                }

                string msg = inputField != null ? inputField.text.Trim() : string.Empty;
                if (string.IsNullOrEmpty(msg))
                {
                    return;
                }

                AppendLine("You: " + msg);
                if (inputField != null)
                {
                    inputField.text = string.Empty;
                }

                LlmCompletionResult result = await _chat.SendPlayerMessageAsync(msg);
                if (result.Ok)
                {
                    AppendLine("Assistant: " + result.Content);
                }
                else
                {
                    AppendLine("[error] " + result.Error);
                }
            }
            catch (System.Exception ex)
            {
                AppendLine("[error] " + ex.Message);
                Debug.LogError($"[InGameChatPanel] Exception in OnSendClicked: {ex}");
            }
        }

        private void OnClearClicked()
        {
            _chat?.ClearHistory();
            if (outputText != null)
            {
                outputText.text = string.Empty;
            }
        }

        private void AppendLine(string line)
        {
            if (outputText == null)
            {
                return;
            }

            outputText.text += line + "\n";
        }
    }
}