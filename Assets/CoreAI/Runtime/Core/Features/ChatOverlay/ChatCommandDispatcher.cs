using System;
using CoreAI.Logging;

namespace CoreAI.ChatOverlay
{
    /// <summary>What the chat does with one submitted line.</summary>
    public enum ChatDispatchKind
    {
        /// <summary>Nothing to do (an empty line).</summary>
        Ignored = 0,

        /// <summary>A command ran; its reply (may be empty) is shown locally.</summary>
        Handled = 1,

        /// <summary>A command refused or failed; the reply is shown locally as an error.</summary>
        Failed = 2,

        /// <summary>No command has that name; the reply is shown locally and nothing reaches the model.</summary>
        UnknownCommand = 3,

        /// <summary>The text goes to the model.</summary>
        SendToModel = 4
    }

    /// <summary>Decision of <see cref="ChatCommandDispatcher.Dispatch"/>.</summary>
    public readonly struct ChatDispatchResult
    {
        private readonly string _reply;
        private readonly string _modelText;
        private readonly string _roleId;
        private readonly string _commandName;

        /// <summary>Creates a result.</summary>
        public ChatDispatchResult(ChatDispatchKind kind, string reply = "", string modelText = "", string roleId = "",
            string commandName = "")
        {
            Kind = kind;
            _reply = reply ?? "";
            _modelText = modelText ?? "";
            _roleId = roleId ?? "";
            _commandName = commandName ?? "";
        }

        /// <summary>What to do.</summary>
        public ChatDispatchKind Kind { get; }

        /// <summary>Local reply; never null.</summary>
        public string Reply => _reply ?? "";

        /// <summary>Text for the model when <see cref="Kind"/> is <see cref="ChatDispatchKind.SendToModel"/>.</summary>
        public string ModelText => _modelText ?? "";

        /// <summary>Role that answers; empty means the active role.</summary>
        public string RoleId => _roleId ?? "";

        /// <summary>The command name as typed, for a command line; empty otherwise.</summary>
        public string CommandName => _commandName ?? "";
    }

    /// <summary>
    /// Routes one submitted line: commands are parsed and run locally before anything reaches the model, an
    /// unknown command answers locally, and only plain text (or a command that asks for it) is sent on.
    /// </summary>
    public sealed class ChatCommandDispatcher
    {
        private readonly IChatCommandRegistry _registry;
        private readonly ChatOverlayOptions _options;
        private readonly IChatOverlayMessages _messages;
        private readonly ILog _log;

        /// <summary>Creates a dispatcher over <paramref name="registry"/>.</summary>
        public ChatCommandDispatcher(IChatCommandRegistry registry, ChatOverlayOptions options = null,
            IChatOverlayMessages messages = null, ILog log = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _options = options ?? new ChatOverlayOptions();
            _messages = messages;
            _log = log ?? NullLog.Instance;
        }

        /// <summary>Decides what <paramref name="text"/> is and runs it when it is a command.</summary>
        public ChatDispatchResult Dispatch(string text)
        {
            char prefix = _options.CommandPrefix;
            ChatInputKind kind = ChatCommandParser.Classify(text, prefix, out ChatCommandInvocation invocation,
                out string modelText);
            if (kind == ChatInputKind.Empty)
            {
                return new ChatDispatchResult(ChatDispatchKind.Ignored);
            }

            if (kind == ChatInputKind.Text)
            {
                return new ChatDispatchResult(ChatDispatchKind.SendToModel, modelText: modelText);
            }

            string name = invocation.Name;
            if (name.Length == 0)
            {
                return new ChatDispatchResult(ChatDispatchKind.UnknownCommand,
                    _options.WithPrefix(_options.EmptyCommandText));
            }

            if (!_registry.TryResolve(name, out IChatCommand command))
            {
                return new ChatDispatchResult(ChatDispatchKind.UnknownCommand,
                    _options.WithPrefix(_options.UnknownCommandFormat).Replace("{command}", name),
                    commandName: name);
            }

            ChatCommandResult result;
            try
            {
                result = command.Execute(new ChatCommandContext(invocation, _registry, _messages, prefix));
            }
            catch (Exception ex)
            {
                _log.Error($"[ChatOverlay] Command {prefix}{name} threw: {ex}", LogTag.Core);
                return new ChatDispatchResult(ChatDispatchKind.Failed,
                    _options.WithPrefix(_options.CommandFailedFormat)
                        .Replace("{command}", name)
                        .Replace("{error}", ex.Message),
                    commandName: name);
            }

            switch (result.Outcome)
            {
                case ChatCommandOutcome.Failed:
                    return new ChatDispatchResult(ChatDispatchKind.Failed, result.Reply, commandName: name);
                case ChatCommandOutcome.SendToModel:
                    if (string.IsNullOrWhiteSpace(result.ModelText))
                    {
                        return new ChatDispatchResult(ChatDispatchKind.Failed,
                            _options.WithPrefix(_options.CommandFailedFormat)
                                .Replace("{command}", name)
                                .Replace("{error}", "it produced no text for the AI"),
                            commandName: name);
                    }

                    return new ChatDispatchResult(ChatDispatchKind.SendToModel, result.Reply,
                        result.ModelText.Trim(), result.RoleId, name);
                default:
                    return new ChatDispatchResult(ChatDispatchKind.Handled, result.Reply, commandName: name);
            }
        }
    }
}
