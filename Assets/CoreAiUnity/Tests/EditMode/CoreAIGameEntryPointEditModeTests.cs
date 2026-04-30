using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Composition;
using CoreAI.Logging;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    [TestFixture]
    public sealed class CoreAIGameEntryPointEditModeTests
    {
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
            CoreAIAgent.Reset();
            CoreAIGameEntryPoint.ResetInitializationGuardForTests();
            CoreAIGameEntryPoint.AutoBootstrap = false;
        }

        [Test]
        public void Start_FirstEntryPoint_InitializesCoreAiFacade()
        {
            TestLogger logger = new();
            StubOrchestrator orchestrator = new();
            AgentMemoryPolicy policy = new();
            StubMemoryStore memoryStore = new();
            CoreAIGameEntryPoint entryPoint = new(logger, orchestrator, policy, memoryStore);

            entryPoint.Start();

            Assert.AreSame(orchestrator, CoreAIAgent.Orchestrator);
            Assert.AreSame(policy, CoreAIAgent.Policy);
            Assert.AreSame(memoryStore, CoreAIAgent.MemoryStore);
            Assert.AreEqual(0, logger.WarnCount);
            entryPoint.Dispose();
        }

        [Test]
        public void Start_SecondEntryPoint_IsSkippedAndDoesNotOverrideFacade()
        {
            TestLogger logger1 = new();
            StubOrchestrator orchestrator1 = new();
            AgentMemoryPolicy policy1 = new();
            StubMemoryStore memoryStore1 = new();
            CoreAIGameEntryPoint first = new(logger1, orchestrator1, policy1, memoryStore1);

            TestLogger logger2 = new();
            StubOrchestrator orchestrator2 = new();
            AgentMemoryPolicy policy2 = new();
            StubMemoryStore memoryStore2 = new();
            CoreAIGameEntryPoint second = new(logger2, orchestrator2, policy2, memoryStore2);

            first.Start();
            second.Start();

            Assert.AreSame(orchestrator1, CoreAIAgent.Orchestrator);
            Assert.AreSame(policy1, CoreAIAgent.Policy);
            Assert.AreSame(memoryStore1, CoreAIAgent.MemoryStore);
            // Duplicate-start is reported via Debug (not Warn) to keep the console quiet
            // when additive scenes / tests legitimately spin up a second LifetimeScope.
            Assert.AreEqual(1, logger2.DebugCount, "Duplicate start should be reported exactly once via Debug.");
            Assert.AreEqual(0, logger2.WarnCount, "Duplicate start must not emit a Warning.");

            first.Dispose();
            second.Dispose();
        }

        private sealed class TestLogger : ILog
        {
            public int DebugCount { get; private set; }
            public int WarnCount { get; private set; }
            public void Debug(string message, string tag = null) => DebugCount++;
            public void Info(string message, string tag = null) { }
            public void Warn(string message, string tag = null) => WarnCount++;
            public void Error(string message, string tag = null) { }
        }

        private sealed class StubOrchestrator : IAiOrchestrationService
        {
            public Task<string> RunTaskAsync(AiTaskRequest task, CancellationToken cancellationToken = default)
                => Task.FromResult(string.Empty);

            public async IAsyncEnumerable<LlmStreamChunk> RunStreamingAsync(
                AiTaskRequest task,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                yield return new LlmStreamChunk { IsDone = true };
                await Task.CompletedTask;
            }

            public void CancelTasks(string cancellationScope) { }
        }

        private sealed class StubMemoryStore : IAgentMemoryStore
        {
            public bool TryLoad(string roleId, out AgentMemoryState state)
            {
                state = null;
                return false;
            }

            public void Save(string roleId, AgentMemoryState state) { }
            public void Clear(string roleId) { }
            public void ClearChatHistory(string roleId) { }
            public void AppendChatMessage(string roleId, string role, string content, bool persistToDisk = true) { }
            public ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0) => System.Array.Empty<ChatMessage>();
        }
    }
}
