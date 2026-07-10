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
                Assert.AreEqual(20, CoreAISettings.MaxToolCallRoundtrips,
                    "Default MaxToolCallRoundtrips should be 20");
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
                    "Default MaxToolCallHistoryMessages should be 20 (0 = explicit opt-out, unlimited)");
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

                CoreAISettings.MaxToolCallHistoryMessages = 0;
                Assert.AreEqual(0, CoreAISettings.MaxToolCallHistoryMessages,
                    "0 must be honored as an explicit opt-out (unlimited)");

                CoreAISettings.ResetOverrides();
                Assert.AreEqual(20, CoreAISettings.MaxToolCallHistoryMessages, "Should reset to default (20)");
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
        public async Task Fallback_PrimaryReturnsNonRetryableError_SecondaryNotCalled()
        {
            ErrorResultLlmClient primary = new(LlmErrorCode.InvalidRequest);
            CountingLlmClient secondary = new("secondary");
            FallbackLlmClientDecorator fallback = new(primary, secondary);

            LlmCompletionResult result =
                await fallback.CompleteAsync(new LlmCompletionRequest { AgentRoleId = "test" });

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(LlmErrorCode.InvalidRequest, result.ErrorCode);
            Assert.AreEqual(0, fallback.FallbackCount, "Non-retryable errors should not trigger fallback.");
            Assert.AreEqual(0, secondary.CallCount, "Secondary must not be called for non-retryable error.");
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
        public async Task Fallback_PrimaryStreamingThrows_SecondaryStreamingIsCalled()
        {
            ThrowingStreamingLlmClient primary = new();
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
        public async Task Fallback_PrimaryStreamingNonRetryableErrorChunk_IsNotFallbacked()
        {
            ErrorStreamingLlmClient primary = new(LlmErrorCode.InvalidRequest);
            StreamingCountingLlmClient secondary = new("secondary-stream");
            FallbackLlmClientDecorator fallback = new(primary, secondary);

            List<LlmStreamChunk> chunks = new();
            await foreach (LlmStreamChunk chunk in fallback.CompleteStreamingAsync(
                               new LlmCompletionRequest { AgentRoleId = "test" }))
            {
                chunks.Add(chunk);
            }

            Assert.AreEqual(0, fallback.FallbackCount);
            Assert.AreEqual(0, secondary.StreamingCallCount);
            Assert.AreEqual(1, chunks.Count, "Primary non-retryable error chunk should be preserved.");
            Assert.AreEqual("primary-error", chunks[0].Error);
            Assert.AreEqual(LlmErrorCode.InvalidRequest, chunks[0].ErrorCode);
            Assert.IsFalse(chunks[0].IsDone);
        }

        [Test]
        public async Task Fallback_Cancellation_DoesNotFallback()
        {
            CancellationTokenSource cts = new();
            cts.Cancel();

            // FailingLlmClient checks ct.ThrowIfCancellationRequested() first
            FailingLlmClient primary = new();
            CountingLlmClient secondary = new("secondary");
            FallbackLlmClientDecorator fallback = new(primary, secondary);

            try
            {
                await fallback.CompleteAsync(new LlmCompletionRequest { AgentRoleId = "test" }, cts.Token);
                Assert.Fail("expected OperationCanceledException");
            }
            catch (OperationCanceledException)
            {
            }

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

        // ==================== FallbackLlmClientDecorator: commit-aware streaming fallback (F-09) ====================

        [Test]
        public async Task Fallback_ControlChunkThenInternalTimeout_FallsBackToSecondary()
        {
            ControlThenTimeoutStreamingLlmClient primary = new();
            StreamingCountingLlmClient secondary = new("secondary-stream");
            FallbackLlmClientDecorator fallback = new(primary, secondary);

            List<LlmStreamChunk> chunks = new();
            await foreach (LlmStreamChunk chunk in fallback.CompleteStreamingAsync(
                               new LlmCompletionRequest { AgentRoleId = "test" }))
            {
                chunks.Add(chunk);
            }

            Assert.AreEqual(1, primary.StreamingCallCount);
            Assert.AreEqual(1, fallback.FallbackCount,
                "An internal timeout before any visible content must still fall back");
            Assert.AreEqual(1, secondary.StreamingCallCount);

            // First chunk is primary's benign control chunk (no visible text), then the full secondary stream.
            Assert.AreEqual(3, chunks.Count);
            Assert.IsTrue(string.IsNullOrEmpty(chunks[0].Text), "Control chunk carries no visible text");
            Assert.AreEqual("secondary-stream", chunks[1].Text);
            Assert.IsTrue(chunks[2].IsDone);
        }

        [Test]
        public async Task Fallback_FirstTextChunkThenTimeout_DoesNotFallback_NoDoubleExecution()
        {
            TextThenTimeoutStreamingLlmClient primary = new();
            StreamingCountingLlmClient secondary = new("secondary-stream");
            FallbackLlmClientDecorator fallback = new(primary, secondary);

            List<LlmStreamChunk> chunks = new();
            try
            {
                await foreach (LlmStreamChunk chunk in fallback.CompleteStreamingAsync(
                                   new LlmCompletionRequest { AgentRoleId = "test" }))
                {
                    chunks.Add(chunk);
                }

                Assert.Fail("Expected the internal timeout to propagate once content was already committed");
            }
            catch (OperationCanceledException)
            {
            }

            Assert.AreEqual(1, chunks.Count, "Only the already-committed text chunk should have been yielded");
            Assert.AreEqual("primary-text", chunks[0].Text);
            Assert.AreEqual(0, fallback.FallbackCount, "Must not fall back once content was already committed");
            Assert.AreEqual(0, secondary.StreamingCallCount,
                "Secondary must not run - it would duplicate already-streamed content");
        }

        [Test]
        public async Task Fallback_StreamingCallerCancelled_DoesNotFallback()
        {
            CancellationTokenSource cts = new();
            cts.Cancel();

            StreamingCountingLlmClient primary = new("primary-text");
            StreamingCountingLlmClient secondary = new("secondary-text");
            FallbackLlmClientDecorator fallback = new(primary, secondary);

            try
            {
                await foreach (LlmStreamChunk chunk in fallback.CompleteStreamingAsync(
                                   new LlmCompletionRequest { AgentRoleId = "test" }, cts.Token))
                {
                }

                Assert.Fail("expected OperationCanceledException");
            }
            catch (OperationCanceledException)
            {
            }

            Assert.AreEqual(0, secondary.StreamingCallCount, "Secondary must not run on caller cancellation");
            Assert.AreEqual(0, fallback.FallbackCount);
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

        private sealed class ThrowingStreamingLlmClient : ILlmClient
        {
            public int StreamingCallCount { get; private set; }

            public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request,
                CancellationToken ct = default)
            {
                return Task.FromResult(new LlmCompletionResult { Ok = false, Error = "streaming exception" });
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken ct = default)
            {
                StreamingCallCount++;
                await Task.Yield();
                throw new LlmClientException("primary stream fault", LlmErrorCode.ProviderError);
#pragma warning disable CS0162
                yield return default;
#pragma warning restore CS0162
            }
        }

        /// <summary>Yields one benign control chunk (no visible text) then simulates an internal
        /// provider/transport timeout, mirroring MeaiLlmClient's tool-buffering hint chunk followed by a
        /// transport-level timeout with the caller's token uncancelled.</summary>
        private sealed class ControlThenTimeoutStreamingLlmClient : ILlmClient
        {
            public int StreamingCallCount { get; private set; }

            public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request,
                CancellationToken ct = default)
            {
                return Task.FromResult(new LlmCompletionResult { Ok = false, Error = "n/a" });
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken ct = default)
            {
                StreamingCallCount++;
                yield return new LlmStreamChunk(); // control/hint chunk: no text, no tool call, not done
                await Task.Yield();
                throw new LlmOperationTimeoutException(); // internal timeout; caller ct is NOT cancelled
#pragma warning disable CS0162
                yield return default;
#pragma warning restore CS0162
            }
        }

        /// <summary>Yields a real visible text chunk (committing the stream) then simulates an internal
        /// timeout. The decorator must not fall back after this point.</summary>
        private sealed class TextThenTimeoutStreamingLlmClient : ILlmClient
        {
            public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request,
                CancellationToken ct = default)
            {
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = "primary-text" });
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken ct = default)
            {
                yield return new LlmStreamChunk { Text = "primary-text" };
                await Task.Yield();
                throw new LlmOperationTimeoutException(); // internal timeout after commitment; must propagate
#pragma warning disable CS0162
                yield return default;
#pragma warning restore CS0162
            }
        }

        private sealed class ErrorStreamingLlmClient : ILlmClient
        {
            private readonly LlmErrorCode _code;
            public int StreamingCallCount { get; private set; }

            public ErrorStreamingLlmClient(LlmErrorCode code)
            {
                _code = code;
            }

            public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request,
                CancellationToken ct = default)
            {
                return Task.FromResult(new LlmCompletionResult
                    { Ok = false, Error = "primary async error", ErrorCode = _code });
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken ct = default)
            {
                StreamingCallCount++;
                await Task.Yield();
                ct.ThrowIfCancellationRequested();
                yield return new LlmStreamChunk
                {
                    Text = string.Empty,
                    Error = "primary-error",
                    ErrorCode = _code
                };
            }
        }

        // ==================== Retry Backoff Jitter (v3.x) ====================

        [Test]
        public void BackoffJitter_DelayWithinBaseBounds()
        {
            Random random = new(12345);
            for (int attempt = 0; attempt < 8; attempt++)
            {
                int baseDelay = LoggingLlmClientDecorator.ComputeBackoffBase(attempt);
                for (int i = 0; i < 200; i++)
                {
                    int delay = LoggingLlmClientDecorator.ComputeBackoffDelay(attempt, random);
                    Assert.That(delay, Is.GreaterThanOrEqualTo(0),
                        $"attempt={attempt}: jittered delay must be >= 0");
                    Assert.That(delay, Is.LessThanOrEqualTo(baseDelay),
                        $"attempt={attempt}: jittered delay must be <= base ({baseDelay}s)");
                }
            }
        }

        [Test]
        public void BackoffJitter_BaseGrowsMonotonicallyAndIsCapped()
        {
            int previous = 0;
            for (int attempt = 0; attempt < 12; attempt++)
            {
                int baseDelay = LoggingLlmClientDecorator.ComputeBackoffBase(attempt);
                Assert.That(baseDelay, Is.GreaterThanOrEqualTo(previous),
                    $"Base delay must grow monotonically (attempt {attempt})");
                Assert.That(baseDelay, Is.LessThanOrEqualTo(30), "Base delay must respect the 30s cap");
                previous = baseDelay;
            }

            Assert.AreEqual(2, LoggingLlmClientDecorator.ComputeBackoffBase(0));
            Assert.AreEqual(4, LoggingLlmClientDecorator.ComputeBackoffBase(1));
            Assert.AreEqual(30, LoggingLlmClientDecorator.ComputeBackoffBase(10), "Large attempts hit the cap");
        }

        [Test]
        public void BackoffJitter_NullRandom_ReturnsDeterministicBase()
        {
            for (int attempt = 0; attempt < 6; attempt++)
            {
                Assert.AreEqual(
                    LoggingLlmClientDecorator.ComputeBackoffBase(attempt),
                    LoggingLlmClientDecorator.ComputeBackoffDelay(attempt, null));
            }
        }

        // ==================== Tool Name Repair Counter (v3.x) ====================

        private sealed class StubLlmTool : ILlmTool
        {
            public StubLlmTool(string name)
            {
                Name = name;
            }

            public string Name { get; }
            public string Description => "stub tool";
            public string ParametersSchema => "{}";
            public bool AllowDuplicates => true;
        }

        [Test]
        public void ToolNameRepair_CasingRepair_IncrementsCounter()
        {
            ToolExecutionPolicy.ResetToolNameRepairCount();
            ResilienceSettings settings = new();
            ToolExecutionPolicy policy = new(NullLog.Instance, settings,
                new List<ILlmTool> { new StubLlmTool("craft_item") }, true, "test", 3);

            MEAI.FunctionCallContent repaired =
                policy.TryRepairToolName(new MEAI.FunctionCallContent("c1", "Craft_Item"));

            Assert.IsNotNull(repaired);
            Assert.AreEqual("craft_item", repaired.Name);
            Assert.AreEqual(1, ToolExecutionPolicy.ToolNameRepairCount, "Repair should increment counter");

            policy.TryRepairToolName(new MEAI.FunctionCallContent("c2", "CRAFT_ITEM"));
            Assert.AreEqual(2, ToolExecutionPolicy.ToolNameRepairCount, "Each repair increments counter");

            ToolExecutionPolicy.ResetToolNameRepairCount();
            Assert.AreEqual(0, ToolExecutionPolicy.ToolNameRepairCount, "Reset should zero the counter");
        }

        [Test]
        public void ToolNameRepair_ExactOrUnknownName_DoesNotIncrementCounter()
        {
            ToolExecutionPolicy.ResetToolNameRepairCount();
            ResilienceSettings settings = new();
            ToolExecutionPolicy policy = new(NullLog.Instance, settings,
                new List<ILlmTool> { new StubLlmTool("craft_item") }, true, "test", 3);

            // Exact match: no repair needed
            MEAI.FunctionCallContent exact =
                policy.TryRepairToolName(new MEAI.FunctionCallContent("c1", "craft_item"));
            Assert.IsNotNull(exact);
            Assert.AreEqual(0, ToolExecutionPolicy.ToolNameRepairCount);

            // Genuinely unknown: no repair possible
            MEAI.FunctionCallContent unknown =
                policy.TryRepairToolName(new MEAI.FunctionCallContent("c2", "no_such_tool"));
            Assert.IsNull(unknown);
            Assert.AreEqual(0, ToolExecutionPolicy.ToolNameRepairCount);
        }

        // ==================== Error-Feedback Lifecycle (v3.x) ====================

        private sealed class ScriptedChatClient : MEAI.IChatClient
        {
            private readonly Queue<Func<MEAI.ChatResponse>> _script;

            public ScriptedChatClient(params Func<MEAI.ChatResponse>[] script)
            {
                _script = new Queue<Func<MEAI.ChatResponse>>(script);
            }

            /// <summary>Snapshot of the message list observed on each inner call.</summary>
            public List<List<MEAI.ChatMessage>> ObservedMessages { get; } = new();

            public Task<MEAI.ChatResponse> GetResponseAsync(
                IEnumerable<MEAI.ChatMessage> messages,
                MEAI.ChatOptions options = null,
                CancellationToken cancellationToken = default)
            {
                ObservedMessages.Add(new List<MEAI.ChatMessage>(messages));
                return Task.FromResult(_script.Dequeue()());
            }

            public IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<MEAI.ChatMessage> messages,
                MEAI.ChatOptions options = null,
                CancellationToken cancellationToken = default)
            {
                throw new NotImplementedException();
            }

            public object GetService(Type serviceType, object serviceKey = null)
            {
                return null;
            }

            public void Dispose()
            {
            }
        }

        private static MEAI.ChatResponse AssistantToolCallResponse(string callId, string toolName)
        {
            return new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant,
                new List<MEAI.AIContent> { new MEAI.FunctionCallContent(callId, toolName) }));
        }

        private static void AssertToolCallPairingValid(List<MEAI.ChatMessage> messages)
        {
            for (int i = 0; i < messages.Count; i++)
            {
                bool hasCall = false;
                foreach (object c in messages[i].Contents)
                {
                    if (c is MEAI.FunctionCallContent)
                    {
                        hasCall = true;
                    }
                }

                if (hasCall)
                {
                    Assert.That(i + 1, Is.LessThan(messages.Count),
                        "Assistant tool-call message must be followed by a Tool result message");
                    Assert.AreEqual(MEAI.ChatRole.Tool, messages[i + 1].Role,
                        "Assistant tool-call message must be immediately followed by a Tool message");
                }

                if (messages[i].Role == MEAI.ChatRole.Tool)
                {
                    Assert.That(i, Is.GreaterThan(0));
                    bool prevHasCall = false;
                    foreach (object c in messages[i - 1].Contents)
                    {
                        if (c is MEAI.FunctionCallContent)
                        {
                            prevHasCall = true;
                        }
                    }

                    Assert.IsTrue(prevHasCall,
                        "Tool result message must be preceded by an assistant tool-call message");
                }
            }
        }

        [Test]
        public async Task ErrorFeedback_RemovedAfterSuccessfulRetry_HistoryStaysPaired()
        {
            ResilienceSettings settings = new();
            // fail_tool returns a structured failure; ok_tool succeeds.
            Func<string> failFunc = () => "{\"success\":false,\"error\":\"boom\"}";
            Func<string> okFunc = () => "ok";
            MEAI.ChatOptions opts = MakeChatOptions(("fail_tool", failFunc), ("ok_tool", okFunc));

            ScriptedChatClient inner = new(
                () => AssistantToolCallResponse("call_fail_1", "fail_tool"),
                () => AssistantToolCallResponse("call_ok_1", "ok_tool"),
                () => new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, "done")));

            SmartToolCallingChatClient client = new(inner, NullLog.Instance, settings,
                true, new List<ILlmTool>(), "test", 3);

            MEAI.ChatResponse response = await client.GetResponseAsync(
                new List<MEAI.ChatMessage> { new(MEAI.ChatRole.User, "hi") }, opts);

            Assert.AreEqual("done", response.Text);
            Assert.AreEqual(3, inner.ObservedMessages.Count);

            // 2nd inner call still sees the error feedback (so the model can retry)
            List<MEAI.ChatMessage> duringRetry = inner.ObservedMessages[1];
            Assert.IsTrue(duringRetry.Exists(m => m.Role == MEAI.ChatRole.Tool),
                "Error feedback must be present while retrying");

            // 3rd inner call: failed pair removed after the successful retry
            List<MEAI.ChatMessage> afterRetry = inner.ObservedMessages[2];
            foreach (MEAI.ChatMessage m in afterRetry)
            {
                foreach (object c in m.Contents)
                {
                    if (c is MEAI.FunctionCallContent fcc)
                    {
                        Assert.AreNotEqual("fail_tool", fcc.Name,
                            "Failed tool-call message should be removed after successful retry");
                    }
                }
            }

            // Exactly one tool-call pair (the successful one) remains, and pairing is valid.
            int toolMsgCount = afterRetry.FindAll(m => m.Role == MEAI.ChatRole.Tool).Count;
            Assert.AreEqual(1, toolMsgCount, "Only the successful tool pair should remain");
            AssertToolCallPairingValid(afterRetry);

            // Original user message is untouched.
            Assert.AreEqual(MEAI.ChatRole.User, afterRetry[0].Role);
        }

        [Test]
        public async Task TrimToolCallHistory_OddOverflow_KeepsPairsCoupled()
        {
            // Cap allows 3 tool-related messages. Each successful tool iteration appends 2
            // (Assistant tool-call + Tool result), so after the 2nd iteration there are 4, forcing
            // a single-message overflow. Removing that one message individually (the old behaviour)
            // would orphan a Tool result whose Assistant tool_calls turn was dropped, which the
            // provider rejects on the next request. The trim must instead drop the whole oldest unit.
            ResilienceSettings settings = new() { MaxToolCallHistoryMessagesOverride = 3 };
            Func<string> okA = () => "a-ok";
            Func<string> okB = () => "b-ok";
            Func<string> okC = () => "c-ok";
            MEAI.ChatOptions opts = MakeChatOptions(("tool_a", okA), ("tool_b", okB), ("tool_c", okC));

            ScriptedChatClient inner = new(
                () => AssistantToolCallResponse("call_a", "tool_a"),
                () => AssistantToolCallResponse("call_b", "tool_b"),
                () => AssistantToolCallResponse("call_c", "tool_c"),
                () => new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, "done")));

            SmartToolCallingChatClient client = new(inner, NullLog.Instance, settings,
                true, new List<ILlmTool>(), "test", 3);

            MEAI.ChatResponse response = await client.GetResponseAsync(
                new List<MEAI.ChatMessage> { new(MEAI.ChatRole.User, "hi") }, opts);

            Assert.AreEqual("done", response.Text);
            Assert.AreEqual(4, inner.ObservedMessages.Count);

            // Every request the provider sees must keep tool results paired with their tool_calls turn.
            foreach (List<MEAI.ChatMessage> observed in inner.ObservedMessages)
            {
                AssertToolCallPairingValid(observed);
            }

            // The final request (after the 3rd tool unit was appended and the oldest trimmed) must
            // hold whole units only, never a stray Tool message. tool_a (the oldest unit) is gone.
            List<MEAI.ChatMessage> finalRequest = inner.ObservedMessages[3];
            int finalToolMsgCount = finalRequest.FindAll(m => m.Role == MEAI.ChatRole.Tool).Count;
            int finalAssistantCallCount = finalRequest.FindAll(m =>
                m.Role == MEAI.ChatRole.Assistant && HasFunctionCall(m)).Count;
            Assert.AreEqual(finalAssistantCallCount, finalToolMsgCount,
                "Each surviving tool result must have a matching assistant tool-call message");
            Assert.IsFalse(finalRequest.Exists(m => HasFunctionCall(m) && CallName(m) == "tool_a"),
                "Oldest tool unit (tool_a) should have been trimmed as a whole");
            Assert.AreEqual(MEAI.ChatRole.User, finalRequest[0].Role, "Original user message is preserved");
        }

        private static bool HasFunctionCall(MEAI.ChatMessage message)
        {
            foreach (object c in message.Contents)
            {
                if (c is MEAI.FunctionCallContent)
                {
                    return true;
                }
            }

            return false;
        }

        private static string CallName(MEAI.ChatMessage message)
        {
            foreach (object c in message.Contents)
            {
                if (c is MEAI.FunctionCallContent fcc)
                {
                    return fcc.Name;
                }
            }

            return null;
        }

        [Test]
        public void RemoveResolvedErrorFeedback_AlreadyTrimmedEntries_AreSkipped()
        {
            MEAI.ChatMessage user = new(MEAI.ChatRole.User, "hi");
            MEAI.ChatMessage failedAssistant = new(MEAI.ChatRole.Assistant,
                new List<MEAI.AIContent> { new MEAI.FunctionCallContent("c1", "fail_tool") });
            MEAI.ChatMessage failedTool = new(MEAI.ChatRole.Tool,
                new List<MEAI.AIContent> { new MEAI.FunctionResultContent("c1", "Error: boom") });

            // failedAssistant was already removed by general history trim; only failedTool remains.
            List<MEAI.ChatMessage> messages = new() { user, failedTool };
            List<MEAI.ChatMessage> pending = new() { failedAssistant, failedTool };

            int removed = ToolCallHistoryTrimmer.RemoveResolvedErrorFeedback(messages, pending);

            Assert.AreEqual(1, removed, "Only the message still present should count as removed");
            Assert.AreEqual(0, pending.Count, "Pending list must be cleared");
            CollectionAssert.AreEqual(new List<MEAI.ChatMessage> { user }, messages);
        }

        // ==================== ToolCallHistoryTrimmer (shared, F1) ====================

        private static MEAI.ChatMessage ToolCallUnitAssistant(string callId, string toolName)
        {
            return new MEAI.ChatMessage(MEAI.ChatRole.Assistant,
                new List<MEAI.AIContent> { new MEAI.FunctionCallContent(callId, toolName) });
        }

        private static MEAI.ChatMessage ToolCallUnitResult(string callId, string result)
        {
            return new MEAI.ChatMessage(MEAI.ChatRole.Tool,
                new List<MEAI.AIContent> { new MEAI.FunctionResultContent(callId, result) });
        }

        [Test]
        public void Trimmer_KeepsSystemAndUserMessages_TrimsOldestUnitsFirst()
        {
            MEAI.ChatMessage system = new(MEAI.ChatRole.System, "sys");
            MEAI.ChatMessage user = new(MEAI.ChatRole.User, "build a castle");
            List<MEAI.ChatMessage> messages = new()
            {
                system,
                user,
                ToolCallUnitAssistant("c1", "tool_a"),
                ToolCallUnitResult("c1", "a-ok"),
                ToolCallUnitAssistant("c2", "tool_b"),
                ToolCallUnitResult("c2", "b-ok"),
                ToolCallUnitAssistant("c3", "tool_c"),
                ToolCallUnitResult("c3", "c-ok")
            };

            // Cap 4 tool-related messages: 6 present, so exactly the OLDEST unit (2 messages) goes.
            int removed = ToolCallHistoryTrimmer.Trim(messages, 4);

            Assert.AreEqual(2, removed, "One whole oldest unit (assistant + tool result) is removed");
            Assert.AreSame(system, messages[0], "System message must always be kept");
            Assert.AreSame(user, messages[1], "Original user message must always be kept");
            Assert.IsFalse(messages.Exists(m => HasFunctionCall(m) && CallName(m) == "tool_a"),
                "The oldest tool exchange must be trimmed first");
            Assert.IsTrue(messages.Exists(m => HasFunctionCall(m) && CallName(m) == "tool_c"),
                "The newest tool exchange must survive");
        }

        [Test]
        public void Trimmer_NeverOrphansToolResultFromItsAssistantMessage()
        {
            // A unit with MULTIPLE tool results must be removed whole - dropping only the assistant
            // message (or only some results) produces the orphaned-tool-message HTTP 400.
            MEAI.ChatMessage user = new(MEAI.ChatRole.User, "hi");
            MEAI.ChatMessage multiAssistant = new(MEAI.ChatRole.Assistant,
                new List<MEAI.AIContent>
                {
                    new MEAI.FunctionCallContent("m1", "tool_x"),
                    new MEAI.FunctionCallContent("m2", "tool_x")
                });
            List<MEAI.ChatMessage> messages = new()
            {
                user,
                multiAssistant,
                ToolCallUnitResult("m1", "x1-ok"),
                ToolCallUnitResult("m2", "x2-ok"),
                ToolCallUnitAssistant("c2", "tool_y"),
                ToolCallUnitResult("c2", "y-ok")
            };

            // Cap 2: the first unit (assistant + BOTH tool results, 3 tool messages) must go whole.
            ToolCallHistoryTrimmer.Trim(messages, 2);

            for (int i = 0; i < messages.Count; i++)
            {
                if (messages[i].Role == MEAI.ChatRole.Tool)
                {
                    Assert.Greater(i, 0);
                    Assert.IsTrue(ToolCallHistoryTrimmer.HasFunctionCallContent(messages[i - 1]),
                        "Every surviving Tool message must directly follow its assistant tool_calls message");
                }
            }

            Assert.IsFalse(messages.Contains(multiAssistant), "The whole oldest unit is removed together");
            Assert.IsTrue(messages.Exists(m => HasFunctionCall(m) && CallName(m) == "tool_y"));
        }

        [Test]
        public void Trimmer_ZeroOrNegativeCap_IsUnlimited_NoTrimming()
        {
            List<MEAI.ChatMessage> messages = new()
            {
                new MEAI.ChatMessage(MEAI.ChatRole.User, "hi"),
                ToolCallUnitAssistant("c1", "tool_a"),
                ToolCallUnitResult("c1", "a-ok"),
                ToolCallUnitAssistant("c2", "tool_b"),
                ToolCallUnitResult("c2", "b-ok")
            };

            Assert.AreEqual(0, ToolCallHistoryTrimmer.Trim(messages, 0), "0 = explicit opt-out (unlimited)");
            Assert.AreEqual(0, ToolCallHistoryTrimmer.Trim(messages, -1), "Negative caps must be inert too");
            Assert.AreEqual(5, messages.Count, "Nothing may be removed when trimming is disabled");
        }

        [Test]
        public void Trimmer_UnderCap_NoTrimming()
        {
            List<MEAI.ChatMessage> messages = new()
            {
                new MEAI.ChatMessage(MEAI.ChatRole.User, "hi"),
                ToolCallUnitAssistant("c1", "tool_a"),
                ToolCallUnitResult("c1", "a-ok")
            };

            Assert.AreEqual(0, ToolCallHistoryTrimmer.Trim(messages, 20));
            Assert.AreEqual(3, messages.Count);
        }

        [Test]
        public void SettingsAsset_MaxToolCallHistoryMessages_DefaultIs20_ZeroIsExplicitOptOut()
        {
            CoreAISettingsAsset asset = UnityEngine.ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            try
            {
                Assert.AreEqual(20, asset.MaxToolCallHistoryMessages,
                    "A fresh settings asset must default to a bounded (20) tool-call history");

                asset.SetMaxToolCallHistoryMessages(0);
                Assert.AreEqual(0, asset.MaxToolCallHistoryMessages,
                    "0 must be honored as an explicit opt-out (unlimited)");

                asset.SetMaxToolCallHistoryMessages(-5);
                Assert.AreEqual(20, asset.MaxToolCallHistoryMessages,
                    "Negative (corrupt) values fall back to the default 20");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        // ==================== Cumulative usage in the non-streaming loop (F5) ====================

        private static MEAI.ChatResponse WithUsage(MEAI.ChatResponse response, int input, int output)
        {
            response.Usage = new MEAI.UsageDetails
            {
                InputTokenCount = input,
                OutputTokenCount = output,
                TotalTokenCount = input + output
            };
            return response;
        }

        [Test]
        public async Task GetResponseAsync_MultiRoundtrip_ReturnsSummedUsage()
        {
            ResilienceSettings settings = new();
            Func<string> okFunc = () => "ok";
            MEAI.ChatOptions opts = MakeChatOptions(("ok_tool", okFunc));

            // Two tool roundtrips + a final text turn, each reporting its own usage: the returned
            // response must carry the SUM (30/12/42), not just the last roundtrip's (10/2/12).
            ScriptedChatClient inner = new(
                () => WithUsage(AssistantToolCallResponse("call_1", "ok_tool"), 10, 4),
                () => WithUsage(AssistantToolCallResponse("call_2", "ok_tool"), 10, 6),
                () => WithUsage(new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, "done")),
                    10, 2));

            SmartToolCallingChatClient client = new(inner, NullLog.Instance, settings,
                true, new List<ILlmTool>(), "test", 3);

            MEAI.ChatResponse response = await client.GetResponseAsync(
                new List<MEAI.ChatMessage> { new(MEAI.ChatRole.User, "hi") }, opts);

            Assert.AreEqual("done", response.Text);
            Assert.IsNotNull(response.Usage, "The terminal response must carry usage");
            Assert.AreEqual(30, (int)(response.Usage.InputTokenCount ?? 0), "Input tokens must be summed");
            Assert.AreEqual(12, (int)(response.Usage.OutputTokenCount ?? 0), "Output tokens must be summed");
            Assert.AreEqual(42, (int)(response.Usage.TotalTokenCount ?? 0), "Total tokens must be summed");
        }

        [Test]
        public async Task GetResponseAsync_SomeRoundtripsWithoutUsage_SumsWhatWasReported()
        {
            ResilienceSettings settings = new();
            Func<string> okFunc = () => "ok";
            MEAI.ChatOptions opts = MakeChatOptions(("ok_tool", okFunc));

            ScriptedChatClient inner = new(
                () => AssistantToolCallResponse("call_1", "ok_tool"), // provider omitted usage
                () => WithUsage(new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, "done")),
                    7, 3));

            SmartToolCallingChatClient client = new(inner, NullLog.Instance, settings,
                true, new List<ILlmTool>(), "test", 3);

            MEAI.ChatResponse response = await client.GetResponseAsync(
                new List<MEAI.ChatMessage> { new(MEAI.ChatRole.User, "hi") }, opts);

            Assert.AreEqual("done", response.Text);
            Assert.IsNotNull(response.Usage);
            Assert.AreEqual(7, (int)(response.Usage.InputTokenCount ?? 0));
            Assert.AreEqual(3, (int)(response.Usage.OutputTokenCount ?? 0));
        }
    }
}
#endif
