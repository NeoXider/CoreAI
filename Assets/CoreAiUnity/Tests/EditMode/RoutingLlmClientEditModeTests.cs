using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using CoreAI.Messaging;
using MessagePipe;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    public sealed class RoutingLlmClientEditModeTests
    {
        /// <summary>
        /// Client stub with full streaming support: configured chunks followed by a final done chunk.
        /// </summary>
        private sealed class StreamingMockLlm : ILlmClient
        {
            private readonly string[] _parts;
            public int CompleteAsyncCalls { get; private set; }
            public int StreamingCalls { get; private set; }

            public StreamingMockLlm(params string[] parts)
            {
                _parts = parts;
            }

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                CompleteAsyncCalls++;
                return Task.FromResult(new LlmCompletionResult
                {
                    Ok = true,
                    Content = string.Concat(_parts),
                    Model = "test-model",
                    PromptTokens = 10,
                    CompletionTokens = 5,
                    TotalTokens = 15,
                    CacheReadTokens = 8,
                    CacheWriteTokens = 4
                });
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request,
                [EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                StreamingCalls++;
                foreach (string part in _parts)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return new LlmStreamChunk { Text = part };
                    await Task.Yield();
                }

                yield return new LlmStreamChunk
                {
                    IsDone = true,
                    Text = string.Empty,
                    Model = "test-model",
                    PromptTokens = 10,
                    CompletionTokens = 5,
                    TotalTokens = 15,
                    CacheReadTokens = 8,
                    CacheWriteTokens = 4
                };
            }
        }

        private sealed class FakeRegistry : ILlmClientRegistry
        {
            private readonly Dictionary<string, ILlmClient> _byRole = new();
            private readonly Dictionary<string, string> _profileByRole = new();
            private readonly Dictionary<string, LlmExecutionMode> _modeByRole = new();
            private readonly ILlmClient _fallback;

            public FakeRegistry(ILlmClient fallback)
            {
                _fallback = fallback;
            }

            public void Register(string roleId, ILlmClient client)
            {
                _byRole[roleId] = client;
                _profileByRole[roleId] = roleId + "Profile";
                _modeByRole[roleId] = LlmExecutionMode.ClientOwnedApi;
            }

            public ILlmClient ResolveClientForRole(string roleId)
            {
                return _byRole.TryGetValue(roleId, out ILlmClient c) ? c : _fallback;
            }

            public int ResolveContextWindowForRole(string roleId)
            {
                return 4096;
            }

            public LlmExecutionMode ResolveExecutionModeForRole(string roleId)
            {
                return _modeByRole.TryGetValue(roleId, out LlmExecutionMode mode) ? mode : LlmExecutionMode.Auto;
            }

            public string ResolveProfileIdForRole(string roleId)
            {
                return _profileByRole.TryGetValue(roleId, out string profileId) ? profileId : "fallback";
            }

            /// <summary>Every route-health report, in order: (profile, code, error). Successes report None.</summary>
            public List<(string ProfileId, LlmErrorCode Code, string Error)> HealthReports { get; } = new();

            public void ReportRouteFailure(string profileId, long generation, LlmErrorCode errorCode, string error)
            {
                HealthReports.Add((profileId, errorCode, error));
            }
        }

        private sealed class CapturingPublisher<T> : IPublisher<T>
        {
            public readonly List<T> Messages = new();

            public void Publish(T message)
            {
                Messages.Add(message);
            }
        }

        private sealed class ThrowingLlm : ILlmClient
        {
            private readonly Exception _ex;

            public ThrowingLlm(Exception ex)
            {
                _ex = ex;
            }

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromException<LlmCompletionResult>(_ex);
            }
        }

        /// <summary>
        /// Client stub emulating an OpenAI-compatible server that returns NO usage object
        /// (LM Studio commonly omits it, including on the streaming final chunk).
        /// </summary>
        private sealed class NoUsageMockLlm : ILlmClient
        {
            private readonly string[] _parts;

            public NoUsageMockLlm(params string[] parts)
            {
                _parts = parts;
            }

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new LlmCompletionResult
                {
                    Ok = true,
                    Content = string.Concat(_parts),
                    Model = "local-model"
                });
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request,
                [EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                foreach (string part in _parts)
                {
                    yield return new LlmStreamChunk { Text = part };
                    await Task.Yield();
                }

                yield return new LlmStreamChunk { IsDone = true, Text = string.Empty, Model = "local-model" };
            }
        }

        [Test]
        public async Task CompleteAsync_NoServerUsage_PublishesEstimatedNonZeroUsage()
        {
            NoUsageMockLlm inner = new("Hello from the local model, a fairly long answer.");
            FakeRegistry registry = new(inner);
            CapturingPublisher<LlmUsageReported> usage = new();
            RoutingLlmClient routing = new(registry, null, null, null, usage);

            LlmCompletionRequest request = new()
            {
                AgentRoleId = "X",
                TraceId = "trace-est",
                SystemPrompt = new string('s', 400),
                UserPayload = new string('u', 200)
            };

            LlmCompletionResult result = await routing.CompleteAsync(request);

            Assert.IsTrue(result.Ok);
            Assert.AreEqual(1, usage.Messages.Count,
                "A completion without server usage must still publish an estimated LlmUsageReported");
            LlmUsageReported reported = usage.Messages[0];
            Assert.Greater(reported.PromptTokens ?? 0, 0, "Estimated prompt tokens must be non-zero");
            Assert.Greater(reported.CompletionTokens ?? 0, 0, "Estimated completion tokens must be non-zero");
            Assert.AreEqual(
                (reported.PromptTokens ?? 0) + (reported.CompletionTokens ?? 0),
                reported.TotalTokens ?? 0);
            // ~4 chars/token: 600 prompt chars => 150 tokens.
            Assert.AreEqual(150, reported.PromptTokens);
        }

        [Test]
        public async Task Streaming_NoServerUsageOnFinalChunk_PublishesEstimatedNonZeroUsage()
        {
            NoUsageMockLlm inner = new("Hello ", "streamed ", "world, quite a few tokens here.");
            FakeRegistry registry = new(inner);
            CapturingPublisher<LlmUsageReported> usage = new();
            RoutingLlmClient routing = new(registry, null, null, null, usage);

            LlmCompletionRequest request = new()
            {
                AgentRoleId = "X",
                TraceId = "trace-est-stream",
                UserPayload = new string('u', 120)
            };

            await foreach (LlmStreamChunk _ in routing.CompleteStreamingAsync(request))
            {
            }

            Assert.AreEqual(1, usage.Messages.Count,
                "A stream without server usage must still publish an estimated LlmUsageReported");
            LlmUsageReported reported = usage.Messages[0];
            Assert.IsTrue(reported.Streaming);
            Assert.IsTrue(reported.Success);
            Assert.Greater(reported.PromptTokens ?? 0, 0, "Estimated prompt tokens must be non-zero");
            Assert.Greater(reported.CompletionTokens ?? 0, 0, "Estimated completion tokens must be non-zero");
            Assert.AreEqual("local-model", reported.Model);
        }

        [Test]
        public async Task CompleteAsync_ServerUsagePresent_IsNotOverriddenByEstimate()
        {
            StreamingMockLlm inner = new("ok");
            FakeRegistry registry = new(inner);
            CapturingPublisher<LlmUsageReported> usage = new();
            RoutingLlmClient routing = new(registry, null, null, null, usage);

            await routing.CompleteAsync(new LlmCompletionRequest
            {
                AgentRoleId = "X",
                UserPayload = new string('u', 4000)
            });

            Assert.AreEqual(1, usage.Messages.Count);
            Assert.AreEqual(10, usage.Messages[0].PromptTokens, "Server-reported usage must win over the estimate");
            Assert.AreEqual(15, usage.Messages[0].TotalTokens);
        }

        [Test]
        public async Task CompleteAsync_LlmOperationTimeoutException_PublishesTimeout()
        {
            ThrowingLlm inner = new(new LlmOperationTimeoutException());
            FakeRegistry registry = new(inner);
            CapturingPublisher<LlmRequestCompleted> completed = new();
            RoutingLlmClient routing = new(registry, null, null, completed, null);

            try
            {
                await routing.CompleteAsync(new LlmCompletionRequest { AgentRoleId = "X", UserPayload = "y" });
                Assert.Fail("expected throw");
            }
            catch (LlmOperationTimeoutException)
            {
            }

            Assert.AreEqual(1, completed.Messages.Count);
            Assert.IsFalse(completed.Messages[0].Success);
            Assert.AreEqual(LlmErrorCode.Timeout, completed.Messages[0].ErrorCode);
        }

        [Test]
        public async Task CompleteAsync_OperationCanceledException_PublishesCancelled()
        {
            using CancellationTokenSource cts = new();
            cts.Cancel();
            ThrowingLlm inner = new(new OperationCanceledException(cts.Token));
            FakeRegistry registry = new(inner);
            CapturingPublisher<LlmRequestCompleted> completed = new();
            RoutingLlmClient routing = new(registry, null, null, completed, null);

            try
            {
                await routing.CompleteAsync(new LlmCompletionRequest { AgentRoleId = "X", UserPayload = "y" });
                Assert.Fail("expected throw");
            }
            catch (OperationCanceledException)
            {
            }

            Assert.AreEqual(1, completed.Messages.Count);
            Assert.AreEqual(LlmErrorCode.Cancelled, completed.Messages[0].ErrorCode);
        }

        [Test]
        public async Task CompleteAsync_TimeoutExceptionAfterCallerCancelled_PublishesCancelled()
        {
            using CancellationTokenSource caller = new();
            caller.Cancel();
            ThrowingLlm inner = new(new LlmOperationTimeoutException());
            CapturingPublisher<LlmRequestCompleted> completed = new();
            RoutingLlmClient routing = new(new FakeRegistry(inner), null, null, completed, null);

            try
            {
                await routing.CompleteAsync(new LlmCompletionRequest { AgentRoleId = "X", UserPayload = "y" },
                    caller.Token);
                Assert.Fail("expected throw");
            }
            catch (OperationCanceledException)
            {
            }

            Assert.AreEqual(1, completed.Messages.Count);
            Assert.AreEqual(LlmErrorCode.Cancelled, completed.Messages[0].ErrorCode,
                "The caller stopped the request; a timer that raced the stop does not make it a timeout.");
        }

        [Test]
        public async Task CompleteAsync_TimeoutResultAfterCallerCancelled_ReportsCancelled()
        {
            using CancellationTokenSource caller = new();
            caller.Cancel();
            FixedResultLlm inner = new(new LlmCompletionResult
            {
                Ok = false,
                Error = "timed out",
                ErrorCode = LlmErrorCode.Timeout
            });
            CapturingPublisher<LlmRequestCompleted> completed = new();
            RoutingLlmClient routing = new(new FakeRegistry(inner), null, null, completed, null);

            LlmCompletionResult result = await routing.CompleteAsync(
                new LlmCompletionRequest { AgentRoleId = "X", UserPayload = "y" }, caller.Token);

            Assert.AreEqual(LlmErrorCode.Cancelled, result.ErrorCode);
            Assert.AreEqual(LlmErrorCode.Cancelled, completed.Messages[0].ErrorCode);
        }

        [Test]
        public async Task CompleteAsync_TimeoutResultWithLiveCaller_StaysTimeout()
        {
            FixedResultLlm inner = new(new LlmCompletionResult
            {
                Ok = false,
                Error = "timed out",
                ErrorCode = LlmErrorCode.Timeout
            });
            CapturingPublisher<LlmRequestCompleted> completed = new();
            RoutingLlmClient routing = new(new FakeRegistry(inner), null, null, completed, null);

            LlmCompletionResult result = await routing.CompleteAsync(
                new LlmCompletionRequest { AgentRoleId = "X", UserPayload = "y" });

            Assert.AreEqual(LlmErrorCode.Timeout, result.ErrorCode);
            Assert.AreEqual(LlmErrorCode.Timeout, completed.Messages[0].ErrorCode);
        }

        [Test]
        public async Task Streaming_TimeoutChunkAfterCallerCancelled_IsReportedAsCancelled()
        {
            using CancellationTokenSource caller = new();
            caller.Cancel();
            FixedResultLlm inner = new(null, new LlmStreamChunk
            {
                IsDone = true,
                Error = "LLM request timed out.",
                ErrorCode = LlmErrorCode.Timeout
            });
            CapturingPublisher<LlmRequestCompleted> completed = new();
            RoutingLlmClient routing = new(new FakeRegistry(inner), null, null, completed, null);

            List<LlmStreamChunk> chunks = new();
            await foreach (LlmStreamChunk chunk in routing.CompleteStreamingAsync(
                               new LlmCompletionRequest { AgentRoleId = "X", UserPayload = "y" }, caller.Token))
            {
                chunks.Add(chunk);
            }

            Assert.AreEqual(1, chunks.Count);
            Assert.AreEqual(LlmErrorCode.Cancelled, chunks[0].ErrorCode);
            Assert.AreEqual(LlmErrorCode.Cancelled, completed.Messages[0].ErrorCode);
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task ThrownFaultAfterCallerCancelled_PublishesCancelled_AndDoesNotDegradeRouteHealth(bool streaming)
        {
            // WHY: a typed transient fault (a 503 the teardown provoked) after the caller cancelled used to be
            // published under its own code and marked the endpoint degraded on a request nobody was waiting for.
            using CancellationTokenSource caller = new();
            caller.Cancel();
            LlmClientException fault = new("HTTP 503", LlmErrorCode.BackendUnavailable, 503);
            FakeRegistry registry = new(new ThrowingLlm(fault));
            registry.Register("X", new ThrowingLlm(fault));
            CapturingPublisher<LlmRequestCompleted> completed = new();
            RoutingLlmClient routing = new(registry, null, null, completed, null);

            Exception thrown = await CaptureAsync(() => Run(routing, streaming, caller.Token));

            Assert.AreSame(fault, thrown, "The routing client publishes and rethrows unchanged.");
            Assert.AreEqual(1, completed.Messages.Count);
            Assert.AreEqual(LlmErrorCode.Cancelled, completed.Messages[0].ErrorCode);
            Assert.AreEqual(LlmCancellation.CancelledErrorText, completed.Messages[0].Error);
            Assert.IsEmpty(registry.HealthReports, "A transient fault after the stop says nothing about the endpoint.");
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task UntypedFaultAfterCallerCancelled_PublishesCancelled(bool streaming)
        {
            using CancellationTokenSource caller = new();
            caller.Cancel();
            FakeRegistry registry = new(new ThrowingLlm(new InvalidOperationException("socket disposed")));
            CapturingPublisher<LlmRequestCompleted> completed = new();
            RoutingLlmClient routing = new(registry, null, null, completed, null);

            await CaptureAsync(() => Run(routing, streaming, caller.Token));

            Assert.AreEqual(LlmErrorCode.Cancelled, completed.Messages[0].ErrorCode);
            Assert.IsEmpty(registry.HealthReports);
        }

        [TestCase(LlmErrorCode.AuthExpired, 401, true)]
        [TestCase(LlmErrorCode.PaymentRequired, 402, true)]
        [TestCase(LlmErrorCode.AuthExpired, 401, false)]
        [TestCase(LlmErrorCode.PaymentRequired, 402, false)]
        public async Task PermanentRefusalWrappedInCancellation_StillDegradesRouteHealth(
            LlmErrorCode refusalCode, int status, bool streaming)
        {
            // WHY: FallbackLlmClientDecorator turns any post-cancel fault into OperationCanceledException with the
            // fault attached. The caller gets the cancellation; the endpoint still has an expired key or an
            // exhausted balance, and the next request would be refused the same way - health must learn it.
            using CancellationTokenSource caller = new();
            caller.Cancel();
            LlmClientException refusal = new($"HTTP {status}", refusalCode, status);
            OperationCanceledException wrapped = LlmCancellation.WrapAsCancellation(refusal, caller.Token, "the primary");
            FakeRegistry registry = new(new ThrowingLlm(wrapped));
            registry.Register("X", new ThrowingLlm(wrapped));
            CapturingPublisher<LlmRequestCompleted> completed = new();
            RoutingLlmClient routing = new(registry, null, null, completed, null);

            Exception thrown = await CaptureAsync(() => Run(routing, streaming, caller.Token));

            Assert.AreSame(wrapped, thrown);
            Assert.AreEqual(LlmErrorCode.Cancelled, completed.Messages[0].ErrorCode, "The caller sees the stop.");
            Assert.AreEqual(1, registry.HealthReports.Count);
            Assert.AreEqual("XProfile", registry.HealthReports[0].ProfileId);
            Assert.AreEqual(refusalCode, registry.HealthReports[0].Code, "Endpoint health sees the refusal.");
        }

        [Test]
        public async Task TransientFaultWrappedInCancellation_DoesNotDegradeRouteHealth()
        {
            using CancellationTokenSource caller = new();
            caller.Cancel();
            OperationCanceledException wrapped = LlmCancellation.WrapAsCancellation(
                new LlmClientException("HTTP 503", LlmErrorCode.BackendUnavailable, 503), caller.Token, "the primary");
            FakeRegistry registry = new(new ThrowingLlm(wrapped));
            registry.Register("X", new ThrowingLlm(wrapped));
            RoutingLlmClient routing = new(registry, null, null, null, null);

            await CaptureAsync(() => Run(routing, false, caller.Token));

            Assert.IsEmpty(registry.HealthReports);
        }

        [TestCase(LlmErrorCode.PaymentRequired, 402)]
        [TestCase(LlmErrorCode.AuthExpired, 401)]
        [TestCase(LlmErrorCode.BackendUnavailable, 503)]
        public async Task EndpointLevelFailureWithLiveCaller_DegradesRouteHealth(LlmErrorCode code, int status)
        {
            LlmClientException fault = new($"HTTP {status}", code, status);
            FakeRegistry registry = new(new ThrowingLlm(fault));
            registry.Register("X", new ThrowingLlm(fault));
            CapturingPublisher<LlmRequestCompleted> completed = new();
            RoutingLlmClient routing = new(registry, null, null, completed, null);

            await CaptureAsync(() => Run(routing, false, CancellationToken.None));

            Assert.AreEqual(code, completed.Messages[0].ErrorCode);
            Assert.AreEqual(1, registry.HealthReports.Count);
            Assert.AreEqual(code, registry.HealthReports[0].Code);
        }

        [Test]
        public async Task TimeoutResultAfterCallerCancelled_IsReissuedAsACopy_TheInnerInstanceIsUntouched()
        {
            // WHY: inner clients may reuse result instances; the rewrite must not mutate what they handed out,
            // and the Error text must agree with the rewritten code.
            using CancellationTokenSource caller = new();
            caller.Cancel();
            LlmCompletionResult inner = new() { Ok = false, Error = "timed out", ErrorCode = LlmErrorCode.Timeout, HttpStatus = 504 };
            RoutingLlmClient routing = new(new FakeRegistry(new FixedResultLlm(inner)), null, null, null, null);

            LlmCompletionResult result = await routing.CompleteAsync(
                new LlmCompletionRequest { AgentRoleId = "X", UserPayload = "y" }, caller.Token);

            Assert.AreNotSame(inner, result);
            Assert.AreEqual(LlmErrorCode.Timeout, inner.ErrorCode);
            Assert.AreEqual("timed out", inner.Error);
            Assert.AreEqual(LlmErrorCode.Cancelled, result.ErrorCode);
            Assert.AreEqual(LlmCancellation.CancelledErrorText, result.Error);
            Assert.AreEqual(504, result.HttpStatus, "Everything else is carried over.");
        }

        [Test]
        public async Task TimeoutChunkAfterCallerCancelled_IsReissuedAsACopy_TheInnerInstanceIsUntouched()
        {
            using CancellationTokenSource caller = new();
            caller.Cancel();
            LlmStreamChunk inner = new() { IsDone = true, Error = "LLM request timed out.", ErrorCode = LlmErrorCode.Timeout, Model = "m" };
            RoutingLlmClient routing = new(new FakeRegistry(new FixedResultLlm(null, inner)), null, null, null, null);

            List<LlmStreamChunk> chunks = new();
            await foreach (LlmStreamChunk chunk in routing.CompleteStreamingAsync(
                               new LlmCompletionRequest { AgentRoleId = "X", UserPayload = "y" }, caller.Token))
            {
                chunks.Add(chunk);
            }

            Assert.AreEqual(1, chunks.Count);
            Assert.AreNotSame(inner, chunks[0]);
            Assert.AreEqual(LlmErrorCode.Timeout, inner.ErrorCode);
            Assert.AreEqual("LLM request timed out.", inner.Error);
            Assert.AreEqual(LlmErrorCode.Cancelled, chunks[0].ErrorCode);
            Assert.AreEqual(LlmCancellation.CancelledErrorText, chunks[0].Error);
            Assert.AreEqual("m", chunks[0].Model, "Everything else is carried over.");
        }

        [Test]
        public async Task FailureChunkWithLiveCaller_IsPassedThroughUnchanged()
        {
            LlmStreamChunk inner = new() { IsDone = true, Error = "HTTP 503", ErrorCode = LlmErrorCode.BackendUnavailable };
            FakeRegistry registry = new(new FixedResultLlm(null, inner));
            registry.Register("X", new FixedResultLlm(null, inner));
            RoutingLlmClient routing = new(registry, null, null, null, null);

            List<LlmStreamChunk> chunks = new();
            await foreach (LlmStreamChunk chunk in routing.CompleteStreamingAsync(
                               new LlmCompletionRequest { AgentRoleId = "X", UserPayload = "y" }))
            {
                chunks.Add(chunk);
            }

            Assert.AreSame(inner, chunks[0], "No rewrite, no copy.");
            Assert.AreEqual(1, registry.HealthReports.Count);
            Assert.AreEqual(LlmErrorCode.BackendUnavailable, registry.HealthReports[0].Code);
        }

        [Test]
        public async Task TransientFailureChunkAfterCallerCancelled_DoesNotDegradeRouteHealth()
        {
            using CancellationTokenSource caller = new();
            caller.Cancel();
            LlmStreamChunk inner = new() { IsDone = true, Error = "HTTP 503", ErrorCode = LlmErrorCode.BackendUnavailable };
            FakeRegistry registry = new(new FixedResultLlm(null, inner));
            registry.Register("X", new FixedResultLlm(null, inner));
            RoutingLlmClient routing = new(registry, null, null, null, null);

            await foreach (LlmStreamChunk _ in routing.CompleteStreamingAsync(
                               new LlmCompletionRequest { AgentRoleId = "X", UserPayload = "y" }, caller.Token))
            {
            }

            Assert.IsEmpty(registry.HealthReports);
        }

        [TestCase(LlmErrorCode.ProviderError, false)]
        [TestCase(LlmErrorCode.BackendUnavailable, false)]
        [TestCase(LlmErrorCode.AuthExpired, false)]
        [TestCase(LlmErrorCode.ProviderError, true)]
        [TestCase(LlmErrorCode.BackendUnavailable, true)]
        [TestCase(LlmErrorCode.AuthExpired, true)]
        public async Task FailureReturnedAfterCallerCancelled_IsPublishedAsCancelled_RouteHealthJudgesTheRawCode(
            LlmErrorCode code,
            bool streaming)
        {
            // WHY: a THROWN fault after the stop was already published as the cancellation, a RETURNED one kept
            // its own code - the same Stop reached subscribers two ways. Route health still reads the code the
            // endpoint reported: a permanent refusal is a fact about the endpoint, a transient one is not.
            using CancellationTokenSource caller = new();
            caller.Cancel();
            LlmCompletionResult innerResult = new() { Ok = false, Error = "HTTP refusal", ErrorCode = code, HttpStatus = 499 };
            LlmStreamChunk innerChunk = new() { IsDone = true, Error = "HTTP refusal", ErrorCode = code, HttpStatus = 499 };
            FixedResultLlm inner = new(innerResult, innerChunk);
            FakeRegistry registry = new(inner);
            registry.Register("X", inner);
            CapturingPublisher<LlmRequestCompleted> completed = new();
            RoutingLlmClient routing = new(registry, null, null, completed, null);
            LlmCompletionRequest request = new() { AgentRoleId = "X", UserPayload = "y" };

            LlmErrorCode seenCode;
            string seenError;
            int? seenStatus;
            if (streaming)
            {
                List<LlmStreamChunk> chunks = new();
                await foreach (LlmStreamChunk chunk in routing.CompleteStreamingAsync(request, caller.Token))
                {
                    chunks.Add(chunk);
                }

                Assert.AreEqual(1, chunks.Count);
                seenCode = chunks[0].ErrorCode;
                seenError = chunks[0].Error;
                seenStatus = chunks[0].HttpStatus;
            }
            else
            {
                LlmCompletionResult result = await routing.CompleteAsync(request, caller.Token);
                seenCode = result.ErrorCode;
                seenError = result.Error;
                seenStatus = result.HttpStatus;
            }

            Assert.AreEqual(LlmErrorCode.Cancelled, seenCode, "The caller sees the stop.");
            Assert.AreEqual(LlmCancellation.CancelledErrorText, seenError, "The text follows the code.");
            Assert.AreEqual(499, seenStatus, "Everything else is carried over.");
            Assert.AreEqual(code, innerResult.ErrorCode, "The inner instances are not mutated.");
            Assert.AreEqual(code, innerChunk.ErrorCode);
            Assert.AreEqual(1, completed.Messages.Count);
            Assert.AreEqual(LlmErrorCode.Cancelled, completed.Messages[0].ErrorCode);
            Assert.AreEqual(LlmCancellation.CancelledErrorText, completed.Messages[0].Error);
            if (code == LlmErrorCode.AuthExpired)
            {
                Assert.AreEqual(1, registry.HealthReports.Count, "A permanent refusal still reaches route health.");
                Assert.AreEqual("XProfile", registry.HealthReports[0].ProfileId);
                Assert.AreEqual(LlmErrorCode.AuthExpired, registry.HealthReports[0].Code);
            }
            else
            {
                Assert.IsEmpty(registry.HealthReports,
                    "A transient failure after the stop says nothing about the endpoint.");
            }
        }

        [Test]
        public async Task CodelessErrorChunkAfterCallerCancelled_IsPublishedAsCancelled()
        {
            using CancellationTokenSource caller = new();
            caller.Cancel();
            LlmStreamChunk inner = new() { IsDone = true, Error = "socket closed" };
            FakeRegistry registry = new(new FixedResultLlm(null, inner));
            CapturingPublisher<LlmRequestCompleted> completed = new();
            RoutingLlmClient routing = new(registry, null, null, completed, null);

            List<LlmStreamChunk> chunks = new();
            await foreach (LlmStreamChunk chunk in routing.CompleteStreamingAsync(
                               new LlmCompletionRequest { AgentRoleId = "X", UserPayload = "y" }, caller.Token))
            {
                chunks.Add(chunk);
            }

            Assert.AreEqual(1, chunks.Count);
            Assert.AreEqual(LlmErrorCode.Cancelled, chunks[0].ErrorCode);
            Assert.AreEqual(LlmCancellation.CancelledErrorText, chunks[0].Error);
            Assert.AreEqual(LlmErrorCode.Cancelled, completed.Messages[0].ErrorCode);
            Assert.IsEmpty(registry.HealthReports);
        }

        private static async Task Run(RoutingLlmClient routing, bool streaming, CancellationToken token)
        {
            LlmCompletionRequest request = new() { AgentRoleId = "X", UserPayload = "y" };
            if (streaming)
            {
                await foreach (LlmStreamChunk _ in routing.CompleteStreamingAsync(request, token))
                {
                }

                return;
            }

            await routing.CompleteAsync(request, token);
        }

        private static async Task<Exception> CaptureAsync(Func<Task> action)
        {
            // WHY not Assert.ThrowsAsync: it blocks the calling thread, which deadlocks under Unity's
            // SynchronizationContext while the awaited delegate's continuation waits for that same thread.
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                return ex;
            }

            Assert.Fail("Expected an exception.");
            return null;
        }

        /// <summary>Returns one fixed result, and streams one fixed chunk, ignoring the token on purpose.</summary>
        private sealed class FixedResultLlm : ILlmClient
        {
            private readonly LlmCompletionResult _result;
            private readonly LlmStreamChunk _chunk;

            public FixedResultLlm(LlmCompletionResult result, LlmStreamChunk chunk = null)
            {
                _result = result;
                _chunk = chunk;
            }

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(_result);
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.Yield();
                yield return _chunk;
            }
        }

        [Test]
        public async Task Streaming_RoutesToInnerClient_ForRole()
        {
            // Invariant for issue 2: with no streaming override, the interface's
            // default implementation falls back to CompleteAsync and glues the whole
            // answer into one chunk, so streaming is never visible in the UI. This test
            // pins that RoutingLlmClient really takes the streaming path.
            StreamingMockLlm fastClient = new("Hel", "lo");
            StreamingMockLlm defaultClient = new("De", "fault");

            FakeRegistry registry = new(defaultClient);
            registry.Register("FastRole", fastClient);

            RoutingLlmClient routing = new(registry);

            List<LlmStreamChunk> chunks = new();
            await foreach (LlmStreamChunk chunk in routing.CompleteStreamingAsync(
                               new LlmCompletionRequest { AgentRoleId = "FastRole", UserPayload = "hi" }))
            {
                chunks.Add(chunk);
            }

            Assert.AreEqual(1, fastClient.StreamingCalls, "The router must call streaming on the inner client");
            Assert.AreEqual(0, fastClient.CompleteAsyncCalls, "CompleteAsync must not be called");
            Assert.AreEqual(0, defaultClient.StreamingCalls, "The fallback client must not be involved");

            // 2 text chunks + 1 terminal
            Assert.AreEqual(3, chunks.Count);
            Assert.AreEqual("Hel", chunks[0].Text);
            Assert.AreEqual("lo", chunks[1].Text);
            Assert.IsTrue(chunks[2].IsDone);
        }

        [Test]
        public async Task Streaming_UsesFallbackClient_ForUnknownRole()
        {
            StreamingMockLlm fallback = new("A", "B", "C");
            FakeRegistry registry = new(fallback);

            RoutingLlmClient routing = new(registry);

            List<LlmStreamChunk> chunks = new();
            await foreach (LlmStreamChunk chunk in routing.CompleteStreamingAsync(
                               new LlmCompletionRequest { AgentRoleId = "UnknownRole", UserPayload = "hi" }))
            {
                chunks.Add(chunk);
            }

            Assert.AreEqual(1, fallback.StreamingCalls);
            Assert.AreEqual(4, chunks.Count, "3 text chunks + 1 terminal");
        }

        [Test]
        public async Task Streaming_NullRequest_YieldsErrorChunk()
        {
            StreamingMockLlm fallback = new("ignored");
            FakeRegistry registry = new(fallback);
            RoutingLlmClient routing = new(registry);

            List<LlmStreamChunk> chunks = new();
            await foreach (LlmStreamChunk chunk in routing.CompleteStreamingAsync(null))
            {
                chunks.Add(chunk);
            }

            Assert.AreEqual(1, chunks.Count);
            Assert.IsTrue(chunks[0].IsDone);
            StringAssert.Contains("null", chunks[0].Error);
            Assert.AreEqual(0, fallback.StreamingCalls, "A null request must not reach the inner client");
        }

        [Test]
        public async Task CompleteAsync_PublishesRoutingEvents()
        {
            StreamingMockLlm roleClient = new("ok");
            FakeRegistry registry = new(new StreamingMockLlm("fallback"));
            registry.Register("Merchant", roleClient);
            CapturingPublisher<LlmBackendSelected> selected = new();
            CapturingPublisher<LlmRequestStarted> started = new();
            CapturingPublisher<LlmRequestCompleted> completed = new();
            CapturingPublisher<LlmUsageReported> usage = new();
            RoutingLlmClient routing = new(registry, selected, started, completed, usage);

            LlmCompletionRequest request = new()
            {
                AgentRoleId = "Merchant",
                TraceId = "trace-1",
                UserPayload = "hello"
            };

            LlmCompletionResult result = await routing.CompleteAsync(request);

            Assert.IsTrue(result.Ok);
            Assert.AreEqual("MerchantProfile", request.RoutingProfileId);
            Assert.AreEqual(4096, request.ContextWindowTokens);
            Assert.AreEqual(1, selected.Messages.Count);
            Assert.AreEqual(1, started.Messages.Count);
            Assert.AreEqual(1, completed.Messages.Count);
            Assert.AreEqual(1, usage.Messages.Count);
            Assert.AreEqual(LlmExecutionMode.ClientOwnedApi, selected.Messages[0].ExecutionMode);
            Assert.IsTrue(completed.Messages[0].Success);
            Assert.AreEqual(15, usage.Messages[0].TotalTokens);
            Assert.AreEqual(8, usage.Messages[0].CacheReadTokens);
            Assert.AreEqual(4, usage.Messages[0].CacheWriteTokens);
            Assert.AreEqual("test-model", usage.Messages[0].Model);
        }
    }
}
