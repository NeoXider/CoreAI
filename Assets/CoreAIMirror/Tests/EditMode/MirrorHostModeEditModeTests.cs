using System;
using System.Collections.Generic;
using System.Text;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using Mirror;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Net.Mirror.Tests
{
    /// <summary>
    /// Mirror host mode, end to end through a real world: the host's own local client is admitted
    /// through the real authenticator over Mirror's real local connection pair, becomes the host's
    /// Player, and its remotes run in process both ways — beside a remote client that still joins
    /// over the wire and waits for its readiness.
    /// </summary>
    /// <remarks>
    /// WHY the remote client is a server-side connection and not a second Mirror client: Mirror's
    /// client is one static per process, and in host mode it is the host's own. A remote peer is
    /// therefore driven the way the bridge sees one — packets pushed into the server's transport,
    /// sends read back at the transport's send boundary.
    /// </remarks>
    [TestFixture]
    public sealed class MirrorHostModeEditModeTests
    {
        private const string WorldId = "world-a";
        private const int Remote = 7;

        private OfflineMirror _mirror;
        private GameObject _go;
        private CoreAiMirrorAuthenticator _authenticator;
        private MirrorNetworkBridge _server;
        private CoreAiMirrorSessionHost _sessionHost;
        private InstanceRegistry _registry;
        private LuaCsRbxApiBindings _world;
        private List<string> _bridgeLog;
        private List<string> _sessionLog;
        private double _now;

        [SetUp]
        public void CreateHostWorld()
        {
            _mirror = new OfflineMirror();
            _now = 0d;
            _go = new GameObject("CoreAI_MirrorHostMode");
            _authenticator = _go.AddComponent<CoreAiMirrorAuthenticator>();
            _authenticator.Configure(new NamedProvider(), WorldId);
            _authenticator.ConfigureClientCredential(() => Encoding.UTF8.GetBytes("host"));
            _bridgeLog = new List<string>();
            _sessionLog = new List<string>();
            _server = new MirrorNetworkBridge(isServer: true, _authenticator, clockSeconds: () => _now,
                log: _bridgeLog.Add);
            _registry = new InstanceRegistry(
                worldAclVersion: InstanceRegistry.CurrentWorldAclVersion, worldId: WorldId);
            _world = new LuaCsRbxApiBindings(_registry, DataModelBootstrap.CreateGame(_registry),
                networkBridge: _server, log: _ => { });
            _sessionHost = new CoreAiMirrorSessionHost(
                _server,
                context => _world.ConnectActor(context) != null,
                context => _world.DisconnectActor(context),
                _sessionLog.Add);
            _world.Players.IdentitySource = _sessionHost;
            // WHY these two listeners: there is no NetworkManager, and these are what it and the
            // scene provider do once the authenticator accepts.
            _authenticator.OnServerAuthenticated.AddListener(conn =>
            {
                _sessionHost.Admit(conn.connectionId, _authenticator.ResultFor(conn.connectionId), sessionId: null);
                conn.isAuthenticated = true;
            });
            _authenticator.OnClientAuthenticated.AddListener(() => NetworkClient.connection.isAuthenticated = true);
        }

        [TearDown]
        public void RestoreEverything()
        {
            OfflineMirror.RunAll(
                () => _world.Dispose(),
                () => _sessionHost.Dispose(),
                () => _server.Dispose(),
                () => _mirror.Dispose(),
                () => UnityEngine.Object.DestroyImmediate(_go));
        }

        [Test]
        public void TheHostsLocalClient_BecomesTheHostsPlayer_WithTheIdentityItsAdmissionGave()
        {
            List<object[]> added = new();
            _world.Players.PlayerAdded.Connect((Action<object[]>)added.Add);

            LocalConnectionToClient local = StartHostAndAdmitTheHost();
            _world.Scheduler.Advance(0d);

            Assert.IsTrue(_sessionHost.IsHostLocalSession(local.connectionId));
            Assert.AreEqual(1, _sessionHost.HostPlayerSessions);
            Assert.AreEqual(0, _sessionHost.HostModeConnectionsRefused);
            Assert.IsTrue(_server.IsHostLocalConnection(local.connectionId));
            Assert.IsTrue(_world.Players.TryGetByActorId("actor-host", out RbxPlayer host));
            Assert.AreEqual(1001L, host.UserId, "the host's Player carries the UserId its admission decided");
            Assert.AreEqual(1, added.Count, "PlayerAdded fires once for the host's own player");
            Assert.AreEqual("actor-host", _authenticator.ClientActorId,
                "the host's client half heard its admission over Mirror's local connection");
            Assert.IsTrue(_sessionHost.Roster.TryGetMember("actor-host", out CoreAiRosterMember member));
            Assert.IsTrue(member.IsHost, "the roster says who the host is");
            CollectionAssert.IsEmpty(_sessionLog, "nothing about the host's own client is refused or said");
        }

        [Test]
        public void FireClientToTheHostsPlayer_FiresItsOnClientEvent_InProcess_AndNothingTouchesMirror()
        {
            StartHostAndAdmitTheHost();
            Assert.IsTrue(_world.Players.TryGetByActorId("actor-host", out RbxPlayer host));
            RbxRemoteEvent remote = (RbxRemoteEvent)_registry.Create("RemoteEvent");
            remote.AttachScheduler(_world.Scheduler);
            List<object[]> heard = new();
            remote.GetOnClientEvent("actor-host").Connect((Action<object[]>)heard.Add);
            using SentMessages<CoreAiRemoteEventMessage> onMirror = new();

            remote.FireClient(_server, host, Encoding.UTF8.GetBytes("[\"hello\",7]"));
            _world.Scheduler.Advance(0d);

            Assert.AreEqual(1, heard.Count, "the host's own LocalScripts hear FireClient to the host's player");
            CollectionAssert.AreEqual(new object[] { "hello", 7d }, heard[0]);
            CollectionAssert.IsEmpty(onMirror.Messages, "a remote to the host's player never touches the wire");
            Assert.AreEqual(1, _server.LocalDeliveries);
        }

        [Test]
        public void FireServerFromTheHostsPlayer_ReachesOnServerEvent_WithTheHostsPlayerFirst()
        {
            StartHostAndAdmitTheHost();
            Assert.IsTrue(_world.Players.TryGetByActorId("actor-host", out RbxPlayer host));
            RbxRemoteEvent remote = (RbxRemoteEvent)_registry.Create("RemoteEvent");
            remote.AttachScheduler(_world.Scheduler);
            List<object[]> heard = new();
            remote.OnServerEvent.Connect((Action<object[]>)heard.Add);

            remote.FireServer(_server, "actor-host", Encoding.UTF8.GetBytes("[\"hi\"]"));
            _world.Scheduler.Advance(0d);

            Assert.AreEqual(1, heard.Count, "the server's scripts hear the host's own FireServer");
            Assert.AreSame(host, heard[0][0], "OnServerEvent gets the host's Player first, as Roblox prepends the sender");
            Assert.AreEqual("hi", heard[0][1]);
        }

        [Test]
        public void FireAllClients_ReachesTheHostsPlayerInProcess_AndTheRemoteClientOnTheWire()
        {
            StartHostAndAdmitTheHost();
            AdmitRemote("guest");
            OfflineMirror.DeliverToServer(Remote, new CoreAiClientReadyMessage());
            RbxRemoteEvent remote = (RbxRemoteEvent)_registry.Create("RemoteEvent");
            remote.AttachScheduler(_world.Scheduler);
            List<object[]> hostHeard = new();
            remote.GetOnClientEvent("actor-host").Connect((Action<object[]>)hostHeard.Add);

            remote.FireAllClients(_server, Encoding.UTF8.GetBytes("[\"all\"]"));
            _world.Scheduler.Advance(0d);
            _mirror.FlushServer();

            Assert.AreEqual(1, hostHeard.Count, "the host's player gets its copy in process");
            CollectionAssert.AreEqual(new[] { Remote }, _mirror.ServerSendTargetsOf<CoreAiRemoteEventMessage>(),
                "the remote client gets its copy on the wire, and the local connection none");
        }

        [Test]
        public void Negative_ARemoteClientBesideTheHost_StillWaitsForItsReadiness_WhileTheHostDoesNot()
        {
            StartHostAndAdmitTheHost();
            AdmitRemote("guest");
            Assert.IsTrue(_world.Players.TryGetByActorId("actor-host", out RbxPlayer host));
            Assert.IsTrue(_world.Players.TryGetByActorId("actor-guest", out RbxPlayer guest));
            RbxRemoteEvent remote = (RbxRemoteEvent)_registry.Create("RemoteEvent");
            remote.AttachScheduler(_world.Scheduler);
            List<object[]> hostHeard = new();
            remote.GetOnClientEvent("actor-host").Connect((Action<object[]>)hostHeard.Add);

            remote.FireClient(_server, host, Encoding.UTF8.GetBytes("[1]"));
            remote.FireClient(_server, guest, Encoding.UTF8.GetBytes("[2]"));
            _world.Scheduler.Advance(0d);
            _mirror.FlushServer();

            Assert.AreEqual(1, hostHeard.Count, "the host's player is served at once");
            Assert.IsFalse(_server.IsHostLocalConnection(Remote), "a remote connection is no host-local one");
            Assert.IsFalse(_sessionHost.IsHostLocalSession(Remote));
            Assert.AreEqual(1, _server.PacketsHeldUntilReady, "the remote client's remote waits for its readiness");
            CollectionAssert.IsEmpty(_mirror.ServerSendTargetsOf<CoreAiRemoteEventMessage>());

            OfflineMirror.DeliverToServer(Remote, new CoreAiClientReadyMessage());
            _mirror.FlushServer();

            CollectionAssert.AreEqual(new[] { Remote }, _mirror.ServerSendTargetsOf<CoreAiRemoteEventMessage>());
        }

        [Test]
        public void InvokeClientOnTheHostsPlayer_IsAnsweredInProcess()
        {
            StartHostAndAdmitTheHost();
            List<RbxNetworkRequestMessage> asked = new();
            _server.RequestReceived += (message, responder) =>
            {
                asked.Add(message);
                responder.Complete(new byte[] { 42 });
            };
            List<RbxNetworkResponse> answered = new();
            using SentMessages<CoreAiRemoteRequestMessage> onMirror = new();

            _server.SendRequest(new RbxNetworkRequestMessage(new InstanceId(6UL), RbxNetworkDirection.ServerToClient,
                null, "actor-host", new byte[] { 1 }), answered.Add);

            Assert.AreEqual(1, answered.Count, "InvokeClient on the host's player is answered in process, at once");
            Assert.IsTrue(answered[0].Succeeded);
            Assert.AreEqual("actor-host", asked[0].RecipientActorId);
            CollectionAssert.IsEmpty(onMirror.Messages);
        }

        [Test]
        public void Negative_TheHostsLocalConnection_IsSentNoAnchorNoRoster_AndNeverMeetsTheReadinessDeadline()
        {
            LocalConnectionToClient local = StartHostAndAdmitTheHost();
            using SentMessages<CoreAiServerClockMessage> anchors = new();
            using SentMessages<CoreAiRosterMessage> rosters = new();

            _now = MirrorNetworkBridge.ReadinessTimeoutSeconds + MirrorNetworkBridge.ClockAnchorIntervalSeconds + 1d;
            _server.Pump();
            _server.Pump();
            OfflineMirror.PumpHost();

            CollectionAssert.IsEmpty(anchors.Messages, "the host's clock is the server's own");
            CollectionAssert.IsEmpty(rosters.Messages, "the host reads the roster from its session host");
            Assert.AreEqual(0, _server.ReadinessTimeouts);
            Assert.AreSame(local, NetworkServer.connections[local.connectionId], "the host's client stays on Mirror");
            Assert.AreEqual(1, _sessionHost.LiveSessionCount);
        }

        [Test]
        public void StoppingTheHostsClient_EndsTheHostsPlayer_OnTheNextPump()
        {
            StartHostAndAdmitTheHost();
            List<object[]> removing = new();
            _world.Players.PlayerRemoving.Connect((Action<object[]>)removing.Add);

            OfflineMirror.StopHostClient();
            Assert.AreEqual(1, _sessionHost.LiveSessionCount,
                "Mirror removed the local connection without a disconnect event; nothing has heard yet");

            _server.Pump();
            _world.Scheduler.Advance(0d);

            Assert.AreEqual(1, _server.HostLocalConnectionsReleased);
            Assert.AreEqual(0, _sessionHost.LiveSessionCount);
            CollectionAssert.IsEmpty(_server.ActorIds);
            CollectionAssert.IsEmpty(_world.Players.GetPlayers());
            Assert.AreEqual(1, removing.Count, "PlayerRemoving fires once for the host's player");
            Assert.IsFalse(_sessionHost.Roster.TryGetMember("actor-host", out _));

            _server.Pump();
            Assert.AreEqual(1, _server.HostLocalConnectionsReleased, "and the release is made once");
        }

        [Test]
        public void ANewHostClientOnTheSameId_EndsTheOldHostPlayerFirst_AndKeepsItsOwnAdmission()
        {
            StartHostAndAdmitTheHost();
            List<object[]> removing = new();
            List<object[]> added = new();
            _world.Players.PlayerRemoving.Connect((Action<object[]>)removing.Add);
            _world.Players.PlayerAdded.Connect((Action<object[]>)added.Add);
            OfflineMirror.StopHostClient();
            _authenticator.ConfigureClientCredential(() => Encoding.UTF8.GetBytes("host2"));

            // WHY no pump in between: a host that restarts its client within one frame reaches the
            // new admission before any pump noticed the old local connection was gone.
            NetworkClient.ConnectHost();
            HostMode.InvokeOnConnected();
            LocalConnectionToClient second = NetworkServer.localConnection;
            _authenticator.OnServerAuthenticate(second);
            _authenticator.OnClientAuthenticate();
            OfflineMirror.PumpHost();
            _world.Scheduler.Advance(0d);

            Assert.AreEqual(1, removing.Count, "the old host player left before the new one joined");
            Assert.AreEqual(1, added.Count);
            Assert.IsTrue(_world.Players.TryGetByActorId("actor-host2", out _));
            Assert.IsFalse(_world.Players.TryGetByActorId("actor-host", out _));
            Assert.IsTrue(_sessionHost.IsHostLocalSession(second.connectionId));
            Assert.IsNotNull(_authenticator.ResultFor(second.connectionId),
                "releasing the old host session must not take the new local client's admission with it");
            Assert.AreEqual(1, _server.HostLocalConnectionsReleased);
        }

        [Test]
        public void KickingTheHostsOwnPlayer_EndsItsSession_SendsNoNotice_AndLeavesTheLocalClientOnMirror()
        {
            LocalConnectionToClient local = StartHostAndAdmitTheHost();
            Assert.IsTrue(_world.Players.TryGetByActorId("actor-host", out RbxPlayer host));
            RbxEnumItem creatorKick = _world.Enums.Get("PlayerExitReason")["CreatorKick"];
            using SentMessages<CoreAiDisconnectNoticeMessage> notices = new();

            Assert.IsTrue(_world.Players.KickPlayer(host, creatorKick));
            _world.Scheduler.Advance(0d);
            _now = 1d;
            _server.Pump();
            OfflineMirror.PumpHost();

            Assert.AreEqual(0, _sessionHost.LiveSessionCount);
            CollectionAssert.IsEmpty(_world.Players.GetPlayers());
            CollectionAssert.IsEmpty(notices.Messages,
                "the host's client half has no handler for a notice; a notice would disconnect it");
            Assert.AreEqual(0, _server.DisconnectNoticesSent);
            Assert.AreSame(local, NetworkServer.connections[local.connectionId],
                "the local connection is never dropped: that would take the host off Mirror");
            CollectionAssert.IsEmpty(_mirror.ServerDisconnectRequests);
        }

        [Test]
        public void Negative_BindHostLocalConnection_RefusesARemoteConnection_AndAClientBridge()
        {
            OfflineMirror.StartServer();
            OfflineMirror.AdmitServerConnection(Remote);
            MirrorNetworkBridge client = new(isServer: false, clockSeconds: () => 0d);
            try
            {
                Assert.Throws<ArgumentException>(() => _server.BindHostLocalConnection(Remote,
                    new RbxNetworkPeer("actor-a", "session-a", "7")));
                Assert.Throws<InvalidOperationException>(() => client.BindHostLocalConnection(0,
                    new RbxNetworkPeer("actor-a", "session-a", "0")));
                Assert.IsFalse(_server.IsHostLocalConnection(Remote));
            }
            finally
            {
                client.Dispose();
            }
        }

        /// <summary>
        /// Starts Mirror's host mode and runs the host's own admission the way NetworkManager does:
        /// the server's connect hook, then the client's admission request over the local pair.
        /// </summary>
        private LocalConnectionToClient StartHostAndAdmitTheHost()
        {
            LocalConnectionToClient local = OfflineMirror.StartHost();
            _authenticator.OnStartServer();
            _authenticator.OnStartClient();
            _authenticator.OnServerAuthenticate(local);
            _authenticator.OnClientAuthenticate();
            OfflineMirror.PumpHost();
            Assert.IsTrue(_sessionHost.HasLiveSession(local.connectionId), "the host's own admission must have completed");
            return local;
        }

        /// <summary>Admits one remote client connection through the authenticator's real request handler.</summary>
        private void AdmitRemote(string name)
        {
            OfflineMirror.OpenServerConnection(Remote);
            OfflineMirror.DeliverToServer(Remote, new CoreAiAdmissionRequestMessage
            {
                Credential = Encoding.UTF8.GetBytes(name),
                ProtocolVersion = CoreAiMirrorProtocol.Version
            });
            Assert.IsTrue(_sessionHost.HasLiveSession(Remote), "the remote client must have been admitted");
        }

        /// <summary>Admits any credential as the actor <c>actor-&lt;credential&gt;</c>, UserIds from 1001.</summary>
        private sealed class NamedProvider : IActorAdmissionProvider
        {
            private long _nextUserId = 1001L;

            public ActorAdmissionResult TryAdmit(in ActorCredential credential, string worldId)
            {
                string name = Encoding.UTF8.GetString(credential.Opaque);
                ActorContext context = new LocalActorIdentityProvider(
                        "actor-" + name, "session-" + name, worldId, ActorGrantSet.Create(new[] { "read" }),
                        AgentMemoryScope.Empty)
                    .GetActorContext(BuiltInAgentRoleIds.SmartChat);
                return ActorAdmissionResult.Admit(context, _nextUserId++, name, name);
            }
        }
    }
}
