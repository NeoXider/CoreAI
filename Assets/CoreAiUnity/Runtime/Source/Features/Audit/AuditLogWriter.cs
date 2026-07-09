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
        {
            _folder = Path.Combine(
                Application.persistentDataPath,
                CoreAiPersistentPaths.RootFolderName,
                "Audit");
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

                using StreamReader reader = new(_filePath);
                string lastLine = null;
                string line;
                long count = 0;
                while ((line = reader.ReadLine()) != null)
                {
                    lastLine = line;
                    count++;
                }

                _seq = count;
                if (lastLine != null)
                {
                    try
                    {
                        var last = JsonConvert.DeserializeAnonymousType(lastLine, new { hash = "" });
                        _prevHash = last?.hash ?? "";
                    }
                    catch
                    {
                        _prevHash = "";
                    }
                }
            }
            catch
            {
                _seq = 0;
                _prevHash = "";
            }
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
                    string json = JsonConvert.SerializeObject(
                        new AuditEntry(
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
                            prevHash: _prevHash,
                            hash: ""),
                        _jsonSettings);

                    string hash = AuditHash.Chain(_prevHash, json);
                    _prevHash = hash;

                    string line = JsonConvert.SerializeObject(
                        new AuditEntry(
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
                            prevHash: entry.PrevHash,
                            hash: hash),
                        _jsonSettings);

                    lines.Add(line);
                }
            }

            if (lines.Count == 0)
            {
                return;
            }

            try
            {
                long currentSize = File.Exists(_filePath) ? new FileInfo(_filePath).Length : 0;
                if (currentSize > MaxFileSize)
                {
                    Rotate();
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
    }
}
