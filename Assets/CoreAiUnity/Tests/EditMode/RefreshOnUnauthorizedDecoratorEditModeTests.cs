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