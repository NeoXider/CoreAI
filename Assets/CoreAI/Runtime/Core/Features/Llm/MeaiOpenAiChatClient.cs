#if COREAI_LLM
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

        /// <summary>
        /// Starved-stream watchdog: while a streaming attempt has produced ZERO parsed deltas and the
        /// server has sent nothing but SSE comment lines (": keep-alive"), the attempt is aborted after
        /// this many seconds instead of waiting for the server to close the stream. A proxy that hides
        /// an upstream rate limit behind HTTP 200 + keep-alives can hold each attempt open for 30-60s;
        /// without this cap, the empty-stream retries alone exceed callers' turn timeouts (~120s).
        /// Internal setter is a test hook (InternalsVisibleTo CoreAI.Tests).
        /// </summary>
        internal static int StarvedStreamFirstDeltaTimeoutSeconds { get; set; } = 15;

        /// <summary>
        /// Extra attempts after a transient HTTP failure (429/408/5xx) before the request gives up:
        /// the streamed path then falls back to ONE non-streaming completion, and only if that also
        /// fails does the typed error surface - "request → retry → fallback request → error".
        /// The Retry-After header is honored when present, else 2s backoff.
        /// Internal setter is a test hook (InternalsVisibleTo CoreAI.Tests).
        /// </summary>
        internal static int RateLimitMaxRetries { get; set; } = 1;

        /// <summary>Transient HTTP statuses worth retrying: 408 timeout, 429 rate limit, any 5xx.</summary>
        private static bool IsRetryableHttpStatus(int status)
        {
            return status == 408 || status == 429 || (status >= 500 && status < 600);
        }

        /// <summary>
        /// Backoff before a rate-limit retry: Retry-After header when present, else a retry window
        /// parsed from the error body ("Please try again in 14.017s" - Groq puts it there, and on
        /// WebGL the Retry-After header is invisible to fetch unless CORS exposes it), else
        /// 2s * retry index. Capped at 20s so one retry can actually clear a typical TPM window
        /// instead of guaranteed-failing into it.
        /// </summary>
        private static int ResolveRateLimitBackoffMs(
            IReadOnlyDictionary<string, IEnumerable<string>> headers, int retryIndex, string errorBody = null)
        {
            const int capMs = 20000;
            string retryAfter = TryGetHeaderFirstValue(headers, "Retry-After");
            if (!string.IsNullOrWhiteSpace(retryAfter) &&
                double.TryParse(retryAfter, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double seconds) &&
                seconds > 0)
            {
                return (int)Math.Min(capMs, seconds * 1000);
            }

            double? bodySeconds = TryParseRetryWindowSecondsFromBody(errorBody);
            if (bodySeconds.HasValue)
            {
                // WHY: +250ms margin: providers report the window with sub-second precision and a retry
                // landing exactly on the boundary still gets rejected.
                return (int)Math.Min(capMs, bodySeconds.Value * 1000 + 250);
            }

            return Math.Min(capMs, 2000 * Math.Max(1, retryIndex));
        }

        /// <summary>
        /// Extracts a retry window from a rate-limit error body, e.g. Groq's
        /// "Please try again in 14.0175s" or "try again in 2m3.5s". Returns null when absent.
        /// </summary>
        internal static double? TryParseRetryWindowSecondsFromBody(string errorBody)
        {
            if (string.IsNullOrEmpty(errorBody))
            {
                return null;
            }

            System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(
                errorBody,
                @"try again in\s+(?:(\d+)m)?(\d+(?:\.\d+)?)s",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success)
            {
                return null;
            }

            double total = 0;
            if (m.Groups[1].Success &&
                int.TryParse(m.Groups[1].Value, out int minutes))
            {
                total += minutes * 60;
            }

            if (double.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double secs))
            {
                total += secs;
            }

            return total > 0 ? total : (double?)null;
        }

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

        /// <summary>
        /// Writes the <c>model</c> field of an OpenAI-compatible request body, or deliberately leaves it out.
        /// <para>
        /// Under <see cref="LlmExecutionMode.ServerManagedApi"/> the backend owns the model choice, so an
        /// empty client-side name is a valid configuration and the field is omitted — the server decides.
        /// In every other mode an empty name is a configuration error and fails loudly: substituting a
        /// built-in default used to send traffic to a model nobody selected, and then reported that
        /// invented name in logs, usage history and token accounting.
        /// </para>
        /// </summary>
        /// <exception cref="LlmClientException">No model is configured for a client-owned backend.</exception>
        private void AddRequestModel(Dictionary<string, object> reqBody)
        {
            string model = _settings.Model?.Trim() ?? "";
            if (model.Length > 0)
            {
                reqBody["model"] = model;
                return;
            }

            if (_settings.ExecutionMode == LlmExecutionMode.ServerManagedApi)
            {
                _log.Info(
                    "MeaiOpenAiChatClient: no client-side model configured under ServerManagedApi - omitting 'model' from the request body so the backend picks it.",
                    LogTag.Llm);
                return;
            }

            throw new LlmClientException(
                "No LLM model is configured. Set the model id on the CoreAI settings asset (or the HTTP profile), " +
                "or switch the execution mode to ServerManagedApi so the backend chooses the model.",
                LlmErrorCode.InvalidRequest);
        }

        public Task<MEAI.ChatResponse> GetResponseAsync(
            IEnumerable<MEAI.ChatMessage> chatMessages,
            MEAI.ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return GetResponseCoreAsync(chatMessages, options, RateLimitMaxRetries, cancellationToken);
        }

        /// <summary>
        /// Non-streaming completion with an explicit transient-HTTP retry budget. The public
        /// <see cref="GetResponseAsync"/> uses <see cref="RateLimitMaxRetries"/>; the streamed path's
        /// LAST-RESORT fallback passes 0 so the whole turn stays at
        /// "request → retry → one fallback request → typed error".
        /// </summary>
        private async Task<MEAI.ChatResponse> GetResponseCoreAsync(
            IEnumerable<MEAI.ChatMessage> chatMessages,
            MEAI.ChatOptions? options,
            int transientHttpRetries,
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

            Dictionary<string, object> reqBody = new();
            AddRequestModel(reqBody);
            reqBody["messages"] = messages;
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
            int transientHttpRetriesLeft = transientHttpRetries;

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
                    _log.Warn(
                        $"MeaiOpenAiChatClient: {FormatHttpErrorForLog(postResult.StatusCode, postResult.BodyText)}",
                        LogTag.Llm);

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

                    if (IsRetryableHttpStatus(postResult.StatusCode) && transientHttpRetriesLeft > 0)
                    {
                        transientHttpRetriesLeft--;
                        int transientBackoffMs = ResolveRateLimitBackoffMs(
                            postResult.ResponseHeaders, transientHttpRetries - transientHttpRetriesLeft,
                            postResult.BodyText);
                        _log.Warn(
                            $"MeaiOpenAiChatClient: HTTP {postResult.StatusCode} (transient) - retrying after {transientBackoffMs}ms ({transientHttpRetriesLeft} retries left)...",
                            LogTag.Llm);
                        await BackoffDelayAsync(transientBackoffMs, cancellationToken);
                        continue;
                    }

                    throw BuildHttpException(postResult.StatusCode, postResult.BodyText,
                        FormatHttpErrorForLog(postResult.StatusCode, postResult.BodyText),
                        postResult.ResponseHeaders);
                }
                catch (OperationCanceledException ex)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }

                    // WHY: The transport's internal timeout CTS fired while the CALLER's token is still
                    // live: this is a backend timeout, not a cancellation. Rethrowing the raw OCE made
                    // MeaiLlmClient map it to LlmErrorCode.Cancelled, which is neither retryable in
                    // LoggingLlmClientDecorator nor fallback-eligible in FallbackLlmClientDecorator -
                    // a dead primary backend then blocked the secondary provider entirely. Surfacing a
                    // typed Timeout keeps retry/fallback resilience working.
                    _log.Warn(
                        $"MeaiOpenAiChatClient: Request timed out at the transport after {transportTimeoutSec}s ({ex.GetType().Name}): {ex.Message}",
                        LogTag.Llm);
                    throw new LlmClientException(
                        $"LLM request timed out after {transportTimeoutSec}s without a response.",
                        LlmErrorCode.Timeout);
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

            return ParseResponse(responseJson, _log);
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
                MEAI.ChatResponse simulated = await GetResponseAsync(chatMessages, options, cancellationToken);
                foreach (MEAI.ChatResponseUpdate u in FullResponseToSimulatedStreamingUpdates(simulated))
                {
                    yield return u;
                }

                yield break;
            }

            List<MEAI.ChatMessage> msgs = chatMessages.ToList();
            string url = _settings.ApiBaseUrl.TrimEnd('/') + "/chat/completions";

            List<Dictionary<string, object>> messages = BuildMessagesPayload(msgs);
            List<Dictionary<string, object>> toolsList = BuildToolsPayload(options);

            Dictionary<string, object> reqBody = new();
            AddRequestModel(reqBody);
            reqBody["messages"] = messages;
            reqBody["stream"] = true;
            reqBody["stream_options"] = new Dictionary<string, object> { { "include_usage", true } };
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
            const int emptyStreamMaxAttempts = 3;
            bool fallBackToNonStreaming = false;
            int starvedStreamFirstDeltaTimeoutSec = StarvedStreamFirstDeltaTimeoutSeconds;
            int rateLimitRetriesLeft = RateLimitMaxRetries;

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
                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }

                    // WHY: The SSE header-phase timeout (HttpClientOpenAiTransport bounds only the
                    // time-to-headers with TransportTimeoutSeconds) fired while the CALLER's token is
                    // still live: a backend that accepts TCP but never sends headers is a TIMEOUT.
                    // Rethrowing the raw OCE got mapped to LlmErrorCode.Cancelled downstream (not
                    // retryable, not fallback-eligible), so the secondary provider was never tried.
                    _log.Warn(
                        $"MeaiOpenAiChatClient: stream open timed out at the transport after {streamTransportTimeoutSec}s ({ex.GetType().Name}): {ex.Message}",
                        LogTag.Llm);
                    throw new LlmClientException(
                        $"LLM stream open timed out after {streamTransportTimeoutSec}s without response headers.",
                        LlmErrorCode.Timeout);
                }
                catch (LlmClientException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // WHY: Include the inner exception: the outer "An error occurred while sending the request"
                    // is a generic wrapper; the real cause (e.g. WebException ConnectFailure = the local
                    // server refused the TCP connection, vs a read/reset mid-stream) only appears inside.
                    string innerDetail = ex.InnerException != null
                        ? $" ({ex.InnerException.GetType().Name}: {ex.InnerException.Message})"
                        : "";
                    _log.Warn($"MeaiOpenAiChatClient: stream open failed: {ex.Message}{innerDetail}", LogTag.Llm);

                    // WHY: A transport-level SEND failure (typically a pooled keep-alive connection the local
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
                        _log.Warn(
                            $"MeaiOpenAiChatClient: stream error - {FormatHttpErrorForLog(openResult.StatusCode, streamBody)}",
                            LogTag.Llm);

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

                        if (IsRetryableHttpStatus(openResult.StatusCode))
                        {
                            if (rateLimitRetriesLeft > 0)
                            {
                                rateLimitRetriesLeft--;
                                int transientBackoffMs = ResolveRateLimitBackoffMs(
                                    openResult.ResponseHeaders, RateLimitMaxRetries - rateLimitRetriesLeft,
                                    streamBody);
                                _log.Warn(
                                    $"MeaiOpenAiChatClient: HTTP {openResult.StatusCode} (transient) on stream-open - retrying after {transientBackoffMs}ms ({rateLimitRetriesLeft} retries left)...",
                                    LogTag.Llm);
                                await BackoffDelayAsync(transientBackoffMs, cancellationToken);
                                continue;
                            }

                            // WHY: Retries exhausted: LAST RESORT is one plain (non-streaming) completion in
                            // the same turn - request → retry → fallback request → typed error. The
                            // fallback runs with a ZERO transient budget so the turn ends after it.
                            _log.Warn(
                                $"MeaiOpenAiChatClient: HTTP {openResult.StatusCode} persisted after {RateLimitMaxRetries} stream retries - falling back to ONE non-streaming completion for this turn.",
                                LogTag.Llm);
                            fallBackToNonStreaming = true;
                            break;
                        }

                        throw BuildHttpException(openResult.StatusCode, streamBody,
                            FormatHttpErrorForLog(openResult.StatusCode, streamBody),
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
                    DateTime attemptStartUtc = DateTime.UtcNow;
                    bool sawNonCommentLine = false;
                    bool starvedAttemptAborted = false;
                    int parsedSseDeltas = 0;

                    // WHY: buffering (Mono on Windows can hold lines back until a larger buffer fills, collapsing
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

                        // WHY: Starved-stream early abort: a proxy hiding an upstream failure behind
                        // HTTP 200 sends only SSE comment lines (": keep-alive") and can hold the
                        // connection open far longer than callers' turn budgets. If the stream has
                        // produced nothing but comments/blank lines for the first-delta window,
                        // abandon this attempt now; the empty-stream retry/fallback below handles it.
                        if (parsedSseDeltas == 0 && !sawNonCommentLine)
                        {
                            bool isCommentOrBlank = string.IsNullOrWhiteSpace(line)
                                                    || line.StartsWith(":", StringComparison.Ordinal);
                            if (!isCommentOrBlank)
                            {
                                sawNonCommentLine = true;
                            }
                            else if ((DateTime.UtcNow - attemptStartUtc).TotalSeconds
                                     > starvedStreamFirstDeltaTimeoutSec)
                            {
                                _log.Warn(
                                    $"MeaiOpenAiChatClient: SSE stream sent only keep-alive comments for {starvedStreamFirstDeltaTimeoutSec}s with 0 parsed deltas - aborting this streaming attempt early.",
                                    LogTag.Llm);
                                starvedAttemptAborted = true;
                                break;
                            }
                        }

                        foreach (MEAI.ChatResponseUpdate update in ParseSseUpdates(line + "\n", toolAccumulator))
                        {
                            parsedSseDeltas++;
                            string updateText = update?.Text ?? "";
                            bool textOnly = !string.IsNullOrEmpty(updateText)
                                            && (update.Contents == null
                                                || update.Contents.Count == 0
                                                || update.Contents.All(c => c is MEAI.TextContent));
                            // WHY: Some upstream providers (e.g. OpenRouter `:free` models from Nvidia/etc.)
                            // batch many tokens into a single SSE delta, which makes streaming look
                            // jumpy in the UI. Re-emit large text-only deltas in small word-sized
                            // pieces with a tiny delay so the UI sees smooth per-word streaming.
                            // True per-token providers (LM Studio, paid models) already send small
                            // deltas and skip this path.
                            if (textOnly && updateText.Length > 24)
                            {
                                foreach (string piece in SplitForSmoothStreaming(updateText))
                                {
                                    yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, piece)
                                    {
                                        // WHY: the re-emitted pieces replace the original delta, so the
                                        // served model id has to travel with them or the consumer never
                                        // sees which model answered a smoothly-streamed turn.
                                        ModelId = update.ModelId
                                    };
                                    await DelayBetweenSyntheticStreamPiecesAsync(cancellationToken);
                                }
                            }
                            else
                            {
                                yield return update;
                            }
                        }

                        // WHY: Execute-as-you-stream: surface every tool call whose arguments JSON is
                        // already complete NOW, instead of holding all calls until the final Flush.
                        // The consumer (MeaiLlmClient) can then run each call while the model is
                        // still generating the rest of the turn.
                        MEAI.ChatResponseUpdate completedCalls = toolAccumulator.DrainCompleted();
                        if (completedCalls != null)
                        {
                            parsedSseDeltas++;
                            yield return completedCalls;
                        }

                        // WHY: Re-arm the stall clock only AFTER every update for this line has been
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
                        // WHY: An SSE 200 with zero deltas usually hides an upstream rate limit (e.g. an
                        // OpenRouter 429 behind a proxy): the stream is starved, but the SAME provider
                        // often still answers a plain completion. Retry the stream only a couple of
                        // times, then fall back to ONE non-streaming completion (mirroring the
                        // no-SSE-transport path) instead of burning ~a minute of backoff on a stream
                        // that will not produce tokens - the chat stays "busy" that whole time.
                        if (attempt < emptyStreamMaxAttempts)
                        {
                            // WHY: A starved-aborted attempt already spent the whole first-delta window
                            // waiting; retry immediately instead of stacking more backoff on top.
                            int emptyStreamBackoffMs =
                                starvedAttemptAborted ? 0 : Math.Min(6000, 900 * attempt);
                            _log.Warn(
                                $"MeaiOpenAiChatClient: HTTP 200 but 0 parsed SSE deltas (likely only upstream keep-alive comments - provider/model produced no tokens). " +
                                $"Retrying (attempt {attempt + 1}/{emptyStreamMaxAttempts}) after {emptyStreamBackoffMs}ms backoff...",
                                LogTag.Llm);
                            await BackoffDelayAsync(emptyStreamBackoffMs, cancellationToken);
                            continue;
                        }

                        _log.Warn(
                            "MeaiOpenAiChatClient: stream ended with HTTP success but 0 parsed SSE deltas after " +
                            $"{emptyStreamMaxAttempts} attempts - falling back to a NON-streaming completion for this turn.",
                            LogTag.Llm);
                        fallBackToNonStreaming = true;
                        break;
                    }

                    // WHY: В инциденте RedoSchool reasoning_content сохранился как готовая заметка
                    // ученика. Даже reasoning-only поток остаётся диагностикой и не становится TextContent.
                    yield break;
                }
            }

            if (!fallBackToNonStreaming)
            {
                // WHY: Falling out of the attempt loop means every attempt ended in `continue` (transient
                // 5xx/429 on stream-open). Without this the iterator would complete with zero chunks and
                // zero exceptions, and the caller would report a meaningless "stream ended without
                // content" while the real HTTP status stayed in the log only.
                _log.Warn(
                    $"MeaiOpenAiChatClient: stream exhausted all {transientLocalLlmReloadMaxAttempts} attempts without a usable response.",
                    LogTag.Llm);
                throw new LlmClientException(
                    "LLM stream exhausted all attempts without a response.",
                    LlmErrorCode.BackendUnavailable);
            }

            // WHY: Zero transient budget: this IS the fallback request; if it fails too, the typed
            // error surfaces (request → retry → fallback → error, no hidden extra rounds).
            MEAI.ChatResponse full = await GetResponseCoreAsync(msgs, options, 0, cancellationToken);
            foreach (MEAI.ChatResponseUpdate u in FullResponseToSimulatedStreamingUpdates(full))
            {
                yield return u;
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
            // WHY: Browser WebGL has no reliable worker ThreadPool. A timer-based Task.Delay here can
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
                // WHY: No ConfigureAwait(false): on WebGL the continuation must capture
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

            // WHY: Drive the idle timeout off a linked CTS so that when the read wins (the hot path, hit once
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

            // WHY: Idle timeout fired: the read is abandoned. Observe it so its eventual fault (e.g.
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

            // WHY: WebGL in the browser: cross-origin requests trigger CORS preflight for non-safelisted headers.
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
            if (response == null)
            {
                yield break;
            }

            // WHY: this path replaces a real stream (WebGL without native SSE, and the stream->non-stream
            // fallback), so the served model id has to be re-emitted on every synthetic update — otherwise
            // those turns are the only ones that cannot report which model answered.
            string servedModel = response.ModelId;

            if (response.Messages != null && response.Messages.Count > 0)
            {
                MEAI.ChatMessage msg = response.Messages[0];

                if (msg.Contents != null && msg.Contents.Count > 0)
                {
                    foreach (MEAI.AIContent c in EnumerableContents(msg))
                    {
                        if (c is MEAI.TextContent tc && !string.IsNullOrEmpty(tc.Text))
                        {
                            yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, tc.Text)
                            {
                                ModelId = servedModel
                            };
                        }
                        else if (c is MEAI.TextReasoningContent rc && !string.IsNullOrEmpty(rc.Text))
                        {
                            MEAI.ChatResponseUpdate ru = new(MEAI.ChatRole.Assistant, "")
                            {
                                ModelId = servedModel
                            };
                            ru.Contents = new List<MEAI.AIContent> { rc };
                            yield return ru;
                        }
                        else if (c is MEAI.FunctionCallContent fc)
                        {
                            MEAI.ChatResponseUpdate u = new(MEAI.ChatRole.Assistant, "")
                            {
                                ModelId = servedModel
                            };
                            u.Contents = new List<MEAI.AIContent> { fc };
                            yield return u;
                        }
                    }
                }
                else if (!string.IsNullOrEmpty(msg.Text))
                {
                    yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, msg.Text)
                    {
                        ModelId = servedModel
                    };
                }
            }

            if (response.Usage != null)
            {
                // WHY: The full response's usage was invisible to streaming consumers on the
                // WebGL non-native-streaming path and the stream->non-stream fallback, so those
                // turns reported 0 tokens. Re-emit it as a trailing UsageContent update, mirroring
                // OpenAI's final stream_options.include_usage chunk.
                MEAI.ChatResponseUpdate usageUpdate = new(MEAI.ChatRole.Assistant, "")
                {
                    ModelId = servedModel
                };
                usageUpdate.Contents = new List<MEAI.AIContent> { new MEAI.UsageContent(response.Usage) };
                yield return usageUpdate;
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
            if (!retryAfter.HasValue)
            {
                // WHY: WebGL fetch cannot see Retry-After unless CORS exposes it; Groq-style bodies
                // carry the window as "Please try again in 14.017s" - surface it on the typed
                // error so upper layers can show a meaningful "retry in Ns" instead of nothing.
                double? bodyWindow = TryParseRetryWindowSecondsFromBody(responseBody);
                if (bodyWindow.HasValue)
                {
                    retryAfter = (int)Math.Ceiling(bodyWindow.Value);
                }
            }

            // WHY: The exception Message is surfaced up the stack and logged (result.Error). Redact it at the
            // source, not just at the log call: a 401 (invalid-credentials) parsed error.message can echo the
            // submitted key/token, so use the already-redacted detail instead of ExtractProviderMessage. Every
            // other status (including 403 forbidden/permission/geo) keeps its parsed provider message, capped
            // so a huge body cannot dump content into logs.
            string providerMessage = status == 401
                ? errorDetail
                : TruncateMessageForException(ExtractProviderMessage(responseBody, errorDetail));

            return new LlmClientException(
                $"HTTP error {status}: {providerMessage}",
                code,
                status > 0 ? status : null,
                retryAfter,
                responseBody);
        }

        private static string TruncateMessageForException(string message)
        {
            if (string.IsNullOrEmpty(message) || message.Length <= MaxErrorBodyLogChars)
            {
                return message ?? "";
            }

            return message.Substring(0, MaxErrorBodyLogChars) +
                   $"... [+{message.Length - MaxErrorBodyLogChars} chars]";
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

            // WHY: 402 says the provider account is out of credit. Replaying it with the same key
            // returns the same 402 forever, so it must NOT reach the transient ProviderError bucket
            // that the retry/fallback decorators replay. Checked after the quota branch on purpose:
            // a 402 whose body already says "quota" keeps the older, equally permanent QuotaExceeded.
            if (status == 402)
            {
                return LlmErrorCode.PaymentRequired;
            }

            if (status == 429 || ContainsRateLimitToken(text))
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

            // WHY: 408 is the one 4xx a replay can actually clear, so it is classified before the
            // permanent-4xx sweep below (and matches IsRetryableHttpStatus, which already retries it).
            if (status == 408)
            {
                return LlmErrorCode.Timeout;
            }

            if (status >= 500 || status == 0)
            {
                return LlmErrorCode.BackendUnavailable;
            }

            // WHY: every remaining 4xx (404 model/route missing, 405, 410, 451, ...) is a permanent
            // refusal. Falling through to ProviderError made the streaming retry and the fallback
            // decorator replay it as if the backend were merely flaky, multiplying the user's wait
            // by the retry budget for an answer that could never change.
            if (status >= 400 && status < 500)
            {
                return LlmErrorCode.PermanentProviderError;
            }

            return LlmErrorCode.ProviderError;
        }

        /// <summary>
        /// Conservative rate-limit detection in (lowercased) error text: a bare "rate" substring also
        /// matched "generate"/"moderate" and misclassified unrelated errors as RateLimited, so only
        /// specific provider phrasings count. HTTP 429 is handled by status in <see cref="MapHttpStatus"/>.
        /// </summary>
        private static bool ContainsRateLimitToken(string lowerText)
        {
            return lowerText.Contains("rate limit") ||
                   lowerText.Contains("rate-limit") ||
                   lowerText.Contains("rate_limit") ||
                   lowerText.Contains("ratelimit") ||
                   lowerText.Contains("too many requests");
        }

        /// <summary>EditMode tests: HTTP status + body/detail text → typed error code classification.</summary>
        internal static LlmErrorCode MapHttpStatusForTests(int status, string body, string fallback)
        {
            return MapHttpStatus(status, body, fallback);
        }

        /// <summary>EditMode tests: the typed HTTP exception built from a provider error response,
        /// so tests can assert the exception message never carries the raw/untruncated body.</summary>
        internal static LlmClientException BuildHttpExceptionForTests(int status, string body)
        {
            return BuildHttpException(status, body, FormatHttpErrorForLog(status, body), null);
        }

        private const int MaxErrorBodyLogChars = 500;

        /// <summary>
        /// Redacts a provider HTTP error body for logging: auth-failure bodies (401/403) can echo the
        /// submitted key/token, so their body is never logged; other bodies are truncated so a large
        /// provider error can't dump prompt echoes or unbounded content into the log. The full body is
        /// still used for retry/classification logic and the typed error, just not written verbatim to logs.
        /// </summary>
        internal static string FormatHttpErrorForLog(int status, string body)
        {
            if (string.IsNullOrEmpty(body))
            {
                return $"HTTP {status}";
            }

            // WHY: Only 401 (invalid-credentials) bodies routinely echo the submitted key/token, so only they
            // are fully blanked. 403 (forbidden / permission / geo-block / model-access) carries useful,
            // non-secret diagnostics, so it is truncated like any other status rather than blanked.
            if (status == 401)
            {
                return $"HTTP {status} | Body: [redacted auth error body]";
            }

            string trimmed = body.Trim();
            if (trimmed.Length > MaxErrorBodyLogChars)
            {
                trimmed = trimmed.Substring(0, MaxErrorBodyLogChars) +
                          $"... [+{trimmed.Length - MaxErrorBodyLogChars} chars]";
            }

            return $"HTTP {status} | Body: {trimmed}";
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

        internal static MEAI.ChatResponse ParseResponse(string json, ILog? log = null)
        {
            ILog logger = log ?? NullLog.Instance;
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
                string reasoning = ExtractAssistantMessageReasoningText(msg);

                JArray toolCalls = msg?["tool_calls"] as JArray;
                bool hasToolCalls = toolCalls != null && toolCalls.Count > 0;

                List<MEAI.AIContent> contents = new();
                // WHY: В инциденте RedoSchool reasoning_content попал в сохраняемую заметку вместо
                // ответа. Оставляем рассуждения только в диагностическом TextReasoningContent.
                if (!string.IsNullOrEmpty(reasoning))
                {
                    contents.Add(new MEAI.TextReasoningContent(reasoning));
                }

                if (!string.IsNullOrEmpty(content))
                {
                    contents.Add(new MEAI.TextContent(content));
                }

                if (hasToolCalls)
                {
                    foreach (JToken tc in toolCalls)
                    {
                        JObject func = tc["function"] as JObject;
                        if (func == null)
                        {
                            logger.Warn(
                                "MeaiOpenAiChatClient: dropped non-streaming tool call without a 'function' object.",
                                LogTag.Llm);
                            continue;
                        }

                        contents.Add(ParseNonStreamingToolCall(tc, func, logger));
                    }
                }

                MEAI.ChatResponse response = new(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, contents));

                // WHY: the provider names the model it ACTUALLY served (a proxy/router may pick a different
                // one than the client asked for, and under ServerManagedApi the client asks for nothing at
                // all). Carrying it out is what lets usage history and cost telemetry report the truth.
                string servedModel = root["model"]?.ToString();
                if (!string.IsNullOrWhiteSpace(servedModel))
                {
                    response.ModelId = servedModel.Trim();
                }

                if (root["usage"] is JObject usage)
                {
                    response.Usage = BuildUsageDetailsFromOpenAiUsageObject(usage);
                }

                return response;
            }
            catch (Exception ex)
            {
                logger.Warn(
                    $"MeaiOpenAiChatClient: failed to parse non-streaming response JSON - returning empty assistant message: {ex.Message}",
                    LogTag.Llm);
                return new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, ""));
            }
        }

        /// <summary>
        /// Parses ONE non-streaming tool call. Malformed <c>arguments</c> JSON degrades only THIS
        /// call: it is surfaced with the shared parse-error markers (same contract as the streaming
        /// accumulator; <see cref="ToolExecutionPolicy"/> turns it into a failed call) instead of
        /// wiping the assistant text and every other tool call in the message.
        /// </summary>
        private static MEAI.FunctionCallContent ParseNonStreamingToolCall(JToken tc, JObject func, ILog log)
        {
            string callId = tc["id"]?.ToString() ?? "";
            string name = func["name"]?.ToString() ?? "";
            string argsJson = func["arguments"]?.ToString() ?? "{}";

            Dictionary<string, object?> args;
            try
            {
                args = JsonConvert.DeserializeObject<Dictionary<string, object?>>(argsJson)
                       ?? new Dictionary<string, object?>();
            }
            catch (JsonException ex)
            {
                log.Warn(
                    $"MeaiOpenAiChatClient: non-streaming tool call '{name}' (id='{callId}') had malformed arguments JSON - " +
                    $"surfacing raw string via parse-error markers instead of dropping the whole message: {ex.Message}",
                    LogTag.Llm);
                args = new Dictionary<string, object?>
                {
                    { ToolCallArgumentMarkers.RawArgumentsKey, argsJson },
                    { ToolCallArgumentMarkers.ParseErrorKey, true }
                };
            }

            return new MEAI.FunctionCallContent(callId, name, args);
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

        /// <summary>
        /// Reads the provider reasoning field from an OpenAI-compatible delta/message object.
        /// Covers DeepSeek/Qwen <c>reasoning_content</c> plus the <c>reasoning</c> and
        /// <c>reasoningContent</c> spellings used by other OpenAI-compatible servers.
        /// </summary>
        private static string ExtractReasoningFieldText(JObject obj)
        {
            if (obj == null)
            {
                return "";
            }

            JToken token = obj["reasoning_content"] ?? obj["reasoning"] ?? obj["reasoningContent"];
            if (token == null || token.Type == JTokenType.Null)
            {
                return "";
            }

            return ExtractMessageContentString(token);
        }

        /// <summary>
        /// Extracts the inner text of every inline <c>&lt;think&gt;...&lt;/think&gt;</c> block so
        /// hidden reasoning stripped from the visible answer can still surface as
        /// <see cref="MEAI.TextReasoningContent"/> instead of being silently discarded.
        /// </summary>
        private static string ExtractInlineThinkText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "";
            }

            System.Text.RegularExpressions.MatchCollection matches =
                System.Text.RegularExpressions.Regex.Matches(text,
                    @"<think>([\s\S]*?)</think>",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (matches.Count == 0)
            {
                return "";
            }

            StringBuilder sb = new();
            foreach (System.Text.RegularExpressions.Match m in matches)
            {
                string inner = m.Groups[1].Value.Trim();
                if (inner.Length == 0)
                {
                    continue;
                }

                if (sb.Length > 0)
                {
                    sb.Append('\n');
                }

                sb.Append(inner);
            }

            return sb.ToString();
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

        /// <summary>
        /// Collects the hidden reasoning of one non-streaming assistant message: the provider
        /// reasoning field (<c>reasoning_content</c>/<c>reasoning</c>/<c>reasoningContent</c>) plus
        /// any inline <c>&lt;think&gt;</c> blocks that <see cref="StripRedactedThinkingBlock"/>
        /// removes from the visible text. Both sources surface as
        /// <see cref="MEAI.TextReasoningContent"/> so the UI can show a collapsible thinking section.
        /// </summary>
        private static string ExtractAssistantMessageReasoningText(JToken msg)
        {
            if (msg is not JObject m)
            {
                return "";
            }

            string fieldReasoning = ExtractReasoningFieldText(m);
            string inlineThink = ExtractInlineThinkText(ExtractMessageContentString(m["content"]));
            if (string.IsNullOrEmpty(fieldReasoning))
            {
                return inlineThink;
            }

            return string.IsNullOrEmpty(inlineThink)
                ? fieldReasoning
                : fieldReasoning + "\n" + inlineThink;
        }

        private static IEnumerable<MEAI.ChatResponseUpdate> ParseSseUpdates(string raw,
            SseToolCallAccumulator accumulator)
        {
            string[] lines = raw.Split('\n');
            foreach (string line in lines)
            {
                string trimmed = line.Trim();
                // WHY: OpenAI uses "data: {...}"; some local servers (LM Studio, llama.cpp) omit the space after "data:".
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

        /// <summary>
        /// Parses one SSE data object and stamps the served model id onto the resulting update. OpenAI-
        /// compatible providers repeat <c>model</c> in every streamed chunk; carrying it out is how the
        /// consumer learns which model actually answered instead of echoing the client's configuration.
        /// </summary>
        private static MEAI.ChatResponseUpdate ExtractDeltaUpdate(string json, SseToolCallAccumulator accumulator)
        {
            JObject root;
            try
            {
                root = JObject.Parse(json);
            }
            catch
            {
                return null;
            }

            MEAI.ChatResponseUpdate update = ExtractDeltaUpdateCore(root, accumulator);
            if (update == null)
            {
                return null;
            }

            string servedModel = root["model"]?.ToString();
            if (string.IsNullOrEmpty(update.ModelId) && !string.IsNullOrWhiteSpace(servedModel))
            {
                update.ModelId = servedModel.Trim();
            }

            return update;
        }

        private static MEAI.ChatResponseUpdate ExtractDeltaUpdateCore(JObject obj, SseToolCallAccumulator accumulator)
        {
            try
            {
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
                    string reasoningDelta = ExtractReasoningFieldText(deltaObj);

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
                        MEAI.ChatResponseUpdate contentUpdate = new(MEAI.ChatRole.Assistant, deltaContent);
                        if (!string.IsNullOrEmpty(reasoningDelta))
                        {
                            contentUpdate.Contents.Add(new MEAI.TextReasoningContent(reasoningDelta));
                        }

                        return contentUpdate;
                    }

                    if (!string.IsNullOrEmpty(reasoningDelta))
                    {
                        // WHY: DeepSeek/Qwen reasoning models stream their thinking as
                        // delta.reasoning_content with content=null. Surfacing it as
                        // TextReasoningContent (never TextContent) keeps the visible answer clean
                        // while still counting as a REAL parsed delta, so the empty-stream
                        // retry/fallback does not fire on a turn that is actively thinking.
                        MEAI.ChatResponseUpdate reasoningUpdate = new(MEAI.ChatRole.Assistant, "");
                        reasoningUpdate.Contents = new List<MEAI.AIContent>
                        {
                            new MEAI.TextReasoningContent(reasoningDelta)
                        };
                        return reasoningUpdate;
                    }
                }

                // WHY: Local servers (LM Studio / llama.cpp) sometimes stream only `message` or `text` per chunk.
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
            if (prompt == 0 && completion == 0 && total == 0 &&
                (additionalCounts == null || additionalCounts.Count == 0))
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

        /// <summary>EditMode tests: simulated streaming updates built from a full response
        /// (WebGL non-native-streaming and the stream->non-stream fallback path).</summary>
        internal static List<MEAI.ChatResponseUpdate> FullResponseToSimulatedStreamingUpdatesForTests(
            MEAI.ChatResponse response)
        {
            return FullResponseToSimulatedStreamingUpdates(response).ToList();
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
        internal static bool IsCompleteJsonObjectForTests(string s)
        {
            return SseToolCallAccumulator.IsCompleteJsonObject(s);
        }

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

            // WHY: Tombstones for calls already surfaced by DrainCompleted(). One accumulator instance
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
                        // WHY: Attribute the fragment when it is NOT genuinely ambiguous: only a call
                        // whose accumulated argument JSON is still open can own more argument text,
                        // so a single open call among the pending ones is an unambiguous target.
                        PendingToolCall soleOpen = FindSoleOpenPendingCall();
                        if (soleOpen != null)
                        {
                            return soleOpen;
                        }

                        MarkAmbiguousMissingIndex(name, argumentsFragment);
                        return null;
                    }
                }

                return CreateEntry(index, stableId);
            }

            /// <summary>
            /// True while the entry can still accept argument fragments: its accumulated arguments do
            /// not yet form one complete JSON object. A completed entry gaining anything further would
            /// only accumulate trailing junk, so it can never own a new fragment.
            /// </summary>
            private static bool IsOpenForMoreArguments(PendingToolCall pending)
            {
                string argsStr = pending.Arguments.ToString();
                return argsStr.Length == 0 || !IsCompleteJsonObject(argsStr);
            }

            /// <summary>
            /// Returns the single pending call whose arguments are still open, or null when zero or
            /// several are open (attribution of an id/index-less fragment truly ambiguous).
            /// </summary>
            private PendingToolCall FindSoleOpenPendingCall()
            {
                PendingToolCall sole = null;
                foreach (PendingToolCall pending in _pending)
                {
                    if (!IsOpenForMoreArguments(pending))
                    {
                        continue;
                    }

                    if (sole != null)
                    {
                        return null;
                    }

                    sole = pending;
                }

                return sole;
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
                    // WHY: A new call (fresh id) reusing a drained index takes the index over: id-less
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
                // WHY: Poison only calls that could plausibly own the lost fragment (arguments still
                // open). Calls whose argument JSON is already complete cannot have been corrupted by
                // it; failing them too used to break ALL parallel tool calls on providers that
                // occasionally emit an index-less delta.
                int poisoned = 0;
                foreach (PendingToolCall pending in _pending)
                {
                    if (!IsOpenForMoreArguments(pending))
                    {
                        continue;
                    }

                    pending.ForceParseError = true;
                    poisoned++;
                }

                _log.Warn(
                    "MeaiOpenAiChatClient: streamed tool-call fragment had no id/index while multiple calls were pending; " +
                    $"dropping ambiguous fragment and marking {poisoned} still-open call(s) as parse errors " +
                    $"(name='{name ?? ""}', args length={argumentsFragment?.Length ?? 0}).",
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

                // WHY: Emit in ascending tool-call index order so the FunctionCallContent order is
                // deterministic and matches the provider's tool_calls index order.
                foreach (PendingToolCall pending in _pending
                             .OrderBy(p => p.Index ?? int.MaxValue)
                             .ThenBy(p => p.Sequence))
                {
                    string argsStr = pending.Arguments.ToString();

                    if (string.IsNullOrEmpty(pending.Name))
                    {
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
        {
            return BuildMessagesPayloadStatic(msgs);
        }

        private List<Dictionary<string, object>> BuildMessagesPayload(List<MEAI.ChatMessage> msgs)
        {
            return BuildMessagesPayloadStatic(msgs);
        }

        private static List<Dictionary<string, object>> BuildMessagesPayloadStatic(List<MEAI.ChatMessage> msgs)
        {
            List<Dictionary<string, object>> messages = new();

            // WHY: tool_call_id symmetry for calls that arrived WITHOUT a provider id: the assistant echo
            // derives a deterministic synthetic id, and the matching tool-role reply must use the
            // SAME id or the model sees an unanswered call (and a dangling reply). Ids are queued in
            // emission order; the next tool result with an empty CallId consumes the next queued id
            // (results always follow their assistant tool_calls message in protocol order).
            Queue<string> pendingSyntheticToolCallIds = new();
            int assistantToolCallMessageIndex = 0;

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
                    // WHY: One MEAI Tool message can carry SEVERAL FunctionResultContent items (the tool
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
                            string toolCallId = functionResult.CallId;
                            if (string.IsNullOrEmpty(toolCallId) && pendingSyntheticToolCallIds.Count > 0)
                            {
                                // WHY: Pair with the synthetic id the assistant echo emitted for the
                                // id-less call this result answers (see queue comment above).
                                toolCallId = pendingSyntheticToolCallIds.Dequeue();
                            }

                            if (!string.IsNullOrEmpty(toolCallId))
                            {
                                resultDict["tool_call_id"] = toolCallId;
                            }

                            string resultStr = functionResult.Result switch
                            {
                                string s => s,
                                // WHY: Newtonsoft serializes System.Text.Json's JsonElement struct by
                                // reflection into a useless {"ValueKind":N}, which would blind the
                                // model to its own tool results - emit the element's real JSON.
                                System.Text.Json.JsonElement je =>
                                    je.ValueKind == System.Text.Json.JsonValueKind.String
                                        ? je.GetString() ?? ""
                                        : je.GetRawText(),
                                null => "",
                                _ => JsonConvert.SerializeObject(functionResult.Result)
                            };
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
                        for (int callIndex = 0; callIndex < funcCalls.Count; callIndex++)
                        {
                            MEAI.FunctionCallContent call = funcCalls[callIndex];

                            // WHY: Never invent a RANDOM id: a Guid here could not be reproduced on the
                            // matching tool-role reply, leaving the model with an unanswered call it
                            // then re-issues. Derive a deterministic synthetic id instead and queue
                            // it so the paired tool result (also id-less) reuses the exact same id.
                            string callId = call.CallId;
                            if (string.IsNullOrEmpty(callId))
                            {
                                callId = $"synth_{assistantToolCallMessageIndex}_{callIndex}";
                                pendingSyntheticToolCallIds.Enqueue(callId);
                            }

                            toolCallsList.Add(new Dictionary<string, object>
                            {
                                { "id", callId },
                                { "type", "function" },
                                {
                                    "function", new Dictionary<string, object>
                                    {
                                        { "name", call.Name },
                                        { "arguments", SerializeToolCallArguments(call.Arguments) }
                                    }
                                }
                            });
                        }

                        assistantToolCallMessageIndex++;
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
        /// Serializes a tool call's arguments for the assistant <c>tool_calls</c> echo. Parse-error
        /// calls (arguments carrying <see cref="ToolCallArgumentMarkers.RawArgumentsKey"/>) echo the
        /// model's ORIGINAL raw argument string instead of the internal marker dictionary - the
        /// markers are a CoreAI-private contract and leaking
        /// <c>{"__raw_arguments":...,"__parse_error":true}</c> onto the wire would show the model a
        /// call shape it never produced.
        /// </summary>
        private static string SerializeToolCallArguments(IDictionary<string, object?> arguments)
        {
            if (arguments != null &&
                arguments.TryGetValue(ToolCallArgumentMarkers.RawArgumentsKey, out object? rawArguments))
            {
                return rawArguments switch
                {
                    string s => s,
                    System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.String =>
                        je.GetString() ?? "",
                    null => "",
                    _ => rawArguments.ToString() ?? ""
                };
            }

            return JsonConvert.SerializeObject(arguments ?? new Dictionary<string, object?>());
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
