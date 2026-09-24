using System;
using NUnit.Framework;
using NUnit.Framework.Constraints;

namespace UnityEngine.TestTools.Constraints
{
    /// <summary>
    /// Portable twin of the Unity Test Framework's <c>UnityEngine.TestTools.Constraints.Is</c>: NUnit's
    /// <see cref="NUnit.Framework.Is"/> plus <see cref="AllocatingGCMemory"/>, so fixtures that write
    /// <c>using Is = UnityEngine.TestTools.Constraints.Is;</c> compile and keep every NUnit member.
    /// </summary>
    public class Is : NUnit.Framework.Is
    {
        /// <summary>Succeeds when the delegate under test allocates managed heap memory.</summary>
        public static AllocatingGCMemoryConstraint AllocatingGCMemory()
        {
            return new AllocatingGCMemoryConstraint();
        }
    }

    /// <summary>Lets <c>Is.Not.AllocatingGCMemory()</c> chain, as in the Unity Test Framework.</summary>
    public static class ConstraintExtensions
    {
        /// <summary>Appends an <see cref="AllocatingGCMemoryConstraint"/> to the expression.</summary>
        public static AllocatingGCMemoryConstraint AllocatingGCMemory(this ConstraintExpression chain)
        {
            AllocatingGCMemoryConstraint constraint = new();
            chain.Append(constraint);
            return constraint;
        }
    }

    /// <summary>
    /// Portable twin of the Unity Test Framework's <c>AllocatingGCMemoryConstraint</c>. Like Unity's, it
    /// runs the delegate exactly once, with no warm-up, counts only allocations made on the calling
    /// thread, and succeeds when anything was allocated (so <c>Is.Not.AllocatingGCMemory()</c> passes
    /// only for an allocation-free delegate). Unity counts allocation events through the
    /// <c>GC.Alloc</c> profiler recorder; this twin measures allocated bytes with
    /// <see cref="GC.GetAllocatedBytesForCurrentThread"/>, which is exact on .NET 8 (it includes the
    /// unused tail of the thread's allocation context), so "any allocation" means the same thing.
    /// </summary>
    /// <remarks>
    /// WHY no warm-up: Unity does not warm up either, so a delegate that allocates only on its first call
    /// (lazy initialisation, a cached lambda, a static constructor) fails in Unity and must fail here;
    /// warming up would turn exactly those Unity failures into portable passes.
    /// WHY a probe before every measurement: <c>Is.Not.AllocatingGCMemory()</c> turns "measured
    /// nothing" into a pass, so a counter that does not move (a runtime that does not implement it, as
    /// Mono's returns 0) would be a silent false pass. The probe allocates a known object and the
    /// measurement is refused as Inconclusive unless the counter saw it.
    /// Limits: CoreCLR's base library avoids some allocations Mono's still makes (boxing in some
    /// generic comparers and enumerators), so a portable pass does not prove a Unity pass when the
    /// delegate reaches such BCL paths; a first call can also allocate runtime bookkeeping that Mono
    /// would not, which can only produce a failure, never a pass. The test project targets net8.0,
    /// whose JIT does not stack-allocate objects; a newer runtime with object stack allocation could
    /// hide allocations Unity makes.
    /// </remarks>
    public class AllocatingGCMemoryConstraint : Constraint
    {
        private static object _probeSink;

        /// <inheritdoc />
        public override string Description => "allocates GC memory";

        /// <inheritdoc />
        public override ConstraintResult ApplyTo<TActual>(TActual actual)
        {
            if (actual == null)
            {
                throw new ArgumentNullException(nameof(actual));
            }

            if (!(actual is TestDelegate testDelegate))
            {
                throw new ArgumentException(
                    "The actual value must be a TestDelegate but was " + actual.GetType());
            }

            return ApplyTo(() => testDelegate.Invoke(), actual);
        }

        /// <inheritdoc />
        public override ConstraintResult ApplyTo<TActual>(ActualValueDelegate<TActual> del)
        {
            if (del == null)
            {
                throw new ArgumentNullException(nameof(del));
            }

            return ApplyTo(() => del.Invoke(), del);
        }

        private ConstraintResult ApplyTo(Action action, object original)
        {
            RequireWorkingCounter();
            long before = GC.GetAllocatedBytesForCurrentThread();
            action();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            return new AllocatingGCMemoryResult(this, original, allocated);
        }

        private static void RequireWorkingCounter()
        {
            long before;
            long after;
            try
            {
                before = GC.GetAllocatedBytesForCurrentThread();
                _probeSink = new object[4];
                after = GC.GetAllocatedBytesForCurrentThread();
            }
            catch (PlatformNotSupportedException ex)
            {
                Assert.Inconclusive("AllocatingGCMemory cannot be measured on this runtime: " + ex.Message);
                return;
            }
            finally
            {
                _probeSink = null;
            }

            if (after <= before)
            {
                Assert.Inconclusive("AllocatingGCMemory cannot be measured on this runtime: " +
                                    "GC.GetAllocatedBytesForCurrentThread did not see a probe allocation.");
            }
        }

        private sealed class AllocatingGCMemoryResult : ConstraintResult
        {
            private readonly long _allocatedBytes;

            public AllocatingGCMemoryResult(IConstraint constraint, object actualValue, long allocatedBytes)
                : base(constraint, actualValue, allocatedBytes > 0)
            {
                _allocatedBytes = allocatedBytes;
            }

            public override void WriteMessageTo(MessageWriter writer)
            {
                if (_allocatedBytes == 0)
                {
                    writer.WriteMessageLine("The provided delegate did not make any GC allocations.");
                }
                else
                {
                    writer.WriteMessageLine(
                        "The provided delegate allocated {0} byte(s) of GC memory.", _allocatedBytes);
                }
            }
        }
    }
}
