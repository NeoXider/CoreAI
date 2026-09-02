using System;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>Instance-bound ambient server mutation envelope; disposed in strict LIFO order.</summary>
    public sealed class MutationEnvelopeScope : IDisposable
    {
        private InstanceRegistry _registry;

        internal MutationEnvelopeScope(InstanceRegistry registry, MutationEnvelope envelope,
            bool actorIsUnrestricted, string actorWorldId, MutationEnvelopeScope previous)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            Envelope = envelope;
            ActorIsUnrestricted = actorIsUnrestricted;
            ActorWorldId = actorWorldId ?? "";
            Previous = previous;
        }

        /// <summary>Server-issued mutation envelope active for this scope.</summary>
        public MutationEnvelope Envelope { get; }

        internal bool ActorIsUnrestricted { get; }

        internal string ActorWorldId { get; }

        internal MutationEnvelopeScope Previous { get; }

        /// <inheritdoc />
        public void Dispose()
        {
            InstanceRegistry registry = _registry;
            if (registry == null)
            {
                return;
            }

            _registry = null;
            registry.EndMutationEnvelopeScope(this);
        }
    }

    /// <summary>Actor-level decision for the engine-free access control.</summary>
    public enum WorldAclDecision
    {
        WriteProperty,
        MutateMetadata,
        ReparentSelf,
        AcceptChild,
        Destroy
    }

    /// <summary>Engine-free ACL authorizer promoted from the Lua binding layer.</summary>
    public static class WorldAclAuthorizer
    {
        /// <summary>Refuses an actor that cannot perform the requested operation on the target.</summary>
        public static void Demand(InstanceRegistry registry, string actorId,
            bool isUnrestricted, string actorWorldId, RbxInstance target,
            WorldAclDecision decision, string operation)
        {
            if (registry == null)
            {
                throw new ArgumentNullException(nameof(registry));
            }

            if (string.IsNullOrWhiteSpace(actorId))
            {
                throw RbxError.BadArgument(
                    "actor id is required", "use a trusted ActorContext.ActorId");
            }

            if (!registry.IsWorldAclEnabled)
            {
                return;
            }

            string actor = actorId.Trim();
            if (isUnrestricted)
            {
                if (target != null
                    && registry.TryGetRecord(target.Id, out InstanceRecord hostRecord)
                    && hostRecord.AccessScope == InstanceAccessScope.HostProtected
                    && (decision == WorldAclDecision.Destroy
                        || decision == WorldAclDecision.ReparentSelf))
                {
                    Deny(actor, target, operation,
                        "world-lifetime singletons are HostProtected against destroy and reparent, including for unrestricted actors");
                }

                return;
            }

            if (registry.WorldId.Length > 0
                && !string.Equals(registry.WorldId, actorWorldId ?? "",
                    StringComparison.Ordinal))
            {
                Deny(actor, target, operation,
                    "the actor belongs to world '" + actorWorldId
                    + "', not world '" + registry.WorldId + "'");
            }

            if (target == null || !registry.TryGetRecord(target.Id, out InstanceRecord record))
            {
                Deny(actor, target, operation, "the target has no live access-control record");
                return;
            }

            if (record.AccessScope == InstanceAccessScope.Owned)
            {
                if (record.OwnerActorId != null
                    && string.Equals(record.OwnerActorId, actor, StringComparison.Ordinal))
                {
                    return;
                }

                Deny(actor, target, operation,
                    record.OwnerActorId == null
                        ? "the target is Owned by the host"
                        : "the target is Owned by actor '" + record.OwnerActorId + "'");
            }

            if (record.AccessScope == InstanceAccessScope.SharedWritable)
            {
                if (decision != WorldAclDecision.Destroy)
                {
                    return;
                }
                if (record.OwnerActorId != null
                    && string.Equals(record.OwnerActorId, actor, StringComparison.Ordinal))
                {
                    return;
                }

                Deny(actor, target, operation, "SharedWritable destruction is limited to its owner or the host");
            }

            if (record.AccessScope == InstanceAccessScope.HostProtected)
            {
                if (decision == WorldAclDecision.WriteProperty)
                {
                    return;
                }
                if (decision == WorldAclDecision.AcceptChild
                    && target.ClassName == "Workspace")
                {
                    return;
                }

                Deny(actor, target, operation, "the target is HostProtected for this operation");
            }

            Deny(actor, target, operation, "the target has an unsupported access scope");
        }

        private static void Deny(string actorId, RbxInstance target, string operation, string reason)
        {
            string targetName = target == null
                ? "null"
                : target.ClassName + " '" + target.GetFullName() + "'";
            throw RbxError.BadArgument(
                "actor '" + actorId + "' cannot " + operation + " on "
                + targetName + ": " + reason,
                "use an object owned by actor '" + actorId
                + "' or ask the owner/host to perform the operation");
        }
    }
}
