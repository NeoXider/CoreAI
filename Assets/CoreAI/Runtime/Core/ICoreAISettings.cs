namespace CoreAI
{
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

        /// <summary>Retry count for transient LLM network failures.</summary>
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

        /// <summary>Default sampling temperature (0.0–2.0).</summary>
        float Temperature { get; }

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
    }
}
