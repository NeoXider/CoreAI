using System.Collections.Generic;
using System.Linq;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode тесты для AgentBuilder — конструктора кастомных агентов.
    /// </summary>
    [TestFixture]
    public sealed class AgentBuilderEditModeTests
    {
        [Test]
        public void Builder_CreatesBasicAgent_WithDefaults()
        {
            AgentConfig config = new AgentBuilder("TestAgent")
                .WithSystemPrompt("You are a test agent.")
                .Build();

            Assert.AreEqual("TestAgent", config.RoleId);
            Assert.AreEqual("You are a test agent.", config.SystemPrompt);
            Assert.AreEqual(0, config.Tools.Count);
            Assert.AreEqual(AgentMode.ToolsAndChat, config.Mode);
        }

        [Test]
        public void Builder_AddsTools_Correctly()
        {
            AgentConfig config = new AgentBuilder("ToolAgent")
                .WithSystemPrompt("You use tools.")
                .WithTool(new MemoryLlmTool())
                .Build();

            Assert.AreEqual(1, config.Tools.Count);
            Assert.AreEqual("memory", config.Tools[0].Name);
        }

        [Test]
        public void Builder_AddsMultipleTools_Correctly()
        {
            AgentConfig config = new AgentBuilder("MultiToolAgent")
                .WithSystemPrompt("You use many tools.")
                .WithTool(new MemoryLlmTool())
                .WithTool(new MemoryLlmTool())
                .WithTool(new MemoryLlmTool())
                .Build();

            Assert.AreEqual(3, config.Tools.Count);
        }

        [Test]
        public void Builder_WithMemory_AddsMemoryTool()
        {
            AgentConfig config = new AgentBuilder("MemoryAgent")
                .WithSystemPrompt("You remember things.")
                .WithMemory()
                .Build();

            Assert.AreEqual(1, config.Tools.Count);
            Assert.AreEqual("memory", config.Tools[0].Name);
        }

        [Test]
        public void Builder_WithMode_SetsCorrectMode()
        {
            AgentConfig toolsOnly = new AgentBuilder("ToolsOnly")
                .WithMode(AgentMode.ToolsOnly)
                .Build();

            AgentConfig toolsAndChat = new AgentBuilder("ToolsAndChat")
                .WithMode(AgentMode.ToolsAndChat)
                .Build();

            AgentConfig chatOnly = new AgentBuilder("ChatOnly")
                .WithMode(AgentMode.ChatOnly)
                .Build();

            Assert.AreEqual(AgentMode.ToolsOnly, toolsOnly.Mode);
            Assert.AreEqual(AgentMode.ToolsAndChat, toolsAndChat.Mode);
            Assert.AreEqual(AgentMode.ChatOnly, chatOnly.Mode);
        }

        [Test]
        public void Builder_FullConfiguration_AllFieldsSet()
        {
            AgentConfig config = new AgentBuilder("FullAgent")
                .WithSystemPrompt("You are a full configured agent.")
                .WithMemory(MemoryToolAction.Append)
                .WithMode(AgentMode.ToolsAndChat)
                .Build();

            Assert.AreEqual("FullAgent", config.RoleId);
            Assert.AreEqual("You are a full configured agent.", config.SystemPrompt);
            Assert.AreEqual(1, config.Tools.Count);
            Assert.AreEqual(AgentMode.ToolsAndChat, config.Mode);
        }

        [Test]
        public void Config_ApplyToPolicy_SetsToolsOnPolicy()
        {
            AgentMemoryPolicy policy = new();

            AgentConfig config = new AgentBuilder("PolicyAgent")
                .WithSystemPrompt("Test")
                .WithMemory()
                .Build();

            config.ApplyToPolicy(policy);

            IReadOnlyList<ILlmTool> tools = policy.GetToolsForRole("PolicyAgent");
            Assert.Greater(tools.Count, 0, "Should have tools after ApplyToPolicy");
        }

        [Test]
        public void Builder_ChainCalls_ReturnsSameBuilder()
        {
            AgentBuilder builder = new("ChainAgent");

            AgentBuilder result1 = builder.WithSystemPrompt("Test");
            AgentBuilder result2 = builder.WithMemory();
            AgentBuilder result3 = builder.WithMode(AgentMode.ChatOnly);

            Assert.AreSame(builder, result1);
            Assert.AreSame(builder, result2);
            Assert.AreSame(builder, result3);
        }
    }
}