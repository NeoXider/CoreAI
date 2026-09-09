using System.Collections.Generic;
using System.Text;
using CoreAI.Ai;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// The two accumulator shapes of the ONE message-boundary rule must never disagree.
    /// <para>
    /// This matters beyond tidiness: the chat panel accumulates a streamed turn with the builder
    /// overload (the string overload copied the whole answer on every token, which is quadratic in the
    /// answer length on WebGL's single thread), while other consumers still accumulate with the string
    /// overload. If the two drifted, the same turn would be separated one way on screen and another way
    /// in the history a reader sees next time — the exact defect the joiner was created to end.
    /// </para>
    /// </summary>
    public sealed class StreamedMessageJoinerEditModeTests
    {
        private static readonly string[][] Turns =
        {
            new[] { "hello" },
            new[] { "check yourself:", "**Turn finished.**" },
            new[] { "a", "b", "c", "d" },
            new[] { "", "text after an empty chunk" },
            new[] { "ends with a paragraph\n\n", "next reply" },
            new[] { "ends with one newline\n", "next reply" },
            new[] { "trailing spaces   \n  ", "next reply" },
            new[] { "   ", "reply after whitespace-only accumulator" },
            new[] { "first", "", "third" }
        };

        /// <summary>
        /// Same fragments, same boundary flags, same result — for every position of the boundary,
        /// including "boundary on the very first chunk" and "boundary after an empty accumulator".
        /// </summary>
        [Test]
        public void BuilderOverload_MatchesStringOverload_ForEveryBoundaryPosition()
        {
            foreach (string[] fragments in Turns)
            {
                for (int boundary = 0; boundary < fragments.Length; boundary++)
                {
                    string byString = "";
                    StringBuilder byBuilder = new();
                    LlmStreamChunk scratch = new();

                    for (int i = 0; i < fragments.Length; i++)
                    {
                        bool startsNewMessage = i == boundary;
                        byString = StreamedMessageJoiner.Append(byString, fragments[i], startsNewMessage);

                        scratch.Text = fragments[i];
                        scratch.StartsNewMessage = startsNewMessage;
                        StreamedMessageJoiner.Append(byBuilder, scratch);
                    }

                    Assert.AreEqual(byString, byBuilder.ToString(),
                        $"fragments [{string.Join("|", fragments)}], boundary at {boundary}");
                }
            }
        }

        /// <summary>No boundary anywhere: both accumulators are plain concatenation.</summary>
        [Test]
        public void BuilderOverload_MatchesStringOverload_WithoutAnyBoundary()
        {
            foreach (string[] fragments in Turns)
            {
                string byString = "";
                StringBuilder byBuilder = new();
                LlmStreamChunk scratch = new();

                foreach (string fragment in fragments)
                {
                    byString = StreamedMessageJoiner.Append(byString, fragment, false);
                    scratch.Text = fragment;
                    scratch.StartsNewMessage = false;
                    StreamedMessageJoiner.Append(byBuilder, scratch);
                }

                Assert.AreEqual(byString, byBuilder.ToString(), string.Join("|", fragments));
            }
        }

        /// <summary>
        /// Reusing ONE chunk instance across a whole turn (what the panel does, so the fix does not
        /// trade an O(n) copy per token for an allocation per token) must not change the result.
        /// </summary>
        [Test]
        public void ReusedScratchChunk_ProducesTheSameTextAsFreshChunks()
        {
            List<string> fragments = new() { "one", "two", "three", "four" };

            StringBuilder reused = new();
            LlmStreamChunk scratch = new();
            StringBuilder fresh = new();

            for (int i = 0; i < fragments.Count; i++)
            {
                bool startsNewMessage = i == 2;

                scratch.Text = fragments[i];
                scratch.StartsNewMessage = startsNewMessage;
                StreamedMessageJoiner.Append(reused, scratch);

                StreamedMessageJoiner.Append(fresh, new LlmStreamChunk
                {
                    Text = fragments[i],
                    StartsNewMessage = startsNewMessage
                });
            }

            Assert.AreEqual(fresh.ToString(), reused.ToString());
            Assert.AreEqual("onetwo\n\nthreefour", reused.ToString());
        }
    }
}
