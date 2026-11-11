using System;
using System.Collections.Generic;
using System.Globalization;
using CoreAI.Ai;
using CoreAI.Logging;

namespace CoreAI.Decisions
{
    /// <summary>Who decided.</summary>
    public enum DecisionSource
    {
        /// <summary>The decision model's answer was accepted.</summary>
        Model = 0,

        /// <summary>The deterministic fallback decided (low confidence, refusal or failure).</summary>
        Fallback = 1
    }

    /// <summary>Named reasons a policy fell back.</summary>
    public static class DecisionFallbackReasons
    {
        /// <summary>The answer's confidence was below the threshold.</summary>
        public const string LowConfidence = "low_confidence";

        /// <summary>The call failed; the trace carries its error code and reason.</summary>
        public const string CallFailed = "call_failed";

        /// <summary>The backend declined the question.</summary>
        public const string Refused = "refused";

        /// <summary>The result had no answer for the policy's question.</summary>
        public const string NoAnswer = "no_answer";

        /// <summary>The model chose an id outside the action set.</summary>
        public const string UnknownAction = "unknown_action";
    }

    /// <summary>One decision a policy acted on, for logs, the Hub and tests.</summary>
    public sealed class DecisionTrace
    {
        /// <summary>Consumer that decided (for example <c>npc-brain</c>).</summary>
        public string ConsumerId { get; set; } = "";

        /// <summary>The NPC or player the decision is for.</summary>
        public string SubjectId { get; set; } = "";

        /// <summary>Chosen action or goal id.</summary>
        public string OptionId { get; set; } = "";

        /// <summary>Who decided.</summary>
        public DecisionSource Source { get; set; }

        /// <summary>For a fallback, the reason from <see cref="DecisionFallbackReasons"/>; empty for a model decision.</summary>
        public string FallbackReason { get; set; } = "";

        /// <summary>Confidence of the model answer (0 when there was none).</summary>
        public double Confidence { get; set; }

        /// <summary>Whether the model answer was calibrated.</summary>
        public bool Calibrated { get; set; }

        /// <summary>Error of a failed call.</summary>
        public LlmErrorCode ErrorCode { get; set; }

        /// <summary>Named error reason of a failed call.</summary>
        public string ErrorReason { get; set; } = "";

        /// <summary>Clock time of the decision in seconds.</summary>
        public double AtSeconds { get; set; }

        /// <summary>One-line English description.</summary>
        public override string ToString()
        {
            string text = $"[{ConsumerId}] {SubjectId} -> {OptionId} ({Source}";
            if (Source == DecisionSource.Fallback)
            {
                text += ": " + FallbackReason;
                if (ErrorCode != LlmErrorCode.None)
                {
                    text += ", " + ErrorCode + (ErrorReason.Length > 0 ? "/" + ErrorReason : "");
                }
            }

            return text + ", confidence " + Confidence.ToString("0.00", CultureInfo.InvariantCulture) + ")";
        }
    }

    /// <summary>Receives every decision a policy acts on.</summary>
    public interface IDecisionTraceSink
    {
        /// <summary>Records one decision.</summary>
        void Record(DecisionTrace trace);
    }

    /// <summary>Discards traces.</summary>
    public sealed class NullDecisionTraceSink : IDecisionTraceSink
    {
        /// <inheritdoc />
        public void Record(DecisionTrace trace)
        {
        }
    }

    /// <summary>Keeps the most recent traces in memory (bounded), for the Hub and tests.</summary>
    public sealed class InMemoryDecisionTraceSink : IDecisionTraceSink
    {
        /// <summary>Default capacity.</summary>
        public const int DefaultCapacity = 256;

        private readonly object _gate = new();
        private readonly Queue<DecisionTrace> _traces = new();
        private readonly int _capacity;
        private int _total;
        private int _fallbacks;

        /// <summary>Creates a sink keeping at most <paramref name="capacity"/> traces.</summary>
        public InMemoryDecisionTraceSink(int capacity = DefaultCapacity)
        {
            _capacity = Math.Max(1, capacity);
        }

        /// <summary>Traces recorded since creation.</summary>
        public int TotalCount
        {
            get
            {
                lock (_gate)
                {
                    return _total;
                }
            }
        }

        /// <summary>Fallback decisions recorded since creation.</summary>
        public int FallbackCount
        {
            get
            {
                lock (_gate)
                {
                    return _fallbacks;
                }
            }
        }

        /// <inheritdoc />
        public void Record(DecisionTrace trace)
        {
            if (trace == null)
            {
                return;
            }

            lock (_gate)
            {
                _total++;
                if (trace.Source == DecisionSource.Fallback)
                {
                    _fallbacks++;
                }

                _traces.Enqueue(trace);
                while (_traces.Count > _capacity)
                {
                    _traces.Dequeue();
                }
            }
        }

        /// <summary>The kept traces, oldest first.</summary>
        public IReadOnlyList<DecisionTrace> Snapshot()
        {
            lock (_gate)
            {
                return _traces.ToArray();
            }
        }
    }

    /// <summary>Writes traces to an <see cref="ILog"/>: model decisions at debug level, fallbacks at info level.</summary>
    public sealed class LogDecisionTraceSink : IDecisionTraceSink
    {
        /// <summary>Log tag of decision traces.</summary>
        public const string Tag = "Decisions";

        private readonly ILog _log;

        /// <summary>Creates a sink over <paramref name="log"/>.</summary>
        public LogDecisionTraceSink(ILog log)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        /// <inheritdoc />
        public void Record(DecisionTrace trace)
        {
            if (trace == null)
            {
                return;
            }

            if (trace.Source == DecisionSource.Fallback)
            {
                _log.Info("Decision fallback: " + trace, Tag);
            }
            else
            {
                _log.Debug("Decision: " + trace, Tag);
            }
        }
    }
}
