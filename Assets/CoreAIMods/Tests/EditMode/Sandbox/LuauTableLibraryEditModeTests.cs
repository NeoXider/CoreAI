#if COREAI_LUA
using System.Threading;
using CoreAI.Sandbox.LuaCs;
using Lua;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.Sandbox
{
    /// <summary>
    /// The Luau <c>table</c> library of MVP13 (RT5) against the mirror's <c>table</c> page and Luau's own rules:
    /// <c>find</c>, <c>clear</c>, <c>create</c>, <c>clone</c>, <c>freeze</c>, <c>isfrozen</c>, <c>move</c>,
    /// <c>maxn</c>, the deprecated <c>getn</c>/<c>foreach</c>/<c>foreachi</c> and <c>unpack</c>, through the
    /// sandbox every mod and <c>execute_lua</c> chunk runs in. Each behaviour has its negative twin, and the loops
    /// are pinned to cost what the Lua loop they replace costs under the guard.
    /// </summary>
    [TestFixture]
    public sealed class LuauTableLibraryEditModeTests
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

        private static LuaValue[] Run(string chunk)
        {
            LuaCsSecureEnvironment environment = new();
            LuaState state = environment.Create();
            return environment.RunChunk(state, chunk);
        }

        /// <summary>Every result of <paramref name="chunk"/> as tostring gives it, joined by '|'.</summary>
        private static string Text(string chunk)
        {
            LuaValue[] results = Run("local r = table.pack(" + chunk + ") for i = 1, r.n do r[i] = tostring(r[i]) end "
                                     + "return table.concat(r, '|', 1, r.n)");
            return results[0].Read<string>();
        }

        private static string Error(string call)
        {
            LuaValue[] results = Run("local ok, err = pcall(function() return " + call + " end) "
                                     + "return tostring(ok) .. '|' .. tostring(err)");
            return results[0].Read<string>();
        }

        private sealed class StepRecorder : ILuaCsGuardObserver
        {
            public long Steps;

            public void OnGuardedExecutionCompleted(in LuaCsGuardExecutionRecord record)
            {
                Steps = record.Steps;
            }
        }

        private static long StepsOf(LuaCsSecureEnvironment environment, LuaState state, string chunk)
        {
            StepRecorder recorder = new();
            LuaCsExecutionGuard guard = new(timeoutMs: 60_000, maxSteps: 100_000_000, guardObserver: recorder);
            environment.RunChunk(state, chunk, guard);
            return recorder.Steps;
        }

        // ---- table.find -----------------------------------------------------------------------------

        [Test]
        public void Find_ReturnsTheMirrorsDocumentedIndices()
        {
            Assert.AreEqual("4|nil|nil", Text("table.find({'a','b','c','d','e'}, 'd'), "
                                              + "table.find({'a','b','c','d','e'}, 'z'), table.find({'a','b','c','d','e'}, 'b', 3)"));
            Assert.AreEqual("2", Text("table.find({'a','b','c','b'}, 'b')"), "the first occurrence wins");
            Assert.AreEqual("4", Text("table.find({'a','b','c','b'}, 'b', 3)"), "init starts the search");
        }

        [Test]
        public void Find_StopsAtTheFirstNil_AndReadsRaw()
        {
            Assert.AreEqual("nil", Text("table.find({1, nil, 3}, 3)"), "Luau stops at the first nil, like ipairs");
            Assert.AreEqual("nil", Text("table.find(setmetatable({}, {__index = {7}}), 7)"),
                "the array is read raw, as luaH_getnum reads it");
            Assert.AreEqual("nil", Text("table.find({1, 2}, nil)"), "nil is a value, and never equals an element");
        }

        [Test]
        public void Find_ComparesDatatypesByTheirEquality_AsDoesEqualsEquals()
        {
            LuaCsSecureEnvironment environment = new();
            LuaState state = environment.Create();
            LuaValue[] results = environment.RunChunk(state, @"
                local mt = { __eq = function(a, b) return a.id == b.id end }
                local list = { setmetatable({ id = 1 }, mt), setmetatable({ id = 2 }, mt) }
                local other = setmetatable({ id = 2 }, {})
                return table.find(list, setmetatable({ id = 2 }, mt)), table.find(list, other)");
            Assert.AreEqual(2d, results[0].Read<double>(), "a shared __eq decides, as it does for ==");
            Assert.AreEqual(LuaValueType.Nil, results[1].Type, "a different metatable never calls __eq");
        }

        [Test]
        public void Find_RefusesBadArguments_WithLuausReasons()
        {
            StringAssert.Contains("bad argument #1 to 'find' (table expected, got nil)", Error("table.find(nil, 1)"));
            StringAssert.Contains("bad argument #2 to 'find' (value expected)", Error("table.find({})"));
            StringAssert.Contains("bad argument #3 to 'find' (index out of range)", Error("table.find({1}, 1, 0)"));
            StringAssert.Contains("(number expected, got table)", Error("table.find({1}, 1, {})"));
            Assert.AreEqual("1", Text("table.find({1}, 1, '1')"), "a numeric string is a number, as luaL_checkinteger reads it");
            Assert.AreEqual("1", Text("table.find({1}, 1, 1.9)"), "the fraction is truncated");
        }

        [Test]
        public void Find_IsChargedLikeTheLuaLoopItReplaces()
        {
            LuaCsSecureEnvironment environment = new();
            LuaState state = environment.Create();
            environment.RunChunk(state, "T = {} for i = 1, 20000 do T[i] = i end");
            long empty = StepsOf(environment, state, "return 0");
            long loop = StepsOf(environment, state,
                "local t, x = T, -1 for i = 1, #t do if t[i] == x then return i end end return nil") - empty;
            long native = StepsOf(environment, state, "return table.find(T, -1)") - empty;
            Assert.That(native, Is.InRange(loop * 9 / 10, loop * 11 / 10),
                "a scan of 20,000 elements costs the steps of the loop it replaces, not one instruction");
            long small = StepsOf(environment, state, "return table.find({1, 2, 3}, 3)") - empty;
            Assert.Less(small, 100, "a short scan costs a short loop's steps");
        }

        [Test]
        public void Find_OverAHugeTable_TripsTheBudgetThatTheLuaLoopWouldTrip()
        {
            LuaCsSecureEnvironment environment = new();
            LuaState state = environment.Create();
            environment.RunChunk(state, "T = {} for i = 1, 50000 do T[i] = i end");
            LuaCsExecutionGuard tight = new(timeoutMs: 60_000, maxSteps: 10_000);
            LuaCsHostFunctionException trip = Assert.Throws<LuaCsHostFunctionException>(() =>
                environment.RunChunk(state, "local r = table.find(T, -1) local x = 0 x = x + 1 return r", tight));
            StringAssert.Contains("EXCEEDED", trip.Message, "the run ends with its step budget, as a Lua loop would");

            LuaCsExecutionGuard twin = new(timeoutMs: 60_000, maxSteps: 10_000);
            LuaValue[] results = environment.RunChunk(state, "return table.find(T, 500)", twin);
            Assert.AreEqual(500d, results[0].Read<double>(), "a scan within the budget completes");
        }

        // ---- table.clear / create / clone -------------------------------------------------------------

        [Test]
        public void Clear_EmptiesTheTable_AndKeepsItsMetatable()
        {
            Assert.AreEqual("nil|0|nil|true", Text(
                "(function() local mt = {} local t = setmetatable({95, 82, 71, x = 1}, mt) table.clear(t) "
                + "return t[1], #t, t.x, getmetatable(t) == mt end)()"));
            StringAssert.Contains("bad argument #1 to 'clear' (table expected, got string)", Error("table.clear('x')"));
        }

        [Test]
        public void Create_FillsTheMirrorsExample_AndOnlyReservesWithoutAValue()
        {
            Assert.AreEqual("RobloxRobloxRoblox", Text("table.concat(table.create(3, 'Roblox'))"));
            Assert.AreEqual("0|0|3", Text("#table.create(5), #table.create(5, nil), #table.create(3, false)"));
            Assert.AreEqual("0", Text("#table.create(0, 1)"));
        }

        [Test]
        public void Create_RefusesANegativeSize_AndCapsWhatOneCallMayFill()
        {
            StringAssert.Contains("bad argument #1 to 'create' (size out of range)", Error("table.create(-1)"));
            StringAssert.Contains("bad argument #1 to 'create' (size out of range)", Error("table.create(1e10)"));
            StringAssert.Contains("(number expected, got no value)", Error("table.create()"));
            string refusal = Error("table.create(" + (LuaCsSecureEnvironment.MaxTableBuildLength + 1) + ", 0)");
            StringAssert.StartsWith("false|sandbox: table.create would fill", refusal);
            Assert.AreEqual("0", Text("#table.create(1e9)"), "a reservation past the cap is unobservable and allowed");
            Assert.AreEqual(LuaCsSecureEnvironment.MaxTableBuildLength.ToString(),
                Text("#table.create(" + LuaCsSecureEnvironment.MaxTableBuildLength + ", true)"), "the cap itself is allowed");
        }

        [Test]
        public void Clone_IsShallow_KeepsTheMetatable_AndIsNeverFrozen()
        {
            Assert.AreEqual("true|1|true|true|false", Text(
                "(function() local mt = {} local inner = {} local t = setmetatable({1, a = inner}, mt) "
                + "table.freeze(t) local c = table.clone(t) "
                + "return c ~= t, c[1], c.a == inner, getmetatable(c) == mt, table.isfrozen(c) end)()"));
            StringAssert.Contains("bad argument #1 to 'clone' (table has a protected metatable)",
                Error("table.clone(setmetatable({}, {__metatable = 'locked'}))"));
        }

        // ---- table.freeze -----------------------------------------------------------------------------

        [Test]
        public void Freeze_ReturnsTheTable_AndIsFrozenTellsFrozenFromNot()
        {
            Assert.AreEqual("true|true|false", Text(
                "(function() local t = {} return table.freeze(t) == t, table.isfrozen(t), table.isfrozen({}) end)()"));
            StringAssert.Contains("bad argument #1 to 'isfrozen' (table expected, got no value)", Error("table.isfrozen()"));
            StringAssert.Contains("(table has a protected metatable)",
                Error("table.freeze(setmetatable({}, {__metatable = false}))"));
        }

        [Test]
        public void Freeze_MakesLibraryWritesRaise_ReadsKeepWorking()
        {
            string[] writes =
            {
                "table.insert(t, 4)", "table.insert(t, 1, 0)", "table.remove(t)", "table.sort(t)", "table.clear(t)",
                "rawset(t, 'x', 1)", "setmetatable(t, {})", "table.move({9}, 1, 1, 1, t)"
            };
            foreach (string write in writes)
            {
                Assert.AreEqual("false|attempt to modify a readonly table",
                    Error("(function() local t = table.freeze({3, 1, 2}) " + write + " end)()"), write);
            }

            Assert.AreEqual("3|1|3|3", Text("(function() local t = table.freeze({3, 1, 2}) "
                                            + "return #t, t[2], select('#', unpack(t)), table.find(t, 2) end)()"));
            Assert.AreEqual("4", Text("(function() local t = {3, 1, 2} table.insert(t, 4) return #t end)()"),
                "an unfrozen table is untouched by the check");
            Assert.AreEqual("1", Text("(function() local t = table.freeze({}) table.move(t, 1, 0, 1, {}) "
                                      + "return 1 end)()"), "moving nothing writes nothing");
        }

        [Test]
        public void Freeze_DoesNotRefuseAPlainAssignment_Dev106()
        {
            Assert.AreEqual("2|true", Text("(function() local t = table.freeze({a = 1}) t.a = 2 "
                                           + "return t.a, table.isfrozen(t) end)()"),
                "the Lua 5.2 VM has no read-only tables; this pins the documented gap so a fix is noticed");
        }

        // ---- table.move / maxn / getn / foreach -------------------------------------------------------

        [Test]
        public void Move_CopiesTheMirrorsExample_AndHandlesOverlapBothWays()
        {
            Assert.AreEqual("1,2,3,4,5", Text("table.concat(table.move({4, 5}, 1, 2, 4, {1, 2, 3}), ',')"));
            Assert.AreEqual("1,1,2,3,4", Text("(function() local t = {1,2,3,4,5} table.move(t, 1, 4, 2) "
                                              + "return table.concat(t, ',') end)()"));
            Assert.AreEqual("2,3,4,5,5", Text("(function() local t = {1,2,3,4,5} table.move(t, 2, 5, 1) "
                                              + "return table.concat(t, ',') end)()"));
            Assert.AreEqual("true", Text("(function() local t = {} return table.move({}, 1, 0, 1, t) == t end)()"));
        }

        [Test]
        public void Move_RefusesBadRanges_AndCapsOneCall()
        {
            StringAssert.Contains("bad argument #3 to 'move' (too many elements to move)",
                Error("table.move({}, -1, 2147483647, 1)"));
            StringAssert.Contains("bad argument #4 to 'move' (destination wrap around)",
                Error("table.move({}, 1, 10, 2147483647)"));
            StringAssert.Contains("(table expected, got number)", Error("table.move({}, 1, 1, 1, 5)"));
            StringAssert.StartsWith("false|sandbox: table.move would move", Error("table.move({}, 1, 1e8, 1)"));
        }

        [Test]
        public void MaxN_GetN_ForEach_FollowTheDeprecatedLuauBehaviour()
        {
            Assert.AreEqual("10|0", Text("table.maxn({1, 2, [10] = 3, [2.5] = 1, x = 4}), table.maxn({x = 1})"));
            Assert.AreEqual("2.5", Text("table.maxn({[2.5] = true})"), "a fractional key counts, as in Luau");
            Assert.AreEqual("3", Text("table.getn({1, 2, 3})"));
            Assert.AreEqual("6|b2", Text("(function() local s = 0 table.foreachi({1, 2, 3}, function(i, v) s = s + v end) "
                                         + "return s, table.foreachi({'a', 'b'}, function(i, v) if i == 2 then return v .. i end end) end)()"));
            Assert.AreEqual("a1", Text("table.foreach({a = 1}, function(k, v) return k .. v end)"));
            StringAssert.Contains("bad argument #2 to 'foreach' (function expected, got no value)", Error("table.foreach({})"));
        }

        [Test]
        public void ForEach_RefusesAYieldFromItsCallback_AsAnyLibraryCallback()
        {
            LuaValue[] results = Run(@"
                local co = coroutine.create(function() table.foreachi({1}, function() coroutine.yield() end) end)
                return coroutine.resume(co)");
            Assert.IsFalse(results[0].Read<bool>());
            StringAssert.Contains(LuaCsSecureEnvironment.YieldAcrossCallBoundaryMessage, results[1].ToString());
        }

        [Test]
        public void TableFunctions_SeeEveryElementOfATableFilledBackwards()
        {
            // WHY: Lua-CSharp 0.5.6 kept t[9..] of such a table out of its array part (issue 345, fixed in 0.5.8),
            // so this pins that the library finds the border itself and is right on both versions.
            Assert.AreEqual("12|12|12|78|12", Text("(function() local t = {} for i = 12, 1, -1 do t[i] = i end "
                                                   + "local s = 0 table.foreachi(t, function(_, v) s = s + v end) "
                                                   + "return table.getn(t), select('#', unpack(t)), table.find(t, 12), s, "
                                                   + "table.maxn(t) end)()"));
            Assert.AreEqual("12|12", Text("(function() local t = {} for i = 12, 1, -1 do t[i] = i end "
                                          + "local c = table.clone(t) return table.getn(c), c[12] end)()"));
        }

        // ---- unpack -----------------------------------------------------------------------------------

        [Test]
        public void Unpack_IsAGlobal_AndReturnsTheRangeRaw()
        {
            Assert.AreEqual("1|2|3", Text("unpack({1, 2, 3})"));
            Assert.AreEqual("2|3", Text("unpack({1, 2, 3}, 2)"));
            Assert.AreEqual("3|nil|nil", Text("select('#', unpack({}, 1, 3)), unpack({}, 1, 2)"));
            Assert.AreEqual("0", Text("select('#', table.unpack({1}, 2, 1))"));
            Assert.AreEqual("true", Text("unpack == table.unpack"));
        }

        [Test]
        public void Unpack_RefusesMoreValuesThanLuauReturns()
        {
            string refusal = Error("unpack({}, 1, " + (LuaCsSecureEnvironment.MaxUnpackResults + 1) + ")");
            StringAssert.StartsWith("false|", refusal);
            StringAssert.EndsWith("too many results to unpack", refusal);
            StringAssert.EndsWith("too many results to unpack", Error("unpack({}, 1, 100000)"),
                "Lua-CSharp's own unpack returned 34,464 values here");
            Assert.AreEqual(LuaCsSecureEnvironment.MaxUnpackResults.ToString(),
                Text("select('#', unpack({}, 1, " + LuaCsSecureEnvironment.MaxUnpackResults + "))"));
        }
    }
}
#endif
