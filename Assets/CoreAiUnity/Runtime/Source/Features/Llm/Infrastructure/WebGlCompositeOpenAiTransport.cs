#if UNITY_WEBGL && !UNITY_EDITOR && COREAI_LLM
using System;
using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// WebGL transport that routes each call shape to the implementation that supports it.
    /// <para>
    /// The browser <c>fetch</c> SSE bridge (<see cref="FetchSseOpenAiTransport"/>) gives true token
    /// streaming for chat, but cannot serve a non-streaming completion (its
    /// <see cref="FetchSseOpenAiTransport.PostNonStreamingAsync"/> throws). Internal non-streaming agents
    /// (e.g. <c>TeacherLessonFeedback</c>, structured-output analyzers) therefore failed on WebGL with
    /// <c>"Use UnityWebRequestOpenAiTransport for non-streaming in WebGL."</c>.
    /// </para>
    /// <para>
    /// This composite keeps native SSE streaming via the fetch bridge while delegating non-streaming POSTs
    /// to <see cref="UnityWebRequestOpenAiTransport"/>, so both paths work in the same player.
    /// </para>
    /// </summary>
    public sealed class WebGlCompositeOpenAiTransport : IOpenAiHttpTransport, IDisposable
    {
        private readonly FetchSseOpenAiTransport _streaming;
        private readonly UnityWebRequestOpenAiTransport _nonStreaming;

        public WebGlCompositeOpenAiTransport(FetchSseOpenAiTransport streaming,
            UnityWebRequestOpenAiTransport nonStreaming)
        {
            _streaming = streaming ?? throw new ArgumentNullException(nameof(streaming));
            _nonStreaming = nonStreaming ?? throw new ArgumentNullException(nameof(nonStreaming));
        }

        public string DebugLabel => "WebGlComposite(FetchSSE+UnityWebRequest)";

        public bool SupportsSseStreaming => true;

        public Task<OpenAiHttpPostResult> PostNonStreamingAsync(OpenAiHttpPostRequest request,
            CancellationToken cancellationToken = default)
            => _nonStreaming.PostNonStreamingAsync(request, cancellationToken);

        public Task<OpenAiHttpSseOpenResult> OpenSseResponseStreamAsync(OpenAiHttpPostRequest request,
            CancellationToken cancellationToken = default)
            => _streaming.OpenSseResponseStreamAsync(request, cancellationToken);

        public void Dispose()
        {
            _streaming.Dispose();
        }
    }
}
#endif
