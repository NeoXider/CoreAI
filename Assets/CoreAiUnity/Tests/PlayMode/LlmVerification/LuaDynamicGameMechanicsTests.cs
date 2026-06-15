#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Llm;
using CoreAI.Messaging;
using CoreAI.Session;
using CoreAI.Sandbox;
using MoonSharp.Interpreter;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    ///     (  Lua)  " "   .
    ///      (,  ), 
    ///      .
    /// </summary>
#if !COREAI_NO_LLM && !UNITY_WEBGL
    public sealed class LuaDynamicGameMechanicsTests
    {
        private sealed class SharedLuaExecutor : LuaTool.ILuaExecutor
        {
            public readonly SecureLuaEnvironment Sandbox = new();
            public readonly LuaApiRegistry Registry = new();
            public readonly LuaLogicSlots LogicSlots = new();
            public int ExecutionCount;
            public string LastCode = "";
            public Script ScriptInstance;

            public SharedLuaExecutor()
            {
                LogicSlots.DeclareSlot("calculate_damage");
                LogicSlots.RegisterApis(Registry);
            }

            public Task<LuaTool.LuaResult> ExecuteAsync(string code, System.Threading.CancellationToken ct)
            {
                try
                {
                    if (ScriptInstance == null)
                    {
                        ScriptInstance = Sandbox.CreateScript(Registry);
                    }

                    ExecutionCount++;
                    LastCode = code ?? "";
                    Debug.Log($"[LuaDynamic] execute_lua code:\n{LastCode}");

                    DynValue result = Sandbox.RunChunk(ScriptInstance, code);
                    return Task.FromResult(
                        new LuaTool.LuaResult { Success = true, Output = result?.ToString() ?? "ok" });
                }
                catch (Exception ex)
                {
                    return Task.FromResult(new LuaTool.LuaResult { Success = false, Error = ex.Message });
                }
            }

            public double CallDamageCurrent()
            {
                if (LogicSlots.TryInvokeNumber("calculate_damage", out double overriddenDamage))
                {
                    return overriddenDamage;
                }

                if (ScriptInstance == null)
                {
                    return 0;
                }

                DynValue func = ScriptInstance.Globals.Get("calculate_damage");
                if (func.Type == DataType.Function)
                {
                    return ScriptInstance.Call(func).Number;
                }

                return 0;
            }
        }

        private sealed class InMemoryStore : IAgentMemoryStore
        {
            public bool TryLoad(string roleId, out AgentMemoryState state)
            {
                state = default;
                return false;
            }

            public void Save(string roleId, AgentMemoryState state)
            {
            }

            public void Clear(string roleId)
            {
            }

            public void ClearChatHistory(string roleId)
            {
            }

            public void AppendChatMessage(string roleId, string role, string content, bool persistToDisk = true)
            {
            }

            public ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0)
            {
                return Array.Empty<ChatMessage>();
            }
        }

        private sealed class ListSink : IAiGameCommandSink
        {
            public readonly List<ApplyAiGameCommand> Items = new();

            public void Publish(ApplyAiGameCommand command)
            {
                Items.Add(command);
            }
        }

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator GameMaster_ModifiesDamageFormula_AtRuntime()
        {
            Debug.Log("[LuaDynamic]  LUA MECHANICS MODIFICATION TEST START ");

            if (!PlayModeProductionLikeLlmFactory.TryCreate(
                    null, 0.1f, 300, out PlayModeProductionLikeLlmHandle handle, out string ignore))
            {
                Assert.Ignore(ignore);
            }

            try
            {
                if (handle.ResolvedBackend == PlayModeProductionLikeLlmBackend.LlmUnity)
                {
                    yield return PlayModeProductionLikeLlmFactory.EnsureLlmUnityModelReady(handle);
                }

                // 1.   Lua Executor  ""  ( )
                SharedLuaExecutor executor = new();
                const string INITIAL_LOGIC = @"
--  ,   
function calculate_damage()
    return 10
end
";
                //   
                executor.ExecuteAsync(INITIAL_LOGIC, default).GetAwaiter().GetResult();

                // ,       10
                double initialDamage = executor.CallDamageCurrent();
                Debug.Log($"[LuaDynamic] Initial calculate_damage() = {initialDamage}");
                Assert.AreEqual(10.0, initialDamage, "Initial damage should be 10");

                // 2.  -,     execute_lua
                CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();

                //  
                AgentBuilder builder = new AgentBuilder("GameMaster")
                    .WithSystemPrompt("You are the GameMaster. You manage game mechanics.")
                    .WithTool(new LuaLlmTool(executor, settings, Logging.NullLog.Instance))
                    .WithAllowDuplicateToolCalls(true)
                    .WithMode(AgentMode.ToolsOnly);

                AgentConfig config = builder.Build();
                AgentMemoryPolicy policy = new();
                config.ApplyToPolicy(policy);

                ListSink sink = new();
                AiOrchestrator orch = new(
                    new SoloAuthorityHost(),
                    handle.Client,
                    sink,
                    new SessionTelemetryCollector(),
                    new AiPromptComposer(
                        new BuiltInDefaultAgentSystemPromptProvider(),
                        new NoAgentUserPromptTemplateProvider(),
                        new NullLuaScriptVersionStore()),
                    new InMemoryStore(),
                    policy,
                    new NoOpRoleStructuredResponsePolicy(),
                    new NullAiOrchestrationMetrics(),
                    settings
                );

                // 3.   : " ,   .    5 ."
                string prompt = "Players are complaining that the game is too hard. " +
                                "The game exposes a runtime logic slot named calculate_damage through the logic_* Lua API. " +
                                "Change that slot so the current damage result becomes 50 instead of 10.\n" +
                                "Apply the change through the available Lua execution tool.";

                Debug.Log($"[LuaDynamic]  PROMPT: {prompt}");

                using CancellationTokenSource cts = new();
                Task t = orch.RunTaskAsync(new AiTaskRequest
                {
                    RoleId = "GameMaster",
                    Hint = prompt
                }, cts.Token);

                yield return PlayModeTestAwait.WaitTask(t, 240f, "modify lua mechanics", cts);

                for (int i = 0; i < sink.Items.Count; i++)
                {
                    Debug.Log($"[LuaDynamic] LLM RESPONSE[{i}]: {sink.Items[i].JsonPayload}");
                }

                Assert.Greater(executor.ExecutionCount, 1,
                    "GameMaster must execute Lua through execute_lua after the initial setup script.");

                // 4. ,        !
                double modifiedDamage = executor.CallDamageCurrent();
                Debug.Log($"[LuaDynamic] Modified calculate_damage() = {modifiedDamage}");

                Assert.AreEqual(50.0, modifiedDamage,
                    "AI must successfully change the runtime damage rule to return 50.");

                Debug.Log("[LuaDynamic]  AI successfully modified game logic at runtime!");
            }
            finally
            {
                handle.Dispose();
            }
        }
    }
#endif
}
#endif
