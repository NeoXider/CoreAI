using System;

namespace CoreAI.Sandbox.LuaCs
{
    /// <summary>
    /// The per-execution allocation backstop shared by every guarded Lua-CSharp execution: the
    /// <see cref="LuaCsExecutionGuard"/> hook and the per-resume coroutine hook in
    /// <see cref="LuaCsSecureEnvironment"/> both hold one of these, so the rule cannot drift between the
    /// two hooks the way two hand-copied checks did.
    /// <para>
    /// It exists for ONE attack: an allocation bomb built from plain concatenation (<c>s = s .. s</c>),
    /// which is ordinary VM opcodes with no library call site to cap, unlike
    /// <c>string.rep</c>/<c>string.format</c>/<c>table.concat</c>. Step and time budgets do not see it —
    /// a doubling loop is a handful of instructions per iteration.
    /// </para>
    /// <para>
    /// A SUSPICION IS NOT A TRIP. The cheap sample is <see cref="GC.GetTotalMemory(bool)"/> with
    /// <c>forceFullCollection: false</c>: a process-wide, garbage-INCLUSIVE heap reading (Unity's Mono
    /// does not implement <c>GC.GetAllocatedBytesForCurrentThread</c>, which returns 0 unconditionally).
    /// The hook that samples it fires every few VM instructions and the VM allocates per fire, so on a
    /// single-threaded Boehm GC that reading crosses a 256 MB budget after a few seconds of a loop that
    /// retains NOTHING — which is exactly how a pure-arithmetic runaway was measured tripping the memory
    /// budget instead of its own wall-clock limit. So the sample only raises a suspicion; the trip is
    /// decided by ONE confirming <see cref="GC.GetTotalMemory(bool)"/> with <c>forceFullCollection:
    /// true</c>. A real bomb's result string is LIVE and survives that collection, while the VM's and the
    /// hook's own transient garbage is collected away and clears the suspicion.
    /// </para>
    /// <para>
    /// Forced collections stay bounded because a cleared suspicion re-baselines from the post-collection
    /// reading: every further confirmation needs another full budget of growth on top of the live heap
    /// that was just measured.
    /// </para>
    /// </summary>
    public struct LuaCsAllocationBudget
    {
        private long _budgetBytes;
        private long _baselineBytes;

        /// <summary>The budget in bytes; <c>&lt;= 0</c> disables the check entirely.</summary>
        public long BudgetBytes => _budgetBytes;

        /// <summary>True when a budget is armed and <see cref="IsExceeded"/> does real work.</summary>
        public bool IsEnabled => _budgetBytes > 0;

        /// <summary>Arms a fresh per-execution budget and takes the (cheap, sampled) baseline.</summary>
        /// <param name="budgetBytes">Allowed growth in bytes; <c>&lt;= 0</c> disables the check.</param>
        public void Reset(long budgetBytes)
        {
            _budgetBytes = budgetBytes;
            _baselineBytes = budgetBytes > 0 ? GC.GetTotalMemory(false) : 0;
        }

        /// <summary>
        /// True only when growth beyond the budget survives a forced collection. Cheap on every call
        /// but the last: the sampled reading short-circuits long before any collection is forced.
        /// </summary>
        public bool IsExceeded()
        {
            if (_budgetBytes <= 0)
            {
                return false;
            }

            if (GC.GetTotalMemory(false) - _baselineBytes <= _budgetBytes)
            {
                return false;
            }

            // WHY: the confirming reading is compared against the SAMPLED baseline, which included
            // whatever garbage was already on the heap when the execution started. Live growth is
            // therefore understated by at most that garbage, i.e. the confirmation can only ever be
            // late, never early — it cannot invent a trip for a script that retained nothing.
            long liveBytes = GC.GetTotalMemory(true);
            if (liveBytes - _baselineBytes > _budgetBytes)
            {
                return true;
            }

            _baselineBytes = liveBytes;
            return false;
        }
    }
}
