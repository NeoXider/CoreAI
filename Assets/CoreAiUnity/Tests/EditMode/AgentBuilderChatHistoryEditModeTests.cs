using System.Collections.Generic;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode coverage for AgentBuilder chat history and context-window options.
    /// </summary>
    public sealed class AgentBuilderChatHistoryEditModeTests
    {
        private string _savedUniversalPrefix;

        [SetUp]
        public void SetUp()
        {
            _savedUniversalPrefix = CoreAISettings.UniversalSystemPromptPrefix;
            CoreAISettings.UniversalSystemPromptPrefix = string.Empty;
        }

        [TearDown]
        public void TearDown()
        {
            CoreAISettings.UniversalSystemPromptPrefix = _savedUniversalPrefix;
        }

        [Test]
        public void WithChatHistory_Default_ShouldUseSettingsContext()
        {
            AgentConfig config = new AgentBuilder("TestAgent")
                .WithSystemPrompt("Test prompt")
                .WithChatHistory()
                .Build();

            Assert.IsTrue(config.WithChatHistory);
            Assert.AreEqual(CoreAISettings.ContextWindowTokens, config.ContextWindowTokens);
        }

        [Test]
        public void WithChatHistory_WithCustomTokens_ShouldOverride()
        {
            AgentConfig config = new AgentBuilder("TestAgent")
                .WithSystemPrompt("Test prompt")
                .WithChatHistory(4096)
                .Build();

            Assert.IsTrue(config.WithChatHistory);
            Assert.AreEqual(4096, config.ContextWindowTokens);
        }

        [Test]
        public void WithChatHistory_ZeroTokens_ShouldUseZero()
        {
            AgentConfig config = new AgentBuilder("TestAgent")
                .WithSystemPrompt("Test prompt")
                .WithChatHistory(0)
                .Build();

            Assert.IsTrue(config.WithChatHistory);
            Assert.AreEqual(0, config.ContextWindowTokens);
        }

        [Test]
        public void WithChatHistory_WithPersist_ShouldSetPersistFlag()
        {
            AgentConfig config = new AgentBuilder("TestAgent")
                .WithSystemPrompt("Test prompt")
                .WithChatHistory(4096, true)
                .Build();

            Assert.IsTrue(config.WithChatHistory);
            Assert.AreEqual(4096, config.ContextWindowTokens);
            Assert.IsTrue(config.PersistChatHistoryBetweenSessions);
        }

        [Test]
        public void DefaultBuilder_ShouldEnableChatHistoryWithoutPersistence()
        {
            AgentConfig config = new AgentBuilder("TestAgent")
                .WithSystemPrompt("Test prompt")
                .Build();

            Assert.IsTrue(config.WithChatHistory);
            Assert.IsFalse(config.PersistChatHistoryBetweenSessions);
            Assert.AreEqual(30, config.MaxChatHistoryMessages);
            Assert.AreEqual(ToolResultMemoryPolicy.CompactSummary, config.ToolResultMemory);
        }

        [Test]
        public void WithoutChatHistory_ShouldOptOutOfDefaultHistory()
        {
            AgentConfig config = new AgentBuilder("TestAgent")
                .WithSystemPrompt("Test prompt")
                .WithoutChatHistory()
                .Build();

            Assert.IsFalse(config.WithChatHistory);
            Assert.IsFalse(config.PersistChatHistoryBetweenSessions);
        }

        [Test]
        public void AgentMemoryPolicy_Default_PlainChat_PersistsChatHistory_WithoutMemoryTool()
        {
            AgentMemoryPolicy policy = new();
            AgentMemoryPolicy.RoleMemoryConfig config = policy.GetRoleConfig(BuiltInAgentRoleIds.PlainChat);

            Assert.IsFalse(config.UseMemoryTool, "PlainChat should not expose MemoryTool by default");
            Assert.IsTrue(config.WithChatHistory, "PlainChat should keep conversation context by default");
            Assert.IsTrue(config.PersistChatHistory, "PlainChat should restore session after app restart by default");
        }

        [Test]
        public void AgentMemoryPolicy_Default_SmartChat_UsesMemoryTool_And_PersistsChatHistory()
        {
            AgentMemoryPolicy policy = new();
            AgentMemoryPolicy.RoleMemoryConfig config = policy.GetRoleConfig(BuiltInAgentRoleIds.SmartChat);

            Assert.IsTrue(config.UseMemoryTool, "SmartChat should expose MemoryTool by default");
            Assert.AreEqual(MemoryToolAction.Append, config.DefaultAction);
            Assert.IsTrue(config.WithChatHistory, "SmartChat should keep conversation context by default");
            Assert.IsTrue(config.PersistChatHistory, "SmartChat should restore session after app restart by default");
        }

        [Test]
        public void AgentMemoryPolicy_Default_Creator_UsesMemoryToolAndChatHistory()
        {
            AgentMemoryPolicy policy = new();
            AgentMemoryPolicy.RoleMemoryConfig config = policy.GetRoleConfig(BuiltInAgentRoleIds.Creator);

            Assert.IsTrue(config.UseMemoryTool);
            Assert.IsTrue(config.WithChatHistory);
            Assert.IsFalse(config.PersistChatHistory);
            Assert.AreEqual(30, config.MaxChatHistoryMessages);
        }

        [Test]
        public void RoleMemoryConfig_DefaultContextTokens_ShouldInheritGlobalContextWindow()
        {
            CoreAISettingsOptions settings = new();
            AgentMemoryPolicy.RoleMemoryConfig config =
                new AgentMemoryPolicy.RoleMemoryConfig(true, MemoryToolAction.Append);

            Assert.AreEqual(0, config.ContextTokens);
            Assert.AreEqual(CoreAISettings.DefaultContextWindowTokens, settings.ContextWindowTokens);

            int effectiveContextTokens = config.ContextTokens > 0
                ? config.ContextTokens
                : settings.ContextWindowTokens;
            ContextBudget budget = new DefaultContextBudgetPolicy().Compute(
                new ContextBudgetRequest
                {
                    MaxContextTokens = effectiveContextTokens
                },
                new HeuristicTokenEstimator());

            Assert.AreEqual(CoreAISettings.DefaultContextWindowTokens, budget.MaxContextTokens);
        }

        [Test]
        public void AgentMemoryPolicy_Default_AINpc_HasChatHistoryOn()
        {
            AgentMemoryPolicy policy = new();
            AgentMemoryPolicy.RoleMemoryConfig config = policy.GetRoleConfig(BuiltInAgentRoleIds.AiNpc);

            Assert.IsTrue(config.UseMemoryTool);
            Assert.IsTrue(config.WithChatHistory);
            Assert.IsFalse(config.PersistChatHistory);
            Assert.AreEqual(30, config.MaxChatHistoryMessages);
        }

        [Test]
        public void AgentMemoryPolicy_Default_ToolResultMemory_ByBuiltInRole()
        {
            AgentMemoryPolicy policy = new();

            Assert.AreEqual(ToolResultMemoryPolicy.Full,
                policy.GetRoleConfig(BuiltInAgentRoleIds.Programmer).ToolResultMemory);
            Assert.AreEqual(ToolResultMemoryPolicy.Full,
                policy.GetRoleConfig(BuiltInAgentRoleIds.CoreMechanic).ToolResultMemory);
            Assert.AreEqual(ToolResultMemoryPolicy.CompactSummary,
                policy.GetRoleConfig(BuiltInAgentRoleIds.Merchant).ToolResultMemory);
            Assert.AreEqual(ToolResultMemoryPolicy.CompactSummary,
                policy.GetRoleConfig(BuiltInAgentRoleIds.AiNpc).ToolResultMemory);
            Assert.AreEqual(ToolResultMemoryPolicy.CompactSummary,
                policy.GetRoleConfig(BuiltInAgentRoleIds.Creator).ToolResultMemory);
            Assert.AreEqual(ToolResultMemoryPolicy.CompactSummary,
                policy.GetRoleConfig(BuiltInAgentRoleIds.SmartChat).ToolResultMemory);
        }

        [Test]
        public void WithChatHistory_OnlyPersist_ShouldUseDefaultTokens()
        {
            AgentConfig config = new AgentBuilder("TestAgent")
                .WithSystemPrompt("Test prompt")
                .WithChatHistory(persistBetweenSessions: true)
                .Build();

            Assert.IsTrue(config.WithChatHistory);
            Assert.AreEqual(CoreAISettings.ContextWindowTokens, config.ContextWindowTokens);
            Assert.IsTrue(config.PersistChatHistoryBetweenSessions);
        }

        [Test]
        public void WithToolResultMemoryPolicy_ShouldFlowToPolicy()
        {
            AgentConfig config = new AgentBuilder("ToolMemoryAgent")
                .WithSystemPrompt("Test prompt")
                .WithToolResultMemoryPolicy(ToolResultMemoryPolicy.ErrorsOnly)
                .BuildDetached();
            AgentMemoryPolicy policy = new();
            config.ApplyToPolicy(policy);

            Assert.AreEqual(ToolResultMemoryPolicy.ErrorsOnly, config.ToolResultMemory);
            Assert.AreEqual(ToolResultMemoryPolicy.ErrorsOnly,
                policy.GetRoleConfig("ToolMemoryAgent").ToolResultMemory);
        }

        [Test]
        public void Builder_Chaining_ShouldWorkCorrectly()
        {
            AgentConfig config = new AgentBuilder("Merchant")
                .WithSystemPrompt("You are a merchant")
                .WithTool(new MemoryLlmTool())
                .WithChatHistory(8192, true)
                .WithMemory()
                .WithMode(AgentMode.ToolsAndChat)
                .Build();

            Assert.AreEqual("Merchant", config.RoleId);
            Assert.AreEqual("You are a merchant", config.SystemPrompt);
            Assert.AreEqual(2, config.Tools.Count); // MemoryLlmTool + MemoryLlmTool from WithMemory
            Assert.AreEqual(AgentMode.ToolsAndChat, config.Mode);
            Assert.IsTrue(config.WithChatHistory);
            Assert.AreEqual(8192, config.ContextWindowTokens);
            Assert.IsTrue(config.PersistChatHistoryBetweenSessions);
        }
    }
}
