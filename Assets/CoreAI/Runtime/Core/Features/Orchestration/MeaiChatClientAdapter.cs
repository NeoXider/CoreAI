#if !COREAI_NO_MEAI
#pragma warning disable CS8600, CS8602, CS8603, CS8625
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MEAI = Microsoft.Extensions.AI;

namespace CoreAI.Ai
{
    /// <summary>
    /// Адаптер нашего <see cref="ILlmClient"/> к интерфейсу <see cref="MEAI.IChatClient"/> от Microsoft.Extensions.AI.
    /// Позволяет использовать MEAI function calling поверх любого LLM-бэкенда.
    /// </summary>
    public sealed class MeaiChatClientAdapter : MEAI.IChatClient
    {
        private readonly ILlmClient _innerClient;
        private readonly List<MEAI.AIFunction> _tools = new();

        public MeaiChatClientAdapter(ILlmClient innerClient)
        {
            _innerClient = innerClient ?? throw new ArgumentNullException(nameof(innerClient));
        }

        public void RegisterTool(MEAI.AIFunction tool)
        {
            if (tool == null) throw new ArgumentNullException(nameof(tool));
            _tools.Add(tool);
        }

        public IReadOnlyList<MEAI.AIFunction> GetTools() => _tools.AsReadOnly();

        async Task<MEAI.ChatResponse> MEAI.IChatClient.GetResponseAsync(
            IEnumerable<MEAI.ChatMessage> chatMessages,
            MEAI.ChatOptions options,
            CancellationToken cancellationToken)
        {
            if (chatMessages == null) throw new ArgumentNullException(nameof(chatMessages));

            var messagesList = chatMessages.ToList();
            string systemMessage = "";
            string userMessage = "";

            foreach (var msg in messagesList)
            {
                if (msg.Role == MEAI.ChatRole.System)
                {
                    systemMessage += string.Join("", msg.Contents.OfType<MEAI.TextContent>()) + "\n";
                }
                else if (msg.Role == MEAI.ChatRole.User)
                {
                    userMessage += string.Join("", msg.Contents.OfType<MEAI.TextContent>()) + "\n";
                }
            }

            LlmCompletionRequest request = new()
            {
                AgentRoleId = options?.ModelId ?? "unknown",
                SystemPrompt = systemMessage.Trim(),
                UserPayload = userMessage.Trim(),
                TraceId = options?.ConversationId ?? ""
            };

            if (_tools.Count > 0)
            {
                request.SystemPrompt += "\n\n## Available Tools\n" +
                    "You have access to the following tools. When you need to use a tool, output ONLY a JSON object:\n" +
                    "{\"tool\": \"tool_name\", \"arguments\": {...}}\n" +
                    "Do not output anything else when calling a tool.";
            }

            LlmCompletionResult result =
                await _innerClient.CompleteAsync(request, cancellationToken).ConfigureAwait(false);

            if (result == null || !result.Ok)
            {
                throw new InvalidOperationException(result?.Error ?? "LLM request failed");
            }

            var responseMessage = new MEAI.ChatMessage(
                MEAI.ChatRole.Assistant,
                result.Content);

            return new MEAI.ChatResponse(responseMessage)
            {
                ModelId = options?.ModelId,
                FinishReason = MEAI.ChatFinishReason.Stop
            };
        }

        async IAsyncEnumerable<MEAI.ChatResponseUpdate> MEAI.IChatClient.GetStreamingResponseAsync(
            IEnumerable<MEAI.ChatMessage> chatMessages,
            MEAI.ChatOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var response = await ((MEAI.IChatClient)this).GetResponseAsync(chatMessages, options, cancellationToken);
            foreach (var message in response.Messages)
            {
                yield return new MEAI.ChatResponseUpdate
                {
                    Role = message.Role,
                    Contents = message.Contents
                };
            }
        }

        public void Dispose()
        {
        }

        object MEAI.IChatClient.GetService(Type serviceType, object key)
        {
            if (serviceType == typeof(ILlmClient))
            {
                return _innerClient;
            }
            return null;
        }
    }
}
#pragma warning restore CS8600, CS8602, CS8603, CS8625
#endif
