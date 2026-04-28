#if !COREAI_NO_LLM
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;
using MEAI = Microsoft.Extensions.AI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// MEAI IChatClient для HTTP API (OpenAI-compatible).
    /// </summary>
    public sealed class MeaiOpenAiChatClient : MEAI.IChatClient
    {
        private readonly IOpenAiHttpSettings _settings;
        private readonly IGameLogger _logger;

        public MeaiOpenAiChatClient(IOpenAiHttpSettings settings, IGameLogger logger)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<MEAI.ChatResponse> GetResponseAsync(
            IEnumerable<MEAI.ChatMessage> chatMessages,
            MEAI.ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            List<MEAI.ChatMessage> msgs = chatMessages.ToList();
            string url = _settings.ApiBaseUrl.TrimEnd('/') + "/chat/completions";

            // Debug: логируем URL для диагностики
            _logger.LogInfo(GameLogFeature.Llm, $"MeaiOpenAiChatClient: POST {url}");

            // Логируем входящие промпты если включено
            if (_settings.LogLlmInput)
            {
                _logger.LogInfo(GameLogFeature.Llm, $"MeaiOpenAiChatClient: === LLM Input ===");
                foreach (MEAI.ChatMessage msg in msgs)
                {
                    // Для tool messages извлекаем content правильно
                    string content = msg.Text ?? "";
                    if (string.IsNullOrEmpty(content) && msg.Contents != null && msg.Contents.Count > 0)
                    {
                        MEAI.TextContent textContent = msg.Contents.OfType<MEAI.TextContent>().FirstOrDefault();
                        content = textContent?.Text ?? string.Join(", ", msg.Contents.Select(c => c.GetType().Name));
                    }

                    _logger.LogInfo(GameLogFeature.Llm, $"MeaiOpenAiChatClient: [{msg.Role}] {content}");
                }

                if (options?.Tools != null && options.Tools.Count > 0)
                {
                    _logger.LogInfo(GameLogFeature.Llm, $"MeaiOpenAiChatClient: Tools ({options.Tools.Count}):");
                    foreach (MEAI.AITool tool in options.Tools)
                    {
                        if (tool is MEAI.AIFunction af)
                        {
                            _logger.LogInfo(GameLogFeature.Llm,
                                $"MeaiOpenAiChatClient:   - {af.Name}: {af.Description}");
                        }
                    }
                }
            }

            List<Dictionary<string, object>> messages = BuildMessagesPayload(msgs);
            List<Dictionary<string, object>> toolsList = BuildToolsPayload(options);

            Dictionary<string, object> req = new()
            {
                { "model", _settings.Model },
                { "temperature", options?.Temperature ?? _settings.Temperature },
                { "messages", messages }
            };
            if (options?.MaxOutputTokens.HasValue == true)
            {
                req["max_tokens"] = options.MaxOutputTokens.Value;
            }

            if (toolsList.Count > 0)
            {
                req["tools"] = toolsList;
            }

            string json = JsonConvert.SerializeObject(req);

            // Логируем сырой JSON запроса если включено
            if (_settings.EnableHttpDebugLogging)
            {
                _logger.LogInfo(GameLogFeature.Llm, $"MeaiOpenAiChatClient: Request JSON={json}");
            }

            using UnityWebRequest webReq = new(url, "POST");
            byte[] bodyRaw = Encoding.UTF8.GetBytes(json);
            webReq.uploadHandler = new UploadHandlerRaw(bodyRaw);
            webReq.downloadHandler = new DownloadHandlerBuffer();
            webReq.SetRequestHeader("Content-Type", "application/json");

            // OpenRouter требует эти заголовки
            if (url.Contains("openrouter"))
            {
                webReq.SetRequestHeader("HTTP-Referer", "https://unity.com");
                webReq.SetRequestHeader("X-Title", "CoreAI");
                _logger.LogInfo(GameLogFeature.Llm, $"MeaiOpenAiChatClient: Added OpenRouter headers");
            }

            string authorizationHeader = ResolveAuthorizationHeader();
            if (!string.IsNullOrEmpty(authorizationHeader))
            {
                webReq.SetRequestHeader("Authorization", authorizationHeader);
                _logger.LogInfo(GameLogFeature.Llm,
                    $"MeaiOpenAiChatClient: Authorization header set (len={authorizationHeader.Length})");
            }

            _logger.LogInfo(GameLogFeature.Llm, $"MeaiOpenAiChatClient: Timeout={_settings.RequestTimeoutSeconds}s");
            webReq.timeout = _settings.RequestTimeoutSeconds;

            UnityWebRequestAsyncOperation op = webReq.SendWebRequest();
            while (!op.isDone)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    try { webReq.Abort(); } catch { /* ignore */ }
                    cancellationToken.ThrowIfCancellationRequested();
                }
                await Task.Yield();
            }

            if (webReq.result != UnityWebRequest.Result.Success)
            {
                string responseBody = webReq.downloadHandler?.text ?? "";
                string errorDetail = !string.IsNullOrEmpty(responseBody)
                    ? $"{webReq.error} | Body: {responseBody}"
                    : webReq.error;
                _logger.LogWarning(GameLogFeature.Llm, $"MeaiOpenAiChatClient: {errorDetail}");
                throw BuildHttpException(webReq, responseBody, errorDetail);
            }

            // Логируем ответ от модели если включено
            string responseJson = webReq.downloadHandler.text;
            if (_settings.EnableHttpDebugLogging)
            {
                _logger.LogInfo(GameLogFeature.Llm, $"MeaiOpenAiChatClient: Response JSON={responseJson}");
            }

            MEAI.ChatResponse response = ParseResponse(responseJson);

            return response;
        }

        private static MEAI.ChatResponse ParseResponse(string json)
        {
            try
            {
                Dictionary<string, object> root = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
                JArray choices = root?["choices"] as JArray;
                if (choices == null || choices.Count == 0)
                {
                    return new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, ""));
                }

                JToken msg = choices[0]["message"];
                // reasoning_content (Qwen/LM Studio, OpenAI-совм.) — цепь размышлений; в ответе Assistant используем только content
                string content = msg?["content"]?.ToString() ?? "";

                // Strip <think>...</think> blocks if any
                if (!string.IsNullOrEmpty(content))
                {
                    content = System.Text.RegularExpressions.Regex.Replace(content, @"<think>[\s\S]*?</think>\s*", "",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
                }

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

                JObject usage = root?["usage"] as JObject;
                if (usage != null)
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

        public async IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<MEAI.ChatMessage> chatMessages,
            MEAI.ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            List<MEAI.ChatMessage> msgs = chatMessages.ToList();
            string url = _settings.ApiBaseUrl.TrimEnd('/') + "/chat/completions";

            // Build request JSON (reuse same logic as GetResponseAsync)
            List<Dictionary<string, object>> messages = BuildMessagesPayload(msgs);
            List<Dictionary<string, object>> toolsList = BuildToolsPayload(options);

            Dictionary<string, object> req = new()
            {
                { "model", _settings.Model },
                { "temperature", options?.Temperature ?? _settings.Temperature },
                { "messages", messages },
                { "stream", true }
            };
            if (options?.MaxOutputTokens.HasValue == true)
            {
                req["max_tokens"] = options.MaxOutputTokens.Value;
            }
            if (toolsList.Count > 0)
            {
                req["tools"] = toolsList;
            }

            string json = JsonConvert.SerializeObject(req);

            // ВАЖНО: UnityWebRequest и DownloadHandlerBuffer создаются из нативного
            // Unity API и требуют главного потока. Вызывающий код должен
            // выполняться на main thread (например, через UniTask/coroutine),
            // Task.Run приведёт к исключению "Create can only be called from the main thread".
            using UnityWebRequest webReq = new(url, "POST");
            byte[] bodyRaw = Encoding.UTF8.GetBytes(json);
            webReq.uploadHandler = new UploadHandlerRaw(bodyRaw);
            webReq.downloadHandler = new DownloadHandlerBuffer();
            webReq.SetRequestHeader("Content-Type", "application/json");
            webReq.SetRequestHeader("Accept", "text/event-stream");

            if (url.Contains("openrouter"))
            {
                webReq.SetRequestHeader("HTTP-Referer", "https://unity.com");
                webReq.SetRequestHeader("X-Title", "CoreAI");
            }
            string authorizationHeader = ResolveAuthorizationHeader();
            if (!string.IsNullOrEmpty(authorizationHeader))
            {
                webReq.SetRequestHeader("Authorization", authorizationHeader);
            }
            webReq.timeout = _settings.RequestTimeoutSeconds;

            _logger.LogInfo(GameLogFeature.Llm, $"MeaiOpenAiChatClient: POST (stream) {url}");

            UnityWebRequestAsyncOperation op = webReq.SendWebRequest();
            int lastProcessed = 0;
            bool cancelled = false;
            SseToolCallAccumulator toolAccumulator = new();
            try
            {
                // Poll for SSE chunks
                while (!op.isDone)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        cancelled = true;
                        try { webReq.Abort(); } catch { /* ignore */ }
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    string partial = webReq.downloadHandler?.text ?? "";
                    if (partial.Length > lastProcessed)
                    {
                        string newData = partial.Substring(lastProcessed);
                        lastProcessed = partial.Length;

                        foreach (MEAI.ChatResponseUpdate update in ParseSseUpdates(newData, toolAccumulator))
                        {
                            yield return update;
                        }
                    }

                    await Task.Yield();
                }

                // Process any remaining data after request completed
                if (webReq.result == UnityWebRequest.Result.Success)
                {
                    string fullText = webReq.downloadHandler?.text ?? "";
                    if (fullText.Length > lastProcessed)
                    {
                        string remaining = fullText.Substring(lastProcessed);
                        foreach (MEAI.ChatResponseUpdate update in ParseSseUpdates(remaining, toolAccumulator))
                        {
                            yield return update;
                        }
                    }

                    // Flush any accumulated partial tool calls at stream end
                    MEAI.ChatResponseUpdate flushed = toolAccumulator.Flush();
                    if (flushed != null)
                    {
                        yield return flushed;
                    }
                }
                else if (webReq.result != UnityWebRequest.Result.Success && !cancelled &&
                         !cancellationToken.IsCancellationRequested)
                {
                    string streamBody = webReq.downloadHandler?.text ?? "";
                    string streamErr = !string.IsNullOrEmpty(streamBody)
                        ? $"{webReq.error} | Body: {streamBody}"
                        : webReq.error;
                    _logger.LogWarning(GameLogFeature.Llm,
                        $"MeaiOpenAiChatClient: stream error — {streamErr}");
                    throw BuildHttpException(webReq, streamBody, streamErr);
                }
            }
            finally
            {
                // If consumer stops enumeration early (or token cancels), close request aggressively.
                if (!op.isDone)
                {
                    try { webReq.Abort(); } catch { /* ignore */ }
                }
            }
        }

        /// <summary>
        /// Парсит SSE data: строки и извлекает delta.content и delta.tool_calls из JSON чанков.
        /// delta.reasoning_content (Qwen и др.) обрабатывается в <see cref="ExtractDeltaUpdate"/> и в UI не попадает.
        /// Returns ChatResponseUpdate objects that may contain text. Tool calls are accumulated
        /// in the <paramref name="accumulator"/> and flushed when complete.
        /// </summary>
        private static IEnumerable<MEAI.ChatResponseUpdate> ParseSseUpdates(string raw, SseToolCallAccumulator accumulator)
        {
            string[] lines = raw.Split('\n');
            foreach (string line in lines)
            {
                string trimmed = line.Trim();
                if (!trimmed.StartsWith("data: ")) continue;
                string data = trimmed.Substring(6);
                if (data == "[DONE]") yield break;

                MEAI.ChatResponseUpdate update = ExtractDeltaUpdate(data, accumulator);
                if (update != null)
                {
                    yield return update;
                }
            }
        }

        private static LlmClientException BuildHttpException(
            UnityWebRequest request,
            string responseBody,
            string errorDetail)
        {
            int status = (int)request.responseCode;
            LlmErrorCode code = MapHttpStatus(status, responseBody, errorDetail);
            int? retryAfter = null;
            string retryHeader = request.GetResponseHeader("Retry-After");
            if (int.TryParse(retryHeader, out int parsedRetry))
            {
                retryAfter = parsedRetry;
            }

            return new LlmClientException(
                $"HTTP error {status}: {ExtractProviderMessage(responseBody, errorDetail)}",
                code,
                status > 0 ? status : null,
                retryAfter,
                responseBody);
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

        private static LlmErrorCode MapHttpStatus(int status, string body, string fallback)
        {
            string text = ((body ?? "") + " " + (fallback ?? "")).ToLowerInvariant();
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

        /// <summary>
        /// Извлекает choices[0].delta из SSE JSON: видимый текст — только <c>delta.content</c>.
        /// <c>delta.reasoning_content</c> (Qwen/LM Studio и т.п.) намеренно не передается в UI.
        /// Tool call deltas are accumulated in the <paramref name="accumulator"/> rather than
        /// emitted immediately, because cloud providers split name/arguments across chunks.
        /// </summary>
        private static MEAI.ChatResponseUpdate ExtractDeltaUpdate(string json, SseToolCallAccumulator accumulator)
        {
            try
            {
                JObject obj = JObject.Parse(json);
                JToken delta = obj?["choices"]?[0]?["delta"];
                if (delta == null) return null;

                // Сообщаем, что дельта обработана; цепь размышлений в ответ пользователю не идет
                _ = delta["reasoning_content"]?.ToString();

                string content = delta["content"]?.ToString();
                JArray toolCallsArray = delta["tool_calls"] as JArray;

                // Accumulate tool call deltas (they arrive spread across multiple SSE chunks)
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

                // Only return an update if there's visible text content
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

        /// <summary>
        /// Тестовый hook: разбор одной JSON-строки тела <c>data: …</c> (без префикса <c>data: </c>).
        /// Только для NUnit/EditMode. Не вызывать из продакшн-кода.
        /// </summary>
        internal static MEAI.ChatResponseUpdate ParseSseDataLineForTests(string dataJson) =>
            ExtractDeltaUpdate(dataJson, new SseToolCallAccumulator());

        /// <summary>
        /// Accumulates partial SSE delta.tool_calls across multiple chunks.
        /// OpenAI streams tool calls as: chunk 1 has id+name, chunks 2..N have arguments fragments.
        /// Call <see cref="Flush"/> at stream end to emit completed FunctionCallContent.
        /// </summary>
        private sealed class SseToolCallAccumulator
        {
            private readonly Dictionary<int, (string id, string name, StringBuilder args)> _pending = new();

            /// <summary>Feed one delta.tool_calls entry. Safe to call with all-null values.</summary>
            public void Feed(int index, string callId, string name, string argumentsFragment)
            {
                if (!_pending.TryGetValue(index, out var entry))
                {
                    entry = (callId, name, new StringBuilder());
                    _pending[index] = entry;
                }
                else
                {
                    // Update id/name if provided (first chunk has them, subsequent don't)
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

            /// <summary>
            /// Flush all accumulated tool calls into a single ChatResponseUpdate.
            /// Returns null if no tool calls were accumulated.
            /// </summary>
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
                        catch { /* Malformed JSON accumulated — skip this tool call */ }
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

        /// <summary>Строит массив messages для HTTP payload (переиспользуется в обоих методах).</summary>
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

        /// <summary>Строит массив tools для HTTP payload.</summary>
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

        public object? GetService(Type serviceType, object? key = null)
        {
            return null;
        }
    }
}
#endif
