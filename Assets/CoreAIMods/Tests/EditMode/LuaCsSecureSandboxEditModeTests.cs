#if COREAI_LUA
using System.Collections.Generic;
using System.Threading;
using CoreAI.Ai.LuaCs;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Sandbox.LuaCs;
using CoreAI.Scripting;
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
            LuaRuntimeException ex = Assert.Throws<LuaCsHostFunctionException>(() =>
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

            LuaRuntimeException ex = Assert.Throws<LuaCsHostFunctionException>(() =>
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
            LuaRuntimeException ex = Assert.Throws<LuaCsHostFunctionException>(() =>
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
        public void IsMemoryBudgetTrip_FollowsTheCauseAHostFunctionErrorCarries()
        {
            // WHY: a host function's Lua error keeps its cause in HostException, not InnerException, so the
            // classifier must step through it or a trip that crossed a host function would read as a Lua bug.
            System.Exception crossed = new LuaCsHostFunctionException(null, "mods_call: budget",
                new System.InvalidOperationException("wrapped",
                    new LuaMemoryBudgetException(
                        $"LuaCsSecureEnvironment: {LuaCsExecutionGuard.MemoryBudgetTripMarker} (268435456 bytes)")));
            Assert.IsTrue(LuaCsExecutionGuard.IsMemoryBudgetTrip(crossed),
                "A memory trip behind a host function error must still be recognised by its type.");

            Assert.IsFalse(LuaCsExecutionGuard.IsMemoryBudgetTrip(
                    new LuaCsHostFunctionException(null,
                        $"forged {LuaCsExecutionGuard.MemoryBudgetTripMarker}",
                        new System.InvalidOperationException(LuaCsExecutionGuard.MemoryBudgetTripMarker))),
                "A host function error whose cause is no trip must not be classified as one, whatever its text.");
            Assert.IsFalse(LuaCsExecutionGuard.IsMemoryBudgetTrip(
                    new LuaCsHostFunctionException(null, "no cause", null)),
                "A host function error without a cause is not a memory trip.");
        }

        [Test]
        public void NeutralIsMemoryBudgetTrip_FollowsTheCauseAHostFunctionErrorCarries()
        {
            // WHY: the mod runtime labels a handler failure through the engine-neutral classifier, which walked
            // InnerException only. A host function error - every guard trip included - keeps its cause in
            // HostException, so that walk stopped at it and a real memory trip behind one was labelled a
            // plain error.
            System.Exception crossed = new LuaCsHostFunctionException(null, "mods_call: budget",
                new System.InvalidOperationException("wrapped",
                    new LuaMemoryBudgetException(
                        $"LuaCsSecureEnvironment: {LuaCsExecutionGuard.MemoryBudgetTripMarker} (268435456 bytes)")));
            Assert.IsTrue(ScriptExecutionErrors.IsMemoryBudgetTrip(crossed),
                "A memory trip behind a host function error must be recognised by its type through the neutral walker.");
            Assert.IsTrue(ScriptExecutionErrors.IsMemoryBudgetTrip(
                    new System.InvalidOperationException("outer", crossed)),
                "An ordinary wrapper around that error must not hide it either.");

            Assert.IsFalse(ScriptExecutionErrors.IsMemoryBudgetTrip(
                    new LuaCsHostFunctionException(null,
                        $"forged {LuaCsExecutionGuard.MemoryBudgetTripMarker}",
                        new System.InvalidOperationException(LuaCsExecutionGuard.MemoryBudgetTripMarker))),
                "A host function error whose cause is no trip must not be classified as one, whatever its text.");
            Assert.IsFalse(ScriptExecutionErrors.IsMemoryBudgetTrip(
                    new LuaCsHostFunctionException(null, "no cause", null)),
                "A host function error without a cause is not a memory trip.");
        }

        [Test]
        public void HostFunctionError_IsTheNeutralHostFailure_AndBothCauseWalkersTakeTheSameStep()
        {
            System.InvalidOperationException cause = new("cause", new System.TimeoutException("inner"));
            LuaCsHostFunctionException host = new(null, "api: cause", cause);

            IScriptHostFailure neutral = host;
            Assert.AreSame(cause, neutral.HostException,
                "the engine-neutral view must expose the same cause C# reads from HostException");
            Assert.AreSame(cause, ScriptExecutionErrors.NextCause(host));
            Assert.AreSame(cause, LuaCsHostFunctionException.NextCause(host),
                "the Lua-side walker must step exactly like the neutral one");
            Assert.AreSame(cause.InnerException, ScriptExecutionErrors.NextCause(cause),
                "an ordinary exception still steps through InnerException");
            Assert.AreSame(cause.InnerException, LuaCsHostFunctionException.NextCause(cause));
            Assert.IsNull(ScriptExecutionErrors.NextCause(null));
            Assert.IsNull(LuaCsHostFunctionException.NextCause(null));
        }

        /// <summary>
        /// Lua prelude whose <c>probe(host)</c> calls a failing host function through every protected path a
        /// script has and returns one "ok|type|text" row per path: pcall of the host function itself, pcall of
        /// a Lua function calling it, xpcall's handler argument and a protected <c>coroutine.resume</c>.
        /// </summary>
        private const string ProtectedPathsProbe =
            "local function probe(host)\n" +
            "  local nested = function() return host() end\n" +
            "  local out = {}\n" +
            "  local function add(ok, err)\n" +
            "    out[#out + 1] = tostring(ok) .. '|' .. type(err) .. '|' .. tostring(err)\n" +
            "  end\n" +
            "  add(pcall(host))\n" +
            "  add(pcall(nested))\n" +
            "  add(xpcall(nested, function(e) return e end))\n" +
            "  add(coroutine.resume(coroutine.create(nested)))\n" +
            "  return table.concat(out, '\\n')\n" +
            "end\n";

        private static void AssertEveryProtectedPathGets(string expected, LuaValue[] probeResult)
        {
            string[] rows = probeResult[0].Read<string>().Split('\n');
            string[] paths = { "pcall(host)", "pcall(lua -> host)", "xpcall handler", "coroutine.resume" };
            Assert.AreEqual(paths.Length, rows.Length, string.Join(" / ", rows));
            for (int index = 0; index < paths.Length; index++)
            {
                Assert.AreEqual("false|string|" + expected, rows[index],
                    paths[index] + " must receive exactly the host's error line and nothing wrapped around it");
            }
        }

        [Test]
        public void RbxHostFunctionError_EveryProtectedPathGetsOnlyTheErrorLine_AndCSharpKeepsTheRbxError()
        {
            const string expected = "CONTEXT_VIOLATION: refused on purpose | fix: call it from a loaded mod";
            LuaCsSecureEnvironment env = new();
            LuaCsApiRegistry registry = new();
            registry.RegisterCallback("refuse", LuaCsRbxLua.Fn("refuse",
                _ => throw new RbxError(RbxErrorCode.ContextViolation,
                    "refused on purpose", "call it from a loaded mod")));
            LuaState state = env.Create(registry);

            // WHY: an error built over an inner exception gives pcall that exception's ToString() - CLR type
            // names, the host stack trace and absolute source paths - and gives xpcall and resume nil.
            AssertEveryProtectedPathGets(expected, env.RunChunk(state, ProtectedPathsProbe + "return probe(refuse)"));

            LuaRuntimeException uncaught = Assert.Catch<LuaRuntimeException>(() => env.RunChunk(state, "refuse()"));
            Assert.AreEqual(expected, uncaught.Message, "C# callers keep reading the same line from Message");
            LuaCsHostFunctionException host = uncaught as LuaCsHostFunctionException;
            Assert.IsNotNull(host, "a failing host function must raise the host-function error type");
            RbxError error = host.HostException as RbxError;
            Assert.IsNotNull(error, "the original RbxError must stay reachable from C#");
            Assert.AreEqual(RbxErrorCode.ContextViolation, error.Code,
                "C# must still classify the failure by its code");
        }

        [Test]
        public void RegistryHostFunctionError_EveryProtectedPathGetsOnlyTheNamedMessage_AndCSharpKeepsTheCause()
        {
            System.InvalidOperationException delegateFailure = new("delegate exploded");
            System.InvalidOperationException callbackFailure = new("callback exploded");
            LuaCsSecureEnvironment env = new();
            LuaCsApiRegistry registry = new();
            registry.Register("explode", new System.Func<double>(() => throw delegateFailure));
            registry.RegisterVarArgs("explode_varargs", _ => throw callbackFailure);
            LuaState state = env.Create(registry);

            AssertEveryProtectedPathGets("explode: delegate exploded",
                env.RunChunk(state, ProtectedPathsProbe + "return probe(explode)"));
            AssertEveryProtectedPathGets("explode_varargs: callback exploded",
                env.RunChunk(state, ProtectedPathsProbe + "return probe(explode_varargs)"));

            LuaCsHostFunctionException fromDelegate =
                Assert.Throws<LuaCsHostFunctionException>(() => env.RunChunk(state, "explode()"));
            Assert.AreEqual("explode: delegate exploded", fromDelegate.Message);
            Assert.AreSame(delegateFailure, fromDelegate.HostException,
                "the delegate's own exception, unwrapped from reflection's wrapper, must stay reachable");
            LuaCsHostFunctionException fromCallback =
                Assert.Throws<LuaCsHostFunctionException>(() => env.RunChunk(state, "explode_varargs()"));
            Assert.AreSame(callbackFailure, fromCallback.HostException);
        }

        [Test]
        public void RegistryDelegate_LuaErrorRaisedInside_CrossesTheHostFunctionWithItsOwnErrorValue()
        {
            LuaCsSecureEnvironment env = new();
            LuaCsApiRegistry registry = new();
            LuaState state = null;
            registry.Register("rethrow", new System.Func<double>(() =>
            {
                LuaTable errorObject = new();
                errorObject["code"] = 7d;
                throw new LuaRuntimeException(state, new LuaValue(errorObject), 0);
            }));
            state = env.Create(registry);

            // WHY: reflection wraps whatever the delegate throws, so without unwrapping, a Lua error raised
            // inside one (a nested guarded call's failure) is re-wrapped as a host failure and its error value
            // flattened to text.
            LuaValue[] result = env.RunChunk(state,
                "local ok, err = pcall(rethrow)\n" +
                "return tostring(ok) .. '|' .. type(err) .. '|' .. " +
                "(type(err) == 'table' and string.format('%d', err.code) or tostring(err))");

            Assert.AreEqual("false|table|7", result[0].Read<string>(),
                "the Lua error value must cross the host function unchanged");
        }

        [TestCase("string.rep('x', 2000000)",
            "LuaCsSecureEnvironment: string.rep result would exceed 1000000 chars.")]
        [TestCase("table.concat({'a', true})",
            "invalid value (Boolean) at index 2 in table for 'concat'")]
        [TestCase("table.concat({string.rep('x', 600000), string.rep('y', 600000)})",
            "LuaCsSecureEnvironment: table.concat result would exceed 1000000 chars.")]
        [TestCase("string.format('%99999999d', 1)",
            "LuaCsSecureEnvironment: string.format width/precision exceeds 1000000 chars.")]
        [TestCase("(string.rep('a', 30000):gsub('.', string.rep('b', 40)))",
            "LuaCsSecureEnvironment: string.gsub result would exceed 1000000 chars.")]
        public void SandboxLibraryRefusal_EveryProtectedPathGetsTheSameCleanLine(string call, string expected)
        {
            // WHY: string.rep, table.concat and the string.format width check raised their refusal over an inner
            // InvalidOperationException, so pcall handed the script "System.InvalidOperationException: ..." and
            // xpcall and a protected coroutine.resume handed it nil. The string.gsub result cap was a level-1
            // error object, which pcall alone prefixed with a "[string ...]:line:" position.
            LuaCsSecureEnvironment env = new();
            LuaState state = env.Create();

            AssertEveryProtectedPathGets(expected,
                env.RunChunk(state, ProtectedPathsProbe + "return probe(function() return " + call + " end)"));

            LuaCsHostFunctionException uncaught =
                Assert.Throws<LuaCsHostFunctionException>(() => env.RunChunk(state, "return " + call));
            Assert.AreEqual(expected, uncaught.Message, "C# callers read the same line from Message");
            Assert.AreEqual(expected, uncaught.ErrorObject.ToString(),
                "the error value a protected resume hands its resumer must be the same line");
            AssertIsOnlyTheErrorLine(expected);
        }

        [Test]
        [Timeout(60000)]
        public void PatternStepTrip_EveryProtectedPathGetsTheSameLine()
        {
            // WHY: the pattern-step trip was a level-1 error object, so inside string.gsub pcall alone prepended
            // a "[string ...]:line:" position the error value does not hold, while xpcall and coroutine.resume
            // read the bare line.
            LuaCsSecureEnvironment env = new();
            LuaState state = env.Create();

            string[] rows = env.RunChunk(state,
                    ProtectedPathsProbe
                    + "return probe(function() return (string.rep('a', 120):gsub('.-.-.-.-b', 'x')) end)")[0]
                .Read<string>().Split('\n');

            Assert.AreEqual(4, rows.Length, string.Join(" / ", rows));
            StringAssert.StartsWith(
                "false|string|LuaCsSecureEnvironment: " + LuaCsSecureEnvironment.PatternStepBudgetTripMarker,
                rows[0], "pcall must receive the trip line itself, with nothing in front of it");
            StringAssert.Contains("in string.gsub", rows[0]);
            for (int index = 1; index < rows.Length; index++)
            {
                Assert.AreEqual(rows[0], rows[index], "every protected path must receive the same line");
            }

            AssertIsOnlyTheErrorLine(rows[0]);
        }

        [TestCase("steps")]
        [TestCase("time")]
        [Timeout(60000)]
        public void GuardStepAndTimeTrips_PcallAndXpcallCannotCatchThem_AndTheRunEndsWithTheTripLine(string budget)
        {
            // WHY (security, W6-A): pcall and xpcall used to catch a trip. The guard hook THREW to trip, which
            // leaves Lua-CSharp's in-hook flag set, so the next Lua frame entered was never counted again:
            // `pcall(runaway); pcall(work)` ran `work` unguarded (20M iterations under a 20k step budget, and a
            // `while true` in it hung the host), and xpcall ran its handler after the budget was gone. A trip
            // now ends the run from inside any protected call, with exactly the trip's clean line (the guard
            // once raised it over an inner exception, handing pcall "System.TimeoutException: ...").
            bool steps = budget == "steps";
            string expected = steps
                ? "LuaCsSecureEnvironment: EXCEEDED_HARD_LIMIT_STEPS (20000)"
                : "Lua exceeded 100 ms.";
            string[] calls =
            {
                "record(pcall(runaway))",
                "record(xpcall(runaway, function(e) record('handler', e) return e end))",
                "pcall(runaway)\npcall(work)",
                "xpcall(runaway, echo)\nxpcall(work, echo)",
                "pcall(function() pcall(runaway) end)\nwork()"
            };
            foreach (string call in calls)
            {
                LuaCsExecutionGuard guard = steps
                    ? new LuaCsExecutionGuard(60_000, 20_000, 0)
                    : new LuaCsExecutionGuard(100, 5_000_000_000L, 0);
                List<string> rows = new();

                LuaRuntimeException ended = RunRecordingChunk(
                    "local function runaway() local n = 0 for i = 1, 50000000 do n = n + 1 end return n end\n"
                    + "local function work() local n = 0 for i = 1, 20000000 do n = n + 1 end record('work', n) end\n"
                    + call + "\n"
                    + "record('after')\n"
                    + "return 1",
                    guard, rows);

                CollectionAssert.IsEmpty(rows,
                    call + " must not return to the script, run a handler or run anything after the trip: "
                    + string.Join(" / ", rows));
                Assert.IsNotNull(ended, call + ": the trip must end the run");
                Assert.AreEqual(expected, ended.Message);
                AssertIsOnlyTheErrorLine(ended.Message);
                Assert.AreEqual(expected, ended.ErrorObject.ToString(),
                    "the error value, which a protected coroutine.resume hands its resumer, must be the same line");
                LuaCsHostFunctionException trip = ended as LuaCsHostFunctionException;
                Assert.IsNotNull(trip, "a guard trip must carry its cause for C#: " + ended.GetType().Name);
                Assert.IsInstanceOf(steps ? typeof(System.InvalidOperationException) : typeof(System.TimeoutException),
                    trip.HostException);
                Assert.IsFalse(LuaCsExecutionGuard.IsMemoryBudgetTrip(ended));
                Assert.IsFalse(ScriptExecutionErrors.IsMemoryBudgetTrip(ended));
            }
        }

        /// <summary>
        /// Runs <paramref name="chunk"/> under <paramref name="guard"/> on a fresh state with two host functions
        /// that run no Lua instruction: <c>record(...)</c> adds one "type:value|..." row of its arguments to
        /// <paramref name="rows"/>, and <c>echo(...)</c> returns its arguments (an xpcall handler). Returns the
        /// Lua error that ended the run, or null when the chunk returned.
        /// </summary>
        /// <param name="liveResumeBudget">
        /// The state's raw-coroutine budget settings (see <see cref="LuaCsSecureEnvironment.Create"/>); null keeps
        /// CoreAI's defaults.
        /// </param>
        internal static LuaRuntimeException RunRecordingChunk(string chunk, LuaCsExecutionGuard guard,
            List<string> rows, LuaCsCoroutineBudgetSettings liveResumeBudget = null)
        {
            LuaCsSecureEnvironment env = new();
            // WHY a fresh state per run: its record() writes into this call's rows and nowhere else. Reusing a
            // state after a trip has its own test, UncaughtTrip_LeavesTheStateGuarded_* in
            // LuaCsGuardFrameAndAllocationEditModeTests.
            LuaState state = CreateRecordingState(env, rows, liveResumeBudget);
            try
            {
                env.RunChunk(state, chunk, guard);
                return null;
            }
            catch (LuaRuntimeException ex)
            {
                return ex;
            }
        }

        /// <summary>
        /// A sandboxed state carrying the <c>record(...)</c> and <c>echo(...)</c> host functions described on
        /// <see cref="RunRecordingChunk"/>.
        /// </summary>
        /// <param name="env">The environment that builds the state.</param>
        /// <param name="rows">Where <c>record(...)</c> writes.</param>
        /// <param name="liveResumeBudget">
        /// The state's raw-coroutine budget settings (see <see cref="LuaCsSecureEnvironment.Create"/>); null keeps
        /// CoreAI's defaults.
        /// </param>
        internal static LuaState CreateRecordingState(LuaCsSecureEnvironment env, List<string> rows,
            LuaCsCoroutineBudgetSettings liveResumeBudget = null)
        {
            LuaCsApiRegistry registry = new();
            registry.RegisterCallback("record", (ctx, ct) =>
            {
                List<string> parts = new();
                for (int index = 0; index < ctx.ArgumentCount; index++)
                {
                    LuaValue value = ctx.GetArgument(index);
                    parts.Add(value.Type.ToString().ToLowerInvariant() + ":" + value);
                }

                rows.Add(string.Join("|", parts));
                return new System.Threading.Tasks.ValueTask<int>(ctx.Return());
            });
            registry.RegisterCallback("echo", (ctx, ct) =>
                new System.Threading.Tasks.ValueTask<int>(ctx.Return(ctx.Arguments.ToArray())));
            return env.Create(registry, liveResumeBudget);
        }

        /// <summary>
        /// Raw-coroutine budget settings with CoreAI's default step budget and a wall-clock allowance no host load
        /// reaches: a raw <c>coroutine.resume</c> gets
        /// <see cref="LuaCsSecureEnvironment.RawCoroutineResumeTimeoutMultiplier"/> times 30 s, 60 s, instead of the
        /// default 1 s. For a test whose raw coroutines do work that is not about the time budget and whose guard
        /// already allows its run a minute.
        /// </summary>
        /// <remarks>
        /// WHY: the 1 s default is wall time, and on a loaded host (four cores shared with several test processes)
        /// the 80 MB body of RawCoroutineResume_UnderAResumerWithTheDefaultBudget_KeepsItsEightyMegabytes, about
        /// 0.4 s idle, crossed it and was cut partway with "Lua coroutine resume exceeded 1000 ms.": a budget that
        /// test does not measure, failing it as if the allocation budget had.
        /// </remarks>
        internal static LuaCsCoroutineBudgetSettings UnhurriedRawResumes()
        {
            return new LuaCsCoroutineBudgetSettings(LuaCsCoroutineHandle.DefaultBudgetPerResume, 30_000);
        }

        [Test]
        [Timeout(30000)]
        public void OrdinaryLuaErrors_AreStillCaughtByPcallAndXpcall_UnderTheGuard()
        {
            // WHY the negative twin: only budget trips became uncatchable. A script's own error(), a host
            // function's refusal and xpcall's handler must behave exactly as before, and a run inside its budget
            // must complete.
            List<string> rows = new();
            LuaRuntimeException ended = RunRecordingChunk(
                "record(pcall(error, 'boom', 0))\n" +
                "record(xpcall(function() error('boom', 0) end, function(e) return 'handled ' .. e end))\n" +
                "record(pcall(string.rep, 'x', 2000000))\n" +
                "record('after')\n" +
                "return 1",
                new LuaCsExecutionGuard(2_000, 200_000, 0), rows);

            Assert.IsNull(ended, "a run inside its budget must complete: " + ended?.Message);
            CollectionAssert.AreEqual(new[]
            {
                "boolean:false|string:boom",
                "boolean:false|string:handled boom",
                "boolean:false|string:LuaCsSecureEnvironment: string.rep result would exceed 1000000 chars.",
                "string:after"
            }, rows);
        }

        [TestCase("record(pcall(runaway))", false)]
        [TestCase("record(xpcall(runaway, function(e) record('handler', e) return e end))", false)]
        [TestCase("pcall(runaway)\npcall(work)", false)]
        [TestCase("record(pcall(runaway))", true)]
        [TestCase("record(xpcall(runaway, function(e) record('handler', e) return e end))", true)]
        [TestCase("pcall(runaway)\npcall(work)", true)]
        [Timeout(60000)]
        public void RawCoroutineResumeTrip_CannotBeCaughtInsideTheCoroutine_AndTheResumerGetsTheTripLine(
            string call, bool onALaterResume)
        {
            // WHY: the per-resume hook a mod-created coroutine gets threw to trip, like the guard's, so a pcall
            // inside the coroutine caught it and `pcall(runaway); pcall(work)` then ran `work` with no hook at
            // all - neither the coroutine's (the in-hook flag stayed set) nor the resumer's guard, which never
            // fires on another thread. The coroutine now dies with its trip, and only its resumer, which is
            // not over any budget, receives the line from the protected coroutine.resume as before.
            // WHY a later resume too: Lua-CSharp runs a coroutine body with the token of its FIRST resume for
            // its whole life, so a trip that cancelled only the current resume's token would not reach it.
            List<string> rows = new();
            string yieldFirst = onALaterResume ? "  coroutine.yield('first')\n" : string.Empty;
            string firstResume = onALaterResume ? "record(coroutine.resume(co))\n" : string.Empty;
            LuaRuntimeException ended = RunRecordingChunk(
                "local function runaway() local n = 0 for i = 1, 50000000 do n = n + 1 end return n end\n" +
                "local function work() local n = 0 for i = 1, 20000000 do n = n + 1 end record('work', n) end\n" +
                "local co = coroutine.create(function()\n" +
                yieldFirst +
                call + "\n" +
                "  record('after')\n" +
                "end)\n" +
                firstResume +
                "record(coroutine.resume(co))\n" +
                "record(coroutine.status(co))\n" +
                "return 1",
                new LuaCsExecutionGuard(60_000, 5_000_000_000L, 0), rows);

            Assert.IsNull(ended, "the resumer is inside its own budget and must run on: " + ended?.Message);
            if (onALaterResume)
            {
                Assert.IsNotEmpty(rows);
                Assert.AreEqual("boolean:true|string:first", rows[0], "the first resume stays inside its budget");
                rows.RemoveAt(0);
            }

            Assert.AreEqual(2, rows.Count, "only the resumer may record anything: " + string.Join(" / ", rows));
            StringAssert.StartsWith(
                "boolean:false|string:LuaCsSecureEnvironment: EXCEEDED_COROUTINE_STEP_BUDGET (500000)", rows[0]);
            AssertIsOnlyTheErrorLine(rows[0]);
            Assert.AreEqual("string:dead", rows[1], "a coroutine cut by its budget must be dead");
        }

        [Test]
        [Timeout(60000)]
        public void RawCoroutine_StartedInOneGuardedRun_SurvivesATripInALaterRun_AndResumesInTheNext()
        {
            // WHY: a coroutine body keeps the token of its first resume for life. Were that the guard's own
            // token - pooled with the guard's hook and cancelled by whichever later run trips - one runaway
            // handler would kill every healthy coroutine a sibling handler had started earlier.
            LuaCsSecureEnvironment env = new();
            LuaState state = env.Create();
            LuaCsExecutionGuard guard = new(60_000, 20_000, 0);
            env.RunChunk(state,
                "co = coroutine.create(function() for r = 1, 3 do coroutine.yield(r) end return 'done' end)\n" +
                "assert(coroutine.resume(co))",
                guard);

            Assert.Throws<LuaCsHostFunctionException>(() => env.RunChunk(state, UnboundedLoop, guard));

            LuaValue[] later = env.RunChunk(state,
                "local ok, r = coroutine.resume(co)\n" +
                "return tostring(ok) .. '|' .. string.format('%d', r)",
                guard);
            Assert.AreEqual("true|2", later[0].Read<string>());
        }

        private const string UnboundedLoop = "local n = 0\nwhile true do n = n + 1 end";

        [Test]
        [Timeout(60000)]
        public void CoroutineHandleTrip_CannotBeCaught_EvenWhenTheNextInstructionEntersAMetamethod()
        {
            // WHY: a scheduler thread's hook fires on every instruction, so after a pcall had caught its trip
            // the very next instruction usually re-tripped. Not when that instruction enters a new Lua frame
            // itself: a CONCAT right after the pcall calls __concat, whose frame Lua-CSharp no longer counts
            // once the throwing hook left its in-hook flag set, and 20M iterations ran under a 10k budget.
            LuaCsSecureEnvironment env = new();
            List<string> rows = new();
            LuaState state = CreateRecordingState(env, rows);
            LuaFunction body = env.RunChunk(state,
                "return function()\n" +
                "  local slow = setmetatable({}, { __concat = function()\n" +
                "    local n = 0 for i = 1, 20000000 do n = n + 1 end\n" +
                "    record('metamethod', n)\n" +
                "    return 'x'\n" +
                "  end })\n" +
                "  local joined = slow .. (pcall(function() while true do end end))\n" +
                "  record('after', joined)\n" +
                "end")[0].Read<LuaFunction>();
            LuaCsCoroutineHandle handle = new(state, body, budgetPerResume: 10_000, resumeTimeoutMs: 5_000,
                totalLifetimeSteps: LuaCsCoroutineHandle.UnlimitedLifetimeSteps);

            handle.Resume();

            CollectionAssert.IsEmpty(rows, "nothing after the trip may run: " + string.Join(" / ", rows));
            Assert.IsFalse(handle.LastOk);
            StringAssert.StartsWith("LuaCsCoroutineHandle: EXCEEDED_RESUME_STEP_BUDGET (10000)", handle.LastErrorText);
            AssertIsOnlyTheErrorLine(handle.LastErrorText);
            Assert.AreEqual(LuaCsGuardTripKind.Steps, handle.LastTrip);
            Assert.AreEqual(LuaThreadStatus.Dead, handle.Status, "a budget-cut thread is over");
            Assert.IsTrue(handle.IsFinished);
            Assert.IsFalse(handle.CanResume);
        }

        [Test]
        [Timeout(60000)]
        public void CoroutineHandle_EachResumeKeepsItsOwnBudget_AndATripRetiresTheThread()
        {
            // WHY the negative twin: a trip that ends the thread for good must not turn the per-resume budget
            // into a cumulative one. Three resumes of about 4k steps each fit a 10k budget one by one (12k
            // together would not), and only the fourth, a runaway, is cut.
            LuaCsSecureEnvironment env = new();
            LuaState state = env.Create();
            LuaFunction body = env.RunChunk(state,
                "return function()\n" +
                "  for r = 1, 3 do\n" +
                "    local n = 0\n" +
                "    for i = 1, 2000 do n = n + 1 end\n" +
                "    coroutine.yield(r)\n" +
                "  end\n" +
                "  while true do end\n" +
                "end")[0].Read<LuaFunction>();
            LuaCsCoroutineHandle handle = new(state, body, budgetPerResume: 10_000, resumeTimeoutMs: 5_000,
                totalLifetimeSteps: LuaCsCoroutineHandle.UnlimitedLifetimeSteps);

            for (int resume = 1; resume <= 3; resume++)
            {
                LuaValue[] yielded = handle.Resume();
                Assert.IsTrue(handle.LastOk, "resume " + resume + " is inside its budget: " + handle.LastErrorText);
                Assert.AreEqual(resume, (int)yielded[0].Read<double>());
                Assert.AreEqual(LuaCsGuardTripKind.None, handle.LastTrip);
            }

            handle.Resume();

            Assert.IsFalse(handle.LastOk);
            StringAssert.StartsWith("LuaCsCoroutineHandle: EXCEEDED_RESUME_STEP_BUDGET (10000)", handle.LastErrorText);
            Assert.AreEqual(LuaCsGuardTripKind.Steps, handle.LastTrip);
            Assert.IsTrue(handle.IsFinished);
            Assert.Throws<System.InvalidOperationException>(() => handle.Resume(),
                "a budget-cut thread must never run again");
        }

        /// <summary>
        /// Fails when <paramref name="text"/> carries anything of a CLR exception beyond its message: a type
        /// name, a managed stack frame, an absolute source path or a second line.
        /// </summary>
        internal static void AssertIsOnlyTheErrorLine(string text)
        {
            StringAssert.DoesNotContain("Exception", text, "no CLR exception type name may leak: " + text);
            StringAssert.DoesNotContain("   at ", text, "no managed stack frame may leak: " + text);
            StringAssert.DoesNotContain("/Assets/", text, "no source path may leak: " + text);
            StringAssert.DoesNotContain(":\\", text, "no Windows source path may leak: " + text);
            StringAssert.DoesNotContain("\n", text, "the error must stay one line: " + text);
            StringAssert.DoesNotContain("System.", text, "no CLR type name may leak: " + text);
            StringAssert.DoesNotContain("LuaValueType", text, "no Lua-CSharp value type name may leak: " + text);
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
            LuaRuntimeException ex = Assert.Throws<LuaCsHostFunctionException>(() =>
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
            LuaRuntimeException ex = Assert.Throws<LuaCsHostFunctionException>(() =>
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
            LuaRuntimeException ex = Assert.Throws<LuaCsHostFunctionException>(() =>
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
            Assert.Throws<LuaCsHostFunctionException>(() => FindFromEveryStart(bounded, 40));
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

            LuaRuntimeException gsub = Assert.Throws<LuaCsHostFunctionException>(() =>
                env.RunChunk(state, "return (string.rep('a', 120):gsub('.-.-.-.-b', 'x'))"));
            StringAssert.Contains(LuaCsSecureEnvironment.PatternStepBudgetTripMarker, gsub.Message);
            StringAssert.Contains("string.gsub", gsub.Message);

            LuaRuntimeException gmatch = Assert.Throws<LuaCsHostFunctionException>(() =>
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
            LuaRuntimeException ex = Assert.Throws<LuaCsHostFunctionException>(() =>
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
            LuaRuntimeException ex = Assert.Throws<LuaCsHostFunctionException>(() =>
                env.RunChunk(state,
                    "local s = string.rep('a', 30000)\n" +
                    "local args = {}\n" +
                    "for i = 1, 40 do args[i] = s end\n" +
                    "return string.format(string.rep('%s', 40), table.unpack(args))"));
            StringAssert.Contains(
                "string.format result would exceed " + LuaCsSecureEnvironment.MaxStringFormatResultLength,
                ex.Message);

            // WHY: a __tostring object counts with the length of the string it returns.
            LuaRuntimeException viaToString = Assert.Throws<LuaCsHostFunctionException>(() =>
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

        #region A2 (FX-RT-A): a handle's thread is not raw-resumable; calls back into Lua are depth-capped

        private static readonly string RefusedResumeRow =
            "boolean:false|string:" + LuaCsSecureEnvironment.SchedulerThreadResumeRefusal;

        /// <summary>
        /// Builds a <see cref="LuaCsCoroutineHandle"/> over <paramref name="body"/> (a chunk returning the
        /// thread's function) and resumes it once, so it is parked at its first <c>coroutine.yield</c>.
        /// </summary>
        private static LuaCsCoroutineHandle ParkedHandle(LuaCsSecureEnvironment env, LuaState state, string body)
        {
            LuaFunction function = env.RunChunk(state, body)[0].Read<LuaFunction>();
            LuaCsCoroutineHandle handle = new(state, function, budgetPerResume: 10_000, resumeTimeoutMs: 5_000,
                totalLifetimeSteps: LuaCsCoroutineHandle.UnlimitedLifetimeSteps);
            handle.Resume();
            Assert.IsTrue(handle.LastOk, handle.LastErrorText);
            Assert.AreEqual(LuaThreadStatus.Suspended, handle.Status);
            return handle;
        }

        [Test]
        [Timeout(30000)]
        public void RawCoroutineResume_OfACoroutineHandlesThread_IsRefused_AndOnlyTheHandleResumesIt()
        {
            // WHY (A2-01): a coroutine.running() value taken inside a task thread let the mod resume that thread
            // with coroutine.resume. It then ran with the handle's token under a hook that cancelled another one,
            // so a budget trip there was swallowed by xpcall. The resume is refused before it touches the thread,
            // and the handle still drives it: the value the handle passes is what coroutine.yield receives.
            LuaCsSecureEnvironment env = new();
            List<string> rows = new();
            LuaState state = CreateRecordingState(env, rows);
            LuaCsCoroutineHandle handle = ParkedHandle(env, state,
                "return function()\n" +
                "  record(coroutine.resume(coroutine.running()))\n" +
                "  victim = coroutine.running()\n" +
                "  local got = coroutine.yield('parked')\n" +
                "  record('resumed', got)\n" +
                "  coroutine.yield('again')\n" +
                "end");
            Assert.AreEqual(new[] { RefusedResumeRow }, rows.ToArray(),
                "resuming its own running thread is refused with the same line");
            rows.Clear();

            env.RunChunk(state,
                "record(coroutine.resume(victim, 'raw'))\n" +
                "record(coroutine.status(victim))\n" +
                "local co = coroutine.create(function(a) return a * 2 end)\n" +
                "record(coroutine.resume(co, 21))",
                new LuaCsExecutionGuard(10_000, 5_000_000, 0));

            CollectionAssert.AreEqual(new[] { RefusedResumeRow, "string:suspended", "boolean:true|number:42" }, rows,
                "the handle's thread is refused and untouched; a coroutine.create thread still resumes");
            AssertIsOnlyTheErrorLine(LuaCsSecureEnvironment.SchedulerThreadResumeRefusal);
            rows.Clear();

            LuaValue[] yielded = handle.Resume(new LuaValue("handle"));

            Assert.IsTrue(handle.LastOk, handle.LastErrorText);
            CollectionAssert.AreEqual(new[] { "string:resumed|string:handle" }, rows);
            Assert.AreEqual("again", yielded[0].Read<string>());
        }

        [TestCase("in the xpcall handler",
            "  xpcall(function() while true do end end, function(e)\n" +
            "    local n = 0 for i = 1, 30000000 do n = n + 1 end\n" +
            "    record('work', n)\n" +
            "    return e\n" +
            "  end)\n")]
        [TestCase("in a later frame",
            "  xpcall(function() while true do end end, function(e) return e end)\n" +
            "  local function work() local n = 0 for i = 1, 30000000 do n = n + 1 end record('work', n) end\n" +
            "  work()\n")]
        [Timeout(60000)]
        public void RawCoroutineResume_OfAHandlesThreadThatWouldTrip_CannotRunWorkUnguarded(string where,
            string afterTheYield)
        {
            // WHY (A2-01): through coroutine.resume the runaway below tripped a hook that could only throw, and
            // xpcall, whose own token was not the cancelled one, caught it and ran 30M iterations of work with no
            // hook at all. WHY gated: on a build that lets a raw resume of a handle's thread through, that trip
            // also corrupts the thread and a later kill crashes the test host from a Lua-CSharp continuation
            // (A2-02), so the thread that would trip is only resumed after a harmless one was refused.
            LuaCsSecureEnvironment env = new();
            List<string> rows = new();
            LuaState state = CreateRecordingState(env, rows);
            LuaCsExecutionGuard guard = new(60_000, 5_000_000_000L, 0);
            ParkedHandle(env, state,
                "return function() probe = coroutine.running() coroutine.yield() record('probe ran') end");
            env.RunChunk(state, "record(coroutine.resume(probe))", guard);
            Assert.AreEqual(new[] { RefusedResumeRow }, rows.ToArray(),
                "gate: a raw resume of a handle's thread must be refused before the one that would trip is tried");
            rows.Clear();

            LuaCsCoroutineHandle victim = ParkedHandle(env, state,
                "return function()\n" +
                "  victim = coroutine.running()\n" +
                "  coroutine.yield()\n" +
                afterTheYield +
                "  record('end of body')\n" +
                "end");
            env.RunChunk(state, "record(coroutine.resume(victim))\nrecord(coroutine.status(victim))", guard);

            CollectionAssert.AreEqual(new[] { RefusedResumeRow, "string:suspended" }, rows, where);
            rows.Clear();

            victim.Resume();

            CollectionAssert.IsEmpty(rows, "the handle's own trip must end the thread before any work " + where);
            Assert.IsFalse(victim.LastOk);
            StringAssert.StartsWith("LuaCsCoroutineHandle: EXCEEDED_RESUME_STEP_BUDGET (10000)", victim.LastErrorText);
            Assert.AreEqual(LuaCsGuardTripKind.Steps, victim.LastTrip);
            Assert.AreEqual(LuaThreadStatus.Dead, victim.Status);
        }

        /// <summary>
        /// Global depth bookkeeping shared by the C-stack shapes: <c>enter()</c> counts one Lua level and fails
        /// with "mod cap" at <paramref name="modCap"/>, like a mod's own recursion guard.
        /// </summary>
        private static string CStackPrelude(int modCap)
        {
            return "depth = 0 maxDepth = 0\n" +
                   "local function enter()\n" +
                   "  depth = depth + 1\n" +
                   "  if depth > maxDepth then maxDepth = depth end\n" +
                   "  if depth >= " + modCap + " then error('mod cap', 0) end\n" +
                   "end\n";
        }

        [TestCase("table.sort", 200,
            "local function cmp(a, b) enter() table.sort({3, 1, 2}, cmp) depth = depth - 1 return a < b end\n" +
            "record(pcall(table.sort, {3, 1, 2}, cmp))")]
        [TestCase("string.format", 200,
            "local o = setmetatable({}, {})\n" +
            "getmetatable(o).__tostring = function(x) enter() local r = string.format('%s', x) depth = depth - 1 return r end\n" +
            "record(pcall(string.format, '%s', o))")]
        [TestCase("tostring", 200,
            "local o = setmetatable({}, {})\n" +
            "getmetatable(o).__tostring = function(x) enter() local r = tostring(x) depth = depth - 1 return r end\n" +
            "record(pcall(tostring, o))")]
        [TestCase("print", 200,
            "local o = setmetatable({}, {})\n" +
            "getmetatable(o).__tostring = function(x) enter() print(x) depth = depth - 1 return 'x' end\n" +
            "record(pcall(print, o))")]
        [TestCase("string.gsub", 201,
            "local function g() enter() local r = (string.gsub('a', 'a', g)) depth = depth - 1 return r end\n" +
            "record(pcall(g))")]
        [TestCase("string.gsub", 200,
            "local t = setmetatable({}, {})\n" +
            "getmetatable(t).__index = function() enter() local r = (string.gsub('a', 'a', t)) depth = depth - 1 return r end\n" +
            "record(pcall(string.gsub, 'a', 'a', t))")]
        [TestCase("pairs", 200,
            "local o = setmetatable({}, {})\n" +
            "getmetatable(o).__pairs = function() enter() local a, b, c = pairs(o) depth = depth - 1 return a, b, c end\n" +
            "record(pcall(pairs, o))")]
        [TestCase("ipairs", 200,
            "local o = setmetatable({}, {})\n" +
            "getmetatable(o).__ipairs = function() enter() local a, b, c = ipairs(o) depth = depth - 1 return a, b, c end\n" +
            "record(pcall(ipairs, o))")]
        [TestCase("coroutine.resume", 201,
            "local function f()\n" +
            "  enter()\n" +
            "  local ok, e = coroutine.resume(coroutine.create(f))\n" +
            "  depth = depth - 1\n" +
            "  if not ok then error(e, 0) end\n" +
            "end\n" +
            "record(pcall(f))")]
        [Timeout(120000)]
        public void CallsBackIntoLua_NestedPastTheCStackLimit_FailFastWithOneCatchableLine(string boundary,
            int deepestLuaLevel, string shape)
        {
            // WHY (A2-05): each of these calls runs Lua as a nested VM call on the .NET stack, and an error
            // raised N levels deep unwinds in about N squared with no instruction running, so no budget hook
            // fires: a comparator re-entering table.sort 1,000 deep took 8.1 s to fail under a 10 s budget, a
            // __tostring re-entering string.format 7.4 s, and unbounded the recursion ran for a minute. The
            // library stops the nesting at MaxCCallDepth with Luau's error, which pcall catches like any other.
            // A resume continues its resumer's count, and a function that recurses before its first library call
            // reaches one Lua level more than the count.
            // WHY the reference run: the unwind left at the cap is intrinsic to Lua-CSharp (about 200 squared) and
            // its wall time depends on the host, so the capped run is timed against the same shape failing on its
            // own at 150 levels on the same host. Capped, the ratio is about (200/150)^2 = 1.8; uncapped at 1,000
            // it was about (1000/150)^2 = 44.
            string referenceChunk = CStackPrelude(150) + shape;
            string cappedChunk = CStackPrelude(1000) + shape + "\nrecord(maxDepth)";
            List<string> reference = new();
            long referenceMs = TimeCStackRun(referenceChunk, reference, out LuaRuntimeException referenceEnded);
            Assert.IsNull(referenceEnded, referenceEnded?.Message);
            Assert.AreEqual(new[] { "boolean:false|string:mod cap" }, reference.ToArray(),
                "the reference run fails on the mod's own cap, below the C-stack limit");

            List<string> rows = new();
            long cappedMs = TimeCStackRun(cappedChunk, rows, out LuaRuntimeException ended);

            Assert.IsNull(ended, "the error is an ordinary one that pcall catches: " + ended?.Message);
            Assert.AreEqual(2, rows.Count, string.Join(" / ", rows));
            string expectedLine = LuaCsSecureEnvironment.CStackOverflowMessage + " (" + boundary + ": more than "
                                  + LuaCsSecureEnvironment.MaxCCallDepth
                                  + " nested calls from library functions back into Lua)";
            Assert.AreEqual("boolean:false|string:" + expectedLine, rows[0]);
            AssertIsOnlyTheErrorLine(expectedLine);
            Assert.AreEqual("number:" + deepestLuaLevel, rows[1],
                "the nesting stops at the limit, not at the mod's own cap of 1,000");
            AssertCappedNestingUnwindsLikeItsReference(referenceMs, cappedMs,
                () => RunCStackChunkAgain(referenceChunk, reference),
                () => RunCStackChunkAgain(cappedChunk, rows));
        }

        /// <summary>How many times each C-stack shape is timed; the fastest run of each is what is compared.</summary>
        internal const int CStackTimingRounds = 3;

        /// <summary>
        /// The timing half of every C-stack test: fails unless a nesting stopped at
        /// <see cref="LuaCsSecureEnvironment.MaxCCallDepth"/> unwinds about as fast as a reference run of the same
        /// shape that fails on the mod's own cap below it, i.e. capped &lt; 4 x reference + 250 ms, comparing the
        /// fastest of <see cref="CStackTimingRounds"/> runs of each. <paramref name="referenceMs"/> and
        /// <paramref name="cappedMs"/> time the first run of each, whose outcome the caller has already asserted;
        /// the two actions run their shape once more and fail unless it ends exactly as its first run did, so every
        /// timed run does the same work.
        /// </summary>
        /// <remarks>
        /// WHY the fastest run of each shape: host load only ever adds wall time, so the fastest run is the closest
        /// reading of a shape's own cost. One run of each compared what the scheduler did to two runs: on a loaded
        /// host a stall inside one capped run (440 ms against a 47 ms reference) failed the bound while every other
        /// assertion held. Without the cap every run of the capped shape is slow, not only an unlucky one, so the
        /// fastest still fails the bound: 3.1 s against a 62 ms reference for table.sort, 2.9 s against 58 ms for
        /// string.format. WHY the order alternates: neither shape is always the one that pays for the garbage the
        /// other left behind.
        /// </remarks>
        internal static void AssertCappedNestingUnwindsLikeItsReference(long referenceMs, long cappedMs,
            System.Action runReferenceAgain, System.Action runCappedAgain)
        {
            for (int round = 1; round < CStackTimingRounds; round++)
            {
                bool cappedFirst = round % 2 == 1;
                if (cappedFirst)
                {
                    cappedMs = System.Math.Min(cappedMs, ElapsedMs(runCappedAgain));
                }

                referenceMs = System.Math.Min(referenceMs, ElapsedMs(runReferenceAgain));
                if (!cappedFirst)
                {
                    cappedMs = System.Math.Min(cappedMs, ElapsedMs(runCappedAgain));
                }
            }

            Assert.Less(cappedMs, 4 * referenceMs + 250,
                "the capped nesting must unwind about as fast as the reference shape failing below the limit ("
                + referenceMs + " ms; the fastest of " + CStackTimingRounds
                + " runs of each), not quadratically in the mod's own depth");
        }

        /// <summary>The wall time of one call of <paramref name="run"/>, in milliseconds.</summary>
        internal static long ElapsedMs(System.Action run)
        {
            System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
            run();
            clock.Stop();
            return clock.ElapsedMilliseconds;
        }

        /// <summary>
        /// Runs <paramref name="chunk"/> through <see cref="RunRecordingChunk"/> under a guard only the nesting can
        /// end (a minute, 50M steps, no allocation cap), with <see cref="UnhurriedRawResumes"/>, and returns its
        /// wall time in milliseconds.
        /// </summary>
        private static long TimeCStackRun(string chunk, List<string> rows, out LuaRuntimeException ended)
        {
            System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
            ended = RunRecordingChunk(chunk, new LuaCsExecutionGuard(60_000, 50_000_000, 0), rows,
                UnhurriedRawResumes());
            clock.Stop();
            return clock.ElapsedMilliseconds;
        }

        /// <summary>
        /// Runs <paramref name="chunk"/> once more like <see cref="TimeCStackRun"/> and fails unless it ended exactly
        /// as its first run did, recorded in <paramref name="firstRows"/>.
        /// </summary>
        private static void RunCStackChunkAgain(string chunk, List<string> firstRows)
        {
            List<string> rows = new();
            TimeCStackRun(chunk, rows, out LuaRuntimeException ended);
            Assert.IsNull(ended, ended?.Message);
            CollectionAssert.AreEqual(firstRows, rows, "every timed run of a shape must end the same way");
        }

        private const string NestSort =
            "local function nest(n)\n" +
            "  if n == 0 then return 0 end\n" +
            "  local deepest, done = 0, false\n" +
            "  table.sort({2, 1}, function(a, b)\n" +
            "    if not done then done = true deepest = nest(n - 1) + 1 end\n" +
            "    return a < b\n" +
            "  end)\n" +
            "  return deepest\n" +
            "end\n";

        [Test]
        [Timeout(60000)]
        public void CallsBackIntoLua_UpToTheCStackLimitSucceed_OneMoreIsRefused_AndARefusalReleasesEveryLevel()
        {
            // WHY the negative twin: the cap must cost legitimate code nothing below Luau's own limit, and a
            // refused call must give back every level the calls under it held, or the thread would lose depth
            // with each caught refusal until nothing could call back into Lua at all.
            // WHY unhurried raw resumes: a chain runs inside ONE resume of its outermost coroutine, which the
            // default 1 s wall-clock allowance would cut on a slow or loaded host (a first, JIT-compiling chain
            // of 200 took 0.6 s under load), and the time budget is not what this test measures.
            List<string> rows = new();
            int max = LuaCsSecureEnvironment.MaxCCallDepth;
            LuaRuntimeException ended = RunRecordingChunk(
                NestSort +
                "local function chain(n)\n" +
                "  if n == 0 then return 0 end\n" +
                "  local ok, v = coroutine.resume(coroutine.create(chain), n - 1)\n" +
                "  if not ok then error(v, 0) end\n" +
                "  return v + 1\n" +
                "end\n" +
                "record(pcall(nest, " + max + "))\n" +
                "record(pcall(nest, " + (max + 1) + "))\n" +
                "record(pcall(nest, " + max + "))\n" +
                "record(pcall(chain, " + max + "))\n" +
                "record(pcall(chain, " + (max + 1) + "))\n" +
                "record(pcall(chain, " + max + "))",
                new LuaCsExecutionGuard(30_000, 50_000_000, 0), rows, UnhurriedRawResumes());

            Assert.IsNull(ended, ended?.Message);
            Assert.AreEqual(6, rows.Count, string.Join(" / ", rows));
            Assert.AreEqual("boolean:true|number:" + max, rows[0], max + " nested table.sort calls are allowed");
            StringAssert.StartsWith("boolean:false|string:" + LuaCsSecureEnvironment.CStackOverflowMessage
                                    + " (table.sort:", rows[1]);
            Assert.AreEqual("boolean:true|number:" + max, rows[2], "the refusal released every level");
            Assert.AreEqual("boolean:true|number:" + max, rows[3], max + " nested resumes are allowed");
            StringAssert.StartsWith("boolean:false|string:" + LuaCsSecureEnvironment.CStackOverflowMessage
                                    + " (coroutine.resume:", rows[4]);
            Assert.AreEqual("boolean:true|number:" + max, rows[5]);
        }

        [Test]
        [Timeout(60000)]
        public void CallsBackIntoLua_ASuspendedCoroutineHoldsItsLevelsOnItsOwnThreadOnly()
        {
            // WHY: the count is per Lua thread, not per .NET thread. A coroutine that yields inside a table.sort
            // comparator keeps that call open while it is suspended; a shared count would charge it to every
            // other thread, which on Unity's single main thread means every other mod.
            // WHY unhurried raw resumes: the second resume runs 199 nested sorts inside one raw resume; its default
            // 1 s wall-clock allowance is not what this test measures.
            List<string> rows = new();
            int max = LuaCsSecureEnvironment.MaxCCallDepth;
            LuaRuntimeException ended = RunRecordingChunk(
                NestSort +
                "local co = coroutine.create(function()\n" +
                "  local yielded = false\n" +
                "  table.sort({2, 1}, function(a, b)\n" +
                "    if not yielded then yielded = true coroutine.yield('inside sort') end\n" +
                "    return a < b\n" +
                "  end)\n" +
                "  return nest(" + (max - 1) + ")\n" +
                "end)\n" +
                "record(coroutine.resume(co))\n" +
                "record(pcall(nest, " + max + "))\n" +
                "record(coroutine.resume(co))",
                new LuaCsExecutionGuard(30_000, 50_000_000, 0), rows, UnhurriedRawResumes());

            Assert.IsNull(ended, ended?.Message);
            CollectionAssert.AreEqual(new[]
            {
                "boolean:true|string:inside sort",
                "boolean:true|number:" + max,
                "boolean:true|number:" + (max - 1)
            }, rows, "the suspended sort neither limits the main thread nor stays counted once it finished");
        }

        [Test]
        [Timeout(60000)]
        public void DeepLuaRecursion_AndMetamethodsTheVmRunsItself_AreNotLimitedByTheCStackCap()
        {
            // WHY the negative twin: plain Lua calls and the metamethods the VM dispatches in its own loop do
            // not nest .NET calls, so they keep Lua-CSharp's own, far deeper limits.
            List<string> rows = new();
            LuaRuntimeException ended = RunRecordingChunk(
                "local function f(n) if n == 0 then return 0 end return 1 + f(n - 1) end\n" +
                "record(f(5000))\n" +
                "local o = setmetatable({}, {})\n" +
                "getmetatable(o).__index = function(t, k) if k == 0 then return 0 end return t[k - 1] + 1 end\n" +
                "record(o[5000])\n" +
                "local mt = {}\n" +
                "mt.__lt = function(a, b) if a.n == 0 then return true end return setmetatable({n = a.n - 1}, mt) < b end\n" +
                "record(setmetatable({n = 5000}, mt) < setmetatable({n = 0}, mt))",
                new LuaCsExecutionGuard(30_000, 50_000_000, 0), rows);

            Assert.IsNull(ended, ended?.Message);
            CollectionAssert.AreEqual(new[] { "number:5000", "number:5000", "boolean:true" }, rows);
        }

        #endregion
    }
}
#endif
