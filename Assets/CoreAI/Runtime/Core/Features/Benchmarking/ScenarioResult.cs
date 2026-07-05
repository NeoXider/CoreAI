using System;
using System.Collections.Generic;

namespace CoreAI.Benchmarking
{
    /// <summary>
    /// Everything recorded for a single scenario run against a single model: the graded
    /// <see cref="Score"/>, the checkpoints that produced it, and the run metrics (turns, tool calls,
    /// real BPE token counts, latency, cost). Pure data — no transport, no Unity.
    /// </summary>
    public sealed class ScenarioResult
    {
        public string ScenarioId { get; set; } = string.Empty;
        public string ScenarioName { get; set; } = string.Empty;

        /// <summary>Benchmark family this scenario belongs to (e.g. "G1", "G2").</summary>
        public string Group { get; set; } = string.Empty;

        public string ModelId { get; set; } = string.Empty;

        public GoalScore Score { get; set; }
        public BenchmarkClassification Classification => Score.Classification;
        public FailureAttribution Attribution { get; set; } = FailureAttribution.None;

        public IReadOnlyList<BenchmarkCheckpoint> Checkpoints { get; set; } = Array.Empty<BenchmarkCheckpoint>();
        public IReadOnlyList<BenchmarkPenalty> Penalties { get; set; } = Array.Empty<BenchmarkPenalty>();

        public int Turns { get; set; }
        public int ToolCalls { get; set; }

        /// <summary>Tool calls that returned an error/failure (subset of <see cref="ToolCalls"/>).</summary>
        public int FailedToolCalls { get; set; }

        /// <summary>World commands that could not be parsed/were malformed (penalized, not counted as built).</summary>
        public int InvalidCommands { get; set; }

        /// <summary>True when token counts came from provider usage; false when estimated via BPE.</summary>
        public bool TokensFromProvider { get; set; }

        /// <summary>Full model session transcript (per-turn prompt/answer/tool-calls), appended to the report.</summary>
        public string SessionTranscript { get; set; } = string.Empty;

        /// <summary>
        /// PNG bytes of a real Unity screenshot of the scene the model built (world scenarios only, when a
        /// graphics device is available). Null otherwise. The host writes it next to the report and embeds it.
        /// </summary>
        public byte[] SceneScreenshotPng { get; set; }

        /// <summary>Canonical, reproducible prompt token count from the real BPE counter (R3).</summary>
        public int PromptTokens { get; set; }

        /// <summary>Canonical, reproducible completion token count from the real BPE counter (R3).</summary>
        public int CompletionTokens { get; set; }

        public int TotalTokens => PromptTokens + CompletionTokens;

        /// <summary>Provider-reported prompt tokens when available (for BPE-vs-provider drift checks).</summary>
        public int? ProviderPromptTokens { get; set; }

        public int? ProviderCompletionTokens { get; set; }

        public double LatencyMs { get; set; }

        /// <summary>Wall-clock spent inside LLM calls only (generation), excluding tool execution and grading.</summary>
        public double GenerationMs { get; set; }

        public double CostUsd { get; set; }
        public bool CostKnown { get; set; }

        public bool TimedOut { get; set; }

        /// <summary>Non-empty when the run could not be graded normally (crash, timeout, env failure).</summary>
        public string Failure { get; set; } = string.Empty;

        /// <summary>1-based repetition index when a scenario is run N times for stability.</summary>
        public int Repetition { get; set; } = 1;

        /// <summary>One-line "what this test checks" description, shown under the scene screenshot.</summary>
        public string WhatItChecks { get; set; } = string.Empty;
    }
}