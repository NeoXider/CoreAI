using NUnit.Framework;
using System;
using System.Text;
using System.Text.RegularExpressions;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Тесты для фильтрации &lt;think&gt; блоков из стримингового ответа LLM.
    /// Тестируем state machine через ThinkBlockFilter — вынесенную логику.
    /// </summary>
    public class ThinkBlockFilterEditModeTests
    {
        // ===================== Non-streaming (regex) =====================

        [Test]
        public void StripThinkBlocks_RemovesSimpleBlock()
        {
            string input = "<think>reasoning here</think>Visible text.";
            string result = StripThinkBlocks(input);
            Assert.AreEqual("Visible text.", result);
        }

        [Test]
        public void StripThinkBlocks_RemovesMultilineBlock()
        {
            string input = "<think>\nLine 1\nLine 2\n</think>\nVisible.";
            string result = StripThinkBlocks(input);
            Assert.AreEqual("Visible.", result);
        }

        [Test]
        public void StripThinkBlocks_RemovesMultipleBlocks()
        {
            string input = "<think>a</think>Hello <think>b</think>World";
            string result = StripThinkBlocks(input);
            Assert.AreEqual("Hello World", result);
        }

        [Test]
        public void StripThinkBlocks_CaseInsensitive()
        {
            string input = "<THINK>hidden</THINK>Visible";
            string result = StripThinkBlocks(input);
            Assert.AreEqual("Visible", result);
        }

        [Test]
        public void StripThinkBlocks_NoThinkBlock_Unchanged()
        {
            string input = "Just normal text.";
            string result = StripThinkBlocks(input);
            Assert.AreEqual("Just normal text.", result);
        }

        [Test]
        public void StripThinkBlocks_EmptyInput()
        {
            Assert.AreEqual("", StripThinkBlocks(""));
            Assert.IsNull(StripThinkBlocks(null));
        }

        // ===================== Streaming (state machine) =====================

        [Test]
        public void StreamFilter_NormalText_PassesThrough()
        {
            var filter = new ThinkBlockFilter();
            string result = filter.ProcessChunk("Hello world");
            Assert.AreEqual("Hello world", result);
        }

        [Test]
        public void StreamFilter_ThinkBlockInSingleChunk_Removed()
        {
            var filter = new ThinkBlockFilter();
            string result = filter.ProcessChunk("<think>reasoning</think>Answer.");
            Assert.AreEqual("Answer.", result);
        }

        [Test]
        public void StreamFilter_ThinkBlockAcrossMultipleChunks()
        {
            var filter = new ThinkBlockFilter();

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
        public void StreamFilter_TextBeforeThink_Preserved()
        {
            var filter = new ThinkBlockFilter();
            string result = filter.ProcessChunk("Prefix <think>hidden</think> Suffix");
            Assert.AreEqual("Prefix  Suffix", result);
        }

        [Test]
        public void StreamFilter_MultipleThinkBlocks()
        {
            var filter = new ThinkBlockFilter();
            string r1 = filter.ProcessChunk("<think>a</think>Hello ");
            string r2 = filter.ProcessChunk("<think>b</think>World");

            Assert.AreEqual("Hello ", r1);
            Assert.AreEqual("World", r2);
        }

        [Test]
        public void StreamFilter_ThinkAtEnd_NoOutput()
        {
            var filter = new ThinkBlockFilter();
            string r1 = filter.ProcessChunk("<think>still thinking...");

            Assert.AreEqual("", r1, "No output while inside think block");
        }

        [Test]
        public void StreamFilter_SplitOpenTag()
        {
            var filter = new ThinkBlockFilter();

            // "<think>" split across chunks
            string r1 = filter.ProcessChunk("Hello <th");
            string r2 = filter.ProcessChunk("ink>hidden</think>World");

            // First chunk may buffer the partial tag
            // Combined output should be "Hello World"
            string combined = r1 + r2;
            Assert.That(combined, Does.Contain("Hello"));
            Assert.That(combined, Does.Contain("World"));
            Assert.That(combined, Does.Not.Contain("hidden"));
        }

        // ===================== Helpers =====================

        private static string StripThinkBlocks(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return Regex.Replace(text, @"<think>[\s\S]*?</think>\s*", "",
                RegexOptions.IgnoreCase).Trim();
        }

        /// <summary>
        /// Standalone state machine for testing think-block filtering in streaming.
        /// Mirrors the logic in CoreAiChatPanel.FilterStreamChunk.
        /// </summary>
        private class ThinkBlockFilter
        {
            private bool _insideThink;
            private readonly StringBuilder _buffer = new();

            public string ProcessChunk(string chunk)
            {
                if (string.IsNullOrEmpty(chunk)) return "";

                _buffer.Append(chunk);
                string buf = _buffer.ToString();
                StringBuilder visible = new();

                while (buf.Length > 0)
                {
                    if (_insideThink)
                    {
                        int closeIdx = buf.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
                        if (closeIdx >= 0)
                        {
                            _insideThink = false;
                            buf = buf.Substring(closeIdx + 8);
                        }
                        else
                        {
                            _buffer.Clear();
                            _buffer.Append(buf);
                            return visible.ToString();
                        }
                    }
                    else
                    {
                        int openIdx = buf.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
                        if (openIdx >= 0)
                        {
                            if (openIdx > 0) visible.Append(buf.Substring(0, openIdx));
                            _insideThink = true;
                            buf = buf.Substring(openIdx + 7);
                        }
                        else
                        {
                            // Buffer partial tags
                            int lastLt = buf.LastIndexOf('<');
                            if (lastLt >= 0)
                            {
                                string possibleTag = buf.Substring(lastLt);
                                if ("<think>".StartsWith(possibleTag, StringComparison.OrdinalIgnoreCase))
                                {
                                    visible.Append(buf.Substring(0, lastLt));
                                    _buffer.Clear();
                                    _buffer.Append(possibleTag);
                                    return visible.ToString();
                                }
                            }

                            visible.Append(buf);
                            buf = "";
                        }
                    }
                }

                _buffer.Clear();
                return visible.ToString();
            }
        }
    }
}
