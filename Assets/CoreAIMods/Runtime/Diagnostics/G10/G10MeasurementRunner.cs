using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Mods.Rbx.Instances.Scheduling;

namespace CoreAI.Diagnostics.G10
{
    /// <summary>Runs both required G10 arrival patterns against production-composed services.</summary>
    public static class G10MeasurementRunner
    {
        private const string SharedCancellationScope = "g10-shared-chat-scope";
        private const string EventProbeStoreKey = "event_probe";

        /// <summary>Runs the configured workload and returns measured values without inferred defaults.</summary>
        public static async Task<G10MeasurementReport> RunAsync(
            G10MeasurementConfiguration configuration,
            string benchActorSource,
            Action<string> progress = null,
            CancellationToken cancellationToken = default)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            if (string.IsNullOrWhiteSpace(benchActorSource))
            {
                throw new ArgumentException("bench_actor.lua source is required.", nameof(benchActorSource));
            }

            IReadOnlyList<string> errors = configuration.Validate();
            if (errors.Count > 0)
            {
                throw new InvalidOperationException(string.Join("; ", errors));
            }

            G10MeasurementReport report = CreateReport(configuration);
            int providerResponses = 0;
            int stubResponses = 0;
            foreach (G10ArrivalPattern pattern in new[]
                     {
                         G10ArrivalPattern.Staggered,
                         G10ArrivalPattern.SynchronizedBurst
                     })
            {
                progress?.Invoke("starting " + pattern);
                G10PatternRunOutcome outcome = await RunPatternAsync(
                    configuration,
                    benchActorSource,
                    pattern,
                    progress,
                    cancellationToken);
                report.Patterns.Add(outcome.PatternReport);
                report.WorldRuns.Add(outcome.WorldReport);
                if (configuration.Provider.ProviderMode == G10ProviderMode.RealProvider)
                {
                    providerResponses += outcome.SuccessfulProviderResponses;
                }
                else
                {
                    stubResponses += outcome.SuccessfulProviderResponses;
                }

                progress?.Invoke("completed " + pattern);
            }

            report.Provider = BuildProviderReport(configuration.Provider, providerResponses, stubResponses);
            report.CompletedUtc = DateTime.UtcNow;
            EvaluateGate(report, configuration);
            return report;
        }

        /// <summary>Builds the exact 40-request measurement schedule for a manifest-length phase.</summary>
        public static IReadOnlyList<double> BuildManifestArrivalSchedule(G10ArrivalPattern pattern)
        {
            return BuildArrivalSchedule(
                pattern,
                G10MeasurementConfiguration.ManifestMeasurementSeconds,
                false);
        }

        /// <summary>Builds the seeded, exactly uniform delayed-thread frame assignments.</summary>
        public static IReadOnlyList<int> BuildDelayedDeadlineFrames()
        {
            int actorCount = G10MeasurementConfiguration.ManifestActorCount;
            int delayedPerActor = G10MeasurementConfiguration.ManifestDelayedThreadsPerActor;
            int frameCount = G10MeasurementConfiguration.ManifestDelayFrameCount;
            List<int> frames = new List<int>(actorCount * delayedPerActor);
            for (int repeat = 0; repeat < actorCount * delayedPerActor / frameCount; repeat++)
            {
                for (int frame = 1; frame <= frameCount; frame++)
                {
                    frames.Add(frame);
                }
            }

            uint state = G10MeasurementConfiguration.ManifestRandomSeed;
            for (int index = frames.Count - 1; index > 0; index--)
            {
                state = NextRandom(state);
                int swapIndex = (int)(state % (uint)(index + 1));
                int value = frames[index];
                frames[index] = frames[swapIndex];
                frames[swapIndex] = value;
            }

            return frames;
        }

        private static async Task<G10PatternRunOutcome> RunPatternAsync(
            G10MeasurementConfiguration configuration,
            string benchActorSource,
            G10ArrivalPattern pattern,
            Action<string> progress,
            CancellationToken cancellationToken)
        {
            using G10MeasurementSession session = G10MeasurementComposition.Compose(configuration);
            List<G10ActorState> actors = CreateActors();
            IReadOnlyList<int> delayedFrames = BuildDelayedDeadlineFrames();
            LoadActorMods(session, actors, delayedFrames, benchActorSource, configuration.FrameRate);

            ActorContext hostActor = session.ActorIdentityProvider.GetActorContext(BuiltInAgentRoleIds.Programmer);
            List<G10RequestRecord> records = new List<G10RequestRecord>();
            CancellationTokenSource requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Stopwatch runClock = Stopwatch.StartNew();
            long warmupStart = Stopwatch.GetTimestamp();
            await RunPhaseAsync(
                configuration,
                session,
                actors,
                hostActor,
                pattern,
                true,
                warmupStart,
                configuration.WarmupSeconds,
                records,
                requestDeadline.Token,
                progress,
                cancellationToken);

            G10RuntimeCounterSnapshot warmupCounters = session.Observability.Snapshot();
            int warmupGuardSampleCount = session.Observability
                .SnapshotGuardedInstructionStepSamples()
                .Count;
            int warmupStoreWriteCount = session.ModStore.SnapshotWrites().Count;
            Dictionary<string, G10ActorLuaSnapshot> warmupLua = CaptureActorLua(session.ModStore, actors);
            long measurementStart = Stopwatch.GetTimestamp();
            await RunPhaseAsync(
                configuration,
                session,
                actors,
                hostActor,
                pattern,
                false,
                measurementStart,
                configuration.MeasurementSeconds,
                records,
                requestDeadline.Token,
                progress,
                cancellationToken);

            G10RuntimeCounterSnapshot finalCounters = session.Observability.Snapshot();
            IReadOnlyList<long> finalGuardSamples = session.Observability
                .SnapshotGuardedInstructionStepSamples();
            IReadOnlyList<G10ModStoreWrite> finalStoreWrites = session.ModStore.SnapshotWrites();
            Dictionary<string, G10ActorLuaSnapshot> finalLua = CaptureActorLua(session.ModStore, actors);
            await DrainRequestsAsync(records, requestDeadline, progress, cancellationToken);
            runClock.Stop();

            G10PatternReport patternReport = BuildPatternReport(records, session.ProviderProbe, pattern);
            G10WorldWorkloadReport worldReport = BuildWorldReport(
                session,
                actors,
                delayedFrames,
                warmupCounters,
                finalCounters,
                warmupGuardSampleCount,
                finalGuardSamples,
                warmupStoreWriteCount,
                finalStoreWrites,
                warmupLua,
                finalLua,
                configuration.MeasurementSeconds);
            int successfulProviderResponses = session.ProviderProbe.Snapshot().Count(
                observation => observation.Succeeded);
            requestDeadline.Dispose();
            return new G10PatternRunOutcome(patternReport, worldReport, successfulProviderResponses);
        }

        private static List<G10ActorState> CreateActors()
        {
            List<G10ActorState> actors = new List<G10ActorState>(
                G10MeasurementConfiguration.ManifestActorCount);
            for (int actorIndex = 0;
                 actorIndex < G10MeasurementConfiguration.ManifestActorCount;
                 actorIndex++)
            {
                string actorId = "g10-actor-" + actorIndex.ToString("D2", CultureInfo.InvariantCulture);
                string sessionId = actorId + "-session";
                LocalActorIdentityProvider identity = new LocalActorIdentityProvider(
                    actorId,
                    sessionId,
                    "g10-world",
                    ActorGrantSet.None,
                    AgentMemoryScope.Empty);
                actors.Add(new G10ActorState(
                    actorId,
                    "g10-mod-" + actorIndex.ToString("D2", CultureInfo.InvariantCulture),
                    "g10-event-" + actorIndex.ToString("D2", CultureInfo.InvariantCulture),
                    identity.GetActorContext(BuiltInAgentRoleIds.SmartChat)));
            }

            return actors;
        }

        private static void LoadActorMods(
            G10MeasurementSession session,
            IReadOnlyList<G10ActorState> actors,
            IReadOnlyList<int> delayedFrames,
            string benchActorSource,
            int frameRate)
        {
            int delayIndex = 0;
            for (int actorIndex = 0; actorIndex < actors.Count; actorIndex++)
            {
                G10ActorState actor = actors[actorIndex];
                List<int> actorDelayFrames = new List<int>(
                    G10MeasurementConfiguration.ManifestDelayedThreadsPerActor);
                for (int threadIndex = 0;
                     threadIndex < G10MeasurementConfiguration.ManifestDelayedThreadsPerActor;
                     threadIndex++)
                {
                    actorDelayFrames.Add(delayedFrames[delayIndex]);
                    delayIndex++;
                }

                string source = InstantiateBenchActorSource(
                    benchActorSource,
                    actor.EventName,
                    actorDelayFrames,
                    frameRate);
                session.ModRuntime.LoadMod(
                    actor.Context,
                    actor.ModId,
                    source,
                    LuaCapabilities.All,
                    false);
            }
        }

        private static string InstantiateBenchActorSource(
            string template,
            string eventName,
            IReadOnlyList<int> delayFrames,
            int frameRate)
        {
            List<string> delays = new List<string>(delayFrames.Count);
            for (int index = 0; index < delayFrames.Count; index++)
            {
                double seconds = delayFrames[index] / (double)frameRate;
                delays.Add(seconds.ToString("0.000000000", CultureInfo.InvariantCulture));
            }

            string source = template.Replace("__G10_EVENT_NAME__", eventName);
            source = source.Replace("__G10_DELAY_SECONDS__", string.Join(", ", delays));
            if (source.Contains("__G10_", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("bench_actor.lua contains an unresolved G10 placeholder.");
            }

            return source;
        }

        private static async Task RunPhaseAsync(
            G10MeasurementConfiguration configuration,
            G10MeasurementSession session,
            IReadOnlyList<G10ActorState> actors,
            ActorContext hostActor,
            G10ArrivalPattern pattern,
            bool warmup,
            long phaseStartTimestamp,
            double durationSeconds,
            List<G10RequestRecord> records,
            CancellationToken requestCancellationToken,
            Action<string> progress,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<double> arrivals = BuildArrivalSchedule(pattern, durationSeconds, warmup);
            int nextArrival = 0;
            int frameCount = (int)Math.Ceiling(durationSeconds * configuration.FrameRate);
            double frameSeconds = 1d / configuration.FrameRate;
            OfferDueRequests(
                session,
                actors,
                pattern,
                warmup,
                phaseStartTimestamp,
                0d,
                arrivals,
                ref nextArrival,
                records,
                requestCancellationToken);

            for (int frame = 1; frame <= frameCount; frame++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double phaseSeconds = Math.Min(frame * frameSeconds, durationSeconds);
                await WaitUntilAsync(phaseStartTimestamp, phaseSeconds, cancellationToken);
                session.ModStack.GameplayBindings.RbxApi.Scheduler.Advance(frameSeconds);
                if (frame % configuration.FrameRate == 0)
                {
                    EmitActorEvents(
                        session,
                        actors,
                        hostActor,
                        warmup,
                        frame / configuration.FrameRate);
                }
                session.ModRuntime.Tick(hostActor, frameSeconds);

                OfferDueRequests(
                    session,
                    actors,
                    pattern,
                    warmup,
                    phaseStartTimestamp,
                    phaseSeconds,
                    arrivals,
                    ref nextArrival,
                    records,
                    requestCancellationToken);
                if (progress != null && frame % (configuration.FrameRate * 10) == 0)
                {
                    progress((warmup ? "warmup " : "measurement ") +
                             phaseSeconds.ToString("0", CultureInfo.InvariantCulture) + "/" +
                             durationSeconds.ToString("0", CultureInfo.InvariantCulture) + " s");
                }
            }
        }

        private static void EmitActorEvents(
            G10MeasurementSession session,
            IReadOnlyList<G10ActorState> actors,
            ActorContext hostActor,
            bool warmup,
            int second)
        {
            for (int actorIndex = 0; actorIndex < actors.Count; actorIndex++)
            {
                G10ActorState actor = actors[actorIndex];
                string payload = BuildEventProbePayload(warmup, second, actor.ActorId);
                session.ModRuntime.EmitEvent(hostActor, actor.EventName, payload);
            }
        }

        private static string BuildEventProbePayload(bool warmup, int second, string actorId)
        {
            return (warmup ? "warmup" : "measurement") + "|" +
                   second.ToString(CultureInfo.InvariantCulture) + "|" + actorId;
        }

        private static IReadOnlyList<double> BuildArrivalSchedule(
            G10ArrivalPattern pattern,
            double durationSeconds,
            bool warmup)
        {
            List<double> arrivals = new List<double>();
            int roundCount = warmup
                ? 1
                : (int)Math.Ceiling(durationSeconds /
                                    G10MeasurementConfiguration.ManifestChatCadenceSeconds);
            for (int round = 0; round < roundCount; round++)
            {
                double roundStart = round * G10MeasurementConfiguration.ManifestChatCadenceSeconds;
                for (int actorIndex = 0;
                     actorIndex < G10MeasurementConfiguration.ManifestActorCount;
                     actorIndex++)
                {
                    double target = pattern == G10ArrivalPattern.SynchronizedBurst
                        ? roundStart
                        : roundStart + actorIndex *
                        (G10MeasurementConfiguration.ManifestChatCadenceSeconds /
                         (double)G10MeasurementConfiguration.ManifestActorCount);
                    if (target < durationSeconds)
                    {
                        arrivals.Add(target);
                    }
                }
            }

            arrivals.Sort();
            return arrivals;
        }

        private static void OfferDueRequests(
            G10MeasurementSession session,
            IReadOnlyList<G10ActorState> actors,
            G10ArrivalPattern pattern,
            bool warmup,
            long phaseStartTimestamp,
            double phaseSeconds,
            IReadOnlyList<double> arrivals,
            ref int nextArrival,
            List<G10RequestRecord> records,
            CancellationToken cancellationToken)
        {
            while (nextArrival < arrivals.Count && arrivals[nextArrival] <= phaseSeconds + 0.000001d)
            {
                double targetSeconds = arrivals[nextArrival];
                int actorIndex;
                if (pattern == G10ArrivalPattern.SynchronizedBurst)
                {
                    actorIndex = nextArrival % G10MeasurementConfiguration.ManifestActorCount;
                }
                else
                {
                    int indexWithinRound = nextArrival % G10MeasurementConfiguration.ManifestActorCount;
                    actorIndex = indexWithinRound;
                }

                G10ActorState actor = actors[actorIndex];
                string phaseLabel = warmup ? "warmup" : "measurement";
                string traceId = "g10-" + pattern + "-" + phaseLabel + "-" +
                                 nextArrival.ToString("D2", CultureInfo.InvariantCulture) + "-" + actor.ActorId;
                G10RequestRecord record = new G10RequestRecord
                {
                    ActorId = actor.ActorId,
                    TraceId = traceId,
                    Warmup = warmup,
                    TargetSeconds = targetSeconds,
                    PhaseStartTimestamp = phaseStartTimestamp,
                    OfferedTimestamp = Stopwatch.GetTimestamp()
                };
                records.Add(record);
                record.Task = ObserveRequestAsync(
                    session.Orchestrator,
                    actor,
                    record,
                    cancellationToken);
                nextArrival++;
            }
        }

        private static async Task ObserveRequestAsync(
            IAiOrchestrationService orchestrator,
            G10ActorState actor,
            G10RequestRecord record,
            CancellationToken cancellationToken)
        {
            try
            {
                string response = await orchestrator.RunTaskAsync(new AiTaskRequest
                {
                    RoleId = BuiltInAgentRoleIds.SmartChat,
                    Hint = "G10 capacity probe " + record.TraceId,
                    TraceId = record.TraceId,
                    ActorContext = actor.Context,
                    CancellationScope = SharedCancellationScope,
                    DeadlineCancellationToken = cancellationToken,
                    MaxToolCallRoundtrips = 0
                }, cancellationToken);
                record.ReturnedContent = response ?? "";
            }
            catch (OperationCanceledException ex)
            {
                record.Cancelled = true;
                record.Error = ex.Message;
            }
            catch (AiOrchestrationQueueFullException ex)
            {
                record.AdmissionFailure = true;
                record.Error = ex.Message;
            }
            catch (Exception ex)
            {
                record.ProviderFailure = true;
                record.Error = ex.Message;
            }
            finally
            {
                record.CompletedTimestamp = Stopwatch.GetTimestamp();
            }
        }

        private static async Task DrainRequestsAsync(
            IReadOnlyList<G10RequestRecord> records,
            CancellationTokenSource requestDeadline,
            Action<string> progress,
            CancellationToken cancellationToken)
        {
            long latestOffer = records.Count == 0
                ? Stopwatch.GetTimestamp()
                : records.Max(record => record.OfferedTimestamp);
            long deadline = latestOffer + 60L * Stopwatch.Frequency;
            while (records.Any(record => record.Task != null && !record.Task.IsCompleted) &&
                   Stopwatch.GetTimestamp() < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(20, cancellationToken);
            }

            List<G10RequestRecord> overdue = records
                .Where(record => record.Task != null && !record.Task.IsCompleted)
                .ToList();
            if (overdue.Count > 0)
            {
                for (int index = 0; index < overdue.Count; index++)
                {
                    overdue[index].HarnessDeadlineCancellation = true;
                }

                progress?.Invoke("cancelling " + overdue.Count + " requests at the 60 s starvation deadline");
                requestDeadline.Cancel();
            }

            Task[] tasks = records
                .Where(record => record.Task != null)
                .Select(record => record.Task)
                .ToArray();
            if (tasks.Length > 0)
            {
                await Task.WhenAll(tasks);
            }
        }

        private static async Task WaitUntilAsync(
            long phaseStartTimestamp,
            double targetSeconds,
            CancellationToken cancellationToken)
        {
            while (true)
            {
                long targetTimestamp = phaseStartTimestamp +
                                       (long)(targetSeconds * Stopwatch.Frequency);
                long remainingTicks = targetTimestamp - Stopwatch.GetTimestamp();
                if (remainingTicks <= 0)
                {
                    return;
                }

                double remainingMilliseconds = remainingTicks * 1000d / Stopwatch.Frequency;
                int delayMilliseconds = remainingMilliseconds > 2d
                    ? Math.Max(1, (int)Math.Floor(remainingMilliseconds - 1d))
                    : 1;
                await Task.Delay(delayMilliseconds, cancellationToken);
            }
        }

        private static Dictionary<string, G10ActorLuaSnapshot> CaptureActorLua(
            G10MemoryLuaModStore store,
            IReadOnlyList<G10ActorState> actors)
        {
            Dictionary<string, G10ActorLuaSnapshot> snapshots =
                new Dictionary<string, G10ActorLuaSnapshot>(StringComparer.Ordinal);
            for (int index = 0; index < actors.Count; index++)
            {
                G10ActorState actor = actors[index];
                snapshots[actor.ActorId] = new G10ActorLuaSnapshot(
                    ReadStoreInt(store, actor.ModId, "deferred_count"),
                    ReadStoreInt(store, actor.ModId, "delayed_count"),
                    ReadStoreInt(store, actor.ModId, "timer_count"),
                    ReadStoreInt(store, actor.ModId, "event_count"));
            }

            return snapshots;
        }

        private static int ReadStoreInt(G10MemoryLuaModStore store, string modId, string key)
        {
            string value = store.Get(modId, key);
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                ? parsed
                : 0;
        }

        private static G10PatternReport BuildPatternReport(
            IReadOnlyList<G10RequestRecord> allRecords,
            G10ProviderProbe providerProbe,
            G10ArrivalPattern pattern)
        {
            ClassifyCancellations(allRecords);
            List<G10RequestRecord> records = allRecords.Where(record => !record.Warmup).ToList();
            List<double> totalLatencies = new List<double>();
            List<double> queueLatencies = new List<double>();
            List<double> providerLatencies = new List<double>();
            for (int index = 0; index < records.Count; index++)
            {
                G10RequestRecord record = records[index];
                if (providerProbe.TryGet(record.TraceId, out G10ProviderObservation observation))
                {
                    record.ProviderSucceeded = observation.Succeeded;
                    record.ProviderFailure = !observation.Succeeded && !observation.Cancelled;
                    if (observation.Succeeded)
                    {
                        double total = ElapsedMilliseconds(record.OfferedTimestamp, record.CompletedTimestamp);
                        double queue = ElapsedMilliseconds(record.OfferedTimestamp, observation.StartedTimestamp);
                        totalLatencies.Add(total);
                        queueLatencies.Add(Math.Max(0d, queue));
                        providerLatencies.Add(observation.LatencyMilliseconds);
                    }
                }
            }

            int served = records.Count(record => record.ProviderSucceeded);
            G10PatternReport report = new G10PatternReport
            {
                Pattern = pattern.ToString(),
                Offered = records.Count,
                Served = served,
                ServedFraction = records.Count == 0 ? 0d : served / (double)records.Count,
                P95EndToEndLatencyMilliseconds = Percentile95(totalLatencies),
                P95QueueLatencyMilliseconds = Percentile95(queueLatencies),
                P95ProviderLatencyMilliseconds = Percentile95(providerLatencies),
                MaximumArrivalSkewMilliseconds = CalculateArrivalSkew(records, pattern),
                CrossActorCancellations = allRecords.Count(record => record.CrossActorCancellation),
                SameActorCancellations = allRecords.Count(record => record.SameActorCancellation),
                HarnessDeadlineCancellations = allRecords.Count(record => record.HarnessDeadlineCancellation),
                ProviderFailures = records.Count(record => record.ProviderFailure),
                AdmissionFailures = records.Count(record => record.AdmissionFailure)
            };
            report.ServedFractionAtLeastNinetyFivePercent = report.ServedFraction >= 0.95d;
            report.P95AtMostFiveSeconds =
                report.P95EndToEndLatencyMilliseconds.Status == "measured" &&
                report.P95EndToEndLatencyMilliseconds.Value.HasValue &&
                report.P95EndToEndLatencyMilliseconds.Value.Value <= 5000d;
            report.NoCrossActorCancellations = report.CrossActorCancellations == 0;
            report.Actors = BuildActorReports(records);
            report.NoActorStarvedBeyondSixtySeconds =
                report.Actors.All(actor => !actor.StarvedBeyondSixtySeconds);
            return report;
        }

        private static List<G10ActorServiceReport> BuildActorReports(IReadOnlyList<G10RequestRecord> records)
        {
            List<G10ActorServiceReport> reports = new List<G10ActorServiceReport>();
            for (int actorIndex = 0;
                 actorIndex < G10MeasurementConfiguration.ManifestActorCount;
                 actorIndex++)
            {
                string actorId = "g10-actor-" + actorIndex.ToString("D2", CultureInfo.InvariantCulture);
                List<G10RequestRecord> actorRecords = records
                    .Where(record => string.Equals(record.ActorId, actorId, StringComparison.Ordinal))
                    .OrderBy(record => record.OfferedTimestamp)
                    .ToList();
                List<double> waits = new List<double>();
                bool starved = false;
                for (int requestIndex = 0; requestIndex < actorRecords.Count; requestIndex++)
                {
                    G10RequestRecord offered = actorRecords[requestIndex];
                    G10RequestRecord nextService = actorRecords.FirstOrDefault(candidate =>
                        candidate.ProviderSucceeded &&
                        candidate.CompletedTimestamp >= offered.OfferedTimestamp);
                    if (nextService == null)
                    {
                        starved = true;
                        continue;
                    }

                    double waitSeconds = ElapsedMilliseconds(
                        offered.OfferedTimestamp,
                        nextService.CompletedTimestamp) / 1000d;
                    waits.Add(waitSeconds);
                    if (waitSeconds > 60d)
                    {
                        starved = true;
                    }
                }

                reports.Add(new G10ActorServiceReport
                {
                    ActorId = actorId,
                    Offered = actorRecords.Count,
                    Served = actorRecords.Count(record => record.ProviderSucceeded),
                    MaximumServiceWaitSeconds = waits.Count == 0
                        ? G10MeasurementValue<double?>.NotMeasured("actor received no successful response")
                        : G10MeasurementValue<double?>.Measured(waits.Max()),
                    StarvedBeyondSixtySeconds = starved
                });
            }

            return reports;
        }

        private static void ClassifyCancellations(IReadOnlyList<G10RequestRecord> records)
        {
            for (int index = 0; index < records.Count; index++)
            {
                G10RequestRecord cancelled = records[index];
                if (!cancelled.Cancelled || cancelled.HarnessDeadlineCancellation)
                {
                    continue;
                }

                G10RequestRecord sameActorTrigger = records
                    .Where(candidate =>
                        !ReferenceEquals(candidate, cancelled) &&
                        candidate.OfferedTimestamp >= cancelled.OfferedTimestamp &&
                        candidate.OfferedTimestamp <= cancelled.CompletedTimestamp &&
                        string.Equals(candidate.ActorId, cancelled.ActorId, StringComparison.Ordinal))
                    .OrderByDescending(candidate => candidate.OfferedTimestamp)
                    .FirstOrDefault();
                if (sameActorTrigger != null)
                {
                    cancelled.SameActorCancellation = true;
                    continue;
                }

                G10RequestRecord crossActorTrigger = records
                    .Where(candidate =>
                        !ReferenceEquals(candidate, cancelled) &&
                        candidate.OfferedTimestamp >= cancelled.OfferedTimestamp &&
                        candidate.OfferedTimestamp <= cancelled.CompletedTimestamp)
                    .OrderByDescending(candidate => candidate.OfferedTimestamp)
                    .FirstOrDefault();
                if (crossActorTrigger != null)
                {
                    cancelled.CrossActorCancellation = true;
                }
            }
        }

        private static double CalculateArrivalSkew(
            IReadOnlyList<G10RequestRecord> records,
            G10ArrivalPattern pattern)
        {
            if (records.Count == 0)
            {
                return 0d;
            }

            if (pattern == G10ArrivalPattern.Staggered)
            {
                return records.Max(record => Math.Abs(
                    ElapsedMilliseconds(record.PhaseStartTimestamp, record.OfferedTimestamp) -
                    record.TargetSeconds * 1000d));
            }

            double maximumWidth = 0d;
            IEnumerable<IGrouping<double, G10RequestRecord>> waves = records.GroupBy(
                record => record.TargetSeconds);
            foreach (IGrouping<double, G10RequestRecord> wave in waves)
            {
                long first = wave.Min(record => record.OfferedTimestamp);
                long last = wave.Max(record => record.OfferedTimestamp);
                maximumWidth = Math.Max(maximumWidth, ElapsedMilliseconds(first, last));
            }

            return maximumWidth;
        }

        private static G10WorldWorkloadReport BuildWorldReport(
            G10MeasurementSession session,
            IReadOnlyList<G10ActorState> actors,
            IReadOnlyList<int> delayedFrames,
            G10RuntimeCounterSnapshot warmupCounters,
            G10RuntimeCounterSnapshot finalCounters,
            int warmupGuardSampleCount,
            IReadOnlyList<long> finalGuardSamples,
            int warmupStoreWriteCount,
            IReadOnlyList<G10ModStoreWrite> finalStoreWrites,
            IReadOnlyDictionary<string, G10ActorLuaSnapshot> warmupLua,
            IReadOnlyDictionary<string, G10ActorLuaSnapshot> finalLua,
            double measurementSeconds)
        {
            long measurementSteps = finalCounters.GuardedInstructionSteps -
                                    warmupCounters.GuardedInstructionSteps;
            long measurementOperations = finalCounters.CompletedOperations -
                                         warmupCounters.CompletedOperations;
            long measurementEvents = finalCounters.EventsDelivered - warmupCounters.EventsDelivered;
            int deferred = 0;
            int delayed = 0;
            int timers = 0;
            int events = 0;
            bool allDeferred = true;
            bool allDelayed = true;
            int expectedEventsPerActor = (int)Math.Floor(
                measurementSeconds /
                (G10MeasurementConfiguration.ManifestEventCadenceMilliseconds / 1000d));
            bool exactEvents = true;
            for (int index = 0; index < actors.Count; index++)
            {
                string actorId = actors[index].ActorId;
                G10ActorLuaSnapshot warmup = warmupLua[actorId];
                G10ActorLuaSnapshot final = finalLua[actorId];
                deferred += final.DeferredCount;
                delayed += final.DelayedCount;
                int timerDelta = final.TimerCount - warmup.TimerCount;
                int eventDelta = final.EventCount - warmup.EventCount;
                timers += timerDelta;
                events += eventDelta;
                allDeferred &= final.DeferredCount ==
                               G10MeasurementConfiguration.ManifestDeferredThreadsPerActor;
                allDelayed &= final.DelayedCount ==
                               G10MeasurementConfiguration.ManifestDelayedThreadsPerActor;
                exactEvents &= eventDelta == expectedEventsPerActor;
            }

            List<long> measurementGuardSamples = finalGuardSamples
                .Skip(Math.Min(warmupGuardSampleCount, finalGuardSamples.Count))
                .ToList();
            Dictionary<long, int> guardedStepHistogram = measurementGuardSamples
                .GroupBy(value => value)
                .ToDictionary(group => group.Key, group => group.Count());
            long calibratedLuaBodyInstructions = measurementGuardSamples.Count == 0
                ? 0L
                : measurementGuardSamples.Max();
            long sampledGuardedSteps = measurementGuardSamples.Sum();

            Dictionary<string, string> expectedProbeTargets = new Dictionary<string, string>(
                StringComparer.Ordinal);
            for (int second = 1; second <= expectedEventsPerActor; second++)
            {
                for (int actorIndex = 0; actorIndex < actors.Count; actorIndex++)
                {
                    G10ActorState actor = actors[actorIndex];
                    expectedProbeTargets.Add(
                        BuildEventProbePayload(false, second, actor.ActorId),
                        actor.ModId);
                }
            }

            Dictionary<string, int> matchingProbeCounts = new Dictionary<string, int>(
                StringComparer.Ordinal);
            int subscriberInvocations = 0;
            int nonSubscriberInvocations = 0;
            IEnumerable<G10ModStoreWrite> measurementWrites = finalStoreWrites
                .Skip(Math.Min(warmupStoreWriteCount, finalStoreWrites.Count));
            foreach (G10ModStoreWrite write in measurementWrites)
            {
                if (!string.Equals(write.Key, EventProbeStoreKey, StringComparison.Ordinal) ||
                    !write.Value.StartsWith("measurement|", StringComparison.Ordinal))
                {
                    continue;
                }

                if (expectedProbeTargets.TryGetValue(write.Value, out string expectedModId) &&
                    string.Equals(write.ModId, expectedModId, StringComparison.Ordinal))
                {
                    subscriberInvocations++;
                    matchingProbeCounts.TryGetValue(write.Value, out int count);
                    matchingProbeCounts[write.Value] = count + 1;
                }
                else
                {
                    nonSubscriberInvocations++;
                }
            }

            bool everyProbeMatchedExactlyOnce = expectedProbeTargets.Keys.All(payload =>
                matchingProbeCounts.TryGetValue(payload, out int count) && count == 1);
            int independentNonSubscriberChecks = expectedProbeTargets.Count *
                                                 G10MeasurementConfiguration.ManifestNonSubscribersPerEmit;

            IReadOnlyList<LuaModInfo> mods = session.ModRuntime.ListMods(
                session.ActorIdentityProvider.GetActorContext(BuiltInAgentRoleIds.Programmer));
            bool everyActorHasOneTimer = mods.Count == actors.Count &&
                                         mods.All(mod => mod.TimerCount == 1);
            Dictionary<int, int> histogram = new Dictionary<int, int>();
            for (int frame = 1;
                 frame <= G10MeasurementConfiguration.ManifestDelayFrameCount;
                 frame++)
            {
                histogram[frame] = delayedFrames.Count(value => value == frame);
            }

            double meanSteps = measurementOperations > 0
                ? measurementSteps / (double)measurementOperations
                : 0d;
            return new G10WorldWorkloadReport
            {
                GuardedInstructionSteps = finalCounters.GuardedInstructionSteps,
                MeasurementGuardedInstructionSteps = measurementSteps,
                ThreadResumes = finalCounters.ThreadResumes,
                EventsDelivered = finalCounters.EventsDelivered,
                MeasurementEventsDelivered = measurementEvents,
                CompletedLuaOperations = finalCounters.CompletedOperations,
                MeasurementCompletedLuaOperations = measurementOperations,
                MeanGuardedStepsPerMeasurementOperation = meanSteps,
                CalibratedLuaBodyGuardedInstructions = calibratedLuaBodyInstructions,
                MeasurementGuardedStepHistogram = guardedStepHistogram,
                MeanGuardedStepsWithinManifestFrameBudget =
                    measurementOperations > 0 &&
                    measurementGuardSamples.Count == measurementOperations &&
                    sampledGuardedSteps == measurementSteps &&
                    calibratedLuaBodyInstructions <=
                    G10MeasurementConfiguration.ManifestGuardedInstructionsPerFrame,
                DeferredCallbacksCompleted = deferred,
                DelayedCallbacksCompleted = delayed,
                TimerCallbacksDuringMeasurement = timers,
                SubscriberInvocationsDuringMeasurement = subscriberInvocations,
                NonSubscriberInvocationsDuringMeasurement = nonSubscriberInvocations,
                IndependentNonSubscriberChecksDuringMeasurement = independentNonSubscriberChecks,
                SubscriberIsolationEvidenceSource =
                    "external Lua mod-store event_probe write trace keyed by emitted payload",
                EveryActorHasOneTimer = everyActorHasOneTimer,
                EveryActorCompletedTenDeferredThreads = allDeferred,
                EveryActorCompletedTenDelayedThreads = allDelayed,
                EveryEmitInvokedExactlyOneSubscriber =
                    exactEvents &&
                    measurementEvents == events &&
                    subscriberInvocations == expectedProbeTargets.Count &&
                    everyProbeMatchedExactlyOnce &&
                    nonSubscriberInvocations == 0,
                DelayedDeadlineFrameHistogram = histogram
            };
        }

        private static G10MeasurementReport CreateReport(G10MeasurementConfiguration configuration)
        {
            return new G10MeasurementReport
            {
                StartedUtc = DateTime.UtcNow,
                ManifestWorkload = configuration.IsManifestWorkload,
                ActorCount = G10MeasurementConfiguration.ManifestActorCount,
                ModsPerActor = G10MeasurementConfiguration.ManifestModsPerActor,
                RandomSeed = G10MeasurementConfiguration.ManifestRandomSeed,
                FrameRate = configuration.FrameRate,
                WarmupSeconds = configuration.WarmupSeconds,
                MeasurementSeconds = configuration.MeasurementSeconds,
                DiscoveredTests = configuration.DiscoveredTestCount.HasValue
                    ? G10MeasurementValue<int?>.Measured(configuration.DiscoveredTestCount.Value)
                    : G10MeasurementValue<int?>.NotMeasured("test discovery was not part of this process"),
                SkippedTests = configuration.SkippedTestCount.HasValue
                    ? G10MeasurementValue<int?>.Measured(configuration.SkippedTestCount.Value)
                    : G10MeasurementValue<int?>.NotMeasured("test discovery was not part of this process"),
                DiscoveryEvidenceSource = configuration.DiscoveryEvidenceSource ?? ""
            };
        }

        private static G10ProviderReport BuildProviderReport(
            G10ProviderConfiguration configuration,
            int providerResponses,
            int stubResponses)
        {
            bool real = configuration.ProviderMode == G10ProviderMode.RealProvider;
            return new G10ProviderReport
            {
                Mode = configuration.ProviderMode.ToString(),
                ModelId = real
                    ? G10MeasurementValue<string>.Measured(configuration.ModelId)
                    : G10MeasurementValue<string>.NotMeasured("scripted stub has no real model"),
                ContextCapTokens = real
                    ? G10MeasurementValue<int?>.Measured(configuration.ContextCapTokens.Value)
                    : G10MeasurementValue<int?>.NotMeasured("scripted stub has no provider context cap"),
                OutputCapTokens = real
                    ? G10MeasurementValue<int?>.Measured(configuration.OutputCapTokens.Value)
                    : G10MeasurementValue<int?>.NotMeasured("scripted stub has no provider output cap"),
                BackendConcurrency = real
                    ? G10MeasurementValue<int?>.Measured(configuration.BackendConcurrency.Value)
                    : G10MeasurementValue<int?>.NotMeasured("scripted stub has no real backend concurrency"),
                ScriptedLatencyMilliseconds = real
                    ? G10MeasurementValue<int?>.NotMeasured("real-provider mode does not use scripted latency")
                    : G10MeasurementValue<int?>.Measured(configuration.StubLatencyMilliseconds.Value),
                ChatResponsesActuallyProducedByProvider = real
                    ? G10MeasurementValue<int?>.Measured(providerResponses)
                    : G10MeasurementValue<int?>.NotMeasured(
                        "scripted responses do not satisfy the provider-produced response counter"),
                ScriptedStubResponses = stubResponses
            };
        }

        private static void EvaluateGate(
            G10MeasurementReport report,
            G10MeasurementConfiguration configuration)
        {
            G10GateEvaluation gate = new G10GateEvaluation();
            if (!report.ManifestWorkload)
            {
                gate.NotMeasuredFields.Add("manifest 30 s warm-up / 60 s measurement workload");
            }

            if (configuration.Provider.ProviderMode != G10ProviderMode.RealProvider)
            {
                gate.NotMeasuredFields.Add("real-provider model id");
                gate.NotMeasuredFields.Add("real-provider context cap");
                gate.NotMeasuredFields.Add("real-provider output cap");
                gate.NotMeasuredFields.Add("real-provider backend concurrency");
                gate.NotMeasuredFields.Add("real-provider served fraction and p95");
                gate.NotMeasuredFields.Add("provider-produced chat response counter");
            }

            if (report.DiscoveredTests.Status != "measured")
            {
                gate.NotMeasuredFields.Add("discovered test count");
            }
            else if (!report.DiscoveredTests.Value.HasValue || report.DiscoveredTests.Value.Value <= 0)
            {
                gate.Failures.Add("discovered test count must be non-zero");
            }

            if (report.SkippedTests.Status != "measured")
            {
                gate.NotMeasuredFields.Add("test skip count");
            }
            else if (!report.SkippedTests.Value.HasValue || report.SkippedTests.Value.Value != 0)
            {
                gate.Failures.Add("test skip count must be zero");
            }

            for (int index = 0; index < report.Patterns.Count; index++)
            {
                G10PatternReport pattern = report.Patterns[index];
                if (!pattern.ServedFractionAtLeastNinetyFivePercent)
                {
                    gate.Failures.Add(pattern.Pattern + " served fraction is below 95%");
                }

                if (!pattern.P95AtMostFiveSeconds)
                {
                    gate.Failures.Add(pattern.Pattern + " p95 end-to-end latency exceeds 5 s or is unmeasured");
                }

                if (!pattern.NoActorStarvedBeyondSixtySeconds)
                {
                    gate.Failures.Add(pattern.Pattern + " starved at least one actor beyond 60 s");
                }

                if (!pattern.NoCrossActorCancellations)
                {
                    gate.Failures.Add(pattern.Pattern + " observed cross-actor cancellation");
                }
            }

            for (int index = 0; index < report.WorldRuns.Count; index++)
            {
                G10WorldWorkloadReport world = report.WorldRuns[index];
                if (world.GuardedInstructionSteps <= 0)
                {
                    gate.Failures.Add("guarded Lua instruction counter is zero");
                }

                if (world.ThreadResumes <= 0)
                {
                    gate.Failures.Add("thread resume counter is zero");
                }

                if (world.EventsDelivered <= 0)
                {
                    gate.Failures.Add("event delivery counter is zero");
                }

                if (world.CompletedLuaOperations <= 0)
                {
                    gate.Failures.Add("completed Lua operation counter is zero");
                }

                if (!world.MeanGuardedStepsWithinManifestFrameBudget)
                {
                    gate.Failures.Add(
                        "bench_actor.lua per-resume guarded step calibration is incomplete or exceeds 589");
                }

                if (!world.EveryActorHasOneTimer ||
                    !world.EveryActorCompletedTenDeferredThreads ||
                    !world.EveryActorCompletedTenDelayedThreads ||
                    !world.EveryEmitInvokedExactlyOneSubscriber ||
                    world.NonSubscriberInvocationsDuringMeasurement != 0)
                {
                    gate.Failures.Add("Lua workload shape or subscriber isolation did not match manifest §2");
                }
            }

            if (configuration.Provider.ProviderMode == G10ProviderMode.RealProvider &&
                (report.Provider.ChatResponsesActuallyProducedByProvider.Status != "measured" ||
                 !report.Provider.ChatResponsesActuallyProducedByProvider.Value.HasValue ||
                 report.Provider.ChatResponsesActuallyProducedByProvider.Value.Value <= 0))
            {
                gate.Failures.Add("provider-produced chat response counter is zero");
            }

            gate.Status = gate.Failures.Count > 0
                ? "failed"
                : gate.NotMeasuredFields.Count > 0
                    ? "not_measured"
                    : "passed";
            report.Gate = gate;
        }

        private static G10MeasurementValue<double?> Percentile95(List<double> values)
        {
            if (values.Count == 0)
            {
                return G10MeasurementValue<double?>.NotMeasured("no served requests");
            }

            values.Sort();
            int index = Math.Max(0, (int)Math.Ceiling(values.Count * 0.95d) - 1);
            return G10MeasurementValue<double?>.Measured(values[index]);
        }

        private static double ElapsedMilliseconds(long startTimestamp, long endTimestamp)
        {
            if (endTimestamp <= startTimestamp)
            {
                return 0d;
            }

            return (endTimestamp - startTimestamp) * 1000d / Stopwatch.Frequency;
        }

        private static uint NextRandom(uint state)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return state;
        }

        private sealed class G10ActorState
        {
            public G10ActorState(string actorId, string modId, string eventName, ActorContext context)
            {
                ActorId = actorId;
                ModId = modId;
                EventName = eventName;
                Context = context;
            }

            public string ActorId { get; }

            public string ModId { get; }

            public string EventName { get; }

            public ActorContext Context { get; }
        }

        private sealed class G10RequestRecord
        {
            public string ActorId;
            public string TraceId;
            public bool Warmup;
            public double TargetSeconds;
            public long PhaseStartTimestamp;
            public long OfferedTimestamp;
            public long CompletedTimestamp;
            public Task Task;
            public string ReturnedContent = "";
            public string Error = "";
            public bool ProviderSucceeded;
            public bool ProviderFailure;
            public bool AdmissionFailure;
            public bool Cancelled;
            public bool CrossActorCancellation;
            public bool SameActorCancellation;
            public bool HarnessDeadlineCancellation;
        }

        private readonly struct G10ActorLuaSnapshot
        {
            public G10ActorLuaSnapshot(
                int deferredCount,
                int delayedCount,
                int timerCount,
                int eventCount)
            {
                DeferredCount = deferredCount;
                DelayedCount = delayedCount;
                TimerCount = timerCount;
                EventCount = eventCount;
            }

            public int DeferredCount { get; }

            public int DelayedCount { get; }

            public int TimerCount { get; }

            public int EventCount { get; }
        }

        private sealed class G10PatternRunOutcome
        {
            public G10PatternRunOutcome(
                G10PatternReport patternReport,
                G10WorldWorkloadReport worldReport,
                int successfulProviderResponses)
            {
                PatternReport = patternReport;
                WorldReport = worldReport;
                SuccessfulProviderResponses = successfulProviderResponses;
            }

            public G10PatternReport PatternReport { get; }

            public G10WorldWorkloadReport WorldReport { get; }

            public int SuccessfulProviderResponses { get; }
        }
    }
}
