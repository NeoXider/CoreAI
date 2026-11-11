using System;
using CoreAI.ChatOverlay;
using CoreAI.Mods.Rbx.Instances.TextChat;

namespace CoreAI.Ai.LuaCs
{
    /// <summary>
    /// The in-world chat's command registry (<see cref="IChatCommandRegistry"/>) as the command list of a world's
    /// <c>TextChatService</c>: a script's <c>TextChatCommand</c> becomes a slash command of the chat, answered by
    /// firing its <c>Triggered</c> with the whole line, and nothing reaches the model. A command already in the
    /// registry (the game's own, or an earlier script's) is never replaced.
    /// </summary>
    public sealed class ChatCommandRegistrySink : IRbxChatCommandSink
    {
        private const string Description = "A game command (TextChatCommand).";

        private readonly IChatCommandRegistry _registry;

        /// <summary>Adapts <paramref name="registry"/>.</summary>
        public ChatCommandRegistrySink(IChatCommandRegistry registry)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        }

        /// <inheritdoc />
        public bool IsTaken(string alias)
        {
            return _registry.TryResolve(alias, out IChatCommand _);
        }

        /// <inheritdoc />
        public IDisposable Add(string name, string secondaryAlias, Action<string> trigger)
        {
            if (trigger == null)
            {
                throw new ArgumentNullException(nameof(trigger));
            }

            ChatCommand command = secondaryAlias == null
                ? new ChatCommand(name, Description, context => Run(context, trigger))
                : new ChatCommand(name, Description, context => Run(context, trigger), "", secondaryAlias);
            return _registry.Register(command);
        }

        private static ChatCommandResult Run(ChatCommandContext context, Action<string> trigger)
        {
            trigger(context.Invocation.RawText);
            return ChatCommandResult.Handled();
        }
    }
}
