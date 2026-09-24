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

        [Tooltip("Seconds a connection has to send its admission request before it is dropped. "
            + "Mirror has no authentication timeout of its own; without this a peer that never "
            + "speaks keeps its connection slot for ever.")]
        [SerializeField]
        private float admissionTimeoutSeconds = DefaultAdmissionTimeoutSeconds;

        private readonly Dictionary<int, ActorAdmissionResult> _admitted = new();
        private readonly Dictionary<int, AdmissionAttempt> _attempts = new();
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
            NetworkClient.Send(new CoreAiAdmissionRequestMessage
            {
                Credential = _clientCredential() ?? Array.Empty<byte>()
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

        private void Update()
        {
            if (NetworkServer.active)
            {
                EnforceAdmissionDeadline();
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

            ActorAdmissionResult result =
                Decide(conn.connectionId, conn.address, message.Credential);
            conn.Send(Respond(result));

            if (result.Admitted)
            {
                ServerAccept(conn);
                return;
            }

            ServerReject(conn);
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
                ActorId = admitted ? result.Context.ActorId : ""
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

        private void OnAdmissionResponse(CoreAiAdmissionResponseMessage message)
        {
            if (message.Admitted)
            {
                // WHY stored before ClientAccept: the composition binds the bridge from the
                // OnClientAuthenticated listener that ClientAccept fires, and reads the id here.
                ClientActorId = message.ActorId;
                ClientAccept();
                return;
            }

            ClientActorId = null;
            ClientReject();
        }
    }
}
