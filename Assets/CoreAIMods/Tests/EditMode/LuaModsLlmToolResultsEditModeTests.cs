using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Composition;
using CoreAI.Logging;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// P16 for <c>manage_mods</c>: a description within the contract limit, the advice that left it arriving with
    /// the failure it prevents, results sized to the tool-result cap with a count of what is not shown, and a note
    /// for arguments the action does not read.
    /// </summary>
    public sealed class LuaModsLlmToolResultsEditModeTests
    {
        private SynchronizationContext _savedContext;

        [SetUp]
        public void SetUp()
        {
            // WHY: the Lua-CSharp runtime completes on the thread pool only without a context.
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void TearDown()
        {
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        /// <summary>A runtime stand-in that answers the calls a test scripts and returns defaults otherwise.</summary>
        public class ScriptedRuntime : DispatchProxy
        {
            public Func<MethodInfo, object[], object> Answer;

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                object answer = Answer?.Invoke(targetMethod, args);
                if (answer != null)
                {
                    return answer;
                }

                Type returns = targetMethod.ReturnType;
                return returns.IsValueType && returns != typeof(void) ? Activator.CreateInstance(returns) : null;
            }
        }

        private static LuaModsLlmTool Tool(ILuaModRuntime runtime, int cap = 8000)
        {
            return new LuaModsLlmTool(runtime,
                new CoreAISettingsOptions { MaxToolResultChars = cap, LogToolCalls = false, LogToolCallResults = false },
                NullLog.Instance, LuaCapabilities.All, true, CoreServicesInstaller.DefaultLocalHostIdentityProvider,
                BuiltInAgentRoleIds.Programmer, null, null);
        }

        private static ILuaModRuntime Scripted(Func<MethodInfo, object[], object> answer)
        {
            ILuaModRuntime runtime = DispatchProxy.Create<ILuaModRuntime, ScriptedRuntime>();
            ((ScriptedRuntime)(object)runtime).Answer = answer;
            return runtime;
        }

        [Test]
        public void Description_FitsTheContractLimit_AndSaysAModComesBackAfterARestart()
        {
            LuaModsLlmTool tool = Tool(new LuaCsModRuntime());

            Assert.LessOrEqual(tool.Description.Length, 500);
            StringAssert.Contains("comes back after a restart", tool.Description);
            StringAssert.Contains("read_skill('Lua Modding')", tool.Description);
        }

        [Test]
        public async Task Load_WithTheHandlerOutsideTheCall_FailsWithTheFix()
        {
            LuaModsLlmTool tool = Tool(new LuaCsModRuntime());

            JObject result = JObject.Parse(await tool.ExecuteAsync("load", "ticker",
                code: "hooks_every(1) function() report('tick') end"));

            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
            StringAssert.Contains("hooks_every(seconds, function() ... end)", result.Value<string>("message"));
        }

        [Test]
        public void SourceHint_NamesDoubleEscapedNewlines_AndStaysSilentOtherwise()
        {
            StringAssert.Contains("escaped twice", LuaModsLlmTool.SourceHint("local a = 1\\nlocal b = 2"));
            Assert.AreEqual("", LuaModsLlmTool.SourceHint("local a = 1\nlocal b = 2\n"));
            Assert.AreEqual("", LuaModsLlmTool.SourceHint(null));
        }

        [Test]
        public async Task Versions_OverTheCap_ShowTheNewestThatFit_AndSaySo()
        {
            List<LuaScriptRevision> history = new();
            for (int i = 0; i < 60; i++)
            {
                history.Add(new LuaScriptRevision(i, new string('x', 300) + i, DateTime.UtcNow.Ticks));
            }

            LuaModsLlmTool tool = Tool(Scripted((method, args) =>
                method.Name == nameof(ILuaModRuntime.ListModVersions) ? history : null), 4000);

            string text = await tool.ExecuteAsync("versions", "arena");
            JObject result = JObject.Parse(text);

            Assert.LessOrEqual(text.Length, 4000);
            JArray shown = (JArray)result["data"];
            Assert.Greater(shown.Count, 0);
            Assert.Less(shown.Count, 60);
            Assert.AreEqual(59, shown[shown.Count - 1].Value<int>("revision"), "the newest revision is kept");
            StringAssert.Contains($"60 revision(s) of mod 'arena', the newest {shown.Count} shown",
                result.Value<string>("message"));
        }

        [Test]
        public async Task ArgumentTheActionDoesNotRead_IsNamedInTheResult()
        {
            LuaModsLlmTool tool = Tool(Scripted((method, args) =>
            {
                if (method.Name != nameof(ILuaModRuntime.TryGetModSource))
                {
                    return null;
                }

                args[2] = "local a = 1\n";
                return true;
            }));

            JObject result = JObject.Parse(await tool.ExecuteAsync("get_source", "arena", folder: "Games"));

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            StringAssert.Contains("get_source does not read folder", result.Value<string>("argument_note"));
            Assert.AreEqual("local a = 1\n", result.Value<string>("data"));
        }

        [Test]
        public void CutAdvice_NamesTheArgumentThatAsksForLess()
        {
            LuaModsLlmTool tool = Tool(new LuaCsModRuntime());

            StringAssert.Contains("line_count", tool.AdviseOnCut("manage_mods",
                new Dictionary<string, object> { ["action"] = "get_source" }));
            Assert.IsNull(tool.AdviseOnCut("manage_mods", new Dictionary<string, object> { ["action"] = "load" }));
        }
    }
}
