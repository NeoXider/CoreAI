using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
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
        private readonly IClickPickSource _pickSource;
        private readonly ModConnectionRegistry _connections;
        private readonly LuaCsRbxScriptThreadFactory _schedulerThreadFactory;
        private readonly ModScheduler _scheduler;
        private readonly Dictionary<string, string> _resumeOperationByMod = new();
        private readonly IRbxClockSource _clockSource;
        private readonly object _serverTimeGate = new();
        private double _lastServerTimeNow = double.NegativeInfinity;
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
        private readonly Dictionary<string, Dictionary<int, HashSet<IRbxScriptThread>>>
            _scheduledThreadsByMod = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _currentSchedulerGenerationByMod =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, ExecutingScriptBacking> _executingScriptsByMod =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, ActorContext> _actorContextsByOwnerModId =
            new(StringComparer.Ordinal);
        private readonly Action<string> _log;
        private CoreAI.Ai.IInGameLlmChatServiceFactory _chatFactory;
        private int _consoleInvocationCounter;
        private float _runServiceElapsed;
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
        /// </summary>
        public LuaCsRbxApiBindings(InstanceRegistry registry = null, RbxDataModel game = null,
            RbxEnumRegistry enums = null, Action<string> log = null, IPartPropertySink partSink = null,
            IRbxCameraRig cameraRig = null, IInputSource inputSource = null,
            ModConnectionRegistry connections = null, IClickPickSource pickSource = null,
            IRbxRuntimeObservabilitySink observability = null,
            INetworkBridge networkBridge = null, Func<DateTimeOffset> utcNowProvider = null,
            IRbxClockSource clockSource = null)
        {
            _registry = registry ?? new InstanceRegistry();
            _connections = connections ?? new ModConnectionRegistry();
            IRbxRuntimeObservabilitySink resolvedObservability =
                observability != null && observability.IsEnabled
                ? observability
                : null;
            _schedulerThreadFactory = new LuaCsRbxScriptThreadFactory(
                observability: resolvedObservability,
                resumeEnvelope: ResumeSchedulerThread);
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

            _networkCodec = new LuaCsRbxNetworkCodec(_registry, _enums, _log);
            _partSink = partSink ?? new InMemoryPartPropertySink();
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
            _tweenPropertyHost = new LuaCsTweenPropertyHost(_partSink, _registry);
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
                    _scheduler, _tweenPropertyHost, ResolvePlaybackStateItem);
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

            if (_userInputService != null)
            {
                _userInputService.InputBegan.BindScheduler(_scheduler);
                _userInputService.InputEnded.BindScheduler(_scheduler);
                _userInputService.InputChanged.BindScheduler(_scheduler);
            }

            if (_runService != null)
            {
                _runService.Heartbeat.BindScheduler(_scheduler);
                _runService.Stepped.BindScheduler(_scheduler);
                _runService.RenderStepped.BindScheduler(_scheduler);
            }

            _players.PlayerAdded.BindScheduler(_scheduler);
            _players.PlayerRemoving.BindScheduler(_scheduler);
            _networkRequestSignal = new RbxScriptSignal("NetworkBridge.RequestReceived");
            _networkRequestSignal.BindScheduler(_scheduler);
            _networkRequestConnection = _networkRequestSignal.Connect(
                (Action<object[]>)DeliverNetworkRequest);
            _networkBridge.EventReceived += DeliverNetworkEvent;
            _networkBridge.RequestReceived += QueueNetworkRequest;
            _registry.Unregistered += OnInstanceUnregistered;

            _scheduler.PhaseReached += PumpSchedulerPhase;

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
                AttachCharacterMotor(humanoid);
            }
        }

        private void AttachCharacterMotor(RbxHumanoid humanoid)
        {
            IRbxCharacterMotor motor = _characterMotorFactory?.Invoke(humanoid);
            humanoid.AttachHost(_scheduler, motor, humanoid.Parent);
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

        /// <summary>Injectable source behind every Lua-visible clock. Null at composition
        /// means the production system-clock default; a game passes its own source to redefine
        /// time.</summary>
        public IRbxClockSource ClockSource => _clockSource;

        /// <summary>Server-synced epoch seconds behind <c>workspace:GetServerTimeNow()</c>,
        /// monotonic-smoothed on top of <see cref="ClockSource"/> so it never steps back.</summary>
        internal double GetServerTimeNow()
        {
            // WHY the bridge's offset is added: on a client the local clock is its own machine's,
            // and a player whose system time is an hour off would otherwise disagree with the server
            // about when everything happened. The offset is zero on a server and on the loopback,
            // so solo behaviour is byte-identical to before.
            double now = _clockSource.UnixTimeSecondsFractional
                         + (_networkBridge?.ServerClockOffsetSeconds ?? 0d);
            lock (_serverTimeGate)
            {
                // WHY: clamp, don't throw — callers expect a clock that keeps ticking through
                // NTP/system-clock corrections, never one that errors or rewinds.
                if (now < _lastServerTimeNow)
                {
                    return _lastServerTimeNow;
                }

                _lastServerTimeNow = now;
                return now;
            }
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
            os["time"] = Fn("os.time", _ => (double)_clockSource.UnixTimeSeconds);
            os["clock"] = Fn("os.clock", _ => _clockSource.ProcessTimeSeconds);
            return new LuaValue(os);
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

            return true;
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
            if (_debris != null)
            {
                _debris.DetachHost();
            }
            if (_collectionService != null)
            {
                _collectionService.DetachHost();
            }
            _scheduler.PhaseReached -= PumpSchedulerPhase;
            _networkRequestConnection.Disconnect();

            List<string> owners = new(_scheduledThreadsByMod.Keys);
            for (int index = 0; index < owners.Count; index++)
            {
                string ownerModId = owners[index];
                KillAllScheduledOwnedBy(ownerModId);
                _connections.DisconnectOwnedBy(ownerModId);
            }

            _serverRemoteCallbacks.Clear();
            _clientRemoteCallbacks.Clear();
            _remoteFunctionWaitGenerations.Clear();
            _scheduledThreadsByMod.Clear();
            _currentSchedulerGenerationByMod.Clear();
            _actorContextsByOwnerModId.Clear();
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
                this, ownerModId, OriginTag.FromMod(ownerModId));
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
            TrackScheduledThread(context, _scheduler.SpawnSignal(
                ownerModId, callable, arguments));
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

        private void DeliverNetworkEvent(RbxNetworkEventMessage message)
        {
            try
            {
                if (message == null)
                {
                    _log?.Invoke("Network event message was null.");
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
                    _log?.Invoke("Network event targeted an unknown RemoteEvent.");
                    return;
                }

                if (remote.Reliability != message.Reliability)
                {
                    _log?.Invoke(
                        "Network event reliability does not match " + remote.GetFullName() + ".");
                    return;
                }

                object[] arguments = _networkCodec.DecodeArguments(message.Payload);
                remote.AttachScheduler(_scheduler);
                switch (message.Direction)
                {
                    case RbxNetworkDirection.ClientToServer:
                        RbxPlayer player = EnsureNetworkActor(admittedSender);
                        remote.DeliverToServer(player, arguments);
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
                        _log?.Invoke("Network event has an unknown delivery direction.");
                        return;
                }
            }
            catch (RbxError)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log?.Invoke("Network event delivery failed: " + ex.Message);
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

                object[] decoded = _networkCodec.DecodeArguments(message.Payload);
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

                SpawnRemoteFunctionCallback(registration, callbackArguments, responder);
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

        private void SpawnRemoteFunctionCallback(
            RemoteFunctionCallbackRegistration registration, object[] arguments,
            RbxNetworkRequestResponder responder)
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
                TrackScheduledThread(registration.Context, _scheduler.SpawnSignal(
                    RequireTaskOwner(registration.Context), callable, arguments));
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
            _registry.ProcessPreSimulation();
            if (_runService == null || _runService.IsDestroyed)
            {
                return;
            }

            _runServiceElapsed += dt;
            if (_runService.PreSimulation.HasConnections)
            {
                _runService.PreSimulation.Fire(dt);
            }

            if (_runService.Stepped.HasConnections)
            {
                _runService.Stepped.Fire(_runServiceElapsed, dt);
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

        private void PumpSchedulerPhase(SchedulerPhase phase, double deltaSeconds)
        {
            if (phase == SchedulerPhase.InputProcessing)
            {
                PumpInput();
            }
        }

        /// <summary>
        /// Per-frame click pick: on the RISING edge of MouseButton1 (one fire per click), casts a
        /// camera ray through the mouse position, resolves the nearest world instance, and fires the
        /// MouseClick of a ClickDetector CHILD of that part when the hit is within its
        /// MaxActivationDistance. Only the single nearest ray hit fires, so clicking one part never
        /// fires another part's detector, and clicking empty space fires nothing. Every step is
        /// null-guarded, so the headless default (no camera/physics) is a silent no-op.
        /// </summary>
        // TODO: MVP2 — MouseHoverEnter/MouseHoverLeave once the pick pump tracks the hovered part
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
            if (!_pickSource.TryPick(location, out InstanceId hitId, out double distanceStuds)
                || !_registry.TryGet(hitId, out RbxInstance hit)
                || hit.IsDestroyed)
            {
                return;
            }

            RbxClickDetector detector = FindClickDetector(hit);
            if (detector == null || detector.IsDestroyed)
            {
                return;
            }

            // WHY: gate on MaxActivationDistance (studs from the camera) exactly like Roblox — a click
            // farther than the detector's range does not activate it — and skip the fire when nothing
            // listens so an unlistened detector boxes nothing.
            if (distanceStuds <= detector.MaxActivationDistance && detector.MouseClick.HasConnections)
            {
                detector.MouseClick.Fire();
            }
        }

        // WHY: Roblox parents a ClickDetector UNDER the clickable part, so the hit part's direct
        // children are searched for the first ClickDetector; a part with no detector is inert.
        private static RbxClickDetector FindClickDetector(RbxInstance part)
        {
            foreach (RbxInstance child in part.GetChildren())
            {
                if (child is RbxClickDetector detector)
                {
                    return detector;
                }
            }

            return null;
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
            // WHY: registered on every tier so a WorldEdit-less call fails with the actionable
            // capability message instead of "attempt to call a nil value".
            luaRegistry.RegisterValue("camera_set_cframe",
                () => new LuaValue(BuildCameraSetCFrame(context)));
            luaRegistry.RegisterValue("camera_follow",
                () => new LuaValue(BuildCameraFollow(context)));

            if (context.CanWorldEdit)
            {
                luaRegistry.RegisterValue("Instance", () => BuildInstanceGlobal(context));
            }
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
                _cameraRig.SetCFrame(cframe);
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
                if (target.Type == LuaValueType.Nil)
                {
                    SetCameraSubject(null);
                    context.RecordMutation(camera);
                    return LuaValue.Nil;
                }

                if (!TryGetInstance(target, out LuaCsRbxInstanceProxy proxy))
                {
                    throw RbxError.BadArgument(
                        "camera_follow expects an Instance or nil at argument 1",
                        "pass a world instance to follow (or nil to stop), got "
                        + Describe(target) + " at argument 1");
                }

                SetCameraSubject(proxy.Instance);
                context.RecordMutation(camera);
                return LuaValue.Nil;
            });
        }

        // ---- Instance.new -------------------------------------------------------------------

        private LuaValue BuildInstanceGlobal(LuaCsRbxModContext context)
        {
            LuaTable t = new();
            t["new"] = Fn("Instance.new", ctx =>
            {
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
            t["fromExisting"] = Fn("Instance.fromExisting", _ => throw RbxError.NotImplemented(
                "Instance.fromExisting", "no planned MVP (backlog)", "use instance:Clone() instead"),
                context);
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
            t["_realtime"] = Fn("task._realtime", _ =>
                LuaCsValueMarshaller.Unbox(UnityEngine.Time.realtimeSinceStartupAsDouble));
            t["spawn"] = Fn("task.spawn", ctx => WrapTaskThread(
                TrackScheduledThread(context,
                    _scheduler.Spawn(
                        RequireTaskOwner(context),
                        ReadTaskCallable(ctx, 0, "task.spawn"),
                        ReadTaskArguments(ctx, 1))),
                threadMeta));
            t["defer"] = Fn("task.defer", ctx => WrapTaskThread(
                TrackScheduledThread(context,
                    _scheduler.Defer(
                        RequireTaskOwner(context),
                        ReadTaskCallable(ctx, 0, "task.defer"),
                        ReadTaskArguments(ctx, 1))),
                threadMeta));
            t["delay"] = Fn("task.delay", ctx => WrapTaskThread(
                TrackScheduledThread(context,
                    _scheduler.Delay(
                        RequireTaskOwner(context),
                        ReadDouble(ctx, 0, "task.delay"),
                        ReadTaskCallable(ctx, 1, "task.delay"),
                        ReadTaskArguments(ctx, 2))),
                threadMeta));
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
                _scheduler.ScheduleWait(caller, duration);
                return LuaValue.Nil;
            }
            catch (Exception ex)
            {
                throw ToLuaError(ctx.State, ex);
            }
        }

        private LuaValue ReadWaitResumeValue(LuaCsRbxModContext context,
            LuaFunctionExecutionContext ctx)
        {
            try
            {
                string ownerModId = RequireTaskOwner(context);
                if (!(_schedulerThreadFactory.CurrentThread is LuaCsRbxScriptThread caller)
                    || !string.Equals(caller.OwnerModId, ownerModId,
                        StringComparison.Ordinal))
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

                signal.BindScheduler(_scheduler);
                double scheduledAt = _scheduler.CurrentTime;
                RbxScriptConnection connection = null;
                LuaValue timeoutValue = Arg(ctx, 1);
                if (timeoutValue.Type == LuaValueType.Nil)
                {
                    _scheduler.ScheduleSignalWait(caller);
                }
                else
                {
                    double timeout = ReadDouble(ctx, 1, "signal timed wait");
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
                        StringComparison.Ordinal))
                {
                    throw RbxError.BadArgument(
                        "signal:Wait resumed outside its owning scheduler thread",
                        "resume signal waiters through the deferred signal drain");
                }

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

                long generation = luaThread.AdvanceRemoteFunctionWaitGeneration();
                _remoteFunctionWaitGenerations[caller] = generation;
                string actorId = context.ActorContext.ActorId;
                string remoteFullName = remote.GetFullName();

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
                    LuaTable values = BuildRemoteFunctionResumeValues(context, response);
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
                                context, timeoutResponse);
                            return new object[] { new LuaValue(timeoutValues) };
                        });
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

        private LuaTable BuildRemoteFunctionResumeValues(LuaCsRbxModContext context,
            RbxNetworkResponse response)
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
                object[] decoded = _networkCodec.DecodeArguments(response.Payload);
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
                        StringComparison.Ordinal))
                {
                    throw RbxError.BadArgument(
                        "RemoteFunction resumed outside its owning scheduler thread",
                        "resume remote invocations through the deferred network response signal");
                }

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

        /// <summary>Kills every scheduler thread owned by a mod on unload or quarantine.</summary>
        public int KillAllScheduledOwnedBy(string ownerModId)
        {
            int killed = _scheduler.KillOwnedBy(ownerModId);
            RemoveRemoteFunctionWaitsOwnedBy(ownerModId, null);
            RemoveRemoteFunctionCallbacksOwnedBy(ownerModId, null);
            _scheduledThreadsByMod.Remove(ownerModId);
            _currentSchedulerGenerationByMod.Remove(ownerModId);
            _actorContextsByOwnerModId.Remove(ownerModId);
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

                foreach (IRbxScriptThread thread in generation.Value)
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
            foreach (IRbxScriptThread thread in threads)
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

            RemoveRemoteFunctionWaits(threads);
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
            }

            return thread;
        }

        private static void PruneDeadThreads(HashSet<IRbxScriptThread> threads)
        {
            threads.RemoveWhere(thread => thread == null || thread.IsDead
                                          || thread.Status == RbxScriptThreadStatus.Dead);
        }

        private object ReadTaskCallable(LuaFunctionExecutionContext ctx, int index, string what)
        {
            LuaValue callable = Arg(ctx, index);
            try
            {
                return _schedulerThreadFactory.CaptureCallable(ctx.State, callable);
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

        private static LuaValue WrapTaskThread(IRbxScriptThread thread, LuaTable threadMeta)
        {
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
