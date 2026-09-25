using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using Mirror;
using NUnit.Framework;

namespace CoreAI.Net.Mirror.Tests
{
    /// <summary>
    /// Where a server broadcast goes: to every admitted connection and to no other, however many
    /// connections Mirror holds that connected and never finished admission.
    /// </summary>
    /// <remarks>
    /// WHY the witness is the transport's send boundary decoded by message type: the bridge's own
    /// counter is written by the code under test, and a count of batches is polluted by Mirror,
    /// which pings every connection on its first flush — below admission, on the unreliable
    /// channel's own batch — so an unadmitted connection is reached by the transport without ever
    /// being addressed by the bridge. Only the wire id of each message says whether a batch
    /// carried the bridge's event or Mirror's ping, and that is what every assertion here reads.
    /// </remarks>
    [TestFixture]
    public sealed class MirrorBroadcastEditModeTests
    {
        private const int Stranger = 12;
        private const double ServerUnix = 1790000000d;

        private OfflineMirror _mirror;
        private MirrorNetworkBridge _bridge;
        private FakeWallClock _wall;
        private List<string> _said;
        private double _now;

        [SetUp]
        public void StartServerWithAStrangerConnected()
        {
            _mirror = new OfflineMirror();
            _now = 0d;
            _wall = new FakeWallClock(ServerUnix, 86400d);
            _said = new List<string>();
            _bridge = new MirrorNetworkBridge(isServer: true, clockSeconds: () => _now, log: _said.Add,
                wallClock: _wall);
            OfflineMirror.StartServer();
            OfflineMirror.OpenServerConnection(Stranger);
        }

        [TearDown]
        public void RestoreMirror()
        {
            OfflineMirror.RunAll(
                () => _bridge.Dispose(),
                () => _mirror.Dispose());
        }

        [Test]
        public void Broadcast_ReachesTheAdmittedConnection_AndNotTheOneThatNeverFinishedAdmission()
        {
            Admit(11, "actor-a");

            _bridge.SendEvent(FireAllClients());
            _mirror.FlushServer();

            CollectionAssert.AreEqual(new[] { 11 }, _mirror.ServerSendTargetsOf<CoreAiRemoteEventMessage>(),
                "a connection that never sent its admission request must receive no world state");
            Assert.AreEqual(1, _bridge.PacketsSent);
        }

        [Test]
        public void Broadcast_ReachesEveryAdmittedConnection_Once()
        {
            Admit(11, "actor-a");
            Admit(13, "actor-b");

            _bridge.SendEvent(FireAllClients());
            _mirror.FlushServer();

            CollectionAssert.AreEquivalent(new[] { 11, 13 },
                _mirror.ServerSendTargetsOf<CoreAiRemoteEventMessage>(),
                "one broadcast is one event per admitted connection, however many batches Mirror flushes");
            Assert.AreEqual(2, _bridge.PacketsSent);
        }

        [Test]
        public void Negative_ABroadcastWithNobodyAdmitted_ReachesNoConnection()
        {
            _bridge.SendEvent(FireAllClients());
            _mirror.FlushServer();

            CollectionAssert.IsEmpty(_mirror.ServerSendTargetsOf<CoreAiRemoteEventMessage>(),
                "Mirror holding a connection is not the same as the world having admitted it");
            Assert.AreEqual(0, _bridge.PacketsSent);
        }

        [Test]
        public void Negative_TheWitness_WouldSeeAnEventThatReachedTheStranger()
        {
            // WHY a send behind the bridge's back: the negatives above pass on an empty list, and an
            // empty list is also what a witness that decodes nothing returns. This batches to the
            // stranger the one message the bridge must never batch to it, so the witness is proven
            // to see exactly the failure those negatives exist for.
            NetworkServer.connections[Stranger].Send(new CoreAiRemoteEventMessage
            {
                RemoteId = 5UL,
                Direction = (byte)RbxNetworkDirection.ServerToAllClients,
                Reliability = (byte)RbxNetworkReliability.ReliableOrdered,
                Payload = new byte[] { 1 }
            });
            _mirror.FlushServer();

            CollectionAssert.AreEqual(new[] { Stranger },
                _mirror.ServerSendTargetsOf<CoreAiRemoteEventMessage>());
        }

        [Test]
        public void Mirror_PingsTheStrangerBelowAdmission_AndTheWitnessTellsThatFromTheBroadcast()
        {
            // WHY a zero ping interval: a connection pings on a flush only once NetworkTime.localTime (the editor's
            // unscaled time) has passed its interval, so whether this flush pinged anybody depended on the editor's
            // clock, and the test failed when its class ran alone. With no interval every flush pings every connection.
            float pingInterval = NetworkTime.PingInterval;
            NetworkTime.PingInterval = 0f;
            List<int> pinged;
            try
            {
                Admit(11, "actor-a");

                _bridge.SendEvent(FireAllClients());
                _mirror.FlushServer();

                pinged = _mirror.ServerSendTargetsOf<NetworkPingMessage>();
            }
            finally
            {
                NetworkTime.PingInterval = pingInterval;
            }

            CollectionAssert.Contains(pinged, Stranger,
                "the transport does reach the stranger — with Mirror's ping, never with the bridge's event");
            CollectionAssert.Contains(pinged, 11);
            CollectionAssert.AreEqual(new[] { 11 }, _mirror.ServerSendTargetsOf<CoreAiRemoteEventMessage>());
            Assert.AreEqual(pinged.Count + 1, _mirror.ServerSends.Count,
                "everything the transport was handed is accounted for: the pings, and the bridge's one event");
        }

        [Test]
        public void ClientToServerOnTheServer_IsDeliveredInProcess_AndNeverPutOnTheWire()
        {
            // WHY: on a server, this direction comes from an actor running in this process — a
            // restricted actor's mod on the host. On the wire it would reach every remote client as
            // though the server had fired at it, while the server's own scripts never heard it.
            Admit(11, "actor-a");
            Admit(13, "actor-b");
            _bridge.RegisterActor("actor-a");
            _bridge.RegisterActor("actor-b");
            _bridge.RegisterActor("carol-agent");
            List<RbxNetworkEventMessage> heard = new();
            _bridge.EventReceived += heard.Add;

            _bridge.SendEvent(FireServerFrom("carol-agent"));
            _mirror.FlushServer();

            CollectionAssert.IsEmpty(_mirror.ServerSendTargetsOf<CoreAiRemoteEventMessage>(),
                "a client-to-server remote must never leave the server");
            Assert.AreEqual(1, heard.Count, "the server's own world hears it, once");
            Assert.AreEqual(RbxNetworkDirection.ClientToServer, heard[0].Direction);
            Assert.AreEqual("carol-agent", heard[0].SenderActorId);
            Assert.AreEqual(0, _bridge.PacketsSent);
            Assert.AreEqual(1, _bridge.LocalDeliveries);
        }

        [Test]
        public void Negative_ClientToServerFromAnActorThisHostNeverRegistered_IsRefused()
        {
            Admit(11, "actor-a");
            List<RbxNetworkEventMessage> heard = new();
            _bridge.EventReceived += heard.Add;

            RbxError error = Assert.Throws<RbxError>(() => _bridge.SendEvent(FireServerFrom("ghost")));
            _mirror.FlushServer();

            Assert.AreEqual(RbxErrorCode.NotAuthority, error.Code);
            Assert.IsEmpty(heard);
            CollectionAssert.IsEmpty(_mirror.ServerSendTargetsOf<CoreAiRemoteEventMessage>());
        }

        [Test]
        public void InvokeServerFromAHostLocalActor_IsAnsweredInProcess_NotLeftToTheTimeout()
        {
            Admit(11, "actor-a");
            _bridge.RegisterActor("carol-agent");
            RbxNetworkRequestMessage asked = null;
            _bridge.RequestReceived += (message, responder) =>
            {
                asked = message;
                responder.Complete(new byte[] { 42 });
            };
            List<RbxNetworkResponse> completed = new();

            _bridge.SendRequest(new RbxNetworkRequestMessage(new InstanceId(6UL),
                RbxNetworkDirection.ClientToServer, "carol-agent", null, new byte[] { 1 }), completed.Add);
            _mirror.FlushServer();

            Assert.AreEqual(1, completed.Count, "the answer comes now, not after thirty seconds");
            Assert.IsTrue(completed[0].Succeeded);
            CollectionAssert.AreEqual(new byte[] { 42 }, completed[0].Payload);
            Assert.AreEqual("carol-agent", asked.SenderActorId);
            CollectionAssert.IsEmpty(_mirror.ServerSendTargetsOf<CoreAiRemoteRequestMessage>());
        }

        [Test]
        public void FireAllClients_ReachesEveryConnectionOnTheWire_AndEachHostLocalActorInProcess()
        {
            Admit(11, "actor-a");
            _bridge.RegisterActor("actor-a");
            _bridge.RegisterActor("local-player");
            List<RbxNetworkEventMessage> heard = new();
            _bridge.EventReceived += heard.Add;

            _bridge.SendEvent(FireAllClients());
            _mirror.FlushServer();

            CollectionAssert.AreEqual(new[] { 11 }, _mirror.ServerSendTargetsOf<CoreAiRemoteEventMessage>());
            Assert.AreEqual(1, heard.Count,
                "the host-local actor gets its copy in process; the remote one got its copy on the wire");
            Assert.AreEqual(RbxNetworkDirection.ServerToClient, heard[0].Direction);
            Assert.AreEqual("local-player", heard[0].RecipientActorId,
                "addressed to the local actor alone, so the world does not fire the remote players' signals too");
            Assert.AreEqual(1, _bridge.PacketsSent);
            Assert.AreEqual(1, _bridge.LocalDeliveries);
        }

        [Test]
        public void FireClientAndInvokeClient_ToAHostLocalActor_AreDeliveredInProcess()
        {
            _bridge.RegisterActor("local-player");
            List<RbxNetworkEventMessage> heard = new();
            _bridge.EventReceived += heard.Add;
            _bridge.RequestReceived += (_, responder) => responder.Complete(new byte[] { 7 });
            List<RbxNetworkResponse> completed = new();

            _bridge.SendEvent(FireClientTo("local-player"));
            _bridge.SendRequest(new RbxNetworkRequestMessage(new InstanceId(6UL),
                RbxNetworkDirection.ServerToClient, null, "local-player", Array.Empty<byte>()), completed.Add);
            _mirror.FlushServer();

            Assert.AreEqual(1, heard.Count);
            Assert.AreEqual("local-player", heard[0].RecipientActorId);
            Assert.AreEqual(1, completed.Count, "InvokeClient to a local actor is answered in process");
            Assert.IsTrue(completed[0].Succeeded);
            CollectionAssert.IsEmpty(_mirror.ServerSendTargetsOf<CoreAiRemoteEventMessage>());
            CollectionAssert.IsEmpty(_mirror.ServerSendTargetsOf<CoreAiRemoteRequestMessage>());
        }

        [Test]
        public void Negative_FireClientToAnActorNobodyKnows_IsCounted_NotSilent()
        {
            Admit(11, "actor-a");
            List<RbxNetworkEventMessage> heard = new();
            _bridge.EventReceived += heard.Add;

            _bridge.SendEvent(FireClientTo("nobody"));
            _mirror.FlushServer();

            Assert.IsEmpty(heard);
            CollectionAssert.IsEmpty(_mirror.ServerSendTargetsOf<CoreAiRemoteEventMessage>());
            Assert.AreEqual(1, _bridge.UnroutablePacketsDropped);
            Assert.AreEqual(0, _bridge.PacketsSent);
        }

        [Test]
        public void AJoiningConnection_IsSentNothingUntilItsReadinessAck_ThenTheClockAndEverythingHeld_InOrder()
        {
            Join(11, "actor-a");
            List<RbxNetworkResponse> answered = new();

            _bridge.SendEvent(FireClientTo("actor-a"));
            _bridge.SendRequest(InvokeClientOf("actor-a"), answered.Add);
            _bridge.SendEvent(FireAllClients());
            _mirror.FlushServer();

            CollectionAssert.IsEmpty(BridgeMessagesTo(11),
                "a client that has not read its admission yet cannot route a remote, and one whose "
                + "bridge is not built would be disconnected by its own Mirror for it");
            Assert.AreEqual(0, _bridge.PacketsSent);
            Assert.AreEqual(3, _bridge.PacketsHeldUntilReady);
            using SentMessages<CoreAiServerClockMessage> anchors = new();

            OfflineMirror.DeliverToServer(11, new CoreAiClientReadyMessage());
            _mirror.FlushServer();

            CollectionAssert.AreEqual(new[]
                {
                    NetworkMessageId<CoreAiServerClockMessage>.Id,
                    NetworkMessageId<CoreAiRemoteEventMessage>.Id,
                    NetworkMessageId<CoreAiRemoteRequestMessage>.Id,
                    NetworkMessageId<CoreAiRemoteEventMessage>.Id
                },
                BridgeMessagesTo(11),
                "the server's clock first, then every held remote in the order it was fired");
            Assert.AreEqual(1, _bridge.ReadyAcknowledgements);
            Assert.AreEqual(3, _bridge.PacketsSent, "a held remote counts as sent when it is sent");
            Assert.AreEqual(1, anchors.Messages.Count);
            Assert.AreEqual(ServerUnix, anchors.Messages[0].ServerUnixSeconds,
                "the anchor is the server's wall clock, not its uptime");
            Assert.AreEqual(Channels.Reliable, anchors.ChannelIds[0],
                "the anchor that makes a client's clock valid must arrive");
            Assert.IsEmpty(answered, "the held InvokeClient is on its way, not failed");
            Assert.AreEqual(0, _bridge.NotReadyPacketsDropped);
        }

        [Test]
        public void Negative_AnUnreliableRemoteToAJoiningConnection_IsDroppedAndCounted_NeverHeld()
        {
            Join(11, "actor-a");
            Admit(13, "actor-b");

            _bridge.SendEvent(UnreliableFireAllClients());
            _mirror.FlushServer();

            CollectionAssert.AreEqual(new[] { 13 }, _mirror.ServerSendTargetsOf<CoreAiRemoteEventMessage>(),
                "the connection that is ready gets it; the joining one does not");
            Assert.AreEqual(1, _bridge.NotReadyPacketsDropped);
            Assert.AreEqual(0, _bridge.PacketsHeldUntilReady,
                "an unreliable update replayed after the join would be stale state delivered as news");

            OfflineMirror.DeliverToServer(11, new CoreAiClientReadyMessage());
            _bridge.SendEvent(UnreliableFireAllClients());
            _mirror.FlushServer();

            CollectionAssert.AreEquivalent(new[] { 13, 13, 11 },
                _mirror.ServerSendTargetsOf<CoreAiRemoteEventMessage>(),
                "the dropped one never comes back; the next one reaches both");
            CollectionAssert.IsEmpty(_said, "a routine drop at join is counted, not logged");
        }

        [Test]
        public void Negative_AJoiningConnectionThatNeverAcknowledges_IsDroppedAtTheDeadline_AndAnAcknowledgedOneIsNot()
        {
            Join(11, "actor-a");
            Join(13, "actor-b");
            _bridge.RegisterActor("actor-a");
            _bridge.RegisterActor("actor-b");
            List<RbxNetworkPeerDisconnected> disconnects = new();
            _bridge.PeerDisconnected += disconnects.Add;
            OfflineMirror.DeliverToServer(13, new CoreAiClientReadyMessage());
            _bridge.SendEvent(FireClientTo("actor-a"));

            _now = MirrorNetworkBridge.ReadinessTimeoutSeconds - 0.01d;
            _bridge.Pump();

            CollectionAssert.IsEmpty(_mirror.ServerDisconnectRequests, "the deadline is ten seconds, not less");
            Assert.IsEmpty(disconnects);

            _now = MirrorNetworkBridge.ReadinessTimeoutSeconds;
            _bridge.Pump();

            CollectionAssert.AreEqual(new[] { 11 }, _mirror.ServerDisconnectRequests,
                "a client that never says it can hear the server — an older client, or one whose "
                + "bridge was never built — is dropped rather than kept as a Player that hears nothing");
            Assert.AreEqual(1, disconnects.Count, "its session is torn down, once");
            Assert.AreEqual("actor-a", disconnects[0].Peer.ActorId);
            Assert.AreEqual(RbxNetworkDisconnectReason.ServerClosed, disconnects[0].Reason);
            Assert.AreEqual(1, _bridge.ReadinessTimeouts);
            Assert.AreEqual(1, _bridge.NotReadyPacketsDropped, "what was held for it is discarded, counted");
            CollectionAssert.AreEqual(new[] { "actor-b" }, _bridge.ActorIds);
            Assert.AreEqual(1, _said.Count);
            StringAssert.Contains("did not acknowledge readiness", _said[0]);

            _now = 100d;
            _bridge.Pump();

            CollectionAssert.AreEqual(new[] { 11 }, _mirror.ServerDisconnectRequests,
                "the acknowledged connection has no deadline, and the dropped one is dropped once");
        }

        [Test]
        public void Negative_WhatIsHeldForAJoiningConnection_IsBounded_AndAHeldInvokeClientPastTheBoundFailsNow()
        {
            Join(11, "actor-a");
            for (int index = 0; index < MirrorNetworkBridge.MaxHeldMessagesPerJoiningConnection; index++)
            {
                _bridge.SendEvent(FireClientTo("actor-a"));
            }

            _bridge.SendEvent(FireClientTo("actor-a"));
            List<RbxNetworkResponse> answered = new();
            _bridge.SendRequest(InvokeClientOf("actor-a"), answered.Add);

            Assert.AreEqual(MirrorNetworkBridge.MaxHeldMessagesPerJoiningConnection,
                _bridge.PacketsHeldUntilReady);
            Assert.AreEqual(2, _bridge.NotReadyPacketsDropped);
            Assert.AreEqual(1, answered.Count,
                "a call that cannot be held fails now instead of waiting out the whole timeout");
            Assert.IsFalse(answered[0].Succeeded);
            StringAssert.Contains("had not finished joining", answered[0].Error);
            Assert.AreEqual(1, _said.Count, "the overflow is said once per connection");

            OfflineMirror.DeliverToServer(11, new CoreAiClientReadyMessage());
            _mirror.FlushServer();

            Assert.AreEqual(MirrorNetworkBridge.MaxHeldMessagesPerJoiningConnection,
                _mirror.ServerSendTargetsOf<CoreAiRemoteEventMessage>().Count);
            CollectionAssert.IsEmpty(_mirror.ServerSendTargetsOf<CoreAiRemoteRequestMessage>());
        }

        [Test]
        public void Negative_WhatIsHeld_IsBoundedInBytesToo()
        {
            Join(11, "actor-a");
            int large = 60000;
            int fit = MirrorNetworkBridge.MaxHeldBytesPerJoiningConnection / large;
            for (int index = 0; index <= fit; index++)
            {
                _bridge.SendEvent(new RbxNetworkEventMessage(new InstanceId(5UL),
                    RbxNetworkDirection.ServerToClient, RbxNetworkReliability.ReliableOrdered, null,
                    "actor-a", new byte[large]));
            }

            Assert.AreEqual(fit, _bridge.PacketsHeldUntilReady);
            Assert.AreEqual(1, _bridge.NotReadyPacketsDropped);
        }

        [Test]
        public void Negative_AJoiningConnectionThatLeaves_DiscardsWhatWasHeld_AndFailsItsHeldCall()
        {
            Join(11, "actor-a");
            List<RbxNetworkResponse> answered = new();
            _bridge.SendEvent(FireClientTo("actor-a"));
            _bridge.SendRequest(InvokeClientOf("actor-a"), answered.Add);

            _bridge.NotifyDisconnected(11, RbxNetworkDisconnectReason.TransportLost);
            OfflineMirror.DeliverToServer(11, new CoreAiClientReadyMessage());
            _mirror.FlushServer();

            Assert.AreEqual(2, _bridge.NotReadyPacketsDropped);
            Assert.AreEqual(1, answered.Count);
            Assert.IsFalse(answered[0].Succeeded);
            CollectionAssert.IsEmpty(BridgeMessagesTo(11),
                "an acknowledgement after the connection left binds nothing and releases nothing");
            Assert.AreEqual(1, _bridge.UnadmittedPacketsDropped);
            Assert.AreEqual(0, _bridge.ReadyAcknowledgements);
        }

        [Test]
        public void Negative_AReadinessAckFromAConnectionWithNoBinding_ReachesNothing_UntilItIsBound()
        {
            OfflineMirror.AdmitServerConnection(11);

            OfflineMirror.DeliverToServer(11, new CoreAiClientReadyMessage());
            _mirror.FlushServer();

            Assert.AreEqual(1, _bridge.UnadmittedPacketsDropped);
            Assert.AreEqual(0, _bridge.ReadyAcknowledgements);
            CollectionAssert.IsEmpty(BridgeMessagesTo(11), "an unbound connection is sent no clock");

            _bridge.BindConnection(11, new RbxNetworkPeer("actor-a", "session-a", "conn-11"),
                awaitClientReady: true);
            OfflineMirror.DeliverToServer(11, new CoreAiClientReadyMessage());
            OfflineMirror.DeliverToServer(11, new CoreAiClientReadyMessage());
            _mirror.FlushServer();

            Assert.AreEqual(1, _bridge.ReadyAcknowledgements,
                "the client repeats until answered; one connection is acknowledged once");
            CollectionAssert.AreEqual(new[] { 11 }, _mirror.ServerSendTargetsOf<CoreAiServerClockMessage>());
        }

        [Test]
        public void APeriodicClockAnchor_ReachesEveryAcknowledgedConnection_Unreliably_AndNoOther()
        {
            Join(11, "actor-a");
            Admit(13, "actor-b");
            Join(14, "actor-c");
            OfflineMirror.DeliverToServer(11, new CoreAiClientReadyMessage());
            using SentMessages<CoreAiServerClockMessage> anchors = new();

            _now = MirrorNetworkBridge.ClockAnchorIntervalSeconds - 0.01d;
            _bridge.Pump();
            Assert.IsEmpty(anchors.Messages, "not before the interval");

            _now = MirrorNetworkBridge.ClockAnchorIntervalSeconds;
            _wall.Advance(MirrorNetworkBridge.ClockAnchorIntervalSeconds);
            _bridge.Pump();
            _bridge.Pump();
            _mirror.FlushServer();

            Assert.AreEqual(1, anchors.Messages.Count, "one anchor per interval");
            Assert.AreEqual(Channels.Unreliable, anchors.ChannelIds[0],
                "a lost periodic anchor is replaced by the next; a retransmitted one would arrive stale");
            Assert.AreEqual(ServerUnix + MirrorNetworkBridge.ClockAnchorIntervalSeconds,
                anchors.Messages[0].ServerUnixSeconds);
            CollectionAssert.AreEqual(new[] { 11, 11 }, _mirror.ServerSendTargetsOf<CoreAiServerClockMessage>(),
                "the one at the acknowledgement and the periodic one; a connection that never "
                + "acknowledged may be a client with no handler for the anchor, so it gets none");
            Assert.AreEqual(0d, _bridge.ServerClockOffsetSeconds, "a server's own offset is zero");
            Assert.IsTrue(_bridge.IsServerClockSynchronized);
        }

        private void Admit(int connectionId, string actorId)
        {
            OfflineMirror.AdmitServerConnection(connectionId);
            _bridge.BindConnection(connectionId,
                new RbxNetworkPeer(actorId, "session-" + actorId, "conn-" + connectionId));
        }

        /// <summary>
        /// Admits a connection the way the session host does: bound, and joining until its client
        /// acknowledges readiness.
        /// </summary>
        private void Join(int connectionId, string actorId)
        {
            OfflineMirror.AdmitServerConnection(connectionId);
            _bridge.BindConnection(connectionId,
                new RbxNetworkPeer(actorId, "session-" + actorId, "conn-" + connectionId),
                awaitClientReady: true);
        }

        /// <summary>The wire ids of what the transport carried to one connection, Mirror's pings left out.</summary>
        private List<ushort> BridgeMessagesTo(int connectionId)
        {
            List<ushort> ids = new();
            foreach (OfflineMirror.SentMessage sent in _mirror.ServerSends)
            {
                if (sent.ConnectionId == connectionId && sent.MessageId != NetworkMessageId<NetworkPingMessage>.Id)
                {
                    ids.Add(sent.MessageId);
                }
            }

            return ids;
        }

        /// <summary>The message <c>RemoteFunction:InvokeClient(player, ...)</c> hands the bridge.</summary>
        private static RbxNetworkRequestMessage InvokeClientOf(string actorId)
        {
            return new RbxNetworkRequestMessage(
                new InstanceId(6UL),
                RbxNetworkDirection.ServerToClient,
                null,
                actorId,
                new byte[] { 2 });
        }

        /// <summary>An <c>UnreliableRemoteEvent:FireAllClients(...)</c>.</summary>
        private static RbxNetworkEventMessage UnreliableFireAllClients()
        {
            return new RbxNetworkEventMessage(
                new InstanceId(5UL),
                RbxNetworkDirection.ServerToAllClients,
                RbxNetworkReliability.UnreliableUnordered,
                null,
                null,
                new byte[] { 9 });
        }

        /// <summary>
        /// The message <c>RemoteEvent:FireServer(...)</c> hands the bridge from an actor in this process.
        /// </summary>
        private static RbxNetworkEventMessage FireServerFrom(string actorId)
        {
            return new RbxNetworkEventMessage(
                new InstanceId(5UL),
                RbxNetworkDirection.ClientToServer,
                RbxNetworkReliability.ReliableOrdered,
                actorId,
                null,
                new byte[] { 1 });
        }

        /// <summary>The message <c>RemoteEvent:FireClient(player, ...)</c> hands the bridge.</summary>
        private static RbxNetworkEventMessage FireClientTo(string actorId)
        {
            return new RbxNetworkEventMessage(
                new InstanceId(5UL),
                RbxNetworkDirection.ServerToClient,
                RbxNetworkReliability.ReliableOrdered,
                null,
                actorId,
                new byte[] { 1 });
        }

        /// <summary>The message <c>RemoteEvent:FireAllClients(...)</c> hands the bridge.</summary>
        private static RbxNetworkEventMessage FireAllClients()
        {
            return new RbxNetworkEventMessage(
                new InstanceId(5UL),
                RbxNetworkDirection.ServerToAllClients,
                RbxNetworkReliability.ReliableOrdered,
                null,
                null,
                new byte[] { 1 });
        }
    }
}
