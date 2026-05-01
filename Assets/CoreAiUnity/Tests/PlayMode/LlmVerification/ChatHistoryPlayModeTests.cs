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
#if !COREAI_NO_LLM && !UNITY_WEBGL
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

        private string _testDirPath;

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
            if (!PlayModeProductionLikeLlmFactory.TryCreate(null, 0.2f, 300, out PlayModeProductionLikeLlmHandle handle,
                    out string ignore))
            {
                Assert.Ignore(ignore);
            }

            try
            {
                yield return PlayModeProductionLikeLlmFactory.EnsureLlmUnityModelReady(handle);

                // 
                AgentConfig chatAgent = new AgentBuilder("TestPersistentChatAgent")
                    .WithSystemPrompt("You are a helpful assistant. Keep your answers brief.")
                    .WithMemory(MemoryToolAction.Append) //  
                    .WithChatHistory(8192, true) // : persistent = true
                    .WithMode(AgentMode.ChatOnly)
                    .Build();

                Debug.Log("[ChatHistory]  STEP 1: Sending first message...");

                // ===  1 ===
                FileAgentMemoryStore store1 = new();
                AgentMemoryPolicy policy1 = new();
                chatAgent.ApplyToPolicy(policy1);

                AiOrchestrator orch1 = new(
                    new SoloAuthorityHost(),
                    handle.Client,
                    new ListSink(),
                    new SessionTelemetryCollector(),
                    new AiPromptComposer(new CustomAgentPromptProvider(chatAgent.SystemPrompt),
                        new NoAgentUserPromptTemplateProvider(), new NullLuaScriptVersionStore()),
                    store1, policy1, new NoOpRoleStructuredResponsePolicy(), new NullAiOrchestrationMetrics(), UnityEngine.ScriptableObject.CreateInstance<CoreAI.Infrastructure.Llm.CoreAISettingsAsset>());

                Task t1 = orch1.RunTaskAsync(new AiTaskRequest
                    { RoleId = chatAgent.RoleId, Hint = "Hello! My secret word is 'Pineapple'." });
                yield return PlayModeTestAwait.WaitTask(t1, 300f, "chat history part 1");

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

                //    ,    
                CapturingLlmClient cap = new(handle.Client);

                AiOrchestrator orch2 = new(
                    new SoloAuthorityHost(),
                    cap,
                    new ListSink(),
                    new SessionTelemetryCollector(),
                    new AiPromptComposer(new CustomAgentPromptProvider(chatAgent.SystemPrompt),
                        new NoAgentUserPromptTemplateProvider(), new NullLuaScriptVersionStore()),
                    store2, policy2, new NoOpRoleStructuredResponsePolicy(), new NullAiOrchestrationMetrics(), UnityEngine.ScriptableObject.CreateInstance<CoreAI.Infrastructure.Llm.CoreAISettingsAsset>());

                Task t2 = orch2.RunTaskAsync(new AiTaskRequest
                    { RoleId = chatAgent.RoleId, Hint = "What was my secret word?" });
                yield return PlayModeTestAwait.WaitTask(t2, 300f, "chat history part 2");

                string response2 = cap.LastContent ?? "";
                Debug.Log($"[ChatHistory] Final Response: {response2}");

                Assert.IsTrue(response2.Contains("Pineapple", StringComparison.OrdinalIgnoreCase),
                    $"Agent did not remember the secret word. Response was: {response2}");

                Debug.Log("[ChatHistory]  TEST PASSED");
            }
            finally
            {
                handle.Dispose();
            }
        }

        private sealed class CapturingLlmClient : ILlmClient
        {
            private readonly ILlmClient _inner;
            public string LastContent;

            public CapturingLlmClient(ILlmClient inner)
            {
                _inner = inner;
            }

            public async Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
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
#endif
}


