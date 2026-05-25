using System;
using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>
    /// Thread-safe in-memory collector for AI orchestration counters and per-role metrics.
    /// </summary>
    public sealed class InMemoryAiOrchestrationMetrics : IAiOrchestrationMetrics
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, RoleMetrics> _perRole = new(StringComparer.Ordinal);
        private DateTime _lastSuccessUtc = DateTime.UtcNow;

        // ARCH-9: cap per-role dictionary to prevent unbounded growth from dynamic roleIds.
        private const int MaxRoles = 256;

        /// <summary>Total number of recorded LLM completions.</summary>
        public int TotalCompletions { get; private set; }

        /// <summary>Number of successful LLM completions.</summary>
        public int SuccessfulCompletions { get; private set; }

        /// <summary>Number of failed LLM completions.</summary>
        public int FailedCompletions { get; private set; }

        /// <summary>Number of structured-response retries.</summary>
        public int StructuredRetries { get; private set; }

        /// <summary>Number of AI commands published through orchestration.</summary>
        public int CommandsPublished { get; private set; }

        /// <summary>Accumulated LLM completion latency in milliseconds.</summary>
        public double TotalLatencyMs { get; private set; }

        /// <summary>Average LLM completion latency in milliseconds.</summary>
        public double AverageLatencyMs => TotalCompletions > 0 ? TotalLatencyMs / TotalCompletions : 0;

        /// <summary>UTC timestamp of the most recent successful LLM completion.</summary>
        public DateTime LastSuccessUtc
        {
            get
            {
                lock (_lock)
                {
                    return _lastSuccessUtc;
                }
            }
        }

        /// <summary>Elapsed seconds since the most recent successful LLM completion.</summary>
        public double SecondsSinceLastSuccess
        {
            get
            {
                lock (_lock)
                {
                    return (DateTime.UtcNow - _lastSuccessUtc).TotalSeconds;
                }
            }
        }

        /// <summary>Returns whether no successful LLM completion has occurred within the threshold.</summary>
        public bool IsLlmUnresponsive(double thresholdSeconds = 300)
        {
            return SecondsSinceLastSuccess > thresholdSeconds;
        }

        /// <inheritdoc />
        public void RecordLlmCompletion(string roleId, string traceId, bool ok, double wallMs)
        {
            lock (_lock)
            {
                TotalCompletions++;
                TotalLatencyMs += wallMs;
                if (ok)
                {
                    SuccessfulCompletions++;
                    _lastSuccessUtc = DateTime.UtcNow;
                }
                else
                {
                    FailedCompletions++;
                }

                GetOrCreate(roleId).RecordCompletion(ok, wallMs);
            }
        }

        /// <inheritdoc />
        public void RecordStructuredRetry(string roleId, string traceId, string reason)
        {
            lock (_lock)
            {
                StructuredRetries++;
                GetOrCreate(roleId).StructuredRetries++;
            }
        }

        /// <inheritdoc />
        public void RecordCommandPublished(string roleId, string traceId)
        {
            lock (_lock)
            {
                CommandsPublished++;
                GetOrCreate(roleId).CommandsPublished++;
            }
        }

        /// <summary>Returns accumulated orchestration metrics for a single role.</summary>
        public RoleMetrics GetRoleMetrics(string roleId)
        {
            lock (_lock)
            {
                return _perRole.TryGetValue(roleId ?? "", out RoleMetrics rm) ? rm : null;
            }
        }

        /// <summary>Returns a snapshot of orchestration metrics for every recorded role.</summary>
        public Dictionary<string, RoleMetrics> GetAllRoleMetrics()
        {
            lock (_lock)
            {
                return new Dictionary<string, RoleMetrics>(_perRole);
            }
        }

        /// <summary>Clears all accumulated metrics and resets the success timestamp.</summary>
        public void Reset()
        {
            lock (_lock)
            {
                TotalCompletions = 0;
                SuccessfulCompletions = 0;
                FailedCompletions = 0;
                StructuredRetries = 0;
                CommandsPublished = 0;
                TotalLatencyMs = 0;
                _lastSuccessUtc = DateTime.UtcNow;
                _perRole.Clear();
            }
        }

        private RoleMetrics GetOrCreate(string roleId)
        {
            roleId ??= "";
            if (!_perRole.TryGetValue(roleId, out RoleMetrics rm))
            {
                // ARCH-9: evict the least-used entry when cap is reached.
                if (_perRole.Count >= MaxRoles)
                {
                    string evictKey = null;
                    int minCompletions = int.MaxValue;
                    foreach (KeyValuePair<string, RoleMetrics> kvp in _perRole)
                    {
                        if (kvp.Value.Completions < minCompletions)
                        {
                            minCompletions = kvp.Value.Completions;
                            evictKey = kvp.Key;
                        }
                    }

                    if (evictKey != null)
                    {
                        _perRole.Remove(evictKey);
                    }
                }

                rm = new RoleMetrics(roleId);
                _perRole[roleId] = rm;
            }

            return rm;
        }

        /// <summary>Mutable counters recorded for one agent role.</summary>
        public sealed class RoleMetrics
        {
            public string RoleId { get; }
            public int Completions { get; private set; }
            public int Successes { get; private set; }
            public int Failures { get; private set; }
            public int StructuredRetries { get; internal set; }
            public int CommandsPublished { get; internal set; }
            public double TotalLatencyMs { get; private set; }
            public double AverageLatencyMs => Completions > 0 ? TotalLatencyMs / Completions : 0;

            internal RoleMetrics(string roleId)
            {
                RoleId = roleId;
            }

            internal void RecordCompletion(bool ok, double wallMs)
            {
                Completions++;
                TotalLatencyMs += wallMs;
                if (ok)
                {
                    Successes++;
                }
                else
                {
                    Failures++;
                }
            }
        }
    }
}
