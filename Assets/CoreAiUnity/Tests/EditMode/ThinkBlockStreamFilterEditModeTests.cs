using System.Text;
using CoreAI.Ai;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode coverage for <see cref="ThinkBlockStreamFilter"/> streaming filtering:
    /// split tags, multiple blocks, flush/reset behavior, case-insensitivity, and edge cases.
    /// <para>
    /// Some of the tests are phrased from the point of view of a learner reading a Python teacher's answer in
    /// Russian: in such an answer <c>&lt;</c> is a comparison operator, not the start of a tag, and must not be lost.
    /// </para>
    /// </summary>
    [TestFixture]
    public sealed class ThinkBlockStreamFilterEditModeTests
    {
        private static string FeedChunks(ThinkBlockStreamFilter filter, params string[] chunks)
        {
            StringBuilder sb = new();
            foreach (string c in chunks)
            {
                sb.Append(filter.ProcessChunk(c));
            }

            sb.Append(filter.Flush());
            return sb.ToString();
        }

        // ===================== Basic pass-through =====================

        [Test]
        public void ProcessChunk_PlainText_ReturnsAsIs()
        {
            ThinkBlockStreamFilter filter = new();
            Assert.AreEqual("Hello world", filter.ProcessChunk("Hello world"));
            Assert.AreEqual(string.Empty, filter.Flush());
        }

        [Test]
        public void ProcessChunk_NullOrEmpty_ReturnsEmptyString()
        {
            ThinkBlockStreamFilter filter = new();
            Assert.AreEqual(string.Empty, filter.ProcessChunk(null));
            Assert.AreEqual(string.Empty, filter.ProcessChunk(""));
            Assert.AreEqual(string.Empty, filter.Flush());
        }

        [Test]
        public void ProcessChunk_MultipleChunks_ConcatenatedCorrectly()
        {
            ThinkBlockStreamFilter filter = new();
            string result = FeedChunks(filter, "Hel", "lo ", "world");
            Assert.AreEqual("Hello world", result);
        }

        // ===================== Whole think block in one chunk =====================

        [Test]
        public void ProcessChunk_ThinkBlockInSingleChunk_Stripped()
        {
            ThinkBlockStreamFilter filter = new();
            string result = filter.ProcessChunk("<think>secret</think>Answer: 42");
            Assert.AreEqual("Answer: 42", result);
        }

        [Test]
        public void ProcessChunk_ThinkBlockInMiddle_TextAroundPreserved()
        {
            ThinkBlockStreamFilter filter = new();
            string result = filter.ProcessChunk("Hi <think>planning</think>there!");
            Assert.AreEqual("Hi there!", result);
        }

        [Test]
        public void ProcessChunk_MultipleThinkBlocksInOneChunk_AllStripped()
        {
            ThinkBlockStreamFilter filter = new();
            string result = filter.ProcessChunk("<think>a</think>Hello <think>b</think>World");
            Assert.AreEqual("Hello World", result);
        }

        [Test]
        public void ProcessChunk_ThinkBlockInEachChunk_EachStripped()
        {
            // A block in every chunk: the "inside a block" state must close correctly between calls.
            ThinkBlockStreamFilter filter = new();
            string r1 = filter.ProcessChunk("<think>a</think>Hello ");
            string r2 = filter.ProcessChunk("<think>b</think>World");

            Assert.AreEqual("Hello ", r1);
            Assert.AreEqual("World", r2);
        }

        // ===================== Split tags across chunks =====================

        [Test]
        public void ProcessChunk_OpenTagSplitAcrossChunks_BufferedAndStripped()
        {
            ThinkBlockStreamFilter filter = new();
            string result = FeedChunks(filter,
                "Prefix <th",
                "ink>hidden</think>",
                "Suffix");
            Assert.AreEqual("Prefix Suffix", result);
        }

        /// <summary>
        /// Not only the final result, but every individual return value: neither a piece of a tag nor the reasoning
        /// may flash on screen, not even for a single chunk.
        /// </summary>
        [Test]
        public void ProcessChunk_SplitOpenTag_NothingLeaksOnAnySingleChunk()
        {
            ThinkBlockStreamFilter filter = new();

            string r1 = filter.ProcessChunk("<thi");
            string r2 = filter.ProcessChunk("nk>I am thinking about this");
            string r3 = filter.ProcessChunk("</think>");
            string r4 = filter.ProcessChunk("The answer is 42.");

            Assert.AreEqual("", r1, "Partial tag buffered");
            Assert.AreEqual("", r2, "Inside think block");
            Assert.AreEqual("", r3, "Closing tag consumed");
            Assert.AreEqual("The answer is 42.", r4, "After think block");
        }

        [Test]
        public void ProcessChunk_CloseTagSplitAcrossChunks_Handled()
        {
            ThinkBlockStreamFilter filter = new();
            string result = FeedChunks(filter,
                "<think>reasoning</th",
                "ink>Answer");
            Assert.AreEqual("Answer", result);
        }

        [Test]
        public void ProcessChunk_OrphanCloseTag_DropsBufferedReasoningPrefix()
        {
            ThinkBlockStreamFilter filter = new();
            string result = filter.ProcessChunk("reasoning text</think>Answer");

            Assert.AreEqual("Answer", result);
        }

        [Test]
        public void ProcessChunk_OrphanCloseTagSplitAcrossChunks_DropsBufferedReasoningPrefix()
        {
            ThinkBlockStreamFilter filter = new();
            string result = FeedChunks(filter,
                "reasoning text</th",
                "ink>Answer");

            Assert.AreEqual("Answer", result);
        }

        /// <summary>
        /// The learner is already reading the answer when the model drops a lone <c>&lt;/think&gt;</c>. There is nothing
        /// before it to hide (the text is on screen), yet the held remainder of the chunk (" Ещё ") was lost: the
        /// learner read "Ответ: да. хвост" instead of "Ответ: да. Ещё  хвост". A lone tag after visible text is
        /// litter that is removed on its own, without the text around it.
        /// </summary>
        [Test]
        public void ProcessChunk_OrphanCloseTagAfterVisibleText_StripsOnlyTheTag()
        {
            ThinkBlockStreamFilter filter = new();
            StringBuilder reasoning = new();
            filter.ReasoningSink = s => reasoning.Append(s);

            string result = FeedChunks(filter, "Ответ: да.", " Ещё <", "/think> хвост");

            Assert.AreEqual("Ответ: да. Ещё  хвост", result);
            Assert.AreEqual(string.Empty, reasoning.ToString(),
                "After visible text there is nothing to hide: nothing may go into the reasoning sink.");
        }

        /// <summary>
        /// Visible text, then a lone <c>&lt;</c> on a chunk boundary, then <c>/think&gt;</c>: what the learner has
        /// already read cannot disappear.
        /// </summary>
        [Test]
        public void ProcessChunk_VisibleTextThenLoneLessThanThenOrphanClose_KeepsVisibleText()
        {
            ThinkBlockStreamFilter filter = new();
            string result = FeedChunks(filter, "Сравни числа", "<", "/think>");

            Assert.AreEqual("Сравни числа", result);
        }

        [Test]
        public void ProcessChunk_BothTagsHeavilySplit_Handled()
        {
            ThinkBlockStreamFilter filter = new();
            string result = FeedChunks(filter,
                "hello ", "<", "th", "in", "k", ">", "deep ", "thoughts", "<", "/th", "ink", ">", " world");
            Assert.AreEqual("hello  world", result);
        }

        [Test]
        public void ProcessChunk_OneCharAtATime_CorrectlyStripsBlock()
        {
            ThinkBlockStreamFilter filter = new();
            const string input = "A<think>X</think>B";
            StringBuilder sb = new();
            foreach (char ch in input)
            {
                sb.Append(filter.ProcessChunk(ch.ToString()));
            }

            sb.Append(filter.Flush());

            Assert.AreEqual("AB", sb.ToString());
        }

        // ===================== Case insensitivity =====================

        [Test]
        public void ProcessChunk_UpperCaseTags_Stripped()
        {
            ThinkBlockStreamFilter filter = new();
            string result = filter.ProcessChunk("<THINK>hidden</THINK>Visible");
            Assert.AreEqual("Visible", result);
        }

        [Test]
        public void ProcessChunk_MixedCaseTags_Stripped()
        {
            ThinkBlockStreamFilter filter = new();
            string result = filter.ProcessChunk("<ThInK>x</tHiNk>OK");
            Assert.AreEqual("OK", result);
        }

        // ===================== Cyrillic =====================

        [Test]
        public void ProcessChunk_CyrillicAroundTags_HiddenSpanStrippedTextPreserved()
        {
            ThinkBlockStreamFilter filter = new();
            StringBuilder reasoning = new();
            filter.ReasoningSink = s => reasoning.Append(s);

            string whole = filter.ProcessChunk("Привет!<think>думаю по-русски</think>Ответ: да");

            Assert.AreEqual("Привет!Ответ: да", whole);
            Assert.AreEqual("думаю по-русски", reasoning.ToString());
        }

        [Test]
        public void ProcessChunk_CyrillicAroundSplitTags_SameAsUnsplit()
        {
            ThinkBlockStreamFilter filter = new();
            StringBuilder reasoning = new();
            filter.ReasoningSink = s => reasoning.Append(s);

            string result = FeedChunks(filter, "Прив", "ет!<th", "ink>ду", "маю</th", "ink>От", "вет: да");

            Assert.AreEqual("Привет!Ответ: да", result);
            Assert.AreEqual("думаю", reasoning.ToString());
        }

        // ===================== Unclosed think block =====================

        [Test]
        public void ProcessChunk_OpenedButNeverClosed_NoVisibleOutput()
        {
            ThinkBlockStreamFilter filter = new();
            string r1 = filter.ProcessChunk("<think>still thinking...");
            string tail = filter.Flush();

            Assert.AreEqual(string.Empty, r1, "Nothing should leak while inside think");
            Assert.AreEqual(string.Empty, tail, "Flush inside unclosed think returns nothing");
        }

        // ===================== Flush: a tail that never became a tag =====================

        /// <summary>
        /// The teacher's answer ends with <c>&lt;</c> ("comparison operator: &lt;"). The filter holds the character
        /// back in case it starts a <c>&lt;think&gt;</c>, but the stream ended and no tag is coming.
        /// Flush used to throw such a tail away, and the learner read the answer without its last character.
        /// </summary>
        [Test]
        public void Flush_AnswerEndsWithLessThan_KeepsIt()
        {
            ThinkBlockStreamFilter separateChunk = new();
            Assert.AreEqual("Оператор сравнения: <", FeedChunks(separateChunk, "Оператор сравнения: ", "<"));

            ThinkBlockStreamFilter sameChunk = new();
            Assert.AreEqual("Оператор сравнения: <", FeedChunks(sameChunk, "Оператор сравнения: <"));
        }

        [TestCase("<")]
        [TestCase("<t")]
        [TestCase("<thin")]
        [TestCase("</")]
        [TestCase("</thin")]
        public void Flush_PartialTagAtEndOfStream_IsVisibleText(string tail)
        {
            ThinkBlockStreamFilter filter = new();
            Assert.AreEqual("Хвост " + tail, FeedChunks(filter, "Хвост ", tail),
                "The tail was held back only in case of a tag; the stream ended, so it is text.");
        }

        [Test]
        public void Flush_PartialOpenTagAlone_IsReturnedNotDropped()
        {
            // The test used to pin the opposite ("a tag cut in half means: drop the buffer"). That is wrong:
            // "<thi" without "nk>" is no different from any other text containing "<", and dropping the
            // tail is exactly what lost the "<" at the end of an answer.
            ThinkBlockStreamFilter filter = new();
            Assert.AreEqual(string.Empty, filter.ProcessChunk("<thi"));
            Assert.AreEqual("<thi", filter.Flush());
        }

        [Test]
        public void Flush_AfterNormalText_ReturnsEmpty()
        {
            ThinkBlockStreamFilter filter = new();
            filter.ProcessChunk("Text without any tags");
            Assert.AreEqual(string.Empty, filter.Flush(),
                "With no partial tag the buffer is already empty, so Flush returns an empty string");
        }

        [Test]
        public void ProcessChunk_LessThanNotThinkPrefix_PassedThrough()
        {
            ThinkBlockStreamFilter filter = new();
            // "<y" is not a prefix of "<think>", so the filter hands everything back immediately.
            string visible = filter.ProcessChunk("x <y");
            Assert.AreEqual("x <y", visible);
            Assert.AreEqual(string.Empty, filter.Flush());
        }

        // ===================== Reset =====================

        [Test]
        public void Reset_AfterPartialBlock_RestoresCleanState()
        {
            ThinkBlockStreamFilter filter = new();
            filter.ProcessChunk("<think>half");
            filter.Reset();

            string result = filter.ProcessChunk("Pure text");
            Assert.AreEqual("Pure text", result);
            Assert.AreEqual(string.Empty, filter.Flush());
        }

        [Test]
        public void Reset_CanBeReused_ForMultipleStreams()
        {
            ThinkBlockStreamFilter filter = new();

            string first = FeedChunks(filter, "<think>a</think>one");
            filter.Reset();
            string second = FeedChunks(filter, "<think>b</think>two");

            Assert.AreEqual("one", first);
            Assert.AreEqual("two", second);
        }

        /// <summary>
        /// Reset must also forget that the previous stream already had visible text: otherwise a lone
        /// <c>&lt;/think&gt;</c> at the start of the next answer would stop hiding the reasoning.
        /// </summary>
        [Test]
        public void Reset_ForgetsThatVisibleTextWasShown()
        {
            ThinkBlockStreamFilter filter = new();
            Assert.AreEqual("first", FeedChunks(filter, "first"));
            filter.Reset();

            Assert.AreEqual("Answer", FeedChunks(filter, "reasoning</think>Answer"));
        }

        // ===================== Edge cases =====================

        [Test]
        public void ProcessChunk_LessThanNotThinkTag_PassedThrough()
        {
            ThinkBlockStreamFilter filter = new();
            string result = FeedChunks(filter, "2 < 3 and 5 > 4");
            Assert.AreEqual("2 < 3 and 5 > 4", result);
        }

        /// <summary>
        /// The provider cuts the stream right after <c>&lt;</c>: "2 &lt;" + " 3". The comparison must arrive whole:
        /// the character is merely delayed until the next chunk, not dropped.
        /// </summary>
        [Test]
        public void ProcessChunk_ComparisonSplitRightAfterLessThan_PassedThrough()
        {
            ThinkBlockStreamFilter filter = new();
            string result = FeedChunks(filter, "2 <", " 3 and 5 > 4");
            Assert.AreEqual("2 < 3 and 5 > 4", result);
        }

        [Test]
        public void ProcessChunk_UnrelatedTag_PassedThrough()
        {
            ThinkBlockStreamFilter filter = new();
            string result = FeedChunks(filter, "<b>bold</b> text");
            Assert.AreEqual("<b>bold</b> text", result);
        }

        [Test]
        public void ProcessChunk_EmptyThinkBlock_Removed()
        {
            ThinkBlockStreamFilter filter = new();
            string result = filter.ProcessChunk("<think></think>Hello");
            Assert.AreEqual("Hello", result);
        }

        [Test]
        public void ProcessChunk_LongThinkBlockAcrossManyChunks_Stripped()
        {
            ThinkBlockStreamFilter filter = new();
            StringBuilder sb = new();

            sb.Append(filter.ProcessChunk("<think>"));
            for (int i = 0; i < 50; i++)
            {
                sb.Append(filter.ProcessChunk($"chunk {i} of reasoning... "));
            }

            sb.Append(filter.ProcessChunk("</think>"));
            sb.Append(filter.ProcessChunk("FINAL"));
            sb.Append(filter.Flush());

            Assert.AreEqual("FINAL", sb.ToString());
        }

        // ===================== Reasoning sink =====================

        [Test]
        public void ReasoningSink_SplitTagStreaming_EmitsHiddenSpan()
        {
            // The hidden span must surface through the sink even when both tags are split across
            // chunk boundaries, while the visible output stays clean.
            ThinkBlockStreamFilter filter = new();
            StringBuilder reasoning = new();
            filter.ReasoningSink = s => reasoning.Append(s);

            string visible = FeedChunks(filter, "A<th", "ink>hid", "den</th", "ink>B");

            Assert.AreEqual("AB", visible);
            Assert.AreEqual("hidden", reasoning.ToString());
        }

        [Test]
        public void ReasoningSink_UnclosedThinkAtFlush_EmitsBufferedReasoning()
        {
            ThinkBlockStreamFilter filter = new();
            StringBuilder reasoning = new();
            filter.ReasoningSink = s => reasoning.Append(s);

            string visible = FeedChunks(filter, "<think>never closed");

            Assert.AreEqual(string.Empty, visible);
            Assert.AreEqual("never closed", reasoning.ToString());
        }

        [Test]
        public void ReasoningSink_OrphanCloseTag_EmitsHiddenPrefix()
        {
            ThinkBlockStreamFilter filter = new();
            StringBuilder reasoning = new();
            filter.ReasoningSink = s => reasoning.Append(s);

            string visible = filter.ProcessChunk("reasoning text</think>Answer");

            Assert.AreEqual("Answer", visible);
            Assert.AreEqual("reasoning text", reasoning.ToString());
        }

        [Test]
        public void ReasoningSink_NotSet_SuppressOnlyBehaviorUnchanged()
        {
            ThinkBlockStreamFilter filter = new();
            string visible = FeedChunks(filter, "A<think>hidden</think>B");
            Assert.AreEqual("AB", visible);
        }

        [Test]
        public void ProcessChunk_ThinkInsideThink_NotNested_TreatedAsText()
        {
            // <think> up to </think>: the first </think> encountered closes the block.
            // Nested "<think>" inside it counts as text and is discarded together with the block.
            ThinkBlockStreamFilter filter = new();
            string result = filter.ProcessChunk("<think>outer <think>inner</think>tail");
            Assert.AreEqual("tail", result);
        }
    }
}
