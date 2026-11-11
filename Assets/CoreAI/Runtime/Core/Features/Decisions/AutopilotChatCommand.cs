using System;
using System.Collections.Generic;
using CoreAI.ChatOverlay;

namespace CoreAI.Decisions
{
    /// <summary>
    /// <c>/autopilot [on|off|status]</c> for the in-world chat: hands the player's character to the autopilot, takes
    /// it back, or says what is driving it (which decision model, or the built-in fallback and why). A bare
    /// <c>/autopilot</c> shows the status.
    /// </summary>
    public sealed class AutopilotChatCommand : IChatCommand
    {
        /// <summary>The command's name.</summary>
        public const string CommandName = "autopilot";

        private readonly IAutopilotControl _autopilot;

        /// <summary>Creates the command over <paramref name="autopilot"/>.</summary>
        public AutopilotChatCommand(IAutopilotControl autopilot)
        {
            _autopilot = autopilot ?? throw new ArgumentNullException(nameof(autopilot));
        }

        /// <inheritdoc />
        public string Name => CommandName;

        /// <inheritdoc />
        public IReadOnlyList<string> Aliases => Array.Empty<string>();

        /// <inheritdoc />
        public string Usage => "[on|off|status]";

        /// <inheritdoc />
        public string Description =>
            "Hands your character to the autopilot, takes it back, or says what drives it. Any movement key takes it back too.";

        /// <summary>
        /// Registers the command and posts a line into the chat whenever the autopilot stops on its own (the player
        /// pressed a key, the character died). Dispose the handle to remove both.
        /// </summary>
        public static IDisposable Register(IChatCommandRegistry registry, IAutopilotControl autopilot,
            IChatOverlayMessages messages = null, bool replaceExisting = false)
        {
            if (registry == null)
            {
                throw new ArgumentNullException(nameof(registry));
            }

            IDisposable command = registry.Register(new AutopilotChatCommand(autopilot), replaceExisting);
            return new Registration(command, autopilot, messages);
        }

        /// <inheritdoc />
        public ChatCommandResult Execute(ChatCommandContext context)
        {
            string prefix = context.Prefix.ToString();
            string argument = context.Invocation.ArgumentAt(0).Trim().ToLowerInvariant();
            if (context.Invocation.Arguments.Count > 1)
            {
                return ChatCommandResult.Failed(UsageText(prefix));
            }

            switch (argument)
            {
                case "":
                case "status":
                    return ChatCommandResult.Handled(DescribeStatus(_autopilot.GetStatus(), prefix));
                case "on":
                    return TurnOn(prefix);
                case "off":
                    return _autopilot.Disengage()
                        ? ChatCommandResult.Handled("Autopilot off: you have control.")
                        : ChatCommandResult.Handled("Autopilot is already off.");
                default:
                    return ChatCommandResult.Failed(UsageText(prefix));
            }
        }

        /// <summary>The status line <c>/autopilot status</c> answers with.</summary>
        public static string DescribeStatus(AutopilotStatus status, string prefix = "/")
        {
            string driver = AutopilotStatusText.DescribeDriver(status);
            if (status.Engaged)
            {
                AutopilotDecision decision = status.LastDecision;
                string goal = decision == null
                    ? "no goal chosen yet"
                    : "goal '" + decision.GoalId + "', " +
                      AutopilotStatusText.DescribeDecision(decision, status.ConfidenceThreshold);
                return "Autopilot on, " + goal + ". Decisions come from " + driver + ".";
            }

            string released = AutopilotStatusText.DescribeRelease(status.LastReleaseReason);
            string head = released.Length > 0 ? "Autopilot off (" + released + ")." : "Autopilot off.";
            string unavailable = status.UnavailableReason.Length > 0 ? " " + status.UnavailableReason : "";
            return head + unavailable + " Decisions would come from " + driver + ". Type " + prefix +
                   CommandName + " on to hand your character over.";
        }

        private ChatCommandResult TurnOn(string prefix)
        {
            AutopilotEngageResult result = _autopilot.Engage();
            if (!result.Engaged)
            {
                return ChatCommandResult.Failed("Autopilot not started: " + result.Message);
            }

            AutopilotStatus status = _autopilot.GetStatus();
            string head = result.WasEngaged ? "Autopilot is already on" : "Autopilot on";
            string goals = status.GoalIds.Count > 0 ? " Goals: " + string.Join(", ", status.GoalIds) + "." : "";
            return ChatCommandResult.Handled(head + ": decisions come from " + AutopilotStatusText.DescribeDriver(status) +
                                             "." + goals + " Press any movement key, or type " + prefix + CommandName +
                                             " off, to take control back.");
        }

        private static string UsageText(string prefix)
        {
            return "Usage: " + prefix + CommandName + " [on|off|status]";
        }

        private sealed class Registration : IDisposable
        {
            private readonly IAutopilotControl _autopilot;
            private readonly IChatOverlayMessages _messages;
            private IDisposable _command;

            public Registration(IDisposable command, IAutopilotControl autopilot, IChatOverlayMessages messages)
            {
                _command = command;
                _autopilot = autopilot;
                _messages = messages;
                if (_messages != null)
                {
                    _autopilot.EngagedChanged += HandleEngagedChanged;
                }
            }

            public void Dispose()
            {
                IDisposable command = _command;
                if (command == null)
                {
                    return;
                }

                _command = null;
                command.Dispose();
                if (_messages != null)
                {
                    _autopilot.EngagedChanged -= HandleEngagedChanged;
                }
            }

            private void HandleEngagedChanged(bool engaged, string reason)
            {
                if (engaged || reason == PlayerAutopilotPolicy.ReleasedByHost)
                {
                    return;
                }

                string released = AutopilotStatusText.DescribeRelease(reason);
                _messages.PostSystemMessage(released.Length > 0
                    ? "Autopilot off: " + released + "."
                    : "Autopilot off.");
            }
        }
    }
}
