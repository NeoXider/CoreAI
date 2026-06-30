namespace CoreAI.Benchmarking
{
    /// <summary>
    /// Single source of truth for the game-creation benchmark's identity and version. Bump
    /// <see cref="Version"/> whenever the scenarios, prompts, or grading change in a way that makes new
    /// scores non-comparable with older runs, so results stay versioned (v1 -> v2 -> ...).
    /// </summary>
    public static class BenchmarkInfo
    {
        /// <summary>Display name of the benchmark suite.</summary>
        public const string SuiteName = "CoreAI Game-Creation Benchmark";

        /// <summary>Current benchmark version tag (scenarios + prompts + grading).</summary>
        public const string Version = "v1";

        /// <summary>"CoreAI Game-Creation Benchmark v1" — name and version combined.</summary>
        public const string TitleWithVersion = SuiteName + " " + Version;
    }
}
