using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Chat;
using CoreAI.Infrastructure.Llm;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.Hub.UI
{
    /// <summary>
    /// Built-in Hub page for runtime AI backend settings. The page exposes the same backend-switching
    /// surface as <see cref="CoreAiBackend"/>: Auto, LLMUnity, HTTP API, and Offline, plus an inline
    /// health probe. API keys are write-only in the UI.
    /// </summary>
    public sealed class HubSettingsPage : HubPageBase
    {
        /// <summary>Default registry id for the built-in Settings page.</summary>
        public const string DefaultPageId = "coreai.hub.settings";

        private static readonly List<string> ModeOptions = new()
        {
            "Auto",
            "LLMUnity (local)",
            "HTTP API",
            "Offline"
        };

        private readonly ICoreAISettings _settings;
        private readonly CoreAiChatConfig _chatConfig;

        private DropdownField _mode;
        private VisualElement _httpGroup;
        private VisualElement _localGroup;
        private VisualElement _limitsGroup;
        private TextField _baseUrl;
        private TextField _apiKey;
        private TextField _model;
        private TextField _ggufModelPath;
        private DropdownField _ggufModel;
        private const string GgufAutoLabel = "[ Auto / Fallback ]";
        private IntegerField _gpuLayers;
        private Toggle _overrideTemperature;
        private FloatField _temperature;
        private IntegerField _timeoutSeconds;
        private IntegerField _maxTokens;
        private Label _status;
        private Label _health;
        private Button _applyButton;
        private Button _testButton;
        private bool _busy;
        private bool _subscribed;

        /// <summary>Creates the Settings page from optional live config sources (null-tolerant).</summary>
        public HubSettingsPage(
            ICoreAISettings settings = null,
            CoreAiChatConfig chatConfig = null,
            string pageId = DefaultPageId,
            string displayName = "AI Settings",
            int order = 100)
            : base(
                string.IsNullOrWhiteSpace(pageId) ? DefaultPageId : pageId,
                string.IsNullOrWhiteSpace(displayName) ? "AI Settings" : displayName,
                order)
        {
            _settings = settings;
            _chatConfig = chatConfig;
        }

        /// <inheritdoc />
        public override Func<object> CreatePageContent => BuildContent;

        /// <inheritdoc />
        public override void OnActivated()
        {
            RefreshFromStatus();
        }

        /// <inheritdoc />
        public override void OnDestroyed()
        {
            if (_subscribed)
            {
                CoreAiBackend.OnBackendChanged -= HandleBackendChanged;
                _subscribed = false;
            }
        }

        private object BuildContent()
        {
            ScrollView scroll = HubPageWidgets.CreatePage(DisplayName, out VisualElement body);
            scroll.AddToClassList("coreai-hub-page");
            body.AddToClassList("coreai-hub-page-body");

            if (!_subscribed)
            {
                CoreAiBackend.OnBackendChanged += HandleBackendChanged;
                _subscribed = true;
            }

            CoreAISettingsAsset asset = ResolveSettingsAsset();
            if (_settings == null && asset == null)
            {
                body.Add(HubPageWidgets.MakeNote(
                    "No CoreAI settings asset is wired. Add a Resources/CoreAISettings asset or pass " +
                    "ICoreAISettings to HubBuiltInPages.RegisterAll."));
                return scroll;
            }

            body.Add(HubPageWidgets.MakeSection("Backend"));
            _mode = new DropdownField("Mode", ModeOptions, 0);
            StyleField(_mode);
            _mode.RegisterValueChangedCallback(_ => RefreshInteractivity());
            body.Add(_mode);

            _httpGroup = MakeGroup("HTTP API");
            _baseUrl = new TextField("API base URL");
            StyleField(_baseUrl);
            _httpGroup.Add(_baseUrl);

            _apiKey = new TextField("API key");
            _apiKey.isPasswordField = true;
            _apiKey.maskChar = '*';
            StyleField(_apiKey);
            _httpGroup.Add(_apiKey);

            _model = new TextField("HTTP model");
            StyleField(_model);
            _httpGroup.Add(_model);
            body.Add(_httpGroup);

            _localGroup = MakeGroup("LLMUnity");
            _ggufModel = MakeGgufModelDropdown();
            _localGroup.Add(_ggufModel);

            _ggufModelPath = new TextField("GGUF model path (manual override)");
            StyleField(_ggufModelPath);
            _localGroup.Add(_ggufModelPath);

            _gpuLayers = new IntegerField("GPU layers");
            StyleField(_gpuLayers);
            _localGroup.Add(_gpuLayers);
            body.Add(_localGroup);

            _limitsGroup = MakeGroup("Request limits");
            _overrideTemperature = new Toggle("Override temperature")
            {
                tooltip = "When off, the model's default sampling temperature is used. " +
                          "When on, the value below is sent to the backend."
            };
            StyleField(_overrideTemperature);
            _overrideTemperature.RegisterValueChangedCallback(_ => UpdateTemperatureInteractivity());
            _limitsGroup.Add(_overrideTemperature);

            _temperature = new FloatField("Temperature");
            StyleField(_temperature);
            _limitsGroup.Add(_temperature);

            _timeoutSeconds = new IntegerField("HTTP timeout seconds");
            StyleField(_timeoutSeconds);
            _limitsGroup.Add(_timeoutSeconds);

            _maxTokens = new IntegerField("Max output tokens");
            StyleField(_maxTokens);
            _limitsGroup.Add(_maxTokens);
            body.Add(_limitsGroup);

            VisualElement actions = new();
            actions.AddToClassList("coreai-hub-actions");
            actions.style.flexDirection = FlexDirection.Row;
            actions.style.flexWrap = Wrap.Wrap;
            _applyButton = MakeButton("Apply", Apply);
            _testButton = MakeButton("Test backend", TestBackend);
            actions.Add(_applyButton);
            actions.Add(_testButton);
            body.Add(actions);

            _status = MakeStatusLabel();
            body.Add(_status);
            _health = HubPageWidgets.MakeNote("");
            body.Add(_health);

            AddReadOnlySummary(body);
            RefreshFromStatus();
            return scroll;
        }

        private void AddReadOnlySummary(VisualElement body)
        {
            if (_settings == null)
            {
                return;
            }

            body.Add(HubPageWidgets.MakeSection("Current chat + logging"));
            body.Add(HubPageWidgets.MakeRow("Context window", Tokens(_settings.ContextWindowTokens)));
            body.Add(HubPageWidgets.MakeRow("Streaming", OnOff(_settings.EnableStreaming)));
            body.Add(HubPageWidgets.MakeRow("Max request retries",
                _settings.MaxLlmRequestRetries.ToString(CultureInfo.InvariantCulture)));
            body.Add(HubPageWidgets.MakeRow("Max tool-call roundtrips",
                _settings.MaxToolCallRoundtrips == 0
                    ? "unlimited"
                    : _settings.MaxToolCallRoundtrips.ToString(CultureInfo.InvariantCulture)));
            body.Add(HubPageWidgets.MakeRow("Token usage logging", OnOff(_settings.LogTokenUsage)));
            body.Add(HubPageWidgets.MakeRow("Tool-call logging", OnOff(_settings.LogToolCalls)));

            if (_chatConfig != null)
            {
                body.Add(HubPageWidgets.MakeSection("Chat UI"));
                body.Add(HubPageWidgets.MakeRow("Agent role", Value(_chatConfig.RoleId)));
                body.Add(HubPageWidgets.MakeRow("Header title", Value(_chatConfig.HeaderTitle)));
                body.Add(HubPageWidgets.MakeRow("UI streaming", OnOff(_chatConfig.EnableStreaming)));
                body.Add(HubPageWidgets.MakeRow("Show tool calls", OnOff(_chatConfig.ShowToolCallsInChat)));
            }
        }

        private void Apply()
        {
            if (_busy)
            {
                return;
            }

            LlmExecutionMode mode = SelectedMode();
            bool live;
            switch (mode)
            {
                case LlmExecutionMode.LocalModel:
                    live = CoreAiBackend.ApplyLlmUnity(
                        string.IsNullOrWhiteSpace(_ggufModelPath.value) ? null : _ggufModelPath.value.Trim(),
                        null,
                        _gpuLayers.value < 0 ? 0 : _gpuLayers.value);
                    break;
                case LlmExecutionMode.Offline:
                    live = CoreAiBackend.ApplyOffline();
                    break;
                case LlmExecutionMode.Auto:
                    live = CoreAiBackend.ApplyAuto();
                    break;
                default:
                    bool overrideTemp = _overrideTemperature.value;
                    live = CoreAiBackend.ApplyHttpApi(
                        (_baseUrl.value ?? "").Trim(),
                        ResolveApiKey(),
                        (_model.value ?? "").Trim(),
                        overrideTemp ? Mathf.Clamp(_temperature.value, 0f, 2f) : null,
                        _timeoutSeconds.value <= 0 ? null : _timeoutSeconds.value,
                        _maxTokens.value <= 0 ? null : _maxTokens.value,
                        overrideTemp);
                    break;
            }

            CoreAiBackendStatus status = CoreAiBackend.Status;
            RefreshFromStatus();
            _status.text = live
                ? "Applied live: " + status
                : "Saved to settings; no live CoreAI scope is running: " + status;
            _health.text = "";
        }

        private async void TestBackend()
        {
            if (_busy)
            {
                return;
            }

            _busy = true;
            SetButtonsEnabled(false);
            _health.text = "Testing backend...";
            try
            {
                CoreAiBackendHealth health = await CoreAiBackend.VerifyAsync(30);
                _health.text = health.Ok
                    ? string.Format(
                        CultureInfo.InvariantCulture,
                        "Health OK: {0} / {1} in {2:0} ms.",
                        health.Mode,
                        Value(health.Model),
                        health.LatencyMs)
                    : "Health failed: " + Value(health.Error);
            }
            finally
            {
                _busy = false;
                SetButtonsEnabled(true);
            }
        }

        private void RefreshFromStatus()
        {
            if (_mode == null)
            {
                return;
            }

            CoreAiBackendStatus status = CoreAiBackend.Status;
            CoreAISettingsAsset asset = ResolveSettingsAsset();

            _mode.SetValueWithoutNotify(ModeToOption(status.Mode));
            _baseUrl.SetValueWithoutNotify(status.BaseUrl);
            _apiKey.SetValueWithoutNotify("");
            _model.SetValueWithoutNotify(status.Model);
            _ggufModelPath.SetValueWithoutNotify(status.GgufModelPath);
            SyncGgufModelDropdown(status.GgufModelPath);

            if (asset != null)
            {
                _gpuLayers.SetValueWithoutNotify(asset.NumGPULayers);
                _overrideTemperature.SetValueWithoutNotify(asset.OverrideTemperature);
                _temperature.SetValueWithoutNotify(asset.Temperature);
                _timeoutSeconds.SetValueWithoutNotify(asset.RequestTimeoutSeconds);
                _maxTokens.SetValueWithoutNotify(asset.MaxTokens);
            }
            else if (_settings != null)
            {
                _overrideTemperature.SetValueWithoutNotify(_settings.OverrideTemperature);
                _temperature.SetValueWithoutNotify(_settings.Temperature);
                _timeoutSeconds.SetValueWithoutNotify((int)Math.Round(_settings.LlmRequestTimeoutSeconds));
                _maxTokens.SetValueWithoutNotify(_settings.MaxTokens);
            }

            UpdateTemperatureInteractivity();

            _status.text = status.IsLive ? "Live backend: " + status : "Settings only: " + status;
            RefreshInteractivity();
        }

        private void RefreshInteractivity()
        {
            if (_mode == null)
            {
                return;
            }

            LlmExecutionMode mode = SelectedMode();
            bool http = mode == LlmExecutionMode.ClientOwnedApi;
            bool local = mode == LlmExecutionMode.LocalModel;
            SetVisible(_httpGroup, http);
            SetVisible(_localGroup, local);
            SetVisible(_limitsGroup, http);
        }

        private void UpdateTemperatureInteractivity()
        {
            if (_temperature == null || _overrideTemperature == null)
            {
                return;
            }

            _temperature.SetEnabled(_overrideTemperature.value);
        }

        private void HandleBackendChanged(CoreAiBackendStatus status)
        {
            Task.Yield().GetAwaiter().OnCompleted(RefreshFromStatus);
        }

        private LlmExecutionMode SelectedMode()
        {
            return _mode != null ? OptionToMode(_mode.value) : LlmExecutionMode.Auto;
        }

        private string ResolveApiKey()
        {
            string typed = _apiKey != null ? _apiKey.value : "";
            if (!string.IsNullOrEmpty(typed))
            {
                return typed;
            }

            CoreAISettingsAsset asset = ResolveSettingsAsset();
            return asset != null ? asset.ApiKey : "";
        }

        private CoreAISettingsAsset ResolveSettingsAsset()
        {
            return _settings as CoreAISettingsAsset ?? CoreAISettingsAsset.Instance;
        }

        private static string ModeToOption(LlmExecutionMode mode)
        {
            switch (mode)
            {
                case LlmExecutionMode.LocalModel:
                    return ModeOptions[1];
                case LlmExecutionMode.ClientOwnedApi:
                case LlmExecutionMode.ClientLimited:
                case LlmExecutionMode.ServerManagedApi:
                    return ModeOptions[2];
                case LlmExecutionMode.Offline:
                    return ModeOptions[3];
                default:
                    return ModeOptions[0];
            }
        }

        private static LlmExecutionMode OptionToMode(string option)
        {
            if (string.Equals(option, ModeOptions[1], StringComparison.Ordinal))
            {
                return LlmExecutionMode.LocalModel;
            }

            if (string.Equals(option, ModeOptions[2], StringComparison.Ordinal))
            {
                return LlmExecutionMode.ClientOwnedApi;
            }

            if (string.Equals(option, ModeOptions[3], StringComparison.Ordinal))
            {
                return LlmExecutionMode.Offline;
            }

            return LlmExecutionMode.Auto;
        }

        private static Button MakeButton(string text, Action clicked)
        {
            Button button = new(clicked) { text = text };
            button.AddToClassList("coreai-hub-action-button");
            return button;
        }

        private static Label MakeStatusLabel()
        {
            Label label = HubPageWidgets.MakeNote("");
            label.AddToClassList("coreai-hub-status");
            return label;
        }

        private static VisualElement MakeGroup(string title)
        {
            VisualElement group = new();
            group.AddToClassList("coreai-hub-settings-group");
            Label label = HubPageWidgets.MakeSection(title);
            label.AddToClassList("coreai-hub-settings-group-title");
            group.Add(label);
            return group;
        }

        private DropdownField MakeGgufModelDropdown()
        {
            List<string> options = new() { GgufAutoLabel };
            string[] models = CoreAiBackend.GetLlmUnityModelFileNames();
            if (models != null)
            {
                foreach (string model in models)
                {
                    if (!string.IsNullOrEmpty(model))
                    {
                        options.Add(model);
                    }
                }
            }

            DropdownField dropdown = new("LLMUnity model", options, 0);
            StyleField(dropdown);
            dropdown.tooltip =
                "Pick a GGUF model known to the LLMUnity Model Manager. [ Auto / Fallback ] keeps the " +
                "configured default. Type a specific .gguf filename in the field below for a custom path.";
            dropdown.RegisterValueChangedCallback(evt =>
                _ggufModelPath.SetValueWithoutNotify(evt.newValue == GgufAutoLabel ? "" : evt.newValue));
            return dropdown;
        }

        private void SyncGgufModelDropdown(string path)
        {
            if (_ggufModel == null)
            {
                return;
            }

            string trimmed = string.IsNullOrEmpty(path) ? "" : path.Trim();
            if (!string.IsNullOrEmpty(trimmed) && _ggufModel.choices.IndexOf(trimmed) >= 0)
            {
                _ggufModel.SetValueWithoutNotify(trimmed);
            }
            else
            {
                _ggufModel.SetValueWithoutNotify(GgufAutoLabel);
            }
        }

        private static void StyleField(BaseField<string> field)
        {
            StyleFieldBase(field);
        }

        private static void StyleField(BaseField<int> field)
        {
            StyleFieldBase(field);
        }

        private static void StyleField(BaseField<float> field)
        {
            StyleFieldBase(field);
        }

        private static void StyleField(BaseField<bool> field)
        {
            StyleFieldBase(field);
        }

        private static void StyleFieldBase(VisualElement field)
        {
            field.AddToClassList("coreai-hub-field");
        }

        private static void SetVisible(VisualElement field, bool visible)
        {
            if (field != null)
            {
                field.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }

        private void SetButtonsEnabled(bool enabled)
        {
            if (_applyButton != null)
            {
                _applyButton.SetEnabled(enabled);
            }

            if (_testButton != null)
            {
                _testButton.SetEnabled(enabled);
            }
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
    }
}
