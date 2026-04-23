using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Тесты стримингового пути оркестратора: default-fallback в интерфейсе
    /// <see cref="IAiOrchestrationService"/> и прозрачный проброс через
    /// <see cref="QueuedAiOrchestrator"/>.
    /// </summary>
    public sealed class AiOrchestratorStreamingEditModeTests
    {
        /// <summary>
        /// Стаб-оркестратор, который реализует ТОЛЬКО <see cref="IAiOrchestrationService.RunTaskAsync"/>
        /// и полагается на default-реализацию <c>RunStreamingAsync</c> из интерфейса.
        /// Полезен для проверки того, что fallback корректно эмитит 2 чанка (текст + done).
        /// </summary>
        private sealed class FallbackOnlyOrchestrator : IAiOrchestrationService
        {
            private readonly string _result;
            public FallbackOnlyOrchestrator(string result) { _result = result; }

            public Task<string> RunTaskAsync(AiTaskRequest task, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(_result);
            }
        }

        /// <summary>
        /// Оркестратор с явной реализацией стриминга: эмитит N delta-чанков.
        /// Проверяем что QueuedAiOrchestrator корректно пробрасывает их через свою очередь.
        /// </summary>
        private sealed class StreamingOrchestrator : IAiOrchestrationService
        {
            private readonly string[] _parts;
            public int StreamCalls { get; private set; }
            public int RunTaskCalls { get; private set; }

            public StreamingOrchestrator(params string[] parts) { _parts = parts; }

            public Task<string> RunTaskAsync(AiTaskRequest task, CancellationToken cancellationToken = default)
            {
                RunTaskCalls++;
                return Task.FromResult(string.Concat(_parts));
            }

            public async IAsyncEnumerable<LlmStreamChunk> RunStreamingAsync(
                AiTaskRequest task,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                StreamCalls++;
                foreach (string part in _parts)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return new LlmStreamChunk { Text = part };
                    await Task.Yield();
                }

                yield return new LlmStreamChunk { IsDone = true, Text = string.Empty };
            }
        }

        [Test]
        public async Task DefaultFallback_EmitsSingleTextChunkThenDone()
        {
            IAiOrchestrationService svc = new FallbackOnlyOrchestrator("full result");

            List<LlmStreamChunk> chunks = new();
            await foreach (LlmStreamChunk chunk in svc.RunStreamingAsync(new AiTaskRequest()))
            {
                chunks.Add(chunk);
            }

            Assert.AreEqual(2, chunks.Count, "default-fallback → 1 текст + 1 терминальный");
            Assert.AreEqual("full result", chunks[0].Text);
            Assert.IsFalse(chunks[0].IsDone);
            Assert.IsTrue(chunks[1].IsDone);
        }

        [Test]
        public async Task DefaultFallback_EmptyResult_EmitsErrorChunk()
        {
            IAiOrchestrationService svc = new FallbackOnlyOrchestrator(null);

            List<LlmStreamChunk> chunks = new();
            await foreach (LlmStreamChunk chunk in svc.RunStreamingAsync(new AiTaskRequest()))
            {
                chunks.Add(chunk);
            }

            Assert.AreEqual(1, chunks.Count);
            Assert.IsTrue(chunks[0].IsDone);
            Assert.AreEqual("empty result", chunks[0].Error);
        }

        [Test]
        public async Task QueuedAiOrchestrator_Streaming_DelegatesRealChunks()
        {
            // Если QueuedAiOrchestrator не переопределял бы RunStreamingAsync, default-fallback
            // склеил бы весь ответ в 1 чанк через RunTaskAsync. Этот тест фиксирует контракт.
            StreamingOrchestrator inner = new("Hel", "lo,", " wo", "rld!");
            QueuedAiOrchestrator queued = new(inner, new AiOrchestrationQueueOptions { MaxConcurrent = 2 });

            List<LlmStreamChunk> chunks = new();
            await foreach (LlmStreamChunk chunk in queued.RunStreamingAsync(
                new AiTaskRequest { RoleId = "Tester", Hint = "go" }))
            {
                chunks.Add(chunk);
            }

            Assert.AreEqual(1, inner.StreamCalls, "должен быть вызов стриминга, не sync-пути");
            Assert.AreEqual(0, inner.RunTaskCalls, "RunTaskAsync не должен вызываться");

            // 4 текстовых + 1 терминальный
            Assert.AreEqual(5, chunks.Count);
            Assert.AreEqual("Hel", chunks[0].Text);
            Assert.AreEqual("lo,", chunks[1].Text);
            Assert.AreEqual(" wo", chunks[2].Text);
            Assert.AreEqual("rld!", chunks[3].Text);
            Assert.IsTrue(chunks[4].IsDone);
        }

        [Test]
        public async Task QueuedAiOrchestrator_Streaming_RespectsMaxConcurrent()
        {
            // Two streams in parallel with MaxConcurrent=1: second stream must wait until first finishes.
            StreamingOrchestrator inner = new("a", "b", "c");
            QueuedAiOrchestrator queued = new(inner, new AiOrchestrationQueueOptions { MaxConcurrent = 1 });

            Task<List<LlmStreamChunk>> stream1 = CollectAsync(queued.RunStreamingAsync(
                new AiTaskRequest { RoleId = "T1", Hint = "first" }));
            Task<List<LlmStreamChunk>> stream2 = CollectAsync(queued.RunStreamingAsync(
                new AiTaskRequest { RoleId = "T2", Hint = "second" }));

            await Task.WhenAll(stream1, stream2);

            Assert.AreEqual(4, stream1.Result.Count, "stream1: 3 текстовых + 1 терминальный");
            Assert.AreEqual(4, stream2.Result.Count, "stream2: 3 текстовых + 1 терминальный");
            Assert.AreEqual(2, inner.StreamCalls, "оба стрима выполнены");
        }

        [Test]
        public async Task QueuedAiOrchestrator_Streaming_ExternalCancellation_EmitsCancelledTerminal()
        {
            // Пользовательская отмена (cancellationToken параметр) во время стрима
            // должна привести к терминальному чанку с Error="cancelled", а не к
            // необработанному OperationCanceledException в reader'е.
            SlowStreamingOrchestrator inner = new();
            QueuedAiOrchestrator queued = new(inner, new AiOrchestrationQueueOptions { MaxConcurrent = 2 });

            using CancellationTokenSource cts = new();
            List<LlmStreamChunk> collected = new();

            // Отменяем через 80мс — стрим уже начал выдавать чанки.
            _ = Task.Run(async () =>
            {
                await Task.Delay(80);
                cts.Cancel();
            });

            await foreach (LlmStreamChunk chunk in queued.RunStreamingAsync(
                new AiTaskRequest { RoleId = "T", Hint = "first" }, cts.Token))
            {
                collected.Add(chunk);
                if (chunk.IsDone)
                {
                    break;
                }
            }

            // Должен быть как минимум один терминальный чанк с Error="cancelled".
            bool gotCancelled = false;
            foreach (LlmStreamChunk chunk in collected)
            {
                if (chunk.IsDone && chunk.Error == "cancelled")
                {
                    gotCancelled = true;
                    break;
                }
            }

            Assert.IsTrue(gotCancelled,
                $"QueuedAiOrchestrator должен эмитить терминальный chunk с Error=\"cancelled\" при отмене. " +
                $"Получено чанков: {collected.Count}");
        }

        private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> source)
        {
            List<T> list = new();
            await foreach (T item in source)
            {
                list.Add(item);
            }

            return list;
        }

        /// <summary>
        /// Эмитит первый chunk немедленно, затем ждёт либо cancellation token, либо
        /// завершения Gate. Gate не разделяется — у каждой реализации свой экземпляр.
        /// </summary>
        private sealed class SlowStreamingOrchestrator : IAiOrchestrationService
        {
            public Task<string> RunTaskAsync(AiTaskRequest task, CancellationToken cancellationToken = default)
            {
                return Task.FromResult("sync");
            }

            public async IAsyncEnumerable<LlmStreamChunk> RunStreamingAsync(
                AiTaskRequest task,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                yield return new LlmStreamChunk { Text = "first-chunk" };

                // Имитируем долгую генерацию, но реагируем на отмену.
                try
                {
                    await Task.Delay(10000, cancellationToken);
                }
                catch (TaskCanceledException)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                yield return new LlmStreamChunk { IsDone = true, Text = string.Empty };
            }
        }
    }
}
