using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using CoreAI.Audit;
using CoreAI.Infrastructure;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace CoreAI.Features.Audit
{
    public sealed class AuditLogWriter : IAuditLog, IDisposable
    {
        private readonly ConcurrentQueue<AuditEntry> _queue = new();
        private readonly ConcurrentQueue<(LogType Level, string Message)> _pendingLogs = new();
        private readonly string _folder;
        private readonly string _filePath;
        private readonly JsonSerializerSettings _jsonSettings;
        private readonly object _flushGate = new();

        private string _prevHash = "";
        private long _seq;
        private long _queueCount;
        private long _droppedCount;
        private long _lastReportedDroppedCount;
        private CancellationTokenSource _cts;
#if !UNITY_WEBGL
        private Thread _worker;
#endif

        private const long MaxFileSize = 50 * 1024 * 1024;
        private const int FlushIntervalMs = 500;
        private const int MaxQueueSize = 10_000;
        private static readonly TimeSpan DisposeDrainDeadline = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan WorkerJoinTimeout = DisposeDrainDeadline + TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// Test-only hook: when set and returns true, the next file append throws instead of touching
        /// disk, so tests can exercise the requeue-on-failure path without needing a real I/O fault.
        /// </summary>
        internal Func<bool> SimulateWriteFailureForTesting;

        public AuditLogWriter()
            : this(Path.Combine(Application.persistentDataPath, CoreAiPersistentPaths.RootFolderName, "Audit"))
        {
        }

        internal AuditLogWriter(string folder)
        {
            _folder = folder;
            Directory.CreateDirectory(_folder);
            _filePath = Path.Combine(_folder, "audit.jsonl");

            _jsonSettings = new JsonSerializerSettings
            {
                Formatting = Formatting.None,
                NullValueHandling = NullValueHandling.Ignore
            };

            ResumeChain();
            _cts = new CancellationTokenSource();

            // Serialization + SHA-256 + file I/O run off the main thread wherever real threads
            // exist. WebGL has no threads, so it keeps the original main-thread, frame-budgeted
            // UniTask.Delay loop instead.
#if UNITY_WEBGL
            FlushLoop(_cts.Token).Forget();
#else
            _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "CoreAI-AuditLogWriter" };
            _worker.Start();
#endif
        }

        internal string FilePath => _filePath;

        /// <summary>Cumulative count of entries dropped by the bounded queue (test/diagnostics use).</summary>
        internal long DroppedCount => Interlocked.Read(ref _droppedCount);

        public void Record(AuditEntry entry)
        {
            EnqueueRaw(entry);

            // Bounded queue: drop the oldest entries once the backlog exceeds MaxQueueSize so a burst
            // that outruns the flush loop cannot grow the queue without limit and OOM the process.
            while (Interlocked.Read(ref _queueCount) > MaxQueueSize)
            {
                if (_queue.TryDequeue(out _))
                {
                    Interlocked.Decrement(ref _queueCount);
                    Interlocked.Increment(ref _droppedCount);
                }
                else
                {
                    break;
                }
            }
        }

        private void EnqueueRaw(AuditEntry entry)
        {
            _queue.Enqueue(entry);
            Interlocked.Increment(ref _queueCount);
        }

        public void Dispose()
        {
            // Cancelling wakes the worker's WaitHandle immediately (no need to wait for the next
            // 500ms tick); the worker then drains the queue itself, bounded by DisposeDrainDeadline.
            _cts?.Cancel();

#if !UNITY_WEBGL
            bool joined = _worker == null || _worker.Join(WorkerJoinTimeout);
            if (!joined || !_queue.IsEmpty)
            {
                // Worker didn't finish in time (or never started) — fall back to draining on the
                // calling thread so entries are not silently lost.
                DrainOnDispose();
            }
#else
            DrainOnDispose();
#endif

            DrainPendingLogs();
            _cts?.Dispose();
            _cts = null;
        }

        /// <summary>Synchronously flushes the queue. Used by tests that need deterministic writes.</summary>
        internal void FlushForTesting()
        {
            FlushBatch();
            DrainPendingLogs();
        }

        /// <summary>
        /// Drains the entire queue on shutdown, bounded by <see cref="DisposeDrainDeadline"/> so a
        /// stuck disk (or a producer that never stops enqueuing) cannot hang application exit forever.
        /// </summary>
        private void DrainOnDispose()
        {
            DateTime start = DateTime.UtcNow;
            while (!_queue.IsEmpty)
            {
                FlushBatch();
                if (DateTime.UtcNow - start >= DisposeDrainDeadline)
                {
                    Debug.LogWarning("[AuditLogWriter] Dispose drain deadline (2s) reached with entries still queued.");
                    break;
                }
            }
        }

        private void ResumeChain()
        {
            if (!File.Exists(_filePath))
            {
                _seq = 0;
                _prevHash = "";
                return;
            }

            try
            {
                ReadTailState();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AuditLogWriter] Failed to resume audit chain, starting fresh: {ex.Message}");
                _seq = 0;
                _prevHash = "";
                AppendChainResetMarker($"resume failed: {ex.Message}");
                return;
            }

            if (NeedsRotation())
            {
                try
                {
                    RotateNow();
                }
                catch (Exception ex)
                {
                    // The chain itself was read fine — only rotation failed (e.g. disk full/locked).
                    // Leave _seq/_prevHash as read; the oversized file will be retried on the next
                    // flush tick instead of discarding an otherwise-valid chain.
                    Debug.LogWarning(
                        $"[AuditLogWriter] Startup rotation failed, will retry on next flush: {ex.Message}");
                }
            }
        }

        /// <summary>Reads the current file's line count and last line's hash into _seq/_prevHash.</summary>
        private void ReadTailState()
        {
            string lastLine = null;
            long count = 0;
            using (StreamReader reader = new(_filePath))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    lastLine = line;
                    count++;
                }
            }

            _seq = count;
            if (lastLine == null)
            {
                _prevHash = "";
                return;
            }

            try
            {
                var last = JsonConvert.DeserializeAnonymousType(lastLine, new { hash = "" });
                if (last == null)
                {
                    throw new InvalidOperationException("tail line deserialized to null");
                }

                _prevHash = last.hash ?? "";
            }
            catch (Exception ex)
            {
                Debug.LogError(
                    $"[AuditLogWriter] Audit log tail line is corrupt, chain cannot be resumed: {ex.Message}");
                _prevHash = "";
                AppendChainResetMarker($"corrupt tail line: {ex.Message}");
            }
        }

        private void AppendChainResetMarker(string reason)
        {
            EnqueueRaw(AuditEntry.ForChainReset(0, "system", reason));
            FlushBatch();
        }

        private bool NeedsRotation()
        {
            return File.Exists(_filePath) && new FileInfo(_filePath).Length > MaxFileSize;
        }

        /// <summary>
        /// Renames the current file aside and starts a fresh one, linking the two via a
        /// <see cref="AuditEntryKind.RotationMarker"/> (last line of the old file) and a
        /// <see cref="AuditEntryKind.RotationAnchor"/> (first line of the new file, whose own
        /// <c>prevHash</c> is the marker's hash). Throws without mutating <see cref="_seq"/>/
        /// <see cref="_prevHash"/> if any step fails, so the caller can requeue and retry.
        /// </summary>
        private void RotateNow()
        {
            string markerHash;
            JObject lastObj = PeekLastLine(_filePath);
            bool alreadyMarked = lastObj != null && (int?)lastObj["Kind"] == (int)AuditEntryKind.RotationMarker;

            if (alreadyMarked)
            {
                // Recovery from a prior partial rotation: the marker was written but the rename
                // that should have followed did not complete. Reuse it instead of appending a
                // second marker whose prevHash would no longer match the file's real chain head.
                markerHash = (string)lastObj["hash"] ?? _prevHash;
            }
            else
            {
                long markerSeq = _seq + 1;
                AuditEntry marker = AuditEntry.ForRotationMarker(markerSeq, "system", _prevHash);
                string markerPreimage = JsonConvert.SerializeObject(marker, _jsonSettings);
                markerHash = AuditHash.Chain(_prevHash, markerPreimage);
                string markerLine = JsonConvert.SerializeObject(marker.WithHash(markerHash), _jsonSettings);
                AppendLinesToFile(_filePath, new List<string> { markerLine });
            }

            string rotatedName = Rotate();

            const long anchorSeq = 0;
            AuditEntry anchor = AuditEntry.ForRotationAnchor(anchorSeq, "system", rotatedName, markerHash);
            string anchorPreimage = JsonConvert.SerializeObject(anchor, _jsonSettings);
            string anchorHash = AuditHash.Chain(markerHash, anchorPreimage);
            string anchorLine = JsonConvert.SerializeObject(anchor.WithHash(anchorHash), _jsonSettings);
            AppendLinesToFile(_filePath, new List<string> { anchorLine });

            _seq = anchorSeq;
            _prevHash = anchorHash;
        }

        /// <summary>Reads only the last line of a file (or null if it doesn't exist/is empty), parsed as JSON.</summary>
        private static JObject PeekLastLine(string path)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            string lastLine = null;
            using (StreamReader reader = new(path))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    lastLine = line;
                }
            }

            if (string.IsNullOrWhiteSpace(lastLine))
            {
                return null;
            }

            try
            {
                using StringReader stringReader = new(lastLine);
                using JsonTextReader jsonReader = new(stringReader) { DateParseHandling = DateParseHandling.None };
                return JObject.Load(jsonReader);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Renames the active file aside and returns the new file's name (not full path).</summary>
        private string Rotate()
        {
            string dir = Path.GetDirectoryName(_filePath);
            string name = Path.GetFileNameWithoutExtension(_filePath);
            string ext = Path.GetExtension(_filePath);
            int idx = 1;
            string rotated;
            do
            {
                rotated = Path.Combine(dir, $"{name}_{idx:D4}{ext}");
                idx++;
            } while (File.Exists(rotated));

            File.Move(_filePath, rotated);
            return Path.GetFileName(rotated);
        }

#if UNITY_WEBGL
        /// <summary>WebGL has no threads, so the flush tick stays on the main thread (player loop).</summary>
        private async UniTaskVoid FlushLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await UniTask.Delay(FlushIntervalMs, cancellationToken: ct);
                    FlushBatch();
                    DrainPendingLogs();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
#else
        /// <summary>
        /// Background flush loop: wakes every <see cref="FlushIntervalMs"/> or immediately when
        /// <see cref="_cts"/> is cancelled (Dispose). Owns <see cref="_seq"/>/<see cref="_prevHash"/>
        /// exclusively via <see cref="_flushGate"/> — Record() only ever enqueues.
        /// Must not call any UnityEngine API directly; log messages are deferred via
        /// <see cref="EnqueueLog"/> and emitted on the main thread instead.
        /// </summary>
        private void WorkerLoop()
        {
            CancellationToken ct = _cts.Token;
            while (!ct.WaitHandle.WaitOne(FlushIntervalMs))
            {
                FlushBatch();
            }

            // Stop requested: drain whatever remains, bounded by DisposeDrainDeadline so a stuck
            // disk (or a producer that never stops enqueuing) cannot hang application exit forever.
            DateTime start = DateTime.UtcNow;
            while (!_queue.IsEmpty)
            {
                FlushBatch();
                if (DateTime.UtcNow - start >= DisposeDrainDeadline)
                {
                    EnqueueLog(LogType.Warning,
                        "[AuditLogWriter] Dispose drain deadline (2s) reached with entries still queued.");
                    break;
                }
            }
        }
#endif

        /// <summary>Queues a log message for emission on the main thread (see <see cref="DrainPendingLogs"/>).</summary>
        private void EnqueueLog(LogType level, string message)
        {
            _pendingLogs.Enqueue((level, message));
        }

        /// <summary>
        /// Emits log messages queued by the background worker. Unity's Debug.Log family must run on
        /// the main thread, so callers (log pump, FlushForTesting, Dispose) must all be main-thread.
        /// </summary>
        private void DrainPendingLogs()
        {
            while (_pendingLogs.TryDequeue(out (LogType Level, string Message) log))
            {
                switch (log.Level)
                {
                    case LogType.Warning:
                        Debug.LogWarning(log.Message);
                        break;
                    case LogType.Error:
                        Debug.LogError(log.Message);
                        break;
                    default:
                        Debug.Log(log.Message);
                        break;
                }
            }
        }

        /// <summary>Serializes all flush entry points (timer loop, Dispose, FlushForTesting) so they cannot interleave.</summary>
        private void FlushBatch()
        {
            lock (_flushGate)
            {
                FlushBatchCore();
            }
        }

        private void FlushBatchCore()
        {
            ReportDroppedEntriesIfAny();

            List<AuditEntry> batch = new();
            while (_queue.TryDequeue(out AuditEntry entry))
            {
                Interlocked.Decrement(ref _queueCount);
                batch.Add(entry);
            }

            if (batch.Count == 0)
            {
                return;
            }

            if (NeedsRotation())
            {
                try
                {
                    RotateNow();
                }
                catch (Exception ex)
                {
                    EnqueueLog(LogType.Warning,
                        $"[AuditLogWriter] Rotation failed, re-queuing {batch.Count} pending entr{(batch.Count == 1 ? "y" : "ies")}: {ex.Message}");
                    RequeueFront(batch);
                    return;
                }
            }

            long localSeq = _seq;
            string localPrevHash = _prevHash;
            List<string> lines = new(batch.Count);

            foreach (AuditEntry entry in batch)
            {
                localSeq++;

                // Built ONCE — a single Ts, the real chain-head prevHash, hash left blank. This is
                // the canonical preimage: the stored line is this same entry with only the hash
                // field filled in, so a verifier can reconstruct it exactly.
                AuditEntry finalEntry = new(
                    localSeq,
                    entry.Kind,
                    entry.TraceId,
                    entry.Actor,
                    entry.Model,
                    entry.PromptHash,
                    entry.ToolName,
                    entry.Args,
                    entry.PolicyDecision,
                    entry.Result,
                    entry.ResultDetail,
                    entry.DurationMs,
                    entry.WorldDiff,
                    entry.RollbackHandle,
                    sourceTag: entry.SourceTag,
                    prevHash: localPrevHash,
                    hash: "");

                string preimage = JsonConvert.SerializeObject(finalEntry, _jsonSettings);
                string hash = AuditHash.Chain(localPrevHash, preimage);
                localPrevHash = hash;

                lines.Add(JsonConvert.SerializeObject(finalEntry.WithHash(hash), _jsonSettings));
            }

            try
            {
                AppendLinesToFile(_filePath, lines);

                // Commit the advanced chain head only after the append succeeded, so a failed write
                // can never leave _prevHash pointing at a record that isn't actually on disk.
                _seq = localSeq;
                _prevHash = localPrevHash;
            }
            catch (Exception ex)
            {
                EnqueueLog(LogType.Warning,
                    $"[AuditLogWriter] Write failed, re-queuing {batch.Count} entr{(batch.Count == 1 ? "y" : "ies")}: {ex.Message}");
                RequeueFront(batch);
            }
        }

        /// <summary>
        /// Puts a batch back at the front of the queue after a failed write, ahead of anything
        /// enqueued while the batch was being processed, preserving overall FIFO order.
        /// </summary>
        private void RequeueFront(List<AuditEntry> failedBatch)
        {
            List<AuditEntry> trailing = new();
            while (_queue.TryDequeue(out AuditEntry e))
            {
                Interlocked.Decrement(ref _queueCount);
                trailing.Add(e);
            }

            foreach (AuditEntry e in failedBatch)
            {
                EnqueueRaw(e);
            }

            foreach (AuditEntry e in trailing)
            {
                EnqueueRaw(e);
            }
        }

        /// <summary>
        /// If the bounded queue has dropped entries since the last report, enqueues a
        /// <see cref="AuditEntryKind.QueueDropped"/> marker so the backpressure itself is audited
        /// rather than silently discarded.
        /// </summary>
        private void ReportDroppedEntriesIfAny()
        {
            long dropped = Interlocked.Read(ref _droppedCount);
            if (dropped == _lastReportedDroppedCount)
            {
                return;
            }

            _lastReportedDroppedCount = dropped;
            EnqueueRaw(AuditEntry.ForQueueDropped(0, "system", dropped));
        }

        /// <summary>
        /// Appends <paramref name="lines"/> to <paramref name="path"/>, repairing a missing trailing
        /// newline first. Throws on failure instead of swallowing it, so callers can requeue rather
        /// than silently advancing the chain past a record that never reached disk.
        /// </summary>
        private void AppendLinesToFile(string path, List<string> lines)
        {
            if (SimulateWriteFailureForTesting != null && SimulateWriteFailureForTesting())
            {
                throw new IOException("Simulated write failure (test hook).");
            }

            bool exists = File.Exists(path);
            long currentSize = exists ? new FileInfo(path).Length : 0;

            // A crash or a corrupt-tail resume can leave the file's last line without a trailing
            // newline. Appending straight onto that (StreamWriter's append mode does not insert one)
            // would concatenate the new entry into the corrupt line, making it unparseable.
            if (exists && currentSize > 0 && !EndsWithNewline(path))
            {
                File.AppendAllText(path, Environment.NewLine);
            }

            using (StreamWriter writer = new(path, true))
            {
                foreach (string line in lines)
                {
                    writer.WriteLine(line);
                }
            }

            CoreAiWebGlPersistence.Sync();
        }

        /// <summary>Reads just the last byte to check whether the file ends with a line terminator.</summary>
        private static bool EndsWithNewline(string path)
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length == 0)
            {
                return true;
            }

            stream.Seek(-1, SeekOrigin.End);
            int last = stream.ReadByte();
            return last == '\n';
        }
    }
}