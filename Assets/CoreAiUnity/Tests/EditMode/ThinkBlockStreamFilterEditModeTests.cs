using System.Text;
using CoreAI.Ai;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode-тесты для production-класса <see cref="ThinkBlockStreamFilter"/>.
    /// Покрывают все ключевые сценарии стриминговой фильтрации <c>&lt;think&gt;...&lt;/think&gt;</c>:
    /// split-теги, несколько блоков, <see cref="ThinkBlockStreamFilter.Flush"/>,
    /// <see cref="ThinkBlockStreamFilter.Reset"/>, регистронезависимость и edge-cases.
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

        [Test]
        public void Flush_PartialOpenTagBuffered_Hidden()
        {
            ThinkBlockStreamFilter filter = new();
            Assert.AreEqual(string.Empty, filter.ProcessChunk("<thi"));
            // Stream oborvan na polovine taga — safely drop the buffer
            Assert.AreEqual(string.Empty, filter.Flush());
        }

        [Test]
        public void Flush_AfterNormalText_ReturnsEmpty()
        {
            ThinkBlockStreamFilter filter = new();
            filter.ProcessChunk("Text without any tags");
            Assert.AreEqual(string.Empty, filter.Flush(),
                "Без частичного тега буфер уже пуст → Flush вернёт пустую строку");
        }

        [Test]
        public void ProcessChunk_LessThanNotThinkPrefix_PassedThrough()
        {
            ThinkBlockStreamFilter filter = new();
            // "<y" не является префиксом "<think>" → фильтр сразу отдаёт всё как есть.
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

        // ===================== Edge cases =====================

        [Test]
        public void ProcessChunk_LessThanNotThinkTag_PassedThrough()
        {
            ThinkBlockStreamFilter filter = new();
            string result = FeedChunks(filter, "2 < 3 and 5 > 4");
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

        [Test]
        public void ProcessChunk_ThinkInsideThink_NotNested_TreatedAsText()
        {
            // <think> до </think> — первое попадание </think> закрывает блок.
            // Вложенные "<think>" внутри считаются текстом и отбрасываются вместе с блоком.
            ThinkBlockStreamFilter filter = new();
            string result = filter.ProcessChunk("<think>outer <think>inner</think>tail");
            Assert.AreEqual("tail", result);
        }
    }
}
