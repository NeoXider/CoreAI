using CoreAI;
using CoreAI.Ai;
using UnityEngine;
using UnityEngine.Serialization;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>Legacy coarse backend selector surfaced in older scenes.</summary>
    public enum LlmBackendType
    {
        /// <summary>Try LLMUnity, then HTTP, then Offline.</summary>
        Auto = 0,

        /// <summary>On-device GGUF via LLMUnity.</summary>
        LlmUnity = 1,

        /// <summary>Uses the built-in OpenAI-compatible HTTP transport as the primary LLM backend.</summary>
        OpenAiHttp = 2,

        /// <summary>Uses the offline fallback backend instead of a remote LLM provider.</summary>
        Offline = 3
    }

    /// <summary>Preference order inside <see cref="LlmBackendType.Auto"/>.</summary>
    public enum LlmAutoPriority
    {
        /// <summary>Prefers LLMUnity and falls back to HTTP when configured.</summary>
        LlmUnityFirst = 0,

        /// <summary>Prefers HTTP and falls back to LLMUnity when configured.</summary>
        HttpFirst = 1
    }

    /// <summary>
    /// ScriptableObject implementation of CoreAI runtime and LLM settings.
    /// </summary>
    [CreateAssetMenu(menuName = "CoreAI/CoreAI Settings", fileName = "CoreAISettings")]
    public sealed class CoreAISettingsAsset : ScriptableObject, ICoreAISettings
    {
        #region Singleton

        private static CoreAISettingsAsset _instance;

        /// <summary>Resources-backed singleton (<c>CoreAISettings</c>) overridden by LifetimeScope injection.</summary>
        public static CoreAISettingsAsset Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = Resources.Load<CoreAISettingsAsset>("CoreAISettings");
                }

                return _instance;
            }
        }

        /// <summary>Assign runtime instance from composition root.</summary>
        public static void SetInstance(CoreAISettingsAsset settings)
        {
            _instance = settings;
        }

        /// <summary>Clears static cache (tests).</summary>
        public static void ResetInstance()
        {
            _instance = null;
        }

        #endregion

        #region LLM settings

        [Header("LLM Backend")]
        [Tooltip("Runtime backend preset: Auto, LLMUnity, HTTP API, or Offline.")]
        [SerializeField]
        private LlmBackendType backendType = LlmBackendType.Auto;

        [Tooltip("Product-facing LLM mode. Auto preserves legacy backend selection.")] [SerializeField]
        private LlmExecutionMode executionMode = LlmExecutionMode.Auto;

        [Tooltip("When backend is Auto, prefer LLMUnity or HTTP API first.")] [SerializeField]
        private LlmAutoPriority autoPriority = LlmAutoPriority.LlmUnityFirst;

        [Header("HTTP API (OpenAI-compatible)")]
        [Tooltip(
            "Base URL without trailing slash (e.g., https://api.openai.com/v1 or http://localhost:1234/v1 for LM Studio).")]
        [SerializeField]
        private string apiBaseUrl = "http://localhost:1234/v1";

        [Tooltip("Bearer/API key - required for hosted providers; empty for many local gateways.")] [SerializeField]
        private string apiKey = "";

        [Tooltip("Model identifier passed to OpenAI-compatible servers (gpt-4o-mini, qwen3.5-4b, etc.).")]
        [SerializeField]
        private string modelName = "gpt-4o-mini";

        [Tooltip(
            "When enabled, the Temperature value below is sent to OpenAI-compatible HTTP APIs and LLMUnity (MEAI). " +
            "When disabled, sampling temperature is omitted so each backend uses its own default.")]
        [SerializeField]
        [FormerlySerializedAs("overrideTemperature")]
        private bool enableTemperatureOverriding;

        [Tooltip(
            "Sampling temperature (0.0 = deterministic, 2.0 = creative). Used only when temperature override is on.")]
        [SerializeField]
        [Range(0f, 2f)]
        private float temperature = 0.1f;

        [Tooltip("Max tokens per completion across HTTP + LLMUnity when no per-call override. 0 = provider default.")]
        [SerializeField]
        private int maxTokens = 2048;

        [Header("Client limits")] [SerializeField] [Min(0)]
        private int maxClientLimitedRequestsPerSession;

        [SerializeField] [Min(0)] private int maxClientLimitedPromptChars;

        [Tooltip("HTTP layer timeout seconds (fallback 120 when unset).")] [SerializeField] [Min(0)]
        private int requestTimeoutSeconds = 120;

        [Header("Fallback Backend (secondary)")]
        [Tooltip(
            "When enabled and secondary URL/model are set, requests that fail on the primary backend " +
            "are automatically retried on the secondary. Useful for local model + cloud fallback.")]
        [SerializeField]
        private bool enableFallbackBackend;

        [Tooltip("Secondary backend base URL (e.g., https://api.openai.com/v1).")] [SerializeField]
        private string secondaryApiBaseUrl = "";

        [Tooltip("Secondary backend API key.")] [SerializeField]
        private string secondaryApiKey = "";

        [Tooltip("Secondary backend model name (e.g., gpt-4o-mini).")] [SerializeField]
        private string secondaryModelName = "";

        [Header("LLMUnity (local model)")]
        [Tooltip("GameObject hosting LLMAgent; empty auto-detects.")]
        [SerializeField]
        private string llmUnityAgentName = "";

        [Tooltip(
            "When no LLMAgent exists in loaded scenes, spawn a hidden LLM+LLMAgent host (see Runtime host name). " +
            "Disable if you always place LLMAgent in the scene manually.")]
        [SerializeField]
        private bool llmUnityAutoCreateRuntimeHost = true;

        [Tooltip("GameObject name for the auto-created LLMUnity host. Empty = CoreAI_LLMUnity_Runtime.")]
        [SerializeField]
        private string llmUnityRuntimeHostObjectName = "";

        [Tooltip(
            "After play starts, warm up the local llama.cpp server so the first chat request is faster. " +
            "Uses Startup Timeout from this asset. Disable to defer load until the first LLM call.")]
        [SerializeField]
        private bool llmUnityAutostartLocalServer = true;

        [Tooltip("Relative GGUF path; falls back to LLMUnity model manager hints when empty.")] [SerializeField]
        private string ggufModelPath = "Qwen3.5-2B-Q4_K_M.gguf";

        [Tooltip("Keep LLMAgent alive across loads (persist service).")] [SerializeField]
        private bool llmUnityDontDestroyOnLoad = true;

        [Tooltip("Seconds to wait for LLMUnity service startup.")] [SerializeField] [Min(5f)]
        private float llmUnityStartupTimeoutSeconds = 120f;

        [Tooltip("Delay after local model server reports ready.")] [SerializeField] [Min(0f)]
        private float llmUnityStartupDelaySeconds = 1f;

        [Tooltip("Keep LLMUnity server warm between turns (faster tests).")] [SerializeField]
        private bool llmUnityKeepAlive = false;

        [Tooltip(
            "Provider Default leaves HTTP request bodies unchanged. Disabled/Enabled sends provider-specific thinking controls for compatible models.")]
        [SerializeField]
        private LlmReasoningMode reasoningMode = LlmReasoningMode.ProviderDefault;

        [Tooltip(
            "Optional thinking budget for compatible OpenAI-style providers. 0 = omit. " +
            "When set, CoreAI sends thinking_budget together with reasoning controls.")]
        [SerializeField]
        [Min(0)]
        private int thinkingBudgetTokens;

        [Tooltip("Optional JSON object merged into OpenAI-compatible HTTP request bodies. Leave empty for provider default.")]
        [TextArea(2, 6)]
        [SerializeField]
        private string extraBodyJson = "";

        [Tooltip("Concurrent LLMUnity chat sessions (1 = strictly serial).")] [SerializeField] [Min(1)]
        private int llmUnityMaxConcurrentChats = 1;

        [Tooltip("GPU offload depth (0 = CPU only, 99 = all layers, LM Studio style).")] [SerializeField] [Min(0)]
        private int llmUnityNumGPULayers = 99;

        [Header("Shared agent defaults")]
        [Tooltip(
            "Universal system prefix prepended before every agent-specific system prompt.")]
        [TextArea(3, 6)]
        [SerializeField]
        private string universalSystemPromptPrefix = "Respond concisely and to the point. Avoid unnecessary verbosity.";

        [Tooltip(
            "Optional lines appended after the built-in ## Tool Contract block when a role has tools (before the tool list). Leave empty to use only default guidance.")]
        [TextArea(2, 8)]
        [SerializeField]
        private string toolContractAdditionalInstructions = "";

        [Tooltip(
            "Max Programmer Lua auto-repair attempts before aborting the repair loop (resets after success).")]
        [SerializeField]
        [Min(1)]
        private int maxLuaRepairRetries = 3;

        [Tooltip("Max consecutive tool-call failures before stopping the agent turn (resets after success).")]
        [SerializeField]
        [Min(1)]
        private int maxToolCallRetries = 3;

        [Tooltip("Allow identical tool+args invocations back-to-back (needed for intentional repeats).")]
        [SerializeField]
        private bool allowDuplicateToolCalls = false;

        [Tooltip(
            "Transport-level retries after HTTP 429 / 5xx (and equivalent failed completions). Each retry waits Retry-After or 2s, 4s, etc. (capped).")]
        [SerializeField]
        [Min(1)]
        private int maxLlmRequestRetries = 1;

        [Tooltip(
            "Max bounded retries after a provider context-length-exceeded error; each retry drops ~25% more of the oldest history (roadmap §5). 0 disables overflow recovery.")]
        [SerializeField]
        [Min(0)]
        private int maxContextOverflowRetries = 3;

        [Tooltip("Default context-window hint in tokens.")] [SerializeField] [Min(256)]
        private int contextWindowTokens = CoreAISettings.DefaultContextWindowTokens;

        [Header("Resilience & Safety")]
        [Tooltip(
            "Max chars per tool result before soft-truncation with ellipsis. Prevents a single tool from overflowing the context. " +
            "0 = no truncation. Default 8000 (~2000 tokens).")]
        [SerializeField]
        [Min(0)]
        private int maxToolResultChars = 8000;

        [Tooltip(
            "Per-tool execution timeout in milliseconds. If a tool body hangs (e.g. HTTP to a dead server), " +
            "it is cancelled after this duration. 0 = no per-tool timeout (relies on outer orchestrator timeout). Default 30000 (30s).")]
        [SerializeField]
        [Min(0)]
        private int defaultToolTimeoutMs = 30000;

        [Tooltip(
            "Max total response characters from the model before soft-truncation. Prevents runaway generation. " +
            "0 = disabled (no limit). Default 0.")]
        [SerializeField]
        [Min(0)]
        private int maxResponseChars;

        [Tooltip(
            "Max tool-call roundtrips per single request. Each roundtrip = one LLM call + tool execution. " +
            "Prevents infinite tool-calling loops. Default 10.")]
        [SerializeField]
        [Min(1)]
        private int maxToolCallRoundtrips = 10;

        [Tooltip(
            "Max tool call history messages retained in the MEAI message list during a single request's tool-calling loop. " +
            "Prevents unbounded context growth in long multi-tool sessions. 0 = no limit. Default 20.")]
        [SerializeField]
        [Min(0)]
        private int maxToolCallHistoryMessages = 20;

        [Header("Streaming")]
        [Tooltip(
            "Global streaming preference (SSE/LLMUnity). Override per-role via AgentBuilder/policy or CoreAiChatConfig in UI. " +
            "Shown at the top of the CoreAI Settings custom inspector (Essentials). Default: on.")]
        [SerializeField]
        private bool enableStreaming = true;

        [Header("WebGL (player)")]
        [Tooltip(
            "WebGL only: route streaming HTTP through the native fetch + ReadableStream bridge (CoreAiSseFetch.jslib) " +
            "instead of UnityWebRequest. Requires the .jslib plugin and a CORS-permitted backend that does not buffer SSE.")]
        [SerializeField]
        private bool webGlNativeStreaming = true;

        [Tooltip(
            "WebGL only: fetch credentials mode. When true -> credentials: 'same-origin' (cookies on same host). " +
            "When false -> 'omit' (default): Bearer keys still work; required for many APIs that return CORS " +
            "Access-Control-Allow-Origin: * (e.g. OpenRouter). Turn on only if you need same-origin cookie behavior.")]
        [SerializeField]
        private bool sameOriginCredentials = false;

        [Header("Chat history summarization")]
        [Tooltip(
            "When off, the full loaded chat transcript is sent in the MEAI tail without rolling prefix into ## Conversation Summary (may exceed model context).")]
        [SerializeField]
        private bool enableConversationHistorySummarization = true;

        [Tooltip(
            "When true, the conversation summary is prepended as the first tail message before recent verbatim turns instead of placed in the system prefix, so the cached prefix stays stable (roadmap §1a). Later memory deltas/world-state still belong near the end of the tail. Default false = legacy behaviour.")]
        [SerializeField]
        private bool placeLiveContextInTail = CoreAISettings.DefaultPlaceLiveContextInTail;

        [Tooltip(
            "When greater than zero, overrides the orchestrator's computed recent-history token budget (heuristic). Zero keeps automatic budgeting from context window minus system/tools.")]
        [SerializeField]
        [Min(0)]
        private int conversationHistoryRecentTokenBudgetOverride;

        [Tooltip(
            "When greater than zero, truncates the persisted rolling summary to roughly this many estimated tokens after each rollup.")]
        [SerializeField]
        [Min(0)]
        private int conversationRolledSummaryMaxTokens;

        [Tooltip(
            "Roadmap §2: compaction only triggers once estimated history tokens reach this fraction of the history budget. Below it, all turns stay verbatim and the stored summary is left untouched. Values <= 0 or > 1 preserve legacy budget-boundary behavior.")]
        [SerializeField]
        [Range(0f, 1f)]
        private float conversationCompactionTriggerRatio = CoreAISettings.DefaultConversationCompactionTriggerRatio;

        [Tooltip(
            "Roadmap §7: prune stale/superseded prompt-history entries before compaction. Operates only on the in-memory request copy; durable chat history on disk is untouched.")]
        [SerializeField]
        private bool enableContextPruning = CoreAISettings.DefaultEnableContextPruning;

        [Tooltip(
            "Roadmap §7: newest durable tool-result messages (role 'tool', headed ## Tool Results) retained in the prompt history copy before compaction.")]
        [SerializeField]
        [Min(0)]
        private int maxRetainedToolResultMessages = CoreAISettings.DefaultMaxRetainedToolResultMessages;

        [Tooltip(
            "Optional auxiliary LLM to fold evicted transcript (costlier than deterministic rollup; off by default). Still requires per-role UseLlmContextCompaction.")]
        [SerializeField]
        private bool enableLlmContextCompaction = false;

        [Tooltip(
            "When true, the pre-flight token estimate is nudged toward observed real prompt tokens (bounded). The script-aware base estimate always applies.")]
        [SerializeField]
        private bool enableTokenCalibration = CoreAISettings.DefaultEnableTokenCalibration;

        [Header("Offline mode")]
        [Tooltip("Serve a fixed string instead of per-role stubs when Offline mode is active.")]
        [SerializeField]
        private bool offlineUseCustomResponse = false;

        [Tooltip("Replacement assistant text returned for matched offline roles.")] [SerializeField] [TextArea(3, 8)]
        private string offlineCustomResponse = "Offline mode: LLM unavailable";

        [Tooltip("Comma-separated role ids (or * for everyone) that receive OfflineCustomResponse.")] [SerializeField]
        private string offlineCustomResponseRoles = "*";

        [Header("Debug")] [Tooltip("Verbose MEAI diagnostics (requests/responses).")] [SerializeField]
        private bool enableMeaiDebugLogging = false;

        [Tooltip("Dump raw HTTP bodies (noisy; dev only).")] [SerializeField]
        private bool enableHttpDebugLogging = false;

        [Tooltip("Log composed prompts / tool definitions before dispatch.")] [SerializeField]
        private bool logLlmInput = true;

        [Tooltip("Log assistant completions and aggregated tool summaries.")] [SerializeField]
        private bool logLlmOutput = true;

        [Tooltip("Emit usage.prompt / usage.completion totals when backends provide them.")] [SerializeField]
        private bool logTokenUsage = true;

        [Tooltip("Log measured LLM latency in milliseconds.")] [SerializeField]
        private bool logLlmLatency = true;

        [Tooltip("Log transport failures (timeouts, unreachable hosts).")] [SerializeField]
        private bool logLlmConnectionErrors = true;

        [Header("Tool Call Logging")] [Tooltip("Emit a line whenever a native tool executes.")] [SerializeField]
        private bool logToolCalls = true;

        [Tooltip("Serialize tool arguments into logs.")] [SerializeField]
        private bool logToolCallArguments = true;

        [Tooltip("Serialize tool outputs into logs.")] [SerializeField]
        private bool logToolCallResults = true;

        [Tooltip("Trace MEAI function-calling iterations / inner retries.")] [SerializeField]
        private bool logMeaiToolCallingSteps = true;

        [Tooltip(
            "Orchestration-level LLM cancel-after seconds (streaming + heavy tool loops often need 60-180 seconds)..")]
        [SerializeField]
        [Min(0f)]
        private float llmRequestTimeoutSeconds = 120f;

        [Tooltip("Concurrent orchestrator runs allowed by CoreAILifetimeScope.")] [SerializeField] [Min(1)]
        private int maxConcurrentOrchestrations = 2;

        [Tooltip("Emit orchestrator timing / counters to the Unity log.")] [SerializeField]
        private bool logOrchestrationMetrics = false;

        [Header("Token Budget Diagnostics")]
        [Tooltip("USD price per 1K prompt/input tokens for the token-budget overlay. 0 = unset (tokens only).")]
        [SerializeField]
        [Min(0f)]
        private float inputTokenPricePer1KUsd = 0f;

        [Tooltip("USD price per 1K completion/output tokens for the token-budget overlay. 0 = unset (tokens only).")]
        [SerializeField]
        [Min(0f)]
        private float outputTokenPricePer1KUsd = 0f;

        #endregion

        #region Properties

        /// <summary>Serialized legacy backend enum.</summary>
        public LlmBackendType BackendType => backendType;

        /// <summary>Product-facing LLM execution mode.</summary>
        public LlmExecutionMode ExecutionMode => ResolveExecutionMode(executionMode, backendType);

        /// <summary>True when current mode routes through OpenAI-compatible HTTP.</summary>
        public bool UseHttpApi =>
            ExecutionMode == LlmExecutionMode.ClientOwnedApi ||
            ExecutionMode == LlmExecutionMode.ClientLimited ||
            ExecutionMode == LlmExecutionMode.ServerManagedApi ||
            backendType == LlmBackendType.OpenAiHttp;

        /// <summary>True when Auto/LocalModel may bind LLMUnity.</summary>
        public bool UseLlmUnity =>
            ExecutionMode == LlmExecutionMode.LocalModel || ExecutionMode == LlmExecutionMode.Auto;

        /// <summary>True when execution mode is Offline.</summary>
        public bool UseOffline => ExecutionMode == LlmExecutionMode.Offline;

        /// <summary>Whether the current mode uses a user-owned provider key.</summary>
        public bool UseClientOwnedApi => ExecutionMode == LlmExecutionMode.ClientOwnedApi;

        /// <summary>Whether the current mode applies local client-side limits.</summary>
        public bool UseClientLimited => ExecutionMode == LlmExecutionMode.ClientLimited;

        /// <summary>Whether the current mode delegates provider access to a backend service.</summary>
        public bool UseServerManagedApi => ExecutionMode == LlmExecutionMode.ServerManagedApi;

        /// <summary>Auto-mode backend ordering preference.</summary>
        public LlmAutoPriority AutoPriority => autoPriority;

        /// <summary>Whether offline replies use <see cref="OfflineCustomResponse"/>.</summary>
        public bool OfflineUseCustomResponse => offlineUseCustomResponse;

        /// <summary>Fallback/custom assistant line for offline flows.</summary>
        public string OfflineCustomResponse => string.IsNullOrWhiteSpace(offlineCustomResponse)
            ? "Offline mode: LLM unavailable"
            : offlineCustomResponse;

        /// <summary>Role filter list or <c>*</c> wildcard.</summary>
        public string OfflineCustomResponseRoles =>
            string.IsNullOrWhiteSpace(offlineCustomResponseRoles) ? "*" : offlineCustomResponseRoles;

        /// <summary>Returns true when <paramref name="roleId"/> should receive custom offline copy.</summary>
        public bool ShouldUseOfflineCustomResponse(string roleId)
        {
            if (!offlineUseCustomResponse)
            {
                return false;
            }

            if (offlineCustomResponseRoles == "*")
            {
                return true;
            }

            if (string.IsNullOrEmpty(roleId))
            {
                return false;
            }

            string[] roles = offlineCustomResponseRoles.Split(',');
            foreach (string r in roles)
            {
                if (r.Trim().Equals(roleId, System.StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>OpenAI-compatible base URL without trailing slash.</summary>
        public string ApiBaseUrl =>
            string.IsNullOrWhiteSpace(apiBaseUrl) ? "http://localhost:1234/v1" : apiBaseUrl.TrimEnd('/');

        /// <summary>Bearer-style API credential.</summary>
        public string ApiKey => apiKey ?? "";


        /// <summary>Whether fallback to secondary backend is enabled.</summary>
        public bool EnableFallbackBackend => enableFallbackBackend;

        /// <summary>Secondary backend base URL.</summary>
        public string SecondaryApiBaseUrl =>
            string.IsNullOrWhiteSpace(secondaryApiBaseUrl) ? "" : secondaryApiBaseUrl.TrimEnd('/');

        /// <summary>Secondary backend API key.</summary>
        public string SecondaryApiKey => secondaryApiKey ?? "";

        /// <summary>Secondary backend model name.</summary>
        public string SecondaryModelName => secondaryModelName ?? "";

        /// <summary>True when fallback is enabled and secondary URL + model are both configured.</summary>
        public bool HasValidFallbackBackend =>
            enableFallbackBackend &&
            !string.IsNullOrWhiteSpace(secondaryApiBaseUrl) &&
            !string.IsNullOrWhiteSpace(secondaryModelName);

        /// <summary>Provider model identifier (HTTP) or GGUF hint (LLMUnity fallback).</summary>
        public string ModelName
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(modelName))
                {
                    return modelName;
                }

                if (ExecutionMode == LlmExecutionMode.LocalModel || ExecutionMode == LlmExecutionMode.Auto)
                {
                    if (!string.IsNullOrWhiteSpace(ggufModelPath))
                    {
                        return ggufModelPath;
                    }
                }

                return "gpt-4o-mini";
            }
        }

        /// <summary>Serialized temperature (used when <see cref="OverrideTemperature"/> is true).</summary>
        public float Temperature => temperature;

        /// <summary>When true, <see cref="Temperature"/> is sent on LLM requests; when false, backends use default sampling temperature.</summary>
        public bool OverrideTemperature => enableTemperatureOverriding;

        /// <summary>Global max-output cap (tokens).</summary>
        public int MaxTokens => maxTokens;

        /// <summary>Maximum client-limited requests allowed in the current session; zero disables this limit.</summary>
        public int MaxClientLimitedRequestsPerSession =>
            maxClientLimitedRequestsPerSession < 0 ? 0 : maxClientLimitedRequestsPerSession;

        /// <summary>Maximum client-limited prompt characters per request; zero disables this limit.</summary>
        public int MaxClientLimitedPromptChars =>
            maxClientLimitedPromptChars < 0 ? 0 : maxClientLimitedPromptChars;

        /// <summary>HTTP adapter timeout backing field.</summary>
        public int RequestTimeoutSeconds => requestTimeoutSeconds <= 0 ? 120 : requestTimeoutSeconds;

        /// <summary>
        /// Single-request HTTP limit aligned with <see cref="LlmRequestTimeoutSeconds"/> so the transport
        /// does not outlive the orchestrator/chat <c>CancelAfterSlim</c> window (notably WebGL non-streaming).
        /// </summary>
        public int EffectiveHttpRequestTimeoutSeconds
        {
            get
            {
                int http = RequestTimeoutSeconds;
                float llm = LlmRequestTimeoutSeconds;
                if (llm <= 0f)
                {
                    return http;
                }

                int orchestratorCap = System.Math.Max(1, (int)System.Math.Ceiling(llm));
                return System.Math.Min(http, orchestratorCap);
            }
        }

        /// <summary>Optional LLMAgent GameObject identifier.</summary>
        public string LlmUnityAgentName => llmUnityAgentName;

        /// <summary>Spawn LLM+LLMAgent at runtime when none exists in scenes.</summary>
        public bool LlmUnityAutoCreateRuntimeHost => llmUnityAutoCreateRuntimeHost;

        /// <summary>Custom name for auto-created host GameObject.</summary>
        public string LlmUnityRuntimeHostObjectName => llmUnityRuntimeHostObjectName ?? "";

        /// <summary>Warm up local GGUF server shortly after game start.</summary>
        public bool LlmUnityAutostartLocalServer => llmUnityAutostartLocalServer;

        /// <summary>Relative GGUF location for LLMUnity.</summary>
        public string GgufModelPath => ggufModelPath ?? "";

        /// <summary>Persist LLMUnity host GameObject.</summary>
        public bool LlmUnityDontDestroyOnLoad => llmUnityDontDestroyOnLoad;

        /// <summary>Local model readiness timeout.</summary>
        public float LlmUnityStartupTimeoutSeconds =>
            llmUnityStartupTimeoutSeconds < 5f ? 120f : llmUnityStartupTimeoutSeconds;

        /// <summary>Cooldown after daemon ready signal.</summary>
        public float LlmUnityStartupDelaySeconds => llmUnityStartupDelaySeconds;

        /// <summary>Hold LLMUnity server between prompts.</summary>
        public bool LlmUnityKeepAlive => llmUnityKeepAlive;

        /// <summary>Provider-specific HTTP reasoning mode.</summary>
        public LlmReasoningMode ReasoningMode => reasoningMode;

        /// <summary>Optional provider-side thinking budget in tokens. 0 = omit.</summary>
        public int ThinkingBudgetTokens => thinkingBudgetTokens < 0 ? 0 : thinkingBudgetTokens;

        /// <summary>Optional provider-specific HTTP request body JSON.</summary>
        public string ExtraBodyJson => extraBodyJson ?? "";

        /// <summary>LLMUnity session concurrency clamp.</summary>
        public int LlmUnityMaxConcurrentChats => llmUnityMaxConcurrentChats < 1 ? 1 : llmUnityMaxConcurrentChats;

        /// <summary>Exported GPU offload depth.</summary>
        public int NumGPULayers => llmUnityNumGPULayers < 0 ? 0 : llmUnityNumGPULayers;

        /// <summary>Universal system preamble.</summary>
        public string UniversalSystemPromptPrefix => universalSystemPromptPrefix ?? "";

        /// <inheritdoc cref="ICoreAISettings.ToolContractAdditionalInstructions"/>
        public string ToolContractAdditionalInstructions => toolContractAdditionalInstructions ?? "";

        /// <summary>Clamp for Programmer Lua retries.</summary>
        public int MaxLuaRepairRetries => maxLuaRepairRetries < 1 ? 3 : maxLuaRepairRetries;

        /// <summary>Clamp for consecutive failing tool executions.</summary>
        public int MaxToolCallRetries => maxToolCallRetries < 1 ? 3 : maxToolCallRetries;

        /// <summary>Duplicate invocation guardrail.</summary>
        public bool AllowDuplicateToolCalls => allowDuplicateToolCalls;

        /// <summary>Clamp for decorator-level HTTP retries.</summary>
        public int MaxLlmRequestRetries => maxLlmRequestRetries < 1 ? 1 : maxLlmRequestRetries;

        /// <inheritdoc cref="ICoreAISettings.MaxContextOverflowRetries"/>
        public int MaxContextOverflowRetries => maxContextOverflowRetries < 0 ? 0 : maxContextOverflowRetries;

        /// <summary>Estimated context-window tokens exposed to budgeting.</summary>
        public int ContextWindowTokens => contextWindowTokens < 256
            ? CoreAISettings.DefaultContextWindowTokens
            : contextWindowTokens;

        /// <summary>Global streaming flag.</summary>
        public bool EnableStreaming => enableStreaming;

        /// <summary>WebGL-only: opt in to the native fetch SSE bridge instead of UnityWebRequest.</summary>
        public bool WebGlNativeStreaming => webGlNativeStreaming;

        /// <summary>WebGL-only: send cookies on cross-origin requests (fetch credentials='include').</summary>
        public bool SameOriginCredentials => sameOriginCredentials;

        /// <summary>Optional LLM-assisted memory compaction flag.</summary>
        public bool EnableLlmContextCompaction => enableLlmContextCompaction;

        /// <inheritdoc cref="ICoreAISettings.EnableTokenCalibration"/>
        public bool EnableTokenCalibration => enableTokenCalibration;

        /// <summary>When false, skip rolling history partition into summary + recent tail.</summary>
        public bool EnableConversationHistorySummarization => enableConversationHistorySummarization;

        /// <inheritdoc cref="ICoreAISettings.PlaceLiveContextInTail"/>
        public bool PlaceLiveContextInTail => placeLiveContextInTail;

        /// <summary>Zero = use automatic history budget; positive = override recent tail token budget.</summary>
        public int ConversationHistoryRecentTokenBudgetOverride =>
            conversationHistoryRecentTokenBudgetOverride < 0 ? 0 : conversationHistoryRecentTokenBudgetOverride;

        /// <summary>Zero = do not truncate rolled summary; positive = cap estimated tokens.</summary>
        public int ConversationRolledSummaryMaxTokens =>
            conversationRolledSummaryMaxTokens < 0 ? 0 : conversationRolledSummaryMaxTokens;

        /// <inheritdoc cref="ICoreAISettings.ConversationCompactionTriggerRatio"/>
        public float ConversationCompactionTriggerRatio => conversationCompactionTriggerRatio;

        /// <inheritdoc cref="ICoreAISettings.EnableContextPruning"/>
        public bool EnableContextPruning => enableContextPruning;

        /// <inheritdoc cref="ICoreAISettings.MaxRetainedToolResultMessages"/>
        public int MaxRetainedToolResultMessages =>
            maxRetainedToolResultMessages < 0 ? 0 : maxRetainedToolResultMessages;

        /// <summary>Max chars per tool result before truncation. 0 = no truncation.</summary>
        public int MaxToolResultChars => maxToolResultChars < 0 ? 0 : maxToolResultChars;

        /// <summary>Per-tool execution timeout (ms). 0 = no per-tool timeout.</summary>
        public int DefaultToolTimeoutMs => defaultToolTimeoutMs < 0 ? 0 : defaultToolTimeoutMs;

        /// <summary>Max response chars from the model. 0 = disabled.</summary>
        public int MaxResponseChars => maxResponseChars < 0 ? 0 : maxResponseChars;

        /// <summary>Max tool-call roundtrips per request.</summary>
        public int MaxToolCallRoundtrips => maxToolCallRoundtrips < 1 ? 10 : maxToolCallRoundtrips;

        /// <summary>Max tool call history messages in the MEAI message list. 0 = no limit.</summary>
        public int MaxToolCallHistoryMessages => maxToolCallHistoryMessages < 0 ? 20 : maxToolCallHistoryMessages;

        /// <inheritdoc cref="ICoreAISettings.ToolInvocationMarshaler"/>
        public ILlmAsyncMarshaler ToolInvocationMarshaler => UnityMainThreadLlmAsyncMarshaler.Instance;

        /// <inheritdoc />
        public bool EnableMeaiDebugLogging => enableMeaiDebugLogging;

        /// <inheritdoc />
        public bool EnableHttpDebugLogging => enableHttpDebugLogging;

        /// <inheritdoc />
        public float LlmRequestTimeoutSeconds => llmRequestTimeoutSeconds;

        /// <summary>Parallel orchestrator task ceiling.</summary>
        public int MaxConcurrentOrchestrations => maxConcurrentOrchestrations < 1 ? 2 : maxConcurrentOrchestrations;

        /// <summary>Orchestrator metrics logging toggle.</summary>
        public bool LogOrchestrationMetrics => logOrchestrationMetrics;

        /// <summary>Log inbound prompts before dispatch.</summary>
        public bool LogLlmInput => logLlmInput;

        /// <summary>Log assistant completions / tool summaries.</summary>
        public bool LogLlmOutput => logLlmOutput;

        /// <inheritdoc />
        public bool LogTokenUsage => logTokenUsage;

        /// <inheritdoc />
        public bool LogLlmLatency => logLlmLatency;

        /// <inheritdoc />
        public bool LogLlmConnectionErrors => logLlmConnectionErrors;

        /// <inheritdoc />
        public bool LogToolCalls => logToolCalls;

        /// <inheritdoc />
        public bool LogToolCallArguments => logToolCallArguments;

        /// <inheritdoc />
        public bool LogToolCallResults => logToolCallResults;

        /// <inheritdoc />
        public bool LogMeaiToolCallingSteps => logMeaiToolCallingSteps;

        /// <inheritdoc cref="ICoreAISettings.InputTokenPricePer1KUsd"/>
        public float InputTokenPricePer1KUsd => inputTokenPricePer1KUsd < 0f ? 0f : inputTokenPricePer1KUsd;

        /// <inheritdoc cref="ICoreAISettings.OutputTokenPricePer1KUsd"/>
        public float OutputTokenPricePer1KUsd => outputTokenPricePer1KUsd < 0f ? 0f : outputTokenPricePer1KUsd;

        #endregion

        #region Runtime Options

        /// <summary>
        /// Builds a Unity-free settings snapshot for runtime consumers and tests.
        /// </summary>
        public CoreAISettingsOptions ToOptions()
        {
            return CoreAISettingsOptions.From(this);
        }

        /// <summary>
        /// Copies portable settings values into this Unity authoring asset.
        /// Unity-only backend discovery and resource lifecycle fields are intentionally unchanged.
        /// </summary>
        public void ApplyOptions(ICoreAISettings options)
        {
            if (options == null)
            {
                return;
            }

            maxLuaRepairRetries = options.MaxLuaRepairRetries < 1 ? 3 : options.MaxLuaRepairRetries;
            enableMeaiDebugLogging = options.EnableMeaiDebugLogging;
            llmRequestTimeoutSeconds = options.LlmRequestTimeoutSeconds < 0f ? 120f : options.LlmRequestTimeoutSeconds;
            maxLlmRequestRetries = options.MaxLlmRequestRetries < 1 ? 1 : options.MaxLlmRequestRetries;
            maxContextOverflowRetries =
                options.MaxContextOverflowRetries < 0 ? 0 : options.MaxContextOverflowRetries;
            enableHttpDebugLogging = options.EnableHttpDebugLogging;
            logTokenUsage = options.LogTokenUsage;
            logLlmLatency = options.LogLlmLatency;
            logLlmConnectionErrors = options.LogLlmConnectionErrors;
            contextWindowTokens = options.ContextWindowTokens < 256
                ? CoreAISettings.DefaultContextWindowTokens
                : options.ContextWindowTokens;
            universalSystemPromptPrefix = options.UniversalSystemPromptPrefix ?? "";
            toolContractAdditionalInstructions = options.ToolContractAdditionalInstructions ?? "";
            temperature = Mathf.Clamp(options.Temperature, 0f, 2f);
            enableTemperatureOverriding = options.OverrideTemperature;
            maxToolCallRetries = options.MaxToolCallRetries < 1 ? 3 : options.MaxToolCallRetries;
            logToolCalls = options.LogToolCalls;
            logToolCallArguments = options.LogToolCallArguments;
            logToolCallResults = options.LogToolCallResults;
            logMeaiToolCallingSteps = options.LogMeaiToolCallingSteps;
            allowDuplicateToolCalls = options.AllowDuplicateToolCalls;
            enableStreaming = options.EnableStreaming;
            maxTokens = options.MaxTokens <= 0 ? 2048 : options.MaxTokens;
            enableLlmContextCompaction = options.EnableLlmContextCompaction;
            enableTokenCalibration = options.EnableTokenCalibration;
            enableConversationHistorySummarization = options.EnableConversationHistorySummarization;
            placeLiveContextInTail = options.PlaceLiveContextInTail;
            conversationHistoryRecentTokenBudgetOverride =
                options.ConversationHistoryRecentTokenBudgetOverride < 0
                    ? 0
                    : options.ConversationHistoryRecentTokenBudgetOverride;
            conversationRolledSummaryMaxTokens =
                options.ConversationRolledSummaryMaxTokens < 0 ? 0 : options.ConversationRolledSummaryMaxTokens;
            conversationCompactionTriggerRatio =
                options.ConversationCompactionTriggerRatio > 1f ? 1f : options.ConversationCompactionTriggerRatio;
            enableContextPruning = options.EnableContextPruning;
            maxRetainedToolResultMessages =
                options.MaxRetainedToolResultMessages < 0 ? 0 : options.MaxRetainedToolResultMessages;
            maxToolResultChars = options.MaxToolResultChars < 0 ? 0 : options.MaxToolResultChars;
            defaultToolTimeoutMs = options.DefaultToolTimeoutMs < 0 ? 0 : options.DefaultToolTimeoutMs;
            maxResponseChars = options.MaxResponseChars < 0 ? 0 : options.MaxResponseChars;
            maxToolCallRoundtrips = options.MaxToolCallRoundtrips < 1 ? 10 : options.MaxToolCallRoundtrips;
            maxToolCallHistoryMessages =
                options.MaxToolCallHistoryMessages < 0 ? 20 : options.MaxToolCallHistoryMessages;
        }

        #endregion

        #region Runtime Configuration

        /// <summary>Programmatic HTTP preset (tests / runtime bootstrap).</summary>
        public void ConfigureHttpApi(
            string baseUrl,
            string key,
            string model,
            float temperature = 0.2f,
            int timeoutSeconds = 120,
            int maxTokens = 2048)
        {
            backendType = LlmBackendType.OpenAiHttp;
            executionMode = LlmExecutionMode.ClientOwnedApi;
            apiBaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? "http://localhost:1234/v1" : baseUrl;
            apiKey = key ?? "";
            modelName = string.IsNullOrWhiteSpace(model) ? "gpt-4o-mini" : model;
            this.temperature = Mathf.Clamp(temperature, 0f, 2f);
            enableTemperatureOverriding = true;
            requestTimeoutSeconds = timeoutSeconds <= 0 ? 120 : timeoutSeconds;
            this.maxTokens = maxTokens <= 0 ? 2048 : maxTokens;
        }

        /// <summary>
        /// Switches to a user-owned OpenAI-compatible HTTP API.
        /// </summary>
        public void ConfigureClientOwnedApi(
            string baseUrl,
            string key,
            string model,
            float temperature = 0.2f,
            int timeoutSeconds = 120,
            int maxTokens = 2048)
        {
            ConfigureHttpApi(baseUrl, key, model, temperature, timeoutSeconds, maxTokens);
            executionMode = LlmExecutionMode.ClientOwnedApi;
        }

        /// <summary>
        /// Switches to an OpenAI-compatible HTTP API with local client-side limits.
        /// </summary>
        public void ConfigureClientLimited(
            string baseUrl,
            string key,
            string model,
            int maxRequestsPerSession,
            int maxPromptChars,
            float temperature = 0.2f,
            int timeoutSeconds = 120,
            int maxTokens = 2048)
        {
            ConfigureHttpApi(baseUrl, key, model, temperature, timeoutSeconds, maxTokens);
            executionMode = LlmExecutionMode.ClientLimited;
            maxClientLimitedRequestsPerSession = maxRequestsPerSession < 0 ? 0 : maxRequestsPerSession;
            maxClientLimitedPromptChars = maxPromptChars < 0 ? 0 : maxPromptChars;
        }

        /// <summary>
        /// Switches to a backend-managed OpenAI-compatible proxy without requiring a provider key in the client.
        /// </summary>
        public void ConfigureServerManagedApi(
            string backendBaseUrl,
            string model,
            string backendAuthToken = "",
            float temperature = 0.2f,
            int timeoutSeconds = 120,
            int maxTokens = 2048)
        {
            ConfigureHttpApi(backendBaseUrl, backendAuthToken, model, temperature, timeoutSeconds, maxTokens);
            executionMode = LlmExecutionMode.ServerManagedApi;
        }

        /// <summary>Force local GGUF routing via LLMUnity.</summary>
        public void ConfigureLlmUnity(
            string agentName = "",
            string ggufPath = "Qwen3.5-2B-Q4_K_M.gguf",
            bool keepAlive = false,
            float startupTimeout = 120f,
            float startupDelay = 1f,
            bool dontDestroyOnLoad = true,
            int numGpuLayers = 99)
        {
            backendType = LlmBackendType.LlmUnity;
            executionMode = LlmExecutionMode.LocalModel;
            llmUnityAgentName = agentName ?? "";
            ggufModelPath = string.IsNullOrWhiteSpace(ggufPath) ? "Qwen3.5-2B-Q4_K_M.gguf" : ggufPath;
            llmUnityKeepAlive = keepAlive;
            llmUnityStartupTimeoutSeconds = startupTimeout < 5f ? 120f : startupTimeout;
            llmUnityStartupDelaySeconds = startupDelay;
            llmUnityDontDestroyOnLoad = dontDestroyOnLoad;
            llmUnityNumGPULayers = numGpuLayers < 0 ? 0 : numGpuLayers;
        }

        /// <summary>Disable networked LLMs (offline/stub).</summary>
        public void ConfigureOffline()
        {
            ConfigureOffline(false);
        }

        /// <summary>Disable networked LLMs and optionally serve a fixed custom response for matched roles.</summary>
        public void ConfigureOffline(bool useCustomResponse, string customResponse = null, string roles = null)
        {
            backendType = LlmBackendType.Offline;
            executionMode = LlmExecutionMode.Offline;
            offlineUseCustomResponse = useCustomResponse;
            if (customResponse != null)
            {
                offlineCustomResponse = customResponse;
            }

            if (roles != null)
            {
                offlineCustomResponseRoles = roles;
            }
        }

        /// <summary>Enable/disable the secondary fallback backend and set its URL/model/key.</summary>
        public void ConfigureFallbackBackend(
            bool enabled,
            string secondaryBaseUrl,
            string secondaryModel,
            string secondaryKey = "")
        {
            enableFallbackBackend = enabled;
            secondaryApiBaseUrl = secondaryBaseUrl ?? "";
            secondaryModelName = secondaryModel ?? "";
            secondaryApiKey = secondaryKey ?? "";
        }

        /// <summary>Sets the orchestration-level LLM cancel-after window (seconds).</summary>
        public void SetOrchestratorTimeoutSeconds(float seconds)
        {
            llmRequestTimeoutSeconds = seconds;
        }

        /// <summary>Sets the OpenAI-compatible base URL without normalization (callers may pass raw values).</summary>
        public void SetApiBaseUrl(string baseUrl)
        {
            apiBaseUrl = baseUrl;
        }

        /// <summary>Sets execution mode, legacy backend, and model identifiers for backend/model resolution.</summary>
        public void SetModelResolution(
            LlmExecutionMode mode,
            LlmBackendType backend,
            string model,
            string ggufPath)
        {
            executionMode = mode;
            backendType = backend;
            modelName = model;
            ggufModelPath = ggufPath;
        }

        /// <summary>Use automatic backend resolution (respects routing manifest + priority).</summary>
        public void ConfigureAuto()
        {
            backendType = LlmBackendType.Auto;
            executionMode = LlmExecutionMode.Auto;
        }

        /// <summary>
        /// Maps legacy backend settings to the public execution mode surface.
        /// </summary>
        public static LlmExecutionMode ResolveExecutionMode(LlmExecutionMode mode, LlmBackendType legacyBackend)
        {
            if (mode != LlmExecutionMode.Auto)
            {
                return mode;
            }

            switch (legacyBackend)
            {
                case LlmBackendType.LlmUnity:
                    return LlmExecutionMode.LocalModel;
                case LlmBackendType.OpenAiHttp:
                    return LlmExecutionMode.ClientOwnedApi;
                case LlmBackendType.Offline:
                    return LlmExecutionMode.Offline;
                default:
                    return LlmExecutionMode.Auto;
            }
        }

        #endregion

        #region Unity Editor Helpers

#if UNITY_EDITOR
        /// <summary>
        /// Restores editor defaults for newly created settings assets.
        /// Ensures global streaming and the WebGL fetch bridge default to <b>on</b>.
        /// </summary>
        private void Reset()
        {
            enableStreaming = true;
            webGlNativeStreaming = true;
        }

        private void OnValidate()
        {
            if (requestTimeoutSeconds < 0)
            {
                requestTimeoutSeconds = 120;
            }

            if (maxLuaRepairRetries < 1)
            {
                maxLuaRepairRetries = 3;
            }

            if (maxToolCallRetries < 1)
            {
                maxToolCallRetries = 3;
            }

            if (maxLlmRequestRetries < 1)
            {
                maxLlmRequestRetries = 1;
            }

            if (contextWindowTokens < 256)
            {
                contextWindowTokens = CoreAISettings.DefaultContextWindowTokens;
            }

            if (maxConcurrentOrchestrations < 1)
            {
                maxConcurrentOrchestrations = 2;
            }

            if (maxClientLimitedRequestsPerSession < 0)
            {
                maxClientLimitedRequestsPerSession = 0;
            }

            if (maxClientLimitedPromptChars < 0)
            {
                maxClientLimitedPromptChars = 0;
            }

            if (conversationHistoryRecentTokenBudgetOverride < 0)
            {
                conversationHistoryRecentTokenBudgetOverride = 0;
            }

            if (conversationRolledSummaryMaxTokens < 0)
            {
                conversationRolledSummaryMaxTokens = 0;
            }

            if (conversationCompactionTriggerRatio > 1f)
            {
                conversationCompactionTriggerRatio = 1f;
            }

            if (maxRetainedToolResultMessages < 0)
            {
                maxRetainedToolResultMessages = CoreAISettings.DefaultMaxRetainedToolResultMessages;
            }

            if (llmRequestTimeoutSeconds < 0f)
            {
                llmRequestTimeoutSeconds = 120f;
            }

            if (llmUnityStartupTimeoutSeconds < 5f)
            {
                llmUnityStartupTimeoutSeconds = 120f;
            }

            if (llmUnityStartupDelaySeconds < 0f)
            {
                llmUnityStartupDelaySeconds = 1f;
            }

            if (llmUnityMaxConcurrentChats < 1)
            {
                llmUnityMaxConcurrentChats = 1;
            }

            if (llmUnityNumGPULayers < 0)
            {
                llmUnityNumGPULayers = 0;
            }

            if (thinkingBudgetTokens < 0)
            {
                thinkingBudgetTokens = 0;
            }

            if (maxToolResultChars < 0)
            {
                maxToolResultChars = 0;
            }

            if (defaultToolTimeoutMs < 0)
            {
                defaultToolTimeoutMs = 0;
            }

            if (maxResponseChars < 0)
            {
                maxResponseChars = 0;
            }

            if (maxToolCallRoundtrips < 1)
            {
                maxToolCallRoundtrips = 10;
            }

            if (maxToolCallHistoryMessages < 0)
            {
                maxToolCallHistoryMessages = 20;
            }
        }
#endif

        #endregion
    }
}
