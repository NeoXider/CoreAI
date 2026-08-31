using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Composition;
using CoreAI.Infrastructure.Logging;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using VContainer;

namespace CoreAI.Presentation.PlayerChat
{
    /// <summary>
    /// Unity UI panel for in-game player chat.
    /// </summary>
    public sealed class InGameChatPanel : MonoBehaviour
    {
        [Tooltip("Player message input field (TextMeshPro).")]
        [SerializeField]
        private TMP_InputField inputField;

        [Tooltip("Conversation output text area (TextMeshPro).")]
        [SerializeField]
        private TMP_Text outputText;

        [Tooltip("Sends the current text to the LLM.")]
        [SerializeField]
        private Button sendButton;

        [Tooltip("Clears chat history on the chat service side.")]
        [SerializeField]
        private Button clearHistoryButton;

        private IInGameLlmChatService _chat;
        private ActorContext _chatActor;
        private IInGameLlmChatServiceFactory _chatFactory;
        private bool _hasChatActor;
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
                ResolveCurrentActorChat(_scope.Container);
            }
        }

        private void OnDestroy()
        {
            ReleaseCurrentActorChat();
        }

        /// <summary>Releases this panel's actor session from the retained chat factory.</summary>
        internal void ReleaseCurrentActorChat()
        {
            if (!_hasChatActor || _chatFactory == null)
            {
                return;
            }

            try
            {
                _chatFactory.ReleaseActor(_chatActor);
            }
            catch (System.ObjectDisposedException)
            {
            }

            _chat = null;
            _chatFactory = null;
            _hasChatActor = false;
        }

        /// <summary>Resolves the chat service for the actor currently admitted by the host.</summary>
        internal void ResolveCurrentActorChat(IObjectResolver resolver)
        {
            ReleaseCurrentActorChat();
            IActorIdentityProvider identityProvider = resolver.Resolve<IActorIdentityProvider>();
            ActorContext actor = identityProvider.GetActorContext(BuiltInAgentRoleIds.SmartChat);
            IInGameLlmChatServiceFactory factory = resolver.Resolve<IInGameLlmChatServiceFactory>();
            IInGameLlmChatService chat = factory.Resolve(actor);

            _chatActor = actor;
            _chatFactory = factory;
            _chat = chat;
            _hasChatActor = true;
        }

        /// <summary>Currently bound actor-owned service.</summary>
        internal IInGameLlmChatService BoundChatService => _chat;

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
                GameLoggerUnscopedFallback.Instance.LogError(GameLogFeature.Core,
                    $"[InGameChatPanel] Exception in OnSendClicked: {ex}");
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
