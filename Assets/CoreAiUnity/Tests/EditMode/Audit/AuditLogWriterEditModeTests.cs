using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CoreAI.Audit;
using CoreAI.Features.Audit;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode.Audit
{
    public sealed class AuditLogWriterEditModeTests
    {
        private string _testFolder;
        private AuditLogWriter _writer;

        [SetUp]
        public void SetUp()
        {
            _testFolder = Path.Combine(Application.temporaryCachePath, "AuditTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_testFolder);
        }

        [TearDown]
        public void TearDown()
        {
            _writer?.Dispose();
            if (Directory.Exists(_testFolder))
            {
                try { Directory.Delete(_testFolder, recursive: true); }
                catch { }
            }
        }

        [Test]
        public void Record_QueuesEntry()
        {
            using AuditLogWriter writer = new();
            Assert.DoesNotThrow(() =>
                writer.Record(AuditEntry.ForToolCall(
                    0, "t", "a", "m", "ph", "test", "{}", "allowed", "ok", "", 0)));
        }

        [Test]
        public void AuditContext_SetAndGetPromptHash()
        {
            AuditContext.SetPromptHash("trace-1", "hash-value");
            Assert.AreEqual("hash-value", AuditContext.GetPromptHash("trace-1"));
        }

        [Test]
        public void AuditContext_SetAndGetModel()
        {
            AuditContext.SetModel("trace-1", "gpt-4");
            Assert.AreEqual("gpt-4", AuditContext.GetModel("trace-1"));
        }

        [Test]
        public void AuditContext_Cleanup_RemovesEntries()
        {
            AuditContext.SetPromptHash("trace-1", "hash");
            AuditContext.SetModel("trace-1", "gpt-4");
            AuditContext.Cleanup("trace-1");
            Assert.AreEqual("", AuditContext.GetPromptHash("trace-1"));
            Assert.AreEqual("", AuditContext.GetModel("trace-1"));
        }

        [Test]
        public void NullAuditLog_DoesNotThrow()
        {
            var log = NullAuditLog.Instance;
            Assert.DoesNotThrow(() =>
                log.Record(AuditEntry.ForToolCall(0, "t", "a", "m", "ph", "test", "{}", "allowed", "ok", "", 0)));
        }

        private static void RecordN(AuditLogWriter writer, int count)
        {
            for (int i = 0; i < count; i++)
            {
                writer.Record(AuditEntry.ForToolCall(
                    seq: 0, traceId: $"trace-{i}", actor: "creator", model: "gpt-4", promptHash: "ph",
                    toolName: "test_tool", args: $"{{\"i\":{i}}}", policyDecision: "allowed",
                    result: "ok", resultDetail: "", durationMs: i));
            }
        }

        [Test]
        public void Burst_1000Entries_AllFlushedAndVerifyOk()
        {
            _writer = new AuditLogWriter(_testFolder);
            RecordN(_writer, 1000);
            _writer.FlushForTesting();

            List<AuditEntry> entries = AuditLogVerifier.ReadAll(_writer.FilePath);
            Assert.AreEqual(1000, entries.Count);

            AuditVerifyResult result = AuditLogVerifier.Verify(_writer.FilePath);
            Assert.IsTrue(result.Ok, result.Error);
            Assert.AreEqual(1000, result.LineCount);
        }

        [Test]
        public void Dispose_WithQueuedEntries_DrainsEntireQueueToFile()
        {
            _writer = new AuditLogWriter(_testFolder);
            RecordN(_writer, 500);
            string path = _writer.FilePath;

            // No FlushForTesting() — Dispose alone must join the background worker and have it
            // drain the whole backlog, not one batch (see AuditLogWriter.WorkerLoop).
            _writer.Dispose();
            _writer = null;

            List<AuditEntry> entries = AuditLogVerifier.ReadAll(path);
            Assert.AreEqual(500, entries.Count);

            AuditVerifyResult result = AuditLogVerifier.Verify(path);
            Assert.IsTrue(result.Ok, result.Error);
        }

        [Test]
        public void ConcurrentRecord_FromFourThreads_AllEntriesPresentAndChainVerifies()
        {
            _writer = new AuditLogWriter(_testFolder);
            const int perThread = 250;
            const int threadCount = 4;

            Task[] tasks = new Task[threadCount];
            for (int t = 0; t < threadCount; t++)
            {
                int threadIndex = t;
                tasks[t] = Task.Run(() =>
                {
                    for (int i = 0; i < perThread; i++)
                    {
                        _writer.Record(AuditEntry.ForToolCall(
                            seq: 0, traceId: $"trace-{threadIndex}-{i}", actor: "creator", model: "gpt-4",
                            promptHash: "ph", toolName: "test_tool", args: $"{{\"i\":{i}}}",
                            policyDecision: "allowed", result: "ok", resultDetail: "", durationMs: i));
                    }
                });
            }

            Task.WaitAll(tasks);

            _writer.FlushForTesting();

            List<AuditEntry> entries = AuditLogVerifier.ReadAll(_writer.FilePath);
            Assert.AreEqual(perThread * threadCount, entries.Count);

            AuditVerifyResult result = AuditLogVerifier.Verify(_writer.FilePath);
            Assert.IsTrue(result.Ok, result.Error);
        }

        [Test]
        public void WriteFailure_RequeuesBatch_ChainStillVerifiesAfterRecovery()
        {
            _writer = new AuditLogWriter(_testFolder);
            RecordN(_writer, 3);

            bool failNext = true;
            _writer.SimulateWriteFailureForTesting = () =>
            {
                if (!failNext)
                {
                    return false;
                }

                failNext = false;
                return true;
            };

            UnityEngine.TestTools.LogAssert.Expect(
                LogType.Warning,
                new System.Text.RegularExpressions.Regex(@"\[AuditLogWriter\] Write failed"));

            _writer.FlushForTesting(); // simulated I/O failure: must requeue, not lose or half-commit entries

            Assert.AreEqual(0, AuditLogVerifier.ReadAll(_writer.FilePath).Count, "Failed write must not reach disk.");

            _writer.SimulateWriteFailureForTesting = null;
            _writer.FlushForTesting(); // retry succeeds

            List<AuditEntry> entries = AuditLogVerifier.ReadAll(_writer.FilePath);
            Assert.AreEqual(3, entries.Count, "No entries should be lost or duplicated across the retry.");

            AuditVerifyResult result = AuditLogVerifier.Verify(_writer.FilePath);
            Assert.IsTrue(result.Ok, result.Error);
        }

        [Test]
        public void BoundedQueue_DropOldest_AuditsDroppedCountMarker()
        {
            _writer = new AuditLogWriter(_testFolder);

            // Enqueue well past the bounded-queue limit (10_000) without flushing, forcing drop-oldest.
            RecordN(_writer, 10_500);
            Assert.Greater(_writer.DroppedCount, 0, "Expected the bounded queue to drop the oldest entries under sustained backlog.");

            _writer.FlushForTesting();

            List<AuditEntry> entries = AuditLogVerifier.ReadAll(_writer.FilePath);
            Assert.IsTrue(entries.Any(e => e.Kind == AuditEntryKind.QueueDropped),
                "Expected a QueueDropped marker entry auditing the backpressure.");

            AuditVerifyResult result = AuditLogVerifier.Verify(_writer.FilePath);
            Assert.IsTrue(result.Ok, result.Error);
        }
    }
}
