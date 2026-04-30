using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Logging;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    public sealed class LoggingLlmClientDecoratorEditModeTests
    {
        private sealed class SpyLogger : ILog
        {
            public readonly List<string> Lines = new();

            public void Debug(string message, string tag = null)
            {
                Lines.Add($"D:{tag}:{message}");
            }

            public void Info(string message, string tag = null)
            {
                Lines.Add($"I:{tag}:{message}");
            }

            public void Warn(string message, string tag = null)
            {
                Lines.Add($"W:{tag}:{message}");
            }

            public void Error(string message, string tag = null)
            {
                Lines.Add($"E:{tag}:{message}");
            }
        }

        private sealed class MockLlm : ILlmClient
        {
            private readonly int _delayMs;
            private readonly LlmCompletionResult _result;

            public MockLlm(int delayMs, LlmCompletionResult result)
            {
                _delayMs = delayMs;
                _result = result;
            }

            public async Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                if (_delayMs > 0)
                {
                    await Task.Delay(_delayMs, cancellationToken).ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                return _result;
            }
        }

        [Test]
        public async Task Success_LogsTraceIdAndRole()
        {
            SpyLogger spy = new();
            MockLlm inner = new(0, new LlmCompletionResult { Ok = true, Content = "ok" });
            LoggingLlmClientDecorator dec = new(inner, spy, 0f);
            LlmCompletionRequest req = new()
            {
                AgentRoleId = BuiltInAgentRoleIds.Creator,
                TraceId = "abc123",
                SystemPrompt = "sys",
                UserPayload = "user"
            };
            LlmCompletionResult r = await dec.CompleteAsync(req);
            Assert.IsTrue(r.Ok);
            string joined = string.Join("\n", spy.Lines);
            StringAssert.Contains("abc123", joined);
            StringAssert.Contains(BuiltInAgentRoleIds.Creator, joined);
            StringAssert.Contains("LLM ▶", joined);
            StringAssert.Contains("LLM ◀", joined);
            StringAssert.Contains("promptBudget", joined);
            StringAssert.Contains("estTok≈", joined);
            StringAssert.Contains("words≈", joined);
            StringAssert.Contains("systemSplit", joined);
            StringAssert.Contains("outWords≈", joined);
        }

        [Test]
        public void CallerCancellation_RethrowsOperationCancelled()
        {
            // v1.5.1: decorator no longer enforces its own CancelAfter timeout.
            // Instead, it re-throws OperationCanceledException when the caller's
            // token is cancelled (timeout is now enforced by CoreAiChatService
            // via UniTask CancelAfterSlim).
            //
            // We pre-cancel the CTS to avoid depending on System.Threading.Timer
            // (unreliable in Unity EditMode without a SynchronizationContext).
            SpyLogger spy = new();
            MockLlm inner = new(0, new LlmCompletionResult { Ok = true, Content = "late" });
            LoggingLlmClientDecorator dec = new(inner, spy, 0f);
            using CancellationTokenSource cts = new();
            cts.Cancel(); // pre-cancel — CompleteAsync should throw immediately
            LlmCompletionRequest req = new()
            {
                AgentRoleId = BuiltInAgentRoleIds.Programmer,
                TraceId = "t-out",
                UserPayload = "x"
            };
            Assert.CatchAsync<OperationCanceledException>(
                async () => await dec.CompleteAsync(req, cts.Token));
        }

        /// <summary>
        /// Мок-клиент со своей реализацией стриминга: эмитит N чанков подряд.
        /// Нужен чтобы проверить, что decorator/RoutingLlmClient не делают fallback
        /// на default-реализацию (которая собрала бы всё в 1 чанк через CompleteAsync).
        /// </summary>
        private sealed class StreamingMockLlm : ILlmClient
        {
            private readonly string[] _parts;

            public StreamingMockLlm(params string[] parts)
            {
                _parts = parts;
            }

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                // Если кто-то вызвал CompleteAsync вместо стриминга — тест это
                // увидит через Ok=true и Content = concat(parts), но это будет
                // единичный chunk (в fallback пути) — индикатор бага.
                return Task.FromResult(new LlmCompletionResult
                {
                    Ok = true,
                    Content = string.Concat(_parts)
                });
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
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
        public async Task Streaming_DelegatesRealChunks_NotSingleShotFallback()
        {
            // Если LoggingLlmClientDecorator не переопределял CompleteStreamingAsync,
            // дефолтная реализация интерфейса свернула бы всё в один chunk через
            // CompleteAsync — стриминг «не был бы виден» (как в issue 2).
            SpyLogger spy = new();
            StreamingMockLlm inner = new("Hel", "lo,", " wo", "rld!");
            LoggingLlmClientDecorator dec = new(inner, spy, 0f);

            List<LlmStreamChunk> chunks = new();
            await foreach (LlmStreamChunk chunk in dec.CompleteStreamingAsync(
                new LlmCompletionRequest { AgentRoleId = "Tester", TraceId = "s1", UserPayload = "hi" }))
            {
                chunks.Add(chunk);
            }

            // 4 текстовых + 1 финальный = 5 чанков (не 1 как было при fallback)
            Assert.AreEqual(5, chunks.Count, "Streaming должен прокидывать чанки по мере поступления");
            Assert.AreEqual("Hel", chunks[0].Text);
            Assert.AreEqual("lo,", chunks[1].Text);
            Assert.AreEqual(" wo", chunks[2].Text);
            Assert.AreEqual("rld!", chunks[3].Text);
            Assert.IsTrue(chunks[4].IsDone, "Последний чанк должен быть терминальным");
        }

        [Test]
        public async Task Streaming_LogsStartAndFinish()
        {
            SpyLogger spy = new();
            StreamingMockLlm inner = new("a", "b");
            LoggingLlmClientDecorator dec = new(inner, spy, 0f);

            List<LlmStreamChunk> chunks = new();
            await foreach (LlmStreamChunk chunk in dec.CompleteStreamingAsync(
                new LlmCompletionRequest { AgentRoleId = "Tester", TraceId = "s2", UserPayload = "hi" }))
            {
                chunks.Add(chunk);
            }

            string joined = string.Join("\n", spy.Lines);
            StringAssert.Contains("s2", joined, "Должен быть traceId");
            StringAssert.Contains("(stream)", joined, "Маркер стримингового вызова");
            StringAssert.Contains("LLM ▶", joined, "Лог старта");
            StringAssert.Contains("LLM ◀", joined, "Лог успешного завершения");
            StringAssert.Contains("chunks=2", joined, "Должно быть число текстовых чанков");
            StringAssert.Contains("promptBudget", joined);
        }

        [Test]
        public void PromptBudget_CountWordsAndEstimateTokensRough()
        {
            Assert.AreEqual(0, LoggingLlmClientDecorator.CountWords(null));
            Assert.AreEqual(0, LoggingLlmClientDecorator.CountWords("   "));
            Assert.AreEqual(1, LoggingLlmClientDecorator.CountWords("a"));
            Assert.AreEqual(2, LoggingLlmClientDecorator.CountWords("hello world"));
            Assert.AreEqual(1, LoggingLlmClientDecorator.EstimateTokensRough("abcd"));
            Assert.AreEqual(2, LoggingLlmClientDecorator.EstimateTokensRough("abcde"));
        }

        [Test]
        public void PromptBudget_FormatLine_SplitsSystemAndChat()
        {
            string line = LoggingLlmClientDecorator.FormatPromptBudgetLine("sys", "user one two");
            StringAssert.Contains("promptBudget", line);
            StringAssert.Contains("systemSplit", line);
            StringAssert.Contains("total=3", line);
            StringAssert.Contains("core=3", line);
            StringAssert.Contains("memory=0", line);
            StringAssert.Contains("toolsDef≈0(0 tools)", line);
            StringAssert.Contains("chat chars=12", line);
            StringAssert.Contains("estTok≈3", line);
            StringAssert.Contains("words≈3", line);
        }

        [Test]
        public void SplitSystemCoreAndMemory_UsesOrchestratorDelimiter()
        {
            string sys = "role prompt" + LoggingLlmClientDecorator.OrchestratorMemorySectionDelimiter + "mem line one";
            LoggingLlmClientDecorator.SplitSystemCoreAndMemory(sys, out string core, out string mem);
            Assert.AreEqual("role prompt", core);
            Assert.AreEqual("mem line one", mem);
        }

        private sealed class StubTool : ILlmTool
        {
            public string Name { get; set; }
            public string Description { get; set; }
            public string ParametersSchema { get; set; }
            public bool AllowDuplicates => false;
        }

        [Test]
        public void PromptBudget_ToolsDefNonZeroWhenToolsPresent()
        {
            IReadOnlyList<ILlmTool> tools = new[]
            {
                new StubTool { Name = "ping", Description = "pong", ParametersSchema = "{}" }
            };
            int d = LoggingLlmClientDecorator.EstimateToolsCatalogChars(tools);
            Assert.Greater(d, 50);
            string line = LoggingLlmClientDecorator.FormatPromptBudgetLine("x", "y", tools);
            StringAssert.Contains("toolsDef≈", line);
            StringAssert.Contains("(1 tools)", line);
        }
    }
}