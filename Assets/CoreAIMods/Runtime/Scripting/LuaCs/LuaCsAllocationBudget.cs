using System;

namespace CoreAI.Sandbox.LuaCs
{
    /// <summary>
    /// The per-execution allocation backstop shared by every guarded Lua-CSharp execution: the
    /// <see cref="LuaCsExecutionGuard"/> hook, the per-resume hook of every <see cref="LuaCsCoroutineHandle"/>
    /// (scheduler threads, signal runners, a mod's main chunk) and the raw <c>coroutine.resume</c> hook in
    /// <see cref="LuaCsSecureEnvironment"/> all hold one of these, so the rule cannot drift between hooks
    /// the way hand-copied checks did.
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
    /// THE TRIP REFERENCE NEVER MOVES UP. The baseline taken at <see cref="Reset(long)"/> is the reference
    /// every confirmation is compared against for the whole execution; a cleared suspicion may LOWER it
    /// (a post-collection reading below it is a truer start point, so the trip can only get earlier, and
    /// still only for real live growth inside this execution) but never raise it. What a cleared
    /// suspicion raises instead is the separate SUSPICION threshold: the next forced collection waits
    /// until the sampled reading has grown a quarter of the budget (<see cref="SuspicionSlackDivisor"/>)
    /// past the post-collection reading. That keeps forced collections bounded (one per quarter budget of
    /// fresh allocation) without letting live growth escape: it can pass the budget by at most that
    /// quarter before the next confirmation catches it. The previous rule re-baselined the REFERENCE to the
    /// post-collection reading, so every confirmation forgave the live growth it had just measured and a
    /// doubling bomb passed a 256 MB budget on its way to a 1 GB string.
    /// </para>
    /// <para>
    /// The budget is per EXECUTION (one guarded call, one coroutine resume), never cumulative across a
    /// coroutine's resumes: the reading is process-wide, and between two resumes every other mod and the
    /// host allocate into the same heap, so a reference carried across resumes would bill one thread for
    /// the whole world's growth.
    /// </para>
    /// <para>
    /// An execution that runs NESTED inside another one (a coroutine resumed while another guarded run is
    /// executing, see <see cref="LuaCsGuardedRun"/>) is also held under a CEILING: the enclosing run's own
    /// line, an absolute live-heap reading. Its own budget still counts from its own baseline, so whichever
    /// line is lower trips it, and <see cref="CeilingExceeded"/> tells which one did.
    /// </para>
    /// </summary>
    public struct LuaCsAllocationBudget
    {
        /// <summary>
        /// A cleared suspicion re-arms the next one at the post-collection reading plus
        /// <c>budget / SuspicionSlackDivisor</c> (never below the budget line itself).
        /// </summary>
        internal const int SuspicionSlackDivisor = 4;

        private static readonly Func<long> ReadLiveBytesAfterFullCollection = ReadLiveBytes;

        private long _budgetBytes;
        private long _baselineBytes;
        private long _suspicionBytes;
        private long _ceilingBytes;
        private bool _hasCeiling;
        private bool _ceilingExceeded;

        /// <summary>The budget in bytes; <c>&lt;= 0</c> disables the check entirely.</summary>
        public long BudgetBytes => _budgetBytes;

        /// <summary>True when a budget or a ceiling is armed and <see cref="IsExceeded"/> does real work.</summary>
        public bool IsEnabled => _budgetBytes > 0 || _hasCeiling;

        /// <summary>The trip reference of the current execution; it may only ever move down.</summary>
        internal long BaselineBytes => _baselineBytes;

        /// <summary>The sampled reading above which the next confirming collection is forced.</summary>
        internal long SuspicionBytes => _suspicionBytes;

        /// <summary>
        /// The live-heap reading above which this execution trips: the lower of its own line (baseline plus
        /// budget) and its ceiling; <see cref="long.MaxValue"/> when neither is armed. A run nested inside this
        /// one takes it as its ceiling.
        /// </summary>
        internal long LineBytes
        {
            get
            {
                long own = _budgetBytes > 0 ? SaturatingAdd(_baselineBytes, _budgetBytes) : long.MaxValue;
                return _hasCeiling && _ceilingBytes < own ? _ceilingBytes : own;
            }
        }

        /// <summary>
        /// True when the ceiling is what the lower line is: the run this one is nested in lent the
        /// allowance that is left, so exhausting it exhausts that run too.
        /// </summary>
        internal bool LineIsCeiling
        {
            get
            {
                long own = _budgetBytes > 0 ? SaturatingAdd(_baselineBytes, _budgetBytes) : long.MaxValue;
                return _hasCeiling && _ceilingBytes <= own;
            }
        }

        /// <summary>
        /// True once <see cref="IsExceeded()"/> returned true because the live heap passed the ceiling (the
        /// enclosing run's line) rather than this execution's own budget.
        /// </summary>
        internal bool CeilingExceeded => _ceilingExceeded;

        /// <summary>Arms a fresh per-execution budget and takes the (cheap, sampled) baseline.</summary>
        /// <param name="budgetBytes">Allowed growth in bytes; <c>&lt;= 0</c> disables the check.</param>
        public void Reset(long budgetBytes)
        {
            ResetNested(budgetBytes, long.MaxValue);
        }

        /// <summary>
        /// Arms a fresh budget against an explicit baseline reading. The production entry is
        /// <see cref="Reset(long)"/>; this overload exists so the rule can be pinned with exact numbers.
        /// </summary>
        internal void Reset(long budgetBytes, long sampledBaselineBytes)
        {
            ResetNested(budgetBytes, long.MaxValue, sampledBaselineBytes);
        }

        /// <summary>
        /// Arms a fresh per-execution budget held under <paramref name="ceilingBytes"/>, the
        /// <see cref="LineBytes"/> of the run this execution is nested in (<see cref="long.MaxValue"/> for none),
        /// and takes the (cheap, sampled) baseline.
        /// </summary>
        internal void ResetNested(long budgetBytes, long ceilingBytes)
        {
            bool enabled = budgetBytes > 0 || ceilingBytes != long.MaxValue;
            ResetNested(budgetBytes, ceilingBytes, enabled ? GC.GetTotalMemory(false) : 0);
        }

        /// <summary>
        /// <see cref="ResetNested(long, long)"/> against an explicit baseline reading, so the rule can be pinned
        /// with exact numbers.
        /// </summary>
        internal void ResetNested(long budgetBytes, long ceilingBytes, long sampledBaselineBytes)
        {
            _budgetBytes = budgetBytes;
            _hasCeiling = ceilingBytes != long.MaxValue;
            _ceilingBytes = ceilingBytes;
            _ceilingExceeded = false;
            bool enabled = budgetBytes > 0 || _hasCeiling;
            _baselineBytes = enabled ? sampledBaselineBytes : 0;
            _suspicionBytes = enabled ? LineBytes : long.MaxValue;
        }

        /// <summary>
        /// True only when growth beyond the budget survives a forced collection. Cheap on every call
        /// but the last: the sampled reading short-circuits long before any collection is forced.
        /// </summary>
        public bool IsExceeded()
        {
            if (!IsEnabled)
            {
                return false;
            }

            return IsExceeded(GC.GetTotalMemory(false), ReadLiveBytesAfterFullCollection);
        }

        /// <summary>
        /// The rule behind <see cref="IsExceeded()"/> over explicit readings: <paramref name="sampledBytes"/>
        /// is the cheap garbage-inclusive sample and <paramref name="readLiveBytes"/> is invoked only when
        /// that sample raises a suspicion, to take the confirming post-collection reading.
        /// </summary>
        internal bool IsExceeded(long sampledBytes, Func<long> readLiveBytes)
        {
            if (!IsEnabled || sampledBytes <= _suspicionBytes)
            {
                return false;
            }

            // WHY: the confirming reading is compared against the SAMPLED baseline, which included
            // whatever garbage was already on the heap when the execution started. Live growth is
            // therefore understated by at most that garbage, i.e. the confirmation can only ever be
            // late, never early — it cannot invent a trip for a script that retained nothing.
            long liveBytes = readLiveBytes();
            // WHY the ceiling first: past it the enclosing run's own allowance is gone as well, and a trip
            // that names its budget ends that run too; this execution's own line may be passed at the same
            // reading, but the enclosing run is the one that has to stop.
            if (_hasCeiling && liveBytes > _ceilingBytes)
            {
                _ceilingExceeded = true;
                return true;
            }

            if (_budgetBytes > 0 && liveBytes - _baselineBytes > _budgetBytes)
            {
                return true;
            }

            if (liveBytes < _baselineBytes)
            {
                _baselineBytes = liveBytes;
            }

            long budgetLine = LineBytes;
            long allowance = budgetLine == long.MaxValue ? long.MaxValue : budgetLine - _baselineBytes;
            long rearmed = SaturatingAdd(liveBytes, Math.Max(1L, allowance / SuspicionSlackDivisor));
            _suspicionBytes = rearmed > budgetLine ? rearmed : budgetLine;
            return false;
        }

        private static long ReadLiveBytes()
        {
            return GC.GetTotalMemory(true);
        }

        private static long SaturatingAdd(long value, long addend)
        {
            return value > long.MaxValue - addend ? long.MaxValue : value + addend;
        }
    }
}
