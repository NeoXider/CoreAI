#if COREAI_LLM && !UNITY_WEBGL
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
    /// PlayMode: <see cref="InGameLlmChatService"/> with built-in <see cref="BuiltInAgentRoleIds.SmartChat"/> role.
    /// </summary>
    public sealed class SmartChatAndAINpcPlayModeTests
    {
        // WHY: the longest body is two 120 s chat turns or one 240 s tool turn after an optional GGUF load;
        // LiveTestRequestScope caps each wait to what is left so a slow load ends in the test's own
        // cancelling wait instead of a framework abort that leaves the request running.
        private const int TestTimeoutMs = 300000;

        private LiveTestRequestScope _requests;
        private PlayModeProductionLikeLlmHandle _handle;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _requests = new LiveTestRequestScope(TestTimeoutMs);
            LogAssert.ignoreFailingMessages = true;
            yield break;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            // WHY: a framework timeout skips the test body's cleanup, so cancel the abandoned request here and
            // let it unwind before the client is disposed.
            if (_requests != null)
            {
                yield return _requests.CancelAllAndDrain();
                _requests = null;
            }

            _handle?.Dispose();
            _handle = null;

            LogAssert.ignoreFailingMessages = false;
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

        [UnityTest]
        [Timeout(TestTimeoutMs)]
        public IEnumerator SmartChat_RespondsToGreeting()
        {
            Debug.Log("[SmartChat] === TEST START ===");

            if (!PlayModeProductionLikeLlmFactory.TryCreate(null, 0.7f, 120,
                    out PlayModeProductionLikeLlmHandle handle, out string ignore))
            {
                Assert.Ignore(ignore);
            }

            // WHY: disposed in [UnityTearDown] after the request drain, never under a still-running turn.
            _handle = handle;

            //   LLMUnity    .  HTTP  .
            if (handle.ResolvedBackend == PlayModeProductionLikeLlmBackend.LlmUnity)
            {
                yield return PlayModeProductionLikeLlmFactory.EnsureLlmUnityModelReady(handle);
            }

            Debug.Log($"[SmartChat] Backend: {handle.ResolvedBackend}");

            InMemoryStore store = new();
            LiveCapturingLlmClient capturing = new(handle.WrapWithMemoryStore(store));

            BuiltInDefaultAgentSystemPromptProvider systemPrompts = new();
            AiPromptComposer composer = new(
                systemPrompts,
                new NoAgentUserPromptTemplateProvider(),
                new NullLuaScriptVersionStore());

            InGameLlmChatService chatService = new(capturing, systemPrompts, 10);

            Debug.Log("[SmartChat] Sending: 'Hello, how are you?'");
            CancellationTokenSource cts = _requests.CreateCancellation();
            Task<LlmCompletionResult> task = _requests.Track(
                chatService.SendPlayerMessageAsync("Hello, how are you?", cts.Token));
            yield return PlayModeTestAwait.WaitTask(task, _requests.Cap(120f), "Send message 1", cts);
            LlmCompletionResult result = task.Result;

            Debug.Log("[SmartChat] ----------------------------------------");
            Debug.Log($"[SmartChat] Role: {capturing.LastRoleId}");
            Debug.Log(
                $"[SmartChat] System Prompt: {capturing.LastSystemPrompt?.Substring(0, Math.Min(100, capturing.LastSystemPrompt?.Length ?? 0))}...");
            Debug.Log($"[SmartChat] Response: {result.Content}");
            Debug.Log("[SmartChat] ----------------------------------------");

            Assert.IsTrue(result.Ok, $"LLM call failed: {result.Error}");
            Assert.AreEqual(BuiltInAgentRoleIds.SmartChat, capturing.LastRoleId);
            Assert.IsFalse(string.IsNullOrWhiteSpace(result.Content), "Response should not be empty");

            //        ( just tool call result)
            bool hasTextContent = result.Content.Length > 10;
            Assert.IsTrue(hasTextContent, "Response should contain text");

            Debug.Log(
                $"[SmartChat] Response preview: {result.Content.Substring(0, Math.Min(50, result.Content.Length))}...");
            Debug.Log("[SmartChat] TEST PASSED");
        }

        [UnityTest]
        [Timeout(TestTimeoutMs)]
        public IEnumerator SmartChat_MaintainsHistory()
        {
            Debug.Log("[SmartChat] === TEST START ===");

            if (!PlayModeProductionLikeLlmFactory.TryCreate(null, 0.7f, 120,
                    out PlayModeProductionLikeLlmHandle handle, out string ignore))
            {
                Assert.Ignore(ignore);
            }

            // WHY: disposed in [UnityTearDown] after the request drain, never under a still-running turn.
            _handle = handle;

            if (handle.ResolvedBackend == PlayModeProductionLikeLlmBackend.LlmUnity)
            {
                yield return PlayModeProductionLikeLlmFactory.EnsureLlmUnityModelReady(handle);
            }

            Debug.Log($"[SmartChat] Backend: {handle.ResolvedBackend}");

            InMemoryStore store = new();
            LiveCapturingLlmClient capturing = new(handle.WrapWithMemoryStore(store));

            BuiltInDefaultAgentSystemPromptProvider systemPrompts = new();
            InGameLlmChatService chatService = new(capturing, systemPrompts, 10);

            // First message
            CancellationTokenSource cts = _requests.CreateCancellation();
            Task<LlmCompletionResult> t1 = _requests.Track(
                chatService.SendPlayerMessageAsync("My name is Adventurer", cts.Token));
            yield return PlayModeTestAwait.WaitTask(t1, _requests.Cap(120f), "First message", cts);
            LlmCompletionResult r1 = t1.Result;
            Assert.AreEqual(1, chatService.HistoryPairCount);

            // Second message - should see history
            Task<LlmCompletionResult> t2 = _requests.Track(
                chatService.SendPlayerMessageAsync("What is my name?", cts.Token));
            yield return PlayModeTestAwait.WaitTask(t2, _requests.Cap(120f), "Second message", cts);
            LlmCompletionResult r2 = t2.Result;
            Assert.AreEqual(2, chatService.HistoryPairCount);

            // Verify history is in the prompt
            bool foundAdventurer = false;
            if (capturing.LastChatHistory != null)
            {
                foreach (Microsoft.Extensions.AI.ChatMessage msg in capturing.LastChatHistory)
                {
                    if (msg.Text != null && msg.Text.Contains("Adventurer"))
                    {
                        foundAdventurer = true;
                        break;
                    }
                }
            }

            Assert.IsTrue(foundAdventurer, "The chat history should contain the string 'Adventurer'");

            Debug.Log($"[SmartChat] History maintained: {chatService.HistoryPairCount} pairs");
            Debug.Log("[SmartChat] TEST PASSED");
        }

        [UnityTest]
        [Timeout(TestTimeoutMs)]
        public IEnumerator SmartChat_ClearHistory_Works()
        {
            Debug.Log("[SmartChat] === TEST START (ClearHistory) ===");

            if (!PlayModeProductionLikeLlmFactory.TryCreate(null, 0.7f, 120,
                    out PlayModeProductionLikeLlmHandle handle, out string ignore))
            {
                Assert.Ignore(ignore);
            }

            // WHY: disposed in [UnityTearDown] after the request drain, never under a still-running turn.
            _handle = handle;

            if (handle.ResolvedBackend == PlayModeProductionLikeLlmBackend.LlmUnity)
            {
                yield return PlayModeProductionLikeLlmFactory.EnsureLlmUnityModelReady(handle);
            }

            Debug.Log($"[SmartChat] Backend: {handle.ResolvedBackend}");

            InMemoryStore store = new();
            LiveCapturingLlmClient capturing = new(handle.WrapWithMemoryStore(store));

            BuiltInDefaultAgentSystemPromptProvider systemPrompts = new();
            InGameLlmChatService chatService = new(capturing, systemPrompts, 10);

            CancellationTokenSource cts = _requests.CreateCancellation();
            Task<LlmCompletionResult> t1 = _requests.Track(
                chatService.SendPlayerMessageAsync("First line", cts.Token));
            yield return PlayModeTestAwait.WaitTask(t1, _requests.Cap(120f), "First message", cts);
            LlmCompletionResult r1 = t1.Result;
            Assert.IsTrue(r1.Ok, r1.Error);
            Assert.AreEqual(1, chatService.HistoryPairCount);

            chatService.ClearHistory();
            Assert.AreEqual(0, chatService.HistoryPairCount);

            Task<LlmCompletionResult> t2 = _requests.Track(
                chatService.SendPlayerMessageAsync("After clear", cts.Token));
            yield return PlayModeTestAwait.WaitTask(t2, _requests.Cap(120f), "After clear", cts);
            LlmCompletionResult r2 = t2.Result;
            Assert.IsTrue(r2.Ok, r2.Error);
            Assert.AreEqual(1, chatService.HistoryPairCount);

            Debug.Log("[SmartChat] ClearHistory test PASSED");
        }

        [UnityTest]
        [Timeout(TestTimeoutMs)]
        public IEnumerator AINpc_ToolsAndChatMode_CanUseTools()
        {
            Debug.Log("[AINpc] === TEST START ===");

            if (!PlayModeProductionLikeLlmFactory.TryCreate(null, 0.3f, 240,
                    out PlayModeProductionLikeLlmHandle handle, out string ignore))
            {
                Assert.Ignore(ignore);
            }

            // WHY: disposed in [UnityTearDown] after the request drain, never under a still-running turn.
            _handle = handle;

            if (handle.ResolvedBackend == PlayModeProductionLikeLlmBackend.LlmUnity)
            {
                yield return PlayModeProductionLikeLlmFactory.EnsureLlmUnityModelReady(handle);
            }

            Debug.Log($"[AINpc] Backend: {handle.ResolvedBackend}");

            InMemoryStore store = new();
            LiveCapturingLlmClient capturing = new(handle.WrapWithMemoryStore(store));
            ListSink sink = new();

            AgentMemoryPolicy policy = new();
            TestAgentPolicyDefaults.ApplyToolsAndChatWithMemory(policy, BuiltInAgentRoleIds.AiNpc);

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
                new NullAiOrchestrationMetrics(),
                ScriptableObject.CreateInstance<Infrastructure.Llm.CoreAISettingsAsset>(),
                new LocalActorIdentityProvider("ai-npc-memory-test"));

            Debug.Log("[AINpc] Requesting NPC with memory tool...");
            CoreAi.ClearToolCallHistory();
            CancellationTokenSource cts = _requests.CreateCancellation();
            Task t = _requests.Track(orch.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.AiNpc,
                Hint = "First save 'Player name: Hero' with the memory tool, then welcome the player.",
                // WHY: this test proves the ToolsAndChat role can execute a real tool, not that a model
                // volunteers one. Under Auto a model may only greet the player - a model-compliance
                // outcome that failed this wiring gate - so RequireSpecific(memory) makes the tool round
                // trip the deterministic part of the turn.
                ForcedToolMode = LlmToolChoiceMode.RequireSpecific,
                RequiredToolName = "memory",
                MaxOutputTokens = 4096
            }, cts.Token));

            yield return PlayModeTestAwait.WaitTask(t, _requests.Cap(240f), "AINpc with tools", cts);

            Debug.Log($"[AINpc] Response: {capturing.LastResult?.Content}");
            Debug.Log($"[AINpc] Commands: {sink.Items.Count}");

            // WHY: a provider may end a successful tool turn with no closing text (EmptyResponse); every
            // other failure after the tool ran - HTTP 5xx, timeout, cancellation, or a client exception the
            // orchestrator swallowed (no completion captured) - must still fail.
            Assert.IsTrue(capturing.LastIsOkOrEmptyFinalText,
                $"AINpc turn failed: {capturing.DescribeLastOutcome()}");

            bool completedMemoryTool = CoreAi.GetToolCallHistorySnapshot()
                .Any(r => r.Status == "completed" &&
                          r.Info.RoleId == BuiltInAgentRoleIds.AiNpc &&
                          r.Info.ToolName == "memory");
            Assert.IsTrue(completedMemoryTool,
                "AINpc ToolsAndChat mode must complete a real memory tool call, not merely mention memory in text.");

            Assert.IsTrue(store.TryLoad(BuiltInAgentRoleIds.AiNpc, out AgentMemoryState npcMemory),
                "AINpc memory tool call should persist data in the memory store.");
            Assert.That(npcMemory.Memory, Does.Contain("Hero"),
                "AINpc memory store should contain the remembered player name.");

            Debug.Log("[AINpc] AINpc with ToolsAndChat mode completed");
            Debug.Log("[AINpc] TEST PASSED");
        }

        [UnityTest]
        [Timeout(TestTimeoutMs)]
        public IEnumerator AINpc_ChatOnlyMode_PlainTextOnly()
        {
            Debug.Log("[AINpc] === TEST START ===");

            if (!PlayModeProductionLikeLlmFactory.TryCreate(null, 0.7f, 120,
                    out PlayModeProductionLikeLlmHandle handle, out string ignore))
            {
                Assert.Ignore(ignore);
            }

            // WHY: disposed in [UnityTearDown] after the request drain, never under a still-running turn.
            _handle = handle;

            if (handle.ResolvedBackend == PlayModeProductionLikeLlmBackend.LlmUnity)
            {
                yield return PlayModeProductionLikeLlmFactory.EnsureLlmUnityModelReady(handle);
            }

            InMemoryStore store = new();
            LiveCapturingLlmClient capturing = new(handle.WrapWithMemoryStore(store));
            ListSink sink = new();

            // ChatOnly mode - no tools
            AgentConfig config = new AgentBuilder(BuiltInAgentRoleIds.AiNpc)
                .WithMode(AgentMode.ChatOnly)
                .WithSystemPrompt("You are a mysterious merchant.")
                .Build();
            AgentMemoryPolicy policy = new();
            config.ApplyToPolicy(policy);

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
                new AINpcResponsePolicy(),
                new NullAiOrchestrationMetrics(),
                ScriptableObject.CreateInstance<Infrastructure.Llm.CoreAISettingsAsset>(),
                new LocalActorIdentityProvider("ai-npc-chat-only-test"));

            Debug.Log("[AINpc] Testing ChatOnly mode...");
            CancellationTokenSource cts = _requests.CreateCancellation();
            Task t = _requests.Track(orch.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.AiNpc,
                Hint = "A customer approaches. What do you sell?",
                MaxOutputTokens = 4096
            }, cts.Token));

            yield return PlayModeTestAwait.WaitTask(t, _requests.Cap(120f), "AINpc ChatOnly", cts);

            Assert.IsNotNull(capturing.LastResult, $"AINpc ChatOnly turn failed: {capturing.DescribeLastOutcome()}");
            Assert.IsTrue(capturing.LastResult.Ok, $"AINpc ChatOnly turn failed: {capturing.DescribeLastOutcome()}");
            Assert.IsFalse(string.IsNullOrWhiteSpace(capturing.LastResult.Content));

            Debug.Log("[AINpc] AINpc ChatOnly mode works");
            Debug.Log("[AINpc] TEST PASSED");
        }
    }
}
#endif
