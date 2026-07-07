using System;
using System.Globalization;
using CoreAI.Chat;
using UnityEngine.UIElements;

namespace CoreAI.Hub.UI
{
    /// <summary>
    /// Built-in Hub page that shows the current backend / LLM configuration read-only. It reads the
    /// host <see cref="ICoreAISettings"/> (context window, timeouts, retries, streaming, pricing) and the
    /// <see cref="CoreAiChatConfig"/> (agent role, UI options). H3 is display-only; editing lands later.
    /// </summary>
    public sealed class HubSettingsPage : IHubPage
    {
        /// <summary>Default registry id for the built-in Settings page.</summary>
        public const string DefaultPageId = "coreai.hub.settings";

        private readonly ICoreAISettings _settings;
        private readonly CoreAiChatConfig _chatConfig;

        /// <summary>Creates the Settings page from optional live config sources (null-tolerant).</summary>
        public HubSettingsPage(
            ICoreAISettings settings = null,
            CoreAiChatConfig chatConfig = null,
            string pageId = DefaultPageId,
            string displayName = "Settings",
            int order = 100)
        {
            _settings = settings;
            _chatConfig = chatConfig;
            PageId = string.IsNullOrWhiteSpace(pageId) ? DefaultPageId : pageId;
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? "Settings" : displayName;
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
        }

        private object BuildContent()
        {
            ScrollView scroll = HubPageWidgets.CreatePage(DisplayName, out VisualElement body);

            if (_settings == null && _chatConfig == null)
            {
                body.Add(HubPageWidgets.MakeNote(
                    "No settings source is wired. Pass an ICoreAISettings and/or CoreAiChatConfig to " +
                    "HubBuiltInPages.RegisterAll to display the active backend configuration."));
                return scroll;
            }

            if (_settings != null)
            {
                body.Add(HubPageWidgets.MakeSection("LLM / Backend"));
                body.Add(HubPageWidgets.MakeRow("Context window", Tokens(_settings.ContextWindowTokens)));
                body.Add(HubPageWidgets.MakeRow("Max output tokens",
                    _settings.MaxTokens > 0 ? Tokens(_settings.MaxTokens) : "provider default"));
                body.Add(HubPageWidgets.MakeRow("Request timeout",
                    _settings.LlmRequestTimeoutSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s"));
                body.Add(HubPageWidgets.MakeRow("Streaming", OnOff(_settings.EnableStreaming)));
                body.Add(HubPageWidgets.MakeRow("Temperature",
                    _settings.OverrideTemperature
                        ? _settings.Temperature.ToString("0.##", CultureInfo.InvariantCulture)
                        : "provider default"));
                body.Add(HubPageWidgets.MakeRow("Max request retries",
                    _settings.MaxLlmRequestRetries.ToString(CultureInfo.InvariantCulture)));
                body.Add(HubPageWidgets.MakeRow("Max tool-call roundtrips",
                    _settings.MaxToolCallRoundtrips == 0
                        ? "unlimited"
                        : _settings.MaxToolCallRoundtrips.ToString(CultureInfo.InvariantCulture)));

                body.Add(HubPageWidgets.MakeSection("Token pricing (per 1K)"));
                body.Add(HubPageWidgets.MakeRow("Input", Price(_settings.InputTokenPricePer1KUsd)));
                body.Add(HubPageWidgets.MakeRow("Output", Price(_settings.OutputTokenPricePer1KUsd)));

                body.Add(HubPageWidgets.MakeSection("Logging"));
                body.Add(HubPageWidgets.MakeRow("Token usage", OnOff(_settings.LogTokenUsage)));
                body.Add(HubPageWidgets.MakeRow("LLM latency", OnOff(_settings.LogLlmLatency)));
                body.Add(HubPageWidgets.MakeRow("Tool calls", OnOff(_settings.LogToolCalls)));
            }

            if (_chatConfig != null)
            {
                body.Add(HubPageWidgets.MakeSection("Chat"));
                body.Add(HubPageWidgets.MakeRow("Agent role", Value(_chatConfig.RoleId)));
                body.Add(HubPageWidgets.MakeRow("Header title", Value(_chatConfig.HeaderTitle)));
                body.Add(HubPageWidgets.MakeRow("UI streaming", OnOff(_chatConfig.EnableStreaming)));
                body.Add(HubPageWidgets.MakeRow("Show tool calls", OnOff(_chatConfig.ShowToolCallsInChat)));
                body.Add(HubPageWidgets.MakeRow("Max message length",
                    _chatConfig.MaxMessageLength > 0
                        ? _chatConfig.MaxMessageLength.ToString(CultureInfo.InvariantCulture) + " chars"
                        : "unlimited"));
                body.Add(HubPageWidgets.MakeRow("Agent switching", OnOff(_chatConfig.AllowAgentSwitching)));
            }

            body.Add(HubPageWidgets.MakeNote("Read-only view. Editing arrives in a later phase."));
            return scroll;
        }

        private static string Value(string text)
        {
            return string.IsNullOrEmpty(text) ? "-" : text;
        }

        private static string OnOff(bool value)
        {
            return value ? "On" : "Off";
        }

        private static string Tokens(int tokens)
        {
            return tokens.ToString("N0", CultureInfo.InvariantCulture) + " tok";
        }

        private static string Price(float pricePer1K)
        {
            return pricePer1K > 0f
                ? "$" + pricePer1K.ToString("0.####", CultureInfo.InvariantCulture)
                : "unset";
        }
    }
}
