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
    /// WHY: single-threaded, main-thread-only by invariant — Lua executes on the main thread, so the
    /// dictionary is unsynchronized like the instance ledger.
    /// </summary>
    public sealed class ModConnectionRegistry
    {
        private readonly Dictionary<string, List<RbxScriptConnection>> _byMod =
            new Dictionary<string, List<RbxScriptConnection>>(StringComparer.Ordinal);

        /// <summary>
        /// Records a connection against its owning mod. No-op when <paramref name="modId"/> is null or
        /// empty (one-off / editor execution has no mod to attribute or tear down).
        /// </summary>
        public void Track(string modId, RbxScriptConnection connection)
        {
            if (string.IsNullOrEmpty(modId) || connection == null)
            {
                return;
            }

            if (!_byMod.TryGetValue(modId, out List<RbxScriptConnection> list))
            {
                list = new List<RbxScriptConnection>();
                _byMod[modId] = list;
            }

            list.Add(connection);
        }

        /// <summary>Snapshot of the connections currently owned by a mod (empty when none).</summary>
        public IReadOnlyList<RbxScriptConnection> GetOwnedBy(string modId)
        {
            if (modId != null && _byMod.TryGetValue(modId, out List<RbxScriptConnection> list))
            {
                return new List<RbxScriptConnection>(list);
            }

            return Array.Empty<RbxScriptConnection>();
        }

        /// <summary>
        /// Disconnects every connection owned by a mod and drops the mod's entry, returning the count
        /// disconnected. <see cref="RbxScriptConnection.Disconnect"/> is idempotent, so a connection the
        /// mod already dropped (or a signal already torn down) is a safe no-op. Called for EVERY teardown
        /// reason — unlike spawned instances, connections must be released on reload/quarantine too
        /// because the re-run chunk re-Connects fresh handlers.
        /// </summary>
        public int DisconnectOwnedBy(string modId)
        {
            if (modId == null || !_byMod.TryGetValue(modId, out List<RbxScriptConnection> list))
            {
                return 0;
            }

            _byMod.Remove(modId);
            int count = 0;
            foreach (RbxScriptConnection connection in list)
            {
                if (connection == null)
                {
                    continue;
                }

                connection.Disconnect();
                count++;
            }

            return count;
        }
    }
}
