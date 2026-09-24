using System;
using System.Collections.Generic;
using System.Text;
using CoreAI.Mods.Rbx.Datatypes;
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
    /// <b>One connection per actor, newest wins.</b> Binding an actor that already holds a
    /// connection closes the older one: its binding, open requests and admission record are
    /// released at once, the older client is told why, and the transport drops it on the first
    /// <see cref="Pump"/> of a later frame, the way Roblox ends the older session of a player who
    /// joins again. Every teardown is keyed by the connection, so the late report of that older
    /// drop finds nothing and cannot tear down the session that replaced it.
    /// </description></item>
    /// <item><description>
    /// <b>A reason before every server-side close.</b> A kick and a superseded session send the
    /// client a <see cref="CoreAiDisconnectNoticeMessage"/>, and the transport drop waits for a
    /// later frame so Mirror flushes the notice first; the session is torn down and unbound at
    /// once. A kick on a client is the client's own player leaving: the client disconnects.
    /// </description></item>
    /// <item><description>
    /// <b>Readiness.</b> A connection admitted through the session host is joining until its client
    /// sends <see cref="CoreAiClientReadyMessage"/>: reliable server remotes addressed to it are held
    /// in order (bounded) and sent when the acknowledgement arrives, unreliable ones are dropped
    /// and counted, and a connection that never acknowledges is dropped at
    /// <see cref="ReadinessTimeoutSeconds"/>. What the client sends is heard at once.
    /// </description></item>
    /// <item><description>
    /// <b>Server clock.</b> An acknowledged connection is sent the server's Unix time, then again
    /// every <see cref="ClockAnchorIntervalSeconds"/>; a client derives
    /// <see cref="ServerClockOffsetSeconds"/> from those anchors alone.
    /// </description></item>
    /// <item><description>
    /// <b>Correlation.</b> A response completes a request only when the connection AND the
    /// correlation id both match an open entry. A late response after the timeout is counted and
    /// dropped, so a slow answer cannot resolve a call that already failed.
    /// </description></item>
    /// <item><description>
    /// <b>Budget, on the sending side.</b> Client-to-server traffic is charged against the shared
    /// <see cref="RbxNetworkRateLimiter"/> where it is SENT: a client bridge charges its own
    /// FireServer and InvokeServer, and a server bridge charges the in-process traffic of its
    /// host-local actors. What a server RECEIVES from a remote client is not budgeted yet; that is
    /// a known limit, and this rule is no defence against a hostile client.
    /// </description></item>
    /// <item><description>
    /// <b>Client-to-server traffic never leaves a server.</b> On a server, a ClientToServer call
    /// comes from an actor running in this process — a restricted actor's mod on the host — and it
    /// is delivered here, in process, the way the loopback bridge delivers it. Likewise a server
    /// remote addressed to a registered actor that holds no connection is delivered in process.
    /// Nothing addressed to nobody is dropped silently: it is counted.
    /// </description></item>
    /// <item><description>
    /// <b>Self binding.</b> On a client, the recipient of every inbound remote is this bridge's own
    /// admitted actor — learned from the server's admission response and bound by the composition
    /// through <see cref="BindAdmittedActor"/>, never read from a packet. A remote that arrives
    /// before that binding exists, or after a disconnect cleared it, is dropped and counted: the
    /// same rule the server applies to an unadmitted connection, seen from the other end. That rule
    /// is this bridge's own and not Mirror's authentication flag, so the client handlers do not
    /// require Mirror authentication: an unreliable remote that overtakes the reliable admission
    /// response is dropped and counted instead of making Mirror disconnect the joining client.
    /// </description></item>
    /// <item><description>
    /// <b>Broadcast.</b> A server broadcast goes to the admitted connections, not to Mirror's
    /// connection table: a connection that connected and never finished admission sits in that
    /// table until the authenticator's admission deadline drops it
    /// (<see cref="CoreAiMirrorAuthenticator.AdmissionTimeoutSeconds"/>; Mirror itself has no
    /// authentication timeout), and reaches nothing meanwhile.
    /// </description></item>
    /// <item><description>
    /// <b>Size.</b> The payload ceiling is per channel and read from the transport: a reliable
    /// remote or RemoteFunction carries up to the codec's 64 KiB when the transport's reliable
    /// channel fits it, an unreliable remote up to Roblox's 1000 bytes when the unreliable channel
    /// fits that. A payload over its ceiling is refused before anything is counted, so nothing
    /// counted as sent is one Mirror then drops for its size.
    /// </description></item>
    /// <item><description>
    /// <b>Malformed envelopes.</b> A remote id that is not a server-assigned instance id, a
    /// reliability byte outside the enum, or a server-bound envelope that claims another direction
    /// is dropped and counted, never thrown inside Mirror's handler.
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
            public bool HasConnection;
            public int ConnectionId;
            public Action<RbxNetworkResponse> Complete;
            public double DeadlineSeconds;
        }

        /// <summary>A reliable envelope held for a connection that has not acknowledged readiness.</summary>
        private sealed class HeldSend
        {
            public bool IsRequest;
            public CoreAiRemoteEventMessage Event;
            public CoreAiRemoteRequestMessage Request;
            public byte[] Payload;
        }

        /// <summary>A bound connection whose client has not acknowledged readiness yet.</summary>
        private sealed class JoiningConnection
        {
            public double BoundAt;
            public readonly List<HeldSend> Held = new();
            public long HeldBytes;
            public bool OverflowLogged;
        }

        /// <summary>A transport drop owed to a connection whose notice must be flushed first.</summary>
        private readonly struct OwedDrop
        {
            public OwedDrop(NetworkConnectionToClient connection, double requestedAt)
            {
                Connection = connection;
                RequestedAt = requestedAt;
            }

            public NetworkConnectionToClient Connection { get; }

            public double RequestedAt { get; }
        }

        /// <summary>The mirror-documented timeout for a RemoteFunction invocation.</summary>
        public const double RequestTimeoutSeconds = 30d;

        /// <summary>
        /// Seconds a connection admitted through the session host has, from its binding, to
        /// acknowledge readiness before it is dropped.
        /// </summary>
        /// <remarks>
        /// WHY a deadline: a client older than the readiness handshake never acknowledges, and
        /// without one it would stay a Player that receives nothing, for ever, in silence. Ten
        /// seconds matches the admission deadline; a current client acknowledges one round trip
        /// after its admission.
        /// </remarks>
        public const double ReadinessTimeoutSeconds = 10d;

        /// <summary>
        /// Reliable envelopes held at most for one joining connection; the next is dropped and counted.
        /// </summary>
        public const int MaxHeldMessagesPerJoiningConnection = 256;

        /// <summary>
        /// Payload bytes held at most for one joining connection; an envelope past it is dropped and
        /// counted.
        /// </summary>
        public const int MaxHeldBytesPerJoiningConnection = 262144;

        /// <summary>
        /// Seconds a client waits before acknowledging readiness again on a connection whose server
        /// has not answered with a clock anchor yet.
        /// </summary>
        /// <remarks>
        /// WHY it repeats: a server that admitted the connection before its world attached had no
        /// binding to record the first acknowledgement against, and binds it later; one message
        /// per second until the answer costs nothing and spares that client the readiness deadline.
        /// </remarks>
        public const double ReadyAcknowledgementRetrySeconds = 1d;

        /// <summary>Seconds between the clock anchors a server sends every acknowledged connection.</summary>
        public const double ClockAnchorIntervalSeconds = 5d;

        /// <summary>
        /// A clock anchor that disagrees with a client's estimate by more than this many seconds
        /// replaces the estimate outright instead of being blended in: the server's clock stepped,
        /// or the client joined another server.
        /// </summary>
        public const double ClockStepThresholdSeconds = 1d;

        /// <summary>
        /// What a kicked client is told when the kick gave no message of its own.
        /// </summary>
        public const string DefaultKickMessage = "You were kicked from this experience.";

        /// <summary>What the client of a superseded connection is told.</summary>
        public const string SupersededNoticeMessage =
            "This session was replaced by a newer connection for the same player.";

        /// <summary>The UTF-8 bytes of notice text sent at most; a longer kick message is cut.</summary>
        public const int MaxNoticeMessageBytes = 1024;

        /// <summary>
        /// How much of the gap between an anchor and the current estimate one anchor closes, below
        /// <see cref="ClockStepThresholdSeconds"/>.
        /// </summary>
        private const double ClockSmoothing = 0.25d;

        /// <summary>
        /// The codec's own ceiling: the bound of every reliable remote and RemoteFunction payload,
        /// and the whole bound when no transport is active.
        /// </summary>
        public const int CodecPayloadCeilingBytes = 65536;

        /// <summary>
        /// Roblox's ceiling for an <c>UnreliableRemoteEvent</c> payload; Roblox drops a larger one,
        /// this bridge refuses it where it is fired.
        /// </summary>
        public const int UnreliablePayloadCeilingBytes = 1000;

        /// <summary>
        /// Wire bytes of an event envelope besides its payload: RemoteId, Direction, Reliability.
        /// </summary>
        private const int EventEnvelopeBytes = 8 + 1 + 1;

        /// <summary>
        /// Wire bytes of a request envelope besides its payload: RemoteId, Direction, CorrelationId.
        /// </summary>
        private const int RequestEnvelopeBytes = 8 + 1 + 4;

        /// <summary>
        /// Wire bytes of a response envelope besides its payload and strings: CorrelationId, Success.
        /// </summary>
        private const int ResponseEnvelopeBytes = 4 + 1;

        /// <summary>Mirror's length header in front of every string.</summary>
        private const int StringHeaderBytes = 2;

        private const string ResponseErrorCode = "REMOTE_FAILED";
        private const string PeerDisconnectedReason = "the peer disconnected";
        private const string NotConnectedReason = "the client is not connected to a server";
        private const string DisposedReason = "the network bridge was disposed before the remote answered";
        private const string SupersededReason =
            "the actor's session was replaced by a newer connection for the same actor";

        private readonly Dictionary<int, RbxNetworkPeer> _peersByConnection = new();
        private readonly Dictionary<string, int> _connectionsByActor = new(StringComparer.Ordinal);
        private readonly Dictionary<uint, PendingRequest> _pending = new();
        private readonly List<string> _actorOrder = new();
        private readonly Queue<RbxNetworkEventMessage> _localEvents = new();
        private readonly Dictionary<int, JoiningConnection> _joining = new();
        private readonly HashSet<int> _acknowledged = new();
        private readonly List<OwedDrop> _owedDrops = new();
        private readonly RbxNetworkRateLimiter _rateLimiter;
        private readonly Func<double> _clockSeconds;
        private readonly IRbxClockSource _wallClock;
        private readonly CoreAiMirrorAuthenticator _authenticator;
        private readonly Action<string> _log;
        private readonly bool _isServer;
        private string _admittedActorId;
        private NetworkConnectionToServer _readyAcknowledgedOn;
        private NetworkConnectionToServer _anchoredOn;
        private uint _nextCorrelationId = 1u;
        private double _nextReadyAcknowledgementAt;
        private double _nextClockAnchorAt;
        private double _serverUnixAtMonotonicZero;
        private bool _clockAnchored;
        private bool _disposed;
        private bool _deliveringLocally;
        private bool _unadmittedDropLogged;
        private bool _unheardActorLogged;
        private bool _unsentDropLogged;
        private bool _malformedDropLogged;

        /// <summary>
        /// Creates the bridge for one Mirror side, started or not, and registers its handlers there;
        /// see <see cref="AttachHandlers"/> for what a stop and start of that side needs.
        /// </summary>
        /// <param name="isServer">Which Mirror side this bridge serves; fixed for its lifetime.</param>
        /// <param name="authenticator">The admission authenticator whose records a released connection forgets.</param>
        /// <param name="maxClientRequestsPerSecond">The per-actor budget of client-to-server traffic.</param>
        /// <param name="clockSeconds">
        /// The frame clock request timeouts, readiness deadlines, owed drops and the anchor interval
        /// run on; null means Mirror's <c>NetworkTime.localTime</c>, which is fixed within a frame.
        /// </param>
        /// <param name="log">Where the bridge's one-time operator lines go; null means a Unity warning.</param>
        /// <param name="wallClock">
        /// The clock server time is measured against: its Unix time is what a server's anchors carry
        /// and what a client's <see cref="ServerClockOffsetSeconds"/> is relative to, and its process
        /// time carries a client's estimate between anchors. Null means the system clock — the one a
        /// world composed without its own <see cref="IRbxClockSource"/> reads; a world that reads
        /// another clock composes its bridge with that same clock.
        /// </param>
        public MirrorNetworkBridge(bool isServer, CoreAiMirrorAuthenticator authenticator = null,
            int maxClientRequestsPerSecond = RbxNetworkRateLimiter.DefaultMaxClientRequestsPerSecond,
            Func<double> clockSeconds = null, Action<string> log = null, IRbxClockSource wallClock = null)
        {
            _isServer = isServer;
            _authenticator = authenticator;
            _log = log ?? Debug.LogWarning;
            _clockSeconds = clockSeconds ?? (() => NetworkTime.localTime);
            _wallClock = wallClock ?? new RbxSystemClockSource();
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
        /// Packets dropped because the envelope was malformed: from an admitted peer, a remote id
        /// that is not a server-assigned instance id, a reliability byte outside the enum, or a
        /// server-bound envelope claiming another direction; from a server, a clock anchor that is
        /// not a positive finite time. Never delivered, never thrown.
        /// </summary>
        public int MalformedPacketsDropped { get; private set; }

        /// <summary>
        /// Server remotes that reached nobody: addressed to an actor that holds neither a live
        /// connection nor a registration in this process. Counted instead of vanishing.
        /// </summary>
        public int UnroutablePacketsDropped { get; private set; }

        /// <summary>
        /// Envelopes a server delivered in process, to or from an actor running on this host,
        /// without handing anything to the transport.
        /// </summary>
        public int LocalDeliveries { get; private set; }

        /// <summary>
        /// RemoteFunction answers not sent because the connection that asked is gone or now speaks
        /// for someone else: an answer outliving its connection never reaches a stranger on the
        /// reused id.
        /// </summary>
        public int StaleResponsesDropped { get; private set; }

        /// <summary>
        /// RemoteFunction answers too large for one reliable message on this transport, sent as a
        /// failure that names the size instead of being dropped by Mirror.
        /// </summary>
        public int OversizeResponsesFailed { get; private set; }

        /// <summary>
        /// Connections closed because the same actor was bound on a newer one (newest wins).
        /// </summary>
        public int SupersededConnections { get; private set; }

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

        /// <summary>
        /// Envelopes a client dropped instead of handing to the transport because it was not
        /// connected; never counted as sent. Always zero on a server.
        /// </summary>
        public int UnsentPacketsDropped { get; private set; }

        /// <summary>Requests that reached the timeout without an answer.</summary>
        public int TimedOutRequests { get; private set; }

        /// <summary>Envelopes handed to the transport.</summary>
        public int PacketsSent { get; private set; }

        /// <summary>Envelopes delivered out of the transport.</summary>
        public int PacketsDelivered { get; private set; }

        /// <summary>Payload bytes handed to the transport.</summary>
        public long BytesSent { get; private set; }

        /// <summary>Readiness acknowledgements a server accepted, one per acknowledged connection.</summary>
        public int ReadyAcknowledgements { get; private set; }

        /// <summary>
        /// Reliable envelopes a server held for a connection that had not acknowledged readiness,
        /// whether they were later sent or not.
        /// </summary>
        public int PacketsHeldUntilReady { get; private set; }

        /// <summary>
        /// Envelopes a server never sent because their connection had not acknowledged readiness: an
        /// unreliable remote, a held one past <see cref="MaxHeldMessagesPerJoiningConnection"/> or
        /// <see cref="MaxHeldBytesPerJoiningConnection"/>, and the held ones of a connection that left
        /// before acknowledging.
        /// </summary>
        public int NotReadyPacketsDropped { get; private set; }

        /// <summary>Connections a server dropped for not acknowledging readiness in time.</summary>
        public int ReadinessTimeouts { get; private set; }

        /// <summary>Clock anchors a server handed the transport.</summary>
        public int ClockAnchorsSent { get; private set; }

        /// <summary>Clock anchors a client accepted.</summary>
        public int ClockAnchorsReceived { get; private set; }

        /// <summary>Kick and supersede notices a server handed the transport.</summary>
        public int DisconnectNoticesSent { get; private set; }

        /// <summary>Times a client disconnected itself because its own player was kicked.</summary>
        public int SelfKicks { get; private set; }

        /// <summary>
        /// On a client, the last reason a server gave for closing this client's connection; null
        /// until one arrives. Kept after the disconnect, so the host can show it, and cleared by the
        /// next admission.
        /// </summary>
        public CoreAiDisconnectNoticeMessage? LastDisconnectNotice { get; private set; }

        /// <summary>
        /// Whether <see cref="ServerClockOffsetSeconds"/> measures the server's clock: always on a
        /// server, and on a client from the first clock anchor on. Before that a client's offset is
        /// zero because nothing is known, not because the clocks agree.
        /// </summary>
        public bool IsServerClockSynchronized => _isServer || _clockAnchored;

        /// <summary>
        /// Test seam: the round trip in seconds a client's anchor is corrected by half of; null means
        /// Mirror's measured <c>NetworkTime.rtt</c>.
        /// </summary>
        internal Func<double> RoundTripSeconds { get; set; }

        /// <inheritdoc />
        /// <remarks>
        /// The ceiling of every reliable remote: a reliable <c>RemoteEvent</c> and both
        /// <c>RemoteFunction</c> directions. An <c>UnreliableRemoteEvent</c> has the tighter
        /// <see cref="MaxPayloadBytesFor"/> of its own. Read from the transport at runtime rather
        /// than assumed: KCP, WebSockets and a LAN transport do not agree on a packet size.
        /// </remarks>
        public int MaxPayloadBytes
        {
            get
            {
                int events = MaxPayloadBytesFor(RbxNetworkReliability.ReliableOrdered);
                int requests = MaxRequestPayloadBytes;
                return events < requests ? events : requests;
            }
        }

        /// <summary>
        /// The largest RemoteFunction payload, either direction: the codec's ceiling, or less when
        /// the transport's reliable channel cannot carry that in one message.
        /// </summary>
        public int MaxRequestPayloadBytes =>
            Math.Min(CodecPayloadCeilingBytes, LargestFittingPayload(Channels.Reliable, RequestEnvelopeBytes));

        /// <inheritdoc />
        /// <remarks>
        /// The contract: the wall clock's Unix time plus this value is the server's Unix time now,
        /// where the wall clock is the one this bridge was built with (the system clock by
        /// default). Zero on a server. On a client, zero until the first
        /// <see cref="CoreAiServerClockMessage"/> arrives — <see cref="IsServerClockSynchronized"/>
        /// says which — and from then on
        /// <c>anchor + (process time now − process time at the anchor) − wall clock now</c>, the
        /// anchor being the server's Unix time corrected by half the measured round trip. The value
        /// moves: at the first anchor it steps from zero to the whole skew between the two wall
        /// clocks, which can be hours; the first anchor of every later connection replaces the
        /// estimate, as does one farther than <see cref="ClockStepThresholdSeconds"/> from it, and
        /// a nearer one is blended in; and between anchors it follows the client's own wall clock,
        /// so a client whose clock is corrected mid-session still reads the server's time. A
        /// consumer that keeps the derived clock monotonic must treat the first synchronization as
        /// a re-base, not as time going backwards.
        /// WHY not Mirror's <c>NetworkTime.offset</c>: it is this process's uptime minus the
        /// server's interpolated uptime — no wall clock at all, and the opposite sign — so a client
        /// of a server that had run for a day read the server's time a day off, and a clamp on the
        /// result froze for as long. WHY not Mirror's predicted time either: before the first ping
        /// returns it is the client's own uptime, the same error again.
        /// </remarks>
        public double ServerClockOffsetSeconds
        {
            get
            {
                if (_isServer || !_clockAnchored)
                {
                    return 0d;
                }

                return _serverUnixAtMonotonicZero + _wallClock.ProcessTimeSeconds
                       - _wallClock.UnixTimeSecondsFractional;
            }
        }

        /// <inheritdoc />
        public event Action<RbxNetworkEventMessage> EventReceived;

        /// <inheritdoc />
        public event Action<RbxNetworkRequestMessage, RbxNetworkRequestResponder> RequestReceived;

        /// <inheritdoc />
        public event Action<RbxNetworkPeerDisconnected> PeerDisconnected;

        /// <summary>
        /// On a client, raised when the server says why it is about to close this connection — a
        /// kick, with the kick's message, or a newer session of the same player. The drop follows;
        /// a host that shows the player why subscribes here or reads <see cref="LastDisconnectNotice"/>.
        /// </summary>
        public event Action<CoreAiDisconnectNoticeMessage> DisconnectNoticeReceived;

        /// <summary>
        /// The largest <c>RemoteEvent</c> payload one delivery class carries on this transport.
        /// </summary>
        /// <remarks>
        /// WHY per channel: kcp2k's unreliable channel is bounded by one datagram (1194 bytes) while
        /// its reliable channel carries hundreds of kilobytes, so a single smallest-of-both ceiling
        /// would refuse every reliable remote and RemoteFunction above about 1.2 KB — a leaderboard
        /// that works in solo would fail online. WHY Roblox's 1000 bytes for unreliable even where the
        /// transport would carry more: that is the contract a script written for Roblox is sized to.
        /// </remarks>
        public int MaxPayloadBytesFor(RbxNetworkReliability reliability)
        {
            bool unreliable = reliability == RbxNetworkReliability.UnreliableUnordered;
            int ceiling = unreliable ? UnreliablePayloadCeilingBytes : CodecPayloadCeilingBytes;
            int transport = LargestFittingPayload(
                unreliable ? Channels.Unreliable : Channels.Reliable, EventEnvelopeBytes);
            return ceiling < transport ? ceiling : transport;
        }

        /// <summary>
        /// Binds an admitted connection to its actor as a peer that already listens: server remotes
        /// reach it at once. Called by a composition that knows its client is ready, never by this
        /// bridge on a first packet; the session host binds through the overload that waits for the
        /// client's readiness.
        /// </summary>
        public void BindConnection(int connectionId, RbxNetworkPeer peer)
        {
            BindConnection(connectionId, peer, awaitClientReady: false);
        }

        /// <summary>
        /// Binds an admitted connection to its actor. Called by the composition after admission,
        /// never by this bridge on a first packet. With <paramref name="awaitClientReady"/> the
        /// connection is joining until its client sends <see cref="CoreAiClientReadyMessage"/>.
        /// </summary>
        /// <remarks>
        /// WHY an actor that already holds another connection loses it: that is a player who joined
        /// again before the transport noticed the old link was gone — kcp2k keeps a dead peer for
        /// its whole timeout — and Roblox ends the older session. Keeping both would let the old
        /// connection's late drop tear down the player the new one is using. The older connection is
        /// released here, told why, and dropped at the transport on a later frame's
        /// <see cref="Pump"/>; the reason also goes to the host's log and to the older connection's
        /// open requests. WHY a joining connection is not sent remotes yet: the admission response
        /// reaches the client one round trip after this binding, and until the client has processed
        /// it — and built its bridge, which a composition may do later — a remote finds nothing
        /// that can route it, or no handler at all, which makes Mirror disconnect the client.
        /// </remarks>
        public void BindConnection(int connectionId, RbxNetworkPeer peer, bool awaitClientReady)
        {
            if (string.IsNullOrEmpty(peer.ActorId))
            {
                throw new ArgumentException(
                    "a connection must be bound to the actor its admission named", nameof(peer));
            }

            if (_peersByConnection.TryGetValue(connectionId, out RbxNetworkPeer current)
                && !string.Equals(current.ActorId, peer.ActorId, StringComparison.Ordinal))
            {
                if (_connectionsByActor.TryGetValue(current.ActorId, out int mapped)
                    && mapped == connectionId)
                {
                    _connectionsByActor.Remove(current.ActorId);
                }

                FailPendingFor(connectionId, "the connection now speaks for another actor");
            }

            if (_connectionsByActor.TryGetValue(peer.ActorId, out int older) && older != connectionId)
            {
                Supersede(older, connectionId, peer.ActorId);
            }

            _peersByConnection[connectionId] = peer;
            _connectionsByActor[peer.ActorId] = connectionId;
            if (awaitClientReady && !_acknowledged.Contains(connectionId)
                                 && !_joining.ContainsKey(connectionId))
            {
                _joining[connectionId] = new JoiningConnection { BoundAt = _clockSeconds() };
            }
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
        /// other side; this is its mirror image for the one connection a client has. WHY the
        /// readiness acknowledgement leaves from here: this is the first moment this client can
        /// route a server remote, and the server holds them until it hears so; a binding made while
        /// the client is not connected yet is acknowledged by the first <see cref="Pump"/> that
        /// finds it connected.
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
            LastDisconnectNotice = null;
            AcknowledgeReadinessIfDue();
        }

        /// <summary>
        /// Forgets the admitted actor after a disconnect, so a reconnect starts unadmitted, and fails
        /// every request this client still waits on.
        /// </summary>
        /// <remarks>
        /// WHY the requests fail here: they left on the connection that is gone, and no answer can
        /// come back on another one — waiting out the timeout would be thirty seconds of a lie.
        /// WHY the clock estimate is kept: it is carried on this machine's monotonic clock and stays
        /// the best one there is until the next connection's first anchor replaces it; dropping it
        /// would step every derived clock back by the whole skew at each disconnect.
        /// </remarks>
        public void ForgetAdmittedActor()
        {
            _admittedActorId = null;
            _readyAcknowledgedOn = null;
            if (!_isServer)
            {
                FailAllPending(NotConnectedReason, containCompletionFailures: false);
            }
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
        /// <remarks>
        /// An actor holds at most one connection here (see
        /// <see cref="BindConnection(int, RbxNetworkPeer, bool)"/>), so the
        /// connection released is the one this actor holds now, never an older or a newer one.
        /// </remarks>
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
                ReleaseBinding(connectionId, PeerDisconnectedReason);
            }
        }

        /// <summary>Reports a transport-level disconnect so the world can tear the actor down.</summary>
        /// <remarks>
        /// WHY keyed by the connection and released here too: the teardown listener may be absent
        /// or may throw, and a connection that is gone must stop resolving to its actor either way.
        /// Only this connection's binding is released — a connection that was already replaced by a
        /// newer one for the same actor was unbound when it was replaced, so its late report finds
        /// nothing here and cannot reach the session that replaced it.
        /// </remarks>
        public void NotifyDisconnected(int connectionId, RbxNetworkDisconnectReason reason)
        {
            if (!_peersByConnection.TryGetValue(connectionId, out RbxNetworkPeer peer))
            {
                return;
            }

            try
            {
                FailPendingFor(connectionId, PeerDisconnectedReason);
                PeerDisconnected?.Invoke(new RbxNetworkPeerDisconnected(peer, reason));
            }
            finally
            {
                if (_peersByConnection.TryGetValue(connectionId, out RbxNetworkPeer still)
                    && SamePeer(still, peer))
                {
                    ReleaseConnection(connectionId, PeerDisconnectedReason);
                }
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// On a server this is <see cref="DisconnectActor(string, string)"/> with
        /// <see cref="DefaultKickMessage"/>. On a client it is the client's own player being kicked
        /// — a LocalScript's <c>Players.LocalPlayer:Kick()</c> — and the client disconnects from the
        /// server, as Roblox does; a client has no authority over anyone else's connection, so any
        /// other actor is nothing here.
        /// </remarks>
        public void DisconnectActor(string actorId)
        {
            if (!_isServer)
            {
                DisconnectSelf(actorId);
                return;
            }

            DisconnectActor(actorId, DefaultKickMessage);
        }

        /// <summary>
        /// Kicks one admitted actor from the server side: the client is told
        /// <paramref name="message"/> first, the session is torn down as
        /// <see cref="RbxNetworkDisconnectReason.ServerClosed"/> and unbound at once, and the
        /// transport drops the connection on a later frame's <see cref="Pump"/>. Nothing happens for
        /// an actor that holds no connection here. On a client this is the self-kick of
        /// <see cref="DisconnectActor(string)"/>, and the message is not sent anywhere.
        /// </summary>
        /// <remarks>
        /// WHY the teardown runs here, before the transport is asked: kcp2k reports the drop
        /// before ServerDisconnect returns and another transport reports it later, so a teardown
        /// left to that report would run as TransportLost, or on a binding this bridge had by then
        /// forgotten — run here it runs once, as ServerClosed, and the transport's own report then
        /// finds no binding and does nothing. WHY the binding is released even when nobody listened
        /// for the peer: a kicked connection that still resolved to its actor would deliver that
        /// client's next packet as the player the world just removed. WHY the release and the
        /// owed drop are in a finally: the teardown behind that report is the world's, and mod code
        /// runs inside it; a throw out of there must not leave the socket open on a connection
        /// that is authenticated to Mirror and bound to nobody. The throw itself stays the
        /// caller's to report — a kick that failed halfway is not made to look whole. WHY the drop
        /// is owed rather than made: Mirror discards a connection's unflushed messages when it is
        /// dropped, and flushes only at the end of the frame, so the notice leaves only if the drop
        /// waits for a later frame; until then the connection is bound to nobody and every packet
        /// from it is dropped as unadmitted. WHY the notice goes even to a joining connection: a
        /// kick at join — a ban check in <c>PlayerAdded</c> — is the common case, and a current
        /// client hears the notice after its admission response on the same ordered channel.
        /// </remarks>
        public void DisconnectActor(string actorId, string message)
        {
            if (!_isServer)
            {
                DisconnectSelf(actorId);
                return;
            }

            if (string.IsNullOrEmpty(actorId)
                || !_connectionsByActor.TryGetValue(actorId, out int connectionId))
            {
                return;
            }

            NetworkServer.connections.TryGetValue(connectionId, out NetworkConnectionToClient conn);
            SendNotice(conn, CoreAiDisconnectNoticeKind.Kicked,
                string.IsNullOrWhiteSpace(message) ? DefaultKickMessage : message);
            try
            {
                NotifyDisconnected(connectionId, RbxNetworkDisconnectReason.ServerClosed);
            }
            finally
            {
                ReleaseConnection(connectionId, PeerDisconnectedReason);
                if (!_connectionsByActor.ContainsKey(actorId))
                {
                    _actorOrder.Remove(actorId);
                    _rateLimiter.Forget(actorId);
                }

                OweDrop(conn);
            }
        }

        /// <summary>
        /// Fails every expired request, and on a server performs the drops owed from an earlier
        /// frame, drops joining connections past <see cref="ReadinessTimeoutSeconds"/> and sends the
        /// interval's clock anchors; on a client it acknowledges readiness if a binding still owes
        /// that. Call once per frame; the scene provider does.
        /// </summary>
        public void Pump()
        {
            if (_disposed)
            {
                return;
            }

            PumpTimeouts();
            if (!_isServer)
            {
                AcknowledgeReadinessIfDue();
                return;
            }

            double now = _clockSeconds();
            PerformOwedDrops(now, all: false);
            DropConnectionsPastTheReadinessDeadline(now);
            SendDueClockAnchors(now);
        }

        /// <summary>
        /// Performs every transport drop a kick or a supersede still owes, now, without waiting for a
        /// later frame: for a composition that stops pumping, such as a disabled provider.
        /// </summary>
        /// <remarks>
        /// WHY it exists: an owed drop is a socket still open on a connection bound to nobody, and it
        /// must not stay open for as long as nothing pumps. A notice queued in this same frame may be
        /// lost by the drop; the connection is closed either way.
        /// </remarks>
        public void PerformOwedDropsNow()
        {
            PerformOwedDrops(0d, all: true);
        }

        /// <inheritdoc />
        /// <remarks>
        /// WHY a client's connection is checked before its budget: a remote fired while
        /// disconnected is documented as dropped and counted, never sent — and a drop that was
        /// still charged to the budget would answer the fire after the budget's last one with a
        /// rate-limit error, for a packet that never left. One state, one outcome. WHY a server
        /// never puts ClientToServer on the wire: on a server that direction can only come from an
        /// actor in this process, and the wire would carry it to every remote client as though the
        /// server had fired at them — a leak, and a remote the server's own scripts never hear.
        /// </remarks>
        public void SendEvent(RbxNetworkEventMessage message)
        {
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            bool unreliable = message.Reliability == RbxNetworkReliability.UnreliableUnordered;
            RequirePayloadFits(message.Payload, MaxPayloadBytesFor(message.Reliability),
                unreliable ? "an UnreliableRemoteEvent" : "a reliable RemoteEvent",
                unreliable
                    ? "fire a reliable RemoteEvent for data this large, or split it"
                    : "split the payload, or send a reference the receiver can resolve");
            if (!_isServer && !NetworkClient.isConnected)
            {
                DropUnsent();
                return;
            }

            RbxNetworkRateGroup group = unreliable
                ? RbxNetworkRateGroup.UnreliableRemoteEvent
                : RbxNetworkRateGroup.ReliableRemoteEvent;
            if (message.Direction == RbxNetworkDirection.ClientToServer)
            {
                if (_isServer)
                {
                    RequireLocalSender(message.SenderActorId, "send to the server");
                }

                _rateLimiter.Admit(message.SenderActorId, group);
            }

            int channel = unreliable ? Channels.Unreliable : Channels.Reliable;
            if (!_isServer)
            {
                NetworkClient.Send(ToWire(message), channel);
                CountClientSend(message.Payload);
                return;
            }

            switch (message.Direction)
            {
                case RbxNetworkDirection.ClientToServer:
                    DeliverEventLocally(message);
                    return;
                case RbxNetworkDirection.ServerToClient:
                    SendEventTo(message, channel);
                    return;
                case RbxNetworkDirection.ServerToAllClients:
                    BroadcastEvent(message, channel);
                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(message), message.Direction,
                        "a remote event travels in one of the three Roblox directions");
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// The connection is checked before the budget for the reason <see cref="SendEvent"/>
        /// gives: a call that never leaves is a drop, not a budget entry. On a server, a
        /// ClientToServer invocation and one addressed to a registered actor without a connection
        /// are answered in process, as the loopback answers them, instead of waiting out a timeout
        /// for a packet that was never sent.
        /// </remarks>
        public void SendRequest(RbxNetworkRequestMessage message,
            Action<RbxNetworkResponse> response)
        {
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            RequirePayloadFits(message.Payload, MaxRequestPayloadBytes, "a RemoteFunction",
                "split the payload, or send a reference the receiver can resolve");
            if (_disposed)
            {
                response?.Invoke(RbxNetworkResponse.Failure(DisposedReason));
                return;
            }

            if (!_isServer && !NetworkClient.isConnected)
            {
                // WHY failed now rather than at the timeout: nothing was handed to the transport,
                // so nothing can answer, and thirty seconds of waiting would be a lie about a call
                // that never left.
                DropUnsent();
                response?.Invoke(RbxNetworkResponse.Failure(NotConnectedReason));
                return;
            }

            if (message.Direction == RbxNetworkDirection.ClientToServer)
            {
                if (_isServer)
                {
                    RequireLocalSender(message.SenderActorId, "invoke the server");
                }

                _rateLimiter.Admit(message.SenderActorId, RbxNetworkRateGroup.RemoteFunction);
            }

            if (_isServer && (message.Direction == RbxNetworkDirection.ClientToServer
                              || IsLocalActor(message.RecipientActorId)))
            {
                DeliverRequestLocally(message, response);
                return;
            }

            bool routed = false;
            int connectionId = 0;
            if (_isServer && !string.IsNullOrEmpty(message.RecipientActorId))
            {
                routed = _connectionsByActor.TryGetValue(message.RecipientActorId, out connectionId);
            }

            uint correlationId = _nextCorrelationId++;
            _pending[correlationId] = new PendingRequest
            {
                HasConnection = routed,
                ConnectionId = connectionId,
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
                CountClientSend(message.Payload);
                return;
            }

            if (routed
                && NetworkServer.connections.TryGetValue(connectionId, out NetworkConnectionToClient conn))
            {
                if (_joining.TryGetValue(connectionId, out JoiningConnection joining))
                {
                    Hold(connectionId, joining, new HeldSend
                    {
                        IsRequest = true,
                        Request = wire,
                        Payload = message.Payload
                    });
                    return;
                }

                conn.Send(wire);
                Count(message.Payload);
                return;
            }

            UnroutablePacketsDropped++;
        }

        /// <summary>
        /// Fails every request whose deadline has passed, and on a client every request at all once
        /// it is no longer connected. <see cref="Pump"/> runs this with the rest of a frame's work;
        /// a composition calls that once per frame.
        /// </summary>
        public void PumpTimeouts()
        {
            if (_pending.Count == 0)
            {
                return;
            }

            if (!_isServer && !NetworkClient.isConnected)
            {
                FailAllPending(NotConnectedReason, containCompletionFailures: false);
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
                // WHY remove-and-test: a completion is mod code and may reach UnregisterActor,
                // whose FailPendingFor removes and fails entries still ahead in this list.
                if (!_pending.Remove(expired[index], out PendingRequest request))
                {
                    continue;
                }

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
        /// provider. WHY the client handlers do not require Mirror authentication: the client
        /// connection is marked authenticated only when the reliable admission response is
        /// processed, and an unreliable remote the server fires in the same frame leaves first —
        /// with Mirror's default, that packet would make Mirror disconnect the joining client. The
        /// bridge's own rule already drops and counts every remote until the admitted actor is
        /// bound, which is the stricter of the two. The server handlers keep Mirror's default: a
        /// connection that never finished admission is disconnected by Mirror before it reaches
        /// this bridge at all.
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
                NetworkServer.ReplaceHandler<CoreAiClientReadyMessage>(OnServerClientReady);
                return;
            }

            NetworkClient.ReplaceHandler<CoreAiRemoteEventMessage>(OnClientEvent,
                requireAuthentication: false);
            NetworkClient.ReplaceHandler<CoreAiRemoteRequestMessage>(OnClientRequest,
                requireAuthentication: false);
            NetworkClient.ReplaceHandler<CoreAiRemoteResponseMessage>(OnClientResponse,
                requireAuthentication: false);
            NetworkClient.ReplaceHandler<CoreAiServerClockMessage>(OnClientClockAnchor,
                requireAuthentication: false);
            NetworkClient.ReplaceHandler<CoreAiDisconnectNoticeMessage>(OnClientDisconnectNotice,
                requireAuthentication: false);
        }

        /// <inheritdoc />
        /// <remarks>
        /// WHY open requests are failed rather than cleared: each one is a script waiting on an
        /// answer, and a completion dropped here would leave it waiting for the world's own timeout
        /// with no reason given. A completion that throws is logged, not allowed to stop the rest.
        /// </remarks>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            UnregisterHandlers();
            PerformOwedDrops(0d, all: true);
            _peersByConnection.Clear();
            _connectionsByActor.Clear();
            _actorOrder.Clear();
            _localEvents.Clear();
            _joining.Clear();
            _acknowledged.Clear();
            _admittedActorId = null;
            _readyAcknowledgedOn = null;
            EventReceived = null;
            RequestReceived = null;
            PeerDisconnected = null;
            DisconnectNoticeReceived = null;
            FailAllPending(DisposedReason, containCompletionFailures: true);
        }

        private void UnregisterHandlers()
        {
            if (_isServer)
            {
                NetworkServer.UnregisterHandler<CoreAiRemoteEventMessage>();
                NetworkServer.UnregisterHandler<CoreAiRemoteRequestMessage>();
                NetworkServer.UnregisterHandler<CoreAiRemoteResponseMessage>();
                NetworkServer.UnregisterHandler<CoreAiClientReadyMessage>();
                return;
            }

            NetworkClient.UnregisterHandler<CoreAiRemoteEventMessage>();
            NetworkClient.UnregisterHandler<CoreAiRemoteRequestMessage>();
            NetworkClient.UnregisterHandler<CoreAiRemoteResponseMessage>();
            NetworkClient.UnregisterHandler<CoreAiServerClockMessage>();
            NetworkClient.UnregisterHandler<CoreAiDisconnectNoticeMessage>();
        }

        private void OnServerEvent(NetworkConnectionToClient conn, CoreAiRemoteEventMessage wire)
        {
            if (conn == null)
            {
                UnadmittedPacketsDropped++;
                return;
            }

            ReceiveServerEvent(conn.connectionId, wire);
        }

        /// <summary>
        /// The server's receive path for one event, keyed by connection rather than by a Mirror
        /// object.
        /// </summary>
        /// <remarks>
        /// WHY it takes an id: the sender is resolved from this bridge's own map, so the connection
        /// object itself is never needed — and taking only the id makes the rule ("an unadmitted
        /// connection reaches nothing") provable without standing up a transport, which is the
        /// difference between a rule that is tested and one that is asserted in a comment. Any id
        /// is a connection id: kcp2k derives them from a hash, so a negative one is as real as any.
        /// </remarks>
        internal void ReceiveServerEvent(int connectionId, CoreAiRemoteEventMessage wire)
        {
            if (!TryResolveSender(connectionId, out RbxNetworkPeer peer))
            {
                return;
            }

            if (wire.Direction != (byte)RbxNetworkDirection.ClientToServer
                || !IsKnownReliability(wire.Reliability)
                || !IsServerAssigned(wire.RemoteId))
            {
                DropMalformed();
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

            if (!IsKnownReliability(wire.Reliability) || !IsServerAssigned(wire.RemoteId))
            {
                DropMalformed();
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
            if (conn == null)
            {
                UnadmittedPacketsDropped++;
                return;
            }

            ReceiveServerRequest(conn.connectionId, wire);
        }

        /// <summary>The server's receive path for one request, keyed by connection.</summary>
        /// <remarks>
        /// WHY the connection and its binding are captured now: the handler may answer seconds
        /// later, after this client left and — kcp2k ids being endpoint hashes — someone else took
        /// the id, possibly with a request of the same correlation id. The answer goes to the
        /// connection that asked, still bound to the actor that asked, or to nobody.
        /// </remarks>
        internal void ReceiveServerRequest(int connectionId, CoreAiRemoteRequestMessage wire)
        {
            if (!TryResolveSender(connectionId, out RbxNetworkPeer peer))
            {
                return;
            }

            if (wire.Direction != (byte)RbxNetworkDirection.ClientToServer
                || !IsServerAssigned(wire.RemoteId))
            {
                DropMalformed();
                return;
            }

            NetworkServer.connections.TryGetValue(connectionId, out NetworkConnectionToClient asked);
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
                    RespondTo(connectionId, asked, peer, correlationId, result)));
        }

        private void OnClientRequest(CoreAiRemoteRequestMessage wire)
        {
            if (!TryResolveSelf(out string self))
            {
                return;
            }

            if (!IsServerAssigned(wire.RemoteId))
            {
                DropMalformed();
                return;
            }

            PacketsDelivered++;
            uint correlationId = wire.CorrelationId;
            NetworkConnectionToServer asked = NetworkClient.connection;
            RequestReceived?.Invoke(
                new RbxNetworkRequestMessage(
                    new InstanceId(wire.RemoteId),
                    RbxNetworkDirection.ServerToClient,
                    null,
                    self,
                    wire.Payload),
                new RbxNetworkRequestResponder(result =>
                    SendClientResponse(asked, correlationId, result)));
        }

        private void SendClientResponse(NetworkConnectionToServer asked, uint correlationId,
            RbxNetworkResponse result)
        {
            if (!NetworkClient.isConnected)
            {
                DropUnsent();
                return;
            }

            if (!ReferenceEquals(NetworkClient.connection, asked))
            {
                StaleResponsesDropped++;
                return;
            }

            NetworkClient.Send(FitResponse(correlationId, result));
            _unsentDropLogged = false;
        }

        private void OnServerResponse(NetworkConnectionToClient conn,
            CoreAiRemoteResponseMessage wire)
        {
            if (conn == null)
            {
                OrphanResponsesDropped++;
                return;
            }

            ReceiveServerResponse(conn.connectionId, wire);
        }

        /// <summary>The server's receive path for one response, keyed by connection.</summary>
        internal void ReceiveServerResponse(int connectionId, CoreAiRemoteResponseMessage wire)
        {
            CompleteResponse(fromConnection: true, connectionId, wire);
        }

        private void OnClientResponse(CoreAiRemoteResponseMessage wire)
        {
            CompleteResponse(fromConnection: false, connectionId: 0, wire);
        }

        private void OnServerClientReady(NetworkConnectionToClient conn, CoreAiClientReadyMessage _)
        {
            if (conn == null)
            {
                UnadmittedPacketsDropped++;
                return;
            }

            ReceiveClientReady(conn.connectionId);
        }

        /// <summary>
        /// The server's receive path for one readiness acknowledgement, keyed by connection: the
        /// connection gets the server's clock, then everything held for it, in order.
        /// </summary>
        /// <remarks>
        /// WHY the anchor goes first: a held remote may carry a server timestamp its handler compares
        /// with <c>GetServerTimeNow</c>, and the client should know the server's clock by then. WHY a
        /// repeat is ignored: the client repeats until it hears the anchor, and one connection is
        /// acknowledged once. WHY an acknowledgement from a connection with no binding is dropped: a
        /// connection admitted before the world attached is bound later, and its client repeats.
        /// </remarks>
        internal void ReceiveClientReady(int connectionId)
        {
            if (!_peersByConnection.ContainsKey(connectionId))
            {
                UnadmittedPacketsDropped++;
                return;
            }

            if (!_acknowledged.Add(connectionId))
            {
                return;
            }

            ReadyAcknowledgements++;
            if (_acknowledged.Count == 1)
            {
                _nextClockAnchorAt = _clockSeconds() + ClockAnchorIntervalSeconds;
            }

            NetworkServer.connections.TryGetValue(connectionId, out NetworkConnectionToClient conn);
            SendClockAnchor(conn, Channels.Reliable);
            if (_joining.TryGetValue(connectionId, out JoiningConnection joining))
            {
                _joining.Remove(connectionId);
                SendHeld(conn, joining);
            }
        }

        /// <summary>
        /// A client's receive path for one clock anchor: the server's Unix time now is estimated as
        /// the anchor plus half the round trip, and carried forward on this machine's monotonic clock.
        /// </summary>
        /// <remarks>
        /// WHY the first anchor of each connection replaces the estimate outright: it may be another
        /// server's clock. WHY a later one within <see cref="ClockStepThresholdSeconds"/> is blended
        /// in: each anchor is late by its own trip, a reliable one by its retransmits too, and a
        /// quarter of the gap per anchor keeps one slow sample from jerking every derived clock. WHY
        /// one farther off replaces it: that is the server's clock stepping, not network jitter.
        /// WHY an anchor that is not a positive finite time is dropped: it would poison every clock
        /// derived from it, and a throw here would make Mirror disconnect the client.
        /// </remarks>
        private void OnClientClockAnchor(CoreAiServerClockMessage wire)
        {
            double serverUnix = wire.ServerUnixSeconds;
            if (double.IsNaN(serverUnix) || double.IsInfinity(serverUnix) || serverUnix <= 0d)
            {
                DropMalformed();
                return;
            }

            double roundTrip = RoundTripSeconds?.Invoke() ?? NetworkTime.rtt;
            if (double.IsNaN(roundTrip) || double.IsInfinity(roundTrip) || roundTrip < 0d)
            {
                roundTrip = 0d;
            }

            double sample = serverUnix + roundTrip * 0.5d - _wallClock.ProcessTimeSeconds;
            NetworkConnectionToServer connection = NetworkClient.connection;
            bool replace = !_clockAnchored
                           || !ReferenceEquals(_anchoredOn, connection)
                           || Math.Abs(sample - _serverUnixAtMonotonicZero) > ClockStepThresholdSeconds;
            _serverUnixAtMonotonicZero = replace
                ? sample
                : _serverUnixAtMonotonicZero + (sample - _serverUnixAtMonotonicZero) * ClockSmoothing;
            _clockAnchored = true;
            _anchoredOn = connection;
            ClockAnchorsReceived++;
        }

        /// <summary>
        /// A client's receive path for the server's reason for closing this connection: kept,
        /// logged, and raised to the host; the drop itself is the server's.
        /// </summary>
        /// <remarks>
        /// WHY the client does not disconnect itself here: the server drops the connection a frame
        /// later anyway, and a client that left first would turn the server's kick into a transport
        /// loss in the server's own log. WHY a kind this version does not know is still kept: the
        /// text is what the player is shown, and a newer server may have a newer reason.
        /// </remarks>
        private void OnClientDisconnectNotice(CoreAiDisconnectNoticeMessage wire)
        {
            wire.Message ??= "";
            LastDisconnectNotice = wire;
            string kind = Enum.IsDefined(typeof(CoreAiDisconnectNoticeKind), wire.Kind)
                ? ((CoreAiDisconnectNoticeKind)wire.Kind).ToString()
                : "reason " + wire.Kind;
            _log("[CoreAI.Mirror] the server is closing this connection (" + kind + "): "
                 + wire.Message);
            try
            {
                DisconnectNoticeReceived?.Invoke(wire);
            }
            catch (Exception exception)
            {
                // WHY contained: this runs inside Mirror's handler, where a throw disconnects the
                // client before the server's drop and loses the ordering the notice exists for.
                _log("[CoreAI.Mirror] a disconnect notice listener threw: " + exception.Message);
            }
        }

        private void CompleteResponse(bool fromConnection, int connectionId,
            CoreAiRemoteResponseMessage wire)
        {
            if (!_pending.TryGetValue(wire.CorrelationId, out PendingRequest request))
            {
                // Either a reply to a request that already timed out, or a crafted id. Both are
                // dropped and counted; neither may complete anything.
                OrphanResponsesDropped++;
                return;
            }

            // WHY both sides of the test: a response from a connection completes only a request
            // that was sent on that very connection — whatever the sign of its id — and a request
            // that was never sent anywhere is completed by no connection at all. Otherwise any
            // client could answer another client's question.
            bool matches = fromConnection
                ? request.HasConnection && request.ConnectionId == connectionId
                : !request.HasConnection;
            if (!matches)
            {
                OrphanResponsesDropped++;
                return;
            }

            _pending.Remove(wire.CorrelationId);
            PacketsDelivered++;
            request.Complete?.Invoke(wire.Success
                ? RbxNetworkResponse.Success(wire.Payload)
                : RbxNetworkResponse.Failure(wire.ErrorMessage));
        }

        private void RespondTo(int connectionId, NetworkConnectionToClient asked, RbxNetworkPeer peer,
            uint correlationId, RbxNetworkResponse result)
        {
            if (asked == null
                || !NetworkServer.connections.TryGetValue(connectionId, out NetworkConnectionToClient live)
                || !ReferenceEquals(live, asked)
                || !_peersByConnection.TryGetValue(connectionId, out RbxNetworkPeer bound)
                || !SamePeer(bound, peer))
            {
                StaleResponsesDropped++;
                return;
            }

            live.Send(FitResponse(correlationId, result));
        }

        /// <summary>
        /// The response that goes on the wire for one result: the result itself when it fits one
        /// reliable message on this transport, otherwise a failure that says why.
        /// </summary>
        /// <remarks>
        /// WHY a failure and not the oversize answer: Mirror drops a message larger than its channel
        /// allows with an error in the log, and the caller then waits out the whole timeout for an
        /// answer that was computed and thrown away. A failure naming the size ends the call now.
        /// </remarks>
        private CoreAiRemoteResponseMessage FitResponse(uint correlationId, RbxNetworkResponse result)
        {
            CoreAiRemoteResponseMessage wire = ToWire(correlationId, result);
            long content = Transport.active == null
                ? int.MaxValue
                : NetworkMessages.MaxContentSize(Channels.Reliable);
            if (ResponseWireBytes(wire) <= content)
            {
                return wire;
            }

            OversizeResponsesFailed++;
            string reason = result.Succeeded
                ? "the RemoteFunction's answer of " + result.Payload.Length + " bytes does not fit one "
                  + "reliable message on this transport (" + content + " bytes of content at most)"
                : result.Error;
            long budget = content - ResponseEnvelopeBytes - 1 - StringWireBytes(ResponseErrorCode)
                          - StringHeaderBytes;
            int limit = budget > NetworkWriter.MaxStringLength
                ? NetworkWriter.MaxStringLength
                : (int)Math.Max(0L, budget);
            return new CoreAiRemoteResponseMessage
            {
                CorrelationId = correlationId,
                Success = false,
                Payload = Array.Empty<byte>(),
                ErrorCode = ResponseErrorCode,
                ErrorMessage = Truncate(reason, limit)
            };
        }

        private static CoreAiRemoteResponseMessage ToWire(uint correlationId,
            RbxNetworkResponse result)
        {
            return new CoreAiRemoteResponseMessage
            {
                CorrelationId = correlationId,
                Success = result.Succeeded,
                Payload = result.Payload,
                ErrorCode = result.Succeeded ? "" : ResponseErrorCode,
                ErrorMessage = result.Error ?? ""
            };
        }

        private static CoreAiRemoteEventMessage ToWire(RbxNetworkEventMessage message)
        {
            return new CoreAiRemoteEventMessage
            {
                RemoteId = message.RemoteId.Value,
                Direction = (byte)message.Direction,
                Reliability = (byte)message.Reliability,
                Payload = message.Payload
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
        /// before its admission response was processed, or this composition never bound one —
        /// either way the count and the one-time line are what an operator needs, and a flood of
        /// them is not.
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
            if (_peersByConnection.TryGetValue(connectionId, out peer))
            {
                return true;
            }

            peer = default;
            UnadmittedPacketsDropped++;
            return false;
        }

        /// <summary>
        /// Refuses an in-process client-to-server call from an actor this host never registered,
        /// the way the loopback refuses one.
        /// </summary>
        private void RequireLocalSender(string senderActorId, string operation)
        {
            string sender = senderActorId ?? "";
            if (sender.Length > 0
                && (_actorOrder.Contains(sender) || _connectionsByActor.ContainsKey(sender)))
            {
                return;
            }

            throw new RbxError(
                RbxErrorCode.NotAuthority,
                "actor '" + sender + "' cannot " + operation
                + " because the actor is not registered with this host's network bridge",
                "register the actor context before using remotes");
        }

        /// <summary>Whether an actor runs in this process: registered here, bound to no connection.</summary>
        private bool IsLocalActor(string actorId)
        {
            return !string.IsNullOrEmpty(actorId)
                   && !_connectionsByActor.ContainsKey(actorId)
                   && _actorOrder.Contains(actorId);
        }

        private void SendEventTo(RbxNetworkEventMessage message, int channel)
        {
            string recipient = message.RecipientActorId;
            if (!string.IsNullOrEmpty(recipient)
                && _connectionsByActor.TryGetValue(recipient, out int connectionId))
            {
                SendEventOnWire(connectionId, ToWire(message), message.Payload, channel);
                return;
            }

            if (IsLocalActor(recipient))
            {
                DeliverEventLocally(message);
                return;
            }

            UnroutablePacketsDropped++;
        }

        /// <summary>
        /// A server broadcast: every connection on the wire, every host-local actor in process.
        /// </summary>
        /// <remarks>
        /// WHY a host-local actor gets its copy addressed to it rather than the broadcast itself:
        /// the world answers a broadcast by firing every registered actor's signal, remote players
        /// included, who already receive theirs over the wire.
        /// </remarks>
        private void BroadcastEvent(RbxNetworkEventMessage message, int channel)
        {
            CoreAiRemoteEventMessage wire = ToWire(message);
            foreach (KeyValuePair<string, int> pair in _connectionsByActor)
            {
                SendEventOnWire(pair.Value, wire, message.Payload, channel);
            }

            List<string> local = null;
            for (int index = 0; index < _actorOrder.Count; index++)
            {
                if (!_connectionsByActor.ContainsKey(_actorOrder[index]))
                {
                    local ??= new List<string>();
                    local.Add(_actorOrder[index]);
                }
            }

            for (int index = 0; local != null && index < local.Count; index++)
            {
                DeliverEventLocally(new RbxNetworkEventMessage(message.RemoteId,
                    RbxNetworkDirection.ServerToClient, message.Reliability, null, local[index],
                    message.Payload));
            }
        }

        /// <summary>
        /// Hands one server event to one connection's transport, or holds or drops it while that
        /// connection is joining.
        /// </summary>
        /// <remarks>
        /// WHY an unreliable one is dropped rather than held: it is allowed to be lost, and a 20-60 Hz
        /// update replayed after the join would be stale state delivered as news.
        /// </remarks>
        private void SendEventOnWire(int connectionId, CoreAiRemoteEventMessage wire, byte[] payload,
            int channel)
        {
            if (!NetworkServer.connections.TryGetValue(connectionId, out NetworkConnectionToClient conn))
            {
                UnroutablePacketsDropped++;
                return;
            }

            if (_joining.TryGetValue(connectionId, out JoiningConnection joining))
            {
                if (channel == Channels.Unreliable)
                {
                    NotReadyPacketsDropped++;
                    return;
                }

                Hold(connectionId, joining, new HeldSend { Event = wire, Payload = payload });
                return;
            }

            conn.Send(wire, channel);
            Count(payload);
        }

        /// <summary>
        /// Holds one reliable envelope for a joining connection, within its bounds; past them the
        /// envelope is dropped and counted, and a held RemoteFunction call fails now.
        /// </summary>
        /// <remarks>
        /// WHY bounded: the client may never acknowledge, and a server firing at it every frame
        /// would otherwise grow this queue until the deadline. WHY a call over the bound fails at
        /// once: its caller would otherwise wait the whole timeout for a request that never left.
        /// </remarks>
        private void Hold(int connectionId, JoiningConnection joining, HeldSend held)
        {
            int bytes = held.Payload?.Length ?? 0;
            if (joining.Held.Count >= MaxHeldMessagesPerJoiningConnection
                || joining.HeldBytes + bytes > MaxHeldBytesPerJoiningConnection)
            {
                NotReadyPacketsDropped++;
                if (!joining.OverflowLogged)
                {
                    joining.OverflowLogged = true;
                    _log("[CoreAI.Mirror] connection " + connectionId + " has not acknowledged "
                         + "readiness and already holds " + joining.Held.Count + " remotes ("
                         + joining.HeldBytes + " bytes); this remote and any that follow past the "
                         + "bound are dropped and counted");
                }

                if (held.IsRequest)
                {
                    FailPending(held.Request.CorrelationId,
                        "the client had not finished joining and too many remotes were already "
                        + "waiting for it");
                }

                return;
            }

            joining.Held.Add(held);
            joining.HeldBytes += bytes;
            PacketsHeldUntilReady++;
        }

        /// <summary>Sends what was held for a connection that just acknowledged readiness, in order.</summary>
        private void SendHeld(NetworkConnectionToClient conn, JoiningConnection joining)
        {
            for (int index = 0; index < joining.Held.Count; index++)
            {
                HeldSend held = joining.Held[index];
                if (held.IsRequest && !_pending.ContainsKey(held.Request.CorrelationId))
                {
                    // WHY skipped: the call already failed or timed out, so nothing waits for its answer.
                    NotReadyPacketsDropped++;
                    continue;
                }

                if (conn == null)
                {
                    NotReadyPacketsDropped++;
                    continue;
                }

                if (held.IsRequest)
                {
                    conn.Send(held.Request);
                }
                else
                {
                    conn.Send(held.Event, Channels.Reliable);
                }

                Count(held.Payload);
            }

            joining.Held.Clear();
            joining.HeldBytes = 0;
        }

        /// <summary>
        /// Hands one clock anchor to a connection: the wall clock's Unix time now.
        /// </summary>
        private void SendClockAnchor(NetworkConnectionToClient conn, int channel)
        {
            if (conn == null)
            {
                return;
            }

            conn.Send(new CoreAiServerClockMessage
            {
                ServerUnixSeconds = _wallClock.UnixTimeSecondsFractional
            }, channel);
            ClockAnchorsSent++;
        }

        /// <summary>
        /// Sends every acknowledged connection an anchor once the interval has passed.
        /// </summary>
        /// <remarks>
        /// WHY the periodic ones are unreliable: a lost one is replaced by the next, and a reliable
        /// one that waited for a retransmit would arrive stale — late by exactly the error it exists
        /// to correct. The first anchor, the one that makes the client's clock valid, is reliable.
        /// </remarks>
        private void SendDueClockAnchors(double now)
        {
            if (_acknowledged.Count == 0 || now < _nextClockAnchorAt)
            {
                return;
            }

            _nextClockAnchorAt = now + ClockAnchorIntervalSeconds;
            foreach (int connectionId in _acknowledged)
            {
                if (NetworkServer.connections.TryGetValue(connectionId,
                        out NetworkConnectionToClient conn))
                {
                    SendClockAnchor(conn, Channels.Unreliable);
                }
            }
        }

        /// <summary>
        /// Drops every joining connection whose client has not acknowledged readiness within
        /// <see cref="ReadinessTimeoutSeconds"/>, tearing its session down as
        /// <see cref="RbxNetworkDisconnectReason.ServerClosed"/>.
        /// </summary>
        /// <remarks>
        /// WHY no notice: a client that never acknowledged has shown no handler for one — an older
        /// client would disconnect itself on the unknown message, which is noise, not a reason.
        /// WHY the drop is made at once: there is nothing queued for it that must leave first.
        /// </remarks>
        private void DropConnectionsPastTheReadinessDeadline(double now)
        {
            if (_joining.Count == 0)
            {
                return;
            }

            List<int> expired = null;
            foreach (KeyValuePair<int, JoiningConnection> pair in _joining)
            {
                if (now - pair.Value.BoundAt >= ReadinessTimeoutSeconds)
                {
                    expired ??= new List<int>();
                    expired.Add(pair.Key);
                }
            }

            for (int index = 0; expired != null && index < expired.Count; index++)
            {
                int connectionId = expired[index];
                if (!_joining.ContainsKey(connectionId)
                    || !_peersByConnection.TryGetValue(connectionId, out RbxNetworkPeer peer))
                {
                    continue;
                }

                ReadinessTimeouts++;
                _log("[CoreAI.Mirror] connection " + connectionId + " (actor '" + peer.ActorId
                     + "') was admitted but did not acknowledge readiness within "
                     + ReadinessTimeoutSeconds.ToString("0") + " seconds: a client older than the "
                     + "readiness handshake, or one whose CoreAI Mirror bridge was never built; it "
                     + "is dropped");
                NetworkServer.connections.TryGetValue(connectionId, out NetworkConnectionToClient conn);
                try
                {
                    NotifyDisconnected(connectionId, RbxNetworkDisconnectReason.ServerClosed);
                }
                catch (Exception exception)
                {
                    // WHY contained: this runs from the frame pump, and a world teardown that threw
                    // must not stop the other expired connections from being dropped.
                    _log("[CoreAI.Mirror] the teardown of connection " + connectionId
                         + " threw while it was dropped for readiness: " + exception.Message);
                }
                finally
                {
                    ReleaseConnection(connectionId, PeerDisconnectedReason);
                    if (conn != null
                        && NetworkServer.connections.TryGetValue(connectionId,
                            out NetworkConnectionToClient live)
                        && ReferenceEquals(live, conn))
                    {
                        conn.Disconnect();
                    }
                }
            }
        }

        /// <summary>
        /// Hands a connection's client the reason it is about to be dropped. Nothing for a
        /// connection Mirror no longer holds.
        /// </summary>
        private void SendNotice(NetworkConnectionToClient conn, CoreAiDisconnectNoticeKind kind,
            string message)
        {
            if (conn == null)
            {
                return;
            }

            conn.Send(new CoreAiDisconnectNoticeMessage
            {
                Kind = (byte)kind,
                Message = Truncate(message, MaxNoticeMessageBytes)
            });
            DisconnectNoticesSent++;
        }

        /// <summary>
        /// Records a transport drop a notice must precede: performed by the first
        /// <see cref="Pump"/> whose clock reads later than now.
        /// </summary>
        /// <remarks>
        /// WHY "later than now" and not "the next pump": the default clock is Mirror's frame time,
        /// fixed for a whole frame, so a later reading is a later frame — after the late update that
        /// flushed the notice — wherever in the frame the kick came from, even before this frame's
        /// own pump.
        /// </remarks>
        private void OweDrop(NetworkConnectionToClient conn)
        {
            if (conn == null)
            {
                return;
            }

            for (int index = 0; index < _owedDrops.Count; index++)
            {
                if (ReferenceEquals(_owedDrops[index].Connection, conn))
                {
                    return;
                }
            }

            _owedDrops.Add(new OwedDrop(conn, _clockSeconds()));
        }

        /// <summary>
        /// Drops each owed connection that is due — or every one, with <paramref name="all"/> — and
        /// only while Mirror still holds that same connection under its id.
        /// </summary>
        /// <remarks>
        /// WHY the reference is compared and not only the id: kcp2k reuses connection ids, so a
        /// connection that left on its own meanwhile may have a stranger on its id by now. WHY a
        /// snapshot of the due ones: on kcp2k the drop is reported inside the call, and what that
        /// report runs may owe another drop.
        /// </remarks>
        private void PerformOwedDrops(double now, bool all)
        {
            if (_owedDrops.Count == 0)
            {
                return;
            }

            List<NetworkConnectionToClient> due = null;
            for (int index = _owedDrops.Count - 1; index >= 0; index--)
            {
                OwedDrop owed = _owedDrops[index];
                if (all || now > owed.RequestedAt)
                {
                    due ??= new List<NetworkConnectionToClient>();
                    due.Add(owed.Connection);
                    _owedDrops.RemoveAt(index);
                }
            }

            for (int index = due == null ? -1 : due.Count - 1; index >= 0; index--)
            {
                NetworkConnectionToClient conn = due[index];
                if (NetworkServer.connections.TryGetValue(conn.connectionId,
                        out NetworkConnectionToClient live)
                    && ReferenceEquals(live, conn))
                {
                    conn.Disconnect();
                }
            }
        }

        /// <summary>
        /// A client's own player was kicked: the client disconnects, and forgets its admission.
        /// </summary>
        /// <remarks>
        /// WHY only the admitted actor: that is the one player this client is; a client that could
        /// end anyone else's connection would be a server.
        /// </remarks>
        private void DisconnectSelf(string actorId)
        {
            if (string.IsNullOrWhiteSpace(actorId) || _admittedActorId == null
                || !string.Equals(actorId.Trim(), _admittedActorId, StringComparison.Ordinal))
            {
                return;
            }

            SelfKicks++;
            NetworkClient.Disconnect();
            ForgetAdmittedActor();
        }

        /// <summary>
        /// Tells the server this client can hear it, once per connection and again every
        /// <see cref="ReadyAcknowledgementRetrySeconds"/> until the server's first clock anchor on
        /// that connection answers.
        /// </summary>
        private void AcknowledgeReadinessIfDue()
        {
            if (_isServer || _disposed || _admittedActorId == null || !NetworkClient.isConnected)
            {
                return;
            }

            NetworkConnectionToServer connection = NetworkClient.connection;
            if (connection == null || ReferenceEquals(_anchoredOn, connection))
            {
                return;
            }

            double now = _clockSeconds();
            if (ReferenceEquals(_readyAcknowledgedOn, connection) && now < _nextReadyAcknowledgementAt)
            {
                return;
            }

            NetworkClient.Send(new CoreAiClientReadyMessage());
            _readyAcknowledgedOn = connection;
            _nextReadyAcknowledgementAt = now + ReadyAcknowledgementRetrySeconds;
        }

        private void FailPending(uint correlationId, string reason)
        {
            if (_pending.Remove(correlationId, out PendingRequest request))
            {
                request.Complete?.Invoke(RbxNetworkResponse.Failure(reason));
            }
        }

        /// <summary>
        /// Delivers one event to this process's world, in the order it was sent.
        /// </summary>
        /// <remarks>
        /// WHY a queue: a delivery runs world code that may fire again, and a nested fire delivered
        /// inside the first would overtake events already waiting — the loopback's order rule.
        /// </remarks>
        private void DeliverEventLocally(RbxNetworkEventMessage message)
        {
            LocalDeliveries++;
            _localEvents.Enqueue(message);
            if (_deliveringLocally)
            {
                return;
            }

            _deliveringLocally = true;
            try
            {
                while (_localEvents.Count > 0)
                {
                    EventReceived?.Invoke(_localEvents.Dequeue());
                }
            }
            finally
            {
                _deliveringLocally = false;
            }
        }

        private void DeliverRequestLocally(RbxNetworkRequestMessage message,
            Action<RbxNetworkResponse> response)
        {
            LocalDeliveries++;
            Action<RbxNetworkRequestMessage, RbxNetworkRequestResponder> receiver = RequestReceived;
            if (receiver == null)
            {
                response?.Invoke(RbxNetworkResponse.Failure(
                    "nothing on this host handles RemoteFunction requests"));
                return;
            }

            RbxNetworkRequestResponder responder = new(result => response?.Invoke(result));
            try
            {
                receiver(message, responder);
            }
            catch (Exception exception)
            {
                if (!responder.IsCompleted)
                {
                    responder.Fail(exception.Message);
                }
            }
        }

        /// <summary>
        /// Closes the older connection of an actor bound again on a newer one: released here, its
        /// open requests failed with the reason, its client told why, and dropped at the transport
        /// on a later frame's <see cref="Pump"/>.
        /// </summary>
        private void Supersede(int older, int newer, string actorId)
        {
            SupersededConnections++;
            _log("[CoreAI.Mirror] actor '" + actorId + "' was admitted again on connection " + newer
                 + "; its older connection " + older + " is closed: " + SupersededReason);
            NetworkConnectionToClient stale = null;
            if (_isServer)
            {
                NetworkServer.connections.TryGetValue(older, out stale);
            }

            SendNotice(stale, CoreAiDisconnectNoticeKind.Superseded, SupersededNoticeMessage);
            ReleaseBinding(older, SupersededReason);
            OweDrop(stale);
        }

        /// <summary>
        /// Releases one connection: its binding, its admission record and its open requests, and
        /// the actor's registration once the actor holds no connection at all.
        /// </summary>
        private void ReleaseConnection(int connectionId, string reason)
        {
            if (!_peersByConnection.TryGetValue(connectionId, out RbxNetworkPeer peer))
            {
                return;
            }

            ReleaseBinding(connectionId, reason);
            if (!_connectionsByActor.ContainsKey(peer.ActorId))
            {
                _actorOrder.Remove(peer.ActorId);
                _rateLimiter.Forget(peer.ActorId);
            }
        }

        /// <summary>
        /// Unbinds one connection and nothing else: the actor keeps its registration, and another
        /// connection the actor holds is untouched. What was held for the connection while it was
        /// joining is discarded and counted.
        /// </summary>
        private void ReleaseBinding(int connectionId, string reason)
        {
            if (_peersByConnection.TryGetValue(connectionId, out RbxNetworkPeer peer))
            {
                _peersByConnection.Remove(connectionId);
                if (_connectionsByActor.TryGetValue(peer.ActorId, out int mapped)
                    && mapped == connectionId)
                {
                    _connectionsByActor.Remove(peer.ActorId);
                }
            }

            if (_joining.TryGetValue(connectionId, out JoiningConnection joining))
            {
                _joining.Remove(connectionId);
                NotReadyPacketsDropped += joining.Held.Count;
            }

            _acknowledged.Remove(connectionId);
            _authenticator?.Forget(connectionId);
            FailPendingFor(connectionId, reason);
        }

        private void FailPendingFor(int connectionId, string reason)
        {
            List<uint> affected = null;
            foreach (KeyValuePair<uint, PendingRequest> pair in _pending)
            {
                if (pair.Value.HasConnection && pair.Value.ConnectionId == connectionId)
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
                // WHY remove-and-test: same as PumpTimeouts — a completion that unregisters the
                // actor re-enters here and empties the rest of this list first.
                if (!_pending.Remove(affected[index], out PendingRequest request))
                {
                    continue;
                }

                request.Complete?.Invoke(RbxNetworkResponse.Failure(reason));
            }
        }

        private void FailAllPending(string reason, bool containCompletionFailures)
        {
            if (_pending.Count == 0)
            {
                return;
            }

            List<uint> open = new(_pending.Keys);
            for (int index = 0; index < open.Count; index++)
            {
                if (!_pending.Remove(open[index], out PendingRequest request))
                {
                    continue;
                }

                if (!containCompletionFailures)
                {
                    request.Complete?.Invoke(RbxNetworkResponse.Failure(reason));
                    continue;
                }

                try
                {
                    request.Complete?.Invoke(RbxNetworkResponse.Failure(reason));
                }
                catch (Exception exception)
                {
                    _log("[CoreAI.Mirror] a RemoteFunction completion threw while the bridge failed "
                         + "its open requests (" + reason + "): " + exception.Message);
                }
            }
        }

        private static void RequirePayloadFits(byte[] payload, int ceiling, string what, string fix)
        {
            int length = payload?.Length ?? 0;
            if (length <= ceiling)
            {
                return;
            }

            throw new RbxError(
                RbxErrorCode.PayloadTooLarge,
                "network payload of " + length + " bytes exceeds the transport limit of "
                + ceiling + " bytes for " + what,
                fix);
        }

        /// <summary>
        /// The largest payload one message of an envelope carries on a channel of the active
        /// transport, counted the way Mirror packs it; unbounded when no transport is active.
        /// </summary>
        /// <remarks>
        /// WHY Mirror's own content size and not the transport's packet size: Mirror reserves a
        /// batch timestamp, a size header and the message id out of every packet, and drops with an
        /// error whatever exceeds the rest — so a ceiling read from the packet size alone passes
        /// payloads a few bytes too large, counts them as sent, and they never leave.
        /// </remarks>
        private static int LargestFittingPayload(int channel, int envelopeBytes)
        {
            if (Transport.active == null)
            {
                return int.MaxValue;
            }

            int content = NetworkMessages.MaxContentSize(channel);
            int payload = content - envelopeBytes - 1;
            while (payload > 0
                   && envelopeBytes + Compression.VarUIntSize((ulong)payload + 1UL) + payload > content)
            {
                payload--;
            }

            return payload > 0 ? payload : 0;
        }

        private static long ResponseWireBytes(CoreAiRemoteResponseMessage wire)
        {
            int payload = wire.Payload?.Length ?? 0;
            return ResponseEnvelopeBytes + Compression.VarUIntSize((ulong)payload + 1UL) + payload
                   + StringWireBytes(wire.ErrorCode) + StringWireBytes(wire.ErrorMessage);
        }

        /// <summary>
        /// What Mirror writes for one string; a string Mirror refuses to write counts as too large
        /// for any message.
        /// </summary>
        private static long StringWireBytes(string text)
        {
            int bytes = text == null ? 0 : Encoding.UTF8.GetByteCount(text);
            return bytes > NetworkWriter.MaxStringLength
                ? int.MaxValue + 1L
                : StringHeaderBytes + bytes;
        }

        private static string Truncate(string text, int maxBytes)
        {
            if (string.IsNullOrEmpty(text) || maxBytes <= 0)
            {
                return "";
            }

            if (Encoding.UTF8.GetByteCount(text) <= maxBytes)
            {
                return text;
            }

            // WHY a third: no UTF-16 code unit takes more than three UTF-8 bytes, so this many
            // characters always fit; a surrogate pair is never split.
            int length = Math.Min(text.Length, maxBytes / 3);
            if (length > 0 && char.IsHighSurrogate(text[length - 1]))
            {
                length--;
            }

            return text.Substring(0, length);
        }

        private static bool IsKnownReliability(byte reliability)
        {
            return reliability == (byte)RbxNetworkReliability.ReliableOrdered
                   || reliability == (byte)RbxNetworkReliability.UnreliableUnordered;
        }

        private static bool IsServerAssigned(ulong remoteId)
        {
            return new InstanceId(remoteId).IsServerAssigned;
        }

        private static bool SamePeer(RbxNetworkPeer left, RbxNetworkPeer right)
        {
            return string.Equals(left.ActorId, right.ActorId, StringComparison.Ordinal)
                   && string.Equals(left.SessionId, right.SessionId, StringComparison.Ordinal)
                   && string.Equals(left.ConnectionHandle, right.ConnectionHandle, StringComparison.Ordinal);
        }

        /// <summary>
        /// Drops one malformed envelope from an admitted peer: counted, never delivered, and said
        /// once, because a flood of them must not become a flood of log lines.
        /// </summary>
        /// <remarks>
        /// WHY dropped and not thrown: a throw inside Mirror's handler makes Mirror disconnect the
        /// sender, and one from the message constructor would escape after the packet had already
        /// been counted as delivered.
        /// The connection stays up; a peer that only sends garbage reaches nothing.
        /// </remarks>
        private void DropMalformed()
        {
            MalformedPacketsDropped++;
            if (_malformedDropLogged)
            {
                return;
            }

            _malformedDropLogged = true;
            _log("[CoreAI.Mirror] an envelope with an invalid remote id, reliability, direction "
                 + "or clock anchor arrived; it and any that follow are dropped and counted");
        }

        private void Count(byte[] payload)
        {
            PacketsSent++;
            BytesSent += payload?.Length ?? 0;
        }

        /// <summary>Counts a client send the transport took, and arms the unsent line again.</summary>
        private void CountClientSend(byte[] payload)
        {
            Count(payload);
            _unsentDropLogged = false;
        }

        /// <summary>
        /// Drops one client envelope that had no connection to leave on: counted, never sent, and
        /// said once per disconnected stretch.
        /// </summary>
        /// <remarks>
        /// WHY guarded here rather than left to Mirror: NetworkClient.Send logs an error and drops
        /// the message on every call made without a connection, so a world that fires while
        /// disconnected would flood the log while PacketsSent claimed the packets left.
        /// </remarks>
        private void DropUnsent()
        {
            UnsentPacketsDropped++;
            if (_unsentDropLogged)
            {
                return;
            }

            _unsentDropLogged = true;
            _log("[CoreAI.Mirror] a remote was fired while this client is not connected to a "
                 + "server; it and any that follow are dropped and counted, not sent, until the "
                 + "client connects");
        }
    }
}
