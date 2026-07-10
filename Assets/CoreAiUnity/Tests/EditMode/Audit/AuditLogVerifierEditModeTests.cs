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

            // The resume path INTENTIONALLY logs an error when the tail is corrupt (that is the
            // audited chain-reset behavior under test) — declare it so LogAssert doesn't fail the test.
            UnityEngine.TestTools.LogAssert.Expect(
                LogType.Error,
                new System.Text.RegularExpressions.Regex(@"\[AuditLogWriter\] Audit log tail line is corrupt"));

            using (AuditLogWriter resumed = new(_testFolder))
            {
                // ResumeChain() already flushed the marker synchronously; nothing else to do.
            }

            List<AuditEntry> entries = AuditLogVerifier.ReadAll(filePath);
            Assert.IsTrue(entries.Any(e => e.Kind == AuditEntryKind.ChainReset), "Expected a ChainReset entry to be appended after resume.");

            AuditVerifyResult result = AuditLogVerifier.Verify(filePath);
            Assert.IsFalse(result.Ok, "Truncated tail line should break verification.");
        }

        [Test]
        public void Rotation_BothFiles_VerifyStandaloneAndAnchorLinked()
        {
            using AuditLogWriter writer = new(_testFolder);

            // Pad past MaxFileSize (50 MB) with a large Args payload per entry so rotation triggers
            // through the normal flush path rather than by tampering with the file on disk.
            string padding = new string('x', 11_000);
            for (int i = 0; i < 5000; i++)
            {
                writer.Record(AuditEntry.ForToolCall(
                    seq: 0, traceId: $"trace-{i}", actor: "creator", model: "gpt-4", promptHash: "ph",
                    toolName: "test_tool", args: padding, policyDecision: "allowed",
                    result: "ok", resultDetail: "", durationMs: i));
            }

            writer.FlushForTesting(); // file now exceeds MaxFileSize, but rotation is checked at the START of a flush

            writer.Record(AuditEntry.ForToolCall(
                seq: 0, traceId: "after-rotation", actor: "creator", model: "gpt-4", promptHash: "ph",
                toolName: "test_tool", args: "{}", policyDecision: "allowed", result: "ok", resultDetail: "", durationMs: 1));
            writer.FlushForTesting(); // this flush should detect the oversized file and rotate

            string rotatedPath = Path.Combine(_testFolder, "audit_0001.jsonl");
            string activePath = writer.FilePath;

            Assert.IsTrue(File.Exists(rotatedPath), "Expected the oversized file to be rotated aside.");
            Assert.IsTrue(File.Exists(activePath), "Expected a fresh active file after rotation.");

            AuditVerifyResult rotatedResult = AuditLogVerifier.Verify(rotatedPath);
            Assert.IsTrue(rotatedResult.Ok, $"Rotated file should verify standalone: {rotatedResult.Error}");

            AuditVerifyResult activeResult = AuditLogVerifier.Verify(activePath);
            Assert.IsTrue(activeResult.Ok, $"New active file should verify standalone via anchored genesis: {activeResult.Error}");

            List<AuditEntry> rotatedEntries = AuditLogVerifier.ReadAll(rotatedPath);
            Assert.AreEqual(AuditEntryKind.RotationMarker, rotatedEntries[^1].Kind,
                "Rotated file's last entry should be the RotationMarker.");

            List<AuditEntry> activeEntries = AuditLogVerifier.ReadAll(activePath);
            Assert.AreEqual(AuditEntryKind.RotationAnchor, activeEntries[0].Kind,
                "New active file should open with a RotationAnchor.");
            Assert.AreEqual(rotatedEntries[^1].Hash, activeEntries[0].PrevHash,
                "Anchor's prevHash should equal the rotated file's final hash.");

            AuditVerifyResult setResult = AuditLogVerifier.VerifyChainedSet(new[] { rotatedPath, activePath });
            Assert.IsTrue(setResult.Ok, $"Rotated set should verify as linked: {setResult.Error}");
        }
    }
}
