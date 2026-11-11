using System;
using CoreAI.Ai;
using CoreAI.Ai.Logging;
using CoreAI.Ai.LuaCs;
using CoreAI.Ai.SelfRepair;
using CoreAI.Authority;
using CoreAI.ChatOverlay;
using CoreAI.Infrastructure.Llm;
using CoreAI.Logging;
using CoreAI.Mods.WorldPackages;
using CoreAI.Presentation;
using CoreAI.RuntimeUi;
using CoreAI.UiFocus;
using UnityEngine;
using VContainer;
using VContainer.Unity;

namespace CoreAI.Composition
{
    /// <summary>
    /// Settings of the runtime self-repair loop and the in-game mod console (<c>Docs/CoreAIMods/MOD_CONSOLE.md</c>).
    /// Every value is a plain field, so a host sets it in the Inspector or in code before the mods scope builds.
    /// </summary>
    [Serializable]
    public sealed class CoreAiModSelfRepairSettings
    {
        [Header("Runtime self-repair")]
        [Tooltip("By Backend (default) repairs a failing AI-written mod without asking when the model is local "
                 + "(LLMUnity, or LM Studio / llama.cpp on this machine or the local network) and asks first with a "
                 + "paid or unknown backend, so no token is spent before /fixmod; Automatic always repairs; Ask First "
                 + "reports it with a /fixmod hint; Off only reports it.")]
        public LuaModRuntimeRepairMode mode = LuaModRuntimeRepairMode.ByBackend;

        [Tooltip("AI Written Only leaves mods the player wrote and bundled samples alone (they are reported, and "
                 + "/fixmod still repairs them on request); All Mods repairs every failing mod.")]
        public LuaModRuntimeRepairScope scope = LuaModRuntimeRepairScope.AiWrittenOnly;

        [Tooltip("Repair attempts per incident before the loop gives up on the mod.")]
        [Min(1)]
        public int maxAttemptsPerIncident = LuaModRuntimeRepairOptions.DefaultMaxAttemptsPerIncident;

        [Tooltip("Seconds before the second attempt; each later wait is three times longer.")]
        [Min(0f)]
        public float firstBackoffSeconds = (float)LuaModRuntimeRepairOptions.DefaultFirstBackoffSeconds;

        [Tooltip("Seconds a repaired mod must run without an error before the fix counts.")]
        [Min(0f)]
        public float verifySeconds = (float)LuaModRuntimeRepairOptions.DefaultVerifySeconds;

        [Tooltip("A repair turn still running after this many seconds is stopped and counts as a failed attempt.")]
        [Min(1f)]
        public float turnTimeoutSeconds = (float)LuaModRuntimeRepairOptions.DefaultTurnTimeoutSeconds;

        [Tooltip("Repair turns allowed per ten minutes across all mods (the guard against a repair storm).")]
        [Min(1)]
        public int maxRepairTurnsPerTenMinutes = LuaModRuntimeRepairOptions.DefaultMaxTurnsPerWindow;

        [Header("Mod console")]
        [Tooltip("The in-game console of the running mods' logs.")]
        public bool consoleEnabled = true;

        [Tooltip("Opens and closes the console. F9 is taken by the orchestration dashboard; T, Enter and / by the chat.")]
        public KeyCode consoleKey = KeyCode.F8;

        [Tooltip("Log entries the console keeps (a ring: a mod that prints every frame does not grow it).")]
        [Min(16)]
        public int consoleCapacity = LuaModConsoleModel.DefaultCapacity;

        [Tooltip("Rows the console shows at once.")]
        [Range(5, 80)]
        public int consoleRows = CoreAiModConsoleView.DefaultVisibleRows;

        [Header("Chat commands")]
        [Tooltip("Add /console (alias /logs) and /fixmod <mod id> to the in-world chat.")]
        public bool chatCommands = true;

        /// <summary>The engine-free numbers these settings describe.</summary>
        public LuaModRuntimeRepairOptions ToOptions()
        {
            return new LuaModRuntimeRepairOptions
            {
                Mode = mode,
                Scope = scope,
                MaxAttemptsPerIncident = maxAttemptsPerIncident,
                FirstBackoffSeconds = firstBackoffSeconds,
                VerifySeconds = verifySeconds,
                TurnTimeoutSeconds = turnTimeoutSeconds,
                MaxTurnsPerWindow = maxRepairTurnsPerTenMinutes,
                TurnWindowSeconds = 600d
            }.Normalized();
        }
    }

    /// <summary>
    /// Registers the runtime self-repair loop and the in-game mod console in a mods scope, next to
    /// <see cref="CoreAiModsInstaller.RegisterCoreAiMods"/>: one entry point that owns the
    /// <see cref="LuaModSelfRepairCoordinator"/>, the <see cref="LuaModLifecycleLog"/>, the
    /// <see cref="LuaModConsoleModel"/> and its view, and adds <c>/console</c> and <c>/fixmod</c> to the chat of the
    /// parent CoreAI scope. Repair turns run as host turns of that chat; without a chat they run through the
    /// orchestrator; without either, failing mods are only reported.
    /// </summary>
    public static class CoreAiModSelfRepairInstaller
    {
        /// <summary>Name of the console command.</summary>
        public const string ConsoleCommandName = "console";

        /// <summary>Name of the repair command.</summary>
        public const string FixModCommandName = "fixmod";

        /// <summary>Registers the loop and the console; <paramref name="settings"/> null uses the defaults.</summary>
        public static void RegisterCoreAiModSelfRepair(this IContainerBuilder builder,
            CoreAiModSelfRepairSettings settings = null)
        {
            if (builder == null)
            {
                throw new ArgumentNullException(nameof(builder));
            }

            CoreAiModSelfRepairSettings effective = settings ?? new CoreAiModSelfRepairSettings();
            builder.RegisterInstance(effective);
            // WHY the loop is not an entry point itself: entry points are constructed while the container is built,
            // in the scope's Awake, and the loop resolves the mod runtime, which builds the world and its network
            // bridge. A scope must not build a transport in Awake (see CoreAiModsLifetimeScope.Configure), so the
            // pump resolves the loop on its first tick, and anyone who asks for the loop earlier gets it then.
            builder.Register(c => new CoreAiModSelfRepairEntryPoint(c, effective), Lifetime.Singleton).AsSelf();
            builder.RegisterEntryPoint(c => new CoreAiModSelfRepairPump(c), Lifetime.Singleton).AsSelf();
        }
    }

    /// <summary>
    /// Ticks the self-repair loop and builds it on the first tick, so that nothing of the mod world is built while
    /// the scope's container is.
    /// </summary>
    public sealed class CoreAiModSelfRepairPump : ITickable
    {
        private readonly IObjectResolver _container;
        private CoreAiModSelfRepairEntryPoint _loop;

        /// <summary>Keeps <paramref name="container"/> to resolve the loop from on the first tick.</summary>
        public CoreAiModSelfRepairPump(IObjectResolver container)
        {
            _container = container ?? throw new ArgumentNullException(nameof(container));
        }

        /// <inheritdoc />
        public void Tick()
        {
            _loop ??= _container.Resolve<CoreAiModSelfRepairEntryPoint>();
            _loop.Tick();
        }
    }

    /// <summary>
    /// The frame pump of the self-repair loop and the console. Built once by the mods scope; ticks the coordinator,
    /// polls the console key (ignored while a text field or the chat input has the keyboard) and refreshes the view.
    /// While the console is closed a tick allocates nothing.
    /// </summary>
    public sealed class CoreAiModSelfRepairEntryPoint : ITickable, IDisposable
    {
        private readonly CoreAiModSelfRepairSettings _settings;
        private readonly ICoreAiUiFocus _focus;
        private readonly IChatOverlayInputState _chatInput;
        private readonly CoreAiModConsoleKey _consoleKey;
        private readonly CoreAiModConsoleView _view;
        private readonly IDisposable _consoleCommand;
        private readonly IDisposable _fixModCommand;
        private readonly IDisposable _requestHook;
        private readonly IObservableWorldMutationGate _observedGate;

        /// <summary>Builds the loop and the console from <paramref name="container"/>.</summary>
        public CoreAiModSelfRepairEntryPoint(IObjectResolver container, CoreAiModSelfRepairSettings settings)
        {
            if (container == null)
            {
                throw new ArgumentNullException(nameof(container));
            }

            _settings = settings ?? new CoreAiModSelfRepairSettings();
            ILog log = container.ResolveOrDefault<ILog>() ?? Logging.Log.Instance;
            ILuaModRuntime runtime = container.ResolveOrDefault<ILuaModRuntime>();
            ILuaLogService logs = container.ResolveOrDefault<ILuaLogService>();
            IActorIdentityProvider identity = container.ResolveOrDefault<IActorIdentityProvider>()
                                              ?? CoreServicesInstaller.DefaultLocalHostIdentityProvider;
            ActorContext actor = identity.GetActorContext(BuiltInAgentRoleIds.Programmer);
            ChatOverlayController chat = container.ResolveOrDefault<ChatOverlayController>();
            _chatInput = container.ResolveOrDefault<IChatOverlayInputState>();
            _focus = container.ResolveOrDefault<ICoreAiUiFocus>();

            Authorship = new LuaModAuthorshipTracker();
            _observedGate = container.ResolveOrDefault<IConfirmedWorldMutationGate>() as IObservableWorldMutationGate;
            _observedGate?.AddObserver(Authorship);

            if (runtime != null && logs != null)
            {
                Lifecycle = new LuaModLifecycleLog(logs, runtime, actor);
                _observedGate?.AddObserver(Lifecycle);
            }

            LuaModPlayerRequestRecorder recorder = null;
            if (chat != null)
            {
                recorder = new LuaModPlayerRequestRecorder(chat);
                _requestHook = chat.AddPreSendHook(recorder);
            }

            if (runtime != null)
            {
                Func<bool> modelAvailable = ModelProbe(container);
                ILuaModRepairTurnRunner runner = CreateRunner(container, chat, identity, modelAvailable);
                Coordinator = new LuaModSelfRepairCoordinator(
                    runtime,
                    actor,
                    _settings.ToOptions(),
                    Authorship,
                    runner,
                    logs,
                    chat,
                    container.ResolveOrDefault<ILuaModSourceStore>(),
                    () => Time.realtimeSinceStartupAsDouble,
                    recorder != null ? () => recorder.CurrentRequest : null,
                    log,
                    CreatePorts(container, actor));
            }

            if (_settings.consoleEnabled && logs != null)
            {
                Console = new LuaModConsoleModel(Math.Max(16, _settings.consoleCapacity));
                Console.Attach(logs);
                IRuntimeUiHost host = container.ResolveOrDefault<IRuntimeUiHost>();
                if (host != null && host.IsEnabled)
                {
                    _view = new CoreAiModConsoleView(host, Console, _settings.consoleRows);
                }

                _consoleKey = new CoreAiModConsoleKey(_settings.consoleKey);
            }

            IChatCommandRegistry commands = _settings.chatCommands ? container.ResolveOrDefault<IChatCommandRegistry>() : null;
            if (commands != null)
            {
                if (Console != null)
                {
                    _consoleCommand = TryRegister(commands, new ChatCommand(CoreAiModSelfRepairInstaller.ConsoleCommandName,
                        "Show or hide the mod console (" + _settings.consoleKey + ").", _ => ToggleConsole(), "", "logs"), log);
                }

                if (Coordinator != null)
                {
                    _fixModCommand = TryRegister(commands, new ChatCommand(CoreAiModSelfRepairInstaller.FixModCommandName,
                        "Ask the AI to fix a failing mod.", context => ChatCommandResult.Handled(
                            Coordinator.RequestRepair(context == null ? "" : context.Invocation.ArgumentText)),
                        "<mod id>"), log);
                }
            }
        }

        /// <summary>The repair loop; null without a mod runtime.</summary>
        public LuaModSelfRepairCoordinator Coordinator { get; }

        /// <summary>Who wrote each mod's source.</summary>
        public LuaModAuthorshipTracker Authorship { get; }

        /// <summary>The load timeline in the mod log; null without a mod log.</summary>
        public LuaModLifecycleLog Lifecycle { get; }

        /// <summary>The console's model; null when the console is off or there is no mod log.</summary>
        public LuaModConsoleModel Console { get; }

        /// <inheritdoc />
        public void Tick()
        {
            Coordinator?.Tick();
            if (Console == null)
            {
                return;
            }

            if (_consoleKey != null && _consoleKey.WasPressedThisFrame() &&
                (_focus == null || !_focus.HoldsKeyboard) && (_chatInput == null || !_chatInput.IsInputOpen))
            {
                Console.Toggle();
            }

            _view?.Refresh();
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _consoleCommand?.Dispose();
            _fixModCommand?.Dispose();
            _requestHook?.Dispose();
            _observedGate?.RemoveObserver(Authorship);
            if (Lifecycle != null)
            {
                _observedGate?.RemoveObserver(Lifecycle);
            }

            Coordinator?.Dispose();
            Lifecycle?.Dispose();
            _view?.Dispose();
            Console?.Dispose();
        }

        private ChatCommandResult ToggleConsole()
        {
            if (_view == null)
            {
                return ChatCommandResult.Failed("The mod console needs the runtime UI, which is switched off.");
            }

            return ChatCommandResult.Handled(Console.Toggle()
                ? "Mod console opened; " + _settings.consoleKey + " or /console closes it."
                : "Mod console closed.");
        }

        private static IDisposable TryRegister(IChatCommandRegistry commands, IChatCommand command, ILog log)
        {
            if (commands.TryResolve(command.Name, out _))
            {
                log.Info("[CoreAiModSelfRepair] '/" + command.Name + "' is already registered; the game's own command is kept.",
                    LogTag.Composition);
                return null;
            }

            return commands.Register(command);
        }

        private static ILuaModRepairTurnRunner CreateRunner(IObjectResolver container, ChatOverlayController chat,
            IActorIdentityProvider identity, Func<bool> modelAvailable)
        {
            if (chat != null)
            {
                return new ChatOverlayLuaModRepairTurnRunner(chat, modelAvailable);
            }

            IAiOrchestrationService orchestrator = container.ResolveOrDefault<IAiOrchestrationService>();
            return orchestrator == null
                ? null
                : new OrchestratorLuaModRepairTurnRunner(orchestrator, role => identity.GetActorContext(role), modelAvailable);
        }

        /// <summary>
        /// The loop's services from the scope: the backend's cost for the Programmer role, the undo history's repair
        /// face, the runtime's pause, and the Programmer's chat history for the one system-event note of an incident.
        /// </summary>
        private static LuaModSelfRepairPorts CreatePorts(IObjectResolver container, ActorContext actor)
        {
            LuaCsModStack stack = container.ResolveOrDefault<LuaCsModStack>();
            IAgentMemoryStore memory = container.ResolveOrDefault<IAgentMemoryStore>();
            AgentMemoryPolicy memoryPolicy = container.ResolveOrDefault<AgentMemoryPolicy>();
            return new LuaModSelfRepairPorts
            {
                BackendCost = CostProbe(container),
                Undo = container.ResolveOrDefault<IRbxWorldUndoService>() as IRbxWorldRepairUndo,
                Pause = stack == null ? null : (id, reason) => stack.Runtime != null && stack.Runtime.TryQuarantine(actor, id, reason),
                HistoryNote = memory == null ? null : note => NoteInHistory(memory, memoryPolicy, note)
            };
        }

        /// <summary>
        /// The backend's cost for the Programmer role: the Hub's endpoint route of the role when it names its
        /// endpoints, otherwise the global execution mode and HTTP base URL; Unknown without settings.
        /// </summary>
        private static Func<LuaModRepairBackendCost> CostProbe(IObjectResolver container)
        {
            ILlmEndpointRegistry registry = container.ResolveOrDefault<ILlmEndpointRegistry>();
            CoreAISettingsAsset settings = container.ResolveOrDefault<ICoreAISettings>() as CoreAISettingsAsset;
            return () =>
            {
                LuaModRepairBackendCost routed =
                    LuaModRepairBackendCostRules.ClassifyRole(registry, BuiltInAgentRoleIds.Programmer);
                if (routed != LuaModRepairBackendCost.Unknown || settings == null)
                {
                    return routed;
                }

                return LuaModRepairBackendCostRules.Classify(settings.ExecutionMode, settings.ApiBaseUrl);
            };
        }

        /// <summary>
        /// Stores the note under the internal "tool" history role, which the orchestrator replays to the model as
        /// context and the chat panel does not show; nothing when the role keeps no chat history.
        /// </summary>
        private static void NoteInHistory(IAgentMemoryStore memory, AgentMemoryPolicy memoryPolicy, string note)
        {
            bool persist = false;
            if (memoryPolicy != null)
            {
                AgentMemoryPolicy.RoleMemoryConfig config = memoryPolicy.GetRoleConfig(BuiltInAgentRoleIds.Programmer);
                if (!config.WithChatHistory)
                {
                    return;
                }

                persist = config.PersistChatHistory;
            }

            memory.AppendChatMessage(BuiltInAgentRoleIds.Programmer, "tool", note, persist);
        }

        private static Func<bool> ModelProbe(IObjectResolver container)
        {
            CoreAISettingsAsset settings = container.ResolveOrDefault<ICoreAISettings>() as CoreAISettingsAsset;
            if (settings == null)
            {
                return null;
            }

            return () => settings.BackendType != LlmBackendType.Offline &&
                         settings.ExecutionMode != LlmExecutionMode.Offline;
        }
    }
}
