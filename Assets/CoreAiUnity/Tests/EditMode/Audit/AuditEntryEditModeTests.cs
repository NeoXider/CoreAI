using CoreAI.Audit;
using Newtonsoft.Json;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.Audit
{
    public sealed class AuditEntryEditModeTests
    {
        [Test]
        public void ForToolCall_SetsCorrectFields()
        {
            AuditEntry entry = AuditEntry.ForToolCall(
                seq: 1,
                traceId: "trace-1",
                actor: "creator",
                model: "gpt-4",
                promptHash: "abc123",
                toolName: "world_command",
                args: "{\"action\":\"spawn\"}",
                policyDecision: "allowed",
                result: "ok",
                resultDetail: "{\"ok\":true}",
                durationMs: 45);

            Assert.AreEqual(1, entry.Seq);
            Assert.AreEqual(AuditEntryKind.ToolCall, entry.Kind);
            Assert.AreEqual("trace-1", entry.TraceId);
            Assert.AreEqual("creator", entry.Actor);
            Assert.AreEqual("gpt-4", entry.Model);
            Assert.AreEqual("abc123", entry.PromptHash);
            Assert.AreEqual("world_command", entry.ToolName);
            Assert.AreEqual("allowed", entry.PolicyDecision);
            Assert.AreEqual("ok", entry.Result);
            Assert.AreEqual(45, entry.DurationMs);
        }

        [Test]
        public void ForWorldMutation_SetsCorrectFields()
        {
            AuditEntry entry = AuditEntry.ForWorldMutation(
                seq: 2,
                traceId: "trace-2",
                actor: "mod",
                commandTypeId: "spawn",
                jsonPayload: "{\"prefab\":\"cube\"}",
                sourceTag: "lua:world_command",
                success: true);

            Assert.AreEqual(2, entry.Seq);
            Assert.AreEqual(AuditEntryKind.WorldMutation, entry.Kind);
            Assert.AreEqual("mod", entry.Actor);
            Assert.AreEqual("spawn", entry.ToolName);
            Assert.AreEqual("{\"prefab\":\"cube\"}", entry.Args);
            Assert.AreEqual("allowed", entry.PolicyDecision);
            Assert.AreEqual("ok", entry.Result);
        }

        [Test]
        public void ForWorldMutation_Failure_SetsError()
        {
            AuditEntry entry = AuditEntry.ForWorldMutation(
                seq: 3, traceId: "t", actor: "a",
                commandTypeId: "spawn", jsonPayload: "{}", sourceTag: "test", success: false);

            Assert.AreEqual("failed", entry.PolicyDecision);
            Assert.AreEqual("error", entry.Result);
        }

        [Test]
        public void ForLlmRequest_SetsCorrectKind()
        {
            AuditEntry entry = AuditEntry.ForLlmRequest(1, "t", "creator", "gpt-4", "hash", "default", true);

            Assert.AreEqual(AuditEntryKind.LlmRequest, entry.Kind);
            Assert.AreEqual("creator", entry.Actor);
            Assert.AreEqual("pending", entry.Result);
        }

        [Test]
        public void ForLlmResponse_SetsCorrectKind()
        {
            AuditEntry entry = AuditEntry.ForLlmResponse(1, "t", "creator", "gpt-4", "hash", true, "");

            Assert.AreEqual(AuditEntryKind.LlmResponse, entry.Kind);
            Assert.AreEqual("completed", entry.PolicyDecision);
            Assert.AreEqual("ok", entry.Result);
        }

        [Test]
        public void ForLlmResponse_Failure_SetsError()
        {
            AuditEntry entry = AuditEntry.ForLlmResponse(1, "t", "creator", "gpt-4", "hash", false, "timeout");

            Assert.AreEqual("failed", entry.PolicyDecision);
            Assert.AreEqual("error", entry.Result);
            Assert.AreEqual("timeout", entry.ResultDetail);
        }

        [Test]
        public void Serialization_RoundTrip_KeepsFields()
        {
            AuditEntry original = AuditEntry.ForToolCall(
                seq: 42, traceId: "tr", actor: "a", model: "m", promptHash: "ph",
                toolName: "test", args: "{}", policyDecision: "allowed",
                result: "ok", resultDetail: "done", durationMs: 100);

            string json = JsonConvert.SerializeObject(original, new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore
            });

            Assert.IsTrue(json.Contains("\"Seq\":42"));
            Assert.IsTrue(json.Contains("\"Kind\":"));
            Assert.IsTrue(json.Contains("\"TraceId\":\"tr\""));
            Assert.IsTrue(json.Contains("\"PolicyDecision\":\"allowed\""));
        }
    }
}
