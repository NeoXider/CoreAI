using System;
using CoreAI.Authority;

namespace CoreAI.Ai.Npc
{
    /// <summary>
    /// Per-NPC memory partitions. Every NPC turn runs with an <see cref="AgentMemoryScope"/> whose topic is
    /// <c>npc:&lt;memoryKey&gt;</c>, so one agent role keeps a separate history and memory for each NPC and each
    /// speaker without a role per NPC. The tenant, user and session come from the host's scope.
    /// </summary>
    public static class NpcMemoryScopes
    {
        /// <summary>Prefix of every NPC memory topic.</summary>
        public const string TopicPrefix = "npc:";

        /// <summary>The topic of the NPC whose memory key is <paramref name="memoryKey"/>.</summary>
        public static string TopicFor(string memoryKey)
        {
            string key = (memoryKey ?? "").Trim();
            if (key.Length == 0)
            {
                throw new ArgumentException("An NPC memory key must be a non-empty string.", nameof(memoryKey));
            }

            return TopicPrefix + key;
        }

        /// <summary><paramref name="baseScope"/> with its topic replaced by the NPC's topic.</summary>
        public static AgentMemoryScope ScopeFor(AgentMemoryScope baseScope, string memoryKey)
        {
            return new AgentMemoryScope(baseScope.TenantId, baseScope.UserId, baseScope.SessionId,
                TopicFor(memoryKey));
        }

        /// <summary>The cancellation scope of the NPC's turns: <c>npc:&lt;npcId&gt;</c>.</summary>
        public static string CancellationScopeFor(string npcId)
        {
            return TopicPrefix + (npcId ?? "").Trim();
        }
    }

    /// <summary>
    /// Chooses the trusted actor an NPC turn runs as. The actor decides the memory partition (with the NPC topic)
    /// and the queue's per-actor fairness; it never widens authority.
    /// </summary>
    public interface INpcActorContextFactory
    {
        /// <summary>The actor for one turn of <paramref name="agent"/> answering <paramref name="line"/>.</summary>
        ActorContext CreateTurnActor(NpcAgent agent, NpcHeardLine line);
    }

    /// <summary>
    /// The default actor choice: the speaker's trusted actor (or the host's current actor, or a synthetic one
    /// named by the speaker id) re-issued with no grants and with the NPC's memory topic over the host's
    /// tenant, user and session. The re-issued context only narrows: grants are always empty.
    /// </summary>
    public sealed class NpcActorContextFactory : INpcActorContextFactory
    {
        private readonly IActorIdentityProvider _hostIdentity;
        private readonly IAgentMemoryScopeProvider _scopeProvider;
        private readonly string _fallbackSessionId = Guid.NewGuid().ToString("N");

        /// <summary>Creates the factory; both arguments are optional.</summary>
        public NpcActorContextFactory(IActorIdentityProvider hostIdentity = null,
            IAgentMemoryScopeProvider scopeProvider = null)
        {
            _hostIdentity = hostIdentity;
            _scopeProvider = scopeProvider;
        }

        /// <inheritdoc />
        public ActorContext CreateTurnActor(NpcAgent agent, NpcHeardLine line)
        {
            if (agent == null)
            {
                throw new ArgumentNullException(nameof(agent));
            }

            string roleId = agent.Persona.EffectiveRoleId;
            ActorContext? speaker = line?.SpeakerActor;
            if ((!speaker.HasValue || !speaker.Value.IsTrusted) && _hostIdentity != null &&
                string.IsNullOrWhiteSpace(line?.SpeakerId))
            {
                speaker = _hostIdentity.GetActorContext(roleId);
            }

            bool trusted = speaker.HasValue && speaker.Value.IsTrusted;
            AgentMemoryScope baseScope = trusted && !IsEmpty(speaker.Value.MemoryScope)
                ? speaker.Value.MemoryScope
                : _scopeProvider?.GetScope(roleId) ?? AgentMemoryScope.Empty;
            string actorId = trusted
                ? speaker.Value.ActorId
                : string.IsNullOrWhiteSpace(line?.SpeakerId)
                    ? LocalActorIdentityProvider.DefaultActorId
                    : line.SpeakerId.Trim();
            string sessionId = trusted ? speaker.Value.SessionId : _fallbackSessionId;
            string worldId = trusted ? speaker.Value.WorldId : "";
            AgentMemoryScope npcScope = NpcMemoryScopes.ScopeFor(baseScope, agent.MemoryKey);
            return new LocalActorIdentityProvider(actorId, sessionId, worldId, ActorGrantSet.None, npcScope)
                .GetActorContext(roleId);
        }

        private static bool IsEmpty(AgentMemoryScope scope)
        {
            return string.IsNullOrEmpty(scope.TenantId) && string.IsNullOrEmpty(scope.UserId) &&
                   string.IsNullOrEmpty(scope.SessionId) && string.IsNullOrEmpty(scope.TopicId);
        }
    }
}
