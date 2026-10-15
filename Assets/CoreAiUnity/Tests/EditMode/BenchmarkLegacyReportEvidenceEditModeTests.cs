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
    /// <summary>Legacy JSON, Markdown and trace companions preserve the evidence of every retained result.</summary>
    public sealed class BenchmarkLegacyReportEvidenceEditModeTests
    {
        [Test]
        public void RealOldLayout_RepairPreservesKeptEvidenceAndReplacesEnvironmentRows()
        {
            string fixture = FixtureRoot();
            string originalJson = File.ReadAllText(Path.Combine(fixture, "legacy.json"));
            string originalMd = File.ReadAllText(Path.Combine(fixture, "legacy.md"));
            string trace = File.ReadAllText(Path.Combine(fixture, "legacy.tools.jsonl"));
            BenchmarkTargetState state = BenchmarkRunStore.ImportReport(originalJson, originalMd, trace);
            ScenarioResult kept = state.Results[0];
            ScenarioResult estimated = state.Results[1];
            Assert.AreEqual(1, kept.FailedToolCalls);
            Assert.AreEqual(0, kept.InvalidCommands);
            Assert.IsTrue(kept.TokensFromProvider);
            Assert.IsFalse(estimated.TokensFromProvider);
            Assert.AreEqual(1.5, kept.Score.EfficiencyBonus);
            Assert.AreEqual(2.5, kept.Score.CorrectnessBonus);
            Assert.AreEqual(95, kept.Score.Base);
            Assert.AreEqual(4, kept.Score.Bonus);
            Assert.AreEqual(99, kept.Score.Total);
            Assert.AreEqual("Checks the installed damage rule and hidden calls.", kept.WhatItChecks);
            Assert.AreEqual("1 failed tool call(s)", kept.Penalties.Single().Reason);
            Assert.AreEqual(2, kept.Penalties.Single().Points);
            Assert.Greater(state.Segments.Single().WallSeconds, 300);
            string failure = kept.ImportedFailureMarkdown;
            string session = kept.ImportedSessionMarkdown;
            string scene = kept.ImportedSceneMarkdown;
            Assert.IsNotEmpty(failure);
            Assert.IsNotEmpty(session);
            Assert.IsNotEmpty(scene);
            StringAssert.Contains(failure, originalMd);
            StringAssert.Contains(session, originalMd);
            StringAssert.Contains(scene, originalMd);
            string saved = JsonConvert.SerializeObject(state);
            state = JsonConvert.DeserializeObject<BenchmarkTargetState>(saved);
            List<string> raw = state.Results.Take(2).Select(row => row.ImportedJson).ToList();
            BenchmarkReport keptBefore = new();
            keptBefore.Results.AddRange(state.Results.Take(2));
            double[] dimensions = keptBefore.DimensionBreakdown().Select(score => score.Score).ToArray();
            int executions = FakeRepair(state);
            Assert.AreEqual(1, executions);
            string repairedMd = BenchmarkReportFormatter.ToMarkdown(state.ToReport());
            string repairedJson = BenchmarkReportFormatter.ToJson(state.ToReport());
            JObject originalMetadata = BenchmarkJson.ParseObject(originalJson)["metadata"] as JObject;
            JObject repairedMetadata = BenchmarkJson.ParseObject(repairedJson)["metadata"] as JObject;
            foreach (JProperty property in originalMetadata.Properties())
                Assert.IsTrue(JToken.DeepEquals(property.Value, repairedMetadata[property.Name]), property.Name);
            Assert.AreEqual((string)originalMetadata["timestampUtc"], state.Segments[0].StartedUtc);
            BenchmarkTargetState reimported = BenchmarkRunStore.ImportReport(repairedJson, repairedMd);
            Assert.AreEqual((string)originalMetadata["timestampUtc"], reimported.Metadata.TimestampUtc);
            Assert.AreEqual(state.Segments[0].StartedUtc, reimported.Segments[0].StartedUtc);
            StringAssert.Contains(failure, repairedMd);
            StringAssert.Contains(session, repairedMd);
            StringAssert.DoesNotContain("_No session captured._", repairedMd);
            foreach (string row in raw) StringAssert.Contains(row, repairedJson);
            BenchmarkReport keptAfter = new();
            keptAfter.Results.AddRange(state.Results.Take(2));
            CollectionAssert.AreEqual(dimensions, keptAfter.DimensionBreakdown().Select(score => score.Score).ToArray());
            Assert.AreEqual(2, state.ToReport().TotalFailedToolCalls);
            Assert.AreEqual(2, state.ToReport().TotalInvalidCommands);
            Assert.IsTrue(state.Results[0].TokensFromProvider);
            Assert.IsFalse(state.Results[1].TokensFromProvider);
            Assert.AreEqual(1.5, state.Results[0].Score.EfficiencyBonus);
            Assert.AreEqual(0.5, state.Results[1].Score.EfficiencyBonus);
            Assert.IsEmpty(state.Results[2].ImportedJson);
            Assert.AreEqual(FailureAttribution.None, state.Results[2].Attribution);
            Assert.IsEmpty(state.PreservedToolTraceJsonl);
            string[] repairedTrace = ToolTrace(state).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            Assert.AreEqual(trace.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Length + executions, repairedTrace.Length);
            Assert.AreEqual(executions, repairedTrace.Count(line => (string)BenchmarkJson.ParseObject(line)["name"] == "fake_repair"));
        }

        [Test]
        public void TraceOnly_RestoresFailedAndInvalidCommandCounts()
        {
            JObject report = BenchmarkJson.ParseObject(File.ReadAllText(Path.Combine(FixtureRoot(), "legacy.json")));
            report["results"] = new JArray(((JObject)report["results"][0]).DeepClone());
            string id = (string)report["results"][0]["scenarioId"];
            string trace = new JObject
            {
                ["scenarioId"] = id, ["name"] = "world_command", ["status"] = "failed", ["arguments"] = "{broken"
            }.ToString(Formatting.None);
            BenchmarkTargetState state = BenchmarkRunStore.ImportReport(report.ToString(), null, trace);
            Assert.AreEqual(1, state.Results.Single().FailedToolCalls);
            Assert.AreEqual(1, state.Results.Single().InvalidCommands);
        }

        [Test]
        public void TraceSplit_RepeatedAndUnknownScenarioCallsAreRetainedExactlyOnce()
        {
            string fixture = FixtureRoot();
            BenchmarkTargetState state = BenchmarkRunStore.ImportReport(File.ReadAllText(Path.Combine(fixture, "legacy.json")));
            ScenarioResult repeated = JsonConvert.DeserializeObject<ScenarioResult>(JsonConvert.SerializeObject(state.Results[0]));
            repeated.Repetition = 2;
            state.Results.Add(repeated);
            string unknown = "{\"scenarioId\":\"unknown\",\"name\":\"unused\",\"status\":\"completed\"}\n";
            string trace = File.ReadAllText(Path.Combine(fixture, "legacy.tools.jsonl")) + unknown;
            BenchmarkLegacyReportEvidence.Restore(state, null, trace);
            Assert.IsEmpty(repeated.ToolTraceJsonl);
            Assert.IsEmpty(state.Results[0].ToolTraceJsonl);
            Assert.IsNotEmpty(state.PreservedToolTraceJsonl);
            Assert.AreEqual(trace.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Length,
                ToolTrace(state).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Length);
        }

        [Test]
        public void CanonicalJson_UsesCamelCaseForSegmentsAndPenaltyDetails()
        {
            BenchmarkTargetState state = BenchmarkRunStore.ImportReport(File.ReadAllText(Path.Combine(FixtureRoot(), "legacy.json")));
            state.Results[0].ImportedJson = "";
            state.Results[0].Penalties = new[] { new BenchmarkPenalty("Saved evidence", 2) };
            JObject output = BenchmarkJson.ParseObject(BenchmarkReportFormatter.ToJson(state.ToReport()));
            Assert.IsNotNull(output["metadata"]["segments"][0]["startedUtc"]);
            Assert.IsNull(output["metadata"]["segments"][0]["StartedUtc"]);
            Assert.AreEqual("Saved evidence", (string)output["results"][0]["penaltyDetails"][0]["reason"]);
            Assert.IsNull(output["results"][0]["penaltyDetails"][0]["Reason"]);
        }

        [TestCase("```lua\nunterminated")]
        [TestCase("```text\n```lua\nprint(1)\n```\n```")]
        [TestCase("```text\n" + "truncated block with no closing marker")]
        public void Sessions_ExpectedHeadersSurviveUnclosedNestedAndTruncatedFences(string transcript)
        {
            string fixture = FixtureRoot();
            string markdown = File.ReadAllText(Path.Combine(fixture, "legacy.md"));
            markdown = markdown.Replace("GOAL: The game has a runtime logic slot", transcript + "\nGOAL: The game has a runtime logic slot");
            BenchmarkTargetState state = BenchmarkRunStore.ImportReport(File.ReadAllText(Path.Combine(fixture, "legacy.json")), markdown);
            Assert.AreEqual(3, state.Results.Count(result => result.ImportedSessionMarkdown.Length > 0));
            StringAssert.Contains(transcript, state.Results[0].ImportedSessionMarkdown);
            StringAssert.Contains("Level-scaled damage", state.Results[1].ImportedSessionMarkdown);
            StringAssert.DoesNotContain("Level-scaled damage", state.Results[0].ImportedSessionMarkdown);
        }

        [Test]
        public void Sessions_MissingKeptRowRefusesRepairWithRowIdentity()
        {
            string fixture = FixtureRoot();
            string markdown = File.ReadAllText(Path.Combine(fixture, "legacy.md"));
            markdown = markdown.Replace("### G2 \u00b7 Level-scaled damage", "### removed kept section");
            InvalidDataException exception = Assert.Throws<InvalidDataException>(() => BenchmarkRunStore.ImportReport(
                File.ReadAllText(Path.Combine(fixture, "legacy.json")), markdown));
            StringAssert.Contains("g2_level_damage", exception.Message);
            StringAssert.Contains("run 1", exception.Message);
        }

        [TestCase("")]
        [TestCase("# Missing report body")]
        [TestCase("## Session heading was corrupted")]
        public void Sessions_ProvidedMarkdownWithoutSessionHeadingRefusesRepair(string markdown)
        {
            InvalidDataException exception = Assert.Throws<InvalidDataException>(() => BenchmarkRunStore.ImportReport(
                File.ReadAllText(Path.Combine(FixtureRoot(), "legacy.json")), markdown));
            StringAssert.Contains("g2_flat_damage", exception.Message);
            StringAssert.Contains("session", exception.Message);
        }

        [Test]
        public void FailureSection_DeletedKeptEvidenceRefusesRepair()
        {
            string fixture = FixtureRoot();
            string json = File.ReadAllText(Path.Combine(fixture, "legacy.json"));
            string markdown = File.ReadAllText(Path.Combine(fixture, "legacy.md"));
            BenchmarkTargetState before = BenchmarkRunStore.ImportReport(json, markdown);
            markdown = markdown.Replace(before.Results[0].ImportedFailureMarkdown, "");
            InvalidDataException exception = Assert.Throws<InvalidDataException>(() => BenchmarkRunStore.ImportReport(json, markdown));
            StringAssert.Contains("g2_flat_damage", exception.Message);
            StringAssert.Contains("failure section", exception.Message);
        }

        [Test]
        public void SceneSection_ExplicitScreenshotWithoutEvidenceRefusesRepair()
        {
            string fixture = FixtureRoot();
            string json = File.ReadAllText(Path.Combine(fixture, "legacy.json"));
            string markdown = File.ReadAllText(Path.Combine(fixture, "legacy.md"));
            BenchmarkTargetState state = BenchmarkRunStore.ImportReport(json, markdown);
            markdown = markdown.Replace(state.Results[0].ImportedSceneMarkdown, "");
            state.Results[0].SceneScreenshotPng = new byte[] { 1 };
            InvalidDataException exception = Assert.Throws<InvalidDataException>(() => BenchmarkLegacyReportEvidence.Restore(state, markdown, null));
            StringAssert.Contains("g2_flat_damage", exception.Message);
            StringAssert.Contains("scene section", exception.Message);
        }

        [Test]
        public void SceneSection_HeroScreenshotDoesNotRequirePerScenarioSection()
        {
            BenchmarkTargetState state = BenchmarkRunStore.ImportReport(File.ReadAllText(Path.Combine(FixtureRoot(), "legacy.json")));
            ScenarioResult hero = state.Results[1];
            state.Results.Clear();
            state.Results.Add(hero);
            hero.Group = "G6";
            hero.ScenarioId = "g6_free_build";
            hero.SceneScreenshotPng = new byte[] { 1 };
            hero.SessionTranscript = "Completed hero session.";
            string markdown = BenchmarkReportFormatter.ToMarkdown(state.ToReport());
            Assert.DoesNotThrow(() => BenchmarkLegacyReportEvidence.Restore(state, markdown, null));
            Assert.IsEmpty(hero.ImportedSceneMarkdown);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RepeatedRows_FailureAndSceneEvidenceBelongOnlyToEligibleRepetition(bool legacyHeadings)
        {
            string json = File.ReadAllText(Path.Combine(FixtureRoot(), "legacy.json"));
            BenchmarkTargetState state = BenchmarkRunStore.ImportReport(json);
            ScenarioResult first = state.Results[1];
            ScenarioResult second = JsonConvert.DeserializeObject<ScenarioResult>(JsonConvert.SerializeObject(first));
            second.Repetition = 2;
            second.Failure = "Second run failed.";
            second.SceneScreenshotPng = new byte[] { 1 };
            state.Results.Clear();
            state.Results.Add(first);
            state.Results.Add(second);
            string secondFailure = "\n" + (legacyHeadings ? "### " + second.ScenarioName : BenchmarkReportFormatter.FailureHeading(second)) +
                "\n- \u22123 penalty: second run only\n";
            string secondScene = "\n" + (legacyHeadings ? BenchmarkReportFormatter.SceneHeading(second).Replace(" (run 2)", "") :
                BenchmarkReportFormatter.SceneHeading(second)) + "\n_Second run scene._\n\n![scene](fixture_g2_level_damage.png)\n";
            string markdown = "## Failed checkpoints\n" + secondFailure + "\n---\n## Full model session\n\n" +
                BenchmarkReportFormatter.SessionHeading(first) + "\n\nfirst session\n\n" +
                BenchmarkReportFormatter.SessionHeading(second) + "\n\nsecond session\n\n---\n## Scene screenshots\n" + secondScene;
            BenchmarkLegacyReportEvidence.Restore(state, markdown, null);
            Assert.IsEmpty(first.ImportedFailureMarkdown);
            Assert.IsEmpty(first.ImportedSceneMarkdown);
            Assert.IsEmpty(first.Penalties);
            Assert.AreEqual(secondFailure, second.ImportedFailureMarkdown);
            Assert.AreEqual(secondScene, second.ImportedSceneMarkdown);
            Assert.AreEqual(3, second.Penalties.Single().Points);
        }

        [Test]
        public void RepeatedLegacyScene_WithoutRepetitionEvidenceRefusesAmbiguousRepair()
        {
            BenchmarkTargetState state = BenchmarkRunStore.ImportReport(File.ReadAllText(Path.Combine(FixtureRoot(), "legacy.json")));
            ScenarioResult first = state.Results[1];
            ScenarioResult second = JsonConvert.DeserializeObject<ScenarioResult>(JsonConvert.SerializeObject(first));
            second.Repetition = 2;
            state.Results.Clear();
            state.Results.Add(first);
            state.Results.Add(second);
            string markdown = "## Scene screenshots\n\n" + BenchmarkReportFormatter.SceneHeading(first) +
                "\n\n![scene](fixture_g2_level_damage.png)\n";
            InvalidDataException exception = Assert.Throws<InvalidDataException>(() => BenchmarkLegacyReportEvidence.Restore(state, markdown, null));
            StringAssert.Contains("g2_level_damage", exception.Message);
            StringAssert.Contains("ambiguous", exception.Message);
        }

        [Test]
        public void Markdown_ReimportIsIdempotentAndSectionsKeepTheirOriginalBytes()
        {
            string fixture = FixtureRoot();
            BenchmarkTargetState state = BenchmarkRunStore.ImportReport(File.ReadAllText(Path.Combine(fixture, "legacy.json")),
                File.ReadAllText(Path.Combine(fixture, "legacy.md")));
            string[] sessions = state.Results.Select(result => result.ImportedSessionMarkdown).ToArray();
            string[] failures = state.Results.Select(result => result.ImportedFailureMarkdown).ToArray();
            string first = BenchmarkReportFormatter.ToMarkdown(state.ToReport());
            BenchmarkTargetState next = BenchmarkRunStore.ImportReport(BenchmarkReportFormatter.ToJson(state.ToReport()), first);
            string second = BenchmarkReportFormatter.ToMarkdown(next.ToReport());
            Assert.AreEqual(first, second);
            CollectionAssert.AreEqual(sessions, next.Results.Select(result => result.ImportedSessionMarkdown));
            CollectionAssert.AreEqual(failures, next.Results.Select(result => result.ImportedFailureMarkdown));
        }

        [Test]
        public void LegacyTableFailedCountWinsAndTraceLinesRemainVerbatim()
        {
            string fixture = FixtureRoot();
            string trace = "{\"scenarioId\":\"g2_flat_damage\",\"name\":\"execute_lua\",\"status\":\"completed\",\"latencyMs\":7.1137000000000006}\n";
            BenchmarkTargetState state = BenchmarkRunStore.ImportReport(File.ReadAllText(Path.Combine(fixture, "legacy.json")),
                File.ReadAllText(Path.Combine(fixture, "legacy.md")), trace);
            Assert.AreEqual(1, state.Results[0].FailedToolCalls);
            Assert.AreEqual(trace, state.Results[0].ToolTraceJsonl);
            Assert.AreEqual(trace, ToolTrace(state));
        }

        [Test, Explicit("Read-only verification of all six owner reports; writes only to a new system temporary directory.")]
        public void RealReports_TemporaryFakeRepairPreservesEveryKeptMarkdownSectionAndCounter()
        {
            string root = Path.Combine(RepositoryRoot(), "TestResults", "CoreAI", "Benchmarks");
            string destination = Path.Combine(Path.GetTempPath(), "CoreAI-legacy-repair-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(destination);
            string[] reports = Directory.GetFiles(root, "BENCHMARK_20261015_224850_*.json");
            Assert.AreEqual(6, reports.Length);
            foreach (string source in reports.OrderBy(path => path, StringComparer.Ordinal))
            {
                string stem = Path.GetFileNameWithoutExtension(source);
                foreach (string extension in new[] { ".json", ".md", ".tools.jsonl" })
                    File.Copy(Path.Combine(root, stem + extension), Path.Combine(destination, stem + extension));
                string json = File.ReadAllText(Path.Combine(destination, stem + ".json"));
                string md = File.ReadAllText(Path.Combine(destination, stem + ".md"));
                string trace = File.ReadAllText(Path.Combine(destination, stem + ".tools.jsonl"));
                BenchmarkTargetState state = BenchmarkRunStore.ImportReport(json, md, trace);
                JObject original = BenchmarkJson.ParseObject(json);
                JObject before = BenchmarkJson.ParseObject(BenchmarkReportFormatter.ToJson(state.ToReport()));
                AssertRecordedMetadataEqual((JObject)original["metadata"], (JObject)before["metadata"]);
                foreach (JProperty property in ((JObject)original["summary"]).Properties())
                    Assert.IsTrue(JToken.DeepEquals(property.Value, before["summary"][property.Name]), stem + " summary " + property.Name);
                Assert.AreEqual((string)original["metadata"]["timestampUtc"], state.Segments[0].StartedUtc);
                List<ScenarioResult> kept = state.Results.Where(row => row.Attribution != FailureAttribution.Environment).ToList();
                string[] originalOrder = state.Results.Select(RowKey).ToArray();
                HashSet<string> changedGroups = new(state.Results.Where(row => row.Attribution == FailureAttribution.Environment).Select(row => row.Group));
                int failedKept = kept.Sum(row => row.FailedToolCalls);
                int invalidKept = kept.Sum(row => row.InvalidCommands);
                string[] traceKept = kept.SelectMany(row => row.ToolTraceJsonl.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)).ToArray();
                Assert.IsTrue(kept.All(row => row.ImportedSessionMarkdown.Length > 0));
                int reruns = FakeRepair(state);
                string outputMd = RenderWithScenes(state);
                string outputJson = BenchmarkReportFormatter.ToJson(state.ToReport());
                string outputTrace = ToolTrace(state);
                JObject repaired = BenchmarkJson.ParseObject(outputJson);
                AssertRecordedMetadataEqual((JObject)original["metadata"], (JObject)repaired["metadata"]);
                CollectionAssert.AreEqual(originalOrder, state.Results.Select(RowKey));
                IReadOnlyList<string> rawRows = BenchmarkJson.RawArrayObjects(json, "results");
                foreach (ScenarioResult row in kept)
                {
                    Assert.AreEqual(rawRows[Array.IndexOf(originalOrder, RowKey(row))], row.ImportedJson);
                    StringAssert.Contains(row.ImportedJson, outputJson);
                    StringAssert.Contains(row.ImportedSessionMarkdown, outputMd);
                    if (row.ImportedFailureMarkdown.Length > 0) StringAssert.Contains(row.ImportedFailureMarkdown, outputMd);
                    if (row.ImportedSceneMarkdown.Length > 0) StringAssert.Contains(row.ImportedSceneMarkdown, outputMd);
                }
                foreach (JProperty group in ((JObject)original["summary"]["groups"]).Properties())
                    if (!changedGroups.Contains(group.Name))
                        Assert.IsTrue(JToken.DeepEquals(group.Value, repaired["summary"]["groups"][group.Name]), stem + " unchanged group " + group.Name);
                foreach (string line in traceKept) StringAssert.Contains(line + "\n", outputTrace);
                Assert.AreEqual(failedKept + reruns, state.ToReport().TotalFailedToolCalls);
                Assert.AreEqual(invalidKept + 2 * reruns, state.ToReport().TotalInvalidCommands);
                BenchmarkTargetState reimported = BenchmarkRunStore.ImportReport(outputJson, outputMd, outputTrace);
                Assert.AreEqual(outputMd, RenderWithScenes(reimported), stem + " Markdown idempotence");
                Assert.AreEqual(outputJson, BenchmarkReportFormatter.ToJson(reimported.ToReport()), stem + " JSON idempotence");
                foreach (ScenarioResult row in kept)
                {
                    ScenarioResult restored = reimported.Results.Single(candidate => RowKey(candidate) == RowKey(row));
                    Assert.AreEqual(row.TokensFromProvider, restored.TokensFromProvider);
                    Assert.AreEqual(row.Score.EfficiencyBonus, restored.Score.EfficiencyBonus);
                    Assert.AreEqual(row.ImportedSessionMarkdown, restored.ImportedSessionMarkdown);
                    Assert.AreEqual(row.ImportedFailureMarkdown, restored.ImportedFailureMarkdown);
                    Assert.AreEqual(row.ImportedSceneMarkdown, restored.ImportedSceneMarkdown);
                }
                File.WriteAllText(Path.Combine(destination, stem + ".md"), outputMd);
                File.WriteAllText(Path.Combine(destination, stem + ".json"), outputJson);
                File.WriteAllText(Path.Combine(destination, stem + ".tools.jsonl"), outputTrace);
                TestContext.Progress.WriteLine($"{stem}: lines {md.Split('\n').Length - 1} -> {outputMd.Split('\n').Length - 1}; " +
                    $"failed {(int)original["summary"]["failedToolCalls"]} -> {state.ToReport().TotalFailedToolCalls}; " +
                    $"invalid {(int)original["summary"]["invalidCommands"]} -> {state.ToReport().TotalInvalidCommands}; " +
                    $"tokens {(int)original["summary"]["totalTokens"]} -> {state.ToReport().TotalTokens}; " +
                    $"tool calls {(int)original["summary"]["totalToolCalls"]} -> {state.ToReport().TotalToolCalls}; " +
                    $"reruns {reruns}; kept {kept.Count}; raw rows/sections/traces/order/metadata/groups/idempotence verified; output {destination}");
            }
        }

        private static string RowKey(ScenarioResult row) => row.ScenarioId + "/" + row.Repetition;

        private static void AssertRecordedMetadataEqual(JObject original, JObject repaired)
        {
            foreach (JProperty property in original.Properties())
                Assert.IsTrue(JToken.DeepEquals(property.Value, repaired[property.Name]), "Metadata changed: " + property.Name);
        }

        private static string RenderWithScenes(BenchmarkTargetState state)
        {
            string markdown = BenchmarkReportFormatter.ToMarkdown(state.ToReport());
            List<ScenarioResult> scenes = state.Results.Where(row => !string.IsNullOrEmpty(row.ImportedSceneMarkdown)).ToList();
            if (scenes.Count > 0)
                markdown += "\n\n---\n## Scene screenshots\n\n_Original per-scenario scene evidence._\n" + string.Concat(scenes.Select(row => row.ImportedSceneMarkdown));
            return markdown;
        }

        [Test]
        public void UnchangedSummary_PreservesOriginalRoundingForGroupMeansAndEverySavedCounter()
        {
            JObject document = BenchmarkJson.ParseObject(File.ReadAllText(Path.Combine(FixtureRoot(), "legacy.json")));
            JArray rows = (JArray)document["results"];
            rows.RemoveAt(2);
            rows[0]["group"] = "G7";
            rows[0]["base"] = 0;
            rows[0]["extreme"] = true;
            rows[1]["group"] = "G7";
            rows[1]["base"] = 47.826087;
            rows[1]["extreme"] = false;
            document["summary"] = BenchmarkJson.ParseObject("{\"groups\":{\"G7\":{\"meanBase\":23.913043,\"scenarios\":2,\"pass\":0,\"regularBase\":47.826087,\"regularScenarios\":1,\"extremeBase\":0,\"extremeScenarios\":1,\"extremePass\":0}},\"totalLatencyMs\":331732.50840000005}");
            BenchmarkTargetState state = BenchmarkRunStore.ImportReport(document.ToString(Formatting.None));
            JObject output = BenchmarkJson.ParseObject(BenchmarkReportFormatter.ToJson(state.ToReport()));
            Assert.AreEqual(23.913043, state.ToReport().GroupBreakdown().Single().MeanBase);
            foreach (JProperty property in ((JObject)document["summary"]).Properties())
                Assert.IsTrue(JToken.DeepEquals(property.Value, output["summary"][property.Name]), property.Name);
            string saved = BenchmarkReportFormatter.ToJson(state.ToReport());
            Assert.AreEqual(saved, BenchmarkReportFormatter.ToJson(BenchmarkRunStore.ImportReport(saved).ToReport()));
        }

        private static int FakeRepair(BenchmarkTargetState state)
        {
            int executed = 0;
            List<BenchmarkPlannedScenario> plan = state.Results.Select(row => new BenchmarkPlannedScenario
            {
                Id = row.ScenarioId, Group = row.Group, Extreme = row.Extreme, Repetition = row.Repetition
            }).ToList();
            foreach (ScenarioResult old in state.Results.Where(row => row.Attribution == FailureAttribution.Environment).ToArray())
            {
                ScenarioResult replacement = JsonConvert.DeserializeObject<ScenarioResult>(JsonConvert.SerializeObject(old));
                replacement.Attribution = FailureAttribution.None;
                replacement.Failure = "";
                replacement.ImportedJson = "";
                replacement.ImportedFailureMarkdown = "";
                replacement.ImportedSessionMarkdown = "";
                replacement.ImportedSceneMarkdown = "";
                replacement.SessionTranscript = "Fake completed scenario transcript.";
                replacement.PromptTokens = 100;
                replacement.CompletionTokens = 50;
                replacement.ToolCalls = 1;
                replacement.TokensFromProvider = true;
                replacement.FailedToolCalls = 1;
                replacement.InvalidCommands = 2;
                replacement.ToolTraceJsonl = new JObject
                {
                    ["scenarioId"] = replacement.ScenarioId, ["name"] = "fake_repair", ["status"] = "failed"
                }.ToString(Formatting.None) + "\n";
                state.Merge(replacement, plan);
                executed++;
            }
            return executed;
        }

        private static string ToolTrace(BenchmarkTargetState state) => state.PreservedToolTraceJsonl +
            string.Concat(state.Results.Select(row => row.ToolTraceJsonl));

        private static string FixtureRoot() => Path.Combine(RepositoryRoot(), "Assets", "CoreAiUnity", "Tests", "EditMode", "Fixtures", "BenchmarkLegacyReport");

        private static string RepositoryRoot()
        {
            DirectoryInfo directory = new(Environment.CurrentDirectory);
            while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "Assets"))) directory = directory.Parent;
            if (directory == null) throw new DirectoryNotFoundException("Cannot locate the repository Assets directory.");
            return directory.FullName;
        }
    }
}
