using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using CoreAI.Audit;
using CoreAI.Infrastructure;
using Newtonsoft.Json;
using UnityEngine;

namespace CoreAI.Features.Audit
{
    public sealed class AuditLogWriter : IAuditLog, IDisposable
    {
        private readonly ConcurrentQueue<AuditEntry> _queue = new();
        private readonly string _folder;
        private readonly string _filePath;
        private readonly JsonSerializerSettings _jsonSettings;

        private string _prevHash = "";
        private long _seq;
        private CancellationTokenSource _cts;

        private const long MaxFileSize = 50 * 1024 * 1024;
        private const int FlushIntervalMs = 500;
        private const int MaxBatchSize = 10;

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
            FlushLoop(_cts.Token).Forget();
        }

        internal string FilePath => _filePath;

        public void Record(AuditEntry entry)
        {
            _queue.Enqueue(entry);
        }

        public void Dispose()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            FlushBatch();
        }

        /// <summary>Synchronously flushes the queue. Used by tests that need deterministic writes.</summary>
        internal void FlushForTesting()
        {
            FlushBatch();
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
                long fileSize = new FileInfo(_filePath).Length;
                if (fileSize > MaxFileSize)
                {
                    Rotate();
                    _seq = 0;
                    _prevHash = "";
                    return;
                }

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
                if (lastLine != null)
                {
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
                        Debug.LogError($"[AuditLogWriter] Audit log tail line is corrupt, chain cannot be resumed: {ex.Message}");
                        _prevHash = "";
                        AppendChainResetMarker($"corrupt tail line: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AuditLogWriter] Failed to resume audit chain, starting fresh: {ex.Message}");
                _seq = 0;
                _prevHash = "";
                AppendChainResetMarker($"resume failed: {ex.Message}");
            }
        }

        private void AppendChainResetMarker(string reason)
        {
            _queue.Enqueue(AuditEntry.ForChainReset(seq: 0, actor: "system", reason: reason));
            FlushBatch();
        }

        private void Rotate()
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
        }

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
            }
        }

        private void FlushBatch()
        {
            if (_queue.IsEmpty)
            {
                return;
            }

            List<string> lines = new(MaxBatchSize);
            while (!_queue.IsEmpty && lines.Count < MaxBatchSize)
            {
                if (_queue.TryDequeue(out AuditEntry entry))
                {
                    long seq = Interlocked.Increment(ref _seq);

                    // Built ONCE — a single Ts, the real chain-head prevHash, hash left blank.
                    // This is the canonical preimage: the stored line is this same entry with
                    // only the hash field filled in, so a verifier can reconstruct it exactly.
                    AuditEntry finalEntry = new(
                        seq: seq,
                        kind: entry.Kind,
                        traceId: entry.TraceId,
                        actor: entry.Actor,
                        model: entry.Model,
                        promptHash: entry.PromptHash,
                        toolName: entry.ToolName,
                        args: entry.Args,
                        policyDecision: entry.PolicyDecision,
                        result: entry.Result,
                        resultDetail: entry.ResultDetail,
                        durationMs: entry.DurationMs,
                        worldDiff: entry.WorldDiff,
                        rollbackHandle: entry.RollbackHandle,
                        sourceTag: entry.SourceTag,
                        prevHash: _prevHash,
                        hash: "");

                    string preimage = JsonConvert.SerializeObject(finalEntry, _jsonSettings);
                    string hash = AuditHash.Chain(_prevHash, preimage);
                    _prevHash = hash;

                    string line = JsonConvert.SerializeObject(finalEntry.WithHash(hash), _jsonSettings);
                    lines.Add(line);
                }
            }

            if (lines.Count == 0)
            {
                return;
            }

            try
            {
                bool exists = File.Exists(_filePath);
                long currentSize = exists ? new FileInfo(_filePath).Length : 0;
                if (currentSize > MaxFileSize)
                {
                    Rotate();
                    exists = File.Exists(_filePath);
                    currentSize = exists ? new FileInfo(_filePath).Length : 0;
                }

                // A crash or a corrupt-tail resume can leave the file's last line without a trailing
                // newline. Appending straight onto that (StreamWriter's append mode does not insert one)
                // would concatenate the new entry into the corrupt line, making it unparseable — exactly
                // when a ChainReset marker needs to be readable. Repair the missing newline first.
                if (exists && currentSize > 0 && !EndsWithNewline(_filePath))
                {
                    File.AppendAllText(_filePath, Environment.NewLine);
                }

                using StreamWriter writer = new(_filePath, append: true);
                foreach (string line in lines)
                {
                    writer.WriteLine(line);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AuditLogWriter] Write failed: {ex.Message}");
            }
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
