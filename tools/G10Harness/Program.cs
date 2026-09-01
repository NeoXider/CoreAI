using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using CoreAI.Diagnostics.G10;

namespace CoreAI.Tools.G10
{
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            Dictionary<string, string> options = ParseOptions(args);
            if (!options.TryGetValue("config", out string configPath))
            {
                Console.Error.WriteLine(
                    "Usage: dotnet run --project tools/G10Harness/G10Harness.csproj -- --config <json> [--output <json>] [--script <lua>] [--quick] [--discovered-tests <count> --skipped-tests <count> --discovery-source <path>]");
                return 2;
            }

            string scriptPath = options.TryGetValue("script", out string configuredScriptPath)
                ? configuredScriptPath
                : Path.Combine(
                    "Assets",
                    "CoreAIMods",
                    "Runtime",
                    "Resources",
                    "CoreAI",
                    "G10",
                    "bench_actor.lua");
            JsonSerializerOptions jsonOptions = CreateJsonOptions();
            string configJson = await File.ReadAllTextAsync(configPath);
            G10MeasurementConfiguration configuration = JsonSerializer.Deserialize<G10MeasurementConfiguration>(
                configJson,
                jsonOptions);
            if (configuration == null)
            {
                Console.Error.WriteLine("Configuration did not deserialize to a G10MeasurementConfiguration.");
                return 2;
            }

            if (options.ContainsKey("quick"))
            {
                configuration.WarmupSeconds = 1d;
                configuration.MeasurementSeconds = 2d;
            }

            if (options.TryGetValue("discovered-tests", out string discoveredText))
            {
                if (!int.TryParse(discoveredText, out int discoveredTests) || discoveredTests < 0)
                {
                    Console.Error.WriteLine("--discovered-tests must be a non-negative integer.");
                    return 2;
                }

                configuration.DiscoveredTestCount = discoveredTests;
            }

            if (options.TryGetValue("skipped-tests", out string skippedText))
            {
                if (!int.TryParse(skippedText, out int skippedTests) || skippedTests < 0)
                {
                    Console.Error.WriteLine("--skipped-tests must be a non-negative integer.");
                    return 2;
                }

                configuration.SkippedTestCount = skippedTests;
            }

            if (options.TryGetValue("discovery-source", out string discoverySource))
            {
                configuration.DiscoveryEvidenceSource = discoverySource;
            }

            IReadOnlyList<string> validationErrors = configuration.Validate();
            if (validationErrors.Count > 0)
            {
                Console.Error.WriteLine(string.Join(Environment.NewLine, validationErrors));
                return 2;
            }

            string script = await File.ReadAllTextAsync(scriptPath);
            G10MeasurementReport report = await G10MeasurementRunner.RunAsync(
                configuration,
                script,
                message => Console.Error.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " " + message));
            string reportJson = JsonSerializer.Serialize(report, jsonOptions);
            if (!options.ContainsKey("quiet"))
            {
                Console.WriteLine(reportJson);
            }
            if (options.TryGetValue("output", out string outputPath))
            {
                string directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                await File.WriteAllTextAsync(outputPath, reportJson + Environment.NewLine);
            }

            return report.Gate.Failures.Count == 0 ? 0 : 1;
        }

        private static Dictionary<string, string> ParseOptions(string[] args)
        {
            Dictionary<string, string> options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < args.Length; index++)
            {
                string argument = args[index];
                if (!argument.StartsWith("--", StringComparison.Ordinal))
                {
                    continue;
                }

                string key = argument.Substring(2);
                if (string.Equals(key, "quick", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, "quiet", StringComparison.OrdinalIgnoreCase))
                {
                    options[key] = "true";
                    continue;
                }

                if (index + 1 >= args.Length)
                {
                    throw new ArgumentException("Missing value for --" + key);
                }

                options[key] = args[index + 1];
                index++;
            }

            return options;
        }

        private static JsonSerializerOptions CreateJsonOptions()
        {
            JsonSerializerOptions options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            };
            options.Converters.Add(new JsonStringEnumConverter());
            return options;
        }
    }
}
