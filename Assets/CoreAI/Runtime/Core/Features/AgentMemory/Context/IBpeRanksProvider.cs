using System.IO;

namespace CoreAI.Ai
{
    /// <summary>
    /// Supplies the raw token-rank data file for a <see cref="BpeEncoding"/>. The portable core has
    /// no Unity/engine references, so it cannot call <c>Resources.Load</c> directly; the host adapts
    /// its asset system to a <see cref="Stream"/> here.
    /// </summary>
    /// <remarks>
    /// <para>Expected file format is the standard tiktoken BPE rank format: one entry per line,
    /// "&lt;base64-of-token-bytes&gt; &lt;integer-rank&gt;", e.g.</para>
    /// <code>
    /// IQ== 0
    /// Ig== 1
    /// Iw== 2
    /// </code>
    /// <para>The base64 decodes to the raw UTF-8 bytes of a BPE token piece; the integer is its
    /// merge rank. The file is large (~100k lines) and ships as-is from tiktoken
    /// (cl100k_base.tiktoken / o200k_base.tiktoken). See <see cref="BpeTokenCounter"/> for the exact
    /// resource path the maintainer must populate.</para>
    /// </remarks>
    public interface IBpeRanksProvider
    {
        /// <summary>
        /// Opens a readable stream of the rank file for the encoding, or returns null when the data
        /// is not available on this platform/host. Implementations MUST NOT throw; a null return is
        /// the signal to fall back to the heuristic estimator.
        /// </summary>
        Stream OpenRanks(BpeEncoding encoding);
    }

    /// <summary>
    /// Provider that never has data; forces <see cref="BpeTokenCounter"/> to use its estimator
    /// fallback. Useful default for hosts that have not shipped the tiktoken data files.
    /// </summary>
    public sealed class NullBpeRanksProvider : IBpeRanksProvider
    {
        /// <summary>Shared stateless instance.</summary>
        public static readonly NullBpeRanksProvider Instance = new();

        private NullBpeRanksProvider()
        {
        }

        /// <inheritdoc />
        public Stream OpenRanks(BpeEncoding encoding)
        {
            return null;
        }
    }
}