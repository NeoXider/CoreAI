#if COREAI_LLM && !UNITY_WEBGL
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.World;
using CoreAI.Messaging;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// PlayMode   WorldCommand tool calling   MEAI pipeline.
    ///    CoreAISettingsAsset.
    /// </summary>
    public sealed class WorldCommandPlayModeTests
    {
        // WHY: each test is one 240 s world tool turn + a 5 s flag-sync wait + the 20 s LiveTestRequestScope
        // reserve = 265 s; 360 s leaves ~95 s for setup. TestAgentSetup.Initialize is not capped (SharedLlmUnity
        // allows up to 600 s for a cold GGUF load and 300 s when another test is already loading); the Cap on every
        // wait protects the request, so a slower load shortens the turn or ends in the test's own cancelling wait,
        // never a stranded request.
        private const int TestTimeoutMs = 360000;

        private LiveTestRequestScope _requests;
        private TestAgentSetup _setup;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _requests = new LiveTestRequestScope(TestTimeoutMs);
            yield break;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            // WHY: a framework timeout skips the test body's cleanup; cancel and let the abandoned turn unwind
            // before the client is disposed.
            if (_requests != null)
            {
                yield return _requests.CancelAllAndDrain();
                _requests = null;
            }

            _setup?.Dispose();
            _setup = null;
        }

        [UnityTest]
        [Timeout(TestTimeoutMs)]
        public IEnumerator WorldTool_SpawnObject()
        {
            // WHY: [UnityTearDown] disposes the setup only after the in-flight request is drained.
            TestAgentSetup setup = new();
            _setup = setup;
            yield return setup.Initialize();
            if (!setup.IsReady)
            {
                Assert.Ignore("TestAgentSetup failed");
            }

            Debug.Log($"[WorldTest] Backend: {setup.BackendName}, testing spawn...");

            //  WorldTool   Creator
            List<ILlmTool> tools = new()
            {
                new WorldLlmTool(setup.WorldExecutor, ScriptableObject.CreateInstance<CoreAISettingsAsset>(),
                    GameLoggerUnscopedFallback.Instance)
            };
            setup.Policy.SetToolsForRole(BuiltInAgentRoleIds.Creator, tools);

            CancellationTokenSource cts = _requests.CreateCancellation();
            Task task = _requests.Track(setup.Orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Creator,
                Hint =
                    "Spawn TestPrefab as an object named test_obj at coordinates x=0, y=0, z=0."
            }, cts.Token));

            yield return setup.RunAndWait(task, _requests.Cap(240f), "world spawn", cts);

            //      (      Orchestrator   )
            //      tool execution.
            if (!setup.WorldExecutor.LastCommandWasCalled)
            {
                yield return PlayModeTestAwait.WaitUntil(() => setup.WorldExecutor.LastCommandWasCalled, 5f,
                    "last command flag sync");
            }

            Debug.Log($"[WorldTest] SUCCESS! World command executed!");
            Assert.IsTrue(setup.WorldExecutor.AllCommandsJson.Exists(j => j.Contains("spawn")),
                $"Command spawn not found. Last was: {setup.WorldExecutor.LastCommandJson}");
        }

        [UnityTest]
        [Timeout(TestTimeoutMs)]
        public IEnumerator WorldTool_MoveObject()
        {
            // WHY: [UnityTearDown] disposes the setup only after the in-flight request is drained.
            TestAgentSetup setup = new();
            _setup = setup;
            yield return setup.Initialize();
            if (!setup.IsReady)
            {
                Assert.Ignore("TestAgentSetup failed");
            }

            Debug.Log($"[WorldTest] Backend: {setup.BackendName}, testing move...");

            //  WorldTool   Creator
            List<ILlmTool> tools = new()
            {
                new WorldLlmTool(setup.WorldExecutor, ScriptableObject.CreateInstance<CoreAISettingsAsset>(),
                    GameLoggerUnscopedFallback.Instance)
            };
            setup.Policy.SetToolsForRole(BuiltInAgentRoleIds.Creator, tools);

            CancellationTokenSource cts = _requests.CreateCancellation();
            Task task = _requests.Track(setup.Orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Creator,
                Hint =
                    "Move the target named 'Player' to coordinates x=10, y=20, z=30."
            }, cts.Token));

            yield return setup.RunAndWait(task, _requests.Cap(240f), "world move", cts);

            if (!setup.WorldExecutor.LastCommandWasCalled)
            {
                Debug.LogWarning("[WorldTest] Move command not executed.");
                Assert.Fail("World move failed: tool call was not executed.");
            }

            Debug.Log($"[WorldTest] SUCCESS! Move command executed!");
            // The tool schema advertises 'change' for position updates ('move' is only a legacy executor
            // alias), so a model that emits change with the target coordinates has moved the object.
            Assert.IsTrue(setup.WorldExecutor.AllCommandsJson.Exists(j =>
                    j.Contains("move") || (j.Contains("\"change\"") && j.Contains("\"hasPosition\":true"))),
                $"No move/position-change command found. Last was: {setup.WorldExecutor.LastCommandJson}");
        }

        [UnityTest]
        [Timeout(TestTimeoutMs)]
        public IEnumerator WorldTool_ListObjects()
        {
            // WHY: [UnityTearDown] disposes the setup only after the in-flight request is drained.
            TestAgentSetup setup = new();
            _setup = setup;
            yield return setup.Initialize();
            if (!setup.IsReady)
            {
                Assert.Ignore("TestAgentSetup failed");
            }

            Debug.Log($"[WorldTest] Backend: {setup.BackendName}, testing list_objects...");

            //  WorldTool   Creator
            List<ILlmTool> tools = new()
            {
                new WorldLlmTool(setup.WorldExecutor, ScriptableObject.CreateInstance<CoreAISettingsAsset>(),
                    GameLoggerUnscopedFallback.Instance)
            };
            setup.Policy.SetToolsForRole(BuiltInAgentRoleIds.Creator, tools);

            CancellationTokenSource cts = _requests.CreateCancellation();
            Task task = _requests.Track(setup.Orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Creator,
                Hint =
                    "List all objects currently in the scene."
            }, cts.Token));

            yield return setup.RunAndWait(task, _requests.Cap(240f), "world list_objects", cts);

            if (!setup.WorldExecutor.LastCommandWasCalled)
            {
                Debug.LogWarning("[WorldTest] List objects command not executed.");
                Assert.Fail("World list_objects failed: tool call was not executed.");
            }

            Debug.Log($"[WorldTest] SUCCESS! List objects command executed!");
            Assert.IsTrue(setup.WorldExecutor.AllCommandsJson.Exists(j => j.Contains("list_objects")),
                $"Command list_objects not found. Last was: {setup.WorldExecutor.LastCommandJson}");
        }

        [UnityTest]
        [Timeout(TestTimeoutMs)]
        public IEnumerator WorldTool_PlayAnimation()
        {
            // WHY: [UnityTearDown] disposes the setup only after the in-flight request is drained.
            TestAgentSetup setup = new();
            _setup = setup;
            yield return setup.Initialize();
            if (!setup.IsReady)
            {
                Assert.Ignore("TestAgentSetup failed");
            }

            Debug.Log($"[WorldTest] Backend: {setup.BackendName}, testing play_animation...");

            //  WorldTool   Creator
            List<ILlmTool> tools = new()
            {
                new WorldLlmTool(setup.WorldExecutor, ScriptableObject.CreateInstance<CoreAISettingsAsset>(),
                    GameLoggerUnscopedFallback.Instance)
            };
            setup.Policy.SetToolsForRole(BuiltInAgentRoleIds.Creator, tools);

            CancellationTokenSource cts = _requests.CreateCancellation();
            Task task = _requests.Track(setup.Orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Creator,
                Hint =
                    "Play the 'attack' animation on the target named 'Enemy'."
            }, cts.Token));

            yield return setup.RunAndWait(task, _requests.Cap(240f), "world play_animation", cts);

            if (!setup.WorldExecutor.LastCommandWasCalled)
            {
                yield return PlayModeTestAwait.WaitUntil(() => setup.WorldExecutor.LastCommandWasCalled, 5f,
                    "last command flag sync (play_animation)");
            }

            Debug.Log($"[WorldTest] SUCCESS! Play animation command executed!");
            Assert.IsTrue(setup.WorldExecutor.AllCommandsJson.Exists(j => j.Contains("play_animation")),
                $"Command play_animation not found. Last was: {setup.WorldExecutor.LastCommandJson}");
        }

        [UnityTest]
        [Timeout(TestTimeoutMs)]
        public IEnumerator WorldTool_ListAnimations()
        {
            // WHY: [UnityTearDown] disposes the setup only after the in-flight request is drained.
            TestAgentSetup setup = new();
            _setup = setup;
            yield return setup.Initialize();
            if (!setup.IsReady)
            {
                Assert.Ignore("TestAgentSetup failed");
            }

            Debug.Log($"[WorldTest] Backend: {setup.BackendName}, testing list_animations...");

            //  WorldTool   Creator
            List<ILlmTool> tools = new()
            {
                new WorldLlmTool(setup.WorldExecutor, ScriptableObject.CreateInstance<CoreAISettingsAsset>(),
                    GameLoggerUnscopedFallback.Instance)
            };
            setup.Policy.SetToolsForRole(BuiltInAgentRoleIds.Creator, tools);

            CancellationTokenSource cts = _requests.CreateCancellation();
            Task task = _requests.Track(setup.Orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Creator,
                Hint =
                    "List the available animations for the target named 'Enemy'."
            }, cts.Token));

            yield return setup.RunAndWait(task, _requests.Cap(240f), "world list_animations", cts);

            if (!setup.WorldExecutor.LastCommandWasCalled)
            {
                yield return PlayModeTestAwait.WaitUntil(() => setup.WorldExecutor.LastCommandWasCalled, 5f,
                    "last command flag sync (list)");
            }

            Debug.Log($"[WorldTest] SUCCESS! List animations command executed!");
            Assert.IsTrue(setup.WorldExecutor.AllCommandsJson.Exists(j => j.Contains("list_animations")),
                $"Command list_animations not found in any of the executed commands. Last was: {setup.WorldExecutor.LastCommandJson}");
        }

        [Test]
        public void WorldLlmTool_CanBeCreated()
        {
            CoreAISettingsAsset settings = CoreAISettingsAsset.Instance;
            if (settings == null)
            {
                Assert.Ignore("CoreAISettingsAsset not found");
            }

            IGameLogger logger = GameLoggerUnscopedFallback.Instance;
            TestWorldCommandExecutor worldExecutor = new();
            WorldLlmTool worldTool = new(worldExecutor, settings, logger);

            Assert.IsNotNull(worldTool);
            Assert.AreEqual("world_command", worldTool.Name);
            Assert.IsTrue(worldTool.Description.Contains("spawn"));
            Assert.IsTrue(worldTool.Description.Contains("move"));
        }

        private sealed class TestWorldCommandExecutor : ICoreAiWorldCommandExecutor
        {
            public volatile bool LastCommandWasCalled;
            public string LastCommandJson;
            public List<string> AllCommandsJson = new();

            public string[] LastListedAnimations { get; private set; } = System.Array.Empty<string>();
            public List<Dictionary<string, object>> LastListedObjects { get; private set; } = new();

            public bool TryExecute(ApplyAiGameCommand cmd)
            {
                LastCommandWasCalled = true;
                LastCommandJson = cmd.JsonPayload;
                AllCommandsJson.Add(cmd.JsonPayload);

                //    
                if (cmd.JsonPayload != null)
                {
                    if (cmd.JsonPayload.Contains("list_animations"))
                    {
                        LastListedAnimations = new[] { "attack", "idle" };
                    }

                    if (cmd.JsonPayload.Contains("list_objects"))
                    {
                        LastListedObjects = new List<Dictionary<string, object>> { new() { { "name", "Enemy" } } };
                    }
                }

                return true;
            }
        }
    }
}
#endif
