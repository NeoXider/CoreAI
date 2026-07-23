using System;
using System.Threading;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Sandbox.LuaCs;
using CoreAI.Scripting;
using Lua;
using static CoreAI.Ai.LuaCs.LuaCsRobloxLua;

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
    public sealed class LuaCsRobloxApiBindings
    {
        private readonly InstanceRegistry _registry;
        private readonly RbxDataModel _game;
        private readonly RbxInstance _workspace;
        private readonly RbxEnumRegistry _enums;
        private readonly IPartPropertySink _partSink;
        private readonly IRobloxCameraRig _cameraRig;
        private readonly RbxUserInputService _userInputService;
        private readonly Action<string> _log;
        private int _consoleInvocationCounter;

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
        public LuaCsRobloxApiBindings(InstanceRegistry registry = null, RbxDataModel game = null,
            RbxEnumRegistry enums = null, Action<string> log = null, IPartPropertySink partSink = null,
            IRobloxCameraRig cameraRig = null, IInputSource inputSource = null)
        {
            _registry = registry ?? new InstanceRegistry();
            _game = game ?? DataModelBootstrap.CreateGame(_registry);
            _workspace = _game.FindFirstChildOfClass("Workspace")
                         ?? throw new ArgumentException(
                             "the game tree has no Workspace child", nameof(game));
            _enums = enums ?? RbxEnumRegistry.CreateWithBuiltins();
            _partSink = partSink ?? new InMemoryPartPropertySink();
            _cameraRig = cameraRig ?? new InMemoryCameraRig();
            _log = log;
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
        public IRobloxCameraRig CameraRig => _cameraRig;

        /// <summary>The shared UserInputService instance (input signals + poll surface).</summary>
        public RbxUserInputService UserInputService => _userInputService;

        /// <summary>
        /// Per-frame input pump: polls the input source, diffs, and fires
        /// InputBegan/InputEnded/InputChanged. The host composition calls this once per frame
        /// BEFORE the mod runtime tick so handlers observe this frame's events.
        /// </summary>
        // TODO: MVP2 — the general signal scheduler replaces this pump.
        public void PumpInput()
        {
            _userInputService?.Step();
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
                    "LuaCsRobloxApiBindings requires the Lua-CSharp registry adapter; a second " +
                    "engine ships its own Roblox binding adapter next to it.", nameof(registry));
            }

            string originTag = ownerModId != null
                ? OriginTag.FromMod(ownerModId)
                // WHY: no invocation identity reaches this layer yet (execute_lua invocation ids
                // arrive with the tooling slice), so console-origin instances are grouped per
                // registered console surface — still selectively cleanable by prefix.
                : OriginTag.FromConsole(
                    "session-" + Interlocked.Increment(ref _consoleInvocationCounter));

            LuaCsRobloxModContext context =
                new LuaCsRobloxModContext(this, capabilities, ownerModId, originTag);

            luaRegistry.RegisterValue("Vector3", LuaCsRobloxDatatypeBindings.BuildVector3Global);
            luaRegistry.RegisterValue("Vector2", LuaCsRobloxDatatypeBindings.BuildVector2Global);
            luaRegistry.RegisterValue("CFrame", LuaCsRobloxDatatypeBindings.BuildCFrameGlobal);
            luaRegistry.RegisterValue("Color3", LuaCsRobloxDatatypeBindings.BuildColor3Global);
            luaRegistry.RegisterValue("UDim", LuaCsRobloxDatatypeBindings.BuildUDimGlobal);
            luaRegistry.RegisterValue("UDim2", LuaCsRobloxDatatypeBindings.BuildUDim2Global);
            luaRegistry.RegisterValue("Random", LuaCsRobloxDatatypeBindings.BuildRandomGlobal);
            luaRegistry.RegisterValue("Enum", () => LuaCsRobloxDatatypeBindings.BuildEnumGlobal(_enums));
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

        private LuaFunction BuildCameraSetCFrame(LuaCsRobloxModContext context)
        {
            return Fn("camera_set_cframe", ctx =>
            {
                context.RequireWorldEdit("camera_set_cframe");
                RbxCFrame cframe = ReadCFrame(ctx, 0, "camera_set_cframe");
                _cameraRig.SetCFrame(cframe);
                return LuaValue.Nil;
            });
        }

        private LuaFunction BuildCameraFollow(LuaCsRobloxModContext context)
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

                if (!TryGetInstance(target, out LuaCsRobloxInstanceProxy proxy))
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

        private LuaValue BuildInstanceGlobal(LuaCsRobloxModContext context)
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
                            "[RobloxApi] Instance.new(\"" + className + "\", parent) — the parent " +
                            "argument is deprecated by Roblox; set instance.Parent after " +
                            "configuring the instance instead. (Logged once per mod.)");
                    }

                    if (!TryGetInstance(parentValue, out LuaCsRobloxInstanceProxy parent))
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

        // ---- task.* (MVP2 scheduler stubs) --------------------------------------------------

        private LuaValue BuildTaskGlobal(LuaCsRobloxModContext context)
        {
            LuaTable t = new();
            // TODO: MVP2 — ModScheduler + TaskLibrary replace these stubs.
            t["wait"] = SchedulerStub("task.wait");
            t["spawn"] = SchedulerStub("task.spawn");
            t["defer"] = SchedulerStub("task.defer");
            t["delay"] = SchedulerStub("task.delay");
            t["cancel"] = SchedulerStub("task.cancel");

            // WHY: DEV-5 — Parallel Luau context switches are no-ops with a once-per-mod note, so
            // parallel-annotated corpus scripts keep running instead of failing.
            LuaFunction parallelNoOp = Fn("task.synchronize", _ =>
            {
                if (!context.HasLoggedParallelNoOp)
                {
                    context.HasLoggedParallelNoOp = true;
                    _log?.Invoke(
                        "[RobloxApi] task.synchronize/desynchronize are no-ops: CoreAI mods run " +
                        "single-threaded (DEV-5). (Logged once per mod.)");
                }

                return LuaValue.Nil;
            });
            t["synchronize"] = parallelNoOp;
            t["desynchronize"] = parallelNoOp;
            return new LuaValue(t);
        }

        private static LuaFunction SchedulerStub(string name)
        {
            return Fn(name, _ => throw RbxError.NotImplemented(
                name, "MVP2",
                "the task scheduler lands in MVP2; use hooks_every for periodic work until then"));
        }
    }
}
