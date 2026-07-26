using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Infrastructure.Logging;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Proves a Lua table returned from <c>execute_lua</c> is summarized as JSON instead of the
    /// opaque VM handle ("table: 0x..."), end to end through the same
    /// <see cref="LuaCsModRuntimeFactory"/> stack the DI scope wires.
    /// </summary>
    public sealed class LuaCsTableSummaryEditModeTests
    {
        private SynchronizationContext _savedContext;

        /// <summary>
        /// The Lua-CSharp runtime bridges its async VM to a synchronous call site via
        /// <c>GetAwaiter().GetResult()</c>; detaching Unity's main-thread SynchronizationContext lets VM
        /// continuations complete on the thread pool instead of deadlocking the blocked test thread.
        /// </summary>
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

        private sealed class FakeGameLogger : IGameLogger
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
                Logger = new FakeGameLogger(),
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All
            });
        }

        [Test]
        public void LuaCs_OneOff_ReturnedArrayTable_SummarizesAsJsonNotHandle()
        {
            LuaCsModStack stack = BuildStack();

            LuaTool.LuaResult result = stack.ToolExecutor
                .ExecuteAsync("return {1, 2, 3}", CancellationToken.None)
                .GetAwaiter().GetResult();

            Assert.IsTrue(result.Success, result.Error);
            StringAssert.DoesNotStartWith("table:", result.Output,
                "A returned table must not surface as the VM handle address.");
            StringAssert.StartsWith("[", result.Output,
                "An array-shaped table must be rendered as a JSON array.");
            StringAssert.Contains("2", result.Output,
                "The JSON summary must carry the actual element values.");
        }

        [Test]
        public void LuaCs_OneOff_ReturnedMapTable_SummarizesAsJsonWithKeys()
        {
            LuaCsModStack stack = BuildStack();

            LuaTool.LuaResult result = stack.ToolExecutor
                .ExecuteAsync("return {a = 1}", CancellationToken.None)
                .GetAwaiter().GetResult();

            Assert.IsTrue(result.Success, result.Error);
            StringAssert.DoesNotStartWith("table:", result.Output,
                "A returned table must not surface as the VM handle address.");
            StringAssert.Contains("\"a\"", result.Output,
                "A map-shaped table must be rendered as JSON carrying its string keys.");
        }
    }
}
