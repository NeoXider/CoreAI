using System;
using System.Collections.Generic;
using System.Text;
using CoreAI.Ai;
using CoreAI.Authority;
using Mirror;
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

        [Test]
        public void ASecondAdmissionRequestOnOneConnection_IsIgnored_AndTheProviderIsAskedOnce()
        {
            // WHY through the real message handler: the once-per-connection rule lives where a
            // connection asks. Every accept raises OnServerAuthenticated again, so a connection
            // that could ask twice would mint a Player per request and call the provider at will.
            TokenProvider provider = new("open-sesame");
            _authenticator.Configure(provider, "world-a");
            List<int> accepted = new();
            _authenticator.OnServerAuthenticated.AddListener(conn =>
            {
                conn.isAuthenticated = true;
                accepted.Add(conn.connectionId);
            });
            OfflineMirror mirror = new();
            try
            {
                StartServer();
                OfflineMirror.OpenServerConnection(7);

                RequestAdmission(7, "open-sesame");
                RequestAdmission(7, "open-sesame");

                Assert.AreEqual(1, provider.Calls, "the host's provider is asked once per connection");
                CollectionAssert.AreEqual(new[] { 7 }, accepted, "one connection is accepted once");
                Assert.AreEqual(1, _authenticator.AdmittedCount);
                Assert.AreEqual(1, _authenticator.IgnoredAdmissionRequests);
                Assert.AreEqual("remote-1", _authenticator.ResultFor(7).Context.ActorId,
                    "the first admission stands; the repeat changed nothing");
            }
            finally
            {
                mirror.Dispose();
            }
        }

        [Test]
        public void Negative_ARepeatThatWouldBeRefused_DoesNotTearDownTheAdmittedSession()
        {
            TokenProvider provider = new("open-sesame");
            _authenticator.Configure(provider, "world-a");
            _authenticator.OnServerAuthenticated.AddListener(conn => conn.isAuthenticated = true);
            OfflineMirror mirror = new();
            try
            {
                StartServer();
                OfflineMirror.OpenServerConnection(7);
                RequestAdmission(7, "open-sesame");

                RequestAdmission(7, "guess");

                CollectionAssert.IsEmpty(mirror.ServerDisconnectRequests,
                    "the policy is pinned: a repeat is ignored, so it cannot disconnect the session it repeats");
                Assert.AreEqual(0, _authenticator.RejectedCount);
                Assert.AreEqual(1, provider.Calls);
                Assert.IsNotNull(_authenticator.ResultFor(7));
            }
            finally
            {
                mirror.Dispose();
            }
        }

        [Test]
        public void Negative_ARefusedConnection_CannotRetryBeforeItsDropTakesEffect()
        {
            // WHY: a refusal disconnects, but a transport may report the drop later than the next
            // packet; a second credential in that window must not reach the provider.
            TokenProvider provider = new("open-sesame");
            _authenticator.Configure(provider, "world-a");
            OfflineMirror mirror = new();
            try
            {
                StartServer();
                OfflineMirror.OpenServerConnection(8);

                RequestAdmission(8, "guess");
                RequestAdmission(8, "open-sesame");

                Assert.AreEqual(0, _authenticator.AdmittedCount, "the retry admitted nobody");
                Assert.AreEqual(1, provider.Calls);
                Assert.AreEqual(1, _authenticator.IgnoredAdmissionRequests);
                CollectionAssert.AreEqual(new[] { 8 }, mirror.ServerDisconnectRequests,
                    "the refused connection is dropped once, for its one attempt");
            }
            finally
            {
                mirror.Dispose();
            }
        }

        [Test]
        public void ANewConnectionOnAReusedId_GetsItsOwnAttempt()
        {
            TokenProvider provider = new("open-sesame");
            _authenticator.Configure(provider, "world-a");
            OfflineMirror mirror = new();
            try
            {
                StartServer();
                OfflineMirror.OpenServerConnection(8);
                RequestAdmission(8, "guess");
                OfflineMirror.DropServerConnection(8);
                // WHY no Forget: nothing is hooked to the drop here, so the departed attempt is
                // still on record — and a new connection object must not inherit it.
                OfflineMirror.OpenServerConnection(8);

                RequestAdmission(8, "open-sesame");

                Assert.AreEqual(1, _authenticator.AdmittedCount,
                    "kcp2k reuses ids; the next peer on this one is someone new with an attempt of its own");
                Assert.AreEqual(0, _authenticator.IgnoredAdmissionRequests);
            }
            finally
            {
                mirror.Dispose();
            }
        }

        [Test]
        public void AConnectionThatSendsNothing_IsDroppedAtTheAdmissionDeadline_AndNotBefore()
        {
            double now = 0d;
            _authenticator.ClockSeconds = () => now;
            _authenticator.Configure(new TokenProvider("open-sesame"), "world-a");
            OfflineMirror mirror = new();
            try
            {
                StartServer();
                _authenticator.OnServerAuthenticate(OfflineMirror.OpenServerConnection(7));

                now = 9.9d;
                _authenticator.EnforceAdmissionDeadline();
                CollectionAssert.IsEmpty(mirror.ServerDisconnectRequests, "the deadline is 10 seconds, not 9.9");

                now = 10d;
                _authenticator.EnforceAdmissionDeadline();
                CollectionAssert.AreEqual(new[] { 7 }, mirror.ServerDisconnectRequests,
                    "Mirror has no authentication timeout: without this the silent peer keeps its slot for ever");
                Assert.AreEqual(1, _authenticator.AdmissionTimeouts);

                now = 30d;
                _authenticator.EnforceAdmissionDeadline();
                RequestAdmission(7, "open-sesame");
                Assert.AreEqual(1, mirror.ServerDisconnectRequests.Count, "the drop is made once");
                Assert.AreEqual(0, _authenticator.AdmittedCount,
                    "a request arriving after the deadline admits nobody");
            }
            finally
            {
                mirror.Dispose();
            }
        }

        [Test]
        public void AConnectionAdmittedBeforeTheDeadline_IsNotTouched()
        {
            double now = 0d;
            _authenticator.ClockSeconds = () => now;
            _authenticator.Configure(new TokenProvider("open-sesame"), "world-a");
            _authenticator.OnServerAuthenticated.AddListener(conn => conn.isAuthenticated = true);
            OfflineMirror mirror = new();
            try
            {
                StartServer();
                _authenticator.OnServerAuthenticate(OfflineMirror.OpenServerConnection(7));
                now = 3d;
                RequestAdmission(7, "open-sesame");

                now = 100d;
                _authenticator.EnforceAdmissionDeadline();

                CollectionAssert.IsEmpty(mirror.ServerDisconnectRequests);
                Assert.AreEqual(0, _authenticator.AdmissionTimeouts);
                Assert.IsNotNull(_authenticator.ResultFor(7));
            }
            finally
            {
                mirror.Dispose();
            }
        }

        [Test]
        public void AConnectionThatLeftOrWasForgotten_DuringAdmission_DoesNotTroubleTheDeadline()
        {
            double now = 0d;
            _authenticator.ClockSeconds = () => now;
            _authenticator.Configure(new TokenProvider("open-sesame"), "world-a");
            OfflineMirror mirror = new();
            try
            {
                StartServer();
                _authenticator.OnServerAuthenticate(OfflineMirror.OpenServerConnection(7));
                _authenticator.OnServerAuthenticate(OfflineMirror.OpenServerConnection(8));
                _authenticator.Forget(7);
                OfflineMirror.DropServerConnection(8);

                now = 100d;
                Assert.DoesNotThrow(() => _authenticator.EnforceAdmissionDeadline());
                Assert.DoesNotThrow(() => _authenticator.EnforceAdmissionDeadline());

                CollectionAssert.IsEmpty(mirror.ServerDisconnectRequests,
                    "a forgotten record is not enforced, and a connection that is gone is not dropped again");
                Assert.AreEqual(0, _authenticator.AdmissionTimeouts);
            }
            finally
            {
                mirror.Dispose();
            }
        }

        [Test]
        public void AdmissionTimeout_DefaultsToTenSeconds_AndRefusesADeadlineThatNeverComes()
        {
            Assert.AreEqual(10f, _authenticator.AdmissionTimeoutSeconds);

            Assert.Throws<ArgumentOutOfRangeException>(() => _authenticator.AdmissionTimeoutSeconds = 0f);
            Assert.Throws<ArgumentOutOfRangeException>(() => _authenticator.AdmissionTimeoutSeconds = -1f);
            Assert.Throws<ArgumentOutOfRangeException>(() => _authenticator.AdmissionTimeoutSeconds = float.NaN);
            Assert.Throws<ArgumentOutOfRangeException>(
                () => _authenticator.AdmissionTimeoutSeconds = float.PositiveInfinity);
            _authenticator.AdmissionTimeoutSeconds = 2.5f;
            Assert.AreEqual(2.5f, _authenticator.AdmissionTimeoutSeconds);
        }

        private void StartServer()
        {
            OfflineMirror.StartServer();
            _authenticator.OnStartServer();
        }

        private static void RequestAdmission(int connectionId, string credential)
        {
            OfflineMirror.DeliverToServer(connectionId, new CoreAiAdmissionRequestMessage
            {
                Credential = Encoding.UTF8.GetBytes(credential)
            });
        }

        private sealed class TokenProvider : IActorAdmissionProvider
        {
            private readonly string _expected;
            private int _issued;

            public TokenProvider(string expected)
            {
                _expected = expected;
            }

            public int Calls { get; private set; }

            public ActorAdmissionResult TryAdmit(in ActorCredential credential, string worldId)
            {
                Calls++;
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
