using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>
    /// Bounded in-memory trace sink for tests.
    /// </summary>
    public sealed class InMemoryAgentTurnTraceSink : IAgentTurnTraceSink
    {
        private readonly object _gate = new();
        private readonly Queue<AgentTurnTrace> _traces = new();
        private readonly int _capacity;

        /// <summary>Creates a bounded trace sink.</summary>
        public InMemoryAgentTurnTraceSink(int capacity = 128)
        {
            _capacity = capacity < 1 ? 128 : capacity;
        }

        /// <inheritdoc />
        public void Record(AgentTurnTrace trace)
        {
            if (trace == null)
            {
                return;
            }

            lock (_gate)
            {
                while (_traces.Count >= _capacity)
                {
                    _traces.Dequeue();
                }

                _traces.Enqueue(trace);
            }
        }

        /// <summary>Returns a snapshot of recorded traces.</summary>
        public AgentTurnTrace[] Snapshot()
        {
            lock (_gate)
            {
                return _traces.ToArray();
            }
        }
    }
}
