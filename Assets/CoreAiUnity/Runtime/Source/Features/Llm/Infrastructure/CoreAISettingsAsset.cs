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

        /// <summary>OpenAI-compatible HTTP (LM Studio, OpenRouter, Qwen, …).</summary>
        OpenAiHttp = 2,

        /// <summary>No network LLM — deterministic stubs for CI/offline flows.</summary>
        Offline = 3
    }

    /// <summary>Preference order inside <see cref="LlmBackendType.Auto"/>.</summary>
    public enum LlmAutoPriority
    {
        /// <summary>LLMUnity → HTTP → Offline.</summary>
        LlmUnityFirst = 0,

        /// <summary>HTTP → LLMUnity → Offline.</summary>
        HttpFirst = 1
    }

    /// <summary>
    /// Central CoreAI tuning asset (<c>Create → CoreAI → Core AI Settings</c>).
    /// Loaded lazily via <see cref="Instance"/> unless <see cref="SetInstance"/> injects another reference.
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

        [Header("🤖 LLM Backend")]
        [Tooltip("Runtime backend preset: Auto, LLMUnity, HTTP API, or Offline.")]
        [SerializeField]
        private LlmBackendType backendType = LlmBackendType.Auto;

        [Tooltip("Product-facing LLM mode. Auto preserves legacy backend selection.")]
        [SerializeField]
        private LlmExecutionMode executionMode = LlmExecutionMode.Auto;

        [Tooltip("When backend is Auto, prefer LLMUnity or HTTP API first.")]
        [SerializeField]
        private LlmAutoPriority autoPriority = LlmAutoPriority.LlmUnityFirst;

        [Header("🌐 HTTP API (OpenAI-compatible)")]
        [Tooltip(
            "Base URL without trailing slash (e.g., https://api.openai.com/v1 or http://localhost:1234/v1 for LM Studio).")]
        [SerializeField]
        private string apiBaseUrl = "http://localhost:1234/v1";

        [Tooltip("Bearer/API key — required for hosted providers; empty for many local gateways.")]
        [SerializeField]
        private string apiKey = "";

        [Tooltip("Model identifier passed to OpenAI-compatible servers (gpt-4o-mini, qwen3.5-4b, …).")]
        [SerializeField]
        private string modelName = "gpt-4o-mini";

        [Tooltip(
            "When enabled, the Temperature value below is sent to OpenAI-compatible HTTP APIs and LLMUnity (MEAI). " +
            "When disabled, sampling temperature is omitted so each backend uses its own default.")]
        [SerializeField]
        [FormerlySerializedAs("overrideTemperature")]
        private bool enableTemperatureOverriding;

        [Tooltip("Sampling temperature (0.0 = deterministic … 2.0 = creative). Used only when temperature override is on.")]
        [SerializeField]
        [Range(0f, 2f)]
        private float temperature = 0.1f;

        [Tooltip("Max tokens per completion across HTTP + LLMUnity when no per-call override. 0 = provider default.")]
        [SerializeField]
        private int maxTokens = 2048;

        [Header("Client limits")]
        [SerializeField] [Min(0)] private int maxClientLimitedRequestsPerSession;

        [SerializeField] [Min(0)] private int maxClientLimitedPromptChars;

        [Tooltip("HTTP layer timeout seconds (fallback 120 when unset).")] [SerializeField] [Min(0)]
        private int requestTimeoutSeconds = 120;

        [Header("💾 LLMUnity (local model)")]
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

        [Tooltip("Relative GGUF path; falls back to LLMUnity model manager hints when empty.")]
        [SerializeField]
        private string ggufModelPath = "Qwen3.5-2B-Q4_K_M.gguf";

        [Tooltip("Keep LLMAgent alive across loads (persist service).")] [SerializeField]
        private bool llmUnityDontDestroyOnLoad = true;

        [Tooltip("Seconds to wait for LLMUnity service startup.")]
        [SerializeField] [Min(5f)]
        private float llmUnityStartupTimeoutSeconds = 120f;

        [Tooltip("Delay after local model server reports ready.")]
        [SerializeField] [Min(0f)]
        private float llmUnityStartupDelaySeconds = 1f;

        [Tooltip("Keep LLMUnity server warm between turns (faster tests).")] [SerializeField]
        private bool llmUnityKeepAlive = false;

        [Tooltip(
            "Strip/enable reasoning tags for models that emit <think> blocks (Qwen3.5, DeepSeek, …). Works for HTTP + LLMUnity.")]
        [SerializeField]
        private bool enableReasoning = false;

        [Tooltip("Concurrent LLMUnity chat sessions (1 = strictly serial).")] [SerializeField] [Min(1)]
        private int llmUnityMaxConcurrentChats = 1;

        [Tooltip("GPU offload depth (0 = CPU only, 99 = all layers — LM Studio style).")]
        [SerializeField]
        [Min(0)]
        private int llmUnityNumGPULayers = 99;

        [Header("⚙️ Shared agent defaults")]
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

        [Tooltip("Transport-level retries after HTTP 429 / 5xx (and equivalent failed completions). Each retry waits Retry-After or 2s, 4s, … (capped).")]
        [SerializeField]
        [Min(1)]
        private int maxLlmRequestRetries = 1;

        [Tooltip("Default context-window hint in tokens.")]
        [SerializeField] [Min(256)]
        private int contextWindowTokens = 8192;

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
            "WebGL only: fetch credentials mode. When true → credentials: 'same-origin' (cookies on same host). " +
            "When false → 'omit' (default): Bearer keys still work; required for many APIs that return CORS " +
            "Access-Control-Allow-Origin: * (e.g. OpenRouter). Turn on only if you need same-origin cookie behavior.")]
        [SerializeField]
        private bool sameOriginCredentials = false;

        [Header("Chat history summarization")]
        [Tooltip(
            "When off, the full loaded chat transcript is sent in the MEAI tail without rolling prefix into ## Conversation Summary (may exceed model context).")]
        [SerializeField]
        private bool enableConversationHistorySummarization = true;

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
            "Optional auxiliary LLM to fold evicted transcript (costlier than deterministic rollup; off by default). Still requires per-role UseLlmContextCompaction.")]
        [SerializeField]
        private bool enableLlmContextCompaction = false;

        [Header("🔌 Offline mode")]
        [Tooltip("Serve a fixed string instead of per-role stubs when Offline mode is active.")]
        [SerializeField]
        private bool offlineUseCustomResponse = false;

        [Tooltip("Replacement assistant text returned for matched offline roles.")]
        [SerializeField] [TextArea(3, 8)]
        private string offlineCustomResponse = "Offline mode: LLM unavailable";

        [Tooltip("Comma-separated role ids (or * for everyone) that receive OfflineCustomResponse.")]
        [SerializeField]
        private string offlineCustomResponseRoles = "*";

        [Header("🔧 Debug")]
        [Tooltip("Verbose MEAI diagnostics (requests/responses).")]
        [SerializeField]
        private bool enableMeaiDebugLogging = false;

        [Tooltip("Dump raw HTTP bodies (noisy — dev only).")] [SerializeField]
        private bool enableHttpDebugLogging = false;

        [Tooltip("Log composed prompts / tool definitions before dispatch.")]
        [SerializeField]
        private bool logLlmInput = true;

        [Tooltip("Log assistant completions and aggregated tool summaries.")]
        [SerializeField]
        private bool logLlmOutput = true;

        [Tooltip("Emit usage.prompt / usage.completion totals when backends provide them.")]
        [SerializeField]
        private bool logTokenUsage = true;

        [Tooltip("Log measured LLM latency in milliseconds.")]
        [SerializeField]
        private bool logLlmLatency = true;

        [Tooltip("Log transport failures (timeouts, unreachable hosts).")] [SerializeField]
        private bool logLlmConnectionErrors = true;

        [Header("🔨 Tool Call Logging")]
        [Tooltip("Emit a line whenever a native tool executes.")]
        [SerializeField]
        private bool logToolCalls = true;

        [Tooltip("Serialize tool arguments into logs.")]
        [SerializeField]
        private bool logToolCallArguments = true;

        [Tooltip("Serialize tool outputs into logs.")]
        [SerializeField]
        private bool logToolCallResults = true;

        [Tooltip("Trace MEAI function-calling iterations / inner retries.")]
        [SerializeField]
        private bool logMeaiToolCallingSteps = true;

        [Tooltip("Orchestration-level LLM cancel-after seconds (streaming + heavy tool loops often need ≥60–180).")]
        [SerializeField] [Min(0f)]
        private float llmRequestTimeoutSeconds = 120f;

        [Tooltip("Concurrent orchestrator runs allowed by CoreAILifetimeScope.")] [SerializeField] [Min(1)]
        private int maxConcurrentOrchestrations = 2;

        [Tooltip("Emit orchestrator timing / counters to the Unity log.")]
        [SerializeField]
        private bool logOrchestrationMetrics = false;

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
        public bool UseLlmUnity => ExecutionMode == LlmExecutionMode.LocalModel || ExecutionMode == LlmExecutionMode.Auto;

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

        /// <summary>Reasoning-tag cleanup toggle.</summary>
        public bool EnableReasoning => enableReasoning;

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

        /// <summary>Estimated context-window tokens exposed to budgeting.</summary>
        public int ContextWindowTokens => contextWindowTokens < 256 ? 8192 : contextWindowTokens;

        /// <summary>Global streaming flag.</summary>
        public bool EnableStreaming => enableStreaming;

        /// <summary>WebGL-only: opt in to the native fetch SSE bridge instead of UnityWebRequest.</summary>
        public bool WebGlNativeStreaming => webGlNativeStreaming;

        /// <summary>WebGL-only: send cookies on cross-origin requests (fetch credentials='include').</summary>
        public bool SameOriginCredentials => sameOriginCredentials;

        /// <summary>Optional LLM-assisted memory compaction flag.</summary>
        public bool EnableLlmContextCompaction => enableLlmContextCompaction;

        /// <summary>When false, skip rolling history partition into summary + recent tail.</summary>
        public bool EnableConversationHistorySummarization => enableConversationHistorySummarization;

        /// <summary>Zero = use automatic history budget; positive = override recent tail token budget.</summary>
        public int ConversationHistoryRecentTokenBudgetOverride =>
            conversationHistoryRecentTokenBudgetOverride < 0 ? 0 : conversationHistoryRecentTokenBudgetOverride;

        /// <summary>Zero = do not truncate rolled summary; positive = cap estimated tokens.</summary>
        public int ConversationRolledSummaryMaxTokens =>
            conversationRolledSummaryMaxTokens < 0 ? 0 : conversationRolledSummaryMaxTokens;

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
            backendType = LlmBackendType.Offline;
            executionMode = LlmExecutionMode.Offline;
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
        /// Unity calls this after <i>Create → Core AI Settings</i> or when the user chooses <i>Reset</i> on the asset.
        /// Ensures global streaming and the WebGL fetch bridge default to <b>on</b> (matches <see cref="ICoreAISettings"/> contract).
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
                contextWindowTokens = 8192;
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
        }
#endif

        #endregion
    }
}