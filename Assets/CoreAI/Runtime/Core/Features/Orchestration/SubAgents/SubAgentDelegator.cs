using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Ai.SubAgents
{
    /// <summary>
    /// The body of <c>delegate_task</c>: runs one sub-agent for the session's running plan through an injected
    /// <see cref="IAiTaskResultService"/>, in a fresh conversation that carries only the lead's task and the
    /// context the lead passed, and turns the outcome into a typed <see cref="SubAgentDelegationResult"/>.
    /// <para>
    /// Order of a delegation: refuse what cannot run (no plan, nested call, unknown sub-agent, blank task, busy
    /// task, spent budget) without waiting; reserve one call; wait FIFO for a free slot (the plan's parallelism);
    /// run with a token linked to the plan scope, the lead's tool invocation and the definition's deadline;
    /// record the outcome on the plan; free the slot.
    /// </para>
    /// <para>
    /// Cancellation: when the lead's own invocation token fires, the body rethrows
    /// <see cref="OperationCanceledException"/> (the lead's turn is ending, as for any tool). When only the plan
    /// scope fires (Stop, completion, teardown) the lead receives a typed <c>plan_cancelled</c> result.
    /// </para>
    /// </summary>
    public sealed class SubAgentDelegator
    {
        private const string ClipMarker = "\n...[clipped {0} chars]";

        private readonly OrchestratorSession _session;
        private readonly ISubAgentRegistry _registry;
        private readonly IAiTaskResultService _runner;
        private readonly ILlmAsyncMarshaler _asyncMarshaler;

        // WHY an instance AsyncLocal: it flows from this body into the sub-agent turn it starts and into any
        // tool that turn invokes, so a sub-agent reaching this body by any route (a skill proxy, a host that
        // registered the tool on the sub-agent's role) is recognised and refused. Siblings started by the tool
        // policy run in their own execution contexts and do not see each other's mark.
        private readonly AsyncLocal<int> _depth = new();

        internal SubAgentDelegator(
            OrchestratorSession session,
            ISubAgentRegistry registry,
            IAiTaskResultService runner,
            ILlmAsyncMarshaler asyncMarshaler)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _runner = runner ?? throw new ArgumentNullException(nameof(runner));
            // WHY: the portable default delays on Task.Delay, which never fires in a WebGL player; a Unity host
            // passes its frame-driven marshaler so the deadline exists there.
            _asyncMarshaler = asyncMarshaler ?? PassThroughLlmAsyncMarshaler.Instance;
        }

        /// <summary>True while the current execution context is inside a sub-agent run started here.</summary>
        public bool IsInsideSubAgentRun => _depth.Value > 0;

        /// <summary>Runs one delegation; see the class summary for its order and cancellation rules.</summary>
        /// <param name="request">What the lead asked for.</param>
        /// <param name="cancellationToken">The lead's tool invocation token.</param>
        public Task<SubAgentDelegationResult> DelegateAsync(
            SubAgentDelegationRequest request,
            CancellationToken cancellationToken = default)
        {
            if (_depth.Value > 0)
            {
                return Task.FromResult(SubAgentDelegationResult.Refused(
                    SubAgentErrorCode.NestedDelegation,
                    "A sub-agent cannot delegate: finish this task yourself with the tools you have.",
                    request?.SubAgentId?.Trim()));
            }

            return DelegateCoreAsync(request ?? new SubAgentDelegationRequest(), cancellationToken);
        }

        private async Task<SubAgentDelegationResult> DelegateCoreAsync(
            SubAgentDelegationRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string requestedId = request.SubAgentId?.Trim() ?? "";
            OrchestrationPlan plan = _session.ActivePlan;
            SubAgentDelegationResult refusal = RefuseForPlanState(plan, requestedId);
            if (refusal != null)
            {
                return refusal;
            }

            if (string.IsNullOrWhiteSpace(request.Task))
            {
                return SubAgentDelegationResult.Refused(SubAgentErrorCode.InvalidArguments,
                    "'task' is required: describe the one thing the sub-agent should do.", requestedId);
            }

            if (requestedId.Length == 0 || !_registry.TryGet(requestedId, out SubAgentDefinition definition))
            {
                return SubAgentDelegationResult.Refused(
                    requestedId.Length == 0 ? SubAgentErrorCode.InvalidArguments : SubAgentErrorCode.UnknownSubAgent,
                    (requestedId.Length == 0 ? "'subagent' is required." : $"Unknown sub-agent '{requestedId}'.") +
                    " Known sub-agents: " + DescribeKnownIds() + ".",
                    requestedId);
            }

            string context = Clip(request.Context, _session.Options.MaxContextChars, out _);
            SubAgentErrorCode begin = plan.TryBeginTask(request.TaskId, definition.Id, request.Task,
                out string taskId, out int attempt);
            if (begin != SubAgentErrorCode.None)
            {
                return SubAgentDelegationResult.Refused(begin,
                    $"Task '{taskId}' is already being worked on; wait for its result.", definition.Id, taskId);
            }

            SubAgentErrorCode reserve = plan.Ledger.TryReserveCall();
            if (reserve != SubAgentErrorCode.None)
            {
                return RefuseForBudget(plan, reserve, definition.Id, taskId);
            }

            CancellationTokenSource waitLink =
                CancellationTokenSource.CreateLinkedTokenSource(plan.Scope.Token, cancellationToken);
            try
            {
                await plan.Gate.WaitAsync(waitLink.Token);
            }
            catch (OperationCanceledException)
            {
                plan.Ledger.Refund();
                plan.FinishTask(taskId, PlanTaskState.Cancelled, SubAgentErrorCode.PlanCancelled, "");
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                return SubAgentDelegationResult.Refused(SubAgentErrorCode.PlanCancelled,
                    "The plan was stopped before this task started.", definition.Id, taskId);
            }
            finally
            {
                waitLink.Dispose();
            }

            try
            {
                if (plan.Ledger.IsTokenBudgetExhausted)
                {
                    plan.Ledger.Refund();
                    return RefuseForBudget(plan, SubAgentErrorCode.TokenBudgetExhausted, definition.Id, taskId);
                }

                AiTaskRequest subRequest = SubAgentRequestFactory.Create(definition, request.Task, context,
                    plan.Parent, plan.Id, taskId, attempt, _session.Options.DelegateToolName);
                subRequest.Progress = new LlmRequestProgress();
                plan.MarkTaskRunning(taskId, subRequest.TraceId, subRequest.Progress);
                return await RunAsync(plan, definition, taskId, subRequest, cancellationToken);
            }
            finally
            {
                plan.Gate.Release();
            }
        }

        private async Task<SubAgentDelegationResult> RunAsync(
            OrchestrationPlan plan,
            SubAgentDefinition definition,
            string taskId,
            AiTaskRequest subRequest,
            CancellationToken invocationToken)
        {
            CancellationTokenSource deadline = new();
            CancellationTokenSource deadlineStop = new();
            CancellationTokenSource run =
                CancellationTokenSource.CreateLinkedTokenSource(plan.Scope.Token, invocationToken, deadline.Token);
            _ = ArmDeadlineAsync(definition.Deadline, deadline, deadlineStop.Token);
            _depth.Value = _depth.Value + 1;

            LlmCompletionResult result = null;
            Exception failure = null;
            try
            {
                result = await _runner.RunTaskResultAsync(subRequest, run.Token);
            }
            catch (OperationCanceledException)
            {
                result = null;
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                deadlineStop.Cancel();
                run.Dispose();
            }

            int? tokens = ResolveTokens(result);
            plan.Ledger.RecordCompletion(tokens);
            if (result != null && result.Ok)
            {
                string content = Clip(result.Content, definition.MaxResultChars, out bool truncated);
                plan.FinishTask(taskId, PlanTaskState.Succeeded, SubAgentErrorCode.None, content);
                return new SubAgentDelegationResult
                {
                    Ok = true,
                    TaskId = taskId,
                    SubAgentId = definition.Id,
                    Content = content,
                    Truncated = truncated,
                    TotalTokens = tokens,
                    TraceId = subRequest.TraceId
                };
            }

            if (invocationToken.IsCancellationRequested)
            {
                plan.FinishTask(taskId, PlanTaskState.Cancelled, SubAgentErrorCode.PlanCancelled, "");
                throw new OperationCanceledException(invocationToken);
            }

            SubAgentErrorCode code;
            PlanTaskState state;
            string message;
            if (plan.Scope.IsCancellationRequested)
            {
                code = SubAgentErrorCode.PlanCancelled;
                state = PlanTaskState.Cancelled;
                message = "The plan was stopped while this task ran.";
            }
            else if (deadline.IsCancellationRequested)
            {
                code = SubAgentErrorCode.DeadlineExceeded;
                state = PlanTaskState.Failed;
                message = $"The sub-agent did not finish within {definition.Deadline.TotalSeconds:0.#} s.";
            }
            else
            {
                code = SubAgentErrorCode.SubAgentFailed;
                state = PlanTaskState.Failed;
                message = failure != null
                    ? "The sub-agent run failed: " + failure.GetBaseException().Message
                    : "The sub-agent run failed: " + DescribeFailure(result);
            }

            plan.FinishTask(taskId, state, code, "");
            return new SubAgentDelegationResult
            {
                Ok = false,
                ErrorCode = code,
                Message = message,
                TaskId = taskId,
                SubAgentId = definition.Id,
                ProviderErrorCode = result?.ErrorCode ?? LlmErrorCode.None,
                TotalTokens = tokens,
                TraceId = subRequest.TraceId
            };
        }

        private async Task ArmDeadlineAsync(TimeSpan deadline, CancellationTokenSource expire, CancellationToken stop)
        {
            double totalMs = Math.Ceiling(deadline.TotalMilliseconds);
            int milliseconds = totalMs >= int.MaxValue ? int.MaxValue : Math.Max(1, (int)totalMs);
            try
            {
                await _asyncMarshaler.DelayAsync(milliseconds, stop);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // WHY: a host that bridges a cancelled frame delay into a Task faults it instead of cancelling
                // it, and a delay that faults for any other reason is not an expired deadline either: failing a
                // healthy run on it would be worse than leaving it bounded by the plan scope alone.
                return;
            }

            if (!stop.IsCancellationRequested)
            {
                expire.Cancel();
            }
        }

        private SubAgentDelegationResult RefuseForPlanState(OrchestrationPlan plan, string requestedId)
        {
            if (plan == null)
            {
                return SubAgentDelegationResult.Refused(SubAgentErrorCode.PlanNotActive,
                    "No plan is running in this conversation, so there is nothing to delegate for.", requestedId);
            }

            if (plan.Scope.IsCancellationRequested || plan.State == OrchestrationPlanState.Cancelled)
            {
                return SubAgentDelegationResult.Refused(SubAgentErrorCode.PlanCancelled,
                    "The plan was stopped; do not start new tasks.", requestedId);
            }

            if (plan.State == OrchestrationPlanState.BudgetExhausted)
            {
                SubAgentErrorCode exhausted = plan.ExhaustedBudget == SubAgentErrorCode.None
                    ? SubAgentErrorCode.CallBudgetExhausted
                    : plan.ExhaustedBudget;
                return SubAgentDelegationResult.Refused(exhausted, DescribeBudget(plan, exhausted), requestedId);
            }

            if (plan.State != OrchestrationPlanState.Running)
            {
                return SubAgentDelegationResult.Refused(SubAgentErrorCode.PlanNotActive,
                    "The plan has already finished.", requestedId);
            }

            return null;
        }

        private static SubAgentDelegationResult RefuseForBudget(
            OrchestrationPlan plan,
            SubAgentErrorCode budget,
            string subAgentId,
            string taskId)
        {
            plan.MarkBudgetExhausted(budget);
            plan.FinishTask(taskId, PlanTaskState.Refused, budget, "");
            return SubAgentDelegationResult.Refused(budget, DescribeBudget(plan, budget), subAgentId, taskId);
        }

        private static string DescribeBudget(OrchestrationPlan plan, SubAgentErrorCode budget)
        {
            OrchestrationBudgetSnapshot spent = plan.Ledger.Snapshot();
            return budget == SubAgentErrorCode.TokenBudgetExhausted
                ? $"The plan's token budget is spent ({spent.TokensUsed} of {spent.MaxTotalTokens}). " +
                  "Report what is done to the player instead of starting new tasks."
                : $"The plan's call budget is spent ({spent.MaxSubAgentCalls} sub-agent runs). " +
                  "Report what is done to the player instead of starting new tasks.";
        }

        private string DescribeKnownIds()
        {
            IReadOnlyList<SubAgentDefinition> definitions = _registry.Definitions;
            if (definitions.Count == 0)
            {
                return "none are registered";
            }

            List<string> ids = new(definitions.Count);
            foreach (SubAgentDefinition definition in definitions)
            {
                ids.Add(definition.Id);
            }

            return string.Join(", ", ids);
        }

        private static string DescribeFailure(LlmCompletionResult result)
        {
            if (result == null)
            {
                return "no result was returned.";
            }

            string error = string.IsNullOrWhiteSpace(result.Error) ? "no error text" : result.Error.Trim();
            return result.ErrorCode == LlmErrorCode.None ? error : result.ErrorCode + ": " + error;
        }

        private static int? ResolveTokens(LlmCompletionResult result)
        {
            if (result == null)
            {
                return null;
            }

            if (result.TotalTokens.HasValue)
            {
                return result.TotalTokens.Value;
            }

            if (result.PromptTokens.HasValue || result.CompletionTokens.HasValue)
            {
                return (result.PromptTokens ?? 0) + (result.CompletionTokens ?? 0);
            }

            return null;
        }

        /// <summary>Clips <paramref name="text"/> to <paramref name="maxChars"/> with a marker; 0 or less keeps it whole.</summary>
        internal static string Clip(string text, int maxChars, out bool truncated)
        {
            string value = text ?? "";
            truncated = false;
            if (maxChars <= 0 || value.Length <= maxChars)
            {
                return value;
            }

            // WHY the marker is sized for the whole length first: the dropped count it finally prints is never
            // longer than that, so the clipped text stays within maxChars whenever the marker itself fits.
            string widestMarker = string.Format(ClipMarker, value.Length);
            int keep = Math.Max(0, maxChars - widestMarker.Length);
            truncated = true;
            return value.Substring(0, keep) + string.Format(ClipMarker, value.Length - keep);
        }
    }
}
