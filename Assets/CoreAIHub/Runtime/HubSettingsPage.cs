using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Chat;
using CoreAI.Infrastructure.Llm;
using CoreAI.Vision;
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

        private static readonly List<string> VisionOptions = new()
        {
            "Auto (detect from model name)",
            "On (force multimodal)",
            "Off (text-only)"
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
        private DropdownField _modelPicker;
        private Button _fetchModelsButton;
        private Label _modelFetchStatus;
        private DropdownField _visionMode;
        private Button _detectVisionButton;
        private const string ModelPickerPlaceholder = "[ Fetch models to list ]";
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
        private VisualElement _endpointListContainer;
        private Foldout _endpointAdvanced;
        private DropdownField _endpointGgufModel;
        private TextField _endpointId;
        private TextField _endpointName;
        private DropdownField _endpointKind;
        private TextField _endpointBaseUrl;
        private TextField _endpointModel;
        private DropdownField _endpointModelPicker;
        private Button _endpointFetchModelsButton;
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
        private DropdownField _routingRole;
        private TextField _routingCustomRole;
        private DropdownField _routingProfile;
        private Label _routingStatus;
        private readonly Dictionary<string, string> _profileIdByLabel = new(StringComparer.Ordinal);
        private CancellationTokenSource _routingCts;
        private string _editingEndpointId = "";
        private bool _clearSessionKey;
        private string _removeConfirmEndpointId = "";
        private Button _pendingRemoveButton;
        private SynchronizationContext _uiSynchronizationContext;
        private const string AutomaticProfileLabel = "Automatic / agent default";

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

            VisualElement modelDiscovery = new();
            modelDiscovery.AddToClassList("coreai-hub-actions");
            _fetchModelsButton = MakeButton("Fetch models", FetchHttpModels);
            _fetchModelsButton.tooltip =
                "Query GET {API base URL}/models and list the models the server advertises, " +
                "so you can copy an exact name into HTTP model.";
            modelDiscovery.Add(_fetchModelsButton);
            _httpGroup.Add(modelDiscovery);

            _modelPicker = new DropdownField("Discovered models", new List<string> { ModelPickerPlaceholder }, 0);
            StyleField(_modelPicker);
            _modelPicker.tooltip = "Models reported by the server. Pick one to copy it into HTTP model.";
            _modelPicker.RegisterValueChangedCallback(evt =>
            {
                if (!string.IsNullOrEmpty(evt.newValue) && evt.newValue != ModelPickerPlaceholder)
                {
                    _model.SetValueWithoutNotify(evt.newValue);
                }
            });
            SetVisible(_modelPicker, false);
            _httpGroup.Add(_modelPicker);

            _modelFetchStatus = HubPageWidgets.MakeNote("");
            _httpGroup.Add(_modelFetchStatus);

            _visionMode = new DropdownField("Vision", VisionOptions, 0)
            {
                tooltip = "Whether images may be sent to this model and the camera tool is usable. " +
                          "Auto guesses from the model name; set On for a multimodal model whose name is " +
                          "not auto-detected (e.g. a local qwen3.5 vision build)."
            };
            StyleField(_visionMode);
            _httpGroup.Add(_visionMode);

            VisualElement visionActions = new();
            visionActions.AddToClassList("coreai-hub-actions");
            _detectVisionButton = MakeButton("Detect vision", DetectVision);
            _detectVisionButton.tooltip =
                "Send the model a small test image and check whether it can read it. " +
                "Sets Vision to On/Off from the measured answer (requires a running CoreAI scope).";
            visionActions.Add(_detectVisionButton);
            _httpGroup.Add(visionActions);
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

            // WHY: A live list of saved endpoints with a per-row Remove is clearer than a picker + a single
            // ambiguous Remove button — you act on the specific endpoint you can see.
            _endpointListContainer = new VisualElement();
            endpointGroup.Add(_endpointListContainer);

            _endpointInventoryStatus = HubPageWidgets.MakeNote("");
            _endpointInventoryStatus.name = "coreai-endpoint-inventory-status";
            endpointGroup.Add(_endpointInventoryStatus);

            VisualElement endpointActions = new();
            endpointActions.AddToClassList("coreai-hub-actions");
            endpointActions.Add(MakeButton("New endpoint", ClearEndpointEditor));
            endpointGroup.Add(endpointActions);

            endpointGroup.Add(HubPageWidgets.MakeSection("Endpoint editor"));

            _endpointName = new TextField("Name");
            StyleField(_endpointName);
            SetPlaceholder(_endpointName, "e.g. LM Studio (local)");
            endpointGroup.Add(_endpointName);

            _endpointKind = new DropdownField(
                "Type",
                new List<string> { "HTTP API", "LLMUnity", "Offline" },
                0);
            StyleField(_endpointKind);
            _endpointKind.RegisterValueChangedCallback(_ => RefreshEndpointEditorVisibility());
            endpointGroup.Add(_endpointKind);

            _endpointBaseUrl = new TextField("Base URL");
            StyleField(_endpointBaseUrl);
            SetPlaceholder(_endpointBaseUrl, "http://127.0.0.1:1234/v1");
            endpointGroup.Add(_endpointBaseUrl);

            _endpointModel = new TextField("Model");
            StyleField(_endpointModel);
            SetPlaceholder(_endpointModel, "model id — Fetch models to list");
            endpointGroup.Add(_endpointModel);

            VisualElement endpointModelDiscovery = new();
            endpointModelDiscovery.AddToClassList("coreai-hub-actions");
            _endpointFetchModelsButton = MakeButton("Fetch models", FetchEndpointModels);
            _endpointFetchModelsButton.tooltip =
                "Query GET {Base URL}/models and list the models the server advertises, " +
                "so you can copy an exact name into Model.";
            endpointModelDiscovery.Add(_endpointFetchModelsButton);
            endpointGroup.Add(endpointModelDiscovery);

            _endpointModelPicker = new DropdownField("Discovered models", new List<string> { ModelPickerPlaceholder }, 0);
            StyleField(_endpointModelPicker);
            _endpointModelPicker.tooltip = "Models reported by the server. Pick one to copy it into Model.";
            _endpointModelPicker.RegisterValueChangedCallback(evt =>
            {
                if (!string.IsNullOrEmpty(evt.newValue) && evt.newValue != ModelPickerPlaceholder)
                {
                    _endpointModel.SetValueWithoutNotify(evt.newValue);
                }
            });
            SetVisible(_endpointModelPicker, false);
            endpointGroup.Add(_endpointModelPicker);

            _endpointGgufModel = MakeEndpointGgufDropdown();
            endpointGroup.Add(_endpointGgufModel);

            _endpointSessionKey = new TextField("Session API key")
            {
                isPasswordField = true,
                maskChar = '*',
                tooltip = "Optional key sent as a Bearer token. Write-only and kept for this session only."
            };
            StyleField(_endpointSessionKey);
            endpointGroup.Add(_endpointSessionKey);

            _endpointClearSessionKeyButton = MakeButton("Clear saved key", ClearSessionKey);
            _endpointClearSessionKeyButton.tooltip = "Explicitly forget the in-memory key for this endpoint. Leaving the field blank preserves it.";
            endpointGroup.Add(_endpointClearSessionKeyButton);

            _endpointActive = new Toggle("Active") { value = true };
            StyleField(_endpointActive);
            endpointGroup.Add(_endpointActive);

            _endpointAdvanced = new Foldout { text = "Advanced", value = false };
            _endpointAdvanced.AddToClassList("coreai-hub-field");

            _endpointId = new TextField("Endpoint ID")
            {
                tooltip = "Stable id used by profiles. Leave empty to derive it from the name."
            };
            StyleField(_endpointId);
            SetPlaceholder(_endpointId, "auto-derived from name");
            _endpointAdvanced.Add(_endpointId);

            _endpointContextWindow = new IntegerField("Context window (tokens)")
            {
                tooltip = "0 (empty) = no limit; the provider decides. Otherwise the per-request token budget."
            };
            StyleField(_endpointContextWindow);
            _endpointAdvanced.Add(_endpointContextWindow);

            _endpointParallelSlots = new IntegerField("Parallel slots") { value = 1 };
            StyleField(_endpointParallelSlots);
            _endpointAdvanced.Add(_endpointParallelSlots);

            _endpointSecretReference = new TextField("Secret reference")
            {
                tooltip = "Name resolved by the host secret provider. The secret value is not stored."
            };
            StyleField(_endpointSecretReference);
            _endpointAdvanced.Add(_endpointSecretReference);

            _endpointKeepWarm = new Toggle("Keep warm")
            {
                tooltip = "Keep the endpoint ready even when no agent is currently assigned."
            };
            StyleField(_endpointKeepWarm);
            _endpointAdvanced.Add(_endpointKeepWarm);

            _endpointUnityAgentName = new TextField("LLMUnity agent name")
            {
                tooltip = "Optional LLMAgent GameObject name. Leave empty to use the configured default agent."
            };
            StyleField(_endpointUnityAgentName);
            _endpointAdvanced.Add(_endpointUnityAgentName);

            _endpointPort = new IntegerField("Local server port") { value = 13333 };
            StyleField(_endpointPort);
            _endpointAdvanced.Add(_endpointPort);

            _endpointGpuLayers = new IntegerField("GPU layers");
            StyleField(_endpointGpuLayers);
            _endpointAdvanced.Add(_endpointGpuLayers);

            _endpointFlashAttention = new Toggle("Flash attention")
            {
                tooltip = "Enable llama.cpp flash attention for this local endpoint when supported."
            };
            StyleField(_endpointFlashAttention);
            _endpointAdvanced.Add(_endpointFlashAttention);

            endpointGroup.Add(_endpointAdvanced);

            VisualElement saveActions = new();
            saveActions.AddToClassList("coreai-hub-actions");
            _endpointSaveButton = MakeButton("Save endpoint", SaveEndpoint);
            saveActions.Add(_endpointSaveButton);
            endpointGroup.Add(saveActions);

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

            // WHY: initialise the editor to a clean HTTP "New endpoint" state so field visibility is applied
            // on first render (otherwise LLMUnity-only fields would show under an HTTP endpoint).
            ClearEndpointEditor();
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
            if (_endpointListContainer == null)
            {
                return;
            }

            IReadOnlyList<LlmEndpointSnapshot> endpoints = _routingController?.GetEndpoints();
            RebuildEndpointList(endpoints);

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
            _routingProfile.SetEnabled(available);
            _endpointInventoryStatus.text = available
                ? endpoints == null || endpoints.Count == 0
                    ? "No API endpoints yet. Create one below; agents use Automatic/default routing meanwhile."
                    : endpoints.Count + " endpoint(s). Edit or Remove any below."
                : "Endpoint registry is not available in the current CoreAI scope.";
            RefreshRoutingSelection();
        }

        /// <summary>
        /// Rebuilds the endpoint list: one row per saved endpoint with its name/state and per-row
        /// Edit / Remove buttons. Acting on the specific row avoids the old picker + single-Remove ambiguity.
        /// </summary>
        private void RebuildEndpointList(IReadOnlyList<LlmEndpointSnapshot> endpoints)
        {
            _endpointListContainer.Clear();
            if (endpoints == null || endpoints.Count == 0)
            {
                return;
            }

            foreach (LlmEndpointSnapshot snapshot in endpoints)
            {
                LlmEndpointDescriptor endpoint = snapshot?.Descriptor;
                if (endpoint == null || string.IsNullOrWhiteSpace(endpoint.EndpointId))
                {
                    continue;
                }

                string endpointId = endpoint.EndpointId;
                VisualElement row = new();
                row.AddToClassList("coreai-hub-actions");
                row.style.flexDirection = FlexDirection.Row;
                row.style.alignItems = Align.Center;
                row.style.justifyContent = Justify.SpaceBetween;

                string name = string.IsNullOrWhiteSpace(endpoint.DisplayName) ? endpointId : endpoint.DisplayName;
                bool isEditing = string.Equals(endpointId, _editingEndpointId, StringComparison.Ordinal);
                Label label = HubPageWidgets.MakeNote((isEditing ? "▸ " : "") + name + "  —  " + snapshot.State);
                label.style.flexGrow = 1;
                row.Add(label);

                row.Add(MakeButton("Edit", () => LoadEndpointById(endpointId)));
                Button remove = MakeButton("Remove", null);
                remove.clicked += () => RemoveEndpointRow(endpointId, name, remove);
                row.Add(remove);
                _endpointListContainer.Add(row);
            }
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

        private void LoadEndpointById(string id)
        {
            IReadOnlyList<LlmEndpointSnapshot> endpoints = _routingController?.GetEndpoints();
            if (endpoints == null || string.IsNullOrEmpty(id))
            {
                return;
            }

            foreach (LlmEndpointSnapshot snapshot in endpoints)
            {
                LlmEndpointDescriptor endpoint = snapshot?.Descriptor;
                if (endpoint != null && string.Equals(endpoint.EndpointId, id, StringComparison.Ordinal))
                {
                    _editingEndpointId = endpoint.EndpointId;
                    _endpointId.SetValueWithoutNotify(endpoint.EndpointId);
                    _endpointId.SetEnabled(false);
                    _endpointName.SetValueWithoutNotify(endpoint.DisplayName);
                    _endpointKind.SetValueWithoutNotify(KindLabel(endpoint.Kind));
                    _endpointBaseUrl.SetValueWithoutNotify(endpoint.BaseUrl);
                    string modelValue =
                        endpoint.Kind == LlmEndpointKind.LlmUnity && !string.IsNullOrWhiteSpace(endpoint.LocalModelPath)
                            ? endpoint.LocalModelPath
                            : endpoint.Model;
                    _endpointModel.SetValueWithoutNotify(modelValue);
                    SyncEndpointGgufDropdown(modelValue);
                    _endpointContextWindow.SetValueWithoutNotify(
                        endpoint.ContextWindowTokens >= CoreAISettings.UnlimitedContextWindowTokens
                            ? 0
                            : endpoint.ContextWindowTokens);
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
                    _endpointOperationStatus.text = "Editing '" + (string.IsNullOrWhiteSpace(endpoint.DisplayName)
                        ? endpoint.EndpointId
                        : endpoint.DisplayName) + "'. State: " + snapshot.State +
                        (string.IsNullOrWhiteSpace(snapshot.Error) ? "" : " — " + snapshot.Error);
                    RefreshEndpointEditorVisibility();
                    RefreshEndpointManagement();
                    return;
                }
            }
        }

        private void ClearEndpointEditor()
        {
            _editingEndpointId = "";
            _endpointId.SetValueWithoutNotify("");
            _endpointId.SetEnabled(true);
            _endpointName.SetValueWithoutNotify("");
            _endpointKind.SetValueWithoutNotify("HTTP API");
            _endpointBaseUrl.SetValueWithoutNotify("");
            _endpointModel.SetValueWithoutNotify("");
            SyncEndpointGgufDropdown("");
            _endpointContextWindow.SetValueWithoutNotify(0);
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
            _endpointOperationStatus.text = "New endpoint. Endpoint ID is derived from the name unless set in Advanced.";
            RefreshEndpointEditorVisibility();
            RefreshEndpointManagement();
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
                LoadEndpointById(endpoint.EndpointId);
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

        private async void RemoveEndpointRow(string endpointId, string displayName, Button rowButton)
        {
            if (_routingController == null || string.IsNullOrEmpty(endpointId) || rowButton == null)
            {
                return;
            }

            // WHY: Two-click inline confirm on the row's own button — no separate confirm dialog, and the
            // pending state is bound to the specific row so it cannot act on the wrong endpoint.
            if (!string.Equals(_removeConfirmEndpointId, endpointId, StringComparison.Ordinal))
            {
                ResetRemoveConfirm();
                _removeConfirmEndpointId = endpointId;
                _pendingRemoveButton = rowButton;
                rowButton.text = "Confirm?";
                List<string> affected = AffectedBuiltInRoles(endpointId);
                _endpointOperationStatus.text = affected.Count == 0
                    ? "Click 'Confirm?' again to remove '" + displayName + "'."
                    : "Click 'Confirm?' again to remove '" + displayName + "'. Agents " +
                      string.Join(", ", affected) + " will return to Automatic/default routing.";
                return;
            }

            ResetRemoveConfirm();
            SetEndpointBusy(true);
            try
            {
                CoreAiRoutingUiResult result = await _routingController.RemoveEndpointAsync(endpointId);
                if (result.Ok && string.Equals(_editingEndpointId, endpointId, StringComparison.Ordinal))
                {
                    ClearEndpointEditor();
                }

                RefreshEndpointManagement();
                _endpointOperationStatus.text = result.Ok ? "Removed '" + displayName + "'." : result.Message;
            }
            finally
            {
                SetEndpointBusy(false);
            }
        }

        private void ResetRemoveConfirm()
        {
            if (_pendingRemoveButton != null)
            {
                _pendingRemoveButton.text = "Remove";
            }

            _pendingRemoveButton = null;
            _removeConfirmEndpointId = "";
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
                // WHY: 0 / empty means "no limit" in the UI; the descriptor rejects < 256, so map it to the
                // unlimited sentinel the rest of the stack already understands (provider decides the window).
                ContextWindowTokens = _endpointContextWindow.value <= 0
                    ? CoreAISettings.UnlimitedContextWindowTokens
                    : _endpointContextWindow.value,
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
            bool http = kind == LlmEndpointKind.HttpOpenAi;
            bool local = kind == LlmEndpointKind.LlmUnity;
            SetVisible(_endpointBaseUrl, http);
            SetVisible(_endpointSecretReference, http);
            SetVisible(_endpointSessionKey, http);
            SetVisible(_endpointClearSessionKeyButton, http);
            // HTTP models are free-form text (+ discovery); LLMUnity models come from the GGUF dropdown.
            SetVisible(_endpointModel, http);
            SetVisible(_endpointFetchModelsButton, http);
            if (!http)
            {
                SetVisible(_endpointModelPicker, false);
            }
            SetVisible(_endpointGgufModel, local);
            SetVisible(_endpointPort, local);
            SetVisible(_endpointGpuLayers, local);
            SetVisible(_endpointUnityAgentName, local);
            SetVisible(_endpointFlashAttention, local);
            SetVisible(_endpointParallelSlots, kind != LlmEndpointKind.Offline);
            _endpointKeepWarm?.SetEnabled(kind != LlmEndpointKind.Offline);
        }

        private void SetEndpointBusy(bool busy)
        {
            _endpointSaveButton?.SetEnabled(!busy && _routingController != null);
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
            return _editingEndpointId ?? "";
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

            CoreAISettingsAsset visionAsset = ResolveSettingsAsset();
            if (visionAsset != null && _visionMode != null)
            {
                visionAsset.SetVisionSupport(SelectedVisionMode());
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

        private async void DetectVision()
        {
            if (_busy)
            {
                return;
            }

            CoreAISettingsAsset asset = ResolveSettingsAsset();
            if (asset == null)
            {
                _modelFetchStatus.text = "No CoreAI settings asset resolved; cannot detect vision.";
                return;
            }

            _busy = true;
            SetButtonsEnabled(false);
            _modelFetchStatus.text = "Probing vision: sending the model a test image…";
            try
            {
                // TODO: verify live that the probe round-trips through the active backend and that the
                // detected mode survives an Apply/RefreshFromStatus cycle in the Hub UI.
                VisionSupportMode mode = await new VisionSelfProbe().DetectAndApplyAsync(asset);
                _visionMode?.SetValueWithoutNotify(VisionModeToOption(mode));
                _modelFetchStatus.text = mode == VisionSupportMode.On
                    ? "Vision detected: the model read the test image. Vision set to On."
                    : "Vision not detected: the model could not read the test image. Vision set to Off " +
                      "(see Console for details).";
            }
            finally
            {
                _busy = false;
                SetButtonsEnabled(true);
            }
        }

        private async void FetchHttpModels()
        {
            if (_busy)
            {
                return;
            }

            string baseUrl = (_baseUrl.value ?? "").Trim();
            if (string.IsNullOrEmpty(baseUrl))
            {
                _modelFetchStatus.text = "Enter an API base URL first.";
                return;
            }

            _busy = true;
            SetButtonsEnabled(false);
            _modelFetchStatus.text = "Fetching models…";
            try
            {
                CoreAiModelListResult result = await CoreAiBackend.ListModelsAsync(baseUrl, ResolveApiKey());
                PopulateModelPicker(_modelPicker, result);
                _modelFetchStatus.text = result.Ok
                    ? string.Format(
                        CultureInfo.InvariantCulture,
                        "Found {0} model(s). Pick one to copy it into HTTP model.",
                        result.Models.Count)
                    : "Could not list models: " + Value(result.Error);
            }
            finally
            {
                _busy = false;
                SetButtonsEnabled(true);
            }
        }

        private async void FetchEndpointModels()
        {
            if (_busy)
            {
                return;
            }

            string baseUrl = (_endpointBaseUrl.value ?? "").Trim();
            if (string.IsNullOrEmpty(baseUrl))
            {
                _endpointOperationStatus.text = "Enter a Base URL first.";
                return;
            }

            _busy = true;
            _endpointFetchModelsButton?.SetEnabled(false);
            _endpointOperationStatus.text = "Fetching models…";
            try
            {
                CoreAiModelListResult result = await CoreAiBackend.ListModelsAsync(
                    baseUrl, _endpointSessionKey?.value ?? "");
                PopulateModelPicker(_endpointModelPicker, result);
                _endpointOperationStatus.text = result.Ok
                    ? string.Format(
                        CultureInfo.InvariantCulture,
                        "Found {0} model(s). Pick one to copy it into Model.",
                        result.Models.Count)
                    : "Could not list models: " + Value(result.Error);
            }
            finally
            {
                _busy = false;
                _endpointFetchModelsButton?.SetEnabled(true);
            }
        }

        private static void PopulateModelPicker(DropdownField picker, CoreAiModelListResult result)
        {
            if (picker == null)
            {
                return;
            }

            List<string> choices = new();
            if (result.Ok)
            {
                foreach (string model in result.Models)
                {
                    if (!string.IsNullOrWhiteSpace(model))
                    {
                        choices.Add(model);
                    }
                }
            }

            if (choices.Count == 0)
            {
                choices.Add(ModelPickerPlaceholder);
            }

            picker.choices = choices;
            picker.SetValueWithoutNotify(choices[0]);
            SetVisible(picker, result.Ok);
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

            if (_visionMode != null && asset != null)
            {
                _visionMode.SetValueWithoutNotify(VisionModeToOption(asset.VisionSupport));
            }

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

        private VisionSupportMode SelectedVisionMode()
        {
            string value = _visionMode?.value;
            if (string.Equals(value, VisionOptions[1], StringComparison.Ordinal))
            {
                return VisionSupportMode.On;
            }

            if (string.Equals(value, VisionOptions[2], StringComparison.Ordinal))
            {
                return VisionSupportMode.Off;
            }

            return VisionSupportMode.Auto;
        }

        private static string VisionModeToOption(VisionSupportMode mode)
        {
            switch (mode)
            {
                case VisionSupportMode.On:
                    return VisionOptions[1];
                case VisionSupportMode.Off:
                    return VisionOptions[2];
                default:
                    return VisionOptions[0];
            }
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

        private DropdownField MakeEndpointGgufDropdown()
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

            DropdownField dropdown = new("Model (GGUF)", options, 0);
            StyleField(dropdown);
            dropdown.tooltip =
                "Pick a GGUF model known to the LLMUnity Model Manager. [ Auto / Fallback ] keeps the " +
                "endpoint's configured default.";
            dropdown.RegisterValueChangedCallback(evt =>
                _endpointModel.SetValueWithoutNotify(evt.newValue == GgufAutoLabel ? "" : evt.newValue));
            return dropdown;
        }

        private void SyncEndpointGgufDropdown(string path)
        {
            if (_endpointGgufModel == null)
            {
                return;
            }

            string trimmed = string.IsNullOrEmpty(path) ? "" : path.Trim();
            _endpointGgufModel.SetValueWithoutNotify(
                !string.IsNullOrEmpty(trimmed) && _endpointGgufModel.choices.IndexOf(trimmed) >= 0
                    ? trimmed
                    : GgufAutoLabel);
        }

        /// <summary>
        /// Overlays a muted hint label inside a text field's input, shown only while the field is empty.
        /// Value-safe: unlike the "fake value" trick, the field's <c>value</c> is never set to the hint, so
        /// callers read the real (possibly empty) text.
        /// </summary>
        private static void SetPlaceholder(TextField field, string placeholder)
        {
            if (field == null || string.IsNullOrEmpty(placeholder))
            {
                return;
            }

            VisualElement input = field.Q("unity-text-input") ?? field;
            Label hint = new(placeholder) { pickingMode = PickingMode.Ignore };
            hint.style.position = Position.Absolute;
            hint.style.left = 4;
            hint.style.top = 0;
            hint.style.bottom = 0;
            hint.style.unityTextAlign = TextAnchor.MiddleLeft;
            hint.style.color = new Color(1f, 1f, 1f, 0.35f);
            input.Add(hint);

            void Refresh()
            {
                hint.style.display = string.IsNullOrEmpty(field.value) ? DisplayStyle.Flex : DisplayStyle.None;
            }

            field.RegisterValueChangedCallback(_ => Refresh());
            Refresh();
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

            if (_fetchModelsButton != null)
            {
                _fetchModelsButton.SetEnabled(enabled);
            }

            if (_detectVisionButton != null)
            {
                _detectVisionButton.SetEnabled(enabled);
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
