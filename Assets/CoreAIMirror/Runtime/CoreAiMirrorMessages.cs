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

        /// <summary>
        /// The CoreAI Mirror protocol the joining client speaks (<see cref="CoreAiMirrorProtocol.Version"/>);
        /// <see cref="CoreAiMirrorProtocol.Unversioned"/> from a client that predates the field.
        /// </summary>
        public int ProtocolVersion;
    }

    /// <summary>The server's answer to an admission request.</summary>
    public struct CoreAiAdmissionResponseMessage : NetworkMessage
    {
        /// <summary>Whether the connection may proceed.</summary>
        public bool Admitted;

        /// <summary>
        /// A short reason shown to the rejected client: the fixed "not admitted" for a refusal of
        /// the host's admission provider, and both protocols for a protocol mismatch.
        /// </summary>
        /// <remarks>
        /// WHY not the provider's own reason: "signature mismatch" versus "expired token" tells an
        /// attacker which half of a forged credential to fix. The detailed reason goes to the host's
        /// log; the client is told only that it was refused. A protocol number is no such secret.
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

        /// <summary>
        /// The protocol the server speaks; <see cref="CoreAiMirrorProtocol.Unversioned"/> from a
        /// server that predates the field. A client refuses an admission from a server of another
        /// protocol.
        /// </summary>
        public int ProtocolVersion;

        /// <summary>Why the admission was refused, as <see cref="CoreAiAdmissionRefusalKind"/>.</summary>
        public byte Refusal;
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
    /// Wire compatibility: the admission handshake carries <see cref="CoreAiMirrorProtocol.Version"/>
    /// both ways, and a peer of another protocol is refused there with a reason, before this or any
    /// later message is sent — so the two sides of an admitted connection always speak the same
    /// protocol. Against a server older than the handshake, a current client refuses the admission
    /// itself: such a server's response carries no protocol number.
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

    /// <summary>What one <see cref="CoreAiRosterMessage"/> does to the client's roster.</summary>
    public enum CoreAiRosterChange : byte
    {
        /// <summary>Replaces the roster with these entries: the first message of a snapshot.</summary>
        Snapshot = 1,

        /// <summary>Appends these entries to the snapshot the previous message started.</summary>
        SnapshotContinued = 2,

        /// <summary>These members joined the room.</summary>
        Joined = 3,

        /// <summary>These members left the room; only their member ids are meaningful.</summary>
        Left = 4
    }

    /// <summary>One member of the room, as a client is told it.</summary>
    /// <remarks>
    /// WHY no actor id: it is the server's routing key and can carry the host's account id; a
    /// client needs who is in the room — the Roblox <c>Player</c> fields — and a key to remove an
    /// entry by, which <see cref="MemberId"/> is.
    /// </remarks>
    public struct CoreAiRosterEntry
    {
        /// <summary>The server's key for this member, unique within the server's run.</summary>
        public uint MemberId;

        /// <summary>The admitted UserId.</summary>
        public long UserId;

        /// <summary>The admitted name, cut to <see cref="CoreAiMirrorRoster.MaxNameBytes"/> UTF-8 bytes.</summary>
        public string Name;

        /// <summary>The admitted display name, cut the same way.</summary>
        public string DisplayName;

        /// <summary>Whether this member is the host's own player (Mirror host mode).</summary>
        public bool IsHost;
    }

    /// <summary>
    /// Who is in the room, server to client: a snapshot when the client acknowledges readiness,
    /// then every join and every leave, on the reliable channel.
    /// </summary>
    public struct CoreAiRosterMessage : NetworkMessage
    {
        /// <summary>What this message does, as <see cref="CoreAiRosterChange"/>.</summary>
        public byte Change;

        /// <summary>
        /// The roster's revision after this message; a snapshot carries the current one and every
        /// join or leave the next.
        /// </summary>
        public uint Revision;

        /// <summary>The recipient's own member id in a snapshot; zero when the recipient is not a member.</summary>
        public uint SelfMemberId;

        /// <summary>The members this message is about.</summary>
        public CoreAiRosterEntry[] Entries;
    }
}
