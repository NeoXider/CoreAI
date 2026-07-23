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
        public async Task Streaming_RoutesToInnerClient_ForRole()
        {
            // Инвариант issue 2: если streaming override'а нет — default-реализация
            // интерфейса делает fallback к CompleteAsync и склеивает весь ответ
            // в один chunk, и стриминг не виден в UI. Этот тест проверяет что
            // RoutingLlmClient использует именно стриминговый путь.
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

            Assert.AreEqual(1, fastClient.StreamingCalls, "Роутер должен вызвать стриминг внутреннего клиента");
            Assert.AreEqual(0, fastClient.CompleteAsyncCalls, "Не должно быть вызова CompleteAsync");
            Assert.AreEqual(0, defaultClient.StreamingCalls, "Fallback клиент не должен быть задействован");

            // 2 текстовых + 1 терминальный
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
            Assert.AreEqual(4, chunks.Count, "3 текстовых + 1 терминальный");
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
            Assert.AreEqual(0, fallback.StreamingCalls, "При null-запросе не должен вызывать внутренний клиент");
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
