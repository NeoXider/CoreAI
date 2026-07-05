using System;
using System.Collections.Generic;
using CoreAI.Ai;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// The decorator maps role ids to scoped keys. The encoding must be injective: distinct scope tuples
    /// must never collide on the same key, or one user/session would read another's durable memory.
    /// </summary>
    public sealed class ScopedAgentMemoryStoreDecoratorEditModeTests
    {
        private sealed class KeyCapturingStore : IAgentMemoryStore
        {
            public readonly List<string> SavedKeys = new();

            public bool TryLoad(string roleId, out AgentMemoryState state)
            {
                state = null;
                return false;
            }

            public void Save(string roleId, AgentMemoryState state)
            {
                SavedKeys.Add(roleId);
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
                return Array.Empty<ChatMessage>();
            }
        }

        private sealed class FixedScopeProvider : IAgentMemoryScopeProvider
        {
            private readonly AgentMemoryScope _scope;

            public FixedScopeProvider(AgentMemoryScope scope)
            {
                _scope = scope;
            }

            public AgentMemoryScope GetScope(string roleId)
            {
                return _scope;
            }
        }

        private static string ScopedKeyFor(AgentMemoryScope scope, string roleId)
        {
            KeyCapturingStore store = new();
            ScopedAgentMemoryStoreDecorator decorator = new(store, new FixedScopeProvider(scope));
            decorator.Save(roleId, new AgentMemoryState());
            return store.SavedKeys[0];
        }

        [Test]
        public void ScopedKeys_AmbiguousUnderscorePlacement_DoNotCollide()
        {
            // Without an injective encoding these two distinct tuples both produced "...a___b...".
            string keyA = ScopedKeyFor(new AgentMemoryScope("", "a_", "b", ""), "role");
            string keyB = ScopedKeyFor(new AgentMemoryScope("", "a", "_b", ""), "role");

            Assert.AreNotEqual(keyA, keyB,
                "Distinct user/session tuples must map to distinct memory keys (isolation).");
        }

        [Test]
        public void EmptyScope_PreservesBareRoleKey()
        {
            string key = ScopedKeyFor(AgentMemoryScope.Empty, "merchant");
            Assert.AreEqual("merchant", key,
                "An empty scope must preserve the bare role id (unchanged default behavior).");
        }

        [Test]
        public void DistinctUsers_ProduceDistinctKeys()
        {
            string keyUser1 = ScopedKeyFor(new AgentMemoryScope("t", "user1", "s", ""), "role");
            string keyUser2 = ScopedKeyFor(new AgentMemoryScope("t", "user2", "s", ""), "role");
            Assert.AreNotEqual(keyUser1, keyUser2);
        }
    }
}