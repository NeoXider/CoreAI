using System;
using System.Collections.Generic;
using Newtonsoft.Json;
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
    /// EditMode coverage for MEAI tool calls, including MemoryTool, LuaTool,
    /// and shared JSON tool-call parsing.
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
            MemoryTool.MemoryResult result = JsonConvert.DeserializeObject<MemoryTool.MemoryResult>(resultJson);

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
            MemoryTool.MemoryResult result = JsonConvert.DeserializeObject<MemoryTool.MemoryResult>(resultJson);

            Assert.IsTrue(result.Success);
            Assert.IsTrue(store.States["TestRole"].Memory.Contains("Line 1"));
            Assert.IsTrue(store.States["TestRole"].Memory.Contains("Line 2"));
        }

        [Test]
        public async Task MemoryTool_ExecuteAsync_Append_UsesNewTextFallback()
        {
            TestMemoryStore store = new();
            store.Save("TestRole", new AgentMemoryState { Memory = "Line 1" });
            MemoryTool tool = new(store, "TestRole");

            string resultJson = await tool.ExecuteAsync("append", new_text: "Line 2");
            MemoryTool.MemoryResult result = JsonConvert.DeserializeObject<MemoryTool.MemoryResult>(resultJson);

            Assert.IsTrue(result.Success);
            Assert.AreEqual("Line 1\nLine 2", store.States["TestRole"].Memory);
        }

        [Test]
        public async Task MemoryTool_ExecuteAsync_Read_ReturnsCurrentMemoryWithoutSaving()
        {
            TestMemoryStore store = new();
            store.Save("TestRole", new AgentMemoryState { Memory = "Known learner fact" });
            store.LastSaved = null;
            MemoryTool tool = new(store, "TestRole");

            string resultJson = await tool.ExecuteAsync("read");
            MemoryTool.MemoryResult result = JsonConvert.DeserializeObject<MemoryTool.MemoryResult>(resultJson);

            Assert.IsTrue(result.Success);
            Assert.AreEqual("Known learner fact", result.Memory);
            Assert.AreEqual("Known learner fact".Length, result.MemoryLength);
            Assert.IsNull(store.LastSaved, "Read must not mutate or resave memory state.");
        }

        [Test]
        public async Task MemoryTool_ExecuteAsync_Clear_RemovesMemory()
        {
            TestMemoryStore store = new();
            store.Save("TestRole", new AgentMemoryState { Memory = "Old memory" });
            MemoryTool tool = new(store, "TestRole");

            string resultJson = await tool.ExecuteAsync("clear");
            MemoryTool.MemoryResult result = JsonConvert.DeserializeObject<MemoryTool.MemoryResult>(resultJson);

            Assert.IsTrue(result.Success);
            Assert.IsFalse(store.TryLoad("TestRole", out _), "Clear removes the role memory row.");
        }

        [Test]
        public async Task MemoryTool_ExecuteAsync_StrReplace_ReplacesFirstExactMatch()
        {
            TestMemoryStore store = new();
            store.Save("TestRole", new AgentMemoryState { Memory = "Quest: old\nQuest: old" });
            MemoryTool tool = new(store, "TestRole");

            string resultJson = await tool.ExecuteAsync("str_replace", old_text: "old", new_text: "new");
            MemoryTool.MemoryResult result = JsonConvert.DeserializeObject<MemoryTool.MemoryResult>(resultJson);

            Assert.IsTrue(result.Success);
            Assert.AreEqual("Quest: new\nQuest: old", store.States["TestRole"].Memory);
        }

        [Test]
        public async Task MemoryTool_ExecuteAsync_Insert_AddsContentAfterAnchorLine()
        {
            TestMemoryStore store = new();
            store.Save("TestRole", new AgentMemoryState { Memory = "Profile:\nFacts:" });
            MemoryTool tool = new(store, "TestRole");

            string resultJson = await tool.ExecuteAsync("insert", "Mood: wary", anchor: "Profile:");
            MemoryTool.MemoryResult result = JsonConvert.DeserializeObject<MemoryTool.MemoryResult>(resultJson);

            Assert.IsTrue(result.Success);
            Assert.AreEqual("Profile:\nMood: wary\nFacts:", store.States["TestRole"].Memory);
        }

        [Test]
        public async Task MemoryTool_ExecuteAsync_Delete_RemovesFirstExactBlock()
        {
            TestMemoryStore store = new();
            store.Save("TestRole", new AgentMemoryState { Memory = "Keep\nDelete me\nKeep\nDelete me" });
            MemoryTool tool = new(store, "TestRole");

            string resultJson = await tool.ExecuteAsync("delete", old_text: "Delete me\n");
            MemoryTool.MemoryResult result = JsonConvert.DeserializeObject<MemoryTool.MemoryResult>(resultJson);

            Assert.IsTrue(result.Success);
            Assert.AreEqual("Keep\nKeep\nDelete me", store.States["TestRole"].Memory);
        }

        [Test]
        public async Task MemoryTool_ExecuteAsync_Rename_RenamesFirstLeadingKeyLabel()
        {
            TestMemoryStore store = new();
            store.Save("TestRole", new AgentMemoryState { Memory = "Profile: calm\nProfile: duplicate" });
            MemoryTool tool = new(store, "TestRole");

            string resultJson = await tool.ExecuteAsync("rename", old_text: "Profile", new_text: "Identity");
            MemoryTool.MemoryResult result = JsonConvert.DeserializeObject<MemoryTool.MemoryResult>(resultJson);

            Assert.IsTrue(result.Success);
            Assert.AreEqual("Identity: calm\nProfile: duplicate", store.States["TestRole"].Memory);
        }

        [Test]
        public async Task MemoryTool_ExecuteAsync_Versioning_RevertRestoresPriorSnapshot()
        {
            TestMemoryStore store = new();
            MemoryTool tool = new(store, "TestRole");

            await tool.ExecuteAsync("write", "Version one");
            await tool.ExecuteAsync("append", "Version two");

            IReadOnlyList<AgentMemoryVersionSnapshot> versions = store.ListVersions("TestRole");
            Assert.AreEqual(2, versions.Count);
            Assert.AreEqual(1, versions[0].Version);
            Assert.AreEqual("Version one", versions[0].ContentAfter);

            Assert.IsTrue(store.Revert("TestRole", 1, out string error), error);
            Assert.AreEqual("Version one", store.States["TestRole"].Memory);

            IReadOnlyList<AgentMemoryVersionSnapshot> afterRevert = store.ListVersions("TestRole");
            Assert.AreEqual(3, afterRevert.Count);
            Assert.AreEqual("revert", afterRevert[2].Action);
        }

        #endregion

        #region WaitTool Tests

        [Test]
        public void WaitLlmTool_CreateAIFunction_ReturnsNonNull()
        {
            WaitLlmTool tool = new(0.01d);

            AIFunction function = tool.CreateAIFunction();

            Assert.IsNotNull(function);
            Assert.AreEqual("wait", function.Name);
            Assert.IsTrue(tool.AllowDuplicates);
        }

        [Test]
        public async Task WaitLlmTool_ExecuteAsync_ClampsAndReturnsResult()
        {
            WaitLlmTool tool = new(0.001d);

            string resultJson = await tool.ExecuteAsync(10d, "polling test");
            WaitLlmTool.WaitResult result = JsonConvert.DeserializeObject<WaitLlmTool.WaitResult>(resultJson);

            Assert.IsTrue(result.Success);
            Assert.AreEqual(10d, result.RequestedSeconds, 0.0001d);
            Assert.AreEqual(0.001d, result.WaitedSeconds, 0.0001d);
            Assert.AreEqual("polling test", result.Reason);
        }

        #endregion

        #region LuaTool Tests

        [Test]
        public void LuaTool_CreateAIFunction_ReturnsNonNull()
        {
            TestLuaExecutor executor = new();
            LuaTool tool = new(executor,
                UnityEngine.ScriptableObject.CreateInstance<Infrastructure.Llm.CoreAISettingsAsset>(),
                Logging.NullLog.Instance);

            AIFunction function = tool.CreateAIFunction();

            Assert.IsNotNull(function);
            Assert.AreEqual("execute_lua", function.Name);
        }

        [Test]
        public async Task LuaTool_ExecuteAsync_EmptyCode_ReturnsError()
        {
            TestLuaExecutor executor = new();
            LuaTool tool = new(executor,
                UnityEngine.ScriptableObject.CreateInstance<Infrastructure.Llm.CoreAISettingsAsset>(),
                Logging.NullLog.Instance);

            string resultJson = await tool.ExecuteAsync("");
            LuaTool.LuaResult result = JsonConvert.DeserializeObject<LuaTool.LuaResult>(resultJson);

            Assert.IsFalse(result.Success);
            Assert.AreEqual("Lua code is required", result.Error);
        }

        [Test]
        public async Task LuaTool_ExecuteAsync_ValidCode_CallsExecutor()
        {
            TestLuaExecutor executor = new();
            LuaTool tool = new(executor,
                UnityEngine.ScriptableObject.CreateInstance<Infrastructure.Llm.CoreAISettingsAsset>(),
                Logging.NullLog.Instance);

            string resultJson = await tool.ExecuteAsync("report('test')");
            LuaTool.LuaResult result = JsonConvert.DeserializeObject<LuaTool.LuaResult>(resultJson);

            Assert.IsTrue(result.Success);
            Assert.IsTrue(executor.WasCalled);
            Assert.AreEqual("report('test')", executor.LastCode);
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

            public void ClearChatHistory(string roleId)
            {
            }

            public void AppendChatMessage(string roleId, string role, string content, bool persistToDisk = true)
            {
            }

            public Ai.ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0)
            {
                return Array.Empty<Ai.ChatMessage>();
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

            public void ClearChatHistory(string roleId)
            {
            }

            public void AppendChatMessage(string roleId, string role, string content, bool persistToDisk = true)
            {
            }

            public Ai.ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0)
            {
                return Array.Empty<Ai.ChatMessage>();
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
