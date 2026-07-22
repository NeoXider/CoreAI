using System.Collections.Generic;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Infrastructure.Logging;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Proves the Luau downleveler is wired into the ACTUAL runtime compile paths, not just callable in
    /// isolation: mods and <c>execute_lua</c> now accept Luau-only syntax because
    /// <see cref="LuauSourceGate"/> runs BEFORE the Lua 5.2 VM compiles the chunk. Exercised end to end
    /// through <see cref="LuaCsModRuntimeFactory"/> exactly as the DI scope wires it. The test assembly
    /// does not reference the Lua-CSharp package, so genuine VM parse failures are caught via
    /// <see cref="Assert.Catch(TestDelegate)"/> rather than by exception type.
    /// </summary>
    public sealed class LuauDownlevelWiringEditModeTests
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

        private sealed class MemoryStore : ILuaModStore
        {
            private readonly Dictionary<(string, string), string> _values = new();

            public string Get(string modId, string key)
            {
                return _values.TryGetValue((modId, key), out string value) ? value : "";
            }

            public void Set(string modId, string key, string value)
            {
                if (value == null)
                {
                    _values.Remove((modId, key));
                    return;
                }

                _values[(modId, key)] = value;
            }

            public void Clear(string modId)
            {
            }
        }

        private sealed class FakeGameLogger : IGameLogger
        {
            public void LogDebug(GameLogFeature feature, string message, UnityEngine.Object context = null) { }
            public void LogInfo(GameLogFeature feature, string message, UnityEngine.Object context = null) { }
            public void LogWarning(GameLogFeature feature, string message, UnityEngine.Object context = null) { }
            public void LogError(GameLogFeature feature, string message, UnityEngine.Object context = null) { }
        }

        private static LuaCsModStack BuildStack(ILuaModStore store = null)
        {
            return LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new FakeGameLogger(),
                ModStore = store,
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All
            });
        }

        [Test]
        public void LuaCs_Mod_LuauCompoundAssignContinueInterpolation_RunsThroughRuntime()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(store);

            // +=, continue in a numeric for, and backtick string interpolation — none of which the
            // bundled Lua 5.2 VM parses raw. The downlevel gate makes the mod load and run.
            stack.Runtime.LoadMod("m", string.Join("\n", new[]
            {
                "local total = 0",
                "for i = 1, 5 do",
                "\tif i % 2 == 0 then continue end",
                "\ttotal += i",
                "end",
                "store_set('sum', tostring(total))",
                "local who = \"world\"",
                "store_set('greeting', `hello {who} {total}`)"
            }));

            Assert.IsTrue(stack.Runtime.IsLoaded("m"), "The Luau mod must load, not fail at compile.");
            Assert.AreEqual("9", store.Get("m", "sum"),
                "Compound assignment + continue must compute 1+3+5 = 9.");
            Assert.AreEqual("hello world 9", store.Get("m", "greeting"),
                "Backtick interpolation must format through tostring.");
            Assert.IsEmpty(stack.Runtime.GetRecentHandlerErrors("m"),
                "The downleveled load chunk must not raise any error.");
        }

        [Test]
        public void LuaCs_Mod_LuauTypeAnnotatedLocal_RunsThroughRuntime()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(store);

            stack.Runtime.LoadMod("t", string.Join("\n", new[]
            {
                "local n: number = 20",
                "local label: string = \"answer\"",
                "local doubled = (n :: number) * 2 + 2",
                "store_set('v', label .. tostring(doubled))"
            }));

            Assert.IsTrue(stack.Runtime.IsLoaded("t"), "A type-annotated Luau mod must load.");
            Assert.AreEqual("answer42", store.Get("t", "v"),
                "Type annotations and casts must be stripped and the arithmetic run.");
        }

        [Test]
        public void LuaCs_Mod_GenuineSyntaxError_SurfacesLoudly_NotSilentRawFallback()
        {
            LuaCsModStack stack = BuildStack(new MemoryStore());

            // Genuine broken syntax (dangling operator): it is not lowerable and must fail the load
            // with a non-empty message rather than silently compiling raw and swallowing the problem.
            System.Exception ex = Assert.Catch(() => stack.Runtime.LoadMod("bad", "local x = 1 +"));
            Assert.IsNotNull(ex, "A genuine syntax error must throw out of LoadMod.");
            Assert.IsFalse(string.IsNullOrWhiteSpace(ex.Message),
                "The surfaced error must carry a sensible, non-empty message.");
            Assert.IsFalse(stack.Runtime.IsLoaded("bad"), "A failed load must leave nothing registered.");
        }

        [Test]
        public void LuaCs_Mod_PlainLua_IsUnaffected_SourceRoundTripsExactly()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(store);

            const string plain =
                "local t = {}\nfunction t.add(n) return 1 + n end\nstore_set('r', tostring(t.add(2)))";
            stack.Runtime.LoadMod("p", plain);

            Assert.IsTrue(stack.Runtime.IsLoaded("p"));
            Assert.AreEqual("3", store.Get("p", "r"), "Plain Lua must run exactly as before.");
            Assert.IsTrue(stack.Runtime.TryGetModSource("p", out string stored));
            Assert.AreEqual(plain, stored,
                "The stored source must be the author's ORIGINAL text, byte-for-byte (get_source round-trip).");
        }

        [Test]
        public void LuaCs_OneOff_LuauSyntax_ExecutesThroughToolExecutor()
        {
            LuaCsModStack stack = BuildStack();

            LuaTool.LuaResult result = stack.ToolExecutor
                .ExecuteAsync("local a = 1 a += 4 local s = `v={a}` return s", CancellationToken.None)
                .GetAwaiter().GetResult();

            Assert.IsTrue(result.Success, result.Error);
            Assert.AreEqual("v=5", result.Output,
                "execute_lua must accept compound assignment and interpolation.");
        }

        [Test]
        public void LuaCs_OneOff_GenuineSyntaxError_ReportsFailureWithMessage()
        {
            LuaCsModStack stack = BuildStack();

            LuaTool.LuaResult result = stack.ToolExecutor
                .ExecuteAsync("return 1 +", CancellationToken.None)
                .GetAwaiter().GetResult();

            Assert.IsFalse(result.Success, "A genuine syntax error must not report success.");
            Assert.IsFalse(string.IsNullOrEmpty(result.Error), "The failure must carry a message.");
        }
    }
}
