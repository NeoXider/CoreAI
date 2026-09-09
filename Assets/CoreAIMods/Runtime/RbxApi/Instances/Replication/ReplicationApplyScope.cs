using System;

namespace CoreAI.Mods.Rbx.Instances.Replication
{
    /// <summary>
    /// The window in which a replica registry applies what the server sent. Setters called inside it
    /// are the server's writes, not the client's: revisions stay the server's and nothing is marked
    /// diverged. Disposed in strict LIFO order, the same shape as <see cref="MutationEnvelopeScope"/>.
    /// </summary>
    public sealed class ReplicationApplyScope : IDisposable
    {
        private InstanceRegistry _registry;

        internal ReplicationApplyScope(InstanceRegistry registry, ReplicationApplyScope previous)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            Previous = previous;
        }

        internal ReplicationApplyScope Previous { get; }

        /// <inheritdoc />
        public void Dispose()
        {
            InstanceRegistry registry = _registry;
            if (registry == null)
            {
                return;
            }

            // WHY the registry is forgotten only after it accepted the end: an out-of-order dispose
            // is refused, and a scope that already forgot its registry could never be closed in the
            // right order afterwards - the replica would stay "applying" for good.
            registry.EndReplicationApplyScope(this);
            _registry = null;
        }
    }
}
