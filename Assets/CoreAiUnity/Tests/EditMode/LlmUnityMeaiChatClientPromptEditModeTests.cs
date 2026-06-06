#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL && !COREAI_NO_LLM
using System;
using System.Collections.Generic;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;
using MEAI = Microsoft.Extensions.AI;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Verifies LLMUnity prompt preparation parity between streaming and non-streaming paths
    /// through <see cref="LlmUnityMeaiChatClient.BuildLlmUnityPrompt"/>.
    /// </summary>
    [TestFixture]
    public sealed class LlmUnityMeaiChatClientPromptEditModeTests
    {
        [Test]
        public void BuildLlmUnityPrompt_SameInputTwice_YieldsIdenticalSystemAndUser()
        {
            List<MEAI.ChatMessage> messages = SampleMessages();
            MEAI.ChatOptions options = SampleOptions();

            LlmUnityMeaiChatClient.BuildLlmUnityPrompt(messages, options, out string s1, out string u1);
            LlmUnityMeaiChatClient.BuildLlmUnityPrompt(messages, options, out string s2, out string u2);

            Assert.AreEqual(s1, s2);
            Assert.AreEqual(u1, u2);
        }

        [Test]
        public void BuildLlmUnityPrompt_WithTools_ContainsLocalInferenceSectionOnce()
        {
            LlmUnityMeaiChatClient.BuildLlmUnityPrompt(SampleMessages(), SampleOptions(), out string sys, out _);

            Assert.That(sys, Does.Contain("## Local inference (LLMUnity)"));
            Assert.That(sys, Does.Contain("no native API tool channel"));
            int idx = sys.IndexOf("## Local inference (LLMUnity)", StringComparison.Ordinal);
            int idx2 = sys.IndexOf("## Local inference (LLMUnity)", idx + 1, StringComparison.Ordinal);
            Assert.AreEqual(-1, idx2);
        }

        [Test]
        public void BuildLlmUnityPrompt_DoesNotContainLegacyCriticalToolRules()
        {
            LlmUnityMeaiChatClient.BuildLlmUnityPrompt(SampleMessages(), SampleOptions(), out string sys, out _);

            Assert.That(sys, Does.Not.Contain("CRITICAL SYSTEM RULES FOR TOOLS"));
            Assert.That(sys, Does.Not.Contain("ONLY output the JSON block"));
        }

        [Test]
        public void BuildLlmUnityPrompt_IncludesAssistantToolCallAndToolOutputInUserBlob()
        {
            MEAI.FunctionCallContent call = new("c1", "memory",
                new Dictionary<string, object> { ["action"] = "append", ["content"] = "x" });
            List<MEAI.ChatMessage> msgs = new()
            {
                new MEAI.ChatMessage(MEAI.ChatRole.System, "Sys."),
                new MEAI.ChatMessage(MEAI.ChatRole.User, "Hi"),
                new MEAI.ChatMessage(MEAI.ChatRole.Assistant, new List<MEAI.AIContent> { call }),
                new MEAI.ChatMessage(MEAI.ChatRole.Tool,
                    new List<MEAI.AIContent> { new MEAI.FunctionResultContent("c1", "{\"ok\":true}") })
            };

            LlmUnityMeaiChatClient.BuildLlmUnityPrompt(msgs, null, out _, out string user);

            Assert.That(user, Does.Contain("Assistant Tool Call:"));
            Assert.That(user, Does.Contain("\"name\": \"memory\""));
            Assert.That(user, Does.Contain("Tool output:"));
            Assert.That(user, Does.Contain("{\"ok\":true}"));
            Assert.That(user, Does.Not.Contain("[SYSTEM ERROR]"));
        }

        private static List<MEAI.ChatMessage> SampleMessages()
        {
            return new List<MEAI.ChatMessage>
            {
                new(MEAI.ChatRole.System, "Role system."),
                new(MEAI.ChatRole.User, "Hello.")
            };
        }

        private static MEAI.ChatOptions SampleOptions()
        {
            MEAI.AIFunction fn = MEAI.AIFunctionFactory.Create((Func<string>)(() => "x"),
                new MEAI.AIFunctionFactoryOptions { Name = "memory", Description = "Memory tool." });
            return new MEAI.ChatOptions { Tools = new List<MEAI.AITool> { fn } };
        }
    }
}
#endif