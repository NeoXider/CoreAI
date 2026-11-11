using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using CoreAI.Mods.Performance;

namespace CoreAI.PerfBench
{
    /// <summary>The facts every number is reported with: runtime, machine, commit and the measured binary.</summary>
    internal static class BenchEnvironment
    {
        /// <summary>Fills <paramref name="report"/> with the environment of this process.</summary>
        public static void Describe(PerfRunReport report, string repoRoot, string label)
        {
            report.SetEnvironment("label", label ?? "");
            report.SetEnvironment("capturedAtUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            report.SetEnvironment("runtime", RuntimeInformation.FrameworkDescription);
            report.SetEnvironment("runtimeKind", IsMono ? "mono" : "coreclr");
            report.SetEnvironment("gc", DescribeGc());
            report.SetEnvironment("processArchitecture", RuntimeInformation.ProcessArchitecture.ToString());
            report.SetEnvironment("os", RuntimeInformation.OSDescription);
            report.SetEnvironment("processorCount", Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture));
            report.SetEnvironment("processor", Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "");
            report.SetEnvironment("machine", Environment.MachineName);
            report.SetEnvironment("stopwatchFrequency", Stopwatch.Frequency.ToString(CultureInfo.InvariantCulture));
            report.SetEnvironment("commit", Git(repoRoot, "rev-parse HEAD"));
            report.SetEnvironment("uncommittedPaths", CountLines(Git(repoRoot, "status --porcelain")));
            report.SetEnvironment("luaDllSha256", Sha256(Path.Combine(repoRoot, "Assets", "CoreAIMods", "Plugins", "Lua.dll")));
            report.SetEnvironment("harnessBuild", "Release, Optimize=true; production sources compiled by tools/portable/LuaTier (UnityEngine shim, no rendering)");
            report.SetEnvironment("allocationCounter", AllocationCounterWorks()
                ? "GC.GetAllocatedBytesForCurrentThread (exact)"
                : "unavailable on this runtime (bytes columns are null)");
        }

        /// <summary>True on the Mono runtime.</summary>
        public static bool IsMono => Type.GetType("Mono.Runtime") != null;

        /// <summary>
        /// True when the per-thread allocation counter moves for a known allocation; Mono answers 0.
        /// </summary>
        public static bool AllocationCounterWorks()
        {
            long before = AllocatedBytes();
            object[] probe = new object[64];
            GC.KeepAlive(probe);
            return AllocatedBytes() - before >= 64 * IntPtr.Size;
        }

        /// <summary>Bytes allocated on this thread so far (0 where the runtime does not count).</summary>
        public static long AllocatedBytes()
        {
            return GC.GetAllocatedBytesForCurrentThread();
        }

        /// <summary>Finds the checkout root from the working directory or this assembly's location.</summary>
        public static string FindRepoRoot()
        {
            string[] starts = { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
            for (int index = 0; index < starts.Length; index++)
            {
                DirectoryInfo directory = new(starts[index]);
                while (directory != null)
                {
                    if (File.Exists(Path.Combine(directory.FullName, "Assets", "CoreAIMods", "Plugins", "Lua.dll")))
                    {
                        return directory.FullName;
                    }

                    directory = directory.Parent;
                }
            }

            throw new InvalidOperationException("Run from inside the CoreAI checkout (Assets/CoreAIMods/Plugins/Lua.dll not found).");
        }

        private static string DescribeGc()
        {
            if (IsMono)
            {
                return "mono (" + (Environment.GetEnvironmentVariable("MONO_ENV_OPTIONS") ?? "") + ")";
            }

            return (GCSettings.IsServerGC ? "server" : "workstation") + ", latency=" + GCSettings.LatencyMode;
        }

        private static string Git(string repoRoot, string arguments)
        {
            try
            {
                ProcessStartInfo start = new("git", arguments)
                {
                    WorkingDirectory = repoRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using Process process = Process.Start(start);
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(10_000);
                return output.Trim();
            }
            catch (Exception exception)
            {
                return "unknown (" + exception.GetType().Name + ")";
            }
        }

        private static string CountLines(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return "0";
            }

            return text.Split('\n').Length.ToString(CultureInfo.InvariantCulture);
        }

        private static string Sha256(string path)
        {
            if (!File.Exists(path))
            {
                return "missing";
            }

            using SHA256 sha = SHA256.Create();
            using FileStream stream = File.OpenRead(path);
            byte[] hash = sha.ComputeHash(stream);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }
    }
}
