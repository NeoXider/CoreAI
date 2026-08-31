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
    /// Trusted construction boundary for actor identity providers.
    /// </summary>
    public abstract class ActorIdentityProviderBase : IActorIdentityProvider
    {
        /// <inheritdoc />
        public abstract ActorContext GetActorContext(string roleId);

        /// <summary>Issues an immutable actor context.</summary>
        protected static ActorContext IssueActorContext(
            string actorId,
            string sessionId,
            string roleId,
            string worldId,
            ActorGrantSet grants,
            AgentMemoryScope memoryScope)
        {
            return ActorContext.Issue(actorId, sessionId, roleId, worldId, grants, memoryScope);
        }
    }

    /// <summary>
    /// Synthetic identity provider for local hosts and unauthenticated single-player sessions.
    /// </summary>
    public sealed class LocalActorIdentityProvider : ActorIdentityProviderBase
    {
        /// <summary>Durable id shared by local connections and independent of agent role.</summary>
        public const string DefaultActorId = "local";

        private static readonly LocalActorIdentityProvider DefaultValue = new();

        private readonly string _actorId;
        private readonly ActorGrantSet _grants;
        private readonly AgentMemoryScope _memoryScope;
        private readonly string _sessionId;
        private readonly string _worldId;

        /// <summary>Stable no-configuration provider for existing local hosts.</summary>
        public static LocalActorIdentityProvider Default => DefaultValue;

        /// <summary>Starts the default local actor with a new connection session.</summary>
        public LocalActorIdentityProvider()
            : this(DefaultActorId)
        {
        }

        /// <summary>Starts a synthetic actor with a durable id and a new connection session.</summary>
        public LocalActorIdentityProvider(string actorId)
            : this(actorId, Guid.NewGuid().ToString("N"), "", ActorGrantSet.Unrestricted, AgentMemoryScope.Empty)
        {
        }

        /// <summary>Creates a synthetic provider with explicit identity, grants, and memory composition.</summary>
        public LocalActorIdentityProvider(
            string actorId,
            string sessionId,
            string worldId,
            ActorGrantSet grants,
            AgentMemoryScope memoryScope)
        {
            ActorContext validated = IssueActorContext(
                actorId,
                sessionId,
                BuiltInAgentRoleIds.Creator,
                worldId,
                grants,
                memoryScope);
            _actorId = validated.ActorId;
            _sessionId = validated.SessionId;
            _worldId = validated.WorldId;
            _grants = validated.Grants;
            _memoryScope = validated.MemoryScope;
        }

        /// <inheritdoc />
        public override ActorContext GetActorContext(string roleId)
        {
            return IssueActorContext(_actorId, _sessionId, roleId, _worldId, _grants, _memoryScope);
        }
    }
}
