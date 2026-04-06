#if !COREAI_NO_LLM
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Config;
using CoreAI.Infrastructure.Logging;
using LLMUnity;
using MEAI = Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;
using System.Linq;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// MEAI IChatClient обёртка над LLMAgent.
    /// Парсит tool calls из текстового ответа модели (Qwen не поддерживает структурные tool_calls).
    /// </summary>
    public sealed class LlmUnityMeaiChatClient : MEAI.IChatClient
    {
        private readonly LLMAgent _unityAgent;
        private readonly IGameLogger _logger;

        public LlmUnityMeaiChatClient(LLMAgent agent, IGameLogger logger)
        {
            _unityAgent = agent ?? throw new ArgumentNullException(nameof(agent));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<MEAI.ChatResponse> GetResponseAsync(
            IEnumerable<MEAI.ChatMessage> chatMessages,
            MEAI.ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var msgs = new List<MEAI.ChatMessage>(chatMessages);
            string userMessage = "";
            foreach (var msg in msgs)
            {
                if (msg.Role == MEAI.ChatRole.User)
                {
                    foreach (var item in msg.Contents)
                    {
                        if (item is MEAI.TextContent tc)
                            userMessage += tc.Text + "\n";
                    }
                }
            }

            string result = await _unityAgent.Chat(userMessage.Trim(), addToHistory: false);

            var responseContents = new List<MEAI.AIContent>();
            var tools = options?.Tools?.ToList() ?? new List<MEAI.AITool>();

            if (TryParseToolCallFromText(result, tools,
                    out List<MEAI.FunctionCallContent> toolCallContents, out string cleanedText))
            {
                responseContents.AddRange(toolCallContents);
                if (!string.IsNullOrEmpty(cleanedText))
                    responseContents.Add(new MEAI.TextContent(cleanedText));
            }
            else
            {
                responseContents.Add(new MEAI.TextContent(result));
            }

            var responseMsg = new MEAI.ChatMessage(MEAI.ChatRole.Assistant, responseContents);
            return new MEAI.ChatResponse(responseMsg)
            {
                ModelId = options?.ModelId,
                FinishReason = MEAI.ChatFinishReason.Stop
            };
        }

        public static bool TryParseToolCallFromText(
            string text,
            IReadOnlyList<MEAI.AITool> availableTools,
            out List<MEAI.FunctionCallContent> toolCalls,
            out string cleanedText)
        {
            toolCalls = new List<MEAI.FunctionCallContent>();
            cleanedText = text;

            if (string.IsNullOrEmpty(text) || availableTools == null || availableTools.Count == 0)
                return false;

            var jsonRegex = new Regex(
                @"```json\s*(\{[^`]+\})\s*```|(\{[^{}]*""name""\s*:\s*""([^""]+)""[^{}]*""arguments""\s*:\s*\{[^{}]*\}[^{}]*\})",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            var match = jsonRegex.Match(text);
            if (!match.Success)
                return false;

            try
            {
                string jsonStr = match.Groups[1].Success ? match.Groups[1].Value :
                    match.Groups[2].Success ? match.Groups[2].Value : "";

                var json = JObject.Parse(jsonStr);
                string functionName = null;
                var argumentsDict = new Dictionary<string, object?>();

                if (json["name"] != null && json["arguments"] != null)
                {
                    functionName = json["name"]?.ToString()?.Trim();
                    var argsObj = json["arguments"] as JObject;
                    if (argsObj != null)
                    {
                        foreach (var prop in argsObj.Properties())
                        {
                            argumentsDict[prop.Name] = prop.Value?.Type == JTokenType.String
                                ? prop.Value.ToString()
                                : prop.Value?.ToObject<object>();
                        }
                    }
                }

                if (functionName == null)
                    return false;

                var functionCall = new MEAI.FunctionCallContent($"call_{functionName}_1", functionName, argumentsDict);
                toolCalls.Add(functionCall);

                cleanedText = text.Substring(0, match.Index) + text.Substring(match.Index + match.Length);
                cleanedText = cleanedText.Trim();

                return true;
            }
            catch
            {
                return false;
            }
        }

        public async IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<MEAI.ChatMessage> chatMessages,
            MEAI.ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(chatMessages, options, cancellationToken);
            foreach (var msg in response.Messages)
            {
                yield return new MEAI.ChatResponseUpdate(msg.Role, msg.Text);
            }
        }

        public object? GetService(Type serviceType, object? key) => null;
        public void Dispose() { }
    }
}
#endif
