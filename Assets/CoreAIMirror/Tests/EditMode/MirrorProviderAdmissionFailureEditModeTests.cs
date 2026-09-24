using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Mods.Rbx.Instances.Networking;
using Mirror;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Net.Mirror.Tests
{
    /// <summary>
    /// What the provider's own accept listener does when the world cannot produce a player for a
    /// connection the authenticator admitted: Mirror's own listener still runs, on a connection
    /// that is still there, and the drop follows from the provider's next Update — so no peer is
    /// left authenticated to Mirror with nothing behind it, and no NetworkManager sees the
    /// connection leave before it arrived.
    /// </summary>
    /// <remarks>
    /// WHY the listener is driven through a real admission over the loopback and not by calling the
    /// session host: both defects are in the order UnityEvent runs listeners — the provider's, added
    /// in the mods scope's Awake, before the NetworkManager's, added at StartServer — and only the
    /// event chain shows that a throw out of the first skips the second, or that a drop made inside
    /// the first reaches the manager's disconnect before its connect. The stand-in here is added
    /// after the bridge is built, the order a scene gives, and records what it saw when it ran.
    /// WHY <c>exceptionsDisconnect</c> is off: with it on, Mirror answers a throw out of any handler
    /// by dropping the connection itself, and this fixture would pass for Mirror's reason instead of
    /// the provider's; off is the setting in which the departed throw left an authenticated nobody
    /// behind. WHY Update and OnDisable go through reflection: Unity runs neither for a plain
    /// component in edit mode, and the provider's frame is the thing under test.
    /// </remarks>
    [TestFixture]
    public sealed class MirrorProviderAdmissionFailureEditModeTests
    {
        private const string Credential = "open-sesame";
        private const string WorldId = "world-a";
        private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

        private OfflineMirror _mirror;
        private GameObject _go;
        private CoreAiMirrorAuthenticator _authenticator;
        private CoreAiMirrorNetworkBridgeProvider _provider;
        private bool _previousExceptionsDisconnect;
        private int _managerListenerRan;
        private bool _managerSawTheConnectionLive;
        private int _dropsRequestedWhenManagerRan;
        private NetworkConnectionToClient _accepted;

        [SetUp]
        public void CreateProviderAndBothSides()
        {
            _previousExceptionsDisconnect = NetworkServer.exceptionsDisconnect;
            NetworkServer.exceptionsDisconnect = false;
            _managerListenerRan = 0;
            _managerSawTheConnectionLive = false;
            _dropsRequestedWhenManagerRan = -1;
            _accepted = null;
            _mirror = new OfflineMirror(loopback: true);
            _go = new GameObject("CoreAI_ProviderAdmissionFailure");
            _authenticator = _go.AddComponent<CoreAiMirrorAuthenticator>();
            _authenticator.Configure(new TokenProvider(Credential), WorldId);
            _authenticator.ConfigureClientCredential(() => Encoding.UTF8.GetBytes(Credential));
            _provider = _go.AddComponent<CoreAiMirrorNetworkBridgeProvider>();
            _provider.ClockSeconds = () => 0d;
            SetField(_provider, "authenticator", _authenticator);
        }

        [TearDown]
        public void RestoreEverything()
        {
            OfflineMirror.RunAll(
                () => NetworkServer.exceptionsDisconnect = _previousExceptionsDisconnect,
                () => _provider.ReleaseTransport(),
                () => _mirror.Dispose(),
                () => UnityEngine.Object.DestroyImmediate(_go));
        }

        [Test]
        public void Negative_AWorldThatThrowsOnAdmission_HasItsConnectionDropped_AfterMirrorsOwnListener_NotBefore()
        {
            _provider.AttachWorld(
                _ => throw new InvalidOperationException("PlayerAdded blew up"),
                _ => true);
            MirrorNetworkBridge bridge = BuildBridgeThenStandInForTheNetworkManager();
            LogAssert.Expect(LogType.Error, new Regex(
                "the world threw while admitting connection " + OfflineMirror.LoopbackConnectionId));

            Join();

            AssertTheAcceptChainRanWhole_AndTheDropIsStillOwed(bridge);
            Assert.IsNull(_authenticator.ResultFor(OfflineMirror.LoopbackConnectionId),
                "the released admission must not linger for a reused id");

            InvokeUpdate(_provider);

            AssertTheDropWasPerformedOnce();
        }

        [Test]
        public void Negative_AWorldThatRefusesTheActor_HasItsConnectionDropped_Too()
        {
            _provider.AttachWorld(_ => false, _ => true);
            MirrorNetworkBridge bridge = BuildBridgeThenStandInForTheNetworkManager();

            Join();

            AssertTheAcceptChainRanWhole_AndTheDropIsStillOwed(bridge);

            InvokeUpdate(_provider);

            AssertTheDropWasPerformedOnce();
        }

        [Test]
        public void A4_10_HostMode_TheHostsOwnLocalClient_IsRefusedAsAPlayerLoudly_AndLeftOnMirror()
        {
            // WHY: in Mirror host mode the host's own client connection was admitted as a remote
            // player, never acknowledged readiness — no CoreAI client bridge sits beside a server
            // one — and was dropped from its own host ten seconds later with a line blaming an old
            // client (A4-10).
            double now = 0d;
            _provider.ClockSeconds = () => now;
            List<string> connected = new();
            _provider.AttachWorld(context =>
            {
                connected.Add(context.ActorId);
                return true;
            }, _ => true);
            MirrorNetworkBridge bridge = (MirrorNetworkBridge)_provider.Bridge;
            OfflineMirror.StartServer();
            _authenticator.OnStartServer();
            LocalConnectionToClient local = new() { isAuthenticated = true };
            NetworkServer.AddConnection(local);
            try
            {
                Assert.IsTrue(_authenticator.Decide(local.connectionId, "localhost",
                    Encoding.UTF8.GetBytes(Credential)).Admitted);
                LogAssert.Expect(LogType.Error, new Regex("host mode is not supported"));

                _authenticator.OnServerAuthenticated.Invoke(local);
                InvokeUpdate(_provider);
                now = MirrorNetworkBridge.ReadinessTimeoutSeconds + 1d;
                InvokeUpdate(_provider);

                CollectionAssert.IsEmpty(connected, "the host's own client is no world player");
                Assert.AreEqual(1, _provider.SessionHost.HostModeConnectionsRefused);
                Assert.AreEqual(0, _provider.SessionHost.LiveSessionCount);
                CollectionAssert.IsEmpty(bridge.ActorIds);
                Assert.AreEqual(0, bridge.ReadinessTimeouts, "nothing waits for a readiness it cannot send");
                Assert.IsTrue(NetworkServer.connections.TryGetValue(local.connectionId,
                        out NetworkConnectionToClient still) && ReferenceEquals(still, local),
                    "the host's local client is left to Mirror, not dropped");
                CollectionAssert.IsEmpty(_mirror.ServerDisconnectRequests);
            }
            finally
            {
                // WHY removed by hand: Mirror's shutdown disconnects every connection, and a local one
                // with no local client behind it has nothing to disconnect.
                NetworkServer.RemoveConnection(local.connectionId);
            }
        }

        [Test]
        public void AConnectionThatLeftBeforeTheDrop_IsNotTouched_NorIsAStrangerNowOnItsId()
        {
            _provider.AttachWorld(_ => false, _ => true);
            BuildBridgeThenStandInForTheNetworkManager();
            Join();
            CollectionAssert.IsEmpty(_mirror.ServerDisconnectRequests, "the drop is owed, not yet made");

            NetworkClient.Disconnect();
            _mirror.PumpLoopback();
            CollectionAssert.IsEmpty(NetworkServer.connections,
                "the client hung up on its own and the server learned it from the transport");
            // WHY a second connect on the same id: the loopback has one id, the way kcp2k reuses
            // them, so the stranger who arrives next sits exactly where the dropped connection was.
            // WHY the client side is stopped first: that is a NetworkManager's client stop, and
            // Mirror's own handlers are registered afresh on a table it cleared.
            OfflineMirror.StopClient();
            _mirror.ConnectLoopback();
            NetworkConnectionToClient stranger =
                NetworkServer.connections[OfflineMirror.LoopbackConnectionId];
            Assert.AreNotSame(_accepted, stranger, "the harness must have given the id to a new connection");

            InvokeUpdate(_provider);

            CollectionAssert.IsEmpty(_mirror.ServerDisconnectRequests,
                "a connection that left on its own owes nothing, and the stranger on its id owes "
                + "nothing either: the provider must ask the transport for no drop at all");
            Assert.AreSame(stranger, NetworkServer.connections[OfflineMirror.LoopbackConnectionId],
                "the stranger must still be connected");
            Assert.IsTrue(NetworkClient.isConnected);
        }

        [Test]
        public void Negative_NoPendingDropSurvivesDisablingTheProvider()
        {
            _provider.AttachWorld(_ => false, _ => true);
            BuildBridgeThenStandInForTheNetworkManager();
            Join();
            CollectionAssert.IsEmpty(_mirror.ServerDisconnectRequests, "the drop is owed, not yet made");

            InvokePrivate(_provider, "OnDisable");

            CollectionAssert.AreEqual(new[] { OfflineMirror.LoopbackConnectionId },
                _mirror.ServerDisconnectRequests,
                "a disabled provider runs no Update, so the drop it owes must be made as it goes off");
            CollectionAssert.IsEmpty(NetworkServer.connections);

            InvokeUpdate(_provider);

            Assert.AreEqual(1, _mirror.ServerDisconnectRequests.Count,
                "the drop was made once; nothing remained for the next frame");
        }

        [Test]
        public void Negative_NoPendingDropSurvivesReleasingTheTransport()
        {
            _provider.AttachWorld(_ => false, _ => true);
            BuildBridgeThenStandInForTheNetworkManager();
            Join();
            CollectionAssert.IsEmpty(_mirror.ServerDisconnectRequests, "the drop is owed, not yet made");

            _provider.ReleaseTransport();

            CollectionAssert.AreEqual(new[] { OfflineMirror.LoopbackConnectionId },
                _mirror.ServerDisconnectRequests,
                "the teardown that spends the provider must first pay the drop it owes: with "
                + "domain reload off, nothing else ever would");
            CollectionAssert.IsEmpty(NetworkServer.connections);
        }

        /// <summary>
        /// Right after the accept event: Mirror's own listener ran, on a connection that was still
        /// in the server's table and had not been asked to leave; the connection is now
        /// authenticated to Mirror and to the client, holds no player, and is owed a drop.
        /// </summary>
        private void AssertTheAcceptChainRanWhole_AndTheDropIsStillOwed(MirrorNetworkBridge bridge)
        {
            Assert.AreEqual(1, _managerListenerRan,
                "the failure must no longer abort the accept event before Mirror's own listener");
            Assert.IsTrue(_managerSawTheConnectionLive,
                "Mirror's own listener must run OnServerConnect on a connection the server still "
                + "holds, not on one already removed by a drop made inside the accept event");
            Assert.AreEqual(0, _dropsRequestedWhenManagerRan,
                "no drop may reach the transport before Mirror's own listener has run");
            CollectionAssert.IsEmpty(_mirror.ServerDisconnectRequests,
                "right after the accept event the drop is owed, not made");
            Assert.IsTrue(NetworkServer.connections.TryGetValue(OfflineMirror.LoopbackConnectionId,
                    out NetworkConnectionToClient live) && ReferenceEquals(live, _accepted),
                "the accepted connection is still the server's until the provider's next frame");
            Assert.IsTrue(_accepted.isAuthenticated, "Mirror's own listener marked it authenticated");
            Assert.IsTrue(NetworkClient.isConnected);
            Assert.AreEqual(0, _provider.SessionHost.LiveSessionCount);
            CollectionAssert.IsEmpty(bridge.ActorIds);
        }

        /// <summary>
        /// After the provider's frame: the transport was asked once, the server holds nothing for
        /// the connection, the client learns of the drop from the transport, and a further frame
        /// asks for nothing more.
        /// </summary>
        private void AssertTheDropWasPerformedOnce()
        {
            CollectionAssert.AreEqual(new[] { OfflineMirror.LoopbackConnectionId },
                _mirror.ServerDisconnectRequests,
                "the provider must ask the transport to drop the connection the world could not "
                + "admit, from its next frame");
            CollectionAssert.IsEmpty(NetworkServer.connections,
                "once the transport reports the drop the server holds nothing for it");
            Assert.AreEqual(0, _provider.SessionHost.LiveSessionCount);

            _mirror.PumpLoopback();
            Assert.IsFalse(NetworkClient.isConnected,
                "the client learns of the drop from the transport rather than staying admitted");

            InvokeUpdate(_provider);
            Assert.AreEqual(1, _mirror.ServerDisconnectRequests.Count,
                "the drop is made exactly once; a later frame owes nothing");
        }

        /// <summary>
        /// Builds the bridge, which adds the provider's listener, and only then adds the one the
        /// NetworkManager adds at StartServer: marking the connection authenticated — recording,
        /// as it does, whether the connection was still the server's and still undropped.
        /// </summary>
        private MirrorNetworkBridge BuildBridgeThenStandInForTheNetworkManager()
        {
            MirrorNetworkBridge bridge = (MirrorNetworkBridge)_provider.Bridge;
            _authenticator.OnServerAuthenticated.AddListener(conn =>
            {
                conn.isAuthenticated = true;
                _managerListenerRan++;
                _accepted = conn;
                _managerSawTheConnectionLive =
                    NetworkServer.connections.TryGetValue(conn.connectionId,
                        out NetworkConnectionToClient live)
                    && ReferenceEquals(live, conn);
                _dropsRequestedWhenManagerRan = _mirror.ServerDisconnectRequests.Count;
            });
            return bridge;
        }

        /// <summary>Runs the whole admission exchange over the loopback, both sides real.</summary>
        private void Join()
        {
            OfflineMirror.StartServer();
            _authenticator.OnStartServer();
            _mirror.ConnectLoopback();
            _authenticator.OnStartClient();
            _authenticator.OnClientAuthenticate();
            _mirror.PumpLoopback();
        }

        private static void InvokeUpdate(CoreAiMirrorNetworkBridgeProvider provider)
        {
            InvokePrivate(provider, "Update");
        }

        private static void InvokePrivate(CoreAiMirrorNetworkBridgeProvider provider, string name)
        {
            MethodInfo method = typeof(CoreAiMirrorNetworkBridgeProvider).GetMethod(
                name, Private, null, Type.EmptyTypes, null);
            Assert.IsNotNull(method, "the provider must have a private " + name + "()");
            method.Invoke(provider, null);
        }

        private static void SetField(object target, string name, object value)
        {
            FieldInfo field = target.GetType().GetField(name, Private);
            Assert.IsNotNull(field, name);
            field.SetValue(target, value);
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
