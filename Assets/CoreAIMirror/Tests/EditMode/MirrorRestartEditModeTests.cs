using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.RegularExpressions;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using Mirror;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Net.Mirror.Tests
{
    /// <summary>
    /// The transport must survive Mirror stopping and starting again in one process with the SAME
    /// bridge: the world caches its bridge for its lifetime, and Mirror's Shutdown clears every
    /// handler that bridge registered without telling it.
    /// </summary>
    /// <remarks>
    /// WHY the provider is driven through its own Update rather than the bridge's re-registration
    /// being called by hand: the restart edge is detected in Update, beside the role-conflict guard
    /// and the disconnect hook that share the same Mirror lifecycle wart, so what is proven here is
    /// what one frame after a restart does. Nothing in this fixture names the re-registration
    /// method, so it compiles against the code without the fix and fails there at run time.
    /// </remarks>
    [TestFixture]
    public sealed class MirrorRestartEditModeTests
    {
        private const int Connection = 7;
        private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

        private OfflineMirror _mirror;
        private GameObject _go;
        private CoreAiMirrorNetworkBridgeProvider _provider;
        private double _now;

        [SetUp]
        public void CreateProvider()
        {
            _mirror = new OfflineMirror();
            _go = new GameObject("CoreAI_MirrorRestartTest");
            _provider = _go.AddComponent<CoreAiMirrorNetworkBridgeProvider>();
            _now = 0d;
            _provider.ClockSeconds = () => _now;
        }

        [TearDown]
        public void RestoreMirror()
        {
            OfflineMirror.RunAll(
                () => _provider.ReleaseTransport(),
                () => _mirror.Dispose(),
                () => UnityEngine.Object.DestroyImmediate(_go));
        }

        [Test]
        public void Server_TheSameBridgeReceivesAgainAfterMirrorStopsAndStarts()
        {
            MirrorNetworkBridge bridge = (MirrorNetworkBridge)_provider.Bridge;
            List<RbxNetworkEventMessage> delivered = new();
            bridge.EventReceived += delivered.Add;
            StartServerAndAdmit(bridge);
            OfflineMirror.DeliverToServer(Connection, OfflineMirror.Event(1UL));
            Assert.AreEqual(1, delivered.Count, "the first start must deliver before a restart is tested");

            OfflineMirror.StopServer();
            Frame();
            StartServerAndAdmit(bridge);
            OfflineMirror.DeliverToServer(Connection, OfflineMirror.Event(2UL));

            Assert.AreEqual(2, delivered.Count,
                "the bridge the world still holds went deaf across the restart");
            Assert.AreEqual(2UL, delivered[1].RemoteId.Value);
            Assert.AreEqual("actor-a", delivered[1].SenderActorId);
            Assert.AreEqual(2, bridge.PacketsDelivered);
        }

        [Test]
        public void Client_TheSameBridgeReceivesAgainAfterMirrorStopsAndStarts()
        {
            _provider.Role = CoreAiMirrorRole.Client;
            MirrorNetworkBridge bridge = (MirrorNetworkBridge)_provider.Bridge;
            List<RbxNetworkEventMessage> delivered = new();
            bridge.EventReceived += delivered.Add;
            StartClientAndAdmit(bridge);
            OfflineMirror.DeliverToClient(OfflineMirror.Event(1UL));
            Assert.AreEqual(1, delivered.Count, "the first connect must deliver before a restart is tested");

            OfflineMirror.StopClient();
            Frame();
            Assert.IsNull(bridge.AdmittedActorId, "the stop must forget who this client was");
            StartClientAndAdmit(bridge);
            OfflineMirror.DeliverToClient(OfflineMirror.Event(2UL));

            Assert.AreEqual(2, delivered.Count,
                "the client side has its own handler table and its own Shutdown that clears it");
            Assert.AreEqual(RbxNetworkDirection.ServerToClient, delivered[1].Direction);
            Assert.AreEqual("actor-a", delivered[1].RecipientActorId);
            Assert.AreEqual(2, bridge.PacketsDelivered);
        }

        [Test]
        public void Update_OnALiveServer_NeverDoublesDelivery()
        {
            MirrorNetworkBridge bridge = (MirrorNetworkBridge)_provider.Bridge;
            List<RbxNetworkEventMessage> delivered = new();
            bridge.EventReceived += delivered.Add;
            StartServerAndAdmit(bridge);
            Frame();
            Frame();

            OfflineMirror.DeliverToServer(Connection, OfflineMirror.Event(1UL));

            Assert.AreEqual(1, delivered.Count,
                "the constructor's registration plus every frame's re-check must still be one handler");
            Assert.AreEqual(1, bridge.PacketsDelivered);
        }

        [Test]
        public void Restart_ReattachesTheDisconnectHookExactlyOnce()
        {
            MirrorNetworkBridge bridge = (MirrorNetworkBridge)_provider.Bridge;
            List<RbxNetworkPeerDisconnected> disconnects = new();
            bridge.PeerDisconnected += disconnects.Add;
            StartServerAndAdmit(bridge);

            OfflineMirror.StopServer();
            Assert.AreEqual(1, disconnects.Count, "a stop must report every admitted peer as lost");
            Frame();
            StartServerAndAdmit(bridge);
            Frame();
            OfflineMirror.DropServerConnection(Connection);

            Assert.AreEqual(2, disconnects.Count,
                "after a restart the hook must be back, and back once");
            Assert.AreEqual(RbxNetworkDisconnectReason.TransportLost, disconnects[1].Reason);
        }

        [Test]
        public void HostMode_AServerBridgeServesTheLocalClient_AsTheHostsOwnPlayer_WithItsRemotesInProcess()
        {
            // WHY: this replaces the known limitation it was before MVP5 — a Mirror HOST (server and
            // local client in one process, the most common Mirror topology) had no client-side
            // remotes, and its own local client was refused as a player. The host's client is now
            // admitted through the real authenticator over Mirror's real local connection pair and is
            // the host's Player, whose remotes run in process both ways.
            CoreAiMirrorAuthenticator authenticator = _go.AddComponent<CoreAiMirrorAuthenticator>();
            authenticator.Configure(new HostCredentialProvider(), "world-a");
            authenticator.ConfigureClientCredential(() => Encoding.UTF8.GetBytes("host"));
            _provider.Authenticator = authenticator;
            List<string> connected = new();
            _provider.AttachWorld(context =>
            {
                connected.Add(context.ActorId);
                return true;
            }, _ => true);
            MirrorNetworkBridge bridge = (MirrorNetworkBridge)_provider.Bridge;
            List<RbxNetworkEventMessage> delivered = new();
            bridge.EventReceived += delivered.Add;
            bridge.RequestReceived += (_, responder) => responder.Complete(new byte[] { 42 });
            authenticator.OnServerAuthenticated.AddListener(conn => conn.isAuthenticated = true);
            authenticator.OnClientAuthenticated.AddListener(() => NetworkClient.connection.isAuthenticated = true);
            LocalConnectionToClient local = OfflineMirror.StartHost();
            authenticator.OnStartServer();
            authenticator.OnStartClient();
            authenticator.OnServerAuthenticate(local);
            authenticator.OnClientAuthenticate();
            OfflineMirror.PumpHost();
            Frame();
            using SentMessages<CoreAiRemoteEventMessage> eventsOnMirror = new();
            using SentMessages<CoreAiRemoteRequestMessage> requestsOnMirror = new();
            List<RbxNetworkResponse> answered = new();

            bridge.SendEvent(new RbxNetworkEventMessage(new InstanceId(5UL), RbxNetworkDirection.ServerToClient,
                RbxNetworkReliability.ReliableOrdered, null, "actor-host", new byte[] { 1 }));
            bridge.SendEvent(new RbxNetworkEventMessage(new InstanceId(5UL), RbxNetworkDirection.ClientToServer,
                RbxNetworkReliability.ReliableOrdered, "actor-host", null, new byte[] { 2 }));
            bridge.SendRequest(new RbxNetworkRequestMessage(new InstanceId(6UL), RbxNetworkDirection.ClientToServer,
                "actor-host", null, new byte[] { 3 }), answered.Add);
            OfflineMirror.PumpHost();
            Frame();

            CollectionAssert.AreEqual(new[] { "actor-host" }, connected, "the host's local client is the host's Player");
            Assert.AreEqual("actor-host", authenticator.ClientActorId,
                "the host's client half learned who it is from the real admission response");
            Assert.IsTrue(NetworkClient.connection.isAuthenticated);
            Assert.IsTrue(bridge.IsHostLocalConnection(local.connectionId));
            Assert.AreEqual(2, delivered.Count, "FireClient to the host's player and its FireServer both arrive");
            Assert.AreEqual(RbxNetworkDirection.ServerToClient, delivered[0].Direction);
            Assert.AreEqual("actor-host", delivered[0].RecipientActorId);
            Assert.AreEqual(RbxNetworkDirection.ClientToServer, delivered[1].Direction);
            Assert.AreEqual("actor-host", delivered[1].SenderActorId);
            Assert.AreEqual(1, answered.Count, "the host's InvokeServer is answered at once");
            Assert.IsTrue(answered[0].Succeeded);
            CollectionAssert.IsEmpty(eventsOnMirror.Messages, "nothing the host's player does touches Mirror");
            CollectionAssert.IsEmpty(requestsOnMirror.Messages);
            Assert.AreEqual(0, bridge.ReadinessTimeouts);
        }

        [Test]
        public void Negative_AServerBridge_WhoseProcessRestartsAsClient_SaysSoOnceAndStaysOffTheClientSide()
        {
            MirrorNetworkBridge bridge = (MirrorNetworkBridge)_provider.Bridge;
            StartServerAndAdmit(bridge);
            OfflineMirror.StopServer();
            Frame();
            OfflineMirror.StartClient();
            using LogCounter said = new("running as a client");
            LogAssert.Expect(LogType.Error, new Regex("configured as Server but Mirror is running as a client"));

            Frame();
            Frame();

            Assert.AreEqual(1, said.Count,
                "the conflict is said on the frame it appears, not on every frame it lasts");
            OfflineMirror.ExpectNoClientHandler();
            OfflineMirror.DeliverToClient(OfflineMirror.Event(1UL));
            Assert.AreEqual(0, bridge.PacketsDelivered,
                "the frames after the first must still not re-register on the wrong side");
        }

        [Test]
        public void RoleConflict_IsReCheckedEveryFrame_PumpsMeanwhile_AndHooksACorrectedSideBack()
        {
            MirrorNetworkBridge bridge = (MirrorNetworkBridge)_provider.Bridge;
            List<RbxNetworkEventMessage> delivered = new();
            bridge.EventReceived += delivered.Add;
            List<RbxNetworkResponse> completed = new();
            StartServerAndAdmit(bridge);
            OfflineMirror.StopServer();
            Frame();
            // WHY a bound peer whose connection Mirror does not hold: a request to a player with no
            // binding fails at once (A4-07), and one on a connection Mirror holds is failed by the
            // stop that reports it lost; only this one is left for the conflict frame's pump.
            bridge.BindConnection(55, new RbxNetworkPeer("actor-silent", "session-silent", "55"));
            bridge.SendRequest(
                new RbxNetworkRequestMessage(new InstanceId(3UL),
                    RbxNetworkDirection.ServerToClient, null, "actor-silent", Array.Empty<byte>()),
                completed.Add);
            Assert.IsEmpty(completed, "the request must still be open when the conflict begins");
            OfflineMirror.StartClient();
            LogAssert.Expect(LogType.Error, new Regex("running as a client"));
            Frame();
            _now = MirrorNetworkBridge.RequestTimeoutSeconds + 1d;

            Frame();

            Assert.AreEqual(1, completed.Count, "a conflict frame must still pump the timeouts");
            OfflineMirror.StopClient();
            StartServerAndAdmit(bridge);
            OfflineMirror.DeliverToServer(Connection, OfflineMirror.Event(1UL));
            Assert.AreEqual(1, delivered.Count,
                "once the side is corrected the same bridge must be back on the transport");
            OfflineMirror.StopServer();
            Frame();
            OfflineMirror.StartClient();
            LogAssert.Expect(LogType.Error, new Regex("running as a client"));
            Frame();
        }

        [Test]
        public void Negative_ABridgeDisposedByItsOwner_IsSaidOnceAndSpendsTheProvider()
        {
            MirrorNetworkBridge bridge = (MirrorNetworkBridge)_provider.Bridge;
            // WHY disposed by hand: this is what the container does to its singleton bridge at
            // scope teardown, with the provider still alive and updating.
            bridge.Dispose();
            OfflineMirror.StartServer();
            using LogCounter said = new("disposed by its owner");
            LogAssert.Expect(LogType.Warning, new Regex("disposed by its owner"));

            Frame();
            Frame();
            Frame();

            Assert.AreEqual(1, said.Count, "one line for the operator, not one per frame");
            Assert.IsFalse(_provider.HasBridge,
                "a bridge the container disposed is not one the provider can hand out again");
            Assert.Throws<ObjectDisposedException>(() => _ = _provider.Bridge);
        }

        [Test]
        public void Negative_AReleasedProvider_NeverReattachesAcrossARestart()
        {
            MirrorNetworkBridge bridge = (MirrorNetworkBridge)_provider.Bridge;
            StartServerAndAdmit(bridge);

            _provider.ReleaseTransport();
            OfflineMirror.StopServer();
            OfflineMirror.StartServer();
            OfflineMirror.AdmitServerConnection(Connection);
            Frame();
            OfflineMirror.ExpectNoServerHandler();
            OfflineMirror.DeliverToServer(Connection, OfflineMirror.Event(1UL));

            Assert.IsFalse(_provider.HasBridge);
            Assert.AreEqual(0, bridge.PacketsDelivered,
                "a disposed bridge must stay off the table however many times Mirror restarts");
        }

        [Test]
        public void Negative_AWorldWhoseDisconnectTeardownThrows_DoesNotEscapeTheTransportTick()
        {
            // WHY it matters: Mirror raises the disconnect event from inside the transport's receive
            // tick, and a throw out of there aborts that tick for every other connection this frame.
            _provider.AttachWorld(_ => true, _ => throw new InvalidOperationException("PlayerRemoving blew up"));
            MirrorNetworkBridge bridge = (MirrorNetworkBridge)_provider.Bridge;
            OfflineMirror.StartServer();
            OfflineMirror.AdmitServerConnection(Connection);
            Assert.IsTrue(_provider.SessionHost.Admit(Connection, AdmittedAs("actor-a"), sessionId: null));
            Frame();
            LogAssert.Expect(LogType.Exception, new Regex("PlayerRemoving blew up"));

            Assert.DoesNotThrow(() => OfflineMirror.DropServerConnection(Connection),
                "the world's teardown failure is logged, never thrown into the transport");

            Assert.AreEqual(0, _provider.SessionHost.LiveSessionCount);
            CollectionAssert.IsEmpty(bridge.ActorIds, "the connection is released even though the teardown threw");
        }

        [Test]
        public void AReassignedDisconnectEvent_IsHookedBackOnTheNextFrame()
        {
            MirrorNetworkBridge bridge = (MirrorNetworkBridge)_provider.Bridge;
            List<RbxNetworkPeerDisconnected> disconnects = new();
            bridge.PeerDisconnected += disconnects.Add;
            StartServerAndAdmit(bridge);
            // WHY an assignment: that is what NetworkManager itself does at StartServer, and what
            // any script copying it does — it silently removes every listener added before.
            NetworkServer.OnDisconnectedEvent = _ => { };
            LogAssert.Expect(LogType.Warning, new Regex("was reassigned"));

            Frame();
            OfflineMirror.DropServerConnection(Connection);

            Assert.AreEqual(1, disconnects.Count, "the provider's hook is back, so the lost peer leaves the world");
        }

        [Test]
        public void AThrowingDisconnectListenerAheadOfTheProvider_DoesNotSkipCoreAisTeardown()
        {
            MirrorNetworkBridge bridge = (MirrorNetworkBridge)_provider.Bridge;
            List<RbxNetworkPeerDisconnected> disconnects = new();
            bridge.PeerDisconnected += disconnects.Add;
            OfflineMirror.StartServer();
            // WHY assigned before the provider's frame: NetworkManager assigns its own handler at
            // StartServer, before the provider sees the server active, and a user's override of
            // OnServerDisconnect runs inside it.
            NetworkServer.OnDisconnectedEvent =
                _ => throw new InvalidOperationException("OnServerDisconnect override blew up");
            OfflineMirror.AdmitServerConnection(Connection);
            bridge.BindConnection(Connection, new RbxNetworkPeer("actor-a", "session-a", "conn-7"));
            Frame();

            Assert.Throws<InvalidOperationException>(() => OfflineMirror.DropServerConnection(Connection),
                "the other listener's throw is its own and still surfaces");

            Assert.AreEqual(1, disconnects.Count,
                "CoreAI's teardown ran first, so another listener's throw cannot leave a Player behind");
        }

        /// <summary>Admits any credential as the actor <c>actor-&lt;credential&gt;</c>.</summary>
        private sealed class HostCredentialProvider : IActorAdmissionProvider
        {
            public ActorAdmissionResult TryAdmit(in ActorCredential credential, string worldId)
            {
                string name = Encoding.UTF8.GetString(credential.Opaque);
                ActorContext context = new LocalActorIdentityProvider(
                        "actor-" + name, "session-" + name, worldId, ActorGrantSet.Create(new[] { "read" }),
                        AgentMemoryScope.Empty)
                    .GetActorContext(BuiltInAgentRoleIds.SmartChat);
                return ActorAdmissionResult.Admit(context, 1001L, name, name);
            }
        }

        private static ActorAdmissionResult AdmittedAs(string actorId)
        {
            ActorContext context = new LocalActorIdentityProvider(
                    actorId, "session-" + actorId, "world-a", ActorGrantSet.Create(new[] { "read" }),
                    AgentMemoryScope.Empty)
                .GetActorContext(BuiltInAgentRoleIds.SmartChat);
            return ActorAdmissionResult.Admit(context, 1001L, actorId, actorId);
        }

        private void StartServerAndAdmit(MirrorNetworkBridge bridge)
        {
            OfflineMirror.StartServer();
            OfflineMirror.AdmitServerConnection(Connection);
            bridge.BindConnection(Connection, new RbxNetworkPeer("actor-a", "session-a", "conn-7"));
            Frame();
        }

        /// <summary>Connects the client and stands in for the admission response that names it.</summary>
        private void StartClientAndAdmit(MirrorNetworkBridge bridge)
        {
            OfflineMirror.StartClient();
            Frame();
            bridge.BindAdmittedActor("actor-a");
        }

        /// <summary>One provider frame, with the reflection wrapper peeled off any exception.</summary>
        private void Frame()
        {
            MethodInfo update = typeof(CoreAiMirrorNetworkBridgeProvider).GetMethod(
                "Update", Private, null, Type.EmptyTypes, null);
            Assert.IsNotNull(update, "the provider must drive the bridge from Update()");
            try
            {
                update.Invoke(_provider, null);
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            }
        }

        /// <summary>Counts the lines Unity logged that contain one phrase, whatever their level.</summary>
        /// <remarks>
        /// WHY not LogAssert alone: an unexpected warning fails nothing, so "said once" is only
        /// provable for a warning by counting.
        /// </remarks>
        private sealed class LogCounter : IDisposable
        {
            private readonly string _phrase;

            public LogCounter(string phrase)
            {
                _phrase = phrase;
                Application.logMessageReceived += OnLog;
            }

            public int Count { get; private set; }

            public void Dispose()
            {
                Application.logMessageReceived -= OnLog;
            }

            private void OnLog(string condition, string stackTrace, LogType type)
            {
                if (condition.Contains(_phrase))
                {
                    Count++;
                }
            }
        }
    }
}
