using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
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
        private readonly ICoreAiRoutingUiController _routingControllerOverride;

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
        private ICoreAiRoutingUiController _routingController;
        private DropdownField _endpointPicker;
        private TextField _endpointId;
        private TextField _endpointName;
        private DropdownField _endpointKind;
        private TextField _endpointBaseUrl;
        private TextField _endpointModel;
        private TextField _endpointUnityAgentName;
        private TextField _endpointSecretReference;
        private TextField _endpointSessionKey;
        private Button _endpointClearSessionKeyButton;
        private IntegerField _endpointContextWindow;
        private IntegerField _endpointPort;
        private IntegerField _endpointGpuLayers;
        private IntegerField _endpointParallelSlots;
        private Toggle _endpointFlashAttention;
        private Toggle _endpointActive;
        private Toggle _endpointKeepWarm;
        private Label _endpointInventoryStatus;
        private Label _endpointOperationStatus;
        private Button _endpointSaveButton;
        private Button _endpointRemoveButton;
        private DropdownField _routingRole;
        private TextField _routingCustomRole;
        private DropdownField _routingProfile;
        private Label _routingStatus;
        private readonly Dictionary<string, string> _endpointIdByLabel = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _profileIdByLabel = new(StringComparer.Ordinal);
        private CancellationTokenSource _routingCts;
        private string _editingEndpointId = "";
        private bool _clearSessionKey;
        private bool _removeConfirmationPending;
        private SynchronizationContext _uiSynchronizationContext;
        private const string AutomaticProfileLabel = "Automatic / agent default";
        private const string SelectEndpointLabel = "Select an endpoint…";

        /// <summary>Creates the Settings page from optional live config sources (null-tolerant).</summary>
        public HubSettingsPage(
            ICoreAISettings settings = null,
            CoreAiChatConfig chatConfig = null,
            string pageId = DefaultPageId,
            string displayName = "AI Settings",
            int order = 100,
            ICoreAiRoutingUiController routingController = null)
            : base(
                string.IsNullOrWhiteSpace(pageId) ? DefaultPageId : pageId,
                string.IsNullOrWhiteSpace(displayName) ? "AI Settings" : displayName,
                order)
        {
            _settings = settings;
            _chatConfig = chatConfig;
            _routingControllerOverride = routingController;
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
            CoreAiRoutingUi.ControllerChanged -= HandleRoutingControllerChanged;
            AttachRoutingController(null);
            try
            {
                _routingCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            try
            {
                _routingCts?.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
            _routingCts = null;
            if (_subscribed)
            {
                CoreAiBackend.OnBackendChanged -= HandleBackendChanged;
                _subscribed = false;
            }
        }

        private object BuildContent()
        {
            _uiSynchronizationContext = SynchronizationContext.Current;
            ScrollView scroll = HubPageWidgets.CreatePage(DisplayName, out VisualElement body);
            scroll.AddToClassList("coreai-hub-page");
            body.AddToClassList("coreai-hub-page-body");

            if (!_subscribed)
            {
                CoreAiBackend.OnBackendChanged += HandleBackendChanged;
                _subscribed = true;
            }

            CoreAiRoutingUi.ControllerChanged -= HandleRoutingControllerChanged;
            CoreAiRoutingUi.ControllerChanged += HandleRoutingControllerChanged;
            AttachRoutingController(_routingControllerOverride ?? CoreAiRoutingUi.Controller);

            CoreAISettingsAsset asset = ResolveSettingsAsset();
            if (_settings == null && asset == null)
            {
                BuildEndpointManagement(body);
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
            BuildEndpointManagement(body);
            RefreshFromStatus();
            return scroll;
        }

        private void BuildEndpointManagement(VisualElement body)
        {
            body.Add(HubPageWidgets.MakeSection("API profiles"));
            body.Add(HubPageWidgets.MakeNote(
                "Create multiple API endpoints and select the profile used by each agent. " +
                "Session keys are write-only and are never saved by this screen."));

            VisualElement endpointGroup = MakeGroup("Endpoints");
            _endpointPicker = new DropdownField("Edit endpoint");
            StyleField(_endpointPicker);
            _endpointPicker.RegisterValueChangedCallback(_ => LoadSelectedEndpoint());
            endpointGroup.Add(_endpointPicker);

            VisualElement endpointActions = new();
            endpointActions.AddToClassList("coreai-hub-actions");
            endpointActions.Add(MakeButton("New endpoint", ClearEndpointEditor));
            endpointGroup.Add(endpointActions);

            _endpointName = new TextField("Name");
            StyleField(_endpointName);
            endpointGroup.Add(_endpointName);

            _endpointId = new TextField("Endpoint ID")
            {
                tooltip = "Stable id used by profiles. Leave empty to derive it from the name."
            };
            StyleField(_endpointId);
            endpointGroup.Add(_endpointId);

            _endpointKind = new DropdownField(
                "Type",
                new List<string> { "HTTP API", "LLMUnity", "Offline" },
                0);
            StyleField(_endpointKind);
            _endpointKind.RegisterValueChangedCallback(_ => RefreshEndpointEditorVisibility());
            endpointGroup.Add(_endpointKind);

            _endpointBaseUrl = new TextField("Base URL");
            StyleField(_endpointBaseUrl);
            endpointGroup.Add(_endpointBaseUrl);

            _endpointModel = new TextField("Model / GGUF");
            StyleField(_endpointModel);
            endpointGroup.Add(_endpointModel);

            _endpointUnityAgentName = new TextField("LLMUnity agent name")
            {
                tooltip = "Optional LLMAgent GameObject name. Leave empty to use the configured default agent."
            };
            StyleField(_endpointUnityAgentName);
            endpointGroup.Add(_endpointUnityAgentName);

            _endpointContextWindow = new IntegerField("Context window") { value = 4096 };
            StyleField(_endpointContextWindow);
            endpointGroup.Add(_endpointContextWindow);

            _endpointPort = new IntegerField("Local server port") { value = 13333 };
            StyleField(_endpointPort);
            endpointGroup.Add(_endpointPort);

            _endpointGpuLayers = new IntegerField("GPU layers");
            StyleField(_endpointGpuLayers);
            endpointGroup.Add(_endpointGpuLayers);

            _endpointParallelSlots = new IntegerField("Parallel slots") { value = 1 };
            StyleField(_endpointParallelSlots);
            endpointGroup.Add(_endpointParallelSlots);

            _endpointFlashAttention = new Toggle("Flash attention")
            {
                tooltip = "Enable llama.cpp flash attention for this local endpoint when supported."
            };
            StyleField(_endpointFlashAttention);
            endpointGroup.Add(_endpointFlashAttention);

            _endpointSecretReference = new TextField("Secret reference")
            {
                tooltip = "Name resolved by the host secret provider. The secret value is not stored."
            };
            StyleField(_endpointSecretReference);
            endpointGroup.Add(_endpointSecretReference);

            _endpointSessionKey = new TextField("Session API key")
            {
                isPasswordField = true,
                maskChar = '*',
                tooltip = "Optional write-only key kept for this running session only."
            };
            StyleField(_endpointSessionKey);
            endpointGroup.Add(_endpointSessionKey);

            _endpointClearSessionKeyButton = MakeButton("Clear saved session key", ClearSessionKey);
            _endpointClearSessionKeyButton.tooltip = "Explicitly forget the in-memory key for this endpoint. Leaving the field blank preserves it.";
            endpointGroup.Add(_endpointClearSessionKeyButton);

            _endpointActive = new Toggle("Active") { value = true };
            StyleField(_endpointActive);
            endpointGroup.Add(_endpointActive);

            _endpointKeepWarm = new Toggle("Keep warm")
            {
                tooltip = "Keep the endpoint ready even when no agent is currently assigned."
            };
            StyleField(_endpointKeepWarm);
            endpointGroup.Add(_endpointKeepWarm);

            VisualElement saveActions = new();
            saveActions.AddToClassList("coreai-hub-actions");
            _endpointSaveButton = MakeButton("Save endpoint", SaveEndpoint);
            _endpointRemoveButton = MakeButton("Remove", RemoveEndpoint);
            saveActions.Add(_endpointSaveButton);
            saveActions.Add(_endpointRemoveButton);
            endpointGroup.Add(saveActions);

            _endpointInventoryStatus = HubPageWidgets.MakeNote("");
            _endpointInventoryStatus.name = "coreai-endpoint-inventory-status";
            endpointGroup.Add(_endpointInventoryStatus);
            _endpointOperationStatus = HubPageWidgets.MakeNote("");
            _endpointOperationStatus.name = "coreai-endpoint-operation-status";
            endpointGroup.Add(_endpointOperationStatus);
            body.Add(endpointGroup);

            VisualElement routingGroup = MakeGroup("Agent routing");
            _routingRole = new DropdownField(
                "Agent",
                new List<string>(BuiltInAgentRoleIds.AllBuiltInRoles),
                0);
            StyleField(_routingRole);
            _routingRole.RegisterValueChangedCallback(_ =>
            {
                _routingCustomRole?.SetValueWithoutNotify("");
                RefreshRoutingSelection();
            });
            routingGroup.Add(_routingRole);

            _routingCustomRole = new TextField("Custom agent role")
            {
                tooltip = "Optional runtime role id. When set, it overrides the built-in Agent selection."
            };
            StyleField(_routingCustomRole);
            _routingCustomRole.RegisterValueChangedCallback(_ => RefreshRoutingSelection());
            routingGroup.Add(_routingCustomRole);

            _routingProfile = new DropdownField("API profile");
            StyleField(_routingProfile);
            routingGroup.Add(_routingProfile);
            routingGroup.Add(MakeButton("Assign to agent", AssignProfileToAgent));
            _routingStatus = HubPageWidgets.MakeNote("");
            routingGroup.Add(_routingStatus);
            body.Add(routingGroup);

            RefreshEndpointManagement();
        }

        private void HandleRoutingControllerChanged()
        {
            DispatchToUi(() =>
            {
                if (_routingControllerOverride == null)
                {
                    AttachRoutingController(CoreAiRoutingUi.Controller);
                    RefreshEndpointManagement();
                }
            });
        }

        private void AttachRoutingController(ICoreAiRoutingUiController controller)
        {
            if (ReferenceEquals(_routingController, controller))
            {
                return;
            }

            if (_routingController != null)
            {
                _routingController.Changed -= HandleRoutingChanged;
            }

            _routingController = controller;
            if (_routingController != null)
            {
                _routingController.Changed += HandleRoutingChanged;
            }
        }

        private void HandleRoutingChanged()
        {
            DispatchToUi(RefreshEndpointManagement);
        }

        private void DispatchToUi(Action action)
        {
            SynchronizationContext context = _uiSynchronizationContext;
            if (context == null)
            {
                return;
            }

            if (ReferenceEquals(context, SynchronizationContext.Current))
            {
                action();
                return;
            }

            context.Post(_ => action(), null);
        }

        private void RefreshEndpointManagement()
        {
            if (_endpointPicker == null)
            {
                return;
            }

            _endpointIdByLabel.Clear();
            _endpointIdByLabel[SelectEndpointLabel] = "";
            List<string> endpointLabels = new() { SelectEndpointLabel };
            IReadOnlyList<LlmEndpointSnapshot> endpoints = _routingController?.GetEndpoints();
            if (endpoints != null)
            {
                foreach (LlmEndpointSnapshot snapshot in endpoints)
                {
                    LlmEndpointDescriptor endpoint = snapshot?.Descriptor;
                    if (endpoint == null || string.IsNullOrWhiteSpace(endpoint.EndpointId))
                    {
                        continue;
                    }

                    string label = UniqueLabel(
                        (string.IsNullOrWhiteSpace(endpoint.DisplayName) ? endpoint.EndpointId : endpoint.DisplayName) +
                        " — " + snapshot.State,
                        _endpointIdByLabel);
                    _endpointIdByLabel[label] = endpoint.EndpointId;
                    endpointLabels.Add(label);
                }
            }

            string selectedEndpointId = ResolveSelectedEndpointId();
            _endpointPicker.choices = endpointLabels;
            _endpointPicker.SetValueWithoutNotify(FindLabel(_endpointIdByLabel, selectedEndpointId));

            _profileIdByLabel.Clear();
            _profileIdByLabel[AutomaticProfileLabel] = "";
            List<string> profileLabels = new() { AutomaticProfileLabel };
            IReadOnlyList<LlmRuntimeProfile> profiles = _routingController?.GetProfiles();
            if (profiles != null)
            {
                foreach (LlmRuntimeProfile profile in profiles)
                {
                    if (profile == null || string.IsNullOrWhiteSpace(profile.ProfileId))
                    {
                        continue;
                    }

                    string label = UniqueLabel(
                        (string.IsNullOrWhiteSpace(profile.DisplayName) ? profile.ProfileId : profile.DisplayName) +
                        " — " + EndpointStateLabel(profile.EndpointId, endpoints),
                        _profileIdByLabel);
                    _profileIdByLabel[label] = profile.ProfileId;
                    profileLabels.Add(label);
                }
            }

            _routingProfile.choices = profileLabels;
            bool available = _routingController != null;
            _endpointSaveButton?.SetEnabled(available);
            _endpointRemoveButton?.SetEnabled(available && !string.IsNullOrEmpty(ResolveSelectedEndpointId()));
            _routingProfile.SetEnabled(available);
            _endpointInventoryStatus.text = available
                ? endpoints == null || endpoints.Count == 0
                    ? "No API endpoints yet. Create one below; agents will use Automatic/default routing meanwhile."
                    : endpoints.Count + " endpoint(s) available. Select one to inspect its live state."
                : "Endpoint registry is not available in the current CoreAI scope.";
            RefreshRoutingSelection();
        }

        private void RefreshRoutingSelection()
        {
            if (_routingProfile == null || _routingRole == null)
            {
                return;
            }

            string profileId = _routingController?.GetProfileForRole(SelectedRoutingRole()) ?? "";
            _routingProfile.SetValueWithoutNotify(FindLabel(_profileIdByLabel, profileId, AutomaticProfileLabel));
        }

        private void LoadSelectedEndpoint()
        {
            string id = ResolveSelectedEndpointId();
            IReadOnlyList<LlmEndpointSnapshot> endpoints = _routingController?.GetEndpoints();
            if (endpoints == null)
            {
                return;
            }

            foreach (LlmEndpointSnapshot snapshot in endpoints)
            {
                LlmEndpointDescriptor endpoint = snapshot?.Descriptor;
                if (endpoint != null && string.Equals(endpoint.EndpointId, id, StringComparison.Ordinal))
                {
                    _editingEndpointId = endpoint.EndpointId;
                    _removeConfirmationPending = false;
                    _endpointId.SetValueWithoutNotify(endpoint.EndpointId);
                    _endpointId.SetEnabled(false);
                    _endpointName.SetValueWithoutNotify(endpoint.DisplayName);
                    _endpointKind.SetValueWithoutNotify(KindLabel(endpoint.Kind));
                    _endpointBaseUrl.SetValueWithoutNotify(endpoint.BaseUrl);
                    _endpointModel.SetValueWithoutNotify(
                        endpoint.Kind == LlmEndpointKind.LlmUnity && !string.IsNullOrWhiteSpace(endpoint.LocalModelPath)
                            ? endpoint.LocalModelPath
                            : endpoint.Model);
                    _endpointContextWindow.SetValueWithoutNotify(endpoint.ContextWindowTokens);
                    _endpointPort.SetValueWithoutNotify(endpoint.Port);
                    _endpointGpuLayers.SetValueWithoutNotify(endpoint.GpuLayers);
                    _endpointParallelSlots.SetValueWithoutNotify(endpoint.ParallelSlots);
                    _endpointUnityAgentName.SetValueWithoutNotify(endpoint.UnityAgentName);
                    _endpointFlashAttention.SetValueWithoutNotify(endpoint.FlashAttention);
                    _endpointSecretReference.SetValueWithoutNotify(endpoint.SecretReference);
                    _endpointSessionKey.SetValueWithoutNotify("");
                    _clearSessionKey = false;
                    _endpointActive.SetValueWithoutNotify(endpoint.Active);
                    _endpointKeepWarm.SetValueWithoutNotify(endpoint.KeepWarm);
                    _endpointOperationStatus.text = "State: " + snapshot.State +
                                                    (string.IsNullOrWhiteSpace(snapshot.Error) ? "" : " — " + snapshot.Error);
                    _endpointRemoveButton.text = "Remove";
                    RefreshEndpointEditorVisibility();
                    return;
                }
            }
        }

        private void ClearEndpointEditor()
        {
            _editingEndpointId = "";
            _removeConfirmationPending = false;
            _endpointPicker.SetValueWithoutNotify("");
            _endpointId.SetValueWithoutNotify("");
            _endpointId.SetEnabled(true);
            _endpointName.SetValueWithoutNotify("");
            _endpointKind.SetValueWithoutNotify("HTTP API");
            _endpointBaseUrl.SetValueWithoutNotify("");
            _endpointModel.SetValueWithoutNotify("");
            _endpointContextWindow.SetValueWithoutNotify(4096);
            _endpointPort.SetValueWithoutNotify(13333);
            _endpointGpuLayers.SetValueWithoutNotify(0);
            _endpointParallelSlots.SetValueWithoutNotify(1);
            _endpointUnityAgentName.SetValueWithoutNotify("");
            _endpointFlashAttention.SetValueWithoutNotify(false);
            _endpointSecretReference.SetValueWithoutNotify("");
            _endpointSessionKey.SetValueWithoutNotify("");
            _clearSessionKey = false;
            _endpointActive.SetValueWithoutNotify(true);
            _endpointKeepWarm.SetValueWithoutNotify(false);
            _endpointOperationStatus.text = "New endpoint. Its stable ID is generated from the name unless provided.";
            _endpointRemoveButton.text = "Remove";
            RefreshEndpointEditorVisibility();
        }

        private async void SaveEndpoint()
        {
            if (_routingController == null)
            {
                return;
            }

            LlmEndpointDescriptor endpoint = ReadEndpointEditor();
            string validation = ValidateEndpoint(endpoint);
            if (string.IsNullOrEmpty(validation) && string.IsNullOrEmpty(_editingEndpointId) &&
                EndpointIdExists(endpoint.EndpointId))
            {
                validation = "Endpoint ID already exists. Select it to edit, or choose a different ID.";
            }
            if (!string.IsNullOrEmpty(validation))
            {
                _endpointOperationStatus.text = validation;
                return;
            }

            SetEndpointBusy(true);
            _endpointOperationStatus.text = endpoint.Active || endpoint.KeepWarm
                ? "Saving and waiting for readiness…"
                : "Saving endpoint…";
            try
            {
                CancellationTokenSource previousCts = _routingCts;
                try
                {
                    previousCts?.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }

                _routingCts = new CancellationTokenSource();
                try
                {
                    previousCts?.Dispose();
                }
                catch (ObjectDisposedException)
                {
                }

                CancellationToken cancellationToken = _routingCts.Token;
                string enteredKey = _endpointSessionKey.value ?? "";
                string sessionKey = string.IsNullOrEmpty(_editingEndpointId)
                    ? enteredKey
                    : _clearSessionKey
                        ? ""
                        : string.IsNullOrEmpty(enteredKey) ? null : enteredKey;
                CoreAiRoutingUiResult result = await _routingController.SaveEndpointAsync(
                    endpoint,
                    sessionKey,
                    cancellationToken);
                _endpointSessionKey.SetValueWithoutNotify("");
                _clearSessionKey = false;
                _editingEndpointId = endpoint.EndpointId;
                RefreshEndpointManagement();
                _endpointPicker.SetValueWithoutNotify(FindLabel(_endpointIdByLabel, endpoint.EndpointId));
                LoadSelectedEndpoint();
                _endpointOperationStatus.text = string.IsNullOrEmpty(result.Message)
                    ? result.Endpoint == null
                        ? "Endpoint saved."
                        : "Endpoint saved — " + result.Endpoint.State + "."
                    : result.Message;
            }
            catch (OperationCanceledException)
            {
                _endpointOperationStatus.text = "Endpoint operation cancelled.";
            }
            finally
            {
                SetEndpointBusy(false);
            }
        }

        private async void RemoveEndpoint()
        {
            string endpointId = ResolveSelectedEndpointId();
            if (_routingController == null || string.IsNullOrEmpty(endpointId))
            {
                return;
            }

            if (!_removeConfirmationPending)
            {
                _removeConfirmationPending = true;
                _endpointRemoveButton.text = "Confirm remove";
                List<string> affected = AffectedBuiltInRoles(endpointId);
                _endpointOperationStatus.text = affected.Count == 0
                    ? "Press Confirm remove again to permanently remove this endpoint."
                    : "Assigned agents: " + string.Join(", ", affected) +
                      ". Press Confirm remove again; their routing will return to Automatic/default.";
                return;
            }

            SetEndpointBusy(true);
            try
            {
                CoreAiRoutingUiResult result = await _routingController.RemoveEndpointAsync(endpointId);
                if (result.Ok)
                {
                    ClearEndpointEditor();
                }

                RefreshEndpointManagement();
                _endpointOperationStatus.text = result.Ok ? "Endpoint removed." : result.Message;
            }
            finally
            {
                _removeConfirmationPending = false;
                SetEndpointBusy(false);
            }
        }

        private void AssignProfileToAgent()
        {
            if (_routingController == null ||
                !_profileIdByLabel.TryGetValue(_routingProfile.value ?? "", out string profileId))
            {
                return;
            }

            string roleId = SelectedRoutingRole();
            if (string.IsNullOrWhiteSpace(roleId))
            {
                _routingStatus.text = "Agent role is required.";
                return;
            }

            CoreAiRoutingUiResult result = _routingController.AssignProfileToRole(roleId, profileId);
            _routingStatus.text = result.Ok
                ? string.IsNullOrEmpty(profileId)
                    ? "Agent API override cleared; Automatic/default routing is active."
                    : "Agent API profile updated."
                : result.Message;
        }

        private string SelectedRoutingRole()
        {
            string custom = _routingCustomRole?.value?.Trim() ?? "";
            return string.IsNullOrEmpty(custom) ? _routingRole?.value?.Trim() ?? "" : custom;
        }

        private LlmEndpointDescriptor ReadEndpointEditor()
        {
            string name = (_endpointName.value ?? "").Trim();
            string id = (_endpointId.value ?? "").Trim();
            if (string.IsNullOrEmpty(id))
            {
                id = LlmEndpointDescriptor.EnsureUniqueEndpointId(
                    LlmEndpointDescriptor.DeriveEndpointSlug(name),
                    ExistingEndpointIds());
                _endpointId.SetValueWithoutNotify(id);
            }

            return new LlmEndpointDescriptor
            {
                EndpointId = id,
                DisplayName = name,
                Kind = LabelKind(_endpointKind.value),
                BaseUrl = (_endpointBaseUrl.value ?? "").Trim(),
                Model = (_endpointModel.value ?? "").Trim(),
                SecretReference = (_endpointSecretReference.value ?? "").Trim(),
                Active = _endpointActive.value,
                KeepWarm = _endpointKeepWarm.value,
                ContextWindowTokens = _endpointContextWindow.value,
                LocalModelPath = LabelKind(_endpointKind.value) == LlmEndpointKind.LlmUnity
                    ? (_endpointModel.value ?? "").Trim()
                    : "",
                UnityAgentName = (_endpointUnityAgentName.value ?? "").Trim(),
                Port = _endpointPort.value,
                GpuLayers = Mathf.Max(0, _endpointGpuLayers.value),
                ParallelSlots = _endpointParallelSlots.value,
                FlashAttention = _endpointFlashAttention.value
            };
        }

        internal static string ValidateEndpoint(LlmEndpointDescriptor endpoint)
        {
            if (endpoint == null)
            {
                return "Endpoint is required.";
            }

            if (string.IsNullOrWhiteSpace(endpoint.DisplayName))
            {
                return "Name is required.";
            }

            IReadOnlyList<string> errors = endpoint.Validate();
            return errors.Count == 0 ? "" : string.Join(" ", errors);
        }

        private IEnumerable<string> ExistingEndpointIds()
        {
            foreach (LlmEndpointSnapshot snapshot in _routingController?.GetEndpoints() ??
                     Array.Empty<LlmEndpointSnapshot>())
            {
                if (!string.IsNullOrWhiteSpace(snapshot?.Descriptor?.EndpointId))
                {
                    yield return snapshot.Descriptor.EndpointId;
                }
            }
        }

        private void RefreshEndpointEditorVisibility()
        {
            LlmEndpointKind kind = LabelKind(_endpointKind?.value);
            SetVisible(_endpointBaseUrl, kind == LlmEndpointKind.HttpOpenAi);
            SetVisible(_endpointSecretReference, kind == LlmEndpointKind.HttpOpenAi);
            SetVisible(_endpointSessionKey, kind == LlmEndpointKind.HttpOpenAi);
            SetVisible(_endpointClearSessionKeyButton, kind == LlmEndpointKind.HttpOpenAi);
            SetVisible(_endpointModel, kind != LlmEndpointKind.Offline);
            SetVisible(_endpointPort, kind == LlmEndpointKind.LlmUnity);
            SetVisible(_endpointGpuLayers, kind == LlmEndpointKind.LlmUnity);
            SetVisible(_endpointUnityAgentName, kind == LlmEndpointKind.LlmUnity);
            SetVisible(_endpointFlashAttention, kind == LlmEndpointKind.LlmUnity);
            SetVisible(_endpointParallelSlots, kind != LlmEndpointKind.Offline);
            _endpointKeepWarm?.SetEnabled(kind != LlmEndpointKind.Offline);
        }

        private void SetEndpointBusy(bool busy)
        {
            _endpointSaveButton?.SetEnabled(!busy && _routingController != null);
            _endpointRemoveButton?.SetEnabled(!busy && _routingController != null &&
                                               !string.IsNullOrEmpty(ResolveSelectedEndpointId()));
        }

        private void ClearSessionKey()
        {
            _endpointSessionKey.SetValueWithoutNotify("");
            _clearSessionKey = true;
            _endpointOperationStatus.text = "Session key will be cleared when this endpoint is saved.";
        }

        private bool EndpointIdExists(string endpointId)
        {
            IReadOnlyList<LlmEndpointSnapshot> endpoints = _routingController?.GetEndpoints();
            if (endpoints == null)
            {
                return false;
            }

            foreach (LlmEndpointSnapshot snapshot in endpoints)
            {
                if (string.Equals(snapshot?.Descriptor?.EndpointId, endpointId, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private List<string> AffectedBuiltInRoles(string endpointId)
        {
            HashSet<string> endpointProfiles = new(StringComparer.Ordinal);
            foreach (LlmRuntimeProfile profile in _routingController?.GetProfiles() ?? Array.Empty<LlmRuntimeProfile>())
            {
                if (profile != null && string.Equals(profile.EndpointId, endpointId, StringComparison.Ordinal))
                {
                    endpointProfiles.Add(profile.ProfileId);
                }
            }

            List<string> roles = new();
            foreach (string role in BuiltInAgentRoleIds.AllBuiltInRoles)
            {
                if (endpointProfiles.Contains(_routingController?.GetProfileForRole(role) ?? ""))
                {
                    roles.Add(role);
                }
            }

            return roles;
        }

        private static string EndpointStateLabel(
            string endpointId,
            IReadOnlyList<LlmEndpointSnapshot> endpoints)
        {
            if (endpoints != null)
            {
                foreach (LlmEndpointSnapshot snapshot in endpoints)
                {
                    if (string.Equals(snapshot?.Descriptor?.EndpointId, endpointId, StringComparison.Ordinal))
                    {
                        return snapshot.State.ToString();
                    }
                }
            }

            return "Unavailable";
        }

        private string ResolveSelectedEndpointId()
        {
            return _endpointPicker != null &&
                   _endpointIdByLabel.TryGetValue(_endpointPicker.value ?? "", out string endpointId)
                ? endpointId
                : "";
        }

        private static string UniqueLabel(string label, IReadOnlyDictionary<string, string> existing)
        {
            string candidate = label;
            int suffix = 2;
            while (existing.ContainsKey(candidate))
            {
                candidate = label + " (" + suffix++ + ")";
            }

            return candidate;
        }

        private static string FindLabel(
            IReadOnlyDictionary<string, string> labels,
            string id,
            string fallback = "")
        {
            foreach (KeyValuePair<string, string> pair in labels)
            {
                if (string.Equals(pair.Value, id, StringComparison.Ordinal))
                {
                    return pair.Key;
                }
            }

            return fallback;
        }

        private static string KindLabel(LlmEndpointKind kind)
        {
            return kind == LlmEndpointKind.LlmUnity
                ? "LLMUnity"
                : kind == LlmEndpointKind.Offline
                    ? "Offline"
                    : "HTTP API";
        }

        private static LlmEndpointKind LabelKind(string label)
        {
            return string.Equals(label, "LLMUnity", StringComparison.Ordinal)
                ? LlmEndpointKind.LlmUnity
                : string.Equals(label, "Offline", StringComparison.Ordinal)
                    ? LlmEndpointKind.Offline
                    : LlmEndpointKind.HttpOpenAi;
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
            _status.text = EffectiveRoutingStatus(live, status);
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
            DispatchToUi(RefreshFromStatus);
        }

        private string EffectiveRoutingStatus(bool live, CoreAiBackendStatus status)
        {
            string profileId = _routingController?.GetProfileForRole(BuiltInAgentRoleIds.SmartChat) ?? "";
            if (!string.IsNullOrWhiteSpace(profileId))
            {
                string profileName = profileId;
                foreach (LlmRuntimeProfile profile in _routingController.GetProfiles() ??
                         Array.Empty<LlmRuntimeProfile>())
                {
                    if (string.Equals(profile?.ProfileId, profileId, StringComparison.Ordinal))
                    {
                        profileName = string.IsNullOrWhiteSpace(profile.DisplayName) ? profileId : profile.DisplayName;
                        break;
                    }
                }

                return "Saved backend settings; SmartChat currently routes to runtime profile '" +
                       profileName + "'. Backend settings apply only to Automatic/default roles.";
            }

            return live
                ? "Applied live: " + status
                : "Saved to settings; no live CoreAI scope is running: " + status;
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
            return tokens >= CoreAISettings.UnlimitedContextWindowTokens
                ? "unlimited (provider decides)"
                : tokens.ToString("N0", CultureInfo.InvariantCulture) + " tok";
        }
    }
}
