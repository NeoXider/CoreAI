using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Diagnostics.G10;
using CoreAI.Mods.Rbx.Instances.Scheduling;

namespace CoreAI.Tools.Scale
{
    /// <summary>
    /// Drives one (actorCount, repeat) run: production composition, N client mods plus one host server
    /// mod, warm-up, the measured window with both chat arrival patterns, a drain, and the summary.
    /// The per-frame order mirrors the shipped <c>LuaModRuntimeTickDriver</c>: scheduler
    /// <c>Advance</c> with the RunService pumps at PreSimulation/Heartbeat/PreRender, then the mod
    /// runtime <c>Tick</c>.
    /// </summary>
    public sealed class ScaleRunner
    {
        private const string ServerModId = "scale-server";
        private const string SharedCancellationScope = "scale-shared-chat-scope";

        private readonly ScaleWorkload _workload;
        private readonly string _actorTemplate;
        private readonly string _serverTemplate;
        private readonly Action<string> _log;

        public ScaleRunner(ScaleWorkload workload, string actorTemplate, string serverTemplate, Action<string> log)
        {
            _workload = workload ?? throw new ArgumentNullException(nameof(workload));
            _actorTemplate = actorTemplate ?? throw new ArgumentNullException(nameof(actorTemplate));
            _serverTemplate = serverTemplate ?? throw new ArgumentNullException(nameof(serverTemplate));
            _log = log ?? (_ => { });
        }

        public ScaleRepeatResult Run(int actorCount, int repeat)
        {
            PumpedSynchronizationContext sync = new PumpedSynchronizationContext();
            SynchronizationContext previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(sync);
            try
            {
                using ScaleSession session = ScaleComposition.Compose(_workload);
                return RunCore(session, sync, actorCount, repeat);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
        }

        private ScaleRepeatResult RunCore(ScaleSession session, PumpedSynchronizationContext sync,
            int actorCount, int repeat)
        {
            ScaleWorkload w = _workload;
            double frameSeconds = w.FrameSeconds;
            long periodTicks = (long)(frameSeconds * Stopwatch.Frequency);
            ActorContext host = session.HostIdentityProvider.GetActorContext(BuiltInAgentRoleIds.Programmer);
            LuaCsRbxApiBindings rbxApi = session.RbxApi;
            ModScheduler scheduler = rbxApi.Scheduler;

            long handlerErrors = 0;
            List<string> handlerErrorSamples = new List<string>();
            session.ModRuntime.AddModHandlerErroredListener(host, (modId, error, count) =>
            {
                Interlocked.Increment(ref handlerErrors);
                lock (handlerErrorSamples)
                {
                    if (handlerErrorSamples.Count < 8)
                    {
                        handlerErrorSamples.Add(modId + ": " + error);
                    }
                }
            });

            string serverSource = _serverTemplate.Replace("__SCALE_SNAPSHOT_EVENT__", w.SnapshotEventName);
            EnsureNoPlaceholder(serverSource, "scale_server.lua");
            session.ModRuntime.LoadMod(host, ServerModId, serverSource, LuaCapabilities.All, false);

            List<ScaleActor> actors = new List<ScaleActor>(actorCount);
            for (int index = 0; index < actorCount; index++)
            {
                string actorId = "scale-actor-" + index.ToString("D3", CultureInfo.InvariantCulture);
                LocalActorIdentityProvider identity = new LocalActorIdentityProvider(
                    actorId,
                    actorId + "-session",
                    "scale-world",
                    ActorGrantSet.None,
                    AgentMemoryScope.Empty);
                ScaleActor actor = new ScaleActor
                {
                    Index = index,
                    ActorId = actorId,
                    ModId = "scale-mod-" + index.ToString("D3", CultureInfo.InvariantCulture),
                    Context = identity.GetActorContext(BuiltInAgentRoleIds.SmartChat)
                };
                string source = InstantiateActorSource(index);
                session.ModRuntime.LoadMod(actor.Context, actor.ModId, source, LuaCapabilities.All, false);
                actors.Add(actor);
            }

            long heartbeatTimestamp = 0;
            long inputTimestamp = 0;
            long heartbeatAlloc = 0;
            long inputAlloc = 0;
            Action<SchedulerPhase, double> onPhase = (phase, delta) =>
            {
                float frameDelta = (float)delta;
                switch (phase)
                {
                    case SchedulerPhase.PreSimulation:
                        rbxApi.PumpPreSimulation(frameDelta);
                        return;
                    case SchedulerPhase.Heartbeat:
                        heartbeatTimestamp = Stopwatch.GetTimestamp();
                        heartbeatAlloc = GC.GetAllocatedBytesForCurrentThread();
                        rbxApi.PumpHeartbeat(frameDelta);
                        return;
                    case SchedulerPhase.InputProcessing:
                        inputTimestamp = Stopwatch.GetTimestamp();
                        inputAlloc = GC.GetAllocatedBytesForCurrentThread();
                        return;
                    case SchedulerPhase.PreRender:
                        rbxApi.PumpPreRender(frameDelta);
                        return;
                }
            };
            scheduler.PhaseReached += onPhase;

            List<ScaleChatRecord> chatRecords = new List<ScaleChatRecord>();
            List<ScaleChatArrival> arrivals = BuildArrivals(actorCount);
            int nextArrival = 0;
            CancellationTokenSource deadline = new CancellationTokenSource();
            List<ScaleFrameSample> samples = new List<ScaleFrameSample>(w.MeasurementFrames);
            List<double> heapTimes = new List<double>();
            List<double> heapBytes = new List<double>();
            List<double> workingSetBytes = new List<double>();
            long previousFrameStart = 0;

            ScaleFrameSample RunFrame(bool measure, int measurementFrame)
            {
                long frameStart = Stopwatch.GetTimestamp();
                long allocStart = GC.GetAllocatedBytesForCurrentThread();
                if (measure)
                {
                    while (nextArrival < arrivals.Count && arrivals[nextArrival].Frame <= measurementFrame)
                    {
                        ScaleChatArrival arrival = arrivals[nextArrival];
                        chatRecords.Add(OfferChat(session, actors[arrival.ActorIndex], arrival, measurementFrame,
                            deadline.Token));
                        nextArrival++;
                    }
                }

                sync.Pump();
                long afterOrchestrator = Stopwatch.GetTimestamp();
                long bridgeBefore = session.Bridge.TicksInside;
                long packetsBefore = session.Bridge.EventsSent;
                long bytesBefore = session.Bridge.PayloadBytes;
                long stepsBefore = session.Observability.GuardedSteps;
                long resumesBefore = session.Observability.ThreadResumes;
                heartbeatTimestamp = 0;
                inputTimestamp = 0;
                scheduler.Advance(frameSeconds);
                long afterScheduler = Stopwatch.GetTimestamp();
                session.ModRuntime.Tick(host, frameSeconds);
                long afterRuntime = Stopwatch.GetTimestamp();
                sync.Pump();
                long frameEnd = Stopwatch.GetTimestamp();
                long allocEnd = GC.GetAllocatedBytesForCurrentThread();

                double signalsMs = heartbeatTimestamp > 0 && inputTimestamp > heartbeatTimestamp
                    ? ScaleMath.TicksToMs(inputTimestamp - heartbeatTimestamp)
                    : 0d;
                ScaleFrameSample sample = new ScaleFrameSample
                {
                    TotalMs = ScaleMath.TicksToMs(frameEnd - frameStart),
                    OrchestratorMs = ScaleMath.TicksToMs(afterOrchestrator - frameStart)
                                     + ScaleMath.TicksToMs(frameEnd - afterRuntime),
                    SchedulerMs = ScaleMath.TicksToMs(afterScheduler - afterOrchestrator) - signalsMs,
                    SignalsMs = signalsMs,
                    NetworkMs = ScaleMath.TicksToMs(session.Bridge.TicksInside - bridgeBefore),
                    ModRuntimeMs = ScaleMath.TicksToMs(afterRuntime - afterScheduler),
                    AllocBytes = allocEnd - allocStart,
                    AllocSignalsBytes = heartbeatTimestamp > 0 ? inputAlloc - heartbeatAlloc : 0,
                    GuardedSteps = session.Observability.GuardedSteps - stepsBefore,
                    ThreadResumes = session.Observability.ThreadResumes - resumesBefore,
                    PacketsSent = session.Bridge.EventsSent - packetsBefore,
                    PayloadBytes = session.Bridge.PayloadBytes - bytesBefore,
                    WallPeriodMs = previousFrameStart > 0 ? ScaleMath.TicksToMs(frameStart - previousFrameStart) : 0d
                };
                previousFrameStart = frameStart;
                Pace(frameStart, periodTicks);
                return sample;
            }

            _log("N=" + actorCount + " repeat " + repeat + ": warm-up " + w.WarmupFrames + " frames");
            for (int frame = 0; frame < w.WarmupFrames; frame++)
            {
                RunFrame(false, -1);
            }

            Dictionary<string, ScaleActorCounters> startCounters = Snapshot(session, host, actors, frameSeconds);
            long serverReceivedStart = session.ModStore.ReadLong(ServerModId, "received");
            long stepsStart = session.Observability.GuardedSteps;
            long resumesStart = session.Observability.ThreadResumes;
            long eventsStart = session.Observability.EventsDelivered;
            long operationsStart = session.Observability.CompletedOperations;
            long packetsStart = session.Bridge.EventsSent;
            long deliveredStart = session.Bridge.EventsDelivered;
            long bytesStart = session.Bridge.PayloadBytes;
            long rateRefusalsStart = session.Bridge.RateRefusals;
            long otherRefusalsStart = session.Bridge.OtherRefusals;
            long handlerErrorsStart = Interlocked.Read(ref handlerErrors);
            long syncStart = sync.Pumped;
            int gen0Start = GC.CollectionCount(0);
            int gen1Start = GC.CollectionCount(1);
            int gen2Start = GC.CollectionCount(2);
            double heapStart = GC.GetTotalMemory(false);
            double workingSetStart = Environment.WorkingSet;
            long measurementStart = Stopwatch.GetTimestamp();

            _log("N=" + actorCount + " repeat " + repeat + ": measuring " + w.MeasurementFrames + " frames");
            for (int frame = 0; frame < w.MeasurementFrames; frame++)
            {
                ScaleFrameSample sample = RunFrame(true, frame);
                samples.Add(sample);
                if (frame % 30 == 0)
                {
                    heapTimes.Add(ScaleMath.TicksToMs(Stopwatch.GetTimestamp() - measurementStart) / 1000d);
                    heapBytes.Add(GC.GetTotalMemory(false));
                    workingSetBytes.Add(Environment.WorkingSet);
                }
            }

            double measurementWallSeconds = ScaleMath.TicksToMs(Stopwatch.GetTimestamp() - measurementStart) / 1000d;
            double heapEnd = GC.GetTotalMemory(false);
            double workingSetEnd = Environment.WorkingSet;
            Dictionary<string, ScaleActorCounters> endCounters = Snapshot(session, host, actors, frameSeconds);
            long serverReceivedEnd = session.ModStore.ReadLong(ServerModId, "received");

            long drainStart = Stopwatch.GetTimestamp();
            int drainFrames = 0;
            while (drainFrames < w.DrainFramesMax && chatRecords.Any(record => record.Outcome == "pending"))
            {
                RunFrame(false, -1);
                drainFrames++;
            }

            int deadlineCancellations = 0;
            if (chatRecords.Any(record => record.Outcome == "pending"))
            {
                deadlineCancellations = chatRecords.Count(record => record.Outcome == "pending");
                _log("N=" + actorCount + " repeat " + repeat + ": cancelling " + deadlineCancellations
                     + " chat requests at the drain deadline");
                deadline.Cancel();
                for (int frame = 0; frame < 30; frame++)
                {
                    RunFrame(false, -1);
                }
            }

            double drainWallSeconds = ScaleMath.TicksToMs(Stopwatch.GetTimestamp() - drainStart) / 1000d;
            scheduler.PhaseReached -= onPhase;

            ScaleRepeatResult result = new ScaleRepeatResult
            {
                ActorCount = actorCount,
                Repeat = repeat,
                MeasuredFrames = samples.Count,
                MeasurementWallSeconds = measurementWallSeconds,
                TotalMs = ScaleDistribution.Of(samples.Select(s => s.TotalMs)),
                OrchestratorMs = ScaleDistribution.Of(samples.Select(s => s.OrchestratorMs)),
                SchedulerMs = ScaleDistribution.Of(samples.Select(s => s.SchedulerMs)),
                SignalsMs = ScaleDistribution.Of(samples.Select(s => s.SignalsMs)),
                NetworkMs = ScaleDistribution.Of(samples.Select(s => s.NetworkMs)),
                ModRuntimeMs = ScaleDistribution.Of(samples.Select(s => s.ModRuntimeMs)),
                AllocBytesPerFrame = ScaleDistribution.Of(samples.Select(s => (double)s.AllocBytes)),
                AllocSignalsBytesPerFrame = ScaleDistribution.Of(samples.Select(s => (double)s.AllocSignalsBytes)),
                GuardedStepsPerFrame = ScaleDistribution.Of(samples.Select(s => (double)s.GuardedSteps)),
                ThreadResumesPerFrame = ScaleDistribution.Of(samples.Select(s => (double)s.ThreadResumes)),
                PacketsPerFrame = ScaleDistribution.Of(samples.Select(s => (double)s.PacketsSent)),
                WallPeriodMs = ScaleDistribution.Of(samples.Skip(1).Select(s => s.WallPeriodMs)),
                GuardedStepsTotal = session.Observability.GuardedSteps - stepsStart,
                ThreadResumesTotal = session.Observability.ThreadResumes - resumesStart,
                SnapshotEventsDelivered = session.Observability.EventsDelivered - eventsStart,
                CompletedOperationsTotal = session.Observability.CompletedOperations - operationsStart,
                PacketsSentTotal = session.Bridge.EventsSent - packetsStart,
                PacketsDeliveredTotal = session.Bridge.EventsDelivered - deliveredStart,
                PayloadBytesTotal = session.Bridge.PayloadBytes - bytesStart,
                RateRefusals = session.Bridge.RateRefusals - rateRefusalsStart,
                BridgeOtherRefusals = session.Bridge.OtherRefusals - otherRefusalsStart,
                HandlerErrors = Interlocked.Read(ref handlerErrors) - handlerErrorsStart,
                HandlerErrorSamples = handlerErrorSamples.ToList(),
                LiveInstancesAtEnd = rbxApi.Registry.Count,
                HeapStartBytes = heapStart,
                HeapEndBytes = heapEnd,
                WorkingSetStartBytes = workingSetStart,
                WorkingSetEndBytes = workingSetEnd,
                Gen0Collections = GC.CollectionCount(0) - gen0Start,
                Gen1Collections = GC.CollectionCount(1) - gen1Start,
                Gen2Collections = GC.CollectionCount(2) - gen2Start,
                ChatDeadlineCancellations = deadlineCancellations,
                DrainWallSeconds = drainWallSeconds,
                SyncContextContinuations = sync.Pumped - syncStart
            };

            double heapSlope = ScaleMath.Slope(heapTimes, heapBytes, out double _, out double _);
            double workingSetSlope = ScaleMath.Slope(heapTimes, workingSetBytes, out double _, out double _);
            result.HeapSlopeMegabytesPerMinute = heapSlope * 60d / (1024d * 1024d);
            result.WorkingSetSlopeMegabytesPerMinute = workingSetSlope * 60d / (1024d * 1024d);

            List<double> heartbeatDeltas = new List<double>();
            List<double> ackDeltas = new List<double>();
            foreach (ScaleActor actor in actors)
            {
                ScaleActorCounters start = startCounters[actor.ActorId];
                ScaleActorCounters end = endCounters[actor.ActorId];
                heartbeatDeltas.Add(end.Heartbeats - start.Heartbeats);
                ackDeltas.Add(end.Acks - start.Acks);
                result.HeartbeatsTotal += end.Heartbeats - start.Heartbeats;
                result.RemoteSentTotal += end.Sent - start.Sent;
                result.AcksTotal += end.Acks - start.Acks;
                result.PartsSpawnedTotal += end.Spawned - start.Spawned;
                result.WaitLoopResumesTotal += end.Loops - start.Loops;
            }

            result.ServerReceivedTotal = serverReceivedEnd - serverReceivedStart;
            result.FairnessHeartbeatMaxMinRatio = ScaleMath.MaxMinRatio(heartbeatDeltas);
            result.FairnessAckMaxMinRatio = ScaleMath.MaxMinRatio(ackDeltas);

            AttachProviderTimestamps(session.ProviderProbe, chatRecords);
            result.Chat = SummarizeChat(chatRecords);
            result.ChatSameActorReplacements = chatRecords.Count(record =>
                record.Outcome == "cancelled" && record.Error.IndexOf("replaced", StringComparison.OrdinalIgnoreCase) >= 0);
            List<double> burstEndToEnd = chatRecords
                .Where(record => record.Pattern == "burst" && record.Outcome == "served")
                .Select(record => record.EndToEndMs)
                .ToList();
            result.FairnessChatEndToEndMaxMinRatio = ScaleMath.MaxMinRatio(burstEndToEnd);

            result.Counters["guardedSteps"] = result.GuardedStepsTotal;
            result.Counters["threadResumes"] = result.ThreadResumesTotal;
            result.Counters["heartbeats"] = result.HeartbeatsTotal;
            result.Counters["remoteSent"] = result.RemoteSentTotal;
            result.Counters["serverReceived"] = result.ServerReceivedTotal;
            result.Counters["acks"] = result.AcksTotal;
            result.Counters["packetsSent"] = result.PacketsSentTotal;
            result.Counters["packetsDelivered"] = result.PacketsDeliveredTotal;
            result.Counters["payloadBytes"] = result.PayloadBytesTotal;
            result.Counters["partsSpawned"] = result.PartsSpawnedTotal;
            result.Counters["waitLoopResumes"] = result.WaitLoopResumesTotal;
            result.Counters["snapshotEventsDelivered"] = result.SnapshotEventsDelivered;
            result.Counters["chatServed"] = result.Chat.Sum(summary => summary.Served);
            result.Counters["chatOffered"] = result.Chat.Sum(summary => summary.Offered);
            result.Counters["chatAdmissionRefusals"] = result.Chat.Sum(summary => summary.AdmissionRefusals);
            result.Counters["syncContextContinuations"] = result.SyncContextContinuations;
            result.Counters["rateRefusals"] = result.RateRefusals;
            result.Counters["handlerErrors"] = result.HandlerErrors;
            result.Counters["completedOperations"] = result.CompletedOperationsTotal;
            result.Counters["liveInstancesAtEnd"] = result.LiveInstancesAtEnd;
            foreach (string counter in w.NonZeroCounters)
            {
                if (!result.Counters.TryGetValue(counter, out long value) || value <= 0)
                {
                    result.ZeroCounters.Add(counter);
                }
            }

            result.Frames.TotalMs = samples.Select(s => Math.Round(s.TotalMs, 4)).ToList();
            result.Frames.OrchestratorMs = samples.Select(s => Math.Round(s.OrchestratorMs, 4)).ToList();
            result.Frames.SchedulerMs = samples.Select(s => Math.Round(s.SchedulerMs, 4)).ToList();
            result.Frames.SignalsMs = samples.Select(s => Math.Round(s.SignalsMs, 4)).ToList();
            result.Frames.NetworkMs = samples.Select(s => Math.Round(s.NetworkMs, 4)).ToList();
            result.Frames.ModRuntimeMs = samples.Select(s => Math.Round(s.ModRuntimeMs, 4)).ToList();
            result.Frames.AllocBytes = samples.Select(s => s.AllocBytes).ToList();
            result.Frames.GuardedSteps = samples.Select(s => s.GuardedSteps).ToList();
            result.Frames.PacketsSent = samples.Select(s => s.PacketsSent).ToList();

            deadline.Dispose();
            _log("N=" + actorCount + " repeat " + repeat + ": median " +
                 result.TotalMs.Median.ToString("0.000", CultureInfo.InvariantCulture) + " ms, p99 " +
                 result.TotalMs.P99.ToString("0.000", CultureInfo.InvariantCulture) + " ms, max " +
                 result.TotalMs.Max.ToString("0.000", CultureInfo.InvariantCulture) + " ms; steps/frame " +
                 result.GuardedStepsPerFrame.Median.ToString("0", CultureInfo.InvariantCulture) +
                 "; chat served " + result.Counters["chatServed"] + "/" + result.Counters["chatOffered"] +
                 "; zero counters: " + (result.ZeroCounters.Count == 0 ? "none" : string.Join(",", result.ZeroCounters)));
            return result;
        }

        private string InstantiateActorSource(int actorIndex)
        {
            ScalePerActorWorkload perActor = _workload.PerActor;
            CultureInfo ci = CultureInfo.InvariantCulture;
            string source = _actorTemplate
                .Replace("__SCALE_ACTOR_INDEX__", (perActor.PhaseOffsetByActorIndex ? actorIndex : 0).ToString(ci))
                .Replace("__SCALE_WORK__", perActor.HeartbeatLoopIterations.ToString(ci))
                .Replace("__SCALE_REMOTE_EVERY__", perActor.RemoteFireEveryFrames.ToString(ci))
                .Replace("__SCALE_SPAWN_EVERY__", perActor.PartSpawnEveryFrames.ToString(ci))
                .Replace("__SCALE_WAIT_SECONDS__", perActor.PersistentWaitSeconds.ToString("0.000000", ci))
                .Replace("__SCALE_SNAPSHOT_EVENT__", _workload.SnapshotEventName);
            EnsureNoPlaceholder(source, "scale_actor.lua");
            return source;
        }

        private static void EnsureNoPlaceholder(string source, string name)
        {
            if (source.Contains("__SCALE_", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(name + " contains an unresolved __SCALE_ placeholder.");
            }
        }

        private List<ScaleChatArrival> BuildArrivals(int actorCount)
        {
            ScaleChatWorkload chat = _workload.Chat;
            List<ScaleChatArrival> arrivals = new List<ScaleChatArrival>(actorCount * 2);
            for (int index = 0; index < actorCount; index++)
            {
                arrivals.Add(new ScaleChatArrival
                {
                    Frame = chat.SynchronizedBurstAtFrame,
                    ActorIndex = index,
                    Pattern = "burst"
                });
            }

            int span = chat.StaggeredWindowEndFrame - chat.StaggeredWindowStartFrame;
            for (int index = 0; index < actorCount; index++)
            {
                arrivals.Add(new ScaleChatArrival
                {
                    Frame = chat.StaggeredWindowStartFrame + (int)Math.Floor(index * (double)span / actorCount),
                    ActorIndex = index,
                    Pattern = "staggered"
                });
            }

            return arrivals.OrderBy(arrival => arrival.Frame).ThenBy(arrival => arrival.ActorIndex).ToList();
        }

        private static ScaleChatRecord OfferChat(ScaleSession session, ScaleActor actor, ScaleChatArrival arrival,
            int frame, CancellationToken deadline)
        {
            ScaleChatRecord record = new ScaleChatRecord
            {
                ActorId = actor.ActorId,
                Pattern = arrival.Pattern,
                OfferedFrame = frame,
                TraceId = "scale-" + arrival.Pattern + "-" + actor.ActorId + "-" +
                          frame.ToString(CultureInfo.InvariantCulture),
                OfferedTimestamp = Stopwatch.GetTimestamp()
            };
            Task<string> task;
            try
            {
                task = session.Orchestrator.RunTaskAsync(new AiTaskRequest
                {
                    RoleId = BuiltInAgentRoleIds.SmartChat,
                    Hint = "scale staircase probe " + record.TraceId,
                    TraceId = record.TraceId,
                    ActorContext = actor.Context,
                    CancellationScope = SharedCancellationScope,
                    DeadlineCancellationToken = deadline,
                    MaxToolCallRoundtrips = 0
                }, deadline);
            }
            catch (AiOrchestrationQueueFullException ex)
            {
                record.Outcome = "refused";
                record.Error = ex.Message;
                record.CompletedTimestamp = Stopwatch.GetTimestamp();
                return record;
            }

            task.ContinueWith(completed =>
            {
                record.CompletedTimestamp = Stopwatch.GetTimestamp();
                if (completed.IsCanceled)
                {
                    record.Outcome = "cancelled";
                    record.Error = "cancelled";
                    return;
                }

                if (completed.IsFaulted)
                {
                    Exception error = completed.Exception?.GetBaseException();
                    record.Error = error?.Message ?? "faulted";
                    record.Outcome = error is AiOrchestrationQueueFullException ? "refused" : "failed";
                    return;
                }

                record.Outcome = completed.Result != null ? "served" : "failed";
                if (completed.Result == null)
                {
                    record.Error = "null result";
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return record;
        }

        private static void AttachProviderTimestamps(G10ProviderProbe probe, List<ScaleChatRecord> records)
        {
            foreach (ScaleChatRecord record in records)
            {
                if (probe.TryGet(record.TraceId, out G10ProviderObservation observation))
                {
                    record.ProviderStartedTimestamp = observation.StartedTimestamp;
                }
            }
        }

        private static List<ScaleChatPatternSummary> SummarizeChat(List<ScaleChatRecord> records)
        {
            List<ScaleChatPatternSummary> summaries = new List<ScaleChatPatternSummary>();
            foreach (string pattern in new[] { "burst", "staggered" })
            {
                List<ScaleChatRecord> subset = records.Where(record => record.Pattern == pattern).ToList();
                List<ScaleChatRecord> served = subset.Where(record => record.Outcome == "served").ToList();
                summaries.Add(new ScaleChatPatternSummary
                {
                    Pattern = pattern,
                    Offered = subset.Count,
                    Served = served.Count,
                    AdmissionRefusals = subset.Count(record => record.Outcome == "refused"),
                    Cancelled = subset.Count(record => record.Outcome == "cancelled"),
                    Failed = subset.Count(record => record.Outcome == "failed"),
                    Pending = subset.Count(record => record.Outcome == "pending"),
                    QueueWaitMs = ScaleDistribution.Of(served.Select(record => record.QueueWaitMs)),
                    EndToEndMs = ScaleDistribution.Of(served.Select(record => record.EndToEndMs))
                });
            }

            return summaries;
        }

        private Dictionary<string, ScaleActorCounters> Snapshot(ScaleSession session, ActorContext host,
            List<ScaleActor> actors, double frameSeconds)
        {
            session.ModRuntime.EmitEvent(host, _workload.SnapshotEventName, "snapshot");
            session.ModRuntime.Tick(host, frameSeconds);
            Dictionary<string, ScaleActorCounters> counters =
                new Dictionary<string, ScaleActorCounters>(StringComparer.Ordinal);
            foreach (ScaleActor actor in actors)
            {
                counters[actor.ActorId] = new ScaleActorCounters
                {
                    ActorId = actor.ActorId,
                    Heartbeats = session.ModStore.ReadLong(actor.ModId, "heartbeats"),
                    Sent = session.ModStore.ReadLong(actor.ModId, "sent"),
                    Acks = session.ModStore.ReadLong(actor.ModId, "acks"),
                    Spawned = session.ModStore.ReadLong(actor.ModId, "spawned"),
                    Loops = session.ModStore.ReadLong(actor.ModId, "loops")
                };
            }

            return counters;
        }

        private static void Pace(long frameStart, long periodTicks)
        {
            long target = frameStart + periodTicks;
            while (true)
            {
                long remaining = target - Stopwatch.GetTimestamp();
                if (remaining <= 0)
                {
                    return;
                }

                if (ScaleMath.TicksToMs(remaining) > 3d)
                {
                    Thread.Sleep(1);
                }
                else
                {
                    Thread.SpinWait(200);
                }
            }
        }

        private sealed class ScaleActor
        {
            public int Index;
            public string ActorId = "";
            public string ModId = "";
            public ActorContext Context;
        }

        private sealed class ScaleChatArrival
        {
            public int Frame;
            public int ActorIndex;
            public string Pattern = "";
        }
    }
}
