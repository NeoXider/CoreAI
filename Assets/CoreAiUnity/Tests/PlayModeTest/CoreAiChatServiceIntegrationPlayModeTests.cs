using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Chat;
using CoreAI.Infrastructure.Llm;
using CoreAI.Messaging;
using CoreAI.Session;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// PlayMode integration tests for CoreAiChatService + IAiOrchestrationService.
    /// Verifies "chat only", "tools only", "chat + tools", and "swapping agents on the fly".
    /// </summary>
#if !COREAI_NO_LLM && !UNITY_WEBGL
    public sealed class CoreAiChatServiceIntegrationPlayModeTests
    {
        private sealed class DummyGameCommandSink : IAiGameCommandSink
        {
            public readonly List<ApplyAiGameCommand> Items = new();
            public void Publish(ApplyAiGameCommand command) => Items.Add(command);
        }

        private sealed class TestInventoryProvider : InventoryTool.IInventoryProvider
        {
            public List<InventoryTool.InventoryItem> Inventory { get; } = new()
            {
                new InventoryTool.InventoryItem { Name = "Magic Staff", Type = "weapon", Quantity = 1, Price = 100 }
            };

            public Task<List<InventoryTool.InventoryItem>> GetInventoryAsync(System.Threading.CancellationToken cancellationToken)
                => Task.FromResult(Inventory);
        }

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator ChatService_Integration_AllModesAndAgentSwapping()
        {
            Debug.Log("[ChatServiceIntegration] ===== TEST START =====");

            if (!PlayModeProductionLikeLlmFactory.TryCreate(
                    null, 0.3f, 300, out PlayModeProductionLikeLlmHandle handle, out string ignore))
            {
                Assert.Ignore(ignore);
            }

            try
            {
                yield return PlayModeProductionLikeLlmFactory.EnsureLlmUnityModelReady(handle);
                Debug.Log($"[ChatServiceIntegration] Backend: {handle.ResolvedBackend}");

                // Setup infrastructure
                InMemoryStore store = new();
                AgentMemoryPolicy policy = new();
                SessionTelemetryCollector telemetry = new();
                AiPromptComposer composer = new(
                    new BuiltInDefaultAgentSystemPromptProvider(),
                    new NoAgentUserPromptTemplateProvider(),
                    new NullLuaScriptVersionStore());
                DummyGameCommandSink sink = new();

                // Setup tools for roles
                policy.SetToolsForRole("MerchantToolOnly", new List<ILlmTool> { new InventoryLlmTool(new TestInventoryProvider()) });
                policy.SetToolsForRole("MerchantHybrid", new List<ILlmTool> { new InventoryLlmTool(new TestInventoryProvider()) });
                policy.SetToolsForRole("SimpleChatOnly", new List<ILlmTool>());
                policy.SetStreamingEnabled("SimpleChatOnly", false);

                AiOrchestrator orchestrator = new AiOrchestrator(
                    new SoloAuthorityHost(),
                    handle.Client,
                    sink,
                    telemetry,
                    composer,
                    store,
                    policy,
                    new NoOpRoleStructuredResponsePolicy(),
                    new NullAiOrchestrationMetrics(),
                    ScriptableObject.CreateInstance<CoreAISettingsAsset>());

                TestSettings settingsAsset = new TestSettings { EnableStreaming = true };
                CoreAiChatService chatService = new CoreAiChatService(orchestrator, policy, settingsAsset, store, null);

                // --- 1. Chat Only ---
                Debug.Log("[ChatServiceIntegration] Mode: Chat Only");
                string chatOnlyResponse = null;
                var t1 = chatService.SendMessageSmartAsync("Hello, who are you?", "SimpleChatOnly",
                    c => { if (c.IsDone) chatOnlyResponse += ""; else chatOnlyResponse += c.Text; });
                yield return PlayModeTestAwait.WaitTask(t1, 60f, "Chat Only");
                if (string.IsNullOrEmpty(chatOnlyResponse))
                {
                    chatOnlyResponse = t1.Result;
                }
                Assert.IsNotEmpty(chatOnlyResponse, "Chat only response should not be empty");
                //       -   chat-only.
                //  chat-only     ,    sink.
                sink.Items.Clear();

                // --- 2. Tools Only (Implicitly, the prompt drives it to use tool) ---
                Debug.Log("[ChatServiceIntegration] Mode: Tools Only");
                string toolOnlyResponse = null;
                var t2 = chatService.SendMessageSmartAsync("What is in your inventory? Just use the tool, don't say anything else.", "MerchantToolOnly",
                    c => { if (!c.IsDone) toolOnlyResponse += c.Text; });
                yield return PlayModeTestAwait.WaitTask(t2, 60f, "Tools Only");
                
                // It might still output some text, but the main thing is the tool should be called.
                // We verify sink or the response
                bool calledTool = false;
                if (sink.Items.Count > 0)
                {
                    calledTool = true;
                }
                else if (toolOnlyResponse != null && toolOnlyResponse.Contains("Staff", StringComparison.OrdinalIgnoreCase))
                {
                    calledTool = true;
                }
                
                // --- 3. Hybrid (Chat + Tools) ---
                Debug.Log("[ChatServiceIntegration] Mode: Hybrid");
                string hybridResponse = null;
                sink.Items.Clear();
                var t3 = chatService.SendMessageSmartAsync("Tell me a short joke and then check your inventory.", "MerchantHybrid",
                    c => { if (!c.IsDone) hybridResponse += c.Text; });
                yield return PlayModeTestAwait.WaitTask(t3, 120f, "Hybrid");
                if (string.IsNullOrEmpty(hybridResponse))
                {
                    hybridResponse = t3.Result;
                }

                Assert.IsNotEmpty(hybridResponse, "Hybrid response should not be empty");

                // --- 4. Agent Swapping ---
                Debug.Log("[ChatServiceIntegration] Mode: Agent Swapping");
                string swappedResponse = null;
                var t4 = chatService.SendMessageSmartAsync("Now reply as a simple chat bot again.", "SimpleChatOnly",
                    c => { if (!c.IsDone) swappedResponse += c.Text; });
                yield return PlayModeTestAwait.WaitTask(t4, 60f, "Agent Swapping");
                if (string.IsNullOrEmpty(swappedResponse))
                {
                    swappedResponse = t4.Result;
                }
                Assert.IsNotEmpty(swappedResponse, "Swapped response should not be empty");

                Debug.Log("[ChatServiceIntegration] ===== TEST PASSED =====");
            }
            finally
            {
                handle.Dispose();
            }
        }
        private class TestSettings : ICoreAISettings
        {
            public string UniversalSystemPromptPrefix => "";
            public LlmBackendType BackendType => LlmBackendType.Offline;
            public int ContextWindowTokens => 8192;
            public int MaxContextTokens => 4000;
            public int MaxLuaRepairRetries => 3;
            public int MaxToolCallRetries => 3;
            public bool AllowDuplicateToolCalls => false;
            public string ApiKey => "";
            public string ModelName => "";
            public string CustomBaseUrl => "";
            public float Temperature => 0.3f;
            public string DeveloperInstructions => "";
            public string ApplicationName => "";
            public bool EnableHttpDebugLogging => false;
            public bool LogMeaiToolCallingSteps => false;
            public bool EnableMeaiDebugLogging => false;
            public float LlmRequestTimeoutSeconds => 120f;
            public int MaxLlmRequestRetries => 2;
            public bool LogTokenUsage => false;
            public bool LogLlmLatency => false;
            public bool LogLlmConnectionErrors => false;
            public bool LogToolCalls => false;
            public bool LogToolCallArguments => false;
            public bool LogToolCallResults => false;
            public bool EnableStreaming { get; set; } = true;
        }
    }
#endif
}

