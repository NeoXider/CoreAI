using System;
using CoreAI.Ai;

namespace CoreAI.Authority
{
    /// <summary>
    /// Domain port that resolves the trusted actor acting through an agent role.
    /// </summary>
    public interface IActorIdentityProvider
    {
        /// <summary>Returns the current actor context for the supplied role.</summary>
        ActorContext GetActorContext(string roleId);
    }

    /// <summary>
    /// Extension point for actor identity providers. Subclasses receive no context-issuance authority.
    /// </summary>
    public abstract class ActorIdentityProviderBase : IActorIdentityProvider
    {
        /// <inheritdoc />
        public abstract ActorContext GetActorContext(string roleId);
    }

    /// <summary>
    /// Synthetic identity provider for local hosts and unauthenticated single-player sessions.
    /// </summary>
    public sealed class LocalActorIdentityProvider : ActorIdentityProviderBase
    {
        /// <summary>Durable id shared by local connections and independent of agent role.</summary>
        public const string DefaultActorId = "local";

        private readonly string _actorId;
        private readonly ActorGrantSet _grants;
        private readonly object _issuanceCapability;
        private readonly AgentMemoryScope _memoryScope;
        private readonly string _sessionId;
        private readonly string _worldId;

        /// <summary>
        /// Starts an unprivileged synthetic actor using the reserved local id. Host composition uses
        /// its capability-guarded default instead.
        /// </summary>
        public LocalActorIdentityProvider()
            : this(DefaultActorId)
        {
        }

        /// <summary>Starts an unprivileged synthetic actor with a durable id and a new connection session.</summary>
        public LocalActorIdentityProvider(string actorId)
            : this(actorId, Guid.NewGuid().ToString("N"), "", ActorGrantSet.None, AgentMemoryScope.Empty)
        {
        }

        /// <summary>Creates a synthetic provider with explicit identity, grants, and memory composition.</summary>
        public LocalActorIdentityProvider(
            string actorId,
            string sessionId,
            string worldId,
            ActorGrantSet grants,
            AgentMemoryScope memoryScope)
            : this(null, actorId, sessionId, worldId, grants, memoryScope)
        {
        }

        private LocalActorIdentityProvider(
            object issuanceCapability,
            string actorId,
            string sessionId,
            string worldId,
            ActorGrantSet grants,
            AgentMemoryScope memoryScope)
        {
            ActorContext validated = issuanceCapability == null
                ? ActorContext.IssueRestricted(
                    actorId,
                    sessionId,
                    BuiltInAgentRoleIds.Creator,
                    worldId,
                    grants,
                    memoryScope)
                : ActorContext.IssueForComposition(
                    issuanceCapability,
                    actorId,
                    sessionId,
                    BuiltInAgentRoleIds.Creator,
                    worldId,
                    memoryScope);
            _actorId = validated.ActorId;
            _sessionId = validated.SessionId;
            _worldId = validated.WorldId;
            _grants = validated.Grants;
            _memoryScope = validated.MemoryScope;
            _issuanceCapability = issuanceCapability;
        }

        /// <inheritdoc />
        public override ActorContext GetActorContext(string roleId)
        {
            return _issuanceCapability == null
                ? ActorContext.IssueRestricted(_actorId, _sessionId, roleId, _worldId, _grants, _memoryScope)
                : ActorContext.IssueForComposition(
                    _issuanceCapability,
                    _actorId,
                    _sessionId,
                    roleId,
                    _worldId,
                    _memoryScope);
        }

        internal static LocalActorIdentityProvider CreateForComposition(
            object issuanceCapability,
            string actorId,
            string sessionId,
            string worldId,
            AgentMemoryScope memoryScope)
        {
            ActorIdentityComposition.AssertIssuanceCapability(issuanceCapability);
            return new LocalActorIdentityProvider(
                issuanceCapability,
                actorId,
                sessionId,
                worldId,
                ActorGrantSet.Unrestricted,
                memoryScope);
        }
    }

    /// <summary>
    /// Composition-only factory for the unrestricted synthetic host actor. Its opaque capability guards
    /// against accidental or careless escalation by CoreAI code and embedders. It cannot defend against
    /// a hostile full-trust assembly in the same process, because reflection can bypass private members.
    /// </summary>
    internal static class ActorIdentityComposition
    {
        private const string CompositionAssemblyName = "CoreAI.Source";
        private const string CompositionEntryPointTypeName = "CoreAI.Composition.CoreServicesInstaller";
        private static readonly object IssuanceCapability = new();

        internal static LocalActorIdentityProvider CreateLocalHost(object compositionEntryPoint)
        {
            AssertCompositionEntryPoint(compositionEntryPoint);
            return LocalActorIdentityProvider.CreateForComposition(
                IssuanceCapability,
                LocalActorIdentityProvider.DefaultActorId,
                Guid.NewGuid().ToString("N"),
                "",
                AgentMemoryScope.Empty);
        }

        internal static void AssertIssuanceCapability(object issuanceCapability)
        {
            if (!ReferenceEquals(issuanceCapability, IssuanceCapability))
            {
                throw new InvalidOperationException(
                    "Unrestricted actor contexts may only be issued by host composition.");
            }
        }

        private static void AssertCompositionEntryPoint(object compositionEntryPoint)
        {
            Type proofType = compositionEntryPoint?.GetType();
            Type declaringType = proofType?.DeclaringType;
            if (proofType == null ||
                !proofType.IsNestedPrivate ||
                declaringType == null ||
                !string.Equals(declaringType.FullName, CompositionEntryPointTypeName, StringComparison.Ordinal) ||
                !string.Equals(proofType.Assembly.GetName().Name, CompositionAssemblyName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Unrestricted actor providers may only be created by the CoreAI composition entry point.");
            }
        }
    }
}
