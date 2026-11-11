using System;
using System.Collections.Generic;
using System.Globalization;
using CoreAI.Authority;
using Mirror;
using UnityEngine;

namespace CoreAI.Net.Mirror
{
    /// <summary>
    /// Admits connections to a CoreAI world through the host's own <see cref="IActorAdmissionProvider"/>.
    /// </summary>
    /// <remarks>
    /// WHY admission is a Mirror authenticator and not a first-packet check inside the bridge: an
    /// authenticator runs BEFORE the connection is authenticated, which is the only point at which
    /// nothing exists yet — no Player, no chat session, no mod ownership, no world access. Deciding
    /// later means deciding after something has already been created for a stranger.
    /// <para>
    /// There is no anonymous path. A composition with no provider refuses every connection rather
    /// than admitting them all, because the failure mode of the other choice is an open server that
    /// looks like it is working.
    /// </para>
    /// <para>
    /// One attempt per connection, and a deadline for it. A connection's first admission request is
    /// decided; every later one on the same connection is ignored and counted, so a connection can
    /// neither mint a second Player by asking again nor retry credentials in the window before its
    /// refusal takes effect. A connection that sends no request within
    /// <see cref="AdmissionTimeoutSeconds"/> is dropped: Mirror has no authentication timeout of its
    /// own, so without this a peer that never speaks holds its connection slot for ever.
    /// </para>
    /// <para>
    /// The protocol first. A request names the CoreAI Mirror protocol its client speaks
    /// (<see cref="CoreAiMirrorProtocol.Version"/>); one of another protocol — or none, from a client
    /// older than the field — is refused before the host's provider is asked, with a reason that
    /// names both protocols. A client refuses, the same way, an admission from a server that speaks
    /// another protocol or names none.
    /// </para>
    /// <para>
    /// A refused client learns why. The refusal is sent, and the connection is dropped on a later
    /// frame: Mirror discards what a connection has not flushed when it is dropped, and kcp2k drops
    /// before the call returns, so a drop made with the refusal would have sent nothing. Meanwhile
    /// the connection reaches nothing — it is not authenticated, and its one attempt is spent. The
    /// client keeps the reason after the connection closes (<see cref="ClientRefusal"/>).
    /// </para>
    /// </remarks>
    [AddComponentMenu("CoreAI/CoreAI Mirror Authenticator")]
    public sealed class CoreAiMirrorAuthenticator : NetworkAuthenticator
    {
        /// <summary>The admission deadline a connection gets when none is configured.</summary>
        public const float DefaultAdmissionTimeoutSeconds = 10f;

        /// <summary>What a rejected client is told. Deliberately uninformative.</summary>
        private const string ClientFacingRejection = "not admitted";

        private enum AttemptState
        {
            AwaitingRequest,
            Decided,
            TimedOut
        }

        /// <summary>
        /// Where one connection's admission stands; held with the connection object, because kcp2k
        /// reuses connection ids and a record must never be mistaken for the next peer's.
        /// </summary>
        private sealed class AdmissionAttempt
        {
            public NetworkConnectionToClient Connection;
            public double ConnectedAt;
            public AttemptState State;
            public bool IgnoredLogged;
        }

        /// <summary>A refused connection whose drop waits for its refusal to be flushed.</summary>
        private readonly struct OwedRefusal
        {
            public OwedRefusal(NetworkConnectionToClient connection, double refusedAt)
            {
                Connection = connection;
                RefusedAt = refusedAt;
            }

            public NetworkConnectionToClient Connection { get; }

            public double RefusedAt { get; }
        }

        [Tooltip("Seconds a connection has to send its admission request before it is dropped. "
            + "Mirror has no authentication timeout of its own; without this a peer that never "
            + "speaks keeps its connection slot for ever.")]
        [SerializeField]
        private float admissionTimeoutSeconds = DefaultAdmissionTimeoutSeconds;

        private readonly Dictionary<int, ActorAdmissionResult> _admitted = new();
        private readonly Dictionary<int, AdmissionAttempt> _attempts = new();
        private readonly List<OwedRefusal> _owedRefusals = new();
        private IActorAdmissionProvider _provider;
        private string _worldId = "";
        private Func<byte[]> _clientCredential = Array.Empty<byte>;
        private Action<string> _log;

        /// <summary>Admissions granted so far, for the gate that counts them.</summary>
        public int AdmittedCount { get; private set; }

        /// <summary>Admissions refused so far.</summary>
        public int RejectedCount { get; private set; }

        /// <summary>
        /// Admission requests ignored because their connection had already had its one attempt, was
        /// already authenticated, or had run past its admission deadline. The provider never saw
        /// them and nothing was created for them.
        /// </summary>
        public int IgnoredAdmissionRequests { get; private set; }

        /// <summary>Connections dropped because they sent no admission request in time.</summary>
        public int AdmissionTimeouts { get; private set; }

        /// <summary>
        /// Admission requests refused because their client speaks another CoreAI Mirror protocol, or
        /// names none; the host's provider never saw them. Counted in <see cref="RejectedCount"/> too.
        /// </summary>
        public int ProtocolMismatchRefusals { get; private set; }

        /// <summary>
        /// Refused connections this server dropped a frame after their refusal was sent; a client
        /// that disconnected on its refusal first is not counted.
        /// </summary>
        public int RefusalDropsPerformed { get; private set; }

        /// <summary>
        /// On a client, why the last admission failed — refused by the server, or the server
        /// refused by this client for its protocol; null until one fails, kept after the
        /// connection closes so the game can show it, and cleared when the next attempt starts.
        /// </summary>
        public CoreAiAdmissionRefusal? ClientRefusal { get; private set; }

        /// <summary>
        /// On a client, raised when an admission fails, before the connection closes; a listener's
        /// throw is logged and the connection still closes.
        /// </summary>
        public event Action<CoreAiAdmissionRefusal> ClientRefused;

        /// <summary>
        /// Seconds a connection has, from connecting, to send its admission request. Values that
        /// are not positive read as <see cref="DefaultAdmissionTimeoutSeconds"/>: a deadline that
        /// never comes is the hole this closes.
        /// </summary>
        public float AdmissionTimeoutSeconds
        {
            get => admissionTimeoutSeconds > 0f ? admissionTimeoutSeconds : DefaultAdmissionTimeoutSeconds;
            set
            {
                if (!(value > 0f) || float.IsInfinity(value))
                {
                    throw new ArgumentOutOfRangeException(nameof(value), value,
                        "the admission deadline must be a positive, finite number of seconds");
                }

                admissionTimeoutSeconds = value;
            }
        }

        /// <summary>
        /// The actor this process was admitted as when it joined as a client; null before the
        /// server answers, after a refusal, and after the client stops.
        /// </summary>
        public string ClientActorId { get; private set; }

        /// <summary>Test seam: the clock admission deadlines run on; null means Mirror's own.</summary>
        internal Func<double> ClockSeconds { get; set; }

        /// <summary>
        /// Wires the host's provider. Call before <c>StartServer</c>.
        /// </summary>
        public void Configure(IActorAdmissionProvider provider, string worldId,
            Action<string> log = null)
        {
            _provider = provider;
            _worldId = worldId ?? "";
            _log = log;
        }

        /// <summary>Supplies the credential this process sends when it joins as a client.</summary>
        public void ConfigureClientCredential(Func<byte[]> credential)
        {
            _clientCredential = credential ?? (() => Array.Empty<byte>());
        }

        /// <summary>The admission result for a connection, or null when it was never admitted.</summary>
        public ActorAdmissionResult ResultFor(int connectionId)
        {
            return _admitted.TryGetValue(connectionId, out ActorAdmissionResult result)
                ? result
                : null;
        }

        /// <summary>Forgets a connection's admission when it disconnects.</summary>
        public void Forget(int connectionId)
        {
            _admitted.Remove(connectionId);
            _attempts.Remove(connectionId);
        }

        /// <inheritdoc />
        public override void OnStartServer()
        {
            NetworkServer.RegisterHandler<CoreAiAdmissionRequestMessage>(
                OnAdmissionRequest, requireAuthentication: false);
        }

        /// <inheritdoc />
        public override void OnStopServer()
        {
            NetworkServer.UnregisterHandler<CoreAiAdmissionRequestMessage>();
            _admitted.Clear();
            _attempts.Clear();
            _owedRefusals.Clear();
        }

        /// <inheritdoc />
        public override void OnStartClient()
        {
            NetworkClient.RegisterHandler<CoreAiAdmissionResponseMessage>(
                OnAdmissionResponse, requireAuthentication: false);
        }

        /// <inheritdoc />
        public override void OnStopClient()
        {
            NetworkClient.UnregisterHandler<CoreAiAdmissionResponseMessage>();
            ClientActorId = null;
        }

        /// <inheritdoc />
        public override void OnClientAuthenticate()
        {
            ClientRefusal = null;
            NetworkClient.Send(new CoreAiAdmissionRequestMessage
            {
                Credential = _clientCredential() ?? Array.Empty<byte>(),
                ProtocolVersion = CoreAiMirrorProtocol.Version
            });
        }

        /// <inheritdoc />
        /// <remarks>
        /// Nothing is admitted here: the server waits for the credential message rather than
        /// admitting on connection. What starts here is the connection's admission deadline, which
        /// <see cref="EnforceAdmissionDeadline"/> enforces every frame — Mirror has no
        /// authentication timeout of its own, and a connection that never sends a request would
        /// otherwise hold its slot, having reached nothing, for as long as it likes.
        /// </remarks>
        public override void OnServerAuthenticate(NetworkConnectionToClient conn)
        {
            if (conn == null)
            {
                return;
            }

            if (_attempts.TryGetValue(conn.connectionId, out AdmissionAttempt attempt)
                && ReferenceEquals(attempt.Connection, conn))
            {
                return;
            }

            _attempts[conn.connectionId] = new AdmissionAttempt
            {
                Connection = conn,
                ConnectedAt = Now(),
                State = AttemptState.AwaitingRequest
            };
        }

        /// <summary>
        /// Drops every live connection that has been waiting longer than
        /// <see cref="AdmissionTimeoutSeconds"/> without sending its admission request, and forgets
        /// the records of connections that are gone. Runs every frame from <c>Update</c>.
        /// </summary>
        /// <remarks>
        /// WHY here and not in the bridge provider: the deadline is part of admission, and a host
        /// that runs this authenticator without the provider must not be left without it. WHY the
        /// connection object is compared and not only the id: a record whose connection left and
        /// whose id kcp2k gave to someone new is that someone's business, not this record's.
        /// </remarks>
        internal void EnforceAdmissionDeadline()
        {
            if (_attempts.Count == 0)
            {
                return;
            }

            double now = Now();
            double timeout = AdmissionTimeoutSeconds;
            List<int> gone = null;
            List<NetworkConnectionToClient> expired = null;
            foreach (KeyValuePair<int, AdmissionAttempt> pair in _attempts)
            {
                AdmissionAttempt attempt = pair.Value;
                if (!NetworkServer.connections.TryGetValue(pair.Key, out NetworkConnectionToClient live)
                    || !ReferenceEquals(live, attempt.Connection))
                {
                    gone ??= new List<int>();
                    gone.Add(pair.Key);
                    continue;
                }

                if (attempt.State == AttemptState.AwaitingRequest
                    && !attempt.Connection.isAuthenticated
                    && now - attempt.ConnectedAt >= timeout)
                {
                    attempt.State = AttemptState.TimedOut;
                    expired ??= new List<NetworkConnectionToClient>();
                    expired.Add(attempt.Connection);
                }
            }

            for (int index = 0; gone != null && index < gone.Count; index++)
            {
                _attempts.Remove(gone[index]);
            }

            for (int index = 0; expired != null && index < expired.Count; index++)
            {
                NetworkConnectionToClient conn = expired[index];
                AdmissionTimeouts++;
                _log?.Invoke("[CoreAI.Mirror] connection " + conn.connectionId
                             + " sent no admission request within "
                             + timeout.ToString("0.###", CultureInfo.InvariantCulture)
                             + " seconds; it is dropped");
                conn.Disconnect();
            }
        }

        /// <summary>
        /// Decides one connection's admission, without touching the transport.
        /// </summary>
        /// <remarks>
        /// WHY the decision is separable from the message handler: this is the security boundary of
        /// the whole online rung, and a rule that can only be exercised through a live socket is a
        /// rule that gets tested rarely, late, and never in the failure cases that matter. The
        /// once-per-connection rule lives in the message handler, which is where a connection asks.
        /// </remarks>
        public ActorAdmissionResult Decide(int connectionId, string address, byte[] credential)
        {
            if (_provider == null)
            {
                // WHY refuse rather than allow: a composition that forgot to configure admission is
                // a misconfigured server, and the safe reading of "I do not know who you are" is no.
                return Refused(connectionId,
                    "no IActorAdmissionProvider is configured on this host");
            }

            ActorAdmissionResult result;
            try
            {
                result = _provider.TryAdmit(new ActorCredential(credential, address), _worldId);
            }
            catch (Exception exception)
            {
                // A provider that throws has not admitted anyone. Treating an exception as a pass
                // would turn every bug in a host's authentication into an open door.
                return Refused(connectionId, "the admission provider threw: " + exception.Message);
            }

            if (result == null || !result.Admitted)
            {
                return Refused(connectionId,
                    result == null ? "the provider returned no decision" : result.Reason);
            }

            _admitted[connectionId] = result;
            AdmittedCount++;
            return result;
        }

        /// <summary>
        /// Drops every refused connection whose refusal was sent on an earlier frame, while Mirror
        /// still holds that same connection. Runs every frame from <c>Update</c>.
        /// </summary>
        /// <remarks>
        /// WHY "an earlier frame" is read as a later clock reading: Mirror's frame clock is fixed
        /// within a frame, so a later reading comes after the late update that flushed the refusal.
        /// WHY the connection object is compared: kcp2k reuses connection ids, and a refused client
        /// that left on its own may have a stranger on its id by now.
        /// </remarks>
        internal void PerformDueRefusalDrops()
        {
            PerformRefusalDrops(Now(), all: false);
        }

        /// <summary>
        /// Drops every refused connection at once, without waiting for a later frame: for a
        /// composition that stops updating this component. A refusal sent in this same frame may
        /// be lost; the connection is closed either way.
        /// </summary>
        public void PerformOwedRefusalDropsNow()
        {
            PerformRefusalDrops(0d, all: true);
        }

        private void Update()
        {
            if (NetworkServer.active)
            {
                PerformDueRefusalDrops();
                EnforceAdmissionDeadline();
            }
        }

        /// <remarks>
        /// WHY here as well as in Update: a disabled component runs no Update, and a refused
        /// connection must not stay open for as long as it is off.
        /// </remarks>
        private void OnDisable()
        {
            if (NetworkServer.active)
            {
                PerformOwedRefusalDropsNow();
            }
        }

        private void OnAdmissionRequest(NetworkConnectionToClient conn,
            CoreAiAdmissionRequestMessage message)
        {
            if (conn == null)
            {
                return;
            }

            if (!TryBeginAttempt(conn))
            {
                return;
            }

            if (message.ProtocolVersion != CoreAiMirrorProtocol.Version)
            {
                RefuseProtocol(conn, message.ProtocolVersion);
                return;
            }

            ActorAdmissionResult result =
                Decide(conn.connectionId, conn.address, message.Credential);
            conn.Send(Respond(result));

            if (result.Admitted)
            {
                ServerAccept(conn);
                return;
            }

            OweRefusalDrop(conn);
        }

        /// <summary>
        /// Refuses a client of another protocol without asking the host's provider: counted, logged
        /// for the host, answered with both protocols, and dropped on a later frame.
        /// </summary>
        /// <remarks>
        /// WHY before the provider: a client that cannot read what follows an admission must not be
        /// admitted whatever its credential, and the provider — the host's own authentication,
        /// perhaps a web call — has no reason to run for it.
        /// </remarks>
        private void RefuseProtocol(NetworkConnectionToClient conn, int clientVersion)
        {
            ProtocolMismatchRefusals++;
            RejectedCount++;
            _log?.Invoke("[CoreAI.Mirror] admission refused for connection " + conn.connectionId + ": "
                         + CoreAiMirrorProtocol.MismatchReason(CoreAiMirrorProtocol.Version, clientVersion,
                             localIsServer: true));
            conn.Send(RespondProtocolMismatch(clientVersion));
            OweRefusalDrop(conn);
        }

        private void OweRefusalDrop(NetworkConnectionToClient conn)
        {
            for (int index = 0; index < _owedRefusals.Count; index++)
            {
                if (ReferenceEquals(_owedRefusals[index].Connection, conn))
                {
                    return;
                }
            }

            _owedRefusals.Add(new OwedRefusal(conn, Now()));
        }

        private void PerformRefusalDrops(double now, bool all)
        {
            if (_owedRefusals.Count == 0)
            {
                return;
            }

            List<NetworkConnectionToClient> due = null;
            for (int index = _owedRefusals.Count - 1; index >= 0; index--)
            {
                OwedRefusal owed = _owedRefusals[index];
                if (all || now > owed.RefusedAt)
                {
                    due ??= new List<NetworkConnectionToClient>();
                    due.Add(owed.Connection);
                    _owedRefusals.RemoveAt(index);
                }
            }

            for (int index = due == null ? -1 : due.Count - 1; index >= 0; index--)
            {
                NetworkConnectionToClient conn = due[index];
                if (NetworkServer.connections.TryGetValue(conn.connectionId, out NetworkConnectionToClient live)
                    && ReferenceEquals(live, conn))
                {
                    RefusalDropsPerformed++;
                    ServerReject(conn);
                }
            }
        }

        /// <summary>
        /// Opens a connection's one admission attempt, or ignores and counts a request that has none
        /// left.
        /// </summary>
        /// <remarks>
        /// WHY a second request is ignored rather than decided: every accept raises
        /// <c>OnServerAuthenticated</c> again, so a connection that could ask twice would mint a
        /// second Player per request — ghosts no disconnect would ever remove — and call the host's
        /// provider as often as it liked. WHY ignored rather than disconnected: the connection's
        /// first session is legitimate and stays untouched; the repeat simply reaches nothing.
        /// </remarks>
        private bool TryBeginAttempt(NetworkConnectionToClient conn)
        {
            bool known = _attempts.TryGetValue(conn.connectionId, out AdmissionAttempt attempt)
                         && ReferenceEquals(attempt.Connection, conn);
            if (!known)
            {
                attempt = new AdmissionAttempt
                {
                    Connection = conn,
                    ConnectedAt = Now(),
                    State = AttemptState.AwaitingRequest
                };
                _attempts[conn.connectionId] = attempt;
            }

            if (attempt.State == AttemptState.AwaitingRequest && !conn.isAuthenticated)
            {
                attempt.State = AttemptState.Decided;
                return true;
            }

            if (attempt.State == AttemptState.AwaitingRequest)
            {
                attempt.State = AttemptState.Decided;
            }

            IgnoredAdmissionRequests++;
            if (!attempt.IgnoredLogged)
            {
                attempt.IgnoredLogged = true;
                _log?.Invoke("[CoreAI.Mirror] connection " + conn.connectionId
                             + " asked for admission after its one attempt was used or its deadline "
                             + "passed; this and any further requests on it are ignored and counted");
            }

            return false;
        }

        /// <summary>
        /// What the client is told about a decision: on acceptance the actor it now is, on refusal
        /// the fixed rejection and nothing else.
        /// </summary>
        /// <remarks>
        /// WHY separable from the handler, like <see cref="Decide"/>: what a rejected client learns
        /// is a security property, and this is the one place that decides it.
        /// </remarks>
        internal static CoreAiAdmissionResponseMessage Respond(ActorAdmissionResult result)
        {
            bool admitted = result != null && result.Admitted;
            return new CoreAiAdmissionResponseMessage
            {
                Admitted = admitted,
                Reason = admitted ? "" : ClientFacingRejection,
                ActorId = admitted ? result.Context.ActorId : "",
                ProtocolVersion = CoreAiMirrorProtocol.Version,
                Refusal = (byte)(admitted ? CoreAiAdmissionRefusalKind.None : CoreAiAdmissionRefusalKind.NotAdmitted)
            };
        }

        /// <summary>What a client of another protocol is told: both protocols, and nothing about anyone.</summary>
        internal static CoreAiAdmissionResponseMessage RespondProtocolMismatch(int clientVersion)
        {
            return new CoreAiAdmissionResponseMessage
            {
                Admitted = false,
                Reason = CoreAiMirrorProtocol.MismatchReason(CoreAiMirrorProtocol.Version, clientVersion,
                    localIsServer: true),
                ActorId = "",
                ProtocolVersion = CoreAiMirrorProtocol.Version,
                Refusal = (byte)CoreAiAdmissionRefusalKind.ProtocolMismatch
            };
        }

        private ActorAdmissionResult Refused(int connectionId, string reason)
        {
            RejectedCount++;
            // The detailed reason goes to the host's log only. Telling the client which half of a
            // forged credential to fix is the one thing a rejection must never do.
            _log?.Invoke("[CoreAI.Mirror] admission refused for connection " + connectionId
                         + ": " + reason);
            return ActorAdmissionResult.Reject(string.IsNullOrWhiteSpace(reason)
                ? "refused without a stated reason"
                : reason);
        }

        private double Now()
        {
            return ClockSeconds?.Invoke() ?? NetworkTime.localTime;
        }

        /// <remarks>
        /// WHY a client refuses an admission itself: a server of another protocol — or one too old to
        /// name its protocol — admitted a client whose messages it cannot read, and the two would
        /// half-work until the first message one of them does not know; closing here says why.
        /// </remarks>
        private void OnAdmissionResponse(CoreAiAdmissionResponseMessage message)
        {
            if (!message.Admitted)
            {
                ClientActorId = null;
                bool mismatch = message.Refusal == (byte)CoreAiAdmissionRefusalKind.ProtocolMismatch;
                RecordClientRefusal(new CoreAiAdmissionRefusal(
                    mismatch ? CoreAiAdmissionRefusalKind.ProtocolMismatch : CoreAiAdmissionRefusalKind.NotAdmitted,
                    string.IsNullOrWhiteSpace(message.Reason) ? ClientFacingRejection : message.Reason,
                    message.ProtocolVersion, refusedByServer: true));
                ClientReject();
                return;
            }

            if (message.ProtocolVersion != CoreAiMirrorProtocol.Version)
            {
                ClientActorId = null;
                RecordClientRefusal(new CoreAiAdmissionRefusal(CoreAiAdmissionRefusalKind.ProtocolMismatch,
                    CoreAiMirrorProtocol.MismatchReason(CoreAiMirrorProtocol.Version, message.ProtocolVersion,
                        localIsServer: false),
                    message.ProtocolVersion, refusedByServer: false));
                ClientReject();
                return;
            }

            // WHY stored before ClientAccept: the composition binds the bridge from the
            // OnClientAuthenticated listener that ClientAccept fires, and reads the id here.
            ClientActorId = message.ActorId;
            ClientAccept();
        }

        /// <summary>Keeps, says and raises a failed admission on a client, before the connection closes.</summary>
        private void RecordClientRefusal(CoreAiAdmissionRefusal refusal)
        {
            ClientRefusal = refusal;
            Debug.LogWarning("[CoreAI.Mirror] admission failed (" + refusal.Kind + ", "
                             + (refusal.RefusedByServer ? "refused by the server" : "this client refused the server")
                             + "): " + refusal.Reason);
            try
            {
                ClientRefused?.Invoke(refusal);
            }
            catch (Exception exception)
            {
                // WHY contained: this runs inside Mirror's handler, and the connection must close
                // whatever a listener does.
                Debug.LogWarning("[CoreAI.Mirror] a ClientRefused listener threw: " + exception.Message);
            }
        }
    }
}
