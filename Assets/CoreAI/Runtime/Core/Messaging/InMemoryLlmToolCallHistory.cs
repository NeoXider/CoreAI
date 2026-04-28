using System.Collections.Generic;

namespace CoreAI.Messaging
{
    /// <summary>
    /// Thread-safe bounded tool-call history.
    /// </summary>
    public sealed class InMemoryLlmToolCallHistory : ILlmToolCallHistory
    {
        private readonly object _gate = new();
        private readonly int _capacity;
        private readonly Queue<LlmToolCallRecord> _records;

        /// <summary>Creates a bounded history.</summary>
        public InMemoryLlmToolCallHistory(int capacity = 256)
        {
            _capacity = capacity < 1 ? 256 : capacity;
            _records = new Queue<LlmToolCallRecord>(_capacity);
        }

        /// <inheritdoc />
        public void RecordStarted(LlmToolCallStarted evt)
        {
            Add(new LlmToolCallRecord { Info = evt.Info, Status = "started" });
        }

        /// <inheritdoc />
        public void RecordCompleted(LlmToolCallCompleted evt)
        {
            Add(new LlmToolCallRecord
            {
                Info = evt.Info,
                Status = "completed",
                ResultJson = evt.ResultJson,
                DurationMs = evt.DurationMs
            });
        }

        /// <inheritdoc />
        public void RecordFailed(LlmToolCallFailed evt)
        {
            Add(new LlmToolCallRecord
            {
                Info = evt.Info,
                Status = "failed",
                Error = evt.Error,
                DurationMs = evt.DurationMs
            });
        }

        /// <inheritdoc />
        public IReadOnlyList<LlmToolCallRecord> Snapshot()
        {
            lock (_gate)
            {
                return _records.ToArray();
            }
        }

        /// <inheritdoc />
        public void Clear()
        {
            lock (_gate)
            {
                _records.Clear();
            }
        }

        private void Add(LlmToolCallRecord record)
        {
            lock (_gate)
            {
                while (_records.Count >= _capacity)
                {
                    _records.Dequeue();
                }

                _records.Enqueue(record);
            }
        }
    }
}
