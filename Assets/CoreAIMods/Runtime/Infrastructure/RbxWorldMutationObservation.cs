using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;

namespace CoreAI.Mods.WorldPackages
{
    /// <summary>
    /// The optional face of an <see cref="IConfirmedWorldMutationGate"/> that records which actor asked
    /// for a mutation. The built-in tools call it through
    /// <see cref="WorldMutationGateExtensions.ExecuteAttributedAsync{TResult}"/>, which falls back to the
    /// plain call for a gate without it.
    /// </summary>
    public interface IAttributedWorldMutationGate : IConfirmedWorldMutationGate
    {
        /// <summary>Runs a mutation like <see cref="IConfirmedWorldMutationGate.ExecuteAsync{TResult}"/>, attributed to <paramref name="actorId"/>.</summary>
        Task<TResult> ExecuteAsync<TResult>(
            string trigger,
            string actorId,
            Func<CancellationToken, Task<TResult>> mutationAsync,
            CancellationToken cancellationToken);
    }

    /// <summary>A gate that tells observers about every mutation it runs, with the snapshot it took before it.</summary>
    public interface IObservableWorldMutationGate
    {
        /// <summary>Adds an observer; adding the same one twice keeps one registration.</summary>
        void AddObserver(IRbxWorldMutationObserver observer);

        /// <summary>Removes an observer; unknown observers are ignored.</summary>
        void RemoveObserver(IRbxWorldMutationObserver observer);
    }

    /// <summary>
    /// Receives each gated mutation while the gate is held. Implementations must be quick and must not
    /// throw; a throw is contained by the gate and never changes the mutation or its result.
    /// </summary>
    public interface IRbxWorldMutationObserver
    {
        /// <summary>Runs after the pre-mutation backup was taken or skipped, right before the mutation.</summary>
        void OnMutationStarting(RbxWorldMutationInfo mutation);

        /// <summary>
        /// Runs after the mutation returned (<paramref name="completed"/> true) or threw. <paramref name="before"/>
        /// is the world as the gate captured it before the mutation, or null when no capture exists (a world
        /// past a format limit). The autosave of that snapshot may still be on its way to storage.
        /// </summary>
        void OnMutationFinished(RbxWorldMutationInfo mutation, RbxWorldSnapshot before, bool completed);
    }

    /// <summary>
    /// A gate that times its own pre-mutation backups, so a live world, a HUD or a benchmark harness can
    /// read what one AI edit's safety net costs.
    /// </summary>
    public interface IMeasuredWorldMutationGate
    {
        /// <summary>The newest measurement; null before the first backup.</summary>
        RbxWorldBackupMetrics LatestBackupMetrics { get; }

        /// <summary>Adds an observer told about every measured backup; adding one twice keeps one registration.</summary>
        void AddMetricsObserver(IRbxWorldBackupMetricsObserver observer);

        /// <summary>Removes a metrics observer; unknown observers are ignored.</summary>
        void RemoveMetricsObserver(IRbxWorldBackupMetricsObserver observer);
    }

    /// <summary>Receives one <see cref="RbxWorldBackupMetrics"/> per backup; must be quick and must not throw.</summary>
    public interface IRbxWorldBackupMetricsObserver
    {
        /// <summary>
        /// Called once the backup's outcome is known: before the mutation for a durable-first backup or a
        /// refused one, after the durable write for a backup written behind its mutation.
        /// </summary>
        void OnBackupMeasured(RbxWorldBackupMetrics metrics);
    }

    /// <summary>What one pre-mutation backup cost and how it ended.</summary>
    public sealed class RbxWorldBackupMetrics
    {
        public RbxWorldBackupMetrics(
            string trigger,
            long sequence,
            bool writtenBehind,
            double captureMilliseconds,
            double encodeMilliseconds,
            double writeMilliseconds,
            long packageBytes,
            bool succeeded,
            string error)
        {
            Trigger = trigger ?? "";
            Sequence = sequence;
            WrittenBehind = writtenBehind;
            CaptureMilliseconds = captureMilliseconds;
            EncodeMilliseconds = encodeMilliseconds;
            WriteMilliseconds = writeMilliseconds;
            PackageBytes = packageBytes;
            Succeeded = succeeded;
            Error = succeeded ? "" : error ?? "";
        }

        /// <summary>A write-behind backup refused before its mutation because the world cannot be encoded.</summary>
        internal static RbxWorldBackupMetrics Refused(
            string trigger,
            long sequence,
            bool writtenBehind,
            double captureMilliseconds,
            double encodeMilliseconds,
            string error)
        {
            return new RbxWorldBackupMetrics(
                trigger, sequence, writtenBehind, captureMilliseconds, encodeMilliseconds, 0d, -1L, false, error);
        }

        public string Trigger { get; }

        /// <summary>The sequence of the mutation this backup protects (see <see cref="RbxWorldMutationInfo.Sequence"/>).</summary>
        public long Sequence { get; }

        /// <summary>True when the durable write ran behind the mutation.</summary>
        public bool WrittenBehind { get; }

        /// <summary>The synchronous world capture in front of the mutation.</summary>
        public double CaptureMilliseconds { get; }

        /// <summary>
        /// The store's preparation in front of the mutation: the in-memory encode, or only the proof that the
        /// encode cannot fail when the store defers it; 0 for a durable-first backup, whose store encodes
        /// inside the write.
        /// </summary>
        public double EncodeMilliseconds { get; }

        /// <summary>
        /// The store's write: behind the mutation when <see cref="WrittenBehind"/>, otherwise in front of it;
        /// it includes a deferred encode.
        /// </summary>
        public double WriteMilliseconds { get; }

        /// <summary>Bytes of the encoded package; -1 when the store did not report them.</summary>
        public long PackageBytes { get; }

        public bool Succeeded { get; }

        public string Error { get; }

        /// <summary>Time this backup kept the mutation waiting.</summary>
        public double MillisecondsInFrontOfTheMutation =>
            CaptureMilliseconds + EncodeMilliseconds + (WrittenBehind ? 0d : WriteMilliseconds);
    }

    /// <summary>What an observer learns about one gated mutation.</summary>
    public sealed class RbxWorldMutationInfo
    {
        public RbxWorldMutationInfo(string trigger, string actorId, long sequence)
        {
            Trigger = trigger ?? "";
            ActorId = actorId ?? "";
            Sequence = sequence;
        }

        /// <summary>The backup trigger the caller passed, for example <c>execute_lua</c>.</summary>
        public string Trigger { get; }

        /// <summary>The actor the mutation is attributed to; empty when the caller did not say.</summary>
        public string ActorId { get; }

        /// <summary>Increases by one for every mutation the gate runs.</summary>
        public long Sequence { get; }
    }

    /// <summary>
    /// The world as the gate captured it before one mutation, kept in memory: the encoded package when
    /// the store prepared one (compact), otherwise the captured payload.
    /// </summary>
    public sealed class RbxWorldSnapshot
    {
        private readonly object _gate = new();
        private RbxWorldPackagePayload _payload;
        private byte[] _packageBytes;

        /// <param name="packageBytes">The encoded package; when set, the payload is not kept.</param>
        public RbxWorldSnapshot(
            string trigger,
            DateTime capturedAtUtc,
            RbxWorldPackagePayload payload,
            byte[] packageBytes)
        {
            if (payload == null && packageBytes == null)
            {
                throw new ArgumentException("A world snapshot needs a payload or an encoded package.");
            }

            Trigger = trigger ?? "";
            CapturedAtUtc = capturedAtUtc;
            _packageBytes = packageBytes;
            _payload = packageBytes == null ? payload : null;
        }

        /// <summary>
        /// A snapshot of <paramref name="payload"/> that trades the payload for the compact package as soon as
        /// <paramref name="prepared"/> holds it, so an autosave encoded behind its mutation still leaves only
        /// the package in memory. Without <paramref name="prepared"/> it keeps the payload.
        /// </summary>
        public static RbxWorldSnapshot Of(
            string trigger,
            DateTime capturedAtUtc,
            RbxWorldPackagePayload payload,
            RbxPreparedAutoSave prepared)
        {
            RbxWorldSnapshot snapshot = new(trigger, capturedAtUtc, payload, prepared?.PackageBytes);
            if (prepared != null && prepared.Success && !snapshot.HoldsEncodedPackage)
            {
                prepared.WhenEncoded(snapshot.AdoptPackage);
            }

            return snapshot;
        }

        public string Trigger { get; }

        public DateTime CapturedAtUtc { get; }

        /// <summary>True when the snapshot holds an encoded package rather than a payload object graph.</summary>
        public bool HoldsEncodedPackage
        {
            get
            {
                lock (_gate)
                {
                    return _packageBytes != null;
                }
            }
        }

        /// <summary>Bytes of the encoded package; 0 when the snapshot holds a payload object graph.</summary>
        public long EncodedSizeBytes
        {
            get
            {
                lock (_gate)
                {
                    return _packageBytes?.LongLength ?? 0L;
                }
            }
        }

        /// <summary>The captured world as a package payload; decodes the package when the snapshot holds one.</summary>
        public RbxWorldPackagePayload ToPayload()
        {
            RbxWorldPackagePayload payload;
            byte[] packageBytes;
            lock (_gate)
            {
                payload = _payload;
                packageBytes = _packageBytes;
            }

            return payload ?? RbxWorldPackageSerializer.ReadPackage(packageBytes);
        }

        private void AdoptPackage(byte[] packageBytes)
        {
            lock (_gate)
            {
                _packageBytes = packageBytes;
                _payload = null;
            }
        }
    }

    /// <summary>
    /// A package store whose autosave can be split in two: encoded in memory before a mutation (where
    /// every encoding failure is still found) and written durably behind it. The gate writes behind
    /// only through a store that implements this; any other store keeps the durable-first autosave.
    /// </summary>
    public interface IRbxWorldAutoSaveWriteBehindStore
    {
        /// <summary>
        /// Decides whether <paramref name="payload"/> can become the store's autosave, touching no storage:
        /// it encodes it, or proves that no limit can refuse the encode and leaves the encode to
        /// <see cref="CommitAutoAsync"/>. A payload that cannot be encoded (a format limit, a platform
        /// budget, text the encoder refuses) comes back as an unsuccessful <see cref="RbxPreparedAutoSave"/>,
        /// so every refusal is known before the mutation; cancellation throws.
        /// </summary>
        UniTask<RbxPreparedAutoSave> PrepareAutoAsync(
            string trigger,
            RbxWorldPackagePayload payload,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Installs a prepared autosave into the ring with the same durability and rotation rules as
        /// <see cref="IRbxWorldPackageStore.CreateAutoAsync"/>, first encoding one whose encode was deferred
        /// (a frame after the mutation, so the change is drawn before the encode runs). Never throws for an
        /// I/O or durability failure; that is the returned result.
        /// </summary>
        UniTask<RbxWorldPackageWriteResult> CommitAutoAsync(
            RbxPreparedAutoSave prepared,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Autosaves whose write behind a mutation had begun but never finished when the process stopped,
        /// found when the store opened. Each one is one missing backup; the ring itself is intact.
        /// </summary>
        IReadOnlyList<RbxMissingAutoSave> MissingAutoSavesAtOpen { get; }

        /// <summary>
        /// Called by the world session on its first frame after it reported <see cref="MissingAutoSavesAtOpen"/>:
        /// the store may forget them, so the next start does not report them again. A composition that never
        /// runs a frame (a test, a tool) never calls it, so it cannot swallow a report the game still owes.
        /// </summary>
        void AcknowledgeMissingAutoSaves();
    }

    /// <summary>
    /// An autosave ready to be written, or the reason it cannot be encoded. Ready means either encoded in
    /// memory already, or proven encodable: the store showed before the mutation that no limit can refuse
    /// the encode, and encodes it in <see cref="IRbxWorldAutoSaveWriteBehindStore.CommitAutoAsync"/>, behind
    /// the mutation.
    /// </summary>
    public sealed class RbxPreparedAutoSave
    {
        private readonly object _gate = new();
        private RbxWorldPackagePayload _deferredPayload;
        private byte[] _packageBytes;
        private List<Action<byte[]>> _encodedListeners;

        private RbxPreparedAutoSave(
            string trigger,
            DateTime capturedAtUtc,
            byte[] packageBytes,
            RbxWorldPackagePayload deferredPayload,
            string error)
        {
            Trigger = trigger ?? "";
            CapturedAtUtc = capturedAtUtc;
            _packageBytes = packageBytes;
            _deferredPayload = deferredPayload;
            Error = error ?? "";
        }

        /// <summary>An autosave ready to be committed.</summary>
        public static RbxPreparedAutoSave Encoded(string trigger, DateTime capturedAtUtc, byte[] packageBytes)
        {
            return new RbxPreparedAutoSave(
                trigger,
                capturedAtUtc,
                packageBytes ?? throw new ArgumentNullException(nameof(packageBytes)),
                null,
                "");
        }

        /// <summary>
        /// An autosave of <paramref name="payload"/> proven encodable but not encoded yet; the store encodes
        /// it when it is committed. Only a store that proved it may create one.
        /// </summary>
        public static RbxPreparedAutoSave ProvenEncodable(
            string trigger,
            DateTime capturedAtUtc,
            RbxWorldPackagePayload payload,
            long packageBytesUpperBound)
        {
            RbxPreparedAutoSave prepared = new(
                trigger,
                capturedAtUtc,
                null,
                payload ?? throw new ArgumentNullException(nameof(payload)),
                "");
            prepared.PackageBytesUpperBound = packageBytesUpperBound;
            return prepared;
        }

        /// <summary>A payload that can never become a package; <paramref name="error"/> says why.</summary>
        public static RbxPreparedAutoSave Unencodable(string trigger, DateTime capturedAtUtc, string error)
        {
            return new RbxPreparedAutoSave(
                trigger,
                capturedAtUtc,
                null,
                null,
                string.IsNullOrWhiteSpace(error) ? "the world package could not be encoded" : error);
        }

        public string Trigger { get; }

        /// <summary>The store's clock when the autosave was prepared; it names the autosave file.</summary>
        public DateTime CapturedAtUtc { get; }

        /// <summary>
        /// The encoded package; null when <see cref="Success"/> is false, and for a proven-encodable autosave
        /// until its commit encoded it. Callers must not modify it.
        /// </summary>
        public byte[] PackageBytes
        {
            get
            {
                lock (_gate)
                {
                    return _packageBytes;
                }
            }
        }

        /// <summary>True while the autosave is proven encodable but its encode still waits for the commit.</summary>
        public bool IsEncodeDeferred
        {
            get
            {
                lock (_gate)
                {
                    return _deferredPayload != null;
                }
            }
        }

        /// <summary>
        /// The bound the proof established for the compressed package; 0 for an autosave encoded at once or
        /// unencodable.
        /// </summary>
        public long PackageBytesUpperBound { get; private set; }

        public bool Success => Error.Length == 0;

        public string Error { get; }

        /// <summary>
        /// Calls <paramref name="listener"/> with the encoded package once it exists: at once when it already
        /// does, otherwise when the commit encodes it. Never called for an unencodable autosave, nor when the
        /// deferred encode fails.
        /// </summary>
        public void WhenEncoded(Action<byte[]> listener)
        {
            if (listener == null)
            {
                throw new ArgumentNullException(nameof(listener));
            }

            byte[] ready;
            lock (_gate)
            {
                ready = _packageBytes;
                if (ready == null)
                {
                    if (_deferredPayload != null)
                    {
                        _encodedListeners ??= new List<Action<byte[]>>();
                        _encodedListeners.Add(listener);
                    }

                    return;
                }
            }

            listener(ready);
        }

        /// <summary>The payload a deferred encode still has to encode; null once encoded, and for any other autosave.</summary>
        internal RbxWorldPackagePayload DeferredPayload
        {
            get
            {
                lock (_gate)
                {
                    return _deferredPayload;
                }
            }
        }

        /// <summary>Records the bytes the commit encoded and tells every listener; the payload is released.</summary>
        internal void CompleteDeferredEncode(byte[] packageBytes)
        {
            if (packageBytes == null)
            {
                throw new ArgumentNullException(nameof(packageBytes));
            }

            List<Action<byte[]>> listeners;
            lock (_gate)
            {
                if (_deferredPayload == null)
                {
                    throw new InvalidOperationException("This autosave has no deferred encode.");
                }

                _packageBytes = packageBytes;
                _deferredPayload = null;
                listeners = _encodedListeners;
                _encodedListeners = null;
            }

            if (listeners == null)
            {
                return;
            }

            foreach (Action<byte[]> listener in listeners)
            {
                listener(packageBytes);
            }
        }

        /// <summary>Drops the payload and the listeners of a deferred encode that failed.</summary>
        internal void AbandonDeferredEncode()
        {
            lock (_gate)
            {
                _deferredPayload = null;
                _encodedListeners = null;
            }
        }

        /// <summary>The request a deferred encode spread over frames listens to (see <see cref="RequestEncodeAtOnce"/>).</summary>
        internal RbxWorldPackageEncodeUrgency EncodeUrgency { get; } = new();

        /// <summary>
        /// Asks a deferred encode that is spread over frames to finish at once, in one step; nothing for an autosave
        /// already encoded. The gate calls it when the next mutation waits for this backup.
        /// </summary>
        internal void RequestEncodeAtOnce()
        {
            EncodeUrgency.Request();
        }

        /// <summary>The write result a durable-first autosave of the same payload would have returned.</summary>
        public RbxWorldPackageWriteResult ToFailedWrite()
        {
            return new RbxWorldPackageWriteResult(false, "", Error, true);
        }
    }

    /// <summary>One autosave a crash interrupted after its mutation had run.</summary>
    public sealed class RbxMissingAutoSave
    {
        public RbxMissingAutoSave(string trigger, DateTime timestampUtc, string markerFileName)
        {
            Trigger = trigger ?? "";
            TimestampUtc = timestampUtc;
            MarkerFileName = markerFileName ?? "";
        }

        public string Trigger { get; }

        public DateTime TimestampUtc { get; }

        public string MarkerFileName { get; }

        /// <summary>The line the session reports at start for these missing backups; empty when there are none.</summary>
        public static string Describe(IReadOnlyList<RbxMissingAutoSave> missing)
        {
            if (missing == null || missing.Count == 0)
            {
                return "";
            }

            List<string> parts = new(missing.Count);
            foreach (RbxMissingAutoSave entry in missing)
            {
                parts.Add("'" + entry.Trigger + "' at " + entry.TimestampUtc.ToString(
                    "yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            }

            return (missing.Count == 1 ? "One autosave was" : missing.Count.ToString(CultureInfo.InvariantCulture)
                       + " autosaves were")
                   + " still being written when the app stopped (" + string.Join(", ", parts) + "), so "
                   + (missing.Count == 1 ? "that backup is" : "those backups are")
                   + " missing. Every other autosave is intact.";
        }
    }

    /// <summary>Calls on any gate that use its optional faces when it has them.</summary>
    public static class WorldMutationGateExtensions
    {
        /// <summary>
        /// Runs <paramref name="mutationAsync"/> through <paramref name="gate"/> attributed to
        /// <paramref name="actorId"/> when the gate records actors, and as a plain gated call otherwise.
        /// </summary>
        public static Task<TResult> ExecuteAttributedAsync<TResult>(
            this IConfirmedWorldMutationGate gate,
            string trigger,
            string actorId,
            Func<CancellationToken, Task<TResult>> mutationAsync,
            CancellationToken cancellationToken)
        {
            if (gate == null)
            {
                throw new ArgumentNullException(nameof(gate));
            }

            return gate is IAttributedWorldMutationGate attributed
                ? attributed.ExecuteAsync(trigger, actorId ?? "", mutationAsync, cancellationToken)
                : gate.ExecuteAsync(trigger, mutationAsync, cancellationToken);
        }
    }
}
