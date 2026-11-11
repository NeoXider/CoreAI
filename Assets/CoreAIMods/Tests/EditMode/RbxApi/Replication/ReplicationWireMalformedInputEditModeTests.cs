using System;
using System.Collections.Generic;
using System.Text;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Replication;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Replication
{
    /// <summary>
    /// The decoder's promise to a client: a packet that is empty, oversized, truncated, of another
    /// version or malformed in any field is refused with a reason — never thrown, never half-applied,
    /// and never paid for with an allocation the packet's own bytes cannot back.
    /// </summary>
    [TestFixture]
    public sealed class ReplicationWireMalformedInputEditModeTests
    {
        private const string Golden =
            "CA 01 00 07 03 "
            + "31 14 06 02 01 04 50 61 72 74 "
            + "79 80 40 80 08 00 FF 80 00 00 00 80 3E "
            + "04 01 01 05 42 6C 6F 63 6B 02 01 04 01 02 48 70 01 00 00 00 00 00 00 24 40 06 01 03 4F 72 65 "
            + "22 02 00 01 01 03 "
            + "03 0D";

        [Test]
        public void Negative_TheGoldenItself_Decodes()
        {
            Assert.AreEqual(3, WireAssert.Decode(WireAssert.FromHex(Golden)).Plan.Operations.Count);
        }

        [Test]
        public void AnEmptyPacket_IsRefused()
        {
            Assert.AreEqual(ReplicationWireError.Empty, WireAssert.Refusal(Array.Empty<byte>()));
        }

        [Test]
        public void AnOversizedPacket_IsRefusedBeforeAnyFieldIsRead_AndCostsNoAllocation()
        {
            byte[] oversized = new byte[ReplicationWireFormat.DefaultMaxPacketBytes + 1];
            oversized[0] = ReplicationWireFormat.Magic;
            oversized[1] = ReplicationWireFormat.Version;
            ReplicationWireDecoder decoder = new();
            decoder.TryDecode(oversized, "alice", out _, out _, out _);

            long before = GC.GetAllocatedBytesForCurrentThread();
            bool decoded = decoder.TryDecode(oversized, "alice", out ReplicationWireBatch batch,
                out ReplicationWireError error, out string detail);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.IsFalse(decoded);
            Assert.IsNull(batch);
            Assert.AreEqual(ReplicationWireError.PacketTooLarge, error);
            StringAssert.Contains("65537", detail);
            Assert.Less(allocated, 1024L, "the refusal costs its message, not a parse");
            Assert.AreEqual(ReplicationWireError.PacketTooLarge,
                WireAssert.Refusal(WireAssert.FromHex(Golden), maxPacketBytes: 32),
                "a receiver's own smaller cap is honoured too");
        }

        [Test]
        public void ARemotePayloadABadMagicAndAnotherVersion_AreRefusedByTheirHeader()
        {
            Assert.AreEqual(ReplicationWireError.BadMagic,
                WireAssert.Refusal(Encoding.UTF8.GetBytes("[{\"$rbx\":\"Instance\",\"id\":\"5\"}]")));
            byte[] nextVersion = WireAssert.FromHex(Golden);
            nextVersion[1] = 2;
            Assert.AreEqual(ReplicationWireError.UnsupportedVersion, WireAssert.Refusal(nextVersion));
            byte[] reservedFlag = WireAssert.FromHex(Golden);
            reservedFlag[2] = 0x02;
            Assert.AreEqual(ReplicationWireError.ReservedBits, WireAssert.Refusal(reservedFlag));
        }

        [Test]
        public void EveryTruncation_IsRefused_AndNoneThrows()
        {
            byte[] golden = WireAssert.FromHex(Golden);
            for (int length = 1; length < golden.Length; length++)
            {
                byte[] cut = new byte[length];
                Array.Copy(golden, cut, length);
                ReplicationWireError error = WireAssert.Refusal(cut);
                Assert.AreNotEqual(ReplicationWireError.None, error, "cut at " + length);
            }
        }

        [Test]
        public void EverySingleByteCorruption_IsDecodedOrRefused_NeverThrown()
        {
            byte[] golden = WireAssert.FromHex(Golden);
            ReplicationWireDecoder decoder = new();
            int refused = 0;
            for (int position = 0; position < golden.Length; position++)
            {
                for (int value = 0; value < 256; value++)
                {
                    byte[] corrupt = (byte[])golden.Clone();
                    corrupt[position] = (byte)value;
                    bool decoded = decoder.TryDecode(corrupt, "alice", out ReplicationWireBatch batch,
                        out ReplicationWireError error, out string detail);
                    Assert.AreEqual(decoded, batch != null);
                    Assert.AreEqual(decoded, error == ReplicationWireError.None, detail);
                    if (!decoded)
                    {
                        refused++;
                        StringAssert.DoesNotContain("the decoder failed", detail,
                            "a corruption at byte " + position + " reached an unchecked read");
                    }
                }
            }

            int total = golden.Length * 256;
            TestContext.WriteLine(refused + " of " + total + " single-byte corruptions were refused; the rest "
                                  + "changed a value inside a valid field");
            Assert.Greater(refused, 0);
            Assert.Less(refused, total, "a corrupted payload byte is still a well-formed packet");
        }

        [Test]
        public void RandomBytes_AreNeverThrown()
        {
            Random random = new(7);
            ReplicationWireDecoder decoder = new();
            for (int round = 0; round < 5000; round++)
            {
                byte[] noise = new byte[random.Next(1, 96)];
                random.NextBytes(noise);
                if (round % 2 == 0)
                {
                    noise[0] = ReplicationWireFormat.Magic;
                    if (noise.Length > 1)
                    {
                        noise[1] = ReplicationWireFormat.Version;
                    }
                }

                bool decoded = decoder.TryDecode(noise, "alice", out ReplicationWireBatch batch,
                    out ReplicationWireError error, out string detail);
                Assert.AreEqual(decoded, batch != null);
                if (!decoded)
                {
                    StringAssert.DoesNotContain("the decoder failed", detail);
                }
            }
        }

        [Test]
        public void AClaimedHugeCount_IsRefusedBeforeAnythingIsSizedByIt()
        {
            byte[] claim = WireAssert.FromHex("CA 01 00 01 FF FF FF FF 07 03 02");
            ReplicationWireDecoder decoder = new();
            decoder.TryDecode(claim, "alice", out _, out _, out _);

            long before = GC.GetAllocatedBytesForCurrentThread();
            bool decoded = decoder.TryDecode(claim, "alice", out _, out ReplicationWireError error, out _);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.IsFalse(decoded);
            Assert.AreEqual(ReplicationWireError.CountExceedsPacket, error);
            Assert.Less(allocated, 1024L, "two billion claimed records cost nothing but the refusal");
            Assert.AreEqual(ReplicationWireError.Truncated,
                WireAssert.Refusal(WireAssert.FromHex("CA 01 00 01 01 31 14 06 02 01 FF FF FF 07")),
                "a string claiming two gigabytes is refused the same way");
        }

        [Test]
        public void ALargePacketOfRecords_AllocatesInProportionToWhatItCarries()
        {
            List<ReplicationOperation> operations = new();
            for (int index = 1; index <= 20000; index++)
            {
                operations.Add(new ReplicationOperation(ReplicationOperationKind.Remove, new InstanceId((ulong)index), 0L));
            }

            byte[] packet = new ReplicationWireEncoder().EncodeToArray(
                new ReplicationBatchPlan("alice", 1L, operations), new FakeWireSource());
            byte[] lastRecordBroken = (byte[])packet.Clone();
            lastRecordBroken[lastRecordBroken.Length - 2] = 0x04;
            byte[] cut = new byte[packet.Length - 1];
            Array.Copy(packet, cut, cut.Length);
            ReplicationWireDecoder decoder = new();
            decoder.TryDecode(lastRecordBroken, "alice", out _, out _, out _);

            long before = GC.GetAllocatedBytesForCurrentThread();
            decoder.TryDecode(lastRecordBroken, "alice", out _, out ReplicationWireError error, out _);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.AreEqual(ReplicationWireError.UnknownRecordKind, error, "refused at its very last record");
            TestContext.WriteLine("a " + packet.Length + "-byte packet refused at its last record allocated "
                                  + allocated + " bytes");
            Assert.Less(allocated, 64L * packet.Length, "the cost of a refused packet is bounded by its length");
            Assert.AreEqual(ReplicationWireError.CountExceedsPacket, WireAssert.Refusal(cut),
                "one byte short, the record count is refused before the first record");
        }

        [Test]
        public void MalformedFields_AreRefusedWithTheirOwnReason()
        {
            Dictionary<string, ReplicationWireError> cases = new()
            {
                ["CA 01 00 00 00"] = ReplicationWireError.InvalidSequence,
                ["CA 01 00 80 00 00"] = ReplicationWireError.MalformedVarint,
                ["CA 01 00 01 01 04 02"] = ReplicationWireError.UnknownRecordKind,
                ["CA 01 00 01 01 00 02"] = ReplicationWireError.UnknownRecordKind,
                ["CA 01 00 01 01 43 02"] = ReplicationWireError.ReservedBits,
                ["CA 01 00 01 01 13 02"] = ReplicationWireError.ReservedBits,
                ["CA 01 00 01 01 03 00"] = ReplicationWireError.InvalidInstanceId,
                ["CA 01 00 01 01 03 01"] = ReplicationWireError.InvalidInstanceId,
                ["CA 01 00 01 01 02 02 01"] = ReplicationWireError.InvalidRevision,
                ["CA 01 00 01 01 01 02 00 80 80 80 80 80 80 80 80 80 01 01 01 50"] = ReplicationWireError.InvalidInstanceId,
                ["CA 01 00 01 01 01 02 00 00 00"] = ReplicationWireError.MalformedString,
                ["CA 01 00 01 01 01 02 00 00 05"] = ReplicationWireError.MalformedString,
                ["CA 01 00 01 01 01 02 00 00 01 02 C3 28"] = ReplicationWireError.MalformedString,
                ["CA 01 00 01 01 12 02 00 00"] = ReplicationWireError.ReservedBits,
                ["CA 01 00 01 01 12 02 00 80 01"] = ReplicationWireError.ReservedBits,
                ["CA 01 00 01 01 12 02 00 02"] = ReplicationWireError.ReservedBits,
                ["CA 01 00 01 01 12 02 00 40"] = ReplicationWireError.ReservedBits,
                ["CA 01 00 01 01 12 02 00 07 00 00 00 3F"] = ReplicationWireError.MalformedCFrame,
                ["CA 01 00 01 01 12 02 00 07 00 00 00 1B"] = ReplicationWireError.MalformedCFrame,
                ["CA 01 00 01 01 12 02 00 03 00 00 00 FF FF FF FF FF 7F"] = ReplicationWireError.MalformedCFrame,
                ["CA 01 00 01 01 12 02 00 01 FE FF FF FF 1F 00 00"] = ReplicationWireError.MalformedCFrame,
                ["CA 01 00 01 01 22 02 00 00"] = ReplicationWireError.MalformedValue,
                ["CA 01 00 01 01 22 02 00 01 7F 00"] = ReplicationWireError.UnknownMemberTag,
                ["CA 01 00 01 01 22 02 00 01 20 01 01 61 02"] = ReplicationWireError.UnknownMemberTag,
                ["CA 01 00 01 01 22 02 00 01 02 02"] = ReplicationWireError.MalformedValue,
                ["CA 01 00 01 01 22 02 00 01 03 80 80 80 80 80 80 80 80 80 01"] = ReplicationWireError.InvalidInstanceId,
                ["CA 01 00 01 01 22 02 00 01 08 09"] = ReplicationWireError.MalformedValue,
                ["CA 01 00 01 01 22 02 00 01 04 01 01 61 07"] = ReplicationWireError.MalformedValue,
                ["CA 01 00 01 01 22 02 00 01 01 00"] = ReplicationWireError.MalformedString,
                ["CA 01 00 01 01 22 02 00 01 0D 01 00"] = ReplicationWireError.MalformedString,
                ["CA 01 00 01 01 03 02 00"] = ReplicationWireError.TrailingBytes,
                ["CA 01 00 01 02 03 02"] = ReplicationWireError.CountExceedsPacket
            };

            foreach (KeyValuePair<string, ReplicationWireError> malformed in cases)
            {
                Assert.AreEqual(malformed.Value, WireAssert.Refusal(WireAssert.FromHex(malformed.Key)),
                    "packet " + malformed.Key);
            }
        }

        [Test]
        public void Negative_TheWellFormedTwinsOfTheMalformedFields_Decode()
        {
            string[] wellFormed =
            {
                "CA 01 00 01 00",
                "CA 01 00 01 01 03 02",
                "CA 01 00 01 01 02 02 00",
                "CA 01 00 01 01 12 02 00 07 00 00 00 15",
                "CA 01 00 01 01 22 02 00 01 02 01",
                "CA 01 00 01 01 22 02 00 01 01 01 01 61",
                "CA 01 00 01 01 22 02 00 01 0D 01 01 61"
            };

            foreach (string packet in wellFormed)
            {
                WireAssert.Decode(WireAssert.FromHex(packet));
            }
        }

        [Test]
        public void ARefusedPacket_ReachesNoReplica_AndTheNextGoodOneIsAGap()
        {
            using WireClient client = new("alice");
            ReplicationWireEncoder encoder = new();
            FakeWireSource source = new();
            source.Nodes[1UL] = new InstanceSnapshot { Id = 1UL, ClassName = "DataModel", Name = "Game" };
            byte[] first = encoder.EncodeToArray(new ReplicationBatchPlan("alice", 1L, new List<ReplicationOperation>
            {
                new(ReplicationOperationKind.Spawn, new InstanceId(1UL), 1L, new[] { "Name" })
            }), source, isWorld: true);
            byte[] third = encoder.EncodeToArray(new ReplicationBatchPlan("alice", 3L, new List<ReplicationOperation>()),
                source);

            client.Deliver(new SentPacketStub(first).Packet);
            ReplicationReceiveResult refused = client.Receiver.Receive(WireAssert.FromHex("CA 01 00 02 01 07 02"));
            client.Deliver(new SentPacketStub(third).Packet);

            Assert.IsTrue(client.Results[0].IsApplied, client.Results[0].Applied?.ToString());
            Assert.IsTrue(refused.IsRejected);
            Assert.AreEqual(ReplicationWireError.UnknownRecordKind, refused.Rejection);
            Assert.AreEqual(1L, client.Receiver.RejectedPackets);
            Assert.AreEqual(ReplicationApplyStatus.GapDetected, client.LastResult.Applied.Status);
            Assert.AreEqual(1, client.ResyncRequests.Count, "the lost sequence is asked for once, by the applier");
            Assert.IsTrue(client.Diagnostics.Exists(line => line.Contains("refused a replication packet")));
        }

        /// <summary>Wraps raw bytes as a delivered packet for a client fed by hand.</summary>
        private sealed class SentPacketStub
        {
            public SentPacketStub(byte[] bytes)
            {
                Packet = new SentPacket(new ReplicationOutgoingPacket("alice", 0L, ReplicationPacketKind.Delta, 0,
                    new ArraySegment<byte>(bytes)));
            }

            public SentPacket Packet { get; }
        }
    }
}
