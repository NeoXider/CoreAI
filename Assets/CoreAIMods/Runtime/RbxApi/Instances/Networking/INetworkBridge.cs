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
    public interface INetworkBridge
    {
        RbxNetworkTopology Topology { get; }

        IReadOnlyList<string> ActorIds { get; }

        event Action<RbxNetworkEventMessage> EventReceived;

        event Action<RbxNetworkRequestMessage, RbxNetworkRequestResponder> RequestReceived;

        void RegisterActor(string actorId);

        void UnregisterActor(string actorId);

        void SendEvent(RbxNetworkEventMessage message);

        void SendRequest(RbxNetworkRequestMessage message,
            Action<RbxNetworkResponse> response);
    }
}
