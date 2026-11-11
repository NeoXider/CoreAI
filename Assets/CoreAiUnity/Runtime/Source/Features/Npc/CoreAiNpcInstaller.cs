using System;
using CoreAI.Authority;
using CoreAI.ChatOverlay;
using CoreAI.Logging;
using UnityEngine;
using VContainer;
using VContainer.Unity;

namespace CoreAI.Ai.Npc
{
    /// <summary>
    /// Registers the NPC block for a Unity scope: the director (<see cref="INpcDirector"/>), the speech sinks (the
    /// in-world chat feed when the chat overlay is present, and the bubble board), the bubble anchors, the clock and
    /// the entry point that ticks them and draws the bubbles. A registration of any of those interfaces made before
    /// this call wins, so a game replaces one block (an arbiter, a line-of-sight query, a moderation filter, a sink)
    /// without touching the rest. A scope without a model pipeline still gets NPCs that speak their fallback lines.
    /// </summary>
    public static class CoreAiNpcInstaller
    {
        /// <summary>Registers the services and the entry point; nothing when <see cref="CoreAiNpcSettings.Enabled"/> is false.</summary>
        public static void RegisterCoreAiNpcs(this IContainerBuilder builder, CoreAiNpcSettings settings = null)
        {
            CoreAiNpcSettings resolved = settings ?? new CoreAiNpcSettings();
            if (!resolved.Enabled || builder.Exists(typeof(CoreAiNpcEntryPoint)))
            {
                return;
            }

            builder.RegisterCoreAiNpcServices(resolved);
            builder.RegisterEntryPoint(c => new CoreAiNpcEntryPoint(
                    c.Resolve<INpcDirector>(),
                    c.Resolve<NpcSpeechBubbleBoard>(),
                    new NpcSpeechBubbleView(c.Resolve<NpcSpeechBubbleBoard>(), c.Resolve<INpcSpeechAnchors>(),
                        resolved.BubbleMaxDistance),
                    resolved,
                    resolved.HearChat ? c.ResolveOrDefault<ChatOverlayController>() : null,
                    c.ResolveOrDefault<IActorIdentityProvider>(),
                    c.ResolveOrDefault<INpcListenerPosition>()),
                Lifetime.Singleton);
        }

        /// <summary>Registers everything except the entry point, for a host that ticks the director and draws bubbles itself.</summary>
        public static void RegisterCoreAiNpcServices(this IContainerBuilder builder, CoreAiNpcSettings settings = null)
        {
            CoreAiNpcSettings resolved = settings ?? new CoreAiNpcSettings();
            if (!builder.Exists(typeof(CoreAiNpcSettings)))
            {
                builder.RegisterInstance(resolved);
            }

            if (!builder.Exists(typeof(INpcClock), true))
            {
                builder.Register<INpcClock>(_ => new UnityNpcClock(), Lifetime.Singleton);
            }

            if (!builder.Exists(typeof(NpcSpeechBubbleBoard)))
            {
                builder.Register(c => new NpcSpeechBubbleBoard(resolved.ResolveBubbleOptions(), c.Resolve<INpcClock>()),
                    Lifetime.Singleton);
            }

            if (!builder.Exists(typeof(INpcSpeechAnchors), true))
            {
                builder.Register(_ => new NpcSpeechAnchorRegistry(), Lifetime.Singleton)
                    .AsSelf()
                    .As<INpcSpeechAnchors>();
            }

            if (!builder.Exists(typeof(INpcSpeechSink), true))
            {
                builder.Register<INpcSpeechSink>(c => CreateSinks(c, resolved), Lifetime.Singleton);
            }

            if (!builder.Exists(typeof(INpcListenerPosition), true))
            {
                // WHY a relay: the listener is the local character, which only the mods scope's world knows; it
                // attaches its source here, so NPCs hear the player by distance once a world exists.
                builder.Register(_ => new NpcListenerPositionRelay(), Lifetime.Singleton)
                    .AsSelf()
                    .As<INpcListenerPosition>();
            }

            if (!builder.Exists(typeof(INpcDirector), true))
            {
                builder.Register<INpcDirector>(c => new NpcDirector(
                        c.ResolveOrDefault<IAiOrchestrationService>() ?? UnavailableAiOrchestrationService.Instance,
                        c.Resolve<INpcSpeechSink>(),
                        resolved.ResolveDirectorOptions(),
                        c.Resolve<INpcClock>(),
                        c.ResolveOrDefault<INpcAddressArbiter>(),
                        c.ResolveOrDefault<INpcLineOfSight>(),
                        c.ResolveOrDefault<INpcActorContextFactory>() ?? new NpcActorContextFactory(
                            c.ResolveOrDefault<IActorIdentityProvider>(),
                            c.ResolveOrDefault<IAgentMemoryScopeProvider>()),
                        c.ResolveOrDefault<INpcTurnPromptBuilder>(),
                        c.ResolveOrDefault<ITextModerationFilter>(),
                        c.ResolveOrDefault<ILog>()),
                    Lifetime.Singleton);
            }
        }

        private static INpcSpeechSink CreateSinks(IObjectResolver c, CoreAiNpcSettings settings)
        {
            CompositeNpcSpeechSink sinks = new(null, c.ResolveOrDefault<ILog>());
            if (settings.PostToChat)
            {
                IChatOverlayMessages messages = c.ResolveOrDefault<IChatOverlayMessages>();
                if (messages != null)
                {
                    sinks.Add(new ChatOverlayNpcSpeechSink(messages));
                }
            }

            if (settings.ShowBubbles)
            {
                sinks.Add(c.Resolve<NpcSpeechBubbleBoard>());
            }

            return sinks;
        }
    }

    /// <summary>
    /// Scope entry point of the NPC block: puts the bubble view on its own document, lets NPCs hear the local
    /// player's chat, and every frame ticks the director and the bubble board and draws the bubbles.
    /// </summary>
    public sealed class CoreAiNpcEntryPoint : IStartable, ITickable, IDisposable
    {
        private readonly INpcDirector _director;
        private readonly NpcSpeechBubbleBoard _board;
        private readonly NpcSpeechBubbleView _view;
        private readonly CoreAiNpcSettings _settings;
        private readonly ChatOverlayController _overlay;
        private readonly IActorIdentityProvider _identity;
        private readonly INpcListenerPosition _listener;
        private NpcSpeechBubbleSurface _surface;
        private NpcChatHearing _hearing;
        private bool _started;
        private bool _disposed;

        /// <summary>Creates the entry point; the last three arguments may be null.</summary>
        public CoreAiNpcEntryPoint(INpcDirector director, NpcSpeechBubbleBoard board, NpcSpeechBubbleView view,
            CoreAiNpcSettings settings, ChatOverlayController overlay = null, IActorIdentityProvider identity = null,
            INpcListenerPosition listener = null)
        {
            _director = director ?? throw new ArgumentNullException(nameof(director));
            _board = board ?? throw new ArgumentNullException(nameof(board));
            _view = view ?? throw new ArgumentNullException(nameof(view));
            _settings = settings ?? new CoreAiNpcSettings();
            _overlay = overlay;
            _identity = identity;
            _listener = listener;
        }

        /// <summary>The bubbles' own document; null before <see cref="Start"/> or without bubbles.</summary>
        public NpcSpeechBubbleSurface Surface => _surface;

        /// <summary>The chat hearing adapter; null when NPCs do not hear the chat.</summary>
        public NpcChatHearing Hearing => _hearing;

        /// <summary>The bubble view.</summary>
        public NpcSpeechBubbleView View => _view;

        /// <inheritdoc />
        public void Start()
        {
            if (_started || _disposed)
            {
                return;
            }

            _started = true;
            if (_settings.ShowBubbles)
            {
                _surface = NpcSpeechBubbleSurface.Create(_settings);
                if (_surface.Root != null)
                {
                    _view.Attach(_surface.Root);
                }
            }

            if (_settings.HearChat && _overlay != null)
            {
                INpcListenerPosition listener = _listener;
                _hearing = new NpcChatHearing(_director, _settings.PlayerId, _settings.PlayerName,
                    listener != null ? () => listener.Current : (Func<NpcPosition?>)null, _identity);
                _hearing.AiAddressAliases = _settings.AiAddressAliases ?? Array.Empty<string>();
                if (_settings.AddressAiByName)
                {
                    ChatOverlayController overlay = _overlay;
                    _hearing.AiDisplayName = () => AssistantName(overlay);
                }

                _hearing.AttachTo(_overlay);
            }
        }

        /// <summary>The name the overlay shows before the AI's lines, for the current role.</summary>
        internal static string AssistantName(ChatOverlayController overlay)
        {
            string template = overlay?.Options?.AssistantSpeakerName ?? "";
            return template.Replace("{role}", overlay?.ActiveRoleId ?? "").Trim();
        }

        /// <inheritdoc />
        public void Tick()
        {
            if (!_started || _disposed)
            {
                return;
            }

            _director.Tick();
            _board.Tick();
            if (_settings.ShowBubbles)
            {
                _view.Render(_settings.Camera != null ? _settings.Camera : Camera.main);
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _hearing?.Dispose();
            _hearing = null;
            _view.Dispose();
            _surface?.Dispose();
            _surface = null;
        }
    }
}
