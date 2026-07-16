#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UIElements;
#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL
using LLMUnity;
#endif

namespace CoreAI.Infrastructure.Llm.Editor
{
    /// <summary>
    /// UI Toolkit inspector for CoreAISettingsAsset: guided essentials, connection test, and
    /// collapsed advanced sections. Connection fields (URL/key/model, GGUF) appear once, in
    /// Essentials; advanced sections hold only their own extra settings.
    /// </summary>
    [CustomEditor(typeof(CoreAISettingsAsset))]
    public sealed class CoreAISettingsAssetEditor : UnityEditor.Editor
    {
        private const string PrefAdvancedOpen = "CoreAI.SettingsEditor.AdvancedOpen";
        private const string PrefHttpOpen = "CoreAI.SettingsEditor.HttpOpen";
        private const string PrefLlmUnityOpen = "CoreAI.SettingsEditor.LlmUnityOpen";
        private const string PrefGeneralOpen = "CoreAI.SettingsEditor.GeneralOpen";
        private const string PrefOfflineOpen = "CoreAI.SettingsEditor.OfflineOpen";
        private const string PrefDebugOpen = "CoreAI.SettingsEditor.DebugOpen";
        private const string PrefSummarizationOpen = "CoreAI.SettingsEditor.SummarizationOpen";
        private const string PrefWebGlPlayerOpen = "CoreAI.SettingsEditor.WebGlPlayerOpen";
        private const string PrefFallbackBackendOpen = "CoreAI.SettingsEditor.FallbackBackendOpen";

        private static readonly string[] HttpModelPresets =
        {
            "gpt-4o-mini",
            "gpt-4o",
            "gpt-4.1-mini",
            "gpt-4.1",
            "o4-mini",
            "qwen3.5-4b",
            "qwen3.5-8b",
            "llama-3.1-8b-instruct",
            "llama-3.2-3b-instruct",
            "deepseek-chat"
        };

        private const string CustomPresetLabel = "(custom)";

        // Connection-test state.
        private bool _isTestingConnection;
        private string _testResultMessage;
        private MessageType _testResultType;

        // Dynamic elements updated by RefreshDynamicState.
        private VisualElement _autoPriorityRow;
        private VisualElement _httpEssentials;
        private VisualElement _llmUnityEssentials;
        private HelpBox _productionWarning;
        private HelpBox _webGlStreamingHint;
        private DropdownField _modelPreset;
        private VisualElement _httpAdvancedGroup;
        private HelpBox _serverManagedWarning;
        private HelpBox _httpAutoHint;
        private HelpBox _fallbackIncompleteWarning;
        private HelpBox _fallbackActiveInfo;
        private HelpBox _webGlNoNativeSseWarning;
        private HelpBox _webGlCredentialsInfo;
        private VisualElement _llmUnityAdvancedGroup;
        private HelpBox _llmUnityAutoHint;
        private PropertyField _temperatureField;
        private HelpBox _emptyPrefixHint;
        private VisualElement _offlineCustomGroup;
        private HelpBox _offlineDefaultInfo;
        private Button _testButton;
        private Label _routeHint;
        private HelpBox _testResultBox;
#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL
        private DropdownField _ggufDropdown;
        private HelpBox _ggufEmptyHint;
        private readonly List<string> _ggufFileNames = new();
#endif

        public override VisualElement CreateInspectorGUI()
        {
            serializedObject.Update();
            VisualElement root = new();

            BuildHeader(root);
            root.Add(BuildEssentialsCard());
            root.Add(BuildConnectionTestCard());
            root.Add(BuildAdvancedCard());
            root.Add(BuildFooterButtons());

            // One tracker refreshes every conditional row/warning; cheaper and simpler than
            // wiring a callback per field.
            root.TrackSerializedObjectValue(serializedObject, _ => RefreshDynamicState());
            root.RegisterCallback<AttachToPanelEvent>(_ => RefreshDynamicState());
            return root;
        }

        private CoreAISettingsAsset Settings => (CoreAISettingsAsset)target;

        private static void BuildHeader(VisualElement root)
        {
            Label title = new("CoreAI Settings");
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.fontSize = 14;
            title.style.marginTop = 6;
            root.Add(title);

            Label subtitle = new("Project-wide LLM configuration");
            subtitle.style.fontSize = 10;
            subtitle.style.opacity = 0.7f;
            subtitle.style.marginBottom = 6;
            root.Add(subtitle);
        }

        private VisualElement BuildEssentialsCard()
        {
            VisualElement card = MakeCard();
            card.Add(MakeSectionLabel("Essentials"));

            card.Add(Field("backendType", "LLM Backend",
                "Auto = pick the best available; HTTP API = LM Studio / OpenAI / etc.; LLMUnity = local GGUF; Offline = stub"));
            card.Add(Field("executionMode", "LLM Mode",
                "Public runtime mode. Use Auto to preserve legacy backend selection."));

            _productionWarning = MakeDynamicHelpBox(HelpBoxMessageType.Warning);
            card.Add(_productionWarning);

            card.Add(MakeMiniLabel("Streaming"));
            card.Add(Field("enableStreaming", "Global streaming",
                "Token-by-token replies when backends support it. Overridden if the chat panel turns streaming off (CoreAiChatConfig) or a role sets AgentBuilder.WithStreaming(false). Default: on."));
            _webGlStreamingHint = MakeDynamicHelpBox(HelpBoxMessageType.Warning);
            _webGlStreamingHint.text =
                "WebGL build target: incremental SSE in the player needs WebGL: native SSE (fetch) enabled under Advanced Settings > WebGL player (browser build).";
            card.Add(_webGlStreamingHint);

            _autoPriorityRow = new VisualElement();
            _autoPriorityRow.Add(Field("autoPriority", "Auto Priority",
                "Which backend to try first when in Auto mode"));
            card.Add(_autoPriorityRow);

            _httpEssentials = new VisualElement();
            _httpEssentials.Add(MakeMiniLabel("HTTP API connection"));
            _httpEssentials.Add(Field("apiBaseUrl", "Base URL",
                "https://api.openai.com/v1, http://localhost:1234/v1 (LM Studio)"));
            _httpEssentials.Add(Field("apiKey", "API Key", "Bearer token. Leave empty for LM Studio."));
            _httpEssentials.Add(Field("modelName", "Model",
                "Provider model id. Type a custom value or choose a common preset below."));
            _httpEssentials.Add(BuildModelPresetDropdown());
            card.Add(_httpEssentials);

            _llmUnityEssentials = new VisualElement();
            _llmUnityEssentials.Add(MakeMiniLabel("LLMUnity (local model)"));
            BuildGgufModelControl(_llmUnityEssentials);
            card.Add(_llmUnityEssentials);

            return card;
        }

        private DropdownField BuildModelPresetDropdown()
        {
            List<string> choices = new() { CustomPresetLabel };
            choices.AddRange(HttpModelPresets);
            _modelPreset = new DropdownField("Model Preset", choices, 0)
            {
                tooltip = "Quickly fill the Model field with a common OpenAI-compatible model id."
            };
            _modelPreset.RegisterValueChangedCallback(evt =>
            {
                if (evt.newValue == CustomPresetLabel)
                {
                    return;
                }

                serializedObject.FindProperty("modelName").stringValue = evt.newValue;
                serializedObject.ApplyModifiedProperties();
            });
            return _modelPreset;
        }

        private VisualElement BuildConnectionTestCard()
        {
            VisualElement card = MakeCard();
            card.Add(MakeSectionLabel("Connection test"));

            _testButton = new Button(() => TestConnection(Settings)) { text = "Test Connection" };
            _testButton.style.height = 28;
            card.Add(_testButton);

            _routeHint = new Label("");
            _routeHint.style.fontSize = 10;
            _routeHint.style.opacity = 0.7f;
            _routeHint.style.marginTop = 2;
            card.Add(_routeHint);

            _testResultBox = MakeDynamicHelpBox(HelpBoxMessageType.Info);
            card.Add(_testResultBox);
            return card;
        }

        private VisualElement BuildAdvancedCard()
        {
            VisualElement card = MakeCard();
            Foldout advanced = MakePersistentFoldout(
                "Advanced Settings (routing, prompts, retry, debug)", PrefAdvancedOpen, false);
            advanced.Add(new HelpBox(
                "Beginners can leave these alone. Touch only when you need per-role routing, prompt prefixes, retry tuning, or debug logs.",
                HelpBoxMessageType.None));

            advanced.Add(BuildHttpAdvancedFoldout());
            advanced.Add(BuildFallbackBackendFoldout());
            advanced.Add(BuildWebGlPlayerFoldout());
            advanced.Add(BuildLlmUnityFoldout());
            advanced.Add(BuildSummarizationFoldout());
            advanced.Add(BuildGeneralFoldout());
            advanced.Add(BuildOfflineFoldout());
            advanced.Add(BuildDebugFoldout());

            card.Add(advanced);
            return card;
        }

        private Foldout BuildHttpAdvancedFoldout()
        {
            Foldout foldout = MakePersistentFoldout("HTTP API (advanced)", PrefHttpOpen, true);
            foldout.Add(new HelpBox(
                "Connection fields (Base URL, API Key, Model) live in Essentials above.",
                HelpBoxMessageType.None));

            _httpAdvancedGroup = new VisualElement();
            _httpAdvancedGroup.Add(Field("requestTimeoutSeconds", "Timeout (sec)",
                "HTTP request timeout in seconds."));
            _httpAdvancedGroup.Add(Field("maxClientLimitedRequestsPerSession", "ClientLimited Max Requests",
                "0 = no local request limit"));
            _httpAdvancedGroup.Add(Field("maxClientLimitedPromptChars", "ClientLimited Max Prompt Chars",
                "0 = no local prompt-size limit"));
            foldout.Add(_httpAdvancedGroup);

            _serverManagedWarning = MakeDynamicHelpBox(HelpBoxMessageType.Warning);
            _serverManagedWarning.text =
                "ServerManagedApi should point to your backend proxy. Leave provider keys on the server, not in the client asset.";
            foldout.Add(_serverManagedWarning);

            _httpAutoHint = MakeDynamicHelpBox(HelpBoxMessageType.Info);
            foldout.Add(_httpAutoHint);
            return foldout;
        }

        private Foldout BuildFallbackBackendFoldout()
        {
            Foldout foldout = MakePersistentFoldout("Fallback Backend (secondary)", PrefFallbackBackendOpen, false);
            foldout.Add(new HelpBox(
                "When enabled and secondary URL/model are set, retryable primary backend failures are retried on this secondary HTTP backend. Auth, invalid request, quota and user cancellation do not fall back.",
                HelpBoxMessageType.None));

            foldout.Add(Field("enableFallbackBackend", "Enable Fallback Backend",
                "Retry supported primary backend failures on the secondary OpenAI-compatible HTTP backend."));
            foldout.Add(Field("secondaryApiBaseUrl", "Secondary Base URL",
                "OpenAI-compatible /v1 base URL for fallback requests."));
            foldout.Add(Field("secondaryApiKey", "Secondary API Key",
                "Bearer token for the secondary provider. Leave empty for local servers that do not require auth."));
            foldout.Add(Field("secondaryModelName", "Secondary Model",
                "Model id used by the secondary provider."));

            _fallbackIncompleteWarning = MakeDynamicHelpBox(HelpBoxMessageType.Warning);
            _fallbackIncompleteWarning.text =
                "Fallback is enabled, but Secondary Base URL and Secondary Model must both be set before the runtime can use it.";
            foldout.Add(_fallbackIncompleteWarning);

            _fallbackActiveInfo = MakeDynamicHelpBox(HelpBoxMessageType.Info);
            _fallbackActiveInfo.text =
                "Fallback backend is configured. Runtime fallback is active for retryable primary failures.";
            foldout.Add(_fallbackActiveInfo);
            return foldout;
        }

        private Foldout BuildWebGlPlayerFoldout()
        {
            Foldout foldout = MakePersistentFoldout("WebGL player (browser build)", PrefWebGlPlayerOpen, false);
            foldout.Add(new HelpBox(
                "Built WebGL player only (Editor / Standalone ignore these). " +
                "Problem pattern: global streaming on but native SSE off -> one block reply, no live tokens. Enable native SSE. " +
                "LM Studio and most local OpenAI-compatible hosts: keep fetch credentials off.",
                HelpBoxMessageType.None));

            foldout.Add(Field("webGlNativeStreaming", "WebGL: native SSE (fetch)",
                "Default: on. CoreAiSseFetch.jslib + fetch ReadableStream for incremental SSE. Off = buffered non-streaming HTTP in the player."));
            foldout.Add(Field("sameOriginCredentials", "WebGL: fetch credentials (same-origin)",
                "Default off -> fetch credentials: omit (Bearer still sent; works with CORS ACAO: * e.g. OpenRouter). On -> same-origin. Rarely needed for LM Studio / local APIs."));

            _webGlNoNativeSseWarning = MakeDynamicHelpBox(HelpBoxMessageType.Warning);
            _webGlNoNativeSseWarning.text =
                "Active build target is WebGL and global streaming is on, but native SSE is off. The player will not stream incrementally.";
            foldout.Add(_webGlNoNativeSseWarning);

            _webGlCredentialsInfo = MakeDynamicHelpBox(HelpBoxMessageType.Info);
            _webGlCredentialsInfo.text =
                "Fetch credentials is on. For LM Studio and many setups this is unnecessary and can worsen cross-origin behavior. Turn off unless you know you need same-origin cookie mode.";
            foldout.Add(_webGlCredentialsInfo);
            return foldout;
        }

        private Foldout BuildLlmUnityFoldout()
        {
            Foldout foldout = MakePersistentFoldout("LLMUnity (local model)", PrefLlmUnityOpen, true);
            foldout.Add(new HelpBox(
                "The GGUF model picker lives in Essentials above.",
                HelpBoxMessageType.None));

            _llmUnityAdvancedGroup = new VisualElement();
            _llmUnityAdvancedGroup.Add(Field("llmUnityAgentName", "Agent Name",
                "GameObject name that hosts LLMAgent. Empty = auto-detect."));
            _llmUnityAdvancedGroup.Add(Field("llmUnityAutoCreateRuntimeHost", "Auto-create LLM host",
                "When no LLMAgent exists in loaded scenes, create a runtime GameObject with LLM + LLMAgent and apply the GGUF model hint."));
            _llmUnityAdvancedGroup.Add(Field("llmUnityRuntimeHostObjectName", "Runtime host name",
                "Name for the auto-created runtime host GameObject. Empty = CoreAI_LLMUnity_Runtime."));
            _llmUnityAdvancedGroup.Add(Field("llmUnityAutostartLocalServer", "Autostart local server",
                "Warm up the local llama.cpp server after startup. Uses Startup Timeout."));
            _llmUnityAdvancedGroup.Add(Field("llmUnityDontDestroyOnLoad", "Dont Destroy On Load",
                "Keep the runtime host alive across scene loads."));

            SliderInt gpuLayers = new("GPU Layers", 0, 99)
            {
                showInputField = true,
                bindingPath = "llmUnityNumGPULayers",
                tooltip = "Number of layers to offload to GPU. 0 = CPU only, 99 = maximum offload."
            };
            _llmUnityAdvancedGroup.Add(gpuLayers);

            _llmUnityAdvancedGroup.Add(Field("llmUnityStartupTimeoutSeconds", "Startup Timeout (sec)",
                "Seconds to wait for local model startup."));
            _llmUnityAdvancedGroup.Add(Field("llmUnityStartupDelaySeconds", "Startup Delay (sec)",
                "Delay after the local model server reports ready."));
            _llmUnityAdvancedGroup.Add(Field("llmUnityKeepAlive", "Keep Alive",
                "Keep the local server warm between prompts."));
            _llmUnityAdvancedGroup.Add(Field("llmUnityMaxConcurrentChats", "Max Concurrent Chats",
                "1 = serial chats; greater values allow parallel chat sessions."));
            foldout.Add(_llmUnityAdvancedGroup);

            foldout.Add(BuildLlmUnityWiringStatus());

            _llmUnityAutoHint = MakeDynamicHelpBox(HelpBoxMessageType.Info);
            foldout.Add(_llmUnityAutoHint);
            return foldout;
        }

        private Foldout BuildSummarizationFoldout()
        {
            Foldout foldout = MakePersistentFoldout("Chat history summarization", PrefSummarizationOpen, true);
            foldout.Add(Field("enableConversationHistorySummarization", "Enable history summarization",
                "When off, the full loaded transcript is kept in the chat tail without rolling older turns into ## Conversation Summary (risk of context overflow)."));
            foldout.Add(Field("conversationHistoryRecentTokenBudgetOverride", "Recent history token budget override",
                "0 = automatic from context window. When set, caps the verbatim tail to this many estimated tokens; older lines roll into the summary when summarization is on."));
            foldout.Add(Field("conversationRolledSummaryMaxTokens", "Max rolled summary (tokens)",
                "0 = unlimited. When set, truncates the persisted rolling summary to roughly this many estimated tokens after each rollup."));
            foldout.Add(Field("conversationCompactionTriggerRatio", "Compaction trigger ratio",
                "Roadmap §2. Summarize older turns only when estimated history tokens reach this fraction of the history budget. Invalid values fall back to the CoreAI default."));
            foldout.Add(Field("enableContextPruning", "Enable context pruning",
                "Roadmap §7. Prunes duplicate/stale prompt-history entries before summarization, only from the in-memory request copy; stored chat history remains intact."));
            foldout.Add(Field("maxRetainedToolResultMessages", "Max retained tool results",
                "Newest durable ## Tool Results messages retained in the prompt history copy before compaction. Older tool observations are superseded and omitted from the request."));
            foldout.Add(Field("enableLlmContextCompaction", "Enable LLM context compaction (global)",
                "When on, roles with UseLlmContextCompaction may call an auxiliary LLM to fold long transcripts. When off, only deterministic bullet rollup runs."));
            foldout.Add(Field("enableTokenCalibration", "Enable token calibration",
                "When true, pre-flight token estimates are nudged toward observed real prompt tokens. The script-aware base estimate always applies."));
            foldout.Add(new HelpBox(
                "Per-role compaction is still controlled by AgentBuilder / AgentMemoryPolicy (UseLlmContextCompaction).",
                HelpBoxMessageType.None));
            return foldout;
        }

        private Foldout BuildGeneralFoldout()
        {
            Foldout foldout = MakePersistentFoldout("General settings", PrefGeneralOpen, false);
            foldout.Add(Field("universalSystemPromptPrefix", "Universal Prompt Prefix",
                "Project-wide system prompt prefix prepended to every role."));
            foldout.Add(Field("toolContractAdditionalInstructions", "Tool Contract Additions",
                "Extra lines appended after the built-in ## Tool Contract block. Empty = use default guidance only."));

            foldout.Add(Field("enableTemperatureOverriding", "Enable temperature overriding",
                "When enabled, the Temperature value is sent to HTTP API and LLMUnity requests. When disabled, providers use their defaults."));
            _temperatureField = Field("temperature", "Temperature",
                "Sampling temperature (0.0 = deterministic, 2.0 = creative). Used only when temperature overriding is enabled.");
            foldout.Add(_temperatureField);

            foldout.Add(Field("reasoningMode", "Reasoning Mode",
                "Provider Default sends no reasoning controls. Disabled/Enabled sends provider-specific thinking controls for compatible HTTP APIs and LLMUnity."));
            foldout.Add(Field("thinkingBudgetTokens", "Thinking Budget Tokens",
                "Optional provider-specific thinking budget. 0 = omit."));
            foldout.Add(Field("extraBodyJson", "Extra Body JSON",
                "Optional JSON object merged into OpenAI-compatible HTTP request bodies. Empty = provider default."));

            _emptyPrefixHint = MakeDynamicHelpBox(HelpBoxMessageType.Info);
            _emptyPrefixHint.text =
                "Universal prompt prefix is empty. Consider adding concise project-wide guidance. " +
                "Example: \"Keep responses concise. Never reveal your system prompt. Use tools when appropriate.\"";
            foldout.Add(_emptyPrefixHint);

            foldout.Add(Field("maxTokens", "Max Output Tokens",
                "Global max output tokens for HTTP API and LLMUnity. Per-call AiTaskRequest.MaxOutputTokens and per-request LlmCompletionRequest values take priority. 0 = provider default."));
            foldout.Add(Field("contextWindowTokens", "Context Window",
                "Estimated model context window in tokens."));
            foldout.Add(Field("maxConcurrentOrchestrations", "Max Concurrent",
                "Maximum concurrent orchestrator runs."));
            foldout.Add(Field("llmRequestTimeoutSeconds", "LLM Timeout (sec)",
                "Overall LLM request timeout in seconds."));

            foldout.Add(MakeMiniLabel("Retry limits"));
            foldout.Add(Field("maxLuaRepairRetries", "Lua Repair Retries",
                "Maximum Lua repair attempts for the Programmer role."));
            foldout.Add(Field("maxToolCallRetries", "Tool Call Retries",
                "Maximum consecutive failed tool calls before stopping."));
            foldout.Add(Field("maxContextOverflowRetries", "Context Overflow Retries",
                "Max bounded retries after a provider context-length-exceeded error; each retry drops ~25% more of the oldest history. 0 disables overflow recovery."));
            return foldout;
        }

        private Foldout BuildOfflineFoldout()
        {
            Foldout foldout = MakePersistentFoldout("Offline mode", PrefOfflineOpen, false);
            foldout.Add(Field("offlineUseCustomResponse", "Custom Response",
                "Return a fixed response instead of role-specific offline stubs."));

            _offlineCustomGroup = new VisualElement();
            _offlineCustomGroup.style.marginLeft = 12;
            _offlineCustomGroup.Add(Field("offlineCustomResponse", "Response Text",
                "Assistant text returned for matched offline roles."));
            _offlineCustomGroup.Add(Field("offlineCustomResponseRoles", "Roles",
                "Comma-separated role ids. * = all roles. Example: Creator,Programmer."));
            foldout.Add(_offlineCustomGroup);

            _offlineDefaultInfo = MakeDynamicHelpBox(HelpBoxMessageType.Info);
            _offlineDefaultInfo.text =
                "Default offline mode returns role-specific stubs (ProgrammerLua, CreatorJSON, Chat echo).";
            foldout.Add(_offlineDefaultInfo);
            return foldout;
        }

        private Foldout BuildDebugFoldout()
        {
            Foldout foldout = MakePersistentFoldout("Debug logging", PrefDebugOpen, false);
            foldout.Add(MakeMiniLabel("LLM logging"));
            foldout.Add(Field("logLlmInput", "Log LLM Input", "Log composed system and user prompts."));
            foldout.Add(Field("logLlmOutput", "Log LLM Output",
                "Log assistant completions and aggregated tool-call summaries."));

            foldout.Add(MakeMiniLabel("Tool call logging"));
            foldout.Add(Field("logToolCalls", "Log Tool Calls", "Log whenever a native tool executes."));
            foldout.Add(Field("logToolCallArguments", "Log Arguments", "Serialize tool-call arguments into logs."));
            foldout.Add(Field("logToolCallResults", "Log Results", "Serialize tool-call results into logs."));
            foldout.Add(Field("logMeaiToolCallingSteps", "Log MEAI Steps",
                "Trace FunctionInvokingChatClient tool-calling iterations and retries."));

            foldout.Add(MakeMiniLabel("Transport / orchestration"));
            foldout.Add(Field("enableHttpDebugLogging", "HTTP Debug Logging",
                "Log HTTP request/response JSON."));
            foldout.Add(Field("enableMeaiDebugLogging", "MEAI Debug Logging", ""));
            foldout.Add(Field("logOrchestrationMetrics", "Log Orchestration Metrics", ""));

            foldout.Add(MakeMiniLabel("Token budget overlay"));
            foldout.Add(Field("inputTokenPricePer1KUsd", "Input $ / 1K Tokens",
                "USD price per 1K prompt tokens for the token-budget overlay. 0 = unset (tokens only)."));
            foldout.Add(Field("outputTokenPricePer1KUsd", "Output $ / 1K Tokens",
                "USD price per 1K completion tokens for the token-budget overlay. 0 = unset (tokens only)."));
            return foldout;
        }

        private VisualElement BuildFooterButtons()
        {
            VisualElement row = new();
            row.style.flexDirection = FlexDirection.Row;
            row.style.marginTop = 6;

            Button copyKey = new(() =>
            {
                EditorGUIUtility.systemCopyBuffer = Settings.ApiKey;
                Debug.Log("[CoreAI] API Key copied to clipboard");
            })
            {
                text = "Copy API Key"
            };
            copyKey.style.height = 24;
            copyKey.style.flexGrow = 1;
            row.Add(copyKey);

            Button reset = new(() =>
            {
                if (EditorUtility.DisplayDialog("Reset Settings",
                        "Reset all settings to default values?",
                        "Reset", "Cancel"))
                {
                    Settings.ConfigureAuto();
                    Settings.ConfigureHttpApi("http://localhost:1234/v1", "", "gpt-4o-mini");
                    Settings.ConfigureLlmUnity();
                    EditorUtility.SetDirty(target);
                    serializedObject.Update();
                    RefreshDynamicState();
                }
            })
            {
                text = "Reset"
            };
            reset.style.height = 24;
            reset.style.flexGrow = 1;
            row.Add(reset);
            return row;
        }

        private VisualElement BuildLlmUnityWiringStatus()
        {
            VisualElement card = MakeCard();
            card.Add(MakeSectionLabel("LLMUnity status"));

            bool packageInstalled = IsLlmUnityPackageInstalled();
            bool defineActive = IsLlmUnityDefineActive();

            if (!packageInstalled)
            {
                card.Add(new HelpBox(
                    "LLMUnity package is not installed. Add package `ai.undream.llm` from Package Manager to enable local GGUF models.",
                    HelpBoxMessageType.Warning));
                Button open = new(() => EditorApplication.ExecuteMenuItem("Window/Package Manager"))
                {
                    text = "Open Package Manager"
                };
                open.style.height = 24;
                card.Add(open);
            }
            else if (!defineActive)
            {
                card.Add(new HelpBox(
                    "LLMUnity package is installed, but COREAI_HAS_LLMUNITY is not active for CoreAI assemblies. " +
                    "This usually means asmdef versionDefines point to the old package name.",
                    HelpBoxMessageType.Warning));
                Button fix = new(FixLlmUnityAsmdefWiring) { text = "Auto-fix asmdef wiring" };
                fix.style.height = 24;
                card.Add(fix);
            }
            else
            {
                card.Add(new HelpBox(
                    "LLMUnity package is installed and CoreAI assemblies see COREAI_HAS_LLMUNITY.",
                    HelpBoxMessageType.Info));
            }

            return card;
        }

        /// <summary>
        /// GGUF model picker. With LLMUnity present: dropdown of Model Manager entries +
        /// Browse + Rescan + manual override. Without LLMUnity: plain path field.
        /// </summary>
        private void BuildGgufModelControl(VisualElement parent)
        {
#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL
            VisualElement row = new();
            row.style.flexDirection = FlexDirection.Row;

            _ggufDropdown = new DropdownField("GGUF Model", new List<string> { "[ Auto / Fallback ]" }, 0)
            {
                tooltip =
                    "Model known to LLMUnity Model Manager. Empty = auto/fallback. Use Browse or Manual override for a specific .gguf file."
            };
            _ggufDropdown.style.flexGrow = 1;
            _ggufDropdown.RegisterValueChangedCallback(evt =>
            {
                int index = _ggufDropdown.choices.IndexOf(evt.newValue);
                if (index >= 0 && index < _ggufFileNames.Count)
                {
                    serializedObject.FindProperty("ggufModelPath").stringValue = _ggufFileNames[index];
                    serializedObject.ApplyModifiedProperties();
                }
            });
            row.Add(_ggufDropdown);

            Button browse = new(() =>
            {
                string path = EditorUtility.OpenFilePanel("Select GGUF Model", "", "gguf");
                if (!string.IsNullOrEmpty(path))
                {
                    serializedObject.FindProperty("ggufModelPath").stringValue = Path.GetFileName(path);
                    serializedObject.ApplyModifiedProperties();
                }
            })
            {
                text = "Browse"
            };
            browse.style.width = 64;
            row.Add(browse);

            Button rescan = new(() =>
            {
                try
                {
                    LLMManager.LoadFromDisk();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[CoreAI] GGUF rescan: LLMManager.LoadFromDisk failed: {ex.Message}");
                }

                RefreshGgufChoices();
            })
            {
                text = "Rescan",
                tooltip = "Re-scan the GGUF models known to the LLMUnity Model Manager."
            };
            rescan.style.width = 58;
            row.Add(rescan);
            parent.Add(row);

            TextField manual = new("Manual override")
            {
                isDelayed = true,
                bindingPath = "ggufModelPath",
                tooltip = "Type a .gguf filename when it is not listed in the dropdown."
            };
            manual.style.marginLeft = 12;
            parent.Add(manual);

            _ggufEmptyHint = MakeDynamicHelpBox(HelpBoxMessageType.None);
            _ggufEmptyHint.text =
                "LLMUnity Model Manager did not report any GGUF models.\n" +
                "Open the LLMUnity Model Manager or choose a file with Browse / Manual override.";
            parent.Add(_ggufEmptyHint);

            RefreshGgufChoices();
#else
            PropertyField path = Field("ggufModelPath", "GGUF Path",
                "Relative .gguf model path. Empty = auto/fallback.");
            parent.Add(path);
            parent.Add(new HelpBox(
                "LLMUnity package is not active, so the model dropdown is unavailable. " +
                "If package ai.undream.llm is installed, use the LLMUnity status helper under Advanced Settings to fix CoreAI asmdef versionDefines.",
                HelpBoxMessageType.None));
#endif
        }

#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL
        private void RefreshGgufChoices()
        {
            List<string> options = new() { "[ Auto / Fallback ]" };
            _ggufFileNames.Clear();
            _ggufFileNames.Add("");

            int discovered = 0;
            try
            {
                // ModelEntries can be empty until the LLMUnity manager scans disk.
                if (LLMManager.modelEntries == null || LLMManager.modelEntries.Count == 0)
                {
                    LLMManager.LoadFromDisk();
                }

                if (LLMManager.modelEntries != null)
                {
                    foreach (ModelEntry entry in LLMManager.modelEntries)
                    {
                        if (entry == null || entry.lora)
                        {
                            continue;
                        }

                        options.Add(entry.filename);
                        _ggufFileNames.Add(entry.filename);
                        discovered++;
                    }
                }
            }
            catch (Exception ex)
            {
                // Keep the inspector usable even if LLMUnity cannot scan the model directory.
                Debug.LogWarning($"[CoreAI] GGUF dropdown: LLMManager.LoadFromDisk failed: {ex.Message}");
            }

            string currentValue = serializedObject.FindProperty("ggufModelPath")?.stringValue ?? "";
            int currentIndex = _ggufFileNames.IndexOf(currentValue);
            if (currentIndex == -1 && !string.IsNullOrEmpty(currentValue))
            {
                options.Add(currentValue + " (manual)");
                _ggufFileNames.Add(currentValue);
                currentIndex = _ggufFileNames.Count - 1;
            }
            else if (currentIndex == -1)
            {
                currentIndex = 0;
            }

            _ggufDropdown.choices = options;
            _ggufDropdown.SetValueWithoutNotify(options[currentIndex]);
            if (_ggufEmptyHint != null)
            {
                _ggufEmptyHint.style.display = discovered == 0 ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }
#endif

        /// <summary>
        /// Re-evaluates every conditional row, warning, and hint after any settings change.
        /// </summary>
        private void RefreshDynamicState()
        {
            CoreAISettingsAsset settings = Settings;
            if (settings == null)
            {
                return;
            }

            bool isAuto = settings.BackendType == LlmBackendType.Auto ||
                          settings.ExecutionMode == LlmExecutionMode.Auto;
            bool showHttp = isAuto || settings.UseHttpApi;
            bool showLlmUnity = isAuto || settings.ExecutionMode == LlmExecutionMode.LocalModel;
            bool isWebGlTarget = EditorUserBuildSettings.activeBuildTarget == BuildTarget.WebGL;

            Show(_autoPriorityRow, isAuto);
            Show(_httpEssentials, showHttp);
            Show(_llmUnityEssentials, showLlmUnity);

            string productionWarning = CoreAI.Editor.CoreAIProductionSettingsValidator.GetWebGlClientKeyWarning(
                settings, isWebGlTarget);
            _productionWarning.text = productionWarning ?? "";
            Show(_productionWarning, !string.IsNullOrEmpty(productionWarning));

            SerializedProperty streaming = serializedObject.FindProperty("enableStreaming");
            SerializedProperty nativeSse = serializedObject.FindProperty("webGlNativeStreaming");
            bool missingNativeSse = isWebGlTarget && streaming.boolValue && !nativeSse.boolValue;
            Show(_webGlStreamingHint, missingNativeSse);
            Show(_webGlNoNativeSseWarning, missingNativeSse);
            Show(_webGlCredentialsInfo,
                isWebGlTarget && serializedObject.FindProperty("sameOriginCredentials").boolValue);

            // Model preset mirrors the model field without writing back.
            string model = serializedObject.FindProperty("modelName").stringValue?.Trim() ?? "";
            _modelPreset.SetValueWithoutNotify(
                Array.IndexOf(HttpModelPresets, model) >= 0 ? model : CustomPresetLabel);

            _httpAdvancedGroup.SetEnabled(isAuto || settings.UseHttpApi);
            Show(_serverManagedWarning, settings.ExecutionMode == LlmExecutionMode.ServerManagedApi);
            string priorityHint = settings.AutoPriority == LlmAutoPriority.HttpFirst
                ? "HTTP API -> LLMUnity -> Offline"
                : "LLMUnity -> HTTP API -> Offline";
            _httpAutoHint.text = $"Auto priority: {priorityHint}.";
            Show(_httpAutoHint, isAuto);
            _llmUnityAutoHint.text = $"Auto priority: {priorityHint}.";
            Show(_llmUnityAutoHint, isAuto);
            _llmUnityAdvancedGroup.SetEnabled(isAuto || settings.ExecutionMode == LlmExecutionMode.LocalModel);

            bool fallbackEnabled = serializedObject.FindProperty("enableFallbackBackend").boolValue;
            bool missingUrl = string.IsNullOrWhiteSpace(
                serializedObject.FindProperty("secondaryApiBaseUrl").stringValue);
            bool missingModel = string.IsNullOrWhiteSpace(
                serializedObject.FindProperty("secondaryModelName").stringValue);
            Show(_fallbackIncompleteWarning, fallbackEnabled && (missingUrl || missingModel));
            Show(_fallbackActiveInfo, settings.HasValidFallbackBackend);

            _temperatureField.SetEnabled(settings.OverrideTemperature);
            Show(_emptyPrefixHint, string.IsNullOrEmpty(settings.UniversalSystemPromptPrefix));

            Show(_offlineCustomGroup, settings.OfflineUseCustomResponse);
            Show(_offlineDefaultInfo, !settings.OfflineUseCustomResponse);

            _routeHint.text = BuildRouteHint(settings, priorityHint);
#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL
            if (_ggufDropdown != null)
            {
                RefreshGgufChoices();
            }
#endif
        }

        private static string BuildRouteHint(CoreAISettingsAsset settings, string priorityHint)
        {
            switch (settings.BackendType)
            {
                case LlmBackendType.OpenAiHttp:
                    return $"HTTP API: {settings.ApiBaseUrl} (model: {settings.ModelName})";
                case LlmBackendType.LlmUnity:
                    return "LLMUnity: LLMAgent scene/runtime host with GGUF model";
                case LlmBackendType.Auto:
                    return $"Auto: {priorityHint}";
                case LlmBackendType.Offline:
                    return "Offline mode: no networked LLM will be used";
                default:
                    return "";
            }
        }

        private void UpdateTestResultUi()
        {
            if (_testButton != null)
            {
                _testButton.SetEnabled(!_isTestingConnection);
                _testButton.text = _isTestingConnection ? "Testing..." : "Test Connection";
            }

            if (_testResultBox == null)
            {
                return;
            }

            bool hasMessage = !string.IsNullOrEmpty(_testResultMessage);
            _testResultBox.style.display = hasMessage ? DisplayStyle.Flex : DisplayStyle.None;
            if (hasMessage)
            {
                _testResultBox.text = _testResultMessage;
                _testResultBox.messageType = ToHelpBoxType(_testResultType);
            }
        }

        private static HelpBoxMessageType ToHelpBoxType(MessageType type)
        {
            switch (type)
            {
                case MessageType.Error: return HelpBoxMessageType.Error;
                case MessageType.Warning: return HelpBoxMessageType.Warning;
                case MessageType.Info: return HelpBoxMessageType.Info;
                default: return HelpBoxMessageType.None;
            }
        }

        // ---------- UI helpers ----------

        private static VisualElement MakeCard()
        {
            VisualElement card = new();
            card.style.marginTop = 4;
            card.style.marginBottom = 4;
            card.style.paddingTop = 6;
            card.style.paddingBottom = 6;
            card.style.paddingLeft = 8;
            card.style.paddingRight = 8;
            card.style.borderTopWidth = 1;
            card.style.borderBottomWidth = 1;
            card.style.borderLeftWidth = 1;
            card.style.borderRightWidth = 1;
            Color border = new(0.35f, 0.35f, 0.35f, 0.6f);
            card.style.borderTopColor = border;
            card.style.borderBottomColor = border;
            card.style.borderLeftColor = border;
            card.style.borderRightColor = border;
            card.style.borderTopLeftRadius = 4;
            card.style.borderTopRightRadius = 4;
            card.style.borderBottomLeftRadius = 4;
            card.style.borderBottomRightRadius = 4;
            return card;
        }

        private static Label MakeSectionLabel(string text)
        {
            Label label = new(text);
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.marginBottom = 4;
            return label;
        }

        private static Label MakeMiniLabel(string text)
        {
            Label label = new(text);
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.fontSize = 10;
            label.style.marginTop = 6;
            label.style.marginBottom = 2;
            return label;
        }

        private static HelpBox MakeDynamicHelpBox(HelpBoxMessageType type)
        {
            HelpBox box = new("", type);
            box.style.display = DisplayStyle.None;
            box.style.marginTop = 2;
            box.style.marginBottom = 2;
            return box;
        }

        private PropertyField Field(string propertyPath, string label, string tooltip)
        {
            PropertyField field = new(serializedObject.FindProperty(propertyPath), label);
            if (!string.IsNullOrEmpty(tooltip))
            {
                field.tooltip = tooltip;
            }

            return field;
        }

        private static Foldout MakePersistentFoldout(string title, string prefKey, bool defaultOpen)
        {
            Foldout foldout = new()
            {
                text = title,
                value = EditorPrefs.GetBool(prefKey, defaultOpen)
            };
            foldout.style.marginTop = 2;
            foldout.RegisterValueChangedCallback(evt =>
            {
                // Child foldout toggles bubble ChangeEvent<bool>; persist only this foldout's own state.
                if (ReferenceEquals(evt.target, foldout))
                {
                    EditorPrefs.SetBool(prefKey, evt.newValue);
                }
            });
            return foldout;
        }

        private static void Show(VisualElement element, bool visible)
        {
            if (element != null)
            {
                element.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }

        // ---------- LLMUnity wiring helpers ----------

        private static bool IsLlmUnityPackageInstalled()
        {
            return UnityEditor.PackageManager.PackageInfo.FindForPackageName("ai.undream.llm") != null;
        }

        private static bool IsLlmUnityDefineActive()
        {
#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL
            return true;
#else
            return false;
#endif
        }

        private static void FixLlmUnityAsmdefWiring()
        {
            string[] asmdefPaths =
            {
                "Assets/CoreAiUnity/Runtime/Source/CoreAI.Source.asmdef",
                "Assets/CoreAiUnity/Editor/CoreAI.Editor.asmdef",
                "Assets/CoreAiUnity/Tests/CoreAI.Tests.asmdef",
                "Assets/CoreAiUnity/Tests/PlayMode/LlmInfra/CoreAI.Tests.PlayMode.LlmInfra.asmdef",
                "Assets/CoreAiUnity/Tests/PlayMode/LlmVerification/CoreAI.Tests.PlayMode.LlmVerification.asmdef",
                "Assets/CoreAiUnity/Tests/PlayMode/Scenarios/CoreAI.Tests.PlayMode.Scenarios.asmdef"
            };

            int changed = 0;
            foreach (string assetPath in asmdefPaths)
            {
                string fullPath = Path.GetFullPath(assetPath);
                if (!File.Exists(fullPath))
                {
                    Debug.LogWarning($"[CoreAI] LLMUnity wiring: asmdef not found: {assetPath}");
                    continue;
                }

                string text = File.ReadAllText(fullPath);
                string updated = Regex.Replace(
                    text,
                    "\"name\"\\s*:\\s*\"undream\\.llmunity\"\\s*,\\s*\"expression\"\\s*:\\s*\"\"",
                    "\"name\": \"ai.undream.llm\",\n      \"expression\": \"1.0.0\"");

                if (updated == text)
                {
                    continue;
                }

                File.WriteAllText(fullPath, updated);
                AssetDatabase.ImportAsset(assetPath);
                changed++;
            }

            AssetDatabase.Refresh();
            string message = changed > 0
                ? $"[CoreAI] LLMUnity asmdef wiring updated in {changed} file(s). Unity will recompile; reopen this inspector after compilation."
                : "[CoreAI] LLMUnity asmdef wiring already looks correct.";
            Debug.Log(message);
            EditorUtility.DisplayDialog("CoreAI LLMUnity Wiring", message, "OK");
        }

        // ---------- Connection test ----------

        /// <summary>
        /// Runs the selected connection test.
        /// </summary>
        private async void TestConnection(CoreAISettingsAsset settings)
        {
            _isTestingConnection = true;
            _testResultMessage = "";
            UpdateTestResultUi();

            Debug.Log("[CoreAI Test] Starting connection test");
            Debug.Log($"[CoreAI Test] Backend: {settings.BackendType}");

            try
            {
                switch (settings.BackendType)
                {
                    case LlmBackendType.OpenAiHttp:
                        await TestHttpConnection(settings);
                        break;

                    case LlmBackendType.LlmUnity:
                        TestLlmUnityConnection(settings);
                        break;

                    case LlmBackendType.Auto:
                        // Auto tests the configured priority chain.
                        await TestAutoConnection(settings);
                        break;

                    case LlmBackendType.Offline:
                        _testResultMessage =
                            "Offline mode is active.\n\nSwitch to HTTP API or LLMUnity to test a live model.";
                        _testResultType = MessageType.Info;
                        break;
                }
            }
            catch (Exception ex)
            {
                _testResultMessage = $"Connection test failed: {ex.Message}";
                _testResultType = MessageType.Error;
            }
            finally
            {
                _isTestingConnection = false;
                UpdateTestResultUi();

                // Mirror the inspector result in the Console for automation logs.
                if (!string.IsNullOrEmpty(_testResultMessage))
                {
                    switch (_testResultType)
                    {
                        case MessageType.Error:
                            Debug.LogError($"[CoreAI Test] {_testResultMessage}");
                            break;
                        case MessageType.Warning:
                            Debug.LogWarning($"[CoreAI Test] {_testResultMessage}");
                            break;
                        case MessageType.Info:
                            Debug.Log($"[CoreAI Test] {_testResultMessage}");
                            break;
                    }
                }
            }
        }

        /// <summary>
        /// Tests HTTP API by probing /models for small local APIs, then chat completions.
        /// </summary>
        private async System.Threading.Tasks.Task TestHttpConnection(CoreAISettingsAsset settings)
        {
            string baseUrl = settings.ApiBaseUrl.TrimEnd('/');

            // Large hosted APIs can return very large /models payloads, so go straight to chat completions.
            bool isLargeApi = baseUrl.Contains("openrouter") || baseUrl.Contains("api.openai.com");

            if (isLargeApi)
            {
                Debug.Log(
                    "[CoreAI Test] Large API detected, skipping /models check, going straight to chat completions");
                await TestViaChatCompletions(settings);
                return;
            }

            // Local APIs such as LM Studio usually have a small /models payload.
            string modelsUrl = baseUrl + "/models";
            Debug.Log($"[CoreAI Test] Probing API: {modelsUrl}");

            using (UnityWebRequest req = UnityWebRequest.Get(modelsUrl))
            {
                req.timeout = 10;
                UnityWebRequestAsyncOperation op = req.SendWebRequest();
                while (!op.isDone)
                {
                    await System.Threading.Tasks.Task.Yield();
                }

                if (req.result != UnityWebRequest.Result.Success)
                {
                    Debug.Log("[CoreAI Test] /models probe failed, falling back to chat completions...");
                    await TestViaChatCompletions(settings);
                    return;
                }

                string responseText = req.downloadHandler.text;
                Debug.Log($"[CoreAI Test] /models returned {responseText.Length} characters");

                // Guard against unexpectedly large provider payloads.
                if (responseText.Length > 100000)
                {
                    Debug.Log("[CoreAI Test] Response too large, skipping model check");
                    await TestViaChatCompletions(settings);
                    return;
                }

                // Always verify the actual chat completion route.
                await TestViaChatCompletions(settings);
            }
        }

        /// <summary>
        /// Sends a minimal chat completions request.
        /// </summary>
        private async System.Threading.Tasks.Task TestViaChatCompletions(CoreAISettingsAsset settings)
        {
            string url = settings.ApiBaseUrl.TrimEnd('/') + "/chat/completions";

            string jsonBody =
                $"{{\"model\":\"{settings.ModelName}\",\"messages\":[{{\"role\":\"user\",\"content\":\"Say exactly: OK\"}}],\"max_tokens\":64}}";

            Debug.Log($"[CoreAI Test] Chat URL: {url}");
            Debug.Log($"[CoreAI Test] Chat request: {jsonBody}");

            using (UnityWebRequest req = new(url, "POST"))
            {
                byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonBody);
                req.uploadHandler = new UploadHandlerRaw(bodyRaw);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.SetRequestHeader(OpenAiHttpConstants.HttpRefererHeaderName,
                    OpenAiHttpConstants.HttpRefererUnityUrl);
                req.SetRequestHeader("X-Title", "CoreAI");

                if (!string.IsNullOrEmpty(settings.ApiKey))
                {
                    req.SetRequestHeader("Authorization", "Bearer " + settings.ApiKey);
                }

                req.timeout = settings.RequestTimeoutSeconds;

                UnityWebRequestAsyncOperation op = req.SendWebRequest();
                while (!op.isDone)
                {
                    await System.Threading.Tasks.Task.Yield();
                }

                if (req.result != UnityWebRequest.Result.Success)
                {
                    string error = req.error;
                    string responseText = req.downloadHandler?.text ?? "";

                    // Log the raw provider response only on failure.
                    if (!string.IsNullOrEmpty(responseText))
                    {
                        Debug.LogError($"[CoreAI Test] Error response: {responseText}");
                    }

                    // Parse OpenAI-compatible error envelopes when present.
                    if (!string.IsNullOrEmpty(responseText) && responseText.Contains("\"error\""))
                    {
                        try
                        {
                            dynamic json = Newtonsoft.Json.JsonConvert.DeserializeObject<dynamic>(responseText);
                            string serverError = json?.error?.message?.ToString();
                            string errorCode = json?.error?.code?.ToString();
                            string errorType = json?.error?.type?.ToString();

                            // OpenRouter may include the useful provider error in metadata.raw.
                            string rawMessage = json?.error?.metadata?.raw?.ToString();
                            if (!string.IsNullOrEmpty(rawMessage))
                            {
                                serverError = rawMessage;
                            }

                            if (!string.IsNullOrEmpty(serverError))
                            {
                                error = serverError;
                                if (!string.IsNullOrEmpty(errorCode))
                                {
                                    error = $"[{errorCode}] {error}";
                                }

                                if (!string.IsNullOrEmpty(errorType))
                                {
                                    error += $" (type: {errorType})";
                                }
                            }
                        }
                        catch
                        {
                            /* ignore */
                        }
                    }

                    // Add a short operator hint for common provider failures.
                    string hint = "";
                    if (error.Contains("authentication") || error.Contains("Unauthorized") ||
                        error.Contains("invalid_api") || error.Contains("api_key"))
                    {
                        hint = "\n\nCheck the API key.";
                    }
                    else if (error.Contains("model") || error.Contains("not_found") || error.Contains("does not exist"))
                    {
                        hint = "\n\nCheck that the selected model exists on this provider.";
                    }
                    else if (error.Contains("credit") || error.Contains("billing") || error.Contains("payment"))
                    {
                        hint = "\n\nCheck provider credits or billing.";
                    }
                    else if (error.Contains("rate") || error.Contains("too_many") || error.Contains("429"))
                    {
                        hint = "\n\nRate limited. Wait 30-60 seconds or check provider limits.";
                    }
                    else if (error.Contains("temporarily") || error.Contains("upstream"))
                    {
                        hint = "\n\nProvider is temporarily unavailable. Try again later.";
                    }
                    else if (error.Contains("timeout") || error.Contains("connect"))
                    {
                        hint = "\n\nCheck the Base URL and local server status.";
                    }

                    _testResultMessage =
                        $"Chat completions failed:\n{error}{hint}\n\nURL: {url}\nModel: {settings.ModelName}";
                    _testResultType = MessageType.Error;
                    Debug.LogError($"[CoreAI Test] Chat completions failed: {error}");
                }
                else
                {
                    string responseText = req.downloadHandler.text;

                    // Basic OpenAI-compatible success shape.
                    bool hasContent = responseText.Contains("\"content\"") ||
                                      responseText.Contains("\"choices\"");

                    if (hasContent)
                    {
                        // Extract a short preview when possible.
                        string content = "";
                        try
                        {
                            dynamic json = Newtonsoft.Json.JsonConvert.DeserializeObject<dynamic>(responseText);
                            content = json?.choices?[0]?.message?.content?.ToString() ?? "";

                            // Usage is optional but useful when providers include it.
                            dynamic usage = json?.usage;
                            if (usage != null)
                            {
                                int promptTokens = (int)usage?.prompt_tokens;
                                int completionTokens = (int)usage?.completion_tokens;
                                Debug.Log(
                                    $"[CoreAI Test] Token usage: prompt={promptTokens}, completion={completionTokens}");
                            }
                        }
                        catch
                        {
                            /* ignore */
                        }

                        if (!string.IsNullOrEmpty(content))
                        {
                            _testResultMessage =
                                $"Connection succeeded.\n\nBase URL: {settings.ApiBaseUrl}\nModel: {settings.ModelName}\nResponse: \"{content}\"";
                            _testResultType = MessageType.Info;
                            Debug.Log($"[CoreAI Test] Connection successful! Response: {content}");
                        }
                        else
                        {
                            Debug.LogWarning($"[CoreAI Test] Empty-content response: {responseText}");
                            _testResultMessage =
                                "Provider returned choices but no message content. Check the model response shape, reasoning settings, or max output tokens.";
                            _testResultType = MessageType.Warning;
                        }
                    }
                    else
                    {
                        _testResultMessage =
                            "Provider response did not look OpenAI-compatible. Check the endpoint and model.";
                        _testResultType = MessageType.Warning;
                    }
                }
            }
        }

        /// <summary>
        /// Tests LLMUnity scene/runtime wiring.
        /// </summary>
        private void TestLlmUnityConnection(CoreAISettingsAsset settings)
        {
#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL
            LLMAgent agent = null;

            // Prefer the explicitly named agent, then fall back to the first LLMAgent in the scene.
            if (!string.IsNullOrEmpty(settings.LlmUnityAgentName))
            {
                GameObject go = GameObject.Find(settings.LlmUnityAgentName);
                if (go != null)
                {
                    agent = go.GetComponent<LLMAgent>();
                }
            }

            // Fallback:
            if (agent == null)
            {
                agent = FindFirstObjectByType<LLMAgent>();
            }

            if (agent == null)
            {
                _testResultMessage =
                    "LLMAgent was not found.\n\nAdd an LLMAgent GameObject to the scene or enable auto-created runtime host settings.";
                _testResultType = MessageType.Error;
                return;
            }

            LLM llm = agent.GetComponent<LLM>();
            if (llm == null)
            {
                _testResultMessage =
                    "The LLMAgent GameObject has no LLM component.\n\nAdd an LLM component and assign a GGUF model.";
                _testResultType = MessageType.Error;
                return;
            }

            if (string.IsNullOrWhiteSpace(llm.model))
            {
                _testResultMessage =
                    $"LLM model is empty.\n\nGameObject: {agent.gameObject.name}\nAssign a GGUF model.";
                _testResultType = MessageType.Warning;
                return;
            }

            string modelPath = LLMManager.GetAssetPath(llm.model);
            bool modelExists = !string.IsNullOrEmpty(modelPath) && File.Exists(modelPath);

            if (llm.started && !llm.failed)
            {
                _testResultMessage =
                    $"LLMAgent is running.\n\nGameObject: {agent.gameObject.name}\nModel: {llm.model}\nPath: {modelPath ?? "N/A"}";
                _testResultType = MessageType.Info;
            }
            else if (modelExists)
            {
                _testResultMessage =
                    $"LLMAgent is configured but not started yet.\n\nGameObject: {agent.gameObject.name}\nModel: {llm.model}\nThe model file was found.";
                _testResultType = MessageType.Info;
            }
            else
            {
                _testResultMessage =
                    $"GGUF model file was not found.\n\nModel: {llm.model}\nPath: {modelPath ?? "N/A"}\nCheck LLMUnity Model Manager.";
                _testResultType = MessageType.Error;
            }
#else
            _testResultMessage =
                "LLMUnity test is unavailable in this build context (package missing or UNITY_WEBGL). Use HTTP API or Offline mode.";
            _testResultType = MessageType.Warning;
#endif
        }

        /// <summary>
        /// Tests the Auto route order and reports the first usable backend.
        /// </summary>
        private async System.Threading.Tasks.Task TestAutoConnection(CoreAISettingsAsset settings)
        {
            StringBuilder messages = new();
            bool anyWorking = false;

            string priorityText = settings.AutoPriority == LlmAutoPriority.HttpFirst
                ? "HTTP API -> LLMUnity -> Offline"
                : "LLMUnity -> HTTP API -> Offline";
            messages.AppendLine($"Auto priority: {priorityText}");
            messages.AppendLine();

#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL
            // 1. LLMUnity
            LLMAgent agent = null;
            if (!string.IsNullOrEmpty(settings.LlmUnityAgentName))
            {
                GameObject go = GameObject.Find(settings.LlmUnityAgentName);
                if (go != null)
                {
                    agent = go.GetComponent<LLMAgent>();
                }
            }

            if (agent == null)
            {
                agent = FindFirstObjectByType<LLMAgent>();
            }

            if (agent != null)
            {
                LLM llm = agent.GetComponent<LLM>();
                if (llm != null && !string.IsNullOrWhiteSpace(llm.model))
                {
                    string modelPath = LLMManager.GetAssetPath(llm.model);
                    bool modelExists = !string.IsNullOrEmpty(modelPath) && File.Exists(modelPath);

                    if (llm.started && !llm.failed)
                    {
                        messages.AppendLine("1. LLMUnity: running");
                        messages.AppendLine($"   Model: {llm.model}");
                        messages.AppendLine($"   Path: {modelPath ?? "N/A"}");
                        anyWorking = true;
                    }
                    else if (modelExists)
                    {
                        messages.AppendLine("1. LLMUnity: configured, model file found");
                        messages.AppendLine($"   Model: {llm.model}");
                        messages.AppendLine("   It can start when the local backend is requested.");
                        anyWorking = true;
                    }
                    else
                    {
                        messages.AppendLine("1. LLMUnity: GGUF file not found");
                        messages.AppendLine($"   Model: {llm.model}");
                    }
                }
                else
                {
                    messages.AppendLine("1. LLMUnity: LLM component or model is missing");
                }
            }
            else
            {
                messages.AppendLine("1. LLMUnity: LLMAgent not found");
            }

            messages.AppendLine();
#endif

            // 2. HTTP API
            if (!string.IsNullOrEmpty(settings.ApiBaseUrl))
            {
                messages.AppendLine($"2. HTTP API: {settings.ApiBaseUrl}");
                messages.AppendLine($"   Model: {settings.ModelName}");

                try
                {
                    string url = settings.ApiBaseUrl.TrimEnd('/') + "/chat/completions";
                    string jsonBody =
                        $"{{\"model\":\"{settings.ModelName}\",\"messages\":[{{\"role\":\"user\",\"content\":\"Say OK\"}}],\"max_tokens\":10}}";

                    using (UnityWebRequest req = new(url, "POST"))
                    {
                        byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonBody);
                        req.uploadHandler = new UploadHandlerRaw(bodyRaw);
                        req.downloadHandler = new DownloadHandlerBuffer();
                        req.SetRequestHeader("Content-Type", "application/json");
                        req.SetRequestHeader(OpenAiHttpConstants.HttpRefererHeaderName,
                            OpenAiHttpConstants.HttpRefererUnityUrl);
                        req.SetRequestHeader("X-Title", "CoreAI");

                        if (!string.IsNullOrEmpty(settings.ApiKey))
                        {
                            req.SetRequestHeader("Authorization", "Bearer " + settings.ApiKey);
                        }

                        req.timeout = 15;

                        UnityWebRequestAsyncOperation op = req.SendWebRequest();
                        while (!op.isDone)
                        {
                            await System.Threading.Tasks.Task.Yield();
                        }

                        if (req.result == UnityWebRequest.Result.Success)
                        {
                            string responseText = req.downloadHandler.text;
                            if (responseText.Contains("\"content\"") || responseText.Contains("\"choices\""))
                            {
                                messages.AppendLine("2. HTTP API: chat completions succeeded");
                                anyWorking = true;
                            }
                            else
                            {
                                messages.AppendLine("2. HTTP API: response did not include choices/content");
                            }
                        }
                        else
                        {
                            string error = req.error;
                            if (!string.IsNullOrEmpty(req.downloadHandler?.text))
                            {
                                try
                                {
                                    dynamic json =
                                        Newtonsoft.Json.JsonConvert
                                            .DeserializeObject<dynamic>(req.downloadHandler.text);
                                    error = json?.error?.message?.ToString() ?? error;
                                }
                                catch
                                {
                                    /* ignore */
                                }
                            }

                            messages.AppendLine($"2. HTTP API: {error}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    messages.AppendLine($"2. HTTP API: {ex.Message}");
                }
            }
            else
            {
                messages.AppendLine("2. HTTP API: Base URL is empty");
            }

            messages.AppendLine();

            // 3. Summarize route health.
            if (anyWorking)
            {
                messages.AppendLine("Auto route has at least one usable backend.");
                _testResultMessage = messages.ToString();
                _testResultType = MessageType.Info;
            }
            else
            {
                messages.AppendLine("No live backend was confirmed. Check LLMUnity model setup or HTTP API settings.");
                _testResultMessage = messages.ToString();
                _testResultType = MessageType.Warning;
            }
        }
    }
}
#endif
