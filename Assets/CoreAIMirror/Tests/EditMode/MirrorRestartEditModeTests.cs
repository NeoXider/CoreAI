using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
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
        public void KnownLimitation_HostMode_AServerBridgeDoesNotServeTheLocalClient()
        {
            MirrorNetworkBridge bridge = (MirrorNetworkBridge)_provider.Bridge;
            OfflineMirror.StartServer();
            OfflineMirror.StartClient();
            Frame();
            OfflineMirror.ExpectNoClientHandler();

            OfflineMirror.DeliverToClient(OfflineMirror.Event(1UL));

            Assert.AreEqual(0, bridge.PacketsDelivered,
                "KNOWN LIMITATION, not a contract: a server bridge installs server handlers only, and "
                + "the provider refuses a client bridge while the server is active, so a Mirror HOST - "
                + "server and local client in one process, the most common Mirror topology - has no "
                + "client-side remotes at all. This test pins today's behaviour so a change is noticed; "
                + "it does NOT say the behaviour is right. When host mode is fixed this test must be "
                + "rewritten, not kept green. Tracked in TODO.md under the MVP2.5 transport gaps.");
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
            bridge.SendRequest(
                new RbxNetworkRequestMessage(new InstanceId(3UL),
                    RbxNetworkDirection.ServerToClient, null, "actor-nobody", Array.Empty<byte>()),
                completed.Add);
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
