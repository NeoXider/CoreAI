using System;

namespace CoreAI.Authority
{
    /// <summary>
    /// An opaque credential offered by a connecting client, plus the transport address it arrived from.
    /// </summary>
    /// <remarks>
    /// WHY opaque: CoreAI does not own an account system and must not grow one. The bytes are whatever
    /// the host's own authentication produced — a signed token, a session ticket, a platform receipt —
    /// and CoreAI never parses, stores or logs them. <see cref="TransportAddress"/> is carried for the
    /// host's rate limiting and ban lists, not consulted here.
    /// </remarks>
    public readonly struct ActorCredential
    {
        private readonly byte[] _opaque;
        private readonly string _transportAddress;

        /// <summary>Creates a credential from the bytes a client sent and the address it sent them from.</summary>
        public ActorCredential(byte[] opaque, string transportAddress)
        {
            _opaque = opaque;
            _transportAddress = transportAddress;
        }

        /// <summary>The host-defined credential bytes; never empty for an admissible connection.</summary>
        public byte[] Opaque => _opaque ?? Array.Empty<byte>();

        /// <summary>Transport-level origin, for the host's own rate limiting. Never an identity.</summary>
        /// <remarks>
        /// WHY read through a field: a struct's default value skips every constructor, so an
        /// auto-property would hand a caller null on <c>default(ActorCredential)</c> — and this value
        /// is read on the rejection path, where a NullReferenceException would replace the real
        /// reason a connection was refused.
        /// </remarks>
        public string TransportAddress => _transportAddress ?? "";
    }

    /// <summary>
    /// The answer to "may this connection join, and as whom".
    /// </summary>
    /// <remarks>
    /// WHY a sealed result with factories instead of settable fields: an admission that accidentally
    /// says "admitted" with no context, or with an unrestricted one, would hand a remote client the
    /// host's own authority. Both are refused at construction, so the dangerous shape cannot be built
    /// at all — including by a host implementing this port outside CoreAI.
    /// </remarks>
    public sealed class ActorAdmissionResult
    {
        private ActorAdmissionResult(bool admitted, string reason, ActorContext context,
            long userId, string name, string displayName)
        {
            Admitted = admitted;
            Reason = reason ?? "";
            Context = context;
            UserId = userId;
            Name = name ?? "";
            DisplayName = displayName ?? "";
        }

        /// <summary>Whether the connection may proceed to actor registration.</summary>
        public bool Admitted { get; }

        /// <summary>Why a rejection happened, for the host's log. Never sent to the rejected client verbatim.</summary>
        public string Reason { get; }

        /// <summary>
        /// The restricted actor context this connection acts through. On a rejection it is the
        /// default value, whose <c>IsTrusted</c> is false — an untrusted context authorizes nothing,
        /// so a caller that forgets to check <see cref="Admitted"/> still cannot act on it.
        /// </summary>
        public ActorContext Context { get; }

        /// <summary>Durable numeric identity, the value Lua reads as <c>Player.UserId</c>.</summary>
        public long UserId { get; }

        /// <summary>Durable account name, the value Lua reads as <c>Player.Name</c>.</summary>
        public string Name { get; }

        /// <summary>Presentation name, the value Lua reads as <c>Player.DisplayName</c>.</summary>
        public string DisplayName { get; }

        /// <summary>Refuses the connection. The reason is for the host's log, not for the client.</summary>
        public static ActorAdmissionResult Reject(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                throw new ArgumentException(
                    "A rejection must say why, or an admission failure is undiagnosable.", nameof(reason));
            }

            return new ActorAdmissionResult(false, reason, default, 0L, null, null);
        }

        /// <summary>
        /// Admits the connection as the supplied identity.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// The context was not issued by an identity provider or carries unrestricted grants; or the
        /// identity is not usable as a <c>Player</c>.
        /// </exception>
        public static ActorAdmissionResult Admit(
            ActorContext context, long userId, string name, string displayName)
        {
            if (!context.IsTrusted)
            {
                throw new ArgumentException(
                    "An admission context must come from an IActorIdentityProvider; a hand-built value "
                    + "carries no identity.", nameof(context));
            }

            if (context.Grants.IsUnrestricted)
            {
                throw new ArgumentException(
                    "A remote connection can never be admitted with unrestricted grants — that is the "
                    + "host's own authority.", nameof(context));
            }

            if (userId <= 0L)
            {
                throw new ArgumentOutOfRangeException(nameof(userId),
                    "Player.UserId is positive in Roblox; 0 and negatives are reserved for absent identity.");
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Player.Name cannot be empty.", nameof(name));
            }

            return new ActorAdmissionResult(true, "", context, userId, name,
                string.IsNullOrWhiteSpace(displayName) ? name : displayName);
        }
    }

    /// <summary>
    /// Domain port a host implements to decide who may join an online world.
    /// </summary>
    /// <remarks>
    /// WHY there is no default implementation: an "allow anyone" fallback is exactly the bug this port
    /// exists to prevent, and a default would be reached by every composition that forgot to configure
    /// one. An online composition without a provider is a startup error, not an open door. Solo
    /// compositions never consult this port — there is no remote connection to admit.
    /// </remarks>
    public interface IActorAdmissionProvider
    {
        /// <summary>
        /// Decides whether a connection offering <paramref name="credential"/> may join
        /// <paramref name="worldId"/>. Called once per connection, before any Player, chat, mod,
        /// remote or world state exists for it.
        /// </summary>
        ActorAdmissionResult TryAdmit(in ActorCredential credential, string worldId);
    }
}
