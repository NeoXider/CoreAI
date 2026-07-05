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
            StringAssert.Contains("action", tool.ParametersSchema);
            Assert.IsFalse(tool.ParametersSchema.Contains("move"));
            Assert.IsFalse(tool.ParametersSchema.Contains("update_score"));
            Assert.IsFalse(tool.ParametersSchema.Contains("spawn_particles"));
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

        #endregion
    }
}