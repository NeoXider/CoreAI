#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
#if !COREAI_NO_LLM && !UNITY_WEBGL
using System.Collections.Generic;
using static CoreAI.Tests.PlayMode.Benchmarks.GameCreationBenchmarkHarness;

namespace CoreAI.Tests.PlayMode.Benchmarks
{
    /// <summary>
    /// The single registry of benchmark scenario groups. To add a future group (e.g. G3 — incremental
    /// builds), implement its scenarios and append one line here — nothing else in the harness, scoring,
    /// report, or comparison needs to change.
    /// </summary>
    internal static class BenchmarkSuiteCatalog
    {
        public static IReadOnlyList<GameBenchmarkScenario> All()
        {
            List<GameBenchmarkScenario> all = new();
            all.AddRange(GameMechanicScenariosG2.All()); // G2 — runtime mechanic authoring
            all.AddRange(GameBuildScenariosG1.All()); // G1 — build a game from a spec
            all.AddRange(GameReasoningScenariosG3.All()); // G3 — reasoning & design (harder, intelligence)
            all.AddRange(GamePlaythroughScenariosG4.All()); // G4 — playable game (simulated playthrough)
            all.AddRange(GameInstructionScenariosG5.All()); // G5 — strict instruction-following (subtractive)
            all.AddRange(GameFreeBuildScenariosG6.All()); // G6 — free-form visual build (default: castle)
            all.AddRange(GameIntegrationScenariosG7.All()); // G7 — comprehensive: world + Lua consistency
            return all;
        }
    }
}
#endif
#endif