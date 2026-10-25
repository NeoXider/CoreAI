using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Logging;
using Newtonsoft.Json;

namespace CoreAI.Ai
{
    /// <summary>
    /// Persists per-role summaries with atomic filesystem writes and host-owned durability confirmation.
    /// <para>On desktop, private file work runs off the calling thread. WebGL runs MEMFS work inline
    /// and checks the configured host persistence confirmation or automatic-persistence arming hook.
    /// No browser flush completion receipt is implied. Failed writes, deletes, and confirmations throw; missing
    /// files read as empty while corrupt or inaccessible files fail strict async reads.</para>
    /// <para>Canonical-path state is shared across store instances. A VFS mutation remains unconfirmed
    /// until its generation is acknowledged, including when a different instance next reads its fold
    /// marker. An older callback cannot confirm a newer write. Host callbacks run outside file gates,
    /// on the entry context; they own their timeout and cannot recursively access the same file.</para>
    /// <para>Cancellation before the atomic swap prevents mutation. After commit, confirmation runs
    /// without caller cancellation. Synchronous compatibility calls fail promptly if file work is busy
    /// or the requested read still needs async confirmation. Dispose rejects new calls; admitted work
    /// finishes and shared path gates remain valid.</para>
    /// </summary>
    public sealed class FileConversationSummaryStore : IConversationSummaryStore, IAsyncConversationSummaryStore, ITransactionalConversationSummaryStore, IDisposable
    {
        private static readonly JsonSerializerSettings JsonSettings = new()
        {
            Formatting = Formatting.None
        };

        private static readonly SharedPathStateRegistry<PathState> MutationGates =
            new(state => state.Pending != null);
        /// <summary>Shared summary states retained by operations, prepared snapshots or pending confirmation.</summary>
        public static int SharedPathStateCount => MutationGates.Count;
        /// <summary>Committed summary paths still requiring their configured durability confirmation.</summary>
        public static int PendingConfirmationCount => MutationGates.CountWhere(state => state.Pending != null);
        private static readonly AsyncLocal<ConfirmationFrame> CurrentConfirmation = new();

        private sealed class ConfirmationFrame
        {
            internal PathState State;
            internal ConfirmationFrame Previous;
        }

        private sealed class DurabilityRequest
        {
            internal long Generation;
            internal Func<Task> ConfirmAsync;
            internal Action ConfirmSync;
        }

        private sealed class PathState
        {
            public PathState() { }
            internal long Revision { get { lock (_stateGate) return _generation; } }
            internal readonly SemaphoreSlim Gate = new(1, 1);
            private readonly object _stateGate = new();
            private long _generation;
            private DurabilityRequest _pending;

            internal DurabilityRequest RecordMutation(FileConversationSummaryStore owner)
            {
                lock (_stateGate)
                {
                    bool inherit = owner._afterWrite == null && owner._afterWriteAsync == null && _pending != null;
                    _pending = new DurabilityRequest
                    {
                        Generation = ++_generation,
                        ConfirmAsync = inherit ? _pending.ConfirmAsync : owner.ConfirmDurabilityAsync,
                        ConfirmSync = inherit ? _pending.ConfirmSync : owner.ConfirmDurabilitySync
                    };
                    return _pending;
                }
            }

            internal DurabilityRequest Pending
            {
                get { lock (_stateGate) return _pending; }
            }

            internal void Confirmed(DurabilityRequest request)
            {
                lock (_stateGate)
                {
                    // WHY: A callback begun for an older write cannot acknowledge a newer generation.
                    if (_pending != null && _pending.Generation <= request.Generation) _pending = null;
                }
            }
        }

        private readonly string _dir;
        private readonly ILog _log;
        private readonly Func<bool> _afterWrite;
        private readonly Func<CancellationToken, Task<bool>> _afterWriteAsync;

        private readonly object _lifeLock = new();
        private int _activeCount;
        private bool _disposed;

        private sealed class PersistedDto
        {
            public string Summary { get; set; } = "";
            public string RoleId { get; set; }
        }

        /// <summary>Creates store writing to <paramref name="rootDirectory"/>.</summary>
        /// <param name="rootDirectory">Directory path; created on first write.</param>
        /// <param name="log">Optional logger.</param>
        /// <param name="afterWrite">
        /// Synchronous host hook, used by the synchronous API. False or an exception reports an
        /// unconfirmed VFS mutation and leaves the file unreadable through the synchronous API until a
        /// confirmation succeeds. A successful answer confirms the write outright: the two hooks are
        /// two SHAPES of one durability answer, not two stages of one, so a present async hook does not
        /// turn a successful sync answer into a mere queue request.
        /// </param>
        /// <param name="afterWriteAsync">
        /// Async host confirmation, invoked with CancellationToken.None outside file gates on the
        /// calling context. Must bound its own completion time; false or an exception becomes IOException.
        /// If absent, the explicitly configured sync hook is used. No hooks means ordinary filesystem persistence.
        /// </param>
        public FileConversationSummaryStore(
            string rootDirectory,
            ILog log = null,
            Func<bool> afterWrite = null,
            Func<CancellationToken, Task<bool>> afterWriteAsync = null)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory))
            {
                throw new ArgumentException("Root directory is required.", nameof(rootDirectory));
            }

            _dir = Path.GetFullPath(rootDirectory.Trim());
            _log = log;
            _afterWrite = afterWrite;
            _afterWriteAsync = afterWriteAsync;
        }

        /// <summary>
        /// Runs file I/O on the thread pool so it does not stall the caller's (Unity main) thread.
        /// On WebGL (no threads) the work runs inline instead.
        /// </summary>
        private static Task RunOffThread(Action action)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            action();
            return Task.CompletedTask;
#else
            return Task.Run(action);
#endif
        }

        /// <inheritdoc cref="RunOffThread(Action)"/>
        private static Task<T> RunOffThread<T>(Func<T> func)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return Task.FromResult(func());
#else
            return Task.Run(func);
#endif
        }

        /// <inheritdoc />
        public string LoadSummary(string roleId)
        {
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return "";
            }

            EnterOperation();
            try
            {
                using SharedPathStateRegistry<PathState>.Lease pathLease = AcquirePath(GetPath(roleId));
                PathState state = pathLease.State;
                SemaphoreSlim gate = state.Gate;
                if (!gate.Wait(0))
                {
                    throw new InvalidOperationException(
                        "[FileConversationSummaryStore] Target file is busy with an active async operation; " +
                        "await the async API instead of blocking the calling (Unity main) thread on sync I/O.");
                }

                try
                {
                    if (state.Pending != null)
                        throw new InvalidOperationException("Summary durability is unconfirmed; await LoadSummaryAsync before using the stored fold marker.");
                    return LoadSummaryCore(roleId);
                }
                finally
                {
                    gate.Release();
                }
            }
            finally
            {
                ExitOperation();
            }
        }

        /// <summary>
        /// Async variant of <see cref="LoadSummary"/> that performs the file read off the calling thread.
        /// </summary>
        public async Task<string> LoadSummaryAsync(string roleId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return "";
            }

            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            EnterOperation();
            try
            {
                // WHY: awaited with default capture so the logging below runs on the original entry
                // context; only the private file delegate runs on a worker.
                using SharedPathStateRegistry<PathState>.Lease readLease = AcquirePath(GetPath(roleId));
                (string summary, DurabilityRequest pending) = await ReadCommittedAsync(roleId, cancellationToken);
                if (pending != null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await ConfirmPendingAsync(readLease.State, pending);
                }
                return summary;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                LogStorageFailure("Load", ex);
                throw;
            }
            finally
            {
                ExitOperation();
            }
        }

        private async Task<(string Summary, DurabilityRequest Pending)> ReadCommittedAsync(string roleId, CancellationToken cancellationToken)
        {
            using SharedPathStateRegistry<PathState>.Lease pathLease = AcquirePath(GetPath(roleId));
                PathState state = pathLease.State;
            SemaphoreSlim gate = state.Gate;
            await gate.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                string result = await RunOffThread(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return ReadSummaryCoreStrict(roleId);
                });
                return (result, state.Pending);
            }
            finally
            {
                gate.Release();
            }
        }

        private string LoadSummaryCore(string roleId)
        {
            try
            {
                return ReadSummaryCoreStrict(roleId);
            }
            catch (FileNotFoundException)
            {
                return "";
            }
            catch (DirectoryNotFoundException)
            {
                return "";
            }
            catch (Exception ex)
            {
                LogStorageFailure("Load", ex);
                return "";
            }
        }

        /// <summary>
        /// Reads the committed summary; missing file stays empty, corrupt or unreadable storage throws
        /// instead of silently overwriting the existing data.
        /// </summary>
        private string ReadSummaryCoreStrict(string roleId)
        {
            string path = GetPath(roleId);
            EnsureLegacyMigration(roleId);
            ValidateExistingParent(path);
            string json;
            try { json = File.ReadAllText(path); }
            catch (FileNotFoundException) { return ""; }
            catch (DirectoryNotFoundException) { return ""; }
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new InvalidDataException("[FileConversationSummaryStore] Summary file is empty.");
            }

            PersistedDto dto;
            try
            {
                dto = JsonConvert.DeserializeObject<PersistedDto>(json, JsonSettings);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("[FileConversationSummaryStore] Summary file is corrupt.", ex);
            }

            if (dto == null)
            {
                throw new InvalidDataException("[FileConversationSummaryStore] Summary file is corrupt.");
            }

            if (dto.RoleId != null && !string.Equals(dto.RoleId, roleId, StringComparison.Ordinal))
                throw new InvalidDataException("Summary record identity does not match its requested key.");
            return dto.Summary?.Trim() ?? "";
        }

        /// <inheritdoc />
        public void SaveSummary(string roleId, string summary)
        {
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return;
            }

            EnterOperation();
            try
            {
                RequireSynchronousConfirmation();
                using SharedPathStateRegistry<PathState>.Lease pathLease = AcquirePath(GetPath(roleId));
                PathState state = pathLease.State;
                SemaphoreSlim gate = state.Gate;
                if (!gate.Wait(0))
                {
                    throw new InvalidOperationException(
                        "[FileConversationSummaryStore] Target file is busy with an active async operation; " +
                        "await the async API instead of blocking the calling (Unity main) thread on sync I/O.");
                }

                DurabilityRequest pending;
                try
                {
                    EnsureLegacyMigration(roleId);
                    _ = ReadSummaryCoreStrict(roleId);
                    WriteSummaryCore(GetPath(roleId), summary ?? "", roleId);
                    pending = state.RecordMutation(this);
                }
                finally
                {
                    gate.Release();
                }

                ConfirmPendingSync(state, pending);
            }
            finally
            {
                ExitOperation();
            }
        }

        /// <summary>
        /// Async variant of <see cref="SaveSummary"/> that performs the atomic file write off the calling
        /// thread and awaits host durability confirmation on the original entry context.
        /// </summary>
        public async Task SaveSummaryAsync(string roleId, string summary, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return;
            }

            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            EnterOperation();
            try
            {
                // WHY: awaited with default capture so durability confirmation and logging below run on
                // the original entry context outside the file gate.
                string path = GetPath(roleId);
                using SharedPathStateRegistry<PathState>.Lease writeLease = AcquirePath(path);
                DurabilityRequest pending = await CommitSaveAsync(path, roleId, summary ?? "", cancellationToken);
                await ConfirmPendingAsync(writeLease.State, pending);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                LogStorageFailure("Save", ex);
                throw;
            }
            finally
            {
                ExitOperation();
            }
        }

        private async Task<DurabilityRequest> CommitSaveAsync(string path, string roleId, string summary, CancellationToken cancellationToken)
        {
            using SharedPathStateRegistry<PathState>.Lease pathLease = AcquirePath(path);
            PathState state = pathLease.State;
            SemaphoreSlim gate = state.Gate;
            await gate.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await RunOffThread(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    EnsureLegacyMigration(roleId);
                    _ = ReadSummaryCoreStrict(roleId);
                    PersistedDto dto = new() { Summary = summary, RoleId = roleId };
                    string json = JsonConvert.SerializeObject(dto, JsonSettings);
                    AtomicWriteAllText(path, json, cancellationToken);
                });
                return state.RecordMutation(this);
            }
            finally
            {
                gate.Release();
            }
        }

        private void WriteSummaryCore(string path, string summary, string roleId)
        {
            PersistedDto dto = new() { Summary = summary, RoleId = roleId };
            string json = JsonConvert.SerializeObject(dto, JsonSettings);
            try
            {
                // WHY the directory is created inside this try: failing to create it IS a write
                // failure, and outside the try it was the one storage error that reached the caller
                // with nothing in the log — the same failure through the file itself was logged.
                EnsureDir();
                AtomicWriteAllText(path, json);
            }
            catch (Exception ex)
            {
                LogStorageFailure("Save", ex);
                throw;
            }
        }

        private void ConfirmDurabilitySync()
        {
            bool confirmed;
            try { confirmed = _afterWrite == null || _afterWrite(); }
            catch (Exception ex) { throw UnconfirmedDurability(ex); }
            if (!confirmed)
            {
                IOException failure = new(
                    "[FileConversationSummaryStore] VFS write committed but durability was not confirmed.");
                LogStorageFailure("Save", failure);
                throw failure;
            }
        }

        private async Task ConfirmDurabilityAsync()
        {
            bool confirmed;
            try
            {
                // WHY: committed mutation is already durable in-process; the host confirmation must be
                // awaited uncancelled so a caller cancelling after commit still learns the real outcome.
                confirmed = _afterWriteAsync != null
                    ? await _afterWriteAsync(CancellationToken.None)
                    : _afterWrite == null || _afterWrite();
            }
            catch (Exception ex) { throw UnconfirmedDurability(ex); }

            if (!confirmed)
            {
                throw new IOException(
                    "[FileConversationSummaryStore] VFS write committed but durability was not confirmed.");
            }
        }

        private static IOException UnconfirmedDurability(Exception cause) => new(
            "[FileConversationSummaryStore] VFS write committed but durability was not confirmed.", cause);

        private void RequireSynchronousConfirmation()
        {
            if (_afterWrite == null && _afterWriteAsync != null)
                throw new InvalidOperationException("Only async durability confirmation is configured; use the async summary API.");
        }

        /// <inheritdoc />
        public void ClearSummary(string roleId)
        {
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return;
            }

            EnterOperation();
            try
            {
                RequireSynchronousConfirmation();
                string path = GetPath(roleId);
                using SharedPathStateRegistry<PathState>.Lease pathLease = AcquirePath(path);
            PathState state = pathLease.State;
                SemaphoreSlim gate = state.Gate;
                if (!gate.Wait(0))
                {
                    throw new InvalidOperationException(
                        "[FileConversationSummaryStore] Target file is busy with an active async operation; " +
                        "await the async API instead of blocking the calling (Unity main) thread on sync I/O.");
                }

                DurabilityRequest pending;
                try
                {
                    EnsureLegacyMigration(roleId);
                    DeleteSummaryCore(path);
                    pending = state.RecordMutation(this);
                }
                finally
                {
                    gate.Release();
                }

                // WHY: A retry after unconfirmed deletion must flush again even when the VFS file is already absent.
                ConfirmPendingSync(state, pending);
            }
            finally
            {
                ExitOperation();
            }
        }

        /// <summary>
        /// Async variant of <see cref="ClearSummary"/> that performs the file delete off the calling
        /// thread and awaits host durability confirmation on the original entry context.
        /// </summary>
        public async Task ClearSummaryAsync(string roleId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return;
            }

            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            EnterOperation();
            try
            {
                // WHY: awaited with default capture so durability confirmation and logging below run on
                // the original entry context outside the file gate.
                string path = GetPath(roleId);
                using SharedPathStateRegistry<PathState>.Lease clearLease = AcquirePath(path);
                DurabilityRequest pending = await CommitClearAsync(path, roleId, cancellationToken);
                await ConfirmPendingAsync(clearLease.State, pending);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                LogStorageFailure("Clear", ex);
                throw;
            }
            finally
            {
                ExitOperation();
            }
        }

        private async Task<DurabilityRequest> CommitClearAsync(string path, string roleId, CancellationToken cancellationToken)
        {
            using SharedPathStateRegistry<PathState>.Lease pathLease = AcquirePath(path);
            PathState state = pathLease.State;
            SemaphoreSlim gate = state.Gate;
            await gate.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await RunOffThread(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    EnsureLegacyMigration(roleId);
                    return TryDeleteFile(path);
                });
                return state.RecordMutation(this);
            }
            finally
            {
                gate.Release();
            }
        }

        /// <inheritdoc />
        public ConversationSummaryTransaction OpenSummaryTransaction(string roleId)
        {
            EnterOperation();
            SharedPathStateRegistry<PathState>.Lease lease = null;
            try
            {
                string path = GetPath(roleId);
                lease = AcquirePath(path);
                PathState state = lease.State;
                if (!state.Gate.Wait(0)) throw new InvalidOperationException("Summary file is busy; use asynchronous context preparation.");
                string summary;
                long baseline;
                try
                {
                    if (state.Pending != null) throw new InvalidOperationException("Summary durability is unconfirmed; use asynchronous context preparation.");
                    summary = ReadSummaryCoreStrict(roleId);
                    baseline = state.Revision;
                }
                finally { state.Gate.Release(); }
                return CreateTransaction(roleId, path, state, baseline, summary, lease);
            }
            catch { lease?.Dispose(); throw; }
            finally { ExitOperation(); }
        }

        /// <inheritdoc />
        public async Task<ConversationSummaryTransaction> OpenSummaryTransactionAsync(string roleId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnterOperation();
            SharedPathStateRegistry<PathState>.Lease lease = null;
            try
            {
                string path = GetPath(roleId);
                lease = AcquirePath(path);
                PathState state = lease.State;
                while (true)
                {
                    await state.Gate.WaitAsync(cancellationToken);
                    string summary;
                    long baseline;
                    DurabilityRequest pending;
                    try
                    {
                        summary = await RunOffThread(() => ReadSummaryCoreStrict(roleId));
                        baseline = state.Revision;
                        pending = state.Pending;
                    }
                    finally { state.Gate.Release(); }
                    if (pending == null) return CreateTransaction(roleId, path, state, baseline, summary, lease);
                    await ConfirmPendingAsync(state, pending);
                }
            }
            catch { lease?.Dispose(); throw; }
            finally { ExitOperation(); }
        }

        private ConversationSummaryTransaction CreateTransaction(string roleId, string path, PathState state,
            long baseline, string summary, IDisposable lease)
        {
            DurabilityRequest ownPending = null;
            bool Commit(string value)
            {
                EnterOperation();
                try
                {
                    RequireSynchronousConfirmation();
                    if (!state.Gate.Wait(0)) throw new InvalidOperationException("Summary file is busy; use asynchronous context preparation.");
                    try
                    {
                        if (state.Revision != (ownPending?.Generation ?? baseline)) return false;
                        if (ownPending == null)
                        {
                            WriteSummaryCore(path, value, roleId);
                            ownPending = state.RecordMutation(this);
                        }
                    }
                    finally { state.Gate.Release(); }
                    ConfirmPendingSync(state, ownPending);
                    return state.Revision == ownPending.Generation;
                }
                finally { ExitOperation(); }
            }

            async Task<bool> CommitAsync(string value, CancellationToken token)
            {
                EnterOperation();
                try
                {
                    await state.Gate.WaitAsync(token);
                    try
                    {
                        if (state.Revision != (ownPending?.Generation ?? baseline)) return false;
                        if (ownPending == null)
                        {
                            await RunOffThread(() =>
                            {
                                token.ThrowIfCancellationRequested();
                                PersistedDto dto = new() { Summary = value, RoleId = roleId };
                                AtomicWriteAllText(path, JsonConvert.SerializeObject(dto, JsonSettings), token);
                            });
                            ownPending = state.RecordMutation(this);
                        }
                    }
                    finally { state.Gate.Release(); }
                    await ConfirmPendingAsync(state, ownPending);
                    return state.Revision == ownPending.Generation;
                }
                finally { ExitOperation(); }
            }
            return new ConversationSummaryTransaction(summary, Commit, CommitAsync, lease);
        }

        private bool DeleteSummaryCore(string path)
        {
            try
            {
                return TryDeleteFile(path);
            }
            catch (Exception ex)
            {
                LogStorageFailure("Clear", ex);
                throw;
            }
        }

        private static bool TryDeleteFile(string path)
        {
            ValidateExistingParent(path);
            try { _ = File.GetAttributes(path); }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
            File.Delete(path);
            return true;
        }

        /// <summary>Distinguishes an absent directory from an unreadable path or a file used as a directory.</summary>
        private static void ValidateExistingParent(string path)
        {
            string parent = Path.GetDirectoryName(path);
            while (!string.IsNullOrEmpty(parent))
            {
                try
                {
                    FileAttributes attributes = File.GetAttributes(parent);
                    if ((attributes & FileAttributes.Directory) == 0)
                        throw new IOException("Summary storage parent is not a directory.");
                    return;
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                parent = Path.GetDirectoryName(parent);
            }
        }

        private void LogStorageFailure(string operation, Exception ex)
        {
            // WHY: roleId may be a scoped persistence key and exception messages commonly repeat the
            // filesystem path. Keep diagnostics actionable without disclosing either identity data or keys.
            _log?.Error($"[FileConversationSummaryStore] {operation} failed ({ex.GetType().Name}).");
        }

        /// <summary>
        /// Rejects new operations; accepted operations finish safely. Shared path leases retain gates
        /// through active operations, prepared generations, queued waiters and pending confirmation.
        /// </summary>
        public void Dispose()
        {
            lock (_lifeLock)
            {
                _disposed = true;
            }
        }

        private void ThrowIfDisposed()
        {
            lock (_lifeLock)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(FileConversationSummaryStore));
                }
            }
        }

        private void EnterOperation()
        {
            lock (_lifeLock)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(FileConversationSummaryStore));
                }

                _activeCount++;
            }
        }

        private void ExitOperation()
        {
            lock (_lifeLock)
            {
                _activeCount--;
            }
        }

        private static SharedPathStateRegistry<PathState>.Lease AcquirePath(string path)
        {
            SharedPathStateRegistry<PathState>.Lease lease = MutationGates.Acquire(path);
            for (ConfirmationFrame frame = CurrentConfirmation.Value; frame != null; frame = frame.Previous)
            {
                if (ReferenceEquals(frame.State, lease.State))
                {
                    lease.Dispose();
                    throw new InvalidOperationException("A summary durability callback cannot reenter the same summary file.");
                }
            }
            return lease;
        }

        private static async Task ConfirmPendingAsync(PathState state, DurabilityRequest pending)
        {
            ConfirmationFrame previous = CurrentConfirmation.Value;
            CurrentConfirmation.Value = new ConfirmationFrame { State = state, Previous = previous };
            try
            {
                await pending.ConfirmAsync();
                state.Confirmed(pending);
            }
            finally { CurrentConfirmation.Value = previous; }
        }

        private static void ConfirmPendingSync(PathState state, DurabilityRequest pending)
        {
            ConfirmationFrame previous = CurrentConfirmation.Value;
            CurrentConfirmation.Value = new ConfirmationFrame { State = state, Previous = previous };
            try
            {
                pending.ConfirmSync();
                // WHY unconditional: the synchronous hook used to be treated as a mere QUEUE request
                // whenever an async hook was configured too, leaving the mark parked "until the async
                // one confirms". That rested on the two hooks being different things - a manual
                // FS.syncfs handshake behind the async one. That channel is gone: both hooks now carry
                // the SAME immediate engine answer (CoreAiWebGlPersistence.Sync / SyncAsync), which is
                // exactly the pair CoreAILifetimeScope configures. Nothing ever cleared the mark, so a
                // synchronous save poisoned the file and the next synchronous read threw - on the live
                // chat-history reset and session-inspector paths. A hook that says false still throws
                // above and leaves the mark, which is the guarantee worth keeping.
                state.Confirmed(pending);
            }
            finally { CurrentConfirmation.Value = previous; }
        }

        private string GetPath(string roleId)
        {
            return Path.Combine(_dir, $"{SanitizedFileStem(roleId)}.json");
        }

        /// <summary>
        /// Maps a raw role id to a unique file stem. Invalid filename characters are replaced, and
        /// when the replacement changed anything a short hash of the raw id is appended so distinct
        /// ids like "A/B" and "A_B" cannot collide on the same file.
        /// </summary>
        internal static string SanitizedFileStem(string roleId)
        {
            return AgentMemoryFileKey.FileStem(roleId);
        }

        private void EnsureLegacyMigration(string roleId)
        {
            AgentMemoryFileKey.MigrateLegacy(_dir, roleId, ".json", path =>
            {
                PersistedDto dto = JsonConvert.DeserializeObject<PersistedDto>(File.ReadAllText(path), JsonSettings)
                    ?? throw new InvalidDataException("Legacy summary is corrupt.");
                if (dto.RoleId != null ? !string.Equals(dto.RoleId, roleId, StringComparison.Ordinal)
                    : !AgentMemoryFileKey.CanAdoptUntaggedLegacy(roleId, path, ".json"))
                    throw new InvalidDataException("Legacy summary identity is ambiguous; explicit identity repair is required.");
            });
        }

        private void EnsureDir()
        {
            if (!Directory.Exists(_dir))
            {
                Directory.CreateDirectory(_dir);
            }
        }

        /// <summary>
        /// Writes <paramref name="contents"/> to <paramref name="path"/> atomically by writing to a unique
        /// temp file first and then swapping it into place, so a crash mid-write cannot corrupt the existing
        /// file and concurrent writers from distinct store instances cannot share one temp path. The final
        /// file name and JSON shape are unchanged. Callers hold the per-path gate, so the
        /// exists/replace-versus-move decision cannot race another in-process writer for the same target.
        /// </summary>
        private static void AtomicWriteAllText(string path, string contents, CancellationToken cancellationToken = default)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string tmpPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(tmpPath, contents);
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(path))
                {
                    File.Replace(tmpPath, path, null);
                }
                else
                {
                    File.Move(tmpPath, path);
                }
            }
            finally
            {
                if (File.Exists(tmpPath))
                {
                    try
                    {
                        File.Delete(tmpPath);
                    }
                    catch
                    {
                        /* best-effort cleanup */
                    }
                }
            }
        }
    }
}
