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

        /// <summary>Default cap on distinct role IDs retained in <see cref="_latestByRole"/>.</summary>
        public const int DefaultMaxRoles = 32;

        private readonly int _maxRoles;

        /// <summary>Creates a bounded trace sink.</summary>
        public InMemoryAgentTurnTraceSink(int capacity = 128, int maxRoles = DefaultMaxRoles)
        {
            _capacity = capacity < 1 ? 128 : capacity;
            _maxRoles = maxRoles < 1 ? DefaultMaxRoles : maxRoles;
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
                if (!_latestByRole.ContainsKey(roleId) && _latestByRole.Count >= _maxRoles)
                {
                    // Dynamic role IDs (e.g. per-session or per-mod agents) are otherwise unbounded, so
                    // this dictionary would grow for the lifetime of the process. Which specific role is
                    // dropped does not matter - only that cardinality stays capped - so evict any single
                    // entry rather than tracking access order for a true LRU.
                    Dictionary<string, AgentTurnTrace>.Enumerator enumerator = _latestByRole.GetEnumerator();
                    if (enumerator.MoveNext())
                    {
                        _latestByRole.Remove(enumerator.Current.Key);
                    }
                }

                _latestByRole[roleId] = trace;
            }
        }

        /// <summary>Clears all recorded traces and the latest-by-role snapshot.</summary>
        public void Clear()
        {
            lock (_gate)
            {
                _traces.Clear();
                _latestByRole.Clear();
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