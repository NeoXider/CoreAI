using CoreAI;
using CoreAI.Ai;
using CoreAI.Sandbox;
using MoonSharp.Interpreter;
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
            var client = new StubLlmClient();
            var task = client.CompleteAsync(new LlmCompletionRequest { UserPayload = "x" });
            var result = task.GetAwaiter().GetResult();
            Assert.IsTrue(result.Ok);
            StringAssert.Contains("ApplyWaveModifier", result.Content);
            StringAssert.Contains("\"agentRole\":\"Creator\"", result.Content);
        }

        [Test]
        public void StubLlmClient_PlayerChat_IsConversational()
        {
            var client = new StubLlmClient();
            var r = client.CompleteAsync(new LlmCompletionRequest
            {
                AgentRoleId = BuiltInAgentRoleIds.PlayerChat,
                UserPayload = "hello"
            }).GetAwaiter().GetResult();
            Assert.IsTrue(r.Ok);
            StringAssert.Contains("stub", r.Content.ToLowerInvariant());
        }

        [Test]
        public void AiPromptComposer_UsesSystemProviderAndTemplates()
        {
            var sys = new BuiltInDefaultAgentSystemPromptProvider();
            var user = new NoAgentUserPromptTemplateProvider();
            var composer = new AiPromptComposer(sys, user);
            var s = composer.GetSystemPrompt(BuiltInAgentRoleIds.Programmer);
            StringAssert.Contains("Programmer", s);
            var snap = new CoreAI.Session.GameSessionSnapshot();
            snap.Telemetry["wave"] = "2";
            var u = composer.BuildUserPayload(snap, new AiTaskRequest { RoleId = BuiltInAgentRoleIds.Creator, Hint = "h" });
            StringAssert.Contains("\"wave\":\"2\"", u);
            StringAssert.Contains("\"hint\":\"h\"", u);
        }

        [Test]
        public void SecureLuaEnvironment_AllowsWhitelistedApi()
        {
            var env = new SecureLuaEnvironment();
            var reg = new LuaApiRegistry();
            reg.Register("add", new System.Func<double, double, double>((a, b) => a + b));
            var script = env.CreateScript(reg);
            var r = script.DoString("return add(2,3)");
            Assert.AreEqual(5, (int)r.Number);
        }

        [Test]
        public void SecureLuaEnvironment_StripGlobals_RemovesRequire()
        {
            var env = new SecureLuaEnvironment();
            var script = env.CreateScript(new LuaApiRegistry());
            Assert.Throws<ScriptRuntimeException>(() => script.DoString("return require('x')"));
        }
    }
}
