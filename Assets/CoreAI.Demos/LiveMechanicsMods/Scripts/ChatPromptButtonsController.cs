using System.Reflection;
using System.Threading;
using CoreAI.Chat;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.Demos
{
    /// <summary>
    /// Small IMGUI helper for demo scenes: preset prompts are inserted into the CoreAI chat input
    /// so users can inspect/edit them before sending.
    /// </summary>
    public sealed class ChatPromptButtonsController : MonoBehaviour
    {
        [System.Serializable]
        public sealed class PromptButton
        {
            public string Label = "Prompt";

            [TextArea(3, 12)] public string Prompt = "";
        }

        [SerializeField] private string title = "Prompt templates";

        [Tooltip("Width/height of the panel. It is auto-anchored to the bottom of the screen, " +
                 "just left of the chat, so it does not overlap the usage / mod panels.")]
        [SerializeField]
        private Rect panelRect = new(12, 420, 520, 180);

        [Tooltip("Horizontal space (px) reserved on the right for the chat panel.")] [SerializeField]
        private float chatReserveWidth = 700f;

        [SerializeField] private PromptButton[] prompts = System.Array.Empty<PromptButton>();

        [SerializeField] private bool submitWhenInputUnavailable;

        [Tooltip("Show or hide the prompt buttons panel.")] [SerializeField]
        private bool _showPanel = true;

        [Tooltip("Hotkey that toggles the panel at runtime. Set to None to disable the hotkey.")] [SerializeField]
        private KeyCode _toggleKey = KeyCode.F8;

        private CoreAiChatPanel _chatPanel;
        private string _status = "Click a prompt to insert it into chat.";

        private void Awake()
        {
            _chatPanel = FindFirstObjectByType<CoreAiChatPanel>();
        }

        private void Update()
        {
            if (_toggleKey != KeyCode.None && Input.GetKeyDown(_toggleKey))
            {
                _showPanel = !_showPanel;
            }
        }

        public void Configure(string panelTitle, Rect rect, PromptButton[] promptButtons)
        {
            title = panelTitle;
            panelRect = rect;
            prompts = promptButtons ?? System.Array.Empty<PromptButton>();
        }

        private void OnGUI()
        {
            if (prompts == null || prompts.Length == 0 || !_showPanel)
            {
                return;
            }

            // Anchor to the bottom of the screen, just left of the chat panel, so the prompt
            // buttons sit next to the chat and never overlap the usage / mod-manager panels.
            float w = Mathf.Min(panelRect.width, Screen.width - 24f);
            float h = panelRect.height;
            float x = Mathf.Clamp(Screen.width - chatReserveWidth - w - 12f, 12f,
                Mathf.Max(12f, Screen.width - w - 12f));
            float y = Mathf.Max(12f, Screen.height - h - 12f);
            Rect rect = new(x, y, w, h);

            GUILayout.BeginArea(rect, GUI.skin.box);
            if (GUI.Button(new Rect(w - 58f, 2f, 52f, 18f), "Hide"))
            {
                _showPanel = false;
            }

            GUILayout.Label($"<b>{title}</b> ({_toggleKey})", RichLabel());
            GUILayout.Label(_status, RichLabel());

            foreach (PromptButton prompt in prompts)
            {
                if (prompt == null || string.IsNullOrWhiteSpace(prompt.Prompt))
                {
                    continue;
                }

                if (GUILayout.Button(prompt.Label, GUILayout.Height(28)))
                {
                    InsertOrSubmit(prompt.Prompt);
                }
            }

            GUILayout.EndArea();
        }

        private void InsertOrSubmit(string prompt)
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

        private static GUIStyle RichLabel()
        {
            return new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true };
        }
    }
}