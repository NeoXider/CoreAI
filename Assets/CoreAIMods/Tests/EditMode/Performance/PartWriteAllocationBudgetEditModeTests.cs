#if COREAI_LUA
using System.Threading;
using CoreAI.Ai.LuaCs;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.Performance
{
    /// <summary>
    /// What one Lua member write on a Part costs (finding N10 of <c>dev-docs/PERF_BASELINE_2026-11.md</c>):
    /// the write itself allocates nothing, so a Heartbeat handler that moves parts to prebuilt vectors pays
    /// only for its own fire. Measured as the difference between a handler doing 64 writes a frame and the
    /// same handler doing none, so the handler fire's own cost (another path) cancels out. The negative twin
    /// writes a fresh <c>Vector3.new</c> each time and must break the same budget by the userdata box.
    /// </summary>
    [TestFixture]
    public sealed class PartWriteAllocationBudgetEditModeTests
    {
        private const int WritesPerFrame = 64;
        private const int Frames = 200;

        /// <summary>Bytes one Part member write may allocate: none.</summary>
        private const double WriteBudgetBytes = 0.5d;

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

        private static double BytesPerFrame(int writes, bool freshVector)
        {
            LuaCsRbxApiBindings bindings = new();
            LuaCsSecureSandboxEditModeTests.NestedRunModStore store = new();
            LuaCsModStack stack = LuaCsSecureSandboxEditModeTests.NewNestedRunModStack(bindings, store);
            stack.Runtime.LoadMod("writer",
                "local part = Instance.new('Part')\n" +
                "part.Anchored = true\n" +
                "part.Parent = workspace\n" +
                "local a, b = Vector3.new(1, 2, 3), Vector3.new(4, 5, 6)\n" +
                "local writes = " + writes + "\n" +
                "game:GetService('RunService').Heartbeat:Connect(function()\n" +
                "  for i = 1, writes do\n" +
                (freshVector
                    ? "    part.Position = Vector3.new(i, 0, 0)\n"
                    : "    if i % 2 == 0 then part.Position = a else part.Position = b end\n") +
                "  end\n" +
                "end)");
            Assert.IsTrue(stack.Runtime.IsLoaded("writer"));
            long bytes = AllocationMeter.Measure(count =>
            {
                for (int index = 0; index < count; index++)
                {
                    bindings.Scheduler.Advance(1d / 60d);
                }
            }, Frames, 32);
            return bytes / (double)Frames;
        }

        private static double BytesPerWrite(bool freshVector)
        {
            double idle = BytesPerFrame(0, false);
            double writing = BytesPerFrame(WritesPerFrame, freshVector);
            double perWrite = (writing - idle) / WritesPerFrame;
            TestContext.WriteLine("frame with no writes: " + idle + " B; with " + WritesPerFrame + " writes: "
                                  + writing + " B; per write: " + perWrite + " B");
            return perWrite;
        }

        [Test]
        public void N10_PartMemberWrite_AllocatesNothing()
        {
            if (!AllocationMeter.IsSupported(out string reason))
            {
                Assert.Ignore(reason);
            }

            Assert.LessOrEqual(BytesPerWrite(false), WriteBudgetBytes,
                "a Part write from Lua must not allocate a closure or a delegate per write");
        }

        [Test]
        public void N10_PartMemberWrite_WithAFreshVector3_BreaksTheBudget_NegativeTwin()
        {
            if (!AllocationMeter.IsSupported(out string reason))
            {
                Assert.Ignore(reason);
            }

            Assert.Greater(BytesPerWrite(true), 16d,
                "Vector3.new boxes its value as userdata, so the twin must be measured above the budget");
        }
    }
}
#endif
