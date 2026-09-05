using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using NUnit.Framework;

namespace CoreAI.Net.Mirror.Tests
{
    /// <summary>
    /// MVP11 gates for the rules the Mirror bridge enforces on every packet it receives.
    /// </summary>
    /// <remarks>
    /// WHY these drive the bridge's receive path directly instead of a live host: what is being
    /// proven is CoreAI's own rules — an unadmitted connection reaches nothing, a sender comes from
    /// the server's map and not from the packet, a response closes only its own request. None of
    /// those are transport behaviour, and routing them through Mirror's host loop would make the
    /// gate depend on the player loop's timing rather than on the rule.
    /// <para>
    /// What this file explicitly does NOT claim: that bytes cross a real socket. Delivery over the
    /// wire, MTU behaviour and latency belong to a two-process run that has not been done yet, and
    /// nothing here should be read as evidence for it.
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class MirrorBridgeRulesEditModeTests
    {
        private MirrorNetworkBridge _bridge;
        private double _now;

        [SetUp]
        public void CreateBridge()
        {
            _now = 0d;
            _bridge = new MirrorNetworkBridge(
                isServer: true, authenticator: null, maxClientRequestsPerSecond: 3,
                clockSeconds: () => _now);
        }

        [TearDown]
        public void DisposeBridge()
        {
            _bridge.Dispose();
        }

        [Test]
        public void AdmittedConnection_DeliversWithTheSenderTheServerBound()
        {
            // The envelope has no actor field at all, so this is where the sender comes from: the
            // server's own map. That is what makes impersonation impossible rather than discouraged.
            _bridge.BindConnection(11, new RbxNetworkPeer("actor-a", "session-a", "conn-11"));
            List<RbxNetworkEventMessage> delivered = new();
            _bridge.EventReceived += delivered.Add;

            _bridge.ReceiveServerEvent(11, new CoreAiRemoteEventMessage
            {
                RemoteId = 7UL,
                Direction = (byte)RbxNetworkDirection.ClientToServer,
                Reliability = (byte)RbxNetworkReliability.ReliableOrdered,
                Payload = new byte[] { 9 }
            });

            Assert.AreEqual(1, delivered.Count);
            Assert.AreEqual("actor-a", delivered[0].SenderActorId);
            Assert.AreEqual(7UL, delivered[0].RemoteId.Value);
            CollectionAssert.AreEqual(new byte[] { 9 }, delivered[0].Payload);
            Assert.AreEqual(0, _bridge.UnadmittedPacketsDropped);
        }

        [Test]
        public void Negative_UnadmittedConnection_ReachesNothingAndIsCounted()
        {
            List<RbxNetworkEventMessage> delivered = new();
            _bridge.EventReceived += delivered.Add;

            _bridge.ReceiveServerEvent(404, new CoreAiRemoteEventMessage
            {
                RemoteId = 1UL,
                Direction = (byte)RbxNetworkDirection.ClientToServer,
                Reliability = (byte)RbxNetworkReliability.ReliableOrdered,
                Payload = new byte[] { 1 }
            });

            Assert.IsEmpty(delivered, "a connection nobody admitted must reach no handler");
            Assert.AreEqual(1, _bridge.UnadmittedPacketsDropped,
                "the drop must be counted, or an operator cannot tell it from silence");
            Assert.AreEqual(0, _bridge.PacketsDelivered);
        }

        [Test]
        public void Negative_AnUnadmittedRequest_IsDroppedToo()
        {
            // The event path and the request path are separate handlers; a rule enforced on one and
            // forgotten on the other is the shape this twin exists to catch.
            bool received = false;
            _bridge.RequestReceived += (_, _) => received = true;

            _bridge.ReceiveServerRequest(404, new CoreAiRemoteRequestMessage
            {
                RemoteId = 1UL,
                Direction = (byte)RbxNetworkDirection.ClientToServer,
                CorrelationId = 1u,
                Payload = Array.Empty<byte>()
            });

            Assert.IsFalse(received);
            Assert.AreEqual(1, _bridge.UnadmittedPacketsDropped);
        }

        [Test]
        public void Negative_AResponseWithAnUnknownCorrelationId_CompletesNothing()
        {
            _bridge.BindConnection(11, new RbxNetworkPeer("actor-a", "session-a", "conn-11"));

            _bridge.ReceiveServerResponse(11, new CoreAiRemoteResponseMessage
            {
                CorrelationId = 4242u,
                Success = true,
                Payload = new byte[] { 1 },
                ErrorCode = "",
                ErrorMessage = ""
            });

            Assert.AreEqual(1, _bridge.OrphanResponsesDropped,
                "a crafted or replayed correlation id must close nothing and be counted");
        }

        [Test]
        public void Request_ThatIsNeverAnswered_FailsAtTheTimeoutAndNotBefore()
        {
            _bridge.BindConnection(11, new RbxNetworkPeer("actor-a", "session-a", "conn-11"));
            List<RbxNetworkResponse> completed = new();

            _bridge.SendRequest(ServerRequest(), completed.Add);

            _now = MirrorNetworkBridge.RequestTimeoutSeconds - 0.5d;
            _bridge.PumpTimeouts();
            Assert.IsEmpty(completed, "the documented timeout is 30 seconds, not 29.5");

            _now = MirrorNetworkBridge.RequestTimeoutSeconds + 0.1d;
            _bridge.PumpTimeouts();

            Assert.AreEqual(1, completed.Count);
            Assert.IsFalse(completed[0].Succeeded);
            StringAssert.Contains("30", completed[0].Error);
            Assert.AreEqual(1, _bridge.TimedOutRequests);
        }

        [Test]
        public void Negative_AResponseArrivingAfterTheTimeout_IsDroppedNotApplied()
        {
            // A late answer completing a call that already failed would hand the script two results
            // for one question — and the second one after it had already given up.
            _bridge.BindConnection(11, new RbxNetworkPeer("actor-a", "session-a", "conn-11"));
            List<RbxNetworkResponse> completed = new();
            _bridge.SendRequest(ServerRequest(), completed.Add);
            _now = MirrorNetworkBridge.RequestTimeoutSeconds + 1d;
            _bridge.PumpTimeouts();

            _bridge.ReceiveServerResponse(11, new CoreAiRemoteResponseMessage
            {
                CorrelationId = 1u,
                Success = true,
                Payload = new byte[] { 7 },
                ErrorCode = "",
                ErrorMessage = ""
            });

            Assert.AreEqual(1, completed.Count, "the late answer must not complete anything twice");
            Assert.IsFalse(completed[0].Succeeded);
            Assert.AreEqual(1, _bridge.OrphanResponsesDropped);
        }

        [Test]
        public void Negative_AResponseFromAnotherConnection_CompletesNothing()
        {
            // Otherwise any admitted client could answer another client's question.
            _bridge.BindConnection(11, new RbxNetworkPeer("actor-a", "session-a", "conn-11"));
            _bridge.BindConnection(12, new RbxNetworkPeer("actor-b", "session-b", "conn-12"));
            List<RbxNetworkResponse> completed = new();
            _bridge.SendRequest(ServerRequest(), completed.Add);

            _bridge.ReceiveServerResponse(12, new CoreAiRemoteResponseMessage
            {
                CorrelationId = 1u,
                Success = true,
                Payload = new byte[] { 7 },
                ErrorCode = "",
                ErrorMessage = ""
            });

            Assert.IsEmpty(completed);
            Assert.AreEqual(1, _bridge.OrphanResponsesDropped);
        }

        [Test]
        public void Disconnect_FailsThePeersOpenRequestsAndReportsTheReason()
        {
            _bridge.BindConnection(11, new RbxNetworkPeer("actor-a", "session-a", "conn-11"));
            _bridge.RegisterActor("actor-a");
            List<RbxNetworkResponse> completed = new();
            List<RbxNetworkPeerDisconnected> disconnects = new();
            _bridge.PeerDisconnected += disconnects.Add;
            _bridge.SendRequest(ServerRequest(), completed.Add);

            _bridge.NotifyDisconnected(11, RbxNetworkDisconnectReason.TransportLost);

            Assert.AreEqual(1, disconnects.Count);
            Assert.AreEqual(RbxNetworkDisconnectReason.TransportLost, disconnects[0].Reason);
            Assert.AreEqual("actor-a", disconnects[0].Peer.ActorId);
            Assert.AreEqual(1, completed.Count, "an open call must fail, not hang forever");
            Assert.IsFalse(completed[0].Succeeded);
        }

        [Test]
        public void Negative_AnOversizePayload_IsRefusedBeforeTheWire()
        {
            _bridge.BindConnection(11, new RbxNetworkPeer("actor-a", "session-a", "conn-11"));

            RbxError error = Assert.Throws<RbxError>(() => _bridge.SendEvent(
                new RbxNetworkEventMessage(
                    new InstanceId(1UL),
                    RbxNetworkDirection.ServerToClient,
                    RbxNetworkReliability.ReliableOrdered,
                    null,
                    "actor-a",
                    new byte[_bridge.MaxPayloadBytes + 1])));

            Assert.AreEqual(RbxErrorCode.PayloadTooLarge, error.Code);
            Assert.AreEqual(0, _bridge.PacketsSent, "nothing may reach the wire after a refusal");
        }

        [Test]
        public void Negative_ClientTrafficPastTheBudget_IsRefused()
        {
            // The transport facing a real network must not be the one without a budget.
            _bridge.BindConnection(11, new RbxNetworkPeer("actor-a", "session-a", "conn-11"));

            for (int request = 0; request < 3; request++)
            {
                _bridge.SendEvent(ClientEvent());
            }

            RbxError error = Assert.Throws<RbxError>(() => _bridge.SendEvent(ClientEvent()));

            Assert.AreEqual(RbxErrorCode.BudgetExceeded, error.Code);
        }

        [Test]
        public void UnregisterActor_ReleasesTheConnectionAndTheBudget()
        {
            _bridge.BindConnection(11, new RbxNetworkPeer("actor-a", "session-a", "conn-11"));
            _bridge.RegisterActor("actor-a");
            _bridge.SendEvent(ClientEvent());

            _bridge.UnregisterActor("actor-a");
            List<RbxNetworkEventMessage> delivered = new();
            _bridge.EventReceived += delivered.Add;
            _bridge.ReceiveServerEvent(11, new CoreAiRemoteEventMessage
            {
                RemoteId = 1UL,
                Direction = (byte)RbxNetworkDirection.ClientToServer,
                Reliability = (byte)RbxNetworkReliability.ReliableOrdered,
                Payload = Array.Empty<byte>()
            });

            CollectionAssert.IsEmpty(_bridge.ActorIds);
            Assert.IsEmpty(delivered,
                "a released connection id must not still resolve to the actor that left");
            Assert.AreEqual(1, _bridge.UnadmittedPacketsDropped);
        }

        [Test]
        public void MaxPayloadBytes_FallsBackToTheCodecCeilingWithNoTransport()
        {
            // A transport-less bridge (a composition built before the network starts) must still
            // answer with a usable bound rather than zero, which would refuse every message.
            Assert.AreEqual(65536, _bridge.MaxPayloadBytes);
        }

        private static RbxNetworkRequestMessage ServerRequest()
        {
            return new RbxNetworkRequestMessage(
                new InstanceId(3UL),
                RbxNetworkDirection.ServerToClient,
                null,
                "actor-a",
                Array.Empty<byte>());
        }

        private static RbxNetworkEventMessage ClientEvent()
        {
            return new RbxNetworkEventMessage(
                new InstanceId(5UL),
                RbxNetworkDirection.ClientToServer,
                RbxNetworkReliability.ReliableOrdered,
                "actor-a",
                null,
                Array.Empty<byte>());
        }
    }
}
