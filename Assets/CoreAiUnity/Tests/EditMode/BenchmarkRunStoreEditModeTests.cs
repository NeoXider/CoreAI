using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CoreAI.Benchmarking;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>Portable checkpoint, compatibility, interruption and legacy-report repair regression tests.</summary>
    public sealed class BenchmarkRunStoreEditModeTests
    {
        private string _root;

        [Test]
        public void ProductionStyleCompanionRestore_RefreshesBaselineAfterRecoveredEvidence()
        {
            const string json = "{\"metadata\":{\"runId\":\"run\",\"modelId\":\"model\"},\"summary\":{\"meanEfficiencyBonus\":1.234568},\"results\":[{\"scenarioId\":\"scenario\",\"scenarioName\":\"Scenario\",\"group\":\"G1\",\"base\":95,\"bonus\":4,\"total\":99,\"classification\":\"Pass\"}]}";
            const string markdown = "| Scenario | G1 | 95 | 4 (1.2) | 99 | PASS | 1 |\n\n## Full model session\n\n### G1 \u00b7 Scenario\n\nOriginal transcript.\n";
            BenchmarkTargetState state = BenchmarkRunStore.ImportReport(json);
            BenchmarkLegacyReportEvidence.Restore(state, markdown, null);
            state.RefreshSummaryBaseline();
            JObject baseline = BenchmarkJson.ParseObject(state.BaselineSummaryJson);
            Assert.AreEqual(1.2, (double)baseline["meanEfficiencyBonus"]);
            JObject repaired = BenchmarkJson.ParseObject(BenchmarkReportFormatter.ToJson(state.ToReport()));
            Assert.AreEqual(1.234568, (double)repaired["summary"]["meanEfficiencyBonus"]);
        }

        [Test]
        public void ArtifactRecovery_AmbiguousSuccessfulHeroOwnershipRefusesInsteadOfSwappingImages()
        {
            BenchmarkTargetState state = new()
            {
                Results = new List<ScenarioResult>
                {
                    new() { ScenarioId = "g6_free_build", Group = "G6" },
                    new() { ScenarioId = "g6_free_build_vision", Group = "G6" }
                }
            };
            File.WriteAllBytes(Path.Combine(_root, "report_g6_free_build_hero.png"), new byte[] { 1 });
            InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
                BenchmarkArtifactRecovery.RestoreVisualArtifacts(state, _root, "report"));
            StringAssert.Contains("owns the hero image", error.Message);
            Assert.IsNull(state.Results[0].SceneScreenshotPng);
            Assert.IsNull(state.Results[1].SceneScreenshotPng);
        }

        [Test]
        public void NeedsWork_CompletedStateRepairsMissingMarkerAndMissingReportReopensTarget()
        {
            BenchmarkRunManifest manifest = new() { RunId = "run" };
            BenchmarkStoredTarget target = BenchmarkStoredTarget.Deferred(new BenchmarkTarget("model", "http://localhost/v1"), "target");
            manifest.Targets.Add(target);
            BenchmarkRunStore store = new(_root, "run");
            store.Save("target", new BenchmarkTargetState
            {
                ReportsWritten = true, Metadata = new BenchmarkRunMetadata { RunId = "run", ModelId = "model" }
            });
            File.WriteAllText(Path.Combine(_root, "target.json"), "{}");
            File.Delete(store.CompletionPath("target"));
            Assert.IsFalse(store.NeedsWork(manifest, target));
            Assert.IsTrue(File.Exists(store.CompletionPath("target")));
            File.Delete(Path.Combine(_root, "target.json"));
            Assert.IsTrue(store.NeedsWork(manifest, target));
        }

        [Test]
        public void BenchmarkJson_PreservesIsoStringsAndExactRawResultObjectText()
        {
            const string timestamp = "2026-10-15T17:48:50.1764570Z";
            const string row = "{ \"value\":7.1137000000000006, \"date\":\"2026-10-15T17:48:50.1764570Z\", \"text\":\"} escaped \\\"\" }";
            foreach (string newline in new[] { "\n", "\r\n" })
            {
                string source = "{" + newline + "\"metadata\":{\"timestampUtc\":\"" + timestamp + "\"}," + newline + "\"results\": [" + newline + row + newline + "]}";
                JObject document = BenchmarkJson.ParseObject(source);
                Assert.AreEqual(JTokenType.String, document["metadata"]["timestampUtc"].Type);
                Assert.AreEqual(timestamp, document["metadata"].ToObject<BenchmarkRunMetadata>().TimestampUtc);
                CollectionAssert.AreEqual(new[] { row }, BenchmarkJson.RawArrayObjects(source, "results"));
            }
        }

        [Test]
        public void Store_RoundTripsMetadataAndSegmentTimestampStringsWithoutDateCoercion()
        {
            const string timestamp = "2026-10-15T17:48:50.1764570Z";
            BenchmarkTargetState state = new()
            {
                Metadata = new BenchmarkRunMetadata { RunId = "run", TimestampUtc = timestamp },
                Segments = new List<BenchmarkRunSegment> { new BenchmarkRunSegment { StartedUtc = timestamp, WallSeconds = 2.25 } }
            };
            BenchmarkRunStore store = new(_root, "run");
            store.Save("target", state);
            BenchmarkTargetState loaded = store.Load("target");
            Assert.AreEqual(timestamp, loaded.Metadata.TimestampUtc);
            Assert.AreEqual(timestamp, loaded.Segments[0].StartedUtc);
        }

        [TestCase(FailureAttribution.Model)]
        [TestCase(FailureAttribution.Environment)]
        public void ArtifactRecovery_FailedVisionKeepsTextHeroReplayAndItsOwnScene(FailureAttribution attribution)
        {
            ScenarioResult text = new() { ScenarioId = "g6_free_build", Group = "G6" };
            ScenarioResult vision = new()
            {
                ScenarioId = "g6_free_build_vision", Group = "G6", Failure = "Interrupted vision build", Attribution = attribution
            };
            BenchmarkTargetState state = new() { PreserveResultOrder = true, Results = new List<ScenarioResult> { text, vision } };
            byte[] hero = { 1, 2, 3 };
            byte[] separate = { 4, 5, 6 };
            File.WriteAllBytes(Path.Combine(_root, "report_g6_free_build_hero.png"), hero);
            File.WriteAllBytes(Path.Combine(_root, "report_g6_free_build.png"), new byte[] { 99 });
            File.WriteAllBytes(Path.Combine(_root, "report_g6_free_build_vision.png"), separate);
            File.WriteAllText(Path.Combine(_root, "report_g6_replay.lua"), "return 'text replay'");
            Assert.AreSame(text, BenchmarkArtifactRecovery.RestoreVisualArtifacts(state, _root, "report"));
            Assert.AreSame(text, BenchmarkArtifactRecovery.FindFreeBuildHeroResult(state.Results));
            CollectionAssert.AreEqual(hero, text.SceneScreenshotPng);
            CollectionAssert.AreEqual(separate, vision.SceneScreenshotPng);
            Assert.AreEqual("return 'text replay'", text.LuaReplayScript);
            Assert.IsEmpty(vision.LuaReplayScript);
            if (attribution == FailureAttribution.Environment)
            {
                state.Merge(new ScenarioResult { ScenarioId = vision.ScenarioId, Group = "G6" }, new[]
                {
                    new BenchmarkPlannedScenario { Id = text.ScenarioId, Group = "G6", Repetition = 1 },
                    new BenchmarkPlannedScenario { Id = vision.ScenarioId, Group = "G6", Repetition = 1 }
                });
                Assert.AreSame(text, BenchmarkArtifactRecovery.FindFreeBuildHeroResult(state.Results));
                CollectionAssert.AreEqual(hero, text.SceneScreenshotPng);
                Assert.IsNotEmpty(text.LuaReplayScript);
            }
        }

        [Test]
        public void LegacyMerge_ReplacementsKeepOriginalPositionsDespiteDifferentPlanOrder()
        {
            BenchmarkTargetState state = new() { PreserveResultOrder = true };
            ScenarioResult visual = new() { ScenarioId = "visual", Group = "G6" };
            state.Results.Add(visual);
            state.Results.Add(new ScenarioResult { ScenarioId = "regular", Group = "G1", Attribution = FailureAttribution.Environment });
            BenchmarkPlannedScenario[] plan =
            {
                new() { Id = "regular", Group = "G1", Repetition = 1 },
                new() { Id = "visual", Group = "G6", Repetition = 1 },
                new() { Id = "missing", Group = "G2", Repetition = 1 }
            };
            ScenarioResult replacement = new() { ScenarioId = "regular", Group = "G1" };
            state.Merge(replacement, plan);
            state.Merge(new ScenarioResult { ScenarioId = "missing", Group = "G2" }, plan);
            CollectionAssert.AreEqual(new[] { "visual", "regular", "missing" }, state.Results.Select(result => result.ScenarioId));
            Assert.AreSame(visual, state.Results[0]);
            Assert.AreSame(replacement, state.Results[1]);
        }

        [Test]
        public void BackupReportFiles_SameClockTickAcrossTargetsCannotCollideOnIndex()
        {
            File.WriteAllText(Path.Combine(_root, "BENCHMARK_run_a.json"), "a");
            File.WriteAllText(Path.Combine(_root, "BENCHMARK_run_b.json"), "b");
            File.WriteAllText(Path.Combine(_root, "INDEX.md"), "original index");
            BenchmarkRunStore.BackupReportFiles(_root, "BENCHMARK_run_a", "same_tick");
            BenchmarkRunStore.BackupReportFiles(_root, "BENCHMARK_run_b", "same_tick");
            BenchmarkRunStore.BackupReportFiles(_root, "BENCHMARK_run_a", "same_tick");
            string[] backups = Directory.GetFiles(_root, "INDEX.md.bak-*");
            Assert.AreEqual(3, backups.Length);
            foreach (string path in backups) Assert.AreEqual("original index", File.ReadAllText(path));
        }

        [Test]
        public void RetryPolicyAndParallelExecution_AreIndependentOfSavedSecretSource()
        {
            Assert.IsTrue(BenchmarkRunManifest.OptionMatches("COREAI_BENCHMARK_RETRIES", "4", "1"));
            BenchmarkRunManifest manifest = new() { UseDefaultSecretSource = true, ParallelExecution = false };
            manifest.UseDefaultSecretSource = false;
            Assert.IsFalse(manifest.ParallelExecution);
            manifest.ParallelExecution = true;
            manifest.UseDefaultSecretSource = true;
            Assert.IsTrue(manifest.ParallelExecution);
        }

        [Test]
        public void BenchmarkingAssembly_UsesPrecompiledNewtonsoftWithoutAnAsmdefReference()
        {
            DirectoryInfo root = new(Directory.GetCurrentDirectory());
            while (root != null && !File.Exists(Path.Combine(root.FullName, "Assets/CoreAI/Runtime/Core/CoreAI.Core.asmdef"))) root = root.Parent;
            Assert.IsNotNull(root, "The repository root must be available to validate shipped assembly references.");
            JObject definition = BenchmarkJson.ParseObject(File.ReadAllText(Path.Combine(root.FullName,
                "Assets/CoreAI/Runtime/Core/Features/Benchmarking/CoreAI.Benchmarking.asmdef")));
            Assert.IsTrue((bool)definition["overrideReferences"]);
            CollectionAssert.Contains(definition["precompiledReferences"].Values<string>(), "Newtonsoft.Json.dll");
            CollectionAssert.DoesNotContain(definition["references"].Values<string>(), "Unity.Newtonsoft.Json");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AtomicWrite_RetriesTransientCommitFailuresWithoutLosingPreviousData(bool accessDenied)
        {
            string path = Path.Combine(_root, "checkpoint.json");
            File.WriteAllText(path, "original");
            int attempts = 0;
            List<int> backoffs = new();
            BenchmarkRunStore.AtomicWrite(path, "replacement", (temporary, destination) =>
            {
                attempts++;
                if (attempts < 4)
                {
                    Assert.AreEqual("original", File.ReadAllText(destination));
                    if (accessDenied) throw new UnauthorizedAccessException("Transient file scanner lock");
                    throw new IOException("Transient file scanner lock");
                }
                File.Replace(temporary, destination, null);
            }, milliseconds => backoffs.Add(milliseconds));
            Assert.AreEqual(4, attempts);
            CollectionAssert.AreEqual(new[] { 15, 30, 60 }, backoffs);
            Assert.AreEqual("replacement", File.ReadAllText(path));
            Assert.IsEmpty(Directory.GetFiles(_root, "*.tmp-*"));
        }

        [Test]
        public void AtomicWrite_PermanentCommitFailurePreservesOriginalAndCleansTemporary()
        {
            string path = Path.Combine(_root, "checkpoint.json");
            File.WriteAllText(path, "original");
            int attempts = 0;
            Assert.Throws<IOException>(() => BenchmarkRunStore.AtomicWrite(path, "replacement", (temporary, destination) =>
            {
                attempts++;
                throw new IOException("Permanent commit failure");
            }, milliseconds => { }));
            Assert.AreEqual(4, attempts);
            Assert.AreEqual("original", File.ReadAllText(path));
            Assert.IsEmpty(Directory.GetFiles(_root, "*.tmp-*"));
        }

        [Test]
        public void IncompleteRuns_SkipsInvalidAndCorruptFoldersAndNeverReadsLargeStates()
        {
            BenchmarkRunStore completed = new(_root, "20261016_114850");
            completed.SaveManifest(Manifest("20261016_114850"));
            completed.Save("target", new BenchmarkTargetState { ReportsWritten = true });
            File.WriteAllText(Path.Combine(completed.DirectoryPath, "target.state.json"), "unreadable state with a valid completion marker");
            Assert.IsEmpty(BenchmarkRunStore.ListIncompleteRuns(_root));
            File.Delete(completed.CompletionPath("target"));
            CollectionAssert.AreEqual(new[] { "20261016_114850" }, BenchmarkRunStore.ListIncompleteRuns(_root));
            completed.MarkCompleted("target");
            string invalid = Path.Combine(_root, ".runs", "bad.id");
            Directory.CreateDirectory(invalid);
            File.WriteAllText(Path.Combine(invalid, "manifest.json"), "{}");
            string corrupt = Path.Combine(_root, ".runs", "corrupt");
            Directory.CreateDirectory(corrupt);
            File.WriteAllText(Path.Combine(corrupt, "manifest.json"), "{torn");
            Dictionary<string, string> invalidManifests = new()
            {
                ["empty_object"] = "{}", ["null_manifest"] = "null",
                ["wrong_id"] = "{\"RunId\":\"other\",\"Targets\":[{\"Slug\":\"target\"}]}",
                ["wrong_version"] = "{\"RunId\":\"wrong_version\",\"FormatVersion\":2,\"Targets\":[{\"Slug\":\"target\"}]}",
                ["no_targets"] = "{\"RunId\":\"no_targets\",\"Targets\":[]}"
            };
            foreach (KeyValuePair<string, string> entry in invalidManifests)
            {
                string folder = Path.Combine(_root, ".runs", entry.Key);
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "manifest.json"), entry.Value);
            }
            List<string> warnings = new();
            Assert.IsEmpty(BenchmarkRunStore.ListIncompleteRuns(_root, warnings.Add));
            Assert.AreEqual(2 + invalidManifests.Count, warnings.Count);
            StringAssert.Contains("Skipped benchmark run folder", warnings[0]);
        }

        [Test]
        public void BackupReportFiles_IncludesAllOverwrittenArtifactsAndExcludesOtherTargets()
        {
            const string stem = "BENCHMARK_run_model";
            string[] suffixes = { ".json", ".md", ".svg", ".tools.jsonl", "_modelcard.svg", "_modelcard.png",
                "_g6_free_build_hero.png", "_g6_free_build_vision.png", "_g6_replay.lua", "_g1_scenario.png" };
            foreach (string suffix in suffixes) File.WriteAllText(Path.Combine(_root, stem + suffix), suffix);
            File.WriteAllText(Path.Combine(_root, "INDEX.md"), "index");
            File.WriteAllText(Path.Combine(_root, stem + "__other.json"), "other");
            File.WriteAllText(Path.Combine(_root, stem + "__other_g6.png"), "other image");
            File.WriteAllText(Path.Combine(_root, stem + ".json.bak-old"), "old backup");
            IReadOnlyList<string> backups = BenchmarkRunStore.BackupReportFiles(_root, stem, "stamp");
            Assert.AreEqual(suffixes.Length + 1, backups.Count);
            foreach (string suffix in suffixes)
                Assert.AreEqual(suffix, File.ReadAllText(Path.Combine(_root, stem + suffix + ".bak-stamp")));
            Assert.IsFalse(File.Exists(Path.Combine(_root, stem + "__other.json.bak-stamp")));
            Assert.IsFalse(File.Exists(Path.Combine(_root, stem + ".json.bak-old.bak-stamp")));
        }

        [Test]
        public void LegacySegment_UsesMeasuredTimeAndPreservesExistingSegments()
        {
            BenchmarkTargetState state = new() { LegacyWallSeconds = 120 };
            state.Results.Add(new ScenarioResult { LatencyMs = 34000 });
            state.SeedLegacySegment();
            Assert.AreEqual(120, state.Segments[0].WallSeconds);
            state.SeedLegacySegment();
            Assert.AreEqual(1, state.Segments.Count);
            state.Segments.Clear();
            state.LegacyWallSeconds = 0;
            state.SeedLegacySegment();
            Assert.AreEqual(34, state.Segments[0].WallSeconds);
        }

        [Test]
        public void LegacyTargetValidation_IgnoresUnselectedConfigurationAndRefusesSelectedMismatch()
        {
            BenchmarkTarget unselected = new("other", "http://localhost/v1?key=secret", "literal", "other", "high",
                "{\"refresh_token\":\"sensitive\"}", 1);
            BenchmarkStoredTarget deferred = BenchmarkStoredTarget.Deferred(unselected, "other");
            Assert.IsTrue(deferred.ConfigurationDeferred);
            StringAssert.DoesNotContain("secret", deferred.BaseUrl);
            Assert.IsEmpty(deferred.ExtraBodyJson);
            BenchmarkRunMetadata metadata = new() { ModelId = "other", ReasoningEffort = "low", Streaming = true };
            deferred.ValidateLegacyMetadata(metadata, false, false, false);
            Assert.Throws<InvalidOperationException>(() => deferred.ValidateLegacyMetadata(metadata, true, false, false));
        }

        [TestCase("other", "suite", "model")]
        [TestCase("run", "other", "model")]
        [TestCase("run", "suite", "other")]
        public void CheckpointIdentity_RefusesStateFromAnotherRunSuiteOrTarget(string run, string suite, string model)
        {
            BenchmarkTargetState state = new()
            {
                Metadata = new BenchmarkRunMetadata { RunId = "run", SuiteVersion = "suite", ModelId = "model" }
            };
            state.ValidateIdentity("run", "suite", "model");
            Assert.Throws<InvalidOperationException>(() => state.ValidateIdentity(run, suite, model));
        }

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "CoreAiRunStore_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            Directory.Delete(_root, true);
        }

        [Test]
        public void RoundTrip_PreservesScoresMetricsTraceReplayAndScreenshot()
        {
            BenchmarkRunStore store = new(_root, "20261015_224850");
            BenchmarkTargetState state = new();
            ScenarioResult result = Result(Plan()[1]);
            result.Score = new GoalScore(98, 2, 95, 94, 0.25, 97, 3, 2, 1, 3, 6, 95, 101,
                BenchmarkClassification.Pass);
            result.Attribution = FailureAttribution.Model;
            result.Failure = "Measured deadline";
            result.TimedOut = true;
            result.SceneScreenshotPng = new byte[] { 137, 80, 78, 71, 1, 2, 3 };
            result.ToolTraceJsonl = "{\"tool\":\"spawn\",\"arguments\":{\"name\":\"castle\"}}\n";
            result.LuaReplayScript = "return workspace.Castle\n";
            result.SessionTranscript = "prompt\nanswer";
            result.CastlePrefabPath = "Assets/Benchmark/model/castle.prefab";
            result.Checkpoints = new[] { new BenchmarkCheckpoint("cp", "Gate", 3, true, true, "detail", BenchmarkDimension.Reasoning) };
            result.Penalties = new[] { new BenchmarkPenalty("Extra call", 2) };
            state.Results.Add(result);
            state.Metadata.ModelId = "model";
            state.Segments.Add(new BenchmarkRunSegment { StartedUtc = "2026-10-15T22:48:50Z", WallSeconds = 12.5 });
            store.Save("model", state);

            BenchmarkTargetState loaded = store.Load("model");
            Assert.AreEqual(JsonConvert.SerializeObject(state), JsonConvert.SerializeObject(loaded));
            Assert.AreEqual(95, loaded.Results[0].Score.Base);
            CollectionAssert.AreEqual(result.SceneScreenshotPng, loaded.Results[0].SceneScreenshotPng);
            Assert.AreEqual(BenchmarkDimension.Reasoning, loaded.Results[0].Checkpoints[0].Dimension);
        }

        [Test]
        public void AtomicWrite_TornTemporaryFileAndFailedReplacementPreserveCheckpoint()
        {
            BenchmarkRunStore store = new(_root, "run");
            BenchmarkTargetState state = new();
            state.Results.Add(Result(Plan()[0]));
            store.Save("target", state);
            string path = Path.Combine(store.DirectoryPath, "target.state.json");
            string original = File.ReadAllText(path);
            File.WriteAllText(path + ".tmp-interrupted", "{\"Results\":[");
            Assert.AreEqual(state.Results[0].ScenarioId, store.Load("target").Results[0].ScenarioId);

            Assert.Throws<ArgumentNullException>(() => BenchmarkRunStore.AtomicWrite(path, null));
            Assert.AreEqual(original, File.ReadAllText(path));
            CollectionAssert.AreEqual(new[] { path + ".tmp-interrupted" }, Directory.GetFiles(store.DirectoryPath, "*.tmp-*"));

            state.Results.Add(Result(Plan()[1]));
            store.Save("target", state);
            Assert.AreEqual(2, store.Load("target").Results.Count);
        }

        [TestCase("env:")]
        [TestCase("env: ")]
        public void Manifest_RoundTripStoresReferencesWithoutLiteralOrResolvedSecrets(string prefix)
        {
            const string variable = "COREAI_CHECKPOINT_SECRET_TEST";
            string previous = Environment.GetEnvironmentVariable(variable);
            try
            {
                Environment.SetEnvironmentVariable(variable, "resolved-sensitive-value");
                BenchmarkRunManifest manifest = Manifest("run");
                manifest.Targets.Add(BenchmarkStoredTarget.From(new BenchmarkTarget("referenced", "http://localhost/v1",
                    prefix + variable, "referenced"), "referenced"));
                manifest.Targets.Add(BenchmarkStoredTarget.From(new BenchmarkTarget("literal", "http://localhost/v1",
                    "literal-sensitive-value", "literal"), "literal"));
                BenchmarkRunStore store = new(_root, "run");
                store.SaveManifest(manifest);
                string json = File.ReadAllText(Path.Combine(store.DirectoryPath, "manifest.json"));
                StringAssert.Contains(prefix + variable, json);
                StringAssert.DoesNotContain("resolved-sensitive-value", json);
                StringAssert.DoesNotContain("literal-sensitive-value", json);
                BenchmarkRunManifest loaded = store.LoadManifest();
                loaded.Validate("suite-v1", Plan());
                Assert.AreEqual("resolved-sensitive-value", loaded.Targets[1].Resolve().ResolveApiKey());
                Assert.Throws<InvalidOperationException>(() => loaded.Targets[2].Resolve());
                Assert.AreEqual("replacement-secret", loaded.Targets[2].Resolve(new BenchmarkTarget("literal",
                    "http://localhost/v1", "replacement-secret", "literal")).ResolveApiKey());
            }
            finally
            {
                Environment.SetEnvironmentVariable(variable, previous);
            }
        }

        [TestCase("on", "1")]
        [TestCase(" true ", "1")]
        [TestCase("enabled", "1")]
        [TestCase("no", "0")]
        [TestCase(" FALSE ", "0")]
        [TestCase("disabled", "0")]
        public void Manifest_ProviderFlagAliasesMatchResolvedSavedValues(string original, string saved)
        {
            Assert.IsTrue(BenchmarkRunManifest.OptionMatches("COREAI_TEST_STREAMING", original, saved));
            Assert.IsTrue(BenchmarkRunManifest.OptionMatches("COREAI_TEST_NATIVE_TOOLS", original, saved));
            Assert.IsFalse(BenchmarkRunManifest.OptionMatches("COREAI_TEST_STREAMING", original, saved == "1" ? "0" : "1"));
        }

        [TestCase("{\"api_key\":\"secret\"}")]
        [TestCase("{\"nested\":{\"authorization\":\"secret\"}}")]
        [TestCase("{\"access-token\":\"secret\"}")]
        [TestCase("{\"refresh_token\":\"secret\"}")]
        [TestCase("{\"bearer_token\":\"secret\"}")]
        [TestCase("{\"id_token\":\"secret\"}")]
        [TestCase("{\"client_secret\":\"secret\"}")]
        [TestCase("{\"password\":\"secret\"}")]
        [TestCase("{\"nested\":{\"AUTH_TOKEN\":\"secret\"}}")]
        [TestCase("{\"nested\":{\"api_token\":\"secret\"}}")]
        [TestCase("{\"session_token\":\"secret\"}")]
        [TestCase("{\"private_key\":\"secret\"}")]
        [TestCase("{\"Credentials\":{\"user\":\"secret\"}}")]
        [TestCase("{\"cookie\":\"secret\"}")]
        [TestCase("{\"headers\":{\"X-Custom\":\"bEaReR sensitive\"}}")]
        [TestCase("{\"headers\":{\"X-Custom\":[\"Basic sensitive\"]}}")]
        [TestCase("{\"nested\":{\"Authorization\":\"sensitive\"}}")]
        public void Manifest_RejectsSecretsInProviderBody(string body)
        {
            Assert.Throws<ArgumentException>(() => BenchmarkStoredTarget.From(
                new BenchmarkTarget("model", "http://localhost/v1", "", "target", "", body), "target"));
        }

        [TestCase("http://user:password@localhost/v1")]
        [TestCase("http://localhost/v1?api_key=secret")]
        public void Manifest_RejectsCredentialsInEndpoint(string endpoint)
        {
            Assert.Throws<ArgumentException>(() => BenchmarkStoredTarget.From(
                new BenchmarkTarget("model", endpoint, "", "target"), "target"));
        }

        [Test]
        public void Compatibility_RejectsChangedSuiteScenarioGroupTierOrRepetition()
        {
            BenchmarkRunManifest manifest = Manifest("run");
            Assert.Throws<InvalidOperationException>(() => manifest.Validate("suite-v2", Plan()));
            foreach (Action<BenchmarkPlannedScenario> mutate in new Action<BenchmarkPlannedScenario>[]
            {
                scenario => scenario.Id = "different", scenario => scenario.Group = "G8",
                scenario => scenario.Extreme = true, scenario => scenario.Repetition = 7
            })
            {
                List<BenchmarkPlannedScenario> changed = Plan();
                mutate(changed[0]);
                Assert.Throws<InvalidOperationException>(() => manifest.Validate("suite-v1", changed));
            }
        }

        [Test]
        public void Compatibility_RejectsEveryChangedTargetInput()
        {
            BenchmarkStoredTarget original = Manifest("run").Targets[0];
            foreach (Action<BenchmarkStoredTarget> mutate in new Action<BenchmarkStoredTarget>[]
            {
                target => target.Slug = "other", target => target.Model = "other", target => target.BaseUrl = "http://other/v1",
                target => target.Label = "other", target => target.ReasoningEffort = "high", target => target.Temperature = 0.5f,
                target => target.ExtraBodyJson = "{\"seed\":2}", target => target.KeyReference = "env:OTHER",
                target => target.RequiresExternalKey = true
            })
            {
                BenchmarkStoredTarget changed = JsonConvert.DeserializeObject<BenchmarkStoredTarget>(JsonConvert.SerializeObject(original));
                mutate(changed);
                Assert.Throws<InvalidOperationException>(() => original.Validate(changed));
            }
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(3)]
        [TestCase(5)]
        public void InterruptedRunner_ResumeMatchesUninterruptedResultsAndBothTierScores(int finished)
        {
            List<BenchmarkPlannedScenario> plan = Plan();
            BenchmarkTargetState uninterrupted = new();
            ExecutePending(uninterrupted, plan, int.MaxValue, null);
            BenchmarkRunStore store = new(_root, "run");
            BenchmarkTargetState interrupted = new();
            store.Save("target", interrupted);
            ExecutePending(interrupted, plan, finished, state => store.Save("target", state));
            BenchmarkTargetState resumed = store.Load("target");
            int executed = ExecutePending(resumed, plan, int.MaxValue, state => store.Save("target", state));

            Assert.AreEqual(plan.Count - finished, executed);
            Assert.AreEqual(JsonConvert.SerializeObject(uninterrupted.Results), JsonConvert.SerializeObject(resumed.Results));
            Assert.AreEqual(uninterrupted.ToReport().SuiteBaseScore, resumed.ToReport().SuiteBaseScore);
            Assert.AreEqual(uninterrupted.ToReport().RegularBaseScore, resumed.ToReport().RegularBaseScore);
            Assert.AreEqual(uninterrupted.ToReport().ExtremeBaseScore, resumed.ToReport().ExtremeBaseScore);
            CollectionAssert.AreEqual(plan.Select(s => s.Id + "/" + s.Repetition), resumed.Results.Select(s => s.ScenarioId + "/" + s.Repetition));
        }

        [Test]
        public void Resume_ReexecutesEnvironmentAndInFlightScenariosOnly()
        {
            List<BenchmarkPlannedScenario> plan = Plan();
            BenchmarkTargetState state = new();
            ExecutePending(state, plan, 2, null);
            state.Results[1].Attribution = FailureAttribution.Environment;
            state.Results[1].Failure = "Provider unavailable";
            BenchmarkRunStore store = new(_root, "run");
            store.Save("target", state);
            ScenarioResult computedButNotSaved = Result(plan[2]);
            Assert.IsNotNull(computedButNotSaved);
            BenchmarkTargetState resumed = store.Load("target");
            Assert.IsTrue(resumed.IsFinalized(plan[0].Id, plan[0].Repetition));
            Assert.IsFalse(resumed.IsFinalized(plan[1].Id, plan[1].Repetition));
            Assert.IsFalse(resumed.IsFinalized(plan[2].Id, plan[2].Repetition));
            Assert.AreEqual(plan.Count - 1, ExecutePending(resumed, plan, int.MaxValue, null));
            Assert.IsTrue(resumed.Results.All(r => r.Attribution != FailureAttribution.Environment));
            Assert.AreEqual(plan.Count, resumed.Results.Count);
        }

        [Test]
        public void LegacyReport_RepairsEnvironmentPreservingAllOtherResultJson()
        {
            List<BenchmarkPlannedScenario> plan = Plan();
            BenchmarkTargetState original = new();
            ExecutePending(original, plan, plan.Count - 1, null);
            original.Results[1].Attribution = FailureAttribution.Environment;
            original.Results[1].Failure = "Provider unavailable";
            original.Results[0].SessionTranscript = "Saved transcript";
            JObject fixture = BenchmarkJson.ParseObject(BenchmarkReportFormatter.ToJson(original.ToReport()));
            ((JObject)fixture["results"][0])["futureDiagnostic"] = new JObject { ["unknown"] = "must survive" };
            BenchmarkTargetState imported = BenchmarkRunStore.ImportReport(fixture.ToString(Formatting.None));
            string[] unchanged = imported.Results.Where(r => r.Attribution != FailureAttribution.Environment)
                .Select(r => r.ImportedJson).ToArray();
            Assert.AreEqual(2, ExecutePending(imported, plan, int.MaxValue, null));
            string json = BenchmarkReportFormatter.ToJson(imported.ToReport());
            foreach (string row in unchanged)
            {
                StringAssert.Contains(row, json);
            }
            ScenarioResult kept = imported.Results[0];
            Assert.AreEqual(original.Results[0].PromptTokens, kept.PromptTokens);
            Assert.AreEqual(original.Results[0].CompletionTokens, kept.CompletionTokens);
            Assert.AreEqual(original.Results[0].LatencyMs, kept.LatencyMs);
            Assert.AreEqual(original.Results[0].Score.Base, kept.Score.Base);
            Assert.AreEqual(original.Results[0].Checkpoints[0].Dimension, kept.Checkpoints[0].Dimension);
            Assert.AreEqual(plan.Count, imported.Results.Count);
            Assert.IsEmpty(imported.Results[1].ImportedJson);
        }

        [Test]
        public void IncompleteRuns_NewestFirstSkipsCompletedAndIgnoresUninitializedFolder()
        {
            foreach (string id in new[] { "20261015_224850", "20261016_104850", "20261016_114850" })
            {
                BenchmarkRunStore store = new(_root, id);
                store.SaveManifest(Manifest(id));
                if (id == "20261016_114850") store.Save("target", new BenchmarkTargetState { ReportsWritten = true });
            }
            Directory.CreateDirectory(Path.Combine(_root, ".runs", "20261016_124850"));
            CollectionAssert.AreEqual(new[] { "20261016_104850", "20261015_224850" }, BenchmarkRunStore.ListIncompleteRuns(_root));
        }

        [Test]
        public void Segments_ReportRetainsResumeCountAndWallTime()
        {
            BenchmarkTargetState state = new();
            state.Segments.Add(new BenchmarkRunSegment { WallSeconds = 12.5, Finished = false });
            state.Segments.Add(new BenchmarkRunSegment { WallSeconds = 7.5, Finished = true });
            BenchmarkReport report = state.ToReport();
            Assert.IsTrue(report.Metadata.Resumed);
            Assert.AreEqual(2, report.Metadata.Segments.Count);
            Assert.AreEqual(20, report.Metadata.TotalWallSeconds);
        }

        private static int ExecutePending(BenchmarkTargetState state, IReadOnlyList<BenchmarkPlannedScenario> plan,
            int maximum, Action<BenchmarkTargetState> save)
        {
            int executed = 0;
            foreach (BenchmarkPlannedScenario scenario in plan)
            {
                if (executed == maximum) break;
                if (state.IsFinalized(scenario.Id, scenario.Repetition)) continue;
                state.Merge(Result(scenario), plan);
                save?.Invoke(state);
                executed++;
            }
            return executed;
        }

        private static List<BenchmarkPlannedScenario> Plan()
        {
            List<BenchmarkPlannedScenario> plan = new();
            for (int repetition = 1; repetition <= 2; repetition++)
            {
                plan.Add(new BenchmarkPlannedScenario { Id = "regular", Group = "G1", Repetition = repetition });
                plan.Add(new BenchmarkPlannedScenario { Id = "extreme", Group = "G1", Repetition = repetition, Extreme = true });
                plan.Add(new BenchmarkPlannedScenario { Id = "visual", Group = "G6", Repetition = repetition });
            }
            return plan;
        }

        private static ScenarioResult Result(BenchmarkPlannedScenario scenario)
        {
            double score = scenario.Extreme ? 60 + scenario.Repetition : 90 + scenario.Repetition;
            return new ScenarioResult
            {
                ScenarioId = scenario.Id, ScenarioName = scenario.Id + " name", Group = scenario.Group,
                Repetition = scenario.Repetition, Extreme = scenario.Extreme, ModelId = "model",
                Score = new GoalScore(score, 0, null, double.NaN, 0, score, 0, 0, 0, 0, 0, score, score,
                    scenario.Extreme ? BenchmarkClassification.Partial : BenchmarkClassification.Pass),
                Turns = 2, ToolCalls = 3, FailedToolCalls = 1, InvalidCommands = 1,
                PromptTokens = 123, CompletionTokens = 456, ProviderPromptTokens = 125, ProviderCompletionTokens = 458,
                TokensFromProvider = true, LatencyMs = 1250, GenerationMs = 1000, CostUsd = 0.012, CostKnown = true,
                WhatItChecks = "Saved grading evidence", Checkpoints = new[]
                {
                    new BenchmarkCheckpoint("gate", "Saved checkpoint", 2, true, false, "detail", BenchmarkDimension.Reasoning)
                }
            };
        }

        private static BenchmarkRunManifest Manifest(string runId)
        {
            return new BenchmarkRunManifest
            {
                RunId = runId, SuiteVersion = "suite-v1", StartedUtc = "2026-10-15T22:48:50Z",
                Groups = "G1,G6", Repetitions = 2, VisionMode = "off", Scenarios = Plan(),
                Targets = new List<BenchmarkStoredTarget>
                {
                    BenchmarkStoredTarget.From(new BenchmarkTarget("model", "http://localhost/v1", "", "target", "", "{\"seed\":1}"), "target")
                }
            };
        }
    }
}
