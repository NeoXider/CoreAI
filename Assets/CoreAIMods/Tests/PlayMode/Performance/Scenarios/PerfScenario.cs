using System;
using System.Collections.Generic;

namespace CoreAI.Mods.Performance
{
    /// <summary>
    /// One P19 reference scene: what the world holds and which Lua runs in it. The same definition
    /// drives the engine-free benchmark (tools/perfbench), the Editor PlayMode fixtures and the
    /// player runner, so a row in every table names the same workload.
    /// </summary>
    public sealed class PerfScenario
    {
        /// <param name="id">Stable identifier used on command lines, in test names and in the JSON report.</param>
        /// <param name="description">One line saying what the scene contains.</param>
        /// <param name="staticPartCount">Anchored Parts the host builds before measuring; no script touches them.</param>
        /// <param name="moverCount">Parts a mod creates and moves from Heartbeat every frame (0 for none).</param>
        /// <param name="moverHandlerCount">Heartbeat handlers the movers are split across.</param>
        /// <param name="loadsBundledSampleMods">True when every bundled sample mod is loaded and running.</param>
        public PerfScenario(string id, string description, int staticPartCount, int moverCount,
            int moverHandlerCount, bool loadsBundledSampleMods)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("A scenario id is required.", nameof(id));
            }

            Id = id;
            Description = description ?? "";
            StaticPartCount = Math.Max(0, staticPartCount);
            MoverCount = Math.Max(0, moverCount);
            MoverHandlerCount = moverCount > 0 ? Math.Max(1, moverHandlerCount) : 0;
            LoadsBundledSampleMods = loadsBundledSampleMods;
        }

        /// <summary>Stable identifier used on command lines, in test names and in the JSON report.</summary>
        public string Id { get; }

        /// <summary>One line saying what the scene contains.</summary>
        public string Description { get; }

        /// <summary>Anchored Parts the host builds before measuring; no script touches them.</summary>
        public int StaticPartCount { get; }

        /// <summary>Parts a mod creates and moves from Heartbeat every frame.</summary>
        public int MoverCount { get; }

        /// <summary>Heartbeat handlers the movers are split across (0 when there are no movers).</summary>
        public int MoverHandlerCount { get; }

        /// <summary>True when every bundled sample mod is loaded and running.</summary>
        public bool LoadsBundledSampleMods { get; }

        /// <summary>Parts in the world once the scene is built, movers included.</summary>
        public int TotalPartCount => StaticPartCount + MoverCount;
    }

    /// <summary>The reference scenes of P19 and the parsing of a scenario list.</summary>
    public static class PerfScenarioCatalog
    {
        /// <summary>Parts a Heartbeat scene moves every frame, the same in both sizes so only the part count changes.</summary>
        public const int HeartbeatMoverCount = 256;

        /// <summary>Handlers the movers are split across; each stays well inside the default per-resume step budget.</summary>
        public const int HeartbeatHandlerCount = 4;

        /// <summary>An empty world with the mod stack composed and no mods.</summary>
        public static readonly PerfScenario IdleWorld = new(
            "idle-world", "empty Rbx world, mod stack composed, no mods", 0, 0, 0, false);

        /// <summary>Every bundled sample mod loaded and running.</summary>
        public static readonly PerfScenario SampleMods = new(
            "sample-mods", "every bundled sample mod (Resources/CoreAIMods) loaded and running", 0, 0, 0, true);

        /// <summary>2,048 anchored Parts and no script.</summary>
        public static readonly PerfScenario Parts2048Static = new(
            "parts-2048-static", "2,048 anchored Parts, no script", 2048, 0, 0, false);

        /// <summary>2,048 Parts, 256 of them moved from Heartbeat.</summary>
        public static readonly PerfScenario Parts2048Heartbeat = new(
            "parts-2048-heartbeat", "1,792 anchored Parts + 256 Parts moved every frame by 4 Heartbeat handlers",
            2048 - HeartbeatMoverCount, HeartbeatMoverCount, HeartbeatHandlerCount, false);

        /// <summary>16,384 anchored Parts and no script.</summary>
        public static readonly PerfScenario Parts16384Static = new(
            "parts-16384-static", "16,384 anchored Parts, no script", 16384, 0, 0, false);

        /// <summary>16,384 Parts, 256 of them moved from Heartbeat.</summary>
        public static readonly PerfScenario Parts16384Heartbeat = new(
            "parts-16384-heartbeat", "16,128 anchored Parts + 256 Parts moved every frame by 4 Heartbeat handlers",
            16384 - HeartbeatMoverCount, HeartbeatMoverCount, HeartbeatHandlerCount, false);

        /// <summary>Every reference scene in the order a full run measures them.</summary>
        public static IReadOnlyList<PerfScenario> All { get; } = new[]
        {
            IdleWorld, SampleMods, Parts2048Static, Parts2048Heartbeat, Parts16384Static, Parts16384Heartbeat
        };

        /// <summary>Finds a reference scene by id (ordinal, case-insensitive).</summary>
        public static bool TryGet(string id, out PerfScenario scenario)
        {
            for (int index = 0; index < All.Count; index++)
            {
                if (string.Equals(All[index].Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    scenario = All[index];
                    return true;
                }
            }

            scenario = null;
            return false;
        }

        /// <summary>
        /// Parses a comma-separated scenario list; empty or <c>all</c> selects every scene. An unknown id
        /// is an error rather than a silent skip, so a typo never produces a report that looks complete.
        /// </summary>
        public static IReadOnlyList<PerfScenario> ParseList(string list)
        {
            if (string.IsNullOrWhiteSpace(list) || string.Equals(list.Trim(), "all", StringComparison.OrdinalIgnoreCase))
            {
                return All;
            }

            List<PerfScenario> selected = new();
            string[] parts = list.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int index = 0; index < parts.Length; index++)
            {
                if (!TryGet(parts[index].Trim(), out PerfScenario scenario))
                {
                    throw new ArgumentException(
                        "Unknown performance scenario '" + parts[index] + "'; known: " + KnownIds() + ".",
                        nameof(list));
                }

                if (!selected.Contains(scenario))
                {
                    selected.Add(scenario);
                }
            }

            return selected;
        }

        private static string KnownIds()
        {
            string[] ids = new string[All.Count];
            for (int index = 0; index < All.Count; index++)
            {
                ids[index] = All[index].Id;
            }

            return string.Join(", ", ids);
        }
    }
}
