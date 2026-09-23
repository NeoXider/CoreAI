using System;
using System.IO;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Infrastructure.AiMemory;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// The one orchestrator history test that needs the engine: <see cref="FileAgentMemoryStore"/> is a
    /// CoreAiUnity type rooted at <see cref="Application.persistentDataPath"/>. Everything else about the
    /// orchestrator's history lives in <see cref="AiOrchestratorHistoryEditModeTests"/>, which is linked into
    /// the portable test project and must stay engine-free.
    /// </summary>
    [TestFixture]
    public sealed class AiOrchestratorHistoryFileStoreEditModeTests
    {
        [Test]
        public async Task RunTaskAsync_WithFileStore_AndPersistChatHistory_WritesDiskReadableByNewStore()
        {
            string roleId = "EditMode_OrchPersist_" + Guid.NewGuid().ToString("N");
            string dir = Path.Combine(Application.persistentDataPath, "CoreAI", "AgentMemory");
            string safeName = string.Join("_", roleId.Split(Path.GetInvalidFileNameChars()));
            string filePath = Path.Combine(dir, $"{safeName}.json");
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            try
            {
                FileAgentMemoryStore store1 = new();
                AgentMemoryPolicy policy = new();
                policy.ConfigureChatHistory(roleId, true, 8192, true, 50);
                policy.DisableMemoryTool(roleId);
                policy.SetToolsForRole(roleId, Array.Empty<ILlmTool>());

                AiOrchestratorHistoryEditModeTests.TestLlmClient llm = new();
                AiOrchestratorHistoryEditModeTests.TestSettings settings = new();
                AiOrchestrator orchestrator = new(
                    new AiOrchestratorHistoryEditModeTests.TestAuthority(), llm,
                    new AiOrchestratorHistoryEditModeTests.TestSink(),
                    new AiOrchestratorHistoryEditModeTests.TestTelemetry(),
                    new AiPromptComposer(
                        new AiOrchestratorHistoryEditModeTests.NullSys(),
                        new AiOrchestratorHistoryEditModeTests.NullUsr(),
                        null, null, policy, settings),
                    store1, policy, null, null, settings,
                    AiOrchestratorHistoryEditModeTests.TestActorIdentityProvider);

                await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = roleId, Hint = "persist hint" });

                ChatMessage[] h1 = store1.GetChatHistory(roleId);
                Assert.GreaterOrEqual(h1.Length, 2,
                    "After a successful turn the store should contain user + assistant lines.");

                FileAgentMemoryStore store2 = new();
                ChatMessage[] h2 = store2.GetChatHistory(roleId);
                Assert.AreEqual(h1.Length, h2.Length,
                    "A new FileAgentMemoryStore should reload the same persisted chat from disk.");
                Assert.AreEqual(h1[^1].Content, h2[^1].Content);
            }
            finally
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
        }
    }
}
