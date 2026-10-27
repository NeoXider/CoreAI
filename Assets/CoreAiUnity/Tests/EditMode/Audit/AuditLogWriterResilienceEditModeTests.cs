using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
#if !UNITY_WEBGL
using System.Threading.Tasks;
#endif
using CoreAI.Audit;
using CoreAI.Features.Audit;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.EditMode.Audit
{
    [Category("Audit")]
#if !UNITY_5_3_OR_NEWER
    // WHY: Portable NUnit supports this attribute; Unity's stripped NUnit runs EditMode tests sequentially.
    [NonParallelizable]
#endif
    public sealed class AuditLogWriterResilienceEditModeTests
    {
        private string _folder;
        private readonly List<AuditLogWriter> _writers = new();
        private readonly List<(string Folder, byte[] Bytes, bool Existed)> _recoveryBackups = new();

        [SetUp]
        public void SetUp()
        {
            _folder = Path.Combine(Application.temporaryCachePath,
                "AuditResilience_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (AuditLogWriter writer in _writers)
            {
                writer.SimulateWriteFailureForTesting = null;
                writer.AfterFirstLineFlushedForTesting = null;
                writer.BeforeRollbackForTesting = null;
                writer.PersistenceArmedForTesting = null;
                writer.Dispose();
            }

            RestoreRememberedQuarantines();
            _writers.Clear();
            _recoveryBackups.Clear();
            if (Directory.Exists(_folder))
            {
                Directory.Delete(_folder, true);
            }
        }

        [Test]
        public void LockedFileBeforeAppend_RequeuesWithoutQuarantine_ThenRetriesAfterUnlock()
        {
            AuditLogWriter writer = CreateWriter(QuietOptions());
            writer.Record(Entry("seed"));
            writer.FlushForTesting();
            byte[] original = File.ReadAllBytes(writer.FilePath);
            int rollbackAttempts = 0;
            writer.BeforeRollbackForTesting = () => Interlocked.Increment(ref rollbackAttempts);
            using (FileStream locked = new(writer.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                writer.Record(Entry("first"));
                writer.Record(Entry("second"));
                writer.FlushForTesting(false);
                Assert.Greater(writer.PendingDiagnosticCount, 0,
                    "The locked append must exercise the real pre-write failure path.");
                CollectionAssert.AreEqual(original, File.ReadAllBytes(writer.FilePath));
                using AuditPathStateRegistry.Lease operation = AuditPathStateRegistry.TryAcquireExisting(writer.FilePath);
                lock (operation.SharedState.Gate)
                {
                    Assert.IsFalse(operation.SharedState.Quarantined);
                }

                Assert.AreEqual(0, rollbackAttempts, "An append that never attempted a write needs no rollback.");
            }

            writer.BeforeRollbackForTesting = null;
            writer.FlushForTesting();
            List<AuditEntry> entries = AuditLogVerifier.ReadAll(writer.FilePath);
            CollectionAssert.AreEqual(new[] { "seed", "first", "second" },
                entries.Select(entry => entry.TraceId).ToArray());
            CollectionAssert.AreEqual(new long[] { 1, 2, 3 }, entries.Select(entry => entry.Seq).ToArray());
            AssertVerified(writer.FilePath);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PartialAppend_RollsBackExactBytesIncludingMissingNewline_ThenRetriesOnce(bool removeNewline)
        {
            AuditLogWriter writer = CreateWriter(QuietOptions());
            writer.Record(Entry("seed"));
            writer.FlushForTesting();
            byte[] original = File.ReadAllBytes(writer.FilePath);
            if (removeNewline)
            {
                int length = original.Length;
                while (length > 0 && (original[length - 1] == '\r' || original[length - 1] == '\n'))
                {
                    length--;
                }

                Array.Resize(ref original, length);
                File.WriteAllBytes(writer.FilePath, original);
            }

            long persistedLength = 0;
            writer.AfterFirstLineFlushedForTesting = () =>
            {
                Interlocked.Exchange(ref persistedLength, new FileInfo(writer.FilePath).Length);
                throw new IOException("Partial append fault.");
            };
            writer.Record(Entry("first"));
            writer.Record(Entry("second"));
            writer.FlushForTesting(false);
            using (AuditPathStateRegistry.Lease operation = AuditPathStateRegistry.TryAcquireExisting(writer.FilePath))
            {
                lock (operation.SharedState.Gate)
                {
                    Assert.Greater(Interlocked.Read(ref persistedLength), original.Length,
                        "The fault must occur after an actual persisted prefix, not before the write.");
                    CollectionAssert.AreEqual(original, File.ReadAllBytes(writer.FilePath));
                    Assert.IsFalse(operation.SharedState.Quarantined);
                    writer.AfterFirstLineFlushedForTesting = null;
                }
            }

            writer.FlushForTesting();
            CollectionAssert.AreEqual(new[] { "seed", "first", "second" },
                AuditLogVerifier.ReadAll(writer.FilePath).Select(entry => entry.TraceId).ToArray());
            AssertVerified(writer.FilePath);
            writer.Dispose();
            AuditLogWriter resumed = CreateWriter(QuietOptions());
            resumed.Record(Entry("reopened"));
            resumed.FlushForTesting();
            CollectionAssert.AreEqual(new long[] { 1, 2, 3, 4 },
                AuditLogVerifier.ReadAll(resumed.FilePath).Select(entry => entry.Seq).ToArray());
            AssertVerified(resumed.FilePath);
        }

        [Test]
        public void PartialRotationMarkerAppend_RollsBackBeforeSharedWriterRetries()
        {
            AuditLogWriter writer = CreateWriter(QuietOptions());
            AuditLogWriter peer = CreateWriter(QuietOptions());
            writer.Record(Entry("seed"));
            writer.FlushForTesting();
            byte[] original = File.ReadAllBytes(writer.FilePath);
            writer.AfterFirstLineFlushedForTesting = () => throw new IOException("Partial marker fault.");
            Assert.Catch(() => writer.RotateForTesting());
            CollectionAssert.AreEqual(original, File.ReadAllBytes(writer.FilePath));
            writer.AfterFirstLineFlushedForTesting = null;
            peer.RotateForTesting();
            peer.Record(Entry("after-rotation"));
            peer.FlushForTesting();
            string rotated = Path.Combine(_folder, "audit_0001.jsonl");
            Assert.AreEqual(1, AuditLogVerifier.ReadAll(rotated)
                .Count(entry => entry.Kind == AuditEntryKind.RotationMarker));
            AuditVerifyResult result = AuditLogVerifier.VerifyChainedSet(new[] { rotated, peer.FilePath });
            Assert.IsTrue(result.Ok, result.Error);
        }

        [Test]
        public void RollbackFailure_QuarantinesPeersAndNewWriters_UntilExactVerifiedRepair()
        {
            int baseline = AuditPathStateRegistry.Count;
            AuditLogWriter writer = CreateWriter(QuietOptions());
            AuditLogWriter peer = CreateWriter(QuietOptions());
            writer.Record(Entry("seed"));
            writer.FlushForTesting();
            byte[] original = RememberOriginalBytes(writer);
            int appendFaults = 0;
            writer.AfterFirstLineFlushedForTesting = () =>
            {
                Interlocked.Increment(ref appendFaults);
                throw new IOException("Partial append fault.");
            };
            writer.BeforeRollbackForTesting = () => throw new IOException("Rollback fault.");
            writer.Record(Entry("first"));
            writer.Record(Entry("second"));
            writer.FlushForTesting(false);
            byte[] partial = File.ReadAllBytes(writer.FilePath);
            Assert.Greater(partial.Length, original.Length);
            peer.Record(Entry("peer"));
            peer.FlushForTesting(false);
            writer.FlushForTesting(false);
            Assert.Catch<InvalidOperationException>(() => peer.RotateForTesting());
            Assert.Catch<InvalidOperationException>(() => new AuditLogWriter(_folder, QuietOptions()));
            CollectionAssert.AreEqual(partial, File.ReadAllBytes(writer.FilePath));
            Assert.AreEqual(1, appendFaults, "Quarantine must prevent further append attempts.");
            Assert.AreEqual(baseline + 1, AuditPathStateRegistry.Count);

#if !UNITY_WEBGL
            bool recoveredOnWorker = false;
            string workerError = "";
            Assert.IsTrue(Task.Run(() => recoveredOnWorker = AuditLogWriter.TryRecoverQuarantinedPath(_folder, out workerError))
                .Wait(TimeSpan.FromSeconds(5)));
            Assert.IsFalse(recoveredOnWorker);
            StringAssert.Contains("main thread", workerError);
            CollectionAssert.AreEqual(partial, File.ReadAllBytes(writer.FilePath));
#endif

            Assert.IsFalse(AuditLogWriter.TryRecoverQuarantinedPath(_folder, out string lengthError));
            StringAssert.Contains("Partial append fault.", lengthError);
            StringAssert.Contains("Rollback fault.", lengthError);
            CollectionAssert.AreEqual(partial, File.ReadAllBytes(writer.FilePath));

            byte[] corrupt = (byte[])original.Clone();
            int seedOffset = Encoding.UTF8.GetString(corrupt).IndexOf("seed", StringComparison.Ordinal);
            Assert.GreaterOrEqual(seedOffset, 0);
            corrupt[seedOffset] = (byte)'f';
            File.WriteAllBytes(writer.FilePath, corrupt);
            Assert.IsFalse(AuditLogWriter.TryRecoverQuarantinedPath(_folder, out string corruptError));
            StringAssert.Contains("verification failed", corruptError);
            CollectionAssert.AreEqual(corrupt, File.ReadAllBytes(writer.FilePath));

            byte[] alternate = AlternateValidSeed(original);
            Assert.AreEqual(original.Length, alternate.Length);
            File.WriteAllBytes(writer.FilePath, alternate);
            AssertVerified(writer.FilePath);
            Assert.IsFalse(AuditLogWriter.TryRecoverQuarantinedPath(_folder, out string headError));
            StringAssert.Contains("saved pre-append sequence and hash", headError);
            CollectionAssert.AreEqual(alternate, File.ReadAllBytes(writer.FilePath));

            File.WriteAllBytes(writer.FilePath, original);
            writer.AfterFirstLineFlushedForTesting = null;
            writer.BeforeRollbackForTesting = null;
            using (AuditPathStateRegistry.Lease operation = AuditPathStateRegistry.TryAcquireExisting(writer.FilePath))
            {
                lock (operation.SharedState.Gate)
                {
                    Assert.IsTrue(AuditLogWriter.TryRecoverQuarantinedPath(_folder, out string error), error);
                    CollectionAssert.AreEqual(original, File.ReadAllBytes(writer.FilePath));
                }
            }
            writer.FlushForTesting();
            peer.FlushForTesting();
            AuditLogWriter replacement = CreateWriter(QuietOptions());
            replacement.Record(Entry("replacement"));
            replacement.FlushForTesting();
            List<AuditEntry> entries = AuditLogVerifier.ReadAll(writer.FilePath);
            CollectionAssert.AreEquivalent(new[] { "seed", "first", "second", "peer", "replacement" },
                entries.Select(entry => entry.TraceId).ToArray());
            Assert.Less(entries.FindIndex(entry => entry.TraceId == "first"),
                entries.FindIndex(entry => entry.TraceId == "second"));
            CollectionAssert.AreEqual(new long[] { 1, 2, 3, 4, 5 }, entries.Select(entry => entry.Seq).ToArray());
            AssertVerified(writer.FilePath);
        }

        [Test]
        public void QuarantineAfterLastOwner_IsRetainedWithinValidatedProcessCapacity()
        {
            int baseline = AuditPathStateRegistry.Count;
            int previousCapacity = AuditLogWriter.SharedPathCapacity;
            AuditLogWriter.SharedPathCapacity = baseline + 2;
            try
            {
                for (int i = 0; i < 2; i++)
                {
                    string folder = Path.Combine(_folder, "quarantine-" + i);
                    AuditLogWriter writer = new(folder, QuietOptions());
                    _writers.Add(writer);
                    RememberOriginalBytes(writer);
                    writer.AfterFirstLineFlushedForTesting = () => throw new IOException("Partial append fault.");
                    writer.BeforeRollbackForTesting = () => throw new IOException("Rollback fault.");
                    writer.Record(Entry("quarantined"));
                    writer.FlushForTesting(false);
                    writer.Dispose();
                    Assert.Catch<InvalidOperationException>(() => new AuditLogWriter(folder, QuietOptions()));
                }

                Assert.AreEqual(baseline + 2, AuditPathStateRegistry.Count);
                Assert.Throws<ArgumentOutOfRangeException>(() => AuditLogWriter.SharedPathCapacity = 0);
                Assert.Throws<InvalidOperationException>(() => AuditLogWriter.SharedPathCapacity = baseline + 1);
                string overflowFolder = Path.Combine(_folder, "over-capacity");
                Assert.Throws<InvalidOperationException>(() => new AuditLogWriter(overflowFolder, QuietOptions()));
                Assert.IsFalse(Directory.Exists(overflowFolder));
                Assert.AreEqual(baseline + 2, AuditPathStateRegistry.Count);
                RestoreRememberedQuarantines();
                Assert.AreEqual(baseline, AuditPathStateRegistry.Count);
            }
            finally
            {
                foreach (AuditLogWriter writer in _writers)
                {
                    writer.Dispose();
                }

                RestoreRememberedQuarantines();
                AuditLogWriter.SharedPathCapacity = previousCapacity;
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PersistenceArmingRefusalAfterCommit_DoesNotReplayAppendOrRotation(bool throwOnCheck)
        {
            AuditLogWriter writer = CreateWriter(QuietOptions());
            writer.PersistenceArmedForTesting = () =>
            {
                if (throwOnCheck)
                {
                    throw new IOException("Persistence check fault.");
                }

                return false;
            };
            writer.Record(Entry("first"));
            writer.Record(Entry("second"));
            writer.FlushForTesting(false);
            writer.FlushForTesting(false);
            writer.RotateForTesting();
            writer.Record(Entry("third"));
            writer.FlushForTesting(false);
            writer.FlushForTesting(false);
            Assert.Greater(writer.PendingDiagnosticCount, 0);
            string rotated = Path.Combine(_folder, "audit_0001.jsonl");
            List<AuditEntry> entries = AuditLogVerifier.ReadAll(rotated);
            entries.AddRange(AuditLogVerifier.ReadAll(writer.FilePath));
            CollectionAssert.AreEqual(new[] { "first", "second", "third" },
                entries.Where(entry => entry.Kind == AuditEntryKind.ToolCall)
                    .Select(entry => entry.TraceId).ToArray());
            AuditVerifyResult result = AuditLogVerifier.VerifyChainedSet(new[] { rotated, writer.FilePath });
            Assert.IsTrue(result.Ok, result.Error);
            writer.Dispose();
            AuditLogWriter resumed = CreateWriter(QuietOptions());
            resumed.Record(Entry("fourth"));
            resumed.FlushForTesting();
            CollectionAssert.AreEqual(new long[] { 0, 1, 2 },
                AuditLogVerifier.ReadAll(resumed.FilePath).Select(entry => entry.Seq).ToArray());
            AssertVerified(resumed.FilePath);
        }

        [TestCase("DiagnosticQueueCapacity")]
        [TestCase("MaxDiagnosticsPerPump")]
        [TestCase("MaxFileSizeBytes")]
        [TestCase("MaxRetainedRotatedFiles")]
        public void NonpositiveOptions_RejectBeforeRetainingPath(string property)
        {
            int baseline = AuditPathStateRegistry.Count;
            AuditLogWriterOptions options = QuietOptions();
            switch (property)
            {
                case "DiagnosticQueueCapacity": options.DiagnosticQueueCapacity = 0; break;
                case "MaxDiagnosticsPerPump": options.MaxDiagnosticsPerPump = 0; break;
                case "MaxFileSizeBytes": options.MaxFileSizeBytes = 0; break;
                case "MaxRetainedRotatedFiles": options.MaxRetainedRotatedFiles = 0; break;
            }

            Assert.Throws<ArgumentOutOfRangeException>(() => new AuditLogWriter(_folder, options));
            Assert.AreEqual(baseline, AuditPathStateRegistry.Count);
        }

        [Test]
        public void RepeatedAppendFaults_BoundDiagnosticsAndRetainEntriesForRecovery()
        {
            List<string> delivered = new();
            AuditLogWriterOptions options = QuietOptions();
            options.DiagnosticQueueCapacity = 3;
            options.MaxDiagnosticsPerPump = 2;
            options.DiagnosticSink = (level, message) => delivered.Add(message);
            AuditLogWriter writer = CreateWriter(options);
            int failures = 0;
            writer.SimulateWriteFailureForTesting = () =>
            {
                Interlocked.Increment(ref failures);
                return true;
            };
            writer.Record(Entry("first"));
            writer.Record(Entry("second"));
            for (int i = 0; i < 64; i++)
            {
                writer.FlushForTesting(false);
            }

            writer.SimulateWriteFailureForTesting = null;
            writer.FlushForTesting(false);
            Assert.AreEqual(3, writer.PendingDiagnosticCount);
            Assert.AreEqual(failures - 3, writer.DroppedDiagnosticCount);
            Assert.IsEmpty(delivered, "File flushing must only enqueue diagnostics.");

            Assert.IsTrue(writer.DiagnosticPumpForTesting.MoveNext());
            Assert.AreEqual(2, delivered.Count);
            Assert.AreEqual(1, writer.PendingDiagnosticCount);
            writer.DiagnosticPumpForTesting.MoveNext();
            Assert.AreEqual(3, delivered.Count);
            Assert.AreEqual(0, writer.PendingDiagnosticCount);
            CollectionAssert.AreEqual(new[] { "first", "second" },
                AuditLogVerifier.ReadAll(writer.FilePath).Select(entry => entry.TraceId).ToArray());
            AssertVerified(writer.FilePath);
        }

        [Test]
        public void DiagnosticOverflow_DropsOldestAndReentrantDrainHonorsTickBudget()
        {
            List<string> delivered = new();
            AuditDiagnosticBuffer buffer = null;
            AuditLogWriterOptions options = QuietOptions();
            options.DiagnosticQueueCapacity = 3;
            options.MaxDiagnosticsPerPump = 2;
            options.DiagnosticSink = (level, message) =>
            {
                delivered.Add(message);
                if (message == "second")
                {
                    buffer.Enqueue(LogType.Warning, "reentrant");
                    buffer.Drain();
                }
            };
            buffer = new AuditDiagnosticBuffer(options);
            buffer.Enqueue(LogType.Warning, "first");
            buffer.Enqueue(LogType.Warning, "second");
            buffer.Enqueue(LogType.Warning, "third");
            buffer.Enqueue(LogType.Warning, "fourth");
            buffer.Drain();

            CollectionAssert.AreEqual(new[] { "second", "third" }, delivered);
            Assert.AreEqual(1, buffer.DroppedCount);
            Assert.AreEqual(2, buffer.Count);
            buffer.Drain();
            CollectionAssert.AreEqual(new[] { "second", "third", "fourth", "reentrant" }, delivered);
            buffer.Close();
        }

        [UnityTest]
        public IEnumerator PendingDiagnostics_AreDeliveredByOwnedPlayerLoopOnMainThread()
        {
            int mainThread = Thread.CurrentThread.ManagedThreadId;
            List<int> callbackThreads = new();
            AuditLogWriterOptions options = QuietOptions();
            options.DiagnosticSink = (level, message) => callbackThreads.Add(Thread.CurrentThread.ManagedThreadId);
            AuditLogWriter writer = CreateWriter(options);
            writer.SimulateWriteFailureForTesting = () => true;
            writer.Record(Entry("pump"));
            writer.FlushForTesting(false);
            writer.SimulateWriteFailureForTesting = null;
            writer.FlushForTesting(false);

            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            while (callbackThreads.Count == 0 && DateTime.UtcNow < deadline)
            {
                yield return null;
            }

            Assert.IsNotEmpty(callbackThreads, "The registered production pump must deliver without a test drain.");
            Assert.IsTrue(callbackThreads.All(thread => thread == mainThread));
            writer.Dispose();
            Assert.IsFalse(writer.DiagnosticPumpForTesting.MoveNext());
        }

        [Test]
        public void ThrowingSink_DisposeIsBoundedAndAccountsUndeliveredDiagnostics()
        {
            int baseline = AuditPathStateRegistry.Count;
            AuditLogWriterOptions options = QuietOptions();
            options.DiagnosticQueueCapacity = 8;
            options.MaxDiagnosticsPerPump = 2;
            options.DiagnosticSink = (level, message) => throw new InvalidOperationException("Rejected diagnostic.");
            AuditLogWriter writer = CreateWriter(options);
            int failures = 0;
            writer.SimulateWriteFailureForTesting = () =>
            {
                Interlocked.Increment(ref failures);
                return true;
            };
            writer.Record(Entry("throwing-sink"));
            for (int i = 0; i < 6; i++)
            {
                writer.FlushForTesting(false);
            }

            writer.SimulateWriteFailureForTesting = null;
            writer.FlushForTesting(false);
            Assert.DoesNotThrow(writer.Dispose);
            Assert.AreEqual(2, writer.DiagnosticDeliveryFailureCount);
            Assert.AreEqual(failures - 2, writer.DroppedDiagnosticCount);
            Assert.AreEqual(0, writer.PendingDiagnosticCount);
            Assert.IsFalse(writer.DiagnosticPumpForTesting.IsAttached);
            Assert.AreEqual(baseline, AuditPathStateRegistry.Count);
            Assert.DoesNotThrow(writer.Dispose);
        }

        [Test]
        public void SinkReenteringDisposeAndThrowing_DetachesPumpAndReleasesPath()
        {
            int baseline = AuditPathStateRegistry.Count;
            int deliveries = 0;
            AuditLogWriter writer = null;
            AuditLogWriterOptions options = QuietOptions();
            options.DiagnosticSink = (level, message) =>
            {
                deliveries++;
                writer.Dispose();
                throw new InvalidOperationException("Rejected after disposal.");
            };
            writer = CreateWriter(options);
            writer.SimulateWriteFailureForTesting = () => true;
            writer.Record(Entry("reentrant-sink"));
            writer.FlushForTesting(false);
            writer.SimulateWriteFailureForTesting = null;
            writer.FlushForTesting(false);

            Assert.IsFalse(writer.DiagnosticPumpForTesting.MoveNext());
            Assert.AreEqual(1, deliveries);
            Assert.AreEqual(1, writer.DiagnosticDeliveryFailureCount);
            Assert.AreEqual(0, writer.PendingDiagnosticCount);
            Assert.AreEqual(baseline, AuditPathStateRegistry.Count);
            Assert.IsFalse(writer.DiagnosticPumpForTesting.MoveNext());
            Assert.AreEqual(1, deliveries);

            AuditLogWriterOptions replacementOptions = QuietOptions();
            replacementOptions.MaxRetainedRotatedFiles = 1;
            AuditLogWriter replacement = CreateWriter(replacementOptions);
            replacement.Record(Entry("replacement"));
            replacement.FlushForTesting();
            AssertVerified(replacement.FilePath);
        }

        [Test]
        public void ConflictingSharedStorageLimits_RejectWithoutRetainingAnotherOwner()
        {
            int baseline = AuditPathStateRegistry.Count;
            AuditLogWriter first = CreateWriter(QuietOptions());
            AuditLogWriterOptions incompatible = QuietOptions();
            incompatible.MaxRetainedRotatedFiles = 1;
            Assert.Throws<InvalidOperationException>(() => new AuditLogWriter(_folder, incompatible));
            Assert.AreEqual(baseline + 1, AuditPathStateRegistry.Count);
            first.Record(Entry("still-usable"));
            first.FlushForTesting();
            first.Dispose();
            Assert.AreEqual(baseline, AuditPathStateRegistry.Count);
        }

        [Test]
        public void Rotation_PrunesOnlyOwnedFilesAndContinuesSequenceAcrossRestart()
        {
            string[] unrelatedNames =
            {
                "audit_1.jsonl", "audit_00001.jsonl", "audit_-001.jsonl", "audit_0000.jsonl",
                "audit_0001.jsonl.backup", "AUDIT_0500.JSONL", "audit_0042.jsonl"
            };
            foreach (string name in unrelatedNames)
            {
                File.WriteAllText(Path.Combine(_folder, name), "Unrelated bytes: " + name);
            }

            string reservedDirectory = Path.Combine(_folder, "audit_0043.jsonl");
            Directory.CreateDirectory(reservedDirectory);
            string nestedFile = Path.Combine(reservedDirectory, "audit_0001.jsonl");
            File.WriteAllText(nestedFile, "Nested unrelated bytes.");

            AuditLogWriterOptions options = QuietOptions();
            options.MaxRetainedRotatedFiles = 2;
            AuditLogWriter writer = CreateWriter(options);
            for (int i = 0; i < 5; i++)
            {
                writer.Record(Entry("rotation-" + i));
                writer.FlushForTesting();
                writer.RotateForTesting();
            }

            Assert.IsFalse(File.Exists(Path.Combine(_folder, "audit_0044.jsonl")));
            Assert.IsFalse(File.Exists(Path.Combine(_folder, "audit_0045.jsonl")));
            Assert.IsFalse(File.Exists(Path.Combine(_folder, "audit_0046.jsonl")));
            Assert.IsTrue(File.Exists(Path.Combine(_folder, "audit_0047.jsonl")));
            Assert.IsTrue(File.Exists(Path.Combine(_folder, "audit_0048.jsonl")));
            writer.Record(Entry("before-restart"));
            writer.FlushForTesting();
            writer.Dispose();

            AuditLogWriter resumed = CreateWriter(options);
            resumed.Record(Entry("after-restart"));
            resumed.FlushForTesting();
            List<AuditEntry> entries = AuditLogVerifier.ReadAll(resumed.FilePath);
            CollectionAssert.AreEqual(new long[] { 0, 1, 2 }, entries.Select(entry => entry.Seq).ToArray(),
                "Resume must use the stored tail sequence, excluding the anchor from line-count arithmetic.");
            resumed.RotateForTesting();
            Assert.IsFalse(File.Exists(Path.Combine(_folder, "audit_0047.jsonl")));
            Assert.IsTrue(File.Exists(Path.Combine(_folder, "audit_0049.jsonl")));
            AuditVerifyResult result = AuditLogVerifier.VerifyChainedSet(new[]
            {
                Path.Combine(_folder, "audit_0048.jsonl"),
                Path.Combine(_folder, "audit_0049.jsonl"), resumed.FilePath
            });
            Assert.IsTrue(result.Ok, result.Error);
            foreach (string name in unrelatedNames)
            {
                Assert.AreEqual("Unrelated bytes: " + name, File.ReadAllText(Path.Combine(_folder, name)));
            }

            Assert.AreEqual("Nested unrelated bytes.", File.ReadAllText(nestedFile));
        }

        [Test]
        public void ConfiguredFileSize_RotatesBeforeNextNonemptyFlush()
        {
            AuditLogWriterOptions options = QuietOptions();
            options.MaxFileSizeBytes = 1;
            AuditLogWriter writer = CreateWriter(options);
            writer.Record(Entry("first"));
            writer.FlushForTesting();
            writer.Record(Entry("second"));
            writer.FlushForTesting();
            string rotatedPath = Path.Combine(_folder, "audit_0001.jsonl");
            Assert.IsTrue(File.Exists(rotatedPath));
            AuditVerifyResult result = AuditLogVerifier.VerifyChainedSet(new[] { rotatedPath, writer.FilePath });
            Assert.IsTrue(result.Ok, result.Error);
            Assert.AreEqual("second", AuditLogVerifier.ReadAll(writer.FilePath).Last().TraceId);
        }

        [Test]
        public void SharedPathRotation_UpdatesBothWritersAndPreservesCrossFileLink()
        {
            AuditLogWriterOptions options = QuietOptions();
            options.MaxRetainedRotatedFiles = 1;
            AuditLogWriter first = CreateWriter(options);
            AuditLogWriter second = CreateWriter(options);
            first.Record(Entry("before-rotation"));
            first.FlushForTesting();
            second.RotateForTesting();
            second.Record(Entry("second-after-rotation"));
            second.FlushForTesting();
            first.Record(Entry("first-after-rotation"));
            first.FlushForTesting();
            CollectionAssert.AreEqual(new long[] { 0, 1, 2 },
                AuditLogVerifier.ReadAll(first.FilePath).Select(entry => entry.Seq).ToArray());
            AuditVerifyResult result = AuditLogVerifier.VerifyChainedSet(new[]
            {
                Path.Combine(_folder, "audit_0001.jsonl"), first.FilePath
            });
            Assert.IsTrue(result.Ok, result.Error);
        }

        [Test]
        public void RotationReplacementFailure_SharedWriterRetriesOneMarkerAndRequeuesBatch()
        {
            AuditLogWriter first = CreateWriter(QuietOptions());
            AuditLogWriter second = CreateWriter(QuietOptions());
            first.Record(Entry("before-failed-rotation"));
            first.FlushForTesting();
            string blockedAnchor = first.FilePath + ".rotation.tmp";
            Directory.CreateDirectory(blockedAnchor);
            Assert.Catch(() => first.RotateForTesting());
            second.Record(Entry("after-failed-rotation"));
            second.FlushForTesting(false);
            Assert.AreEqual(1, AuditLogVerifier.ReadAll(first.FilePath)
                .Count(entry => entry.Kind == AuditEntryKind.RotationMarker));
            Directory.Delete(blockedAnchor);
            second.FlushForTesting();
            first.Record(Entry("after-recovery"));
            first.FlushForTesting();

            string rotated = Path.Combine(_folder, "audit_0001.jsonl");
            Assert.AreEqual(1, AuditLogVerifier.ReadAll(rotated)
                .Count(entry => entry.Kind == AuditEntryKind.RotationMarker));
            CollectionAssert.AreEqual(new[] { "after-failed-rotation", "after-recovery" },
                AuditLogVerifier.ReadAll(first.FilePath).Where(entry => entry.Kind == AuditEntryKind.ToolCall)
                    .Select(entry => entry.TraceId).ToArray());
            AuditVerifyResult result = AuditLogVerifier.VerifyChainedSet(new[] { rotated, first.FilePath });
            Assert.IsTrue(result.Ok, result.Error);
        }

        [Test]
        public void RestartAfterRotationReplacementFailure_ReusesPersistedMarker()
        {
            AuditLogWriter first = CreateWriter(QuietOptions());
            first.Record(Entry("before-restart"));
            first.FlushForTesting();
            string blockedAnchor = first.FilePath + ".rotation.tmp";
            Directory.CreateDirectory(blockedAnchor);
            Assert.Catch(() => first.RotateForTesting());
            first.Dispose();
            Directory.Delete(blockedAnchor);

            AuditLogWriter resumed = CreateWriter(QuietOptions());
            resumed.Record(Entry("after-restart"));
            resumed.FlushForTesting();
            string rotated = Path.Combine(_folder, "audit_0001.jsonl");
            Assert.AreEqual(1, AuditLogVerifier.ReadAll(rotated)
                .Count(entry => entry.Kind == AuditEntryKind.RotationMarker));
            CollectionAssert.AreEqual(new long[] { 0, 1 },
                AuditLogVerifier.ReadAll(resumed.FilePath).Select(entry => entry.Seq).ToArray());
            AuditVerifyResult result = AuditLogVerifier.VerifyChainedSet(new[] { rotated, resumed.FilePath });
            Assert.IsTrue(result.Ok, result.Error);
        }

#if !UNITY_WEBGL
        [Test]
        public void OffMainThreadFlushPumpAndDispose_NeverInvokeDiagnosticSink()
        {
            int callbacks = 0;
            AuditLogWriterOptions options = QuietOptions();
            options.DiagnosticSink = (level, message) => Interlocked.Increment(ref callbacks);
            AuditLogWriter writer = CreateWriter(options);
            writer.SimulateWriteFailureForTesting = () => true;
            writer.Record(Entry("background"));
            Assert.IsTrue(Task.Run(writer.FlushForTesting).Wait(TimeSpan.FromSeconds(5)));
            writer.SimulateWriteFailureForTesting = null;
            Assert.IsTrue(Task.Run(() => writer.FlushForTesting(false)).Wait(TimeSpan.FromSeconds(5)));
            Assert.Greater(writer.PendingDiagnosticCount, 0);
            Assert.IsTrue(Task.Run(() => writer.DiagnosticPumpForTesting.MoveNext()).Wait(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(0, callbacks);
            Assert.IsTrue(Task.Run(writer.Dispose).Wait(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(0, callbacks);
            Assert.Greater(writer.DroppedDiagnosticCount, 0);
            Assert.AreEqual(0, writer.PendingDiagnosticCount);
            Assert.IsFalse(writer.DiagnosticPumpForTesting.IsAttached);
        }

        [Test]
        public void CanonicalPathWriters_ConcurrentFlushAndOwnerRetirement_PreserveOneChain()
        {
            int baseline = AuditPathStateRegistry.Count;
            AuditLogWriter first = CreateWriter(QuietOptions());
            string alias = Path.Combine(_folder, "alias", "..");
            AuditLogWriter second = new(alias, QuietOptions());
            _writers.Add(second);
            Assert.AreEqual(baseline + 1, AuditPathStateRegistry.Count);
            for (int i = 0; i < 100; i++)
            {
                first.Record(Entry("first-" + i));
                second.Record(Entry("second-" + i));
            }

            Task.WaitAll(Task.Run(() => first.FlushForTesting(false)),
                Task.Run(() => second.FlushForTesting(false)));
            first.Dispose();
            second.Record(Entry("after-owner-retired"));
            second.FlushForTesting();
            List<AuditEntry> entries = AuditLogVerifier.ReadAll(second.FilePath);
            Assert.AreEqual(201, entries.Count);
            CollectionAssert.AreEqual(Enumerable.Range(1, 201).Select(value => (long)value).ToArray(),
                entries.Select(entry => entry.Seq).ToArray());
            Assert.AreEqual(201, entries.Select(entry => entry.TraceId).Distinct().Count());
            AssertVerified(second.FilePath);
            second.Dispose();
            Assert.AreEqual(baseline, AuditPathStateRegistry.Count);
        }

        [Test]
        public void PathRegistry_RetainsQueuedWaiterAfterOwnerReleaseAndReclaimsAfterLastOperation()
        {
            int baseline = AuditPathStateRegistry.Count;
            string path = Path.Combine(_folder, "audit.jsonl");
            AuditPathStateRegistry.Lease owner = AuditPathStateRegistry.Acquire(path, 1024, 2);
            AuditPathStateRegistry.State state = owner.SharedState;
            AuditPathStateRegistry.Lease operation = owner.Retain();
            using ManualResetEventSlim waiting = new();
            using ManualResetEventSlim entered = new();
            Task waiter = null;
            Monitor.Enter(state.Gate);
            try
            {
                waiter = Task.Run(() =>
                {
                    using (operation)
                    {
                        waiting.Set();
                        lock (state.Gate)
                        {
                            entered.Set();
                        }
                    }
                });
                Assert.IsTrue(waiting.Wait(TimeSpan.FromSeconds(2)));
                owner.Dispose();
                using AuditPathStateRegistry.Lease replacement = AuditPathStateRegistry.Acquire(path, 1024, 2);
                Assert.AreSame(state, replacement.SharedState,
                    "A queued operation must prevent creating a second lock for the same path.");
                Assert.IsFalse(entered.IsSet);
                Assert.AreEqual(baseline + 1, AuditPathStateRegistry.Count);
            }
            finally
            {
                Monitor.Exit(state.Gate);
                owner.Dispose();
                if (waiter != null)
                {
                    Assert.IsTrue(waiter.Wait(TimeSpan.FromSeconds(2)));
                }
                else
                {
                    operation.Dispose();
                }
            }

            Assert.IsTrue(entered.IsSet);
            Assert.AreEqual(baseline, AuditPathStateRegistry.Count);
            Assert.IsNull(owner.Retain());
        }

        [Test]
        public void DisposedWriters_KeepActiveAndWaitingAppendsOnOnePathState()
        {
            int baseline = AuditPathStateRegistry.Count;
            AuditLogWriter first = CreateWriter(QuietOptions());
            AuditLogWriter second = CreateWriter(QuietOptions());
            AuditPathStateRegistry.State state;
            using (AuditPathStateRegistry.Lease probe = AuditPathStateRegistry.Acquire(first.FilePath,
                50L * 1024 * 1024, 8))
            {
                state = probe.SharedState;
            }

            using ManualResetEventSlim appendEntered = new();
            using ManualResetEventSlim releaseAppend = new();
            using ManualResetEventSlim waiterStarted = new();
            Task firstFlush = null;
            Task secondFlush = null;
            Task[] disposals = Array.Empty<Task>();
            first.SimulateWriteFailureForTesting = () =>
            {
                appendEntered.Set();
                if (!releaseAppend.Wait(TimeSpan.FromSeconds(15)))
                {
                    throw new TimeoutException("The test did not release the active append.");
                }

                return false;
            };
            try
            {
                first.Record(Entry("active-append"));
                firstFlush = Task.Run(() => first.FlushForTesting(false));
                Assert.IsTrue(appendEntered.Wait(TimeSpan.FromSeconds(2)));
                second.Record(Entry("waiting-append"));
                secondFlush = Task.Run(() =>
                {
                    waiterStarted.Set();
                    second.FlushForTesting(false);
                });
                Assert.IsTrue(waiterStarted.Wait(TimeSpan.FromSeconds(2)));
                Assert.IsTrue(SpinWait.SpinUntil(() => Volatile.Read(ref state.Users) >= 4,
                    TimeSpan.FromSeconds(2)));
                disposals = new[] { Task.Run(first.Dispose), Task.Run(second.Dispose) };
                Assert.IsTrue(Task.WaitAll(disposals, TimeSpan.FromSeconds(4)),
                    "Disposal must not wait indefinitely for a shared-path operation.");
                Assert.AreEqual(baseline + 1, AuditPathStateRegistry.Count);
                using (AuditPathStateRegistry.Lease replacement = AuditPathStateRegistry.Acquire(first.FilePath,
                    50L * 1024 * 1024, 8))
                {
                    Assert.AreSame(state, replacement.SharedState);
                    releaseAppend.Set();
                    Assert.IsTrue(Task.WaitAll(new[] { firstFlush, secondFlush }, TimeSpan.FromSeconds(4)));
                }
            }
            finally
            {
                releaseAppend.Set();
                first.SimulateWriteFailureForTesting = null;
                Task[] pending = new[] { firstFlush, secondFlush }.Where(task => task != null)
                    .Concat(disposals).ToArray();
                if (pending.Length > 0)
                {
                    Assert.IsTrue(Task.WaitAll(pending, TimeSpan.FromSeconds(4)));
                }
            }

            CollectionAssert.AreEquivalent(new[] { "active-append", "waiting-append" },
                AuditLogVerifier.ReadAll(first.FilePath).Select(entry => entry.TraceId).ToArray());
            AssertVerified(first.FilePath);
            Assert.AreEqual(baseline, AuditPathStateRegistry.Count);
        }
#endif

        [Test]
        public void UnpairedSurrogateDiagnostic_DoesNotPoisonFlush()
        {
            AuditLogWriter writer = CreateWriter(QuietOptions());
            string poisonResult = "prefix" + "\ud83d" + "suffix";
            AuditEntry poison = AuditEntry.ForToolCall(0, "surrogate", "actor", "model", "prompt", "tool", "{}",
                "allowed", "ok", poisonResult, 1);
            writer.Record(poison);
            writer.Record(Entry("healthy"));
            writer.FlushForTesting();
            List<AuditEntry> persisted = AuditLogVerifier.ReadAll(writer.FilePath);
            CollectionAssert.AreEqual(new[] { "surrogate", "healthy" },
                persisted.Select(entry => entry.TraceId).ToArray());
            Assert.IsTrue(IsWellFormedUnicode(persisted.First(entry => entry.TraceId == "surrogate").ResultDetail));
            AssertVerified(writer.FilePath);
        }

        private static bool IsWellFormedUnicode(string text)
        {
            if (text == null)
            {
                return true;
            }

            for (int index = 0; index < text.Length; index++)
            {
                if (char.IsLowSurrogate(text[index]))
                {
                    return false;
                }

                if (!char.IsHighSurrogate(text[index]))
                {
                    continue;
                }

                if (index + 1 >= text.Length || !char.IsLowSurrogate(text[++index]))
                {
                    return false;
                }
            }

            return true;
        }

        private AuditLogWriter CreateWriter(AuditLogWriterOptions options)
        {
            AuditLogWriter writer = new(_folder, options);
            _writers.Add(writer);
            return writer;
        }

        private byte[] RememberOriginalBytes(AuditLogWriter writer)
        {
            bool existed = File.Exists(writer.FilePath);
            byte[] bytes = existed ? File.ReadAllBytes(writer.FilePath) : Array.Empty<byte>();
            _recoveryBackups.Add((Path.GetDirectoryName(writer.FilePath), bytes, existed));
            return bytes;
        }

        private void RestoreRememberedQuarantines()
        {
            foreach ((string Folder, byte[] Bytes, bool Existed) backup in _recoveryBackups)
            {
                string path = Path.Combine(backup.Folder, "audit.jsonl");
                using AuditPathStateRegistry.Lease operation = AuditPathStateRegistry.TryAcquireExisting(path);
                if (operation == null)
                {
                    continue;
                }

                lock (operation.SharedState.Gate)
                {
                    if (!operation.SharedState.Quarantined)
                    {
                        continue;
                    }

                    if (backup.Existed)
                    {
                        File.WriteAllBytes(path, backup.Bytes);
                    }
                    else if (File.Exists(path))
                    {
                        File.Delete(path);
                    }

                    if (!AuditLogWriter.TryRecoverQuarantinedPath(backup.Folder, out string error))
                    {
                        throw new InvalidOperationException("Quarantine cleanup could not verify recovery: " + error);
                    }
                }
            }
        }

        private static byte[] AlternateValidSeed(byte[] originalBytes)
        {
            using StringReader reader = new(Encoding.UTF8.GetString(originalBytes).TrimEnd('\r', '\n'));
            using JsonTextReader json = new(reader) { DateParseHandling = DateParseHandling.None };
            JObject entry = JObject.Load(json);
            entry["TraceId"] = "fake";
            entry["hash"] = "";
            string preimage = entry.ToString(Formatting.None);
            entry["hash"] = AuditHash.Chain((string)entry["prevHash"] ?? "", preimage);
            return Encoding.UTF8.GetBytes(entry.ToString(Formatting.None) + Environment.NewLine);
        }

        private static AuditLogWriterOptions QuietOptions()
        {
            return new AuditLogWriterOptions { DiagnosticSink = (level, message) => { } };
        }

        private static AuditEntry Entry(string trace)
        {
            return AuditEntry.ForToolCall(0, trace, "actor", "model", "prompt", "tool", "{}",
                "allowed", "ok", "", 1);
        }

        private static void AssertVerified(string path)
        {
            AuditVerifyResult result = AuditLogVerifier.Verify(path);
            Assert.IsTrue(result.Ok, result.Error);
        }
    }
}
