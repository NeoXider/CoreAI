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
    /// The protocol handshake: the admission request names the client's CoreAI Mirror protocol and
    /// the response the server's, a peer of another protocol is refused with a reason that names
    /// both, and that reason reaches the refused side before its connection closes.
    /// </summary>
    /// <remarks>
    /// WHY the older and newer peers are written byte by byte: this build's serializers can only
    /// write this build's layout. An older client's request ends after its credential — the layout
    /// of CoreAI 7.56.0 — and a newer one carries a higher protocol and fields this build has never
    /// heard of; both must be read, not thrown on, or Mirror drops the peer without a reason.
    /// </remarks>
    [TestFixture]
    public sealed class MirrorProtocolVersionEditModeTests
    {
        private const string Credential = "open-sesame";
        private const int Connection = 8;

        private OfflineMirror _mirror;
        private GameObject _go;
        private CoreAiMirrorAuthenticator _authenticator;
        private CountingProvider _provider;
        private List<string> _hostLog;
        private double _now;

        [SetUp]
        public void CreateAuthenticator()
        {
            _mirror = new OfflineMirror();
            _now = 0d;
            _go = new GameObject("CoreAI_MirrorProtocol");
            _authenticator = _go.AddComponent<CoreAiMirrorAuthenticator>();
            _authenticator.ClockSeconds = () => _now;
            _provider = new CountingProvider();
            _hostLog = new List<string>();
            _authenticator.Configure(_provider, "world-a", _hostLog.Add);
        }

        [TearDown]
        public void RestoreMirror()
        {
            OfflineMirror.RunAll(
                () => _mirror.Dispose(),
                () => UnityEngine.Object.DestroyImmediate(_go));
        }

        [Test]
        public void TheWeaverUsesTheTolerantAdmissionReaders()
        {
            // WHY: if Mirror's weaver generated its own readers instead, an older client would be
            // dropped without a reason, and every test below written against the wire would fail
            // in a way that points nowhere near the cause.
            Assert.IsTrue(CoreAiMirrorAdmissionSerialization.TolerantReadersRegistered(),
                "Mirror's codec table must hold CoreAiMirrorAdmissionSerialization's readers");
        }

        [Test]
        public void ASameProtocolClient_IsAdmitted_AndItsRequestCarriesTheProtocol()
        {
            StartServer();
            OfflineMirror.OpenServerConnection(Connection);
            using SentMessages<CoreAiAdmissionResponseMessage> told = new();

            OfflineMirror.DeliverToServer(Connection, new CoreAiAdmissionRequestMessage
            {
                Credential = Encoding.UTF8.GetBytes(Credential),
                ProtocolVersion = CoreAiMirrorProtocol.Version
            });

            Assert.AreEqual(1, _authenticator.AdmittedCount);
            Assert.AreEqual(0, _authenticator.ProtocolMismatchRefusals);
            Assert.AreEqual(1, _provider.Calls);
            Assert.IsTrue(told.Messages[0].Admitted);
            Assert.AreEqual(CoreAiMirrorProtocol.Version, told.Messages[0].ProtocolVersion,
                "the server names its own protocol to the client it admits");
            Assert.AreEqual((byte)CoreAiAdmissionRefusalKind.None, told.Messages[0].Refusal);
        }

        [Test]
        public void Negative_AnOlderClient_WhoseRequestNamesNoProtocol_IsRefusedWithBothProtocols_BeforeTheProviderIsAsked()
        {
            StartServer();
            OfflineMirror.OpenServerConnection(Connection);
            using SentMessages<CoreAiAdmissionResponseMessage> told = new();

            OfflineMirror.DeliverRawToServer(Connection, NetworkMessageId<CoreAiAdmissionRequestMessage>.Id,
                writer => writer.WriteBytesAndSize(Encoding.UTF8.GetBytes(Credential)));

            AssertRefusedForTheProtocol(told, CoreAiMirrorProtocol.Unversioned);
            StringAssert.Contains("7.56.0 or older", told.Messages[0].Reason,
                "the player is told the other side predates the handshake");
        }

        [Test]
        public void Negative_ANewerClient_WithAHigherProtocolAndFieldsThisBuildDoesNotKnow_IsRefusedWithBothProtocols()
        {
            StartServer();
            OfflineMirror.OpenServerConnection(Connection);
            using SentMessages<CoreAiAdmissionResponseMessage> told = new();

            OfflineMirror.DeliverRawToServer(Connection, NetworkMessageId<CoreAiAdmissionRequestMessage>.Id,
                writer =>
                {
                    writer.WriteBytesAndSize(Encoding.UTF8.GetBytes(Credential));
                    writer.WriteVarInt(CoreAiMirrorProtocol.Version + 1);
                    writer.WriteString("a field from a later protocol");
                    writer.WriteVarULong(42UL);
                });

            AssertRefusedForTheProtocol(told, CoreAiMirrorProtocol.Version + 1);
        }

        [Test]
        public void Negative_AProtocolRefusal_UsesTheConnectionsOneAttempt()
        {
            StartServer();
            OfflineMirror.OpenServerConnection(Connection);
            OfflineMirror.DeliverRawToServer(Connection, NetworkMessageId<CoreAiAdmissionRequestMessage>.Id,
                writer => writer.WriteBytesAndSize(Encoding.UTF8.GetBytes(Credential)));

            OfflineMirror.DeliverToServer(Connection, new CoreAiAdmissionRequestMessage
            {
                Credential = Encoding.UTF8.GetBytes(Credential),
                ProtocolVersion = CoreAiMirrorProtocol.Version
            });

            Assert.AreEqual(0, _authenticator.AdmittedCount,
                "a peer refused for its protocol cannot retry as the current one on the same connection");
            Assert.AreEqual(1, _authenticator.IgnoredAdmissionRequests);
            Assert.AreEqual(0, _provider.Calls);
        }

        [Test]
        public void TheClient_KeepsTheServersProtocolReason_AndClosesItsConnection()
        {
            StartClient();
            List<CoreAiAdmissionRefusal> raised = new();
            _authenticator.ClientRefused += raised.Add;
            string reason = CoreAiMirrorProtocol.MismatchReason(CoreAiMirrorProtocol.Version + 1,
                CoreAiMirrorProtocol.Version, localIsServer: true);

            OfflineMirror.DeliverToClient(new CoreAiAdmissionResponseMessage
            {
                Admitted = false,
                Reason = reason,
                ActorId = "",
                ProtocolVersion = CoreAiMirrorProtocol.Version + 1,
                Refusal = (byte)CoreAiAdmissionRefusalKind.ProtocolMismatch
            });

            Assert.IsTrue(_authenticator.ClientRefusal.HasValue);
            CoreAiAdmissionRefusal refusal = _authenticator.ClientRefusal.Value;
            Assert.AreEqual(CoreAiAdmissionRefusalKind.ProtocolMismatch, refusal.Kind);
            Assert.AreEqual(reason, refusal.Reason, "the player is shown the server's own words");
            Assert.AreEqual(CoreAiMirrorProtocol.Version + 1, refusal.ServerProtocolVersion);
            Assert.AreEqual(CoreAiMirrorProtocol.Version, refusal.ClientProtocolVersion);
            Assert.IsTrue(refusal.RefusedByServer);
            Assert.AreEqual(1, raised.Count);
            Assert.IsNull(_authenticator.ClientActorId);
            Assert.IsFalse(NetworkClient.connection.isAuthenticated);
        }

        [Test]
        public void Negative_AnOlderServersAdmission_WhichNamesNoProtocol_IsRefusedByTheClient()
        {
            StartClient();
            int accepted = 0;
            _authenticator.OnClientAuthenticated.AddListener(() => accepted++);

            OfflineMirror.DeliverRawToClient(NetworkMessageId<CoreAiAdmissionResponseMessage>.Id, writer =>
            {
                writer.WriteBool(true);
                writer.WriteString("");
                writer.WriteString("remote-1");
            });

            Assert.AreEqual(0, accepted, "an admission from a server that cannot read this client is not taken");
            Assert.IsNull(_authenticator.ClientActorId);
            Assert.IsTrue(_authenticator.ClientRefusal.HasValue);
            Assert.AreEqual(CoreAiAdmissionRefusalKind.ProtocolMismatch, _authenticator.ClientRefusal.Value.Kind);
            Assert.IsFalse(_authenticator.ClientRefusal.Value.RefusedByServer, "this client refused the server");
            Assert.AreEqual(CoreAiMirrorProtocol.Unversioned, _authenticator.ClientRefusal.Value.ServerProtocolVersion);
            StringAssert.Contains("this client speaks protocol", _authenticator.ClientRefusal.Value.Reason);
        }

        [Test]
        public void Negative_ANewerServersAdmission_WithFieldsThisBuildDoesNotKnow_IsRefusedByTheClient()
        {
            StartClient();
            int accepted = 0;
            _authenticator.OnClientAuthenticated.AddListener(() => accepted++);

            OfflineMirror.DeliverRawToClient(NetworkMessageId<CoreAiAdmissionResponseMessage>.Id, writer =>
            {
                writer.WriteBool(true);
                writer.WriteString("");
                writer.WriteString("remote-1");
                writer.WriteVarInt(CoreAiMirrorProtocol.Version + 1);
                writer.WriteByte((byte)CoreAiAdmissionRefusalKind.None);
                writer.WriteString("a field from a later protocol");
            });

            Assert.AreEqual(0, accepted);
            Assert.IsNull(_authenticator.ClientActorId);
            Assert.AreEqual(CoreAiMirrorProtocol.Version + 1, _authenticator.ClientRefusal.Value.ServerProtocolVersion);
            Assert.AreEqual(CoreAiAdmissionRefusalKind.ProtocolMismatch, _authenticator.ClientRefusal.Value.Kind);
        }

        [Test]
        public void ASameProtocolServersAdmission_IsTaken()
        {
            StartClient();
            int accepted = 0;
            _authenticator.OnClientAuthenticated.AddListener(() => accepted++);

            OfflineMirror.DeliverToClient(new CoreAiAdmissionResponseMessage
            {
                Admitted = true,
                Reason = "",
                ActorId = "remote-1",
                ProtocolVersion = CoreAiMirrorProtocol.Version
            });

            Assert.AreEqual(1, accepted);
            Assert.AreEqual("remote-1", _authenticator.ClientActorId);
            Assert.IsFalse(_authenticator.ClientRefusal.HasValue);
        }

        [Test]
        public void TheSerializers_RoundTripEveryField_AndReadTheOlderLayoutsAsUnversioned()
        {
            NetworkWriter writer = new();
            writer.WriteCoreAiAdmissionRequest(new CoreAiAdmissionRequestMessage
            {
                Credential = new byte[] { 1, 2, 3 },
                ProtocolVersion = 9
            });
            CoreAiAdmissionRequestMessage request =
                new NetworkReader(writer.ToArraySegment()).ReadCoreAiAdmissionRequest();
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, request.Credential);
            Assert.AreEqual(9, request.ProtocolVersion);

            writer = new NetworkWriter();
            writer.WriteCoreAiAdmissionResponse(new CoreAiAdmissionResponseMessage
            {
                Admitted = false,
                Reason = "why",
                ActorId = "",
                ProtocolVersion = 9,
                Refusal = (byte)CoreAiAdmissionRefusalKind.ProtocolMismatch
            });
            CoreAiAdmissionResponseMessage response =
                new NetworkReader(writer.ToArraySegment()).ReadCoreAiAdmissionResponse();
            Assert.IsFalse(response.Admitted);
            Assert.AreEqual("why", response.Reason);
            Assert.AreEqual(9, response.ProtocolVersion);
            Assert.AreEqual((byte)CoreAiAdmissionRefusalKind.ProtocolMismatch, response.Refusal);

            writer = new NetworkWriter();
            writer.WriteBytesAndSize(new byte[] { 7 });
            Assert.AreEqual(CoreAiMirrorProtocol.Unversioned,
                new NetworkReader(writer.ToArraySegment()).ReadCoreAiAdmissionRequest().ProtocolVersion);

            writer = new NetworkWriter();
            writer.WriteBool(true);
            writer.WriteString("");
            writer.WriteString("remote-1");
            CoreAiAdmissionResponseMessage older = new NetworkReader(writer.ToArraySegment()).ReadCoreAiAdmissionResponse();
            Assert.AreEqual("remote-1", older.ActorId);
            Assert.AreEqual(CoreAiMirrorProtocol.Unversioned, older.ProtocolVersion);
            Assert.AreEqual((byte)CoreAiAdmissionRefusalKind.None, older.Refusal);
        }

        /// <summary>
        /// The server refused the peer for its protocol: the provider never asked, the refusal names
        /// both protocols and carries the server's, the host's log says why, and the drop waits for a
        /// later frame and then happens once.
        /// </summary>
        private void AssertRefusedForTheProtocol(SentMessages<CoreAiAdmissionResponseMessage> told, int clientVersion)
        {
            Assert.AreEqual(0, _provider.Calls, "the host's provider is never asked about a peer of another protocol");
            Assert.AreEqual(0, _authenticator.AdmittedCount);
            Assert.AreEqual(1, _authenticator.ProtocolMismatchRefusals);
            Assert.AreEqual(1, _authenticator.RejectedCount);
            Assert.IsNull(_authenticator.ResultFor(Connection));
            Assert.AreEqual(1, told.Messages.Count, "the refused peer is answered");
            CoreAiAdmissionResponseMessage refusal = told.Messages[0];
            Assert.IsFalse(refusal.Admitted);
            Assert.AreEqual("", refusal.ActorId);
            Assert.AreEqual((byte)CoreAiAdmissionRefusalKind.ProtocolMismatch, refusal.Refusal);
            Assert.AreEqual(CoreAiMirrorProtocol.Version, refusal.ProtocolVersion);
            StringAssert.Contains("protocol " + CoreAiMirrorProtocol.Describe(CoreAiMirrorProtocol.Version), refusal.Reason);
            StringAssert.Contains(CoreAiMirrorProtocol.Describe(clientVersion), refusal.Reason);
            Assert.AreEqual(1, _hostLog.Count);
            StringAssert.Contains("protocol mismatch", _hostLog[0]);
            CollectionAssert.IsEmpty(_mirror.ServerDisconnectRequests, "the drop waits for the refusal to leave");

            _now = 1d;
            _authenticator.PerformDueRefusalDrops();
            _authenticator.PerformDueRefusalDrops();

            CollectionAssert.AreEqual(new[] { Connection }, _mirror.ServerDisconnectRequests);
        }

        private void StartServer()
        {
            OfflineMirror.StartServer();
            _authenticator.OnStartServer();
        }

        private void StartClient()
        {
            OfflineMirror.StartClient();
            NetworkClient.connection.isAuthenticated = false;
            _authenticator.OnStartClient();
        }

        private sealed class CountingProvider : IActorAdmissionProvider
        {
            public int Calls { get; private set; }

            public ActorAdmissionResult TryAdmit(in ActorCredential credential, string worldId)
            {
                Calls++;
                ActorContext context = new LocalActorIdentityProvider(
                        "remote-" + Calls, "session-" + Calls, worldId, ActorGrantSet.Create(new[] { "read" }),
                        AgentMemoryScope.Empty)
                    .GetActorContext(BuiltInAgentRoleIds.SmartChat);
                return ActorAdmissionResult.Admit(context, 1000 + Calls, "player" + Calls, "");
            }
        }
    }
}
