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
    public sealed class HubChatPage : HubPageBase, IHubFullBleedPage, IHubEscapeHandler
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
        /// <param name="stopGenerationOnEscape">
        /// Opt-in: when <c>true</c>, Escape stops an in-flight AI turn instead of letting the Hub
        /// collapse (consuming the key-press once). Default <c>false</c> — Escape always collapses the
        /// Hub immediately and any in-progress generation keeps running in the background; the answer
        /// still lands normally once the Hub is expanded again.
        /// </param>
        public HubChatPage(
            VisualTreeAsset chatTemplate = null,
            StyleSheet chatStyleSheet = null,
            CoreAiChatConfig chatConfig = null,
            string pageId = DefaultPageId,
            string displayName = "Chat",
            int order = 0,
            bool stopGenerationOnEscape = false)
            : base(
                string.IsNullOrWhiteSpace(pageId) ? DefaultPageId : pageId,
                string.IsNullOrWhiteSpace(displayName) ? "Chat" : displayName,
                order)
        {
            _chatTemplate = chatTemplate;
            _chatStyleSheet = chatStyleSheet;
            _chatConfig = chatConfig;
            StopGenerationOnEscape = stopGenerationOnEscape;
        }

        /// <summary>
        /// Opt-in switch (default <c>false</c>) for whether Escape stops an in-flight AI turn instead of
        /// letting the Hub collapse. Settable after construction so host code can flip it at runtime
        /// (e.g. a Settings toggle) without rebuilding the page.
        /// </summary>
        public bool StopGenerationOnEscape { get; set; }

        /// <inheritdoc />
        public override Func<object> CreatePageContent => BuildContent;

        /// <inheritdoc />
        public override void OnActivated()
        {
            // WHY: the embedded panel's own collapse button/Esc could otherwise leave the chat
            // collapsed inside the tab (invisible but still "active") the next time this tab is shown.
            _panel?.SetCollapsed(false, false);
        }

        /// <inheritdoc />
        public override void OnDestroyed()
        {
            if (_panel != null)
            {
                UnityEngine.Object.Destroy(_panel.gameObject);
                _panel = null;
            }

            _host = null;
        }

        /// <summary>
        /// When <see cref="StopGenerationOnEscape"/> is off (default), always returns <c>false</c> so
        /// Escape falls straight through to the Hub's collapse behavior — an in-progress generation is
        /// left running in the background and its answer lands normally once the Hub reopens. When on,
        /// stops an in-flight AI turn on the first Escape (consuming it, the Hub stays expanded) and lets
        /// a second Escape collapse the Hub as usual.
        /// </summary>
        public bool TryHandleEscape()
        {
            if (!StopGenerationOnEscape || _panel == null || !_panel.IsRequestInProgress)
            {
                return false;
            }

            if (_panel.EffectiveEnableStopGeneration)
            {
                _panel.StopAgent();
            }

            return true;
        }

        private object BuildContent()
        {
            // WHY: no inner padding here — the Hub content area drops its padding for the chat (see
            // CoreAiHubWindow full-bleed handling) so the chat reaches all four edges evenly.
            _host = new VisualElement { name = "coreai-hub-chat-host" };
            _host.style.flexGrow = 1f;

            if (_chatTemplate == null)
            {
                _host.Add(HubPageWidgets.MakeTitle(DisplayName));
                _host.Add(HubPageWidgets.MakeNote(
                    "Chat UXML template is not assigned. Assign the chat UXML (CoreAiChat.uxml) on the Hub " +
                    "wiring component so the Chat tab can embed the CoreAiChatPanel."));
                return _host;
            }

            _panel = CoreAiChatPanel.CreateEmbedded(_host, _chatTemplate, _chatStyleSheet, _chatConfig);

            // WHY: Escape belongs to the Hub (see CoreAiHubWindow.OnRootKeyDown / TryHandleEscape above).
            // Without this the embedded panel's own Escape handling collapses ITSELF inside the tab —
            // the chat visually "disappears" while the Hub window stays open.
            _panel?.SetRuntimeEscapeChatShortcutsEnabled(false);

            // WHY: the Hub chat always offers the agent/role dropdown in its header so the player can switch
            // between the conversational role and the Programmer role that carries the mod tools — i.e.
            // write mods straight from the Hub chat, not just the standalone chat.
            if (_panel != null)
            {
                _panel.EnableAgentSwitching();
                _panel.EnableApiSwitching();

                // WHY: the Hub chat always surfaces tool-call progress so the player can see mod/tool actions
                // execute inline. This overrides the shared chat config for the embedded panel only — the
                // standalone chat keeps its own ShowToolCallsInChat setting.
                CoreAiChatOptions hubOptions = _chatConfig != null
                    ? CoreAiChatOptions.From(_chatConfig)
                    : CoreAiChatOptions.CreateDefault();
                hubOptions.ShowToolCallsInChat = true;
                _panel.SetRuntimeOptions(hubOptions);
            }

            return _host;
        }
    }
}
