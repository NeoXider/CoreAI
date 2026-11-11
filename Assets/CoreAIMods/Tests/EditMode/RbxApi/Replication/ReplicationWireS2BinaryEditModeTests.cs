using System;
using System.Collections.Generic;
using System.Diagnostics;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Replication;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Replication
{
    /// <summary>
    /// Spike S2, binary leg: bytes per CFrame patch on the wire, measured the way the MVP6 DoD states
    /// it (a server moving 200 parts at 20 Hz), plus the encode and decode cost per patch. The JSON leg
    /// runs through the remote codec in <c>ReplicationWireS2JsonEditModeTests</c>; both write their
    /// numbers to the test output and <c>dev-docs/REPLICATION_CODEC.md</c> records them.
    /// </summary>
    /// <remarks>
    /// WHY only the byte bar is asserted and the times are only printed: bytes are a property of the
    /// format and the same on every machine; a time is a property of the machine, and a timing assert
    /// is a flaky test.
    /// </remarks>
    [TestFixture]
    public sealed class ReplicationWireS2BinaryEditModeTests
    {
        private const double DraftBarBytesPerPatch = 24d;
        private const int PartCount = 200;
        private const int Ticks = 100;

        [Test]
        public void S2_TwoHundredPartsMovingAt20Hz_AverageAtMost24BytesPerCFramePatch()
        {
            double average = MeasureMovingParts(free: true, out int patches, out long bytes);

            TestContext.WriteLine("S2 binary, free motion: " + patches + " CFrame patches in " + bytes
                                  + " bytes on the wire = " + average.ToString("F2") + " B/patch");
            Assert.AreEqual(PartCount * Ticks, patches, "every part moved every tick, one patch each");
            Assert.LessOrEqual(average, DraftBarBytesPerPatch);
        }

        [Test]
        public void S2_TwoHundredBlocksSteppingOnAGrid_TravelExactlyAndSmaller()
        {
            double average = MeasureMovingParts(free: false, out int patches, out long bytes);

            TestContext.WriteLine("S2 binary, grid blocks with axis-aligned turns: " + patches + " patches in "
                                  + bytes + " bytes = " + average.ToString("F2") + " B/patch");
            Assert.LessOrEqual(average, 16d, "a block world needs no rotation quantization at all");
        }

        [Test]
        public void S2_OneCFramePatchAlone_InItsOwnPacket()
        {
            FakeWireSource source = new();
            source.Parts[1234UL] = ReplicationPartState.Of(
                RbxCFrame.FromQuaternion(312.25f, 18.5f, -97.125f, 0.1f, 0.6f, -0.2f, 0.77f),
                new RbxColor3(0f, 0f, 0f), 0f, false);
            ReplicationBatchPlan plan = new("alice", 5000L, new List<ReplicationOperation>
            {
                new(ReplicationOperationKind.Patch, new InstanceId(1234UL), 4321L, new[] { ReplicationPartMembers.CFrame })
            });

            byte[] packet = new ReplicationWireEncoder().EncodeToArray(plan, source);

            TestContext.WriteLine("S2 binary, one patch alone: " + packet.Length + " bytes (" + WireAssert.Hex(packet) + ")");
            Assert.AreEqual(27, packet.Length, "6 header + head 1 + id 2 + revision 2 + mask 1 + position 9 + rotation 6");
        }

        [Test]
        public void S2_EncodeAndDecodeCostPerCFramePatch()
        {
            FakeWireSource source = new();
            List<ReplicationOperation> operations = new();
            Random random = new(11);
            for (int index = 1; index <= PartCount; index++)
            {
                source.Parts[(ulong)index + 500UL] = ReplicationPartState.Of(RandomCFrame(random),
                    new RbxColor3(0f, 0f, 0f), 0f, false);
                operations.Add(new ReplicationOperation(ReplicationOperationKind.Patch,
                    new InstanceId((ulong)index + 500UL), 2000L + index, new[] { ReplicationPartMembers.CFrame }));
            }

            ReplicationBatchPlan plan = new("alice", 77L, operations);
            ReplicationWireEncoder encoder = new();
            ReplicationWireDecoder decoder = new();
            byte[] packet = encoder.EncodeToArray(plan, source);
            const int rounds = 500;
            for (int warm = 0; warm < 200; warm++)
            {
                encoder.Encode(plan, source);
                decoder.TryDecode(packet, "alice", out _, out _, out _);
            }

            Stopwatch encodeClock = new();
            Stopwatch decodeClock = new();
            long encodeAllocated = GC.GetAllocatedBytesForCurrentThread();
            encodeClock.Start();
            for (int round = 0; round < rounds; round++)
            {
                encoder.Encode(plan, source);
            }

            encodeClock.Stop();
            encodeAllocated = GC.GetAllocatedBytesForCurrentThread() - encodeAllocated;

            long decodeAllocated = GC.GetAllocatedBytesForCurrentThread();
            decodeClock.Start();
            for (int round = 0; round < rounds; round++)
            {
                decoder.TryDecode(packet, "alice", out _, out _, out _);
            }

            decodeClock.Stop();
            decodeAllocated = GC.GetAllocatedBytesForCurrentThread() - decodeAllocated;

            double patches = (double)rounds * PartCount;
            TestContext.WriteLine("S2 binary cost per CFrame patch, 200-patch packets of " + packet.Length + " B: encode "
                                  + (encodeClock.Elapsed.TotalMilliseconds * 1e6 / patches).ToString("F0") + " ns, "
                                  + (encodeAllocated / patches).ToString("F1") + " B allocated; decode "
                                  + (decodeClock.Elapsed.TotalMilliseconds * 1e6 / patches).ToString("F0") + " ns, "
                                  + (decodeAllocated / patches).ToString("F1") + " B allocated");
            Assert.AreEqual(0L, encodeAllocated, "the encoder allocates nothing per CFrame patch");
            Assert.LessOrEqual(decodeAllocated / patches, 160d,
                "the decoder's cost per patch is one operation and one part entry, not a node per part");
        }

        private static double MeasureMovingParts(bool free, out int patches, out long bytes)
        {
            using WireWorld world = new(new ReplicationPublisherOptions { TickRateHz = 20d });
            world.Join("alice");
            Random random = new(free ? 21 : 22);
            List<RbxInstance> parts = new();
            RbxInstance map = world.Create("Folder", "Map", world.Workspace);
            for (int index = 0; index < PartCount; index++)
            {
                parts.Add(world.CreatePart("Part" + index, map, free ? RandomCFrame(random) : GridCFrame(random)));
            }

            world.Advance(0d);
            int worldPackets = world.Sent.Count;
            for (int tick = 0; tick < Ticks; tick++)
            {
                foreach (RbxInstance part in parts)
                {
                    world.Move(part, free ? RandomCFrame(random) : GridCFrame(random));
                }

                Assert.IsTrue(world.Advance(0.05d));
            }

            patches = 0;
            bytes = 0L;
            for (int index = worldPackets; index < world.Sent.Count; index++)
            {
                patches += world.Sent[index].OperationCount;
                bytes += world.Sent[index].Bytes.Length;
            }

            world.AssertConverged("alice");
            return bytes / (double)patches;
        }

        /// <summary>A part anywhere within 512 studs of the origin, turned any way.</summary>
        private static RbxCFrame RandomCFrame(Random random)
        {
            return RbxCFrame.FromQuaternion(
                (float)(random.NextDouble() * 1024d - 512d), (float)(random.NextDouble() * 64d),
                (float)(random.NextDouble() * 1024d - 512d),
                (float)(random.NextDouble() * 2d - 1d), (float)(random.NextDouble() * 2d - 1d),
                (float)(random.NextDouble() * 2d - 1d), (float)(random.NextDouble() * 2d - 1d) + 0.01f);
        }

        /// <summary>A block on the 4-stud grid of a voxel world, turned in quarter steps about Y.</summary>
        private static RbxCFrame GridCFrame(Random random)
        {
            float x = random.Next(-128, 128) * 4f;
            float y = random.Next(0, 16) * 4f;
            float z = random.Next(-128, 128) * 4f;
            switch (random.Next(4))
            {
                case 0:
                    return RbxCFrame.FromPosition(x, y, z);
                case 1:
                    return new RbxCFrame(x, y, z, 0f, 0f, 1f, 0f, 1f, 0f, -1f, 0f, 0f);
                case 2:
                    return new RbxCFrame(x, y, z, -1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, -1f);
                default:
                    return new RbxCFrame(x, y, z, 0f, 0f, -1f, 0f, 1f, 0f, 1f, 0f, 0f);
            }
        }
    }
}
