using System;
using System.Collections.Generic;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>
    /// Ownership ledger for the <see cref="RbxScriptConnection"/> handles a mod opens through
    /// <c>signal:Connect</c>/<c>:Once</c> (RunService.Heartbeat, UserInputService.InputBegan, ...),
    /// mirroring <see cref="InstanceRegistry"/>'s <c>OwnerModId</c> ledger for spawned instances.
    /// The Lua Connect binding records each returned connection here against the acting mod; the
    /// composition disconnects them on <c>ModTearingDown</c> so an unloaded/reloaded/quarantined
    /// mod's per-frame handlers stop firing against its torn-down state instead of logging
    /// INSTANCE_DESTROYED one frame later.
    /// <para>
    /// Connections are keyed by (modId, GENERATION), not modId alone. A reload builds and RUNS the
    /// replacement chunk BEFORE the outgoing instance is torn down, so the new chunk's top-level
    /// <c>Connect</c> calls are already tracked when <c>ModTearingDown(Reload)</c> fires. Keying by
    /// generation lets the reload teardown disconnect ONLY the previous chunk's connections and keep
    /// the freshly created ones live — mirroring the logic-slot <c>keepState</c> exclusion. Each load
    /// stamps its context with the next generation via <see cref="BeginGeneration"/> before its chunk
    /// runs, so the acting generation is captured from the same context that carries the mod id.
    /// </para>
    /// WHY: single-threaded, main-thread-only by invariant — Lua executes on the main thread, so the
    /// dictionaries are unsynchronized like the instance ledger.
    /// </summary>
    public sealed class ModConnectionRegistry
    {
        private readonly struct Entry
        {
            public Entry(int generation, RbxScriptConnection connection)
            {
                Generation = generation;
                Connection = connection;
            }

            public int Generation { get; }

            public RbxScriptConnection Connection { get; }
        }

        private readonly Dictionary<string, List<Entry>> _byMod =
            new Dictionary<string, List<Entry>>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _currentGeneration =
            new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>
        /// Advances a mod's generation counter and returns the new value. Called once per load/reload
        /// BEFORE the chunk runs (when the mod's Rbx context is built), so every <c>Connect</c> the
        /// chunk makes is tracked under this generation and the following reload teardown can tell the
        /// new chunk's connections from the outgoing chunk's. Returns 0 for a null/empty mod id (the
        /// one-off / editor surface, which is never tracked).
        /// </summary>
        public int BeginGeneration(string modId)
        {
            if (string.IsNullOrEmpty(modId))
            {
                return 0;
            }

            int next = (_currentGeneration.TryGetValue(modId, out int current) ? current : 0) + 1;
            _currentGeneration[modId] = next;
            return next;
        }

        /// <summary>
        /// Records a connection against its owning mod + generation. No-op when <paramref name="modId"/>
        /// is null/empty (one-off / editor execution has nothing to tear down). Prunes already-dead
        /// entries of the mod on the way in so <c>:Once</c> auto-disconnects and manual
        /// <c>conn:Disconnect()</c> calls cannot accumulate unbounded between teardowns.
        /// </summary>
        public void Track(string modId, int generation, RbxScriptConnection connection)
        {
            if (string.IsNullOrEmpty(modId) || connection == null)
            {
                return;
            }

            if (!_byMod.TryGetValue(modId, out List<Entry> list))
            {
                list = new List<Entry>();
                _byMod[modId] = list;
            }

            PruneDead(list);
            list.Add(new Entry(generation, connection));
        }

        /// <summary>Snapshot of the live connections currently owned by a mod (empty when none).</summary>
        public IReadOnlyList<RbxScriptConnection> GetOwnedBy(string modId)
        {
            var result = new List<RbxScriptConnection>();
            if (modId != null && _byMod.TryGetValue(modId, out List<Entry> list))
            {
                foreach (Entry entry in list)
                {
                    if (entry.Connection != null && entry.Connection.Connected)
                    {
                        result.Add(entry.Connection);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Disconnects the connections owned by a mod, returning the count disconnected.
        /// <see cref="RbxScriptConnection.Disconnect"/> is idempotent, so a connection the mod already
        /// dropped (or a signal already torn down) is a safe no-op.
        /// <para>
        /// <paramref name="keepCurrentGeneration"/> is true on RELOAD: the replacement chunk has already
        /// run and registered its connections under the mod's current generation, so those are KEPT and
        /// only the previous generation(s) are disconnected. It is false on UNLOAD and QUARANTINE (no new
        /// chunk exists) — every connection is disconnected and the mod's entry dropped.
        /// </para>
        /// </summary>
        public int DisconnectOwnedBy(string modId, bool keepCurrentGeneration = false)
        {
            if (modId == null || !_byMod.TryGetValue(modId, out List<Entry> list))
            {
                return 0;
            }

            int liveGeneration = keepCurrentGeneration
                                 && _currentGeneration.TryGetValue(modId, out int current)
                ? current
                : int.MinValue;

            List<Entry> survivors = keepCurrentGeneration ? new List<Entry>() : null;
            int count = 0;
            foreach (Entry entry in list)
            {
                RbxScriptConnection connection = entry.Connection;
                if (keepCurrentGeneration && entry.Generation == liveGeneration)
                {
                    // WHY: the reload's fresh chunk owns these — keep them (and drop any already dead).
                    if (connection != null && connection.Connected)
                    {
                        survivors.Add(entry);
                    }

                    continue;
                }

                if (connection != null && connection.Connected)
                {
                    connection.Disconnect();
                    count++;
                }
            }

            if (survivors != null && survivors.Count > 0)
            {
                _byMod[modId] = survivors;
            }
            else
            {
                _byMod.Remove(modId);
            }

            return count;
        }

        private static void PruneDead(List<Entry> list)
        {
            for (int i = list.Count - 1; i >= 0; i--)
            {
                RbxScriptConnection connection = list[i].Connection;
                if (connection == null || !connection.Connected)
                {
                    list.RemoveAt(i);
                }
            }
        }
    }
}
