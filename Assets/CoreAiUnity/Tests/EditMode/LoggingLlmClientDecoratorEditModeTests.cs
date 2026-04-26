using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    public sealed class LoggingLlmClientDecoratorEditModeTests
    {
        private sealed class SpyLogger : IGameLogger
        {
            public readonly List<string> Lines = new();

            public void LogDebug(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
                Lines.Add($"D:{feature}:{message}");
            }

            public void LogInfo(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
                Lines.Add($"I:{feature}:{message}");
            }

            public void LogWarning(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
                Lines.Add($"W:{feature}:{message}");
            }

            public void LogError(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
                Lines.Add($"E:{feature}:{message}");
            }
        }

        private sealed class AllOnSettings : IGameLogSettings
        {
            public bool ShouldLog(GameLogFeature feature, GameLogLevel level)
            {
                return true;
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
        public async Task Timeout_LogsWarningAndReturnsError()
        {
            SpyLogger spy = new();
            MockLlm inner = new(5000, new LlmCompletionResult { Ok = true, Content = "late" });
            LoggingLlmClientDecorator dec = new(inner, spy, 0.05f);
            using CancellationTokenSource cts = new();
            LlmCompletionRequest req = new()
            {
                AgentRoleId = BuiltInAgentRoleIds.Programmer,
                TraceId = "t-out",
                UserPayload = "x"
            };
            LlmCompletionResult r = await dec.CompleteAsync(req, cts.Token);
            Assert.IsFalse(r.Ok);
            Assert.IsTrue(r.Error?.Contains("timeout") == true || r.Error?.Contains("Timeout") == true);
            string joined = string.Join("\n", spy.Lines);
            StringAssert.Contains("t-out", joined);
            StringAssert.Contains("LLM ⏱", joined);
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