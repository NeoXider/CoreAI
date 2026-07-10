using System.Collections.Generic;
using System.Text;

namespace CoreAI.Ai
{
    /// <summary>
    /// Shared revision-history retention policy used by every <see cref="ILuaScriptVersionStore"/> /
    /// <see cref="IDataOverlayVersionStore"/> implementation. Keeps history bounded so long sessions
    /// cannot grow a per-key revision list (and the file stores' serialized JSON) without limit.
    /// </summary>
    public static class VersionRetentionPolicy
    {
        /// <summary>Default number of intermediate revisions kept between the original and the current one.</summary>
        public const int DefaultMaxIntermediateRevisions = 20;

        /// <summary>Default total payload byte budget per key (UTF-8 bytes summed across kept revisions).</summary>
        public const long DefaultMaxTotalBytes = 2 * 1024 * 1024;

        /// <summary>
        /// Trims <paramref name="history"/> in place to at most <c>original + last N intermediate + current</c>
        /// entries, then evicts the oldest remaining intermediate entries until the total payload size is
        /// within <paramref name="maxTotalBytes"/>. Assumes entries are sorted ascending by
        /// <see cref="LuaScriptRevision.Index"/> (stable sequence numbers are never reassigned; eviction only
        /// removes entries, so gaps in <see cref="LuaScriptRevision.Index"/> are expected after this runs).
        /// The first entry (original) and the last entry (current) are never evicted, even if the current
        /// revision alone exceeds the byte budget.
        /// </summary>
        public static void Enforce(
            List<LuaScriptRevision> history,
            int maxIntermediateRevisions = DefaultMaxIntermediateRevisions,
            long maxTotalBytes = DefaultMaxTotalBytes)
        {
            if (history == null || history.Count <= 2)
            {
                return;
            }

            int middleCount = history.Count - 2;
            if (middleCount > maxIntermediateRevisions)
            {
                int removeCount = middleCount - maxIntermediateRevisions;
                history.RemoveRange(1, removeCount);
            }

            long total = TotalBytes(history);
            int i = 1;
            while (total > maxTotalBytes && history.Count > 2 && i < history.Count - 1)
            {
                total -= ByteSize(history[i].Source);
                history.RemoveAt(i);
            }
        }

        private static long TotalBytes(List<LuaScriptRevision> history)
        {
            long total = 0;
            for (int i = 0; i < history.Count; i++)
            {
                total += ByteSize(history[i].Source);
            }

            return total;
        }

        private static long ByteSize(string s)
        {
            return string.IsNullOrEmpty(s) ? 0 : Encoding.UTF8.GetByteCount(s);
        }
    }
}
