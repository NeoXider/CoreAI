#if COREAI_LUA
#if COREAI_LLM && !UNITY_WEBGL
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

        /// <summary>
        /// A batch is graded as its items: one <c>spawn</c> each, named exactly as the production
        /// executor names them, so batch-spawned objects reach <c>Count("spawn")</c> and the spawn graders.
        /// </summary>
        [Test]
        public void SpawnBatch_RecordsOneSpawnPerItem_NamedLikeTheProductionExecutor()
        {
            RecordingWorldExecutor executor = new();
            Execute(executor, new CoreAiWorldCommandEnvelope
            {
                action = "spawn_batch",
                prefabKeyOrName = "Cube",
                items = new[]
                {
                    new CoreAiSpawnBatchItem { name = "Keep", x = 1f },
                    new CoreAiSpawnBatchItem { x = 2f },
                    new CoreAiSpawnBatchItem { prefabKey = "Sphere", x = 3f }
                }
            });
            Execute(executor, new CoreAiWorldCommandEnvelope
            {
                action = "spawn_batch",
                targetName = "Wall",
                prefabKeyOrName = "Cube",
                items = new[] { new CoreAiSpawnBatchItem { x = 4f } }
            });

            Assert.AreEqual(4, executor.Count("spawn"));
            Assert.AreEqual(0, executor.Count("spawn_batch"));
            Assert.AreEqual(0, executor.InvalidCommandCount);
            Assert.AreEqual("Keep", executor.Commands[0].TargetName);
            Assert.AreEqual("Cube_2", executor.Commands[1].TargetName);
            Assert.AreEqual("Sphere_3", executor.Commands[2].TargetName);
            Assert.AreEqual("Sphere", executor.Commands[2].PrefabKeyOrName);
            Assert.AreEqual("Wall_1", executor.Commands[3].TargetName);
            Assert.AreEqual(4f, executor.Commands[3].X);
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
            RecordingWorldExecutor executor, CoreAiWorldCommandEnvelope envelope)
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
