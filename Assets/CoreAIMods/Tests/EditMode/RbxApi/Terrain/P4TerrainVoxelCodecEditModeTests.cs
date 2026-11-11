using System;
using System.Collections.Generic;
using System.Diagnostics;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Terrain;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Terrain
{
    /// <summary>
    /// P4, engine-free: the Terrain package payload (<c>Terrain/voxels.bin</c>). A golden byte layout,
    /// a byte-identical round trip of the reference world inside the WebGL 4 MiB package budget, and a
    /// refusal for every malformed payload, each against the golden that passes.
    /// </summary>
    [TestFixture]
    public sealed class P4TerrainVoxelCodecEditModeTests
    {
        private const string GoldenHex =
            "43415658" + "0100" + "1000" + "17"
            + "0007" + "0008" + "1002" + "1403" + "2003" + "2403" + "3003" + "3403" + "4403" + "5003" + "7003"
            + "8003" + "9003" + "0005" + "0405" + "1005" + "3005" + "4005" + "5005" + "6005" + "7005" + "0006"
            + "1006"
            + "02000000"
            + "010000" + "03" + "0F0000" + "0101FF" + "F01F0000"
            + "000000" + "02" + "010DFF" + "FF1F0000";

        private static TerrainChunkStore GoldenStore()
        {
            TerrainChunkStore store = new(TerrainLimits.Desktop);
            store.SetCell(0, 0, 0, P4TerrainTestWorld.Slot("Grass"), TerrainOccupancy.Full);
            store.SetCell(-1, 0, 0, TerrainMaterials.Water, TerrainOccupancy.Full);
            return store;
        }

        private static byte[] Golden()
        {
            return FromHex(GoldenHex);
        }

        [Test]
        public void P4_Codec_Encode_MatchesTheGoldenLayout()
        {
            Assert.AreEqual(GoldenHex, ToHex(TerrainVoxelCodec.Encode(GoldenStore())));
        }

        [Test]
        public void P4_Codec_DecodeOfTheGolden_GivesTheCells()
        {
            TerrainChunkStore store = new(TerrainLimits.Desktop);

            TerrainVoxelCodec.DecodeInto(Golden(), store);

            Assert.AreEqual(P4TerrainTestWorld.Slot("Grass"), store.GetMaterial(0, 0, 0));
            Assert.AreEqual(TerrainMaterials.Water, store.GetMaterial(-1, 0, 0));
            Assert.AreEqual(2, store.SolidCellCount);
            Assert.AreEqual(2, store.ChunkCount);
        }

        [Test]
        public void P4_Codec_EmptyStore_HasNoContent_AndEncodesAnEmptyChunkList()
        {
            TerrainChunkStore store = new(TerrainLimits.Desktop);

            Assert.IsFalse(TerrainVoxelCodec.HasContent(store), "an empty Terrain writes no package entry");
            byte[] bytes = TerrainVoxelCodec.Encode(store);
            TerrainChunkStore decoded = new(TerrainLimits.Desktop);
            TerrainVoxelCodec.DecodeInto(bytes, decoded);
            Assert.AreEqual(0, decoded.ChunkCount);
        }

        [Test]
        public void P4_Codec_ReferenceWorld_RoundTripsByteIdentical_InsideTheWebGlPackageBudget()
        {
            TerrainChunkStore store = new(TerrainLimits.WebGl);
            P4ReferenceWorld.Fill(store);
            Stopwatch encodeClock = Stopwatch.StartNew();
            byte[] first = TerrainVoxelCodec.Encode(store);
            encodeClock.Stop();

            TerrainChunkStore restored = new(TerrainLimits.WebGl);
            Stopwatch decodeClock = Stopwatch.StartNew();
            TerrainVoxelCodec.DecodeInto(first, restored);
            decodeClock.Stop();
            byte[] second = TerrainVoxelCodec.Encode(restored);

            TestContext.WriteLine("reference world: " + store.SolidCellCount + " non-empty cells in "
                                  + store.ChunkCount + " chunks; payload " + first.Length + " bytes; encode "
                                  + encodeClock.Elapsed.TotalMilliseconds.ToString("0.0") + " ms, decode "
                                  + decodeClock.Elapsed.TotalMilliseconds.ToString("0.0") + " ms");
            CollectionAssert.AreEqual(first, second, "save, load, save is byte-identical");
            Assert.AreEqual(store.SolidCellCount, restored.SolidCellCount);
            Assert.LessOrEqual(first.Length, 4 * 1024 * 1024, "the payload fits the WebGL package budget");
            AssertSameCells(store, restored);
        }

        [Test]
        public void P4_Codec_DecodeIntoAStoreWithSmallerBudgets_IsRefused_AndLeavesItUntouched()
        {
            TerrainChunkStore source = new(TerrainLimits.Desktop);
            source.FillBox(new TerrainCellBox(0, 0, 0, 32, 4, 4), P4TerrainTestWorld.Slot("Rock"),
                TerrainOccupancy.Full);
            byte[] bytes = TerrainVoxelCodec.Encode(source);
            TerrainChunkStore target = new(P4TerrainTestWorld.Tiny(maxSolidCells: 100));
            target.SetCell(5, 5, 5, P4TerrainTestWorld.Slot("Sand"), TerrainOccupancy.Full);

            RbxError error = Assert.Throws<RbxError>(() => TerrainVoxelCodec.DecodeInto(bytes, target));

            Assert.AreEqual(RbxErrorCode.BudgetExceeded, error.Code);
            Assert.AreEqual(1, target.SolidCellCount);
            Assert.AreEqual(P4TerrainTestWorld.Slot("Sand"), target.GetMaterial(5, 5, 5));
        }

        [Test]
        public void P4_Codec_Decode_RefusesAWrongSignature()
        {
            byte[] bytes = Golden();
            bytes[0] = (byte)'X';

            AssertRefused(bytes, "signature");
        }

        [Test]
        public void P4_Codec_Decode_RefusesAnotherFormatVersion()
        {
            byte[] bytes = Golden();
            bytes[4] = 2;

            AssertRefused(bytes, "format version 2");
        }

        [Test]
        public void P4_Codec_Decode_RefusesATruncatedPayload()
        {
            byte[] bytes = Golden();
            Array.Resize(ref bytes, bytes.Length - 3);

            AssertRefused(bytes, "ends early");
        }

        [Test]
        public void P4_Codec_Decode_RefusesTrailingBytes()
        {
            byte[] bytes = Golden();
            Array.Resize(ref bytes, bytes.Length + 1);

            AssertRefused(bytes, "after its last chunk");
        }

        [Test]
        public void P4_Codec_Decode_RefusesANonTerrainMaterialInThePalette()
        {
            byte[] bytes = Golden();
            bytes[9] = 0x00;
            bytes[10] = 0x01;

            AssertRefused(bytes, "not a Terrain material");
        }

        [Test]
        public void P4_Codec_Decode_RefusesChunksOutOfOrder()
        {
            List<byte> bytes = new(Golden());
            int firstChunk = 9 + 2 * 23 + 4;
            byte[] first = bytes.GetRange(firstChunk, 14).ToArray();
            byte[] second = bytes.GetRange(firstChunk + 14, 11).ToArray();
            bytes.RemoveRange(firstChunk, 25);
            bytes.InsertRange(firstChunk, first);
            bytes.InsertRange(firstChunk, second);

            AssertRefused(bytes.ToArray(), "out of order");
        }

        [Test]
        public void P4_Codec_Decode_RefusesARunPastTheChunk()
        {
            byte[] bytes = Golden();
            int lastRunLength = bytes.Length - 4;
            bytes[lastRunLength] = 0x80;
            bytes[lastRunLength + 1] = 0x20;

            AssertRefused(bytes, "remain");
        }

        [Test]
        public void P4_Codec_Decode_RefusesAirWithOccupancy()
        {
            byte[] bytes = Golden();
            bytes[bytes.Length - 1] = 0x10;

            AssertRefused(bytes, "pairs Air");
        }

        [Test]
        public void P4_Codec_Decode_RefusesAnAllAirChunk()
        {
            byte[] bytes = Payload(new byte[] { 0x00, 0x00, 0x00, 0x01, 0x80, 0x20, 0x00, 0x00 });

            AssertRefused(bytes, "holds only Air");
        }

        [Test]
        public void P4_Codec_Decode_RefusesASplitRun()
        {
            byte[] bytes = Payload(new byte[]
            {
                0x00, 0x00, 0x00, 0x03, 0x01, 0x0D, 0xFF, 0xFF, 0x0F, 0x00, 0x00, 0x80, 0x10, 0x00, 0x00
            });

            AssertRefused(bytes, "splits one run");
        }

        [Test]
        public void P4_Codec_Decode_OfTheSameChunkWithMaximalRuns_Passes()
        {
            byte[] bytes = Payload(new byte[] { 0x00, 0x00, 0x00, 0x02, 0x01, 0x0D, 0xFF, 0xFF, 0x1F, 0x00, 0x00 });
            TerrainChunkStore store = new(TerrainLimits.Desktop);

            TerrainVoxelCodec.DecodeInto(bytes, store);

            Assert.AreEqual(1, store.SolidCellCount);
        }

        /// <summary>The golden header with one chunk count and the given chunk records.</summary>
        private static byte[] Payload(params byte[][] chunks)
        {
            List<byte> bytes = new(Golden());
            bytes.RemoveRange(55, bytes.Count - 55);
            bytes.AddRange(BitConverter.GetBytes((uint)chunks.Length));
            foreach (byte[] chunk in chunks)
            {
                bytes.AddRange(chunk);
            }

            return bytes.ToArray();
        }

        private static void AssertRefused(byte[] bytes, string expectedProblem)
        {
            TerrainChunkStore store = new(TerrainLimits.Desktop);
            store.SetCell(7, 7, 7, P4TerrainTestWorld.Slot("Rock"), TerrainOccupancy.Full);

            TerrainVoxelFormatException error = Assert.Throws<TerrainVoxelFormatException>(() =>
                TerrainVoxelCodec.DecodeInto(bytes, store));

            StringAssert.Contains(expectedProblem, error.Message);
            Assert.AreEqual(1, store.SolidCellCount, "a refused payload leaves the store as it was");
        }

        private static void AssertSameCells(TerrainChunkStore expected, TerrainChunkStore actual)
        {
            List<TerrainChunkCoord> coords = new();
            expected.CopyChunkCoords(coords);
            List<TerrainChunkCoord> actualCoords = new();
            actual.CopyChunkCoords(actualCoords);
            CollectionAssert.AreEqual(coords, actualCoords);
            foreach (TerrainChunkCoord coord in coords)
            {
                expected.TryGetChunk(coord, out TerrainChunk left);
                actual.TryGetChunk(coord, out TerrainChunk right);
                Assert.IsTrue(left.Materials.SequenceEqual(right.Materials), "materials differ in " + coord);
                Assert.IsTrue(left.Occupancy.SequenceEqual(right.Occupancy), "occupancy differs in " + coord);
            }
        }

        private static byte[] FromHex(string hex)
        {
            byte[] bytes = new byte[hex.Length / 2];
            for (int index = 0; index < bytes.Length; index++)
            {
                bytes[index] = Convert.ToByte(hex.Substring(index * 2, 2), 16);
            }

            return bytes;
        }

        private static string ToHex(byte[] bytes)
        {
            return BitConverter.ToString(bytes).Replace("-", "");
        }
    }
}
