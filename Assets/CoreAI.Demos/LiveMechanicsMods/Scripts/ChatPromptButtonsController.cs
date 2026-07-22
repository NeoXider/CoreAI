using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using CoreAI.Chat;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.Demos
{
    /// <summary>
    /// GUI-less driver holding a serialized list of preset prompts that can be inserted into the CoreAI
    /// chat input via <see cref="InsertOrSubmit"/>. Legacy: demo prompt templates now live in the chat's
    /// own "≡" example menu (see <c>CoreAiChatPanel.EnableExamplePrompts</c>); this component is retained
    /// for demo scenes not yet migrated to that single prompt location.
    /// </summary>
    public sealed class ChatPromptButtonsController : MonoBehaviour
    {
        [System.Serializable]
        public sealed class PromptButton
        {
            public string Label = "Prompt";

            [TextArea(3, 12)]
            public string Prompt = "";
        }

        [SerializeField]
        private string title = "Prompt templates";

        [SerializeField]
        private PromptButton[] prompts = System.Array.Empty<PromptButton>();

        [SerializeField]
        private bool submitWhenInputUnavailable;

        private CoreAiChatPanel _chatPanel;
        private string _status = "Click a prompt to insert it into chat.";

        /// <summary>Panel title shown by the Hub page.</summary>
        public string Title => title;

        /// <summary>The preset prompts rendered by the Hub page.</summary>
        public IReadOnlyList<PromptButton> Prompts => prompts;

        /// <summary>Last insertion status, surfaced by the Hub page.</summary>
        public string Status => _status;

        private void Awake()
        {
            _chatPanel = FindFirstObjectByType<CoreAiChatPanel>();
        }

        public void Configure(string panelTitle, PromptButton[] promptButtons)
        {
            title = panelTitle;
            prompts = promptButtons ?? System.Array.Empty<PromptButton>();
        }

        /// <summary>Inserts <paramref name="prompt"/> into the chat input (or submits it as a fallback).</summary>
        public void InsertOrSubmit(string prompt)
        {
            if (_chatPanel == null)
            {
                _chatPanel = FindFirstObjectByType<CoreAiChatPanel>();
            }

            if (_chatPanel == null)
            {
                _status = "CoreAiChatPanel not found.";
                return;
            }

            _chatPanel.SetCollapsed(false, true);
            if (TrySetInputField(_chatPanel, prompt))
            {
                _status = "Prompt inserted. Review it and press send.";
                return;
            }

            if (!submitWhenInputUnavailable)
            {
                _status = "Chat input is not initialized yet. Open chat and try again.";
                return;
            }

            _ = _chatPanel.SubmitMessageFromExternalAsync(
                prompt,
                new CoreAiChatExternalSubmitOptions { AppendUserMessageToChat = true },
                CancellationToken.None);
            _status = "Chat input unavailable; prompt submitted directly.";
        }

        private static bool TrySetInputField(CoreAiChatPanel panel, string text)
        {
            FieldInfo field = typeof(CoreAiChatPanel).GetField(
                "InputField",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (field?.GetValue(panel) is not TextField input)
            {
                return false;
            }

            input.value = text ?? "";
            input.schedule.Execute(() => input.Focus());
            return true;
        }
    }
}
