using System;
using System.Collections.Generic;
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
        /// Admits one connection into the world. Returns false when the decision was a refusal, in
        /// which case nothing at all was created for it.
        /// </summary>
        public bool Admit(int connectionId, ActorAdmissionResult admission, string sessionId)
        {
            if (admission == null || !admission.Admitted)
            {
                return false;
            }

            ActorContext context = admission.Context;
            _identitiesByActor[context.ActorId] = admission;
            _bridge.BindConnection(connectionId,
                new RbxNetworkPeer(context.ActorId, sessionId ?? context.SessionId,
                    connectionId.ToString()));
            _bridge.RegisterActor(context.ActorId);

            if (!_connectActor(context))
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

        private void OnPeerDisconnected(RbxNetworkPeerDisconnected disconnected)
        {
            foreach (KeyValuePair<int, ActorContext> pair in _actorsByConnection)
            {
                if (string.Equals(pair.Value.ActorId, disconnected.Peer.ActorId,
                        StringComparison.Ordinal))
                {
                    Release(pair.Key, pair.Value);
                    return;
                }
            }
        }

        private void Release(int connectionId, ActorContext context)
        {
            _actorsByConnection.Remove(connectionId);
            _identitiesByActor.Remove(context.ActorId);
            // WHY the world first: DisconnectActor is what fires PlayerRemoving, and that handler is
            // entitled to read the leaving player. Unbinding the connection before it would leave
            // the handler looking at an actor the bridge no longer knows.
            _disconnectActor(context);
            _bridge.UnregisterActor(context.ActorId);
        }
    }
}
