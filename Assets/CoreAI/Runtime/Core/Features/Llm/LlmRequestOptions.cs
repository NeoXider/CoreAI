using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>Provider-independent reasoning effort. Default inherits the next settings layer.</summary>
    public enum LlmReasoningEffort
    {
        Default = 0,
        None,
        Minimal,
        Low,
        Medium,
        High,
        XHigh
    }

    /// <summary>Wire format used to express provider reasoning controls.</summary>
    public enum LlmReasoningDialect
    {
        Auto = 0,
        OpenAi,
        OpenRouter,
        QwenTemplate,
        AnthropicCompat,
        GeminiCompat,
        PassthroughEffort,
        Off
    }

    /// <summary>Portable reasoning overrides for one agent or request.</summary>
    public readonly struct LlmReasoningOptions
    {
        /// <summary>Requested reasoning effort; Default inherits the next layer.</summary>
        public LlmReasoningEffort Effort { get; }

        /// <summary>Optional provider reasoning token budget.</summary>
        public int? BudgetTokens { get; }

        public LlmReasoningOptions(LlmReasoningEffort effort, int? budgetTokens = null)
        {
            Effort = effort;
            BudgetTokens = budgetTokens;
        }
    }

    /// <summary>Optional sampling values. Null fields inherit the next settings layer.</summary>
    public sealed class LlmSamplingOptions
    {
        /// <summary>Nucleus sampling probability.</summary>
        public float? TopP { get; set; }

        /// <summary>Optional deterministic sampling seed.</summary>
        public long? Seed { get; set; }

        /// <summary>Sequences that stop response generation.</summary>
        public IReadOnlyList<string> Stop { get; set; }

        /// <summary>Penalty for tokens repeated frequently in the conversation.</summary>
        public float? FrequencyPenalty { get; set; }

        /// <summary>Penalty for tokens already present in the conversation.</summary>
        public float? PresencePenalty { get; set; }
    }
}
