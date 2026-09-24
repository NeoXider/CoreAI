using System;
using System.Collections.Generic;
using CoreAI.Ai.LuaCs;
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
    /// that turns admitted connections into players; every frame it pumps request timeouts, drops
    /// the connections admission could not turn into players, keeps its disconnect hook in Mirror's
    /// disconnect event, and keeps the attached world's identity source wired.
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

        private readonly Queue<NetworkConnectionToClient> _pendingDrops = new();
        private MirrorNetworkBridge _bridge;
        private CoreAiMirrorSessionHost _sessionHost;
        private Func<ActorContext, bool> _connectActor;
        private Func<ActorContext, bool> _disconnectActor;
        private Func<LuaCsRbxApiBindings> _world;
        private Action<NetworkConnectionToClient> _serverDisconnectHook;
        private Action<NetworkConnectionToClient> _checkedDisconnectEvent;
        private bool _foreignIdentityWarned;
        private bool _worldResolveWarned;
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
        /// The server's session host, built together with the bridge; null on a client. It is the
        /// world's <c>Players.IdentitySource</c>, so a Player's UserId is the admitted one:
        /// <see cref="AttachWorld(Func{LuaCsRbxApiBindings})"/> wires that itself, a composition
        /// over the delegate overload assigns it by hand.
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
        /// Attaches the world this provider serves: every admitted connection becomes a player in
        /// the world <paramref name="world"/> returns at that moment and every lost one leaves it,
        /// and that world's <c>Players.IdentitySource</c> is the session host, so a Player's UserId
        /// is the one admission decided — no manual wiring. Once per provider, like the delegate
        /// overload; unused on a client.
        /// </summary>
        /// <remarks>
        /// WHY a function and not the world itself: a world loaded at runtime replaces the bindings
        /// behind the composition's facade, so <c>() =&gt; stack.GameplayBindings.RbxApi</c> keeps
        /// following the live one. WHY the identity source is checked every frame and before every
        /// admission: a newly published world starts with none, and without one its Players service
        /// hands admitted players counter UserIds. An identity source the host already set on the
        /// world is left as it is, and said once. A world published between two frames is wired on
        /// the next one; a player first seen in that window is not.
        /// </remarks>
        public void AttachWorld(Func<LuaCsRbxApiBindings> world)
        {
            if (world == null)
            {
                throw new ArgumentNullException(nameof(world));
            }

            RequireNoWorldAttached();
            _world = world;
            AttachWorld(
                context =>
                {
                    LuaCsRbxApiBindings live = world();
                    return live != null && live.ConnectActor(context) != null;
                },
                context =>
                {
                    LuaCsRbxApiBindings live = world();
                    return live != null && live.DisconnectActor(context);
                });
        }

        /// <summary>
        /// Wires the world's connect/disconnect entry points, which the session host calls for every
        /// admitted and every lost connection. A connection admitted before a world is attached
        /// gets its player when one is. Once per provider: a second call throws and changes
        /// nothing. Unused on a client.
        /// </summary>
        /// <remarks>
        /// WHY a world cannot be swapped here: the sessions already admitted belong to the world
        /// that created their players. Handing them to another would either admit them again —
        /// PlayerAdded for players who never left — or leave the first world holding players whose
        /// disconnects now reach a world that never created them, and never it. A world committed
        /// at runtime therefore needs a new composition, the rule <see cref="Role"/> already
        /// follows.
        /// </remarks>
        public void AttachWorld(Func<ActorContext, bool> connectActor,
            Func<ActorContext, bool> disconnectActor)
        {
            if (connectActor == null)
            {
                throw new ArgumentNullException(nameof(connectActor));
            }

            if (disconnectActor == null)
            {
                throw new ArgumentNullException(nameof(disconnectActor));
            }

            RequireNoWorldAttached();
            _connectActor = connectActor;
            _disconnectActor = disconnectActor;
            AdmitRecordedConnections();
        }

        /// <summary>
        /// Drops what admission still owes, then disposes the session host and the bridge; the
        /// component is spent afterwards.
        /// </summary>
        internal void ReleaseTransport()
        {
            _released = true;
            FlushPendingDrops();
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

            if (_bridgeIsServer && _serverDisconnectHook != null)
            {
                NetworkServer.OnDisconnectedEvent -= _serverDisconnectHook;
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
            FlushPendingDrops();
            if (_bridgeIsServer)
            {
                WireIdentitySource();
            }

            _bridge.PumpTimeouts();
        }

        /// <summary>
        /// Drops what admission still owes before the component stops updating.
        /// </summary>
        /// <remarks>
        /// WHY here as well as in Update: a disabled component runs no Update, and a connection
        /// owed a drop must not stay authenticated to Mirror as nobody for as long as the provider
        /// is off — however long that is.
        /// </remarks>
        private void OnDisable()
        {
            FlushPendingDrops();
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
                if (_bridgeIsServer && !ServerDisconnectHookInPlace())
                {
                    // WHY the handlers too: a stop and start within one frame reassigns the event
                    // the same way and clears the handler table with it; replacing them is idempotent.
                    Debug.LogWarning("[CoreAI.Mirror] NetworkServer.OnDisconnectedEvent was reassigned "
                                     + "while the server ran, which removed the provider's disconnect "
                                     + "hook; it is put back so a lost connection still leaves the world");
                    _bridge.AttachHandlers();
                    HookServerDisconnects();
                }

                return;
            }

            _bridge.AttachHandlers();
            if (_bridgeIsServer)
            {
                HookServerDisconnects();
            }

            _transportHooked = true;
        }

        /// <summary>
        /// Puts the provider's disconnect hook FIRST in Mirror's disconnect event, once.
        /// </summary>
        /// <remarks>
        /// WHY first: Mirror invokes that multicast unguarded, from inside the transport's receive
        /// tick, so a listener ahead of this one that throws — a NetworkManager.OnServerDisconnect
        /// override, which the manager assigns when the server starts — would skip CoreAI's
        /// teardown and leave a Player behind for a connection that is gone.
        /// </remarks>
        private void HookServerDisconnects()
        {
            if (_serverDisconnectHook == null)
            {
                _serverDisconnectHook = OnServerDisconnected;
            }

            Action<NetworkConnectionToClient> others = NetworkServer.OnDisconnectedEvent;
            if (others != null)
            {
                others -= _serverDisconnectHook;
            }

            NetworkServer.OnDisconnectedEvent = _serverDisconnectHook + others;
            _checkedDisconnectEvent = NetworkServer.OnDisconnectedEvent;
        }

        /// <summary>
        /// Whether the disconnect hook is still in Mirror's event; the invocation list is only read
        /// when the event is no longer the delegate this provider last checked, so a frame where
        /// nothing changed allocates nothing.
        /// </summary>
        private bool ServerDisconnectHookInPlace()
        {
            Action<NetworkConnectionToClient> current = NetworkServer.OnDisconnectedEvent;
            if (current == null || _serverDisconnectHook == null)
            {
                return false;
            }

            if (ReferenceEquals(current, _checkedDisconnectEvent))
            {
                return true;
            }

            Delegate[] listeners = current.GetInvocationList();
            for (int index = 0; index < listeners.Length; index++)
            {
                if (listeners[index].Equals(_serverDisconnectHook))
                {
                    _checkedDisconnectEvent = current;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Admits an accepted connection into the attached world; with no world attached yet the
        /// connection stays recorded by the authenticator for the world's attach to admit.
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

            AdmitOrDrop(conn);
        }

        /// <summary>
        /// Admits one authenticated connection into the world, or marks it for the drop the next
        /// frame performs when the world threw or refused: the session host has already released
        /// the binding and the admission record by then, so nothing could ever admit that
        /// connection again.
        /// </summary>
        /// <remarks>
        /// WHY the throw is contained here and not in the session host: this listener was added in
        /// the mods scope's Awake and the NetworkManager's at StartServer, and a UnityEvent runs its
        /// listeners in that order — so a throw out of here skips NetworkManager.OnServerAuthenticated,
        /// leaving the connection unauthenticated to Mirror while the client already holds
        /// Admitted = true with its actor id: a peer no dispatch can admit and no catch-up will,
        /// its record gone. The session host's rethrow is right for a caller that can answer it; a
        /// Mirror listener cannot, so the honest outcome — no player — is made true on the wire.
        /// WHY a refusal drops too: after the release the connection is authenticated nobody, and
        /// every packet from it would be dropped as unadmitted until the client gave up.
        /// WHY the drop is deferred rather than made here: the same listener order puts this
        /// before Mirror's own listener, and on kcp2k ServerDisconnect reports the drop before it
        /// returns — so a drop made here runs NetworkManager.OnServerDisconnect first, and Mirror
        /// then marks the connection authenticated and calls OnServerConnect on a connection it
        /// has already removed. A NetworkManager that tracks per-connection state sees a
        /// disconnect before its connect and leaks the entry. Recorded here and performed from
        /// the next Update, the drop follows the whole accept chain, and the manager sees connect
        /// then disconnect, in that order. The frame between is a connection that is
        /// authenticated to Mirror and bound to nobody on the bridge: every packet from it is
        /// dropped as unadmitted, which is what the same connection got before the fix, for
        /// longer. The catch-up path takes the same route so there is one.
        /// </remarks>
        private void AdmitOrDrop(NetworkConnectionToClient conn)
        {
            WireIdentitySource();
            bool admitted;
            try
            {
                admitted = _sessionHost.Admit(conn.connectionId,
                    authenticator.ResultFor(conn.connectionId), sessionId: null);
            }
            catch (Exception exception)
            {
                Debug.LogError("[CoreAI.Mirror] the world threw while admitting connection "
                               + conn.connectionId + "; the connection is dropped: " + exception);
                admitted = false;
            }

            if (!admitted && !_pendingDrops.Contains(conn))
            {
                _pendingDrops.Enqueue(conn);
            }
        }

        /// <summary>
        /// Performs the drops <see cref="AdmitOrDrop"/> recorded: each connection still live under
        /// its id, and only that same connection.
        /// </summary>
        /// <remarks>
        /// WHY the reference is compared and not only the id: kcp2k reuses connection ids, so a
        /// connection that left on its own between the record and this frame may have a stranger
        /// on its id by now, whom this provider owes nothing. WHY a queue drained to empty: the
        /// transport's report of a drop reaches this provider's own listeners inside the call,
        /// and anything they record is served in the same pass rather than a frame late.
        /// </remarks>
        private void FlushPendingDrops()
        {
            while (_pendingDrops.Count > 0)
            {
                NetworkConnectionToClient dropped = _pendingDrops.Dequeue();
                if (NetworkServer.connections.TryGetValue(dropped.connectionId,
                        out NetworkConnectionToClient live)
                    && ReferenceEquals(live, dropped))
                {
                    dropped.Disconnect();
                }
            }
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
            if (_sessionHost == null || _connectActor == null)
            {
                return;
            }

            WireIdentitySource();
            if (authenticator == null)
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
                if (NetworkServer.connections.TryGetValue(recorded[index],
                        out NetworkConnectionToClient conn))
                {
                    AdmitOrDrop(conn);
                }
            }
        }

        /// <summary>
        /// Reports every lost connection as <see cref="RbxNetworkDisconnectReason.TransportLost"/>
        /// — Mirror raises one event whether the peer left or the link dropped, and the world's
        /// teardown is the same either way — and forgets its admission, which a connection admitted
        /// before a world attached has no binding to release it through.
        /// </summary>
        /// <remarks>
        /// WHY nothing escapes: Mirror raises this from inside the transport's receive tick, and a
        /// throw out of the world's teardown — mod code runs in PlayerRemoving — would abort that
        /// tick for every other connection this frame. The failure is logged; the bridge has
        /// released the connection's binding in its own finally by then.
        /// </remarks>
        private void OnServerDisconnected(NetworkConnectionToClient conn)
        {
            if (conn == null)
            {
                return;
            }

            try
            {
                if (authenticator != null)
                {
                    authenticator.Forget(conn.connectionId);
                }

                _bridge?.NotifyDisconnected(conn.connectionId,
                    RbxNetworkDisconnectReason.TransportLost);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
        }

        /// <summary>
        /// Makes the session host the attached world's identity source when the world has none.
        /// </summary>
        private void WireIdentitySource()
        {
            if (_world == null || _sessionHost == null)
            {
                return;
            }

            LuaCsRbxApiBindings live;
            try
            {
                live = _world();
            }
            catch (Exception exception)
            {
                if (!_worldResolveWarned)
                {
                    _worldResolveWarned = true;
                    Debug.LogWarning("[CoreAI.Mirror] the attached world could not be resolved to wire "
                                     + "its Players.IdentitySource: " + exception.Message);
                }

                return;
            }

            RbxPlayers players = live?.Players;
            if (players == null)
            {
                return;
            }

            IRbxActorIdentitySource current = players.IdentitySource;
            if (current == null)
            {
                players.IdentitySource = _sessionHost;
                return;
            }

            if (ReferenceEquals(current, _sessionHost) || _foreignIdentityWarned)
            {
                return;
            }

            _foreignIdentityWarned = true;
            Debug.LogWarning("[CoreAI.Mirror] the attached world's Players.IdentitySource is a "
                             + current.GetType().Name + ", not this provider's session host; it is "
                             + "left as the host set it, so admitted players get their UserIds from it");
        }

        private void RequireNoWorldAttached()
        {
            if (_connectActor != null)
            {
                throw new InvalidOperationException(
                    "a world is already attached to this Mirror network bridge provider and cannot "
                    + "be swapped: the sessions it admitted are its own, so a world committed at "
                    + "runtime needs a new composition with a new provider");
            }
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
