using Mirror;

namespace CoreAI.Net.Mirror
{
    /// <summary>
    /// The credential a joining client offers, before it is anything to the world.
    /// </summary>
    /// <remarks>
    /// WHY it carries no identity fields: a client that could state its own actor id, UserId or name
    /// would be stating its own privileges. The only thing it may send is the opaque proof the host's
    /// own authentication issued; who that proof belongs to is decided on the server.
    /// </remarks>
    public struct CoreAiAdmissionRequestMessage : NetworkMessage
    {
        /// <summary>Host-defined credential bytes. CoreAI never parses, stores or logs them.</summary>
        public byte[] Credential;
    }

    /// <summary>The server's answer to an admission request.</summary>
    public struct CoreAiAdmissionResponseMessage : NetworkMessage
    {
        /// <summary>Whether the connection may proceed.</summary>
        public bool Admitted;

        /// <summary>
        /// A short, non-specific reason shown to the rejected client.
        /// </summary>
        /// <remarks>
        /// WHY not the provider's own reason: "signature mismatch" versus "expired token" tells an
        /// attacker which half of a forged credential to fix. The detailed reason goes to the host's
        /// log; the client is told only that it was refused.
        /// </remarks>
        public string Reason;
    }

    /// <summary>
    /// One RemoteEvent fire on the wire.
    /// </summary>
    /// <remarks>
    /// WHY there is no sender field: the server fills the sender from its own connection map, so a
    /// client cannot claim to be someone else by editing a packet. The absence of the field is the
    /// defence — a field that exists but is ignored is one refactor away from being trusted.
    /// </remarks>
    public struct CoreAiRemoteEventMessage : NetworkMessage
    {
        /// <summary>The RemoteEvent instance this fire belongs to.</summary>
        public ulong RemoteId;

        /// <summary>Direction, as the engine-free layer spells it.</summary>
        public byte Direction;

        /// <summary>Reliability, as the engine-free layer spells it.</summary>
        public byte Reliability;

        /// <summary>The serialized argument payload.</summary>
        public byte[] Payload;
    }

    /// <summary>One RemoteFunction invocation.</summary>
    public struct CoreAiRemoteRequestMessage : NetworkMessage
    {
        /// <summary>The RemoteFunction instance being invoked.</summary>
        public ulong RemoteId;

        /// <summary>Direction, as the engine-free layer spells it.</summary>
        public byte Direction;

        /// <summary>
        /// Server-scoped correlation id. A response completes a request only when the connection AND
        /// this id both match an open entry, so a crafted or replayed id completes nothing.
        /// </summary>
        public uint CorrelationId;

        /// <summary>The serialized argument payload.</summary>
        public byte[] Payload;
    }

    /// <summary>One RemoteFunction result travelling back.</summary>
    public struct CoreAiRemoteResponseMessage : NetworkMessage
    {
        /// <summary>The correlation id of the request being answered.</summary>
        public uint CorrelationId;

        /// <summary>Whether the invocation succeeded.</summary>
        public bool Success;

        /// <summary>The serialized result payload when successful.</summary>
        public byte[] Payload;

        /// <summary>The structured error code when not.</summary>
        public string ErrorCode;

        /// <summary>The human-readable error message when not.</summary>
        public string ErrorMessage;
    }
}
