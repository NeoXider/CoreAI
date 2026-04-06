using System.Collections.Generic;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode тесты для AgentBuilder с новыми функциями (ChatHistory, ContextWindowTokens).
    /// </summary>
    public sealed class AgentBuilderChatHistoryEditModeTests
    {
        [Test]
        public void WithChatHistory_Default_ShouldUseSettingsContext()
        {
            var config = new AgentBuilder("TestAgent")
                .WithSystemPrompt("Test prompt")
                .WithChatHistory()
                .Build();

            Assert.IsTrue(config.WithChatHistory);
            Assert.AreEqual(CoreAI.CoreAISettings.ContextWindowTokens, config.ContextWindowTokens);
        }

        [Test]
        public void WithChatHistory_WithCustomTokens_ShouldOverride()
        {
            var config = new AgentBuilder("TestAgent")
                .WithSystemPrompt("Test prompt")
                .WithChatHistory(4096)
                .Build();

            Assert.IsTrue(config.WithChatHistory);
            Assert.AreEqual(4096, config.ContextWindowTokens);
        }

        [Test]
        public void WithChatHistory_ZeroTokens_ShouldUseZero()
        {
            var config = new AgentBuilder("TestAgent")
                .WithSystemPrompt("Test prompt")
                .WithChatHistory(0)
                .Build();

            Assert.IsTrue(config.WithChatHistory);
            Assert.AreEqual(0, config.ContextWindowTokens);
        }

        [Test]
        public void WithChatHistory_WithPersist_ShouldSetPersistFlag()
        {
            var config = new AgentBuilder("TestAgent")
                .WithSystemPrompt("Test prompt")
                .WithChatHistory(4096, persistBetweenSessions: true)
                .Build();

            Assert.IsTrue(config.WithChatHistory);
            Assert.AreEqual(4096, config.ContextWindowTokens);
            Assert.IsTrue(config.PersistChatHistoryBetweenSessions);
        }

        [Test]
        public void WithoutChatHistory_ShouldHaveDefaults()
        {
            var config = new AgentBuilder("TestAgent")
                .WithSystemPrompt("Test prompt")
                .Build();

            Assert.IsFalse(config.WithChatHistory);
            Assert.IsFalse(config.PersistChatHistoryBetweenSessions);
        }

        [Test]
        public void WithChatHistory_OnlyPersist_ShouldUseDefaultTokens()
        {
            var config = new AgentBuilder("TestAgent")
                .WithSystemPrompt("Test prompt")
                .WithChatHistory(persistBetweenSessions: true)
                .Build();

            Assert.IsTrue(config.WithChatHistory);
            Assert.AreEqual(CoreAI.CoreAISettings.ContextWindowTokens, config.ContextWindowTokens);
            Assert.IsTrue(config.PersistChatHistoryBetweenSessions);
        }

        [Test]
        public void Builder_Chaining_ShouldWorkCorrectly()
        {
            var config = new AgentBuilder("Merchant")
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
