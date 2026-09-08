using System;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Authority;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Ключ памяти именованного актора. <see cref="ActorContext.MemoryScope"/> обещан как «tenant, user,
    /// session и topic, которыми пользуется персистентность» — значит, два урока одного актора не делят
    /// один файл, а хост с именованными акторами может получить тот же ключ вне хода, что и внутри него.
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
                "Раньше для именованного актора поля scope отбрасывались: два урока делили один файл, хотя " +
                "ActorContext.MemoryScope документирован как участник ключа.");
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
                "Одинаковый id актора у двух арендаторов — обычное дело (счётчик соединений); смешивать их память нельзя.");
        }

        [Test]
        public void NamedActor_WithoutOwnScope_TakesTheHostDeclaredScope()
        {
            ActorContext actor = NamedActor("player-7", AgentMemoryScope.Empty);
            string learnerA = KeyInsideTurn(actor, new FixedScopeProvider(new AgentMemoryScope("school", "learner-a", "", "")));
            string learnerB = KeyInsideTurn(actor, new FixedScopeProvider(new AgentMemoryScope("school", "learner-b", "", "")));

            Assert.AreNotEqual(learnerA, learnerB,
                "Провайдер личности не заполнил scope, но хост объявил ученика через IAgentMemoryScopeProvider: " +
                "два ученика на одном id актора не должны читать друг друга.");
        }

        [Test]
        public void NamedActor_EmptyScopeEverywhere_KeepsTheLegacyActorKey()
        {
            // WHY: файлы уже существующих именованных акторов без scope (серверные хосты) не должны осиротеть.
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
            string outsideEntered = KeyInsideTurn(actor, host); // тот же вход, что доступен хосту вне хода

            Assert.AreNotEqual(inside, outsideWithoutActor,
                "Вне хода актора нет, и ключ считается по провайдеру: хост с именованными акторами читал и чистил " +
                "не тот файл, что писал ход. Расхождение честное, и закрывается оно входом ниже.");
            Assert.AreEqual(inside, outsideEntered,
                "AgentMemoryActorScope.Enter даёт снаружи ровно тот ключ, под которым пишет ход.");
        }

        [Test]
        public void Enter_RejectsAContextThatNoProviderIssued()
        {
            Assert.Throws<InvalidOperationException>(() => AgentMemoryActorScope.Enter(default(ActorContext)),
                "Собранный вручную контекст не должен открывать чужую память.");
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
