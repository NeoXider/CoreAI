using System.Collections.Generic;
using CoreAI.Ai.SelfRepair;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.SelfRepair
{
    /// <summary>
    /// The decision core of the runtime repair loop: which failures open an incident, the attempt and backoff numbers,
    /// verification, relapse and the give-up rules. Every trigger has a twin that must not open an incident.
    /// </summary>
    public sealed class LuaModRuntimeRepairPolicyEditModeTests
    {
        private const string Boom = "[string \"sandbox_chunk\"]:12: attempt to index a nil value (local 'part')";

        private static LuaModRuntimeRepairPolicy Policy()
        {
            return new LuaModRuntimeRepairPolicy(new LuaModRuntimeRepairOptions());
        }

        [Test]
        public void Defaults_AreTheDocumentedNumbers()
        {
            LuaModRuntimeRepairOptions options = new LuaModRuntimeRepairOptions().Normalized();

            Assert.AreEqual(3, options.StreakThreshold);
            Assert.AreEqual(3, options.SameSiteThreshold);
            Assert.AreEqual(30d, options.SameSiteWindowSeconds);
            Assert.AreEqual(2, options.BudgetTripThreshold);
            Assert.AreEqual(8, options.QuarantineStreak);
            Assert.AreEqual(3, options.MaxAttemptsPerIncident);
            Assert.AreEqual(10d, options.BackoffAfter(1));
            Assert.AreEqual(30d, options.BackoffAfter(2));
            Assert.AreEqual(5d, options.VerifySeconds);
            Assert.AreEqual(120d, options.RelapseWindowSeconds);
            Assert.AreEqual(2, options.MaxIncidentsPerMod);
            Assert.AreEqual(3, options.MaxQueuedMods);
            Assert.AreEqual(6, options.MaxTurnsPerWindow);
            Assert.AreEqual(LuaModRuntimeRepairMode.Automatic, options.Mode);
            Assert.AreEqual(LuaModRuntimeRepairScope.AiWrittenOnly, options.Scope);
        }

        [Test]
        public void Streak_OfThree_OpensAnIncident()
        {
            LuaModRuntimeRepairPolicy policy = Policy();

            Assert.AreEqual(LuaModRepairErrorOutcome.Counted, policy.RecordError("tower", Boom, 1, 0d));
            Assert.AreEqual(LuaModRepairErrorOutcome.Counted, policy.RecordError("tower", Boom, 2, 0.02d));
            Assert.AreEqual(LuaModRepairErrorOutcome.IncidentOpened, policy.RecordError("tower", Boom, 3, 0.04d));

            LuaModRepairIncident incident = policy.Get("tower");
            Assert.AreEqual(LuaModRepairPhase.Detected, incident.Phase);
            Assert.AreEqual(LuaModRepairTrigger.Streak, incident.Trigger);
            Assert.AreEqual(12, incident.LastSite.Line);
            Assert.AreEqual("tower:12", incident.LastSite.Describe("tower"));
        }

        [Test]
        public void SingleError_ThatRecovers_OpensNothing()
        {
            LuaModRuntimeRepairPolicy policy = Policy();

            Assert.AreEqual(LuaModRepairErrorOutcome.Counted, policy.RecordError("tower", Boom, 1, 0d));
            Assert.AreEqual(LuaModRepairErrorOutcome.Counted, policy.RecordError("tower", Boom, 1, 40d),
                "the streak reset in between and the first error is outside the same-site window");

            Assert.AreEqual(LuaModRepairPhase.Healthy, policy.PhaseOf("tower"));
        }

        [Test]
        public void SameSite_ThreeTimesInThirtySeconds_OpensAnIncident_WhileTheStreakStaysAtOne()
        {
            LuaModRuntimeRepairPolicy policy = Policy();

            policy.RecordError("door", Boom, 1, 0d);
            policy.RecordError("door", Boom, 1, 10d);
            LuaModRepairErrorOutcome third = policy.RecordError("door", Boom, 1, 20d);

            Assert.AreEqual(LuaModRepairErrorOutcome.IncidentOpened, third);
            Assert.AreEqual(LuaModRepairTrigger.RepeatedSite, policy.Get("door").Trigger);
        }

        [Test]
        public void SameSite_SpreadOverMoreThanTheWindow_OpensNothing()
        {
            LuaModRuntimeRepairPolicy policy = Policy();

            policy.RecordError("door", Boom, 1, 0d);
            policy.RecordError("door", Boom, 1, 20d);
            LuaModRepairErrorOutcome third = policy.RecordError("door", Boom, 1, 40d);

            Assert.AreEqual(LuaModRepairErrorOutcome.Counted, third);
        }

        [Test]
        public void DifferentSites_DoNotAddUp()
        {
            LuaModRuntimeRepairPolicy policy = Policy();

            policy.RecordError("door", "[string \"sandbox_chunk\"]:3: a", 1, 0d);
            policy.RecordError("door", "[string \"sandbox_chunk\"]:4: b", 1, 1d);
            LuaModRepairErrorOutcome third = policy.RecordError("door", "[string \"sandbox_chunk\"]:5: c", 1, 2d);

            Assert.AreEqual(LuaModRepairErrorOutcome.Counted, third);
        }

        [Test]
        public void BudgetTrips_TwiceInTheWindow_OpenAnIncident_OneTripDoesNot()
        {
            LuaModRuntimeRepairPolicy policy = Policy();
            const string trip = "sandbox: EXCEEDED_HARD_LIMIT_STEPS (100000) at line 7";

            Assert.AreEqual(LuaModRepairErrorOutcome.Counted, policy.RecordError("loop", trip, 1, 0d));
            Assert.AreEqual(LuaModRepairErrorOutcome.IncidentOpened, policy.RecordError("loop", trip, 1, 5d));
            Assert.AreEqual(LuaModRepairTrigger.BudgetTrips, policy.Get("loop").Trigger);
            Assert.AreEqual(7, policy.Get("loop").LastSite.Line);
        }

        [Test]
        public void Quarantine_Streak_IsReportedAsQuarantined()
        {
            LuaModRuntimeRepairPolicy policy = new(new LuaModRuntimeRepairOptions { StreakThreshold = 20 });

            for (int streak = 1; streak < 8; streak++)
            {
                Assert.AreEqual(LuaModRepairErrorOutcome.Counted, policy.RecordError("q", "boom " + streak, streak, streak));
            }

            Assert.AreEqual(LuaModRepairErrorOutcome.IncidentOpened, policy.RecordError("q", "boom 8", 8, 8d));
            Assert.AreEqual(LuaModRepairTrigger.Quarantined, policy.Get("q").Trigger);
        }

        [Test]
        public void Attempts_BackOff_TenThenThirtySeconds_AndGiveUpAfterThree()
        {
            LuaModRuntimeRepairPolicy policy = Policy();
            Open(policy, "tower", 0d);

            Assert.IsTrue(policy.TryBeginAttempt("tower", 1d, out int first));
            Assert.AreEqual(1, first);
            Assert.AreEqual(LuaModRepairAttemptOutcome.RetryScheduled,
                policy.EndAttempt("tower", LuaModRepairTurnEnd.Completed, 2d), "no change landed");
            Assert.AreEqual(12d, policy.Get("tower").NextAttemptAt);
            Assert.IsFalse(policy.TryBeginAttempt("tower", 11.9d, out _), "still backing off");

            Assert.IsTrue(policy.TryBeginAttempt("tower", 12d, out int second));
            Assert.AreEqual(2, second);
            policy.NoteFixLanded("tower", 13d);
            Assert.AreEqual(LuaModRepairAttemptOutcome.Verifying,
                policy.EndAttempt("tower", LuaModRepairTurnEnd.Completed, 14d));
            Assert.AreEqual(18d, policy.Get("tower").VerifyUntil);
            Assert.AreEqual(LuaModRepairErrorOutcome.AttemptFailed, policy.RecordError("tower", Boom, 1, 15d));
            Assert.AreEqual(45d, policy.Get("tower").NextAttemptAt);

            Assert.IsTrue(policy.TryBeginAttempt("tower", 45d, out int third));
            Assert.AreEqual(3, third);
            Assert.AreEqual(LuaModRepairAttemptOutcome.GaveUp,
                policy.EndAttempt("tower", LuaModRepairTurnEnd.Failed, 46d));
            Assert.AreEqual(LuaModRepairGiveUpReason.AttemptsExhausted, policy.Get("tower").GiveUpReason);
            Assert.IsFalse(policy.TryBeginAttempt("tower", 1000d, out _), "a given-up mod is left alone");
            Assert.AreEqual(LuaModRepairErrorOutcome.Counted, policy.RecordError("tower", Boom, 9, 1001d),
                "errors of a given-up mod are counted, not reported again");
        }

        [Test]
        public void VerifiedFix_BecomesFixed_AndARelapseContinuesTheIncident()
        {
            LuaModRuntimeRepairPolicy policy = Policy();
            Open(policy, "tower", 0d);
            policy.TryBeginAttempt("tower", 0d, out _);
            policy.NoteFixLanded("tower", 1d);
            policy.EndAttempt("tower", LuaModRepairTurnEnd.Completed, 2d);

            List<LuaModRepairIncident> fixedNow = new();
            policy.Tick(5.9d, fixedNow);
            Assert.IsEmpty(fixedNow, "the verify window has not passed");
            policy.Tick(6d, fixedNow);
            Assert.AreEqual(1, fixedNow.Count);
            Assert.AreEqual(LuaModRepairPhase.Fixed, policy.PhaseOf("tower"));

            policy.RecordError("tower", Boom, 1, 30d);
            policy.RecordError("tower", Boom, 2, 30.1d);
            Assert.AreEqual(LuaModRepairErrorOutcome.Relapsed, policy.RecordError("tower", Boom, 3, 30.2d));
            Assert.AreEqual(1, policy.Get("tower").Attempt, "the relapse keeps the incident's attempt count");
            Assert.AreEqual(LuaModRepairPhase.Backoff, policy.PhaseOf("tower"));
        }

        [Test]
        public void FixOlderThanTheRelapseWindow_ClosesTheIncident_AndTheNextFailureIsANewOne()
        {
            LuaModRuntimeRepairPolicy policy = Policy();
            Open(policy, "tower", 0d);
            policy.TryBeginAttempt("tower", 0d, out _);
            policy.NoteFixLanded("tower", 1d);
            policy.EndAttempt("tower", LuaModRepairTurnEnd.Completed, 1d);
            policy.Tick(6d, null);
            policy.Tick(127d, null);

            Assert.AreEqual(LuaModRepairPhase.Healthy, policy.PhaseOf("tower"));
            Assert.AreEqual(LuaModRepairErrorOutcome.IncidentOpened, Open(policy, "tower", 200d));
            Assert.AreEqual(2, policy.Get("tower").Incidents);
            policy.TryBeginAttempt("tower", 200d, out _);
            policy.NoteFixLanded("tower", 201d);
            policy.EndAttempt("tower", LuaModRepairTurnEnd.Completed, 201d);
            policy.Tick(206d, null);
            policy.Tick(400d, null);
            Assert.AreEqual(LuaModRepairErrorOutcome.GaveUp, Open(policy, "tower", 500d),
                "a third incident in one session is left to the player");
            Assert.AreEqual(LuaModRepairGiveUpReason.IncidentsExhausted, policy.Get("tower").GiveUpReason);
        }

        [Test]
        public void ErrorsAfterTheFixLandedInTheSameTurn_FailTheAttempt()
        {
            LuaModRuntimeRepairPolicy policy = Policy();
            Open(policy, "tower", 0d);
            policy.TryBeginAttempt("tower", 0d, out _);
            policy.RecordError("tower", Boom, 4, 0.5d);
            policy.NoteFixLanded("tower", 1d);
            policy.RecordError("tower", Boom, 1, 1.5d);

            Assert.AreEqual(LuaModRepairAttemptOutcome.RetryScheduled,
                policy.EndAttempt("tower", LuaModRepairTurnEnd.Completed, 2d));
        }

        [Test]
        public void PlayerStop_GivesUp_AndConsentArmsItAgain()
        {
            LuaModRuntimeRepairPolicy policy = Policy();
            Open(policy, "tower", 0d);
            policy.TryBeginAttempt("tower", 0d, out _);

            Assert.AreEqual(LuaModRepairAttemptOutcome.GaveUp, policy.EndAttempt("tower", LuaModRepairTurnEnd.Cancelled, 1d));
            Assert.AreEqual(LuaModRepairGiveUpReason.StoppedByPlayer, policy.Get("tower").GiveUpReason);
            Assert.IsTrue(policy.Consent("tower"));
            Assert.IsTrue(policy.TryBeginAttempt("tower", 2d, out int attempt));
            Assert.AreEqual(1, attempt, "consent starts with a fresh attempt budget");
        }

        [Test]
        public void Unavailable_DoesNotSpendAnAttempt()
        {
            LuaModRuntimeRepairPolicy policy = Policy();
            Open(policy, "tower", 0d);
            policy.TryBeginAttempt("tower", 0d, out _);

            policy.EndAttempt("tower", LuaModRepairTurnEnd.Unavailable, 1d);

            Assert.AreEqual(0, policy.Get("tower").Attempt);
            Assert.AreEqual(LuaModRepairGiveUpReason.Offline, policy.Get("tower").GiveUpReason);
        }

        [Test]
        public void ReplacedBySomeoneElse_ClosesTheIncident()
        {
            LuaModRuntimeRepairPolicy policy = Policy();
            Open(policy, "tower", 0d);
            policy.ParkDetected("tower", LuaModRepairGiveUpReason.NotAllowed);

            policy.NoteReplaced("tower");

            Assert.AreEqual(LuaModRepairPhase.Healthy, policy.PhaseOf("tower"));
            Assert.AreEqual(LuaModRepairErrorOutcome.Counted, policy.RecordError("tower", Boom, 1, 1d),
                "the old code's errors were forgotten with it");
        }

        [Test]
        public void Consent_ForAModThatNeverFailed_IsRefused()
        {
            Assert.IsFalse(Policy().Consent("never"));
        }

        [TestCase("[string \"sandbox_chunk\"]:12: attempt to index nil", "sandbox_chunk", 12, "attempt to index nil")]
        [TestCase("Lua-CSharp: [string \"main.lua\"]:5: BAD_ARGUMENT: boom", "main.lua", 5, "BAD_ARGUMENT: boom")]
        [TestCase("[mod:tower script:Workspace.Tower line:9] INSTANCE_DESTROYED: x | fix: y", "Workspace.Tower", 9,
            "INSTANCE_DESTROYED: x | fix: y")]
        [TestCase("ReplicatedStorage.A:3: attempt to index nil", "ReplicatedStorage.A", 3, "attempt to index nil")]
        [TestCase("sandbox: EXCEEDED_HARD_LIMIT_STEPS (100) at line 7", "", 7,
            "sandbox: EXCEEDED_HARD_LIMIT_STEPS (100) at line 7")]
        [TestCase("plain failure", "", 0, "plain failure")]
        public void ErrorSite_ReadsScriptLineAndMessage(string message, string script, int line, string body)
        {
            LuaModErrorSite site = LuaModErrorSite.Parse(message);

            Assert.AreEqual(script, site.Script);
            Assert.AreEqual(line, site.Line);
            Assert.AreEqual(body, site.Message);
        }

        [Test]
        public void ErrorSite_KeyGroupsRepeatsOfOneLine_AndTheMessageWithoutALine()
        {
            Assert.AreEqual(LuaModErrorSite.Parse("[string \"sandbox_chunk\"]:12: a").Key,
                LuaModErrorSite.Parse("[string \"sandbox_chunk\"]:12: b").Key);
            Assert.AreNotEqual(LuaModErrorSite.Parse("[string \"sandbox_chunk\"]:12: a").Key,
                LuaModErrorSite.Parse("[string \"sandbox_chunk\"]:13: a").Key);
            Assert.AreEqual("plain failure", LuaModErrorSite.Parse("plain failure").Key);
        }

        private static LuaModRepairErrorOutcome Open(LuaModRuntimeRepairPolicy policy, string modId, double at)
        {
            policy.RecordError(modId, Boom, 1, at);
            policy.RecordError(modId, Boom, 2, at + 0.01d);
            return policy.RecordError(modId, Boom, 3, at + 0.02d);
        }
    }
}
