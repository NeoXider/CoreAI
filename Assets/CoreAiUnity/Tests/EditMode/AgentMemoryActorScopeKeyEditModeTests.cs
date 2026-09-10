using System;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Authority;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// The memory key of a named actor. <see cref="ActorContext.MemoryScope"/> is promised to be the "tenant,
    /// user, session and topic that persistence uses", which means two lessons of the same actor must not share
    /// one file, and a host with named actors can obtain the same key outside a turn as it does inside one.
    /// </summary>
    public sealed class AgentMemoryActorScopeKeyEditModeTests
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

        private static ActorContext NamedActor(string actorId, AgentMemoryScope scope, string roleId = "Teacher")
        {
            return new LocalActorIdentityProvider(actorId, "connection-1", "", ActorGrantSet.None, scope)
                .GetActorContext(roleId);
        }

        private static string KeyInsideTurn(ActorContext actor, IAgentMemoryScopeProvider provider)
        {
            KeyCapturingStore store = new();
            ScopedAgentMemoryStoreDecorator decorator = new(store, provider);
            using (AgentMemoryActorScope.Enter(actor))
            {
                decorator.Save("Teacher", new AgentMemoryState());
            }

            return store.SavedKeys[0];
        }

        private static string KeyOutsideTurn(IAgentMemoryScopeProvider provider)
        {
            KeyCapturingStore store = new();
            new ScopedAgentMemoryStoreDecorator(store, provider).Save("Teacher", new AgentMemoryState());
            return store.SavedKeys[0];
        }

        [Test]
        public void NamedActor_TwoTopicsOfTheSameActor_DoNotShareMemory()
        {
            IAgentMemoryScopeProvider host = new DefaultAgentMemoryScopeProvider();
            string lesson1 = KeyInsideTurn(NamedActor("player-42", new AgentMemoryScope("school", "u1", "", "lesson-1")), host);
            string lesson2 = KeyInsideTurn(NamedActor("player-42", new AgentMemoryScope("school", "u1", "", "lesson-2")), host);

            Assert.AreNotEqual(lesson1, lesson2,
                "The scope fields used to be dropped for a named actor: two lessons shared one file, even though " +
                "ActorContext.MemoryScope is documented as part of the key.");
            StringAssert.IsMatch("^actor-v1-[0-9a-f]{64}$", lesson1);
            StringAssert.IsMatch("^actor-v1-[0-9a-f]{64}$", lesson2);
        }

        [Test]
        public void NamedActor_TwoTenantsWithTheSameActorId_DoNotShareMemory()
        {
            IAgentMemoryScopeProvider host = new DefaultAgentMemoryScopeProvider();
            string tenantA = KeyInsideTurn(NamedActor("player-1", new AgentMemoryScope("tenant-a", "", "", "")), host);
            string tenantB = KeyInsideTurn(NamedActor("player-1", new AgentMemoryScope("tenant-b", "", "", "")), host);

            Assert.AreNotEqual(tenantA, tenantB,
                "The same actor id under two tenants is ordinary (a connection counter); their memory must never be mixed.");
        }

        [Test]
        public void NamedActor_OneLongLivedDecorator_TenantChangeStillForksMemory()
        {
            // WHY: production registers ScopedAgentMemoryStoreDecorator as a process-wide Singleton, so a
            // regression that pins the first scope seen per ActorId only shows up when ONE decorator
            // instance serves multiple calls - the tests above build a fresh decorator per call and cannot
            // see it. Two tenants sharing an ActorId (a connection counter, say) must never share memory.
            IAgentMemoryScopeProvider host = new DefaultAgentMemoryScopeProvider();
            KeyCapturingStore store = new();
            ScopedAgentMemoryStoreDecorator decorator = new(store, host);

            using (AgentMemoryActorScope.Enter(NamedActor("player-1", new AgentMemoryScope("tenant-a", "", "", ""))))
            {
                decorator.Save("Teacher", new AgentMemoryState());
            }

            using (AgentMemoryActorScope.Enter(NamedActor("player-1", new AgentMemoryScope("tenant-b", "", "", ""))))
            {
                decorator.Save("Teacher", new AgentMemoryState());
            }

            Assert.AreEqual(2, store.SavedKeys.Count,
                "A single long-lived decorator must not merge two tenants that happen to share an ActorId.");
            Assert.AreNotEqual(store.SavedKeys[0], store.SavedKeys[1]);
        }

        [Test]
        public void NamedActor_OneLongLivedDecorator_TopicChangeStillForksMemory()
        {
            IAgentMemoryScopeProvider host = new DefaultAgentMemoryScopeProvider();
            KeyCapturingStore store = new();
            ScopedAgentMemoryStoreDecorator decorator = new(store, host);

            using (AgentMemoryActorScope.Enter(
                       NamedActor("player-42", new AgentMemoryScope("school", "u1", "", "lesson-1"))))
            {
                decorator.Save("Teacher", new AgentMemoryState());
            }

            using (AgentMemoryActorScope.Enter(
                       NamedActor("player-42", new AgentMemoryScope("school", "u1", "", "lesson-2"))))
            {
                decorator.Save("Teacher", new AgentMemoryState());
            }

            Assert.AreEqual(2, store.SavedKeys.Count,
                "A single long-lived decorator must not merge two topics of the same actor into one lesson.");
            Assert.AreNotEqual(store.SavedKeys[0], store.SavedKeys[1]);
        }

        [Test]
        public void NamedActor_WithoutOwnScope_TakesTheHostDeclaredScope()
        {
            ActorContext actor = NamedActor("player-7", AgentMemoryScope.Empty);
            string learnerA = KeyInsideTurn(actor, new FixedScopeProvider(new AgentMemoryScope("school", "learner-a", "", "")));
            string learnerB = KeyInsideTurn(actor, new FixedScopeProvider(new AgentMemoryScope("school", "learner-b", "", "")));

            Assert.AreNotEqual(learnerA, learnerB,
                "The identity provider left the scope empty, but the host declared the learner through IAgentMemoryScopeProvider: " +
                "two learners sharing one actor id must not read each other.");
        }

        [Test]
        public void NamedActor_EmptyScopeEverywhere_KeepsTheLegacyActorKey()
        {
            // WHY: files of already existing named actors with no scope (server hosts) must not be orphaned.
            string legacy = KeyInsideTurn(NamedActor("durable-actor", AgentMemoryScope.Empty), new DefaultAgentMemoryScopeProvider());
            string again = KeyInsideTurn(NamedActor("durable-actor", AgentMemoryScope.Empty), new DefaultAgentMemoryScopeProvider());

            Assert.AreEqual(legacy, again);
            StringAssert.IsMatch("^actor-v1-[0-9a-f]{64}$", legacy);
        }

        [Test]
        public void NamedActor_OutsideTurn_HostReproducesTheInTurnKeyByEnteringTheActor()
        {
            FixedScopeProvider host = new(new AgentMemoryScope("school", "learner-a", "", ""));
            ActorContext actor = NamedActor("player-7", new AgentMemoryScope("school", "learner-a", "", "lesson-3"));

            string inside = KeyInsideTurn(actor, host);
            string outsideWithoutActor = KeyOutsideTurn(host);
            string outsideEntered = KeyInsideTurn(actor, host); // the same entry point the host has outside a turn

            Assert.AreNotEqual(inside, outsideWithoutActor,
                "Outside a turn there is no actor and the key is derived from the provider: a host with named actors read and " +
                "cleared a different file than the turn wrote. The divergence is honest, and the entry below closes it.");
            Assert.AreEqual(inside, outsideEntered,
                "AgentMemoryActorScope.Enter yields from the outside exactly the key the turn writes under.");
        }

        [Test]
        public void Enter_RejectsAContextThatNoProviderIssued()
        {
            Assert.Throws<InvalidOperationException>(() => AgentMemoryActorScope.Enter(default(ActorContext)),
                "A hand-assembled context must not unlock somebody else's memory.");
        }

        [Test]
        public void Enter_WithPlainScope_MatchesTheProviderKeyForThatScope()
        {
            AgentMemoryScope scope = new("school", "learner-z", "s1", "");
            KeyCapturingStore store = new();
            ScopedAgentMemoryStoreDecorator decorator = new(store, new DefaultAgentMemoryScopeProvider());

            using (AgentMemoryActorScope.Enter(scope))
            {
                decorator.Save("Teacher", new AgentMemoryState());
            }

            Assert.AreEqual(KeyOutsideTurn(new FixedScopeProvider(scope)), store.SavedKeys[0]);
        }
    }
}
