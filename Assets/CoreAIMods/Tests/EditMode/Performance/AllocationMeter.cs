using System;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.Performance
{
    /// <summary>
    /// Measures the managed bytes a hot path allocates per operation with
    /// <see cref="GC.GetAllocatedBytesForCurrentThread"/>, and turns a measurement into a budget verdict
    /// that names the measured number.
    /// </summary>
    /// <remarks>
    /// WHY a probe before every measurement: Unity's Mono answers 0 from the counter, which would make every
    /// budget pass silently. A measurement whose counter does not see a known allocation is reported as
    /// Ignored with the reason, never as a pass; the budgets are enforced where the counter exists (the
    /// portable .NET leg, tools/perfbench/Tests).
    /// WHY a warm-up: the first calls JIT-compile the path and fill lazy caches; a budget is about the
    /// steady state a frame pays, which is what a player sees after the first frames.
    /// </remarks>
    internal static class AllocationMeter
    {
        /// <summary>True when the per-thread counter sees a known allocation on this runtime.</summary>
        public static bool IsSupported(out string reason)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            object[] probe = new object[32];
            GC.KeepAlive(probe);
            long seen = GC.GetAllocatedBytesForCurrentThread() - before;
            if (seen >= 32 * IntPtr.Size)
            {
                reason = null;
                return true;
            }

            reason = "GC.GetAllocatedBytesForCurrentThread did not count a known " + (32 * IntPtr.Size)
                     + "-byte allocation (saw " + seen + "); this runtime cannot measure the budget (Unity's Mono "
                     + "answers 0) - it is enforced on the portable .NET leg (tools/perfbench/Tests)";
            return false;
        }

        /// <summary>
        /// Runs <paramref name="body"/> for <paramref name="warmupOps"/> operations, then measures the bytes
        /// one call of <paramref name="ops"/> operations allocates on this thread.
        /// </summary>
        public static long Measure(Action<int> body, int ops, int warmupOps)
        {
            if (body == null)
            {
                throw new ArgumentNullException(nameof(body));
            }

            body(Math.Max(1, warmupOps));
            body(Math.Max(1, warmupOps));
            long before = GC.GetAllocatedBytesForCurrentThread();
            body(ops);
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        /// <summary>
        /// Null when <paramref name="bytes"/> over <paramref name="ops"/> operations stays within
        /// <paramref name="budgetBytesPerOp"/>; otherwise the failure line, with the measured numbers.
        /// </summary>
        public static string Verdict(string subject, long bytes, int ops, long budgetBytesPerOp)
        {
            if (ops <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(ops));
            }

            long allowed = budgetBytesPerOp * ops;
            if (bytes <= allowed)
            {
                return null;
            }

            return subject + " allocated " + bytes + " bytes over " + ops + " operations ("
                   + (bytes / (double)ops).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)
                   + " B/op) against a budget of " + budgetBytesPerOp + " B/op";
        }

        /// <summary>
        /// Asserts <paramref name="body"/> stays within <paramref name="budgetBytesPerOp"/>; Ignored when the
        /// runtime cannot count. The measured bytes are written to the test output either way.
        /// </summary>
        public static void AssertWithinBudget(string subject, Action<int> body, int ops, long budgetBytesPerOp,
            int warmupOps = 256)
        {
            if (!IsSupported(out string reason))
            {
                Assert.Ignore(reason);
            }

            long bytes = Measure(body, ops, warmupOps);
            TestContext.WriteLine(subject + ": " + bytes + " bytes over " + ops + " operations");
            string verdict = Verdict(subject, bytes, ops, budgetBytesPerOp);
            Assert.IsNull(verdict, verdict);
        }

        /// <summary>
        /// The negative twin: the same body with one deliberate allocation per operation must break the
        /// same budget, and the verdict must name the measured bytes. Proves the budget is not vacuous.
        /// </summary>
        public static void AssertTwinBreaksBudget(string subject, Action<int> body, int ops, long budgetBytesPerOp,
            int warmupOps = 256)
        {
            if (!IsSupported(out string reason))
            {
                Assert.Ignore(reason);
            }

            Action<int> allocating = count =>
            {
                for (int index = 0; index < count; index++)
                {
                    GC.KeepAlive(new byte[16]);
                }

                body(count);
            };
            long bytes = Measure(allocating, ops, warmupOps);
            TestContext.WriteLine(subject + " (allocating twin): " + bytes + " bytes over " + ops + " operations");
            string verdict = Verdict(subject, bytes, ops, budgetBytesPerOp);
            Assert.IsNotNull(verdict, "the twin allocates one array per operation, so the budget must reject it");
            StringAssert.Contains(bytes.ToString(System.Globalization.CultureInfo.InvariantCulture), verdict,
                "a broken budget must print the measured bytes");
        }
    }
}
