using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CoreAI.Benchmarking
{
    /// <summary>A reproducible scenario/repetition identity, in canonical report order.</summary>
    public sealed class BenchmarkPlannedScenario
    {
        public string Id { get; set; } = "";
        public string Group { get; set; } = "";
        public int Repetition { get; set; }
        public bool Extreme { get; set; }
    }

    /// <summary>Non-secret target configuration. Literal keys must be supplied again by the caller.</summary>
    public sealed class BenchmarkStoredTarget
    {
        public string Slug { get; set; } = "";
        public string Model { get; set; } = "";
        public string BaseUrl { get; set; } = "";
        public string Label { get; set; } = "";
        public string KeyReference { get; set; } = "";
        public bool RequiresExternalKey { get; set; }
        public bool UsesDefaultSecretSource { get; set; }
        public bool ConfigurationDeferred { get; set; }
        public string ReasoningEffort { get; set; } = "";
        public float? Temperature { get; set; }
        public string ExtraBodyJson { get; set; } = "";

        public static BenchmarkStoredTarget From(BenchmarkTarget target, string slug)
        {
            Uri endpoint = new Uri(target.BaseUrl);
            if (endpoint.UserInfo.Length > 0 || endpoint.Query.Length > 0)
            {
                throw new ArgumentException("Benchmark state cannot persist endpoint credentials or query parameters.");
            }
            if (!string.IsNullOrWhiteSpace(target.ExtraBodyJson))
            {
                ValidateBody(BenchmarkJson.ParseToken(target.ExtraBodyJson));
            }
            return new BenchmarkStoredTarget
            {
                Slug = slug, Model = target.Model, BaseUrl = target.BaseUrl, Label = target.Label,
                KeyReference = target.HasKeyReference ? target.ApiKey : "",
                RequiresExternalKey = !target.HasKeyReference && target.ApiKey.Length > 0,
                ReasoningEffort = target.ReasoningEffort, Temperature = target.Temperature,
                ExtraBodyJson = target.ExtraBodyJson
            };
        }

        private static void ValidateBody(JToken token)
        {
            foreach (JProperty property in ((JContainer)token).Descendants().OfType<JProperty>())
            {
                string name = property.Name.Trim().Replace("_", "").Replace("-", "").ToLowerInvariant();
                if (name.Contains("apikey") || name.Contains("authorization") || name.Contains("password") ||
                    name.Contains("secret") || name == "token" || name == "accesstoken" ||
                    name == "refreshtoken" || name == "bearertoken" || name == "idtoken" ||
                    name == "authtoken" || name == "apitoken" || name == "sessiontoken" ||
                    name == "privatekey" || name == "credentials" || name == "cookie")
                {
                    throw new ArgumentException("Secrets in benchmark extraBody are not allowed in durable run state.");
                }
            }
            foreach (JValue item in ((JContainer)token).Descendants().OfType<JValue>())
            {
                if (item.Type != JTokenType.String) continue;
                string value = ((string)item)?.TrimStart() ?? "";
                if (HasScheme(value, "Bearer") || HasScheme(value, "Basic"))
                    throw new ArgumentException("Authentication header values are not allowed in durable benchmark provider bodies.");
            }
        }

        private static bool HasScheme(string value, string scheme) => value.Length > scheme.Length &&
            value.StartsWith(scheme, StringComparison.OrdinalIgnoreCase) && char.IsWhiteSpace(value[scheme.Length]);

        public BenchmarkTarget Resolve(BenchmarkTarget supplied = null)
        {
            if (RequiresExternalKey && supplied == null)
            {
                throw new InvalidOperationException($"Target '{LabelOrModel}' needs its original secret source; supply the targets again.");
            }
            string key = KeyReference.Length > 0 ? KeyReference : supplied?.ApiKey ?? "";
            if (KeyReference.Length > 0 && string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable(KeyReference.Substring(BenchmarkTarget.ApiKeyEnvPrefix.Length).Trim())))
            {
                throw new InvalidOperationException($"Target '{LabelOrModel}' needs {KeyReference} in this process.");
            }
            return new BenchmarkTarget(Model, BaseUrl, key, Label, ReasoningEffort, ExtraBodyJson, Temperature);
        }

        public string LabelOrModel => Label.Length > 0 ? Label : Model;

        /// <summary>Records only safe identity for an unselected legacy target; its config is resolved when selected later.</summary>
        public static BenchmarkStoredTarget Deferred(BenchmarkTarget target, string slug)
        {
            UriBuilder endpoint = new(target.BaseUrl) { UserName = "", Password = "", Query = "", Fragment = "" };
            return new BenchmarkStoredTarget
            {
                Slug = slug, Model = target.Model, Label = target.Label, BaseUrl = endpoint.Uri.AbsoluteUri.TrimEnd('/'),
                KeyReference = target.HasKeyReference ? target.ApiKey : "",
                RequiresExternalKey = !target.HasKeyReference && target.ApiKey.Length > 0,
                ConfigurationDeferred = true
            };
        }

        public void Validate(BenchmarkStoredTarget other)
        {
            if (Slug != other.Slug || Model != other.Model || BaseUrl != other.BaseUrl || Label != other.Label ||
                ReasoningEffort != other.ReasoningEffort || Temperature != other.Temperature ||
                KeyReference != other.KeyReference || RequiresExternalKey != other.RequiresExternalKey ||
                !JToken.DeepEquals(ParseBody(ExtraBodyJson), ParseBody(other.ExtraBodyJson)))
            {
                throw new InvalidOperationException($"Cannot resume: target configuration mismatch for '{LabelOrModel}'.");
            }
        }

        public void ValidateLegacyMetadata(BenchmarkRunMetadata metadata, bool selected, bool streaming, bool nativeTools)
        {
            if (!selected) return;
            if (metadata.ModelId != LabelOrModel || metadata.ReasoningEffort != ReasoningEffort ||
                metadata.Temperature != Temperature || metadata.Streaming != streaming || metadata.NativeToolCalling != nativeTools)
                throw new InvalidOperationException($"Cannot resume: legacy report target configuration mismatch for '{LabelOrModel}'. Keep the original effort, temperature, streaming and native-tool settings.");
        }

        private static JToken ParseBody(string body) => string.IsNullOrWhiteSpace(body) ? new JObject() : BenchmarkJson.ParseToken(body);
    }

    /// <summary>Run-wide immutable inputs. Only the coordinator writes this file.</summary>
    public sealed class BenchmarkRunManifest
    {
        public int FormatVersion { get; set; } = 1;
        public string SuiteVersion { get; set; } = "";
        public string RunId { get; set; } = "";
        public string StartedUtc { get; set; } = "";
        public string Groups { get; set; } = "";
        public int Repetitions { get; set; } = 1;
        public string VisionMode { get; set; } = "";
        public string ReasoningEffort { get; set; } = "";
        public Dictionary<string, string> Options { get; set; } = new();
        public string LocalBackend { get; set; } = "";
        public string TargetsFileReference { get; set; } = "";
        public bool UseDefaultSecretSource { get; set; }
        public bool ParallelExecution { get; set; } = true;
        public List<BenchmarkStoredTarget> Targets { get; set; } = new();
        public List<BenchmarkPlannedScenario> Scenarios { get; set; } = new();

        /// <summary>Compares the provider flags using the same aliases as live configuration resolution.</summary>
        public static bool OptionMatches(string key, string current, string saved)
        {
            if (key == "COREAI_BENCHMARK_RETRIES") return true;
            if (key == "COREAI_TEST_STREAMING" || key == "COREAI_TEST_NATIVE_TOOLS")
                return ProviderFlag(current) == ProviderFlag(saved);
            return string.Equals(current ?? "", saved ?? "", StringComparison.Ordinal);
        }

        private static bool ProviderFlag(string value)
        {
            string normalized = (value ?? "").Trim().ToLowerInvariant();
            return normalized != "0" && normalized != "false" && normalized != "no" &&
                normalized != "off" && normalized != "disabled";
        }

        public void Validate(string suiteVersion, IReadOnlyList<BenchmarkPlannedScenario> plan)
        {
            if (FormatVersion != 1 || SuiteVersion != suiteVersion)
            {
                throw new InvalidOperationException($"Cannot resume: suite version mismatch (saved {SuiteVersion}, current {suiteVersion}).");
            }
            if (!JToken.DeepEquals(JArray.FromObject(Scenarios), JArray.FromObject(plan)))
            {
                throw new InvalidOperationException("Cannot resume: scenario ids, groups, tiers or repetitions differ from the saved run.");
            }
        }
    }

    /// <summary>One process segment, periodically checkpointed with scenario results.</summary>
    public sealed class BenchmarkRunSegment
    {
        public string StartedUtc { get; set; } = "";
        public double WallSeconds { get; set; }
        public bool Finished { get; set; }
    }

    /// <summary>Complete per-target checkpoint, including transcripts, tool traces, replay and PNG bytes.</summary>
    public sealed class BenchmarkTargetState
    {
        public BenchmarkRunMetadata Metadata { get; set; } = new();
        public List<ScenarioResult> Results { get; set; } = new();
        public bool ReportsWritten { get; set; }
        public bool PreserveResultOrder { get; set; }
        public List<BenchmarkRunSegment> Segments { get; set; } = new();
        public string PreservedToolTraceJsonl { get; set; } = "";
        public string OriginalSummaryJson { get; set; } = "";
        public string BaselineSummaryJson { get; set; } = "";
        public double LegacyWallSeconds { get; set; }

        /// <summary>Seeds the original segment from legacy wall time, or its measured scenario latency lower bound.</summary>
        public void SeedLegacySegment()
        {
            if (Segments.Count == 0)
                Segments.Add(new BenchmarkRunSegment
                {
                    StartedUtc = Metadata.TimestampUtc,
                    WallSeconds = LegacyWallSeconds > 0 ? LegacyWallSeconds : Results.Sum(r => r.LatencyMs) / 1000.0,
                    Finished = false
                });
        }

        public void ValidateIdentity(string runId, string suiteVersion, string modelId)
        {
            if (Metadata.RunId != runId || Metadata.SuiteVersion != suiteVersion || Metadata.ModelId != modelId)
                throw new InvalidOperationException("Cannot resume: checkpoint belongs to a different run, suite or target.");
        }

        public bool IsFinalized(string id, int repetition) => Results.Any(r =>
            r.ScenarioId == id && r.Repetition == repetition && r.Attribution != FailureAttribution.Environment);

        public void Merge(ScenarioResult result, IReadOnlyList<BenchmarkPlannedScenario> plan)
        {
            PlanIndex(plan, result);
            int existing = Results.FindIndex(r => r.ScenarioId == result.ScenarioId && r.Repetition == result.Repetition);
            if (PreserveResultOrder)
            {
                if (existing >= 0) Results[existing] = result;
                else Results.Add(result);
                ReportsWritten = false;
                return;
            }
            Results.RemoveAll(r => r.ScenarioId == result.ScenarioId && r.Repetition == result.Repetition);
            Results.Add(result);
            Results = Results.OrderBy(r => PlanIndex(plan, r)).ToList();
            ReportsWritten = false;
        }

        private static int PlanIndex(IReadOnlyList<BenchmarkPlannedScenario> plan, ScenarioResult result)
        {
            for (int i = 0; i < plan.Count; i++)
            {
                if (plan[i].Id == result.ScenarioId && plan[i].Repetition == result.Repetition) return i;
            }
            throw new InvalidOperationException($"Cannot resume: unknown scenario/repetition '{result.ScenarioId}/{result.Repetition}'.");
        }

        public void Validate(IReadOnlyList<BenchmarkPlannedScenario> plan)
        {
            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (ScenarioResult result in Results)
            {
                int index = PlanIndex(plan, result);
                if (!seen.Add(result.ScenarioId + "/" + result.Repetition) ||
                    plan[index].Group != result.Group || plan[index].Extreme != result.Extreme)
                {
                    throw new InvalidOperationException("Cannot resume: duplicate result or incompatible scenario group/tier.");
                }
            }
        }

        public BenchmarkReport ToReport()
        {
            Metadata.Segments = Segments;
            BenchmarkReport report = new() { Metadata = Metadata };
            report.PreservedToolTraceJsonl = PreservedToolTraceJsonl;
            report.OriginalSummaryJson = OriginalSummaryJson;
            report.BaselineSummaryJson = BaselineSummaryJson;
            report.Results.AddRange(Results);
            return report;
        }

        public void RefreshSummaryBaseline()
        {
            BenchmarkReport baseline = ToReport();
            baseline.OriginalSummaryJson = "";
            baseline.BaselineSummaryJson = "";
            BaselineSummaryJson = BenchmarkJson.ParseObject(BenchmarkReportFormatter.ToJson(baseline))["summary"].ToString(Formatting.None);
        }
    }

    /// <summary>Engine-free atomic checkpoint storage. A failed replace leaves the previous checkpoint intact.</summary>
    public sealed class BenchmarkRunStore
    {
        private static readonly JsonSerializerSettings JsonSettings = new()
        {
            TypeNameHandling = TypeNameHandling.None,
            DateParseHandling = DateParseHandling.None,
            Formatting = Formatting.None
        };
        public string DirectoryPath { get; }
        private readonly string _resultsRoot;

        public BenchmarkRunStore(string resultsRoot, string runId)
        {
            ValidateRunId(runId);
            _resultsRoot = resultsRoot;
            DirectoryPath = Path.Combine(resultsRoot, ".runs", runId);
        }

        public static void ValidateRunId(string runId)
        {
            if (string.IsNullOrWhiteSpace(runId) || runId == "." || runId == ".." ||
                runId.Any(c => !char.IsLetterOrDigit(c) && c != '_' && c != '-'))
                throw new ArgumentException("Invalid benchmark run id; use letters, digits, underscore and hyphen.");
        }

        private string StatePath(string slug)
        {
            if (string.IsNullOrEmpty(slug) || Path.GetFileName(slug) != slug || slug.Contains("..") ||
                slug.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException("Invalid benchmark target slug.");
            return Path.Combine(DirectoryPath, slug + ".state.json");
        }

        public bool HasManifest => File.Exists(Path.Combine(DirectoryPath, "manifest.json"));
        public void SaveManifest(BenchmarkRunManifest manifest) =>
            AtomicWrite(Path.Combine(DirectoryPath, "manifest.json"), JsonConvert.SerializeObject(manifest, JsonSettings));
        public BenchmarkRunManifest LoadManifest() =>
            JsonConvert.DeserializeObject<BenchmarkRunManifest>(File.ReadAllText(Path.Combine(DirectoryPath, "manifest.json")), JsonSettings)
            ?? throw new InvalidDataException("Empty benchmark manifest.");
        public void Save(string slug, BenchmarkTargetState state)
        {
            string marker = CompletionPath(slug);
            if (!state.ReportsWritten && File.Exists(marker)) File.Delete(marker);
            AtomicWrite(StatePath(slug), JsonConvert.SerializeObject(state, JsonSettings));
            if (state.ReportsWritten) MarkCompleted(slug);
        }
        public string CompletionPath(string slug) => StatePath(slug) + ".completed";
        public void MarkCompleted(string slug) => AtomicWrite(CompletionPath(slug), "completed\n");
        public BenchmarkTargetState Load(string slug) => File.Exists(StatePath(slug))
            ? JsonConvert.DeserializeObject<BenchmarkTargetState>(File.ReadAllText(StatePath(slug)), JsonSettings)
              ?? throw new InvalidDataException("Empty benchmark checkpoint.") : null;

        public bool NeedsWork(BenchmarkRunManifest manifest, BenchmarkStoredTarget target)
        {
            BenchmarkTargetState state = Load(target.Slug);
            state?.ValidateIdentity(manifest.RunId, manifest.SuiteVersion, target.LabelOrModel);
            state?.Validate(manifest.Scenarios);
            bool needed = state?.ReportsWritten != true || manifest.Scenarios.Any(p => !state.IsFinalized(p.Id, p.Repetition)) ||
                !File.Exists(Path.Combine(_resultsRoot, target.Slug + ".json"));
            if (!needed && !File.Exists(CompletionPath(target.Slug))) MarkCompleted(target.Slug);
            return needed;
        }

        public static void AtomicWrite(string path, string content,
            Action<string, string> commit = null, Action<int> backoff = null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] bytes = new UTF8Encoding(false).GetBytes(content);
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        if (commit != null) commit(temporary, path);
                        else if (File.Exists(path)) File.Replace(temporary, path, null);
                        else File.Move(temporary, path);
                        break;
                    }
                    catch (Exception error) when (attempt < 3 && (error is IOException || error is UnauthorizedAccessException))
                    {
                        int milliseconds = 15 << attempt;
                        if (backoff != null) backoff(milliseconds);
#if !UNITY_WEBGL
                        else System.Threading.Thread.Sleep(milliseconds);
#endif
                    }
                }
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        public static IReadOnlyList<string> ListIncompleteRuns(string root, Action<string> reportIssue = null)
        {
            string runs = Path.Combine(root, ".runs");
            if (!Directory.Exists(runs)) return Array.Empty<string>();
            List<string> incomplete = new();
            foreach (string folder in Directory.GetDirectories(runs).OrderByDescending(Path.GetFileName, StringComparer.Ordinal))
            {
                try
                {
                    BenchmarkRunStore store = new(root, Path.GetFileName(folder));
                    if (!store.HasManifest) continue;
                    BenchmarkRunManifest manifest = store.LoadManifest();
                    if (manifest.RunId != Path.GetFileName(folder) || manifest.Targets == null || manifest.Targets.Count == 0 || manifest.FormatVersion != 1)
                        throw new InvalidDataException("Invalid manifest identity or format.");
                    if (manifest.Targets.Any(t => !File.Exists(store.CompletionPath(t.Slug)))) incomplete.Add(manifest.RunId);
                }
                catch (Exception error) when (error is ArgumentException || error is IOException || error is JsonException ||
                                               error is InvalidDataException || error is FormatException ||
                                               error is UnauthorizedAccessException || error is NullReferenceException)
                {
                    string message = $"Skipped benchmark run folder '{folder}': {error.Message}";
                    if (reportIssue != null) reportIssue(message);
                    else System.Diagnostics.Trace.TraceWarning(message);
                }
            }
            return incomplete;
        }

        /// <summary>Backs up all artifacts the selected report writer can replace, without taking another target's files.</summary>
        public static IReadOnlyList<string> BackupReportFiles(string root, string stem, string stamp)
        {
            if (Path.GetFileName(stem) != stem || Path.GetFileName(stamp) != stamp)
                throw new ArgumentException("Expected a report stem and timestamp without directory components.");
            List<string> longer = Directory.GetFiles(root, "BENCHMARK_*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(other => other.Length > stem.Length && other.StartsWith(stem, StringComparison.Ordinal)).ToList();
            List<string> backups = new();
            foreach (string source in Directory.GetFiles(root))
            {
                string name = Path.GetFileName(source);
                string extension = Path.GetExtension(source);
                bool own = name.StartsWith(stem + ".", StringComparison.Ordinal) || name.StartsWith(stem + "_", StringComparison.Ordinal);
                if (longer.Any(other => name.StartsWith(other + ".", StringComparison.Ordinal) || name.StartsWith(other + "_", StringComparison.Ordinal))) own = false;
                if (name != "INDEX.md" && (!own || !new[] { ".json", ".jsonl", ".md", ".png", ".svg", ".lua" }.Contains(extension))) continue;
                string destination = source + ".bak-" + stamp + (name == "INDEX.md" ? "-" + stem : "");
                int suffix = 1;
                string unique = destination;
                while (File.Exists(unique)) unique = destination + "-" + suffix++;
                destination = unique;
                File.Copy(source, destination, false);
                backups.Add(destination);
            }
            return backups;
        }

        /// <summary>Imports the flattened public report without recomputing any saved score or changing its result JSON.</summary>
        public static BenchmarkTargetState ImportReport(string json, string markdown = null, string toolTraceJsonl = null)
        {
            JObject document = BenchmarkJson.ParseObject(json);
            BenchmarkTargetState state = new()
            {
                PreserveResultOrder = true,
                Metadata = document["metadata"]?.ToObject<BenchmarkRunMetadata>()
                    ?? throw new InvalidDataException("Report metadata is missing.")
            };
            JArray rows = document["results"] as JArray ?? throw new InvalidDataException("Report results are missing.");
            IReadOnlyList<string> rawRows = BenchmarkJson.RawArrayObjects(json, "results");
            int rowIndex = 0;
            foreach (JObject row in rows.Cast<JObject>())
            {
                JObject data = (JObject)row.DeepClone();
                data.Remove("penalties");
                if (row["penaltyDetails"] != null) data["penalties"] = row["penaltyDetails"];
                ScenarioResult result = data.ToObject<ScenarioResult>();
                result.Score = GoalScore.Restore(
                    (double?)row["checkpointScore"] ?? 0, (double?)row["penalties"] ?? 0,
                    (int?)row["hardCap"], (double?)row["judgeScore"] ?? double.NaN,
                    (double?)row["judgeWeight"] ?? 0, (double?)row["mergedBase"] ?? (double?)row["base"] ?? 0,
                    (double?)row["correctnessBonus"] ?? (double?)row["bonus"] ?? 0,
                    (double?)row["tokenBonus"] ?? 0, (double?)row["timeBonus"] ?? 0,
                    (double?)row["efficiencyBonus"] ?? 0, (double?)row["bonus"] ?? 0,
                    (double?)row["base"] ?? 0, (double?)row["total"] ?? 0,
                    Enum.TryParse((string)row["classification"], out BenchmarkClassification classification) ? classification : BenchmarkClassification.Fail);
                result.ImportedJson = rawRows[rowIndex++];
                state.Results.Add(result);
            }
            state.ReportsWritten = true;
            state.Segments = state.Metadata.Segments;
            state.LegacyWallSeconds = (double?)document["metadata"]?["totalWallSeconds"]
                ?? (double?)document["summary"]?["totalWallSeconds"]
                ?? ((double?)document["summary"]?["totalLatencyMs"] ?? state.Results.Sum(result => result.LatencyMs)) / 1000.0;
            state.SeedLegacySegment();
            if (markdown != null || toolTraceJsonl != null)
                BenchmarkLegacyReportEvidence.Restore(state, markdown, toolTraceJsonl);
            state.BaselineSummaryJson = BenchmarkJson.ParseObject(BenchmarkReportFormatter.ToJson(state.ToReport()))["summary"].ToString(Formatting.None);
            state.OriginalSummaryJson = document["summary"]?.ToString(Formatting.None) ?? "";
            return state;
        }
    }
}
