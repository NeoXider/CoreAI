using System;
using System.Collections.Generic;
using CoreAI.Authority;

namespace CoreAI.Ai.Logging
{
    /// <summary>
    /// Writes each mod's load, reload and unload into its log (<see cref="LifecycleScriptName"/>, level
    /// <see cref="LuaLogLevel.Print"/>), so the console shows the timeline next to the mod's output, and remembers the
    /// log sequence of every mod's latest load: the cursor <c>get_mod_logs since_reload=true</c> starts after, so a
    /// model that just changed a mod sees only what the new code did. Engine-free; thread-safe.
    /// </summary>
    public sealed class LuaModLifecycleLog : IDisposable
    {
        /// <summary><c>ScriptName</c> of the lifecycle lines; mods cannot set a script name, so they cannot forge one.</summary>
        public const string LifecycleScriptName = "coreai:lifecycle";

        /// <summary>Message of a first load.</summary>
        public const string LoadedMessage = "loaded";

        /// <summary>Message of a reload.</summary>
        public const string ReloadedMessage = "reloaded";

        /// <summary>Message of an unload.</summary>
        public const string UnloadedMessage = "unloaded";

        private readonly object _gate = new();
        private readonly ILuaLogService _logs;
        private readonly ILuaModRuntime _runtime;
        private readonly ActorContext _actor;
        private readonly Dictionary<string, long> _lastLoad = new(StringComparer.Ordinal);
        private readonly HashSet<string> _loaded = new(StringComparer.Ordinal);
        private readonly Action<string, string, LuaCapabilities> _onLoaded;
        private readonly Action<string, string, LuaCapabilities> _onUnloaded;
        private bool _disposed;

        /// <summary>Starts writing the lifecycle of the mods <paramref name="runtime"/> loads into <paramref name="logs"/>.</summary>
        public LuaModLifecycleLog(ILuaLogService logs, ILuaModRuntime runtime, ActorContext actor)
        {
            _logs = logs ?? throw new ArgumentNullException(nameof(logs));
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _actor = actor;
            _onLoaded = OnLoaded;
            _onUnloaded = OnUnloaded;
            _runtime.AddModSourceLoadedListener(_actor, _onLoaded);
            _runtime.AddModSourceUnloadedListener(_actor, _onUnloaded);
        }

        /// <summary>The log sequence of the mod's latest load or reload; false when it did not load while watched.</summary>
        public bool TryGetLastLoadSequence(string modId, out long sequence)
        {
            lock (_gate)
            {
                return _lastLoad.TryGetValue((modId ?? "").Trim(), out sequence);
            }
        }

        /// <summary>True for a line this class wrote.</summary>
        public static bool IsLifecycleEntry(LuaLogEntry entry)
        {
            return entry != null && string.Equals(entry.ScriptName, LifecycleScriptName, StringComparison.Ordinal);
        }

        /// <summary>True for the line of a load or a reload.</summary>
        public static bool IsLoadEntry(LuaLogEntry entry)
        {
            return IsLifecycleEntry(entry) &&
                   (string.Equals(entry.Message, LoadedMessage, StringComparison.Ordinal) ||
                    string.Equals(entry.Message, ReloadedMessage, StringComparison.Ordinal));
        }

        /// <summary>
        /// The sequence after which a model sees only what the mod's current code did: the recorded latest load, else
        /// the newest load line still in the mod's buffer, else 0 (everything the buffer holds).
        /// </summary>
        public static long SinceLatestLoad(ILuaLogService logs, LuaModLifecycleLog lifecycle, string modId)
        {
            if (lifecycle != null && lifecycle.TryGetLastLoadSequence(modId, out long recorded))
            {
                return recorded;
            }

            if (logs == null || string.IsNullOrWhiteSpace(modId))
            {
                return 0;
            }

            IReadOnlyList<LuaLogEntry> entries = logs.Query(new LuaLogQuery { ModId = modId.Trim() });
            for (int index = entries.Count - 1; index >= 0; index--)
            {
                if (IsLoadEntry(entries[index]))
                {
                    return entries[index].Sequence;
                }
            }

            return 0;
        }

        /// <summary>The highest sequence the log assigned so far; 0 for an empty log.</summary>
        public static long LatestSequence(ILuaLogService logs)
        {
            if (logs == null)
            {
                return 0;
            }

            IReadOnlyList<LuaLogEntry> newest = logs.Query(new LuaLogQuery { MaxCount = 1 });
            return newest.Count == 0 ? 0 : newest[newest.Count - 1].Sequence;
        }

        /// <summary>Stops listening.</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _runtime.RemoveModSourceLoadedListener(_actor, _onLoaded);
            _runtime.RemoveModSourceUnloadedListener(_actor, _onUnloaded);
        }

        private void OnLoaded(string modId, string source, LuaCapabilities capabilities)
        {
            string id = (modId ?? "").Trim();
            if (id.Length == 0)
            {
                return;
            }

            bool reload;
            lock (_gate)
            {
                reload = !_loaded.Add(id);
            }

            LuaLogEntry entry = Append(id, reload ? ReloadedMessage : LoadedMessage);
            if (entry != null && entry.Sequence > 0)
            {
                lock (_gate)
                {
                    _lastLoad[id] = entry.Sequence;
                }
            }
        }

        private void OnUnloaded(string modId, string source, LuaCapabilities capabilities)
        {
            string id = (modId ?? "").Trim();
            if (id.Length == 0)
            {
                return;
            }

            lock (_gate)
            {
                _loaded.Remove(id);
            }

            Append(id, UnloadedMessage);
        }

        private LuaLogEntry Append(string modId, string message)
        {
            LuaLogEntry entry = new()
            {
                ModId = modId,
                Level = LuaLogLevel.Print,
                Message = message,
                ScriptName = LifecycleScriptName
            };

            try
            {
                _logs.Append(entry);
                return entry;
            }
            catch
            {
                // WHY: a logging sink must never throw out of a mod's load path.
                return null;
            }
        }
    }
}
