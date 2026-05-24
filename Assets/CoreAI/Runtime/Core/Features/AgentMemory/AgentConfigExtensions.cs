using System;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Logging;

namespace CoreAI.Ai
{
    /// <summary>
    /// Provides agent config extensions functionality.
    /// </summary>
    public static class AgentConfigExtensions
    {
        /// <summary>
/// Executes AskAsync API operation.
        /// <para>See the implementation details for usage guidance.</para>
        /// </summary>
        /// <param name="config">The config value.</param>
        /// <param name="message">The message value.</param>
        /// <param name="priority">The priority value.</param>
        /// <param name="cancellationToken">The cancellation token value.</param>
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
/// Executes AskAsync API operation.
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

            return orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = config.RoleId,
                Hint = message,
                Priority = priority,
                CancellationScope =
                    config.RoleId // Automatically cancels previous in-flight call for same role, if still generating.
            }, cancellationToken);
        }

        /// <summary>
/// Executes Ask API operation.
        ///
        /// <para>See the implementation details for usage guidance.</para>
        /// </summary>
        /// <param name="config">The config value.</param>
        /// <param name="message">The message value.</param>
        /// <param name="onDone">The on done value.</param>
        /// <param name="priority">The priority value.</param>
        /// <example>
        /// Usage example:
        ///
        ///
        ///
        ///
        /// </example>
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
/// Executes ClearMemory API operation.
        ///
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
