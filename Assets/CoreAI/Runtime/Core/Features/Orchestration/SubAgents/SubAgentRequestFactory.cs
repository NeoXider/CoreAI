using System;
using System.Collections.Generic;
using CoreAI.Authority;

namespace CoreAI.Ai.SubAgents
{
    /// <summary>
    /// What a plan inherits from the lead turn that started it: who the player is (actor and session), the
    /// lead's trace and cancellation scope, and its queue priority. Nothing of the lead's conversation is here,
    /// so nothing of it can reach a sub-agent.
    /// </summary>
    public sealed class SubAgentParentContext
    {
        /// <summary>Creates the parent context of a plan.</summary>
        /// <param name="leadRoleId">Role the lead turn runs under.</param>
        /// <param name="leadTraceId">Trace id of the lead turn; sub-agent trace ids are derived from it.</param>
        /// <param name="leadCancellationScope">Cancellation scope of the lead turn; never reused by a sub-agent.</param>
        /// <param name="actorContext">Trusted actor of the lead turn; sub-agents run as the same actor.</param>
        /// <param name="priority">Queue priority of the lead turn.</param>
        public SubAgentParentContext(
            string leadRoleId,
            string leadTraceId,
            string leadCancellationScope = "",
            ActorContext? actorContext = null,
            int priority = 0)
        {
            LeadRoleId = leadRoleId?.Trim() ?? "";
            LeadTraceId = leadTraceId?.Trim() ?? "";
            LeadCancellationScope = leadCancellationScope?.Trim() ?? "";
            ActorContext = actorContext;
            Priority = priority;
        }

        /// <summary>Role the lead turn runs under.</summary>
        public string LeadRoleId { get; }

        /// <summary>Trace id of the lead turn.</summary>
        public string LeadTraceId { get; }

        /// <summary>Cancellation scope of the lead turn.</summary>
        public string LeadCancellationScope { get; }

        /// <summary>Trusted actor of the lead turn, or <c>null</c> for an actorless host.</summary>
        public ActorContext? ActorContext { get; }

        /// <summary>Queue priority of the lead turn.</summary>
        public int Priority { get; }
    }

    /// <summary>
    /// Builds the request of one sub-agent turn. The request is a fresh conversation: its user message is the
    /// lead's task plus the context the lead chose to pass, and nothing else of the lead's turn (no history,
    /// no attachments, no system prompt override, not the plan goal, not a sibling's result).
    /// </summary>
    public static class SubAgentRequestFactory
    {
        /// <summary><see cref="AiTaskRequest.SourceTag"/> of every sub-agent turn.</summary>
        public const string SourceTag = "SubAgent";

        /// <summary>Heading that separates the lead's context from the task in the sub-agent's user message.</summary>
        public const string ContextHeading = "Context from the lead:";

        /// <summary>True when <paramref name="sourceTag"/> marks a sub-agent turn (also a derived tag such as <c>SubAgent:lua_repair</c>).</summary>
        public static bool IsSubAgentSourceTag(string sourceTag)
        {
            if (string.IsNullOrWhiteSpace(sourceTag))
            {
                return false;
            }

            string trimmed = sourceTag.Trim();
            return string.Equals(trimmed, SourceTag, StringComparison.Ordinal) ||
                   trimmed.StartsWith(SourceTag + ":", StringComparison.Ordinal);
        }

        /// <summary>The user message a sub-agent receives: the task, then the lead's context when there is one.</summary>
        public static string ComposeHint(string task, string context)
        {
            string trimmedTask = task?.Trim() ?? "";
            string trimmedContext = context?.Trim() ?? "";
            if (trimmedContext.Length == 0)
            {
                return trimmedTask;
            }

            return trimmedTask + "\n\n" + ContextHeading + "\n" + trimmedContext;
        }

        /// <summary>Builds the isolated request for one run of <paramref name="definition"/>.</summary>
        /// <param name="definition">The sub-agent to run.</param>
        /// <param name="task">The lead's task text.</param>
        /// <param name="context">The context the lead passed (already clipped by the caller).</param>
        /// <param name="parent">What the plan inherits from the lead turn.</param>
        /// <param name="planId">Id of the plan the run belongs to.</param>
        /// <param name="taskId">Plan task id.</param>
        /// <param name="attempt">1-based attempt of that task, so a retried task gets a new trace and scope.</param>
        /// <param name="delegateToolName">Delegate tool name; removed from the allowlist again as a second guard.</param>
        /// <param name="guardedToolNames">
        /// Further tool names the sub-agent may never call (the configured Team-style tool names); removed from
        /// the allowlist like the delegate tool.
        /// </param>
        public static AiTaskRequest Create(
            SubAgentDefinition definition,
            string task,
            string context,
            SubAgentParentContext parent,
            string planId,
            string taskId,
            int attempt,
            string delegateToolName = DelegateTaskTool.DefaultName,
            IEnumerable<string> guardedToolNames = null)
        {
            if (definition == null)
            {
                throw new ArgumentNullException(nameof(definition));
            }

            if (parent == null)
            {
                throw new ArgumentNullException(nameof(parent));
            }

            string runKey = (taskId ?? "") + "." + (attempt < 1 ? 1 : attempt);
            string traceId = parent.LeadTraceId.Length > 0
                ? parent.LeadTraceId + ".sub." + runKey
                : "sub." + (planId ?? "") + "." + runKey;
            string scopeRoot = parent.LeadCancellationScope.Length > 0 ? parent.LeadCancellationScope : "orchestrator";

            return new AiTaskRequest
            {
                RoleId = definition.RoleId,
                RoutingProfileId = definition.RoutingProfileId,
                Hint = ComposeHint(task, context),
                RequestSystemInstructions = definition.Instructions,
                TraceId = traceId,
                Priority = parent.Priority,
                SourceTag = SourceTag,
                // WHY both modes: a clean context means the role's stored conversation is neither read nor
                // extended (the role may be the lead's own), and its durable memory stays out unless the
                // definition opts in.
                HistoryMode = AiTaskHistoryMode.None,
                MemoryMode = definition.UseRoleMemory ? AiTaskMemoryMode.RoleDefault : AiTaskMemoryMode.None,
                ActorContext = parent.ActorContext,
                // WHY a scope per run: the queue treats a scope as latest-wins, so a sub-agent sharing the lead's
                // scope (or a sibling's) would cancel it on admission.
                CancellationScope = scopeRoot + "/" + (planId ?? "") + "/sub/" + runKey,
                AllowedToolNames = BuildAllowlist(definition, delegateToolName, guardedToolNames),
                MaxOutputTokens = definition.MaxOutputTokens,
                MaxToolCallRoundtrips = definition.MaxToolCallRoundtrips
            };
        }

        private static string[] BuildAllowlist(
            SubAgentDefinition definition,
            string delegateToolName,
            IEnumerable<string> guardedToolNames)
        {
            HashSet<string> guarded = new(StringComparer.OrdinalIgnoreCase)
            {
                DelegateTaskTool.DefaultName,
                string.IsNullOrWhiteSpace(delegateToolName) ? DelegateTaskTool.DefaultName : delegateToolName.Trim()
            };
            if (guardedToolNames != null)
            {
                foreach (string name in guardedToolNames)
                {
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        guarded.Add(name.Trim());
                    }
                }
            }

            List<string> names = new(definition.AllowedToolNames.Count);
            foreach (string name in definition.AllowedToolNames)
            {
                if (!guarded.Contains(name) && !SubAgentTeamToolNames.IsDefaultName(name))
                {
                    names.Add(name);
                }
            }

            // WHY never null: a null allowlist means "every tool of the role", which would include a delegate
            // tool a host registered on that role by mistake.
            return names.ToArray();
        }
    }
}
