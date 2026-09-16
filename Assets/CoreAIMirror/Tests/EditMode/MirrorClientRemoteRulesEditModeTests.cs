using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using Mirror;
using NUnit.Framework;

namespace CoreAI.Net.Mirror.Tests
{
    /// <summary>
    /// The rules a client bridge enforces on every server remote it receives: the recipient is the
    /// actor this client was admitted as, a broadcast keeps the direction the server sent, and a
    /// remote with no admission bound is dropped, counted and said once.
    /// </summary>
    /// <remarks>
    /// WHY these cross Mirror's real client handler table instead of calling the bridge directly:
    /// the drop rule exists because a throwing client handler makes Mirror disconnect the client, so
    /// what has to be proven is that the packet crosses that table and comes out counted rather than
    /// thrown — a rule tested below the table would not distinguish the two.
    /// </remarks>
    [TestFixture]
    public sealed class MirrorClientRemoteRulesEditModeTests
    {
        private const string Admitted = "remote-1";

        private OfflineMirror _mirror;
        private MirrorNetworkBridge _client;
        private List<RbxNetworkEventMessage> _events;
        private List<RbxNetworkRequestMessage> _requests;
        private List<string> _said;

        [SetUp]
        public void CreateClientBridge()
        {
            _mirror = new OfflineMirror();
            _said = new List<string>();
            _events = new List<RbxNetworkEventMessage>();
            _requests = new List<RbxNetworkRequestMessage>();
            _client = new MirrorNetworkBridge(isServer: false, clockSeconds: () => 0d, log: _said.Add);
            _client.EventReceived += _events.Add;
            _client.RequestReceived += (message, _) => _requests.Add(message);
            OfflineMirror.StartClient();
        }

        [TearDown]
        public void RestoreMirror()
        {
            OfflineMirror.RunAll(
                () => _client.Dispose(),
                () => _mirror.Dispose());
        }

        [Test]
        public void Negative_ARemoteBeforeAdmission_IsDroppedCountedAndSaidOnce()
        {
            Assert.IsNull(_client.AdmittedActorId, "a fresh client bridge knows nobody");

            OfflineMirror.DeliverToClient(OfflineMirror.Event(1UL));
            OfflineMirror.DeliverToClient(OfflineMirror.Event(2UL));
            OfflineMirror.DeliverToClient(Request(3UL));

            CollectionAssert.IsEmpty(_events, "nothing may reach the world before admission");
            CollectionAssert.IsEmpty(_requests, "the request path shares the rule");
            Assert.AreEqual(3, _client.UnadmittedPacketsDropped);
            Assert.AreEqual(0, _client.PacketsDelivered);
            Assert.AreEqual(1, _said.Count, "one line for the operator, not one per packet");
            StringAssert.Contains("before this client's admission", _said[0]);
        }

        [Test]
        public void AdmittedActor_IsTheRecipientOfEveryInboundRemote()
        {
            _client.BindAdmittedActor(Admitted);

            OfflineMirror.DeliverToClient(OfflineMirror.Event(1UL));
            OfflineMirror.DeliverToClient(Request(2UL));

            Assert.AreEqual(1, _events.Count);
            Assert.AreEqual(RbxNetworkDirection.ServerToClient, _events[0].Direction);
            Assert.AreEqual(Admitted, _events[0].RecipientActorId);
            Assert.IsNull(_events[0].SenderActorId, "a server has no actor to be the sender");
            Assert.AreEqual(1, _requests.Count);
            Assert.AreEqual(Admitted, _requests[0].RecipientActorId);
            Assert.AreEqual(2, _client.PacketsDelivered);
            Assert.AreEqual(0, _client.UnadmittedPacketsDropped);
            CollectionAssert.IsEmpty(_said);
        }

        [Test]
        public void Broadcast_KeepsTheDirectionTheServerSent_SoTheWorldReachesEveryLocalActor()
        {
            _client.BindAdmittedActor(Admitted);
            CoreAiRemoteEventMessage wire = OfflineMirror.Event(1UL);
            wire.Direction = (byte)RbxNetworkDirection.ServerToAllClients;

            OfflineMirror.DeliverToClient(wire);

            Assert.AreEqual(1, _events.Count);
            Assert.AreEqual(RbxNetworkDirection.ServerToAllClients, _events[0].Direction);
            Assert.IsNull(_events[0].RecipientActorId,
                "a broadcast names nobody; the world delivers it to each registered local actor");
        }

        [Test]
        public void ForgetAdmittedActor_ReturnsTheBridgeToTheUnadmittedRule()
        {
            _client.BindAdmittedActor(Admitted);
            OfflineMirror.DeliverToClient(OfflineMirror.Event(1UL));

            _client.ForgetAdmittedActor();
            OfflineMirror.DeliverToClient(OfflineMirror.Event(2UL));

            Assert.IsNull(_client.AdmittedActorId);
            Assert.AreEqual(1, _events.Count, "a remote after the disconnect must reach nothing");
            Assert.AreEqual(1, _client.UnadmittedPacketsDropped);
            Assert.AreEqual(1, _said.Count, "the one-time line is armed again by each admission");
        }

        [Test]
        public void Negative_AnAdmittedActorTheLocalWorldNeverRegistered_IsSaidOnceAndStillDelivered()
        {
            _client.RegisterActor("local");
            _client.BindAdmittedActor(Admitted);

            OfflineMirror.DeliverToClient(OfflineMirror.Event(1UL));
            OfflineMirror.DeliverToClient(OfflineMirror.Event(2UL));

            Assert.AreEqual(2, _events.Count, "the packet is still delivered as the server addressed it");
            Assert.AreEqual(Admitted, _events[1].RecipientActorId);
            Assert.AreEqual(1, _said.Count);
            StringAssert.Contains("'" + Admitted + "'", _said[0]);
            StringAssert.Contains("[local]", _said[0]);
            StringAssert.Contains("IActorIdentityProvider", _said[0]);
        }

        [Test]
        public void AdmittedActor_TheLocalWorldRegistered_IsNotQuestioned()
        {
            _client.RegisterActor(Admitted);
            _client.BindAdmittedActor(Admitted);

            OfflineMirror.DeliverToClient(OfflineMirror.Event(1UL));

            Assert.AreEqual(1, _events.Count);
            CollectionAssert.IsEmpty(_said);
        }

        [Test]
        public void Negative_AClientThatIsNotConnected_DropsItsSendsCountedAndSaidOnce()
        {
            // WHY this state: StartClient leaves Mirror connecting, and a remote fired before the
            // transport connects — or after it dropped — is the case Mirror answers with an error
            // per call while the counters claimed the packets left.
            Assert.IsFalse(NetworkClient.isConnected);
            List<RbxNetworkResponse> completed = new();

            _client.SendEvent(ClientEvent());
            _client.SendEvent(ClientEvent());
            _client.SendRequest(ClientRequest(), completed.Add);

            Assert.AreEqual(0, _client.PacketsSent, "nothing the transport never took may count as sent");
            Assert.AreEqual(0L, _client.BytesSent);
            Assert.AreEqual(3, _client.UnsentPacketsDropped);
            Assert.AreEqual(1, completed.Count,
                "a request with no connection to leave on fails now, not thirty seconds later");
            Assert.IsFalse(completed[0].Succeeded);
            StringAssert.Contains("not connected", completed[0].Error);
            Assert.AreEqual(1, _said.Count, "one line for the operator, not one per packet");
            StringAssert.Contains("not connected", _said[0]);
        }

        [Test]
        public void Negative_AClientThatIsNotConnected_IsNeverRateLimited_EveryFireIsADrop()
        {
            // WHY a budget of two: connected, the third fire of a second is refused with
            // BudgetExceeded; disconnected, every fire is the documented drop — and a budget
            // charged before the connection was checked would turn the third into that error, for
            // a packet that never left. WHY the fixture's bridge is replaced: the budget is fixed
            // at construction, and the default is too large to reach in a test.
            _client.Dispose();
            _client = new MirrorNetworkBridge(isServer: false, maxClientRequestsPerSecond: 2,
                clockSeconds: () => 0d, log: _said.Add);
            Assert.IsFalse(NetworkClient.isConnected);
            List<RbxNetworkResponse> completed = new();

            for (int fire = 1; fire <= 3; fire++)
            {
                Assert.DoesNotThrow(() => _client.SendEvent(ClientEvent()),
                    "event fire " + fire + " while disconnected must be a drop, never a budget error");
                Assert.DoesNotThrow(() => _client.SendRequest(ClientRequest(), completed.Add),
                    "request " + fire + " while disconnected must be a drop, never a budget error");
            }

            Assert.AreEqual(6, _client.UnsentPacketsDropped, "every call is counted as dropped");
            Assert.AreEqual(0, _client.PacketsSent);
            Assert.AreEqual(3, completed.Count, "each request fails now, as not connected");
            Assert.IsTrue(completed.TrueForAll(response =>
                    !response.Succeeded && response.Error.Contains("not connected")),
                "a request dropped for no connection says so, not that a budget was spent");
            Assert.AreEqual(1, _said.Count, "one line for the disconnected stretch");

            // WHY the budget is then shown whole: a drop that had been charged would leave a
            // connected client refused on its first fire, for packets that never left.
            Transport.active.OnClientConnected?.Invoke();
            Assert.IsTrue(NetworkClient.isConnected);
            _client.SendEvent(ClientEvent());
            _client.SendEvent(ClientEvent());
            RbxError error = Assert.Throws<RbxError>(() => _client.SendEvent(ClientEvent()),
                "connected, the budget applies from its first fire, untouched by the drops");
            Assert.AreEqual(RbxErrorCode.BudgetExceeded, error.Code);
            Assert.AreEqual(2, _client.PacketsSent);
        }

        [Test]
        public void AConnectedClient_HandsItsSendsToTheTransport_AndTheUnsentLineIsArmedAgainByADrop()
        {
            _client.SendEvent(ClientEvent());
            Assert.AreEqual(1, _said.Count);
            Transport.active.OnClientConnected?.Invoke();
            Assert.IsTrue(NetworkClient.isConnected, "the transport's connect must move Mirror to connected");

            _client.SendEvent(ClientEvent());

            Assert.AreEqual(1, _client.PacketsSent);
            Assert.AreEqual(1, _client.UnsentPacketsDropped, "the earlier drop stays counted; the send does not");
            Assert.AreEqual(1, _said.Count, "a send that left says nothing");

            Transport.active.OnClientDisconnected?.Invoke();
            _client.SendEvent(ClientEvent());

            Assert.IsFalse(NetworkClient.isConnected);
            Assert.AreEqual(1, _client.PacketsSent);
            Assert.AreEqual(2, _client.UnsentPacketsDropped);
            Assert.AreEqual(2, _said.Count, "each disconnected stretch is said once");
        }

        [Test]
        public void Negative_AServerBridge_HasNoAdmittedActorOfItsOwn()
        {
            MirrorNetworkBridge server = new(isServer: true, clockSeconds: () => 0d);
            try
            {
                Assert.Throws<InvalidOperationException>(() => server.BindAdmittedActor(Admitted));
                Assert.IsNull(server.AdmittedActorId);
            }
            finally
            {
                server.Dispose();
            }
        }

        [Test]
        public void Negative_AnAdmissionThatNamesNobody_IsRefusedNotBound()
        {
            Assert.Throws<ArgumentException>(() => _client.BindAdmittedActor(""));
            Assert.Throws<ArgumentException>(() => _client.BindAdmittedActor(null));

            Assert.IsNull(_client.AdmittedActorId);
        }

        [Test]
        public void Dispose_ForgetsTheAdmittedActor()
        {
            _client.BindAdmittedActor(Admitted);

            _client.Dispose();

            Assert.IsNull(_client.AdmittedActorId);
        }

        private static CoreAiRemoteRequestMessage Request(ulong remoteId)
        {
            return new CoreAiRemoteRequestMessage
            {
                RemoteId = remoteId,
                Direction = (byte)RbxNetworkDirection.ServerToClient,
                CorrelationId = 1u,
                Payload = Array.Empty<byte>()
            };
        }

        /// <summary>The message <c>RemoteEvent:FireServer()</c> hands a client bridge.</summary>
        private static RbxNetworkEventMessage ClientEvent()
        {
            return new RbxNetworkEventMessage(
                new InstanceId(5UL),
                RbxNetworkDirection.ClientToServer,
                RbxNetworkReliability.ReliableOrdered,
                Admitted,
                null,
                new byte[] { 1 });
        }

        /// <summary>The message <c>RemoteFunction:InvokeServer()</c> hands a client bridge.</summary>
        private static RbxNetworkRequestMessage ClientRequest()
        {
            return new RbxNetworkRequestMessage(
                new InstanceId(6UL),
                RbxNetworkDirection.ClientToServer,
                Admitted,
                null,
                new byte[] { 1 });
        }
    }
}
