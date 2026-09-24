using System;
using System.Collections.Generic;
using System.Globalization;
using CoreAI.Authority;
using CoreAI.Mods.Rbx.Instances.Networking;
using Mirror;
using UnityEngine;

namespace CoreAI.Net.Mirror
{
    /// <summary>
    /// Turns an admitted connection into a world citizen, and a lost connection back into nothing.
    /// </summary>
    /// <remarks>
    /// WHY this sits between the authenticator and the world instead of inside either: admission
    /// decides WHO may join and the world decides WHAT a player is; joining them in one class would
    /// mean the security decision and the gameplay object share a lifetime, and the failure that
    /// causes — a rejected connection that still created a Player — is the exact thing MVP11 forbids.
    /// Here the order is explicit: admit, bind the connection, then create the actor. Nothing before
    /// the admission returns.
    /// <para>
    /// One session per actor, newest wins: an actor admitted again on a new connection — a player
    /// who reconnected before the transport gave up on the old link — keeps its Player, and the
    /// older connection is closed without a teardown, the way Roblox ends the older session. Every
    /// teardown is keyed by the connection, so the older connection's late drop reaches nothing.
    /// </para>
    /// <para>
    /// A session the world ends itself — a host's own <c>DisconnectActor</c>, outside a kick or a
    /// transport drop — is forgotten here when the bridge releases its connection: the world has
    /// already torn the actor down, and the bridge ends the connection (A4-09).
    /// </para>
    /// <para>
    /// Mirror host mode is not supported: the host's own local client connection is refused as a
    /// world player, loudly, and left to Mirror (A4-10). Its player is a host-local actor, served in
    /// process.
    /// </para>
    /// </remarks>
    public sealed class CoreAiMirrorSessionHost : IRbxActorIdentitySource, IDisposable
    {
        private readonly Dictionary<string, ActorAdmissionResult> _identitiesByActor =
            new(StringComparer.Ordinal);
        private readonly Dictionary<int, ActorContext> _actorsByConnection = new();
        private readonly MirrorNetworkBridge _bridge;
        private readonly Func<ActorContext, bool> _connectActor;
        private readonly Func<ActorContext, bool> _disconnectActor;
        private readonly Action<string> _log;
        private bool _disposed;

        /// <summary>
        /// Wires the session host to a bridge and the world's connect/disconnect entry points.
        /// </summary>
        /// <param name="bridge">The server bridge whose connections this host turns into players.</param>
        /// <param name="connectActor">The world's entry point that creates a player.</param>
        /// <param name="disconnectActor">The world's entry point that removes one.</param>
        /// <param name="log">Where a refused host-mode connection is said; null means a Unity error.</param>
        public CoreAiMirrorSessionHost(MirrorNetworkBridge bridge,
            Func<ActorContext, bool> connectActor, Func<ActorContext, bool> disconnectActor,
            Action<string> log = null)
        {
            _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
            _connectActor = connectActor ?? throw new ArgumentNullException(nameof(connectActor));
            _disconnectActor = disconnectActor
                               ?? throw new ArgumentNullException(nameof(disconnectActor));
            _log = log ?? Debug.LogError;
            _bridge.PeerDisconnected += OnPeerDisconnected;
            _bridge.BindingReleased += OnBindingReleased;
        }

        /// <summary>How many connections currently hold a live actor.</summary>
        public int LiveSessionCount => _actorsByConnection.Count;

        /// <summary>
        /// Sessions ended because their actor was admitted again on a newer connection; the Player
        /// carried over and no teardown ran for them.
        /// </summary>
        public int SupersededSessions { get; private set; }

        /// <summary>Whether one connection currently holds a live actor.</summary>
        public bool HasLiveSession(int connectionId) => _actorsByConnection.ContainsKey(connectionId);

        /// <summary>
        /// Admissions refused because the connection is Mirror's host-mode local client, which this
        /// host never turns into a world player.
        /// </summary>
        public int HostModeConnectionsRefused { get; private set; }

        /// <summary>
        /// Admits one connection into the world. Returns false when the decision was a refusal, in
        /// which case nothing at all was created for it.
        /// </summary>
        /// <remarks>
        /// WHY a connection that already holds a session is not admitted again: one connection is
        /// one session. The same actor asking twice gets the session it has, and nothing is created
        /// twice; another actor is refused and the session the connection holds is left as it was —
        /// a connection must never switch to a second identity, or mint a second Player, by asking
        /// again. WHY the older session of the same actor is dropped before the new binding: the
        /// bridge releases the older connection while binding and the transport reports its drop
        /// later — a frame later, once the older client has been told why — and whenever that
        /// report comes this host must no longer hold the older session, or it would tear down the
        /// Player the new session is carrying over. WHY the binding waits for the client's
        /// readiness: this runs one round trip before the client has read its admission, so the
        /// bridge holds server remotes for the connection until the client says it can route them.
        /// </remarks>
        public bool Admit(int connectionId, ActorAdmissionResult admission, string sessionId)
        {
            if (admission == null || !admission.Admitted)
            {
                return false;
            }

            if (IsHostModeLocalConnection(connectionId))
            {
                // WHY refused, loudly, and left connected: the host's own client has no CoreAI client
                // bridge beside a server one, so it never acknowledges readiness — admitted, it was a
                // Player for ten seconds and was then dropped from its own host with a line blaming
                // an old client (A4-10). Dropping it instead would take the host's local player off
                // Mirror altogether; refused, it keeps Mirror's own features and CoreAI serves the
                // host's player in process.
                HostModeConnectionsRefused++;
                _log("[CoreAI.Mirror] host mode is not supported: connection " + connectionId
                     + " is this host's own local Mirror client, which the CoreAI Mirror bridge "
                     + "cannot serve as a remote player; it is not admitted to the world and stays "
                     + "connected to Mirror. Run the host's player as a host-local actor "
                     + "(LuaCsRbxApiBindings.ConnectActor), which is served in process");
                return false;
            }

            ActorContext context = admission.Context;
            if (_actorsByConnection.TryGetValue(connectionId, out ActorContext current))
            {
                return string.Equals(current.ActorId, context.ActorId, StringComparison.Ordinal);
            }

            if (TryFindSession(context.ActorId, out int older))
            {
                _actorsByConnection.Remove(older);
                SupersededSessions++;
            }

            _identitiesByActor[context.ActorId] = admission;
            _bridge.BindConnection(connectionId,
                new RbxNetworkPeer(context.ActorId, sessionId ?? context.SessionId,
                    connectionId.ToString(CultureInfo.InvariantCulture)),
                awaitClientReady: true);
            _bridge.RegisterActor(context.ActorId);

            bool connected;
            try
            {
                connected = _connectActor(context);
            }
            catch
            {
                // WHY released before the rethrow: the throw is the world's to report, but the binding
                // made above would otherwise resolve this connection — and, once kcp2k reuses its id,
                // the next one — as a player the world never created.
                Release(connectionId, context);
                throw;
            }

            if (!connected)
            {
                // The world refused the actor after admission said yes — the connection must not be
                // left holding a binding that resolves to a player who does not exist.
                Release(connectionId, context);
                return false;
            }

            _actorsByConnection[connectionId] = context;
            return true;
        }

        /// <summary>Tears one connection's actor down, whatever ended it.</summary>
        public void Release(int connectionId)
        {
            if (_actorsByConnection.TryGetValue(connectionId, out ActorContext context))
            {
                Release(connectionId, context);
            }
        }

        /// <inheritdoc />
        public bool TryGetIdentity(string actorId, out long userId, out string username,
            out string displayName)
        {
            if (!string.IsNullOrEmpty(actorId)
                && _identitiesByActor.TryGetValue(actorId, out ActorAdmissionResult admission))
            {
                userId = admission.UserId;
                username = admission.Name;
                displayName = admission.DisplayName;
                return true;
            }

            userId = 0L;
            username = null;
            displayName = null;
            return false;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _bridge.PeerDisconnected -= OnPeerDisconnected;
            _bridge.BindingReleased -= OnBindingReleased;
            _actorsByConnection.Clear();
            _identitiesByActor.Clear();
        }

        /// <summary>
        /// Releases the session the lost connection held, and only that one.
        /// </summary>
        /// <remarks>
        /// WHY by connection and not by actor: an actor that reconnected before its old link timed
        /// out holds its session on the NEW connection; found by actor, the old one's report would
        /// release the live session instead and leave the new socket authenticated to nobody.
        /// The connection is read back from the handle this host wrote when it admitted it, and the
        /// actor must match too, so a peer this host never admitted releases nothing.
        /// </remarks>
        private void OnPeerDisconnected(RbxNetworkPeerDisconnected disconnected)
        {
            if (!int.TryParse(disconnected.Peer.ConnectionHandle, NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out int connectionId)
                || !_actorsByConnection.TryGetValue(connectionId, out ActorContext context)
                || !string.Equals(context.ActorId, disconnected.Peer.ActorId, StringComparison.Ordinal))
            {
                return;
            }

            Release(connectionId, context);
        }

        /// <summary>
        /// Forgets the session a released connection held when nothing here released it: its world
        /// ended the actor itself, and the bridge is ending the connection.
        /// </summary>
        /// <remarks>
        /// WHY no world call: the world is the one that released it, mid-teardown; calling it again
        /// would tear the same actor down twice. WHY the identity goes only with the actor's last
        /// session: a newer session of the same actor still uses it.
        /// </remarks>
        private void OnBindingReleased(int connectionId)
        {
            if (!_actorsByConnection.TryGetValue(connectionId, out ActorContext context))
            {
                return;
            }

            _actorsByConnection.Remove(connectionId);
            if (!TryFindSession(context.ActorId, out _))
            {
                _identitiesByActor.Remove(context.ActorId);
            }
        }

        private static bool IsHostModeLocalConnection(int connectionId)
        {
            return NetworkServer.connections.TryGetValue(connectionId, out NetworkConnectionToClient conn)
                   && conn is LocalConnectionToClient;
        }

        private bool TryFindSession(string actorId, out int connectionId)
        {
            foreach (KeyValuePair<int, ActorContext> pair in _actorsByConnection)
            {
                if (string.Equals(pair.Value.ActorId, actorId, StringComparison.Ordinal))
                {
                    connectionId = pair.Key;
                    return true;
                }
            }

            connectionId = 0;
            return false;
        }

        private void Release(int connectionId, ActorContext context)
        {
            _actorsByConnection.Remove(connectionId);
            _identitiesByActor.Remove(context.ActorId);
            // WHY the world first: DisconnectActor is what fires PlayerRemoving, and that handler is
            // entitled to read the leaving player. Unbinding the connection before it would leave
            // the handler looking at an actor the bridge no longer knows. WHY finally: that handler
            // is mod code, and if it throws the connection id — which kcp2k reuses — must still stop
            // resolving to the actor that left. WHY by actor here: the host holds one session per
            // actor, so the actor's binding on the bridge is this connection's. WHY marked as a
            // teardown: whoever called this — a transport drop, a kick, a refused admission — ends
            // the connection its own way, and the world's unregister must not end it a second time.
            bool marked = _bridge.BeginTeardown(connectionId);
            try
            {
                _disconnectActor(context);
            }
            finally
            {
                try
                {
                    _bridge.UnregisterActor(context.ActorId);
                }
                finally
                {
                    if (marked)
                    {
                        _bridge.EndTeardown(connectionId);
                    }
                }
            }
        }
    }
}
