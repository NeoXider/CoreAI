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

        /// <summary>Default sampling temperature (0.0–2.0).</summary>
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
        /// Global streaming toggle for LLM completions (SSE on HTTP APIs, callbacks on LLMUnity).
        /// Per-role override: <c>AgentBuilder.WithStreaming()</c> / <c>AgentMemoryPolicy.SetStreamingEnabled()</c>.
        /// UI layer may further override via <c>CoreAiChatConfig.EnableStreaming</c>. Default host implementations typically return <c>true</c>.
        /// </summary>
        bool EnableStreaming { get; }

        /// <summary>
        /// Default max output tokens for both HTTP and LLMUnity when callers omit explicit limits.
        /// Applied through <c>ChatOptions.MaxOutputTokens</c> when absent on the outgoing request.
        /// <para>
        /// Priority: <c>LlmCompletionRequest.MaxOutputTokens</c> → <c>AiTaskRequest.MaxOutputTokens</c> → per-agent policy →
        /// <see cref="MaxTokens"/> (this fallback) → provider default.
        /// </para>
        /// <para>
        /// <c>0</c> or negative means unset — skip this fallback. Default interface member returns <c>0</c> so legacy stub settings compile unchanged.
        /// </para>
        /// </summary>
        int MaxTokens => 0;

        /// <summary>
        /// When true, overflowing chat history may be summarized via an auxiliary LLM call (extra latency/cost).
        /// When false, compaction stays on the deterministic bullet rollup.
        /// </summary>
        bool EnableLlmContextCompaction => false;

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
        /// Marshaler for MEAI <see cref="Microsoft.Extensions.AI.AIFunction.InvokeAsync"/> so tool bodies run on the host’s required thread.
        /// Default portable implementation: <see cref="PassThroughLlmAsyncMarshaler.Instance"/>.
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
        /// Maximum tool-call roundtrips (iterations) within a single request.
        /// Each iteration = one LLM call + tool execution batch. Prevents infinite tool-calling loops.
        /// Default 10. Must be at least 1.
        /// </summary>
        int MaxToolCallRoundtrips => 10;

        /// <summary>
        /// Maximum number of tool call message pairs (assistant + tool result) to keep in the
        /// MEAI message list during a single request's tool-calling loop in <see cref="SmartToolCallingChatClient"/>.
        /// When the count exceeds this limit, the oldest tool call pair is removed to prevent unbounded growth.
        /// <para>0 = no limit (retain all). Default 20 (10 roundtrips × 2 messages each).</para>
        /// </summary>
        int MaxToolCallHistoryMessages => 20;
    }
}
