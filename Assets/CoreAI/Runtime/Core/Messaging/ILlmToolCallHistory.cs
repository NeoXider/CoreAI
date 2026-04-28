using System.Collections.Generic;

namespace CoreAI.Messaging
{
    /// <summary>
    /// Bounded tool-call history for tests and diagnostics.
    /// </summary>
    public interface ILlmToolCallHistory
    {
        /// <summary>Records a started tool call.</summary>
        void RecordStarted(LlmToolCallStarted evt);

        /// <summary>Records a completed tool call.</summary>
        void RecordCompleted(LlmToolCallCompleted evt);

        /// <summary>Records a failed tool call.</summary>
        void RecordFailed(LlmToolCallFailed evt);

        /// <summary>Returns a snapshot of recent records.</summary>
        IReadOnlyList<LlmToolCallRecord> Snapshot();

        /// <summary>Clears all records.</summary>
        void Clear();
    }
}
