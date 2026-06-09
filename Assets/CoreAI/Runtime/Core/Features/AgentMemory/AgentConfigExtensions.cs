using System;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Logging;

namespace CoreAI.Ai
{
    /// <summary>
    /// Convenience methods that run an <see cref="AgentConfig"/> through the global
    /// <see cref="CoreAIAgent"/> facade or an explicitly supplied orchestrator.
    /// </summary>
    public static class AgentConfigExtensions
    {
        /// <summary>
        /// Sends a single prompt through the global orchestrator using this agent's role id.
        /// </summary>
        /// <param name="config">Agent configuration produced by <see cref="AgentBuilder.Build"/>.</param>
        /// <param name="message">User or gameplay prompt to send to the agent.</param>
        /// <param name="priority">Queue priority forwarded to <see cref="AiTaskRequest.Priority"/>.</param>
        /// <param name="cancellationToken">Cancellation token for the orchestration call.</param>
        /// <example>
        /// await merchant.AskAsync("Show me your swords");
        /// </example>
        public static Task<string> AskAsync(
            this AgentConfig config,
            string message,
            int priority = 0,
            CancellationToken cancellationToken = default)
        {
            return AskAsync(config, CoreAIAgent.Orchestrator, message, priority, cancellationToken);
        }

        /// <summary>
        /// Sends a single prompt through the supplied orchestrator using this agent's role id.
        /// </summary>
        public static Task<string> AskAsync(
            this AgentConfig config,
            IAiOrchestrationService orchestrator,
            string message,
            int priority = 0,
            CancellationToken cancellationToken = default)
        {
            if (orchestrator == null)
            {
                throw new InvalidOperationException(
                    "Orchestrator is null. Make sure CoreAILifetimeScope is initialized or pass orchestrator explicitly.");
            }

            ValidateRoleRegistered(config);

            return orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = config.RoleId,
                Hint = message,
                Priority = priority,
                CancellationScope =
                    config.RoleId // Automatically cancels previous in-flight call for same role, if still generating.
            }, cancellationToken);
        }

        private static void ValidateRoleRegistered(AgentConfig config)
        {
            if (string.IsNullOrWhiteSpace(config?.RoleId))
            {
                throw new InvalidOperationException("Role id is missing. Provide RoleId in AgentBuilder.");
            }

            AgentMemoryPolicy policy = CoreAIAgent.Policy;
            if (policy == null || !policy.HasRole(config.RoleId))
            {
                throw new InvalidOperationException(
                    $"Role '{config.RoleId}' is not registered in CoreAIAgent.Policy. Call config.ApplyToPolicy(CoreAIAgent.Policy) (or Use BuildDetached() + explicit ApplyToPolicy()).");
            }
        }

        /// <summary>
        /// Starts a fire-and-forget prompt through the global orchestrator and invokes
        /// <paramref name="onDone"/> with the final response.
        /// </summary>
        /// <param name="config">Agent configuration produced by <see cref="AgentBuilder.Build"/>.</param>
        /// <param name="message">User or gameplay prompt to send to the agent.</param>
        /// <param name="onDone">Optional callback invoked after a successful response.</param>
        /// <param name="priority">Queue priority forwarded to <see cref="AiTaskRequest.Priority"/>.</param>
        public static void Ask(
            this AgentConfig config,
            string message,
            Action<string> onDone = null,
            int priority = 0)
        {
            _ = RunAskFireAndForgetAsync(config, message, onDone, priority);
        }

        private static async Task RunAskFireAndForgetAsync(
            AgentConfig config,
            string message,
            Action<string> onDone,
            int priority)
        {
            try
            {
                string result = await AskAsync(config, message, priority).ConfigureAwait(false);
                onDone?.Invoke(result);
            }
            catch (Exception ex)
            {
                Log.Instance.Error($"Ask() failed for agent '{config.RoleId}': {ex.Message}", LogTag.Llm);
            }
        }

        /// <summary>
        /// Clears persisted chat history for this agent role when a memory store is registered.
        /// </summary>
        public static void ClearMemory(this AgentConfig config)
        {
            if (CoreAIAgent.MemoryStore != null)
            {
                CoreAIAgent.MemoryStore.ClearChatHistory(config.RoleId);
            }
            else
            {
                Log.Instance.Warn("Cannot clear memory: CoreAIAgent.MemoryStore is not initialized.",
                    LogTag.Memory);
            }
        }
    }
}
