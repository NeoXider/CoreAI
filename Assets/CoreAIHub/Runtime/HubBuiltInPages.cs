using System;
using CoreAI.Ai;
using CoreAI.Chat;
using UnityEngine.UIElements;

namespace CoreAI.Hub.UI
{
    /// <summary>
    /// One-call registration for the built-in Hub pages (Chat, Settings, Statistics) into a
    /// <see cref="HubPageRegistry"/>. Pages are registered as lazy factories, so their content is only
    /// built when a tab is first activated. Every argument is optional and null-tolerant: a page without
    /// its data source still renders a short setup note instead of throwing.
    /// </summary>
    public static class HubBuiltInPages
    {
        /// <summary>Registry id of the built-in Chat page.</summary>
        public const string ChatPageId = HubChatPage.DefaultPageId;

        /// <summary>Registry id of the built-in Settings page.</summary>
        public const string SettingsPageId = HubSettingsPage.DefaultPageId;

        /// <summary>Registry id of the built-in Statistics page.</summary>
        public const string StatisticsPageId = HubStatisticsPage.DefaultPageId;

        /// <summary>
        /// Registers the Chat, Settings, and Statistics pages into <paramref name="registry"/>.
        /// </summary>
        /// <param name="registry">Target registry. Required.</param>
        /// <param name="chatTemplate">Chat UXML cloned by the Chat page (e.g. <c>CoreAiChat.uxml</c>).</param>
        /// <param name="chatStyleSheet">Optional chat stylesheet (e.g. <c>CoreAiChat.uss</c>).</param>
        /// <param name="chatConfig">Optional chat configuration asset (also shown on the Settings page).</param>
        /// <param name="settings">Optional host settings surfaced by Settings and Statistics.</param>
        /// <param name="metrics">Optional orchestration metrics surfaced by Statistics.</param>
        /// <param name="chatStopGenerationOnEscape">
        /// Opt-in, off by default: forwarded to <see cref="HubChatPage.StopGenerationOnEscape"/>. See that
        /// property for the default-vs-opt-in Escape behavior.
        /// </param>
        public static void RegisterAll(
            HubPageRegistry registry,
            VisualTreeAsset chatTemplate = null,
            StyleSheet chatStyleSheet = null,
            CoreAiChatConfig chatConfig = null,
            ICoreAISettings settings = null,
            InMemoryAiOrchestrationMetrics metrics = null,
            bool chatStopGenerationOnEscape = false)
        {
            if (registry == null)
            {
                throw new ArgumentNullException(nameof(registry));
            }

            registry.Register(
                ChatPageId,
                () => new HubChatPage(
                    chatTemplate, chatStyleSheet, chatConfig,
                    stopGenerationOnEscape: chatStopGenerationOnEscape),
                0);

            registry.Register(
                SettingsPageId,
                () => new HubSettingsPage(settings, chatConfig),
                100);

            registry.Register(
                StatisticsPageId,
                () => new HubStatisticsPage(metrics, settings),
                200);
        }
    }
}
