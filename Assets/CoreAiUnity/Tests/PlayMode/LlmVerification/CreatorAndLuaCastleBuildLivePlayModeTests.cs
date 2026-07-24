#if !COREAI_NO_LLM && !UNITY_WEBGL
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.World;
using CoreAI.Logging;
using CoreAI.Messaging;
using CoreAI.Session;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Live end-to-end castle gates that complement <see cref="BuilderAgentCastleBuildLivePlayModeTests"/>
    /// by exercising the two OTHER production paths a maintainer asked to verify:
    /// <list type="bullet">
    /// <item>the built-in <see cref="BuiltInAgentRoleIds.Creator"/> role building a castle through the real
    /// <c>world_command</c> tool (Creator's system prompt forbids emitting code, so it must place objects itself);</item>
    /// <item>the built-in <see cref="BuiltInAgentRoleIds.Programmer"/> role building a castle through LUA — the
    /// model writes Lua that calls the <c>coreai_world_spawn</c> world API; the emitted world commands are
    /// replayed on the main thread into the production <see cref="CoreAiWorldCommandExecutor"/> and must leave
    /// at least 8 "Castle*" objects.</item>
    /// </list>
    /// Both self-skip via <see cref="Assert.Ignore(string)"/> when no live backend is configured.
    /// </summary>
    [Explicit("Live LLM required: configure COREAI_TEST_BASE_URL / COREAI_TEST_MODEL (or CoreAISettingsAsset).")]
    [Category("LiveLlm")]
    [Timeout(1_800_000)]
    public sealed class CreatorAndLuaCastleBuildLivePlayModeTests
    {
        private const string CastlePrefix = "Castle";
        private const int MinCastleObjects = 8;

        private const string CreatorPrompt =
            "Build a small castle at the origin: four corner towers and four walls connecting them, " +
            "on a stone base. Name every part starting with 'Castle'.";

        private const string LuaPrompt =
            "Build a small castle at the world origin using Lua. Write and run Lua that calls the world API " +
            "coreai_world_spawn{ ... } once per part to place a stone base, four corner towers and four walls " +
            "connecting them — at least 8 parts total. Give every part a DISTINCT targetName starting with " +
            "'Castle' (CastleBase, CastleTower1, CastleWallNorth, ...), explicit x/y/z coordinates (1 unit = 1 " +
            "meter) and scaleX/scaleY/scaleZ for the walls and towers. Do not build a mod or hook the tick loop; " +
            "just spawn the parts immediately.";

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            // WHY: [UnitySetUp] runs in a different LogAssert scope than the [UnityTest] body in PlayMode, so
            // this is re-armed in-body too; a live model can log recoverable errors mid-build that must not fail
            // these gates (they assert on world state, not console hygiene).
            LogAssert.ignoreFailingMessages = true;
            yield break;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            LogAssert.ignoreFailingMessages = false;
            yield break;
        }

        private sealed class NullSink : IAiGameCommandSink
        {
            public void Publish(ApplyAiGameCommand command)
            {
            }
        }

        /// <summary>Records every world command the Lua bindings publish so the test can replay them on the
        /// main thread; recording is thread-safe because Lua tool execution may hop off the main thread.</summary>
        private sealed class RecordingWorldSink : IAiGameCommandSink
        {
            private readonly object _lock = new();
            private readonly List<ApplyAiGameCommand> _commands = new();

            public void Publish(ApplyAiGameCommand command)
            {
                lock (_lock)
                {
                    _commands.Add(command);
                }
            }

            public List<ApplyAiGameCommand> Snapshot()
            {
                lock (_lock)
                {
                    return new List<ApplyAiGameCommand>(_commands);
                }
            }
        }

        /// <summary>Minimal in-memory mod store so the Lua stack has store_get/store_set without touching disk.</summary>
        private sealed class InMemoryModStore : ILuaModStore
        {
            private readonly object _lock = new();
            private readonly Dictionary<string, Dictionary<string, string>> _data = new(StringComparer.Ordinal);

            public string Get(string modId, string key)
            {
                lock (_lock)
                {
                    return _data.TryGetValue(modId ?? "", out Dictionary<string, string> mod) &&
                           mod.TryGetValue(key ?? "", out string value)
                        ? value
                        : "";
                }
            }

            public void Set(string modId, string key, string value)
            {
                lock (_lock)
                {
                    if (!_data.TryGetValue(modId ?? "", out Dictionary<string, string> mod))
                    {
                        mod = new Dictionary<string, string>(StringComparer.Ordinal);
                        _data[modId ?? ""] = mod;
                    }

                    if (value == null)
                    {
                        mod.Remove(key ?? "");
                    }
                    else
                    {
                        mod[key ?? ""] = value;
                    }
                }
            }

            public void Clear(string modId)
            {
                lock (_lock)
                {
                    _data.Remove(modId ?? "");
                }
            }
        }

        // ------------------------------------------------------------------------------------------
        // TEST A: Creator role builds a castle through the real world_command tool
        // ------------------------------------------------------------------------------------------

        [UnityTest]
        [Timeout(1_800_000)]
        public IEnumerator Creator_BuildsSmallCastle_ViaWorldCommand()
        {
            LogAssert.ignoreFailingMessages = true;
            TestContext.WriteLine("[CreatorCastle] === TEST START ===");

            if (!PlayModeProductionLikeLlmFactory.TryCreate(null, 0.2f, 600,
                    out PlayModeProductionLikeLlmHandle handle, out string ignore))
            {
                Assert.Ignore(ignore);
            }

            CoreAISettingsAsset settings = null;
            HashSet<int> preExisting = CollectCastleInstanceIds();
            try
            {
                if (handle.ResolvedBackend == PlayModeProductionLikeLlmBackend.LlmUnity)
                {
                    yield return PlayModeProductionLikeLlmFactory.EnsureLlmUnityModelReady(handle);
                }

                TestContext.WriteLine($"[CreatorCastle] Backend: {handle.ResolvedBackend}");

                InMemoryStore store = new();
                ILlmClient client = handle.WrapWithMemoryStore(store);

                CoreAiWorldCommandExecutor worldExecutor =
                    new(GameLoggerUnscopedFallback.Instance, null, allowPrimitives: true);

                settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
                settings.SetOrchestratorTimeoutSeconds(600);

                // WHY: DEFAULT policy keeps Creator's production config (built-in system prompt, unlimited
                // tool roundtrips); only the world tool is attached, exactly like production hosts do.
                AgentMemoryPolicy policy = new();
                policy.SetToolsForRole(BuiltInAgentRoleIds.Creator, new List<ILlmTool>
                {
                    new WorldLlmTool(worldExecutor, settings, GameLoggerUnscopedFallback.Instance)
                });

                AiOrchestrator orchestrator = BuildOrchestrator(client, store, policy, settings);

                TestContext.WriteLine($"[CreatorCastle] Prompt: {CreatorPrompt}");
                using CancellationTokenSource cts = new();
                Task task = orchestrator.RunTaskAsync(new AiTaskRequest
                {
                    RoleId = BuiltInAgentRoleIds.Creator,
                    Hint = CreatorPrompt,
                    MaxOutputTokens = 128000
                }, cts.Token);

                yield return PlayModeTestAwait.WaitTask(task, 1500f, "Creator castle build", cts);

                float graceStarted = Time.realtimeSinceStartup;
                while (CountNewCastleObjects(preExisting, out _) < MinCastleObjects &&
                       Time.realtimeSinceStartup - graceStarted < 10f)
                {
                    yield return null;
                }

                int newCastle = CountNewCastleObjects(preExisting, out List<string> names);
                TestContext.WriteLine("[CreatorCastle] ---------- TRANSCRIPT ----------");
                TestContext.WriteLine($"[CreatorCastle] Castle objects found ({newCastle}): {string.Join(", ", names)}");
                TestContext.WriteLine("[CreatorCastle] --------------------------------");

                Assert.GreaterOrEqual(newCastle, MinCastleObjects,
                    $"Expected at least {MinCastleObjects} new scene objects named '{CastlePrefix}*' after the " +
                    $"Creator build. Found {newCastle}: [{string.Join(", ", names)}].");

                TestContext.WriteLine("[CreatorCastle] TEST PASSED");
            }
            finally
            {
                DestroyNewCastleObjects(preExisting);
                if (settings != null)
                {
                    UnityEngine.Object.DestroyImmediate(settings);
                }

                handle.Dispose();
            }
        }

        // ------------------------------------------------------------------------------------------
        // TEST B: Programmer role builds a castle through Lua (coreai_world_spawn)
        // ------------------------------------------------------------------------------------------

        [UnityTest]
        [Timeout(1_800_000)]
        public IEnumerator Programmer_BuildsCastle_ViaLuaWorldApi()
        {
            LogAssert.ignoreFailingMessages = true;
            TestContext.WriteLine("[LuaCastle] === TEST START ===");

            if (!PlayModeProductionLikeLlmFactory.TryCreate(null, 0.2f, 600,
                    out PlayModeProductionLikeLlmHandle handle, out string ignore))
            {
                Assert.Ignore(ignore);
            }

            CoreAISettingsAsset settings = null;
            LuaCsModStack stack = null;
            HashSet<int> preExisting = CollectCastleInstanceIds();
            try
            {
                if (handle.ResolvedBackend == PlayModeProductionLikeLlmBackend.LlmUnity)
                {
                    yield return PlayModeProductionLikeLlmFactory.EnsureLlmUnityModelReady(handle);
                }

                TestContext.WriteLine($"[LuaCastle] Backend: {handle.ResolvedBackend}");

                RecordingWorldSink recordingSink = new();

                // WHY: Same production Lua stack as CoreAiModsInstaller (enableFullLuaAccess=false): mods get
                // LuaCapabilities.All, one-off execute_lua gets All minus Full. The world bindings publish
                // coreai_world_spawn commands to the sink; we replay them into the real executor below.
                LuaCapabilities scriptCapabilities = LuaCapabilities.All;
                LuaCapabilities oneOffCapabilities = scriptCapabilities & ~LuaCapabilities.Full;

                stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
                {
                    Logger = GameLoggerUnscopedFallback.Instance,
                    CommandSink = recordingSink,
                    ModStore = new InMemoryModStore(),
                    Log = Log.Instance,
                    Capabilities = scriptCapabilities,
                    OneOffCapabilities = oneOffCapabilities,
                    RbxApi = new LuaCsRbxApiBindings()
                });

                settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
                settings.SetOrchestratorTimeoutSeconds(600);

                AgentMemoryPolicy policy = new();
                policy.AddToolForRole(BuiltInAgentRoleIds.Programmer,
                    new LuaLlmTool(stack.ToolExecutor, settings, Log.Instance, new LuaGenerationRateLimiter()));
                policy.AddToolForRole(BuiltInAgentRoleIds.Programmer,
                    new LuaModsLlmTool(stack.Runtime, settings, Log.Instance, scriptCapabilities));
                policy.AddSkillForRole(BuiltInAgentRoleIds.Programmer, SkillSet.FromTextContent(
                    BuiltInLuaModdingSkillText.SkillName,
                    BuiltInLuaModdingSkillText.SkillDescription,
                    BuiltInLuaModdingSkillText.Instructions));
                policy.AddSkillForRole(BuiltInAgentRoleIds.Programmer, SkillSet.FromTextContent(
                    BuiltInRbxApiSkillText.SkillName,
                    BuiltInRbxApiSkillText.SkillDescription,
                    BuiltInRbxApiSkillText.Instructions));

                InMemoryStore store = new();
                ILlmClient client = handle.WrapWithMemoryStore(store);
                AiOrchestrator orchestrator = BuildOrchestrator(client, store, policy, settings);

                TestContext.WriteLine($"[LuaCastle] Prompt: {LuaPrompt}");
                using CancellationTokenSource cts = new();
                Task task = orchestrator.RunTaskAsync(new AiTaskRequest
                {
                    RoleId = BuiltInAgentRoleIds.Programmer,
                    Hint = LuaPrompt,
                    MaxOutputTokens = 128000
                }, cts.Token);

                yield return PlayModeTestAwait.WaitTask(task, 1500f, "Programmer lua castle build", cts);

                // WHY: The model's Lua ran on the worker thread and published its world commands to the sink;
                // parse them to prove the Lua produced a castle's worth of DISTINCT 'Castle*' spawns. This is
                // the authoritative proof of "castle via Lua" — it does not depend on prefab resolution.
                List<ApplyAiGameCommand> commands = recordingSink.Snapshot();
                HashSet<string> castleSpawns = new(StringComparer.OrdinalIgnoreCase);
                List<ApplyAiGameCommand> replay = new();
                foreach (ApplyAiGameCommand command in commands)
                {
                    if (!string.Equals(command.CommandTypeId, AiGameCommandTypeIds.WorldCommand, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    CoreAiWorldCommandEnvelope env;
                    try
                    {
                        env = JsonUtility.FromJson<CoreAiWorldCommandEnvelope>(command.JsonPayload ?? "");
                    }
                    catch (Exception)
                    {
                        env = null;
                    }

                    if (env == null ||
                        !string.Equals(env.action?.Trim(), "spawn", StringComparison.OrdinalIgnoreCase) ||
                        string.IsNullOrEmpty(env.targetName) ||
                        !env.targetName.StartsWith(CastlePrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    castleSpawns.Add(env.targetName.Trim());

                    // WHY: This minimal gate has no prefab registry, so map each part onto a built-in primitive
                    // (geometry unchanged) so the spawns MATERIALIZE into real GameObjects on replay below.
                    env.prefabKeyOrName = "cube";
                    replay.Add(new ApplyAiGameCommand
                    {
                        CommandTypeId = AiGameCommandTypeIds.WorldCommand,
                        JsonPayload = JsonUtility.ToJson(env, false),
                        SourceRoleId = command.SourceRoleId,
                        SourceTaskHint = command.SourceTaskHint,
                        SourceTag = command.SourceTag
                    });
                }

                // WHY: Replay on the MAIN thread — CoreAiWorldCommandExecutor creates GameObjects and must not
                // be driven from the Lua worker thread. Corroborates that the emitted spawns are applicable.
                CoreAiWorldCommandExecutor worldExecutor =
                    new(GameLoggerUnscopedFallback.Instance, null, allowPrimitives: true);
                int accepted = 0;
                foreach (ApplyAiGameCommand command in replay)
                {
                    if (worldExecutor.TryExecute(command))
                    {
                        accepted++;
                    }
                }

                int newCastle = CountNewCastleObjects(preExisting, out List<string> names);

                TestContext.WriteLine("[LuaCastle] ---------- TRANSCRIPT ----------");
                TestContext.WriteLine($"[LuaCastle] World commands emitted by Lua: {commands.Count}");
                TestContext.WriteLine($"[LuaCastle] Distinct Castle* spawns from Lua ({castleSpawns.Count}): " +
                                      string.Join(", ", castleSpawns));
                TestContext.WriteLine($"[LuaCastle] Materialized on replay ({newCastle}, accepted {accepted}): " +
                                      string.Join(", ", names));
                TestContext.WriteLine("[LuaCastle] --------------------------------");

                Assert.GreaterOrEqual(castleSpawns.Count, MinCastleObjects,
                    $"Programmer's Lua must call coreai_world_spawn for at least {MinCastleObjects} distinct " +
                    $"'{CastlePrefix}*' parts. Distinct Castle spawns: {castleSpawns.Count} " +
                    $"([{string.Join(", ", castleSpawns)}]); total world commands emitted: {commands.Count}.");
                Assert.GreaterOrEqual(newCastle, MinCastleObjects,
                    $"Expected at least {MinCastleObjects} '{CastlePrefix}*' objects after replaying the Lua " +
                    $"spawns. Found {newCastle}: [{string.Join(", ", names)}]. Accepted: {accepted}.");

                TestContext.WriteLine("[LuaCastle] TEST PASSED");
            }
            finally
            {
                DestroyNewCastleObjects(preExisting);
                if (settings != null)
                {
                    UnityEngine.Object.DestroyImmediate(settings);
                }

                handle.Dispose();
            }
        }

        // ------------------------------------------------------------------------------------------
        // Shared helpers
        // ------------------------------------------------------------------------------------------

        private static AiOrchestrator BuildOrchestrator(
            ILlmClient client, InMemoryStore store, AgentMemoryPolicy policy, CoreAISettingsAsset settings)
        {
            AiPromptComposer composer = new(
                new BuiltInDefaultAgentSystemPromptProvider(),
                new NoAgentUserPromptTemplateProvider(),
                new NullLuaScriptVersionStore());

            return new AiOrchestrator(
                new SoloAuthorityHost(),
                client,
                new NullSink(),
                new SessionTelemetryCollector(),
                composer,
                store,
                policy,
                new CompositeRoleStructuredResponsePolicy(),
                new NullAiOrchestrationMetrics(),
                settings);
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
    }
}
#endif
