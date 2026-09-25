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
    /// <b>Server clock.</b> An acknowledged connection is sent the server's time — what the server
    /// world's own <c>GetServerTimeNow</c> reads, and whether it is holding its last reading after
    /// the wall clock stepped back — then again every <see cref="ClockAnchorIntervalSeconds"/>, and
    /// at once when that clock leaves the course clients extrapolate; a client derives
    /// <see cref="ServerClockOffsetSeconds"/> and <see cref="IsServerClockHeld"/> from those anchors
    /// alone.
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
    /// response is dropped and counted instead of making Mirror disconnect the joining client. The
    /// other way round the server's handlers do require it, so a client puts nothing on the wire
    /// before its admission is bound: an unreliable remote fired meanwhile is dropped and counted
    /// (<see cref="UnadmittedSendsDropped"/>), because it would overtake the admission request and
    /// Mirror would disconnect the joining client for it; a reliable remote or an InvokeServer is
    /// held, bounded, and sent in order right after the admission is bound. An admission belongs to
    /// the connection it was bound on: a newer connection — a reconnect made within one frame —
    /// starts unadmitted, and what was held for a connection that closed is dropped, its calls
    /// failed.
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

        /// <summary>
        /// A reliable envelope held until its connection can take it: on a server, for a connection
        /// that has not acknowledged readiness; on a client, until this client's own admission.
        /// </summary>
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
        /// Reliable sends a client holds at most while its admission has not arrived; the next is
        /// dropped and counted in <see cref="AdmissionHoldOverflowDrops"/>. The server's readiness
        /// bound, seen from the other end.
        /// </summary>
        public const int MaxHeldSendsUntilAdmitted = MaxHeldMessagesPerJoiningConnection;

        /// <summary>
        /// Payload bytes a client holds at most while its admission has not arrived; a send past it
        /// is dropped and counted in <see cref="AdmissionHoldOverflowDrops"/>.
        /// </summary>
        public const int MaxHeldBytesUntilAdmitted = MaxHeldBytesPerJoiningConnection;

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
        /// A hold shorter than this is read as no hold: two reads of one clock a moment apart may
        /// disagree by that much, and a hold is a step back of the server's clock, not jitter.
        /// </summary>
        private const double HeldToleranceSeconds = 0.001d;

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
        /// The most wire bytes Mirror writes for a <c>ulong</c> field of a message: 9, its varint.
        /// </summary>
        /// <remarks>
        /// WHY the varint's worst case: Mirror's weaver writes every <c>ulong</c> and <c>uint</c> field
        /// through its <c>[WeaverPriority]</c> varint writers, not as fixed 8 and 4 bytes, so a remote id
        /// takes 1-9 bytes and a correlation id 1-5. Sized for fixed widths, the ceilings let a payload
        /// through that Mirror drops once a correlation id passes 16,777,215 or a remote id 2^56 - 1;
        /// sized for the worst case they hold for every id and are one byte lower (two for a request):
        /// in Unity a 600-byte unreliable packet carried 576 payload bytes before and 575 now.
        /// </remarks>
        private const int MaxVarULongBytes = 9;

        /// <summary>The most wire bytes Mirror writes for a <c>uint</c> field of a message: 5, its varint.</summary>
        private const int MaxVarUIntBytes = 5;

        /// <summary>
        /// Wire bytes of an event envelope besides its payload, at most: RemoteId, Direction, Reliability.
        /// </summary>
        private const int EventEnvelopeBytes = MaxVarULongBytes + 1 + 1;

        /// <summary>
        /// Wire bytes of a request envelope besides its payload, at most: RemoteId, Direction, CorrelationId.
        /// </summary>
        private const int RequestEnvelopeBytes = MaxVarULongBytes + 1 + MaxVarUIntBytes;

        /// <summary>
        /// Wire bytes of a response envelope besides its payload and strings, at most: CorrelationId, Success.
        /// </summary>
        private const int ResponseEnvelopeBytes = MaxVarUIntBytes + 1;

        /// <summary>Mirror's length header in front of every string.</summary>
        private const int StringHeaderBytes = 2;

        private const string ResponseErrorCode = "REMOTE_FAILED";
        private const string PeerDisconnectedReason = "the peer disconnected";
        private const string NotConnectedReason = "the client is not connected to a server";
        private const string AdmissionHoldFullReason =
            "the client has not been admitted by the server yet and too many remotes are already "
            + "waiting for the admission, so nothing was sent";
        private const string HeldConnectionClosedReason =
            "the connection closed before the server admitted this client, so the call was never sent";
        private const string PlayerNotConnectedReason =
            "the player is not connected to this server, so nothing was sent";
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
        private readonly HashSet<int> _tearingDown = new();
        private readonly List<HeldSend> _heldUntilAdmitted = new();
        private readonly RbxNetworkRateLimiter _rateLimiter;
        private readonly Func<double> _clockSeconds;
        private readonly IRbxClockSource _wallClock;
        private readonly CoreAiMirrorAuthenticator _authenticator;
        private readonly Action<string> _log;
        private readonly bool _isServer;
        private string _admittedActorId;
        private NetworkConnectionToServer _admittedOn;
        private NetworkConnectionToServer _heldOn;
        private long _heldUntilAdmittedBytes;
        private NetworkConnectionToServer _readyAcknowledgedOn;
        private NetworkConnectionToServer _anchoredOn;
        private uint _nextCorrelationId = 1u;
        private double _nextReadyAcknowledgementAt;
        private double _nextClockAnchorAt;
        private double _serverUnixAtMonotonicZero;
        private double _serverClockFloor = double.NaN;
        private bool _clockAnchored;
        private bool _backwardAnchorPending;
        private double _setAsideSample;
        private RbxServerClockReader _serverClock;
        private bool _anchorSent;
        private double _lastAnchorServerSeconds;
        private double _lastAnchorHeldSeconds;
        private double _lastAnchorProcessSeconds;
        private bool _serverClockFailureLogged;
        private bool _disposed;
        private bool _deliveringLocally;
        private bool _unadmittedDropLogged;
        private bool _unheardActorLogged;
        private bool _unsentDropLogged;
        private bool _unadmittedSendLogged;
        private bool _admissionHoldOverflowLogged;
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
        /// On a client, the actor the server admitted this process as on the connection it has now;
        /// null before admission, after a disconnect, and on a newer connection until that
        /// connection's own admission. Always null on a server.
        /// </summary>
        public string AdmittedActorId => IsAdmittedOnTheLiveConnection() ? _admittedActorId : null;

        /// <summary>
        /// Whether <see cref="Dispose"/> ran; a composition that outlives the container owning this
        /// bridge reads it before touching Mirror through it.
        /// </summary>
        public bool IsDisposed => _disposed;

        /// <summary>Responses dropped because nothing was waiting for their correlation id.</summary>
        public int OrphanResponsesDropped { get; private set; }

        /// <summary>
        /// Envelopes a client dropped instead of handing to the transport because it was not
        /// connected — fired while disconnected, or held for an admission on a connection that closed
        /// before the admission arrived; never counted as sent. Always zero on a server.
        /// </summary>
        public int UnsentPacketsDropped { get; private set; }

        /// <summary>
        /// Unreliable client-to-server remotes a client dropped instead of handing to the transport
        /// because the server had not admitted it yet — connected, but with no admission bound on
        /// this connection; never counted as sent, never charged to the budget. A reliable remote or
        /// an InvokeServer in that window is held instead (<see cref="SendsHeldUntilAdmitted"/>).
        /// Always zero on a server.
        /// </summary>
        public int UnadmittedSendsDropped { get; private set; }

        /// <summary>
        /// Reliable client-to-server envelopes — a reliable remote or an InvokeServer — a client held
        /// because the server had not admitted it yet, whether they were later sent or not; each is
        /// charged to the budget when it is held and counted in <see cref="PacketsSent"/> when it
        /// leaves. Always zero on a server.
        /// </summary>
        public int SendsHeldUntilAdmitted { get; private set; }

        /// <summary>
        /// Reliable client-to-server envelopes a client dropped because the hold for its admission
        /// was already at <see cref="MaxHeldSendsUntilAdmitted"/> or
        /// <see cref="MaxHeldBytesUntilAdmitted"/>; never sent, never charged, and an InvokeServer
        /// among them failed at once. Always zero on a server.
        /// </summary>
        public int AdmissionHoldOverflowDrops { get; private set; }

        /// <summary>
        /// Connections a server closed because its world unregistered their actor outside a kick or
        /// a transport drop — a host's own <c>DisconnectActor</c>: told why, unbound at once, and
        /// dropped at the transport on a later frame's <see cref="Pump"/>.
        /// </summary>
        public int WorldReleasedConnections { get; private set; }

        /// <summary>
        /// Clock anchors a server sent outside the interval because its clock left the course its
        /// clients extrapolate — held after a backward step, released when the hold ended, or stepped
        /// ahead. Counted in <see cref="ClockAnchorsSent"/> too.
        /// </summary>
        public int ClockStepAnchorsSent { get; private set; }

        /// <summary>
        /// Clock anchors a client set aside as a single outlier: one that would move the estimate back
        /// by more than <see cref="ClockStepThresholdSeconds"/>, which a late packet does. The next
        /// anchor is taken when it agrees with the one set aside — carried back to the moment that one
        /// arrived, it reads the server's clock within <see cref="ClockStepThresholdSeconds"/> of it;
        /// one that agrees with the estimate is blended in and the outlier forgotten; one that agrees
        /// with neither is set aside in its place.
        /// </summary>
        public int ClockAnchorsSetAside { get; private set; }

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

        /// <summary>
        /// Clock anchors a client accepted; one set aside as a single outlier is counted in
        /// <see cref="ClockAnchorsSetAside"/> instead.
        /// </summary>
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
        /// anchor being the server's running clock corrected by half the measured round trip, never
        /// less than the value an anchor said the server's clock is held at. The value moves: at the
        /// first anchor it steps from zero to the whole skew between the two wall clocks, which can
        /// be hours; the first anchor of every later connection replaces the estimate, as does a held
        /// one and one more than <see cref="ClockStepThresholdSeconds"/> ahead of it; one that far
        /// behind is set aside and replaces it only when the next anchor agrees with it (see
        /// <see cref="ClockAnchorsSetAside"/>), and a nearer one is blended in; and between anchors
        /// it follows the client's own wall clock, so a client whose
        /// clock is corrected mid-session still reads the server's time. A consumer that keeps the
        /// derived clock monotonic must treat the first synchronization as a re-base, not as time
        /// going backwards, and must hold while <see cref="IsServerClockHeld"/> says so.
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

                double running = _serverUnixAtMonotonicZero + _wallClock.ProcessTimeSeconds;
                double server = _serverClockFloor > running ? _serverClockFloor : running;
                return server - _wallClock.UnixTimeSecondsFractional;
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// True on a client while the last anchor said the server's clock is held and this machine's
        /// estimate of the server's running clock has not caught up with the held value yet; the hold
        /// ends on its own at that moment, as it does on the server. Always false on a server.
        /// </remarks>
        public bool IsServerClockHeld =>
            !_isServer && _clockAnchored && _serverClockFloor
            > _serverUnixAtMonotonicZero + _wallClock.ProcessTimeSeconds;

        /// <inheritdoc />
        /// <remarks>
        /// On a server, every anchor from then on carries what <paramref name="serverClock"/> reads —
        /// the server world's own <c>GetServerTimeNow</c> and its hold — instead of this bridge's wall
        /// clock; the latest world to attach is the one read. Ignored on a client, whose world is not
        /// the server clock.
        /// </remarks>
        public void AttachServerClock(RbxServerClockReader serverClock)
        {
            if (_isServer && serverClock != null)
            {
                _serverClock = serverClock;
            }
        }

        /// <inheritdoc />
        public void DetachServerClock(RbxServerClockReader serverClock)
        {
            if (serverClock != null && _serverClock == serverClock)
            {
                _serverClock = null;
            }
        }

        /// <inheritdoc />
        public event Action<RbxNetworkEventMessage> EventReceived;

        /// <inheritdoc />
        public event Action<RbxNetworkRequestMessage, RbxNetworkRequestResponder> RequestReceived;

        /// <inheritdoc />
        public event Action<RbxNetworkPeerDisconnected> PeerDisconnected;

        /// <summary>
        /// On a server, raised with a connection's id each time its binding is released, whatever
        /// released it; the session host forgets a session its world ended without it.
        /// </summary>
        internal event Action<int> BindingReleased;

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
        /// finds it connected. WHY what was held leaves from here too, before the acknowledgement:
        /// the server authenticated this connection in the same call that admitted it, so a reliable
        /// remote queued now reaches a handler that accepts it, in the order it was fired. WHY the
        /// admission remembers its connection: Mirror can stop and start a client within one frame,
        /// before any frame's pump sees it stopped, and the newer connection must not speak as the
        /// older one's actor before its own admission (B1-08); a binding made while Mirror holds no
        /// connection at all belongs to the first one it holds next.
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

            ReconcileWithTheLiveConnection();
            _admittedActorId = actorId.Trim();
            _admittedOn = NetworkClient.connection;
            _unadmittedDropLogged = false;
            _unadmittedSendLogged = false;
            _admissionHoldOverflowLogged = false;
            _unheardActorLogged = false;
            LastDisconnectNotice = null;
            SendHeldUntilAdmitted();
            AcknowledgeReadinessIfDue();
        }

        /// <summary>
        /// Forgets the admitted actor after a disconnect, so a reconnect starts unadmitted, fails
        /// every request this client still waits on, and drops what was held for an admission.
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
            _admittedOn = null;
            _readyAcknowledgedOn = null;
            if (!_isServer)
            {
                DiscardHeldUntilAdmitted();
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
        /// connection released is the one this actor holds now, never an older or a newer one. On a
        /// server, a connection released this way outside a teardown this bridge or its session host
        /// is already running — a host's own <c>DisconnectActor</c> — is ended the way a kick ends
        /// one: its client is told, and the transport drops it on a later frame's <see cref="Pump"/>.
        /// WHY the drop is owed here too: the world's disconnect is the host saying this player is
        /// gone, and a connection it leaves open stays authenticated to Mirror and bound to nobody —
        /// every packet from it dropped as unadmitted, its slot held and its session host entry and
        /// identity kept for as long as the client stays (A4-09).
        /// </remarks>
        public void UnregisterActor(string actorId)
        {
            if (string.IsNullOrEmpty(actorId))
            {
                return;
            }

            _actorOrder.Remove(actorId);
            _rateLimiter.Forget(actorId);
            if (!_connectionsByActor.TryGetValue(actorId, out int connectionId))
            {
                return;
            }

            if (!_isServer || _tearingDown.Contains(connectionId))
            {
                ReleaseBinding(connectionId, PeerDisconnectedReason);
                return;
            }

            NetworkServer.connections.TryGetValue(connectionId, out NetworkConnectionToClient conn);
            if (conn != null)
            {
                WorldReleasedConnections++;
                SendNotice(conn, CoreAiDisconnectNoticeKind.Kicked, DefaultKickMessage);
            }

            try
            {
                ReleaseBinding(connectionId, PeerDisconnectedReason);
            }
            finally
            {
                OweDrop(conn);
            }
        }

        /// <summary>
        /// Marks one connection as being torn down by its session host, so the host's own
        /// <see cref="UnregisterActor"/> of it releases the binding and nothing more; false when the
        /// connection was already marked, in which case the caller must not end the mark.
        /// </summary>
        internal bool BeginTeardown(int connectionId)
        {
            return _tearingDown.Add(connectionId);
        }

        /// <summary>Ends a mark <see cref="BeginTeardown"/> made.</summary>
        internal void EndTeardown(int connectionId)
        {
            _tearingDown.Remove(connectionId);
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

            bool marked = BeginTeardown(connectionId);
            try
            {
                FailPendingFor(connectionId, PeerDisconnectedReason);
                PeerDisconnected?.Invoke(new RbxNetworkPeerDisconnected(peer, reason));
            }
            finally
            {
                if (marked)
                {
                    EndTeardown(connectionId);
                }

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
        /// interval's clock anchors; on a client it forgets an admission or a hold that belongs to a
        /// connection Mirror no longer holds, and acknowledges readiness if a binding still owes
        /// that. Call once per frame; the scene provider does.
        /// </summary>
        public void Pump()
        {
            if (_disposed)
            {
                return;
            }

            ReconcileWithTheLiveConnection();
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
        /// rate-limit error, for a packet that never left. One state, one outcome. WHY a client that
        /// is connected but not admitted yet puts nothing on the wire: the server's handlers require
        /// Mirror authentication, and an unreliable remote leaves before the reliable admission
        /// request queued with it, so it reached the server on an unauthenticated connection and
        /// Mirror disconnected the joining client for it (A4-04); it is dropped and counted in
        /// <see cref="UnadmittedSendsDropped"/> instead. WHY a reliable one is held rather than
        /// dropped: on the ordered reliable channel it would have followed the admission request,
        /// which the server answers in the same call, so it used to arrive — and a script has no
        /// signal for "admitted" to wait on (B1-07). It is charged when held, because it leaves; one
        /// past the hold's bound is dropped uncharged. WHY a server never puts ClientToServer on the
        /// wire: on a server that direction can only come from an actor in this process, and the wire
        /// would carry it to every remote client as though the server had fired at them — a leak,
        /// and a remote the server's own scripts never hear.
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
            ReconcileWithTheLiveConnection();
            if (!_isServer && !NetworkClient.isConnected)
            {
                DropUnsent();
                return;
            }

            bool holdUntilAdmitted = !_isServer && !IsAdmittedOnTheLiveConnection();
            if (holdUntilAdmitted && (unreliable || _disposed))
            {
                DropUnadmittedSend();
                return;
            }

            if (holdUntilAdmitted && !HasRoomUntilAdmitted(message.Payload))
            {
                DropPastTheAdmissionHold();
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
                if (holdUntilAdmitted)
                {
                    HoldUntilAdmitted(new HeldSend { Event = ToWire(message), Payload = message.Payload });
                    return;
                }

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
        /// The connection, and a client's room to hold the call until its admission, are checked
        /// before the budget for the reasons <see cref="SendEvent"/> gives: a call that never leaves
        /// is a drop, not a budget entry, and it fails at once. A client's call made before its
        /// admission is held like a reliable remote and sent right after the admission; its timeout
        /// runs from the call, held or not, and one that timed out while held is never sent. On a
        /// server, a ClientToServer invocation and one addressed to a
        /// registered actor without a connection are answered in process, as the loopback answers
        /// them; one addressed to a player with no live connection here fails at once, because no
        /// packet leaves and nothing can answer it (A4-07).
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

            ReconcileWithTheLiveConnection();
            if (!_isServer && !NetworkClient.isConnected)
            {
                // WHY failed now rather than at the timeout: nothing was handed to the transport,
                // so nothing can answer, and thirty seconds of waiting would be a lie about a call
                // that never left.
                DropUnsent();
                response?.Invoke(RbxNetworkResponse.Failure(NotConnectedReason));
                return;
            }

            bool holdUntilAdmitted = !_isServer && !IsAdmittedOnTheLiveConnection();
            if (holdUntilAdmitted && !HasRoomUntilAdmitted(message.Payload))
            {
                DropPastTheAdmissionHold();
                response?.Invoke(RbxNetworkResponse.Failure(AdmissionHoldFullReason));
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
            if (_isServer)
            {
                routed = !string.IsNullOrEmpty(message.RecipientActorId)
                         && _connectionsByActor.TryGetValue(message.RecipientActorId, out connectionId);
                if (!routed)
                {
                    // WHY failed now: the player holds no connection here, so nothing will be sent
                    // and nothing can answer, and a pending entry would keep the server script
                    // waiting thirty seconds for a player who left (A4-07).
                    UnroutablePacketsDropped++;
                    response?.Invoke(RbxNetworkResponse.Failure(PlayerNotConnectedReason));
                    return;
                }
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
                if (holdUntilAdmitted)
                {
                    HoldUntilAdmitted(new HeldSend { IsRequest = true, Request = wire, Payload = message.Payload });
                    return;
                }

                NetworkClient.Send(wire);
                CountClientSend(message.Payload);
                return;
            }

            if (NetworkServer.connections.TryGetValue(connectionId, out NetworkConnectionToClient conn))
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

            // WHY left pending: the binding outlives its transport connection only until the
            // transport reports the drop, and that report fails this request with the rest of the
            // connection's.
            UnroutablePacketsDropped++;
        }

        /// <summary>
        /// Fails every request whose deadline has passed, and on a client every request at all once
        /// it is no longer connected or its connection was replaced, held ones included.
        /// <see cref="Pump"/> runs this with the rest of a frame's work; a composition calls that
        /// once per frame.
        /// </summary>
        public void PumpTimeouts()
        {
            ReconcileWithTheLiveConnection();
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
            _tearingDown.Clear();
            _heldUntilAdmitted.Clear();
            _heldUntilAdmittedBytes = 0;
            _heldOn = null;
            _admittedActorId = null;
            _admittedOn = null;
            _readyAcknowledgedOn = null;
            _serverClock = null;
            EventReceived = null;
            RequestReceived = null;
            PeerDisconnected = null;
            DisconnectNoticeReceived = null;
            BindingReleased = null;
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
        /// A client's receive path for one clock anchor: the server's running clock now is estimated
        /// as the anchor, less its hold, plus half the round trip, and carried forward on this
        /// machine's monotonic clock; a held anchor also sets the value the server's clock holds at
        /// until that estimate reaches it.
        /// </summary>
        /// <remarks>
        /// WHY the first anchor of each connection replaces the estimate outright: it may be another
        /// server's clock. WHY a later one within <see cref="ClockStepThresholdSeconds"/> is blended
        /// in: each anchor is late by its own trip, a reliable one by its retransmits too, and a
        /// quarter of the gap per anchor keeps one slow sample from jerking every derived clock. WHY
        /// one farther ahead replaces it: that is the server's clock stepping, not network jitter.
        /// WHY one farther behind replaces it only when the next anchor agrees (A4-13): a packet
        /// held up in the network for more than the threshold looks exactly like a server clock
        /// that stepped back, and replacing the estimate with it put every client clock seconds
        /// behind until the next anchor; two late anchors in a row are rare, a real step back
        /// repeats in every anchor, and a step back the server's scripts can see arrives held — the
        /// server says so, and that is taken at once. WHY "agrees" compares the two samples: each is
        /// the server's clock carried back to this machine's monotonic zero, so two anchors of one
        /// stepped clock land within the threshold of each other however far apart they arrived,
        /// while two packets late by different amounts do not — and the later of those is set aside
        /// in turn, since it is the newer evidence (B1-10). WHY an anchor that is not a positive finite
        /// time, or a hold that is negative or not finite, is dropped: it would poison every clock
        /// derived from it, and a throw here would make Mirror disconnect the client.
        /// </remarks>
        private void OnClientClockAnchor(CoreAiServerClockMessage wire)
        {
            double serverUnix = wire.ServerUnixSeconds;
            double held = wire.HeldAheadOfWallSeconds;
            if (double.IsNaN(serverUnix) || double.IsInfinity(serverUnix) || serverUnix <= 0d
                || double.IsNaN(held) || double.IsInfinity(held) || held < 0d)
            {
                DropMalformed();
                return;
            }

            double roundTrip = RoundTripSeconds?.Invoke() ?? NetworkTime.rtt;
            if (double.IsNaN(roundTrip) || double.IsInfinity(roundTrip) || roundTrip < 0d)
            {
                roundTrip = 0d;
            }

            bool serverHeld = held > HeldToleranceSeconds;
            double sample = serverUnix - held + roundTrip * 0.5d - _wallClock.ProcessTimeSeconds;
            NetworkConnectionToServer connection = NetworkClient.connection;
            double gap = sample - _serverUnixAtMonotonicZero;
            bool newConnection = !_clockAnchored || !ReferenceEquals(_anchoredOn, connection);
            bool agreesWithTheSetAside = _backwardAnchorPending
                                         && Math.Abs(sample - _setAsideSample) <= ClockStepThresholdSeconds;
            if (newConnection || serverHeld || gap > ClockStepThresholdSeconds
                || (gap < -ClockStepThresholdSeconds && agreesWithTheSetAside))
            {
                _serverUnixAtMonotonicZero = sample;
                _backwardAnchorPending = false;
            }
            else if (gap < -ClockStepThresholdSeconds)
            {
                _backwardAnchorPending = true;
                _setAsideSample = sample;
                ClockAnchorsSetAside++;
                return;
            }
            else
            {
                _serverUnixAtMonotonicZero += gap * ClockSmoothing;
                _backwardAnchorPending = false;
            }

            _serverClockFloor = serverHeld ? serverUnix : double.NaN;
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
        /// before its admission response was processed, the binding is an earlier connection's, or
        /// this composition never bound one —
        /// either way the count and the one-time line are what an operator needs, and a flood of
        /// them is not.
        /// </remarks>
        private bool TryResolveSelf(out string actorId)
        {
            actorId = AdmittedActorId;
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
        /// Hands one clock anchor to a connection: the server's time now, and its hold.
        /// </summary>
        private void SendClockAnchor(NetworkConnectionToClient conn, int channel)
        {
            if (conn == null)
            {
                return;
            }

            double serverSeconds = ReadServerClock(out double heldAheadSeconds);
            SendClockAnchor(conn, channel, serverSeconds, heldAheadSeconds);
        }

        private void SendClockAnchor(NetworkConnectionToClient conn, int channel, double serverSeconds,
            double heldAheadSeconds)
        {
            conn.Send(new CoreAiServerClockMessage
            {
                ServerUnixSeconds = serverSeconds,
                HeldAheadOfWallSeconds = heldAheadSeconds
            }, channel);
            ClockAnchorsSent++;
            _anchorSent = true;
            _lastAnchorServerSeconds = serverSeconds;
            _lastAnchorHeldSeconds = heldAheadSeconds;
            _lastAnchorProcessSeconds = _wallClock.ProcessTimeSeconds;
        }

        /// <summary>
        /// The server time an anchor carries now: what the attached world's <c>GetServerTimeNow</c>
        /// reads, with how far that is held ahead of the world's running clock; this bridge's wall
        /// clock, never held, when no world is attached or the attached one fails to answer.
        /// </summary>
        private double ReadServerClock(out double heldAheadSeconds)
        {
            heldAheadSeconds = 0d;
            RbxServerClockReader reader = _serverClock;
            if (reader != null)
            {
                try
                {
                    double serverSeconds = reader(out double held);
                    if (!double.IsNaN(serverSeconds) && !double.IsInfinity(serverSeconds))
                    {
                        heldAheadSeconds = held > HeldToleranceSeconds && !double.IsInfinity(held)
                            ? held
                            : 0d;
                        return serverSeconds;
                    }
                }
                catch (Exception exception)
                {
                    // WHY contained: this runs from the frame pump and from Mirror's handler, and a
                    // world that cannot say its time must not stop the rest of the frame.
                    if (!_serverClockFailureLogged)
                    {
                        _serverClockFailureLogged = true;
                        _log("[CoreAI.Mirror] the world's server clock threw; anchors carry this "
                             + "bridge's wall clock instead: " + exception.Message);
                    }
                }
            }

            return _wallClock.UnixTimeSecondsFractional;
        }

        /// <summary>
        /// Sends every acknowledged connection an anchor once the interval has passed, and at once —
        /// reliably — when the server's clock has left the course its clients extrapolate from the
        /// last anchor: held after its wall clock stepped back, released when the hold ended, or
        /// stepped more than <see cref="ClockStepThresholdSeconds"/> away.
        /// </summary>
        /// <remarks>
        /// WHY the periodic ones are unreliable: a lost one is replaced by the next, and a reliable
        /// one that waited for a retransmit would arrive stale — late by exactly the error it exists
        /// to correct. The first anchor, the one that makes the client's clock valid, is reliable.
        /// WHY a step is sent at once: a client learns of it only from an anchor, and until then it
        /// runs on while the server holds — up to an interval of disagreement (A4-08); a step anchor
        /// is reliable because it is sent once, and losing it would cost that whole interval.
        /// </remarks>
        private void SendDueClockAnchors(double now)
        {
            if (_acknowledged.Count == 0)
            {
                return;
            }

            double serverSeconds = ReadServerClock(out double heldAheadSeconds);
            bool stepped = _anchorSent && LeftTheExtrapolatedCourse(serverSeconds, heldAheadSeconds);
            if (!stepped && now < _nextClockAnchorAt)
            {
                return;
            }

            _nextClockAnchorAt = now + ClockAnchorIntervalSeconds;
            foreach (int connectionId in _acknowledged)
            {
                if (NetworkServer.connections.TryGetValue(connectionId,
                        out NetworkConnectionToClient conn))
                {
                    SendClockAnchor(conn, stepped ? Channels.Reliable : Channels.Unreliable,
                        serverSeconds, heldAheadSeconds);
                    if (stepped)
                    {
                        ClockStepAnchorsSent++;
                    }
                }
            }
        }

        /// <summary>
        /// Whether a client carrying the last anchor forward would now misread the server's clock:
        /// the hold began or ended, or the time is farther than
        /// <see cref="ClockStepThresholdSeconds"/> from where the client puts it.
        /// </summary>
        private bool LeftTheExtrapolatedCourse(double serverSeconds, double heldAheadSeconds)
        {
            bool wasHeld = _lastAnchorHeldSeconds > 0d;
            if (wasHeld != heldAheadSeconds > 0d)
            {
                return true;
            }

            double running = _lastAnchorServerSeconds - _lastAnchorHeldSeconds
                             + (_wallClock.ProcessTimeSeconds - _lastAnchorProcessSeconds);
            double predicted = wasHeld && _lastAnchorServerSeconds > running
                ? _lastAnchorServerSeconds
                : running;
            return Math.Abs(serverSeconds - predicted) > ClockStepThresholdSeconds;
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
            if (_isServer || _disposed || !IsAdmittedOnTheLiveConnection() || !NetworkClient.isConnected)
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
                    // WHY dequeued before the call: with nobody listening, `EventReceived?.Invoke(...)`
                    // skips its argument, so the event was never taken off the queue and a local fire
                    // spun here forever, freezing the host's main thread.
                    RbxNetworkEventMessage next = _localEvents.Dequeue();
                    EventReceived?.Invoke(next);
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
            bool wasBound = _peersByConnection.TryGetValue(connectionId, out RbxNetworkPeer peer);
            if (wasBound)
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
            if (wasBound)
            {
                // WHY before the open requests fail: their completions are mod code, and one that
                // throws must not leave the session host holding a session nothing is bound to.
                BindingReleased?.Invoke(connectionId);
            }

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

        /// <summary>
        /// Drops one client-to-server envelope fired before the server admitted this client that
        /// cannot be held for the admission — an unreliable remote, or any on a disposed bridge:
        /// counted, never sent, never charged, and said once until an admission is bound.
        /// </summary>
        private void DropUnadmittedSend()
        {
            UnadmittedSendsDropped++;
            if (_unadmittedSendLogged)
            {
                return;
            }

            _unadmittedSendLogged = true;
            _log("[CoreAI.Mirror] an unreliable remote was fired before the server admitted this "
                 + "client; it and any that follow are dropped and counted, not sent, until the "
                 + "admission response binds the actor (reliable remotes and InvokeServer calls are "
                 + "held and sent right after it)");
        }

        /// <summary>
        /// Whether this client's admission is bound on the connection Mirror holds now, or was bound
        /// while Mirror held none; always false on a server.
        /// </summary>
        private bool IsAdmittedOnTheLiveConnection()
        {
            return !_isServer && _admittedActorId != null
                   && (_admittedOn == null || ReferenceEquals(_admittedOn, NetworkClient.connection));
        }

        /// <summary>
        /// On a client, forgets what belongs to a connection that is no longer Mirror's live one: the
        /// sends held for an admission on a connection that closed, and an admission bound on an
        /// older connection together with the requests that left on it.
        /// </summary>
        /// <remarks>
        /// WHY at every client entry point and not only in the frame pump: Mirror can stop and start
        /// a client within one frame, before any pump sees it stopped, and a send made meanwhile
        /// must not leave on the newer connection as the older one's admitted actor — the server's
        /// handlers require authentication, and Mirror would disconnect the joining client for it
        /// (B1-08). WHY a binding made while Mirror held no connection takes the next one: a
        /// composition may bind before Mirror connects, and that binding is meant for the
        /// connection that follows. WHY the requests' completions are contained: this runs inside
        /// a script's own FireServer and inside Mirror's admission handler, where another script's
        /// throw must not surface.
        /// </remarks>
        private void ReconcileWithTheLiveConnection()
        {
            if (_isServer || _disposed)
            {
                return;
            }

            NetworkConnectionToServer live = NetworkClient.connection;
            if (_heldUntilAdmitted.Count > 0
                && (!NetworkClient.isConnected || !ReferenceEquals(_heldOn, live)))
            {
                DiscardHeldUntilAdmitted();
            }

            if (_admittedActorId == null || live == null)
            {
                return;
            }

            if (_admittedOn == null)
            {
                _admittedOn = live;
                return;
            }

            if (ReferenceEquals(_admittedOn, live))
            {
                return;
            }

            _admittedActorId = null;
            _admittedOn = null;
            _readyAcknowledgedOn = null;
            FailAllPending(NotConnectedReason, containCompletionFailures: true);
        }

        /// <summary>Whether the hold for this client's admission takes one more payload of this size.</summary>
        private bool HasRoomUntilAdmitted(byte[] payload)
        {
            return _heldUntilAdmitted.Count < MaxHeldSendsUntilAdmitted
                   && _heldUntilAdmittedBytes + (payload?.Length ?? 0) <= MaxHeldBytesUntilAdmitted;
        }

        /// <summary>Holds one reliable client envelope for the admission of the live connection.</summary>
        private void HoldUntilAdmitted(HeldSend held)
        {
            _heldOn = NetworkClient.connection;
            _heldUntilAdmitted.Add(held);
            _heldUntilAdmittedBytes += held.Payload?.Length ?? 0;
            SendsHeldUntilAdmitted++;
        }

        /// <summary>
        /// Sends what this client held for its admission, in the order it was fired, on the
        /// connection it was held for; a call that already failed — timed out while held — is not
        /// sent, and a hold whose connection is gone is dropped instead.
        /// </summary>
        private void SendHeldUntilAdmitted()
        {
            if (_heldUntilAdmitted.Count == 0)
            {
                return;
            }

            if (!NetworkClient.isConnected || !ReferenceEquals(_heldOn, NetworkClient.connection))
            {
                DiscardHeldUntilAdmitted();
                return;
            }

            HeldSend[] held = _heldUntilAdmitted.ToArray();
            _heldUntilAdmitted.Clear();
            _heldUntilAdmittedBytes = 0;
            _heldOn = null;
            for (int index = 0; index < held.Length; index++)
            {
                HeldSend send = held[index];
                if (send.IsRequest)
                {
                    if (!_pending.ContainsKey(send.Request.CorrelationId))
                    {
                        continue;
                    }

                    NetworkClient.Send(send.Request);
                }
                else
                {
                    NetworkClient.Send(send.Event, Channels.Reliable);
                }

                CountClientSend(send.Payload);
            }
        }

        /// <summary>
        /// Drops what this client held for an admission that will not come on the connection it was
        /// held for: counted in <see cref="UnsentPacketsDropped"/>, said once per hold, and every
        /// InvokeServer among it failed now.
        /// </summary>
        private void DiscardHeldUntilAdmitted()
        {
            _heldOn = null;
            _heldUntilAdmittedBytes = 0;
            _admissionHoldOverflowLogged = false;
            if (_heldUntilAdmitted.Count == 0)
            {
                return;
            }

            HeldSend[] discarded = _heldUntilAdmitted.ToArray();
            _heldUntilAdmitted.Clear();
            UnsentPacketsDropped += discarded.Length;
            _log("[CoreAI.Mirror] the connection closed before the server admitted this client; the "
                 + discarded.Length + " remotes held for the admission are dropped and counted, and "
                 + "the InvokeServer calls among them fail");
            for (int index = 0; index < discarded.Length; index++)
            {
                if (!discarded[index].IsRequest
                    || !_pending.Remove(discarded[index].Request.CorrelationId, out PendingRequest request))
                {
                    continue;
                }

                try
                {
                    request.Complete?.Invoke(RbxNetworkResponse.Failure(HeldConnectionClosedReason));
                }
                catch (Exception exception)
                {
                    // WHY contained: see ReconcileWithTheLiveConnection; one script's throw must not
                    // stop the other held calls from failing.
                    _log("[CoreAI.Mirror] a RemoteFunction completion threw while the calls held for "
                         + "an admission were failed: " + exception.Message);
                }
            }
        }

        /// <summary>
        /// Drops one reliable client envelope past the bound of the hold for this client's admission:
        /// counted, never sent, never charged, and said once per unadmitted stretch.
        /// </summary>
        private void DropPastTheAdmissionHold()
        {
            AdmissionHoldOverflowDrops++;
            if (_admissionHoldOverflowLogged)
            {
                return;
            }

            _admissionHoldOverflowLogged = true;
            _log("[CoreAI.Mirror] this client has not been admitted yet and already holds "
                 + _heldUntilAdmitted.Count + " remotes (" + _heldUntilAdmittedBytes + " bytes) for "
                 + "the admission; this remote and any that follow past the bound are dropped and "
                 + "counted, and an InvokeServer among them fails");
        }
    }
}
