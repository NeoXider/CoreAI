#if !COREAI_NO_LLM
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using CoreAI.Logging;
using NUnit.Framework;
using MEAI = Microsoft.Extensions.AI;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Tests for production resilience features: tool result truncation, per-tool timeout,
    /// max tool-call roundtrips. These validate <see cref="ToolExecutionPolicy"/> and
    /// <see cref="SmartToolCallingChatClient"/> safety mechanisms.
    /// </summary>
    [TestFixture]
    public sealed class ResilienceFeaturesEditModeTests
    {
        // ==================== Helpers ====================

        private sealed class ResilienceSettings : ICoreAISettings
        {
            public int MaxLuaRepairRetries => 3;
            public bool EnableMeaiDebugLogging => false;
            public float LlmRequestTimeoutSeconds => 120;
            public int MaxLlmRequestRetries => 1;
            public bool EnableHttpDebugLogging => false;
            public bool LogTokenUsage => false;
            public bool LogLlmLatency => false;
            public bool LogLlmConnectionErrors => false;
            public int ContextWindowTokens => 8192;
            public string UniversalSystemPromptPrefix => "";
            public float Temperature => 0.1f;
            public int MaxToolCallRetries => 3;
            public bool LogToolCalls => true;
            public bool LogToolCallArguments => false;
            public bool LogToolCallResults => true;
            public bool LogMeaiToolCallingSteps => true;
            public bool AllowDuplicateToolCalls => true;
            public bool EnableStreaming => false;

            // Resilience settings with test-friendly overrides
            public int MaxToolResultCharsOverride { get; set; } = 8000;
            public int MaxToolResultChars => MaxToolResultCharsOverride;

            public int DefaultToolTimeoutMsOverride { get; set; } = 30000;
            public int DefaultToolTimeoutMs => DefaultToolTimeoutMsOverride;

            public int MaxResponseCharsOverride { get; set; }
            public int MaxResponseChars => MaxResponseCharsOverride;

            public int MaxToolCallRoundtripsOverride { get; set; } = 10;
            public int MaxToolCallRoundtrips => MaxToolCallRoundtripsOverride;

            public int MaxToolCallHistoryMessagesOverride { get; set; } = 20;
            public int MaxToolCallHistoryMessages => MaxToolCallHistoryMessagesOverride;
        }

        private static MEAI.FunctionCallContent MakeToolCall(string name)
        {
            return new MEAI.FunctionCallContent($"call_{name}_{Guid.NewGuid():N}", name);
        }

        private static MEAI.ChatOptions MakeChatOptions(params (string name, Delegate func)[] tools)
        {
            MEAI.ChatOptions opts = new() { Tools = new List<MEAI.AITool>() };
            foreach ((string name, Delegate func) in tools)
            {
                opts.Tools.Add(MEAI.AIFunctionFactory.Create(func,
                    new MEAI.AIFunctionFactoryOptions { Name = name, Description = $"Tool {name}" }));
            }

            return opts;
        }

        // ==================== Tool Result Truncation ====================

        [Test]
        public async Task ToolResultTruncation_LargeResult_IsSoftTruncated()
        {
            ResilienceSettings settings = new() { MaxToolResultCharsOverride = 100 };
            string bigResult = new('A', 500);
            ToolExecutionPolicy policy = new(NullLog.Instance, settings,
                new List<ILlmTool>(), true, "test", 3);

            Func<string> func = () => bigResult;
            MEAI.ChatOptions opts = MakeChatOptions(("big_tool", func));

            ToolExecutionPolicy.ToolCallResult result =
                await policy.ExecuteSingleAsync(MakeToolCall("big_tool"), opts, CancellationToken.None);

            Assert.IsTrue(result.Succeeded);
            string text = result.Result.Result?.ToString() ?? "";
            Assert.That(text, Does.Contain("truncated"), "Should have truncation notice");
            Assert.That(text, Does.Contain("500"), "Should mention original length");
            Assert.That(text.Length, Is.LessThan(300), "Should be much smaller than original");
        }

        [Test]
        public async Task ToolResultTruncation_SmallResult_Untouched()
        {
            ResilienceSettings settings = new() { MaxToolResultCharsOverride = 8000 };
            string smallResult = "OK, crafted Iron Sword.";
            ToolExecutionPolicy policy = new(NullLog.Instance, settings,
                new List<ILlmTool>(), true, "test", 3);

            Func<string> func = () => smallResult;
            MEAI.ChatOptions opts = MakeChatOptions(("small_tool", func));

            ToolExecutionPolicy.ToolCallResult result =
                await policy.ExecuteSingleAsync(MakeToolCall("small_tool"), opts, CancellationToken.None);

            Assert.IsTrue(result.Succeeded);
            Assert.AreEqual(smallResult, result.Result.Result?.ToString(),
                "Small result should not be truncated");
        }

        [Test]
        public async Task ToolResultTruncation_DisabledWhenZero()
        {
            ResilienceSettings settings = new() { MaxToolResultCharsOverride = 0 };
            string bigResult = new('X', 50_000);
            ToolExecutionPolicy policy = new(NullLog.Instance, settings,
                new List<ILlmTool>(), true, "test", 3);

            Func<string> func = () => bigResult;
            MEAI.ChatOptions opts = MakeChatOptions(("huge_tool", func));

            ToolExecutionPolicy.ToolCallResult result =
                await policy.ExecuteSingleAsync(MakeToolCall("huge_tool"), opts, CancellationToken.None);

            Assert.IsTrue(result.Succeeded);
            Assert.AreEqual(bigResult, result.Result.Result?.ToString(),
                "Zero maxToolResultChars should disable truncation");
        }

        // ==================== Per-Tool Timeout ====================

        [Test]
        public async Task ToolTimeout_SlowTool_ReturnsTimeoutError()
        {
            ResilienceSettings settings = new() { DefaultToolTimeoutMsOverride = 200 }; // 200ms
            ToolExecutionPolicy policy = new(NullLog.Instance, settings,
                new List<ILlmTool>(), true, "test", 3);

            Func<CancellationToken, Task<string>> func = async ct =>
            {
                await Task.Delay(10_000, ct); // 10 seconds — should be cancelled
                return "done";
            };
            MEAI.ChatOptions opts = MakeChatOptions(("slow_tool", func));

            ToolExecutionPolicy.ToolCallResult result =
                await policy.ExecuteSingleAsync(MakeToolCall("slow_tool"), opts, CancellationToken.None);

            Assert.IsFalse(result.Succeeded, "Slow tool should fail");
            string text = result.Result.Result?.ToString() ?? "";
            Assert.That(text, Does.Contain("timed out"), "Should mention timeout");
        }

        [Test]
        public async Task ToolTimeout_FastTool_Succeeds()
        {
            ResilienceSettings settings = new() { DefaultToolTimeoutMsOverride = 5000 }; // 5s
            ToolExecutionPolicy policy = new(NullLog.Instance, settings,
                new List<ILlmTool>(), true, "test", 3);

            Func<string> func = () => "fast-result";
            MEAI.ChatOptions opts = MakeChatOptions(("fast_tool", func));

            ToolExecutionPolicy.ToolCallResult result =
                await policy.ExecuteSingleAsync(MakeToolCall("fast_tool"), opts, CancellationToken.None);

            Assert.IsTrue(result.Succeeded);
            Assert.AreEqual("fast-result", result.Result.Result?.ToString());
        }

        [Test]
        public async Task ToolTimeout_DisabledWhenZero_NoTimeout()
        {
            ResilienceSettings settings = new() { DefaultToolTimeoutMsOverride = 0 };
            ToolExecutionPolicy policy = new(NullLog.Instance, settings,
                new List<ILlmTool>(), true, "test", 3);

            Func<string> func = () => "no-timeout-ok";
            MEAI.ChatOptions opts = MakeChatOptions(("tool", func));

            ToolExecutionPolicy.ToolCallResult result =
                await policy.ExecuteSingleAsync(MakeToolCall("tool"), opts, CancellationToken.None);

            Assert.IsTrue(result.Succeeded);
        }

        // ==================== CoreAISettings Static Proxy ====================

        [Test]
        public void CoreAISettings_Defaults_MatchInterfaceDefaults()
        {
            // Store original and reset
            CoreAISettings.ResetOverrides();
            ICoreAISettings original = CoreAISettings.Instance;
            CoreAISettings.Instance = null;

            try
            {
                Assert.AreEqual(8000, CoreAISettings.MaxToolResultChars,
                    "Default MaxToolResultChars should be 8000");
                Assert.AreEqual(30000, CoreAISettings.DefaultToolTimeoutMs,
                    "Default DefaultToolTimeoutMs should be 30000");
                Assert.AreEqual(0, CoreAISettings.MaxResponseChars,
                    "Default MaxResponseChars should be 0 (disabled)");
                Assert.AreEqual(10, CoreAISettings.MaxToolCallRoundtrips,
                    "Default MaxToolCallRoundtrips should be 10");
            }
            finally
            {
                CoreAISettings.Instance = original;
            }
        }

        [Test]
        public void CoreAISettings_OverridesWork()
        {
            CoreAISettings.ResetOverrides();
            ICoreAISettings original = CoreAISettings.Instance;
            CoreAISettings.Instance = null;

            try
            {
                CoreAISettings.MaxToolResultChars = 500;
                Assert.AreEqual(500, CoreAISettings.MaxToolResultChars);

                CoreAISettings.DefaultToolTimeoutMs = 1000;
                Assert.AreEqual(1000, CoreAISettings.DefaultToolTimeoutMs);

                CoreAISettings.MaxResponseChars = 2000;
                Assert.AreEqual(2000, CoreAISettings.MaxResponseChars);

                CoreAISettings.MaxToolCallRoundtrips = 5;
                Assert.AreEqual(5, CoreAISettings.MaxToolCallRoundtrips);

                CoreAISettings.ResetOverrides();
                Assert.AreEqual(8000, CoreAISettings.MaxToolResultChars, "Should reset to default");
            }
            finally
            {
                CoreAISettings.Instance = original;
                CoreAISettings.ResetOverrides();
            }
        }
        // ==================== Tool Call History Truncation (v2.2.0) ====================

        [Test]
        public void CoreAISettings_MaxToolCallHistoryMessages_DefaultIs20()
        {
            CoreAISettings.ResetOverrides();
            ICoreAISettings original = CoreAISettings.Instance;
            CoreAISettings.Instance = null;

            try
            {
                Assert.AreEqual(20, CoreAISettings.MaxToolCallHistoryMessages,
                    "Default MaxToolCallHistoryMessages should be 20");
            }
            finally
            {
                CoreAISettings.Instance = original;
            }
        }

        [Test]
        public void CoreAISettings_MaxToolCallHistoryMessages_OverrideWorks()
        {
            CoreAISettings.ResetOverrides();
            ICoreAISettings original = CoreAISettings.Instance;
            CoreAISettings.Instance = null;

            try
            {
                CoreAISettings.MaxToolCallHistoryMessages = 6;
                Assert.AreEqual(6, CoreAISettings.MaxToolCallHistoryMessages);

                CoreAISettings.ResetOverrides();
                Assert.AreEqual(20, CoreAISettings.MaxToolCallHistoryMessages, "Should reset to default");
            }
            finally
            {
                CoreAISettings.Instance = original;
                CoreAISettings.ResetOverrides();
            }
        }

        // ==================== Rate Limiter Metrics (v2.2.0) ====================

        [Test]
        public void RateLimiterMetrics_Struct_HoldsValues()
        {
            RateLimiterMetrics m = new(10, 60, 3, 7);
            Assert.AreEqual(10, m.MaxRequestsPerWindow);
            Assert.AreEqual(60, m.WindowSeconds);
            Assert.AreEqual(3, m.AcceptedInWindow);
            Assert.AreEqual(7, m.TotalRejected);
        }

        [Test]
        public async Task RateLimiterMetrics_InGameLlmChatService_TracksRejections()
        {
            // Create a service with max 2 requests per 60s window
            StubLlmClient stubLlm = new("pong");
            StubSystemPromptProvider stubPrompts = new();
            InGameLlmChatService service = new(stubLlm, stubPrompts, 24,
                2, 60);

            // First two should succeed (rate limiter accepts)
            LlmCompletionResult r1 = await service.SendPlayerMessageAsync("msg1");
            LlmCompletionResult r2 = await service.SendPlayerMessageAsync("msg2");

            // Third should be rate-limited
            LlmCompletionResult r3 = await service.SendPlayerMessageAsync("msg3");
            Assert.IsFalse(r3.Ok, "Third request should be rate-limited");
            Assert.That(r3.Error, Does.Contain("rate_limited"));

            // Check metrics
            RateLimiterMetrics metrics = service.GetRateLimiterMetrics();
            Assert.AreEqual(2, metrics.MaxRequestsPerWindow);
            Assert.AreEqual(60, metrics.WindowSeconds);
            Assert.AreEqual(2, metrics.AcceptedInWindow);
            Assert.AreEqual(1, metrics.TotalRejected, "Should have 1 rejection");
        }

        // ==================== Helpers for rate limiter tests ====================

        private sealed class StubLlmClient : ILlmClient
        {
            private readonly string _response;

            public StubLlmClient(string response)
            {
                _response = response;
            }

            public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request, CancellationToken ct = default)
            {
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = _response });
            }

            public IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(LlmCompletionRequest request,
                CancellationToken ct = default)
            {
                throw new NotImplementedException();
            }
        }

        private sealed class StubSystemPromptProvider : IAgentSystemPromptProvider
        {
            public bool TryGetSystemPrompt(string roleId, out string prompt)
            {
                prompt = "Test system prompt";
                return true;
            }
        }

        // ==================== FallbackLlmClientDecorator (v2.3.0) ====================

        [Test]
        public async Task Fallback_PrimarySucceeds_SecondaryNotCalled()
        {
            StubLlmClient primary = new("primary-ok");
            CountingLlmClient secondary = new("secondary-ok");
            FallbackLlmClientDecorator fallback = new(primary, secondary);

            LlmCompletionResult result =
                await fallback.CompleteAsync(new LlmCompletionRequest { AgentRoleId = "test" });
            Assert.IsTrue(result.Ok);
            Assert.AreEqual("primary-ok", result.Content);
            Assert.AreEqual(0, secondary.CallCount, "Secondary should not be called when primary succeeds");
            Assert.AreEqual(0, fallback.FallbackCount);
        }

        [Test]
        public async Task Fallback_PrimaryFails_SecondaryIsCalled()
        {
            FailingLlmClient primary = new();
            StubLlmClient secondary = new("secondary-ok");
            FallbackLlmClientDecorator fallback = new(primary, secondary);

            LlmCompletionResult result =
                await fallback.CompleteAsync(new LlmCompletionRequest { AgentRoleId = "test" });
            Assert.IsTrue(result.Ok);
            Assert.AreEqual("secondary-ok", result.Content);
            Assert.AreEqual(1, fallback.FallbackCount);
        }

        [Test]
        public async Task Fallback_PrimaryReturnsRetryableError_SecondaryIsCalled()
        {
            ErrorResultLlmClient primary = new(LlmErrorCode.BackendUnavailable);
            StubLlmClient secondary = new("fallback-success");
            FallbackLlmClientDecorator fallback = new(primary, secondary);

            LlmCompletionResult result =
                await fallback.CompleteAsync(new LlmCompletionRequest { AgentRoleId = "test" });
            Assert.IsTrue(result.Ok);
            Assert.AreEqual("fallback-success", result.Content);
            Assert.AreEqual(1, fallback.FallbackCount);
        }

        [Test]
        public async Task Fallback_PrimaryStreamingCompletesWithoutChunks_SecondaryStreamingIsCalled()
        {
            EmptyStreamingLlmClient primary = new();
            StreamingCountingLlmClient secondary = new("secondary-stream");
            FallbackLlmClientDecorator fallback = new(primary, secondary);

            List<LlmStreamChunk> chunks = new();
            await foreach (LlmStreamChunk chunk in fallback.CompleteStreamingAsync(
                               new LlmCompletionRequest { AgentRoleId = "test" }))
            {
                chunks.Add(chunk);
            }

            Assert.AreEqual(1, fallback.FallbackCount);
            Assert.AreEqual(1, secondary.StreamingCallCount);
            Assert.AreEqual(2, chunks.Count);
            Assert.AreEqual("secondary-stream", chunks[0].Text);
            Assert.IsTrue(chunks[1].IsDone);
        }

        [Test]
        public void Fallback_Cancellation_DoesNotFallback()
        {
            CancellationTokenSource cts = new();
            cts.Cancel();

            // FailingLlmClient checks ct.ThrowIfCancellationRequested() first
            FailingLlmClient primary = new();
            CountingLlmClient secondary = new("secondary");
            FallbackLlmClientDecorator fallback = new(primary, secondary);

            Assert.CatchAsync<OperationCanceledException>(async () =>
                await fallback.CompleteAsync(new LlmCompletionRequest { AgentRoleId = "test" }, cts.Token));
            Assert.AreEqual(0, secondary.CallCount, "Secondary should not be called on cancellation");
            Assert.AreEqual(0, fallback.FallbackCount);
        }

        [Test]
        public async Task Fallback_MultipleFails_CounterIncrements()
        {
            FailingLlmClient primary = new();
            StubLlmClient secondary = new("ok");
            FallbackLlmClientDecorator fallback = new(primary, secondary);

            await fallback.CompleteAsync(new LlmCompletionRequest { AgentRoleId = "a" });
            await fallback.CompleteAsync(new LlmCompletionRequest { AgentRoleId = "b" });
            await fallback.CompleteAsync(new LlmCompletionRequest { AgentRoleId = "c" });

            Assert.AreEqual(3, fallback.FallbackCount);
        }

        // Helpers for fallback tests

        private sealed class CountingLlmClient : ILlmClient
        {
            private readonly string _response;
            public int CallCount { get; private set; }

            public CountingLlmClient(string response)
            {
                _response = response;
            }

            public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request, CancellationToken ct = default)
            {
                CallCount++;
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = _response });
            }

            public IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(LlmCompletionRequest request,
                CancellationToken ct = default)
            {
                throw new NotImplementedException();
            }
        }

        private sealed class EmptyStreamingLlmClient : ILlmClient
        {
            public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request, CancellationToken ct = default)
            {
                return Task.FromResult(new LlmCompletionResult { Ok = false, Error = "empty" });
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken ct = default)
            {
                await Task.Yield();
                yield break;
            }
        }

        private sealed class StreamingCountingLlmClient : ILlmClient
        {
            private readonly string _text;
            public int StreamingCallCount { get; private set; }

            public StreamingCountingLlmClient(string text)
            {
                _text = text;
            }

            public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request, CancellationToken ct = default)
            {
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = _text });
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken ct = default)
            {
                StreamingCallCount++;
                ct.ThrowIfCancellationRequested();
                yield return new LlmStreamChunk { Text = _text };
                await Task.Yield();
                ct.ThrowIfCancellationRequested();
                yield return new LlmStreamChunk { IsDone = true };
            }
        }

        private sealed class FailingLlmClient : ILlmClient
        {
            public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request, CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                throw new LlmClientException("primary down", LlmErrorCode.ProviderError);
            }

            public IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(LlmCompletionRequest request,
                CancellationToken ct = default)
            {
                throw new LlmClientException("primary down", LlmErrorCode.ProviderError);
            }
        }

        private sealed class ErrorResultLlmClient : ILlmClient
        {
            private readonly LlmErrorCode _code;

            public ErrorResultLlmClient(LlmErrorCode code)
            {
                _code = code;
            }

            public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request, CancellationToken ct = default)
            {
                return Task.FromResult(new LlmCompletionResult
                    { Ok = false, Error = "backend error", ErrorCode = _code });
            }

            public IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(LlmCompletionRequest request,
                CancellationToken ct = default)
            {
                throw new NotImplementedException();
            }
        }
    }
}
#endif
