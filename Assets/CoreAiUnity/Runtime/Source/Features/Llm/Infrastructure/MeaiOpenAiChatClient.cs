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

            List<Dictionary<string, object>> messages = new();
            foreach (MEAI.ChatMessage msg in msgs)
            {
                // Для tool messages нужно извлекать content и tool_call_id правильно
                string content = msg.Text ?? "";
                if (string.IsNullOrEmpty(content) && msg.Contents != null && msg.Contents.Count > 0)
                {
                    // Tool messages могут иметь content в Contents коллекции
                    MEAI.TextContent textContent = msg.Contents.OfType<MEAI.TextContent>().FirstOrDefault();
                    if (textContent != null)
                    {
                        content = textContent.Text;
                    }
                    else
                    {
                        // Fallback: сериализуем весь Contents
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

                        // Если есть tool_calls, то в content обычно отправляем пустую строку или текст (если модель дала текст)
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

            if (!string.IsNullOrEmpty(_settings.ApiKey))
            {
                webReq.SetRequestHeader("Authorization", "Bearer " + _settings.ApiKey);
                _logger.LogInfo(GameLogFeature.Llm,
                    $"MeaiOpenAiChatClient: API key set (len={_settings.ApiKey.Length})");
            }

            _logger.LogInfo(GameLogFeature.Llm, $"MeaiOpenAiChatClient: Timeout={_settings.RequestTimeoutSeconds}s");
            webReq.timeout = _settings.RequestTimeoutSeconds;

            UnityWebRequestAsyncOperation op = webReq.SendWebRequest();
            while (!op.isDone)
            {
                await Task.Yield();
            }

            if (webReq.result != UnityWebRequest.Result.Success)
            {
                _logger.LogError(GameLogFeature.Llm, $"MeaiOpenAiChatClient: {webReq.error}");
                throw new Exception($"HTTP error: {webReq.error}");
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
            MEAI.ChatResponse response = await GetResponseAsync(chatMessages, options, cancellationToken);
            if (!string.IsNullOrEmpty(response.Text))
            {
                yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, response.Text);
            }
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