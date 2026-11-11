using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Infrastructure.Llm;
using CoreAI.Logging;
using MEAI = Microsoft.Extensions.AI;

namespace CoreAI.PerfBench
{
    /// <summary>
    /// The streamed turn loop of <see cref="MeaiOpenAiChatClient.GetStreamingResponseAsync"/> per token and per
    /// tool-call fragment, over an in-memory SSE body (no network): request build, transport open, line
    /// framing, the per-line parse, timeout bookkeeping, tool-call argument assembly and the usage chunk.
    /// </summary>
    internal static class StreamingCases
    {
        public const int TextTokens = 200;
        public const int ArgumentFragments = 100;

        public static void Add(List<MicroCase> cases)
        {
            byte[] textTurn = Encoding.UTF8.GetBytes(BuildTextTurn(TextTokens));
            cases.Add(new MicroCase("llm-turn", "streamed turn, " + TextTokens + " content tokens + finish + usage", ops =>
            {
                long updates = 0;
                for (int i = 0; i < ops; i++)
                {
                    updates += Drain(textTurn);
                }

                return updates;
            }, "one op = one turn; divide by " + TextTokens + " for the per-token cost of the whole loop"));

            cases.Add(new MicroCase("llm-turn", "streamed turn, " + TextTokens + " content tokens, one SSE event per read", ops =>
            {
                long updates = 0;
                for (int i = 0; i < ops; i++)
                {
                    updates += Drain(textTurn, EventSizedReads);
                }

                return updates;
            }, "as a network delivers tokens: every ReadAsync returns at most one event, so per-read costs become per-token costs"));

            byte[] toolTurn = Encoding.UTF8.GetBytes(BuildToolTurn(ArgumentFragments));
            cases.Add(new MicroCase("llm-turn", "streamed tool call, " + ArgumentFragments + " argument fragments", ops =>
            {
                long updates = 0;
                for (int i = 0; i < ops; i++)
                {
                    updates += Drain(toolTurn);
                }

                return updates;
            }, "one op = one turn with one tool call whose arguments arrive in " + ArgumentFragments + " fragments"));
        }

        /// <summary>Largest read the event-sized variant returns: one content event of the text turn.</summary>
        private const int EventSizedReads = 200;

        private static long Drain(byte[] body, int maxRead = int.MaxValue)
        {
            using MeaiOpenAiChatClient client = new(Options, new MemoryTransport(body, maxRead), NullLog.Instance);
            List<MEAI.ChatMessage> messages = new() { new MEAI.ChatMessage(MEAI.ChatRole.User, "hi") };
            IAsyncEnumerator<MEAI.ChatResponseUpdate> stream = client.GetStreamingResponseAsync(messages, null,
                CancellationToken.None).GetAsyncEnumerator();
            long count = 0;
            try
            {
                while (stream.MoveNextAsync().AsTask().GetAwaiter().GetResult())
                {
                    count++;
                }
            }
            finally
            {
                stream.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            return count;
        }

        private static readonly OpenAiHttpOptions Options = new()
        {
            UseOpenAiCompatibleHttp = true,
            ApiBaseUrl = "http://perfbench.invalid/v1",
            Model = "perf-model",
            LogLlmInput = false,
            LogLlmOutput = false
        };

        private static string BuildTextTurn(int tokens)
        {
            StringBuilder body = new();
            body.Append("data: {\"id\":\"chatcmpl-perf\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"perf-model\",")
                .Append("\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"\"},\"finish_reason\":null}]}\n\n");
            for (int index = 0; index < tokens; index++)
            {
                body.Append("data: {\"id\":\"chatcmpl-perf\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"perf-model\",")
                    .Append("\"choices\":[{\"index\":0,\"delta\":{\"content\":\" tok").Append(index)
                    .Append("\"},\"logprobs\":null,\"finish_reason\":null}]}\n\n");
            }

            body.Append("data: {\"id\":\"chatcmpl-perf\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n");
            body.Append("data: {\"id\":\"chatcmpl-perf\",\"choices\":[],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":")
                .Append(tokens).Append(",\"total_tokens\":").Append(tokens + 10).Append("}}\n\n");
            body.Append("data: [DONE]\n\n");
            return body.ToString();
        }

        private static string BuildToolTurn(int fragments)
        {
            StringBuilder body = new();
            body.Append("data: {\"id\":\"chatcmpl-tool\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"tool_calls\":")
                .Append("[{\"index\":0,\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"spawn\",\"arguments\":\"\"}}]}}]}\n\n");
            body.Append("data: {\"id\":\"chatcmpl-tool\",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,")
                .Append("\"function\":{\"arguments\":\"{\\\"items\\\":[\"}}]}}]}\n\n");
            for (int index = 0; index < fragments; index++)
            {
                body.Append("data: {\"id\":\"chatcmpl-tool\",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,")
                    .Append("\"function\":{\"arguments\":\"").Append(index == 0 ? "" : ",").Append(index).Append("\"}}]}}]}\n\n");
            }

            body.Append("data: {\"id\":\"chatcmpl-tool\",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,")
                .Append("\"function\":{\"arguments\":\"]}\"}}]}}]}\n\n");
            body.Append("data: {\"id\":\"chatcmpl-tool\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n");
            body.Append("data: [DONE]\n\n");
            return body.ToString();
        }

        private sealed class MemoryTransport : IOpenAiHttpTransport
        {
            private readonly byte[] _body;
            private readonly int _maxRead;

            public MemoryTransport(byte[] body, int maxRead)
            {
                _body = body;
                _maxRead = maxRead;
            }

            public string DebugLabel => "perfbench-memory";

            public bool SupportsSseStreaming => true;

            public Task<OpenAiHttpPostResult> PostNonStreamingAsync(OpenAiHttpPostRequest request,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException("the streaming benchmark never posts non-streaming");
            }

            public Task<OpenAiHttpSseOpenResult> OpenSseResponseStreamAsync(OpenAiHttpPostRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new OpenAiHttpSseOpenResult { StatusCode = 200 }
                    .WithRawStream(new CappedReadStream(new MemoryStream(_body, false), _maxRead)));
            }
        }

        /// <summary>A read-only stream whose reads return at most a fixed number of bytes, like a network body.</summary>
        private sealed class CappedReadStream : Stream
        {
            private readonly Stream _inner;
            private readonly int _maxRead;

            public CappedReadStream(Stream inner, int maxRead)
            {
                _inner = inner;
                _maxRead = maxRead;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _inner.Length;

            public override long Position
            {
                get => _inner.Position;
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                return _inner.Read(buffer, offset, Math.Min(count, _maxRead));
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                return _inner.ReadAsync(buffer, offset, Math.Min(count, _maxRead), cancellationToken);
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

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _inner.Dispose();
                }

                base.Dispose(disposing);
            }
        }
    }
}
