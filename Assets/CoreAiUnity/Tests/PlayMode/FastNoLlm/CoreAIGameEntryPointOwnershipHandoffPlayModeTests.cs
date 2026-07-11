using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Composition;
using CoreAI.Logging;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayModeTest
{
    /// <summary>
    /// Covers F-14: when the owning CoreAIGameEntryPoint is destroyed (e.g. its additive scene is
    /// unloaded), a standby entry point registered earlier must be promoted so the CoreAI facade
    /// keeps resolving instead of going stale.
    /// </summary>
    [TestFixture]
    public sealed class CoreAIGameEntryPointOwnershipHandoffPlayModeTests
    {
        private readonly List<GameObject> _spawned = new();

        [SetUp]
        public void SetUp()
        {
            CoreAIAgent.Reset();
            CoreAIGameEntryPoint.ResetInitializationGuardForTests();
            CoreAIGameEntryPoint.AutoBootstrap = false;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in _spawned)
            {
                if (go != null)
                {
                    Object.Destroy(go);
                }
            }

            _spawned.Clear();

            CoreAIAgent.Reset();
            CoreAIGameEntryPoint.ResetInitializationGuardForTests();
            CoreAIGameEntryPoint.AutoBootstrap = false;
        }

        [UnityTest]
        public IEnumerator DestroyingOwner_PromotesStandbyEntryPoint_FacadeStaysResolvable()
        {
            yield return null;

            GameObject firstOwnerObject = CreateOwnerGameObject("FirstAdditiveSceneEntryPoint");
            GameObject secondOwnerObject = CreateOwnerGameObject("SecondAdditiveSceneEntryPoint");

            StubOrchestrator orchestrator1 = new();
            AgentMemoryPolicy policy1 = new();
            StubMemoryStore memoryStore1 = new();
            CoreAIGameEntryPoint first = new(new TestLogger(), orchestrator1, policy1, memoryStore1);

            StubOrchestrator orchestrator2 = new();
            AgentMemoryPolicy policy2 = new();
            StubMemoryStore memoryStore2 = new();
            CoreAIGameEntryPoint second = new(new TestLogger(), orchestrator2, policy2, memoryStore2);

            // Simulate two additive scenes each spinning up their own composition root.
            first.Start();
            second.Start();

            Assert.AreSame(orchestrator1, CoreAIAgent.Orchestrator,
                "The first entry point should own the facade after both are started.");

            yield return null;

            // Simulate the first additive scene being unloaded: its GameObject and entry point are destroyed.
            Object.Destroy(firstOwnerObject);
            first.Dispose();

            yield return null;

            Assert.AreSame(orchestrator2, CoreAIAgent.Orchestrator,
                "The facade must hand off to the standby second entry point instead of going stale.");
            Assert.AreSame(policy2, CoreAIAgent.Policy);
            Assert.AreSame(memoryStore2, CoreAIAgent.MemoryStore);

            second.Dispose();
            Object.Destroy(secondOwnerObject);
        }

        private GameObject CreateOwnerGameObject(string name)
        {
            GameObject go = new(name);
            _spawned.Add(go);
            return go;
        }

        private sealed class TestLogger : ILog
        {
            public void Debug(string message, string tag = null)
            {
            }

            public void Info(string message, string tag = null)
            {
            }

            public void Warn(string message, string tag = null)
            {
            }

            public void Error(string message, string tag = null)
            {
            }
        }

        private sealed class StubOrchestrator : IAiOrchestrationService
        {
            public Task<string> RunTaskAsync(AiTaskRequest task, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(string.Empty);
            }

            public async IAsyncEnumerable<LlmStreamChunk> RunStreamingAsync(
                AiTaskRequest task,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                yield return new LlmStreamChunk { IsDone = true };
                await Task.CompletedTask;
            }

            public void CancelTasks(string cancellationScope)
            {
            }
        }

        private sealed class StubMemoryStore : IAgentMemoryStore
        {
            public bool TryLoad(string roleId, out AgentMemoryState state)
            {
                state = null;
                return false;
            }

            public void Save(string roleId, AgentMemoryState state)
            {
            }

            public void Clear(string roleId)
            {
            }

            public void ClearChatHistory(string roleId)
            {
            }

            public void AppendChatMessage(string roleId, string role, string content, bool persistToDisk = true)
            {
            }

            public ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0)
            {
                return System.Array.Empty<ChatMessage>();
            }
        }
    }
}