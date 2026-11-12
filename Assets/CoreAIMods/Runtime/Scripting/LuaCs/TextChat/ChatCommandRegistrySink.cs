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

            ScriptCommand command = new(name, secondaryAlias, trigger);
            return _registry.Register(command);
        }

        /// <summary>True when <paramref name="command"/> is a world's <c>TextChatCommand</c> added through a sink.</summary>
        public static bool IsScriptCommand(IChatCommand command)
        {
            return command is ScriptCommand;
        }

        /// <summary>A <c>TextChatCommand</c> in the registry; marked so the host's command list leaves it out.</summary>
        private sealed class ScriptCommand : IChatCommand
        {
            private readonly Action<string> _trigger;

            public ScriptCommand(string name, string secondaryAlias, Action<string> trigger)
            {
                Name = name;
                Aliases = secondaryAlias == null ? Array.Empty<string>() : new[] { secondaryAlias };
                _trigger = trigger;
            }

            public string Name { get; }

            public System.Collections.Generic.IReadOnlyList<string> Aliases { get; }

            public string Usage => "";

            public string Description => ChatCommandRegistrySink.Description;

            public ChatCommandResult Execute(ChatCommandContext context)
            {
                _trigger(context.Invocation.RawText);
                return ChatCommandResult.Handled();
            }
        }
    }
}
