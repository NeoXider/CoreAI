using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Ai
{
    public sealed class LlmCompletionRequest
    {
        public string AgentRoleId { get; set; } = "";
        public string SystemPrompt { get; set; } = "";
        public string UserPayload { get; set; } = "";

        /// <summary>Сквозной id для логов (оркестратор / декоратор LLM / роутер команд).</summary>
        public string TraceId { get; set; } = "";
    }

    public sealed class LlmCompletionResult
    {
        public bool Ok { get; set; }
        public string Content { get; set; } = "";
        public string Error { get; set; } = "";

        /// <summary>Заполняется OpenAI-compatible HTTP при наличии <c>usage</c> в JSON.</summary>
        public int? PromptTokens { get; set; }

        public int? CompletionTokens { get; set; }
        public int? TotalTokens { get; set; }
    }

    /// <summary>
    /// Абстракция вызова модели (DGF_SPEC §5.2, §7). Реализации — в Core (stub) и Unity-слое (LLMUnity, OpenAI-compatible HTTP).
    /// </summary>
    public interface ILlmClient
    {
        Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request, CancellationToken cancellationToken = default);
    }
}
