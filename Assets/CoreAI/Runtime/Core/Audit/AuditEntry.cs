using System;
using Newtonsoft.Json;

namespace CoreAI.Audit
{
    public enum AuditEntryKind
    {
        LlmRequest,
        LlmResponse,
        ToolCall,
        WorldMutation,
        PolicyDecision
    }

    public readonly struct AuditEntry
    {
        public AuditEntry(
            long seq,
            AuditEntryKind kind,
            string traceId,
            string actor,
            string model = "",
            string promptHash = "",
            string toolName = "",
            string args = "",
            string policyDecision = "",
            string result = "",
            string resultDetail = "",
            double durationMs = 0,
            string worldDiff = "",
            string rollbackHandle = "",
            string prevHash = "",
            string hash = "")
        {
            Seq = seq;
            Ts = DateTime.UtcNow;
            Kind = kind;
            TraceId = traceId ?? "";
            Actor = actor ?? "";
            Model = model ?? "";
            PromptHash = promptHash ?? "";
            ToolName = toolName ?? "";
            Args = args ?? "";
            PolicyDecision = policyDecision ?? "";
            Result = result ?? "";
            ResultDetail = resultDetail ?? "";
            DurationMs = durationMs;
            WorldDiff = worldDiff ?? "";
            RollbackHandle = rollbackHandle ?? "";
            PrevHash = prevHash ?? "";
            Hash = hash ?? "";
        }

        public long Seq { get; }
        public DateTime Ts { get; }
        public AuditEntryKind Kind { get; }

        public string TraceId { get; }
        public string Actor { get; }
        public string Model { get; }
        public string PromptHash { get; }

        public string ToolName { get; }
        public string Args { get; }
        public string PolicyDecision { get; }

        public string Result { get; }
        public string ResultDetail { get; }
        public double DurationMs { get; }

        public string WorldDiff { get; }
        public string RollbackHandle { get; }

        [JsonIgnore]
        public string PrevHash { get; }
        [JsonIgnore]
        public string Hash { get; }

        [JsonProperty("prevHash")]
        private string PrevHashForSerialization => PrevHash;
        [JsonProperty("hash")]
        private string HashForSerialization => Hash;

        public static AuditEntry ForToolCall(
            long seq,
            string traceId,
            string actor,
            string model,
            string promptHash,
            string toolName,
            string args,
            string policyDecision,
            string result,
            string resultDetail,
            double durationMs)
        {
            return new AuditEntry(
                seq: seq,
                kind: AuditEntryKind.ToolCall,
                traceId: traceId,
                actor: actor,
                model: model,
                promptHash: promptHash,
                toolName: toolName,
                args: args,
                policyDecision: policyDecision,
                result: result,
                resultDetail: resultDetail,
                durationMs: durationMs);
        }

        public static AuditEntry ForWorldMutation(
            long seq,
            string traceId,
            string actor,
            string commandTypeId,
            string jsonPayload,
            string sourceTag,
            bool success,
            string worldDiff = "",
            string rollbackHandle = "")
        {
            return new AuditEntry(
                seq: seq,
                kind: AuditEntryKind.WorldMutation,
                traceId: traceId,
                actor: actor,
                toolName: commandTypeId,
                args: jsonPayload,
                policyDecision: success ? "allowed" : "failed",
                result: success ? "ok" : "error",
                worldDiff: worldDiff,
                rollbackHandle: rollbackHandle);
        }

        public static AuditEntry ForLlmRequest(
            long seq,
            string traceId,
            string actor,
            string model,
            string promptHash,
            string routingProfileId,
            bool streaming)
        {
            return new AuditEntry(
                seq: seq,
                kind: AuditEntryKind.LlmRequest,
                traceId: traceId,
                actor: actor,
                model: model,
                args: $"{{\"routingProfile\":\"{routingProfileId}\",\"streaming\":{streaming}}}",
                policyDecision: "started",
                result: "pending");
        }

        public static AuditEntry ForLlmResponse(
            long seq,
            string traceId,
            string actor,
            string model,
            string promptHash,
            bool success,
            string error)
        {
            return new AuditEntry(
                seq: seq,
                kind: AuditEntryKind.LlmResponse,
                traceId: traceId,
                actor: actor,
                model: model,
                promptHash: promptHash,
                policyDecision: success ? "completed" : "failed",
                result: success ? "ok" : "error",
                resultDetail: error);
        }
    }
}
