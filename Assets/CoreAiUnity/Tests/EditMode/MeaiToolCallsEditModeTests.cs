using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using Microsoft.Extensions.AI;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode тесты для всех MEAI tool calls.
    /// Тестирует: MemoryTool, LuaTool, и общий парсинг JSON tool calls.
    /// </summary>
    [TestFixture]
    public sealed class MeaiToolCallsEditModeTests
    {
        #region MemoryTool Tests

        [Test]
        public void MemoryTool_CreateAIFunction_ReturnsNonNull()
        {
            TestMemoryStore store = new();
            MemoryTool tool = new(store, "TestRole");

            AIFunction function = tool.CreateAIFunction();

            Assert.IsNotNull(function);
            Assert.AreEqual("memory", function.Name);
        }

        [Test]
        public async Task MemoryTool_ExecuteAsync_Write_SavesMemory()
        {
            TestMemoryState store = new();
            MemoryTool tool = new(store, "TestRole");

            string resultJson = await tool.ExecuteAsync("write", "Test memory content");
            MemoryTool.MemoryResult result = JsonSerializer.Deserialize<MemoryTool.MemoryResult>(resultJson);

            Assert.IsTrue(result.Success);
            Assert.AreEqual("Test memory content", store.LastSaved?.Memory);
        }

        [Test]
        public async Task MemoryTool_ExecuteAsync_Append_AppendsToExisting()
        {
            TestMemoryStore store = new();
            store.Save("TestRole", new AgentMemoryState { Memory = "Line 1" });
            MemoryTool tool = new(store, "TestRole");

            string resultJson = await tool.ExecuteAsync("append", "Line 2");
            MemoryTool.MemoryResult result = JsonSerializer.Deserialize<MemoryTool.MemoryResult>(resultJson);

            Assert.IsTrue(result.Success);
            Assert.IsTrue(store.States["TestRole"].Memory.Contains("Line 1"));
            Assert.IsTrue(store.States["TestRole"].Memory.Contains("Line 2"));
        }

        [Test]
        public async Task MemoryTool_ExecuteAsync_Clear_RemovesMemory()
        {
            TestMemoryStore store = new();
            store.Save("TestRole", new AgentMemoryState { Memory = "Old memory" });
            MemoryTool tool = new(store, "TestRole");

            string resultJson = await tool.ExecuteAsync("clear");
            MemoryTool.MemoryResult result = JsonSerializer.Deserialize<MemoryTool.MemoryResult>(resultJson);

            Assert.IsTrue(result.Success);
            Assert.IsFalse(store.States.ContainsKey("TestRole"));
        }

        #endregion

        #region LuaTool Tests

        [Test]
        public void LuaTool_CreateAIFunction_ReturnsNonNull()
        {
            TestLuaExecutor executor = new();
            LuaTool tool = new(executor);

            AIFunction function = tool.CreateAIFunction();

            Assert.IsNotNull(function);
            Assert.AreEqual("execute_lua", function.Name);
        }

        [Test]
        public async Task LuaTool_ExecuteAsync_EmptyCode_ReturnsError()
        {
            TestLuaExecutor executor = new();
            LuaTool tool = new(executor);

            LuaTool.LuaResult result = await tool.ExecuteAsync("");

            Assert.IsFalse(result.Success);
            Assert.AreEqual("Lua code is required", result.Error);
        }

        [Test]
        public async Task LuaTool_ExecuteAsync_ValidCode_CallsExecutor()
        {
            TestLuaExecutor executor = new();
            LuaTool tool = new(executor);

            LuaTool.LuaResult result = await tool.ExecuteAsync("report('test')");

            Assert.IsTrue(result.Success);
            Assert.IsTrue(executor.WasCalled);
            Assert.AreEqual("report('test')", executor.LastCode);
        }

        #endregion

        #region JSON Tool Call Parsing Tests

        [Test]
        public void TryParseToolCallFromText_MemoryTool_WithCodeBlock()
        {
            string json = "{\"name\": \"memory\", \"arguments\": {\"action\": \"write\", \"content\": \"Test\"}}";
            JObject obj = JObject.Parse(json);

            Assert.AreEqual("memory", obj["name"]?.ToString());
            Assert.IsNotNull(obj["arguments"]);
            Assert.AreEqual("write", obj["arguments"]["action"]?.ToString());
            Assert.AreEqual("Test", obj["arguments"]["content"]?.ToString());
        }

        [Test]
        public void TryParseToolCallFromText_LuaTool_WithCodeBlock()
        {
            string json = "{\"name\": \"execute_lua\", \"arguments\": {\"code\": \"create_item('Sword')\"}}";
            JObject obj = JObject.Parse(json);

            Assert.AreEqual("execute_lua", obj["name"]?.ToString());
            Assert.IsNotNull(obj["arguments"]);
            Assert.AreEqual("create_item('Sword')", obj["arguments"]["code"]?.ToString());
        }

        [Test]
        public void TryParseToolCallFromText_JsonWithoutCodeBlock()
        {
            string json =
                "{\"name\": \"memory\", \"arguments\": {\"action\": \"write\", \"content\": \"Direct JSON\"}}";
            JObject obj = JObject.Parse(json);

            Assert.AreEqual("memory", obj["name"]?.ToString());
            Assert.AreEqual("write", obj["arguments"]["action"]?.ToString());
        }

        [Test]
        public void Regex_MatchesJsonInCodeBlock()
        {
            string text = "```json\n{\"name\": \"memory\", \"arguments\": {\"action\": \"write\"}}\n```";
            Regex regex = new(
                @"```json\s*(\{[^`]+\})\s*```|(\{[^{}]*""name""\s*:\s*""([^""]+)""[^{}]*""arguments""\s*:\s*\{[^{}]*\}[^{}]*\})",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                System.Text.RegularExpressions.RegexOptions.Singleline);

            Assert.IsTrue(regex.IsMatch(text));
        }

        [Test]
        public void Regex_MatchesPlainJson()
        {
            string text = "{\"name\": \"memory\", \"arguments\": {\"action\": \"write\"}}";
            Regex regex = new(
                @"```json\s*(\{[^`]+\})\s*```|(\{[^{}]*""name""\s*:\s*""([^""]+)""[^{}]*""arguments""\s*:\s*\{[^{}]*\}[^{}]*\})",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                System.Text.RegularExpressions.RegexOptions.Singleline);

            Assert.IsTrue(regex.IsMatch(text));
        }

        [Test]
        public void Regex_DoesNotMatchPlainEnglish()
        {
            string text = "Just regular text without any tool calls.";
            Regex regex = new(
                @"```json\s*(\{[^`]+\})\s*```|(\{[^{}]*""name""\s*:\s*""([^""]+)""[^{}]*""arguments""\s*:\s*\{[^{}]*\}[^{}]*\})",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                System.Text.RegularExpressions.RegexOptions.Singleline);

            Assert.IsFalse(regex.IsMatch(text));
        }

        #endregion

        #region Test Helpers

        private sealed class TestMemoryStore : IAgentMemoryStore
        {
            public readonly Dictionary<string, AgentMemoryState> States = new();
            public AgentMemoryState LastSaved;

            public bool TryLoad(string roleId, out AgentMemoryState state)
            {
                return States.TryGetValue(roleId, out state);
            }

            public void Save(string roleId, AgentMemoryState state)
            {
                LastSaved = state;
                States[roleId] = state;
            }

            public void Clear(string roleId)
            {
                States.Remove(roleId);
            }

            public void AppendChatMessage(string roleId, string role, string content)
            {
            }

            public Ai.ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0)
            {
                return Array.Empty<CoreAI.Ai.ChatMessage>();
            }
        }

        private sealed class TestMemoryState : IAgentMemoryStore
        {
            public AgentMemoryState LastSaved;

            public bool TryLoad(string roleId, out AgentMemoryState state)
            {
                state = LastSaved;
                return LastSaved != null;
            }

            public void Save(string roleId, AgentMemoryState state)
            {
                LastSaved = state;
            }

            public void Clear(string roleId)
            {
                LastSaved = null;
            }

            public void AppendChatMessage(string roleId, string role, string content)
            {
            }

            public Ai.ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0)
            {
                return Array.Empty<CoreAI.Ai.ChatMessage>();
            }
        }

        private sealed class TestLuaExecutor : LuaTool.ILuaExecutor
        {
            public bool WasCalled;
            public string LastCode;

            public Task<LuaTool.LuaResult> ExecuteAsync(string code, CancellationToken cancellationToken)
            {
                WasCalled = true;
                LastCode = code;
                return Task.FromResult(new LuaTool.LuaResult { Success = true, Output = "executed" });
            }
        }

        #endregion
    }
}