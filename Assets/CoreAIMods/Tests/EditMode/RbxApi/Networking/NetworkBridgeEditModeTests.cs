using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Networking
{
    /// <summary>Production-port coverage for the engine-free loopback network bridge.</summary>
    [TestFixture]
    public sealed class NetworkBridgeEditModeTests
    {
        private const string ActorId = "actor-loopback";

        [Test]
        public void BridgeTypes_LiveInTheEngineFreeInstancesAssembly()
        {
            Assert.AreSame(typeof(RbxInstance).Assembly, typeof(INetworkBridge).Assembly);
            Assert.AreSame(typeof(RbxInstance).Assembly, typeof(NullNetworkBridge).Assembly);
        }

        [Test]
        public void Loopback_DeliversCopiedBytePayloadThroughThePort()
        {
            INetworkBridge bridge = CreateBridge();
            List<RbxNetworkEventMessage> received = new();
            bridge.EventReceived += received.Add;
            byte[] payload = { 3, 5, 8 };
            RbxNetworkEventMessage message = Event(payload,
                RbxNetworkReliability.ReliableOrdered);
            payload[0] = 99;

            bridge.SendEvent(message);

            Assert.AreEqual(1, received.Count);
            Assert.AreEqual(ActorId, received[0].SenderActorId);
            Assert.AreEqual(RbxNetworkReliability.ReliableOrdered,
                received[0].Reliability);
            CollectionAssert.AreEqual(new byte[] { 3, 5, 8 }, received[0].Payload);
        }

        [Test]
        public void ReliableOrdered_PreservesSubmissionOrderWithinTheClass()
        {
            INetworkBridge bridge = CreateBridge();
            List<byte> received = new();
            bridge.EventReceived += message => received.Add(message.Payload[0]);

            for (byte value = 0; value < 16; value++)
            {
                bridge.SendEvent(Event(new byte[] { value },
                    RbxNetworkReliability.ReliableOrdered));
            }

            CollectionAssert.AreEqual(
                new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 },
                received);
        }

        [Test]
        public void UnreliableUnordered_CanDropAndReorder()
        {
            INetworkBridge droppingBridge = CreateBridge(
                unreliableBehavior: RbxNullNetworkUnreliableBehavior.DropAll);
            List<byte> droppedDelivery = new();
            droppingBridge.EventReceived += message =>
                droppedDelivery.Add(message.Payload[0]);
            droppingBridge.SendEvent(Event(new byte[] { 1 },
                RbxNetworkReliability.UnreliableUnordered));

            Assert.IsEmpty(droppedDelivery);

            INetworkBridge reorderingBridge = CreateBridge(
                unreliableBehavior: RbxNullNetworkUnreliableBehavior.ReverseAdjacentPairs);
            List<byte> reorderedDelivery = new();
            reorderingBridge.EventReceived += message =>
                reorderedDelivery.Add(message.Payload[0]);
            reorderingBridge.SendEvent(Event(new byte[] { 1 },
                RbxNetworkReliability.UnreliableUnordered));
            reorderingBridge.SendEvent(Event(new byte[] { 2 },
                RbxNetworkReliability.UnreliableUnordered));

            CollectionAssert.AreEqual(new byte[] { 2, 1 }, reorderedDelivery);
        }

        [Test]
        public void RateAdmission_AtLimitSucceedsThenRefusesWithActorAndReason()
        {
            INetworkBridge bridge = CreateBridge(maxClientRequestsPerSecond: 2);
            List<RbxNetworkEventMessage> received = new();
            bridge.EventReceived += received.Add;
            bridge.SendEvent(Event(new byte[] { 1 },
                RbxNetworkReliability.ReliableOrdered));
            bridge.SendEvent(Event(new byte[] { 2 },
                RbxNetworkReliability.UnreliableUnordered));

            RbxError error = Assert.Throws<RbxError>(() =>
                bridge.SendEvent(Event(new byte[] { 3 },
                    RbxNetworkReliability.ReliableOrdered)));

            Assert.AreEqual(RbxErrorCode.BudgetExceeded, error.Code);
            StringAssert.Contains("actor '" + ActorId + "'", error.RawMessage);
            StringAssert.Contains(
                "network request rate quota reached (limit 2 requests/s)", error.RawMessage);

            bridge.RegisterActor("actor-independent");
            bridge.SendEvent(Event(new byte[] { 4 }, RbxNetworkReliability.ReliableOrdered,
                "actor-independent"));

            Assert.AreEqual(3, received.Count);
        }

        [Test]
        public void DefaultRateAdmission_IsFiveHundredRequestsPerSecond()
        {
            NullNetworkBridge bridge = new();

            Assert.AreEqual(500, bridge.MaxClientRequestsPerSecond);
        }

        private static INetworkBridge CreateBridge(
            int maxClientRequestsPerSecond =
                NullNetworkBridge.DefaultMaxClientRequestsPerSecond,
            RbxNullNetworkUnreliableBehavior unreliableBehavior =
                RbxNullNetworkUnreliableBehavior.PassThrough)
        {
            NullNetworkBridge bridge = new(maxClientRequestsPerSecond, () => 0d,
                unreliableBehavior);
            bridge.RegisterActor(ActorId);
            return bridge;
        }

        private static RbxNetworkEventMessage Event(byte[] payload,
            RbxNetworkReliability reliability, string actorId = ActorId)
        {
            return new RbxNetworkEventMessage(
                new InstanceId(1UL),
                RbxNetworkDirection.ClientToServer,
                reliability,
                actorId,
                null,
                payload);
        }
    }
}
