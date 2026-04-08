using System.Threading.Tasks;
using CoreAI;
using CoreAI.Ai;
using CoreAI.Sandbox;
using CoreAI.Session;
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
            StubLlmClient client = new();
            Task<LlmCompletionResult> task = client.CompleteAsync(new LlmCompletionRequest { UserPayload = "x" });
            LlmCompletionResult result = task.GetAwaiter().GetResult();
            Assert.IsTrue(result.Ok);
            StringAssert.Contains("ApplyWaveModifier", result.Content);
            StringAssert.Contains("\"agentRole\":\"Creator\"", result.Content);
        }

        [Test]
        public void StubLlmClient_PlayerChat_IsConversational()
        {
            StubLlmClient client = new();
            LlmCompletionResult r = client.CompleteAsync(new LlmCompletionRequest
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

        [Test]
        public void SecureLuaEnvironment_AllowsWhitelistedApi()
        {
            SecureLuaEnvironment env = new();
            LuaApiRegistry reg = new();
            reg.Register("add", new System.Func<double, double, double>((a, b) => a + b));
            Script script = env.CreateScript(reg);
            DynValue r = script.DoString("return add(2,3)");
            Assert.AreEqual(5, (int)r.Number);
        }

        [Test]
        public void SecureLuaEnvironment_StripGlobals_RemovesRequire()
        {
            SecureLuaEnvironment env = new();
            Script script = env.CreateScript(new LuaApiRegistry());
            Assert.Throws<ScriptRuntimeException>(() => script.DoString("return require('x')"));
        }

        [Test]
        // Теперь наш SecureLuaEnvironment имеет пуленепробиваемую защиту через debug.sethook
        // Поэтому этот тест больше не будет вешать саму Unity
        public void SecureLuaEnvironment_InfiniteLoop_IsInterrupted()
        {
            SecureLuaEnvironment env = new();
            Script script = env.CreateScript(new LuaApiRegistry());
            
            var ex = Assert.Throws<ScriptRuntimeException>(() => 
                env.RunChunk(script, "while true do end")
            );
            
            StringAssert.Contains("EXCEEDED_HARD_LIMIT_STEPS", ex.Message);
        }
    }
}