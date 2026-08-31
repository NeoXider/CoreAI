using System.Collections.Generic;
using CoreAI.Ai;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode
{
    /// <summary>Regression tests for actor-specific orchestration metrics and bounded retention.</summary>
    public sealed class InMemoryAiOrchestrationMetricsEditModeTests
    {
        [Test]
        public void TwoActorsWithSameRole_HaveDistinctRows_AndAggregateIntoRole()
        {
            InMemoryAiOrchestrationMetrics metrics = new(4, 4);

            metrics.RecordLlmCompletion("actor-a", "builder", "trace-a", true, 10d);
            metrics.RecordLlmCompletion("actor-b", "builder", "trace-b", false, 30d);

            Dictionary<string, InMemoryAiOrchestrationMetrics.ActorMetrics> actors =
                metrics.GetAllActorMetrics();
            InMemoryAiOrchestrationMetrics.RoleMetrics role = metrics.GetRoleMetrics("builder");

            Assert.AreEqual(2, actors.Count);
            Assert.AreEqual(1, actors["actor-a"].Completions);
            Assert.AreEqual(1, actors["actor-b"].Completions);
            Assert.AreEqual("builder", actors["actor-a"].RoleId);
            Assert.AreEqual(2, role.Completions);
            Assert.AreEqual(1, role.Successes);
            Assert.AreEqual(1, role.Failures);
            Assert.AreEqual(20d, role.AverageLatencyMs);
        }

        [Test]
        public void Denial_RetainsRecoverableReasonForActorAndRole()
        {
            InMemoryAiOrchestrationMetrics metrics = new(4, 4);

            metrics.RecordDenial("actor-a", "builder", "trace-a", "quota exhausted");

            InMemoryAiOrchestrationMetrics.ActorMetrics actor = metrics.GetActorMetrics("actor-a");
            InMemoryAiOrchestrationMetrics.RoleMetrics role = metrics.GetRoleMetrics("builder");
            Assert.AreEqual(1, metrics.TotalDenials);
            Assert.AreEqual(1, actor.Denials);
            Assert.AreEqual("quota exhausted", actor.LastDenialReason);
            Assert.AreEqual("trace-a", actor.RecentDenials[0].TraceId);
            Assert.AreEqual("quota exhausted", actor.RecentDenials[0].Reason);
            Assert.AreEqual(1, role.Denials);
            Assert.AreEqual("quota exhausted", role.LastDenialReason);
        }

        [Test]
        public void Retention_DoesNotExceedConfiguredActorOrPerActorDenialLimits()
        {
            InMemoryAiOrchestrationMetrics metrics = new(2, 2);
            metrics.RecordDenial("actor-a", "builder", "trace-1", "reason-1");
            metrics.RecordDenial("actor-a", "builder", "trace-2", "reason-2");
            metrics.RecordDenial("actor-a", "builder", "trace-3", "reason-3");

            InMemoryAiOrchestrationMetrics.ActorMetrics actor = metrics.GetActorMetrics("actor-a");
            Assert.AreEqual(2, actor.RecentDenials.Count);
            Assert.AreEqual("reason-2", actor.RecentDenials[0].Reason);
            Assert.AreEqual("reason-3", actor.RecentDenials[1].Reason);

            metrics.RecordLlmCompletion("actor-b", "builder", "trace-b", true, 1d);
            metrics.RecordLlmCompletion("actor-c", "builder", "trace-c", true, 1d);

            Dictionary<string, InMemoryAiOrchestrationMetrics.ActorMetrics> actors =
                metrics.GetAllActorMetrics();
            Assert.AreEqual(2, actors.Count);
            Assert.IsFalse(actors.ContainsKey("actor-a"));
            Assert.IsTrue(actors.ContainsKey("actor-b"));
            Assert.IsTrue(actors.ContainsKey("actor-c"));
        }
    }
}
