using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>
    /// Bounded in-memory trace sink. Retains the recent ring of traces and the latest
    /// trace per role so live diagnostics can read the most recent turn without persisting.
    /// </summary>
    public sealed class InMemoryAgentTurnTraceSink : IAgentTurnTraceSink, IAgentTurnTraceReader
    {
        private readonly object _gate = new();
        private readonly Queue<AgentTurnTrace> _traces = new();

        private readonly Dictionary<string, AgentTurnTrace> _latestByRole =
            new(System.StringComparer.Ordinal);

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

                string roleId = trace.RoleId ?? "";
                _latestByRole[roleId] = trace;
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

        /// <inheritdoc />
        public bool TryGetLatestTrace(string roleId, out AgentTurnTrace trace)
        {
            string key = roleId ?? "";
            lock (_gate)
            {
                return _latestByRole.TryGetValue(key, out trace);
            }
        }
    }
}