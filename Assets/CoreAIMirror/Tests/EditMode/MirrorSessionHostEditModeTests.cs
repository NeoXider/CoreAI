using System;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Mods.Rbx.Instances.Networking;
using NUnit.Framework;

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
    }
}
