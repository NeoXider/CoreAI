#if COREAI_LUA
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
        [Timeout(30000)]
        public void Coroutine_RunawayLoop_IsCutByResumeBudget()
        {
            LuaCsSecureEnvironment env = new();
            LuaState state = env.Create();

            // A runaway loop inside a mod-created coroutine runs on a CHILD LuaState the native library does not
            // guard; wrapping coroutine.resume arms a per-resume step/time budget on that child. `resume` runs
            // protected, so a cut surfaces as `ok == false` (never runs to completion). The loop is finite so a
            // REGRESSION (guard not armed) still terminates (returning ok == true, failing the assert) instead
            // of hanging the run.
            LuaValue[] result = env.RunChunk(state,
                "local co = coroutine.create(function()\n" +
                "  local n = 0\n" +
                "  for i = 1, 2000000 do n = n + 1 end\n" +
                "end)\n" +
                "local ok = coroutine.resume(co)\n" +
                "return ok");

            Assert.IsTrue(result.Length > 0, "RunChunk must return the resume result.");
            Assert.IsFalse(result[0].Read<bool>(),
                "The runaway coroutine loop must be cut by the per-resume guard (resume returns false), not complete.");
        }

        [Test]
        [Timeout(30000)]
        public void Coroutine_Wrap_IsRemoved_UnguardablePrimitiveCannotHang()
        {
            LuaCsSecureEnvironment env = new();
            LuaState state = env.Create();

            // SECURITY: coroutine.wrap drives a hidden CHILD LuaState through the library's OWN resume, bypassing
            // the guarded coroutine.resume, so a wrap body (`while true do end`) would run with no step/time/alloc
            // hook and hang the host forever. It is therefore stripped (set nil). First, assert it is gone.
            LuaValue[] isNil = env.RunChunk(state, "return coroutine.wrap == nil");
            Assert.IsTrue(isNil.Length > 0 && isNil[0].Read<bool>(),
                "coroutine.wrap must be removed from the sandbox — it cannot be guarded on this Lua-CSharp build.");

            // And reaching for it must raise a clean nil-call error PROMPTLY, not hang. If a regression re-natives
            // wrap, the unguarded child-state loop below runs forever and this [Timeout] test fails — the signal.
            Assert.Throws<LuaRuntimeException>(
                () => env.RunChunk(state, "coroutine.wrap(function() while true do end end)()"),
                "Calling the removed coroutine.wrap must raise a nil-call error, never hang.");
        }

        [Test]
        public void AllocationBomb_ConcatDoubling_ThrowsMemoryBudgetError()
        {
            LuaCsSecureEnvironment env = new();
            LuaState state = env.Create();

            // WHY: the production guard deliberately samples GC.GetTotalMemory(false), whose committed
            // high-water mark can be reused across repeated test runs. Compact first so this test measures
            // the bomb's live growth instead of inheriting capacity from an earlier Unity process run.
            System.GC.Collect();
            System.GC.WaitForPendingFinalizers();
            System.GC.Collect();

            // string.rep is capped at MaxStringRepLength (1MB); doubling it via plain concatenation (no library
            // call site to intercept) must be caught by the per-instruction GC allocation budget. Use an
            // explicit low budget (64MB) with generous step/time so the trip fires on the MEMORY backstop while
            // the string stays bounded (~128MB peak) — a huge default-budget bomb risks a multi-GB concat opcode
            // (uninterruptible between VM instructions) that can hang/OOM the machine.
            LuaCsExecutionGuard guard = new(8000, 10_000_000, 64 * 1024 * 1024);
            LuaRuntimeException ex = Assert.Throws<LuaRuntimeException>(() =>
                env.RunChunk(state,
                    "local s = string.rep('x', 1000000)\n" +
                    "for i = 1, 7 do s = s .. s end\n" +
                    "return s",
                    guard));

            // The security guarantee is that the run is CUT before unbounded growth — accept the memory backstop
            // or the step/time budgets it races (GC.GetTotalMemory reflects managed growth only coarsely).
            Assert.IsTrue(
                ex.Message.Contains("EXCEEDED_MEMORY_BUDGET") || ex.Message.Contains("exceeded"),
                $"Expected the allocation bomb to be cut by a sandbox budget, got: {ex.Message}");
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
        [Timeout(15000)]
        public void NestedGuardedCall_SameState_OuterBudgetStaysArmed()
        {
            LuaCsSecureEnvironment env = new();
            LuaCsApiRegistry registry = new();
            LuaCsExecutionGuard nestedGuard = new(2000, 10_000);
            LuaState state = null;
            LuaFunction noop = null;
            registry.Register("nested", new System.Func<double>(() =>
            {
                // Mirrors mods_call: a guarded call re-entering the guard on the SAME LuaState.
                LuaValue[] r = nestedGuard.Execute(state, noop, CancellationToken.None);
                return r.Length > 0 ? r[0].Read<double>() : 0d;
            }));
            state = env.Create(registry);
            noop = env.RunChunk(state, "return function() return 1 end")[0].Read<LuaFunction>();

            // The nested guard's cleanup must restore the outer hook instead of clearing it; otherwise
            // the over-budget loop after nested() runs unlimited and the chunk returns normally.
            LuaCsExecutionGuard outerGuard = new(2000, 5_000);
            LuaRuntimeException ex = Assert.Throws<LuaRuntimeException>(() =>
                env.RunChunk(state,
                    "nested()\n" +
                    "local x = 0\n" +
                    "for i = 1, 100000 do x = x + 1 end\n" +
                    "return x",
                    outerGuard));

            Assert.IsTrue(ex.Message.Contains("EXCEEDED_HARD_LIMIT_STEPS"),
                $"Expected the OUTER step budget to stay armed after a nested guarded call, got: {ex.Message}");
        }

        [Test]
        public void IsMemoryBudgetTrip_ClassifiesProcessHeapTripsOnlyNotStepOrTimeOverruns()
        {
            // The runtime relies on this classification to keep a blameless mod loaded: a process-heap
            // memory trip must be recognised by its dedicated TYPE (through the wrapping exception chain)
            // while the real step and time guards must NOT be — those keep counting toward the streak.
            System.Exception memoryTrip = new System.InvalidOperationException("wrapped",
                new LuaMemoryBudgetException(
                    $"LuaCsSecureEnvironment: {LuaCsExecutionGuard.MemoryBudgetTripMarker} (268435456 bytes)"));
            Assert.IsTrue(LuaCsExecutionGuard.IsMemoryBudgetTrip(memoryTrip),
                "A LuaMemoryBudgetException anywhere in the exception chain must be recognised.");

            // SECURITY: a mod's own error() text is NOT a memory trip, even if it forges the marker string —
            // classification is by type, so a mod cannot dodge the auto-unload guard by faking the message.
            Assert.IsFalse(LuaCsExecutionGuard.IsMemoryBudgetTrip(
                    new System.InvalidOperationException(
                        $"boom {LuaCsExecutionGuard.MemoryBudgetTripMarker} forged by a mod")),
                "A forged marker string in an ordinary exception message must NOT be classified as a memory trip.");

            Assert.IsFalse(LuaCsExecutionGuard.IsMemoryBudgetTrip(
                    new System.InvalidOperationException("LuaCsSecureEnvironment: EXCEEDED_HARD_LIMIT_STEPS (200000)")),
                "A step overrun is a real guard and must not be classified as a memory trip.");
            Assert.IsFalse(LuaCsExecutionGuard.IsMemoryBudgetTrip(
                    new System.TimeoutException("Lua exceeded 500 ms.")),
                "A timeout is a real guard and must not be classified as a memory trip.");
            Assert.IsFalse(LuaCsExecutionGuard.IsMemoryBudgetTrip(null),
                "A null exception is not a memory trip.");
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

        [Test]
        [Timeout(15000)]
        public void StepBudget_Overrun_IsCut_AfterSamplingHookScalesSteps()
        {
            LuaCsSecureEnvironment env = new();
            LuaState state = env.Create();

            // The instruction hook now fires every HookInstructionBatch instructions (sampling) and charges
            // that batch to the step counter, so the SAME max-instruction ceiling is enforced. A loop far
            // longer than the 5,000-step budget must still be cut — if the batch scaling regressed (steps no
            // longer accumulate), the hook would under-count and the loop would run to completion, returning
            // normally and failing this Assert.Throws instead of hanging (the loop is finite).
            LuaCsExecutionGuard guard = new(60_000, 5_000, 0);
            LuaRuntimeException ex = Assert.Throws<LuaRuntimeException>(() =>
                env.RunChunk(state,
                    "local x = 0\n" +
                    "for i = 1, 5000000 do x = x + 1 end\n" +
                    "return x",
                    guard));

            Assert.IsTrue(ex.Message.Contains("EXCEEDED_HARD_LIMIT_STEPS"),
                $"Expected the sampled step budget to cut the over-budget loop, got: {ex.Message}");
        }

        [Test]
        [Timeout(15000)]
        public void Timeout_Overrun_IsCut_ByWallClockRegardlessOfSampling()
        {
            LuaCsSecureEnvironment env = new();
            LuaState state = env.Create();

            // Time is wall-clock, read from Stopwatch.GetTimestamp() on each sampled hook, so sampling does
            // not weaken it. A huge step budget forces the TIME budget to be the one that trips. A regression
            // that broke the GetTimestamp/ticks-budget math would let the busy loop run unbounded and this
            // [Timeout] test would fail — the signal.
            LuaCsExecutionGuard guard = new(150, 5_000_000_000L, 0);
            LuaRuntimeException ex = Assert.Throws<LuaRuntimeException>(() =>
                env.RunChunk(state,
                    "local x = 0\n" +
                    "while true do x = x + 1 end\n" +
                    "return x",
                    guard));

            Assert.IsTrue(ex.Message.Contains("exceeded"),
                $"Expected the wall-clock timeout to cut the infinite loop, got: {ex.Message}");
        }

        [Test]
        [Timeout(15000)]
        public void NormalShortHandler_UnderGuard_CompletesAndReturns()
        {
            LuaCsSecureEnvironment env = new();
            LuaState state = env.Create();

            // A well-behaved short handler (the 20 Hz tick shape the guard is on) must pass cleanly under a
            // tight-but-sufficient budget: none of the sampled step/time/alloc limits may trip a normal call.
            LuaCsExecutionGuard guard = new(2_000, 200_000);
            LuaValue[] result = env.RunChunk(state,
                "local x = 0\n" +
                "for i = 1, 100 do x = x + i end\n" +
                "return x",
                guard);

            Assert.IsTrue(result.Length > 0, "A normal short handler must return its result under the guard.");
            Assert.AreEqual(5050, (int)result[0].Read<double>(),
                "The guarded short handler must compute the correct result, unaffected by the sampling hook.");
        }

        /// <summary>
        /// Serializes every value a call returns as "count|type:value|...", so a conformance row pins the
        /// number of results and their types, not just the first value.
        /// </summary>
        private const string SerializePrelude =
            "local function ser(...)\n" +
            "  local n = select('#', ...)\n" +
            "  local parts = {}\n" +
            "  for i = 1, n do\n" +
            "    local v = select(i, ...)\n" +
            "    parts[#parts + 1] = type(v) .. ':' .. tostring(v)\n" +
            "  end\n" +
            "  return n .. '|' .. table.concat(parts, '|')\n" +
            "end\n";

        private const string CollectGMatch =
            "local function collect(s, p)\n" +
            "  local out = {}\n" +
            "  for a, b in string.gmatch(s, p) do\n" +
            "    out[#out + 1] = '[' .. tostring(a) .. (b ~= nil and (',' .. tostring(b)) or '') .. ']'\n" +
            "  end\n" +
            "  return table.concat(out, ';')\n" +
            "end\n";

        /// <summary>Row provenance: the Lua 5.1 reference manual semantics, checked against the real Lua 5.1 interpreter.</summary>
        private const string Reference = "Lua 5.1 reference";

        /// <summary>
        /// Row provenance: Luau's lstrlib, where it differs from 5.1 (a start past the end is nil, %g
        /// exists, '%' before a non-digit in a replacement is an error, recursion deeper than 200 is
        /// "pattern too complex").
        /// </summary>
        private const string Luau = "Luau (differs from Lua 5.1)";

        /// <summary>
        /// Row provenance: the Lua 5.1 reference result, which the native Lua-CSharp matcher the sandbox
        /// used before did not return.
        /// </summary>
        private const string NativeBug = "Lua 5.1 reference; the previous native matcher differed";

        [TestCase("string.find('hello world', 'wor')", "2|number:7|number:9", Reference)]
        [TestCase("string.find('hello world', 'o', 6)", "2|number:8|number:8", Reference)]
        [TestCase("string.find('hello world', 'l', -2)", "2|number:10|number:10", Reference)]
        [TestCase("string.find('hello world', 'xyz')", "1|nil:nil", Reference)]
        [TestCase("string.find('hello', '', 6)", "2|number:6|number:5", Reference)]
        [TestCase("string.find('hello', '', 7)", "1|nil:nil", Luau)]
        [TestCase("string.find('abc', 'b', -100)", "2|number:2|number:2", Reference)]
        [TestCase("string.find('a.b', '.', 1, true)", "2|number:2|number:2", Reference)]
        [TestCase("string.find('a+b', '+', 1, true)", "2|number:2|number:2", Reference)]
        [TestCase("string.find('a.b', '%.')", "2|number:2|number:2", Reference)]
        [TestCase("string.find('hello', '(l)(l)')", "4|number:3|number:4|string:l|string:l", Reference)]
        [TestCase("string.find('hello', '()ll()')", "4|number:3|number:4|number:3|number:5", Reference)]
        [TestCase("string.find('abc', '^b')", "1|nil:nil", Reference)]
        [TestCase("string.find('abc', '^a')", "2|number:1|number:1", Reference)]
        [TestCase("string.find('abc', '$')", "2|number:4|number:3", NativeBug)]
        [TestCase("string.find('a$c', '$c')", "2|number:2|number:3", Reference)]
        [TestCase("string.find('', 'a*')", "2|number:1|number:0", NativeBug)]
        [TestCase("string.find('aXbXc', 'X', 3)", "2|number:4|number:4", Reference)]
        [TestCase("string.match('key = value', '(%w+)%s*=%s*(%w+)')", "2|string:key|string:value", Reference)]
        [TestCase("string.match('  trim  ', '^%s*(.-)%s*$')", "1|string:trim", Reference)]
        [TestCase("string.match('2024-01-15', '(%d+)-(%d+)-(%d+)')", "3|string:2024|string:01|string:15", Reference)]
        [TestCase("string.match('hello', '()ll()')", "2|number:3|number:5", Reference)]
        [TestCase("string.match('abc', '((a)(b))')", "3|string:ab|string:a|string:b", Reference)]
        [TestCase("string.match('f(a(b)c)d', '%b()')", "1|string:(a(b)c)", Reference)]
        [TestCase("string.match('abc', '%b()')", "1|nil:nil", Reference)]
        [TestCase("string.match('hello', 'h?ello')", "1|string:hello", Reference)]
        [TestCase("string.match('ello', 'h?ello')", "1|string:ello", Reference)]
        [TestCase("string.match('aaab', 'a-b')", "1|string:aaab", Reference)]
        [TestCase("string.match('xaaab', 'a+')", "1|string:aaa", Reference)]
        [TestCase("string.match('abcabc', '(abc)%1')", "1|string:abc", Reference)]
        [TestCase("string.match('abcd', '(a)(b)%2')", "1|nil:nil", Reference)]
        [TestCase("string.match('THE (quick) fox', '%f[%a]%a+')", "1|string:THE", Reference)]
        [TestCase("string.match('THE fox', '%a+%f[%A]', 5)", "1|string:fox", NativeBug)]
        [TestCase("string.match('abc', '[^a]+')", "1|string:bc", Reference)]
        [TestCase("string.match('a]b', '[]]')", "1|string:]", Reference)]
        [TestCase("string.match('a-z', '[a-]+')", "1|string:a-", Reference)]
        [TestCase("string.match('A1_b', '[%w_]+')", "1|string:A1_b", Reference)]
        [TestCase("string.match('0x1F', '0x(%x+)')", "1|string:1F", Reference)]
        [TestCase("string.match('aBc', '%u')", "1|string:B", Reference)]
        [TestCase("string.match('hello!', '%p')", "1|string:!", Reference)]
        [TestCase("string.match('ab1', '%A')", "1|string:1", Reference)]
        [TestCase("#string.match('a\\0b', '%z')", "1|number:1", Reference)]
        [TestCase("string.match('  x', '%g')", "1|string:x", Luau)]
        [TestCase("string.match('hello', '^h', 2)", "1|nil:nil", Reference)]
        [TestCase("string.match('hello', '^e', 2)", "1|string:e", Reference)]
        [TestCase("string.match('aaa', '^(a-)a$')", "1|string:aa", Reference)]
        [TestCase("string.match('<a><b>', '<(.-)>')", "1|string:a", Reference)]
        [TestCase("string.match('<a><b>', '<(.*)>')", "1|string:a><b", Reference)]
        [TestCase("collect('one two three', '%a+')", "1|string:[one];[two];[three]", Reference)]
        [TestCase("collect('k1=v1, k2=v2', '(%w+)=(%w+)')", "1|string:[k1,v1];[k2,v2]", Reference)]
        [TestCase("collect('abc', '')", "1|string:[];[];[];[]", Reference)]
        [TestCase("collect('^a^a', '^a')", "1|string:[^a];[^a]", NativeBug)]
        [TestCase("collect('THE (quick) fox', '%f[%a]%a+%f[%A]')", "1|string:[THE];[quick];[fox]", NativeBug)]
        [TestCase("collect('hello', '()l')", "1|string:[3];[4]", Reference)]
        [TestCase("collect('aaa', 'a*')", "1|string:[aaa];[]", NativeBug)]
        [TestCase("select('#', (function() local it = string.gmatch('ab', '%a') it() it() return it() end)())",
            "1|number:0", NativeBug)]
        [TestCase("string.gsub('hello world', 'o', '0')", "2|string:hell0 w0rld|number:2", Reference)]
        [TestCase("string.gsub('hello world', 'o', '0', 1)", "2|string:hell0 world|number:1", Reference)]
        [TestCase("string.gsub('hello', '', '-')", "2|string:-h-e-l-l-o-|number:6", Reference)]
        [TestCase("string.gsub('abc', '%w', '%0%0')", "2|string:aabbcc|number:3", Reference)]
        [TestCase("string.gsub('hello world', '(%w+) (%w+)', '%2 %1')", "2|string:world hello|number:1", Reference)]
        [TestCase("string.gsub('abc', 'b', '%%')", "2|string:a%c|number:1", Reference)]
        [TestCase("string.gsub('$name is $age', '%$(%w+)', {name = 'Bob', age = 42})", "2|string:Bob is 42|number:2", Reference)]
        [TestCase("string.gsub('$name $missing', '%$(%w+)', {name = 'Bob'})", "2|string:Bob $missing|number:2", Reference)]
        [TestCase("string.gsub('abc', '%w', function(c) return c:upper() .. '.' end)", "2|string:A.B.C.|number:3", Reference)]
        [TestCase("string.gsub('abc', '%w', function(c) if c == 'b' then return false end return 'x' end)",
            "2|string:xbx|number:3", Reference)]
        [TestCase("string.gsub('abc', '^a', 'x')", "2|string:xbc|number:1", Reference)]
        [TestCase("string.gsub('aaa', '^a', 'x')", "2|string:xaa|number:1", Reference)]
        [TestCase("string.gsub('abc', 'x*', '-')", "2|string:-a-b-c-|number:4", NativeBug)]
        [TestCase("string.gsub('hello world', '%w*', 'x')", "2|string:xx xx|number:4", NativeBug)]
        [TestCase("string.gsub('abc', '(b)()', '%2')", "2|string:a3c|number:1", Reference)]
        [TestCase("string.gsub('abc', 'b', 5)", "2|string:a5c|number:1", NativeBug)]
        [TestCase("string.gsub('abc', '', 'x', 2)", "2|string:xaxbc|number:2", Reference)]
        [TestCase("string.gsub('abc', 'b', 'x', 0)", "2|string:abc|number:0", Reference)]
        [TestCase("string.gsub('abc', '%w', '%1')", "2|string:abc|number:3", Reference)]
        [TestCase("string.gsub('abc', '(b)', setmetatable({}, {__index = function(t, k) return k:upper() end}))",
            "2|string:aBc|number:1", NativeBug)]
        [TestCase("string.gsub('hello world', 'l+', function(s) return #s end)", "2|string:he2o wor1d|number:2", Reference)]
        [TestCase("('x-y'):gsub('%-', '+')", "2|string:x+y|number:1", Reference)]
        public void PatternFunctions_ReturnLuaReferenceResults(string expression, string expected,
            string provenance)
        {
            LuaCsSecureEnvironment env = new();
            LuaState state = env.Create();

            LuaValue[] result = env.RunChunk(state,
                SerializePrelude + CollectGMatch + "return ser(" + expression + ")");

            Assert.AreEqual(expected, result[0].Read<string>(), provenance + ": " + expression);
        }

        [TestCase("string.find('abc', '%')", "malformed pattern (ends with '%')", Reference)]
        [TestCase("string.find('abc', '[a')", "malformed pattern (missing ']')", Reference)]
        [TestCase("string.find('abc', '%f')", "missing '[' after '%f' in pattern", NativeBug)]
        [TestCase("string.find('abc', '%1')", "invalid capture index %1", Reference)]
        [TestCase("string.match('abc', 'a)')", "invalid pattern capture", Reference)]
        [TestCase("string.find('abc', '%b')", "malformed pattern (missing arguments to '%b')", Reference)]
        [TestCase("string.match(string.rep('a', 40), string.rep('(a)', 33))", "too many captures", Reference)]
        [TestCase("string.find(string.rep('a', 300), string.rep('a?', 300))", "pattern too complex", Luau)]
        [TestCase("string.find('abc', '(')", "unfinished capture", NativeBug)]
        [TestCase("string.gsub('abc', '%w', '%2')", "invalid capture index", NativeBug)]
        [TestCase("string.gsub('abc', '%w', '%a')", "invalid use of '%' in replacement string", Luau)]
        [TestCase("string.gsub('abc', '.', {a = 1, b = true})", "invalid replacement value (a boolean)", Reference)]
        [TestCase("string.gsub('abc', 'b', nil)", "string/function/table expected", Reference)]
        public void PatternFunctions_RaiseLuaReferenceErrors(string expression, string expectedError,
            string provenance)
        {
            LuaCsSecureEnvironment env = new();
            LuaState state = env.Create();

            LuaValue[] result = env.RunChunk(state,
                "local ok, err = pcall(function() return " + expression + " end)\n" +
                "return tostring(ok) .. '|' .. tostring(err)");

            string outcome = result[0].Read<string>();
            StringAssert.StartsWith("false|", outcome, provenance + ": " + expression);
            StringAssert.Contains(expectedError, outcome, provenance + ": " + expression);
        }

        [Test]
        [Timeout(60000)]
        public void PatternFind_CatastrophicBacktracking_IsRefusedWithThePatternStepBudgetError()
        {
            LuaCsSecureEnvironment env = new();
            LuaState state = env.Create();

            // WHY: the audit's exact case. One find call backtracks about n^4.6 times, and the native matcher ran it
            // to completion inside ONE VM instruction (3.3 s at n = 120, returning nil), where no guard hook
            // can interrupt it. The budgeted matcher must refuse it, naming the budget and the function.
            LuaRuntimeException ex = Assert.Throws<LuaRuntimeException>(() =>
                env.RunChunk(state, "return string.rep('a', 120):find('.-.-.-.-b')"));

            StringAssert.Contains(LuaCsSecureEnvironment.PatternStepBudgetTripMarker, ex.Message);
            StringAssert.Contains("string.find", ex.Message);
            StringAssert.Contains(LuaCsSecureEnvironment.MaxPatternMatchSteps.ToString(), ex.Message);
            // WHY: the scheduler classifies a budget error raised outside the thread's own hook by this
            // text, so it must survive for the kill to read BUDGET_EXCEEDED instead of a Lua bug.
            StringAssert.Contains("resume exceeded", ex.Message);
        }

        [Test]
        public void PatternMatcher_StepBudget_StopsACatastrophicPatternAtExactlyOneStepPastTheBudget()
        {
            LuaCsSecureEnvironment env = new();
            LuaState state = env.Create();
            const long budget = 100_000;

            // WHY: without a budget this 40-char subject needs far more steps than the budget (asserted below),
            // so the only way the call can stop at budget + 1 is the step counter itself.
            LuaCsSecureEnvironment.LuaPatternMatcher bounded =
                new(state, "string.find", new string('a', 40), ".-.-.-.-b", budget);
            Assert.Throws<LuaRuntimeException>(() => FindFromEveryStart(bounded, 40));
            Assert.AreEqual(budget + 1, bounded.Steps,
                "the matcher must stop on the first step past its budget, not finish the backtracking");

            LuaCsSecureEnvironment.LuaPatternMatcher unbounded =
                new(state, "string.find", new string('a', 40), ".-.-.-.-b", long.MaxValue);
            Assert.AreEqual(-1, FindFromEveryStart(unbounded, 40),
                "with no budget the same search completes and finds nothing");
            Assert.Greater(unbounded.Steps, budget * 5,
                "the case must genuinely need far more steps than the budget, or the trip proves nothing");

            // WHY: negative twin. A small subject finishes well inside the budget with the right answer.
            LuaCsSecureEnvironment.LuaPatternMatcher small =
                new(state, "string.find", "aaab", ".-.-.-.-b", budget);
            Assert.AreEqual(4, FindFromEveryStart(small, 4));
            Assert.Less(small.Steps, budget);
        }

        private static int FindFromEveryStart(LuaCsSecureEnvironment.LuaPatternMatcher matcher, int subjectLength)
        {
            for (int start = 0; start <= subjectLength; start++)
            {
                matcher.Reset();
                int end = matcher.Match(start, 0);
                if (end >= 0)
                {
                    return end;
                }
            }

            return -1;
        }

        [Test]
        [Timeout(60000)]
        public void PatternGsubAndGmatch_CatastrophicBacktracking_AreRefusedAndNameTheFunction()
        {
            LuaCsSecureEnvironment env = new();
            LuaState state = env.Create();

            LuaRuntimeException gsub = Assert.Throws<LuaRuntimeException>(() =>
                env.RunChunk(state, "return (string.rep('a', 120):gsub('.-.-.-.-b', 'x'))"));
            StringAssert.Contains(LuaCsSecureEnvironment.PatternStepBudgetTripMarker, gsub.Message);
            StringAssert.Contains("string.gsub", gsub.Message);

            LuaRuntimeException gmatch = Assert.Throws<LuaRuntimeException>(() =>
                env.RunChunk(state, "for w in string.rep('a', 120):gmatch('.-.-.-.-b') do end"));
            StringAssert.Contains(LuaCsSecureEnvironment.PatternStepBudgetTripMarker, gmatch.Message);
            StringAssert.Contains("string.gmatch", gmatch.Message);
        }

        [Test]
        public void PatternFunctions_LinearWorkOnALargeSubject_StaysInsideTheBudget()
        {
            LuaCsSecureEnvironment env = new();
            LuaState state = env.Create();

            // WHY: negative twin of the budget tests. Ordinary linear work over a MaxStringRepLength-class subject
            // must not be refused. Each call here needs 1-2 million steps.
            LuaValue[] result = env.RunChunk(state,
                "local s = string.rep('ab cd  ', 100000)\n" +
                "local collapsed, n = s:gsub('%s+', ' ')\n" +
                "local trimmed = ('   ' .. string.rep('a', 500000) .. '   '):match('^%s*(.-)%s*$')\n" +
                "local words = 0\n" +
                "for w in s:gmatch('%a+') do words = words + 1 end\n" +
                "return n, #trimmed, words, (s .. 'needle'):find('ne.dle')");

            Assert.AreEqual(200000, (int)result[0].Read<double>(), "gsub replacement count");
            Assert.AreEqual(500000, (int)result[1].Read<double>(), "trimmed length");
            Assert.AreEqual(200000, (int)result[2].Read<double>(), "gmatch word count");
            Assert.AreEqual(700001, (int)result[3].Read<double>(), "find position");
        }

        [Test]
        public void Gsub_ResultOverTheCap_IsRefused_AndAResultAtTheCapIsNot()
        {
            LuaCsSecureEnvironment env = new();
            LuaState state = env.Create();

            // WHY: 30,000 matches x 40 chars = 1,200,000 chars, over MaxStringGsubLength. The native gsub built
            // it (the audit's 1M x 40 variant built 40,000,000 chars, +151 MB, in one call).
            LuaRuntimeException ex = Assert.Throws<LuaRuntimeException>(() =>
                env.RunChunk(state, "return (string.rep('a', 30000):gsub('.', string.rep('b', 40)))"));
            StringAssert.Contains("string.gsub result would exceed " + LuaCsSecureEnvironment.MaxStringGsubLength,
                ex.Message);

            // WHY: 25,000 x 40 is exactly the cap, which is allowed, and the result is complete.
            LuaValue[] atCap = env.RunChunk(state,
                "local r, n = string.rep('a', 25000):gsub('.', string.rep('b', 40)) return #r, n");
            Assert.AreEqual(LuaCsSecureEnvironment.MaxStringGsubLength, (int)atCap[0].Read<double>());
            Assert.AreEqual(25000, (int)atCap[1].Read<double>());
        }

        [Test]
        public void Format_ResultOverTheCap_IsRefused_AndAResultUnderTheCapIsExact()
        {
            LuaCsSecureEnvironment env = new();
            LuaState state = env.Create();

            // WHY: 40 x %s of a 30,000-char string = 1,200,000 chars, over MaxStringFormatResultLength. Only the
            // width/precision fields were capped before, so the native format built it.
            LuaRuntimeException ex = Assert.Throws<LuaRuntimeException>(() =>
                env.RunChunk(state,
                    "local s = string.rep('a', 30000)\n" +
                    "local args = {}\n" +
                    "for i = 1, 40 do args[i] = s end\n" +
                    "return string.format(string.rep('%s', 40), table.unpack(args))"));
            StringAssert.Contains(
                "string.format result would exceed " + LuaCsSecureEnvironment.MaxStringFormatResultLength,
                ex.Message);

            // WHY: a __tostring object counts with the length of the string it returns.
            LuaRuntimeException viaToString = Assert.Throws<LuaRuntimeException>(() =>
                env.RunChunk(state,
                    "local big = setmetatable({}, {__tostring = function() return string.rep('x', 600000) end})\n" +
                    "return string.format('%s%s', big, big)"));
            StringAssert.Contains("string.format result would exceed", viaToString.Message);

            LuaValue[] under = env.RunChunk(state,
                "local s = string.rep('a', 30000)\n" +
                "local args = {}\n" +
                "for i = 1, 30 do args[i] = s end\n" +
                "local r = string.format(string.rep('%s', 30), table.unpack(args))\n" +
                "return #r, r == string.rep('a', 900000)");
            Assert.AreEqual(900000, (int)under[0].Read<double>());
            Assert.IsTrue(under[1].Read<bool>(), "the formatted result must be exact, not truncated");
        }

        [TestCase("string.format('%5d|%-5d|%05d', 42, 42, 42)", "   42|42   |00042")]
        [TestCase("string.format('%.3f', 3.14159)", "3.142")]
        [TestCase("string.format('%s-%s-%s', 'a', 1, true)", "a-1-true")]
        [TestCase("string.format('%x %X %c', 255, 255, 65)", "ff FF A")]
        [TestCase("string.format('%5.1s|%.2s|%-4s|', 'abc', 'hello', 'x')", "    a|he|x   |")]
        [TestCase("string.format('%s', setmetatable({}, {__tostring = function() return 'OBJ' end}))", "OBJ")]
        [TestCase("string.format('[%6s]', setmetatable({}, {__tostring = function() return 'ab' end}))", "[    ab]")]
        [TestCase("string.format('%q', 'a\"b')", "\"a\\\"b\"")]
        [TestCase("string.format('100%%')", "100%")]
        public void Format_StillFormatsThroughTheNativeImplementation(string expression, string expected)
        {
            LuaCsSecureEnvironment env = new();
            LuaState state = env.Create();

            LuaValue[] result = env.RunChunk(state, "return " + expression);

            Assert.AreEqual(expected, result[0].Read<string>(), "Lua expression: " + expression);
        }

        [Test]
        public void Format_YieldInsideToStringOfARawCoroutine_RaisesTheCallBoundaryError_AndTheCoroutineRunsOn()
        {
            LuaCsSecureEnvironment env = new();
            LuaState state = env.Create();

            // WHY (M2-19): the native wrapper waited on the call synchronously, so a yield inside __tostring gave
            // "Operation is not valid due to the current state of the object." after it had already told
            // the resumer the coroutine was suspended, which broke every later yield of that coroutine.
            LuaValue[] result = env.RunChunk(state,
                "local co = coroutine.create(function()\n" +
                "  local obj = setmetatable({}, {__tostring = function() coroutine.yield('inner') return 'X' end})\n" +
                "  local ok, err = pcall(string.format, '%s', obj)\n" +
                "  coroutine.yield(tostring(ok) .. '|' .. tostring(err))\n" +
                "  return 'done'\n" +
                "end)\n" +
                "local ok1, first = coroutine.resume(co)\n" +
                "local ok2, second = coroutine.resume(co)\n" +
                "return tostring(ok1), first, tostring(ok2), second, coroutine.status(co)");

            Assert.AreEqual("true", result[0].Read<string>());
            StringAssert.StartsWith("false|", result[1].Read<string>(),
                "the yield inside __tostring must fail inside string.format, not suspend the coroutine");
            StringAssert.Contains(LuaCsSecureEnvironment.YieldAcrossCallBoundaryMessage, result[1].Read<string>());
            Assert.AreEqual("true", result[2].Read<string>(),
                "the coroutine must still yield and resume normally after the refused yield");
            Assert.AreEqual("done", result[3].Read<string>());
            Assert.AreEqual("dead", result[4].Read<string>());
        }
    }
}
#endif
