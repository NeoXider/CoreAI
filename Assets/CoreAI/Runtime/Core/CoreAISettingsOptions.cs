namespace CoreAI
{
    /// <summary>
    /// Mutable Unity-free implementation of <see cref="ICoreAISettings"/> for tests and runtime bootstrap.
    /// Unity projects should author these values through CoreAISettingsAsset and pass around this interface.
    /// </summary>
    public sealed class CoreAISettingsOptions : ICoreAISettings
    {
        public int MaxLuaRepairRetries { get; set; } = 3;
        public bool EnableMeaiDebugLogging { get; set; }
        public float LlmRequestTimeoutSeconds { get; set; } = 120f;
        public int MaxLlmRequestRetries { get; set; } = 1;
        public int MaxContextOverflowRetries { get; set; } = 3;
        public bool EnableHttpDebugLogging { get; set; }
        public bool LogTokenUsage { get; set; } = true;
        public bool LogLlmLatency { get; set; } = true;
        public bool LogLlmConnectionErrors { get; set; } = true;
        public int ContextWindowTokens { get; set; } = CoreAISettings.DefaultContextWindowTokens;

        // WHY: The canonical prefix, not a paraphrase. This class used to declare its own short
        // "Respond concisely..." string, so every host bootstrapped through it silently lost the
        // CRITICAL RULES block - including "NEVER output JSON in your text response if tools are
        // available", the rule that keeps models on the function-calling channel.
        public string UniversalSystemPromptPrefix { get; set; } =
            CoreAISettings.DefaultUniversalSystemPromptPrefix;

        public string ToolContractAdditionalInstructions { get; set; } = "";
        public float Temperature { get; set; } = 0.1f;
        public bool OverrideTemperature { get; set; }
        public int MaxToolCallRetries { get; set; } = 3;
        public bool LogToolCalls { get; set; } = true;
        public bool LogToolCallArguments { get; set; } = true;
        public bool LogToolCallResults { get; set; } = true;
        public bool LogMeaiToolCallingSteps { get; set; } = true;
        public bool AllowDuplicateToolCalls { get; set; }
        public bool EnableStreaming { get; set; } = true;
        public int MaxTokens { get; set; } = 128000;
        public bool EnableLlmContextCompaction { get; set; }
        public bool EnableTokenCalibration { get; set; } = CoreAISettings.DefaultEnableTokenCalibration;
        public string TokenCalibrationModelKey { get; set; } = "default";
        public bool EnableConversationHistorySummarization { get; set; } = true;
        public int ConversationHistoryRecentTokenBudgetOverride { get; set; }

        // WHY: 0 is the EXPLICIT "unlimited" opt-out, so the implicit default must be the interface's
        // documented cap; otherwise the rolling summary was never trimmed for options-based hosts.
        public int ConversationRolledSummaryMaxTokens { get; set; } =
            ICoreAISettings.DefaultConversationRolledSummaryMaxTokens;

        public float ConversationCompactionTriggerRatio { get; set; } =
            CoreAISettings.DefaultConversationCompactionTriggerRatio;

        public bool EnableContextPruning { get; set; } = CoreAISettings.DefaultEnableContextPruning;

        public int MaxRetainedToolResultMessages { get; set; } =
            CoreAISettings.DefaultMaxRetainedToolResultMessages;

        public ILlmAsyncMarshaler ToolInvocationMarshaler { get; set; } = PassThroughLlmAsyncMarshaler.Instance;
        public int MaxToolResultChars { get; set; } = 8000;
        public int DefaultToolTimeoutMs { get; set; } = 30000;
        public int MaxResponseChars { get; set; }
        public int MaxToolCallRoundtrips { get; set; } = 20;
        public int MaxToolCallHistoryMessages { get; set; } = 20; // 0 = EXPLICIT opt-out (unlimited)
        public int MaxParallelToolCalls { get; set; } = 4;

        // WHY: These four were never declared here, so the compiler silently bound them to the default
        // interface members and From() could not copy them. AllowWorldPrimitives in particular meant a
        // host that had DISABLED primitive spawning got it re-enabled the moment its settings round-tripped
        // through this class, and the per-token prices reset to 0 (cost overlay shows tokens only).
        public bool AllowWorldPrimitives { get; set; } = true;
        public bool EnableLuaOnWebGl { get; set; } = true;
        public float InputTokenPricePer1KUsd { get; set; }
        public float OutputTokenPricePer1KUsd { get; set; }

        public static CoreAISettingsOptions From(ICoreAISettings source)
        {
            if (source == null)
            {
                return new CoreAISettingsOptions();
            }

            return new CoreAISettingsOptions
            {
                MaxLuaRepairRetries = source.MaxLuaRepairRetries,
                EnableMeaiDebugLogging = source.EnableMeaiDebugLogging,
                LlmRequestTimeoutSeconds = source.LlmRequestTimeoutSeconds,
                MaxLlmRequestRetries = source.MaxLlmRequestRetries,
                MaxContextOverflowRetries = source.MaxContextOverflowRetries,
                EnableHttpDebugLogging = source.EnableHttpDebugLogging,
                LogTokenUsage = source.LogTokenUsage,
                LogLlmLatency = source.LogLlmLatency,
                LogLlmConnectionErrors = source.LogLlmConnectionErrors,
                ContextWindowTokens = source.ContextWindowTokens,
                UniversalSystemPromptPrefix = source.UniversalSystemPromptPrefix,
                ToolContractAdditionalInstructions = source.ToolContractAdditionalInstructions,
                Temperature = source.Temperature,
                OverrideTemperature = source.OverrideTemperature,
                MaxToolCallRetries = source.MaxToolCallRetries,
                LogToolCalls = source.LogToolCalls,
                LogToolCallArguments = source.LogToolCallArguments,
                LogToolCallResults = source.LogToolCallResults,
                LogMeaiToolCallingSteps = source.LogMeaiToolCallingSteps,
                AllowDuplicateToolCalls = source.AllowDuplicateToolCalls,
                EnableStreaming = source.EnableStreaming,
                MaxTokens = source.MaxTokens,
                EnableLlmContextCompaction = source.EnableLlmContextCompaction,
                EnableTokenCalibration = source.EnableTokenCalibration,
                TokenCalibrationModelKey = source.TokenCalibrationModelKey,
                EnableConversationHistorySummarization = source.EnableConversationHistorySummarization,
                ConversationHistoryRecentTokenBudgetOverride = source.ConversationHistoryRecentTokenBudgetOverride,
                ConversationRolledSummaryMaxTokens = source.ConversationRolledSummaryMaxTokens,
                ConversationCompactionTriggerRatio = source.ConversationCompactionTriggerRatio,
                EnableContextPruning = source.EnableContextPruning,
                MaxRetainedToolResultMessages = source.MaxRetainedToolResultMessages,
                ToolInvocationMarshaler = source.ToolInvocationMarshaler,
                MaxToolResultChars = source.MaxToolResultChars,
                DefaultToolTimeoutMs = source.DefaultToolTimeoutMs,
                MaxResponseChars = source.MaxResponseChars,
                MaxToolCallRoundtrips = source.MaxToolCallRoundtrips,
                MaxToolCallHistoryMessages = source.MaxToolCallHistoryMessages,
                MaxParallelToolCalls = source.MaxParallelToolCalls,
                AllowWorldPrimitives = source.AllowWorldPrimitives,
                EnableLuaOnWebGl = source.EnableLuaOnWebGl,
                InputTokenPricePer1KUsd = source.InputTokenPricePer1KUsd,
                OutputTokenPricePer1KUsd = source.OutputTokenPricePer1KUsd
            };
        }
    }
}
