using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using NUnit.Framework;
using CoreAI;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode coverage for the static <see cref="CoreAi"/> facade when no
    /// <c>CoreAILifetimeScope</c> is present in the scene.
    /// </summary>
    public sealed class CoreAiFacadeEditModeTests
    {
        [SetUp]
        public void ResetFacade()
        {
            CoreAi.Invalidate();
        }

        [Test]
        public void IsReady_WithoutLifetimeScope_ReturnsFalse()
        {
            Assert.IsFalse(CoreAi.IsReady, "Без CoreAILifetimeScope в сцене фасад не должен считаться готовым");
        }

        [Test]
        public void IsReady_ConcurrentWithResolverMutation_DoesNotThrowOrDeadlock()
        {
            // IsReady mutates shared resolver state via TryResolve; it must take SyncRoot like every
            // other resolver entry point so it cannot race a concurrent SetResolver and corrupt the
            // cached fields. A stub resolver keeps TryResolve off the Unity-main-thread scene lookup.
            CoreAi.SetResolver(() => new TestStubOrchestrator());

            Exception captured = null;
            Task[] workers = new Task[8];
            for (int i = 0; i < workers.Length; i++)
            {
                bool mutator = i % 2 == 0;
                workers[i] = Task.Run(() =>
                {
                    try
                    {
                        for (int n = 0; n < 500; n++)
                        {
                            if (mutator)
                            {
                                CoreAi.SetResolver(() => new TestStubOrchestrator());
                            }
                            else
                            {
                                _ = CoreAi.IsReady;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Interlocked.CompareExchange(ref captured, ex, null);
                    }
                });
            }

            Assert.IsTrue(Task.WaitAll(workers, TimeSpan.FromSeconds(10)),
                "Concurrent IsReady/SetResolver workers should finish without deadlocking.");
            Assert.IsNull(captured, $"IsReady must acquire SyncRoot so it is thread-safe: {captured}");
        }

        [Test]
        public void Invalidate_DoesNotThrow_WhenCalledMultipleTimes()
        {
            Assert.DoesNotThrow(() => CoreAi.Invalidate());
            Assert.DoesNotThrow(() => CoreAi.Invalidate());
            Assert.DoesNotThrow(() => CoreAi.Invalidate());
        }

        [Test]
        public void GetSettings_WithoutLifetimeScope_ReturnsNull()
        {
            ICoreAISettings settings = CoreAi.GetSettings();
            Assert.IsNull(settings,
                "Без scope GetSettings возвращает null (caller должен сам использовать CoreAISettings.Instance)");
        }

        [Test]
        public void GetChatService_WithoutLifetimeScope_ThrowsInvalidOperation()
        {
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => CoreAi.GetChatService());
            StringAssert.Contains("CoreAILifetimeScope", ex.Message,
                "Исключение должно подсказывать, где искать проблему");
        }

        [Test]
        public void TryGetChatService_WithoutLifetimeScope_ReturnsFalse()
        {
            Assert.IsFalse(CoreAi.TryGetChatService(out _),
                "Без scope TryGet не бросает исключение и возвращает false");
        }

        [Test]
        public void GetOrchestrator_WithoutLifetimeScope_ThrowsInvalidOperation()
        {
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => CoreAi.GetOrchestrator());
            StringAssert.Contains("IAiOrchestrationService", ex.Message,
                "Исключение должно объяснять, что не зарегистрирован оркестратор");
        }

        [Test]
        public void TryGetOrchestrator_WithoutLifetimeScope_ReturnsFalse()
        {
            Assert.IsFalse(CoreAi.TryGetOrchestrator(out _));
        }

        [Test]
        public void SetResolver_OverridesOrchestratorResolution_ForTesting()
        {
            TestStubOrchestrator resolverOrchestrator = new();
            CoreAi.SetResolver(() => resolverOrchestrator);

            IAiOrchestrationService resolved = CoreAi.GetOrchestrator();

            Assert.AreSame(resolverOrchestrator, resolved);
        }

        private sealed class TestStubOrchestrator : IAiOrchestrationService
        {
            public Task<string> RunTaskAsync(AiTaskRequest task, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(string.Empty);
            }

            public async IAsyncEnumerable<LlmStreamChunk> RunStreamingAsync(
                AiTaskRequest task,
                [EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                yield return new LlmStreamChunk { IsDone = true };
                await Task.CompletedTask;
            }

            public void CancelTasks(string cancellationScope)
            {
            }
        }
    }
}
