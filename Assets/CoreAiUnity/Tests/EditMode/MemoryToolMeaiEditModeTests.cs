using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using Microsoft.Extensions.AI;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Тесты для MEAI MemoryTool через AIFunctionFactory.Create().
    /// Проверяют что memory tool корректно работает через MEAI function calling.
    /// </summary>
    public sealed class MemoryToolMeaiEditModeTests
    {
        private sealed class InMemoryStore : IAgentMemoryStore
        {
            public System.Collections.Generic.Dictionary<string, AgentMemoryState> States = new();

            public bool TryLoad(string roleId, out AgentMemoryState state)
            {
                return States.TryGetValue(roleId, out state);
            }

            public void Save(string roleId, AgentMemoryState state)
            {
                States[roleId] = state;
            }

            public void Clear(string roleId)
            {
                States.Remove(roleId);
            }
        }

        [Test]
        public async Task MemoryTool_WriteAction_ReplacesMemory()
        {
            InMemoryStore store = new();
            MemoryTool tool = new(store, "Creator");
            AIFunction function = tool.CreateAIFunction();

            Assert.IsNotNull(function);
            Assert.AreEqual("memory", function.Name);

            MemoryTool.MemoryResult result = await tool.ExecuteAsync("write", "test memory content");

            Assert.IsTrue(result.Success);
            Assert.IsTrue(store.TryLoad("Creator", out AgentMemoryState state));
            Assert.AreEqual("test memory content", state.Memory);
        }

        [Test]
        public async Task MemoryTool_AppendAction_AddsToExisting()
        {
            InMemoryStore store = new();
            store.Save("Creator", new AgentMemoryState { Memory = "first line" });

            MemoryTool tool = new(store, "Creator");
            MemoryTool.MemoryResult result = await tool.ExecuteAsync("append", "second line");

            Assert.IsTrue(result.Success);
            Assert.IsTrue(store.TryLoad("Creator", out AgentMemoryState state));
            StringAssert.Contains("first line", state.Memory);
            StringAssert.Contains("second line", state.Memory);
        }

        [Test]
        public async Task MemoryTool_ClearAction_RemovesMemory()
        {
            InMemoryStore store = new();
            store.Save("Creator", new AgentMemoryState { Memory = "to be cleared" });

            MemoryTool tool = new(store, "Creator");
            MemoryTool.MemoryResult result = await tool.ExecuteAsync("clear");

            Assert.IsTrue(result.Success);
            Assert.IsFalse(store.TryLoad("Creator", out _));
        }

        [Test]
        public async Task MemoryTool_InvalidAction_ReturnsError()
        {
            InMemoryStore store = new();
            MemoryTool tool = new(store, "Creator");

            MemoryTool.MemoryResult result = await tool.ExecuteAsync("invalid_action");

            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Error.Contains("Unknown action"));
        }

        [Test]
        public async Task MemoryTool_WriteWithoutContent_ReturnsError()
        {
            InMemoryStore store = new();
            MemoryTool tool = new(store, "Creator");

            MemoryTool.MemoryResult result = await tool.ExecuteAsync("write", null);

            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Error.Contains("Content is required"));
        }

        [Test]
        public async Task MemoryTool_MultipleWrites_LastOneWins()
        {
            InMemoryStore store = new();
            MemoryTool tool = new(store, "Creator");

            await tool.ExecuteAsync("write", "first");
            await tool.ExecuteAsync("write", "second");
            await tool.ExecuteAsync("write", "third");

            Assert.IsTrue(store.TryLoad("Creator", out AgentMemoryState state));
            Assert.AreEqual("third", state.Memory);
        }

        [Test]
        public async Task MemoryTool_DifferentRoles_IsolatedMemory()
        {
            InMemoryStore store = new();
            MemoryTool creatorTool = new(store, "Creator");
            MemoryTool programmerTool = new(store, "Programmer");

            await creatorTool.ExecuteAsync("write", "creator memory");
            await programmerTool.ExecuteAsync("write", "programmer memory");

            Assert.IsTrue(store.TryLoad("Creator", out AgentMemoryState creatorState));
            Assert.IsTrue(store.TryLoad("Programmer", out AgentMemoryState programmerState));

            Assert.AreEqual("creator memory", creatorState.Memory);
            Assert.AreEqual("programmer memory", programmerState.Memory);
        }
    }
}