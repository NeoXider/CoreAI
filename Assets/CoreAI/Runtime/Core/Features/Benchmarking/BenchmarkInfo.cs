using System.Collections.Generic;

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
        public const string Version = "v2";

        /// <summary>"CoreAI Game-Creation Benchmark v2" — name and version combined.</summary>
        public const string TitleWithVersion = SuiteName + " " + Version;

        /// <summary>
        /// Canonical per-group difficulty on a single 1–10 scale, the ONE source both the editor window
        /// (RUN tab toggles) and the PlayMode scenarios/progress read from, so the number never disagrees
        /// between UI and history. Each scenario maps its own <c>Difficulty</c> to this via
        /// <see cref="GroupDifficulty10"/>. Keep these in sync with the per-group scenario design.
        /// </summary>
        public static readonly IReadOnlyDictionary<string, int> GroupDifficulty10 = new Dictionary<string, int>
        {
            { "G2", 2 }, // runtime mechanic authoring (pure Lua)
            { "G1", 3 }, // build a game (world + Lua)
            { "G5", 5 }, // strict instruction-following (subtractive)
            { "G6", 6 }, // free-build visual (bonus; default castle)
            { "G3", 7 }, // reasoning & design
            { "G8", 7 }, // observe described state, then act (director-AI; conditional selection + rule encoding)
            { "G4", 8 }, // playable game (simulated playthrough)
            { "G7", 9 } // comprehensive integration (world + Lua cross-consistency; one-off, like G6)
        };

        /// <summary>Difficulty (1–10) for a group id; 5 (mid) when the group is unknown.</summary>
        public static int DifficultyFor(string group)
        {
            return group != null && GroupDifficulty10.TryGetValue(group, out int d) ? d : 5;
        }

        /// <summary>Every scenario group in display order, so charts keep one column per group even when a run skipped some.</summary>
        public static readonly IReadOnlyList<string> Groups = new[] { "G1", "G2", "G3", "G4", "G5", "G6", "G7", "G8" };

        /// <summary>Short title per group, printed beside the group id on cards and tables.</summary>
        public static readonly IReadOnlyDictionary<string, string> GroupTitles = new Dictionary<string, string>
        {
            { "G1", "Build" },
            { "G2", "Mechanics" },
            { "G3", "Reasoning" },
            { "G4", "Playthrough" },
            { "G5", "Instructions" },
            { "G6", "Free build" },
            { "G7", "Integration" },
            { "G8", "Observe & act" }
        };

        /// <summary>Short title for a group id; the id itself when the group is unknown.</summary>
        public static string TitleFor(string group)
        {
            return group != null && GroupTitles.TryGetValue(group, out string title) ? title : group ?? string.Empty;
        }
    }
}
