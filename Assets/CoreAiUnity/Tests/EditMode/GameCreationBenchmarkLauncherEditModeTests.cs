using System.IO;
using CoreAI.Tests.EditMode;
using NUnit.Framework;
using UnityEditor;

namespace CoreAI.Tests.EditMode
{
    /// <summary>Covers <see cref="GameCreationBenchmarkLauncher.DeleteRun"/>/<c>DeleteAllRuns</c> file cleanup.</summary>
    public sealed class GameCreationBenchmarkLauncherEditModeTests
    {
        private string _tempDir;
        private readonly string[] _groupPrefs =
        {
            GameCreationBenchmarkLauncher.PrefG1,
            GameCreationBenchmarkLauncher.PrefG2,
            GameCreationBenchmarkLauncher.PrefG3,
            GameCreationBenchmarkLauncher.PrefG4,
            GameCreationBenchmarkLauncher.PrefG5,
            GameCreationBenchmarkLauncher.PrefG6,
            GameCreationBenchmarkLauncher.PrefG7,
            GameCreationBenchmarkLauncher.PrefG8
        };

        private readonly System.Collections.Generic.Dictionary<string, bool> _savedValues = new();
        private readonly System.Collections.Generic.HashSet<string> _existingPrefs = new();

        [SetUp]
        public void SetUp()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "CoreAiBenchmarkLauncherTests_" + Path.GetRandomFileName());
            Directory.CreateDirectory(_tempDir);
            foreach (string pref in _groupPrefs)
            {
                if (EditorPrefs.HasKey(pref))
                {
                    _existingPrefs.Add(pref);
                    _savedValues[pref] = EditorPrefs.GetBool(pref);
                }

                EditorPrefs.DeleteKey(pref);
            }
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }

            foreach (string pref in _groupPrefs)
            {
                EditorPrefs.DeleteKey(pref);
                if (_existingPrefs.Contains(pref))
                {
                    EditorPrefs.SetBool(pref, _savedValues[pref]);
                }
            }

            _existingPrefs.Clear();
            _savedValues.Clear();
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

        [Test]
        public void LoadSavedG8Preference_UnsetWithAllSevenEnabled_IncludesG8()
        {
            foreach (string pref in _groupPrefs)
            {
                if (pref != GameCreationBenchmarkLauncher.PrefG8)
                {
                    EditorPrefs.SetBool(pref, true);
                }
            }

            Assert.IsTrue(GameCreationBenchmarkLauncher.LoadSavedG8Preference());
        }

        [Test]
        public void LoadSavedG8Preference_UnsetWithSubset_ExcludesG8()
        {
            for (int i = 0; i < 7; i++)
            {
                EditorPrefs.SetBool(_groupPrefs[i], i < 5);
            }

            Assert.IsFalse(GameCreationBenchmarkLauncher.LoadSavedG8Preference());
        }

        [TestCase(true)]
        [TestCase(false)]
        public void LoadSavedG8Preference_ExplicitValueAlwaysWins(bool expected)
        {
            EditorPrefs.SetBool(GameCreationBenchmarkLauncher.PrefG1, !expected);
            EditorPrefs.SetBool(GameCreationBenchmarkLauncher.PrefG8, expected);

            Assert.AreEqual(expected, GameCreationBenchmarkLauncher.LoadSavedG8Preference());
        }
    }
}
