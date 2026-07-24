namespace CoreAI
{
    /// <summary>
    /// Static proxy for process-wide CoreAI runtime settings and test-time in-memory overrides.
    /// </summary>
    public static class CoreAISettings
    {
        // WHY: This lock guards ONLY Instance get/set and serializes concurrent ResetOverrides calls
        // against each other. The override backing fields below are read and written without it, so a
        // reader racing ResetOverrides can still observe a partially cleared override set; callers that
        // need a stable snapshot must not mutate overrides concurrently with reads.
        private static readonly object _lock = new();

        /// <summary>
        /// Global settings instance for CoreAI. /// /// ///.</summary>
        public static ICoreAISettings Instance
        {
            get
            {
                lock (_lock)
                {
                    return _instance;
                }
            }
            set
            {
                lock (_lock)
                {
                    _instance = value;
                }
            }
        }

        private static ICoreAISettings _instance;

        #region Override storage (nullable; null means "use Instance")

        private static int? _maxLuaRepairRetries;
        private static bool? _enableMeaiDebugLogging;
        private static int? _llmRequestTimeoutSeconds;
        private static int? _maxLlmRequestRetries;
        private static int? _maxContextOverflowRetries;
        private static bool? _enableHttpDebugLogging;
        private static bool? _logTokenUsage;
        private static bool? _logLlmLatency;
        private static bool? _logLlmConnectionErrors;
        private static int? _contextWindowTokens;
        private static string _universalSystemPromptPrefix;
        private static bool _universalSystemPromptPrefixSet;
        private static float? _temperature;
        private static bool? _overrideTemperature;
        private static int? _maxToolCallRetries;
        private static bool? _logToolCalls;
        private static bool? _logToolCallArguments;
        private static bool? _logToolCallResults;
        private static bool? _logMeaiToolCallingSteps;
        private static bool? _allowDuplicateToolCalls;
        private static bool? _enableStreaming;
        private static bool? _enableLuaOnWebGl;
        private static bool? _enableLlmContextCompaction;
        private static bool? _enableTokenCalibration;
        private static float? _conversationCompactionTriggerRatio;
        private static bool? _enableContextPruning;
        private static int? _maxRetainedToolResultMessages;
        private static int? _maxToolResultChars;
        private static int? _defaultToolTimeoutMs;
        private static int? _maxResponseChars;
        private static int? _maxToolCallRoundtrips;
        private static int? _maxToolCallHistoryMessages;
        private static int? _maxParallelToolCalls;

        #endregion

        #region Defaults

        private const int DefaultMaxLuaRepairRetries = 3;
        private const bool DefaultEnableMeaiDebugLogging = false;
        private const int DefaultLlmRequestTimeoutSeconds = 300;
        private const int DefaultMaxLlmRequestRetries = 1;
        private const int DefaultMaxContextOverflowRetries = 3;
        private const bool DefaultEnableHttpDebugLogging = false;
        private const bool DefaultLogTokenUsage = true;
        private const bool DefaultLogLlmLatency = true;
        private const bool DefaultLogLlmConnectionErrors = true;

        /// <summary>Default model context window in tokens (128K = 131072).</summary>
        public const int DefaultContextWindowTokens = 131072;

        /// <summary>
        /// Effectively-unlimited context window sentinel (16M tokens) used when no explicit window
        /// override is configured: client-side history budgeting/compaction never binds and the
        /// provider enforces its own real limit. Large enough for any model, small enough that
        /// downstream token/char arithmetic (e.g. tokens*4 chars) stays far from int overflow.
        /// </summary>
        public const int UnlimitedContextWindowTokens = 16_777_216;

        private const float DefaultTemperature = 0.1f;
        private const bool DefaultOverrideTemperature = false;
        private const int DefaultMaxToolCallRetries = 3;
        private const bool DefaultLogToolCalls = true;
        private const bool DefaultLogToolCallArguments = true;
        private const bool DefaultLogToolCallResults = true;
        private const bool DefaultLogMeaiToolCallingSteps = true;
        private const bool DefaultAllowDuplicateToolCalls = false;
        private const bool DefaultEnableStreaming = true;
        private const bool DefaultEnableLuaOnWebGl = true;
        private const bool DefaultEnableLlmContextCompaction = false;
        public const bool DefaultEnableTokenCalibration = true;
        public const float DefaultConversationCompactionTriggerRatio = 0.8f;
        public const bool DefaultEnableContextPruning = true;
        public const int DefaultMaxRetainedToolResultMessages = 3;
        private const int DefaultMaxToolResultChars = 8000;
        private const int DefaultDefaultToolTimeoutMs = 30000;
        private const int DefaultMaxResponseChars = 0;

        private const int DefaultMaxToolCallRoundtrips = 20;

        // Default 20 (bounds context growth out of the box); 0 = EXPLICIT opt-out (unlimited).
        private const int DefaultMaxToolCallHistoryMessages = 20;
        private const int DefaultMaxParallelToolCalls = 4;

        internal const string DefaultUniversalSystemPromptPrefix =
            "CRITICAL RULES FOR ALL AGENTS:\n" +
            "1. TOOL CALLING: When tools/functions are available, you MUST use them (function calling format). NEVER output JSON in your text response if tools are available to do the job.\n" +
            "2. STRICT ADHERENCE: You must follow the user's task or hint EXACTLY. Do not hallucinate, invent, or add creative flair to tool arguments unless strictly requested.\n" +
            "3. NO CHIT-CHAT: Respond concisely. Do not explain what you are doing unless asked.\n" +
            "4. TOOL LIFECYCLE: If a tool returns a success message, continue with the NEXT step of the task. Do not call the same tool again with the same arguments.";

        #endregion

        #region Properties - delegate to Instance, allow override

        /// <summary>
        /// How many Lua repair attempts are allowed after execution failures. ///.</summary>
        public static int MaxLuaRepairRetries
        {
            get => _maxLuaRepairRetries ?? Instance?.MaxLuaRepairRetries ?? DefaultMaxLuaRepairRetries;
            set => _maxLuaRepairRetries = value;
        }

        /// <summary>
        /// Whether MEAI integration diagnostics are written to the log. ///.</summary>
        public static bool EnableMeaiDebugLogging
        {
            get => _enableMeaiDebugLogging ?? Instance?.EnableMeaiDebugLogging ?? DefaultEnableMeaiDebugLogging;
            set => _enableMeaiDebugLogging = value;
        }

        /// <summary>
        /// Timeout, in seconds, applied to LLM requests. ///.</summary>
        public static int LlmRequestTimeoutSeconds
        {
            get => _llmRequestTimeoutSeconds ??
                   (int?)Instance?.LlmRequestTimeoutSeconds ?? DefaultLlmRequestTimeoutSeconds;
            set => _llmRequestTimeoutSeconds = value;
        }

        /// <summary>
        /// How many times failed LLM requests may be retried. ///.</summary>
        public static int MaxLlmRequestRetries
        {
            get => _maxLlmRequestRetries ?? Instance?.MaxLlmRequestRetries ?? DefaultMaxLlmRequestRetries;
            set => _maxLlmRequestRetries = value;
        }

        /// <summary>
        /// Max bounded retries after a provider context-length-exceeded error; each retry drops ~25% more of the oldest history (roadmap §5). 0 disables overflow recovery.
        /// </summary>
        public static int MaxContextOverflowRetries
        {
            get => _maxContextOverflowRetries ??
                   Instance?.MaxContextOverflowRetries ?? DefaultMaxContextOverflowRetries;
            set => _maxContextOverflowRetries = value;
        }

        /// <summary>
        /// Whether HTTP request and response diagnostics are logged. ///.</summary>
        public static bool EnableHttpDebugLogging
        {
            get => _enableHttpDebugLogging ?? Instance?.EnableHttpDebugLogging ?? DefaultEnableHttpDebugLogging;
            set => _enableHttpDebugLogging = value;
        }

        /// <summary>
        /// Whether LLM token usage metrics are logged when available. ///.</summary>
        public static bool LogTokenUsage
        {
            get => _logTokenUsage ?? Instance?.LogTokenUsage ?? DefaultLogTokenUsage;
            set => _logTokenUsage = value;
        }

        /// <summary>
        /// Whether LLM request latency is logged. ///.</summary>
        public static bool LogLlmLatency
        {
            get => _logLlmLatency ?? Instance?.LogLlmLatency ?? DefaultLogLlmLatency;
            set => _logLlmLatency = value;
        }

        /// <summary>
        /// Whether LLM connection failures are logged. ///.</summary>
        public static bool LogLlmConnectionErrors
        {
            get => _logLlmConnectionErrors ?? Instance?.LogLlmConnectionErrors ?? DefaultLogLlmConnectionErrors;
            set => _logLlmConnectionErrors = value;
        }

        /// <summary>
        /// Approximate context window size used for prompt budgeting. ///.</summary>
        public static int ContextWindowTokens
        {
            get => _contextWindowTokens ?? Instance?.ContextWindowTokens ?? DefaultContextWindowTokens;
            set => _contextWindowTokens = value;
        }

        /// <summary>
        /// Universal system prompt prefix prepended to all agent prompts. ///.</summary>
        /// <example>
        /// CoreAISettings.UniversalSystemPromptPrefix =
        ///     "You are an AI agent in a Unity game. Always respond in the expected format. " +
        ///     "Never break character. Use tools when appropriate.";
        /// </example>
        public static string UniversalSystemPromptPrefix
        {
            get => _universalSystemPromptPrefixSet
                ? _universalSystemPromptPrefix
                : Instance?.UniversalSystemPromptPrefix ?? DefaultUniversalSystemPromptPrefix;
            set
            {
                _universalSystemPromptPrefix = value;
                _universalSystemPromptPrefixSet = true;
            }
        }

        /// <summary>
        /// Sampling temperature value supplied to LLM backends when enabled. ///.</summary>
        public static float Temperature
        {
            get => _temperature ?? Instance?.Temperature ?? DefaultTemperature;
            set => _temperature = value;
        }

        /// <summary>
        /// When <c>true</c>, <see cref="Temperature"/> is sent to LLM backends; when <c>false</c>, backends use their default sampling temperature.
        /// </summary>
        public static bool OverrideTemperature
        {
            get =>
                _overrideTemperature ??
                Instance?.OverrideTemperature ??
                DefaultOverrideTemperature;
            set => _overrideTemperature = value;
        }

        /// <summary>
        /// How many tool-call failures may be retried during one model request. ///.</summary>
        public static int MaxToolCallRetries
        {
            get => _maxToolCallRetries ?? Instance?.MaxToolCallRetries ?? DefaultMaxToolCallRetries;
            set => _maxToolCallRetries = value;
        }

        /// <summary>
        /// Whether tool-call lifecycle events are logged. ///.</summary>
        public static bool LogToolCalls
        {
            get => _logToolCalls ?? Instance?.LogToolCalls ?? DefaultLogToolCalls;
            set => _logToolCalls = value;
        }

        /// <summary>
        /// Whether tool-call arguments are included in logs. ///.</summary>
        public static bool LogToolCallArguments
        {
            get => _logToolCallArguments ?? Instance?.LogToolCallArguments ?? DefaultLogToolCallArguments;
            set => _logToolCallArguments = value;
        }

        /// <summary>
        /// Whether tool-call results are included in logs. ///.</summary>
        public static bool LogToolCallResults
        {
            get => _logToolCallResults ?? Instance?.LogToolCallResults ?? DefaultLogToolCallResults;
            set => _logToolCallResults = value;
        }

        /// <summary>
        /// Whether detailed MEAI tool-calling steps are logged. ///.</summary>
        public static bool LogMeaiToolCallingSteps
        {
            get => _logMeaiToolCallingSteps ?? Instance?.LogMeaiToolCallingSteps ?? DefaultLogMeaiToolCallingSteps;
            set => _logMeaiToolCallingSteps = value;
        }

        /// <summary>
        /// Whether identical tool calls may run more than once in a request. ///.</summary>
        public static bool AllowDuplicateToolCalls
        {
            get => _allowDuplicateToolCalls ?? Instance?.AllowDuplicateToolCalls ?? DefaultAllowDuplicateToolCalls;
            set => _allowDuplicateToolCalls = value;
        }

        /// <summary>
        /// Whether streaming LLM responses are enabled when supported. ///.</summary>
        public static bool EnableStreaming
        {
            get => _enableStreaming ?? Instance?.EnableStreaming ?? DefaultEnableStreaming;
            set => _enableStreaming = value;
        }

        /// <summary>
        /// Whether the Lua sandbox may run on the WebGL player (opt-in). ///.</summary>
        public static bool EnableLuaOnWebGl
        {
            get => _enableLuaOnWebGl ?? Instance?.EnableLuaOnWebGl ?? DefaultEnableLuaOnWebGl;
            set => _enableLuaOnWebGl = value;
        }

        /// <summary>
        /// Whether long LLM histories may be compacted before requests. ///.</summary>
        public static bool EnableLlmContextCompaction
        {
            get =>
                _enableLlmContextCompaction ??
                Instance?.EnableLlmContextCompaction ??
                DefaultEnableLlmContextCompaction;
            set => _enableLlmContextCompaction = value;
        }

        /// <summary>
        /// When true, the pre-flight token estimate is nudged toward observed real prompt tokens (bounded).
        /// The script-aware base estimate always applies.
        /// </summary>
        public static bool EnableTokenCalibration
        {
            get =>
                _enableTokenCalibration ??
                Instance?.EnableTokenCalibration ??
                DefaultEnableTokenCalibration;
            set => _enableTokenCalibration = value;
        }

        /// <summary>
        /// Roadmap §2 compaction trigger fraction of the history budget.
        /// </summary>
        public static float ConversationCompactionTriggerRatio
        {
            get =>
                _conversationCompactionTriggerRatio ??
                Instance?.ConversationCompactionTriggerRatio ??
                DefaultConversationCompactionTriggerRatio;
            set => _conversationCompactionTriggerRatio = value;
        }

        /// <summary>
        /// Whether roadmap §7 context editing prunes stale prompt-history entries before compaction.
        /// </summary>
        public static bool EnableContextPruning
        {
            get =>
                _enableContextPruning ??
                Instance?.EnableContextPruning ??
                DefaultEnableContextPruning;
            set => _enableContextPruning = value;
        }

        /// <summary>
        /// Newest durable tool-result messages retained in the prompt history copy during roadmap §7 pruning.
        /// </summary>
        public static int MaxRetainedToolResultMessages
        {
            get => MathMaxZero(
                _maxRetainedToolResultMessages ??
                Instance?.MaxRetainedToolResultMessages ??
                DefaultMaxRetainedToolResultMessages);
            set => _maxRetainedToolResultMessages = value;
        }

        /// <summary>
        /// Max chars per tool result sent to the model. 0 = no truncation.
        /// Default: 8000 (~2000 tokens).
        /// </summary>
        public static int MaxToolResultChars
        {
            get => _maxToolResultChars ?? Instance?.MaxToolResultChars ?? DefaultMaxToolResultChars;
            set => _maxToolResultChars = value;
        }

        /// <summary>
        /// Per-tool execution timeout (ms). 0 = no per-tool timeout.
        /// Default: 30000 (30 seconds).
        /// </summary>
        public static int DefaultToolTimeoutMs
        {
            get => _defaultToolTimeoutMs ?? Instance?.DefaultToolTimeoutMs ?? DefaultDefaultToolTimeoutMs;
            set => _defaultToolTimeoutMs = value;
        }

        /// <summary>
        /// Max response chars from the model before soft-truncation. 0 = disabled.
        /// </summary>
        public static int MaxResponseChars
        {
            get => _maxResponseChars ?? Instance?.MaxResponseChars ?? DefaultMaxResponseChars;
            set => _maxResponseChars = value;
        }

        /// <summary>
        /// Max tool-call roundtrips per request. Prevents infinite loops. Default: 20.
        /// <c>0</c> = unlimited (cap disabled). Per-agent and per-call overrides take priority.
        /// </summary>
        public static int MaxToolCallRoundtrips
        {
            get => _maxToolCallRoundtrips ?? Instance?.MaxToolCallRoundtrips ?? DefaultMaxToolCallRoundtrips;
            set => _maxToolCallRoundtrips = value;
        }

        /// <summary>
        /// Max tool call history messages in the MEAI message list during tool-calling loop.
        /// Default: 20. <c>0</c> = explicit opt-out (no limit).
        /// </summary>
        public static int MaxToolCallHistoryMessages
        {
            get => _maxToolCallHistoryMessages ??
                   Instance?.MaxToolCallHistoryMessages ?? DefaultMaxToolCallHistoryMessages;
            set => _maxToolCallHistoryMessages = value;
        }

        /// <summary>
        /// Max tool calls within one batch that may run concurrently. 1 (or less) = sequential.
        /// Default: 4.
        /// </summary>
        public static int MaxParallelToolCalls
        {
            get => _maxParallelToolCalls ?? Instance?.MaxParallelToolCalls ?? DefaultMaxParallelToolCalls;
            set => _maxParallelToolCalls = value;
        }

        #endregion

        /// <summary>
        /// Clears all process-level setting overrides so subsequent reads fall back to
        /// the active <see cref="Instance"/> or the built-in defaults. Not atomic with respect to
        /// concurrent property reads: only two concurrent resets are serialized against each other.
        /// </summary>
        public static void ResetOverrides()
        {
            lock (_lock)
            {
                _maxLuaRepairRetries = null;
                _enableMeaiDebugLogging = null;
                _llmRequestTimeoutSeconds = null;
                _maxLlmRequestRetries = null;
                _maxContextOverflowRetries = null;
                _enableHttpDebugLogging = null;
                _logTokenUsage = null;
                _logLlmLatency = null;
                _logLlmConnectionErrors = null;
                _contextWindowTokens = null;
                _universalSystemPromptPrefix = null;
                _universalSystemPromptPrefixSet = false;
                _temperature = null;
                _overrideTemperature = null;
                _maxToolCallRetries = null;
                _logToolCalls = null;
                _logToolCallArguments = null;
                _logToolCallResults = null;
                _logMeaiToolCallingSteps = null;
                _allowDuplicateToolCalls = null;
                _enableStreaming = null;
                _enableLuaOnWebGl = null;
                _enableLlmContextCompaction = null;
                _enableTokenCalibration = null;
                _conversationCompactionTriggerRatio = null;
                _enableContextPruning = null;
                _maxRetainedToolResultMessages = null;
                _maxToolResultChars = null;
                _defaultToolTimeoutMs = null;
                _maxResponseChars = null;
                _maxToolCallRoundtrips = null;
                _maxToolCallHistoryMessages = null;
                _maxParallelToolCalls = null;
            }
        }

        private static int MathMaxZero(int value)
        {
            return value < 0 ? 0 : value;
        }
    }
}
