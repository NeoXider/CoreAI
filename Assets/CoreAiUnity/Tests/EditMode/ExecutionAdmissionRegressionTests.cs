using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Messaging;
using CoreAI.Session;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>Admission snapshots, connection cancellation and bounded streaming backlog contracts.</summary>
    public sealed class ExecutionAdmissionRegressionTests
    {
        private SynchronizationContext _previousSynchronizationContext;

        [SetUp]
        public void DetachSynchronizationContext()
        {
            _previousSynchronizationContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void RestoreSynchronizationContext() => SynchronizationContext.SetSynchronizationContext(_previousSynchronizationContext);

        [TestCase(false)]
        [TestCase(true)]
        public async Task MemoryScope_RealQueuedInnerKeepsAdmissionPartitionAcrossWaitAndProviderAwait(bool streaming)
        {
            MemoryScenario scenario = new();
            AgentMemoryScope first = Scope("first");
            AgentMemoryScope second = Scope("second");
            scenario.Seed(first, "first-seed");
            scenario.Seed(second, "second-seed");
            scenario.ScopeProvider.Current = first;
            using QueuedAiOrchestrator queue = new(scenario.Core, new AiOrchestrationQueueOptions { MaxConcurrent = 1 }, scenario.ScopeProvider);
            Task<string> blocker = queue.RunTaskAsync(new AiTaskRequest { RoleId = "Other", Hint = "blocker" });
            await EventuallyAsync(() => scenario.Provider.CallCount == 1);
            AiTaskRequest request = new() { RoleId = "Teacher", Hint = "admitted-question", SourceTag = "Chat" };
            Task turn = streaming ? DrainAsync(queue.RunStreamingAsync(request)) : queue.RunTaskAsync(request);
            ActorContext admittedActor = request.ActorContext.Value;
            request.ActorContext = Actor("changed-after-admission", "changed-connection");
            scenario.ScopeProvider.Current = second;
            scenario.Provider.Finish(0, "blocker");
            await EventuallyAsync(() => scenario.Provider.CallCount == 2);
            string promptHistory = "";
            foreach (Microsoft.Extensions.AI.ChatMessage message in scenario.Provider.RequestAt(1).ChatHistory) promptHistory += message.Text;
            StringAssert.Contains("first-seed", promptHistory);
            StringAssert.DoesNotContain("second-seed", promptHistory);
            scenario.ScopeProvider.Current = Scope("third");
            scenario.Provider.Finish(1, "admitted-answer");
            await turn;
            await blocker;
            Assert.AreEqual(2, scenario.ScopeProvider.Calls, "Queue admission must capture each effective scope once; the real inner must preserve it.");
            ChatMessage[] admitted = scenario.History(admittedActor);
            Assert.AreEqual(3, admitted.Length);
            Assert.AreEqual("admitted-question", admitted[1].Content);
            Assert.AreEqual("admitted-answer", admitted[2].Content);
            using (AgentMemoryActorScope.Enter(second)) Assert.AreEqual(1, scenario.Memory.GetChatHistory("Teacher").Length);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task MemoryScope_CancelledPendingRealInnerRecordsOnlyAdmissionPartition(bool streaming)
        {
            MemoryScenario scenario = new();
            scenario.ScopeProvider.Current = Scope("first");
            using QueuedAiOrchestrator queue = new(scenario.Core, new AiOrchestrationQueueOptions { MaxConcurrent = 1 }, scenario.ScopeProvider);
            Task<string> blocker = queue.RunTaskAsync(new AiTaskRequest { RoleId = "Other", Hint = "blocker" });
            using CancellationTokenSource cancellation = new();
            AiTaskRequest request = new() { RoleId = "Teacher", Hint = "pending-question", SourceTag = "Chat" };
            Task turn = streaming ? DrainAsync(queue.RunStreamingAsync(request, cancellation.Token)) : queue.RunTaskAsync(request, cancellation.Token);
            scenario.ScopeProvider.Current = Scope("second");
            cancellation.Cancel();
            await EventuallyAsync(() => turn.IsCompleted);
            if (streaming) await turn; else Assert.CatchAsync<OperationCanceledException>(async () => await turn);
            ChatMessage[] history = scenario.History(request.ActorContext.Value);
            Assert.AreEqual(1, history.Length);
            Assert.AreEqual("pending-question", history[0].Content);
            using (AgentMemoryActorScope.Enter(Scope("second"))) Assert.AreEqual(0, scenario.Memory.GetChatHistory("Teacher").Length);
            Assert.AreEqual(1, scenario.Provider.CallCount);
            scenario.Provider.Finish(0, "blocker");
            await blocker;
        }

        [Test]
        public async Task MemoryScope_DirectInterleavedTaskAndStreamKeepSeparateActorsAndRestoreAmbientScope()
        {
            MemoryScenario scenario = new();
            AiTaskRequest first = Request("Teacher", "first-question", Actor("first", "first-connection"));
            AiTaskRequest second = Request("Teacher", "second-question", Actor("second", "second-connection"));
            AgentMemoryScope outer = Scope("ambient");
            using (AgentMemoryActorScope.Enter(outer))
            {
                scenario.ScopeProvider.Current = Scope("first");
                Task<string> firstTurn = scenario.Core.RunTaskAsync(first);
                scenario.ScopeProvider.Current = Scope("second");
                Task<List<LlmStreamChunk>> secondTurn = DrainAsync(scenario.Core.RunStreamingAsync(second));
                await EventuallyAsync(() => scenario.Provider.CallCount == 2);
                scenario.ScopeProvider.Current = Scope("third");
                scenario.Provider.Finish(1, "second-answer");
                await secondTurn;
                scenario.Provider.Finish(0, "first-answer");
                await firstTurn;
                Assert.AreEqual(2, scenario.ScopeProvider.Calls);
                Assert.AreEqual("first-question", scenario.History(first.ActorContext.Value)[0].Content);
                Assert.AreEqual("first-answer", scenario.History(first.ActorContext.Value)[1].Content);
                Assert.AreEqual("second-question", scenario.History(second.ActorContext.Value)[0].Content);
                Assert.AreEqual("second-answer", scenario.History(second.ActorContext.Value)[1].Content);
                Assert.AreEqual(0, scenario.Memory.GetChatHistory("Teacher").Length, "Direct turns must restore the caller's outer memory context.");
                scenario.Memory.AppendChatMessage("Teacher", "user", "ambient-marker", false);
            }
            using (AgentMemoryActorScope.Enter(outer)) Assert.AreEqual("ambient-marker", scenario.Memory.GetChatHistory("Teacher")[0].Content);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task MemoryScope_DirectFrozenEmptyDoesNotFallBackAfterProviderChanges(bool streaming)
        {
            MemoryScenario scenario = new();
            scenario.ScopeProvider.Current = AgentMemoryScope.Empty;
            AiTaskRequest request = new() { RoleId = "Teacher", Hint = "empty-scope-question", SourceTag = "Chat" };
            Task turn = streaming ? DrainAsync(scenario.Core.RunStreamingAsync(request)) : scenario.Core.RunTaskAsync(request);
            await EventuallyAsync(() => scenario.Provider.CallCount == 1);
            scenario.ScopeProvider.Current = Scope("new-user");
            scenario.Provider.Finish(0, "empty-scope-answer");
            await turn;
            Assert.AreEqual(1, scenario.ScopeProvider.Calls);
            Assert.AreEqual(2, scenario.History(request.ActorContext.Value).Length);
            using (AgentMemoryActorScope.Enter(Scope("new-user"))) Assert.AreEqual(0, scenario.Memory.GetChatHistory("Teacher").Length);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task MemoryScope_DirectFailureOrCancellationRestoresAmbientScope(bool cancellation)
        {
            MemoryScenario scenario = new();
            using CancellationTokenSource token = new();
            using (AgentMemoryActorScope.Enter(Scope("outer")))
            {
                scenario.ScopeProvider.Current = Scope("first");
                AiTaskRequest request = new() { RoleId = "Teacher", Hint = "failed-question", SourceTag = "Chat" };
                Task<LlmCompletionResult> turn = scenario.Core.RunTaskResultAsync(request, token.Token);
                await EventuallyAsync(() => scenario.Provider.CallCount == 1);
                scenario.ScopeProvider.Current = Scope("second");
                if (cancellation) token.Cancel(); else scenario.Provider.Fail(0);
                if (cancellation) Assert.CatchAsync<OperationCanceledException>(async () => await turn);
                else Assert.IsFalse((await turn).Ok);
                Assert.AreEqual("failed-question", scenario.History(request.ActorContext.Value)[0].Content);
                Assert.AreEqual(0, scenario.Memory.GetChatHistory("Teacher").Length);
            }
        }

        [Test]
        public void MemoryScope_NarrowGrantsPreservesFrozenEmptyBindingAndIdentity()
        {
            MutableScopeProvider provider = new() { Current = AgentMemoryScope.Empty };
            ActorContext original = new LocalActorIdentityProvider("actor", "connection", "world", ActorGrantSet.Create(new[] { "a", "b" }), AgentMemoryScope.Empty).GetActorContext("Teacher");
            ActorContext bound = AgentMemoryActorScope.Bind(original, provider, "Teacher").NarrowGrants(ActorGrantSet.Create(new[] { "a" }));
            provider.Current = Scope("changed");
            ActorContext rebound = AgentMemoryActorScope.Bind(bound, provider, "Teacher");
            Assert.IsTrue(rebound.IsTrusted);
            Assert.IsTrue(rebound.HasBoundMemoryScope);
            Assert.AreEqual(original.ActorId, rebound.ActorId);
            Assert.AreEqual(original.SessionId, rebound.SessionId);
            Assert.AreEqual(original.RoleId, rebound.RoleId);
            Assert.AreEqual(original.WorldId, rebound.WorldId);
            Assert.IsTrue(rebound.Grants.Contains("a"));
            Assert.IsFalse(rebound.Grants.Contains("b"));
            Assert.AreEqual("", rebound.MemoryScope.UserId);
            Assert.AreEqual(1, provider.Calls);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ActorCancellation_UnscopedSameRoleLeavesOtherActorAlive(bool streaming)
        {
            GateOrchestrator inner = new();
            using QueuedAiOrchestrator queue = new(inner, new AiOrchestrationQueueOptions { MaxConcurrent = 2 });
            ActorContext firstActor = Actor("first", "connection-a");
            ActorContext secondActor = Actor("second", "connection-b");
            AiTaskRequest firstRequest = Request("Teacher", "first", firstActor);
            Task first = streaming ? DrainAsync(queue.RunStreamingAsync(firstRequest)) : queue.RunTaskAsync(firstRequest);
            Task<string> second = queue.RunTaskAsync(Request("Teacher", "second", secondActor));
            Task<string> pending = queue.RunTaskAsync(Request("Teacher", "pending", firstActor));
            try
            {
                await EventuallyAsync(() => inner.Gates.Count == 2);
                ((IActorAiTaskCancellation)queue).CancelActorTasks(firstActor, " Teacher ");
                await EventuallyAsync(() => first.IsCompleted && pending.IsCompleted);
                if (streaming)
                {
                    List<LlmStreamChunk> chunks = await (Task<List<LlmStreamChunk>>)first;
                    Assert.AreEqual(LlmErrorCode.Cancelled, chunks[chunks.Count - 1].ErrorCode);
                }
                else
                {
                    Assert.CatchAsync<OperationCanceledException>(async () => await first);
                }
                Assert.CatchAsync<OperationCanceledException>(async () => await pending);
                Assert.IsFalse(second.IsCompleted);
                Assert.AreEqual(1, inner.Unstarted);
                inner.Gates[1].TrySetResult("survived");
                Assert.AreEqual("survived", await second);
            }
            finally
            {
                foreach (TaskCompletionSource<string> gate in inner.Gates) gate.TrySetResult("released");
            }
        }

        [Test]
        public void ActorCancellation_RequiresTrustedContext()
        {
            using QueuedAiOrchestrator queue = new(new GateOrchestrator(), new AiOrchestrationQueueOptions());
            Assert.Throws<InvalidOperationException>(() => ((IActorAiTaskCancellation)queue).CancelActorTasks(default));
        }

        [TestCase(0, 1024)]
        [TestCase(2, 0)]
        [TestCase(-1, 1024)]
        public void StreamBudget_RequiresPositiveLimits(int chunks, int bytes)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new QueuedAiOrchestrator(new GateOrchestrator(),
                new AiOrchestrationQueueOptions { MaxBufferedStreamChunks = chunks, MaxBufferedStreamBytes = bytes }));
        }

        [Test]
        public async Task StreamBudget_PausedConsumerStopsUpstreamAndResumesWithoutLoss()
        {
            List<LlmStreamChunk> supplied = new();
            for (int index = 0; index < 8; index++) supplied.Add(new LlmStreamChunk { Text = index.ToString() });
            supplied.Add(new LlmStreamChunk { IsDone = true, TotalTokens = 37, ExecutedToolCalls = new[] { new LlmToolCallTrace("effect", true, 1, "test", "kept") } });
            BudgetOrchestrator inner = new(supplied);
            using QueuedAiOrchestrator queue = new(inner, new AiOrchestrationQueueOptions { MaxBufferedStreamChunks = 2, MaxBufferedStreamBytes = 1024 });
            await using IAsyncEnumerator<LlmStreamChunk> stream = queue.RunStreamingAsync(new AiTaskRequest()).GetAsyncEnumerator();
            Assert.IsTrue(await stream.MoveNextAsync());
            Assert.AreEqual("0", stream.Current.Text);
            await EventuallyAsync(() => inner.Produced == 4);
            (int BufferedChunks, long BufferedBytes, bool HasOversizedChunk)[] snapshot = queue.GetStreamingBufferSnapshotsForTests();
            Assert.AreEqual(1, snapshot.Length);
            Assert.AreEqual(2, snapshot[0].BufferedChunks);
            Assert.LessOrEqual(snapshot[0].BufferedBytes, 1024);
            Assert.IsFalse(inner.Finished.Task.IsCompleted);
            string text = stream.Current.Text;
            LlmStreamChunk terminal = null;
            while (await stream.MoveNextAsync())
            {
                text += stream.Current.Text;
                if (stream.Current.IsDone) terminal = stream.Current;
            }
            Assert.AreEqual("01234567", text);
            Assert.IsNotNull(terminal);
            Assert.AreEqual(37, terminal.TotalTokens);
            Assert.AreEqual("kept", terminal.ExecutedToolCalls[0].Detail);
            await EventuallyAsync(() => inner.Finished.Task.IsCompleted);
        }

        [Test]
        public async Task StreamBudget_BytesPauseWriterBeforeChunkCountLimit()
        {
            QueuedAiOrchestrator.AsyncChunkQueue buffer = new(8, 256);
            await buffer.WriteAsync(new LlmStreamChunk { Text = new string('a', 64) }, CancellationToken.None);
            Task next = buffer.WriteAsync(new LlmStreamChunk { Text = "b" }, CancellationToken.None).AsTask();
            Assert.IsFalse(next.IsCompleted, "Retained bytes must apply even below the chunk cap.");
            Assert.AreEqual(1, buffer.BufferedCount);
            Assert.LessOrEqual(buffer.BufferedBytes, 256);
            Assert.IsTrue(buffer.TryTake(out LlmStreamChunk first));
            Assert.AreEqual(new string('a', 64), first.Text);
            await next;
            Assert.IsTrue(buffer.TryTake(out LlmStreamChunk second));
            Assert.AreEqual("b", second.Text);
            buffer.Complete();
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task StreamBudget_OversizedTerminalWaitsForHandoffAndSurvivesCancellation(bool dispose)
        {
            string detail = new('x', 1024);
            BudgetOrchestrator inner = new(new[]
            {
                new LlmStreamChunk { Text = "before" },
                new LlmStreamChunk { Text = "last", IsDone = true, TotalTokens = 37, ExecutedToolCalls = new[] { new LlmToolCallTrace("effect", true, 1, "test", detail) } }
            });
            using CancellationTokenSource cancellation = new();
            using QueuedAiOrchestrator queue = new(inner, new AiOrchestrationQueueOptions { MaxBufferedStreamChunks = 2, MaxBufferedStreamBytes = 512 });
            await using IAsyncEnumerator<LlmStreamChunk> stream = queue.RunStreamingAsync(new AiTaskRequest(), cancellation.Token).GetAsyncEnumerator();
            Assert.IsTrue(await stream.MoveNextAsync());
            Assert.AreEqual("before", stream.Current.Text);
            await EventuallyAsync(() => queue.GetStreamingBufferSnapshotsForTests()[0].HasOversizedChunk);
            Assert.AreEqual(2, inner.Produced);
            Assert.IsFalse(inner.Finished.Task.IsCompleted, "Upstream cannot move past an oversized chunk before handoff.");
            Assert.AreEqual(0, queue.GetStreamingBufferSnapshotsForTests()[0].BufferedChunks);
            if (dispose) queue.Dispose(); else cancellation.Cancel();
            await EventuallyAsync(() => inner.Finished.Task.IsCompleted);
            Assert.IsTrue(await stream.MoveNextAsync());
            Assert.IsTrue(stream.Current.IsDone);
            Assert.AreEqual("last", stream.Current.Text);
            Assert.AreEqual(37, stream.Current.TotalTokens);
            Assert.AreEqual(detail, stream.Current.ExecutedToolCalls[0].Detail);
            Assert.AreEqual(LlmErrorCode.Cancelled, stream.Current.ErrorCode);
            Assert.IsFalse(await stream.MoveNextAsync());
        }

        [Test]
        public async Task StreamBudget_OversizedChunkHandoffPreservesOrderAndAbandonmentReleasesWriter()
        {
            string oversized = new('x', 1024);
            BudgetOrchestrator inner = new(new[] { new LlmStreamChunk { Text = "first" }, new LlmStreamChunk { Text = oversized }, new LlmStreamChunk { IsDone = true, TotalTokens = 37 } });
            using QueuedAiOrchestrator queue = new(inner, new AiOrchestrationQueueOptions { MaxBufferedStreamChunks = 2, MaxBufferedStreamBytes = 512 });
            IAsyncEnumerator<LlmStreamChunk> stream = queue.RunStreamingAsync(new AiTaskRequest()).GetAsyncEnumerator();
            Assert.IsTrue(await stream.MoveNextAsync());
            Assert.AreEqual("first", stream.Current.Text);
            await EventuallyAsync(() => queue.GetStreamingBufferSnapshotsForTests()[0].HasOversizedChunk);
            Assert.IsTrue(await stream.MoveNextAsync());
            Assert.AreEqual(oversized, stream.Current.Text);
            await stream.DisposeAsync();
            await EventuallyAsync(() => inner.Finished.Task.IsCompleted);
        }

        private static ActorContext Actor(string actorId, string sessionId) =>
            new LocalActorIdentityProvider(actorId, sessionId, "world", ActorGrantSet.None, AgentMemoryScope.Empty).GetActorContext("Teacher");

        private static AgentMemoryScope Scope(string userId) => new("tenant", userId, "lesson", "topic");

        private static AiTaskRequest Request(string roleId, string hint, ActorContext actor) => new() { RoleId = roleId, Hint = hint, SourceTag = "Chat", ActorContext = actor };

        private static async Task<List<LlmStreamChunk>> DrainAsync(IAsyncEnumerable<LlmStreamChunk> source)
        {
            List<LlmStreamChunk> chunks = new();
            await foreach (LlmStreamChunk chunk in source) chunks.Add(chunk);
            return chunks;
        }

        private static async Task EventuallyAsync(Func<bool> condition)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(1);
            Assert.IsTrue(condition(), "The controlled asynchronous operation did not reach its expected state.");
        }

        private sealed class MutableScopeProvider : IAgentMemoryScopeProvider
        {
            public AgentMemoryScope Current;
            public int Calls;
            public AgentMemoryScope GetScope(string roleId) { Calls++; return Current; }
        }

        private sealed class MemoryScenario
        {
            public readonly MutableScopeProvider ScopeProvider = new();
            public readonly GatedClient Provider = new();
            public readonly ScopedAgentMemoryStoreDecorator Memory;
            public readonly AiOrchestrator Core;

            public MemoryScenario()
            {
                Memory = new ScopedAgentMemoryStoreDecorator(new InMemoryAgentMemoryStore(), ScopeProvider);
                AgentMemoryPolicy policy = new();
                policy.ConfigureChatHistory("Teacher", true, 4096, false);
                policy.ConfigureChatHistory("Other", true, 4096, false);
                Core = new AiOrchestrator(new Authority(), Provider, new Sink(), new Telemetry(),
                    new AiPromptComposer(new BuiltInDefaultAgentSystemPromptProvider(), new NoAgentUserPromptTemplateProvider(), null),
                    Memory, policy, null, null, new CoreAISettingsOptions { EnableStreaming = false },
                     new LocalActorIdentityProvider(), null, null, null, null, null, ScopeProvider);
            }

            public void Seed(AgentMemoryScope scope, string text)
            {
                using (AgentMemoryActorScope.Enter(scope)) Memory.AppendChatMessage("Teacher", "assistant", text, false);
            }

            public ChatMessage[] History(ActorContext actor)
            {
                using (AgentMemoryActorScope.Enter(actor)) return Memory.GetChatHistory("Teacher");
            }
        }

        private sealed class Authority : IAuthorityHost
        {
            public bool CanRunAiTasks => true;
            public bool IsServer => true;
            public bool IsClient => true;
        }

        private sealed class Sink : IAiGameCommandSink { public void Publish(ApplyAiGameCommand command) { } }
        private sealed class Telemetry : ISessionTelemetryProvider { public GameSessionSnapshot BuildSnapshot() => new(); }

        private sealed class GatedClient : ILlmClient
        {
            private readonly object _gate = new();
            private readonly List<LlmCompletionRequest> _requests = new();
            private readonly List<TaskCompletionSource<LlmCompletionResult>> _results = new();
            public int CallCount { get { lock (_gate) return _requests.Count; } }
            public LlmCompletionRequest RequestAt(int index) { lock (_gate) return _requests[index]; }
            public void Finish(int index, string content) { lock (_gate) _results[index].TrySetResult(new LlmCompletionResult { Ok = true, Content = content }); }
            public void Fail(int index) { lock (_gate) _results[index].TrySetResult(new LlmCompletionResult { Ok = false, ErrorCode = LlmErrorCode.ProviderError, Error = "Expected test failure." }); }
            public async Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request, CancellationToken cancellationToken = default)
            {
                TaskCompletionSource<LlmCompletionResult> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_gate) { _requests.Add(request); _results.Add(result); }
                using CancellationTokenRegistration registration = cancellationToken.Register(() => result.TrySetCanceled(cancellationToken));
                return await result.Task;
            }
        }

        private sealed class GateOrchestrator : IAiOrchestrationService, IUnstartedAiTurnRecorder
        {
            public readonly List<TaskCompletionSource<string>> Gates = new();
            public int Unstarted;
            public async Task<string> RunTaskAsync(AiTaskRequest task, CancellationToken cancellationToken = default)
            {
                TaskCompletionSource<string> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
                Gates.Add(gate);
                using CancellationTokenRegistration registration = cancellationToken.Register(() => gate.TrySetCanceled(cancellationToken));
                return await gate.Task;
            }
            public async IAsyncEnumerable<LlmStreamChunk> RunStreamingAsync(AiTaskRequest task, [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                string result = await RunTaskAsync(task, cancellationToken);
                yield return new LlmStreamChunk { Text = result, IsDone = true };
            }
            public void CancelTasks(string cancellationScope) { }
            public void RecordUnstartedUserTurn(AiTaskRequest task) { Unstarted++; }
        }

        private sealed class BudgetOrchestrator : IAiOrchestrationService
        {
            private readonly IReadOnlyList<LlmStreamChunk> _chunks;
            public int Produced;
            public readonly TaskCompletionSource<bool> Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public BudgetOrchestrator(IReadOnlyList<LlmStreamChunk> chunks) { _chunks = chunks; }
            public Task<string> RunTaskAsync(AiTaskRequest task, CancellationToken cancellationToken = default) => Task.FromResult("unused");
            public void CancelTasks(string cancellationScope) { }
            public async IAsyncEnumerable<LlmStreamChunk> RunStreamingAsync(AiTaskRequest task, [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.CompletedTask;
                try
                {
                    foreach (LlmStreamChunk chunk in _chunks)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Interlocked.Increment(ref Produced);
                        yield return chunk;
                    }
                }
                finally { Finished.TrySetResult(true); }
            }
        }
    }
}
