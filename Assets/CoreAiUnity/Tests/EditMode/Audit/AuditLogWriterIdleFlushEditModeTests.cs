using System;
using System.IO;
using CoreAI.Audit;
using CoreAI.Features.Audit;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace CoreAI.Tests.EditMode.Audit
{
    /// <summary>
    /// The audit flush tick runs twice a second for the whole session (on WebGL on the main thread) and
    /// the queue is empty almost every time. That idle tick must be free: it used to allocate a batch
    /// list before discovering there was nothing to write.
    /// </summary>
    [Category("Audit")]
    public sealed class AuditLogWriterIdleFlushEditModeTests
    {
        private string _testFolder;
        private AuditLogWriter _writer;

        [SetUp]
        public void SetUp()
        {
            _testFolder = Path.Combine(Application.temporaryCachePath,
                "AuditIdleFlush_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_testFolder);
            _writer = new AuditLogWriter(_testFolder);
        }

        [TearDown]
        public void TearDown()
        {
            _writer?.Dispose();
            _writer = null;
            try
            {
                Directory.Delete(_testFolder, true);
            }
            catch
            {
            }
        }

        [Test]
        public void Flush_WithEmptyQueue_DoesNotAllocateGcMemory()
        {
            _writer.FlushForTesting(); // warm-up

            Assert.That(() => _writer.FlushForTesting(), Is.Not.AllocatingGCMemory(),
                "An idle flush tick must not allocate.");
        }

        [Test]
        public void Flush_AfterEntriesWereWritten_IdleTickIsStillFree()
        {
            _writer.Record(AuditEntry.ForToolCall(0, "t", "a", "m", "ph", "test", "{}", "allowed", "ok", "", 0));
            _writer.FlushForTesting();
            Assert.IsTrue(File.Exists(_writer.FilePath), "The non-empty flush must still reach the file.");

            _writer.FlushForTesting(); // warm-up of the idle path after a write

            Assert.That(() => _writer.FlushForTesting(), Is.Not.AllocatingGCMemory());
        }
    }
}
