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
    /// <c>chatHistoryJson</c>. Chat-history clear preserves long-term memory; memory clear preserves the
    /// role's history and transcripts.
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
        public async Task MemoryTool_Clear_OnDisk_Preserves_History_Transcripts_And_Versions()
        {
            string root = CreateTempRoot();
            try
            {
                FileAgentMemoryStore store = new(null, root);
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

                MemoryTool tool = new(store, _roleId);
                string result = await tool.ExecuteAsync("clear");

                Assert.That(result, Does.Contain("\"Success\":true"));
                FileAgentMemoryStore reloaded = new(null, root);
                Assert.IsTrue(reloaded.TryLoad(_roleId, out AgentMemoryState loaded));
                Assert.AreEqual("", loaded.Memory);
                Assert.GreaterOrEqual(loaded.Versions.Length, 2);
                Assert.AreEqual("line1", reloaded.GetChatHistory(_roleId).Single().Content);
                Assert.IsTrue(reloaded.GetTranscriptEntries(_roleId, 0)
                    .Any(entry => entry.Content == "tool transcript"));
            }
            finally
            {
                DeleteTempRoot(root);
            }
        }

        [Test]
        public void Clear_WithoutPersistedConversation_RemovesTheRoleKey()
        {
            // Regression (PlayMode MemoryTool_ClearsMemory): when the role file holds ONLY memory —
            // no persisted chat history or transcripts — Clear must remove the key entirely instead of
            // leaving an empty row behind (contract from a883db00, broken by the audit-wave rewrite).
            string root = CreateTempRoot();
            try
            {
                FileAgentMemoryStore store = new(null, root);
                AgentMemoryState state = new() { Memory = "only_memory", LastSystemPrompt = "s" };
                state.RecordVersion("write", state.Memory);
                store.Save(_roleId, state);

                store.Clear(_roleId);

                FileAgentMemoryStore reloaded = new(null, root);
                Assert.IsFalse(reloaded.TryLoad(_roleId, out _),
                    "Clear must remove the role key when no persisted conversation shares the file.");
            }
            finally
            {
                DeleteTempRoot(root);
            }
        }

        [Test]
        public void Clear_AfterChatHistoryCleared_RemovesTheRoleKey()
        {
            // Once ClearChatHistory left only cleared conversation markers in the file, a memory clear
            // has nothing left to preserve — the role key must go away like in the memory-only case.
            string root = CreateTempRoot();
            try
            {
                FileAgentMemoryStore store = new(null, root);
                store.Save(_roleId, new AgentMemoryState { Memory = "only_memory" });
                store.AppendChatMessage(_roleId, "user", "temp", true);
                store.ClearChatHistory(_roleId);

                store.Clear(_roleId);

                FileAgentMemoryStore reloaded = new(null, root);
                Assert.IsFalse(reloaded.TryLoad(_roleId, out _),
                    "An empty serialized history wrapper must not keep the cleared role row alive.");
            }
            finally
            {
                DeleteTempRoot(root);
            }
        }

        [Test]
        public void Clear_OnDisk_WipesMemoryVersionsAndSnapshot_BumpsPromptVersion()
        {
            string root = CreateTempRoot();
            try
            {
                FileAgentMemoryStore store = new(null, root);
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

                FileAgentMemoryStore reloaded = new(null, root);
                Assert.IsTrue(reloaded.TryLoad(_roleId, out AgentMemoryState loaded));
                Assert.AreEqual("", loaded.Memory);
                Assert.IsNull(loaded.Versions);
                Assert.AreEqual("", loaded.SystemPromptMemorySnapshot);
                Assert.AreEqual(5, loaded.SystemPromptMemoryVersion);
                Assert.AreEqual("preserved", reloaded.GetChatHistory(_roleId).Single().Content);
            }
            finally
            {
                DeleteTempRoot(root);
            }
        }

        [Test]
        public void Clear_CorruptFile_RewritesClearedState()
        {
            string root = CreateTempRoot();
            try
            {
                string path = Path.Combine(root, _roleId + ".json");
                File.WriteAllText(path, "{ definitely not json");

                FileAgentMemoryStore store = new(null, root);
                store.Clear(_roleId);

                Assert.IsTrue(store.TryLoad(_roleId, out AgentMemoryState loaded));
                Assert.AreEqual("", loaded.Memory);
                Assert.AreEqual("", loaded.SystemPromptMemorySnapshot);
                Assert.AreEqual(1, loaded.SystemPromptMemoryVersion);
            }
            finally
            {
                DeleteTempRoot(root);
            }
        }

        [Test]
        public void DefaultWriteBackstop_RetainsHistoryBeyondRoleWindowDefaults()
        {
            string root = CreateTempRoot();
            try
            {
                FileAgentMemoryStore store = new(null, root);
                for (int i = 0; i < 130; i++)
                {
                    store.AppendChatMessage(_roleId, "user", $"chat_{i}", true);
                }

                FileAgentMemoryStore reloaded = new(null, root);
                Assert.AreEqual(130, reloaded.GetChatHistory(_roleId).Length);
                Assert.GreaterOrEqual(reloaded.GetTranscriptEntries(_roleId, 0).Count, 130);
            }
            finally
            {
                DeleteTempRoot(root);
            }
        }

        [Test]
        public void AppendWrites_TrimHistoryAndTranscripts_ToConfiguredCaps()
        {
            string root = CreateTempRoot();
            try
            {
                FileAgentMemoryStore store = new(null, root, 2, 3);
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

                FileAgentMemoryStore reloaded = new(null, root, 2, 3);
                CollectionAssert.AreEqual(new[] { "chat_3", "chat_4" },
                    reloaded.GetChatHistory(_roleId).Select(message => message.Content).ToArray());
                CollectionAssert.AreEqual(new[] { "transcript_2", "transcript_3", "transcript_4" },
                    reloaded.GetTranscriptEntries(_roleId, 0).Select(entry => entry.Content).ToArray());
            }
            finally
            {
                DeleteTempRoot(root);
            }
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
        public async Task SaveAsync_Then_TryLoadAsync_RoundTrips_WrittenData()
        {
            string root = CreateTempRoot();
            try
            {
                FileAgentMemoryStore store = new(null, root);
                await store.SaveAsync(_roleId, new AgentMemoryState
                {
                    Memory = "ASYNC_FACT:dragon_slain",
                    LastSystemPrompt = "npc"
                });

                AgentMemoryState loaded = await store.TryLoadAsync(_roleId);
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
        public void Save_Then_Load_RoundTrips_MemoryVersions_And_Snapshot()
        {
            string root = CreateTempRoot();
            try
            {
                FileAgentMemoryStore store = new(null, root);

                AgentMemoryState state = new() { LastSystemPrompt = "npc", Memory = "v2-content" };
                state.RecordVersion("write", "v1-content");
                state.RecordVersion("write", "v2-content");
                state.SystemPromptMemorySnapshot = "v1-content";
                state.SystemPromptMemoryVersion = 1;
                store.Save(_roleId, state);

                // Fresh instance forces a reload from disk (the orchestrator reloads every request).
                FileAgentMemoryStore reloaded = new(null, root);
                Assert.IsTrue(reloaded.TryLoad(_roleId, out AgentMemoryState loaded));

                Assert.IsNotNull(loaded.Versions, "Memory versions must survive a disk round-trip (rollback feature).");
                Assert.AreEqual(2, loaded.Versions.Length, "Both recorded versions must persist.");
                Assert.AreEqual("v1-content", loaded.Versions[0].ContentAfter);
                Assert.AreEqual("v2-content", loaded.Versions[1].ContentAfter);
                Assert.AreEqual("v1-content", loaded.SystemPromptMemorySnapshot,
                    "System-prompt memory snapshot must persist (tail-update prompt-cache optimization).");
                Assert.AreEqual(1, loaded.SystemPromptMemoryVersion);
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
