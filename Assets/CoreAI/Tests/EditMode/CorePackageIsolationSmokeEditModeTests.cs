#if COREAI_LLM
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode
{
    /// <summary>
    /// Package-local smoke tests for <c>com.neoxider.coreai</c> (the portable <c>CoreAI.Core</c> assembly),
    /// proving the core package compiles and tests in a STANDALONE UPM graph — this assembly references
    /// only <c>CoreAI.Core</c> (no <c>CoreAI.Source</c>/Unity host), so a regression that leaks a Unity or
    /// cross-package dependency into the core surface fails here rather than only in the monorepo. Also
    /// exercises the portable-core resilience decorators (F-22 package isolation gate).
    /// </summary>
    public sealed class CorePackageIsolationSmokeEditModeTests
    {
        [Test]
        public void CoreResultTypes_HaveExpectedDefaults()
        {
            LlmCompletionResult ok = new() { Ok = true, Content = "hi" };
            Assert.IsTrue(ok.Ok);
            Assert.AreEqual(LlmErrorCode.None, ok.ErrorCode);

            LlmStreamChunk chunk = new();
            Assert.IsFalse(chunk.IsDone);
            Assert.AreEqual(LlmErrorCode.None, chunk.ErrorCode);
        }

        [Test]
        public async Task TimeoutDecorator_Disabled_DelegatesThroughCorePackage()
        {
            EchoClient inner = new();
            TimeoutLlmClientDecorator sut = new(inner, () => 0f);

            LlmCompletionResult result = await sut.CompleteAsync(new LlmCompletionRequest { UserPayload = "x" });

            Assert.IsTrue(result.Ok);
            Assert.AreEqual("echo", result.Content);
        }

        [Test]
        public async Task RetryingStreamingDecorator_NonStreaming_DelegatesThroughCorePackage()
        {
            EchoClient inner = new();
            RetryingStreamingLlmClientDecorator sut = new(inner, 2);

            LlmCompletionResult result = await sut.CompleteAsync(new LlmCompletionRequest { UserPayload = "x" });

            Assert.IsTrue(result.Ok);
            Assert.AreEqual(1, inner.CompleteCalls);
        }

        private sealed class EchoClient : ILlmClient
        {
            public int CompleteCalls;

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request, CancellationToken cancellationToken = default)
            {
                CompleteCalls++;
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = "echo" });
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request,
                [EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                await Task.Yield();
                yield return new LlmStreamChunk { Text = "echo", IsDone = true };
            }
        }
    }
}
#endif
