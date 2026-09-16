using System;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Mods.Rbx.Instances.Networking;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Net.Mirror.Tests
{
    /// <summary>
    /// MVP11 gate N11.2: the admitted identity is what the world gets, and a lost peer leaves nothing.
    /// </summary>
    [TestFixture]
    public sealed class MirrorSessionHostEditModeTests
    {
        private MirrorNetworkBridge _bridge;
        private List<string> _connected;
        private List<string> _disconnected;
        private CoreAiMirrorSessionHost _host;

        [SetUp]
        public void CreateHost()
        {
            _bridge = new MirrorNetworkBridge(isServer: true, clockSeconds: () => 0d);
            _connected = new List<string>();
            _disconnected = new List<string>();
            _host = new CoreAiMirrorSessionHost(
                _bridge,
                context =>
                {
                    _connected.Add(context.ActorId);
                    return true;
                },
                context =>
                {
                    _disconnected.Add(context.ActorId);
                    return true;
                });
        }

        [TearDown]
        public void DisposeHost()
        {
            _host.Dispose();
            _bridge.Dispose();
        }

        [Test]
        public void Admission_CreatesTheActorAndCarriesItsDurableIdentity()
        {
            ActorAdmissionResult admission = Admit("actor-a", 4242L, "neo", "Neo");

            Assert.IsTrue(_host.Admit(3, admission, "session-a"));

            CollectionAssert.AreEqual(new[] { "actor-a" }, _connected);
            Assert.AreEqual(1, _host.LiveSessionCount);
            Assert.IsTrue(_host.TryGetIdentity("actor-a", out long userId, out string username,
                out string displayName));
            Assert.AreEqual(4242L, userId,
                "the UserId a script saves by must be the admitted one, not a session counter");
            Assert.AreEqual("neo", username);
            Assert.AreEqual("Neo", displayName);
            CollectionAssert.Contains(_bridge.ActorIds, "actor-a");
        }

        [Test]
        public void Negative_ARefusedAdmission_CreatesNothing()
        {
            Assert.IsFalse(_host.Admit(3, ActorAdmissionResult.Reject("no"), "session-a"));

            Assert.IsEmpty(_connected, "a refused connection must reach no world state at all");
            Assert.AreEqual(0, _host.LiveSessionCount);
            CollectionAssert.IsEmpty(_bridge.ActorIds);
            Assert.IsFalse(_host.TryGetIdentity("actor-a", out _, out _, out _));
        }

        [Test]
        public void Negative_ANullAdmission_IsRefusedRatherThanCrashing()
        {
            Assert.IsFalse(_host.Admit(3, null, "session-a"));
            Assert.IsEmpty(_connected);
        }

        [Test]
        public void Negative_AWorldThatRefusesTheActor_LeavesNoBinding()
        {
            // Admission said yes and the world said no. The connection must not be left bound to a
            // player that does not exist — that binding is what the sender resolution trusts.
            CoreAiMirrorSessionHost refusing = new(_bridge, _ => false, _ => true);
            ActorAdmissionResult admission = Admit("actor-b", 7L, "b", "B");

            Assert.IsFalse(refusing.Admit(5, admission, "session-b"));

            Assert.AreEqual(0, refusing.LiveSessionCount);
            Assert.IsFalse(refusing.TryGetIdentity("actor-b", out _, out _, out _));
            refusing.Dispose();
        }

        [Test]
        public void PeerDisconnect_TearsTheActorDownExactlyOnce()
        {
            ActorAdmissionResult admission = Admit("actor-a", 1L, "a", "A");
            _host.Admit(3, admission, "session-a");

            _bridge.NotifyDisconnected(3, RbxNetworkDisconnectReason.TransportLost);
            _bridge.NotifyDisconnected(3, RbxNetworkDisconnectReason.TransportLost);

            CollectionAssert.AreEqual(new[] { "actor-a" }, _disconnected,
                "a second disconnect for the same peer must fire nothing");
            Assert.AreEqual(0, _host.LiveSessionCount);
            CollectionAssert.IsEmpty(_bridge.ActorIds);
        }

        [Test]
        public void Kick_ThroughTheBridge_ReleasesTheSessionOnce_AsServerClosed()
        {
            _host.Admit(3, Admit("actor-a", 1L, "a", "A"), "session-a");
            List<RbxNetworkPeerDisconnected> disconnects = new();
            _bridge.PeerDisconnected += disconnects.Add;

            _bridge.DisconnectActor("actor-a");
            // WHY a second report: the transport reports the drop too — on kcp2k inside the call
            // above, on another transport a pump later — and it must find nothing left to tear down.
            _bridge.NotifyDisconnected(3, RbxNetworkDisconnectReason.TransportLost);

            CollectionAssert.AreEqual(new[] { "actor-a" }, _disconnected,
                "the world is told once, by the kick, not again by the transport's report");
            Assert.AreEqual(0, _host.LiveSessionCount, "a kicked session must not linger in the host");
            Assert.IsFalse(_host.HasLiveSession(3));
            Assert.IsFalse(_host.TryGetIdentity("actor-a", out _, out _, out _));
            CollectionAssert.IsEmpty(_bridge.ActorIds);
            Assert.AreEqual(1, disconnects.Count);
            Assert.AreEqual(RbxNetworkDisconnectReason.ServerClosed, disconnects[0].Reason,
                "a kick is the server ending the connection, and the teardown must say so");
        }

        [Test]
        public void Negative_KickingAnActorWithNoConnection_DoesNothing()
        {
            _bridge.DisconnectActor("actor-nobody");
            _bridge.DisconnectActor("");

            Assert.IsEmpty(_disconnected);
            Assert.AreEqual(0, _host.LiveSessionCount);
        }

        [Test]
        public void Release_AfterTeardown_ForgetsTheIdentity()
        {
            // A reconnect must be admitted afresh rather than inheriting the previous session's
            // identity from a table nobody cleared.
            _host.Admit(3, Admit("actor-a", 1L, "a", "A"), "session-a");

            _host.Release(3);

            Assert.IsFalse(_host.TryGetIdentity("actor-a", out _, out _, out _));
            CollectionAssert.AreEqual(new[] { "actor-a" }, _disconnected);
        }

        [Test]
        public void Negative_UnknownConnection_ReleasesNothing()
        {
            _host.Release(999);

            Assert.IsEmpty(_disconnected);
        }

        [Test]
        public void Negative_AWorldThatThrowsOnDisconnect_StillReleasesTheConnection_SoAReusedIdIsNobody()
        {
            // WHY a real authenticator here: the departed actor lives on in two tables, the bridge's
            // and the authenticator's, and both are released through the same call that mod code
            // could interrupt.
            GameObject go = new("CoreAI_SessionHostAuthenticator");
            CoreAiMirrorAuthenticator authenticator = go.AddComponent<CoreAiMirrorAuthenticator>();
            authenticator.Configure(new AdmittingProvider(), "world-a");
            MirrorNetworkBridge bridge = new(isServer: true, authenticator, clockSeconds: () => 0d);
            CoreAiMirrorSessionHost throwing = new(bridge, _ => true,
                _ => throw new InvalidOperationException("PlayerRemoving blew up"));
            try
            {
                ActorAdmissionResult admission = authenticator.Decide(3, "127.0.0.1:3", new byte[] { 1 });
                Assert.IsTrue(throwing.Admit(3, admission, "session-a"));
                List<RbxNetworkEventMessage> delivered = new();
                bridge.EventReceived += delivered.Add;

                Assert.Throws<InvalidOperationException>(() => throwing.Release(3));

                Assert.AreEqual(0, throwing.LiveSessionCount);
                CollectionAssert.IsEmpty(bridge.ActorIds);
                Assert.IsNull(authenticator.ResultFor(3),
                    "the authenticator must not still hold the departed actor for an id kcp2k reuses");
                bridge.ReceiveServerEvent(3, OfflineMirror.Event(1UL));
                Assert.IsEmpty(delivered,
                    "the next connection on id 3 must be nobody until it is admitted itself");
                Assert.AreEqual(1, bridge.UnadmittedPacketsDropped);
            }
            finally
            {
                OfflineMirror.RunAll(
                    () => throwing.Dispose(),
                    () => bridge.Dispose(),
                    () => UnityEngine.Object.DestroyImmediate(go));
            }
        }

        [Test]
        public void Negative_AWorldThatThrowsOnConnect_LeavesNoBinding_SoTheConnectionIsNobody()
        {
            CoreAiMirrorSessionHost throwing = new(_bridge,
                _ => throw new InvalidOperationException("PlayerAdded blew up"), _ => true);
            List<RbxNetworkEventMessage> delivered = new();
            _bridge.EventReceived += delivered.Add;
            try
            {
                Assert.Throws<InvalidOperationException>(
                    () => throwing.Admit(5, Admit("actor-b", 7L, "b", "B"), "session-b"));

                Assert.AreEqual(0, throwing.LiveSessionCount);
                CollectionAssert.IsEmpty(_bridge.ActorIds);
                Assert.IsFalse(throwing.TryGetIdentity("actor-b", out _, out _, out _));
                _bridge.ReceiveServerEvent(5, OfflineMirror.Event(1UL));
                Assert.IsEmpty(delivered,
                    "a connection whose player was never created must resolve to nobody");
                Assert.AreEqual(1, _bridge.UnadmittedPacketsDropped);
            }
            finally
            {
                throwing.Dispose();
            }
        }

        private static ActorAdmissionResult Admit(string actorId, long userId, string name,
            string displayName)
        {
            ActorContext context = new LocalActorIdentityProvider(
                    actorId,
                    "session-" + actorId,
                    "world-a",
                    ActorGrantSet.Create(new[] { "read" }),
                    AgentMemoryScope.Empty)
                .GetActorContext(BuiltInAgentRoleIds.SmartChat);
            return ActorAdmissionResult.Admit(context, userId, name, displayName);
        }

        private sealed class AdmittingProvider : IActorAdmissionProvider
        {
            public ActorAdmissionResult TryAdmit(in ActorCredential credential, string worldId)
            {
                return Admit("actor-a", 1L, "a", "A");
            }
        }
    }
}
