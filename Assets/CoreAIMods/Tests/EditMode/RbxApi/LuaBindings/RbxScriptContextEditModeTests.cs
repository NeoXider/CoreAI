using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Infrastructure.Logging;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Sandbox.LuaCs;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.LuaBindings
{
    /// <summary>
    /// <c>game:GetService("ScriptContext")</c> and <c>ScriptContext:SetTimeout(seconds)</c>:
    /// mirrors <c>ScriptContext.yaml</c> — <c>security: PluginSecurity</c>, <c>properties: []</c> — so
    /// an ordinary mod is refused, the composition-issued unrestricted (host) actor succeeds and moves
    /// the live <see cref="CoreAI.Sandbox.LuaCs.LuaCsCoroutineBudgetSettings"/> wall-clock half, and no
    /// readable <c>Timeout</c> property was invented.
    /// </summary>
    [TestFixture]
    public sealed class RbxScriptContextEditModeTests
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

        private sealed class MemoryStore : ILuaModStore
        {
            private readonly Dictionary<(string ModId, string Key), string> _values = new();

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
                List<(string ModId, string Key)> keys = new();
                foreach ((string storedModId, string key) in _values.Keys)
                {
                    if (storedModId == modId)
                    {
                        keys.Add((storedModId, key));
                    }
                }

                foreach ((string ModId, string Key) key in keys)
                {
                    _values.Remove(key);
                }
            }
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

        private static LuaCsModStack BuildStack(LuaCsRbxApiBindings roblox, MemoryStore store)
        {
            return LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new FakeGameLogger(),
                ModStore = store,
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                RbxApi = roblox,
                // WHY: production composition (CoreAiModsInstaller) always threads rbxApi.CoroutineResumeBudget
                // in here too, so the mod-stack's own IScriptEngine — which hardens a mod-created RAW
                // coroutine.resume (LuaCsSecureEnvironment.HardenCoroutineLibrary) — shares the SAME live
                // settings object ScriptContext:SetTimeout mutates, instead of silently falling back to an
                // independent default. Without this a test proving SetTimeout reaches raw coroutine.resume
                // would prove nothing: the two objects would just never be the same one.
                CoroutineResumeBudget = roblox.CoroutineResumeBudget
            });
        }

        /// <summary>
        /// Composed instruction cap for the tests below that assert on the WALL-CLOCK bound a tightened
        /// <c>ScriptContext:SetTimeout</c> names. Same magnitude and reasoning as the override fixture in
        /// <c>RbxHeartbeatBudgetKillEditModeTests</c>.
        /// </summary>
        private const int WallClockDominantStepBudget = 100_000_000;

        /// <summary>
        /// Bindings whose composed step cap is far above what a bare loop can burn inside any wall-clock
        /// bound these tests name, with the wall-clock half left at the untouched 500 ms default so a
        /// later <c>SetTimeout(0.05)</c> is a genuine tightening. WHY: <c>ScriptContext:SetTimeout</c>
        /// moves only the wall-clock half of the live budget by design (see
        /// <see cref="LuaCsCoroutineBudgetSettings"/>); the composition-only step cap is independent, and
        /// at its 10,000-step default an empty <c>while true do end</c> trips THAT cap within a couple of
        /// milliseconds — long before any 50 ms clock — so a test asserting on the clock's bound could
        /// never observe it, and its timing assertion could not tell a live budget from a frozen one.
        /// </summary>
        private static LuaCsRbxApiBindings NewBindingsWhereTheWallClockIsTheBindingBound()
        {
            return new LuaCsRbxApiBindings(coroutineResumeBudget: new LuaCsCoroutineBudgetSettings(
                WallClockDominantStepBudget, LuaCsCoroutineHandle.DefaultResumeTimeoutMs));
        }

        [Test]
        public void ScriptContext_ServiceResolvesByExactMirrorClassName()
        {
            LuaCsRbxApiBindings roblox = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);
            stack.Runtime.LoadMod("m", @"
                local sc = game:GetService('ScriptContext')
                store_set('class', sc.ClassName)");

            Assert.AreEqual("ScriptContext", store.Get("m", "class"));
        }

        [Test]
        public void ScriptContext_SetTimeout_RefusedForAnOrdinaryNonHostMod()
        {
            LuaCsRbxApiBindings roblox = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);
            // WHY LoadModForActor and not LoadMod: LoadMod attributes the mod to the composition-issued
            // HOST actor (ownerHasHostAuthority = true internally), which is exactly the elevated tier
            // this test must NOT have. LoadModForActor attributes it to an explicit, ordinary actor id
            // instead, giving Grants.IsUnrestricted = false — an actual non-elevated mod, not a
            // relabelled host call.
            stack.Runtime.LoadModForActor("m", @"
                local ok, err = pcall(function()
                    game:GetService('ScriptContext'):SetTimeout(1)
                end)
                store_set('ok', tostring(ok))
                store_set('err', tostring(err))", "actor-ordinary");

            // WHY lowercase: these fixtures compare what Lua's own tostring() produced, and Lua spells
            // its booleans "true"/"false" — not C#'s Boolean.ToString().
            Assert.AreEqual("false", store.Get("m", "ok"),
                "an ordinary (non-unrestricted) mod must be refused ScriptContext:SetTimeout");
            string message = store.Get("m", "err");
            // Same refusal shape as LuaCsRbxModContext.RequireNetworkSide (RbxErrorCode.NotAuthority,
            // "actor '<id>' cannot use <member> because ..."), with Grants.IsUnrestricted as the gate —
            // see LuaCsRbxModContext.RequireUnrestricted.
            StringAssert.Contains("NOT_AUTHORITY", message);
            StringAssert.Contains("ScriptContext:SetTimeout", message);
            StringAssert.Contains("actor-ordinary", message);

            // Refused before any effect: the live budget must be untouched.
            Assert.AreEqual(LuaCsCoroutineHandle.DefaultResumeTimeoutMs,
                roblox.CoroutineResumeBudget.ResumeTimeoutMs);
        }

        [Test]
        public void ScriptContext_SetTimeout_SucceedsForTheHostAndMovesTheLiveResumeBudget()
        {
            LuaCsRbxApiBindings roblox = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);
            // WHY LoadMod (not LoadModForActor): the composition-issued host actor is exactly
            // Grants.IsUnrestricted = true — the case RequireUnrestricted must let through.
            stack.Runtime.LoadMod("m", @"
                local ok = pcall(function()
                    game:GetService('ScriptContext'):SetTimeout(0.05)
                end)
                store_set('ok', tostring(ok))");

            Assert.AreEqual("true", store.Get("m", "ok"));
            Assert.AreEqual(50, roblox.CoroutineResumeBudget.ResumeTimeoutMs,
                "SetTimeout(0.05) must move the live wall-clock half to 50 ms (seconds -> ms)");
        }

        [Test]
        [Timeout(15000)]
        public void ScriptContext_SetTimeout_AlsoReachesTheOneOffExecuteLuaSurface()
        {
            // WHY this is a separate surface and not covered by the mod-runtime tests above: the one-off
            // execute_lua executor builds its OWN IScriptEngine over the same sandbox. It used to default
            // that engine's budget, so it held a private settings object nothing could reach: a host that
            // tightened the budget constrained every loaded mod and left admin/AI-issued chunks running on
            // the untouched default. Identity on the exposed property is kept because a private object
            // would compare unequal however similar its values — but the property is not the execution
            // path, so a chunk is also RUN through the surface: a state built over a fresh engine with a
            // private budget would keep the property identical and still let the chunk escape.
            LuaCsRbxApiBindings roblox = NewBindingsWhereTheWallClockIsTheBindingBound();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);

            Assert.AreSame(roblox.CoroutineResumeBudget, stack.ToolExecutor.CoroutineResumeBudget,
                "the one-off executor must arm the SAME live budget object as the persistent runtime");

            stack.Runtime.LoadMod("host", "game:GetService('ScriptContext'):SetTimeout(0.05)");
            Assert.AreEqual(50, stack.ToolExecutor.CoroutineResumeBudget.ResumeTimeoutMs,
                "a host's SetTimeout must move the bound the one-off surface arms on its next resume too");

            // WHY a raw coroutine and not a bare top-level loop: the one-off chunk itself runs under the
            // one-shot hard limit (LuaCsSecureEnvironment.OneShotHardLimitSteps, 10 s), not under the
            // per-resume budget; that budget binds the resumes INSIDE it, and the raw-coroutine guard the
            // executor's state arms derives its bound from the live setting
            // (LuaCsSecureEnvironment.RawCoroutineResumeTimeoutMultiplier = 2, so 50 ms -> 100 ms). The
            // sync-over-async bridge is the same one the sibling one-off fixtures use, safe because this
            // fixture detaches Unity's SynchronizationContext in SetUp.
            Stopwatch stopwatch = Stopwatch.StartNew();
            LuaTool.LuaResult result = stack.ToolExecutor
                .ExecuteAsync(@"
                    local co = coroutine.create(function()
                        while true do end
                    end)
                    local ok, err = coroutine.resume(co)
                    return tostring(ok) .. '|' .. tostring(err)", CancellationToken.None)
                .GetAwaiter().GetResult();
            stopwatch.Stop();

            Assert.IsTrue(result.Success,
                "the chunk must complete; only the runaway coroutine inside it is cut — error was: " + result.Error);
            StringAssert.StartsWith("false|", result.Output,
                "the runaway raw coroutine must be cut, not run to completion — output was: " + result.Output);
            StringAssert.Contains("exceeded 100 ms", result.Output,
                "the cut must name the bound derived from the tightened 50 ms live setting, not the "
                + "untouched default's 1,000 ms — output was: " + result.Output);
            Assert.Less(stopwatch.ElapsedMilliseconds, 500,
                "cut at " + stopwatch.ElapsedMilliseconds + " ms is too slow to have used a bound derived "
                + "from the tightened live setting through the one-off surface");
        }

        [Test]
        public void ScriptContext_HasNoReadableTimeoutProperty()
        {
            // WHY: ScriptContext.yaml pins properties: [] — SetTimeout is write-only by the mirror's
            // own design, and this codebase must not invent a scriptable readback Roblox never shipped.
            LuaCsRbxApiBindings roblox = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);
            stack.Runtime.LoadMod("m", @"
                local ok, err = pcall(function()
                    return game:GetService('ScriptContext').Timeout
                end)
                store_set('ok', tostring(ok))
                store_set('err', tostring(err))");

            Assert.AreEqual("false", store.Get("m", "ok"),
                "reading ScriptContext.Timeout must fail exactly like any other unknown member read");
            StringAssert.Contains("not a valid member", store.Get("m", "err"));
        }

        /// <summary>Runs one host frame — a single scheduler Advance, which fires Heartbeat from its own
        /// phase and drains the queued invocation in the same call. Mirrors the sibling budget-kill
        /// fixture's helper and the production frame driver, which also advances the scheduler and pumps
        /// nothing itself; pumping as well would run the frame twice.</summary>
        private static void PumpHeartbeat(LuaCsRbxApiBindings roblox, double scaledDt)
        {
            roblox.Scheduler.Advance(scaledDt);
        }

        [Test]
        public void ScriptContext_SetTimeout_ReachesAnAlreadyWarmedPooledSignalRunner()
        {
            // WHY this test exists: ScriptContext_SetTimeout_SucceedsForTheHostAndMovesTheLiveResumeBudget
            // above only checks that roblox.CoroutineResumeBudget.ResumeTimeoutMs itself changed — it never
            // resumes an already-constructed coroutine handle again afterwards. A LuaCsCoroutineHandle (or
            // LuaCsRbxSignalRunner) that froze its budget at CONSTRUCTION instead of re-reading it on every
            // Resume() would pass that test just as well, which is exactly the regression this configurable-
            // budget work exists to prevent. This test warms a POOLED signal runner first (so its handle
            // already exists, built at the OLD default budget), tightens the timeout only afterwards, then
            // resumes that SAME runner again — proving live propagation reaches a handle that predates the
            // change, not merely a freshly constructed one.
            LuaCsRbxApiBindings roblox = NewBindingsWhereTheWallClockIsTheBindingBound();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);
            stack.Runtime.LoadMod("m", @"
                local rs = game:GetService('RunService')
                local warmed = false
                rs.Heartbeat:Connect(function(dt)
                    if not warmed then
                        warmed = true
                        store_set('warm', 'ok')
                        return
                    end
                    while true do end
                end)");

            // First fire: well-behaved, returns without yielding — warms the pooled runner's coroutine
            // handle at the untouched default budget (500 ms) and parks it back in the idle pool.
            PumpHeartbeat(roblox, 0.1d);
            Assert.AreEqual("ok", store.Get("m", "warm"), "the first fire must complete normally");
            Assert.AreEqual(1, roblox.SchedulerThreadFactory.SignalRunnersCreated,
                "exactly one pooled runner must have been built so far");

            // Tighten AFTER the runner is warmed and parked, via a SEPARATE host script — not nested
            // inside the connected handler's own resume, so this is a genuine "later" live change.
            stack.Runtime.LoadMod("host", "game:GetService('ScriptContext'):SetTimeout(0.05)");
            Assert.AreEqual(50, roblox.CoroutineResumeBudget.ResumeTimeoutMs,
                "SetTimeout(0.05) must have moved the live wall-clock half to 50 ms");

            List<string> errors = new();
            stack.Runtime.ModHandlerErrored += (modId, message, streak) => errors.Add(message);

            // Second fire: the SAME connected Lua closure now takes the runaway branch. If the pooled
            // runner's handle re-reads the live settings on this resume (the fix), it is cut near the NEW
            // 50 ms bound. If it had frozen its budget at construction (the regression), it would instead
            // run for the OLD 500 ms default before being cut.
            Stopwatch stopwatch = Stopwatch.StartNew();
            PumpHeartbeat(roblox, 0.1d);
            stopwatch.Stop();

            Assert.AreEqual(1, errors.Count, "the runaway second fire must be cut exactly once");
            Assert.AreEqual(1, roblox.SchedulerThreadFactory.SignalRunnersCreated,
                "the SAME pooled runner (built before the tightening) must have served this fire — a " +
                "second created runner would mean this test accidentally proved nothing about liveness");
            StringAssert.Contains("exceeded 50 ms", errors[0],
                "the guard message must name the NEW 50 ms bound, not the OLD 500 ms default the handle " +
                "was constructed with — message was: " + errors[0]);
            // Tight enough that the old frozen-at-construction 500 ms default would fail it, generous
            // enough to absorb ordinary jitter around the real ~50 ms target (same reasoning as the
            // override-honoring fixture in RbxHeartbeatBudgetKillEditModeTests).
            Assert.Less(stopwatch.ElapsedMilliseconds, 250,
                "cut at " + stopwatch.ElapsedMilliseconds + " ms is too slow to have used the tightened " +
                "50 ms bound on this already-warmed handle");
        }

        [Test]
        public void ScriptContext_SetTimeout_ReachesAYieldedSchedulerCoroutineHandleBetweenResumes()
        {
            // WHY: covers the non-pooled scheduler path (task.spawn) alongside the pooled signal-runner
            // case above — a coroutine handle parked inside task.wait is resumed again later by the
            // scheduler, and that later resume must also observe a timeout tightened while it was
            // suspended, not the budget frozen when task.spawn first created it.
            LuaCsRbxApiBindings roblox = NewBindingsWhereTheWallClockIsTheBindingBound();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);
            stack.Runtime.LoadMod("m", @"
                task.spawn(function()
                    store_set('phase', 'waiting')
                    task.wait(0.05)
                    while true do end
                end)");

            // task.spawn runs synchronously up to task.wait's yield as part of LoadMod itself.
            Assert.AreEqual("waiting", store.Get("m", "phase"));

            // Tighten while the thread is parked inside task.wait, via a separate host script.
            stack.Runtime.LoadMod("host", "game:GetService('ScriptContext'):SetTimeout(0.05)");
            Assert.AreEqual(50, roblox.CoroutineResumeBudget.ResumeTimeoutMs);

            List<string> errors = new();
            stack.Runtime.ModHandlerErrored += (modId, message, streak) => errors.Add(message);

            // Advancing scaled time past the 0.05 s wait resumes the SAME coroutine handle, which then
            // immediately runs the runaway loop on this second resume.
            Stopwatch stopwatch = Stopwatch.StartNew();
            roblox.Scheduler.Advance(0.06d);
            stopwatch.Stop();

            Assert.AreEqual(1, errors.Count,
                "the runaway loop after task.wait must be cut exactly once on the resumed thread");
            StringAssert.Contains("exceeded 50 ms", errors[0],
                "the guard message must name the tightened 50 ms bound on this already-suspended " +
                "handle's next resume — message was: " + errors[0]);
            Assert.Less(stopwatch.ElapsedMilliseconds, 250,
                "cut at " + stopwatch.ElapsedMilliseconds + " ms is too slow to have used the tightened " +
                "50 ms bound on this already-suspended handle");
        }

        [Test]
        public void ScriptContext_SetTimeout_ReachesARawCoroutineCreatedBeforeTheCall()
        {
            // WHY: closes the raw-coroutine escape hatch (a separate defect from the two tests above,
            // which cover the C#-managed LuaCsCoroutineHandle only): a mod-created coroutine driven
            // directly through native coroutine.create/coroutine.resume runs on a CHILD LuaState that
            // LuaCsSecureEnvironment guards independently (HardenCoroutineLibrary). That guard used to arm
            // fixed constants that never read the live settings, so a mod could dodge a just-tightened
            // ScriptContext:SetTimeout by moving its runaway loop into a raw child coroutine instead of a
            // scheduler-managed thread. The coroutine here is created BEFORE SetTimeout runs and resumed
            // AFTER, in the same script, proving the guard armed on ITS resume — not on the state's
            // creation — reads the live value.
            LuaCsRbxApiBindings roblox = NewBindingsWhereTheWallClockIsTheBindingBound();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);

            Stopwatch stopwatch = Stopwatch.StartNew();
            stack.Runtime.LoadMod("m", @"
                local co = coroutine.create(function()
                    while true do end
                end)
                game:GetService('ScriptContext'):SetTimeout(0.05)
                local ok, err = coroutine.resume(co)
                store_set('ok', tostring(ok))
                store_set('err', tostring(err))");
            stopwatch.Stop();

            Assert.AreEqual(50, roblox.CoroutineResumeBudget.ResumeTimeoutMs,
                "SetTimeout(0.05) must have moved the live wall-clock half to 50 ms");
            Assert.AreEqual("false", store.Get("m", "ok"),
                "the runaway raw coroutine must be cut, not run to completion");
            // The raw-coroutine guard derives its bound from the live settings via a fixed multiplier
            // (LuaCsSecureEnvironment.RawCoroutineResumeTimeoutMultiplier = 2), so 50 ms live -> 100 ms
            // here — nowhere near the OLD fixed 1,000 ms constant this guard used to arm unconditionally.
            StringAssert.Contains("exceeded 100 ms", store.Get("m", "err"),
                "the raw-coroutine guard must name a bound derived from the tightened 50 ms live setting " +
                "(100 ms), not the old untracked 1,000 ms constant — message was: " + store.Get("m", "err"));
            Assert.Less(stopwatch.ElapsedMilliseconds, 500,
                "cut at " + stopwatch.ElapsedMilliseconds + " ms is too slow to have used a bound derived " +
                "from the tightened live setting instead of the old fixed 1,000 ms constant");
        }
    }
}
