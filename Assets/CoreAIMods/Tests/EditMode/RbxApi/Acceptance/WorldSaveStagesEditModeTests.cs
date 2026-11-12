using System;
using CoreAI.Mods.WorldPackages;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Acceptance
{
    /// <summary>The save steps a hang report names: a step is named while it runs and forgotten once it ends.</summary>
    public sealed class WorldSaveStagesEditModeTests
    {
        [Test]
        public void AStepRunning_IsNamedAsStuck_PastTheThreshold_AndForgottenOnceItEnds()
        {
            int before = WorldSaveStages.RunningCount;
            IDisposable step = WorldSaveStages.Enter("gate", "'stages_test_call' waits for the autosave behind 'stages_test_reload'");

            string stuck = WorldSaveStages.DescribeStuck(TimeSpan.Zero);
            StringAssert.Contains("gate: 'stages_test_call' waits for the autosave behind 'stages_test_reload' for ", stuck);
            Assert.AreEqual(before + 1, WorldSaveStages.RunningCount);

            step.Dispose();
            step.Dispose();
            Assert.AreEqual(before, WorldSaveStages.RunningCount, "an ended step is gone, a second dispose is harmless");
            StringAssert.DoesNotContain("'stages_test_call'", WorldSaveStages.DescribeStuck(TimeSpan.Zero));
        }

        [Test]
        public void AStepYoungerThanTheThreshold_IsNotNamed_NegativeTwin()
        {
            using (WorldSaveStages.Enter("record", "'stages_test_world' writes the record"))
            {
                StringAssert.DoesNotContain("'stages_test_world' writes the record",
                    WorldSaveStages.DescribeStuck(TimeSpan.FromMinutes(5)));
            }
        }
    }
}
