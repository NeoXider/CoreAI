using System;
using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>
    /// Thread-safe in-memory collector for global, per-actor, and per-role orchestration metrics.
    /// </summary>
    public sealed class InMemoryAiOrchestrationMetrics : IAiOrchestrationMetrics
    {
        /// <summary>Default maximum number of actor rows retained in memory.</summary>
        public const int DefaultMaxActors = 256;

        /// <summary>Default maximum number of role rows retained in memory.</summary>
        public const int DefaultMaxRoles = 256;

        /// <summary>Default maximum number of recent denial records retained for each actor.</summary>
        public const int DefaultMaxDenialReasonsPerActor = 32;

        private readonly object _lock = new();
        private readonly Dictionary<string, ActorMetrics> _perActor = new(StringComparer.Ordinal);
        private readonly Dictionary<string, RoleMetrics> _perRole = new(StringComparer.Ordinal);
        private readonly int _maxActors;
        private readonly int _maxRoles;
        private readonly int _maxDenialReasonsPerActor;
        private DateTime _lastSuccessUtc = DateTime.UtcNow;
        private long _touchOrder;

        /// <summary>Creates a collector with the default bounded-retention limits.</summary>
        public InMemoryAiOrchestrationMetrics()
            : this(DefaultMaxActors, DefaultMaxDenialReasonsPerActor, DefaultMaxRoles)
        {
        }

        /// <summary>Creates a collector with configurable actor, denial-history, and role limits.</summary>
        public InMemoryAiOrchestrationMetrics(
            int maxActors,
            int maxDenialReasonsPerActor,
            int maxRoles = DefaultMaxRoles)
        {
            if (maxActors <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxActors));
            }

            if (maxDenialReasonsPerActor <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxDenialReasonsPerActor));
            }

            if (maxRoles <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxRoles));
            }

            _maxActors = maxActors;
            _maxDenialReasonsPerActor = maxDenialReasonsPerActor;
            _maxRoles = maxRoles;
        }

        /// <summary>Maximum number of actor rows retained in memory.</summary>
        public int MaxActors => _maxActors;

        /// <summary>Maximum number of recent denial records retained for each actor.</summary>
        public int MaxDenialReasonsPerActor => _maxDenialReasonsPerActor;

        /// <summary>Maximum number of role rows retained in memory.</summary>
        public int MaxRoles => _maxRoles;

        /// <summary>Total number of recorded LLM completions.</summary>
        public int TotalCompletions { get; private set; }

        /// <summary>Number of successful LLM completions.</summary>
        public int SuccessfulCompletions { get; private set; }

        /// <summary>Number of LLM completions that failed at the provider boundary.</summary>
        public int ProviderFailures { get; private set; }

        /// <summary>Number of LLM completions that failed at the provider boundary.</summary>
        public int FailedCompletions => ProviderFailures;

        /// <summary>Number of cancelled LLM completions, including replacements and deadlines.</summary>
        public int CancelledCompletions { get; private set; }

        /// <summary>Number of LLM completions superseded by newer work in the same scope.</summary>
        public int ReplacedCompletions { get; private set; }

        /// <summary>Number of LLM completions cancelled by a caller-owned deadline.</summary>
        public int DeadlineCancelledCompletions { get; private set; }

        /// <summary>Number of structured-response retries.</summary>
        public int StructuredRetries { get; private set; }

        /// <summary>Number of AI commands published through orchestration.</summary>
        public int CommandsPublished { get; private set; }

        /// <summary>Number of quota, authorization, backpressure, or other denials.</summary>
        public int TotalDenials { get; private set; }

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

        /// <summary>Records an LLM completion without an actor dimension.</summary>
        public void RecordLlmCompletion(string roleId, string traceId, bool ok, double wallMs)
        {
            RecordLlmCompletion(
                "",
                roleId,
                traceId,
                ok ? AiLlmCompletionOutcome.Succeeded : AiLlmCompletionOutcome.ProviderFailure,
                wallMs);
        }

        /// <summary>Records a typed LLM completion without an actor dimension.</summary>
        public void RecordLlmCompletion(
            string roleId,
            string traceId,
            AiLlmCompletionOutcome outcome,
            double wallMs)
        {
            RecordLlmCompletion("", roleId, traceId, outcome, wallMs);
        }

        /// <summary>Records a boolean LLM completion for compatibility with direct collector callers.</summary>
        public void RecordLlmCompletion(
            string actorId,
            string roleId,
            string traceId,
            bool ok,
            double wallMs)
        {
            RecordLlmCompletion(
                actorId,
                roleId,
                traceId,
                ok ? AiLlmCompletionOutcome.Succeeded : AiLlmCompletionOutcome.ProviderFailure,
                wallMs);
        }

        /// <inheritdoc />
        public void RecordLlmCompletion(
            string actorId,
            string roleId,
            string traceId,
            AiLlmCompletionOutcome outcome,
            double wallMs)
        {
            lock (_lock)
            {
                TotalCompletions++;
                TotalLatencyMs += wallMs;
                switch (outcome)
                {
                    case AiLlmCompletionOutcome.Succeeded:
                        SuccessfulCompletions++;
                        _lastSuccessUtc = DateTime.UtcNow;
                        break;
                    case AiLlmCompletionOutcome.ProviderFailure:
                        ProviderFailures++;
                        break;
                    case AiLlmCompletionOutcome.Replaced:
                        CancelledCompletions++;
                        ReplacedCompletions++;
                        break;
                    case AiLlmCompletionOutcome.DeadlineCancellation:
                        CancelledCompletions++;
                        DeadlineCancelledCompletions++;
                        break;
                    default:
                        CancelledCompletions++;
                        break;
                }

                ActorMetrics actorMetrics = GetOrCreateActor(actorId, roleId);
                actorMetrics?.RecordCompletion(outcome, wallMs);
                GetOrCreateRole(roleId).RecordCompletion(outcome, wallMs);
            }
        }

        /// <summary>Records a structured retry without an actor dimension.</summary>
        public void RecordStructuredRetry(string roleId, string traceId, string reason)
        {
            RecordStructuredRetry("", roleId, traceId, reason);
        }

        /// <inheritdoc />
        public void RecordStructuredRetry(string actorId, string roleId, string traceId, string reason)
        {
            lock (_lock)
            {
                StructuredRetries++;
                ActorMetrics actorMetrics = GetOrCreateActor(actorId, roleId);
                actorMetrics?.RecordStructuredRetry(reason);
                GetOrCreateRole(roleId).RecordStructuredRetry(reason);
            }
        }

        /// <summary>Records a published command without an actor dimension.</summary>
        public void RecordCommandPublished(string roleId, string traceId)
        {
            RecordCommandPublished("", roleId, traceId);
        }

        /// <inheritdoc />
        public void RecordCommandPublished(string actorId, string roleId, string traceId)
        {
            lock (_lock)
            {
                CommandsPublished++;
                ActorMetrics actorMetrics = GetOrCreateActor(actorId, roleId);
                actorMetrics?.RecordCommandPublished();
                GetOrCreateRole(roleId).RecordCommandPublished();
            }
        }

        /// <summary>Records a refusal and retains its reason on the affected actor row.</summary>
        public void RecordDenial(string actorId, string roleId, string traceId, string reason)
        {
            lock (_lock)
            {
                TotalDenials++;
                ActorMetrics actorMetrics = GetOrCreateActor(actorId, roleId);
                actorMetrics?.RecordDenial(traceId, reason);
                GetOrCreateRole(roleId).RecordDenial(reason);
            }
        }

        /// <summary>Records a rejection and retains its reason on the affected actor row.</summary>
        public void RecordRejection(string actorId, string roleId, string traceId, string reason)
        {
            RecordDenial(actorId, roleId, traceId, reason);
        }

        /// <summary>Returns accumulated orchestration metrics for a single actor.</summary>
        public ActorMetrics GetActorMetrics(string actorId)
        {
            lock (_lock)
            {
                return _perActor.TryGetValue(actorId ?? "", out ActorMetrics metrics) ? metrics : null;
            }
        }

        /// <summary>Returns a snapshot of orchestration metrics for every retained actor.</summary>
        public Dictionary<string, ActorMetrics> GetAllActorMetrics()
        {
            lock (_lock)
            {
                return new Dictionary<string, ActorMetrics>(_perActor);
            }
        }

        /// <summary>Returns accumulated orchestration metrics for a single role.</summary>
        public RoleMetrics GetRoleMetrics(string roleId)
        {
            lock (_lock)
            {
                return _perRole.TryGetValue(roleId ?? "", out RoleMetrics metrics) ? metrics : null;
            }
        }

        /// <summary>Returns a snapshot of orchestration metrics for every retained role.</summary>
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
                ProviderFailures = 0;
                CancelledCompletions = 0;
                ReplacedCompletions = 0;
                DeadlineCancelledCompletions = 0;
                StructuredRetries = 0;
                CommandsPublished = 0;
                TotalDenials = 0;
                TotalLatencyMs = 0;
                _lastSuccessUtc = DateTime.UtcNow;
                _touchOrder = 0;
                _perActor.Clear();
                _perRole.Clear();
            }
        }

        private ActorMetrics GetOrCreateActor(string actorId, string roleId)
        {
            string normalizedActorId = actorId ?? "";
            if (normalizedActorId.Length == 0)
            {
                return null;
            }

            if (!_perActor.TryGetValue(normalizedActorId, out ActorMetrics metrics))
            {
                if (_perActor.Count >= _maxActors)
                {
                    EvictLeastRecentlyUsedActor();
                }

                metrics = new ActorMetrics(normalizedActorId, roleId ?? "", _maxDenialReasonsPerActor);
                _perActor[normalizedActorId] = metrics;
            }

            _touchOrder++;
            metrics.Touch(roleId ?? "", _touchOrder);
            return metrics;
        }

        private RoleMetrics GetOrCreateRole(string roleId)
        {
            string normalizedRoleId = roleId ?? "";
            if (!_perRole.TryGetValue(normalizedRoleId, out RoleMetrics metrics))
            {
                if (_perRole.Count >= _maxRoles)
                {
                    EvictLeastRecentlyUsedRole();
                }

                metrics = new RoleMetrics(normalizedRoleId);
                _perRole[normalizedRoleId] = metrics;
            }

            _touchOrder++;
            metrics.Touch(_touchOrder);
            return metrics;
        }

        private void EvictLeastRecentlyUsedActor()
        {
            string evictKey = null;
            long oldestTouch = long.MaxValue;
            foreach (KeyValuePair<string, ActorMetrics> pair in _perActor)
            {
                if (pair.Value.LastTouchOrder < oldestTouch)
                {
                    oldestTouch = pair.Value.LastTouchOrder;
                    evictKey = pair.Key;
                }
            }

            if (evictKey != null)
            {
                _perActor.Remove(evictKey);
            }
        }

        private void EvictLeastRecentlyUsedRole()
        {
            string evictKey = null;
            long oldestTouch = long.MaxValue;
            foreach (KeyValuePair<string, RoleMetrics> pair in _perRole)
            {
                if (pair.Value.LastTouchOrder < oldestTouch)
                {
                    oldestTouch = pair.Value.LastTouchOrder;
                    evictKey = pair.Key;
                }
            }

            if (evictKey != null)
            {
                _perRole.Remove(evictKey);
            }
        }

        /// <summary>Mutable counters recorded for one actor identity.</summary>
        public sealed class ActorMetrics
        {
            private readonly object _denialsLock = new();
            private readonly Queue<DenialRecord> _recentDenials = new();
            private readonly int _maxDenialReasons;

            internal ActorMetrics(string actorId, string roleId, int maxDenialReasons)
            {
                ActorId = actorId;
                RoleId = roleId;
                _maxDenialReasons = maxDenialReasons;
            }

            /// <summary>Stable identity of the actor represented by this row.</summary>
            public string ActorId { get; }

            /// <summary>Most recently observed role for this actor.</summary>
            public string RoleId { get; private set; }

            /// <summary>Number of recorded completions.</summary>
            public int Completions { get; private set; }

            /// <summary>Number of successful completions.</summary>
            public int Successes { get; private set; }

            /// <summary>Number of provider-failed completions.</summary>
            public int Failures { get; private set; }

            /// <summary>Number of cancelled completions, including replacements and deadlines.</summary>
            public int Cancellations { get; private set; }

            /// <summary>Number of completions superseded by newer work in the same scope.</summary>
            public int Replacements { get; private set; }

            /// <summary>Number of completions cancelled by a caller-owned deadline.</summary>
            public int DeadlineCancellations { get; private set; }

            /// <summary>Number of structured-response retries.</summary>
            public int StructuredRetries { get; private set; }

            /// <summary>Most recently recorded structured-response retry reason.</summary>
            public string LastStructuredRetryReason { get; private set; } = "";

            /// <summary>Number of published commands.</summary>
            public int CommandsPublished { get; private set; }

            /// <summary>Number of recorded denials.</summary>
            public int Denials { get; private set; }

            /// <summary>Accumulated completion latency in milliseconds.</summary>
            public double TotalLatencyMs { get; private set; }

            /// <summary>Average completion latency in milliseconds.</summary>
            public double AverageLatencyMs => Completions > 0 ? TotalLatencyMs / Completions : 0;

            /// <summary>Most recently recorded denial reason.</summary>
            public string LastDenialReason
            {
                get
                {
                    lock (_denialsLock)
                    {
                        return _recentDenials.Count > 0 ? GetLastDenial().Reason : "";
                    }
                }
            }

            /// <summary>Bounded snapshot of recent denial records, oldest first.</summary>
            public IReadOnlyList<DenialRecord> RecentDenials
            {
                get
                {
                    lock (_denialsLock)
                    {
                        return _recentDenials.ToArray();
                    }
                }
            }

            internal long LastTouchOrder { get; private set; }

            internal void Touch(string roleId, long touchOrder)
            {
                RoleId = roleId;
                LastTouchOrder = touchOrder;
            }

            internal void RecordCompletion(AiLlmCompletionOutcome outcome, double wallMs)
            {
                Completions++;
                TotalLatencyMs += wallMs;
                switch (outcome)
                {
                    case AiLlmCompletionOutcome.Succeeded:
                        Successes++;
                        break;
                    case AiLlmCompletionOutcome.ProviderFailure:
                        Failures++;
                        break;
                    case AiLlmCompletionOutcome.Replaced:
                        Cancellations++;
                        Replacements++;
                        break;
                    case AiLlmCompletionOutcome.DeadlineCancellation:
                        Cancellations++;
                        DeadlineCancellations++;
                        break;
                    default:
                        Cancellations++;
                        break;
                }
            }

            internal void RecordStructuredRetry(string reason)
            {
                StructuredRetries++;
                LastStructuredRetryReason = reason ?? "";
            }

            internal void RecordCommandPublished()
            {
                CommandsPublished++;
            }

            internal void RecordDenial(string traceId, string reason)
            {
                Denials++;
                lock (_denialsLock)
                {
                    while (_recentDenials.Count >= _maxDenialReasons)
                    {
                        _recentDenials.Dequeue();
                    }

                    _recentDenials.Enqueue(new DenialRecord(traceId, reason));
                }
            }

            private DenialRecord GetLastDenial()
            {
                DenialRecord last = default;
                foreach (DenialRecord denial in _recentDenials)
                {
                    last = denial;
                }

                return last;
            }
        }

        /// <summary>Mutable counters aggregated for one agent role.</summary>
        public sealed class RoleMetrics
        {
            internal RoleMetrics(string roleId)
            {
                RoleId = roleId;
            }

            /// <summary>Role represented by this aggregate row.</summary>
            public string RoleId { get; }

            /// <summary>Number of recorded completions.</summary>
            public int Completions { get; private set; }

            /// <summary>Number of successful completions.</summary>
            public int Successes { get; private set; }

            /// <summary>Number of provider-failed completions.</summary>
            public int Failures { get; private set; }

            /// <summary>Number of cancelled completions, including replacements and deadlines.</summary>
            public int Cancellations { get; private set; }

            /// <summary>Number of completions superseded by newer work in the same scope.</summary>
            public int Replacements { get; private set; }

            /// <summary>Number of completions cancelled by a caller-owned deadline.</summary>
            public int DeadlineCancellations { get; private set; }

            /// <summary>Number of structured-response retries.</summary>
            public int StructuredRetries { get; private set; }

            /// <summary>Most recently recorded structured-response retry reason.</summary>
            public string LastStructuredRetryReason { get; private set; } = "";

            /// <summary>Number of published commands.</summary>
            public int CommandsPublished { get; private set; }

            /// <summary>Number of recorded denials.</summary>
            public int Denials { get; private set; }

            /// <summary>Most recently recorded denial reason.</summary>
            public string LastDenialReason { get; private set; } = "";

            /// <summary>Accumulated completion latency in milliseconds.</summary>
            public double TotalLatencyMs { get; private set; }

            /// <summary>Average completion latency in milliseconds.</summary>
            public double AverageLatencyMs => Completions > 0 ? TotalLatencyMs / Completions : 0;

            internal long LastTouchOrder { get; private set; }

            internal void Touch(long touchOrder)
            {
                LastTouchOrder = touchOrder;
            }

            internal void RecordCompletion(AiLlmCompletionOutcome outcome, double wallMs)
            {
                Completions++;
                TotalLatencyMs += wallMs;
                switch (outcome)
                {
                    case AiLlmCompletionOutcome.Succeeded:
                        Successes++;
                        break;
                    case AiLlmCompletionOutcome.ProviderFailure:
                        Failures++;
                        break;
                    case AiLlmCompletionOutcome.Replaced:
                        Cancellations++;
                        Replacements++;
                        break;
                    case AiLlmCompletionOutcome.DeadlineCancellation:
                        Cancellations++;
                        DeadlineCancellations++;
                        break;
                    default:
                        Cancellations++;
                        break;
                }
            }

            internal void RecordStructuredRetry(string reason)
            {
                StructuredRetries++;
                LastStructuredRetryReason = reason ?? "";
            }

            internal void RecordCommandPublished()
            {
                CommandsPublished++;
            }

            internal void RecordDenial(string reason)
            {
                Denials++;
                LastDenialReason = reason ?? "";
            }
        }

        /// <summary>One retained actor denial with its trace and reason.</summary>
        public readonly struct DenialRecord
        {
            internal DenialRecord(string traceId, string reason)
            {
                TraceId = traceId ?? "";
                Reason = reason ?? "";
            }

            /// <summary>Trace associated with the refused operation.</summary>
            public string TraceId { get; }

            /// <summary>Recoverable quota, authorization, backpressure, or other refusal reason.</summary>
            public string Reason { get; }
        }
    }
}
