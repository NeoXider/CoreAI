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

        private void Admit(int connectionId, string actorId)
        {
            OfflineMirror.AdmitServerConnection(connectionId);
            _bridge.BindConnection(connectionId,
                new RbxNetworkPeer(actorId, "session-" + actorId, "conn-" + connectionId));
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
