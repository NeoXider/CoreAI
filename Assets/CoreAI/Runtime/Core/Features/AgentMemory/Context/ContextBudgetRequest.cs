using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>Inputs for <see cref="IContextBudgetPolicy"/>.</summary>
    public sealed class ContextBudgetRequest
    {
        /// <summary>Context window tokens (role config or routing profile).</summary>
        public int MaxContextTokens { get; set; }

        /// <summary>Full system prompt before conversation summary injection.</summary>
        public string SystemPrompt { get; set; }

        /// <summary>User payload text.</summary>
        public string UserPayload { get; set; }

        /// <summary>Tools exposed on this request (for schema/description estimate).</summary>
        public IReadOnlyList<ILlmTool> Tools { get; set; }

        /// <summary>Optional max output tokens for this call.</summary>
        public int? MaxOutputTokens { get; set; }

        /// <summary>Retry level used when progressively shrinking context.</summary>
        public int ContextRetryLevel { get; set; }
    }
}
