using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using Mirror;
using UnityEngine;

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
    /// <item><description>
    /// <b>Self binding.</b> On a client, the recipient of every inbound remote is this bridge's own
    /// admitted actor — learned from the server's admission response and bound by the composition
    /// through <see cref="BindAdmittedActor"/>, never read from a packet. A remote that arrives
    /// before that binding exists, or after a disconnect cleared it, is dropped and counted: the
    /// same rule the server applies to an unadmitted connection, seen from the other end.
    /// </description></item>
    /// <item><description>
    /// <b>Broadcast.</b> A server broadcast goes to the admitted connections, not to Mirror's
    /// connection table: a connection that connected and never finished admission sits in that
    /// table until the authentication timeout, and reaches nothing meanwhile.
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
        private readonly Action<string> _log;
        private readonly bool _isServer;
        private string _admittedActorId;
        private uint _nextCorrelationId = 1u;
        private bool _disposed;
        private bool _unadmittedDropLogged;
        private bool _unheardActorLogged;

        /// <summary>The mirror-documented timeout for a RemoteFunction invocation.</summary>
        public const double RequestTimeoutSeconds = 30d;

        /// <summary>
        /// Creates the bridge for one Mirror side, started or not, and registers its handlers there;
        /// see <see cref="AttachHandlers"/> for what a stop and start of that side needs.
        /// </summary>
        public MirrorNetworkBridge(bool isServer, CoreAiMirrorAuthenticator authenticator = null,
            int maxClientRequestsPerSecond = RbxNetworkRateLimiter.DefaultMaxClientRequestsPerSecond,
            Func<double> clockSeconds = null, Action<string> log = null)
        {
            _isServer = isServer;
            _authenticator = authenticator;
            _log = log ?? Debug.LogWarning;
            _clockSeconds = clockSeconds ?? (() => NetworkTime.localTime);
            _rateLimiter = new RbxNetworkRateLimiter(maxClientRequestsPerSecond, _clockSeconds);
            AttachHandlers();
        }

        /// <inheritdoc />
        public RbxNetworkTopology Topology =>
            _isServer ? RbxNetworkTopology.Host : RbxNetworkTopology.Client;

        /// <inheritdoc />
        public IReadOnlyList<string> ActorIds => _actorOrder.AsReadOnly();

        /// <summary>
        /// Packets dropped because their connection was never admitted: a client's packet on the
        /// server, or the server's packet on a client whose own admission is not bound.
        /// </summary>
        public int UnadmittedPacketsDropped { get; private set; }

        /// <summary>
        /// On a client, the actor the server admitted this process as; null before admission and
        /// after a disconnect. Always null on a server.
        /// </summary>
        public string AdmittedActorId => _admittedActorId;

        /// <summary>
        /// Whether <see cref="Dispose"/> ran; a composition that outlives the container owning this
        /// bridge reads it before touching Mirror through it.
        /// </summary>
        public bool IsDisposed => _disposed;

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

        /// <summary>
        /// Binds this client's own admitted actor. Called by the composition when the server's
        /// admission response arrives, never from a packet's own claim.
        /// </summary>
        /// <remarks>
        /// WHY the identity lives on the bridge rather than on each message: the wire envelope
        /// deliberately carries no actor field, and a recipient field the server fills would be a
        /// field the server-side handler has to ignore — one refactor away from being trusted. The
        /// admitted actor is a property of this connection with a lifetime (admission to
        /// disconnect), which is exactly what the server's own connection map records for the
        /// other side; this is its mirror image for the one connection a client has.
        /// </remarks>
        public void BindAdmittedActor(string actorId)
        {
            if (_isServer)
            {
                throw new InvalidOperationException(
                    "a server bridge has no admitted actor of its own; its peers are bound per "
                    + "connection through BindConnection");
            }

            if (string.IsNullOrWhiteSpace(actorId))
            {
                throw new ArgumentException(
                    "an admission that names no actor cannot route a single server remote; the "
                    + "server must send the id it admitted this client as", nameof(actorId));
            }

            _admittedActorId = actorId.Trim();
            _unadmittedDropLogged = false;
            _unheardActorLogged = false;
        }

        /// <summary>Forgets the admitted actor after a disconnect, so a reconnect starts unadmitted.</summary>
        public void ForgetAdmittedActor()
        {
            _admittedActorId = null;
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

            foreach (KeyValuePair<string, int> pair in _connectionsByActor)
            {
                if (NetworkServer.connections.TryGetValue(pair.Value, out NetworkConnectionToClient conn))
                {
                    conn.Send(wire, channel);
                    Count(message.Payload);
                }
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

        /// <summary>
        /// Registers this bridge's message handlers with the Mirror side it was built for. The
        /// constructor does this once; call it again after that side was stopped and started.
        /// </summary>
        /// <remarks>
        /// WHY it exists: <c>NetworkServer.Shutdown</c> and <c>NetworkClient.Shutdown</c> clear every
        /// registered handler and tell nobody, so after a stop and start in one process the same
        /// bridge — which the world caches for its lifetime — would receive nothing and log nothing.
        /// A new bridge is no answer to that: it would carry traffic the world never sees. Idempotent
        /// by construction: on a live Mirror the handlers are replaced by equivalent ones, never
        /// doubled, so calling it on every start is safe. Public rather than internal because any
        /// composition over the public constructor meets the same restart, not only the scene
        /// provider.
        /// </remarks>
        public void AttachHandlers()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(MirrorNetworkBridge),
                    "a disposed bridge must not take Mirror's handlers back from whatever owns them now");
            }

            if (_isServer)
            {
                NetworkServer.ReplaceHandler<CoreAiRemoteEventMessage>(OnServerEvent);
                NetworkServer.ReplaceHandler<CoreAiRemoteRequestMessage>(OnServerRequest);
                NetworkServer.ReplaceHandler<CoreAiRemoteResponseMessage>(OnServerResponse);
                return;
            }

            NetworkClient.ReplaceHandler<CoreAiRemoteEventMessage>(OnClientEvent);
            NetworkClient.ReplaceHandler<CoreAiRemoteRequestMessage>(OnClientRequest);
            NetworkClient.ReplaceHandler<CoreAiRemoteResponseMessage>(OnClientResponse);
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
            _admittedActorId = null;
            EventReceived = null;
            RequestReceived = null;
            PeerDisconnected = null;
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

        /// <summary>
        /// The client's receive path for one event: the recipient is this bridge's admitted actor,
        /// and a broadcast keeps the direction the server sent so the world delivers it to every
        /// local actor the way the loopback does.
        /// </summary>
        private void OnClientEvent(CoreAiRemoteEventMessage wire)
        {
            if (!TryResolveSelf(out string self))
            {
                return;
            }

            PacketsDelivered++;
            bool broadcast = wire.Direction == (byte)RbxNetworkDirection.ServerToAllClients;
            EventReceived?.Invoke(new RbxNetworkEventMessage(
                new InstanceId(wire.RemoteId),
                broadcast ? RbxNetworkDirection.ServerToAllClients : RbxNetworkDirection.ServerToClient,
                (RbxNetworkReliability)wire.Reliability,
                null,
                broadcast ? null : self,
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
            if (!TryResolveSelf(out string self))
            {
                return;
            }

            PacketsDelivered++;
            uint correlationId = wire.CorrelationId;
            RequestReceived?.Invoke(
                new RbxNetworkRequestMessage(
                    new InstanceId(wire.RemoteId),
                    RbxNetworkDirection.ServerToClient,
                    null,
                    self,
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

        /// <summary>
        /// Resolves the admitted actor an inbound server remote is for, or drops the packet.
        /// </summary>
        /// <remarks>
        /// WHY drop rather than throw or pass null: Mirror disconnects a client whose handler
        /// throws, so throwing would let a wiring gap on this side kick the client on every
        /// reconnect; passing null is what the world refuses with an exception, which is the same
        /// kick one layer later. A packet here without a binding means the server sent a remote
        /// before its admission response, or this composition never bound one — either way the
        /// count and the one-time line are what an operator needs, and a flood of them is not.
        /// </remarks>
        private bool TryResolveSelf(out string actorId)
        {
            actorId = _admittedActorId;
            if (!string.IsNullOrEmpty(actorId))
            {
                WarnOnceIfUnheard(actorId);
                return true;
            }

            UnadmittedPacketsDropped++;
            if (!_unadmittedDropLogged)
            {
                _unadmittedDropLogged = true;
                _log("[CoreAI.Mirror] a server remote arrived before this client's admission was "
                     + "bound (or after a disconnect cleared it); it and any that follow are dropped "
                     + "and counted until the admission response binds the actor");
            }

            return false;
        }

        /// <summary>
        /// Says once when the admitted actor is not one the local world registered, because a
        /// FireClient delivered to that id would fire a signal no local script holds.
        /// </summary>
        /// <remarks>
        /// WHY only a warning: the mismatch is a host composition error — the client's identity
        /// provider and the server's admission provider disagree on the durable actor id — and the
        /// packet is still delivered as addressed, so the world's own diagnostics stay truthful.
        /// </remarks>
        private void WarnOnceIfUnheard(string actorId)
        {
            if (_unheardActorLogged || _actorOrder.Count == 0 || _actorOrder.Contains(actorId))
            {
                return;
            }

            _unheardActorLogged = true;
            _log("[CoreAI.Mirror] the server admitted this client as '" + actorId
                 + "' but the local world registered [" + string.Join(", ", _actorOrder)
                 + "]; server-to-client remotes will reach no local script until the client's "
                 + "IActorIdentityProvider issues the same durable actor id the server admits");
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
