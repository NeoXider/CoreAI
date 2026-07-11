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
            bool first = true;

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
                    return new AuditVerifyResult(false, lineCount, lineCount,
                        $"line {lineCount}: unparsable ({ex.Message})");
                }

                long seq = obj["Seq"]?.Value<long>() ?? lineCount;
                string storedPrevHash = (string)obj["prevHash"] ?? "";
                string storedHash = (string)obj["hash"] ?? "";

                // Anchored genesis: a file created by rotation starts with a RotationAnchor entry
                // whose own prevHash is the previous file's final hash, not "". Trust that stored
                // value as the seed for THIS file's chain so it still verifies standalone (the
                // cross-file link is embedded on disk rather than required at verify time).
                if (first && (int?)obj["Kind"] == (int)AuditEntryKind.RotationAnchor)
                {
                    prevHash = storedPrevHash;
                }

                first = false;

                if (storedPrevHash != prevHash)
                {
                    return new AuditVerifyResult(false, lineCount, seq,
                        $"seq {seq}: prevHash does not match chain head");
                }

                obj["hash"] = "";
                string preimage = obj.ToString(Formatting.None);
                string computedHash = AuditHash.Chain(prevHash, preimage);

                if (computedHash != storedHash)
                {
                    return new AuditVerifyResult(false, lineCount, seq,
                        $"seq {seq}: hash does not match recomputed chain value");
                }

                prevHash = computedHash;
            }

            return new AuditVerifyResult(true, lineCount, -1, "");
        }

        /// <summary>
        /// Verifies a rotated set of audit files in chronological order: each file must verify
        /// standalone (<see cref="Verify"/>), and each file after the first must open with a
        /// <see cref="AuditEntryKind.RotationAnchor"/> whose embedded <c>prevHash</c> equals the
        /// previous file's final line hash — i.e. the set is linked, not just individually valid.
        /// </summary>
        public static AuditVerifyResult VerifyChainedSet(IReadOnlyList<string> filePathsInChronologicalOrder)
        {
            string expectedAnchorHash = null;
            long totalLines = 0;

            for (int i = 0; i < filePathsInChronologicalOrder.Count; i++)
            {
                string path = filePathsInChronologicalOrder[i];
                AuditVerifyResult standalone = Verify(path);
                if (!standalone.Ok)
                {
                    return new AuditVerifyResult(false, totalLines + standalone.LineCount, standalone.FirstBrokenSeq,
                        $"{Path.GetFileName(path)}: {standalone.Error}");
                }

                List<AuditEntry> entries = ReadAll(path);

                if (i > 0)
                {
                    bool linked = entries.Count > 0
                                  && entries[0].Kind == AuditEntryKind.RotationAnchor
                                  && entries[0].PrevHash == expectedAnchorHash;

                    if (!linked)
                    {
                        return new AuditVerifyResult(false, totalLines + standalone.LineCount,
                            entries.Count > 0 ? entries[0].Seq : -1,
                            $"{Path.GetFileName(path)}: rotation anchor does not link to previous file's final hash");
                    }
                }

                totalLines += standalone.LineCount;
                expectedAnchorHash = entries.Count > 0 ? entries[entries.Count - 1].Hash : "";
            }

            return new AuditVerifyResult(true, totalLines, -1, "");
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
                obj["Seq"]?.Value<long>() ?? 0,
                kind,
                (string)obj["TraceId"] ?? "",
                (string)obj["Actor"] ?? "",
                (string)obj["Model"] ?? "",
                (string)obj["PromptHash"] ?? "",
                (string)obj["ToolName"] ?? "",
                (string)obj["Args"] ?? "",
                (string)obj["PolicyDecision"] ?? "",
                (string)obj["Result"] ?? "",
                (string)obj["ResultDetail"] ?? "",
                obj["DurationMs"]?.Value<double>() ?? 0,
                (string)obj["WorldDiff"] ?? "",
                (string)obj["RollbackHandle"] ?? "",
                (string)obj["prevHash"] ?? "",
                (string)obj["hash"] ?? "",
                (string)obj["SourceTag"] ?? "",
                ts);
        }
    }
}
