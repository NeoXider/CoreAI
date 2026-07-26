#if !COREAI_NO_LLM && !UNITY_WEBGL
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Chat;
using CoreAI.Composition;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using CoreAI.Logging;
using CoreAI.Messaging;
using CoreAI.Session;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Live end-to-end gates for the built-in <see cref="BuiltInAgentRoleIds.Programmer"/> role over the
    /// REAL Lua mod stack (<see cref="LuaCsModRuntimeFactory"/>, same wiring as CoreAiModsInstaller:
    /// execute_lua + manage_mods tools plus the LuaModding/RbxApi skills):
    /// one test builds the built-in Tetris example mod from the chat Example prompt, the other repairs a
    /// deliberately broken mod whose event handler fails at dispatch time. Self-skips when no live LLM
    /// backend is configured (COREAI_TEST_BASE_URL / COREAI_TEST_MODEL or a configured CoreAISettingsAsset).
    /// </summary>
    [Explicit("Live LLM required: configure COREAI_TEST_BASE_URL / COREAI_TEST_MODEL (or CoreAISettingsAsset).")]
    [Category("LiveLlm")]
    [Timeout(1_800_000)]
    public sealed class ProgrammerLuaModsLivePlayModeTests
    {
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

        // ------------------------------------------------------------------------------------------
        // Shared production-like Programmer setup
        // ------------------------------------------------------------------------------------------

        /// <summary>Thread-safe in-memory <see cref="ILuaModStore"/> so store_set/store_get exist without touching FileLuaModStore.</summary>
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

        /// <summary>Counts world/data commands the Lua bindings publish, proving Lua reached the world seam.</summary>
        private sealed class CountingCommandSink : IAiGameCommandSink
        {
            private int _count;

            public int Count => Volatile.Read(ref _count);

            public void Publish(ApplyAiGameCommand command)
            {
                Interlocked.Increment(ref _count);
            }
        }

        private sealed class CapturingLlmClient : ILlmClient
        {
            public LlmCompletionResult LastResult;

            private readonly ILlmClient _inner;

            public CapturingLlmClient(ILlmClient inner)
            {
                _inner = inner;
            }

            public async Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                LastResult = await _inner.CompleteAsync(request, cancellationToken);
                return LastResult;
            }

            public void SetTools(IReadOnlyList<ILlmTool> tools)
            {
                _inner.SetTools(tools);
            }
        }

        private sealed class NullSink : IAiGameCommandSink
        {
            public void Publish(ApplyAiGameCommand command)
            {
            }
        }

        /// <summary>Everything one live Programmer scenario needs, built per test and disposed in finally.</summary>
        private sealed class ProgrammerSetup
        {
            public LuaCsModStack Stack;
            public CountingCommandSink CommandSink;
            public AiOrchestrator Orchestrator;
            public CapturingLlmClient Capturing;
            public CoreAISettingsAsset Settings;

            public void Dispose()
            {
                if (Settings != null)
                {
                    UnityEngine.Object.DestroyImmediate(Settings);
                    Settings = null;
                }
            }
        }

        /// <summary>
        /// Builds the production Programmer pipeline: the real Lua-CSharp mod stack (in-memory stores so
        /// this test is independent of FileLuaModStore work), the production execute_lua + manage_mods
        /// tools, both built-in skills, and the orchestrator over the DEFAULT AgentMemoryPolicy (which
        /// keeps Programmer's production config: unlimited tool roundtrips, Full tool-result memory).
        /// </summary>
        private static ProgrammerSetup BuildProgrammerSetup(PlayModeProductionLikeLlmHandle handle)
        {
            ProgrammerSetup setup = new()
            {
                CommandSink = new CountingCommandSink(),
                Settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>()
            };
            setup.Settings.SetOrchestratorTimeoutSeconds(600);

            // WHY: Mirrors CoreAiModsInstaller with enableFullLuaAccess=false: mods get LuaCapabilities.All,
            // one-off execute_lua gets All minus Full — the exact production capability split.
            LuaCapabilities scriptCapabilities = LuaCapabilities.All;
            LuaCapabilities oneOffCapabilities = scriptCapabilities & ~LuaCapabilities.Full;

            setup.Stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = GameLoggerUnscopedFallback.Instance,
                CommandSink = setup.CommandSink,
                ModStore = new InMemoryModStore(),
                Log = Log.Instance,
                Capabilities = scriptCapabilities,
                OneOffCapabilities = oneOffCapabilities,
                RbxApi = new LuaCsRbxApiBindings()
            });

            AgentMemoryPolicy policy = new();
            policy.AddToolForRole(BuiltInAgentRoleIds.Programmer,
                new LuaLlmTool(setup.Stack.ToolExecutor, setup.Settings, Log.Instance,
                    new LuaGenerationRateLimiter()));
            policy.AddToolForRole(BuiltInAgentRoleIds.Programmer,
                new LuaModsLlmTool(setup.Stack.Runtime, setup.Settings, Log.Instance, scriptCapabilities));
            policy.AddSkillForRole(BuiltInAgentRoleIds.Programmer, SkillSet.FromTextContent(
                BuiltInLuaModdingSkillText.SkillName,
                BuiltInLuaModdingSkillText.SkillDescription,
                BuiltInLuaModdingSkillText.Instructions));
            policy.AddSkillForRole(BuiltInAgentRoleIds.Programmer, SkillSet.FromTextContent(
                BuiltInRbxApiSkillText.SkillName,
                BuiltInRbxApiSkillText.SkillDescription,
                BuiltInRbxApiSkillText.Instructions));

            InMemoryStore memoryStore = new();
            setup.Capturing = new CapturingLlmClient(handle.WrapWithMemoryStore(memoryStore));

            AiPromptComposer composer = new(
                new BuiltInDefaultAgentSystemPromptProvider(),
                new NoAgentUserPromptTemplateProvider(),
                new NullLuaScriptVersionStore());

            setup.Orchestrator = new AiOrchestrator(
                new SoloAuthorityHost(),
                setup.Capturing,
                new NullSink(),
                new SessionTelemetryCollector(),
                composer,
                memoryStore,
                policy,
                new CompositeRoleStructuredResponsePolicy(),
                new NullAiOrchestrationMetrics(),
                setup.Settings);

            return setup;
        }

        private static string ResolveExampleMessage(string exampleId)
        {
            foreach (CoreAiChatExample example in CoreAiChatExamples.All)
            {
                if (string.Equals(example.Id, exampleId, StringComparison.Ordinal))
                {
                    return example.Message;
                }
            }

            Assert.Fail($"Built-in chat example '{exampleId}' not found in CoreAiChatExamples.All.");
            return null;
        }

        private static void LogToolCallTranscript(string label)
        {
            IReadOnlyList<LlmToolCallRecord> history = CoreAi.GetToolCallHistorySnapshot();
            TestContext.WriteLine($"[{label}] Tool calls recorded: {history?.Count ?? 0}");
            if (history == null)
            {
                return;
            }

            foreach (LlmToolCallRecord record in history)
            {
                if (record == null)
                {
                    continue;
                }

                string args = record.Info.ArgumentsJson ?? "";
                TestContext.WriteLine(
                    $"[{label}]   {record.Info.ToolName} [{record.Status}] " +
                    $"args={args.Substring(0, Math.Min(200, args.Length))}");
            }
        }

        // ------------------------------------------------------------------------------------------
        // TEST A: Tetris mod from the built-in example prompt
        // ------------------------------------------------------------------------------------------

        [UnityTest]
        [Timeout(1_800_000)]
        public IEnumerator Programmer_BuildsTetrisMod_FromBuiltInExamplePrompt()
        {
            // WHY: [UnitySetUp] runs in a different LogAssert scope than the [UnityTest] body in
            // PlayMode, so the flag set there does NOT carry here. A deliberately-buggy mod the agent
            // may reload mid-repair logs error-level handler failures by design; re-arm the flag in-body
            // so those expected logs don't fail this live gate. See TearDown for the reset.
            LogAssert.ignoreFailingMessages = true;
            TestContext.WriteLine("[TetrisMod] === TEST START ===");

            if (!PlayModeProductionLikeLlmFactory.TryCreate(null, 0.2f, 600,
                    out PlayModeProductionLikeLlmHandle handle, out string ignore))
            {
                Assert.Ignore(ignore);
            }

            ProgrammerSetup setup = null;
            try
            {
                if (handle.ResolvedBackend == PlayModeProductionLikeLlmBackend.LlmUnity)
                {
                    yield return PlayModeProductionLikeLlmFactory.EnsureLlmUnityModelReady(handle);
                }

                TestContext.WriteLine($"[TetrisMod] Backend: {handle.ResolvedBackend}");
                GlobalMessagePipeMinimalBootstrap.EnsureInitializedForLlmDiagnostics();

                setup = BuildProgrammerSetup(handle);

                // WHY: The exact prompt the in-game chat Examples menu inserts for its Tetris button.
                string prompt = ResolveExampleMessage("tetris");
                TestContext.WriteLine($"[TetrisMod] Prompt length: {prompt.Length} chars (built-in 'tetris' example)");

                List<string> loadedModIds = new();
                setup.Stack.Runtime.ModSourceLoaded += (id, _, _) => loadedModIds.Add(id);

                CoreAi.ClearToolCallHistory();
                using CancellationTokenSource cts = new();
                Task task = setup.Orchestrator.RunTaskAsync(new AiTaskRequest
                {
                    RoleId = BuiltInAgentRoleIds.Programmer,
                    Hint = prompt,
                    MaxOutputTokens = 128000
                }, cts.Token);

                yield return PlayModeTestAwait.WaitTask(task, 1500f, "Programmer tetris mod", cts);

                IReadOnlyList<LuaModInfo> mods = setup.Stack.Runtime.ListMods();

                TestContext.WriteLine("[TetrisMod] ---------- TRANSCRIPT ----------");
                LogToolCallTranscript("TetrisMod");
                TestContext.WriteLine($"[TetrisMod] ModSourceLoaded events: {loadedModIds.Count} " +
                                      $"({string.Join(", ", loadedModIds)})");
                TestContext.WriteLine($"[TetrisMod] World commands published during load: {setup.CommandSink.Count}");
                foreach (LuaModInfo mod in mods)
                {
                    TestContext.WriteLine(
                        $"[TetrisMod] Mod '{mod.Id}': handlers={mod.HandlerCount} timers={mod.TimerCount} " +
                        $"errors={mod.ErrorCount} quarantined={mod.Quarantined}");
                    if (setup.Stack.Runtime.TryGetModSource(mod.Id, out string source))
                    {
                        TestContext.WriteLine($"[TetrisMod] --- source of '{mod.Id}' ---\n{source}");
                    }
                }

                string finalAnswer = setup.Capturing.LastResult.Content ?? "";
                TestContext.WriteLine($"[TetrisMod] Final answer: {finalAnswer}");
                TestContext.WriteLine("[TetrisMod] --------------------------------");

                Assert.IsTrue(setup.Capturing.LastResult.Ok,
                    $"Programmer run failed: {setup.Capturing.LastResult.Error}");
                Assert.GreaterOrEqual(loadedModIds.Count, 1,
                    "The agent must load at least one mod through the real manage_mods tool " +
                    "(ModSourceLoaded never fired).");
                Assert.GreaterOrEqual(mods.Count, 1, "Runtime must contain at least one loaded mod.");

                LuaModInfo newest = mods[mods.Count - 1];
                Assert.IsFalse(newest.Quarantined, $"Mod '{newest.Id}' is quarantined after load.");
                Assert.AreEqual(0, newest.ErrorCount,
                    $"Mod '{newest.Id}' reported {newest.ErrorCount} handler errors right after load.");
                Assert.Greater(newest.HandlerCount + newest.TimerCount, 0,
                    $"Mod '{newest.Id}' registered no hooks/timers — a Tetris mod must hook the tick loop.");
                Assert.IsFalse(string.IsNullOrWhiteSpace(finalAnswer),
                    "The agent's final answer must be non-empty.");

                bool completedModsToolCall = CoreAi.GetToolCallHistorySnapshot().Any(r =>
                    r != null && r.Status == "completed" &&
                    (r.Info.ToolName == "manage_mods" || r.Info.ToolName == "execute_lua"));
                Assert.IsTrue(completedModsToolCall,
                    "Tool-call history must contain a completed manage_mods/execute_lua call.");

                TestContext.WriteLine("[TetrisMod] TEST PASSED");
            }
            finally
            {
                setup?.Dispose();
                handle.Dispose();
            }
        }

        // ------------------------------------------------------------------------------------------
        // TEST B: fix a broken mod whose event handler fails at dispatch time
        // ------------------------------------------------------------------------------------------

        // WHY: Mirrors BUG 1 of the built-in 'arena' chat example (CoreAiChatExamples.ArenaLua): the code
        // reads cfg.spawn_count but the field is spawnCount -> arithmetic on a nil value. The example's
        // BUG 2 (hooks_every(0, ...)) is omitted deliberately: it throws at LOAD time, and this arrange
        // step needs a mod that loads cleanly and only fails when the 'wave_start' event is dispatched.
        private const string BrokenArenaLua =
            @"local cfg = {
  spawnCount = 5,
  radius = 6.0,
}

local wave = 0

hooks_on('wave_start', function()
  wave = wave + 1
  local count = cfg.spawn_count * wave
  events_emit('wave_done', tostring(count))
end)
";

        private const string BrokenModId = "arena_spawner";

        // WHY: One message, phrased like the built-in 'arena' example ('find out why it fails and fix it'),
        // but against the pre-loaded mod instead of pasting code, so the agent must use manage_mods
        // (list/diagnostics/get_source) to discover the fault before repairing it.
        private const string FixPrompt =
            "The game's wave spawner is broken. The mod 'arena_spawner' is already loaded, but every time " +
            "the 'wave_start' event fires it throws an error and no wave result is reported. Find out why " +
            "it fails and fix it: use manage_mods to inspect the loaded mods, read the recent runtime " +
            "diagnostics and the mod's source, then reload the mod with the corrected code. Keep its " +
            "contract: on each 'wave_start' event it must call events_emit('wave_done', count) where count " +
            "is a real number.";

        [UnityTest]
        [Timeout(1_800_000)]
        public IEnumerator Programmer_FixesBrokenMod_FromSingleNaturalLanguagePrompt()
        {
            // WHY: [UnitySetUp] runs in a different LogAssert scope than the [UnityTest] body in
            // PlayMode, so the flag set there does NOT carry here. The arrange step deliberately triggers
            // the broken handler, which logs error-level failures by design; re-arm the flag in-body so
            // those expected logs don't fail this live gate. See TearDown for the reset.
            LogAssert.ignoreFailingMessages = true;
            TestContext.WriteLine("[FixMod] === TEST START ===");

            if (!PlayModeProductionLikeLlmFactory.TryCreate(null, 0.2f, 600,
                    out PlayModeProductionLikeLlmHandle handle, out string ignore))
            {
                Assert.Ignore(ignore);
            }

            ProgrammerSetup setup = null;
            try
            {
                if (handle.ResolvedBackend == PlayModeProductionLikeLlmBackend.LlmUnity)
                {
                    yield return PlayModeProductionLikeLlmFactory.EnsureLlmUnityModelReady(handle);
                }

                TestContext.WriteLine($"[FixMod] Backend: {handle.ResolvedBackend}");
                GlobalMessagePipeMinimalBootstrap.EnsureInitializedForLlmDiagnostics();

                setup = BuildProgrammerSetup(handle);

                int handlerErrors = 0;
                string lastHandlerError = null;
                List<string> waveDonePayloads = new();
                setup.Stack.Runtime.ModHandlerErrored += (_, error, _) =>
                {
                    handlerErrors++;
                    lastHandlerError = error;
                };
                setup.Stack.Runtime.ModEventEmitted += (_, eventName, payload) =>
                {
                    if (eventName == "wave_done")
                    {
                        waveDonePayloads.Add(payload ?? "");
                    }
                };

                // ---- Arrange: load the broken mod through the REAL runtime and prove it is broken.
                setup.Stack.Runtime.LoadMod(BrokenModId, BrokenArenaLua, LuaCapabilities.All,
                    false);
                Assert.IsTrue(setup.Stack.Runtime.IsLoaded(BrokenModId),
                    "Arrange failed: the broken mod did not load.");

                yield return TriggerWave(setup.Stack.Runtime);

                TestContext.WriteLine($"[FixMod] BEFORE: handlerErrors={handlerErrors} " +
                                      $"waveDone={waveDonePayloads.Count} lastError={lastHandlerError}");
                Assert.GreaterOrEqual(handlerErrors, 1,
                    "Arrange failed: the broken handler did not error on 'wave_start'.");
                Assert.AreEqual(0, waveDonePayloads.Count,
                    "Arrange failed: the broken mod unexpectedly emitted 'wave_done'.");

                // ---- Act: one natural-language prompt through the production Programmer pipeline.
                int errorsBeforeAgent = handlerErrors;
                CoreAi.ClearToolCallHistory();
                using CancellationTokenSource cts = new();
                Task task = setup.Orchestrator.RunTaskAsync(new AiTaskRequest
                {
                    RoleId = BuiltInAgentRoleIds.Programmer,
                    Hint = FixPrompt,
                    MaxOutputTokens = 128000
                }, cts.Token);

                yield return PlayModeTestAwait.WaitTask(task, 1500f, "Programmer fix broken mod", cts);

                // ---- Assert: the previously failing dispatch now succeeds.
                int errorsAfterAgent = handlerErrors;
                waveDonePayloads.Clear();

                yield return TriggerWave(setup.Stack.Runtime);

                IReadOnlyList<LuaModInfo> mods = setup.Stack.Runtime.ListMods();

                TestContext.WriteLine("[FixMod] ---------- TRANSCRIPT ----------");
                LogToolCallTranscript("FixMod");
                foreach (LuaModInfo mod in mods)
                {
                    TestContext.WriteLine(
                        $"[FixMod] Mod '{mod.Id}': handlers={mod.HandlerCount} timers={mod.TimerCount} " +
                        $"errors={mod.ErrorCount} quarantined={mod.Quarantined}");
                    if (setup.Stack.Runtime.TryGetModSource(mod.Id, out string source))
                    {
                        TestContext.WriteLine($"[FixMod] --- source of '{mod.Id}' (after) ---\n{source}");
                    }
                }

                TestContext.WriteLine($"[FixMod] AFTER: newHandlerErrors={handlerErrors - errorsAfterAgent} " +
                                      $"waveDone={waveDonePayloads.Count} " +
                                      $"payloads=[{string.Join(", ", waveDonePayloads)}]");
                TestContext.WriteLine($"[FixMod] Final answer: {setup.Capturing.LastResult.Content}");
                TestContext.WriteLine("[FixMod] --------------------------------");

                Assert.IsTrue(setup.Capturing.LastResult.Ok,
                    $"Programmer run failed: {setup.Capturing.LastResult.Error}");
                Assert.GreaterOrEqual(mods.Count, 1, "No mod is loaded after the repair.");
                Assert.IsTrue(mods.Any(m => !m.Quarantined),
                    "All loaded mods are quarantined after the repair.");
                Assert.AreEqual(errorsAfterAgent, handlerErrors,
                    $"The repaired handler still errors on 'wave_start': {lastHandlerError}");
                Assert.GreaterOrEqual(waveDonePayloads.Count, 1,
                    "The repaired mod did not emit 'wave_done' when 'wave_start' fired.");
                Assert.IsTrue(waveDonePayloads.Any(p => !string.IsNullOrWhiteSpace(p)),
                    "The 'wave_done' payload must carry a real value.");

                bool completedModsToolCall = CoreAi.GetToolCallHistorySnapshot().Any(r =>
                    r != null && r.Status == "completed" && r.Info.ToolName == "manage_mods");
                Assert.IsTrue(completedModsToolCall,
                    "Tool-call history must contain a completed manage_mods call (the repair path).");

                TestContext.WriteLine("[FixMod] TEST PASSED");
            }
            finally
            {
                setup?.Dispose();
                handle.Dispose();
            }
        }

        /// <summary>
        /// Emits 'wave_start' and drives <see cref="ILuaModRuntime.Tick"/> for a short window, the same
        /// dispatch path the production frame ticker uses.
        /// </summary>
        private static IEnumerator TriggerWave(ILuaModRuntime runtime)
        {
            runtime.EmitEvent("wave_start");
            for (int i = 0; i < 10; i++)
            {
                runtime.Tick(0.05);
                yield return null;
            }
        }
    }
}
#endif
