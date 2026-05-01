#if !COREAI_NO_LLM
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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
    /// MEAI <see cref="MEAI.IChatClient"/> для OpenAI-compatible HTTP API.
    /// Портативная реализация на <see cref="HttpClient"/> — без UnityEngine / UnityWebRequest
    /// (корректнее для WebGL-браузерного стека и единого поведения таймаутов).
    /// Continuation после await без <c>ConfigureAwait(false)</c>, чтобы на хостах с главным потоком Unity
    /// (в т.ч. WebGL) сохранялась привязка к synchronization context, когда он задан.
    /// </summary>
    public sealed class MeaiOpenAiChatClient : MEAI.IChatClient, IDisposable
    {
        private readonly IOpenAiHttpSettings _settings;
        private readonly ILog _log;

        public MeaiOpenAiChatClient(IOpenAiHttpSettings settings, ILog? log = null)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _log = log ?? Log.Instance;
        }

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
                _log.Info($"MeaiOpenAiChatClient: Timeout={transportTimeoutSec}s (HttpClient)", LogTag.Llm);

                using HttpClient client = CreateBoundedHttpClient(transportTimeoutSec);

                using HttpRequestMessage httpRequest = new(HttpMethod.Post, url);
                httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");
                AddCommonHeaders(httpRequest, url, acceptEventStream: false);

                try
                {
                    using (HttpResponseMessage response = await client.SendAsync(httpRequest,
                               HttpCompletionOption.ResponseContentRead, cancellationToken))
                {
                    HttpStatusCode statusCode = response.StatusCode;
                    bool success = response.IsSuccessStatusCode;
                    cancellationToken.ThrowIfCancellationRequested();
                    // HttpContent on .NET Standard 2.0 / some Unity profiles has no ReadAsStringAsync(CancellationToken).
                    string bodyText = await response.Content.ReadAsStringAsync();

                    if (success)
                    {
                        responseJson = bodyText;
                        break;
                    }

                    string errorDetail = !string.IsNullOrEmpty(bodyText)
                        ? $"{response.ReasonPhrase} | Body: {bodyText}"
                        : (response.ReasonPhrase ?? "HTTP error");
                    _log.Warn($"MeaiOpenAiChatClient: {errorDetail}", LogTag.Llm);

                    bool canRetryTransient = attempt < transientLocalLlmReloadMaxAttempts
                        && IsTransientLocalLlmReloadError((int)statusCode, bodyText, errorDetail);

                    if (canRetryTransient)
                    {
                        _log.Info(
                            $"MeaiOpenAiChatClient: transient local LLM / reload response (attempt {attempt}/{transientLocalLlmReloadMaxAttempts}); retrying after backoff...",
                            LogTag.Llm);
                        await BackoffDelayAsync(Math.Min(6000, 900 * attempt), cancellationToken);
                        continue;
                    }

                    throw BuildHttpException(statusCode, bodyText, errorDetail, response.Headers);
                }
                }
                catch (OperationCanceledException ex)
                {
                    // HttpClient uses TaskCanceledException for per-request timeouts; caller token may still be inactive.
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
                    _log.Warn($"MeaiOpenAiChatClient: SendAsync failed: {ex.Message}", LogTag.Llm);
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
            List<MEAI.ChatMessage> msgs = chatMessages.ToList();
            string url = _settings.ApiBaseUrl.TrimEnd('/') + "/chat/completions";

            List<Dictionary<string, object>> messages = BuildMessagesPayload(msgs);
            List<Dictionary<string, object>> toolsList = BuildToolsPayload(options);

            Dictionary<string, object> reqBody = new()
            {
                { "model", _settings.Model },
                { "temperature", options?.Temperature ?? _settings.Temperature },
                { "messages", messages },
                { "stream", true }
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

                using HttpClient client = CreateStreamingHttpClient(streamTransportTimeoutSec);

                using HttpRequestMessage httpRequest = new(HttpMethod.Post, url);
                httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");
                AddCommonHeaders(httpRequest, url, acceptEventStream: true);

                _log.Info(
                    $"MeaiOpenAiChatClient: POST (stream) {url} (attempt {attempt}/{transientLocalLlmReloadMaxAttempts})",
                    LogTag.Llm);

                HttpResponseMessage response;
                try
                {
                    response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken);
                }
                catch (OperationCanceledException ex)
                {
                    // HttpClient uses TaskCanceledException for per-request timeouts; caller token may still be inactive.
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
                    _log.Warn($"MeaiOpenAiChatClient: stream SendAsync failed: {ex.Message}", LogTag.Llm);
                    throw new LlmClientException($"HTTP stream send failed: {ex.Message}", LlmErrorCode.BackendUnavailable);
                }

                using (response)
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string streamBody = await response.Content.ReadAsStringAsync();
                        string streamErr = !string.IsNullOrEmpty(streamBody)
                            ? $"{response.ReasonPhrase} | Body: {streamBody}"
                            : (response.ReasonPhrase ?? "HTTP error");
                        _log.Warn($"MeaiOpenAiChatClient: stream error — {streamErr}", LogTag.Llm);

                        bool canRetryTransient = attempt < transientLocalLlmReloadMaxAttempts
                            && IsTransientLocalLlmReloadError((int)response.StatusCode, streamBody, streamErr);

                        if (canRetryTransient)
                        {
                            _log.Info("MeaiOpenAiChatClient: transient local LLM on stream-open; retrying after backoff...",
                                LogTag.Llm);
                            await BackoffDelayAsync(Math.Min(6000, 900 * attempt), cancellationToken);
                            continue;
                        }

                        throw BuildHttpException(response.StatusCode, streamBody, streamErr, response.Headers);
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    using (Stream stream = await response.Content.ReadAsStreamAsync())
                    using (StreamReader reader = new(stream, Encoding.UTF8, false, 8192))
                    {
                        SseToolCallAccumulator toolAccumulator = new();
                        DateTime lastProgressUtc = DateTime.UtcNow;

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
                                yield return update;
                            }
                        }

                        MEAI.ChatResponseUpdate flushed = toolAccumulator.Flush();
                        if (flushed != null)
                        {
                            yield return flushed;
                        }
                    }

                    yield break;
                }
            }
        }

        private static HttpClient CreateBoundedHttpClient(int transportTimeoutSec)
        {
#if UNITY_EDITOR
            if (MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory != null)
            {
                return MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory();
            }
#endif
            int sec = transportTimeoutSec <= 0 ? 120 : transportTimeoutSec;
            return new HttpClient { Timeout = TimeSpan.FromSeconds(sec) };
        }

        /// <summary>
        /// Длинные SSE: не режем весь запрос <see cref="HttpClient.Timeout"/> — только межбайтовый простой
        /// (см. цикл чтения + <see cref="Task.Delay"/>), иначе длинная генерация оборвётся.
        /// </summary>
        private static HttpClient CreateStreamingHttpClient(int stallBudgetSeconds)
        {
#if UNITY_EDITOR
            if (MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory != null)
            {
                return MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory();
            }
#endif
            _ = stallBudgetSeconds;
            return new HttpClient { Timeout = TimeSpan.FromHours(24) };
        }

        private void AddCommonHeaders(HttpRequestMessage request, string url, bool acceptEventStream)
        {
            if (acceptEventStream)
            {
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            }

            if (url.Contains("openrouter", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.TryAddWithoutValidation(OpenAiHttpConstants.HttpRefererHeaderName,
                    OpenAiHttpConstants.HttpRefererUnityUrl);
                request.Headers.TryAddWithoutValidation("X-Title", "CoreAI");
                _log.Info("MeaiOpenAiChatClient: Added OpenRouter headers", LogTag.Llm);
            }

            string authorizationHeader = ResolveAuthorizationHeader();
            if (!string.IsNullOrEmpty(authorizationHeader))
            {
                request.Headers.TryAddWithoutValidation("Authorization", authorizationHeader);
                _log.Info($"MeaiOpenAiChatClient: Authorization header set (len={authorizationHeader.Length})",
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
            HttpStatusCode statusCode,
            string responseBody,
            string errorDetail,
            HttpHeaders responseHeaders)
        {
            int status = (int)statusCode;
            LlmErrorCode code = MapHttpStatus(status, responseBody, errorDetail);
            int? retryAfter = TryParseRetryAfterHeaders(responseHeaders);

            return new LlmClientException(
                $"HTTP error {status}: {ExtractProviderMessage(responseBody, errorDetail)}",
                code,
                status > 0 ? status : null,
                retryAfter,
                responseBody);
        }

        private static int? TryParseRetryAfterHeaders(HttpHeaders headers)
        {
            if (headers == null)
            {
                return null;
            }

            if (headers.TryGetValues("Retry-After-Ms", out IEnumerable<string> msVals))
            {
                string retryMsHeader = msVals.FirstOrDefault();
                if (!string.IsNullOrEmpty(retryMsHeader) &&
                    float.TryParse(retryMsHeader, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float retryMs))
                {
                    return (int)Math.Ceiling(retryMs / 1000f);
                }
            }

            if (headers.TryGetValues("Retry-After", out IEnumerable<string> sVals))
            {
                string retryHeader = sVals.FirstOrDefault();
                if (int.TryParse(retryHeader, out int parsedRetry))
                {
                    return parsedRetry;
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
                if (!trimmed.StartsWith("data: ", StringComparison.Ordinal)) continue;
                string data = trimmed.Substring(6);
                if (data == "[DONE]") yield break;

                MEAI.ChatResponseUpdate update = ExtractDeltaUpdate(data, accumulator);
                if (update != null)
                {
                    yield return update;
                }
            }
        }

        private static MEAI.ChatResponseUpdate ExtractDeltaUpdate(string json, SseToolCallAccumulator accumulator)
        {
            try
            {
                JObject obj = JObject.Parse(json);
                JToken delta = obj?["choices"]?[0]?["delta"];
                if (delta == null) return null;

                _ = delta["reasoning_content"]?.ToString();

                string content = delta["content"]?.ToString();
                JArray toolCallsArray = delta["tool_calls"] as JArray;

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

                if (!string.IsNullOrEmpty(content))
                {
                    return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, content);
                }

                return null;
            }
            catch
            {
                return null;
            }
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
