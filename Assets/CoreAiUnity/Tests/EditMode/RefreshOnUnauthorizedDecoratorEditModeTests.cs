#if COREAI_LLM
using System;
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
            ServerManagedAuthorization.ClearRequestHeaderProvider();
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
        public async Task CompleteAsync_HeaderScopeSpansAuthRetry_ButNotLaterInvocationWithSameRequest()
        {
            ScopeAwareAuthClient inner = new("lesson-a");
            ServerManagedAuthorization.SetRefresher(new RotatingLessonRefresher(inner, "lesson-b"));
            ILlmClient sut = new RefreshOnUnauthorizedDecorator(inner);
            LlmCompletionRequest request = new() { AgentRoleId = "Teacher" };

            LlmCompletionResult first = await sut.CompleteAsync(request, CancellationToken.None);

            Assert.IsTrue(first.Ok);
            CollectionAssert.AreEqual(new[] { "lesson-a", "lesson-a" }, inner.CompleteLessons);
            Assert.AreEqual(1, inner.ScopeCount);

            inner.CurrentLesson = "lesson-c";
            LlmCompletionResult second = await sut.CompleteAsync(request, CancellationToken.None);

            Assert.IsTrue(second.Ok);
            CollectionAssert.AreEqual(new[] { "lesson-a", "lesson-a", "lesson-c" }, inner.CompleteLessons);
            Assert.AreEqual(2, inner.ScopeCount,
                "A later invocation must take a fresh snapshot even when it reuses the request object.");
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

        [Test]
        public async Task CompleteStreamingAsync_HeaderScopeSpansAuthRetry_ButNotLaterInvocationWithSameRequest()
        {
            ScopeAwareAuthClient inner = new("lesson-a");
            ServerManagedAuthorization.SetRefresher(new RotatingLessonRefresher(inner, "lesson-b"));
            ILlmClient sut = new RefreshOnUnauthorizedDecorator(inner);
            LlmCompletionRequest request = new() { AgentRoleId = "Teacher" };

            await foreach (LlmStreamChunk _ in sut.CompleteStreamingAsync(request, CancellationToken.None))
            {
            }

            CollectionAssert.AreEqual(new[] { "lesson-a", "lesson-a" }, inner.StreamLessons);
            Assert.AreEqual(1, inner.ScopeCount);

            inner.CurrentLesson = "lesson-c";
            await foreach (LlmStreamChunk _ in sut.CompleteStreamingAsync(request, CancellationToken.None))
            {
            }

            CollectionAssert.AreEqual(new[] { "lesson-a", "lesson-a", "lesson-c" }, inner.StreamLessons);
            Assert.AreEqual(2, inner.ScopeCount);
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

        private sealed class RotatingLessonRefresher : IServerManagedAuthRefresher
        {
            private readonly ScopeAwareAuthClient _client;
            private readonly string _nextLesson;

            public RotatingLessonRefresher(ScopeAwareAuthClient client, string nextLesson)
            {
                _client = client;
                _nextLesson = nextLesson;
            }

            public Task<bool> RefreshAsync(CancellationToken cancellationToken = default)
            {
                _client.CurrentLesson = _nextLesson;
                return Task.FromResult(true);
            }
        }

        private sealed class ScopeAwareAuthClient : ILlmClient, ILlmRequestHeaderScope
        {
            private string _activeLesson;
            private int _depth;
            private int _completeCalls;
            private int _streamCalls;

            public ScopeAwareAuthClient(string currentLesson)
            {
                CurrentLesson = currentLesson;
            }

            public string CurrentLesson { get; set; }

            public int ScopeCount { get; private set; }

            public List<string> CompleteLessons { get; } = new();

            public List<string> StreamLessons { get; } = new();

            public IDisposable BeginRequestHeaders(LlmCompletionRequest request)
            {
                string previous = _activeLesson;
                if (_depth == 0)
                {
                    _activeLesson = CurrentLesson;
                    ScopeCount++;
                }

                _depth++;
                return new Scope(this, previous);
            }

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                CompleteLessons.Add(_activeLesson);
                _completeCalls++;
                return Task.FromResult(_completeCalls == 1
                    ? new LlmCompletionResult { Ok = false, ErrorCode = LlmErrorCode.AuthExpired }
                    : new LlmCompletionResult { Ok = true, Content = "ok" });
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                StreamLessons.Add(_activeLesson);
                _streamCalls++;
                await Task.Yield();
                if (_streamCalls == 1)
                {
                    yield return new LlmStreamChunk
                    {
                        IsDone = true,
                        Error = "401",
                        ErrorCode = LlmErrorCode.AuthExpired
                    };
                    yield break;
                }

                yield return new LlmStreamChunk { Text = "ok" };
            }

            private sealed class Scope : IDisposable
            {
                private readonly ScopeAwareAuthClient _owner;
                private readonly string _previous;

                public Scope(ScopeAwareAuthClient owner, string previous)
                {
                    _owner = owner;
                    _previous = previous;
                }

                public void Dispose()
                {
                    _owner._depth--;
                    _owner._activeLesson = _previous;
                }
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
