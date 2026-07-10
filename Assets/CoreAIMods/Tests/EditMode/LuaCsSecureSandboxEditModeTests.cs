using System.Threading;
using CoreAI.Sandbox.LuaCs;
using Lua;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode coverage for the Lua-CSharp sandbox allocation-bomb backstop (F-08): plain string
    /// concatenation and <c>table.concat</c> have no single library call site to cap the way
    /// <c>string.rep</c>/<c>string.format</c> are capped, so <see cref="LuaCsSecureEnvironment"/> and
    /// <see cref="LuaCsExecutionGuard"/> enforce a total per-execution GC allocation budget instead.
    /// Mirrors the MoonSharp <c>SecureLuaSandboxEditModeTests</c> allocation-bomb fixtures.
    /// </summary>
    [TestFixture]
    public sealed class LuaCsSecureSandboxEditModeTests
    {
        private SynchronizationContext _savedContext;

        /// <summary>
        /// The Lua-CSharp runtime bridges its async VM to a synchronous call site via
        /// <c>state.ExecuteAsync(...).GetAwaiter().GetResult()</c> inside the execution guard. On
        /// Unity's main thread a <see cref="SynchronizationContext"/> is installed, so any continuation
        /// the VM posts back to it would deadlock the blocked main thread. Detaching the context for the
        /// duration of each test lets those continuations complete on the thread pool.
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

        [Test]
        public void AllocationBomb_ConcatDoubling_ThrowsMemoryBudgetError()
        {
            LuaCsSecureEnvironment env = new();
            LuaState state = env.Create();

            // string.rep is capped at MaxStringRepLength (1MB), so the seed string itself is allowed.
            // Doubling it via plain concatenation (no library call site to intercept) must still be
            // caught by the per-instruction GC allocation budget before it reaches hundreds of MB.
            LuaRuntimeException ex = Assert.Throws<LuaRuntimeException>(() =>
                env.RunChunk(state,
                    "local s = string.rep('x', 1000000)\n" +
                    "for i = 1, 30 do s = s .. s end\n" +
                    "return s"));

            Assert.IsTrue(ex.Message.Contains("EXCEEDED_MEMORY_BUDGET"),
                $"Expected the allocation-bomb backstop to fire, got: {ex.Message}");
        }

        [Test]
        public void AllocationBomb_TableConcat_CapEnforced()
        {
            LuaCsSecureEnvironment env = new();
            LuaState state = env.Create();

            LuaRuntimeException ex = Assert.Throws<LuaRuntimeException>(() =>
                env.RunChunk(state,
                    "local t = {}\n" +
                    "local chunk = string.rep('x', 1000000)\n" +
                    "for i = 1, 5 do t[i] = chunk end\n" +
                    "return table.concat(t)"));

            Assert.IsTrue(ex.Message.Contains("table.concat"),
                $"Expected the table.concat cap to fire, got: {ex.Message}");
        }

        [Test]
        public void AllocationBomb_NormalHundredKbString_StillPasses()
        {
            LuaCsSecureEnvironment env = new();
            LuaState state = env.Create();

            LuaValue[] result = env.RunChunk(state,
                "local s = string.rep('x', 100000)\n" +
                "s = s .. s\n" +
                "return #s");

            Assert.AreEqual(200000, (int)result[0].Read<double>(),
                "A normal, non-adversarial 100KB-class string script must not be blocked by the budget.");
        }
    }
}
