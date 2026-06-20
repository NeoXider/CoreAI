#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
using System;
using System.Collections.Generic;
using CoreAI.Logging;
using CoreAI.Sandbox;
using MoonSharp.Interpreter;

namespace CoreAI.Ai
{
    /// <summary>Snapshot of a loaded mod for diagnostics/UI.</summary>
    public sealed class LuaModInfo
    {
        public string Id = "";
        public LuaCapabilities Capabilities;
        public int HandlerCount;
        public int TimerCount;
        public int ErrorCount;
        public bool LogReports;
        public DateTime LoadedAtUtc;
    }

    /// <summary>
    /// Persistent runtime for long-lived Lua mods (vs one-shot envelope scripts). A mod is a
    /// sandboxed script that registers hooks during load and then lives across frames:
    /// <list type="bullet">
    /// <item><c>hooks_on(event, fn)</c> — handler for named events (from the game or other mods).</item>
    /// <item><c>hooks_every(seconds, fn)</c> — repeating timer driven by <see cref="Tick"/>.</item>
    /// <item><c>events_emit(name, payload)</c> — emits an event to the game (<see cref="ModEventEmitted"/>) and other mods.</item>
    /// <item><c>store_set(key, value)</c> / <c>store_get(key)</c> — persistent per-mod k/v (when an <see cref="ILuaModStore"/> is supplied).</item>
    /// <item><c>mod_id()</c> — the mod's own id.</item>
    /// </list>
    /// The host (Unity layer) calls <see cref="Tick"/> once per frame; every handler call runs
    /// under a per-call instruction/time guard, and a mod failing
    /// <see cref="MaxErrorsBeforeUnload"/> times in a row (the counter resets on a successful
    /// call) is unloaded automatically.
    /// </summary>
    public sealed class LuaModRuntime
    {
        public const int DefaultHandlerTimeoutMs = 100;
        public const long DefaultHandlerMaxSteps = 100_000;
        public const int DefaultMaxMods = 32;
        public const int DefaultMaxHandlersPerMod = 64;
        public const int DefaultMaxTimersPerMod = 16;
        public const int DefaultMaxQueuedEventsPerMod = 256;
        public const int DefaultMaxEventsDispatchedPerTick = 64;

        /// <summary>
        /// Upper bound on handler invocations dispatched across <em>all</em> mods in a single
        /// <see cref="Tick"/>. The per-mod cap (<see cref="DefaultMaxEventsDispatchedPerTick"/>)
        /// alone lets a tick fan out to up to <see cref="DefaultMaxMods"/> mods, i.e.
        /// <c>DefaultMaxMods * DefaultMaxEventsDispatchedPerTick</c> calls, which is a large
        /// main-thread stall. This global budget caps the whole tick; mods not reached once it is
        /// exhausted keep their queued events and are serviced on later ticks (no events are
        /// dropped). Chosen as 4x the per-mod cap: comfortably above the per-mod cap so a single
        /// busy mod is never throttled below its own budget, while still bounding a worst-case
        /// burst across many mods to a few hundred calls per frame.
        /// </summary>
        public const int DefaultMaxEventsDispatchedPerTickGlobal = 256;

        public const int MaxErrorsBeforeUnload = 8;

        /// <summary>Shortest accepted <c>hooks_every</c> interval, so timers cannot degenerate into per-instruction spam.</summary>
        public const double MinTimerIntervalSeconds = 0.05;

        private sealed class TimerEntry
        {
            public double IntervalSeconds;
            public double DueIn;
            public Closure Fn;
        }

        private sealed class Mod
        {
            public string Id = "";
            public Script Script;
            public string Source = "";
            public LuaCapabilities Caps;
            public bool LogReports;
            public readonly Dictionary<string, List<Closure>> Handlers = new(StringComparer.Ordinal);
            public readonly List<TimerEntry> Timers = new();
            public readonly Queue<KeyValuePair<string, string>> Pending = new();
            public int HandlerCount;
            public int ErrorCount;
            public DateTime LoadedAtUtc;
        }

        private readonly object _gate = new();
        private readonly Dictionary<string, Mod> _mods = new(StringComparer.Ordinal);
        private readonly SecureLuaEnvironment _env = new();
        private readonly LuaExecutionGuard _handlerGuard;
        private readonly IGameLuaRuntimeBindings _gameBindings;
        private readonly ILuaModStore _store;
        private readonly ILog _log;
        private readonly List<Mod> _tickScratch = new();

        /// <summary>
        /// Raised when a mod calls <c>events_emit(name, payload)</c>: (modId, eventName, payload).
        /// The Unity layer bridges this to MessagePipe/game systems.
        /// </summary>
        public event Action<string, string, string> ModEventEmitted;

        /// <summary>
        /// Raised after a mod source is successfully loaded or reloaded: (modId, source, caps).
        /// Hosts can use this to persist their selected autoload mod set.
        /// </summary>
        public event Action<string, string, LuaCapabilities> ModSourceLoaded;

        /// <summary>
        /// Raised after a mod is unloaded, including automatic unloads after repeated errors:
        /// (modId, source, caps).
        /// </summary>
        public event Action<string, string, LuaCapabilities> ModSourceUnloaded;

        /// <summary>
        /// Raised when a loaded mod's hook/timer throws while running under <see cref="Tick"/>:
        /// (modId, error, consecutiveErrorCount). Unlike load/reload failures (which propagate
        /// synchronously to the caller), these happen asynchronously on the host thread long after
        /// the mod was accepted. The count is the mod's current consecutive-failure streak (it resets
        /// to zero after any successful call), so a host can bridge this into an auto-repair loop and
        /// debounce on the streak length. The mod is still loaded when this fires; a handler may call
        /// <see cref="TryGetModSource"/> to capture the failing source, but must not reload/unload
        /// synchronously; schedule that work instead.
        /// </summary>
        public event Action<string, string, int> ModHandlerErrored;

        /// <summary>
        /// Raised when a loaded mod calls <c>report(message)</c> and report logging is enabled for
        /// that mod: (modId, message). Reports are muted by default so timer mods cannot flood logs.
        /// </summary>
        public event Action<string, string> ModReportEmitted;

        /// <summary>True when the Lua sandbox is available on this platform.</summary>
        public static bool IsSupported => SecureLuaEnvironment.IsSupported;

        /// <param name="gameBindings">
        /// Game API surface granted to mods (typically an aggregator constructed for the desired
        /// <see cref="LuaCapabilities"/> tier); null = mods only get the built-in mod APIs.
        /// </param>
        /// <param name="store">Optional persistent per-mod k/v store backing <c>store_set/get</c>.</param>
        /// <param name="log">Optional logger.</param>
        /// <param name="handlerTimeoutMs">Wall-clock budget per handler/timer call.</param>
        /// <param name="handlerMaxSteps">Instruction budget per handler/timer call.</param>
        public LuaModRuntime(
            IGameLuaRuntimeBindings gameBindings = null,
            ILuaModStore store = null,
            ILog log = null,
            int handlerTimeoutMs = DefaultHandlerTimeoutMs,
            long handlerMaxSteps = DefaultHandlerMaxSteps)
        {
            _gameBindings = gameBindings;
            _store = store;
            _log = log;
            _handlerGuard = new LuaExecutionGuard(handlerTimeoutMs, handlerMaxSteps);
        }

        /// <summary>Snapshot of all loaded mods.</summary>
        public IReadOnlyList<LuaModInfo> ListMods()
        {
            List<LuaModInfo> result = new();
            lock (_gate)
            {
                foreach (Mod mod in _mods.Values)
                {
                    result.Add(new LuaModInfo
                    {
                        Id = mod.Id,
                        Capabilities = mod.Caps,
                        HandlerCount = mod.HandlerCount,
                        TimerCount = mod.Timers.Count,
                        ErrorCount = mod.ErrorCount,
                        LogReports = mod.LogReports,
                        LoadedAtUtc = mod.LoadedAtUtc
                    });
                }
            }

            return result;
        }

        /// <summary>
        /// Returns the Lua source of a loaded mod (the exact chunk passed to
        /// <see cref="LoadMod"/>/<see cref="ReloadMod"/>), so agents and tooling can inspect and
        /// rewrite their own mods. False when no mod with this id is loaded.
        /// </summary>
        public bool TryGetModSource(string id, out string source)
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
        public bool GetModReportLoggingEnabled(string id)
        {
            lock (_gate)
            {
                return _mods.TryGetValue(Normalize(id), out Mod mod) && mod.LogReports;
            }
        }

        /// <summary>Enables or disables <c>report()</c> output for a loaded mod.</summary>
        public bool SetModReportLoggingEnabled(string id, bool enabled)
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

        /// <summary>True when a mod with this id is currently loaded.</summary>
        public bool IsLoaded(string id)
        {
            lock (_gate)
            {
                return _mods.ContainsKey(Normalize(id));
            }
        }

        /// <summary>
        /// Loads a mod: creates a sandboxed script with the game bindings plus mod APIs and runs
        /// the chunk (which registers its hooks). Throws on invalid input, duplicate id, mod-count
        /// limit, or script error — nothing is left registered when the load fails.
        /// </summary>
        public void LoadMod(string id, string luaCode, LuaCapabilities capabilities = LuaCapabilities.All)
        {
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

                if (_mods.Count >= DefaultMaxMods)
                {
                    throw new InvalidOperationException($"Mod limit reached ({DefaultMaxMods}).");
                }
            }

            Mod mod = BuildMod(modId, luaCode, capabilities);

            lock (_gate)
            {
                if (_mods.ContainsKey(modId))
                {
                    throw new InvalidOperationException($"Mod '{modId}' was loaded concurrently.");
                }

                _mods[modId] = mod;
            }

            _log?.Info($"[LuaModRuntime] Mod '{modId}' loaded (caps={capabilities}).");
            ModSourceLoaded?.Invoke(modId, luaCode, capabilities);
        }

        /// <summary>
        /// Creates the sandboxed script with capability-scoped game bindings plus mod APIs and
        /// runs the chunk (hook registration happens there). Errors propagate to the caller and
        /// the mod is never added, so a failed build leaves no handlers behind.
        /// </summary>
        private Mod BuildMod(string modId, string luaCode, LuaCapabilities capabilities)
        {
            Mod mod = new()
            {
                Id = modId,
                Source = luaCode,
                Caps = capabilities,
                LoadedAtUtc = DateTime.UtcNow
            };

            LuaApiRegistry registry = new();
            RegisterGameBindings(registry, capabilities);
            RegisterModApis(registry, mod);

            Script script = _env.CreateScript(registry);
            mod.Script = script;

            // The game bindings (and their world transaction state) are shared with the envelope
            // and tool executors. A load/reload chunk that opens a world transaction and then errors
            // would otherwise leave that shared transaction open, silently buffering later scripts'
            // world commands. Reset before running and abort in the finally so a leaked transaction
            // cannot bleed out of mod loading.
            (_gameBindings as ILuaTransactionScope)?.ResetTransactions();
            try
            {
                _env.RunChunk(script, luaCode);
            }
            finally
            {
                (_gameBindings as ILuaTransactionScope)?.ResetTransactions();
            }

            return mod;
        }

        private void RegisterGameBindings(LuaApiRegistry registry, LuaCapabilities capabilities)
        {
            if (_gameBindings == null || capabilities == LuaCapabilities.None)
            {
                return;
            }

            if (_gameBindings is ICapabilityScopedLuaBindings scoped)
            {
                scoped.RegisterGameplayApis(registry, capabilities);
                return;
            }

            if (capabilities == LuaCapabilities.All)
            {
                _gameBindings.RegisterGameplayApis(registry);
                return;
            }

            // Fail closed: a non-scoped binding set cannot be trimmed to the requested tier, so a
            // restricted mod gets no game APIs at all instead of silently getting everything.
            _log?.Warn(
                $"[LuaModRuntime] Game bindings ({_gameBindings.GetType().Name}) do not implement " +
                $"ICapabilityScopedLuaBindings; mod requested '{capabilities}' - game APIs withheld.");
        }

        /// <summary>Unloads a mod and drops its handlers/timers/queued events.</summary>
        public bool UnloadMod(string id)
        {
            string modId = Normalize(id);
            string source;
            LuaCapabilities caps;
            lock (_gate)
            {
                if (!_mods.TryGetValue(modId, out Mod mod))
                {
                    return false;
                }

                source = mod.Source;
                caps = mod.Caps;
                _mods.Remove(modId);
            }

            _log?.Info($"[LuaModRuntime] Mod '{modId}' unloaded.");
            ModSourceUnloaded?.Invoke(modId, source, caps);
            return true;
        }

        /// <summary>
        /// Replaces a loaded mod with new code, keeping its capability tier. The new chunk is
        /// built and run first; if it fails, the old mod stays loaded and untouched.
        /// </summary>
        public void ReloadMod(string id, string luaCode)
        {
            string modId = Normalize(id);
            if (string.IsNullOrWhiteSpace(luaCode))
            {
                throw new ArgumentException("Mod code is required.", nameof(luaCode));
            }

            LuaCapabilities caps;
            lock (_gate)
            {
                if (!_mods.TryGetValue(modId, out Mod existing))
                {
                    throw new InvalidOperationException($"Mod '{modId}' is not loaded.");
                }

                caps = existing.Caps;
            }

            Mod replacement = BuildMod(modId, luaCode, caps);

            lock (_gate)
            {
                _mods[modId] = replacement;
            }

            _log?.Info($"[LuaModRuntime] Mod '{modId}' reloaded (caps={caps}).");
            ModSourceLoaded?.Invoke(modId, luaCode, caps);
        }

        /// <summary>Queues a game event for delivery to every mod's <c>hooks_on</c> handlers on the next <see cref="Tick"/>.</summary>
        public void EmitEvent(string name, string payload = "")
        {
            string evt = Normalize(name);
            if (evt.Length == 0)
            {
                return;
            }

            lock (_gate)
            {
                foreach (Mod mod in _mods.Values)
                {
                    EnqueueLocked(mod, evt, payload ?? "");
                }
            }
        }

        /// <summary>
        /// Advances timers and dispatches queued events. Call once per frame from the host
        /// (main thread); every handler call is individually instruction/time guarded.
        /// </summary>
        public void Tick(double deltaSeconds)
        {
            if (deltaSeconds < 0d || double.IsNaN(deltaSeconds))
            {
                return;
            }

            lock (_gate)
            {
                if (_mods.Count == 0)
                {
                    return;
                }

                _tickScratch.Clear();
                foreach (Mod mod in _mods.Values)
                {
                    _tickScratch.Add(mod);
                }
            }

            // Running count of handler invocations dispatched across all mods this tick. Timers
            // always run (they are bounded to one fire per timer per tick); only event dispatch is
            // charged against the global budget so a heavy event-fan-out tick cannot stall the
            // main thread. Once the budget is exhausted, the remaining mods keep their queued
            // events untouched and are serviced on later ticks.
            int dispatchedThisTick = 0;
            for (int i = 0; i < _tickScratch.Count; i++)
            {
                Mod mod = _tickScratch[i];
                TickTimers(mod, deltaSeconds);

                if (dispatchedThisTick < DefaultMaxEventsDispatchedPerTickGlobal)
                {
                    dispatchedThisTick += DispatchPendingEvents(mod, dispatchedThisTick);
                }

                if (mod.ErrorCount >= MaxErrorsBeforeUnload)
                {
                    UnloadMod(mod.Id);
                    _log?.Warn(
                        $"[LuaModRuntime] Mod '{mod.Id}' unloaded after {mod.ErrorCount} handler errors.");
                }
            }
        }

        private void TickTimers(Mod mod, double dt)
        {
            for (int i = 0; i < mod.Timers.Count; i++)
            {
                TimerEntry timer = mod.Timers[i];
                timer.DueIn -= dt;
                if (timer.DueIn > 0d)
                {
                    continue;
                }

                // One invocation per tick maximum — a long hitch must not burst-fire a timer.
                timer.DueIn = timer.IntervalSeconds;
                InvokeGuarded(mod, timer.Fn);
            }
        }

        /// <summary>
        /// Dispatches this mod's queued events, honouring both the per-mod cap
        /// (<see cref="DefaultMaxEventsDispatchedPerTick"/>) and the shared global budget
        /// (<see cref="DefaultMaxEventsDispatchedPerTickGlobal"/>). <paramref name="alreadyDispatchedThisTick"/>
        /// is the number of handler invocations other mods already spent this tick, so the
        /// effective limit is the smaller of the per-mod cap and the remaining global budget.
        /// Returns the number of invocations this mod dispatched; surplus events stay queued and
        /// are carried over to the next tick.
        /// </summary>
        private int DispatchPendingEvents(Mod mod, int alreadyDispatchedThisTick)
        {
            int globalRemaining = DefaultMaxEventsDispatchedPerTickGlobal - alreadyDispatchedThisTick;
            int limit = Math.Min(DefaultMaxEventsDispatchedPerTick, globalRemaining);
            int dispatched = 0;
            while (dispatched < limit)
            {
                KeyValuePair<string, string> evt;
                lock (_gate)
                {
                    if (mod.Pending.Count == 0)
                    {
                        return dispatched;
                    }

                    evt = mod.Pending.Dequeue();
                }

                if (!mod.Handlers.TryGetValue(evt.Key, out List<Closure> handlers))
                {
                    continue;
                }

                foreach (Closure fn in handlers)
                {
                    if (dispatched >= limit)
                    {
                        return dispatched;
                    }

                    InvokeGuarded(mod, fn, evt.Key, evt.Value);
                    dispatched++;
                }
            }

            return dispatched;
        }

        private void InvokeGuarded(Mod mod, Closure fn, params object[] args)
        {
            try
            {
                Script owner = fn.OwnerScript;
                DynValue[] dynArgs = new DynValue[args.Length];
                for (int i = 0; i < args.Length; i++)
                {
                    dynArgs[i] = DynValue.FromObject(owner, args[i]);
                }

                _handlerGuard.Execute(owner, DynValue.FromObject(owner, fn), dynArgs);

                // "MaxErrorsBeforeUnload failures in a row": a successful call forgives past
                // errors, so rare sporadic failures over a long lifetime do not unload the mod.
                mod.ErrorCount = 0;
            }
            catch (Exception ex)
            {
                mod.ErrorCount++;
                _log?.Error($"[LuaModRuntime] Mod '{mod.Id}' handler failed ({mod.ErrorCount}): {ex}");

                // Surface the runtime failure so hosts can drive auto-repair. Fired outside the gate
                // (InvokeGuarded never holds it); a throwing subscriber must not derail the tick.
                if (ModHandlerErrored != null)
                {
                    string message = (ex is InterpreterException ie ? ie.Message : ex.Message ?? "")
                        .Replace("\r", " ").Replace("\n", " ").Trim();
                    try
                    {
                        ModHandlerErrored.Invoke(mod.Id, message, mod.ErrorCount);
                    }
                    catch (Exception subscriberEx)
                    {
                        _log?.Error($"[LuaModRuntime] ModHandlerErrored subscriber threw: {subscriberEx}");
                    }
                }
            }
        }

        private void RegisterModApis(LuaApiRegistry registry, Mod mod)
        {
            registry.Register("mod_id", new Func<string>(() => mod.Id));

            registry.Register("hooks_on", new Func<string, Closure, bool>((evt, fn) =>
            {
                string name = Normalize(evt);
                if (name.Length == 0 || fn == null)
                {
                    throw new ArgumentException("hooks_on: event name and function are required.");
                }

                if (mod.HandlerCount >= DefaultMaxHandlersPerMod)
                {
                    throw new InvalidOperationException(
                        $"hooks_on: handler limit reached ({DefaultMaxHandlersPerMod}).");
                }

                if (!mod.Handlers.TryGetValue(name, out List<Closure> list))
                {
                    list = new List<Closure>();
                    mod.Handlers[name] = list;
                }

                list.Add(fn);
                mod.HandlerCount++;
                return true;
            }));

            registry.Register("hooks_every", new Func<double, Closure, bool>((seconds, fn) =>
            {
                if (fn == null || double.IsNaN(seconds) || double.IsInfinity(seconds) ||
                    seconds < MinTimerIntervalSeconds)
                {
                    throw new ArgumentException(
                        $"hooks_every: interval must be >= {MinTimerIntervalSeconds}s and fn required.");
                }

                if (mod.Timers.Count >= DefaultMaxTimersPerMod)
                {
                    throw new InvalidOperationException(
                        $"hooks_every: timer limit reached ({DefaultMaxTimersPerMod}).");
                }

                mod.Timers.Add(new TimerEntry { IntervalSeconds = seconds, DueIn = seconds, Fn = fn });
                return true;
            }));

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
                if (!mod.LogReports)
                {
                    return;
                }

                ModReportEmitted?.Invoke(mod.Id, message ?? "");
            }));
        }

        private void EmitFromMod(Mod sender, string evt, string payload)
        {
            // Deliver to every other mod's queue (no self-delivery: trivial infinite loops).
            lock (_gate)
            {
                foreach (Mod mod in _mods.Values)
                {
                    if (!ReferenceEquals(mod, sender))
                    {
                        EnqueueLocked(mod, evt, payload);
                    }
                }
            }

            ModEventEmitted?.Invoke(sender.Id, evt, payload);
        }

        private void EnqueueLocked(Mod mod, string evt, string payload)
        {
            if (mod.Pending.Count >= DefaultMaxQueuedEventsPerMod)
            {
                // Drop oldest: a stalled mod must not grow its queue without bound.
                mod.Pending.Dequeue();
            }

            mod.Pending.Enqueue(new KeyValuePair<string, string>(evt, payload));
        }

        private static string Normalize(string value)
        {
            return (value ?? "").Trim();
        }
    }
}
#endif
