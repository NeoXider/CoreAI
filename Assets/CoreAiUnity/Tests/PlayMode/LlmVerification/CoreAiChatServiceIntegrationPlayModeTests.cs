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
#if COREAI_LLM && !UNITY_WEBGL
    public sealed class CoreAiChatServiceIntegrationPlayModeTests
    {
        private sealed class DummyGameCommandSink : IAiGameCommandSink
        {
            public readonly List<ApplyAiGameCommand> Items = new();

            public void Publish(ApplyAiGameCommand command)
            {
                Items.Add(command);
            }
        }

        private sealed class TestInventoryProvider : InventoryTool.IInventoryProvider
        {
            public List<InventoryTool.InventoryItem> Inventory { get; } = new()
            {
                new InventoryTool.InventoryItem { Name = "Magic Staff", Type = "weapon", Quantity = 1, Price = 100 }
            };

            public bool WasInvoked { get; private set; }

            public void ResetInvocation()
            {
                WasInvoked = false;
            }

            public Task<List<InventoryTool.InventoryItem>> GetInventoryAsync(
                System.Threading.CancellationToken cancellationToken)
            {
                WasInvoked = true;
                return Task.FromResult(Inventory);
            }
        }

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator ChatService_Integration_AllModesAndAgentSwapping()
        {
            Debug.Log("[ChatServiceIntegration] ===== TEST START =====");

            if (!PlayModeProductionLikeLlmFactory.TryCreate(
                    null, 0.3f, 240, out PlayModeProductionLikeLlmHandle handle, out string ignore))
            {
                Assert.Ignore(ignore);
            }

            try
            {
                yield return PlayModeProductionLikeLlmFactory.EnsureLlmUnityModelReady(handle);
                Debug.Log($"[ChatServiceIntegration] Backend: {handle.ResolvedBackend}");

                if (handle.ResolvedBackend == PlayModeProductionLikeLlmBackend.Offline)
                {
                    Assert.Ignore(
                        "Tools-Only / Hybrid tool-calling verification requires a live LLM backend (HTTP or LLMUnity).");
                }

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
                TestInventoryProvider toolOnlyProvider = new();
                TestInventoryProvider hybridProvider = new();
                policy.SetToolsForRole("MerchantToolOnly",
                    new List<ILlmTool> { new InventoryLlmTool(toolOnlyProvider) });
                policy.SetToolsForRole("MerchantHybrid",
                    new List<ILlmTool> { new InventoryLlmTool(hybridProvider) });
                policy.SetToolsForRole("SimpleChatOnly", new List<ILlmTool>());
                policy.SetStreamingEnabled("SimpleChatOnly", false);

                AiOrchestrator orchestrator = new(
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

                TestSettings settingsAsset = new() { EnableStreaming = true };
                CoreAiChatService chatService = new(orchestrator, policy, settingsAsset, store, null);

                // --- 1. Chat Only ---
                Debug.Log("[ChatServiceIntegration] Mode: Chat Only");
                string chatOnlyResponse = null;
                Task<string> t1 = chatService.SendMessageSmartAsync("Hello, who are you?", "SimpleChatOnly",
                    c =>
                    {
                        if (c.IsDone)
                        {
                            chatOnlyResponse += "";
                        }
                        else
                        {
                            chatOnlyResponse += c.Text;
                        }
                    });
                yield return PlayModeTestAwait.WaitTask(t1, 240f, "Chat Only");
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
                toolOnlyProvider.ResetInvocation();
                string toolOnlyResponse = null;
                Task<string> t2 = chatService.SendMessageSmartAsync(
                    "What is in your inventory? Just use the tool, don't say anything else.", "MerchantToolOnly",
                    c =>
                    {
                        if (!c.IsDone)
                        {
                            toolOnlyResponse += c.Text;
                        }
                    });
                yield return PlayModeTestAwait.WaitTask(t2, 240f, "Tools Only");

                // WHY: assert on evidence the tool actually ran (provider invoked) or that its concrete
                // result surfaced in the reply, not on envelope/reply publication which is true for any answer.
                bool calledTool = toolOnlyProvider.WasInvoked ||
                                  (toolOnlyResponse != null &&
                                   toolOnlyResponse.Contains("Staff", StringComparison.OrdinalIgnoreCase));

                Assert.IsTrue(calledTool,
                    "Tools-Only mode must actually invoke the inventory tool (provider GetInventoryAsync) or " +
                    "surface the concrete tool result (inventory item 'Staff') in the reply.");

                // --- 3. Hybrid (Chat + Tools) ---
                Debug.Log("[ChatServiceIntegration] Mode: Hybrid");
                hybridProvider.ResetInvocation();
                string hybridResponse = null;
                sink.Items.Clear();
                Task<string> t3 = chatService.SendMessageSmartAsync(
                    "Tell me a short joke and then check your inventory.", "MerchantHybrid",
                    c =>
                    {
                        if (!c.IsDone)
                        {
                            hybridResponse += c.Text;
                        }
                    });
                yield return PlayModeTestAwait.WaitTask(t3, 240f, "Hybrid");
                // WHY: same as Tools-Only - require real tool invocation (or its concrete result), not reply publication.
                bool hybridCalledTool = hybridProvider.WasInvoked ||
                                        (hybridResponse != null &&
                                         hybridResponse.Contains("Staff", StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrEmpty(hybridResponse))
                {
                    hybridResponse = t3.Result;
                }

                Assert.IsNotEmpty(hybridResponse, "Hybrid response should not be empty");
                Assert.IsTrue(hybridCalledTool,
                    "Hybrid mode must actually invoke the inventory tool (provider GetInventoryAsync) or surface " +
                    "the concrete tool result (inventory item 'Staff') alongside the chat reply.");

                // --- 4. Agent Swapping ---
                Debug.Log("[ChatServiceIntegration] Mode: Agent Swapping");
                string swappedResponse = "";
                yield return SendAndCapture(
                    chatService,
                    "Return one short non-empty sentence.",
                    "SimpleChatOnly",
                    240f,
                    "Agent Swapping",
                    value => swappedResponse = value);

                Assert.IsNotEmpty(swappedResponse, "Swapped response should not be empty");

                Debug.Log("[ChatServiceIntegration] ===== TEST PASSED =====");
            }
            finally
            {
                handle.Dispose();
            }
        }

        private static IEnumerator SendAndCapture(
            CoreAiChatService chatService,
            string text,
            string roleId,
            float timeoutSeconds,
            string label,
            Action<string> capture)
        {
            string response = "";
            Task<string> task = chatService.SendMessageSmartAsync(text, roleId, c =>
            {
                if (!c.IsDone && !string.IsNullOrEmpty(c.Text))
                {
                    response += c.Text;
                }
            });
            yield return PlayModeTestAwait.WaitTask(task, timeoutSeconds, label);
            if (string.IsNullOrWhiteSpace(response))
            {
                response = task.Result ?? "";
            }

            capture(response);
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
            public float LlmRequestTimeoutSeconds => 240f;
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
