using System;
using System.Collections.Generic;
using CoreAI.Authority;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Instances.Networking;
using Mirror;
using UnityEngine;

namespace CoreAI.Net.Mirror
{
    /// <summary>Which side of the Mirror transport this process's Rbx world is.</summary>
    public enum CoreAiMirrorRole
    {
        /// <summary>The authority: a dedicated server, or the server half of a host.</summary>
        Server,

        /// <summary>A remote player that joined someone else's server.</summary>
        Client
    }

    /// <summary>
    /// Puts CoreAI's Rbx world on the Mirror transport: drop it next to the <c>NetworkManager</c>,
    /// hand it to <c>CoreAiModsLifetimeScope</c>'s network bridge provider field, and the world's
    /// remotes travel over the wire instead of the in-process loopback. Builds the
    /// <see cref="MirrorNetworkBridge"/> and, on a server, the <see cref="CoreAiMirrorSessionHost"/>
    /// that turns admitted connections into players; pumps request timeouts every frame.
    /// </summary>
    /// <remarks>
    /// WHY the side is declared in the scene rather than read from Mirror when the bridge is built:
    /// <see cref="MirrorNetworkBridge"/> fixes its side in the constructor, and the bridge is built
    /// when the container first resolves <see cref="INetworkBridge"/>. With a default (auto-run)
    /// scope in Play Mode that is the mods installer's build callback — still inside the scope's
    /// Awake, before any NetworkManager has called StartServer or StartClient — so
    /// <c>NetworkServer.active</c> and <c>NetworkClient.active</c> are both false and there is
    /// nothing to read. The cost is that a scene is built for one side, the same discipline the scope
    /// already demands of <c>enableFullLuaAccess</c>; a build that decides at runtime sets
    /// <see cref="Role"/> before the mods scope builds. A contradiction is refused, never absorbed:
    /// the bridge is not built while Mirror already runs as the other side, the role cannot change
    /// once the bridge exists, and if Mirror starts as the other side afterwards every frame that
    /// sees it keeps the bridge off the transport and the first of them says so as an error — a
    /// bridge silently carrying the wrong side's handlers is the one outcome this seam must not
    /// have. A stop and start of the bridge's own side keeps the same bridge: Mirror's Shutdown
    /// clears its handlers, and the first frame that sees the side active again puts them back
    /// through <see cref="MirrorNetworkBridge.AttachHandlers"/>. On a client the same authenticator
    /// that admits connections on a server is where this process learns the actor the server
    /// admitted it as: its accept event binds that id on the bridge, and the first frame that sees
    /// the client side inactive forgets it, so a reconnect starts unadmitted.
    /// WHY admission is also caught up from the authenticator's record: the NetworkManager admits
    /// on its own schedule, the bridge waits for the container's first resolve and a server's world
    /// attaches after that, so an accept event may have fired before anything listened. A client
    /// binds the recorded actor when the bridge is built; a server admits the recorded connections
    /// once the bridge and the world both exist, from whichever of the two comes last.
    /// </remarks>
    [AddComponentMenu("CoreAI/CoreAI Mirror Network Bridge Provider")]
    [DisallowMultipleComponent]
    public sealed class CoreAiMirrorNetworkBridgeProvider : RbxNetworkBridgeProviderBehaviour
    {
        [Tooltip("Which side of the transport this scene is: Server for a dedicated server or a host, "
            + "Client for a joining player. Fixed once the bridge exists; a build that decides at "
            + "runtime sets Role from code before the mods scope builds.")]
        [SerializeField]
        private CoreAiMirrorRole role = CoreAiMirrorRole.Server;

        [Tooltip("The admission authenticator the NetworkManager uses. On a server it is what turns "
            + "an authenticated connection into a world player; without it no connection is ever "
            + "admitted and every client packet is dropped as unadmitted. On a client it is where "
            + "the bridge learns the actor the server admitted this process as; without it every "
            + "server-to-client remote is dropped as unadmitted.")]
        [SerializeField]
        private CoreAiMirrorAuthenticator authenticator;

        private MirrorNetworkBridge _bridge;
        private CoreAiMirrorSessionHost _sessionHost;
        private Func<ActorContext, bool> _connectActor;
        private Func<ActorContext, bool> _disconnectActor;
        private bool _bridgeIsServer;
        private bool _admissionHooked;
        private bool _transportHooked;
        private bool _roleConflict;
        private bool _released;

        /// <summary>Test seam: the clock the bridge times requests by; null means Mirror's own.</summary>
        internal Func<double> ClockSeconds { get; set; }

        /// <summary>Which side this process is. Settable only until the bridge exists.</summary>
        public CoreAiMirrorRole Role
        {
            get => role;
            set
            {
                if (_bridge != null && value != role)
                {
                    throw new InvalidOperationException(
                        "the Mirror network bridge was already built as " + role
                        + " and cannot become " + value + ": the side is fixed at construction, so "
                        + "set Role before the mods scope builds or rebuild the composition");
                }

                role = value;
            }
        }

        /// <summary>Whether the bridge has been built yet.</summary>
        public bool HasBridge => _bridge != null;

        /// <inheritdoc />
        public override INetworkBridge Bridge => EnsureBridge();

        /// <summary>
        /// The server's session host, built together with the bridge; null on a client. Assign it
        /// to the world's <c>Players.IdentitySource</c> so a Player's UserId is the admitted one.
        /// </summary>
        public CoreAiMirrorSessionHost SessionHost
        {
            get
            {
                EnsureBridge();
                return _sessionHost;
            }
        }

        /// <summary>
        /// Wires the world's connect/disconnect entry points, which the session host calls for every
        /// admitted and every lost connection. A connection admitted before a world is attached
        /// gets its player when one is; calling it again swaps the world (a staged world committed
        /// at runtime) without losing or repeating the sessions already admitted. Unused on a client.
        /// </summary>
        public void AttachWorld(Func<ActorContext, bool> connectActor,
            Func<ActorContext, bool> disconnectActor)
        {
            _connectActor = connectActor ?? throw new ArgumentNullException(nameof(connectActor));
            _disconnectActor = disconnectActor
                               ?? throw new ArgumentNullException(nameof(disconnectActor));
            AdmitRecordedConnections();
        }

        /// <summary>Disposes the session host and the bridge; the component is spent afterwards.</summary>
        internal void ReleaseTransport()
        {
            _released = true;
            if (_admissionHooked && authenticator != null)
            {
                if (_bridgeIsServer)
                {
                    authenticator.OnServerAuthenticated.RemoveListener(OnServerAuthenticated);
                }
                else
                {
                    authenticator.OnClientAuthenticated.RemoveListener(OnClientAuthenticated);
                }
            }

            if (_transportHooked && _bridgeIsServer)
            {
                NetworkServer.OnDisconnectedEvent -= OnServerDisconnected;
            }

            _admissionHooked = false;
            _transportHooked = false;
            _sessionHost?.Dispose();
            _sessionHost = null;
            _bridge?.Dispose();
            _bridge = null;
        }

        private MirrorNetworkBridge EnsureBridge()
        {
            if (_bridge != null)
            {
                return _bridge;
            }

            if (_released)
            {
                throw new ObjectDisposedException(nameof(CoreAiMirrorNetworkBridgeProvider),
                    "the transport was released with the component; a new composition needs a new provider");
            }

            bool isServer = role == CoreAiMirrorRole.Server;
            if (MirrorRunsAsOtherSide(isServer))
            {
                throw RoleConflict(isServer);
            }

            _bridgeIsServer = isServer;
            _bridge = new MirrorNetworkBridge(isServer, authenticator, clockSeconds: ClockSeconds);
            if (!isServer)
            {
                if (authenticator != null)
                {
                    authenticator.OnClientAuthenticated.AddListener(OnClientAuthenticated);
                    _admissionHooked = true;
                    BindRecordedAdmission();
                }

                return _bridge;
            }

            // WHY forwarding lambdas rather than the world's own delegates: the world does not exist
            // yet — this bridge is a constructor argument of the world that will call AttachWorld —
            // so the session host takes indirections that read whatever is attached at call time.
            _sessionHost = new CoreAiMirrorSessionHost(
                _bridge,
                context => _connectActor != null && _connectActor(context),
                context => _disconnectActor != null && _disconnectActor(context));
            if (authenticator != null)
            {
                authenticator.OnServerAuthenticated.AddListener(OnServerAuthenticated);
                _admissionHooked = true;
            }

            AdmitRecordedConnections();
            return _bridge;
        }

        private void Update()
        {
            if (_bridge == null)
            {
                return;
            }

            if (_bridge.IsDisposed)
            {
                // WHY released rather than rebuilt: the container owns the singleton bridge and
                // disposes it with the scope; the world that held it went with it, and a second
                // bridge would carry traffic no world sees.
                Debug.LogWarning("[CoreAI.Mirror] the network bridge was disposed by its owner before "
                                 + "this provider was destroyed; the transport is released and the "
                                 + "provider is spent, so a new composition needs a new provider");
                ReleaseTransport();
                return;
            }

            // WHY an error rather than a throw: a throw out of Update skips the pump for that frame
            // and, once latched, is never re-checked; this says it once per conflict, keeps the
            // bridge off the transport while the conflict lasts, and hooks it back when the side is
            // corrected.
            bool conflict = MirrorRunsAsOtherSide(_bridgeIsServer);
            if (conflict && !_roleConflict)
            {
                Debug.LogError("[CoreAI.Mirror] " + RoleConflict(_bridgeIsServer).Message);
            }

            _roleConflict = conflict;
            HookTransport();
            _bridge.PumpTimeouts();
        }

        private void OnDestroy()
        {
            ReleaseTransport();
        }

        // HACK: Mirror's Shutdown clears the message handler table and nulls
        // NetworkServer.OnDisconnectedEvent (which NetworkManager ASSIGNS when the server starts), so
        // a bridge built before StartServer or StartClient — the normal order — would be silently
        // deaf after a stop and start. Both are attached on the first frame the bridge's side is seen
        // active with Mirror not running as the other side, and again after every restart; a stop
        // and start within one frame would be missed.
        private void HookTransport()
        {
            bool sideActive = !_roleConflict
                              && (_bridgeIsServer ? NetworkServer.active : NetworkClient.active);
            if (!sideActive)
            {
                if (_transportHooked && !_bridgeIsServer)
                {
                    _bridge.ForgetAdmittedActor();
                }

                _transportHooked = false;
                return;
            }

            if (_transportHooked)
            {
                return;
            }

            _bridge.AttachHandlers();
            if (_bridgeIsServer)
            {
                NetworkServer.OnDisconnectedEvent += OnServerDisconnected;
            }

            _transportHooked = true;
        }

        /// <summary>
        /// Admits an accepted connection into the attached world; with no world attached yet the
        /// connection stays recorded by the authenticator for <see cref="AttachWorld"/> to admit.
        /// </summary>
        /// <remarks>
        /// WHY not admitted anyway: the session host binds first and asks the world second, and a
        /// world that is not there reads as a refusal — which releases the binding and, with it, the
        /// very record the attach would replay.
        /// </remarks>
        private void OnServerAuthenticated(NetworkConnectionToClient conn)
        {
            if (conn == null || _sessionHost == null || _connectActor == null)
            {
                return;
            }

            _sessionHost.Admit(conn.connectionId, authenticator.ResultFor(conn.connectionId),
                sessionId: null);
        }

        /// <summary>
        /// Binds the actor the server admitted this client as; read from the authenticator, which
        /// stored it from the admission response before raising this event, or before the bridge
        /// existed.
        /// </summary>
        /// <remarks>
        /// WHY an error rather than a throw when the server named nobody: this runs inside Mirror's
        /// message handler, where a throw disconnects the client. The server did admit us, so the
        /// honest state is connected with server-to-client remotes off — and said so.
        /// </remarks>
        private void OnClientAuthenticated()
        {
            string actorId = authenticator.ClientActorId;
            if (string.IsNullOrWhiteSpace(actorId))
            {
                Debug.LogError("[CoreAI.Mirror] the server admitted this client without naming its "
                               + "actor; server-to-client remotes stay off until a server that "
                               + "fills CoreAiAdmissionResponseMessage.ActorId");
                return;
            }

            _bridge?.BindAdmittedActor(actorId);
        }

        /// <summary>
        /// Binds the actor a server admitted this client as before the bridge existed, when that
        /// admission is the live connection's.
        /// </summary>
        /// <remarks>
        /// WHY the connection must be live and authenticated: the authenticator forgets its actor
        /// only through OnStopClient, and an id left behind by a composition that never called it
        /// must not become an admission on the next connection.
        /// </remarks>
        private void BindRecordedAdmission()
        {
            if (NetworkClient.active && NetworkClient.connection is { isAuthenticated: true }
                && authenticator.ClientActorId != null)
            {
                OnClientAuthenticated();
            }
        }

        /// <summary>
        /// Admits every live, authenticated connection the authenticator recorded before the session
        /// host and the world both existed, from whichever of the two arrived last.
        /// </summary>
        /// <remarks>
        /// WHY authenticated as well as recorded: kcp2k reuses connection ids, so a record left by a
        /// connection that left before anything could forget it must not admit the stranger now on
        /// that id — who is unauthenticated until the authenticator decides on it, at which point
        /// the record is that stranger's own. WHY a snapshot: the world's connect entry point may
        /// kick a connection, which removes it from Mirror's table mid-iteration.
        /// </remarks>
        private void AdmitRecordedConnections()
        {
            if (_sessionHost == null || _connectActor == null || authenticator == null)
            {
                return;
            }

            List<int> recorded = null;
            foreach (NetworkConnectionToClient conn in NetworkServer.connections.Values)
            {
                if (conn.isAuthenticated && !_sessionHost.HasLiveSession(conn.connectionId)
                    && authenticator.ResultFor(conn.connectionId) != null)
                {
                    recorded ??= new List<int>();
                    recorded.Add(conn.connectionId);
                }
            }

            for (int index = 0; recorded != null && index < recorded.Count; index++)
            {
                _sessionHost.Admit(recorded[index], authenticator.ResultFor(recorded[index]),
                    sessionId: null);
            }
        }

        /// <summary>
        /// Reports every lost connection as <see cref="RbxNetworkDisconnectReason.TransportLost"/>
        /// — Mirror raises one event whether the peer left or the link dropped, and the world's
        /// teardown is the same either way — and forgets its admission, which a connection admitted
        /// before a world attached has no binding to release it through.
        /// </summary>
        private void OnServerDisconnected(NetworkConnectionToClient conn)
        {
            if (conn == null)
            {
                return;
            }

            if (authenticator != null)
            {
                authenticator.Forget(conn.connectionId);
            }

            _bridge?.NotifyDisconnected(conn.connectionId, RbxNetworkDisconnectReason.TransportLost);
        }

        private static bool MirrorRunsAsOtherSide(bool bridgeIsServer)
        {
            return bridgeIsServer
                ? NetworkClient.active && !NetworkServer.active
                : NetworkServer.active;
        }

        private static InvalidOperationException RoleConflict(bool bridgeIsServer)
        {
            string configured = bridgeIsServer ? "Server" : "Client";
            string running = NetworkServer.active ? "a server" : "a client";
            return new InvalidOperationException(
                "the Mirror network bridge is configured as " + configured
                + " but Mirror is running as " + running + ": the bridge fixes its side at "
                + "construction, so set the provider's Role to match how the NetworkManager is started");
        }
    }
}
