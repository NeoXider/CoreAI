using System;
using System.Collections.Generic;
using System.Threading;
using CoreAI.Sandbox.LuaCs;
using CoreAI.Scripting;
using CoreAI.Scripting.LuaCs;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode coverage for the engine abstraction seam (Roblox roadmap MVP1 item 1): the neutral
    /// <see cref="IScriptEngine"/>/<see cref="IScriptFunctionRegistry"/>/<see cref="IValueMarshaller"/>/
    /// <see cref="IScriptExecutionGuard"/> contracts must behave exactly like the concrete Lua-CSharp
    /// classes they wrap, because every consumer above the seam now depends on them alone.
    /// </summary>
    [TestFixture]
    public sealed class ScriptEngineSeamEditModeTests
    {
        private SynchronizationContext _savedContext;

        /// <summary>
        /// The guard bridges the async VM to sync call sites via <c>GetAwaiter().GetResult()</c>; with a
        /// main-thread <see cref="SynchronizationContext"/> installed those continuations would deadlock.
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

        private static LuaCsScriptEngine NewEngine()
        {
            return new LuaCsScriptEngine();
        }

        // ---- Marshaller truth table -------------------------------------------------------------

        [Test]
        public void Marshaller_ScalarRoundTrip_MatchesHistoricalCoercions()
        {
            IValueMarshaller m = NewEngine().Marshaller;

            Assert.IsNull(m.ToHostValue(m.ToScriptArgument(null)), "nil must round-trip to null.");
            Assert.AreEqual(true, m.ToHostValue(m.ToScriptArgument(true)));
            Assert.AreEqual(false, m.ToHostValue(m.ToScriptArgument(false)));
            Assert.AreEqual("text", m.ToHostValue(m.ToScriptArgument("text")));
            Assert.AreEqual(2.5d, m.ToHostValue(m.ToScriptArgument(2.5d)));

            // WHY: Lua-CSharp is double-only; every CLR integer kind must surface as a double.
            Assert.AreEqual(3d, m.ToHostValue(m.ToScriptArgument(3)));
            Assert.AreEqual(3d, m.ToHostValue(m.ToScriptArgument(3L)));
            Assert.AreEqual(1.5d, m.ToHostValue(m.ToScriptArgument(1.5f)));

            Assert.AreEqual(ScriptValueKind.Nil, m.GetKind(m.ToScriptArgument(null)));
            Assert.AreEqual(ScriptValueKind.Boolean, m.GetKind(m.ToScriptArgument(true)));
            Assert.AreEqual(ScriptValueKind.Number, m.GetKind(m.ToScriptArgument(7)));
            Assert.AreEqual(ScriptValueKind.String, m.GetKind(m.ToScriptArgument("s")));

            Assert.AreEqual("nil", m.Describe(m.ToScriptArgument(null)));
        }

        [Test]
        public void Marshaller_DictionariesAndLists_BecomeScriptTables()
        {
            IValueMarshaller m = NewEngine().Marshaller;

            object dict = m.ToScriptValue(new Dictionary<string, object>
            {
                { "a", 1 },
                { "b", "x" }
            });
            Assert.AreEqual(ScriptValueKind.Table, m.GetKind(dict),
                "A CLR dictionary must convert to a script table.");

            object list = m.ToScriptValue(new List<object> { 1d, 2d, 3d });
            Assert.AreEqual(ScriptValueKind.Table, m.GetKind(list),
                "A CLR list must convert to a 1-based array table.");
        }

        [Test]
        public void Marshaller_PortableRoundTrip_PreservesTableContent()
        {
            LuaCsScriptEngine engine = NewEngine();
            IValueMarshaller m = engine.Marshaller;
            IScriptState state = engine.CreateState();

            object[] results = engine.RunChunk(state, "return { a = 1, b = 'x', c = { 2, true } }");
            Assert.AreEqual(1, results.Length);

            object portable = m.ToPortable(results[0], 4);
            object rebuilt = m.FromPortable(portable);
            Assert.AreEqual(ScriptValueKind.Table, m.GetKind(rebuilt),
                "FromPortable must rebuild a table for a table portable.");

            // WHY: A second ToPortable pass over the rebuilt value proves the deep copy is lossless for
            // the portable subset (nil/boolean/number/string/table).
            List<KeyValuePair<object, object>> pairs = (List<KeyValuePair<object, object>>)m.ToPortable(rebuilt, 4);
            Assert.AreEqual(1d, Find(pairs, "a"));
            Assert.AreEqual("x", Find(pairs, "b"));
            List<KeyValuePair<object, object>> nested = (List<KeyValuePair<object, object>>)Find(pairs, "c");
            Assert.AreEqual(2d, Find(nested, 1d));
            Assert.AreEqual(true, Find(nested, 2d));
        }

        [Test]
        public void Marshaller_PortableDepthCap_Throws()
        {
            LuaCsScriptEngine engine = NewEngine();
            IScriptState state = engine.CreateState();
            object[] results = engine.RunChunk(state, "return { l1 = { l2 = { l3 = { 1 } } } }");

            ArgumentException ex = Assert.Throws<ArgumentException>(() => engine.Marshaller.ToPortable(results[0], 2));
            StringAssert.Contains("nest at most 2 levels", ex.Message);
        }

        [Test]
        public void Marshaller_PortableRejectsFunctions()
        {
            LuaCsScriptEngine engine = NewEngine();
            IScriptState state = engine.CreateState();
            object[] results = engine.RunChunk(state, "return function() end");

            Assert.Throws<ArgumentException>(() => engine.Marshaller.ToPortable(results[0], 4),
                "Functions must never cross the portable boundary.");
        }

        // ---- Function registry dispatch ---------------------------------------------------------

        [Test]
        public void Registry_TypedDelegates_DispatchWithHistoricalCoercion()
        {
            LuaCsScriptEngine engine = NewEngine();
            IScriptFunctionRegistry registry = engine.CreateFunctionRegistry();
            registry.Register("add", new Func<double, double, double>((a, b) => a + b));
            registry.Register("echo_int", new Func<int, double>(i => i));

            Assert.IsTrue(registry.Contains("add"));
            Assert.IsFalse(registry.Contains("missing"));

            IScriptState state = engine.CreateState();
            registry.ApplyTo(state);

            // WHY: int parameters historically round via Convert.ToInt32 (4.6 -> 5), part of the locked
            // coercion behavior.
            object[] results = engine.RunChunk(state, "return add(2, 3) + echo_int(4.6)");
            Assert.AreEqual(10d, engine.Marshaller.ToHostValue(results[0]));
        }

        [Test]
        public void Registry_VarArgs_RawArgumentsTypedAccessorsAndMultipleReturns()
        {
            LuaCsScriptEngine engine = NewEngine();
            IScriptFunctionRegistry registry = engine.CreateFunctionRegistry();

            registry.RegisterVarArgs("sum_all", call =>
            {
                double sum = 0;
                for (int i = 0; i < call.ArgumentCount; i++)
                {
                    sum += call.GetNumber(i);
                }

                return ScriptCallResult.Return(sum);
            });

            registry.RegisterVarArgs("two", _ => ScriptCallResult.Return(1d, "x"));

            registry.RegisterVarArgs("probe", call => ScriptCallResult.Return(
                call.GetString(0) == null &&
                call.GetKind(1) == ScriptValueKind.Function &&
                call.GetKind(2) == ScriptValueKind.Table &&
                call.GetTable(2)["k"] is double d && d == 5d));

            IScriptState state = engine.CreateState();
            registry.ApplyTo(state);

            object[] sum = engine.RunChunk(state, "return sum_all(1, 2, 3)");
            Assert.AreEqual(6d, engine.Marshaller.ToHostValue(sum[0]));

            object[] multi = engine.RunChunk(state, "local a, b = two(); return a == 1 and b == 'x'");
            Assert.AreEqual(true, engine.Marshaller.ToHostValue(multi[0]));

            object[] probe = engine.RunChunk(state, "return probe(nil, function() end, { k = 5 })");
            Assert.AreEqual(true, engine.Marshaller.ToHostValue(probe[0]),
                "Var-args accessors must expose nil-as-null, function kinds and neutral table views.");
        }

        [Test]
        public void Registry_ApplyTo_RejectsForeignStates()
        {
            LuaCsScriptEngine engine = NewEngine();
            IScriptFunctionRegistry registry = engine.CreateFunctionRegistry();

            Assert.Throws<ScriptRuntimeException>(() => registry.ApplyTo(new ForeignState()),
                "A state not created by the Lua-CSharp engine must be rejected, not misused.");
        }

        private sealed class ForeignState : IScriptState
        {
            public void Dispose()
            {
            }
        }

        // ---- Execution guard through the seam ---------------------------------------------------

        [Test]
        public void Guard_InvokesFunctionsWithMarshalledArguments()
        {
            LuaCsScriptEngine engine = NewEngine();
            IScriptState state = engine.CreateState();
            object[] fn = engine.RunChunk(state, "return function(a, b) return a + b end");

            IScriptExecutionGuard guard = engine.CreateGuard(new ExecutionBudget(2000, 10_000));
            object[] results = guard.Invoke(state, fn[0], 2, 3.5d);

            Assert.AreEqual(5.5d, engine.Marshaller.ToHostValue(results[0]));
        }

        [Test]
        [Timeout(15000)]
        public void Guard_StepBudget_CutsRunawayFunction()
        {
            LuaCsScriptEngine engine = NewEngine();
            IScriptState state = engine.CreateState();
            object[] fn = engine.RunChunk(state,
                "return function() local x = 0; for i = 1, 1000000 do x = x + 1 end; return x end");

            IScriptExecutionGuard guard = engine.CreateGuard(new ExecutionBudget(5000, 5_000));
            Exception ex = Assert.Catch<Exception>(() => guard.Invoke(state, fn[0]),
                "An over-budget function must be cut by the seam guard.");
            StringAssert.Contains("EXCEEDED_HARD_LIMIT_STEPS", ex.Message);
        }

        [Test]
        public void Guard_RejectsNonCallableValues()
        {
            LuaCsScriptEngine engine = NewEngine();
            IScriptState state = engine.CreateState();
            IScriptExecutionGuard guard = engine.CreateGuard();

            Assert.Throws<ScriptRuntimeException>(() => guard.Invoke(state, "not a function"));
        }

        [Test]
        public void MemoryBudgetTrip_NeutralClassification_ByTypeOnly()
        {
            Exception trip = new InvalidOperationException("wrapped",
                new LuaMemoryBudgetException("LuaCsSecureEnvironment: EXCEEDED_MEMORY_BUDGET (1 bytes)"));
            Assert.IsTrue(ScriptExecutionErrors.IsMemoryBudgetTrip(trip),
                "The engine's memory-budget exception must classify through the neutral helper.");

            Assert.IsFalse(ScriptExecutionErrors.IsMemoryBudgetTrip(
                    new InvalidOperationException("boom EXCEEDED_MEMORY_BUDGET forged by a mod")),
                "A forged marker string must NOT classify as a memory trip.");
            Assert.IsFalse(ScriptExecutionErrors.IsMemoryBudgetTrip(null));
        }

        // ---- Engine facade ----------------------------------------------------------------------

        [Test]
        public void Engine_IdentityAndChunkExecution()
        {
            LuaCsScriptEngine engine = NewEngine();
            Assert.AreEqual("Lua-CSharp", engine.EngineName);
            Assert.IsNotEmpty(engine.EngineVersion);

            IScriptState state = engine.CreateState(ScriptSandboxProfile.Default);
            object[] results = engine.RunChunk(state, "return 40 + 2");
            Assert.AreEqual(42d, engine.Marshaller.ToHostValue(results[0]));

            object[] stripped = engine.RunChunk(state, "return os == nil and io == nil and load == nil");
            Assert.AreEqual(true, engine.Marshaller.ToHostValue(stripped[0]),
                "Seam-created states must carry the full sandbox hardening.");
        }

        [Test]
        public void Engine_Coroutine_ResumesAndFinishesThroughSeam()
        {
            LuaCsScriptEngine engine = NewEngine();
            IScriptState state = engine.CreateState();
            object[] fn = engine.RunChunk(state,
                "return function() coroutine.yield(1); coroutine.yield(2); return 3 end");

            IScriptCoroutine co = engine.CreateCoroutine(state, fn[0]);
            Assert.IsTrue(co.CanResume);

            ScriptResumeResult first = co.Resume();
            Assert.IsTrue(first.Ok);
            Assert.AreEqual(1d, engine.Marshaller.ToHostValue(first.Values[0]));

            ScriptResumeResult second = co.Resume();
            Assert.AreEqual(2d, engine.Marshaller.ToHostValue(second.Values[0]));

            ScriptResumeResult last = co.Resume();
            Assert.IsTrue(last.Ok);
            Assert.AreEqual(3d, engine.Marshaller.ToHostValue(last.Values[0]));
            Assert.IsTrue(co.IsFinished);
        }

        private static object Find(List<KeyValuePair<object, object>> pairs, object key)
        {
            foreach (KeyValuePair<object, object> pair in pairs)
            {
                if (Equals(pair.Key, key))
                {
                    return pair.Value;
                }
            }

            Assert.Fail($"Key '{key}' not found in portable table.");
            return null;
        }
    }
}
