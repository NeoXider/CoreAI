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
            _env.RunChunk(script, luaCode);
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
                $"ICapabilityScopedLuaBindings; mod requested '{capabilities}' — game APIs withheld.");
        }

        /// <summary>Unloads a mod and drops its handlers/timers/queued events.</summary>
        public bool UnloadMod(string id)
        {
            string modId = Normalize(id);
            lock (_gate)
            {
                if (!_mods.Remove(modId))
                {
                    return false;
                }
            }

            _log?.Info($"[LuaModRuntime] Mod '{modId}' unloaded.");
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

            for (int i = 0; i < _tickScratch.Count; i++)
            {
                Mod mod = _tickScratch[i];
                TickTimers(mod, deltaSeconds);
                DispatchPendingEvents(mod);

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

        private void DispatchPendingEvents(Mod mod)
        {
            int dispatched = 0;
            while (dispatched < DefaultMaxEventsDispatchedPerTick)
            {
                KeyValuePair<string, string> evt;
                lock (_gate)
                {
                    if (mod.Pending.Count == 0)
                    {
                        return;
                    }

                    evt = mod.Pending.Dequeue();
                }

                if (!mod.Handlers.TryGetValue(evt.Key, out List<Closure> handlers))
                {
                    continue;
                }

                foreach (Closure fn in handlers)
                {
                    if (dispatched >= DefaultMaxEventsDispatchedPerTick)
                    {
                        return;
                    }

                    InvokeGuarded(mod, fn, evt.Key, evt.Value);
                    dispatched++;
                }
            }
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