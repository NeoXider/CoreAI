using System;
using System.Collections.Generic;

namespace CoreAI.Diagnostics.G10
{
    /// <summary>Provider modes required by the MVP2 G10 acceptance manifest.</summary>
    public enum G10ProviderMode
    {
        ScriptedStub,
        RealProvider
    }

    /// <summary>Chat arrival patterns required by the MVP2 G10 acceptance manifest.</summary>
    public enum G10ArrivalPattern
    {
        Staggered,
        SynchronizedBurst
    }

    /// <summary>A report value that distinguishes a measurement from missing evidence.</summary>
    public sealed class G10MeasurementValue<T>
    {
        public string Status { get; set; } = "not_measured";

        public T Value { get; set; }

        public string Reason { get; set; } = "";

        /// <summary>Creates a measured report value.</summary>
        public static G10MeasurementValue<T> Measured(T value)
        {
            return new G10MeasurementValue<T> { Status = "measured", Value = value };
        }

        /// <summary>Creates an explicitly unmeasured report value.</summary>
        public static G10MeasurementValue<T> NotMeasured(string reason)
        {
            return new G10MeasurementValue<T> { Status = "not_measured", Reason = reason ?? "" };
        }
    }

    /// <summary>Provider configuration supplied to the G10 production composition.</summary>
    public sealed class G10ProviderConfiguration
    {
        public G10ProviderMode ProviderMode { get; set; }

        public string Endpoint { get; set; } = "";

        public string ApiKey { get; set; } = "";

        public string ModelId { get; set; } = "";

        public int? ContextCapTokens { get; set; }

        public int? OutputCapTokens { get; set; }

        public int? BackendConcurrency { get; set; }

        public int? OrchestratorConcurrency { get; set; }

        public int? RequestTimeoutSeconds { get; set; }

        public int? StubLatencyMilliseconds { get; set; }

        public float Temperature { get; set; }

        public string ExtraBodyJson { get; set; } = "";

        /// <summary>Returns configuration errors without substituting unmeasured provider values.</summary>
        public IReadOnlyList<string> Validate()
        {
            List<string> errors = new List<string>();
            if (!OrchestratorConcurrency.HasValue || OrchestratorConcurrency.Value < 1)
            {
                errors.Add("orchestratorConcurrency must be supplied and greater than zero");
            }

            if (ProviderMode == G10ProviderMode.ScriptedStub)
            {
                if (!StubLatencyMilliseconds.HasValue || StubLatencyMilliseconds.Value < 0)
                {
                    errors.Add("stubLatencyMilliseconds must be supplied and non-negative");
                }

                return errors;
            }

            if (string.IsNullOrWhiteSpace(Endpoint))
            {
                errors.Add("endpoint is not measured/configured");
            }

            if (string.IsNullOrWhiteSpace(ModelId))
            {
                errors.Add("modelId is not measured/configured");
            }

            if (!ContextCapTokens.HasValue || ContextCapTokens.Value < 256)
            {
                errors.Add("contextCapTokens is not measured/configured");
            }

            if (!OutputCapTokens.HasValue || OutputCapTokens.Value < 1)
            {
                errors.Add("outputCapTokens is not measured/configured");
            }

            if (!BackendConcurrency.HasValue || BackendConcurrency.Value < 1)
            {
                errors.Add("backendConcurrency is not measured/configured");
            }

            if (!RequestTimeoutSeconds.HasValue || RequestTimeoutSeconds.Value < 1)
            {
                errors.Add("requestTimeoutSeconds must be supplied and greater than zero");
            }

            return errors;
        }
    }

    /// <summary>Fixed workload plus provider configuration for one G10 harness invocation.</summary>
    public sealed class G10MeasurementConfiguration
    {
        public const int ManifestActorCount = 20;
        public const int ManifestModsPerActor = 1;
        public const int ManifestDeferredThreadsPerActor = 10;
        public const int ManifestDelayedThreadsPerActor = 10;
        public const int ManifestDelayFrameCount = 10;
        public const int ManifestRandomSeed = 20260831;
        public const int ManifestFrameRate = 60;
        public const int ManifestWarmupSeconds = 30;
        public const int ManifestMeasurementSeconds = 60;
        public const int ManifestChatCadenceSeconds = 30;
        public const int ManifestTimerCadenceMilliseconds = 500;
        public const int ManifestEventCadenceMilliseconds = 1000;
        public const int ManifestSubscribersPerEmit = 1;
        public const int ManifestNonSubscribersPerEmit = 19;
        public const int ManifestGuardedInstructionsPerFrame = 589;

        public G10ProviderConfiguration Provider { get; set; } = new G10ProviderConfiguration();

        public double WarmupSeconds { get; set; } = ManifestWarmupSeconds;

        public double MeasurementSeconds { get; set; } = ManifestMeasurementSeconds;

        public int FrameRate { get; set; } = ManifestFrameRate;

        public int? DiscoveredTestCount { get; set; }

        public int? SkippedTestCount { get; set; }

        public string DiscoveryEvidenceSource { get; set; } = "";

        public bool IsManifestWorkload =>
            Math.Abs(WarmupSeconds - ManifestWarmupSeconds) < 0.0001d &&
            Math.Abs(MeasurementSeconds - ManifestMeasurementSeconds) < 0.0001d &&
            FrameRate == ManifestFrameRate;

        /// <summary>Returns configuration errors without running a partial gate.</summary>
        public IReadOnlyList<string> Validate()
        {
            List<string> errors = new List<string>();
            if (Provider == null)
            {
                errors.Add("provider configuration is required");
            }
            else
            {
                errors.AddRange(Provider.Validate());
            }

            if (WarmupSeconds < 0d)
            {
                errors.Add("warmupSeconds cannot be negative");
            }

            if (MeasurementSeconds <= 0d)
            {
                errors.Add("measurementSeconds must be greater than zero");
            }

            if (FrameRate < 1)
            {
                errors.Add("frameRate must be greater than zero");
            }

            if (SkippedTestCount.HasValue && SkippedTestCount.Value < 0)
            {
                errors.Add("skippedTestCount cannot be negative");
            }

            return errors;
        }
    }

    /// <summary>One actor's measured chat service and starvation outcome.</summary>
    public sealed class G10ActorServiceReport
    {
        public string ActorId { get; set; } = "";

        public int Offered { get; set; }

        public int Served { get; set; }

        public G10MeasurementValue<double?> MaximumServiceWaitSeconds { get; set; }

        public bool StarvedBeyondSixtySeconds { get; set; }
    }

    /// <summary>Measured chat result for one required arrival pattern.</summary>
    public sealed class G10PatternReport
    {
        public string Pattern { get; set; } = "";

        public int Offered { get; set; }

        public int Served { get; set; }

        public double ServedFraction { get; set; }

        public G10MeasurementValue<double?> P95EndToEndLatencyMilliseconds { get; set; }

        public G10MeasurementValue<double?> P95QueueLatencyMilliseconds { get; set; }

        public G10MeasurementValue<double?> P95ProviderLatencyMilliseconds { get; set; }

        public double MaximumArrivalSkewMilliseconds { get; set; }

        public int CrossActorCancellations { get; set; }

        public int SameActorCancellations { get; set; }

        public int HarnessDeadlineCancellations { get; set; }

        public int ProviderFailures { get; set; }

        public int AdmissionFailures { get; set; }

        public bool ServedFractionAtLeastNinetyFivePercent { get; set; }

        public bool P95AtMostFiveSeconds { get; set; }

        public bool NoActorStarvedBeyondSixtySeconds { get; set; }

        public bool NoCrossActorCancellations { get; set; }

        public List<G10ActorServiceReport> Actors { get; set; } = new List<G10ActorServiceReport>();
    }

    /// <summary>Lua workload measurements and manifest counter evidence.</summary>
    public sealed class G10WorldWorkloadReport
    {
        public long GuardedInstructionSteps { get; set; }

        public long MeasurementGuardedInstructionSteps { get; set; }

        public long ThreadResumes { get; set; }

        public long EventsDelivered { get; set; }

        public long MeasurementEventsDelivered { get; set; }

        public long CompletedLuaOperations { get; set; }

        public long MeasurementCompletedLuaOperations { get; set; }

        public double MeanGuardedStepsPerMeasurementOperation { get; set; }

        public long CalibratedLuaBodyGuardedInstructions { get; set; }

        public Dictionary<long, int> MeasurementGuardedStepHistogram { get; set; } =
            new Dictionary<long, int>();

        public bool MeanGuardedStepsWithinManifestFrameBudget { get; set; }

        public int DeferredCallbacksCompleted { get; set; }

        public int DelayedCallbacksCompleted { get; set; }

        public int TimerCallbacksDuringMeasurement { get; set; }

        public int SubscriberInvocationsDuringMeasurement { get; set; }

        public int NonSubscriberInvocationsDuringMeasurement { get; set; }

        public int IndependentNonSubscriberChecksDuringMeasurement { get; set; }

        public string SubscriberIsolationEvidenceSource { get; set; } = "";

        public bool EveryActorHasOneTimer { get; set; }

        public bool EveryActorCompletedTenDeferredThreads { get; set; }

        public bool EveryActorCompletedTenDelayedThreads { get; set; }

        public bool EveryEmitInvokedExactlyOneSubscriber { get; set; }

        public Dictionary<int, int> DelayedDeadlineFrameHistogram { get; set; } = new Dictionary<int, int>();
    }

    /// <summary>Provider configuration and response evidence captured by one harness invocation.</summary>
    public sealed class G10ProviderReport
    {
        public string Mode { get; set; } = "";

        public G10MeasurementValue<string> ModelId { get; set; }

        public G10MeasurementValue<int?> ContextCapTokens { get; set; }

        public G10MeasurementValue<int?> OutputCapTokens { get; set; }

        public G10MeasurementValue<int?> BackendConcurrency { get; set; }

        public G10MeasurementValue<int?> ScriptedLatencyMilliseconds { get; set; }

        public G10MeasurementValue<int?> ChatResponsesActuallyProducedByProvider { get; set; }

        public int ScriptedStubResponses { get; set; }
    }

    /// <summary>Final G10 gate judgment, including explicit unmeasured evidence.</summary>
    public sealed class G10GateEvaluation
    {
        public string Status { get; set; } = "not_measured";

        public List<string> Failures { get; set; } = new List<string>();

        public List<string> NotMeasuredFields { get; set; } = new List<string>();
    }

    /// <summary>Complete machine-readable output from the G10 measurement harness.</summary>
    public sealed class G10MeasurementReport
    {
        public string SchemaVersion { get; set; } = "g10-mvp2-v1";

        public DateTime StartedUtc { get; set; }

        public DateTime CompletedUtc { get; set; }

        public bool ManifestWorkload { get; set; }

        public int ActorCount { get; set; }

        public int ModsPerActor { get; set; }

        public int RandomSeed { get; set; }

        public int FrameRate { get; set; }

        public double WarmupSeconds { get; set; }

        public double MeasurementSeconds { get; set; }

        public G10ProviderReport Provider { get; set; }

        public List<G10PatternReport> Patterns { get; set; } = new List<G10PatternReport>();

        public List<G10WorldWorkloadReport> WorldRuns { get; set; } = new List<G10WorldWorkloadReport>();

        public G10MeasurementValue<int?> DiscoveredTests { get; set; }

        public G10MeasurementValue<int?> SkippedTests { get; set; }

        public string DiscoveryEvidenceSource { get; set; } = "";

        public G10GateEvaluation Gate { get; set; } = new G10GateEvaluation();
    }
}
