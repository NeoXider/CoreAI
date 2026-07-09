using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CoreAI.Audit
{
    public readonly struct AuditVerifyResult
    {
        public AuditVerifyResult(bool ok, long lineCount, long firstBrokenSeq, string error)
        {
            Ok = ok;
            LineCount = lineCount;
            FirstBrokenSeq = firstBrokenSeq;
            Error = error ?? "";
        }

        public bool Ok { get; }
        public long LineCount { get; }
        public long FirstBrokenSeq { get; }
        public string Error { get; }
    }

    /// <summary>
    /// Reads and verifies an audit.jsonl file written by AuditLogWriter.
    ///
    /// The canonical preimage of a stored line is that same line with the "hash" field set to
    /// the empty string (all other fields, including "ts" and "prevHash", unchanged). To verify,
    /// re-chain from genesis (prevHash=""): for each line, blank its "hash" field, recompute
    /// AuditHash.Chain(runningPrevHash, preimage) and compare against the stored "hash".
    /// </summary>
    public static class AuditLogVerifier
    {
        public static List<AuditEntry> ReadAll(string filePath)
        {
            List<AuditEntry> entries = new();
            if (!File.Exists(filePath))
            {
                return entries;
            }

            foreach (string line in File.ReadAllLines(filePath))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                JObject obj;
                try
                {
                    obj = ParseLine(line);
                }
                catch
                {
                    continue;
                }

                entries.Add(ToEntry(obj));
            }

            return entries;
        }

        public static AuditVerifyResult Verify(string filePath)
        {
            if (!File.Exists(filePath))
            {
                return new AuditVerifyResult(true, 0, -1, "");
            }

            string prevHash = "";
            long lineCount = 0;

            foreach (string rawLine in File.ReadAllLines(filePath))
            {
                if (string.IsNullOrWhiteSpace(rawLine))
                {
                    continue;
                }

                lineCount++;

                JObject obj;
                try
                {
                    obj = ParseLine(rawLine);
                }
                catch (Exception ex)
                {
                    return new AuditVerifyResult(false, lineCount, lineCount, $"line {lineCount}: unparsable ({ex.Message})");
                }

                long seq = obj["Seq"]?.Value<long>() ?? lineCount;
                string storedPrevHash = (string)obj["prevHash"] ?? "";
                string storedHash = (string)obj["hash"] ?? "";

                if (storedPrevHash != prevHash)
                {
                    return new AuditVerifyResult(false, lineCount, seq, $"seq {seq}: prevHash does not match chain head");
                }

                obj["hash"] = "";
                string preimage = obj.ToString(Formatting.None);
                string computedHash = AuditHash.Chain(prevHash, preimage);

                if (computedHash != storedHash)
                {
                    return new AuditVerifyResult(false, lineCount, seq, $"seq {seq}: hash does not match recomputed chain value");
                }

                prevHash = computedHash;
            }

            return new AuditVerifyResult(true, lineCount, -1, "");
        }

        private static JObject ParseLine(string line)
        {
            using StringReader stringReader = new(line);
            using JsonTextReader jsonReader = new(stringReader) { DateParseHandling = DateParseHandling.None };
            return JObject.Load(jsonReader);
        }

        private static AuditEntry ToEntry(JObject obj)
        {
            AuditEntryKind kind = (AuditEntryKind)(obj["Kind"]?.Value<int>() ?? 0);

            DateTime ts = default;
            string tsRaw = (string)obj["Ts"];
            if (!string.IsNullOrEmpty(tsRaw))
            {
                DateTime.TryParse(
                    tsRaw,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out ts);
            }

            return new AuditEntry(
                seq: obj["Seq"]?.Value<long>() ?? 0,
                kind: kind,
                traceId: (string)obj["TraceId"] ?? "",
                actor: (string)obj["Actor"] ?? "",
                model: (string)obj["Model"] ?? "",
                promptHash: (string)obj["PromptHash"] ?? "",
                toolName: (string)obj["ToolName"] ?? "",
                args: (string)obj["Args"] ?? "",
                policyDecision: (string)obj["PolicyDecision"] ?? "",
                result: (string)obj["Result"] ?? "",
                resultDetail: (string)obj["ResultDetail"] ?? "",
                durationMs: obj["DurationMs"]?.Value<double>() ?? 0,
                worldDiff: (string)obj["WorldDiff"] ?? "",
                rollbackHandle: (string)obj["RollbackHandle"] ?? "",
                prevHash: (string)obj["prevHash"] ?? "",
                hash: (string)obj["hash"] ?? "",
                sourceTag: (string)obj["SourceTag"] ?? "",
                ts: ts);
        }
    }
}
