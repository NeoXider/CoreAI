using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CoreAI.Benchmarking
{
    /// <summary>Shares hero selection between report writing and legacy artifact recovery.</summary>
    public static class BenchmarkArtifactRecovery
    {
        public static ScenarioResult FindFreeBuildHeroResult(IReadOnlyList<ScenarioResult> results,
            Func<ScenarioResult, bool> screenshotAvailable = null)
        {
            ScenarioResult plain = null;
            foreach (ScenarioResult result in results)
            {
                bool image = screenshotAvailable?.Invoke(result) ?? result.SceneScreenshotPng?.Length > 0;
                if (!image || !string.Equals(result.Group, "G6", StringComparison.OrdinalIgnoreCase) ||
                    !string.IsNullOrEmpty(result.Failure) || result.Attribution == FailureAttribution.Environment ||
                    result.Attribution == FailureAttribution.Framework) continue;
                if (string.Equals(result.ScenarioId, "g6_free_build_vision", StringComparison.OrdinalIgnoreCase)) return result;
                if (plain == null && string.Equals(result.ScenarioId, "g6_free_build", StringComparison.OrdinalIgnoreCase)) plain = result;
            }
            return plain;
        }

        public static ScenarioResult RestoreVisualArtifacts(BenchmarkTargetState state, string directory, string stem)
        {
            string heroPath = Path.Combine(directory, stem + "_g6_free_build_hero.png");
            string replayPath = Path.Combine(directory, stem + "_g6_replay.lua");
            bool HasHeroEvidence(ScenarioResult result) => result.ImportedSceneMarkdown.Length == 0 ||
                !result.ImportedSceneMarkdown.Contains(stem + "_" + result.ScenarioId + ".png");
            List<ScenarioResult> candidates = state.Results.Where(result =>
                FindFreeBuildHeroResult(new[] { result }, HasHeroEvidence) != null).ToList();
            if (File.Exists(heroPath) && candidates.Count > 1)
                throw new InvalidDataException("Cannot restore the G6 hero: legacy artifacts do not identify which successful repetition owns the hero image. Restore the original scene sections or provide an unambiguous checkpoint.");
            ScenarioResult hero = File.Exists(heroPath) ? FindFreeBuildHeroResult(candidates, HasHeroEvidence) : null;
            foreach (ScenarioResult result in state.Results)
            {
                if (ReferenceEquals(result, hero)) continue;
                string screenshot = Path.Combine(directory, stem + "_" + result.ScenarioId + ".png");
                if (File.Exists(screenshot)) result.SceneScreenshotPng = File.ReadAllBytes(screenshot);
            }
            if (hero != null)
            {
                hero.SceneScreenshotPng = File.ReadAllBytes(heroPath);
                if (File.Exists(replayPath)) hero.LuaReplayScript = File.ReadAllText(replayPath);
            }
            return hero;
        }
    }
}
