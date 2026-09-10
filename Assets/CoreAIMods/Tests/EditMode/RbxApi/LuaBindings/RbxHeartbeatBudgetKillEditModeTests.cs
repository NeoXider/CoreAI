using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
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
    /// Honest end-to-end proof of MVP2 acceptance criterion 14 (roadmap §5.2.9 item 14, ROBLOX_API_ROADMAP.md):
    /// a genuine <c>while true do end</c> inside a <c>RunService.Heartbeat</c> handler is cut by
    /// <see cref="LuaCsCoroutineHandle"/>'s per-resume guard, with <c>BUDGET_EXCEEDED</c> and mod
    /// attribution, while a second well-behaved mod's handler still runs the SAME frame.
    /// </summary>
    /// <remarks>
    /// WHY this file exists: <c>dev-docs/MVP_CLOSURE_AUDIT_2026-09-06.md</c> finding "MVP2 #1" is that
    /// criterion 14 was previously proven only by a test that INJECTS a pre-made
    /// <see cref="RbxErrorCode.BudgetExceeded"/> <see cref="RbxError"/> into a fake scheduler thread
    /// (<c>ModSchedulerEditModeTests.DEV3_BudgetKillTargetsOnlyOwningModAndOtherModRunsSameFrame</c> —
    /// left in place as a legitimate engine-free unit test of <c>ModScheduler</c>'s own kill/orphan
    /// logic, not touched here) rather than ever running a loop through real Lua. Nothing there asserted
    /// the string <c>BUDGET_EXCEEDED</c>, mod/line attribution, or budgets at <c>timeScale = 0</c>, and
    /// the audit records a repo comment claiming the guard only cuts a tight loop after "~8 s" — this
    /// file measures the real bound instead of assuming either claim.
    /// <para>
    /// Scope note: criterion 14 also promises "K consecutive kills quarantine only that mod"; that half
    /// is already covered (via <c>store_set</c> loops, not Heartbeat) by the quarantine fixtures in
    /// <c>LuaCsModRuntimeEditModeTests</c> and is not repeated here.
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class RbxHeartbeatBudgetKillEditModeTests
    {
        private SynchronizationContext _savedContext;

        /// <summary>Same sync-over-async hazard as the sibling RunService/signal-reuse fixtures:
        /// detach Unity's SynchronizationContext so VM continuations complete on the thread pool.</summary>
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

        private const string InfiniteLoopModSource = @"
            local rs = game:GetService('RunService')
            rs.Heartbeat:Connect(function(dt)
                while true do end
            end)";

        private static LuaCsModStack BuildStack(LuaCsRbxApiBindings roblox, MemoryStore store)
        {
            return LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new FakeGameLogger(),
                ModStore = store,
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                RbxApi = roblox
            });
        }

        /// <summary>
        /// Runs one host frame exactly as production does: a single <see cref="ModScheduler.Advance"/>.
        /// The scheduler walks its phase pipeline and <c>LuaCsRbxApiBindings</c> fires Heartbeat from
        /// the Heartbeat phase, so the signal fires once and its queued invocation drains in the same
        /// call. <paramref name="scaledDt"/> is the SCALED per-frame delta, so passing 0 simulates
        /// <c>timeScale = 0</c> — a paused world whose clock never advances.
        /// </summary>
        /// <remarks>
        /// WHY not <c>roblox.PumpHeartbeat(dt)</c> followed by <c>Advance(scaledDt)</c>, as this helper
        /// used to do: that was the pre-merge host shape, when the bindings routed only the input phase.
        /// The bindings now route every phase, so pumping as well runs the frame TWICE — the runaway
        /// handler below was cut once per copy and every "exactly once" assertion saw 2.
        /// </remarks>
        private static void PumpHeartbeat(LuaCsRbxApiBindings roblox, double scaledDt)
        {
            roblox.Scheduler.Advance(scaledDt);
        }

        [Test]
        public void Lua_WhileTrueDoEndInHeartbeatHandler_IsCutWithBudgetExceededAndModAttribution()
        {
            LuaCsRbxApiBindings roblox = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);
            List<(string ModId, string Message)> errors = new();
            stack.Runtime.ModHandlerErrored += (modId, message, streak) => errors.Add((modId, message));
            stack.Runtime.LoadMod("looper", InfiniteLoopModSource);

            PumpHeartbeat(roblox, 0.1d);

            Assert.AreEqual(1, errors.Count,
                "a genuine while-true-do-end handler must be cut exactly once by the guard, not hang " +
                "the test run and not silently succeed");
            Assert.AreEqual("looper", errors[0].ModId,
                "the failure must be attributed to the mod that owns the runaway handler");
            StringAssert.Contains("BUDGET_EXCEEDED", errors[0].Message);
            // WHY a real line, not just the [mod:.. line:0] contract prefix (which is always present
            // and always 0 for a scheduler-thread fault, RbxError's own default): LuaCsCoroutineHandle's
            // guard hook now names the actual currently-executing author line via
            // LuaState.GetTraceback().LastLine at the moment it trips (mirroring how
            // LuaCsRbxValues.WithProductionContext attributes an ordinary API-call error), appended as
            // " at line N" to the raw guard message. This assertion is the one that actually
            // distinguishes real attribution from the fixed "line:0" every RbxError carries by default.
            Match lineMatch = Regex.Match(errors[0].Message, @"at line (\d+)");
            Assert.IsTrue(lineMatch.Success,
                "the guard message must name the author line the loop was cut on — message was: "
                + errors[0].Message);
            Assert.Greater(int.Parse(lineMatch.Groups[1].Value), 0,
                "the attributed line must be a real (positive) source line, not a placeholder");
            Assert.AreEqual(0, roblox.Scheduler.LiveThreadCount,
                "the killed runner must not remain a live scheduler thread");
        }

        [Test]
        public void Lua_WhileTrueDoEndInHeartbeatHandler_FaultsWithTheBudgetExceededCode()
        {
            // WHY a code assertion next to the text assertions above: ModHandlerErrored carries only
            // the formatted message, and "BUDGET_EXCEEDED" in it is merely the wire name the code
            // prints. Quarantine and auto-repair key on the structured RbxError the scheduler raises,
            // so this pins RbxErrorCode.BudgetExceeded itself: a change to the guard's message wording
            // that silently dropped the classification back to BadArgument fails here regardless of
            // what any text check accepts.
            LuaCsRbxApiBindings roblox = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);
            List<(string ModId, RbxError Error)> faults = new();
            roblox.Scheduler.ThreadFaulted += (modId, error) => faults.Add((modId, error));
            stack.Runtime.LoadMod("looper", InfiniteLoopModSource);

            PumpHeartbeat(roblox, 0.1d);

            Assert.AreEqual(1, faults.Count,
                "the runaway handler must fault its scheduler thread exactly once");
            Assert.AreEqual("looper", faults[0].ModId);
            Assert.AreEqual(RbxErrorCode.BudgetExceeded, faults[0].Error.Code,
                "a handler cut by the per-resume guard must classify as a budget kill, not as a Lua " +
                "bug of the mod's own — error was: " + faults[0].Error.Message);
            Assert.AreEqual("looper", faults[0].Error.ModId,
                "the structured error must carry the owning mod, not just the raised message");
        }

        [Test]
        public void Lua_WhileTrueDoEndInHeartbeatHandler_OtherModKeepsRunningSameFrame()
        {
            LuaCsRbxApiBindings roblox = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);
            stack.Runtime.LoadMod("looper", InfiniteLoopModSource);
            stack.Runtime.LoadMod("good", @"
                local rs = game:GetService('RunService')
                local n = 0
                rs.Heartbeat:Connect(function(dt)
                    n = n + 1
                    store_set('n', tostring(n))
                end)");

            PumpHeartbeat(roblox, 0.1d);

            Assert.AreEqual("1", store.Get("good", "n"),
                "the well-behaved mod's handler must still run — and complete — in the SAME frame the " +
                "runaway mod's handler was cut in");
        }

        [Test]
        public void Lua_WhileTrueDoEndInHeartbeatHandler_IsCutAtTimeScaleZero()
        {
            LuaCsRbxApiBindings roblox = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);
            List<string> errors = new();
            stack.Runtime.ModHandlerErrored += (modId, message, streak) => errors.Add(message);
            stack.Runtime.LoadMod("looper", InfiniteLoopModSource);

            // WHY scaledDt = 0 here specifically: this simulates timeScale = 0 — a paused world whose
            // scaled clock never advances. LuaCsCoroutineHandle's guard is armed from a real
            // System.Diagnostics.Stopwatch and a per-instruction counter (see ResumeGuardHook), neither
            // of which reads the scheduler's scaled clock at all, so this call proves that rather than
            // assuming it: if the guard were instead driven by scaled time, this Advance(0d) would never
            // cut the loop and this test would hang the whole run.
            PumpHeartbeat(roblox, 0d);

            Assert.AreEqual(1, errors.Count,
                "the per-resume guard is wall-clock + instruction based, not scaled-time based, so it " +
                "must still cut the loop even though the scheduler's scaled clock never advanced");
            StringAssert.Contains("BUDGET_EXCEEDED", errors[0]);
        }

        [Test]
        public void Lua_WhileTrueDoEndInHeartbeatHandler_CutTimeIsMeasuredWithinConfiguredDefaultBudget()
        {
            LuaCsRbxApiBindings roblox = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);
            int errorCount = 0;
            stack.Runtime.ModHandlerErrored += (modId, message, streak) => errorCount++;
            stack.Runtime.LoadMod("looper", InfiniteLoopModSource);

            Stopwatch stopwatch = Stopwatch.StartNew();
            PumpHeartbeat(roblox, 0.1d);
            stopwatch.Stop();

            Assert.AreEqual(1, errorCount);
            // WHY measured against the configured constant with a margin, not against the audit's
            // disproven "~8 s" claim, and not just asserted true from the constant: a future change to
            // the guard mechanism that quietly widens the real cut time (e.g. the hook stops
            // re-checking the Stopwatch every instruction) must fail this test even though it would
            // still eventually cut the loop. The margin absorbs CI/debugger jitter while staying nowhere
            // near the disproven order of magnitude.
            long budgetMs = LuaCsCoroutineHandle.DefaultResumeTimeoutMs;
            const long marginMs = 2000;
            Assert.Less(stopwatch.ElapsedMilliseconds, budgetMs + marginMs,
                "measured cut time " + stopwatch.ElapsedMilliseconds + " ms must stay within the "
                + budgetMs + " ms configured wall-clock budget plus a " + marginMs + " ms margin");
        }

        [Test]
        public void Lua_WhileTrueDoEndInHeartbeatHandler_HonorsComposedResumeBudgetOverride()
        {
            // WHY these specific numbers: an instruction cap 10,000x the DEFAULT (LuaCsCoroutineHandle.
            // DefaultBudgetPerResume = 10_000) means that IF the composition override were silently
            // ignored and that old default applied instead, this trivial empty loop would be cut almost
            // instantly — well under half the wall-clock window asserted below. Pairing it with a
            // wall-clock cap 5x SHORTER than the default 500 ms means proving this end to end requires
            // BOTH halves of LuaCsCoroutineBudgetSettings to actually reach the coroutine handle behind
            // this mod's Heartbeat connection (LuaCsRbxApiBindings -> LuaCsRbxScriptThreadFactory ->
            // LuaCsRbxSignalRunner -> LuaCsCoroutineHandle).
            const int overrideBudgetPerResume = 100_000_000;
            const int overrideResumeTimeoutMs = 100;
            LuaCsCoroutineBudgetSettings overrideSettings =
                new(overrideBudgetPerResume, overrideResumeTimeoutMs);
            LuaCsRbxApiBindings roblox = new(coroutineResumeBudget: overrideSettings);
            Assert.AreSame(overrideSettings, roblox.CoroutineResumeBudget,
                "the constructor must expose exactly the composed instance, not a copy — this is the " +
                "SAME object ScriptContext:SetTimeout mutates");

            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);
            List<string> errors = new();
            stack.Runtime.ModHandlerErrored += (modId, message, streak) => errors.Add(message);
            stack.Runtime.LoadMod("looper", InfiniteLoopModSource);

            Stopwatch stopwatch = Stopwatch.StartNew();
            PumpHeartbeat(roblox, 0.1d);
            stopwatch.Stop();

            Assert.AreEqual(1, errors.Count);
            // WHY assert on the bound the raised error NAMES, not just that SOME cut happened: the old
            // 500 ms fixed default is itself a valid budget that would also cut this infinite loop and
            // raise a BUDGET_EXCEEDED error, so a regression that silently ignores the composition
            // override (falls back to LuaCsCoroutineHandle.DefaultResumeTimeoutMs) still makes errors.Count
            // == 1 above. Only the exact number in the guard's own message text — which LuaCsCoroutineHandle.
            // ResumeGuardHook.Hook formats as "... exceeded {timeoutMs} ms." — proves the LIVE 100 ms
            // override, not the untouched 500 ms default, was actually armed on this resume.
            StringAssert.Contains("exceeded " + overrideResumeTimeoutMs + " ms", errors[0],
                "the guard's own error message must name the composed 100 ms override, not some other " +
                "bound — message was: " + errors[0]);
            // Lower bound: proves the huge instruction cap is actually in effect. The untouched default
            // (10,000 steps) would have cut this empty loop in a small fraction of a millisecond, long
            // before half of the overridden 100 ms wall-clock window elapsed.
            Assert.GreaterOrEqual(stopwatch.ElapsedMilliseconds, overrideResumeTimeoutMs / 2,
                "cut at " + stopwatch.ElapsedMilliseconds + " ms is too fast to have used the "
                + overrideBudgetPerResume + "-step override — the untouched default 10,000-step budget "
                + "must have been used instead, meaning the composition override never reached the "
                + "coroutine handle");
            // WHY 150 ms and not the old 2000 ms margin: a margin that wide accepts the untouched 500 ms
            // default outright (100 + 2000 > 500), which is exactly how this assertion previously shipped
            // green against broken propagation despite its own comment claiming otherwise. 150 ms stays
            // comfortably clear of jitter for a hook that re-checks the Stopwatch every VM instruction,
            // while landing far short of the 500 ms default this test must be able to fail against.
            const long marginMs = 150;
            Assert.Less(stopwatch.ElapsedMilliseconds, overrideResumeTimeoutMs + marginMs,
                "cut at " + stopwatch.ElapsedMilliseconds + " ms overshoots the overridden "
                + overrideResumeTimeoutMs + " ms wall-clock budget by more than the test's margin — this "
                + "must be small enough that the untouched 500 ms default would fail it");
        }
    }
}
