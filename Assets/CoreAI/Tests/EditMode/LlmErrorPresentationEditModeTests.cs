using System;
using CoreAI.Ai;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode
{
    /// <summary>
    /// Guards the split between the string the PLAYER reads and the string the LOG keeps
    /// (<see cref="LlmErrorPresentation"/>): a phrase OUR backend wrote for the player reaches the chat
    /// bubble; provider text, JSON, stack traces and tracebacks do not; the body of a 401 never leaves the
    /// log, because it may echo the key that was just sent.
    /// </summary>
    public sealed class LlmErrorPresentationEditModeTests
    {
        [TestCase(LlmErrorCode.AuthExpired, null)]
        [TestCase(LlmErrorCode.ProviderError, 401)]
        public void AuthDiagnostic_RedactsSecretsFromExceptionMessageAsWellAsBody(LlmErrorCode code, int? status)
        {
            LlmClientException error = new("Authorization: Bearer secret-key", code, status, null,
                "{\"token\":\"secret-key\"}");
            string diagnostic = LlmErrorPresentation.ToDiagnosticText(error);
            Assert.That(diagnostic, Does.Not.Contain("secret-key"));
            Assert.That(diagnostic, Does.Contain(code.ToString()));
        }

        [Test]
        public void LibraryDeadline_IsPresentedAsTimeoutRatherThanUserStop()
        {
            string message = LlmErrorPresentation.ToUserMessage(new LlmOperationTimeoutException());
            Assert.AreEqual(LlmErrorPresentation.ForErrorCode(LlmErrorCode.Timeout), message);
            Assert.AreNotEqual(LlmErrorPresentation.ForErrorCode(LlmErrorCode.Cancelled), message);
        }

        // The canonical backend envelope: error_code + message + details + request_id, plus the error.{code,message} mirror.
        private const string BackendEnvelope =
            "{\"error_code\":\"ai_upstream_error\"," +
            "\"message\":\"Алена сейчас недоступна: сервис ИИ ответил ошибкой.\"," +
            "\"details\":{},\"request_id\":\"6f1c2a\"," +
            "\"detail\":\"Алена сейчас недоступна: сервис ИИ ответил ошибкой.\"," +
            "\"error\":{\"code\":\"ai_upstream_error\",\"message\":\"Алена сейчас недоступна: сервис ИИ ответил ошибкой.\"}}";

        [Test]
        public void UserMessage_ShowsSentenceOurBackendWroteForThePlayer()
        {
            LlmClientException exception = new(
                "HTTP error 502: Алена сейчас недоступна: сервис ИИ ответил ошибкой.",
                LlmErrorCode.BackendUnavailable,
                502,
                null,
                BackendEnvelope);

            Assert.AreEqual(
                "Алена сейчас недоступна: сервис ИИ ответил ошибкой.",
                LlmErrorPresentation.ToUserMessage(exception));
        }

        [Test]
        public void UserMessage_BackendEnvelope_MessageMirrorInErrorBlockIsAccepted()
        {
            string envelopeWithoutTopLevelMessage =
                "{\"error_code\":\"ai_upstream_error\",\"request_id\":\"abc\"," +
                "\"error\":{\"code\":\"ai_upstream_error\",\"message\":\"Учитель отдыхает, попробуй через минуту.\"}}";
            LlmClientException exception = new(
                "HTTP error 503: whatever", LlmErrorCode.BackendUnavailable, 503, null, envelopeWithoutTopLevelMessage);

            Assert.AreEqual("Учитель отдыхает, попробуй через минуту.", LlmErrorPresentation.ToUserMessage(exception));
        }

        /// <summary>
        /// The player used to see "You exceeded your current quota, please check your plan and billing
        /// details..." - OpenAI's text, in the provider's own language, with a billing link. Nobody wrote
        /// it for the player.
        /// </summary>
        [Test]
        public void UserMessage_ProviderErrorMessage_IsNotShownVerbatim()
        {
            const string openAiBody =
                "{\"error\":{\"message\":\"You exceeded your current quota, please check your plan and billing " +
                "details. For more information on this error, read the docs: " +
                "https://platform.openai.com/docs/guides/error-codes/api-errors.\"," +
                "\"type\":\"insufficient_quota\",\"param\":null,\"code\":\"insufficient_quota\"}}";
            LlmClientException exception = new(
                "HTTP error 429: You exceeded your current quota, please check your plan and billing details.",
                LlmErrorCode.RateLimited,
                429,
                null,
                openAiBody);

            string message = LlmErrorPresentation.ToUserMessage(exception);

            Assert.AreEqual(LlmErrorPresentation.ForErrorCode(LlmErrorCode.RateLimited), message);
            StringAssert.DoesNotContain("billing", message);
            StringAssert.DoesNotContain("https://", message);
        }

        [Test]
        public void UserMessage_ProviderTextWithModelAndOrganisationIds_IsNotShownVerbatim()
        {
            const string body =
                "{\"error\":{\"message\":\"The model `gpt-4o-2024-11-20` does not exist or you do not have access " +
                "to it (org-Ab12Cd34Ef).\",\"type\":\"invalid_request_error\",\"code\":\"model_not_found\"}}";
            LlmClientException exception = new(
                "HTTP error 404: The model `gpt-4o-2024-11-20` does not exist",
                LlmErrorCode.PermanentProviderError,
                404,
                null,
                body);

            string message = LlmErrorPresentation.ToUserMessage(exception);

            Assert.AreEqual(LlmErrorPresentation.ForErrorCode(LlmErrorCode.PermanentProviderError), message);
            StringAssert.DoesNotContain("gpt-4o", message);
            StringAssert.DoesNotContain("org-", message);
        }

        /// <summary>
        /// "Upstream gateway is down." from the exception message used to be shown to the player as-is:
        /// an exception message is the adapter's transport text and has no authorship.
        /// </summary>
        [Test]
        public void UserMessage_ExceptionMessageWithoutBackendEnvelope_FallsBackToTypedPhrase()
        {
            LlmClientException exception = new(
                "HTTP error 502: Upstream gateway is down.",
                LlmErrorCode.BackendUnavailable,
                502,
                null,
                "<html>502 Bad Gateway</html>");

            Assert.AreEqual(
                LlmErrorPresentation.ForErrorCode(LlmErrorCode.BackendUnavailable),
                LlmErrorPresentation.ToUserMessage(exception));
        }

        [Test]
        public void UserMessage_FallsBackToTypedPhraseWhenBodyIsJsonWithoutMessage()
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

        // ---- Diagnostics that leaked into the backend's message never reach the feed ----

        [Test]
        public void UserMessage_DotNetStackTraceInBackendMessage_IsNotShown()
        {
            // A real .NET frame: "\r\n" + THREE spaces + "at ". The old check looked for "\n at " with one.
            string trace =
                "Object reference not set to an instance of an object.\r\n" +
                "   at CoreAI.Ai.MeaiOpenAiChatClient.SendAsync(LlmCompletionRequest request)\r\n" +
                "   at CoreAI.Ai.AiOrchestrator.RunTaskAsync(AiTaskRequest task)";
            LlmClientException exception = Envelope(trace, LlmErrorCode.BackendUnavailable, 500);

            string message = LlmErrorPresentation.ToUserMessage(exception);

            Assert.AreEqual(LlmErrorPresentation.ForErrorCode(LlmErrorCode.BackendUnavailable), message);
            StringAssert.DoesNotContain("at CoreAI", message);
        }

        [Test]
        public void UserMessage_PythonTracebackInBackendMessage_IsNotShown()
        {
            string traceback =
                "Traceback (most recent call last):\n" +
                "  File \"/app/app/services/ai_gateway.py\", line 212, in stream\n" +
                "    raise ValueError(frame)\n" +
                "ValueError: unexpected frame 27A";
            LlmClientException exception = Envelope(traceback, LlmErrorCode.ProviderError, 500);

            string message = LlmErrorPresentation.ToUserMessage(exception);

            Assert.AreEqual(LlmErrorPresentation.ForErrorCode(LlmErrorCode.ProviderError), message);
            StringAssert.DoesNotContain("Traceback", message);
        }

        [Test]
        public void UserMessage_ExceptionLineInBackendMessage_IsNotShown()
        {
            Assert.AreEqual(
                LlmErrorPresentation.ForErrorCode(LlmErrorCode.BackendUnavailable),
                LlmErrorPresentation.ToUserMessage(Envelope("TypeError: Failed to fetch", LlmErrorCode.BackendUnavailable, 502)));
            Assert.AreEqual(
                LlmErrorPresentation.ForErrorCode(LlmErrorCode.ProviderError),
                LlmErrorPresentation.ToUserMessage(Envelope("ValueError: bad frame", LlmErrorCode.ProviderError, 500)));
            Assert.AreEqual(
                LlmErrorPresentation.ForErrorCode(LlmErrorCode.BackendUnavailable),
                LlmErrorPresentation.ToUserMessage(Envelope(
                    "System.Net.Http.HttpRequestException: No connection could be made",
                    LlmErrorCode.BackendUnavailable, 503)));
        }

        [Test]
        public void IsPresentableToPlayer_AcceptsOrdinarySentencesAndRejectsDiagnostics()
        {
            Assert.IsTrue(LlmErrorPresentation.IsPresentableToPlayer("Попробуй ещё раз через минуту."));
            Assert.IsTrue(LlmErrorPresentation.IsPresentableToPlayer("Error: try again in a minute."),
                "The word \"Error\" inside an ordinary sentence is not an exception line.");
            Assert.IsTrue(LlmErrorPresentation.IsPresentableToPlayer("We are at capacity right now."),
                "An \"at\" inside a sentence is not a stack frame.");
            Assert.IsFalse(LlmErrorPresentation.IsPresentableToPlayer("boom\n    at fetch (app.js:12:3)"));
            Assert.IsFalse(LlmErrorPresentation.IsPresentableToPlayer("{\"error\":1}"));
            Assert.IsFalse(LlmErrorPresentation.IsPresentableToPlayer(new string('x', LlmErrorPresentation.MaxUserMessageLength + 1)));
            Assert.IsFalse(LlmErrorPresentation.IsPresentableToPlayer(""));
            Assert.IsFalse(LlmErrorPresentation.IsPresentableToPlayer(null));
        }

        // ---- 401: each marker on its own ----

        [Test]
        public void UserMessage_AuthByStatusAlone_NeverEchoesBody()
        {
            // The adapter passed status 401 but classified it under a different code.
            LlmClientException exception = new(
                "HTTP error 401: invalid api key sk-secret-value",
                LlmErrorCode.PermanentProviderError,
                401,
                null,
                WithEnvelope("Invalid API key sk-secret-value"));

            string message = LlmErrorPresentation.ToUserMessage(exception);

            Assert.AreEqual(LlmErrorPresentation.ForErrorCode(LlmErrorCode.AuthExpired), message);
            StringAssert.DoesNotContain("sk-secret-value", message);
        }

        [Test]
        public void UserMessage_AuthByCodeAlone_NeverEchoesBody()
        {
            // The AuthExpired code without an HTTP status (e.g. from a stream or from local classification).
            LlmClientException exception = new(
                "token expired sk-secret-value",
                LlmErrorCode.AuthExpired,
                null,
                null,
                WithEnvelope("Token expired: sk-secret-value"));

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
        public void ForErrorCode_ClientLimit_IsNotPresentedAsAccountQuota()
        {
            string local = LlmErrorPresentation.ForErrorCode(LlmErrorCode.ClientLimitExceeded);
            string account = LlmErrorPresentation.ForErrorCode(LlmErrorCode.QuotaExceeded);

            Assert.AreNotEqual(account, local);
            StringAssert.DoesNotContain("account", local);
            StringAssert.Contains("session", local);
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
        public void ExtractBackendAuthoredMessage_RequiresTheWholeEnvelope()
        {
            Assert.AreEqual("", LlmErrorPresentation.ExtractBackendAuthoredMessage(
                "{\"error\":{\"message\":\"provider text\"}}"), "A bare error.message is the shape any provider uses.");
            Assert.AreEqual("", LlmErrorPresentation.ExtractBackendAuthoredMessage(
                "{\"error_code\":\"x\",\"message\":\"no request id\"}"));
            Assert.AreEqual("", LlmErrorPresentation.ExtractBackendAuthoredMessage(
                "{\"request_id\":\"1\",\"message\":\"no error code\"}"));
            Assert.AreEqual("", LlmErrorPresentation.ExtractBackendAuthoredMessage("<html>nope</html>"));
            Assert.AreEqual("", LlmErrorPresentation.ExtractBackendAuthoredMessage(null));
            Assert.AreEqual("ok", LlmErrorPresentation.ExtractBackendAuthoredMessage(
                "{\"error_code\":\"x\",\"request_id\":\"1\",\"message\":\" ok \"}"));
        }

        [Test]
        public void StripHttpErrorPrefix_LeavesOrdinaryTextAlone()
        {
            Assert.AreEqual("Model is overloaded.", LlmErrorPresentation.StripHttpErrorPrefix("Model is overloaded."));
            Assert.AreEqual("Model is overloaded.",
                LlmErrorPresentation.StripHttpErrorPrefix("HTTP error 503: Model is overloaded."));
            Assert.AreEqual("", LlmErrorPresentation.StripHttpErrorPrefix(null));
        }

        // ---- helpers ----

        private static LlmClientException Envelope(string backendMessage, LlmErrorCode code, int status)
        {
            return new LlmClientException($"HTTP error {status}: {backendMessage}", code, status, null, WithEnvelope(backendMessage));
        }

        private static string WithEnvelope(string backendMessage)
        {
            string escaped = backendMessage.Replace("\\", "\\\\").Replace("\"", "\\\"")
                .Replace("\r", "\\r").Replace("\n", "\\n");
            return "{\"error_code\":\"internal_error\",\"message\":\"" + escaped + "\"," +
                   "\"details\":{},\"request_id\":\"r-1\",\"error\":{\"code\":\"internal_error\",\"message\":\"" + escaped + "\"}}";
        }
    }
}
