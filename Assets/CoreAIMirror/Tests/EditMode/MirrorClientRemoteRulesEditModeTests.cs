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
            _client.BindAdmittedActor(Admitted);
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
        public void A4_04_AConnectedClientNotAdmittedYet_DropsItsSends_CountedUnchargedAndSaidOnce()
        {
            // WHY: the transport connects before the admission is answered, and a remote handed to
            // Mirror in that window reached the server on an unauthenticated connection — Mirror
            // disconnects the joining client for it (A4-04).
            _client.Dispose();
            _client = new MirrorNetworkBridge(isServer: false, maxClientRequestsPerSecond: 2,
                clockSeconds: () => 0d, log: _said.Add);
            Transport.active.OnClientConnected?.Invoke();
            Assert.IsTrue(NetworkClient.isConnected);
            List<RbxNetworkResponse> completed = new();

            for (int fire = 0; fire < 3; fire++)
            {
                Assert.DoesNotThrow(() => _client.SendEvent(ClientEvent()),
                    "an unadmitted fire is a drop, never a budget error");
            }

            _client.SendRequest(ClientRequest(), completed.Add);

            Assert.AreEqual(4, _client.UnadmittedSendsDropped);
            Assert.AreEqual(0, _client.PacketsSent, "nothing is handed to Mirror before the admission");
            Assert.AreEqual(1, completed.Count, "an InvokeServer before admission fails now");
            Assert.IsFalse(completed[0].Succeeded);
            StringAssert.Contains("not been admitted", completed[0].Error);
            Assert.AreEqual(1, _said.Count, "one line for the stretch, not one per fire");

            _client.BindAdmittedActor(Admitted);
            _client.SendEvent(ClientEvent());
            _client.SendEvent(ClientEvent());

            Assert.AreEqual(2, _client.PacketsSent, "admitted, the budget applies whole from its first fire");
            Assert.AreEqual(4, _client.UnadmittedSendsDropped);
        }

        [Test]
        public void AConnectedClient_HandsItsSendsToTheTransport_AndTheUnsentLineIsArmedAgainByADrop()
        {
            _client.BindAdmittedActor(Admitted);
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

        [Test]
        public void ARemoteThatOvertakesTheAdmissionResponse_IsDroppedByTheBridge_NotRefusedByMirror()
        {
            // WHY this state: the connection is up but Mirror has not marked it authenticated,
            // because the reliable admission response has not been processed yet — and an
            // unreliable remote the server fired in the same frame arrives first. With Mirror's
            // default the handler never runs and Mirror disconnects the joining client for it.
            NetworkClient.connection.isAuthenticated = false;
            Transport.active.OnClientConnected?.Invoke();
            CoreAiRemoteEventMessage early = OfflineMirror.Event(1UL);
            early.Reliability = (byte)RbxNetworkReliability.UnreliableUnordered;

            OfflineMirror.DeliverToClient(early);

            Assert.AreEqual(1, _client.UnadmittedPacketsDropped,
                "the bridge's own rule saw the packet: Mirror's authentication gate did not throw it away first");
            CollectionAssert.IsEmpty(_events);
            Assert.AreEqual(1, _said.Count);

            _client.BindAdmittedActor(Admitted);
            OfflineMirror.DeliverToClient(OfflineMirror.Event(2UL));

            Assert.AreEqual(1, _events.Count,
                "once admission binds the actor, remotes are heard whatever Mirror's own flag says");
            Assert.IsTrue(NetworkClient.isConnected);
        }

        [Test]
        public void Negative_AReliabilityByteOutsideTheEnum_IsDroppedOnTheClientToo()
        {
            _client.BindAdmittedActor(Admitted);
            CoreAiRemoteEventMessage wire = OfflineMirror.Event(1UL);
            wire.Reliability = 9;

            OfflineMirror.DeliverToClient(wire);
            OfflineMirror.DeliverToClient(OfflineMirror.Event(0UL));

            CollectionAssert.IsEmpty(_events);
            Assert.AreEqual(2, _client.MalformedPacketsDropped);
            Assert.AreEqual(0, _client.PacketsDelivered);
        }

        [Test]
        public void AnOpenInvokeServer_FailsAtOnce_WhenTheConnectionDrops()
        {
            Transport.active.OnClientConnected?.Invoke();
            _client.BindAdmittedActor(Admitted);
            List<RbxNetworkResponse> completed = new();
            _client.SendRequest(ClientRequest(), completed.Add);
            Assert.IsEmpty(completed, "connected, the request is on its way");
            Assert.AreEqual(1, _client.PacketsSent);

            Transport.active.OnClientDisconnected?.Invoke();
            _client.PumpTimeouts();

            Assert.AreEqual(1, completed.Count,
                "no answer can come back on a connection that is gone; the call fails now, not in thirty seconds");
            Assert.IsFalse(completed[0].Succeeded);
            StringAssert.Contains("not connected", completed[0].Error);
        }

        [Test]
        public void ForgetAdmittedActor_FailsTheOpenRequests()
        {
            Transport.active.OnClientConnected?.Invoke();
            _client.BindAdmittedActor(Admitted);
            List<RbxNetworkResponse> completed = new();
            _client.SendRequest(ClientRequest(), completed.Add);

            _client.ForgetAdmittedActor();

            Assert.AreEqual(1, completed.Count);
            Assert.IsFalse(completed[0].Succeeded);
            StringAssert.Contains("not connected", completed[0].Error);
        }

        [Test]
        public void Dispose_FailsTheOpenRequests_InsteadOfDroppingThem()
        {
            Transport.active.OnClientConnected?.Invoke();
            _client.BindAdmittedActor(Admitted);
            List<RbxNetworkResponse> completed = new();
            _client.SendRequest(ClientRequest(), completed.Add);

            _client.Dispose();

            Assert.AreEqual(1, completed.Count, "a waiting script is told, not abandoned");
            Assert.IsFalse(completed[0].Succeeded);
            StringAssert.Contains("disposed", completed[0].Error);
        }

        [Test]
        public void AnOnClientInvokeAnswerTooLargeForOneMessage_IsSentAsAFailureThatSaysSo()
        {
            _mirror.UsePacketSizes(4096, 4096);
            Transport.active.OnClientConnected?.Invoke();
            _client.BindAdmittedActor(Admitted);
            List<RbxNetworkRequestResponder> responders = new();
            _client.RequestReceived += (_, responder) => responders.Add(responder);
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
                OfflineMirror.DeliverToClient(Request(2UL));

                responders[0].Complete(new byte[5000]);

                Assert.AreEqual(1, handedToMirror.Count,
                    "Mirror took the answer rather than dropping it for its size");
                Assert.IsFalse(handedToMirror[0].Success);
                StringAssert.Contains("5000 bytes", handedToMirror[0].ErrorMessage);
                Assert.AreEqual(1, _client.OversizeResponsesFailed);
            }
            finally
            {
                NetworkDiagnostics.OutMessageEvent -= record;
            }
        }

        [Test]
        public void ServerClockOffset_IsZeroUntilTheFirstAnchor_ThenTheServersTime_WhateverEitherUptimeOrTheClientsWallClock()
        {
            // WHY these numbers: the audit's probe — a server up a day, a client up five minutes
            // whose wall clock is an hour fast. Mirror's own offset compared the two uptimes and
            // read the server's time almost 23 hours off.
            const double serverUnix = 1790000000d;
            FakeWallClock clientWall = new(serverUnix + 3600d, 300d);
            RebuildClient(clientWall);
            _client.RoundTripSeconds = () => 0.1d;

            Assert.AreEqual(0d, _client.ServerClockOffsetSeconds, "nothing is known before an anchor");
            Assert.IsFalse(_client.IsServerClockSynchronized,
                "and the zero says so: it is not a claim that the clocks agree");

            OfflineMirror.DeliverToClient(new CoreAiServerClockMessage { ServerUnixSeconds = serverUnix });

            Assert.IsTrue(_client.IsServerClockSynchronized);
            Assert.AreEqual(1, _client.ClockAnchorsReceived);
            Assert.AreEqual(serverUnix + 0.05d, ServerNow(clientWall), 1e-4,
                "the server's time is the anchor plus half the round trip");
            Assert.Less(Math.Abs(ServerNow(clientWall) - serverUnix), 0.2d);

            clientWall.Advance(10d);

            Assert.AreEqual(serverUnix + 10.05d, ServerNow(clientWall), 1e-4,
                "between anchors the estimate runs on the client's monotonic clock");

            clientWall.UnixTimeSecondsFractional -= 3600d;

            Assert.AreEqual(serverUnix + 10.05d, ServerNow(clientWall), 1e-4,
                "a client whose wall clock is corrected mid-session still reads the server's time");
        }

        [Test]
        public void AClockStepOverTheThreshold_IsTakenAtOnce_ASmallCorrectionIsBlended_AndAnotherConnectionsFirstAnchorReplaces()
        {
            const double serverUnix = 1790000000d;
            FakeWallClock clientWall = new(serverUnix, 300d);
            RebuildClient(clientWall);
            _client.RoundTripSeconds = () => 0d;
            OfflineMirror.DeliverToClient(new CoreAiServerClockMessage { ServerUnixSeconds = serverUnix });

            OfflineMirror.DeliverToClient(new CoreAiServerClockMessage { ServerUnixSeconds = serverUnix + 0.4d });

            Assert.AreEqual(serverUnix + 0.1d, ServerNow(clientWall), 1e-4,
                "a sample within the threshold moves the estimate a quarter of the way: one late "
                + "anchor must not jerk every clock derived from it");

            OfflineMirror.DeliverToClient(new CoreAiServerClockMessage { ServerUnixSeconds = serverUnix + 5d });

            Assert.AreEqual(serverUnix + 5d, ServerNow(clientWall), 1e-4,
                "past the threshold the server's clock stepped, and the step is taken whole");

            OfflineMirror.StopClient();
            OfflineMirror.StartClient();
            _client.AttachHandlers();
            OfflineMirror.DeliverToClient(new CoreAiServerClockMessage { ServerUnixSeconds = serverUnix + 5.3d });

            Assert.AreEqual(serverUnix + 5.3d, ServerNow(clientWall), 1e-4,
                "a new connection may be another server: its first anchor replaces the estimate");
            Assert.AreEqual(4, _client.ClockAnchorsReceived);
        }

        [Test]
        public void A4_13_ASingleAnchorFarBehind_IsSetAside_AndTakenOnlyWhenTheNextAgreesWithIt()
        {
            // WHY: one periodic anchor held up in the network for more than the threshold looked like
            // the server's clock stepping back, and replaced the estimate: every clock on the client
            // ran seconds behind until the next anchor (A4-13).
            const double serverUnix = 1790000000d;
            FakeWallClock clientWall = new(serverUnix, 300d);
            RebuildClient(clientWall);
            _client.RoundTripSeconds = () => 0d;
            OfflineMirror.DeliverToClient(new CoreAiServerClockMessage { ServerUnixSeconds = serverUnix });
            clientWall.Advance(5d);

            OfflineMirror.DeliverToClient(new CoreAiServerClockMessage { ServerUnixSeconds = serverUnix + 3d });

            Assert.AreEqual(serverUnix + 5d, ServerNow(clientWall), 1e-4,
                "one anchor two seconds behind is a late packet until another says the same");
            Assert.AreEqual(1, _client.ClockAnchorsSetAside);
            Assert.AreEqual(1, _client.ClockAnchorsReceived, "a set-aside anchor is not taken");

            OfflineMirror.DeliverToClient(new CoreAiServerClockMessage { ServerUnixSeconds = serverUnix + 3d });

            Assert.AreEqual(serverUnix + 3d, ServerNow(clientWall), 1e-4,
                "the second in a row that far behind is the server's clock, and it is taken whole");
            Assert.AreEqual(2, _client.ClockAnchorsReceived);
        }

        [Test]
        public void A4_13_Negative_AnAnchorThatAgreesAfterAnOutlier_IsBlended_AndTheOutlierForgotten()
        {
            const double serverUnix = 1790000000d;
            FakeWallClock clientWall = new(serverUnix, 300d);
            RebuildClient(clientWall);
            _client.RoundTripSeconds = () => 0d;
            OfflineMirror.DeliverToClient(new CoreAiServerClockMessage { ServerUnixSeconds = serverUnix });

            OfflineMirror.DeliverToClient(new CoreAiServerClockMessage { ServerUnixSeconds = serverUnix - 2d });
            OfflineMirror.DeliverToClient(new CoreAiServerClockMessage { ServerUnixSeconds = serverUnix + 0.4d });
            OfflineMirror.DeliverToClient(new CoreAiServerClockMessage { ServerUnixSeconds = serverUnix - 2d });

            Assert.AreEqual(serverUnix + 0.1d, ServerNow(clientWall), 1e-4,
                "the agreeing anchor is blended as usual, and the next outlier needs a second of its own");
            Assert.AreEqual(2, _client.ClockAnchorsSetAside);
        }

        [Test]
        public void A4_08_AHeldAnchor_HoldsTheClientsServerTime_UntilItsRunningClockCatchesUp()
        {
            const double serverUnix = 1790000000d;
            FakeWallClock clientWall = new(serverUnix + 3600d, 300d);
            RebuildClient(clientWall);
            _client.RoundTripSeconds = () => 0d;
            OfflineMirror.DeliverToClient(new CoreAiServerClockMessage { ServerUnixSeconds = serverUnix });

            OfflineMirror.DeliverToClient(new CoreAiServerClockMessage
            {
                ServerUnixSeconds = serverUnix,
                HeldAheadOfWallSeconds = 10d
            });

            Assert.IsTrue(_client.IsServerClockHeld);
            Assert.AreEqual(serverUnix, ServerNow(clientWall), 1e-4, "held at the server's reading");
            clientWall.Advance(9d);
            Assert.AreEqual(serverUnix, ServerNow(clientWall), 1e-4, "still held nine seconds on");
            Assert.IsTrue(_client.IsServerClockHeld);
            clientWall.Advance(2d);
            Assert.IsFalse(_client.IsServerClockHeld, "the hold ends where the running clock catches up");
            Assert.AreEqual(serverUnix + 1d, ServerNow(clientWall), 1e-4,
                "and from there the server's time runs again");
        }

        [Test]
        public void A4_08_Negative_AHoldThatIsNegativeOrNotFinite_IsDroppedAsMalformed()
        {
            const double serverUnix = 1790000000d;
            FakeWallClock clientWall = new(serverUnix, 300d);
            RebuildClient(clientWall);
            _client.RoundTripSeconds = () => 0d;
            OfflineMirror.DeliverToClient(new CoreAiServerClockMessage { ServerUnixSeconds = serverUnix });

            foreach (double bad in new[] { -1d, double.NaN, double.PositiveInfinity })
            {
                OfflineMirror.DeliverToClient(new CoreAiServerClockMessage
                {
                    ServerUnixSeconds = serverUnix + 100d,
                    HeldAheadOfWallSeconds = bad
                });
            }

            Assert.AreEqual(3, _client.MalformedPacketsDropped);
            Assert.AreEqual(1, _client.ClockAnchorsReceived);
            Assert.IsFalse(_client.IsServerClockHeld);
            Assert.AreEqual(serverUnix, ServerNow(clientWall), 1e-4);
        }

        [Test]
        public void Negative_AnAnchorThatIsNotAPositiveFiniteTime_IsDroppedAndCounted_AndTheClockKept()
        {
            const double serverUnix = 1790000000d;
            FakeWallClock clientWall = new(serverUnix, 300d);
            RebuildClient(clientWall);
            _client.RoundTripSeconds = () => 0d;

            OfflineMirror.DeliverToClient(new CoreAiServerClockMessage { ServerUnixSeconds = double.NaN });
            Assert.IsFalse(_client.IsServerClockSynchronized, "a NaN anchor makes no clock valid");

            OfflineMirror.DeliverToClient(new CoreAiServerClockMessage { ServerUnixSeconds = serverUnix + 60d });
            foreach (double bad in new[] { double.NaN, double.PositiveInfinity, 0d, -5d })
            {
                OfflineMirror.DeliverToClient(new CoreAiServerClockMessage { ServerUnixSeconds = bad });
            }

            Assert.AreEqual(5, _client.MalformedPacketsDropped);
            Assert.AreEqual(1, _client.ClockAnchorsReceived);
            Assert.AreEqual(serverUnix + 60d, ServerNow(clientWall), 1e-4,
                "the estimate the one good anchor gave is untouched");
            Assert.AreEqual(1, _said.Count, "one line for the operator, not one per packet");
        }

        [Test]
        public void BindingTheAdmittedActor_AcknowledgesReadiness_AndAgainEachSecondUntilTheServerAnswers_ThenNever()
        {
            double now = 0d;
            _client.Dispose();
            _client = new MirrorNetworkBridge(isServer: false, clockSeconds: () => now, log: _said.Add);
            Transport.active.OnClientConnected?.Invoke();
            using SentMessages<CoreAiClientReadyMessage> acks = new();

            _client.BindAdmittedActor(Admitted);
            _client.Pump();

            Assert.AreEqual(1, acks.Messages.Count,
                "the binding is the first moment this client can route a server remote");

            now = MirrorNetworkBridge.ReadyAcknowledgementRetrySeconds - 0.01d;
            _client.Pump();
            Assert.AreEqual(1, acks.Messages.Count, "not before the retry interval");

            now = MirrorNetworkBridge.ReadyAcknowledgementRetrySeconds;
            _client.Pump();
            Assert.AreEqual(2, acks.Messages.Count,
                "a server that admitted this connection before its world attached bound it later, "
                + "and hears the repeat");

            OfflineMirror.DeliverToClient(new CoreAiServerClockMessage { ServerUnixSeconds = 1790000000d });
            now = 100d;
            _client.Pump();

            Assert.AreEqual(2, acks.Messages.Count, "the server's anchor is the answer; nothing repeats after it");
            CollectionAssert.IsEmpty(_said);
        }

        [Test]
        public void Negative_ABindingWhileNotConnected_AcknowledgesNothing_UntilAPumpFindsItConnected()
        {
            using SentMessages<CoreAiClientReadyMessage> acks = new();
            Assert.IsFalse(NetworkClient.isConnected);

            _client.BindAdmittedActor(Admitted);
            _client.Pump();

            Assert.IsEmpty(acks.Messages, "Mirror cannot send before it is connected, and must not be asked to");

            Transport.active.OnClientConnected?.Invoke();
            _client.Pump();

            Assert.AreEqual(1, acks.Messages.Count);
        }

        [Test]
        public void ASelfKick_DisconnectsTheClient_FailsItsOpenRequests_AndForgetsTheAdmission()
        {
            Transport.active.OnClientConnected?.Invoke();
            _client.BindAdmittedActor(Admitted);
            List<RbxNetworkResponse> completed = new();
            _client.SendRequest(ClientRequest(), completed.Add);

            _client.DisconnectActor(Admitted);

            Assert.IsFalse(NetworkClient.isConnected,
                "a LocalScript's Player:Kick() on its own player disconnects the client, as on Roblox");
            Assert.IsFalse(NetworkClient.active);
            Assert.AreEqual(1, _client.SelfKicks);
            Assert.IsNull(_client.AdmittedActorId);
            Assert.AreEqual(1, completed.Count, "the call that left on the kicked connection fails now");
            Assert.IsFalse(completed[0].Succeeded);

            _client.DisconnectActor(Admitted);

            Assert.AreEqual(1, _client.SelfKicks, "a second kick finds nobody");
        }

        [Test]
        public void Negative_AClientKickingAnyoneButItsOwnPlayer_DisconnectsNothing()
        {
            Transport.active.OnClientConnected?.Invoke();
            _client.BindAdmittedActor(Admitted);

            _client.DisconnectActor("someone-else");
            _client.DisconnectActor("");
            _client.DisconnectActor(null);
            _client.DisconnectActor("someone-else", "a client has no say over this");

            Assert.IsTrue(NetworkClient.isConnected, "a client has no authority over anyone else's connection");
            Assert.AreEqual(0, _client.SelfKicks);
            Assert.AreEqual(Admitted, _client.AdmittedActorId);
        }

        [Test]
        public void ADisconnectNotice_IsKeptSaidAndRaised_OutlivesTheDisconnect_AndTheNextAdmissionClearsIt()
        {
            _client.BindAdmittedActor(Admitted);
            List<CoreAiDisconnectNoticeMessage> raised = new();
            _client.DisconnectNoticeReceived += raised.Add;

            OfflineMirror.DeliverToClient(new CoreAiDisconnectNoticeMessage
            {
                Kind = (byte)CoreAiDisconnectNoticeKind.Kicked,
                Message = "banned for griefing"
            });

            Assert.IsTrue(_client.LastDisconnectNotice.HasValue);
            Assert.AreEqual((byte)CoreAiDisconnectNoticeKind.Kicked, _client.LastDisconnectNotice.Value.Kind);
            Assert.AreEqual("banned for griefing", _client.LastDisconnectNotice.Value.Message);
            Assert.AreEqual(1, raised.Count);
            Assert.AreEqual(1, _said.Count);
            StringAssert.Contains("Kicked", _said[0]);
            StringAssert.Contains("banned for griefing", _said[0]);

            _client.ForgetAdmittedActor();
            Assert.IsTrue(_client.LastDisconnectNotice.HasValue, "the host reads it after the drop");

            _client.BindAdmittedActor(Admitted);
            Assert.IsFalse(_client.LastDisconnectNotice.HasValue, "a new admission starts with no reason");
        }

        [Test]
        public void Negative_ANoticeListenerThatThrows_DoesNotEscapeMirrorsHandler_AndAnUnknownKindIsStillKept()
        {
            _client.DisconnectNoticeReceived += _ => throw new InvalidOperationException("the host's dialog blew up");

            Assert.DoesNotThrow(() => OfflineMirror.DeliverToClient(new CoreAiDisconnectNoticeMessage
            {
                Kind = 9,
                Message = "a reason from a newer server"
            }));

            Assert.AreEqual((byte)9, _client.LastDisconnectNotice.Value.Kind,
                "the text is what the player is shown, whatever the kind");
            Assert.AreEqual(2, _said.Count);
            StringAssert.Contains("reason 9", _said[0]);
            StringAssert.Contains("the host's dialog blew up", _said[1]);
        }

        /// <summary>Replaces the fixture's client bridge with one that reads the given wall clock.</summary>
        private void RebuildClient(FakeWallClock wallClock)
        {
            _client.Dispose();
            _client = new MirrorNetworkBridge(isServer: false, clockSeconds: () => 0d, log: _said.Add,
                wallClock: wallClock);
        }

        /// <summary>What <c>GetServerTimeNow</c> computes on this client: its wall clock plus the offset.</summary>
        private double ServerNow(FakeWallClock clientWall)
        {
            return clientWall.UnixTimeSecondsFractional + _client.ServerClockOffsetSeconds;
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
