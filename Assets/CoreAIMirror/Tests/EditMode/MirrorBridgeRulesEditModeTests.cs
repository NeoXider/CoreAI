using System;
using System.Collections.Generic;
using System.Text;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using Mirror;
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
    /// wire and latency belong to a two-process run that has not been done yet, and nothing here
    /// should be read as evidence for it. The size rules are proven against the packet sizes kcp2k
    /// reports, which the harness can be told to report, and against Mirror's own packer — not
    /// against kcp2k itself.
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
        public void Negative_ATimeoutCompletionThatUnregistersTheActor_DoesNotAbortThePump()
        {
            // WHY two open requests and an unregistering completion: the completion is mod code —
            // a failed RemoteFunction handler that kicks the player reaches UnregisterActor, whose
            // FailPendingFor removes the peer's other open request before the pump gets to it.
            _bridge.BindConnection(11, new RbxNetworkPeer("actor-a", "session-a", "conn-11"));
            _bridge.RegisterActor("actor-a");
            List<RbxNetworkResponse> first = new();
            List<RbxNetworkResponse> second = new();
            _bridge.SendRequest(ServerRequest(), response =>
            {
                first.Add(response);
                _bridge.UnregisterActor("actor-a");
            });
            _bridge.SendRequest(ServerRequest(), second.Add);
            _now = MirrorNetworkBridge.RequestTimeoutSeconds + 1d;

            Assert.DoesNotThrow(() => _bridge.PumpTimeouts());

            Assert.AreEqual(1, first.Count);
            Assert.AreEqual(1, second.Count,
                "exactly once: failed by the unregister, never again by the pump");
            StringAssert.Contains("disconnected", second[0].Error);
            Assert.AreEqual(1, _bridge.TimedOutRequests,
                "only the request the pump itself failed counts as timed out");
        }

        [Test]
        public void Negative_ADisconnectCompletionThatUnregistersTheActor_DoesNotAbortTheRest()
        {
            _bridge.BindConnection(11, new RbxNetworkPeer("actor-a", "session-a", "conn-11"));
            _bridge.RegisterActor("actor-a");
            List<RbxNetworkResponse> first = new();
            List<RbxNetworkResponse> second = new();
            _bridge.SendRequest(ServerRequest(), response =>
            {
                first.Add(response);
                _bridge.UnregisterActor("actor-a");
            });
            _bridge.SendRequest(ServerRequest(), second.Add);

            Assert.DoesNotThrow(() => _bridge.NotifyDisconnected(11,
                RbxNetworkDisconnectReason.TransportLost));

            Assert.AreEqual(1, first.Count);
            Assert.AreEqual(1, second.Count, "the nested unregister failed it; the outer loop must not fail it twice");
            CollectionAssert.IsEmpty(_bridge.ActorIds);
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
        public void Negative_ALocalFireWithNobodyListening_Returns_AndIsNeverReplayedToALaterListener()
        {
            // WHY: the local delivery loop called `EventReceived?.Invoke(queue.Dequeue())`, whose argument
            // is skipped when nobody listens, so the event stayed queued and a host-local actor's
            // FireServer before any handler connected spun the host's main thread forever.
            _bridge.RegisterActor("actor-local");

            _bridge.SendEvent(ClientEventFrom("actor-local", 1));
            List<RbxNetworkEventMessage> delivered = new();
            _bridge.EventReceived += delivered.Add;
            _bridge.SendEvent(ClientEventFrom("actor-local", 2));

            Assert.AreEqual(2, _bridge.LocalDeliveries);
            Assert.AreEqual(1, delivered.Count, "the fire nobody heard is gone, not held for the next listener");
            CollectionAssert.AreEqual(new byte[] { 2 }, delivered[0].Payload);
            Assert.AreEqual("actor-local", delivered[0].SenderActorId);
        }

        [Test]
        public void Negative_ClientTrafficPastTheBudget_IsRefused()
        {
            // WHY a server bridge: it charges the client-to-server traffic of the actors running in
            // its own process, as the loopback does; what it receives from remote clients is not
            // budgeted yet, a known limit.
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

        [Test]
        public void MaxPayloadBytesFor_Unreliable_FallsBackToRobloxsCeilingWithNoTransport()
        {
            Assert.AreEqual(MirrorNetworkBridge.UnreliablePayloadCeilingBytes,
                _bridge.MaxPayloadBytesFor(RbxNetworkReliability.UnreliableUnordered),
                "an UnreliableRemoteEvent is capped at Roblox's 1000 bytes before any transport exists");
            Assert.AreEqual(65536, _bridge.MaxPayloadBytesFor(RbxNetworkReliability.ReliableOrdered));
            Assert.AreEqual(65536, _bridge.MaxRequestPayloadBytes);
        }

        [Test]
        public void NegativeConnectionIds_AreConnectionsLikeAnyOther()
        {
            // WHY: kcp2k derives connection ids from an endpoint hash, and Mirror allows any id but
            // 0. A bridge that treated a negative id as "no connection" would drop every packet of
            // roughly half the players as unadmitted.
            _bridge.BindConnection(-5, new RbxNetworkPeer("actor-b", "session-b", "-5"));
            List<RbxNetworkEventMessage> delivered = new();
            _bridge.EventReceived += delivered.Add;

            _bridge.ReceiveServerEvent(-5, ClientWire(7UL));

            Assert.AreEqual(1, delivered.Count, "an admitted connection with a negative id must be heard");
            Assert.AreEqual("actor-b", delivered[0].SenderActorId);
            Assert.AreEqual(0, _bridge.UnadmittedPacketsDropped);
        }

        [Test]
        public void Negative_AResponseFromANegativeIdConnection_CannotAnswerAnotherConnectionsQuestion()
        {
            _bridge.BindConnection(11, new RbxNetworkPeer("actor-a", "session-a", "conn-11"));
            _bridge.BindConnection(-5, new RbxNetworkPeer("actor-b", "session-b", "conn-5"));
            List<RbxNetworkResponse> completed = new();
            _bridge.SendRequest(ServerRequest(), completed.Add);

            _bridge.ReceiveServerResponse(-5, Answer(1u));

            Assert.IsEmpty(completed,
                "the question was asked of actor-a's connection; actor-b's must not be able to answer it");
            Assert.AreEqual(1, _bridge.OrphanResponsesDropped);
        }

        [Test]
        public void Negative_NoConnectionCanAnswerAQuestionThatWasNeverSentAnywhere()
        {
            List<RbxNetworkResponse> completed = new();
            _bridge.SendRequest(new RbxNetworkRequestMessage(new InstanceId(3UL),
                RbxNetworkDirection.ServerToClient, null, "actor-nobody", Array.Empty<byte>()), completed.Add);

            _bridge.ReceiveServerResponse(-1, Answer(1u));

            Assert.AreEqual(1, completed.Count,
                "a request that reached no connection fails once, at once, and no response completes it");
            Assert.IsFalse(completed[0].Succeeded);
            StringAssert.Contains("not connected", completed[0].Error,
                "the failure is the bridge's own, not an answer from a connection");
            Assert.AreEqual(1, _bridge.OrphanResponsesDropped);
            Assert.AreEqual(1, _bridge.UnroutablePacketsDropped,
                "a request addressed to nobody never left, and that is counted");
        }

        [Test]
        public void A4_07_InvokeClientToAPlayerWithNoConnection_FailsAtOnce_AndLeavesNothingPending()
        {
            // WHY: the request was recorded as pending with no connection to answer it, so the
            // server script waited the whole thirty seconds for a player who had left (A4-07).
            List<RbxNetworkResponse> completed = new();

            _bridge.SendRequest(new RbxNetworkRequestMessage(new InstanceId(9UL),
                RbxNetworkDirection.ServerToClient, null, "left-the-game", new byte[] { 1 }), completed.Add);

            Assert.AreEqual(1, completed.Count, "answered at once, not at the timeout");
            Assert.IsFalse(completed[0].Succeeded);
            StringAssert.Contains("the player is not connected", completed[0].Error);
            Assert.AreEqual(1, _bridge.UnroutablePacketsDropped);
            _now = MirrorNetworkBridge.RequestTimeoutSeconds + 1d;
            _bridge.PumpTimeouts();
            Assert.AreEqual(1, completed.Count, "nothing was left pending to fail a second time");
            Assert.AreEqual(0, _bridge.TimedOutRequests);
        }

        [Test]
        public void A4_07_Negative_InvokeClientToABoundPlayer_StillWaitsForItsAnswer()
        {
            _bridge.BindConnection(11, new RbxNetworkPeer("actor-a", "session-a", "conn-11"));
            List<RbxNetworkResponse> completed = new();

            _bridge.SendRequest(ServerRequest(), completed.Add);

            Assert.IsEmpty(completed, "a player the bridge holds a connection for is asked, and awaited");
            _bridge.ReceiveServerResponse(11, Answer(1u));
            Assert.AreEqual(1, completed.Count);
            Assert.IsTrue(completed[0].Succeeded);
        }

        [Test]
        public void Negative_AnEventWithAnInvalidRemoteId_IsDroppedAndCounted_NeverThrown()
        {
            // WHY: left to the engine-free message constructor, an invalid id throws inside
            // Mirror's handler after the packet has been counted as delivered.
            _bridge.BindConnection(11, new RbxNetworkPeer("actor-a", "session-a", "conn-11"));
            List<RbxNetworkEventMessage> delivered = new();
            _bridge.EventReceived += delivered.Add;

            Assert.DoesNotThrow(() => _bridge.ReceiveServerEvent(11, ClientWire(0UL)));
            Assert.DoesNotThrow(() => _bridge.ReceiveServerEvent(11, ClientWire(InstanceId.AuthorityBit | 5UL)));

            Assert.IsEmpty(delivered);
            Assert.AreEqual(0, _bridge.PacketsDelivered, "a dropped packet is not a delivered one");
            Assert.AreEqual(2, _bridge.MalformedPacketsDropped);

            _bridge.ReceiveServerEvent(11, ClientWire(7UL));
            Assert.AreEqual(1, delivered.Count, "the connection stays admitted; its next valid remote is heard");
        }

        [Test]
        public void Negative_AReliabilityByteOutsideTheEnum_IsDroppedAndCounted()
        {
            _bridge.BindConnection(11, new RbxNetworkPeer("actor-a", "session-a", "conn-11"));
            List<RbxNetworkEventMessage> delivered = new();
            _bridge.EventReceived += delivered.Add;
            CoreAiRemoteEventMessage wire = ClientWire(7UL);
            wire.Reliability = 7;

            _bridge.ReceiveServerEvent(11, wire);

            Assert.IsEmpty(delivered, "a delivery class the enum does not name must reach no world");
            Assert.AreEqual(1, _bridge.MalformedPacketsDropped);
        }

        [Test]
        public void Negative_AServerBoundEventClaimingAnotherDirection_IsDroppedAndCounted()
        {
            _bridge.BindConnection(11, new RbxNetworkPeer("actor-a", "session-a", "conn-11"));
            List<RbxNetworkEventMessage> delivered = new();
            _bridge.EventReceived += delivered.Add;
            CoreAiRemoteEventMessage wire = ClientWire(7UL);
            wire.Direction = (byte)RbxNetworkDirection.ServerToAllClients;

            _bridge.ReceiveServerEvent(11, wire);

            Assert.IsEmpty(delivered);
            Assert.AreEqual(1, _bridge.MalformedPacketsDropped);
        }

        [Test]
        public void Negative_ARequestWithAnInvalidRemoteId_IsDroppedAndCounted_NeverThrown()
        {
            _bridge.BindConnection(11, new RbxNetworkPeer("actor-a", "session-a", "conn-11"));
            bool received = false;
            _bridge.RequestReceived += (_, _) => received = true;

            Assert.DoesNotThrow(() => _bridge.ReceiveServerRequest(11, new CoreAiRemoteRequestMessage
            {
                RemoteId = 0UL,
                Direction = (byte)RbxNetworkDirection.ClientToServer,
                CorrelationId = 1u,
                Payload = Array.Empty<byte>()
            }));

            Assert.IsFalse(received);
            Assert.AreEqual(0, _bridge.PacketsDelivered);
            Assert.AreEqual(1, _bridge.MalformedPacketsDropped);
        }

        [Test]
        public void Negative_AMalformedEnvelope_CrossingMirrorsHandler_LeavesTheConnectionUp()
        {
            OfflineMirror mirror = new();
            try
            {
                OfflineMirror.StartServer();
                OfflineMirror.AdmitServerConnection(11);
                _bridge.BindConnection(11, new RbxNetworkPeer("actor-a", "session-a", "conn-11"));
                List<RbxNetworkEventMessage> delivered = new();
                _bridge.EventReceived += delivered.Add;

                OfflineMirror.DeliverToServer(11, ClientWire(0UL));
                OfflineMirror.DeliverToServer(11, ClientWire(7UL));

                CollectionAssert.IsEmpty(mirror.ServerDisconnectRequests,
                    "the policy is pinned: a malformed envelope is dropped and counted, and the "
                    + "connection is not disconnected for it");
                Assert.AreEqual(1, _bridge.MalformedPacketsDropped);
                Assert.AreEqual(1, delivered.Count);
                Assert.AreEqual(7UL, delivered[0].RemoteId.Value);
            }
            finally
            {
                mirror.Dispose();
            }
        }

        [Test]
        public void NotifyDisconnected_ReleasesThatConnection_EvenWithNobodyListening_AndOnlyThatOne()
        {
            _bridge.BindConnection(11, new RbxNetworkPeer("actor-a", "session-a", "conn-11"));
            _bridge.BindConnection(12, new RbxNetworkPeer("actor-b", "session-b", "conn-12"));
            _bridge.RegisterActor("actor-a");
            _bridge.RegisterActor("actor-b");
            List<RbxNetworkEventMessage> delivered = new();
            _bridge.EventReceived += delivered.Add;

            _bridge.NotifyDisconnected(11, RbxNetworkDisconnectReason.TransportLost);
            _bridge.ReceiveServerEvent(11, ClientWire(7UL));
            _bridge.ReceiveServerEvent(12, ClientWire(8UL));

            Assert.AreEqual(1, delivered.Count,
                "a connection that is gone must stop resolving to its actor, whoever listened");
            Assert.AreEqual("actor-b", delivered[0].SenderActorId);
            Assert.AreEqual(1, _bridge.UnadmittedPacketsDropped);
            CollectionAssert.AreEqual(new[] { "actor-b" }, _bridge.ActorIds);
        }

        [Test]
        public void BindingAnActorOnANewConnection_ClosesItsOlderOne_NewestWins()
        {
            OfflineMirror mirror = new();
            try
            {
                OfflineMirror.StartServer();
                OfflineMirror.AdmitServerConnection(1);
                OfflineMirror.AdmitServerConnection(2);
                _bridge.BindConnection(1, new RbxNetworkPeer("actor-a", "session-1", "1"));
                _bridge.RegisterActor("actor-a");
                List<RbxNetworkResponse> olderQuestion = new();
                _bridge.SendRequest(ServerRequest(), olderQuestion.Add);
                List<RbxNetworkEventMessage> delivered = new();
                List<RbxNetworkPeerDisconnected> disconnects = new();
                _bridge.EventReceived += delivered.Add;
                _bridge.PeerDisconnected += disconnects.Add;
                using SentMessages<CoreAiDisconnectNoticeMessage> notices = new();

                _bridge.BindConnection(2, new RbxNetworkPeer("actor-a", "session-2", "2"));
                mirror.FlushServer();

                CollectionAssert.AreEqual(new[] { 1 },
                    mirror.ServerSendTargetsOf<CoreAiDisconnectNoticeMessage>(),
                    "the older client is told why, and only it");
                Assert.AreEqual(1, notices.Messages.Count);
                Assert.AreEqual((byte)CoreAiDisconnectNoticeKind.Superseded, notices.Messages[0].Kind);
                Assert.AreEqual(MirrorNetworkBridge.SupersededNoticeMessage, notices.Messages[0].Message);
                CollectionAssert.IsEmpty(mirror.ServerDisconnectRequests,
                    "the drop waits for a later frame: Mirror discards a dropped connection's "
                    + "unflushed messages, and the notice is one");
                _bridge.Pump();
                CollectionAssert.IsEmpty(mirror.ServerDisconnectRequests,
                    "a pump in the same frame is still that frame");

                _now = 0.016d;
                _bridge.Pump();

                CollectionAssert.AreEqual(new[] { 1 }, mirror.ServerDisconnectRequests,
                    "the older connection of an actor bound again is dropped at the transport");
                Assert.AreEqual(1, _bridge.SupersededConnections);
                Assert.AreEqual(1, olderQuestion.Count, "the older connection's open request fails now");
                StringAssert.Contains("newer connection", olderQuestion[0].Error);
                CollectionAssert.AreEqual(new[] { "actor-a" }, _bridge.ActorIds,
                    "the actor carries over to its new connection");

                _bridge.NotifyDisconnected(1, RbxNetworkDisconnectReason.TransportLost);
                _bridge.ReceiveServerEvent(1, ClientWire(7UL));
                _bridge.ReceiveServerEvent(2, ClientWire(8UL));

                Assert.IsEmpty(disconnects,
                    "the older connection's late report finds nothing: it cannot tear down the new session");
                Assert.AreEqual(1, delivered.Count);
                Assert.AreEqual(8UL, delivered[0].RemoteId.Value);
                Assert.AreEqual("actor-a", delivered[0].SenderActorId);
                Assert.AreEqual(1, _bridge.UnadmittedPacketsDropped);
            }
            finally
            {
                mirror.Dispose();
            }
        }

        [Test]
        public void AKick_TellsTheClientWhyFirst_UnbindsAtOnce_AndTheTransportDropsItOnALaterFrame()
        {
            OfflineMirror mirror = new();
            try
            {
                OfflineMirror.StartServer();
                OfflineMirror.AdmitServerConnection(11);
                _bridge.BindConnection(11, new RbxNetworkPeer("actor-a", "session-a", "conn-11"));
                _bridge.RegisterActor("actor-a");
                List<RbxNetworkPeerDisconnected> disconnects = new();
                _bridge.PeerDisconnected += disconnects.Add;
                using SentMessages<CoreAiDisconnectNoticeMessage> notices = new();
                string longMessage = "banned: " + new string('x', 3000);

                _bridge.DisconnectActor("actor-a", longMessage);

                Assert.AreEqual(1, notices.Messages.Count, "the kicked client is told why");
                Assert.AreEqual((byte)CoreAiDisconnectNoticeKind.Kicked, notices.Messages[0].Kind);
                StringAssert.StartsWith("banned: xxx", notices.Messages[0].Message);
                Assert.LessOrEqual(Encoding.UTF8.GetByteCount(notices.Messages[0].Message),
                    MirrorNetworkBridge.MaxNoticeMessageBytes, "a long kick message is cut, never refused");
                Assert.AreEqual(1, _bridge.DisconnectNoticesSent);
                Assert.AreEqual(1, disconnects.Count, "the session is torn down at once");
                Assert.AreEqual(RbxNetworkDisconnectReason.ServerClosed, disconnects[0].Reason);
                CollectionAssert.IsEmpty(_bridge.ActorIds);
                _bridge.ReceiveServerEvent(11, ClientWire(7UL));
                Assert.AreEqual(1, _bridge.UnadmittedPacketsDropped,
                    "until the drop, the kicked connection is nobody");
                CollectionAssert.IsEmpty(mirror.ServerDisconnectRequests,
                    "the drop is owed, not made, in the kick's own frame");

                _now = 0.016d;
                _bridge.Pump();
                _bridge.Pump();

                CollectionAssert.AreEqual(new[] { 11 }, mirror.ServerDisconnectRequests,
                    "the next frame drops it, once");
            }
            finally
            {
                mirror.Dispose();
            }
        }

        [Test]
        public void AKickWithoutAMessage_TellsTheClientTheDefault_AndAnActorWithNoConnectionIsNothing()
        {
            OfflineMirror mirror = new();
            try
            {
                OfflineMirror.StartServer();
                OfflineMirror.AdmitServerConnection(11);
                _bridge.BindConnection(11, new RbxNetworkPeer("actor-a", "session-a", "conn-11"));
                using SentMessages<CoreAiDisconnectNoticeMessage> notices = new();

                _bridge.DisconnectActor("actor-nobody");
                _bridge.DisconnectActor("actor-nobody", "never sent");
                Assert.IsEmpty(notices.Messages, "an actor with no connection here gets no notice");

                ((INetworkBridge)_bridge).DisconnectActor("actor-a");

                Assert.AreEqual(1, notices.Messages.Count);
                Assert.AreEqual(MirrorNetworkBridge.DefaultKickMessage, notices.Messages[0].Message,
                    "the interface's kick carries no message, so the client is told the default");
            }
            finally
            {
                mirror.Dispose();
            }
        }

        [Test]
        public void Negative_ABridgeThatStopsPumping_StillDropsWhatItOwes_WhenAskedOrDisposed()
        {
            OfflineMirror mirror = new();
            try
            {
                OfflineMirror.StartServer();
                OfflineMirror.AdmitServerConnection(11);
                OfflineMirror.AdmitServerConnection(12);
                _bridge.BindConnection(11, new RbxNetworkPeer("actor-a", "session-a", "conn-11"));
                _bridge.BindConnection(12, new RbxNetworkPeer("actor-b", "session-b", "conn-12"));

                _bridge.DisconnectActor("actor-a");
                _bridge.PerformOwedDropsNow();

                CollectionAssert.AreEqual(new[] { 11 }, mirror.ServerDisconnectRequests,
                    "asked to, the bridge drops what it owes without waiting for a frame");

                _bridge.DisconnectActor("actor-b");
                _bridge.Dispose();

                CollectionAssert.AreEqual(new[] { 11, 12 }, mirror.ServerDisconnectRequests,
                    "a disposed bridge must not leave a kicked socket open on a connection bound to nobody");
            }
            finally
            {
                mirror.Dispose();
            }
        }

        [Test]
        public void Negative_AnOwedDrop_NeverReachesAStrangerOnTheReusedId()
        {
            OfflineMirror mirror = new();
            try
            {
                OfflineMirror.StartServer();
                OfflineMirror.AdmitServerConnection(11);
                _bridge.BindConnection(11, new RbxNetworkPeer("actor-a", "session-a", "conn-11"));
                _bridge.DisconnectActor("actor-a");
                // WHY the same id: the kicked peer left on its own before the drop was made, and
                // kcp2k gave its endpoint's id to the next connection.
                NetworkServer.RemoveConnection(11);
                OfflineMirror.AdmitServerConnection(11);

                _now = 0.016d;
                _bridge.Pump();

                CollectionAssert.IsEmpty(mirror.ServerDisconnectRequests,
                    "the drop was owed to the kicked connection, not to whoever holds its id now");
            }
            finally
            {
                mirror.Dispose();
            }
        }

        [Test]
        public void UnderKcpSizes_AReliableRemoteAndARemoteFunctionOf2000Bytes_AreSentWhole()
        {
            // WHY: a single ceiling taken from the smaller channel — kcp2k's one-datagram
            // unreliable limit — refuses every reliable remote over about 1.2 KB, and online only.
            OfflineMirror mirror = new();
            try
            {
                mirror.UsePacketSizes(OfflineMirror.KcpReliableMaxPacketSize,
                    OfflineMirror.KcpUnreliableMaxPacketSize);
                OfflineMirror.StartServer();
                OfflineMirror.AdmitServerConnection(11);
                _bridge.BindConnection(11, new RbxNetworkPeer("actor-a", "session-a", "conn-11"));

                Assert.AreEqual(65536, _bridge.MaxPayloadBytes,
                    "kcp2k's reliable channel carries the codec's whole 64 KiB in one message");
                Assert.DoesNotThrow(
                    () => _bridge.SendEvent(ServerEvent(RbxNetworkReliability.ReliableOrdered, 2000)));
                Assert.DoesNotThrow(() => _bridge.SendRequest(new RbxNetworkRequestMessage(new InstanceId(3UL),
                    RbxNetworkDirection.ServerToClient, null, "actor-a", new byte[2000]), _ => { }));
                mirror.FlushServer();

                CollectionAssert.AreEqual(new[] { 11 }, mirror.ServerSendTargetsOf<CoreAiRemoteEventMessage>());
                CollectionAssert.AreEqual(new[] { 11 }, mirror.ServerSendTargetsOf<CoreAiRemoteRequestMessage>());
                Assert.AreEqual(2, _bridge.PacketsSent);
            }
            finally
            {
                mirror.Dispose();
            }
        }

        [Test]
        public void Negative_UnderKcpSizes_AnUnreliableRemoteOverRobloxs1000Bytes_IsRefused_AndNeverCountedAsSent()
        {
            OfflineMirror mirror = new();
            try
            {
                mirror.UsePacketSizes(OfflineMirror.KcpReliableMaxPacketSize,
                    OfflineMirror.KcpUnreliableMaxPacketSize);
                OfflineMirror.StartServer();
                OfflineMirror.AdmitServerConnection(11);
                _bridge.BindConnection(11, new RbxNetworkPeer("actor-a", "session-a", "conn-11"));

                Assert.AreEqual(1000, _bridge.MaxPayloadBytesFor(RbxNetworkReliability.UnreliableUnordered));
                // WHY 1190 as well as 1001: 1190 bytes fit kcp2k's 1194-byte datagram, but the
                // message Mirror makes of them is 1204 bytes, which Mirror drops — so it must be
                // refused here, never counted as sent.
                foreach (int size in new[] { 1001, 1190 })
                {
                    RbxError error = Assert.Throws<RbxError>(
                        () => _bridge.SendEvent(ServerEvent(RbxNetworkReliability.UnreliableUnordered, size)));
                    Assert.AreEqual(RbxErrorCode.PayloadTooLarge, error.Code);
                    StringAssert.Contains("UnreliableRemoteEvent", error.Message);
                }

                Assert.DoesNotThrow(
                    () => _bridge.SendEvent(ServerEvent(RbxNetworkReliability.UnreliableUnordered, 1000)));
                mirror.FlushServer();

                CollectionAssert.AreEqual(new[] { 11 }, mirror.ServerSendTargetsOf<CoreAiRemoteEventMessage>(),
                    "exactly the 1000-byte fire reached the transport");
                Assert.AreEqual(1, _bridge.PacketsSent, "a refused fire is never counted as sent");
            }
            finally
            {
                mirror.Dispose();
            }
        }

        [Test]
        public void UnreliableCeiling_IsTheLargestPayloadMirrorsUnreliableChannelCarries_OnASmallMtu()
        {
            OfflineMirror mirror = new();
            try
            {
                mirror.UsePacketSizes(65536, 600);
                OfflineMirror.StartServer();
                OfflineMirror.AdmitServerConnection(11);
                _bridge.BindConnection(11, new RbxNetworkPeer("actor-a", "session-a", "conn-11"));
                int ceiling = _bridge.MaxPayloadBytesFor(RbxNetworkReliability.UnreliableUnordered);
                int mirrorMax = NetworkMessages.MaxMessageSize(Channels.Unreliable);

                Assert.Less(ceiling, 1000, "a 600-byte datagram is the tighter bound here");
                Assert.LessOrEqual(PackedSize(ceiling), mirrorMax,
                    "a payload at the ceiling fits the message Mirror accepts on the unreliable channel");
                Assert.Greater(PackedSize(ceiling + 1), mirrorMax,
                    "one byte more would be dropped by Mirror, so the ceiling is tight");

                _bridge.SendEvent(ServerEvent(RbxNetworkReliability.UnreliableUnordered, ceiling));
                Assert.Throws<RbxError>(
                    () => _bridge.SendEvent(ServerEvent(RbxNetworkReliability.UnreliableUnordered, ceiling + 1)));
                mirror.FlushServer();

                CollectionAssert.AreEqual(new[] { 11 }, mirror.ServerSendTargetsOf<CoreAiRemoteEventMessage>(),
                    "the fire at the ceiling reached the transport rather than being dropped by Mirror");
            }
            finally
            {
                mirror.Dispose();
            }
        }

        [Test]
        public void AnOnServerInvokeAnswerTooLargeForOneMessage_IsSentAsAFailureThatSaysSo()
        {
            OfflineMirror mirror = new();
            List<CoreAiRemoteResponseMessage> handedToMirror = new();
            Action<NetworkDiagnostics.MessageInfo> record = info =>
            {
                if (info.message is CoreAiRemoteResponseMessage answer)
                {
                    handedToMirror.Add(answer);
                }
            };
            NetworkDiagnostics.OutMessageEvent += record;
            try
            {
                mirror.UsePacketSizes(4096, 4096);
                OfflineMirror.StartServer();
                OfflineMirror.AdmitServerConnection(11);
                _bridge.BindConnection(11, new RbxNetworkPeer("actor-a", "session-a", "conn-11"));
                List<RbxNetworkRequestResponder> responders = new();
                _bridge.RequestReceived += (_, responder) => responders.Add(responder);
                _bridge.ReceiveServerRequest(11, ClientRequestWire(1u));
                _bridge.ReceiveServerRequest(11, ClientRequestWire(2u));

                responders[0].Complete(new byte[5000]);
                responders[1].Complete(new byte[100]);
                mirror.FlushServer();

                Assert.AreEqual(2, handedToMirror.Count,
                    "both answers reach the transport; neither is dropped by Mirror for its size");
                Assert.IsFalse(handedToMirror[0].Success);
                StringAssert.Contains("5000 bytes", handedToMirror[0].ErrorMessage,
                    "the caller learns why at once instead of waiting out the timeout");
                Assert.AreEqual(1u, handedToMirror[0].CorrelationId);
                Assert.IsTrue(handedToMirror[1].Success, "an answer that fits is sent unchanged");
                Assert.AreEqual(100, handedToMirror[1].Payload.Length);
                Assert.AreEqual(1, _bridge.OversizeResponsesFailed);
            }
            finally
            {
                NetworkDiagnostics.OutMessageEvent -= record;
                mirror.Dispose();
            }
        }

        [Test]
        public void Negative_AnAnswerOutlivingItsConnection_NeverReachesTheStrangerOnTheReusedId()
        {
            OfflineMirror mirror = new();
            try
            {
                OfflineMirror.StartServer();
                OfflineMirror.AdmitServerConnection(7);
                _bridge.BindConnection(7, new RbxNetworkPeer("actor-a", "session-a", "7"));
                List<RbxNetworkRequestResponder> responders = new();
                _bridge.RequestReceived += (_, responder) => responders.Add(responder);
                _bridge.ReceiveServerRequest(7, ClientRequestWire(1u));
                OfflineMirror.DropServerConnection(7);
                _bridge.NotifyDisconnected(7, RbxNetworkDisconnectReason.TransportLost);
                // WHY the same id: kcp2k's ids are endpoint hashes, so the next peer from that
                // endpoint sits exactly where the one that asked was, and its first request uses
                // correlation id 1 too.
                OfflineMirror.AdmitServerConnection(7);
                _bridge.BindConnection(7, new RbxNetworkPeer("actor-b", "session-b", "7"));

                responders[0].Complete(new byte[] { 1 });
                mirror.FlushServer();

                CollectionAssert.IsEmpty(mirror.ServerSendTargetsOf<CoreAiRemoteResponseMessage>(),
                    "actor-a's answer must not be delivered to actor-b on the reused id");
                Assert.AreEqual(1, _bridge.StaleResponsesDropped);
            }
            finally
            {
                mirror.Dispose();
            }
        }

        [Test]
        public void AnAnswer_ReachesTheConnectionThatAsked()
        {
            OfflineMirror mirror = new();
            try
            {
                OfflineMirror.StartServer();
                OfflineMirror.AdmitServerConnection(7);
                _bridge.BindConnection(7, new RbxNetworkPeer("actor-a", "session-a", "7"));
                List<RbxNetworkRequestResponder> responders = new();
                _bridge.RequestReceived += (_, responder) => responders.Add(responder);
                _bridge.ReceiveServerRequest(7, ClientRequestWire(1u));

                responders[0].Complete(new byte[] { 1 });
                mirror.FlushServer();

                CollectionAssert.AreEqual(new[] { 7 }, mirror.ServerSendTargetsOf<CoreAiRemoteResponseMessage>());
                Assert.AreEqual(0, _bridge.StaleResponsesDropped);
            }
            finally
            {
                mirror.Dispose();
            }
        }

        [Test]
        public void Dispose_FailsOpenRequestsInsteadOfDroppingThem_AndARequestAfterwardsFailsAtOnce()
        {
            _bridge.BindConnection(11, new RbxNetworkPeer("actor-a", "session-a", "conn-11"));
            List<RbxNetworkResponse> completed = new();
            _bridge.SendRequest(ServerRequest(), completed.Add);

            _bridge.Dispose();

            Assert.AreEqual(1, completed.Count, "a waiting script is told, not abandoned");
            Assert.IsFalse(completed[0].Succeeded);
            StringAssert.Contains("disposed", completed[0].Error);

            _bridge.SendRequest(ServerRequest(), completed.Add);

            Assert.AreEqual(2, completed.Count,
                "nothing pumps a disposed bridge's timeouts, so a request made to it fails now");
            Assert.IsFalse(completed[1].Succeeded);
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

        private static RbxNetworkEventMessage ServerEvent(RbxNetworkReliability reliability, int size)
        {
            return new RbxNetworkEventMessage(
                new InstanceId(4UL),
                RbxNetworkDirection.ServerToClient,
                reliability,
                null,
                "actor-a",
                new byte[size]);
        }

        private static CoreAiRemoteEventMessage ClientWire(ulong remoteId)
        {
            return new CoreAiRemoteEventMessage
            {
                RemoteId = remoteId,
                Direction = (byte)RbxNetworkDirection.ClientToServer,
                Reliability = (byte)RbxNetworkReliability.ReliableOrdered,
                Payload = new byte[] { 1 }
            };
        }

        private static CoreAiRemoteRequestMessage ClientRequestWire(uint correlationId)
        {
            return new CoreAiRemoteRequestMessage
            {
                RemoteId = 9UL,
                Direction = (byte)RbxNetworkDirection.ClientToServer,
                CorrelationId = correlationId,
                Payload = Array.Empty<byte>()
            };
        }

        private static CoreAiRemoteResponseMessage Answer(uint correlationId)
        {
            return new CoreAiRemoteResponseMessage
            {
                CorrelationId = correlationId,
                Success = true,
                Payload = new byte[] { 7 },
                ErrorCode = "",
                ErrorMessage = ""
            };
        }

        /// <summary>The size Mirror's own packer gives an unreliable fire of this many payload bytes.</summary>
        /// <summary>
        /// What Mirror packs for an unreliable event of <paramref name="payloadBytes"/> under the widest remote
        /// id: Mirror writes the id as a varint, so the largest id is the envelope a ceiling must hold for.
        /// </summary>
        private static int PackedSize(int payloadBytes)
        {
            NetworkWriter writer = new();
            NetworkMessages.Pack(new CoreAiRemoteEventMessage
            {
                RemoteId = ulong.MaxValue,
                Direction = (byte)RbxNetworkDirection.ServerToClient,
                Reliability = (byte)RbxNetworkReliability.UnreliableUnordered,
                Payload = new byte[payloadBytes]
            }, writer);
            return writer.Position;
        }

        private static RbxNetworkEventMessage ClientEventFrom(string actorId, byte payload)
        {
            return new RbxNetworkEventMessage(
                new InstanceId(5UL),
                RbxNetworkDirection.ClientToServer,
                RbxNetworkReliability.ReliableOrdered,
                actorId,
                null,
                new[] { payload });
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
