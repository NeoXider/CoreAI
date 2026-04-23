#if !COREAI_NO_LLM && !UNITY_WEBGL
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Crafting;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using CoreAI.Messaging;
using CoreAI.Session;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// PlayMode С‚РµСЃС‚С‹ РґР»СЏ CompatibilityLlmTool СЃ СЂРµР°Р»СЊРЅРѕР№ LLM.
    /// РџСЂРѕРІРµСЂСЏРµС‚ С‡С‚Рѕ РјРѕРґРµР»СЊ РјРѕР¶РµС‚ РІС‹Р·РІР°С‚СЊ check_compatibility tool Рё РёРЅС‚РµСЂРїСЂРµС‚РёСЂРѕРІР°С‚СЊ СЂРµР·СѓР»СЊС‚Р°С‚.
    /// </summary>
    public sealed class CompatibilityToolPlayModeTests
    {
        /// <summary>
        /// РўРµСЃС‚: LLM РІС‹Р·С‹РІР°РµС‚ check_compatibility РґР»СЏ СЃРѕРІРјРµСЃС‚РёРјС‹С… РёРЅРіСЂРµРґРёРµРЅС‚РѕРІ Рё СЃРѕРѕР±С‰Р°РµС‚ СЂРµР·СѓР»СЊС‚Р°С‚.
        /// </summary>
        [UnityTest]
        [Timeout(300000)]
        public IEnumerator CompatibilityTool_CompatibleIngredients_LlmReportsCompatible()
        {
            Debug.Log("[CompatibilityTest] в•ђв•ђв•ђ TEST 1: Compatible ingredients в•ђв•ђв•ђ");
            if (!PlayModeProductionLikeLlmFactory.TryCreate(null, 0.1f, 300,
                    out PlayModeProductionLikeLlmHandle handle, out string ignore))
            {
                Assert.Ignore(ignore);
            }

            try
            {
                yield return PlayModeProductionLikeLlmFactory.EnsureLlmUnityModelReady(handle);

                // РЎРѕР·РґР°С‘Рј checker СЃ СЃРёРЅРµСЂРіРёРµР№ Fire+Earth
                CompatibilityChecker checker = new();
                checker.AddRule("Fire", "Earth", 1.5f, "Fire and Earth create lava вЂ” bonus synergy");
                CompatibilityLlmTool tool = new(checker);

                AgentConfig agent = new AgentBuilder("TestCompatChecker")
                    .WithSystemPrompt(
                        "You are a crafting assistant. When the user asks to check ingredients, " +
                        "call the check_compatibility tool with the ingredient names as a comma-separated string. " +
                        "Then report whether they are compatible and mention the score.")
                    .WithTool(tool)
                    .WithMode(AgentMode.ToolsAndChat)
                    .Build();

                ILlmClient clientWithStore = handle.WrapWithMemoryStore(new InMemoryStore());
                Task<TestResult> task =
                    RunAgentTestAsync(clientWithStore, agent, "Check if Fire and Earth are compatible");
                yield return PlayModeTestAwait.WaitTask(task, 240f, "compatibility_compatible");

                TestResult r = task.Result;
                Debug.Log(
                    $"[CompatibilityTest] Tools: {r.ToolsCount}, Response: {r.Response?.Substring(0, Math.Min(120, r.Response?.Length ?? 0))}");
                Assert.Greater(r.ToolsCount, 0, "Agent should have the compatibility tool");
                Assert.IsNotNull(r.Response, "Agent should return a response");
                Debug.Log("[CompatibilityTest] вњ“ TEST 1 PASSED");
            }
            finally
            {
                handle.Dispose();
            }
        }

        /// <summary>
        /// РўРµСЃС‚: LLM РІС‹Р·С‹РІР°РµС‚ check_compatibility РґР»СЏ РЅРµСЃРѕРІРјРµСЃС‚РёРјС‹С… РёРЅРіСЂРµРґРёРµРЅС‚РѕРІ.
        /// </summary>
        [UnityTest]
        [Timeout(300000)]
        public IEnumerator CompatibilityTool_IncompatibleIngredients_LlmReportsIncompatible()
        {
            Debug.Log("[CompatibilityTest] в•ђв•ђв•ђ TEST 2: Incompatible ingredients в•ђв•ђв•ђ");
            if (!PlayModeProductionLikeLlmFactory.TryCreate(null, 0.1f, 300,
                    out PlayModeProductionLikeLlmHandle handle, out string ignore))
            {
                Assert.Ignore(ignore);
            }

            try
            {
                yield return PlayModeProductionLikeLlmFactory.EnsureLlmUnityModelReady(handle);

                CompatibilityChecker checker = new();
                checker.AddRule("Fire", "Water", 0f, "Fire and Water cancel each other out");
                CompatibilityLlmTool tool = new(checker);

                AgentConfig agent = new AgentBuilder("TestIncompatChecker")
                    .WithSystemPrompt(
                        "You are a crafting assistant. When the user asks to check ingredients, " +
                        "call the check_compatibility tool with the ingredient names as a comma-separated string. " +
                        "Report the result: whether compatible or not, and warnings.")
                    .WithTool(tool)
                    .WithMode(AgentMode.ToolsAndChat)
                    .Build();

                ILlmClient clientWithStore = handle.WrapWithMemoryStore(new InMemoryStore());
                Task<TestResult> task =
                    RunAgentTestAsync(clientWithStore, agent, "Check if Fire and Water are compatible");
                yield return PlayModeTestAwait.WaitTask(task, 240f, "compatibility_incompatible");

                TestResult r = task.Result;
                Debug.Log(
                    $"[CompatibilityTest] Tools: {r.ToolsCount}, Response: {r.Response?.Substring(0, Math.Min(120, r.Response?.Length ?? 0))}");
                Assert.Greater(r.ToolsCount, 0, "Agent should have the compatibility tool");
                Assert.IsNotNull(r.Response, "Agent should return a response");
                Debug.Log("[CompatibilityTest] вњ“ TEST 2 PASSED");
            }
            finally
            {
                handle.Dispose();
            }
        }

        /// <summary>
        /// РўРµСЃС‚: LLM РїСЂРѕРІРµСЂСЏРµС‚ 3 РёРЅРіСЂРµРґРёРµРЅС‚Р° СЃ РіСЂСѓРїРїРѕРІС‹Рј РїСЂР°РІРёР»РѕРј.
        /// </summary>
        [UnityTest]
        [Timeout(300000)]
        public IEnumerator CompatibilityTool_ThreeIngredients_GroupRule()
        {
            Debug.Log("[CompatibilityTest] в•ђв•ђв•ђ TEST 3: Three ingredients group rule в•ђв•ђв•ђ");
            if (!PlayModeProductionLikeLlmFactory.TryCreate(null, 0.1f, 300,
                    out PlayModeProductionLikeLlmHandle handle, out string ignore))
            {
                Assert.Ignore(ignore);
            }

            try
            {
                yield return PlayModeProductionLikeLlmFactory.EnsureLlmUnityModelReady(handle);

                CompatibilityChecker checker = new();
                checker.AddGroupRule(1.8f, "Fire+Earth+Air create a volcanic eruption вЂ” amazing synergy!",
                    "Fire", "Earth", "Air");
                CompatibilityLlmTool tool = new(checker);

                AgentConfig agent = new AgentBuilder("TestTripleChecker")
                    .WithSystemPrompt(
                        "You are a crafting assistant. Call the check_compatibility tool with " +
                        "the ingredient names separated by commas. Report the compatibility result.")
                    .WithTool(tool)
                    .WithMode(AgentMode.ToolsAndChat)
                    .Build();

                ILlmClient clientWithStore = handle.WrapWithMemoryStore(new InMemoryStore());
                Task<TestResult> task =
                    RunAgentTestAsync(clientWithStore, agent, "Check compatibility of Fire, Earth, Air");
                yield return PlayModeTestAwait.WaitTask(task, 240f, "compatibility_triple");

                TestResult r = task.Result;
                Debug.Log(
                    $"[CompatibilityTest] Tools: {r.ToolsCount}, Response: {r.Response?.Substring(0, Math.Min(120, r.Response?.Length ?? 0))}");
                Assert.Greater(r.ToolsCount, 0, "Agent should have the compatibility tool");
                Assert.IsNotNull(r.Response, "Agent should return a response");
                Debug.Log("[CompatibilityTest] вњ“ TEST 3 PASSED");
            }
            finally
            {
                handle.Dispose();
            }
        }

        // в•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђ
        // Helpers
        // в•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђ

        private sealed class TestResult
        {
            public string Response { get; set; }
            public int ToolsCount { get; set; }
        }

        private sealed class InMemoryStore : IAgentMemoryStore
        {
            public readonly Dictionary<string, AgentMemoryState> States = new();

            public bool TryLoad(string roleId, out AgentMemoryState state)
            {
                return States.TryGetValue(roleId, out state);
            }

            public void Save(string roleId, AgentMemoryState state)
            {
                States[roleId] = state;
            }

            public void Clear(string roleId)
            {
                States.Remove(roleId);
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

        private sealed class CapturingLlmClient : ILlmClient
        {
            private readonly ILlmClient _inner;
            public string LastContent;
            public IReadOnlyList<ILlmTool> LastTools;

            public CapturingLlmClient(ILlmClient inner)
            {
                _inner = inner;
            }

            public async Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                LastTools = request.Tools;
                LlmCompletionResult result = await _inner.CompleteAsync(request, cancellationToken);
                if (result != null && result.Ok)
                {
                    LastContent = result.Content;
                }

                return result;
            }

            public void SetTools(IReadOnlyList<ILlmTool> tools)
            {
                _inner.SetTools(tools);
            }
        }

        private async Task<TestResult> RunAgentTestAsync(ILlmClient llm, AgentConfig cfg, string msg)
        {
            InMemoryStore store = new();
            AgentMemoryPolicy policy = new();
            cfg.ApplyToPolicy(policy);
            CapturingLlmClient cap = new(llm);
            AiOrchestrator orch = new(
                new SoloAuthorityHost(), cap,
                new NullSink(),
                new SessionTelemetryCollector(),
                new AiPromptComposer(new CustomPromptProvider(cfg.SystemPrompt),
                    new NoAgentUserPromptTemplateProvider(), new NullLuaScriptVersionStore()),
                store, policy, new NoOpRoleStructuredResponsePolicy(),
                new NullAiOrchestrationMetrics(),
                ScriptableObject.CreateInstance<CoreAISettingsAsset>());

            await orch.RunTaskAsync(new AiTaskRequest { RoleId = cfg.RoleId, Hint = msg });
            return new TestResult { Response = cap.LastContent, ToolsCount = cap.LastTools?.Count ?? 0 };
        }

        private sealed class CustomPromptProvider : IAgentSystemPromptProvider
        {
            private readonly string _p;

            public CustomPromptProvider(string p)
            {
                _p = p;
            }

            public bool TryGetSystemPrompt(string roleId, out string prompt)
            {
                prompt = _p;
                return !string.IsNullOrEmpty(prompt);
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
