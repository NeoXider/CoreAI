using System;
using System.Collections.Generic;

namespace CoreAI.ChatOverlay
{
    /// <summary>
    /// One slash command of the in-world chat. Commands run on the thread that owns the chat (the Unity main
    /// thread) before anything reaches the model. A command that finishes later (an undo that awaits a world
    /// restore) returns <see cref="ChatCommandResult.Handled"/> at once and posts its outcome through
    /// <see cref="ChatCommandContext.Messages"/> when it is done.
    /// </summary>
    public interface IChatCommand
    {
        /// <summary>Primary name without the prefix, for example <c>agent</c>.</summary>
        string Name { get; }

        /// <summary>Other names that resolve to this command; may be empty.</summary>
        IReadOnlyList<string> Aliases { get; }

        /// <summary>Argument syntax shown by <c>/help</c>, for example <c>&lt;role&gt;</c>; empty when none.</summary>
        string Usage { get; }

        /// <summary>One sentence for <c>/help</c>.</summary>
        string Description { get; }

        /// <summary>Runs the command. An exception is reported as a failed command, never thrown to the chat.</summary>
        ChatCommandResult Execute(ChatCommandContext context);
    }

    /// <summary>What a command asks the chat to do next.</summary>
    public enum ChatCommandOutcome
    {
        /// <summary>Done; the reply, when not empty, is shown as a command line.</summary>
        Handled = 0,

        /// <summary>Refused or failed; the reply is shown as an error line.</summary>
        Failed = 1,

        /// <summary>Send <see cref="ChatCommandResult.ModelText"/> to the model as if the player had typed it.</summary>
        SendToModel = 2
    }

    /// <summary>Result of <see cref="IChatCommand.Execute"/>.</summary>
    public readonly struct ChatCommandResult
    {
        private readonly string _reply;
        private readonly string _modelText;
        private readonly string _roleId;

        private ChatCommandResult(ChatCommandOutcome outcome, string reply, string modelText, string roleId)
        {
            Outcome = outcome;
            _reply = reply ?? "";
            _modelText = modelText ?? "";
            _roleId = roleId ?? "";
        }

        /// <summary>What the chat does next.</summary>
        public ChatCommandOutcome Outcome { get; }

        /// <summary>Text shown in the feed; never null.</summary>
        public string Reply => _reply ?? "";

        /// <summary>Text sent to the model for <see cref="ChatCommandOutcome.SendToModel"/>; never null.</summary>
        public string ModelText => _modelText ?? "";

        /// <summary>Role that answers a <see cref="ChatCommandOutcome.SendToModel"/>; empty means the active role.</summary>
        public string RoleId => _roleId ?? "";

        /// <summary>The command did its work; <paramref name="reply"/> may be empty.</summary>
        public static ChatCommandResult Handled(string reply = "")
        {
            return new ChatCommandResult(ChatCommandOutcome.Handled, reply, "", "");
        }

        /// <summary>The command refused or failed; <paramref name="reply"/> says why.</summary>
        public static ChatCommandResult Failed(string reply)
        {
            return new ChatCommandResult(ChatCommandOutcome.Failed, reply, "", "");
        }

        /// <summary>Sends <paramref name="text"/> to the model, answered by <paramref name="roleId"/> or the active role.</summary>
        public static ChatCommandResult SendToModel(string text, string roleId = "")
        {
            return new ChatCommandResult(ChatCommandOutcome.SendToModel, "", text, roleId);
        }
    }

    /// <summary>Everything a command receives when it runs.</summary>
    public sealed class ChatCommandContext
    {
        /// <summary>Creates a context.</summary>
        public ChatCommandContext(ChatCommandInvocation invocation, IChatCommandRegistry registry,
            IChatOverlayMessages messages, char prefix)
        {
            Invocation = invocation;
            Registry = registry ?? throw new ArgumentNullException(nameof(registry));
            Messages = messages;
            Prefix = prefix;
        }

        /// <summary>The parsed command line.</summary>
        public ChatCommandInvocation Invocation { get; }

        /// <summary>The registry the command was resolved from.</summary>
        public IChatCommandRegistry Registry { get; }

        /// <summary>Feed entry point for replies posted after <see cref="IChatCommand.Execute"/> returned; may be null.</summary>
        public IChatOverlayMessages Messages { get; }

        /// <summary>The command prefix of this chat, for replies that name other commands.</summary>
        public char Prefix { get; }
    }

    /// <summary>
    /// A command built from a delegate, so a game adds one in a line:
    /// <c>registry.Register(new ChatCommand("fly", "Toggle flight.", ctx =&gt; ...));</c>
    /// </summary>
    public sealed class ChatCommand : IChatCommand
    {
        private readonly Func<ChatCommandContext, ChatCommandResult> _handler;

        /// <summary>Creates a command; <paramref name="aliases"/> may be empty.</summary>
        public ChatCommand(string name, string description, Func<ChatCommandContext, ChatCommandResult> handler,
            string usage = "", params string[] aliases)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("A chat command needs a name.", nameof(name));
            }

            Name = name.Trim();
            Description = description ?? "";
            Usage = usage ?? "";
            Aliases = aliases ?? Array.Empty<string>();
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        /// <inheritdoc />
        public string Name { get; }

        /// <inheritdoc />
        public IReadOnlyList<string> Aliases { get; }

        /// <inheritdoc />
        public string Usage { get; }

        /// <inheritdoc />
        public string Description { get; }

        /// <inheritdoc />
        public ChatCommandResult Execute(ChatCommandContext context)
        {
            return _handler(context);
        }
    }
}
