using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.AiMemory;
using Newtonsoft.Json;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    public sealed class MemoryFileStoreRegressionTests
    {
        private string _root;
        private SynchronizationContext _previous;
        [SetUp] public void SetUp()
        {
            _previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            _root = Path.Combine(Path.GetTempPath(), "coreai-memory-contract-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }
        [TearDown] public void TearDown()
        {
            SynchronizationContext.SetSynchronizationContext(_previous);
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        [TestCase("versionsJson")]
        [TestCase("chatHistoryJson")]
        [TestCase("transcriptEntriesJson")]
        public async Task CorruptEmbeddedJson_ReadAndMutation_PreserveOriginalBytes(string field)
        {
            string path = Path.Combine(_root, AgentMemoryFileKey.FileStem("role") + ".json");
            string encoded = JsonConvert.SerializeObject(new System.Collections.Generic.Dictionary<string, object>
            { ["memory"] = "recoverable fact", [field] = "{unfinished" });
            File.WriteAllText(path, encoded);
            using FileAgentMemoryStore store = new(null, _root);
            Assert.AreEqual(AgentMemoryLoadStatus.Failed, store.TryLoadDetailed("role", out _));
            Exception fault = await Capture(store.MutateAsync("role", state => { state.Memory = "replacement"; return true; }));
            Assert.IsNotNull(fault);
            Assert.AreEqual(encoded, File.ReadAllText(path));
            Assert.IsFalse(Directory.EnumerateFiles(_root, "*.history.jsonl").Any());
        }

        [TestCase("Math", "math")]
        [TestCase("a/b", "a_b_3a8e75c1")]
        public async Task LogicalRoles_WithLegacyAliases_RemainIndependent(string firstRole, string secondRole)
        {
            if (firstRole == "a/b") secondRole = AgentMemoryFileKey.LegacyFileStem(firstRole);
            using FileAgentMemoryStore first = new(null, _root);
            using FileAgentMemoryStore peer = new(null, _root);
            await first.SaveAsync(firstRole, new AgentMemoryState { Memory = "first fact" });
            await peer.SaveAsync(secondRole, new AgentMemoryState { Memory = "second fact" });
            await first.AppendChatMessageAsync(firstRole, "user", "first history");
            await peer.AppendChatMessageAsync(secondRole, "user", "second history");
            Assert.IsTrue(peer.TryLoad(firstRole, out AgentMemoryState a));
            Assert.IsTrue(first.TryLoad(secondRole, out AgentMemoryState b));
            Assert.AreEqual("first fact", a.Memory);
            Assert.AreEqual("second fact", b.Memory);
            Assert.AreEqual("first history", peer.GetChatHistory(firstRole).Single().Content);
            Assert.AreEqual("second history", first.GetChatHistory(secondRole).Single().Content);
            await first.ClearChatHistoryAsync(firstRole);
            Assert.IsEmpty(first.GetChatHistory(firstRole));
            Assert.AreEqual("second history", peer.GetChatHistory(secondRole).Single().Content);
        }

        [Test]
        public void LegacyMigration_UnambiguousIdentity_PreservesSourceBackup()
        {
            string legacy = Path.Combine(_root, "teacher.json");
            string bytes = "{\"memory\":\"legacy fact\"}";
            File.WriteAllText(legacy, bytes);
            using FileAgentMemoryStore store = new(null, _root);
            Assert.IsTrue(store.TryLoad("teacher", out AgentMemoryState state));
            Assert.AreEqual("legacy fact", state.Memory);
            Assert.AreEqual(bytes, File.ReadAllText(legacy + ".legacy"));
            Assert.IsTrue(File.Exists(Path.Combine(_root, AgentMemoryFileKey.FileStem("teacher") + ".json")));
        }

        [Test]
        public async Task LegacyMigration_AmbiguousSanitizedIdentity_RejectsWithoutWriting()
        {
            const string role = "a/b";
            string legacy = Path.Combine(_root, AgentMemoryFileKey.LegacyFileStem(role) + ".json");
            string bytes = "{\"memory\":\"owner unknown\"}";
            File.WriteAllText(legacy, bytes);
            using FileAgentMemoryStore store = new(null, _root);
            Assert.IsNotNull(await Capture(store.SaveAsync(role, new AgentMemoryState { Memory = "replacement" })));
            Assert.AreEqual(bytes, File.ReadAllText(legacy));
            Assert.AreEqual(1, Directory.EnumerateFiles(_root).Count());
        }

        [Test]
        public async Task LegacyMigration_RoleResemblingCanonicalStem_CannotAdoptAnotherHistory()
        {
            using FileAgentMemoryStore store = new(null, _root);
            await store.AppendChatMessageAsync("owner", "user", "private fact");
            string alias = AgentMemoryFileKey.FileStem("owner");
            Assert.Throws<InvalidDataException>(() => store.GetChatHistory(alias));
            Assert.AreEqual("private fact", store.GetChatHistory("owner").Single().Content);
            Assert.AreEqual(1, Directory.EnumerateFiles(_root).Count());
        }

        [Test]
        public async Task ResidentCache_ScopeChurn_IsBoundedAndDisposalReleasesPathLeases()
        {
            int before = FileAgentMemoryStore.SharedPathStateCount;
            FileAgentMemoryStore store = new(null, _root, maxResidentHistories: 2);
            for (int index = 0; index < 24; index++)
            {
                await store.AppendChatMessageAsync("role-" + index, "user", "fact-" + index);
                Assert.LessOrEqual(store.ResidentHistoryCount, 2);
            }
            Assert.AreEqual("fact-0", store.GetChatHistory("role-0").Single().Content);
            store.Dispose();
            Assert.AreEqual(0, store.ResidentHistoryCount);
            Assert.LessOrEqual(FileAgentMemoryStore.SharedPathStateCount, before);
        }

        [Test]
        public void ResidentCache_TransientPartitions_AreRefusedInsteadOfSilentlyEvicted()
        {
            using FileAgentMemoryStore store = new(null, _root, maxResidentHistories: 1);
            store.AppendChatMessage("first", "user", "transient fact", false);
            Assert.Throws<InvalidOperationException>(() => store.GetChatHistory("second"));
            Assert.AreEqual("transient fact", store.GetChatHistory("first").Single().Content);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task DisposedReader_CannotStrandHealthyPeerGate(bool transcript)
        {
            FileAgentMemoryStore old = new(null, _root);
            old.Dispose();
            if (transcript) Assert.Throws<ObjectDisposedException>(() => old.GetTranscriptEntries("role", 10));
            else Assert.Throws<ObjectDisposedException>(() => old.GetChatHistory("role"));
            using FileAgentMemoryStore peer = new(null, _root);
            Task mutation = peer.AppendChatMessageAsync("role", "user", "healthy");
            Assert.AreSame(mutation, await Task.WhenAny(mutation, Task.Delay(5000)));
            await mutation;
            Assert.AreEqual("healthy", peer.GetChatHistory("role").Single().Content);
        }

        [Test]
        public async Task Disposal_WithAdmittedMutatorAndPeerWaiter_DoesNotCreateTwoGates()
        {
            FileAgentMemoryStore old = new(null, _root);
            using FileAgentMemoryStore peer = new(null, _root);
            using ManualResetEventSlim release = new(false);
            TaskCompletionSource<bool> entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task first = Task.Run(() => old.MutateAsync("role", state =>
            {
                entered.TrySetResult(true);
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Mutator was not released.");
                state.Memory = "first";
                return true;
            }));
            Assert.AreSame(entered.Task, await Task.WhenAny(entered.Task, Task.Delay(5000)));
            Task second = peer.MutateAsync("role", state => { state.Memory += " second"; return true; });
            Assert.IsFalse(second.IsCompleted);
            old.Dispose();
            release.Set();
            Task joined = Task.WhenAll(first, second);
            Assert.AreSame(joined, await Task.WhenAny(joined, Task.Delay(5000)));
            await joined;
            Assert.IsTrue(peer.TryLoad("role", out AgentMemoryState result));
            Assert.AreEqual("first second", result.Memory);
        }

        private static async Task<Exception> Capture(Task task) { try { await task; return null; } catch (Exception ex) { return ex; } }
    }
}
