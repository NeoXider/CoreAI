using System;
using System.Collections.Generic;

namespace CoreAI.Mods.Rbx.Instances.Replication
{
    /// <summary>
    /// A recipient's byte budget as a token bucket: <see cref="BytesPerSecond"/> refill up to
    /// <see cref="BurstBytes"/>. <see cref="Unlimited"/> (a rate of zero) never holds anything back.
    /// </summary>
    public readonly struct ReplicationRecipientBudget
    {
        /// <summary>Creates a budget; a burst at or below zero becomes one second of the rate.</summary>
        public ReplicationRecipientBudget(int bytesPerSecond, int burstBytes)
        {
            BytesPerSecond = Math.Max(0, bytesPerSecond);
            BurstBytes = burstBytes > 0 ? burstBytes : BytesPerSecond;
        }

        public static ReplicationRecipientBudget Unlimited => default;

        public int BytesPerSecond { get; }

        public int BurstBytes { get; }

        public bool IsUnlimited => BytesPerSecond <= 0;
    }

    /// <summary>Configuration of a <see cref="ReplicationPublisher"/>; every member has a working default.</summary>
    public sealed class ReplicationPublisherOptions
    {
        /// <summary>Publish ticks per second; 20 by default, the rate the MVP6 acceptance moves parts at.</summary>
        public double TickRateHz { get; set; } = 20d;

        /// <summary>The budget a recipient added without one of its own gets.</summary>
        public ReplicationRecipientBudget DefaultBudget { get; set; } = ReplicationRecipientBudget.Unlimited;

        /// <summary>The largest packet the encoder may produce; a larger batch faults its recipient.</summary>
        public int MaxPacketBytes { get; set; } = ReplicationWireFormat.DefaultMaxPacketBytes;

        /// <summary>How many after-state dispatches one recipient may have queued before more are refused.</summary>
        public int MaxQueuedAfterStatePerRecipient { get; set; } = 1024;

        /// <summary>Where faults and refusals are reported; null drops the reports.</summary>
        public Action<string> Diagnostics { get; set; }
    }

    /// <summary>Whether a packet is a recipient's whole world or a delta against what it holds.</summary>
    public enum ReplicationPacketKind
    {
        /// <summary>The answer to a join or a resync; the replica arms <see cref="ReplicationApplier.BeginResync"/>.</summary>
        World,

        Delta
    }

    /// <summary>
    /// One packet the publisher hands to its send function. <see cref="Bytes"/> is the publisher's
    /// reused buffer: valid only during the call, so a transport that keeps it must copy it.
    /// </summary>
    public readonly struct ReplicationOutgoingPacket
    {
        public ReplicationOutgoingPacket(string recipientActorId, long sequence, ReplicationPacketKind kind,
            int operationCount, ArraySegment<byte> bytes)
        {
            RecipientActorId = recipientActorId;
            Sequence = sequence;
            Kind = kind;
            OperationCount = operationCount;
            Bytes = bytes;
        }

        public string RecipientActorId { get; }

        public long Sequence { get; }

        public ReplicationPacketKind Kind { get; }

        public int OperationCount { get; }

        public ArraySegment<byte> Bytes { get; }
    }

    /// <summary>Delivers one packet to one recipient, reliably and in order (the bridge's ReliableOrdered channel).</summary>
    public delegate void ReplicationPacketSend(in ReplicationOutgoingPacket packet);

    /// <summary>A read-only view of one recipient's publishing state.</summary>
    public readonly struct ReplicationRecipientInfo
    {
        internal ReplicationRecipientInfo(string actorId, long lastSentSequence, long packetsSent, long bytesSent,
            long deferredTicks, double tokens, bool needsWorld, bool isFaulted, string faultReason,
            int backlogCount, int queuedAfterState, int knownCount)
        {
            ActorId = actorId;
            LastSentSequence = lastSentSequence;
            PacketsSent = packetsSent;
            BytesSent = bytesSent;
            DeferredTicks = deferredTicks;
            Tokens = tokens;
            NeedsWorld = needsWorld;
            IsFaulted = isFaulted;
            FaultReason = faultReason;
            BacklogCount = backlogCount;
            QueuedAfterState = queuedAfterState;
            KnownCount = knownCount;
        }

        public string ActorId { get; }

        public long LastSentSequence { get; }

        public long PacketsSent { get; }

        public long BytesSent { get; }

        /// <summary>Ticks at which the budget held this recipient's changes back.</summary>
        public long DeferredTicks { get; }

        /// <summary>The token bucket's level in bytes; negative while a large packet is being paid off.</summary>
        public double Tokens { get; }

        public bool NeedsWorld { get; }

        public bool IsFaulted { get; }

        public string FaultReason { get; }

        /// <summary>Instances whose changes are held back for the recipient's next packet.</summary>
        public int BacklogCount { get; }

        public int QueuedAfterState { get; }

        public int KnownCount { get; }
    }

    /// <summary>
    /// Publishes world state per tick: at each tick of an injected clock it plans every recipient's
    /// batch from one shared <see cref="ReplicationDirtySet"/>, encodes it with
    /// <see cref="ReplicationWireEncoder"/> and hands the bytes to an injected send function, then
    /// clears the dirty set. Engine-free and single-threaded; call <see cref="Update"/> once a frame.
    /// </summary>
    /// <remarks>
    /// Coalescing: every change between two ticks is one record per instance with values read at
    /// the tick, so the last write of a member wins; an instance spawned and removed between two
    /// ticks sends nothing; a removal wins over every change to the same instance.
    /// <para>
    /// Ordering, per recipient: packets carry consecutive sequences and are handed to the send
    /// function in that order; a recipient's first packet is its world; records keep the plan's
    /// order (spawns and patches parent-first, removals last); a change the budget held back goes out
    /// merged into the recipient's next packet, never after a later one; an action queued with
    /// <see cref="EnqueueAfterState"/> runs right after the first packet planned at or after the
    /// queueing, so a remote event fired after a write reaches the client after that write.
    /// </para>
    /// <para>
    /// Budgets: a recipient whose token bucket is empty is skipped for the tick and its changes are
    /// merged into a backlog by the dirty set's rules; a world packet is always sent when due and paid
    /// for afterwards, because nothing else can be delivered before it.
    /// </para>
    /// <para>
    /// WHY a recipient whose packet cannot be encoded or sent is faulted rather than retried: the
    /// stream committed that batch, so the replica will see a gap, and every later delta would be
    /// planned against state it does not hold. The fault is reported once and lasts until
    /// <see cref="RequestWorld"/>, which is also the answer to a replica's own resync request.
    /// </para>
    /// </remarks>
    public sealed class ReplicationPublisher
    {
        private sealed class Recipient
        {
            public Recipient(string actorId, ReplicationStream stream, ReplicationRecipientBudget budget, double now)
            {
                ActorId = actorId;
                Stream = stream;
                Budget = budget;
                Tokens = budget.BurstBytes;
                LastRefillAt = now;
                NeedsWorld = true;
            }

            public string ActorId { get; }

            public ReplicationStream Stream { get; }

            public ReplicationRecipientBudget Budget { get; }

            public double Tokens;

            public double LastRefillAt;

            public bool NeedsWorld;

            public bool Faulted;

            public string FaultReason = "";

            public ReplicationDeltaBacklog Backlog;

            public Queue<Action> AfterState;

            public long LastSentSequence;

            public long PacketsSent;

            public long BytesSent;

            public long DeferredTicks;
        }

        private readonly ReplicationDirtySet _dirty;
        private readonly IReplicationWireSource _source;
        private readonly Func<double> _clock;
        private readonly ReplicationPacketSend _send;
        private readonly ReplicationWireEncoder _encoder;
        private readonly ReplicationPublisherOptions _options;
        private readonly List<Recipient> _recipients = new();
        private readonly Dictionary<string, Recipient> _byActor = new(StringComparer.Ordinal);
        private double _nextTickAt;
        private bool _started;
        private bool _ticking;

        /// <summary>
        /// Creates a publisher over one dirty set. <paramref name="clock"/> returns monotonic seconds
        /// (in production <c>IRbxClockSource.ProcessTimeSeconds</c>); <paramref name="source"/> reads
        /// the values each batch names, by default the dirty set's registry with no part layer.
        /// </summary>
        public ReplicationPublisher(ReplicationDirtySet dirty, Func<double> clock, ReplicationPacketSend send,
            IReplicationWireSource source = null, ReplicationPublisherOptions options = null)
        {
            _dirty = dirty ?? throw new ArgumentNullException(nameof(dirty));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _send = send ?? throw new ArgumentNullException(nameof(send));
            _options = options ?? new ReplicationPublisherOptions();
            if (!(_options.TickRateHz > 0d) || double.IsInfinity(_options.TickRateHz))
            {
                throw new ArgumentOutOfRangeException(nameof(options), _options.TickRateHz,
                    "The publish tick rate must be a positive, finite number of ticks per second.");
            }

            _source = source ?? new RegistryReplicationWireSource(dirty.Registry);
            _encoder = new ReplicationWireEncoder(_options.MaxPacketBytes);
            TickIntervalSeconds = 1d / _options.TickRateHz;
        }

        public double TickIntervalSeconds { get; }

        public long TickCount { get; private set; }

        public int RecipientCount => _recipients.Count;

        /// <summary>
        /// Starts publishing to a recipient: its first packet, at the next tick, is its whole visible
        /// world (the join snapshot). Not allowed while a tick runs.
        /// </summary>
        public void AddRecipient(string actorId, ReplicationRecipientBudget? budget = null)
        {
            ThrowIfTicking();
            if (string.IsNullOrWhiteSpace(actorId))
            {
                throw new ArgumentException("Recipient actor id is required.", nameof(actorId));
            }

            string key = actorId.Trim();
            if (_byActor.ContainsKey(key))
            {
                throw new ArgumentException("Recipient '" + key + "' is already published to.", nameof(actorId));
            }

            Recipient recipient = new(key, new ReplicationStream(_dirty, key), budget ?? _options.DefaultBudget,
                _clock());
            _recipients.Add(recipient);
            _byActor.Add(key, recipient);
        }

        /// <summary>Stops publishing to a recipient and drops what was queued for it.</summary>
        public bool RemoveRecipient(string actorId)
        {
            ThrowIfTicking();
            if (actorId == null || !_byActor.TryGetValue(actorId.Trim(), out Recipient recipient))
            {
                return false;
            }

            _byActor.Remove(recipient.ActorId);
            _recipients.Remove(recipient);
            return true;
        }

        /// <summary>
        /// Sends the recipient its whole world again at the next tick: the answer to its replica's
        /// <see cref="ReplicationApplier.ResyncRequested"/>, and the only way out of a fault.
        /// </summary>
        public bool RequestWorld(string actorId)
        {
            if (actorId == null || !_byActor.TryGetValue(actorId.Trim(), out Recipient recipient))
            {
                return false;
            }

            recipient.NeedsWorld = true;
            recipient.Faulted = false;
            recipient.FaultReason = "";
            return true;
        }

        /// <summary>
        /// Queues an action to run right after the recipient's next packet is handed to the send
        /// function — or at its next tick when it has nothing to send — so whatever the action sends
        /// arrives after every change made before it was queued. False when the recipient is unknown
        /// or its queue is full.
        /// </summary>
        public bool EnqueueAfterState(string actorId, Action dispatch)
        {
            if (dispatch == null)
            {
                throw new ArgumentNullException(nameof(dispatch));
            }

            if (actorId == null || !_byActor.TryGetValue(actorId.Trim(), out Recipient recipient))
            {
                return false;
            }

            recipient.AfterState ??= new Queue<Action>();
            if (recipient.AfterState.Count >= _options.MaxQueuedAfterStatePerRecipient)
            {
                Report("[CoreAI.RbxApi] replication publisher refused an after-state dispatch for '"
                       + recipient.ActorId + "': " + recipient.AfterState.Count + " are already queued");
                return false;
            }

            recipient.AfterState.Enqueue(dispatch);
            return true;
        }

        public bool TryGetRecipient(string actorId, out ReplicationRecipientInfo info)
        {
            if (actorId == null || !_byActor.TryGetValue(actorId.Trim(), out Recipient recipient))
            {
                info = default;
                return false;
            }

            info = new ReplicationRecipientInfo(recipient.ActorId, recipient.LastSentSequence,
                recipient.PacketsSent, recipient.BytesSent, recipient.DeferredTicks, recipient.Tokens,
                recipient.NeedsWorld, recipient.Faulted, recipient.FaultReason,
                recipient.Backlog?.Count ?? 0, recipient.AfterState?.Count ?? 0, recipient.Stream.KnownCount);
            return true;
        }

        /// <summary>
        /// Runs one tick when the clock has reached it and returns whether it did. At most one tick
        /// runs per call: a publisher that fell behind publishes once, with everything coalesced, and
        /// schedules the next tick one interval from now.
        /// </summary>
        public bool Update()
        {
            double now = _clock();
            if (double.IsNaN(now))
            {
                return false;
            }

            if (!_started)
            {
                _started = true;
                _nextTickAt = now;
            }

            if (now < _nextTickAt)
            {
                return false;
            }

            RunTick(now);
            _nextTickAt += TickIntervalSeconds;
            if (_nextTickAt <= now)
            {
                _nextTickAt = now + TickIntervalSeconds;
            }

            return true;
        }

        /// <summary>Runs one tick now, off the schedule: a flush before shutdown, or a test step.</summary>
        public void PublishNow()
        {
            RunTick(_clock());
        }

        private void RunTick(double now)
        {
            ThrowIfTicking();
            _ticking = true;
            try
            {
                TickCount++;
                IReadOnlyList<ReplicationDelta> pending = _dirty.Pending;
                for (int index = 0; index < _recipients.Count; index++)
                {
                    Serve(_recipients[index], pending, now);
                }
            }
            finally
            {
                _ticking = false;
                _dirty.Clear();
            }
        }

        private void Serve(Recipient recipient, IReadOnlyList<ReplicationDelta> pending, double now)
        {
            Refill(recipient, now);
            if (recipient.Faulted)
            {
                return;
            }

            ReplicationBatchPlan plan;
            ReplicationPacketKind kind;
            try
            {
                if (recipient.NeedsWorld)
                {
                    recipient.NeedsWorld = false;
                    recipient.Backlog?.Clear();
                    plan = recipient.Stream.PlanWorld();
                    kind = ReplicationPacketKind.World;
                }
                else if (!recipient.Budget.IsUnlimited && recipient.Tokens <= 0d)
                {
                    recipient.Backlog ??= new ReplicationDeltaBacklog();
                    recipient.Backlog.Merge(pending);
                    recipient.DeferredTicks++;
                    return;
                }
                else
                {
                    IReadOnlyList<ReplicationDelta> due = recipient.Backlog != null && recipient.Backlog.Count > 0
                        ? recipient.Backlog.Drain(pending)
                        : pending;
                    plan = recipient.Stream.Plan(due);
                    kind = ReplicationPacketKind.Delta;
                }
            }
            catch (Exception exception)
            {
                Fault(recipient, "planning failed (" + exception.GetType().Name + "): " + exception.Message);
                return;
            }

            if (plan != null && !Send(recipient, plan, kind))
            {
                return;
            }

            DispatchAfterState(recipient);
        }

        private bool Send(Recipient recipient, ReplicationBatchPlan plan, ReplicationPacketKind kind)
        {
            ArraySegment<byte> bytes;
            try
            {
                bytes = _encoder.Encode(plan, _source, kind == ReplicationPacketKind.World);
            }
            catch (Exception exception)
            {
                string refusal = exception is ReplicationWireException wire
                    ? wire.Error.ToString()
                    : exception.GetType().Name;
                Fault(recipient, "batch " + plan.Sequence + " could not be encoded (" + refusal + "): "
                                 + exception.Message);
                return false;
            }

            ReplicationOutgoingPacket packet = new(recipient.ActorId, plan.Sequence, kind,
                plan.Operations.Count, bytes);
            try
            {
                _send(in packet);
            }
            catch (Exception exception)
            {
                Fault(recipient, "batch " + plan.Sequence + " could not be sent ("
                                 + exception.GetType().Name + "): " + exception.Message);
                return false;
            }

            recipient.LastSentSequence = plan.Sequence;
            recipient.PacketsSent++;
            recipient.BytesSent += bytes.Count;
            if (!recipient.Budget.IsUnlimited)
            {
                recipient.Tokens -= bytes.Count;
            }

            return true;
        }

        private void DispatchAfterState(Recipient recipient)
        {
            Queue<Action> queue = recipient.AfterState;
            if (queue == null || queue.Count == 0)
            {
                return;
            }

            // WHY only what was queued before this point runs now: an action that queues another must
            // not run it ahead of the state the next tick carries.
            int due = queue.Count;
            for (int index = 0; index < due; index++)
            {
                Action dispatch = queue.Dequeue();
                try
                {
                    dispatch();
                }
                catch (Exception exception)
                {
                    Report("[CoreAI.RbxApi] an after-state dispatch for '" + recipient.ActorId + "' threw: "
                           + exception);
                }
            }
        }

        private void Refill(Recipient recipient, double now)
        {
            if (recipient.Budget.IsUnlimited)
            {
                return;
            }

            double elapsed = now - recipient.LastRefillAt;
            if (elapsed > 0d)
            {
                recipient.Tokens = Math.Min(recipient.Budget.BurstBytes,
                    recipient.Tokens + recipient.Budget.BytesPerSecond * elapsed);
                recipient.LastRefillAt = now;
            }
        }

        private void Fault(Recipient recipient, string reason)
        {
            recipient.Faulted = true;
            recipient.FaultReason = reason;
            recipient.Backlog?.Clear();
            Report("[CoreAI.RbxApi] replication to '" + recipient.ActorId + "' stopped until its world is "
                   + "requested again: " + reason);
        }

        private void Report(string message)
        {
            Action<string> sink = _options.Diagnostics;
            if (sink == null)
            {
                return;
            }

            try
            {
                sink(message);
            }
            catch (Exception)
            {
                // WHY a throwing sink is ignored: the report is about a recipient's fault, and the
                // host's logger must not turn one recipient's fault into a failed tick for all.
            }
        }

        private void ThrowIfTicking()
        {
            if (_ticking)
            {
                throw new InvalidOperationException(
                    "The replication publisher is in the middle of a tick; change recipients after it.");
            }
        }
    }

    /// <summary>
    /// The changes a budget held back for one recipient, merged across ticks by the dirty set's own
    /// rules: the newest revision wins, a removal is never downgraded, named members are a union and
    /// a whole-node mark is kept alongside them.
    /// </summary>
    internal sealed class ReplicationDeltaBacklog
    {
        private sealed class Entry
        {
            public long Revision;
            public bool Removed;
            public bool WholeNode;
            public List<string> Members;
        }

        private readonly Dictionary<ulong, Entry> _entries = new();
        private readonly List<ulong> _order = new();

        public int Count => _order.Count;

        public void Merge(IReadOnlyList<ReplicationDelta> deltas)
        {
            for (int index = 0; index < deltas.Count; index++)
            {
                Merge(deltas[index]);
            }
        }

        /// <summary>Merges the current step in, returns everything as one list in first-seen order, and empties the backlog.</summary>
        public IReadOnlyList<ReplicationDelta> Drain(IReadOnlyList<ReplicationDelta> current)
        {
            Merge(current);
            ReplicationDelta[] merged = new ReplicationDelta[_order.Count];
            for (int index = 0; index < _order.Count; index++)
            {
                ulong id = _order[index];
                Entry entry = _entries[id];
                merged[index] = new ReplicationDelta(new InstanceId(id), entry.Revision, entry.Removed,
                    entry.WholeNode, entry.Members);
            }

            Clear();
            return merged;
        }

        public void Clear()
        {
            _entries.Clear();
            _order.Clear();
        }

        private void Merge(ReplicationDelta delta)
        {
            ulong id = delta.InstanceId.Value;
            if (!_entries.TryGetValue(id, out Entry entry))
            {
                entry = new Entry
                {
                    Revision = delta.Revision,
                    Removed = delta.Removed,
                    WholeNode = !delta.Removed && delta.IsWholeNode
                };
                if (!delta.Removed && delta.Members.Count > 0)
                {
                    entry.Members = new List<string>(delta.Members);
                }

                _entries.Add(id, entry);
                _order.Add(id);
                return;
            }

            if (delta.Revision > entry.Revision)
            {
                entry.Revision = delta.Revision;
            }

            if (entry.Removed)
            {
                return;
            }

            if (delta.Removed)
            {
                entry.Removed = true;
                entry.WholeNode = false;
                entry.Members = null;
                return;
            }

            entry.WholeNode |= delta.IsWholeNode;
            for (int index = 0; index < delta.Members.Count; index++)
            {
                entry.Members ??= new List<string>();
                if (!entry.Members.Contains(delta.Members[index]))
                {
                    entry.Members.Add(delta.Members[index]);
                }
            }
        }
    }
}
