using System;
using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>
    /// Outcome of a recorded agent turn, used by live diagnostics.
    /// </summary>
    public enum AgentTurnStatus
    {
        /// <summary>The turn completed and produced an assistant response.</summary>
        Completed = 0,

        /// <summary>The turn ended with an error before producing a usable response.</summary>
        Failed = 1
    }

    /// <summary>
    /// One observed tool call captured in an <see cref="AgentTurnTrace"/> for diagnostics.
    /// </summary>
    public sealed class AgentTurnToolCallTrace
    {
        /// <summary>Tool name.</summary>
        public string Name { get; set; } = "";

        /// <summary>True when the tool returned a non-error result and did not throw.</summary>
        public bool Success { get; set; }

        /// <summary>Wall-clock execution time in milliseconds.</summary>
        public double DurationMs { get; set; }

        /// <summary>How the call was discovered: native, text, duplicate, or missing.</summary>
        public string Source { get; set; } = "";

        /// <summary>Tool result or failure detail.</summary>
        public string Detail { get; set; } = "";
    }

    /// <summary>
    /// Diagnostic trace for one agent turn.
    /// </summary>
    public sealed class AgentTurnTrace
    {
        /// <summary>Trace id.</summary>
        public string TraceId { get; set; } = "";

        /// <summary>Agent role id.</summary>
        public string RoleId { get; set; } = "";

        /// <summary>Routing profile id when known.</summary>
        public string RoutingProfileId { get; set; } = "";

        /// <summary>Model id when known.</summary>
        public string Model { get; set; } = "";

        /// <summary>System prompt preview.</summary>
        public string SystemPromptPreview { get; set; } = "";

        /// <summary>User payload.</summary>
        public string UserPayload { get; set; } = "";

        /// <summary>Assistant response.</summary>
        public string AssistantResponse { get; set; } = "";

        /// <summary>Error text, when the turn failed.</summary>
        public string Error { get; set; } = "";

        /// <summary>Prompt tokens.</summary>
        public int PromptTokens { get; set; }

        /// <summary>Completion tokens.</summary>
        public int CompletionTokens { get; set; }

        /// <summary>Total tokens.</summary>
        public int TotalTokens { get; set; }

        /// <summary>Provider-reported prompt/input tokens read from cache.</summary>
        public int CacheReadTokens { get; set; }

        /// <summary>Provider-reported prompt/input tokens written to cache.</summary>
        public int CacheWriteTokens { get; set; }

        /// <summary>Estimated history budget last applied (0 when chat history off).</summary>
        public int HistoryTokenBudget { get; set; }

        /// <summary>Chat messages sent as MEAI history for this turn.</summary>
        public int ChatHistoryMessageCount { get; set; }

        /// <summary>Turn outcome derived from the presence of <see cref="Error"/>.</summary>
        public AgentTurnStatus Status { get; set; } = AgentTurnStatus.Completed;

        /// <summary>UTC ticks when the trace was recorded (turn completion time).</summary>
        public long RecordedAtUtcTicks { get; set; } = DateTime.UtcNow.Ticks;

        /// <summary>Tool calls observed during this turn, in execution order.</summary>
        public List<AgentTurnToolCallTrace> ToolCalls { get; } = new();
    }
}
