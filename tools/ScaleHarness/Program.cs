using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CoreAI.Tools.Scale
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            Dictionary<string, string> options = ParseOptions(args);
            string baseDirectory = AppContext.BaseDirectory;
            string configPath = options.TryGetValue("config", out string configured)
                ? configured
                : Path.Combine(baseDirectory, "scale.workload.json");
            string actorLuaPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? baseDirectory,
                "scale_actor.lua");
            string serverLuaPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? baseDirectory,
                "scale_server.lua");

            ScaleWorkload workload = ScaleWorkload.Load(configPath);
            bool frozen = true;
            if (options.ContainsKey("quick"))
            {
                workload.Staircase = new List<int> { 20 };
                workload.Repeats = 1;
                workload.WarmupFrames = 30;
                workload.MeasurementFrames = 120;
                workload.Chat.StaggeredWindowStartFrame = 60;
                workload.Chat.StaggeredWindowEndFrame = 120;
                frozen = false;
            }

            if (options.TryGetValue("only", out string onlyText))
            {
                workload.Staircase = new List<int> { int.Parse(onlyText, CultureInfo.InvariantCulture) };
                frozen = false;
            }

            if (options.TryGetValue("repeats", out string repeatsText))
            {
                workload.Repeats = int.Parse(repeatsText, CultureInfo.InvariantCulture);
                frozen = false;
            }

            IReadOnlyList<string> errors = workload.Validate();
            if (errors.Count > 0)
            {
                Console.Error.WriteLine(string.Join(Environment.NewLine, errors));
                return 2;
            }

            string actorLua = File.ReadAllText(actorLuaPath);
            string serverLua = File.ReadAllText(serverLuaPath);
            ScaleStaircaseReport report = new ScaleStaircaseReport
            {
                Label = options.TryGetValue("label", out string label) ? label : "",
                StartedUtc = DateTime.UtcNow,
                FrozenWorkloadHonoured = frozen,
                WorkloadPath = workload.SourcePath,
                WorkloadSha256 = workload.Sha256,
                ActorLuaSha256 = Sha256Of(actorLuaPath),
                ServerLuaSha256 = Sha256Of(serverLuaPath),
                Workload = workload,
                Environment = DescribeEnvironment(options)
            };

            Action<string> log = message => Console.Error.WriteLine(
                DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " " + message);
            log("workload " + workload.SourcePath + " sha256 " + workload.Sha256 + (frozen ? " (frozen)" : " (OVERRIDDEN: smoke only)"));
            foreach (KeyValuePair<string, string> pair in report.Environment)
            {
                log(pair.Key + " = " + pair.Value);
            }

            ScaleRunner runner = new ScaleRunner(workload, actorLua, serverLua, log);
            foreach (int actorCount in workload.Staircase)
            {
                for (int repeat = 1; repeat <= workload.Repeats; repeat++)
                {
                    report.Repeats.Add(runner.Run(actorCount, repeat));
                }
            }

            report.CompletedUtc = DateTime.UtcNow;
            ScaleReportBuilder.Summarize(report);
            string markdown = ScaleReportBuilder.ToMarkdown(report);
            Console.WriteLine(markdown);

            JsonSerializerOptions jsonOptions = ScaleWorkload.CreateJsonOptions();
            if (options.TryGetValue("output", out string outputPath))
            {
                string directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(outputPath, JsonSerializer.Serialize(report, jsonOptions) + Environment.NewLine);
                log("report written to " + Path.GetFullPath(outputPath));
            }

            if (options.TryGetValue("markdown", out string markdownPath))
            {
                string directory = Path.GetDirectoryName(Path.GetFullPath(markdownPath));
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(markdownPath, markdown);
            }

            bool anyZero = false;
            foreach (ScaleStepSummary step in report.Steps)
            {
                anyZero |= step.ZeroCounters.Count > 0;
            }

            return anyZero ? 1 : 0;
        }

        private static Dictionary<string, string> DescribeEnvironment(Dictionary<string, string> options)
        {
            Dictionary<string, string> environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["runtime"] = RuntimeInformation.FrameworkDescription,
                ["runtimeIdentifier"] = RuntimeInformation.RuntimeIdentifier,
                ["processArchitecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
                ["os"] = RuntimeInformation.OSDescription,
                ["processorCount"] = Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture),
                ["serverGc"] = GCSettings.IsServerGC.ToString(),
                ["gcLatencyMode"] = GCSettings.LatencyMode.ToString(),
                ["tieredCompilation"] = (AppContext.GetData("System.Runtime.TieredCompilation") ?? "default").ToString(),
                ["harnessOptimized"] = (!IsJitOptimizerDisabled(typeof(Program).Assembly)).ToString(),
                ["coreAiModsOptimized"] = (!IsJitOptimizerDisabled(typeof(CoreAI.Composition.CoreAiModsInstaller).Assembly)).ToString(),
                ["rbxInstancesOptimized"] = (!IsJitOptimizerDisabled(typeof(CoreAI.Mods.Rbx.Instances.Scheduling.ModScheduler).Assembly)).ToString(),
                ["coreAiSourceOptimized"] = (!IsJitOptimizerDisabled(typeof(CoreAI.Ai.QueuedAiOrchestrator).Assembly)).ToString(),
                ["coreAiModsLocation"] = typeof(CoreAI.Composition.CoreAiModsInstaller).Assembly.Location,
                ["luaDllSha256"] = Sha256Of(typeof(global::Lua.LuaState).Assembly.Location),
                ["gitHead"] = ReadGitHead(options),
                ["powerPlan"] = ReadPowerPlan(),
                ["stopwatchFrequency"] = Stopwatch.Frequency.ToString(CultureInfo.InvariantCulture)
            };
            return environment;
        }

        private static bool IsJitOptimizerDisabled(Assembly assembly)
        {
            DebuggableAttribute attribute =
                (DebuggableAttribute)Attribute.GetCustomAttribute(assembly, typeof(DebuggableAttribute));
            return attribute != null && attribute.IsJITOptimizerDisabled;
        }

        private static string Sha256Of(string path)
        {
            try
            {
                using SHA256 sha = SHA256.Create();
                using FileStream stream = File.OpenRead(path);
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            }
            catch (Exception ex)
            {
                return "unavailable: " + ex.Message;
            }
        }

        private static string ReadGitHead(Dictionary<string, string> options)
        {
            try
            {
                string repo = options.TryGetValue("repo", out string configured)
                    ? configured
                    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
                ProcessStartInfo info = new ProcessStartInfo("git", "rev-parse --short=12 HEAD")
                {
                    WorkingDirectory = repo,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using Process process = Process.Start(info);
                string output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit(5000);
                return output.Length == 0 ? "unavailable" : output;
            }
            catch (Exception ex)
            {
                return "unavailable: " + ex.Message;
            }
        }

        private static string ReadPowerPlan()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return "n/a";
            }

            try
            {
                ProcessStartInfo info = new ProcessStartInfo("powercfg", "/getactivescheme")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using Process process = Process.Start(info);
                string output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit(5000);
                int open = output.LastIndexOf('(');
                int close = output.LastIndexOf(')');
                return open >= 0 && close > open ? output.Substring(open + 1, close - open - 1) : output;
            }
            catch (Exception ex)
            {
                return "unavailable: " + ex.Message;
            }
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
                if (string.Equals(key, "quick", StringComparison.OrdinalIgnoreCase))
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
    }
}
