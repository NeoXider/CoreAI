using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Infrastructure.AiMemory;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Disk layout: <see cref="FileAgentMemoryStore"/> keeps MemoryTool text in <c>memory</c> and chat in
    /// <c>chatHistoryJson</c>. Chat-history clear preserves long-term memory; memory clear removes the
    /// role file to match <see cref="IAgentMemoryStore.Clear"/>.
    /// </summary>
    public sealed class FileAgentMemoryStoreEditModeTests
    {
        private string _roleId;
        private string _filePath;

        [SetUp]
        public void SetUp()
        {
            _roleId = "EditMode_FileStore_" + Guid.NewGuid().ToString("N");
            string dir = Path.Combine(Application.persistentDataPath, "CoreAI", "AgentMemory");
            string safeName = string.Join("_", _roleId.Split(Path.GetInvalidFileNameChars()));
            _filePath = Path.Combine(dir, $"{safeName}.json");
            if (File.Exists(_filePath))
            {
                File.Delete(_filePath);
            }
        }

        [TearDown]
        public void TearDown()
        {
            if (!string.IsNullOrEmpty(_filePath) && File.Exists(_filePath))
            {
                File.Delete(_filePath);
            }
        }

        [Test]
        public void ClearChatHistory_OnDisk_Preserves_MemoryTool_Field()
        {
            FileAgentMemoryStore store = new();
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

            ChatMessage[] history = store.GetChatHistory(_roleId);
            Assert.AreEqual(0, history.Length, "Chat history should be empty after ClearChatHistory");

            // Second process / new store instance reads same JSON from disk
            FileAgentMemoryStore store2 = new();
            Assert.IsTrue(store2.TryLoad(_roleId, out AgentMemoryState mem2));
            Assert.That(mem2.Memory, Does.Contain("PLAYER_QUEST:rescue_dog"));
            Assert.AreEqual(0, store2.GetChatHistory(_roleId).Length);
        }

        [Test]
        public void ClearChatHistory_SameStoreInstance_GetChatHistory_IsSafe()
        {
            FileAgentMemoryStore store = new();
            store.AppendChatMessage(_roleId, "user", "one", true);
            store.ClearChatHistory(_roleId);

            ChatMessage[] history = store.GetChatHistory(_roleId);
            Assert.AreEqual(0, history.Length,
                "After ClearChatHistory, same store must reload empty history without throwing");
        }

        [Test]
        public void Clear_MemoryTool_OnDisk_Removes_Role_Row()
        {
            FileAgentMemoryStore store = new();
            store.Save(_roleId, new AgentMemoryState { Memory = "will_clear", LastSystemPrompt = "s" });
            store.AppendChatMessage(_roleId, "user", "line1", true);

            store.Clear(_roleId);

            // New store instance forces reload from disk (same process would keep ephemeral cache).
            FileAgentMemoryStore store2 = new();
            Assert.IsFalse(store2.TryLoad(_roleId, out _), "Clear() should delete the role memory row.");
            Assert.AreEqual(0, store2.GetChatHistory(_roleId).Length,
                "Clear() removes all persisted state for the role.");
            Assert.IsFalse(File.Exists(_filePath), "Clear() should remove the role file.");
        }

        [Serializable]
        private sealed class PersistedProbe
        {
            public string lastSystemPrompt;
            public string memory;
        }

        private static string CreateTempRoot()
        {
            string root = Path.Combine(Path.GetTempPath(), "CoreAITestAgentMem_" + Path.GetRandomFileName());
            Directory.CreateDirectory(root);
            return root;
        }

        private static void DeleteTempRoot(string root)
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch
            {
                /* best effort */
            }
        }

        [Test]
        public void SaveAsync_Then_TryLoadAsync_RoundTrips_WrittenData()
        {
            string root = CreateTempRoot();
            try
            {
                FileAgentMemoryStore store = new(null, root);
                store.SaveAsync(_roleId, new AgentMemoryState
                {
                    Memory = "ASYNC_FACT:dragon_slain",
                    LastSystemPrompt = "npc"
                }).GetAwaiter().GetResult();

                AgentMemoryState loaded = store.TryLoadAsync(_roleId).GetAwaiter().GetResult();
                Assert.IsNotNull(loaded, "TryLoadAsync should return the state written by SaveAsync");
                Assert.AreEqual("ASYNC_FACT:dragon_slain", loaded.Memory);
                Assert.AreEqual("npc", loaded.LastSystemPrompt);

                // Sync path reads what the async path wrote.
                Assert.IsTrue(store.TryLoad(_roleId, out AgentMemoryState syncLoaded));
                Assert.AreEqual("ASYNC_FACT:dragon_slain", syncLoaded.Memory);
            }
            finally
            {
                DeleteTempRoot(root);
            }
        }

        [Test]
        public void SaveAsync_ConcurrentWrites_FinalFileIsValidJsonOfOneWrite()
        {
            string root = CreateTempRoot();
            try
            {
                FileAgentMemoryStore store = new(null, root);
                const int writers = 24;
                string[] values = Enumerable.Range(0, writers).Select(i => $"memory_payload_{i}").ToArray();

                Task[] tasks = values
                    .Select(v => Task.Run(() => store.SaveAsync(_roleId,
                        new AgentMemoryState { Memory = v, LastSystemPrompt = "p_" + v })))
                    .ToArray();
                Task.WaitAll(tasks);

                string safeName = string.Join("_", _roleId.Split(Path.GetInvalidFileNameChars()));
                string path = Path.Combine(root, $"{safeName}.json");
                Assert.IsTrue(File.Exists(path), "Memory file should exist after concurrent writes");
                Assert.IsFalse(File.Exists(path + ".tmp"), "No leftover tmp file after atomic writes");

                string json = File.ReadAllText(path);
                PersistedProbe probe = JsonUtility.FromJson<PersistedProbe>(json);
                Assert.IsNotNull(probe, "Final file must be valid JSON");
                Assert.That(values, Does.Contain(probe.memory),
                    "Final memory must be exactly one of the concurrently written values");
                Assert.AreEqual("p_" + probe.memory, probe.lastSystemPrompt,
                    "Memory and prompt must come from the same (non-torn) write");
            }
            finally
            {
                DeleteTempRoot(root);
            }
        }

        [Test]
        public void AppendChatMessageAsync_ConcurrentAppends_AllMessagesSurvive()
        {
            string root = CreateTempRoot();
            try
            {
                FileAgentMemoryStore store = new(null, root);
                const int writers = 16;

                Task[] tasks = Enumerable.Range(0, writers)
                    .Select(i => Task.Run(() => store.AppendChatMessageAsync(_roleId, "user", $"msg_{i}", true)))
                    .ToArray();
                Task.WaitAll(tasks);

                // Fresh store instance forces a reload from disk.
                FileAgentMemoryStore store2 = new(null, root);
                ChatMessage[] history = store2.GetChatHistory(_roleId);
                Assert.AreEqual(writers, history.Length,
                    "All concurrently appended chat messages must be persisted");
                for (int i = 0; i < writers; i++)
                {
                    string expected = $"msg_{i}";
                    Assert.IsTrue(history.Any(m => m.Content == expected), $"Missing {expected}");
                }
            }
            finally
            {
                DeleteTempRoot(root);
            }
        }
    }
}
