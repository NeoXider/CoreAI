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
    }
}
