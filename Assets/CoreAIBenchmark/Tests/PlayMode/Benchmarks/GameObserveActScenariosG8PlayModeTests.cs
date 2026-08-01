#if COREAI_LUA
#if COREAI_LLM && !UNITY_WEBGL
using System;
using CoreAI.Benchmarking;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;
using UnityEngine;
using static CoreAI.Tests.PlayMode.Benchmarks.GameCreationBenchmarkHarness;

namespace CoreAI.Tests.PlayMode.Benchmarks
{
    public sealed class GameObserveActScenariosG8PlayModeTests
    {
        [Test]
        public void SelectiveRaise_ExactScaleOnlyChanges_Pass()
        {
            WithEnvironment((scenario, env) =>
            {
                env.World.Commands.Add(ScaleOnly("TowerB", 2f));
                env.World.Commands.Add(ScaleOnly("TowerC", 2f));

                ScenarioGrading grading = scenario.Grade(env, SuccessfulRun(2));

                AssertCheckpoint(grading, "raised_short_B", true);
                AssertCheckpoint(grading, "raised_short_C", true);
                AssertCheckpoint(grading, "left_tall_A", true);
                AssertCheckpoint(grading, "exactly_two", true);
                Assert.AreEqual(0, grading.Penalties.Count);
            }, "g8_selective_raise");
        }

        [Test]
        public void SelectiveRaise_WrongScaleOrPositionPollution_FailsExactChecks()
        {
            WithEnvironment((scenario, env) =>
            {
                RecordedWorldCommand wrongScale = ScaleOnly("TowerB", 3f);
                RecordedWorldCommand polluted = ScaleOnly("TowerC", 2f);
                polluted.HasPosition = true;
                polluted.HasX = true;
                polluted.X = 99f;
                env.World.Commands.Add(wrongScale);
                env.World.Commands.Add(polluted);

                ScenarioGrading grading = scenario.Grade(env, SuccessfulRun(2));

                AssertCheckpoint(grading, "raised_short_B", false);
                AssertCheckpoint(grading, "raised_short_C", false);
                AssertCheckpoint(grading, "exactly_two", false);
                Assert.Greater(grading.Penalties.Count, 0);
            }, "g8_selective_raise");
        }

        [Test]
        public void TidyScene_AllowsObservationButRejectsExtraMutation()
        {
            WithEnvironment((scenario, env) =>
            {
                env.World.Commands.Add(new RecordedWorldCommand { Action = "list_objects" });
                env.World.Commands.Add(new RecordedWorldCommand { Action = "destroy", TargetName = "Debris1" });
                env.World.Commands.Add(new RecordedWorldCommand { Action = "destroy", TargetName = "Debris2" });

                ScenarioGrading clean = scenario.Grade(env, SuccessfulRun(3));
                AssertCheckpoint(clean, "only_debris", true);

                env.World.Commands.Add(new RecordedWorldCommand
                {
                    Action = "change",
                    TargetName = "Tower",
                    HasScale = true,
                    FloatValue = 2f
                });

                ScenarioGrading polluted = scenario.Grade(env, SuccessfulRun(4));
                AssertCheckpoint(polluted, "only_debris", false);
                Assert.Greater(polluted.Penalties.Count, 0);
            }, "g8_tidy_scene");
        }

        private static RecordedWorldCommand ScaleOnly(string targetName, float scale)
        {
            return new RecordedWorldCommand
            {
                Action = "change",
                TargetName = targetName,
                HasScale = true,
                FloatValue = scale
            };
        }

        private static RunObservation SuccessfulRun(int toolCalls)
        {
            return new RunObservation { Turns = 1, ToolCalls = toolCalls };
        }

        private static void AssertCheckpoint(ScenarioGrading grading, string id, bool expected)
        {
            BenchmarkCheckpoint checkpoint = grading.Checkpoints.Find(c => c.Id == id);
            Assert.IsNotNull(checkpoint, $"Missing checkpoint '{id}'.");
            Assert.AreEqual(expected, checkpoint.Passed, id);
        }

        private static void WithEnvironment(
            Action<GameBenchmarkScenario, BenchmarkEnvironment> assertion, string scenarioId)
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            try
            {
                GameBenchmarkScenario scenario = Array.Find(
                    GameObserveActScenariosG8.All(), candidate => candidate.Id == scenarioId);
                Assert.IsNotNull(scenario, scenarioId);
                assertion(scenario, new BenchmarkEnvironment(settings));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }
    }
}
#endif
#endif
