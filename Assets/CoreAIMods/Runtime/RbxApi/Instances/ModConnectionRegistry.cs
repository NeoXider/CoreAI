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

        /// <summary>
        /// One mod's ledger. Dead entries are pruned when the list reaches <see cref="PruneAt"/>, which
        /// then doubles past the surviving count, so pruning costs amortized O(1) per Track instead of
        /// a full scan on every connect (M1-36, M2-26).
        /// </summary>
        private sealed class ModLedger
        {
            public ModLedger(List<Entry> entries)
            {
                Entries = entries;
            }

            public List<Entry> Entries { get; }

            public int PruneAt { get; set; } = MinPruneThreshold;
        }

        private const int MinPruneThreshold = 16;

        private readonly Dictionary<string, ModLedger> _byMod = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _currentGeneration = new(StringComparer.Ordinal);

        /// <summary>Ledger entries visited by dead-entry pruning (M1-36 regression counter).</summary>
        internal long PruneVisitCount { get; private set; }

        /// <summary>Entries currently held for a mod, dead ones not yet pruned included.</summary>
        internal int TrackedEntryCount(string modId)
        {
            return modId != null && _byMod.TryGetValue(modId, out ModLedger ledger)
                ? ledger.Entries.Count
                : 0;
        }

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
        /// Records a connection against its owning mod + generation and stamps
        /// <see cref="RbxScriptConnection.OwnerModId"/>, which the scheduler uses to attribute handler
        /// failures and signal overload to the mod. No-op when <paramref name="modId"/> is null/empty
        /// (one-off / editor execution has nothing to tear down). Already-dead entries of the mod are
        /// pruned whenever the ledger doubles, so <c>:Once</c> auto-disconnects and manual
        /// <c>conn:Disconnect()</c> calls cannot accumulate unbounded between teardowns.
        /// </summary>
        public void Track(string modId, int generation, RbxScriptConnection connection)
        {
            if (string.IsNullOrEmpty(modId) || connection == null)
            {
                return;
            }

            connection.OwnerModId ??= modId;
            if (!_byMod.TryGetValue(modId, out ModLedger ledger))
            {
                ledger = new ModLedger(new List<Entry>());
                _byMod[modId] = ledger;
            }

            List<Entry> list = ledger.Entries;
            if (list.Count >= ledger.PruneAt)
            {
                PruneDead(list);
                ledger.PruneAt = Math.Max(MinPruneThreshold, list.Count * 2);
            }

            list.Add(new Entry(generation, connection));
        }

        /// <summary>Snapshot of the live connections currently owned by a mod (empty when none).</summary>
        public IReadOnlyList<RbxScriptConnection> GetOwnedBy(string modId)
        {
            List<RbxScriptConnection> result = new();
            if (modId != null && _byMod.TryGetValue(modId, out ModLedger ledger))
            {
                foreach (Entry entry in ledger.Entries)
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
            if (modId == null || !_byMod.TryGetValue(modId, out ModLedger ledger))
            {
                return 0;
            }

            List<Entry> list = ledger.Entries;

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
                _byMod[modId] = new ModLedger(survivors)
                {
                    PruneAt = Math.Max(MinPruneThreshold, survivors.Count * 2)
                };
            }
            else
            {
                _byMod.Remove(modId);
            }

            return count;
        }

        private void PruneDead(List<Entry> list)
        {
            int writeIndex = 0;
            for (int readIndex = 0; readIndex < list.Count; readIndex++)
            {
                Entry entry = list[readIndex];
                if (entry.Connection != null && entry.Connection.Connected)
                {
                    list[writeIndex] = entry;
                    writeIndex++;
                }
            }

            PruneVisitCount += list.Count;
            list.RemoveRange(writeIndex, list.Count - writeIndex);
        }
    }
}
