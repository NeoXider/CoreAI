using System;

namespace CoreAI.Ai
{
    /// <summary>
    /// The one rule every string-returning convenience API uses for a failed turn: it never returns the failure text
    /// as the reply, it throws. A turn the caller (or <c>StopAgent</c>) stopped surfaces as
    /// <see cref="OperationCanceledException"/>; every other failure as <see cref="LlmRequestFailedException"/>
    /// carrying the typed code. The result-typed APIs (<c>AskResultAsync</c>, <c>SmartAskResultAsync</c>, ...)
    /// return the same failure as <see cref="LlmCompletionResult"/> with <c>Ok = false</c> instead.
    /// </summary>
    public static class LlmTurnFailure
    {
        /// <summary>
        /// True when <paramref name="chunk"/> reports a failure: an error text or a failure category. A terminal
        /// chunk that merely ends the stream carries neither.
        /// </summary>
        public static bool IsFailure(LlmStreamChunk chunk)
        {
            return chunk != null && (!string.IsNullOrEmpty(chunk.Error) || chunk.ErrorCode != LlmErrorCode.None);
        }

        /// <summary>
        /// Throws the exception <c>CoreAi.StreamAsync</c> throws when <paramref name="chunk"/> reports a failure;
        /// returns normally for a content or a clean terminal chunk. <paramref name="operation"/> names the caller
        /// in the message (<c>"CoreAi.StreamAsync failed: ..."</c>).
        /// </summary>
        public static void ThrowIfFailed(LlmStreamChunk chunk, string operation)
        {
            if (!IsFailure(chunk))
            {
                return;
            }

            throw CreateException(operation, chunk.Error, chunk.ErrorCode, chunk.HttpStatus,
                chunk.RetryAfterSeconds, null);
        }

        /// <summary>
        /// The reply text of a successful <paramref name="result"/>; throws for a failed or missing one (see the
        /// class summary). An empty successful reply is returned as <c>""</c>.
        /// </summary>
        public static string ContentOrThrow(LlmCompletionResult result, string operation)
        {
            if (result == null)
            {
                throw CreateException(operation, "No completion was returned.", LlmErrorCode.EmptyResponse, null,
                    null, null);
            }

            if (result.Ok)
            {
                return result.Content ?? "";
            }

            throw CreateException(operation, result.Error, result.ErrorCode, result.HttpStatus,
                result.RetryAfterSeconds, result.ProviderErrorBody);
        }

        /// <summary>
        /// A failed <see cref="LlmCompletionResult"/> describing <paramref name="chunk"/>, for the result-typed APIs
        /// that collect a stream. A missing category becomes <see cref="LlmErrorCode.ProviderError"/>.
        /// </summary>
        public static LlmCompletionResult ToFailedResult(LlmStreamChunk chunk, string partialContent)
        {
            LlmErrorCode code = chunk == null || chunk.ErrorCode == LlmErrorCode.None
                ? LlmErrorCode.ProviderError
                : chunk.ErrorCode;
            return new LlmCompletionResult
            {
                Ok = false,
                Content = partialContent ?? "",
                Error = string.IsNullOrEmpty(chunk?.Error) ? code.ToString() : chunk.Error,
                ErrorCode = code,
                HttpStatus = chunk?.HttpStatus,
                RetryAfterSeconds = chunk?.RetryAfterSeconds,
                Model = chunk?.Model ?? ""
            };
        }

        /// <summary>
        /// The exception for one failed turn: <see cref="OperationCanceledException"/> for
        /// <see cref="LlmErrorCode.Cancelled"/>, <see cref="LlmRequestFailedException"/> otherwise. A missing
        /// category becomes <see cref="LlmErrorCode.ProviderError"/>. An authorization failure (401 or
        /// <see cref="LlmErrorCode.AuthExpired"/>) carries a redacted message; the provider body stays on
        /// <see cref="LlmClientException.ProviderErrorBody"/> for <see cref="LlmErrorPresentation.ToDiagnosticText"/>,
        /// which redacts it too.
        /// </summary>
        public static Exception CreateException(
            string operation,
            string error,
            LlmErrorCode errorCode,
            int? httpStatus,
            int? retryAfterSeconds,
            string providerErrorBody)
        {
            if (errorCode == LlmErrorCode.Cancelled)
            {
                return new OperationCanceledException(string.IsNullOrEmpty(error)
                    ? LlmCancellation.CancelledErrorText
                    : error);
            }

            LlmErrorCode code = errorCode == LlmErrorCode.None ? LlmErrorCode.ProviderError : errorCode;
            // WHY: the text of a 401 can echo the key that was just sent, and exception messages travel into logs
            // and crash reports; same redaction rule as LlmErrorPresentation.ToDiagnosticText.
            bool authFailure = code == LlmErrorCode.AuthExpired || httpStatus == 401;
            string detail = authFailure
                ? "authorization failed" + (httpStatus.HasValue ? $" (HTTP {httpStatus.Value})" : "")
                : string.IsNullOrWhiteSpace(error)
                    ? code.ToString()
                    : error.Trim();
            string caller = string.IsNullOrWhiteSpace(operation) ? "LLM request" : operation.Trim();
            return new LlmRequestFailedException(
                $"{caller} failed: {detail}",
                new LlmClientException(detail, code, httpStatus, retryAfterSeconds, providerErrorBody));
        }
    }
}
