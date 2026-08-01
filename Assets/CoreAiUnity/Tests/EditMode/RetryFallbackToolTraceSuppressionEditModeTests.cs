using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using CoreAI.Logging;
using MEAI = Microsoft.Extensions.AI;
using Newtonsoft.Json;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Retry/fallback suppression must count only traces whose tool body actually ran.
    /// An error result carrying only rejected/never-invoked traces (unknown tool, duplicate,
    /// parse error, missing binding) executed nothing, so a retryable provider failure in the
    /// same turn must stay retry- and fallback-eligible. Any actually-invoked trace (including
    /// invoked-but-failed, which may have mutated state) must keep suppressing both.
    /// </summary>
    public sealed class RetryFallbackToolTraceSuppressionEditModeTests
    {
        private sealed class StubSettings : ICoreAISettings
        {
            public int MaxLuaRepairRetries => 3;
            public bool EnableMeaiDebugLogging => false;
            public float LlmRequestTimeoutSeconds => 30f;
            public int MaxLlmRequestRetries => 3;
            public bool EnableHttpDebugLogging => false;
            public bool LogTokenUsage => false;
            public bool LogLlmLatency => false;
            public bool LogLlmConnectionErrors => false;
            public int ContextWindowTokens => 4096;
            public string UniversalSystemPromptPrefix => "";
            public float Temperature => 0.7f;
            public int MaxToolCallRetries => 3;
            public bool LogToolCalls => false;
            public bool LogToolCallArguments => false;
            public bool LogToolCallResults => false;
            public bool LogMeaiToolCallingSteps => false;
            public bool AllowDuplicateToolCalls => false;
            public bool EnableStreaming => true;
            public int MaxParallelToolCalls => 1;
            public int MaxToolResultChars => 8000;
            public ILlmAsyncMarshaler ToolInvocationMarshaler => PassThroughLlmAsyncMarshaler.Instance;
        }

        private static LlmToolCallTrace RejectedUnknownToolTrace()
        {
            return new LlmToolCallTrace("no_such_tool", false, 0d, "unknown-tool",
                "Error: Unknown tool 'no_such_tool'.");
        }

        private static LlmToolCallTrace RejectedDuplicateTrace()
        {
            return new LlmToolCallTrace("spawn", false, 0d, "duplicate",
                "Duplicate tool call 'spawn' with same arguments - skipped.");
        }

        private static LlmToolCallTrace InvokedNativeTrace()
        {
            return new LlmToolCallTrace("world_tool", false, 3d, "native", "threw: boom");
        }

        private sealed class RetryableFailureThenOkMock : ILlmClient
        {
            private readonly IReadOnlyList<LlmToolCallTrace> _firstFailureTraces;
            public int CompleteCallCount;

            public RetryableFailureThenOkMock(IReadOnlyList<LlmToolCallTrace> firstFailureTraces)
            {
                _firstFailureTraces = firstFailureTraces;
            }

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                CompleteCallCount++;
                if (CompleteCallCount == 1)
                {
                    return Task.FromResult(new LlmCompletionResult
                    {
                        Ok = false,
                        Error = "HTTP 429",
                        ErrorCode = LlmErrorCode.RateLimited,
                        HttpStatus = 429,
                        RetryAfterSeconds = 1,
                        ExecutedToolCalls = _firstFailureTraces
                    });
                }

                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = "recovered" });
            }
        }

        private sealed class FixedResultLlm : ILlmClient
        {
            private readonly LlmCompletionResult _result;
            public int CompleteCallCount;

            public FixedResultLlm(LlmCompletionResult result)
            {
                _result = result;
            }

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                CompleteCallCount++;
                return Task.FromResult(_result);
            }
        }

#if COREAI_LLM
        [Test]
        public async Task DelegateLlmTool_MutateThenThrow_ReturnsErrorAndRecordsNativeTrace()
        {
            int sideEffects = 0;
            Func<string> body = () =>
            {
                sideEffects++;
                throw new JsonReaderException("cannot convert mutated payload");
            };
            DelegateLlmTool tool = new("grant_item", "Grant an item.", body);
            ToolExecutionPolicy policy = new(
                NullLog.Instance,
                new StubSettings(),
                new ILlmTool[] { tool },
                false,
                "Tester");
            MEAI.ChatOptions options = new() { Tools = new List<MEAI.AITool> { tool.CreateAIFunction() } };
            MEAI.FunctionCallContent call = new(
                "call_grant_item",
                tool.Name,
                new Dictionary<string, object>());

            ToolExecutionPolicy.ToolCallResult result =
                await policy.ExecuteSingleAsync(call, options, CancellationToken.None);

            Assert.AreEqual(1, sideEffects);
            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual("Error: cannot convert mutated payload", result.Result.Result.ToString());
            Assert.AreEqual(1, policy.ExecutedTraces.Count);
            Assert.AreEqual("native", policy.ExecutedTraces[0].Source);
        }

        [Test]
        public async Task DelegateLlmTool_ArgumentCoercionFailure_RecordsArgConversionWithoutInvokingBody()
        {
            int sideEffects = 0;
            Func<int, string> body = count =>
            {
                sideEffects += count;
                return "ok";
            };
            DelegateLlmTool tool = new("grant_items", "Grant items.", body);
            ToolExecutionPolicy policy = new(
                NullLog.Instance,
                new StubSettings(),
                new ILlmTool[] { tool },
                false,
                "Tester");
            MEAI.ChatOptions options = new() { Tools = new List<MEAI.AITool> { tool.CreateAIFunction() } };
            MEAI.FunctionCallContent call = new(
                "call_grant_items",
                tool.Name,
                new Dictionary<string, object> { ["count"] = "not-an-integer" });

            ToolExecutionPolicy.ToolCallResult result =
                await policy.ExecuteSingleAsync(call, options, CancellationToken.None);

            Assert.AreEqual(0, sideEffects);
            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(1, policy.ExecutedTraces.Count);
            Assert.AreEqual("arg-conversion", policy.ExecutedTraces[0].Source);
        }
#endif

        [Test]
        [Timeout(20_000)]
        public async Task LoggingDecorator_RetryableFailure_OnlyRejectedTraces_RetryProceeds()
        {
            RetryableFailureThenOkMock inner = new(new[]
            {
                RejectedUnknownToolTrace(),
                RejectedDuplicateTrace()
            });
            LoggingLlmClientDecorator dec = new(inner, NullLog.Instance, 0f, 1);

            LlmCompletionResult result = await dec.CompleteAsync(new LlmCompletionRequest
            {
                AgentRoleId = "Tester",
                TraceId = "rejected-traces-retry",
                UserPayload = "x"
            });

            Assert.IsTrue(result.Ok, "A 429 after only rejected tool calls must be retried");
            Assert.AreEqual("recovered", result.Content);
            Assert.AreEqual(2, inner.CompleteCallCount);
        }

        [Test]
        [Timeout(20_000)]
        public async Task LoggingDecorator_RetryableFailure_ExecutedTrace_RetrySuppressed()
        {
            RetryableFailureThenOkMock inner = new(new[] { InvokedNativeTrace() });
            LoggingLlmClientDecorator dec = new(inner, NullLog.Instance, 0f, 1);

            LlmCompletionResult result = await dec.CompleteAsync(new LlmCompletionRequest
            {
                AgentRoleId = "Tester",
                TraceId = "executed-trace-no-retry",
                UserPayload = "x"
            });

            Assert.IsFalse(result.Ok, "A turn whose tool body ran must never be blindly retried");
            Assert.AreEqual(1, inner.CompleteCallCount);
            Assert.AreEqual(1, result.ExecutedToolCalls.Count);
        }

        [Test]
        [Timeout(20_000)]
        public async Task FallbackDecorator_RetryableFailure_OnlyRejectedTraces_FallsBack()
        {
            FixedResultLlm primary = new(new LlmCompletionResult
            {
                Ok = false,
                Error = "HTTP 429",
                ErrorCode = LlmErrorCode.RateLimited,
                ExecutedToolCalls = new[] { RejectedUnknownToolTrace() }
            });
            FixedResultLlm secondary = new(new LlmCompletionResult { Ok = true, Content = "secondary" });
            FallbackLlmClientDecorator dec = new(primary, secondary, NullLog.Instance);

            LlmCompletionResult result = await dec.CompleteAsync(new LlmCompletionRequest
            {
                AgentRoleId = "Tester",
                UserPayload = "x"
            });

            Assert.IsTrue(result.Ok, "Nothing executed, so the secondary provider must be tried");
            Assert.AreEqual("secondary", result.Content);
            Assert.AreEqual(1, primary.CompleteCallCount);
            Assert.AreEqual(1, secondary.CompleteCallCount);
        }

        [Test]
        [Timeout(20_000)]
        public async Task FallbackDecorator_RetryableFailure_ExecutedTrace_DoesNotFallBack()
        {
            FixedResultLlm primary = new(new LlmCompletionResult
            {
                Ok = false,
                Error = "HTTP 503 after mutation",
                ErrorCode = LlmErrorCode.BackendUnavailable,
                ExecutedToolCalls = new[] { InvokedNativeTrace() }
            });
            FixedResultLlm secondary = new(new LlmCompletionResult { Ok = true, Content = "secondary" });
            FallbackLlmClientDecorator dec = new(primary, secondary, NullLog.Instance);

            LlmCompletionResult result = await dec.CompleteAsync(new LlmCompletionRequest
            {
                AgentRoleId = "Tester",
                UserPayload = "x"
            });

            Assert.IsFalse(result.Ok, "A turn that invoked a tool must not be replayed on the secondary");
            Assert.AreEqual(0, secondary.CompleteCallCount);
        }

        [Test]
        [Timeout(20_000)]
        public async Task FallbackDecorator_TimeoutFailureResult_IsFallbackEligible()
        {
            FixedResultLlm primary = new(new LlmCompletionResult
            {
                Ok = false,
                Error = "LLM request timed out after 30s without a response.",
                ErrorCode = LlmErrorCode.Timeout
            });
            FixedResultLlm secondary = new(new LlmCompletionResult { Ok = true, Content = "secondary" });
            FallbackLlmClientDecorator dec = new(primary, secondary, NullLog.Instance);

            LlmCompletionResult result = await dec.CompleteAsync(new LlmCompletionRequest
            {
                AgentRoleId = "Tester",
                UserPayload = "x"
            });

            Assert.IsTrue(result.Ok, "A transport timeout on the primary must reach the secondary");
            Assert.AreEqual(1, secondary.CompleteCallCount);
        }

        [Test]
        public void TraceIndicatesInvocation_ClassifiesSourcesCorrectly()
        {
            Assert.IsFalse(LoggingLlmClientDecorator.TraceIndicatesInvocation(
                new LlmToolCallTrace("t", false, 0d, "duplicate")));
            Assert.IsFalse(LoggingLlmClientDecorator.TraceIndicatesInvocation(
                new LlmToolCallTrace("t", false, 0d, "parse-error")));
            Assert.IsFalse(LoggingLlmClientDecorator.TraceIndicatesInvocation(
                new LlmToolCallTrace("t", false, 0d, "unknown-tool")));
            Assert.IsFalse(LoggingLlmClientDecorator.TraceIndicatesInvocation(
                new LlmToolCallTrace("t", false, 0d, "missing")));
            Assert.IsFalse(LoggingLlmClientDecorator.TraceIndicatesInvocation(
                new LlmToolCallTrace("t", false, 0d, "unbound-native")));
            Assert.IsFalse(LoggingLlmClientDecorator.TraceIndicatesInvocation(
                new LlmToolCallTrace("t", false, 0d, "schema-validation")));
            Assert.IsFalse(LoggingLlmClientDecorator.TraceIndicatesInvocation(
                new LlmToolCallTrace("t", false, 0d, "arg-conversion")));

            Assert.IsTrue(LoggingLlmClientDecorator.TraceIndicatesInvocation(
                new LlmToolCallTrace("t", true, 5d, "native")));
            Assert.IsTrue(LoggingLlmClientDecorator.TraceIndicatesInvocation(
                new LlmToolCallTrace("t", false, 5d, "native")));
            Assert.IsTrue(LoggingLlmClientDecorator.TraceIndicatesInvocation(
                new LlmToolCallTrace("t", false, 5d, "timeout")));
            // WHY: unknown/new sources must fail safe as "invoked" so double-execution
            // protection holds even if a new trace source is added later.
            Assert.IsTrue(LoggingLlmClientDecorator.TraceIndicatesInvocation(
                new LlmToolCallTrace("t", false, 5d, "some-future-source")));
        }

        [Test]
        public void HasInvokedToolCalls_EmptyOrRejectedOnly_ReturnsFalse()
        {
            Assert.IsFalse(LoggingLlmClientDecorator.HasInvokedToolCalls(null));
            Assert.IsFalse(LoggingLlmClientDecorator.HasInvokedToolCalls(Array.Empty<LlmToolCallTrace>()));
            Assert.IsFalse(LoggingLlmClientDecorator.HasInvokedToolCalls(new[]
            {
                RejectedUnknownToolTrace(),
                RejectedDuplicateTrace()
            }));
            Assert.IsTrue(LoggingLlmClientDecorator.HasInvokedToolCalls(new[]
            {
                RejectedDuplicateTrace(),
                InvokedNativeTrace()
            }));
        }

        private sealed class FixedResultClient : ILlmClient
        {
            public LlmCompletionResult Result;

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(Result);
            }
        }

        [Test]
        public async Task DefaultStreamAdapter_TerminalChunk_KeepsCumulativePromptTokensAndCarriesLastRoundtrip()
        {
            ILlmClient client = new FixedResultClient
            {
                Result = new LlmCompletionResult
                {
                    Ok = true,
                    Content = "hi",
                    PromptTokens = 100,
                    LastRoundtripPromptTokens = 25,
                    CompletionTokens = 40,
                    TotalTokens = 140
                }
            };

            LlmStreamChunk terminal = null;
            await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(new LlmCompletionRequest()))
            {
                if (chunk.IsDone)
                {
                    terminal = chunk;
                }
            }

            Assert.IsNotNull(terminal);
            Assert.AreEqual(100, terminal.PromptTokens, "PromptTokens must stay the cumulative turn sum");
            Assert.AreEqual(25, terminal.LastRoundtripPromptTokens,
                "calibration field must carry the last roundtrip's prompt size");
            Assert.AreEqual(terminal.PromptTokens + terminal.CompletionTokens, terminal.TotalTokens);
        }
    }
}
