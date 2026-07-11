using System.Threading.Tasks;
using CoreAI;
using CoreAI.Ai;
using CoreAI.Session;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    public sealed class CoreAssemblyEditModeTests
    {
        [Test]
        public void CoreAssemblyMarker_IsDefined()
        {
            Assert.AreEqual("CoreAI.Core", CoreAssemblyMarker.AssemblyName);
        }

        [Test]
        public void StubLlmClient_ReturnsJson()
        {
            StubLlmClient client = new();
            Task<LlmCompletionResult> task = client.CompleteAsync(new LlmCompletionRequest { UserPayload = "x" });
            LlmCompletionResult result = task.GetAwaiter().GetResult();
            Assert.IsTrue(result.Ok);
            StringAssert.Contains("ApplyWaveModifier", result.Content);
            StringAssert.Contains("\"agentRole\":\"Creator\"", result.Content);
        }

        [Test]
        public void StubLlmClient_SmartChat_IsConversational()
        {
            StubLlmClient client = new();
            LlmCompletionResult r = client.CompleteAsync(new LlmCompletionRequest
            {
                AgentRoleId = BuiltInAgentRoleIds.SmartChat,
                UserPayload = "hello"
            }).GetAwaiter().GetResult();
            Assert.IsTrue(r.Ok);
            StringAssert.Contains("stub", r.Content.ToLowerInvariant());
        }

        [Test]
        public void StubLlmClient_Teacher_IsShortOfflineMessage()
        {
            StubLlmClient client = new();
            LlmCompletionResult r = client.CompleteAsync(new LlmCompletionRequest
            {
                AgentRoleId = "Teacher",
                UserPayload = "{\"telemetry\":{},\"hint\":\"test\"}"
            }).GetAwaiter().GetResult();

            Assert.IsTrue(r.Ok);
            StringAssert.StartsWith("[stub]", r.Content);
            StringAssert.DoesNotContain("telemetry", r.Content);
        }

        [Test]
        public void AiPromptComposer_UsesSystemProviderAndTemplates()
        {
            BuiltInDefaultAgentSystemPromptProvider sys = new();
            NoAgentUserPromptTemplateProvider user = new();
            AiPromptComposer composer = new(sys, user, new NullLuaScriptVersionStore());
            string s = composer.GetSystemPrompt(BuiltInAgentRoleIds.Programmer);
            StringAssert.Contains("Programmer", s);
            GameSessionSnapshot snap = new();
            snap.Telemetry["wave"] = "2";
            string u = composer.BuildUserPayload(snap,
                new AiTaskRequest { RoleId = BuiltInAgentRoleIds.Creator, Hint = "h" });
            StringAssert.Contains("\"wave\":\"2\"", u);
            StringAssert.Contains("\"hint\":\"h\"", u);
        }
    }
}
