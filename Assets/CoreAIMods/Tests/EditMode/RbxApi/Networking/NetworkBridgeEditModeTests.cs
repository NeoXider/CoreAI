using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using CoreAI.Mods.Rbx.Instances.Scheduling;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Networking
{
    /// <summary>Production-port coverage for the engine-free loopback network bridge.</summary>
    [TestFixture]
    public sealed class NetworkBridgeEditModeTests
    {
        private const string ActorId = "actor-loopback";

        private sealed class UnusedThreadFactory : IRbxScriptThreadFactory
        {
            public IRbxScriptThread Create(string ownerModId, object callable)
            {
                throw new InvalidOperationException(
                    "The networking signal probe must not create script threads.");
            }
        }

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
            bridge.EventReceived += message =>
            {
                byte value = message.Payload[0];
                received.Add(value);
                if (value == 0)
                {
                    bridge.SendEvent(Event(new byte[] { 1 },
                        RbxNetworkReliability.ReliableOrdered));
                    bridge.SendEvent(Event(new byte[] { 2 },
                        RbxNetworkReliability.ReliableOrdered));
                }
            };

            bridge.SendEvent(Event(new byte[] { 0 },
                RbxNetworkReliability.ReliableOrdered));
            for (byte value = 3; value < 16; value++)
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
        public void RateAdmission_IsPerActorAndRemoteTypeThenRefusesWithActorAndReason()
        {
            INetworkBridge bridge = CreateBridge(maxClientRequestsPerSecond: 2);
            List<RbxNetworkEventMessage> received = new();
            bridge.EventReceived += received.Add;
            bridge.SendEvent(Event(new byte[] { 1 },
                RbxNetworkReliability.ReliableOrdered));
            bridge.SendEvent(Event(new byte[] { 2 },
                RbxNetworkReliability.ReliableOrdered));
            bridge.SendEvent(Event(new byte[] { 3 },
                RbxNetworkReliability.UnreliableUnordered));
            bridge.SendEvent(Event(new byte[] { 4 },
                RbxNetworkReliability.UnreliableUnordered));
            bridge.SendRequest(Request(), _ => { });
            bridge.SendRequest(Request(), _ => { });

            RbxError reliableError = Assert.Throws<RbxError>(() =>
                bridge.SendEvent(Event(new byte[] { 5 },
                    RbxNetworkReliability.ReliableOrdered)));
            RbxError unreliableError = Assert.Throws<RbxError>(() =>
                bridge.SendEvent(Event(new byte[] { 6 },
                    RbxNetworkReliability.UnreliableUnordered)));
            RbxError functionError = Assert.Throws<RbxError>(() =>
                bridge.SendRequest(Request(), _ => { }));

            Assert.AreEqual(RbxErrorCode.BudgetExceeded, reliableError.Code);
            Assert.AreEqual(RbxErrorCode.BudgetExceeded, unreliableError.Code);
            Assert.AreEqual(RbxErrorCode.BudgetExceeded, functionError.Code);
            StringAssert.Contains("actor '" + ActorId + "'", reliableError.RawMessage);
            StringAssert.Contains(
                "network request rate quota reached (limit 2 requests/s)",
                reliableError.RawMessage);

            bridge.RegisterActor("actor-independent");
            bridge.SendEvent(Event(new byte[] { 7 }, RbxNetworkReliability.ReliableOrdered,
                "actor-independent"));

            Assert.AreEqual(5, received.Count);
        }

        [Test]
        public void DefaultRateAdmission_IsFiveHundredRequestsPerSecond()
        {
            NullNetworkBridge bridge = new();

            Assert.AreEqual(500, bridge.MaxClientRequestsPerSecond);
        }

        [Test]
        public void OnServerEvent_QueuesRealPlayerAsFirstArgument()
        {
            InstanceRegistry registry = new();
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            RbxPlayers players = (RbxPlayers)game.GetService("Players");
            RbxPlayer player = players.EnsureActor(registry, ActorId);
            RbxRemoteEvent remote = (RbxRemoteEvent)registry.Create("RemoteEvent");
            ModScheduler scheduler = new(
                new UnusedThreadFactory(), new RbxAccumulatingTimeSource());
            remote.AttachScheduler(scheduler);
            object[] received = null;
            remote.OnServerEvent.Connect((Action<object[]>)(arguments =>
                received = arguments));

            remote.DeliverToServer(player, new object[] { "payload", 7d });

            Assert.IsNull(received);
            scheduler.Advance(0.016d);
            Assert.IsNotNull(received);
            Assert.AreSame(player, received[0]);
            Assert.AreEqual("payload", received[1]);
            Assert.AreEqual(7d, received[2]);
        }

        [Test]
        public void RemoteFunction_PropagatesReturnPayloadAndReceiverError()
        {
            InstanceRegistry registry = new();
            RbxRemoteFunction remote = (RbxRemoteFunction)registry.Create("RemoteFunction");
            NullNetworkBridge returningBridge = (NullNetworkBridge)CreateBridge();
            returningBridge.RequestReceived +=
                (RbxNetworkRequestMessage message, RbxNetworkRequestResponder responder) =>
                {
                    Assert.AreEqual(remote.Id, message.RemoteId);
                    CollectionAssert.AreEqual(new byte[] { 1, 2 }, message.Payload);
                    responder.Complete(new byte[] { 8, 13 });
                };
            RbxNetworkResponse returned = null;

            remote.InvokeServer(returningBridge, ActorId, new byte[] { 1, 2 },
                response => returned = response);

            Assert.IsNotNull(returned);
            Assert.IsTrue(returned.Succeeded);
            CollectionAssert.AreEqual(new byte[] { 8, 13 }, returned.Payload);

            NullNetworkBridge failingBridge = (NullNetworkBridge)CreateBridge();
            failingBridge.RequestReceived +=
                (RbxNetworkRequestMessage message, RbxNetworkRequestResponder responder) =>
                    throw new InvalidOperationException("receiver exploded");
            RbxNetworkResponse failed = null;

            remote.InvokeServer(failingBridge, ActorId, Array.Empty<byte>(),
                response => failed = response);

            Assert.IsNotNull(failed);
            Assert.IsFalse(failed.Succeeded);
            Assert.AreEqual("receiver exploded", failed.Error);
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

        private static RbxNetworkRequestMessage Request(string actorId = ActorId)
        {
            return new RbxNetworkRequestMessage(
                new InstanceId(2UL),
                RbxNetworkDirection.ClientToServer,
                actorId,
                null,
                Array.Empty<byte>());
        }
    }
}
