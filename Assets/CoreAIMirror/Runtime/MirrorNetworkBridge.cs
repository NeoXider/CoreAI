using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using Mirror;

namespace CoreAI.Net.Mirror
{
    /// <summary>
    /// Carries CoreAI's remotes over a real Mirror transport.
    /// </summary>
    /// <remarks>
    /// The rules this implementation exists to keep, each of which is a gate:
    /// <list type="bullet">
    /// <item><description>
    /// <b>Sender binding.</b> The wire envelope carries no actor id. The sender is filled from this
    /// bridge's own connection map, which only the admission adapter populates — so a client cannot
    /// become someone else by editing a packet, and a packet from an unadmitted connection is
    /// dropped and counted rather than delivered.
    /// </description></item>
    /// <item><description>
    /// <b>Correlation.</b> A response completes a request only when the connection AND the
    /// correlation id both match an open entry. A late response after the timeout is counted and
    /// dropped, so a slow answer cannot resolve a call that already failed.
    /// </description></item>
    /// <item><description>
    /// <b>Budget.</b> Client traffic is admitted through the same shared
    /// <see cref="RbxNetworkRateLimiter"/> the loopback uses, so the transport facing a real network
    /// cannot be the one without a budget.
    /// </description></item>
    /// </list>
    /// <para>
    /// No <c>NetworkBehaviour</c> or <c>SyncVar</c> is ever exposed to mods: this uses Mirror's
    /// message handlers only, which keeps the entire Lua surface transport-agnostic.
    /// </para>
    /// </remarks>
    public sealed class MirrorNetworkBridge : INetworkBridge, IDisposable
    {
        private sealed class PendingRequest
        {
            public int ConnectionId;
            public Action<RbxNetworkResponse> Complete;
            public double DeadlineSeconds;
        }

        private readonly Dictionary<int, RbxNetworkPeer> _peersByConnection = new();
        private readonly Dictionary<string, int> _connectionsByActor = new(StringComparer.Ordinal);
        private readonly Dictionary<uint, PendingRequest> _pending = new();
        private readonly List<string> _actorOrder = new();
        private readonly RbxNetworkRateLimiter _rateLimiter;
        private readonly Func<double> _clockSeconds;
        private readonly CoreAiMirrorAuthenticator _authenticator;
        private readonly bool _isServer;
        private uint _nextCorrelationId = 1u;
        private bool _disposed;

        /// <summary>The mirror-documented timeout for a RemoteFunction invocation.</summary>
        public const double RequestTimeoutSeconds = 30d;

        /// <summary>Creates the bridge over an already-started Mirror server or client.</summary>
        public MirrorNetworkBridge(bool isServer, CoreAiMirrorAuthenticator authenticator = null,
            int maxClientRequestsPerSecond = RbxNetworkRateLimiter.DefaultMaxClientRequestsPerSecond,
            Func<double> clockSeconds = null)
        {
            _isServer = isServer;
            _authenticator = authenticator;
            _clockSeconds = clockSeconds ?? (() => NetworkTime.localTime);
            _rateLimiter = new RbxNetworkRateLimiter(maxClientRequestsPerSecond, _clockSeconds);
            RegisterHandlers();
        }

        /// <inheritdoc />
        public RbxNetworkTopology Topology =>
            _isServer ? RbxNetworkTopology.Host : RbxNetworkTopology.Client;

        /// <inheritdoc />
        public IReadOnlyList<string> ActorIds => _actorOrder.AsReadOnly();

        /// <summary>Packets dropped because their connection was never admitted.</summary>
        public int UnadmittedPacketsDropped { get; private set; }

        /// <summary>Responses dropped because nothing was waiting for their correlation id.</summary>
        public int OrphanResponsesDropped { get; private set; }

        /// <summary>Requests that reached the timeout without an answer.</summary>
        public int TimedOutRequests { get; private set; }

        /// <summary>Envelopes handed to the transport.</summary>
        public int PacketsSent { get; private set; }

        /// <summary>Envelopes delivered out of the transport.</summary>
        public int PacketsDelivered { get; private set; }

        /// <summary>Payload bytes handed to the transport.</summary>
        public long BytesSent { get; private set; }

        /// <inheritdoc />
        /// <remarks>
        /// Read from the transport at runtime rather than assumed: KCP, WebSockets and a LAN
        /// transport do not agree on a packet size, and the unreliable channel is the tighter of the
        /// two — which is the one a gameplay burst uses.
        /// </remarks>
        public int MaxPayloadBytes
        {
            get
            {
                Transport active = Transport.active;
                if (active == null)
                {
                    return 65536;
                }

                int reliable = active.GetMaxPacketSize(Channels.Reliable);
                int unreliable = active.GetMaxPacketSize(Channels.Unreliable);
                int smallest = reliable < unreliable ? reliable : unreliable;
                return smallest < 65536 ? smallest : 65536;
            }
        }

        /// <inheritdoc />
        public double ServerClockOffsetSeconds => _isServer ? 0d : NetworkTime.offset;

        /// <inheritdoc />
        public event Action<RbxNetworkEventMessage> EventReceived;

        /// <inheritdoc />
        public event Action<RbxNetworkRequestMessage, RbxNetworkRequestResponder> RequestReceived;

        /// <inheritdoc />
        public event Action<RbxNetworkPeerDisconnected> PeerDisconnected;

        /// <summary>
        /// Binds an admitted connection to its actor. Called by the composition after admission,
        /// never by this bridge on a first packet.
        /// </summary>
        public void BindConnection(int connectionId, RbxNetworkPeer peer)
        {
            _peersByConnection[connectionId] = peer;
            _connectionsByActor[peer.ActorId] = connectionId;
        }

        /// <inheritdoc />
        public void RegisterActor(string actorId)
        {
            if (string.IsNullOrEmpty(actorId) || _actorOrder.Contains(actorId))
            {
                return;
            }

            _actorOrder.Add(actorId);
        }

        /// <inheritdoc />
        public void UnregisterActor(string actorId)
        {
            if (string.IsNullOrEmpty(actorId))
            {
                return;
            }

            _actorOrder.Remove(actorId);
            _rateLimiter.Forget(actorId);
            if (_connectionsByActor.TryGetValue(actorId, out int connectionId))
            {
                _connectionsByActor.Remove(actorId);
                _peersByConnection.Remove(connectionId);
                _authenticator?.Forget(connectionId);
                FailPendingFor(connectionId, "the peer disconnected");
            }
        }

        /// <summary>Reports a transport-level disconnect so the world can tear the actor down.</summary>
        public void NotifyDisconnected(int connectionId, RbxNetworkDisconnectReason reason)
        {
            if (!_peersByConnection.TryGetValue(connectionId, out RbxNetworkPeer peer))
            {
                return;
            }

            FailPendingFor(connectionId, "the peer disconnected");
            PeerDisconnected?.Invoke(new RbxNetworkPeerDisconnected(peer, reason));
        }

        /// <inheritdoc />
        public void SendEvent(RbxNetworkEventMessage message)
        {
            RequirePayloadFits(message.Payload);
            RbxNetworkRateGroup group =
                message.Reliability == RbxNetworkReliability.UnreliableUnordered
                    ? RbxNetworkRateGroup.UnreliableRemoteEvent
                    : RbxNetworkRateGroup.ReliableRemoteEvent;
            if (message.Direction == RbxNetworkDirection.ClientToServer)
            {
                _rateLimiter.Admit(message.SenderActorId, group);
            }

            CoreAiRemoteEventMessage wire = new()
            {
                RemoteId = message.RemoteId.Value,
                Direction = (byte)message.Direction,
                Reliability = (byte)message.Reliability,
                Payload = message.Payload
            };

            int channel = message.Reliability == RbxNetworkReliability.UnreliableUnordered
                ? Channels.Unreliable
                : Channels.Reliable;

            if (!_isServer)
            {
                NetworkClient.Send(wire, channel);
                Count(message.Payload);
                return;
            }

            if (!string.IsNullOrEmpty(message.RecipientActorId))
            {
                if (_connectionsByActor.TryGetValue(message.RecipientActorId, out int single)
                    && NetworkServer.connections.TryGetValue(single, out NetworkConnectionToClient one))
                {
                    one.Send(wire, channel);
                    Count(message.Payload);
                }

                return;
            }

            foreach (KeyValuePair<int, NetworkConnectionToClient> pair in NetworkServer.connections)
            {
                pair.Value.Send(wire, channel);
                Count(message.Payload);
            }
        }

        /// <inheritdoc />
        public void SendRequest(RbxNetworkRequestMessage message,
            Action<RbxNetworkResponse> response)
        {
            RequirePayloadFits(message.Payload);
            if (message.Direction == RbxNetworkDirection.ClientToServer)
            {
                _rateLimiter.Admit(message.SenderActorId, RbxNetworkRateGroup.RemoteFunction);
            }

            uint correlationId = _nextCorrelationId++;
            _pending[correlationId] = new PendingRequest
            {
                ConnectionId = ResolveConnectionId(message),
                Complete = response,
                DeadlineSeconds = _clockSeconds() + RequestTimeoutSeconds
            };

            CoreAiRemoteRequestMessage wire = new()
            {
                RemoteId = message.RemoteId.Value,
                Direction = (byte)message.Direction,
                CorrelationId = correlationId,
                Payload = message.Payload
            };

            if (!_isServer)
            {
                NetworkClient.Send(wire);
                Count(message.Payload);
                return;
            }

            if (_connectionsByActor.TryGetValue(message.RecipientActorId ?? "", out int connectionId)
                && NetworkServer.connections.TryGetValue(connectionId, out NetworkConnectionToClient conn))
            {
                conn.Send(wire);
                Count(message.Payload);
            }
        }

        /// <summary>Fails every request whose deadline has passed. Call once per frame.</summary>
        public void PumpTimeouts()
        {
            if (_pending.Count == 0)
            {
                return;
            }

            double now = _clockSeconds();
            List<uint> expired = null;
            foreach (KeyValuePair<uint, PendingRequest> pair in _pending)
            {
                if (pair.Value.DeadlineSeconds <= now)
                {
                    expired ??= new List<uint>();
                    expired.Add(pair.Key);
                }
            }

            if (expired == null)
            {
                return;
            }

            for (int index = 0; index < expired.Count; index++)
            {
                PendingRequest request = _pending[expired[index]];
                _pending.Remove(expired[index]);
                TimedOutRequests++;
                request.Complete?.Invoke(RbxNetworkResponse.Failure(
                    "the remote did not answer within "
                    + RequestTimeoutSeconds.ToString("0") + " seconds"));
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            UnregisterHandlers();
            _pending.Clear();
            _peersByConnection.Clear();
            _connectionsByActor.Clear();
            _actorOrder.Clear();
            EventReceived = null;
            RequestReceived = null;
            PeerDisconnected = null;
        }

        private void RegisterHandlers()
        {
            if (_isServer)
            {
                NetworkServer.RegisterHandler<CoreAiRemoteEventMessage>(OnServerEvent);
                NetworkServer.RegisterHandler<CoreAiRemoteRequestMessage>(OnServerRequest);
                NetworkServer.RegisterHandler<CoreAiRemoteResponseMessage>(OnServerResponse);
                return;
            }

            NetworkClient.RegisterHandler<CoreAiRemoteEventMessage>(OnClientEvent);
            NetworkClient.RegisterHandler<CoreAiRemoteRequestMessage>(OnClientRequest);
            NetworkClient.RegisterHandler<CoreAiRemoteResponseMessage>(OnClientResponse);
        }

        private void UnregisterHandlers()
        {
            if (_isServer)
            {
                NetworkServer.UnregisterHandler<CoreAiRemoteEventMessage>();
                NetworkServer.UnregisterHandler<CoreAiRemoteRequestMessage>();
                NetworkServer.UnregisterHandler<CoreAiRemoteResponseMessage>();
                return;
            }

            NetworkClient.UnregisterHandler<CoreAiRemoteEventMessage>();
            NetworkClient.UnregisterHandler<CoreAiRemoteRequestMessage>();
            NetworkClient.UnregisterHandler<CoreAiRemoteResponseMessage>();
        }

        private void OnServerEvent(NetworkConnectionToClient conn, CoreAiRemoteEventMessage wire)
        {
            ReceiveServerEvent(conn?.connectionId ?? -1, wire);
        }

        /// <summary>
        /// The server's receive path for one event, keyed by connection rather than by a Mirror
        /// object.
        /// </summary>
        /// <remarks>
        /// WHY it takes an id: the sender is resolved from this bridge's own map, so the connection
        /// object itself is never needed — and taking only the id makes the rule ("an unadmitted
        /// connection reaches nothing") provable without standing up a transport, which is the
        /// difference between a rule that is tested and one that is asserted in a comment.
        /// </remarks>
        internal void ReceiveServerEvent(int connectionId, CoreAiRemoteEventMessage wire)
        {
            if (!TryResolveSender(connectionId, out RbxNetworkPeer peer))
            {
                return;
            }

            PacketsDelivered++;
            EventReceived?.Invoke(new RbxNetworkEventMessage(
                new InstanceId(wire.RemoteId),
                RbxNetworkDirection.ClientToServer,
                (RbxNetworkReliability)wire.Reliability,
                peer.ActorId,
                null,
                wire.Payload));
        }

        private void OnClientEvent(CoreAiRemoteEventMessage wire)
        {
            PacketsDelivered++;
            EventReceived?.Invoke(new RbxNetworkEventMessage(
                new InstanceId(wire.RemoteId),
                RbxNetworkDirection.ServerToClient,
                (RbxNetworkReliability)wire.Reliability,
                null,
                null,
                wire.Payload));
        }

        private void OnServerRequest(NetworkConnectionToClient conn, CoreAiRemoteRequestMessage wire)
        {
            ReceiveServerRequest(conn?.connectionId ?? -1, wire);
        }

        /// <summary>The server's receive path for one request, keyed by connection.</summary>
        internal void ReceiveServerRequest(int connectionId, CoreAiRemoteRequestMessage wire)
        {
            if (!TryResolveSender(connectionId, out RbxNetworkPeer peer))
            {
                return;
            }

            PacketsDelivered++;
            uint correlationId = wire.CorrelationId;
            RequestReceived?.Invoke(
                new RbxNetworkRequestMessage(
                    new InstanceId(wire.RemoteId),
                    RbxNetworkDirection.ClientToServer,
                    peer.ActorId,
                    null,
                    wire.Payload),
                new RbxNetworkRequestResponder(result =>
                    RespondTo(connectionId, correlationId, result)));
        }

        private void OnClientRequest(CoreAiRemoteRequestMessage wire)
        {
            PacketsDelivered++;
            uint correlationId = wire.CorrelationId;
            RequestReceived?.Invoke(
                new RbxNetworkRequestMessage(
                    new InstanceId(wire.RemoteId),
                    RbxNetworkDirection.ServerToClient,
                    null,
                    null,
                    wire.Payload),
                new RbxNetworkRequestResponder(result => NetworkClient.Send(
                    ToWire(correlationId, result))));
        }

        private void OnServerResponse(NetworkConnectionToClient conn,
            CoreAiRemoteResponseMessage wire)
        {
            ReceiveServerResponse(conn?.connectionId ?? -1, wire);
        }

        /// <summary>The server's receive path for one response, keyed by connection.</summary>
        internal void ReceiveServerResponse(int connectionId, CoreAiRemoteResponseMessage wire)
        {
            CompleteResponse(connectionId, wire);
        }

        private void OnClientResponse(CoreAiRemoteResponseMessage wire)
        {
            CompleteResponse(connectionId: -1, wire);
        }

        private void CompleteResponse(int connectionId, CoreAiRemoteResponseMessage wire)
        {
            if (!_pending.TryGetValue(wire.CorrelationId, out PendingRequest request))
            {
                // Either a reply to a request that already timed out, or a crafted id. Both are
                // dropped and counted; neither may complete anything.
                OrphanResponsesDropped++;
                return;
            }

            if (connectionId >= 0 && request.ConnectionId != connectionId)
            {
                // A response from a DIFFERENT connection than the one asked. Completing it would let
                // any client answer another client's question.
                OrphanResponsesDropped++;
                return;
            }

            _pending.Remove(wire.CorrelationId);
            PacketsDelivered++;
            request.Complete?.Invoke(wire.Success
                ? RbxNetworkResponse.Success(wire.Payload)
                : RbxNetworkResponse.Failure(wire.ErrorMessage));
        }

        private void RespondTo(int connectionId, uint correlationId, RbxNetworkResponse result)
        {
            if (NetworkServer.connections.TryGetValue(connectionId,
                    out NetworkConnectionToClient conn))
            {
                conn.Send(ToWire(correlationId, result));
            }
        }

        private static CoreAiRemoteResponseMessage ToWire(uint correlationId,
            RbxNetworkResponse result)
        {
            return new CoreAiRemoteResponseMessage
            {
                CorrelationId = correlationId,
                Success = result.Succeeded,
                Payload = result.Payload,
                ErrorCode = result.Succeeded ? "" : "REMOTE_FAILED",
                ErrorMessage = result.Error ?? ""
            };
        }

        private bool TryResolveSender(int connectionId, out RbxNetworkPeer peer)
        {
            if (connectionId >= 0 && _peersByConnection.TryGetValue(connectionId, out peer))
            {
                return true;
            }

            peer = default;
            UnadmittedPacketsDropped++;
            return false;
        }

        private int ResolveConnectionId(RbxNetworkRequestMessage message)
        {
            return !string.IsNullOrEmpty(message.RecipientActorId)
                   && _connectionsByActor.TryGetValue(message.RecipientActorId, out int connectionId)
                ? connectionId
                : -1;
        }

        private void FailPendingFor(int connectionId, string reason)
        {
            List<uint> affected = null;
            foreach (KeyValuePair<uint, PendingRequest> pair in _pending)
            {
                if (pair.Value.ConnectionId == connectionId)
                {
                    affected ??= new List<uint>();
                    affected.Add(pair.Key);
                }
            }

            if (affected == null)
            {
                return;
            }

            for (int index = 0; index < affected.Count; index++)
            {
                PendingRequest request = _pending[affected[index]];
                _pending.Remove(affected[index]);
                request.Complete?.Invoke(RbxNetworkResponse.Failure(reason));
            }
        }

        private void RequirePayloadFits(byte[] payload)
        {
            int length = payload?.Length ?? 0;
            if (length <= MaxPayloadBytes)
            {
                return;
            }

            throw new RbxError(
                RbxErrorCode.PayloadTooLarge,
                "network payload of " + length + " bytes exceeds the transport limit of "
                + MaxPayloadBytes + " bytes",
                "split the payload, or send a reference the receiver can resolve");
        }

        private void Count(byte[] payload)
        {
            PacketsSent++;
            BytesSent += payload?.Length ?? 0;
        }
    }
}
