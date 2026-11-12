using System;
using System.Collections.Generic;
using CoreAI.ChatOverlay;
using CoreAI.Logging;
using CoreAI.Mods.Rbx.Instances.TextChat;

namespace CoreAI.Ai.LuaCs
{
    /// <summary>
    /// The host's in-world chat services as a world's <see cref="IRbxChatHost"/>: <c>TextChannel:SendAsync</c> runs a
    /// command of the chat's registry (a host command such as <c>/undo</c>, or a world's <c>TextChatCommand</c>), a
    /// delivered line is offered to the overlay's pre-send hooks (villagers, the self-repair recorder), every line
    /// posted to the overlay's feed by other code (NPC speech, a command result that arrives later, notices) is
    /// posted into the world's chat (and, while a world holds the chat, the finished answers of the overlay's host
    /// turns), and a world that switches the default chat off claims the overlay's surface.
    /// One instance serves every world of a composition; each world subscribes and unsubscribes itself. Runs on
    /// the main thread.
    /// </summary>
    public sealed class ChatOverlayWorldChatHost : IRbxChatHost, IDisposable
    {
        private readonly IChatCommandRegistry _registry;
        private readonly IChatOverlayPlayerLineRouter _router;
        private readonly ChatFeed _feed;
        private readonly IChatOverlaySurfaceSwitch _surface;
        private readonly IChatOverlayMessages _messages;
        private readonly ChatOverlayOptions _options;
        private readonly ILog _log;
        private readonly Action<ChatFeedLine> _onLineAdded;
        private readonly Action<ChatFeedLine> _onLineChanged;
        private readonly Action<ChatFeedLine> _onLineRemoved;
        private readonly HashSet<long> _forwarded = new();
        private readonly Action _onRegistryChanged;
        private bool _disposed;

        /// <summary>
        /// Creates the host. Only <paramref name="registry"/> is required: without a router no line is ever taken,
        /// without a feed no host line arrives, without a surface switch the default chat never steps aside.
        /// </summary>
        /// <param name="registry">The chat's commands.</param>
        /// <param name="router">The overlay's pre-send hooks (the <see cref="ChatOverlayController"/>).</param>
        /// <param name="feed">The overlay's feed, whose posted lines become the world's host lines.</param>
        /// <param name="surface">Who draws the chat.</param>
        /// <param name="messages">Where a command posts a reply that arrives later (the overlay's controller).</param>
        /// <param name="options">The command prefix and the failure texts; null uses the defaults.</param>
        /// <param name="log">Diagnostics.</param>
        public ChatOverlayWorldChatHost(IChatCommandRegistry registry, IChatOverlayPlayerLineRouter router = null,
            ChatFeed feed = null, IChatOverlaySurfaceSwitch surface = null, IChatOverlayMessages messages = null,
            ChatOverlayOptions options = null, ILog log = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _router = router;
            _feed = feed;
            _surface = surface;
            _messages = messages;
            _options = options ?? new ChatOverlayOptions();
            _log = log ?? NullLog.Instance;
            _onLineAdded = OnLineAdded;
            _onLineChanged = OnLineChanged;
            _onLineRemoved = line => _forwarded.Remove(line.Id);
            _onRegistryChanged = () => HostCommandsChanged?.Invoke();
            _registry.Changed += _onRegistryChanged;
            if (_feed != null)
            {
                _feed.LineAdded += _onLineAdded;
                _feed.LineChanged += _onLineChanged;
                _feed.LineRemoved += _onLineRemoved;
            }
        }

        /// <inheritdoc />
        public event Action HostCommandsChanged;

        /// <inheritdoc />
        public event Action<RbxChatHostLine> LinePosted;

        /// <inheritdoc />
        public RbxChatSendResult RunCommand(string text)
        {
            string trimmed = (text ?? "").Trim();
            ChatInputKind kind = ChatCommandParser.Classify(trimmed, _options.CommandPrefix,
                out ChatCommandInvocation invocation, out string modelText);
            if (kind == ChatInputKind.Text)
            {
                return RbxChatSendResult.Deliver(modelText);
            }

            if (kind != ChatInputKind.Command || invocation.Name.Length == 0
                                               || !_registry.TryResolve(invocation.Name, out IChatCommand command))
            {
                return RbxChatSendResult.Deliver();
            }

            ChatCommandResult result;
            try
            {
                result = command.Execute(new ChatCommandContext(invocation, _registry, _messages,
                    _options.CommandPrefix));
            }
            catch (Exception ex)
            {
                _log.Error($"[WorldChat] Command {_options.CommandPrefix}{invocation.Name} threw: {ex}", LogTag.Core);
                return RbxChatSendResult.Command(_options.WithPrefix(_options.CommandFailedFormat)
                    .Replace("{command}", invocation.Name)
                    .Replace("{error}", ex.Message), true);
            }

            switch (result.Outcome)
            {
                case ChatCommandOutcome.Failed:
                    return RbxChatSendResult.Command(result.Reply, true);
                case ChatCommandOutcome.SendToModel:
                    // WHY delivered: the command hands the chat a line to say, which the script's chat then routes
                    // like any line the player typed.
                    return string.IsNullOrWhiteSpace(result.ModelText)
                        ? RbxChatSendResult.Command(result.Reply)
                        : RbxChatSendResult.Deliver(result.ModelText.Trim());
                default:
                    return RbxChatSendResult.Command(result.Reply);
            }
        }

        /// <inheritdoc />
        public bool OfferPlayerLine(string text)
        {
            return !_disposed && _router != null && _router.OfferPlayerLine(text);
        }

        /// <inheritdoc />
        public IReadOnlyList<RbxChatHostCommand> GetHostCommands()
        {
            List<RbxChatHostCommand> commands = new();
            IReadOnlyList<IChatCommand> registered = _registry.Commands;
            for (int i = 0; i < registered.Count; i++)
            {
                IChatCommand command = registered[i];
                if (command == null || ChatCommandRegistrySink.IsScriptCommand(command))
                {
                    continue;
                }

                commands.Add(new RbxChatHostCommand(command.Name, command.Aliases, command.Usage, command.Description));
            }

            return commands;
        }

        /// <inheritdoc />
        public IDisposable ClaimChat(string owner)
        {
            return _surface?.Claim(owner) ?? NoClaim.Instance;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _registry.Changed -= _onRegistryChanged;
            if (_feed != null)
            {
                _feed.LineAdded -= _onLineAdded;
                _feed.LineChanged -= _onLineChanged;
                _feed.LineRemoved -= _onLineRemoved;
            }

            _forwarded.Clear();
        }

        /// <summary>The <c>Metadata</c> a feed line of <paramref name="kind"/> gets in the world's chat.</summary>
        public static string MetadataFor(ChatFeedLineKind kind)
        {
            switch (kind)
            {
                case ChatFeedLineKind.Command:
                    return RbxTextChatMetadata.Command;
                case ChatFeedLineKind.Error:
                    return RbxTextChatMetadata.Error;
                case ChatFeedLineKind.Assistant:
                    return RbxTextChatMetadata.Assistant;
                default:
                    return RbxTextChatMetadata.System;
            }
        }

        private void OnLineAdded(ChatFeedLine line)
        {
            if (_disposed || line == null || line.Kind == ChatFeedLineKind.Player || string.IsNullOrEmpty(line.Text))
            {
                return;
            }

            if (line.Kind == ChatFeedLineKind.Assistant)
            {
                OnLineChanged(line);
                return;
            }

            Post(line);
        }

        // WHY an answer only once it is complete and only while a world holds the chat: with the overlay hidden its
        // feed's answers are the host turns' (wakes), which the world's chat shows; with the overlay shown they are the
        // overlay's own, which it draws itself.
        private void OnLineChanged(ChatFeedLine line)
        {
            if (_disposed || line == null || line.Kind != ChatFeedLineKind.Assistant || line.IsStreaming
                || string.IsNullOrEmpty(line.Text) || _surface == null || _surface.IsDefaultSurfaceShown
                || !_forwarded.Add(line.Id))
            {
                return;
            }

            Post(line);
        }

        private void Post(ChatFeedLine line)
        {
            try
            {
                LinePosted?.Invoke(new RbxChatHostLine(line.Speaker, line.Text, MetadataFor(line.Kind)));
            }
            catch (Exception ex)
            {
                _log.Error($"[WorldChat] A world failed to take a chat line: {ex}", LogTag.Core);
            }
        }

        private sealed class NoClaim : IDisposable
        {
            public static readonly NoClaim Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
