#if !COREAI_NO_LLM && !UNITY_WEBGL
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.World;
using CoreAI.Messaging;
using CoreAI.Session;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Live end-to-end gate for the built-in <see cref="BuiltInAgentRoleIds.Builder"/> role:
    /// one natural-language prompt goes through the production <see cref="AiOrchestrator"/> pipeline
    /// (built-in Builder system prompt, real LLM, real <c>world_command</c> tool over the production
    /// <see cref="CoreAiWorldCommandExecutor"/>) and must leave at least 8 "Castle*" objects in the scene.
    /// Self-skips via <see cref="Assert.Ignore(string)"/> when no live backend is configured
    /// (COREAI_TEST_BASE_URL / COREAI_TEST_MODEL or a configured CoreAISettingsAsset).
    /// </summary>
    [Explicit("Live LLM required: configure COREAI_TEST_BASE_URL / COREAI_TEST_MODEL (or CoreAISettingsAsset).")]
    [Category("LiveLlm")]
    [Timeout(1_800_000)]
    public sealed class BuilderAgentCastleBuildLivePlayModeTests
    {
        private const string CastlePrefix = "Castle";
        private const int MinCastleObjects = 8;

        private const string Prompt =
            "Build a small castle at the origin: four corner towers and four walls connecting them, " +
            "on a stone base. Name every part starting with 'Castle'.";

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            LogAssert.ignoreFailingMessages = true;
            yield break;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            LogAssert.ignoreFailingMessages = false;
            yield break;
        }

        /// <summary>
        /// Pass-through decorator over the PRODUCTION world executor that records every command payload,
        /// so the transcript can prove the model reached the real tool. Execution itself is untouched.
        /// </summary>
        private sealed class RecordingWorldExecutor : ICoreAiWorldCommandExecutor
        {
            private readonly ICoreAiWorldCommandExecutor _inner;
            private readonly object _lock = new();
            private readonly List<string> _payloads = new();

            public RecordingWorldExecutor(ICoreAiWorldCommandExecutor inner)
            {
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            }

            public IReadOnlyList<string> Payloads
            {
                get
                {
                    lock (_lock)
                    {
                        return _payloads.ToArray();
                    }
                }
            }

            public bool TryExecute(ApplyAiGameCommand cmd)
            {
                lock (_lock)
                {
                    _payloads.Add(cmd.JsonPayload ?? "");
                }

                return _inner.TryExecute(cmd);
            }

            public string[] LastListedAnimations => _inner.LastListedAnimations;

            public List<Dictionary<string, object>> LastListedObjects => _inner.LastListedObjects;

            // WHY: Forward the default-implemented members too; otherwise the decorator's interface defaults
            // (empty values) would mask the real executor's prefab lists and error details from the model.
            public IReadOnlyList<string> LastListedPrefabKeys => _inner.LastListedPrefabKeys;

            public string LastErrorMessage => _inner.LastErrorMessage;

            public CoreAiSpawnBatchResult LastSpawnBatchResult => _inner.LastSpawnBatchResult;
        }

        [UnityTest]
        [Timeout(1_800_000)]
        public IEnumerator Builder_BuildsSmallCastle_FromSingleNaturalLanguagePrompt()
        {
            TestContext.WriteLine("[BuilderCastle] === TEST START ===");

            if (!PlayModeProductionLikeLlmFactory.TryCreate(null, 0.2f, 600,
                    out PlayModeProductionLikeLlmHandle handle, out string ignore))
            {
                Assert.Ignore(ignore);
            }

            CoreAISettingsAsset orchestratorSettings = null;
            HashSet<int> preExistingCastleIds = CollectCastleInstanceIds();

            try
            {
                if (handle.ResolvedBackend == PlayModeProductionLikeLlmBackend.LlmUnity)
                {
                    yield return PlayModeProductionLikeLlmFactory.EnsureLlmUnityModelReady(handle);
                }

                TestContext.WriteLine($"[BuilderCastle] Backend: {handle.ResolvedBackend}");

                InMemoryStore store = new();
                ILlmClient client = handle.WrapWithMemoryStore(store);

                // WHY: The production world pipeline: WorldLlmTool -> CoreAiWorldCommandExecutor spawns
                // REAL GameObjects (primitives allowed, no registry needed), same as WorldCommandsInstaller
                // wires for the in-game chat and DirectorAi paths.
                RecordingWorldExecutor worldExecutor = new(
                    new CoreAiWorldCommandExecutor(GameLoggerUnscopedFallback.Instance, null,
                        allowPrimitives: true));

                orchestratorSettings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
                orchestratorSettings.SetOrchestratorTimeoutSeconds(600);

                // WHY: Keep the DEFAULT AgentMemoryPolicy so the Builder role retains its production
                // configuration (built-in system prompt routing, MaxToolCallRoundtrips = 0 = unlimited).
                // Only the world tool is attached, exactly like production hosts do per role.
                AgentMemoryPolicy policy = new();
                policy.SetToolsForRole(BuiltInAgentRoleIds.Builder, new List<ILlmTool>
                {
                    new WorldLlmTool(worldExecutor, orchestratorSettings, GameLoggerUnscopedFallback.Instance)
                });

                BuiltInDefaultAgentSystemPromptProvider systemPrompts = new();
                AiPromptComposer composer = new(
                    systemPrompts,
                    new NoAgentUserPromptTemplateProvider(),
                    new NullLuaScriptVersionStore());

                AiOrchestrator orchestrator = new(
                    new SoloAuthorityHost(),
                    client,
                    new NullSink(),
                    new SessionTelemetryCollector(),
                    composer,
                    store,
                    policy,
                    new CompositeRoleStructuredResponsePolicy(),
                    new NullAiOrchestrationMetrics(),
                    orchestratorSettings);

                TestContext.WriteLine($"[BuilderCastle] Prompt: {Prompt}");

                using CancellationTokenSource cts = new();
                Task task = orchestrator.RunTaskAsync(new AiTaskRequest
                {
                    RoleId = BuiltInAgentRoleIds.Builder,
                    Hint = Prompt,
                    MaxOutputTokens = 128000
                }, cts.Token);

                yield return PlayModeTestAwait.WaitTask(task, 1500f, "Builder castle build", cts);

                // WHY: Tool execution hops to the main thread; give late spawns a short NON-FAILING grace
                // window (PlayModeTestAwait.WaitUntil would Assert.Fail before the transcript is logged).
                float graceStarted = Time.realtimeSinceStartup;
                while (CountNewCastleObjects(preExistingCastleIds, out _) < MinCastleObjects &&
                       Time.realtimeSinceStartup - graceStarted < 10f)
                {
                    yield return null;
                }

                IReadOnlyList<string> commands = worldExecutor.Payloads;
                int newCastleCount = CountNewCastleObjects(preExistingCastleIds, out List<string> castleNames);

                TestContext.WriteLine("[BuilderCastle] ---------- TRANSCRIPT ----------");
                TestContext.WriteLine($"[BuilderCastle] World tool calls executed: {commands.Count}");
                for (int i = 0; i < commands.Count; i++)
                {
                    string payload = commands[i];
                    TestContext.WriteLine(
                        $"[BuilderCastle]   #{i + 1}: {payload.Substring(0, Math.Min(220, payload.Length))}");
                }

                TestContext.WriteLine($"[BuilderCastle] Castle objects found ({newCastleCount}): " +
                                      string.Join(", ", castleNames));
                TestContext.WriteLine("[BuilderCastle] --------------------------------");

                Assert.IsTrue(commands.Count > 0,
                    "Builder must reach the real world_command tool at least once; no commands were executed.");
                Assert.GreaterOrEqual(newCastleCount, MinCastleObjects,
                    $"Expected at least {MinCastleObjects} new scene objects named '{CastlePrefix}*' after the build. " +
                    $"Found {newCastleCount}: [{string.Join(", ", castleNames)}]. " +
                    $"Tool calls executed: {commands.Count}.");

                TestContext.WriteLine("[BuilderCastle] TEST PASSED");
            }
            finally
            {
                DestroyNewCastleObjects(preExistingCastleIds);
                if (orchestratorSettings != null)
                {
                    UnityEngine.Object.DestroyImmediate(orchestratorSettings);
                }

                handle.Dispose();
            }
        }

        private static HashSet<int> CollectCastleInstanceIds()
        {
            HashSet<int> ids = new();
            foreach (Transform tr in UnityEngine.Object.FindObjectsByType<Transform>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (tr != null && tr.name.StartsWith(CastlePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    ids.Add(tr.gameObject.GetInstanceID());
                }
            }

            return ids;
        }

        private static int CountNewCastleObjects(HashSet<int> preExistingIds, out List<string> names)
        {
            names = new List<string>();
            foreach (Transform tr in UnityEngine.Object.FindObjectsByType<Transform>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (tr == null || !tr.name.StartsWith(CastlePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!preExistingIds.Contains(tr.gameObject.GetInstanceID()))
                {
                    names.Add(tr.name);
                }
            }

            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names.Count;
        }

        private static void DestroyNewCastleObjects(HashSet<int> preExistingIds)
        {
            foreach (Transform tr in UnityEngine.Object.FindObjectsByType<Transform>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (tr == null || tr.parent != null)
                {
                    continue;
                }

                if (tr.name.StartsWith(CastlePrefix, StringComparison.OrdinalIgnoreCase) &&
                    !preExistingIds.Contains(tr.gameObject.GetInstanceID()))
                {
                    UnityEngine.Object.Destroy(tr.gameObject);
                }
            }
        }

        private sealed class NullSink : IAiGameCommandSink
        {
            public void Publish(ApplyAiGameCommand command)
            {
            }
        }
    }
}
#endif
