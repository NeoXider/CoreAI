using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using Mirror;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Net.Mirror.Tests
{
    /// <summary>
    /// The half of the Roblox remote surface that travels server to client, end to end: a client
    /// admitted through the real authenticator over a real Mirror exchange receives what the server
    /// fires, and the world's own dispatch fires the mod-visible <c>OnClientEvent</c> signal.
    /// </summary>
    /// <remarks>
    /// WHY nothing here names the fix: the fixture is written against the surface that existed
    /// before it — provider, authenticator, bridge, world bindings — so it compiles against the
    /// code without the fix and fails there at run time, where the world refuses the null recipient
    /// with an exception and Mirror answers that by disconnecting the client. WHY the client bridge
    /// is built before the server starts and the provider is never driven through Update: a host's
    /// provider refuses a client bridge beside a live server, a separate and open defect, and the
    /// frame is not what is under test here — the accept-event wiring the provider installs when
    /// the bridge is built is. WHY a refusal is observed where the authenticator hands it to
    /// Mirror and not on the wire: the authenticator disconnects inline, right after queueing the
    /// refusal, and Mirror's default transport (kcp2k) reports the drop before that call returns —
    /// at which point Mirror clears the connection's unsent batches — so the harness, which echoes
    /// a drop the same way, carries nothing to a refused client; a wire assertion would therefore
    /// assert the harness, not the product.
    /// </remarks>
    [TestFixture]
    public sealed class MirrorClientRemotesEndToEndEditModeTests
    {
        private const string Credential = "open-sesame";
        private const string WorldId = "world-a";
        private const double ServerUnix = 1790000000d;
        private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

        /// <summary>
        /// The one line a world built without a part materialiser writes on purpose, verbatim: this
        /// fixture builds such a world, having nothing to render.
        /// </summary>
        private const string HeadlessNotice =
            "[CoreAI.RbxApi] Headless mode: no part materialiser (InstanceGameObjectBinder) — "
            + "Instance.new creates data-model instances but nothing renders in the scene. If this "
            + "is a player build, check link.xml preserves CoreAI.RbxApi.* assemblies and that "
            + "RbxWorldHost is wired on CoreAiModsLifetimeScope.";

        private OfflineMirror _mirror;
        private GameObject _go;
        private CoreAiMirrorAuthenticator _authenticator;
        private CoreAiMirrorNetworkBridgeProvider _provider;
        private MirrorNetworkBridge _server;
        private MirrorNetworkBridge _client;
        private CoreAiMirrorSessionHost _sessionHost;
        private InstanceRegistry _registry;
        private LuaCsRbxApiBindings _bindings;
        private List<string> _worldLog;
        private List<string> _serverActors;
        private List<CoreAiAdmissionResponseMessage> _responsesHandedToMirror;
        private int _clientAccepts;
        private double _now;
        private FakeWallClock _serverWall;
        private FakeWallClock _clientWall;

        [SetUp]
        public void CreateBothSides()
        {
            _responsesHandedToMirror = new List<CoreAiAdmissionResponseMessage>();
            // WHY reset and not left at the field's default: NUnit runs every test of a fixture on
            // one instance, so an accept a sibling test heard would be carried into the refused
            // client's "never" and counted against it.
            _clientAccepts = 0;
            _now = 0d;
            // WHY these clocks: a server up a day and a client up five minutes whose wall clock is an
            // hour fast — the case in which Mirror's own offset read the server's time a day off.
            _serverWall = new FakeWallClock(ServerUnix, 86400d);
            _clientWall = new FakeWallClock(ServerUnix + 3600d, 300d);
            NetworkDiagnostics.OutMessageEvent += RecordAdmissionResponse;
            _mirror = new OfflineMirror(loopback: true);
            _go = new GameObject("CoreAI_ClientRemotesEndToEnd");
            _authenticator = _go.AddComponent<CoreAiMirrorAuthenticator>();
            _authenticator.Configure(new TokenProvider(Credential), WorldId);
            _provider = _go.AddComponent<CoreAiMirrorNetworkBridgeProvider>();
            _provider.Role = CoreAiMirrorRole.Client;
            _provider.ClockSeconds = () => 0d;
            _provider.WallClock = _clientWall;
            SetField(_provider, "authenticator", _authenticator);
            _client = (MirrorNetworkBridge)_provider.Bridge;
            _client.RoundTripSeconds = () => 0d;

            _serverActors = new List<string>();
            _server = new MirrorNetworkBridge(isServer: true, _authenticator, clockSeconds: () => _now,
                wallClock: _serverWall);
            _sessionHost = new CoreAiMirrorSessionHost(
                _server,
                context =>
                {
                    _serverActors.Add(context.ActorId);
                    return true;
                },
                _ => true);
            // WHY two listeners here: there is no NetworkManager, and these are the two things it
            // does on each side once the authenticator accepts.
            _authenticator.OnServerAuthenticated.AddListener(conn =>
            {
                conn.isAuthenticated = true;
                _sessionHost.Admit(conn.connectionId, _authenticator.ResultFor(conn.connectionId),
                    sessionId: null);
            });
            _authenticator.OnClientAuthenticated.AddListener(() =>
            {
                _clientAccepts++;
                NetworkClient.connection.isAuthenticated = true;
            });

            _worldLog = new List<string>();
            _registry = new InstanceRegistry(
                worldAclVersion: InstanceRegistry.CurrentWorldAclVersion, worldId: WorldId);
            _bindings = new LuaCsRbxApiBindings(_registry, DataModelBootstrap.CreateGame(_registry),
                networkBridge: _client, log: _worldLog.Add);
        }

        [TearDown]
        public void RestoreEverything()
        {
            OfflineMirror.RunAll(
                () => NetworkDiagnostics.OutMessageEvent -= RecordAdmissionResponse,
                () => _bindings.Dispose(),
                () => _sessionHost.Dispose(),
                () => _server.Dispose(),
                () => _provider.ReleaseTransport(),
                () => _mirror.Dispose(),
                () => UnityEngine.Object.DestroyImmediate(_go));
        }

        [Test]
        public void AdmittedClient_ReceivesFireClient_AndOnClientEventFiresWithTheArgumentsOnly()
        {
            string admitted = Join(Credential);
            List<object[]> received = new();
            RbxRemoteEvent remote = ClientRemoteHeardBy(admitted, received);

            _server.SendEvent(FireClient(remote, admitted, "[\"hello\",7]"));
            _mirror.PumpLoopback();
            _bindings.Scheduler.Advance(0d);

            Assert.AreEqual(1, received.Count, "FireClient never reached the client's OnClientEvent");
            CollectionAssert.AreEqual(new object[] { "hello", 7d }, received[0],
                "OnClientEvent gets the fired arguments and nothing else: the mirror prepends the "
                + "player to OnServerEvent, never to OnClientEvent");
            Assert.AreEqual(1, _client.PacketsDelivered);
            Assert.AreEqual(0, _client.UnadmittedPacketsDropped);
            Assert.AreEqual(1, _clientAccepts, "the client hears its admission exactly once");
            CollectionAssert.IsEmpty(WorldLogBesidesTheHeadlessNotice(),
                "the world must not have refused the delivery");
        }

        [Test]
        public void AdmittedClient_ReceivesFireAllClients_OnItsOwnOnClientEvent()
        {
            string admitted = Join(Credential);
            List<object[]> received = new();
            RbxRemoteEvent remote = ClientRemoteHeardBy(admitted, received);

            _server.SendEvent(FireAllClients(remote, "[\"all\"]"));
            _mirror.PumpLoopback();
            _bindings.Scheduler.Advance(0d);

            Assert.AreEqual(1, received.Count, "FireAllClients never reached the client's OnClientEvent");
            CollectionAssert.AreEqual(new object[] { "all" }, received[0]);
            Assert.AreEqual(1, _clientAccepts, "the client hears its admission exactly once");
            CollectionAssert.IsEmpty(WorldLogBesidesTheHeadlessNotice(),
                "the world must not have refused the delivery");
        }

        [Test]
        public void Negative_ARefusedClient_IsAuthenticatedNowhere_AndTheServerHoldsNoActorForIt()
        {
            Join("guess");

            Assert.AreEqual(0, _authenticator.AdmittedCount);
            Assert.AreEqual(1, _authenticator.RejectedCount);
            Assert.IsNull(_authenticator.ResultFor(OfflineMirror.LoopbackConnectionId));
            CollectionAssert.IsEmpty(_serverActors, "a refusal must create nothing in the world");
            CollectionAssert.IsEmpty(_server.ActorIds);
            Assert.AreEqual(0, _clientAccepts, "the client side must never hear an accept");
            CollectionAssert.AreEqual(new[] { OfflineMirror.LoopbackConnectionId },
                _mirror.ServerDisconnectRequests,
                "the refusal must ask the transport to drop that connection, and only that one");
            CollectionAssert.IsEmpty(NetworkServer.connections,
                "once the transport reports the drop the server holds nothing for it");
            Assert.IsFalse(NetworkClient.isConnected,
                "and the client learns of the drop from the transport, not from a message");
            CoreAiAdmissionResponseMessage told = TheOneAdmissionResponseHandedToMirror();
            Assert.IsFalse(told.Admitted);
            Assert.AreEqual("not admitted", told.Reason,
                "what leaves the authenticator is the fixed refusal, never the provider's reason");
            Assert.AreEqual("", told.ActorId, "and it names nobody");
        }

        [Test]
        public void AnUnreliableBroadcastThatOvertakesTheAdmissionResponse_LeavesTheJoiningClientConnected()
        {
            // WHY the server fires from its accept listener: that is the same frame the admission
            // response is queued in, and a game broadcasting an UnreliableRemoteEvent at 20-60 Hz
            // does exactly that. kcp2k sends the unreliable datagram at once and the reliable
            // response on its next tick, so the remote would reach the client first. The server
            // now holds nothing unreliable for a client that has not acknowledged readiness: the
            // early remote is dropped and counted where it is fired, and never overtakes anything.
            // The client's own rule for a remote that does arrive early — from a server that puts
            // one on the wire anyway — is MirrorClientRemoteRulesEditModeTests' to prove.
            RbxRemoteEvent remote = (RbxRemoteEvent)_registry.Create("UnreliableRemoteEvent");
            _authenticator.OnServerAuthenticated.AddListener(
                _ => _server.SendEvent(FireAllClients(remote, "[\"early\"]")));
            _mirror.UnreliableOvertakesReliable = true;

            string admitted = Join(Credential);

            Assert.IsTrue(NetworkClient.isConnected,
                "the early remote must not make Mirror disconnect the client it was sent to");
            Assert.AreEqual(1, _clientAccepts, "the admission response still arrived and was heard");
            Assert.AreEqual(1, _server.NotReadyPacketsDropped,
                "the early remote is the server's to drop and count, before the client said it is ready");
            Assert.AreEqual(0, _client.UnadmittedPacketsDropped,
                "so nothing reached the client before its admission was bound");
            Assert.AreEqual(1, _server.ReadyAcknowledgements);
            Assert.AreEqual(admitted, _client.AdmittedActorId);

            List<object[]> received = new();
            ListenOn(remote, admitted, received);
            _server.SendEvent(FireAllClients(remote, "[\"tick\"]"));
            _mirror.PumpLoopback();
            _bindings.Scheduler.Advance(0d);

            Assert.AreEqual(1, received.Count, "the next broadcast reaches the admitted client's OnClientEvent");
            CollectionAssert.AreEqual(new object[] { "tick" }, received[0]);
            CollectionAssert.IsEmpty(WorldLogBesidesTheHeadlessNotice());
        }

        [Test]
        public void A4_04_AClientThatFiresBeforeItsAdmission_DropsTheFire_AndIsStillAdmitted()
        {
            // WHY: the client's transport is connected before its admission is answered, and an
            // UnreliableRemoteEvent fired in that window left before the reliable admission request
            // queued with it; the server's handler requires Mirror authentication, so Mirror
            // disconnected the joining client for its own early remote (A4-04).
            _mirror.UnreliableOvertakesReliable = true;
            RbxRemoteEvent remote = (RbxRemoteEvent)_registry.Create("UnreliableRemoteEvent");
            List<RbxNetworkEventMessage> heardOnTheServer = new();
            _server.EventReceived += heardOnTheServer.Add;

            string admitted = JoinFiringFirst(Credential, () => _client.SendEvent(
                new RbxNetworkEventMessage(remote.Id, RbxNetworkDirection.ClientToServer,
                    RbxNetworkReliability.UnreliableUnordered, "remote-1", null,
                    Encoding.UTF8.GetBytes("[\"early\"]"))));

            Assert.IsTrue(NetworkClient.isConnected,
                "the early fire must not make Mirror disconnect the client that fired it");
            Assert.AreEqual(1, _clientAccepts, "the admission still arrived and was heard");
            Assert.AreEqual("remote-1", admitted);
            Assert.AreEqual(1, _client.UnadmittedSendsDropped, "the fire is the client's to drop and count");
            Assert.AreEqual(0, _client.PacketsSent, "nothing unadmitted is counted as sent");
            Assert.IsEmpty(heardOnTheServer);
            CollectionAssert.IsEmpty(_mirror.ServerDisconnectRequests);
        }

        [Test]
        public void A4_04_Negative_TheWitness_AnUnreliableRemoteThatOvertakesTheAdmissionRequest_IsFatalToTheJoin()
        {
            // WHY: proves the harness carries the client's unreliable datagram ahead of its admission
            // request into the server's real Mirror, which disconnects an unauthenticated sender —
            // so the test above passes because the client held its fire, not because the harness
            // would have delivered it harmlessly.
            _mirror.UnreliableOvertakesReliable = true;
            CoreAiRemoteEventMessage early = OfflineMirror.Event(5UL);
            early.Reliability = (byte)RbxNetworkReliability.UnreliableUnordered;
            LogAssert.Expect(LogType.Warning, new Regex("required authentication"));

            string admitted = JoinFiringFirst(Credential, () => NetworkClient.Send(early, Channels.Unreliable));

            Assert.IsNull(admitted, "the joining connection was dropped before its admission request was read");
            Assert.AreEqual(0, _clientAccepts);
            Assert.IsFalse(NetworkClient.isConnected);
        }

        [Test]
        public void B1_07_ReliableFiresAndAnInvokeServerBeforeAdmission_ReachTheServerAfterIt_InOrder_AndTheJoinSurvives()
        {
            // WHY: a FireServer on a reliable remote in the round trip between connect and admission
            // used to arrive — it followed the admission request on the ordered reliable channel and
            // the server admits in the same call — until the A4-04 fix dropped it with the unreliable
            // ones (B1-07). WHY an unreliable fire rides along with the unreliable channel overtaking
            // the reliable one: it is still the client's to drop, and the join must survive both.
            _mirror.UnreliableOvertakesReliable = true;
            List<string> heardOnTheServer = new();
            _server.EventReceived += message =>
                heardOnTheServer.Add("event " + message.Payload[0] + " from " + message.SenderActorId);
            _server.RequestReceived += (message, responder) =>
            {
                heardOnTheServer.Add("request " + message.Payload[0] + " from " + message.SenderActorId);
                responder.Complete(new byte[] { 42 });
            };
            List<RbxNetworkResponse> answered = new();

            string admitted = JoinFiringFirst(Credential, () =>
            {
                _client.SendEvent(ClientFire(RbxNetworkReliability.ReliableOrdered, 1));
                _client.SendEvent(ClientFire(RbxNetworkReliability.UnreliableUnordered, 2));
                _client.SendRequest(new RbxNetworkRequestMessage(new InstanceId(6UL),
                    RbxNetworkDirection.ClientToServer, "remote-1", null, new byte[] { 3 }), answered.Add);
                _client.SendEvent(ClientFire(RbxNetworkReliability.ReliableOrdered, 4));
            });

            Assert.AreEqual("remote-1", admitted);
            Assert.IsTrue(NetworkClient.isConnected, "nothing the client fired early cost it the join");
            CollectionAssert.IsEmpty(_mirror.ServerDisconnectRequests);
            CollectionAssert.AreEqual(
                new[] { "event 1 from remote-1", "request 3 from remote-1", "event 4 from remote-1" },
                heardOnTheServer,
                "the reliable ones reach the server after the admission, in the order they were fired");
            Assert.AreEqual(3, _client.SendsHeldUntilAdmitted);
            Assert.AreEqual(1, _client.UnadmittedSendsDropped, "the unreliable one is the client's to drop");
            Assert.AreEqual(3, _client.PacketsSent);
            Assert.AreEqual(1, answered.Count, "the InvokeServer made before the admission is answered");
            Assert.IsTrue(answered[0].Succeeded);
            CollectionAssert.AreEqual(new byte[] { 42 }, answered[0].Payload);
            Assert.AreEqual(1, _server.ReadyAcknowledgements);
        }

        [Test]
        public void Negative_AnAdmittedClientThatAsksAgain_GetsNoSecondPlayer_AndIsNotDisconnected()
        {
            string admitted = Join(Credential);
            Assert.IsNotNull(admitted);

            _authenticator.OnClientAuthenticate();
            _mirror.PumpLoopback();

            CollectionAssert.AreEqual(new[] { admitted }, _serverActors,
                "one connection is one admission: asking again mints no second actor");
            Assert.AreEqual(1, _authenticator.AdmittedCount, "the host's provider is asked once");
            Assert.AreEqual(1, _authenticator.IgnoredAdmissionRequests);
            Assert.AreEqual(1, _clientAccepts);
            TheOneAdmissionResponseHandedToMirror();
            CollectionAssert.IsEmpty(_mirror.ServerDisconnectRequests,
                "the repeat is ignored; the session the connection holds is left as it was");
            Assert.IsTrue(NetworkClient.isConnected);
            CollectionAssert.AreEqual(new[] { admitted }, _server.ActorIds);
        }

        [Test]
        public void AReliableRemoteFiredAtAdmission_IsHeldForTheJoiningClient_AndReachesItsOnClientEventOnceReady()
        {
            // WHY: the Roblox idiom — PlayerAdded:Connect(function(p) remote:FireClient(p, state) end)
            // — fires in the very call that admits the connection. The readiness hold must deliver
            // it, in order, the moment the client can route it; losing it would be the regression.
            RbxRemoteEvent remote = (RbxRemoteEvent)_registry.Create("RemoteEvent");
            List<object[]> received = new();
            ListenOn(remote, "remote-1", received);
            _authenticator.OnServerAuthenticated.AddListener(
                _ => _server.SendEvent(FireClient(remote, _serverActors[0], "[\"welcome\"]")));

            string admitted = Join(Credential);
            _bindings.Scheduler.Advance(0d);

            Assert.AreEqual("remote-1", admitted, "the fixture's provider names its first actor so");
            Assert.AreEqual(1, _server.PacketsHeldUntilReady, "held while the client had not said it is ready");
            Assert.AreEqual(1, received.Count, "and delivered once it had");
            CollectionAssert.AreEqual(new object[] { "welcome" }, received[0]);
            Assert.AreEqual(0, _client.UnadmittedPacketsDropped);
            CollectionAssert.IsEmpty(WorldLogBesidesTheHeadlessNotice());
        }

        [Test]
        public void AJoiningClient_ReadsTheServersClock_FromTheAnchorItsReadinessBrings_WhateverItsOwnWallClock()
        {
            Assert.AreEqual(0d, _client.ServerClockOffsetSeconds, "nothing is known before the join");
            _now = 86400d;

            Join(Credential);

            Assert.IsTrue(_client.IsServerClockSynchronized);
            Assert.AreEqual(1, _server.ClockAnchorsSent);
            Assert.AreEqual(1, _client.ClockAnchorsReceived);
            double serverNow = _clientWall.UnixTimeSecondsFractional + _client.ServerClockOffsetSeconds;
            Assert.Less(Math.Abs(serverNow - ServerUnix), 0.2d,
                "the client reads the server's wall clock, not its own an hour fast, and neither "
                + "machine's uptime enters it");

            _serverWall.Advance(30d);
            _clientWall.Advance(30d);

            serverNow = _clientWall.UnixTimeSecondsFractional + _client.ServerClockOffsetSeconds;
            Assert.Less(Math.Abs(serverNow - (ServerUnix + 30d)), 0.2d,
                "and keeps reading it between anchors");
        }

        [Test]
        public void A4_08_AfterTheServersWallClockStepsBack_ServerAndClientTime_AgreeWithinASecondThroughout()
        {
            // WHY: the anchors carried the server's raw wall clock while the server's own
            // GetServerTimeNow held its last reading after a backward step, and the client slewed on
            // at half speed — half the step apart by the end of the hold (A4-08, A3-04). WHY one
            // world per side over the real exchange: agreement is between what two scripts read.
            LuaCsRbxApiBindings serverWorld = new(new InstanceRegistry(), null, networkBridge: _server,
                clockSource: _serverWall, log: _ => { });
            LuaCsRbxApiBindings clientWorld = new(new InstanceRegistry(), null, networkBridge: _client,
                clockSource: _clientWall, log: _ => { });
            try
            {
                Join(Credential);
                const double stepBack = 600d;
                double worst = 0d;
                double heldAt = 0d;
                double atMidHold = 0d;
                for (int second = 1; second <= 700; second++)
                {
                    _serverWall.Advance(1d);
                    _clientWall.Advance(1d);
                    _now += 1d;
                    if (second == 30)
                    {
                        _serverWall.UnixTimeSecondsFractional -= stepBack;
                    }

                    _server.Pump();
                    _mirror.PumpLoopback();
                    double server = ServerTimeNow(serverWorld);
                    double client = ServerTimeNow(clientWorld);
                    worst = Math.Max(worst, Math.Abs(server - client));
                    Assert.Less(Math.Abs(server - client), 1d,
                        "second " + second + ": the server's scripts read " + server.ToString("F3")
                        + " and the client's read " + client.ToString("F3"));
                    if (second == 30)
                    {
                        heldAt = server;
                    }

                    if (second == 300)
                    {
                        atMidHold = server;
                    }
                }

                Assert.AreEqual(ServerUnix + 29d, heldAt, "the server holds its last reading at the step");
                Assert.AreEqual(heldAt, atMidHold, "and still holds it half-way through");
                Assert.AreEqual(_serverWall.UnixTimeSecondsFractional, ServerTimeNow(serverWorld),
                    "once the wall clock caught up the server runs on it again");
                Assert.Greater(_server.ClockStepAnchorsSent, 0, "the hold was sent the moment it began");
                Assert.IsFalse(_client.IsServerClockHeld);
            }
            finally
            {
                serverWorld.Dispose();
                clientWorld.Dispose();
            }
        }

        [Test]
        public void AServerKick_ReachesTheKickedClientAsANotice_BeforeTheTransportDropsIt()
        {
            string admitted = Join(Credential);
            List<bool> connectedWhenTold = new();
            _client.DisconnectNoticeReceived += _ => connectedWhenTold.Add(NetworkClient.isConnected);

            _server.DisconnectActor(admitted, "banned for griefing");
            _mirror.PumpLoopback();

            CollectionAssert.AreEqual(new[] { true }, connectedWhenTold,
                "the reason arrives while the client is still connected");
            Assert.IsTrue(_client.LastDisconnectNotice.HasValue);
            Assert.AreEqual((byte)CoreAiDisconnectNoticeKind.Kicked, _client.LastDisconnectNotice.Value.Kind);
            Assert.AreEqual("banned for griefing", _client.LastDisconnectNotice.Value.Message);
            CollectionAssert.IsEmpty(_mirror.ServerDisconnectRequests, "the drop waits for a later frame");
            Assert.AreEqual(0, _sessionHost.LiveSessionCount, "while the session is already over");

            _now = 1d;
            _server.Pump();
            _mirror.PumpLoopback();

            CollectionAssert.AreEqual(new[] { OfflineMirror.LoopbackConnectionId },
                _mirror.ServerDisconnectRequests);
            Assert.IsFalse(NetworkClient.isConnected, "and then the transport drops it");
            Assert.AreEqual("banned for griefing", _client.LastDisconnectNotice.Value.Message,
                "the reason outlives the disconnect, for the host to show");
        }

        [Test]
        public void AServerScriptsKickWithAMessage_ReachesTheKickedClient_AsThatText()
        {
            // WHY: Player:Kick(message) dropped the text between the script and the transport, so
            // the kicked client was shown the transport's default notice whatever the script said.
            // WHY the context is detached: the Lua VM completes its continuations on the thread
            // pool, and Unity's editor context would queue them behind this very test.
            SynchronizationContext savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            InstanceRegistry serverRegistry = new(
                worldAclVersion: InstanceRegistry.CurrentWorldAclVersion, worldId: WorldId);
            LuaCsRbxApiBindings serverWorld = new(serverRegistry,
                DataModelBootstrap.CreateGame(serverRegistry), networkBridge: _server, log: _ => { });
            try
            {
                serverWorld.Players.IdentitySource = _sessionHost;
                LuaCsModStack serverMods = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
                {
                    ModStore = new MemoryModStore(),
                    Capabilities = LuaCapabilities.Read | LuaCapabilities.WorldEdit,
                    OneOffCapabilities = LuaCapabilities.Read | LuaCapabilities.WorldEdit,
                    RbxApi = serverWorld
                });
                string admitted = Join(Credential);
                RbxPlayer kicked = serverWorld.ConnectActor(ActorFor(admitted));

                serverMods.Runtime.LoadMod(ActorFor(admitted), "kick-self",
                    "game:GetService('Players'):GetPlayerByUserId(" + kicked.UserId
                    + "):Kick('banned for griefing')",
                    persistToStore: false);
                _mirror.PumpLoopback();

                Assert.IsTrue(kicked.IsDestroyed, "the script's kick removed the player on the server");
                Assert.IsTrue(_client.LastDisconnectNotice.HasValue, "the kicked client is told why");
                Assert.AreEqual((byte)CoreAiDisconnectNoticeKind.Kicked, _client.LastDisconnectNotice.Value.Kind);
                Assert.AreEqual("banned for griefing", _client.LastDisconnectNotice.Value.Message,
                    "in the words of the server's script, not the transport's default");
                Assert.AreEqual(0, _sessionHost.LiveSessionCount);
            }
            finally
            {
                serverWorld.Dispose();
                SynchronizationContext.SetSynchronizationContext(savedContext);
            }
        }

        [Test]
        public void Negative_TheWitness_ADropInTheKicksOwnFrame_WouldLoseTheNotice()
        {
            // WHY: the owed drop exists because Mirror discards a dropped connection's unflushed
            // messages; this proves the harness models that loss, so the test above is not passing
            // on a harness that would deliver the notice anyway.
            string admitted = Join(Credential);

            _server.DisconnectActor(admitted, "banned");
            _server.PerformOwedDropsNow();
            _mirror.PumpLoopback();

            Assert.AreEqual(1, _server.DisconnectNoticesSent, "the notice was handed to Mirror");
            Assert.IsFalse(_client.LastDisconnectNotice.HasValue, "and never left: the drop discarded it");
            Assert.IsFalse(NetworkClient.isConnected);
        }

        [Test]
        public void AClientsOwnKick_DisconnectsTheClient_AndTheServerEndsTheSession()
        {
            string admitted = Join(Credential);
            NetworkServer.OnDisconnectedEvent = conn =>
                _server.NotifyDisconnected(conn.connectionId, RbxNetworkDisconnectReason.TransportLost);
            RbxPlayer self = _bindings.ConnectActor(ActorFor(admitted));
            RbxEnumItem creatorKick = _bindings.Enums.Get("PlayerExitReason")["CreatorKick"];

            Assert.IsTrue(_bindings.Players.KickPlayer(self, creatorKick));

            Assert.IsFalse(NetworkClient.isConnected,
                "a LocalScript's Player:Kick() on its own player disconnects the client, as on Roblox");
            Assert.AreEqual(1, _client.SelfKicks);
            Assert.IsNull(_client.AdmittedActorId);

            _mirror.PumpLoopback();

            Assert.AreEqual(0, _sessionHost.LiveSessionCount, "the server hears the drop and ends the session");
            CollectionAssert.IsEmpty(_server.ActorIds);
        }

        [Test]
        public void Negative_AClientKickingALocalPlayerThatIsNotItsOwn_StaysConnected()
        {
            Join(Credential);
            RbxPlayer other = _bindings.ConnectActor(ActorFor("local-guest"));
            RbxEnumItem creatorKick = _bindings.Enums.Get("PlayerExitReason")["CreatorKick"];

            Assert.IsTrue(_bindings.Players.KickPlayer(other, creatorKick));

            Assert.IsTrue(NetworkClient.isConnected, "only this client's own player ends its connection");
            Assert.AreEqual(0, _client.SelfKicks);
            Assert.AreEqual(1, _sessionHost.LiveSessionCount);
        }

        /// <summary>Runs the whole admission exchange and returns the actor the server admitted, or null.</summary>
        private string Join(string credential)
        {
            return JoinFiringFirst(credential, null);
        }

        /// <summary>
        /// <see cref="Join"/>, with <paramref name="fire"/> run on the client right after it asks for
        /// admission and before either side hears anything: the window a client script's early
        /// remote falls in.
        /// </summary>
        private string JoinFiringFirst(string credential, Action fire)
        {
            _authenticator.ConfigureClientCredential(() => Encoding.UTF8.GetBytes(credential));
            OfflineMirror.StartServer();
            _authenticator.OnStartServer();
            _mirror.ConnectLoopback();
            _authenticator.OnStartClient();
            _authenticator.OnClientAuthenticate();
            fire?.Invoke();
            _mirror.PumpLoopback();
            return _serverActors.Count == 1 ? _serverActors[0] : null;
        }

        /// <summary>
        /// The client world's side of a LocalScript that connects <c>OnClientEvent</c>: the actor
        /// joins the world under its own id and listens on the remote's signal for that id.
        /// </summary>
        private RbxRemoteEvent ClientRemoteHeardBy(string actorId, List<object[]> received)
        {
            RbxRemoteEvent remote = (RbxRemoteEvent)_registry.Create("RemoteEvent");
            ListenOn(remote, actorId, received);
            return remote;
        }

        /// <summary>The client world's side of a LocalScript listening on an existing remote.</summary>
        private void ListenOn(RbxRemoteEvent remote, string actorId, List<object[]> received)
        {
            Assert.IsNotNull(actorId, "admission must have succeeded before a client can listen");
            _bindings.ConnectActor(ActorFor(actorId));
            remote.AttachScheduler(_bindings.Scheduler);
            remote.GetOnClientEvent(actorId).Connect((Action<object[]>)received.Add);
        }

        /// <summary>
        /// Every line the world logged except the one it writes, on purpose, when built without a
        /// part materialiser.
        /// </summary>
        /// <remarks>
        /// WHY one exact line and not a prefix: the notice is the only benign line a headless world
        /// writes, and any other wording — the registry-less variant of the same notice included —
        /// is something new that this fixture must not absorb.
        /// </remarks>
        private List<string> WorldLogBesidesTheHeadlessNotice()
        {
            List<string> lines = new(_worldLog);
            lines.Remove(HeadlessNotice);
            return lines;
        }

        /// <summary>
        /// The admission response the server handed Mirror for the loopback client, as the
        /// authenticator built it, seen at Mirror's send boundary.
        /// </summary>
        private CoreAiAdmissionResponseMessage TheOneAdmissionResponseHandedToMirror()
        {
            Assert.AreEqual(1, _responsesHandedToMirror.Count,
                "the server must answer one admission request with exactly one response");
            return _responsesHandedToMirror[0];
        }

        private void RecordAdmissionResponse(NetworkDiagnostics.MessageInfo info)
        {
            if (info.message is CoreAiAdmissionResponseMessage told)
            {
                _responsesHandedToMirror.Add(told);
            }
        }

        /// <summary>The message <c>RemoteEvent:FireClient(player, ...)</c> hands the bridge.</summary>
        private static RbxNetworkEventMessage FireClient(RbxRemoteEvent remote, string recipient,
            string envelope)
        {
            return new RbxNetworkEventMessage(remote.Id, RbxNetworkDirection.ServerToClient,
                remote.Reliability, null, recipient, Encoding.UTF8.GetBytes(envelope));
        }

        /// <summary>
        /// The message a client's <c>FireServer</c> hands its bridge, with a payload that starts with
        /// <paramref name="tag"/> so the server side can tell the fires apart.
        /// </summary>
        private static RbxNetworkEventMessage ClientFire(RbxNetworkReliability reliability, byte tag)
        {
            return new RbxNetworkEventMessage(new InstanceId(5UL), RbxNetworkDirection.ClientToServer,
                reliability, "remote-1", null, new[] { tag });
        }

        /// <summary>The message <c>RemoteEvent:FireAllClients(...)</c> hands the bridge.</summary>
        private static RbxNetworkEventMessage FireAllClients(RbxRemoteEvent remote, string envelope)
        {
            return new RbxNetworkEventMessage(remote.Id, RbxNetworkDirection.ServerToAllClients,
                remote.Reliability, null, null, Encoding.UTF8.GetBytes(envelope));
        }

        /// <summary>The trusted context a client composition's identity provider issues for an actor.</summary>
        private static ActorContext ActorFor(string actorId)
        {
            return new LocalActorIdentityProvider(
                    actorId, "session-" + actorId, WorldId, ActorGrantSet.None, AgentMemoryScope.Empty)
                .GetActorContext(BuiltInAgentRoleIds.Programmer);
        }

        /// <summary>
        /// What <c>workspace:GetServerTimeNow()</c> returns in a world, read without a script so a
        /// long run of seconds stays cheap.
        /// </summary>
        private static double ServerTimeNow(LuaCsRbxApiBindings world)
        {
            MethodInfo read = typeof(LuaCsRbxApiBindings).GetMethod("GetServerTimeNow", Private, null,
                Type.EmptyTypes, null);
            Assert.IsNotNull(read, "the world's GetServerTimeNow backs workspace:GetServerTimeNow()");
            return (double)read.Invoke(world, null);
        }

        private static void SetField(object target, string name, object value)
        {
            FieldInfo field = target.GetType().GetField(name, Private);
            Assert.IsNotNull(field, name);
            field.SetValue(target, value);
        }

        /// <summary>The per-mod key-value store a server script's <c>store_set</c> writes to, in memory.</summary>
        private sealed class MemoryModStore : ILuaModStore
        {
            private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

            public string Get(string modId, string key)
            {
                return _values.TryGetValue(modId + "\n" + key, out string value) ? value : "";
            }

            public void Set(string modId, string key, string value)
            {
                if (value == null)
                {
                    _values.Remove(modId + "\n" + key);
                    return;
                }

                _values[modId + "\n" + key] = value;
            }

            public void Clear(string modId)
            {
                List<string> keys = new();
                foreach (string key in _values.Keys)
                {
                    if (key.StartsWith(modId + "\n", StringComparison.Ordinal))
                    {
                        keys.Add(key);
                    }
                }

                for (int index = 0; index < keys.Count; index++)
                {
                    _values.Remove(keys[index]);
                }
            }
        }

        private sealed class TokenProvider : IActorAdmissionProvider
        {
            private readonly string _expected;
            private int _issued;

            public TokenProvider(string expected)
            {
                _expected = expected;
            }

            public ActorAdmissionResult TryAdmit(in ActorCredential credential, string worldId)
            {
                string offered = Encoding.UTF8.GetString(credential.Opaque);
                if (!string.Equals(offered, _expected, StringComparison.Ordinal))
                {
                    return ActorAdmissionResult.Reject("credential mismatch");
                }

                _issued++;
                ActorContext context = new LocalActorIdentityProvider(
                        "remote-" + _issued,
                        "session-" + _issued,
                        worldId,
                        ActorGrantSet.Create(new[] { "read" }),
                        AgentMemoryScope.Empty)
                    .GetActorContext(BuiltInAgentRoleIds.SmartChat);
                return ActorAdmissionResult.Admit(context, 1000 + _issued, "player" + _issued, "");
            }
        }
    }
}
