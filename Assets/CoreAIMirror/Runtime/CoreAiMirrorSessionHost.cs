using System;
using System.Collections.Generic;
using System.Globalization;
using CoreAI.Authority;
using CoreAI.Mods.Rbx.Instances.Networking;

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
    /// </remarks>
    public sealed class CoreAiMirrorSessionHost : IRbxActorIdentitySource, IDisposable
    {
        private readonly Dictionary<string, ActorAdmissionResult> _identitiesByActor =
            new(StringComparer.Ordinal);
        private readonly Dictionary<int, ActorContext> _actorsByConnection = new();
        private readonly MirrorNetworkBridge _bridge;
        private readonly Func<ActorContext, bool> _connectActor;
        private readonly Func<ActorContext, bool> _disconnectActor;
        private bool _disposed;

        /// <summary>
        /// Wires the session host to a bridge and the world's connect/disconnect entry points.
        /// </summary>
        public CoreAiMirrorSessionHost(MirrorNetworkBridge bridge,
            Func<ActorContext, bool> connectActor, Func<ActorContext, bool> disconnectActor)
        {
            _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
            _connectActor = connectActor ?? throw new ArgumentNullException(nameof(connectActor));
            _disconnectActor = disconnectActor
                               ?? throw new ArgumentNullException(nameof(disconnectActor));
            _bridge.PeerDisconnected += OnPeerDisconnected;
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
        /// Admits one connection into the world. Returns false when the decision was a refusal, in
        /// which case nothing at all was created for it.
        /// </summary>
        /// <remarks>
        /// WHY a connection that already holds a session is not admitted again: one connection is
        /// one session. The same actor asking twice gets the session it has, and nothing is created
        /// twice; another actor is refused and the session the connection holds is left as it was —
        /// a connection must never switch to a second identity, or mint a second Player, by asking
        /// again. WHY the older session of the same actor is dropped before the new binding: the
        /// bridge closes the older connection while binding, and on kcp2k that drop is reported
        /// before the call returns; by then this host must no longer hold the older session, or the
        /// report would tear down the Player the new session is carrying over.
        /// </remarks>
        public bool Admit(int connectionId, ActorAdmissionResult admission, string sessionId)
        {
            if (admission == null || !admission.Admitted)
            {
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
                    connectionId.ToString(CultureInfo.InvariantCulture)));
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
            // actor, so the actor's binding on the bridge is this connection's.
            try
            {
                _disconnectActor(context);
            }
            finally
            {
                _bridge.UnregisterActor(context.ActorId);
            }
        }
    }
}
