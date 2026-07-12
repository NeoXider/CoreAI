using System.IO;
using CoreAI.Tests.EditMode;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>Covers <see cref="GameCreationBenchmarkLauncher.DeleteRun"/>/<c>DeleteAllRuns</c> file cleanup.</summary>
    public sealed class GameCreationBenchmarkLauncherEditModeTests
    {
        private string _tempDir;

        [SetUp]
        public void SetUp()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "CoreAiBenchmarkLauncherTests_" + Path.GetRandomFileName());
            Directory.CreateDirectory(_tempDir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }

        [Test]
        public void DeleteRun_RemovesCompanionArtifacts_NotJustJsonAndMd()
        {
            const string stem = "BENCHMARK_20260701_162831_qwen3.5-4b-mtp";
            string jsonPath = Path.Combine(_tempDir, stem + ".json");
            string mdPath = Path.Combine(_tempDir, stem + ".md");
            string svgPath = Path.Combine(_tempDir, stem + ".svg");
            string heroPath = Path.Combine(_tempDir, stem + "_g6_free_build_hero.png");
            string cardPath = Path.Combine(_tempDir, stem + "_modelcard.png");
            string scenarioPngPath = Path.Combine(_tempDir, stem + "_g1_spawn_arena.png");

            foreach (string path in new[] { jsonPath, mdPath, svgPath, heroPath, cardPath, scenarioPngPath })
            {
                File.WriteAllText(path, "x");
            }

            bool deleted = GameCreationBenchmarkLauncher.DeleteRun(
                new GameCreationBenchmarkLauncher.RunEntry { JsonPath = jsonPath, MdPath = mdPath });

            Assert.IsTrue(deleted);
            foreach (string path in new[] { jsonPath, mdPath, svgPath, heroPath, cardPath, scenarioPngPath })
            {
                Assert.IsFalse(File.Exists(path), $"Expected '{path}' to be deleted alongside the run.");
            }
        }

        [Test]
        public void DeleteRun_DoesNotTouchAnotherRunWithASimilarStem()
        {
            const string stemA = "BENCHMARK_20260701_162831_qwen3.5-4b-mtp";
            // A distinct run whose stem starts with the same characters plus a suffix - must survive.
            const string stemB = "BENCHMARK_20260701_162831_qwen3.5-4b-mtp-v2";

            string jsonA = Path.Combine(_tempDir, stemA + ".json");
            string mdA = Path.Combine(_tempDir, stemA + ".md");
            string jsonB = Path.Combine(_tempDir, stemB + ".json");
            string mdB = Path.Combine(_tempDir, stemB + ".md");

            foreach (string path in new[] { jsonA, mdA, jsonB, mdB })
            {
                File.WriteAllText(path, "x");
            }

            GameCreationBenchmarkLauncher.DeleteRun(
                new GameCreationBenchmarkLauncher.RunEntry { JsonPath = jsonA, MdPath = mdA });

            Assert.IsFalse(File.Exists(jsonA));
            Assert.IsFalse(File.Exists(mdA));
            Assert.IsTrue(File.Exists(jsonB), "An unrelated run with a similar stem must not be deleted.");
            Assert.IsTrue(File.Exists(mdB), "An unrelated run with a similar stem must not be deleted.");
        }

        [Test]
        public void DeleteRun_NullEntry_ReturnsFalse()
        {
            Assert.IsFalse(GameCreationBenchmarkLauncher.DeleteRun(null));
        }

        [Test]
        public void GroupsCsv_AllEightGroupsOn_ReturnsEmptyMeaningAllGroups()
        {
            Assert.AreEqual("", GameCreationBenchmarkLauncher.GroupsCsv(
                g1: true, g2: true, g3: true, g4: true, g5: true, g6: true, g7: true, g8: true));
        }

        [Test]
        public void GroupsCsv_NoGroupsOn_ReturnsEmptyMeaningAllGroups()
        {
            Assert.AreEqual("", GameCreationBenchmarkLauncher.GroupsCsv(
                g1: false, g2: false, g3: false, g4: false, g5: false, g6: false, g7: false, g8: false));
        }

        [Test]
        public void GroupsCsv_SubsetKeepsG8WhenSelected()
        {
            string csv = GameCreationBenchmarkLauncher.GroupsCsv(
                g1: true, g2: false, g3: false, g4: false, g5: false, g6: false, g7: false, g8: true);

            StringAssert.Contains("G1", csv);
            StringAssert.Contains("G8", csv,
                "Unchecking another group must not silently drop G8 from a subset run.");
            StringAssert.DoesNotContain("G2", csv);
        }

        [Test]
        public void GroupsCsv_SubsetWithoutG8_ExcludesItExplicitly()
        {
            string csv = GameCreationBenchmarkLauncher.GroupsCsv(
                g1: true, g2: true, g3: false, g4: false, g5: false, g6: false, g7: false, g8: false);

            StringAssert.Contains("G1", csv);
            StringAssert.Contains("G2", csv);
            StringAssert.DoesNotContain("G8", csv);
        }

        [Test]
        public void GroupsCsv_OnlyG8_ReturnsJustG8()
        {
            Assert.AreEqual("G8", GameCreationBenchmarkLauncher.GroupsCsv(
                g1: false, g2: false, g3: false, g4: false, g5: false, g6: false, g7: false, g8: true));
        }

        [Test]
        public void GroupsCsv_AllOnExceptG8_IsNoLongerTreatedAsAllGroups()
        {
            string csv = GameCreationBenchmarkLauncher.GroupsCsv(
                g1: true, g2: true, g3: true, g4: true, g5: true, g6: true, g7: true, g8: false);

            Assert.AreNotEqual("", csv,
                "Seven groups on with G8 off is a real subset, not the 'all groups' empty CSV.");
            StringAssert.DoesNotContain("G8", csv);
        }
    }
}
