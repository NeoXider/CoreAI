#if COREAI_LLM
using System.Collections.Generic;
using System.Linq;
using CoreAI.Infrastructure.Llm;
using MEAI = Microsoft.Extensions.AI;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Pins the per-line SSE hot path. Everything here runs once per streamed token on WebGL's single
    /// thread, so the guarantees are about work NOT done: the line is framed without copies, and the
    /// "are these tool arguments a complete JSON object?" question is answered from an incremental scan
    /// instead of re-materialising and re-parsing the whole buffer on every line.
    /// </summary>
    public sealed class MeaiOpenAiChatClientStreamHotPathEditModeTests
    {
        private const string TextDelta =
            "{\"id\":\"resp_1\",\"model\":\"m\",\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}";

        // ---- SSE line framing -------------------------------------------------------------------

        /// <summary>
        /// The allocation-free framing must extract exactly what
        /// <c>line.Trim().Substring(5).TrimStart()</c> extracted, including the shapes local servers
        /// produce (no space after the colon, stray indentation, upper-case prefix).
        /// </summary>
        [TestCase("data: {\"a\":1}", "{\"a\":1}")]
        [TestCase("data:{\"a\":1}", "{\"a\":1}")]
        [TestCase("   data:   {\"a\":1}   ", "{\"a\":1}")]
        [TestCase("DATA: [DONE]", "[DONE]")]
        [TestCase("data:", "")]
        [TestCase("data:    ", "")]
        [TestCase("\tdata:\t{\"a\":1}\t", "{\"a\":1}")]
        public void SseDataPayload_MatchesTheTrimAndSubstringItReplaced(string line, string expected)
        {
            Assert.AreEqual(expected, MeaiOpenAiChatClient.SseDataPayloadForTests(line));
        }

        [TestCase("")]
        [TestCase("   ")]
        [TestCase(": keep-alive")]
        [TestCase("event: message")]
        [TestCase("dat: {\"a\":1}")]
        [TestCase("xdata: {\"a\":1}")]
        public void SseDataPayload_IsNullForEveryNonDataLine(string line)
        {
            Assert.IsNull(MeaiOpenAiChatClient.SseDataPayloadForTests(line));
        }

        [TestCase("data: [DONE]", true)]
        [TestCase("data:[DONE]", true)]
        [TestCase("  DATA:  [DONE]  ", true)]
        [TestCase("data: [done]", false)]
        [TestCase("data: [DONE] extra", false)]
        [TestCase("data: {\"a\":1}", false)]
        [TestCase(": keep-alive", false)]
        [TestCase("", false)]
        public void IsSseDoneLine_KeepsItsOrdinalExactMatch(string line, bool expected)
        {
            Assert.AreEqual(expected, MeaiOpenAiChatClient.IsSseDoneLineForTests(line));
        }

        /// <summary>
        /// The streaming loop now parses ONE line directly instead of building <c>line + "\n"</c> and
        /// splitting it. Both entry points must see the same delta.
        /// </summary>
        [TestCase("data: " + TextDelta)]
        [TestCase("data:" + TextDelta)]
        [TestCase("   data:   " + TextDelta + "   ")]
        public void ParseSseLine_AgreesWithTheMultiLineParser(string line)
        {
            MEAI.ChatResponseUpdate single = MeaiOpenAiChatClient.ParseSseLineForTests(line);
            List<MEAI.ChatResponseUpdate> multi =
                MeaiOpenAiChatClient.ParseSseUpdatesForTests(line + "\n").ToList();

            Assert.IsNotNull(single);
            Assert.AreEqual(1, multi.Count);
            Assert.AreEqual(multi[0].Text, single.Text);
            Assert.AreEqual(multi[0].ModelId, single.ModelId);
        }

        [TestCase("data: [DONE]")]
        [TestCase(": keep-alive")]
        [TestCase("")]
        [TestCase("data:")]
        [TestCase("data: not json")]
        public void ParseSseLine_ReturnsNullWhereTheLoopMustSkip(string line)
        {
            Assert.IsNull(MeaiOpenAiChatClient.ParseSseLineForTests(line));
        }

        // ---- Incremental tool-argument completeness ---------------------------------------------

        private static readonly string[] ArgumentPayloads =
        {
            "{}",
            "{\"a\":1}",
            "{\"a\":{\"b\":[1,2,3]},\"c\":\"}\"}",
            "{\"path\":\"C:\\\\tmp\\\\x.txt\"}",
            "{\"quote\":\"he said \\\"hi\\\"\"}",
            "{\"trailing\":1}   ",
            "{\"unclosed\":1",
            "{\"junk\":1} tail",
            "{}{}",
            "   {\"lead\":1}",
            "not json",
            "",
            "   "
        };

        /// <summary>
        /// The incremental verdict must equal the batch scan for EVERY payload and EVERY way the
        /// provider could split it across deltas — including a split inside a string and a split right
        /// after a backslash, where the scanner has to carry its state across the boundary.
        /// </summary>
        [Test]
        public void IncrementalCompleteness_EqualsBatchScan_ForEverySplitPoint()
        {
            foreach (string payload in ArgumentPayloads)
            {
                bool expected = MeaiOpenAiChatClient.IsCompleteJsonObjectForTests(payload);

                Assert.AreEqual(expected,
                    MeaiOpenAiChatClient.IncrementalArgumentsCompleteForTests(new[] { payload }),
                    $"whole payload: {payload}");

                for (int split = 1; split < payload.Length; split++)
                {
                    string[] fragments = { payload.Substring(0, split), payload.Substring(split) };
                    Assert.AreEqual(expected,
                        MeaiOpenAiChatClient.IncrementalArgumentsCompleteForTests(fragments),
                        $"payload '{payload}' split at {split}");
                }

                // Character by character: the worst fragmentation a provider can produce.
                string[] perChar = payload.Select(symbol => symbol.ToString()).ToArray();
                Assert.AreEqual(expected,
                    MeaiOpenAiChatClient.IncrementalArgumentsCompleteForTests(perChar),
                    $"payload '{payload}' one char per delta");
            }
        }

        // ---- Cost of the drain loop -------------------------------------------------------------

        /// <summary>
        /// The measurable claim: a tool call whose arguments arrive in many deltas materialises its
        /// buffer ONCE (at the drain that emits it), not once per delta. Before the incremental scan
        /// every streamed line cost a full <c>StringBuilder.ToString()</c> plus a full re-parse of
        /// everything accumulated so far, which is quadratic in the argument length.
        /// </summary>
        [Test]
        public void LongToolCall_MaterialisesItsArgumentBufferOnce()
        {
            List<string> chunks = new()
            {
                ToolCallDelta(0, "call_a", "build", "{\"items\":[")
            };
            for (int i = 0; i < 40; i++)
            {
                chunks.Add(ToolCallDelta(0, null, null, $"\"item-{i}\","));
            }

            chunks.Add(ToolCallDelta(0, null, null, "\"last\"]}"));

            Assert.AreEqual(1, MeaiOpenAiChatClient.ArgumentMaterialisationsForTests(chunks));
        }

        /// <summary>Two parallel calls: one materialisation each, still independent of line count.</summary>
        [Test]
        public void ParallelToolCalls_MaterialiseOncePerCall()
        {
            List<string> chunks = new()
            {
                ToolCallDelta(0, "call_a", "first", "{\"x\":"),
                ToolCallDelta(1, "call_b", "second", "{\"y\":"),
                ToolCallDelta(0, null, null, "1}"),
                ToolCallDelta(1, null, null, "2}")
            };

            Assert.AreEqual(2, MeaiOpenAiChatClient.ArgumentMaterialisationsForTests(chunks));
        }

        /// <summary>A pure-text turn never touches the argument machinery at all.</summary>
        [Test]
        public void TextOnlyStream_MaterialisesNothing()
        {
            List<string> chunks = Enumerable.Repeat(TextDelta, 50).ToList();
            Assert.AreEqual(0, MeaiOpenAiChatClient.ArgumentMaterialisationsForTests(chunks));
        }

        private static string ToolCallDelta(int index, string id, string name, string argumentsFragment)
        {
            string idField = id == null ? "" : $"\"id\":\"{id}\",";
            string nameField = name == null ? "" : $"\"name\":\"{name}\",";
            string escapedArguments = argumentsFragment
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"");
            return "{\"id\":\"resp_1\",\"model\":\"m\",\"choices\":[{\"delta\":{\"tool_calls\":[{" +
                   $"\"index\":{index},{idField}\"type\":\"function\",\"function\":{{{nameField}" +
                   $"\"arguments\":\"{escapedArguments}\"}}}}]}}}}]}}";
        }
    }
}
#endif
