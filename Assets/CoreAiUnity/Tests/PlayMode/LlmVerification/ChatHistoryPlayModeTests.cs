using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Infrastructure.AiMemory;
using CoreAI.Infrastructure.Llm;
using CoreAI.Session;
using CoreAI.Messaging;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// PlayMode : ,  ChatHistory     (PersistentChatHistoryBetweenSessions = true)
    ///       (  ).
    /// </summary>
    public sealed class ChatHistoryPlayModeTests
    {
        private sealed class ListSink : IAiGameCommandSink
        {
            public readonly List<ApplyAiGameCommand> Items = new();

            public void Publish(ApplyAiGameCommand command)
            {
                Items.Add(command);
            }
        }

        [SetUp]
        public void Setup()
        {
            //    ,    
            //   FileAgentMemoryStore   ,        roleId
            string dir = Path.Combine(Application.persistentDataPath, "CoreAI", "AgentMemory");
            string safePath = Path.Combine(dir, "TestPersistentChatAgent.json");
            if (File.Exists(safePath))
            {
                File.Delete(safePath);
            }
        }

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator ChatHistory_PersistentBetweenSessions_Works()
        {
            Debug.Log("[ChatHistory]  TEST 1: Persistent Context Across Restarts ");

            // Persistent chat transcript only — no MemoryTool and no live model. This test is about
            // deterministic disk persistence/replay; live LLM behavior is covered by focused LLM suites.
            AgentConfig chatAgent = new AgentBuilder("TestPersistentChatAgent")
                .WithSystemPrompt(
                    "You are a helpful assistant. Keep answers brief. " +
                    "When asked about earlier messages in this conversation, answer from the dialogue you already have in context.")
                .WithChatHistory(8192, true)
                .WithMode(AgentMode.ChatOnly)
                .Build();

            Debug.Log("[ChatHistory]  STEP 1: Sending first message...");

            // ===  1 ===
            FileAgentMemoryStore store1 = new();
            AgentMemoryPolicy policy1 = new();
            chatAgent.ApplyToPolicy(policy1);
            PersistedHistoryStubLlmClient llm1 = new("I will remember Pineapple.");

            AiOrchestrator orch1 = new(
                new SoloAuthorityHost(),
                llm1,
                new ListSink(),
                new SessionTelemetryCollector(),
                new AiPromptComposer(new CustomAgentPromptProvider(chatAgent.SystemPrompt),
                    new NoAgentUserPromptTemplateProvider(), new NullLuaScriptVersionStore()),
                store1, policy1, new NoOpRoleStructuredResponsePolicy(), new NullAiOrchestrationMetrics(),
                ScriptableObject.CreateInstance<CoreAISettingsAsset>());

            Task t1 = orch1.RunTaskAsync(new AiTaskRequest
                { RoleId = chatAgent.RoleId, Hint = "Hello! My secret word is 'Pineapple'." });
            yield return PlayModeTestAwait.WaitTask(t1, 5f, "chat history part 1");

            // ===   ===
            ChatMessage[] history1 = store1.GetChatHistory(chatAgent.RoleId);
            Assert.GreaterOrEqual(history1.Length, 2,
                "History should contain at least 2 messages (user + assistant)");
            bool foundSecret = false;
            foreach (ChatMessage m in history1)
            {
                if (m.Content.Contains("Pineapple"))
                {
                    foundSecret = true;
                }
            }

            Assert.IsTrue(foundSecret, "The secret word should be preserved in memory store history.");

            Debug.Log("[ChatHistory]  STEP 2: Restarting game (creating new orchestrator/store)...");

            // ===  2 === (  )
            FileAgentMemoryStore store2 = new();
            AgentMemoryPolicy policy2 = new();
            chatAgent.ApplyToPolicy(policy2);
            PersistedHistoryStubLlmClient llm2 = new("Your secret word was Pineapple.");

            AiOrchestrator orch2 = new(
                new SoloAuthorityHost(),
                llm2,
                new ListSink(),
                new SessionTelemetryCollector(),
                new AiPromptComposer(new CustomAgentPromptProvider(chatAgent.SystemPrompt),
                    new NoAgentUserPromptTemplateProvider(), new NullLuaScriptVersionStore()),
                store2, policy2, new NoOpRoleStructuredResponsePolicy(), new NullAiOrchestrationMetrics(),
                ScriptableObject.CreateInstance<CoreAISettingsAsset>());

            Task t2 = orch2.RunTaskAsync(new AiTaskRequest
                { RoleId = chatAgent.RoleId, Hint = "What was my secret word?" });
            yield return PlayModeTestAwait.WaitTask(t2, 5f, "chat history part 2");

            Assert.IsTrue(llm2.LastRequestContainsHistory("Pineapple"),
                "The restarted orchestrator should replay persisted chat history to the LLM request.");

            string response2 = llm2.LastContent ?? "";
            Debug.Log($"[ChatHistory] Final Response: {response2}");

            Assert.IsTrue(response2.Contains("Pineapple", StringComparison.OrdinalIgnoreCase),
                $"Agent did not remember the secret word. Response was: {response2}");

            Debug.Log("[ChatHistory]  TEST PASSED");
        }

        private sealed class PersistedHistoryStubLlmClient : ILlmClient
        {
            private readonly string _response;
            private IList<Microsoft.Extensions.AI.ChatMessage> _lastChatHistory;
            public string LastContent;

            public PersistedHistoryStubLlmClient(string response)
            {
                _response = response;
            }

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                _lastChatHistory = request.ChatHistory;
                LastContent = _response;
                return Task.FromResult(new LlmCompletionResult
                {
                    Ok = true,
                    Content = _response
                });
            }

            public bool LastRequestContainsHistory(string value)
            {
                if (_lastChatHistory == null)
                {
                    return false;
                }

                foreach (Microsoft.Extensions.AI.ChatMessage message in _lastChatHistory)
                {
                    if ((message.Text ?? string.Empty).Contains(value, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                return false;
            }

            public void SetTools(IReadOnlyList<ILlmTool> tools)
            {
            }
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
    }
}
