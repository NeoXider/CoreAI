using System;
using System.IO;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Infrastructure.AiMemory;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Disk layout: <see cref="FileAgentMemoryStore"/> keeps MemoryTool text in <c>memory</c> and chat in
    /// <c>chatHistoryJson</c>. Clearing one must not wipe the other (prod chat UI vs long-term facts).
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
            store.AppendChatMessage(_roleId, "user", "Hello merchant", persistToDisk: true);
            store.AppendChatMessage(_roleId, "assistant", "Welcome.", persistToDisk: true);

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
            store.AppendChatMessage(_roleId, "user", "one", persistToDisk: true);
            store.ClearChatHistory(_roleId);

            ChatMessage[] history = store.GetChatHistory(_roleId);
            Assert.AreEqual(0, history.Length, "After ClearChatHistory, same store must reload empty history without throwing");
        }

        [Test]
        public void Clear_MemoryTool_OnDisk_Preserves_ChatHistory_Field()
        {
            FileAgentMemoryStore store = new();
            store.Save(_roleId, new AgentMemoryState { Memory = "will_clear", LastSystemPrompt = "s" });
            store.AppendChatMessage(_roleId, "user", "line1", persistToDisk: true);

            store.Clear(_roleId);

            // New store instance forces reload from disk (same process would keep ephemeral cache).
            FileAgentMemoryStore store2 = new();
            ChatMessage[] history = store2.GetChatHistory(_roleId);
            Assert.GreaterOrEqual(history.Length, 1, "Chat lines should survive memory Clear() on disk");
            Assert.That(history[0].Content, Does.Contain("line1"));

            Assert.IsTrue(store2.TryLoad(_roleId, out AgentMemoryState mem));
            Assert.IsTrue(string.IsNullOrEmpty(mem.Memory), "Memory field should be empty after Clear()");
        }
    }
}
