using System;

namespace CoreAI.Ai.SelfRepair
{
    /// <summary>When the runtime repair loop acts.</summary>
    public enum LuaModRuntimeRepairMode
    {
        /// <summary>Nothing is repaired; failing mods are still reported once each.</summary>
        Off = 0,

        /// <summary>A failing mod is reported with a <c>/fixmod</c> hint; the AI repairs it only when asked.</summary>
        AskFirst = 1,

        /// <summary>A failing mod that qualifies is repaired without asking.</summary>
        Automatic = 2
    }

    /// <summary>Which failing mods the loop may rewrite without being asked.</summary>
    public enum LuaModRuntimeRepairScope
    {
        /// <summary>Only a mod whose current source the AI wrote through its tools.</summary>
        AiWrittenOnly = 0,

        /// <summary>Every mod, including ones the player wrote by hand and bundled samples.</summary>
        AllMods = 1
    }

    /// <summary>
    /// The numbers of the runtime repair loop: what opens an incident, how many attempts it gets and how far apart,
    /// how a fix is verified, and the guards against a repair storm. Engine-free; a Unity settings object maps onto it.
    /// </summary>
    public sealed class LuaModRuntimeRepairOptions
    {
        /// <summary>Consecutive failed calls that open an incident (a handler failing every frame reaches it in 3 frames).</summary>
        public const int DefaultStreakThreshold = 3;

        /// <summary>Errors from the same site within <see cref="DefaultSameSiteWindowSeconds"/> that open an incident.</summary>
        public const int DefaultSameSiteThreshold = 3;

        /// <summary>Window for <see cref="DefaultSameSiteThreshold"/>.</summary>
        public const double DefaultSameSiteWindowSeconds = 30d;

        /// <summary>Budget trips within <see cref="DefaultBudgetTripWindowSeconds"/> that open an incident.</summary>
        public const int DefaultBudgetTripThreshold = 2;

        /// <summary>Window for <see cref="DefaultBudgetTripThreshold"/>.</summary>
        public const double DefaultBudgetTripWindowSeconds = 30d;

        /// <summary>The runtime's quarantine streak (<c>LuaCsModRuntime.DefaultMaxErrorsBeforeQuarantine</c>).</summary>
        public const int DefaultQuarantineStreak = 8;

        /// <summary>Repair attempts per incident.</summary>
        public const int DefaultMaxAttemptsPerIncident = 3;

        /// <summary>Wait before the second attempt; each later wait is <see cref="DefaultBackoffMultiplier"/> times longer.</summary>
        public const double DefaultFirstBackoffSeconds = 10d;

        /// <summary>Growth of the wait between attempts.</summary>
        public const double DefaultBackoffMultiplier = 3d;

        /// <summary>Seconds a fixed mod must run without an error before the fix is accepted.</summary>
        public const double DefaultVerifySeconds = 5d;

        /// <summary>A relapse within this many seconds of a fix continues the same incident and its attempt count.</summary>
        public const double DefaultRelapseWindowSeconds = 120d;

        /// <summary>Incidents one mod may open in a session before the loop leaves it to the player.</summary>
        public const int DefaultMaxIncidentsPerMod = 2;

        /// <summary>Mods waiting for a repair turn; a mod past it is reported and not queued.</summary>
        public const int DefaultMaxQueuedMods = 3;

        /// <summary>Repair turns allowed per <see cref="DefaultTurnWindowSeconds"/>, across all mods.</summary>
        public const int DefaultMaxTurnsPerWindow = 6;

        /// <summary>Window for <see cref="DefaultMaxTurnsPerWindow"/>.</summary>
        public const double DefaultTurnWindowSeconds = 600d;

        /// <summary>A repair turn still running after this many seconds is stopped and counts as a failed attempt.</summary>
        public const double DefaultTurnTimeoutSeconds = 120d;

        /// <summary>Whether and how the loop acts.</summary>
        public LuaModRuntimeRepairMode Mode { get; set; } = LuaModRuntimeRepairMode.Automatic;

        /// <summary>Which mods the loop may rewrite without being asked.</summary>
        public LuaModRuntimeRepairScope Scope { get; set; } = LuaModRuntimeRepairScope.AiWrittenOnly;

        /// <inheritdoc cref="DefaultStreakThreshold"/>
        public int StreakThreshold { get; set; } = DefaultStreakThreshold;

        /// <inheritdoc cref="DefaultSameSiteThreshold"/>
        public int SameSiteThreshold { get; set; } = DefaultSameSiteThreshold;

        /// <inheritdoc cref="DefaultSameSiteWindowSeconds"/>
        public double SameSiteWindowSeconds { get; set; } = DefaultSameSiteWindowSeconds;

        /// <inheritdoc cref="DefaultBudgetTripThreshold"/>
        public int BudgetTripThreshold { get; set; } = DefaultBudgetTripThreshold;

        /// <inheritdoc cref="DefaultBudgetTripWindowSeconds"/>
        public double BudgetTripWindowSeconds { get; set; } = DefaultBudgetTripWindowSeconds;

        /// <inheritdoc cref="DefaultQuarantineStreak"/>
        public int QuarantineStreak { get; set; } = DefaultQuarantineStreak;

        /// <inheritdoc cref="DefaultMaxAttemptsPerIncident"/>
        public int MaxAttemptsPerIncident { get; set; } = DefaultMaxAttemptsPerIncident;

        /// <inheritdoc cref="DefaultFirstBackoffSeconds"/>
        public double FirstBackoffSeconds { get; set; } = DefaultFirstBackoffSeconds;

        /// <inheritdoc cref="DefaultBackoffMultiplier"/>
        public double BackoffMultiplier { get; set; } = DefaultBackoffMultiplier;

        /// <inheritdoc cref="DefaultVerifySeconds"/>
        public double VerifySeconds { get; set; } = DefaultVerifySeconds;

        /// <inheritdoc cref="DefaultRelapseWindowSeconds"/>
        public double RelapseWindowSeconds { get; set; } = DefaultRelapseWindowSeconds;

        /// <inheritdoc cref="DefaultMaxIncidentsPerMod"/>
        public int MaxIncidentsPerMod { get; set; } = DefaultMaxIncidentsPerMod;

        /// <inheritdoc cref="DefaultMaxQueuedMods"/>
        public int MaxQueuedMods { get; set; } = DefaultMaxQueuedMods;

        /// <inheritdoc cref="DefaultMaxTurnsPerWindow"/>
        public int MaxTurnsPerWindow { get; set; } = DefaultMaxTurnsPerWindow;

        /// <inheritdoc cref="DefaultTurnWindowSeconds"/>
        public double TurnWindowSeconds { get; set; } = DefaultTurnWindowSeconds;

        /// <inheritdoc cref="DefaultTurnTimeoutSeconds"/>
        public double TurnTimeoutSeconds { get; set; } = DefaultTurnTimeoutSeconds;

        /// <summary>The wait after attempt <paramref name="attempt"/> (1-based) failed.</summary>
        public double BackoffAfter(int attempt)
        {
            double wait = Math.Max(0d, FirstBackoffSeconds);
            double factor = Math.Max(1d, BackoffMultiplier);
            for (int index = 1; index < attempt; index++)
            {
                wait *= factor;
            }

            return wait;
        }

        /// <summary>A copy with every value clamped to a usable range.</summary>
        public LuaModRuntimeRepairOptions Normalized()
        {
            return new LuaModRuntimeRepairOptions
            {
                Mode = Mode,
                Scope = Scope,
                StreakThreshold = Math.Max(1, StreakThreshold),
                SameSiteThreshold = Math.Max(1, SameSiteThreshold),
                SameSiteWindowSeconds = Math.Max(0.1d, SameSiteWindowSeconds),
                BudgetTripThreshold = Math.Max(1, BudgetTripThreshold),
                BudgetTripWindowSeconds = Math.Max(0.1d, BudgetTripWindowSeconds),
                QuarantineStreak = Math.Max(1, QuarantineStreak),
                MaxAttemptsPerIncident = Math.Max(1, MaxAttemptsPerIncident),
                FirstBackoffSeconds = Math.Max(0d, FirstBackoffSeconds),
                BackoffMultiplier = Math.Max(1d, BackoffMultiplier),
                VerifySeconds = Math.Max(0d, VerifySeconds),
                RelapseWindowSeconds = Math.Max(0d, RelapseWindowSeconds),
                MaxIncidentsPerMod = Math.Max(1, MaxIncidentsPerMod),
                MaxQueuedMods = Math.Max(1, MaxQueuedMods),
                MaxTurnsPerWindow = Math.Max(1, MaxTurnsPerWindow),
                TurnWindowSeconds = Math.Max(1d, TurnWindowSeconds),
                TurnTimeoutSeconds = Math.Max(1d, TurnTimeoutSeconds)
            };
        }
    }
}
