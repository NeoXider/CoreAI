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
    /// PlayMode : Merchant ()  get_inventory    .
    ///   NPC  :  + .
    /// </summary>
#if COREAI_LLM && !UNITY_WEBGL
    public sealed class MerchantWithToolCallingPlayModeTests
    {
        private const int LlmTurnTimeoutSeconds = 240;
        private const int LiveModelMaxOutputTokens = 128000;

        // WHY: 120 s optional GGUF load + one 240 s tool turn = 360 s; 420 s leaves the 20 s
        // LiveTestRequestScope reserve plus margin (the former 300 s could abort mid-turn after a slow load).
        private const int TestTimeoutMs = 420_000;

        private LiveTestRequestScope _requests;
        private PlayModeProductionLikeLlmHandle _handle;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _requests = new LiveTestRequestScope(TestTimeoutMs);
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

            _handle?.Dispose();
            _handle = null;
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

        private sealed class ListSink : IAiGameCommandSink
        {
            public readonly List<ApplyAiGameCommand> Items = new();

            public void Publish(ApplyAiGameCommand command)
            {
                Items.Add(command);
            }
        }

        /// <summary>
        /// Fake inventory   .
        /// </summary>
        private sealed class TestInventoryProvider : InventoryTool.IInventoryProvider
        {
            public List<InventoryTool.InventoryItem> Inventory { get; } = new();
            public int CallCount { get; private set; }

            public Task<List<InventoryTool.InventoryItem>> GetInventoryAsync(
                CancellationToken cancellationToken)
            {
                CallCount++;
                return Task.FromResult(Inventory);
            }
        }

        private sealed class CapturingLlmClient : ILlmClient
        {
            private readonly ILlmClient _inner;
            public string LastSystemPrompt;
            public string LastUserPayload;
            public string LastContent;

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

        /// <summary>
        /// :   " ", Chat Agent  get_inventory     .
        /// </summary>
        [UnityTest]
        [Timeout(TestTimeoutMs)]
        public IEnumerator ChatAgent_CallsInventoryTool_ThenRespondsWithItems()
        {
            Debug.Log("[ChatWithToolCalling]  TEST START ");

            if (!PlayModeProductionLikeLlmFactory.TryCreate(
                    null,
                    0.3f,
                    LlmTurnTimeoutSeconds,
                    out PlayModeProductionLikeLlmHandle handle,
                    out string ignore))
            {
                Assert.Ignore(ignore);
            }

            // WHY: disposed in [UnityTearDown] after the request drain, never under a still-running turn.
            _handle = handle;

            yield return PlayModeProductionLikeLlmFactory.EnsureLlmUnityModelReady(handle);
            Debug.Log($"[ChatWithToolCalling] Backend: {handle.ResolvedBackend}");

            //
            TestInventoryProvider testInventory = new();
            testInventory.Inventory.Add(new InventoryTool.InventoryItem
                { Name = "Iron Sword", Type = "weapon", Quantity = 3, Price = 50 });
            testInventory.Inventory.Add(new InventoryTool.InventoryItem
                { Name = "Health Potion", Type = "consumable", Quantity = 10, Price = 25 });
            testInventory.Inventory.Add(new InventoryTool.InventoryItem
                { Name = "Leather Armor", Type = "armor", Quantity = 2, Price = 100 });

            InMemoryStore store = new();
            AgentMemoryPolicy policy = new();
            SessionTelemetryCollector telemetry = new();
            AiPromptComposer composer = new(
                new BuiltInDefaultAgentSystemPromptProvider(),
                new NoAgentUserPromptTemplateProvider(),
                new NullLuaScriptVersionStore());

            ListSink sink = new();

            //     MemoryStore   capturing
            ILlmClient clientWithMemory = handle.WrapWithMemoryStore(store);
            CapturingLlmClient capturingLlm = new(clientWithMemory);

            //    InventoryTool
            AiOrchestrator orch = CreateOrchestratorWithInventory(
                capturingLlm, store, policy, telemetry, composer, sink, testInventory);

            //
            string playerMessage = "I want to buy something. What do you have?";

            Debug.Log($"[ChatWithToolCalling] ");
            Debug.Log($"[ChatWithToolCalling]  PLAYER MESSAGE:");
            Debug.Log($"[ChatWithToolCalling] {playerMessage}");
            Debug.Log($"[ChatWithToolCalling] ");

            CancellationTokenSource cts = _requests.CreateCancellation();
            Task t = _requests.Track(orch.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Merchant,
                Hint = playerMessage,
                MaxOutputTokens = LiveModelMaxOutputTokens
            }, cts.Token));

            yield return PlayModeTestAwait.WaitTask(t, _requests.Cap(LlmTurnTimeoutSeconds), "chat with tool calling", cts);

            Debug.Log($"[ChatWithToolCalling]  AGENT RESPONSE:");
            Debug.Log($"[ChatWithToolCalling] Content: {capturingLlm.LastContent}");
            Debug.Log($"[ChatWithToolCalling] Commands produced: {sink.Items.Count}");

            //     -
            bool responseMentionsItems =
                capturingLlm.LastContent?.Contains("Sword", StringComparison.OrdinalIgnoreCase) == true ||
                capturingLlm.LastContent?.Contains("Potion", StringComparison.OrdinalIgnoreCase) == true ||
                capturingLlm.LastContent?.Contains("Armor", StringComparison.OrdinalIgnoreCase) == true ||
                capturingLlm.LastContent?.Contains("inventory", StringComparison.OrdinalIgnoreCase) == true ||
                capturingLlm.LastContent?.Contains("items", StringComparison.OrdinalIgnoreCase) == true;

            Assert.Greater(testInventory.CallCount, 0,
                "Merchant must call the inventory tool before answering available items.");
            Assert.IsTrue(responseMentionsItems,
                $"Merchant should answer with available inventory items after tool use. Response: {capturingLlm.LastContent}");

            Debug.Log("[ChatWithToolCalling]  TEST PASSED ");
        }

        private static AiOrchestrator CreateOrchestratorWithInventory(
            ILlmClient client,
            IAgentMemoryStore store,
            AgentMemoryPolicy policy,
            SessionTelemetryCollector telemetry,
            AiPromptComposer composer,
            IAiGameCommandSink sink,
            InventoryTool.IInventoryProvider inventoryProvider)
        {
            new AgentBuilder(BuiltInAgentRoleIds.Merchant)
                .WithMode(AgentMode.ToolsAndChat)
                .WithMemory(MemoryToolAction.Append)
                .WithTool(new InventoryLlmTool(inventoryProvider))
                .Build()
                .ApplyToPolicy(policy);

            return new AiOrchestrator(
                new SoloAuthorityHost(),
                client,
                sink,
                telemetry,
                composer,
                store,
                policy,
                new NoOpRoleStructuredResponsePolicy(),
                new NullAiOrchestrationMetrics(), ScriptableObject.CreateInstance<CoreAISettingsAsset>(),
                new LocalActorIdentityProvider("chat-tool-calling-test"));
        }
    }
#endif
}
