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
                { "temperature", options?.Temperature ?? _settings.Temperature },
                { "messages", messages }
            };
            if (options?.MaxOutputTokens.HasValue == true)
            {
                reqBody["max_tokens"] = options.MaxOutputTokens.Value;
            }

            if (toolsList.Count > 0)
            {
                reqBody["tools"] = toolsList;
            }

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
                _log.Info($"MeaiOpenAiChatClient: Timeout={transportTimeoutSec}s ({_transport.DebugLabel})", LogTag.Llm);

                try
                {
                    OpenAiHttpPostResult postResult = await _transport.PostNonStreamingAsync(
                        new OpenAiHttpPostRequest
                        {
                            Url = url,
                            JsonBody = json,
                            AcceptEventStream = false,
                            TransportTimeoutSeconds = transportTimeoutSec,
                            Headers = BuildTransportHeaders(url, acceptEventStream: false)
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
                        && IsTransientLocalLlmReloadError(postResult.StatusCode, postResult.BodyText, errorDetail);

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
                throw new InvalidOperationException("MeaiOpenAiChatClient: request completed without success or typed error.");
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
                    "MeaiOpenAiChatClient: transport has no SSE support — using non-stream completion and simulated streaming updates (WebGL / UnityWebRequest).",
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
                { "temperature", options?.Temperature ?? _settings.Temperature },
                { "messages", messages },
                { "stream", true },
                { "stream_options", new Dictionary<string, object> { { "include_usage", true } } }
            };
            if (options?.MaxOutputTokens.HasValue == true)
            {
                reqBody["max_tokens"] = options.MaxOutputTokens.Value;
            }

            if (toolsList.Count > 0)
            {
                reqBody["tools"] = toolsList;
            }

            string json = JsonConvert.SerializeObject(reqBody);

            const int transientLocalLlmReloadMaxAttempts = 10;

            for (int attempt = 1; attempt <= transientLocalLlmReloadMaxAttempts; attempt++)
            {
                int streamTransportTimeoutSec = _settings.RequestTimeoutSeconds <= 0 ? 120 : _settings.RequestTimeoutSeconds;

                OpenAiHttpPostRequest transportReq = new()
                {
                    Url = url,
                    JsonBody = json,
                    AcceptEventStream = true,
                    TransportTimeoutSeconds = streamTransportTimeoutSec,
                    Headers = BuildTransportHeaders(url, acceptEventStream: true)
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
                    throw new LlmClientException($"HTTP stream send failed: {ex.Message}", LlmErrorCode.BackendUnavailable);
                }

                using (openResult)
                {
                    string ctype = TryGetHeaderFirstValue(openResult.ResponseHeaders, "Content-Type") ?? "n/a";
                    LogStreamingHttpResponseSummary(openResult.StatusCode, openResult.StatusCode >= 200 && openResult.StatusCode < 300, "", ctype);

                    if (openResult.StatusCode < 200 || openResult.StatusCode >= 300)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string streamBody = openResult.ErrorBodyText ?? "";
                        string streamErr = !string.IsNullOrEmpty(streamBody)
                            ? $"HTTP {openResult.StatusCode} | Body: {streamBody}"
                            : $"HTTP {openResult.StatusCode}";
                        _log.Warn($"MeaiOpenAiChatClient: stream error — {streamErr}", LogTag.Llm);

                        bool canRetryTransient = attempt < transientLocalLlmReloadMaxAttempts
                            && IsTransientLocalLlmReloadError(openResult.StatusCode, streamBody, streamErr);

                        if (canRetryTransient)
                        {
                            _log.Info("MeaiOpenAiChatClient: transient local LLM on stream-open; retrying after backoff...",
                                LogTag.Llm);
                            await BackoffDelayAsync(Math.Min(6000, 900 * attempt), cancellationToken);
                            continue;
                        }

                        throw BuildHttpException(openResult.StatusCode, streamBody, streamErr, openResult.ResponseHeaders);
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    Stream? stream = openResult.ResponseStream;
                    if (stream == null)
                    {
                        throw new LlmClientException("HTTP stream: success status but no response stream.",
                            LlmErrorCode.BackendUnavailable);
                    }

                    using (StreamReader reader = new(stream, Encoding.UTF8, false, 8192, leaveOpen: true))
                    {
                        SseToolCallAccumulator toolAccumulator = new();
                        DateTime lastProgressUtc = DateTime.UtcNow;
                        int parsedSseDeltas = 0;

                        while (true)
                        {
                            if ((DateTime.UtcNow - lastProgressUtc).TotalSeconds > streamTransportTimeoutSec)
                            {
                                _log.Warn(
                                    $"MeaiOpenAiChatClient: SSE stall timeout after {streamTransportTimeoutSec}s without new lines; aborting.",
                                    LogTag.Llm);
                                throw new LlmClientException(
                                    $"LLM SSE stalled — no data for {streamTransportTimeoutSec}s.",
                                    LlmErrorCode.Timeout);
                            }

                            Task<string> lineTask = reader.ReadLineAsync();
                            Task delayTask = Task.Delay(TimeSpan.FromSeconds(streamTransportTimeoutSec), cancellationToken);
                            Task finished = await Task.WhenAny(lineTask, delayTask);

                            if (finished != lineTask)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                _log.Warn(
                                    $"MeaiOpenAiChatClient: SSE read wait exceeded {streamTransportTimeoutSec}s; aborting.",
                                    LogTag.Llm);
                                throw new LlmClientException(
                                    $"LLM SSE read timed out after {streamTransportTimeoutSec}s.",
                                    LlmErrorCode.Timeout);
                            }

                            string line = await lineTask;
                            if (line == null)
                            {
                                break;
                            }

                            if (!string.IsNullOrWhiteSpace(line))
                            {
                                lastProgressUtc = DateTime.UtcNow;
                            }

                            foreach (MEAI.ChatResponseUpdate update in ParseSseUpdates(line + "\n", toolAccumulator))
                            {
                                parsedSseDeltas++;
                                yield return update;
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
                            _log.Warn(
                                "MeaiOpenAiChatClient: stream ended with HTTP success but 0 parsed SSE deltas — empty body, " +
                                "non-event-stream payload, or chunk shape not matching OpenAI choices[0].delta (check LM Studio / proxy). " +
                                "If Content-Type is application/json, try non-streaming mode or EnableHttpDebugLogging for raw body.",
                                LogTag.Llm);
                        }
                    }

                    yield break;
                }
            }
        }

        private List<KeyValuePair<string, string>> BuildTransportHeaders(string url, bool acceptEventStream)
        {
            _ = acceptEventStream;
            List<KeyValuePair<string, string>> list = new();

            if (url.Contains("openrouter", StringComparison.OrdinalIgnoreCase))
            {
                list.Add(new KeyValuePair<string, string>(OpenAiHttpConstants.HttpRefererHeaderName,
                    OpenAiHttpConstants.HttpRefererUnityUrl));
                list.Add(new KeyValuePair<string, string>("X-Title", "CoreAI"));
                _log.Info("MeaiOpenAiChatClient: Added OpenRouter headers", LogTag.Llm);
            }

            string authorizationHeader = ResolveAuthorizationHeader();
            if (!string.IsNullOrEmpty(authorizationHeader))
            {
                list.Add(new KeyValuePair<string, string>("Authorization", authorizationHeader));
                _log.Info($"MeaiOpenAiChatClient: Authorization header set (len={authorizationHeader.Length})",
                    LogTag.Llm);
            }

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

            IRequestHeaderProvider hp = _settings.HeaderProvider;
            if (hp != null)
            {
                IReadOnlyList<KeyValuePair<string, string>> extra = hp.GetHeaders();
                if (extra != null)
                {
                    foreach (KeyValuePair<string, string> kv in extra)
                    {
                        if (!string.IsNullOrEmpty(kv.Key))
                        {
                            list.Add(kv);
                        }
                    }
                }

                if (!string.IsNullOrEmpty(hp.IdempotencyKey) && !ContainsHeader(list, "Idempotency-Key"))
                {
                    list.Add(new KeyValuePair<string, string>("Idempotency-Key", hp.IdempotencyKey));
                }

                if (!string.IsNullOrEmpty(hp.RequestId) && !ContainsHeader(list, "X-Request-Id"))
                {
                    list.Add(new KeyValuePair<string, string>("X-Request-Id", hp.RequestId));
                }
            }

            return list;
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
            if (headers == null) return null;
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
            if (msg.Contents == null) yield break;
            foreach (MEAI.AIContent c in msg.Contents)
            {
                yield return c;
            }
        }

        private static IEnumerable<MEAI.ChatResponseUpdate> FullResponseToSimulatedStreamingUpdates(
            MEAI.ChatResponse response)
        {
            if (response?.Messages == null || response.Messages.Count == 0) yield break;
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
            if (string.IsNullOrEmpty(text)) return text ?? "";
            return System.Text.RegularExpressions.Regex.Replace(text,
                @"<think>[\s\S]*?</think>\s*", "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
        }

        private static string ExtractMessageContentString(JToken contentToken)
        {
            if (contentToken == null || contentToken.Type == JTokenType.Null)
                return "";

            if (contentToken.Type == JTokenType.String)
                return contentToken.Value<string>() ?? "";

            if (contentToken.Type == JTokenType.Array)
            {
                StringBuilder sb = new();
                foreach (JToken part in contentToken)
                {
                    if (part.Type == JTokenType.String)
                    {
                        if (sb.Length > 0) sb.Append('\n');
                        sb.Append(part.ToString());
                    }
                    else if (part is JObject o)
                    {
                        string t = o["text"]?.ToString();
                        if (!string.IsNullOrEmpty(t))
                        {
                            if (sb.Length > 0) sb.Append('\n');
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
                return "";

            if (msg.Type == JTokenType.String)
                return StripRedactedThinkingBlock(msg.Value<string>() ?? "");

            if (msg.Type != JTokenType.Object)
                return "";

            JObject m = (JObject)msg;

            string content = StripRedactedThinkingBlock(ExtractMessageContentString(m["content"]));
            if (!string.IsNullOrWhiteSpace(content))
                return content;

            foreach (string key in new[] { "reasoning_content", "reasoningContent", "reasoning" })
            {
                JToken t = SelectPropertyCaseInsensitive(m, key);
                if (t == null || t.Type == JTokenType.Null) continue;
                if (t.Type != JTokenType.String) continue;
                string reasoning = t.Value<string>() ?? "";
                reasoning = StripRedactedThinkingBlock(reasoning);
                if (!string.IsNullOrWhiteSpace(reasoning))
                    return reasoning;
            }

            return "";
        }

        private static JToken SelectPropertyCaseInsensitive(JObject obj, string name)
        {
            foreach (JProperty p in obj.Properties())
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                    return p.Value;
            }

            return null;
        }

        private static IEnumerable<MEAI.ChatResponseUpdate> ParseSseUpdates(string raw, SseToolCallAccumulator accumulator)
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

        /// <summary>EditMode tests: full SSE line(s) including the <c>data:</c> prefix.</summary>
        internal static IEnumerable<MEAI.ChatResponseUpdate> ParseSseUpdatesForTests(string raw)
        {
            return ParseSseUpdates(raw, new SseToolCallAccumulator());
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

        internal static MEAI.ChatResponseUpdate ParseSseDataLineForTests(string dataJson) =>
            ExtractDeltaUpdate(dataJson, new SseToolCallAccumulator());

        private sealed class SseToolCallAccumulator
        {
            private readonly Dictionary<int, (string id, string name, StringBuilder args)> _pending = new();

            public void Feed(int index, string callId, string name, string argumentsFragment)
            {
                if (!_pending.TryGetValue(index, out var entry))
                {
                    entry = (callId, name, new StringBuilder());
                    _pending[index] = entry;
                }
                else
                {
                    if (!string.IsNullOrEmpty(callId)) entry.id = callId;
                    if (!string.IsNullOrEmpty(name)) entry.name = name;
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
                if (_pending.Count == 0) return null;

                MEAI.ChatResponseUpdate update = new(MEAI.ChatRole.Assistant, "");
                update.Contents = new List<MEAI.AIContent>();

                foreach (var kvp in _pending)
                {
                    var (id, name, argsBuilder) = kvp.Value;
                    if (string.IsNullOrEmpty(name)) continue;

                    Dictionary<string, object> args = null;
                    string argsStr = argsBuilder.ToString();
                    if (!string.IsNullOrEmpty(argsStr))
                    {
                        try
                        {
                            args = JsonConvert.DeserializeObject<Dictionary<string, object>>(argsStr);
                        }
                        catch { }
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
