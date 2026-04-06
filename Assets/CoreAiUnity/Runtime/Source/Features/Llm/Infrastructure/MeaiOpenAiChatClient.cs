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
            var msgs = chatMessages.ToList();
            string url = _settings.ApiBaseUrl.TrimEnd('/') + "/chat/completions";

            var messages = new List<Dictionary<string, string>>();
            foreach (var msg in msgs)
            {
                messages.Add(new Dictionary<string, string>
                {
                    { "role", msg.Role.ToString().ToLowerInvariant() },
                    { "content", msg.Text ?? "" }
                });
            }

            var toolsList = new List<Dictionary<string, object>>();
            if (options?.Tools != null)
            {
                foreach (var tool in options.Tools)
                {
                    if (tool is MEAI.AIFunction af)
                    {
                        toolsList.Add(new Dictionary<string, object>
                        {
                            { "type", "function" },
                            { "function", new Dictionary<string, object>
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

            var req = new Dictionary<string, object>
            {
                { "model", _settings.Model },
                { "temperature", options?.Temperature ?? _settings.Temperature },
                { "messages", messages }
            };
            if (toolsList.Count > 0) req["tools"] = toolsList;

            string json = JsonConvert.SerializeObject(req);

            using var webReq = new UnityWebRequest(url, "POST");
            byte[] bodyRaw = Encoding.UTF8.GetBytes(json);
            webReq.uploadHandler = new UploadHandlerRaw(bodyRaw);
            webReq.downloadHandler = new DownloadHandlerBuffer();
            webReq.SetRequestHeader("Content-Type", "application/json");
            if (!string.IsNullOrEmpty(_settings.ApiKey))
                webReq.SetRequestHeader("Authorization", "Bearer " + _settings.ApiKey);
            webReq.timeout = _settings.RequestTimeoutSeconds;

            var op = webReq.SendWebRequest();
            while (!op.isDone) await Task.Yield();

            if (webReq.result != UnityWebRequest.Result.Success)
            {
                _logger.LogError(GameLogFeature.Llm, $"MeaiOpenAiChatClient: {webReq.error}");
                throw new Exception($"HTTP error: {webReq.error}");
            }

            return ParseResponse(webReq.downloadHandler.text);
        }

        private static MEAI.ChatResponse ParseResponse(string json)
        {
            try
            {
                var root = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
                var choices = root?["choices"] as JArray;
                if (choices == null || choices.Count == 0) return new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, ""));

                var msg = choices[0]["message"];
                string content = msg?["content"]?.ToString() ?? "";
                var toolCalls = msg?["tool_calls"] as JArray;

                var response = new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, content));

                if (toolCalls != null && toolCalls.Count > 0)
                {
                    var contents = new List<MEAI.AIContent>();
                    if (!string.IsNullOrEmpty(content)) contents.Add(new MEAI.TextContent(content));
                    foreach (var tc in toolCalls)
                    {
                        var func = tc["function"] as JObject;
                        if (func != null)
                        {
                            contents.Add(new MEAI.FunctionCallContent(
                                tc["id"]?.ToString() ?? "",
                                func["name"]?.ToString() ?? "",
                                JsonConvert.DeserializeObject<Dictionary<string, object?>>(func["arguments"]?.ToString() ?? "{}")));
                        }
                    }
                    response.Messages[0] = new MEAI.ChatMessage(MEAI.ChatRole.Assistant, contents);
                }

                var usage = root?["usage"] as JObject;
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
            catch { return new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, "")); }
        }

        public async IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<MEAI.ChatMessage> chatMessages,
            MEAI.ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(chatMessages, options, cancellationToken);
            if (!string.IsNullOrEmpty(response.Text))
                yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, response.Text);
        }

        public void Dispose() { }
        public object? GetService(Type serviceType, object? key = null) => null;
    }
}
#endif
