using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Instances.Networking;
using UnityEngine;

namespace CoreAI.Net.Mirror
{
    /// <summary>
    /// How much one admitted connection may send the server: remotes per second, payload bytes per
    /// second, and how long a burst above those rates is absorbed. What exceeds them is dropped and
    /// counted before the world decodes or dispatches it.
    /// </summary>
    /// <remarks>
    /// WHY on the server's receive path when every client already limits its own sends: the client's
    /// limiter (<see cref="RbxNetworkRateLimiter"/>) is the client's code, and a custom client simply
    /// does not run it. WHY the defaults sit above what an honest client can send: the client's
    /// budget is <see cref="RbxNetworkRateLimiter.DefaultMaxClientRequestsPerSecond"/> per kind of
    /// remote, counted in whole-second windows, so an honest client can put three kinds' worth on the
    /// wire in a second, and twice that across the edge of two windows — the defaults absorb exactly
    /// that, so only a client that bypasses its own limiter meets this one.
    /// </remarks>
    [Serializable]
    public sealed class CoreAiMirrorInboundLimits
    {
        /// <summary>Remotes per second per connection by default: three remote kinds' client budget.</summary>
        public const float DefaultMessagesPerSecond = 3f * RbxNetworkRateLimiter.DefaultMaxClientRequestsPerSecond;

        /// <summary>Payload bytes per second per connection by default: 1 MiB.</summary>
        public const float DefaultBytesPerSecond = 1048576f;

        /// <summary>Seconds of the rates a connection may send at once by default.</summary>
        public const float DefaultBurstSeconds = 2f;

        [Tooltip("Whether the server meters what each admitted connection sends it. Off, decoding and "
            + "dispatch are bounded only by the world's own handler budgets.")]
        [SerializeField]
        private bool enabled = true;

        [Tooltip("Remotes (RemoteEvent fires and RemoteFunction calls) one connection may send per "
            + "second; more are dropped and counted.")]
        [SerializeField]
        private float messagesPerSecond = DefaultMessagesPerSecond;

        [Tooltip("Payload bytes one connection may send per second; more are dropped and counted.")]
        [SerializeField]
        private float bytesPerSecond = DefaultBytesPerSecond;

        [Tooltip("Seconds' worth of both rates a connection may send at once before drops begin.")]
        [SerializeField]
        private float burstSeconds = DefaultBurstSeconds;

        /// <summary>Limits that meter nothing.</summary>
        public static CoreAiMirrorInboundLimits Unlimited => new() { Enabled = false };

        /// <summary>Whether the server meters inbound remotes at all.</summary>
        public bool Enabled
        {
            get => enabled;
            set => enabled = value;
        }

        /// <summary>Remotes one connection may send per second.</summary>
        public float MessagesPerSecond
        {
            get => messagesPerSecond > 0f ? messagesPerSecond : DefaultMessagesPerSecond;
            set => messagesPerSecond = RequirePositive(value, nameof(MessagesPerSecond));
        }

        /// <summary>Payload bytes one connection may send per second.</summary>
        public float BytesPerSecond
        {
            get => bytesPerSecond > 0f ? bytesPerSecond : DefaultBytesPerSecond;
            set => bytesPerSecond = RequirePositive(value, nameof(BytesPerSecond));
        }

        /// <summary>Seconds of both rates a connection may send at once.</summary>
        public float BurstSeconds
        {
            get => burstSeconds > 0f ? burstSeconds : DefaultBurstSeconds;
            set => burstSeconds = RequirePositive(value, nameof(BurstSeconds));
        }

        /// <summary>
        /// The most remotes a full bucket admits at once: the burst, and at least one.
        /// </summary>
        public double MessageCapacity => Math.Max(1d, (double)MessagesPerSecond * BurstSeconds);

        /// <summary>
        /// The most payload bytes a full bucket admits at once: the burst, and never less than one
        /// largest payload a remote may carry, so a full bucket always admits any legal remote.
        /// </summary>
        public double ByteCapacity => Math.Max(MirrorNetworkBridge.CodecPayloadCeilingBytes,
            (double)BytesPerSecond * BurstSeconds);

        /// <summary>A copy, so a bridge built from these limits is not changed by later edits.</summary>
        public CoreAiMirrorInboundLimits Clone()
        {
            return new CoreAiMirrorInboundLimits
            {
                enabled = enabled,
                messagesPerSecond = MessagesPerSecond,
                bytesPerSecond = BytesPerSecond,
                burstSeconds = BurstSeconds
            };
        }

        private static float RequirePositive(float value, string name)
        {
            if (!(value > 0f) || float.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(name, value,
                    "an inbound limit must be a positive, finite number; turn metering off with Enabled instead");
            }

            return value;
        }
    }

    /// <summary>What one connection has sent the server, as the inbound meter counted it.</summary>
    public readonly struct CoreAiMirrorInboundTraffic
    {
        /// <summary>Creates a snapshot.</summary>
        public CoreAiMirrorInboundTraffic(int connectionId, long messagesAccepted, long bytesAccepted,
            long messagesDropped, long bytesDropped)
        {
            ConnectionId = connectionId;
            MessagesAccepted = messagesAccepted;
            BytesAccepted = bytesAccepted;
            MessagesDropped = messagesDropped;
            BytesDropped = bytesDropped;
        }

        /// <summary>The connection.</summary>
        public int ConnectionId { get; }

        /// <summary>Remotes the meter let through to decoding and dispatch.</summary>
        public long MessagesAccepted { get; }

        /// <summary>Payload bytes of those remotes.</summary>
        public long BytesAccepted { get; }

        /// <summary>Remotes dropped for exceeding the connection's limits.</summary>
        public long MessagesDropped { get; }

        /// <summary>Payload bytes of those remotes.</summary>
        public long BytesDropped { get; }
    }

    /// <summary>
    /// The per-connection token buckets behind <see cref="CoreAiMirrorInboundLimits"/>: one for
    /// remotes and one for payload bytes, each refilled at its rate up to its capacity.
    /// </summary>
    /// <remarks>
    /// WHY token buckets and not the client limiter's fixed windows: a fixed window admits twice its
    /// budget across the edge of two windows, which is the burst a flood is made of; a bucket
    /// admits its capacity once and then only its rate. WHY no allocation per remote: a flood is
    /// exactly what this runs under, so a remote costs two subtractions and a comparison.
    /// </remarks>
    internal sealed class CoreAiMirrorInboundMeter
    {
        private sealed class Bucket
        {
            public double Messages;
            public double Bytes;
            public double RefilledAt;
            public long MessagesAccepted;
            public long BytesAccepted;
            public long MessagesDropped;
            public long BytesDropped;
            public long DroppedSinceReport;
            public long DroppedBytesSinceReport;
            public double ReportedAt = double.NegativeInfinity;
        }

        private readonly Dictionary<int, Bucket> _buckets = new();
        private readonly CoreAiMirrorInboundLimits _limits;

        public CoreAiMirrorInboundMeter(CoreAiMirrorInboundLimits limits)
        {
            _limits = (limits ?? new CoreAiMirrorInboundLimits()).Clone();
        }

        /// <summary>A copy of the limits this meter enforces.</summary>
        public CoreAiMirrorInboundLimits Limits => _limits.Clone();

        /// <summary>Remotes dropped across every connection since the meter was built.</summary>
        public int MessagesDropped { get; private set; }

        /// <summary>Payload bytes of those remotes.</summary>
        public long BytesDropped { get; private set; }

        /// <summary>
        /// Counts one remote of <paramref name="bytes"/> payload bytes against its connection, and
        /// says whether it may be decoded and dispatched.
        /// </summary>
        public bool TryAdmit(int connectionId, int bytes, double now)
        {
            if (!_limits.Enabled)
            {
                return true;
            }

            if (!_buckets.TryGetValue(connectionId, out Bucket bucket))
            {
                bucket = new Bucket
                {
                    Messages = _limits.MessageCapacity,
                    Bytes = _limits.ByteCapacity,
                    RefilledAt = now
                };
                _buckets.Add(connectionId, bucket);
            }

            Refill(bucket, now);
            double size = bytes > 0 ? bytes : 0;
            if (bucket.Messages >= 1d && bucket.Bytes >= size)
            {
                bucket.Messages -= 1d;
                bucket.Bytes -= size;
                bucket.MessagesAccepted++;
                bucket.BytesAccepted += (long)size;
                return true;
            }

            bucket.MessagesDropped++;
            bucket.BytesDropped += (long)size;
            bucket.DroppedSinceReport++;
            bucket.DroppedBytesSinceReport += (long)size;
            MessagesDropped++;
            BytesDropped += (long)size;
            return false;
        }

        /// <summary>
        /// Whether the drops of one connection are due to be said again, at most once per
        /// <paramref name="intervalSeconds"/>; when they are, hands back and clears what was
        /// dropped since the last time.
        /// </summary>
        public bool TryTakeReport(int connectionId, double now, double intervalSeconds,
            out long dropped, out long droppedBytes)
        {
            dropped = 0L;
            droppedBytes = 0L;
            if (!_buckets.TryGetValue(connectionId, out Bucket bucket) || bucket.DroppedSinceReport == 0L
                || (now - bucket.ReportedAt < intervalSeconds && now >= bucket.ReportedAt))
            {
                return false;
            }

            dropped = bucket.DroppedSinceReport;
            droppedBytes = bucket.DroppedBytesSinceReport;
            bucket.DroppedSinceReport = 0L;
            bucket.DroppedBytesSinceReport = 0L;
            bucket.ReportedAt = now;
            return true;
        }

        /// <summary>The traffic one connection has sent since it was bound; false when it sent nothing metered.</summary>
        public bool TryGetTraffic(int connectionId, out CoreAiMirrorInboundTraffic traffic)
        {
            if (!_buckets.TryGetValue(connectionId, out Bucket bucket))
            {
                traffic = default;
                return false;
            }

            traffic = new CoreAiMirrorInboundTraffic(connectionId, bucket.MessagesAccepted,
                bucket.BytesAccepted, bucket.MessagesDropped, bucket.BytesDropped);
            return true;
        }

        /// <summary>Every metered connection's traffic.</summary>
        public List<CoreAiMirrorInboundTraffic> Snapshot()
        {
            List<CoreAiMirrorInboundTraffic> traffic = new(_buckets.Count);
            foreach (KeyValuePair<int, Bucket> pair in _buckets)
            {
                traffic.Add(new CoreAiMirrorInboundTraffic(pair.Key, pair.Value.MessagesAccepted,
                    pair.Value.BytesAccepted, pair.Value.MessagesDropped, pair.Value.BytesDropped));
            }

            return traffic;
        }

        /// <summary>Forgets a connection, so the next peer on its id starts with a full bucket.</summary>
        public void Forget(int connectionId)
        {
            _buckets.Remove(connectionId);
        }

        /// <summary>Refills a bucket for the time since its last refill.</summary>
        /// <remarks>
        /// WHY a clock that stepped back only moves the mark: refilling on it would grant time that
        /// never passed, and keeping the old mark would freeze the bucket until the clock caught up.
        /// </remarks>
        private void Refill(Bucket bucket, double now)
        {
            double elapsed = now - bucket.RefilledAt;
            bucket.RefilledAt = now;
            if (!(elapsed > 0d))
            {
                return;
            }

            bucket.Messages = Math.Min(_limits.MessageCapacity, bucket.Messages + elapsed * _limits.MessagesPerSecond);
            bucket.Bytes = Math.Min(_limits.ByteCapacity, bucket.Bytes + elapsed * _limits.BytesPerSecond);
        }
    }
}
