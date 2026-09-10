using System.Text;

namespace CoreAI.Ai
{
    /// <summary>
    /// Assembles the text of one stream into a coherent answer, separating DIFFERENT assistant replies
    /// with a blank line.
    /// <para>
    /// A single stream carries several replies: after every tool round the model speaks anew, yet what
    /// leaves the client is one continuous run of chunks. Until
    /// <see cref="LlmStreamChunk.StartsNewMessage"/> existed in the contract, accumulators glued the end of
    /// one reply directly onto the start of another, and in production the learner read
    /// "...Check yourself:<b>Turn complete - waiting for the learner's answer on the card.</b>" - a colon
    /// flush against a capital letter.
    /// </para>
    /// <para>
    /// <b>The rule lives here and only here.</b> Three accumulators need it at once - the orchestrator
    /// (chat history and <c>ApplyAiGameCommand</c>), the chat panel (the full answer text) and consumers
    /// outside CoreAI. When each carried its own copy, the copies diverged within a single day: two
    /// treated an accumulator made of nothing but spaces as "already separated", the third appended a
    /// blank line to it. A divergence in a rule like this does not fail a test, it quietly changes what
    /// the child reads.
    /// </para>
    /// <para>
    /// The boundary comes ONLY from the marker on the chunk. Guessing it from punctuation is not an
    /// option: a reply may legitimately end with a colon and legitimately begin with a lowercase letter,
    /// so any heuristic errs in both directions - and makes the defect impossible to reproduce.
    /// </para>
    /// </summary>
    public static class StreamedMessageJoiner
    {
        /// <summary>Replies are separated by a blank line: that is a markdown paragraph, not just a break.</summary>
        public const int SeparatorNewlines = 2;

        /// <summary>
        /// Appends the next piece of text to a string accumulator. Without the boundary marker it behaves
        /// exactly like the previous concatenation, so dropping it into any old call site is safe.
        /// </summary>
        public static string Append(string accumulated, string text, bool startsNewMessage)
        {
            if (string.IsNullOrEmpty(text))
            {
                return accumulated ?? string.Empty;
            }

            if (string.IsNullOrEmpty(accumulated))
            {
                return text;
            }

            return startsNewMessage
                ? accumulated + SeparatorFor(accumulated) + text
                : accumulated + text;
        }

        /// <summary>The same contract for a StringBuilder accumulator.</summary>
        public static void Append(StringBuilder accumulated, LlmStreamChunk chunk)
        {
            if (accumulated == null || chunk == null || string.IsNullOrEmpty(chunk.Text))
            {
                return;
            }

            if (chunk.StartsNewMessage && accumulated.Length > 0)
            {
                // Snapshotting the string here is not wasteful: a boundary happens once per tool round,
                // not per chunk. The cost is one copy of the turn's tail; the gain is that the separation
                // rule is not duplicated once more for StringBuilder and cannot drift apart.
                accumulated.Append(SeparatorFor(accumulated.ToString()));
            }

            accumulated.Append(chunk.Text);
        }

        /// <summary>
        /// What is still missing for a blank line. Exactly the shortfall is appended: a reply that already
        /// ended with a paragraph break does not get a third blank line.
        /// </summary>
        private static string SeparatorFor(string accumulated) =>
            new('\n', SeparatorNewlines - TrailingNewlines(accumulated));

        /// <summary>
        /// How many newlines already stand at the tail; spaces, tabs and <c>\r</c> do not break the tail.
        /// An accumulator with no meaningful text counts as "already separated" - a separator before the
        /// first reply would add nothing but emptiness.
        /// </summary>
        private static int TrailingNewlines(string accumulated)
        {
            int newlines = 0;
            for (int i = accumulated.Length - 1; i >= 0; i--)
            {
                char symbol = accumulated[i];
                if (symbol == '\n')
                {
                    newlines++;
                    if (newlines >= SeparatorNewlines)
                    {
                        return newlines;
                    }

                    continue;
                }

                if (symbol == '\r' || symbol == ' ' || symbol == '\t')
                {
                    continue;
                }

                return newlines;
            }

            return SeparatorNewlines;
        }
    }
}
