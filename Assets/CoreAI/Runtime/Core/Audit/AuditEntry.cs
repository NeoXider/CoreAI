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
        PolicyDecision,
        ChainReset
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
            string hash = "",
            string sourceTag = "",
            DateTime ts = default)
        {
            Seq = seq;
            Ts = ts == default ? DateTime.UtcNow : ts;
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
            SourceTag = sourceTag ?? "";
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
        public string SourceTag { get; }

        [JsonIgnore]
        public string PrevHash { get; }
        [JsonIgnore]
        public string Hash { get; }

        [JsonProperty("prevHash")]
        private string PrevHashForSerialization => PrevHash;
        [JsonProperty("hash")]
        private string HashForSerialization => Hash;

        /// <summary>
        /// Returns a copy of this entry with only the <see cref="Hash"/> field changed — used to
        /// go from the canonical preimage (hash="") to the final stored line without disturbing
        /// Seq/Ts/PrevHash/etc, which are exactly what got hashed.
        /// </summary>
        public AuditEntry WithHash(string hash)
        {
            return new AuditEntry(
                seq: Seq,
                kind: Kind,
                traceId: TraceId,
                actor: Actor,
                model: Model,
                promptHash: PromptHash,
                toolName: ToolName,
                args: Args,
                policyDecision: PolicyDecision,
                result: Result,
                resultDetail: ResultDetail,
                durationMs: DurationMs,
                worldDiff: WorldDiff,
                rollbackHandle: RollbackHandle,
                prevHash: PrevHash,
                hash: hash,
                sourceTag: SourceTag,
                ts: Ts);
        }

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
                rollbackHandle: rollbackHandle,
                sourceTag: sourceTag);
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
                promptHash: promptHash,
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

        /// <summary>
        /// Marks that the writer could not resume the previous hash chain (corrupt tail line or
        /// I/O failure) and restarted it from genesis. This entry is itself part of the chain
        /// that follows it, so the reset is audited rather than silently hidden.
        /// </summary>
        public static AuditEntry ForChainReset(long seq, string actor, string reason)
        {
            return new AuditEntry(
                seq: seq,
                kind: AuditEntryKind.ChainReset,
                traceId: "",
                actor: actor,
                policyDecision: "reset",
                result: "error",
                resultDetail: reason ?? "");
        }
    }
}
