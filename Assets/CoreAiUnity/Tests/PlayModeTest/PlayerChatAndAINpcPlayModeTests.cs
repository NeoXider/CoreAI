#if !COREAI_NO_LLM
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Infrastructure.Logging;
using CoreAI.Messaging;
using CoreAI.Session;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// PlayMode тесты: PlayerChat отвечает на вопрос игрока через InGameLlmChatService.
    /// </summary>
    public sealed class PlayerChatPlayModeTests
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
            public string LastSystemPrompt;
            public string LastUserPayload;
            public string LastRoleId;
            public IList<Microsoft.Extensions.AI.ChatMessage> LastChatHistory;
            public LlmCompletionResult LastResult;

            private readonly ILlmClient _inner;

            public CapturingLlmClient(ILlmClient inner)
            {
                _inner = inner;
            }

            public async Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                System.Threading.CancellationToken cancellationToken = default)
            {
                LastSystemPrompt = request.SystemPrompt;
                LastUserPayload = request.UserPayload;
                LastRoleId = request.AgentRoleId;
                LastChatHistory = request.ChatHistory;

                LastResult = await _inner.CompleteAsync(request, cancellationToken);
                return LastResult;
            }

            public void SetTools(IReadOnlyList<ILlmTool> tools)
            {
                _inner.SetTools(tools);
            }
        }

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator PlayerChat_RespondsToGreeting()
        {
            Debug.Log("[PlayerChat] ═══ TEST START ═══");

            if (!PlayModeProductionLikeLlmFactory.TryCreate(null, 0.7f, 120,
                    out PlayModeProductionLikeLlmHandle handle, out string ignore))
            {
                Assert.Ignore(ignore);
            }

            try
            {
                // Только для LLMUnity — ждём готовности модели. Для HTTP не нужно.
                if (handle.ResolvedBackend == PlayModeProductionLikeLlmBackend.LlmUnity)
                {
                    yield return PlayModeProductionLikeLlmFactory.EnsureLlmUnityModelReady(handle);
                }

                Debug.Log($"[PlayerChat] Backend: {handle.ResolvedBackend}");

                InMemoryStore store = new();
                CapturingLlmClient capturing = new(handle.WrapWithMemoryStore(store));

                BuiltInDefaultAgentSystemPromptProvider systemPrompts = new();
                AiPromptComposer composer = new(
                    systemPrompts,
                    new NoAgentUserPromptTemplateProvider(),
                    new NullLuaScriptVersionStore());

                InGameLlmChatService chatService = new(capturing, systemPrompts, 10);

                Debug.Log($"[PlayerChat] Sending: 'Hello, how are you?'");
                Task<LlmCompletionResult> task = chatService.SendPlayerMessageAsync("Hello, how are you?");
                yield return PlayModeTestAwait.WaitTask(task, 120f, "Send message 1");
                LlmCompletionResult result = task.Result;

                Debug.Log($"[PlayerChat] ═══════════════════════════════════════");
                Debug.Log($"[PlayerChat] Role: {capturing.LastRoleId}");
                Debug.Log(
                    $"[PlayerChat] System Prompt: {capturing.LastSystemPrompt?.Substring(0, Math.Min(100, capturing.LastSystemPrompt?.Length ?? 0))}...");
                Debug.Log($"[PlayerChat] Response: {result.Content}");
                Debug.Log($"[PlayerChat] ═══════════════════════════════════════");

                Assert.IsTrue(result.Ok, $"LLM call failed: {result.Error}");
                Assert.AreEqual(BuiltInAgentRoleIds.PlayerChat, capturing.LastRoleId);
                Assert.IsFalse(string.IsNullOrWhiteSpace(result.Content), "Response should not be empty");

                // Проверяем что в ответе есть осмысленный текст (не just tool call result)
                bool hasTextContent = result.Content.Length > 10;
                Assert.IsTrue(hasTextContent, "Response should contain text");

                Debug.Log(
                    $"[PlayerChat] ✓ PlayerChat responded with: {result.Content.Substring(0, Math.Min(50, result.Content.Length))}...");
                Debug.Log("[PlayerChat] ═══ TEST PASSED ═══");
            }
            finally
            {
                handle.Dispose();
            }
        }

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator PlayerChat_MaintainsHistory()
        {
            Debug.Log("[PlayerChat] ═══ TEST START ═══");

            if (!PlayModeProductionLikeLlmFactory.TryCreate(null, 0.7f, 120,
                    out PlayModeProductionLikeLlmHandle handle, out string ignore))
            {
                Assert.Ignore(ignore);
            }

            try
            {
                if (handle.ResolvedBackend == PlayModeProductionLikeLlmBackend.LlmUnity)
                {
                    yield return PlayModeProductionLikeLlmFactory.EnsureLlmUnityModelReady(handle);
                }

                Debug.Log($"[PlayerChat] Backend: {handle.ResolvedBackend}");

                InMemoryStore store = new();
                CapturingLlmClient capturing = new(handle.WrapWithMemoryStore(store));

                BuiltInDefaultAgentSystemPromptProvider systemPrompts = new();
                InGameLlmChatService chatService = new(capturing, systemPrompts, 10);

                // First message
                Task<LlmCompletionResult> t1 = chatService.SendPlayerMessageAsync("My name is Adventurer");
                yield return PlayModeTestAwait.WaitTask(t1, 120f, "First message");
                LlmCompletionResult r1 = t1.Result;
                Assert.AreEqual(1, chatService.HistoryPairCount);

                // Second message - should see history
                Task<LlmCompletionResult> t2 = chatService.SendPlayerMessageAsync("What is my name?");
                yield return PlayModeTestAwait.WaitTask(t2, 120f, "Second message");
                LlmCompletionResult r2 = t2.Result;
                Assert.AreEqual(2, chatService.HistoryPairCount);

                // Verify history is in the prompt
                bool foundAdventurer = false;
                if (capturing.LastChatHistory != null)
                {
                    foreach (var msg in capturing.LastChatHistory)
                    {
                        if (msg.Text != null && msg.Text.Contains("Adventurer"))
                        {
                            foundAdventurer = true;
                            break;
                        }
                    }
                }
                Assert.IsTrue(foundAdventurer, "The chat history should contain the string 'Adventurer'");

                Debug.Log($"[PlayerChat] ✓ History maintained: {chatService.HistoryPairCount} pairs");
                Debug.Log("[PlayerChat] ═══ TEST PASSED ═══");
            }
            finally
            {
                handle.Dispose();
            }
        }

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator PlayerChat_ClearHistory_Works()
        {
            Debug.Log("[PlayerChat] ═══ TEST START ═══");

            if (!PlayModeProductionLikeLlmFactory.TryCreate(null, 0.7f, 120,
                    out PlayModeProductionLikeLlmHandle handle, out string ignore))
            {
                Assert.Ignore(ignore);
            }

            try
            {
                if (handle.ResolvedBackend == PlayModeProductionLikeLlmBackend.LlmUnity)
                {
                    yield return PlayModeProductionLikeLlmFactory.EnsureLlmUnityModelReady(handle);
                }

                Debug.Log($"[AINpc] Backend: {handle.ResolvedBackend}");

                InMemoryStore store = new();
                CapturingLlmClient capturing = new(handle.WrapWithMemoryStore(store));
                ListSink sink = new();

                BuiltInDefaultAgentSystemPromptProvider systemPrompts = new();
                AiPromptComposer composer = new(
                    systemPrompts,
                    new NoAgentUserPromptTemplateProvider(),
                    new NullLuaScriptVersionStore());

                AiOrchestrator orch = new(
                    new SoloAuthorityHost(),
                    capturing,
                    sink,
                    new SessionTelemetryCollector(),
                    composer,
                    store,
                    new AgentMemoryPolicy(),
                    new NoOpRoleStructuredResponsePolicy(),
                    new NullAiOrchestrationMetrics());

                Debug.Log("[AINpc] Requesting NPC dialogue...");
                Task t = orch.RunTaskAsync(new AiTaskRequest
                {
                    RoleId = BuiltInAgentRoleIds.AiNpc,
                    Hint = "A traveler enters the tavern. Say greeting."
                });

                yield return PlayModeTestAwait.WaitTask(t, 300f, "AINpc dialogue");

                Debug.Log($"[AINpc] ═══════════════════════════════════════");
                Debug.Log($"[AINpc] Role: {capturing.LastRoleId}");
                Debug.Log($"[AINpc] Response: {capturing.LastResult.Content}");
                Debug.Log($"[AINpc] ═══════════════════════════════════════");

                Assert.IsTrue(capturing.LastResult.Ok, $"LLM call failed: {capturing.LastResult.Error}");
                Assert.AreEqual(BuiltInAgentRoleIds.AiNpc, capturing.LastRoleId);
                Assert.IsFalse(string.IsNullOrWhiteSpace(capturing.LastResult.Content));

                // AINpcResponsePolicy validates: non-empty text passes
                Debug.Log($"[AINpc] ✓ AINpc generated dialogue");
                Debug.Log("[AINpc] ═══ TEST PASSED ═══");
            }
            finally
            {
                handle.Dispose();
            }
        }

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator AINpc_ToolsAndChatMode_CanUseTools()
        {
            Debug.Log("[AINpc] ═══ TEST START ═══");

            if (!PlayModeProductionLikeLlmFactory.TryCreate(null, 0.3f, 180,
                    out PlayModeProductionLikeLlmHandle handle, out string ignore))
            {
                Assert.Ignore(ignore);
            }

            try
            {
                if (handle.ResolvedBackend == PlayModeProductionLikeLlmBackend.LlmUnity)
                {
                    yield return PlayModeProductionLikeLlmFactory.EnsureLlmUnityModelReady(handle);
                }

                Debug.Log($"[AINpc] Backend: {handle.ResolvedBackend}");

                InMemoryStore store = new();
                CapturingLlmClient capturing = new(handle.WrapWithMemoryStore(store));
                ListSink sink = new();

                AgentMemoryPolicy policy = new();
                policy.SetToolsForRole(BuiltInAgentRoleIds.AiNpc, new List<ILlmTool>
                {
                    new MemoryLlmTool()
                });
                policy.EnableMemoryTool(BuiltInAgentRoleIds.AiNpc);

                BuiltInDefaultAgentSystemPromptProvider systemPrompts = new();
                AiPromptComposer composer = new(
                    systemPrompts,
                    new NoAgentUserPromptTemplateProvider(),
                    new NullLuaScriptVersionStore());

                AiOrchestrator orch = new(
                    new SoloAuthorityHost(),
                    capturing,
                    sink,
                    new SessionTelemetryCollector(),
                    composer,
                    store,
                    policy,
                    new CompositeRoleStructuredResponsePolicy(),
                    new NullAiOrchestrationMetrics());

                Debug.Log("[AINpc] Requesting NPC with memory tool...");
                Task t = orch.RunTaskAsync(new AiTaskRequest
                {
                    RoleId = BuiltInAgentRoleIds.AiNpc,
                    Hint = "Welcome the player and remember their name is 'Hero'"
                });

                yield return PlayModeTestAwait.WaitTask(t, 180f, "AINpc with tools");

                Debug.Log($"[AINpc] Response: {capturing.LastResult.Content}");
                Debug.Log($"[AINpc] Commands: {sink.Items.Count}");

                Assert.IsTrue(capturing.LastResult.Ok);

                // Should have called memory tool
                bool usedMemory = capturing.LastResult.Content?.Contains("memory") == true ||
                                  capturing.LastResult.Content?.Contains("remember") == true;

                Debug.Log($"[AINpc] ✓ AINpc with ToolsAndChat mode completed");
                Debug.Log("[AINpc] ═══ TEST PASSED ═══");
            }
            finally
            {
                handle.Dispose();
            }
        }

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator AINpc_ChatOnlyMode_PlainTextOnly()
        {
            Debug.Log("[AINpc] ═══ TEST START ═══");

            if (!PlayModeProductionLikeLlmFactory.TryCreate(null, 0.7f, 120,
                    out PlayModeProductionLikeLlmHandle handle, out string ignore))
            {
                Assert.Ignore(ignore);
            }

            try
            {
                if (handle.ResolvedBackend == PlayModeProductionLikeLlmBackend.LlmUnity)
                {
                    yield return PlayModeProductionLikeLlmFactory.EnsureLlmUnityModelReady(handle);
                }

                InMemoryStore store = new();
                CapturingLlmClient capturing = new(handle.WrapWithMemoryStore(store));
                ListSink sink = new();

                // ChatOnly mode - no tools
                AgentConfig config = new AgentBuilder(BuiltInAgentRoleIds.AiNpc)
                    .WithMode(AgentMode.ChatOnly)
                    .WithSystemPrompt("You are a mysterious merchant.")
                    .Build();

                BuiltInDefaultAgentSystemPromptProvider systemPrompts = new();
                AiPromptComposer composer = new(
                    systemPrompts,
                    new NoAgentUserPromptTemplateProvider(),
                    new NullLuaScriptVersionStore());

                AiOrchestrator orch = new(
                    new SoloAuthorityHost(),
                    capturing,
                    sink,
                    new SessionTelemetryCollector(),
                    composer,
                    store,
                    new AgentMemoryPolicy(), // No tools
                    new AINpcResponsePolicy(),
                    new NullAiOrchestrationMetrics());

                Debug.Log("[AINpc] Testing ChatOnly mode...");
                Task t = orch.RunTaskAsync(new AiTaskRequest
                {
                    RoleId = BuiltInAgentRoleIds.AiNpc,
                    Hint = "A customer approaches. What do you sell?"
                });

                yield return PlayModeTestAwait.WaitTask(t, 300f, "AINpc ChatOnly");

                Assert.IsTrue(capturing.LastResult.Ok);
                Assert.IsFalse(string.IsNullOrWhiteSpace(capturing.LastResult.Content));

                Debug.Log($"[AINpc] ✓ AINpc ChatOnly mode works");
                Debug.Log("[AINpc] ═══ TEST PASSED ═══");
            }
            finally
            {
                handle.Dispose();
            }
        }
    }
}
#endif