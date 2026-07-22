using CoreAI.Ai;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    public sealed class LuaModAutoRepairPolicyEditModeTests
    {
        private static LuaModAutoRepairPolicy NewPolicy(int minErrors = 3, int maxAttempts = 2, double cooldown = 20d)
        {
            return new LuaModAutoRepairPolicy(minErrors, maxAttempts, cooldown);
        }

        [Test]
        public void Evaluate_BelowThreshold_Skips()
        {
            LuaModAutoRepairPolicy policy = NewPolicy(3);

            Assert.AreEqual(LuaModAutoRepairDecision.Skip, policy.Evaluate("m", 1, 0d, out int attempt));
            Assert.AreEqual(0, attempt);
            Assert.AreEqual(LuaModAutoRepairDecision.Skip, policy.Evaluate("m", 2, 0d, out _));
        }

        [Test]
        public void Evaluate_AtThreshold_RepairsAndCountsAttempt()
        {
            LuaModAutoRepairPolicy policy = NewPolicy(3);

            Assert.AreEqual(LuaModAutoRepairDecision.Repair, policy.Evaluate("m", 3, 0d, out int attempt));
            Assert.AreEqual(1, attempt);
            Assert.IsTrue(policy.IsRepairing("m"));
        }

        [Test]
        public void Evaluate_WhileInFlight_Skips()
        {
            LuaModAutoRepairPolicy policy = NewPolicy(3);

            Assert.AreEqual(LuaModAutoRepairDecision.Repair, policy.Evaluate("m", 3, 0d, out _));
            // A second error arriving before the in-flight repair finishes must not launch another.
            Assert.AreEqual(LuaModAutoRepairDecision.Skip, policy.Evaluate("m", 4, 1d, out _));
        }

        [Test]
        public void Evaluate_SecondAttempt_RequiresCooldownThenRepairs()
        {
            LuaModAutoRepairPolicy policy = NewPolicy(3, 2, 20d);

            Assert.AreEqual(LuaModAutoRepairDecision.Repair, policy.Evaluate("m", 3, 0d, out _));
            policy.OnRepairCompleted("m");

            // Still within cooldown -> skip.
            Assert.AreEqual(LuaModAutoRepairDecision.Skip, policy.Evaluate("m", 4, 10d, out _));
            // After cooldown -> second attempt.
            Assert.AreEqual(LuaModAutoRepairDecision.Repair, policy.Evaluate("m", 5, 21d, out int attempt));
            Assert.AreEqual(2, attempt);
        }

        [Test]
        public void Evaluate_ExhaustedBudget_ReportsGaveUpOnceThenSkips()
        {
            LuaModAutoRepairPolicy policy = NewPolicy(3, 1, 0d);

            Assert.AreEqual(LuaModAutoRepairDecision.Repair, policy.Evaluate("m", 3, 0d, out _));
            policy.OnRepairCompleted("m");

            // Budget is spent: first follow-up error reports GaveUp exactly once, then stays quiet.
            Assert.AreEqual(LuaModAutoRepairDecision.GaveUp, policy.Evaluate("m", 4, 5d, out _));
            Assert.AreEqual(LuaModAutoRepairDecision.Skip, policy.Evaluate("m", 5, 6d, out _));
        }

        [Test]
        public void OnModReloaded_WhileInFlight_KeepsAttemptBudget()
        {
            LuaModAutoRepairPolicy policy = NewPolicy(3, 2, 0d);

            Assert.AreEqual(LuaModAutoRepairDecision.Repair, policy.Evaluate("m", 3, 0d, out _));
            // The repair's own manage_mods reload fires while still in flight: clears in-flight, keeps count.
            policy.OnModReloaded("m");
            Assert.IsFalse(policy.IsRepairing("m"));
            Assert.AreEqual(1, policy.AttemptsFor("m"));
        }

        [Test]
        public void OnModReloaded_ExternalReload_ResetsAttemptBudget()
        {
            LuaModAutoRepairPolicy policy = NewPolicy(3, 1, 0d);

            Assert.AreEqual(LuaModAutoRepairDecision.Repair, policy.Evaluate("m", 3, 0d, out _));
            policy.OnRepairCompleted("m");

            // A manual reload (not in flight) is a clean slate: auto-repair is armed again.
            policy.OnModReloaded("m");
            Assert.AreEqual(0, policy.AttemptsFor("m"));
            Assert.AreEqual(LuaModAutoRepairDecision.Repair, policy.Evaluate("m", 3, 100d, out int attempt));
            Assert.AreEqual(1, attempt);
        }

        [Test]
        public void Evaluate_IndependentMods_TrackedSeparately()
        {
            LuaModAutoRepairPolicy policy = NewPolicy(3);

            Assert.AreEqual(LuaModAutoRepairDecision.Repair, policy.Evaluate("a", 3, 0d, out _));
            Assert.AreEqual(LuaModAutoRepairDecision.Repair, policy.Evaluate("b", 3, 0d, out _));
            Assert.IsTrue(policy.IsRepairing("a"));
            Assert.IsTrue(policy.IsRepairing("b"));
        }

        [Test]
        public void Evaluate_EmptyModId_Skips()
        {
            LuaModAutoRepairPolicy policy = NewPolicy(1);

            Assert.AreEqual(LuaModAutoRepairDecision.Skip, policy.Evaluate("", 5, 0d, out _));
            Assert.AreEqual(LuaModAutoRepairDecision.Skip, policy.Evaluate(null, 5, 0d, out _));
        }

        [Test]
        public void Evaluate_ZeroMaxAttempts_NeverRepairs()
        {
            LuaModAutoRepairPolicy policy = NewPolicy(1, 0);

            // With no budget at all the very first qualifying error reports GaveUp, then goes quiet.
            Assert.AreEqual(LuaModAutoRepairDecision.GaveUp, policy.Evaluate("m", 1, 0d, out _));
            Assert.AreEqual(LuaModAutoRepairDecision.Skip, policy.Evaluate("m", 2, 1d, out _));
        }

        [Test]
        public void CreateProgrammerRepairTask_CarriesModErrorSourceAndVersionContext()
        {
            AiTaskRequest task = LuaModAutoRepairTaskFactory.CreateProgrammerRepairTask(
                "broken_mod",
                "hooks_every(1, function() error('boom') end)",
                "attempt to index nil",
                2,
                "demo.live_mechanics.mods_chat.mod.",
                "demo_auto_repair");

            Assert.AreEqual(BuiltInAgentRoleIds.Programmer, task.RoleId);
            Assert.AreEqual(2, task.LuaRepairGeneration);
            Assert.AreEqual("hooks_every(1, function() error('boom') end)", task.LuaRepairPreviousCode);
            Assert.AreEqual("attempt to index nil", task.LuaRepairErrorMessage);
            Assert.AreEqual("demo_auto_repair", task.SourceTag);
            Assert.AreEqual("demo.live_mechanics.mods_chat.mod.broken_mod", task.LuaScriptVersionKey);
            StringAssert.Contains("broken_mod", task.Hint);
            StringAssert.Contains("fix_this_lua", task.Hint);
            StringAssert.Contains("lua_error", task.Hint);
            StringAssert.Contains("manage_mods reload", task.Hint);
            // WHY: The error policy is quarantine, not unload — the prompt must teach that the mod is
            // still loaded and that reload clears the quarantine, not a reload-vs-load workaround.
            StringAssert.Contains("QUARANTINE", task.Hint);
            StringAssert.Contains("clears the", task.Hint);
            StringAssert.DoesNotContain("manage_mods load", task.Hint);
            StringAssert.Contains("do not create a new mod id", task.Hint);
        }
    }
}
