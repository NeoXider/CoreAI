using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using CoreAI.Ai.Logging;
using CoreAI.Authority;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using CoreAI.Mods.Rbx.Instances.Scheduling;
using CoreAI.Sandbox.LuaCs;
using CoreAI.Scripting;
using CoreAI.Scripting.LuaCs;
using Lua;
using Lua.Runtime;
using static CoreAI.Ai.LuaCs.LuaCsRbxLua;

namespace CoreAI.Ai.LuaCs
{
    /// <summary>
    /// Installs the Roblox MVP1 Lua surface (roadmap §5.1.3) into mod environments: datatype
    /// constructor globals (<c>Vector3</c>/<c>Vector2</c>/<c>CFrame</c>/<c>Color3</c>/<c>UDim</c>/
    /// <c>UDim2</c>/<c>Random</c>), the <c>Enum</c> registry, <c>Instance.new</c> over the
    /// scripted-creation whitelist, and the <c>game</c>/<c>workspace</c> globals over one shared
    /// <see cref="InstanceRegistry"/> world. One bindings instance is shared by every mod and the
    /// one-off executor (like <see cref="LuaCsGameplayBindings"/>), so all scripts see one world;
    /// each registration gets its own capability/ownership context. Registration goes through the
    /// <see cref="IScriptFunctionRegistry"/> seam; value globals use the Lua-CSharp registry's
    /// engine-specific value escape hatch, which is why this class lives in the adapter layer.
    /// </summary>
    public sealed class LuaCsRbxApiBindings : IDisposable
    {
        private const double LegacySchedulerMinimumDelaySeconds = 0.029d;
        internal const double RemoteFunctionInvokeTimeoutSeconds = 30d;

        /// <summary>
        /// Handler threads one remote sender may keep alive at once on this machine (MP-10): the
        /// OnServerInvoke callbacks and OnServerEvent handlers its calls started that are still
        /// suspended. They are charged to the sender, never to the handler's owner, so a flooding client
        /// exhausts only this budget; a call over it is refused (RemoteFunction) or dropped and counted
        /// (RemoteEvent) without faulting the handler's mod.
        /// </summary>
        internal const int MaxRemoteHandlerThreadsPerSender = 32;

        private sealed class ExecutingScriptBacking
        {
            public ExecutingScriptBacking(RbxInstance container, RbxInstance script)
            {
                Container = container;
                Script = script;
            }

            public RbxInstance Container { get; }

            public RbxInstance Script { get; }
        }

        private sealed class RemoteFunctionCallbackRegistration
        {
            public RemoteFunctionCallbackRegistration(LuaCsRbxModContext context,
                IScriptState ownerState, LuaValue callback)
            {
                Context = context ?? throw new ArgumentNullException(nameof(context));
                OwnerState = ownerState ?? throw new ArgumentNullException(nameof(ownerState));
                Callback = callback;
            }

            public LuaCsRbxModContext Context { get; }

            public IScriptState OwnerState { get; }

            public LuaValue Callback { get; }
        }

        internal sealed class ModLoadCandidate
        {
            public ModLoadCandidate(string ownerModId, bool hadPreviousGeneration,
                int previousGeneration, HashSet<RbxScriptConnection> existingConnections,
                bool hadExecutingScriptBacking)
            {
                OwnerModId = ownerModId;
                HadPreviousGeneration = hadPreviousGeneration;
                PreviousGeneration = previousGeneration;
                ExistingConnections = existingConnections;
                HadExecutingScriptBacking = hadExecutingScriptBacking;
            }

            public string OwnerModId { get; }

            public bool HadPreviousGeneration { get; }

            public int PreviousGeneration { get; }

            public HashSet<RbxScriptConnection> ExistingConnections { get; }

            public bool HadExecutingScriptBacking { get; }
        }

        private readonly InstanceRegistry _registry;
        private readonly RbxDataModel _game;
        private readonly RbxInstance _workspace;
        private readonly RbxEnumRegistry _enums;
        private readonly IPartPropertySink _partSink;
        private readonly IRbxCameraRig _cameraRig;
        private readonly RbxUserInputService _userInputService;
        private readonly RbxRunService _runService;
        private readonly RbxDebris _debris;
        private readonly RbxCollectionService _collectionService;
        private readonly RbxTweenService _tweenService;
        private readonly LuaCsTweenPropertyHost _tweenPropertyHost;
        private readonly RbxWorldPhysics _worldPhysics;
        private Func<RbxHumanoid, IRbxCharacterMotor> _characterMotorFactory;
        private readonly Dictionary<RbxHumanoid, IRbxCharacterMotor> _characterMotors = new();
        private readonly List<RbxHumanoid> _motorRefreshScratch = new();
        private readonly IClickPickSource _pickSource;
        private readonly ModConnectionRegistry _connections;
        private readonly LuaCsRbxScriptThreadFactory _schedulerThreadFactory;
        private readonly ModScheduler _scheduler;
        private readonly Dictionary<string, string> _resumeOperationByMod = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _originTagByMod = new(StringComparer.Ordinal);
        private readonly IRbxClockSource _clockSource;
        private readonly object _serverTimeGate = new();
        private double _lastServerTimeNow;
        private double _lastServerTimeProcessSeconds;
        private bool _serverTimeBased;
        private readonly INetworkBridge _networkBridge;
        private readonly RbxPlayers _players;
        private readonly LuaCsRbxNetworkCodec _networkCodec;
        private readonly RbxScriptSignal _networkRequestSignal;
        private readonly RbxScriptConnection _networkRequestConnection;
        private readonly Dictionary<InstanceId, RemoteFunctionCallbackRegistration>
            _serverRemoteCallbacks = new();
        private readonly Dictionary<InstanceId,
            Dictionary<string, RemoteFunctionCallbackRegistration>>
            _clientRemoteCallbacks = new();
        private readonly Dictionary<IRbxScriptThread, long>
            _remoteFunctionWaitGenerations = new();
        private readonly HashSet<string> _legacySchedulerDeprecationOwners =
            new(StringComparer.Ordinal);
        private readonly HashSet<string> _remoteRefusalLoggedSenders = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Sender, NetworkWarningKind Kind), NetworkWarningWindow>
            _networkWarningWindows = new();
        private readonly Dictionary<string, Dictionary<int, HashSet<IRbxScriptThread>>>
            _scheduledThreadsByMod = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _currentSchedulerGenerationByMod =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, ExecutingScriptBacking> _executingScriptsByMod =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, ActorContext> _actorContextsByOwnerModId =
            new(StringComparer.Ordinal);
        private readonly Action<string> _log;
        private ILuaLogService _modLog;
        private CoreAI.Ai.IInGameLlmChatServiceFactory _chatFactory;
        private int _consoleInvocationCounter;
        private double _runServiceElapsed;
        private bool _disposed;

        private bool _mouseButton1Down;

        /// <summary>
        /// Creates the bindings over an existing world, or bootstraps a fresh MVP1 game tree when
        /// <paramref name="registry"/>/<paramref name="game"/> are omitted.
        /// <paramref name="partSink"/> receives BasePart spatial/appearance writes and stores the
        /// Roblox-space <see cref="PartProperties"/> the Lua layer reads back; pass the live
        /// <see cref="InstanceGameObjectBinder"/> (which is both binder and sink) to materialize
        /// parts as GameObjects, or omit it for the headless in-memory default.
        /// <paramref name="cameraRig"/> backs workspace.CurrentCamera and the camera_* globals;
        /// pass the host's <see cref="UnityCameraRig"/> to drive the real camera, or omit it for
        /// the headless in-memory default.
        /// <paramref name="inputSource"/> backs game:GetService("UserInputService"); pass the
        /// host's <see cref="UnityNewInputSource"/> to read real devices, or omit it for the
        /// headless in-memory default (tests drive it directly).
        /// <paramref name="clockSource"/> backs every Lua-visible clock; pass a game-owned
        /// source to redefine time, or omit it for the production system-clock default whose
        /// scaled game time delegates to the scheduler's clock.
        /// <paramref name="defaultCharacterAutoLoads"/> seeds <c>Players.CharacterAutoLoads</c>
        /// before any actor can join (F7): a script cannot reliably race the first join to flip
        /// the flag, so a host that wants auto-spawn off from the start configures it here instead.
        /// </summary>
        /// <param name="coroutineResumeBudget">
        /// Optional composition override for the per-resume budget every guarded coroutine arms by
        /// default (instruction-step cap and wall-clock cap) — see
        /// <see cref="CoreAI.Sandbox.LuaCs.LuaCsCoroutineBudgetSettings"/>. Null builds a settings
        /// object holding CoreAI's documented defaults. The instance this bindings ends up with,
        /// whichever it is, is exposed as <see cref="CoroutineResumeBudget"/> — the SAME object every
        /// scheduler-built coroutine handle in this world reads live, and the object
        /// <c>ScriptContext:SetTimeout</c> mutates.
        /// </param>
        public LuaCsRbxApiBindings(InstanceRegistry registry = null, RbxDataModel game = null,
            RbxEnumRegistry enums = null, Action<string> log = null, IPartPropertySink partSink = null,
            IRbxCameraRig cameraRig = null, IInputSource inputSource = null,
            ModConnectionRegistry connections = null, IClickPickSource pickSource = null,
            IRbxRuntimeObservabilitySink observability = null,
            INetworkBridge networkBridge = null, Func<DateTimeOffset> utcNowProvider = null,
            IRbxClockSource clockSource = null, bool defaultCharacterAutoLoads = true,
            LuaCsCoroutineBudgetSettings coroutineResumeBudget = null)
        {
            _registry = registry ?? new InstanceRegistry();
            _connections = connections ?? new ModConnectionRegistry();
            IRbxRuntimeObservabilitySink resolvedObservability =
                observability != null && observability.IsEnabled
                ? observability
                : null;
            CoroutineResumeBudget = coroutineResumeBudget ?? new LuaCsCoroutineBudgetSettings();
            _schedulerThreadFactory = new LuaCsRbxScriptThreadFactory(
                observability: resolvedObservability,
                resumeEnvelope: ResumeSchedulerThread,
                coroutineResumeBudget: CoroutineResumeBudget);
            _scheduler = new ModScheduler(
                _schedulerThreadFactory, new RbxAccumulatingTimeSource());
            // WHY: every Lua-visible clock reads through one injectable source, so a game with
            // accelerated days, a deterministic replay, or a server-synced session redefines time
            // by supplying its own source; the default's scaled game time delegates to the
            // scheduler's clock and tests force the source backwards to prove monotonicity.
            _clockSource = clockSource
                ?? new RbxSystemClockSource(
                    gameTimeSecondsReader: () => _scheduler.CurrentTime,
                    utcNowProvider: utcNowProvider);
            // WHY: no camera/physics behind the headless default, so clicks resolve to nothing until
            // a live UnityClickPickSource is wired at composition (mirrors the camera-rig default).
            _pickSource = pickSource ?? new InMemoryClickPickSource();
            _game = game ?? DataModelBootstrap.CreateGame(_registry);
            _workspace = _game.FindFirstChildOfClass("Workspace")
                         ?? throw new ArgumentException(
                             "the game tree has no Workspace child", nameof(game));
            _enums = enums ?? RbxEnumRegistry.CreateWithBuiltins();
            _networkBridge = networkBridge ?? new NullNetworkBridge();
            _players = _game.FindFirstChildOfClass("Players") as RbxPlayers;
            if (_players == null && _registry.Catalog.TryGet("Players", out _))
            {
                _players = (RbxPlayers)_registry.Create("Players");
                _players.Parent = _game;
            }

            if (_players == null)
            {
                throw new ArgumentException(
                    "the game tree has no Players service", nameof(game));
            }

            // WHY set here, before Scheduler/PartPositionReader are wired below and long before any
            // actor can join: this is the one point in composition guaranteed to run before
            // EnsureActor could possibly fire (F7). A script cannot beat this.
            _players.CharacterAutoLoads = defaultCharacterAutoLoads;

            _networkCodec = new LuaCsRbxNetworkCodec(_registry, _enums, log);
            // WHY the registry-aware sink: it releases a destroyed part's live state itself and keeps a
            // bounded last-known copy, so a headless world neither grows forever nor answers a
            // Destroying handler with default values.
            _partSink = partSink ?? new InMemoryPartPropertySink(_registry);
            _cameraRig = cameraRig ?? new InMemoryCameraRig();
            _log = log;
            if (registry == null || partSink == null)
            {
                _log?.Invoke(
                    "[CoreAI.RbxApi] Headless mode: " +
                    (registry == null ? "no InstanceRegistry " : "") +
                    (partSink == null ? "no part materialiser (InstanceGameObjectBinder)" : "") +
                    " — Instance.new creates data-model instances but nothing renders in the scene. " +
                    "If this is a player build, check link.xml preserves CoreAI.RbxApi.* assemblies and that " +
                    "RbxWorldHost is wired on CoreAiModsLifetimeScope.");
            }

            // WHY: worlds bootstrapped before the input slice (older snapshots / external trees)
            // may lack the service; creating it here keeps game:GetService("UserInputService")
            // resolvable for every world this bindings instance fronts.
            _userInputService = _game.FindFirstChildOfClass("UserInputService") as RbxUserInputService;
            if (_userInputService == null
                && _registry.Catalog.TryGet("UserInputService", out _))
            {
                _userInputService = (RbxUserInputService)_registry.Create("UserInputService");
                _userInputService.Parent = _game;
            }

            if (_userInputService != null)
            {
                _userInputService.AttachEnums(_enums);
                _userInputService.AttachInputSource(inputSource);
            }

            // WHY: same rationale as UserInputService — worlds bootstrapped before the game-loop
            // slice may lack RunService; create it here so game:GetService("RunService") and the
            // per-frame Heartbeat pump resolve for every world this bindings instance fronts.
            _runService = _game.FindFirstChildOfClass("RunService") as RbxRunService;
            if (_runService == null && _registry.Catalog.TryGet("RunService", out _))
            {
                _runService = (RbxRunService)_registry.Create("RunService");
                _runService.Parent = _game;
            }

            // WHY: same rationale as RunService — worlds bootstrapped before the Debris slice may
            // lack the service; create it here so game:GetService("Debris") resolves, then attach
            // the scheduler host timer and the mod log sink so AddItem can schedule and report.
            _debris = _game.FindFirstChildOfClass("Debris") as RbxDebris;
            if (_debris == null && _registry.Catalog.TryGet("Debris", out _))
            {
                _debris = (RbxDebris)_registry.Create("Debris");
                _debris.Parent = _game;
            }

            if (_debris != null)
            {
                _debris.AttachHost(_scheduler, _log);
            }

            // WHY: same rationale as Debris — worlds bootstrapped before the CollectionService
            // slice may lack the service; create it here so game:GetService("CollectionService")
            // resolves, then attach the registry tag-transition subscriptions so the signals fire.
            _collectionService =
                _game.FindFirstChildOfClass("CollectionService") as RbxCollectionService;
            if (_collectionService == null
                && _registry.Catalog.TryGet("CollectionService", out _))
            {
                _collectionService = (RbxCollectionService)_registry.Create("CollectionService");
                _collectionService.Parent = _game;
            }

            if (_collectionService != null)
            {
                _collectionService.AttachHost(_scheduler);
            }

            // WHY: same rationale as Debris — worlds bootstrapped before the TweenService
            // slice may lack the service; create it here so game:GetService("TweenService")
            // resolves, then attach the Heartbeat driver, the property host, and the
            // PlaybackState item resolver so Create/Play/Completed all work.
            // WHY a reader for world physics: physics is built further down, and the host reads it on
            // every spatial write, so a tweened move reaches the teleport note of the live physics port.
            _tweenPropertyHost = new LuaCsTweenPropertyHost(
                _partSink, _registry, () => _worldPhysics, _cameraRig);
            _tweenService =
                _game.FindFirstChildOfClass("TweenService") as RbxTweenService;
            if (_tweenService == null
                && _registry.Catalog.TryGet("TweenService", out _))
            {
                _tweenService = (RbxTweenService)_registry.Create("TweenService");
                _tweenService.Parent = _game;
            }

            if (_tweenService != null)
            {
                _tweenService.AttachHost(
                    _scheduler, _tweenPropertyHost, ResolvePlaybackStateItem, _log);
            }

            // WHY constructed unconditionally, unlike the services above: world physics is not an
            // instance in the tree, and a world with no engine adapter still has to answer
            // workspace:Raycast (with a miss) and remember a scripted Workspace.Gravity until a host
            // attaches one. The null port is that answer.
            _worldPhysics = new RbxWorldPhysics(_registry);
            // WHY every Humanoid is wired on registration rather than on first use: a Humanoid that
            // has no scheduler silently never times out a MoveTo and never changes state, and the
            // script that created it has no way to notice.
            _registry.Registered += OnInstanceRegisteredForCharacter;
            _registry.SceneMembershipChanged += OnCharacterSceneMembershipChanged;

            if (_userInputService != null)
            {
                _userInputService.InputBegan.BindScheduler(_scheduler);
                _userInputService.InputEnded.BindScheduler(_scheduler);
                _userInputService.InputChanged.BindScheduler(_scheduler);
            }

            if (_runService != null)
            {
                // WHY every one, including the modern names: a signal with no scheduler refuses a
                // C# Connect outright, and a host listening for PreRender is as legitimate as a mod
                // doing it. Binding only the three legacy signals made the modern four Lua-only.
                _runService.Heartbeat.BindScheduler(_scheduler);
                _runService.Stepped.BindScheduler(_scheduler);
                _runService.RenderStepped.BindScheduler(_scheduler);
                _runService.PreAnimation.BindScheduler(_scheduler);
                _runService.PreSimulation.BindScheduler(_scheduler);
                _runService.PostSimulation.BindScheduler(_scheduler);
                _runService.PreRender.BindScheduler(_scheduler);
            }

            _players.PlayerAdded.BindScheduler(_scheduler);
            _players.PlayerRemoving.BindScheduler(_scheduler);
            _players.Scheduler = _scheduler;
            _players.NetworkBridge = _networkBridge;
            _players.PartPositionReader = ReadPartPositionStuds;
            _players.RootPartSpawnSeeder = SeedCharacterRootPart;
            _networkRequestSignal = new RbxScriptSignal("NetworkBridge.RequestReceived");
            _networkRequestSignal.BindScheduler(_scheduler);
            _networkRequestConnection = _networkRequestSignal.Connect(
                (Action<object[]>)DeliverNetworkRequest);
            _networkBridge.EventReceived += DeliverNetworkEvent;
            _networkBridge.RequestReceived += QueueNetworkRequest;
            _registry.Unregistered += OnInstanceUnregistered;

            _scheduler.PhaseReached += PumpSchedulerPhase;
            _scheduler.ThreadRetired += OnSchedulerThreadRetired;

            // WHY: a restored world registers its Humanoids before these bindings exist, so the
            // Registered wiring above never sees them. A headless host attaches no motor factory to
            // sweep them later, which left every restored Humanoid with no scheduler at all.
            WireExistingCharacterHumanoids();

            // WHY: Roblox default; a custom enum registry without CameraType simply reads nil.
            if (_enums.TryGet("CameraType", out RbxEnum cameraType)
                && cameraType.TryGetItem("Custom", out RbxEnumItem custom))
            {
                CameraTypeItem = custom;
            }
        }

        /// <summary>The shared instance world every registered script operates on.</summary>
        public InstanceRegistry Registry => _registry;

        /// <summary>The shared DataModel root exposed as the <c>game</c> global.</summary>
        public RbxDataModel Game => _game;

        /// <summary>The shared enum registry exposed as the <c>Enum</c> global.</summary>
        public RbxEnumRegistry Enums => _enums;

        /// <summary>Sink that stores BasePart spatial/appearance state the Lua layer reads and writes.</summary>
        public IPartPropertySink PartSink => _partSink;

        /// <summary>Camera seam behind workspace.CurrentCamera and the camera_* globals.</summary>
        public IRbxCameraRig CameraRig => _cameraRig;

        /// <summary>The shared UserInputService instance (input signals + poll surface).</summary>
        public RbxUserInputService UserInputService => _userInputService;

        /// <summary>The shared RunService instance (Heartbeat/Stepped/RenderStepped signals).</summary>
        public RbxRunService RunService => _runService;

        /// <summary>The shared Debris instance (scheduled guaranteed destruction).</summary>
        public RbxDebris Debris => _debris;

        /// <summary>The shared CollectionService instance (tag collections and signals).</summary>
        public RbxCollectionService CollectionService => _collectionService;

        /// <summary>The shared TweenService instance (scaled-time property tweens).</summary>
        public RbxTweenService TweenService => _tweenService;

        /// <summary>World queries, gravity and contact relay; the host attaches the engine port.</summary>
        public RbxWorldPhysics WorldPhysics => _worldPhysics;

        /// <summary>
        /// Supplies the character controller behind every <c>Humanoid</c>. Without one, humanoids
        /// keep their health and state but never move.
        /// </summary>
        /// <remarks>
        /// WHY a factory and not one motor: each Humanoid drives its own character, and the host
        /// decides what that is — the bundled motor, its own controller, or nothing at all in a
        /// headless world.
        /// </remarks>
        public void AttachCharacterMotorFactory(Func<RbxHumanoid, IRbxCharacterMotor> factory)
        {
            _characterMotorFactory = factory;
            IReadOnlyList<RbxInstance> live = _registry.GetLiveInstances();
            for (int index = 0; index < live.Count; index++)
            {
                if (live[index] is RbxHumanoid humanoid)
                {
                    AttachCharacterMotor(humanoid);
                }
            }
        }

        private void OnInstanceRegisteredForCharacter(InstanceRecord record)
        {
            if (record.Instance is RbxHumanoid humanoid)
            {
                WireCharacterHumanoid(humanoid);
            }
        }

        /// <summary>Wires every Humanoid that was already registered when these bindings were built.</summary>
        private void WireExistingCharacterHumanoids()
        {
            IReadOnlyList<RbxInstance> live = _registry.GetLiveInstances();
            for (int index = 0; index < live.Count; index++)
            {
                if (live[index] is RbxHumanoid humanoid)
                {
                    WireCharacterHumanoid(humanoid);
                }
            }
        }

        /// <summary>
        /// Gives a Humanoid its scheduler, motor and respawn-on-death wiring, once per Humanoid.
        /// </summary>
        private void WireCharacterHumanoid(RbxHumanoid humanoid)
        {
            // WHY keyed on the motor table: every wired Humanoid enters it and leaves it only when it
            // unregisters or these bindings are disposed, so the registration path and the attach-time
            // sweep can never both wire one Humanoid. A second pass would add a second Died
            // connection and queue a second respawn timer for every death.
            if (humanoid.IsDestroyed || _characterMotors.ContainsKey(humanoid))
            {
                return;
            }

            AttachCharacterMotor(humanoid);
            WireRespawnOnDeath(humanoid);
        }

        /// <summary>
        /// Reloads a player's character <see cref="RbxPlayers.RespawnTime"/> seconds after its
        /// Humanoid dies (F8) — the mirror's respawn-on-death behavior RespawnTime otherwise had no
        /// consumer for. A no-op for a Humanoid that is not part of any player's Character.
        /// </summary>
        private void WireRespawnOnDeath(RbxHumanoid humanoid)
        {
            humanoid.Died.Connect((Action<object[]>)(_ =>
            {
                RbxInstance character = humanoid.Parent;
                if (character == null || character.IsDestroyed)
                {
                    return;
                }

                // WHY resolved through GetPlayerFromLoadedCharacter and not GetPlayerFromCharacter:
                // Character is Lua-writable with no ownership check on assignment (a script can
                // point its OWN Character at a foreign character just by writing to a property it
                // owns). Matching against it here would let that alias steal another player's
                // death-triggered respawn — the owner of a dying character must be resolved through
                // the reference the lifecycle itself set, not through an alias any connected actor
                // can point anywhere.
                RbxPlayer player = _players.GetPlayerFromLoadedCharacter(character);
                if (player == null || !_players.CharacterAutoLoads)
                {
                    return;
                }

                double respawnSeconds = Math.Max(0d, _players.RespawnTime);
                _scheduler.ScheduleHostCallback(respawnSeconds, () =>
                {
                    // WHY re-checked at fire time, not captured at Died: CharacterAutoLoads may have
                    // changed since, the player may have disconnected, and the dead character may
                    // already have been replaced by an explicit LoadCharacterAsync — any of those
                    // means this timer's job is already done or no longer wanted. WHY re-resolved
                    // through GetPlayerFromLoadedCharacter again instead of comparing player.Character:
                    // the same alias risk applies at fire time — some other actor's script could have
                    // pointed its own Character at this dead character in the meantime, and that must
                    // not affect whether THIS player's respawn proceeds.
                    if (player.IsDestroyed || !_players.CharacterAutoLoads
                        || !ReferenceEquals(_players.GetPlayerFromLoadedCharacter(character), player)
                        || _registry.WorldRoot == null)
                    {
                        return;
                    }

                    // WHY caught here rather than left to propagate: this callback runs from the
                    // scheduler's host-callback slot, outside any mod's dispatch try/catch — the
                    // same slot the join-time deferred spawn runs from, and an unguarded failure
                    // here would just as surely kill the whole scheduler frame for every mod. A
                    // failed respawn should cost only this player its character.
                    try
                    {
                        RbxCharacterFactory.Load(_registry, _registry.WorldRoot, player);
                    }
                    catch (Exception exception)
                    {
                        LogFailedRespawn(player, exception);
                    }
                });
            }));
        }

        /// <summary>
        /// Reports a failed death-triggered respawn through the registry's diagnostics seam — the
        /// same seam <see cref="RbxPlayers"/>'s join-time auto-load failure uses — so the player is
        /// simply left without a character instead of the failure vanishing silently.
        /// </summary>
        private void LogFailedRespawn(RbxPlayer player, Exception exception)
        {
            Action<string> diagnostics = _registry.Diagnostics;
            if (diagnostics != null)
            {
                diagnostics("[CoreAI.RbxApi] The death-triggered respawn for '" + player.Name
                    + "' failed and was skipped, so Character stays nil: " + exception);
            }
        }

        /// <summary>
        /// Builds (or rebuilds) the character for the Player passed from Lua, and returns it.
        /// </summary>
        /// <remarks>
        /// WHY this is a C# hook behind a Lua bridge rather than a plain bound method: the mirror's
        /// LoadCharacterAsync yields, and yielding is a Lua-side coroutine.yield the C# method
        /// cannot perform. The bridge builds here, then waits one scheduler slot so the deferred
        /// CharacterAdded handlers run before the caller resumes — the same shape WaitForChild uses.
        /// </remarks>
        private LuaValue BuildCharacterForLoad(LuaCsRbxModContext context,
            LuaFunctionExecutionContext ctx)
        {
            if (!TryGetInstance(Arg(ctx, 0), out LuaCsRbxInstanceProxy proxy)
                || !(proxy.Instance is RbxPlayer player))
            {
                throw RbxError.BadArgument(
                    "Player:LoadCharacterAsync expects a Player",
                    "call it on a Player, e.g. Players.LocalPlayer:LoadCharacterAsync()");
            }

            // WHY authorized like a destroy: loading replaces the player's whole character subtree,
            // so it is exactly as destructive as Player:Kick's teardown and authorizes the same way.
            context.RequireDestroyTree(player, "load character");
            if (player.Character != null && !player.Character.IsDestroyed)
            {
                context.RequireDestroyTree(player.Character, "replace character");
            }
            RbxInstance worldRoot = _registry.WorldRoot
                ?? throw RbxError.BadArgument(
                    "Player:LoadCharacterAsync needs a world root",
                    "attach the Rbx world before loading a character");
            RbxInstance character = RbxCharacterFactory.Load(_registry, worldRoot, player);
            return context.WrapInstance(character);
        }

        /// <summary>Seeds a freshly built character root part's size and spawn position.</summary>
        // WHY the spawn transform is pushed through the sink rather than set on the instance: a
        // BasePart's spatial state lives in the part sink, not on RbxInstance, and the character
        // factory builds its root part in the engine-free assembly that cannot reach the sink.
        // Without this push the sink materializes the part from its own default — a 4x1x2 block at
        // the world origin — so every joining player dropped an unanchored collidable box into
        // whatever already stood there.
        private void SeedCharacterRootPart(RbxInstance rootPart, RbxVector3 size, RbxVector3 position)
        {
            if (rootPart == null || _partSink == null)
            {
                return;
            }

            _partSink.SetSize(rootPart.Id, size);
            _partSink.SetPosition(rootPart.Id, position);
        }

        /// <summary>Reads a part's live position in studs, for DistanceFromCharacter.</summary>
        // WHY live and not the stored PartProperties: the character motor and the world's gravity
        // move the backing Rigidbody directly and never write back into the part-property store, so
        // reading the store gave every proximity check a position frozen at spawn (or at the last
        // script write) no matter how far the part had actually walked or fallen since. The sink
        // falls back to the stored/default value on its own for a part with no backing object yet.
        private RbxVector3 ReadPartPositionStuds(RbxInstance part)
        {
            return part != null ? _partSink.GetLivePositionStuds(part.Id) : RbxVector3.Zero;
        }

        // WHY this class releases every motor it hands to a Humanoid, not whoever supplied it:
        // this is the one place a motor is built (the bundled factory closure or a registered
        // IRbxCharacterMotorProvider.TryCreate, both reached only from here) and the one place
        // every replacement, unregistration and disposal path already runs through — the pipeline
        // that creates a motor is the pipeline that retires it.
        private void AttachCharacterMotor(RbxHumanoid humanoid)
        {
            // WHY: ownership is dropped before the host's Release can run, so a Release that
            // throws leaves nothing behind for a later attach to find and release a second time.
            _characterMotors.Remove(humanoid, out IRbxCharacterMotor previousMotor);
            humanoid.AttachHost(_scheduler, null, ResolveRootPart(humanoid));
            // WHY released here, once the Humanoid no longer forwards to it, and before the
            // factory builds a replacement: a host motor may hold a registration keyed by this
            // character (a controller-registry slot, a rig instance) that a fresh TryCreate for
            // the same body would collide with if the old one had not already let go.
            ReleaseMotorContained(humanoid, previousMotor);
            IRbxCharacterMotor motor = _characterMotorFactory?.Invoke(humanoid);
            humanoid.AttachHost(_scheduler, motor, ResolveRootPart(humanoid));
            _characterMotors[humanoid] = motor;
        }

        /// <summary>
        /// Retires a motor this pipeline has already stopped owning, so a host <c>Release</c> that
        /// throws costs that one motor and nothing else.
        /// </summary>
        /// <remarks>
        /// WHY contained instead of propagated: <see cref="IRbxCharacterMotor"/> is a public seam
        /// and Release is the host's code. Escaping from Dispose used to abort the rest of the
        /// teardown permanently (the disposed flag was already set), leaving every other motor
        /// unreleased and the scheduler pumping a disposed object; escaping from a rebuild left
        /// the dead motor owned and released again on the next attach. The failure goes through
        /// the registry's diagnostics route because a sink that throws is contained there too.
        /// </remarks>
        private void ReleaseMotorContained(RbxHumanoid humanoid, IRbxCharacterMotor motor)
        {
            if (motor == null)
            {
                return;
            }

            try
            {
                motor.Release();
            }
            catch (Exception exception)
            {
                _registry.ReportDiagnostic("[CoreAI.RbxApi] IRbxCharacterMotor.Release threw for '"
                    + humanoid.Name + "' and was contained; the motor is no longer owned by the "
                    + "character pipeline and will not be released again: " + exception);
            }
        }

        private void OnCharacterSceneMembershipChanged(RbxInstance instance, bool entered)
        {
            // WHY scoped to the changed subtree instead of a full refresh: this fires on every
            // reparent anywhere in the world, so loading a 500-part model used to run 500 full
            // sweeps — a list allocation plus a FindFirstChild name scan per tracked humanoid
            // each time, and a factory retry for every motor-less humanoid forever. Both the
            // entering and the leaving direction can flip the rebuild decision (a body appearing
            // or disappearing), so entered filters nothing out.
            _ = entered;
            if (instance == null || _characterMotors.Count == 0)
            {
                return;
            }

            _motorRefreshScratch.Clear();
            foreach (RbxHumanoid humanoid in _characterMotors.Keys)
            {
                if (humanoid.IsDestroyed
                    || !IsHumanoidAffectedByMembershipChange(humanoid, instance)
                    || !MotorNeedsRebuild(humanoid))
                {
                    continue;
                }

                _motorRefreshScratch.Add(humanoid);
            }

            for (int index = 0; index < _motorRefreshScratch.Count; index++)
            {
                RbxHumanoid humanoid = _motorRefreshScratch[index];
                if (!humanoid.IsDestroyed)
                {
                    AttachCharacterMotor(humanoid);
                }
            }

            _motorRefreshScratch.Clear();
        }

        private void RefreshCharacterMotors()
        {
            if (_characterMotors.Count == 0)
            {
                return;
            }

            _motorRefreshScratch.Clear();
            foreach (RbxHumanoid humanoid in _characterMotors.Keys)
            {
                if (!humanoid.IsDestroyed && MotorNeedsRebuild(humanoid))
                {
                    _motorRefreshScratch.Add(humanoid);
                }
            }

            for (int index = 0; index < _motorRefreshScratch.Count; index++)
            {
                RbxHumanoid humanoid = _motorRefreshScratch[index];
                if (!humanoid.IsDestroyed)
                {
                    AttachCharacterMotor(humanoid);
                }
            }

            _motorRefreshScratch.Clear();
        }

        private static bool IsHumanoidAffectedByMembershipChange(
            RbxHumanoid humanoid, RbxInstance instance)
        {
            if (ReferenceEquals(humanoid, instance) || humanoid.IsDescendantOf(instance))
            {
                return true;
            }

            RbxInstance character = humanoid.Parent;
            if (character == null || character.IsDestroyed)
            {
                return false;
            }

            if (ReferenceEquals(character, instance) || character.IsDescendantOf(instance))
            {
                return true;
            }

            if (ReferenceEquals(humanoid.RootPart, instance))
            {
                return true;
            }

            return instance.IsDescendantOf(character)
                && instance.IsA("BasePart")
                && string.Equals(
                    instance.Name,
                    Mods.Rbx.Instances.Networking.RbxCharacterFactory.RootPartName,
                    StringComparison.Ordinal);
        }

        private bool MotorNeedsRebuild(RbxHumanoid humanoid)
        {
            if (!ReferenceEquals(humanoid.RootPart, ResolveRootPart(humanoid)))
            {
                return true;
            }

            IRbxCharacterMotor current = _characterMotors[humanoid];
            if (current == null)
            {
                return _characterMotorFactory != null;
            }

            // WHY asked through the interface and not by concrete type: a host motor supplied
            // through IRbxCharacterMotorProvider goes stale exactly the same way, and testing
            // for CoreAI's own type left it holding a destroyed body with no rebuild.
            return current is { IsAvailable: false };
        }

        /// <summary>
        /// Resolves the mirror's <c>Humanoid.RootPart</c>: the sibling BasePart named
        /// <c>HumanoidRootPart</c>, or null when the character has none.
        /// </summary>
        /// <remarks>
        /// WHY not the Humanoid's parent, which is what this used to pass: the parent is the
        /// character MODEL, and Roblox's RootPart is a BasePart a script positions and reads
        /// velocity from. Handing back a Model made every RootPart read a wrong answer of the wrong
        /// class, silently.
        /// </remarks>
        private static RbxInstance ResolveRootPart(RbxHumanoid humanoid)
        {
            RbxInstance character = humanoid.Parent;
            if (character == null || character.IsDestroyed)
            {
                return null;
            }

            RbxInstance root = character.FindFirstChild(
                Mods.Rbx.Instances.Networking.RbxCharacterFactory.RootPartName);
            return root != null && root.IsA("BasePart") ? root : null;
        }

        /// <summary>Property IO behind the tween driver (same assembly as the bindings).</summary>
        internal LuaCsTweenPropertyHost TweenPropertyHost => _tweenPropertyHost;

        /// <summary>
        /// Resolves an engine-free tween state to its interned Enum.PlaybackState item for the
        /// Completed signal payload (mirror: Completed passes the PlaybackState).
        /// </summary>
        internal RbxEnumItem ResolvePlaybackStateItem(RbxTweenPlaybackState state)
        {
            if (_enums.TryGet("PlaybackState", out RbxEnum playbackState)
                && playbackState.TryGetItem(state.ToString(), out RbxEnumItem item))
            {
                return item;
            }

            throw RbxError.BadArgument(
                "Tween.Completed cannot resolve Enum.PlaybackState." + state,
                "use the default enum registry, which ships PlaybackState with TweenService");
        }

        /// <summary>Mod log sink behind Debris drop reports and headless-mode notes.</summary>
        internal Action<string> LogSink => _log;

        /// <summary>Camera-ray seam behind ClickDetector.MouseClick (headless in-memory by default,
        /// the composition attaches the engine-backed source once).</summary>
        public IClickPickSource PickSource => _pickSource;

        /// <summary>
        /// Ledger of the signal connections mods open through <c>Connect</c>/<c>Once</c>. The
        /// composition disconnects a mod's connections on <c>ModTearingDown</c> so its per-frame
        /// handlers stop after unload/reload/quarantine.
        /// </summary>
        public ModConnectionRegistry Connections => _connections;

        /// <summary>
        /// Shared logical task scheduler advanced once per scaled host frame by the runtime driver.
        /// </summary>
        public ModScheduler Scheduler => _scheduler;

        /// <summary>The scheduler thread factory (tests read its runner-reuse counters).</summary>
        internal LuaCsRbxScriptThreadFactory SchedulerThreadFactory => _schedulerThreadFactory;

        /// <summary>
        /// Live per-resume coroutine budget (instruction-step cap + wall-clock cap) every coroutine
        /// handle in this world reads by default — never null. This is the composition-supplied
        /// <see cref="CoreAI.Sandbox.LuaCs.LuaCsCoroutineBudgetSettings"/> (or a default-valued one when
        /// none was supplied) and the exact object <c>ScriptContext:SetTimeout</c> mutates, so the change
        /// reaches every already-pooled signal runner's next resume too.
        /// </summary>
        public LuaCsCoroutineBudgetSettings CoroutineResumeBudget { get; }

        /// <summary>Injectable source behind every Lua-visible clock. Null at composition
        /// means the production system-clock default; a game passes its own source to redefine
        /// time.</summary>
        public IRbxClockSource ClockSource => _clockSource;

        /// <summary>
        /// The share of real time the server clock gives up while it slews back onto an estimate that
        /// moved behind it: at 0.5 it runs at half speed, so a backward correction of N seconds is
        /// absorbed in 2N seconds of real time and the clock never stands still.
        /// </summary>
        /// <remarks>
        /// WHY not Roblox's 0.6%: Roblox corrects errors of milliseconds, while a CoreAI correction can
        /// be a wall-clock step on the host or a client joining another server — whole seconds or
        /// more. At 0.6% a ten-second correction would keep every client off the server's time for
        /// half an hour; at half speed timers stay ordered and agree again within twenty seconds.
        /// </remarks>
        internal const double ServerTimeSlewRate = 0.5d;

        /// <summary>
        /// Server-synced epoch seconds behind <c>workspace:GetServerTimeNow()</c>. From the bridge's
        /// first synchronization on it never decreases: an estimate that moves ahead is taken at once,
        /// one that moves behind is slewed onto at <see cref="ServerTimeSlewRate"/> of real time.
        /// Before the bridge is synchronized it is the local clock, unsmoothed.
        /// </summary>
        internal double GetServerTimeNow()
        {
            // WHY the bridge's offset is added: on a client the local clock is its own machine's,
            // and a player whose system time is an hour off would otherwise disagree with the server
            // about when everything happened. The offset is zero on a server and on the loopback,
            // so solo behaviour is byte-identical to before.
            double estimate = _clockSource.UnixTimeSecondsFractional
                              + (_networkBridge?.ServerClockOffsetSeconds ?? 0d);
            bool synchronized = _networkBridge == null || _networkBridge.IsServerClockSynchronized;
            double processSeconds = _clockSource.ProcessTimeSeconds;
            lock (_serverTimeGate)
            {
                // WHY nothing is kept before the first synchronization: an unsynchronized offset is zero
                // because nothing is known, and a floor recorded from the local clock then froze the
                // synchronized value for the whole skew — a day, for a client of a long-running server.
                if (!synchronized)
                {
                    _serverTimeBased = false;
                    return estimate;
                }

                if (!_serverTimeBased)
                {
                    // WHY a re-base and not a clamp: the first synchronized estimate replaces a guess, so
                    // it may land on either side of the local clock once; monotonicity starts here.
                    _serverTimeBased = true;
                    _lastServerTimeNow = IsFinite(estimate) ? estimate : _clockSource.UnixTimeSecondsFractional;
                    _lastServerTimeProcessSeconds = processSeconds;
                    return _lastServerTimeNow;
                }

                double elapsed = processSeconds - _lastServerTimeProcessSeconds;
                if (elapsed > 0d)
                {
                    _lastServerTimeProcessSeconds = processSeconds;
                }
                else
                {
                    elapsed = 0d;
                }

                double freeRunning = _lastServerTimeNow + elapsed;
                double next;
                if (!IsFinite(estimate))
                {
                    next = freeRunning;
                }
                else if (estimate >= freeRunning)
                {
                    next = estimate;
                }
                else
                {
                    // WHY slewed instead of clamped: a clamp froze the clock for the whole size of a
                    // backward step (an hour, for an hour's NTP correction), stalling every timer built
                    // on it; running slower converges while every reading still moves forward.
                    next = Math.Max(estimate,
                        _lastServerTimeNow + elapsed * (1d - ServerTimeSlewRate));
                }

                _lastServerTimeNow = next;
                return next;
            }
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        /// <summary>
        /// Builds the sanctioned sandbox <c>os</c> table: ONLY <c>time</c> and <c>clock</c>. The
        /// stock library stays removed (it carries execute/remove/rename/exit/getenv/tmpname);
        /// this is the single definition shared by the value factory and the HttpService
        /// decorator, so decorator ordering can never widen or narrow the surface.
        /// </summary>
        internal LuaValue BuildOsTable()
        {
            LuaTable os = new();
            os["time"] = Fn("os.time", ctx =>
            {
                LuaValue date = Arg(ctx, 0);
                if (date.Type == LuaValueType.Nil)
                {
                    return (double)_clockSource.UnixTimeSeconds;
                }

                if (!date.TryRead(out LuaTable fields))
                {
                    throw ExpectedArgument("os.time", "a date table or nil", date, 1);
                }

                return UnixSecondsFromDateTable(fields);
            });
            os["clock"] = Fn("os.clock", _ => _clockSource.ProcessTimeSeconds);
            return new LuaValue(os);
        }

        /// <summary>
        /// <c>os.time(t)</c>: the Unix seconds of the date the table describes. <c>year</c>,
        /// <c>month</c> and <c>day</c> are required; <c>hour</c> defaults to 12 and <c>min</c> and
        /// <c>sec</c> to 0; a field outside its range carries into the next one (month 13 is January of
        /// the next year) and <c>isdst</c> is ignored.
        /// </summary>
        /// <remarks>
        /// WHY the table is read as UTC: Luau replaced Lua 5.1's local-time <c>mktime</c> with a UTC
        /// conversion ("we prefer UTC for consistency"), and every other CoreAI clock is UTC —
        /// <c>os.time()</c> reads <see cref="IRbxClockSource.UnixTimeSeconds"/> — so
        /// <c>os.time(os.date("!*t", t)) == t</c> holds and a server and a client in different time
        /// zones compute the same timestamp. That is also why <c>isdst</c> changes nothing.
        /// </remarks>
        private const double MaxDateFieldMagnitude = 1e6 * 366d * 86400d;

        private static double UnixSecondsFromDateTable(LuaTable fields)
        {
            long year = ReadDateField(fields, "year", null);
            long month = ReadDateField(fields, "month", null);
            long day = ReadDateField(fields, "day", null);
            long hour = ReadDateField(fields, "hour", 12);
            long minute = ReadDateField(fields, "min", 0);
            long second = ReadDateField(fields, "sec", 0);

            long monthIndex = month - 1;
            year += FloorDivide(monthIndex, 12);
            monthIndex -= FloorDivide(monthIndex, 12) * 12;
            long days = DaysFromCivil(year, monthIndex + 1, 1) + (day - 1);
            return days * 86400d + hour * 3600d + minute * 60d + second;
        }

        private static long ReadDateField(LuaTable fields, string name, long? fallback)
        {
            LuaValue value = fields[name];
            if (value.Type == LuaValueType.Nil)
            {
                if (fallback.HasValue)
                {
                    return fallback.Value;
                }

                throw RbxError.BadArgument(
                    "os.time: field '" + name + "' missing in date table",
                    "pass year, month and day, e.g. os.time({year = 2024, month = 1, day = 1, hour = 0})");
            }

            if (!TryCoerceNumber(value, out double number))
            {
                throw RbxError.BadArgument(
                    "os.time: field '" + name + "' must be a number, got " + Describe(value),
                    "pass whole numbers, e.g. os.time({year = 2024, month = 1, day = 1, hour = 0})");
            }

            // WHY bounded by about a million years' worth of seconds: every field then stays exact
            // through the date arithmetic in a long, and no real date comes near it, so a larger
            // value (or NaN or an infinity) is a bug in the script rather than a date.
            if (double.IsNaN(number) || Math.Abs(number) > MaxDateFieldMagnitude)
            {
                throw RbxError.BadArgument(
                    "os.time: field '" + name + "' is out of range, got " + number.ToString(
                        "R", System.Globalization.CultureInfo.InvariantCulture),
                    "pass a calendar date, e.g. os.time({year = 2024, month = 1, day = 1, hour = 0})");
            }

            // WHY truncated: Luau reads each field with lua_tointeger, which drops the fraction.
            return (long)Math.Truncate(number);
        }

        private static long FloorDivide(long value, long divisor)
        {
            long quotient = value / divisor;
            return (value % divisor != 0 && (value < 0) != (divisor < 0)) ? quotient - 1 : quotient;
        }

        /// <summary>Days from 1970-01-01 to the proleptic Gregorian date (month 1..12).</summary>
        private static long DaysFromCivil(long year, long month, long day)
        {
            long shiftedYear = month <= 2 ? year - 1 : year;
            long era = FloorDivide(shiftedYear, 400);
            long yearOfEra = shiftedYear - era * 400;
            long monthFromMarch = month > 2 ? month - 3 : month + 9;
            long dayOfYear = (153 * monthFromMarch + 2) / 5 + day - 1;
            long dayOfEra = yearOfEra * 365 + yearOfEra / 4 - yearOfEra / 100 + dayOfYear;
            return era * 146097 + dayOfEra - 719468;
        }

        /// <summary>Transport-neutral bridge used by the production Lua remote surface.</summary>
        public INetworkBridge NetworkBridge => _networkBridge;

        /// <summary>Players service populated from trusted network actor contexts.</summary>
        public RbxPlayers Players => _players;

        /// <summary>
        /// Mirror <c>Player:Kick</c> entry: removes the player with the <c>CreatorKick</c> exit
        /// reason (the enum item comes from the shared registry so Lua identity comparison
        /// against <c>Enum.PlayerExitReason.CreatorKick</c> holds). Already-removed players are a
        /// silent no-op here; the Lua boundary refuses destroyed instances before reaching this.
        /// </summary>
        internal void KickPlayerWithCreatorKick(RbxPlayer player)
        {
            RbxEnumItem reason = _enums.Get("PlayerExitReason")["CreatorKick"];
            _players.KickPlayer(player, reason);
        }

        internal int CountRemoteFunctionWaitsOwnedBy(string ownerModId)
        {
            int count = 0;
            foreach (IRbxScriptThread thread in _remoteFunctionWaitGenerations.Keys)
            {
                if (thread is LuaCsRbxScriptThread luaThread && string.Equals(
                        luaThread.OwnerModId, ownerModId, StringComparison.Ordinal))
                {
                    count++;
                }
            }

            return count;
        }

        internal int CountRemoteFunctionCallbacksOwnedBy(string ownerModId)
        {
            int count = 0;
            foreach (RemoteFunctionCallbackRegistration registration
                     in _serverRemoteCallbacks.Values)
            {
                if (string.Equals(registration.Context.OwnerModId,
                        ownerModId, StringComparison.Ordinal))
                {
                    count++;
                }
            }

            foreach (Dictionary<string, RemoteFunctionCallbackRegistration> callbacks
                     in _clientRemoteCallbacks.Values)
            {
                foreach (RemoteFunctionCallbackRegistration registration in callbacks.Values)
                {
                    if (string.Equals(registration.Context.OwnerModId,
                            ownerModId, StringComparison.Ordinal))
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        /// <summary>Attaches the actor-keyed chat factory released on disconnect.</summary>
        public void AttachChatFactory(CoreAI.Ai.IInGameLlmChatServiceFactory chatFactory)
        {
            _chatFactory = chatFactory;
        }

        /// <summary>Admits a trusted actor to the loopback bridge and creates its Player once.</summary>
        public RbxPlayer ConnectActor(ActorContext actorContext)
        {
            if (!actorContext.IsTrusted)
            {
                throw new RbxError(
                    RbxErrorCode.NotAuthority,
                    "an untrusted actor cannot connect to the network bridge",
                    "use an ActorContext issued by the configured identity provider");
            }

            return EnsureNetworkActor(actorContext.ActorId);
        }

        /// <summary>Disconnects one admitted actor and releases every actor-owned runtime resource.</summary>
        public bool DisconnectActor(ActorContext actorContext)
        {
            if (!actorContext.IsTrusted)
            {
                throw new RbxError(
                    RbxErrorCode.NotAuthority,
                    "an untrusted actor cannot disconnect a network identity",
                    "use an ActorContext issued by the configured identity provider");
            }

            string actorId = actorContext.ActorId;
            bool hadPlayer = _players.TryGetByActorId(actorId, out _);
            bool hadBridge = IsNetworkActorAdmitted(actorId);
            List<string> ownerModIds = new();
            foreach (KeyValuePair<string, ActorContext> pair
                     in _actorContextsByOwnerModId)
            {
                if (string.Equals(pair.Value.ActorId, actorId,
                        StringComparison.Ordinal))
                {
                    ownerModIds.Add(pair.Key);
                }
            }

            IReadOnlyList<RbxInstance> instances = _registry.GetLiveInstances();
            bool hadRemoteState = false;
            for (int index = 0; index < instances.Count; index++)
            {
                if (instances[index] is RbxRemoteEvent remote
                    && remote.HasActor(actorId))
                {
                    hadRemoteState = true;
                    break;
                }
            }

            bool hadState = hadPlayer || hadBridge || hadRemoteState
                            || ownerModIds.Count > 0;
            if (!hadState)
            {
                return false;
            }

            _networkBridge.UnregisterActor(actorId);
            RbxEnumItem reason = _enums.Get("PlayerExitReason")["Unknown"];
            _players.RemoveActor(actorId, reason);
            _chatFactory?.ReleaseActor(actorContext);

            for (int index = 0; index < ownerModIds.Count; index++)
            {
                string ownerModId = ownerModIds[index];
                KillAllScheduledOwnedBy(ownerModId);
                _connections.DisconnectOwnedBy(ownerModId);
                _registry.ClearActorAttribution(
                    ownerModId, OriginTag.FromMod(ownerModId));
            }

            for (int index = 0; index < instances.Count; index++)
            {
                if (instances[index] is RbxRemoteEvent remote)
                {
                    remote.RemoveActor(actorId);
                }
            }

            foreach (Dictionary<string, RemoteFunctionCallbackRegistration> callbacks
                     in _clientRemoteCallbacks.Values)
            {
                callbacks.Remove(actorId);
            }

            _remoteRefusalLoggedSenders.Remove(actorId);
            ForgetNetworkWarningsFrom(actorId);
            if (ownerModIds.Count > 0)
            {
                RaiseActorModsDisconnected(actorId, ownerModIds);
            }

            return true;
        }

        /// <summary>
        /// Raised by <see cref="DisconnectActor"/> after it has released an actor, with the actor id and
        /// every mod that was loaded for it. Those mods stay loaded here: their threads are killed,
        /// their connections dropped and their actor attribution released, so each later dispatch of
        /// theirs is refused with NOT_AUTHORITY; the mod runtime subscribes to unload or quarantine
        /// them so they stop dispatching at all (M2-24).
        /// </summary>
        /// <remarks>
        /// WHY an event and not an unload here: loading and unloading belong to the mod runtime, which
        /// owns the mods' states, stores and hooks; the bindings only know which mods ran as whom.
        /// </remarks>
        public event Action<string, IReadOnlyList<string>> ActorModsDisconnected;

        private void RaiseActorModsDisconnected(string actorId, List<string> ownerModIds)
        {
            Action<string, IReadOnlyList<string>> handlers = ActorModsDisconnected;
            if (handlers == null)
            {
                return;
            }

            IReadOnlyList<string> mods = ownerModIds.AsReadOnly();
            foreach (Delegate handler in handlers.GetInvocationList())
            {
                // WHY each subscriber is contained: the actor is already gone when this runs, and one
                // subscriber that throws must not keep the others from releasing its mods.
                try
                {
                    ((Action<string, IReadOnlyList<string>>)handler)(actorId, mods);
                }
                catch (Exception ex)
                {
                    _log?.Invoke("[RbxApi] A disconnect subscriber failed for actor '" + actorId
                                 + "': " + ex.Message);
                }
            }
        }

        /// <summary>Releases bridge subscriptions, scheduler work, and captured Lua callback state.</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _networkBridge.EventReceived -= DeliverNetworkEvent;
            _networkBridge.RequestReceived -= QueueNetworkRequest;
            _registry.Unregistered -= OnInstanceUnregistered;
            _registry.Registered -= OnInstanceRegisteredForCharacter;
            _registry.SceneMembershipChanged -= OnCharacterSceneMembershipChanged;
            // WHY: the motors leave this class's ownership before any host Release runs, so a
            // Release that throws is contained per motor and every later teardown step still runs.
            List<KeyValuePair<RbxHumanoid, IRbxCharacterMotor>> motors = new(_characterMotors);
            _characterMotors.Clear();
            for (int index = 0; index < motors.Count; index++)
            {
                motors[index].Key.DetachHost();
                ReleaseMotorContained(motors[index].Key, motors[index].Value);
            }
            if (_debris != null)
            {
                _debris.DetachHost();
            }
            if (_collectionService != null)
            {
                _collectionService.DetachHost();
            }
            // WHY: the service subscribes itself to this scheduler's phases and to the registry; left
            // attached, advancing the old scheduler after a session swap kept stepping its tweens and
            // writing into a world these bindings no longer front (M8-22).
            _tweenService?.DetachHost();
            _scheduler.PhaseReached -= PumpSchedulerPhase;
            _networkRequestConnection.Disconnect();

            List<string> owners = new(_scheduledThreadsByMod.Keys);
            for (int index = 0; index < owners.Count; index++)
            {
                string ownerModId = owners[index];
                KillAllScheduledOwnedBy(ownerModId);
                _connections.DisconnectOwnedBy(ownerModId);
            }

            _scheduler.ThreadRetired -= OnSchedulerThreadRetired;
            _serverRemoteCallbacks.Clear();
            _clientRemoteCallbacks.Clear();
            _remoteFunctionWaitGenerations.Clear();
            _scheduledThreadsByMod.Clear();
            _currentSchedulerGenerationByMod.Clear();
            _actorContextsByOwnerModId.Clear();
            _resumeOperationByMod.Clear();
            _originTagByMod.Clear();
            _networkWarningWindows.Clear();
            ActorModsDisconnected = null;
        }

        internal ModLoadCandidate BeginModLoadCandidate(string ownerModId)
        {
            string owner = string.IsNullOrWhiteSpace(ownerModId)
                ? throw new ArgumentException("Owner mod id is required.", nameof(ownerModId))
                : ownerModId;
            bool hadPreviousGeneration = _currentSchedulerGenerationByMod.TryGetValue(
                owner, out int previousGeneration);
            HashSet<RbxScriptConnection> existingConnections =
                new(_connections.GetOwnedBy(owner));
            bool hadExecutingScriptBacking = TryGetExecutingScriptBacking(owner, out _);
            return new ModLoadCandidate(owner, hadPreviousGeneration,
                previousGeneration, existingConnections, hadExecutingScriptBacking);
        }

        internal void RollbackModLoadCandidate(ModLoadCandidate candidate)
        {
            if (candidate == null)
            {
                throw new ArgumentNullException(nameof(candidate));
            }

            string ownerModId = candidate.OwnerModId;
            if (_currentSchedulerGenerationByMod.TryGetValue(
                    ownerModId, out int candidateGeneration)
                && (!candidate.HadPreviousGeneration
                    || candidateGeneration != candidate.PreviousGeneration))
            {
                CancelScheduledGeneration(ownerModId, candidateGeneration);
            }

            if (candidate.HadPreviousGeneration)
            {
                _currentSchedulerGenerationByMod[ownerModId] =
                    candidate.PreviousGeneration;
            }
            else
            {
                _currentSchedulerGenerationByMod.Remove(ownerModId);
            }

            IReadOnlyList<RbxScriptConnection> currentConnections =
                _connections.GetOwnedBy(ownerModId);
            for (int index = 0; index < currentConnections.Count; index++)
            {
                RbxScriptConnection connection = currentConnections[index];
                if (!candidate.ExistingConnections.Contains(connection))
                {
                    connection.Disconnect();
                }
            }

            if (!candidate.HadExecutingScriptBacking
                && TryGetExecutingScriptBacking(ownerModId,
                    out ExecutingScriptBacking createdBacking))
            {
                _executingScriptsByMod.Remove(ownerModId);
                createdBacking.Script.Destroy();
                createdBacking.Container.Destroy();
            }
        }

        /// <summary>Runs a persistent mod's main chunk as an immediately resumed scheduler thread.</summary>
        public void RunModChunk(IScriptState ownerState, string ownerModId, string source,
            IExecutionBudget resumeBudget)
        {
            string owner = string.IsNullOrWhiteSpace(ownerModId)
                ? throw new ArgumentException("Owner mod id is required.", nameof(ownerModId))
                : ownerModId;
            if (!_currentSchedulerGenerationByMod.TryGetValue(owner, out int generation))
            {
                generation = _connections.BeginGeneration(owner);
                _currentSchedulerGenerationByMod[owner] = generation;
            }

            _schedulerThreadFactory.PrepareWaitBindings(ownerState);
            object callable = _schedulerThreadFactory.CaptureChunk(
                ownerState, source, resumeBudget);
            IRbxScriptThread thread = _scheduler.Spawn(
                owner, callable, Array.Empty<object>());
            if (thread is LuaCsRbxScriptThread luaThread)
            {
                if (luaThread.LastException != null)
                {
                    ExceptionDispatchInfo.Capture(luaThread.LastException).Throw();
                }

                if (luaThread.LastFailure != null)
                {
                    throw luaThread.LastFailure;
                }
            }

            TrackScheduledThread(owner, generation, thread);
        }

        private ScriptResumeResult ResumeSchedulerThread(string ownerModId,
            Func<ScriptResumeResult> resume)
        {
            ActorContext actorContext = ResolveOwnerActorContext(ownerModId);
            return _registry.ApplyServerGeneratedMutation(
                actorContext.ActorId,
                actorContext.Grants.IsUnrestricted,
                actorContext.WorldId,
                ResumeOperationName(ownerModId),
                resume);
        }

        private string ResumeOperationName(string ownerModId)
        {
            // WHY: this label is built for every scheduler resume (every signal handler, every
            // task.wait loop tick); interning it per mod keeps the hot path free of string churn.
            if (!_resumeOperationByMod.TryGetValue(ownerModId, out string operation))
            {
                operation = "resume Lua scheduler thread owned by mod '" + ownerModId + "'";
                _resumeOperationByMod[ownerModId] = operation;
            }

            return operation;
        }

        internal ActorContext ResolveOwnerActorContext(string ownerModId)
        {
            return LuaCsRbxModContext.ResolveActorContext(
                this, ownerModId, ModOriginTag(ownerModId));
        }

        /// <summary>
        /// The <c>mod:&lt;id&gt;</c> origin tag of a mod, built once per mod: every scheduler resume and
        /// every legacy hook dispatch resolves its actor through it.
        /// </summary>
        internal string ModOriginTag(string ownerModId)
        {
            if (!_originTagByMod.TryGetValue(ownerModId, out string originTag))
            {
                originTag = OriginTag.FromMod(ownerModId);
                _originTagByMod[ownerModId] = originTag;
            }

            return originTag;
        }

        /// <summary>Mods holding an interned resume label or origin tag (both are released with the mod).</summary>
        internal int ResumeCacheModCount
        {
            get
            {
                HashSet<string> mods = new(_originTagByMod.Keys, StringComparer.Ordinal);
                mods.UnionWith(_resumeOperationByMod.Keys);
                return mods.Count;
            }
        }

        internal LuaState ResolveSchedulerOwnerState(LuaState fallbackState)
        {
            return _schedulerThreadFactory.ResolveOwnerState(fallbackState);
        }

        internal object CaptureSignalCallable(LuaState ownerState, LuaValue callable)
        {
            // WHY: a signal handler's thread is never exposed to Lua as a task thread, so it may run on
            // a pooled runner (Roblox recycles handler threads the same way).
            return _schedulerThreadFactory.CaptureCallable(ownerState, callable, recyclable: true);
        }

        internal void SpawnSignalHandler(LuaCsRbxModContext context,
            object callable, object[] arguments)
        {
            string ownerModId = RequireTaskOwner(context);
            string sender = _scheduler.CurrentSignalQuotaActorId;
            if (sender == null)
            {
                TrackScheduledThread(context, _scheduler.SpawnSignal(
                    ownerModId, callable, arguments));
                return;
            }

            // WHY charged to the sender: this handler runs because a remote client fired an
            // OnServerEvent. Charged to the handler's owner (normally the host), one client that fired
            // faster than a yielding handler finishes filled the host's whole thread quota (MP-10).
            IRbxScriptThread thread = _scheduler.SpawnSignal(ownerModId, callable, arguments,
                sender, MaxRemoteHandlerThreadsPerSender, out RbxError refusal);
            if (refusal != null)
            {
                NoteRemoteHandlerRefusal(sender, "an OnServerEvent invocation was dropped", refusal);
                return;
            }

            TrackScheduledThread(context, thread);
        }

        /// <summary>
        /// Remote-induced handler starts refused because their sender's budget was full (MP-10):
        /// dropped OnServerEvent invocations plus refused OnServerInvoke calls.
        /// </summary>
        internal long RemoteHandlerRefusalCount { get; private set; }

        private void NoteRemoteHandlerRefusal(string senderActorId, string outcome, RbxError refusal)
        {
            RemoteHandlerRefusalCount++;
            // WHY once per sender: a flood produces one refusal per call, and logging each would turn
            // the client's flood into a log flood on the host.
            if (_remoteRefusalLoggedSenders.Add(senderActorId))
            {
                _log?.Invoke("[RbxApi] " + outcome + ": " + refusal.Message
                             + " (logged once per sender; later refusals are only counted)");
            }
        }

        internal RbxPlayer GetLocalPlayer(LuaCsRbxModContext context)
        {
            if (context.IsNetworkServer)
            {
                return null;
            }

            return EnsureNetworkActor(context.ActorContext.ActorId);
        }

        internal void FireRemoteServer(LuaCsRbxModContext context,
            RbxRemoteEvent remote, LuaFunctionExecutionContext ctx)
        {
            byte[] payload = _networkCodec.EncodeArguments(
                ReadRemoteArguments(ctx, 1));
            remote.FireServer(_networkBridge, context.ActorContext.ActorId, payload);
        }

        internal void FireRemoteClient(RbxRemoteEvent remote, RbxPlayer player,
            LuaFunctionExecutionContext ctx)
        {
            byte[] payload = _networkCodec.EncodeArguments(
                ReadRemoteArguments(ctx, 2));
            remote.FireClient(_networkBridge, player, payload);
        }

        internal void FireRemoteAllClients(RbxRemoteEvent remote,
            LuaFunctionExecutionContext ctx)
        {
            byte[] payload = _networkCodec.EncodeArguments(
                ReadRemoteArguments(ctx, 1));
            remote.FireAllClients(_networkBridge, payload);
        }

        internal LuaValue ReadRemoteFunctionCallback(LuaCsRbxModContext context,
            RbxRemoteFunction remote, bool serverCallback)
        {
            RemoteFunctionCallbackRegistration registration;
            if (serverCallback)
            {
                return _serverRemoteCallbacks.TryGetValue(remote.Id, out registration)
                       && ReferenceEquals(registration.Context, context)
                    ? registration.Callback
                    : LuaValue.Nil;
            }

            string actorId = context.ActorContext.ActorId;
            if (_clientRemoteCallbacks.TryGetValue(remote.Id,
                    out Dictionary<string, RemoteFunctionCallbackRegistration> callbacks)
                && callbacks.TryGetValue(actorId, out registration)
                && ReferenceEquals(registration.Context, context))
            {
                return registration.Callback;
            }

            return LuaValue.Nil;
        }

        internal void WriteRemoteFunctionCallback(LuaCsRbxModContext context,
            RbxRemoteFunction remote, bool serverCallback, LuaState state, LuaValue value)
        {
            if (value.Type == LuaValueType.Nil)
            {
                if (serverCallback)
                {
                    _serverRemoteCallbacks.Remove(remote.Id);
                }
                else if (_clientRemoteCallbacks.TryGetValue(remote.Id,
                             out Dictionary<string, RemoteFunctionCallbackRegistration> callbacks))
                {
                    callbacks.Remove(context.ActorContext.ActorId);
                    if (callbacks.Count == 0)
                    {
                        _clientRemoteCallbacks.Remove(remote.Id);
                    }
                }

                return;
            }

            if (value.Type != LuaValueType.Function)
            {
                string member = serverCallback ? "OnServerInvoke" : "OnClientInvoke";
                throw RbxError.BadArgument(
                    "RemoteFunction." + member + " expects a function or nil, got "
                    + Describe(value),
                    "assign a callback function or nil");
            }

            LuaState ownerState = ResolveSchedulerOwnerState(state);
            object captured = CaptureSignalCallable(ownerState, value);
            if (!(captured is LuaCsRbxSchedulerCallable schedulerCallable)
                || !(schedulerCallable.Callable is LuaValue callback))
            {
                throw RbxError.BadArgument(
                    "RemoteFunction callback could not be captured for the scheduler",
                    "assign the callback from a live persistent mod");
            }

            RemoteFunctionCallbackRegistration registration = new(
                context, schedulerCallable.OwnerState, callback);
            if (serverCallback)
            {
                _serverRemoteCallbacks[remote.Id] = registration;
                return;
            }

            if (!_clientRemoteCallbacks.TryGetValue(remote.Id,
                    out Dictionary<string, RemoteFunctionCallbackRegistration> clientCallbacks))
            {
                clientCallbacks = new Dictionary<string, RemoteFunctionCallbackRegistration>(
                    StringComparer.Ordinal);
                _clientRemoteCallbacks.Add(remote.Id, clientCallbacks);
            }

            clientCallbacks[context.ActorContext.ActorId] = registration;
        }

        private RbxPlayer EnsureNetworkActor(string actorId)
        {
            bool hadPlayer = _players.TryGetByActorId(actorId, out RbxPlayer player);
            if (!hadPlayer)
            {
                player = _players.EnsureActor(_registry, actorId);
            }

            try
            {
                _networkBridge.RegisterActor(actorId);
                return player;
            }
            catch
            {
                if (!hadPlayer)
                {
                    _players.RemoveActor(actorId);
                }

                throw;
            }
        }

        private bool IsNetworkActorAdmitted(string actorId)
        {
            foreach (string admittedActorId in _networkBridge.ActorIds)
            {
                if (string.Equals(admittedActorId, actorId,
                        StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Seconds during which one sender's inbound network warning of one kind is logged once; the
        /// rest in the window are counted in <see cref="RejectedNetworkEventCount"/> and summed into
        /// the next line (MP-16).
        /// </summary>
        internal const double NetworkWarningWindowSeconds = 10d;

        /// <summary>Distinct sender and kind pairs throttled apart; beyond it senders share one window per kind.</summary>
        private const int MaxNetworkWarningWindows = 256;

        private const string AnyNetworkSender = "*";

        private enum NetworkWarningKind
        {
            NullMessage,
            UnknownRemote,
            ReliabilityMismatch,
            UnknownDirection,
            DeliveryFailed
        }

        private sealed class NetworkWarningWindow
        {
            public double OpenedAtSeconds;
            public long Suppressed;
        }

        /// <summary>
        /// Inbound network events dropped with a warning — a null message, an unknown or destroyed
        /// remote, a reliability that does not match the remote, an unknown direction, a failed
        /// delivery — whether or not the warning was logged.
        /// </summary>
        internal long RejectedNetworkEventCount { get; private set; }

        /// <summary>
        /// Logs an inbound network warning at most once per sender and kind every
        /// <see cref="NetworkWarningWindowSeconds"/>, and counts every one.
        /// </summary>
        /// <remarks>
        /// WHY throttled: these fire once per packet, and each line becomes a Unity warning with a stack
        /// trace, so a client sending thousands of packets with a made-up remote id flooded the host's
        /// log, disk and CPU with the one symptom worth seeing once. WHY per sender: one noisy client
        /// must not hide a second one's first warning. WHY the process clock: it is real time that
        /// never steps back, and a paused game still receives packets.
        /// </remarks>
        private void WarnNetworkEvent(string senderActorId, NetworkWarningKind kind,
            RbxInstance remote = null, string failure = null)
        {
            RejectedNetworkEventCount++;
            if (_log == null)
            {
                return;
            }

            (string Sender, NetworkWarningKind Kind) key = (senderActorId ?? "", kind);
            if (!_networkWarningWindows.TryGetValue(key, out NetworkWarningWindow window)
                && _networkWarningWindows.Count >= MaxNetworkWarningWindows)
            {
                key = (AnyNetworkSender, kind);
                _networkWarningWindows.TryGetValue(key, out window);
            }

            double now = _clockSource.ProcessTimeSeconds;
            if (window != null)
            {
                double open = now - window.OpenedAtSeconds;
                if (open >= 0d && open < NetworkWarningWindowSeconds)
                {
                    window.Suppressed++;
                    return;
                }
            }
            else
            {
                window = new NetworkWarningWindow();
                _networkWarningWindows[key] = window;
            }

            long suppressed = window.Suppressed;
            window.OpenedAtSeconds = now;
            window.Suppressed = 0;
            _log("[RbxApi] " + DescribeNetworkWarning(senderActorId, kind, remote, failure)
                 + (suppressed > 0
                     ? " (" + suppressed + " more like it were dropped in the last window)"
                     : "")
                 + " (logged once per sender every " + NetworkWarningWindowSeconds
                 + " s; the rest are only counted)");
        }

        /// <summary>The warning text, built only for a line that is actually logged.</summary>
        private static string DescribeNetworkWarning(string senderActorId, NetworkWarningKind kind,
            RbxInstance remote, string failure)
        {
            string sender = "'" + (senderActorId ?? "") + "'";
            switch (kind)
            {
                case NetworkWarningKind.NullMessage:
                    return "Network event message was null.";
                case NetworkWarningKind.UnknownRemote:
                    return "Network event from " + sender + " targeted an unknown RemoteEvent.";
                case NetworkWarningKind.ReliabilityMismatch:
                    return "Network event reliability from " + sender + " does not match "
                           + (remote != null ? remote.GetFullName() : "the RemoteEvent") + ".";
                case NetworkWarningKind.UnknownDirection:
                    return "Network event from " + sender + " has an unknown delivery direction.";
                default:
                    return "Network event delivery failed: " + failure;
            }
        }

        private void ForgetNetworkWarningsFrom(string actorId)
        {
            if (_networkWarningWindows.Count == 0)
            {
                return;
            }

            List<(string Sender, NetworkWarningKind Kind)> forgotten = new();
            foreach ((string Sender, NetworkWarningKind Kind) key in _networkWarningWindows.Keys)
            {
                if (string.Equals(key.Sender, actorId, StringComparison.Ordinal))
                {
                    forgotten.Add(key);
                }
            }

            for (int index = 0; index < forgotten.Count; index++)
            {
                _networkWarningWindows.Remove(forgotten[index]);
            }
        }

        private void DeliverNetworkEvent(RbxNetworkEventMessage message)
        {
            try
            {
                if (message == null)
                {
                    WarnNetworkEvent(null, NetworkWarningKind.NullMessage);
                    return;
                }

                string admittedSender = null;
                if (message.Direction == RbxNetworkDirection.ClientToServer)
                {
                    admittedSender = DemandAdmittedNetworkSender(
                        message.SenderActorId, "deliver a network event");
                }

                if (!_registry.TryGet(message.RemoteId, out RbxInstance instance)
                    || !(instance is RbxRemoteEvent remote)
                    || remote.IsDestroyed)
                {
                    WarnNetworkEvent(message.SenderActorId, NetworkWarningKind.UnknownRemote);
                    return;
                }

                if (remote.Reliability != message.Reliability)
                {
                    WarnNetworkEvent(message.SenderActorId, NetworkWarningKind.ReliabilityMismatch,
                        remote);
                    return;
                }

                object[] arguments = message.Direction == RbxNetworkDirection.ClientToServer
                    ? _networkCodec.DecodeClientArguments(message.Payload, admittedSender)
                    : _networkCodec.DecodeArguments(message.Payload);
                remote.AttachScheduler(_scheduler);
                switch (message.Direction)
                {
                    case RbxNetworkDirection.ClientToServer:
                        RbxPlayer player = EnsureNetworkActor(admittedSender);
                        string previousSender = _scheduler.BeginSignalsOnBehalfOf(admittedSender);
                        try
                        {
                            remote.DeliverToServer(player, arguments);
                        }
                        finally
                        {
                            _scheduler.EndSignalsOnBehalfOf(previousSender);
                        }

                        return;
                    case RbxNetworkDirection.ServerToClient:
                        remote.DeliverToClient(message.RecipientActorId, arguments);
                        return;
                    case RbxNetworkDirection.ServerToAllClients:
                        IReadOnlyList<string> actorIds = _networkBridge.ActorIds;
                        for (int index = 0; index < actorIds.Count; index++)
                        {
                            remote.DeliverToClient(actorIds[index], arguments);
                        }

                        return;
                    default:
                        WarnNetworkEvent(message.SenderActorId, NetworkWarningKind.UnknownDirection);
                        return;
                }
            }
            catch (RbxError)
            {
                throw;
            }
            catch (Exception ex)
            {
                WarnNetworkEvent(message?.SenderActorId, NetworkWarningKind.DeliveryFailed,
                    failure: ex.Message);
            }
        }

        private void QueueNetworkRequest(RbxNetworkRequestMessage message,
            RbxNetworkRequestResponder responder)
        {
            _networkRequestSignal.Fire(message, responder);
        }

        private void OnInstanceUnregistered(InstanceRecord record)
        {
            if (record == null)
            {
                return;
            }

            _serverRemoteCallbacks.Remove(record.Id);
            _clientRemoteCallbacks.Remove(record.Id);
            // WHY here as well as in a registry-aware sink: a sink built without the registry (a host
            // sink, or the one a fresh restore builds) hears about destruction only through this call;
            // for one that already heard it the call is an idempotent no-op.
            _partSink.OnPartDestroyed(record.Id);
            if (record.Instance is RbxHumanoid humanoid)
            {
                humanoid.DetachHost();
                if (_characterMotors.Remove(humanoid, out IRbxCharacterMotor motor))
                {
                    ReleaseMotorContained(humanoid, motor);
                }
            }
        }

        private void DeliverNetworkRequest(object[] arguments)
        {
            RbxNetworkRequestMessage message = arguments != null && arguments.Length > 0
                ? arguments[0] as RbxNetworkRequestMessage
                : null;
            RbxNetworkRequestResponder responder = arguments != null && arguments.Length > 1
                ? arguments[1] as RbxNetworkRequestResponder
                : null;
            if (responder == null)
            {
                return;
            }

            try
            {
                if (message == null)
                {
                    responder.Fail("RemoteFunction request message was null");
                    return;
                }

                string admittedSender = null;
                if (message.Direction == RbxNetworkDirection.ClientToServer)
                {
                    admittedSender = DemandAdmittedNetworkSender(
                        message.SenderActorId, "invoke a remote function");
                }

                if (!_registry.TryGet(message.RemoteId, out RbxInstance instance)
                    || !(instance is RbxRemoteFunction remote)
                    || remote.IsDestroyed)
                {
                    responder.Fail("RemoteFunction request targeted an unknown remote");
                    return;
                }

                RemoteFunctionCallbackRegistration registration =
                    ResolveRemoteFunctionCallback(message);
                if (registration == null)
                {
                    string member = message.Direction == RbxNetworkDirection.ClientToServer
                        ? "OnServerInvoke"
                        : "OnClientInvoke";
                    responder.Fail(remote.GetFullName() + "." + member + " is not set");
                    return;
                }

                object[] decoded = message.Direction == RbxNetworkDirection.ClientToServer
                    ? _networkCodec.DecodeClientArguments(message.Payload, admittedSender)
                    : _networkCodec.DecodeArguments(message.Payload);
                int prefixCount = message.Direction == RbxNetworkDirection.ClientToServer ? 1 : 0;
                object[] callbackArguments = new object[decoded.Length + prefixCount];
                int destinationIndex = 0;
                if (prefixCount == 1)
                {
                    callbackArguments[0] = registration.Context.WrapInstance(
                        EnsureNetworkActor(admittedSender));
                    destinationIndex = 1;
                }

                for (int index = 0; index < decoded.Length; index++)
                {
                    callbackArguments[destinationIndex + index] =
                        _networkCodec.ToLuaValue(registration.Context, decoded[index]);
                }

                SpawnRemoteFunctionCallback(registration, callbackArguments, responder,
                    admittedSender);
            }
            catch (Exception ex)
            {
                if (!responder.IsCompleted)
                {
                    responder.Fail(ex.Message);
                }
            }
        }

        private string DemandAdmittedNetworkSender(string senderActorId,
            string operation)
        {
            string sender = senderActorId?.Trim() ?? "";
            if (sender.Length > 0 && IsNetworkActorAdmitted(sender))
            {
                return sender;
            }

            throw new RbxError(
                RbxErrorCode.NotAuthority,
                "actor '" + sender + "' cannot " + operation
                + ": sender was not admitted through the network bridge",
                "admit the trusted actor before accepting its network message");
        }

        private RemoteFunctionCallbackRegistration ResolveRemoteFunctionCallback(
            RbxNetworkRequestMessage message)
        {
            switch (message.Direction)
            {
                case RbxNetworkDirection.ClientToServer:
                    return _serverRemoteCallbacks.TryGetValue(
                        message.RemoteId, out RemoteFunctionCallbackRegistration server)
                        ? server
                        : null;
                case RbxNetworkDirection.ServerToClient:
                    if (_clientRemoteCallbacks.TryGetValue(message.RemoteId,
                            out Dictionary<string, RemoteFunctionCallbackRegistration> callbacks)
                        && callbacks.TryGetValue(message.RecipientActorId,
                            out RemoteFunctionCallbackRegistration client))
                    {
                        return client;
                    }

                    return null;
                default:
                    return null;
            }
        }

        /// <summary>
        /// Starts a RemoteFunction callback thread. <paramref name="senderActorId"/> names the client whose
        /// call this is (null for a server-to-client invoke); its call is charged to its own remote
        /// handler budget and refused with a failed response when that budget is full (MP-10).
        /// </summary>
        private void SpawnRemoteFunctionCallback(
            RemoteFunctionCallbackRegistration registration, object[] arguments,
            RbxNetworkRequestResponder responder, string senderActorId)
        {
            LuaFunction callbackRunner = new("RemoteFunction.callback", async (ctx, ct) =>
            {
                try
                {
                    LuaValue[] callbackArguments = ctx.Arguments.ToArray();
                    LuaValue[] results = await ctx.State.CallAsync(
                        registration.Callback, callbackArguments.AsSpan(), ct);
                    responder.Complete(_networkCodec.EncodeArguments(results));
                }
                catch (Exception ex)
                {
                    if (!responder.IsCompleted)
                    {
                        responder.Fail(ex.Message);
                    }
                }

                return ctx.Return();
            });
            LuaCsRbxSchedulerCallable callable = new(
                registration.OwnerState, new LuaValue(callbackRunner));
            try
            {
                string ownerModId = RequireTaskOwner(registration.Context);
                IRbxScriptThread thread;
                if (senderActorId == null)
                {
                    thread = _scheduler.SpawnSignal(ownerModId, callable, arguments);
                }
                else
                {
                    thread = _scheduler.SpawnSignal(ownerModId, callable, arguments, senderActorId,
                        MaxRemoteHandlerThreadsPerSender, out RbxError refusal);
                    if (refusal != null)
                    {
                        NoteRemoteHandlerRefusal(senderActorId, "a RemoteFunction call was refused",
                            refusal);
                        if (!responder.IsCompleted)
                        {
                            responder.Fail(refusal.Message);
                        }

                        return;
                    }
                }

                TrackScheduledThread(registration.Context, thread);
                if (thread == null)
                {
                    // WHY: the scheduler refused to start the callback and reported that to its owner;
                    // the caller would otherwise wait for its whole timeout for an answer that never comes.
                    if (!responder.IsCompleted)
                    {
                        responder.Fail("RemoteFunction callback of mod '" + ownerModId
                                       + "' could not start");
                    }

                    return;
                }

                if (thread is LuaCsRbxScriptThread luaThread && !luaThread.IsDead
                    && !responder.IsCompleted)
                {
                    luaThread.PendingResponder = responder;
                }
            }
            catch (Exception ex)
            {
                if (!responder.IsCompleted)
                {
                    responder.Fail(ex.Message);
                }
            }
        }

        private static List<LuaValue> ReadRemoteArguments(
            LuaFunctionExecutionContext ctx, int startIndex)
        {
            List<LuaValue> arguments = new();
            for (int index = startIndex; index < ctx.ArgumentCount; index++)
            {
                arguments.Add(Arg(ctx, index));
            }

            return arguments;
        }

        /// <summary>
        /// Per-frame input pump: polls the input source, diffs, and fires
        /// InputBegan/InputEnded/InputChanged at the scheduler's input-processing boundary.
        /// </summary>
        public void PumpInput()
        {
            _userInputService?.Step();
        }

        /// <summary>Fires PreAnimation at the scheduler's PreAnimation boundary.</summary>
        public void PumpPreAnimation(float dt)
        {
            FireRunServiceSignal(_runService?.PreAnimation, dt);
        }

        /// <summary>
        /// Fires PreSimulation and its legacy alias Stepped at the scheduler's PreSimulation
        /// boundary. Stepped keeps Roblox's legacy (runTime, dt) signature; PreSimulation takes the
        /// delta alone.
        /// </summary>
        public void PumpPreSimulation(float dt)
        {
            PumpPreSimulationCore(dt, dt);
        }

        /// <summary>
        /// <paramref name="runTimeDelta"/> is the unrounded frame delta Stepped's run time accumulates.
        /// </summary>
        /// <remarks>
        /// WHY a double accumulator fed the scheduler's double delta: a float run time drifted by
        /// seconds within an hour at 60 Hz and stopped advancing at 524,288 s (about six days), while
        /// the mirror types Stepped's time as a double (M2-16).
        /// </remarks>
        private void PumpPreSimulationCore(float dt, double runTimeDelta)
        {
            _registry.ProcessPreSimulation();
            RefreshCharacterMotors();
            if (_runService == null || _runService.IsDestroyed)
            {
                return;
            }

            _runServiceElapsed += runTimeDelta;
            if (_runService.PreSimulation.HasConnections)
            {
                _runService.PreSimulation.Fire(dt);
            }

            if (_runService.Stepped.HasConnections)
            {
                _runService.Stepped.Fire(_runServiceElapsed, dt);
            }
        }

        /// <summary>
        /// Advances every attached <see cref="UnityRbxCharacterMotor"/> by one fixed step (F10).
        /// Call this from the host's fixed-step pump (Unity FixedUpdate), never from the render
        /// frame — velocity-driven walking applied a variable number of times per simulated step
        /// would move characters at a rate that depends on frame rate.
        /// </summary>
        // WHY the dead check lives here rather than in RbxHumanoid: Humanoid.SetHealth already
        // clears the Humanoid's OWN walk target on death, but the motor keeps a separate target of
        // its own that nothing else ever clears — a killed character kept walking to wherever it
        // was headed, forever when CharacterAutoLoads is off. MoveTo(null) both stops the pump from
        // driving the motor further AND zeroes its horizontal velocity, so the corpse stops exactly
        // where it died instead of coasting on whatever velocity the last live step left it with.
        // Repeating the call every step while dead is cheap and self-correcting; it needs no extra
        // per-humanoid bookkeeping to run only once.
        public void StepCharacterMotors(float dt)
        {
            if (dt <= 0f)
            {
                return;
            }

            foreach (KeyValuePair<RbxHumanoid, IRbxCharacterMotor> pair in _characterMotors)
            {
                if (pair.Key.IsDead)
                {
                    pair.Value?.MoveTo(null);
                    continue;
                }

                // WHY every motor is stepped and not only CoreAI's own: a host that supplies its
                // own controller through IRbxCharacterMotorProvider gets the same fixed-step
                // cadence. Testing the concrete type here left a host motor's MoveTo never
                // advancing — the character stood still until the Humanoid's arrival timeout.
                pair.Value?.Step(dt);
            }
        }

        /// <summary>Fires PostSimulation at the scheduler's PostSimulation boundary.</summary>
        public void PumpPostSimulation(float dt)
        {
            FireRunServiceSignal(_runService?.PostSimulation, dt);
        }

        /// <summary>Fires legacy Heartbeat at the scheduler Heartbeat boundary.</summary>
        public void PumpHeartbeat(float dt)
        {
            FireRunServiceSignal(_runService?.Heartbeat, dt);
        }

        /// <summary>
        /// Fires PreRender and its legacy alias RenderStepped, then completes click picking, at the
        /// PreRender boundary. Both signals are withheld on a process that draws no frames.
        /// </summary>
        public void PumpPreRender(float dt)
        {
            if (_runService != null && !_runService.IsDestroyed)
            {
                _runService.FireRenderPhase(dt);
            }

            PumpClicks();
        }

        /// <summary>Runs the split frame pumps in their observable scheduler order.</summary>
        /// <remarks>
        /// WHY this is an EITHER/OR with <see cref="ModScheduler.Advance"/>, never both: since the
        /// scheduler became the frame authority, <see cref="PumpSchedulerPhase"/> already fires every
        /// phase from <c>PhaseReached</c>. A caller that pumps here AND advances runs the frame twice —
        /// Stepped/Heartbeat/RenderStepped fire twice, a mod's per-frame counter doubles, and a runaway
        /// handler is cut once per copy. This entry exists only for a host with no scheduler frame of
        /// its own; the production host (and every test that emulates it) advances the scheduler and
        /// calls nothing here.
        /// </remarks>
        public void PumpFrame(float dt)
        {
            PumpPreAnimation(dt);
            PumpPreSimulation(dt);
            PumpPostSimulation(dt);
            PumpHeartbeat(dt);
            PumpInput();
            PumpPreRender(dt);
        }

        private void FireRunServiceSignal(RbxScriptSignal signal, float dt)
        {
            if (_runService == null || _runService.IsDestroyed || signal == null
                || !signal.HasConnections)
            {
                return;
            }

            signal.Fire(dt);
        }

        /// <summary>
        /// Routes every scheduler phase boundary to its matching pump, in scheduler order.
        /// </summary>
        /// <remarks>
        /// WHY all six and not only input: the production frame path advances only the
        /// scheduler, so any phase ignored here never fires its signals and never steps
        /// character motors in a built player. Each phase fires exactly once per Advance;
        /// the standalone Pump* methods stay public for hosts that drive the frame manually.
        /// </remarks>
        private void PumpSchedulerPhase(SchedulerPhase phase, double deltaSeconds)
        {
            float frameDelta = (float)deltaSeconds;
            switch (phase)
            {
                case SchedulerPhase.PreAnimation:
                    PumpPreAnimation(frameDelta);
                    return;
                case SchedulerPhase.PreSimulation:
                    PumpPreSimulationCore(frameDelta, deltaSeconds);
                    return;
                case SchedulerPhase.PostSimulation:
                    PumpPostSimulation(frameDelta);
                    return;
                case SchedulerPhase.Heartbeat:
                    PumpHeartbeat(frameDelta);
                    return;
                case SchedulerPhase.InputProcessing:
                    PumpInput();
                    return;
                case SchedulerPhase.PreRender:
                    PumpPreRender(frameDelta);
                    return;
            }
        }

        /// <summary>
        /// The actor whose clicks the pick pump reports: this process's local player. Null (the
        /// default) lets the pump work it out — the player whose loaded character the camera follows,
        /// or else the only connected player.
        /// </summary>
        /// <remarks>
        /// WHY settable: only the composition knows which connected actor sits at this screen, and a
        /// host that runs several local actors names the one holding the mouse here.
        /// </remarks>
        public string LocalPlayerActorId { get; set; }

        /// <summary>
        /// Per-frame click pick: on the RISING edge of MouseButton1 (one fire per click), casts a
        /// camera ray through the mouse position, resolves the nearest world instance, and fires
        /// <c>MouseClick(playerWhoClicked)</c> of the deepest ClickDetector above it — a child of the
        /// hit part, or of one of its Model or Folder ancestors below Workspace — when the clicking
        /// player's character is within the detector's MaxActivationDistance of the hit part. Only the
        /// single nearest ray hit fires, so clicking one part never fires another part's detector, and
        /// clicking empty space fires nothing. Every step is null-guarded, so the headless default (no
        /// camera/physics) is a silent no-op.
        /// </summary>
        // TODO: backlog — MouseHoverEnter/MouseHoverLeave once the pick pump tracks the hovered part
        // across frames; today only MouseClick is driven.
        private void PumpClicks()
        {
            if (_userInputService == null || _pickSource == null)
            {
                return;
            }

            IInputSource input = _userInputService.InputSource;
            if (input == null)
            {
                return;
            }

            // WHY: edge-detect against last frame's held state so a held button fires once, not every
            // frame — Roblox delivers one MouseClick per press.
            bool down = input.IsMouseButtonDown(0);
            bool rising = down && !_mouseButton1Down;
            _mouseButton1Down = down;
            if (!rising)
            {
                return;
            }

            RbxVector2 location = input.GetMouseLocation();
            if (!_pickSource.TryPick(location, out InstanceId hitId, out double cameraDistanceStuds)
                || !_registry.TryGet(hitId, out RbxInstance hit)
                || hit.IsDestroyed)
            {
                return;
            }

            RbxClickDetector detector = FindClickDetector(hit);
            // WHY: skip everything when nothing listens, so an unlistened detector boxes nothing.
            if (detector == null || detector.IsDestroyed || !detector.MouseClick.HasConnections)
            {
                return;
            }

            RbxPlayer player = ResolveClickingPlayer();
            if (MeasureClickDistance(player, hit, cameraDistanceStuds)
                <= detector.MaxActivationDistance)
            {
                detector.MouseClick.Fire(new object[] { player });
            }
        }

        /// <summary>
        /// The deepest ClickDetector that owns a click on <paramref name="hit"/>: the first one among
        /// the children of the hit part, else of its nearest Model or Folder ancestor that has one,
        /// walking up to (not including) Workspace.
        /// </summary>
        /// <remarks>
        /// WHY ancestors: Roblox detectors work when parented to a BasePart, a Model or a Folder, and a
        /// door Model with its detector at model level is the common shape; searching only the hit
        /// part's children left it inert. WHY the first child: of sibling detectors the first takes
        /// priority. WHY Workspace is excluded: it is a Model too, and a detector parked there would
        /// otherwise claim every click in the world.
        /// </remarks>
        private RbxClickDetector FindClickDetector(RbxInstance hit)
        {
            for (RbxInstance node = hit; node != null && !ReferenceEquals(node, _workspace)
                                         && !ReferenceEquals(node, _game); node = node.Parent)
            {
                if (!node.IsA("BasePart") && !node.IsA("Model") && !node.IsA("Folder"))
                {
                    continue;
                }

                foreach (RbxInstance child in node.GetChildren())
                {
                    if (child is RbxClickDetector detector && !detector.IsDestroyed)
                    {
                        return detector;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// The player a click on this screen belongs to: <see cref="LocalPlayerActorId"/>'s player when
        /// it is set, else the player whose loaded character the camera follows, else the only
        /// connected player; null when none of those resolves.
        /// </summary>
        private RbxPlayer ResolveClickingPlayer()
        {
            if (!string.IsNullOrEmpty(LocalPlayerActorId))
            {
                return _players.TryGetByActorId(LocalPlayerActorId, out RbxPlayer named)
                       && !named.IsDestroyed
                    ? named
                    : null;
            }

            for (RbxInstance node = CameraSubject; node != null && !node.IsDestroyed; node = node.Parent)
            {
                RbxPlayer followed = _players.GetPlayerFromLoadedCharacter(node);
                if (followed != null)
                {
                    return followed;
                }
            }

            IReadOnlyList<RbxPlayer> players = _players.GetPlayers();
            return players.Count == 1 ? players[0] : null;
        }

        /// <summary>
        /// Studs between the clicking player's character root and the nearest point of the clicked
        /// part's box; the camera-to-hit distance when there is no player or no character.
        /// </summary>
        /// <remarks>
        /// WHY the character and not the camera: MaxActivationDistance is the distance between the
        /// player's character and the detector. Measured from the camera, a third-person camera a dozen
        /// studs behind the character made a detector twenty studs away unclickable, and a free-flying
        /// camera let a far-away character click anything. WHY the nearest point of the box: the click
        /// lands on the part's surface, so a player standing on a large part is next to what they
        /// clicked even when its centre is far away. WHY the camera fallback (OURS, not Roblox's):
        /// with no character there is nothing else to measure from, and a spectator without an avatar
        /// keeps the range check it had.
        /// </remarks>
        private double MeasureClickDistance(RbxPlayer player, RbxInstance hit, double cameraDistanceStuds)
        {
            RbxInstance character = player?.Character;
            RbxInstance root = character != null && !character.IsDestroyed
                ? character.FindFirstChild(Mods.Rbx.Instances.Networking.RbxCharacterFactory.RootPartName)
                : null;
            if (root == null || !root.IsA("BasePart"))
            {
                return cameraDistanceStuds;
            }

            RbxVector3 from = _partSink.GetLivePositionStuds(root.Id);
            if (!hit.IsA("BasePart"))
            {
                return (from - _partSink.GetLivePositionStuds(hit.Id)).Magnitude;
            }

            PartProperties part = _partSink.GetPartPropertiesOrDefault(hit.Id);
            RbxVector3 local = part.CFrame.PointToObjectSpace(from);
            RbxVector3 half = part.Size * 0.5f;
            RbxVector3 nearest = new(
                ClampAxis(local.X, half.X),
                ClampAxis(local.Y, half.Y),
                ClampAxis(local.Z, half.Z));
            return (from - part.CFrame.PointToWorldSpace(nearest)).Magnitude;
        }

        private static float ClampAxis(float value, float halfExtent)
        {
            if (value > halfExtent)
            {
                return halfExtent;
            }

            return value < -halfExtent ? -halfExtent : value;
        }

        /// <summary>Camera.CameraType value shared by every script of this world (state only —
        /// no behavior is derived from it yet; following is driven by <see cref="CameraSubject"/>).</summary>
        internal RbxEnumItem CameraTypeItem { get; set; }

        /// <summary>Camera.CameraSubject; non-null while the rig follows it.</summary>
        internal RbxInstance CameraSubject { get; private set; }

        /// <summary>Shared write path for Camera.CameraSubject and camera_follow: nil stops the
        /// follow, an instance must have a backing object in the world to be followed.</summary>
        internal void SetCameraSubject(RbxInstance subject)
        {
            if (subject == null)
            {
                CameraSubject = null;
                _cameraRig.StopFollowing();
                return;
            }

            if (!_cameraRig.Follow(subject.Id))
            {
                throw RbxError.BadArgument(
                    "camera follow target \"" + subject.GetFullName() + "\" has no backing object",
                    "parent the instance under Workspace before following it");
            }

            CameraSubject = subject;
        }

        /// <summary>
        /// Registers the Roblox surface on one script registry at the given capability tier.
        /// <paramref name="ownerModId"/> follows the gameplay-bindings ownership convention: the
        /// persistent mod id (instances created by the mod get <c>mod:&lt;id&gt;</c> origin and are
        /// swept by hot-reload teardown via <see cref="InstanceRegistry.GetOwnedBy"/>), or null for
        /// the ownerless one-off executor (instances get a <c>console:&lt;n&gt;</c> origin and stay
        /// world-owned per the ownership-ledger decision).
        /// </summary>
        public void Register(IScriptFunctionRegistry registry, LuaCapabilities capabilities,
            string ownerModId = null)
        {
            RegisterCore(registry, capabilities, ownerModId, null, null);
        }

        /// <summary>
        /// Registers an actor-scoped one-off mutation surface without changing the legacy
        /// ownerless registration signature used by consumers that do not submit envelopes.
        /// </summary>
        public void Register(IScriptFunctionRegistry registry, LuaCapabilities capabilities,
            string ownerModId, ActorContext actorContext)
        {
            RegisterCore(registry, capabilities, ownerModId, actorContext, null);
        }

        /// <summary>
        /// Registers an actor-scoped one-off mutation surface without changing the legacy
        /// ownerless registration signature used by consumers that do not submit envelopes.
        /// </summary>
        public void Register(IScriptFunctionRegistry registry, LuaCapabilities capabilities,
            string ownerModId, ActorContext actorContext, MutationEnvelope mutationEnvelope)
        {
            RegisterCore(registry, capabilities, ownerModId, actorContext, mutationEnvelope);
        }

        private void RegisterCore(IScriptFunctionRegistry registry, LuaCapabilities capabilities,
            string ownerModId, ActorContext? actorContext, MutationEnvelope? mutationEnvelope)
        {
            if (registry == null)
            {
                throw new ArgumentNullException(nameof(registry));
            }

            if ((capabilities & LuaCapabilities.Read) == 0)
            {
                return;
            }

            if (!(registry is LuaCsApiRegistry luaRegistry))
            {
                throw new ArgumentException(
                    "LuaCsRbxApiBindings requires the Lua-CSharp registry adapter; a second " +
                    "engine ships its own Roblox binding adapter next to it.", nameof(registry));
            }

            string originTag = ownerModId != null
                ? OriginTag.FromMod(ownerModId)
                // WHY: no invocation identity reaches this layer yet (execute_lua invocation ids
                // arrive with the tooling slice), so console-origin instances are grouped per
                // registered console surface — still selectively cleanable by prefix.
                : OriginTag.FromConsole(
                    "session-" + Interlocked.Increment(ref _consoleInvocationCounter));

            LuaCsRbxModContext context;
            if (actorContext.HasValue)
            {
                context = mutationEnvelope.HasValue
                    ? new LuaCsRbxModContext(
                        this, capabilities, ownerModId, originTag,
                        actorContext.Value, mutationEnvelope.Value)
                    : new LuaCsRbxModContext(
                        this, capabilities, ownerModId, originTag,
                        actorContext.Value);
            }
            else
            {
                context = new LuaCsRbxModContext(
                    this, capabilities, ownerModId, originTag);
            }
            if (!context.IsNetworkServer)
            {
                EnsureNetworkActor(context.ActorContext.ActorId);
            }
            if (!string.IsNullOrWhiteSpace(ownerModId))
            {
                _actorContextsByOwnerModId[ownerModId] = context.ActorContext;
                _currentSchedulerGenerationByMod[ownerModId] =
                    context.ConnectionGeneration;
            }

            luaRegistry.RegisterValue("Vector3", LuaCsRbxDatatypeBindings.BuildVector3Global);
            luaRegistry.RegisterValue("Vector2", LuaCsRbxDatatypeBindings.BuildVector2Global);
            luaRegistry.RegisterValue("CFrame", LuaCsRbxDatatypeBindings.BuildCFrameGlobal);
            luaRegistry.RegisterValue("Color3", LuaCsRbxDatatypeBindings.BuildColor3Global);
            luaRegistry.RegisterValue("UDim", LuaCsRbxDatatypeBindings.BuildUDimGlobal);
            luaRegistry.RegisterValue("UDim2", LuaCsRbxDatatypeBindings.BuildUDim2Global);
            luaRegistry.RegisterValue("Random", LuaCsRbxDatatypeBindings.BuildRandomGlobal);
            luaRegistry.RegisterValue("Enum", () => LuaCsRbxDatatypeBindings.BuildEnumGlobal(_enums));
            luaRegistry.RegisterValue("TweenInfo",
                () => LuaCsRbxDatatypeBindings.BuildTweenInfoGlobal(_enums));
            // WHY per-context and not a shared table like TweenInfo: a RaycastParams filter holds
            // Instances, and reading the list back has to wrap them for the mod that asked.
            luaRegistry.RegisterValue("RaycastParams",
                () => LuaCsRbxInstanceBindings.BuildRaycastParamsGlobal(context));
            if (!string.IsNullOrWhiteSpace(ownerModId))
            {
                luaRegistry.RegisterValue("script", () =>
                    context.WrapInstance(GetOrCreateExecutingScript(context)));
            }

            luaRegistry.RegisterValue("game", () => context.WrapInstance(_game));
            luaRegistry.RegisterValue("workspace", () => context.WrapInstance(_workspace));
            // WHY: input reads are open at the Read tier (observing input mutates nothing); the
            // global aliases the same instance game:GetService("UserInputService") resolves.
            if (_userInputService != null)
            {
                luaRegistry.RegisterValue("UserInputService",
                    () => context.WrapInstance(_userInputService));
            }

            luaRegistry.RegisterValue("task", () => BuildTaskGlobal(context));
            luaRegistry.RegisterValue("wait", () => new LuaValue(Fn(
                "wait", ctx => ScheduleWait(
                    context, ctx, "wait", LegacySchedulerMinimumDelaySeconds, true))));
            luaRegistry.RegisterValue("spawn", () => new LuaValue(Fn(
                "spawn", ctx => LegacySpawn(context, ctx))));
            luaRegistry.RegisterValue("delay", () => new LuaValue(Fn(
                "delay", ctx => LegacyDelay(context, ctx))));
            // WHY: time() reads the injectable source's scaled game time, which defaults to the
            // scheduler's clock (fed the already-scaled host delta by the frame driver), never
            // Unity Time.time directly — so it freezes at time scale 0 like task.wait does.
            luaRegistry.RegisterValue("time", () => new LuaValue(Fn(
                "time", _ => _clockSource.GameTimeSeconds, context)));
            luaRegistry.RegisterValue("tick", () => new LuaValue(Fn(
                "tick", _ =>
                {
                    if (!context.HasLoggedTickDeprecation)
                    {
                        context.HasLoggedTickDeprecation = true;
                        _log?.Invoke(
                            "[RbxApi] tick() is deprecated by Roblox; use os.time() for " +
                            "timestamps or workspace:GetServerTimeNow() for synchronized time " +
                            "instead. (Logged once per mod.)");
                    }

                    return _clockSource.UnixTimeSecondsFractional;
                }, context)));
            // WHY: the stock os library stays removed by the sandbox (execute/remove/rename/exit
            // and friends are a sandbox escape); mods get this two-member table and nothing else.
            luaRegistry.RegisterValue("os", () => BuildOsTable());
            luaRegistry.RegisterValue("typeof", () => new LuaValue(Fn("typeof", ctx =>
                ctx.ArgumentCount == 0
                    ? throw RbxError.BadArgument("typeof expects a value at argument 1",
                        "pass the value to name, e.g. typeof(workspace)")
                    : RobloxTypeOf(ctx.State, ctx.GetArgument(0)))));
            luaRegistry.RegisterValue("warn", () => new LuaValue(BuildWarn(context)));
            RegisterUnimplementedGlobals(luaRegistry);
            // WHY: registered on every tier so a WorldEdit-less call fails with the actionable
            // capability message instead of "attempt to call a nil value".
            luaRegistry.RegisterValue("camera_set_cframe",
                () => new LuaValue(BuildCameraSetCFrame(context)));
            luaRegistry.RegisterValue("camera_follow",
                () => new LuaValue(BuildCameraFollow(context)));
            // WHY on every tier, for the same reason: a read-tier Instance.new used to index a nil
            // global; now it names the WorldEdit capability it lacks (M1-34). Creation itself is
            // refused before anything is read or created.
            luaRegistry.RegisterValue("Instance", () => BuildInstanceGlobal(context));
        }

        /// <summary>
        /// Roblox <c>typeof</c>: the Roblox type name of every datatype CoreAI binds
        /// (<c>"Instance"</c>, <c>"Vector3"</c>, <c>"EnumItem"</c>, <c>"RBXScriptSignal"</c>, ...), the
        /// <c>Enum</c> global as <c>"Enums"</c>, a task thread handle as <c>"thread"</c>, and plain
        /// Lua values exactly as <c>type</c> names them.
        /// </summary>
        internal static string RobloxTypeOf(LuaState state, LuaValue value)
        {
            switch (value.Type)
            {
                case LuaValueType.Nil: return "nil";
                case LuaValueType.Boolean: return "boolean";
                case LuaValueType.Number: return "number";
                case LuaValueType.String: return "string";
                case LuaValueType.Function: return "function";
                case LuaValueType.Thread: return "thread";
                case LuaValueType.Table:
                    return IsEnumsGlobal(state, value) ? "Enums" : "table";
            }

            if (TryGetInstance(value, out LuaCsRbxInstanceProxy _))
            {
                return "Instance";
            }

            if (!value.TryRead(out LuaCsRbxValueBox box))
            {
                return "userdata";
            }

            switch (box.Value)
            {
                case RbxVector3 _: return "Vector3";
                case RbxVector2 _: return "Vector2";
                case RbxCFrame _: return "CFrame";
                case RbxColor3 _: return "Color3";
                case RbxUDim _: return "UDim";
                case RbxUDim2 _: return "UDim2";
                case RbxTweenInfo _: return "TweenInfo";
                case RbxEnumItem _: return "EnumItem";
                case RbxEnum _: return "Enum";
                case RbxRandom _: return "Random";
                case RbxScriptSignal _: return "RBXScriptSignal";
                case RbxScriptConnection _: return "RBXScriptConnection";
                // WHY: InputObject is an Instance class in Roblox, so typeof names it "Instance".
                case RbxInputObject _: return "Instance";
                case IRbxScriptThread _: return "thread";
            }

            // WHY read from the metatable for the rest: TweenInfo, RaycastParams and RaycastResult box
            // a carrier type private to the binding that builds them, but every datatype metatable
            // names its __tostring "<Type>.__tostring", so the type name is recoverable there.
            if (box.Metatable != null
                && box.Metatable[Metamethods.ToString].TryRead(out LuaFunction toString)
                && toString.Name != null
                && toString.Name.EndsWith(".__tostring", StringComparison.Ordinal))
            {
                return toString.Name.Substring(0, toString.Name.Length - ".__tostring".Length);
            }

            return "userdata";
        }

        private static bool IsEnumsGlobal(LuaState state, LuaValue value)
        {
            LuaValue enums = state?.Environment["Enum"] ?? LuaValue.Nil;
            return enums.Type == LuaValueType.Table && value.TryRead(out LuaTable table)
                   && enums.TryRead(out LuaTable enumsTable) && ReferenceEquals(table, enumsTable);
        }

        /// <summary>
        /// Attaches the per-mod log that <c>warn</c> writes to at <see cref="LuaLogLevel.Warn"/>, next to
        /// what a mod's <c>print</c> writes at <see cref="LuaLogLevel.Print"/>. Null detaches it; without
        /// one, <c>warn</c> goes to this world's log sink.
        /// </summary>
        public void AttachModLog(ILuaLogService modLog)
        {
            _modLog = modLog;
        }

        /// <summary>
        /// Lines one script's <c>warn</c> may write to the world's log sink per
        /// <see cref="WarnLogWindowSeconds"/>; the rest are counted and summed into the next line.
        /// </summary>
        internal const int MaxWarnLinesPerWindow = 20;

        /// <summary>The window <see cref="MaxWarnLinesPerWindow"/> is counted over, in process seconds.</summary>
        internal const double WarnLogWindowSeconds = 10d;

        private sealed class WarnLogWindow
        {
            public double OpenedAtSeconds;
            public int Lines;
            public long Suppressed;
        }

        /// <summary>
        /// Roblox <c>warn(...)</c>: the arguments, converted like <c>tostring</c> and joined by spaces,
        /// go to the mod's log as a warning entry, or — for the one-off executor, or while no mod log is
        /// attached — to the world's log sink as a line naming the script, at most
        /// <see cref="MaxWarnLinesPerWindow"/> lines per <see cref="WarnLogWindowSeconds"/>.
        /// </summary>
        /// <remarks>
        /// WHY the log-sink path is capped and the mod log is not: the mod log is a bounded ring buffer
        /// per mod, while every line on the sink becomes a host warning with a stack trace, so a script
        /// warning every frame would flood the host's log, disk and CPU. WHY the window lives in this
        /// closure: it is per registration, so it goes away with the mod's state and needs no cleanup.
        /// </remarks>
        private LuaFunction BuildWarn(LuaCsRbxModContext context)
        {
            string ownerModId = string.IsNullOrWhiteSpace(context.OwnerModId) ? null : context.OwnerModId;
            string source = ownerModId == null ? "one-off script" : "mod '" + ownerModId + "'";
            WarnLogWindow window = new() { OpenedAtSeconds = _clockSource.ProcessTimeSeconds };
            return new LuaFunction("warn", async (ctx, ct) =>
            {
                string[] parts = new string[ctx.ArgumentCount];
                for (int index = 0; index < parts.Length; index++)
                {
                    parts[index] = await DescribeForLog(ctx.State, ctx.GetArgument(index), ct);
                }

                // WHY spaces: Roblox's output joins print and warn arguments with a space.
                string message = string.Join(" ", parts);
                ILuaLogService modLog = _modLog;
                if (modLog != null && ownerModId != null)
                {
                    // WHY contained like the runtime's own log appends: a failing log consumer must not
                    // turn a warning into an error of the mod that wrote it.
                    try
                    {
                        modLog.Append(new LuaLogEntry
                        {
                            ModId = ownerModId,
                            Level = LuaLogLevel.Warn,
                            Message = message
                        });
                    }
                    catch (Exception ex)
                    {
                        _log?.Invoke("[RbxApi] The mod log refused a warn from " + source + ": " + ex.Message);
                    }

                    return ctx.Return();
                }

                if (_log == null)
                {
                    return ctx.Return();
                }

                double now = _clockSource.ProcessTimeSeconds;
                double open = now - window.OpenedAtSeconds;
                if (open < 0d || open >= WarnLogWindowSeconds)
                {
                    long suppressed = window.Suppressed;
                    window.OpenedAtSeconds = now;
                    window.Lines = 0;
                    window.Suppressed = 0;
                    if (suppressed > 0)
                    {
                        _log("[RbxApi] " + suppressed + " more warn lines from " + source
                             + " were dropped (at most " + MaxWarnLinesPerWindow + " every "
                             + WarnLogWindowSeconds + " s reach this log)");
                    }
                }

                if (window.Lines >= MaxWarnLinesPerWindow)
                {
                    window.Suppressed++;
                    return ctx.Return();
                }

                window.Lines++;
                _log("[RbxApi] warn from " + source + ": " + message);
                return ctx.Return();
            });
        }

        /// <summary>
        /// A value's <c>tostring</c> text, including a datatype's own <c>__tostring</c>.
        /// </summary>
        /// <remarks>
        /// WHY an error in a <c>__tostring</c> is not caught: Roblox's warn raises it too, and a budget
        /// trip inside a runaway <c>__tostring</c> travels as the same exception and must reach the
        /// scheduler instead of being turned into a log line.
        /// </remarks>
        private static async System.Threading.Tasks.ValueTask<string> DescribeForLog(LuaState state,
            LuaValue value, CancellationToken cancellationToken)
        {
            LuaValue toString = state.Environment["tostring"];
            if (toString.Type != LuaValueType.Function)
            {
                return value.ToString();
            }

            LuaValue[] converted = await state.CallAsync(toString, new[] { value }.AsSpan(),
                cancellationToken);
            return converted.Length > 0 && converted[0].Type == LuaValueType.String
                ? converted[0].Read<string>()
                : value.ToString();
        }

        /// <summary>
        /// The Roblox globals CoreAI does not implement, as locked metatables whose every read, write
        /// or call raises the same NOT_IMPLEMENTED stub the catalog raises for a known member, so
        /// <c>BrickColor.new("Bright red")</c> names what is missing and what to use instead rather than
        /// failing with "attempt to index a nil value".
        /// </summary>
        /// <remarks>
        /// WHY every one is backlog or unsupported: none is scheduled on a roadmap rung, and a stub that
        /// names a rung promises a delivery date. WHY built once for the process: a locked metatable
        /// cannot be reached from Lua, so every mod can share it.
        /// </remarks>
        private static readonly KeyValuePair<string, LuaTable>[] UnimplementedGlobalMetas =
            BuildUnimplementedGlobalMetas();

        private static KeyValuePair<string, LuaTable>[] BuildUnimplementedGlobalMetas()
        {
            const string overlapWorkaround =
                "cast with workspace:Raycast, or track overlaps with BasePart.Touched/TouchEnded";
            const RbxKnownUnimplementedMemberStatus backlog = RbxKnownUnimplementedMemberStatus.Backlog;
            return new[]
            {
                UnimplementedGlobalMeta("BrickColor", backlog,
                    "use Color3.fromRGB(r, g, b) and assign it to part.Color"),
                UnimplementedGlobalMeta("NumberSequence", backlog,
                    "keep the keypoints in a Lua table; nothing CoreAI binds takes a NumberSequence yet"),
                UnimplementedGlobalMeta("ColorSequence", backlog,
                    "use a single Color3; nothing CoreAI binds takes a ColorSequence yet"),
                UnimplementedGlobalMeta("NumberRange", backlog,
                    "keep the minimum and maximum in two numbers and draw with Random:NextNumber(min, max)"),
                UnimplementedGlobalMeta("Ray", backlog,
                    "use workspace:Raycast(origin, direction, raycastParams)"),
                UnimplementedGlobalMeta("Region3", backlog, overlapWorkaround),
                UnimplementedGlobalMeta("Rect", backlog,
                    "keep the Min and Max corners as two Vector2 values"),
                UnimplementedGlobalMeta("PhysicalProperties", backlog,
                    "use host-side Rigidbody and collider settings until physical properties land"),
                UnimplementedGlobalMeta("OverlapParams", backlog, overlapWorkaround),
                UnimplementedGlobalMeta("DateTime", backlog,
                    "use os.time() for Unix seconds, or workspace:GetServerTimeNow() for time the server "
                    + "and every client agree on"),
                // WHY unsupported rather than backlog: a table every mod of the world can write is the
                // shared mutable state mod isolation exists to prevent.
                UnimplementedGlobalMeta("shared", RbxKnownUnimplementedMemberStatus.Unsupported,
                    "share values between mods with mods_export/mods_get, or with attributes on an "
                    + "instance both mods can see")
            };
        }

        private static KeyValuePair<string, LuaTable> UnimplementedGlobalMeta(string name,
            RbxKnownUnimplementedMemberStatus status, string workaround)
        {
            return new KeyValuePair<string, LuaTable>(name,
                BuildUnimplementedGlobalMeta(name, status, workaround));
        }

        private static void RegisterUnimplementedGlobals(LuaCsApiRegistry luaRegistry)
        {
            for (int index = 0; index < UnimplementedGlobalMetas.Length; index++)
            {
                LuaTable meta = UnimplementedGlobalMetas[index].Value;
                // WHY a fresh table per registration under the shared metatable: a script can still
                // rawset into the table itself, and that must never leak into another mod's copy.
                luaRegistry.RegisterValue(UnimplementedGlobalMetas[index].Key, () =>
                {
                    LuaTable stub = new();
                    stub.Metatable = meta;
                    return new LuaValue(stub);
                });
            }
        }

        private static LuaTable BuildUnimplementedGlobalMeta(string name,
            RbxKnownUnimplementedMemberStatus status, string workaround)
        {
            LuaTable meta = new();
            meta[Metamethods.Index] = Fn(name + ".__index", ctx =>
                throw RbxKnownUnimplementedErrors.ForMember(
                    DescribeUnimplementedAccess(name, Arg(ctx, 1)), status, null, workaround));
            meta[Metamethods.NewIndex] = Fn(name + ".__newindex", ctx =>
                throw RbxKnownUnimplementedErrors.ForMember(
                    DescribeUnimplementedAccess(name, Arg(ctx, 1)), status, null, workaround));
            meta[Metamethods.Call] = Fn(name + ".__call", _ =>
                throw RbxKnownUnimplementedErrors.ForMember(name, status, null, workaround));
            meta[Metamethods.ToString] = Fn(name + ".__tostring", _ => name);
            return Lock(meta);
        }

        private static string DescribeUnimplementedAccess(string name, LuaValue key)
        {
            return key.Type == LuaValueType.String ? name + "." + key.Read<string>() : name;
        }

        private bool TryGetExecutingScriptBacking(string ownerModId,
            out ExecutingScriptBacking backing)
        {
            if (_executingScriptsByMod.TryGetValue(ownerModId, out backing)
                && !backing.Container.IsDestroyed && !backing.Script.IsDestroyed)
            {
                return true;
            }

            _executingScriptsByMod.Remove(ownerModId);
            backing = null;
            return false;
        }

        private RbxInstance GetOrCreateExecutingScript(LuaCsRbxModContext context)
        {
            if (TryGetExecutingScriptBacking(context.OwnerModId,
                    out ExecutingScriptBacking existing))
            {
                return existing.Script;
            }

            RbxInstance serverScripts = _game.FindFirstChildOfClass("ServerScriptService")
                ?? throw new InvalidOperationException(
                    "the game tree has no ServerScriptService for the executing Script");
            RbxInstance container = null;
            RbxInstance script = null;
            try
            {
                container = _registry.Create(
                    "Folder", context.OwnerModId, context.OriginTag,
                    isRuntimeInfrastructure: true);
                container.Name = context.OwnerModId;
                script = _registry.Create(
                    "Script", context.OwnerModId, context.OriginTag,
                    isRuntimeInfrastructure: true);
                script.Name = context.OwnerModId;
                script.Parent = container;
                container.Parent = serverScripts;

                ExecutingScriptBacking backing = new(container, script);
                _executingScriptsByMod[context.OwnerModId] = backing;
                return script;
            }
            catch
            {
                script?.Destroy();
                container?.Destroy();
                throw;
            }
        }

        // ---- camera_* convenience globals ---------------------------------------------------

        private LuaFunction BuildCameraSetCFrame(LuaCsRbxModContext context)
        {
            return Fn("camera_set_cframe", ctx =>
            {
                RbxInstance camera = _workspace.FindFirstChildOfClass("Camera");
                context.RequireWorldEditForWrite(camera, "CFrame");
                RbxCFrame cframe = ReadCFrame(ctx, 0, "camera_set_cframe");
                // WHY the shared write path: it fires Camera.Changed("CFrame") like the property write
                // does, and a handler watching the camera must not miss moves made through this global.
                if (camera != null)
                {
                    LuaCsRbxInstanceBindings.SetCameraCFrame(_cameraRig, camera, in cframe);
                }
                else
                {
                    _cameraRig.SetCFrame(cframe);
                }

                context.RecordMutation(camera);
                return LuaValue.Nil;
            });
        }

        private LuaFunction BuildCameraFollow(LuaCsRbxModContext context)
        {
            return Fn("camera_follow", ctx =>
            {
                RbxInstance camera = _workspace.FindFirstChildOfClass("Camera");
                context.RequireWorldEditForWrite(camera, "CameraSubject");
                LuaValue target = Arg(ctx, 0);
                RbxInstance subject = null;
                if (target.Type != LuaValueType.Nil)
                {
                    if (!TryGetInstance(target, out LuaCsRbxInstanceProxy proxy))
                    {
                        throw RbxError.BadArgument(
                            "camera_follow expects an Instance or nil at argument 1",
                            "pass a world instance to follow (or nil to stop), got "
                            + Describe(target) + " at argument 1");
                    }

                    subject = proxy.Instance;
                }

                RbxInstance previousSubject = CameraSubject;
                SetCameraSubject(subject);
                context.RecordMutation(camera);
                // WHY notified here as the Camera.CameraSubject write does: the subject lives on these
                // bindings, not on the Camera instance, so no setter of the instance can fire Changed.
                if (camera != null && !ReferenceEquals(previousSubject, CameraSubject))
                {
                    camera.NotifyPropertyChanged("CameraSubject");
                }

                return LuaValue.Nil;
            });
        }

        // ---- Instance.new -------------------------------------------------------------------

        private LuaValue BuildInstanceGlobal(LuaCsRbxModContext context)
        {
            LuaTable t = new();
            t["new"] = Fn("Instance.new", ctx =>
            {
                context.RequireWorldEdit("Instance.new");
                string className = ReadString(ctx, 0, "Instance.new");
                LuaValue parentValue = Arg(ctx, 1);
                RbxInstance parentInstance = null;
                RbxInstance creationAnchor = null;
                if (parentValue.Type != LuaValueType.Nil)
                {
                    if (!context.HasLoggedInstanceNewParentDeprecation)
                    {
                        context.HasLoggedInstanceNewParentDeprecation = true;
                        _log?.Invoke(
                            "[RbxApi] Instance.new(\"" + className + "\", parent) — the parent " +
                            "argument is deprecated by Roblox; set instance.Parent after " +
                            "configuring the instance instead. (Logged once per mod.)");
                    }

                    if (!TryGetInstance(parentValue, out LuaCsRbxInstanceProxy parent))
                    {
                        throw RbxError.BadArgument(
                            "Instance.new expects an Instance at argument 2",
                            "pass an Instance parent, got " + Describe(parentValue) + " at argument 2");
                    }

                    parentInstance = parent.Instance;
                    context.RequireCreateUnder(parentInstance);
                }
                else
                {
                    creationAnchor = context.RequireUnparentedCreationAnchor();
                }

                RbxInstance instance = _registry.CreateScripted(
                    className, context.OwnerModId, context.OriginTag);
                try
                {
                    // WHY: the sink answers reads with Roblox defaults for an unpushed Part, but the
                    // world package refuses to capture a BasePart without stored state; seeding the
                    // default bundle here keeps every scripted Part capturable from its first frame.
                    if (instance.IsA("BasePart"))
                    {
                        PartProperties defaults = PartProperties.CreateDefault();
                        _partSink.SetPartProperties(instance.Id, in defaults);
                    }

                    if (parentInstance != null)
                    {
                        instance.Parent = parentInstance;
                    }
                    else if (creationAnchor != null)
                    {
                        context.RecordMutation(creationAnchor);
                    }

                    return context.WrapInstance(instance);
                }
                catch
                {
                    instance.Destroy();
                    throw;
                }
            }, context);
            // TODO: backlog — Instance.fromExisting (not scheduled; Clone covers the corpus).
            t["fromExisting"] = Fn("Instance.fromExisting", _ => throw RbxKnownUnimplementedErrors.ForMember(
                "Instance.fromExisting", RbxKnownUnimplementedMemberStatus.Backlog, null,
                "use instance:Clone() instead"), context);
            return new LuaValue(t);
        }

        private LuaValue BuildTaskGlobal(LuaCsRbxModContext context)
        {
            LuaTable t = new();
            LuaTable threadMeta = Lock(new LuaTable());
            t["wait"] = Fn("task.wait", ctx => ScheduleWait(
                context, ctx, "task.wait", 0d, false));
            t["_resumeValue"] = Fn("task._resumeValue", ctx =>
                ReadWaitResumeValue(context, ctx));
            t["_scheduleSignalWait"] = Fn("task._scheduleSignalWait", ctx =>
                ScheduleSignalWait(context, ctx));
            t["_signalResumeValues"] = Fn("task._signalResumeValues", ctx =>
                ReadSignalResumeValues(context, ctx));
            t["_scheduleRemoteInvokeServer"] = Fn("task._scheduleRemoteInvokeServer", ctx =>
                ScheduleRemoteFunctionInvoke(context, ctx, true));
            t["_scheduleRemoteInvokeClient"] = Fn("task._scheduleRemoteInvokeClient", ctx =>
                ScheduleRemoteFunctionInvoke(context, ctx, false));
            t["_remoteFunctionResumeValues"] = Fn("task._remoteFunctionResumeValues", ctx =>
                ReadRemoteFunctionResumeValues(context, ctx));
            t["_warnInfiniteYield"] = Fn("task._warnInfiniteYield", ctx =>
                WarnInfiniteYield(context, ctx));
            t["_checkWaitForChild"] = FnMulti("task._checkWaitForChild",
                ReadWaitForChildArguments, context);
            t["_realtime"] = Fn("task._realtime", _ =>
                LuaCsValueMarshaller.Unbox(UnityEngine.Time.realtimeSinceStartupAsDouble));
            t["_buildCharacter"] = Fn("task._buildCharacter", ctx =>
                BuildCharacterForLoad(context, ctx));
            t["_noteLoadCharacterDeprecation"] = Fn("task._noteLoadCharacterDeprecation", _ =>
            {
                context.NoteLoadCharacterDeprecation(_log);
                return LuaValue.Nil;
            });
            t["spawn"] = Fn("task.spawn", ctx => WrapTaskThread(
                TrackScheduledThread(context,
                    _scheduler.Spawn(
                        RequireTaskOwner(context),
                        ReadTaskCallable(ctx, 0, "task.spawn"),
                        ReadTaskArguments(ctx, 1))),
                threadMeta, Arg(ctx, 0)));
            t["defer"] = Fn("task.defer", ctx => WrapTaskThread(
                TrackScheduledThread(context,
                    _scheduler.Defer(
                        RequireTaskOwner(context),
                        ReadTaskCallable(ctx, 0, "task.defer"),
                        ReadTaskArguments(ctx, 1))),
                threadMeta, Arg(ctx, 0)));
            t["delay"] = Fn("task.delay", ctx => WrapTaskThread(
                TrackScheduledThread(context,
                    _scheduler.Delay(
                        RequireTaskOwner(context),
                        ReadDouble(ctx, 0, "task.delay"),
                        ReadTaskCallable(ctx, 1, "task.delay"),
                        ReadTaskArguments(ctx, 2))),
                threadMeta, Arg(ctx, 1)));
            t["cancel"] = Fn("task.cancel", ctx =>
            {
                IRbxScriptThread thread = ReadTaskThread(ctx, 0);
                _scheduler.Cancel(thread);
                return LuaValue.Nil;
            });

            // WHY: DEV-5 — Parallel Luau context switches are no-ops with a once-per-mod note, so
            // parallel-annotated corpus scripts keep running instead of failing.
            LuaFunction parallelNoOp = Fn("task.synchronize", _ =>
            {
                if (!context.HasLoggedParallelNoOp)
                {
                    context.HasLoggedParallelNoOp = true;
                    _log?.Invoke(
                        "[RbxApi] task.synchronize/desynchronize are no-ops: CoreAI mods run " +
                        "single-threaded (DEV-5). (Logged once per mod.)");
                }

                return LuaValue.Nil;
            });
            t["synchronize"] = parallelNoOp;
            t["desynchronize"] = parallelNoOp;
            return new LuaValue(t);
        }

        private LuaValue LegacySpawn(LuaCsRbxModContext context,
            LuaFunctionExecutionContext ctx)
        {
            string ownerModId = RequireTaskOwner(context);
            LogLegacySchedulerDeprecation(ownerModId);
            LuaValue callback = ReadLegacyCallback(ctx, 0, "spawn");
            double scheduledAt = _scheduler.CurrentTime;
            LuaFunction timedCallback = new("spawn.callback", async (callbackContext, ct) =>
            {
                LuaValue[] callbackArguments =
                {
                    LuaCsValueMarshaller.Unbox(_scheduler.CurrentTime - scheduledAt),
                    LuaCsValueMarshaller.Unbox(UnityEngine.Time.realtimeSinceStartupAsDouble)
                };
                LuaValue[] results = await callbackContext.State.CallAsync(
                    callback, callbackArguments.AsSpan(), ct);
                return callbackContext.Return(results);
            });
            object callable = _schedulerThreadFactory.CaptureCallable(
                ctx.State, new LuaValue(timedCallback));
            TrackScheduledThread(context, _scheduler.Delay(
                ownerModId, LegacySchedulerMinimumDelaySeconds,
                callable, Array.Empty<object>()));
            return LuaValue.Nil;
        }

        private LuaValue LegacyDelay(LuaCsRbxModContext context,
            LuaFunctionExecutionContext ctx)
        {
            string ownerModId = RequireTaskOwner(context);
            LogLegacySchedulerDeprecation(ownerModId);
            double duration = Math.Max(
                ReadDouble(ctx, 0, "delay"), LegacySchedulerMinimumDelaySeconds);
            LuaValue callback = ReadLegacyCallback(ctx, 1, "delay");
            object callable = _schedulerThreadFactory.CaptureCallable(ctx.State, callback);
            TrackScheduledThread(context, _scheduler.Delay(
                ownerModId, duration, callable, Array.Empty<object>()));
            return LuaValue.Nil;
        }

        private LuaValue ScheduleWait(LuaCsRbxModContext context,
            LuaFunctionExecutionContext ctx, string functionName,
            double minimumDuration, bool legacy)
        {
            try
            {
                string ownerModId = RequireTaskOwner(context);
                if (legacy)
                {
                    LogLegacySchedulerDeprecation(ownerModId);
                }

                IRbxScriptThread caller = _schedulerThreadFactory.CurrentThread;
                if (caller == null)
                {
                    throw RbxError.NotImplemented(
                        functionName + " inside a directly invoked signal/runtime callback",
                        "MVP2 scheduler-owned signal callbacks rung",
                        "start yielding callback work with task.spawn, task.defer, task.delay, " +
                        "spawn, or delay until signal callbacks are scheduler-owned");
                }

                if (!(caller is LuaCsRbxScriptThread luaThread)
                    || !string.Equals(luaThread.OwnerModId, ownerModId,
                        StringComparison.Ordinal))
                {
                    throw RbxError.BadArgument(
                        functionName + " caller is not owned by mod " + ownerModId,
                        "wait only from a live scheduler thread owned by the current mod");
                }

                LuaValue durationValue = Arg(ctx, 0);
                double duration = durationValue.Type == LuaValueType.Nil
                    ? minimumDuration
                    : Math.Max(ReadDouble(ctx, 0, functionName), minimumDuration);
                if (!CanSuspendCaller(ctx, luaThread, functionName))
                {
                    return LuaValue.Nil;
                }

                _scheduler.ScheduleWait(caller, duration);
                luaThread.NoteScheduledYield();
                return LuaValue.Nil;
            }
            catch (Exception ex)
            {
                throw ToLuaError(ctx.State, ex);
            }
        }

        /// <summary>
        /// Decides whether a yielding binding may schedule a wait for <paramref name="caller"/> from the
        /// Lua code running right now. False means that code cannot yield at all (it runs inside a
        /// sandbox callback boundary such as string.format's <c>__tostring</c>): nothing is scheduled, and
        /// the bridge's own yield then raises the boundary error, so the thread's record is never left
        /// waiting for a suspension that did not happen (M2-19). Code running in a nested
        /// <c>coroutine.create</c> coroutine is refused with CONTEXT_VIOLATION: the wait would suspend
        /// that coroutine while the scheduler resumed the enclosing task thread instead (M2-06).
        /// </summary>
        private bool CanSuspendCaller(LuaFunctionExecutionContext ctx, LuaCsRbxScriptThread caller,
            string functionName)
        {
            LuaState running = ctx.State;
            if (running == null || !running.IsCoroutine
                || running.GetStatus() != LuaThreadStatus.Running)
            {
                return false;
            }

            if (!caller.IsOwnCoroutineRunning)
            {
                throw new RbxError(
                    RbxErrorCode.ContextViolation,
                    functionName + " cannot suspend a coroutine.create thread; only a task thread (the "
                    + "mod's chunk, a task.spawn/task.defer/task.delay function or a signal handler) "
                    + "can wait on the scheduler",
                    "run the waiting code with task.spawn(function() ... end) instead of "
                    + "coroutine.create/coroutine.resume");
            }

            // WHY: a wait state here means the thread's previous scheduled yield never took effect (its
            // yield was refused after the wait was scheduled), yet the thread is demonstrably running.
            // Rolled back, the next wait schedules normally instead of failing forever (M2-19).
            if (_scheduler.RollbackUnfinishedYield(caller))
            {
                ReleasePendingWait(caller);
            }

            return true;
        }

        /// <summary>Disconnects the wait connection a thread no longer waits on and forgets its RemoteFunction wait.</summary>
        private void ReleasePendingWait(LuaCsRbxScriptThread thread)
        {
            RbxScriptConnection connection = thread.PendingWaitConnection;
            thread.PendingWaitConnection = null;
            connection?.Disconnect();
            if (_remoteFunctionWaitGenerations.Count > 0)
            {
                _remoteFunctionWaitGenerations.Remove(thread);
            }
        }

        /// <summary>
        /// Drops everything these bindings keep for a thread the scheduler has stopped tracking (M2-07,
        /// M2-18): its tracked-thread ledger entry, the connection of the signal or RemoteFunction wait it
        /// was suspended on, and an unanswered RemoteFunction request, which is failed so its caller
        /// hears about it now instead of at its timeout.
        /// </summary>
        private void OnSchedulerThreadRetired(IRbxScriptThread thread)
        {
            if (!(thread is LuaCsRbxScriptThread luaThread))
            {
                _remoteFunctionWaitGenerations.Remove(thread);
                return;
            }

            HashSet<IRbxScriptThread> trackingSet = luaThread.TrackingSet;
            luaThread.TrackingSet = null;
            trackingSet?.Remove(luaThread);
            ReleasePendingWait(luaThread);
            RbxNetworkRequestResponder responder = luaThread.PendingResponder;
            luaThread.PendingResponder = null;
            if (responder == null || responder.IsCompleted)
            {
                return;
            }

            try
            {
                responder.Fail("RemoteFunction callback of mod '" + luaThread.OwnerModId
                               + "' stopped before it returned");
            }
            catch (Exception ex)
            {
                // WHY contained: this runs inside the scheduler's kill and fault paths; a transport that
                // throws while answering must not abort the teardown of every other thread.
                _log?.Invoke("[RbxApi] Failing an unanswered RemoteFunction request threw: " + ex.Message);
            }
        }

        /// <summary>
        /// Validates WaitForChild's arguments before the bridge touches them (M1-32): the child name
        /// must be a string and the optional timeout a number other than NaN. Returns both, the timeout
        /// as nil when omitted.
        /// </summary>
        private static LuaValue[] ReadWaitForChildArguments(LuaFunctionExecutionContext ctx)
        {
            if (!TryGetInstance(Arg(ctx, 0), out LuaCsRbxInstanceProxy _))
            {
                throw RbxError.BadArgument(
                    "WaitForChild expects an Instance as self",
                    "call it with a colon, e.g. workspace:WaitForChild('Name')");
            }

            string childName = ReadString(ctx, 1, "WaitForChild", 1);
            LuaValue timeoutValue = Arg(ctx, 2);
            if (timeoutValue.Type == LuaValueType.Nil)
            {
                return new LuaValue[] { childName, LuaValue.Nil };
            }

            double timeout = ReadDouble(ctx, 2, "WaitForChild", 2);
            if (double.IsNaN(timeout))
            {
                throw RbxError.BadArgument(
                    "WaitForChild timeout must be a number, not NaN",
                    "pass the timeout in seconds, or omit it to wait until the child appears");
            }

            return new LuaValue[] { childName, timeout };
        }

        private LuaValue ReadWaitResumeValue(LuaCsRbxModContext context,
            LuaFunctionExecutionContext ctx)
        {
            try
            {
                string ownerModId = RequireTaskOwner(context);
                if (!(_schedulerThreadFactory.CurrentThread is LuaCsRbxScriptThread caller)
                    || !string.Equals(caller.OwnerModId, ownerModId,
                        StringComparison.Ordinal)
                    || !caller.IsOwnCoroutineRunning)
                {
                    throw RbxError.BadArgument(
                        "task.wait resumed outside its owning scheduler thread",
                        "resume waiting threads through ModScheduler.Advance");
                }

                object elapsed = caller.ReadCurrentResumeArgument(0);
                if (elapsed == null)
                {
                    throw RbxError.BadArgument(
                        "task.wait resumed without an elapsed-time value",
                        "resume waiting threads through ModScheduler.Advance");
                }

                return LuaCsValueMarshaller.Unbox(elapsed);
            }
            catch (Exception ex)
            {
                throw ToLuaError(ctx.State, ex);
            }
        }

        private LuaValue ScheduleSignalWait(LuaCsRbxModContext context,
            LuaFunctionExecutionContext ctx)
        {
            try
            {
                string ownerModId = RequireTaskOwner(context);
                if (!TryUnbox(Arg(ctx, 0), out RbxScriptSignal signal))
                {
                    throw RbxError.BadArgument(
                        "signal:Wait expects an RBXScriptSignal as self",
                        "call signal methods with a colon, e.g. part.ChildAdded:Wait()");
                }

                IRbxScriptThread caller = _schedulerThreadFactory.CurrentThread;
                if (!(caller is LuaCsRbxScriptThread luaThread)
                    || !string.Equals(luaThread.OwnerModId, ownerModId,
                        StringComparison.Ordinal))
                {
                    throw RbxError.BadArgument(
                        "signal:Wait caller is not owned by mod " + ownerModId,
                        "wait only from a live scheduler thread owned by the current mod");
                }

                LuaValue timeoutValue = Arg(ctx, 1);
                double timeout = timeoutValue.Type == LuaValueType.Nil
                    ? double.NaN
                    : ReadDouble(ctx, 1, "signal timed wait");
                if (!CanSuspendCaller(ctx, luaThread, "signal:Wait"))
                {
                    return LuaValue.Nil;
                }

                signal.BindScheduler(_scheduler);
                double scheduledAt = _scheduler.CurrentTime;
                RbxScriptConnection connection = null;
                if (timeoutValue.Type == LuaValueType.Nil)
                {
                    _scheduler.ScheduleSignalWait(caller);
                }
                else
                {
                    _scheduler.ScheduleSignalWait(caller, timeout, () =>
                    {
                        connection?.Disconnect();
                        LuaTable timeoutValues = BuildSignalResumeValues(
                            context, Array.Empty<object>(), true,
                            _scheduler.CurrentTime - scheduledAt);
                        return new object[] { new LuaValue(timeoutValues) };
                    });
                }

                connection = signal.Wait(arguments =>
                {
                    LuaTable values = BuildSignalResumeValues(
                        context, arguments, false, _scheduler.CurrentTime - scheduledAt);
                    _scheduler.ResumeSignalWait(caller,
                        new object[] { new LuaValue(values) });
                });
                context.TrackConnection(connection);
                luaThread.PendingWaitConnection = connection;
                luaThread.NoteScheduledYield();
                return LuaValue.Nil;
            }
            catch (Exception ex)
            {
                throw ToLuaError(ctx.State, ex);
            }
        }

        private static LuaTable BuildSignalResumeValues(LuaCsRbxModContext context,
            object[] arguments, bool timedOut, double elapsed)
        {
            LuaTable values = new();
            for (int index = 0; index < arguments.Length; index++)
            {
                values[index + 1] = LuaCsRbxDatatypeBindings.MarshalSignalArg(
                    context, arguments[index]);
            }

            values["n"] = arguments.Length;
            values["timedOut"] = timedOut;
            values["elapsed"] = elapsed;
            return values;
        }

        private LuaValue ReadSignalResumeValues(LuaCsRbxModContext context,
            LuaFunctionExecutionContext ctx)
        {
            try
            {
                string ownerModId = RequireTaskOwner(context);
                if (!(_schedulerThreadFactory.CurrentThread is LuaCsRbxScriptThread caller)
                    || !string.Equals(caller.OwnerModId, ownerModId,
                        StringComparison.Ordinal)
                    || !caller.IsOwnCoroutineRunning)
                {
                    throw RbxError.BadArgument(
                        "signal:Wait resumed outside its owning scheduler thread",
                        "resume signal waiters through the deferred signal drain");
                }

                caller.PendingWaitConnection = null;
                object values = caller.ReadCurrentResumeArgument(0);
                if (values == null)
                {
                    throw RbxError.BadArgument(
                        "signal:Wait resumed without fire arguments",
                        "resume signal waiters through the deferred signal drain");
                }

                return LuaCsValueMarshaller.Unbox(values);
            }
            catch (Exception ex)
            {
                throw ToLuaError(ctx.State, ex);
            }
        }

        private LuaValue ScheduleRemoteFunctionInvoke(LuaCsRbxModContext context,
            LuaFunctionExecutionContext ctx, bool invokeServer)
        {
            try
            {
                string ownerModId = RequireTaskOwner(context);
                context.RequireNetworkSide(
                    invokeServer
                        ? "RemoteFunction:InvokeServer"
                        : "RemoteFunction:InvokeClient",
                    !invokeServer);
                if (!TryGetInstance(Arg(ctx, 0), out LuaCsRbxInstanceProxy remoteProxy)
                    || !(remoteProxy.Instance is RbxRemoteFunction remote))
                {
                    throw RbxError.BadArgument(
                        "RemoteFunction invoke expects a RemoteFunction as self",
                        "call InvokeServer or InvokeClient with a colon");
                }

                IRbxScriptThread caller = _schedulerThreadFactory.CurrentThread;
                if (!(caller is LuaCsRbxScriptThread luaThread)
                    || !string.Equals(luaThread.OwnerModId, ownerModId,
                        StringComparison.Ordinal))
                {
                    throw RbxError.BadArgument(
                        "RemoteFunction invoke caller is not owned by mod " + ownerModId,
                        "invoke only from a live scheduler thread owned by the current mod");
                }

                // WHY before anything is sent: a call that cannot suspend its caller must not reach the
                // other side, or the remote handler would run for an answer nobody ever reads.
                if (!CanSuspendCaller(ctx, luaThread,
                        invokeServer ? "RemoteFunction:InvokeServer" : "RemoteFunction:InvokeClient"))
                {
                    return LuaValue.Nil;
                }

                long generation = luaThread.AdvanceRemoteFunctionWaitGeneration();
                _remoteFunctionWaitGenerations[caller] = generation;
                string actorId = context.ActorContext.ActorId;
                string remoteFullName = remote.GetFullName();
                string respondingClientActorId = null;

                RbxScriptSignal responseSignal = new(
                    "RemoteFunction.Response[" + remote.Id.Value + "]");
                responseSignal.BindScheduler(_scheduler);
                RbxScriptConnection responseConnection = responseSignal.Wait(arguments =>
                {
                    if (!_remoteFunctionWaitGenerations.TryGetValue(
                            caller, out long activeGeneration)
                        || activeGeneration != generation)
                    {
                        return;
                    }

                    _remoteFunctionWaitGenerations.Remove(caller);

                    RbxNetworkResponse response = arguments != null && arguments.Length > 0
                        ? arguments[0] as RbxNetworkResponse
                        : null;
                    LuaTable values = BuildRemoteFunctionResumeValues(
                        context, response, respondingClientActorId);
                    _scheduler.ResumeSignalWait(caller,
                        new object[] { new LuaValue(values) });
                });
                context.TrackConnection(responseConnection);

                Action<RbxNetworkResponse> receiveResponse = response =>
                    responseSignal.Fire(response);
                try
                {
                    if (invokeServer)
                    {
                        byte[] payload = _networkCodec.EncodeArguments(
                            ReadRemoteArguments(ctx, 1));
                        remote.InvokeServer(_networkBridge,
                            context.ActorContext.ActorId, payload, receiveResponse);
                    }
                    else
                    {
                        if (!TryGetInstance(Arg(ctx, 1), out LuaCsRbxInstanceProxy playerProxy)
                            || !(playerProxy.Instance is RbxPlayer player))
                        {
                            throw RbxError.BadArgument(
                                "RemoteFunction:InvokeClient expects a Player at argument 1",
                                "pass a Player returned by Players:GetPlayers()");
                        }

                        byte[] payload = _networkCodec.EncodeArguments(
                            ReadRemoteArguments(ctx, 2));
                        // WHY never null here: whatever the Player, its answer is authored by a
                        // client, and a null id would select the trusted decode for it.
                        respondingClientActorId = player.NetworkActorId ?? "";
                        remote.InvokeClient(_networkBridge, player, payload, receiveResponse);
                    }

                    _scheduler.ScheduleSignalWait(
                        caller,
                        RemoteFunctionInvokeTimeoutSeconds,
                        () =>
                        {
                            if (_remoteFunctionWaitGenerations.TryGetValue(
                                    caller, out long activeGeneration)
                                && activeGeneration == generation)
                            {
                                _remoteFunctionWaitGenerations.Remove(caller);
                            }

                            responseConnection.Disconnect();
                            RbxNetworkResponse timeoutResponse = RbxNetworkResponse.Failure(
                                "RemoteFunction invoke refused actor '" + actorId
                                + "' for remote '" + remoteFullName
                                + "': response timed out after 30 seconds");
                            LuaTable timeoutValues = BuildRemoteFunctionResumeValues(
                                context, timeoutResponse, null);
                            return new object[] { new LuaValue(timeoutValues) };
                        });
                    luaThread.PendingWaitConnection = responseConnection;
                    luaThread.NoteScheduledYield();
                }
                catch
                {
                    if (_remoteFunctionWaitGenerations.TryGetValue(
                            caller, out long activeGeneration)
                        && activeGeneration == generation)
                    {
                        _remoteFunctionWaitGenerations.Remove(caller);
                    }

                    responseConnection.Disconnect();
                    throw;
                }

                return LuaValue.Nil;
            }
            catch (Exception ex)
            {
                throw ToLuaError(ctx.State, ex);
            }
        }

        /// <summary>
        /// Turns a RemoteFunction answer into the waiting caller's resume values.
        /// <paramref name="respondingClientActorId"/> names the client that authored the answer (an
        /// InvokeClient response), whose Instance references then resolve only as far as that client
        /// can see; null means the answer came from the trusted server (an InvokeServer response).
        /// </summary>
        private LuaTable BuildRemoteFunctionResumeValues(LuaCsRbxModContext context,
            RbxNetworkResponse response, string respondingClientActorId)
        {
            LuaTable values = new();
            if (response == null || !response.Succeeded)
            {
                values["ok"] = false;
                values["error"] = response?.Error ?? "RemoteFunction returned no response";
                values["n"] = 0;
                return values;
            }

            try
            {
                object[] decoded = respondingClientActorId != null
                    ? _networkCodec.DecodeClientArguments(response.Payload, respondingClientActorId)
                    : _networkCodec.DecodeArguments(response.Payload);
                for (int index = 0; index < decoded.Length; index++)
                {
                    values[index + 1] = _networkCodec.ToLuaValue(context, decoded[index]);
                }

                values["ok"] = true;
                values["n"] = decoded.Length;
                return values;
            }
            catch (Exception ex)
            {
                values["ok"] = false;
                values["error"] = ex.Message;
                values["n"] = 0;
                return values;
            }
        }

        private LuaValue ReadRemoteFunctionResumeValues(LuaCsRbxModContext context,
            LuaFunctionExecutionContext ctx)
        {
            try
            {
                string ownerModId = RequireTaskOwner(context);
                if (!(_schedulerThreadFactory.CurrentThread is LuaCsRbxScriptThread caller)
                    || !string.Equals(caller.OwnerModId, ownerModId,
                        StringComparison.Ordinal)
                    || !caller.IsOwnCoroutineRunning)
                {
                    throw RbxError.BadArgument(
                        "RemoteFunction resumed outside its owning scheduler thread",
                        "resume remote invocations through the deferred network response signal");
                }

                caller.PendingWaitConnection = null;
                object values = caller.ReadCurrentResumeArgument(0);
                if (values == null)
                {
                    throw RbxError.BadArgument(
                        "RemoteFunction resumed without response values",
                        "resume remote invocations through the production network bridge");
                }

                return LuaCsValueMarshaller.Unbox(values);
            }
            catch (Exception ex)
            {
                throw ToLuaError(ctx.State, ex);
            }
        }

        private LuaValue WarnInfiniteYield(LuaCsRbxModContext context,
            LuaFunctionExecutionContext ctx)
        {
            try
            {
                RequireTaskOwner(context);
                if (!TryGetInstance(Arg(ctx, 0), out LuaCsRbxInstanceProxy proxy))
                {
                    throw RbxError.BadArgument(
                        "WaitForChild infinite-yield warning expects an Instance",
                        "invoke WaitForChild through an Instance method");
                }

                string childName = ReadString(ctx, 1, "WaitForChild");
                _log?.Invoke(
                    "Infinite yield possible on '" + proxy.Instance.GetFullName()
                    + ":WaitForChild(\"" + childName + "\")'");
                return LuaValue.Nil;
            }
            catch (Exception ex)
            {
                throw ToLuaError(ctx.State, ex);
            }
        }

        private void LogLegacySchedulerDeprecation(string ownerModId)
        {
            bool firstUse;
            lock (_legacySchedulerDeprecationOwners)
            {
                firstUse = _legacySchedulerDeprecationOwners.Add(ownerModId);
            }

            if (firstUse)
            {
                _log?.Invoke(
                    "[RbxApi] wait/spawn/delay are deprecated; use task.wait/task.spawn/task.delay " +
                    "instead. (Logged once per mod.)");
            }
        }

        private string RequireTaskOwner(LuaCsRbxModContext context)
        {
            if (string.IsNullOrWhiteSpace(context.OwnerModId))
            {
                throw new RbxError(
                    RbxErrorCode.ContextViolation,
                    "task scheduling requires a persistent owning mod id",
                    "run task.* from a loaded mod instead of the ownerless one-off executor");
            }

            return context.OwnerModId;
        }

        /// <summary>
        /// Kills every scheduler thread owned by a mod on unload or quarantine, and destroys the tweens
        /// its scripts created (playing ones stop where they are; nothing fires into the departing mod).
        /// </summary>
        public int KillAllScheduledOwnedBy(string ownerModId)
        {
            int killed = _scheduler.KillOwnedBy(ownerModId);
            _tweenService?.CancelAndReleaseOwnedBy(ownerModId);
            RemoveRemoteFunctionWaitsOwnedBy(ownerModId, null);
            RemoveRemoteFunctionCallbacksOwnedBy(ownerModId, null);
            _scheduledThreadsByMod.Remove(ownerModId);
            _currentSchedulerGenerationByMod.Remove(ownerModId);
            _actorContextsByOwnerModId.Remove(ownerModId);
            // WHY released here: a world that loads and unloads mods for hours would otherwise keep
            // two strings for every mod id it ever ran; a reload rebuilds them on its first resume.
            _resumeOperationByMod.Remove(ownerModId);
            _originTagByMod.Remove(ownerModId);
            return killed;
        }

        /// <summary>Kills only scheduler threads from generations preceding a reload replacement.</summary>
        public int KillOutgoingScheduledGenerations(string ownerModId)
        {
            if (string.IsNullOrWhiteSpace(ownerModId))
            {
                return 0;
            }

            int liveGeneration = _currentSchedulerGenerationByMod.TryGetValue(
                ownerModId, out int current)
                ? current
                : int.MinValue;
            if (!_scheduledThreadsByMod.TryGetValue(
                    ownerModId,
                    out Dictionary<int, HashSet<IRbxScriptThread>> generations))
            {
                RemoveRemoteFunctionWaitsOwnedBy(ownerModId, null);
                RemoveRemoteFunctionCallbacksOwnedBy(ownerModId, liveGeneration);
                return 0;
            }

            HashSet<IRbxScriptThread> liveThreads = generations.TryGetValue(
                liveGeneration, out HashSet<IRbxScriptThread> currentThreads)
                ? currentThreads
                : null;
            List<int> removed = new();
            int killed = 0;
            foreach (KeyValuePair<int, HashSet<IRbxScriptThread>> generation in generations)
            {
                if (generation.Key == liveGeneration)
                {
                    PruneDeadThreads(generation.Value);
                    continue;
                }

                // WHY a snapshot: each cancel retires its thread, and retirement removes the thread from
                // this very set (M2-07).
                List<IRbxScriptThread> outgoing = new(generation.Value);
                foreach (IRbxScriptThread thread in outgoing)
                {
                    if (thread == null || thread.IsDead
                        || thread.Status == RbxScriptThreadStatus.Dead)
                    {
                        continue;
                    }

                    _scheduler.Cancel(thread);
                    killed++;
                }

                removed.Add(generation.Key);
            }

            for (int index = 0; index < removed.Count; index++)
            {
                generations.Remove(removed[index]);
            }

            if (generations.Count == 0)
            {
                _scheduledThreadsByMod.Remove(ownerModId);
            }

            RemoveRemoteFunctionWaitsOwnedBy(ownerModId, liveThreads);
            RemoveRemoteFunctionCallbacksOwnedBy(ownerModId, liveGeneration);

            return killed;
        }

        private int CancelScheduledGeneration(string ownerModId, int generation)
        {
            if (!_scheduledThreadsByMod.TryGetValue(
                    ownerModId,
                    out Dictionary<int, HashSet<IRbxScriptThread>> generations)
                || !generations.TryGetValue(
                    generation, out HashSet<IRbxScriptThread> threads))
            {
                return 0;
            }

            int killed = 0;
            List<IRbxScriptThread> cancelled = new(threads);
            foreach (IRbxScriptThread thread in cancelled)
            {
                if (thread == null || thread.IsDead
                    || thread.Status == RbxScriptThreadStatus.Dead)
                {
                    continue;
                }

                _scheduler.Cancel(thread);
                killed++;
            }

            generations.Remove(generation);
            if (generations.Count == 0)
            {
                _scheduledThreadsByMod.Remove(ownerModId);
            }

            RemoveRemoteFunctionWaits(cancelled);
            RemoveRemoteFunctionCallbacksOwnedBy(ownerModId, generation, true);

            return killed;
        }

        private void RemoveRemoteFunctionWaitsOwnedBy(string ownerModId,
            HashSet<IRbxScriptThread> waitsToKeep)
        {
            List<IRbxScriptThread> removed = new();
            foreach (IRbxScriptThread thread in _remoteFunctionWaitGenerations.Keys)
            {
                if (thread is LuaCsRbxScriptThread luaThread
                    && string.Equals(luaThread.OwnerModId, ownerModId,
                        StringComparison.Ordinal)
                    && (waitsToKeep == null || !waitsToKeep.Contains(thread)
                        || thread.IsDead || thread.Status == RbxScriptThreadStatus.Dead))
                {
                    removed.Add(thread);
                }
            }

            RemoveRemoteFunctionWaits(removed);
        }

        private void RemoveRemoteFunctionWaits(IEnumerable<IRbxScriptThread> threads)
        {
            foreach (IRbxScriptThread thread in threads)
            {
                _remoteFunctionWaitGenerations.Remove(thread);
            }
        }

        private void RemoveRemoteFunctionCallbacksOwnedBy(string ownerModId,
            int? generation)
        {
            RemoveRemoteFunctionCallbacksOwnedBy(ownerModId, generation, false);
        }

        private void RemoveRemoteFunctionCallbacksOwnedBy(string ownerModId,
            int? generation, bool removeMatchingGeneration)
        {
            List<InstanceId> removedServerIds = new();
            foreach (KeyValuePair<InstanceId, RemoteFunctionCallbackRegistration> pair
                     in _serverRemoteCallbacks)
            {
                if (ShouldRemoveRemoteFunctionCallback(
                        pair.Value, ownerModId, generation, removeMatchingGeneration))
                {
                    removedServerIds.Add(pair.Key);
                }
            }

            for (int index = 0; index < removedServerIds.Count; index++)
            {
                _serverRemoteCallbacks.Remove(removedServerIds[index]);
            }

            List<InstanceId> emptyRemoteIds = new();
            foreach (KeyValuePair<InstanceId,
                         Dictionary<string, RemoteFunctionCallbackRegistration>> remotePair
                     in _clientRemoteCallbacks)
            {
                List<string> removedActors = new();
                foreach (KeyValuePair<string, RemoteFunctionCallbackRegistration> actorPair
                         in remotePair.Value)
                {
                    if (ShouldRemoveRemoteFunctionCallback(
                            actorPair.Value, ownerModId, generation,
                            removeMatchingGeneration))
                    {
                        removedActors.Add(actorPair.Key);
                    }
                }

                for (int index = 0; index < removedActors.Count; index++)
                {
                    remotePair.Value.Remove(removedActors[index]);
                }

                if (remotePair.Value.Count == 0)
                {
                    emptyRemoteIds.Add(remotePair.Key);
                }
            }

            for (int index = 0; index < emptyRemoteIds.Count; index++)
            {
                _clientRemoteCallbacks.Remove(emptyRemoteIds[index]);
            }
        }

        private static bool ShouldRemoveRemoteFunctionCallback(
            RemoteFunctionCallbackRegistration registration, string ownerModId,
            int? generation, bool removeMatchingGeneration)
        {
            if (!string.Equals(registration.Context.OwnerModId,
                    ownerModId, StringComparison.Ordinal))
            {
                return false;
            }

            if (!generation.HasValue)
            {
                return true;
            }

            bool matches = registration.Context.ConnectionGeneration == generation.Value;
            return removeMatchingGeneration ? matches : !matches;
        }

        private IRbxScriptThread TrackScheduledThread(
            LuaCsRbxModContext context, IRbxScriptThread thread)
        {
            string ownerModId = RequireTaskOwner(context);
            return TrackScheduledThread(
                ownerModId, context.ConnectionGeneration, thread);
        }

        private IRbxScriptThread TrackScheduledThread(
            string ownerModId, int generation, IRbxScriptThread thread)
        {
            if (!_scheduledThreadsByMod.TryGetValue(
                    ownerModId,
                    out Dictionary<int, HashSet<IRbxScriptThread>> generations))
            {
                generations = new Dictionary<int, HashSet<IRbxScriptThread>>();
                _scheduledThreadsByMod[ownerModId] = generations;
            }

            if (!generations.TryGetValue(
                    generation, out HashSet<IRbxScriptThread> threads))
            {
                threads = new HashSet<IRbxScriptThread>();
                generations[generation] = threads;
            }

            if (thread != null && !thread.IsDead
                && thread.Status != RbxScriptThreadStatus.Dead)
            {
                threads.Add(thread);
                if (thread is LuaCsRbxScriptThread luaThread)
                {
                    // WHY the set is remembered on the thread: the scheduler retires the thread later
                    // through ThreadRetired, and the entry is dropped from exactly this set then instead
                    // of staying until the mod unloads (M2-07).
                    if (luaThread.TrackingSet != null
                        && !ReferenceEquals(luaThread.TrackingSet, threads))
                    {
                        luaThread.TrackingSet.Remove(luaThread);
                    }

                    luaThread.TrackingSet = threads;
                }
            }

            return thread;
        }

        /// <summary>Threads held by the tracked-thread ledger across every mod and generation (M2-07).</summary>
        internal int TrackedScheduledThreadCount
        {
            get
            {
                int count = 0;
                foreach (Dictionary<int, HashSet<IRbxScriptThread>> generations
                         in _scheduledThreadsByMod.Values)
                {
                    foreach (HashSet<IRbxScriptThread> threads in generations.Values)
                    {
                        count += threads.Count;
                    }
                }

                return count;
            }
        }

        private static void PruneDeadThreads(HashSet<IRbxScriptThread> threads)
        {
            threads.RemoveWhere(thread => thread == null || thread.IsDead
                                          || thread.Status == RbxScriptThreadStatus.Dead);
        }

        private object ReadTaskCallable(LuaFunctionExecutionContext ctx, int index, string what)
        {
            LuaValue callable = Arg(ctx, index);
            if (callable.Type == LuaValueType.Thread)
            {
                // WHY named apart from a wrong type: the value IS a thread, just not one the scheduler
                // owns. R4.10 keeps coroutine.create threads and coroutine.running() values outside it.
                throw RbxError.BadArgument(
                    what + " cannot schedule a coroutine.create thread or a coroutine.running() value "
                    + "at argument " + (index + 1),
                    "pass a function, or a thread handle returned by task.spawn, task.defer or task.delay");
            }

            try
            {
                return _schedulerThreadFactory.CaptureCallable(ctx.State, callable,
                    resumableByHandle: true);
            }
            catch (RbxError error)
            {
                throw RbxError.BadArgument(
                    what + " expects a function or thread at argument " + (index + 1),
                    error.Fix);
            }
        }

        private static LuaValue ReadLegacyCallback(
            LuaFunctionExecutionContext ctx, int index, string what)
        {
            LuaValue callback = Arg(ctx, index);
            if (callback.Type == LuaValueType.Function)
            {
                return callback;
            }

            throw RbxError.BadArgument(
                what + " expects a function at argument " + (index + 1),
                "pass a function, got " + Describe(callback) + " at argument " + (index + 1));
        }

        private static object[] ReadTaskArguments(LuaFunctionExecutionContext ctx, int startIndex)
        {
            int count = Math.Max(0, ctx.ArgumentCount - startIndex);
            if (count == 0)
            {
                return Array.Empty<object>();
            }

            object[] arguments = new object[count];
            for (int index = 0; index < count; index++)
            {
                arguments[index] = ctx.GetArgument(startIndex + index);
            }

            return arguments;
        }

        /// <summary>
        /// Boxes a scheduled thread for Lua. A handle passed back in (M2-14) is returned as the very value
        /// the script passed, so <c>task.spawn(t) == t</c> holds the way it does for Roblox threads.
        /// </summary>
        private static LuaValue WrapTaskThread(IRbxScriptThread thread, LuaTable threadMeta,
            LuaValue passed)
        {
            if (TryUnbox(passed, out IRbxScriptThread passedThread)
                && ReferenceEquals(passedThread, thread))
            {
                return passed;
            }

            return Box(thread, threadMeta);
        }

        private static IRbxScriptThread ReadTaskThread(
            LuaFunctionExecutionContext ctx, int index)
        {
            LuaValue value = Arg(ctx, index);
            if (TryUnbox(value, out IRbxScriptThread thread))
            {
                return thread;
            }

            throw RbxError.BadArgument(
                "task.cancel expects a thread at argument " + (index + 1),
                "pass the live thread returned by task.spawn, task.defer, or task.delay");
        }
    }
}
