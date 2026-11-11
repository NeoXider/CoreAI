using System;

namespace CoreAI.Ai
{
    /// <summary>
    /// A failed model turn reported by a string-returning convenience API (<c>CoreAi.AskAsync</c>,
    /// <c>SmartAskAsync</c>, <c>StreamAsync</c>, <c>OrchestrateAsync</c>, ...) instead of handing the failure text
    /// back as if it were the reply. Derives from <see cref="InvalidOperationException"/>, the type
    /// <c>StreamAsync</c> has always thrown, so existing handlers keep catching it.
    /// <para>
    /// <see cref="Failure"/> carries the typed detail: pass it to
    /// <see cref="LlmErrorPresentation.ToUserMessage(LlmClientException, string)"/> for the sentence a player may
    /// see, and to <see cref="LlmErrorPresentation.ToDiagnosticText"/> for the log.
    /// </para>
    /// </summary>
    public sealed class LlmRequestFailedException : InvalidOperationException
    {
        /// <summary>Creates the exception around the typed failure.</summary>
        public LlmRequestFailedException(string message, LlmClientException failure)
            : base(message ?? "", failure)
        {
            Failure = failure ?? new LlmClientException(message ?? "");
        }

        /// <summary>The typed failure: category, HTTP status, retry hint and provider body.</summary>
        public LlmClientException Failure { get; }

        /// <summary>Stable failure category; never <see cref="LlmErrorCode.None"/>.</summary>
        public LlmErrorCode ErrorCode => Failure.ErrorCode;

        /// <summary>HTTP status code when the failure came from an HTTP backend.</summary>
        public int? HttpStatus => Failure.HttpStatus;

        /// <summary>Provider hint for when the request may be retried.</summary>
        public int? RetryAfterSeconds => Failure.RetryAfterSeconds;
    }
}
