#if COREAI_LLM && !UNITY_WEBGL
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
    [Timeout(BuilderAgentCastleBuildLivePlayModeTests.TestTimeoutMs)]
    public sealed class BuilderAgentCastleBuildLivePlayModeTests
    {
        // WHY: 120 s optional GGUF load + one 600 s build turn + 10 s spawn grace = 730 s; 900 s leaves the
        // 20 s LiveTestRequestScope reserve plus margin so the test's own cancelling wait fires first.
        private const int TestTimeoutMs = 900_000;

        private const string CastlePrefix = "Castle";
        private const int MinCastleObjects = 8;

        private const string Prompt =
            "Build a small castle at the origin: four corner towers and four walls connecting them, " +
            "on a stone base. Name every part starting with 'Castle'.";

        private LiveTestRequestScope _requests;
        private PlayModeProductionLikeLlmHandle _handle;
        private CoreAISettingsAsset _orchestratorSettings;
        private HashSet<int> _preExistingCastleIds;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _requests = new LiveTestRequestScope(TestTimeoutMs);
            LogAssert.ignoreFailingMessages = true;
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

            if (_preExistingCastleIds != null)
            {
                DestroyNewCastleObjects(_preExistingCastleIds);
                _preExistingCastleIds = null;
            }

            if (_orchestratorSettings != null)
            {
                UnityEngine.Object.DestroyImmediate(_orchestratorSettings);
                _orchestratorSettings = null;
            }

            _handle?.Dispose();
            _handle = null;
            LogAssert.ignoreFailingMessages = false;
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
        [Timeout(TestTimeoutMs)]
        public IEnumerator Builder_BuildsSmallCastle_FromSingleNaturalLanguagePrompt()
        {
            TestContext.WriteLine("[BuilderCastle] === TEST START ===");

            if (!PlayModeProductionLikeLlmFactory.TryCreate(null, 0.2f, 600,
                    out PlayModeProductionLikeLlmHandle handle, out string ignore))
            {
                Assert.Ignore(ignore);
            }

            // WHY: handle, settings and spawned castle parts are released in [UnityTearDown] after the
            // request drain, so a timed-out turn never runs against a disposed client or destroyed settings.
            _handle = handle;
            _preExistingCastleIds = CollectCastleInstanceIds();
            HashSet<int> preExistingCastleIds = _preExistingCastleIds;

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

            CoreAISettingsAsset orchestratorSettings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            _orchestratorSettings = orchestratorSettings;
            orchestratorSettings.SetOrchestratorTimeoutSeconds(600);

            // WHY: Keep the default Builder prompt and tool policy; the task below bounds this
            // small integration fixture so an over-eager model cannot decorate it for 25 minutes.
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
                orchestratorSettings,
                new LocalActorIdentityProvider("builder-castle-live-test"));

            TestContext.WriteLine($"[BuilderCastle] Prompt: {Prompt}");

            CancellationTokenSource cts = _requests.CreateCancellation();
            Task task = _requests.Track(orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Builder,
                Hint = Prompt,
                MaxOutputTokens = 4096,
                MaxToolCallRoundtrips = 24
            }, cts.Token));

            yield return PlayModeTestAwait.WaitTask(task, _requests.Cap(600f), "Builder castle build", cts);

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
