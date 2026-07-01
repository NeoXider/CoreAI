#if !COREAI_NO_LLM
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
    }
}
#endif
