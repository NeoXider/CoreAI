using System;
using System.Collections.Generic;
using System.Threading;
using CoreAI.Ai.Logging;
using CoreAI.Authority;
using CoreAI.Logging;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Scheduling;
using CoreAI.Sandbox.LuaCs;
using CoreAI.Scripting;
using CoreAI.Scripting.LuaCs;
using Newtonsoft.Json;

namespace CoreAI.Ai
{
    /// <summary>
    /// What a reload does with the objects the replaced run of a mod built at startup. A run's startup
    /// objects are the instances registered while its main chunk ran, up to the chunk's first yield or
    /// its end, that the mod still owns; objects its handlers, timers, remote calls or players create
    /// later are never startup objects.
    /// </summary>
    public enum ModReloadMode
    {
        /// <summary>
        /// The default everywhere. The previous run's startup objects leave the world (Parent = nil)
        /// before the new main chunk runs, so it builds into a clean world, and are destroyed once it
        /// has run. A reload that fails puts them back exactly where they were. An object inside one of
        /// them that the same mod still owns as the same actor (a coin its Heartbeat handler dropped into
        /// its folder) goes with it; one owned by anyone else (a player's build in the mod's folder,
        /// another mod's object) is moved to that startup object's parent before the destroy, never
        /// destroyed.
        /// </summary>
        CleanStartupObjects = 0,

        /// <summary>
        /// Every object stays where it is and the new main chunk builds next to them (the hot reload
        /// every earlier version did). The kept startup objects stay tracked, so a later clean reload
        /// removes them too.
        /// </summary>
        KeepObjects = 1
    }

    /// <summary>What one successful reload did with the startup objects of the run it replaced.</summary>
    public sealed class ModReloadReport
    {
        public ModReloadReport(string modId, ModReloadMode mode, int cleanedObjects, int keptObjects,
            int rescuedObjects)
        {
            ModId = modId ?? "";
            Mode = mode;
            CleanedObjects = cleanedObjects;
            KeptObjects = keptObjects;
            RescuedObjects = rescuedObjects;
        }

        /// <summary>The reloaded mod.</summary>
        public string ModId { get; }

        /// <summary>The mode the reload ran in.</summary>
        public ModReloadMode Mode { get; }

        /// <summary>
        /// Startup objects of earlier runs this reload destroyed, with the objects the mod still owned
        /// inside them (0 in <see cref="ModReloadMode.KeepObjects"/>).
        /// </summary>
        public int CleanedObjects { get; }

        /// <summary>Startup objects of earlier runs this reload left in the world (<see cref="ModReloadMode.KeepObjects"/>).</summary>
        public int KeptObjects { get; }

        /// <summary>
        /// Objects owned by someone other than the mod (a player, another mod, the host) that sat inside a
        /// destroyed startup object, moved out to its parent before the destroy.
        /// </summary>
        public int RescuedObjects { get; }

        /// <summary>One English clause for a status line, e.g. "cleaned 126 objects of the previous run".</summary>
        public string Describe()
        {
            if (Mode == ModReloadMode.KeepObjects)
            {
                return KeptObjects == 0
                    ? "kept objects (the previous run had no startup objects left)"
                    : "kept " + CountObjects(KeptObjects) + " of the previous run";
            }

            string cleaned = CleanedObjects == 0
                ? "nothing to clean (the previous run had no startup objects left)"
                : "cleaned " + CountObjects(CleanedObjects) + " of the previous run";
            return RescuedObjects == 0
                ? cleaned
                : cleaned + " and moved " + CountObjects(RescuedObjects)
                          + " that are not the mod's own out of them first";
        }

        private static string CountObjects(int count)
        {
            return count == 1 ? "1 object" : count + " objects";
        }
    }
}

namespace CoreAI.Ai.LuaCs
{
    /// <summary>
    /// Why a mod instance's runtime side effects (logic-slot overrides, future instance registries /
    /// signals) are being torn down. Carried by <see cref="LuaCsModRuntime.ModTearingDown"/>.
    /// </summary>
    public enum LuaModTeardownReason
    {
        /// <summary>
        /// The mod is being removed from the runtime (<see cref="LuaCsModRuntime.UnloadMod"/>, or because
        /// the actor it ran as disconnected).
        /// </summary>
        Unload,

        /// <summary>The mod is being replaced by a new instance (<see cref="LuaCsModRuntime.ReloadMod"/>); fired before the swap.</summary>
        Reload,

        /// <summary>The mod hit its consecutive-error threshold and enters quarantine (kept loaded, dispatch suspended).</summary>
        Quarantine
    }

    /// <summary>
    /// Lua-CSharp (nuskey8/Lua-CSharp) persistent runtime for long-lived mods. This is the single
    /// Lua VM since the MoonSharp removal (5.4.0): the legacy MoonSharp <c>CoreAI.Ai.LuaModRuntime</c>
    /// is gone, and the public lifecycle/tick/diagnostics surface below mirrors its shape so the
    /// tick driver and consumers keep working unchanged.
    ///
    /// A mod is a sandboxed Lua-CSharp <see cref="IScriptState"/> that registers hooks during load and
    /// then lives across frames:
    /// <list type="bullet">
    /// <item><c>hooks_on(event, fn)</c> — handler for named events (from the game or other mods).</item>
    /// <item><c>hooks_every(seconds, fn)</c> — repeating timer driven by <see cref="Tick"/>.</item>
    /// <item><c>events_emit(name, payload)</c> — emits an event to the game (<see cref="ModEventEmitted"/>) and other mods.</item>
    /// <item><c>store_set(key, value)</c> / <c>store_get(key)</c> — persistent per-mod k/v (when an <see cref="ILuaModStore"/> is supplied).</item>
    /// <item><c>mod_id()</c> — the mod's own id.</item>
    /// <item><c>mods_export/mods_get/mods_call/mods_list_exports</c> — cross-mod, plain-data-copied surface.</item>
    /// </list>
    /// The host calls <see cref="Tick"/> once per frame; every handler/timer call runs under a
    /// per-call instruction/time guard (<see cref="LuaCsExecutionGuard"/>).
    ///
    /// ERROR POLICY — QUARANTINE, NOT UNLOAD: a mod failing <see cref="MaxErrorsBeforeQuarantine"/>
    /// times in a row (a failed hook/timer call counts once and a successful one resets the streak unless
    /// a scheduler fault was charged in the same frame; for scheduler threads a frame with any fault
    /// counts once and a frame whose threads ran cleanly resets it) is QUARANTINED at the end of a
    /// <see cref="Tick"/>: it stops dispatching
    /// (handlers, timers, and queued events are all skipped and its logic-slot overrides revert to
    /// vanilla) but it STAYS loaded and fully addressable — <c>manage_mods list/get_source/diagnostics</c>
    /// keep seeing it and <see cref="ReloadMod"/> works normally, clearing the quarantine and the error
    /// streak. This keeps the async repair loop honest: an LLM repair that takes seconds or minutes
    /// still finds the mod it was asked to fix instead of a "not loaded" error after an auto-unload.
    ///
    /// SCOPE NOTE (migration pass 1): the heavy world/unity gameplay bindings that the MoonSharp
    /// runtime injects via <c>IGameLuaRuntimeBindings.RegisterGameplayApis(LuaApiRegistry)</c> are
    /// NOT ported here. This runtime accepts an injection callback so ported gameplay APIs can be
    /// wired later; see <see cref="RegisterGameplayBindings"/> for the open seam.
    ///
    /// PERSISTENCE PARITY (migration pass 2): source-store persistence, version history, import/export,
    /// rehydrate and forget/revert are now ported from the MoonSharp runtime and behave identically
    /// (a successful <see cref="LoadMod"/>/<see cref="ReloadMod"/> saves source+manifest to the
    /// <see cref="ILuaModSourceStore"/> and records a revision in the <see cref="ILuaScriptVersionStore"/>;
    /// <see cref="UnloadMod"/> marks the package dormant; <see cref="ForgetMod"/> deletes it; a mod unloaded
    /// because the actor it ran as disconnected leaves its package as it was), so
    /// <c>manage_mods</c> can later run on this VM. Both stores default to no-op implementations, so a
    /// host that wires neither keeps the prior in-memory-only behaviour.
    /// </summary>
    public sealed class LuaCsModRuntime : ILuaModRuntime
    {
        // WHY: ~10 s matches Luau's script watchdog so a mod handler is not cut sooner than a Roblox one.
        public const int DefaultHandlerTimeoutMs = 10_000;
        public const long DefaultHandlerMaxSteps = 50_000_000;
        public const int DefaultMaxMods = 32;
        public const int BenchmarkMaxMods = 200;
        public const int EmergencyMaxMods = 256;
        public const int DefaultMaxRegisteredInstancesPerActor = 2048;
        public const int EmergencyMaxRegisteredInstances = 16384;

        /// <summary>
        /// Room kept under the WebGL world-package save budget for the nodes a save carries that the
        /// registered-instance ceiling never charges: the DataModel, its services and the Workspace
        /// Camera (17 in a bootstrapped world).
        /// </summary>
        public const int UnchargedWorldSkeletonAllowance = 64;

        /// <summary>
        /// Emergency registered-instance ceiling of a WebGL player: the WebGL world-package save budget
        /// (<see cref="CoreAI.Mods.WorldPackages.FileRbxWorldPackageStore.MaximumWebGlSafeInstances"/>)
        /// minus <see cref="UnchargedWorldSkeletonAllowance"/>.
        /// </summary>
        /// <remarks>
        /// WHY: the WebGL store refuses to write a package whose tree holds more instances than its
        /// budget, and the pre-mutation autosave gates every world-changing tool on that write. A
        /// script allowed to grow the world to the desktop ceiling would therefore lock every gated tool
        /// until something is deleted by hand, so on WebGL the growth stops where the save still fits.
        /// </remarks>
        public const int WebGlEmergencyMaxRegisteredInstances =
            CoreAI.Mods.WorldPackages.FileRbxWorldPackageStore.MaximumWebGlSafeInstances
            - UnchargedWorldSkeletonAllowance;

        /// <summary>
        /// Emergency registered-instance ceiling a runtime gets when its host passes none:
        /// <see cref="WebGlEmergencyMaxRegisteredInstances"/> in a WebGL player,
        /// <see cref="EmergencyMaxRegisteredInstances"/> everywhere else.
        /// </summary>
#if UNITY_WEBGL && !UNITY_EDITOR
        public const int DefaultEmergencyMaxRegisteredInstances = WebGlEmergencyMaxRegisteredInstances;
#else
        public const int DefaultEmergencyMaxRegisteredInstances = EmergencyMaxRegisteredInstances;
#endif

        /// <summary>
        /// Distinct ownerless scheduler faults (<see cref="ModScheduler.HostFaulted"/>) a runtime logs;
        /// a repeat of a logged fault is never logged again, and the first fault beyond this many distinct
        /// ones is announced by one final line instead of growing the memo without bound.
        /// </summary>
        public const int MaxDistinctHostFaultsLogged = 64;

        public const int DefaultMaxHandlersPerMod = 64;
        public const int DefaultMaxEventSubscriptionsPerActor =
            DefaultMaxMods * DefaultMaxHandlersPerMod;
        public const int EmergencyMaxEventSubscriptions =
            EmergencyMaxMods * DefaultMaxHandlersPerMod;
        public const int DefaultMaxTimersPerMod = 16;
        public const int DefaultMaxQueuedEventsPerMod = 256;
        public const int DefaultMaxEventsDispatchedPerTick = 64;

        /// <summary>
        /// Upper bound on event-handler and timer invocations dispatched across <em>all</em> mods in a
        /// single <see cref="Tick"/>. Chosen as 4x the per-mod event cap: comfortably above that cap so a
        /// single busy mod is never throttled below its own budget, while still bounding a worst-case
        /// burst across many mods to a few hundred calls per frame. Mods not reached once it is
        /// exhausted keep their queued events for later ticks (no events are dropped).
        /// </summary>
        public const int DefaultMaxEventsDispatchedPerTickGlobal = 256;

        /// <summary>
        /// Default consecutive-error streak (reset by any successful call) at which a mod is
        /// quarantined — suspended from dispatch but kept loaded so it can be inspected and repaired
        /// via <see cref="ReloadMod"/>. Overridable per runtime via the constructor /
        /// <c>LuaCsModStackOptions.MaxErrorsBeforeQuarantine</c>.
        /// </summary>
        public const int DefaultMaxErrorsBeforeQuarantine = 8;

        /// <summary>
        /// Default count of budget trips (the instruction, time or memory budget of one call or resume)
        /// in a row at which a mod is quarantined, however far its ordinary error streak is from
        /// <see cref="DefaultMaxErrorsBeforeQuarantine"/>, and its stored package is marked inactive and
        /// suspended so the next start does not run it again.
        /// </summary>
        /// <remarks>
        /// WHY 2 and apart from the error streak: every trip is a stall as long as the budget (10 s for a
        /// hook or timer by default), so eight of them froze the game for over a minute before the
        /// quarantine, long enough for the player to kill the process, and the next start ran the mod
        /// and froze again. One trip can be bad luck; two in a row are a loop.
        /// </remarks>
        public const int DefaultMaxBudgetTripsBeforeQuarantine = 2;

        /// <summary>Maximum values/functions one mod may publish via <c>mods_export</c>.</summary>
        public const int DefaultMaxExportsPerMod = 64;

        /// <summary>
        /// Maximum nested <c>mods_call</c> depth (A calls B calls C ...). Bounds accidental
        /// cross-mod recursion with a clear error instead of a Lua stack overflow.
        /// </summary>
        public const int MaxCrossCallDepth = 8;

        /// <summary>Maximum table nesting marshalled across mods by <c>mods_get</c>/<c>mods_call</c>.</summary>
        public const int CrossModTableDepth = 4;

        /// <summary>Fallback timer cadence used where a slot needs a default interval. NOTE: this is NOT a
        /// hard floor for <c>hooks_every</c> anymore — a timer fires at most once per <see cref="Tick"/>
        /// (once per frame), so a smaller/zero interval is a safe per-frame loop, not per-instruction spam.</summary>
        public const double MinTimerIntervalSeconds = 0.05;

        /// <summary>
        /// Prefix applied to a mod id when forming its <see cref="ILuaScriptVersionStore"/> key, so a mod's
        /// revision history shares the version store with one-shot <c>execute_lua</c> scripts without ever
        /// colliding with a game-defined script slot of the same name. Mirrors the MoonSharp runtime's key.
        /// </summary>
        public const string VersionKeyPrefix = "mod:";

        /// <summary>
        /// Upper bound on the number of recent Tick-time handler errors retained for the agent to
        /// inspect via <see cref="GetRecentHandlerErrors"/>. Oldest entries are dropped once the buffer
        /// is full so a perpetually broken mod cannot grow it without bound.
        /// </summary>
        public const int MaxRetainedHandlerErrors = 32;

        /// <summary>
        /// Upper bound on the number of recent <c>report()</c>/<c>print()</c> emissions retained for
        /// inspection via <see cref="GetRecentReports"/>, independent of each mod's <c>LogReports</c>
        /// mute flag. Oldest entries are dropped once the buffer is full so a chatty mod cannot grow it
        /// without bound.
        /// </summary>
        public const int MaxRetainedReports = 64;

        private sealed class TimerEntry
        {
            public double IntervalSeconds;
            public double DueIn;
            public object Fn;
        }

        /// <summary>
        /// The registry admission that charges each new record to its quota actor, or refuses the
        /// creation with the quota or ceiling text before the record is added or announced.
        /// </summary>
        private sealed class InstanceQuotaAdmission : IInstanceRegistrationAdmission
        {
            private readonly LuaCsModRuntime _runtime;

            public InstanceQuotaAdmission(LuaCsModRuntime runtime)
            {
                _runtime = runtime;
            }

            public string Admit(InstanceRecord record)
            {
                return _runtime.ChargeRegisteredInstance(record);
            }

            public void Revoke(InstanceRecord record)
            {
                _runtime.ReleaseRegisteredInstance(record);
            }
        }

        /// <summary>
        /// One startup object of a mod run: its id (never a reference, so a destroyed instance is not
        /// kept alive) and the actor that owned it when it was created.
        /// </summary>
        private readonly struct StartupObject
        {
            public StartupObject(InstanceId id, string ownerActorId)
            {
                Id = id;
                OwnerActorId = ownerActorId;
            }

            public InstanceId Id { get; }

            public string OwnerActorId { get; }
        }

        /// <summary>
        /// The startup objects one build of a mod collects while its main chunk runs. An instance the
        /// chunk destroys again before it returns leaves the set, so the set is bounded by what the
        /// mod's instance quota lets it hold alive at once.
        /// </summary>
        private sealed class StartupCapture
        {
            private readonly List<StartupObject> _objects = new();
            private readonly HashSet<InstanceId> _live = new();

            public StartupCapture(string modId, StartupCapture enclosing)
            {
                ModId = modId;
                Enclosing = enclosing;
            }

            public string ModId { get; }

            /// <summary>The capture of an outer build of the same mod id this one interrupted, if any.</summary>
            public StartupCapture Enclosing { get; }

            public void Add(InstanceRecord record)
            {
                if (_live.Add(record.Id))
                {
                    _objects.Add(new StartupObject(record.Id, record.OwnerActorId));
                }
            }

            public void Remove(InstanceId id)
            {
                if (!_live.Remove(id))
                {
                    return;
                }

                // WHY compacted here: a chunk that creates and destroys in a loop would otherwise grow
                // the ordered list without bound while the live set stays small.
                if (_objects.Count > 2 * _live.Count + 64)
                {
                    _objects.RemoveAll(entry => !_live.Contains(entry.Id));
                }
            }

            public List<StartupObject> Snapshot()
            {
                List<StartupObject> result = new(_live.Count);
                for (int index = 0; index < _objects.Count; index++)
                {
                    if (_live.Contains(_objects[index].Id))
                    {
                        result.Add(_objects[index]);
                    }
                }

                return result;
            }
        }

        /// <summary>
        /// A top-most startup object a clean reload took out of the world, with where it was: its parent,
        /// its position among that parent's children, and the rest of its ancestor chain for when the
        /// parent itself is gone by the time it is put back or its contents are rescued.
        /// </summary>
        private sealed class DetachedStartupRoot
        {
            public DetachedStartupRoot(RbxInstance instance, RbxInstance originalParent, int siblingIndex,
                List<RbxInstance> ancestors)
            {
                Instance = instance;
                OriginalParent = originalParent;
                SiblingIndex = siblingIndex;
                Ancestors = ancestors;
            }

            public RbxInstance Instance { get; }

            /// <summary>Null for a startup object that was never parented (a tween, an object held only in Lua).</summary>
            public RbxInstance OriginalParent { get; }

            public int SiblingIndex { get; }

            /// <summary>The original parent first, then its ancestors up to the root of the tree.</summary>
            public List<RbxInstance> Ancestors { get; }

            /// <summary>Whether the detach actually took it out (false when it was never parented or the detach failed).</summary>
            public bool Detached { get; set; }
        }

        /// <summary>The startup objects of the run a clean reload replaces, while the new main chunk runs.</summary>
        private sealed class StartupDetachment
        {
            public StartupDetachment(string modId, HashSet<InstanceId> members, List<StartupObject> objects,
                List<DetachedStartupRoot> roots)
            {
                ModId = modId;
                Members = members;
                Objects = objects;
                Roots = roots;
            }

            public string ModId { get; }

            /// <summary>Every live startup object the mod still owned when the reload began.</summary>
            public HashSet<InstanceId> Members { get; }

            /// <summary><see cref="Members"/> as tracked entries, in creation order.</summary>
            public List<StartupObject> Objects { get; }

            /// <summary>The members none of whose ancestors is a member.</summary>
            public List<DetachedStartupRoot> Roots { get; }
        }

        /// <summary>What destroying a set of startup objects did.</summary>
        private readonly struct StartupCleanup
        {
            public StartupCleanup(int destroyed, int rescued, List<StartupObject> survivors)
            {
                Destroyed = destroyed;
                Rescued = rescued;
                Survivors = survivors;
            }

            public int Destroyed { get; }

            public int Rescued { get; }

            /// <summary>Members still alive after the cleanup because a destroy failed; they stay tracked.</summary>
            public List<StartupObject> Survivors { get; }
        }

        private sealed class Mod
        {
            public readonly object EventGate = new();
            public string Id = "";
            public string OwnerActorId = "";
            public bool OwnerHasHostAuthority;
            public IScriptState State;
            public string Source = "";
            public LuaCapabilities Caps;
            public bool LogReports;
            public readonly Dictionary<string, List<object>> Handlers = new(StringComparer.Ordinal);
            public readonly List<TimerEntry> Timers = new();
            public readonly Queue<KeyValuePair<string, string>> Pending = new();
            public readonly HashSet<string> RegisteredEvents = new(StringComparer.Ordinal);
            public readonly Dictionary<string, object> Exports = new(StringComparer.Ordinal);
            public int HandlerCount;
            public int ErrorCount;
            public DateTime LoadedAtUtc;
            public long LoadOrder;
            public volatile bool AcceptsEvents;

            /// <summary>
            /// True once the mod hit the consecutive-error threshold: it stays loaded and addressable
            /// but is skipped by <see cref="Tick"/> (no handlers, timers, or queued events) until a
            /// <see cref="ReloadMod"/> replaces it with a fresh, un-quarantined instance.
            /// </summary>
            public bool Quarantined;

            /// <summary>
            /// Any failure was charged to this mod since the last <see cref="Tick"/> closed its frame, so
            /// a clean scheduler resume earlier in the same frame must not forgive it.
            /// </summary>
            public bool FaultedThisFrame;

            /// <summary>
            /// A scheduler-thread fault of this frame has already been charged; further scheduler faults
            /// of the same frame are reported but do not lengthen the streak again.
            /// </summary>
            public bool SchedulerFaultChargedThisFrame;

            /// <summary>A scheduler thread of this mod yielded or completed cleanly during this frame.</summary>
            public bool SchedulerSucceededThisFrame;

            /// <summary>
            /// Budget trips (instruction, time or memory) since this mod last ran a call or a frame
            /// cleanly; reset wherever <see cref="ErrorCount"/> is. Reaching
            /// <see cref="MaxBudgetTripsBeforeQuarantine"/> quarantines the mod and suspends its stored
            /// package.
            /// </summary>
            public int BudgetTripStreak;

            /// <summary>
            /// This run's startup objects: the instances registered while its main chunk ran, in creation
            /// order, plus the startup objects of earlier runs a <see cref="ModReloadMode.KeepObjects"/>
            /// reload kept. Runtime state only, never persisted: a restart or a world restore runs every
            /// main chunk again through the same build, which records the set afresh.
            /// </summary>
            public List<StartupObject> StartupObjects = new();

            /// <summary>
            /// True once an unload took this instance out of the registry. A <see cref="Tick"/> that
            /// snapshotted it earlier in the same frame skips it from then on instead of dispatching
            /// into a mod that is gone.
            /// </summary>
            public bool Removed;
        }

        private readonly object _gate = new();
        private readonly object _subscriptionGate = new();
        private readonly Dictionary<string, Mod> _mods = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<Mod>> _subscriptions = new(StringComparer.Ordinal);
        private Dictionary<string, Mod[]> _subscriptionSnapshot = new(StringComparer.Ordinal);
        private readonly List<Mod> _modsInLoadOrder = new();
        private readonly Dictionary<string, string> _quotaActorByOwnerModId =
            new(StringComparer.Ordinal);
        private readonly IScriptEngine _engine;
        private readonly IValueMarshaller _marshaller;
        private readonly IScriptExecutionGuard _handlerGuard;
        private readonly Action<IScriptFunctionRegistry, LuaCapabilities, string> _gameplayBindings;
        private readonly LuaCsLogicSlots _logicSlots;
        private readonly ILuaModStore _store;
        private readonly ILuaModSourceStore _sourceStore;
        private readonly ILuaScriptVersionStore _versionStore;
        private readonly ILuaTransactionScope _transactionScope;
        private readonly bool _autoPersistMods;
        private readonly ILog _log;
        private readonly ILuaLogService _logService;
        private readonly LuaCsRbxApiBindings _rbxApi;
        private readonly IRbxRuntimeObservabilitySink _observability;
        private readonly IExecutionBudget _scriptExecutionBudget;
        private readonly List<Mod> _tickScratch = new();
        private readonly object _instanceQuotaGate = new();
        private readonly Dictionary<InstanceId, string> _quotaActorByInstanceId = new();
        private readonly Dictionary<string, int> _registeredInstancesByActor =
            new(StringComparer.Ordinal);
        private readonly InstanceQuotaAdmission _instanceQuotaAdmission;

        private readonly Queue<LuaModHandlerError> _recentHandlerErrors = new();
        private readonly Queue<LuaModReport> _recentReports = new();
        private readonly Dictionary<string, int> _buildDepthByModId = new(StringComparer.Ordinal);
        private readonly object _startupGate = new();
        private readonly Dictionary<string, StartupCapture> _startupCaptureByModId =
            new(StringComparer.Ordinal);
        private Action<string, LuaModTeardownReason> _defaultTeardown;
        private readonly object _hostFaultGate = new();
        private readonly HashSet<string> _loggedHostFaults = new(StringComparer.Ordinal);
        private readonly Queue<KeyValuePair<string, string[]>> _pendingActorModReleases = new();
        private readonly Dictionary<string, string> _actorDisconnectedDuringBuild =
            new(StringComparer.Ordinal);
        private bool _releasingActorMods;
        private int _guardedCallDepth;
        private IScriptExecutionGuard _signalHandlerExportGuard;
        private int _signalHandlerExportTimeoutMs;
        private long _signalHandlerExportMaxSteps;

        private int _registeredInstanceCount;
        private bool _hostFaultOverflowLogged;
        private long _nextLoadOrder;
        private long _subscriptionEntriesTouched;

        // WHY: Reentrancy depth of mods_call on the current thread (ticks run on the main thread; a
        // second thread would only ever see its own chain).
        [ThreadStatic]
        private static int _crossCallDepth;

        private bool _shutdown;

        /// <summary>
        /// Raised when a mod calls <c>events_emit(name, payload)</c>: (modId, eventName, payload).
        /// The Unity layer bridges this to MessagePipe/game systems.
        /// </summary>
        internal event Action<string, string, string> ModEventEmitted;

        /// <summary>Raised after a mod source is successfully loaded or reloaded: (modId, source, caps).</summary>
        internal event Action<string, string, LuaCapabilities> ModSourceLoaded;

        /// <summary>Raised after a mod is unloaded via <see cref="UnloadMod"/>/<see cref="ForgetMod"/>, or because the actor it ran as disconnected: (modId, source, caps). Repeated errors never unload — see <see cref="ModQuarantined"/>.</summary>
        internal event Action<string, string, LuaCapabilities> ModSourceUnloaded;

        /// <summary>
        /// Raised when a mod hits <see cref="MaxErrorsBeforeQuarantine"/> consecutive errors and is
        /// quarantined: (modId, consecutiveErrorCount). The mod stays loaded but stops dispatching
        /// until it is reloaded; hosts drive their repair loop from this instead of an unload.
        /// Subscribers are isolated: a throwing subscriber never skips the rest.
        /// </summary>
        internal event Action<string, int> ModQuarantined;

        /// <summary>
        /// Raised whenever a mod instance's runtime side effects are being torn down — on
        /// <see cref="UnloadMod"/>, on <see cref="ReloadMod"/> (before the new instance is swapped in),
        /// and on quarantine entry: (modId, reason). Logic-slot overrides are already cleared by the
        /// runtime itself; future subsystems (instance registries, signals) subscribe here to release
        /// the mod's effects at the same point. Subscribers are isolated.
        /// </summary>
        internal event Action<string, LuaModTeardownReason> ModTearingDown;

        /// <summary>
        /// Raised when a loaded mod's hook/timer throws while running under <see cref="Tick"/>, or one of
        /// its scheduler threads faults: (modId, error, consecutiveErrorCount). Fired asynchronously on the
        /// host thread; the count resets to zero after a successful hook/timer call in a frame without a
        /// scheduler fault (for scheduler threads, after a frame that ran cleanly), so a host can debounce
        /// an auto-repair loop on the streak length.
        /// </summary>
        internal event Action<string, string, int> ModHandlerErrored;

        /// <summary>
        /// Raised when a loaded mod calls <c>report(message)</c> (or <c>print</c>) and report logging
        /// is enabled for that mod: (modId, message). Reports are muted by default so timer mods cannot
        /// flood logs.
        /// </summary>
        internal event Action<string, string> ModReportEmitted;

        /// <summary>
        /// True when the Lua-CSharp sandbox is available on this platform. Lua-CSharp is a managed,
        /// AOT-safe VM (the reason for this migration), so unlike the MoonSharp runtime this is always
        /// supported — including IL2CPP/WebGL.
        /// </summary>
        public static bool IsSupported => true;

        /// <summary>Host-configured per-actor mod capacity, independently bounded by <see cref="EmergencyMaxMods"/>.</summary>
        public int MaxMods { get; }

        /// <summary>Host-configured per-actor live scheduler-thread capacity.</summary>
        public int MaxSchedulerThreadsPerActor { get; }

        /// <summary>Host-configured per-actor registered-instance capacity.</summary>
        public int MaxRegisteredInstancesPerActor { get; }

        /// <summary>
        /// Emergency ceiling on registered instances across every actor of this runtime: the host's value
        /// clamped to at most <see cref="DefaultEmergencyMaxRegisteredInstances"/>, so a host can lower the
        /// ceiling but never lift it above the platform's hard bound.
        /// </summary>
        public int EmergencyRegisteredInstanceCeiling { get; }

        /// <summary>Host-configured per-actor named-event subscription capacity.</summary>
        public int MaxEventSubscriptionsPerActor { get; }

        /// <summary>
        /// Consecutive-error streak (reset by a successful hook/timer call) at which a mod is quarantined.
        /// Scheduler threads count per frame: a frame with any fault adds one, a frame whose threads only
        /// ran cleanly resets the streak, and a successful hook/timer call does not forgive a frame whose
        /// scheduler fault was already charged.
        /// Quarantine suspends dispatch (handlers, timers, queued events) and reverts the mod's
        /// logic-slot overrides to vanilla, but the mod stays loaded; <see cref="ReloadMod"/> clears
        /// both the quarantine and the streak.
        /// </summary>
        public int MaxErrorsBeforeQuarantine { get; }

        /// <summary>
        /// Budget trips in a row (no clean call or frame between them; ordinary errors do not break the
        /// run) at which a mod is quarantined and its stored package is marked inactive and suspended
        /// (<see cref="LuaModManifest.SuspendedAfterBudgetTrips"/>), so neither a restart nor a world
        /// restore starts it again until it is loaded or reloaded by hand.
        /// </summary>
        public int MaxBudgetTripsBeforeQuarantine { get; }

        /// <summary>True when the Roblox API surface is wired, so mods can use RunService, task and Instance.</summary>
        public bool HasRbxApi => _rbxApi != null;

        /// <param name="gameplayBindings">
        /// Optional seam for registering ported world/unity gameplay APIs on each mod's
        /// <see cref="IScriptFunctionRegistry"/>, scoped to the mod's granted <see cref="LuaCapabilities"/>;
        /// the third argument is the owning mod's id so ownership-tracked surfaces (logic slots) can
        /// attribute what a mod registers. Null = mods only get the built-in mod-core APIs. See
        /// <see cref="RegisterGameplayBindings"/>.
        /// </param>
        /// <param name="store">Optional persistent per-mod k/v store backing <c>store_set/get</c>.</param>
        /// <param name="log">Optional logger.</param>
        /// <param name="handlerTimeoutMs">
        /// Wall-clock budget per handler/timer call and, with an Rbx API wired, per resume of a mod's main
        /// chunk (its <c>task.*</c> threads and signal handlers use the engine's coroutine resume budget).
        /// </param>
        /// <param name="handlerMaxSteps">
        /// Instruction budget per handler/timer call and, with an Rbx API wired, per resume of a mod's main
        /// chunk (its <c>task.*</c> threads and signal handlers use the engine's coroutine resume budget).
        /// </param>
        /// <param name="sourceStore">
        /// Optional package store persisting mod source + manifest so mods survive a restart and can be
        /// shared. Distinct from <paramref name="store"/> (which is per-mod runtime k/v). Null falls back
        /// to <see cref="NullLuaModSourceStore.Instance"/> (in-memory only — the prior behaviour).
        /// </param>
        /// <param name="autoPersistMods">
        /// When true (default), a successful <see cref="LoadMod"/>/<see cref="ReloadMod"/> persists the
        /// source + manifest to <paramref name="sourceStore"/> and <see cref="UnloadMod"/> marks the
        /// stored package dormant. Persistence is always best-effort: a store failure is logged, never
        /// thrown out of the load.
        /// </param>
        /// <param name="versionStore">
        /// Optional revision tracker. When supplied, every successful <see cref="LoadMod"/>/<see cref="ReloadMod"/>
        /// records the mod's source as a new revision (keyed by <see cref="VersionKeyPrefix"/> + mod id), so
        /// the agent (or host) can list past revisions and roll back via <see cref="ListModVersions"/> /
        /// <see cref="TryRevertMod"/>. Null falls back to <see cref="NullLuaScriptVersionStore"/> (no history —
        /// the prior behaviour).
        /// </param>
        /// <param name="transactionScope">
        /// Optional shared transaction scope of the gameplay bindings behind <paramref name="gameplayBindings"/>.
        /// When supplied, the runtime resets it around every load chunk and guarded handler/timer call —
        /// mirroring <see cref="LuaCsGameToolExecutor"/> — so a handler that dies between
        /// <c>coreai_world_begin</c> and commit cannot leave a stale transaction silently buffering the
        /// world commands of later handlers/timers.
        /// </param>
        /// <param name="handlerMaxAllocatedBytes">
        /// Live-heap growth allowed in ONE execution of a mod's code (the allocation-bomb backstop): every
        /// guarded hook/timer call and, with an Rbx API wired, every resume of the mod's main chunk, its
        /// <c>task.*</c> threads and its signal handlers. The budget starts over with each call or resume
        /// and never accumulates across them; a sampled heap reading only raises a suspicion, and a forced
        /// full collection has to confirm the growth is live before it trips
        /// (<see cref="LuaCsAllocationBudget"/>). A trip (<see cref="LuaCsExecutionGuard.IsMemoryBudgetTrip"/>)
        /// cuts that call or resume and is charged like any failure toward
        /// <see cref="MaxErrorsBeforeQuarantine"/>: a hook/timer trip adds one to the streak, a scheduler-thread
        /// trip makes its frame a faulting one. Defaults to
        /// <see cref="LuaCsExecutionGuard.DefaultMaxAllocatedBytesBudget"/>.
        /// </param>
        /// <param name="maxErrorsBeforeQuarantine">
        /// Consecutive-error streak (reset by any success) at which a mod is quarantined — dispatch
        /// suspended, mod kept loaded and repairable. Defaults to
        /// <see cref="DefaultMaxErrorsBeforeQuarantine"/>; clamped to at least 1.
        /// </param>
        /// <param name="logicSlots">
        /// Optional shared logic-slot surface. When supplied, the runtime clears a mod's slot
        /// overrides on unload/reload/quarantine (<see cref="ModTearingDown"/>) and records override
        /// failures in the same diagnostics channel as handler errors, attributed to the owning mod.
        /// </param>
        /// <param name="logService">
        /// Optional mod-log sink (see <see cref="ILuaLogService"/>). When supplied, report/print
        /// emissions, handler/dispatch failures, load (parse) failures, and quarantine events are
        /// appended to it in ADDITION to the existing console/event pipeline, so an in-game agent can
        /// read them back via the <c>get_mod_logs</c> tool. Null keeps the previous behavior (console
        /// log + events + bounded recent buffers only).
        /// </param>
        /// <param name="maxMods">
        /// Host-configured per-actor mod capacity. Defaults to <see cref="DefaultMaxMods"/>; benchmark hosts may
        /// use <see cref="BenchmarkMaxMods"/>. Values above <see cref="EmergencyMaxMods"/> never bypass
        /// the independent emergency ceiling.
        /// </param>
        /// <param name="observability">Optional production counter sink.</param>
        /// <param name="maxSchedulerThreadsPerActor">Per-actor live scheduler-thread quota.</param>
        /// <param name="maxRegisteredInstancesPerActor">Per-actor registered-instance quota.</param>
        /// <param name="maxEventSubscriptionsPerActor">Per-actor named-event subscription quota.</param>
        /// <param name="emergencyMaxRegisteredInstances">
        /// Emergency ceiling on registered instances across all actors. Defaults to
        /// <see cref="DefaultEmergencyMaxRegisteredInstances"/> (the WebGL save budget in a WebGL player);
        /// a larger value is clamped to that default and a value below one to one.
        /// </param>
        /// <param name="maxBudgetTripsBeforeQuarantine">
        /// Budget trips in a row at which a mod is quarantined and its stored package suspended. Defaults
        /// to <see cref="DefaultMaxBudgetTripsBeforeQuarantine"/>; clamped to at least 1.
        /// </param>
        public LuaCsModRuntime(
            Action<IScriptFunctionRegistry, LuaCapabilities, string> gameplayBindings = null,
            ILuaModStore store = null,
            ILog log = null,
            int handlerTimeoutMs = DefaultHandlerTimeoutMs,
            long handlerMaxSteps = DefaultHandlerMaxSteps,
            ILuaModSourceStore sourceStore = null,
            bool autoPersistMods = true,
            ILuaScriptVersionStore versionStore = null,
            ILuaTransactionScope transactionScope = null,
            long handlerMaxAllocatedBytes = LuaCsExecutionGuard.DefaultMaxAllocatedBytesBudget,
            IScriptEngine engine = null,
            int maxErrorsBeforeQuarantine = DefaultMaxErrorsBeforeQuarantine,
            LuaCsLogicSlots logicSlots = null,
            ILuaLogService logService = null,
            LuaCsRbxApiBindings rbxApi = null,
            int maxMods = DefaultMaxMods,
            IRbxRuntimeObservabilitySink observability = null,
            int maxSchedulerThreadsPerActor = ModScheduler.DefaultMaxThreadsPerActor,
            int maxRegisteredInstancesPerActor = DefaultMaxRegisteredInstancesPerActor,
            int maxEventSubscriptionsPerActor = DefaultMaxEventSubscriptionsPerActor,
            int emergencyMaxRegisteredInstances = DefaultEmergencyMaxRegisteredInstances,
            int maxBudgetTripsBeforeQuarantine = DefaultMaxBudgetTripsBeforeQuarantine)
        {
            _gameplayBindings = gameplayBindings;
            _store = store;
            _log = log;
            _logService = logService;
            _rbxApi = rbxApi;
            _observability = observability != null && observability.IsEnabled
                ? observability
                : null;
            _sourceStore = sourceStore ?? NullLuaModSourceStore.Instance;
            _versionStore = versionStore ?? new NullLuaScriptVersionStore();
            _autoPersistMods = autoPersistMods;
            _transactionScope = transactionScope;
            MaxMods = Math.Max(1, maxMods);
            MaxSchedulerThreadsPerActor = Math.Max(1, maxSchedulerThreadsPerActor);
            MaxRegisteredInstancesPerActor = Math.Max(1, maxRegisteredInstancesPerActor);
            MaxEventSubscriptionsPerActor = Math.Max(1, maxEventSubscriptionsPerActor);
            EmergencyRegisteredInstanceCeiling = Math.Max(
                1, Math.Min(emergencyMaxRegisteredInstances, DefaultEmergencyMaxRegisteredInstances));
            MaxErrorsBeforeQuarantine = Math.Max(1, maxErrorsBeforeQuarantine);
            MaxBudgetTripsBeforeQuarantine = Math.Max(1, maxBudgetTripsBeforeQuarantine);
            if (_rbxApi != null)
            {
                _rbxApi.Scheduler.ConfigureActorQuota(
                    MaxSchedulerThreadsPerActor, ResolveSchedulerActorId);
                _rbxApi.Scheduler.ThreadFaulted += OnSchedulerThreadFaulted;
                _rbxApi.Scheduler.ThreadResumeSucceeded += OnSchedulerThreadResumeSucceeded;

                // WHY only with a logger: an unobserved host fault is rethrown by Advance after every
                // frame it recurs in, so a pump that contains the throw logs the same failure every frame
                // and a pump that does not loses the rest of its own frame. With a logger this runtime
                // reports each distinct fault once instead; without one, subscribing would make the
                // fault vanish, so the scheduler's rethrow stays the report.
                if (_log != null)
                {
                    _rbxApi.Scheduler.HostFaulted += OnSchedulerHostFaulted;
                }

                _instanceQuotaAdmission = new InstanceQuotaAdmission(this);
                _rbxApi.Registry.AddRegistrationAdmission(_instanceQuotaAdmission);
                _rbxApi.Registry.Registered += OnInstanceRegistered;
                _rbxApi.Registry.Unregistered += OnInstanceUnregistered;
                SeedRegisteredInstanceCounts(_rbxApi.Registry);
                _rbxApi.ActorModsDisconnected += OnActorModsDisconnected;
            }

            _logicSlots = logicSlots;
            if (_logicSlots != null)
            {
                // WHY: A failing logic_define override is a MOD failure, not a host detail: routing it
                // into the handler-error channel makes it visible to diagnostics/auto-repair instead of
                // the old silent revert-to-vanilla.
                _logicSlots.OverrideFailed += OnLogicSlotOverrideFailed;
                _logicSlots.SetInvocationScope(EnterLogicSlotFormula, ExitLogicSlotFormula);
            }

            // WHY: The factory is the composition root that wires the engine; the default here only keeps
            // direct construction (tests, fixtures) working without an explicit engine.
            _engine = engine ?? new LuaCsScriptEngine();
            _marshaller = _engine.Marshaller;
            _scriptExecutionBudget = new ExecutionBudget(
                handlerTimeoutMs, handlerMaxSteps, handlerMaxAllocatedBytes);
            _handlerGuard = _engine.CreateGuard(_scriptExecutionBudget);
        }

        /// <summary>The <see cref="ILuaScriptVersionStore"/> key for a mod's revision history.</summary>
        private static string VersionKey(string modId)
        {
            return VersionKeyPrefix + modId;
        }

        /// <inheritdoc />
        public IReadOnlyList<LuaModInfo> ListMods(ActorContext caller)
        {
            RequireTrusted(caller);
            return ListMods();
        }

        /// <inheritdoc />
        public bool TryGetModSource(ActorContext caller, string id, out string source)
        {
            DemandModAccess(caller, "get_source", id);
            return TryGetModSource(id, out source);
        }

        /// <inheritdoc />
        public void LoadMod(
            ActorContext caller,
            string id,
            string luaCode,
            LuaCapabilities capabilities = LuaCapabilities.All,
            bool persistToStore = true)
        {
            DemandModAccess(caller, "load", id);
            LoadModInternal(
                id, luaCode, caller.ActorId, capabilities, persistToStore,
                caller.Grants.IsUnrestricted);
        }

        /// <summary>
        /// <see cref="LoadMod(ActorContext, string, string, LuaCapabilities, bool)"/> for a caller that
        /// requires the source store to keep the new mod's source (the world session facade):
        /// <paramref name="demandSourceKept"/> runs inside the load's commit, right after the source was
        /// persisted, and throws when the store did not keep it, so the load is undone by the failed-build
        /// rollback. Pass it only for an id the store does not hold yet.
        /// </summary>
        internal void LoadMod(
            ActorContext caller,
            string id,
            string luaCode,
            LuaCapabilities capabilities,
            bool persistToStore,
            Action<string> demandSourceKept)
        {
            DemandModAccess(caller, "load", id);
            LoadModInternal(
                id, luaCode, caller.ActorId, capabilities, persistToStore,
                caller.Grants.IsUnrestricted, demandSourceKept);
        }

        /// <inheritdoc />
        public string GetModOwnerActorId(ActorContext caller, string id)
        {
            DemandModAccess(caller, "get_owner", id);
            return GetModOwnerActorId(id);
        }

        /// <inheritdoc />
        /// <remarks>Runs in <see cref="ModReloadMode.CleanStartupObjects"/>, the default.</remarks>
        public void ReloadMod(ActorContext caller, string id, string luaCode)
        {
            DemandModAccess(caller, "reload", id);
            ReloadMod(id, luaCode, ModReloadMode.CleanStartupObjects);
        }

        /// <inheritdoc />
        /// <remarks>See <see cref="ReloadMod(string, string, ModReloadMode)"/>; never returns null.</remarks>
        public ModReloadReport ReloadMod(ActorContext caller, string id, string luaCode, ModReloadMode mode)
        {
            DemandModAccess(caller, "reload", id);
            return ReloadMod(id, luaCode, mode);
        }

        /// <inheritdoc />
        public bool UnloadMod(ActorContext caller, string id)
        {
            DemandModAccess(caller, "unload", id);
            return UnloadMod(id);
        }

        /// <inheritdoc />
        public string ExportMod(ActorContext caller, string id)
        {
            DemandModAccess(caller, "export", id);
            return ExportMod(id);
        }

        /// <inheritdoc />
        public bool ImportMod(
            ActorContext caller,
            string bundleJson,
            LuaCapabilities hostGrant,
            bool allowFull = false)
        {
            RequireTrusted(caller);
            string modId = TryReadBundleModId(bundleJson);
            if (modId == null)
            {
                return false;
            }

            DemandModAccess(caller, "import", modId);
            return ImportModInternal(
                bundleJson, caller.ActorId, hostGrant, allowFull,
                caller.Grants.IsUnrestricted);
        }

        /// <summary>
        /// <see cref="ImportMod(ActorContext, string, LuaCapabilities, bool)"/> with the check of
        /// <see cref="LoadMod(ActorContext, string, string, LuaCapabilities, bool, Action{string})"/> for a
        /// mod the import loads for the first time; the check's refusal is thrown, not answered as false.
        /// </summary>
        internal bool ImportMod(
            ActorContext caller,
            string bundleJson,
            LuaCapabilities hostGrant,
            bool allowFull,
            Action<string> demandSourceKept)
        {
            RequireTrusted(caller);
            string modId = TryReadBundleModId(bundleJson);
            if (modId == null)
            {
                return false;
            }

            DemandModAccess(caller, "import", modId);
            return ImportModInternal(
                bundleJson, caller.ActorId, hostGrant, allowFull,
                caller.Grants.IsUnrestricted, demandSourceKept);
        }

        /// <inheritdoc />
        public bool ForgetMod(ActorContext caller, string id)
        {
            DemandModAccess(caller, "forget", id);
            return ForgetMod(id);
        }

        /// <inheritdoc />
        public IReadOnlyList<LuaScriptRevision> ListModVersions(ActorContext caller, string id)
        {
            DemandModAccess(caller, "versions", id);
            return ListModVersions(id);
        }

        /// <inheritdoc />
        public bool TryRevertMod(
            ActorContext caller,
            string id,
            int revisionIndex,
            out string restoredSource)
        {
            DemandModAccess(caller, "revert", id);
            return TryRevertMod(id, revisionIndex, out restoredSource);
        }

        /// <inheritdoc />
        public IReadOnlyList<LuaModHandlerError> GetRecentHandlerErrors(
            ActorContext caller,
            string modId = null)
        {
            RequireTrusted(caller);
            if (!string.IsNullOrWhiteSpace(modId))
            {
                DemandModAccess(caller, "diagnostics", modId);
            }

            IReadOnlyList<LuaModHandlerError> errors = GetRecentHandlerErrors(modId);
            if (caller.Grants.IsUnrestricted)
            {
                return errors;
            }

            List<LuaModHandlerError> visible = new();
            foreach (LuaModHandlerError error in errors)
            {
                if (string.Equals(error.OwnerActorId, caller.ActorId, StringComparison.Ordinal))
                {
                    visible.Add(error);
                }
            }

            return visible;
        }

        /// <inheritdoc />
        public void Tick(ActorContext caller, double deltaSeconds)
        {
            DemandHostAdmin(caller, "tick");
            Tick(deltaSeconds);
        }

        /// <inheritdoc />
        public void EmitEvent(ActorContext caller, string name, string payload = "")
        {
            DemandHostAdmin(caller, "emit_event");
            EmitEvent(name, payload);
        }

        /// <inheritdoc />
        public bool IsLoaded(ActorContext caller, string id)
        {
            DemandModAccess(caller, "is_loaded", id);
            return IsLoaded(id);
        }

        /// <inheritdoc />
        public bool GetModReportLoggingEnabled(ActorContext caller, string id)
        {
            DemandModAccess(caller, "get_report_logging", id);
            return GetModReportLoggingEnabled(id);
        }

        /// <inheritdoc />
        public bool SetModReportLoggingEnabled(ActorContext caller, string id, bool enabled)
        {
            DemandModAccess(caller, "set_report_logging", id);
            return SetModReportLoggingEnabled(id, enabled);
        }

        /// <summary>Rehydrates stored mods for an unrestricted host caller.</summary>
        public int RehydrateFromStore(
            ActorContext caller,
            LuaCapabilities hostGrant,
            bool allowFull = false)
        {
            DemandHostAdmin(caller, "rehydrate");
            return RehydrateFromStore(hostGrant, allowFull);
        }

        /// <summary>Returns recent reports visible to the caller.</summary>
        public IReadOnlyList<LuaModReport> GetRecentReports(ActorContext caller, string modId = null)
        {
            RequireTrusted(caller);
            if (!string.IsNullOrWhiteSpace(modId))
            {
                DemandModAccess(caller, "get_reports", modId);
            }

            IReadOnlyList<LuaModReport> reports = GetRecentReports(modId);
            if (caller.Grants.IsUnrestricted)
            {
                return reports;
            }

            List<LuaModReport> visible = new();
            foreach (LuaModReport report in reports)
            {
                string ownerActorId = GetModOwnerActorId(report.ModId);
                if (string.Equals(ownerActorId, caller.ActorId, StringComparison.Ordinal))
                {
                    visible.Add(report);
                }
            }

            return visible;
        }

        /// <summary>Clears recent handler errors visible to an unrestricted host caller.</summary>
        public int ClearRecentHandlerErrors(ActorContext caller, string modId = null)
        {
            DemandHostAdmin(caller, "clear_handler_errors");
            return ClearRecentHandlerErrors(modId);
        }

        /// <summary>Clears recent reports visible to an unrestricted host caller.</summary>
        public int ClearRecentReports(ActorContext caller, string modId = null)
        {
            DemandHostAdmin(caller, "clear_reports");
            return ClearRecentReports(modId);
        }

        /// <inheritdoc />
        public void AddModHandlerErroredListener(ActorContext caller, Action<string, string, int> listener)
        {
            DemandHostAdminListener(caller, listener, "observe_handler_errors");
            ModHandlerErrored += listener;
        }

        /// <inheritdoc />
        public void RemoveModHandlerErroredListener(ActorContext caller, Action<string, string, int> listener)
        {
            DemandHostAdminListener(caller, listener, "observe_handler_errors");
            ModHandlerErrored -= listener;
        }

        /// <inheritdoc />
        public void AddModSourceLoadedListener(
            ActorContext caller,
            Action<string, string, LuaCapabilities> listener)
        {
            DemandHostAdminListener(caller, listener, "observe_source_loads");
            ModSourceLoaded += listener;
        }

        /// <inheritdoc />
        public void RemoveModSourceLoadedListener(
            ActorContext caller,
            Action<string, string, LuaCapabilities> listener)
        {
            DemandHostAdminListener(caller, listener, "observe_source_loads");
            ModSourceLoaded -= listener;
        }

        /// <inheritdoc />
        public void AddModSourceUnloadedListener(
            ActorContext caller,
            Action<string, string, LuaCapabilities> listener)
        {
            DemandHostAdminListener(caller, listener, "observe_source_unloads");
            ModSourceUnloaded += listener;
        }

        /// <inheritdoc />
        public void RemoveModSourceUnloadedListener(
            ActorContext caller,
            Action<string, string, LuaCapabilities> listener)
        {
            DemandHostAdminListener(caller, listener, "observe_source_unloads");
            ModSourceUnloaded -= listener;
        }

        /// <inheritdoc />
        public void AddModEventEmittedListener(ActorContext caller, Action<string, string, string> listener)
        {
            DemandHostAdminListener(caller, listener, "observe_mod_events");
            ModEventEmitted += listener;
        }

        /// <inheritdoc />
        public void RemoveModEventEmittedListener(ActorContext caller, Action<string, string, string> listener)
        {
            DemandHostAdminListener(caller, listener, "observe_mod_events");
            ModEventEmitted -= listener;
        }

        /// <inheritdoc />
        public void AddModReportEmittedListener(ActorContext caller, Action<string, string> listener)
        {
            DemandHostAdminListener(caller, listener, "observe_mod_reports");
            ModReportEmitted += listener;
        }

        /// <inheritdoc />
        public void RemoveModReportEmittedListener(ActorContext caller, Action<string, string> listener)
        {
            DemandHostAdminListener(caller, listener, "observe_mod_reports");
            ModReportEmitted -= listener;
        }

        private static void RequireTrusted(ActorContext caller)
        {
            if (!caller.IsTrusted)
            {
                throw new InvalidOperationException(
                    "Actor context was not issued by an identity provider.");
            }
        }

        private static void DemandHostAdmin(ActorContext caller, string operation)
        {
            RequireTrusted(caller);
            if (!caller.Grants.IsUnrestricted)
            {
                throw new UnauthorizedAccessException(
                    $"{operation}: actor '{caller.ActorId}' requires unrestricted host authority.");
            }
        }

        private static void DemandHostAdminListener(ActorContext caller, Delegate listener, string operation)
        {
            DemandHostAdmin(caller, operation);
            if (listener == null)
            {
                throw new ArgumentNullException(nameof(listener));
            }
        }

        private void DemandModAccess(ActorContext caller, string operation, string id)
        {
            RequireTrusted(caller);
            if (caller.Grants.IsUnrestricted)
            {
                return;
            }

            string modId = Normalize(id);
            string ownerActorId = GetModOwnerActorId(modId);
            if (ownerActorId == null || string.Equals(ownerActorId, caller.ActorId, StringComparison.Ordinal))
            {
                return;
            }

            string reason = ownerActorId.Length == 0
                ? "it is owned by the host/system"
                : $"it is owned by actor '{ownerActorId}'";
            throw new UnauthorizedAccessException(
                $"{operation}: actor '{caller.ActorId}' is not authorized to access mod '{modId}' because {reason}.");
        }

        private static string TryReadBundleModId(string bundleJson)
        {
            if (string.IsNullOrWhiteSpace(bundleJson))
            {
                return null;
            }

            try
            {
                LuaModBundle bundle = JsonConvert.DeserializeObject<LuaModBundle>(bundleJson);
                string modId = Normalize(bundle?.Manifest?.Id);
                return modId.Length == 0 ? null : modId;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>Snapshot of all loaded mods.</summary>
        internal IReadOnlyList<LuaModInfo> ListMods()
        {
            List<LuaModInfo> result = new();
            lock (_gate)
            {
                foreach (Mod mod in _modsInLoadOrder)
                {
                    result.Add(new LuaModInfo
                    {
                        Id = mod.Id,
                        Capabilities = mod.Caps,
                        HandlerCount = mod.HandlerCount,
                        TimerCount = mod.Timers.Count,
                        ErrorCount = mod.ErrorCount,
                        LogReports = mod.LogReports,
                        LoadedAtUtc = mod.LoadedAtUtc,
                        OwnerActorId = mod.OwnerActorId,
                        Quarantined = mod.Quarantined
                    });
                }
            }

            return result;
        }

        /// <summary>
        /// Returns the Lua source of a loaded mod (the exact chunk passed to
        /// <see cref="LoadMod"/>/<see cref="ReloadMod"/>). False when no mod with this id is loaded.
        /// </summary>
        internal bool TryGetModSource(string id, out string source)
        {
            lock (_gate)
            {
                if (_mods.TryGetValue(Normalize(id), out Mod mod))
                {
                    source = mod.Source;
                    return true;
                }
            }

            source = "";
            return false;
        }

        /// <summary>Returns whether <c>report()</c> output is logged for a loaded mod.</summary>
        internal bool GetModReportLoggingEnabled(string id)
        {
            lock (_gate)
            {
                return _mods.TryGetValue(Normalize(id), out Mod mod) && mod.LogReports;
            }
        }

        /// <summary>Enables or disables <c>report()</c> output for a loaded mod.</summary>
        internal bool SetModReportLoggingEnabled(string id, bool enabled)
        {
            lock (_gate)
            {
                if (!_mods.TryGetValue(Normalize(id), out Mod mod))
                {
                    return false;
                }

                mod.LogReports = enabled;
                return true;
            }
        }

        /// <summary>
        /// True when a load or reload writes the mod's source and manifest to the source store (the
        /// composition's auto-persist setting).
        /// </summary>
        internal bool PersistsModSources => _autoPersistMods;

        /// <summary>True when a mod with this id is currently loaded.</summary>
        internal bool IsLoaded(string id)
        {
            lock (_gate)
            {
                return _mods.ContainsKey(Normalize(id));
            }
        }

        /// <inheritdoc />
        internal string GetModOwnerActorId(string id)
        {
            string modId = Normalize(id);
            lock (_gate)
            {
                if (_mods.TryGetValue(modId, out Mod mod))
                {
                    return mod.OwnerActorId;
                }
            }

            if (_sourceStore.TryLoad(modId, out _, out LuaModManifest manifest))
            {
                return manifest?.OwnerActorId?.Trim() ?? "";
            }

            return null;
        }

        /// <summary>
        /// Loads a mod: creates a sandboxed Lua-CSharp state with the (optional) gameplay bindings plus
        /// mod-core APIs and runs the chunk (which registers its hooks). Throws on invalid input,
        /// duplicate id, mod-count limit, or script error — nothing is left registered when the load
        /// fails.
        /// </summary>
        /// <param name="persistToStore">
        /// When true (default), the mod's source and manifest are written to the source store. Rehydration
        /// and import pass false so loading a mod with masked runtime capabilities never overwrites the
        /// declared capabilities already recorded in the store.
        /// </param>
        internal void LoadMod(
            string id,
            string luaCode,
            LuaCapabilities capabilities = LuaCapabilities.All,
            bool persistToStore = true)
        {
            LoadModInternal(id, luaCode, "", capabilities, persistToStore, true);
        }

        /// <inheritdoc />
        internal void LoadModForActor(
            string id,
            string luaCode,
            string ownerActorId,
            LuaCapabilities capabilities = LuaCapabilities.All,
            bool persistToStore = true)
        {
            if (string.IsNullOrWhiteSpace(ownerActorId))
            {
                throw new ArgumentException("Owner actor id is required.", nameof(ownerActorId));
            }

            LoadModInternal(id, luaCode, ownerActorId.Trim(), capabilities, persistToStore, false);
        }

        private void EnsureModCapacity(string modId, string ownerActorId)
        {
            string actorId = NormalizeQuotaActorId(ownerActorId);
            if (_mods.Count >= EmergencyMaxMods)
            {
                throw new InvalidOperationException(
                    $"load: actor '{actorId}' cannot load mod '{modId}': emergency mod ceiling reached ({EmergencyMaxMods}).");
            }

            int actorModCount = 0;
            foreach (Mod loadedMod in _mods.Values)
            {
                if (string.Equals(
                        NormalizeQuotaActorId(loadedMod.OwnerActorId), actorId,
                        StringComparison.Ordinal))
                {
                    actorModCount++;
                }
            }

            if (actorModCount >= MaxMods)
            {
                throw new InvalidOperationException(
                    $"load: actor '{actorId}' cannot load mod '{modId}': loaded mods quota reached (limit {MaxMods}).");
            }
        }

        private void BindActorAttribution(
            string modId,
            string ownerActorId,
            bool ownerHasHostAuthority)
        {
            string actorId = NormalizeQuotaActorId(ownerActorId);
            lock (_gate)
            {
                _quotaActorByOwnerModId[modId] = actorId;
            }

            if (_rbxApi == null || ownerHasHostAuthority || string.IsNullOrWhiteSpace(ownerActorId))
            {
                return;
            }

            _rbxApi.Registry.BindActorAttribution(
                modId, OriginTag.FromMod(modId), ownerActorId);
        }

        private string ResolveSchedulerActorId(string ownerModId)
        {
            lock (_gate)
            {
                return _quotaActorByOwnerModId.TryGetValue(ownerModId, out string actorId)
                    ? actorId
                    : "host/system";
            }
        }

        private string ResolveInstanceQuotaActorId(InstanceRecord record)
        {
            if (!string.IsNullOrWhiteSpace(record.OwnerActorId))
            {
                return record.OwnerActorId.Trim();
            }

            if (!string.IsNullOrWhiteSpace(record.OwnerModId))
            {
                lock (_gate)
                {
                    if (_quotaActorByOwnerModId.TryGetValue(
                            record.OwnerModId, out string actorId))
                    {
                        return actorId;
                    }
                }
            }

            return "host/system";
        }

        /// <summary>
        /// Charges every record already live when this runtime attaches to <paramref name="registry"/>
        /// to the bucket <see cref="ChargeRegisteredInstance"/> would have charged it to.
        /// </summary>
        /// <remarks>
        /// WHY: a world load registers the whole restored tree while it stages, before the session's
        /// runtime exists. Without this pass none of those records counted toward the per-actor quota
        /// or the emergency ceiling, so every save/load cycle handed each actor a fresh quota.
        /// WHY nothing is refused or destroyed here, unlike <see cref="ChargeRegisteredInstance"/>: these
        /// records are the loaded world, not a creation request. An over-quota world stays whole, and
        /// only the next creation of an actor at or over its quota is refused.
        /// </remarks>
        private void SeedRegisteredInstanceCounts(InstanceRegistry registry)
        {
            IReadOnlyList<RbxInstance> live = registry.GetLiveInstances();
            for (int index = 0; index < live.Count; index++)
            {
                if (!registry.TryGetRecord(live[index].Id, out InstanceRecord record)
                    || record.IsRuntimeInfrastructure
                    || IsUnchargedWorldSkeleton(record))
                {
                    continue;
                }

                string actorId = ResolveInstanceQuotaActorId(record);
                lock (_instanceQuotaGate)
                {
                    if (_quotaActorByInstanceId.ContainsKey(record.Id))
                    {
                        continue;
                    }

                    _registeredInstancesByActor.TryGetValue(actorId, out int actorCount);
                    _quotaActorByInstanceId.Add(record.Id, actorId);
                    _registeredInstancesByActor[actorId] = actorCount + 1;
                    _registeredInstanceCount++;
                }
            }
        }

        /// <summary>
        /// True for the unattributed skeleton of a world: the DataModel, its services and the
        /// Workspace Camera, none of which a script can create.
        /// </summary>
        /// <remarks>
        /// WHY left out of the seeding pass: DataModelBootstrap builds this skeleton before any runtime
        /// can subscribe, so no live session has ever charged it. A restored package carries the same
        /// skeleton, and charging it on attach would make a fresh world pay host/system quota it never
        /// paid before and make a save/load round trip cost quota the saved session did not hold.
        /// </remarks>
        private static bool IsUnchargedWorldSkeleton(InstanceRecord record)
        {
            RbxInstance instance = record.Instance;
            return string.IsNullOrWhiteSpace(record.OwnerActorId)
                && !record.IsAuthoredContent
                && (instance.IsService
                    || instance is RbxDataModel
                    || string.Equals(instance.ClassName, "Camera", StringComparison.Ordinal));
        }

        /// <summary>
        /// The registry admission body (<see cref="InstanceQuotaAdmission"/>): charges
        /// <paramref name="record"/> to its quota actor and returns null, or returns the refusal and
        /// charges nothing when the runtime's emergency ceiling or the actor's quota is full. The
        /// refusal is a <see cref="RbxErrorCode.BudgetExceeded"/> §5.2.7 line, which the registry
        /// raises as that <see cref="RbxError"/>, led by the creation it refused, before the record is
        /// added or announced. Runtime infrastructure is admitted uncharged, and a record already
        /// charged is not charged twice.
        /// </summary>
        private string ChargeRegisteredInstance(InstanceRecord record)
        {
            if (record.IsRuntimeInfrastructure)
            {
                return null;
            }

            string actorId = ResolveInstanceQuotaActorId(record);
            lock (_instanceQuotaGate)
            {
                // WHY: a record the attach-time seeding already charged is never charged twice,
                // whichever of the two paths saw it first.
                if (_quotaActorByInstanceId.ContainsKey(record.Id))
                {
                    return null;
                }

                _registeredInstancesByActor.TryGetValue(actorId, out int actorCount);
                if (_registeredInstanceCount >= EmergencyRegisteredInstanceCeiling)
                {
                    return RbxError.Format(
                        RbxErrorCode.BudgetExceeded,
                        $"actor '{actorId}' cannot register instance '{record.Id.Value}': "
                        + $"emergency registered instances ceiling reached ({EmergencyRegisteredInstanceCeiling})",
                        "destroy instances you no longer need with Instance:Destroy(); this ceiling is shared "
                        + "by every actor in the world",
                        null, null, 0);
                }

                if (actorCount >= MaxRegisteredInstancesPerActor)
                {
                    return RbxError.Format(
                        RbxErrorCode.BudgetExceeded,
                        $"actor '{actorId}' cannot register instance '{record.Id.Value}': "
                        + $"registered instances quota reached (limit {MaxRegisteredInstancesPerActor})",
                        "destroy instances you no longer need with Instance:Destroy() before creating more",
                        null, null, 0);
                }

                _quotaActorByInstanceId.Add(record.Id, actorId);
                _registeredInstancesByActor[actorId] = actorCount + 1;
                _registeredInstanceCount++;
                return null;
            }
        }

        private void OnInstanceUnregistered(InstanceRecord record)
        {
            ReleaseRegisteredInstance(record);
            if (record.IsRuntimeInfrastructure || string.IsNullOrEmpty(record.OwnerModId))
            {
                return;
            }

            lock (_startupGate)
            {
                if (_startupCaptureByModId.Count != 0
                    && _startupCaptureByModId.TryGetValue(record.OwnerModId, out StartupCapture capture))
                {
                    capture.Remove(record.Id);
                }
            }
        }

        /// <summary>
        /// Records a mod-owned instance registered while a build of its mod runs its main chunk as one
        /// of that build's startup objects (<see cref="BeginStartupCapture"/>). Runtime infrastructure
        /// (the per-mod Script, Players) is never a startup object.
        /// </summary>
        private void OnInstanceRegistered(InstanceRecord record)
        {
            if (record.IsRuntimeInfrastructure || string.IsNullOrEmpty(record.OwnerModId))
            {
                return;
            }

            lock (_startupGate)
            {
                if (_startupCaptureByModId.Count != 0
                    && _startupCaptureByModId.TryGetValue(record.OwnerModId, out StartupCapture capture))
                {
                    capture.Add(record);
                }
            }
        }

        /// <summary>
        /// Starts collecting the startup objects of a build of <paramref name="modId"/>; null without
        /// the Roblox surface, which is the only thing that registers instances.
        /// </summary>
        private StartupCapture BeginStartupCapture(string modId)
        {
            if (_rbxApi == null)
            {
                return null;
            }

            lock (_startupGate)
            {
                _startupCaptureByModId.TryGetValue(modId, out StartupCapture enclosing);
                StartupCapture capture = new(modId, enclosing);
                _startupCaptureByModId[modId] = capture;
                return capture;
            }
        }

        /// <summary>Stops collecting for <paramref name="capture"/>; an interrupted outer build of the same id collects again.</summary>
        private void EndStartupCapture(StartupCapture capture)
        {
            if (capture == null)
            {
                return;
            }

            lock (_startupGate)
            {
                if (!_startupCaptureByModId.TryGetValue(capture.ModId, out StartupCapture current)
                    || !ReferenceEquals(current, capture))
                {
                    return;
                }

                if (capture.Enclosing != null)
                {
                    _startupCaptureByModId[capture.ModId] = capture.Enclosing;
                }
                else
                {
                    _startupCaptureByModId.Remove(capture.ModId);
                }
            }
        }

        /// <summary>
        /// Releases the charge <paramref name="record"/> holds, if any: on its Unregistered, or when a
        /// later registry admission refused a record this runtime had already admitted.
        /// </summary>
        private void ReleaseRegisteredInstance(InstanceRecord record)
        {
            lock (_instanceQuotaGate)
            {
                if (!_quotaActorByInstanceId.TryGetValue(record.Id, out string actorId))
                {
                    return;
                }

                _quotaActorByInstanceId.Remove(record.Id);
                _registeredInstanceCount--;
                int nextActorCount = _registeredInstancesByActor[actorId] - 1;
                if (nextActorCount == 0)
                {
                    _registeredInstancesByActor.Remove(actorId);
                }
                else
                {
                    _registeredInstancesByActor[actorId] = nextActorCount;
                }
            }
        }

        private static string NormalizeQuotaActorId(string ownerActorId)
        {
            return string.IsNullOrWhiteSpace(ownerActorId)
                ? "host/system"
                : ownerActorId.Trim();
        }

        /// <param name="demandSourceKept">
        /// Null, or the check of a caller that requires the source store to keep this new mod's source
        /// (the world session facade): the source is then persisted inside the build's commit and the
        /// check, run right after, throws when the store did not keep it, so the failed-build rollback
        /// undoes the load (see <see cref="CommitFirstLoadKeepingSource"/>). Only for an id the store
        /// does not hold yet.
        /// </param>
        private void LoadModInternal(
            string id,
            string luaCode,
            string ownerActorId,
            LuaCapabilities capabilities,
            bool persistToStore,
            bool ownerHasHostAuthority,
            Action<string> demandSourceKept = null)
        {
            ThrowIfShutdown();
            string modId = Normalize(id);
            if (modId.Length == 0)
            {
                throw new ArgumentException("Mod id is required.", nameof(id));
            }

            if (string.IsNullOrWhiteSpace(luaCode))
            {
                throw new ArgumentException("Mod code is required.", nameof(luaCode));
            }

            lock (_gate)
            {
                if (_mods.ContainsKey(modId))
                {
                    throw new InvalidOperationException($"Mod '{modId}' is already loaded. Use ReloadMod.");
                }

                // WHY refused before any chunk runs (C2-01): a second build of an id in flight used to
                // run to its commit and be refused there, and the rollback of that refused build, keyed
                // by mod id on the Roblox side, tore down the threads, connections and tweens of the
                // build that won.
                DemandNoBuildInProgressLocked(modId, $"Mod '{modId}' was loaded concurrently.");
                EnsureModCapacity(modId, ownerActorId);
                EnterModBuildLocked(modId);
            }

            bool persistedInCommit = persistToStore && demandSourceKept != null && _autoPersistMods;
            try
            {
                BuildMod(
                    modId, luaCode, capabilities, ownerActorId, ownerHasHostAuthority,
                    persistedInCommit
                        ? built => CommitFirstLoadKeepingSource(
                            modId, luaCode, capabilities, ownerActorId, built, demandSourceKept)
                        : built => InstallFirstLoad(modId, ownerActorId, built),
                    false);
            }
            finally
            {
                ExitModBuild(modId);
            }

            _log?.Info($"[LuaCsModRuntime] Mod '{modId}' loaded (caps={capabilities}).");

            // WHY: Record the revision before persisting so PersistMod can stamp the manifest Version from
            // the revision count; the version store dedups identical source so a rehydrate replay does not
            // add a spurious entry. Independent of persistToStore — a masked rehydrate/import still needs
            // its history seeded.
            RecordRevision(modId, luaCode);
            if (persistToStore)
            {
                if (!persistedInCommit)
                {
                    PersistMod(modId, luaCode, capabilities, ownerActorId, true);
                }
                else if (VersionFollowsRevisions(modId, luaCode))
                {
                    // WHY again: the commit persisted before the revision was recorded (a refused load must
                    // leave no revision behind), so a manifest whose version is the revision count is one
                    // behind; the rewrite keeps the load order the commit stamped.
                    PersistMod(modId, luaCode, capabilities, ownerActorId, false);
                }
            }

            RaiseModSourceLoaded(modId, luaCode, capabilities);
        }

        /// <summary>
        /// The commit of a first load whose caller requires the new source kept: it checks again that the
        /// id and a mod slot are still free, persists the source and manifest, runs
        /// <paramref name="demandSourceKept"/>, which throws when the store did not keep them, and only
        /// then adds the mod. Any refusal throws inside the build, so the rollback undoes the load like
        /// any failed first load (its threads, connections, logic-slot formulas and quota attribution),
        /// and no revision is recorded for it.
        /// </summary>
        /// <remarks>
        /// WHY here and not after the load (C2-04): the world facade used to undo such a load with an
        /// ordinary unload, which kept a formula of another live mod the chunk had displaced removed,
        /// kept the undone mod's revision, and raised a load and an unload for a mod that never loaded.
        /// </remarks>
        private void CommitFirstLoadKeepingSource(
            string modId,
            string luaCode,
            LuaCapabilities capabilities,
            string ownerActorId,
            Mod built,
            Action<string> demandSourceKept)
        {
            lock (_gate)
            {
                if (_mods.ContainsKey(modId))
                {
                    throw new InvalidOperationException($"Mod '{modId}' was loaded concurrently.");
                }

                EnsureModCapacity(modId, ownerActorId);
            }

            PersistMod(modId, luaCode, capabilities, ownerActorId, true);
            try
            {
                demandSourceKept(modId);
                InstallFirstLoad(modId, ownerActorId, built);
            }
            catch
            {
                // WHY deleted: the caller passes the check only for an id its store did not hold, so
                // whatever the store holds for it now is this refused load's own write.
                ForgetPersistedSource(modId);
                throw;
            }
        }

        /// <summary>Best-effort removal of the source a refused load persisted.</summary>
        private void ForgetPersistedSource(string modId)
        {
            if (!_autoPersistMods)
            {
                return;
            }

            try
            {
                _sourceStore.Delete(modId);
            }
            catch (Exception ex)
            {
                _log?.Error($"[LuaCsModRuntime] Removing the source of the refused load of '{modId}' failed: {ex}");
            }
        }

        /// <summary>True when the manifest version of <paramref name="source"/> is the revision count (its header names none).</summary>
        private static bool VersionFollowsRevisions(string modId, string source)
        {
            return string.IsNullOrWhiteSpace(LuaModHeader.Parse(source ?? "", modId).Version);
        }

        /// <summary>
        /// Adds a first load whose chunk built to the loaded mods, unless another load took its id or
        /// the last mod slot of its actor while the chunk ran.
        /// </summary>
        private void InstallFirstLoad(string modId, string ownerActorId, Mod mod)
        {
            lock (_gate)
            {
                if (_mods.ContainsKey(modId))
                {
                    throw new InvalidOperationException($"Mod '{modId}' was loaded concurrently.");
                }

                EnsureModCapacity(modId, ownerActorId);
                mod.LoadOrder = ++_nextLoadOrder;
                _mods[modId] = mod;
                _modsInLoadOrder.Add(mod);
                lock (_subscriptionGate)
                {
                    ActivateSubscriptionsLocked(mod);
                    PublishSubscriptionSnapshotLocked();
                }
            }
        }

        /// <summary>Refuses a reload whose mod another reload or an unload replaced while its chunk ran.</summary>
        private void DemandStillLoaded(string modId, Mod existing)
        {
            lock (_gate)
            {
                if (!_mods.TryGetValue(modId, out Mod live) || !ReferenceEquals(live, existing))
                {
                    throw new InvalidOperationException($"Mod '{modId}' was reloaded concurrently.");
                }
            }
        }

        /// <summary>
        /// Creates the sandboxed state with capability-scoped gameplay bindings plus mod-core APIs, runs
        /// the chunk (hook registration happens there) and then <paramref name="commit"/>. Errors,
        /// including a refusal from <paramref name="commit"/>, propagate to the caller and the mod is
        /// never added, so a failed build leaves no handlers behind; a failed reload's chunk has the
        /// objects it built destroyed, the logic-slot formulas its chunk defined or reset are put back as they were, and
        /// a failed first load drops the quota attribution it recorded. A successful build records the
        /// instances its main chunk registered as the new run's startup objects.
        /// </summary>
        /// <param name="commit">
        /// The last step of a successful build, inside its rollback: the checks that can still refuse a
        /// built candidate (another load took the id or the last mod slot meanwhile) and, for a first
        /// load, adding it to the loaded mods.
        /// </param>
        /// <param name="isReload">
        /// True for the candidate of a reload, whose failure also destroys the objects its chunk built;
        /// a failed first load keeps them.
        /// </param>
        /// <remarks>
        /// The caller has already entered the build of <paramref name="modId"/>
        /// (<see cref="EnterModBuildLocked"/>, refused while another build of the id is in flight) and
        /// ends it (<see cref="ExitModBuild"/>) once this returns or throws, so every candidate this
        /// builds is the only one of its id and its rollback, keyed by mod id, can only undo its own work.
        /// </remarks>
        private Mod BuildMod(
            string modId,
            string luaCode,
            LuaCapabilities capabilities,
            string ownerActorId,
            bool ownerHasHostAuthority,
            Action<Mod> commit,
            bool isReload)
        {
            Mod mod = new()
            {
                Id = modId,
                OwnerActorId = ownerActorId ?? "",
                OwnerHasHostAuthority = ownerHasHostAuthority,
                Source = luaCode,
                Caps = capabilities,
                LoadedAtUtc = DateTime.UtcNow
            };
            LuaCsRbxApiBindings.ModLoadCandidate rbxLoadCandidate = null;
            StartupCapture startupCapture = null;
            LuaCsLogicSlots.OverrideSnapshot slotsBeforeBuild = CaptureLogicSlots(modId);

            try
            {
                // WHY: Downlevel Luau -> Lua 5.2 BEFORE the VM compiles the chunk so mods may use Luau syntax
                // (compound assignment, continue, string interpolation, if-expressions, type annotations);
                // keyed by mod id so a downlevel error maps to the right source and throws out of the load,
                // never a silent raw fallback. mod.Source keeps the ORIGINAL author text so get_source/versions
                // round-trip the Luau the user wrote.
                string compileSource = LuauSourceGate.ToLua52(luaCode, modId);
                BindActorAttribution(modId, ownerActorId, ownerHasHostAuthority);
                rbxLoadCandidate = _rbxApi?.BeginModLoadCandidate(modId);

                IScriptFunctionRegistry registry = _engine.CreateFunctionRegistry();
                RegisterGameplayBindings(registry, capabilities, modId);
                RegisterModApis(registry, mod);

                // WHY: Create the state BEFORE running the chunk; the mod-core callbacks capture `mod` and read
                // mod.State (set here) only when they later run, so self-referential cross-mod calls made
                // during load resolve correctly.
                mod.State = _engine.CreateState();
                registry.ApplyTo(mod.State);

                // WHY: Run the load chunk on its own transaction frame (mirroring the MoonSharp runtime's
                // per-run reset) so a transaction left open by a failing load is discarded with the frame and
                // cannot bleed into later scripts — and a transaction leaked elsewhere cannot swallow this
                // chunk's world commands.
                startupCapture = BeginStartupCapture(modId);
                PushTransactionScope();
                try
                {
                    if (_rbxApi == null)
                    {
                        _engine.RunChunk(mod.State, compileSource);
                    }
                    else
                    {
                        _rbxApi.RunModChunk(
                            mod.State, modId, compileSource, _scriptExecutionBudget);
                    }
                }
                finally
                {
                    PopTransactionScope();
                    EndStartupCapture(startupCapture);
                }

                if (startupCapture != null)
                {
                    mod.StartupObjects = startupCapture.Snapshot();
                }

                // WHY: a chunk that disconnected its own actor and still reached its end would load a
                // mod for an actor that is gone, only for the queued release to unload it again.
                if (TryTakeActorDisconnectedDuringBuild(modId, out string departedActorId))
                {
                    throw ActorDisconnectedDuringLoad(modId, departedActorId, null);
                }

                // WHY inside the try: a candidate refused after its chunk ran used to be refused after
                // this rollback, so a load reported as failed kept the formulas its chunk defined, its
                // quota attribution and its connections and threads, and answered formula calls.
                commit(mod);
                return mod;
            }
            catch (Exception ex)
            {
                // WHY: the disconnect kills every thread of the actor's mods, this load's main chunk
                // among them, and the VM reports that as a bare cancellation that names neither the
                // mod nor the reason it stopped.
                Exception failure = TryTakeActorDisconnectedDuringBuild(modId, out string disconnectedActorId)
                    ? ActorDisconnectedDuringLoad(modId, disconnectedActorId, ex)
                    : ex;
                if (rbxLoadCandidate != null)
                {
                    try
                    {
                        _rbxApi.RollbackModLoadCandidate(rbxLoadCandidate);
                    }
                    catch (Exception rollbackException)
                    {
                        _log?.Error($"[LuaCsModRuntime] Failed-load rollback for '{modId}' failed: {rollbackException}");
                    }
                }

                // WHY after the rollback: its threads are stopped and its connections dropped first, so
                // nothing of the failed candidate reacts to its own objects going away. WHY for a reload
                // only: a failed reload promises the world as it was, and the rollback released what the
                // chunk registered with the bindings but not what it built, so its half-built objects
                // stayed next to the ones it was meant to replace. A failed first load keeps what its
                // chunk built, as a Roblox script that errors does.
                if (isReload)
                {
                    DestroyFailedBuildObjects(modId, startupCapture);
                }

                RestoreLogicSlotsAfterFailedBuild(slotsBeforeBuild, modId, mod.State);
                DropUnusedQuotaAttribution(modId, 1);

                // WHY: A failed load/parse never reaches the tick-time error channel, yet it is the
                // self-repair loop's most important signal — record it before rethrowing so get_mod_logs
                // can show WHY the mod never came up.
                AppendLog(modId, LuaLogLevel.RuntimeError, $"load failed: {SingleLineErrorMessage(failure)}");
                if (ReferenceEquals(failure, ex))
                {
                    throw;
                }

                throw failure;
            }
        }

        /// <summary>
        /// The error a load or reload fails with when the actor it runs as disconnected while its main
        /// chunk ran: the disconnect stops the chunk, and a mod whose actor is gone cannot load.
        /// </summary>
        private static InvalidOperationException ActorDisconnectedDuringLoad(
            string modId, string actorId, Exception inner)
        {
            return new InvalidOperationException(
                $"mod '{modId}' did not load: its actor '{actorId}' disconnected while its main chunk ran, " +
                "which stopped the chunk; load the mod again once the actor has reconnected.",
                inner);
        }

        /// <summary>
        /// Takes the record that the actor a build of <paramref name="modId"/> runs as disconnected
        /// while the build was in progress (<see cref="OnActorModsDisconnected"/>).
        /// </summary>
        private bool TryTakeActorDisconnectedDuringBuild(string modId, out string actorId)
        {
            lock (_gate)
            {
                if (_actorDisconnectedDuringBuild.Count == 0
                    || !_actorDisconnectedDuringBuild.TryGetValue(modId, out actorId))
                {
                    actorId = null;
                    return false;
                }

                _actorDisconnectedDuringBuild.Remove(modId);
                return true;
            }
        }

        /// <summary>
        /// Refuses a build of <paramref name="modId"/> while another one is in flight, with
        /// <paramref name="refusal"/>; call under the gate before anything of the new build runs.
        /// </summary>
        private void DemandNoBuildInProgressLocked(string modId, string refusal)
        {
            if (_buildDepthByModId.Count != 0 && _buildDepthByModId.ContainsKey(modId))
            {
                throw new InvalidOperationException(refusal);
            }
        }

        /// <summary>
        /// Marks <paramref name="modId"/> as having a candidate chunk under construction; call under the
        /// gate, after <see cref="DemandNoBuildInProgressLocked"/>, and end it with <see cref="ExitModBuild"/>.
        /// </summary>
        private void EnterModBuildLocked(string modId)
        {
            _buildDepthByModId.TryGetValue(modId, out int depth);
            _buildDepthByModId[modId] = depth + 1;
        }

        /// <summary>Ends one <see cref="EnterModBuildLocked"/> of <paramref name="modId"/>.</summary>
        private void ExitModBuild(string modId)
        {
            lock (_gate)
            {
                if (!_buildDepthByModId.TryGetValue(modId, out int depth))
                {
                    return;
                }

                if (depth <= 1)
                {
                    _buildDepthByModId.Remove(modId);
                    _actorDisconnectedDuringBuild.Remove(modId);
                }
                else
                {
                    _buildDepthByModId[modId] = depth - 1;
                }
            }
        }

        /// <summary>
        /// Captures the installed logic-slot formulas before a build of <paramref name="modId"/>; null
        /// without a slot surface or when the surface cannot be read.
        /// </summary>
        private LuaCsLogicSlots.OverrideSnapshot CaptureLogicSlots(string modId)
        {
            if (_logicSlots == null)
            {
                return null;
            }

            try
            {
                return _logicSlots.CaptureOverrides();
            }
            catch (Exception ex)
            {
                _log?.Error($"[LuaCsModRuntime] Capturing logic-slot overrides before building '{modId}' failed: {ex}");
                return null;
            }
        }

        /// <summary>
        /// Puts the logic-slot formulas back as they were before a failed build of
        /// <paramref name="modId"/> (<see cref="LuaCsLogicSlots.RestoreAfterFailedBuild"/>). Best-effort:
        /// a failing slot surface must not replace the load error.
        /// </summary>
        private void RestoreLogicSlotsAfterFailedBuild(LuaCsLogicSlots.OverrideSnapshot snapshot,
            string modId, IScriptState candidateState)
        {
            if (snapshot == null)
            {
                return;
            }

            try
            {
                int restored = snapshot.Owner.RestoreAfterFailedBuild(
                    snapshot, modId, candidateState, IsLogicSlotOwnerLive);
                if (restored > 0)
                {
                    _log?.Info(
                        $"[LuaCsModRuntime] Put back {restored} logic-slot override(s) the failed build of mod '{modId}' changed.");
                }
            }
            catch (Exception ex)
            {
                _log?.Error($"[LuaCsModRuntime] Restoring logic-slot overrides after the failed build of '{modId}' failed: {ex}");
            }
        }

        /// <summary>
        /// True when a logic-slot formula of <paramref name="ownerModId"/> may be installed again: its
        /// mod is loaded and not quarantined (an ownerless formula always may).
        /// </summary>
        private bool IsLogicSlotOwnerLive(string ownerModId)
        {
            string modId = Normalize(ownerModId);
            if (modId.Length == 0)
            {
                return true;
            }

            lock (_gate)
            {
                return _mods.TryGetValue(modId, out Mod mod) && !mod.Quarantined && !mod.Removed;
            }
        }

        /// <summary>
        /// Drops the quota attribution of <paramref name="modId"/> once no loaded instance and no build
        /// other than the caller's own <paramref name="callerBuilds"/> still charges to it.
        /// </summary>
        /// <remarks>
        /// WHY: every load records the actor its mod id is charged to before its chunk runs, and nothing
        /// removed the record, so the map grew by one entry for every mod id ever attempted (A2-11).
        /// WHY only then: a loaded instance and a build in flight of the same id resolve their
        /// scheduler threads and instances through this record.
        /// </remarks>
        private void DropUnusedQuotaAttribution(string modId, int callerBuilds)
        {
            lock (_gate)
            {
                if (_mods.ContainsKey(modId)
                    || _buildDepthByModId.TryGetValue(modId, out int builds) && builds > callerBuilds)
                {
                    return;
                }

                _quotaActorByOwnerModId.Remove(modId);
            }
        }

        /// <summary>
        /// SEAM — <c>// TODO(migration): connect ported world/unity gameplay bindings here.</c>
        /// The MoonSharp runtime registers ~36 capability-scoped world/unity APIs via
        /// <c>IGameLuaRuntimeBindings.RegisterGameplayApis(LuaApiRegistry)</c> (optionally scoped by
        /// <c>ICapabilityScopedLuaBindings</c>). Those heavy bindings are NOT ported to Lua-CSharp in
        /// this pass. Until a ported binding provider exists, a host may inject an
        /// <see cref="Action{IScriptFunctionRegistry, LuaCapabilities}"/> that registers gameplay APIs on the
        /// per-mod <see cref="IScriptFunctionRegistry"/>, scoped to <paramref name="capabilities"/>. The
        /// callback is responsible for its own fail-closed capability trimming.
        /// </summary>
        private void RegisterGameplayBindings(IScriptFunctionRegistry registry, LuaCapabilities capabilities,
            string ownerModId)
        {
            if (_gameplayBindings == null || capabilities == LuaCapabilities.None)
            {
                return;
            }

            // TODO(migration): connect ported world/unity gameplay bindings here.
            _gameplayBindings(registry, capabilities, ownerModId);
        }

        /// <summary>Unloads a mod and drops its handlers/timers/queued events.</summary>
        internal bool UnloadMod(string id)
        {
            return RemoveLoadedMod(Normalize(id), null);
        }

        /// <summary>
        /// Takes a loaded mod out of the runtime: drops its handlers, timers and queued events, tears its
        /// effects down as an <see cref="LuaModTeardownReason.Unload"/> and raises
        /// <see cref="ModSourceUnloaded"/>. False when no mod with this id is loaded.
        /// </summary>
        /// <param name="disconnectedActorId">
        /// Null for an unload someone asked for, which marks the stored package dormant so it does not
        /// start again on the next rehydrate. Otherwise the actor that disconnected: only a mod loaded
        /// for that actor without host authority is removed (false for any other), and its stored
        /// package is left as it was.
        /// </param>
        private bool RemoveLoadedMod(string modId, string disconnectedActorId)
        {
            string source;
            LuaCapabilities caps;
            Mod mod;
            lock (_gate)
            {
                if (!_mods.TryGetValue(modId, out mod))
                {
                    return false;
                }

                // WHY host-authority mods stay: their code runs as the host, not as the actor that
                // loaded them, so it is never refused NOT_AUTHORITY and that actor leaving is no reason
                // to stop it.
                if (disconnectedActorId != null
                    && (mod.OwnerHasHostAuthority
                        || !string.Equals(mod.OwnerActorId, disconnectedActorId, StringComparison.Ordinal)))
                {
                    return false;
                }

                source = mod.Source;
                caps = mod.Caps;
                mod.Removed = true;
                _mods.Remove(modId);
                _modsInLoadOrder.Remove(mod);
                lock (_subscriptionGate)
                {
                    DeactivateSubscriptionsLocked(mod);
                    PublishSubscriptionSnapshotLocked();
                }
            }

            TeardownModEffects(modId, LuaModTeardownReason.Unload);
            DropUnusedQuotaAttribution(modId, 0);
            _log?.Info(disconnectedActorId == null
                ? $"[LuaCsModRuntime] Mod '{modId}' unloaded."
                : $"[LuaCsModRuntime] Mod '{modId}' unloaded: its actor '{disconnectedActorId}' disconnected; its stored package, if any, is left as it was.");

            // WHY: Keep the persisted package but mark it dormant so it does not auto-reload next start; the
            // source is not lost (use ForgetMod to delete it). Best-effort: a store failure must not
            // break unloading.
            if (disconnectedActorId == null && _autoPersistMods)
            {
                try
                {
                    _sourceStore.SetActive(modId, false);
                }
                catch (Exception ex)
                {
                    _log?.Error($"[LuaCsModRuntime] Source store SetActive('{modId}', false) failed: {ex}");
                }
            }

            RaiseModSourceUnloaded(modId, source, caps);
            return true;
        }

        /// <summary>
        /// Replaces a loaded mod with new code in <see cref="ModReloadMode.CleanStartupObjects"/>, the
        /// default (see <see cref="ReloadMod(string, string, ModReloadMode)"/>).
        /// </summary>
        internal void ReloadMod(string id, string luaCode)
        {
            ReloadMod(id, luaCode, ModReloadMode.CleanStartupObjects);
        }

        /// <summary>
        /// Replaces a loaded mod with new code, keeping its capability tier. The new chunk is built and
        /// run first; if it fails, the old mod stays loaded and untouched (including its quarantine
        /// state) and the world is as it was: the objects the failed chunk built are destroyed and, in
        /// <see cref="ModReloadMode.CleanStartupObjects"/>, the previous run's startup objects, taken out
        /// of the world before the chunk ran, are put back exactly where they were. On success the old
        /// instance's runtime effects are torn down before the swap (<see cref="ModTearingDown"/> with
        /// <see cref="LuaModTeardownReason.Reload"/> — its logic-slot overrides are cleared while the
        /// replacement chunk's own <c>logic_define</c> calls are kept), the previous run's startup
        /// objects are destroyed (clean mode) or kept and still tracked (keep mode), and the replacement
        /// starts with a zero error streak and no quarantine, so reloading is THE way to bring a
        /// quarantined mod back to life.
        /// </summary>
        internal ModReloadReport ReloadMod(string id, string luaCode, ModReloadMode mode)
        {
            string modId = Normalize(id);
            if (string.IsNullOrWhiteSpace(luaCode))
            {
                throw new ArgumentException("Mod code is required.", nameof(luaCode));
            }

            LuaCapabilities caps;
            string ownerActorId;
            bool ownerHasHostAuthority;
            Mod existing;
            lock (_gate)
            {
                if (!_mods.TryGetValue(modId, out existing))
                {
                    throw new InvalidOperationException($"Mod '{modId}' is not loaded.");
                }

                // WHY refused here, before the previous run's objects leave the world or a chunk runs
                // (C2-01): see LoadModInternal. WHY the build stays entered until the swap below: a
                // second reload admitted between this build's end and its swap would build against
                // the same outgoing run and be refused at its own commit, and that refusal's rollback
                // would tear down this reload's live replacement.
                DemandNoBuildInProgressLocked(modId, $"Mod '{modId}' was reloaded concurrently.");
                EnterModBuildLocked(modId);
                caps = existing.Caps;
                ownerActorId = existing.OwnerActorId;
                ownerHasHostAuthority = existing.OwnerHasHostAuthority;
            }

            Mod replacement;
            StartupDetachment detachment = null;
            try
            {
                try
                {
                    // WHY detached before the chunk runs and destroyed only after it succeeded: the new
                    // chunk must build into a world without the previous run's objects (a script that
                    // looks for its folder first would otherwise find the old one), yet a reload that
                    // fails must leave the world exactly as it was, which a destroy could not undo.
                    detachment = mode == ModReloadMode.CleanStartupObjects
                        ? DetachStartupObjects(existing)
                        : null;
                    replacement = BuildMod(
                        modId, luaCode, caps, ownerActorId, ownerHasHostAuthority,
                        built => DemandStillLoaded(modId, existing), true);
                }
                catch
                {
                    ReattachStartupObjects(detachment);
                    throw;
                }

                // WHY: Teardown BEFORE the swap so the old instance's effects (its logic-slot overrides)
                // are gone by the time the replacement is live — the old formula must never be invoked
                // after the new load. The replacement's state is excluded: its load chunk already ran in
                // BuildMod and may have re-defined slots, and those fresh defines must survive.
                TeardownModEffects(modId, LuaModTeardownReason.Reload, replacement.State);

                bool replaced;
                lock (_gate)
                {
                    replaced = _mods.TryGetValue(modId, out Mod live) && ReferenceEquals(live, existing);
                    if (replaced)
                    {
                        SwapInReplacementLocked(existing, replacement);
                    }
                }

                if (!replaced)
                {
                    // WHY: only an unload during the teardown gets here (no other build of the id can
                    // run meanwhile); it released everything of the id, so the attribution this build
                    // kept alive goes too.
                    ReattachStartupObjects(detachment);
                    DropUnusedQuotaAttribution(modId, 1);
                    throw new InvalidOperationException($"Mod '{modId}' was reloaded concurrently.");
                }
            }
            finally
            {
                ExitModBuild(modId);
            }

            // WHY after the teardown: the replaced run's threads are stopped and its connections are
            // dropped by then, so none of its code sees its objects destroyed.
            ModReloadReport report;
            if (mode == ModReloadMode.CleanStartupObjects)
            {
                StartupCleanup cleanup = DestroyDetachedStartupObjects(detachment);
                if (cleanup.Survivors.Count > 0)
                {
                    replacement.StartupObjects.InsertRange(0, cleanup.Survivors);
                }

                report = new ModReloadReport(
                    modId, ModReloadMode.CleanStartupObjects, cleanup.Destroyed, 0, cleanup.Rescued);
            }
            else
            {
                // WHY kept objects stay tracked: they are still objects the mod's main chunks built and
                // still owns, so the next clean reload removes them with the newest run's own.
                List<StartupObject> kept = LiveStartupObjects(modId, existing.StartupObjects);
                replacement.StartupObjects.InsertRange(0, kept);
                report = new ModReloadReport(modId, ModReloadMode.KeepObjects, 0, kept.Count, 0);
            }

            _log?.Info($"[LuaCsModRuntime] Mod '{modId}' reloaded (caps={caps}); {report.Describe()}.");
            RecordRevision(modId, luaCode);
            PersistMod(modId, luaCode, caps, ownerActorId, false);
            RaiseModSourceLoaded(modId, luaCode, caps);
            return report;
        }

        /// <summary>Makes <paramref name="replacement"/> the loaded run of its mod in place of <paramref name="existing"/>; call under the gate.</summary>
        private void SwapInReplacementLocked(Mod existing, Mod replacement)
        {
            replacement.LoadOrder = existing.LoadOrder;
            _mods[replacement.Id] = replacement;
            int orderIndex = _modsInLoadOrder.IndexOf(existing);
            if (orderIndex >= 0)
            {
                _modsInLoadOrder[orderIndex] = replacement;
            }

            lock (_subscriptionGate)
            {
                DeactivateSubscriptionsLocked(existing);
                ActivateSubscriptionsLocked(replacement);
                PublishSubscriptionSnapshotLocked();
            }
        }

        /// <summary>
        /// The entries of <paramref name="objects"/> that are still alive and still the mod's: owned by
        /// <paramref name="modId"/> and by the actor that owned them at creation. An object a script or
        /// the host re-attributed to another actor is no longer the mod's to clean.
        /// </summary>
        private List<StartupObject> LiveStartupObjects(string modId, List<StartupObject> objects)
        {
            List<StartupObject> live = new();
            if (_rbxApi == null || objects == null)
            {
                return live;
            }

            InstanceRegistry registry = _rbxApi.Registry;
            for (int index = 0; index < objects.Count; index++)
            {
                StartupObject entry = objects[index];
                if (registry.TryGetRecord(entry.Id, out InstanceRecord record)
                    && !record.Instance.IsDestroyed
                    && string.Equals(record.OwnerModId, modId, StringComparison.Ordinal)
                    && string.Equals(record.OwnerActorId ?? "", entry.OwnerActorId ?? "", StringComparison.Ordinal))
                {
                    live.Add(entry);
                }
            }

            return live;
        }

        /// <summary>
        /// Takes the live startup objects of <paramref name="existing"/> out of the world before a clean
        /// reload runs its new chunk: each top-most one (no ancestor among them) gets Parent = nil, after
        /// its parent, its position among that parent's children and its ancestor chain were recorded;
        /// its descendants go with it. Null without the Roblox surface or once its world was replaced.
        /// </summary>
        private StartupDetachment DetachStartupObjects(Mod existing)
        {
            if (_rbxApi == null || _rbxApi.Registry.IsDetached)
            {
                return null;
            }

            List<StartupObject> objects = LiveStartupObjects(existing.Id, existing.StartupObjects);
            if (objects.Count == 0)
            {
                return new StartupDetachment(existing.Id, new HashSet<InstanceId>(), objects,
                    new List<DetachedStartupRoot>());
            }

            InstanceRegistry registry = _rbxApi.Registry;
            HashSet<InstanceId> members = new();
            for (int index = 0; index < objects.Count; index++)
            {
                members.Add(objects[index].Id);
            }

            // WHY every position is read before anything moves: taking one child out shifts the
            // positions of its later siblings, and the put-back inserts at the original positions.
            // WHY read once per parent: a mod that parents hundreds of startup parts straight into
            // Workspace would otherwise copy Workspace's child list once per part.
            Dictionary<RbxInstance, Dictionary<InstanceId, int>> positionsByParent = new();
            List<DetachedStartupRoot> roots = new();
            for (int index = 0; index < objects.Count; index++)
            {
                registry.TryGet(objects[index].Id, out RbxInstance instance);
                if (HasAncestorIn(instance, members))
                {
                    continue;
                }

                RbxInstance parent = instance.Parent;
                List<RbxInstance> ancestors = new();
                for (RbxInstance ancestor = parent; ancestor != null; ancestor = ancestor.Parent)
                {
                    ancestors.Add(ancestor);
                }

                int siblingIndex = parent == null ? -1 : PositionAmongChildren(positionsByParent, parent, instance);
                roots.Add(new DetachedStartupRoot(instance, parent, siblingIndex, ancestors));
            }

            StartupDetachment detachment = new(existing.Id, members, objects, roots);
            RunStartupObjectWork(existing.Id, "take the previous run's startup objects of mod '"
                                               + existing.Id + "' out of the world", () =>
            {
                for (int index = 0; index < roots.Count; index++)
                {
                    DetachedStartupRoot root = roots[index];
                    if (root.OriginalParent == null)
                    {
                        continue;
                    }

                    try
                    {
                        root.Instance.Parent = null;
                    }
                    catch (Exception ex)
                    {
                        _log?.Warn($"[LuaCsModRuntime] Taking startup object '{root.Instance.Name}' of mod '{existing.Id}' out of the world failed; it stays where it is: {ex.Message}");
                    }

                    root.Detached = root.Instance.Parent == null && !root.Instance.IsDestroyed;
                }
            });
            return detachment;
        }

        /// <summary>
        /// Puts the startup objects a clean reload took out back exactly where they were, after the
        /// reload failed: under their original parent at their original position (in ascending position
        /// order per parent, so each lands where it was), or, if that parent is gone, at the end of the
        /// nearest ancestor that still exists.
        /// </summary>
        private void ReattachStartupObjects(StartupDetachment detachment)
        {
            if (detachment == null || detachment.Roots.Count == 0)
            {
                return;
            }

            List<DetachedStartupRoot> detached = new();
            for (int index = 0; index < detachment.Roots.Count; index++)
            {
                if (detachment.Roots[index].Detached)
                {
                    detached.Add(detachment.Roots[index]);
                }
            }

            if (detached.Count == 0)
            {
                return;
            }

            detached.Sort((left, right) => left.SiblingIndex.CompareTo(right.SiblingIndex));
            try
            {
                RunStartupObjectWork(detachment.ModId, "put the startup objects of mod '" + detachment.ModId
                                                       + "' back after a failed reload",
                    () => ReattachDetachedRoots(detachment.ModId, detached));
            }
            catch (Exception ex)
            {
                // WHY contained: this runs while a failed reload's own error is on its way out, and that
                // error, not this one, is what the caller must see.
                _log?.Error($"[LuaCsModRuntime] Putting the startup objects of mod '{detachment.ModId}' back after a failed reload failed: {ex}");
            }
        }

        /// <summary>Puts each root of <paramref name="detached"/> (sorted by position) back; see <see cref="ReattachStartupObjects"/>.</summary>
        private void ReattachDetachedRoots(string modId, List<DetachedStartupRoot> detached)
        {
            for (int index = 0; index < detached.Count; index++)
            {
                DetachedStartupRoot root = detached[index];
                if (root.Instance.IsDestroyed || root.Instance.Parent != null)
                {
                    continue;
                }

                try
                {
                    if (!root.OriginalParent.IsDestroyed)
                    {
                        root.Instance.SetParentAtSiblingIndex(root.OriginalParent, root.SiblingIndex);
                    }
                    else
                    {
                        RbxInstance fallback = FirstSurvivingAncestor(root.Ancestors);
                        if (fallback != null)
                        {
                            root.Instance.Parent = fallback;
                        }
                    }

                    root.Detached = false;
                }
                catch (Exception ex)
                {
                    _log?.Error($"[LuaCsModRuntime] Putting startup object '{root.Instance.Name}' of mod '{modId}' back after a failed reload failed: {ex}");
                }
            }
        }

        /// <summary>
        /// Destroys the startup objects a successful clean reload took out of the world, with what the
        /// mod still owns inside them (<see cref="WithOwnDescendants"/>). Anything else inside them (a
        /// player's build, another mod's object) is moved out first to the nearest ancestor that is not
        /// being destroyed (for the top-most ones, their original parent, or the nearest ancestor of it
        /// that still exists) and is never destroyed.
        /// </summary>
        private StartupCleanup DestroyDetachedStartupObjects(StartupDetachment detachment)
        {
            if (detachment == null || detachment.Members.Count == 0)
            {
                return new StartupCleanup(0, 0, new List<StartupObject>());
            }

            List<RbxInstance> roots = new(detachment.Roots.Count);
            List<List<RbxInstance>> chains = new(detachment.Roots.Count);
            for (int index = 0; index < detachment.Roots.Count; index++)
            {
                roots.Add(detachment.Roots[index].Instance);
                chains.Add(detachment.Roots[index].Ancestors);
            }

            try
            {
                HashSet<InstanceId> doomed = WithOwnDescendants(
                    detachment.ModId, detachment.Members, detachment.Objects, roots);
                return DestroyStartupObjects(detachment.ModId, doomed, detachment.Objects,
                    roots, chains,
                    "destroy the previous run's startup objects of mod '" + detachment.ModId + "'");
            }
            catch (Exception ex)
            {
                // WHY contained: the reload itself succeeded and the new run is live; a cleanup that
                // could not run must not report the reload as failed. What stays alive stays tracked.
                _log?.Error($"[LuaCsModRuntime] Destroying the previous run's startup objects of mod '{detachment.ModId}' failed: {ex}");
                return new StartupCleanup(0, 0, LiveStartupObjects(detachment.ModId, detachment.Objects));
            }
        }

        /// <summary>
        /// <paramref name="members"/> plus every object inside them that the mod still owns as the same
        /// actor as the startup object it sits in, reached through such objects only. What the mod's
        /// handlers or timers put into its startup objects (coins a Heartbeat handler drops into the
        /// mod's folder) goes with them; an object anyone else owns stops the walk, so it is rescued with
        /// everything inside it.
        /// </summary>
        /// <remarks>
        /// WHY (HUB-RELOAD follow-up H1): the startup objects are only what the main chunk built, so a
        /// clean reload of a mod whose Heartbeat fills its own folder rescued every such object to the
        /// folder's parent, and each Save &amp; run left the previous run's coins lying in Workspace.
        /// </remarks>
        private HashSet<InstanceId> WithOwnDescendants(string modId, HashSet<InstanceId> members,
            List<StartupObject> objects, List<RbxInstance> roots)
        {
            HashSet<InstanceId> doomed = new(members);
            Dictionary<InstanceId, string> actorByMember = new(objects.Count);
            for (int index = 0; index < objects.Count; index++)
            {
                actorByMember[objects[index].Id] = objects[index].OwnerActorId ?? "";
            }

            InstanceRegistry registry = _rbxApi.Registry;
            Stack<KeyValuePair<RbxInstance, string>> pending = new();
            for (int index = 0; index < roots.Count; index++)
            {
                RbxInstance root = roots[index];
                if (root != null && !root.IsDestroyed
                                 && actorByMember.TryGetValue(root.Id, out string rootActor))
                {
                    pending.Push(new KeyValuePair<RbxInstance, string>(root, rootActor));
                }
            }

            while (pending.Count > 0)
            {
                KeyValuePair<RbxInstance, string> entry = pending.Pop();
                IReadOnlyList<RbxInstance> children = entry.Key.GetChildren();
                for (int index = 0; index < children.Count; index++)
                {
                    RbxInstance child = children[index];
                    if (child.IsDestroyed)
                    {
                        continue;
                    }

                    if (actorByMember.TryGetValue(child.Id, out string memberActor))
                    {
                        pending.Push(new KeyValuePair<RbxInstance, string>(child, memberActor));
                        continue;
                    }

                    if (registry.TryGetRecord(child.Id, out InstanceRecord record)
                        && !record.IsRuntimeInfrastructure
                        && string.Equals(record.OwnerModId, modId, StringComparison.Ordinal)
                        && string.Equals(record.OwnerActorId ?? "", entry.Value, StringComparison.Ordinal))
                    {
                        doomed.Add(child.Id);
                        pending.Push(new KeyValuePair<RbxInstance, string>(child, entry.Value));
                    }
                }
            }

            return doomed;
        }

        /// <summary>
        /// Destroys what the main chunk of a reload that failed created, so the failed reload leaves no
        /// half-built objects behind; anything else placed inside them survives the same way it does in a
        /// clean reload.
        /// </summary>
        private void DestroyFailedBuildObjects(string modId, StartupCapture capture)
        {
            if (capture == null || _rbxApi == null || _rbxApi.Registry.IsDetached)
            {
                return;
            }

            try
            {
                List<StartupObject> objects = LiveStartupObjects(modId, capture.Snapshot());
                if (objects.Count == 0)
                {
                    return;
                }

                HashSet<InstanceId> members = new();
                for (int index = 0; index < objects.Count; index++)
                {
                    members.Add(objects[index].Id);
                }

                List<RbxInstance> roots = new();
                List<List<RbxInstance>> chains = new();
                for (int index = 0; index < objects.Count; index++)
                {
                    _rbxApi.Registry.TryGet(objects[index].Id, out RbxInstance instance);
                    if (!HasAncestorIn(instance, members))
                    {
                        roots.Add(instance);
                        chains.Add(new List<RbxInstance>());
                    }
                }

                StartupCleanup cleanup = DestroyStartupObjects(modId, members, objects, roots, chains,
                    "destroy what the failed build of mod '" + modId + "' created");
                if (cleanup.Destroyed > 0)
                {
                    _log?.Info(
                        $"[LuaCsModRuntime] Destroyed {cleanup.Destroyed} object(s) the failed build of mod '{modId}' created.");
                }
            }
            catch (Exception ex)
            {
                _log?.Error($"[LuaCsModRuntime] Destroying what the failed build of mod '{modId}' created failed: {ex}");
            }
        }

        /// <summary>
        /// Destroys the <paramref name="members"/> under each of <paramref name="roots"/>, deepest first:
        /// before a member goes, each child of it that is not a member is moved to the nearest ancestor
        /// of that member that is not a member, or, past a root with no parent, to the first surviving
        /// entry of that root's recorded <paramref name="chains"/> entry (nil when there is none).
        /// Best-effort per object: a failing destroy is logged and its object stays tracked.
        /// </summary>
        private StartupCleanup DestroyStartupObjects(string modId, HashSet<InstanceId> members,
            List<StartupObject> objects, List<RbxInstance> roots, List<List<RbxInstance>> chains,
            string operation)
        {
            int destroyed = 0;
            int rescued = 0;
            RunStartupObjectWork(modId, operation, () =>
            {
                for (int rootIndex = 0; rootIndex < roots.Count; rootIndex++)
                {
                    RbxInstance root = roots[rootIndex];
                    if (root == null || root.IsDestroyed)
                    {
                        continue;
                    }

                    List<RbxInstance> preorder = new() { root };
                    preorder.AddRange(root.GetDescendants());
                    for (int index = preorder.Count - 1; index >= 0; index--)
                    {
                        RbxInstance node = preorder[index];
                        if (node.IsDestroyed || !members.Contains(node.Id))
                        {
                            continue;
                        }

                        try
                        {
                            IReadOnlyList<RbxInstance> children = node.GetChildren();
                            if (children.Count > 0)
                            {
                                RbxInstance target = RescueTarget(node, members, chains[rootIndex]);
                                for (int childIndex = 0; childIndex < children.Count; childIndex++)
                                {
                                    RbxInstance child = children[childIndex];
                                    if (child.IsDestroyed || members.Contains(child.Id))
                                    {
                                        continue;
                                    }

                                    child.Parent = target;
                                    rescued++;
                                }
                            }

                            node.Destroy();
                            destroyed++;
                        }
                        catch (Exception ex)
                        {
                            _log?.Error($"[LuaCsModRuntime] Destroying startup object '{node.Name}' of mod '{modId}' failed: {ex}");
                        }
                    }
                }
            });

            List<StartupObject> survivors = new();
            for (int index = 0; index < objects.Count; index++)
            {
                if (_rbxApi.Registry.TryGetRecord(objects[index].Id, out InstanceRecord record)
                    && !record.Instance.IsDestroyed)
                {
                    survivors.Add(objects[index]);
                }
            }

            return new StartupCleanup(destroyed, rescued, survivors);
        }

        /// <summary>
        /// Where a child of <paramref name="node"/> that is not a startup object goes before
        /// <paramref name="node"/> is destroyed: the nearest ancestor of it that is not a startup object
        /// being destroyed; past a detached root, the first surviving entry of the root's recorded
        /// ancestors; null (nil) when there is none.
        /// </summary>
        private static RbxInstance RescueTarget(RbxInstance node, HashSet<InstanceId> members,
            List<RbxInstance> rootAncestors)
        {
            for (RbxInstance ancestor = node.Parent; ancestor != null; ancestor = ancestor.Parent)
            {
                if (!members.Contains(ancestor.Id))
                {
                    return ancestor;
                }
            }

            return FirstSurvivingAncestor(rootAncestors);
        }

        private static RbxInstance FirstSurvivingAncestor(List<RbxInstance> ancestors)
        {
            if (ancestors == null)
            {
                return null;
            }

            for (int index = 0; index < ancestors.Count; index++)
            {
                if (!ancestors[index].IsDestroyed)
                {
                    return ancestors[index];
                }
            }

            return null;
        }

        private static bool HasAncestorIn(RbxInstance instance, HashSet<InstanceId> members)
        {
            for (RbxInstance ancestor = instance.Parent; ancestor != null; ancestor = ancestor.Parent)
            {
                if (members.Contains(ancestor.Id))
                {
                    return true;
                }
            }

            return false;
        }

        private static int PositionAmongChildren(
            Dictionary<RbxInstance, Dictionary<InstanceId, int>> positionsByParent, RbxInstance parent,
            RbxInstance child)
        {
            if (!positionsByParent.TryGetValue(parent, out Dictionary<InstanceId, int> positions))
            {
                IReadOnlyList<RbxInstance> children = parent.GetChildren();
                positions = new Dictionary<InstanceId, int>(children.Count);
                for (int index = 0; index < children.Count; index++)
                {
                    positions[children[index].Id] = index;
                }

                positionsByParent.Add(parent, positions);
            }

            return positions.TryGetValue(child.Id, out int position) ? position : int.MaxValue;
        }

        /// <summary>
        /// Runs the runtime's own work on a mod's startup objects as one server-generated mutation of
        /// the actor the mod runs as, the way a script's changes are applied, so replication and the
        /// world ACL see it like any script change. When the mod's actor can no longer be resolved (its
        /// attribution was released) the work runs directly: the objects are the mod's own and the host
        /// is cleaning them.
        /// </summary>
        private void RunStartupObjectWork(string modId, string operation, Action work)
        {
            ActorContext actor;
            try
            {
                actor = _rbxApi.ResolveOwnerActorContext(modId);
            }
            catch (RbxError)
            {
                work();
                return;
            }

            _rbxApi.Registry.ApplyServerGeneratedMutation(
                actor.ActorId,
                actor.Grants.IsUnrestricted,
                actor.WorldId,
                operation,
                () =>
                {
                    work();
                    return true;
                });
        }

        /// <summary>
        /// Records <paramref name="luaCode"/> as a new revision of the mod in the version store. Best-effort:
        /// a store failure is logged, never thrown out of a load/reload. <c>SeedOriginal</c> establishes the
        /// baseline (revision 0) on the first record; <c>RecordSuccessfulExecution</c> appends a new revision
        /// only when the source actually changed, so a no-op reload does not grow the history.
        /// </summary>
        private void RecordRevision(string modId, string luaCode)
        {
            try
            {
                string key = VersionKey(modId);
                _versionStore.SeedOriginal(key, luaCode);
                _versionStore.RecordSuccessfulExecution(key, luaCode);
            }
            catch (Exception ex)
            {
                _log?.Error($"[LuaCsModRuntime] Version store record for '{modId}' failed: {ex}");
            }
        }

        /// <summary>
        /// Returns the recorded revision history for a mod (revision 0 = original), newest last, or an empty
        /// list when the mod has no tracked history (no version store, or never loaded through one).
        /// </summary>
        internal IReadOnlyList<LuaScriptRevision> ListModVersions(string id)
        {
            string modId = Normalize(id);
            try
            {
                if (_versionStore.TryGetSnapshot(VersionKey(modId), out LuaScriptVersionRecord snapshot) &&
                    snapshot != null)
                {
                    return snapshot.History;
                }
            }
            catch (Exception ex)
            {
                _log?.Error($"[LuaCsModRuntime] Version store snapshot for '{modId}' failed: {ex}");
            }

            return Array.Empty<LuaScriptRevision>();
        }

        /// <summary>
        /// Rolls a mod back to a recorded revision. When the mod is currently loaded it is reloaded from that
        /// revision's source; the reload appends the restored source as the new current revision (a
        /// non-destructive revert — the history is an audit trail, not rewound) and re-persists the source
        /// store and manifest Version. When the mod is not loaded the version store is rewound to that revision
        /// instead (truncating later revisions), so a future load starts from the chosen point. Sets
        /// <paramref name="restoredSource"/> and returns true on success; returns false when the mod has no such
        /// revision. Throws if the restored source fails to reload (the live mod stays untouched, exactly like
        /// <see cref="ReloadMod"/>).
        /// </summary>
        internal bool TryRevertMod(string id, int revisionIndex, out string restoredSource)
        {
            restoredSource = null;
            string modId = Normalize(id);
            if (revisionIndex < 0)
            {
                return false;
            }

            LuaScriptVersionRecord snapshot;
            try
            {
                if (!_versionStore.TryGetSnapshot(VersionKey(modId), out snapshot) || snapshot == null)
                {
                    return false;
                }
            }
            catch (Exception ex)
            {
                _log?.Error($"[LuaCsModRuntime] Version store snapshot for '{modId}' failed: {ex}");
                return false;
            }

            // WHY: Revision indices are stable sequence numbers assigned by the version store, not positions in
            // History: the store's retention policy can evict middle revisions, leaving gaps, so the
            // requested index must be searched rather than used to index the list directly.
            LuaScriptRevision revision = FindRevisionByIndex(snapshot.History, revisionIndex);
            if (revision == null)
            {
                return false;
            }

            string source = revision.Source ?? "";
            if (string.IsNullOrWhiteSpace(source))
            {
                return false;
            }

            // WHY: Reload first (it can throw on a bad revision, leaving the live mod untouched), then truncate the
            // version history to the chosen revision so a future revert references a clean lineage. Reload
            // re-records the restored source as the new current revision and re-persists the source store.
            if (IsLoaded(modId))
            {
                ReloadMod(modId, source);
            }
            else
            {
                try
                {
                    _versionStore.ResetToRevision(VersionKey(modId), revisionIndex);
                }
                catch (Exception ex)
                {
                    _log?.Error($"[LuaCsModRuntime] Version store revert for '{modId}' failed: {ex}");
                    return false;
                }
            }

            restoredSource = source;
            return true;
        }

        /// <summary>Finds the revision with the given stable <see cref="LuaScriptRevision.Index"/>, or null if it was evicted or never recorded.</summary>
        private static LuaScriptRevision FindRevisionByIndex(IReadOnlyList<LuaScriptRevision> history,
            int revisionIndex)
        {
            for (int i = 0; i < history.Count; i++)
            {
                if (history[i].Index == revisionIndex)
                {
                    return history[i];
                }
            }

            return null;
        }

        /// <summary>Queues a game event for subscribed mods on the next <see cref="Tick"/>.</summary>
        internal void EmitEvent(string name, string payload = "")
        {
            if (_shutdown)
            {
                return;
            }

            string evt = Normalize(name);
            if (evt.Length == 0)
            {
                return;
            }

            RouteEvent(null, evt, payload ?? "");
        }

        /// <summary>
        /// Runs the timer phase before the event phase. Each phase visits mods in stable load order;
        /// timers use registration order, queued events use FIFO order, and handlers use registration
        /// order. Both phases share one global invocation budget. Call once per frame from the host
        /// (main thread); every handler call is individually instruction/time guarded.
        /// </summary>
        internal void Tick(double deltaSeconds)
        {
            if (_shutdown)
            {
                return;
            }

            if (deltaSeconds < 0d || double.IsNaN(deltaSeconds))
            {
                return;
            }

            ReleaseDisconnectedActorMods();
            lock (_gate)
            {
                if (_mods.Count == 0)
                {
                    return;
                }

                _tickScratch.Clear();
                foreach (Mod mod in _modsInLoadOrder)
                {
                    if (!mod.Quarantined)
                    {
                        _tickScratch.Add(mod);
                    }
                }

                if (_tickScratch.Count == 0)
                {
                    return;
                }
            }

            int count = _tickScratch.Count;
            int completedThisTick = 0;
            int eventsDeliveredThisTick = 0;
            for (int i = 0; i < count; i++)
            {
                Mod mod = _tickScratch[i];

                // WHY: a handler of this tick can unload a mod of the snapshot (a kick ends the
                // connection synchronously, and the actor's mods go as soon as the handler returns). A
                // removed mod must never run again: its teardown forgot the actor it ran as, so its
                // hooks would resolve to the host fallback.
                if (mod.Removed)
                {
                    continue;
                }

                try
                {
                    completedThisTick += TickTimers(mod, deltaSeconds, completedThisTick);
                }
                catch (Exception ex)
                {
                    // WHY: A single mod's dispatch failure must never abort the other mods' frame tick.
                    mod.ErrorCount++;
                    mod.FaultedThisFrame = true;
                    _log?.Error($"[LuaCsModRuntime] Mod '{mod.Id}' scheduled dispatch failed: {ex}");
                    AppendLog(mod.Id, LuaLogLevel.RuntimeError,
                        $"scheduled dispatch failed: {SingleLineErrorMessage(ex)}");
                }
            }

            for (int i = 0; i < count; i++)
            {
                Mod mod = _tickScratch[i];
                if (mod.Removed)
                {
                    continue;
                }

                try
                {
                    int eventsDelivered = DispatchPendingEvents(mod, completedThisTick);
                    completedThisTick += eventsDelivered;
                    eventsDeliveredThisTick += eventsDelivered;
                }
                catch (Exception ex)
                {
                    // WHY: A single mod's dispatch failure must never abort the other mods' frame tick.
                    mod.ErrorCount++;
                    mod.FaultedThisFrame = true;
                    _log?.Error($"[LuaCsModRuntime] Mod '{mod.Id}' scheduled dispatch failed: {ex}");
                    AppendLog(mod.Id, LuaLogLevel.RuntimeError,
                        $"scheduled dispatch failed: {SingleLineErrorMessage(ex)}");
                }

                CloseModFrame(mod);
                QuarantineIfExhausted(mod);
            }

            if (_observability != null && completedThisTick > 0)
            {
                if (eventsDeliveredThisTick > 0)
                {
                    try
                    {
                        _observability.RecordEventsDelivered(eventsDeliveredThisTick);
                    }
                    catch
                    {
                    }
                }

                try
                {
                    _observability.RecordCompletedOperations(completedThisTick);
                }
                catch
                {
                }
            }
        }

        /// <summary>
        /// Ends one frame of <paramref name="mod"/>'s scheduler record at <see cref="Tick"/>: a frame whose
        /// scheduler threads only yielded or completed cleanly, with no failure of any kind charged in it,
        /// resets the error streak; a frame with a failure keeps the streak its failures built.
        /// </summary>
        /// <remarks>
        /// WHY a streak of faulting frames, reset by a clean frame, rather than a per-fault count reset by
        /// every success or a sliding window: a scheduler mod runs as many short resumes per frame. A
        /// per-fault streak that any clean resume resets never sees a signal cascade repeated every frame,
        /// because the chain's own handler resumes succeed before the chain is cut, while a count that
        /// nothing resets quarantines a healthy mod for a handful of errors spread over an hour. Counting
        /// a frame once, however many faults it held, also keeps one burst (a physics step that fires an
        /// erroring Touched handler for many parts at once) from quarantining on its own. A sliding window
        /// needs a time or frame horizon that is wrong either for a 30 Hz WebGL page or for a world paused
        /// at timeScale 0, and it would forgive a streak no success ever interrupted. A frame with no
        /// activity changes nothing, so K failures in a row still quarantine however far apart they are.
        /// </remarks>
        private void CloseModFrame(Mod mod)
        {
            lock (_gate)
            {
                if (mod.SchedulerSucceededThisFrame && !mod.FaultedThisFrame)
                {
                    mod.ErrorCount = 0;
                    mod.BudgetTripStreak = 0;
                }

                mod.FaultedThisFrame = false;
                mod.SchedulerFaultChargedThisFrame = false;
                mod.SchedulerSucceededThisFrame = false;
            }
        }

        /// <summary>
        /// Quarantines a mod whose consecutive-error streak reached <see cref="MaxErrorsBeforeQuarantine"/>:
        /// dispatch is suspended and its logic-slot overrides revert to vanilla, but the mod stays in the
        /// registry so diagnostics still see it and <see cref="ReloadMod"/> can repair it at any time.
        /// </summary>
        private void QuarantineIfExhausted(Mod mod)
        {
            bool budgetTrips = mod.BudgetTripStreak >= MaxBudgetTripsBeforeQuarantine;
            if (mod.Quarantined || (mod.ErrorCount < MaxErrorsBeforeQuarantine && !budgetTrips))
            {
                return;
            }

            lock (_gate)
            {
                // WHY: `mod` comes from the tick snapshot and may be STALE: a repair's ReloadMod can land
                // mid-tick (e.g. from a ModHandlerErrored subscriber) and swap the registry entry. Only
                // the still-live instance may be quarantined — quarantining by id from the old object's
                // error streak would suspend the freshly repaired mod.
                if (!_mods.TryGetValue(mod.Id, out Mod live) || !ReferenceEquals(live, mod))
                {
                    return;
                }

                mod.Quarantined = true;
            }

            lock (_subscriptionGate)
            {
                DeactivateSubscriptionsLocked(mod);
                PublishSubscriptionSnapshotLocked();
            }

            if (budgetTrips)
            {
                _log?.Warn(
                    $"[LuaCsModRuntime] Mod '{mod.Id}' quarantined after {mod.BudgetTripStreak} budget trips in a row " +
                    "(instruction, time or memory budget): dispatch suspended, mod kept loaded, and its stored " +
                    "package suspended so it does not start again on the next start; reload it or start it by " +
                    "hand to clear both.");
                AppendLog(mod.Id, LuaLogLevel.Error,
                    $"mod quarantined after {mod.BudgetTripStreak} budget trips in a row; dispatch suspended " +
                    "and the mod is not started again on the next start until it is reloaded or started by hand.");
            }
            else
            {
                _log?.Warn(
                    $"[LuaCsModRuntime] Mod '{mod.Id}' quarantined after {mod.ErrorCount} consecutive handler " +
                    "errors: dispatch suspended, mod kept loaded; reload it to clear the quarantine.");

                // WHY: Error, not RuntimeError — the quarantine is a host-side lifecycle event, not a VM
                // exception; the underlying failures were already appended as RuntimeError by the handler
                // error channel.
                AppendLog(mod.Id, LuaLogLevel.Error,
                    $"mod quarantined after {mod.ErrorCount} consecutive handler errors; " +
                    "dispatch suspended until reload.");
            }

            // WHY the actor record is put back after the teardown: the teardown releases the mod's
            // scheduled work through the same kill an unload uses, and that kill also drops the
            // bindings' record of the actor the mod was loaded for. A quarantined mod stays loaded and
            // keeps its slot of that actor's and the world's mod quota, so without the record the
            // actor's disconnect neither listed nor unloaded it, and it stayed loaded for good (M2-24).
            // WHY not have the runtime add its quarantined mods to each disconnect instead: the bindings
            // raise a disconnect only with the mods they hold a record for, so an actor whose one mod
            // was quarantined raised nothing the runtime could act on.
            LuaCsRbxApiBindings.ModActorRecord actorRecord = _rbxApi?.CaptureModActorRecord(mod.Id);
            TeardownModEffects(mod.Id, LuaModTeardownReason.Quarantine);
            if (actorRecord != null && IsLiveQuarantined(mod))
            {
                _rbxApi.RestoreModActorRecord(actorRecord);
            }

            if (budgetTrips && IsLiveQuarantined(mod))
            {
                SuspendStoredPackage(mod.Id);
            }

            RaiseModQuarantined(mod.Id, mod.ErrorCount);
        }

        /// <summary>
        /// Marks the stored package of a mod quarantined for budget trips inactive and
        /// <see cref="LuaModManifest.SuspendedAfterBudgetTrips"/>, so neither a restart nor a world
        /// restore starts it again. A successful load or reload of the mod writes a fresh manifest, which
        /// clears both. Best-effort like every store write of the runtime, and only where the runtime
        /// persists mods at all.
        /// </summary>
        /// <remarks>
        /// WHY on the stored package: the stall repeats wherever the mod starts, and a player who killed a
        /// frozen game met the same freeze on the next start, before any quarantine could happen.
        /// WHY Active = false rather than a flag every start path must learn to read: rehydrate and the
        /// exact world restore already skip an inactive package, and a world package already carries
        /// the flag; the suspension marker only tells the Hub why the mod is off.
        /// </remarks>
        private void SuspendStoredPackage(string modId)
        {
            if (!_autoPersistMods)
            {
                return;
            }

            try
            {
                if (!_sourceStore.TryLoad(modId, out string source, out LuaModManifest manifest)
                    || manifest == null
                    || string.IsNullOrWhiteSpace(source))
                {
                    return;
                }

                manifest.Active = false;
                manifest.SuspendedAfterBudgetTrips = true;
                _sourceStore.Save(modId, source, manifest);
            }
            catch (Exception ex)
            {
                _log?.Error($"[LuaCsModRuntime] Suspending the stored package of mod '{modId}' after budget trips failed: {ex}");
            }
        }

        /// <summary>
        /// True when a hook or timer failed because the guard cut it at its instruction, time or memory
        /// budget, told by the type of the CLR cause the guard attaches (a <see cref="TimeoutException"/>, a
        /// <see cref="LuaStepBudgetException"/> or a memory trip), never by text: a mod that writes a trip
        /// line into error() must not get another mod that called it suspended, and the guard's lines may be
        /// reworded (audit C3-07 gave them the sandbox's prefix).
        /// </summary>
        private static bool IsBudgetTrip(Exception exception)
        {
            for (Exception cause = exception; cause != null; cause = ScriptExecutionErrors.NextCause(cause))
            {
                if (cause is TimeoutException || cause is LuaStepBudgetException)
                {
                    return true;
                }
            }

            return ScriptExecutionErrors.IsMemoryBudgetTrip(exception);
        }

        /// <summary>
        /// True when a scheduler thread was killed at its resume budget: the scheduler codes it
        /// BUDGET_EXCEEDED from the handle's typed trip and keeps the guard's line. An instance quota
        /// refusal is coded BUDGET_EXCEEDED too, but is an ordinary error: it costs no time.
        /// </summary>
        private static bool IsBudgetTrip(RbxError error)
        {
            return error != null
                   && error.Code == RbxErrorCode.BudgetExceeded
                   && ContainsBudgetTripLine(error.Message);
        }

        private static bool ContainsBudgetTripLine(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return false;
            }

            for (int index = 0; index < BudgetTripLines.Length; index++)
            {
                if (message.IndexOf(BudgetTripLines[index], StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>The markers the execution guard, the coroutine handle and the sandbox put into a budget trip's line.</summary>
        private static readonly string[] BudgetTripLines =
        {
            "EXCEEDED_HARD_LIMIT_STEPS",
            "EXCEEDED_RESUME_STEP_BUDGET",
            "EXCEEDED_COROUTINE_STEP_BUDGET",
            "EXCEEDED_LIFETIME_STEP_BUDGET",
            "EXCEEDED_PATTERN_STEP_BUDGET",
            LuaCsExecutionGuard.MemoryBudgetTripMarker,
            "resume exceeded",
            "Lua exceeded"
        };

        /// <summary>
        /// True while <paramref name="mod"/> is still the loaded instance of its id and quarantined: a
        /// teardown listener may have unloaded or replaced it in the meantime.
        /// </summary>
        private bool IsLiveQuarantined(Mod mod)
        {
            lock (_gate)
            {
                return mod.Quarantined
                       && _mods.TryGetValue(mod.Id, out Mod live)
                       && ReferenceEquals(live, mod);
            }
        }

        /// <summary>
        /// Central teardown of one mod instance's runtime side effects, shared by unload, reload
        /// (before the swap; <paramref name="keepState"/> excludes the replacement's fresh defines) and
        /// quarantine entry: clears the mod's logic-slot overrides (fail back to the vanilla formula)
        /// and raises <see cref="ModTearingDown"/> so future subsystems can release the mod's effects
        /// at the same point. Best-effort: a failing slot surface must not break the lifecycle path.
        /// </summary>
        private void TeardownModEffects(string modId, LuaModTeardownReason reason, IScriptState keepState = null)
        {
            if (_logicSlots != null)
            {
                try
                {
                    int cleared = _logicSlots.ClearOwnedBy(modId, keepState);
                    if (cleared > 0)
                    {
                        _log?.Info(
                            $"[LuaCsModRuntime] Cleared {cleared} logic-slot override(s) of mod '{modId}' ({reason}).");
                    }
                }
                catch (Exception ex)
                {
                    _log?.Error($"[LuaCsModRuntime] Clearing logic-slot overrides of '{modId}' failed: {ex}");
                }
            }

            RaiseModTearingDown(modId, reason);
            RunDefaultTeardown(modId, reason);
        }

        /// <summary>
        /// Installs the teardown the composition root wires by default (the factory's release of a mod
        /// run's threads, connections and, on unload, instances). It runs after every
        /// <see cref="ModTearingDown"/> subscriber, so a host that releases the same effects itself
        /// still sees them when its subscriber runs, and nothing is left for this one to release.
        /// </summary>
        internal void SetDefaultTeardown(Action<string, LuaModTeardownReason> teardown)
        {
            _defaultTeardown = teardown;
        }

        private void RunDefaultTeardown(string modId, LuaModTeardownReason reason)
        {
            Action<string, LuaModTeardownReason> teardown = _defaultTeardown;
            if (teardown == null)
            {
                return;
            }

            try
            {
                teardown(modId, reason);
            }
            catch (Exception ex)
            {
                _log?.Error($"[LuaCsModRuntime] The default teardown of mod '{modId}' ({reason}) failed: {ex}");
            }
        }

        /// <summary>
        /// Permanently stops this runtime without changing persisted mod manifests. World-session
        /// replacement uses this after the active facade has moved to the staged runtime, so outgoing
        /// hooks, timers, connections, scheduler work, and VM states cannot survive the swap while the
        /// restored package's Active flags remain exact.
        /// </summary>
        internal int ShutdownWithoutPersistence()
        {
            List<Mod> outgoing;
            lock (_gate)
            {
                if (_shutdown)
                {
                    return 0;
                }

                _shutdown = true;
                outgoing = new List<Mod>(_modsInLoadOrder);
                _mods.Clear();
                _modsInLoadOrder.Clear();
                _pendingActorModReleases.Clear();
                lock (_subscriptionGate)
                {
                    for (int index = 0; index < outgoing.Count; index++)
                    {
                        DeactivateSubscriptionsLocked(outgoing[index]);
                    }

                    PublishSubscriptionSnapshotLocked();
                }
            }

            for (int index = 0; index < outgoing.Count; index++)
            {
                TeardownModEffects(outgoing[index].Id, LuaModTeardownReason.Unload);
            }

            if (_rbxApi != null)
            {
                _rbxApi.Scheduler.ThreadFaulted -= OnSchedulerThreadFaulted;
                _rbxApi.Scheduler.ThreadResumeSucceeded -= OnSchedulerThreadResumeSucceeded;
                _rbxApi.Scheduler.HostFaulted -= OnSchedulerHostFaulted;
                _rbxApi.Registry.RemoveRegistrationAdmission(_instanceQuotaAdmission);
                _rbxApi.Registry.Registered -= OnInstanceRegistered;
                _rbxApi.Registry.Unregistered -= OnInstanceUnregistered;
                _rbxApi.ActorModsDisconnected -= OnActorModsDisconnected;
            }

            if (_logicSlots != null)
            {
                _logicSlots.OverrideFailed -= OnLogicSlotOverrideFailed;
                _logicSlots.SetInvocationScope(null, null);
            }

            _defaultTeardown = null;
            ModEventEmitted = null;
            ModSourceLoaded = null;
            ModSourceUnloaded = null;
            ModQuarantined = null;
            ModTearingDown = null;
            ModHandlerErrored = null;
            ModReportEmitted = null;
            return outgoing.Count;
        }

        private void ThrowIfShutdown()
        {
            if (_shutdown)
            {
                throw new ObjectDisposedException(
                    nameof(LuaCsModRuntime),
                    "The Lua runtime belongs to a replaced world session.");
            }
        }

        /// <summary>
        /// Queues the mods <see cref="LuaCsRbxApiBindings.ActorModsDisconnected"/> reports for an actor
        /// that has just disconnected (M2-24) and unloads them at once, unless mod code is running, in
        /// which case they are unloaded at the next point where none is
        /// (<see cref="ReleaseDisconnectedActorMods"/>). Each is unloaded only while it is still loaded
        /// for that actor, and its stored package keeps its active flag, so a rehydrate after the actor
        /// rejoins starts it again as it was saved.
        /// </summary>
        private void OnActorModsDisconnected(string actorId, IReadOnlyList<string> modIds)
        {
            if (string.IsNullOrEmpty(actorId) || modIds == null || modIds.Count == 0)
            {
                return;
            }

            // WHY copied: the list is the bindings' own, and a deferred release reads it after the
            // bindings have moved on.
            string[] released = new string[modIds.Count];
            for (int index = 0; index < released.Length; index++)
            {
                released[index] = modIds[index];
            }

            lock (_gate)
            {
                if (_shutdown)
                {
                    return;
                }

                // WHY: a mod whose build is in progress is listed because its load already recorded its
                // actor; its chunk was just killed with the actor's other threads, and the build turns
                // this record into the reason it failed instead of a bare cancellation.
                if (_buildDepthByModId.Count != 0)
                {
                    for (int index = 0; index < released.Length; index++)
                    {
                        string modId = Normalize(released[index]);
                        if (_buildDepthByModId.ContainsKey(modId))
                        {
                            _actorDisconnectedDuringBuild[modId] = actorId;
                        }
                    }
                }

                _pendingActorModReleases.Enqueue(new KeyValuePair<string, string[]>(actorId, released));
            }

            ReleaseDisconnectedActorMods();
        }

        /// <summary>
        /// Unloads every queued mod of a disconnected actor, unless mod code is running or a release is
        /// already in progress. Runs when the disconnect arrives, after each guarded hook/timer call and
        /// each logic-slot formula, and at the start of <see cref="Tick"/>, which picks up a disconnect
        /// reached from a scheduler thread.
        /// </summary>
        /// <remarks>
        /// WHY not while mod code runs: a mod's script can disconnect an actor itself (Player:Kick ends
        /// the connection, and the bridge reports the drop synchronously), possibly its own, from a
        /// hook, a logic-slot formula, or an export another mod's thread called. The unload's teardown
        /// forgets which actor the mod was loaded for, so the rest of that code would resolve to the
        /// host fallback instead of being refused NOT_AUTHORITY, and a thread it scheduled on its way
        /// out would later run as the host. Waiting until the code has returned keeps it refused until
        /// the teardown kills what is left.
        /// WHY one release at a time: an unload's teardown (instance sweeps, host listeners) can
        /// disconnect another actor, and unloading that actor's mods in the middle of the first
        /// teardown would take a second mod down halfway through the first; the loop below releases
        /// it next instead.
        /// </remarks>
        private void ReleaseDisconnectedActorMods()
        {
            lock (_gate)
            {
                if (_releasingActorMods || _pendingActorModReleases.Count == 0 || IsModCodeRunning())
                {
                    return;
                }

                _releasingActorMods = true;
            }

            try
            {
                while (TryDequeueActorModRelease(out KeyValuePair<string, string[]> release))
                {
                    ReleaseModsOfDisconnectedActor(release.Key, release.Value);
                }
            }
            finally
            {
                lock (_gate)
                {
                    _releasingActorMods = false;
                }
            }
        }

        /// <summary>
        /// True while a guarded hook/timer call of this runtime, a formula of its logic slots, or a
        /// scheduler thread (a main chunk, a <c>task.*</c> thread, a signal handler) is executing.
        /// </summary>
        private bool IsModCodeRunning()
        {
            return _guardedCallDepth > 0 || _rbxApi?.SchedulerThreadFactory.CurrentThread != null;
        }

        /// <summary>A formula of this runtime's logic slots starts running (<see cref="IsModCodeRunning"/>).</summary>
        private void EnterLogicSlotFormula()
        {
            _guardedCallDepth++;
        }

        /// <summary>
        /// A formula of this runtime's logic slots returned or failed; the mods of an actor it
        /// disconnected are released once no mod code runs any more.
        /// </summary>
        private void ExitLogicSlotFormula()
        {
            _guardedCallDepth--;
            ReleaseDisconnectedActorMods();
        }

        private bool TryDequeueActorModRelease(out KeyValuePair<string, string[]> release)
        {
            lock (_gate)
            {
                if (_pendingActorModReleases.Count == 0)
                {
                    release = default;
                    return false;
                }

                release = _pendingActorModReleases.Dequeue();
                return true;
            }
        }

        private void ReleaseModsOfDisconnectedActor(string actorId, string[] modIds)
        {
            for (int index = 0; index < modIds.Length; index++)
            {
                string modId = Normalize(modIds[index]);
                if (modId.Length == 0)
                {
                    continue;
                }

                try
                {
                    // WHY unload and not quarantine: the actor is gone, so its mods can never dispatch
                    // again (every resume of theirs is refused NOT_AUTHORITY), while a quarantined mod
                    // stays loaded and keeps its VM state and a slot of the actor's and the world's mod
                    // quota for every player who ever left. WHY the stored package keeps its active
                    // flag, unlike an unload someone asked for: nobody chose to stop the mod, and a
                    // world package saves those flags, so a dormant mark would keep it from starting
                    // with the next world load or a rehydrate after the actor rejoins. Its source,
                    // revisions and store_set data stay; the instances it created are swept like any
                    // unloaded mod's, which loses nothing saved: a world package never holds
                    // mod-owned instances, and the mod builds them again when it next starts.
                    RemoveLoadedMod(modId, actorId);
                }
                catch (Exception ex)
                {
                    _log?.Error(
                        $"[LuaCsModRuntime] Unloading mod '{modId}' of disconnected actor '{actorId}' failed: {ex}");
                }
            }
        }

        /// <summary>
        /// Routes a logic-slot override failure (already reset to vanilla by <see cref="LuaCsLogicSlots"/>)
        /// into the mod's handler-error channel: it charges the owning mod's consecutive-error streak and
        /// is recorded/raised like any hook failure, so <c>diagnostics</c>/auto-repair see WHICH mod's
        /// formula broke instead of a silent revert.
        /// </summary>
        private void OnLogicSlotOverrideFailed(string ownerModId, string slot, string error)
        {
            string modId = Normalize(ownerModId);
            if (modId.Length == 0)
            {
                return;
            }

            int streak;
            lock (_gate)
            {
                if (_mods.TryGetValue(modId, out Mod mod))
                {
                    mod.ErrorCount++;
                    mod.FaultedThisFrame = true;
                    streak = mod.ErrorCount;
                }
                else
                {
                    streak = 1;
                }
            }

            string message = $"logic slot '{slot}' override failed and was reset to vanilla: {error}";
            RecordHandlerError(modId, message, streak);
            RaiseModHandlerErrored(modId, message, streak);
        }

        /// <summary>
        /// Records and reports a contained scheduler fault of a mod, charging the mod's streak once per
        /// frame (see <see cref="CloseModFrame"/>).
        /// </summary>
        private void OnSchedulerThreadFaulted(string ownerModId, RbxError error)
        {
            string modId = Normalize(ownerModId);
            if (modId.Length == 0)
            {
                return;
            }

            int streak;
            lock (_gate)
            {
                if (_mods.TryGetValue(modId, out Mod mod))
                {
                    if (!mod.SchedulerFaultChargedThisFrame)
                    {
                        mod.SchedulerFaultChargedThisFrame = true;
                        mod.ErrorCount++;
                    }

                    // WHY every trip counts, unlike the once-per-frame error streak: a trip is a stall as
                    // long as the resume budget, so a frame of several trips is several stalls, not one
                    // burst of cheap errors.
                    if (IsBudgetTrip(error))
                    {
                        mod.BudgetTripStreak++;
                    }

                    mod.FaultedThisFrame = true;
                    streak = mod.ErrorCount;
                }
                else
                {
                    streak = 1;
                }
            }

            string message = error != null
                ? SingleLineErrorMessage(error)
                : "scheduler thread failed without a structured error";
            RecordHandlerError(modId, message, streak);
            RaiseModHandlerErrored(modId, message, streak);
        }

        /// <summary>
        /// Notes that a scheduler thread of a loaded mod yielded or completed without a fault, so the
        /// frame can reset the mod's streak when it closes clean (see <see cref="CloseModFrame"/>).
        /// </summary>
        private void OnSchedulerThreadResumeSucceeded(string ownerModId, bool completed)
        {
            if (string.IsNullOrEmpty(ownerModId))
            {
                return;
            }

            string modId = Normalize(ownerModId);
            lock (_gate)
            {
                // WHY: while a chunk of this id is being built, its resumes belong to a candidate that
                // is not in _mods yet. On a reload _mods still holds the live instance, and a candidate
                // that runs cleanly but is then refused must not forgive the live instance's streak.
                if (_buildDepthByModId.Count != 0 && _buildDepthByModId.ContainsKey(modId))
                {
                    return;
                }

                if (_mods.TryGetValue(modId, out Mod mod))
                {
                    mod.SchedulerSucceededThisFrame = true;
                }
            }
        }

        /// <summary>
        /// Logs an ownerless scheduler fault (<see cref="ModScheduler.HostFaulted"/>) once per distinct
        /// source, type and message; repeats are dropped and faults past
        /// <see cref="MaxDistinctHostFaultsLogged"/> distinct ones are summarised by one final line.
        /// </summary>
        private void OnSchedulerHostFaulted(string source, Exception exception)
        {
            string description = exception == null
                ? "no exception"
                : exception.GetType().Name + ": " + exception.Message;
            string key = (source ?? "") + "\n" + description;
            bool firstOfItsKind = false;
            lock (_hostFaultGate)
            {
                if (_loggedHostFaults.Contains(key))
                {
                    return;
                }

                if (_loggedHostFaults.Count < MaxDistinctHostFaultsLogged)
                {
                    _loggedHostFaults.Add(key);
                    firstOfItsKind = true;
                }
                else if (_hostFaultOverflowLogged)
                {
                    return;
                }
                else
                {
                    _hostFaultOverflowLogged = true;
                }
            }

            if (firstOfItsKind)
            {
                _log?.Error(
                    $"[LuaCsModRuntime] Host scheduler fault in {source}: {description}. The frame kept "
                    + $"running; this fault is logged once. {exception}");
                return;
            }

            _log?.Error(
                $"[LuaCsModRuntime] More than {MaxDistinctHostFaultsLogged} distinct host scheduler faults; "
                + $"further distinct faults are not logged. Latest: {source}: {description}");
        }

        private int TickTimers(Mod mod, double dt, int alreadyCompletedThisTick)
        {
            int remaining = DefaultMaxEventsDispatchedPerTickGlobal - alreadyCompletedThisTick;
            int dispatched = 0;
            for (int i = 0; i < mod.Timers.Count && !mod.Removed; i++)
            {
                TimerEntry timer = mod.Timers[i];
                timer.DueIn -= dt;
                if (timer.DueIn > 0d || dispatched >= remaining)
                {
                    continue;
                }

                // WHY: One invocation per tick maximum — a long hitch must not burst-fire a timer.
                timer.DueIn = timer.IntervalSeconds;
                InvokeGuarded(mod, timer.Fn);
                dispatched++;
            }

            return dispatched;
        }

        /// <summary>
        /// Dispatches this mod's queued events, honouring both the per-mod cap and the shared global
        /// budget. Returns the number of invocations this mod dispatched; surplus events stay queued
        /// and are carried over to the next tick (no-drop contract).
        /// </summary>
        private int DispatchPendingEvents(Mod mod, int alreadyDispatchedThisTick)
        {
            int globalRemaining = DefaultMaxEventsDispatchedPerTickGlobal - alreadyDispatchedThisTick;
            int limit = Math.Min(DefaultMaxEventsDispatchedPerTick, globalRemaining);
            int dispatched = 0;
            while (dispatched < limit)
            {
                KeyValuePair<string, string> evt;
                object[] handlerSnapshot;
                lock (mod.EventGate)
                {
                    if (mod.Pending.Count == 0 || mod.Removed)
                    {
                        return dispatched;
                    }

                    evt = mod.Pending.Peek();

                    // WHY: Snapshot the handler list under the per-mod gate: a dispatched handler may call hooks_on()
                    // for the same event, mutating mod.Handlers; enumerating the live list would then
                    // throw out of the (unguarded) tick.
                    handlerSnapshot = mod.Handlers.TryGetValue(evt.Key, out List<object> handlers)
                        ? handlers.ToArray()
                        : Array.Empty<object>();

                    // WHY: Dequeue only when the whole handler batch fits so one event is never partially delivered.
                    if (handlerSnapshot.Length > limit - dispatched)
                    {
                        return dispatched;
                    }

                    mod.Pending.Dequeue();
                }

                foreach (object fn in handlerSnapshot)
                {
                    if (mod.Removed)
                    {
                        return dispatched;
                    }

                    InvokeGuarded(mod, fn, evt.Key, evt.Value);
                    dispatched++;
                }
            }

            return dispatched;
        }

        private void InvokeGuarded(Mod mod, object fn, params object[] args)
        {
            // WHY: Push an isolated transaction frame around this call (mirroring the MoonSharp runtime and
            // LuaCsGameToolExecutor) so a transaction opened inside one invocation is discarded with the
            // frame on exit and cannot leak into the next handler/timer/tick — and a nested mods_call runs
            // on its OWN frame instead of corrupting this call's still-open transaction.
            _guardedCallDepth++;
            PushTransactionScope();
            try
            {
                if (_rbxApi == null)
                {
                    _handlerGuard.Invoke(
                        mod.State, fn, CancellationToken.None, args);
                }
                else
                {
                    ActorContext actorContext =
                        _rbxApi.ResolveOwnerActorContext(mod.Id);
                    _rbxApi.Registry.ApplyServerGeneratedMutation(
                        actorContext.ActorId,
                        actorContext.Grants.IsUnrestricted,
                        actorContext.WorldId,
                        "dispatch handler owned by mod '" + mod.Id + "'",
                        () =>
                        {
                            _handlerGuard.Invoke(
                                mod.State, fn, CancellationToken.None, args);
                            return true;
                        });
                }

                // WHY not after a scheduler fault of the same frame: that fault is counted once per
                // frame and only a clean frame forgives it (CloseModFrame). Reset here, it was erased
                // by any healthy hook or timer, so a Heartbeat handler that failed in every frame was
                // never quarantined while its mod also ran a timer (A2-06). A mod with no scheduler
                // fault in the frame keeps the per-call rule: a successful call resets the streak.
                if (!mod.SchedulerFaultChargedThisFrame)
                {
                    mod.ErrorCount = 0;
                    mod.BudgetTripStreak = 0;
                }
            }
            catch (Exception ex)
            {
                // WHY: An allocation-budget trip charges the same consecutive-error streak as any failure: the
                // budget is per call and confirmed against live bytes (see the handlerMaxAllocatedBytes param
                // doc above), so a handler that keeps building an oversized value keeps tripping and is
                // quarantined like any other repeated failure. Classified by TYPE (see IsMemoryBudgetTrip) for
                // the log label only — a mod cannot forge the marker in its own error text to change how it
                // is charged.
                bool memoryTrip = ScriptExecutionErrors.IsMemoryBudgetTrip(ex);
                mod.ErrorCount++;
                mod.FaultedThisFrame = true;
                if (memoryTrip || IsBudgetTrip(ex))
                {
                    mod.BudgetTripStreak++;
                }

                _log?.Error(
                    $"[LuaCsModRuntime] Mod '{mod.Id}' handler failed " +
                    $"({(memoryTrip ? "memory-budget trip" : "error")} {mod.ErrorCount}/{MaxErrorsBeforeQuarantine}): {ex}");

                string message = SingleLineErrorMessage(ex);

                // WHY: Buffer the failure so the agent can poll it next turn, independent of any host-side
                // ModHandlerErrored subscriber.
                RecordHandlerError(mod.Id, message, mod.ErrorCount);

                // WHY: Surface the runtime failure so hosts can drive auto-repair. Fired outside the gate; a
                // throwing subscriber must not derail the tick.
                RaiseModHandlerErrored(mod.Id, message, mod.ErrorCount);
            }
            finally
            {
                PopTransactionScope();
                _guardedCallDepth--;
            }

            ReleaseDisconnectedActorMods();
        }

        /// <summary>
        /// Pushes an isolated world/data transaction frame on the shared gameplay-binding scope for one
        /// guarded run. Best-effort: a throwing scope must not derail the load/tick path.
        /// </summary>
        private void PushTransactionScope()
        {
            if (_transactionScope == null)
            {
                return;
            }

            try
            {
                _transactionScope.PushTransactionScope();
            }
            catch (Exception ex)
            {
                _log?.Error($"[LuaCsModRuntime] ILuaTransactionScope.PushTransactionScope() failed: {ex}");
            }
        }

        /// <summary>
        /// Pops the transaction frame pushed by <see cref="PushTransactionScope"/>, discarding any
        /// unfinished transaction it holds. Best-effort: it runs inside finally blocks on the load/tick
        /// path, so a throwing scope must not derail them.
        /// </summary>
        private void PopTransactionScope()
        {
            if (_transactionScope == null)
            {
                return;
            }

            try
            {
                _transactionScope.PopTransactionScope();
            }
            catch (Exception ex)
            {
                _log?.Error($"[LuaCsModRuntime] ILuaTransactionScope.PopTransactionScope() failed: {ex}");
            }
        }

        // WHY: Per-subscriber isolated raises — a throwing UI/telemetry listener must never make a healthy
        // load/reload/unload/report/error notification look failed; each subscriber runs independently and
        // a subscriber's exception is logged and swallowed, never propagated or allowed to skip the rest.

        private void RaiseModSourceLoaded(string modId, string source, LuaCapabilities caps)
        {
            Action<string, string, LuaCapabilities> handler = ModSourceLoaded;
            if (handler == null)
            {
                return;
            }

            foreach (Action<string, string, LuaCapabilities> subscriber in handler.GetInvocationList())
            {
                try
                {
                    subscriber(modId, source, caps);
                }
                catch (Exception ex)
                {
                    _log?.Error($"[LuaCsModRuntime] [subscriber] ModSourceLoaded handler for '{modId}' threw: {ex}");
                }
            }
        }

        private void RaiseModSourceUnloaded(string modId, string source, LuaCapabilities caps)
        {
            Action<string, string, LuaCapabilities> handler = ModSourceUnloaded;
            if (handler == null)
            {
                return;
            }

            foreach (Action<string, string, LuaCapabilities> subscriber in handler.GetInvocationList())
            {
                try
                {
                    subscriber(modId, source, caps);
                }
                catch (Exception ex)
                {
                    _log?.Error($"[LuaCsModRuntime] [subscriber] ModSourceUnloaded handler for '{modId}' threw: {ex}");
                }
            }
        }

        private void RaiseModQuarantined(string modId, int errorCount)
        {
            Action<string, int> handler = ModQuarantined;
            if (handler == null)
            {
                return;
            }

            foreach (Action<string, int> subscriber in handler.GetInvocationList())
            {
                try
                {
                    subscriber(modId, errorCount);
                }
                catch (Exception ex)
                {
                    _log?.Error($"[LuaCsModRuntime] [subscriber] ModQuarantined handler for '{modId}' threw: {ex}");
                }
            }
        }

        private void RaiseModTearingDown(string modId, LuaModTeardownReason reason)
        {
            Action<string, LuaModTeardownReason> handler = ModTearingDown;
            if (handler == null)
            {
                return;
            }

            foreach (Action<string, LuaModTeardownReason> subscriber in handler.GetInvocationList())
            {
                try
                {
                    subscriber(modId, reason);
                }
                catch (Exception ex)
                {
                    _log?.Error($"[LuaCsModRuntime] [subscriber] ModTearingDown handler for '{modId}' threw: {ex}");
                }
            }
        }

        private void RaiseModHandlerErrored(string modId, string message, int consecutiveErrorCount)
        {
            Action<string, string, int> handler = ModHandlerErrored;
            if (handler == null)
            {
                return;
            }

            foreach (Action<string, string, int> subscriber in handler.GetInvocationList())
            {
                try
                {
                    subscriber(modId, message, consecutiveErrorCount);
                }
                catch (Exception ex)
                {
                    _log?.Error($"[LuaCsModRuntime] [subscriber] ModHandlerErrored handler for '{modId}' threw: {ex}");
                }
            }
        }

        private void RaiseModReportEmitted(string modId, string message)
        {
            Action<string, string> handler = ModReportEmitted;
            if (handler == null)
            {
                return;
            }

            foreach (Action<string, string> subscriber in handler.GetInvocationList())
            {
                try
                {
                    subscriber(modId, message);
                }
                catch (Exception ex)
                {
                    _log?.Error($"[LuaCsModRuntime] [subscriber] ModReportEmitted handler for '{modId}' threw: {ex}");
                }
            }
        }

        private void RaiseModEventEmitted(string modId, string evt, string payload)
        {
            Action<string, string, string> handler = ModEventEmitted;
            if (handler == null)
            {
                return;
            }

            foreach (Action<string, string, string> subscriber in handler.GetInvocationList())
            {
                try
                {
                    subscriber(modId, evt, payload);
                }
                catch (Exception ex)
                {
                    _log?.Error($"[LuaCsModRuntime] [subscriber] ModEventEmitted handler for '{modId}' threw: {ex}");
                }
            }
        }

        private void EnsureEventSubscriptionCapacity(Mod mod, string eventName)
        {
            string actorId = NormalizeQuotaActorId(mod.OwnerActorId);
            int existingTotal = 0;
            int existingForActor = 0;
            lock (_subscriptionGate)
            {
                foreach (List<Mod> subscribers in _subscriptions.Values)
                {
                    foreach (Mod subscriber in subscribers)
                    {
                        if (string.Equals(subscriber.Id, mod.Id, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        existingTotal++;
                        if (string.Equals(
                                NormalizeQuotaActorId(subscriber.OwnerActorId), actorId,
                                StringComparison.Ordinal))
                        {
                            existingForActor++;
                        }
                    }
                }
            }

            int candidateCount;
            lock (mod.EventGate)
            {
                candidateCount = mod.Handlers.Count;
            }

            if (existingTotal + candidateCount >= EmergencyMaxEventSubscriptions)
            {
                throw new InvalidOperationException(
                    $"hooks_on: actor '{actorId}' cannot subscribe to event '{eventName}': "
                    + $"emergency event subscriptions ceiling reached ({EmergencyMaxEventSubscriptions}).");
            }

            if (existingForActor + candidateCount >= MaxEventSubscriptionsPerActor)
            {
                throw new InvalidOperationException(
                    $"hooks_on: actor '{actorId}' cannot subscribe to event '{eventName}': "
                    + $"event subscriptions quota reached (limit {MaxEventSubscriptionsPerActor}).");
            }
        }

        private void RegisterModApis(IScriptFunctionRegistry registry, Mod mod)
        {
            registry.Register("mod_id", new Func<string>(() => mod.Id));

            registry.RegisterVarArgs("hooks_on", call =>
            {
                string name = Normalize(call.GetString(0));
                object fn = ReadFunction(call, 1);
                if (name.Length == 0 || fn == null)
                {
                    throw new ArgumentException("hooks_on: event name and function are required.");
                }

                // WHY: LLM-written mods routinely register hooks_on("tick"/"update"/"frame", fn) expecting a
                // per-frame callback, but hooks_on only receives NAMED events and nothing emits those —
                // the handler would sit dead forever. Route the intuitive spelling to the timer
                // machinery at the shortest allowed interval instead.
                if (name is "tick" or "update" or "frame")
                {
                    if (mod.Timers.Count >= DefaultMaxTimersPerMod)
                    {
                        throw new InvalidOperationException(
                            $"hooks_on('{name}'): timer limit reached ({DefaultMaxTimersPerMod}).");
                    }

                    mod.Timers.Add(new TimerEntry
                    {
                        IntervalSeconds = MinTimerIntervalSeconds,
                        DueIn = MinTimerIntervalSeconds,
                        Fn = fn
                    });
                    return ScriptCallResult.Return(true);
                }

                bool firstSubscription;
                lock (mod.EventGate)
                {
                    if (mod.HandlerCount >= DefaultMaxHandlersPerMod)
                    {
                        throw new InvalidOperationException(
                            $"hooks_on: handler limit reached ({DefaultMaxHandlersPerMod}).");
                    }

                    firstSubscription = !mod.Handlers.ContainsKey(name);
                }

                if (firstSubscription)
                {
                    EnsureEventSubscriptionCapacity(mod, name);
                }

                lock (mod.EventGate)
                {
                    if (mod.HandlerCount >= DefaultMaxHandlersPerMod)
                    {
                        throw new InvalidOperationException(
                            $"hooks_on: handler limit reached ({DefaultMaxHandlersPerMod}).");
                    }

                    if (!mod.Handlers.TryGetValue(name, out List<object> list))
                    {
                        list = new List<object>();
                        mod.Handlers[name] = list;
                        firstSubscription = true;
                    }
                    else
                    {
                        firstSubscription = false;
                    }

                    list.Add(fn);
                    mod.HandlerCount++;
                }

                if (firstSubscription)
                {
                    RegisterSubscription(mod, name);
                }

                return ScriptCallResult.Return(true);
            });

            registry.RegisterVarArgs("hooks_every", call =>
            {
                double seconds = call.GetNumber(0);
                object fn = ReadFunction(call, 1);
                if (fn == null)
                {
                    throw new ArgumentException("hooks_every: a function is required as the second argument.");
                }

                // WHY: A timer fires at most once per Tick (DueIn resets to the full interval, never
                // catches up), so a sub-frame interval behaves as a per-frame loop (RunService.Heartbeat
                // equivalent), not per-instruction spam — so a small/zero/negative/NaN/infinite interval is
                // clamped to 0 ("every frame") instead of rejected, and mods scale motion by time_delta()
                // for frame-rate-independent movement.
                if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0d)
                {
                    seconds = 0d;
                }

                if (mod.Timers.Count >= DefaultMaxTimersPerMod)
                {
                    throw new InvalidOperationException(
                        $"hooks_every: timer limit reached ({DefaultMaxTimersPerMod}).");
                }

                mod.Timers.Add(new TimerEntry { IntervalSeconds = seconds, DueIn = seconds, Fn = fn });
                return ScriptCallResult.Return(true);
            });

            registry.Register("events_emit", new Func<string, string, bool>((evt, payload) =>
            {
                string name = Normalize(evt);
                if (name.Length == 0)
                {
                    throw new ArgumentException("events_emit: event name is required.");
                }

                EmitFromMod(mod, name, payload ?? "");
                return true;
            }));

            registry.RegisterVarArgs("mods_export", call =>
            {
                string exportName = Normalize(call.GetString(0));
                if (exportName.Length == 0)
                {
                    throw new ArgumentException("mods_export: name is required.");
                }

                object value = call.GetArgument(1);
                lock (_gate)
                {
                    if (!mod.Exports.ContainsKey(exportName) && mod.Exports.Count >= DefaultMaxExportsPerMod)
                    {
                        throw new InvalidOperationException(
                            $"mods_export: export limit reached ({DefaultMaxExportsPerMod}).");
                    }

                    mod.Exports[exportName] = value;
                }

                return ScriptCallResult.Return(new object[] { null });
            });

            registry.RegisterVarArgs("mods_get", call =>
            {
                string targetId = call.GetString(0);
                string name = call.GetString(1);
                object export = FindExport(targetId, name, out Mod _);
                if (_marshaller.GetKind(export) == ScriptValueKind.Function)
                {
                    throw new ArgumentException(
                        $"mods_get: '{Normalize(name)}' of mod '{Normalize(targetId)}' is a function - use mods_call.");
                }

                // WHY: Marshal by value: cross-mod reads copy plain data only (no functions/closures/live
                // refs), so no mod can mutate another's state behind its back — the multiplayer-
                // determinism rule.
                return ScriptCallResult.Return(
                    _marshaller.FromPortable(_marshaller.ToPortable(export, CrossModTableDepth)));
            });

            registry.RegisterVarArgs("mods_call", call =>
            {
                string targetId = call.GetString(0);
                string name = call.GetString(1);
                object export = FindExport(targetId, name, out Mod target);
                if (_marshaller.GetKind(export) != ScriptValueKind.Function)
                {
                    throw new ArgumentException(
                        $"mods_call: '{Normalize(name)}' of mod '{Normalize(targetId)}' is not a function - use mods_get.");
                }

                if (_crossCallDepth >= MaxCrossCallDepth)
                {
                    throw new InvalidOperationException(
                        $"mods_call: cross-mod call depth limit reached ({MaxCrossCallDepth}) - break the cycle.");
                }

                int extra = Math.Max(0, call.ArgumentCount - 2);
                object[] marshalled = new object[extra];
                for (int i = 0; i < extra; i++)
                {
                    marshalled[i] = _marshaller.FromPortable(
                        _marshaller.ToPortable(call.GetArgument(i + 2), CrossModTableDepth));
                }

                // WHY the caller's token: every host function that calls back into mod code passes on the
                // token it was called with (see LuaCsCoroutineHandle.ForeignContextTrip). With None, a
                // kill of the calling thread left the export running to the end of its own budget.
                // The price: a caller token that can be cancelled (every hook, timer and scheduler
                // thread) makes the guard link it into a new CancellationTokenSource, about 96 bytes
                // per call; only an uncancellable caller runs on the guard's pooled source.
                CancellationToken callerToken = CallerToken(call);
                IScriptExecutionGuard exportGuard = ResolveExportGuard();
                _crossCallDepth++;

                // WHY: The callee runs on a DIFFERENT state but shares this runtime's single world binding
                // instance, so push an isolated transaction frame: the callee's coreai_world_begin/commit
                // operate on their own frame and cannot flush or clear the caller's still-open transaction
                // (the buffer-corruption bug); popped in finally so a transaction the callee leaks is
                // discarded instead of bleeding into the caller.
                PushTransactionScope();
                try
                {
                    object[] results;
                    if (_rbxApi == null)
                    {
                        results = exportGuard.Invoke(
                            target.State, export, callerToken, marshalled);
                    }
                    else
                    {
                        ActorContext targetActor =
                            _rbxApi.ResolveOwnerActorContext(target.Id);
                        results = _rbxApi.Registry.ApplyServerGeneratedMutation(
                            targetActor.ActorId,
                            targetActor.Grants.IsUnrestricted,
                            targetActor.WorldId,
                            "invoke export owned by mod '" + target.Id + "'",
                            () => exportGuard.Invoke(
                                target.State, export, callerToken, marshalled));
                    }

                    object first = results.Length > 0 ? results[0] : null;

                    return ScriptCallResult.Return(
                        _marshaller.FromPortable(_marshaller.ToPortable(first, CrossModTableDepth)));
                }
                finally
                {
                    PopTransactionScope();
                    _crossCallDepth--;
                }
            });

            registry.Register("mods_list_exports", new Func<string, List<string>>(targetId =>
            {
                lock (_gate)
                {
                    if (!_mods.TryGetValue(Normalize(targetId), out Mod target))
                    {
                        throw new ArgumentException($"mods_list_exports: mod '{Normalize(targetId)}' is not loaded.");
                    }

                    return new List<string>(target.Exports.Keys);
                }
            }));

            if (_store != null)
            {
                registry.Register("store_set", new Action<string, string>((key, value) =>
                {
                    string k = Normalize(key);
                    if (k.Length == 0)
                    {
                        throw new ArgumentException("store_set: key is required.");
                    }

                    _store.Set(mod.Id, k, value);
                }));

                registry.Register("store_get", new Func<string, string>(key =>
                    _store.Get(mod.Id, Normalize(key)) ?? ""));
            }

            registry.Register("report", new Action<string>(message =>
            {
                string text = message ?? "";

                // WHY: Buffered regardless of LogReports: the flag only gates the live event/log spam, not
                // this bounded history, so a Hub logs view can still show a muted mod's history.
                RecordReport(mod.Id, text);

                if (!mod.LogReports)
                {
                    return;
                }

                RaiseModReportEmitted(mod.Id, text);
            }));

            // WHY: print() inside a mod behaves like report(): same event pipeline, same LogReports mute,
            // same report buffer. Overrides the basic library's print on this mod's environment.
            registry.RegisterVarArgs("print", call =>
            {
                string[] parts = new string[call.ArgumentCount];
                for (int i = 0; i < call.ArgumentCount; i++)
                {
                    parts[i] = call.DescribeArgument(i);
                }

                string text = string.Join("\t", parts);
                RecordReport(mod.Id, text);

                if (mod.LogReports)
                {
                    RaiseModReportEmitted(mod.Id, text);
                }

                return ScriptCallResult.Empty;
            });
        }

        /// <summary>Resolves a mod's export or throws a descriptive error naming what is missing.</summary>
        private object FindExport(string targetId, string name, out Mod target)
        {
            string modId = Normalize(targetId);
            string exportName = Normalize(name);
            lock (_gate)
            {
                if (!_mods.TryGetValue(modId, out target))
                {
                    throw new ArgumentException($"mod '{modId}' is not loaded.");
                }

                // WHY: Quarantine must suspend ALL FOUR dispatch surfaces — handlers, timers, queued
                // events/logic_define overrides, AND cross-mod exports. Without this guard a quarantined
                // mod's export stays invokable, and a throwing export would surface in the CALLER's
                // InvokeGuarded catch and mis-charge the caller's streak instead of the quarantined target's.
                if (target.Quarantined)
                {
                    throw new InvalidOperationException(
                        $"mod '{modId}' is quarantined - its exports are suspended; reload it to clear the quarantine.");
                }

                if (!target.Exports.TryGetValue(exportName, out object export))
                {
                    throw new ArgumentException(
                        $"mod '{modId}' has no export '{exportName}' (mods_list_exports lists available names).");
                }

                return export;
            }
        }

        /// <summary>Reads a function-valued argument, or null when the argument is not a function.</summary>
        private static object ReadFunction(ScriptCallContext call, int index)
        {
            return call.GetKind(index) == ScriptValueKind.Function ? call.GetArgument(index) : null;
        }

        /// <summary>
        /// The token the VM called a host function with; <see cref="CancellationToken.None"/> for an
        /// engine whose call context does not carry one.
        /// </summary>
        private static CancellationToken CallerToken(ScriptCallContext call)
        {
            return call is LuaCsScriptCallContext luaCall ? luaCall.CancellationToken : CancellationToken.None;
        }

        /// <summary>
        /// The guard a <c>mods_call</c> export runs under: the handler guard, capped at the per-resume
        /// budget of a signal handler that calls it.
        /// </summary>
        /// <remarks>
        /// WHY capped: the export used to get the whole handler budget (50,000,000 steps, 10 s) whatever
        /// called it, so a Heartbeat handler held to its per-resume budget ran for seconds through one
        /// call of its own export and stalled the frame (A2-10). The handler's own hook checks its wall
        /// clock at its first instruction after the export returns, so a resume now overruns its
        /// wall-clock budget by at most one more budget.
        /// WHY only a signal handler: a pooled signal runner always runs under the composition's live
        /// per-resume budget, so its budget is known here. The remaining budget of any other caller is
        /// not: a task.spawn/defer/delay thread and a mod's main-chunk thread look the same from this
        /// side, though the main chunk resumes under the handler budget and a task thread under the
        /// per-resume one; when a resume started lives in the coroutine handle's private hook; and a
        /// hook or timer call's guard does not expose the steps it has used. Their exports keep the
        /// handler budget.
        /// TODO: cap every export at its caller's remaining budget once the running guard exposes it
        /// (LuaCsCoroutineHandle / LuaCsExecutionGuard) (A2-10).
        /// </remarks>
        private IScriptExecutionGuard ResolveExportGuard()
        {
            if (_rbxApi == null
                || !(_rbxApi.SchedulerThreadFactory.CurrentThread is LuaCsRbxScriptThread { IsSignalRunner: true }))
            {
                return _handlerGuard;
            }

            LuaCsCoroutineBudgetSettings resumeBudget = _rbxApi.CoroutineResumeBudget;
            int timeoutMs = Math.Min(_scriptExecutionBudget.TimeoutMs, resumeBudget.ResumeTimeoutMs);
            long maxSteps = Math.Min(_scriptExecutionBudget.MaxSteps, resumeBudget.BudgetPerResume);
            if (timeoutMs == _scriptExecutionBudget.TimeoutMs && maxSteps == _scriptExecutionBudget.MaxSteps)
            {
                return _handlerGuard;
            }

            // WHY cached by value: ScriptContext:SetTimeout changes the live budget, and a guard per call
            // would add two more allocations to every export call of every signal handler, on top of
            // the linked token source the caller's token already costs (see mods_call).
            if (_signalHandlerExportGuard == null
                || _signalHandlerExportTimeoutMs != timeoutMs
                || _signalHandlerExportMaxSteps != maxSteps)
            {
                _signalHandlerExportGuard = _engine.CreateGuard(new ExecutionBudget(
                    timeoutMs, maxSteps, _scriptExecutionBudget.MaxAllocatedBytes));
                _signalHandlerExportTimeoutMs = timeoutMs;
                _signalHandlerExportMaxSteps = maxSteps;
            }

            return _signalHandlerExportGuard;
        }

        private void EmitFromMod(Mod sender, string evt, string payload)
        {
            RouteEvent(sender, evt, payload);
            RaiseModEventEmitted(sender.Id, evt, payload);
        }

        private void RouteEvent(Mod sender, string evt, string payload)
        {
            Dictionary<string, Mod[]> subscriptions = Volatile.Read(ref _subscriptionSnapshot);
            Mod[] subscriberSnapshot = subscriptions.TryGetValue(evt, out Mod[] subscribers)
                ? subscribers
                : Array.Empty<Mod>();

            int touched = 0;
            for (int i = 0; i < subscriberSnapshot.Length; i++)
            {
                Mod subscriber = subscriberSnapshot[i];
                if (ReferenceEquals(subscriber, sender))
                {
                    continue;
                }

                touched++;
                Enqueue(subscriber, evt, payload);
            }

            Interlocked.Add(ref _subscriptionEntriesTouched, touched);
        }

        private void Enqueue(Mod mod, string evt, string payload)
        {
            lock (mod.EventGate)
            {
                if (!mod.AcceptsEvents)
                {
                    return;
                }

                if (mod.Pending.Count >= DefaultMaxQueuedEventsPerMod)
                {
                    mod.Pending.Dequeue();
                }

                mod.Pending.Enqueue(new KeyValuePair<string, string>(evt, payload));
            }
        }

        private void RegisterSubscription(Mod mod, string evt)
        {
            lock (_subscriptionGate)
            {
                if (!mod.AcceptsEvents)
                {
                    return;
                }

                AddSubscriptionLocked(mod, evt);
                PublishSubscriptionSnapshotLocked();
            }
        }

        private void PublishSubscriptionSnapshotLocked()
        {
            Dictionary<string, Mod[]> snapshot =
                new Dictionary<string, Mod[]>(_subscriptions.Count, StringComparer.Ordinal);
            foreach (KeyValuePair<string, List<Mod>> subscription in _subscriptions)
            {
                snapshot.Add(subscription.Key, subscription.Value.ToArray());
            }

            Volatile.Write(ref _subscriptionSnapshot, snapshot);
        }

        private void ActivateSubscriptionsLocked(Mod mod)
        {
            mod.AcceptsEvents = true;
            lock (mod.EventGate)
            {
                foreach (string evt in mod.Handlers.Keys)
                {
                    AddSubscriptionLocked(mod, evt);
                }
            }
        }

        private void AddSubscriptionLocked(Mod mod, string evt)
        {
            if (!mod.RegisteredEvents.Add(evt))
            {
                return;
            }

            if (!_subscriptions.TryGetValue(evt, out List<Mod> subscribers))
            {
                subscribers = new List<Mod>();
                _subscriptions[evt] = subscribers;
            }

            int insertIndex = subscribers.Count;
            while (insertIndex > 0 && subscribers[insertIndex - 1].LoadOrder > mod.LoadOrder)
            {
                insertIndex--;
            }

            subscribers.Insert(insertIndex, mod);
        }

        private void DeactivateSubscriptionsLocked(Mod mod)
        {
            mod.AcceptsEvents = false;
            foreach (string evt in mod.RegisteredEvents)
            {
                if (!_subscriptions.TryGetValue(evt, out List<Mod> subscribers))
                {
                    continue;
                }

                subscribers.Remove(mod);
                if (subscribers.Count == 0)
                {
                    _subscriptions.Remove(evt);
                }
            }

            mod.RegisteredEvents.Clear();
        }

        /// <summary>
        /// Unloads the mod (if loaded) <em>and</em> deletes its persisted package, so it does not
        /// rehydrate on a future start. Returns true when either an unload or a delete occurred.
        /// </summary>
        internal bool ForgetMod(string id)
        {
            string modId = Normalize(id);
            bool wasLoaded = UnloadMod(modId);

            try
            {
                _sourceStore.Delete(modId);
            }
            catch (Exception ex)
            {
                _log?.Error($"[LuaCsModRuntime] Source store Delete('{modId}') failed: {ex}");
                return wasLoaded;
            }

            return wasLoaded || modId.Length > 0;
        }

        /// <summary>
        /// The order both restore paths start stored mods in: mods without a recorded
        /// <see cref="LuaModManifest.LoadOrder"/> (0 or below: written before the field existed, seeded on
        /// install, or loaded while the store could not be listed; or above
        /// <see cref="LuaModManifest.MaximumLoadOrder"/>, which no store records) first, by ordinal id,
        /// which is the order every restore used before the load order was persisted; then mods with one,
        /// ascending. An active unordered mod had already started at startup before any ordered mod was
        /// first loaded, so an ordered mod may depend on it, never the other way round. Equal load orders
        /// fall back to the ordinal id so the order is total, and a nil manifest sorts first so the exact
        /// restore refuses it before any mod starts.
        /// </summary>
        internal static int CompareRestoreOrder(LuaModManifest left, LuaModManifest right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left == null)
            {
                return -1;
            }

            if (right == null)
            {
                return 1;
            }

            bool leftOrdered = LuaModManifest.IsRecordedLoadOrder(left.LoadOrder);
            bool rightOrdered = LuaModManifest.IsRecordedLoadOrder(right.LoadOrder);
            if (leftOrdered != rightOrdered)
            {
                return leftOrdered ? 1 : -1;
            }

            if (leftOrdered && left.LoadOrder != right.LoadOrder)
            {
                return left.LoadOrder < right.LoadOrder ? -1 : 1;
            }

            return string.CompareOrdinal(left.Id, right.Id);
        }

        /// <summary>
        /// Loads every stored mod whose manifest is <see cref="LuaModManifest.Active"/> and not already
        /// loaded, in <see cref="CompareRestoreOrder"/> order (the order the mods were loaded, so a mod
        /// can use at init what an earlier one made). Each mod's persisted capability request is
        /// intersected with <paramref name="hostGrant"/> and (unless <paramref name="allowFull"/>)
        /// stripped of <see cref="LuaCapabilities.Full"/>, so a persisted or shared mod can never
        /// auto-acquire full reflection. Loads run in independent try/catch blocks so one bad package does
        /// not abort the rest. Returns the count successfully loaded.
        /// </summary>
        internal int RehydrateFromStore(LuaCapabilities hostGrant, bool allowFull = false)
        {
            IReadOnlyList<LuaModManifest> manifests;
            try
            {
                manifests = _sourceStore.List() ?? Array.Empty<LuaModManifest>();
            }
            catch (Exception ex)
            {
                _log?.Error($"[LuaCsModRuntime] Source store List() failed during rehydrate: {ex}");
                return 0;
            }

            List<LuaModManifest> ordered = new(manifests);
            ordered.Sort(CompareRestoreOrder);
            int loaded = 0;
            foreach (LuaModManifest manifest in ordered)
            {
                if (manifest == null || !manifest.Active)
                {
                    continue;
                }

                string modId = Normalize(manifest.Id);
                if (modId.Length == 0 || IsLoaded(modId))
                {
                    continue;
                }

                try
                {
                    if (!_sourceStore.TryLoad(modId, out string source, out LuaModManifest stored) ||
                        string.IsNullOrWhiteSpace(source))
                    {
                        _log?.Warn($"[LuaCsModRuntime] Rehydrate skipped '{modId}': no source in store.");
                        continue;
                    }

                    string capsText = stored != null ? stored.Capabilities : manifest.Capabilities;
                    LuaCapabilities effectiveCaps = ApplyHostGrant(ParseCaps(capsText), hostGrant, allowFull);
                    string ownerActorId = stored?.OwnerActorId?.Trim() ?? manifest.OwnerActorId?.Trim() ?? "";

                    // WHY: Load with the masked runtime tier but do NOT re-persist: the stored manifest already
                    // holds the mod's declared capabilities. Overwriting it with the masked tier would
                    // permanently strip Full from the store, so a later allowFull rehydrate could not
                    // restore it.
                    LoadModInternal(
                        modId, source, ownerActorId, effectiveCaps, false,
                        ownerActorId.Length == 0);
                    loaded++;
                }
                catch (Exception ex)
                {
                    // WHY: quiet skip, not an error — a persisted mod may target a capability tier this
                    // composition does not grant (e.g. a Full-tier demo's mod rehydrating under Read).
                    // One short warning per mod, no stack trace; the mod stays unloaded until the store
                    // entry is fixed or forgotten, and the remaining mods keep loading.
                    _log?.Warn($"[LuaCsModRuntime] Rehydrate skipped mod '{modId}': {ex.Message}");
                }
            }

            return loaded;
        }

        /// <summary>
        /// Starts every active package from an exact staged source set, in
        /// <see cref="CompareRestoreOrder"/> order, and fails the whole candidate on the first missing or
        /// invalid chunk. World-session replacement calls this only after the restored tree exists and
        /// before the candidate is exposed; dormant packages are retained in the source store but never
        /// acquire a VM state.
        /// </summary>
        internal int RehydrateExactOrThrow(LuaCapabilities hostGrant, bool allowFull = false)
        {
            ThrowIfShutdown();
            IReadOnlyList<LuaModManifest> manifests = _sourceStore.List()
                ?? throw new InvalidOperationException(
                    "The staged mod source store returned a nil manifest list.");
            List<LuaModManifest> ordered = new(manifests);
            ordered.Sort(CompareRestoreOrder);
            int loaded = 0;
            for (int index = 0; index < ordered.Count; index++)
            {
                LuaModManifest manifest = ordered[index]
                    ?? throw new InvalidOperationException(
                        "The staged mod source store contains a nil manifest.");
                if (!manifest.Active)
                {
                    continue;
                }

                string modId = Normalize(manifest.Id);
                if (modId.Length == 0)
                {
                    throw new InvalidOperationException(
                        "The staged mod source store contains an active package with an empty id.");
                }

                if (!_sourceStore.TryLoad(
                        modId, out string source, out LuaModManifest stored)
                    || stored == null
                    || string.IsNullOrWhiteSpace(source))
                {
                    throw new InvalidOperationException(
                        "Active staged mod '" + modId + "' has no exact source/manifest.");
                }

                string storedId = Normalize(stored.Id);
                if (!string.Equals(storedId, modId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Staged mod key '" + modId + "' contains manifest id '" + storedId + "'.");
                }

                LuaCapabilities effectiveCaps = ApplyHostGrant(
                    ParseCaps(stored.Capabilities), hostGrant, allowFull);
                string ownerActorId = stored.OwnerActorId?.Trim() ?? "";
                LoadModInternal(
                    modId,
                    source,
                    ownerActorId,
                    effectiveCaps,
                    false,
                    ownerActorId.Length == 0);
                loaded++;
            }

            return loaded;
        }

        /// <summary>
        /// Returns a shareable JSON bundle <c>{ "manifest": {...}, "source": "..." }</c> for a loaded or
        /// stored mod, or null when neither holds the id.
        /// </summary>
        internal string ExportMod(string id)
        {
            string modId = Normalize(id);
            string source = null;
            LuaModManifest manifest = null;

            lock (_gate)
            {
                if (_mods.TryGetValue(modId, out Mod mod))
                {
                    source = mod.Source;
                    manifest = BuildManifest(modId, mod.Source ?? "", mod.Caps, true, null, mod.OwnerActorId);
                }
            }

            if (source == null)
            {
                try
                {
                    if (_sourceStore.TryLoad(modId, out string storedSource, out LuaModManifest storedManifest))
                    {
                        source = storedSource;
                        manifest = storedManifest ??
                                   BuildManifest(modId, storedSource ?? "", LuaCapabilities.None, false);
                    }
                }
                catch (Exception ex)
                {
                    _log?.Error($"[LuaCsModRuntime] Source store TryLoad('{modId}') failed during export: {ex}");
                }
            }

            if (source == null)
            {
                return null;
            }

            try
            {
                return JsonConvert.SerializeObject(new LuaModBundle { Manifest = manifest, Source = source });
            }
            catch (Exception ex)
            {
                _log?.Error($"[LuaCsModRuntime] Export of mod '{modId}' failed: {ex}");
                return null;
            }
        }

        /// <summary>
        /// Parses an <see cref="ExportMod"/> bundle and loads (plus persists) it. The bundle's capability
        /// request is intersected with <paramref name="hostGrant"/> and (unless
        /// <paramref name="allowFull"/>) stripped of <see cref="LuaCapabilities.Full"/>, so an imported
        /// mod can never auto-acquire full reflection. Returns false on malformed input, a missing/blank
        /// id or source, or a load failure.
        /// </summary>
        internal bool ImportMod(string bundleJson, LuaCapabilities hostGrant, bool allowFull = false)
        {
            return ImportModInternal(bundleJson, "", hostGrant, allowFull, true);
        }

        /// <inheritdoc />
        internal bool ImportModForActor(string bundleJson, string ownerActorId, LuaCapabilities hostGrant,
            bool allowFull = false)
        {
            if (string.IsNullOrWhiteSpace(ownerActorId))
            {
                throw new ArgumentException("Owner actor id is required.", nameof(ownerActorId));
            }

            return ImportModInternal(
                bundleJson, ownerActorId.Trim(), hostGrant, allowFull, false);
        }

        /// <param name="demandSourceKept">
        /// Null, or the check of a caller that requires the store to keep a newly imported mod's source
        /// (see <see cref="LoadModInternal"/>); its refusal is thrown to the caller, not reported as false.
        /// </param>
        private bool ImportModInternal(string bundleJson, string ownerActorId, LuaCapabilities hostGrant,
            bool allowFull, bool ownerHasHostAuthority, Action<string> demandSourceKept = null)
        {
            if (string.IsNullOrWhiteSpace(bundleJson))
            {
                return false;
            }

            LuaModBundle bundle;
            try
            {
                bundle = JsonConvert.DeserializeObject<LuaModBundle>(bundleJson);
            }
            catch (Exception ex)
            {
                _log?.Error($"[LuaCsModRuntime] ImportMod failed to parse bundle: {ex}");
                return false;
            }

            if (bundle == null || string.IsNullOrWhiteSpace(bundle.Source))
            {
                _log?.Warn("[LuaCsModRuntime] ImportMod rejected: missing source.");
                return false;
            }

            string modId = Normalize(bundle.Manifest != null ? bundle.Manifest.Id : "");
            if (modId.Length == 0)
            {
                _log?.Warn("[LuaCsModRuntime] ImportMod rejected: missing/blank mod id.");
                return false;
            }

            string capsText = bundle.Manifest != null ? bundle.Manifest.Capabilities : "";
            LuaCapabilities effectiveCaps = ApplyHostGrant(ParseCaps(capsText), hostGrant, allowFull);
            Exception sourceRefusal = null;
            Action<string> keptCheck = demandSourceKept == null
                ? null
                : new Action<string>(keptId =>
                {
                    try
                    {
                        demandSourceKept(keptId);
                    }
                    catch (Exception ex)
                    {
                        sourceRefusal = ex;
                        throw;
                    }
                });

            try
            {
                if (IsLoaded(modId))
                {
                    // WHY: Reloading an already-loaded mod only swaps its SOURCE; it deliberately KEEPS the
                    // mod's current capability tier (ReloadMod reuses existing.Caps), so an import can never
                    // escalate a live mod's privileges from an untrusted bundle header. To change a loaded
                    // mod's tier the host must unload/forget it first, then re-import under the desired grant.
                    ReloadMod(modId, bundle.Source);
                }
                else if (keptCheck != null && _autoPersistMods)
                {
                    // WHY persisted by the load itself here: the check that the store kept the source
                    // runs inside the load's commit, and the load persists exactly effectiveCaps.
                    LoadModInternal(
                        modId, bundle.Source, ownerActorId, effectiveCaps, true,
                        ownerHasHostAuthority, keptCheck);
                }
                else
                {
                    // WHY: Persist the HOST-MASKED effective capabilities, NOT the bundle's declared request
                    // — an untrusted bundle can DECLARE Full, and if the store recorded that, a later
                    // restart's allowFull=true rehydrate would re-grant Full to a mod imported WITHOUT it.
                    // The store must never hold more than the host granted here; re-import under
                    // allowFull=true to raise it later.
                    LoadModInternal(
                        modId, bundle.Source, ownerActorId, effectiveCaps, false,
                        ownerHasHostAuthority);
                    PersistMod(modId, bundle.Source, effectiveCaps, ownerActorId, true);
                }

                return true;
            }
            catch (Exception ex)
            {
                // WHY rethrown: the caller asked to hear why a source was not kept (a limit refusal
                // names the mod to forget), which a bare false would hide.
                if (sourceRefusal != null && ReferenceEquals(ex, sourceRefusal))
                {
                    throw;
                }

                _log?.Error($"[LuaCsModRuntime] ImportMod of '{modId}' failed: {ex}");
                return false;
            }
        }

        /// <summary>JSON shape of an export/import bundle: the manifest plus the raw Lua source.</summary>
        private sealed class LuaModBundle
        {
            [JsonProperty("manifest")]
            public LuaModManifest Manifest;

            [JsonProperty("source")]
            public string Source = "";
        }

        /// <summary>
        /// Best-effort persist of a mod's source + manifest; a store failure is logged, never thrown.
        /// <paramref name="firstLoad"/> stamps the next <see cref="LuaModManifest.LoadOrder"/> (the mod
        /// was not loaded before: a new mod, or one loaded again after an unload); a reload keeps the
        /// stored one, and is stamped like a first load only when the store holds no manifest for it.
        /// </summary>
        private void PersistMod(string modId, string source, LuaCapabilities caps, string ownerActorId,
            bool firstLoad)
        {
            if (!_autoPersistMods)
            {
                return;
            }

            try
            {
                // WHY: Carry over the seed lineage (Origin/Seeded*) from the existing manifest so a runtime
                // load/reload never blanks it — otherwise a bundled sample loaded at runtime would look
                // user-authored to the next seed pass and stop auto-updating.
                LuaModManifest existing = null;
                try
                {
                    _sourceStore.TryLoad(modId, out _, out existing);
                }
                catch
                {
                    existing = null;
                }

                LuaModManifest manifest = BuildManifest(modId, source ?? "", caps, true, existing, ownerActorId);
                // WHY no lock: two first loads racing may read the same maximum and share a value;
                // CompareRestoreOrder breaks such a tie by id.
                manifest.LoadOrder = firstLoad || existing == null
                    ? NextStoredLoadOrder()
                    : existing.LoadOrder;
                _sourceStore.Save(modId, source, manifest);
            }
            catch (Exception ex)
            {
                _log?.Error($"[LuaCsModRuntime] Source store Save('{modId}') failed: {ex}");
            }
        }

        /// <summary>
        /// <see cref="LuaModManifest.NextLoadOrder"/> over this runtime's source store (read from the
        /// store, dormant packages included, not from the loaded mods or the per-session counter). A store
        /// that cannot list (it throws, or answers the unreadable-listing marker) yields 0: the mod is
        /// stored without a recorded order and restores with the unordered mods, before every ordered
        /// one, until a later first load (after an unload) stamps it again.
        /// </summary>
        /// <remarks>
        /// WHY 0 and not the 1 an empty listing gives: 1 is an order the store never recorded, and
        /// passed off as one it put the new mod ahead of every older ordered mod without a word.
        /// </remarks>
        private long NextStoredLoadOrder()
        {
            try
            {
                return LuaModManifest.NextLoadOrder(_sourceStore);
            }
            catch (Exception ex)
            {
                _log?.Error("[LuaCsModRuntime] The source store could not list its mods while stamping a load "
                            + $"order, so the mod is stored without one: {ex}");
                return 0;
            }
        }

        /// <summary>
        /// Builds a manifest for the given mod from its <c>--[[@coreai ... ]]</c> header (name, description,
        /// category, tags, author) with its capability set rendered as a string. <see cref="LuaModManifest.Version"/>
        /// prefers the header's authored version (e.g. <c>1.2.0</c>) so the Mods card shows the real version;
        /// it falls back to the revision count from the version store only when the header omits a version.
        /// The seed lineage (<see cref="LuaModManifest.Origin"/>/<see cref="LuaModManifest.SeededVersion"/>/
        /// <see cref="LuaModManifest.SeededHash"/>) is carried over from <paramref name="existing"/> when present.
        /// </summary>
        private LuaModManifest BuildManifest(string id, string source, LuaCapabilities caps, bool active,
            LuaModManifest existing = null, string ownerActorId = null)
        {
            LuaModHeader header = LuaModHeader.Parse(source ?? "", id);
            string version = string.IsNullOrWhiteSpace(header.Version) ? CurrentVersionString(id) : header.Version;
            return new LuaModManifest
            {
                Id = id,
                Name = string.IsNullOrWhiteSpace(header.Name) ? id : header.Name,
                Description = header.Description ?? "",
                Category = header.Category ?? "",
                Tags = header.Tags ?? "",
                Author = header.Author ?? "",
                OwnerActorId = ownerActorId ?? existing?.OwnerActorId ?? "",
                Capabilities = caps.ToString(),
                Active = active,
                Version = version,
                Origin = existing?.Origin ?? "",
                SeededVersion = existing?.SeededVersion ?? "",
                SeededHash = existing?.SeededHash ?? "",
                UpdateAvailable = existing?.UpdateAvailable ?? false
            };
        }

        /// <summary>
        /// Renders the mod's current version as the count of recorded revisions (e.g. "3" after three distinct
        /// edits). Blank when the version store holds no history for the mod. Best-effort: a store failure
        /// yields a blank version rather than throwing.
        /// </summary>
        private string CurrentVersionString(string id)
        {
            try
            {
                if (_versionStore.TryGetSnapshot(VersionKey(Normalize(id)), out LuaScriptVersionRecord snapshot) &&
                    snapshot != null && snapshot.History.Count > 0)
                {
                    return snapshot.History.Count.ToString();
                }
            }
            catch (Exception ex)
            {
                _log?.Error($"[LuaCsModRuntime] Version store version lookup for '{id}' failed: {ex}");
            }

            return "";
        }

        /// <summary>
        /// Intersects a mod's requested capabilities with the host grant and, unless
        /// <paramref name="allowFull"/>, clears <see cref="LuaCapabilities.Full"/>. Persisted and shared
        /// mods route through here so they can never escalate beyond what the host currently allows.
        /// </summary>
        private static LuaCapabilities ApplyHostGrant(LuaCapabilities requested, LuaCapabilities hostGrant,
            bool allowFull)
        {
            LuaCapabilities effective = requested & hostGrant;
            if (!allowFull)
            {
                effective &= ~LuaCapabilities.Full;
            }

            return effective;
        }

        /// <summary>
        /// Tolerantly parses a persisted capability string into <see cref="LuaCapabilities"/>. An empty
        /// or unparsable value yields <see cref="LuaCapabilities.None"/> (fail closed) and is logged, so
        /// a corrupt manifest grants no capabilities rather than defaulting open.
        /// </summary>
        private LuaCapabilities ParseCaps(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return LuaCapabilities.None;
            }

            if (Enum.TryParse(text.Trim(), true, out LuaCapabilities parsed))
            {
                return parsed;
            }

            _log?.Warn($"[LuaCsModRuntime] Unrecognized capability string '{text}'; defaulting to None.");
            return LuaCapabilities.None;
        }

        /// <summary>
        /// Appends a Tick-time handler failure to the bounded recent-errors buffer, dropping the oldest
        /// entry when full.
        /// </summary>
        private void RecordHandlerError(string modId, string message, int consecutiveCount)
        {
            LuaModHandlerError entry = new()
            {
                ModId = modId,
                Error = message ?? "",
                ConsecutiveCount = consecutiveCount,
                AtUtc = DateTime.UtcNow
            };

            lock (_gate)
            {
                if (_mods.TryGetValue(Normalize(modId), out Mod mod))
                {
                    entry.OwnerActorId = mod.OwnerActorId;
                }

                if (_recentHandlerErrors.Count >= MaxRetainedHandlerErrors)
                {
                    _recentHandlerErrors.Dequeue();
                }

                _recentHandlerErrors.Enqueue(entry);
            }

            AppendLog(modId, LuaLogLevel.RuntimeError, message);
        }

        /// <summary>
        /// Best-effort append to the optional mod-log sink. Same isolation policy as the event raises:
        /// a throwing log consumer must never break a mod's report call, a load, or the tick loop.
        /// </summary>
        private void AppendLog(string modId, LuaLogLevel level, string message)
        {
            if (_logService == null)
            {
                return;
            }

            try
            {
                _logService.Append(new LuaLogEntry
                {
                    ModId = modId ?? "",
                    Level = level,
                    Message = message ?? ""
                });
            }
            catch
            {
                // WHY: A logging sink must never throw out of a mod's print/report or error path and
                // break gameplay.
            }
        }

        /// <summary>Collapses an exception message onto one line, falling back to the type name.</summary>
        private static string SingleLineErrorMessage(Exception ex)
        {
            string message = (ex.Message ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return message.Length == 0 ? ex.GetType().Name : message;
        }

        /// <summary>
        /// Returns a snapshot of recent Tick-time handler failures (oldest first), capped at
        /// <see cref="MaxRetainedHandlerErrors"/>. Pass <paramref name="modId"/> to filter to a single
        /// mod.
        /// </summary>
        internal IReadOnlyList<LuaModHandlerError> GetRecentHandlerErrors(string modId = null)
        {
            string filter = modId == null ? null : Normalize(modId);
            List<LuaModHandlerError> result = new();
            lock (_gate)
            {
                foreach (LuaModHandlerError entry in _recentHandlerErrors)
                {
                    if (filter == null || filter.Length == 0 ||
                        string.Equals(entry.ModId, filter, StringComparison.Ordinal))
                    {
                        result.Add(entry);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Clears the recent Tick-time handler-error buffer (optionally only entries for one mod).
        /// Returns the number of entries removed.
        /// </summary>
        internal int ClearRecentHandlerErrors(string modId = null)
        {
            string filter = modId == null ? null : Normalize(modId);
            lock (_gate)
            {
                if (filter == null || filter.Length == 0)
                {
                    int cleared = _recentHandlerErrors.Count;
                    _recentHandlerErrors.Clear();
                    return cleared;
                }

                int before = _recentHandlerErrors.Count;
                LuaModHandlerError[] kept = new LuaModHandlerError[before];
                int keptCount = 0;
                foreach (LuaModHandlerError entry in _recentHandlerErrors)
                {
                    if (!string.Equals(entry.ModId, filter, StringComparison.Ordinal))
                    {
                        kept[keptCount++] = entry;
                    }
                }

                _recentHandlerErrors.Clear();
                for (int i = 0; i < keptCount; i++)
                {
                    _recentHandlerErrors.Enqueue(kept[i]);
                }

                return before - keptCount;
            }
        }

        /// <summary>
        /// Appends a <c>report()</c>/<c>print()</c> emission to the bounded recent-reports buffer,
        /// dropping the oldest entry when full. Called regardless of the mod's <c>LogReports</c> flag.
        /// </summary>
        private void RecordReport(string modId, string message)
        {
            LuaModReport entry = new()
            {
                ModId = modId,
                Message = message ?? "",
                AtUtc = DateTime.UtcNow
            };

            lock (_gate)
            {
                if (_recentReports.Count >= MaxRetainedReports)
                {
                    _recentReports.Dequeue();
                }

                _recentReports.Enqueue(entry);
            }

            AppendLog(modId, LuaLogLevel.Print, message);
        }

        /// <summary>
        /// Returns a snapshot of recent <c>report()</c>/<c>print()</c> emissions (oldest first), capped
        /// at <see cref="MaxRetainedReports"/>, independent of each mod's <c>LogReports</c> flag. Pass
        /// <paramref name="modId"/> to filter to a single mod.
        /// </summary>
        internal IReadOnlyList<LuaModReport> GetRecentReports(string modId = null)
        {
            string filter = modId == null ? null : Normalize(modId);
            List<LuaModReport> result = new();
            lock (_gate)
            {
                foreach (LuaModReport entry in _recentReports)
                {
                    if (filter == null || filter.Length == 0 ||
                        string.Equals(entry.ModId, filter, StringComparison.Ordinal))
                    {
                        result.Add(entry);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Clears the recent reports buffer (optionally only entries for one mod). Returns the number of
        /// entries removed.
        /// </summary>
        internal int ClearRecentReports(string modId = null)
        {
            string filter = modId == null ? null : Normalize(modId);
            lock (_gate)
            {
                if (filter == null || filter.Length == 0)
                {
                    int cleared = _recentReports.Count;
                    _recentReports.Clear();
                    return cleared;
                }

                int before = _recentReports.Count;
                LuaModReport[] kept = new LuaModReport[before];
                int keptCount = 0;
                foreach (LuaModReport entry in _recentReports)
                {
                    if (!string.Equals(entry.ModId, filter, StringComparison.Ordinal))
                    {
                        kept[keptCount++] = entry;
                    }
                }

                _recentReports.Clear();
                for (int i = 0; i < keptCount; i++)
                {
                    _recentReports.Enqueue(kept[i]);
                }

                return before - keptCount;
            }
        }

        private static string Normalize(string value)
        {
            return (value ?? "").Trim();
        }
    }
}
