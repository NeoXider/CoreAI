#if !COREAI_NO_LLM && !UNITY_WEBGL
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
            List<MEAI.ChatMessage> msgs = new(chatMessages);
            string userMessage = "";
            string sysMessage = "";

            foreach (MEAI.ChatMessage msg in msgs)
            {
                if (msg.Role == MEAI.ChatRole.System)
                {
                    foreach (MEAI.AIContent item in msg.Contents)
                    {
                        if (item is MEAI.TextContent tc)
                        {
                            sysMessage += tc.Text + "\n";
                        }
                    }
                }
                else if (msg.Role == MEAI.ChatRole.User)
                {
                    foreach (MEAI.AIContent item in msg.Contents)
                    {
                        if (item is MEAI.TextContent tc)
                        {
                            userMessage += "User: " + tc.Text + "\n";
                        }
                    }
                }
                else if (msg.Role == MEAI.ChatRole.Assistant)
                {
                    foreach (MEAI.AIContent item in msg.Contents)
                    {
                        if (item is MEAI.TextContent tc)
                        {
                            userMessage += "Assistant: " + tc.Text + "\n";
                        }
                        else if (item is MEAI.FunctionCallContent fcc)
                        {
                            // Simulate tool call for history context
                            userMessage +=
                                $"Assistant Tool Call:\n```json\n{{\"name\": \"{fcc.Name}\", \"arguments\": {Newtonsoft.Json.JsonConvert.SerializeObject(fcc.Arguments)}}}\n```\n";
                        }
                    }
                }
                else if (msg.Role == MEAI.ChatRole.Tool)
                {
                    foreach (MEAI.AIContent item in msg.Contents)
                    {
                        if (item is MEAI.FunctionResultContent frc)
                        {
                            userMessage += $"\n[SYSTEM ERROR]: Your previous tool call failed: {frc.Result}\nPlease try again heavily verifying the tool name, or answer via normal text.\n";
                        }
                    }
                }
            }

            if (options?.Tools != null && options.Tools.Count > 0)
            {
                sysMessage += "\n\nCRITICAL SYSTEM RULES FOR TOOLS:\n";
                sysMessage +=
                    "1. You have access to the following tools. You MUST use one if it matches the user request.\n";
                sysMessage +=
                    "2. To use a tool, output ONLY valid JSON matching this format: ```json\n{\"name\": \"tool_name\", \"arguments\": {\"arg\": \"val\"}}\n```\n";
                sysMessage +=
                    "3. DO NOT output conversational text if you call a tool. ONLY output the JSON block.\n\nAVAILABLE TOOLS:\n";

                JArray toolNames = new();
                foreach (MEAI.AITool tool in options.Tools)
                {
                    toolNames.Add(tool.Name);
                    sysMessage += $"- name: {tool.Name}\n  description: {tool.Description}\n";
                    if (tool is MEAI.AIFunction fn)
                    {
                        sysMessage += $"  parameters schema: {fn.JsonSchema.ToString()}\n";
                    }
                }

                // DO NOT restrict generation strictly to JSON! It prevents the LLM from outputting conversational text
                // when a tool result is successfully executed, causing infinite loops of hallucinated tool calls.
                _unityAgent.grammar = "";
            }
            else
            {
                _unityAgent.grammar = ""; // Clear grammar if no tools
            }

            // Apply a strict system-level instruction to ban Markdown formatting globally in LlmUnity
            sysMessage += "\n\nCRITICAL INSTRUCTION: NEVER use Markdown formatting (such as **, _, #). Output plain text ONLY.\n";
            _unityAgent.systemPrompt = sysMessage.TrimStart().TrimEnd();

            if (options?.Temperature.HasValue == true)
            {
                _unityAgent.temperature = options.Temperature.Value;
            }

            if (options?.MaxOutputTokens.HasValue == true)
            {
                _unityAgent.numPredict = options.MaxOutputTokens.Value;
            }

            string result = await _unityAgent.Chat(userMessage.Trim(), addToHistory: false);

            // Strip <think>...</think> blocks produced by reasoning/thinking mode (Qwen3.5, DeepSeek, etc.)
            if (!string.IsNullOrEmpty(result))
            {
                result = Regex.Replace(result, @"<think>[\s\S]*?</think>\s*", "", RegexOptions.IgnoreCase).Trim();
            }

            List<MEAI.AIContent> responseContents = new();
            List<MEAI.AITool> tools = options?.Tools?.ToList() ?? new List<MEAI.AITool>();

            if (TryParseToolCallFromText(result, tools,
                    out List<MEAI.FunctionCallContent> toolCallContents, out string cleanedText))
            {
                responseContents.AddRange(toolCallContents);
                if (!string.IsNullOrEmpty(cleanedText))
                {
                    responseContents.Add(new MEAI.TextContent(cleanedText));
                }
            }
            else
            {
                responseContents.Add(new MEAI.TextContent(result));
            }

            MEAI.ChatMessage responseMsg = new(MEAI.ChatRole.Assistant, responseContents);
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

            // Strip <think>...</think> blocks from text before parsing
            if (!string.IsNullOrEmpty(text))
            {
                text = Regex.Replace(text, @"<think>[\s\S]*?</think>\s*", "", RegexOptions.IgnoreCase).Trim();
            }

            cleanedText = text;

            if (string.IsNullOrEmpty(text) || availableTools == null || availableTools.Count == 0)
            {
                return false;
            }

            int firstBrace = text.IndexOf('{');
            int lastBrace = text.LastIndexOf('}');

            if (firstBrace == -1 || lastBrace == -1 || lastBrace <= firstBrace)
            {
                return false;
            }

            string possibleJson = text.Substring(firstBrace, lastBrace - firstBrace + 1);

            try
            {
                JObject json = JObject.Parse(possibleJson);
                string functionName = null;
                Dictionary<string, object> argumentsDict = new();

                if (json["name"] != null && json["arguments"] != null)
                {
                    functionName = json["name"]?.ToString()?.Trim();
                    JObject argsObj = json["arguments"] as JObject;
                    if (argsObj != null)
                    {
                        foreach (JProperty prop in argsObj.Properties())
                        {
                            argumentsDict[prop.Name] = prop.Value?.Type == JTokenType.String
                                ? prop.Value.ToString()
                                : prop.Value?.ToObject<object>();
                        }
                    }
                }

                if (string.IsNullOrEmpty(functionName))
                {
                    return false;
                }

                MEAI.FunctionCallContent functionCall = new($"call_{functionName}_1", functionName, argumentsDict);
                toolCalls.Add(functionCall);

                // Clean up the parsed JSON from the text
                cleanedText = text.Substring(0, firstBrace) + text.Substring(lastBrace + 1);
                cleanedText = cleanedText.Replace("```json", "").Replace("```", "").Trim();

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
            // Подготавливаем промпт так же как в GetResponseAsync
            List<MEAI.ChatMessage> msgs = new(chatMessages);
            string userMessage = "";
            string sysMessage = "";

            foreach (MEAI.ChatMessage msg in msgs)
            {
                if (msg.Role == MEAI.ChatRole.System)
                {
                    foreach (MEAI.AIContent item in msg.Contents)
                    {
                        if (item is MEAI.TextContent tc) sysMessage += tc.Text + "\n";
                    }
                }
                else if (msg.Role == MEAI.ChatRole.User)
                {
                    foreach (MEAI.AIContent item in msg.Contents)
                    {
                        if (item is MEAI.TextContent tc) userMessage += "User: " + tc.Text + "\n";
                    }
                }
                else if (msg.Role == MEAI.ChatRole.Assistant)
                {
                    foreach (MEAI.AIContent item in msg.Contents)
                    {
                        if (item is MEAI.TextContent tc) userMessage += "Assistant: " + tc.Text + "\n";
                    }
                }
            }

            sysMessage += "\nCRITICAL INSTRUCTION: NEVER use Markdown formatting (such as **, _, #). Output plain text ONLY.\n";
            _unityAgent.systemPrompt = sysMessage.TrimStart().TrimEnd();

            if (options?.Temperature.HasValue == true)
                _unityAgent.temperature = options.Temperature.Value;
            if (options?.MaxOutputTokens.HasValue == true)
                _unityAgent.numPredict = options.MaxOutputTokens.Value;

            // Настоящий стриминг через LLMAgent.Chat callback.
            // Callback получает полный текст на данный момент — вычисляем дельту.
            // ВАЖНО: <think>-блоки НЕ фильтруем здесь — это делает внешний
            // stateful CoreAI.Ai.ThinkBlockStreamFilter в MeaiLlmClient.CompleteStreamingAsync.
            var chunkQueue = new System.Collections.Concurrent.ConcurrentQueue<string>();
            bool isCompleted = false;
            int previousLength = 0;
            string previousText = "";
            var deltaLock = new object();

            _ = _unityAgent.Chat(userMessage.Trim(),
                (string fullSoFar) =>
                {
                    if (string.IsNullOrEmpty(fullSoFar)) return;

                    // Delta-диф под локом: LLMUnity может вызывать callback из worker-потока.
                    string delta;
                    lock (deltaLock)
                    {
                        if (fullSoFar.Length <= previousLength) return;
                        delta = fullSoFar.Substring(previousLength);
                        previousLength = fullSoFar.Length;
                        previousText = fullSoFar;
                    }

                    if (!string.IsNullOrEmpty(delta))
                    {
                        chunkQueue.Enqueue(delta);
                    }
                },
                () => { isCompleted = true; },
                addToHistory: false);

            try
            {
                while (!isCompleted || !chunkQueue.IsEmpty)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (chunkQueue.TryDequeue(out string chunk))
                    {
                        if (!string.IsNullOrEmpty(chunk))
                        {
                            yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, chunk);
                        }
                    }
                    else
                    {
                        await Task.Delay(10, cancellationToken);
                    }
                }
            }
            finally
            {
                // Для диагностики: если стрим отменён, сохраняем, сколько успели получить.
                _ = previousText;
            }

            // Слив остаточной очереди (на случай гонки между isCompleted и Enqueue).
            while (chunkQueue.TryDequeue(out string remaining))
            {
                if (!string.IsNullOrEmpty(remaining))
                {
                    yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, remaining);
                }
            }
        }

        public object? GetService(Type serviceType, object? key)
        {
            return null;
        }

        public void Dispose()
        {
        }
    }
}
#endif