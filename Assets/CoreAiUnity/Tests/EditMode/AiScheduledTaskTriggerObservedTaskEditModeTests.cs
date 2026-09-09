using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;
using CoreAI.Presentation;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// A scheduled task the timer fires and forgets used to vanish when it failed: nobody awaited the
    /// task, Unity does not report unobserved task exceptions, so a timer agent that failed on every tick
    /// looked like a timer that did nothing. Every outcome must now be observed and a failure reported.
    /// </summary>
    [Category("Scheduling")]
    public sealed class AiScheduledTaskTriggerObservedTaskEditModeTests
    {
        [Test]
        public async Task FaultedTask_IsReportedAsWarning_AndDoesNotEscape()
        {
            CapturingLogger log = new();
            FaultingOrchestrator orchestrator = new(new InvalidOperationException("provider exploded"));

            await AiScheduledTaskTrigger.RunObservedAsync(orchestrator,
                new AiTaskRequest { RoleId = "Creator", SourceTag = "scheduled_timer:test" }, log);

            Assert.AreEqual(1, log.Warnings.Count, "A failed scheduled task must be reported exactly once.");
            StringAssert.Contains("provider exploded", log.Warnings[0]);
            StringAssert.Contains("scheduled_timer:test", log.Warnings[0]);
            Assert.AreEqual(0, log.Errors.Count);
        }

        [Test]
        public async Task SynchronousThrow_IsObservedToo()
        {
            CapturingLogger log = new();
            ThrowingOrchestrator orchestrator = new();

            await AiScheduledTaskTrigger.RunObservedAsync(orchestrator,
                new AiTaskRequest { RoleId = "Creator", SourceTag = "scheduled_timer" }, log);

            Assert.AreEqual(1, log.Warnings.Count);
            StringAssert.Contains("sync boom", log.Warnings[0]);
        }

        [Test]
        public async Task CancelledTask_IsNotAFailure()
        {
            CapturingLogger log = new();
            FaultingOrchestrator orchestrator = new(new OperationCanceledException());

            await AiScheduledTaskTrigger.RunObservedAsync(orchestrator,
                new AiTaskRequest { RoleId = "Creator", SourceTag = "scheduled_timer" }, log);

            Assert.AreEqual(0, log.Warnings.Count, "Cancellation is the normal end of a superseded task.");
            Assert.AreEqual(1, log.Debugs.Count);
        }

        [Test]
        public async Task SuccessfulTask_LogsNothing()
        {
            CapturingLogger log = new();
            SucceedingOrchestrator orchestrator = new();

            await AiScheduledTaskTrigger.RunObservedAsync(orchestrator,
                new AiTaskRequest { RoleId = "Creator", SourceTag = "scheduled_timer" }, log);

            Assert.AreEqual(0, log.Warnings.Count);
            Assert.AreEqual(0, log.Errors.Count);
            Assert.AreEqual(0, log.Debugs.Count);
        }

        private sealed class CapturingLogger : IGameLogger
        {
            public List<string> Debugs { get; } = new();
            public List<string> Warnings { get; } = new();
            public List<string> Errors { get; } = new();

            public void LogDebug(GameLogFeature feature, string message, UnityEngine.Object context = null) =>
                Debugs.Add(message);

            public void LogInfo(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogWarning(GameLogFeature feature, string message, UnityEngine.Object context = null) =>
                Warnings.Add(message);

            public void LogError(GameLogFeature feature, string message, UnityEngine.Object context = null) =>
                Errors.Add(message);
        }

        private abstract class OrchestratorBase : IAiOrchestrationService
        {
            public abstract Task<string> RunTaskAsync(AiTaskRequest request, CancellationToken ct = default);

            public async IAsyncEnumerable<LlmStreamChunk> RunStreamingAsync(
                AiTaskRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken ct = default)
            {
                yield return new LlmStreamChunk { Text = await RunTaskAsync(request, ct), IsDone = true };
            }

            public void CancelTasks(string scopeId)
            {
            }
        }

        private sealed class FaultingOrchestrator : OrchestratorBase
        {
            private readonly Exception _exception;

            public FaultingOrchestrator(Exception exception)
            {
                _exception = exception;
            }

            public override Task<string> RunTaskAsync(AiTaskRequest request, CancellationToken ct = default) =>
                Task.FromException<string>(_exception);
        }

        private sealed class ThrowingOrchestrator : OrchestratorBase
        {
            public override Task<string> RunTaskAsync(AiTaskRequest request, CancellationToken ct = default) =>
                throw new InvalidOperationException("sync boom");
        }

        private sealed class SucceedingOrchestrator : OrchestratorBase
        {
            public override Task<string> RunTaskAsync(AiTaskRequest request, CancellationToken ct = default) =>
                Task.FromResult("done");
        }
    }
}
