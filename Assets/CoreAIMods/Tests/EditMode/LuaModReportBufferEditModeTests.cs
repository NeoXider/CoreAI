using System.Collections.Generic;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Proves the <see cref="LuaCsModRuntime"/> report ring buffer added alongside the existing
    /// handler-error buffer: <c>report()</c>/<c>print()</c> emissions are captured even when a mod's
    /// <c>LogReports</c> flag (muted by default) keeps <see cref="LuaCsModRuntime.ModReportEmitted"/>
    /// silent, per-mod filtering and clearing work, and the buffer evicts its oldest entry once
    /// <see cref="LuaCsModRuntime.MaxRetainedReports"/> is exceeded. Uses the same
    /// SynchronizationContext-detach pattern as <c>LuaCsModRuntimeEditModeTests</c> to avoid the
    /// interactive Test Runner's sync-over-async deadlock on the Lua-CSharp VM's async execution guard.
    /// </summary>
    public sealed class LuaModReportBufferEditModeTests
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

        private static LuaCsModRuntime BuildRuntime()
        {
            return LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All
            }).Runtime;
        }

        [Test]
        public void Report_IsCapturedEvenWhenLogReportsIsDisabled()
        {
            LuaCsModRuntime runtime = BuildRuntime();

            Assert.IsFalse(runtime.GetModReportLoggingEnabled("m"),
                "A freshly loaded mod's LogReports is muted by default.");

            bool eventFired = false;
            runtime.ModReportEmitted += (modId, message) => eventFired = true;

            runtime.LoadMod("m", "report('x')");

            Assert.IsFalse(eventFired, "ModReportEmitted must stay silent while LogReports is disabled.");

            IReadOnlyList<LuaModReport> reports = runtime.GetRecentReports();
            Assert.AreEqual(1, reports.Count, "The buffer captures the report regardless of the mute flag.");
            Assert.AreEqual("m", reports[0].ModId);
            Assert.AreEqual("x", reports[0].Message);
        }

        [Test]
        public void Print_IsAlsoCapturedInTheReportBuffer()
        {
            LuaCsModRuntime runtime = BuildRuntime();
            runtime.LoadMod("m", "print('a', 'b')");

            IReadOnlyList<LuaModReport> reports = runtime.GetRecentReports("m");
            Assert.AreEqual(1, reports.Count);
            Assert.AreEqual("a\tb", reports[0].Message,
                "print() joins its arguments with tabs, same as ModReportEmitted.");
        }

        [Test]
        public void GetRecentReports_FiltersByModId()
        {
            LuaCsModRuntime runtime = BuildRuntime();
            runtime.LoadMod("a", "report('from-a')");
            runtime.LoadMod("b", "report('from-b')");

            IReadOnlyList<LuaModReport> all = runtime.GetRecentReports();
            Assert.AreEqual(2, all.Count);

            IReadOnlyList<LuaModReport> onlyA = runtime.GetRecentReports("a");
            Assert.AreEqual(1, onlyA.Count);
            Assert.AreEqual("from-a", onlyA[0].Message);
        }

        [Test]
        public void ClearRecentReports_ClearsOneModOrAll()
        {
            LuaCsModRuntime runtime = BuildRuntime();
            runtime.LoadMod("a", "report('from-a')");
            runtime.LoadMod("b", "report('from-b')");

            int clearedA = runtime.ClearRecentReports("a");
            Assert.AreEqual(1, clearedA);
            Assert.IsEmpty(runtime.GetRecentReports("a"));
            Assert.AreEqual(1, runtime.GetRecentReports("b").Count,
                "Clearing one mod must not touch another's reports.");

            int clearedRest = runtime.ClearRecentReports();
            Assert.AreEqual(1, clearedRest);
            Assert.IsEmpty(runtime.GetRecentReports());
        }

        [Test]
        public void GetRecentReports_EvictsOldestPastCap()
        {
            LuaCsModRuntime runtime = BuildRuntime();
            runtime.LoadMod("m", @"
                for i = 1, 70 do
                    report(tostring(i))
                end");

            IReadOnlyList<LuaModReport> reports = runtime.GetRecentReports("m");
            Assert.AreEqual(LuaCsModRuntime.MaxRetainedReports, reports.Count,
                "The buffer must cap at MaxRetainedReports, dropping the oldest entries.");
            Assert.AreEqual("7", reports[0].Message, "The oldest surviving report is #7 once #1..#6 are evicted.");
            Assert.AreEqual("70", reports[^1].Message, "The newest report is retained.");
        }
    }
}