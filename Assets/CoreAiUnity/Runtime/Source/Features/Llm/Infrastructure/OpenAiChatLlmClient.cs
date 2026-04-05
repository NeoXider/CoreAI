using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using UnityEngine;
using UnityEngine.Networking;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Клиент к эндпоинту <c>/chat/completions</c> в формате OpenAI (совместим с многими прокси: vLLM, LiteLLM, LM Studio и т.д.).
    /// Вызовы <see cref="CompleteAsync"/> должны идти с главного потока Unity (как и <see cref="MeaiLlmUnityClient"/>).
    /// </summary>
    public sealed class OpenAiChatLlmClient : ILlmClient
    {
        private readonly OpenAiHttpLlmSettings _settings;
        private IReadOnlyList<ILlmTool> _tools;

        /// <param name="settings">Asset с URL, ключом и именем модели.</param>
        public OpenAiChatLlmClient(OpenAiHttpLlmSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _tools = Array.Empty<ILlmTool>();
        }

        /// <inheritdoc />
        public void SetTools(IReadOnlyList<ILlmTool> tools)
        {
            _tools = tools ?? Array.Empty<ILlmTool>();
        }

        /// <inheritdoc />
        public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            bool useResponsesApi = _settings.ApiBaseUrl.Contains("/responses");
            string endpoint = useResponsesApi ? "/responses" : "/chat/completions";
            string url = _settings.ApiBaseUrl + endpoint;
            string body = BuildJsonBody(request);
            UnityWebRequest req = new(url, UnityWebRequest.kHttpVerbPOST);
            byte[] bodyRaw = Encoding.UTF8.GetBytes(body);
            req.uploadHandler = new UploadHandlerRaw(bodyRaw);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            if (!string.IsNullOrEmpty(_settings.ApiKey))
            {
                req.SetRequestHeader("Authorization", "Bearer " + _settings.ApiKey);
            }

            req.timeout = _settings.RequestTimeoutSeconds;

            TaskCompletionSource<LlmCompletionResult> tcs = new();
            CancellationTokenRegistration ctr = default;
            if (cancellationToken.CanBeCanceled)
            {
                ctr = cancellationToken.Register(() =>
                {
                    req.Abort();
                    tcs.TrySetResult(new LlmCompletionResult { Ok = false, Error = "Cancelled" });
                });
            }

            UnityWebRequestAsyncOperation op = req.SendWebRequest();
            op.completed += _ =>
            {
                try
                {
                    ctr.Dispose();
                    if (tcs.Task.IsCompleted)
                    {
                        return;
                    }

                    if (req.result != UnityWebRequest.Result.Success)
                    {
                        tcs.TrySetResult(new LlmCompletionResult
                        {
                            Ok = false,
                            Error = string.IsNullOrEmpty(req.error) ? req.downloadHandler.text : req.error
                        });
                        return;
                    }

                    string text = req.downloadHandler.text ?? "";
                    if (string.IsNullOrEmpty(text))
                    {
                        tcs.TrySetResult(new LlmCompletionResult { Ok = false, Error = "Empty response from LLM" });
                        return;
                    }

                    if (text.IndexOf("\"error\"", StringComparison.Ordinal) >= 0)
                    {
                        tcs.TrySetResult(new LlmCompletionResult { Ok = false, Error = text });
                        return;
                    }

                    (string Content, OaiUsage Usage) parsed = TryParseChatResponse(text);
                    if (parsed.Content == null)
                    {
                        tcs.TrySetResult(new LlmCompletionResult
                            { Ok = false, Error = "Bad chat/completions JSON: " + text });
                    }
                    else
                    {
                        LlmCompletionResult ok = new() { Ok = true, Content = parsed.Content };
                        if (parsed.Usage != null)
                        {
                            ok.PromptTokens = parsed.Usage.prompt_tokens;
                            ok.CompletionTokens = parsed.Usage.completion_tokens;
                            ok.TotalTokens = parsed.Usage.total_tokens;
                        }

                        tcs.TrySetResult(ok);
                    }
                }
                finally
                {
                    req.Dispose();
                }
            };

            return tcs.Task;
        }

        private string BuildJsonBody(LlmCompletionRequest request)
        {
            float temperature = request.Temperature > 0 ? request.Temperature : _settings.Temperature;
            int maxOutputTokens = request.MaxOutputTokens.GetValueOrDefault(
                Math.Max(128, Math.Min(2048, request.ContextWindowTokens / 4)));

            IReadOnlyList<ILlmTool> tools = request.Tools ?? _tools;

            // Detect if using new Responses API (LM Studio) vs Chat Completions
            bool useResponsesApi = _settings.ApiBaseUrl.Contains("/responses");

            if (useResponsesApi)
            {
                return BuildResponsesApiBody(request, temperature, maxOutputTokens, tools);
            }
            else
            {
                return BuildChatCompletionsBody(request, temperature, maxOutputTokens, tools);
            }
        }

        private string BuildChatCompletionsBody(LlmCompletionRequest request, float temperature, int maxOutputTokens,
            IReadOnlyList<ILlmTool> tools)
        {
            Dictionary<string, object> requestDict = new()
            {
                { "model", _settings.Model },
                { "temperature", temperature },
                { "max_tokens", maxOutputTokens },
                {
                    "messages", new List<Dictionary<string, string>>
                    {
                        new() { { "role", "system" }, { "content", request.SystemPrompt ?? "" } },
                        new() { { "role", "user" }, { "content", request.UserPayload ?? "" } }
                    }
                }
            };

            if (tools != null && tools.Count > 0)
            {
                List<Dictionary<string, object>> toolsList = new();
                foreach (ILlmTool tool in tools)
                {
                    toolsList.Add(new Dictionary<string, object>
                    {
                        { "type", "function" },
                        {
                            "function", new Dictionary<string, object>
                            {
                                { "name", tool.Name },
                                { "description", tool.Description },
                                { "parameters", JsonConvert.DeserializeObject(tool.ParametersSchema ?? "{}") }
                            }
                        }
                    });
                }

                requestDict["tools"] = toolsList;
            }

            return JsonConvert.SerializeObject(requestDict, Formatting.None);
        }

        private string BuildResponsesApiBody(LlmCompletionRequest request, float temperature, int maxOutputTokens,
            IReadOnlyList<ILlmTool> tools)
        {
            List<Dictionary<string, object>> inputList = new();

            // System message
            if (!string.IsNullOrEmpty(request.SystemPrompt))
            {
                inputList.Add(new Dictionary<string, object>
                {
                    { "role", "system" },
                    { "content", request.SystemPrompt }
                });
            }

            // User message  
            inputList.Add(new Dictionary<string, object>
            {
                { "role", "user" },
                { "content", request.UserPayload ?? "" }
            });

            Dictionary<string, object> requestDict = new()
            {
                { "model", _settings.Model },
                { "input", inputList },
                { "max_output_tokens", maxOutputTokens }
            };

            // Add reasoning settings for Qwen models
            requestDict["reasoning"] = new Dictionary<string, string>
            {
                { "effort", "low" },
                { "summary", "auto" }
            };

            // Add tools for Responses API
            if (tools != null && tools.Count > 0)
            {
                List<Dictionary<string, object>> toolsList = new();
                foreach (ILlmTool tool in tools)
                {
                    toolsList.Add(new Dictionary<string, object>
                    {
                        { "type", "function" },
                        { "name", tool.Name },
                        { "description", tool.Description },
                        { "parameters", JsonConvert.DeserializeObject(tool.ParametersSchema ?? "{}") }
                    });
                }

                requestDict["tools"] = toolsList;
            }

            return JsonConvert.SerializeObject(requestDict, Formatting.None);
        }

        private static string BuildToolsJson(IReadOnlyList<ILlmTool> tools)
        {
            if (tools == null || tools.Count == 0)
            {
                return "";
            }

            List<string> toolDefs = new();
            foreach (ILlmTool tool in tools)
            {
                string escapedName = JsonEscape(tool.Name);
                string escapedDesc = JsonEscape(tool.Description);
                // Parameters schema is already valid JSON - don't escape it
                string escapedParams = tool.ParametersSchema ?? "{}";
                toolDefs.Add("{\"type\":\"function\",\"function\":{\"name\":\"" + escapedName +
                             "\",\"description\":\"" + escapedDesc + "\",\"parameters\":" + escapedParams + "}}");
            }

            return ",\"tools\":[" + string.Join(",", toolDefs) + "]";
        }

        private static string JsonEscape(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return "";
            }

            StringBuilder sb = new(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    default:
                        if (c < ' ')
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        }
                        else
                        {
                            sb.Append(c);
                        }

                        break;
                }
            }

            return sb.ToString();
        }

        private static (string Content, OaiUsage Usage) TryParseChatResponse(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return (null, null);
            }

            if (!LlmResponseSanitizer.TryPrepareJsonObject(json, out string body) || string.IsNullOrEmpty(body))
            {
                body = json.Trim();
            }

            try
            {
                // Try Responses API first (LM Studio new format)
                if (body.Contains("\"output\""))
                {
                    return ParseResponsesApiResponse(body);
                }

                // Fall back to Chat Completions format
                OaiChatResponse dto = JsonUtility.FromJson<OaiChatResponse>(body);
                if (dto?.choices == null || dto.choices.Length == 0)
                {
                    return (null, null);
                }

                OaiMessage m = dto.choices[0].message;
                string content = m?.content ?? "";
                return (content, dto.usage);
            }
            catch
            {
                return (null, null);
            }
        }

        private static (string Content, OaiUsage Usage) ParseResponsesApiResponse(string body)
        {
            try
            {
                Dictionary<string, object> response = JsonConvert.DeserializeObject<Dictionary<string, object>>(body);
                if (response == null || !response.ContainsKey("output"))
                {
                    return (null, null);
                }

                JArray outputs = response["output"] as Newtonsoft.Json.Linq.JArray;
                if (outputs == null || outputs.Count == 0)
                {
                    return (null, null);
                }

                string content = "";

                // Look for message type in output
                foreach (JToken output in outputs)
                {
                    JObject outputObj = output as Newtonsoft.Json.Linq.JObject;
                    if (outputObj == null)
                    {
                        continue;
                    }

                    string type = outputObj["type"]?.ToString();
                    if (type == "message")
                    {
                        JArray contentArray = outputObj["content"] as Newtonsoft.Json.Linq.JArray;
                        if (contentArray != null)
                        {
                            foreach (JToken item in contentArray)
                            {
                                JObject itemObj = item as Newtonsoft.Json.Linq.JObject;
                                if (itemObj?["type"]?.ToString() == "output_text")
                                {
                                    content = itemObj["text"]?.ToString() ?? "";
                                    break;
                                }
                            }
                        }

                        break;
                    }
                }

                // Parse usage if available
                OaiUsage usage = null;
                if (response.ContainsKey("usage"))
                {
                    JObject usageObj = response["usage"] as Newtonsoft.Json.Linq.JObject;
                    if (usageObj != null)
                    {
                        int inputTokens = 0;
                        int outputTokens = 0;
                        if (usageObj["input_tokens"] != null)
                        {
                            inputTokens = usageObj["input_tokens"].ToObject<int>();
                        }

                        if (usageObj["output_tokens"] != null)
                        {
                            outputTokens = usageObj["output_tokens"].ToObject<int>();
                        }

                        usage = new OaiUsage
                        {
                            prompt_tokens = inputTokens,
                            completion_tokens = outputTokens,
                            total_tokens = inputTokens + outputTokens
                        };
                    }
                }

                return (content, usage);
            }
            catch
            {
                return (null, null);
            }
        }

        [Serializable]
        private class OaiChatResponse
        {
            public OaiChoice[] choices;
            public OaiUsage usage;
        }

        [Serializable]
        private class OaiUsage
        {
            public int prompt_tokens;
            public int completion_tokens;
            public int total_tokens;
        }

        [Serializable]
        private class OaiChoice
        {
            public OaiMessage message;
        }

        [Serializable]
        private class OaiMessage
        {
            public string content;
        }

        [Serializable]
        private class SimpleJson
        {
            public string model;
            public float temperature;
            public int max_tokens;
            public object[] messages;
            public object[] tools;
        }
    }
}