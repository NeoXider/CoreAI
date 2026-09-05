using System;
using CoreAI.Ai;
using CoreAI.Authority;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Net.Mirror.Tests
{
    /// <summary>
    /// MVP11 gate N11.1: who the Mirror transport lets in, and what a refusal tells the world.
    /// </summary>
    /// <remarks>
    /// WHY these run without a socket: admission is the security boundary of the online rung, and
    /// the cases that matter are the failures — no provider configured, a provider that throws, a
    /// forged credential, a second connection reusing a rejected one. A gate that needs a live host
    /// and client to exercise those is a gate that runs rarely and never in CI.
    /// </remarks>
    [TestFixture]
    public sealed class MirrorAdmissionEditModeTests
    {
        private GameObject _host;
        private CoreAiMirrorAuthenticator _authenticator;

        [SetUp]
        public void CreateAuthenticator()
        {
            _host = new GameObject("CoreAI_MirrorAdmissionTest");
            _authenticator = _host.AddComponent<CoreAiMirrorAuthenticator>();
        }

        [TearDown]
        public void DestroyAuthenticator()
        {
            UnityEngine.Object.DestroyImmediate(_host);
        }

        [Test]
        public void ValidCredential_IsAdmittedAsARestrictedActor()
        {
            _authenticator.Configure(new TokenProvider("open-sesame"), "world-a");

            ActorAdmissionResult result = _authenticator.Decide(7, "127.0.0.1:7777",
                System.Text.Encoding.UTF8.GetBytes("open-sesame"));

            Assert.IsTrue(result.Admitted);
            Assert.AreEqual(1, _authenticator.AdmittedCount);
            Assert.AreEqual(0, _authenticator.RejectedCount);
            Assert.IsFalse(result.Context.Grants.IsUnrestricted,
                "a remote client never holds the host's own authority");
            Assert.AreSame(result, _authenticator.ResultFor(7));
        }

        [Test]
        public void Negative_WrongCredential_IsRefusedAndRecordsNothing()
        {
            _authenticator.Configure(new TokenProvider("open-sesame"), "world-a");

            ActorAdmissionResult result = _authenticator.Decide(7, "127.0.0.1:7777",
                System.Text.Encoding.UTF8.GetBytes("guess"));

            Assert.IsFalse(result.Admitted);
            Assert.AreEqual(0, _authenticator.AdmittedCount);
            Assert.AreEqual(1, _authenticator.RejectedCount);
            Assert.IsNull(_authenticator.ResultFor(7),
                "a refused connection must leave no record that could be mistaken for an admission");
        }

        [Test]
        public void Negative_NoProviderConfigured_RefusesEveryone()
        {
            // Decision 1, the whole point of the port: a host that forgot to configure admission
            // must be a closed server, not an open one.
            ActorAdmissionResult result = _authenticator.Decide(1, "127.0.0.1:1", new byte[] { 1 });

            Assert.IsFalse(result.Admitted);
            StringAssert.Contains("IActorAdmissionProvider", result.Reason);
        }

        [Test]
        public void Negative_AProviderThatThrows_RefusesInsteadOfAdmitting()
        {
            // Every bug in a host's own authentication would otherwise become an open door.
            _authenticator.Configure(new ThrowingProvider(), "world-a");

            ActorAdmissionResult result = _authenticator.Decide(2, "127.0.0.1:2", new byte[] { 1 });

            Assert.IsFalse(result.Admitted);
            StringAssert.Contains("threw", result.Reason);
            Assert.AreEqual(1, _authenticator.RejectedCount);
        }

        [Test]
        public void Negative_AProviderThatReturnsNothing_IsTreatedAsARefusal()
        {
            _authenticator.Configure(new NullProvider(), "world-a");

            ActorAdmissionResult result = _authenticator.Decide(3, "127.0.0.1:3", new byte[] { 1 });

            Assert.IsFalse(result.Admitted);
            Assert.AreEqual(1, _authenticator.RejectedCount);
        }

        [Test]
        public void Negative_AnEmptyCredential_ReachesTheProviderAsEmptyNotNull()
        {
            // A provider that has to null-check before it can refuse is a provider that will
            // eventually forget to.
            RecordingProvider provider = new();
            _authenticator.Configure(provider, "world-a");

            _authenticator.Decide(4, "127.0.0.1:4", null);

            Assert.IsNotNull(provider.LastCredential);
            Assert.AreEqual(0, provider.LastCredential.Length);
            Assert.AreEqual("127.0.0.1:4", provider.LastAddress);
            Assert.AreEqual("world-a", provider.LastWorldId,
                "the provider must be told which world is being joined");
        }

        [Test]
        public void Forget_ReleasesAConnectionsAdmission()
        {
            _authenticator.Configure(new TokenProvider("t"), "world-a");
            _authenticator.Decide(9, "127.0.0.1:9", System.Text.Encoding.UTF8.GetBytes("t"));
            Assert.IsNotNull(_authenticator.ResultFor(9));

            _authenticator.Forget(9);

            Assert.IsNull(_authenticator.ResultFor(9),
                "a reused connection id must not inherit the previous peer's admission");
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
                string offered = System.Text.Encoding.UTF8.GetString(credential.Opaque);
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

        private sealed class ThrowingProvider : IActorAdmissionProvider
        {
            public ActorAdmissionResult TryAdmit(in ActorCredential credential, string worldId)
            {
                throw new InvalidOperationException("the token service is down");
            }
        }

        private sealed class NullProvider : IActorAdmissionProvider
        {
            public ActorAdmissionResult TryAdmit(in ActorCredential credential, string worldId)
            {
                return null;
            }
        }

        private sealed class RecordingProvider : IActorAdmissionProvider
        {
            public byte[] LastCredential { get; private set; }

            public string LastAddress { get; private set; }

            public string LastWorldId { get; private set; }

            public ActorAdmissionResult TryAdmit(in ActorCredential credential, string worldId)
            {
                LastCredential = credential.Opaque;
                LastAddress = credential.TransportAddress;
                LastWorldId = worldId;
                return ActorAdmissionResult.Reject("recording only");
            }
        }
    }
}
