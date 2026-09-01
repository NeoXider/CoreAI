using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CoreAI.Diagnostics.G10;
using Xunit;

namespace CoreAI.Tools.G10.Tests
{
    /// <summary>Standalone regression coverage for the production-composed G10 harness.</summary>
    public sealed class G10MeasurementHarnessTests
    {
        [Fact]
        public void RealProviderValidation_LeavesMeasuredFactsUnset()
        {
            G10ProviderConfiguration provider = new G10ProviderConfiguration
            {
                ProviderMode = G10ProviderMode.RealProvider
            };

            IReadOnlyList<string> errors = provider.Validate();

            Assert.Null(provider.ContextCapTokens);
            Assert.Null(provider.OutputCapTokens);
            Assert.Null(provider.BackendConcurrency);
            Assert.Null(provider.OrchestratorConcurrency);
            Assert.Null(provider.RequestTimeoutSeconds);
            Assert.Contains("modelId is not measured/configured", errors);
            Assert.Contains("contextCapTokens is not measured/configured", errors);
            Assert.Contains("outputCapTokens is not measured/configured", errors);
            Assert.Contains("backendConcurrency is not measured/configured", errors);
        }

        [Fact]
        public void NotMeasuredNumericValue_HasNoNumericDefault()
        {
            G10MeasurementValue<int?> measurement =
                G10MeasurementValue<int?>.NotMeasured("no evidence");

            Assert.Equal("not_measured", measurement.Status);
            Assert.Null(measurement.Value);
        }

        [Fact]
        public void ManifestSchedules_AreDeterministicAndExact()
        {
            IReadOnlyList<double> staggered = G10MeasurementRunner.BuildManifestArrivalSchedule(
                G10ArrivalPattern.Staggered);
            IReadOnlyList<double> burst = G10MeasurementRunner.BuildManifestArrivalSchedule(
                G10ArrivalPattern.SynchronizedBurst);
            IReadOnlyList<int> firstDelays = G10MeasurementRunner.BuildDelayedDeadlineFrames();
            IReadOnlyList<int> secondDelays = G10MeasurementRunner.BuildDelayedDeadlineFrames();

            Assert.Equal(40, staggered.Count);
            Assert.Equal(40, burst.Count);
            Assert.Equal(20, burst.Count(value => Math.Abs(value) < 0.000001d));
            Assert.Equal(20, burst.Count(value => Math.Abs(value - 30d) < 0.000001d));
            Assert.Equal(firstDelays, secondDelays);
            for (int frame = 1; frame <= 10; frame++)
            {
                Assert.Equal(20, firstDelays.Count(value => value == frame));
            }
        }

        [Fact]
        public void FixedLuaCopies_AreIdentical()
        {
            string diagnosticsSource = File.ReadAllText(
                Path.Combine(AppContext.BaseDirectory, "bench_actor.lua"));
            string resourceSource = File.ReadAllText(
                Path.Combine(AppContext.BaseDirectory, "bench_actor.resource.lua"));

            Assert.Equal(diagnosticsSource, resourceSource);
        }

        [Fact]
        public void MemoryStoreTrace_RecordsExternalLuaSideEffects()
        {
            G10MemoryLuaModStore store = new G10MemoryLuaModStore();

            store.Set("subscriber", "event_probe", "measurement|1|g10-actor-00");

            G10ModStoreWrite write = Assert.Single(store.SnapshotWrites());
            Assert.Equal("subscriber", write.ModId);
            Assert.Equal("event_probe", write.Key);
            Assert.Equal("measurement|1|g10-actor-00", write.Value);
        }

        [Fact]
        public async Task ScriptedStub_ProductionCompositionCalibratesAndAuditsEventIsolation()
        {
            string script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "bench_actor.lua"));
            G10MeasurementConfiguration configuration = new G10MeasurementConfiguration
            {
                WarmupSeconds = 0.2d,
                MeasurementSeconds = 1d,
                FrameRate = 60,
                Provider = new G10ProviderConfiguration
                {
                    ProviderMode = G10ProviderMode.ScriptedStub,
                    OrchestratorConcurrency = 20,
                    RequestTimeoutSeconds = 10,
                    StubLatencyMilliseconds = 0,
                    Temperature = 0f
                }
            };

            G10MeasurementReport report = await G10MeasurementRunner.RunAsync(configuration, script);

            Assert.Equal(2, report.Patterns.Count);
            Assert.Equal("not_measured", report.Provider.ModelId.Status);
            Assert.Null(report.Provider.ContextCapTokens.Value);
            Assert.Null(report.Provider.OutputCapTokens.Value);
            Assert.Null(report.Provider.BackendConcurrency.Value);
            Assert.Null(report.Provider.ChatResponsesActuallyProducedByProvider.Value);
            foreach (G10PatternReport pattern in report.Patterns)
            {
                Assert.Equal(0, pattern.CrossActorCancellations);
                Assert.True(pattern.ServedFractionAtLeastNinetyFivePercent);
            }

            foreach (G10WorldWorkloadReport world in report.WorldRuns)
            {
                Assert.True(world.CompletedLuaOperations > 0);
                Assert.True(world.ThreadResumes > 0);
                Assert.True(world.EventsDelivered > 0);
                Assert.Equal(580, world.CalibratedLuaBodyGuardedInstructions);
                Assert.True(world.MeanGuardedStepsWithinManifestFrameBudget);
                Assert.Equal(20, world.SubscriberInvocationsDuringMeasurement);
                Assert.Equal(0, world.NonSubscriberInvocationsDuringMeasurement);
                Assert.Equal(380, world.IndependentNonSubscriberChecksDuringMeasurement);
                Assert.True(world.EveryEmitInvokedExactlyOneSubscriber);
                Assert.Equal(
                    world.MeasurementCompletedLuaOperations,
                    world.MeasurementGuardedStepHistogram.Sum(pair => pair.Value));
                Assert.Equal(
                    world.MeasurementGuardedInstructionSteps,
                    world.MeasurementGuardedStepHistogram.Sum(pair => pair.Key * pair.Value));
            }
        }
    }
}
