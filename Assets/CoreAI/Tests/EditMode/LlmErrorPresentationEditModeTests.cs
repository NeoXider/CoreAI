using System;
using CoreAI.Ai;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode
{
    /// <summary>
    /// Guards the split between the string a PLAYER reads and the string a LOG keeps
    /// (<see cref="LlmErrorPresentation"/>): a backend-authored message must survive to the chat
    /// bubble, raw JSON/stack noise must not, and a 401 body must never leave the log — it can echo
    /// the key that was just submitted.
    /// </summary>
    public sealed class LlmErrorPresentationEditModeTests
    {
        [Test]
        public void UserMessage_PrefersBackendAuthoredMessageFromProviderBody()
        {
            LlmClientException exception = new(
                "HTTP error 403: Teacher is unavailable: AI service answered 403.",
                LlmErrorCode.ProviderError,
                403,
                null,
                "{\"error\":{\"code\":\"ai_upstream_error\",\"message\":\"Teacher is unavailable, try again in a minute.\"}}");

            Assert.AreEqual(
                "Teacher is unavailable, try again in a minute.",
                LlmErrorPresentation.ToUserMessage(exception));
        }

        [Test]
        public void UserMessage_StripsHttpPrefixWhenBodyIsNotJson()
        {
            LlmClientException exception = new(
                "HTTP error 502: Upstream gateway is down.",
                LlmErrorCode.BackendUnavailable,
                502,
                null,
                "<html>502 Bad Gateway</html>");

            Assert.AreEqual("Upstream gateway is down.", LlmErrorPresentation.ToUserMessage(exception));
        }

        [Test]
        public void UserMessage_FallsBackToTypedPhraseWhenTextIsDiagnosticNoise()
        {
            LlmClientException exception = new(
                "HTTP error 500: {\"error\":{\"metadata\":{\"raw\":\"...\"}}}",
                LlmErrorCode.BackendUnavailable,
                500,
                null,
                "{\"error\":{\"metadata\":{\"raw\":\"...\"}}}");

            string message = LlmErrorPresentation.ToUserMessage(exception);

            Assert.AreEqual(LlmErrorPresentation.ForErrorCode(LlmErrorCode.BackendUnavailable), message);
            StringAssert.DoesNotContain("{", message);
        }

        [Test]
        public void UserMessage_NeverEchoesAuthFailureBody()
        {
            LlmClientException exception = new(
                "HTTP error 401: invalid api key sk-secret-value",
                LlmErrorCode.AuthExpired,
                401,
                null,
                "{\"error\":{\"message\":\"Invalid API key sk-secret-value\"}}");

            string message = LlmErrorPresentation.ToUserMessage(exception);

            Assert.AreEqual(LlmErrorPresentation.ForErrorCode(LlmErrorCode.AuthExpired), message);
            StringAssert.DoesNotContain("sk-secret-value", message);
        }

        [Test]
        public void UserMessage_UsesRetryHintWhenProviderSuppliedOne()
        {
            LlmClientException exception = new(
                "HTTP error 429: {\"error\":{}}",
                LlmErrorCode.RateLimited,
                429,
                14,
                "{\"error\":{}}");

            StringAssert.Contains("14", LlmErrorPresentation.ToUserMessage(exception));
        }

        [Test]
        public void UserMessage_HandlesPlainExceptionsAndCancellation()
        {
            Assert.AreEqual(
                LlmErrorPresentation.DefaultUserMessage,
                LlmErrorPresentation.ToUserMessage(new InvalidOperationException("boom")));
            Assert.AreEqual(
                LlmErrorPresentation.ForErrorCode(LlmErrorCode.Cancelled),
                LlmErrorPresentation.ToUserMessage(new OperationCanceledException()));
            Assert.AreEqual("Custom fallback.", LlmErrorPresentation.ToUserMessage(null, "Custom fallback."));
        }

        [Test]
        public void DiagnosticText_KeepsEverythingExceptAuthBodies()
        {
            LlmClientException providerError = new(
                "HTTP error 403: blocked",
                LlmErrorCode.ProviderError,
                403,
                7,
                "{\"error\":{\"message\":\"blocked\"}}");

            string diagnostics = LlmErrorPresentation.ToDiagnosticText(providerError);

            StringAssert.Contains("code=ProviderError", diagnostics);
            StringAssert.Contains("http=403", diagnostics);
            StringAssert.Contains("retryAfter=7s", diagnostics);
            StringAssert.Contains("\"message\":\"blocked\"", diagnostics);

            LlmClientException authError = new(
                "HTTP error 401: invalid key",
                LlmErrorCode.AuthExpired,
                401,
                null,
                "{\"error\":{\"message\":\"Invalid API key sk-secret-value\"}}");

            string redacted = LlmErrorPresentation.ToDiagnosticText(authError);

            StringAssert.Contains("[redacted auth error body]", redacted);
            StringAssert.DoesNotContain("sk-secret-value", redacted);
        }

        [Test]
        public void StripHttpErrorPrefix_LeavesOrdinaryTextAlone()
        {
            Assert.AreEqual("Model is overloaded.", LlmErrorPresentation.StripHttpErrorPrefix("Model is overloaded."));
            Assert.AreEqual("Model is overloaded.", LlmErrorPresentation.StripHttpErrorPrefix("HTTP error 503: Model is overloaded."));
            Assert.AreEqual("", LlmErrorPresentation.StripHttpErrorPrefix(null));
        }
    }
}
