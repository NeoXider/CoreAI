using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Authority;
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
        /// The first ask for a role waits for asynchronous skill hydration; a ready role uses the
        /// cheap existing readiness path with no storage reread.
        /// </summary>
        public static Task<string> AskAsync(
            this AgentConfig config,
            IAiOrchestrationService orchestrator,
            string message,
            int priority = 0,
            CancellationToken cancellationToken = default)
        {
            AgentMemoryPolicy policy = ResolvePolicyForAsk(config);
            if (policy.HasRole(config.RoleId))
            {
                return RunAskAsync(config, orchestrator, message, priority, cancellationToken);
            }

            return EnsureRegisteredAndAskAsync(config, orchestrator, message, priority,
                cancellationToken, policy);
        }

        private static AgentMemoryPolicy ResolvePolicyForAsk(AgentConfig config)
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

            return policy;
        }

        private static Task<string> RunAskAsync(
            AgentConfig config,
            IAiOrchestrationService orchestrator,
            string message,
            int priority,
            CancellationToken cancellationToken)
        {
            if (orchestrator == null)
            {
                throw new InvalidOperationException(
                    "Orchestrator is null. Make sure CoreAILifetimeScope is initialized or pass orchestrator explicitly.");
            }

            IActorIdentityProvider actorIdentityProvider = CoreAIAgent.ActorIdentityProvider;
            if (actorIdentityProvider == null)
            {
                throw new InvalidOperationException(
                    "Actor identity provider is null. Initialize CoreAI before asking an agent.");
            }

            ActorContext actorContext = actorIdentityProvider.GetActorContext(config.RoleId);

            return orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = config.RoleId,
                RoutingProfileId = config.LlmProfileId ?? "",
                Hint = message,
                Priority = priority,
                ActorContext = actorContext,
                CancellationScope = actorContext.SessionId
            }, cancellationToken);
        }

        private static async Task<string> EnsureRegisteredAndAskAsync(
            AgentConfig config,
            IAiOrchestrationService orchestrator,
            string message,
            int priority,
            CancellationToken cancellationToken,
            AgentMemoryPolicy policy)
        {
            // WHY: The first ask for a role registers the built config, so `Build()` + `Ask*()` just
            // works without an explicit `ApplyToPolicy(CoreAIAgent.Policy)` call. Registration hydrates
            // asynchronously before dispatch; concurrent first asks share one registration.
            await AgentRoleRegistration.EnsureReadyAsync(policy, config, cancellationToken);
            return await RunAskAsync(config, orchestrator, message, priority, cancellationToken);
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
            // WHY: Capture the caller's synchronization context (Unity main thread when called from it)
            // so onDone is safe to use with Unity APIs even when the awaited work resumed elsewhere.
            // The awaits inside no longer detach from the context themselves — a detached continuation
            // is not merely "on another thread" in a WebGL player, it is a continuation that never runs.
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
                string result = await AskAsync(config, message, priority);
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

    /// <summary>
    /// Per-policy/per-role asynchronous registration gate. Concurrent first registrations share one
    /// hydration; an explicit apply performs a serialized replacement. Lifetime is bounded: the
    /// table dies with the policy, and per-role slots are cleared when their registration settles.
    /// A failed or canceled registration stays retryable and never marks the role ready.
    /// </summary>
    internal static class AgentRoleRegistration
    {
        private sealed class PolicySlots
        {
            public readonly object Sync = new();
            public readonly Dictionary<string, Task> Active = new(StringComparer.Ordinal);
        }

        private static readonly ConditionalWeakTable<AgentMemoryPolicy, PolicySlots> Tables = new();

        public static Task EnsureReadyAsync(AgentMemoryPolicy policy, AgentConfig config,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return policy.HasRole(config.RoleId)
                ? Task.CompletedTask
                : EnsureReadySlowAsync(policy, config, cancellationToken);
        }

        public static async Task ApplyReplacementAsync(AgentMemoryPolicy policy, AgentConfig config,
            CancellationToken cancellationToken)
        {
            string roleId = config.RoleId;
            if (string.IsNullOrWhiteSpace(roleId))
            {
                throw new InvalidOperationException("Role id is missing. Provide RoleId in AgentBuilder.");
            }

            roleId = roleId.Trim();
            cancellationToken.ThrowIfCancellationRequested();
            PolicySlots table = Tables.GetValue(policy, _ => new PolicySlots());
            while (true)
            {
                TaskCompletionSource<bool> owned = null;
                Task prior;
                lock (table.Sync)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!table.Active.TryGetValue(roleId, out prior))
                    {
                        owned = new TaskCompletionSource<bool>(
                            TaskCreationOptions.RunContinuationsAsynchronously);
                        table.Active.Add(roleId, owned.Task);
                    }
                }

                if (owned != null)
                {
                    // WHY: Once ownership is published, all exits must settle and remove it,
                    // including cancellation between acquisition and preparation.
                    await PublishOwnedAsync(table, roleId, owned, config, policy, cancellationToken);
                    await owned.Task;
                    return;
                }

                try
                {
                    await AwaitSharedAsync(prior, cancellationToken);
                }
                catch (Exception)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // WHY: An explicit replacement may recover after the previous owner failed.
                }
            }
        }

        private static Task EnsureReadySlowAsync(AgentMemoryPolicy policy, AgentConfig config,
            CancellationToken cancellationToken)
        {
            string roleId = config.RoleId.Trim();
            PolicySlots table = Tables.GetValue(policy, _ => new PolicySlots());
            TaskCompletionSource<bool> owned = null;
            Task shared;
            lock (table.Sync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (policy.HasRole(roleId))
                {
                    return Task.CompletedTask;
                }

                if (!table.Active.TryGetValue(roleId, out shared))
                {
                    owned = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    shared = owned.Task;
                    table.Active.Add(roleId, shared);
                }
            }

            if (owned != null)
            {
                // WHY: Shared hydration outlives any individual waiter, including its initiator.
                // This runner catches all failures; the shared completion exposes them to waiters.
                _ = PublishOwnedAsync(table, roleId, owned, config, policy, CancellationToken.None);
            }

            return AwaitSharedAsync(shared, cancellationToken);
        }

        private static async Task PublishOwnedAsync(PolicySlots table, string roleId,
            TaskCompletionSource<bool> owned, AgentConfig config, AgentMemoryPolicy policy,
            CancellationToken cancellationToken)
        {
            Exception failure = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await config.PublishPreparedAsync(policy, cancellationToken);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                lock (table.Sync)
                {
                    // WHY: Remove and settle in one critical section. No waiter can acquire a
                    // detached slot, and completed role ids consume no registration-table entries.
                    if (table.Active.TryGetValue(roleId, out Task current) &&
                        ReferenceEquals(current, owned.Task))
                    {
                        table.Active.Remove(roleId);
                    }

                    if (failure is OperationCanceledException canceled)
                    {
                        owned.TrySetCanceled(canceled.CancellationToken);
                    }
                    else if (failure != null)
                    {
                        owned.TrySetException(failure);
                        // WHY: All waiters may have canceled; observe without consuming the
                        // failure, which still propagates to every remaining shared waiter.
                        _ = owned.Task.Exception;
                    }
                    else
                    {
                        owned.TrySetResult(true);
                    }
                }
            }
        }

        private static async Task AwaitSharedAsync(Task shared, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (shared.IsCompleted || !cancellationToken.CanBeCanceled)
            {
                await shared;
                return;
            }

            // WHY not Task.WhenAny(shared, canceled.Task): `shared` deliberately completes with
            // RunContinuationsAsynchronously (it is settled UNDER the role table lock, see
            // PublishOwnedAsync), and WhenAny's internal continuation captures no SynchronizationContext.
            // In a WebGL player that continuation is handed to a thread pool that does not exist, so
            // waiting for a role to become ready - and with it the preparation of its skills - hangs
            // silently and forever. Here the continuation is scheduled on the host context explicitly,
            // and what we await is a plain promise without the inline ban, i.e. a normal
            // context-capturing await.
            TaskScheduler continuationScheduler = SynchronizationContext.Current != null
                ? TaskScheduler.FromCurrentSynchronizationContext()
                : TaskScheduler.Default;
            TaskCompletionSource<bool> settled = new();
            using (cancellationToken.Register(
                state => ((TaskCompletionSource<bool>)state).TrySetResult(true), settled, false))
            {
                _ = shared.ContinueWith(
                    (_, state) => ((TaskCompletionSource<bool>)state).TrySetResult(true),
                    settled, CancellationToken.None, TaskContinuationOptions.None, continuationScheduler);
                await settled.Task;
                cancellationToken.ThrowIfCancellationRequested();
                await shared;
            }
        }
    }
}
