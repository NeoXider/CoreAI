using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
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
    public sealed class LuaCsRbxApiBindings
    {
        private const double LegacySchedulerMinimumDelaySeconds = 0.029d;

        internal sealed class ModLoadCandidate
        {
            public ModLoadCandidate(string ownerModId, bool hadPreviousGeneration,
                int previousGeneration, HashSet<RbxScriptConnection> existingConnections)
            {
                OwnerModId = ownerModId;
                HadPreviousGeneration = hadPreviousGeneration;
                PreviousGeneration = previousGeneration;
                ExistingConnections = existingConnections;
            }

            public string OwnerModId { get; }

            public bool HadPreviousGeneration { get; }

            public int PreviousGeneration { get; }

            public HashSet<RbxScriptConnection> ExistingConnections { get; }
        }

        private readonly InstanceRegistry _registry;
        private readonly RbxDataModel _game;
        private readonly RbxInstance _workspace;
        private readonly RbxEnumRegistry _enums;
        private readonly IPartPropertySink _partSink;
        private readonly IRbxCameraRig _cameraRig;
        private readonly RbxUserInputService _userInputService;
        private readonly RbxRunService _runService;
        private readonly IClickPickSource _pickSource;
        private readonly ModConnectionRegistry _connections;
        private readonly LuaCsRbxScriptThreadFactory _schedulerThreadFactory;
        private readonly ModScheduler _scheduler;
        private readonly HashSet<string> _legacySchedulerDeprecationOwners =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<int, HashSet<IRbxScriptThread>>>
            _scheduledThreadsByMod = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _currentSchedulerGenerationByMod =
            new(StringComparer.Ordinal);
        private readonly Action<string> _log;
        private int _consoleInvocationCounter;
        private float _runServiceElapsed;

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
        /// </summary>
        public LuaCsRbxApiBindings(InstanceRegistry registry = null, RbxDataModel game = null,
            RbxEnumRegistry enums = null, Action<string> log = null, IPartPropertySink partSink = null,
            IRbxCameraRig cameraRig = null, IInputSource inputSource = null,
            ModConnectionRegistry connections = null, IClickPickSource pickSource = null,
            IRbxRuntimeObservabilitySink observability = null)
        {
            _registry = registry ?? new InstanceRegistry();
            _connections = connections ?? new ModConnectionRegistry();
            IRbxRuntimeObservabilitySink resolvedObservability =
                observability != null && observability.IsEnabled
                ? observability
                : null;
            _schedulerThreadFactory = new LuaCsRbxScriptThreadFactory(
                observability: resolvedObservability);
            _scheduler = new ModScheduler(
                _schedulerThreadFactory, new RbxAccumulatingTimeSource());
            // WHY: no camera/physics behind the headless default, so clicks resolve to nothing until
            // a live UnityClickPickSource is wired at composition (mirrors the camera-rig default).
            _pickSource = pickSource ?? new InMemoryClickPickSource();
            _game = game ?? DataModelBootstrap.CreateGame(_registry);
            _workspace = _game.FindFirstChildOfClass("Workspace")
                         ?? throw new ArgumentException(
                             "the game tree has no Workspace child", nameof(game));
            _enums = enums ?? RbxEnumRegistry.CreateWithBuiltins();
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

        internal ModLoadCandidate BeginModLoadCandidate(string ownerModId)
        {
            string owner = string.IsNullOrWhiteSpace(ownerModId)
                ? throw new ArgumentException("Owner mod id is required.", nameof(ownerModId))
                : ownerModId;
            bool hadPreviousGeneration = _currentSchedulerGenerationByMod.TryGetValue(
                owner, out int previousGeneration);
            HashSet<RbxScriptConnection> existingConnections =
                new(_connections.GetOwnedBy(owner));
            return new ModLoadCandidate(owner, hadPreviousGeneration,
                previousGeneration, existingConnections);
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

        internal LuaState ResolveSchedulerOwnerState(LuaState fallbackState)
        {
            return _schedulerThreadFactory.ResolveOwnerState(fallbackState);
        }

        /// <summary>
        /// Per-frame input pump: polls the input source, diffs, and fires
        /// InputBegan/InputEnded/InputChanged after the Heartbeat signal.
        /// </summary>
        // TODO: MVP2 — the general signal scheduler replaces this pump.
        public void PumpInput()
        {
            _userInputService?.Step();
        }

        /// <summary>
        /// Fires legacy Stepped at the scheduler's PreSimulation boundary.
        /// </summary>
        // TODO: MVP2 — the general signal scheduler replaces this pump.
        public void PumpPreSimulation(float dt)
        {
            if (_runService == null || _runService.IsDestroyed)
            {
                return;
            }

            _runServiceElapsed += dt;
            if (_runService.Stepped.HasConnections)
            {
                _runService.Stepped.Fire(_runServiceElapsed, dt);
            }
        }

        /// <summary>Fires legacy Heartbeat, then polls input at the scheduler Heartbeat boundary.</summary>
        public void PumpHeartbeat(float dt)
        {
            if (_runService != null && !_runService.IsDestroyed
                && _runService.Heartbeat.HasConnections)
            {
                _runService.Heartbeat.Fire(dt);
            }

            PumpInput();
        }

        /// <summary>Fires legacy RenderStepped and completes click picking at PreRender.</summary>
        public void PumpPreRender(float dt)
        {
            if (_runService != null && !_runService.IsDestroyed
                && _runService.RenderStepped.HasConnections)
            {
                _runService.RenderStepped.Fire(dt);
            }

            PumpClicks();
        }

        /// <summary>Runs the split frame pumps in their observable scheduler order.</summary>
        public void PumpFrame(float dt)
        {
            PumpPreSimulation(dt);
            PumpHeartbeat(dt);
            PumpPreRender(dt);
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

            LuaCsRbxModContext context = new(this, capabilities, ownerModId, originTag);
            if (!string.IsNullOrWhiteSpace(ownerModId))
            {
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

        // ---- camera_* convenience globals ---------------------------------------------------

        private LuaFunction BuildCameraSetCFrame(LuaCsRbxModContext context)
        {
            return Fn("camera_set_cframe", ctx =>
            {
                context.RequireWorldEdit("camera_set_cframe");
                RbxCFrame cframe = ReadCFrame(ctx, 0, "camera_set_cframe");
                _cameraRig.SetCFrame(cframe);
                return LuaValue.Nil;
            });
        }

        private LuaFunction BuildCameraFollow(LuaCsRbxModContext context)
        {
            return Fn("camera_follow", ctx =>
            {
                context.RequireWorldEdit("camera_follow");
                LuaValue target = Arg(ctx, 0);
                if (target.Type == LuaValueType.Nil)
                {
                    SetCameraSubject(null);
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
                RbxInstance instance = _registry.CreateScripted(
                    className, context.OwnerModId, context.OriginTag);

                LuaValue parentValue = Arg(ctx, 1);
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
                        instance.Destroy();
                        throw RbxError.BadArgument(
                            "Instance.new expects an Instance at argument 2",
                            "pass an Instance parent, got " + Describe(parentValue) + " at argument 2");
                    }

                    instance.Parent = parent.Instance;
                }

                return context.WrapInstance(instance);
            });
            // TODO: backlog — Instance.fromExisting (not scheduled; Clone covers the corpus).
            t["fromExisting"] = Fn("Instance.fromExisting", _ => throw RbxError.NotImplemented(
                "Instance.fromExisting", "no planned MVP (backlog)", "use instance:Clone() instead"));
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
            _scheduledThreadsByMod.Remove(ownerModId);
            _currentSchedulerGenerationByMod.Remove(ownerModId);
            return killed;
        }

        /// <summary>Kills only scheduler threads from generations preceding a reload replacement.</summary>
        public int KillOutgoingScheduledGenerations(string ownerModId)
        {
            if (string.IsNullOrWhiteSpace(ownerModId)
                || !_scheduledThreadsByMod.TryGetValue(
                    ownerModId,
                    out Dictionary<int, HashSet<IRbxScriptThread>> generations))
            {
                return 0;
            }

            int liveGeneration = _currentSchedulerGenerationByMod.TryGetValue(
                ownerModId, out int current)
                ? current
                : int.MinValue;
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

            return killed;
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
