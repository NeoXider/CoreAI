using System.Collections.Generic;
using CoreAI.Infrastructure.Lua;
using CoreAI.Mods.Performance;
using NUnit.Framework;

namespace CoreAI.Tests.PlayMode.Performance
{
    /// <summary>
    /// The P19 <c>sample-mods</c> scene loads the top-level bundled samples it always measured, not the demo mods
    /// in subfolders of <c>Resources/CoreAIMods</c> (the Block World set), which the recursive Resources scan the
    /// production seeder uses also returns.
    /// </summary>
    [TestFixture]
    public sealed class PerfBundledSamplesPlayModeTests
    {
        private static HashSet<string> Ids(IReadOnlyList<BundledMod> mods)
        {
            HashSet<string> ids = new(System.StringComparer.Ordinal);
            foreach (BundledMod mod in mods)
            {
                ids.Add(mod.Id);
            }

            return ids;
        }

        [Test]
        public void ReadBundledSamples_LoadsTheTopLevelSamples()
        {
            HashSet<string> ids = new(System.StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> mod in PerfScenarioRunner.ReadBundledSamples())
            {
                ids.Add(mod.Key);
            }

            Assert.IsTrue(ids.Contains("sample_clicker"), string.Join(", ", ids));
            Assert.IsTrue(ids.Contains("sample_tetris3d"), string.Join(", ", ids));
            Assert.IsTrue(ids.Contains("coreai_player_controls"), string.Join(", ", ids));
        }

        [Test]
        public void ReadBundledSamples_LeavesOutTheModsOfASubfolder_ThatTheSeedersScanReturns_NegativeTwin()
        {
            HashSet<string> subfolder = Ids(new ResourcesBundledModSource(
                ResourcesBundledModSource.DefaultResourceFolder + "/BlockWorld").Load());
            if (subfolder.Count == 0)
            {
                Assert.Ignore("No bundled mods in Resources/CoreAIMods/BlockWorld in this project.");
            }

            HashSet<string> seeded = Ids(new ResourcesBundledModSource().Load());
            Assert.IsTrue(seeded.IsSupersetOf(subfolder), "the seeder's scan is recursive and offers the demo mods");
            foreach (KeyValuePair<string, string> mod in PerfScenarioRunner.ReadBundledSamples())
            {
                Assert.IsFalse(subfolder.Contains(mod.Key), mod.Key + " belongs to a subfolder demo");
            }
        }
    }
}
