using System;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Chat;

namespace CoreAI.Tests.EditMode
{
    /// <summary>Compiles the legacy chat example published in the multiplayer integration guide.</summary>
    public static class LegacyChatExample
    {
        public static Task<LlmCompletionResult> SendLegacyChatAsync(
            IInGameLlmChatServiceFactory chatFactory,
            ActorContext admittedActor,
            string message,
            bool clearHistory,
            CancellationToken connectionCancellationToken)
        {
            connectionCancellationToken.ThrowIfCancellationRequested();
            IInGameLlmChatService service = chatFactory.Resolve(admittedActor);
            if (clearHistory)
            {
                service.ClearHistory();
            }

            return service.SendPlayerMessageAsync(message, connectionCancellationToken);
        }
    }

    /// <summary>Compiles the actor-aware chat example published in the multiplayer integration guide.</summary>
    public static class ActorOrchestrationChatExample
    {
        public static async Task<(string Answer, ChatMessage[] History)> SendOrchestratedChatAsync(
            CoreAiChatService chat,
            ActorContext admittedActor,
            IAgentMemoryScopeProvider hostPartitions,
            string roleId,
            string message,
            bool clearChatHistory,
            bool clearLongTermMemory,
            CancellationToken connectionCancellationToken)
        {
            connectionCancellationToken.ThrowIfCancellationRequested();
            if (hostPartitions == null)
            {
                throw new ArgumentNullException(nameof(hostPartitions));
            }

            ActorContext boundActor = AgentMemoryActorScope.Bind(admittedActor, hostPartitions, roleId);
            if (clearChatHistory || clearLongTermMemory)
            {
                chat.ClearContext(roleId, clearChatHistory, clearLongTermMemory, boundActor);
            }

            AiTaskRequest request = new AiTaskRequest
            {
                RoleId = roleId,
                Hint = message,
                ActorContext = boundActor,
                TraceId = Guid.NewGuid().ToString("N"),
                CancellationScope = "puzzle-view"
            };

            using (CancellationTokenRegistration stopRegistration = connectionCancellationToken.Register(
                       () => chat.StopAgent(roleId, boundActor)))
            {
                string answer = await chat.SendMessageAsync(request, connectionCancellationToken);
                chat.TryGetPersistedChatHistory(roleId, out ChatMessage[] history, 40, boundActor);
                return (answer, history);
            }
        }
    }
}
