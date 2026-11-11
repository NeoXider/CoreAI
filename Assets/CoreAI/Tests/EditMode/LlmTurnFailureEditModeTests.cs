using System;
using CoreAI.Ai;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode
{
    /// <summary>
    /// The one rule every string-returning API uses for a failed turn (<see cref="LlmTurnFailure"/>): a failure
    /// throws a typed <see cref="LlmRequestFailedException"/> (still an <see cref="InvalidOperationException"/>, as
    /// <c>CoreAi.StreamAsync</c> always threw), a stop throws <see cref="OperationCanceledException"/>, and a clean
    /// chunk or a successful result passes through. Also pins <see cref="LlmErrorCode.NotConfigured"/>.
    /// </summary>
    public sealed class LlmTurnFailureEditModeTests
    {
        [Test]
        public void ErrorChunk_Throws_TypedFailure_ThatOldInvalidOperationHandlersStillCatch()
        {
            LlmStreamChunk chunk = new()
            {
                IsDone = true, Error = "HTTP 500: upstream exploded", ErrorCode = LlmErrorCode.BackendUnavailable,
                HttpStatus = 500, RetryAfterSeconds = 7
            };

            InvalidOperationException caught = Assert.Throws<LlmRequestFailedException>(
                () => LlmTurnFailure.ThrowIfFailed(chunk, "CoreAi.StreamAsync"));

            LlmRequestFailedException failure = (LlmRequestFailedException)caught;
            Assert.AreEqual("CoreAi.StreamAsync failed: HTTP 500: upstream exploded", failure.Message);
            Assert.AreEqual(LlmErrorCode.BackendUnavailable, failure.ErrorCode);
            Assert.AreEqual(500, failure.HttpStatus);
            Assert.AreEqual(7, failure.RetryAfterSeconds);
            Assert.AreSame(failure.Failure, failure.InnerException,
                "The typed failure rides along for LlmErrorPresentation.");
        }

        [Test]
        public void CleanChunks_DoNotThrow()
        {
            Assert.DoesNotThrow(() => LlmTurnFailure.ThrowIfFailed(new LlmStreamChunk { Text = "hel" }, "op"));
            Assert.DoesNotThrow(() => LlmTurnFailure.ThrowIfFailed(new LlmStreamChunk { IsDone = true }, "op"));
            Assert.DoesNotThrow(() => LlmTurnFailure.ThrowIfFailed(null, "op"));
            Assert.IsFalse(LlmTurnFailure.IsFailure(new LlmStreamChunk { IsDone = true, Text = "" }));
        }

        [Test]
        public void ErrorCodeAlone_IsAFailure_AndAMissingCodeBecomesProviderError()
        {
            Assert.IsTrue(LlmTurnFailure.IsFailure(
                new LlmStreamChunk { IsDone = true, ErrorCode = LlmErrorCode.EmptyResponse }));

            LlmRequestFailedException untyped = Assert.Throws<LlmRequestFailedException>(
                () => LlmTurnFailure.ThrowIfFailed(new LlmStreamChunk { IsDone = true, Error = "boom" }, "op"));
            Assert.AreEqual(LlmErrorCode.ProviderError, untyped.ErrorCode);
        }

        [Test]
        public void CancelledChunk_IsAStop_NotAFailure()
        {
            Assert.Throws<OperationCanceledException>(() => LlmTurnFailure.ThrowIfFailed(
                new LlmStreamChunk { IsDone = true, ErrorCode = LlmErrorCode.Cancelled }, "op"));
        }

        [Test]
        public void FailedResult_Throws_AndNeverReturnsTheErrorTextAsTheReply()
        {
            LlmCompletionResult failed = new()
            {
                Ok = false, Content = "", Error = "HTTP 503: try later", ErrorCode = LlmErrorCode.BackendUnavailable,
                HttpStatus = 503, ProviderErrorBody = "{\"error\":\"busy\"}"
            };

            LlmRequestFailedException failure = Assert.Throws<LlmRequestFailedException>(
                () => LlmTurnFailure.ContentOrThrow(failed, "CoreAi.AskAsync"));

            Assert.AreEqual(LlmErrorCode.BackendUnavailable, failure.ErrorCode);
            Assert.AreEqual(503, failure.HttpStatus);
            Assert.AreEqual("{\"error\":\"busy\"}", failure.Failure.ProviderErrorBody);
        }

        [Test]
        public void SuccessfulResult_ReturnsItsContent_AndAnEmptyReplyAsEmpty()
        {
            Assert.AreEqual("hello", LlmTurnFailure.ContentOrThrow(
                new LlmCompletionResult { Ok = true, Content = "hello" }, "op"));
            Assert.AreEqual("", LlmTurnFailure.ContentOrThrow(
                new LlmCompletionResult { Ok = true, Content = null }, "op"));
        }

        [Test]
        public void MissingResult_IsAnEmptyResponseFailure()
        {
            LlmRequestFailedException failure = Assert.Throws<LlmRequestFailedException>(
                () => LlmTurnFailure.ContentOrThrow(null, "op"));
            Assert.AreEqual(LlmErrorCode.EmptyResponse, failure.ErrorCode);
        }

        [TestCase(LlmErrorCode.AuthExpired, null)]
        [TestCase(LlmErrorCode.ProviderError, 401)]
        public void AuthFailure_MessageNeverCarriesTheProviderText(LlmErrorCode code, int? status)
        {
            LlmCompletionResult failed = new()
            {
                Ok = false, Error = "HTTP 401: invalid key sk-secret-123", ErrorCode = code, HttpStatus = status
            };

            LlmRequestFailedException failure = Assert.Throws<LlmRequestFailedException>(
                () => LlmTurnFailure.ContentOrThrow(failed, "CoreAi.AskAsync"));

            Assert.That(failure.ToString(), Does.Not.Contain("sk-secret-123"),
                "A 401 text can echo the key; neither the message nor the inner exception may carry it.");
            Assert.That(failure.Message, Does.Contain("authorization failed"));
        }

        [Test]
        public void ToFailedResult_KeepsThePartialText_AndTheTypedFields()
        {
            LlmCompletionResult result = LlmTurnFailure.ToFailedResult(new LlmStreamChunk
            {
                IsDone = true, Error = "stream dropped", ErrorCode = LlmErrorCode.BackendUnavailable, HttpStatus = 502
            }, "half an ans");

            Assert.IsFalse(result.Ok);
            Assert.AreEqual("half an ans", result.Content);
            Assert.AreEqual("stream dropped", result.Error);
            Assert.AreEqual(LlmErrorCode.BackendUnavailable, result.ErrorCode);
            Assert.AreEqual(502, result.HttpStatus);
        }

        [Test]
        public void NotConfigured_HasAStableValue()
        {
            Assert.AreEqual(15, (int)LlmErrorCode.NotConfigured,
                "Error codes are serialized and compared by value; a new code is appended, never inserted.");
            Assert.AreEqual(14, (int)LlmErrorCode.ClientLimitExceeded);
        }
    }
}
