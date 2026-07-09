using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CoreAI.Audit;
using CoreAI.Features.Audit;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode.Audit
{
    public sealed class AuditLogVerifierEditModeTests
    {
        private string _testFolder;

        [SetUp]
        public void SetUp()
        {
            _testFolder = Path.Combine(Application.temporaryCachePath, "AuditVerifierTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_testFolder);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_testFolder))
            {
                try { Directory.Delete(_testFolder, recursive: true); }
                catch { }
            }
        }

        private static void WriteEntries(AuditLogWriter writer, int count)
        {
            for (int i = 0; i < count; i++)
            {
                writer.Record(AuditEntry.ForToolCall(
                    seq: 0,
                    traceId: $"trace-{i}",
                    actor: "creator",
                    model: "gpt-4",
                    promptHash: "ph",
                    toolName: "test_tool",
                    args: $"{{\"i\":{i}}}",
                    policyDecision: "allowed",
                    result: "ok",
                    resultDetail: "",
                    durationMs: i));
            }

            writer.FlushForTesting();
        }

        [Test]
        public void Verify_FreshlyWrittenChain_IsOk()
        {
            using AuditLogWriter writer = new(_testFolder);
            WriteEntries(writer, 5);

            AuditVerifyResult result = AuditLogVerifier.Verify(writer.FilePath);

            Assert.IsTrue(result.Ok, result.Error);
            Assert.AreEqual(5, result.LineCount);
            Assert.AreEqual(-1, result.FirstBrokenSeq);
        }

        [Test]
        public void ReadAll_ReturnsAllWrittenEntries()
        {
            using AuditLogWriter writer = new(_testFolder);
            WriteEntries(writer, 5);

            List<AuditEntry> entries = AuditLogVerifier.ReadAll(writer.FilePath);

            Assert.AreEqual(5, entries.Count);
        }

        [Test]
        public void Verify_TamperedMiddleLine_ReportsFirstBrokenSeq()
        {
            AuditLogWriter writer = new(_testFolder);
            WriteEntries(writer, 5);
            string filePath = writer.FilePath;
            writer.Dispose();

            string[] lines = File.ReadAllLines(filePath);
            JObject tampered = JObject.Parse(lines[2]);
            long tamperedSeq = tampered["Seq"].Value<long>();
            tampered["Args"] = "{\"i\":\"tampered\"}";
            lines[2] = tampered.ToString(Formatting.None);
            File.WriteAllLines(filePath, lines);

            AuditVerifyResult result = AuditLogVerifier.Verify(filePath);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(tamperedSeq, result.FirstBrokenSeq);
        }

        [Test]
        public void ResumeChain_TruncatedTailLine_AppendsChainResetMarker_AndVerifyDetectsBreak()
        {
            AuditLogWriter writer = new(_testFolder);
            WriteEntries(writer, 3);
            string filePath = writer.FilePath;
            writer.Dispose();

            // Simulate a crash mid-write: cut the last line in half, no trailing newline.
            string content = File.ReadAllText(filePath);
            int lastNewline = content.TrimEnd('\n').LastIndexOf('\n');
            string head = content.Substring(0, lastNewline + 1);
            string tail = content.Substring(lastNewline + 1);
            string truncatedTail = tail.Substring(0, tail.Length / 2);
            File.WriteAllText(filePath, head + truncatedTail);

            using (AuditLogWriter resumed = new(_testFolder))
            {
                // ResumeChain() already flushed the marker synchronously; nothing else to do.
            }

            List<AuditEntry> entries = AuditLogVerifier.ReadAll(filePath);
            Assert.IsTrue(entries.Any(e => e.Kind == AuditEntryKind.ChainReset), "Expected a ChainReset entry to be appended after resume.");

            AuditVerifyResult result = AuditLogVerifier.Verify(filePath);
            Assert.IsFalse(result.Ok, "Truncated tail line should break verification.");
        }
    }
}
