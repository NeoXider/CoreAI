#if !COREAI_NO_MEAI
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace CoreAI.Ai
{
    public sealed class MeaiChatClientAdapter : IChatClient
    {
        private readonly ILlmClient _innerClient;
        private readonly List<AIFunction> _tools = new();

        public MeaiChatClientAdapter(ILlmClient innerClient)
        {
            _innerClient = innerClient ?? throw new ArgumentNullException(nameof(innerClient));
        }

        public void RegisterTool(AIFunction tool)
        {
            if (tool == null)
            {
                throw new ArgumentNullException(nameof(tool));
            }

            _tools.Add(tool);
        }

        public IReadOnlyList<AIFunction> GetTools()
        {
            return _tools.AsReadOnly();
        }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (chatMessages == null)
            {
                throw new ArgumentNullException(nameof(chatMessages));
            }

            List<ChatMessage> messagesList = new(chatMessages);
            string systemMessage = "";
            string userMessage = "";

            foreach (ChatMessage msg in messagesList)
            {
                if (msg.Role == ChatRole.System)
                {
                    systemMessage += msg.Text + "\n";
                }
                else if (msg.Role == ChatRole.User)
                {
                    userMessage += msg.Text + "\n";
                }
            }

            LlmCompletionRequest request = new()
            {
                AgentRoleId = options?.ModelId ?? "unknown",
                SystemPrompt = systemMessage.Trim(),
                UserPayload = userMessage.Trim(),
                TraceId = options?.ConversationId ?? ""
            };

            if (options?.Tools != null && options.Tools.Count > 0)
            {
                request.SystemPrompt += "\n\n## Available Tools\n" +
                                        "You have access to tools. When you need to use a tool, output ONLY a JSON object:\n" +
                                        "{\"tool\": \"tool_name\", \"action\": \"action_name\", \"content\": \"data\"}\n" +
                                        "Do not output anything else when using tools.";
            }

            LlmCompletionResult result =
                await _innerClient.CompleteAsync(request, cancellationToken).ConfigureAwait(false);

            if (result == null || !result.Ok)
            {
                throw new InvalidOperationException(result?.Error ?? "LLM request failed");
            }

            ChatMessage responseMessage = new(ChatRole.Assistant, result.Content);
            ChatResponse response = new(responseMessage)
            {
                ModelId = options?.ModelId,
                FinishReason = ChatFinishReason.Stop
            };
            return response;
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ChatResponse response = await GetResponseAsync(chatMessages, options, cancellationToken);
            foreach (ChatMessage message in response.Messages)
            {
                yield return new ChatResponseUpdate
                {
                    Role = message.Role,
                    Contents = message.Contents
                };
            }
        }

        public void Dispose()
        {
            if (_innerClient is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        public object? GetService(Type serviceKey, object? key = null)
        {
            if (serviceKey == typeof(ILlmClient))
            {
                return _innerClient;
            }

            return null;
        }
    }
}
#endif