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

                    SseToolCallAccumulator toolAccumulator = new();
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

                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            lastProgressUtc = DateTime.UtcNow;
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
                            _log.Warn(
                                $"MeaiOpenAiChatClient: HTTP 200 but 0 parsed SSE deltas (likely only upstream keep-alive comments - provider/model produced no tokens). " +
                                $"Retrying (attempt {attempt + 1}/{transientLocalLlmReloadMaxAttempts}) after backoff...",
                                LogTag.Llm);
                            await BackoffDelayAsync(Math.Min(6000, 900 * attempt), cancellationToken);
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
            Task timeoutTask = Task.Delay(timeoutMs, cancellationToken);
            Task completed = await Task.WhenAny(readTask, timeoutTask);
            if (ReferenceEquals(completed, readTask))
            {
                return await readTask;
            }

            cancellationToken.ThrowIfCancellationRequested();
            throw new LlmClientException(
                $"LLM SSE stalled - no data for {Math.Max(1, streamIdleTimeoutSeconds)}s.",
                LlmErrorCode.Timeout);
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
                    response.Usage = new MEAI.UsageDetails
                    {
                        InputTokenCount = usage["prompt_tokens"]?.ToObject<int>() ?? 0,
                        OutputTokenCount = usage["completion_tokens"]?.ToObject<int>() ?? 0,
                        TotalTokenCount = usage["total_tokens"]?.ToObject<int>() ?? 0
                    };
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

            foreach (string key in new[] { "reasoning_content", "reasoningContent", "reasoning" })
            {
                JToken t = SelectPropertyCaseInsensitive(m, key);
                if (t == null || t.Type == JTokenType.Null)
                {
                    continue;
                }

                if (t.Type != JTokenType.String)
                {
                    continue;
                }

                string reasoning = t.Value<string>() ?? "";
                reasoning = StripRedactedThinkingBlock(reasoning);
                if (!string.IsNullOrWhiteSpace(reasoning))
                {
                    return reasoning;
                }
            }

            return "";
        }

        private static JToken SelectPropertyCaseInsensitive(JObject obj, string name)
        {
            foreach (JProperty p in obj.Properties())
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return p.Value;
                }
            }

            return null;
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
                            int index = tc["index"]?.Value<int>() ?? 0;
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
            if (prompt == 0 && completion == 0 && total == 0)
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
                TotalTokenCount = total
            };
        }

        internal static MEAI.ChatResponseUpdate ParseSseDataLineForTests(string dataJson)
        {
            return ExtractDeltaUpdate(dataJson, new SseToolCallAccumulator());
        }

        private sealed class SseToolCallAccumulator
        {
            private readonly Dictionary<int, (string id, string name, StringBuilder args)> _pending = new();

            public void Feed(int index, string callId, string name, string argumentsFragment)
            {
                if (!_pending.TryGetValue(index, out (string id, string name, StringBuilder args) entry))
                {
                    entry = (callId, name, new StringBuilder());
                    _pending[index] = entry;
                }
                else
                {
                    if (!string.IsNullOrEmpty(callId))
                    {
                        entry.id = callId;
                    }

                    if (!string.IsNullOrEmpty(name))
                    {
                        entry.name = name;
                    }

                    _pending[index] = entry;
                }

                if (!string.IsNullOrEmpty(argumentsFragment))
                {
                    _pending[index] = (_pending[index].id, _pending[index].name, _pending[index].args);
                    _pending[index].args.Append(argumentsFragment);
                }
            }

            public MEAI.ChatResponseUpdate Flush()
            {
                if (_pending.Count == 0)
                {
                    return null;
                }

                MEAI.ChatResponseUpdate update = new(MEAI.ChatRole.Assistant, "");
                update.Contents = new List<MEAI.AIContent>();

                foreach (KeyValuePair<int, (string id, string name, StringBuilder args)> kvp in _pending)
                {
                    (string id, string name, StringBuilder argsBuilder) = kvp.Value;
                    if (string.IsNullOrEmpty(name))
                    {
                        continue;
                    }

                    Dictionary<string, object> args = null;
                    string argsStr = argsBuilder.ToString();
                    if (!string.IsNullOrEmpty(argsStr))
                    {
                        try
                        {
                            args = JsonConvert.DeserializeObject<Dictionary<string, object>>(argsStr);
                        }
                        catch
                        {
                        }
                    }

                    args ??= new Dictionary<string, object>();
                    update.Contents.Add(new MEAI.FunctionCallContent(
                        id ?? $"sse_{name}_{Guid.NewGuid():N}",
                        name, args));
                }

                _pending.Clear();
                return update.Contents.Count > 0 ? update : null;
            }
        }

        private List<Dictionary<string, object>> BuildMessagesPayload(List<MEAI.ChatMessage> msgs)
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
                    MEAI.FunctionResultContent functionResult =
                        msg.Contents.OfType<MEAI.FunctionResultContent>().FirstOrDefault();
                    if (functionResult != null)
                    {
                        if (!string.IsNullOrEmpty(functionResult.CallId))
                        {
                            msgDict["tool_call_id"] = functionResult.CallId;
                        }

                        string resultStr = functionResult.Result as string
                                           ?? (functionResult.Result != null
                                               ? JsonConvert.SerializeObject(functionResult.Result)
                                               : "");
                        msgDict["content"] = string.IsNullOrEmpty(resultStr) ? "success" : resultStr;
                    }
                    else
                    {
                        msgDict["content"] = content;
                    }
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
                    msgDict["content"] = content;
                }

                messages.Add(msgDict);
            }

            return messages;
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
