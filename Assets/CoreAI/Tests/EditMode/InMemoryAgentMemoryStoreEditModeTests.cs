using System;
using System.Collections.Generic;
using System.Threading;
using CoreAI.Ai;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>Contract tests for the process-only memory/chat/transcript backing.</summary>
    public sealed class InMemoryAgentMemoryStoreEditModeTests
    {
        [Test]
        public void SaveAndLoad_CloneMutableMemoryState()
        {
            InMemoryAgentMemoryStore store = new();
            AgentMemoryState input = new()
            {
                Memory = "original",
                Versions = new[]
                {
                    new AgentMemoryVersionSnapshot { Version = 1, ContentAfter = "original" }
                }
            };

            store.Save("role", input);
            input.Memory = "mutated caller";
            input.Versions[0].ContentAfter = "mutated caller version";

            Assert.IsTrue(store.TryLoad("role", out AgentMemoryState first));
            Assert.AreEqual("original", first.Memory);
            Assert.AreEqual("original", first.Versions[0].ContentAfter);

            first.Memory = "mutated loaded copy";
            first.Versions[0].ContentAfter = "mutated loaded version";
            Assert.IsTrue(store.TryLoad("role", out AgentMemoryState second));
            Assert.AreEqual("original", second.Memory);
            Assert.AreEqual("original", second.Versions[0].ContentAfter);
        }

        [Test]
        public void ChatAndTranscript_AreBoundedChronological_AndClearedTogether()
        {
            InMemoryAgentMemoryStore store = new(2, 2);
            for (int i = 1; i <= 3; i++)
            {
                store.AppendChatMessage("role", "user", $"chat-{i}", true);
                store.AppendTranscriptEntry("role", new ConversationEntry
                {
                    Kind = ConversationEntryKind.User,
                    Key = "user",
                    Content = $"transcript-{i}"
                }, true);
            }

            ChatMessage[] chat = store.GetChatHistory("role");
            Assert.AreEqual(2, chat.Length);
            Assert.AreEqual("chat-2", chat[0].Content);
            Assert.AreEqual("chat-3", chat[1].Content);
            Assert.AreEqual("chat-3", store.GetChatHistory("role", 1)[0].Content);

            IReadOnlyList<ConversationEntry> transcript = store.GetTranscriptEntries("role", 0);
            Assert.AreEqual(2, transcript.Count);
            Assert.AreEqual("transcript-2", transcript[0].Content);
            Assert.AreEqual("transcript-3", transcript[1].Content);
            transcript[0].Content = "mutated caller";
            Assert.AreEqual("transcript-2", store.GetTranscriptEntries("role", 0)[0].Content);

            store.ClearChatHistory("role");
            Assert.AreEqual(0, store.GetChatHistory("role").Length);
            Assert.AreEqual(0, store.GetTranscriptEntries("role", 0).Count);
        }

        [Test]
        public void Clear_PreservesConversation_AndAtomicMutationPersistsMemory()
        {
            InMemoryAgentMemoryStore store = new();
            store.AppendChatMessage("role", "user", "keep chat");
            store.AppendTranscriptEntry("role", new ConversationEntry { Content = "keep transcript" });
            store.Save("role", new AgentMemoryState { Memory = "clear me" });

            store.Clear("role");

            Assert.AreEqual(AgentMemoryLoadStatus.NotFound,
                store.TryLoadDetailed("role", out AgentMemoryState cleared));
            Assert.IsNull(cleared);
            Assert.AreEqual("keep chat", store.GetChatHistory("role")[0].Content);
            Assert.AreEqual("keep transcript", store.GetTranscriptEntries("role", 0)[0].Content);

            int result = store.MutateAsync("role", state =>
                    {
                        state.Memory = "atomic";
                        return 42;
                    },
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            Assert.AreEqual(42, result);
            Assert.IsTrue(store.TryLoad("role", out AgentMemoryState mutated));
            Assert.AreEqual("atomic", mutated.Memory);
            Assert.Throws<OperationCanceledException>(() => store.MutateAsync(
                    "role",
                    _ => 0,
                    new CancellationToken(true))
                .GetAwaiter()
                .GetResult());
        }
    }
}
