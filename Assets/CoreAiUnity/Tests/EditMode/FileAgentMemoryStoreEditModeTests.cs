using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Infrastructure.AiMemory;
using CoreAI.Logging;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// The on-disk layout: <see cref="FileAgentMemoryStore"/> keeps the memory document in <c>&lt;role&gt;.json</c>
    /// and the conversation in <c>&lt;role&gt;.history.jsonl</c>. The tests are written from the defect's side: what the
    /// learner loses when the store lies about success, swaps an unread document for an empty one, silently cuts the
    /// history or rewrites everything per message. Each test runs in its own temp directory, writing nothing to the machine.
    /// </summary>
    public sealed class FileAgentMemoryStoreEditModeTests
    {
        private string _roleId;
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _roleId = "EditMode_FileStore_" + Guid.NewGuid().ToString("N");
            _root = Path.Combine(Path.GetTempPath(), "CoreAITestAgentMem_" + Path.GetRandomFileName());
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, true);
                }
            }
            catch
            {
                /* best effort */
            }
        }

        private string MemoryPath => Path.Combine(_root, _roleId + ".json");
        private string HistoryPath => Path.Combine(_root, _roleId + ".history.jsonl");

        private FileAgentMemoryStore NewStore(ILog log = null, int chatCap = 500, int transcriptCap = 2000)
        {
            return new FileAgentMemoryStore(log, _root, chatCap, transcriptCap);
        }

        [TestCase("load")]
        [TestCase("save")]
        [TestCase("mutate")]
        [TestCase("clear")]
        [TestCase("clear history")]
        [TestCase("append chat")]
        [TestCase("append transcript")]
        public async Task AsyncStoreOperation_ReleasesLocksWithoutPumpingCallerContext(string operationKind)
        {
            FileAgentMemoryStore store = NewStore();
            store.Save(_roleId, new AgentMemoryState { Memory = "seed" });
            HeldSynchronizationContext context = new();
            TaskCompletionSource<bool> blockerEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using ManualResetEventSlim releaseBlocker = new(false);
            Task blocker = Task.Run(() => store.MutateAsync(_roleId, state =>
            {
                blockerEntered.TrySetResult(true);
                if (!releaseBlocker.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("test did not release initial mutation");
                }
                return true;
            }));
            Task operation = null;
            Task synchronousAccess = null;
            bool completedWithoutPump = false;
            try
            {
                Assert.AreSame(blockerEntered.Task, await Task.WhenAny(blockerEntered.Task, Task.Delay(5000)));
                SynchronizationContext original = SynchronizationContext.Current;
                try
                {
                    SynchronizationContext.SetSynchronizationContext(context);
                    operation = operationKind switch
                    {
                        "load" => store.TryLoadAsync(_roleId),
                        "save" => store.SaveAsync(_roleId, new AgentMemoryState { Memory = "saved" }),
                        "mutate" => store.MutateAsync(_roleId, state => true),
                        "clear" => store.ClearAsync(_roleId),
                        "clear history" => store.ClearChatHistoryAsync(_roleId),
                        "append chat" => store.AppendChatMessageAsync(_roleId, "user", "new turn"),
                        "append transcript" => store.AppendTranscriptEntryAsync(_roleId, new ConversationEntry { Content = "trace" }),
                        _ => throw new ArgumentOutOfRangeException(nameof(operationKind))
                    };
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(original);
                }

                releaseBlocker.Set();
                await blocker;
                // Either IO finished without this context, or its continuation is parked in our
                // deliberately unpumped queue. The initial lock holder forces an actual async wait.
                Task ready = Task.WhenAny(operation, context.FirstPost.Task);
                Assert.AreSame(ready, await Task.WhenAny(ready, Task.Delay(5000)));
                synchronousAccess = Task.Run(() =>
                {
                    store.TryLoad(_roleId, out _);
                    store.TryLoadDetailed(_roleId, out _);
                    store.GetChatHistory(_roleId);
                    store.GetTranscriptEntries(_roleId, 0);
                    store.Save(_roleId, new AgentMemoryState());
                    store.AppendChatMessage(_roleId, "user", "sync");
                    store.AppendTranscriptEntry(_roleId, new ConversationEntry { Content = "sync trace" });
                    store.ClearChatHistory(_roleId);
                    store.Clear(_roleId);
                });
                completedWithoutPump = ReferenceEquals(synchronousAccess,
                    await Task.WhenAny(synchronousAccess, Task.Delay(5000)));
            }
            finally
            {
                // Even a broken implementation is released before the assertion, so the test never
                // leaves a worker or the Unity test runner permanently blocked on a semaphore.
                releaseBlocker.Set();
                context.ReleaseCallbacks();
                await blocker;
                if (operation != null) { await operation; }
                if (synchronousAccess != null) { await synchronousAccess; }
            }
            Assert.IsTrue(completedWithoutPump,
                "Synchronous memory APIs must not wait for the async caller's SynchronizationContext to release storage locks.");
        }

        [Test]
        public async Task AsyncHistoryTrim_MarshalsCallbackToCallerAfterReleasingStorageLocks()
        {
            FileAgentMemoryStore store = NewStore(null, 1, 1);
            store.AppendChatMessage(_roleId, "user", "before");
            HeldSynchronizationContext context = new();
            bool onCallerContext = false;
            store.HistoryTrimmed += _ =>
            {
                onCallerContext = ReferenceEquals(context, SynchronizationContext.Current);
                store.GetChatHistory(_roleId); // A host callback can safely re-enter synchronous APIs.
            };
            TaskCompletionSource<bool> entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using ManualResetEventSlim release = new(false);
            Task blocker = Task.Run(() => store.MutateAsync(_roleId, state =>
            {
                entered.TrySetResult(true);
                if (!release.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("test did not release initial mutation");
                }
                return true;
            }));
            Assert.AreSame(entered.Task, await Task.WhenAny(entered.Task, Task.Delay(5000)));
            Task append;
            SynchronizationContext original = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(context);
                append = store.AppendChatMessageAsync(_roleId, "user", "after");
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(original);
                release.Set();
            }
            await blocker;
            Task read = null;
            bool locksReleased = false;
            try
            {
                Assert.AreSame(context.FirstPost.Task, await Task.WhenAny(context.FirstPost.Task, Task.Delay(5000)));
                read = Task.Run(() => store.GetChatHistory(_roleId));
                locksReleased = ReferenceEquals(read, await Task.WhenAny(read, Task.Delay(5000)));
            }
            finally
            {
                context.ReleaseCallbacks();
                await append;
                if (read != null) { await read; }
            }
            Assert.IsTrue(locksReleased, "The callback context may be paused, but storage must already be unlocked.");
            Assert.IsTrue(onCallerContext, "Host callbacks must retain the async caller's context.");
        }

        private sealed class HeldSynchronizationContext : SynchronizationContext
        {
            private readonly object _gate = new();
            private readonly Queue<(SendOrPostCallback Callback, object State)> _pending = new();
            private bool _released;
            public readonly TaskCompletionSource<bool> FirstPost = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public override void Post(SendOrPostCallback callback, object state)
            {
                lock (_gate)
                {
                    FirstPost.TrySetResult(true);
                    if (!_released)
                    {
                        _pending.Enqueue((callback, state));
                        return;
                    }
                }
                Dispatch(callback, state);
            }

            public void ReleaseCallbacks()
            {
                lock (_gate)
                {
                    _released = true;
                    while (_pending.Count > 0)
                    {
                        (SendOrPostCallback callback, object state) = _pending.Dequeue();
                        Dispatch(callback, state);
                    }
                }
            }

            private void Dispatch(SendOrPostCallback callback, object state)
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    SynchronizationContext original = Current;
                    try { SetSynchronizationContext(this); callback(state); }
                    finally { SetSynchronizationContext(original); }
                });
            }
        }

        [Test]
        public async Task PeerStores_ConcurrentAppendsAndRewrite_PreserveEveryCommittedMessage()
        {
            File.WriteAllText(HistoryPath, "broken-line\n");
            FileAgentMemoryStore first = NewStore();
            FileAgentMemoryStore second = NewStore();
            first.GetChatHistory(_roleId);
            second.GetChatHistory(_roleId);
            Task[] writes = Enumerable.Range(0, 32).Select(i =>
                (i % 2 == 0 ? first : second).AppendChatMessageAsync(_roleId, "user", "message-" + i)).ToArray();
            await Task.WhenAll(writes);

            string[] expected = Enumerable.Range(0, 32).Select(i => "message-" + i).ToArray();
            CollectionAssert.AreEquivalent(expected, NewStore().GetChatHistory(_roleId).Select(m => m.Content));
            CollectionAssert.AreEquivalent(expected, first.GetChatHistory(_roleId).Select(m => m.Content));
            CollectionAssert.AreEquivalent(expected, second.GetChatHistory(_roleId).Select(m => m.Content));
        }

        [Test]
        public void PeerStore_AppendAndClear_InvalidateCachedHistoryAndSessionOnlyRows()
        {
            FileAgentMemoryStore first = NewStore();
            FileAgentMemoryStore second = NewStore();
            first.AppendChatMessage(_roleId, "user", "durable");
            second.GetChatHistory(_roleId);
            second.AppendChatMessage(_roleId, "user", "local only", false);
            first.AppendChatMessage(_roleId, "user", "peer update");
            CollectionAssert.AreEqual(new[] { "durable", "local only", "peer update" },
                second.GetChatHistory(_roleId).Select(m => m.Content));

            first.ClearChatHistory(_roleId);
            Assert.IsEmpty(second.GetChatHistory(_roleId));
            second.AppendChatMessage(_roleId, "user", "after clear");
            CollectionAssert.AreEqual(new[] { "after clear" }, NewStore().GetChatHistory(_roleId).Select(m => m.Content));
        }

        [Test]
        public async Task FailedConfirmedAppend_DoesNotChangeCachedHistoryOrLeakIntoLaterRewrite()
        {
            FileAgentMemoryStore store = NewStore(null, 2, 3);
            await store.AppendChatMessageAsync(_roleId, "user", "before");
            await store.AppendChatMessageAsync(_roleId, "user", "second");
            using (FileStream locked = new(HistoryPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                await CaptureExceptionAsync<IOException>(() =>
                    store.AppendChatMessageAsync(_roleId, "user", "failed"));
                CollectionAssert.AreEqual(new[] { "before", "second" }, store.GetChatHistory(_roleId).Select(m => m.Content));
            }
            await store.AppendChatMessageAsync(_roleId, "user", "after");
            CollectionAssert.AreEqual(new[] { "second", "after" }, NewStore(null, 2, 3).GetChatHistory(_roleId).Select(m => m.Content));
            Assert.That(File.ReadAllText(HistoryPath), Does.Not.Contain("failed"));
        }

        [Test]
        public void FailedBestEffortAppend_IsRecoveredWithoutOverwritingPeerCommits()
        {
            FileAgentMemoryStore first = NewStore();
            FileAgentMemoryStore second = NewStore();
            first.AppendChatMessage(_roleId, "user", "before");
            using (FileStream locked = new(HistoryPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                first.AppendChatMessage(_roleId, "user", "retry this");
            }
            second.AppendChatMessage(_roleId, "user", "peer");
            first.AppendChatMessage(_roleId, "user", "after");

            CollectionAssert.AreEqual(new[] { "before", "retry this", "peer", "after" },
                NewStore().GetChatHistory(_roleId).Select(m => m.Content));
        }

        [Test]
        public void ReopenedHistory_ContinuesSavedOrderAndPreservesLegacyPrefix()
        {
            // On-disk input from an earlier process: no allocator in this process has seen its order.
            File.WriteAllText(HistoryPath,
                "{\"k\":0,\"r\":\"user\",\"c\":\"legacy first\",\"m\":true}\n" +
                "{\"k\":0,\"r\":\"user\",\"c\":\"legacy second\",\"m\":true}\n" +
                "{\"k\":0,\"r\":\"user\",\"c\":\"saved\",\"m\":true,\"o\":1000}\n");
            FileAgentMemoryStore first = NewStore();
            FileAgentMemoryStore peer = NewStore();
            first.AppendChatMessage(_roleId, "user", "local", false);
            peer.AppendChatMessage(_roleId, "user", "peer");

            CollectionAssert.AreEqual(new[] { "legacy first", "legacy second", "saved", "local", "peer" },
                first.GetChatHistory(_roleId).Select(m => m.Content));
            Assert.That(File.ReadAllText(HistoryPath), Does.Not.Contain("local"));
        }

        [Test]
        public async Task ClearAfterPartialLegacyMigration_DoesNotResurrectLegacyHistory()
        {
            LegacyPersisted legacy = new()
            {
                memory = "keep memory",
                chatHistoryJson = JsonUtility.ToJson(new LegacyChatWrapper
                {
                    Items = new[] { new ChatMessage("user", "old conversation") }
                })
            };
            File.WriteAllText(MemoryPath, JsonUtility.ToJson(legacy));
            FileAgentMemoryStore store = NewStore();
            // A blocked temporary file allows the history migration to finish but prevents the
            // legacy document cleanup, reproducing the actual two-file failure boundary.
            Directory.CreateDirectory(MemoryPath + ".tmp");
            Assert.Throws<UnauthorizedAccessException>(() => store.GetChatHistory(_roleId));
            Assert.IsTrue(File.Exists(HistoryPath));
            Directory.Delete(MemoryPath + ".tmp");

            await store.ClearChatHistoryAsync(_roleId);

            Assert.IsEmpty(NewStore().GetChatHistory(_roleId));
            Assert.IsTrue(NewStore().TryLoad(_roleId, out AgentMemoryState state));
            Assert.AreEqual("keep memory", state.Memory);
        }

        // ---------------------------------------------------------------------------------------------
        // Defect 1: a corrupted or unread memory document
        // ---------------------------------------------------------------------------------------------

        [Test]
        public void UnreadableMemoryDocument_IsReportedAsFailed_NotAsMissing()
        {
            File.WriteAllText(MemoryPath, "{ definitely not json");
            FileAgentMemoryStore store = NewStore();

            AgentMemoryLoadStatus status = store.TryLoadDetailed(_roleId, out AgentMemoryState state);

            Assert.AreEqual(AgentMemoryLoadStatus.Failed, status,
                "The file is there, but what is inside it is unknown. That is not \"there is no memory\": whoever takes " +
                "the emptiness for truth and saves it wipes the learner's document together with its versions.");
            Assert.IsNull(state);
            Assert.IsFalse(store.TryLoad(_roleId, out _));
        }

        [Test]
        public async Task MutateAsync_UnreadableDocument_AbortsAndLeavesTheFileUntouched()
        {
            const string corrupt = "{ \"memory\": \"the child likes dragons\", \"versionsJson\": \"[{broken";
            File.WriteAllText(MemoryPath, corrupt);
            FileAgentMemoryStore store = NewStore();

            AgentMemoryLoadException thrown = await CaptureExceptionAsync<AgentMemoryLoadException>(() =>
                store.MutateAsync(_roleId, state =>
                {
                    state.Memory = "brand new";
                    return true;
                }));

            Assert.AreEqual(_roleId, thrown.RoleId);
            Assert.AreEqual(corrupt, File.ReadAllText(MemoryPath),
                "`TryLoadCore(roleId) ?? new AgentMemoryState()` used to apply the mutator to an EMPTY document and " +
                "overwrite the memory and all 30 versions with it: a temporary read failure meant irreversible loss.");
        }

        [Test]
        public async Task ScopedDecorator_OverTheRealFileStore_ForwardsRealLoadDiagnostics()
        {
            // WHY: CoreAuditFindingsEditModeTests checks this protection on a FAKE store that has the capability. While
            // the production file store did not implement it, the decorator honestly returned an optimistic NotFound, and
            // the test gave a false sense of safety.
            File.WriteAllText(MemoryPath, "{ definitely not json");
            ScopedAgentMemoryStoreDecorator scoped = new(NewStore(), new DefaultAgentMemoryScopeProvider());

            Assert.AreEqual(AgentMemoryLoadStatus.Failed, scoped.TryLoadDetailed(_roleId, out _));
            await CaptureExceptionAsync<AgentMemoryLoadException>(() =>
                scoped.MutateAsync(_roleId, state => true));
        }

        [Test]
        public void Save_AfterUnreadableDocument_HealsTheRole()
        {
            File.WriteAllText(MemoryPath, "{ definitely not json");
            FileAgentMemoryStore store = NewStore();

            store.Save(_roleId, new AgentMemoryState { Memory = "recovered" });

            Assert.AreEqual(AgentMemoryLoadStatus.Loaded, NewStore().TryLoadDetailed(_roleId, out AgentMemoryState loaded),
                "Save used to read the old file through `FromJson(...) ?? new`, parsing threw, and after ONE corrupted " +
                "file every later write for that role went into the log and saved nothing, forever.");
            Assert.AreEqual("recovered", loaded.Memory);
        }

        [Test]
        public void AppendChatMessage_WithUnreadableMemoryDocument_StillPersistsTheConversation()
        {
            File.WriteAllText(MemoryPath, "{ definitely not json");
            NewStore().AppendChatMessage(_roleId, "user", "still here", true);

            ChatMessage[] reloaded = NewStore().GetChatHistory(_roleId);

            Assert.AreEqual(1, reloaded.Length,
                "The conversation lives in its own file: a corrupted memory document must not stop lessons being written.");
            Assert.AreEqual("still here", reloaded[0].Content);
        }

        [Test]
        public async Task MemoryTool_WriteThatDidNotReachDisk_IsReportedAsFailure()
        {
            // WHY: a directory named like the memory file: File.Exists(path) = false (it is not a file), and the
            // tmp -> path swap fails. A deterministic write failure with no hooks inside the store.
            Directory.CreateDirectory(MemoryPath);
            MemoryTool tool = new(NewStore(), _roleId, QuietSettings());

            string result = await tool.ExecuteAsync("write", "the child likes dragons");

            Assert.That(result, Does.Contain("\"Success\":false"),
                "The \"saved\" answer used to be assembled INSIDE the mutator, before the write, and the store swallowed " +
                "the exception: the model and the child saw that the teacher had remembered, while the disk was empty.");
            Assert.That(result, Does.Not.Contain("DONE"));
        }

        [Test]
        public async Task MemoryTool_Read_UnreadableDocument_DoesNotPretendMemoryIsEmpty()
        {
            File.WriteAllText(MemoryPath, "{ definitely not json");
            MemoryTool tool = new(NewStore(), _roleId, QuietSettings());

            string result = await tool.ExecuteAsync("read");

            Assert.That(result, Does.Contain("\"Success\":false"));
            Assert.That(result, Does.Not.Contain("Memory is empty"),
                "\"Memory is empty\" on an unreadable document is an invitation for the model to overwrite what it never saw.");
        }

        [Test]
        public async Task MemoryTool_Append_Succeeds_OnlyAfterTheDocumentIsOnDisk()
        {
            MemoryTool tool = new(NewStore(), _roleId, QuietSettings());

            string result = await tool.ExecuteAsync("append", "likes dragons");

            Assert.That(result, Does.Contain("\"Success\":true"));
            Assert.IsTrue(NewStore().TryLoad(_roleId, out AgentMemoryState loaded),
                "A \"DONE\" answer must mean the document is already on disk (the IAtomicAgentMemoryStore contract).");
            Assert.AreEqual("likes dragons", loaded.Memory);
        }

        [Test]
        public void TornHistoryLine_IsSkipped_AndRepairedOnTheNextWrite()
        {
            NewStore().AppendChatMessage(_roleId, "user", "first", true);
            File.AppendAllText(HistoryPath, "{\"k\":0,\"r\":\"user\",\"c\":\"torn by an interrupted sy");
            CapturingLog log = new();
            FileAgentMemoryStore store = NewStore(log);

            ChatMessage[] beforeRepair = store.GetChatHistory(_roleId);
            store.AppendChatMessage(_roleId, "assistant", "second", true);

            Assert.AreEqual(1, beforeRepair.Length, "A truncated tail is skipped and the rest is read.");
            Assert.That(string.Join("\n", log.Messages), Does.Contain("unreadable line"));
            string[] lines = File.ReadAllLines(HistoryPath).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
            Assert.AreEqual(2, lines.Length, "The next write rewrites the file without the garbage.");
            Assert.IsFalse(lines.Any(l => l.Contains("torn by")));
            CollectionAssert.AreEqual(new[] { "first", "second" },
                NewStore().GetChatHistory(_roleId).Select(m => m.Content).ToArray());
        }

        // ---------------------------------------------------------------------------------------------
        // Defect 4: truncation is visible and configurable
        // ---------------------------------------------------------------------------------------------

        [Test]
        public void Trim_IsVisible_ThroughEventAndLog()
        {
            CapturingLog log = new();
            FileAgentMemoryStore store = NewStore(log, 2, 3);
            List<AgentHistoryTrimmedEventArgs> events = new();
            store.HistoryTrimmed += events.Add;

            store.AppendChatMessage(_roleId, "user", "turn 1", true);
            store.AppendChatMessage(_roleId, "user", "turn 2", true);
            store.AppendChatMessage(_roleId, "user", "turn 3", true);

            Assert.AreEqual(1, events.Count, "The first message past the ceiling raises an event for the host.");
            Assert.AreEqual(1, events[0].DroppedChatMessages);
            Assert.AreEqual(2, events[0].RetainedChatMessages);
            Assert.AreEqual(2, events[0].MaxChatHistoryMessages);
            Assert.That(string.Join("\n", log.Messages), Does.Contain("reached its cap"),
                "500/2000 used to be cut silently: past the ceiling the start of the conversation disappeared on every " +
                "new message, and nobody ever found out.");
            Assert.AreEqual(2, store.MaxChatHistoryMessages);
            Assert.AreEqual(3, store.MaxTranscriptEntries);
        }

        [Test]
        public void Trim_LogsTheFirstDrop_EvenForSessionOnlyHistory()
        {
            CapturingLog log = new();
            FileAgentMemoryStore store = NewStore(log, 1, 5);

            store.AppendChatMessage(_roleId, "user", "turn 1", false);
            store.AppendChatMessage(_roleId, "user", "turn 2", false);

            Assert.That(string.Join("\n", log.Messages), Does.Contain("reached its cap"));
        }

        // ---------------------------------------------------------------------------------------------
        // Defect 5: the cost is one line per message, with no duplication and no full rewrite
        // ---------------------------------------------------------------------------------------------

        [Test]
        public void AppendChatMessage_AppendsOneLine_AndDoesNotTouchTheMemoryDocument()
        {
            FileAgentMemoryStore store = NewStore();

            for (int i = 0; i < 20; i++)
            {
                store.AppendChatMessage(_roleId, i % 2 == 0 ? "user" : "assistant", $"turn {i}", true);
            }

            Assert.IsFalse(File.Exists(MemoryPath),
                "A chat message must not rewrite the memory document (text, 30 versions, prompt snapshot).");
            string[] lines = File.ReadAllLines(HistoryPath).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
            Assert.AreEqual(20, lines.Length, "One line per message.");
            string text = File.ReadAllText(HistoryPath);
            Assert.AreEqual(1, CountOccurrences(text, "turn 7"),
                "History and transcript used to store the very same text twice, with indentation.");
        }

        [Test]
        public void HistoryFile_IsCompactedOnlyOnceStaleHeadExceedsSlack_NotOnEveryMessage()
        {
            FileAgentMemoryStore store = NewStore(null, 2, 4);
            int slack = store.CompactionSlack;

            // WHY: after four lines every message leaves one "dead" line at the head of the file; compaction happens
            // once slack of them have piled up, that is on message number (4 + slack + 1).
            for (int i = 0; i < 4 + slack; i++)
            {
                store.AppendChatMessage(_roleId, "user", $"turn {i}", true);
            }

            int linesBeforeCompaction = File.ReadAllLines(HistoryPath).Count(l => !string.IsNullOrWhiteSpace(l));
            Assert.Greater(linesBeforeCompaction, 4,
                "The cut lines stay at the head of the file until compaction; readers never see them.");
            Assert.AreEqual(2, store.GetChatHistory(_roleId).Length);
            Assert.AreEqual(4, store.GetTranscriptEntries(_roleId, 0).Count);

            store.AppendChatMessage(_roleId, "user", "the one that compacts", true);

            int linesAfterCompaction = File.ReadAllLines(HistoryPath).Count(l => !string.IsNullOrWhiteSpace(l));
            Assert.AreEqual(4, linesAfterCompaction, "Compaction leaves exactly the transcript window.");
            Assert.AreEqual(4, NewStore(null, 2, 4).GetTranscriptEntries(_roleId, 0).Count);
        }

        [Test]
        public void LegacyV1Document_IsMigrated_WithoutLosingChatTranscriptOrMemory()
        {
            const long ms = 1_700_000_000_123L;
            LegacyPersisted legacy = new()
            {
                memory = "PLAYER_QUEST:rescue_dog",
                lastSystemPrompt = "npc",
                chatHistoryJson = JsonUtility.ToJson(new LegacyChatWrapper
                {
                    Items = new[]
                    {
                        new ChatMessage { Role = "user", Content = "hello", Timestamp = ms },
                        new ChatMessage { Role = "assistant", Content = "hi", Timestamp = ms + 1000 }
                    }
                }),
                transcriptEntriesJson = JsonConvert.SerializeObject(new List<ConversationEntry>
                {
                    new() { Kind = ConversationEntryKind.User, Key = "user", Content = "hello", Timestamp = ms },
                    new() { Kind = ConversationEntryKind.ToolResult, Key = "lookup", Content = "tool row", CallId = "c1" },
                    new() { Kind = ConversationEntryKind.Assistant, Key = "assistant", Content = "hi", Timestamp = ms + 1000 }
                }, Formatting.Indented),
                versionsJson = JsonConvert.SerializeObject(new[]
                {
                    new AgentMemoryVersionSnapshot { Version = 1, Action = "write", ContentAfter = "PLAYER_QUEST:rescue_dog" }
                }),
                maxMemoryVersions = 30
            };
            File.WriteAllText(MemoryPath, JsonUtility.ToJson(legacy, true));

            FileAgentMemoryStore store = NewStore();
            ChatMessage[] chat = store.GetChatHistory(_roleId);
            IReadOnlyList<ConversationEntry> transcript = store.GetTranscriptEntries(_roleId, 0);

            CollectionAssert.AreEqual(new[] { "hello", "hi" }, chat.Select(m => m.Content).ToArray());
            Assert.AreEqual(ms / 1000, chat[0].Timestamp, "Milliseconds from the v1 format are converted to seconds.");
            CollectionAssert.AreEqual(new[] { "hello", "tool row", "hi" }, transcript.Select(e => e.Content).ToArray());
            Assert.AreEqual("c1", transcript[1].CallId);
            Assert.IsTrue(store.TryLoad(_roleId, out AgentMemoryState state));
            Assert.AreEqual("PLAYER_QUEST:rescue_dog", state.Memory);
            Assert.AreEqual(1, state.Versions.Length);

            Assert.IsTrue(File.Exists(HistoryPath), "The conversation is moved out into its own file.");
            LegacyPersisted rewritten = JsonUtility.FromJson<LegacyPersisted>(File.ReadAllText(MemoryPath));
            Assert.IsTrue(string.IsNullOrEmpty(rewritten.chatHistoryJson) && string.IsNullOrEmpty(rewritten.transcriptEntriesJson),
                "The memory document no longer carries the conversation, so the migration does not run twice.");
            FileAgentMemoryStore reloaded = NewStore();
            Assert.AreEqual(2, reloaded.GetChatHistory(_roleId).Length);
            Assert.AreEqual(3, reloaded.GetTranscriptEntries(_roleId, 0).Count);
        }

        [Test]
        public void LegacyV1Document_ChatOnly_WithoutTranscript_BecomesChatLines()
        {
            LegacyPersisted legacy = new()
            {
                memory = "",
                chatHistoryJson = JsonUtility.ToJson(new LegacyChatWrapper
                {
                    Items = new[]
                    {
                        new ChatMessage { Role = "user", Content = "old one", Timestamp = 1_700_000_000L },
                        new ChatMessage { Role = "assistant", Content = "old two", Timestamp = 1_700_000_001L }
                    }
                })
            };
            File.WriteAllText(MemoryPath, JsonUtility.ToJson(legacy));

            FileAgentMemoryStore store = NewStore();

            CollectionAssert.AreEqual(new[] { "old one", "old two" },
                store.GetChatHistory(_roleId).Select(m => m.Content).ToArray());
            CollectionAssert.AreEqual(new[] { "old one", "old two" },
                store.GetTranscriptEntries(_roleId, 0).Select(e => e.Content).ToArray());
        }

        [Test]
        public void Save_OnLegacyV1Document_MigratesTheConversationBeforeRewriting()
        {
            LegacyPersisted legacy = new()
            {
                memory = "old memory",
                chatHistoryJson = JsonUtility.ToJson(new LegacyChatWrapper
                {
                    Items = new[] { new ChatMessage { Role = "user", Content = "must survive", Timestamp = 1 } }
                })
            };
            File.WriteAllText(MemoryPath, JsonUtility.ToJson(legacy));

            NewStore().Save(_roleId, new AgentMemoryState { Memory = "new memory" });

            FileAgentMemoryStore reloaded = NewStore();
            Assert.AreEqual("must survive", reloaded.GetChatHistory(_roleId).Single().Content,
                "Save rewrites the whole document; a v1-format conversation has to move to its own file BEFORE that.");
            Assert.IsTrue(reloaded.TryLoad(_roleId, out AgentMemoryState state));
            Assert.AreEqual("new memory", state.Memory);
        }

        // ---------------------------------------------------------------------------------------------
        // Defect 6: persistToDisk=false means this process only, with no ambiguity
        // ---------------------------------------------------------------------------------------------

        [Test]
        public void SessionOnlyAppend_NeverReachesDisk_EvenThroughLaterPersistedWritesAndCompaction()
        {
            FileAgentMemoryStore store = NewStore(null, 3, 3);
            store.AppendChatMessage(_roleId, "user", "secret", false);
            store.AppendChatMessage(_roleId, "assistant", "public 1", true);
            for (int i = 0; i < store.CompactionSlack + 8; i++)
            {
                store.AppendChatMessage(_roleId, "assistant", $"public {i + 2}", true);
            }

            store.AppendChatMessage(_roleId, "user", "secret 2", false);
            store.AppendChatMessage(_roleId, "assistant", "public last", true);

            Assert.That(File.ReadAllText(HistoryPath), Does.Not.Contain("secret"),
                "\"Without writing to disk\" used to reach the disk on the next append that did write, so it was neither " +
                "honestly deferred nor honestly kept inside the process.");
            ChatMessage[] inProcess = store.GetChatHistory(_roleId);
            Assert.IsTrue(inProcess.Any(m => m.Content == "secret 2"), "Inside this process the message is visible.");
            Assert.IsFalse(NewStore(null, 3, 3).GetChatHistory(_roleId).Any(m => m.Content.StartsWith("secret")),
                "After a restart it is gone, exactly as a role with PersistChatHistory=false promises.");
        }

        [Test]
        public void SessionOnlyAppend_WithoutAnyPersistedWrite_CreatesNoFiles()
        {
            FileAgentMemoryStore store = NewStore();
            store.AppendChatMessage(_roleId, "user", "session only", false);
            store.AppendTranscriptEntry(_roleId, new ConversationEntry { Content = "session only row" }, false);

            Assert.AreEqual(1, store.GetChatHistory(_roleId).Length);
            Assert.AreEqual(0, Directory.GetFiles(_root).Length);
        }

        // ---------------------------------------------------------------------------------------------
        // Defect 8: the unit of the timestamp
        // ---------------------------------------------------------------------------------------------

        [Test]
        public void ChatTimestamps_AreUnixSeconds_LikeTheChatMessageConstructor()
        {
            long before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            FileAgentMemoryStore store = NewStore();
            store.AppendChatMessage(_roleId, "user", "when", true);
            long after = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            ChatMessage message = NewStore().GetChatHistory(_roleId).Single();
            ConversationEntry row = NewStore().GetTranscriptEntries(_roleId, 0).Single();

            Assert.That(message.Timestamp, Is.InRange(before, after),
                "The file store used to write milliseconds while the ChatMessage constructor and InMemoryAgentMemoryStore " +
                "wrote seconds: the unit depended on the implementation.");
            Assert.AreEqual(message.Timestamp, row.Timestamp);
        }

        // ---------------------------------------------------------------------------------------------
        // Defect 3: durability - in the editor the confirmation is instant, so we check the contract of the async paths
        // ---------------------------------------------------------------------------------------------

        [Test]
        public async Task AsyncWrites_ReturnOnlyAfterTheWriteIsDurable_AndFlushConfirms()
        {
            FileAgentMemoryStore store = NewStore();

            await store.AppendChatMessageAsync(_roleId, "user", "durable", true);
            await store.SaveAsync(_roleId, new AgentMemoryState { Memory = "durable memory" });
            bool flushed = await store.FlushAsync();

            Assert.IsTrue(flushed);
            Assert.AreEqual("durable", NewStore().GetChatHistory(_roleId).Single().Content);
            Assert.IsTrue(NewStore().TryLoad(_roleId, out AgentMemoryState state));
            Assert.AreEqual("durable memory", state.Memory);
        }

        [Test]
        public async Task SaveAsync_WriteFailure_Throws_InsteadOfLoggingSuccess()
        {
            Directory.CreateDirectory(MemoryPath);
            FileAgentMemoryStore store = NewStore();

            await CaptureExceptionAsync<IOException>(() =>
                store.SaveAsync(_roleId, new AgentMemoryState { Memory = "lost?" }));
        }

        // ---------------------------------------------------------------------------------------------
        // The Clear / ClearChatHistory contracts with separate files
        // ---------------------------------------------------------------------------------------------

        [Test]
        public void ClearChatHistory_OnDisk_Preserves_MemoryTool_Field()
        {
            FileAgentMemoryStore store = NewStore();
            store.Save(_roleId, new AgentMemoryState
            {
                Memory = "PLAYER_QUEST:rescue_dog",
                LastSystemPrompt = "npc"
            });
            store.AppendChatMessage(_roleId, "user", "Hello merchant", true);
            store.AppendChatMessage(_roleId, "assistant", "Welcome.", true);

            store.ClearChatHistory(_roleId);

            Assert.IsTrue(store.TryLoad(_roleId, out AgentMemoryState mem));
            Assert.That(mem.Memory, Does.Contain("PLAYER_QUEST:rescue_dog"));
            Assert.AreEqual(0, store.GetChatHistory(_roleId).Length, "Chat history should be empty after ClearChatHistory");

            // Second process / new store instance reads the same files from disk
            FileAgentMemoryStore store2 = NewStore();
            Assert.IsTrue(store2.TryLoad(_roleId, out AgentMemoryState mem2));
            Assert.That(mem2.Memory, Does.Contain("PLAYER_QUEST:rescue_dog"));
            Assert.AreEqual(0, store2.GetChatHistory(_roleId).Length);
            Assert.IsFalse(File.Exists(HistoryPath));
        }

        [Test]
        public void ClearChatHistory_SameStoreInstance_GetChatHistory_IsSafe()
        {
            FileAgentMemoryStore store = NewStore();
            store.AppendChatMessage(_roleId, "user", "one", true);
            store.ClearChatHistory(_roleId);

            Assert.AreEqual(0, store.GetChatHistory(_roleId).Length,
                "After ClearChatHistory, same store must reload empty history without throwing");
        }

        [Test]
        public async Task MemoryTool_Clear_OnDisk_Preserves_History_Transcripts_And_Versions()
        {
            FileAgentMemoryStore store = NewStore();
            AgentMemoryState state = new() { Memory = "will_clear", LastSystemPrompt = "s" };
            state.RecordVersion("write", state.Memory);
            store.Save(_roleId, state);
            store.AppendChatMessage(_roleId, "user", "line1", true);
            store.AppendTranscriptEntry(_roleId, new ConversationEntry
            {
                Kind = ConversationEntryKind.ToolResult,
                Key = "lookup",
                Content = "tool transcript"
            }, true);

            MemoryTool tool = new(store, _roleId, QuietSettings());
            string result = await tool.ExecuteAsync("clear");

            Assert.That(result, Does.Contain("\"Success\":true"));
            FileAgentMemoryStore reloaded = NewStore();
            Assert.IsTrue(reloaded.TryLoad(_roleId, out AgentMemoryState loaded));
            Assert.AreEqual("", loaded.Memory);
            Assert.GreaterOrEqual(loaded.Versions.Length, 2);
            Assert.AreEqual("line1", reloaded.GetChatHistory(_roleId).Single().Content);
            Assert.IsTrue(reloaded.GetTranscriptEntries(_roleId, 0).Any(entry => entry.Content == "tool transcript"));
        }

        [Test]
        public void Clear_WithoutPersistedConversation_RemovesTheRoleKey()
        {
            // Regression (PlayMode MemoryTool_ClearsMemory): Clear must remove the key entirely instead of
            // leaving an empty row behind (contract from a883db00, broken by the audit-wave rewrite).
            FileAgentMemoryStore store = NewStore();
            AgentMemoryState state = new() { Memory = "only_memory", LastSystemPrompt = "s" };
            state.RecordVersion("write", state.Memory);
            store.Save(_roleId, state);

            store.Clear(_roleId);

            Assert.IsFalse(NewStore().TryLoad(_roleId, out _),
                "Clear must remove the role key when no persisted conversation exists.");
        }

        [Test]
        public void Clear_AfterChatHistoryCleared_RemovesTheRoleKey()
        {
            FileAgentMemoryStore store = NewStore();
            store.Save(_roleId, new AgentMemoryState { Memory = "only_memory" });
            store.AppendChatMessage(_roleId, "user", "temp", true);
            store.ClearChatHistory(_roleId);

            store.Clear(_roleId);

            Assert.IsFalse(NewStore().TryLoad(_roleId, out _));
        }

        [Test]
        public void Clear_WithPersistedConversation_RemovesTheMemoryDocument_AndKeepsTheConversation()
        {
            // WHY: the document and the conversation used to share one file, so Clear with a conversation present only
            // zeroed the memory fields and "bumped" the prompt version. Now the conversation has its own file: Clear is
            // an honest deletion of the role document, and TryLoadDetailed answers NotFound, not "empty memory".
            FileAgentMemoryStore store = NewStore();
            AgentMemoryState state = new()
            {
                Memory = "clear_directly",
                SystemPromptMemorySnapshot = "clear_directly",
                SystemPromptMemoryVersion = 4
            };
            state.RecordVersion("write", state.Memory);
            store.Save(_roleId, state);
            store.AppendChatMessage(_roleId, "user", "preserved", true);

            store.Clear(_roleId);

            FileAgentMemoryStore reloaded = NewStore();
            Assert.AreEqual(AgentMemoryLoadStatus.NotFound, reloaded.TryLoadDetailed(_roleId, out _));
            Assert.AreEqual("preserved", reloaded.GetChatHistory(_roleId).Single().Content);
        }

        [Test]
        public async Task Clear_UnreadableMemoryDocument_RestoresAWritableRole()
        {
            File.WriteAllText(MemoryPath, "{ definitely not json");
            CapturingLog log = new();
            FileAgentMemoryStore store = NewStore(log);

            store.Clear(_roleId);

            Assert.AreEqual(AgentMemoryLoadStatus.NotFound, store.TryLoadDetailed(_roleId, out _),
                "After an explicit clear the role is written again: Failed becomes NotFound, not \"empty memory\".");
            Assert.That(string.Join("\n", log.Messages), Does.Contain("unreadable memory document"));
            await store.MutateAsync(_roleId, s =>
            {
                s.Memory = "after clear";
                return true;
            });
            Assert.IsTrue(NewStore().TryLoad(_roleId, out AgentMemoryState loaded));
            Assert.AreEqual("after clear", loaded.Memory);
        }

        [Test]
        public void ClearChatHistory_WhenHistoryFileDeleteFails_KeepsHistoryReadable()
        {
            if (Path.DirectorySeparatorChar != '\\')
            {
                Assert.Ignore("Deleting a file opened with FileShare.None fails only on Windows.");
            }

            CapturingLog log = new();
            FileAgentMemoryStore store = NewStore(log);
            store.AppendChatMessage(_roleId, "user", "keep-me", true);
            Assert.IsTrue(File.Exists(HistoryPath));

            using (File.Open(HistoryPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                // WHY: the void contract does not throw, the failure goes into the log. But a reset that did not happen
                // must stay un-happened in memory as well, otherwise reads return emptiness while the undeleted file
                // resurrects the history on the next load.
                store.ClearChatHistory(_roleId);
                Assert.AreEqual(1, store.GetChatHistory(_roleId).Length,
                    "The file delete failed, so the reset did not happen; reads must still see the turns.");
                Assert.AreEqual("keep-me", store.GetChatHistory(_roleId)[0].Content);
            }

            Assert.That(string.Join("\n", log.Messages), Does.Contain("clear chat history"));

            store.ClearChatHistory(_roleId);
            Assert.IsEmpty(store.GetChatHistory(_roleId));
            Assert.IsFalse(File.Exists(HistoryPath));
        }

        [Test]
        public async Task AppendChatMessage_EmptyOrNullRoleId_WritesNoFiles()
        {
            FileAgentMemoryStore store = NewStore();
            store.AppendChatMessage("", "user", "orphan", true);
            store.AppendChatMessage(null, "user", "orphan", true);
            await store.AppendChatMessageAsync("   ", "user", "orphan", true);

            Assert.AreEqual(0, Directory.GetFiles(_root).Length,
                "An empty role id must not create a shared junk stem; like AppendTranscriptEntry, the call is a no-op.");
            Assert.IsEmpty(store.GetChatHistory(""));
        }

        // ---------------------------------------------------------------------------------------------
        // Ceilings, round-trips, concurrency
        // ---------------------------------------------------------------------------------------------

        [Test]
        public void DefaultWriteBackstop_RetainsHistoryBeyondRoleWindowDefaults()
        {
            FileAgentMemoryStore store = NewStore();
            for (int i = 0; i < 130; i++)
            {
                store.AppendChatMessage(_roleId, "user", $"chat_{i}", true);
            }

            FileAgentMemoryStore reloaded = NewStore();
            Assert.AreEqual(130, reloaded.GetChatHistory(_roleId).Length);
            Assert.GreaterOrEqual(reloaded.GetTranscriptEntries(_roleId, 0).Count, 130);
        }

        [Test]
        public void AppendWrites_TrimHistoryAndTranscripts_ToConfiguredCaps()
        {
            FileAgentMemoryStore store = NewStore(null, 2, 3);
            for (int i = 0; i < 5; i++)
            {
                store.AppendChatMessage(_roleId, "user", $"chat_{i}", true);
            }

            for (int i = 0; i < 5; i++)
            {
                store.AppendTranscriptEntry(_roleId, new ConversationEntry
                {
                    Kind = ConversationEntryKind.ToolResult,
                    Key = "tool",
                    Content = $"transcript_{i}"
                }, true);
            }

            FileAgentMemoryStore reloaded = NewStore(null, 2, 3);
            CollectionAssert.AreEqual(new[] { "chat_3", "chat_4" },
                reloaded.GetChatHistory(_roleId).Select(message => message.Content).ToArray());
            CollectionAssert.AreEqual(new[] { "transcript_2", "transcript_3", "transcript_4" },
                reloaded.GetTranscriptEntries(_roleId, 0).Select(entry => entry.Content).ToArray());
        }

        [Test]
        public void TranscriptCap_DoesNotEvictTheChatWindow()
        {
            // WHY: chat and transcript used to be two lists with independent ceilings; with one write per message the
            // chat window must survive a flood of tool lines exactly as it did before.
            FileAgentMemoryStore store = NewStore(null, 2, 3);
            store.AppendChatMessage(_roleId, "user", "chat_a", true);
            store.AppendChatMessage(_roleId, "assistant", "chat_b", true);
            for (int i = 0; i < 6; i++)
            {
                store.AppendTranscriptEntry(_roleId, new ConversationEntry { Key = "tool", Content = $"tool_{i}" }, true);
            }

            CollectionAssert.AreEqual(new[] { "chat_a", "chat_b" },
                NewStore(null, 2, 3).GetChatHistory(_roleId).Select(m => m.Content).ToArray());
            CollectionAssert.AreEqual(new[] { "tool_3", "tool_4", "tool_5" },
                NewStore(null, 2, 3).GetTranscriptEntries(_roleId, 0).Select(e => e.Content).ToArray());
        }

        [Test]
        public async Task SaveAsync_Then_TryLoadAsync_RoundTrips_WrittenData()
        {
            FileAgentMemoryStore store = NewStore();
            await store.SaveAsync(_roleId, new AgentMemoryState
            {
                Memory = "ASYNC_FACT:dragon_slain",
                LastSystemPrompt = "npc"
            });

            AgentMemoryState loaded = await store.TryLoadAsync(_roleId);
            Assert.IsNotNull(loaded, "TryLoadAsync should return the state written by SaveAsync");
            Assert.AreEqual("ASYNC_FACT:dragon_slain", loaded.Memory);
            Assert.AreEqual("npc", loaded.LastSystemPrompt);

            Assert.IsTrue(store.TryLoad(_roleId, out AgentMemoryState syncLoaded));
            Assert.AreEqual("ASYNC_FACT:dragon_slain", syncLoaded.Memory);
        }

        [Test]
        public void Save_Then_Load_RoundTrips_MemoryVersions_And_Snapshot()
        {
            FileAgentMemoryStore store = NewStore();

            AgentMemoryState state = new() { LastSystemPrompt = "npc", Memory = "v2-content" };
            state.RecordVersion("write", "v1-content");
            state.RecordVersion("write", "v2-content");
            state.SystemPromptMemorySnapshot = "v1-content";
            state.SystemPromptMemoryVersion = 1;
            store.Save(_roleId, state);

            FileAgentMemoryStore reloaded = NewStore();
            Assert.IsTrue(reloaded.TryLoad(_roleId, out AgentMemoryState loaded));

            Assert.IsNotNull(loaded.Versions, "Memory versions must survive a disk round-trip (rollback feature).");
            Assert.AreEqual(2, loaded.Versions.Length, "Both recorded versions must persist.");
            Assert.AreEqual("v1-content", loaded.Versions[0].ContentAfter);
            Assert.AreEqual("v2-content", loaded.Versions[1].ContentAfter);
            Assert.AreEqual("v1-content", loaded.SystemPromptMemorySnapshot,
                "System-prompt memory snapshot must persist (tail-update prompt-cache optimization).");
            Assert.AreEqual(1, loaded.SystemPromptMemoryVersion);
        }

        [Test]
        public void SaveAsync_ConcurrentWrites_FinalFileIsValidJsonOfOneWrite()
        {
            FileAgentMemoryStore store = NewStore();
            const int writers = 24;
            string[] values = Enumerable.Range(0, writers).Select(i => $"memory_payload_{i}").ToArray();

            Task[] tasks = values
                .Select(v => Task.Run(() => store.SaveAsync(_roleId,
                    new AgentMemoryState { Memory = v, LastSystemPrompt = "p_" + v })))
                .ToArray();
            Task.WaitAll(tasks);

            Assert.IsTrue(File.Exists(MemoryPath), "Memory file should exist after concurrent writes");
            Assert.IsFalse(File.Exists(MemoryPath + ".tmp"), "No leftover tmp file after atomic writes");

            PersistedProbe probe = JsonUtility.FromJson<PersistedProbe>(File.ReadAllText(MemoryPath));
            Assert.IsNotNull(probe, "Final file must be valid JSON");
            Assert.That(values, Does.Contain(probe.memory),
                "Final memory must be exactly one of the concurrently written values");
            Assert.AreEqual("p_" + probe.memory, probe.lastSystemPrompt,
                "Memory and prompt must come from the same (non-torn) write");
        }

        [Test]
        public void AppendChatMessageAsync_ConcurrentAppends_AllMessagesSurvive()
        {
            FileAgentMemoryStore store = NewStore();
            const int writers = 16;

            Task[] tasks = Enumerable.Range(0, writers)
                .Select(i => Task.Run(() => store.AppendChatMessageAsync(_roleId, "user", $"msg_{i}", true)))
                .ToArray();
            Task.WaitAll(tasks);

            ChatMessage[] history = NewStore().GetChatHistory(_roleId);
            Assert.AreEqual(writers, history.Length, "All concurrently appended chat messages must be persisted");
            for (int i = 0; i < writers; i++)
            {
                string expected = $"msg_{i}";
                Assert.IsTrue(history.Any(m => m.Content == expected), $"Missing {expected}");
            }
        }

        // ---------------------------------------------------------------------------------------------
        // helpers
        // ---------------------------------------------------------------------------------------------

        private static ICoreAISettings QuietSettings()
        {
            return new CoreAISettingsOptions
            {
                LogToolCalls = false,
                LogToolCallArguments = false,
                LogToolCallResults = false
            };
        }

        private static async Task<TException> CaptureExceptionAsync<TException>(Func<Task> action)
            where TException : Exception
        {
            // WHY: Assert.ThrowsAsync blocks the Unity thread while a captured-context continuation
            // needs that same thread. Awaiting keeps the Unity Task test runner pumping normally.
            try { await action(); }
            catch (TException exception) { return exception; }
            Assert.Fail("Expected exception was not thrown.");
            return null;
        }

        private static int CountOccurrences(string text, string needle)
        {
            int count = 0;
            int index = 0;
            while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }

            return count;
        }

        [Serializable]
        private sealed class PersistedProbe
        {
            public string lastSystemPrompt;
            public string memory;
        }

        /// <summary>Format v1: the conversation inside the memory document. The field names are the contract of the old file.</summary>
        [Serializable]
        private sealed class LegacyPersisted
        {
            public string lastSystemPrompt;
            public string memory;
            public string chatHistoryJson;
            public string transcriptEntriesJson;
            public string versionsJson;
            public string systemPromptMemorySnapshot;
            public int systemPromptMemoryVersion;
            public int maxMemoryVersions;
        }

        [Serializable]
        private struct LegacyChatWrapper
        {
            public ChatMessage[] Items;
        }

        private sealed class CapturingLog : ILog
        {
            public readonly List<string> Messages = new();

            public void Debug(string message, string tag = null)
            {
                Messages.Add(message ?? "");
            }

            public void Info(string message, string tag = null)
            {
                Messages.Add(message ?? "");
            }

            public void Warn(string message, string tag = null)
            {
                Messages.Add(message ?? "");
            }

            public void Error(string message, string tag = null)
            {
                Messages.Add(message ?? "");
            }
        }
    }
}
