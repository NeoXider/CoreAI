using CoreAI.Ai;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Pins <see cref="AiOrchestrationQueueOptions.ForActorCount"/>, the sizing that turned the
    /// capacity target from failing into passing.
    /// <para>
    /// WHY this is worth tests: the default of 64 pending is a HARD REFUSAL, and on the scale
    /// staircase it turned away 96 of 600 requests at 100 actors before any work was attempted — the
    /// capacity gate failed for a reason that had nothing to do with how fast anything ran. Sized for
    /// the actor count, the same run served every request. A regression here reappears as "CoreAI
    /// cannot hold 100 players", which is exactly the wrong conclusion to draw twice.
    /// </para>
    /// </summary>
    public sealed class AiOrchestrationQueueSizingEditModeTests
    {
        [Test]
        public void SmallHost_KeepsTheSmallDefaultFloor()
        {
            AiOrchestrationQueueOptions options = AiOrchestrationQueueOptions.ForActorCount(4);

            Assert.AreEqual(64, options.MaxPending,
                "a small host must not get a queue smaller than the historical default");
            Assert.AreEqual(2, options.MaxConcurrent);
        }

        [Test]
        public void HundredActors_GetAQueueThatHoldsASynchronizedBurst()
        {
            AiOrchestrationQueueOptions options = AiOrchestrationQueueOptions.ForActorCount(100);

            Assert.GreaterOrEqual(options.MaxPending, 100,
                "every actor may have one request in flight at the same instant; a burst must queue, " +
                "not be refused");
            Assert.Greater(options.MaxConcurrent, 2,
                "more actors need more lanes or the queue depth becomes latency");
        }

        [Test]
        public void TwoHundredActors_GetEnoughLanesToKeepTheTailDown()
        {
            // WHY 200 specifically: measured on the staircase, four lanes served all 200 actors but
            // stretched the burst p95 to 5,234 ms against a 5,000 ms budget; sixteen lanes brought the
            // same run to 1,346 ms.
            AiOrchestrationQueueOptions options = AiOrchestrationQueueOptions.ForActorCount(200);

            Assert.GreaterOrEqual(options.MaxPending, 200);
            Assert.GreaterOrEqual(options.MaxConcurrent, 13,
                "the measured configuration that passed at 200 actors used sixteen lanes");
        }

        [Test]
        public void Concurrency_IsCappedSoItCannotOutrunAnyRealBackend()
        {
            // WHY a ceiling: lanes only help while the provider can answer them in parallel. The G10
            // real-provider run measured a 17.4–38.5 s p95 on a single lane, so an unbounded number
            // here would just queue inside the backend instead of inside CoreAI, and hide the wait.
            AiOrchestrationQueueOptions options = AiOrchestrationQueueOptions.ForActorCount(100000);

            Assert.LessOrEqual(options.MaxConcurrent, 16);
            Assert.GreaterOrEqual(options.MaxPending, 100000,
                "the queue still has to hold the burst even when concurrency is capped");
        }

        [Test]
        public void NonPositiveActorCount_IsTreatedAsOne()
        {
            foreach (int actors in new[] { 0, -1, int.MinValue })
            {
                AiOrchestrationQueueOptions options = AiOrchestrationQueueOptions.ForActorCount(actors);
                Assert.AreEqual(64, options.MaxPending, "actors=" + actors);
                Assert.AreEqual(2, options.MaxConcurrent, "actors=" + actors);
            }
        }

        [Test]
        public void PlainConstruction_IsUnchanged()
        {
            // The sizing helper is opt-in; an existing host that constructs the options directly must
            // see exactly what it saw before.
            AiOrchestrationQueueOptions options = new();

            Assert.AreEqual(64, options.MaxPending);
            Assert.AreEqual(2, options.MaxConcurrent);
        }
    }
}
