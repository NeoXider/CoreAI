using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Infrastructure.Logging;
using CoreAI.Logging;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// What an <c>execute_lua</c> chunk writes with <c>report()</c> and <c>print()</c> comes back in the tool's result,
    /// as <c>Printed</c> next to <c>Output</c>: capped, the last lines kept, a note when earlier ones were cut, and also
    /// when the chunk fails. The twin: a chunk that writes nothing gives the same result as before.
    /// </summary>
    public sealed class LuaCsExecuteLuaPrintedEditModeTests
    {
        private SynchronizationContext _savedContext;

        [SetUp]
        public void DetachSynchronizationContext()
        {
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void RestoreSynchronizationContext()
        {
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        private sealed class SilentLogger : IGameLogger
        {
            public void LogDebug(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogInfo(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogWarning(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogError(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }
        }

        private static LuaCsModStack BuildStack()
        {
            return LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new SilentLogger(),
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                AutoPersistMods = false
            });
        }

        private static LuaTool.LuaResult Run(string code)
        {
            return BuildStack().ToolExecutor.ExecuteAsync(code, CancellationToken.None).GetAwaiter().GetResult();
        }

        private static string RunThroughTheTool(string code)
        {
            LuaTool tool = new(BuildStack().ToolExecutor, new CoreAISettingsOptions(), NullLog.Instance);
            return tool.ExecuteAsync(code).GetAwaiter().GetResult();
        }

        [Test]
        public void ReportAndPrint_ComeBackNextToTheOutput()
        {
            LuaTool.LuaResult result = Run("report('the tower stands') print('cells', 12) return 7");

            Assert.IsTrue(result.Success, result.Error);
            Assert.AreEqual("7", result.Output, "the return value is still the Output");
            Assert.AreEqual("the tower stands\ncells\t12", result.Printed);
            Assert.AreEqual("{\"Success\":true,\"Output\":\"7\",\"Printed\":\"the tower stands\\ncells\\t12\"}",
                RunThroughTheTool("report('the tower stands') print('cells', 12) return 7"));
        }

        [Test]
        public void ALongOutput_KeepsTheLastLines_AndSaysHowManyWereCut()
        {
            LuaTool.LuaResult result = Run("for i = 1, 200 do report('line ' .. i) end");

            Assert.IsTrue(result.Success, result.Error);
            StringAssert.StartsWith("(", result.Printed);
            StringAssert.Contains(" earlier lines cut)\n", result.Printed);
            StringAssert.EndsWith("line 199\nline 200", result.Printed, "the last lines win");
            int body = result.Printed.Length - result.Printed.IndexOf('\n') - 1;
            Assert.LessOrEqual(body, 600, "about 600 characters of lines at most");
            StringAssert.DoesNotContain("line 1\n", result.Printed);
        }

        [Test]
        public void AFailingChunk_StillBringsBackWhatItWroteBeforeTheError()
        {
            LuaTool.LuaResult result = Run("report('before') error('boom')");

            Assert.IsFalse(result.Success);
            StringAssert.Contains("boom", result.Error);
            Assert.AreEqual("before", result.Printed);
        }

        [Test]
        public void NegativeTwin_AChunkThatWritesNothing_GivesTheSameResultAsBefore()
        {
            LuaTool.LuaResult result = Run("local n = 3 return n + 4");

            Assert.IsTrue(result.Success, result.Error);
            Assert.AreEqual("7", result.Output);
            Assert.IsNull(result.Printed, "nothing written, nothing added");
            Assert.AreEqual("{\"Success\":true,\"Output\":\"7\"}", RunThroughTheTool("local n = 3 return n + 4"));
            Assert.AreEqual("{\"Success\":true}", RunThroughTheTool("local n = 3"));
        }
    }
}
