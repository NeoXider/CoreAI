#if COREAI_LLM && UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Chat;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using CoreAI.Messaging;
using CoreAI.Session;
using MEAI = Microsoft.Extensions.AI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Proves incremental streaming deterministically. An SSE body whose events are handed out one per
    /// read, with a real delay before each, is driven through the production HTTP path —
    /// <see cref="HttpClientOpenAiTransport"/>, the SSE parser of <see cref="MeaiOpenAiChatClient"/>,
    /// <see cref="MeaiLlmClient"/>, <see cref="AiOrchestrator"/>, <see cref="CoreAiChatService"/> — into the
    /// demo scene's <see cref="CoreAiChatPanel"/>, and the bubble on screen must hold a strict prefix of
    /// the answer before the producer has handed out its last content event.
    /// </summary>
    public sealed class CoreAiChatPanelPacedSseStreamingPlayModeTests
    {
        // WHY a paced fake and not a live endpoint: from inside the client, an endpoint that replays a
        // finished answer as many SSE events at once and a client that received a paced stream and
        // flushed it at completion produce the same signature - several deltas, all at the end. Only a
        // producer whose pacing the test controls can tell them apart: every handout here is stamped on
        // the test's own clock, so text rendered before the last content event was handed out can only
        // have come from a consumer that streams.
        private const string LogPrefix = "[CoreAI.Tests.PacedSse]";
        private const string SceneName = "CoreAiChatDemo";

        private static readonly string[] Pieces =
            { "alpha", " beta", " gamma", " delta", " epsilon", " zeta", " eta", " theta" };

        private static readonly string FullAnswer = string.Concat(Pieces);

        // WHY 250 ms and 8 pieces: an editor frame is 10-100 ms, so every partial state stays on screen for
        // several polled frames, and the whole body still takes about two seconds; the wait is thirty times that.
        private const int InterEventDelayMs = 250;
        private const float TurnTimeoutSeconds = 60f;

        private MEAI.IChatClient _providerClient;

        [TearDown]
        public void RestoreHttpHook()
        {
            // WHY unconditional: domain reload is off in this project, so a factory left behind would route
            // every later HTTP client of the play session into this fake.
            MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory = null;
            _providerClient?.Dispose();
            _providerClient = null;
        }

        [UnityTearDown]
        public IEnumerator UnloadLoadedScenes()
        {
            yield return PlayModeSceneSandbox.UnloadToEmptyScene();
        }

        [UnityTest]
        [Timeout(120000)]
        public IEnumerator PacedSse_BubbleHoldsAStrictPrefixBeforeTheLastContentEventIsHandedOut()
        {
            // WHY: yield once before any skip - an Assert.Ignore on the first MoveNext wedges the runner.
            yield return null;
            TurnObservation turn = new();
            yield return DriveTurn(body => body, turn);

            Assert.IsFalse(turn.Faulted, $"{LogPrefix} The turn faulted: {turn.Fault}");
            Assert.AreEqual(FullAnswer, turn.Result, $"{LogPrefix} The turn did not complete with the full answer.");
            Assert.AreEqual(1, turn.Requests,
                $"{LogPrefix} The client opened the stream {turn.Requests} times; the pacing record belongs to exactly one attempt.");
            foreach (string observed in turn.Observed)
            {
                StringAssert.StartsWith(observed, FullAnswer,
                    $"{LogPrefix} The bubble showed text that is not a prefix of the answer: {turn.Describe()}");
            }

            Assert.GreaterOrEqual(turn.Partials.Count, 2,
                $"{LogPrefix} The bubble did not grow while the turn was open: {turn.Describe()}");
            Assert.IsTrue(turn.RenderedBeforeLastContentHandout,
                $"{LogPrefix} Nothing was on screen before the producer handed out its last content event, " +
                $"so a layer between the socket and the bubble held the deltas until the end: {turn.Describe()}");
        }

        /// <summary>
        /// The regression the test above exists to catch, injected at the transport seam: the same paced
        /// body is drained to its end before its first byte is served — what a client that awaited the
        /// whole body, or opened it with <c>ResponseContentRead</c>, would do. The probe must then see
        /// nothing before the last handout; otherwise a green run above would prove nothing.
        /// </summary>
        [UnityTest]
        [Timeout(120000)]
        public IEnumerator PacedSse_DrainedBody_ShowsNothingBeforeTheLastContentEventIsHandedOut()
        {
            yield return null;
            TurnObservation turn = new();
            yield return DriveTurn(body => new DrainBeforeServingStream(body), turn);

            Assert.IsFalse(turn.Faulted, $"{LogPrefix} The turn faulted: {turn.Fault}");
            Assert.AreEqual(FullAnswer, turn.Result,
                $"{LogPrefix} A drained body still has to complete the turn; only its pacing is lost.");
            Assert.IsFalse(turn.RenderedBeforeLastContentHandout,
                $"{LogPrefix} The probe reported a partial before the drained body's last handout, so it cannot " +
                $"tell a streaming consumer from a batching one: {turn.Describe()}");
        }

        /// <summary>
        /// Loads the demo scene, points the production stack at the paced fake and submits one turn through
        /// the panel, sampling the streaming bubble every frame until the turn completes.
        /// </summary>
        private IEnumerator DriveTurn(Func<Stream, Stream> shapeBody, TurnObservation turn)
        {
            if (!PlayModeSceneSandbox.IsSceneInBuildSettings(SceneName))
            {
                Assert.Ignore($"{LogPrefix} {SceneName} is not in Build Settings.");
            }

            yield return SceneManager.LoadSceneAsync(SceneName, LoadSceneMode.Single);
            yield return null;
            yield return null;

            CoreAiChatPanel panel = Object.FindFirstObjectByType<CoreAiChatPanel>();
            Assert.IsNotNull(panel, $"{LogPrefix} {SceneName} does not contain CoreAiChatPanel.");
            panel.SetCollapsed(false, false);
            UIDocument document = panel.GetComponent<UIDocument>();
            Assert.IsNotNull(document, $"{LogPrefix} The demo panel has no UIDocument to sample the bubble from.");

            // WHY the hook is armed only now: the scene's own scope builds its client from the committed
            // settings asset while loading, and a developer asset pointed at HTTP must not reach this handler.
            System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
            PacedSseHandler handler = new(BuildEvents(), Pieces.Length, InterEventDelayMs, clock, turn, shapeBody);
            MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory = () =>
                new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            panel.ChatService = BuildProductionChatService();

            Task<string> task = panel.SubmitMessageFromExternalAsync(
                "stream this",
                new CoreAiChatExternalSubmitOptions { AppendUserMessageToChat = true },
                CancellationToken.None);

            string lastObserved = null;
            while (!task.IsCompleted)
            {
                // WHY the USS marker and not a private field: the panel documents this class as the way a
                // host tells "a turn is producing text right now" from a settled transcript.
                Label bubble = document.rootVisualElement?.Q<Label>(className: CoreAiChatPanel.StreamingActiveUssClassName);
                string text = bubble?.text;
                if (!string.IsNullOrEmpty(text) && !string.Equals(text, lastObserved, StringComparison.Ordinal))
                {
                    lastObserved = text;
                    turn.RecordObserved(text, clock.Elapsed.TotalSeconds);
                }

                if (clock.Elapsed.TotalSeconds > TurnTimeoutSeconds)
                {
                    Assert.Fail($"{LogPrefix} The turn did not complete within {TurnTimeoutSeconds:0}s: {turn.Describe()}");
                }

                yield return null;
            }

            turn.CompletedSeconds = clock.Elapsed.TotalSeconds;
            turn.Faulted = task.IsFaulted || task.IsCanceled;
            turn.Fault = task.Exception?.GetBaseException().Message ?? (task.IsCanceled ? "cancelled" : null);
            turn.Result = turn.Faulted ? null : task.Result;
            turn.Requests = handler.Requests;
            Debug.Log($"{LogPrefix} {turn.Describe()}");
        }

        /// <summary>
        /// The production client stack the demo scene would build for an OpenAI-compatible endpoint, with
        /// only the socket replaced (through the editor HTTP hook) and persistence and world commands nulled.
        /// </summary>
        private CoreAiChatService BuildProductionChatService()
        {
            TestSettings settings = new();
            _providerClient = new MeaiOpenAiChatClient(settings);
            MeaiLlmClient llm = new(_providerClient, GameLoggerUnscopedFallback.Instance, settings,
                supportsNativeToolCalling: true);
            AgentMemoryPolicy memoryPolicy = new();
            // WHY: the demo panel talks as SmartChat; with no tool bound the client streams every visible
            // delta live, which is the path under test. A bound tool would only add fields to a request the
            // fake never reads.
            memoryPolicy.DisableMemoryTool(BuiltInAgentRoleIds.SmartChat);
            NullAgentMemoryStore memoryStore = new();
            AiOrchestrator orchestrator = new(
                new SoloAuthorityHost(),
                llm,
                new NullCommandSink(),
                new SessionTelemetryCollector(),
                new AiPromptComposer(
                    new BuiltInDefaultAgentSystemPromptProvider(),
                    new NoAgentUserPromptTemplateProvider(),
                    new NullLuaScriptVersionStore()),
                memoryStore,
                memoryPolicy,
                new NoOpRoleStructuredResponsePolicy(),
                new NullAiOrchestrationMetrics(),
                settings,
                new LocalActorIdentityProvider("paced-sse-playmode-test"));
            return new CoreAiChatService(orchestrator, memoryPolicy, settings, memoryStore);
        }

        private static List<byte[]> BuildEvents()
        {
            List<byte[]> events = new(Pieces.Length + 1);
            foreach (string piece in Pieces)
            {
                events.Add(Encoding.UTF8.GetBytes(
                    "data: {\"choices\":[{\"delta\":{\"content\":\"" + piece + "\"}}]}\n\n"));
            }

            events.Add(Encoding.UTF8.GetBytes("data: [DONE]\n\n"));
            return events;
        }

        /// <summary>What the bubble showed while the turn was open, against the producer's handout clock.</summary>
        private sealed class TurnObservation
        {
            private readonly object _gate = new();
            private double _lastContentHandoutSeconds = -1d;

            public readonly List<string> Observed = new();
            public double FirstPartialSeconds = -1d;
            public double CompletedSeconds = -1d;
            public string Result;
            public bool Faulted;
            public string Fault;
            public int Requests;

            public List<string> Partials
            {
                get
                {
                    List<string> partials = new();
                    foreach (string text in Observed)
                    {
                        if (text.Length < FullAnswer.Length)
                        {
                            partials.Add(text);
                        }
                    }

                    return partials;
                }
            }

            public double LastContentHandoutSeconds
            {
                get
                {
                    lock (_gate)
                    {
                        return _lastContentHandoutSeconds;
                    }
                }
            }

            /// <summary>
            /// The decisive predicate: a strictly partial text was on screen before the producer handed out
            /// its last content event. A consumer that holds the deltas until the body ends cannot satisfy it.
            /// </summary>
            public bool RenderedBeforeLastContentHandout =>
                FirstPartialSeconds >= 0d
                && LastContentHandoutSeconds >= 0d
                && FirstPartialSeconds < LastContentHandoutSeconds;

            /// <summary>Stamps one content event's handout, on whichever thread the read completes.</summary>
            public void MarkContentHandout(double seconds)
            {
                lock (_gate)
                {
                    _lastContentHandoutSeconds = seconds;
                }
            }

            public void RecordObserved(string text, double seconds)
            {
                Observed.Add(text);
                if (FirstPartialSeconds < 0d && text.Length < FullAnswer.Length)
                {
                    FirstPartialSeconds = seconds;
                }
            }

            public string Describe()
            {
                return $"{Observed.Count} distinct bubble text(s) while the turn was open, {Partials.Count} strictly partial, " +
                       $"first partial at {FirstPartialSeconds:0.###}s, last content event handed out at " +
                       $"{LastContentHandoutSeconds:0.###}s, turn completed at {CompletedSeconds:0.###}s, " +
                       $"{Requests} request(s); observed: '{string.Join("' | '", Observed)}'";
            }
        }

        /// <summary>Answers every request with a fresh paced body, shaped by the test before it is served.</summary>
        private sealed class PacedSseHandler : HttpMessageHandler
        {
            private readonly IReadOnlyList<byte[]> _events;
            private readonly int _contentEventCount;
            private readonly int _delayMs;
            private readonly System.Diagnostics.Stopwatch _clock;
            private readonly TurnObservation _turn;
            private readonly Func<Stream, Stream> _shapeBody;
            private int _requests;

            public PacedSseHandler(
                IReadOnlyList<byte[]> events,
                int contentEventCount,
                int delayMs,
                System.Diagnostics.Stopwatch clock,
                TurnObservation turn,
                Func<Stream, Stream> shapeBody)
            {
                _events = events;
                _contentEventCount = contentEventCount;
                _delayMs = delayMs;
                _clock = clock;
                _turn = turn;
                _shapeBody = shapeBody;
            }

            public int Requests => Volatile.Read(ref _requests);

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _requests);
                Stream body = _shapeBody(new PacedSseStream(_events, _contentEventCount, _delayMs, _clock, _turn));
                StreamContent content = new(body);
                content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            }
        }

        /// <summary>
        /// Hands out one SSE event per read, after a fixed delay, and stamps each content event's handout
        /// on the shared clock; a reader that asks for less than one event gets the remainder next time.
        /// </summary>
        private sealed class PacedSseStream : ReadOnlyAsyncStream
        {
            private readonly IReadOnlyList<byte[]> _events;
            private readonly int _contentEventCount;
            private readonly int _delayMs;
            private readonly System.Diagnostics.Stopwatch _clock;
            private readonly TurnObservation _turn;
            private int _next;
            private byte[] _current;
            private int _currentOffset;

            public PacedSseStream(
                IReadOnlyList<byte[]> events,
                int contentEventCount,
                int delayMs,
                System.Diagnostics.Stopwatch clock,
                TurnObservation turn)
            {
                _events = events;
                _contentEventCount = contentEventCount;
                _delayMs = delayMs;
                _clock = clock;
                _turn = turn;
            }

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count,
                CancellationToken cancellationToken)
            {
                if (_current == null)
                {
                    if (_next >= _events.Count)
                    {
                        return 0;
                    }

                    await Task.Delay(_delayMs, cancellationToken);
                    int index = _next++;
                    _current = _events[index];
                    _currentOffset = 0;
                    if (index < _contentEventCount)
                    {
                        _turn.MarkContentHandout(_clock.Elapsed.TotalSeconds);
                    }
                }

                int n = Math.Min(count, _current.Length - _currentOffset);
                Array.Copy(_current, _currentOffset, buffer, offset, n);
                _currentOffset += n;
                if (_currentOffset >= _current.Length)
                {
                    _current = null;
                }

                return n;
            }
        }

        /// <summary>Pulls the whole inner body before serving its first byte — a body-buffering consumer, at the seam.</summary>
        private sealed class DrainBeforeServingStream : ReadOnlyAsyncStream
        {
            private readonly Stream _inner;
            private MemoryStream _drained;

            public DrainBeforeServingStream(Stream inner)
            {
                _inner = inner;
            }

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count,
                CancellationToken cancellationToken)
            {
                if (_drained == null)
                {
                    MemoryStream all = new();
                    await _inner.CopyToAsync(all, 8192, cancellationToken);
                    all.Position = 0;
                    _drained = all;
                }

                return await _drained.ReadAsync(buffer, offset, count, cancellationToken);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _inner.Dispose();
                    _drained?.Dispose();
                }

                base.Dispose(disposing);
            }
        }

        /// <summary>Non-seekable read-only stream whose only real member is <see cref="Stream.ReadAsync(byte[],int,int,CancellationToken)"/>.</summary>
        private abstract class ReadOnlyAsyncStream : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                return ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
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

        private sealed class NullCommandSink : IAiGameCommandSink
        {
            public void Publish(ApplyAiGameCommand command)
            {
            }
        }

        private sealed class TestSettings : ICoreAISettings, IOpenAiHttpSettings
        {
            public string ApiBaseUrl => "http://127.0.0.1:9/v1";
            public string ApiKey => "";
            public string AuthorizationHeader => "";
            public string Model => "paced-sse-test";
            public int RequestTimeoutSeconds => 30;
            public int MaxTokens => 256;
            public IRequestHeaderProvider HeaderProvider => null;
            public string UniversalSystemPromptPrefix => "";
            public LlmBackendType BackendType => LlmBackendType.OpenAiHttp;
            public int ContextWindowTokens => 8192;
            public int MaxContextTokens => 4096;
            public int MaxLuaRepairRetries => 1;
            public int MaxToolCallRetries => 1;
            public bool AllowDuplicateToolCalls => false;
            public string ModelName => Model;
            public string CustomBaseUrl => ApiBaseUrl;
            public float Temperature => 0f;
            public string DeveloperInstructions => "";
            public string ApplicationName => "";
            public bool EnableHttpDebugLogging => false;
            public bool LogMeaiToolCallingSteps => false;
            public bool EnableMeaiDebugLogging => false;
            public float LlmRequestTimeoutSeconds => 0f;
            public int MaxLlmRequestRetries => 1;
            public bool LogLlmInput => false;
            public bool LogLlmOutput => false;
            public bool LogTokenUsage => false;
            public bool LogLlmLatency => false;
            public bool LogLlmConnectionErrors => false;
            public bool LogToolCalls => false;
            public bool LogToolCallArguments => false;
            public bool LogToolCallResults => false;
            public bool EnableStreaming => true;
        }
    }
}
#endif
