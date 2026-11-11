using System;
using System.Collections.Generic;
using System.Text;

namespace CoreAI.ChatOverlay
{
    /// <summary>
    /// The commands every in-world chat starts with: <c>/help</c>, <c>/agent</c>, <c>/model</c> and <c>/stop</c>.
    /// Each is an ordinary <see cref="IChatCommand"/>: a game replaces one by registering its own under the same
    /// name with <c>replaceExisting: true</c>, or removes it with <see cref="IChatCommandRegistry.Unregister"/>.
    /// </summary>
    public static class ChatOverlayBuiltInCommands
    {
        /// <summary>
        /// Registers the built-ins whose names are still free (a command the game registered first wins).
        /// <paramref name="models"/> may be null, which leaves <c>/model</c> out. Dispose the result to remove them.
        /// </summary>
        public static IDisposable RegisterAll(IChatCommandRegistry registry, IChatAgentSelector agents,
            IChatModelSelector models, IChatTurnControl turns)
        {
            if (registry == null)
            {
                throw new ArgumentNullException(nameof(registry));
            }

            List<IDisposable> handles = new();
            TryAdd(registry, new HelpChatCommand(), handles);
            if (agents != null)
            {
                TryAdd(registry, new AgentChatCommand(agents), handles);
            }

            if (models != null && agents != null)
            {
                TryAdd(registry, new ModelChatCommand(models, agents), handles);
            }

            if (turns != null)
            {
                TryAdd(registry, new StopChatCommand(turns), handles);
            }

            return new CompositeHandle(handles);
        }

        private static void TryAdd(IChatCommandRegistry registry, IChatCommand command, List<IDisposable> handles)
        {
            if (registry.TryResolve(command.Name, out _))
            {
                return;
            }

            foreach (string alias in command.Aliases)
            {
                if (registry.TryResolve(alias, out _))
                {
                    return;
                }
            }

            handles.Add(registry.Register(command));
        }

        internal static string JoinRoles(IReadOnlyList<string> roles)
        {
            return roles == null || roles.Count == 0 ? "any role id" : string.Join(", ", roles);
        }

        private sealed class CompositeHandle : IDisposable
        {
            private List<IDisposable> _handles;

            public CompositeHandle(List<IDisposable> handles)
            {
                _handles = handles;
            }

            public void Dispose()
            {
                List<IDisposable> handles = _handles;
                _handles = null;
                if (handles == null)
                {
                    return;
                }

                foreach (IDisposable handle in handles)
                {
                    handle.Dispose();
                }
            }
        }
    }

    /// <summary><c>/help [command]</c>: lists the commands, or describes one.</summary>
    public sealed class HelpChatCommand : IChatCommand
    {
        private static readonly string[] HelpAliases = { "?" };

        /// <inheritdoc />
        public string Name => "help";

        /// <inheritdoc />
        public IReadOnlyList<string> Aliases => HelpAliases;

        /// <inheritdoc />
        public string Usage => "[command]";

        /// <inheritdoc />
        public string Description => "List the chat commands, or describe one.";

        /// <inheritdoc />
        public ChatCommandResult Execute(ChatCommandContext context)
        {
            char prefix = context.Prefix;
            if (context.Invocation.HasArguments)
            {
                string wanted = context.Invocation.ArgumentAt(0);
                if (!context.Registry.TryResolve(wanted.TrimStart(prefix), out IChatCommand command))
                {
                    return ChatCommandResult.Failed(
                        $"Unknown command {prefix}{wanted.TrimStart(prefix)}. Type {prefix}help for the list.");
                }

                StringBuilder detail = new();
                detail.Append(Describe(command, prefix));
                if (command.Aliases != null && command.Aliases.Count > 0)
                {
                    detail.Append("\nAliases: ");
                    for (int i = 0; i < command.Aliases.Count; i++)
                    {
                        detail.Append(i == 0 ? "" : ", ").Append(prefix).Append(command.Aliases[i]);
                    }
                }

                return ChatCommandResult.Handled(detail.ToString());
            }

            IReadOnlyList<IChatCommand> commands = context.Registry.Commands;
            if (commands.Count == 0)
            {
                return ChatCommandResult.Handled("No commands are registered.");
            }

            StringBuilder list = new("Commands:");
            foreach (IChatCommand command in commands)
            {
                list.Append('\n').Append(Describe(command, prefix));
            }

            return ChatCommandResult.Handled(list.ToString());
        }

        private static string Describe(IChatCommand command, char prefix)
        {
            string usage = string.IsNullOrWhiteSpace(command.Usage) ? "" : " " + command.Usage.Trim();
            string description = string.IsNullOrWhiteSpace(command.Description) ? "" : " - " + command.Description.Trim();
            return prefix + command.Name + usage + description;
        }
    }

    /// <summary><c>/agent [role]</c>: shows or switches the agent role that answers.</summary>
    public sealed class AgentChatCommand : IChatCommand
    {
        private readonly IChatAgentSelector _agents;

        /// <summary>Creates the command over <paramref name="agents"/>.</summary>
        public AgentChatCommand(IChatAgentSelector agents)
        {
            _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        }

        /// <inheritdoc />
        public string Name => "agent";

        /// <inheritdoc />
        public IReadOnlyList<string> Aliases => Array.Empty<string>();

        /// <inheritdoc />
        public string Usage => "[role]";

        /// <inheritdoc />
        public string Description => "Show the agent that answers, or switch to another role.";

        /// <inheritdoc />
        public ChatCommandResult Execute(ChatCommandContext context)
        {
            if (!context.Invocation.HasArguments)
            {
                return ChatCommandResult.Handled(
                    $"Agent: {_agents.ActiveRoleId}. Available: {ChatOverlayBuiltInCommands.JoinRoles(_agents.AvailableRoles)}.");
            }

            string wanted = context.Invocation.ArgumentAt(0);
            if (!_agents.TrySelect(wanted, out string selected))
            {
                return ChatCommandResult.Failed(
                    $"Unknown agent '{wanted}'. Available: {ChatOverlayBuiltInCommands.JoinRoles(_agents.AvailableRoles)}.");
            }

            return ChatCommandResult.Handled($"Agent: {selected}.");
        }
    }

    /// <summary><c>/model [profile|auto]</c>: shows or switches the model profile of the active agent.</summary>
    public sealed class ModelChatCommand : IChatCommand
    {
        private readonly IChatModelSelector _models;
        private readonly IChatAgentSelector _agents;

        /// <summary>Creates the command over the routing seam and the active agent.</summary>
        public ModelChatCommand(IChatModelSelector models, IChatAgentSelector agents)
        {
            _models = models ?? throw new ArgumentNullException(nameof(models));
            _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        }

        /// <inheritdoc />
        public string Name => "model";

        /// <inheritdoc />
        public IReadOnlyList<string> Aliases => Array.Empty<string>();

        /// <inheritdoc />
        public string Usage => "[profile|auto]";

        /// <inheritdoc />
        public string Description => "Show the model profile of the active agent, or switch it.";

        /// <inheritdoc />
        public ChatCommandResult Execute(ChatCommandContext context)
        {
            if (!_models.IsAvailable)
            {
                return ChatCommandResult.Failed("Model switching is not available: no model routing is configured.");
            }

            string role = _agents.ActiveRoleId;
            IReadOnlyList<ChatModelProfile> profiles = _models.GetProfiles() ?? Array.Empty<ChatModelProfile>();
            if (!context.Invocation.HasArguments)
            {
                string current = _models.GetProfileForRole(role) ?? "";
                return ChatCommandResult.Handled(
                    $"Model for {role}: {Label(current, profiles)}. Profiles: {ListProfiles(profiles)}.");
            }

            string wanted = context.Invocation.ArgumentText.Trim();
            string profileId;
            string label;
            if (IsAutomatic(wanted))
            {
                profileId = "";
                label = "automatic";
            }
            else if (TryFind(wanted, profiles, out ChatModelProfile match))
            {
                profileId = match.Id;
                label = match.DisplayName;
            }
            else
            {
                return ChatCommandResult.Failed(
                    $"Unknown model profile '{wanted}'. Profiles: {ListProfiles(profiles)}.");
            }

            if (!_models.TryAssign(role, profileId, out string message))
            {
                return ChatCommandResult.Failed(string.IsNullOrWhiteSpace(message)
                    ? $"Could not switch the model of {role}."
                    : $"Could not switch the model of {role}: {message}");
            }

            return ChatCommandResult.Handled($"Model for {role}: {label}.");
        }

        private static bool IsAutomatic(string value)
        {
            return string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "automatic", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "default", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryFind(string wanted, IReadOnlyList<ChatModelProfile> profiles,
            out ChatModelProfile match)
        {
            foreach (ChatModelProfile profile in profiles)
            {
                if (string.Equals(profile.Id, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    match = profile;
                    return true;
                }
            }

            foreach (ChatModelProfile profile in profiles)
            {
                if (string.Equals(profile.DisplayName, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    match = profile;
                    return true;
                }
            }

            match = default;
            return false;
        }

        private static string Label(string profileId, IReadOnlyList<ChatModelProfile> profiles)
        {
            if (string.IsNullOrWhiteSpace(profileId))
            {
                return "automatic";
            }

            foreach (ChatModelProfile profile in profiles)
            {
                if (string.Equals(profile.Id, profileId, StringComparison.Ordinal))
                {
                    return profile.DisplayName;
                }
            }

            return profileId;
        }

        private static string ListProfiles(IReadOnlyList<ChatModelProfile> profiles)
        {
            if (profiles.Count == 0)
            {
                return "none yet (create one in Hub Settings)";
            }

            StringBuilder list = new();
            for (int i = 0; i < profiles.Count; i++)
            {
                ChatModelProfile profile = profiles[i];
                list.Append(i == 0 ? "" : ", ").Append(profile.Id);
                bool named = !string.Equals(profile.DisplayName, profile.Id, StringComparison.Ordinal);
                if (named || profile.Status.Length > 0)
                {
                    list.Append(" (");
                    if (named)
                    {
                        list.Append(profile.DisplayName);
                    }

                    if (profile.Status.Length > 0)
                    {
                        list.Append(named ? ", " : "").Append(profile.Status);
                    }

                    list.Append(')');
                }
            }

            return list.ToString();
        }
    }

    /// <summary><c>/stop</c>: stops the agent's running answer.</summary>
    public sealed class StopChatCommand : IChatCommand
    {
        private readonly IChatTurnControl _turns;

        /// <summary>Creates the command over <paramref name="turns"/>.</summary>
        public StopChatCommand(IChatTurnControl turns)
        {
            _turns = turns ?? throw new ArgumentNullException(nameof(turns));
        }

        /// <inheritdoc />
        public string Name => "stop";

        /// <inheritdoc />
        public IReadOnlyList<string> Aliases => Array.Empty<string>();

        /// <inheritdoc />
        public string Usage => "";

        /// <inheritdoc />
        public string Description => "Stop the agent's current answer.";

        /// <inheritdoc />
        public ChatCommandResult Execute(ChatCommandContext context)
        {
            return _turns.StopActiveTurn()
                ? ChatCommandResult.Handled()
                : ChatCommandResult.Handled("Nothing to stop.");
        }
    }
}
