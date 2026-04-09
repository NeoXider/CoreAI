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
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// PlayMode тест: Создание 3 типов кастомных агентов через AgentBuilder.
    /// </summary>
#if !COREAI_NO_LLM
    public sealed class CustomAgentsPlayModeTests
    {
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

        private sealed class ListSink : IAiGameCommandSink
        {
            public readonly List<ApplyAiGameCommand> Items = new();

            public void Publish(ApplyAiGameCommand command)
            {
                Items.Add(command);
            }
        }

        private sealed class CapturingLlmClient : ILlmClient
        {
            private readonly ILlmClient _inner;
            public string LastSystemPrompt;
            public string LastUserPayload;
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
                LastSystemPrompt = request.SystemPrompt;
                LastUserPayload = request.UserPayload;
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

        private sealed class TestResult
        {
            public string Response { get; set; }
            public int ToolsCount { get; set; }
        }

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator CustomAgent_Merchant_ToolsAndChat()
        {
            Debug.Log("[CustomAgents] ═══ TEST 1: MERCHANT (ToolsAndChat) ═══");
            if (!PlayModeProductionLikeLlmFactory.TryCreate(null, 0.3f, 300, out PlayModeProductionLikeLlmHandle handle,
                    out string ignore))
            {
                Assert.Ignore(ignore);
            }

            try
            {
                yield return PlayModeProductionLikeLlmFactory.EnsureLlmUnityModelReady(handle);
                TestInventoryProvider inv = new();
                inv.Items.Add(new InventoryTool.InventoryItem
                    { Name = "Iron Sword", Type = "weapon", Quantity = 3, Price = 50 });
                inv.Items.Add(new InventoryTool.InventoryItem
                    { Name = "Health Potion", Type = "consumable", Quantity = 10, Price = 25 });

                AgentConfig merchant = new AgentBuilder("TestMerchant")
                    .WithSystemPrompt("You are a shopkeeper. When asked about items, call get_inventory first.")
                    .WithTool(new InventoryLlmTool(inv))
                    .WithMemory()
                    .WithMode(AgentMode.ToolsAndChat)
                    .Build();

                // Обернуть клиент для правильной работы MemoryTool
                ILlmClient clientWithStore = handle.WrapWithMemoryStore(new InMemoryStore());
                Task<TestResult> task = RunAgentTestAsync(clientWithStore, merchant, "What items do you have?");
                yield return PlayModeTestAwait.WaitTask(task, 240f, "merchant"); // 240s для retry loop
                TestResult r = task.Result;
                Debug.Log(
                    $"[CustomAgents] MERCHANT Tools: {r.ToolsCount}, Response: {r.Response?.Substring(0, Math.Min(80, r.Response?.Length ?? 0))}");
                Assert.Greater(r.ToolsCount, 0, "Merchant should have tools");
                Assert.IsNotNull(r.Response);
                Debug.Log("[CustomAgents] ✓ TEST 1 PASSED");
            }
            finally
            {
                handle.Dispose();
            }
        }

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator CustomAgent_Analyzer_ToolsOnly()
        {
            Debug.Log("[CustomAgents] ═══ TEST 2: ANALYZER (ToolsOnly) ═══");
            if (!PlayModeProductionLikeLlmFactory.TryCreate(null, 0.2f, 300, out PlayModeProductionLikeLlmHandle handle,
                    out string ignore))
            {
                Assert.Ignore(ignore);
            }

            try
            {
                yield return PlayModeProductionLikeLlmFactory.EnsureLlmUnityModelReady(handle);
                AgentConfig analyzer = new AgentBuilder("TestAnalyzer")
                    .WithSystemPrompt("You analyze sessions. Call get_session_stats tool.")
                    .WithTool(new SessionStatsLlmTool())
                    .WithMode(AgentMode.ToolsOnly)
                    .Build();

                ILlmClient clientWithStore = handle.WrapWithMemoryStore(new InMemoryStore());
                Task<TestResult> task = RunAgentTestAsync(clientWithStore, analyzer, "Analyze session");
                yield return PlayModeTestAwait.WaitTask(task, 240f, "analyzer"); // 240s для retry loop
                TestResult r = task.Result;
                Debug.Log($"[CustomAgents] ANALYZER Tools: {r.ToolsCount}, Mode: {analyzer.Mode}");
                Assert.AreEqual(AgentMode.ToolsOnly, analyzer.Mode);
                Assert.Greater(r.ToolsCount, 0);
                Debug.Log("[CustomAgents] ✓ TEST 2 PASSED");
            }
            finally
            {
                handle.Dispose();
            }
        }

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator CustomAgent_Storyteller_ChatOnly()
        {
            Debug.Log("[CustomAgents] ═══ TEST 3: STORYTELLER (ChatOnly) ═══");
            if (!PlayModeProductionLikeLlmFactory.TryCreate(null, 0.4f, 300, out PlayModeProductionLikeLlmHandle handle,
                    out string ignore))
            {
                Assert.Ignore(ignore);
            }

            try
            {
                yield return PlayModeProductionLikeLlmFactory.EnsureLlmUnityModelReady(handle);
                AgentConfig storyteller = new AgentBuilder("TestStoryteller")
                    .WithSystemPrompt("You are a campfire storyteller. Share tales.")
                    .WithMemory(MemoryToolAction.Append)
                    .WithMode(AgentMode.ChatOnly)
                    .Build();

                ILlmClient clientWithStore = handle.WrapWithMemoryStore(new InMemoryStore());
                Task<TestResult> task = RunAgentTestAsync(clientWithStore, storyteller, "Tell me a story");
                yield return PlayModeTestAwait.WaitTask(task, 240f, "storyteller"); // 240s для retry loop
                TestResult r = task.Result;
                Debug.Log(
                    $"[CustomAgents] STORYTELLER Tools: {r.ToolsCount}, Response: {r.Response?.Substring(0, Math.Min(80, r.Response?.Length ?? 0))}");
                Assert.AreEqual(AgentMode.ChatOnly, storyteller.Mode);
                Assert.IsNotNull(r.Response);
                Debug.Log("[CustomAgents] ✓ TEST 3 PASSED");
            }
            finally
            {
                handle.Dispose();
            }
        }

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator CustomAgent_Helper_WithAction()
        {
            Debug.Log("[CustomAgents] ═══ TEST 4: HELPER (WithAction) ═══");
            if (!PlayModeProductionLikeLlmFactory.TryCreate(null, 0.2f, 300, out PlayModeProductionLikeLlmHandle handle,
                    out string ignore))
            {
                Assert.Ignore(ignore);
            }

            try
            {
                yield return PlayModeProductionLikeLlmFactory.EnsureLlmUnityModelReady(handle);
                bool triggerFired = false;
                string receivedMessage = string.Empty;

                AgentConfig helper = new AgentBuilder("TestHelper")
                    .WithSystemPrompt("Call the send_ping tool with the message 'hello'.")
                    .WithMode(AgentMode.ToolsOnly)
                    .WithAction("send_ping", "Send a ping message", new Action<string>((string message) =>
                    {
                        triggerFired = true;
                        receivedMessage = message;
                    }))
                    .Build();

                ILlmClient clientWithStore = handle.WrapWithMemoryStore(new InMemoryStore());
                Task<TestResult> task = RunAgentTestAsync(clientWithStore, helper, "Send ping");
                yield return PlayModeTestAwait.WaitTask(task, 240f, "helper");
                TestResult r = task.Result;

                Debug.Log(
                    $"[CustomAgents] HELPER Tools: {r.ToolsCount}, Fired: {triggerFired}, Msg: {receivedMessage}");
                Assert.Greater(r.ToolsCount, 0);
                Assert.IsTrue(triggerFired, "Delegate should have been triggered.");
                Debug.Log("[CustomAgents] ✓ TEST 4 PASSED");
            }
            finally
            {
                handle.Dispose();
            }
        }

        private async Task<TestResult> RunAgentTestAsync(ILlmClient llm, AgentConfig cfg, string msg)
        {
            InMemoryStore store = new();
            AgentMemoryPolicy policy = new();
            cfg.ApplyToPolicy(policy);
            ListSink sink = new();
            CapturingLlmClient cap = new(llm);
            AiOrchestrator orch = new(
                new SoloAuthorityHost(), cap, sink, new SessionTelemetryCollector(),
                new AiPromptComposer(new CustomAgentPromptProvider(cfg.SystemPrompt),
                    new NoAgentUserPromptTemplateProvider(), new NullLuaScriptVersionStore()),
                store, policy, new NoOpRoleStructuredResponsePolicy(), new NullAiOrchestrationMetrics(), UnityEngine.ScriptableObject.CreateInstance<CoreAI.Infrastructure.Llm.CoreAISettingsAsset>());

            await orch.RunTaskAsync(new AiTaskRequest { RoleId = cfg.RoleId, Hint = msg });
            return new TestResult { Response = cap.LastContent, ToolsCount = cap.LastTools?.Count ?? 0 };
        }

        private sealed class CustomAgentPromptProvider : IAgentSystemPromptProvider
        {
            private readonly string _p;

            public CustomAgentPromptProvider(string p)
            {
                _p = p;
            }

            public bool TryGetSystemPrompt(string roleId, out string prompt)
            {
                prompt = _p;
                return !string.IsNullOrEmpty(prompt);
            }
        }

        private sealed class TestInventoryProvider : InventoryTool.IInventoryProvider
        {
            public List<InventoryTool.InventoryItem> Items { get; } = new();

            public Task<List<InventoryTool.InventoryItem>> GetInventoryAsync(CancellationToken ct)
            {
                return Task.FromResult(Items);
            }
        }

        private sealed class SessionStatsLlmTool : ILlmTool
        {
            public string Name => "get_session_stats";
            public string Description => "Get session stats.";
            public bool AllowDuplicates => false;
            public string ParametersSchema => "{}";

            public Microsoft.Extensions.AI.AIFunction CreateAIFunction()
            {
                return Microsoft.Extensions.AI.AIFunctionFactory.Create(
                    (Func<CancellationToken, Task<object>>)(async ct =>
                    {
                        await Task.Delay(10, ct);
                        return new { waves = 5, kills = 42 };
                    }),
                    "get_session_stats",
                    "Get session statistics.");
            }
        }
    }
#endif
}

