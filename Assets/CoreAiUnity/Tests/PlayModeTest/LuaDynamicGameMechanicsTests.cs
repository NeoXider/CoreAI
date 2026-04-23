using System;
using System.Collections;
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
    /// Демонстрирует изменение игровой логики (написанной на Lua) прямо "на лету" с помощью агента.
    /// Агент может переписать глобальные функции (например, формулу урона), 
    /// что мгновенно отразится на правилах игры.
    /// </summary>
#if !COREAI_NO_LLM
    public sealed class LuaDynamicGameMechanicsTests
    {
        private sealed class SharedLuaExecutor : LuaTool.ILuaExecutor
        {
            public readonly SecureLuaEnvironment Sandbox = new();
            public readonly LuaApiRegistry Registry = new();
            public Script ScriptInstance;

            public Task<LuaTool.LuaResult> ExecuteAsync(string code, System.Threading.CancellationToken ct)
            {
                try
                {
                    if (ScriptInstance == null)
                    {
                        ScriptInstance = Sandbox.CreateScript(Registry);
                    }
                    
                    DynValue result = Sandbox.RunChunk(ScriptInstance, code);
                    return Task.FromResult(new LuaTool.LuaResult { Success = true, Output = result?.ToString() ?? "ok" });
                }
                catch (Exception ex)
                {
                    return Task.FromResult(new LuaTool.LuaResult { Success = false, Error = ex.Message });
                }
            }

            public double CallFunctionCurrent(string functionName)
            {
                if (ScriptInstance == null) return 0;
                DynValue func = ScriptInstance.Globals.Get(functionName);
                if (func.Type == DataType.Function)
                {
                    return ScriptInstance.Call(func).Number;
                }
                return 0;
            }
        }

        private sealed class InMemoryStore : IAgentMemoryStore
        {
            public bool TryLoad(string roleId, out AgentMemoryState state) { state = default; return false; }
            public void Save(string roleId, AgentMemoryState state) {}
            public void Clear(string roleId) {}
            public void ClearChatHistory(string roleId) {}
            public void AppendChatMessage(string roleId, string role, string content, bool persistToDisk = true) {}
            public Ai.ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0) => Array.Empty<Ai.ChatMessage>();
        }

        private sealed class NullSink : IAiGameCommandSink
        {
            public void Publish(ApplyAiGameCommand command) {}
        }

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator GameMaster_ModifiesDamageFormula_AtRuntime()
        {
            Debug.Log("[LuaDynamic] ═══ LUA MECHANICS MODIFICATION TEST START ═══");

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

                // 1. Инициализируем общий Lua Executor с "моторной" логикой (изначальная механика)
                SharedLuaExecutor executor = new();
                const string INITIAL_LOGIC = @"
-- Базовый урон, который наносит игрок
function calculate_damage()
    return 10
end
";
                // Запускаем начальную логику
                executor.ExecuteAsync(INITIAL_LOGIC, default).GetAwaiter().GetResult();

                // Проверяем, что до вмешательства агента урон равен 10
                double initialDamage = executor.CallFunctionCurrent("calculate_damage");
                Debug.Log($"[LuaDynamic] Initial calculate_damage() = {initialDamage}");
                Assert.AreEqual(10.0, initialDamage, "Initial damage should be 10");

                // 2. Настраиваем Агента-Гейммастера, который имеет доступ к execute_lua
                CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
                
                // Добавляем инструмент
                AgentBuilder builder = new AgentBuilder("GameMaster")
                    .WithSystemPrompt("You are the GameMaster. You manage game mechanics.")
                    .WithTool(new LuaLlmTool(executor, settings, CoreAI.Logging.NullLog.Instance))
                    .WithAllowDuplicateToolCalls(true)
                    .WithMode(AgentMode.ToolsOnly);

                AgentConfig config = builder.Build();
                AgentMemoryPolicy policy = new();
                config.ApplyToPolicy(policy);

                AiOrchestrator orch = new AiOrchestrator(
                    new SoloAuthorityHost(),
                    handle.Client,
                    new NullSink(),
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

                // 3. Даём задание ИИ: "Игроки жалуются, что игра сложная. Подними урон в 5 раз."
                string prompt = "Players are complaining that the game is too hard. " +
                                "Change the 'calculate_damage()' lua function to return 50 instead of 10.\n" +
                                "You MUST use the 'execute_lua' tool to redefine the function globally.\n" +
                                "Example code: \nfunction calculate_damage() return 50 end";

                Debug.Log($"[LuaDynamic] 📤 PROMPT: {prompt}");

                Task t = orch.RunTaskAsync(new AiTaskRequest
                {
                    RoleId = "GameMaster",
                    Hint = prompt
                });

                yield return PlayModeTestAwait.WaitTask(t, 240f, "modify lua mechanics");

                // 4. Проверяем, что ИИ перезаписал функцию и логика игры изменилась!
                double modifiedDamage = executor.CallFunctionCurrent("calculate_damage");
                Debug.Log($"[LuaDynamic] Modified calculate_damage() = {modifiedDamage}");

                Assert.AreEqual(50.0, modifiedDamage, 
                    "AI must successfully rewrite the lua logic to return 50!");

                Debug.Log("[LuaDynamic] ✓ AI successfully modified game logic at runtime!");
            }
            finally
            {
                handle.Dispose();
            }
        }
    }
#endif

}
