#if COREAI_LUA && COREAI_LLM && !UNITY_WEBGL
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CoreAI.Benchmarking;
using UnityEngine;
using static CoreAI.Tests.PlayMode.Benchmarks.GameCreationBenchmarkHarness;

namespace CoreAI.Tests.PlayMode.Benchmarks
{
    /// <summary>Durable scenario checkpoints shared by the single-target and cooperative runners.</summary>
    public sealed partial class GameCreationBenchmarkPlayModeTests
    {
        private const string ResumeEnvironment = "COREAI_BENCHMARK_RESUME";
        private const string OnlyTargetsEnvironment = "COREAI_BENCHMARK_ONLY_TARGETS";
        private static readonly string[] DurableOptions =
        {
            EnvGroups, EnvRepetitions, EnvVisionMode, EnvTimeout, EnvRoundtrips,
            BenchmarkTargetList.EnvReasoningEffort, "COREAI_TEST_STREAMING", "COREAI_TEST_NATIVE_TOOLS",
            "COREAI_BENCHMARK_FREEBUILD_PROMPT", "COREAI_BENCHMARK_FREEBUILD_SUBJECT",
            "COREAI_BENCHMARK_MAX_OUTPUT_TOKENS", "COREAI_BENCHMARK_FREEBUILD_ROUNDTRIPS",
            EnvProviderStallSeconds
        };

        private static List<BenchmarkPlannedScenario> DurablePlan(IReadOnlyList<GameBenchmarkScenario> scenarios, int reps)
        {
            List<BenchmarkPlannedScenario> plan = new();
            foreach (GameBenchmarkScenario scenario in scenarios)
                for (int rep = 1; rep <= (scenario.RepsOverride ?? reps); rep++)
                    plan.Add(new BenchmarkPlannedScenario
                    {
                        Id = scenario.Id, Group = scenario.Group, Extreme = scenario.IsExtreme, Repetition = rep
                    });
            return plan;
        }

        private static string ResolveResumeId()
        {
            string requested = Environment.GetEnvironmentVariable(ResumeEnvironment)?.Trim() ?? "";
            if (!string.Equals(requested, "latest", StringComparison.OrdinalIgnoreCase)) return requested;
            string latest = BenchmarkRunStore.ListIncompleteRuns(DefaultResultsDirectory(), message => Debug.LogWarning(message)).FirstOrDefault();
            if (latest == null) throw new InvalidOperationException("No interrupted benchmark run has a durable manifest. Supply an explicit legacy run id and targets to repair finished reports.");
            return latest;
        }

        private static bool WantsTarget(string label, string model)
        {
            string filter = Environment.GetEnvironmentVariable(OnlyTargetsEnvironment);
            return string.IsNullOrWhiteSpace(filter) || filter.Split(',').Any(name =>
                string.Equals(name.Trim(), label.Length > 0 ? label : model, StringComparison.Ordinal));
        }

        private static void ValidateTargetFilter(IReadOnlyList<BenchmarkTarget> targets)
        {
            string filter = Environment.GetEnvironmentVariable(OnlyTargetsEnvironment);
            if (string.IsNullOrWhiteSpace(filter)) return;
            foreach (string label in filter.Split(',').Select(s => s.Trim()))
                if (!targets.Any(t => t.DisplayName == label))
                    throw new InvalidOperationException($"No benchmark target matches OnlyTargets label '{label}'.");
        }

        private static List<BenchmarkTarget> ResolveResumeTargets(string runId, List<BenchmarkTarget> supplied)
        {
            BenchmarkRunStore store = new(DefaultResultsDirectory(), runId);
            if (!store.HasManifest)
            {
                if (supplied.Count == 0)
                    throw new InvalidOperationException("Legacy report resume requires the original targets file/arguments.");
                string[] reports = Directory.GetFiles(DefaultResultsDirectory(), $"BENCHMARK_{runId}_*.json");
                if (reports.Length == 0) throw new InvalidOperationException($"No checkpoint or reports found for run '{runId}'.");
                foreach (string path in reports)
                    if (!supplied.Any(t => Path.GetFileNameWithoutExtension(path) == ArtifactStem(runId, t.Model, t.Label)))
                        throw new InvalidOperationException($"Cannot resume: report '{Path.GetFileName(path)}' has no matching target. Supply the original complete target list, then filter with OnlyTargets.");
                List<BenchmarkTargetState> imported = reports.Select(p => BenchmarkRunStore.ImportReport(File.ReadAllText(p))).ToList();
                foreach (BenchmarkTargetState state in imported)
                    if (state.Metadata.RunId != runId || state.Metadata.SuiteVersion != SuiteVersion ||
                        state.Metadata.Repetitions != imported[0].Metadata.Repetitions)
                        throw new InvalidOperationException("Cannot resume: legacy report run id, suite version or repetitions mismatch.");
                RestoreLegacyStallPolicy(imported);
                if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvGroups)))
                    SetSavedOption(EnvGroups, string.Join(",", imported.SelectMany(s => s.Results).Select(r => r.Group).Distinct()));
                SetSavedOption(EnvRepetitions, imported[0].Metadata.Repetitions.ToString());
                bool vision = imported.Any(s => s.Results.Any(r => r.ScenarioId == "g6_free_build_vision"));
                bool text = imported.Any(s => s.Results.Any(r => r.ScenarioId == "g6_free_build"));
                if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvVisionMode)))
                    SetSavedOption(EnvVisionMode, vision ? (text ? "both" : "image") : "off");
                ValidateTargetFilter(supplied);
                return supplied;
            }

            BenchmarkRunManifest manifest = store.LoadManifest();
            if (manifest.RunId != runId) throw new InvalidOperationException("Cannot resume: manifest run id mismatch.");
            if (supplied.Count == 0 && manifest.TargetsFileReference.Length > 0 &&
                !BenchmarkTargetList.TryParse(manifest.TargetsFileReference, out supplied, out string sourceError))
                throw new InvalidOperationException("Cannot reopen the original targets file: " + sourceError);
            foreach (KeyValuePair<string, string> option in manifest.Options)
                if (option.Key != EnvRetries) SetSavedOption(option.Key, option.Value);
            manifest.Validate(SuiteVersion, DurablePlan(AllScenarios(), manifest.Repetitions));
            if (supplied.Count > 0 && (supplied.Count != manifest.Targets.Count || supplied.Any(t =>
                !manifest.Targets.Any(saved => saved.Slug == ArtifactStem(runId, t.Model, t.Label)))))
                throw new InvalidOperationException("Cannot resume: the supplied target list differs from the original resolved target list.");
            List<BenchmarkTarget> targets = new();
            foreach (BenchmarkStoredTarget saved in manifest.Targets)
            {
                BenchmarkTarget external = supplied.FirstOrDefault(t => ArtifactStem(runId, t.Model, t.Label) == saved.Slug);
                bool needed = WantsTarget(saved.Label, saved.Model) && TargetNeedsWork(store, manifest, saved);
                if (needed && saved.ConfigurationDeferred && external == null)
                    throw new InvalidOperationException($"Target '{saved.LabelOrModel}' was not selected during legacy import. Supply its original target configuration to reopen it.");
                if (needed && (external == null || string.IsNullOrWhiteSpace(external.ResolveApiKey())) &&
                    saved.RequiresExternalKey && (manifest.UseDefaultSecretSource || saved.UsesDefaultSecretSource))
                {
                    PlayModeOpenAiTestConfig.ResolvedConfig originalSource = PlayModeOpenAiTestConfig.Resolve(saved.Model);
                    if (originalSource.BaseUrl != saved.BaseUrl || string.IsNullOrEmpty(originalSource.ApiKey))
                        throw new InvalidOperationException("Cannot resume: restore the original local configuration secret source or supply the target explicitly.");
                    external = new BenchmarkTarget(saved.Model, saved.BaseUrl, originalSource.ApiKey, saved.Label,
                        saved.ReasoningEffort, saved.ExtraBodyJson, saved.Temperature);
                }
                BenchmarkTarget resolved = needed ? saved.Resolve(external) : new BenchmarkTarget(saved.Model,
                    saved.BaseUrl, saved.KeyReference, saved.Label, saved.ReasoningEffort, saved.ExtraBodyJson, saved.Temperature);
                targets.Add(external ?? resolved);
            }
            ValidateTargetFilter(targets);
            return targets;
        }

        private static bool TargetNeedsWork(BenchmarkRunStore store, BenchmarkRunManifest manifest, BenchmarkStoredTarget target)
            => store.NeedsWork(manifest, target);

        private static void SetSavedOption(string key, string value)
        {
            string current = Environment.GetEnvironmentVariable(key) ?? "";
            if (current.Length > 0 && !BenchmarkRunManifest.OptionMatches(key, current, value))
                throw new InvalidOperationException($"Cannot resume: option {key} differs from the saved run. Clear the override or supply the original value.");
            Environment.SetEnvironmentVariable(key, string.IsNullOrEmpty(value) ? null : value);
        }

        private static void RestoreLegacyStallPolicy(IReadOnlyList<BenchmarkTargetState> imported)
        {
            double? saved = imported[0].Metadata.ProviderStallSeconds;
            if (imported.Any(state => state.Metadata.ProviderStallSeconds != saved))
                throw new InvalidOperationException("Cannot resume: reports disagree on the saved provider inactivity ceiling.");
            if (!saved.HasValue) return;
            if (double.IsNaN(saved.Value) || double.IsInfinity(saved.Value) || saved.Value < 0 || saved.Value > 3600)
                throw new InvalidOperationException("Cannot resume: the saved provider inactivity ceiling is invalid.");
            SetSavedOption(EnvProviderStallSeconds, saved.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        }

        private static BenchmarkRunManifest CreateManifest(string runId, IReadOnlyList<BenchmarkTarget> targets,
            IReadOnlyList<GameBenchmarkScenario> scenarios, int reps,
            IReadOnlyDictionary<string, BenchmarkStoredTarget> frozenTargets = null)
        {
            BenchmarkRunManifest manifest = new()
            {
                RunId = runId, SuiteVersion = SuiteVersion, StartedUtc = DateTime.UtcNow.ToString("o"),
                Groups = Environment.GetEnvironmentVariable(EnvGroups) ?? "", Repetitions = reps,
                VisionMode = Environment.GetEnvironmentVariable(EnvVisionMode) ?? "",
                ReasoningEffort = Environment.GetEnvironmentVariable(BenchmarkTargetList.EnvReasoningEffort) ?? "",
                Scenarios = DurablePlan(scenarios, reps)
            };
            string targetSource = Environment.GetEnvironmentVariable(BenchmarkTargetList.EnvTargets) ?? "";
            if (targetSource.TrimStart().StartsWith("@", StringComparison.Ordinal)) manifest.TargetsFileReference = targetSource;
            foreach (string option in DurableOptions)
                manifest.Options[option] = option == EnvProviderStallSeconds
                    ? ResolveProviderStallSeconds().ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                    : Environment.GetEnvironmentVariable(option) ?? "";
            foreach (BenchmarkTarget target in targets)
            {
                string slug = ArtifactStem(runId, target.Model, target.Label);
                if (frozenTargets != null && frozenTargets.TryGetValue(slug, out BenchmarkStoredTarget frozen))
                    manifest.Targets.Add(frozen);
                else manifest.Targets.Add(WantsTarget(target.Label, target.Model)
                    ? BenchmarkStoredTarget.From(target, slug) : BenchmarkStoredTarget.Deferred(target, slug));
            }
            if (manifest.Targets.Select(t => t.Slug).Distinct(StringComparer.OrdinalIgnoreCase).Count() != targets.Count)
                throw new InvalidOperationException("Benchmark target artifact slugs collide; use distinct labels.");
            return manifest;
        }

        private static void AttachCheckpoint(TargetSuiteRun run, BenchmarkRunStore store, BenchmarkRunManifest manifest,
            string slug, bool resume)
        {
            BenchmarkTargetState state = resume ? store.Load(slug) : null;
            string reportPath = Path.Combine(DefaultResultsDirectory(), slug + ".json");
            bool legacy = resume && state == null && File.Exists(reportPath);
            if (legacy)
            {
                state = BenchmarkRunStore.ImportReport(File.ReadAllText(reportPath));
                RestoreLegacyArtifacts(state, slug);
            }
            state ??= new BenchmarkTargetState { Metadata = run.Report.Metadata };
            state.ValidateIdentity(manifest.RunId, manifest.SuiteVersion, run.Report.Metadata.ModelId);
            state.Validate(manifest.Scenarios);
            if (resume)
                foreach (ScenarioResult restored in state.Results.Where(r => state.IsFinalized(r.ScenarioId, r.Repetition)))
                    run.Progress.CompleteScenario(ProgressLine(restored), Stars(BenchmarkInfo.DifficultyFor(restored.Group)));
            bool hasMissing = manifest.Scenarios.Any(p => !state.IsFinalized(p.Id, p.Repetition));
            run.SkipCompleted = resume && state.ReportsWritten && !hasMissing && File.Exists(reportPath);
            if (run.SkipCompleted)
            {
                store.MarkCompleted(slug);
                run.Report = state.ToReport();
                run.ArtifactPath = Path.ChangeExtension(reportPath, ".md");
                return;
            }
            if (resume && File.Exists(reportPath)) BackupReportFiles(DefaultResultsDirectory(), slug);
            if (resume) state.SeedLegacySegment();
            state.Segments.Add(new BenchmarkRunSegment { StartedUtc = DateTime.UtcNow.ToString("o") });
            state.ReportsWritten = false;
            run.Checkpoint = state;
            run.CheckpointStore = store;
            run.CheckpointSlug = slug;
            run.Plan = manifest.Scenarios;
            run.SegmentStartedSeconds = run.SuiteClock.Elapsed.TotalSeconds;
            run.Report = state.ToReport();
            store.Save(slug, state);
        }

        private static void RestoreLegacyArtifacts(BenchmarkTargetState state, string slug)
        {
            string dir = DefaultResultsDirectory();
            string trace = Path.Combine(dir, slug + ".tools.jsonl");
            if (File.Exists(trace)) state.PreservedToolTraceJsonl = File.ReadAllText(trace);
            string markdown = Path.Combine(dir, slug + ".md");
            BenchmarkLegacyReportEvidence.Restore(state, File.Exists(markdown) ? File.ReadAllText(markdown) : "", state.PreservedToolTraceJsonl);
            state.RefreshSummaryBaseline();
            BenchmarkArtifactRecovery.RestoreVisualArtifacts(state, dir, slug);
        }

        private static void BackupReportFiles(string root, string slug)
        {
            string stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffff");
            BenchmarkRunStore.BackupReportFiles(root, slug, stamp);
        }

        private static void SaveCheckpoint(TargetSuiteRun run, bool reportsWritten = false, bool segmentFinished = false)
        {
            if (run.Checkpoint == null) return;
            BenchmarkRunSegment segment = run.Checkpoint.Segments[run.Checkpoint.Segments.Count - 1];
            if (!segment.Finished) segment.WallSeconds = run.SuiteClock.Elapsed.TotalSeconds - run.SegmentStartedSeconds;
            segment.Finished |= segmentFinished;
            run.Checkpoint.ReportsWritten = reportsWritten;
            run.CheckpointStore.Save(run.CheckpointSlug, run.Checkpoint);
        }
    }
}
#endif
