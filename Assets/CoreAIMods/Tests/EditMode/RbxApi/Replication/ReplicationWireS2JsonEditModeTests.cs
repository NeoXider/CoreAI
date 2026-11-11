using System;
using System.Collections.Generic;
using System.Diagnostics;
using CoreAI.Ai.LuaCs;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Replication;
using Lua;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Replication
{
    /// <summary>
    /// Spike S2, both legs side by side on the same CFrame patches: the remote codec's JSON
    /// (<see cref="LuaCsRbxNetworkCodec"/>, a patch as the arguments <c>(part, revision, cframe)</c>
    /// a script would fire today) against the binary record of <see cref="ReplicationWireEncoder"/>.
    /// The numbers go to the test output; <c>dev-docs/REPLICATION_CODEC.md</c> records them.
    /// </summary>
    /// <remarks>
    /// WHY a patch is three remote arguments: until this codec, a remote was the only way any value
    /// crossed the wire, so a replicated move written today is <c>FireAllClients(part, revision, cframe)</c>.
    /// WHY only bytes are asserted: they belong to the formats; the times belong to the machine.
    /// </remarks>
    [TestFixture]
    public sealed class ReplicationWireS2JsonEditModeTests
    {
        private const int PartCount = 200;
        private const int Rounds = 200;

        [Test]
        public void S2_JsonAgainstBinary_OnTheSameCFramePatches()
        {
            InstanceRegistry registry = new();
            DataModelBootstrap.CreateGame(registry);
            Random random = new(31);
            List<RbxInstance> parts = new();
            List<RbxCFrame> frames = new();
            FakeWireSource source = new();
            List<ReplicationOperation> operations = new();
            for (int index = 0; index < PartCount; index++)
            {
                RbxInstance part = registry.Create("Part");
                part.Parent = registry.WorldRoot;
                RbxCFrame frame = RbxCFrame.FromQuaternion(
                    (float)(random.NextDouble() * 1024d - 512d), (float)(random.NextDouble() * 64d),
                    (float)(random.NextDouble() * 1024d - 512d), (float)(random.NextDouble() * 2d - 1d),
                    (float)(random.NextDouble() * 2d - 1d), (float)(random.NextDouble() * 2d - 1d),
                    (float)(random.NextDouble() * 2d - 1d) + 0.01f);
                parts.Add(part);
                frames.Add(frame);
                source.Parts[part.Id.Value] = ReplicationPartState.Of(frame, new RbxColor3(0f, 0f, 0f), 0f, false);
                operations.Add(new ReplicationOperation(ReplicationOperationKind.Patch, part.Id, 1500L + index,
                    new[] { ReplicationPartMembers.CFrame }));
            }

            LuaCsRbxNetworkCodec json = new(registry, RbxEnumRegistry.CreateWithBuiltins(), null);
            LuaValue[] one = Arguments(parts, frames, operations, 1);
            LuaValue[] batch = Arguments(parts, frames, operations, PartCount);
            byte[] jsonOne = json.EncodeArguments(one);
            byte[] jsonBatch = json.EncodeArguments(batch);
            object[] decodedOne = json.DecodeArguments(jsonOne);
            Assert.AreSame(parts[0], decodedOne[0], "the JSON leg resolves the part");
            Assert.AreEqual(frames[0], (RbxCFrame)decodedOne[2], "the JSON leg is exact");

            ReplicationBatchPlan onePlan = new("alice", 1L, operations.GetRange(0, 1));
            ReplicationBatchPlan batchPlan = new("alice", 1L, operations);
            ReplicationWireEncoder encoder = new();
            ReplicationWireDecoder decoder = new();
            byte[] binaryOne = encoder.EncodeToArray(onePlan, source);
            byte[] binaryBatch = encoder.EncodeToArray(batchPlan, source);

            Measurement jsonCost = Measure(() => json.EncodeArguments(batch), () => json.DecodeArguments(jsonBatch));
            Measurement binaryCost = Measure(() => encoder.Encode(batchPlan, source),
                () => decoder.TryDecode(binaryBatch, "alice", out _, out _, out _));

            double worstRotation = 0d;
            double worstPosition = 0d;
            Assert.IsTrue(decoder.TryDecode(binaryBatch, "alice", out ReplicationWireBatch decoded, out _, out _));
            for (int index = 0; index < PartCount; index++)
            {
                ReplicationPartState state = decoded.GetPartState(index).State;
                worstRotation = Math.Max(worstRotation, WireAssert.RotationAngle(frames[index], state.CFrame));
                worstPosition = Math.Max(worstPosition, Math.Abs(frames[index].X - (double)state.CFrame.X));
                worstPosition = Math.Max(worstPosition, Math.Abs(frames[index].Y - (double)state.CFrame.Y));
                worstPosition = Math.Max(worstPosition, Math.Abs(frames[index].Z - (double)state.CFrame.Z));
            }

            TestContext.WriteLine("| leg | one patch alone | per patch in a 200-patch packet | encode / patch | decode / patch | encode alloc / patch | decode alloc / patch | error |");
            TestContext.WriteLine("| JSON (LuaCsRbxNetworkCodec) | " + jsonOne.Length + " B | "
                                  + (jsonBatch.Length / (double)PartCount).ToString("F1") + " B | "
                                  + jsonCost.EncodeNanoseconds.ToString("F0") + " ns | "
                                  + jsonCost.DecodeNanoseconds.ToString("F0") + " ns | "
                                  + jsonCost.EncodeBytes.ToString("F0") + " B | " + jsonCost.DecodeBytes.ToString("F0")
                                  + " B | exact |");
            TestContext.WriteLine("| binary (ReplicationWireEncoder) | " + binaryOne.Length + " B | "
                                  + (binaryBatch.Length / (double)PartCount).ToString("F1") + " B | "
                                  + binaryCost.EncodeNanoseconds.ToString("F0") + " ns | "
                                  + binaryCost.DecodeNanoseconds.ToString("F0") + " ns | "
                                  + binaryCost.EncodeBytes.ToString("F0") + " B | " + binaryCost.DecodeBytes.ToString("F0")
                                  + " B | position <= " + worstPosition.ToString("G3") + " stud, rotation <= "
                                  + worstRotation.ToString("G3") + " rad |");
            TestContext.WriteLine("JSON sample: " + System.Text.Encoding.UTF8.GetString(jsonOne));

            Assert.LessOrEqual(binaryBatch.Length / (double)PartCount, 24d, "the binary leg meets the draft bar");
            Assert.Greater(jsonBatch.Length, binaryBatch.Length * 4, "the JSON leg is several times larger");
            Assert.LessOrEqual(worstPosition, ReplicationWireFormat.PositionErrorBoundStuds);
            Assert.LessOrEqual(worstRotation, ReplicationWireFormat.RotationErrorBoundRadians);
        }

        private static LuaValue[] Arguments(List<RbxInstance> parts, List<RbxCFrame> frames,
            List<ReplicationOperation> operations, int count)
        {
            LuaValue[] arguments = new LuaValue[count * 3];
            for (int index = 0; index < count; index++)
            {
                arguments[index * 3] = new LuaValue(new LuaCsRbxInstanceProxy(parts[index], null, null));
                arguments[index * 3 + 1] = new LuaValue((double)operations[index].Revision);
                arguments[index * 3 + 2] = LuaCsRbxDatatypeBindings.Wrap(frames[index]);
            }

            return arguments;
        }

        private static Measurement Measure(Action encode, Action decode)
        {
            for (int warm = 0; warm < 100; warm++)
            {
                encode();
                decode();
            }

            Stopwatch clock = new();
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            clock.Start();
            for (int round = 0; round < Rounds; round++)
            {
                encode();
            }

            clock.Stop();
            double patches = (double)Rounds * PartCount;
            double encodeNanoseconds = clock.Elapsed.TotalMilliseconds * 1e6 / patches;
            double encodeBytes = (GC.GetAllocatedBytesForCurrentThread() - allocated) / patches;

            clock.Reset();
            allocated = GC.GetAllocatedBytesForCurrentThread();
            clock.Start();
            for (int round = 0; round < Rounds; round++)
            {
                decode();
            }

            clock.Stop();
            return new Measurement(encodeNanoseconds, clock.Elapsed.TotalMilliseconds * 1e6 / patches, encodeBytes,
                (GC.GetAllocatedBytesForCurrentThread() - allocated) / patches);
        }

        private readonly struct Measurement
        {
            public Measurement(double encodeNanoseconds, double decodeNanoseconds, double encodeBytes,
                double decodeBytes)
            {
                EncodeNanoseconds = encodeNanoseconds;
                DecodeNanoseconds = decodeNanoseconds;
                EncodeBytes = encodeBytes;
                DecodeBytes = decodeBytes;
            }

            public double EncodeNanoseconds { get; }

            public double DecodeNanoseconds { get; }

            public double EncodeBytes { get; }

            public double DecodeBytes { get; }
        }
    }
}
