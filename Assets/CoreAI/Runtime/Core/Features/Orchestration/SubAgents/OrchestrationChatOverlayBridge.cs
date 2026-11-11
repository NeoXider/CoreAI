using System;
using System.Collections.Generic;
using CoreAI.ChatOverlay;

namespace CoreAI.Ai.SubAgents
{
    /// <summary>
    /// Shows orchestrator mode in the in-world chat overlay. The overlay's status line carries the plan line of
    /// the turn it is waiting for ("builder: walls 2/5", or "waiting: ..." while the instruction is queued), and
    /// the feed gets one line whenever a sub-agent starts or finishes a task and when a plan ends, for every
    /// conversation, so the player sees what each sub-agent is doing whichever chat started the plan.
    /// <para>
    /// Call <see cref="Tick"/> on the overlay's thread (the Unity host calls it every frame); it only reads
    /// thread-safe snapshots, so plan changes on other threads show up on the next tick. It also registers the
    /// <c>/orchestrator</c> command that switches the mode on and off.
    /// </para>
    /// </summary>
    public sealed class OrchestrationChatOverlayBridge : IDisposable
    {
        /// <summary>Name of the chat command that switches the mode.</summary>
        public const string CommandName = "orchestrator";

        /// <summary>Plans remembered for narration; older ones are forgotten.</summary>
        public const int MaxRememberedPlans = 32;

        private readonly OrchestratorChatDriver _driver;
        private readonly AgentStatusTracker _status;
        private readonly Func<string> _activeTraceId;
        private readonly IChatOverlayMessages _messages;
        private readonly Dictionary<string, PlanView> _plans = new(StringComparer.Ordinal);
        private readonly List<string> _planOrder = new();
        private IDisposable _command;

        /// <summary>Creates the bridge; any of the overlay parts may be null (that part is skipped).</summary>
        /// <param name="driver">The chat driver of orchestrator mode.</param>
        /// <param name="status">The overlay's status tracker (<c>ChatOverlayController.Status</c>).</param>
        /// <param name="activeTraceId">The trace of the overlay's running turn (<c>ChatOverlayController.ActiveTraceId</c>).</param>
        /// <param name="messages">The overlay feed.</param>
        public OrchestrationChatOverlayBridge(
            OrchestratorChatDriver driver,
            AgentStatusTracker status,
            Func<string> activeTraceId,
            IChatOverlayMessages messages)
        {
            _driver = driver ?? throw new ArgumentNullException(nameof(driver));
            _status = status;
            _activeTraceId = activeTraceId;
            _messages = messages;
        }

        /// <summary>Updates the status line and posts the feed lines for what changed since the last tick.</summary>
        public void Tick()
        {
            UpdateStatusLine();
            Narrate();
        }

        /// <summary>Registers <c>/orchestrator [on|off|status]</c> (alias <c>/lead</c>); disposing the bridge removes it.</summary>
        public void RegisterCommand(IChatCommandRegistry registry)
        {
            if (registry == null || _command != null)
            {
                return;
            }

            _command = registry.Register(new ChatCommand(CommandName,
                "Switch orchestrator mode: a lead agent plans and hands tasks to sub-agents.",
                HandleCommandContext, "[on|off|status]", "lead"));
        }

        /// <summary>The reply of the command for <paramref name="argument"/> (also used by other chat surfaces).</summary>
        public ChatCommandResult HandleCommand(string argument)
        {
            string value = argument?.Trim().ToLowerInvariant() ?? "";
            switch (value)
            {
                case "on":
                    _driver.IsActive = true;
                    return ChatCommandResult.Handled("Orchestrator mode is on: a lead agent plans and delegates.");
                case "off":
                    _driver.IsActive = false;
                    return ChatCommandResult.Handled("Orchestrator mode is off: one agent answers.");
                case "":
                case "status":
                    return ChatCommandResult.Handled(DescribeState());
                default:
                    return ChatCommandResult.Failed("Use /" + CommandName + " on, off or status.");
            }
        }

        /// <summary>Removes the command.</summary>
        public void Dispose()
        {
            _command?.Dispose();
            _command = null;
        }

        private ChatCommandResult HandleCommandContext(ChatCommandContext context)
        {
            return HandleCommand(context == null ? "" : context.Invocation.ArgumentText);
        }

        private string DescribeState()
        {
            string mode = _driver.IsActive ? "on" : "off";
            foreach (OrchestratorSession session in _driver.Sessions.Sessions)
            {
                OrchestrationStatus current = session.Current;
                if (current.Plan != null && current.Plan.State == OrchestrationPlanState.Running)
                {
                    return "Orchestrator mode is " + mode + ". Running: " + OrchestrationStatusText.FormatLine(current) +
                           NextSuffix(current);
                }
            }

            return "Orchestrator mode is " + mode + ". No plan is running.";
        }

        private static string NextSuffix(OrchestrationStatus status)
        {
            string next = OrchestrationStatusText.FormatNextLine(status);
            return next.Length == 0 ? "" : "; " + next;
        }

        private void UpdateStatusLine()
        {
            if (_status == null)
            {
                return;
            }

            string trace = _activeTraceId?.Invoke() ?? "";
            if (_driver.TryGetStatusForLeadTrace(trace, out OrchestrationStatus status, out bool waiting))
            {
                string line = OrchestrationStatusText.FormatLine(status);
                _status.SetDetail(waiting ? "waiting: " + line : line);
                return;
            }

            _status.SetDetail("");
        }

        private void Narrate()
        {
            if (_messages == null)
            {
                return;
            }

            foreach (OrchestratorSession session in _driver.Sessions.Sessions)
            {
                OrchestrationPlanSnapshot plan = session.Current.Plan;
                if (plan == null)
                {
                    continue;
                }

                PlanView view = Remember(plan.PlanId);
                if (view.Ended)
                {
                    continue;
                }

                foreach (PlanTaskSnapshot task in plan.Tasks)
                {
                    view.States.TryGetValue(task.Id, out PlanTaskState seen);
                    bool known = view.States.ContainsKey(task.Id);
                    if (known && seen == task.State)
                    {
                        continue;
                    }

                    view.States[task.Id] = task.State;
                    string line = DescribeTask(task, known);
                    if (line.Length > 0)
                    {
                        _messages.PostMessage(Speaker(task), line);
                    }
                }

                if (plan.State != OrchestrationPlanState.Running)
                {
                    view.Ended = true;
                    _messages.PostSystemMessage("Plan " + OrchestrationStatusText.FormatLine(session.Current) + ".");
                }
            }
        }

        private static string DescribeTask(PlanTaskSnapshot task, bool known)
        {
            switch (task.State)
            {
                case PlanTaskState.Pending:
                    return known ? "" : "planned: " + task.Title;
                case PlanTaskState.Waiting:
                    return "";
                case PlanTaskState.Running:
                    return "working on: " + task.Title;
                case PlanTaskState.Succeeded:
                    return "done: " + task.Title;
                case PlanTaskState.Cancelled:
                    return "stopped: " + task.Title;
                default:
                    return "failed: " + task.Title;
            }
        }

        private static string Speaker(PlanTaskSnapshot task)
        {
            return string.IsNullOrEmpty(task.SubAgentId) ? "lead" : task.SubAgentId;
        }

        private PlanView Remember(string planId)
        {
            if (_plans.TryGetValue(planId, out PlanView view))
            {
                return view;
            }

            view = new PlanView();
            _plans.Add(planId, view);
            _planOrder.Add(planId);
            if (_planOrder.Count > MaxRememberedPlans)
            {
                _plans.Remove(_planOrder[0]);
                _planOrder.RemoveAt(0);
            }

            return view;
        }

        private sealed class PlanView
        {
            public readonly Dictionary<string, PlanTaskState> States = new(StringComparer.Ordinal);

            public bool Ended;
        }
    }
}
