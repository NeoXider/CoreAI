using System;
using CoreAI.Chat;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.Hub.UI
{
    /// <summary>
    /// Built-in Hub page that hosts the existing <see cref="CoreAiChatPanel"/> inside a Hub tab.
    /// It does not rebuild the chat: <see cref="CoreAiChatPanel.CreateEmbedded"/> instantiates the
    /// standard chat UXML into this page's content element and drives it with a real chat panel, so
    /// streaming, tools, history, and hotkeys behave exactly as the standalone chat.
    /// </summary>
    public sealed class HubChatPage : IHubPage
    {
        /// <summary>Default registry id for the built-in Chat page.</summary>
        public const string DefaultPageId = "coreai.hub.chat";

        private readonly VisualTreeAsset _chatTemplate;
        private readonly StyleSheet _chatStyleSheet;
        private readonly CoreAiChatConfig _chatConfig;

        private CoreAiChatPanel _panel;
        private VisualElement _host;

        /// <summary>Creates the Chat page.</summary>
        /// <param name="chatTemplate">
        /// Chat UXML (e.g. <c>CoreAiChat.uxml</c>) cloned to build the panel. When null the page shows a
        /// short setup note instead of the chat, so a mis-wired Hub never throws.
        /// </param>
        /// <param name="chatStyleSheet">Optional chat stylesheet (e.g. <c>CoreAiChat.uss</c>).</param>
        /// <param name="chatConfig">Optional chat configuration asset.</param>
        /// <param name="pageId">Registry id.</param>
        /// <param name="displayName">Tab label.</param>
        /// <param name="order">Sort order (lower is first).</param>
        public HubChatPage(
            VisualTreeAsset chatTemplate = null,
            StyleSheet chatStyleSheet = null,
            CoreAiChatConfig chatConfig = null,
            string pageId = DefaultPageId,
            string displayName = "Chat",
            int order = 0)
        {
            _chatTemplate = chatTemplate;
            _chatStyleSheet = chatStyleSheet;
            _chatConfig = chatConfig;
            PageId = string.IsNullOrWhiteSpace(pageId) ? DefaultPageId : pageId;
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? "Chat" : displayName;
            Order = order;
        }

        /// <inheritdoc />
        public string PageId { get; }

        /// <inheritdoc />
        public string DisplayName { get; }

        /// <inheritdoc />
        public int Order { get; }

        /// <inheritdoc />
        public Func<object> CreatePageContent => BuildContent;

        /// <inheritdoc />
        public void OnActivated()
        {
        }

        /// <inheritdoc />
        public void OnDeactivated()
        {
        }

        /// <inheritdoc />
        public void OnDestroyed()
        {
            if (_panel != null)
            {
                UnityEngine.Object.Destroy(_panel.gameObject);
                _panel = null;
            }

            _host = null;
        }

        private object BuildContent()
        {
            _host = new VisualElement { name = "coreai-hub-chat-host" };
            _host.style.flexGrow = 1f;

            // Bleed the Hub content's 16px padding so the chat fills the whole tab edge-to-edge (no empty
            // strip on the right/bottom). The chat has its own internal header/input padding, so it still
            // reads well flush to the content edges.
            _host.style.marginLeft = -16f;
            _host.style.marginRight = -16f;
            _host.style.marginTop = -16f;
            _host.style.marginBottom = -16f;

            if (_chatTemplate == null)
            {
                _host.Add(HubPageWidgets.MakeTitle(DisplayName));
                _host.Add(HubPageWidgets.MakeNote(
                    "Chat UXML template is not assigned. Assign the chat UXML (CoreAiChat.uxml) on the Hub " +
                    "wiring component so the Chat tab can embed the CoreAiChatPanel."));
                return _host;
            }

            _panel = CoreAiChatPanel.CreateEmbedded(_host, _chatTemplate, _chatStyleSheet, _chatConfig);

            // The Hub chat always offers the agent/role dropdown in its header so the player can switch
            // between the conversational role and the Programmer role that carries the mod tools — i.e.
            // write mods straight from the Hub chat, not just the standalone chat.
            if (_panel != null)
            {
                _panel.EnableAgentSwitching();
            }

            return _host;
        }
    }
}
