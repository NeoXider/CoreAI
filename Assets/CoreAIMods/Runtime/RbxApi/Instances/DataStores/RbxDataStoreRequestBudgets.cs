using System;

namespace CoreAI.Mods.Rbx.Instances.DataStores
{
    /// <summary>The request budgets of one world, as Roblox's server-level request types.</summary>
    public enum RbxDataStoreBudgetKind
    {
        /// <summary><c>StandardRead</c>: GetAsync and the read of UpdateAsync.</summary>
        Read = 0,

        /// <summary><c>StandardWrite</c>: SetAsync, IncrementAsync and the write of UpdateAsync.</summary>
        Write = 1,

        /// <summary><c>StandardRemove</c>: RemoveAsync.</summary>
        Remove = 2,

        /// <summary><c>StandardList</c>: listing (not built yet; its budget is reported).</summary>
        List = 3
    }

    /// <summary>
    /// Token buckets for the request budgets of one world: each refills continuously at its per-minute rate
    /// (<c>base + perPlayer x players</c>, the mirror's server-level defaults), holds at most one minute of
    /// requests and starts full, which is the one-time startup burst a Roblox server gets. Time comes from the
    /// injected monotonic clock, so a test advances it by hand.
    /// </summary>
    public sealed class RbxDataStoreRequestBudgets
    {
        private const int KindCount = 4;

        private readonly RbxDataStoreOptions _options;
        private readonly Func<double> _clock;
        private readonly Func<int> _playerCount;
        private readonly double[] _tokens = new double[KindCount];
        private double _lastRefill;

        /// <summary>Creates full budgets.</summary>
        /// <param name="options">The per-minute rates.</param>
        /// <param name="clock">Monotonic seconds.</param>
        /// <param name="playerCount">Players in the world now; at least 1 counts for a solo world without one yet.</param>
        public RbxDataStoreRequestBudgets(RbxDataStoreOptions options, Func<double> clock, Func<int> playerCount)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _playerCount = playerCount;
            _lastRefill = _clock();
            for (int kind = 0; kind < KindCount; kind++)
            {
                _tokens[kind] = PerMinute((RbxDataStoreBudgetKind)kind, Players());
            }
        }

        /// <summary>Requests of <paramref name="kind"/> that may run now (the value GetRequestBudgetForRequestType reports).</summary>
        public int Available(RbxDataStoreBudgetKind kind)
        {
            Refill();
            return (int)Math.Floor(_tokens[(int)kind] + 1e-9);
        }

        /// <summary>Takes one request of <paramref name="kind"/>; false, and nothing taken, when none is left.</summary>
        public bool TryTake(RbxDataStoreBudgetKind kind)
        {
            Refill();
            if (_tokens[(int)kind] < 1d - 1e-9)
            {
                return false;
            }

            _tokens[(int)kind] -= 1d;
            return true;
        }

        /// <summary>Takes one read and one write together (UpdateAsync); false, and nothing taken, unless both are left.</summary>
        public bool TryTakeReadAndWrite()
        {
            Refill();
            if (_tokens[(int)RbxDataStoreBudgetKind.Read] < 1d - 1e-9 || _tokens[(int)RbxDataStoreBudgetKind.Write] < 1d - 1e-9)
            {
                return false;
            }

            _tokens[(int)RbxDataStoreBudgetKind.Read] -= 1d;
            _tokens[(int)RbxDataStoreBudgetKind.Write] -= 1d;
            return true;
        }

        /// <summary>Gives back one request of <paramref name="kind"/> that was taken and not used (an UpdateAsync that wrote nothing).</summary>
        public void Refund(RbxDataStoreBudgetKind kind)
        {
            int index = (int)kind;
            _tokens[index] = Math.Min(_tokens[index] + 1d, PerMinute(kind, Players()));
        }

        /// <summary>The per-minute rate of <paramref name="kind"/> with <paramref name="players"/> players.</summary>
        public double PerMinute(RbxDataStoreBudgetKind kind, int players)
        {
            int count = Math.Max(1, players);
            switch (kind)
            {
                case RbxDataStoreBudgetKind.Read:
                    return Math.Max(0, _options.ReadBase) + Math.Max(0, _options.ReadPerPlayer) * (double)count;
                case RbxDataStoreBudgetKind.Write:
                    return Math.Max(0, _options.WriteBase) + Math.Max(0, _options.WritePerPlayer) * (double)count;
                case RbxDataStoreBudgetKind.Remove:
                    return Math.Max(0, _options.RemoveBase) + Math.Max(0, _options.RemovePerPlayer) * (double)count;
                default:
                    return Math.Max(0, _options.ListBase) + Math.Max(0, _options.ListPerPlayer) * (double)count;
            }
        }

        /// <summary>Seconds until one request of <paramref name="kind"/> is available, 0 when one is now.</summary>
        public double SecondsUntilAvailable(RbxDataStoreBudgetKind kind)
        {
            Refill();
            double missing = 1d - _tokens[(int)kind];
            if (missing <= 1e-9)
            {
                return 0d;
            }

            double perSecond = PerMinute(kind, Players()) / 60d;
            return perSecond <= 0d ? double.PositiveInfinity : missing / perSecond;
        }

        private int Players()
        {
            return _playerCount == null ? 1 : _playerCount();
        }

        private void Refill()
        {
            double now = _clock();
            double elapsed = now - _lastRefill;
            if (elapsed <= 0d)
            {
                return;
            }

            _lastRefill = now;
            int players = Players();
            for (int kind = 0; kind < KindCount; kind++)
            {
                double perMinute = PerMinute((RbxDataStoreBudgetKind)kind, players);
                _tokens[kind] = Math.Min(perMinute, _tokens[kind] + perMinute * elapsed / 60d);
            }
        }
    }
}
