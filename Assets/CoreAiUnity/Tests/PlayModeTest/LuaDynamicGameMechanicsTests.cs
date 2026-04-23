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
    /// Р”РµРјРѕРЅСЃС‚СЂРёСЂСѓРµС‚ РёР·РјРµРЅРµРЅРёРµ РёРіСЂРѕРІРѕР№ Р»РѕРіРёРєРё (РЅР°РїРёСЃР°РЅРЅРѕР№ РЅР° Lua) РїСЂСЏРјРѕ "РЅР° Р»РµС‚Сѓ" СЃ РїРѕРјРѕС‰СЊСЋ Р°РіРµРЅС‚Р°.
    /// РђРіРµРЅС‚ РјРѕР¶РµС‚ РїРµСЂРµРїРёСЃР°С‚СЊ РіР»РѕР±Р°Р»СЊРЅС‹Рµ С„СѓРЅРєС†РёРё (РЅР°РїСЂРёРјРµСЂ, С„РѕСЂРјСѓР»Сѓ СѓСЂРѕРЅР°), 
    /// С‡С‚Рѕ РјРіРЅРѕРІРµРЅРЅРѕ РѕС‚СЂР°Р·РёС‚СЃСЏ РЅР° РїСЂР°РІРёР»Р°С… РёРіСЂС‹.
    /// </summary>
#if !COREAI_NO_LLM && !UNITY_WEBGL
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
            Debug.Log("[LuaDynamic] в•ђв•ђв•ђ LUA MECHANICS MODIFICATION TEST START в•ђв•ђв•ђ");

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

                // 1. РРЅРёС†РёР°Р»РёР·РёСЂСѓРµРј РѕР±С‰РёР№ Lua Executor СЃ "РјРѕС‚РѕСЂРЅРѕР№" Р»РѕРіРёРєРѕР№ (РёР·РЅР°С‡Р°Р»СЊРЅР°СЏ РјРµС…Р°РЅРёРєР°)
                SharedLuaExecutor executor = new();
                const string INITIAL_LOGIC = @"
-- Р‘Р°Р·РѕРІС‹Р№ СѓСЂРѕРЅ, РєРѕС‚РѕСЂС‹Р№ РЅР°РЅРѕСЃРёС‚ РёРіСЂРѕРє
function calculate_damage()
    return 10
end
";
                // Р—Р°РїСѓСЃРєР°РµРј РЅР°С‡Р°Р»СЊРЅСѓСЋ Р»РѕРіРёРєСѓ
                executor.ExecuteAsync(INITIAL_LOGIC, default).GetAwaiter().GetResult();

                // РџСЂРѕРІРµСЂСЏРµРј, С‡С‚Рѕ РґРѕ РІРјРµС€Р°С‚РµР»СЊСЃС‚РІР° Р°РіРµРЅС‚Р° СѓСЂРѕРЅ СЂР°РІРµРЅ 10
                double initialDamage = executor.CallFunctionCurrent("calculate_damage");
                Debug.Log($"[LuaDynamic] Initial calculate_damage() = {initialDamage}");
                Assert.AreEqual(10.0, initialDamage, "Initial damage should be 10");

                // 2. РќР°СЃС‚СЂР°РёРІР°РµРј РђРіРµРЅС‚Р°-Р“РµР№РјРјР°СЃС‚РµСЂР°, РєРѕС‚РѕСЂС‹Р№ РёРјРµРµС‚ РґРѕСЃС‚СѓРї Рє execute_lua
                CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
                
                // Р”РѕР±Р°РІР»СЏРµРј РёРЅСЃС‚СЂСѓРјРµРЅС‚
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

                // 3. Р”Р°С‘Рј Р·Р°РґР°РЅРёРµ РР: "РРіСЂРѕРєРё Р¶Р°Р»СѓСЋС‚СЃСЏ, С‡С‚Рѕ РёРіСЂР° СЃР»РѕР¶РЅР°СЏ. РџРѕРґРЅРёРјРё СѓСЂРѕРЅ РІ 5 СЂР°Р·."
                string prompt = "Players are complaining that the game is too hard. " +
                                "Change the 'calculate_damage()' lua function to return 50 instead of 10.\n" +
                                "You MUST use the 'execute_lua' tool to redefine the function globally.\n" +
                                "Example code: \nfunction calculate_damage() return 50 end";

                Debug.Log($"[LuaDynamic] рџ“¤ PROMPT: {prompt}");

                Task t = orch.RunTaskAsync(new AiTaskRequest
                {
                    RoleId = "GameMaster",
                    Hint = prompt
                });

                yield return PlayModeTestAwait.WaitTask(t, 240f, "modify lua mechanics");

                // 4. РџСЂРѕРІРµСЂСЏРµРј, С‡С‚Рѕ РР РїРµСЂРµР·Р°РїРёСЃР°Р» С„СѓРЅРєС†РёСЋ Рё Р»РѕРіРёРєР° РёРіСЂС‹ РёР·РјРµРЅРёР»Р°СЃСЊ!
                double modifiedDamage = executor.CallFunctionCurrent("calculate_damage");
                Debug.Log($"[LuaDynamic] Modified calculate_damage() = {modifiedDamage}");

                Assert.AreEqual(50.0, modifiedDamage, 
                    "AI must successfully rewrite the lua logic to return 50!");

                Debug.Log("[LuaDynamic] вњ“ AI successfully modified game logic at runtime!");
            }
            finally
            {
                handle.Dispose();
            }
        }
    }
#endif

}
