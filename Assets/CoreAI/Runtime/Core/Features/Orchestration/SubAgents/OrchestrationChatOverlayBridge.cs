using System;
using System.Collections.Generic;
using CoreAI.ChatOverlay;

namespace CoreAI.Ai.SubAgents
{
    /// <summary>
    /// Shows orchestrator mode in the in-world chat overlay, for every conversation, so the player sees what each
    /// sub-agent is doing whichever chat started it.
    /// <para>
    /// Plan style: the status line carries the plan line of the turn the overlay waits for ("builder: walls 2/5", or
    /// "waiting: ..." while the instruction is queued), and the feed gets one line whenever a sub-agent starts or
    /// finishes a task and when a plan ends.
    /// </para>
    /// <para>
    /// Team style: the feed gets one line per sub-agent change (<c>builder-1: started: ...</c>, <c>working on:</c>,
    /// <c>got a message</c>, <c>finished:</c>, <c>failed:</c>, <c>stopped</c>); the status line shows the busy
    /// sub-agents (<see cref="TeamLine"/>) while the agent's own turn thinks and, between turns, through the
    /// tracker's background detail. When a sub-agent's completion waits and the overlay is free, the bridge runs the
    /// wake turn as an overlay host turn (<see cref="ChatOverlayHostTurnRequest"/>), so the agent's answer streams into
    /// the chat, the status line follows it and <c>/stop</c> stops it.
    /// </para>
    /// <para>
    /// Call <see cref="Tick"/> on the overlay's thread (the Unity host calls it every frame); it only reads
    /// thread-safe snapshots, so changes on other threads show up on the next tick. It also registers the
    /// <c>/orchestrator</c> command that switches the mode on and off and <c>/agents</c>, which lists the sub-agents.
    /// </para>
    /// </summary>
    public sealed class OrchestrationChatOverlayBridge : IDisposable
    {
        /// <summary>Name of the chat command that switches the mode.</summary>
        public const string CommandName = "orchestrator";

        /// <summary>Name of the chat command that lists the sub-agents and their states.</summary>
        public const string AgentsCommandName = "agents";

        /// <summary>Plans remembered for narration; older ones are forgotten.</summary>
        public const int MaxRememberedPlans = 32;

        private readonly OrchestratorChatDriver _driver;
        private readonly AgentStatusTracker _status;
        private readonly Func<string> _activeTraceId;
        private readonly IChatOverlayMessages _messages;
        private readonly Func<ChatOverlayHostTurnRequest, bool> _startHostTurn;
        private readonly Func<bool> _isTurnActive;
        private readonly Dictionary<string, PlanView> _plans = new(StringComparer.Ordinal);
        private readonly List<string> _planOrder = new();
        private readonly Dictionary<string, MemberView> _members = new(StringComparer.Ordinal);
        private readonly HashSet<string> _overlayConversations = new(StringComparer.Ordinal);
        private IDisposable _command;
        private IDisposable _agentsCommand;

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
            : this(driver, status, activeTraceId, messages, null, null)
        {
        }

        /// <summary>Creates the bridge with the overlay's host-turn entry, so it can run wake turns (Team style).</summary>
        /// <param name="driver">The chat driver of orchestrator mode.</param>
        /// <param name="status">The overlay's status tracker (<c>ChatOverlayController.Status</c>).</param>
        /// <param name="activeTraceId">The trace of the overlay's running turn (<c>ChatOverlayController.ActiveTraceId</c>).</param>
        /// <param name="messages">The overlay feed.</param>
        /// <param name="startHostTurn">Starts a host turn (<c>ChatOverlayController.TryStartHostTurn</c>); null runs no wake.</param>
        /// <param name="isTurnActive">Whether the overlay runs a turn (<c>ChatOverlayController.IsTurnActive</c>).</param>
        public OrchestrationChatOverlayBridge(
            OrchestratorChatDriver driver,
            AgentStatusTracker status,
            Func<string> activeTraceId,
            IChatOverlayMessages messages,
            Func<ChatOverlayHostTurnRequest, bool> startHostTurn,
            Func<bool> isTurnActive)
        {
            _driver = driver ?? throw new ArgumentNullException(nameof(driver));
            _status = status;
            _activeTraceId = activeTraceId;
            _messages = messages;
            _startHostTurn = startHostTurn;
            _isTurnActive = isTurnActive;
        }

        /// <summary>
        /// The busy Team-style sub-agents of every conversation as one line (<c>builder-1 working (2 steps);
        /// scripter-1 started</c>), as of the last <see cref="Tick"/>; empty when none is busy.
        /// </summary>
        public string TeamLine { get; private set; } = "";

        /// <summary>
        /// Updates the status line, posts the feed lines for what changed since the last tick and, when a
        /// sub-agent's completion waits and the overlay is free, starts the wake turn.
        /// </summary>
        public void Tick()
        {
            UpdateTeamLine();
            UpdateStatusLine();
            Narrate();
            NarrateTeam();
            TryRunWake();
        }

        /// <summary>
        /// Registers <c>/orchestrator [on|off|status]</c> (alias <c>/lead</c>) and <c>/agents</c> (alias
        /// <c>/subagents</c>); disposing the bridge removes them.
        /// </summary>
        public void RegisterCommand(IChatCommandRegistry registry)
        {
            if (registry == null || _command != null)
            {
                return;
            }

            _command = registry.Register(new ChatCommand(CommandName,
                "Switch orchestrator mode: the agent hands big jobs to sub-agents.",
                HandleCommandContext, "[on|off|status]", "lead"));
            _agentsCommand = registry.Register(new ChatCommand(AgentsCommandName,
                "List the sub-agents and what each is doing; 'stop' stops them all, 'stop <name>' one.",
                context => HandleAgentsCommand(context == null ? "" : context.Invocation.ArgumentText),
                "[stop [name]]", "subagents"));
        }

        /// <summary>The reply of the command for <paramref name="argument"/> (also used by other chat surfaces).</summary>
        public ChatCommandResult HandleCommand(string argument)
        {
            string value = argument?.Trim().ToLowerInvariant() ?? "";
            switch (value)
            {
                case "on":
                    _driver.IsActive = true;
                    return ChatCommandResult.Handled(_driver.Style == OrchestrationStyle.Team
                        ? "Orchestrator mode is on: the agent hands big jobs to sub-agents and keeps talking."
                        : "Orchestrator mode is on: a lead agent plans and delegates.");
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

        /// <summary>
        /// The reply of <c>/agents</c>: without an argument every sub-agent of every conversation and its state;
        /// <c>stop</c> stops all of them (as Stop does, also between turns, when the chat's own Stop finds no turn);
        /// <c>stop name</c> stops one and shows its last answer.
        /// </summary>
        public ChatCommandResult HandleAgentsCommand(string argument = "")
        {
            string value = argument?.Trim() ?? "";
            if (value.Length == 0)
            {
                List<SubAgentMemberSnapshot> all = new();
                foreach (OrchestratorSession session in _driver.Sessions.Sessions)
                {
                    all.AddRange(session.Team.Snapshot());
                }

                return ChatCommandResult.Handled(OrchestrationStatusText.FormatTeamList(all));
            }

            string[] words = value.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (!string.Equals(words[0], "stop", StringComparison.OrdinalIgnoreCase))
            {
                return ChatCommandResult.Failed("Use /" + AgentsCommandName + ", /" + AgentsCommandName +
                                                " stop or /" + AgentsCommandName + " stop <name>.");
            }

            if (words.Length == 1)
            {
                bool stopped = false;
                foreach (OrchestratorSession session in _driver.Sessions.Sessions)
                {
                    stopped |= session.Team.StopAll();
                }

                return ChatCommandResult.Handled(stopped ? "Stopped every sub-agent." : "No sub-agent is working.");
            }

            foreach (OrchestratorSession session in _driver.Sessions.Sessions)
            {
                SubAgentControlResult result = session.Team.Stop(words[1]);
                if (result.Ok)
                {
                    string answer = OrchestrationPlan.ClipLine(result.Answer, OrchestrationPlan.MaxSummaryChars);
                    return ChatCommandResult.Handled(result.Agent + " " +
                                                     (result.WasState < SubAgentMemberState.Finished
                                                         ? "stopped."
                                                         : "was not working.") +
                                                     (answer.Length > 0 ? " Last answer: " + answer : ""));
                }
            }

            return ChatCommandResult.Failed("No sub-agent is called '" + words[1].Trim() + "'.");
        }

        /// <summary>Removes the commands and clears the background status the bridge set.</summary>
        public void Dispose()
        {
            _command?.Dispose();
            _command = null;
            _agentsCommand?.Dispose();
            _agentsCommand = null;
            _status?.SetBackgroundDetail("");
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

            if (_driver.Style == OrchestrationStyle.Team)
            {
                ChatCommandResult agents = HandleAgentsCommand();
                return "Orchestrator mode is " + mode + ". Sub-agents:\n" + agents.Reply;
            }

            return "Orchestrator mode is " + mode + ". No plan is running.";
        }

        private static string NextSuffix(OrchestrationStatus status)
        {
            string next = OrchestrationStatusText.FormatNextLine(status);
            return next.Length == 0 ? "" : "; " + next;
        }

        private void UpdateTeamLine()
        {
            List<SubAgentMemberSnapshot> busy = new();
            foreach (OrchestratorSession session in _driver.Sessions.Sessions)
            {
                busy.AddRange(session.Team.Snapshot());
            }

            TeamLine = OrchestrationStatusText.FormatTeamLine(busy);
            _status?.SetBackgroundDetail(TeamLine);
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

            if (_driver.TryGetTeamSessionForLeadTrace(trace, out OrchestratorSession session, out _))
            {
                _overlayConversations.Add(session.ConversationId);
                // WHY only while thinking: the agent's own tool calls ("running execute_lua") are its truth; the
                // sub-agents' line fills the time the agent shows nothing else.
                string team = OrchestrationStatusText.FormatTeamLine(session.Team.Snapshot());
                _status.SetDetail(_status.Kind == AgentStatusKind.Thinking ? team : "");
                return;
            }

            _status.SetDetail("");
        }

        private void TryRunWake()
        {
            if (_startHostTurn == null || _isTurnActive == null || _isTurnActive())
            {
                return;
            }

            foreach (string conversation in _overlayConversations)
            {
                if (!_driver.TryTakeWake(conversation, out LeadWake wake))
                {
                    continue;
                }

                ChatOverlayHostTurnRequest request = new()
                {
                    Text = wake.Text,
                    RoleId = wake.RoleId,
                    TraceId = wake.TraceId,
                    ShowAnswer = true
                };
                bool started;
                try
                {
                    started = _startHostTurn(request);
                }
                catch (Exception)
                {
                    started = false;
                }

                if (!started)
                {
                    _driver.ReturnWake(wake);
                }

                return;
            }
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

        private void NarrateTeam()
        {
            if (_messages == null)
            {
                return;
            }

            foreach (OrchestratorSession session in _driver.Sessions.Sessions)
            {
                foreach (SubAgentMemberSnapshot member in session.Team.Snapshot())
                {
                    string key = session.ConversationId + "\n" + member.Name;
                    bool known = _members.TryGetValue(key, out MemberView seen);
                    string line = DescribeMemberChange(member, known ? seen : null);
                    _members[key] = new MemberView(member.State, member.Turns, member.PendingMessages);
                    if (line.Length > 0)
                    {
                        _messages.PostMessage(member.Name, line);
                    }
                }
            }
        }

        private static string DescribeMemberChange(SubAgentMemberSnapshot member, MemberView seen)
        {
            if (seen == null)
            {
                return member.IsIdle ? DescribeIdle(member) : "started: " + member.Label;
            }

            bool newTurn = member.Turns != seen.Turns;
            if (member.State == seen.State && !newTurn)
            {
                return member.State == SubAgentMemberState.Working && member.PendingMessages > seen.Pending
                    ? "got a message"
                    : "";
            }

            switch (member.State)
            {
                case SubAgentMemberState.Queued:
                    return seen.State >= SubAgentMemberState.Finished ? "continuing: " + member.Label : "";
                case SubAgentMemberState.Working:
                    return "working on: " + member.Label;
                default:
                    return DescribeIdle(member);
            }
        }

        private static string DescribeIdle(SubAgentMemberSnapshot member)
        {
            switch (member.State)
            {
                case SubAgentMemberState.Finished:
                    return "finished: " + OrchestrationPlan.ClipLine(member.LastAnswer, OrchestrationPlan.MaxSummaryChars);
                case SubAgentMemberState.Failed:
                    return "failed: " + OrchestrationPlan.ClipLine(member.LastError, OrchestrationPlan.MaxSummaryChars);
                default:
                    return "stopped";
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

        private sealed class MemberView
        {
            public MemberView(SubAgentMemberState state, int turns, int pending)
            {
                State = state;
                Turns = turns;
                Pending = pending;
            }

            public SubAgentMemberState State { get; }

            public int Turns { get; }

            public int Pending { get; }
        }
    }
}
