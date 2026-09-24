using System;
using System.Collections.Generic;

namespace CoreAI.Mods.Rbx.Instances.Networking
{
    /// <summary>Topology exposed by an engine-free network bridge.</summary>
    public enum RbxNetworkTopology
    {
        Solo,
        Host,
        DedicatedServer,
        Client
    }

    /// <summary>Delivery contract selected by the remote instance class.</summary>
    public enum RbxNetworkReliability
    {
        ReliableOrdered,
        UnreliableUnordered
    }

    /// <summary>One of the three Roblox-sanctioned remote directions.</summary>
    public enum RbxNetworkDirection
    {
        ClientToServer,
        ServerToClient,
        ServerToAllClients
    }

    /// <summary>Byte payload for one asynchronous remote event.</summary>
    public sealed class RbxNetworkEventMessage
    {
        public RbxNetworkEventMessage(InstanceId remoteId, RbxNetworkDirection direction,
            RbxNetworkReliability reliability, string senderActorId,
            string recipientActorId, byte[] payload)
        {
            InstanceIdWireContract.EnsureWireSafe(remoteId);
            RemoteId = remoteId;
            Direction = direction;
            Reliability = reliability;
            SenderActorId = senderActorId;
            RecipientActorId = recipientActorId;
            Payload = payload == null ? Array.Empty<byte>() : (byte[])payload.Clone();
        }

        public InstanceId RemoteId { get; }

        public RbxNetworkDirection Direction { get; }

        public RbxNetworkReliability Reliability { get; }

        public string SenderActorId { get; }

        public string RecipientActorId { get; }

        public byte[] Payload { get; }
    }

    /// <summary>Byte payload for one RemoteFunction request.</summary>
    public sealed class RbxNetworkRequestMessage
    {
        public RbxNetworkRequestMessage(InstanceId remoteId, RbxNetworkDirection direction,
            string senderActorId, string recipientActorId, byte[] payload)
        {
            InstanceIdWireContract.EnsureWireSafe(remoteId);
            RemoteId = remoteId;
            Direction = direction;
            SenderActorId = senderActorId;
            RecipientActorId = recipientActorId;
            Payload = payload == null ? Array.Empty<byte>() : (byte[])payload.Clone();
        }

        public InstanceId RemoteId { get; }

        public RbxNetworkDirection Direction { get; }

        public string SenderActorId { get; }

        public string RecipientActorId { get; }

        public byte[] Payload { get; }
    }

    /// <summary>Terminal byte response for a RemoteFunction request.</summary>
    public sealed class RbxNetworkResponse
    {
        private RbxNetworkResponse(bool succeeded, byte[] payload, string error)
        {
            Succeeded = succeeded;
            Payload = payload == null ? Array.Empty<byte>() : (byte[])payload.Clone();
            Error = error;
        }

        public bool Succeeded { get; }

        public byte[] Payload { get; }

        public string Error { get; }

        public static RbxNetworkResponse Success(byte[] payload)
        {
            return new RbxNetworkResponse(true, payload, null);
        }

        public static RbxNetworkResponse Failure(string error)
        {
            string reason = string.IsNullOrWhiteSpace(error)
                ? "remote callback failed"
                : error;
            return new RbxNetworkResponse(false, Array.Empty<byte>(), reason);
        }
    }

    /// <summary>Single-use response handle supplied to the receiving RemoteFunction endpoint.</summary>
    public sealed class RbxNetworkRequestResponder
    {
        private readonly Action<RbxNetworkResponse> _complete;
        private bool _completed;

        internal RbxNetworkRequestResponder(Action<RbxNetworkResponse> complete)
        {
            _complete = complete ?? throw new ArgumentNullException(nameof(complete));
        }

        public bool IsCompleted => _completed;

        public void Complete(byte[] payload)
        {
            CompleteCore(RbxNetworkResponse.Success(payload));
        }

        public void Fail(string error)
        {
            CompleteCore(RbxNetworkResponse.Failure(error));
        }

        private void CompleteCore(RbxNetworkResponse response)
        {
            if (_completed)
            {
                throw RbxError.BadArgument(
                    "RemoteFunction request responder is already complete",
                    "complete each request exactly once");
            }

            _completed = true;
            _complete(response);
        }
    }

    /// <summary>
    /// Transport-neutral byte boundary for Roblox remotes. Implementations decide how reliable and
    /// unreliable messages travel; the Rbx layer owns serialization and instance resolution.
    /// </summary>
    /// <summary>One connected peer, as the transport identifies it.</summary>
    public readonly struct RbxNetworkPeer
    {
        /// <summary>Creates a peer record.</summary>
        public RbxNetworkPeer(string actorId, string sessionId, string connectionHandle)
        {
            ActorId = actorId ?? "";
            SessionId = sessionId ?? "";
            ConnectionHandle = connectionHandle ?? "";
        }

        /// <summary>The durable actor this connection was admitted as.</summary>
        public string ActorId { get; }

        /// <summary>This connection's session; a reconnect gets a new one.</summary>
        public string SessionId { get; }

        /// <summary>The transport's own handle, opaque to everything above it.</summary>
        public string ConnectionHandle { get; }
    }

    /// <summary>Why a peer's connection ended.</summary>
    public enum RbxNetworkDisconnectReason
    {
        /// <summary>The client asked to leave.</summary>
        Graceful,

        /// <summary>The transport dropped it — timeout, cable, crash.</summary>
        TransportLost,

        /// <summary>The server ended it (a kick, or admission revoked).</summary>
        ServerClosed
    }

    /// <summary>A peer left, with the reason the teardown must report.</summary>
    public sealed class RbxNetworkPeerDisconnected
    {
        /// <summary>Records one disconnection.</summary>
        public RbxNetworkPeerDisconnected(RbxNetworkPeer peer, RbxNetworkDisconnectReason reason)
        {
            Peer = peer;
            Reason = reason;
        }

        /// <summary>The peer that left.</summary>
        public RbxNetworkPeer Peer { get; }

        /// <summary>Why it left.</summary>
        public RbxNetworkDisconnectReason Reason { get; }
    }

    /// <summary>
    /// Reads the server clock a world tells the time by: the value <c>workspace:GetServerTimeNow()</c>
    /// returns there now, and how many seconds that value is held ahead of the world's own clock —
    /// zero while the clock runs, positive while it holds its last reading after the world's clock
    /// stepped back.
    /// </summary>
    public delegate double RbxServerClockReader(out double heldAheadSeconds);

    public interface INetworkBridge
    {
        RbxNetworkTopology Topology { get; }

        /// <summary>Registered client actor ids; the authoritative server is never a client recipient.</summary>
        IReadOnlyList<string> ActorIds { get; }

        event Action<RbxNetworkEventMessage> EventReceived;

        event Action<RbxNetworkRequestMessage, RbxNetworkRequestResponder> RequestReceived;

        /// <summary>Registers a client actor as a remote recipient.</summary>
        void RegisterActor(string actorId);

        /// <summary>Removes a client actor from remote delivery.</summary>
        void UnregisterActor(string actorId);

        /// <summary>
        /// Ends one admitted actor's connection from the server side — the transport half of a
        /// kick. The peer's teardown runs through <see cref="PeerDisconnected"/> as
        /// <see cref="RbxNetworkDisconnectReason.ServerClosed"/>, its binding is released, and the
        /// transport is told to drop the socket. Nothing happens for an actor that holds no
        /// connection here: a loopback actor, or one that already left.
        /// </summary>
        /// <remarks>
        /// WHY the bridge and not the Players service alone: removing the Player is the world's
        /// half, but only the transport can close the socket behind it, and a kicked connection
        /// that stays bound resolves that client's very next remote to a player the world just
        /// removed — and re-creates it. WHY a default body: the loopback has no socket per actor
        /// and nothing to end; a bridge that owns connections overrides this, and the wrappers
        /// around one forward it.
        /// </remarks>
        void DisconnectActor(string actorId)
        {
        }

        /// <summary>
        /// <see cref="DisconnectActor(string)"/> carrying the text the kicked client is shown — the
        /// transport half of <c>Player:Kick(message)</c>. Null or blank leaves the transport's own
        /// default notice, as a Kick with no message does on Roblox. The Players service cuts the
        /// text to <see cref="RbxPlayers.MaxKickMessageBytes"/> UTF-8 bytes before it gets here.
        /// </summary>
        /// <remarks>
        /// WHY a default body that ends the connection without the text: a bridge with no channel
        /// to tell a client anything — the loopback, a test double, a transport written before this
        /// member — still ends the connection through its one-argument overload, so adding the text
        /// breaks no implementer. A transport that can deliver a notice implements this
        /// (MirrorNetworkBridge sends the text before it drops the connection), and the wrappers
        /// around one forward it.
        /// </remarks>
        void DisconnectActor(string actorId, string message) => DisconnectActor(actorId);

        void SendEvent(RbxNetworkEventMessage message);

        void SendRequest(RbxNetworkRequestMessage message,
            Action<RbxNetworkResponse> response);

        /// <summary>
        /// The largest payload this transport will carry, in bytes.
        /// </summary>
        /// <remarks>
        /// WHY the bridge answers and not a constant: the loopback bridge is bounded only by the
        /// codec, while a real transport is bounded by its MTU for unreliable channels — and a
        /// message accepted in solo that silently vanishes online is the worst shape this seam can
        /// have. Asking the bridge lets the refusal happen at the same place in both.
        /// </remarks>
        int MaxPayloadBytes { get; }

        /// <summary>
        /// A peer's connection ended, gracefully or not.
        /// </summary>
        /// <remarks>
        /// WHY an event and not a return value from UnregisterActor: a network drop is not initiated
        /// by CoreAI, and the Player teardown it triggers (PlayerRemoving, thread kills, quota
        /// release) has to run whether the client said goodbye or the cable did.
        /// </remarks>
        event Action<RbxNetworkPeerDisconnected> PeerDisconnected;

        /// <summary>
        /// How far this process's clock is behind the server's, in seconds; zero on the server.
        /// </summary>
        /// <remarks>
        /// WHY it lives on the transport: only the transport measures round trips. Every clock the
        /// Lua layer exposes reads through this, so a client whose wall clock is an hour off still
        /// agrees with the server about when things happened.
        /// </remarks>
        double ServerClockOffsetSeconds { get; }

        /// <summary>
        /// Whether <see cref="ServerClockOffsetSeconds"/> measures the server's clock yet: always on a
        /// server, and on a client from its first synchronization on. Before that the offset is zero
        /// because nothing is known, not because the clocks agree.
        /// </summary>
        /// <remarks>
        /// WHY consumers need it: a clock kept monotonic on top of the offset must treat the first
        /// synchronization as a re-base, not as time running backwards; without this answer a client
        /// that read its own clock before the first anchor froze its server time until the whole
        /// skew had elapsed. WHY a default body: a server and the loopback are synchronized by
        /// definition; a transport that learns the offset later overrides this, and the wrappers
        /// around one forward it.
        /// </remarks>
        bool IsServerClockSynchronized => true;

        /// <summary>
        /// Whether the server's clock is standing still right now: its wall clock stepped back and
        /// its <c>GetServerTimeNow</c> holds its last reading until the wall clock catches up. Only a
        /// client's transport can answer true, and <see cref="ServerClockOffsetSeconds"/> then already
        /// reproduces the hold.
        /// </summary>
        /// <remarks>
        /// WHY a consumer needs it: a client clock that is kept monotonic slews onto an estimate that
        /// moved behind it — and an estimate that stands still looks exactly like one that moved
        /// behind, so the client ran on at half speed while the server's clock stood, and the two
        /// disagreed by half the step for as long as the hold lasted (A4-08). A consumer that reads
        /// true holds as the server holds. WHY a default body: a server and the loopback are the
        /// clock, and never report their own hold as a remote one.
        /// </remarks>
        bool IsServerClockHeld => false;

        /// <summary>
        /// Hands a server-side bridge the clock its world's <c>GetServerTimeNow</c> reads, so the
        /// server time a transport sends clients is the one the server's own scripts read, held
        /// exactly as they see it held. A bridge that sends no server time ignores it.
        /// </summary>
        /// <remarks>
        /// WHY the world hands it over: the world keeps its clock monotonic, and a transport that sent
        /// its raw wall clock told clients about a backward step the server's scripts never saw
        /// (A4-08, A3-04). WHY a default body: the loopback is the server clock itself, and a transport
        /// written before this member keeps sending what it sent.
        /// </remarks>
        void AttachServerClock(RbxServerClockReader serverClock)
        {
        }

        /// <summary>
        /// Takes back a clock <see cref="AttachServerClock"/> handed over, when its world is disposed;
        /// a clock attached since by another world is left in place.
        /// </summary>
        void DetachServerClock(RbxServerClockReader serverClock)
        {
        }
    }
}
