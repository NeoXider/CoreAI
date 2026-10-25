using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Authority;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    public sealed class MemoryContractRegressionTests
    {
        private SynchronizationContext _previous;
        [SetUp] public void SetUp() { _previous = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(null); }
        [TearDown] public void TearDown() { SynchronizationContext.SetSynchronizationContext(_previous); }

        [TestCase("local")]
        [TestCase("learner")]
        public void BoundActor_ProviderChanges_DoNotMoveMemoryPartition(string actorId)
        {
            MutableProvider provider = new() { Scope = new AgentMemoryScope("tenant", "A", "lesson", "topic") };
            ActorContext actor = new LocalActorIdentityProvider(actorId).GetActorContext("teacher");
            ActorContext bound = AgentMemoryActorScope.Bind(actor, provider, "teacher");
            InMemoryAgentMemoryStore backing = new();
            ScopedAgentMemoryStoreDecorator store = new(backing, provider);
            using (AgentMemoryActorScope.Enter(bound)) store.Save("teacher", new AgentMemoryState { Memory = "A" });
            provider.Scope = new AgentMemoryScope("tenant", "B", "lesson", "topic");
            using (AgentMemoryActorScope.Enter(bound))
            {
                Assert.IsTrue(store.TryLoad("teacher", out AgentMemoryState state));
                Assert.AreEqual("A", state.Memory);
            }
            Assert.AreEqual(1, provider.Calls, "An admitted actor resolves a fallback provider once.");
            using (AgentMemoryActorScope.Enter(actor, provider, "teacher")) Assert.IsFalse(store.TryLoad("teacher", out _));
        }

        [Test]
        public void BoundEmptyScope_ProviderLaterBecomesNonempty_RemainsLegacyPartition()
        {
            MutableProvider provider = new();
            ActorContext actor = new LocalActorIdentityProvider().GetActorContext("teacher");
            ActorContext bound = AgentMemoryActorScope.Bind(actor, provider, "teacher");
            provider.Scope = new AgentMemoryScope("tenant", "B", "", "");
            InMemoryAgentMemoryStore backing = new();
            using (AgentMemoryActorScope.Enter(bound))
                new ScopedAgentMemoryStoreDecorator(backing, provider).Save("teacher", new AgentMemoryState { Memory = "legacy" });
            Assert.IsTrue(backing.TryLoad("teacher", out AgentMemoryState state));
            Assert.AreEqual("legacy", state.Memory);
            Assert.AreEqual(1, provider.Calls);
        }

        [Test]
        public void MemoryRead_PreCancelled_DoesNotLoadOrDiscloseStorage()
        {
            AtomicStore store = new();
            using CancellationTokenSource stop = new();
            stop.Cancel();
            Assert.CatchAsync<OperationCanceledException>(async () => await new MemoryTool(store, "role")
                .ExecuteAsync("read", cancellationToken: stop.Token));
            Assert.AreEqual(0, store.Loads);
        }

        [Test]
        public async Task MemoryMutation_PendingCancellation_EscapesToolBoundary()
        {
            AtomicStore store = new() { Block = true };
            using CancellationTokenSource stop = new();
            Task<string> pending = new MemoryTool(store, "role").ExecuteAsync("append", "fact", cancellationToken: stop.Token);
            Assert.IsFalse(pending.IsCompleted);
            stop.Cancel();
            store.Release.TrySetResult(true);
            Exception fault = await Capture(pending);
            Assert.IsInstanceOf<OperationCanceledException>(fault);
            Assert.IsTrue(string.IsNullOrEmpty(store.State.Memory));
        }

        [Test]
        public async Task MemoryMutation_CancellationAfterCommit_ReturnsCommittedSuccess()
        {
            using CancellationTokenSource stop = new();
            AtomicStore store = new() { AfterCommit = stop.Cancel };
            string result = await new MemoryTool(store, "role").ExecuteAsync("append", "fact", cancellationToken: stop.Token);
            StringAssert.Contains("fact", store.State.Memory);
            StringAssert.Contains("true", result.ToLowerInvariant());
            Assert.IsTrue(stop.IsCancellationRequested);
        }

#if COREAI_LLM
        [Test]
        public async Task MemoryMutation_PolicyInvocation_PreservesCallerCancellation()
        {
            AtomicStore store = new() { Block = true };
            Microsoft.Extensions.AI.AIFunction function = new MemoryTool(store, "role").CreateAIFunction();
            CoreAI.Infrastructure.Llm.ToolExecutionPolicy policy = new(null,
                new CoreAI.CoreAISettingsOptions { DefaultToolTimeoutMs = 0 },
                new ILlmTool[] { new CoreAI.AgentMemory.MemoryLlmTool() }, false, "role");
            Microsoft.Extensions.AI.ChatOptions options = new()
            { Tools = new List<Microsoft.Extensions.AI.AITool> { function } };
            Microsoft.Extensions.AI.FunctionCallContent call = new("call-1", "memory",
                new Dictionary<string, object> { ["action"] = "append", ["content"] = "fact" });
            using CancellationTokenSource stop = new();
            Task pending = policy.ExecuteSingleAsync(call, options, stop.Token);
            Assert.IsFalse(pending.IsCompleted);
            stop.Cancel();
            store.Release.TrySetResult(true);
            Assert.IsInstanceOf<OperationCanceledException>(await Capture(pending));
            Assert.IsTrue(string.IsNullOrEmpty(store.State.Memory));
        }
#endif

        [Test]
        public async Task Inventory_PendingCancellation_EscapesToolBoundary()
        {
            PendingInventory provider = new();
            using CancellationTokenSource stop = new();
            Task<string> pending = new InventoryTool(provider).ExecuteAsync(stop.Token);
            stop.Cancel();
            provider.Release.TrySetCanceled();
            Assert.IsInstanceOf<OperationCanceledException>(await Capture(pending));
        }

        [Test]
        public void PayloadCap_LongPriorSummary_IncludesFramingAndFirstDialogueLine()
        {
            LlmContextCompactionOptions options = new() { MaxPayloadChars = 12000 };
            string payload = LlmAssistedConversationContextManager.BuildCompactionUserPayload(new string('p', 20000),
                new[] { new ChatMessage("user", "new fact") }, 1, 0, options, out _, out int folded);
            Assert.LessOrEqual(payload.Length, 12000);
            StringAssert.Contains("new fact", payload);
            StringAssert.Contains("Output a compact updated rolling summary", payload);
            Assert.AreEqual(1, folded);
        }

        [Test]
        public void PayloadCap_ImpossibleFramingBudget_RejectsBeforeClaimingFold()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => LlmAssistedConversationContextManager.BuildCompactionUserPayload(
                new string('p', 20000), new[] { new ChatMessage("user", "fact") }, 1, 0,
                new LlmContextCompactionOptions { MaxPayloadChars = 16 }, out _, out _));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Summary_DeferredSnapshotAfterClear_CannotResurrectFacts(bool fileStore)
        {
            string root = Path.Combine(Path.GetTempPath(), "coreai-summary-contract-" + Guid.NewGuid().ToString("N"));
            IConversationSummaryStore store = fileStore ? new FileConversationSummaryStore(root) : new InMemoryConversationSummaryStore();
            try
            {
                store.SaveSummary("role", "erased fact");
                ConversationContextSnapshot snapshot = Snapshot(store, 4);
                store.ClearSummary("role");
                Assert.Throws<InvalidOperationException>(() => snapshot.Commit());
                Assert.AreEqual("", store.LoadSummary("role"));
            }
            finally { (store as IDisposable)?.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Summary_ReverseCommitOrder_RejectsOlderSnapshot(bool fileStore)
        {
            string root = Path.Combine(Path.GetTempPath(), "coreai-summary-order-" + Guid.NewGuid().ToString("N"));
            IConversationSummaryStore store = fileStore ? new FileConversationSummaryStore(root) : new InMemoryConversationSummaryStore();
            try
            {
                ConversationContextSnapshot older = Snapshot(store, 4);
                ConversationContextSnapshot newer = Snapshot(store, 7);
                newer.Commit();
                string accepted = store.LoadSummary("role");
                Assert.Throws<InvalidOperationException>(() => older.Commit());
                Assert.AreEqual(accepted, store.LoadSummary("role"));
                StringAssert.Contains("fact-5", accepted);
            }
            finally { (store as IDisposable)?.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        [Test]
        public async Task Summary_ClearDuringAuxiliaryCompletion_InvalidatesPreparedCommit()
        {
            InMemoryConversationSummaryStore store = new();
            store.SaveSummary("role", "old lesson");
            PendingCompactor llm = new();
            LlmAssistedConversationContextManager manager = new(store, new HeuristicTokenEstimator(), llm);
            Task<ConversationContextSnapshot> building = manager.BuildSnapshotAsync("role", History(6),
                new AgentMemoryPolicy.RoleMemoryConfig { MaxChatHistoryMessages = 1 },
                new ConversationContextBuildArgs { DeferSummaryPersistence = true, HistoryTokenBudget = 32 }, "trace", CancellationToken.None);
            Assert.IsTrue(llm.Entered);
            store.ClearSummary("role");
            llm.Release.SetResult(new LlmCompletionResult { Ok = true, Content = "old lesson recap" });
            ConversationContextSnapshot snapshot = await building;
            Assert.CatchAsync<InvalidOperationException>(async () => await snapshot.CommitAsync());
            Assert.AreEqual("", store.LoadSummary("role"));
        }

        [Test]
        public async Task SharedPathLease_QueuedWaiter_RetainsTheSameGateUntilItSettles()
        {
            SharedPathStateRegistry<GateState> registry = new();
            SharedPathStateRegistry<GateState>.Lease holder = registry.Acquire("gate");
            SharedPathStateRegistry<GateState>.Lease waiter = registry.Acquire("gate");
            await holder.State.Gate.WaitAsync();
            Task waiting = waiter.State.Gate.WaitAsync();
            holder.State.Gate.Release();
            holder.Dispose();
            await waiting;
            using (SharedPathStateRegistry<GateState>.Lease peer = registry.Acquire("gate"))
            {
                Assert.AreSame(waiter.State, peer.State);
                Assert.IsFalse(peer.State.Gate.Wait(0));
            }
            waiter.State.Gate.Release();
            waiter.Dispose();
            Assert.AreEqual(0, registry.Count);
        }

        [Test]
        public async Task FileSummary_ScopeChurn_ReclaimsSettledPathStates()
        {
            string root = Path.Combine(Path.GetTempPath(), "coreai-summary-retention-" + Guid.NewGuid().ToString("N"));
            int before = FileConversationSummaryStore.SharedPathStateCount;
            try
            {
                using FileConversationSummaryStore store = new(root);
                for (int index = 0; index < 32; index++)
                {
                    string role = "role-" + index;
                    await store.SaveSummaryAsync(role, "fact");
                    Assert.AreEqual("fact", await store.LoadSummaryAsync(role));
                    await store.ClearSummaryAsync(role);
                }
                Assert.LessOrEqual(FileConversationSummaryStore.SharedPathStateCount, before);
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        [Test]
        public async Task FileSummary_FailedConfirmation_IsObservableAndRecoverableAcrossInstances()
        {
            string root = Path.Combine(Path.GetTempPath(), "coreai-summary-confirmation-" + Guid.NewGuid().ToString("N"));
            int before = FileConversationSummaryStore.PendingConfirmationCount;
            bool confirmed = false;
            try
            {
                using FileConversationSummaryStore store = new(root, afterWriteAsync: _ => Task.FromResult(confirmed));
                Assert.IsInstanceOf<IOException>(await Capture(store.SaveSummaryAsync("role", "fact")));
                Assert.GreaterOrEqual(FileConversationSummaryStore.PendingConfirmationCount, before + 1);
                confirmed = true;
                using FileConversationSummaryStore peer = new(root);
                Assert.AreEqual("fact", await peer.LoadSummaryAsync("role"));
                Assert.LessOrEqual(FileConversationSummaryStore.PendingConfirmationCount, before);
            }
            finally { confirmed = true; if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        [Test]
        public void ScopeEncoding_PreservesWhitespaceAndRejectsMalformedUnicode()
        {
            string first = AgentMemoryScopeKey.Resolve(new AgentMemoryScope("tenant", "alice", "lesson", ""), "role");
            string second = AgentMemoryScopeKey.Resolve(new AgentMemoryScope("tenant", " alice ", "lesson", ""), "role");
            Assert.AreNotEqual(first, second);
            Assert.Throws<System.Text.EncoderFallbackException>(() => AgentMemoryScopeKey.Resolve(
                new AgentMemoryScope("tenant", "\ud800", "lesson", ""), "role"));
            Assert.Throws<System.Text.EncoderFallbackException>(() => AgentMemoryFileKey.FileStem("\ud800"));
        }

        [TestCase("Math", "math")]
        [TestCase("a/b", "a_b_3a8e75c1")]
        public async Task FileSummary_LogicalRoleAliases_RemainIndependent(string first, string second)
        {
            if (first == "a/b") second = AgentMemoryFileKey.LegacyFileStem(first);
            string root = Path.Combine(Path.GetTempPath(), "coreai-summary-keys-" + Guid.NewGuid().ToString("N"));
            try
            {
                using FileConversationSummaryStore store = new(root);
                using FileConversationSummaryStore peer = new(root);
                await Task.WhenAll(store.SaveSummaryAsync(first, "first fact"), peer.SaveSummaryAsync(second, "second fact"));
                Assert.AreEqual("first fact", await peer.LoadSummaryAsync(first));
                Assert.AreEqual("second fact", await store.LoadSummaryAsync(second));
                await store.ClearSummaryAsync(first);
                Assert.AreEqual("", await store.LoadSummaryAsync(first));
                Assert.AreEqual("second fact", await peer.LoadSummaryAsync(second));
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        [Test]
        public async Task FileSummary_LegacyMigration_PreservesBackupAndRejectsConflictingCopies()
        {
            string root = Path.Combine(Path.GetTempPath(), "coreai-summary-legacy-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string legacy = Path.Combine(root, "teacher.json");
            const string bytes = "{\"Summary\":\"legacy fact\"}";
            File.WriteAllText(legacy, bytes);
            try
            {
                using FileConversationSummaryStore store = new(root);
                Assert.AreEqual("legacy fact", await store.LoadSummaryAsync("teacher"));
                Assert.AreEqual(bytes, File.ReadAllText(legacy + ".legacy"));
                await store.SaveSummaryAsync("teacher", "new fact");
                File.WriteAllText(legacy, "{\"Summary\":\"conflicting fact\"}");
                Assert.IsInstanceOf<InvalidDataException>(await Capture(store.ClearSummaryAsync("teacher")));
                Assert.IsTrue(File.Exists(legacy));
                Assert.IsTrue(File.Exists(Path.Combine(root, AgentMemoryFileKey.FileStem("teacher") + ".json")));
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task SummaryPreparation_ProjectionException_ReleasesGenerationImmediately(bool asynchronous)
        {
            string root = SummaryLifetimeRoot();
            int before = FileConversationSummaryStore.SharedPathStateCount;
            try
            {
                using FileConversationSummaryStore store = new(root);
                store.SaveSummary("role", "retained fact");
                DeterministicConversationContextManager manager = new(store, new ThrowingEstimator());
                if (asynchronous)
                    Assert.IsInstanceOf<InvalidOperationException>(await Capture(manager.BuildSnapshotAsync("role", History(6),
                        SummaryRole(), DeferredSummary(), "trace", CancellationToken.None)));
                else Assert.Throws<InvalidOperationException>(() => manager.BuildSnapshot("role", History(6), SummaryRole(), DeferredSummary()));
                Assert.AreEqual(before, FileConversationSummaryStore.SharedPathStateCount);
                Assert.AreEqual("retained fact", store.LoadSummary("role"));
            }
            finally { DeleteSummaryLifetimeRoot(root); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task SummaryPreparation_CancelledAfterOpen_ReleasesGenerationImmediately(bool assisted)
        {
            string root = SummaryLifetimeRoot();
            int before = FileConversationSummaryStore.SharedPathStateCount;
            try
            {
                using FileConversationSummaryStore file = new(root);
                file.SaveSummary("role", "retained fact");
                using CancellationTokenSource stop = new();
                ControlledSummaryStore store = new(file) { AfterOpen = stop.Cancel };
                IAsyncConversationContextManager manager = assisted
                    ? new LlmAssistedConversationContextManager(store, new HeuristicTokenEstimator(), new FaultingCompactor())
                    : new DeterministicConversationContextManager(store);
                Assert.IsInstanceOf<OperationCanceledException>(await Capture(manager.BuildSnapshotAsync("role", History(6),
                    SummaryRole(), DeferredSummary(), "trace", stop.Token)));
                Assert.AreEqual(before, FileConversationSummaryStore.SharedPathStateCount);
                Assert.AreEqual("retained fact", file.LoadSummary("role"));
            }
            finally { DeleteSummaryLifetimeRoot(root); }
        }

        [Test]
        public async Task SummaryPreparation_RepeatedAuxiliaryCancellation_DoesNotRetainAbortedPaths()
        {
            string root = SummaryLifetimeRoot();
            int before = FileConversationSummaryStore.SharedPathStateCount;
            try
            {
                using FileConversationSummaryStore store = new(root);
                for (int index = 0; index < 8; index++)
                {
                    PendingCompactor llm = new();
                    LlmAssistedConversationContextManager manager = new(store, new HeuristicTokenEstimator(), llm);
                    using CancellationTokenSource stop = new();
                    Task<ConversationContextSnapshot> pending = manager.BuildSnapshotAsync("cancelled-" + index, History(6),
                        SummaryRole(), DeferredSummary(), "trace", stop.Token);
                    Assert.AreSame(llm.EnteredTask, await Task.WhenAny(llm.EnteredTask, pending, Task.Delay(5000)),
                        "The controlled auxiliary boundary must be reached before cancellation.");
                    stop.Cancel();
                    llm.Release.TrySetCanceled();
                    Assert.IsInstanceOf<OperationCanceledException>(await Capture(pending));
                    Assert.AreEqual(before, FileConversationSummaryStore.SharedPathStateCount,
                        "Each aborted preparation must release its path without a garbage collection.");
                }
            }
            finally { DeleteSummaryLifetimeRoot(root); }
        }

        [Test]
        public async Task SummaryPreparation_AuxiliaryFailure_FallbackSnapshotCanBeDiscardedWithoutWriting()
        {
            string root = SummaryLifetimeRoot();
            int before = FileConversationSummaryStore.SharedPathStateCount;
            try
            {
                using FileConversationSummaryStore store = new(root);
                store.SaveSummary("role", "retained fact");
                LlmAssistedConversationContextManager manager = new(store, new HeuristicTokenEstimator(), new FaultingCompactor());
                using ConversationContextSnapshot snapshot = await manager.BuildSnapshotAsync("role", History(6),
                    SummaryRole(), DeferredSummary(), "trace", CancellationToken.None);
                Assert.AreEqual(before + 1, FileConversationSummaryStore.SharedPathStateCount);
                string projection = snapshot.Summary;
                ChatMessage[] recent = snapshot.RecentMessages;
                snapshot.Dispose();
                snapshot.Dispose();
                Assert.AreEqual(before, FileConversationSummaryStore.SharedPathStateCount);
                Assert.AreEqual("retained fact", store.LoadSummary("role"));
                Assert.AreEqual(projection, snapshot.Summary);
                Assert.AreSame(recent, snapshot.RecentMessages);
                Assert.CatchAsync<ObjectDisposedException>(async () => await snapshot.CommitAsync());
            }
            finally { DeleteSummaryLifetimeRoot(root); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task SummarySnapshot_CancelledCommit_RetainsGenerationForRetryOrExplicitDiscard(bool retry)
        {
            string root = SummaryLifetimeRoot();
            int before = FileConversationSummaryStore.SharedPathStateCount;
            try
            {
                using FileConversationSummaryStore store = new(root);
                store.SaveSummary("role", "retained fact");
                using ConversationContextSnapshot snapshot = await new DeterministicConversationContextManager(store)
                    .BuildSnapshotAsync("role", History(6), SummaryRole(), DeferredSummary(), "trace", CancellationToken.None);
                using CancellationTokenSource stop = new();
                stop.Cancel();
                Assert.IsInstanceOf<OperationCanceledException>(await Capture(snapshot.CommitAsync(stop.Token)));
                Assert.AreEqual(before + 1, FileConversationSummaryStore.SharedPathStateCount);
                if (retry)
                {
                    await snapshot.CommitAsync();
                    StringAssert.Contains("fact-4", store.LoadSummary("role"));
                }
                snapshot.Dispose();
                Assert.AreEqual(before, FileConversationSummaryStore.SharedPathStateCount);
                if (!retry) Assert.AreEqual("retained fact", store.LoadSummary("role"));
            }
            finally { DeleteSummaryLifetimeRoot(root); }
        }

        [Test]
        public async Task SummarySnapshot_FailedConfirmation_RetryConfirmsCommittedBytesWithoutRewriting()
        {
            string root = SummaryLifetimeRoot();
            int before = FileConversationSummaryStore.SharedPathStateCount;
            bool confirmed = true;
            try
            {
                using FileConversationSummaryStore store = new(root, afterWriteAsync: _ => Task.FromResult(confirmed));
                await store.SaveSummaryAsync("role", "retained fact");
                using ConversationContextSnapshot snapshot = await new DeterministicConversationContextManager(store)
                    .BuildSnapshotAsync("role", History(6), SummaryRole(), DeferredSummary(), "trace", CancellationToken.None);
                confirmed = false;
                Assert.IsInstanceOf<IOException>(await Capture(snapshot.CommitAsync()));
                Assert.AreEqual(before + 1, FileConversationSummaryStore.SharedPathStateCount);
                string path = Path.Combine(root, AgentMemoryFileKey.FileStem("role") + ".json");
                byte[] committed = File.ReadAllBytes(path);
                confirmed = true;
                await snapshot.CommitAsync();
                CollectionAssert.AreEqual(committed, File.ReadAllBytes(path));
                snapshot.Dispose();
                Assert.AreEqual(before, FileConversationSummaryStore.SharedPathStateCount);
                StringAssert.Contains("fact-4", await store.LoadSummaryAsync("role"));
            }
            finally { confirmed = true; DeleteSummaryLifetimeRoot(root); }
        }

        [Test]
        public async Task SummarySnapshot_DisposeDuringCommit_RetainsLeaseUntilActiveAndQueuedCommitsSettle()
        {
            string root = SummaryLifetimeRoot();
            int before = FileConversationSummaryStore.SharedPathStateCount;
            ControlledSummaryStore store = null;
            Task active = null;
            Task queued = null;
            try
            {
                using FileConversationSummaryStore file = new(root);
                file.SaveSummary("role", "retained fact");
                store = new ControlledSummaryStore(file) { GateCommit = true };
                using ConversationContextSnapshot snapshot = await new DeterministicConversationContextManager(store)
                    .BuildSnapshotAsync("role", History(6), SummaryRole(), DeferredSummary(), "trace", CancellationToken.None);
                active = snapshot.CommitAsync();
                Assert.AreSame(store.CommitEntered.Task, await Task.WhenAny(store.CommitEntered.Task, Task.Delay(5000)));
                queued = snapshot.CommitAsync();
                snapshot.Dispose();
                Assert.IsFalse(active.IsCompleted);
                Assert.IsFalse(queued.IsCompleted);
                Assert.AreEqual(before + 1, FileConversationSummaryStore.SharedPathStateCount,
                    "Disposal must not release the generation while an admitted commit may still use it.");
                file.ClearSummary("role");
                store.ReleaseCommit.TrySetResult(true);
                Assert.IsInstanceOf<InvalidOperationException>(await Capture(active));
                Assert.IsInstanceOf<ObjectDisposedException>(await Capture(queued));
                Assert.AreEqual(before, FileConversationSummaryStore.SharedPathStateCount);
                Assert.AreEqual("", file.LoadSummary("role"), "The intervening reset must still invalidate the admitted commit.");
            }
            finally
            {
                store?.ReleaseCommit.TrySetResult(true);
                if (active != null) await Capture(active);
                if (queued != null) await Capture(queued);
                DeleteSummaryLifetimeRoot(root);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
#if !UNITY_5_3_OR_NEWER
        // WHY: Portable NUnit supports this attribute; Unity's stripped NUnit runs EditMode tests sequentially.
        [NonParallelizable]
#endif
        public void InMemorySummary_ChangingWorkingDirectory_DoesNotChangeGenerationIdentity(bool clear)
        {
            string original = Environment.CurrentDirectory;
            string root = SummaryLifetimeRoot();
            string first = Path.Combine(root, "first");
            string second = Path.Combine(root, "second");
            Directory.CreateDirectory(first);
            Directory.CreateDirectory(second);
            ConversationContextSnapshot snapshot = null;
            try
            {
                InMemoryConversationSummaryStore store = new();
                store.SaveSummary("role", "retained fact");
                Environment.CurrentDirectory = first;
                snapshot = Snapshot(store, 6);
                Environment.CurrentDirectory = second;
                if (clear) store.ClearSummary("role");
                else store.SaveSummary("role", "newer fact");
                Assert.Throws<InvalidOperationException>(() => snapshot.Commit());
                Assert.AreEqual(clear ? "" : "newer fact", store.LoadSummary("role"));
            }
            finally
            {
                snapshot?.Dispose();
                Environment.CurrentDirectory = original;
                DeleteSummaryLifetimeRoot(root);
            }
        }

        private static ConversationContextSnapshot Snapshot(IConversationSummaryStore store, int count) =>
            new DeterministicConversationContextManager(store).BuildSnapshot("role", History(count),
                new AgentMemoryPolicy.RoleMemoryConfig { MaxChatHistoryMessages = 1 },
                new ConversationContextBuildArgs { DeferSummaryPersistence = true, HistoryTokenBudget = 32 });
        private static ChatMessage[] History(int count) => Enumerable.Range(0, count).Select(i => new ChatMessage("user", "fact-" + i)).ToArray();
        private static AgentMemoryPolicy.RoleMemoryConfig SummaryRole() => new() { MaxChatHistoryMessages = 1 };
        private static ConversationContextBuildArgs DeferredSummary() => new() { DeferSummaryPersistence = true, HistoryTokenBudget = 32 };
        private static string SummaryLifetimeRoot() => Path.Combine(Path.GetTempPath(), "coreai-summary-lifetime-" + Guid.NewGuid().ToString("N"));
        private static void DeleteSummaryLifetimeRoot(string root) { if (Directory.Exists(root)) Directory.Delete(root, true); }
        private static async Task<Exception> Capture(Task task) { try { await task; return null; } catch (Exception ex) { return ex; } }
        private sealed class MutableProvider : IAgentMemoryScopeProvider
        { internal AgentMemoryScope Scope; internal int Calls; public AgentMemoryScope GetScope(string roleId) { Calls++; return Scope; } }
        private sealed class GateState { public GateState() { } internal readonly SemaphoreSlim Gate = new(1, 1); }
        private sealed class PendingInventory : InventoryTool.IInventoryProvider
        {
            internal readonly TaskCompletionSource<List<InventoryTool.InventoryItem>> Release = new();
            public Task<List<InventoryTool.InventoryItem>> GetInventoryAsync(CancellationToken cancellationToken) => Release.Task;
        }
        private sealed class PendingCompactor : ILlmClient
        {
            internal bool Entered;
            internal readonly TaskCompletionSource<bool> EnteredSignal = new();
            internal Task EnteredTask => EnteredSignal.Task;
            internal readonly TaskCompletionSource<LlmCompletionResult> Release = new();
            public void SetTools(IReadOnlyList<ILlmTool> tools) { }
            public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request, CancellationToken cancellationToken = default)
            { Entered = true; EnteredSignal.TrySetResult(true); return Release.Task; }
        }
        private sealed class FaultingCompactor : ILlmClient
        {
            public void SetTools(IReadOnlyList<ILlmTool> tools) { }
            public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request, CancellationToken cancellationToken = default) =>
                Task.FromException<LlmCompletionResult>(new InvalidOperationException("Controlled auxiliary failure."));
        }
        private sealed class ThrowingEstimator : ITokenEstimator
        { public int EstimateText(string text) => throw new InvalidOperationException("Controlled projection failure."); }
        private sealed class ControlledSummaryStore : IConversationSummaryStore, IAsyncConversationSummaryStore,
            ITransactionalConversationSummaryStore
        {
            private readonly FileConversationSummaryStore _inner;
            internal Action AfterOpen;
            internal bool GateCommit;
            internal readonly TaskCompletionSource<bool> CommitEntered = new();
            internal readonly TaskCompletionSource<bool> ReleaseCommit = new();
            internal ControlledSummaryStore(FileConversationSummaryStore inner) { _inner = inner; }
            public string LoadSummary(string roleId) => _inner.LoadSummary(roleId);
            public void SaveSummary(string roleId, string summary) => _inner.SaveSummary(roleId, summary);
            public void ClearSummary(string roleId) => _inner.ClearSummary(roleId);
            public Task<string> LoadSummaryAsync(string roleId, CancellationToken cancellationToken = default) => _inner.LoadSummaryAsync(roleId, cancellationToken);
            public Task SaveSummaryAsync(string roleId, string summary, CancellationToken cancellationToken = default) => _inner.SaveSummaryAsync(roleId, summary, cancellationToken);
            public Task ClearSummaryAsync(string roleId, CancellationToken cancellationToken = default) => _inner.ClearSummaryAsync(roleId, cancellationToken);
            public ConversationSummaryTransaction OpenSummaryTransaction(string roleId) => Wrap(_inner.OpenSummaryTransaction(roleId));
            public async Task<ConversationSummaryTransaction> OpenSummaryTransactionAsync(string roleId, CancellationToken cancellationToken = default) =>
                Wrap(await _inner.OpenSummaryTransactionAsync(roleId, cancellationToken));
            private ConversationSummaryTransaction Wrap(ConversationSummaryTransaction inner)
            {
                AfterOpen?.Invoke();
                if (!GateCommit) return inner;
                return new ConversationSummaryTransaction(inner.Summary, null, async (value, token) =>
                {
                    CommitEntered.TrySetResult(true);
                    await ReleaseCommit.Task;
                    await inner.CommitAsync(value, token);
                    return true;
                }, inner);
            }
        }
        private sealed class AtomicStore : IAgentMemoryStore, IAtomicAgentMemoryStore
        {
            internal int Loads;
            internal bool Block;
            internal Action AfterCommit;
            internal readonly TaskCompletionSource<bool> Release = new();
            internal AgentMemoryState State = new();
            public bool TryLoad(string roleId, out AgentMemoryState state) { Loads++; state = State; return true; }
            public void Save(string roleId, AgentMemoryState state) { State = state; }
            public void Clear(string roleId) { State = new AgentMemoryState(); }
            public void ClearChatHistory(string roleId) { }
            public void AppendChatMessage(string roleId, string role, string content, bool persistToDisk = true) { }
            public ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0) => Array.Empty<ChatMessage>();
            public async Task<TResult> MutateAsync<TResult>(string roleId, Func<AgentMemoryState, TResult> mutator, CancellationToken cancellationToken = default)
            {
                if (Block) await Release.Task;
                cancellationToken.ThrowIfCancellationRequested();
                TResult result = mutator(State);
                AfterCommit?.Invoke();
                return result;
            }
        }
    }
}
