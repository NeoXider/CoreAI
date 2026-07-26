#if !COREAI_NO_LLM
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using MEAI = Microsoft.Extensions.AI;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    public sealed class MeaiOpenAiChatClientSseEditModeTests
    {
        [Test]
        public void ParseSseUpdates_MessageOnlyChunk_ParsesText()
        {
            const string sse =
                "data:{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"chunk\"}}]}\n";
            List<MEAI.ChatResponseUpdate> list = MeaiOpenAiChatClient.ParseSseUpdatesForTests(sse).ToList();
            Assert.AreEqual(1, list.Count);
            Assert.AreEqual("chunk", list[0].Text);
        }

        [Test]
        public void ParseSseUpdates_DataPrefixWithSpace_ParsesDelta()
        {
            const string sse = "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n";
            List<MEAI.ChatResponseUpdate> list = MeaiOpenAiChatClient.ParseSseUpdatesForTests(sse).ToList();
            Assert.AreEqual(1, list.Count);
            Assert.AreEqual("hi", list[0].Text);
        }

        [Test]
        public void ParseSseUpdates_DataPrefixWithoutSpace_ParsesDelta()
        {
            const string sse = "data:{\"choices\":[{\"delta\":{\"content\":\"local\"}}]}\n";
            List<MEAI.ChatResponseUpdate> list = MeaiOpenAiChatClient.ParseSseUpdatesForTests(sse).ToList();
            Assert.AreEqual(1, list.Count);
            Assert.AreEqual("local", list[0].Text);
        }

        [Test]
        public void ParseSseDataLine_MessageOnly_InStreamChunk_EmitsText()
        {
            const string json =
                "{\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"from message\"}}]}";
            MEAI.ChatResponseUpdate u = MeaiOpenAiChatClient.ParseSseDataLineForTests(json);
            Assert.IsNotNull(u);
            Assert.AreEqual("from message", u.Text);
        }

        [Test]
        public void ParseSseDataLine_LegacyChoicesText_EmitsText()
        {
            const string json = "{\"choices\":[{\"index\":0,\"text\":\"legacy stream\"}]}";
            MEAI.ChatResponseUpdate u = MeaiOpenAiChatClient.ParseSseDataLineForTests(json);
            Assert.IsNotNull(u);
            Assert.AreEqual("legacy stream", u.Text);
        }

        [Test]
        public void ParseSseDataLine_ReasoningOnly_EmitsTextReasoningContentNotAssistantText()
        {
            // A reasoning-only delta (DeepSeek/Qwen delta.reasoning_content with content=null) is a
            // REAL delta: it must surface as TextReasoningContent (so the empty-stream retry never
            // fires) while the visible assistant text stays empty.
            const string json = "{\"choices\":[{\"delta\":{\"reasoning_content\":\"think\"}}]}";
            MEAI.ChatResponseUpdate u = MeaiOpenAiChatClient.ParseSseDataLineForTests(json);
            Assert.IsNotNull(u, "Reasoning-only deltas must parse, not be dropped.");
            Assert.AreEqual("", u.Text ?? "", "Reasoning must never leak into the visible text.");
            MEAI.TextReasoningContent reasoning =
                u.Contents.OfType<MEAI.TextReasoningContent>().Single();
            Assert.AreEqual("think", reasoning.Text);
        }

        [Test]
        public void ParseSseDataLine_ReasoningAlternateSpellings_EmitTextReasoningContent()
        {
            const string bareJson = "{\"choices\":[{\"delta\":{\"reasoning\":\"bare\"}}]}";
            MEAI.ChatResponseUpdate bare = MeaiOpenAiChatClient.ParseSseDataLineForTests(bareJson);
            Assert.IsNotNull(bare);
            Assert.AreEqual("bare",
                bare.Contents.OfType<MEAI.TextReasoningContent>().Single().Text);

            const string camelJson = "{\"choices\":[{\"delta\":{\"reasoningContent\":\"camel\"}}]}";
            MEAI.ChatResponseUpdate camel = MeaiOpenAiChatClient.ParseSseDataLineForTests(camelJson);
            Assert.IsNotNull(camel);
            Assert.AreEqual("camel",
                camel.Contents.OfType<MEAI.TextReasoningContent>().Single().Text);
        }

        [Test]
        public void ParseSseDataLine_ContentOnly_EmitsText()
        {
            const string json = "{\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}";
            MEAI.ChatResponseUpdate u = MeaiOpenAiChatClient.ParseSseDataLineForTests(json);
            Assert.IsNotNull(u);
            Assert.AreEqual("hi", u.Text);
        }

        [Test]
        public void ParseSseDataLine_ReasoningAndContent_EmitsContentAsTextAndReasoningAsReasoning()
        {
            const string json = "{\"choices\":[{\"delta\":{\"reasoning_content\":\"x\",\"content\":\"out\"}}]}";
            MEAI.ChatResponseUpdate u = MeaiOpenAiChatClient.ParseSseDataLineForTests(json);
            Assert.IsNotNull(u);
            Assert.AreEqual("out", u.Text, "Only content may be visible text.");
            Assert.AreEqual("x", u.Contents.OfType<MEAI.TextReasoningContent>().Single().Text,
                "The reasoning delta must ride along as TextReasoningContent, not be dropped.");
        }

        [Test]
        public void ParseCompletion_EmptyContent_PromotesReasoningToVisibleText()
        {
            // Reasoning-only model, non-streaming: the answer lives in reasoning_content and content
            // is empty. The reasoning must surface as TextReasoningContent AND be promoted to the
            // visible text so the turn never ends as LlmErrorCode.EmptyResponse.
            const string json =
                "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"\",\"reasoning_content\":\"Hello from reasoning\"}}]}";
            MEAI.ChatResponse r = MeaiOpenAiChatClient.ParseResponse(json);
            Assert.AreEqual("Hello from reasoning", r.Text);
            Assert.AreEqual("Hello from reasoning",
                r.Messages[0].Contents.OfType<MEAI.TextReasoningContent>().Single().Text);
        }

        [Test]
        public void ParseCompletion_ContentPresent_ReasoningExposedButNotPromoted()
        {
            const string json =
                "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"real answer\",\"reasoning_content\":\"hidden plan\"}}]}";
            MEAI.ChatResponse r = MeaiOpenAiChatClient.ParseResponse(json);
            Assert.AreEqual("real answer", r.Text, "Visible text must stay the provider content.");
            Assert.AreEqual("hidden plan",
                r.Messages[0].Contents.OfType<MEAI.TextReasoningContent>().Single().Text);
        }

        [Test]
        public void ParseCompletion_EmptyContentWithToolCalls_DoesNotPromoteReasoning()
        {
            // A tool-call turn with empty content is NOT an empty answer: its reasoning must never
            // masquerade as visible assistant text next to the executing tool calls.
            const string json =
                "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"\",\"reasoning_content\":\"call plan\",\"tool_calls\":[" +
                "{\"id\":\"c1\",\"type\":\"function\",\"function\":{\"name\":\"go\",\"arguments\":\"{}\"}}]}}]}";
            MEAI.ChatResponse r = MeaiOpenAiChatClient.ParseResponse(json);
            Assert.AreEqual("", r.Text);
            Assert.AreEqual("call plan",
                r.Messages[0].Contents.OfType<MEAI.TextReasoningContent>().Single().Text);
            Assert.AreEqual(1, r.Messages[0].Contents.OfType<MEAI.FunctionCallContent>().Count());
        }

        [Test]
        public void ParseCompletion_InlineThinkOnlyContent_PromotesThinkTextAndExposesReasoning()
        {
            const string json =
                "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"<think>inline plan</think>\"}}]}";
            MEAI.ChatResponse r = MeaiOpenAiChatClient.ParseResponse(json);
            Assert.AreEqual("inline plan", r.Text,
                "A think-only answer must be promoted instead of surfacing as empty.");
            Assert.AreEqual("inline plan",
                r.Messages[0].Contents.OfType<MEAI.TextReasoningContent>().Single().Text);
        }

        [Test]
        public void ParseCompletion_ContentAsTextPartsArray_JoinsText()
        {
            const string json =
                "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"a\"},{\"type\":\"text\",\"text\":\"b\"}]}}]}";
            MEAI.ChatResponse r = MeaiOpenAiChatClient.ParseResponse(json);
            Assert.AreEqual("a\nb", r.Text);
        }

        [Test]
        public void ParseCompletion_EmptyContent_PromotesReasoningToVisibleText_CamelCase()
        {
            const string json =
                "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"\",\"reasoningContent\":\"Hello camel\"}}]}";
            MEAI.ChatResponse r = MeaiOpenAiChatClient.ParseResponse(json);
            Assert.AreEqual("Hello camel", r.Text);
            Assert.AreEqual("Hello camel",
                r.Messages[0].Contents.OfType<MEAI.TextReasoningContent>().Single().Text);
        }

        [Test]
        public void ParseCompletion_MalformedToolCallArguments_DegradesOnlyThatCall()
        {
            // One bad tool call must NOT wipe the assistant text and the other calls: the good call
            // parses normally, the bad one surfaces via the shared parse-error markers (same
            // contract as the streaming accumulator; ToolExecutionPolicy fails just that call).
            const string json =
                "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"doing it\",\"tool_calls\":[" +
                "{\"id\":\"c1\",\"type\":\"function\",\"function\":{\"name\":\"good\",\"arguments\":\"{\\\"a\\\":1}\"}}," +
                "{\"id\":\"c2\",\"type\":\"function\",\"function\":{\"name\":\"bad\",\"arguments\":\"{\\\"broken\\\":\"}}" +
                "]}}]}";

            MEAI.ChatResponse r = MeaiOpenAiChatClient.ParseResponse(json);

            Assert.AreEqual("doing it", r.Text, "Assistant text must survive a bad tool call.");
            List<MEAI.FunctionCallContent> calls =
                r.Messages[0].Contents.OfType<MEAI.FunctionCallContent>().ToList();
            Assert.AreEqual(2, calls.Count, "Both tool calls must survive; only the bad one degrades.");

            MEAI.FunctionCallContent good = calls.Single(c => c.Name == "good");
            Assert.AreEqual(1L, Convert.ToInt64(good.Arguments["a"]));
            Assert.IsFalse(good.Arguments.ContainsKey(MeaiOpenAiChatClient.ToolCallParseErrorKeyForTests));

            MEAI.FunctionCallContent bad = calls.Single(c => c.Name == "bad");
            Assert.IsTrue(bad.Arguments.ContainsKey(MeaiOpenAiChatClient.ToolCallParseErrorKeyForTests),
                "The malformed call must carry the parse-error marker, not silently vanish.");
            Assert.AreEqual("{\"broken\":",
                bad.Arguments[MeaiOpenAiChatClient.ToolCallRawArgumentsKeyForTests]);
        }

        [Test]
        public void ParseCompletion_WholeResponseMalformed_ReturnsEmptyMessage()
        {
            MEAI.ChatResponse r = MeaiOpenAiChatClient.ParseResponse("this is not json");
            Assert.AreEqual("", r.Text);
        }

        [Test]
        public void IsSseDoneLine_DataDone_ReturnsTrue()
        {
            Assert.IsTrue(MeaiOpenAiChatClient.IsSseDoneLineForTests("data: [DONE]"));
            Assert.IsTrue(MeaiOpenAiChatClient.IsSseDoneLineForTests("data:[DONE]"));
            Assert.IsFalse(MeaiOpenAiChatClient.IsSseDoneLineForTests("data: {\"done\":true}"));
        }

        [Test]
        public async Task GetStreamingResponseAsync_DoneSentinelStopsWithoutWaitingForStreamEof()
        {
            const string sse =
                "data: {\"choices\":[{\"delta\":{\"content\":\"hello\"}}]}\n\n" +
                "data: [DONE]\n\n";
            MeaiOpenAiChatClient client = new(new DoneSentinelSettings(), new DoneSentinelTransport(sse));
            List<string> parts = new();

            await foreach (MEAI.ChatResponseUpdate update in client.GetStreamingResponseAsync(
                               new[] { new MEAI.ChatMessage(MEAI.ChatRole.User, "hi") }))
            {
                if (!string.IsNullOrEmpty(update.Text))
                {
                    parts.Add(update.Text);
                }
            }

            Assert.AreEqual("hello", string.Concat(parts));
        }

        [Test]
        public async Task GetStreamingResponseAsync_AsyncChunkedReads_ContinuesAfterFirstChunk()
        {
            string[] chunks =
            {
                "data: {\"choices\":[{\"delta\":{\"content\":\"A\"}}]}\n\n",
                "data: {\"choices\":[{\"delta\":{\"content\":\"B\"}}]}\n\n",
                "data: [DONE]\n\n"
            };
            MeaiOpenAiChatClient client = new(new DoneSentinelSettings(), new AsyncChunkedSseTransport(chunks));
            List<string> parts = new();

            await foreach (MEAI.ChatResponseUpdate update in client.GetStreamingResponseAsync(
                               new[] { new MEAI.ChatMessage(MEAI.ChatRole.User, "hi") }))
            {
                if (!string.IsNullOrEmpty(update.Text))
                {
                    parts.Add(update.Text);
                }
            }

            Assert.AreEqual("AB", string.Concat(parts));
        }

        [Test]
        public async Task GetStreamingResponseAsync_ReasoningOnlyStream_PromotesReasoningToNonEmptyAnswer()
        {
            // A reasoning-only model (DeepSeek/Qwen style: only delta.reasoning_content, content
            // stays null) previously parsed ZERO deltas -> empty-stream retry -> non-stream fallback
            // -> EmptyResponse. Now the reasoning deltas count as real deltas (no retry/fallback:
            // DoneSentinelTransport throws on any non-streaming call) and the accumulated reasoning
            // is promoted to one final visible TextContent, so the turn is never empty.
            const string sse =
                "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"I am \"}}]}\n\n" +
                "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"thinking\"}}]}\n\n" +
                "data: [DONE]\n\n";
            MeaiOpenAiChatClient client = new(new DoneSentinelSettings(), new DoneSentinelTransport(sse));
            List<string> visibleParts = new();
            StringBuilder reasoningParts = new();

            await foreach (MEAI.ChatResponseUpdate update in client.GetStreamingResponseAsync(
                               new[] { new MEAI.ChatMessage(MEAI.ChatRole.User, "hi") }))
            {
                if (!string.IsNullOrEmpty(update.Text))
                {
                    visibleParts.Add(update.Text);
                }

                if (update.Contents != null)
                {
                    foreach (MEAI.TextReasoningContent trc in
                             update.Contents.OfType<MEAI.TextReasoningContent>())
                    {
                        reasoningParts.Append(trc.Text);
                    }
                }
            }

            Assert.AreEqual("I am thinking", reasoningParts.ToString(),
                "Every reasoning delta must surface as TextReasoningContent.");
            Assert.AreEqual("I am thinking", string.Concat(visibleParts).Trim(),
                "A reasoning-only turn must end with the reasoning promoted to a visible answer.");
        }

        [Test]
        public async Task GetStreamingResponseAsync_ReasoningPlusContent_DoesNotPromoteReasoning()
        {
            const string sse =
                "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"plan\"}}]}\n\n" +
                "data: {\"choices\":[{\"delta\":{\"content\":\"answer\"}}]}\n\n" +
                "data: [DONE]\n\n";
            MeaiOpenAiChatClient client = new(new DoneSentinelSettings(), new DoneSentinelTransport(sse));
            List<string> visibleParts = new();

            await foreach (MEAI.ChatResponseUpdate update in client.GetStreamingResponseAsync(
                               new[] { new MEAI.ChatMessage(MEAI.ChatRole.User, "hi") }))
            {
                if (!string.IsNullOrEmpty(update.Text))
                {
                    visibleParts.Add(update.Text);
                }
            }

            Assert.AreEqual("answer", string.Concat(visibleParts),
                "When real content arrived, reasoning must stay hidden (no promotion).");
        }

        [Test]
        public void FullResponseToSimulatedStreamingUpdates_ReEmitsUsageAsTrailingUpdate()
        {
            // WebGL non-native-streaming and the stream->non-stream fallback replay a full response
            // as simulated updates; without a trailing UsageContent every such turn reported 0 tokens.
            MEAI.ChatResponse full = new(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, "hi"))
            {
                Usage = new MEAI.UsageDetails
                {
                    InputTokenCount = 3,
                    OutputTokenCount = 5,
                    TotalTokenCount = 8
                }
            };

            List<MEAI.ChatResponseUpdate> updates =
                MeaiOpenAiChatClient.FullResponseToSimulatedStreamingUpdatesForTests(full);

            Assert.AreEqual("hi", string.Concat(updates.Select(u => u.Text ?? "")),
                "The visible text must still be replayed.");
            MEAI.UsageContent usage = updates
                .SelectMany(u => u.Contents ?? new List<MEAI.AIContent>())
                .OfType<MEAI.UsageContent>()
                .Single();
            Assert.AreEqual(3, (int)(usage.Details.InputTokenCount ?? 0));
            Assert.AreEqual(5, (int)(usage.Details.OutputTokenCount ?? 0));
            Assert.AreEqual(8, (int)(usage.Details.TotalTokenCount ?? 0));
            Assert.AreSame(updates.Last(),
                updates.Single(u => (u.Contents ?? new List<MEAI.AIContent>()).OfType<MEAI.UsageContent>().Any()),
                "Usage must ride the trailing update, mirroring OpenAI's final include_usage chunk.");
        }

        [Test]
        public void FullResponseToSimulatedStreamingUpdates_ReEmitsReasoningContent()
        {
            MEAI.ChatResponse full = new(new MEAI.ChatMessage(MEAI.ChatRole.Assistant,
                new List<MEAI.AIContent>
                {
                    new MEAI.TextReasoningContent("hidden plan"),
                    new MEAI.TextContent("visible answer")
                }));

            List<MEAI.ChatResponseUpdate> updates =
                MeaiOpenAiChatClient.FullResponseToSimulatedStreamingUpdatesForTests(full);

            Assert.AreEqual("visible answer", string.Concat(updates.Select(u => u.Text ?? "")));
            Assert.AreEqual("hidden plan", updates
                .SelectMany(u => u.Contents ?? new List<MEAI.AIContent>())
                .OfType<MEAI.TextReasoningContent>()
                .Single().Text);
        }

        [Test]
        public async Task GetStreamingResponseAsync_EmptyStreamRepeated_FallsBackToNonStreamingCompletion()
        {
            // An SSE 200 that carries only keep-alive comments (upstream rate limit hidden behind a
            // proxy) must NOT burn the full 10-attempt backoff budget: after a few empty streams the
            // turn falls back to ONE plain completion and the user still gets the answer.
            EmptyStreamThenNonStreamingTransport transport = new();
            MeaiOpenAiChatClient client = new(new DoneSentinelSettings(), transport);
            List<string> parts = new();

            await foreach (MEAI.ChatResponseUpdate update in client.GetStreamingResponseAsync(
                               new[] { new MEAI.ChatMessage(MEAI.ChatRole.User, "hi") }))
            {
                if (!string.IsNullOrEmpty(update.Text))
                {
                    parts.Add(update.Text);
                }
            }

            Assert.AreEqual("fallback answer", string.Concat(parts),
                "The non-streaming fallback answer must surface through the streaming iterator.");
            Assert.AreEqual(3, transport.StreamOpens,
                "An empty stream must retry only emptyStreamMaxAttempts (3) times.");
            Assert.AreEqual(1, transport.NonStreamingCalls,
                "After the empty-stream retries exactly one non-streaming completion runs.");
        }

        [Test]
        public async Task GetStreamingResponseAsync_EndlessKeepAliveStream_AbortsEarlyAndFallsBack()
        {
            // A proxy hiding an upstream failure can hold the SSE connection open indefinitely,
            // sending only ": keep-alive" comment lines and never closing. Waiting for the server
            // to end the stream would blow through callers' turn budgets (observed ~40s per attempt
            // x 3 attempts > the 120s briefing watchdog). The starved-stream watchdog must abandon
            // each attempt after the first-delta window and reach the non-streaming fallback.
            int savedTimeout = MeaiOpenAiChatClient.StarvedStreamFirstDeltaTimeoutSeconds;
            MeaiOpenAiChatClient.StarvedStreamFirstDeltaTimeoutSeconds = 0;
            try
            {
                EndlessKeepAliveTransport transport = new();
                MeaiOpenAiChatClient client = new(new DoneSentinelSettings(), transport);
                List<string> parts = new();

                await foreach (MEAI.ChatResponseUpdate update in client.GetStreamingResponseAsync(
                                   new[] { new MEAI.ChatMessage(MEAI.ChatRole.User, "hi") }))
                {
                    if (!string.IsNullOrEmpty(update.Text))
                    {
                        parts.Add(update.Text);
                    }
                }

                Assert.AreEqual("fallback answer", string.Concat(parts),
                    "A never-closing keep-alive-only stream must still deliver the fallback answer.");
                Assert.AreEqual(3, transport.StreamOpens,
                    "Each starved attempt must be aborted early and retried only emptyStreamMaxAttempts (3) times.");
                Assert.AreEqual(1, transport.NonStreamingCalls,
                    "After the starved-stream retries exactly one non-streaming completion runs.");
            }
            finally
            {
                MeaiOpenAiChatClient.StarvedStreamFirstDeltaTimeoutSeconds = savedTimeout;
            }
        }

        [Test]
        public async Task GetStreamingResponseAsync_RateLimited429Once_RetriesAndCompletes()
        {
            // Free-tier providers (OpenRouter :free and similar) reject bursts with 429 routinely;
            // the next window usually accepts. The single bounded retry must absorb that instead of
            // surfacing "Error: HTTP error 429" to the player on the first hit.
            const string sse =
                "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\n" +
                "data: [DONE]\n\n";
            RateLimit429ThenSseTransport transport = new(1, sse);
            MeaiOpenAiChatClient client = new(new DoneSentinelSettings(), transport);
            List<string> parts = new();

            await foreach (MEAI.ChatResponseUpdate update in client.GetStreamingResponseAsync(
                               new[] { new MEAI.ChatMessage(MEAI.ChatRole.User, "hi") }))
            {
                if (!string.IsNullOrEmpty(update.Text))
                {
                    parts.Add(update.Text);
                }
            }

            Assert.AreEqual("ok", string.Concat(parts),
                "The stream must complete after a 429 absorbed by the rate-limit retry.");
            Assert.AreEqual(2, transport.StreamOpens,
                "One 429 must consume exactly the single retry (2 opens total).");
        }

        [Test]
        public async Task GetStreamingResponseAsync_RateLimitedPersists_FallsBackToNonStreaming()
        {
            // Request → retry → FALLBACK: when the stream keeps 429-ing after the retry budget,
            // the turn must try ONE plain completion before surfacing any error.
            RateLimit429ThenSseTransport transport = new(99, "data: [DONE]\n\n")
            {
                NonStreamingBody =
                    "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"fallback answer\"}}]}"
            };
            MeaiOpenAiChatClient client = new(new DoneSentinelSettings(), transport);
            List<string> parts = new();

            await foreach (MEAI.ChatResponseUpdate update in client.GetStreamingResponseAsync(
                               new[] { new MEAI.ChatMessage(MEAI.ChatRole.User, "hi") }))
            {
                if (!string.IsNullOrEmpty(update.Text))
                {
                    parts.Add(update.Text);
                }
            }

            Assert.AreEqual("fallback answer", string.Concat(parts),
                "Persistent 429 on the stream must be rescued by the non-streaming fallback.");
            Assert.AreEqual(2, transport.StreamOpens,
                "1 initial attempt + 1 retry before the fallback.");
            Assert.AreEqual(1, transport.NonStreamingCalls,
                "Exactly one non-streaming fallback request.");
        }

        [Test]
        public async Task GetStreamingResponseAsync_RateLimited429Exhausted_ThrowsRateLimited()
        {
            // Stream 429s twice (initial + retry) AND the fallback completion 429s too:
            // only now the typed RateLimited error surfaces, with no hidden extra rounds.
            // (Deliberately NOT Assert.ThrowsAsync: its sync-over-async wait can deadlock EditMode.)
            RateLimit429ThenSseTransport transport = new(99, "data: [DONE]\n\n")
            {
                NonStreamingBody = null // non-streaming endpoint also answers 429
            };
            MeaiOpenAiChatClient client = new(new DoneSentinelSettings(), transport);

            LlmClientException caught = null;
            try
            {
                await foreach (MEAI.ChatResponseUpdate _ in client.GetStreamingResponseAsync(
                                   new[] { new MEAI.ChatMessage(MEAI.ChatRole.User, "hi") }))
                {
                }
            }
            catch (LlmClientException ex)
            {
                caught = ex;
            }

            Assert.IsNotNull(caught, "Exhausted retries + failed fallback must surface a typed LlmClientException.");
            Assert.AreEqual(LlmErrorCode.RateLimited, caught.ErrorCode);
            Assert.AreEqual(2, transport.StreamOpens,
                "1 initial attempt + 1 retry on the stream.");
            Assert.AreEqual(1, transport.NonStreamingCalls,
                "The fallback completion runs exactly once (zero extra retries), then the error surfaces.");
        }

        [Test]
        public async Task GetStreamingResponseAsync_TransportSendFailsOnce_RetriesAndCompletes()
        {
            const string sse =
                "data: {\"choices\":[{\"delta\":{\"content\":\"hello\"}}]}\n\n" +
                "data: [DONE]\n\n";
            FailSendOnceSseTransport transport = new(sse);
            MeaiOpenAiChatClient client = new(new DoneSentinelSettings(), transport);
            List<string> parts = new();

            await foreach (MEAI.ChatResponseUpdate update in client.GetStreamingResponseAsync(
                               new[] { new MEAI.ChatMessage(MEAI.ChatRole.User, "hi") }))
            {
                if (!string.IsNullOrEmpty(update.Text))
                {
                    parts.Add(update.Text);
                }
            }

            Assert.AreEqual("hello", string.Concat(parts),
                "The stream must complete after retrying a transient transport send failure on open.");
            Assert.AreEqual(2, transport.Calls,
                "A transport send failure on stream-open must trigger exactly one retry (2 opens total).");
        }

        [Test]
        public async Task GetStreamingResponseAsync_SlowConsumerBetweenUpdates_DoesNotTripStallTimeout()
        {
            // The streaming iterator is pull-based: the consumer (MeaiLlmClient) executes tool
            // calls (up to tens of seconds) between MoveNexts. Here the wall time between SSE
            // lines exceeds the 1s transport timeout ONLY because of consumer-side delay, which
            // must not count against the stall budget - the stream is healthy.
            string[] chunks =
            {
                "data: {\"choices\":[{\"delta\":{\"content\":\"A\"}}]}\n\n",
                "data: {\"choices\":[{\"delta\":{\"content\":\"B\"}}]}\n\n",
                "data: [DONE]\n\n"
            };
            MeaiOpenAiChatClient client = new(new ShortTimeoutSettings(), new AsyncChunkedSseTransport(chunks));
            List<string> parts = new();

            await foreach (MEAI.ChatResponseUpdate update in client.GetStreamingResponseAsync(
                               new[] { new MEAI.ChatMessage(MEAI.ChatRole.User, "hi") }))
            {
                if (!string.IsNullOrEmpty(update.Text))
                {
                    parts.Add(update.Text);
                    await Task.Delay(1200); // simulated slow tool execution between MoveNexts
                }
            }

            Assert.AreEqual("AB", string.Concat(parts),
                "A healthy stream with a slow consumer must complete without 'LLM SSE stalled'.");
        }

        [Test]
        public void AccumulateToolCallDeltas_ArgumentsSplitAcrossChunks_Reassemble()
        {
            string[] chunks =
            {
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"function\":{\"name\":\"search\",\"arguments\":\"{\\\"qu\"}}]}}]}",
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"ery\\\":\\\"cat\"}}]}}]}",
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"s\\\"}\"}}]}}]}"
            };

            MEAI.ChatResponseUpdate update = MeaiOpenAiChatClient.AccumulateToolCallDeltasForTests(chunks);

            Assert.IsNotNull(update);
            MEAI.FunctionCallContent call = update.Contents.OfType<MEAI.FunctionCallContent>().Single();
            Assert.AreEqual("search", call.Name);
            Assert.AreEqual("call_1", call.CallId);
            Assert.AreEqual("cats", call.Arguments["query"]);
            Assert.IsFalse(call.Arguments.ContainsKey(MeaiOpenAiChatClient.ToolCallParseErrorKeyForTests));
        }

        [Test]
        public void AccumulateToolCallDeltas_NameInFirstChunk_ArgsInLaterChunks_Reassemble()
        {
            string[] chunks =
            {
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_2\",\"function\":{\"name\":\"add\"}}]}}]}",
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"{\\\"a\\\":1,\"}}]}}]}",
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"\\\"b\\\":2}\"}}]}}]}"
            };

            MEAI.ChatResponseUpdate update = MeaiOpenAiChatClient.AccumulateToolCallDeltasForTests(chunks);

            Assert.IsNotNull(update);
            MEAI.FunctionCallContent call = update.Contents.OfType<MEAI.FunctionCallContent>().Single();
            Assert.AreEqual("add", call.Name);
            Assert.AreEqual("call_2", call.CallId);
            Assert.AreEqual(1L, Convert.ToInt64(call.Arguments["a"]));
            Assert.AreEqual(2L, Convert.ToInt64(call.Arguments["b"]));
        }

        [Test]
        public void AccumulateToolCallDeltas_TwoParallelCalls_BothMaterialize()
        {
            string[] chunks =
            {
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_a\",\"function\":{\"name\":\"alpha\",\"arguments\":\"{\\\"x\\\":\"}}]}}]}",
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":1,\"id\":\"call_b\",\"function\":{\"name\":\"beta\",\"arguments\":\"{\\\"y\\\":\"}}]}}]}",
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"1}\"}}]}}]}",
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":1,\"function\":{\"arguments\":\"2}\"}}]}}]}"
            };

            MEAI.ChatResponseUpdate update = MeaiOpenAiChatClient.AccumulateToolCallDeltasForTests(chunks);

            Assert.IsNotNull(update);
            List<MEAI.FunctionCallContent> calls =
                update.Contents.OfType<MEAI.FunctionCallContent>().ToList();
            Assert.AreEqual(2, calls.Count);

            MEAI.FunctionCallContent alpha = calls.Single(c => c.Name == "alpha");
            Assert.AreEqual("call_a", alpha.CallId);
            Assert.AreEqual(1L, Convert.ToInt64(alpha.Arguments["x"]));

            MEAI.FunctionCallContent beta = calls.Single(c => c.Name == "beta");
            Assert.AreEqual("call_b", beta.CallId);
            Assert.AreEqual(2L, Convert.ToInt64(beta.Arguments["y"]));
        }

        [Test]
        public void AccumulateToolCallDeltas_ReusedIndexWithDifferentId_DoesNotMergeCalls()
        {
            string[] chunks =
            {
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_a\",\"function\":{\"name\":\"alpha\",\"arguments\":\"{\\\"x\\\":1}\"}}]}}]}",
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_b\",\"function\":{\"name\":\"beta\",\"arguments\":\"{\\\"y\\\":2}\"}}]}}]}"
            };

            MEAI.ChatResponseUpdate update = MeaiOpenAiChatClient.AccumulateToolCallDeltasForTests(chunks);

            Assert.IsNotNull(update);
            List<MEAI.FunctionCallContent> calls = update.Contents.OfType<MEAI.FunctionCallContent>().ToList();
            Assert.AreEqual(2, calls.Count);

            MEAI.FunctionCallContent alpha = calls.Single(c => c.CallId == "call_a");
            Assert.AreEqual("alpha", alpha.Name);
            Assert.AreEqual(1L, Convert.ToInt64(alpha.Arguments["x"]));

            MEAI.FunctionCallContent beta = calls.Single(c => c.CallId == "call_b");
            Assert.AreEqual("beta", beta.Name);
            Assert.AreEqual(2L, Convert.ToInt64(beta.Arguments["y"]));
        }

        [Test]
        public void AccumulateToolCallDeltas_MissingIndexWhileAllPendingComplete_DropsFragmentKeepsCalls()
        {
            // Both pending calls already hold COMPLETE argument JSON, so neither can own the
            // id/index-less fragment: it is dropped, and both calls must survive UNPOISONED
            // (previously ALL pending calls were force-failed, breaking parallel tool calling).
            string[] chunks =
            {
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_a\",\"function\":{\"name\":\"alpha\",\"arguments\":\"{\\\"x\\\":1}\"}}]}}]}",
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":1,\"id\":\"call_b\",\"function\":{\"name\":\"beta\",\"arguments\":\"{\\\"y\\\":2}\"}}]}}]}",
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"function\":{\"arguments\":\"unowned\"}}]}}]}"
            };

            MEAI.ChatResponseUpdate update = MeaiOpenAiChatClient.AccumulateToolCallDeltasForTests(chunks);

            Assert.IsNotNull(update);
            List<MEAI.FunctionCallContent> calls = update.Contents.OfType<MEAI.FunctionCallContent>().ToList();
            Assert.AreEqual(2, calls.Count);
            Assert.IsFalse(calls.Any(c => c.Arguments.ContainsKey(MeaiOpenAiChatClient.ToolCallParseErrorKeyForTests)),
                "Calls with complete arguments cannot own the lost fragment and must not be poisoned.");
            Assert.AreEqual(1L, Convert.ToInt64(calls.Single(c => c.Name == "alpha").Arguments["x"]));
            Assert.AreEqual(2L, Convert.ToInt64(calls.Single(c => c.Name == "beta").Arguments["y"]));
        }

        [Test]
        public void AccumulateToolCallDeltas_MissingIndexWithSoleOpenCall_AttributesFragmentToIt()
        {
            // Exactly one pending call still has OPEN argument JSON: the id/index-less fragment can
            // only belong to it, so it must be attributed there and both calls must materialize clean.
            string[] chunks =
            {
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_a\",\"function\":{\"name\":\"alpha\",\"arguments\":\"{\\\"x\\\":1}\"}}]}}]}",
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":1,\"id\":\"call_b\",\"function\":{\"name\":\"beta\",\"arguments\":\"{\\\"y\\\":\"}}]}}]}",
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"function\":{\"arguments\":\"2}\"}}]}}]}"
            };

            MEAI.ChatResponseUpdate update = MeaiOpenAiChatClient.AccumulateToolCallDeltasForTests(chunks);

            Assert.IsNotNull(update);
            List<MEAI.FunctionCallContent> calls = update.Contents.OfType<MEAI.FunctionCallContent>().ToList();
            Assert.AreEqual(2, calls.Count);
            Assert.IsFalse(calls.Any(c => c.Arguments.ContainsKey(MeaiOpenAiChatClient.ToolCallParseErrorKeyForTests)),
                "An unambiguous index-less fragment must be attributed, not poison the pending calls.");
            Assert.AreEqual(1L, Convert.ToInt64(calls.Single(c => c.Name == "alpha").Arguments["x"]));
            Assert.AreEqual(2L, Convert.ToInt64(calls.Single(c => c.Name == "beta").Arguments["y"]),
                "The fragment must complete the sole open call's arguments.");
        }

        [Test]
        public void AccumulateToolCallDeltas_MissingIndexWithMultipleOpenCalls_PoisonsOnlyOpenOnes()
        {
            // Two calls open, one complete: attribution is genuinely ambiguous between the OPEN
            // calls only - they get parse-error markers, the completed call must survive clean.
            string[] chunks =
            {
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_a\",\"function\":{\"name\":\"alpha\",\"arguments\":\"{\\\"x\\\":1}\"}}]}}]}",
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":1,\"id\":\"call_b\",\"function\":{\"name\":\"beta\",\"arguments\":\"{\\\"y\\\":\"}}]}}]}",
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":2,\"id\":\"call_c\",\"function\":{\"name\":\"gamma\",\"arguments\":\"{\\\"z\\\":\"}}]}}]}",
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"function\":{\"arguments\":\"unowned\"}}]}}]}"
            };

            MEAI.ChatResponseUpdate update = MeaiOpenAiChatClient.AccumulateToolCallDeltasForTests(chunks);

            Assert.IsNotNull(update);
            List<MEAI.FunctionCallContent> calls = update.Contents.OfType<MEAI.FunctionCallContent>().ToList();
            Assert.AreEqual(3, calls.Count);
            MEAI.FunctionCallContent alpha = calls.Single(c => c.Name == "alpha");
            Assert.IsFalse(alpha.Arguments.ContainsKey(MeaiOpenAiChatClient.ToolCallParseErrorKeyForTests),
                "The completed call cannot own the fragment and must not be poisoned.");
            Assert.AreEqual(1L, Convert.ToInt64(alpha.Arguments["x"]));
            Assert.IsTrue(calls.Where(c => c.Name != "alpha").All(c =>
                    c.Arguments.ContainsKey(MeaiOpenAiChatClient.ToolCallParseErrorKeyForTests)),
                "Both still-open calls are genuinely ambiguous owners and must surface parse errors.");
        }

        [Test]
        public void AccumulateToolCallDeltas_MalformedJson_SurfacesRawArgsNotSilentlyEmpty()
        {
            string[] chunks =
            {
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_3\",\"function\":{\"name\":\"broken\",\"arguments\":\"{\\\"q\\\":\\\"unclo\"}}]}}]}",
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"sed\"}}]}}]}"
            };

            MEAI.ChatResponseUpdate update = MeaiOpenAiChatClient.AccumulateToolCallDeltasForTests(chunks);

            Assert.IsNotNull(update);
            MEAI.FunctionCallContent call = update.Contents.OfType<MEAI.FunctionCallContent>().Single();
            Assert.AreEqual("broken", call.Name);
            Assert.IsTrue(
                call.Arguments.ContainsKey(MeaiOpenAiChatClient.ToolCallParseErrorKeyForTests),
                "Malformed arguments must surface a parse-error marker rather than silently empty args.");
            Assert.AreEqual(
                "{\"q\":\"unclosed",
                call.Arguments[MeaiOpenAiChatClient.ToolCallRawArgumentsKeyForTests]);
        }

        // --- Execute-as-you-stream: DrainCompleted must surface each call the moment its JSON closes ---

        [Test]
        public void DrainCompleted_WholeCallPerChunk_SurfacesImmediately()
        {
            // A bridge/provider that sends one COMPLETE call per SSE chunk: each call must drain on
            // its own chunk, not wait for the final flush.
            string[] chunks =
            {
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"c1\",\"function\":{\"name\":\"spawn\",\"arguments\":\"{\\\"n\\\":\\\"t1\\\"}\"}}]}}]}",
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":1,\"id\":\"c2\",\"function\":{\"name\":\"spawn\",\"arguments\":\"{\\\"n\\\":\\\"t2\\\"}\"}}]}}]}"
            };

            List<MEAI.ChatResponseUpdate> perChunk = MeaiOpenAiChatClient.DrainPerChunkForTests(chunks);

            Assert.AreEqual(3, perChunk.Count); // one per chunk + final flush
            Assert.IsNotNull(perChunk[0], "First complete call must drain on its own chunk.");
            Assert.AreEqual("t1",
                perChunk[0].Contents.OfType<MEAI.FunctionCallContent>().Single().Arguments["n"]);
            Assert.IsNotNull(perChunk[1], "Second complete call must drain on its own chunk.");
            Assert.AreEqual("t2",
                perChunk[1].Contents.OfType<MEAI.FunctionCallContent>().Single().Arguments["n"]);
            Assert.IsNull(perChunk[2], "Nothing must be left for the final flush.");
        }

        [Test]
        public void DrainCompleted_FragmentedArgs_DrainsOnlyWhenJsonCloses()
        {
            string[] chunks =
            {
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"c1\",\"function\":{\"name\":\"search\",\"arguments\":\"{\\\"qu\"}}]}}]}",
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"ery\\\":\\\"cat\"}}]}}]}",
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"s\\\"}\"}}]}}]}"
            };

            List<MEAI.ChatResponseUpdate> perChunk = MeaiOpenAiChatClient.DrainPerChunkForTests(chunks);

            Assert.IsNull(perChunk[0], "Args still open - must not drain.");
            Assert.IsNull(perChunk[1], "Args still open - must not drain.");
            Assert.IsNotNull(perChunk[2], "Args JSON closed - must drain NOW, not at flush.");
            Assert.AreEqual("cats",
                perChunk[2].Contents.OfType<MEAI.FunctionCallContent>().Single().Arguments["query"]);
            Assert.IsNull(perChunk[3], "Nothing must be left for the final flush.");
        }

        [Test]
        public void DrainCompleted_InterleavedCalls_EarlierOpenCallBlocksLaterClosedCall()
        {
            // Contiguous-prefix contract: a later-indexed call whose JSON closes first must NOT
            // drain (and execute) before an earlier-indexed call that is still open - dependent
            // pairs (create -> configure) rely on provider index order across chunks.
            string[] chunks =
            {
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"ca\",\"function\":{\"name\":\"alpha\",\"arguments\":\"{\\\"x\\\":\"}}]}}]}",
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":1,\"id\":\"cb\",\"function\":{\"name\":\"beta\",\"arguments\":\"{\\\"y\\\":2}\"}}]}}]}",
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"1}\"}}]}}]}"
            };

            List<MEAI.ChatResponseUpdate> perChunk = MeaiOpenAiChatClient.DrainPerChunkForTests(chunks);

            Assert.IsNull(perChunk[0], "alpha still open.");
            Assert.IsNull(perChunk[1],
                "beta is ready but index 0 (alpha) is still open - beta must stay blocked.");
            Assert.IsNotNull(perChunk[2], "alpha closed - the whole ready prefix drains now.");
            List<MEAI.FunctionCallContent> calls =
                perChunk[2].Contents.OfType<MEAI.FunctionCallContent>().ToList();
            Assert.AreEqual(2, calls.Count, "alpha and beta must drain together once alpha closes.");
            Assert.AreEqual("alpha", calls[0].Name, "Drained calls must surface in index order.");
            Assert.AreEqual("beta", calls[1].Name, "Drained calls must surface in index order.");
            Assert.IsNull(perChunk[3], "Nothing must be left for the final flush.");
        }

        [Test]
        public void DrainCompleted_LaterIndexClosesFirst_NothingDrainsUntilPrefixReady()
        {
            // Both calls fragmented; index 1 closes before index 0. Nothing may drain until the
            // earlier index closes, then both drain in ONE update, in index order.
            string[] chunks =
            {
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"ca\",\"function\":{\"name\":\"alpha\",\"arguments\":\"{\\\"x\\\":\"}}]}}]}",
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":1,\"id\":\"cb\",\"function\":{\"name\":\"beta\",\"arguments\":\"{\\\"y\\\":\"}}]}}]}",
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":1,\"function\":{\"arguments\":\"2}\"}}]}}]}",
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"1}\"}}]}}]}"
            };

            List<MEAI.ChatResponseUpdate> perChunk = MeaiOpenAiChatClient.DrainPerChunkForTests(chunks);

            Assert.IsNull(perChunk[0], "alpha still open.");
            Assert.IsNull(perChunk[1], "both still open.");
            Assert.IsNull(perChunk[2],
                "beta closed but alpha (earlier index) is still open - nothing drains.");
            Assert.IsNotNull(perChunk[3], "alpha closed - both drain in one update.");
            List<MEAI.FunctionCallContent> calls =
                perChunk[3].Contents.OfType<MEAI.FunctionCallContent>().ToList();
            Assert.AreEqual(2, calls.Count);
            Assert.AreEqual("alpha", calls[0].Name, "Index order must be preserved.");
            Assert.AreEqual("beta", calls[1].Name, "Index order must be preserved.");
            Assert.IsNull(perChunk[4], "Nothing must be left for the final flush.");
        }

        [Test]
        public void DrainCompleted_CumulativeResendAfterDrain_IsIgnored()
        {
            // Known OpenAI-compat server misbehavior: after a call's arguments closed (and the
            // call drained and EXECUTED), the provider re-sends the full cumulative argument
            // string. That must not create a fresh pending entry and run the call a second time.
            string[] chunks =
            {
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"c1\",\"function\":{\"name\":\"spawn\",\"arguments\":\"{\\\"n\\\":\\\"t1\\\"}\"}}]}}]}",
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"c1\",\"function\":{\"name\":\"spawn\",\"arguments\":\"{\\\"n\\\":\\\"t1\\\"}\"}}]}}]}"
            };

            List<MEAI.ChatResponseUpdate> perChunk = MeaiOpenAiChatClient.DrainPerChunkForTests(chunks);

            Assert.IsNotNull(perChunk[0], "The call drains once, at its close.");
            Assert.AreEqual("t1",
                perChunk[0].Contents.OfType<MEAI.FunctionCallContent>().Single().Arguments["n"]);
            Assert.IsNull(perChunk[1],
                "Cumulative re-send of a drained call must be ignored, not re-drained.");
            Assert.IsNull(perChunk[2], "Flush must emit nothing extra for the ignored re-send.");
        }

        [Test]
        public void DrainCompleted_TrailingEmptyDeltaForDrainedIndex_NoGhostPending()
        {
            // Trailing empty-arguments delta for an already-drained index must not create a ghost
            // pending entry that lingers into Flush.
            string[] chunks =
            {
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"c1\",\"function\":{\"name\":\"spawn\",\"arguments\":\"{\\\"n\\\":\\\"t1\\\"}\"}}]}}]}",
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"\"}}]}}]}"
            };

            List<MEAI.ChatResponseUpdate> perChunk = MeaiOpenAiChatClient.DrainPerChunkForTests(chunks);

            Assert.IsNotNull(perChunk[0], "The call drains at its close.");
            Assert.IsNull(perChunk[1], "Trailing empty delta for a drained index must be ignored.");
            Assert.IsNull(perChunk[2], "Flush must emit nothing - no ghost entry may exist.");
        }

        [Test]
        public void DrainCompleted_MalformedNeverDrains_FlushSurfacesParseError()
        {
            string[] chunks =
            {
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"c1\",\"function\":{\"name\":\"broken\",\"arguments\":\"{\\\"q\\\":\\\"unclo\"}}]}}]}",
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"sed\"}}]}}]}"
            };

            List<MEAI.ChatResponseUpdate> perChunk = MeaiOpenAiChatClient.DrainPerChunkForTests(chunks);

            Assert.IsNull(perChunk[0]);
            Assert.IsNull(perChunk[1], "Unclosed JSON must never drain early.");
            Assert.IsNotNull(perChunk[2], "Flush must still surface the malformed call with markers.");
            MEAI.FunctionCallContent call =
                perChunk[2].Contents.OfType<MEAI.FunctionCallContent>().Single();
            Assert.IsTrue(call.Arguments.ContainsKey(MeaiOpenAiChatClient.ToolCallParseErrorKeyForTests));
        }

        [Test]
        public void IsCompleteJsonObject_Detector_CoversStringsEscapesAndTrailingJunk()
        {
            Assert.IsTrue(MeaiOpenAiChatClient.IsCompleteJsonObjectForTests("{}"));
            Assert.IsTrue(MeaiOpenAiChatClient.IsCompleteJsonObjectForTests("  {\"a\": {\"b\": \"}\"}} \n"));
            Assert.IsTrue(MeaiOpenAiChatClient.IsCompleteJsonObjectForTests("{\"a\": \"x\\\"}\"}"));
            Assert.IsFalse(MeaiOpenAiChatClient.IsCompleteJsonObjectForTests(""));
            Assert.IsFalse(MeaiOpenAiChatClient.IsCompleteJsonObjectForTests("{\"a\": 1"));
            Assert.IsFalse(MeaiOpenAiChatClient.IsCompleteJsonObjectForTests("{\"a\": \"un"));
            Assert.IsFalse(MeaiOpenAiChatClient.IsCompleteJsonObjectForTests("{} extra"));
            Assert.IsFalse(MeaiOpenAiChatClient.IsCompleteJsonObjectForTests("[1,2]"));
        }

        private sealed class DoneSentinelTransport : IOpenAiHttpTransport
        {
            private readonly string _sse;

            public DoneSentinelTransport(string sse)
            {
                _sse = sse;
            }

            public string DebugLabel => "DoneSentinel";
            public bool SupportsSseStreaming => true;

            public Task<OpenAiHttpPostResult> PostNonStreamingAsync(OpenAiHttpPostRequest request,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<OpenAiHttpSseOpenResult> OpenSseResponseStreamAsync(OpenAiHttpPostRequest request,
                CancellationToken cancellationToken = default)
            {
                OpenAiHttpSseOpenResult result = new()
                {
                    StatusCode = 200,
                    ResponseHeaders = new Dictionary<string, IEnumerable<string>>
                    {
                        { "Content-Type", new[] { "text/event-stream" } }
                    }
                };

                return Task.FromResult(result.WithRawStream(new ThrowsAfterPayloadStream(_sse)));
            }
        }

        /// <summary>
        /// Streams always contain ONLY keep-alive comments (a starved upstream behind a proxy: SSE 200,
        /// zero data lines), while the plain completion endpoint answers normally — models the
        /// empty-stream → non-streaming fallback.
        /// </summary>
        private sealed class EmptyStreamThenNonStreamingTransport : IOpenAiHttpTransport
        {
            public int StreamOpens;
            public int NonStreamingCalls;

            public string DebugLabel => "EmptyStreamFallback";
            public bool SupportsSseStreaming => true;

            public Task<OpenAiHttpPostResult> PostNonStreamingAsync(OpenAiHttpPostRequest request,
                CancellationToken cancellationToken = default)
            {
                NonStreamingCalls++;
                return Task.FromResult(new OpenAiHttpPostResult
                {
                    StatusCode = 200,
                    BodyText =
                        "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"fallback answer\"}}]}",
                    ResponseHeaders = new Dictionary<string, IEnumerable<string>>()
                });
            }

            public Task<OpenAiHttpSseOpenResult> OpenSseResponseStreamAsync(OpenAiHttpPostRequest request,
                CancellationToken cancellationToken = default)
            {
                StreamOpens++;
                OpenAiHttpSseOpenResult result = new()
                {
                    StatusCode = 200,
                    ResponseHeaders = new Dictionary<string, IEnumerable<string>>
                    {
                        { "Content-Type", new[] { "text/event-stream" } }
                    }
                };

                // Only comment keep-alives and the DONE sentinel — zero parsed deltas.
                return Task.FromResult(result.WithRawStream(
                    new ThrowsAfterPayloadStream(": keep-alive\n\n: keep-alive\n\ndata: [DONE]\n\n")));
            }
        }

        /// <summary>
        /// Streams NEVER close and never send a data line — an endless drip of ": keep-alive" SSE
        /// comments (a proxy holding a starved upstream connection open). Only the starved-stream
        /// watchdog can end an attempt. The plain completion endpoint answers normally.
        /// </summary>
        private sealed class EndlessKeepAliveTransport : IOpenAiHttpTransport
        {
            public int StreamOpens;
            public int NonStreamingCalls;

            public string DebugLabel => "EndlessKeepAlive";
            public bool SupportsSseStreaming => true;

            public Task<OpenAiHttpPostResult> PostNonStreamingAsync(OpenAiHttpPostRequest request,
                CancellationToken cancellationToken = default)
            {
                NonStreamingCalls++;
                return Task.FromResult(new OpenAiHttpPostResult
                {
                    StatusCode = 200,
                    BodyText =
                        "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"fallback answer\"}}]}",
                    ResponseHeaders = new Dictionary<string, IEnumerable<string>>()
                });
            }

            public Task<OpenAiHttpSseOpenResult> OpenSseResponseStreamAsync(OpenAiHttpPostRequest request,
                CancellationToken cancellationToken = default)
            {
                StreamOpens++;
                OpenAiHttpSseOpenResult result = new()
                {
                    StatusCode = 200,
                    ResponseHeaders = new Dictionary<string, IEnumerable<string>>
                    {
                        { "Content-Type", new[] { "text/event-stream" } }
                    }
                };

                return Task.FromResult(result.WithRawStream(new EndlessKeepAliveStream()));
            }
        }

        private sealed class EndlessKeepAliveStream : Stream
        {
            private static readonly byte[] KeepAlive = Encoding.UTF8.GetBytes(": keep-alive\n\n");

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count,
                CancellationToken cancellationToken)
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                return Read(buffer, offset, count);
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                int toCopy = Math.Min(count, KeepAlive.Length);
                Array.Copy(KeepAlive, 0, buffer, offset, toCopy);
                return toCopy;
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }
        }

        /// <summary>
        /// Rejects the first N stream-opens with HTTP 429 (Retry-After: 0.001 so tests stay fast),
        /// then serves a normal SSE stream — models free-tier burst rate limiting.
        /// </summary>
        private sealed class RateLimit429ThenSseTransport : IOpenAiHttpTransport
        {
            private readonly int _failures;
            private readonly string _sse;

            public RateLimit429ThenSseTransport(int failures, string sse)
            {
                _failures = failures;
                _sse = sse;
            }

            public int StreamOpens { get; private set; }
            public int NonStreamingCalls { get; private set; }

            /// <summary>Non-streaming response body; null = the endpoint answers HTTP 429 as well.</summary>
            public string NonStreamingBody { get; set; }

            public string DebugLabel => "RateLimit429";
            public bool SupportsSseStreaming => true;

            public Task<OpenAiHttpPostResult> PostNonStreamingAsync(OpenAiHttpPostRequest request,
                CancellationToken cancellationToken = default)
            {
                NonStreamingCalls++;
                if (NonStreamingBody == null)
                {
                    return Task.FromResult(new OpenAiHttpPostResult
                    {
                        StatusCode = 429,
                        BodyText = "{\"error\":{\"message\":\"rate-limited upstream\",\"code\":429}}",
                        ResponseHeaders = new Dictionary<string, IEnumerable<string>>
                        {
                            { "Retry-After", new[] { "0.001" } }
                        }
                    });
                }

                return Task.FromResult(new OpenAiHttpPostResult
                {
                    StatusCode = 200,
                    BodyText = NonStreamingBody,
                    ResponseHeaders = new Dictionary<string, IEnumerable<string>>()
                });
            }

            public Task<OpenAiHttpSseOpenResult> OpenSseResponseStreamAsync(OpenAiHttpPostRequest request,
                CancellationToken cancellationToken = default)
            {
                StreamOpens++;
                if (StreamOpens <= _failures)
                {
                    OpenAiHttpSseOpenResult limited = new()
                    {
                        StatusCode = 429,
                        ErrorBodyText = "{\"error\":{\"message\":\"rate-limited upstream\",\"code\":429}}",
                        ResponseHeaders = new Dictionary<string, IEnumerable<string>>
                        {
                            { "Retry-After", new[] { "0.001" } },
                            { "Content-Type", new[] { "application/json" } }
                        }
                    };
                    return Task.FromResult(limited);
                }

                OpenAiHttpSseOpenResult result = new()
                {
                    StatusCode = 200,
                    ResponseHeaders = new Dictionary<string, IEnumerable<string>>
                    {
                        { "Content-Type", new[] { "text/event-stream" } }
                    }
                };
                return Task.FromResult(result.WithRawStream(new ThrowsAfterPayloadStream(_sse)));
            }
        }

        /// <summary>
        /// Fails the FIRST stream-open with a transport send error (as a stale pooled keep-alive
        /// connection does), then serves a normal SSE stream — models the bounded transport-send retry.
        /// </summary>
        private sealed class FailSendOnceSseTransport : IOpenAiHttpTransport
        {
            private readonly string _sse;
            private int _calls;

            public FailSendOnceSseTransport(string sse)
            {
                _sse = sse;
            }

            public int Calls => _calls;
            public string DebugLabel => "FailSendOnce";
            public bool SupportsSseStreaming => true;

            public Task<OpenAiHttpPostResult> PostNonStreamingAsync(OpenAiHttpPostRequest request,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<OpenAiHttpSseOpenResult> OpenSseResponseStreamAsync(OpenAiHttpPostRequest request,
                CancellationToken cancellationToken = default)
            {
                _calls++;
                if (_calls == 1)
                {
                    throw new System.Net.Http.HttpRequestException(
                        "An error occurred while sending the request");
                }

                OpenAiHttpSseOpenResult result = new()
                {
                    StatusCode = 200,
                    ResponseHeaders = new Dictionary<string, IEnumerable<string>>
                    {
                        { "Content-Type", new[] { "text/event-stream" } }
                    }
                };

                return Task.FromResult(result.WithRawStream(new ThrowsAfterPayloadStream(_sse)));
            }
        }

        private sealed class AsyncChunkedSseTransport : IOpenAiHttpTransport
        {
            private readonly IReadOnlyList<string> _chunks;

            public AsyncChunkedSseTransport(IReadOnlyList<string> chunks)
            {
                _chunks = chunks;
            }

            public string DebugLabel => "AsyncChunked";
            public bool SupportsSseStreaming => true;

            public Task<OpenAiHttpPostResult> PostNonStreamingAsync(OpenAiHttpPostRequest request,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<OpenAiHttpSseOpenResult> OpenSseResponseStreamAsync(OpenAiHttpPostRequest request,
                CancellationToken cancellationToken = default)
            {
                OpenAiHttpSseOpenResult result = new()
                {
                    StatusCode = 200,
                    ResponseHeaders = new Dictionary<string, IEnumerable<string>>
                    {
                        { "Content-Type", new[] { "text/event-stream" } }
                    }
                };

                return Task.FromResult(result.WithRawStream(new AsyncChunkedReadStream(_chunks)));
            }
        }

        private sealed class AsyncChunkedReadStream : Stream
        {
            private readonly Queue<byte[]> _chunks;

            public AsyncChunkedReadStream(IEnumerable<string> chunks)
            {
                _chunks = new Queue<byte[]>(chunks.Select(Encoding.UTF8.GetBytes));
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count,
                CancellationToken cancellationToken)
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                return Read(buffer, offset, count);
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_chunks.Count == 0)
                {
                    return 0;
                }

                byte[] chunk = _chunks.Dequeue();
                int toCopy = Math.Min(count, chunk.Length);
                Array.Copy(chunk, 0, buffer, offset, toCopy);
                return toCopy;
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }
        }

        private sealed class ThrowsAfterPayloadStream : Stream
        {
            private readonly byte[] _payload;
            private int _position;

            public ThrowsAfterPayloadStream(string payload)
            {
                _payload = Encoding.UTF8.GetBytes(payload);
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _payload.Length;

            public override long Position
            {
                get => _position;
                set => throw new NotSupportedException();
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count,
                CancellationToken cancellationToken)
            {
                return Task.FromResult(Read(buffer, offset, count));
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_position >= _payload.Length)
                {
                    throw new AssertionException("Client should stop on data: [DONE] without waiting for stream EOF.");
                }

                int toCopy = Math.Min(count, _payload.Length - _position);
                Array.Copy(_payload, _position, buffer, offset, toCopy);
                _position += toCopy;
                return toCopy;
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }
        }

        [Test]
        public void BuildMessagesPayload_ToolMessageWithMultipleResults_EmitsOneWireMessagePerResult()
        {
            // A Tool-role MEAI message carrying the whole turn's results must expand to one
            // OpenAI tool message per tool_call_id — serializing only the first result made
            // models re-issue the "unanswered" calls every round-trip (5 spawns became 15).
            List<MEAI.ChatMessage> msgs = new()
            {
                new MEAI.ChatMessage(MEAI.ChatRole.Assistant, new List<MEAI.AIContent>
                {
                    new MEAI.FunctionCallContent("call_1", "world_command",
                        new Dictionary<string, object?> { ["action"] = "spawn" }),
                    new MEAI.FunctionCallContent("call_2", "world_command",
                        new Dictionary<string, object?> { ["action"] = "spawn" }),
                    new MEAI.FunctionCallContent("call_3", "world_command",
                        new Dictionary<string, object?> { ["action"] = "spawn" })
                }),
                new MEAI.ChatMessage(MEAI.ChatRole.Tool, new List<MEAI.AIContent>
                {
                    new MEAI.FunctionResultContent("call_1", "ok-1"),
                    new MEAI.FunctionResultContent("call_2", "ok-2"),
                    new MEAI.FunctionResultContent("call_3", "ok-3")
                })
            };

            List<Dictionary<string, object>> payload =
                MeaiOpenAiChatClient.BuildMessagesPayloadForTests(msgs);

            List<Dictionary<string, object>> toolMessages =
                payload.Where(m => (string)m["role"] == "tool").ToList();
            Assert.AreEqual(3, toolMessages.Count,
                "Every FunctionResultContent must become its own tool-role wire message.");
            CollectionAssert.AreEqual(
                new[] { "call_1", "call_2", "call_3" },
                toolMessages.Select(m => (string)m["tool_call_id"]).ToArray());
            CollectionAssert.AreEqual(
                new[] { "ok-1", "ok-2", "ok-3" },
                toolMessages.Select(m => (string)m["content"]).ToArray());

            Dictionary<string, object> assistant =
                payload.First(m => (string)m["role"] == "assistant");
            Assert.AreEqual(3,
                ((List<Dictionary<string, object>>)assistant["tool_calls"]).Count,
                "All three calls must survive in the assistant message.");
        }

        [Test]
        public void BuildMessagesPayload_MissingCallId_UsesSameDeterministicIdOnEchoAndReply()
        {
            // F8: a call without a provider id must NOT get a random Guid in the assistant echo
            // while its tool reply omits tool_call_id - both sides must share one deterministic
            // synthetic id, or the model sees an unanswered call and re-issues it.
            List<MEAI.ChatMessage> msgs = new()
            {
                new MEAI.ChatMessage(MEAI.ChatRole.Assistant, new List<MEAI.AIContent>
                {
                    new MEAI.FunctionCallContent("call_real", "world_command",
                        new Dictionary<string, object?> { ["action"] = "spawn" }),
                    new MEAI.FunctionCallContent("", "world_command",
                        new Dictionary<string, object?> { ["action"] = "move" })
                }),
                new MEAI.ChatMessage(MEAI.ChatRole.Tool, new List<MEAI.AIContent>
                {
                    new MEAI.FunctionResultContent("call_real", "ok-real"),
                    new MEAI.FunctionResultContent("", "ok-synth")
                })
            };

            List<Dictionary<string, object>> payload =
                MeaiOpenAiChatClient.BuildMessagesPayloadForTests(msgs);

            Dictionary<string, object> assistant = payload.First(m => (string)m["role"] == "assistant");
            List<Dictionary<string, object>> toolCalls =
                (List<Dictionary<string, object>>)assistant["tool_calls"];
            Assert.AreEqual("call_real", (string)toolCalls[0]["id"]);
            string syntheticId = (string)toolCalls[1]["id"];
            Assert.IsNotEmpty(syntheticId, "The id-less call must still get an id in the echo");
            StringAssert.StartsWith("synth_", syntheticId, "Synthetic ids are deterministic, never random");

            List<Dictionary<string, object>> toolMessages =
                payload.Where(m => (string)m["role"] == "tool").ToList();
            Assert.AreEqual(2, toolMessages.Count);
            Assert.AreEqual("call_real", (string)toolMessages[0]["tool_call_id"]);
            Assert.IsTrue(toolMessages[1].ContainsKey("tool_call_id"),
                "The reply to the id-less call must carry the synthetic id, not omit it");
            Assert.AreEqual(syntheticId, (string)toolMessages[1]["tool_call_id"],
                "Echo and reply must use the SAME synthetic id");

            // Determinism: serializing the same history again yields the same synthetic id.
            List<Dictionary<string, object>> payloadAgain =
                MeaiOpenAiChatClient.BuildMessagesPayloadForTests(msgs);
            Dictionary<string, object> assistantAgain =
                payloadAgain.First(m => (string)m["role"] == "assistant");
            Assert.AreEqual(syntheticId,
                (string)((List<Dictionary<string, object>>)assistantAgain["tool_calls"])[1]["id"],
                "Synthetic ids must be stable across serializations of the same history");
        }

        [Test]
        public void BuildMessagesPayload_ParseErrorCall_EchoesRawArgumentsString()
        {
            // F9: a parse-error call's arguments carry internal marker keys; the assistant echo must
            // emit the model's ORIGINAL raw argument string, never the marker dictionary.
            const string rawArgs = "{\"action\":\"spawn\",\"prefab\":\"tre"; // truncated mid-stream
            List<MEAI.ChatMessage> msgs = new()
            {
                new MEAI.ChatMessage(MEAI.ChatRole.Assistant, new List<MEAI.AIContent>
                {
                    new MEAI.FunctionCallContent("call_1", "world_command",
                        new Dictionary<string, object?>
                        {
                            [ToolCallArgumentMarkers.RawArgumentsKey] = rawArgs,
                            [ToolCallArgumentMarkers.ParseErrorKey] = true
                        })
                }),
                new MEAI.ChatMessage(MEAI.ChatRole.Tool, new List<MEAI.AIContent>
                {
                    new MEAI.FunctionResultContent("call_1", "Error: arguments JSON was truncated")
                })
            };

            List<Dictionary<string, object>> payload =
                MeaiOpenAiChatClient.BuildMessagesPayloadForTests(msgs);

            Dictionary<string, object> assistant = payload.First(m => (string)m["role"] == "assistant");
            Dictionary<string, object> call =
                ((List<Dictionary<string, object>>)assistant["tool_calls"])[0];
            string arguments = (string)((Dictionary<string, object>)call["function"])["arguments"];
            Assert.AreEqual(rawArgs, arguments, "The raw argument string must be echoed verbatim");
            StringAssert.DoesNotContain(ToolCallArgumentMarkers.RawArgumentsKey, arguments,
                "Internal marker keys must never leak onto the wire");
            StringAssert.DoesNotContain(ToolCallArgumentMarkers.ParseErrorKey, arguments);
        }

        private sealed class DoneSentinelSettings : IOpenAiHttpSettings
        {
            public string ApiBaseUrl => "https://example.invalid/v1";
            public string ApiKey => "";
            public string AuthorizationHeader => "";
            public string Model => "dummy";
            public float Temperature => 0f;
            public int RequestTimeoutSeconds => 30;
            public int MaxTokens => 256;
            public bool LogLlmInput => false;
            public bool LogLlmOutput => false;
            public bool EnableHttpDebugLogging => false;
            public IRequestHeaderProvider? HeaderProvider => null;
        }

        /// <summary>1-second transport timeout so consumer delays longer than the timeout are cheap to simulate.</summary>
        private sealed class ShortTimeoutSettings : IOpenAiHttpSettings
        {
            public string ApiBaseUrl => "https://example.invalid/v1";
            public string ApiKey => "";
            public string AuthorizationHeader => "";
            public string Model => "dummy";
            public float Temperature => 0f;
            public int RequestTimeoutSeconds => 1;
            public int MaxTokens => 256;
            public bool LogLlmInput => false;
            public bool LogLlmOutput => false;
            public bool EnableHttpDebugLogging => false;
            public IRequestHeaderProvider? HeaderProvider => null;
        }
    }
}
#endif
