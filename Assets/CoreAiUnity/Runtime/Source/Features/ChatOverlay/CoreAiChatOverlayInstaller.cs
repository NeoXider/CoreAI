using System;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Chat;
using CoreAI.Composition;
using CoreAI.Infrastructure.Logging;
using CoreAI.Logging;
using VContainer;
using VContainer.Unity;

namespace CoreAI.ChatOverlay
{
    /// <summary>
    /// Registers the in-world chat overlay: the command registry, the controller (also exposed as
    /// <see cref="IChatOverlayInputState"/>, <see cref="IChatOverlayMessages"/> and <see cref="IChatTurnControl"/>),
    /// the default view, sender, selectors, keyboard, clock and the entry point, plus the two services a chat drawn
    /// elsewhere (a Lua mod) uses: <see cref="IChatOverlaySurfaceSwitch"/> and <see cref="ChatAgentTurns"/>. A
    /// registration of any of those interfaces made before this call wins, so a game replaces one block without
    /// touching the rest.
    /// </summary>
    public static class CoreAiChatOverlayInstaller
    {
        /// <summary>Registers the overlay services and its entry point; nothing when <see cref="CoreAiChatOverlaySettings.Enabled"/> is false.</summary>
        public static void RegisterCoreAiChatOverlay(this IContainerBuilder builder,
            CoreAiChatOverlaySettings settings = null)
        {
            CoreAiChatOverlaySettings resolved = settings ?? new CoreAiChatOverlaySettings();
            if (!resolved.Enabled || builder.Exists(typeof(CoreAiChatOverlayEntryPoint)))
            {
                return;
            }

            builder.RegisterCoreAiChatOverlayServices(resolved);
            builder.RegisterEntryPoint(c => new CoreAiChatOverlayEntryPoint(
                    c.Resolve<ChatOverlayController>(),
                    c.Resolve<IChatOverlayView>(),
                    c.Resolve<IChatOverlayKeyboard>(),
                    resolved,
                    c.Resolve<IChatAgentSelector>(),
                    c.ResolveOrDefault<IChatModelSelector>(),
                    c.ResolveOrDefault<IChatOverlayOpenGate>(),
                    c.ResolveOrDefault<IGameLogger>(),
                    c.ResolveOrDefault<IChatOverlaySurfaceSwitch>()),
                Lifetime.Singleton);
        }

        /// <summary>
        /// Registers everything except the entry point, for a host that drives <see cref="ChatOverlayController"/>
        /// and the view itself.
        /// </summary>
        public static void RegisterCoreAiChatOverlayServices(this IContainerBuilder builder,
            CoreAiChatOverlaySettings settings = null)
        {
            CoreAiChatOverlaySettings resolved = settings ?? new CoreAiChatOverlaySettings();
            ChatOverlayOptions options = resolved.ResolveOptions();
            if (!builder.Exists(typeof(CoreAiChatOverlaySettings)))
            {
                builder.RegisterInstance(resolved);
            }

            if (!builder.Exists(typeof(IChatCommandRegistry), true))
            {
                builder.Register<IChatCommandRegistry>(_ => new ChatCommandRegistry(), Lifetime.Singleton);
            }

            if (!builder.Exists(typeof(IChatOverlayClock), true))
            {
                builder.Register<IChatOverlayClock>(_ => new UnityChatOverlayClock(), Lifetime.Singleton);
            }

            if (!builder.Exists(typeof(IAgentToolEventSource), true))
            {
                builder.Register<IAgentToolEventSource>(_ => new CoreAiToolEventSource(), Lifetime.Singleton);
            }

            if (!builder.Exists(typeof(IChatOverlayKeyboard), true))
            {
                builder.Register<IChatOverlayKeyboard>(_ => new UnityChatOverlayKeyboard(), Lifetime.Singleton);
            }

            if (!builder.Exists(typeof(IChatAgentSelector), true))
            {
                string initialRole = string.IsNullOrWhiteSpace(options.RoleId)
                    ? CoreAiChatOptions.DefaultRoleId
                    : options.RoleId;
                builder.Register<IChatAgentSelector>(c =>
                {
                    AgentMemoryPolicy policy = c.ResolveOrDefault<AgentMemoryPolicy>();
                    return new ChatAgentSelector(initialRole, () => KnownRoles(policy));
                }, Lifetime.Singleton);
            }

            if (!builder.Exists(typeof(IChatModelSelector), true))
            {
                builder.Register<IChatModelSelector>(c =>
                {
                    ICoreAiRoutingUiController scoped = c.ResolveOrDefault<ICoreAiRoutingUiController>();
                    return scoped != null
                        ? new CoreAiRoutingChatModelSelector(() => scoped)
                        : new CoreAiRoutingChatModelSelector();
                }, Lifetime.Singleton);
            }

            if (!builder.Exists(typeof(IChatOverlayTurnSender), true))
            {
                builder.Register<IChatOverlayTurnSender>(c => CreateTurnSender(c, resolved), Lifetime.Singleton);
            }

            if (!builder.Exists(typeof(IChatOverlayView), true))
            {
                builder.Register<IChatOverlayView>(_ => new CoreAiChatOverlayView(resolved, options),
                    Lifetime.Singleton);
            }

            if (!builder.Exists(typeof(IChatOverlaySurfaceSwitch), true))
            {
                builder.Register<IChatOverlaySurfaceSwitch>(_ => new ChatOverlaySurfaceSwitch(), Lifetime.Singleton);
            }

            if (!builder.Exists(typeof(ChatAgentTurns)))
            {
                builder.Register(c =>
                    {
                        IChatOverlayClock clock = c.Resolve<IChatOverlayClock>();
                        return new ChatAgentTurns(
                            c.Resolve<IChatOverlayTurnSender>(),
                            c.Resolve<IChatAgentSelector>(),
                            c.ResolveOrDefault<IChatModelSelector>(),
                            c.ResolveOrDefault<IAgentToolEventSource>(),
                            options,
                            c.ResolveOrDefault<ILog>(),
                            () => clock.NowSeconds);
                    },
                    Lifetime.Singleton);
            }

            if (!builder.Exists(typeof(ChatOverlayController)))
            {
                builder.Register(c => new ChatOverlayController(
                            options,
                            c.Resolve<IChatOverlayView>(),
                            c.Resolve<IChatOverlayTurnSender>(),
                            c.Resolve<IChatCommandRegistry>(),
                            c.Resolve<IChatAgentSelector>(),
                            c.Resolve<IChatOverlayClock>(),
                            c.ResolveOrDefault<IAgentToolEventSource>(),
                            c.ResolveOrDefault<ILog>()),
                        Lifetime.Singleton)
                    .AsSelf()
                    .As<IChatOverlayInputState>()
                    .As<IChatOverlayMessages>()
                    .As<IChatTurnControl>();
            }
        }

        /// <summary>Built-in roles plus every role the policy knows, without internal routing-only ids.</summary>
        internal static IReadOnlyList<string> KnownRoles(AgentMemoryPolicy policy)
        {
            List<string> roles = new(BuiltInAgentRoleIds.AllBuiltInRoles);
            IReadOnlyList<string> known = policy?.GetKnownRoleIds();
            if (known != null)
            {
                foreach (string role in known)
                {
                    if (string.IsNullOrWhiteSpace(role) || role.StartsWith("__", StringComparison.Ordinal) ||
                        roles.Exists(r => string.Equals(r, role, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    roles.Add(role);
                }
            }

            return roles;
        }

        private static IChatOverlayTurnSender CreateTurnSender(IObjectResolver c, CoreAiChatOverlaySettings settings)
        {
            IGameLogger logger = c.ResolveOrDefault<IGameLogger>();
            IActorIdentityProvider identity = c.ResolveOrDefault<IActorIdentityProvider>() ??
                                              CoreServicesInstaller.DefaultLocalHostIdentityProvider;
            try
            {
                CoreAiChatService service = c.ResolveOrDefault<CoreAiChatService>();
                if (service == null)
                {
                    IAiOrchestrationService orchestrator = c.ResolveOrDefault<IAiOrchestrationService>();
                    if (orchestrator != null)
                    {
                        service = new CoreAiChatService(
                            orchestrator,
                            c.ResolveOrDefault<AgentMemoryPolicy>(),
                            c.ResolveOrDefault<ICoreAISettings>(),
                            c.ResolveOrDefault<IAgentMemoryStore>(),
                            logger,
                            c.ResolveOrDefault<ILlmClient>(),
                            c.ResolveOrDefault<Vision.IAgentCameraService>(),
                            identity,
                            c.ResolveOrDefault<IAgentMemoryScopeProvider>());
                    }
                }

                if (service != null && service.OrchestratorMode == null)
                {
                    service.OrchestratorMode = c.ResolveOrDefault<CoreAI.Ai.SubAgents.OrchestratorChatDriver>();
                }

                return new CoreAiChatServiceTurnSender(service, identity, logger, settings.EnableCameraTool);
            }
            catch (Exception ex)
            {
                // WHY: a scope without an LLM pipeline still gets a working overlay (commands, NPC lines) whose
                // sender says the AI is not connected, instead of failing the whole scope build.
                (logger ?? GameLoggerUnscopedFallback.Instance).LogWarning(GameLogFeature.Composition,
                    $"[CoreAiChatOverlay] No chat service for the overlay: {ex.Message}");
                return new CoreAiChatServiceTurnSender(null, identity, logger, settings.EnableCameraTool);
            }
        }
    }
}
