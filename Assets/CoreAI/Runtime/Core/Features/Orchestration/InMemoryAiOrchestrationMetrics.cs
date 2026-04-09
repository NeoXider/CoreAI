using System;
using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>
    /// In-memory реализация <see cref="IAiOrchestrationMetrics"/>: хранит счётчики и историю
    /// для отображения в dashboard / экспорта в StatsD / Prometheus / Application Insights.
    /// </summary>
    public sealed class InMemoryAiOrchestrationMetrics : IAiOrchestrationMetrics
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, RoleMetrics> _perRole = new(StringComparer.Ordinal);
        private DateTime _lastSuccessUtc = DateTime.UtcNow;

        /// <summary>Общее число completion запросов.</summary>
        public int TotalCompletions { get; private set; }

        /// <summary>Число успешных completion запросов.</summary>
        public int SuccessfulCompletions { get; private set; }

        /// <summary>Число неудачных completion запросов.</summary>
        public int FailedCompletions { get; private set; }

        /// <summary>Число retry из-за structured response policy.</summary>
        public int StructuredRetries { get; private set; }

        /// <summary>Число опубликованных команд.</summary>
        public int CommandsPublished { get; private set; }

        /// <summary>Суммарная задержка LLM в миллисекундах.</summary>
        public double TotalLatencyMs { get; private set; }

        /// <summary>Средняя задержка LLM (0 если нет данных).</summary>
        public double AverageLatencyMs => TotalCompletions > 0 ? TotalLatencyMs / TotalCompletions : 0;

        /// <summary>Время последней успешной операции (UTC).</summary>
        public DateTime LastSuccessUtc
        {
            get
            {
                lock (_lock) return _lastSuccessUtc;
            }
        }

        /// <summary>Секунд с последней успешной операции.</summary>
        public double SecondsSinceLastSuccess
        {
            get
            {
                lock (_lock) return (DateTime.UtcNow - _lastSuccessUtc).TotalSeconds;
            }
        }

        /// <summary>true если LLM не отвечал дольше указанного порога.</summary>
        public bool IsLlmUnresponsive(double thresholdSeconds = 300)
            => SecondsSinceLastSuccess > thresholdSeconds;

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

        /// <summary>Получить метрики для конкретной роли (null если роль не использовалась).</summary>
        public RoleMetrics GetRoleMetrics(string roleId)
        {
            lock (_lock)
            {
                return _perRole.TryGetValue(roleId ?? "", out RoleMetrics rm) ? rm : null;
            }
        }

        /// <summary>Получить все роли с метриками.</summary>
        public Dictionary<string, RoleMetrics> GetAllRoleMetrics()
        {
            lock (_lock)
            {
                return new Dictionary<string, RoleMetrics>(_perRole);
            }
        }

        /// <summary>Сбросить все счётчики.</summary>
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
                rm = new RoleMetrics(roleId);
                _perRole[roleId] = rm;
            }

            return rm;
        }

        /// <summary>Метрики для отдельной роли.</summary>
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
                if (ok) Successes++;
                else Failures++;
            }
        }
    }
}
