using System;
using System.Collections.Generic;
using System.Text;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Net.Mirror.Tests
{
    /// <summary>
    /// <c>Player:Kick()</c> over the Mirror transport: the kicked client's connection is dropped,
    /// the session host and the bridge forget it, and its next remote resurrects nothing.
    /// </summary>
    /// <remarks>
    /// WHY a real world and not a stand-in: the resurrection is the world's own doing — the
    /// server's inbound dispatch creates a player for any admitted sender — so only the world's
    /// dispatch, fed through the bridge's connection map, can show that a kicked connection no
    /// longer counts as admitted.
    /// </remarks>
    [TestFixture]
    public sealed class MirrorKickEditModeTests
    {
        private const int Connection = 7;
        private const string Credential = "open-sesame";
        private const string WorldId = "world-a";

        private OfflineMirror _mirror;
        private GameObject _go;
        private CoreAiMirrorAuthenticator _authenticator;
        private MirrorNetworkBridge _server;
        private CoreAiMirrorSessionHost _sessionHost;
        private InstanceRegistry _registry;
        private LuaCsRbxApiBindings _bindings;
        private List<string> _worldLog;
        private double _now;

        [SetUp]
        public void CreateServerWorld()
        {
            _mirror = new OfflineMirror();
            _now = 0d;
            _go = new GameObject("CoreAI_MirrorKick");
            _authenticator = _go.AddComponent<CoreAiMirrorAuthenticator>();
            _authenticator.Configure(new TokenProvider(Credential), WorldId);
            _server = new MirrorNetworkBridge(isServer: true, _authenticator, clockSeconds: () => _now);
            _worldLog = new List<string>();
            _registry = new InstanceRegistry(
                worldAclVersion: InstanceRegistry.CurrentWorldAclVersion, worldId: WorldId);
            _bindings = new LuaCsRbxApiBindings(_registry, DataModelBootstrap.CreateGame(_registry),
                networkBridge: _server, log: _worldLog.Add);
            _sessionHost = new CoreAiMirrorSessionHost(
                _server,
                context => _bindings.ConnectActor(context) != null,
                context => _bindings.DisconnectActor(context));
            _bindings.Players.IdentitySource = _sessionHost;
            OfflineMirror.StartServer();
            _authenticator.OnStartServer();
        }

        [TearDown]
        public void RestoreEverything()
        {
            OfflineMirror.RunAll(
                () => _bindings.Dispose(),
                () => _sessionHost.Dispose(),
                () => _server.Dispose(),
                () => _mirror.Dispose(),
                () => UnityEngine.Object.DestroyImmediate(_go));
        }

        [Test]
        public void Kick_DropsTheConnection_AndTheKickedClientsNextRemoteResurrectsNoPlayer()
        {
            RbxPlayer player = Admit(Connection);
            RbxRemoteEvent remote = (RbxRemoteEvent)_registry.Create("RemoteEvent");
            OfflineMirror.DeliverToServer(Connection, ClientEvent(remote));
            Assert.AreEqual(1, _server.PacketsDelivered, "the admitted client must reach the world before the kick");
            List<object[]> removing = new();
            _bindings.Players.PlayerRemoving.Connect((Action<object[]>)removing.Add);
            int added = 0;
            _bindings.Players.PlayerAdded.Connect((Action<object[]>)(_ => added++));
            RbxEnumItem creatorKick = _bindings.Enums.Get("PlayerExitReason")["CreatorKick"];

            Assert.IsTrue(_bindings.Players.KickPlayer(player, creatorKick));
            _bindings.Scheduler.Advance(0d);
            _mirror.FlushServer();

            CollectionAssert.AreEqual(new[] { Connection },
                _mirror.ServerSendTargetsOf<CoreAiDisconnectNoticeMessage>(),
                "the kicked client is told why before its connection ends");
            CollectionAssert.IsEmpty(_mirror.ServerDisconnectRequests,
                "the drop waits for a later frame, or Mirror would discard the notice with it");
            NextFrame();
            CollectionAssert.AreEqual(new[] { Connection }, _mirror.ServerDisconnectRequests,
                "a kick must end the kicked client's connection at the transport");
            Assert.AreEqual(0, _sessionHost.LiveSessionCount, "the session host must not keep the kicked session");
            Assert.IsFalse(_sessionHost.HasLiveSession(Connection));
            CollectionAssert.IsEmpty(_server.ActorIds);
            Assert.IsNull(_authenticator.ResultFor(Connection),
                "the kicked client's admission must not linger for a reused id");
            Assert.AreEqual(1, removing.Count, "PlayerRemoving fires once, for the kick");
            Assert.AreSame(creatorKick, removing[0][1],
                "with the kick's reason — not again as Unknown from the drop's own teardown");
            CollectionAssert.IsEmpty(_bindings.Players.GetPlayers());

            OfflineMirror.DeliverToServer(Connection, ClientEvent(remote));
            _bindings.Scheduler.Advance(0d);

            CollectionAssert.IsEmpty(_bindings.Players.GetPlayers(),
                "the kicked client's next remote must not re-create its player");
            Assert.AreEqual(0, added);
            Assert.AreEqual(1, _server.UnadmittedPacketsDropped,
                "the kicked connection is nobody, and its packet is dropped as unadmitted");

            _server.NotifyDisconnected(Connection, RbxNetworkDisconnectReason.TransportLost);
            _bindings.Scheduler.Advance(0d);

            Assert.AreEqual(1, removing.Count, "the transport's own report of the drop finds nothing left to tear down");
        }

        [Test]
        public void Kick_WithAMessage_TellsTheKickedClientThatText_AndALongOneWholeUpToTheCeiling()
        {
            // WHY: the world's kick dropped the script's message, so every kicked client was told the
            // default; and a text cut only by the transport would lose two thirds of the ceiling,
            // because the transport cuts an oversize text to a third of its bytes.
            RbxPlayer told = Admit(Connection);
            RbxPlayer toldAtLength = Admit(Connection + 1);
            RbxEnumItem creatorKick = _bindings.Enums.Get("PlayerExitReason")["CreatorKick"];
            string longMessage = "banned: " + new string('x', 3000);
            using SentMessages<CoreAiDisconnectNoticeMessage> notices = new();

            Assert.IsTrue(_bindings.Players.KickPlayer(told, creatorKick, "banned for griefing"));
            Assert.IsTrue(_bindings.Players.KickPlayer(toldAtLength, creatorKick, longMessage));

            Assert.AreEqual(2, notices.Messages.Count, "each kicked client is told why");
            Assert.AreEqual((byte)CoreAiDisconnectNoticeKind.Kicked, notices.Messages[0].Kind);
            Assert.AreEqual("banned for griefing", notices.Messages[0].Message);
            Assert.AreEqual(longMessage.Substring(0, MirrorNetworkBridge.MaxNoticeMessageBytes),
                notices.Messages[1].Message,
                "a long message arrives whole up to the ceiling: the world cut it once, on a character");
        }

        [Test]
        public void TheWorldsKickMessageCeiling_IsTheTransportsNoticeCeiling()
        {
            // WHY: the world cuts a kick message so that the transport never has to; a world ceiling
            // above the transport's would have the transport cut the text again, to a third.
            Assert.AreEqual(MirrorNetworkBridge.MaxNoticeMessageBytes, RbxPlayers.MaxKickMessageBytes);
        }

        [Test]
        public void Negative_AWorldWhoseTeardownThrows_StillHasTheKickedConnectionDropped_AndTheKickedActorIsNobody()
        {
            // WHY the fixture's host is swapped for one whose disconnect entry point throws: the
            // throw has to come out of the world's own teardown — what the session host calls for
            // the kick, where PlayerRemoving's mod code runs — and the fixture's world does not throw.
            _sessionHost.Dispose();
            _sessionHost = new CoreAiMirrorSessionHost(
                _server,
                context => _bindings.ConnectActor(context) != null,
                _ => throw new InvalidOperationException("the kick's teardown blew up"));
            _bindings.Players.IdentitySource = _sessionHost;
            RbxPlayer player = Admit(Connection);
            RbxRemoteEvent remote = (RbxRemoteEvent)_registry.Create("RemoteEvent");
            int added = 0;
            _bindings.Players.PlayerAdded.Connect((Action<object[]>)(_ => added++));
            RbxEnumItem creatorKick = _bindings.Enums.Get("PlayerExitReason")["CreatorKick"];

            Assert.Throws<InvalidOperationException>(
                () => _bindings.Players.KickPlayer(player, creatorKick),
                "the world's throw is the kicker's to see; the bridge must not swallow it");
            NextFrame();

            // WHY this is the assertion that fails without the bridge's finally: the session host
            // releases its own binding in a finally of its own, but the transport is asked only by
            // the bridge, after the report that threw — skipped, the socket stays open on a
            // connection authenticated to Mirror and bound to nobody.
            CollectionAssert.AreEqual(new[] { Connection }, _mirror.ServerDisconnectRequests,
                "the kicked client's connection must end at the transport even though the world's "
                + "teardown threw");
            Assert.AreEqual(0, _sessionHost.LiveSessionCount);
            Assert.IsFalse(_sessionHost.HasLiveSession(Connection));
            CollectionAssert.IsEmpty(_server.ActorIds);
            Assert.IsNull(_authenticator.ResultFor(Connection),
                "the kicked client's admission must not linger for a reused id");
            CollectionAssert.IsEmpty(_bindings.Players.GetPlayers());

            OfflineMirror.DeliverToServer(Connection, ClientEvent(remote));
            _bindings.Scheduler.Advance(0d);

            Assert.AreEqual(1, _server.UnadmittedPacketsDropped,
                "the kicked connection is nobody, and its next packet is dropped as unadmitted");
            Assert.AreEqual(0, added, "the kicked client's next remote must not re-create its player");
            CollectionAssert.IsEmpty(_bindings.Players.GetPlayers());
        }

        [Test]
        public void SoloWorld_Kick_RemovesThePlayer_AndTheKickedActorsNextRemoteIsAFreshJoin()
        {
            InstanceRegistry registry = new(
                worldAclVersion: InstanceRegistry.CurrentWorldAclVersion, worldId: WorldId);
            NullNetworkBridge loopback = new();
            LuaCsRbxApiBindings solo = new(registry, DataModelBootstrap.CreateGame(registry),
                networkBridge: loopback, log: _ => { });
            try
            {
                ActorContext actor = new LocalActorIdentityProvider(
                        "solo-1", "session-solo", WorldId, ActorGrantSet.None, AgentMemoryScope.Empty)
                    .GetActorContext(BuiltInAgentRoleIds.Programmer);
                RbxPlayer player = solo.ConnectActor(actor);
                RbxRemoteEvent remote = (RbxRemoteEvent)registry.Create("RemoteEvent");
                int added = 0;
                solo.Players.PlayerAdded.Connect((Action<object[]>)(_ => added++));
                RbxEnumItem creatorKick = solo.Enums.Get("PlayerExitReason")["CreatorKick"];

                Assert.IsTrue(solo.Players.KickPlayer(player, creatorKick));

                CollectionAssert.IsEmpty(solo.Players.GetPlayers());
                Assert.IsFalse(solo.Players.KickPlayer(player, creatorKick), "a second kick finds nobody");
                CollectionAssert.AreEqual(new[] { actor.ActorId }, loopback.ActorIds,
                    "the loopback has no connection to end, so a kick is the Player teardown alone "
                    + "and the in-process actor stays registered: the world's own admission, not a socket's");

                remote.FireServer(loopback, actor.ActorId, Encoding.UTF8.GetBytes("[]"));
                solo.Scheduler.Advance(0d);

                // WHY this outcome is pinned: it is the documented loopback semantics, not an
                // accident — with no connection to drop, the kicked actor's next remote is a fresh
                // join, the Player is re-created and PlayerAdded fires again. The Mirror transport
                // differs precisely because there the kick ends a connection.
                Assert.AreEqual(1, added,
                    "documented loopback semantics, not an accident: the kicked actor's next remote "
                    + "is a fresh join, so PlayerAdded fires again");
                Assert.IsTrue(solo.Players.TryGetByActorId(actor.ActorId, out RbxPlayer rejoined),
                    "the fresh join re-creates the player");
                Assert.AreNotSame(player, rejoined,
                    "the kicked Player was destroyed; the fresh join is a new one, not a resurrection");
                Assert.AreEqual(1, solo.Players.GetPlayers().Count);
            }
            finally
            {
                solo.Dispose();
            }
        }

        [Test]
        public void Reconnect_BeforeTheOldLinkTimesOut_KeepsThePlayer_AndPlayerRemovingWaitsForTheLastSession()
        {
            // WHY a real world: "the Player carries over" is the world's own behaviour — its
            // connect entry point finds the Player the durable actor already has — and only the
            // world's PlayerAdded and PlayerRemoving can show that nobody left and nobody joined.
            _authenticator.Configure(new DurableProvider("remote-durable"), WorldId);
            RbxPlayer player = Admit(Connection);
            RbxRemoteEvent remote = (RbxRemoteEvent)_registry.Create("RemoteEvent");
            List<object[]> removing = new();
            _bindings.Players.PlayerRemoving.Connect((Action<object[]>)removing.Add);
            int added = 0;
            _bindings.Players.PlayerAdded.Connect((Action<object[]>)(_ => added++));

            RbxPlayer rejoined = Admit(Connection + 1);
            _bindings.Scheduler.Advance(0d);
            _mirror.FlushServer();

            Assert.AreSame(player, rejoined, "the durable actor keeps its Player across the reconnect");
            Assert.AreEqual(0, added, "nobody joined: PlayerAdded does not fire again");
            CollectionAssert.AreEqual(new[] { Connection },
                _mirror.ServerSendTargetsOf<CoreAiDisconnectNoticeMessage>(),
                "the older client is told its session was replaced");
            CollectionAssert.IsEmpty(_mirror.ServerDisconnectRequests, "the drop waits for a later frame");
            NextFrame();
            CollectionAssert.AreEqual(new[] { Connection }, _mirror.ServerDisconnectRequests,
                "the older connection is closed: the newest session wins");

            _server.NotifyDisconnected(Connection, RbxNetworkDisconnectReason.TransportLost);
            _bindings.Scheduler.Advance(0d);

            Assert.IsEmpty(removing, "the old link's late drop must not remove the player the new link carries");
            Assert.AreEqual(1, _bindings.Players.GetPlayers().Count);
            OfflineMirror.DeliverToServer(Connection + 1, ClientEvent(remote));
            OfflineMirror.DeliverToServer(Connection, ClientEvent(remote));
            Assert.AreEqual(1, _server.PacketsDelivered, "the new connection is heard");
            Assert.AreEqual(1, _server.UnadmittedPacketsDropped, "the old one is nobody");

            _server.NotifyDisconnected(Connection + 1, RbxNetworkDisconnectReason.TransportLost);
            _bindings.Scheduler.Advance(0d);

            Assert.AreEqual(1, removing.Count, "PlayerRemoving fires once, when the last session ends");
            CollectionAssert.IsEmpty(_bindings.Players.GetPlayers());
            Assert.AreEqual(0, _sessionHost.LiveSessionCount);
            CollectionAssert.IsEmpty(_server.ActorIds);
        }

        /// <summary>
        /// The next frame's pump: the bridge's clock reads later, so a drop owed since the last
        /// frame is made.
        /// </summary>
        private void NextFrame()
        {
            _now += 0.016d;
            _server.Pump();
        }

        /// <summary>Admits one connection through the authenticator and the session host, returning its Player.</summary>
        private RbxPlayer Admit(int connectionId)
        {
            OfflineMirror.AdmitServerConnection(connectionId);
            ActorAdmissionResult admission = _authenticator.Decide(connectionId,
                "127.0.0.1:" + connectionId, Encoding.UTF8.GetBytes(Credential));
            Assert.IsTrue(admission.Admitted);
            Assert.IsTrue(_sessionHost.Admit(connectionId, admission, sessionId: null));
            Assert.IsTrue(_bindings.Players.TryGetByActorId(admission.Context.ActorId, out RbxPlayer player),
                "the world must have created the admitted player");
            return player;
        }

        /// <summary>The envelope <c>RemoteEvent:FireServer()</c> puts on the wire, with no arguments.</summary>
        private static CoreAiRemoteEventMessage ClientEvent(RbxRemoteEvent remote)
        {
            return new CoreAiRemoteEventMessage
            {
                RemoteId = remote.Id.Value,
                Direction = (byte)RbxNetworkDirection.ClientToServer,
                Reliability = (byte)remote.Reliability,
                Payload = Encoding.UTF8.GetBytes("[]")
            };
        }

        /// <summary>Issues one durable actor for every valid admission: the same player joining again.</summary>
        private sealed class DurableProvider : IActorAdmissionProvider
        {
            private readonly string _actorId;
            private int _sessions;

            public DurableProvider(string actorId)
            {
                _actorId = actorId;
            }

            public ActorAdmissionResult TryAdmit(in ActorCredential credential, string worldId)
            {
                _sessions++;
                ActorContext context = new LocalActorIdentityProvider(
                        _actorId,
                        "session-" + _sessions,
                        worldId,
                        ActorGrantSet.Create(new[] { "read" }),
                        AgentMemoryScope.Empty)
                    .GetActorContext(BuiltInAgentRoleIds.SmartChat);
                return ActorAdmissionResult.Admit(context, 4242L, "durable", "Durable");
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
