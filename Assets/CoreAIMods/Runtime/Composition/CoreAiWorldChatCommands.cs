using System;
using System.Collections.Generic;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.ChatOverlay;
using CoreAI.Logging;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.WorldPackages;
using Cysharp.Threading.Tasks;
using VContainer;

namespace CoreAI.Composition
{
    /// <summary>
    /// The chat commands the mods package adds to the in-world chat: <c>/undo</c> and <c>/redo</c> take back or put
    /// back the player's latest AI change to the world (<see cref="IRbxWorldUndoService"/>), and <c>/fly</c> toggles
    /// free flight of the local character. Each command answers at once; an undo or redo posts its outcome when the
    /// world restore finished. A command a game registered first under the same name is kept. Dispose to remove them.
    /// </summary>
    public sealed class CoreAiWorldChatCommands : IDisposable
    {
        /// <summary>Name of the undo command.</summary>
        public const string UndoName = "undo";

        /// <summary>Name of the redo command.</summary>
        public const string RedoName = "redo";

        /// <summary>Name of the flight command.</summary>
        public const string FlyName = "fly";

        private readonly IRbxWorldUndoService _undo;
        private readonly Func<ActorContext> _actor;
        private readonly Func<RbxHumanoid> _localHumanoid;
        private readonly ILog _log;
        private readonly List<IDisposable> _handles = new();
        private readonly List<string> _registered = new();
        private bool _disposed;

        /// <summary>
        /// Registers the commands into <paramref name="registry"/>. <paramref name="undo"/> null leaves <c>/undo</c>
        /// and <c>/redo</c> out; <paramref name="localHumanoid"/> null leaves <c>/fly</c> out.
        /// </summary>
        /// <param name="registry">The chat's command registry.</param>
        /// <param name="undo">The world's undo history.</param>
        /// <param name="actor">The player whose AI changes the commands take back (the chat's own actor).</param>
        /// <param name="localHumanoid">The local character's Humanoid now, or null while there is none.</param>
        /// <param name="log">Where a command that could not be registered is reported; null is silent.</param>
        public CoreAiWorldChatCommands(
            IChatCommandRegistry registry,
            IRbxWorldUndoService undo,
            Func<ActorContext> actor,
            Func<RbxHumanoid> localHumanoid,
            ILog log = null)
        {
            if (registry == null)
            {
                throw new ArgumentNullException(nameof(registry));
            }

            _undo = undo;
            _actor = actor ?? throw new ArgumentNullException(nameof(actor));
            _localHumanoid = localHumanoid;
            _log = log;
            if (_undo != null)
            {
                TryAdd(registry, new ChatCommand(UndoName, "Take back your latest AI change to the world.",
                    context => Move(context, true)));
                TryAdd(registry, new ChatCommand(RedoName, "Put back the AI change /undo took back.",
                    context => Move(context, false)));
            }

            if (_localHumanoid != null)
            {
                TryAdd(registry, new ChatCommand(FlyName, "Toggle free flight (Space rises, Left Shift descends).",
                    _ => ToggleFlight()));
            }
        }

        /// <summary>The names this instance registered, for a host or a test.</summary>
        public IReadOnlyList<string> RegisteredNames => _registered;

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (IDisposable handle in _handles)
            {
                handle.Dispose();
            }

            _handles.Clear();
            _registered.Clear();
        }

        /// <summary>The reply to <c>/undo</c> (<paramref name="undo"/> true) or <c>/redo</c>; internal for tests.</summary>
        internal ChatCommandResult Move(ChatCommandContext context, bool undo)
        {
            ActorContext caller = _actor();
            RbxWorldUndoStatus status = _undo.GetStatus(caller.ActorId);
            int available = undo ? status.UndoCount : status.RedoCount;
            if (available == 0)
            {
                return ChatCommandResult.Failed(undo
                    ? "Nothing to undo: none of your AI changes to the world is in the history."
                    : "Nothing to redo: /undo has not taken back an AI change since the last one.");
            }

            bool possible = undo ? status.CanUndo : status.CanRedo;
            if (!possible)
            {
                return ChatCommandResult.Failed(status.Reason.Length > 0
                    ? status.Reason
                    : (undo ? "The latest AI change cannot be undone now." : "The undone change cannot be redone now."));
            }

            RunMoveAsync(caller, undo, context?.Messages).Forget();
            return ChatCommandResult.Handled(undo ? "Undoing your latest AI change..." : "Redoing...");
        }

        /// <summary>The reply to <c>/fly</c>; internal for tests.</summary>
        internal ChatCommandResult ToggleFlight()
        {
            RbxHumanoid humanoid = _localHumanoid();
            if (humanoid == null || humanoid.IsDestroyed)
            {
                return ChatCommandResult.Failed("No character to fly with: the local player has not spawned yet.");
            }

            bool flying = !humanoid.IsFlying;
            if (!humanoid.SetFlying(flying))
            {
                return ChatCommandResult.Failed(flying
                    ? "This character cannot fly: its motor has no flight."
                    : "The character could not leave flight.");
            }

            return ChatCommandResult.Handled(flying
                ? "Flying. Space rises, Left Shift descends; /fly again to land."
                : "Walking.");
        }

        private async UniTaskVoid RunMoveAsync(ActorContext caller, bool undo, IChatOverlayMessages messages)
        {
            RbxWorldUndoResult result;
            try
            {
                result = undo
                    ? await _undo.UndoAsync(caller, CancellationToken.None)
                    : await _undo.RedoAsync(caller, CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                messages?.PostMessage("", undo ? "Undo cancelled." : "Redo cancelled.", ChatFeedLineKind.Error);
                return;
            }
            catch (Exception ex)
            {
                messages?.PostMessage("", (undo ? "Undo failed: " : "Redo failed: ") + ex.Message,
                    ChatFeedLineKind.Error);
                return;
            }

            if (result == null)
            {
                return;
            }

            messages?.PostMessage("", result.Message, result.Success ? ChatFeedLineKind.Command : ChatFeedLineKind.Error);
        }

        private void TryAdd(IChatCommandRegistry registry, IChatCommand command)
        {
            if (registry.TryResolve(command.Name, out _))
            {
                _log?.Info("[CoreAiWorldChatCommands] '/" + command.Name + "' is already registered; the game's own "
                           + "command is kept.", LogTag.Composition);
                return;
            }

            _handles.Add(registry.Register(command));
            _registered.Add(command.Name);
        }
    }

    /// <summary>Registers <see cref="CoreAiWorldChatCommands"/> in a mods scope whose parent has an in-world chat.</summary>
    public static class CoreAiWorldChatCommandsInstaller
    {
        /// <summary>
        /// Adds <c>/undo</c>, <c>/redo</c> and <c>/fly</c> to the chat command registry of this scope or its parent,
        /// once the scope built; nothing when there is no registry (the overlay is switched off). The commands act
        /// as the chat's actor (<see cref="IActorIdentityProvider"/>), whose AI changes the undo history records.
        /// </summary>
        public static void RegisterCoreAiWorldChatCommands(this IContainerBuilder builder)
        {
            builder.Register(c =>
                {
                    IChatCommandRegistry registry = c.ResolveOrDefault<IChatCommandRegistry>();
                    if (registry == null)
                    {
                        return new CoreAiWorldChatCommandsHolder(null);
                    }

                    IActorIdentityProvider identity = c.ResolveOrDefault<IActorIdentityProvider>()
                                                      ?? CoreServicesInstaller.DefaultLocalHostIdentityProvider;
                    RbxWorldRuntimeSessionController session = c.ResolveOrDefault<RbxWorldRuntimeSessionController>();
                    return new CoreAiWorldChatCommandsHolder(new CoreAiWorldChatCommands(
                        registry,
                        c.ResolveOrDefault<IRbxWorldUndoService>(),
                        () => identity.GetActorContext(BuiltInAgentRoleIds.Programmer),
                        session != null ? () => CoreAiLocalPlayer.FindHumanoid(session.CurrentRbxApi) : null,
                        c.ResolveOrDefault<ILog>()));
                },
                Lifetime.Singleton);
            builder.RegisterBuildCallback(container =>
            {
                try
                {
                    container.Resolve<CoreAiWorldChatCommandsHolder>();
                }
                catch (VContainerException)
                {
                    // WHY: minimal containers (tests, headless tools) may omit the world session.
                }
            });
        }

        /// <summary>Owns the commands for the container, which disposes it with the scope.</summary>
        internal sealed class CoreAiWorldChatCommandsHolder : IDisposable
        {
            public CoreAiWorldChatCommandsHolder(CoreAiWorldChatCommands commands)
            {
                Commands = commands;
            }

            public CoreAiWorldChatCommands Commands { get; }

            public void Dispose()
            {
                Commands?.Dispose();
            }
        }
    }
}
