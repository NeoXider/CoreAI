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

        private OfflineMirror _mirror;
        private MirrorNetworkBridge _bridge;

        [SetUp]
        public void StartServerWithAStrangerConnected()
        {
            _mirror = new OfflineMirror();
            _bridge = new MirrorNetworkBridge(isServer: true, clockSeconds: () => 0d);
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
            Admit(11, "actor-a");

            _bridge.SendEvent(FireAllClients());
            _mirror.FlushServer();

            List<int> pinged = _mirror.ServerSendTargetsOf<NetworkPingMessage>();
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

        private void Admit(int connectionId, string actorId)
        {
            OfflineMirror.AdmitServerConnection(connectionId);
            _bridge.BindConnection(connectionId,
                new RbxNetworkPeer(actorId, "session-" + actorId, "conn-" + connectionId));
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
