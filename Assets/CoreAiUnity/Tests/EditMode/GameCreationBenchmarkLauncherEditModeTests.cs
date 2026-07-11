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
    }
}
