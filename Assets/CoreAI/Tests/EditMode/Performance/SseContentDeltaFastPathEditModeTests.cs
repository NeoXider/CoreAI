#if COREAI_LLM
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;
using MEAI = Microsoft.Extensions.AI;

namespace CoreAI.Core.Tests.EditMode.Performance
{
    /// <summary>
    /// The streamed content-delta fast path (<c>SseContentDeltaReader</c>): every chunk it accepts must produce
    /// exactly the update the full Newtonsoft parse produces, every chunk it declines must still be parsed by
    /// the full path, and the accepted shape has an allocation budget with a negative twin (the full parse
    /// of the same line, which must break it).
    /// </summary>
    [TestFixture]
    public sealed class SseContentDeltaFastPathEditModeTests
    {
        private const string Content =
            "data: {\"id\":\"chatcmpl-9x\",\"object\":\"chat.completion.chunk\",\"created\":1731300000," +
            "\"model\":\"gpt-4o-mini-2024-07-18\",\"system_fingerprint\":\"fp_0ba0d124f1\"," +
            "\"choices\":[{\"index\":0,\"delta\":{\"content\":\" world\"},\"logprobs\":null,\"finish_reason\":null}]}";

        /// <summary>Chunks the fast path must accept: the shapes that carry the tokens of a turn.</summary>
        private static readonly string[] Accepted =
        {
            Content,
            "data: {\"id\":\"chatcmpl-9x\",\"object\":\"chat.completion.chunk\",\"created\":1731300000,\"model\":\"m\"," +
            "\"service_tier\":\"default\",\"system_fingerprint\":null,\"usage\":null,\"obfuscation\":\"Zx1\"," +
            "\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"Hi\",\"refusal\":null}," +
            "\"logprobs\":null,\"finish_reason\":null}]}",
            "data:{\"id\":\"ds-1\",\"model\":\"deepseek-reasoner\",\"choices\":[{\"index\":0,\"delta\":" +
            "{\"content\":null,\"reasoning_content\":\"Let me think\"},\"finish_reason\":null}]}",
            "data: {\"id\":\"lm-1\",\"model\":\"qwen3\",\"choices\":[{\"index\":0,\"delta\":" +
            "{\"content\":\"Answer\",\"reasoning\":\"why\"}}]}",
            "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"\",\"reasoning\":null,\"reasoningContent\":\"camel\"," +
            "\"reasoning_text\":\"text\"}}]}",
            "data: {\"model\":\"  padded-model  \",\"choices\":[{\"delta\":{\"content\":\"esc \\\" \\\\ \\/ \\b \\f \\n \\r \\t \\u00e9\\u4e2d\"}}]}",
            "  data:   { \"choices\" : [ { \"index\" : 3 , \"delta\" : { \"content\" : \"spaced\" } } ] }   ",
            "data: {\"choices\":[{\"delta\":{\"content\":\"<think>raw</think> kept verbatim\"}}]}",
            "data: {\"choices\":[{\"delta\":{\"content\":\"\",\"reasoning_content\":\"reasoning only, empty content\"}}]}",
            "data: {\"id\":\"\",\"choices\":[{\"delta\":{\"content\":\"empty id\"}}]}",
            "data: {\"choices\":[{\"delta\":{\"content\":\"emoji 😀 raw\"}}]}"
        };

        /// <summary>Chunks the fast path must decline, so the full parse decides them (some of them throw there).</summary>
        private static readonly string[] Declined =
        {
            "data: {\"id\":\"c\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}",
            "data: {\"id\":\"c\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"x\"},\"finish_reason\":\"length\"}]}",
            "data: {\"id\":\"c\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"\"}}]}",
            "data: {\"id\":\"c\",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\"," +
            "\"type\":\"function\",\"function\":{\"name\":\"spawn\",\"arguments\":\"{\\\"a\\\":\"}}]}}]}",
            "data: {\"id\":\"c\",\"choices\":[],\"usage\":{\"prompt_tokens\":11,\"completion_tokens\":7,\"total_tokens\":18}}",
            "data: {\"id\":\"c\",\"choices\":[{\"delta\":{\"content\":\"x\"}}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":2,\"total_tokens\":3}}",
            "data: {\"error\":{\"code\":\"429\",\"message\":\"slow down\"}}",
            "data: {\"generation_progress\":{\"phase\":\"processing\"},\"choices\":[]}",
            "data: {\"choices\":[{\"delta\":{\"content\":\"a\",\"content\":\"b\"}}]}",
            "data: {\"choices\":[{\"delta\":{\"content\":\"lone \\ud83d surrogate\"}}]}",
            "data: {\"choices\":[{\"delta\":{\"content\":\"pair \\ud83d\\ude00 escaped\"}}]}",
            "data: {\"created\":0123,\"choices\":[{\"delta\":{\"content\":\"octal\"}}]}",
            "data: {\"created\":1.5,\"choices\":[{\"delta\":{\"content\":\"float\"}}]}",
            "data: {\"choices\":[{\"delta\":{\"content\":\"one\"}},{\"delta\":{\"content\":\"two\"}}]}",
            "data: {\"choices\":[{\"delta\":{\"content\":\"x\"},\"text\":\"legacy\"}]}",
            "data: {\"choices\":[{\"message\":{\"content\":\"whole message\"}}]}",
            "data: {\"choices\":[{\"text\":\"legacy text\"}]}",
            "data: {\"x_groq\":{\"usage\":{}},\"choices\":[{\"delta\":{\"content\":\"x\"}}]}",
            "data: {\"choices\":[{\"delta\":{\"content\":7}}]}",
            "data: {\"choices\":[{\"delta\":{\"content\":\"x\"}}]} trailing",
            "data: {\"choices\":[{\"delta\":{\"content\":\"x\"}}]}{\"second\":1}",
            "data: {\"choices\":[{\"delta\":{\"content\":\"x\"}} ]",
            "data: {\"choices\":{\"delta\":{\"content\":\"x\"}}}",
            "data: {\"id\":null,\"choices\":[{\"delta\":{\"content\":\"null id\"}}]}",
            "data: {\"model\":null,\"choices\":[{\"delta\":{\"content\":\"null model\"}}]}",
            "data: {\"choices\":[{\"delta\":{\"content\":\"bad \\x escape\"}}]}",
            "data: {\"choices\":[{\"delta\":{\"content\":\"raw\ttab\"}}]}",
            "data: {/*c*/\"choices\":[{\"delta\":{\"content\":\"comment\"}}]}",
            "data: {}",
            "data: [DONE]",
            ": keep-alive",
            ""
        };

        [Test]
        public void FastPath_AcceptsTheTokenShapes()
        {
            for (int index = 0; index < Accepted.Length; index++)
            {
                Assert.IsTrue(MeaiOpenAiChatClient.SseContentFastPathAcceptsForTests(Accepted[index]),
                    "accepted shape " + index + " fell back to the full parse: " + Accepted[index]);
            }
        }

        [Test]
        public void FastPath_DeclinesEverythingElse()
        {
            for (int index = 0; index < Declined.Length; index++)
            {
                Assert.IsFalse(MeaiOpenAiChatClient.SseContentFastPathAcceptsForTests(Declined[index]),
                    "declined shape " + index + " was taken by the fast path: " + Declined[index]);
            }
        }

        [Test]
        public void EveryShape_ProducesTheSameUpdateOnBothPaths()
        {
            foreach (string line in Accepted.Concat(Declined))
            {
                AssertSameOnBothPaths(new[] { line }, line);
            }
        }

        [Test]
        public void AStreamedTurn_ProducesTheSameUpdatesOnBothPaths()
        {
            string[] turn =
            {
                "data: {\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"m\"," +
                "\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"\"},\"finish_reason\":null}]}",
                "data: {\"id\":\"chatcmpl-1\",\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{\"reasoning_content\":\"plan\"}}]}",
                "data: {\"id\":\"chatcmpl-1\",\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Hel\"}}]}",
                "data: {\"id\":\"chatcmpl-1\",\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"lo\"}}]}",
                "data: {\"id\":\"chatcmpl-1\",\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0," +
                "\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"spawn\",\"arguments\":\"{}\"}}]}}]}",
                "data: {\"id\":\"chatcmpl-1\",\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}]}",
                "data: {\"id\":\"chatcmpl-1\",\"model\":\"m\",\"choices\":[],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":4,\"total_tokens\":9}}",
                "data: [DONE]"
            };
            AssertSameOnBothPaths(turn, "an OpenAI-shaped turn with reasoning, text, a tool call and usage");

            string[] idless =
            {
                "data: {\"choices\":[{\"delta\":{\"content\":\"one\"}}]}",
                "data: {\"choices\":[{\"delta\":{\"content\":\"two\"}}]}",
                "data: {\"id\":\"late-id\",\"choices\":[{\"delta\":{\"content\":\"three\"}}]}"
            };
            AssertSameOnBothPaths(idless, "an id-less provider that names an id late");
        }

        /// <summary>Upper bound of what an accepted token may allocate: the update, its contents and the text.</summary>
        private const long ContentDeltaBudgetBytes = 512;

        [Test]
        public void ContentDelta_StaysWithinItsAllocationBudget()
        {
            if (!CounterWorks(out string reason))
            {
                Assert.Ignore(reason);
            }

            long perLine = MeasurePerLine(true);
            TestContext.WriteLine("content delta, fast path: " + perLine + " B per line");
            Assert.LessOrEqual(perLine, ContentDeltaBudgetBytes,
                "a streamed content token allocated " + perLine + " B against a budget of " + ContentDeltaBudgetBytes + " B");
        }

        [Test]
        public void ContentDelta_FullParseTwin_BreaksTheBudget()
        {
            if (!CounterWorks(out string reason))
            {
                Assert.Ignore(reason);
            }

            long perLine = MeasurePerLine(false);
            TestContext.WriteLine("content delta, full parse: " + perLine + " B per line");
            Assert.Greater(perLine, ContentDeltaBudgetBytes,
                "the full JObject parse of the same line must break the budget the fast path meets (measured "
                + perLine + " B)");
        }

        private static long MeasurePerLine(bool fastPath)
        {
            const int lines = 2000;
            List<string> stream = new(lines);
            for (int index = 0; index < lines; index++)
            {
                stream.Add(Content);
            }

            MeaiOpenAiChatClient.ParseSseLinesForTests(stream, fastPath);
            MeaiOpenAiChatClient.ParseSseLinesForTests(stream, fastPath);
            long fixedBefore = GC.GetAllocatedBytesForCurrentThread();
            MeaiOpenAiChatClient.ParseSseLinesForTests(new List<string>(), fastPath);
            long fixedCost = GC.GetAllocatedBytesForCurrentThread() - fixedBefore;
            long before = GC.GetAllocatedBytesForCurrentThread();
            List<MEAI.ChatResponseUpdate> updates = MeaiOpenAiChatClient.ParseSseLinesForTests(stream, fastPath);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before - fixedCost;
            GC.KeepAlive(updates);
            // WHY the result list is subtracted: it holds the updates for the test, the streaming loop yields
            // each update instead of keeping it.
            long listBytes = IntPtr.Size * (long)lines;
            return (allocated - listBytes) / lines;
        }

        private static bool CounterWorks(out string reason)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            object[] probe = new object[32];
            GC.KeepAlive(probe);
            long seen = GC.GetAllocatedBytesForCurrentThread() - before;
            reason = seen >= 32 * IntPtr.Size
                ? null
                : "GC.GetAllocatedBytesForCurrentThread did not count a known allocation (Unity's Mono answers 0); " +
                  "the budget is enforced on the portable .NET leg";
            return reason == null;
        }

        private static void AssertSameOnBothPaths(IReadOnlyList<string> lines, string what)
        {
            List<MEAI.ChatResponseUpdate> fast = Run(lines, true, out string fastFailure);
            List<MEAI.ChatResponseUpdate> full = Run(lines, false, out string fullFailure);
            Assert.AreEqual(fullFailure, fastFailure, "both paths must fail the same way for " + what);
            if (fullFailure != null)
            {
                return;
            }

            string fastSynthetic = null;
            string fullSynthetic = null;
            Assert.AreEqual(full.Count, fast.Count, what);
            for (int index = 0; index < full.Count; index++)
            {
                string at = what + " (update " + index + ")";
                MEAI.ChatResponseUpdate expected = full[index];
                MEAI.ChatResponseUpdate actual = fast[index];
                if (expected == null || actual == null)
                {
                    Assert.AreEqual(expected == null, actual == null, at);
                    continue;
                }

                Assert.AreEqual(expected.Role, actual.Role, at);
                Assert.AreEqual(expected.Text, actual.Text, at);
                Assert.AreEqual(expected.ModelId, actual.ModelId, at);
                Assert.AreEqual(expected.FinishReason, actual.FinishReason, at);
                AssertSameId(lines, expected.ResponseId, actual.ResponseId, ref fullSynthetic, ref fastSynthetic,
                    "ResponseId, " + at);
                AssertSameId(lines, expected.MessageId, actual.MessageId, ref fullSynthetic, ref fastSynthetic,
                    "MessageId, " + at);
                Assert.AreEqual(Describe(expected.Contents), Describe(actual.Contents), at);
                Assert.AreEqual(Describe(expected.AdditionalProperties), Describe(actual.AdditionalProperties), at);
            }
        }

        /// <summary>
        /// A wire id (one that appears in the lines) must match exactly; a synthetic one is random per attempt,
        /// so each path must use one synthetic id consistently wherever the other path used its own.
        /// </summary>
        private static void AssertSameId(IReadOnlyList<string> lines, string expected, string actual,
            ref string fullSynthetic, ref string fastSynthetic, string at)
        {
            Assert.IsFalse(string.IsNullOrEmpty(actual), at);
            bool expectedIsWire = lines.Any(line => line.Contains("\"" + expected + "\""));
            if (expectedIsWire)
            {
                Assert.AreEqual(expected, actual, at);
                return;
            }

            Assert.IsFalse(lines.Any(line => line.Contains("\"" + actual + "\"")),
                "the fast path used a wire id where the full parse synthesized one, " + at);
            fullSynthetic ??= expected;
            fastSynthetic ??= actual;
            Assert.AreEqual(fullSynthetic, expected, "one synthetic id per attempt (full parse), " + at);
            Assert.AreEqual(fastSynthetic, actual, "one synthetic id per attempt (fast path), " + at);
        }

        private static List<MEAI.ChatResponseUpdate> Run(IReadOnlyList<string> lines, bool fastPath, out string failure)
        {
            failure = null;
            try
            {
                return MeaiOpenAiChatClient.ParseSseLinesForTests(lines, fastPath);
            }
            catch (Exception exception)
            {
                failure = exception.GetType().FullName + ": " + exception.Message;
                return null;
            }
        }

        private static string Describe(IList<MEAI.AIContent> contents)
        {
            if (contents == null)
            {
                return "<null>";
            }

            List<string> parts = new();
            foreach (MEAI.AIContent content in contents)
            {
                switch (content)
                {
                    case MEAI.TextContent text:
                        parts.Add("text:" + text.Text);
                        break;
                    case MEAI.TextReasoningContent reasoning:
                        parts.Add("reasoning:" + reasoning.Text);
                        break;
                    case MEAI.UsageContent usage:
                        parts.Add("usage:" + usage.Details.InputTokenCount + "/" + usage.Details.OutputTokenCount + "/"
                                  + usage.Details.TotalTokenCount);
                        break;
                    case MEAI.FunctionCallContent call:
                        parts.Add("call:" + call.CallId + ":" + call.Name + ":" + (call.Arguments == null
                            ? "<null>"
                            : string.Join(",", call.Arguments.Select(pair => pair.Key + "=" + pair.Value))));
                        break;
                    default:
                        parts.Add(content.GetType().Name);
                        break;
                }
            }

            return string.Join(" | ", parts);
        }

        private static string Describe(MEAI.AdditionalPropertiesDictionary properties)
        {
            if (properties == null)
            {
                return "<null>";
            }

            return string.Join(",", properties.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => pair.Key + "=" + Convert.ToString(pair.Value, CultureInfo.InvariantCulture)));
        }
    }
}
#endif
