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
        ChainReset,

        /// <summary>Final entry of a file being rotated away; its hash is what the next file's <see cref="RotationAnchor"/> embeds as the cross-file link.</summary>
        RotationMarker,

        /// <summary>First entry of a file created by rotation; its stored <c>prevHash</c> is the previous file's final hash rather than the empty-string genesis.</summary>
        RotationAnchor,

        /// <summary>Audits that the writer's bounded in-memory queue dropped older entries due to sustained backpressure.</summary>
        QueueDropped
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
                Seq,
                Kind,
                TraceId,
                Actor,
                Model,
                PromptHash,
                ToolName,
                Args,
                PolicyDecision,
                Result,
                ResultDetail,
                DurationMs,
                WorldDiff,
                RollbackHandle,
                PrevHash,
                hash,
                SourceTag,
                Ts);
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
                seq,
                AuditEntryKind.ToolCall,
                traceId,
                actor,
                model,
                promptHash,
                toolName,
                args,
                policyDecision,
                result,
                resultDetail,
                durationMs);
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
                seq,
                AuditEntryKind.WorldMutation,
                traceId,
                actor,
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
                seq,
                AuditEntryKind.LlmRequest,
                traceId,
                actor,
                model,
                promptHash,
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
                seq,
                AuditEntryKind.LlmResponse,
                traceId,
                actor,
                model,
                promptHash,
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
                seq,
                AuditEntryKind.ChainReset,
                "",
                actor,
                policyDecision: "reset",
                result: "error",
                resultDetail: reason ?? "");
        }

        /// <summary>
        /// Marks the last entry of a file about to be rotated away. Chained normally from the
        /// current head — its resulting hash becomes the anchor embedded in the next file's
        /// <see cref="ForRotationAnchor"/> entry.
        /// </summary>
        public static AuditEntry ForRotationMarker(long seq, string actor, string prevHash)
        {
            return new AuditEntry(
                seq,
                AuditEntryKind.RotationMarker,
                "",
                actor,
                policyDecision: "rotated",
                result: "ok",
                prevHash: prevHash);
        }

        /// <summary>
        /// First entry of a file created by rotation. Its own <c>prevHash</c> field is the previous
        /// file's final hash (not the empty-string genesis), so the link between files is embedded
        /// directly in the chain rather than only in <paramref name="previousFileHash"/> metadata.
        /// </summary>
        public static AuditEntry ForRotationAnchor(long seq, string actor, string previousFileName,
            string previousFileHash)
        {
            return new AuditEntry(
                seq,
                AuditEntryKind.RotationAnchor,
                "",
                actor,
                toolName: previousFileName ?? "",
                args: JsonConvert.SerializeObject(new
                    { previousFile = previousFileName ?? "", previousHash = previousFileHash ?? "" }),
                policyDecision: "anchored",
                result: "ok",
                prevHash: previousFileHash);
        }

        /// <summary>
        /// Audits that the bounded writer queue dropped older entries because producers outran the
        /// flush loop. <paramref name="droppedCount"/> is the cumulative count since the writer started.
        /// </summary>
        public static AuditEntry ForQueueDropped(long seq, string actor, long droppedCount)
        {
            return new AuditEntry(
                seq,
                AuditEntryKind.QueueDropped,
                "",
                actor,
                policyDecision: "backpressure",
                result: "dropped",
                resultDetail: $"entries dropped: {droppedCount}");
        }
    }
}
