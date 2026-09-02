using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using MEAI = Microsoft.Extensions.AI;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// G11 browser finding (2026-09-02): a single retryable 503 on stream-open left the WebGL player on
    /// the typing indicator forever because the retry backoff was a <see cref="Task.Delay"/>, which
    /// never completes on the single-threaded browser runtime. The client must schedule every retry
    /// backoff and the stream idle timeout through the host's <see cref="ILlmAsyncMarshaler"/>.
    /// </summary>
    [Category("Llm")]
    public sealed class MeaiOpenAiChatClientWebGlDelayEditModeTests
    {
        private ILlmAsyncMarshaler _previousDefault;

        [SetUp]
        public void SetUp()
        {
            _previousDefault = MeaiOpenAiChatClient.DefaultAsyncMarshaler;
            MeaiOpenAiChatClient.DefaultAsyncMarshaler = null;
        }

        [TearDown]
        public void TearDown()
        {
            MeaiOpenAiChatClient.DefaultAsyncMarshaler = _previousDefault;
        }

        [Test]
        public async Task TransientStreamOpen503_RetriesThroughInjectedMarshaler_AndAnswers()
        {
            CountingMarshaler marshaler = new();
            FailOnceThenAnswerTransport transport = new();
            MeaiOpenAiChatClient client = new(new Settings(), transport, null, marshaler);

            string text = await CollectAsync(client);

            Assert.AreEqual("hello", text);
            Assert.AreEqual(2, transport.StreamOpens, "the 503 must be followed by exactly one retry");
            Assert.GreaterOrEqual(marshaler.DelayCalls, 1,
                "the retry backoff must be scheduled by the host marshaler, never by Task.Delay");
        }

        [Test]
        public async Task TransientStreamOpen503_WithoutExplicitMarshaler_UsesHostDefault()
        {
            CountingMarshaler marshaler = new();
            MeaiOpenAiChatClient.DefaultAsyncMarshaler = marshaler;
            FailOnceThenAnswerTransport transport = new();
            MeaiOpenAiChatClient client = new(new Settings(), transport);

            string text = await CollectAsync(client);

            Assert.AreEqual("hello", text);
            Assert.GreaterOrEqual(marshaler.DelayCalls, 1,
                "clients built by the static factories must still back off through the host default");
        }

        [Test]
        public void ChatClientSource_KeepsTaskDelayOnlyInThePortableFallback()
        {
            string path = Path.Combine(Directory.GetCurrentDirectory(),
                "Assets/CoreAI/Runtime/Core/Features/Llm/MeaiOpenAiChatClient.cs");
            string[] lines = File.ReadAllLines(path);
            List<int> hits = new();
            for (int index = 0; index < lines.Length; index++)
            {
                string line = lines[index].Trim();
                if (line.Contains("Task.Delay(") && !line.StartsWith("//") && !line.StartsWith("///") &&
                    !line.StartsWith("*"))
                {
                    hits.Add(index);
                }
            }

            Assert.AreEqual(2, hits.Count,
                "only the non-WebGL #else poll and the HostDelayAsync fallback may call Task.Delay; " +
                "hits at lines " + string.Join(", ", hits.ConvertAll(i => (i + 1).ToString())));
            Assert.IsTrue(EnclosedBy(lines, hits[0], "#else") || EnclosedBy(lines, hits[1], "#else"),
                "one Task.Delay must live in the non-WebGL #else branch");
            Assert.IsTrue(EnclosedBy(lines, hits[0], "HostDelayAsync(") ||
                          EnclosedBy(lines, hits[1], "HostDelayAsync("),
                "one Task.Delay must be the HostDelayAsync fallback");
        }

        private static bool EnclosedBy(string[] lines, int index, string marker)
        {
            for (int back = index; back >= Math.Max(0, index - 8); back--)
            {
                if (lines[back].Contains(marker))
                {
                    return true;
                }
            }

            return false;
        }

        private static async Task<string> CollectAsync(MeaiOpenAiChatClient client)
        {
            StringBuilder text = new();
            await foreach (MEAI.ChatResponseUpdate update in client.GetStreamingResponseAsync(
                               new[] { new MEAI.ChatMessage(MEAI.ChatRole.User, "hi") }))
            {
                if (!string.IsNullOrEmpty(update.Text))
                {
                    text.Append(update.Text);
                }
            }

            return text.ToString();
        }

        private sealed class CountingMarshaler : ILlmAsyncMarshaler
        {
            public int DelayCalls;

            public Task<T> InvokeAsync<T>(Func<Task<T>> factory, CancellationToken cancellationToken)
            {
                return factory();
            }

            public Task DelayAsync(int milliseconds, CancellationToken cancellationToken)
            {
                DelayCalls++;
                return Task.CompletedTask;
            }
        }

        private sealed class FailOnceThenAnswerTransport : IOpenAiHttpTransport
        {
            public int StreamOpens;

            public string DebugLabel => "FailOnceThenAnswer";
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
                if (StreamOpens == 1)
                {
                    OpenAiHttpSseOpenResult failure = new()
                    {
                        StatusCode = 503,
                        ResponseHeaders = new Dictionary<string, IEnumerable<string>>
                        {
                            { "Content-Type", new[] { "application/json" } }
                        }
                    };
                    return Task.FromResult(failure.WithRawStream(new MemoryStream(Encoding.UTF8.GetBytes(
                        "{\"error\":{\"message\":\"injected 503\",\"code\":\"service_unavailable\"}}"))));
                }

                OpenAiHttpSseOpenResult success = new()
                {
                    StatusCode = 200,
                    ResponseHeaders = new Dictionary<string, IEnumerable<string>>
                    {
                        { "Content-Type", new[] { "text/event-stream" } }
                    }
                };
                const string sse =
                    "data: {\"choices\":[{\"delta\":{\"content\":\"hello\"}}]}\n\n" +
                    "data: [DONE]\n\n";
                return Task.FromResult(success.WithRawStream(new MemoryStream(Encoding.UTF8.GetBytes(sse))));
            }
        }

        private sealed class Settings : IOpenAiHttpSettings
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
            public IRequestHeaderProvider HeaderProvider => null;
        }
    }
}
