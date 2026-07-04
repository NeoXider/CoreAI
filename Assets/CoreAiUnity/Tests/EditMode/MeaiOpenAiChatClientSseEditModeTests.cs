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
        public void ParseSseDataLine_ReasoningOnly_DoesNotEmitAssistantText()
        {
            const string json = "{\"choices\":[{\"delta\":{\"reasoning_content\":\"think\"}}]}";
            MEAI.ChatResponseUpdate u = MeaiOpenAiChatClient.ParseSseDataLineForTests(json);
            Assert.IsNull(u);
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
        public void ParseSseDataLine_ReasoningAndContent_EmitsOnlyContent()
        {
            const string json = "{\"choices\":[{\"delta\":{\"reasoning_content\":\"x\",\"content\":\"out\"}}]}";
            MEAI.ChatResponseUpdate u = MeaiOpenAiChatClient.ParseSseDataLineForTests(json);
            Assert.IsNotNull(u);
            Assert.AreEqual("out", u.Text);
        }

        [Test]
        public void ParseCompletion_EmptyContent_DoesNotExposeReasoningContent()
        {
            const string json =
                "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"\",\"reasoning_content\":\"Hello from reasoning\"}}]}";
            MEAI.ChatResponse r = MeaiOpenAiChatClient.ParseResponse(json);
            Assert.AreEqual("", r.Text);
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
        public void ParseCompletion_EmptyContent_DoesNotExposeReasoningContent_CamelCase()
        {
            const string json =
                "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"\",\"reasoningContent\":\"Hello camel\"}}]}";
            MEAI.ChatResponse r = MeaiOpenAiChatClient.ParseResponse(json);
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
        public async Task GetStreamingResponseAsync_RateLimited429Twice_RetriesAndCompletes()
        {
            // Free-tier providers (OpenRouter :free and similar) reject bursts with 429 routinely;
            // the next window usually accepts. Two bounded rate-limit retries must absorb that
            // instead of surfacing "Error: HTTP error 429" to the player on the first hit.
            const string sse =
                "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\n" +
                "data: [DONE]\n\n";
            RateLimit429ThenSseTransport transport = new(failures: 2, sse: sse);
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
                "The stream must complete after two 429s absorbed by rate-limit retries.");
            Assert.AreEqual(3, transport.StreamOpens,
                "Two 429 responses must consume exactly the two rate-limit retries (3 opens total).");
        }

        [Test]
        public async Task GetStreamingResponseAsync_RateLimited429Exhausted_ThrowsRateLimited()
        {
            // Three 429s in a row: both retries consumed, the typed RateLimited error must surface.
            // (Deliberately NOT Assert.ThrowsAsync: its sync-over-async wait can deadlock EditMode.)
            RateLimit429ThenSseTransport transport = new(failures: 99, sse: "data: [DONE]\n\n");
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

            Assert.IsNotNull(caught, "Exhausted rate-limit retries must surface a typed LlmClientException.");
            Assert.AreEqual(LlmErrorCode.RateLimited, caught.ErrorCode);
            Assert.AreEqual(3, transport.StreamOpens,
                "1 initial attempt + 2 rate-limit retries, then the typed error.");
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
        public void AccumulateToolCallDeltas_MissingIndexWithoutIdWhileMultiplePending_MarksParseError()
        {
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
            Assert.IsTrue(calls.All(c => c.Arguments.ContainsKey(MeaiOpenAiChatClient.ToolCallParseErrorKeyForTests)));
            Assert.IsTrue(calls.All(c => c.Arguments.ContainsKey(MeaiOpenAiChatClient.ToolCallRawArgumentsKeyForTests)));
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
            public string DebugLabel => "RateLimit429";
            public bool SupportsSseStreaming => true;

            public Task<OpenAiHttpPostResult> PostNonStreamingAsync(OpenAiHttpPostRequest request,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
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
