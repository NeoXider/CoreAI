#if !COREAI_NO_MEAI
#pragma warning disable CS8600, CS8602, CS8603,  CS8625
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;

namespace CoreAI.Ai
{
    /// <summary>
    /// Декоратор ILlmClient, который использует MEAI-style tool injection.
    /// Добавляет описание tools в system prompt автоматически.
    /// Работает с любым LLM бэкендом (OpenAI HTTP, LLMUnity, и т.д.).
    /// </summary>
    public sealed class MeaiToolsLlmClientDecorator : ILlmClient
    {
        private readonly ILlmClient _innerClient;
        private IReadOnlyList<ILlmTool> _tools;

        public MeaiToolsLlmClientDecorator(ILlmClient innerClient)
        {
            _innerClient = innerClient ?? throw new ArgumentNullException(nameof(innerClient));
            _tools = Array.Empty<ILlmTool>();
        }

        public void SetTools(IReadOnlyList<ILlmTool> tools)
        {
            _tools = tools ?? Array.Empty<ILlmTool>();
        }

        public async Task<LlmCompletionResult> CompleteAsync(
            LlmCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            // Inject tools into system prompt (MEAI style)
            string systemWithTools = InjectToolsIntoPrompt(request.SystemPrompt, request.Tools ?? _tools);
            
            var modifiedRequest = new LlmCompletionRequest
            {
                AgentRoleId = request.AgentRoleId,
                SystemPrompt = systemWithTools,
                UserPayload = request.UserPayload,
                TraceId = request.TraceId,
                RoutingProfileId = request.RoutingProfileId,
                ContextWindowTokens = request.ContextWindowTokens,
                MaxOutputTokens = request.MaxOutputTokens,
                Tools = null // Tools already injected into prompt
            };

            return await _innerClient.CompleteAsync(modifiedRequest, cancellationToken);
        }

        private static string InjectToolsIntoPrompt(string basePrompt, IReadOnlyList<ILlmTool> tools)
        {
            if (tools == null || tools.Count == 0)
            {
                return basePrompt ?? "";
            }

            var toolDefs = new List<string>();
            foreach (var tool in tools)
            {
                string paramsJson = tool.ParametersSchema;
                if (string.IsNullOrEmpty(paramsJson) || paramsJson == "{}")
                {
                    paramsJson = "{}";
                }
                
                toolDefs.Add($"<tool=name>{tool.Name}</tool>\n<tool=description>{tool.Description}</tool>\n<tool=parameters>{paramsJson}</tool>");
            }

            string toolsSection = 
                "\n\n<tools>\n" + string.Join("\n\n", toolDefs) + 
                "\n</tools>\n\n" +
                "You can call a tool by outputting <tool_call>name:TOOL_NAME,arguments:JSON_ARGS</tool_call>\n" +
                "Example: <tool_call>name:memory,arguments:{\"content\":\"my note\"}</tool_call>\n" +
                "If you need to save info to memory, call memory tool BEFORE your final answer.";

            return (basePrompt ?? "") + toolsSection;
        }
    }
}
#pragma warning restore CS8600, CS8602, CS8603, CS8625
#endif