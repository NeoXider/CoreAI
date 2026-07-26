using System;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>
    /// Monotonic id allocator with the §3.3 authority-bit partition: two independent counters,
    /// one per identity space, that can never collide because the top bit differs. Ids are never
    /// reused in-session; restore paths advance the counters past deserialized ids so world-file
    /// loads keep stable ids without a remap table (roadmap §2, world file).
    /// </summary>
    public sealed class InstanceIdAllocator
    {
        private readonly object _gate = new();
        private ulong _nextServer = 1UL;
        private ulong _nextLocal = 1UL;

        public InstanceId Next(InstanceIdAuthority authority)
        {
            lock (_gate)
            {
                if (authority == InstanceIdAuthority.Server)
                {
                    if (_nextServer >= InstanceId.AuthorityBit)
                    {
                        throw new InvalidOperationException("Server-space InstanceId counter exhausted.");
                    }

                    return new InstanceId(_nextServer++);
                }

                if (_nextLocal >= InstanceId.AuthorityBit)
                {
                    throw new InvalidOperationException("Local-space InstanceId counter exhausted.");
                }

                return new InstanceId(InstanceId.AuthorityBit | _nextLocal++);
            }
        }

        /// <summary>
        /// Advances the matching space counter so future allocations never collide with
        /// <paramref name="restoredId"/>. Used by snapshot restore (stable-id contract).
        /// </summary>
        public void EnsureNotBelow(InstanceId restoredId)
        {
            if (!restoredId.IsValid)
            {
                return;
            }

            lock (_gate)
            {
                if (restoredId.IsServerAssigned)
                {
                    ulong candidate = restoredId.Value + 1UL;
                    if (candidate > _nextServer)
                    {
                        _nextServer = candidate;
                    }
                }
                else
                {
                    ulong candidate = (restoredId.Value & ~InstanceId.AuthorityBit) + 1UL;
                    if (candidate > _nextLocal)
                    {
                        _nextLocal = candidate;
                    }
                }
            }
        }
    }
}
