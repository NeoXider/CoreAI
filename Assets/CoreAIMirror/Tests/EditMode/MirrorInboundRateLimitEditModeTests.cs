using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using Mirror;
using NUnit.Framework;

namespace CoreAI.Net.Mirror.Tests
{
    /// <summary>
    /// The server's inbound limit: what one admitted connection may send before its remotes are
    /// dropped and counted, ahead of the world's decoding and dispatch.
    /// </summary>
    /// <remarks>
    /// WHY a flood is pushed through the bridge's receive path and not a client bridge: an honest
    /// client bridge limits its own sends, and the limit under test exists for the client that
    /// does not.
    /// </remarks>
    [TestFixture]
    public sealed class MirrorInboundRateLimitEditModeTests
    {
        private const int Flooder = 11;
        private const int Neighbour = 12;

        private OfflineMirror _mirror;
        private MirrorNetworkBridge _bridge;
        private List<string> _log;
        private List<RbxNetworkEventMessage> _delivered;
        private double _now;

        [SetUp]
        public void StartServer()
        {
            _mirror = new OfflineMirror();
            _now = 0d;
            _log = new List<string>();
            _delivered = new List<RbxNetworkEventMessage>();
            OfflineMirror.StartServer();
        }

        [TearDown]
        public void RestoreMirror()
        {
            OfflineMirror.RunAll(
                () => _bridge?.Dispose(),
                () => _mirror.Dispose());
        }

        [Test]
        public void Meter_AdmitsItsBurst_ThenOnlyItsRate()
        {
            CoreAiMirrorInboundMeter meter = new(Limits(messagesPerSecond: 10f, burstSeconds: 1f));
            int admitted = 0;
            for (int index = 0; index < 25; index++)
            {
                admitted += meter.TryAdmit(1, 10, 0d) ? 1 : 0;
            }

            Assert.AreEqual(10, admitted, "a full bucket admits one burst");
            Assert.IsFalse(meter.TryAdmit(1, 10, 0.05d), "half a remote's worth of time admits nothing");
            Assert.IsTrue(meter.TryAdmit(1, 10, 0.25d), "a fifth of a second more at 10/s refills two");
            Assert.IsTrue(meter.TryAdmit(1, 10, 0.25d));
            Assert.IsFalse(meter.TryAdmit(1, 10, 0.25d));
            Assert.IsTrue(meter.TryGetTraffic(1, out CoreAiMirrorInboundTraffic traffic));
            Assert.AreEqual(12, traffic.MessagesAccepted);
            Assert.AreEqual(17, traffic.MessagesDropped);
            Assert.AreEqual(120L, traffic.BytesAccepted);
            Assert.AreEqual(17, meter.MessagesDropped);
        }

        [Test]
        public void Meter_BudgetsBytesToo_AndAlwaysFitsOneLargestPayloadWhenFull()
        {
            CoreAiMirrorInboundMeter meter = new(Limits(bytesPerSecond: 1000f, burstSeconds: 1f));

            Assert.IsTrue(meter.TryAdmit(1, MirrorNetworkBridge.CodecPayloadCeilingBytes, 0d),
                "a full bucket admits the largest payload a remote may carry, whatever the byte rate");
            Assert.IsFalse(meter.TryAdmit(1, 1, 0d), "and then it is empty");
            Assert.IsTrue(meter.TryAdmit(1, 500, 0.5d), "half a second at 1000 B/s refills 500 bytes");
            Assert.IsTrue(meter.TryGetTraffic(1, out CoreAiMirrorInboundTraffic traffic));
            Assert.AreEqual(MirrorNetworkBridge.CodecPayloadCeilingBytes + 500L, traffic.BytesAccepted);
            Assert.AreEqual(1L, traffic.BytesDropped);
        }

        [Test]
        public void Meter_AClockThatStepsBack_NeitherRefillsNorFreezes()
        {
            CoreAiMirrorInboundMeter meter = new(Limits(messagesPerSecond: 1f, burstSeconds: 1f));
            Assert.IsTrue(meter.TryAdmit(1, 0, 100d));

            Assert.IsFalse(meter.TryAdmit(1, 0, 50d), "a step back grants no time that never passed");
            Assert.IsTrue(meter.TryAdmit(1, 0, 51d), "and the bucket refills from the new reading on");
        }

        [Test]
        public void Meter_PerConnection_AndForgottenConnectionsStartFull()
        {
            CoreAiMirrorInboundMeter meter = new(Limits(messagesPerSecond: 1f, burstSeconds: 1f));
            Assert.IsTrue(meter.TryAdmit(1, 0, 0d));
            Assert.IsFalse(meter.TryAdmit(1, 0, 0d));

            Assert.IsTrue(meter.TryAdmit(2, 0, 0d), "one connection's flood costs another nothing");
            meter.Forget(1);
            Assert.IsTrue(meter.TryAdmit(1, 0, 0d), "the next peer on a reused id starts with a full bucket");
            Assert.IsFalse(meter.TryGetTraffic(3, out _));
        }

        [Test]
        public void Meter_Disabled_AdmitsEverything()
        {
            CoreAiMirrorInboundMeter meter = new(CoreAiMirrorInboundLimits.Unlimited);
            for (int index = 0; index < 10000; index++)
            {
                Assert.IsTrue(meter.TryAdmit(1, 65536, 0d));
            }

            Assert.AreEqual(0, meter.MessagesDropped);
        }

        [Test]
        public void Limits_RefuseNonsense_AndTheDefaultsCoverAnHonestClientsBudget()
        {
            CoreAiMirrorInboundLimits limits = new();
            Assert.Throws<ArgumentOutOfRangeException>(() => limits.MessagesPerSecond = 0f);
            Assert.Throws<ArgumentOutOfRangeException>(() => limits.BytesPerSecond = -1f);
            Assert.Throws<ArgumentOutOfRangeException>(() => limits.BurstSeconds = float.NaN);
            Assert.Throws<ArgumentOutOfRangeException>(() => limits.MessagesPerSecond = float.PositiveInfinity);
            Assert.IsTrue(limits.Enabled);
            Assert.GreaterOrEqual(limits.MessageCapacity,
                2d * 3d * RbxNetworkRateLimiter.DefaultMaxClientRequestsPerSecond,
                "an honest client fills three remote kinds' whole-second windows twice across a window edge");
        }

        [Test]
        public void NormalTraffic_WithinTheDefaults_IsDeliveredWhole_AndNothingIsDroppedOrSaid()
        {
            CreateBridge(null);
            Bind(Flooder, "actor-a");

            for (int index = 0; index < 1000; index++)
            {
                _bridge.ReceiveServerEvent(Flooder, Wire(1));
            }

            Assert.AreEqual(1000, _delivered.Count);
            Assert.AreEqual(0, _bridge.InboundMessagesDropped);
            CollectionAssert.IsEmpty(_log);
        }

        [Test]
        public void Negative_AFlood_IsDroppedAndCountedBeforeDispatch_SaidOnce_AndCostsNoNeighbourAnything()
        {
            CreateBridge(Limits(messagesPerSecond: 100f, burstSeconds: 1f));
            Bind(Flooder, "actor-a");
            Bind(Neighbour, "actor-b");

            for (int index = 0; index < 10000; index++)
            {
                _bridge.ReceiveServerEvent(Flooder, Wire(1));
            }

            _bridge.ReceiveServerEvent(Neighbour, Wire(1));

            Assert.AreEqual(101, _delivered.Count, "the flooder's burst, then nothing, and the neighbour's one");
            Assert.AreEqual("actor-b", _delivered[100].SenderActorId);
            Assert.AreEqual(9900, _bridge.InboundMessagesDropped);
            Assert.AreEqual(9900L, _bridge.InboundBytesDropped);
            Assert.IsTrue(_bridge.TryGetInboundTraffic(Flooder, out CoreAiMirrorInboundTraffic flooder));
            Assert.AreEqual(100, flooder.MessagesAccepted);
            Assert.AreEqual(9900, flooder.MessagesDropped);
            Assert.IsTrue(_bridge.TryGetInboundTraffic(Neighbour, out CoreAiMirrorInboundTraffic neighbour));
            Assert.AreEqual(0, neighbour.MessagesDropped);
            Assert.AreEqual(1, _log.Count, "a flood is said once, not ten thousand times");
            StringAssert.Contains("actor-a", _log[0]);
            Assert.AreEqual(101, _bridge.PacketsDelivered, "a dropped remote is never counted as delivered");

            _now = 1d;
            for (int index = 0; index < 150; index++)
            {
                _bridge.ReceiveServerEvent(Flooder, Wire(1));
            }

            Assert.AreEqual(201, _delivered.Count, "a second later the rate admits its next hundred");
            Assert.AreEqual(1, _log.Count, "within the interval the drops are counted, not said");

            _now = 1d + MirrorNetworkBridge.InboundDropLogIntervalSeconds;
            _bridge.ReceiveServerEvent(Flooder, Wire(1));
            for (int index = 0; index < 200; index++)
            {
                _bridge.ReceiveServerEvent(Flooder, Wire(1));
            }

            Assert.AreEqual(2, _log.Count, "a flood that lasts is said again once per interval");
        }

        [Test]
        public void Negative_AFloodThroughMirrorsRealHandler_IsDropped_AndTheConnectionStays()
        {
            CreateBridge(Limits(messagesPerSecond: 5f, burstSeconds: 1f));
            OfflineMirror.AdmitServerConnection(Flooder);
            Bind(Flooder, "actor-a");

            for (int index = 0; index < 20; index++)
            {
                OfflineMirror.DeliverToServer(Flooder, OfflineMirror.Event(1UL));
            }

            Assert.AreEqual(5, _delivered.Count);
            Assert.AreEqual(15, _bridge.InboundMessagesDropped);
            CollectionAssert.IsEmpty(_mirror.ServerDisconnectRequests,
                "a drop is the bridge's answer, not a disconnect: nothing was thrown into Mirror's handler");
            Assert.IsTrue(NetworkServer.connections.ContainsKey(Flooder));
        }

        [Test]
        public void Negative_ARemoteFunctionCallOverTheLimit_IsAnsweredWithAFailure_AndNeverReachesTheWorld()
        {
            CreateBridge(Limits(messagesPerSecond: 1f, burstSeconds: 1f));
            OfflineMirror.AdmitServerConnection(Flooder);
            Bind(Flooder, "actor-a");
            int asked = 0;
            _bridge.RequestReceived += (_, responder) =>
            {
                asked++;
                responder.Complete(new byte[] { 1 });
            };
            using SentMessages<CoreAiRemoteResponseMessage> answers = new();

            _bridge.ReceiveServerRequest(Flooder, Request(1u, 1));
            _bridge.ReceiveServerRequest(Flooder, Request(2u, 1));

            Assert.AreEqual(1, asked, "the call over the limit never reached the world");
            Assert.AreEqual(2, answers.Messages.Count, "and its caller is answered now, not after the timeout");
            Assert.IsTrue(answers.Messages[0].Success);
            Assert.IsFalse(answers.Messages[1].Success);
            Assert.AreEqual(2u, answers.Messages[1].CorrelationId);
            Assert.AreEqual(MirrorNetworkBridge.InboundLimitedReason, answers.Messages[1].ErrorMessage);
        }

        [Test]
        public void Negative_APayloadLargerThanItsChannel_IsDroppedAndCounted_WhateverTheRates()
        {
            CreateBridge(CoreAiMirrorInboundLimits.Unlimited);
            Bind(Flooder, "actor-a");
            CoreAiRemoteEventMessage unreliable = Wire(MirrorNetworkBridge.UnreliablePayloadCeilingBytes + 1);
            unreliable.Reliability = (byte)RbxNetworkReliability.UnreliableUnordered;
            CoreAiRemoteEventMessage unreliableAtTheCeiling = Wire(MirrorNetworkBridge.UnreliablePayloadCeilingBytes);
            unreliableAtTheCeiling.Reliability = (byte)RbxNetworkReliability.UnreliableUnordered;

            _bridge.ReceiveServerEvent(Flooder, unreliable);
            _bridge.ReceiveServerEvent(Flooder, Wire(MirrorNetworkBridge.CodecPayloadCeilingBytes + 1));
            _bridge.ReceiveServerEvent(Flooder, unreliableAtTheCeiling);
            _bridge.ReceiveServerEvent(Flooder, Wire(MirrorNetworkBridge.CodecPayloadCeilingBytes));

            Assert.AreEqual(2, _bridge.InboundOversizeDropped);
            Assert.AreEqual(2, _delivered.Count, "a payload at its channel's ceiling is delivered");
            Assert.AreEqual(1, _log.Count, "said once");
        }

        [Test]
        public void Negative_AnOversizeRemoteFunctionCall_IsAnsweredWithAFailureThatNamesTheSize()
        {
            CreateBridge(CoreAiMirrorInboundLimits.Unlimited);
            OfflineMirror.AdmitServerConnection(Flooder);
            Bind(Flooder, "actor-a");
            int asked = 0;
            _bridge.RequestReceived += (_, _) => asked++;
            using SentMessages<CoreAiRemoteResponseMessage> answers = new();

            _bridge.ReceiveServerRequest(Flooder, Request(3u, MirrorNetworkBridge.CodecPayloadCeilingBytes + 1));

            Assert.AreEqual(0, asked);
            Assert.AreEqual(1, _bridge.InboundOversizeDropped);
            Assert.AreEqual(1, answers.Messages.Count);
            Assert.IsFalse(answers.Messages[0].Success);
            StringAssert.Contains((MirrorNetworkBridge.CodecPayloadCeilingBytes + 1).ToString(), answers.Messages[0].ErrorMessage);
        }

        [Test]
        public void ARebindOrARelease_ForgetsTheConnectionsMeter()
        {
            CreateBridge(Limits(messagesPerSecond: 1f, burstSeconds: 1f));
            Bind(Flooder, "actor-a");
            _bridge.ReceiveServerEvent(Flooder, Wire(1));
            _bridge.ReceiveServerEvent(Flooder, Wire(1));
            Assert.AreEqual(1, _bridge.InboundMessagesDropped);

            _bridge.NotifyDisconnected(Flooder, RbxNetworkDisconnectReason.TransportLost);
            Assert.IsFalse(_bridge.TryGetInboundTraffic(Flooder, out _), "a released connection's meter is gone");
            Bind(Flooder, "actor-c");
            _bridge.ReceiveServerEvent(Flooder, Wire(1));

            Assert.AreEqual(2, _delivered.Count, "the next peer on the id starts with a full bucket");
            Assert.AreEqual(1, _bridge.InboundMessagesDropped, "the total stays for the host to read");
            Assert.AreEqual(1, _bridge.GetInboundTraffic().Count);
        }

        [Test]
        public void TheLimitsABridgeWasBuiltWith_AreACopy()
        {
            CoreAiMirrorInboundLimits limits = Limits(messagesPerSecond: 3f, burstSeconds: 1f);
            CreateBridge(limits);

            limits.MessagesPerSecond = 1000f;
            _bridge.InboundLimits.MessagesPerSecond = 1000f;

            Assert.AreEqual(3f, _bridge.InboundLimits.MessagesPerSecond,
                "editing the options after the bridge was built changes nothing it enforces");
        }

        private void CreateBridge(CoreAiMirrorInboundLimits limits)
        {
            _bridge = new MirrorNetworkBridge(isServer: true, clockSeconds: () => _now, log: _log.Add,
                inboundLimits: limits);
            _bridge.EventReceived += _delivered.Add;
        }

        private void Bind(int connectionId, string actorId)
        {
            _bridge.BindConnection(connectionId, new RbxNetworkPeer(actorId, "session-" + actorId,
                connectionId.ToString()));
        }

        private static CoreAiMirrorInboundLimits Limits(float messagesPerSecond = 1000f,
            float bytesPerSecond = 1048576f, float burstSeconds = 1f)
        {
            return new CoreAiMirrorInboundLimits
            {
                MessagesPerSecond = messagesPerSecond,
                BytesPerSecond = bytesPerSecond,
                BurstSeconds = burstSeconds
            };
        }

        private static CoreAiRemoteEventMessage Wire(int payloadBytes)
        {
            return new CoreAiRemoteEventMessage
            {
                RemoteId = 7UL,
                Direction = (byte)RbxNetworkDirection.ClientToServer,
                Reliability = (byte)RbxNetworkReliability.ReliableOrdered,
                Payload = new byte[payloadBytes]
            };
        }

        private static CoreAiRemoteRequestMessage Request(uint correlationId, int payloadBytes)
        {
            return new CoreAiRemoteRequestMessage
            {
                RemoteId = 9UL,
                Direction = (byte)RbxNetworkDirection.ClientToServer,
                CorrelationId = correlationId,
                Payload = new byte[payloadBytes]
            };
        }
    }
}
