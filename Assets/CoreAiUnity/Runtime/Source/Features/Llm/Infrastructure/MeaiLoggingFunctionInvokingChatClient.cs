#if !COREAI_NO_LLM
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Infrastructure.Logging;
using MEAI = Microsoft.Extensions.AI;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Обёртка над FunctionInvokingChatClient для логирования tool call шагов.
    /// </summary>
    public sealed class MeaiLoggingFunctionInvokingChatClient : MEAI.FunctionInvokingChatClient
    {
        private readonly IGameLogger _logger;
        private int _iterationCount;

        public MeaiLoggingFunctionInvokingChatClient(MEAI.IChatClient innerClient, IGameLogger logger)
            : base(innerClient)
        {
            _logger = logger;
            _iterationCount = 0;
        }

        public override async Task<MEAI.ChatResponse> GetResponseAsync(
            IEnumerable<MEAI.ChatMessage> chatMessages,
            MEAI.ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            _iterationCount++;
            int iteration = _iterationCount;
            var messagesList = chatMessages.ToList();

            if (CoreAISettings.LogMeaiToolCallingSteps)
            {
                _logger.LogInfo(GameLogFeature.Llm, $"[MEAI Tool Call] Iteration {iteration}: Starting with {messagesList?.Count ?? 0} messages");

                // Логируем последние сообщения (новые с предыдущей итерации)
                if (messagesList != null && messagesList.Count > 2)
                {
                    var recentMessages = messagesList.Skip(System.Math.Max(0, messagesList.Count - 3)).ToList();
                    foreach (var msg in recentMessages)
                    {
                        var contents = msg.Contents?.Select(c => c.GetType().Name).ToArray() ?? new[] { "empty" };
                        _logger.LogInfo(GameLogFeature.Llm, $"[MEAI Tool Call]   Message role={msg.Role}, contents=[{string.Join(", ", contents)}]");
                    }
                }
            }

            var response = await base.GetResponseAsync(messagesList, options, cancellationToken);

            if (CoreAISettings.LogMeaiToolCallingSteps && response != null)
            {
                var toolCalls = response.Messages?.SelectMany(m => m.Contents?.OfType<MEAI.FunctionCallContent>() ?? Enumerable.Empty<MEAI.FunctionCallContent>()).ToList();
                var toolResults = response.Messages?.SelectMany(m => m.Contents?.OfType<MEAI.FunctionResultContent>() ?? Enumerable.Empty<MEAI.FunctionResultContent>()).ToList();

                if (toolCalls?.Count > 0)
                {
                    _logger.LogInfo(GameLogFeature.Llm, $"[MEAI Tool Call] Iteration {iteration}: Model requested {toolCalls.Count} tool call(s)");
                }
                if (toolResults?.Count > 0)
                {
                    _logger.LogInfo(GameLogFeature.Llm, $"[MEAI Tool Call] Iteration {iteration}: {toolResults.Count} tool result(s) in response");
                }

                if (response.FinishReason == MEAI.ChatFinishReason.Stop)
                {
                    _logger.LogInfo(GameLogFeature.Llm, $"[MEAI Tool Call] Iteration {iteration}: Finished with Stop (text response)");
                }
                else if (response.FinishReason == MEAI.ChatFinishReason.ToolCalls)
                {
                    _logger.LogInfo(GameLogFeature.Llm, $"[MEAI Tool Call] Iteration {iteration}: Finished with ToolCalls (more iterations needed)");
                }
            }

            _iterationCount = 0; // Сброс для следующего вызова
            return response;
        }
    }
}
#endif
