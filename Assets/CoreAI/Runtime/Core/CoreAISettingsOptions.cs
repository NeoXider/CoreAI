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

        public string UniversalSystemPromptPrefix { get; set; } =
            "Respond concisely and to the point. Avoid unnecessary verbosity.";

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
        public int ConversationRolledSummaryMaxTokens { get; set; }

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
                MaxParallelToolCalls = source.MaxParallelToolCalls
            };
        }
    }
}
