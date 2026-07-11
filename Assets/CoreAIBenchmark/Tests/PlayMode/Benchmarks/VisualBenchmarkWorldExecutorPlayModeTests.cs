#if !COREAI_NO_LUA
#if !COREAI_NO_LLM && !UNITY_WEBGL
using CoreAI.Ai;
using CoreAI.Infrastructure.World;
using CoreAI.Messaging;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;
using static CoreAI.Tests.PlayMode.Benchmarks.GameCreationBenchmarkHarness;

namespace CoreAI.Tests.PlayMode.Benchmarks
{
    public sealed class VisualBenchmarkWorldExecutorPlayModeTests
    {
        [Test]
        public void SpawnAndChange_HonorParentCoordinateSpace()
        {
            VisualBenchmarkWorldExecutor executor = new() { HideLabels = true };
            try
            {
                Execute(executor, Spawn("group", "empty", 10f, 0f, 0f));
                Execute(executor, Spawn("local", "cube", 1f, 2f, 3f, "group"));
                Execute(executor, Spawn("world", "cube", 2f, 3f, 4f, "group", true));

                Transform group = executor.Root.Find("group");
                Transform local = group.Find("local");
                Transform world = group.Find("world");
                Assert.That(local.localPosition, Is.EqualTo(new Vector3(1f, 2f, 3f)));
                Assert.That(world.position, Is.EqualTo(new Vector3(2f, 3f, 4f)));

                CoreAiWorldCommandEnvelope change = new()
                {
                    action = "change",
                    targetName = "world",
                    stringValue = "group",
                    x = 4f,
                    y = 5f,
                    z = 6f,
                    hasPosition = true
                };
                Execute(executor, change);

                Assert.That(world.localPosition, Is.EqualTo(new Vector3(4f, 5f, 6f)));
            }
            finally
            {
                executor.Cleanup();
            }
        }

        private static CoreAiWorldCommandEnvelope Spawn(
            string name, string prefab, float x, float y, float z,
            string parent = "", bool worldPositionStays = false)
        {
            return new CoreAiWorldCommandEnvelope
            {
                action = "spawn",
                targetName = name,
                prefabKeyOrName = prefab,
                stringValue = parent,
                worldPositionStays = worldPositionStays,
                x = x,
                y = y,
                z = z,
                hasPosition = true
            };
        }

        private static void Execute(
            VisualBenchmarkWorldExecutor executor, CoreAiWorldCommandEnvelope envelope)
        {
            Assert.IsTrue(executor.TryExecute(new ApplyAiGameCommand
            {
                CommandTypeId = AiGameCommandTypeIds.WorldCommand,
                JsonPayload = JsonConvert.SerializeObject(envelope)
            }));
        }
    }
}
#endif
#endif
