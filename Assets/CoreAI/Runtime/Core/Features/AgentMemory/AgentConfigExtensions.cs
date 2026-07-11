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
            // Make sure the role is known to the global policy before we run. For the common newcomer
            // flow this AUTO-REGISTERS the built config, so `Build()` + `Ask*()` just works without an
            // explicit `ApplyToPolicy(CoreAIAgent.Policy)` call. Done before the orchestrator null-check
            // so a missing lifetime scope still surfaces its own clear message.
            EnsureRoleRegistered(config);

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

        private static void EnsureRoleRegistered(AgentConfig config)
        {
            if (string.IsNullOrWhiteSpace(config?.RoleId))
            {
                throw new InvalidOperationException("Role id is missing. Provide RoleId in AgentBuilder.");
            }

            AgentMemoryPolicy policy = CoreAIAgent.Policy;
            if (policy == null)
            {
                throw new InvalidOperationException(
                    $"Cannot run agent '{config.RoleId}': CoreAIAgent.Policy is null. Initialize CoreAI first " +
                    "(add CoreAILifetimeScope to the scene / run CoreAI setup) before asking an agent.");
            }

            // Convenience: register the built config with the global policy on first use, so newcomers
            // do not have to call ApplyToPolicy(CoreAIAgent.Policy) by hand. Re-applying is idempotent;
            // advanced users targeting a CUSTOM AgentMemoryPolicy still call ApplyToPolicy on that policy
            // themselves (and, once registered here, this no-ops).
            if (!policy.HasRole(config.RoleId))
            {
                config.ApplyToPolicy(policy);
            }
        }

        /// <summary>
        /// Convenience fire-and-forget prompt through the global orchestrator; invokes
        /// <paramref name="onDone"/> with the final response. The primary idiom is
        /// <see cref="AskAsync(AgentConfig, string, int, CancellationToken)"/> — use this overload
        /// only from callback-style call sites (UnityEvents, legacy code) where awaiting is awkward.
        /// The callback is marshaled to the caller's <see cref="SynchronizationContext"/> when one
        /// exists, for example the Unity main thread; when called from a thread without a
        /// <see cref="SynchronizationContext"/>, the callback may be invoked on a background thread,
        /// and the caller must not touch UnityEngine APIs in that case.
        /// Errors are logged, not thrown.
        /// </summary>
        /// <param name="config">Agent configuration produced by <see cref="AgentBuilder.Build"/>.</param>
        /// <param name="message">User or gameplay prompt to send to the agent.</param>
        /// <param name="onDone">Optional callback invoked after a successful response.</param>
        /// <param name="priority">Queue priority forwarded to <see cref="AiTaskRequest.Priority"/>.</param>
        public static void AskWithCallback(
            this AgentConfig config,
            string message,
            Action<string> onDone = null,
            int priority = 0)
        {
            // Capture the caller's synchronization context (Unity main thread when called from it)
            // so onDone is safe to use with Unity APIs; without it the continuation after
            // ConfigureAwait(false) may land on a thread-pool thread.
            _ = RunAskFireAndForgetAsync(config, message, onDone, priority, SynchronizationContext.Current);
        }

        /// <summary>Legacy alias of <see cref="AskWithCallback"/>.</summary>
        [Obsolete("Use AskAsync (primary, awaitable) or AskWithCallback (fire-and-forget convenience).")]
        public static void Ask(
            this AgentConfig config,
            string message,
            Action<string> onDone = null,
            int priority = 0)
        {
            AskWithCallback(config, message, onDone, priority);
        }

        private static async Task RunAskFireAndForgetAsync(
            AgentConfig config,
            string message,
            Action<string> onDone,
            int priority,
            SynchronizationContext callbackContext)
        {
            try
            {
                string result = await AskAsync(config, message, priority).ConfigureAwait(false);
                if (onDone == null)
                {
                    return;
                }

                if (callbackContext != null)
                {
                    callbackContext.Post(state => onDone((string)state), result);
                }
                else
                {
                    onDone(result);
                }
            }
            catch (Exception ex)
            {
                Log.Instance.Error($"AskWithCallback failed for agent '{config.RoleId}': {ex}", LogTag.Llm);
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