using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode
{
    /// <summary>
    /// <see cref="ClientLimitedLlmClientDecorator"/> from the player's point of view: a local session cap
    /// is not "the account quota is exhausted", and a failed attempt does not eat the limit.
    /// </summary>
    public sealed class ClientLimitedLlmClientDecoratorEditModeTests
    {
        private SynchronizationContext _previousSynchronizationContext;

        /// <summary>
        /// WHY this fixture detaches: ConcurrentRequests_CannotSlipPastTheCapTogether is a synchronous
        /// [Test] that ends on Task.Result. Under Unity's SynchronizationContext the decorator's
        /// continuation is posted back to the very thread that Result is blocking, so the whole EditMode
        /// run hangs at this fixture with no results file — the F12 deadlock, reached through a blocking
        /// property instead of a blocking assertion.
        /// </summary>
        [SetUp]
        public void DetachSynchronizationContext()
        {
            _previousSynchronizationContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void RestoreSynchronizationContext()
        {
            SynchronizationContext.SetSynchronizationContext(_previousSynchronizationContext);
        }

        [Test]
        public async Task RequestLimit_IsReportedAsLocalClientLimit_NotAccountQuota()
        {
            ScriptedClient inner = new();
            ClientLimitedLlmClientDecorator limited = new(inner, 1, 0);

            inner.Results.Enqueue(new LlmCompletionResult { Ok = true, Content = "one" });
            await limited.CompleteAsync(Req("one"));
            LlmCompletionResult rejected = await limited.CompleteAsync(Req("two"));

            Assert.IsFalse(rejected.Ok);
            Assert.AreEqual(LlmErrorCode.ClientLimitExceeded, rejected.ErrorCode);
            Assert.AreNotEqual(
                LlmErrorPresentation.ForErrorCode(LlmErrorCode.QuotaExceeded),
                LlmErrorPresentation.ForErrorCode(rejected.ErrorCode),
                "The player must not read about an exhausted ACCOUNT quota when it was the local counter that ran out.");
            Assert.AreEqual(1, inner.Calls);
        }

        [Test]
        public async Task PromptLimit_IsReportedAsLocalClientLimit()
        {
            ScriptedClient inner = new();
            ClientLimitedLlmClientDecorator limited = new(inner, 0, 3);

            LlmCompletionResult rejected = await limited.CompleteAsync(Req("abcd"));

            Assert.IsFalse(rejected.Ok);
            Assert.AreEqual(LlmErrorCode.ClientLimitExceeded, rejected.ErrorCode);
            Assert.AreEqual(0, inner.Calls);
        }

        [Test]
        public async Task FailedResult_DoesNotConsumeTheSessionSlot()
        {
            ScriptedClient inner = new();
            ClientLimitedLlmClientDecorator limited = new(inner, 1, 0);

            inner.Results.Enqueue(new LlmCompletionResult
            {
                Ok = false, Error = "backend down", ErrorCode = LlmErrorCode.BackendUnavailable
            });
            LlmCompletionResult failed = await limited.CompleteAsync(Req("one"));
            inner.Results.Enqueue(new LlmCompletionResult { Ok = true, Content = "answer" });
            LlmCompletionResult retried = await limited.CompleteAsync(Req("one again"));

            Assert.IsFalse(failed.Ok);
            Assert.IsTrue(retried.Ok, "A backend failure must not have consumed the only session slot.");
            Assert.AreEqual(2, inner.Calls);
        }

        [Test]
        public async Task ThrownAndCancelledAttempts_DoNotConsumeTheSessionSlot()
        {
            ScriptedClient inner = new();
            ClientLimitedLlmClientDecorator limited = new(inner, 1, 0);

            inner.Throw = new LlmClientException("boom", LlmErrorCode.ProviderError, 500);
            Assert.IsInstanceOf<LlmClientException>(await CaptureAsync(limited.CompleteAsync(Req("one"))));

            inner.Throw = new OperationCanceledException();
            Assert.IsInstanceOf<OperationCanceledException>(await CaptureAsync(limited.CompleteAsync(Req("two"))));

            inner.Throw = null;
            inner.Results.Enqueue(new LlmCompletionResult { Ok = true, Content = "answer" });
            LlmCompletionResult ok = await limited.CompleteAsync(Req("three"));

            Assert.IsTrue(ok.Ok, "Neither an exception nor a cancellation must have consumed the slot.");
        }

        [Test]
        public async Task Streaming_FailedStream_DoesNotConsumeTheSessionSlot_ButCleanStreamDoes()
        {
            ScriptedClient inner = new();
            ClientLimitedLlmClientDecorator limited = new(inner, 1, 0);

            inner.Streams.Enqueue(new[]
            {
                new LlmStreamChunk { IsDone = true, Error = "down", ErrorCode = LlmErrorCode.BackendUnavailable }
            });
            List<LlmStreamChunk> failed = await Drain(limited.CompleteStreamingAsync(Req("one")));
            Assert.AreEqual(LlmErrorCode.BackendUnavailable, failed[0].ErrorCode);

            inner.Streams.Enqueue(new[] { new LlmStreamChunk { Text = "hi" }, new LlmStreamChunk { IsDone = true } });
            List<LlmStreamChunk> clean = await Drain(limited.CompleteStreamingAsync(Req("two")));
            Assert.IsTrue(clean.Exists(c => c.Text == "hi"), "After a failed stream the slot must be free.");

            List<LlmStreamChunk> rejected = await Drain(limited.CompleteStreamingAsync(Req("three")));
            Assert.AreEqual(1, rejected.Count);
            Assert.AreEqual(LlmErrorCode.ClientLimitExceeded, rejected[0].ErrorCode,
                "A clean stream does take the slot - the third request hits the local cap.");
            Assert.AreEqual(2, inner.StreamCalls);
        }

        [Test]
        public async Task Streaming_EmptyStream_DoesNotConsumeTheSessionSlot()
        {
            ScriptedClient inner = new();
            ClientLimitedLlmClientDecorator limited = new(inner, 1, 0);

            inner.Streams.Enqueue(Array.Empty<LlmStreamChunk>());
            await Drain(limited.CompleteStreamingAsync(Req("one")));

            inner.Streams.Enqueue(new[] { new LlmStreamChunk { Text = "hi", IsDone = true } });
            List<LlmStreamChunk> next = await Drain(limited.CompleteStreamingAsync(Req("two")));

            Assert.IsTrue(next.Exists(c => c.Text == "hi"), "A stream without a single chunk is not an answer; the slot is not consumed.");
        }

        [Test]
        public void ConcurrentRequests_CannotSlipPastTheCapTogether()
        {
            ScriptedClient inner = new() { Hold = new TaskCompletionSource<bool>() };
            ClientLimitedLlmClientDecorator limited = new(inner, 1, 0);

            inner.Results.Enqueue(new LlmCompletionResult { Ok = true, Content = "a" });
            Task<LlmCompletionResult> first = limited.CompleteAsync(Req("one"));
            Task<LlmCompletionResult> second = limited.CompleteAsync(Req("two"));

            Assert.IsTrue(second.IsCompleted, "The second request must be rejected while the first is in flight.");
            Assert.AreEqual(LlmErrorCode.ClientLimitExceeded, second.Result.ErrorCode);
            Assert.AreEqual(1, inner.Calls, "The slot is reserved BEFORE the call, otherwise two callers would slip past the cap together.");

            inner.Hold.SetResult(true);
            Assert.IsTrue(first.Result.Ok);
        }

        [TestCase("text-error")]
        [TestCase("code-error")]
        [TestCase("exception")]
        [TestCase("cancel")]
        [TestCase("dispose-exception")]
        public async Task FailureAfterPartialAnswer_RefundsExactlyOneSlotAndDisposes(string failure)
        {
            ProbeStream stream = new(failure);
            ProbeClient inner = new(stream);
            ClientLimitedLlmClientDecorator limited = new(inner, 1, 0);
            Exception thrown = await CaptureAsync(Drain(limited.CompleteStreamingAsync(Req("partial"))));
            if (failure == "exception") Assert.IsInstanceOf<LlmClientException>(thrown);
            else if (failure == "cancel") Assert.IsInstanceOf<OperationCanceledException>(thrown);
            else if (failure == "dispose-exception") Assert.IsInstanceOf<IOException>(thrown);
            else Assert.IsNull(thrown);
            Assert.AreEqual(1, stream.Disposals);

            LlmCompletionResult retry = await limited.CompleteAsync(Req("retry"));
            LlmCompletionResult overLimit = await limited.CompleteAsync(Req("extra"));
            Assert.IsTrue(retry.Ok, "A failed partial answer must not exhaust the session limit.");
            Assert.AreEqual(LlmErrorCode.ClientLimitExceeded, overLimit.ErrorCode,
                "Refunding twice would grant an extra successful request.");
            Assert.AreEqual(1, inner.Completions);
        }

        [Test]
        public async Task ProviderAndCleanupFailures_PreserveAuthFailureAndRefundExactlyOnce()
        {
            LlmClientException authFailure = new("expired", LlmErrorCode.AuthExpired);
            IOException cleanupFailure = new("cleanup failed");
            ProbeStream stream = new("both") { MoveFailure = authFailure, CleanupFailure = cleanupFailure };
            ProbeClient inner = new(stream);
            ClientLimitedLlmClientDecorator limited = new(inner, 1, 0);

            Exception thrown = await CaptureAsync(Drain(limited.CompleteStreamingAsync(Req("partial"))));

            Assert.AreSame(authFailure, thrown);
            Assert.AreEqual(LlmErrorCode.AuthExpired, ((LlmClientException)thrown).ErrorCode);
            Assert.AreSame(cleanupFailure, thrown.Data[ClientLimitedLlmClientDecorator.StreamDisposeExceptionDataKey]);
            Assert.AreEqual(1, stream.Disposals);
            Assert.IsTrue((await limited.CompleteAsync(Req("retry"))).Ok);
            Assert.AreEqual(LlmErrorCode.ClientLimitExceeded, (await limited.CompleteAsync(Req("extra"))).ErrorCode);
            Assert.AreEqual(1, inner.Completions);
        }

        [Test]
        public async Task ConsumerAbandonsPartialAnswer_StillConsumesTheSlotAndDisposesOnce()
        {
            ProbeStream stream = new("clean");
            ClientLimitedLlmClientDecorator limited = new(new ProbeClient(stream), 1, 0);
            IAsyncEnumerator<LlmStreamChunk> iterator = limited.CompleteStreamingAsync(Req("partial")).GetAsyncEnumerator();
            Assert.IsTrue(await iterator.MoveNextAsync());
            Assert.AreEqual("partial", iterator.Current.Text);
            await iterator.DisposeAsync();
            await iterator.DisposeAsync();
            Assert.AreEqual(1, stream.Disposals);
            LlmCompletionResult rejected = await limited.CompleteAsync(Req("extra"));
            Assert.AreEqual(LlmErrorCode.ClientLimitExceeded, rejected.ErrorCode);
        }

        [Test]
        public async Task CallerCancelsBetweenChunks_RefundsWhenConsumerClosesTheStream()
        {
            ProbeStream stream = new("clean");
            ClientLimitedLlmClientDecorator limited = new(new ProbeClient(stream), 1, 0);
            using CancellationTokenSource cancellation = new();
            IAsyncEnumerator<LlmStreamChunk> iterator = limited.CompleteStreamingAsync(Req("partial"), cancellation.Token)
                .GetAsyncEnumerator(cancellation.Token);
            Assert.IsTrue(await iterator.MoveNextAsync());
            cancellation.Cancel();
            await iterator.DisposeAsync();
            Assert.AreEqual(1, stream.Disposals);
            Assert.IsTrue((await limited.CompleteAsync(Req("retry"))).Ok);
            Assert.AreEqual(LlmErrorCode.ClientLimitExceeded, (await limited.CompleteAsync(Req("extra"))).ErrorCode);
        }

        [Test]
        public async Task StreamContainingOnlyNulls_DoesNotConsumeTheSlot()
        {
            ProbeStream stream = new("nulls");
            ClientLimitedLlmClientDecorator limited = new(new ProbeClient(stream), 1, 0);
            await Drain(limited.CompleteStreamingAsync(Req("empty")));
            Assert.IsTrue((await limited.CompleteAsync(Req("retry"))).Ok);
            Assert.AreEqual(1, stream.Disposals);
        }

        private sealed class ProbeClient : ILlmClient
        {
            private readonly ProbeStream _stream;
            public ProbeClient(ProbeStream stream) { _stream = stream; }
            public int Completions { get; private set; }
            public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request, CancellationToken cancellationToken = default)
            {
                Completions++;
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = "answer" });
            }
            public IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(LlmCompletionRequest request,
                CancellationToken cancellationToken = default) => _stream;
        }

        private sealed class ProbeStream : IAsyncEnumerable<LlmStreamChunk>, IAsyncEnumerator<LlmStreamChunk>
        {
            private readonly string _outcome;
            private int _moves;
            public ProbeStream(string outcome) { _outcome = outcome; }
            public Exception MoveFailure { get; set; }
            public Exception CleanupFailure { get; set; }
            public int Disposals { get; private set; }
            public LlmStreamChunk Current { get; private set; }
            public IAsyncEnumerator<LlmStreamChunk> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
            public ValueTask<bool> MoveNextAsync()
            {
                _moves++;
                if (_moves == 1)
                {
                    Current = _outcome == "nulls" ? null : new LlmStreamChunk { Text = "partial" };
                    return new ValueTask<bool>(true);
                }
                if (_moves == 2)
                {
                    if (MoveFailure != null) return new ValueTask<bool>(Task.FromException<bool>(MoveFailure));
                    if (_outcome == "exception") return new ValueTask<bool>(Task.FromException<bool>(
                        new LlmClientException("backend failed", LlmErrorCode.ProviderError)));
                    if (_outcome == "cancel") return new ValueTask<bool>(Task.FromCanceled<bool>(new CancellationToken(true)));
                    if (_outcome == "text-error" || _outcome == "code-error")
                    {
                        Current = _outcome == "text-error"
                            ? new LlmStreamChunk { IsDone = true, Error = "backend failed", ErrorCode = LlmErrorCode.None }
                            : new LlmStreamChunk { IsDone = true, ErrorCode = LlmErrorCode.BackendUnavailable };
                        return new ValueTask<bool>(true);
                    }
                }
                return new ValueTask<bool>(false);
            }
            public ValueTask DisposeAsync()
            {
                Disposals++;
                if (CleanupFailure != null) return new ValueTask(Task.FromException(CleanupFailure));
                return _outcome == "dispose-exception" ? new ValueTask(Task.FromException(new IOException("cleanup failed"))) : default;
            }
        }

        private static async Task<Exception> CaptureAsync(Task task)
        {
            try { await task; return null; }
            catch (Exception exception) { return exception; }
        }

        private static LlmCompletionRequest Req(string payload)
        {
            return new LlmCompletionRequest { AgentRoleId = "Test", UserPayload = payload };
        }

        private static async Task<List<LlmStreamChunk>> Drain(IAsyncEnumerable<LlmStreamChunk> stream)
        {
            List<LlmStreamChunk> chunks = new();
            await foreach (LlmStreamChunk c in stream)
            {
                chunks.Add(c);
            }

            return chunks;
        }

        private sealed class ScriptedClient : ILlmClient
        {
            public readonly Queue<LlmCompletionResult> Results = new();
            public readonly Queue<LlmStreamChunk[]> Streams = new();
            public Exception Throw;
            public TaskCompletionSource<bool> Hold;
            public int Calls;
            public int StreamCalls;

            public async Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request, CancellationToken cancellationToken = default)
            {
                Calls++;
                if (Throw != null)
                {
                    throw Throw;
                }

                if (Hold != null)
                {
                    await Hold.Task.ConfigureAwait(false);
                }

                return Results.Count > 0 ? Results.Dequeue() : new LlmCompletionResult { Ok = true, Content = "ok" };
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request,
                [EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                StreamCalls++;
                LlmStreamChunk[] chunks = Streams.Count > 0
                    ? Streams.Dequeue()
                    : new[] { new LlmStreamChunk { Text = "ok", IsDone = true } };
                foreach (LlmStreamChunk c in chunks)
                {
                    await Task.Yield();
                    yield return c;
                }
            }
        }
    }
}
