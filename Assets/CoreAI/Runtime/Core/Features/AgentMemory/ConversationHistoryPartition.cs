using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace CoreAI.Ai
{
    /// <summary>
    /// Shared history token budget resolution (orchestrator-aligned with <see cref="ConversationContextBuildArgs.HistoryTokenBudget"/>).
    /// </summary>
    internal static class ConversationContextBudgetTokens
    {
        internal static int ResolveHistoryChatBudget(
            AgentMemoryPolicy.RoleMemoryConfig roleConfig,
            ConversationContextBuildArgs buildArgs)
        {
            if (buildArgs != null && buildArgs.HistoryTokenBudget > 0)
            {
                return Math.Max(1, buildArgs.HistoryTokenBudget);
            }

            int maxTokens = roleConfig.ContextTokens > 0
                ? roleConfig.ContextTokens
                : CoreAISettings.DefaultContextWindowTokens;
            return Math.Max(1, maxTokens / 2);
        }

        internal static int EstimateHistoryTokens(ChatMessage[] history, ITokenEstimator estimator)
        {
            if (history == null || history.Length == 0)
            {
                return 0;
            }

            long total = 0;
            for (int i = 0; i < history.Length; i++)
            {
                total += Math.Max(0, estimator.EstimateText(history[i].Content ?? ""));
                if (total >= int.MaxValue)
                {
                    return int.MaxValue;
                }
            }

            return (int)total;
        }

        internal static float ResolveCompactionTriggerRatio(ConversationContextBuildArgs buildArgs)
        {
            float ratio = buildArgs?.CompactionTriggerRatio ?? 0f;
            if (ratio <= 0f || ratio > 1f || float.IsNaN(ratio) || float.IsInfinity(ratio))
            {
                return CoreAISettings.DefaultConversationCompactionTriggerRatio;
            }

            return ratio;
        }

        internal static bool ShouldPartitionForCompaction(
            ChatMessage[] history,
            ITokenEstimator estimator,
            int historyBudget,
            ConversationContextBuildArgs buildArgs)
        {
            int totalHistoryTokens = EstimateHistoryTokens(history, estimator);
            double triggerTokens = historyBudget * (double)ResolveCompactionTriggerRatio(buildArgs);
            return totalHistoryTokens >= triggerTokens;
        }
    }

    /// <summary>
    /// Keeps the newest dialogue tail within a heuristic token budget; older prefix is summarized separately.
    /// </summary>
    internal static class ConversationHistoryPartition
    {
        /// <summary>
        /// Returns the exclusive index at which verbatim tail starts (<c>history[splitExclusive..]</c> kept).
        /// </summary>
        public static (int splitExclusive, List<ChatMessage> recentTail) PartitionByBudget(
            ChatMessage[] history,
            ITokenEstimator estimator,
            int budgetTokens)
        {
            List<ChatMessage> recent = new();
            int splitExclusive = history.Length;

            int budgetRemaining = budgetTokens;
            for (int i = history.Length - 1; i >= 0; i--)
            {
                int estimatedTokens = estimator.EstimateText(history[i].Content);
                if (budgetRemaining - estimatedTokens < 0 && recent.Count > 0)
                {
                    splitExclusive = i + 1;
                    break;
                }

                budgetRemaining -= estimatedTokens;
                recent.Insert(0, history[i]);
                splitExclusive = i;
            }

            return (splitExclusive, recent);
        }
    }

    internal static class ConversationBulletSummary
    {
        public static string Format(
            string existingSummary,
            ChatMessage[] history,
            int splitExclusive,
            int startInclusive = 0)
        {
            if (history == null || splitExclusive <= startInclusive)
            {
                return existingSummary?.Trim() ?? "";
            }

            StringBuilder sb = new();
            if (!string.IsNullOrWhiteSpace(existingSummary))
            {
                sb.AppendLine(existingSummary.Trim());
            }
            else
            {
                sb.AppendLine("Previous conversation summary:");
            }

            for (int i = Math.Max(0, startInclusive); i < splitExclusive; i++)
            {
                // WHY: A whitespace-only message would emit a bare "- role: " bullet; besides being noise,
                // such a bullet as the persisted watermark line can never be re-matched by FindFoldStart
                // (blank contents are skipped there), which would refold the whole prefix every turn.
                if (string.IsNullOrWhiteSpace(history[i].Content))
                {
                    continue;
                }

                sb.AppendLine(FormatMessage(history[i]));
            }

            return sb.ToString().Trim();
        }

        /// <summary>
        /// Re-detects the already-folded prefix of <paramref name="history"/> from the persisted summary.
        /// Preferred path: the structured fold marker stamped by <see cref="ConversationFoldMarker.Stamp"/>
        /// (content hashes of the last folded messages; survives pruning/trimming of some of them).
        /// Legacy fallbacks for summaries persisted before the marker existed: whole-final-line watermark
        /// bullet match (wave 3), then whole-bullet substring match (wave 2). At most one re-fold can happen
        /// for legacy formats because the very next save writes a marker.
        /// </summary>
        public static int FindFoldStart(string existingSummary, ChatMessage[] history, int splitExclusive)
        {
            return FindFoldStart(existingSummary, history, splitExclusive, out _);
        }

        /// <summary>
        /// Same as <see cref="FindFoldStart(string,ChatMessage[],int)"/> but reports how the fold point was
        /// detected so callers can log the degraded fold-from-0 case (<see cref="ConversationFoldProbeResult.NoMatch"/>).
        /// </summary>
        public static int FindFoldStart(
            string existingSummary,
            ChatMessage[] history,
            int splitExclusive,
            out ConversationFoldProbeResult probe)
        {
            probe = ConversationFoldProbeResult.NoSummary;
            if (string.IsNullOrWhiteSpace(existingSummary) || history == null || splitExclusive <= 0)
            {
                return 0;
            }

            if (ConversationFoldMarker.TryParse(existingSummary, out HashSet<string> markerHashes))
            {
                // WHY: A marker was written by this code and is authoritative; falling through to bullet
                // matching here would reintroduce duplicate-text false positives the marker exists to fix.
                int markerFold = FindFoldStartFromMarker(markerHashes, history, splitExclusive, out bool anyHashMatched);
                probe = anyHashMatched ? ConversationFoldProbeResult.Marker : ConversationFoldProbeResult.NoMatch;
                return markerFold;
            }

            int finalLineFold = FindFoldStartByFinalLine(existingSummary, history, splitExclusive, out bool finalLineMatched);
            if (finalLineMatched)
            {
                probe = ConversationFoldProbeResult.LegacyFinalLine;
                return finalLineFold;
            }

            int substringFold = FindFoldStartBySubstring(existingSummary, history, splitExclusive, out bool substringMatched);
            if (substringMatched)
            {
                probe = ConversationFoldProbeResult.LegacySubstring;
                return substringFold;
            }

            probe = ConversationFoldProbeResult.NoMatch;
            return 0;
        }

        /// <summary>
        /// Marker probe: a message is folded when its content hash appears in the marker. Each hash may be
        /// consumed once, at its OLDEST occurrence in history; the fold point is one past the newest consumed
        /// index. Oldest-occurrence consumption keeps a live message that repeats folded text verbatim from
        /// pulling the fold point forward and silently dropping the messages in between (F17); the worst case
        /// with pruned watermark messages is a bounded re-summarize of a few already-folded lines, which the
        /// LLM merge dedupes.
        /// </summary>
        private static int FindFoldStartFromMarker(
            HashSet<string> markerHashes,
            ChatMessage[] history,
            int splitExclusive,
            out bool anyHashMatched)
        {
            anyHashMatched = false;
            int foldStart = 0;
            HashSet<string> consumed = new(StringComparer.Ordinal);
            int limit = Math.Min(splitExclusive, history.Length);
            for (int i = 0; i < limit; i++)
            {
                string hash = ConversationFoldMarker.HashMessage(history[i]);
                if (markerHashes.Contains(hash) && consumed.Add(hash))
                {
                    anyHashMatched = true;
                    foldStart = i + 1;
                }
            }

            if (!anyHashMatched)
            {
                return 0;
            }

            // WHY: Duplicate content pins each hash to its OLDEST occurrence, so a repeated message inside
            // the folded prefix (e.g. consecutive whitespace turns) would sit just past foldStart and get
            // re-folded every turn (non-convergent). Skipping forward over messages whose hash is in the
            // marker is safe: their exact role+content is provably already summarized.
            while (foldStart < limit &&
                   markerHashes.Contains(ConversationFoldMarker.HashMessage(history[foldStart])))
            {
                foldStart++;
            }

            return foldStart;
        }

        /// <summary>
        /// Wave-3 legacy probe: the watermark bullet of the last folded non-empty message was stamped as the
        /// final line of the persisted summary, so only a whole-final-line match counts. Substring matches
        /// (blank "- user: " bullets, prefix subsumption like "hel" inside "hello", duplicate messages
        /// matching old mid-summary bullets) are rejected by design.
        /// </summary>
        private static int FindFoldStartByFinalLine(
            string existingSummary,
            ChatMessage[] history,
            int splitExclusive,
            out bool matched)
        {
            matched = false;
            int foldStart = Math.Min(splitExclusive, history.Length);
            for (int i = foldStart - 1; i >= 0; i--)
            {
                // WHY: Blank contents format to "- role: ", a substring of every same-role bullet; they can
                // never be a reliable watermark. Skipping them also keeps whitespace-only tails between the
                // watermark and the live tail from triggering pointless refold passes (folding them is a no-op).
                if (string.IsNullOrWhiteSpace(history[i].Content))
                {
                    continue;
                }

                if (IsFinalLine(existingSummary, FormatMessage(history[i])))
                {
                    matched = true;
                    return foldStart;
                }

                foldStart = i;
            }

            return 0;
        }

        /// <summary>
        /// Wave-2 legacy probe (last resort): summaries persisted by the oldest code could end with a blank
        /// "- user: " bullet, so the final-line probe never matches them (F14); a whole-bullet substring match
        /// against the newest folded non-empty message recognizes them without a full re-summarize. Only used
        /// when no marker exists, so its duplicate-text weakness is a one-shot migration risk at worst.
        /// </summary>
        private static int FindFoldStartBySubstring(
            string existingSummary,
            ChatMessage[] history,
            int splitExclusive,
            out bool matched)
        {
            matched = false;
            for (int i = Math.Min(splitExclusive, history.Length) - 1; i >= 0; i--)
            {
                if (string.IsNullOrWhiteSpace(history[i].Content))
                {
                    continue;
                }

                if (existingSummary.IndexOf(FormatMessage(history[i]), StringComparison.Ordinal) >= 0)
                {
                    matched = true;
                    return i + 1;
                }
            }

            return 0;
        }

        private static bool IsFinalLine(string summary, string bullet)
        {
            if (bullet.Length == 0 || !summary.EndsWith(bullet, StringComparison.Ordinal))
            {
                return false;
            }

            int lineStart = summary.Length - bullet.Length;
            return lineStart == 0 || summary[lineStart - 1] == '\n';
        }

        private static string FormatMessage(ChatMessage message)
        {
            string role = string.IsNullOrWhiteSpace(message.Role) ? "unknown" : message.Role.Trim();
            string content = message.Content ?? "";
            if (content.Length > 280)
            {
                content = content.Substring(0, 280).TrimEnd() + "...";
            }

            return "- " + role + ": " + content;
        }
    }

    /// <summary>
    /// How <see cref="ConversationBulletSummary.FindFoldStart(string,ChatMessage[],int,out ConversationFoldProbeResult)"/>
    /// detected (or failed to detect) the already-folded prefix.
    /// </summary>
    internal enum ConversationFoldProbeResult
    {
        /// <summary>No stored summary (or empty history/split); nothing to probe.</summary>
        NoSummary,

        /// <summary>Structured fold marker matched by content hash.</summary>
        Marker,

        /// <summary>Legacy wave-3 whole-final-line watermark bullet match.</summary>
        LegacyFinalLine,

        /// <summary>Legacy wave-2 whole-bullet substring match.</summary>
        LegacySubstring,

        /// <summary>Summary exists but no fold point was recognized; caller folds from 0 and should warn.</summary>
        NoMatch
    }

    /// <summary>
    /// Explicit fold watermark persisted as the final line of the stored rolling summary:
    /// <c>[fold:v1:&lt;hash&gt;,&lt;hash&gt;,...]</c> with 12-hex SHA-256 content hashes (trimmed role + trimmed
    /// content) of the last <see cref="StoredHashCount"/> folded messages, newest first. The marker is never
    /// shown to the LLM or exposed on snapshots; it exists only so the fold point can be re-detected without
    /// inferring from bullet prose, and it survives pruning/trimming of some watermark messages because any
    /// surviving hash still anchors the fold. Whitespace-only messages hash like any other, so a fold that
    /// folds only whitespace still advances the persisted state (F16 convergence).
    /// </summary>
    internal static class ConversationFoldMarker
    {
        /// <summary>Number of newest folded messages whose hashes are stored in the marker.</summary>
        public const int StoredHashCount = 8;

        private const string MarkerPrefix = "[fold:v1:";
        private const string MarkerSuffix = "]";
        private const int HashHexLength = 12;

        /// <summary>
        /// Returns <paramref name="cleanSummary"/> with the fold marker for
        /// <c>history[0..splitExclusive)</c> appended as its final line. Apply any summary limiter BEFORE
        /// stamping so the marker can never be trimmed away.
        /// </summary>
        public static string Stamp(string cleanSummary, ChatMessage[] history, int splitExclusive)
        {
            string clean = (cleanSummary ?? "").Trim();
            string marker = Build(history, splitExclusive);
            if (marker.Length == 0)
            {
                return clean;
            }

            return clean.Length == 0 ? marker : clean + "\n" + marker;
        }

        /// <summary>Builds the marker line for <c>history[0..splitExclusive)</c>, or "" when there is nothing folded.</summary>
        public static string Build(ChatMessage[] history, int splitExclusive)
        {
            if (history == null || splitExclusive <= 0)
            {
                return "";
            }

            int end = Math.Min(splitExclusive, history.Length);
            if (end <= 0)
            {
                return "";
            }

            List<string> hashes = new(StoredHashCount);
            HashSet<string> seen = new(StringComparer.Ordinal);
            for (int i = end - 1; i >= 0 && hashes.Count < StoredHashCount; i--)
            {
                string hash = HashMessage(history[i]);
                if (seen.Add(hash))
                {
                    hashes.Add(hash);
                }
            }

            if (hashes.Count == 0)
            {
                return "";
            }

            return MarkerPrefix + string.Join(",", hashes) + MarkerSuffix;
        }

        /// <summary>
        /// Removes every fold-marker line from <paramref name="summary"/> and trims the result; this is the
        /// clean prose handed to the LLM and exposed as <see cref="ConversationContextSnapshot.Summary"/>.
        /// </summary>
        public static string Strip(string summary)
        {
            if (string.IsNullOrEmpty(summary))
            {
                return "";
            }

            if (summary.IndexOf(MarkerPrefix, StringComparison.Ordinal) < 0)
            {
                return summary.Trim();
            }

            // WHY: Splitting on '\n' only (keeping any trailing '\r' on each line) preserves the summary's
            // original CRLF/LF line endings byte-for-byte; IsMarkerLine trims, so it matches either ending.
            string[] lines = summary.Split('\n');
            StringBuilder sb = new(summary.Length);
            bool first = true;
            for (int i = 0; i < lines.Length; i++)
            {
                if (IsMarkerLine(lines[i]))
                {
                    continue;
                }

                if (!first)
                {
                    sb.Append('\n');
                }

                sb.Append(lines[i]);
                first = false;
            }

            return sb.ToString().Trim();
        }

        /// <summary>
        /// Parses the marker from the final line of <paramref name="summary"/> into its hash set.
        /// </summary>
        public static bool TryParse(string summary, out HashSet<string> hashes)
        {
            hashes = null;
            if (string.IsNullOrEmpty(summary))
            {
                return false;
            }

            string trimmed = summary.TrimEnd();
            int lastNewline = trimmed.LastIndexOf('\n');
            string lastLine = (lastNewline >= 0 ? trimmed.Substring(lastNewline + 1) : trimmed).Trim();
            if (!IsMarkerLine(lastLine))
            {
                return false;
            }

            string payload = lastLine.Substring(
                MarkerPrefix.Length,
                lastLine.Length - MarkerPrefix.Length - MarkerSuffix.Length);
            string[] parts = payload.Split(',');
            HashSet<string> parsed = new(StringComparer.Ordinal);
            for (int i = 0; i < parts.Length; i++)
            {
                if (!IsHexHash(parts[i]))
                {
                    return false;
                }

                parsed.Add(parts[i]);
            }

            if (parsed.Count == 0)
            {
                return false;
            }

            hashes = parsed;
            return true;
        }

        /// <summary>
        /// 12-hex SHA-256 over trimmed role + '\n' + trimmed content. Role is included so equal text from
        /// different speakers does not collide; content is trimmed to match the pruner's duplicate semantics.
        /// </summary>
        public static string HashMessage(ChatMessage message)
        {
            string role = (message.Role ?? "").Trim();
            string content = (message.Content ?? "").Trim();
            byte[] bytes = Encoding.UTF8.GetBytes(role + "\n" + content);
            using (SHA256 sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(bytes);
                StringBuilder sb = new(HashHexLength);
                for (int i = 0; i < HashHexLength / 2; i++)
                {
                    sb.Append(digest[i].ToString("x2"));
                }

                return sb.ToString();
            }
        }

        private static bool IsMarkerLine(string line)
        {
            string t = line.Trim();
            return t.StartsWith(MarkerPrefix, StringComparison.Ordinal) &&
                   t.EndsWith(MarkerSuffix, StringComparison.Ordinal) &&
                   t.Length > MarkerPrefix.Length + MarkerSuffix.Length;
        }

        private static bool IsHexHash(string value)
        {
            if (value == null || value.Length != HashHexLength)
            {
                return false;
            }

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
                if (!isHex)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
