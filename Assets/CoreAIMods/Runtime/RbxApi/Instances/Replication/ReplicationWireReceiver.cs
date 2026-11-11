using System;

namespace CoreAI.Mods.Rbx.Instances.Replication
{
    /// <summary>What a <see cref="ReplicationWireReceiver"/> did with one packet.</summary>
    public readonly struct ReplicationReceiveResult
    {
        internal ReplicationReceiveResult(ReplicationWireError rejection, string detail, ReplicationWireBatch batch,
            ReplicationApplyResult applied, int partsApplied)
        {
            Rejection = rejection;
            Detail = detail ?? "";
            Batch = batch;
            Applied = applied;
            PartsApplied = partsApplied;
        }

        /// <summary>Why the decoder refused the packet; <see cref="ReplicationWireError.None"/> when it decoded.</summary>
        public ReplicationWireError Rejection { get; }

        public string Detail { get; }

        /// <summary>The decoded batch; null when the packet was refused.</summary>
        public ReplicationWireBatch Batch { get; }

        /// <summary>What the applier did with the batch; null when the packet was refused.</summary>
        public ReplicationApplyResult Applied { get; }

        /// <summary>How many part states reached the part sink.</summary>
        public int PartsApplied { get; }

        public bool IsRejected => Rejection != ReplicationWireError.None;

        public bool IsApplied => Applied != null && Applied.IsApplied;
    }

    /// <summary>
    /// The replica's end of the wire: decodes a packet, arms a resync for a world packet, applies the
    /// batch through <see cref="ReplicationApplier"/> and then hands the part members it carried to an
    /// optional <see cref="IReplicationPartSink"/>. Engine-free and single-threaded.
    /// </summary>
    /// <remarks>
    /// WHY a refused packet asks for nothing by itself: its sequence is lost with it, so the next
    /// packet that decodes is one the applier did not expect, and the applier's own gap detection asks
    /// for the world on the one path every other loss already takes.
    /// </remarks>
    public sealed class ReplicationWireReceiver : IDisposable
    {
        private readonly InstanceRegistry _replica;
        private readonly IReplicationPartSink _parts;
        private readonly ReplicationWireDecoder _decoder;

        public ReplicationWireReceiver(InstanceRegistry replica, string localActorId,
            IReplicationPartSink parts = null, int maxPacketBytes = ReplicationWireFormat.DefaultMaxPacketBytes)
        {
            _replica = replica ?? throw new ArgumentNullException(nameof(replica));
            if (string.IsNullOrWhiteSpace(localActorId))
            {
                throw new ArgumentException("The local actor id is required.", nameof(localActorId));
            }

            LocalActorId = localActorId.Trim();
            _parts = parts;
            _decoder = new ReplicationWireDecoder(maxPacketBytes);
            Applier = new ReplicationApplier(replica);
        }

        public string LocalActorId { get; }

        public ReplicationApplier Applier { get; }

        public long RejectedPackets { get; private set; }

        public ReplicationWireError LastRejection { get; private set; }

        /// <summary>Raised when the replica needs its world again; the host forwards it to the server.</summary>
        public event Action<string> ResyncRequested
        {
            add => Applier.ResyncRequested += value;
            remove => Applier.ResyncRequested -= value;
        }

        public ReplicationReceiveResult Receive(ReadOnlySpan<byte> packet)
        {
            if (!_decoder.TryDecode(packet, LocalActorId, out ReplicationWireBatch batch,
                    out ReplicationWireError error, out string detail))
            {
                RejectedPackets++;
                LastRejection = error;
                // WHY powers of two: a peer that keeps sending bad packets would otherwise write the
                // replica's log at its own packet rate; the counter keeps the exact total.
                if ((RejectedPackets & (RejectedPackets - 1L)) == 0L)
                {
                    _replica.ReportDiagnostic("[CoreAI.RbxApi] replica refused a replication packet ("
                                              + RejectedPackets + " so far): " + error + ": " + detail);
                }

                return new ReplicationReceiveResult(error, detail, null, null, 0);
            }

            if (batch.IsWorld && batch.Sequence >= Applier.ExpectedSequence)
            {
                Applier.BeginResync(batch.Sequence);
            }

            ReplicationApplyResult applied = Applier.Apply(batch.Plan, batch);
            int partsApplied = applied.IsApplied ? ApplyParts(batch) : 0;
            return new ReplicationReceiveResult(ReplicationWireError.None, "", batch, applied, partsApplied);
        }

        public void Dispose()
        {
            Applier.Dispose();
        }

        private int ApplyParts(ReplicationWireBatch batch)
        {
            if (_parts == null || batch.PartStateCount == 0)
            {
                return 0;
            }

            int applied = 0;
            using (_replica.BeginReplicationApply())
            {
                for (int index = 0; index < batch.PartStateCount; index++)
                {
                    ReplicationWirePartEntry entry = batch.GetPartState(index);
                    if (!_replica.TryGet(entry.InstanceId, out RbxInstance instance) || instance.IsDestroyed)
                    {
                        continue;
                    }

                    try
                    {
                        ReplicationPartState state = entry.State;
                        _parts.ApplyPart(instance, in state);
                        applied++;
                    }
                    catch (Exception exception)
                    {
                        // WHY reported and not a resync: the engine-free state already applied and agrees
                        // with the server; a part layer that cannot take a value is that layer's fault,
                        // and a fresh world would hand it the same value again.
                        _replica.ReportDiagnostic("[CoreAI.RbxApi] the part sink threw for instance "
                                                  + entry.InstanceId.Value + ": " + exception);
                    }
                }
            }

            return applied;
        }
    }
}
