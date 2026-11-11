using System;
using System.Collections.Generic;
using System.Globalization;

namespace CoreAI.Mods.Performance
{
    /// <summary>
    /// What a measurement run does: which scenes, how many frames, where the report goes. Read from a player
    /// command line (<c>-coreaiPerf all -coreaiPerfOut report.json</c>), from environment variables
    /// (<c>COREAI_PERF_SCENARIOS</c>, <c>COREAI_PERF_JSON</c>, ...) or from a WebGL page's URL query
    /// (<c>?coreaiPerf=all</c>); the command line wins over the environment, which wins over the defaults.
    /// </summary>
    public sealed class PerfRunnerOptions
    {
        /// <summary>Frames run after a scene is built and before it is measured.</summary>
        public const int DefaultWarmupFrames = 120;

        /// <summary>Frames measured per scene.</summary>
        public const int DefaultMeasuredFrames = 600;

        /// <summary>Command-line switch (and URL query key) that requests a run; its value is the scenario list.</summary>
        public const string ScenariosSwitch = "-coreaiPerf";

        /// <summary>True when the command line, the environment or the URL asked for a run.</summary>
        public bool Requested { get; private set; }

        /// <summary>Comma-separated scenario ids, or <c>all</c>.</summary>
        public string Scenarios { get; set; } = "all";

        /// <summary>Report file path; empty means the report is printed to the log between the markers only.</summary>
        public string OutputPath { get; set; } = "";

        /// <summary>Frames run before measuring each scene.</summary>
        public int WarmupFrames { get; set; } = DefaultWarmupFrames;

        /// <summary>Frames measured per scene.</summary>
        public int MeasuredFrames { get; set; } = DefaultMeasuredFrames;

        /// <summary>Free text stored in the report environment (build type, machine nickname).</summary>
        public string Label { get; set; } = "";

        /// <summary>The commit the player was built from; a player cannot ask git, so the builder passes it.</summary>
        public string Commit { get; set; } = "";

        /// <summary>Quit the player when the run is over (Standalone; ignored in the Editor and on WebGL).</summary>
        public bool QuitWhenDone { get; set; }

        /// <summary>Reads options from the sources in order: defaults, environment, URL query, command line.</summary>
        /// <param name="commandLine">The process arguments (may be null).</param>
        /// <param name="environment">Environment lookup (may be null).</param>
        /// <param name="url">The page URL of a WebGL player (may be null or empty).</param>
        public static PerfRunnerOptions Read(IReadOnlyList<string> commandLine, Func<string, string> environment,
            string url)
        {
            PerfRunnerOptions options = new();
            if (environment != null)
            {
                options.ApplyEnvironment(environment);
            }

            if (!string.IsNullOrEmpty(url))
            {
                options.ApplyQuery(url);
            }

            if (commandLine != null)
            {
                options.ApplyCommandLine(commandLine);
            }

            return options;
        }

        private void ApplyEnvironment(Func<string, string> environment)
        {
            string scenarios = environment("COREAI_PERF_SCENARIOS");
            if (!string.IsNullOrWhiteSpace(scenarios))
            {
                Scenarios = scenarios.Trim();
                Requested = true;
            }

            OutputPath = NonEmpty(environment("COREAI_PERF_JSON"), OutputPath);
            WarmupFrames = ParseFrames(environment("COREAI_PERF_WARMUP"), WarmupFrames);
            MeasuredFrames = ParseFrames(environment("COREAI_PERF_FRAMES"), MeasuredFrames);
            Label = NonEmpty(environment("COREAI_PERF_LABEL"), Label);
            Commit = NonEmpty(environment("COREAI_PERF_COMMIT"), Commit);
            QuitWhenDone |= environment("COREAI_PERF_QUIT") == "1";
        }

        private void ApplyQuery(string url)
        {
            int start = url.IndexOf('?');
            if (start < 0)
            {
                return;
            }

            int end = url.IndexOf('#', start);
            string query = end < 0 ? url.Substring(start + 1) : url.Substring(start + 1, end - start - 1);
            string[] pairs = query.Split('&');
            for (int index = 0; index < pairs.Length; index++)
            {
                int equals = pairs[index].IndexOf('=');
                string key = equals < 0 ? pairs[index] : pairs[index].Substring(0, equals);
                string value = equals < 0 ? "" : Uri.UnescapeDataString(pairs[index].Substring(equals + 1).Replace('+', ' '));
                Apply(key, value, true);
            }
        }

        private void ApplyCommandLine(IReadOnlyList<string> commandLine)
        {
            for (int index = 0; index < commandLine.Count; index++)
            {
                string argument = commandLine[index];
                if (argument == null || !argument.StartsWith("-coreaiPerf", StringComparison.Ordinal))
                {
                    continue;
                }

                string value = index + 1 < commandLine.Count && commandLine[index + 1] != null
                                                             && !commandLine[index + 1].StartsWith("-", StringComparison.Ordinal)
                    ? commandLine[index + 1]
                    : "";
                Apply(argument.Substring(1), value, false);
            }
        }

        private void Apply(string key, string value, bool fromQuery)
        {
            switch (key)
            {
                case "coreaiPerf":
                    Requested = true;
                    Scenarios = string.IsNullOrWhiteSpace(value) ? "all" : value.Trim();
                    break;
                case "coreaiPerfOut":
                    if (!fromQuery)
                    {
                        OutputPath = NonEmpty(value, OutputPath);
                    }

                    break;
                case "coreaiPerfWarmup":
                    WarmupFrames = ParseFrames(value, WarmupFrames);
                    break;
                case "coreaiPerfFrames":
                    MeasuredFrames = ParseFrames(value, MeasuredFrames);
                    break;
                case "coreaiPerfLabel":
                    Label = NonEmpty(value, Label);
                    break;
                case "coreaiPerfCommit":
                    Commit = NonEmpty(value, Commit);
                    break;
                case "coreaiPerfQuit":
                    QuitWhenDone = true;
                    break;
            }
        }

        private static string NonEmpty(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        private static int ParseFrames(string value, int fallback)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int frames) && frames > 0
                ? frames
                : fallback;
        }
    }
}
