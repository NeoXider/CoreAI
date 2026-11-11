using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Instances.Terrain;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Terrain
{
    /// <summary>
    /// Engine-free contracts of the Terrain's streaming policy: the frame budget is full while the last frames are
    /// smooth, smaller after a late frame and smallest after one over 60 Hz (twin), and comes back; queued chunks
    /// sort nearest first with ties kept; and a ray's chunk walk visits the chunks it crosses in order, stops at its
    /// length and at its cap, and adds nothing for no direction (twin).
    /// </summary>
    [TestFixture]
    public sealed class TerrainStreamingPolicyEditModeTests
    {
        [Test]
        public void TheFrameBudget_IsFullWhileFramesAreSmooth_AndComesBackAfterALateOne()
        {
            TerrainFrameBudget budget = new();
            for (int frame = 0; frame < 5; frame++)
            {
                budget.RecordFrame(8d);
            }

            Assert.AreEqual(TerrainFrameBudget.FullMilliseconds, budget.Milliseconds, "smooth frames: about 4 ms");
            budget.RecordFrame(13d);
            Assert.AreEqual(TerrainFrameBudget.ReducedMilliseconds, budget.Milliseconds, "a late frame backs off");
            for (int frame = 0; frame < 4; frame++)
            {
                budget.RecordFrame(8d);
            }

            Assert.AreEqual(TerrainFrameBudget.FullMilliseconds, budget.Milliseconds, "four smooth frames later it is back");
        }

        [Test]
        public void AFrameOver60Hz_LeavesTheSmallestBudget_NegativeTwin()
        {
            TerrainFrameBudget budget = new();
            budget.RecordFrame(8d);
            budget.RecordFrame(20d);

            Assert.AreEqual(TerrainFrameBudget.MinimumMilliseconds, budget.Milliseconds);
        }

        [Test]
        public void QueuedChunks_SortNearestFirst_WithTiesKeptInOrder()
        {
            List<TerrainChunkCoord> coords = new()
            {
                new TerrainChunkCoord(5, 0, 0), new TerrainChunkCoord(1, 0, 0), new TerrainChunkCoord(-1, 0, 0),
                new TerrainChunkCoord(0, 0, 0), new TerrainChunkCoord(0, 0, 3)
            };

            TerrainChunkReach.SortNearestFirst(coords, 8d, 8d, 8d, new List<double>());

            Assert.AreEqual(new TerrainChunkCoord(0, 0, 0), coords[0], "the chunk the point is in");
            Assert.AreEqual(new TerrainChunkCoord(1, 0, 0), coords[1], "a tie keeps its order: (1, 0, 0) was first");
            Assert.AreEqual(new TerrainChunkCoord(-1, 0, 0), coords[2]);
            Assert.AreEqual(new TerrainChunkCoord(0, 0, 3), coords[3]);
            Assert.AreEqual(new TerrainChunkCoord(5, 0, 0), coords[4], "the farthest last");
        }

        [Test]
        public void ARaysChunkWalk_VisitsTheChunksItCrosses_InOrder_UpToItsLengthAndCap()
        {
            List<TerrainChunkCoord> along = new();
            int added = TerrainChunkReach.ChunksAlong(0.5d, 0.5d, 0.5d, 1d, 0d, 0d, 40d, 8, along);

            Assert.AreEqual(3, added, "40 cells from the first chunk reach into the third");
            CollectionAssert.AreEqual(new[]
            {
                new TerrainChunkCoord(0, 0, 0), new TerrainChunkCoord(1, 0, 0), new TerrainChunkCoord(2, 0, 0)
            }, along);

            along.Clear();
            Assert.AreEqual(2, TerrainChunkReach.ChunksAlong(0.5d, 0.5d, 0.5d, 1d, 0d, 0d, 400d, 2, along), "the cap");

            along.Clear();
            TerrainChunkReach.ChunksAlong(1d, 2d, 3d, 1d, -0.6d, 0.8d, 100d, 64, along);
            for (int index = 1; index < along.Count; index++)
            {
                TerrainChunkCoord before = along[index - 1];
                TerrainChunkCoord now = along[index];
                int steps = Math.Abs(now.X - before.X) + Math.Abs(now.Y - before.Y) + Math.Abs(now.Z - before.Z);
                Assert.AreEqual(1, steps, "a diagonal ray steps one face at a time: " + before + " to " + now);
            }
        }

        [Test]
        public void ARayWithNoDirection_AddsNothing_NegativeTwin()
        {
            List<TerrainChunkCoord> along = new();

            Assert.AreEqual(0, TerrainChunkReach.ChunksAlong(0d, 0d, 0d, 0d, 0d, 0d, 10d, 8, along));
            CollectionAssert.IsEmpty(along);
        }
    }
}
