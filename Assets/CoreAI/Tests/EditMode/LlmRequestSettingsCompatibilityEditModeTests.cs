using System;
using System.Reflection;
using CoreAI.Ai;
using CoreAI.Messaging;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode
{
    public sealed class LlmRequestSettingsCompatibilityEditModeTests
    {
        [Test]
        public void SettingsOptions_From_PreservesIndependentPrimaryAndSecondaryReasoning()
        {
            CoreAISettingsOptions source = new CoreAISettingsOptions
            {
                ReasoningEffort = LlmReasoningEffort.High,
                ReasoningDialect = LlmReasoningDialect.OpenAi,
                SecondaryReasoningEffort = LlmReasoningEffort.Minimal,
                SecondaryReasoningDialect = LlmReasoningDialect.QwenTemplate
            };

            CoreAISettingsOptions copy = CoreAISettingsOptions.From(source);

            Assert.AreEqual(LlmReasoningEffort.High, copy.ReasoningEffort);
            Assert.AreEqual(LlmReasoningDialect.OpenAi, copy.ReasoningDialect);
            Assert.AreEqual(LlmReasoningEffort.Minimal, copy.SecondaryReasoningEffort);
            Assert.AreEqual(LlmReasoningDialect.QwenTemplate, copy.SecondaryReasoningDialect);

        }

        [Test]
        public void RoleMemoryConfig_LegacyConstructor_PreservesSignatureAndValues()
        {
            ConstructorInfo constructor = RequireConstructor(typeof(AgentMemoryPolicy.RoleMemoryConfig),
                typeof(bool), typeof(MemoryToolAction), typeof(bool), typeof(bool), typeof(int),
                typeof(bool?), typeof(int), typeof(int?), typeof(bool), typeof(float?),
                typeof(ToolResultMemoryPolicy), typeof(float?));
            AgentMemoryPolicy.RoleMemoryConfig config = (AgentMemoryPolicy.RoleMemoryConfig)constructor.Invoke(
                new object[] { false, MemoryToolAction.Write, false, true, 8192, true, 12, 700,
                    false, 0.3f, ToolResultMemoryPolicy.Full, 0.6f });

            Assert.IsFalse(config.UseMemoryTool);
            Assert.AreEqual(MemoryToolAction.Write, config.DefaultAction);
            Assert.IsFalse(config.WithChatHistory);
            Assert.IsTrue(config.PersistChatHistory);
            Assert.AreEqual(8192, config.ContextTokens);
            Assert.AreEqual(true, config.AllowDuplicateToolCalls);
            Assert.AreEqual(12, config.MaxChatHistoryMessages);
            Assert.AreEqual(700, config.MaxOutputTokens);
            Assert.IsFalse(config.UseLlmContextCompaction);
            Assert.AreEqual(0.3f, config.Temperature);
            Assert.AreEqual(ToolResultMemoryPolicy.Full, config.ToolResultMemory);
            Assert.AreEqual(0.6f, config.CompactionTriggerRatio);
            Assert.IsNull(config.MaxToolCallRoundtrips);
            Assert.IsNull(config.Reasoning);
            Assert.IsNull(config.Sampling);

        }

        [Test]
        public void RoleMemoryConfig_ExtendedConstructor_KeepsReasoningAndSampling()
        {
            LlmSamplingOptions sampling = new LlmSamplingOptions { TopP = 0.8f, Seed = 41 };
            AgentMemoryPolicy.RoleMemoryConfig config = new AgentMemoryPolicy.RoleMemoryConfig(
                temperature: 0.4f, maxOutputTokens: 321,
                reasoning: new LlmReasoningOptions(LlmReasoningEffort.High, 4096), sampling: sampling);

            Assert.AreEqual(0.4f, config.Temperature);
            Assert.AreEqual(321, config.MaxOutputTokens);
            Assert.AreEqual(LlmReasoningEffort.High, config.Reasoning.Value.Effort);
            Assert.AreEqual(4096, config.Reasoning.Value.BudgetTokens);
            Assert.AreSame(sampling, config.Sampling);

        }

        [Test]
        public void LlmRequestStarted_LegacyConstructor_PreservesSignatureAndValues()
        {
            ConstructorInfo constructor = RequireConstructor(typeof(LlmRequestStarted), typeof(string),
                typeof(string), typeof(string), typeof(LlmExecutionMode), typeof(bool), typeof(string));
            LlmRequestStarted message = (LlmRequestStarted)constructor.Invoke(new object[]
                { "trace", "role", "profile", LlmExecutionMode.ClientOwnedApi, true, "actor" });

            Assert.AreEqual("trace", message.TraceId);
            Assert.AreEqual("role", message.RoleId);
            Assert.AreEqual("profile", message.RoutingProfileId);
            Assert.AreEqual(LlmExecutionMode.ClientOwnedApi, message.ExecutionMode);
            Assert.IsTrue(message.Streaming);
            Assert.AreEqual("actor", message.ActorId);
            Assert.AreEqual(LlmReasoningEffort.Default, message.ReasoningEffort);
            Assert.AreEqual(LlmReasoningDialect.Auto, message.ReasoningDialect);
            Assert.IsNull(message.Temperature);
            Assert.IsNull(message.MaxOutputTokens);

        }

        [Test]
        public void LlmRequestStarted_ExtendedConstructor_KeepsEffectiveSettings()
        {
            LlmRequestStarted message = new LlmRequestStarted("trace", "role", "profile",
                LlmExecutionMode.ClientOwnedApi, false, "actor", LlmReasoningEffort.High,
                LlmReasoningDialect.OpenAi, 0.2f, 512);

            Assert.AreEqual("actor", message.ActorId);
            Assert.IsFalse(message.Streaming);
            Assert.AreEqual(LlmReasoningEffort.High, message.ReasoningEffort);
            Assert.AreEqual(LlmReasoningDialect.OpenAi, message.ReasoningDialect);
            Assert.AreEqual(0.2f, message.Temperature);
            Assert.AreEqual(512, message.MaxOutputTokens);

        }

        [Test]
        public void LlmUsageReported_LegacyConstructor_PreservesSignatureAndValues()
        {
            ConstructorInfo constructor = RequireConstructor(typeof(LlmUsageReported), typeof(string),
                typeof(string), typeof(string), typeof(LlmExecutionMode), typeof(string), typeof(int?),
                typeof(int?), typeof(int?), typeof(bool), typeof(bool), typeof(int?), typeof(int?));
            LlmUsageReported message = (LlmUsageReported)constructor.Invoke(new object[]
                { "trace", "role", "profile", LlmExecutionMode.ServerManagedApi, "model", 80, 20, 100,
                    true, false, 40, 10 });

            Assert.AreEqual("trace", message.TraceId);
            Assert.AreEqual("role", message.RoleId);
            Assert.AreEqual("profile", message.RoutingProfileId);
            Assert.AreEqual(LlmExecutionMode.ServerManagedApi, message.ExecutionMode);
            Assert.AreEqual("model", message.Model);
            Assert.AreEqual(80, message.PromptTokens);
            Assert.AreEqual(20, message.CompletionTokens);
            Assert.AreEqual(100, message.TotalTokens);
            Assert.IsTrue(message.Streaming);
            Assert.IsFalse(message.Success);
            Assert.AreEqual(40, message.CacheReadTokens);
            Assert.AreEqual(10, message.CacheWriteTokens);
            Assert.IsNull(message.ReasoningTokens);

        }

        [Test]
        public void LlmUsageReported_ExtendedConstructor_KeepsReasoningUsageSeparate()
        {
            LlmUsageReported message = new LlmUsageReported("trace", "role", "profile",
                LlmExecutionMode.ClientOwnedApi, "model", 80, 20, 100, false, true,
                cacheReadTokens: 30, cacheWriteTokens: 10, reasoningTokens: 7);

            Assert.AreEqual(20, message.CompletionTokens);
            Assert.AreEqual(100, message.TotalTokens);
            Assert.AreEqual(30, message.CacheReadTokens);
            Assert.AreEqual(10, message.CacheWriteTokens);
            Assert.AreEqual(7, message.ReasoningTokens);
            Assert.IsTrue(message.Success);

        }

        private static ConstructorInfo RequireConstructor(Type type, params Type[] signature)
        {
            ConstructorInfo constructor = type.GetConstructor(signature);
            Assert.IsNotNull(constructor, type.FullName + " must retain its original CLR constructor signature.");
            return constructor;
        }
    }
}
