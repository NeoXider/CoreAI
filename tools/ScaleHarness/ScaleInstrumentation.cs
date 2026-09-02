using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;
using CoreAI.Messaging;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using CoreAI.Mods.Rbx.Instances.Scheduling;
using CoreAI.Mods.WorldPackages;
using Cysharp.Threading.Tasks;

namespace CoreAI.Tools.Scale
{
    /// <summary>Allocation-free production observability sink: atomic counters only, no samples kept.</summary>
    public sealed class ScaleObservabilitySink : IRbxRuntimeObservabilitySink
    {
        private long _guardedSteps;
        private long _threadResumes;
        private long _eventsDelivered;
        private long _completedOperations;

        public bool IsEnabled => true;

        public long GuardedSteps => Interlocked.Read(ref _guardedSteps);

        public long ThreadResumes => Interlocked.Read(ref _threadResumes);

        public long EventsDelivered => Interlocked.Read(ref _eventsDelivered);

        public long CompletedOperations => Interlocked.Read(ref _completedOperations);

        public void RecordGuardedInstructionSteps(long count)
        {
            Interlocked.Add(ref _guardedSteps, count);
        }

        public void RecordThreadResumes(long count)
        {
            Interlocked.Add(ref _threadResumes, count);
        }

        public void RecordEventsDelivered(long count)
        {
            Interlocked.Add(ref _eventsDelivered, count);
        }

        public void RecordCompletedOperations(long count)
        {
            Interlocked.Add(ref _completedOperations, count);
        }
    }

    /// <summary>
    /// Production loopback bridge wrapped with packet/byte/time counters. Delivery stays synchronous
    /// exactly as <see cref="NullNetworkBridge"/> does it; the decorator only observes.
    /// </summary>
    public sealed class ScaleLoopbackBridge : INetworkBridge
    {
        private readonly NullNetworkBridge _inner;
        private long _eventsSent;
        private long _eventsDelivered;
        private long _payloadBytes;
        private long _requestsSent;
        private long _rateRefusals;
        private long _otherRefusals;
        private long _ticksInside;

        public ScaleLoopbackBridge(int maxClientRequestsPerSecond)
        {
            _inner = new NullNetworkBridge(maxClientRequestsPerSecond);
            _inner.EventReceived += OnInnerEventReceived;
            _inner.RequestReceived += OnInnerRequestReceived;
        }

        public long EventsSent => Interlocked.Read(ref _eventsSent);

        public long EventsDelivered => Interlocked.Read(ref _eventsDelivered);

        public long PayloadBytes => Interlocked.Read(ref _payloadBytes);

        public long RequestsSent => Interlocked.Read(ref _requestsSent);

        public long RateRefusals => Interlocked.Read(ref _rateRefusals);

        public long OtherRefusals => Interlocked.Read(ref _otherRefusals);

        /// <summary>Stopwatch ticks spent inside SendEvent/SendRequest, including synchronous delivery.</summary>
        public long TicksInside => Interlocked.Read(ref _ticksInside);

        public RbxNetworkTopology Topology => _inner.Topology;

        public IReadOnlyList<string> ActorIds => _inner.ActorIds;

        public event Action<RbxNetworkEventMessage> EventReceived;

        public event Action<RbxNetworkRequestMessage, RbxNetworkRequestResponder> RequestReceived;

        public void RegisterActor(string actorId)
        {
            _inner.RegisterActor(actorId);
        }

        public void UnregisterActor(string actorId)
        {
            _inner.UnregisterActor(actorId);
        }

        public void SendEvent(RbxNetworkEventMessage message)
        {
            long started = Stopwatch.GetTimestamp();
            try
            {
                _inner.SendEvent(message);
                Interlocked.Increment(ref _eventsSent);
                Interlocked.Add(ref _payloadBytes, message?.Payload?.Length ?? 0);
            }
            catch (RbxError error)
            {
                if (error.Code == RbxErrorCode.BudgetExceeded)
                {
                    Interlocked.Increment(ref _rateRefusals);
                }
                else
                {
                    Interlocked.Increment(ref _otherRefusals);
                }

                throw;
            }
            finally
            {
                Interlocked.Add(ref _ticksInside, Stopwatch.GetTimestamp() - started);
            }
        }

        public void SendRequest(RbxNetworkRequestMessage message, Action<RbxNetworkResponse> response)
        {
            long started = Stopwatch.GetTimestamp();
            try
            {
                _inner.SendRequest(message, response);
                Interlocked.Increment(ref _requestsSent);
                Interlocked.Add(ref _payloadBytes, message?.Payload?.Length ?? 0);
            }
            catch (RbxError error)
            {
                if (error.Code == RbxErrorCode.BudgetExceeded)
                {
                    Interlocked.Increment(ref _rateRefusals);
                }
                else
                {
                    Interlocked.Increment(ref _otherRefusals);
                }

                throw;
            }
            finally
            {
                Interlocked.Add(ref _ticksInside, Stopwatch.GetTimestamp() - started);
            }
        }

        private void OnInnerEventReceived(RbxNetworkEventMessage message)
        {
            Interlocked.Increment(ref _eventsDelivered);
            EventReceived?.Invoke(message);
        }

        private void OnInnerRequestReceived(RbxNetworkRequestMessage message,
            RbxNetworkRequestResponder responder)
        {
            RequestReceived?.Invoke(message, responder);
        }
    }

    /// <summary>In-memory mod store without a write log so it cannot pollute the heap-slope measurement.</summary>
    public sealed class ScaleMemoryLuaModStore : ILuaModStore
    {
        private readonly ConcurrentDictionary<string, string> _values =
            new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        public string Get(string modId, string key)
        {
            return _values.TryGetValue(BuildKey(modId, key), out string value) ? value : "";
        }

        public void Set(string modId, string key, string value)
        {
            string storageKey = BuildKey(modId, key);
            if (value == null)
            {
                _values.TryRemove(storageKey, out string _);
                return;
            }

            _values[storageKey] = value;
        }

        public void Clear(string modId)
        {
            string prefix = (modId ?? "") + "\n";
            foreach (KeyValuePair<string, string> pair in _values)
            {
                if (pair.Key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    _values.TryRemove(pair.Key, out string _);
                }
            }
        }

        public long ReadLong(string modId, string key)
        {
            return long.TryParse(Get(modId, key), out long value) ? value : 0L;
        }

        private static string BuildKey(string modId, string key)
        {
            return (modId ?? "") + "\n" + (key ?? "");
        }
    }

    /// <summary>
    /// Main-thread synchronization context pumped once per frame by the harness loop, so awaits inside
    /// the production orchestrator land on the frame loop the way Unity's synchronization context
    /// returns continuations to the main thread.
    /// </summary>
    public sealed class PumpedSynchronizationContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<KeyValuePair<SendOrPostCallback, object>> _queue =
            new ConcurrentQueue<KeyValuePair<SendOrPostCallback, object>>();
        private readonly int _mainThreadId;
        private long _pumped;

        public PumpedSynchronizationContext()
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        public long Pumped => Interlocked.Read(ref _pumped);

        public int QueuedCount => _queue.Count;

        public override void Post(SendOrPostCallback d, object state)
        {
            _queue.Enqueue(new KeyValuePair<SendOrPostCallback, object>(d, state));
        }

        public override void Send(SendOrPostCallback d, object state)
        {
            if (Thread.CurrentThread.ManagedThreadId == _mainThreadId)
            {
                d(state);
                return;
            }

            using ManualResetEventSlim done = new ManualResetEventSlim(false);
            Exception failure = null;
            Post(_ =>
            {
                try
                {
                    d(state);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    done.Set();
                }
            }, null);
            done.Wait();
            if (failure != null)
            {
                throw failure;
            }
        }

        public override SynchronizationContext CreateCopy()
        {
            return this;
        }

        /// <summary>Runs every continuation queued so far (and those they queue) on the calling thread.</summary>
        public int Pump()
        {
            int count = 0;
            while (_queue.TryDequeue(out KeyValuePair<SendOrPostCallback, object> item))
            {
                item.Key(item.Value);
                count++;
            }

            Interlocked.Add(ref _pumped, count);
            return count;
        }
    }

    /// <summary>Discards host log output so logging cost does not enter the measurement.</summary>
    public sealed class ScaleSilentGameLogger : IGameLogger
    {
        public void LogDebug(GameLogFeature feature, string message, UnityEngine.Object context = null)
        {
        }

        public void LogInfo(GameLogFeature feature, string message, UnityEngine.Object context = null)
        {
        }

        public void LogWarning(GameLogFeature feature, string message, UnityEngine.Object context = null)
        {
        }

        public void LogError(GameLogFeature feature, string message, UnityEngine.Object context = null)
        {
        }
    }

    /// <summary>No engine behind the harness: world commands are accepted and dropped.</summary>
    public sealed class ScaleNoopCommandSink : IAiGameCommandSink
    {
        public void Publish(ApplyAiGameCommand command)
        {
        }
    }

    /// <summary>Fixed prompts so the orchestrator builds a real request without a role catalog.</summary>
    public sealed class ScalePromptProvider : IAgentSystemPromptProvider, IAgentUserPromptTemplateProvider
    {
        public bool TryGetSystemPrompt(string roleId, out string prompt)
        {
            prompt = "Return one short acknowledgement for the scale staircase measurement.";
            return true;
        }

        public bool TryGetUserTemplate(string roleId, out string template)
        {
            template = "{hint}";
            return true;
        }
    }

    /// <summary>
    /// World-package store for a workload that never saves or loads a world. Writes and loads fail
    /// loudly instead of touching Application.persistentDataPath, which does not exist outside Unity.
    /// </summary>
    public sealed class ScaleWorldPackageStore : IRbxWorldPackageStore
    {
        public UniTask<RbxWorldPackageWriteResult> CreateManualAsync(string slot,
            RbxWorldPackagePayload payload, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("The scale staircase never writes world packages.");
        }

        public UniTask<RbxWorldPackageWriteResult> CreateAutoAsync(string trigger,
            RbxWorldPackagePayload payload, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("The scale staircase never writes world packages.");
        }

        public UniTask<RbxWorldPackagePayload> LoadManualAsync(string slot,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("The scale staircase never loads world packages.");
        }

        public UniTask<RbxWorldPackagePayload> LoadAutoAsync(string fileName,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("The scale staircase never loads world packages.");
        }

        public IReadOnlyList<string> ListManualSlots()
        {
            return Array.Empty<string>();
        }

        public IReadOnlyList<string> ListAutoFiles()
        {
            return Array.Empty<string>();
        }
    }
}
