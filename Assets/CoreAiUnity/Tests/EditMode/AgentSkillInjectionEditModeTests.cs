#if COREAI_LLM
using System;
using System.Collections.Generic;
using System.Linq;
using CoreAI.Ai;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Host-side skill preloading: <see cref="AgentSkillInjection.InjectSkillIntoHistory"/> pushes a skill's
    /// read_skill payload into a role's history (as a hidden "tool" row) without running a model turn.
    /// </summary>
    public sealed class AgentSkillInjectionEditModeTests
    {
        private sealed class InMemoryStore : IAgentMemoryStore
        {
            private readonly Dictionary<string, List<ChatMessage>> _history = new();

            public bool TryLoad(string roleId, out AgentMemoryState state)
            {
                state = null;
                return false;
            }

            public void Save(string roleId, AgentMemoryState state)
            {
            }

            public void Clear(string roleId)
            {
                _history.Remove(roleId);
            }

            public void ClearChatHistory(string roleId)
            {
                _history.Remove(roleId);
            }

            public void AppendChatMessage(string roleId, string role, string content, bool persistToDisk = true)
            {
                if (!_history.TryGetValue(roleId, out List<ChatMessage> list))
                {
                    list = new List<ChatMessage>();
                    _history[roleId] = list;
                }

                list.Add(new ChatMessage(role, content));
            }

            public ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0)
            {
                return _history.TryGetValue(roleId, out List<ChatMessage> list)
                    ? list.ToArray()
                    : Array.Empty<ChatMessage>();
            }
        }

        private sealed class FakeTool : ILlmTool
        {
            public string Name => "forge_item";
            public string Description => "Forge an item from materials";
            public string ParametersSchema => "{\"type\":\"object\",\"properties\":{}}";
            public bool AllowDuplicates => false;
        }

        [Test]
        public void InjectSkillIntoHistory_PushesSkillInstructionsAndTools_AsHiddenToolRow()
        {
            InMemoryStore store = new();
            SkillSet skill = new(
                "Crafting",
                "Forge weapons and armor from raw materials",
                "Use the forge: heat the metal, then strike. Quality depends on temperature.",
                new FakeTool());

            bool ok = AgentSkillInjection.InjectSkillIntoHistory(store, "Programmer", skill);

            Assert.IsTrue(ok, "Injection should succeed for a valid store/role/skill.");
            ChatMessage[] history = store.GetChatHistory("Programmer");
            Assert.AreEqual(1, history.Length, "Exactly one history row should be appended.");
            Assert.AreEqual("tool", history[0].Role,
                "Injected skill must be stored with the hidden 'tool' role (model sees it, chat hides it).");
            StringAssert.Contains("Crafting", history[0].Content, "Skill name must be present.");
            StringAssert.Contains("Use the forge", history[0].Content, "Skill instructions must be present.");
            StringAssert.Contains("forge_item", history[0].Content, "Skill tool name must be present.");
        }

        [Test]
        public void InjectSkillIntoHistory_DoesNotRunAModelTurn_OnlyAppendsHistory()
        {
            InMemoryStore store = new();
            SkillSet skill = new("Alchemy", "Brew potions", "Combine reagents over heat.", new FakeTool());

            AgentSkillInjection.InjectSkillIntoHistory(store, "Programmer", skill);

            // The only side effect is one appended history row; nothing simulates a model response.
            ChatMessage[] history = store.GetChatHistory("Programmer");
            Assert.AreEqual(1, history.Length);
            Assert.IsFalse(history.Any(m => m.Role == "assistant"),
                "Preloading a skill must not produce an assistant turn.");
        }

        [Test]
        public void InjectSkillIntoHistory_InvalidArgs_ReturnFalse_NoAppend()
        {
            InMemoryStore store = new();
            SkillSet skill = new("S", "d", new FakeTool());

            Assert.IsFalse(AgentSkillInjection.InjectSkillIntoHistory(null, "R", skill),
                "Null store returns false.");
            Assert.IsFalse(AgentSkillInjection.InjectSkillIntoHistory(store, "R", null),
                "Null skill returns false.");
            Assert.IsFalse(AgentSkillInjection.InjectSkillIntoHistory(store, "  ", skill),
                "Blank role id returns false.");

            Assert.AreEqual(0, store.GetChatHistory("R").Length, "No history written on invalid input.");
        }
    }
}
#endif
