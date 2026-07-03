#if !COREAI_NO_LLM
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
#if UNITY_EDITOR
using System.Net.Http;
#endif
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Logging;
using MEAI = Microsoft.Extensions.AI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CoreAI.Infrastructure.Llm
{
#if UNITY_EDITOR
    /// <summary>Unity Editor only: when set, <see cref="MeaiOpenAiChatClient"/> uses this factory for HTTP instead of <c>new HttpClient()</c> (EditMode tests with <see cref="HttpMessageHandler"/> mocks).</summary>
    public static class MeaiOpenAiChatClientEditorTestHooks
    {
        /// <summary>Must be cleared in test teardown.</summary>
        public static Func<HttpClient> HttpClientFactory { get; set; }
    }
#endif

    /// <summary>
    /// Stable marker keys placed inside a tool call's arguments dictionary when the streamed
    /// argument JSON could not be parsed (malformed/truncated). Both the streaming accumulator that
    /// emits the markers and <see cref="ToolExecutionPolicy"/> that detects them reference these
    /// shared constants so the contract stays in sync across the assembly.
    /// </summary>
    public static class ToolCallArgumentMarkers
    {
        /// <summary>Marker key carrying the raw argument string when it failed to parse as JSON.</summary>
        public const string RawArgumentsKey = "__raw_arguments";

        /// <summary>Marker key (boolean <c>true</c>) set when the accumulated arguments could not be parsed as JSON.</summary>
        public const string ParseErrorKey = "__parse_error";
    }

    /// <summary>
    /// MEAI <see cref="MEAI.IChatClient"/> for OpenAI-compatible HTTP APIs.
    /// Uses <see cref="IOpenAiHttpTransport"/> (default <see cref="HttpClientOpenAiTransport"/> outside WebGL player;
    /// WebGL uses <c>UnityWebRequest</c> from CoreAI.Source). Continuations preserve sync context when present.
    /// </summary>
    public sealed class MeaiOpenAiChatClient : MEAI.IChatClient, IDisposable
    {
        private readonly IOpenAiHttpSettings _settings;
        private readonly IOpenAiHttpTransport _transport;
        private readonly ILog _log;

        public MeaiOpenAiChatClient(IOpenAiHttpSettings settings, IOpenAiHttpTransport transport, ILog? log = null)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _log = log ?? Log.Instance;
        }

#if !UNITY_WEBGL || UNITY_EDITOR
        public MeaiOpenAiChatClient(IOpenAiHttpSettings settings, ILog? log = null)
            : this(settings, new HttpClientOpenAiTransport(), log)
        {
        }
#endif

        public async Task<MEAI.ChatResponse> GetResponseAsync(
            IEnumerable<MEAI.ChatMessage> chatMessages,
            MEAI.ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            List<MEAI.ChatMessage> msgs = chatMessages.ToList();
            string url = _settings.ApiBaseUrl.TrimEnd('/') + "/chat/completions";

            _log.Info($"MeaiOpenAiChatClient: POST {url}", LogTag.Llm);

            if (_settings.LogLlmInput)
            {
                _log.Info("MeaiOpenAiChatClient: === LLM Input ===", LogTag.Llm);
                foreach (MEAI.ChatMessage msg in msgs)
                {
                    string content = msg.Text ?? "";
                    if (string.IsNullOrEmpty(content) && msg.Contents != null && msg.Contents.Count > 0)
                    {
                        MEAI.TextContent textContent = msg.Contents.OfType<MEAI.TextContent>().FirstOrDefault();
                        content = textContent?.Text ?? string.Join(", ", msg.Contents.Select(c => c.ToString()));
                    }

                    _log.Info($"MeaiOpenAiChatClient: [{msg.Role}] {content}", LogTag.Llm);
                }

                if (options?.Tools != null && options.Tools.Count > 0)
                {
                    _log.Info($"MeaiOpenAiChatClient: Tools ({options.Tools.Count}):", LogTag.Llm);
                    foreach (MEAI.AITool tool in options.Tools)
                    {
                        if (tool is MEAI.AIFunction af)
                        {
                            _log.Info($"MeaiOpenAiChatClient:   - {af.Name}: {af.Description}", LogTag.Llm);
                        }
                    }
                }
            }

            List<Dictionary<string, object>> messages = BuildMessagesPayload(msgs);
            List<Dictionary<string, object>> toolsList = BuildToolsPayload(options);

            Dictionary<string, object> reqBody = new()
            {
                { "model", _settings.Model },
                { "messages", messages }
            };
            if (options?.Temperature.HasValue == true)
            {
                reqBody["temperature"] = options.Temperature.Value;
            }

            if (options?.MaxOutputTokens.HasValue == true)
            {
                reqBody["max_tokens"] = options.MaxOutputTokens.Value;
            }

            if (toolsList.Count > 0)
            {
                reqBody["tools"] = toolsList;
            }

            ApplyProviderSpecificRequestBody(reqBody);

            string json = JsonConvert.SerializeObject(reqBody);

            if (_settings.EnableHttpDebugLogging)
            {
                _log.Info($"MeaiOpenAiChatClient: Request JSON={json}", LogTag.Llm);
            }

            const int transientLocalLlmReloadMaxAttempts = 10;

            string responseJson = null;

            for (int attempt = 1; attempt <= transientLocalLlmReloadMaxAttempts; attempt++)
            {
                int transportTimeoutSec = _settings.RequestTimeoutSeconds <= 0 ? 120 : _settings.RequestTimeoutSeconds;
                _log.Info($"MeaiOpenAiChatClient: Timeout={transportTimeoutSec}s ({_transport.DebugLabel})",
                    LogTag.Llm);

                try
                {
                    OpenAiHttpPostResult postResult = await _transport.PostNonStreamingAsync(
                        new OpenAiHttpPostRequest
                        {
                            Url = url,
                            JsonBody = json,
                            AcceptEventStream = false,
                            TransportTimeoutSeconds = transportTimeoutSec,
                            Headers = BuildTransportHeaders(url, false)
                        }, cancellationToken);

                    if (postResult.IsSuccessStatusCode)
                    {
                        responseJson = postResult.BodyText;
                        break;
                    }

                    string errorDetail = !string.IsNullOrEmpty(postResult.BodyText)
                        ? $"HTTP {postResult.StatusCode} | Body: {postResult.BodyText}"
                        : $"HTTP {postResult.StatusCode}";
                    _log.Warn($"MeaiOpenAiChatClient: {errorDetail}", LogTag.Llm);

                    bool canRetryTransient = attempt < transientLocalLlmReloadMaxAttempts
                                             && IsTransientLocalLlmReloadError(postResult.StatusCode,
                                                 postResult.BodyText, errorDetail);

                    if (canRetryTransient)
                    {
                        _log.Info(
                            $"MeaiOpenAiChatClient: transient local LLM / reload response (attempt {attempt}/{transientLocalLlmReloadMaxAttempts}); retrying after backoff...",
                            LogTag.Llm);
                        await BackoffDelayAsync(Math.Min(6000, 900 * attempt), cancellationToken);
                        continue;
                    }

                    throw BuildHttpException(postResult.StatusCode, postResult.BodyText, errorDetail,
                        postResult.ResponseHeaders);
                }
                catch (OperationCanceledException ex)
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        _log.Warn(
                            $"MeaiOpenAiChatClient: Request timeout or transport canceled ({ex.GetType().Name}): {ex.Message}",
                            LogTag.Llm);
                    }

                    throw;
                }
                catch (LlmClientException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log.Warn($"MeaiOpenAiChatClient: Send failed: {ex.Message}", LogTag.Llm);
                    throw new LlmClientException($"HTTP send failed: {ex.Message}", LlmErrorCode.BackendUnavailable);
                }
            }

            if (responseJson == null)
            {
                throw new InvalidOperationException(
                    "MeaiOpenAiChatClient: request completed without success or typed error.");
            }

            if (_settings.EnableHttpDebugLogging)
            {
                _log.Info($"MeaiOpenAiChatClient: Response JSON={responseJson}", LogTag.Llm);
            }

            return ParseResponse(responseJson);
        }

        public async IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<MEAI.ChatMessage> chatMessages,
            MEAI.ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            if (!_transport.SupportsSseStreaming)
            {
                _log.Info(
                    "MeaiOpenAiChatClient: transport has no SSE support - using non-stream completion and simulated streaming updates (WebGL / UnityWebRequest).",
                    LogTag.Llm);
                MEAI.ChatResponse full = await GetResponseAsync(chatMessages, options, cancellationToken);
                foreach (MEAI.ChatResponseUpdate u in FullResponseToSimulatedStreamingUpdates(full))
                {
                    yield return u;
                }

                yield break;
            }

            List<MEAI.ChatMessage> msgs = chatMessages.ToList();
            string url = _settings.ApiBaseUrl.TrimEnd('/') + "/chat/completions";

            List<Dictionary<string, object>> messages = BuildMessagesPayload(msgs);
            List<Dictionary<string, object>> toolsList = BuildToolsPayload(options);

            Dictionary<string, object> reqBody = new()
            {
                { "model", _settings.Model },
                { "messages", messages },
                { "stream", true },
                { "stream_options", new Dictionary<string, object> { { "include_usage", true } } }
            };
            if (options?.Temperature.HasValue == true)
            {
                reqBody["temperature"] = options.Temperature.Value;
            }

            if (options?.MaxOutputTokens.HasValue == true)
            {
                reqBody["max_tokens"] = options.MaxOutputTokens.Value;
            }

            if (toolsList.Count > 0)
            {
                reqBody["tools"] = toolsList;
            }

            ApplyProviderSpecificRequestBody(reqBody);

            string json = JsonConvert.SerializeObject(reqBody);

            const int transientLocalLlmReloadMaxAttempts = 10;

            for (int attempt = 1; attempt <= transientLocalLlmReloadMaxAttempts; attempt++)
            {
                int streamTransportTimeoutSec =
                    _settings.RequestTimeoutSeconds <= 0 ? 120 : _settings.RequestTimeoutSeconds;

                OpenAiHttpPostRequest transportReq = new()
                {
                    Url = url,
                    JsonBody = json,
                    AcceptEventStream = true,
                    TransportTimeoutSeconds = streamTransportTimeoutSec,
                    Headers = BuildTransportHeaders(url, true)
                };

                _log.Info(
                    $"MeaiOpenAiChatClient: POST (stream) {url} (attempt {attempt}/{transientLocalLlmReloadMaxAttempts})",
                    LogTag.Llm);

                OpenAiHttpSseOpenResult openResult;
                try
                {
                    openResult = await _transport.OpenSseResponseStreamAsync(transportReq, cancellationToken);
                }
                catch (OperationCanceledException ex)
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        _log.Warn(
                            $"MeaiOpenAiChatClient: stream open: Request timeout or transport canceled ({ex.GetType().Name}): {ex.Message}",
                            LogTag.Llm);
                    }

                    throw;
                }
                catch (LlmClientException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log.Warn($"MeaiOpenAiChatClient: stream open failed: {ex.Message}", LogTag.Llm);

                    // A transport-level SEND failure (typically a pooled keep-alive connection the local
                    // server has already closed — System.Net.Http surfaces it as "An error occurred while
                    // sending the request") is retryable: a fresh attempt opens a new connection. Bounded
                    // to a few quick retries so a genuinely-down backend still surfaces promptly as
                    // BackendUnavailable rather than spinning the full local-reload budget.
                    const int transportSendRetryMaxAttempts = 3;
                    if (attempt < transportSendRetryMaxAttempts && !cancellationToken.IsCancellationRequested)
                    {
                        _log.Info(
                            "MeaiOpenAiChatClient: transient transport send failure on stream-open; retrying after backoff...",
                            LogTag.Llm);
                        await BackoffDelayAsync(Math.Min(2000, 300 * attempt), cancellationToken);
                        continue;
                    }

                    throw new LlmClientException($"HTTP stream send failed: {ex.Message}",
                        LlmErrorCode.BackendUnavailable);
                }

                using (openResult)
                {
                    string ctype = TryGetHeaderFirstValue(openResult.ResponseHeaders, "Content-Type") ?? "n/a";
                    LogStreamingHttpResponseSummary(openResult.StatusCode,
                        openResult.StatusCode >= 200 && openResult.StatusCode < 300, "", ctype);

                    if (openResult.StatusCode < 200 || openResult.StatusCode >= 300)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string streamBody = openResult.ErrorBodyText ?? "";
                        string streamErr = !string.IsNullOrEmpty(streamBody)
                            ? $"HTTP {openResult.StatusCode} | Body: {streamBody}"
                            : $"HTTP {openResult.StatusCode}";
                        _log.Warn($"MeaiOpenAiChatClient: stream error - {streamErr}", LogTag.Llm);

                        bool canRetryTransient = attempt < transientLocalLlmReloadMaxAttempts
                                                 && IsTransientLocalLlmReloadError(openResult.StatusCode, streamBody,
                                                     streamErr);

                        if (canRetryTransient)
                        {
                            _log.Info(
                                "MeaiOpenAiChatClient: transient local LLM on stream-open; retrying after backoff...",
                                LogTag.Llm);
                            await BackoffDelayAsync(Math.Min(6000, 900 * attempt), cancellationToken);
                            continue;
                        }

                        throw BuildHttpException(openResult.StatusCode, streamBody, streamErr,
                            openResult.ResponseHeaders);
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    Stream? stream = openResult.ResponseStream;
                    if (stream == null)
                    {
                        throw new LlmClientException("HTTP stream: success status but no response stream.",
                            LlmErrorCode.BackendUnavailable);
                    }

                    SseToolCallAccumulator toolAccumulator = new(_log);
                    DateTime lastProgressUtc = DateTime.UtcNow;
                    int parsedSseDeltas = 0;

                    // buffering (Mono on Windows can hold lines back until a larger buffer fills, collapsing
                    // 100+ token-by-token deltas into 2 large yields). ReadAsync gives true low-latency streaming.
                    await foreach (string line in ReadUtf8LinesFromStreamAsync(
                                       stream,
                                       streamTransportTimeoutSec,
                                       cancellationToken))
                    {
                        if ((DateTime.UtcNow - lastProgressUtc).TotalSeconds > streamTransportTimeoutSec)
                        {
                            _log.Warn(
                                $"MeaiOpenAiChatClient: SSE stall timeout after {streamTransportTimeoutSec}s without new lines; aborting.",
                                LogTag.Llm);
                            throw new LlmClientException(
                                $"LLM SSE stalled - no data for {streamTransportTimeoutSec}s.",
                                LlmErrorCode.Timeout);
                        }

                        if (IsSseDoneLine(line))
                        {
                            break;
                        }

                        foreach (MEAI.ChatResponseUpdate update in ParseSseUpdates(line + "\n", toolAccumulator))
                        {
                            parsedSseDeltas++;
                            string updateText = update?.Text ?? "";
                            bool textOnly = !string.IsNullOrEmpty(updateText)
                                            && (update.Contents == null
                                                || update.Contents.Count == 0
                                                || update.Contents.All(c => c is MEAI.TextContent));
                            // Some upstream providers (e.g. OpenRouter `:free` models from Nvidia/etc.)
                            // batch many tokens into a single SSE delta, which makes streaming look
                            // jumpy in the UI. Re-emit large text-only deltas in small word-sized
                            // pieces with a tiny delay so the UI sees smooth per-word streaming.
                            // True per-token providers (LM Studio, paid models) already send small
                            // deltas and skip this path.
                            if (textOnly && updateText.Length > 24)
                            {
                                foreach (string piece in SplitForSmoothStreaming(updateText))
                                {
                                    yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, piece);
                                    await DelayBetweenSyntheticStreamPiecesAsync(cancellationToken);
                                }
                            }
                            else
                            {
                                yield return update;
                            }
                        }

                        // Execute-as-you-stream: surface every tool call whose arguments JSON is
                        // already complete NOW, instead of holding all calls until the final Flush.
                        // The consumer (MeaiLlmClient) can then run each call while the model is
                        // still generating the rest of the turn.
                        MEAI.ChatResponseUpdate completedCalls = toolAccumulator.DrainCompleted();
                        if (completedCalls != null)
                        {
                            parsedSseDeltas++;
                            yield return completedCalls;
                        }

                        // Re-arm the stall clock only AFTER every update for this line has been
                        // yielded and consumed. This iterator is pull-based: the consumer
                        // (MeaiLlmClient) executes tool calls between MoveNexts, so re-arming on
                        // line arrival would charge that consumer time against the transport
                        // stall budget and abort healthy streams with slow tools. Measured this
                        // way, the gap checked at the top of the loop is pure transport wait.
                        // Whitespace-only keep-alive lines still do not count as progress.
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            lastProgressUtc = DateTime.UtcNow;
                        }
                    }

                    MEAI.ChatResponseUpdate flushed = toolAccumulator.Flush();
                    if (flushed != null)
                    {
                        parsedSseDeltas++;
                        yield return flushed;
                    }

                    if (parsedSseDeltas == 0)
                    {
                        bool canRetryEmptyStream = attempt < transientLocalLlmReloadMaxAttempts;
                        if (canRetryEmptyStream)
                        {
                            int emptyStreamBackoffMs = Math.Min(6000, 900 * attempt);
                            _log.Warn(
                                $"MeaiOpenAiChatClient: HTTP 200 but 0 parsed SSE deltas (likely only upstream keep-alive comments - provider/model produced no tokens). " +
                                $"Retrying (attempt {attempt + 1}/{transientLocalLlmReloadMaxAttempts}) after {emptyStreamBackoffMs}ms backoff...",
                                LogTag.Llm);
                            await BackoffDelayAsync(emptyStreamBackoffMs, cancellationToken);
                            continue;
                        }

                        _log.Warn(
                            "MeaiOpenAiChatClient: stream ended with HTTP success but 0 parsed SSE deltas after all retries - " +
                            "upstream provider produced no tokens. Surface as backend-unavailable.",
                            LogTag.Llm);
                        throw new LlmClientException(
                            "Upstream model returned no tokens (only keep-alive comments) after all retries.",
                            LlmErrorCode.BackendUnavailable);
                    }

                    yield break;
                }
            }
        }

        /// <summary>
        /// Splits a large text delta into smaller pieces (~6 chars or one word boundary) so the UI
        /// can render smooth per-word streaming even when an upstream provider batches many tokens
        /// into one SSE event.
        /// </summary>
        private static IEnumerable<string> SplitForSmoothStreaming(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                yield break;
            }

            const int targetChunkSize = 6;
            int i = 0;
            while (i < text.Length)
            {
                int end = Math.Min(i + targetChunkSize, text.Length);
                // Extend to the next whitespace to avoid splitting inside a word when possible.
                while (end < text.Length && !char.IsWhiteSpace(text[end - 1]) && end - i < targetChunkSize * 2)
                {
                    end++;
                }

                yield return text.Substring(i, end - i);
                i = end;
            }
        }

        private static Task DelayBetweenSyntheticStreamPiecesAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
#if UNITY_WEBGL && !UNITY_EDITOR
            // Browser WebGL has no reliable worker ThreadPool. A timer-based Task.Delay here can
            // leave a synthetic split delta stuck after the first visible piece on some builds.
            return Task.CompletedTask;
#else
            return Task.Delay(15, cancellationToken);
#endif
        }

        /// <summary>
        /// Cross-platform line reader for SSE streams. Bypasses <see cref="StreamReader.ReadLineAsync"/>
        /// because Mono on Windows can hold lines back until a larger buffer fills, collapsing many small
        /// token-by-token deltas into a single large yield (visible to the user as "no streaming"). WebGL
        /// uses this too because the FetchSseStream pipe doesn't always interoperate cleanly with
        /// <see cref="StreamReader.ReadLineAsync"/>.
        /// </summary>
        private static async IAsyncEnumerable<string> ReadUtf8LinesFromStreamAsync(
            Stream stream,
            int streamIdleTimeoutSeconds,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken)
        {
            byte[] byteBuffer = new byte[8192];
            char[] charBuffer = new char[8192];
            Decoder decoder = Encoding.UTF8.GetDecoder();
            StringBuilder lineBuilder = new(256);

            while (true)
            {
                // No ConfigureAwait(false): on WebGL the continuation must capture
                // UnitySynchronizationContext, otherwise it gets posted to the (non-existent)
                // browser ThreadPool and the read pump silently halts after the first await.
                int read = await ReadWithIdleTimeoutAsync(
                    stream,
                    byteBuffer,
                    0,
                    byteBuffer.Length,
                    streamIdleTimeoutSeconds,
                    cancellationToken);
                if (read <= 0)
                {
                    break;
                }

                int charCount = decoder.GetChars(byteBuffer, 0, read, charBuffer, 0, false);
                for (int i = 0; i < charCount; i++)
                {
                    char c = charBuffer[i];
                    if (c == '\n')
                    {
                        yield return lineBuilder.ToString();
                        lineBuilder.Clear();
                    }
                    else if (c != '\r')
                    {
                        lineBuilder.Append(c);
                    }
                }
            }

            while (true)
            {
                int flushedChars = decoder.GetChars(byteBuffer, 0, 0, charBuffer, 0, true);
                if (flushedChars <= 0)
                {
                    break;
                }

                for (int i = 0; i < flushedChars; i++)
                {
                    char c = charBuffer[i];
                    if (c == '\n')
                    {
                        yield return lineBuilder.ToString();
                        lineBuilder.Clear();
                    }
                    else if (c != '\r')
                    {
                        lineBuilder.Append(c);
                    }
                }
            }

            if (lineBuilder.Length > 0)
            {
                yield return lineBuilder.ToString();
            }
        }

        private static async Task<int> ReadWithIdleTimeoutAsync(
            Stream stream,
            byte[] buffer,
            int offset,
            int count,
            int streamIdleTimeoutSeconds,
            CancellationToken cancellationToken)
        {
            Task<int> readTask = stream.ReadAsync(buffer, offset, count, cancellationToken);
            if (readTask.IsCompleted)
            {
                return await readTask;
            }

            int timeoutMs = Math.Max(1, streamIdleTimeoutSeconds) * 1000;

            // Drive the idle timeout off a linked CTS so that when the read wins (the hot path, hit once
            // per 8 KB read for the whole stream) we cancel the Task.Delay immediately. That releases its
            // underlying System.Threading.Timer and the CancellationTokenRegistration it holds on the
            // request token; leaving it uncancelled accumulates one live ~timeout-length timer per read
            // across the stream (a steady leak proportional to streamed tokens).
            using CancellationTokenSource delayCts =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task timeoutTask = Task.Delay(timeoutMs, delayCts.Token);
            Task completed = await Task.WhenAny(readTask, timeoutTask);
            if (ReferenceEquals(completed, readTask))
            {
                delayCts.Cancel();
                return await readTask;
            }

            // Idle timeout fired: the read is abandoned. Observe it so its eventual fault (e.g.
            // ObjectDisposedException once the caller disposes the stream) does not surface as an
            // unobserved task exception. The buffer it may still write into is iterator-local and is
            // never read again after this throw unwinds the loop.
            ObserveAbandonedRead(readTask);

            cancellationToken.ThrowIfCancellationRequested();
            throw new LlmClientException(
                $"LLM SSE stalled - no data for {Math.Max(1, streamIdleTimeoutSeconds)}s.",
                LlmErrorCode.Timeout);
        }

        /// <summary>
        /// Attaches a fault-only continuation to an abandoned read so a later exception on it (typically
        /// <see cref="ObjectDisposedException"/> after the stream is disposed) is observed and not raised
        /// as an unobserved task exception on the finalizer thread.
        /// </summary>
        private static void ObserveAbandonedRead(Task readTask)
        {
            _ = readTask.ContinueWith(
                t => { _ = t.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private List<KeyValuePair<string, string>> BuildTransportHeaders(string url, bool acceptEventStream)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            const bool omitCorsSensitiveCorrelationHeaders = true;
#else
            const bool omitCorsSensitiveCorrelationHeaders = false;
#endif
            return BuildTransportHeadersCore(
                url,
                acceptEventStream,
                omitCorsSensitiveCorrelationHeaders,
                ResolveAuthorizationHeader(),
                _settings,
                _log);
        }

        /// <summary>
        /// EditMode tests: same header list as <see cref="BuildTransportHeaders"/> for an explicit
        /// <paramref name="omitCorsSensitiveCorrelationHeaders"/> (WebGL player uses <c>true</c>).
        /// </summary>
        internal static List<KeyValuePair<string, string>> BuildTransportHeadersForTests(
            string url,
            bool acceptEventStream,
            bool omitCorsSensitiveCorrelationHeaders,
            string? authorizationHeader,
            IOpenAiHttpSettings settings,
            ILog log)
        {
            return BuildTransportHeadersCore(
                url,
                acceptEventStream,
                omitCorsSensitiveCorrelationHeaders,
                authorizationHeader,
                settings,
                log);
        }

        private static List<KeyValuePair<string, string>> BuildTransportHeadersCore(
            string url,
            bool acceptEventStream,
            bool omitCorsSensitiveCorrelationHeaders,
            string? authorizationHeader,
            IOpenAiHttpSettings settings,
            ILog log)
        {
            _ = acceptEventStream;
            List<KeyValuePair<string, string>> list = new();

            if (url.Contains("openrouter", StringComparison.OrdinalIgnoreCase))
            {
                list.Add(new KeyValuePair<string, string>(OpenAiHttpConstants.HttpRefererHeaderName,
                    OpenAiHttpConstants.HttpRefererUnityUrl));
                list.Add(new KeyValuePair<string, string>("X-Title", "CoreAI"));
                log.Info("MeaiOpenAiChatClient: Added OpenRouter headers", LogTag.Llm);
            }

            if (!string.IsNullOrWhiteSpace(authorizationHeader))
            {
                string trimmedAuth = authorizationHeader.Trim();
                list.Add(new KeyValuePair<string, string>("Authorization", trimmedAuth));
                log.Info($"MeaiOpenAiChatClient: Authorization header set (len={trimmedAuth.Length})",
                    LogTag.Llm);
            }

            // WebGL in the browser: cross-origin requests trigger CORS preflight for non-safelisted headers.
            // Public gateways (e.g. openrouter.ai) often omit X-Request-Id / Idempotency-Key / X-Tenant-Id from
            // Access-Control-Allow-Headers, which blocks the whole POST before it reaches the API.

            if (!omitCorsSensitiveCorrelationHeaders)
            {
                LlmRequestContextFrame ctx = LlmRequestContext.Current;
                if (ctx != null)
                {
                    if (!string.IsNullOrEmpty(ctx.IdempotencyKey))
                    {
                        list.Add(new KeyValuePair<string, string>("Idempotency-Key", ctx.IdempotencyKey));
                    }

                    if (!string.IsNullOrEmpty(ctx.TraceId))
                    {
                        list.Add(new KeyValuePair<string, string>("X-Request-Id", ctx.TraceId));
                    }

                    if (!string.IsNullOrEmpty(ctx.AgentRoleId))
                    {
                        list.Add(new KeyValuePair<string, string>("X-Coreai-Role", ctx.AgentRoleId));
                    }
                }

                ILlmAuthContextProvider auth = LlmAuthContextRegistry.Current;
                if (auth != null)
                {
                    if (!string.IsNullOrEmpty(auth.TenantId))
                    {
                        list.Add(new KeyValuePair<string, string>("X-Tenant-Id", auth.TenantId));
                    }

                    if (!string.IsNullOrEmpty(auth.UserId))
                    {
                        list.Add(new KeyValuePair<string, string>("X-User-Id", auth.UserId));
                    }

                    if (!string.IsNullOrEmpty(auth.SessionId))
                    {
                        list.Add(new KeyValuePair<string, string>("X-Session-Id", auth.SessionId));
                    }
                }
            }

            IRequestHeaderProvider? hp = settings.HeaderProvider;
            if (hp != null)
            {
                IReadOnlyList<KeyValuePair<string, string>>? extra = hp.GetHeaders();
                if (extra != null)
                {
                    foreach (KeyValuePair<string, string> kv in extra)
                    {
                        if (!string.IsNullOrEmpty(kv.Key))
                        {
                            if (omitCorsSensitiveCorrelationHeaders &&
                                IsCorsSensitiveCorrelationHeader(kv.Key))
                            {
                                continue;
                            }

                            list.Add(kv);
                        }
                    }
                }

                if (!omitCorsSensitiveCorrelationHeaders)
                {
                    if (!string.IsNullOrEmpty(hp.IdempotencyKey) && !ContainsHeader(list, "Idempotency-Key"))
                    {
                        list.Add(new KeyValuePair<string, string>("Idempotency-Key", hp.IdempotencyKey));
                    }

                    if (!string.IsNullOrEmpty(hp.RequestId) && !ContainsHeader(list, "X-Request-Id"))
                    {
                        list.Add(new KeyValuePair<string, string>("X-Request-Id", hp.RequestId));
                    }
                }
            }

            return list;
        }

        private static bool IsCorsSensitiveCorrelationHeader(string headerName)
        {
            return string.Equals(headerName, "X-Request-Id", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(headerName, "Idempotency-Key", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(headerName, "X-Coreai-Role", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(headerName, "X-Tenant-Id", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(headerName, "X-User-Id", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(headerName, "X-Session-Id", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ContainsHeader(List<KeyValuePair<string, string>> list, string name)
        {
            foreach (KeyValuePair<string, string> kv in list)
            {
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string? TryGetHeaderFirstValue(IReadOnlyDictionary<string, IEnumerable<string>> headers,
            string name)
        {
            if (headers == null)
            {
                return null;
            }

            foreach (KeyValuePair<string, IEnumerable<string>> kv in headers)
            {
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    return kv.Value?.FirstOrDefault();
                }
            }

            return null;
        }

        private static IEnumerable<MEAI.AIContent> EnumerableContents(MEAI.ChatMessage msg)
        {
            if (msg.Contents == null)
            {
                yield break;
            }

            foreach (MEAI.AIContent c in msg.Contents)
            {
                yield return c;
            }
        }

        private static IEnumerable<MEAI.ChatResponseUpdate> FullResponseToSimulatedStreamingUpdates(
            MEAI.ChatResponse response)
        {
            if (response?.Messages == null || response.Messages.Count == 0)
            {
                yield break;
            }

            MEAI.ChatMessage msg = response.Messages[0];

            if (msg.Contents != null && msg.Contents.Count > 0)
            {
                foreach (MEAI.AIContent c in EnumerableContents(msg))
                {
                    if (c is MEAI.TextContent tc && !string.IsNullOrEmpty(tc.Text))
                    {
                        yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, tc.Text);
                    }
                    else if (c is MEAI.FunctionCallContent fc)
                    {
                        MEAI.ChatResponseUpdate u = new(MEAI.ChatRole.Assistant, "");
                        u.Contents = new List<MEAI.AIContent> { fc };
                        yield return u;
                    }
                }
            }
            else if (!string.IsNullOrEmpty(msg.Text))
            {
                yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, msg.Text);
            }
        }

        private void LogStreamingHttpResponseSummary(int code, bool success, string reason, string contentType)
        {
            string reasonOut = reason ?? "";
            string ctype = string.IsNullOrEmpty(contentType) ? "n/a" : contentType;

            if (success)
            {
                _log.Info(
                    $"MeaiOpenAiChatClient: stream HTTP {code} {reasonOut} | Content-Type: {ctype} | reading SSE body",
                    LogTag.Llm);
            }
            else
            {
                _log.Warn(
                    $"MeaiOpenAiChatClient: stream HTTP {code} {reasonOut} FAILED | Content-Type: {ctype} | reading error body",
                    LogTag.Llm);
            }
        }

        private static async Task BackoffDelayAsync(int milliseconds, CancellationToken cancellationToken)
        {
            if (milliseconds <= 0)
            {
                return;
            }

            await Task.Delay(milliseconds, cancellationToken);
        }

        private string ResolveAuthorizationHeader()
        {
            if (!string.IsNullOrWhiteSpace(_settings.AuthorizationHeader))
            {
                return _settings.AuthorizationHeader.Trim();
            }

            return string.IsNullOrWhiteSpace(_settings.ApiKey)
                ? ""
                : "Bearer " + _settings.ApiKey;
        }

        private static LlmClientException BuildHttpException(
            int statusCode,
            string responseBody,
            string errorDetail,
            IReadOnlyDictionary<string, IEnumerable<string>> responseHeaders)
        {
            int status = statusCode;
            LlmErrorCode code = MapHttpStatus(status, responseBody, errorDetail);
            int? retryAfter = TryParseRetryAfterHeaders(responseHeaders);

            return new LlmClientException(
                $"HTTP error {status}: {ExtractProviderMessage(responseBody, errorDetail)}",
                code,
                status > 0 ? status : null,
                retryAfter,
                responseBody);
        }

        private static int? TryParseRetryAfterHeaders(IReadOnlyDictionary<string, IEnumerable<string>>? headers)
        {
            if (headers == null || headers.Count == 0)
            {
                return null;
            }

            string? retryMsHeader = GetHeaderValues(headers, "Retry-After-Ms")?.FirstOrDefault();
            if (!string.IsNullOrEmpty(retryMsHeader) &&
                float.TryParse(retryMsHeader, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float retryMs))
            {
                return (int)Math.Ceiling(retryMs / 1000f);
            }

            string? retryHeader = GetHeaderValues(headers, "Retry-After")?.FirstOrDefault();
            if (int.TryParse(retryHeader, out int parsedRetry))
            {
                return parsedRetry;
            }

            return null;
        }

        private static IEnumerable<string>? GetHeaderValues(IReadOnlyDictionary<string, IEnumerable<string>> headers,
            string name)
        {
            foreach (KeyValuePair<string, IEnumerable<string>> kv in headers)
            {
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    return kv.Value;
                }
            }

            return null;
        }

        private static bool IsTransientLocalLlmReloadError(int httpStatus, string responseBody, string transportError)
        {
            string text = $"{responseBody ?? ""} {transportError ?? ""}".ToLowerInvariant();

            if (!(text.Contains("model reloaded") ||
                  text.Contains("model is loading") ||
                  text.Contains("loading the model") ||
                  text.Contains("model not ready")))
            {
                return false;
            }

            if (httpStatus == 401 || httpStatus == 403)
            {
                return false;
            }

            return true;
        }

        private static LlmErrorCode MapHttpStatus(int status, string body, string fallback)
        {
            string text = ((body ?? "") + " " + (fallback ?? "")).ToLowerInvariant();
            if (status == 413)
            {
                return LlmErrorCode.ContextLengthExceeded;
            }

            if (status == 401 || status == 403)
            {
                return LlmErrorCode.AuthExpired;
            }

            if (status == 409 || text.Contains("quota") || text.Contains("quota_exceeded"))
            {
                return LlmErrorCode.QuotaExceeded;
            }

            if (status == 429 || text.Contains("rate"))
            {
                return LlmErrorCode.RateLimited;
            }

            if (text.Contains("context_length_exceeded") || text.Contains("maximum context") ||
                text.Contains("context window") || text.Contains("too many tokens") ||
                text.Contains("token limit"))
            {
                return LlmErrorCode.ContextLengthExceeded;
            }

            if (status == 400 || status == 422)
            {
                return LlmErrorCode.InvalidRequest;
            }

            if (status >= 500 || status == 0)
            {
                return LlmErrorCode.BackendUnavailable;
            }

            return LlmErrorCode.ProviderError;
        }

        private static string ExtractProviderMessage(string responseBody, string fallback)
        {
            if (string.IsNullOrWhiteSpace(responseBody))
            {
                return fallback ?? "";
            }

            try
            {
                JObject root = JObject.Parse(responseBody);
                string message = root["error"]?["message"]?.ToString()
                                 ?? root["message"]?.ToString()
                                 ?? root["detail"]?.ToString();
                if (!string.IsNullOrWhiteSpace(message))
                {
                    return message;
                }
            }
            catch
            {
            }

            return fallback ?? responseBody;
        }

        internal static MEAI.ChatResponse ParseResponse(string json)
        {
            try
            {
                JObject root = JObject.Parse(json);
                JArray choices = root["choices"] as JArray;
                if (choices == null || choices.Count == 0)
                {
                    return new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, ""));
                }

                JToken msg = choices[0]?["message"];
                string content = ParseAssistantMessageVisibleText(msg);

                JArray toolCalls = msg?["tool_calls"] as JArray;

                MEAI.ChatResponse response = new(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, content));

                if (toolCalls != null && toolCalls.Count > 0)
                {
                    List<MEAI.AIContent> contents = new();
                    if (!string.IsNullOrEmpty(content))
                    {
                        contents.Add(new MEAI.TextContent(content));
                    }

                    foreach (JToken tc in toolCalls)
                    {
                        JObject func = tc["function"] as JObject;
                        if (func != null)
                        {
                            contents.Add(new MEAI.FunctionCallContent(
                                tc["id"]?.ToString() ?? "",
                                func["name"]?.ToString() ?? "",
                                JsonConvert.DeserializeObject<Dictionary<string, object?>>(
                                    func["arguments"]?.ToString() ?? "{}")));
                        }
                    }

                    response.Messages[0] = new MEAI.ChatMessage(MEAI.ChatRole.Assistant, contents);
                }

                if (root["usage"] is JObject usage)
                {
                    response.Usage = BuildUsageDetailsFromOpenAiUsageObject(usage);
                }

                return response;
            }
            catch
            {
                return new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, ""));
            }
        }

        private static string StripRedactedThinkingBlock(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text ?? "";
            }

            return System.Text.RegularExpressions.Regex.Replace(text,
                @"<think>[\s\S]*?</think>\s*", "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
        }

        private static string ExtractMessageContentString(JToken contentToken)
        {
            if (contentToken == null || contentToken.Type == JTokenType.Null)
            {
                return "";
            }

            if (contentToken.Type == JTokenType.String)
            {
                return contentToken.Value<string>() ?? "";
            }

            if (contentToken.Type == JTokenType.Array)
            {
                StringBuilder sb = new();
                foreach (JToken part in contentToken)
                {
                    if (part.Type == JTokenType.String)
                    {
                        if (sb.Length > 0)
                        {
                            sb.Append('\n');
                        }

                        sb.Append(part.ToString());
                    }
                    else if (part is JObject o)
                    {
                        string t = o["text"]?.ToString();
                        if (!string.IsNullOrEmpty(t))
                        {
                            if (sb.Length > 0)
                            {
                                sb.Append('\n');
                            }

                            sb.Append(t);
                        }
                    }
                }

                return sb.ToString();
            }

            return contentToken.ToString();
        }

        private static string ParseAssistantMessageVisibleText(JToken msg)
        {
            if (msg == null || msg.Type == JTokenType.Null || msg.Type == JTokenType.Undefined)
            {
                return "";
            }

            if (msg.Type == JTokenType.String)
            {
                return StripRedactedThinkingBlock(msg.Value<string>() ?? "");
            }

            if (msg.Type != JTokenType.Object)
            {
                return "";
            }

            JObject m = (JObject)msg;

            string content = StripRedactedThinkingBlock(ExtractMessageContentString(m["content"]));
            if (!string.IsNullOrWhiteSpace(content))
            {
                return content;
            }

            return "";
        }

        private static IEnumerable<MEAI.ChatResponseUpdate> ParseSseUpdates(string raw,
            SseToolCallAccumulator accumulator)
        {
            string[] lines = raw.Split('\n');
            foreach (string line in lines)
            {
                string trimmed = line.Trim();
                // OpenAI uses "data: {...}"; some local servers (LM Studio, llama.cpp) omit the space after "data:".
                if (!trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string data = trimmed.Length <= 5 ? "" : trimmed.Substring(5).TrimStart();
                if (string.IsNullOrEmpty(data))
                {
                    continue;
                }

                if (data == "[DONE]")
                {
                    yield break;
                }

                MEAI.ChatResponseUpdate update = ExtractDeltaUpdate(data, accumulator);
                if (update != null)
                {
                    yield return update;
                }
            }
        }

        private static bool IsSseDoneLine(string line)
        {
            string trimmed = line?.Trim();
            if (string.IsNullOrEmpty(trimmed) ||
                !trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string data = trimmed.Length <= 5 ? "" : trimmed.Substring(5).TrimStart();
            return string.Equals(data, "[DONE]", StringComparison.Ordinal);
        }

        /// <summary>EditMode tests: full SSE line(s) including the <c>data:</c> prefix.</summary>
        internal static IEnumerable<MEAI.ChatResponseUpdate> ParseSseUpdatesForTests(string raw)
        {
            return ParseSseUpdates(raw, new SseToolCallAccumulator());
        }

        internal static bool IsSseDoneLineForTests(string line)
        {
            return IsSseDoneLine(line);
        }

        private static MEAI.ChatResponseUpdate ExtractDeltaUpdate(string json, SseToolCallAccumulator accumulator)
        {
            try
            {
                JObject obj = JObject.Parse(json);
                JToken choice0 = null;
                if (obj["choices"] is JArray choiceArr && choiceArr.Count > 0)
                {
                    choice0 = choiceArr[0];
                }

                if (choice0 == null || choice0.Type != JTokenType.Object)
                {
                    return TryParseStreamingUsageChunk(obj);
                }

                JObject choice = (JObject)choice0;
                JToken delta = choice["delta"];

                if (delta != null && delta.Type == JTokenType.Object)
                {
                    JObject deltaObj = (JObject)delta;
                    _ = deltaObj["reasoning_content"]?.ToString();

                    string deltaContent = deltaObj["content"]?.ToString();
                    JArray toolCallsArray = deltaObj["tool_calls"] as JArray;

                    if (toolCallsArray != null && toolCallsArray.Count > 0)
                    {
                        foreach (JToken tc in toolCallsArray)
                        {
                            int? index = tc["index"]?.Value<int>();
                            string callId = tc["id"]?.ToString();
                            JToken func = tc["function"];
                            string name = func?["name"]?.ToString();
                            string argsFrag = func?["arguments"]?.ToString();

                            accumulator.Feed(index, callId, name, argsFrag);
                        }
                    }

                    if (!string.IsNullOrEmpty(deltaContent))
                    {
                        return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, deltaContent);
                    }
                }

                // Local servers (LM Studio / llama.cpp) sometimes stream only `message` or `text` per chunk.
                string messageText = ParseAssistantMessageVisibleText(choice["message"]);
                if (!string.IsNullOrWhiteSpace(messageText))
                {
                    return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, messageText);
                }

                JToken legacyText = choice["text"];
                if (legacyText != null && legacyText.Type == JTokenType.String)
                {
                    string t = StripRedactedThinkingBlock(legacyText.Value<string>() ?? "");
                    if (!string.IsNullOrWhiteSpace(t))
                    {
                        return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, t);
                    }
                }

                return TryParseStreamingUsageChunk(obj);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>OpenAI streaming: final SSE object may have <c>choices: []</c> and root <c>usage</c> when <c>stream_options.include_usage</c> is set.</summary>
        private static MEAI.ChatResponseUpdate TryParseStreamingUsageChunk(JObject obj)
        {
            JToken usageTok = obj["usage"];
            if (usageTok == null || usageTok.Type == JTokenType.Null)
            {
                return null;
            }

            if (usageTok is not JObject uo)
            {
                return null;
            }

            MEAI.UsageDetails details = BuildUsageDetailsFromOpenAiUsageObject(uo);
            if (details == null)
            {
                return null;
            }

            MEAI.ChatResponseUpdate update = new(MEAI.ChatRole.Assistant, "");
            update.Contents = new List<MEAI.AIContent> { new MEAI.UsageContent(details) };
            return update;
        }

        private static MEAI.UsageDetails BuildUsageDetailsFromOpenAiUsageObject(JObject usage)
        {
            int prompt = usage["prompt_tokens"]?.ToObject<int>() ?? 0;
            int completion = usage["completion_tokens"]?.ToObject<int>() ?? 0;
            int total = usage["total_tokens"]?.ToObject<int>() ?? 0;
            MEAI.AdditionalPropertiesDictionary<long> additionalCounts = BuildAdditionalUsageCounts(usage);
            if (prompt == 0 && completion == 0 && total == 0 && (additionalCounts == null || additionalCounts.Count == 0))
            {
                return null;
            }

            if (total == 0)
            {
                total = prompt + completion;
            }

            return new MEAI.UsageDetails
            {
                InputTokenCount = prompt,
                OutputTokenCount = completion,
                TotalTokenCount = total,
                AdditionalCounts = additionalCounts
            };
        }

        private static MEAI.AdditionalPropertiesDictionary<long> BuildAdditionalUsageCounts(JObject usage)
        {
            if (usage == null)
            {
                return null;
            }

            MEAI.AdditionalPropertiesDictionary<long> counts = new();
            AddAdditionalUsageCounts(counts, usage, "");
            return counts.Count > 0 ? counts : null;
        }

        private static void AddAdditionalUsageCounts(
            MEAI.AdditionalPropertiesDictionary<long> counts,
            JToken token,
            string prefix)
        {
            if (counts == null || token == null)
            {
                return;
            }

            if (token is JObject obj)
            {
                foreach (JProperty property in obj.Properties())
                {
                    string childPrefix = string.IsNullOrEmpty(prefix)
                        ? property.Name
                        : prefix + "." + property.Name;
                    AddAdditionalUsageCounts(counts, property.Value, childPrefix);
                }

                return;
            }

            if (token.Type != JTokenType.Integer || string.IsNullOrEmpty(prefix))
            {
                return;
            }

            long value = token.Value<long>();
            if (value > 0 &&
                !string.Equals(prefix, "prompt_tokens", StringComparison.Ordinal) &&
                !string.Equals(prefix, "completion_tokens", StringComparison.Ordinal) &&
                !string.Equals(prefix, "total_tokens", StringComparison.Ordinal))
            {
                counts[prefix] = value;
            }
        }

        internal static MEAI.ChatResponseUpdate ParseSseDataLineForTests(string dataJson)
        {
            return ExtractDeltaUpdate(dataJson, new SseToolCallAccumulator());
        }

        /// <summary>
        /// EditMode tests: feed several streaming <c>delta</c> data-line JSON payloads into a single
        /// accumulator (mirroring multi-chunk <c>tool_calls</c> reassembly) then return the flushed update.
        /// </summary>
        internal static MEAI.ChatResponseUpdate AccumulateToolCallDeltasForTests(IEnumerable<string> dataJsonChunks)
        {
            SseToolCallAccumulator accumulator = new();
            foreach (string dataJson in dataJsonChunks)
            {
                ExtractDeltaUpdate(dataJson, accumulator);
            }

            return accumulator.Flush();
        }

        /// <summary>
        /// EditMode tests: feed streaming <c>delta</c> data-line JSON payloads one by one and report,
        /// per chunk, which tool calls DrainCompleted() surfaced at that point (execute-as-you-stream
        /// timing), plus the final Flush() leftovers as the last element.
        /// </summary>
        internal static List<MEAI.ChatResponseUpdate> DrainPerChunkForTests(IEnumerable<string> dataJsonChunks)
        {
            SseToolCallAccumulator accumulator = new();
            List<MEAI.ChatResponseUpdate> perChunk = new();
            foreach (string dataJson in dataJsonChunks)
            {
                ExtractDeltaUpdate(dataJson, accumulator);
                perChunk.Add(accumulator.DrainCompleted());
            }

            perChunk.Add(accumulator.Flush());
            return perChunk;
        }

        /// <summary>EditMode tests: direct access to the complete-JSON-object detector.</summary>
        internal static bool IsCompleteJsonObjectForTests(string s) =>
            SseToolCallAccumulator.IsCompleteJsonObject(s);

        /// <summary>EditMode tests: marker key carrying the raw argument string when JSON parsing failed.</summary>
        internal static string ToolCallRawArgumentsKeyForTests => SseToolCallAccumulator.RawArgumentsKey;

        /// <summary>EditMode tests: marker key set when accumulated tool-call arguments could not be parsed.</summary>
        internal static string ToolCallParseErrorKeyForTests => SseToolCallAccumulator.ParseErrorKey;

        /// <summary>
        /// Accumulates OpenAI streaming <c>delta.tool_calls</c> fragments keyed by stable call id when
        /// present, otherwise by tool-call index. Parallel compliant calls accumulate independently.
        /// </summary>
        private sealed class SseToolCallAccumulator
        {
            /// <summary>Marker key carrying the raw argument string when it failed to parse as JSON.</summary>
            internal const string RawArgumentsKey = ToolCallArgumentMarkers.RawArgumentsKey;

            /// <summary>Marker key (boolean) set when the accumulated arguments could not be parsed as JSON.</summary>
            internal const string ParseErrorKey = ToolCallArgumentMarkers.ParseErrorKey;

            private readonly List<PendingToolCall> _pending = new();
            private readonly Dictionary<string, PendingToolCall> _pendingById = new(StringComparer.Ordinal);
            private readonly Dictionary<int, PendingToolCall> _pendingByIndex = new();

            // Tombstones for calls already surfaced by DrainCompleted(). One accumulator instance
            // exists per stream response (created fresh per attempt in GetStreamingResponseAsync),
            // so these are per-response by construction and need no explicit reset. They stop
            // misbehaving OpenAI-compat servers that re-send cumulative argument strings (or
            // trailing empty deltas) for a call that already drained and EXECUTED from creating a
            // fresh pending entry and running the call a second time.
            private readonly HashSet<int> _drainedIndexes = new();
            private readonly HashSet<string> _drainedIds = new(StringComparer.Ordinal);
            private bool _warnedIgnoredDrainedFragment;

            private readonly ILog _log;
            private int _nextSequence;

            public SseToolCallAccumulator(ILog log = null)
            {
                _log = log ?? NullLog.Instance;
            }

            /// <summary>
            /// Feeds one streaming tool-call delta fragment. The first delta for an index creates the entry;
            /// later deltas update <paramref name="callId"/>/<paramref name="name"/> only when non-empty and
            /// always append <paramref name="argumentsFragment"/> to the same buffer, so name/id/args may
            /// arrive in any order across chunks.
            /// </summary>
            public void Feed(int? index, string callId, string name, string argumentsFragment)
            {
                string stableId = string.IsNullOrWhiteSpace(callId) ? null : callId;
                PendingToolCall entry = ResolveEntry(index, stableId, name, argumentsFragment);
                if (entry == null)
                {
                    return;
                }

                if (!string.IsNullOrEmpty(stableId) && string.IsNullOrEmpty(entry.Id))
                {
                    entry.Id = stableId;
                    _pendingById[stableId] = entry;
                }

                if (!string.IsNullOrEmpty(name))
                {
                    entry.Name = name;
                }

                if (!string.IsNullOrEmpty(argumentsFragment))
                {
                    entry.Arguments.Append(argumentsFragment);
                }
            }

            private PendingToolCall ResolveEntry(int? index, string stableId, string name, string argumentsFragment)
            {
                if (IsDrainedTombstone(index, stableId))
                {
                    if (!_warnedIgnoredDrainedFragment)
                    {
                        _warnedIgnoredDrainedFragment = true;
                        _log.Warn(
                            "MeaiOpenAiChatClient: ignoring streamed tool-call fragment for an already-drained call " +
                            $"(index={(index.HasValue ? index.Value.ToString() : "n/a")}, id='{stableId ?? ""}') - " +
                            "provider re-sent cumulative arguments or a trailing empty delta after the call executed.",
                            LogTag.Llm);
                    }

                    return null;
                }

                if (!string.IsNullOrEmpty(stableId) &&
                    _pendingById.TryGetValue(stableId, out PendingToolCall byId))
                {
                    AttachIndexIfSafe(byId, index);
                    return byId;
                }

                if (index.HasValue && _pendingByIndex.TryGetValue(index.Value, out PendingToolCall byIndex))
                {
                    if (!string.IsNullOrEmpty(stableId) &&
                        !string.IsNullOrEmpty(byIndex.Id) &&
                        !string.Equals(byIndex.Id, stableId, StringComparison.Ordinal))
                    {
                        return CreateEntry(index, stableId);
                    }

                    return byIndex;
                }

                if (!index.HasValue && string.IsNullOrEmpty(stableId))
                {
                    if (_pending.Count == 1)
                    {
                        return _pending[0];
                    }

                    if (_pending.Count > 1)
                    {
                        MarkAmbiguousMissingIndex(name, argumentsFragment);
                        return null;
                    }
                }

                return CreateEntry(index, stableId);
            }

            /// <summary>
            /// True when the fragment refers to a call that already drained (and executed). A fragment
            /// carrying a FRESH stable id that merely reuses a drained index is a genuinely new call,
            /// so the id check takes precedence over the index tombstone.
            /// </summary>
            private bool IsDrainedTombstone(int? index, string stableId)
            {
                if (!string.IsNullOrEmpty(stableId))
                {
                    return _drainedIds.Contains(stableId);
                }

                return index.HasValue && _drainedIndexes.Contains(index.Value);
            }

            private void AttachIndexIfSafe(PendingToolCall entry, int? index)
            {
                if (!index.HasValue || entry.Index.HasValue)
                {
                    return;
                }

                if (_pendingByIndex.TryGetValue(index.Value, out PendingToolCall existing) &&
                    !ReferenceEquals(existing, entry))
                {
                    return;
                }

                entry.Index = index;
                _pendingByIndex[index.Value] = entry;
            }

            private PendingToolCall CreateEntry(int? index, string stableId)
            {
                PendingToolCall entry = new()
                {
                    Index = index,
                    Id = stableId,
                    Sequence = _nextSequence++
                };
                _pending.Add(entry);

                if (index.HasValue)
                {
                    _pendingByIndex[index.Value] = entry;
                    // A new call (fresh id) reusing a drained index takes the index over: id-less
                    // follow-up fragments for it must route here, not be swallowed by the tombstone.
                    _drainedIndexes.Remove(index.Value);
                }

                if (!string.IsNullOrEmpty(stableId))
                {
                    _pendingById[stableId] = entry;
                }

                return entry;
            }

            private void MarkAmbiguousMissingIndex(string name, string argumentsFragment)
            {
                foreach (PendingToolCall pending in _pending)
                {
                    pending.ForceParseError = true;
                }

                _log.Warn(
                    "MeaiOpenAiChatClient: streamed tool-call fragment had no id/index while multiple calls were pending; " +
                    $"dropping ambiguous fragment instead of merging it (name='{name ?? ""}', args length={argumentsFragment?.Length ?? 0}).",
                    LogTag.Llm);
            }

            /// <summary>
            /// Drains pending calls whose accumulated arguments already form one COMPLETE JSON
            /// object, emitting them WITHOUT waiting for the stream to end. This is what lets the
            /// client execute tool calls while the model is still generating the rest of the turn
            /// (execute-as-you-stream). To preserve the provider's tool_calls index order across
            /// chunks (dependent pairs like create -> configure must never run out of order), only
            /// the longest CONTIGUOUS PREFIX of the (index, sequence) order in which every entry
            /// is ready is drained: an entry that is not ready yet (no name, ambiguity mark, or
            /// still-open JSON) blocks every later entry, which stays pending and drains on a
            /// later call or at <see cref="Flush"/> (which also handles malformed/truncated cases).
            /// </summary>
            public MEAI.ChatResponseUpdate DrainCompleted()
            {
                if (_pending.Count == 0)
                {
                    return null;
                }

                List<PendingToolCall> ready = null;
                foreach (PendingToolCall pending in _pending
                             .OrderBy(p => p.Index ?? int.MaxValue)
                             .ThenBy(p => p.Sequence))
                {
                    if (!IsReadyToDrain(pending))
                    {
                        break; // an unready entry blocks everything after it (index-order contract)
                    }

                    (ready ??= new List<PendingToolCall>()).Add(pending);
                }

                if (ready == null)
                {
                    return null;
                }

                MEAI.ChatResponseUpdate update = new(MEAI.ChatRole.Assistant, "");
                update.Contents = new List<MEAI.AIContent>();
                foreach (PendingToolCall pending in ready)
                {
                    Dictionary<string, object> args =
                        ParseArguments(pending.Arguments.ToString(), pending.Name, pending);
                    update.Contents.Add(new MEAI.FunctionCallContent(
                        pending.Id ?? $"sse_{pending.Name}_{Guid.NewGuid():N}",
                        pending.Name, args));

                    _pending.Remove(pending);
                    if (!string.IsNullOrEmpty(pending.Id))
                    {
                        _pendingById.Remove(pending.Id);
                        _drainedIds.Add(pending.Id);
                    }

                    if (pending.Index.HasValue)
                    {
                        _pendingByIndex.Remove(pending.Index.Value);
                        _drainedIndexes.Add(pending.Index.Value);
                    }
                }

                return update;
            }

            private static bool IsReadyToDrain(PendingToolCall pending)
            {
                if (string.IsNullOrEmpty(pending.Name) || pending.ForceParseError)
                {
                    return false;
                }

                string argsStr = pending.Arguments.ToString();
                return argsStr.Length > 0 && IsCompleteJsonObject(argsStr);
            }

            /// <summary>
            /// True when <paramref name="s"/> is exactly one complete JSON object (balanced braces
            /// outside strings, string/escape aware, only whitespace after the close). OpenAI tool
            /// arguments are always a single object, so "balanced and closed" means "no more
            /// fragments are coming" for a sane provider.
            /// </summary>
            internal static bool IsCompleteJsonObject(string s)
            {
                int i = 0;
                while (i < s.Length && char.IsWhiteSpace(s[i]))
                {
                    i++;
                }

                if (i >= s.Length || s[i] != '{')
                {
                    return false;
                }

                int depth = 0;
                bool inString = false;
                for (; i < s.Length; i++)
                {
                    char c = s[i];
                    if (inString)
                    {
                        if (c == '\\')
                        {
                            i++; // skip the escaped char (covers \" and \\)
                        }
                        else if (c == '"')
                        {
                            inString = false;
                        }
                    }
                    else if (c == '"')
                    {
                        inString = true;
                    }
                    else if (c == '{')
                    {
                        depth++;
                    }
                    else if (c == '}')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            for (i++; i < s.Length; i++)
                            {
                                if (!char.IsWhiteSpace(s[i]))
                                {
                                    return false; // trailing junk: not exactly one object
                                }
                            }

                            return true;
                        }
                    }
                }

                return false;
            }

            public MEAI.ChatResponseUpdate Flush()
            {
                if (_pending.Count == 0)
                {
                    return null;
                }

                MEAI.ChatResponseUpdate update = new(MEAI.ChatRole.Assistant, "");
                update.Contents = new List<MEAI.AIContent>();

                // Emit in ascending tool-call index order so the FunctionCallContent order is
                // deterministic and matches the provider's tool_calls index order.
                foreach (PendingToolCall pending in _pending
                             .OrderBy(p => p.Index ?? int.MaxValue)
                             .ThenBy(p => p.Sequence))
                {
                    string argsStr = pending.Arguments.ToString();

                    if (string.IsNullOrEmpty(pending.Name))
                    {
                        // An entry with accumulated id/args but no name cannot be invoked. Do not let it
                        // silently vanish - surface it so the loss is observable.
                        if (!string.IsNullOrEmpty(pending.Id) || !string.IsNullOrEmpty(argsStr))
                        {
                            _log.Warn(
                                $"MeaiOpenAiChatClient: dropped streamed tool call at {pending.IdentityLabel} - missing function name " +
                                $"(id='{pending.Id ?? ""}', args length={argsStr.Length}).",
                                LogTag.Llm);
                        }

                        continue;
                    }

                    Dictionary<string, object> args = ParseArguments(argsStr, pending.Name, pending);
                    update.Contents.Add(new MEAI.FunctionCallContent(
                        pending.Id ?? $"sse_{pending.Name}_{Guid.NewGuid():N}",
                        pending.Name, args));
                }

                _pending.Clear();
                _pendingById.Clear();
                _pendingByIndex.Clear();
                return update.Contents.Count > 0 ? update : null;
            }

            /// <summary>
            /// Parses accumulated argument JSON. A non-empty but malformed/truncated string is NOT silently
            /// dropped: it is surfaced under <see cref="RawArgumentsKey"/>/<see cref="ParseErrorKey"/> markers
            /// and a warning is logged, so callers can detect and recover from a broken stream.
            /// </summary>
            private Dictionary<string, object> ParseArguments(string argsStr, string name, PendingToolCall pending)
            {
                if (pending.ForceParseError)
                {
                    _log.Warn(
                        $"MeaiOpenAiChatClient: streamed tool call '{name}' ({pending.IdentityLabel}) was marked ambiguous - " +
                        "surfacing raw arguments instead of invoking with possibly merged args.",
                        LogTag.Llm);
                    return BuildParseErrorArguments(argsStr);
                }

                if (string.IsNullOrEmpty(argsStr))
                {
                    return new Dictionary<string, object>();
                }

                try
                {
                    Dictionary<string, object> parsed =
                        JsonConvert.DeserializeObject<Dictionary<string, object>>(argsStr);
                    if (parsed != null)
                    {
                        return parsed;
                    }
                }
                catch (JsonException ex)
                {
                    _log.Warn(
                        $"MeaiOpenAiChatClient: streamed tool call '{name}' ({pending.IdentityLabel}) had malformed/truncated " +
                        $"arguments JSON - surfacing raw string instead of empty args: {ex.Message}",
                        LogTag.Llm);
                }

                return BuildParseErrorArguments(argsStr);
            }

            private static Dictionary<string, object> BuildParseErrorArguments(string argsStr)
            {
                return new Dictionary<string, object>
                {
                    { RawArgumentsKey, argsStr },
                    { ParseErrorKey, true }
                };
            }

            /// <summary>Mutable accumulation state for one in-progress streamed tool call.</summary>
            private sealed class PendingToolCall
            {
                public int? Index;
                public string Id;
                public string Name;
                public int Sequence;
                public bool ForceParseError;
                public readonly StringBuilder Arguments = new();

                public string IdentityLabel => Index.HasValue ? $"index {Index.Value}" : $"sequence {Sequence}";
            }
        }

        /// <summary>Test hook: exposes the wire-payload message serialization.</summary>
        internal static List<Dictionary<string, object>> BuildMessagesPayloadForTests(List<MEAI.ChatMessage> msgs)
            => BuildMessagesPayloadStatic(msgs);

        private List<Dictionary<string, object>> BuildMessagesPayload(List<MEAI.ChatMessage> msgs)
            => BuildMessagesPayloadStatic(msgs);

        private static List<Dictionary<string, object>> BuildMessagesPayloadStatic(List<MEAI.ChatMessage> msgs)
        {
            List<Dictionary<string, object>> messages = new();
            foreach (MEAI.ChatMessage msg in msgs)
            {
                string content = msg.Text ?? "";
                if (string.IsNullOrEmpty(content) && msg.Contents != null && msg.Contents.Count > 0)
                {
                    MEAI.TextContent textContent = msg.Contents.OfType<MEAI.TextContent>().FirstOrDefault();
                    if (textContent != null)
                    {
                        content = textContent.Text;
                    }
                    else
                    {
                        content = string.Join("\n", msg.Contents.Select(c => c.ToString()));
                    }
                }

                Dictionary<string, object> msgDict = new()
                {
                    { "role", msg.Role.ToString().ToLowerInvariant() }
                };

                if (msg.Role == MEAI.ChatRole.Tool && msg.Contents != null)
                {
                    // One MEAI Tool message can carry SEVERAL FunctionResultContent items (the tool
                    // loop appends the whole turn's results as a single message). The OpenAI wire
                    // protocol requires ONE tool-role message PER tool_call_id — serializing only the
                    // first result left the model with N tool_calls but a single answer, and models
                    // then legitimately re-issued the "unanswered" calls every round-trip (observed
                    // live: a 5-spawn turn ballooned to 15 executed spawns).
                    List<MEAI.FunctionResultContent> functionResults =
                        msg.Contents.OfType<MEAI.FunctionResultContent>().ToList();
                    if (functionResults.Count > 0)
                    {
                        foreach (MEAI.FunctionResultContent functionResult in functionResults)
                        {
                            Dictionary<string, object> resultDict = new()
                            {
                                { "role", "tool" }
                            };
                            if (!string.IsNullOrEmpty(functionResult.CallId))
                            {
                                resultDict["tool_call_id"] = functionResult.CallId;
                            }

                            string resultStr = functionResult.Result as string
                                               ?? (functionResult.Result != null
                                                   ? JsonConvert.SerializeObject(functionResult.Result)
                                                   : "");
                            resultDict["content"] = string.IsNullOrEmpty(resultStr) ? "success" : resultStr;
                            messages.Add(resultDict);
                        }

                        continue;
                    }

                    msgDict["content"] = content;
                }
                else if (msg.Role == MEAI.ChatRole.Assistant && msg.Contents != null)
                {
                    List<MEAI.FunctionCallContent> funcCalls = msg.Contents.OfType<MEAI.FunctionCallContent>().ToList();
                    if (funcCalls.Count > 0)
                    {
                        List<Dictionary<string, object>> toolCallsList = new();
                        foreach (MEAI.FunctionCallContent call in funcCalls)
                        {
                            toolCallsList.Add(new Dictionary<string, object>
                            {
                                { "id", call.CallId ?? Guid.NewGuid().ToString() },
                                { "type", "function" },
                                {
                                    "function", new Dictionary<string, object>
                                    {
                                        { "name", call.Name },
                                        {
                                            "arguments",
                                            JsonConvert.SerializeObject(call.Arguments ??
                                                                        new Dictionary<string, object?>())
                                        }
                                    }
                                }
                            });
                        }

                        msgDict["tool_calls"] = toolCallsList;
                        MEAI.TextContent textContent = msg.Contents.OfType<MEAI.TextContent>().FirstOrDefault();
                        msgDict["content"] = textContent?.Text ?? "";
                    }
                    else
                    {
                        msgDict["content"] = content;
                    }
                }
                else
                {
                    msgDict["content"] = BuildOpenAiMessageContent(content, msg.Contents);
                }

                messages.Add(msgDict);
            }

            return messages;
        }

        /// <summary>
        /// Builds the OpenAI <c>content</c> value for a message. When the message carries image content
        /// (<see cref="MEAI.DataContent"/> or <see cref="MEAI.UriContent"/> with an <c>image/*</c> media
        /// type), the value is a multimodal parts array (<c>{type:"text"}</c> + one or more
        /// <c>{type:"image_url"}</c>) so vision-capable models receive the image; otherwise it is the plain
        /// text string (unchanged behavior for text-only messages). Public for test verification.
        /// </summary>
        public static object BuildOpenAiMessageContent(string text, IList<MEAI.AIContent> contents)
        {
            List<string> imageUrls = null;
            if (contents != null)
            {
                foreach (MEAI.AIContent c in contents)
                {
                    string url = TryGetImageUrl(c);
                    if (!string.IsNullOrEmpty(url))
                    {
                        (imageUrls ??= new List<string>()).Add(url);
                    }
                }
            }

            if (imageUrls == null)
            {
                return text ?? "";
            }

            List<object> parts = new();
            if (!string.IsNullOrEmpty(text))
            {
                parts.Add(new Dictionary<string, object> { { "type", "text" }, { "text", text } });
            }

            foreach (string url in imageUrls)
            {
                parts.Add(new Dictionary<string, object>
                {
                    { "type", "image_url" },
                    { "image_url", new Dictionary<string, object> { { "url", url } } }
                });
            }

            return parts;
        }

        private static string TryGetImageUrl(MEAI.AIContent content)
        {
            switch (content)
            {
                case MEAI.DataContent data when IsImageMediaType(data.MediaType):
                    return $"data:{NormalizeImageMediaType(data.MediaType)};base64," +
                           Convert.ToBase64String(data.Data.ToArray());
                case MEAI.UriContent uri when IsImageMediaType(uri.MediaType):
                    return uri.Uri?.ToString();
                default:
                    return null;
            }
        }

        private static bool IsImageMediaType(string mediaType)
        {
            return !string.IsNullOrEmpty(mediaType) &&
                   mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeImageMediaType(string mediaType)
        {
            return string.IsNullOrEmpty(mediaType) ? "image/jpeg" : mediaType;
        }

        private static List<Dictionary<string, object>> BuildToolsPayload(MEAI.ChatOptions? options)
        {
            List<Dictionary<string, object>> toolsList = new();
            if (options?.Tools != null)
            {
                foreach (MEAI.AITool tool in options.Tools)
                {
                    if (tool is MEAI.AIFunction af)
                    {
                        toolsList.Add(new Dictionary<string, object>
                        {
                            { "type", "function" },
                            {
                                "function", new Dictionary<string, object>
                                {
                                    { "name", af.Name },
                                    { "description", af.Description },
                                    { "parameters", JsonConvert.DeserializeObject(af.JsonSchema.ToString()) }
                                }
                            }
                        });
                    }
                }
            }

            return toolsList;
        }

        private void ApplyProviderSpecificRequestBody(Dictionary<string, object> reqBody)
        {
            if (reqBody == null)
            {
                return;
            }

            string extraBodyJson = _settings.ExtraBodyJson;
            if (!string.IsNullOrWhiteSpace(extraBodyJson))
            {
                try
                {
                    JObject extra = JObject.Parse(extraBodyJson);
                    foreach (JProperty property in extra.Properties())
                    {
                        reqBody[property.Name] = property.Value;
                    }
                }
                catch (Exception ex)
                {
                    _log.Warn($"MeaiOpenAiChatClient: ignored invalid ExtraBodyJson: {ex.Message}", LogTag.Llm);
                }
            }

            if (!_settings.SendReasoningControls)
            {
                return;
            }

            bool enableThinking = _settings.EnableReasoning;
            reqBody["enable_thinking"] = enableThinking;
            ApplyChatTemplateThinkingFlag(reqBody, enableThinking);

            int thinkingBudget = _settings.ThinkingBudgetTokens;
            if (thinkingBudget > 0)
            {
                reqBody["thinking_budget"] = thinkingBudget;
            }
        }

        private static void ApplyChatTemplateThinkingFlag(Dictionary<string, object> reqBody, bool enableThinking)
        {
            const string key = "chat_template_kwargs";
            if (reqBody.TryGetValue(key, out object existing) && existing is JObject jObject)
            {
                jObject["enable_thinking"] = enableThinking;
                return;
            }

            if (existing is Dictionary<string, object> dict)
            {
                dict["enable_thinking"] = enableThinking;
                return;
            }

            reqBody[key] = new Dictionary<string, object>
            {
                { "enable_thinking", enableThinking }
            };
        }

        public void Dispose()
        {
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return null;
        }
    }
}
#endif
