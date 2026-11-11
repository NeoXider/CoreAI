using System;
using System.Collections.Generic;

namespace CoreAI.Ai.SelfRepair
{
    /// <summary>Why an incident was opened.</summary>
    public enum LuaModRepairTrigger
    {
        /// <summary>No incident.</summary>
        None = 0,

        /// <summary>Consecutive failed calls reached the streak threshold: a handler that fails on every call or frame.</summary>
        Streak = 1,

        /// <summary>The same site failed repeatedly within the window while other calls of the mod succeeded.</summary>
        RepeatedSite = 2,

        /// <summary>The instruction, time or memory budget tripped repeatedly within the window.</summary>
        BudgetTrips = 3,

        /// <summary>The streak reached the runtime's quarantine: the mod's handlers are suspended.</summary>
        Quarantined = 4
    }

    /// <summary>Where a mod stands in the repair loop.</summary>
    public enum LuaModRepairPhase
    {
        /// <summary>No open incident.</summary>
        Healthy = 0,

        /// <summary>An incident is open and waits for its first attempt (or for the player's consent).</summary>
        Detected = 1,

        /// <summary>A repair turn is running.</summary>
        Repairing = 2,

        /// <summary>A fix landed; the mod must run the verify window without an error.</summary>
        Verifying = 3,

        /// <summary>An attempt failed; the next one waits for its backoff.</summary>
        Backoff = 4,

        /// <summary>The fix was accepted; a relapse within the relapse window continues the incident.</summary>
        Fixed = 5,

        /// <summary>The loop leaves the mod alone until someone else changes it or the player asks again.</summary>
        GaveUp = 6
    }

    /// <summary>Why the loop left a mod alone.</summary>
    public enum LuaModRepairGiveUpReason
    {
        /// <summary>Not given up.</summary>
        None = 0,

        /// <summary>Every attempt of the incident failed.</summary>
        AttemptsExhausted = 1,

        /// <summary>The mod opened more incidents in this session than allowed.</summary>
        IncidentsExhausted = 2,

        /// <summary>The player stopped the repair.</summary>
        StoppedByPlayer = 3,

        /// <summary>No model answers, so nothing can repair the mod.</summary>
        Offline = 4,

        /// <summary>The settings say no: the loop is off, asks first, or the AI did not write the mod.</summary>
        NotAllowed = 5,

        /// <summary>Too many mods were waiting for a repair already.</summary>
        TooManyFailing = 6,

        /// <summary>The player's <c>/undo</c> or <c>/redo</c> put back another version of the mod.</summary>
        TakenBackByPlayer = 7
    }

    /// <summary>What one recorded error changed.</summary>
    public enum LuaModRepairErrorOutcome
    {
        /// <summary>Nothing: empty mod id.</summary>
        Ignored = 0,

        /// <summary>Counted; no change of phase.</summary>
        Counted = 1,

        /// <summary>A new incident opened.</summary>
        IncidentOpened = 2,

        /// <summary>A fixed mod failed again within the relapse window; the next attempt is scheduled.</summary>
        Relapsed = 3,

        /// <summary>The mod failed while its fix was verified; the next attempt is scheduled.</summary>
        AttemptFailed = 4,

        /// <summary>The mod failed again and the loop now leaves it alone.</summary>
        GaveUp = 5
    }

    /// <summary>How a repair turn ended, as the turn runner reports it.</summary>
    public enum LuaModRepairTurnEnd
    {
        /// <summary>The model finished its answer.</summary>
        Completed = 0,

        /// <summary>The turn failed (provider, transport or pipeline error).</summary>
        Failed = 1,

        /// <summary>The player stopped the turn.</summary>
        Cancelled = 2,

        /// <summary>The turn ran past the repair turn timeout.</summary>
        TimedOut = 3,

        /// <summary>No model answers (offline backend, no AI service).</summary>
        Unavailable = 4
    }

    /// <summary>What ending an attempt decided.</summary>
    public enum LuaModRepairAttemptOutcome
    {
        /// <summary>The mod was not in a running attempt.</summary>
        None = 0,

        /// <summary>The fix landed and is being verified.</summary>
        Verifying = 1,

        /// <summary>The attempt failed; the next one is scheduled after its backoff.</summary>
        RetryScheduled = 2,

        /// <summary>The attempt failed or was stopped and the loop leaves the mod alone.</summary>
        GaveUp = 3
    }

    /// <summary>One mod's state in the repair loop; read-only outside the policy.</summary>
    public sealed class LuaModRepairIncident
    {
        internal const int ErrorRingCapacity = 8;

        internal readonly double[] ErrorTimes = new double[ErrorRingCapacity];
        internal readonly string[] ErrorSites = new string[ErrorRingCapacity];
        internal readonly bool[] ErrorBudget = new bool[ErrorRingCapacity];
        internal int ErrorCount;
        internal int ErrorNext;

        internal LuaModRepairIncident(string modId)
        {
            ModId = modId;
        }

        /// <summary>The mod.</summary>
        public string ModId { get; }

        /// <summary>Where the mod stands.</summary>
        public LuaModRepairPhase Phase { get; internal set; }

        /// <summary>Why the current incident opened.</summary>
        public LuaModRepairTrigger Trigger { get; internal set; }

        /// <summary>Why the loop left the mod alone; <see cref="LuaModRepairGiveUpReason.None"/> otherwise.</summary>
        public LuaModRepairGiveUpReason GiveUpReason { get; internal set; }

        /// <summary>Attempts started in the current incident.</summary>
        public int Attempt { get; internal set; }

        /// <summary>Incidents opened in this session.</summary>
        public int Incidents { get; internal set; }

        /// <summary>The newest error message.</summary>
        public string LastError { get; internal set; } = "";

        /// <summary>The site of the newest error.</summary>
        public LuaModErrorSite LastSite { get; internal set; }

        /// <summary>When the scheduled attempt is due (phase <see cref="LuaModRepairPhase.Backoff"/>).</summary>
        public double NextAttemptAt { get; internal set; }

        /// <summary>When the running attempt started.</summary>
        public double AttemptStartedAt { get; internal set; }

        /// <summary>Whether the running attempt changed the mod.</summary>
        public bool FixLanded { get; internal set; }

        /// <summary>Errors since the running attempt's latest change of the mod.</summary>
        public int ErrorsSinceFix { get; internal set; }

        /// <summary>When the latest fix landed.</summary>
        public double FixedAt { get; internal set; }

        /// <summary>When the verify window ends.</summary>
        public double VerifyUntil { get; internal set; }
    }

    /// <summary>
    /// The engine-free decision core of the runtime repair loop: which run-time failures open an incident (a streak,
    /// repeats from one site, budget trips, a quarantine), how many attempts an incident gets and how far apart, when a
    /// fix counts as verified, and when the loop gives up. Time is passed in; nothing here runs on its own. Not
    /// thread-safe: the coordinator serializes every call.
    /// </summary>
    public sealed class LuaModRuntimeRepairPolicy
    {
        private readonly Dictionary<string, LuaModRepairIncident> _incidents = new(StringComparer.Ordinal);

        /// <summary>Creates the policy; null uses the defaults.</summary>
        public LuaModRuntimeRepairPolicy(LuaModRuntimeRepairOptions options = null)
        {
            Options = (options ?? new LuaModRuntimeRepairOptions()).Normalized();
        }

        /// <summary>The clamped numbers in use.</summary>
        public LuaModRuntimeRepairOptions Options { get; }

        /// <summary>Every tracked mod.</summary>
        public IEnumerable<LuaModRepairIncident> Incidents => _incidents.Values;

        /// <summary>The mod's state, or null when it never failed.</summary>
        public LuaModRepairIncident Get(string modId)
        {
            return _incidents.TryGetValue(Normalize(modId), out LuaModRepairIncident incident) ? incident : null;
        }

        /// <summary>The phase of the mod; <see cref="LuaModRepairPhase.Healthy"/> when untracked.</summary>
        public LuaModRepairPhase PhaseOf(string modId)
        {
            LuaModRepairIncident incident = Get(modId);
            return incident?.Phase ?? LuaModRepairPhase.Healthy;
        }

        /// <summary>Records one run-time error of a mod with the runtime's consecutive-error streak.</summary>
        public LuaModRepairErrorOutcome RecordError(string modId, string message, int consecutiveCount, double now)
        {
            string id = Normalize(modId);
            if (id.Length == 0)
            {
                return LuaModRepairErrorOutcome.Ignored;
            }

            LuaModRepairIncident incident = GetOrCreate(id);
            LuaModErrorSite site = LuaModErrorSite.Parse(message);
            incident.LastError = site.Message.Length > 0 ? site.Message : (message ?? "");
            incident.LastSite = site;
            Push(incident, now, site.Key, IsBudgetTrip(message));
            LuaModRepairTrigger trigger = Evaluate(incident, consecutiveCount, now, site.Key);
            switch (incident.Phase)
            {
                case LuaModRepairPhase.Healthy:
                    if (trigger == LuaModRepairTrigger.None)
                    {
                        return LuaModRepairErrorOutcome.Counted;
                    }

                    if (incident.Incidents >= Options.MaxIncidentsPerMod)
                    {
                        incident.Trigger = trigger;
                        GiveUp(incident, LuaModRepairGiveUpReason.IncidentsExhausted);
                        return LuaModRepairErrorOutcome.GaveUp;
                    }

                    incident.Incidents++;
                    incident.Attempt = 0;
                    incident.Trigger = trigger;
                    incident.GiveUpReason = LuaModRepairGiveUpReason.None;
                    incident.Phase = LuaModRepairPhase.Detected;
                    return LuaModRepairErrorOutcome.IncidentOpened;
                case LuaModRepairPhase.Fixed:
                    if (trigger == LuaModRepairTrigger.None)
                    {
                        return LuaModRepairErrorOutcome.Counted;
                    }

                    incident.Trigger = trigger;
                    return ScheduleRetryOrGiveUp(incident, now) == LuaModRepairAttemptOutcome.GaveUp
                        ? LuaModRepairErrorOutcome.GaveUp
                        : LuaModRepairErrorOutcome.Relapsed;
                case LuaModRepairPhase.Verifying:
                    return ScheduleRetryOrGiveUp(incident, now) == LuaModRepairAttemptOutcome.GaveUp
                        ? LuaModRepairErrorOutcome.GaveUp
                        : LuaModRepairErrorOutcome.AttemptFailed;
                case LuaModRepairPhase.Repairing:
                    if (incident.FixLanded)
                    {
                        incident.ErrorsSinceFix++;
                    }

                    return LuaModRepairErrorOutcome.Counted;
                default:
                    return LuaModRepairErrorOutcome.Counted;
            }
        }

        /// <summary>True when the mod waits for an attempt that may start now.</summary>
        public bool IsAttemptDue(string modId, double now)
        {
            LuaModRepairIncident incident = Get(modId);
            return incident != null &&
                   (incident.Phase == LuaModRepairPhase.Detected ||
                    (incident.Phase == LuaModRepairPhase.Backoff && now >= incident.NextAttemptAt));
        }

        /// <summary>Starts the next attempt when one is due; <paramref name="attempt"/> is its 1-based number.</summary>
        public bool TryBeginAttempt(string modId, double now, out int attempt)
        {
            attempt = 0;
            if (!IsAttemptDue(modId, now))
            {
                return false;
            }

            LuaModRepairIncident incident = Get(modId);
            incident.Attempt++;
            incident.Phase = LuaModRepairPhase.Repairing;
            incident.AttemptStartedAt = now;
            incident.FixLanded = false;
            incident.ErrorsSinceFix = 0;
            attempt = incident.Attempt;
            return true;
        }

        /// <summary>Takes back an attempt that could not start (the runner refused it); the mod is due again.</summary>
        public void AbortAttempt(string modId)
        {
            LuaModRepairIncident incident = Get(modId);
            if (incident == null || incident.Phase != LuaModRepairPhase.Repairing)
            {
                return;
            }

            incident.Attempt = Math.Max(0, incident.Attempt - 1);
            incident.Phase = LuaModRepairPhase.Detected;
        }

        /// <summary>The running attempt's model changed the mod (a successful AI reload).</summary>
        public void NoteFixLanded(string modId, double now)
        {
            LuaModRepairIncident incident = Get(modId);
            if (incident == null || incident.Phase != LuaModRepairPhase.Repairing)
            {
                return;
            }

            incident.FixLanded = true;
            incident.ErrorsSinceFix = 0;
            incident.FixedAt = now;
            ClearErrors(incident);
        }

        /// <summary>
        /// Someone other than a running repair attempt replaced the mod's source (the player, a host, the AI in an
        /// ordinary chat turn): its failures belong to old code, so the incident closes and a given-up mod is armed
        /// again. The session's incident count is kept.
        /// </summary>
        public void NoteReplaced(string modId)
        {
            LuaModRepairIncident incident = Get(modId);
            if (incident == null || incident.Phase == LuaModRepairPhase.Repairing)
            {
                return;
            }

            incident.Phase = LuaModRepairPhase.Healthy;
            incident.Trigger = LuaModRepairTrigger.None;
            incident.GiveUpReason = LuaModRepairGiveUpReason.None;
            incident.Attempt = 0;
            ClearErrors(incident);
        }

        /// <summary>Ends the running attempt and decides what follows.</summary>
        public LuaModRepairAttemptOutcome EndAttempt(string modId, LuaModRepairTurnEnd end, double now)
        {
            LuaModRepairIncident incident = Get(modId);
            if (incident == null || incident.Phase != LuaModRepairPhase.Repairing)
            {
                return LuaModRepairAttemptOutcome.None;
            }

            switch (end)
            {
                case LuaModRepairTurnEnd.Unavailable:
                    incident.Attempt = Math.Max(0, incident.Attempt - 1);
                    GiveUp(incident, LuaModRepairGiveUpReason.Offline);
                    return LuaModRepairAttemptOutcome.GaveUp;
                case LuaModRepairTurnEnd.Cancelled:
                    GiveUp(incident, LuaModRepairGiveUpReason.StoppedByPlayer);
                    return LuaModRepairAttemptOutcome.GaveUp;
            }

            if (!incident.FixLanded || incident.ErrorsSinceFix > 0)
            {
                return ScheduleRetryOrGiveUp(incident, now);
            }

            incident.Phase = LuaModRepairPhase.Verifying;
            incident.VerifyUntil = Math.Max(now, incident.FixedAt + Options.VerifySeconds);
            return LuaModRepairAttemptOutcome.Verifying;
        }

        /// <summary>
        /// Advances time: a verified fix becomes <see cref="LuaModRepairPhase.Fixed"/> (added to
        /// <paramref name="fixedNow"/>), and a fix older than the relapse window closes its incident.
        /// </summary>
        public void Tick(double now, List<LuaModRepairIncident> fixedNow)
        {
            foreach (LuaModRepairIncident incident in _incidents.Values)
            {
                if (incident.Phase == LuaModRepairPhase.Verifying && now >= incident.VerifyUntil)
                {
                    incident.Phase = LuaModRepairPhase.Fixed;
                    incident.FixedAt = now;
                    fixedNow?.Add(incident);
                }
                else if (incident.Phase == LuaModRepairPhase.Fixed && now >= incident.FixedAt + Options.RelapseWindowSeconds)
                {
                    incident.Phase = LuaModRepairPhase.Healthy;
                    incident.Trigger = LuaModRepairTrigger.None;
                    incident.Attempt = 0;
                }
            }
        }

        /// <summary>
        /// The player asked for a repair (<c>/fixmod</c>): a mod that failed, was left alone or waits for consent
        /// gets an incident with a fresh attempt budget. False when the mod never failed or a repair already runs.
        /// </summary>
        public bool Consent(string modId)
        {
            LuaModRepairIncident incident = Get(modId);
            if (incident == null || incident.Phase == LuaModRepairPhase.Repairing ||
                incident.Phase == LuaModRepairPhase.Verifying)
            {
                return false;
            }

            if (incident.Phase == LuaModRepairPhase.Healthy || incident.Phase == LuaModRepairPhase.Fixed)
            {
                if (incident.LastError.Length == 0)
                {
                    return false;
                }

                incident.Incidents++;
            }

            if (incident.Trigger == LuaModRepairTrigger.None)
            {
                incident.Trigger = LuaModRepairTrigger.RepeatedSite;
            }

            incident.Attempt = 0;
            incident.GiveUpReason = LuaModRepairGiveUpReason.None;
            incident.Phase = LuaModRepairPhase.Detected;
            return true;
        }

        /// <summary>Puts a mod that waits for consent or a model back to waiting; used when a turn could not start.</summary>
        public void ParkDetected(string modId, LuaModRepairGiveUpReason reason)
        {
            LuaModRepairIncident incident = Get(modId);
            if (incident != null && incident.Phase != LuaModRepairPhase.Repairing)
            {
                GiveUp(incident, reason);
            }
        }

        /// <summary>
        /// The player's <c>/undo</c> or <c>/redo</c> put back another version of the mod: the incident closes whatever
        /// phase it was in, a running attempt included, and the errors of the old code are dropped. Returns the phase
        /// it had. The session's incident count is kept.
        /// </summary>
        public LuaModRepairPhase CloseTakenBack(string modId)
        {
            LuaModRepairIncident incident = Get(modId);
            if (incident == null)
            {
                return LuaModRepairPhase.Healthy;
            }

            LuaModRepairPhase was = incident.Phase;
            incident.Phase = LuaModRepairPhase.Healthy;
            incident.Trigger = LuaModRepairTrigger.None;
            incident.GiveUpReason = LuaModRepairGiveUpReason.None;
            incident.Attempt = 0;
            incident.FixLanded = false;
            incident.ErrorsSinceFix = 0;
            ClearErrors(incident);
            return was;
        }

        /// <summary>
        /// True when the mod fails all the time rather than now and then: the incident opened on a streak, on budget
        /// trips or on the runtime's quarantine, or the mod's recent errors (at least
        /// <see cref="ContinuousErrorCount"/> within <see cref="ContinuousErrorWindowSeconds"/>) came on average at least
        /// once per <see cref="ContinuousErrorGapSeconds"/>. A mod that fails at one site now and then while the rest of
        /// it works is not.
        /// </summary>
        public bool IsFailingContinuously(string modId, double now)
        {
            LuaModRepairIncident incident = Get(modId);
            if (incident == null)
            {
                return false;
            }

            if (incident.Trigger == LuaModRepairTrigger.Streak || incident.Trigger == LuaModRepairTrigger.BudgetTrips ||
                incident.Trigger == LuaModRepairTrigger.Quarantined)
            {
                return true;
            }

            int count = 0;
            double oldest = double.PositiveInfinity;
            double newest = double.NegativeInfinity;
            for (int index = 0; index < incident.ErrorCount; index++)
            {
                double at = incident.ErrorTimes[index];
                if (now - at > ContinuousErrorWindowSeconds)
                {
                    continue;
                }

                count++;
                oldest = Math.Min(oldest, at);
                newest = Math.Max(newest, at);
            }

            return count >= ContinuousErrorCount && newest - oldest <= (count - 1) * ContinuousErrorGapSeconds;
        }

        /// <summary>Errors that must be seen before their rate says anything (<see cref="IsFailingContinuously"/>).</summary>
        public const int ContinuousErrorCount = 3;

        /// <summary>How far back <see cref="IsFailingContinuously"/> looks.</summary>
        public const double ContinuousErrorWindowSeconds = 30d;

        /// <summary>The average gap between errors at or below which a mod fails all the time.</summary>
        public const double ContinuousErrorGapSeconds = 1d;

        /// <summary>Drops every record of a mod (it was unloaded).</summary>
        public void Forget(string modId)
        {
            _incidents.Remove(Normalize(modId));
        }

        /// <summary>True when the text carries one of the runtime's budget-trip markers.</summary>
        public static bool IsBudgetTrip(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return false;
            }

            return message.IndexOf("EXCEEDED_", StringComparison.Ordinal) >= 0 ||
                   message.IndexOf("resume exceeded", StringComparison.Ordinal) >= 0 ||
                   message.IndexOf("Lua exceeded", StringComparison.Ordinal) >= 0;
        }

        private LuaModRepairAttemptOutcome ScheduleRetryOrGiveUp(LuaModRepairIncident incident, double now)
        {
            if (incident.Attempt >= Options.MaxAttemptsPerIncident)
            {
                GiveUp(incident, LuaModRepairGiveUpReason.AttemptsExhausted);
                return LuaModRepairAttemptOutcome.GaveUp;
            }

            incident.Phase = LuaModRepairPhase.Backoff;
            incident.NextAttemptAt = now + Options.BackoffAfter(Math.Max(1, incident.Attempt));
            return LuaModRepairAttemptOutcome.RetryScheduled;
        }

        private static void GiveUp(LuaModRepairIncident incident, LuaModRepairGiveUpReason reason)
        {
            incident.Phase = LuaModRepairPhase.GaveUp;
            incident.GiveUpReason = reason;
        }

        private LuaModRepairTrigger Evaluate(LuaModRepairIncident incident, int streak, double now, string siteKey)
        {
            if (streak >= Options.QuarantineStreak)
            {
                return LuaModRepairTrigger.Quarantined;
            }

            if (CountRecent(incident, now, Options.BudgetTripWindowSeconds, null, true) >= Options.BudgetTripThreshold)
            {
                return LuaModRepairTrigger.BudgetTrips;
            }

            if (streak >= Options.StreakThreshold)
            {
                return LuaModRepairTrigger.Streak;
            }

            if (CountRecent(incident, now, Options.SameSiteWindowSeconds, siteKey, false) >= Options.SameSiteThreshold)
            {
                return LuaModRepairTrigger.RepeatedSite;
            }

            return LuaModRepairTrigger.None;
        }

        private static int CountRecent(LuaModRepairIncident incident, double now, double window, string siteKey,
            bool budgetOnly)
        {
            int count = 0;
            for (int index = 0; index < incident.ErrorCount; index++)
            {
                if (now - incident.ErrorTimes[index] > window)
                {
                    continue;
                }

                if (budgetOnly ? incident.ErrorBudget[index] : string.Equals(incident.ErrorSites[index], siteKey, StringComparison.Ordinal))
                {
                    count++;
                }
            }

            return count;
        }

        private static void Push(LuaModRepairIncident incident, double now, string siteKey, bool budget)
        {
            int slot = incident.ErrorNext;
            incident.ErrorTimes[slot] = now;
            incident.ErrorSites[slot] = siteKey ?? "";
            incident.ErrorBudget[slot] = budget;
            incident.ErrorNext = (slot + 1) % LuaModRepairIncident.ErrorRingCapacity;
            if (incident.ErrorCount < LuaModRepairIncident.ErrorRingCapacity)
            {
                incident.ErrorCount++;
            }
        }

        private static void ClearErrors(LuaModRepairIncident incident)
        {
            incident.ErrorCount = 0;
            incident.ErrorNext = 0;
        }

        private LuaModRepairIncident GetOrCreate(string id)
        {
            if (!_incidents.TryGetValue(id, out LuaModRepairIncident incident))
            {
                incident = new LuaModRepairIncident(id);
                _incidents[id] = incident;
            }

            return incident;
        }

        private static string Normalize(string modId)
        {
            return (modId ?? "").Trim();
        }
    }
}
