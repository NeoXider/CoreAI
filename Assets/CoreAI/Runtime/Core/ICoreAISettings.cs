namespace CoreAI
{
    /// <summary>
    /// Snapshot of rate-limiter state for diagnostics and UI.
    /// </summary>
    public readonly struct RateLimiterMetrics
    {
        /// <summary>Max requests allowed within the window.</summary>
        public int MaxRequestsPerWindow { get; }

        /// <summary>Window size in seconds.</summary>
        public int WindowSeconds { get; }

        /// <summary>Requests accepted (not rejected) within the current window.</summary>
        public int AcceptedInWindow { get; }

        /// <summary>Total requests rejected since the service was created.</summary>
        public long TotalRejected { get; }

        public RateLimiterMetrics(int maxRequestsPerWindow, int windowSeconds, int acceptedInWindow, long totalRejected)
        {
            MaxRequestsPerWindow = maxRequestsPerWindow;
            WindowSeconds = windowSeconds;
            AcceptedInWindow = acceptedInWindow;
            TotalRejected = totalRejected;
        }
    }

    /// <summary>
    /// Host-wide CoreAI settings: infrastructure switches, logging, and agent limits.
    /// </summary>
    public interface ICoreAISettings
    {
        /// <summary>Max consecutive Programmer Lua repair attempts before stopping the retry loop.</summary>
        int MaxLuaRepairRetries { get; }

        /// <summary>Verbose MEAI pipeline logging (requests, responses, JSON).</summary>
        bool EnableMeaiDebugLogging { get; }

        /// <summary>LLM request timeout in seconds.</summary>
        float LlmRequestTimeoutSeconds { get; }

        /// <summary>
        /// Max **additional** HTTP completion attempts after a retryable failure (<c>429</c> / <c>5xx</c> surfaced as
        /// <see cref="LlmClientException"/> or <see cref="LlmCompletionResult"/> with <see cref="LlmErrorCode.RateLimited"/> /
        /// <see cref="LlmErrorCode.BackendUnavailable"/>). Implemented in <see cref="LoggingLlmClientDecorator"/>.
        /// </summary>
        int MaxLlmRequestRetries { get; }

        /// <summary>
        /// Max bounded retries after a provider context-length-exceeded error; each retry drops ~25% more of the oldest history (roadmap §5). 0 disables overflow recovery.
        /// </summary>
        int MaxContextOverflowRetries => 3;

        /// <summary>Low-level HTTP request/response logging.</summary>
        bool EnableHttpDebugLogging { get; }

        /// <summary>Log token usage (prompt, completion, total).</summary>
        bool LogTokenUsage { get; }

        /// <summary>Log end-to-end LLM latency.</summary>
        bool LogLlmLatency { get; }

        /// <summary>Log LLM connection errors.</summary>
        bool LogLlmConnectionErrors { get; }

        /// <summary>Model context window size in tokens.</summary>
        int ContextWindowTokens { get; }

        /// <summary>Universal system prompt prefix applied ahead of every agent system string.</summary>
        string UniversalSystemPromptPrefix { get; }

        /// <summary>
        /// Optional extra text appended after the standard <c>## Tool Contract</c> block (before conditional lines and the tool list).
        /// Empty string keeps only built-in orchestrator guidance.
        /// </summary>
        string ToolContractAdditionalInstructions => "";

        /// <summary>Sampling temperature requested from the LLM backend.</summary>
        float Temperature { get; }

        /// <summary>
        /// When <c>true</c>, <see cref="Temperature"/> is sent on LLM requests (HTTP body and LLMUnity via MEAI).
        /// When <c>false</c>, sampling temperature is omitted so each backend uses its own default.
        /// </summary>
        bool OverrideTemperature => false;

        /// <summary>Max consecutive tool-call failures before aborting the agent turn.</summary>
        int MaxToolCallRetries { get; }

        /// <summary>Emit a line when any tool is invoked.</summary>
        bool LogToolCalls { get; }

        /// <summary>Log serialized tool arguments (can be noisy).</summary>
        bool LogToolCallArguments { get; }

        /// <summary>Log tool results returned to the model.</summary>
        bool LogToolCallResults { get; }

        /// <summary>Log MEAI function-calling iterations and internal retries.</summary>
        bool LogMeaiToolCallingSteps { get; }

        /// <summary>
        /// Allow identical back-to-back tool calls. When <c>false</c>, guards against accidental loops but blocks intentional repeats.
        /// </summary>
        bool AllowDuplicateToolCalls { get; }

        /// <summary>
        /// When <c>true</c>, the native <c>world_command</c> <c>spawn</c> action may create built-in Unity
        /// primitives (cube, sphere, cylinder, capsule, plane, empty) directly when the requested
        /// <c>prefabKey</c> is not a registered prefab — so the world tool is usable out of the box without a
        /// prefab registry. When <c>false</c>, spawn is restricted to registered prefab keys (legacy). Default <c>true</c>.
        /// </summary>
        bool AllowWorldPrimitives => true;

        /// <summary>
        /// Global streaming toggle for LLM completions (SSE on HTTP APIs, callbacks on LLMUnity).
        /// Per-role override: <c>AgentBuilder.WithStreaming()</c> / <c>AgentMemoryPolicy.SetStreamingEnabled()</c>.
        /// UI layer may further override via <c>CoreAiChatConfig.EnableStreaming</c>. Default host implementations typically return <c>true</c>.
        /// </summary>
        bool EnableStreaming { get; }

        /// <summary>
        /// Run the MoonSharp Lua sandbox on the WebGL player. Default <c>true</c>.
        /// Lua works on all other players regardless of this flag; it gates only the WebGL/IL2CPP path,
        /// which additionally requires link.xml stripping protection (shipped). The Full reflection tier
        /// (<c>unity_*</c>) is always force-disabled on WebGL even when this is <c>true</c>.
        /// </summary>
        bool EnableLuaOnWebGl => true;

        /// <summary>
        /// Maximum tokens.
        /// </summary>
        int MaxTokens => 0;

        /// <summary>
        /// When true, overflowing chat history may be summarized via an auxiliary LLM call (extra latency/cost).
        /// When false, compaction stays on the deterministic bullet rollup.
        /// </summary>
        bool EnableLlmContextCompaction => false;

        /// <summary>
        /// When true, the pre-flight token estimate is nudged toward observed real prompt tokens (bounded).
        /// The script-aware base estimate always applies.
        /// </summary>
        bool EnableTokenCalibration => true;

        /// <summary>
        /// Stable key used to persist token-estimator calibration, usually the active model id.
        /// </summary>
        string TokenCalibrationModelKey => "default";

        /// <summary>
        /// When false, the orchestrator does not cap chat history with a rolling summary partition (full loaded transcript stays in the MEAI tail; risk of context overflow).
        /// </summary>
        bool EnableConversationHistorySummarization => true;

        /// <summary>
        /// When greater than zero, overrides the computed recent-history token budget from <see cref="IContextBudgetPolicy"/>.
        /// </summary>
        int ConversationHistoryRecentTokenBudgetOverride => 0;

        /// <summary>
        /// When greater than zero, caps persisted rolling summary text to roughly this many estimated tokens after each rollup.
        /// </summary>
        int ConversationRolledSummaryMaxTokens => 0;

        /// <summary>
        /// Compaction (summarization of older turns) only triggers once estimated history tokens reach this
        /// fraction of the history budget; below it, all turns are kept verbatim and the stored summary is left untouched.
        /// Roadmap §2. Invalid values fall back to <see cref="CoreAISettings.DefaultConversationCompactionTriggerRatio"/>.
        /// </summary>
        float ConversationCompactionTriggerRatio => 0.8f;

        /// <summary>
        /// When true, roadmap §7 context editing prunes stale prompt-history entries before compaction.
        /// This operates only on the in-memory request copy, never on durable chat history.
        /// </summary>
        bool EnableContextPruning => true;

        /// <summary>
        /// Number of newest durable <c>tool</c> / <c>## Tool Results</c> messages retained in prompt history
        /// by roadmap §7 context pruning.
        /// </summary>
        int MaxRetainedToolResultMessages => 3;

        /// <summary>
        /// Tool invocation marshaler.
        /// </summary>
        ILlmAsyncMarshaler ToolInvocationMarshaler => PassThroughLlmAsyncMarshaler.Instance;

        /// <summary>
        /// When greater than zero, tool result strings longer than this are soft-truncated with an ellipsis
        /// before being sent back to the model. Prevents a single tool from overflowing the context window.
        /// Default 8000 chars (~2000 tokens). 0 = no truncation.
        /// </summary>
        int MaxToolResultChars => 8000;

        /// <summary>
        /// Default per-tool execution timeout in milliseconds. If a tool body does not complete
        /// within this window, the invocation is cancelled and an error result is returned to the model.
        /// Default 30000 ms (30 seconds). 0 = no per-tool timeout (relies on outer orchestrator timeout only).
        /// </summary>
        int DefaultToolTimeoutMs => 30000;

        /// <summary>
        /// When greater than zero, the total accumulated response text is soft-truncated at this character count.
        /// Prevents runaway generation. Default 0 = disabled. Users opt in via Inspector.
        /// </summary>
        int MaxResponseChars => 0;

        /// <summary>
        /// Maximum tool-call roundtrips (iterations) within a single request. One roundtrip = one LLM call +
        /// one tool-execution batch. Prevents infinite tool-calling loops. Default 20.
        /// <para>
        /// <b>Value meaning (identical at every level):</b> a positive number caps the loop;
        /// <c>0</c> = UNLIMITED (cap disabled). At the global level a negative value is clamped back to the
        /// default; at the per-agent / per-call level <c>null</c> means "inherit the next level down".
        /// </para>
        /// <para>
        /// <b>Resolution priority (highest first):</b>
        /// per-call <c>AiTaskRequest.MaxToolCallRoundtrips</c> →
        /// per-agent <see cref="AgentBuilder.WithMaxToolCallRoundtrips"/> →
        /// this global setting. The built-in <c>Programmer</c> and <c>Creator</c> roles default to <c>0</c>
        /// (unlimited). When the cap is hit, the agent stops and logs a warning explaining how to raise it.
        /// </para>
        /// </summary>
        int MaxToolCallRoundtrips => 20;

        /// <summary>
        /// USD price per 1K prompt/input tokens for the token-budget overlay cost estimate.
        /// Default 0 = pricing unset; the overlay shows token counts only.
        /// </summary>
        float InputTokenPricePer1KUsd => 0f;

        /// <summary>
        /// USD price per 1K completion/output tokens for the token-budget overlay cost estimate.
        /// Default 0 = pricing unset; the overlay shows token counts only.
        /// </summary>
        float OutputTokenPricePer1KUsd => 0f;

        /// <summary>
        /// Maximum number of tool call message pairs (assistant + tool result) to keep in the
        /// MEAI message list during a single request's tool-calling loop in <see cref="SmartToolCallingChatClient"/>.
        /// When the count exceeds this limit, the oldest tool call pair is removed. <c>0</c> = unlimited
        /// (default): the model never forgets earlier tool steps in the same turn, so long multi-step work
        /// does not repeat itself.
        /// <para>See the implementation details for usage guidance.</para>
        /// </summary>
        int MaxToolCallHistoryMessages => 0;

        /// <summary>
        /// Maximum number of tool calls within a single batch (one LLM turn) that may execute concurrently.
        /// Default 4. A value of <c>1</c> (or lower) forces the original strictly-sequential execution path.
        /// State-mutating built-in tools (e.g. <c>memory</c>, <c>manage_mods</c>, <c>manage_skills</c>) are always
        /// serialized relative to each other regardless of this value; only independent/read tools run in parallel.
        /// Result order is always preserved (original call order), independent of completion order.
        /// </summary>
        int MaxParallelToolCalls => 4;
    }
}
