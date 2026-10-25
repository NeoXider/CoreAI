using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using CoreAI.Audit;
using CoreAI.Infrastructure;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using VContainer;

namespace CoreAI.Features.Audit
{
    /// <summary>
    /// Buffers audit entries with in-process coordination for a canonical file path. Construct on
    /// Unity's main thread. Hosts own folder isolation; cross-process writers are not coordinated.
    /// </summary>
    public sealed class AuditLogWriter : IAuditLog, IDisposable
    {
        private readonly ConcurrentQueue<AuditEntry> _queue = new();
        private readonly string _folder;
        private readonly string _filePath;
        private readonly JsonSerializerSettings _jsonSettings;
        private readonly object _flushGate = new();
        private readonly object _cancellationGate = new();
        private readonly AuditDiagnosticBuffer _diagnostics;
        private readonly AuditDiagnosticPump _diagnosticPump;
        private readonly CancellationTokenSource _cts = new();
        private AuditPathStateRegistry.Lease _pathLease;
        private long _queueCount;
        private long _droppedCount;
        private long _lastReportedDroppedCount;
        private int _disposeStarted;
        private bool _cancellationDisposed;
#if !UNITY_WEBGL
        private readonly Thread _worker;
        private int _workerCompleted;
#endif

        private const int FlushIntervalMs = 500;
        private const int MaxQueueSize = 10_000;
        private static readonly TimeSpan DisposeDrainDeadline = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan WorkerJoinTimeout = DisposeDrainDeadline + TimeSpan.FromMilliseconds(500);

        /// <summary>Simulates a failed append before disk mutation for deterministic failure coverage.</summary>
        internal Func<bool> SimulateWriteFailureForTesting;

        internal Action AfterFirstLineFlushedForTesting;
        internal Action BeforeRollbackForTesting;
        internal Func<bool> PersistenceArmedForTesting;

        /// <summary>
        /// Maximum process-wide retained audit paths, including quarantines. Defaults to 1024.
        /// Must be positive and at least the current retained path count.
        /// </summary>
        public static int SharedPathCapacity
        {
            get => AuditPathStateRegistry.Capacity;
            set => AuditPathStateRegistry.Capacity = value;
        }

        /// <summary>
        /// Clears an existing process-local quarantine only after externally repaired bytes match
        /// the saved length and verifier-valid sequence/hash. Never writes or repairs file bytes.
        /// Call on Unity's main thread because the reused verifier can emit engine-backed diagnostics.
        /// Returns false with an error if no quarantine exists or verification cannot confirm recovery.
        /// </summary>
        public static bool TryRecoverQuarantinedPath(string folder, out string error)
        {
            error = "";
            if (string.IsNullOrWhiteSpace(folder))
            {
                error = "An audit folder is required.";
                return false;
            }

            if (!PlayerLoopHelper.IsMainThread)
            {
                error = "Audit recovery must run on Unity's main thread.";
                return false;
            }

            try
            {
                string path = Path.Combine(Path.GetFullPath(folder), "audit.jsonl");
                using AuditPathStateRegistry.Lease operation = AuditPathStateRegistry.TryAcquireExisting(path);
                if (operation == null)
                {
                    error = "The audit path is not quarantined in this process.";
                    return false;
                }

                AuditPathStateRegistry.State state = operation.SharedState;
                lock (state.Gate)
                {
                    if (!state.Quarantined)
                    {
                        error = "The audit path is not quarantined in this process.";
                        return false;
                    }

                    long length = File.Exists(path) ? new FileInfo(path).Length : 0;
                    if (length != state.RecoveryLength)
                    {
                        error = $"Audit recovery requires exactly {state.RecoveryLength} pre-append bytes; found {length}. " +
                            GetQuarantineReason(state);
                        return false;
                    }

                    AuditVerifyResult verification = AuditLogVerifier.Verify(path);
                    if (!verification.Ok)
                    {
                        error = "Audit recovery verification failed: " + verification.Error;
                        return false;
                    }

                    JObject tail = PeekLastLine(path);
                    long sequence = tail == null ? 0 : (long?)tail["Seq"] ?? 0;
                    string hash = tail == null ? "" : (string)tail["hash"] ?? "";
                    if (sequence != state.RecoverySequence ||
                        !string.Equals(hash, state.RecoveryHash, StringComparison.Ordinal))
                    {
                        error = "Audit recovery requires the saved pre-append sequence and hash head.";
                        return false;
                    }

                    state.Sequence = state.RecoverySequence;
                    state.PreviousHash = state.RecoveryHash;
                    state.AppendFailure = "";
                    state.RollbackFailure = "";
                    Volatile.Write(ref state.Quarantined, false);
                    return true;
                }
            }
            catch (Exception ex)
            {
                error = "Audit recovery could not verify the file: " + ex.Message;
                return false;
            }
        }

        // WHY: VContainer must select the default constructor instead of resolving folder/options.
        [Inject]
        public AuditLogWriter()
            : this(Path.Combine(Application.persistentDataPath, CoreAiPersistentPaths.RootFolderName, "Audit"))
        {
        }

        internal AuditLogWriter(string folder) : this(folder, new AuditLogWriterOptions())
        {
        }

        /// <summary>
        /// Creates a writer with copied limits and a main-thread diagnostic sink. Writers sharing a
        /// canonical path must supply identical storage limits. No cross-process lock is acquired.
        /// </summary>
        public AuditLogWriter(string folder, AuditLogWriterOptions options)
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                throw new ArgumentException("An audit folder is required.", nameof(folder));
            }

            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            options.Validate();
            if (!PlayerLoopHelper.IsMainThread)
            {
                throw new InvalidOperationException("AuditLogWriter must be constructed on Unity's main thread.");
            }

            _folder = Path.GetFullPath(folder);
            _filePath = Path.Combine(_folder, "audit.jsonl");
            _jsonSettings = new JsonSerializerSettings
            {
                Formatting = Formatting.None,
                NullValueHandling = NullValueHandling.Ignore
            };
            _diagnostics = new AuditDiagnosticBuffer(options);
            _diagnosticPump = new AuditDiagnosticPump(_diagnostics);

            try
            {
                _pathLease = AuditPathStateRegistry.Acquire(_filePath, options.MaxFileSizeBytes,
                    options.MaxRetainedRotatedFiles);
                AuditPathStateRegistry.State state = _pathLease.SharedState;
                lock (state.Gate)
                {
                    ThrowIfQuarantined(state);
                    Directory.CreateDirectory(_folder);
                    if (!state.Initialized)
                    {
                        ResumeChain(state);
                        ThrowIfQuarantined(state);
                        state.Initialized = true;
                        PruneRotatedFilesSafely(state);
                    }
                }

                PlayerLoopHelper.AddAction(PlayerLoopTiming.Update, _diagnosticPump);
#if UNITY_WEBGL
                FlushLoop(_cts.Token).Forget();
#else
                CancellationToken workerToken = _cts.Token;
                _worker = new Thread(() => WorkerLoop(workerToken))
                {
                    IsBackground = true,
                    Name = "CoreAI-AuditLogWriter"
                };
                _worker.Start();
#endif
            }
            catch
            {
                _diagnosticPump.Dispose();
                _diagnostics.Close();
                Interlocked.Exchange(ref _pathLease, null)?.Dispose();
                DisposeCancellationSource();
                throw;
            }
        }

        internal string FilePath => _filePath;
        internal long DroppedCount => Interlocked.Read(ref _droppedCount);
        internal AuditDiagnosticPump DiagnosticPumpForTesting => _diagnosticPump;

        /// <summary>Current number of buffered diagnostics, bounded by the configured capacity.</summary>
        public int PendingDiagnosticCount => _diagnostics.Count;

        /// <summary>Cumulative diagnostics discarded by overflow or teardown, including late worker reports.</summary>
        public long DroppedDiagnosticCount => _diagnostics.DroppedCount;

        /// <summary>Cumulative diagnostic sink exceptions, isolated from file flushing and teardown.</summary>
        public long DiagnosticDeliveryFailureCount => _diagnostics.DeliveryFailureCount;

        public void Record(AuditEntry entry)
        {
            if (Volatile.Read(ref _disposeStarted) != 0)
            {
                return;
            }

            EnqueueRaw(entry);
            while (Interlocked.Read(ref _queueCount) > MaxQueueSize)
            {
                if (!_queue.TryDequeue(out _))
                {
                    break;
                }

                Interlocked.Decrement(ref _queueCount);
                Interlocked.Increment(ref _droppedCount);
            }
        }

        private void EnqueueRaw(AuditEntry entry)
        {
            _queue.Enqueue(entry);
            Interlocked.Increment(ref _queueCount);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            {
                return;
            }

            DateTime deadline = DateTime.UtcNow + WorkerJoinTimeout;
            _diagnosticPump.Dispose();
            try
            {
                lock (_cancellationGate)
                {
                    if (!_cancellationDisposed)
                    {
                        _cts.Cancel();
                    }
                }

#if !UNITY_WEBGL
                bool joined = _worker == null || (Thread.CurrentThread != _worker &&
                    _worker.Join(RemainingMilliseconds(deadline)));
                if (joined)
                {
                    DrainOnDispose(deadline);
                }
                else if (!_queue.IsEmpty)
                {
                    EnqueueLog(LogType.Warning,
                        "[AuditLogWriter] Dispose drain deadline reached with entries still queued.");
                }
#else
                DrainOnDispose(deadline);
#endif
                _diagnostics.Drain();
            }
            finally
            {
                _diagnosticPump.Dispose();
                Interlocked.Exchange(ref _pathLease, null)?.Dispose();
                _diagnostics.Close();
#if !UNITY_WEBGL
                if (_worker == null || Volatile.Read(ref _workerCompleted) != 0)
#endif
                {
                    DisposeCancellationSource();
                }
            }
        }

        /// <summary>Synchronously flushes and pumps diagnostics on the main thread.</summary>
        internal void FlushForTesting()
        {
            FlushForTesting(true);
        }

        internal void FlushForTesting(bool drainDiagnostics)
        {
            FlushBatch();
            if (drainDiagnostics)
            {
                _diagnostics.Drain();
            }
        }

        internal void RotateForTesting()
        {
            using AuditPathStateRegistry.Lease operation = Volatile.Read(ref _pathLease)?.Retain();
            if (operation == null)
            {
                return;
            }

            lock (_flushGate)
            {
                lock (operation.SharedState.Gate)
                {
                    RotateNow(operation.SharedState);
                }
            }
        }

        private void DisposeCancellationSource()
        {
            lock (_cancellationGate)
            {
                if (!_cancellationDisposed)
                {
                    _cts.Dispose();
                    _cancellationDisposed = true;
                }
            }
        }

        private static int RemainingMilliseconds(DateTime deadline)
        {
            return (int)Math.Max(0, Math.Min(int.MaxValue, (deadline - DateTime.UtcNow).TotalMilliseconds));
        }

        private void DrainOnDispose(DateTime deadline)
        {
            while (!_queue.IsEmpty && RemainingMilliseconds(deadline) > 0)
            {
                if (!TryFlushBatch(deadline))
                {
                    break;
                }
            }

            if (!_queue.IsEmpty)
            {
                EnqueueLog(LogType.Warning,
                    "[AuditLogWriter] Dispose completed with entries still queued after a failed or timed-out drain.");
            }
        }

        private void ResumeChain(AuditPathStateRegistry.State state)
        {
            InitializeRotationIndex(state);
            if (!File.Exists(_filePath))
            {
                return;
            }

            try
            {
                ReadTailState(state);
            }
            catch (Exception ex)
            {
                EnqueueLog(LogType.Error,
                    $"[AuditLogWriter] Failed to resume audit chain, starting fresh: {ex.Message}");
                state.Sequence = 0;
                state.PreviousHash = "";
                EnqueueRaw(AuditEntry.ForChainReset(0, "system", $"resume failed: {ex.Message}"));
                FlushBatch();
                return;
            }

            try
            {
                if (NeedsRotation(state))
                {
                    RotateNow(state);
                }
            }
            catch (Exception ex)
            {
                EnqueueLog(LogType.Warning,
                    $"[AuditLogWriter] Startup rotation failed, will retry on next flush: {ex.Message}");
            }
        }

        private void ReadTailState(AuditPathStateRegistry.State state)
        {
            string lastLine = null;
            long count = 0;
            using (StreamReader reader = new(_filePath))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (count == 0)
                    {
                        JObject first = TryParseLine(line);
                        if (first != null && (int?)first["Kind"] == (int)AuditEntryKind.RotationAnchor &&
                            TryGetRotatedIndex((string)first["ToolName"], out long index))
                        {
                            state.LastRotationIndex = Math.Max(state.LastRotationIndex, index);
                        }
                    }

                    lastLine = line;
                    count++;
                }
            }

            state.Sequence = count;
            if (lastLine == null)
            {
                return;
            }

            try
            {
                JObject tail = ParseLine(lastLine);
                state.Sequence = (long?)tail["Seq"] ?? count;
                state.PreviousHash = (string)tail["hash"] ?? "";
                state.RotationPending = (int?)tail["Kind"] == (int)AuditEntryKind.RotationMarker;
            }
            catch (Exception ex)
            {
                EnqueueLog(LogType.Error,
                    $"[AuditLogWriter] Audit log tail line is corrupt, chain cannot be resumed: {ex.Message}");
                state.PreviousHash = "";
                EnqueueRaw(AuditEntry.ForChainReset(0, "system", $"corrupt tail line: {ex.Message}"));
                FlushBatch();
            }
        }

        private bool NeedsRotation(AuditPathStateRegistry.State state)
        {
            return state.RotationPending ||
                (File.Exists(_filePath) && new FileInfo(_filePath).Length > state.MaxFileSizeBytes);
        }

        /// <summary>Links a completed marker/anchor rotation before attempting best-effort retention.</summary>
        private void RotateNow(AuditPathStateRegistry.State state)
        {
            ThrowIfQuarantined(state);
            JObject tail = PeekLastLine(_filePath);
            string markerHash;
            if (tail != null && (int?)tail["Kind"] == (int)AuditEntryKind.RotationMarker)
            {
                markerHash = (string)tail["hash"] ?? state.PreviousHash;
                state.Sequence = (long?)tail["Seq"] ?? state.Sequence;
            }
            else
            {
                long markerSequence = state.Sequence + 1;
                AuditEntry marker = AuditEntry.ForRotationMarker(markerSequence, "system", state.PreviousHash);
                string markerPreimage = JsonConvert.SerializeObject(marker, _jsonSettings);
                markerHash = AuditHash.Chain(state.PreviousHash, markerPreimage);
                AppendLinesToFile(_filePath,
                    new List<string> { JsonConvert.SerializeObject(marker.WithHash(markerHash), _jsonSettings) }, state);
                state.Sequence = markerSequence;
            }

            // WHY: A persisted marker belongs to the shared head even when replacement fails.
            state.PreviousHash = markerHash;
            state.RotationPending = true;
            long rotationIndex = state.LastRotationIndex;
            string rotatedPath;
            do
            {
                rotationIndex = checked(rotationIndex + 1);
                rotatedPath = Path.Combine(_folder, GetRotatedName(rotationIndex));
            } while (File.Exists(rotatedPath) || Directory.Exists(rotatedPath));

            AuditEntry anchor = AuditEntry.ForRotationAnchor(0, "system", Path.GetFileName(rotatedPath), markerHash);
            string anchorPreimage = JsonConvert.SerializeObject(anchor, _jsonSettings);
            string anchorHash = AuditHash.Chain(markerHash, anchorPreimage);
            string anchorPath = _filePath + ".rotation.tmp";
            File.WriteAllText(anchorPath,
                JsonConvert.SerializeObject(anchor.WithHash(anchorHash), _jsonSettings) + Environment.NewLine);
            File.Replace(anchorPath, _filePath, rotatedPath);

            state.Sequence = 0;
            state.PreviousHash = anchorHash;
            state.RotationPending = false;
            state.LastRotationIndex = rotationIndex;
            ReportPersistence();
            PruneRotatedFilesSafely(state);
        }

        private void InitializeRotationIndex(AuditPathStateRegistry.State state)
        {
            foreach (string path in Directory.EnumerateFiles(_folder, "audit_*.jsonl", SearchOption.TopDirectoryOnly))
            {
                if (TryGetRotatedIndex(Path.GetFileName(path), out long index))
                {
                    state.LastRotationIndex = Math.Max(state.LastRotationIndex, index);
                }
            }
        }

        private void PruneRotatedFilesSafely(AuditPathStateRegistry.State state)
        {
            if (state.Quarantined)
            {
                return;
            }

            try
            {
                List<(long Index, string Path)> owned = new();
                foreach (string path in Directory.EnumerateFiles(_folder, "audit_*.jsonl", SearchOption.TopDirectoryOnly))
                {
                    if (!TryGetRotatedIndex(Path.GetFileName(path), out long index) ||
                        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    JObject tail = PeekLastLine(path);
                    if (tail != null && (int?)tail["Kind"] == (int)AuditEntryKind.RotationMarker &&
                        (string)tail["PolicyDecision"] == "rotated" && !string.IsNullOrEmpty((string)tail["hash"]))
                    {
                        owned.Add((index, path));
                    }
                }

                owned.Sort((left, right) => left.Index.CompareTo(right.Index));
                for (int i = 0; i < owned.Count - state.MaxRetainedRotatedFiles; i++)
                {
                    File.Delete(owned[i].Path);
                }

                if (owned.Count > state.MaxRetainedRotatedFiles)
                {
                    ReportPersistence();
                }
            }
            catch (Exception ex)
            {
                // WHY: Rotation already committed; pruning failure must not requeue its entries.
                EnqueueLog(LogType.Warning, $"[AuditLogWriter] Rotated-file retention failed: {ex.Message}");
            }
        }

        private static string GetRotatedName(long index)
        {
            return "audit_" + index.ToString("D4", CultureInfo.InvariantCulture) + ".jsonl";
        }

        private static bool TryGetRotatedIndex(string name, out long index)
        {
            index = 0;
            const string prefix = "audit_";
            const string suffix = ".jsonl";
            if (string.IsNullOrEmpty(name) || !name.StartsWith(prefix, StringComparison.Ordinal) ||
                !name.EndsWith(suffix, StringComparison.Ordinal))
            {
                return false;
            }

            string digits = name.Substring(prefix.Length, name.Length - prefix.Length - suffix.Length);
            return digits.Length >= 4 && long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture,
                out index) && index > 0 && string.Equals(name, GetRotatedName(index), StringComparison.Ordinal);
        }

        private static JObject PeekLastLine(string path)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long start = Math.Max(0, stream.Length - 64 * 1024);
            stream.Seek(start, SeekOrigin.Begin);
            using StreamReader reader = new(stream);
            if (start > 0)
            {
                reader.ReadLine();
            }

            string last = null;
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                last = line;
            }

            if (last == null && start > 0)
            {
                stream.Seek(0, SeekOrigin.Begin);
                reader.DiscardBufferedData();
                while ((line = reader.ReadLine()) != null)
                {
                    last = line;
                }
            }

            return TryParseLine(last);
        }

        private static JObject TryParseLine(string line)
        {
            try
            {
                return string.IsNullOrWhiteSpace(line) ? null : ParseLine(line);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static JObject ParseLine(string line)
        {
            using StringReader stringReader = new(line);
            using JsonTextReader jsonReader = new(stringReader) { DateParseHandling = DateParseHandling.None };
            return JObject.Load(jsonReader);
        }

#if UNITY_WEBGL
        private async UniTaskVoid FlushLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await UniTask.Delay(FlushIntervalMs, cancellationToken: ct);
                    FlushBatch();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    EnqueueLog(LogType.Warning, $"[AuditLogWriter] Flush failed: {ex.Message}");
                }
            }
        }
#else
        private void WorkerLoop(CancellationToken ct)
        {
            try
            {
                while (!ct.WaitHandle.WaitOne(FlushIntervalMs))
                {
                    try
                    {
                        FlushBatch();
                    }
                    catch (Exception ex)
                    {
                        EnqueueLog(LogType.Warning, $"[AuditLogWriter] Flush failed: {ex.Message}");
                    }
                }

                DrainOnDispose(DateTime.UtcNow + DisposeDrainDeadline);
            }
            finally
            {
                Volatile.Write(ref _workerCompleted, 1);
                if (Volatile.Read(ref _disposeStarted) != 0)
                {
                    DisposeCancellationSource();
                }
            }
        }
#endif

        private void EnqueueLog(LogType level, string message)
        {
            _diagnostics.Enqueue(level, message);
        }

        private void FlushBatch()
        {
            if (_queue.IsEmpty && Interlocked.Read(ref _droppedCount) ==
                Interlocked.Read(ref _lastReportedDroppedCount))
            {
                return;
            }

            using AuditPathStateRegistry.Lease operation = Volatile.Read(ref _pathLease)?.Retain();
            if (operation == null)
            {
                return;
            }

            lock (_flushGate)
            {
                lock (operation.SharedState.Gate)
                {
                    FlushBatchCore(operation.SharedState);
                }
            }
        }

        private bool TryFlushBatch(DateTime deadline)
        {
            using AuditPathStateRegistry.Lease operation = Volatile.Read(ref _pathLease)?.Retain();
            if (operation == null)
            {
                return false;
            }

            bool enteredWriter = false;
            bool enteredPath = false;
            try
            {
#if UNITY_WEBGL
                enteredWriter = Monitor.TryEnter(_flushGate);
                enteredPath = enteredWriter && Monitor.TryEnter(operation.SharedState.Gate);
#else
                enteredWriter = Monitor.TryEnter(_flushGate, RemainingMilliseconds(deadline));
                enteredPath = enteredWriter && Monitor.TryEnter(operation.SharedState.Gate,
                    RemainingMilliseconds(deadline));
#endif
                return enteredPath && FlushBatchCore(operation.SharedState);
            }
            finally
            {
                if (enteredPath)
                {
                    Monitor.Exit(operation.SharedState.Gate);
                }

                if (enteredWriter)
                {
                    Monitor.Exit(_flushGate);
                }
            }
        }

        private bool FlushBatchCore(AuditPathStateRegistry.State state)
        {
            if (state.Quarantined)
            {
                EnqueueLog(LogType.Error, "[AuditLogWriter] " + GetQuarantineReason(state));
                return false;
            }

            ReportDroppedEntriesIfAny();
            if (_queue.IsEmpty)
            {
                return true;
            }

            List<AuditEntry> batch = new();
            while (_queue.TryDequeue(out AuditEntry entry))
            {
                Interlocked.Decrement(ref _queueCount);
                batch.Add(entry);
            }

            if (batch.Count == 0)
            {
                return true;
            }

            try
            {
                if (NeedsRotation(state))
                {
                    RotateNow(state);
                }
            }
            catch (Exception ex)
            {
                EnqueueLog(LogType.Warning,
                    $"[AuditLogWriter] Rotation failed, re-queuing {batch.Count} entries: {ex.Message}");
                RequeueFront(batch);
                return false;
            }

            long localSequence = state.Sequence;
            string localPreviousHash = state.PreviousHash;
            List<string> lines = new(batch.Count);
            foreach (AuditEntry entry in batch)
            {
                localSequence++;
                AuditEntry finalEntry = new(
                    localSequence, entry.Kind, entry.TraceId, entry.Actor, entry.Model, entry.PromptHash,
                    entry.ToolName, entry.Args, entry.PolicyDecision, entry.Result, entry.ResultDetail,
                    entry.DurationMs, entry.WorldDiff, entry.RollbackHandle, sourceTag: entry.SourceTag,
                    prevHash: localPreviousHash, hash: "", role: entry.Role);
                string preimage = JsonConvert.SerializeObject(finalEntry, _jsonSettings);
                string hash = AuditHash.Chain(localPreviousHash, preimage);
                localPreviousHash = hash;
                lines.Add(JsonConvert.SerializeObject(finalEntry.WithHash(hash), _jsonSettings));
            }

            try
            {
                AppendLinesToFile(_filePath, lines, state);
                state.Sequence = localSequence;
                state.PreviousHash = localPreviousHash;
                return true;
            }
            catch (Exception ex)
            {
                EnqueueLog(LogType.Warning,
                    $"[AuditLogWriter] Write failed, re-queuing {batch.Count} entries: {ex.Message}");
                RequeueFront(batch);
                return false;
            }
        }

        private void RequeueFront(List<AuditEntry> failedBatch)
        {
            List<AuditEntry> trailing = new();
            while (_queue.TryDequeue(out AuditEntry entry))
            {
                Interlocked.Decrement(ref _queueCount);
                trailing.Add(entry);
            }

            foreach (AuditEntry entry in failedBatch)
            {
                EnqueueRaw(entry);
            }

            foreach (AuditEntry entry in trailing)
            {
                EnqueueRaw(entry);
            }
        }

        private void ReportDroppedEntriesIfAny()
        {
            long dropped = Interlocked.Read(ref _droppedCount);
            if (dropped != _lastReportedDroppedCount)
            {
                _lastReportedDroppedCount = dropped;
                EnqueueRaw(AuditEntry.ForQueueDropped(0, "system", dropped));
            }
        }

        private void AppendLinesToFile(string path, List<string> lines, AuditPathStateRegistry.State state)
        {
            ThrowIfQuarantined(state);
            Func<bool> simulateFailure = SimulateWriteFailureForTesting;
            if (simulateFailure != null && simulateFailure())
            {
                throw new IOException("Simulated write failure (test hook).");
            }

            long originalLength = File.Exists(path) ? new FileInfo(path).Length : 0;
            bool appendWriteAttempted = false;
            try
            {
                using FileStream stream = new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                bool needsNewline = originalLength > 0 && !EndsWithNewline(stream);
                stream.Seek(0, SeekOrigin.End);
                using (StreamWriter writer = new(stream, new UTF8Encoding(false, true), 1024, true))
                {
                    if (needsNewline)
                    {
                        appendWriteAttempted = true;
                        writer.WriteLine();
                    }

                    for (int i = 0; i < lines.Count; i++)
                    {
                        appendWriteAttempted = true;
                        writer.WriteLine(lines[i]);
                        Action afterFirstLine = AfterFirstLineFlushedForTesting;
                        if (i == 0 && afterFirstLine != null)
                        {
                            writer.Flush();
                            stream.Flush();
                            afterFirstLine();
                        }
                    }
                }

                stream.Flush();
            }
            catch (Exception appendFailure)
            {
                if (!appendWriteAttempted)
                {
                    throw;
                }

                // WHY: Both using scopes have ended before rollback, including their failing disposals.
                try
                {
                    BeforeRollbackForTesting?.Invoke();
                    using FileStream rollback = new(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
                    rollback.SetLength(originalLength);
                    rollback.Flush();
                }
                catch (Exception rollbackFailure)
                {
                    state.RecoveryLength = originalLength;
                    state.RecoverySequence = state.Sequence;
                    state.RecoveryHash = state.PreviousHash;
                    Volatile.Write(ref state.Quarantined, true);
                    state.AppendFailure = appendFailure.ToString();
                    state.RollbackFailure = rollbackFailure.ToString();
                    throw new IOException(GetQuarantineReason(state),
                        new AggregateException(appendFailure, rollbackFailure));
                }

                throw;
            }

            ReportPersistence();
        }

        private static string GetQuarantineReason(AuditPathStateRegistry.State state)
        {
            return "Audit path is quarantined after append rollback failed. " +
                "Restore the saved pre-append bytes and request verified recovery. Append failure: " +
                state.AppendFailure + " Rollback failure: " + state.RollbackFailure;
        }

        private static void ThrowIfQuarantined(AuditPathStateRegistry.State state)
        {
            if (state.Quarantined)
            {
                throw new InvalidOperationException(GetQuarantineReason(state));
            }
        }

        private void ReportPersistence()
        {
            try
            {
                Func<bool> persistenceArmed = PersistenceArmedForTesting;
                if (!(persistenceArmed == null ? CoreAiWebGlPersistence.Sync() : persistenceArmed()))
                {
                    EnqueueLog(LogType.Error,
                        "[AuditLogWriter] Audit bytes were written, but automatic WebGL persistence is not armed.");
                }
            }
            catch (Exception ex)
            {
                EnqueueLog(LogType.Error,
                    "[AuditLogWriter] Audit bytes were written, but persistence arming could not be checked: " + ex.Message);
            }
        }

        private static bool EndsWithNewline(FileStream stream)
        {
            if (stream.Length == 0)
            {
                return true;
            }

            stream.Seek(-1, SeekOrigin.End);
            return stream.ReadByte() == '\n';
        }
    }
}
