using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CoreAI.Benchmarking;
using NUnit.Framework;
using UnityEditor;
using TargetRow = CoreAI.Tests.EditMode.GameCreationBenchmarkLauncher.BenchmarkTargetRow;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Covers the multi-target benchmark surface outside Play Mode: target list parsing (every accepted form and
    /// the rejected ones), the multi-run <see cref="BenchmarkProgress"/>, and the launcher's target, CLI and
    /// artifact helpers.
    /// </summary>
    public sealed class BenchmarkTargetsEditModeTests
    {
        private static readonly string[] LauncherEnvVars =
        {
            GameCreationBenchmarkLauncher.EnvModel, GameCreationBenchmarkLauncher.EnvBaseUrl,
            GameCreationBenchmarkLauncher.EnvApiKey, GameCreationBenchmarkLauncher.EnvStreaming,
            GameCreationBenchmarkLauncher.EnvNativeTools, GameCreationBenchmarkLauncher.EnvGroups,
            GameCreationBenchmarkLauncher.EnvRepetitions, GameCreationBenchmarkLauncher.EnvRetries,
            GameCreationBenchmarkLauncher.EnvTimeout, GameCreationBenchmarkLauncher.EnvTargets,
            GameCreationBenchmarkLauncher.EnvParallel, GameCreationBenchmarkLauncher.EnvReasoningEffort
        };

        private readonly Dictionary<string, string> _savedEnv = new();
        private string _tempDir;
        private string _savedTargetsPref;
        private bool _hadTargetsPref;

        [SetUp]
        public void SetUp()
        {
            foreach (string name in LauncherEnvVars)
            {
                _savedEnv[name] = Environment.GetEnvironmentVariable(name);
            }

            Environment.SetEnvironmentVariable(GameCreationBenchmarkLauncher.EnvReasoningEffort, null);

            _hadTargetsPref = EditorPrefs.HasKey(GameCreationBenchmarkLauncher.PrefTargets);
            _savedTargetsPref = EditorPrefs.GetString(GameCreationBenchmarkLauncher.PrefTargets, "");
            _tempDir = Path.Combine(Path.GetTempPath(), "CoreAiBenchmarkTargets_" + Path.GetRandomFileName());
            Directory.CreateDirectory(_tempDir);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (KeyValuePair<string, string> saved in _savedEnv)
            {
                Environment.SetEnvironmentVariable(saved.Key, saved.Value);
            }

            if (_hadTargetsPref)
            {
                EditorPrefs.SetString(GameCreationBenchmarkLauncher.PrefTargets, _savedTargetsPref);
            }
            else
            {
                EditorPrefs.DeleteKey(GameCreationBenchmarkLauncher.PrefTargets);
            }

            BenchmarkProgress.EndSession();
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("# only a comment\n\n;")]
        public void Parse_EmptySpec_MeansTheSingleModelRun(string spec)
        {
            Assert.IsTrue(BenchmarkTargetList.TryParse(spec, out List<BenchmarkTarget> targets, out string error),
                error);
            Assert.AreEqual(0, targets.Count);
        }

        [Test]
        public void Parse_Compact_LocatesFieldsAroundTheUrl()
        {
            List<BenchmarkTarget> targets = BenchmarkTargetList.Parse(
                "qwen3.5-4b|http://127.0.0.1:8831/v1/ ; " +
                "sonnet|claude-sonnet-5|http://127.0.0.1:8832/v1|sk-test-1\n" +
                "# a bridge that is down today\n" +
                "glm-5.2|https://api.example.com/v1|env:GLM_KEY\n" +
                "q8 | local-model | http://127.0.0.1:8833/v1");

            Assert.AreEqual(4, targets.Count);
            AssertTarget(targets[0], "", "qwen3.5-4b", "http://127.0.0.1:8831/v1", "");
            AssertTarget(targets[1], "sonnet", "claude-sonnet-5", "http://127.0.0.1:8832/v1", "sk-test-1");
            AssertTarget(targets[2], "", "glm-5.2", "https://api.example.com/v1", "env:GLM_KEY");
            AssertTarget(targets[3], "q8", "local-model", "http://127.0.0.1:8833/v1", "");
            Assert.AreEqual("qwen3.5-4b", targets[0].DisplayName);
            Assert.AreEqual("sonnet", targets[1].DisplayName);
            Assert.IsTrue(targets[2].HasKeyReference);
        }

        [Test]
        public void Parse_Json_AcceptsAliasesEscapesAndDisabledEntries()
        {
            const string json = "[" +
                                "{\"Label\":\"a\\u0042\",\"model\":\"m1\",\"baseUrl\":\"http://h:1/v1\",\"apiKey\":\"k\\\"1\"}," +
                                "{\"model_id\":\"m2\",\"base_url\":\"http://h:2/v1\",\"key\":null}," +
                                "{\"model\":\"m3\",\"url\":\"http://h:3/v1\",\"enabled\":false}," +
                                "{\"name\":\"n4\",\"modelId\":\"m4\",\"endpoint\":\"https://h4/v1/\"}" +
                                "]";

            List<BenchmarkTarget> targets = BenchmarkTargetList.Parse(json);

            Assert.AreEqual(3, targets.Count, "The disabled entry is skipped.");
            AssertTarget(targets[0], "aB", "m1", "http://h:1/v1", "k\"1");
            AssertTarget(targets[1], "", "m2", "http://h:2/v1", "");
            AssertTarget(targets[2], "n4", "m4", "https://h4/v1", "");
        }

        [Test]
        public void Parse_FileReference_ReadsEitherForm()
        {
            string path = Path.Combine(_tempDir, "targets.txt");
            File.WriteAllText(path, "m1|http://127.0.0.1:8831/v1\nm2|http://127.0.0.1:8832/v1\n");

            List<BenchmarkTarget> targets = BenchmarkTargetList.Parse("@" + path);

            CollectionAssert.AreEqual(new[] { "m1", "m2" }, targets.Select(t => t.Model).ToArray());
        }

        [TestCase("model-only", "no http(s) base URL")]
        [TestCase("m|http://a/v1|http://b/v1", "more than one base URL")]
        [TestCase("http://a/v1|m", "label|model|baseUrl|apiKey")]
        [TestCase("a|b|c|http://a/v1", "label|model|baseUrl|apiKey")]
        [TestCase("m|http://a/v1|key|high|extra", "label|model|baseUrl|apiKey|effort")]
        [TestCase("m|http://a/v1|key|extreme", "reasoning effort 'extreme' must be one of")]
        [TestCase("m|http://a/v1|high", "is ambiguous")]
        [TestCase("[{\"model\":\"m\",\"baseUrl\":\"http://a/v1\",\"reasoningEffort\":\"max\"}]",
            "reasoning effort 'max' must be one of")]
        [TestCase("[{\"model\":\"m\",\"baseUrl\":\"http://a/v1\",\"effort\":3}]", "must be a string")]
        [TestCase("[{\"model\":\"m\",\"baseUrl\":\"http://a/v1\",\"extraBody\":[1]}]", "must be a JSON object")]
        [TestCase("[{\"model\":\"m\",\"baseUrl\":\"http://a/v1\",\"extraBody\":\"top_k=20\"}]",
            "must be a JSON object")]
        [TestCase("m|ftp://a/v1", "no http(s) base URL")]
        [TestCase("[{\"model\":\"m\"}]", "needs an absolute http(s) base URL")]
        [TestCase("[{\"baseUrl\":\"http://a/v1\"}]", "has no model id")]
        [TestCase("[{\"model\":\"m\",\"baseUrl\":\"http://a/v1\",\"temperature\":1}]", "unknown property")]
        [TestCase("[{\"model\":5,\"baseUrl\":\"http://a/v1\"}]", "must be a string")]
        [TestCase("[{\"model\":\"m\",\"baseUrl\":\"http://a/v1\"", "malformed JSON")]
        [TestCase("[\"m|http://a/v1\"]", "is not a JSON object")]
        [TestCase("[{\"model\":\"m\",\"model\":\"n\",\"baseUrl\":\"http://a/v1\"}]", "duplicate property")]
        [TestCase("@Z:\\does\\not\\exist\\targets.json", "cannot read the target file")]
        public void Parse_BadInput_FailsWithAReason(string spec, string expected)
        {
            Assert.IsFalse(BenchmarkTargetList.TryParse(spec, out List<BenchmarkTarget> targets, out string error));
            Assert.AreEqual(0, targets.Count);
            StringAssert.Contains(expected, error);
            Assert.Throws<FormatException>(() => BenchmarkTargetList.Parse(spec));
        }

        [Test]
        public void Parse_ErrorNeverEchoesTheApiKey()
        {
            Assert.IsFalse(BenchmarkTargetList.TryParse("m|http://a/v1|sk-very-secret|high|extra", out _,
                out string error));
            StringAssert.DoesNotContain("sk-very-secret", error);
        }

        [Test]
        public void Parse_DuplicateDisplayNames_GetNumberedLabels()
        {
            List<BenchmarkTarget> targets = BenchmarkTargetList.Parse(
                "m|http://127.0.0.1:8831/v1;m|http://127.0.0.1:8832/v1;M|http://127.0.0.1:8833/v1;" +
                "fast|x|http://127.0.0.1:8834/v1;fast|y|http://127.0.0.1:8835/v1");

            CollectionAssert.AreEqual(new[] { "m", "m#2", "M#3", "fast", "fast#2" },
                targets.Select(t => t.DisplayName).ToArray());
            Assert.AreEqual("m", targets[1].Model, "Disambiguation never changes the requested model.");
        }

        [Test]
        public void ToJson_RoundTripsAndKeepsLiteralKeysOnlyWhenAsked()
        {
            List<BenchmarkTarget> targets = BenchmarkTargetList.Parse(
                "lab \"q\"|m1|http://127.0.0.1:8831/v1|sk-literal;m2|http://127.0.0.1:8832/v1|env:KEY_VAR");

            List<BenchmarkTarget> withKeys = BenchmarkTargetList.Parse(BenchmarkTargetList.ToJson(targets, true));
            AssertTarget(withKeys[0], "lab \"q\"", "m1", "http://127.0.0.1:8831/v1", "sk-literal");
            AssertTarget(withKeys[1], "", "m2", "http://127.0.0.1:8832/v1", "env:KEY_VAR");

            string safe = BenchmarkTargetList.ToJson(targets, false);
            StringAssert.DoesNotContain("sk-literal", safe);
            StringAssert.Contains("env:KEY_VAR", safe);
        }

        [TestCase(null, 9, 9)]
        [TestCase("", 9, 9)]
        [TestCase("0", 9, 9)]
        [TestCase("3", 9, 3)]
        [TestCase("12", 9, 9)]
        [TestCase("-2", 9, 9)]
        [TestCase("lots", 9, 9)]
        [TestCase("4", 0, 0)]
        public void ResolveParallelism_ClampsToTheTargetCount(string raw, int targets, int expected)
        {
            Assert.AreEqual(expected, BenchmarkTargetList.ResolveParallelism(raw, targets));
        }

        [Test]
        public void ResolveApiKey_ReadsAnEnvReferenceAndKeepsALiteral()
        {
            string variable = "COREAI_TEST_TARGET_KEY_" + Guid.NewGuid().ToString("N");
            try
            {
                Environment.SetEnvironmentVariable(variable, "from-env");
                Assert.AreEqual("from-env", new BenchmarkTarget("m", "http://a/v1", "env:" + variable).ResolveApiKey());
                Assert.AreEqual("literal", new BenchmarkTarget("m", "http://a/v1", "literal").ResolveApiKey());
                Assert.AreEqual("", new BenchmarkTarget("m", "http://a/v1", "env:UNSET_" + variable).ResolveApiKey());
            }
            finally
            {
                Environment.SetEnvironmentVariable(variable, null);
            }
        }

        [Test]
        public void Progress_SingleRunShortcut_ReadsAsBefore()
        {
            BenchmarkProgress.Begin(3, "qwen");
            BenchmarkProgress.StartScenario("G2 · Flat damage", 120f);

            Assert.IsTrue(BenchmarkProgress.IsRunning);
            Assert.AreEqual("qwen", BenchmarkProgress.ModelId);
            Assert.AreEqual("G2 · Flat damage", BenchmarkProgress.CurrentLabel);
            Assert.AreEqual(3, BenchmarkProgress.Total);
            Assert.IsTrue(BenchmarkProgress.HasScenarioClock);

            BenchmarkProgress.CompleteScenario("✅ Flat damage — 100", "●○○○○ 2/10");
            Assert.AreEqual(1, BenchmarkProgress.Completed);
            Assert.AreEqual("✅ Flat damage — 100", BenchmarkProgress.RecentLines()[0].Left,
                "A single run's lines carry no target prefix.");

            BenchmarkProgress.RequestStop();
            Assert.IsTrue(BenchmarkProgress.StopRequested);
            BenchmarkProgress.End();
            Assert.IsFalse(BenchmarkProgress.IsRunning);
            Assert.AreEqual("", BenchmarkProgress.CurrentLabel);

            BenchmarkProgress.Begin(1, "next");
            Assert.IsFalse(BenchmarkProgress.StopRequested, "A new run starts without the previous stop.");
        }

        [Test]
        public void Progress_SeveralRuns_AggregateAndStopIndependently()
        {
            BenchmarkProgress.BeginSession();
            BenchmarkRunProgress alpha = BenchmarkProgress.BeginRun("alpha", 4, true);
            BenchmarkRunProgress beta = BenchmarkProgress.BeginRun("beta", 6, true);
            alpha.StartScenario("G1 · Spawn", 100f);
            beta.StartScenario("G6 · Free build", 570f);
            alpha.CompleteScenario("✅ Spawn — 100");
            beta.CompleteScenario("❌ Castle — 12");
            beta.CompleteScenario("✅ Win — 95");

            Assert.AreEqual(10, BenchmarkProgress.Total);
            Assert.AreEqual(3, BenchmarkProgress.Completed);
            Assert.AreEqual("2 targets", BenchmarkProgress.ModelId);
            Assert.IsFalse(BenchmarkProgress.HasScenarioClock, "No single scenario clock with several runs.");
            CollectionAssert.AreEqual(new[] { "alpha · ✅ Spawn — 100", "beta · ❌ Castle — 12", "beta · ✅ Win — 95" },
                BenchmarkProgress.RecentLines().Select(l => l.Left).ToArray());

            IReadOnlyList<BenchmarkRunSnapshot> runs = BenchmarkProgress.Runs();
            Assert.AreEqual(2, runs.Count);
            Assert.AreEqual("alpha", runs[0].DisplayName);
            Assert.AreEqual(1, runs[0].Completed);
            Assert.AreEqual("G6 · Free build", runs[1].CurrentLabel);
            Assert.IsTrue(runs[1].HasScenarioClock);
            Assert.AreEqual(2f / 6f, runs[1].Fraction, 1e-5);

            Assert.IsTrue(BenchmarkProgress.RequestStop("alpha"));
            Assert.IsTrue(alpha.StopRequested);
            Assert.IsFalse(beta.StopRequested, "Stopping one target leaves the others running.");
            Assert.IsFalse(BenchmarkProgress.RequestStop("missing"));

            alpha.End();
            Assert.AreEqual("1 of 2 targets running", BenchmarkProgress.CurrentLabel);
            Assert.IsTrue(BenchmarkProgress.IsRunning);
            Assert.IsFalse(BenchmarkProgress.RequestStop("alpha"), "A finished run cannot be stopped.");

            BenchmarkProgress.RequestStop();
            Assert.IsTrue(beta.StopRequested, "The session stop reaches every run.");
            BenchmarkProgress.EndSession();
            Assert.IsFalse(BenchmarkProgress.IsRunning);
            Assert.IsFalse(BenchmarkProgress.Runs()[1].IsActive);
        }

        [Test]
        public void TryBuildTargetSpec_SkipsDisabledRowsAndReportsBadOnes()
        {
            List<TargetRow> rows = new()
            {
                new TargetRow { model = "m1", baseUrl = "http://127.0.0.1:8831/v1", apiKey = "sk-live" },
                new TargetRow { enabled = false, model = "", baseUrl = "" },
                new TargetRow { model = "m2", baseUrl = "http://127.0.0.1:8832/v1", label = "two" }
            };

            Assert.IsTrue(GameCreationBenchmarkLauncher.TryBuildTargetSpec(rows, out string spec, out string error),
                error);
            List<BenchmarkTarget> targets = BenchmarkTargetList.Parse(spec);
            Assert.AreEqual(2, targets.Count);
            Assert.AreEqual("sk-live", targets[0].ApiKey, "The launched spec carries this session's key.");
            Assert.AreEqual("two", targets[1].DisplayName);

            Assert.IsTrue(GameCreationBenchmarkLauncher.TryBuildTargetSpec(new List<TargetRow>(), out spec, out _));
            Assert.IsNull(spec, "No rows means the single-model run.");

            rows.Add(new TargetRow { model = "m3", baseUrl = "localhost:8833" });
            Assert.IsFalse(GameCreationBenchmarkLauncher.TryBuildTargetSpec(rows, out _, out error));
            StringAssert.Contains("row 4", error);
        }

        [Test]
        public void SaveTargetRows_NeverStoresALiteralKey()
        {
            GameCreationBenchmarkLauncher.SaveTargetRows(new[]
            {
                new TargetRow { model = "m1", baseUrl = "http://a/v1", apiKey = "sk-live-secret" },
                new TargetRow { model = "m2", baseUrl = "http://b/v1", apiKey = "env:PROVIDER_KEY", enabled = false }
            });

            StringAssert.DoesNotContain("sk-live-secret",
                EditorPrefs.GetString(GameCreationBenchmarkLauncher.PrefTargets, ""));
            List<TargetRow> loaded = GameCreationBenchmarkLauncher.LoadSavedTargetRows();
            Assert.AreEqual(2, loaded.Count);
            Assert.AreEqual("", loaded[0].apiKey);
            Assert.AreEqual("env:PROVIDER_KEY", loaded[1].apiKey);
            Assert.IsFalse(loaded[1].enabled);
        }

        [Test]
        public void CliArguments_SetTargetsAndParallel()
        {
            Environment.SetEnvironmentVariable(GameCreationBenchmarkLauncher.EnvTargets, null);
            string[] args =
            {
                "Unity.exe", "-batchmode", GameCreationBenchmarkLauncher.ArgTargets,
                "a|m1|http://127.0.0.1:8831/v1;m2|http://127.0.0.1:8832/v1",
                GameCreationBenchmarkLauncher.ArgParallel, "2", GameCreationBenchmarkLauncher.ArgGroups, "G1,G6"
            };

            Assert.IsTrue(GameCreationBenchmarkLauncher.TryApplyCliArguments(args, out string error), error);
            Assert.AreEqual(2, BenchmarkTargetList.Parse(
                Environment.GetEnvironmentVariable(GameCreationBenchmarkLauncher.EnvTargets)).Count);
            Assert.AreEqual("2", Environment.GetEnvironmentVariable(GameCreationBenchmarkLauncher.EnvParallel));
            Assert.AreEqual("G1,G6", Environment.GetEnvironmentVariable(GameCreationBenchmarkLauncher.EnvGroups));
        }

        [Test]
        public void CliArguments_KeepAnEnvTargetListAndRejectBadInput()
        {
            Environment.SetEnvironmentVariable(GameCreationBenchmarkLauncher.EnvTargets, "m|http://127.0.0.1:8831/v1");
            Assert.IsTrue(GameCreationBenchmarkLauncher.TryApplyCliArguments(new[] { "Unity.exe" }, out string error),
                error);
            Assert.AreEqual("m|http://127.0.0.1:8831/v1",
                Environment.GetEnvironmentVariable(GameCreationBenchmarkLauncher.EnvTargets),
                "An absent argument leaves the env var alone.");

            Assert.IsFalse(GameCreationBenchmarkLauncher.TryApplyCliArguments(
                new[] { "Unity.exe", GameCreationBenchmarkLauncher.ArgTargets, "no-url-here" }, out error));
            StringAssert.Contains("no http(s) base URL", error);

            Assert.IsFalse(GameCreationBenchmarkLauncher.TryApplyCliArguments(
                new[] { "Unity.exe", GameCreationBenchmarkLauncher.ArgParallel, "many" }, out error));
            StringAssert.Contains(GameCreationBenchmarkLauncher.ArgParallel, error);
        }

        [Test]
        public void ConfigureTargets_ClearsAStaleListForASingleModelRun()
        {
            GameCreationBenchmarkLauncher.ConfigureTargets("m|http://127.0.0.1:8831/v1", 3);
            Assert.AreEqual("3", Environment.GetEnvironmentVariable(GameCreationBenchmarkLauncher.EnvParallel));

            GameCreationBenchmarkLauncher.ConfigureTargets(null, 3);
            Assert.IsNull(Environment.GetEnvironmentVariable(GameCreationBenchmarkLauncher.EnvTargets));
            Assert.IsNull(Environment.GetEnvironmentVariable(GameCreationBenchmarkLauncher.EnvParallel));
        }

        [Test]
        public void RunCompanionFiles_SeparatesTargetsThatShareARunId()
        {
            const string plain = "BENCHMARK_20261015_101500_qwen";
            const string labelled = plain + "__q8";
            foreach (string name in new[]
                     {
                         plain + ".json", plain + ".md", plain + ".tools.jsonl", plain + "_modelcard.png",
                         plain + "_g6_free_build_hero.png", labelled + ".json", labelled + ".md",
                         labelled + "_modelcard.png", labelled + "_g6_free_build_hero.png"
                     })
            {
                File.WriteAllText(Path.Combine(_tempDir, name), "x");
            }

            List<string> files = GameCreationBenchmarkLauncher.RunCompanionFiles(_tempDir, plain)
                .Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToList();
            CollectionAssert.AreEqual(new[]
            {
                plain + ".json", plain + ".md", plain + ".tools.jsonl", plain + "_g6_free_build_hero.png",
                plain + "_modelcard.png"
            }, files);

            Assert.IsTrue(GameCreationBenchmarkLauncher.DeleteRun(new GameCreationBenchmarkLauncher.RunEntry
            {
                JsonPath = Path.Combine(_tempDir, plain + ".json"), MdPath = Path.Combine(_tempDir, plain + ".md")
            }));
            Assert.IsTrue(File.Exists(Path.Combine(_tempDir, labelled + ".json")));
            Assert.IsTrue(File.Exists(Path.Combine(_tempDir, labelled + "_g6_free_build_hero.png")),
                "Deleting one target's run must keep its sibling's images.");
        }

        [Test]
        public void LauncherWatchdog_StaysAboveTheSixHourBackstop()
        {
            Assert.Greater(GameCreationBenchmarkLauncher.TimeoutSeconds, 6 * 3600d);
        }

        [TestCase(null, true, "")]
        [TestCase("", true, "")]
        [TestCase(" HIGH ", true, "high")]
        [TestCase("none", true, "none")]
        [TestCase("xhigh", true, "xhigh")]
        [TestCase("max", false, "")]
        [TestCase("hi", false, "")]
        public void TryNormalizeReasoningEffort_AcceptsOnlyTheKnownLevels(string raw, bool valid, string expected)
        {
            Assert.AreEqual(valid, BenchmarkTargetList.TryNormalizeReasoningEffort(raw, out string effort));
            Assert.AreEqual(expected, effort);
        }

        [Test]
        public void Parse_Json_ReadsEffortAndExtraBodyPerTarget()
        {
            const string json = "[" +
                                "{\"model\":\"m1\",\"baseUrl\":\"http://h:1/v1\",\"reasoningEffort\":\"HIGH\"," +
                                "\"extraBody\":{\"top_k\":20,\"provider\":{\"order\":[\"x\"]}}}," +
                                "{\"model\":\"m2\",\"baseUrl\":\"http://h:2/v1\",\"effort\":\"low\"," +
                                "\"extra_body\":\"{\\\"min_p\\\":0.1}\"}," +
                                "{\"model\":\"m3\",\"baseUrl\":\"http://h:3/v1\",\"reasoning_effort\":\"\",\"extraBody\":null}" +
                                "]";

            List<BenchmarkTarget> targets = BenchmarkTargetList.Parse(json);

            Assert.AreEqual("high", targets[0].ReasoningEffort);
            Assert.AreEqual("{\"top_k\":20,\"provider\":{\"order\":[\"x\"]}}", targets[0].ExtraBodyJson);
            Assert.AreEqual("low", targets[1].ReasoningEffort);
            Assert.AreEqual("{\"min_p\":0.1}", targets[1].ExtraBodyJson, "A string holding an object is accepted.");
            Assert.AreEqual("", targets[2].ReasoningEffort);
            Assert.AreEqual("", targets[2].ExtraBodyJson);
            Assert.AreEqual("medium", targets[2].EffectiveReasoningEffort("Medium"), "The suite default fills in.");
            Assert.AreEqual("high", targets[0].EffectiveReasoningEffort("low"), "A target's own effort wins.");
        }

        [Test]
        public void Parse_Compact_FifthFieldIsTheEffort()
        {
            List<BenchmarkTarget> targets = BenchmarkTargetList.Parse(
                "lab|m1|http://127.0.0.1:8831/v1|sk-1|XHIGH;m2|http://127.0.0.1:8832/v1||medium;" +
                "m3|http://127.0.0.1:8833/v1|sk-3");

            AssertTarget(targets[0], "lab", "m1", "http://127.0.0.1:8831/v1", "sk-1");
            Assert.AreEqual("xhigh", targets[0].ReasoningEffort);
            AssertTarget(targets[1], "", "m2", "http://127.0.0.1:8832/v1", "");
            Assert.AreEqual("medium", targets[1].ReasoningEffort);
            Assert.AreEqual("", targets[2].ReasoningEffort);
            Assert.AreEqual("sk-3", targets[2].ApiKey);
        }

        [Test]
        public void ToJson_RoundTripsEffortAndExtraBody()
        {
            List<BenchmarkTarget> targets = BenchmarkTargetList.Parse(
                "[{\"label\":\"a\",\"model\":\"m\",\"baseUrl\":\"http://h:1/v1\",\"reasoningEffort\":\"high\"," +
                "\"extraBody\":{\"top_k\":20}}]");

            List<BenchmarkTarget> back = BenchmarkTargetList.Parse(BenchmarkTargetList.ToJson(targets, false));

            Assert.AreEqual("high", back[0].ReasoningEffort);
            Assert.AreEqual("{\"top_k\":20}", back[0].ExtraBodyJson);
            Assert.AreEqual("a", back[0].Label);
        }

        [Test]
        public void TryBuildTargetSpec_CarriesAndValidatesEffortAndExtraBody()
        {
            List<TargetRow> rows = new()
            {
                new TargetRow
                {
                    model = "m1", baseUrl = "http://127.0.0.1:8831/v1", reasoningEffort = "High",
                    extraBody = "{ \"top_k\": 20 }"
                }
            };

            Assert.IsTrue(GameCreationBenchmarkLauncher.TryBuildTargetSpec(rows, out string spec, out string error),
                error);
            BenchmarkTarget target = BenchmarkTargetList.Parse(spec)[0];
            Assert.AreEqual("high", target.ReasoningEffort);
            Assert.AreEqual("{\"top_k\":20}", target.ExtraBodyJson);

            rows[0].reasoningEffort = "ultra";
            Assert.IsFalse(GameCreationBenchmarkLauncher.TryBuildTargetSpec(rows, out _, out error));
            StringAssert.Contains("reasoning effort 'ultra'", error);

            rows[0].reasoningEffort = "";
            rows[0].extraBody = "{\"model\":\"other\"}";
            Assert.IsFalse(GameCreationBenchmarkLauncher.TryBuildTargetSpec(rows, out _, out error));
            StringAssert.Contains("controlled by CoreAI", error);

            rows[0].extraBody = "{\"top_k\":";
            Assert.IsFalse(GameCreationBenchmarkLauncher.TryBuildTargetSpec(rows, out _, out error));
            StringAssert.Contains("one valid JSON object", error);
        }

        [Test]
        public void SaveTargetRows_KeepsEffortAndExtraBody()
        {
            GameCreationBenchmarkLauncher.SaveTargetRows(new[]
            {
                new TargetRow
                {
                    model = "m1", baseUrl = "http://a/v1", reasoningEffort = "low", extraBody = "{\"top_k\":20}"
                }
            });

            TargetRow loaded = GameCreationBenchmarkLauncher.LoadSavedTargetRows()[0];
            Assert.AreEqual("low", loaded.reasoningEffort);
            Assert.AreEqual("{\"top_k\":20}", loaded.extraBody);
        }

        [Test]
        public void CliArguments_SetAndValidateTheReasoningEffort()
        {
            Environment.SetEnvironmentVariable(GameCreationBenchmarkLauncher.EnvTargets, null);
            Environment.SetEnvironmentVariable(GameCreationBenchmarkLauncher.EnvReasoningEffort, null);
            Assert.IsTrue(GameCreationBenchmarkLauncher.TryApplyCliArguments(
                new[] { "Unity.exe", GameCreationBenchmarkLauncher.ArgReasoningEffort, "High" }, out string error), error);
            Assert.AreEqual("high", Environment.GetEnvironmentVariable(GameCreationBenchmarkLauncher.EnvReasoningEffort));

            Assert.IsFalse(GameCreationBenchmarkLauncher.TryApplyCliArguments(
                new[] { "Unity.exe", GameCreationBenchmarkLauncher.ArgReasoningEffort, "ultra" }, out error));
            StringAssert.Contains(GameCreationBenchmarkLauncher.ArgReasoningEffort, error);

            Environment.SetEnvironmentVariable(GameCreationBenchmarkLauncher.EnvReasoningEffort, "turbo");
            Assert.IsFalse(GameCreationBenchmarkLauncher.TryApplyCliArguments(new[] { "Unity.exe" }, out error));
            StringAssert.Contains(GameCreationBenchmarkLauncher.EnvReasoningEffort, error);
            Environment.SetEnvironmentVariable(GameCreationBenchmarkLauncher.EnvReasoningEffort, null);

            Assert.IsFalse(GameCreationBenchmarkLauncher.TryApplyCliArguments(new[]
            {
                "Unity.exe", GameCreationBenchmarkLauncher.ArgTargets,
                "[{\"model\":\"m\",\"baseUrl\":\"http://127.0.0.1:8831/v1\",\"extraBody\":{\"tools\":[]}}]"
            }, out error));
            StringAssert.Contains("controlled by CoreAI", error);
        }

        [Test]
        public void ConfigureReasoningEffort_SetsAndClears()
        {
            GameCreationBenchmarkLauncher.ConfigureReasoningEffort("xhigh");
            Assert.AreEqual("xhigh", Environment.GetEnvironmentVariable(GameCreationBenchmarkLauncher.EnvReasoningEffort));
            GameCreationBenchmarkLauncher.ConfigureReasoningEffort("");
            Assert.IsNull(Environment.GetEnvironmentVariable(GameCreationBenchmarkLauncher.EnvReasoningEffort));
        }

        [TestCase("high", "reasoning high")]
        [TestCase("", "reasoning provider default")]
        public void ReasoningEffort_RoundTripsThroughTheReportJsonAndMarkdown(string effort, string markdown)
        {
            BenchmarkReport report = new()
            {
                Metadata = new BenchmarkRunMetadata
                {
                    RunId = "20261015_101500", ModelId = "m", ReasoningEffort = effort, SuiteVersion = "1.16"
                }
            };
            report.Add(new ScenarioResult
            {
                ScenarioId = "s", ScenarioName = "S", Group = "G2", ModelId = "m",
                Score = GoalScore.Compute(new[] { new BenchmarkCheckpoint("c", "check", 10, true) })
            });

            string json = BenchmarkReportFormatter.ToJson(report);
            StringAssert.Contains($"\"reasoningEffort\":\"{effort}\"", json);
            Assert.AreEqual(effort, ModelComparison.ParseSummaryJson(json).ReasoningEffort);
            Assert.AreEqual(effort, ModelSummary.From(report).ReasoningEffort);
            StringAssert.Contains(markdown, BenchmarkReportFormatter.ToMarkdown(report));
        }

        private static void AssertTarget(BenchmarkTarget target, string label, string model, string baseUrl,
            string key)
        {
            Assert.AreEqual(label, target.Label, "label");
            Assert.AreEqual(model, target.Model, "model");
            Assert.AreEqual(baseUrl, target.BaseUrl, "baseUrl");
            Assert.AreEqual(key, target.ApiKey, "apiKey");
        }
    }
}
