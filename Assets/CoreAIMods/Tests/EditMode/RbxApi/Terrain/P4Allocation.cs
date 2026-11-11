using System;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Terrain
{
    /// <summary>
    /// Bytes a delegate allocates on the calling thread. A runtime whose counter does not move for a
    /// known probe allocation (Mono answers 0) makes the measuring test inconclusive instead of letting
    /// it pass on a counter that cannot see anything.
    /// </summary>
    internal static class P4Allocation
    {
        public static long Measure(Action action)
        {
            long probeStart = GC.GetAllocatedBytesForCurrentThread();
            object probe = new byte[256];
            long probeBytes = GC.GetAllocatedBytesForCurrentThread() - probeStart;
            GC.KeepAlive(probe);
            if (probeBytes <= 0)
            {
                Assert.Inconclusive("this runtime does not count allocated bytes per thread");
            }

            long start = GC.GetAllocatedBytesForCurrentThread();
            action();
            return GC.GetAllocatedBytesForCurrentThread() - start;
        }
    }
}
