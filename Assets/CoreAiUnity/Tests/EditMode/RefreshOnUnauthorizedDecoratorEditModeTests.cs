#if !COREAI_NO_LLM
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// <see cref="RefreshOnUnauthorizedDecorator"/> — retry after refresh and idempotent request objects.
    /// </summary>
    public sealed class RefreshOnUnauthorizedDecoratorEditModeTests
    {
        [TearDown]
        public void TearDown()
        {
            ServerManagedAuthorization.ClearProvider();
        }

        [Test]
        public async Task CompleteAsync_AuthExpiredThenRefresh_OK_SecondAttemptSucceeds()
        {
            ServerManagedAuthorization.SetRefresher(new AlwaysOkRefresher());
            AuthExpiredThenOkClient inner = new();
            ILlmClient sut = new RefreshOnUnauthorizedDecorator(inner);

            LlmCompletionRequest request = new()
            {
                AgentRoleId = "X",
                SystemPrompt = "s",
                UserPayload = "u"
            };

            LlmCompletionResult result = await sut.CompleteAsync(request, CancellationToken.None);

            Assert.IsTrue(result.Ok);
            Assert.AreEqual("after-refresh", result.Content);
            Assert.AreEqual(2, inner.CompleteCalls);
        }

        [Test]
        public async Task CompleteStreamingAsync_HealthyStream_ForwardsEveryChunkExactlyOnce()
        {
            ScriptedStreamingClient inner = new("alpha", "beta", "gamma");
            ILlmClient sut = new RefreshOnUnauthorizedDecorator(inner);

            List<string> seen = new();
            await foreach (LlmStreamChunk chunk in sut.CompleteStreamingAsync(
                               new LlmCompletionRequest { AgentRoleId = "X" }, CancellationToken.None))
            {
                seen.Add(chunk.Text);
            }

            CollectionAssert.AreEqual(new[] { "alpha", "beta", "gamma" }, seen);
        }

        [Test]
        public async Task CompleteStreamingAsync_EmptyStream_YieldsNothing()
        {
            ScriptedStreamingClient inner = new();
            ILlmClient sut = new RefreshOnUnauthorizedDecorator(inner);

            List<LlmStreamChunk> seen = new();
            await foreach (LlmStreamChunk chunk in sut.CompleteStreamingAsync(
                               new LlmCompletionRequest { AgentRoleId = "X" }, CancellationToken.None))
            {
                seen.Add(chunk);
            }

            Assert.IsEmpty(seen);
        }

        private sealed class ScriptedStreamingClient : ILlmClient
        {
            private readonly string[] _texts;

            public ScriptedStreamingClient(params string[] texts)
            {
                _texts = texts;
            }

            public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = "" });
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                foreach (string text in _texts)
                {
                    await Task.Yield();
                    yield return new LlmStreamChunk { Text = text };
                }
            }
        }

        private sealed class AlwaysOkRefresher : IServerManagedAuthRefresher
        {
            public Task<bool> RefreshAsync(CancellationToken cancellationToken = default)
            {
                return Task.FromResult(true);
            }
        }

        private sealed class AuthExpiredThenOkClient : ILlmClient
        {
            public int CompleteCalls { get; private set; }

            public void SetTools(IReadOnlyList<ILlmTool> tools)
            {
            }

            public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                CompleteCalls++;
                if (CompleteCalls == 1)
                {
                    return Task.FromResult(new LlmCompletionResult
                    {
                        Ok = false,
                        Error = "401",
                        ErrorCode = LlmErrorCode.AuthExpired
                    });
                }

                return Task.FromResult(new LlmCompletionResult
                {
                    Ok = true,
                    Content = "after-refresh"
                });
            }
        }
    }
}
#endif
