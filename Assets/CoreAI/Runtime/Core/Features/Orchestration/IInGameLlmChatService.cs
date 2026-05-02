using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Ai
{
    /// <summary>
    /// Диалог с игроком (как с GPT): история в памяти, системный промпт из провайдера для роли <see cref="BuiltInAgentRoleIds.SmartChat"/>.
    /// </summary>
    public interface IInGameLlmChatService
    {
        /// <summary>Отправить реплику игрока в чат с моделью (роль <see cref="BuiltInAgentRoleIds.SmartChat"/>).</summary>
        Task<LlmCompletionResult> SendPlayerMessageAsync(string message, CancellationToken cancellationToken = default);

        /// <summary>Сбросить историю диалога в памяти сервиса.</summary>
        void ClearHistory();

        /// <summary>Число пар user/assistant в истории (для UI).</summary>
        int HistoryPairCount { get; }
    }
}