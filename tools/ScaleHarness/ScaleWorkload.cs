using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoreAI.Tools.Scale
{
    /// <summary>Frozen per-actor workload and staircase definition loaded from scale.workload.json.</summary>
    public sealed class ScaleWorkload
    {
        public string SchemaVersion { get; set; } = "";

        public string FrozenUtc { get; set; } = "";

        public string Rationale { get; set; } = "";

        public List<int> Staircase { get; set; } = new List<int>();

        public int Repeats { get; set; }

        public int FrameRate { get; set; }

        public int WarmupFrames { get; set; }

        public int MeasurementFrames { get; set; }

        public int DrainFramesMax { get; set; }

        public string SnapshotEventName { get; set; } = "";

        public ScalePerActorWorkload PerActor { get; set; } = new ScalePerActorWorkload();

        public ScaleChatWorkload Chat { get; set; } = new ScaleChatWorkload();

        public ScaleNetworkWorkload Network { get; set; } = new ScaleNetworkWorkload();

        public ScaleBudgets Budgets { get; set; } = new ScaleBudgets();

        public List<string> NonZeroCounters { get; set; } = new List<string>();

        /// <summary>SHA-256 of the exact JSON bytes the run used; filled by the loader.</summary>
        [JsonIgnore]
        public string Sha256 { get; set; } = "";

        [JsonIgnore]
        public string SourcePath { get; set; } = "";

        public double FrameSeconds => 1d / FrameRate;

        public static ScaleWorkload Load(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            ScaleWorkload workload = JsonSerializer.Deserialize<ScaleWorkload>(bytes, CreateJsonOptions());
            if (workload == null)
            {
                throw new InvalidOperationException("scale.workload.json did not deserialize.");
            }

            using SHA256 sha = SHA256.Create();
            workload.Sha256 = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            workload.SourcePath = Path.GetFullPath(path);
            return workload;
        }

        public static JsonSerializerOptions CreateJsonOptions()
        {
            JsonSerializerOptions options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
            };
            options.Converters.Add(new JsonStringEnumConverter());
            return options;
        }

        public IReadOnlyList<string> Validate()
        {
            List<string> errors = new List<string>();
            if (Staircase == null || Staircase.Count == 0)
            {
                errors.Add("staircase must list at least one actor count");
            }
            else
            {
                foreach (int step in Staircase)
                {
                    if (step < 1)
                    {
                        errors.Add("staircase steps must be positive");
                    }
                }
            }

            if (Repeats < 1)
            {
                errors.Add("repeats must be at least 1");
            }

            if (FrameRate < 1)
            {
                errors.Add("frameRate must be positive");
            }

            if (WarmupFrames < 0 || MeasurementFrames < 1)
            {
                errors.Add("warmupFrames must be non-negative and measurementFrames positive");
            }

            if (DrainFramesMax < 0)
            {
                errors.Add("drainFramesMax must be non-negative");
            }

            if (string.IsNullOrWhiteSpace(SnapshotEventName))
            {
                errors.Add("snapshotEventName is required");
            }

            if (PerActor == null)
            {
                errors.Add("perActor is required");
            }
            else
            {
                if (PerActor.HeartbeatLoopIterations < 1)
                {
                    errors.Add("perActor.heartbeatLoopIterations must be positive");
                }

                if (PerActor.RemoteFireEveryFrames < 1 || PerActor.PartSpawnEveryFrames < 1)
                {
                    errors.Add("perActor cadences must be positive");
                }

                if (PerActor.PersistentWaitSeconds <= 0d)
                {
                    errors.Add("perActor.persistentWaitSeconds must be positive");
                }
            }

            if (Chat == null)
            {
                errors.Add("chat is required");
            }
            else
            {
                if (Chat.OrchestratorMaxConcurrent < 1 || Chat.OrchestratorMaxPending < 1)
                {
                    errors.Add("chat orchestrator limits must be positive");
                }

                if (Chat.StubLatencyMilliseconds < 0)
                {
                    errors.Add("chat.stubLatencyMilliseconds cannot be negative");
                }

                if (Chat.StaggeredWindowStartFrame < 0
                    || Chat.StaggeredWindowEndFrame <= Chat.StaggeredWindowStartFrame
                    || Chat.StaggeredWindowEndFrame > MeasurementFrames)
                {
                    errors.Add("chat staggered window must lie inside the measurement window");
                }

                if (Chat.SynchronizedBurstAtFrame < 0 || Chat.SynchronizedBurstAtFrame >= MeasurementFrames)
                {
                    errors.Add("chat.synchronizedBurstAtFrame must lie inside the measurement window");
                }
            }

            if (Network == null || Network.LoopbackMaxClientRequestsPerSecond < 1)
            {
                errors.Add("network.loopbackMaxClientRequestsPerSecond must be positive");
            }

            if (Budgets == null || Budgets.FrameMilliseconds == null || Budgets.FrameMilliseconds.Count == 0)
            {
                errors.Add("budgets.frameMilliseconds must list at least one budget");
            }

            return errors;
        }
    }

    /// <summary>Per-actor Lua workload knobs substituted into scale_actor.lua.</summary>
    public sealed class ScalePerActorWorkload
    {
        public int ModsPerActor { get; set; } = 1;

        public int HeartbeatLoopIterations { get; set; }

        public int PersistentWaitLoops { get; set; } = 1;

        public double PersistentWaitSeconds { get; set; }

        public int RemoteFireEveryFrames { get; set; }

        public int PartSpawnEveryFrames { get; set; }

        public bool PhaseOffsetByActorIndex { get; set; } = true;
    }

    /// <summary>Chat arrival patterns and the production orchestrator configuration under test.</summary>
    public sealed class ScaleChatWorkload
    {
        public int OrchestratorMaxConcurrent { get; set; }

        public int OrchestratorMaxPending { get; set; }

        public int StubLatencyMilliseconds { get; set; }

        public int SynchronizedBurstAtFrame { get; set; }

        public int StaggeredWindowStartFrame { get; set; }

        public int StaggeredWindowEndFrame { get; set; }

        public double P95EndToEndBudgetMilliseconds { get; set; } = 5000d;
    }

    /// <summary>Loopback bridge configuration.</summary>
    public sealed class ScaleNetworkWorkload
    {
        public int LoopbackMaxClientRequestsPerSecond { get; set; }
    }

    /// <summary>Pre-frozen pass/fail budgets.</summary>
    public sealed class ScaleBudgets
    {
        public List<double> FrameMilliseconds { get; set; } = new List<double>();

        public string PassRule { get; set; } = "";

        public double HeapSlopeMegabytesPerMinuteMax { get; set; } = 1d;

        public double FairnessMaxMinRatioMax { get; set; } = 2d;
    }
}
