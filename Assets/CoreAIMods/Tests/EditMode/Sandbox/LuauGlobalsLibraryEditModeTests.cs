#if COREAI_LUA
using System;
using System.Text;
using System.Threading;
using CoreAI.Sandbox.LuaCs;
using Lua;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.Sandbox
{
    /// <summary>
    /// The Luau globals, <c>coroutine</c> members, <c>debug</c> library and <c>os.date</c>/<c>os.difftime</c> of
    /// MVP13 (RT5): <c>newproxy</c>, <c>gcinfo</c>, Roblox's <c>collectgarbage</c>, <c>loadstring</c>, <c>getfenv</c>,
    /// <c>setfenv</c> and <c>ypcall</c> rules; <c>coroutine.wrap</c> rebuilt over the guarded resume,
    /// <c>isyieldable</c> and <c>close</c>; and the loud stubs of <c>buffer</c> and <c>vector</c>.
    /// </summary>
    [TestFixture]
    public sealed class LuauGlobalsLibraryEditModeTests
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

        private static string Text(string chunk)
        {
            LuaCsSecureEnvironment environment = new();
            LuaState state = environment.Create();
            LuaValue[] results = environment.RunChunk(state, "local r = table.pack(" + chunk + ") "
                + "for i = 1, r.n do r[i] = tostring(r[i]) end return table.concat(r, '|', 1, r.n)");
            return results[0].Read<string>();
        }

        private static string Error(string call)
        {
            LuaCsSecureEnvironment environment = new();
            LuaState state = environment.Create();
            LuaValue[] results = environment.RunChunk(state, "local ok, err = pcall(function() return " + call
                + " end) return tostring(ok) .. '|' .. tostring(err)");
            return results[0].Read<string>();
        }

        /// <summary>Drops the <c>chunk:line: </c> position Lua puts in front of an error raised at level 1.</summary>
        private static string StripPosition(string error)
        {
            int bar = error.IndexOf('|');
            string message = error.Substring(bar + 1);
            int position = message.IndexOf("]:", StringComparison.Ordinal);
            if (position >= 0)
            {
                int colon = message.IndexOf(": ", position + 2, StringComparison.Ordinal);
                message = colon >= 0 ? message.Substring(colon + 2) : message;
            }

            return error.Substring(0, bar + 1) + message;
        }

        // ---- globals --------------------------------------------------------------------------------

        [Test]
        public void NewProxy_IsAUserdata_WithAnOptionalMetatableOfItsOwn()
        {
            Assert.AreEqual("userdata|true|nil|5", Text("(function() local p = newproxy(true) local q = newproxy() "
                + "getmetatable(p).__index = function() return 5 end "
                + "return type(p), getmetatable(p) ~= nil, getmetatable(q), p.anything end)()"));
            StringAssert.Contains("bad argument #1 to 'newproxy' (nil or boolean expected, got number)", Error("newproxy(1)"));
            Assert.AreEqual("false", Text("newproxy() == newproxy()"), "each call is a new userdata");
        }

        [Test]
        public void GcInfo_AndCollectGarbageCount_ReportWholeKilobytes_OtherOptionsAreRefused()
        {
            Assert.AreEqual("number|true|true", Text("type(gcinfo()), gcinfo() > 0, collectgarbage('count') % 1 == 0"));
            Assert.AreEqual("false|" + LuaCsSecureEnvironment.CollectGarbageCountOnlyMessage,
                StripPosition(Error("collectgarbage()")));
            Assert.AreEqual("false|" + LuaCsSecureEnvironment.CollectGarbageCountOnlyMessage,
                StripPosition(Error("collectgarbage('collect')")));
        }

        [Test]
        public void LoadString_AnswersAsRobloxDoesWhileLoadStringEnabledIsOff()
        {
            Assert.AreEqual("nil|" + LuaCsSecureEnvironment.LoadStringUnavailableMessage, Text("loadstring('return 1')"));
            Assert.AreEqual("nil|nil", Text("load, dofile"), "the stock loaders stay removed");
        }

        [Test]
        public void GetFEnv_ReturnsTheScriptsEnvironment_SetFEnvIsALoudRefusal()
        {
            Assert.AreEqual("true|true|true|true", Text("getfenv() == _G, getfenv(0) == _G, getfenv(1) == _G, getfenv(print) == _G"));
            Assert.AreEqual("7", Text("(function() getfenv().answer = 7 return answer end)()"));
            StringAssert.Contains("bad argument #1 to 'getfenv' (invalid level)", Error("getfenv(999)"));
            StringAssert.Contains("bad argument #1 to 'getfenv' (level must be non-negative)", Error("getfenv(-1)"));
            string setfenv = Error("setfenv(1, {})");
            StringAssert.Contains("NOT_IMPLEMENTED", setfenv);
            StringAssert.Contains("setfenv", setfenv);
        }

        [Test]
        public void YPCall_IsPCall()
        {
            Assert.AreEqual("true|true|7", Text("ypcall == pcall, ypcall(function() return 7 end)"));
        }

        [Test]
        public void BufferAndVector_AreLoudStubs_NamingTheirRung()
        {
            foreach (string call in new[] { "buffer.create(8)", "buffer.len", "vector.create(1, 2, 3)", "vector.zero" })
            {
                string error = Error(call);
                StringAssert.Contains("NOT_IMPLEMENTED", error, call);
                StringAssert.Contains("planned for MVP14", error, call);
            }
        }

        // ---- coroutine ------------------------------------------------------------------------------

        [Test]
        public void Wrap_ResumesAndReturnsWhatTheBodyYields()
        {
            Assert.AreEqual("2|20", Text("(function() local f = coroutine.wrap(function(a) local b = coroutine.yield(a + 1) "
                                         + "return b * 2 end) return f(1), f(10) end)()"));
            Assert.AreEqual("1|2|3", Text("(function() local f = coroutine.wrap(function() coroutine.yield(1, 2, 3) end) return f() end)()"));
        }

        [Test]
        public void Wrap_RaisesTheBodysError_AndADeadCoroutinesError()
        {
            StringAssert.EndsWith("boom", Error("coroutine.wrap(function() error('boom') end)()"));
            StringAssert.Contains("cannot resume dead coroutine",
                Error("(function() local f = coroutine.wrap(function() end) f() return f() end)()"));
            Assert.AreEqual("false|table", Text("(function() local ok, e = pcall(coroutine.wrap(function() error({}) end)) "
                                                + "return ok, type(e) end)()"), "a non-string error value passes unchanged");
            StringAssert.Contains("bad argument #1 to 'wrap' (function expected, got no value)", Error("coroutine.wrap()"));
        }

        [Test]
        [Timeout(30000)]
        public void Wrap_RunsItsBodyUnderTheResumeBudget_SoARunawayLoopIsCut()
        {
            LuaCsSecureEnvironment environment = new();
            LuaState state = environment.Create();
            LuaValue[] results = environment.RunChunk(state,
                "local f = coroutine.wrap(function() local n = 0 for i = 1, 5000000 do n = n + 1 end return n end) "
                + "local ok, err = pcall(f) return ok, tostring(err)");
            Assert.IsFalse(results[0].Read<bool>(), "the body is cut, never run to completion");
            string cut = results[1].Read<string>();
            Assert.IsTrue(cut.Contains("EXCEEDED_COROUTINE_STEP_BUDGET") || cut.Contains("coroutine resume exceeded"),
                "the per-resume budget of a raw coroutine cut it (steps, or time on a slow machine): " + cut);
        }

        [Test]
        public void IsYieldable_IsTrueOnlyInsideARunningCoroutine()
        {
            Assert.AreEqual("false|true|true", Text("coroutine.isyieldable(), coroutine.resume(coroutine.create(function() "
                                                    + "return coroutine.isyieldable() end))"));
            Assert.AreEqual("true|false", Text("coroutine.resume(coroutine.create(function() local r "
                                               + "table.sort({1, 2}, function(a, b) r = coroutine.isyieldable() return a < b end) return r end))"),
                "inside a library callback a yield would cross the C-call boundary");
        }

        [Test]
        public void Close_EndsASuspendedCoroutine_AndRefusesARunningOne()
        {
            Assert.AreEqual("true|dead|false", Text("(function() local co = coroutine.create(function() coroutine.yield() end) "
                                                    + "coroutine.resume(co) local closed = coroutine.close(co) "
                                                    + "return closed, coroutine.status(co), (coroutine.resume(co)) end)()"));
            Assert.AreEqual("true", Text("coroutine.close(coroutine.create(function() end))"));
            StringAssert.Contains("cannot close running coroutine",
                Error("(function() local co co = coroutine.create(function() return coroutine.close(co) end) "
                      + "local ok, err = coroutine.resume(co) error(err, 0) end)()"));
            StringAssert.Contains("bad argument #1 to 'close' (thread expected, got no value)", Error("coroutine.close()"));
        }

        // ---- debug ----------------------------------------------------------------------------------

        [Test]
        public void Traceback_StartsWithTheMessage_AndListsTheCallers()
        {
            string traceback = Text("(function() local function fnA() local t = debug.traceback('Specific moment during fnA()') return t end "
                                    + "local function fnB() local t = fnA() return t end local t = fnB() return t end)()");
            StringAssert.StartsWith("Specific moment during fnA()\n", traceback);
            Assert.GreaterOrEqual(traceback.Split('\n').Length, 4, "the message and at least fnA, fnB and the chunk");
            Assert.AreEqual("true|true", Text("(function() local t = {} return debug.traceback(t) == t, type(debug.traceback()) == 'string' end)()"),
                "a message that is not a string comes back unchanged");
        }

        [Test]
        public void Info_ReportsSourceLineAndArity()
        {
            Assert.AreEqual("2|2|true", Text("(function()\nlocal function fnB(a, b, ...) end\nreturn debug.info(fnB, 'la') end)()"));
            Assert.AreEqual("3", Text("(function()\nlocal function fnA()\nlocal l = debug.info(1, 'l') return l end\nlocal r = fnA() return r end)()"),
                "level 1 is the function that called debug.info, at the line of the call");
            Assert.AreEqual("[C]|-1", Text("debug.info(print, 'sl')"));
            Assert.AreEqual("0", Text("select('#', debug.info(50, 's'))"), "a level past the stack returns nothing");
            StringAssert.Contains("bad argument #2 to 'info' (invalid option)", Error("debug.info(1, 'x')"));
            StringAssert.Contains("bad argument #2 to 'info' (invalid option)", Error("debug.info(1, 'ss')"));
        }

        [Test]
        public void ProfilerAndMemoryCategory_AreHarmless_DumpCodeSizeIsRefused()
        {
            Assert.AreEqual("main|custom|main", Text("(function() debug.profilebegin('x') debug.profileend() "
                                                      + "local before = debug.setmemorycategory('custom') local now = debug.getmemorycategory() "
                                                      + "debug.resetmemorycategory() return before, now, debug.getmemorycategory() end)()"));
            StringAssert.Contains("(string expected, got no value)", Error("debug.profilebegin()"));
            StringAssert.Contains("NOT_IMPLEMENTED", Error("debug.dumpcodesize()"));
        }

        // ---- os.date / os.difftime -------------------------------------------------------------------

        private static string OsText(string chunk, double now)
        {
            LuaCsSecureEnvironment environment = new();
            LuaState state = environment.Create();
            LuaTable os = new();
            LuaCsSecureEnvironment.InstallLuauOsExtensions(os, () => now);
            state.Environment["os"] = os;
            LuaValue[] results = environment.RunChunk(state, "local r = table.pack(" + chunk + ") "
                + "for i = 1, r.n do r[i] = tostring(r[i]) end return table.concat(r, '|', 1, r.n)");
            return results[0].Read<string>();
        }

        [Test]
        public void Date_FormatsEveryLuauConversion_InTheCLocale()
        {
            Assert.AreEqual("1970-01-01 00:00:00", OsText("os.date('!%Y-%m-%d %H:%M:%S', 0)", 0d));
            Assert.AreEqual("Fri Jan  1 00:00:00 1971", OsText("os.date('!%c', 86400 * 365)", 0d));
            Assert.AreEqual("09/09/01 01:46:40 AM 252 Sun Sunday Sep September 0 01 2001 UTC +0000 36 36 %",
                OsText("os.date('%x %X %p %j %a %A %b %B %w %y %Y %Z %z %U %W %%', 1e9)", 0d));
            Assert.AreEqual("01 PM|12 AM", OsText("os.date('!%I %p', 13 * 3600), os.date('!%I %p', 0)", 0d));
            Assert.AreEqual("2001", OsText("os.date('%Y')", 1e9), "the default time is the clock os.time reads");
        }

        [Test]
        public void Date_StarT_RoundTripsThroughOsTime()
        {
            Assert.AreEqual("2001|9|9|1|46|40|1|252|false", OsText("(function() local d = os.date('*t', 1e9) "
                + "return d.year, d.month, d.day, d.hour, d.min, d.sec, d.wday, d.yday, d.isdst end)()", 0d));
            Assert.AreEqual("true", OsText("os.date('*t', 5) ~= nil and os.date('!*t', 5).sec == 5", 0d));
        }

        [Test]
        public void Date_RefusesAnUnknownConversion_AndAnEpochBeforeTheLocalPath()
        {
            LuaCsSecureEnvironment environment = new();
            LuaState state = environment.Create();
            LuaTable os = new();
            LuaCsSecureEnvironment.InstallLuauOsExtensions(os, () => 0d);
            state.Environment["os"] = os;
            LuaValue[] results = environment.RunChunk(state,
                "local ok, err = pcall(function() return os.date('%Q') end) return ok, err, os.date('*t', -1), os.date('!%Y', -86400)");
            Assert.IsFalse(results[0].Read<bool>());
            StringAssert.Contains("bad argument #1 to 'date' (invalid conversion specifier)", results[1].Read<string>());
            Assert.AreEqual(LuaValueType.Nil, results[2].Type, "Luau's local-time path refuses a date before the epoch");
            Assert.AreEqual("1969", results[3].Read<string>(), "the UTC path computes it");
        }

        [Test]
        public void DiffTime_SubtractsWholeSeconds()
        {
            Assert.AreEqual("7|10|-3", OsText("os.difftime(10, 3), os.difftime(10.9), os.difftime(3, 6)", 0d));
        }
    }
}
#endif
