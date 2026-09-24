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

        /// <summary>
        /// The actor the server admitted this connection as. Empty on a refusal.
        /// </summary>
        /// <remarks>
        /// WHY the client is told at all: its credential is opaque to CoreAI and the identity behind
        /// it is decided on the server, so this response is the only place a client can learn who
        /// the server routes <c>FireClient</c> to — and the client bridge keys every inbound remote
        /// by it. WHY empty on a refusal: a rejection must tell the client nothing, and an identity
        /// would be the first thing it must not tell.
        /// </remarks>
        public string ActorId;
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

    /// <summary>
    /// A joining client's word that it can hear the server: its bridge exists and has bound the actor
    /// the admission response named. Sent once per connection, client to server.
    /// </summary>
    /// <remarks>
    /// WHY an acknowledgement at all: the server admits a connection one round trip before the
    /// client has processed the answer, and anything the server puts on the wire in that window
    /// reaches a client that cannot route it yet — or, when the client's bridge is built after its
    /// admission, one whose Mirror has no handler for it and disconnects itself. Until this arrives
    /// the server holds reliable remotes for the connection and drops unreliable ones, counted.
    /// WHY it carries nothing: who the client is was decided at admission and lives in the server's
    /// connection map; a field here would be a claim the server must ignore.
    /// <para>
    /// Wire compatibility of the three messages below: they are additive, and mixed versions fail
    /// loudly instead of half-working — provided Mirror's <c>exceptionsDisconnect</c> is on, its
    /// default. A server that predates them has no handler for this one, so its Mirror disconnects a
    /// newer client right after admission, with an error in the server log. A client that predates
    /// them never sends it, so a newer server holds that client's reliable remotes and drops it at
    /// the readiness deadline with a log line naming the cause; the clock anchor goes only to
    /// acknowledged connections, so such a client is never sent a message it cannot read before
    /// that. A field added to a message is not caught this way: a reader that meets fewer bytes than
    /// it expects throws inside Mirror's handler, which disconnects only with
    /// <c>exceptionsDisconnect</c> on and otherwise logs and keeps the connection, one message
    /// lost. Server and client therefore run the same CoreAI version, as the admission response
    /// already requires; nothing on the wire negotiates it.
    /// </para>
    /// </remarks>
    public struct CoreAiClientReadyMessage : NetworkMessage
    {
    }

    /// <summary>
    /// The server's time at the moment of sending, so a client can tell the server's time from its
    /// own. Sent to a connection when it acknowledges readiness, to every acknowledged connection at
    /// an interval, and at once when the server's clock leaves the course its clients extrapolate.
    /// </summary>
    /// <remarks>
    /// WHY Unix time and not Mirror's clocks: Mirror's <c>NetworkTime</c> counts seconds since each
    /// process started, and its client offset compares two such uptimes — which says nothing about
    /// the wall clock <c>workspace:GetServerTimeNow()</c> reports. The client carries this anchor
    /// forward on its own monotonic clock, so neither machine's uptime nor the client's wall clock
    /// enters the result. WHY the hold travels too: after the server's wall clock steps back, the
    /// server's <c>GetServerTimeNow</c> holds its last reading until the wall clock catches up, and a
    /// client that knows only the time extrapolates past a server that stands still.
    /// </remarks>
    public struct CoreAiServerClockMessage : NetworkMessage
    {
        /// <summary>
        /// The server's Unix time, in seconds with a fraction, when the message was sent: what the
        /// server's own <c>GetServerTimeNow</c> read then.
        /// </summary>
        public double ServerUnixSeconds;

        /// <summary>
        /// How many seconds <see cref="ServerUnixSeconds"/> is held ahead of the server's running
        /// clock; zero while the server's clock runs. The running clock catches up with the held
        /// value this many seconds later, and the hold ends there.
        /// </summary>
        public double HeldAheadOfWallSeconds;
    }

    /// <summary>Why the server is about to close a client's connection.</summary>
    public enum CoreAiDisconnectNoticeKind : byte
    {
        /// <summary>A script or the host kicked the player.</summary>
        Kicked = 1,

        /// <summary>The same player was admitted again on a newer connection, which replaced this one.</summary>
        Superseded = 2
    }

    /// <summary>
    /// The reason the server closes a client's connection, sent to that client before the drop.
    /// </summary>
    /// <remarks>
    /// WHY a message and not the transport's disconnect: a transport drop carries no reason, so a
    /// kicked player cannot tell a kick from a server that vanished, and Roblox shows the kick
    /// message to the player it kicked. WHY the drop waits a frame for it: Mirror discards what a
    /// connection has not flushed yet when that connection is dropped, and kcp2k reports the drop
    /// before the call returns, so a notice sent in the same frame as the drop never leaves.
    /// </remarks>
    public struct CoreAiDisconnectNoticeMessage : NetworkMessage
    {
        /// <summary>Why the connection closes, as <see cref="CoreAiDisconnectNoticeKind"/>.</summary>
        public byte Kind;

        /// <summary>The text shown to the player; the kick message a script gave, when it gave one.</summary>
        public string Message;
    }
}
