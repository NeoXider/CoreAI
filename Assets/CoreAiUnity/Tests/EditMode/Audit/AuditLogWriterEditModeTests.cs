using System;
using System.IO;
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
    }
}
