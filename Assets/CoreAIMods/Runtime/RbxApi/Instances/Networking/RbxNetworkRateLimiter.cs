using System;
using System.Collections.Generic;

namespace CoreAI.Mods.Rbx.Instances.Networking
{
    /// <summary>The traffic classes a client's request budget is counted per.</summary>
    public enum RbxNetworkRateGroup
    {
        /// <summary>Reliable <c>RemoteEvent</c> fires.</summary>
        ReliableRemoteEvent,

        /// <summary>Unreliable <c>UnreliableRemoteEvent</c> fires.</summary>
        UnreliableRemoteEvent,

        /// <summary>Blocking <c>RemoteFunction</c> invocations.</summary>
        RemoteFunction
    }

    /// <summary>
    /// The per-actor, per-group request budget every network bridge enforces.
    /// </summary>
    /// <remarks>
    /// WHY it is its own type rather than a method inside one bridge: the loopback bridge grew this
    /// limit first, and a Mirror bridge written without it would be a transport that accepts an
    /// unbounded flood from any client — the same hole, reopened by the code path that finally faces
    /// a real network. Shared here, a new transport cannot ship without it by omission; it has to
    /// delete a dependency to lose it.
    /// <para>
    /// The window is fixed, not sliding: a client gets N requests per whole second and the counter
    /// resets. A sliding window would be fairer and would also cost a timestamp per request per
    /// actor, which is exactly the allocation a flood would weaponise.
    /// </para>
    /// </remarks>
    public sealed class RbxNetworkRateLimiter
    {
        /// <summary>The default budget: 500 client requests per second, per group.</summary>
        public const int DefaultMaxClientRequestsPerSecond = 500;

        private sealed class Window
        {
            public double StartedAt;
            public int Accepted;
        }

        private readonly Dictionary<string, Dictionary<RbxNetworkRateGroup, Window>> _windows =
            new(StringComparer.Ordinal);
        private readonly Func<double> _clockSeconds;
        private readonly int _maxRequestsPerSecond;

        /// <summary>Creates a limiter over a clock, in seconds.</summary>
        public RbxNetworkRateLimiter(int maxClientRequestsPerSecond, Func<double> clockSeconds)
        {
            if (maxClientRequestsPerSecond <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxClientRequestsPerSecond),
                    maxClientRequestsPerSecond,
                    "A budget of zero would refuse every client request, including the first.");
            }

            _maxRequestsPerSecond = maxClientRequestsPerSecond;
            _clockSeconds = clockSeconds ?? throw new ArgumentNullException(nameof(clockSeconds));
        }

        /// <summary>The configured budget, per actor per group per second.</summary>
        public int MaxClientRequestsPerSecond => _maxRequestsPerSecond;

        /// <summary>How many actors currently hold a window; used to prove bounded churn.</summary>
        public int TrackedActorCount => _windows.Count;

        /// <summary>
        /// Counts one client request against <paramref name="actorId"/>'s budget.
        /// </summary>
        /// <exception cref="RbxError">The budget for this group is already spent this second.</exception>
        public void Admit(string actorId, RbxNetworkRateGroup rateGroup)
        {
            if (string.IsNullOrEmpty(actorId))
            {
                throw new ArgumentException(
                    "A request with no actor cannot be budgeted; the bridge must resolve the sender "
                    + "from its own connection map before admitting it.", nameof(actorId));
            }

            double now = _clockSeconds();
            if (!_windows.TryGetValue(actorId, out Dictionary<RbxNetworkRateGroup, Window> groups))
            {
                groups = new Dictionary<RbxNetworkRateGroup, Window>();
                _windows.Add(actorId, groups);
            }

            // WHY `now < window.StartedAt` also resets: a clock that steps backwards (a host machine
            // correcting its time) would otherwise freeze the window open and refuse every request
            // until the clock caught up.
            if (!groups.TryGetValue(rateGroup, out Window window)
                || now - window.StartedAt >= 1d
                || now < window.StartedAt)
            {
                window = new Window { StartedAt = now };
                groups[rateGroup] = window;
            }

            if (window.Accepted >= _maxRequestsPerSecond)
            {
                throw new RbxError(
                    RbxErrorCode.BudgetExceeded,
                    "actor '" + actorId + "' cannot send a client network request: network request "
                    + "rate quota reached (limit " + _maxRequestsPerSecond
                    + " requests/s) for " + GroupName(rateGroup),
                    "reduce the request rate or configure a higher admission limit");
            }

            window.Accepted++;
        }

        /// <summary>Releases an actor's windows when it disconnects.</summary>
        public void Forget(string actorId)
        {
            if (!string.IsNullOrEmpty(actorId))
            {
                _windows.Remove(actorId);
            }
        }

        /// <summary>Human-readable group name used in the budget error.</summary>
        public static string GroupName(RbxNetworkRateGroup rateGroup)
        {
            switch (rateGroup)
            {
                case RbxNetworkRateGroup.ReliableRemoteEvent: return "reliable RemoteEvent";
                case RbxNetworkRateGroup.UnreliableRemoteEvent: return "UnreliableRemoteEvent";
                case RbxNetworkRateGroup.RemoteFunction: return "RemoteFunction";
                default: return rateGroup.ToString();
            }
        }
    }
}
