using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Scheduling;
using CoreAI.Mods.WorldPackages;
using CoreAI.Sandbox.LuaCs;
using CoreAI.Scripting;
using CoreAI.Scripting.LuaCs;
using Cysharp.Threading.Tasks;
using Microsoft.Extensions.AI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CoreAI.Mods.WorldPackages
{
    /// <summary>Serializes capture, confirmed autosave, and the mutation protected by that autosave.</summary>
    public interface IConfirmedWorldMutationGate
    {
        /// <summary>Runs a mutation only after its trigger-labelled snapshot is durably confirmed.</summary>
        Task<TResult> ExecuteAsync<TResult>(
            string trigger,
            Func<CancellationToken, Task<TResult>> mutationAsync,
            CancellationToken cancellationToken);
    }

    /// <summary>
    /// The optional second face of an <see cref="IConfirmedWorldMutationGate"/>: what a world session
    /// controller (<see cref="RbxWorldRuntimeSessionController"/>) needs from the shared gate to keep the
    /// world that opens on the next start in step with the live one. <see cref="ConfirmedWorldMutationGate"/>
    /// implements it; a host gate that wraps one forwards these members to it.
    /// </summary>
    /// <remarks>
    /// A gate without it still serializes and backs up every mutation, but the controller then falls
    /// back as follows, and says so once through its diagnostics when it is composed with a startup
    /// store: (1) the startup world follows mod source changes only, recorded from the frame pump, so a
    /// gated change to the world tree (an execute_lua build) is not recorded for the next start; (2) the
    /// boot-time startup restore runs without the gate, so a mutation arriving during boot can change the
    /// default world the restore then replaces; (3) the pump cannot tell that a gated mutation is running
    /// and may record the world in the middle of one (the next change records the finished state).
    /// </remarks>
    public interface IStartupAwareWorldMutationGate : IConfirmedWorldMutationGate
    {
        /// <summary>True while a mutation, a confirmed world load or a boot-time restore holds the gate.</summary>
        bool IsHeld { get; }

        /// <summary>
        /// Called with the trigger of each mutation <see cref="IConfirmedWorldMutationGate.ExecuteAsync{TResult}"/>
        /// runs, after its backup is confirmed and right before the mutation itself; the world session
        /// controller sets it to watch what the mutation changes. Must not throw; a throw is contained.
        /// </summary>
        Action<string> MutationStarting { get; set; }

        /// <summary>
        /// Runs after every mutation that returned, while the gate is still held, with the mutation's
        /// trigger. The world session controller sets it to keep the durable startup selection in step
        /// with the live world; it answers a note for the caller (why the change was not recorded) or an
        /// empty string, and the gate adds a note to the mutation's result. It must not throw; a throw
        /// is contained so it never replaces the mutation result.
        /// </summary>
        Func<string, CancellationToken, UniTask<string>> AfterMutationAsync { get; set; }

        /// <summary>
        /// Runs <paramref name="operationAsync"/> alone under the gate, with no backup and no
        /// after-mutation follow-up: the boot-time startup restore, whose outgoing world is the
        /// reproducible default world. No thread and no blocking wait (WebGL).
        /// </summary>
        Task<TResult> ExecuteWithoutBackupAsync<TResult>(
            Func<CancellationToken, Task<TResult>> operationAsync,
            CancellationToken cancellationToken);
    }

    /// <summary>
    /// Requires a pre-mutation world-package autosave before allowing a runtime mutation. By default
    /// the autosave is durable before the mutation runs. With <see cref="WriteBehindBackups"/> on and a
    /// store that implements <see cref="IRbxWorldAutoSaveWriteBehindStore"/>, the snapshot is captured and
    /// encoded in memory first, or proven encodable and encoded behind (so every capture and encoding
    /// refusal is decided exactly as before), the
    /// mutation runs at once, and the durable write follows behind it; the next gated mutation waits for
    /// that write and, when it failed, backs itself up durably first again. The safety autosave of a
    /// player-confirmed world load always stays durable-first.
    /// </summary>
    public sealed class ConfirmedWorldMutationGate
        : IStartupAwareWorldMutationGate, IAttributedWorldMutationGate, IObservableWorldMutationGate,
            IMeasuredWorldMutationGate
    {
        /// <summary>The manage_mods result field that carries the after-mutation note.</summary>
        internal const string StartupNoteField = "startup_warning";

        /// <summary>
        /// The manage_mods result field that says a mutation ran without its pre-mutation backup,
        /// because the live world cannot be encoded as a package at all, and why; or that the backup
        /// written behind the previous mutation failed.
        /// </summary>
        internal const string BackupNoteField = "backup_warning";

        private readonly Func<CancellationToken, UniTask<RbxWorldPackagePayload>> _captureCurrentAsync;
        private readonly IRbxWorldPackageStore _packageStore;
        private readonly IRbxWorldAutoSaveWriteBehindStore _writeBehindStore;
        private readonly SemaphoreSlim _singleFlight = new(1, 1);
        private readonly object _observerGate = new();
        private readonly List<IRbxWorldMutationObserver> _observers = new();
        private readonly List<IRbxWorldBackupMetricsObserver> _metricsObservers = new();
        private int _held;
        private long _sequence;
        private Task<RbxWorldPackageWriteResult> _pendingBackup;
        private string _pendingTrigger = "";
        private string _lastBackupFailure = "";
        private RbxWorldBackupMetrics _latestMetrics;

        /// <summary>Creates a shared gate over the host capture port and durable package store.</summary>
        public ConfirmedWorldMutationGate(
            Func<CancellationToken, UniTask<RbxWorldPackagePayload>> captureCurrentAsync,
            IRbxWorldPackageStore packageStore)
            : this(captureCurrentAsync, packageStore, false)
        {
        }

        /// <summary>Creates a shared gate; <paramref name="writeBehindBackups"/> sets <see cref="WriteBehindBackups"/>.</summary>
        public ConfirmedWorldMutationGate(
            Func<CancellationToken, UniTask<RbxWorldPackagePayload>> captureCurrentAsync,
            IRbxWorldPackageStore packageStore,
            bool writeBehindBackups)
        {
            _captureCurrentAsync = captureCurrentAsync
                                   ?? throw new ArgumentNullException(nameof(captureCurrentAsync));
            _packageStore = packageStore ?? throw new ArgumentNullException(nameof(packageStore));
            _writeBehindStore = packageStore as IRbxWorldAutoSaveWriteBehindStore;
            WriteBehindBackups = writeBehindBackups;
        }

        /// <inheritdoc />
        public Action<string> MutationStarting { get; set; }

        /// <inheritdoc />
        public Func<string, CancellationToken, UniTask<string>> AfterMutationAsync { get; set; }

        /// <inheritdoc />
        public bool IsHeld => Volatile.Read(ref _held) != 0;

        /// <summary>
        /// Asks for the autosave to be written behind the mutation instead of before it. It takes effect
        /// only with a store that implements <see cref="IRbxWorldAutoSaveWriteBehindStore"/>
        /// (<see cref="WritesBehind"/>); set it before the first mutation. Off by default.
        /// </summary>
        public bool WriteBehindBackups { get; set; }

        /// <summary>True when the next mutation's autosave is written behind it.</summary>
        public bool WritesBehind => WriteBehindBackups && _writeBehindStore != null;

        /// <summary>Optional sink for a write-behind autosave that failed; the composition points it at its log.</summary>
        public Action<string> Diagnostics { get; set; }

        /// <summary>True while an autosave written behind the last mutation has not finished.</summary>
        public bool HasPendingBackup
        {
            get
            {
                Task<RbxWorldPackageWriteResult> pending = Volatile.Read(ref _pendingBackup);
                return pending != null && !pending.IsCompleted;
            }
        }

        /// <summary>Why the latest autosave written behind a mutation failed; empty once a later one succeeded.</summary>
        public string LastBackupFailure => Volatile.Read(ref _lastBackupFailure) ?? "";

        /// <summary>
        /// The autosaves that a crash interrupted after their mutation had run, found when the store
        /// opened; empty for a store without write-behind support.
        /// </summary>
        public IReadOnlyList<RbxMissingAutoSave> MissingBackupsAtStart =>
            _writeBehindStore?.MissingAutoSavesAtOpen ?? Array.Empty<RbxMissingAutoSave>();

        /// <inheritdoc />
        public RbxWorldBackupMetrics LatestBackupMetrics => Volatile.Read(ref _latestMetrics);

        /// <summary>
        /// Completes when the autosave written behind the last mutation finished, with its result; at once
        /// with a success when none is pending. Never throws. No thread and no blocking wait (WebGL).
        /// </summary>
        public async Task<RbxWorldPackageWriteResult> WaitForPendingBackupAsync()
        {
            Task<RbxWorldPackageWriteResult> pending = Volatile.Read(ref _pendingBackup);
            if (pending == null)
            {
                return new RbxWorldPackageWriteResult(true, "", "");
            }

            try
            {
                return await pending ?? new RbxWorldPackageWriteResult(false, "", "The package store returned no result.");
            }
            catch (Exception ex)
            {
                return new RbxWorldPackageWriteResult(false, "", ex.Message);
            }
        }

        /// <inheritdoc />
        public void AddObserver(IRbxWorldMutationObserver observer)
        {
            if (observer == null)
            {
                throw new ArgumentNullException(nameof(observer));
            }

            lock (_observerGate)
            {
                if (!_observers.Contains(observer))
                {
                    _observers.Add(observer);
                }
            }
        }

        /// <inheritdoc />
        public void RemoveObserver(IRbxWorldMutationObserver observer)
        {
            lock (_observerGate)
            {
                _observers.Remove(observer);
            }
        }

        /// <inheritdoc />
        public void AddMetricsObserver(IRbxWorldBackupMetricsObserver observer)
        {
            if (observer == null)
            {
                throw new ArgumentNullException(nameof(observer));
            }

            lock (_observerGate)
            {
                if (!_metricsObservers.Contains(observer))
                {
                    _metricsObservers.Add(observer);
                }
            }
        }

        /// <inheritdoc />
        public void RemoveMetricsObserver(IRbxWorldBackupMetricsObserver observer)
        {
            lock (_observerGate)
            {
                _metricsObservers.Remove(observer);
            }
        }

        /// <inheritdoc />
        public Task<TResult> ExecuteAsync<TResult>(
            string trigger,
            Func<CancellationToken, Task<TResult>> mutationAsync,
            CancellationToken cancellationToken)
        {
            return ExecuteCoreAsync(trigger, "", mutationAsync, cancellationToken);
        }

        /// <inheritdoc />
        public Task<TResult> ExecuteAsync<TResult>(
            string trigger,
            string actorId,
            Func<CancellationToken, Task<TResult>> mutationAsync,
            CancellationToken cancellationToken)
        {
            return ExecuteCoreAsync(trigger, actorId ?? "", mutationAsync, cancellationToken);
        }

        private async Task<TResult> ExecuteCoreAsync<TResult>(
            string trigger,
            string actorId,
            Func<CancellationToken, Task<TResult>> mutationAsync,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(trigger))
            {
                throw new ArgumentException("A deterministic backup trigger is required.", nameof(trigger));
            }

            if (mutationAsync == null)
            {
                throw new ArgumentNullException(nameof(mutationAsync));
            }

            await _singleFlight.WaitAsync(cancellationToken);
            Volatile.Write(ref _held, 1);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                // WHY settled first: the gate keeps one autosave in flight at most, so the ring never
                // holds two half-written backups, and a failed one makes this mutation back itself up
                // durably before it runs, as every mutation did before write-behind.
                string previousBackupFailure = await SettlePendingBackupAsync();
                // WHY a confirmed load stays durable-first: the player chose to replace the whole world, the
                // outgoing world exists nowhere else afterwards, and a load is never on the AI's hot path.
                bool writeBehind = WritesBehind
                                   && previousBackupFailure.Length == 0
                                   && !string.Equals(
                                       trigger,
                                       RbxWorldRuntimeSessionController.PreLoadAutosaveTrigger,
                                       StringComparison.Ordinal);
                Stopwatch watch = Stopwatch.StartNew();
                RbxWorldPackagePayload payload;
                string backupNote = "";
                try
                {
                    payload = await _captureCurrentAsync(cancellationToken);
                }
                catch (RbxWorldPackageFormatLimitException ex) when (MayRunWithoutAnImpossibleBackup(trigger))
                {
                    // WHY: a world past a format limit cannot be captured, so no backup of it can exist.
                    // Forget, a confirmed load and an undo or redo are the ways back under the limit;
                    // refusing them too would leave the world unable to change or save forever.
                    payload = null;
                    backupNote = DescribeSkippedBackup(trigger, ex.Message);
                }

                double captureMilliseconds = watch.Elapsed.TotalMilliseconds;
                double encodeMilliseconds = 0d;
                RbxPreparedAutoSave prepared = null;
                if (backupNote.Length == 0)
                {
                    if (payload == null)
                    {
                        throw new InvalidOperationException(
                            "Confirmed pre-mutation backup capture returned no world payload.");
                    }

                    if (writeBehind)
                    {
                        prepared = await PrepareBehindAsync(trigger, payload, cancellationToken);
                        encodeMilliseconds = watch.Elapsed.TotalMilliseconds - captureMilliseconds;
                        if (!prepared.Success)
                        {
                            if (!MayRunWithoutAnImpossibleBackup(trigger))
                            {
                                PublishMetrics(RbxWorldBackupMetrics.Refused(
                                    trigger, _sequence + 1, true, captureMilliseconds, encodeMilliseconds,
                                    prepared.Error));
                                throw new InvalidOperationException(
                                    "Confirmed pre-mutation backup '" + trigger + "' failed: " + prepared.Error
                                    + DescribeWayBackUnderTheLimits());
                            }

                            backupNote = DescribeSkippedBackup(trigger, prepared.Error);
                            prepared = null;
                        }
                    }
                    else
                    {
                        RbxWorldPackageWriteResult backup;
                        try
                        {
                            backup = await _packageStore.CreateAutoAsync(
                                trigger,
                                payload,
                                cancellationToken);
                        }
                        catch (Exception ex) when (IsEncodingFailure(ex) && MayRunWithoutAnImpossibleBackup(trigger))
                        {
                            backup = new RbxWorldPackageWriteResult(false, "", ex.Message, true);
                        }

                        PublishMetrics(new RbxWorldBackupMetrics(
                            trigger, _sequence + 1, false, captureMilliseconds, 0d,
                            watch.Elapsed.TotalMilliseconds - captureMilliseconds, -1L,
                            backup != null && backup.Success, backup?.Error ?? "no result"));
                        if (backup == null || !backup.Success)
                        {
                            string reason = backup == null || string.IsNullOrWhiteSpace(backup.Error)
                                ? "the package store did not confirm durability"
                                : backup.Error;
                            // WHY only an encoding failure: the captured world itself cannot become a
                            // package (a format limit or a platform budget met at write time), so it fails
                            // the same way on every retry, exactly like a capture past a format limit. A
                            // durability or I/O failure may pass on retry and keeps refusing everything.
                            if (backup == null
                                || !backup.PackageCannotBeEncoded
                                || !MayRunWithoutAnImpossibleBackup(trigger))
                            {
                                throw new InvalidOperationException(
                                    "Confirmed pre-mutation backup '" + trigger + "' failed: " + reason
                                    + (backup != null && backup.PackageCannotBeEncoded
                                        ? DescribeWayBackUnderTheLimits()
                                        : ""));
                            }

                            backupNote = DescribeSkippedBackup(trigger, reason);
                        }
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                RbxWorldMutationInfo mutation = new(trigger, actorId, ++_sequence);
                RbxWorldSnapshot before = payload == null
                    ? null
                    : RbxWorldSnapshot.Of(trigger, payload.CapturedAtUtc, payload, prepared);
                NotifyMutationStarting(trigger);
                NotifyObserversStarting(mutation);
                Task<TResult> running = StartMutation(mutationAsync, cancellationToken);
                // WHY after the mutation has started: its synchronous part (all of a one-off execute_lua
                // build) is visible before any storage work, and the write that follows still takes the
                // store before anything the mutation itself writes there (a startup record).
                if (prepared != null)
                {
                    BeginWriteBehind(prepared, mutation.Sequence, captureMilliseconds, encodeMilliseconds);
                }

                TResult result;
                try
                {
                    result = await running;
                }
                catch (Exception)
                {
                    NotifyObserversFinished(mutation, before, false);
                    throw;
                }

                NotifyObserversFinished(mutation, before, true);
                // WHY a load result never carries the earlier loss: its BackupWarning speaks about the load's
                // own safety autosave; the earlier loss is still in LastBackupFailure and the diagnostics.
                string combinedBackupNote = result is RbxWorldLoadResult
                    ? backupNote
                    : CombineNotes(backupNote, previousBackupFailure);
                if (combinedBackupNote.Length > 0)
                {
                    result = WithNote(result, combinedBackupNote, BackupNoteField);
                }

                string note = await NotifyAfterMutationAsync(trigger);
                return note.Length == 0 ? result : WithNote(result, note, StartupNoteField);
            }
            finally
            {
                Volatile.Write(ref _held, 0);
                _singleFlight.Release();
            }
        }

        /// <inheritdoc />
        public async Task<TResult> ExecuteWithoutBackupAsync<TResult>(
            Func<CancellationToken, Task<TResult>> operationAsync,
            CancellationToken cancellationToken)
        {
            if (operationAsync == null)
            {
                throw new ArgumentNullException(nameof(operationAsync));
            }

            await _singleFlight.WaitAsync(cancellationToken);
            Volatile.Write(ref _held, 1);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                return await operationAsync(cancellationToken);
            }
            finally
            {
                Volatile.Write(ref _held, 0);
                _singleFlight.Release();
            }
        }

        /// <summary>
        /// True for the triggers that may run without a backup when the live world cannot be encoded
        /// as a package at all: forget, the one mutation that removes a mod's source and so can bring
        /// the world back under a limit; the safety autosave of a confirmed world load; and an undo or
        /// redo of an AI change. The last three replace the whole world with a package that was already
        /// read and validated (a load) or captured and encoded (an undo or redo snapshot).
        /// </summary>
        /// <remarks>
        /// WHY not unload as well: an unloaded mod keeps its source, so an unload never brings the
        /// world back under the limit, and letting it skip its backup only removed the backup.
        /// WHY not every other mutation: none of them can make the world encodable again through the
        /// gate, and each would change a world that has no backup.
        /// </remarks>
        private static bool MayRunWithoutAnImpossibleBackup(string trigger)
        {
            return string.Equals(trigger, LuaModsLlmTool.ForgetBackupTrigger, StringComparison.Ordinal)
                   || string.Equals(
                       trigger,
                       RbxWorldRuntimeSessionController.PreLoadAutosaveTrigger,
                       StringComparison.Ordinal)
                   || string.Equals(trigger, RbxWorldUndoService.UndoTrigger, StringComparison.Ordinal)
                   || string.Equals(trigger, RbxWorldUndoService.RedoTrigger, StringComparison.Ordinal);
        }

        /// <summary>True for a failure of the payload itself, as opposed to cancellation or I/O.</summary>
        private static bool IsEncodingFailure(Exception exception)
        {
            return exception is RbxWorldPackageException
                   || exception is System.Text.EncoderFallbackException;
        }

        /// <summary>The note a mutation that ran without its impossible backup carries on its result.</summary>
        internal static string DescribeSkippedBackup(string trigger, string reason)
        {
            return "No pre-mutation backup was written for '" + trigger + "': the live world cannot be "
                   + "encoded as a world package (" + (reason ?? "").Trim() + "). The action ran without "
                   + "one, because it is the way to bring the world back within the package limits.";
        }

        /// <summary>
        /// What a refused mutation's caller can do when the live world cannot be encoded as a package:
        /// appended to the refusal so it never reads as a transient error worth retrying.
        /// </summary>
        internal static string DescribeWayBackUnderTheLimits()
        {
            return " The live world cannot be backed up as a world package, so this change was NOT made and "
                   + "the same call fails until the world fits again. Ways back within the package limits: "
                   + "the player can undo the last AI change (it restores the smaller world kept in memory), "
                   + "forget a mod that is no longer needed (manage_mods action 'forget'), or load a smaller "
                   + "save or autosave on the World Loads page.";
        }

        private async Task<RbxPreparedAutoSave> PrepareBehindAsync(
            string trigger,
            RbxWorldPackagePayload payload,
            CancellationToken cancellationToken)
        {
            RbxPreparedAutoSave prepared;
            try
            {
                prepared = await _writeBehindStore.PrepareAutoAsync(trigger, payload, cancellationToken);
            }
            catch (Exception ex) when (IsEncodingFailure(ex))
            {
                prepared = RbxPreparedAutoSave.Unencodable(trigger, payload.CapturedAtUtc, ex.Message);
            }

            return prepared ?? throw new InvalidOperationException(
                "Confirmed pre-mutation backup '" + trigger + "' failed: the package store prepared no autosave.");
        }

        private void BeginWriteBehind(
            RbxPreparedAutoSave prepared,
            long sequence,
            double captureMilliseconds,
            double encodeMilliseconds)
        {
            _pendingTrigger = prepared.Trigger;
            Volatile.Write(
                ref _pendingBackup,
                CommitBehindAsync(prepared, sequence, captureMilliseconds, encodeMilliseconds));
        }

        /// <summary>Writes a prepared autosave durably behind its mutation. Never throws.</summary>
        private async Task<RbxWorldPackageWriteResult> CommitBehindAsync(
            RbxPreparedAutoSave prepared,
            long sequence,
            double captureMilliseconds,
            double encodeMilliseconds)
        {
            Stopwatch watch = Stopwatch.StartNew();
            RbxWorldPackageWriteResult result;
            try
            {
                // WHY uncancelled: the mutation already ran, so abandoning its backup half-way would only
                // lose it; the caller's token has no say over the write any more.
                result = await _writeBehindStore.CommitAutoAsync(prepared, CancellationToken.None)
                         ?? new RbxWorldPackageWriteResult(false, "", "the package store returned no result");
            }
            catch (Exception ex)
            {
                result = new RbxWorldPackageWriteResult(false, "", ex.Message);
            }

            if (result.Success)
            {
                Volatile.Write(ref _lastBackupFailure, "");
            }
            else
            {
                string failure = DescribeLostBehindBackup(prepared.Trigger, result);
                Volatile.Write(ref _lastBackupFailure, failure);
                ReportDiagnostic(failure);
            }

            PublishMetrics(new RbxWorldBackupMetrics(
                prepared.Trigger, sequence, true, captureMilliseconds, encodeMilliseconds,
                watch.Elapsed.TotalMilliseconds, prepared.PackageBytes?.LongLength ?? -1L,
                result.Success, result.Error));
            return result;
        }

        /// <summary>Waits for the autosave written behind the previous mutation; answers why it failed, or empty.</summary>
        private async Task<string> SettlePendingBackupAsync()
        {
            Task<RbxWorldPackageWriteResult> pending = Volatile.Read(ref _pendingBackup);
            if (pending == null)
            {
                return "";
            }

            RbxWorldPackageWriteResult result;
            try
            {
                result = await pending;
            }
            catch (Exception ex)
            {
                result = new RbxWorldPackageWriteResult(false, "", ex.Message);
            }

            Volatile.Write(ref _pendingBackup, null);
            return result != null && result.Success ? "" : DescribeLostBehindBackup(_pendingTrigger, result);
        }

        private static string DescribeLostBehindBackup(string trigger, RbxWorldPackageWriteResult result)
        {
            string reason = result == null || string.IsNullOrWhiteSpace(result.Error)
                ? "the package store did not confirm durability"
                : result.Error.Trim();
            return "The autosave of the world before the previous change ('" + trigger + "') could not be "
                   + "written behind it: " + reason + " That one change has no autosave; the next change is "
                   + "backed up before it runs.";
        }

        private static string CombineNotes(string first, string second)
        {
            if (first.Length == 0)
            {
                return second;
            }

            return second.Length == 0 ? first : first + " " + second;
        }

        private static Task<TResult> StartMutation<TResult>(
            Func<CancellationToken, Task<TResult>> mutationAsync,
            CancellationToken cancellationToken)
        {
            try
            {
                return mutationAsync(cancellationToken)
                       ?? Task.FromException<TResult>(
                           new InvalidOperationException("The gated mutation returned no task."));
            }
            catch (Exception ex)
            {
                return Task.FromException<TResult>(ex);
            }
        }

        private void ReportDiagnostic(string message)
        {
            Action<string> diagnostics = Diagnostics;
            if (diagnostics == null)
            {
                return;
            }

            try
            {
                diagnostics(message);
            }
            catch (Exception)
            {
            }
        }

        private void NotifyObserversStarting(RbxWorldMutationInfo mutation)
        {
            foreach (IRbxWorldMutationObserver observer in SnapshotObservers())
            {
                // WHY contained: the backup is taken and the mutation is about to run; an observer's
                // failure must not refuse it.
                try
                {
                    observer.OnMutationStarting(mutation);
                }
                catch (Exception)
                {
                }
            }
        }

        private void NotifyObserversFinished(RbxWorldMutationInfo mutation, RbxWorldSnapshot before, bool completed)
        {
            foreach (IRbxWorldMutationObserver observer in SnapshotObservers())
            {
                try
                {
                    observer.OnMutationFinished(mutation, before, completed);
                }
                catch (Exception)
                {
                }
            }
        }

        private IRbxWorldMutationObserver[] SnapshotObservers()
        {
            lock (_observerGate)
            {
                return _observers.Count == 0 ? Array.Empty<IRbxWorldMutationObserver>() : _observers.ToArray();
            }
        }

        private void PublishMetrics(RbxWorldBackupMetrics metrics)
        {
            Volatile.Write(ref _latestMetrics, metrics);
            IRbxWorldBackupMetricsObserver[] observers;
            lock (_observerGate)
            {
                if (_metricsObservers.Count == 0)
                {
                    return;
                }

                observers = _metricsObservers.ToArray();
            }

            foreach (IRbxWorldBackupMetricsObserver observer in observers)
            {
                try
                {
                    observer.OnBackupMeasured(metrics);
                }
                catch (Exception)
                {
                }
            }
        }

        private void NotifyMutationStarting(string trigger)
        {
            Action<string> starting = MutationStarting;
            if (starting == null)
            {
                return;
            }

            // WHY contained: the backup is confirmed and the mutation is about to run; an observer's
            // failure must not refuse it.
            try
            {
                starting(trigger);
            }
            catch (Exception)
            {
            }
        }

        private async UniTask<string> NotifyAfterMutationAsync(string trigger)
        {
            Func<string, CancellationToken, UniTask<string>> afterMutation = AfterMutationAsync;
            if (afterMutation == null)
            {
                return "";
            }

            // WHY uncancelled and contained: the mutation already ran, so its result must reach the
            // caller whatever the follow-up does.
            try
            {
                return await afterMutation(trigger, CancellationToken.None) ?? "";
            }
            catch (Exception)
            {
                return "";
            }
        }

        /// <summary>
        /// Adds <paramref name="note"/> to the result of the gated operations: an execute_lua
        /// <see cref="LuaTool.LuaResult"/> (after its output, or its error when it failed), a
        /// manage_mods JSON object (as <paramref name="jsonField"/>), or, for
        /// <see cref="BackupNoteField"/> only, a confirmed load's <see cref="RbxWorldLoadResult"/>
        /// (as <see cref="RbxWorldLoadResult.BackupWarning"/>). Any other result is returned as is.
        /// </summary>
        /// <remarks>
        /// WHY on the result: the note says a change will not survive a restart, and the one reader who
        /// can act on it is the caller of the tool that made the change.
        /// </remarks>
        private static TResult WithNote<TResult>(TResult result, string note, string jsonField)
        {
            if (result is RbxWorldLoadResult loadResult)
            {
                return string.Equals(jsonField, BackupNoteField, StringComparison.Ordinal)
                    ? (TResult)(object)loadResult.WithBackupWarning(note)
                    : result;
            }

            if (result is LuaTool.LuaResult luaResult)
            {
                LuaTool.LuaResult noted = luaResult.Success
                    ? new LuaTool.LuaResult
                    {
                        Success = true,
                        Output = AppendNote(luaResult.Output, note, true),
                        Error = luaResult.Error
                    }
                    : new LuaTool.LuaResult
                    {
                        Success = false,
                        Output = luaResult.Output,
                        Error = AppendNote(luaResult.Error, note, false)
                    };
                return (TResult)(object)noted;
            }

            if (result is string json)
            {
                try
                {
                    // WHY no date parsing: the result is re-serialized, and a timestamp read back as a
                    // DateTime would come out in another format than the tool wrote it.
                    using System.IO.StringReader text = new(json);
                    using JsonTextReader reader = new(text) { DateParseHandling = DateParseHandling.None };
                    JObject parsed = JObject.Load(reader);
                    parsed[jsonField] = note;
                    return (TResult)(object)parsed.ToString(Formatting.None);
                }
                catch (JsonException)
                {
                    return result;
                }
            }

            return result;
        }

        private static string AppendNote(string text, string note, bool isOutput)
        {
            bool empty = string.IsNullOrEmpty(text)
                         || (isOutput && string.Equals(text, "nil", StringComparison.Ordinal));
            return empty ? note : text.TrimEnd() + "\n" + note;
        }
    }
}

namespace CoreAI.Ai.LuaCs
{
    /// <summary>
    /// Gameplay bindings seam for the Lua-CSharp one-shot/envelope path. Implementations register
    /// the game's callable APIs on a <see cref="LuaCsApiRegistry"/>. Hosts can adapt
    /// <c>LuaCsWorldRuntimeBindings</c> or <c>LuaCsFullUnityRuntimeBindings</c> through their
    /// <c>RegisterGameplayApis</c> methods.
    /// </summary>
    public interface ILuaCsGameRuntimeBindings
    {
        /// <summary>Registers gameplay-facing Lua-CSharp APIs in the provided registry.</summary>
        void RegisterGameplayApis(LuaCsApiRegistry registry);
    }

    internal interface IActorScopedLuaCsGameRuntimeBindings
    {
        InstanceRegistry MutationRegistry { get; }

        void RegisterGameplayApis(LuaCsApiRegistry registry,
            ActorContext actorContext);

        void RegisterGameplayApis(LuaCsApiRegistry registry, ActorContext actorContext,
            MutationEnvelope mutationEnvelope);
    }

    /// <summary>Registers the default Lua-CSharp runtime APIs (mirrors <c>CoreDefaultLuaRuntimeBindings</c>).</summary>
    public sealed class CoreDefaultLuaCsRuntimeBindings : ILuaCsGameRuntimeBindings
    {
        /// <inheritdoc />
        public void RegisterGameplayApis(LuaCsApiRegistry registry)
        {
            registry.Register("report", new Action<string>(_ => { }));
            registry.Register("add", new Func<double, double, double>((a, b) => a + b));
        }
    }

    /// <summary>
    /// Lua-CSharp (nuskey8/Lua-CSharp) counterpart of
    /// <see cref="CoreAI.Infrastructure.Lua.GameLuaToolExecutor"/>. Implements the SAME
    /// <see cref="LuaTool.ILuaExecutor"/> seam so the native <c>execute_lua</c> tool can later be
    /// re-pointed from the MoonSharp executor to this managed one by type. Each chunk runs in a fresh
    /// sandboxed <see cref="LuaState"/> under the <see cref="LuaCsExecutionGuard"/>, mirroring the
    /// envelope pipeline in <see cref="LuaCsAiEnvelopeProcessor"/> (same sandbox limits, same observer
    /// notifications, same result/error caps). State persists through the C#-side APIs the bindings
    /// expose (logic slots, mods, world commands), not through Lua globals.
    /// </summary>
    public sealed class LuaCsGameToolExecutor : LuaTool.ILuaExecutor, LuaTool.IMutationExecutor
    {
        /// <summary>Stable autosave trigger for one-shot Lua execution.</summary>
        /// <summary>
        /// The live per-resume coroutine budget this surface arms — the SAME object the persistent mod
        /// runtime reads when both are composed together, so a host's <c>ScriptContext:SetTimeout</c>
        /// constrains one-off <c>execute_lua</c> chunks exactly as it constrains loaded mods.
        /// </summary>
        public CoreAI.Sandbox.LuaCs.LuaCsCoroutineBudgetSettings CoroutineResumeBudget =>
            _engine.CoroutineResumeBudget;

        public const string ExecuteLuaBackupTrigger = "execute_lua";

        // WHY the concrete type and not IScriptEngine: this surface builds the engine itself and must
        // expose the live coroutine budget it armed, which is not part of the VM-agnostic interface.
        private readonly LuaCsScriptEngine _engine;
        private readonly ILuaCsGameRuntimeBindings _bindings;
        private readonly ILuaExecutionObserver _observer;
        private readonly IConfirmedWorldMutationGate _worldMutationGate;

        /// <summary>
        /// Trusted host/local actor for the plain <see cref="ExecuteAsync(string, CancellationToken)"/>
        /// seam. Production composition supplies it; a null resolver keeps the ACL-world refusal.
        /// </summary>
        public Func<ActorContext> LocalActorResolver { get; set; }

        /// <summary>The shared pre-mutation backup gate this executor runs through; null when none.</summary>
        internal IConfirmedWorldMutationGate WorldMutationGate => _worldMutationGate;

        /// <summary>
        /// Host frame port for the one-shot chunk path. When set, a chunk that runs longer than a few
        /// milliseconds releases the frame instead of freezing the player until its wall-clock budget
        /// cuts it (a runaway was measured freezing a WebGL page for ~6 s). Null keeps the previous
        /// blocking behaviour, so nothing changes for hosts that do not supply one.
        /// </summary>
        public IScriptFrameYielder FrameYielder { get; set; }

        /// <summary>
        /// Raised after <c>execute_lua</c> successfully runs a chunk. Mirrors
        /// <see cref="CoreAI.Infrastructure.Lua.GameLuaToolExecutor.LuaExecutedSuccessfully"/> so scene
        /// demos can persist their own game-specific Lua changes without the generic executor owning
        /// scene policy.
        /// </summary>
        public static event Action<string> LuaExecutedSuccessfully;

        /// <summary>
        /// True when the Lua-CSharp sandbox is available. Lua-CSharp is a managed, AOT-safe VM (the
        /// reason for this migration), so unlike <c>SecureLuaEnvironment</c> this is always supported
        /// — including IL2CPP/WebGL.
        /// </summary>
        public static bool IsSupported => true;

        /// <summary>
        /// Creates the executor without an observability sink.
        /// </summary>
        // WHY: an OPTIONAL sink parameter would force every calling assembly to reference the
        // WHY: engine-free scheduling assembly just to name the type it never passes, so the sink-free
        // WHY: overload keeps that dependency off consumers such as the demos.
        public LuaCsGameToolExecutor(
            LuaCsSecureEnvironment sandbox,
            ILuaCsGameRuntimeBindings bindings,
            ILuaExecutionObserver observer)
            : this(sandbox, bindings, observer, null, null)
        {
        }

        public LuaCsGameToolExecutor(
            LuaCsSecureEnvironment sandbox,
            ILuaCsGameRuntimeBindings bindings,
            ILuaExecutionObserver observer,
            IRbxRuntimeObservabilitySink observability)
            : this(sandbox, bindings, observer, observability, null)
        {
        }

        public LuaCsGameToolExecutor(
            LuaCsSecureEnvironment sandbox,
            ILuaCsGameRuntimeBindings bindings,
            ILuaExecutionObserver observer,
            IRbxRuntimeObservabilitySink observability,
            IConfirmedWorldMutationGate worldMutationGate,
            CoreAI.Sandbox.LuaCs.LuaCsCoroutineBudgetSettings coroutineResumeBudget = null)
        {
            if (sandbox == null)
            {
                throw new ArgumentNullException(nameof(sandbox));
            }

            // WHY the budget is passed in rather than defaulted here: this surface builds its OWN engine
            // over the sandbox the mod runtime already uses, so defaulting would give it a private
            // settings object and a host's ScriptContext:SetTimeout would silently miss every one-off
            // execute_lua chunk while constraining persistent mods.
            _engine = new LuaCsScriptEngine(sandbox, observability,
                coroutineResumeBudget: coroutineResumeBudget);
            _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
            _observer = observer ?? throw new ArgumentNullException(nameof(observer));
            _worldMutationGate = worldMutationGate;
        }

        /// <inheritdoc />
        public async Task<LuaTool.LuaResult> ExecuteAsync(
            string code,
            CancellationToken cancellationToken)
        {
            if (_bindings is IActorScopedLuaCsGameRuntimeBindings scopedCheck
                && scopedCheck.MutationRegistry != null
                && scopedCheck.MutationRegistry.IsWorldAclEnabled)
            {
                ActorContext localActor = LocalActorResolver != null
                    ? LocalActorResolver()
                    : default;
                if (!localActor.IsTrusted)
                {
                    return CreateFailure(
                        "world mutation requires an actor-scoped envelope in this ACL-versioned world: " +
                        "no trusted local actor resolver is configured for the plain execute seam");
                }

                return await ExecuteAsync(code, localActor, cancellationToken);
            }

            try
            {
                return await ExecuteWithBackupAsync(
                    "",
                    token => ExecuteCoreAsync(
                        code,
                        token,
                        _bindings.RegisterGameplayApis),
                    cancellationToken);
            }
            catch (Exception ex)
            {
                return CreateFailure(ex.Message);
            }
        }

        /// <summary>Server-generated envelope execution used by production Lua tools.</summary>
        public async Task<LuaTool.LuaResult> ExecuteAsync(string code, ActorContext actorContext,
            CancellationToken cancellationToken)
        {
            if (!actorContext.IsTrusted)
            {
                return CreateFailure(
                    "actor '<untrusted>' cannot execute Lua: actor context was not issued by an identity provider");
            }

            if (!(_bindings is IActorScopedLuaCsGameRuntimeBindings scopedBindings)
                || scopedBindings.MutationRegistry == null)
            {
                return CreateFailure(
                    "actor '" + actorContext.ActorId + "' cannot execute Lua: the production Rbx mutation surface is not configured");
            }

            try
            {
                return await ExecuteWithBackupAsync(
                    actorContext.ActorId,
                    token => Task.FromResult(
                        scopedBindings.MutationRegistry.ApplyServerGeneratedMutation(
                            actorContext.ActorId,
                            actorContext.Grants.IsUnrestricted,
                            actorContext.WorldId,
                            "execute_lua",
                            () => ExecuteCore(code, token,
                                registry => scopedBindings.RegisterGameplayApis(
                                    registry, actorContext)))),
                    cancellationToken);
            }
            catch (Exception ex)
            {
                return CreateFailure(ex.Message);
            }
        }

        /// <summary>
        /// Executes one actor-scoped world mutation under optimistic revision and idempotency checks.
        /// Kept as an overload so existing executor consumers do not need to reference the envelope
        /// assembly unless they opt into the mutation protocol.
        /// </summary>
        public async Task<LuaTool.LuaResult> ExecuteAsync(string code, ActorContext actorContext,
            MutationEnvelope mutationEnvelope, CancellationToken cancellationToken)
        {
            if (!actorContext.IsTrusted)
            {
                return CreateFailure(
                    "actor '<untrusted>' cannot apply operation '"
                    + mutationEnvelope.OperationId
                    + "': actor context was not issued by an identity provider");
            }

            if (!string.Equals(actorContext.ActorId, mutationEnvelope.ActorId,
                    StringComparison.Ordinal))
            {
                return CreateFailure(
                    "actor '" + actorContext.ActorId + "' cannot apply operation '"
                    + mutationEnvelope.OperationId + "': the mutation envelope belongs to actor '"
                    + mutationEnvelope.ActorId + "'");
            }

            if (!(_bindings is IActorScopedLuaCsGameRuntimeBindings scopedBindings)
                || scopedBindings.MutationRegistry == null)
            {
                return CreateFailure(
                    "actor '" + actorContext.ActorId + "' cannot apply operation '"
                    + mutationEnvelope.OperationId
                    + "': the production Rbx mutation surface is not configured");
            }

            try
            {
                return await ExecuteWithBackupAsync(
                    actorContext.ActorId,
                    token => Task.FromResult(
                        scopedBindings.MutationRegistry.ApplyMutation(
                            mutationEnvelope,
                            () =>
                            {
                                using (scopedBindings.MutationRegistry
                                           .BeginMutationEnvelopeScope(
                                               mutationEnvelope,
                                               actorContext.Grants.IsUnrestricted,
                                               actorContext.WorldId))
                                {
                                    return ExecuteCore(code, token,
                                        registry => scopedBindings.RegisterGameplayApis(
                                            registry, actorContext,
                                            mutationEnvelope));
                                }
                            })),
                    cancellationToken);
            }
            catch (Exception ex)
            {
                return CreateFailure(ex.Message);
            }
        }

        /// <param name="actorId">The actor the gate attributes the change to; empty on the plain seam.</param>
        private Task<LuaTool.LuaResult> ExecuteWithBackupAsync(
            string actorId,
            Func<CancellationToken, Task<LuaTool.LuaResult>> executeAsync,
            CancellationToken cancellationToken)
        {
            if (_worldMutationGate == null)
            {
                return executeAsync(cancellationToken);
            }

            return _worldMutationGate.ExecuteAttributedAsync(
                ExecuteLuaBackupTrigger,
                actorId,
                executeAsync,
                cancellationToken);
        }

        // WHY: still synchronous, and still the path used by the two mutation-envelope overloads above.
        // WHY: InstanceRegistry.ApplyMutation runs its operation inside `lock (_mutationGate)` and under
        // WHY: an ambient MutationEnvelopeScope held in a registry FIELD. Awaiting inside a monitor is not
        // WHY: expressible in C#, and releasing the frame while that ambient scope is pushed would let
        // WHY: whatever else runs in the yielded frame (mod handlers) mutate under THIS actor's envelope.
        // WHY: Yielding those two paths therefore needs an execution-scoped envelope plus an async
        // WHY: mutation protocol, not a signature change here.
        private LuaTool.LuaResult ExecuteCore(string code, CancellationToken cancellationToken,
            Action<LuaCsApiRegistry> registerGameplayApis)
        {
            if (!IsSupported)
            {
                return UnsupportedPlatformResult();
            }

            // WHY: The world bindings are a shared singleton: a prior chunk that died between
            // WHY: coreai_world_begin() and commit/rollback leaves its transaction open, which would
            // WHY: silently buffer this chunk's world commands. Reset before running and abort in the
            // WHY: finally so a leaked transaction can never bleed across runs in either direction.
            (_bindings as ILuaTransactionScope)?.ResetTransactions();
            try
            {
                IScriptState state = PrepareState(registerGameplayApis);
                object[] results = _engine.RunChunk(
                    state,
                    CompileChunk(code),
                    cancellationToken: cancellationToken);
                return ReportSuccess(code, results);
            }
            catch (Exception ex)
            {
                return CreateFailure(ex.Message);
            }
            finally
            {
                (_bindings as ILuaTransactionScope)?.ResetTransactions();
            }
        }

        /// <summary>
        /// The one-shot chunk path that does not hold the host frame: identical to
        /// <see cref="ExecuteCore"/> except that the guard may release the frame while the chunk runs
        /// (see <see cref="FrameYielder"/>).
        /// </summary>
        private async Task<LuaTool.LuaResult> ExecuteCoreAsync(string code, CancellationToken cancellationToken,
            Action<LuaCsApiRegistry> registerGameplayApis)
        {
            if (!IsSupported)
            {
                return UnsupportedPlatformResult();
            }

            (_bindings as ILuaTransactionScope)?.ResetTransactions();
            try
            {
                IScriptState state = PrepareState(registerGameplayApis);
                object[] results = await _engine.RunChunkAsync(
                    state,
                    CompileChunk(code),
                    frameYielder: FrameYielder,
                    cancellationToken: cancellationToken);
                return ReportSuccess(code, results);
            }
            catch (Exception ex)
            {
                return CreateFailure(ex.Message);
            }
            finally
            {
                (_bindings as ILuaTransactionScope)?.ResetTransactions();
            }
        }

        private static LuaTool.LuaResult UnsupportedPlatformResult()
        {
            return new LuaTool.LuaResult
            {
                Success = false,
                Error = "CoreAI Lua execution is disabled on this platform."
            };
        }

        private IScriptState PrepareState(Action<LuaCsApiRegistry> registerGameplayApis)
        {
            LuaCsApiRegistry registry = new();
            registerGameplayApis(registry);
            IScriptState state = _engine.CreateState();
            registry.ApplyTo(state);
            return state;
        }

        // WHY: Downlevel Luau -> Lua 5.2 before compiling so one-shot scripts accept the same
        // WHY: Luau syntax mods do; a downlevel Error is thrown here and caught by the caller as the exec
        // WHY: failure (never a silent raw fallback). Chunk name is stable so diagnostics are legible.
        private string CompileChunk(string code)
        {
            return LuauSourceGate.ToLua52(code, "execute_lua", _engine.TrustedIteratorResolverName);
        }

        private LuaTool.LuaResult ReportSuccess(string code, object[] results)
        {
            string summary = Truncate(Summarize(results), LuaCsAiEnvelopeProcessor.MaxResultSummaryLength,
                "result summary");
            _observer.OnLuaSuccess(summary);
            LuaExecutedSuccessfully?.Invoke(code ?? "");
            return new LuaTool.LuaResult { Success = true, Output = summary };
        }

        private LuaTool.LuaResult CreateFailure(string message)
        {
            string flat = Truncate(
                (message ?? "").Replace("\r", " ").Replace("\n", " ").Trim(),
                LuaCsAiEnvelopeProcessor.MaxErrorMessageLength, "error");
            _observer.OnLuaFailure(flat);
            return new LuaTool.LuaResult { Success = false, Error = flat };
        }

        /// <summary>Renders the chunk's first return value into a printable summary (VM-agnostic).</summary>
        internal static string Summarize(object[] results)
        {
            if (results == null || results.Length == 0)
            {
                return "nil";
            }

            return Stringify(results[0]);
        }

        /// <summary>Max table nesting rendered into the Output summary before values are elided.</summary>
        private const int MaxTableSummaryDepth = 4;

        private static string Stringify(object value)
        {
            IValueMarshaller marshaller = LuaCsValueMarshaller.Instance;
            switch (marshaller.GetKind(value))
            {
                case ScriptValueKind.Nil:
                    return "nil";
                case ScriptValueKind.Boolean:
                    return (bool)marshaller.ToHostValue(value) ? "true" : "false";
                case ScriptValueKind.Number:
                    return ((double)marshaller.ToHostValue(value)).ToString(CultureInfo.InvariantCulture);
                case ScriptValueKind.String:
                    return (string)marshaller.ToHostValue(value) ?? "";
                case ScriptValueKind.Table:
                    return StringifyTable(marshaller, value);
                default:
                    return marshaller.Describe(value);
            }
        }

        // WHY: a returned table otherwise renders as "table: 0x…" (its address), which is useless to the
        // WHY: model — e.g. `return coreai_world_list_prefabs()` or `coreai_world_find(...)` would hand the LLM
        // WHY: an opaque handle instead of the actual names. Convert it to a portable structure and JSON-encode
        // WHY: it so discovery tools are legible; fall back to the plain describe if conversion/encoding fails.
        private static string StringifyTable(IValueMarshaller marshaller, object value)
        {
            try
            {
                object portable = marshaller.ToPortable(value, MaxTableSummaryDepth);
                return Newtonsoft.Json.JsonConvert.SerializeObject(portable);
            }
            catch (Exception)
            {
                return marshaller.Describe(value);
            }
        }

        /// <summary>
        /// Clips Lua output or error text to <paramref name="maxLength"/> with a <c>…[+N chars]</c> marker (it
        /// travels into tool results, payloads and repair prompts) and logs the cut with its numbers.
        /// </summary>
        private static string Truncate(string value, int maxLength, string what)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            {
                return value ?? "";
            }

            string clipped = TruncationMarker.ClipPrefix(value, maxLength, out int dropped);
            CoreAI.Logging.Log.Instance.Info(
                $"[LuaCsGameToolExecutor] Lua {what} clipped: {value.Length} chars total -> {value.Length - dropped} kept, " +
                $"{dropped} dropped (limit {maxLength}).",
                CoreAI.Logging.LogTag.Llm);
            return clipped;
        }
    }
}

namespace CoreAI.Mods.WorldPackages
{
    /// <summary>AI tool that lists autosave packages with metadata.</summary>
    public sealed class ListAutoSavesLlmTool : LlmToolBase, IAIFunctionLlmTool
    {
        /// <summary>The <c>status</c> reported when the autosave ring could not be listed.</summary>
        internal const string ListFailedStatus = "list_failed";

        private readonly IRbxWorldRuntimeService _service;
        private readonly IActorIdentityProvider _identityProvider;
        private readonly string _roleId;

        public ListAutoSavesLlmTool(
            IRbxWorldRuntimeService service,
            IActorIdentityProvider identityProvider,
            string roleId)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _identityProvider = identityProvider ?? throw new ArgumentNullException(nameof(identityProvider));
            _roleId = roleId ?? BuiltInAgentRoleIds.Programmer;
        }

        public override string Name => "list_autosaves";

        public override string Description =>
            "List timestamped, trigger-labelled autosave world packages with name, trigger, timestamp and size.";

        public override string ParametersSchema => JsonParams();

        public AIFunction CreateAIFunction()
        {
            Func<CancellationToken, Task<string>> function = ExecuteAsync;
            return AIFunctionFactory.Create(function, new AIFunctionFactoryOptions
            {
                Name = Name,
                Description = Description
            });
        }

        public Task<string> ExecuteAsync(CancellationToken cancellationToken = default)
        {
            _identityProvider.GetActorContext(_roleId);
            List<object> rows;
            // WHY: every failure except cancellation becomes a result. Listing is read-only, so an I/O,
            // WHY: permission or disposed-session fault changed nothing and the model can retry; thrown,
            // WHY: it crossed the tool invocation boundary and was traced as possibly executed.
            try
            {
                IReadOnlyList<RbxAutoSaveInfo> autosaves = _service.ListAutoSaves()
                    ?? throw new InvalidOperationException("The world service returned no autosave list.");
                rows = new List<object>(autosaves.Count);
                foreach (RbxAutoSaveInfo info in autosaves)
                {
                    rows.Add(new
                    {
                        name = info.FileName,
                        trigger = info.Trigger,
                        timestamp = info.TimestampUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                        size = info.SizeBytes
                    });
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                return Task.FromResult(Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    status = ListFailedStatus,
                    autosaves = Array.Empty<object>(),
                    error = "Listing the autosave ring failed: " + ex.Message
                            + " No autosave was changed. Retry list_autosaves."
                }));
            }

            return Task.FromResult(Newtonsoft.Json.JsonConvert.SerializeObject(new
            {
                autosaves = rows
            }));
        }
    }

    /// <summary>AI tool that requests a confirmed load of a named autosave.</summary>
    public sealed class LoadAutoSaveLlmTool : LlmToolBase, IAIFunctionLlmTool
    {
        /// <summary>The <c>status</c> reported when the named autosave does not exist (e.g. rotated out).</summary>
        internal const string NotFoundStatus = "not_found";

        /// <summary>The <c>status</c> reported when the named autosave is corrupt or above the read limits.</summary>
        internal const string InvalidPackageStatus = "invalid_package";

        /// <summary>The <c>status</c> reported when the named autosave exists but could not be read.</summary>
        internal const string ReadFailedStatus = "read_failed";

        private readonly IRbxWorldRuntimeService _service;
        private readonly IActorIdentityProvider _identityProvider;
        private readonly string _roleId;

        public LoadAutoSaveLlmTool(
            IRbxWorldRuntimeService service,
            IActorIdentityProvider identityProvider,
            string roleId)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _identityProvider = identityProvider ?? throw new ArgumentNullException(nameof(identityProvider));
            _roleId = roleId ?? BuiltInAgentRoleIds.Programmer;
        }

        public override string Name => "load_autosave";

        public override string Description =>
            "Request loading a named autosave package. This never applies directly; the player must confirm the returned request.";

        public override string ParametersSchema => JsonParams(
            ("name", "string", true, "Autosave file name (e.g. 20260902T120000000Z-0000-execute_lua.world)."));

        public AIFunction CreateAIFunction()
        {
            Func<string, CancellationToken, Task<string>> function = ExecuteAsync;
            return AIFunctionFactory.Create(function, new AIFunctionFactoryOptions
            {
                Name = Name,
                Description = Description
            });
        }

        public async Task<string> ExecuteAsync(
            [System.ComponentModel.Description("Autosave file name.")] string name,
            CancellationToken cancellationToken = default)
        {
            // WHY first: see SaveWorldLlmTool - a name the store would throw on is refused as a result.
            if (!RbxWorldPackageNames.TryValidateAutoFileName(name, out string nameError))
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    status = RbxWorldPackageNames.InvalidArgumentStatus,
                    player_confirmation_required = false,
                    request_id = "",
                    slot = name ?? "",
                    world_id = "",
                    error = RbxWorldPackageNames.DescribeInvalidArgument("name", nameError)
                });
            }

            CoreAI.Authority.ActorContext actor = _identityProvider.GetActorContext(_roleId);
            RbxWorldLoadRequest request;
            // WHY: only failures raised while the package is being read are converted. They happen before
            // WHY: any request is queued, so "not executed" is true, and a rotated-away name, a corrupt,
            // WHY: oversized or unreadable file, or a session that was already shut down is an outcome the
            // WHY: model can report or correct. Thrown, it crossed the tool invocation boundary and was
            // WHY: traced as possibly executed. Cancellation propagates.
            try
            {
                request = await _service.RequestAutoLoadAsync(actor, name, cancellationToken);
            }
            catch (RbxWorldLoadRefusedException ex)
            {
                // WHY before the base-class catch: a session-rule refusal carries its own status
                // (network_sessions_active tells the model to wait for clients to leave, not to pick
                // another autosave), and the base-class catch would flatten it to invalid_package.
                return RefuseUnloadable(
                    name,
                    ex.Status,
                    "Autosave '" + name + "' cannot be loaded into this session: " + ex.Message
                    + " The tool was NOT executed.");
            }
            catch (ObjectDisposedException)
            {
                return RefuseUnloadable(
                    name,
                    RbxWorldPackageNames.SessionUnavailableStatus,
                    RbxWorldPackageNames.DescribeSessionUnavailable("Autosave '" + name + "'"));
            }
            catch (System.IO.FileNotFoundException)
            {
                return RefuseUnloadable(name, NotFoundStatus, DescribeNotFound(name));
            }
            catch (System.IO.DirectoryNotFoundException)
            {
                return RefuseUnloadable(name, NotFoundStatus, DescribeNotFound(name));
            }
            catch (RbxWorldPackageException ex)
            {
                return RefuseUnloadable(
                    name,
                    InvalidPackageStatus,
                    "Autosave '" + name + "' is not a loadable world package: " + ex.Message
                    + " The tool was NOT executed. Pick another autosave from list_autosaves.");
            }
            catch (System.IO.IOException ex)
            {
                return RefuseUnloadable(name, ReadFailedStatus, DescribeReadFailure(name, ex));
            }
            catch (UnauthorizedAccessException ex)
            {
                return RefuseUnloadable(name, ReadFailedStatus, DescribeReadFailure(name, ex));
            }
            catch (NotSupportedException ex)
            {
                // WHY this and the next: on a Windows player a name such as CON.world names a device,
                // not a file, and opening it throws one of these instead of an IOException.
                return RefuseUnloadable(name, ReadFailedStatus, DescribeReadFailure(name, ex));
            }
            catch (ArgumentException ex)
            {
                return RefuseUnloadable(name, ReadFailedStatus, DescribeReadFailure(name, ex));
            }

            return Newtonsoft.Json.JsonConvert.SerializeObject(new
            {
                success = false,
                status = "player_confirmation_required",
                player_confirmation_required = request.PlayerConfirmationRequired,
                request_id = request.RequestId,
                slot = request.Slot,
                world_id = request.WorldId
            });
        }

        private static string DescribeNotFound(string name)
        {
            return "Autosave '" + name + "' was not found; it may have been rotated out of the autosave ring."
                   + " The tool was NOT executed. Call list_autosaves and retry with a listed 'name'.";
        }

        private static string DescribeReadFailure(string name, Exception exception)
        {
            return "Autosave '" + name + "' could not be read: " + exception.Message
                   + " The tool was NOT executed. Retry, or pick another autosave from list_autosaves.";
        }

        private static string RefuseUnloadable(string name, string status, string error)
        {
            return Newtonsoft.Json.JsonConvert.SerializeObject(new
            {
                success = false,
                status,
                player_confirmation_required = false,
                request_id = "",
                slot = name,
                world_id = "",
                error
            });
        }
    }
}
