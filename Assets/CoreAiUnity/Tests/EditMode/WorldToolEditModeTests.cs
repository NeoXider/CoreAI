using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.World;
using CoreAI.Messaging;
using Microsoft.Extensions.AI;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode coverage for WorldTool MEAI function calling over world commands.
    /// </summary>
    [TestFixture]
    public sealed class WorldToolEditModeTests
    {
        #region WorldLlmTool Tests

        [Test]
        public void WorldLlmTool_CreateAIFunction_Basic()
        {
            TestWorldExecutor executor = new();
            WorldLlmTool tool = new(executor, UnityEngine.ScriptableObject.CreateInstance<CoreAISettingsAsset>(),
                Infrastructure.Logging.GameLoggerUnscopedFallback.Instance);

            AIFunction function = tool.CreateAIFunction();

            Assert.IsNotNull(function);
            Assert.AreEqual("world_command", function.Name);
        }

        [Test]
        public async Task WorldLlmTool_ExecuteAsync_Spawn_ReturnsSuccess()
        {
            TestWorldExecutor executor = new();
            WorldLlmTool tool = new(executor, UnityEngine.ScriptableObject.CreateInstance<CoreAISettingsAsset>(),
                Infrastructure.Logging.GameLoggerUnscopedFallback.Instance);

            string resultJson = await tool.ExecuteAsync("spawn", prefabKey: "Enemy", targetName: "Enemy_1",
                x: 1f, y: 2f, z: 3f, scaleX: 2f, scaleY: 3f, scaleZ: 4f, stringValue: "Root");
            WorldLlmTool.WorldResult result = JsonConvert.DeserializeObject<WorldLlmTool.WorldResult>(resultJson);
            CoreAiWorldCommandEnvelope envelope =
                JsonConvert.DeserializeObject<CoreAiWorldCommandEnvelope>(executor.LastCommandJson);

            Assert.IsTrue(result.Success);
            Assert.AreEqual("spawn", result.Action);
            Assert.AreEqual("spawn", envelope.action);
            Assert.AreEqual("Enemy", envelope.prefabKeyOrName);
            Assert.AreEqual("Enemy_1", envelope.targetName);
            Assert.AreEqual(2f, envelope.scaleX);
            Assert.AreEqual(3f, envelope.scaleY);
            Assert.AreEqual(4f, envelope.scaleZ);
            Assert.AreEqual("Root", envelope.stringValue);
            Assert.IsFalse(envelope.worldPositionStays,
                "Parented spawns should use local coordinates by default.");
        }

        [Test]
        public async Task WorldLlmTool_ExecuteAsync_Change_ReturnsSuccess()
        {
            TestWorldExecutor executor = new();
            WorldLlmTool tool = new(executor, UnityEngine.ScriptableObject.CreateInstance<CoreAISettingsAsset>(),
                Infrastructure.Logging.GameLoggerUnscopedFallback.Instance);

            string resultJson = await tool.ExecuteAsync("change", targetName: "obj1", x: 0f, fy: 45f,
                scaleY: 2.5f, stringValue: "Root");
            WorldLlmTool.WorldResult result = JsonConvert.DeserializeObject<WorldLlmTool.WorldResult>(resultJson);
            CoreAiWorldCommandEnvelope envelope =
                JsonConvert.DeserializeObject<CoreAiWorldCommandEnvelope>(executor.LastCommandJson);

            Assert.IsTrue(result.Success);
            Assert.AreEqual("change", result.Action);
            Assert.AreEqual("change", envelope.action);
            Assert.AreEqual("obj1", envelope.targetName);
            Assert.IsTrue(envelope.hasX);
            Assert.IsFalse(envelope.hasY);
            Assert.AreEqual(0f, envelope.x);
            Assert.IsTrue(envelope.hasFy);
            Assert.AreEqual(45f, envelope.fy);
            Assert.AreEqual(2.5f, envelope.scaleY);
            Assert.AreEqual("Root", envelope.stringValue);
            Assert.IsFalse(envelope.worldPositionStays);

            await tool.ExecuteAsync("change", targetName: "obj1", stringValue: "OtherRoot",
                worldPositionStays: true);
            envelope = JsonConvert.DeserializeObject<CoreAiWorldCommandEnvelope>(executor.LastCommandJson);
            Assert.IsTrue(envelope.worldPositionStays,
                "The model must be able to request world-space-preserving parenting explicitly.");
        }

        [Test]
        public async Task WorldLlmTool_ExecuteAsync_Destroy_ReturnsSuccess()
        {
            TestWorldExecutor executor = new();
            WorldLlmTool tool = new(executor, UnityEngine.ScriptableObject.CreateInstance<CoreAISettingsAsset>(),
                Infrastructure.Logging.GameLoggerUnscopedFallback.Instance);

            string resultJson = await tool.ExecuteAsync("destroy", targetName: "obj1");
            WorldLlmTool.WorldResult result = JsonConvert.DeserializeObject<WorldLlmTool.WorldResult>(resultJson);

            Assert.IsTrue(result.Success);
            Assert.AreEqual("destroy", result.Action);
            Assert.IsTrue(executor.LastCommandJson.Contains("destroy"));
        }

        [Test]
        public async Task WorldLlmTool_ExecuteAsync_SpawnWithoutPrefab_ReturnsError()
        {
            TestWorldExecutor executor = new();
            WorldLlmTool tool = new(executor, UnityEngine.ScriptableObject.CreateInstance<CoreAISettingsAsset>(),
                Infrastructure.Logging.GameLoggerUnscopedFallback.Instance);

            string resultJson = await tool.ExecuteAsync("spawn", targetName: "obj1", x: 0f, y: 0f, z: 0f);
            WorldLlmTool.WorldResult result = JsonConvert.DeserializeObject<WorldLlmTool.WorldResult>(resultJson);

            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Message.Contains("required") || result.Message.Contains("Missing"));
        }

        [Test]
        public async Task WorldLlmTool_ExecuteAsync_UnknownAction_ReturnsError()
        {
            TestWorldExecutor executor = new();
            WorldLlmTool tool = new(executor, UnityEngine.ScriptableObject.CreateInstance<CoreAISettingsAsset>(),
                new Infrastructure.Logging.NullGameLogger());

            string resultJson = await tool.ExecuteAsync("invalid_action");
            WorldLlmTool.WorldResult result = JsonConvert.DeserializeObject<WorldLlmTool.WorldResult>(resultJson);

            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Message.Contains("Unknown action"));
        }

        [Test]
        public async Task WorldLlmTool_ExecuteAsync_PlayAnimation_ReturnsSuccess()
        {
            TestWorldExecutor executor = new();
            WorldLlmTool tool = new(executor, UnityEngine.ScriptableObject.CreateInstance<CoreAISettingsAsset>(),
                Infrastructure.Logging.GameLoggerUnscopedFallback.Instance);

            string resultJson = await tool.ExecuteAsync("play_animation", targetName: "enemy1", stringValue: "attack");
            WorldLlmTool.WorldResult result = JsonConvert.DeserializeObject<WorldLlmTool.WorldResult>(resultJson);

            Assert.IsTrue(result.Success);
            Assert.AreEqual("play_animation", result.Action);
            Assert.IsTrue(executor.LastCommandJson.Contains("play_animation"));
            Assert.IsTrue(executor.LastCommandJson.Contains("attack"));
        }

        [Test]
        public async Task WorldLlmTool_ExecuteAsync_PlaySound_ReturnsSuccess()
        {
            TestWorldExecutor executor = new();
            WorldLlmTool tool = new(executor, UnityEngine.ScriptableObject.CreateInstance<CoreAISettingsAsset>(),
                Infrastructure.Logging.GameLoggerUnscopedFallback.Instance);

            string resultJson = await tool.ExecuteAsync("play_sound", targetName: "speaker1",
                stringValue: "bell", volume: 0.35f);
            WorldLlmTool.WorldResult result = JsonConvert.DeserializeObject<WorldLlmTool.WorldResult>(resultJson);
            CoreAiWorldCommandEnvelope envelope =
                JsonConvert.DeserializeObject<CoreAiWorldCommandEnvelope>(executor.LastCommandJson);

            Assert.IsTrue(result.Success);
            Assert.AreEqual("play_sound", result.Action);
            Assert.AreEqual("play_sound", envelope.action);
            Assert.AreEqual("speaker1", envelope.targetName);
            Assert.AreEqual("bell", envelope.stringValue);
            Assert.AreEqual(0.35f, envelope.floatValue, 0.0001f);
        }

        [Test]
        public async Task WorldLlmTool_ExecuteAsync_PlaySoundWithoutClip_ReturnsHelpfulError()
        {
            TestWorldExecutor executor = new();
            WorldLlmTool tool = new(executor, UnityEngine.ScriptableObject.CreateInstance<CoreAISettingsAsset>(),
                Infrastructure.Logging.GameLoggerUnscopedFallback.Instance);

            string resultJson = await tool.ExecuteAsync("play_sound", targetName: "speaker1");
            WorldLlmTool.WorldResult result = JsonConvert.DeserializeObject<WorldLlmTool.WorldResult>(resultJson);

            Assert.IsFalse(result.Success);
            Assert.AreEqual("play_sound", result.Action);
            StringAssert.Contains("targetName", result.Message);
            StringAssert.Contains("stringValue", result.Message);
            Assert.IsNull(executor.LastCommandJson,
                "Executor should not run when required audio-command args are missing.");
        }

        [Test]
        public async Task WorldLlmTool_ExecuteAsync_SetVolume_ReturnsSuccess()
        {
            TestWorldExecutor executor = new();
            WorldLlmTool tool = new(executor, UnityEngine.ScriptableObject.CreateInstance<CoreAISettingsAsset>(),
                Infrastructure.Logging.GameLoggerUnscopedFallback.Instance);

            string resultJson = await tool.ExecuteAsync("set_volume", targetName: "speaker1", volume: 0.25f);
            WorldLlmTool.WorldResult result = JsonConvert.DeserializeObject<WorldLlmTool.WorldResult>(resultJson);
            CoreAiWorldCommandEnvelope envelope =
                JsonConvert.DeserializeObject<CoreAiWorldCommandEnvelope>(executor.LastCommandJson);

            Assert.IsTrue(result.Success);
            Assert.AreEqual("set_volume", result.Action);
            Assert.AreEqual("set_volume", envelope.action);
            Assert.AreEqual("speaker1", envelope.targetName);
            Assert.AreEqual(0.25f, envelope.floatValue, 0.0001f);
        }

        [Test]
        public async Task WorldLlmTool_ExecuteAsync_LoadScene_ReturnsSuccess()
        {
            TestWorldExecutor executor = new();
            WorldLlmTool tool = new(executor, UnityEngine.ScriptableObject.CreateInstance<CoreAISettingsAsset>(),
                Infrastructure.Logging.GameLoggerUnscopedFallback.Instance);

            string resultJson = await tool.ExecuteAsync("load_scene", stringValue: "Level2");
            WorldLlmTool.WorldResult result = JsonConvert.DeserializeObject<WorldLlmTool.WorldResult>(resultJson);

            Assert.IsTrue(result.Success);
            Assert.AreEqual("load_scene", result.Action);
            Assert.IsTrue(executor.LastCommandJson.Contains("load_scene"));
            Assert.IsTrue(executor.LastCommandJson.Contains("Level2"));
        }

        [Test]
        public async Task WorldLlmTool_ExecuteAsync_PublicActionSurface_ProducesCommands()
        {
            TestWorldExecutor executor = new();
            WorldLlmTool tool = new(executor, UnityEngine.ScriptableObject.CreateInstance<CoreAISettingsAsset>(),
                Infrastructure.Logging.GameLoggerUnscopedFallback.Instance);

            (string action, System.Func<Task<string>> execute)[] cases =
            {
                ("spawn", () => tool.ExecuteAsync("spawn", prefabKey: "cube", targetName: "Block",
                    x: 1f, y: 2f, z: 3f, fx: 0f, fy: 45f, fz: 0f, scaleX: 2f, scaleY: 1f,
                    scaleZ: 0.5f, stringValue: "Root")),
                ("change", () => tool.ExecuteAsync("change", targetName: "Block", x: 0f, fy: 90f,
                    scaleY: 3f, stringValue: "Root2")),
                ("set_color", () => tool.ExecuteAsync("set_color", targetName: "Block", stringValue: "#3366ff")),
                ("destroy", () => tool.ExecuteAsync("destroy", targetName: "Block")),
                ("load_scene", () => tool.ExecuteAsync("load_scene", stringValue: "Level2")),
                ("reload_scene", () => tool.ExecuteAsync("reload_scene")),
                ("set_active", () => tool.ExecuteAsync("set_active", targetName: "Block")),
                ("play_animation", () => tool.ExecuteAsync("play_animation", targetName: "Block",
                    animationName: "Idle")),
                ("stop_animation", () => tool.ExecuteAsync("stop_animation", targetName: "Block")),
                ("list_animations", () => tool.ExecuteAsync("list_animations", targetName: "Block")),
                ("play_sound", () => tool.ExecuteAsync("play_sound", targetName: "Speaker", stringValue: "ping",
                    volume: 0.4f)),
                ("set_volume", () => tool.ExecuteAsync("set_volume", targetName: "Speaker", volume: 0.25f)),
                ("show_text", () => tool.ExecuteAsync("show_text", targetName: "Panel", textToDisplay: "Ready")),
                ("hide_panel", () => tool.ExecuteAsync("hide_panel", targetName: "Panel")),
                ("apply_force", () => tool.ExecuteAsync("apply_force", targetName: "Block", fx: 1f, fy: 2f,
                    fz: 3f)),
                ("set_velocity", () => tool.ExecuteAsync("set_velocity", targetName: "Block", fx: 4f, fy: 5f,
                    fz: 6f)),
                ("list_objects", () => tool.ExecuteAsync("list_objects", stringValue: "Block"))
            };

            foreach ((string action, System.Func<Task<string>> execute) in cases)
            {
                string resultJson = await execute();
                WorldLlmTool.WorldResult result =
                    JsonConvert.DeserializeObject<WorldLlmTool.WorldResult>(resultJson);
                CoreAiWorldCommandEnvelope envelope =
                    JsonConvert.DeserializeObject<CoreAiWorldCommandEnvelope>(executor.LastCommandJson);

                Assert.IsTrue(result.Success, $"{action}: {result.Message}");
                Assert.AreEqual(action, result.Action);
                Assert.IsNotNull(envelope, action);
                Assert.AreEqual(action, envelope.action, action);
            }
        }

        [Test]
        public async Task WorldLlmTool_ExecuteAsync_ListObjects_ReturnsSuccess()
        {
            TestWorldExecutor executor = new();
            WorldLlmTool tool = new(executor, UnityEngine.ScriptableObject.CreateInstance<CoreAISettingsAsset>(),
                Infrastructure.Logging.GameLoggerUnscopedFallback.Instance);

            string resultJson = await tool.ExecuteAsync("list_objects");
            WorldLlmTool.WorldResult result = JsonConvert.DeserializeObject<WorldLlmTool.WorldResult>(resultJson);

            Assert.IsTrue(result.Success);
            Assert.AreEqual("list_objects", result.Action);
            Assert.IsTrue(executor.LastCommandJson.Contains("list_objects"));
        }

        [Test]
        public async Task WorldLlmTool_ExecuteAsync_ListAnimations_ReturnsSuccess()
        {
            TestWorldExecutor executor = new();
            WorldLlmTool tool = new(executor, UnityEngine.ScriptableObject.CreateInstance<CoreAISettingsAsset>(),
                Infrastructure.Logging.GameLoggerUnscopedFallback.Instance);

            string resultJson = await tool.ExecuteAsync("list_animations", targetName: "enemy1");
            WorldLlmTool.WorldResult result = JsonConvert.DeserializeObject<WorldLlmTool.WorldResult>(resultJson);

            Assert.IsTrue(result.Success);
            Assert.AreEqual("list_animations", result.Action);
            Assert.IsTrue(executor.LastCommandJson.Contains("list_animations"));
            Assert.IsTrue(executor.LastCommandJson.Contains("enemy1"));
        }

        [Test]
        public async Task WorldLlmTool_ExecuteAsync_ListAnimationsWithoutTarget_ReturnsHelpfulError()
        {
            TestWorldExecutor executor = new();
            WorldLlmTool tool = new(executor, UnityEngine.ScriptableObject.CreateInstance<CoreAISettingsAsset>(),
                Infrastructure.Logging.GameLoggerUnscopedFallback.Instance);

            string resultJson = await tool.ExecuteAsync("list_animations");
            WorldLlmTool.WorldResult result = JsonConvert.DeserializeObject<WorldLlmTool.WorldResult>(resultJson);

            Assert.IsFalse(result.Success);
            Assert.AreEqual("list_animations", result.Action);
            StringAssert.Contains("targetName", result.Message);
            Assert.IsNull(executor.LastCommandJson,
                "Executor should not run when required world-command args are missing.");
        }

        [Test]
        public async Task WorldLlmTool_ExecuteAsync_DoesNotForceThreadPoolExecution()
        {
            TestWorldExecutor executor = new();
            WorldLlmTool tool = new(executor, UnityEngine.ScriptableObject.CreateInstance<CoreAISettingsAsset>(),
                Infrastructure.Logging.GameLoggerUnscopedFallback.Instance);
            int callerThreadId = Thread.CurrentThread.ManagedThreadId;

            string resultJson = await tool.ExecuteAsync("list_objects");
            WorldLlmTool.WorldResult result = JsonConvert.DeserializeObject<WorldLlmTool.WorldResult>(resultJson);

            Assert.IsTrue(result.Success);
            Assert.AreEqual(callerThreadId, executor.LastThreadId,
                "WorldLlmTool should not force ICoreAiWorldCommandExecutor.TryExecute onto ThreadPool; Unity world executors require the caller/main thread.");
        }

        [Test]
        public async Task WorldLlmTool_ExecuteAsync_LegacyMove_ReturnsUnknownAction()
        {
            TestWorldExecutor executor = new();
            WorldLlmTool tool = new(executor, UnityEngine.ScriptableObject.CreateInstance<CoreAISettingsAsset>(),
                new Infrastructure.Logging.NullGameLogger());

            string resultJson = await tool.ExecuteAsync("move", targetName: "Player", x: 10f, y: 20f, z: 30f);
            WorldLlmTool.WorldResult result = JsonConvert.DeserializeObject<WorldLlmTool.WorldResult>(resultJson);

            Assert.IsFalse(result.Success);
            StringAssert.Contains("Unknown action", result.Message);
            Assert.IsNull(executor.LastCommandJson);
        }

        [Test]
        public async Task WorldLlmTool_ExecuteAsync_DestroyWithTargetName_IncludesTargetName()
        {
            TestWorldExecutor executor = new();
            WorldLlmTool tool = new(executor, UnityEngine.ScriptableObject.CreateInstance<CoreAISettingsAsset>(),
                Infrastructure.Logging.GameLoggerUnscopedFallback.Instance);

            string resultJson = await tool.ExecuteAsync("destroy", targetName: "Enemy");
            WorldLlmTool.WorldResult result = JsonConvert.DeserializeObject<WorldLlmTool.WorldResult>(resultJson);

            Assert.IsTrue(result.Success);
            Assert.AreEqual("destroy", result.Action);
            Assert.IsTrue(executor.LastCommandJson.Contains("destroy"));
            Assert.IsTrue(executor.LastCommandJson.Contains("Enemy"));
        }

        #endregion

        #region WorldLlmTool Tests

        [Test]
        public void WorldLlmTool_CreateAIFunction_ReturnsNonNull()
        {
            TestWorldExecutor executor = new();
            WorldLlmTool tool = new(executor, UnityEngine.ScriptableObject.CreateInstance<CoreAISettingsAsset>(),
                Infrastructure.Logging.GameLoggerUnscopedFallback.Instance);

            AIFunction function = tool.CreateAIFunction();

            Assert.IsNotNull(function);
            Assert.AreEqual("world_command", function.Name);
            StringAssert.Contains("world", function.Description.ToLowerInvariant());
        }

        [Test]
        public void WorldLlmTool_Properties_AreValid()
        {
            TestWorldExecutor executor = new();
            WorldLlmTool tool = new(executor, UnityEngine.ScriptableObject.CreateInstance<CoreAISettingsAsset>(),
                Infrastructure.Logging.GameLoggerUnscopedFallback.Instance);

            Assert.AreEqual("world_command", tool.Name);
            StringAssert.Contains("spawn", tool.Description);
            StringAssert.Contains("change", tool.Description);
            StringAssert.Contains("set_color", tool.Description);
            StringAssert.Contains("destroy", tool.Description);
            StringAssert.Contains("worldPositionStays", tool.Description);
            StringAssert.Contains("empty root", tool.Description);
            StringAssert.Contains("meaningful hierarchy", tool.Description);
            StringAssert.Contains("action", tool.ParametersSchema);
            StringAssert.Contains("worldPositionStays", tool.ParametersSchema);
            Assert.IsFalse(tool.ParametersSchema.Contains("move"));
            Assert.IsFalse(tool.ParametersSchema.Contains("update_score"));
            Assert.IsFalse(tool.ParametersSchema.Contains("spawn_particles"));
        }

        #endregion

        #region SpawnBatch And ListPrefabs Tests

        [Test]
        public async Task WorldLlmTool_SpawnBatch_SpawnsAllItemsWithParentAndColor_ReturnsCompactResult()
        {
            CoreAiWorldCommandExecutor executor = new(Infrastructure.Logging.GameLoggerUnscopedFallback.Instance);
            WorldLlmTool tool = new(executor, UnityEngine.ScriptableObject.CreateInstance<CoreAISettingsAsset>(),
                Infrastructure.Logging.GameLoggerUnscopedFallback.Instance);
            string id = Guid.NewGuid().ToString("N");
            GameObject parent = new($"BatchParent_{id}");
            parent.transform.position = new Vector3(10f, 0f, 0f);

            try
            {
                string itemsJson = JsonConvert.SerializeObject(new object[]
                {
                    new { name = $"Coin_{id}_1", x = 1f, y = 0f, z = 0f, color = "#ff0000" },
                    new { name = $"Coin_{id}_2", x = 2f, y = 0f, z = 0f, worldPositionStays = true,
                        color = "#00ff00" },
                    new { name = $"Coin_{id}_3", x = 3f, y = 0f, z = 0f }
                });

                string resultJson = await tool.ExecuteAsync("spawn_batch", prefabKey: "cube",
                    stringValue: parent.name, itemsJson: itemsJson);
                WorldLlmTool.WorldResult result = JsonConvert.DeserializeObject<WorldLlmTool.WorldResult>(resultJson);

                Assert.IsTrue(result.Success, result.Message);
                Assert.AreEqual("spawn_batch", result.Action);

                SpawnBatchResultDto batch = JsonConvert.DeserializeObject<SpawnBatchResultDto>(result.Message);
                Assert.IsTrue(batch.Ok);
                Assert.AreEqual(3, batch.Spawned);
                Assert.AreEqual(0, batch.Failed);
                Assert.AreEqual(3, batch.Names.Count);
                StringAssert.DoesNotContain("\"x\":", result.Message,
                    "spawn_batch result must be a compact summary, not an echo of every item.");

                GameObject first = GameObject.Find($"Coin_{id}_1");
                Assert.IsNotNull(first);
                Assert.AreEqual(parent.transform, first.transform.parent);
                Assert.AreEqual(1f, first.transform.localPosition.x, 0.001f,
                    "Batch items should use parent-local coordinates by default.");
                Assert.IsTrue(first.GetComponent<Renderer>().HasPropertyBlock(),
                    "Per-item color should apply a MaterialPropertyBlock.");

                GameObject second = GameObject.Find($"Coin_{id}_2");
                Assert.IsNotNull(second);
                Assert.AreEqual(2f, second.transform.position.x, 0.001f,
                    "A per-item worldPositionStays=true override should preserve world coordinates.");

                GameObject third = GameObject.Find($"Coin_{id}_3");
                Assert.IsNotNull(third);
                Assert.AreEqual(parent.transform, third.transform.parent);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(parent);
            }
        }

        [Test]
        public async Task WorldLlmTool_SpawnBatch_PartialUnknownPrefabKey_CountsFailuresWithoutAborting()
        {
            CoreAiWorldCommandExecutor executor = new(Infrastructure.Logging.GameLoggerUnscopedFallback.Instance);
            WorldLlmTool tool = new(executor, UnityEngine.ScriptableObject.CreateInstance<CoreAISettingsAsset>(),
                Infrastructure.Logging.GameLoggerUnscopedFallback.Instance);
            string id = Guid.NewGuid().ToString("N");

            try
            {
                string itemsJson = JsonConvert.SerializeObject(new object[]
                {
                    new { name = $"Good_{id}_1", x = 0f, y = 0f, z = 0f, prefabKey = "cube" },
                    new { name = $"Bad_{id}", x = 1f, y = 0f, z = 0f, prefabKey = "no_such_prefab_key" },
                    new { name = $"Good_{id}_2", x = 2f, y = 0f, z = 0f, prefabKey = "sphere" }
                });

                string resultJson = await tool.ExecuteAsync("spawn_batch", itemsJson: itemsJson);
                WorldLlmTool.WorldResult result = JsonConvert.DeserializeObject<WorldLlmTool.WorldResult>(resultJson);
                SpawnBatchResultDto batch = JsonConvert.DeserializeObject<SpawnBatchResultDto>(result.Message);

                Assert.IsTrue(result.Success);
                Assert.AreEqual(2, batch.Spawned);
                Assert.AreEqual(1, batch.Failed);
                Assert.IsNotNull(GameObject.Find($"Good_{id}_1"));
                Assert.IsNotNull(GameObject.Find($"Good_{id}_2"));
                Assert.IsNull(GameObject.Find($"Bad_{id}"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(GameObject.Find($"Good_{id}_1"));
                UnityEngine.Object.DestroyImmediate(GameObject.Find($"Good_{id}_2"));
            }
        }

        [Test]
        public async Task WorldLlmTool_Spawn_UnknownPrefabKey_ListsAvailableKeysForSelfCorrection()
        {
            StubPrefabRegistry registry = new();
            GameObject heroSource = new("HeroSource");
            GameObject treeSource = new("TreeSource");

            try
            {
                registry.Add("Hero", heroSource);
                registry.Add("Tree", treeSource);

                CoreAiWorldCommandExecutor executor =
                    new(Infrastructure.Logging.GameLoggerUnscopedFallback.Instance, registry);
                WorldLlmTool tool = new(executor, UnityEngine.ScriptableObject.CreateInstance<CoreAISettingsAsset>(),
                    Infrastructure.Logging.GameLoggerUnscopedFallback.Instance);

                string resultJson = await tool.ExecuteAsync("spawn", prefabKey: "totally_unknown_key",
                    targetName: "X", x: 0f, y: 0f, z: 0f);
                WorldLlmTool.WorldResult result = JsonConvert.DeserializeObject<WorldLlmTool.WorldResult>(resultJson);

                Assert.IsFalse(result.Success);
                StringAssert.Contains("Unknown prefabKey", result.Message);
                StringAssert.Contains("totally_unknown_key", result.Message);
                StringAssert.Contains("Hero", result.Message);
                StringAssert.Contains("Tree", result.Message);
                StringAssert.Contains("cube", result.Message);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(heroSource);
                UnityEngine.Object.DestroyImmediate(treeSource);
            }
        }

        [Test]
        public async Task WorldLlmTool_Spawn_UnknownPrefabKey_TruncatesLongRegistryList()
        {
            StubPrefabRegistry registry = new();
            List<GameObject> sources = new();

            try
            {
                for (int i = 0; i < 25; i++)
                {
                    GameObject source = new($"Prefab_{i}");
                    sources.Add(source);
                    registry.Add($"Prefab_{i}", source);
                }

                CoreAiWorldCommandExecutor executor =
                    new(Infrastructure.Logging.GameLoggerUnscopedFallback.Instance, registry);
                WorldLlmTool tool = new(executor, UnityEngine.ScriptableObject.CreateInstance<CoreAISettingsAsset>(),
                    Infrastructure.Logging.GameLoggerUnscopedFallback.Instance);

                string resultJson = await tool.ExecuteAsync("spawn", prefabKey: "nope", targetName: "X",
                    x: 0f, y: 0f, z: 0f);
                WorldLlmTool.WorldResult result = JsonConvert.DeserializeObject<WorldLlmTool.WorldResult>(resultJson);

                Assert.IsFalse(result.Success);
                StringAssert.Contains("+5 more", result.Message);
            }
            finally
            {
                foreach (GameObject source in sources)
                {
                    UnityEngine.Object.DestroyImmediate(source);
                }
            }
        }

        [Test]
        public async Task WorldLlmTool_ListPrefabs_ReturnsRegistryKeysAndPrimitives()
        {
            StubPrefabRegistry registry = new();
            GameObject heroSource = new("HeroSource2");

            try
            {
                registry.Add("Hero", heroSource);

                CoreAiWorldCommandExecutor executor =
                    new(Infrastructure.Logging.GameLoggerUnscopedFallback.Instance, registry);
                WorldLlmTool tool = new(executor, UnityEngine.ScriptableObject.CreateInstance<CoreAISettingsAsset>(),
                    Infrastructure.Logging.GameLoggerUnscopedFallback.Instance);

                string resultJson = await tool.ExecuteAsync("list_prefabs");
                WorldLlmTool.WorldResult result = JsonConvert.DeserializeObject<WorldLlmTool.WorldResult>(resultJson);

                Assert.IsTrue(result.Success);
                Assert.AreEqual("list_prefabs", result.Action);

                ListPrefabsResultDto listed = JsonConvert.DeserializeObject<ListPrefabsResultDto>(result.Message);
                CollectionAssert.Contains(listed.Prefabs, "Hero");
                CollectionAssert.Contains(listed.Primitives, "cube");
                CollectionAssert.Contains(listed.Primitives, "sphere");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(heroSource);
            }
        }

        #endregion

        #region Test Helpers

        private sealed class TestWorldExecutor : ICoreAiWorldCommandExecutor
        {
            public string LastCommandJson;
            public int LastThreadId;

            public string[] LastListedAnimations { get; private set; } = System.Array.Empty<string>();
            public List<Dictionary<string, object>> LastListedObjects { get; private set; } = new();

            public bool TryExecute(ApplyAiGameCommand cmd)
            {
                LastThreadId = Thread.CurrentThread.ManagedThreadId;
                LastCommandJson = cmd.JsonPayload;
                return true; // Keep execution synchronous for deterministic editor-only tests.
            }
        }

        /// <summary>Minimal in-memory prefab registry/catalog stand-in for spawn_batch/list_prefabs tests.</summary>
        private sealed class StubPrefabRegistry : ICoreAiPrefabRegistry, ICoreAiPrefabCatalog
        {
            private readonly Dictionary<string, GameObject> _prefabs = new();

            public void Add(string key, GameObject prefab)
            {
                _prefabs[key] = prefab;
            }

            public bool TryResolve(string keyOrName, out GameObject prefab)
            {
                return _prefabs.TryGetValue(keyOrName, out prefab);
            }

            public IReadOnlyList<string> ListPrefabKeys()
            {
                return new List<string>(_prefabs.Keys);
            }
        }

        private sealed class SpawnBatchResultDto
        {
            public bool Ok { get; set; }
            public int Spawned { get; set; }
            public int Failed { get; set; }
            public List<string> Names { get; set; } = new();
        }

        private sealed class ListPrefabsResultDto
        {
            public List<string> Prefabs { get; set; } = new();
            public List<string> Primitives { get; set; } = new();
        }

        #endregion
    }
}
