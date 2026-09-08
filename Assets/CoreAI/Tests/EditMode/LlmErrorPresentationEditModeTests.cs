using System;
using CoreAI.Ai;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode
{
    /// <summary>
    /// Сторожит разделение между строкой, которую читает ИГРОК, и строкой, которую хранит ЛОГ
    /// (<see cref="LlmErrorPresentation"/>): фраза, написанная НАШИМ бэкендом для игрока, доходит до
    /// пузыря чата; текст провайдера, JSON, трассы стека и traceback'и — нет; тело 401 никогда не
    /// покидает лог, потому что может вернуть только что отправленный ключ.
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

        // Канонический конверт бэкенда: error_code + message + details + request_id, зеркало error.{code,message}.
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
        /// Игрок видел «You exceeded your current quota, please check your plan and billing details…»
        /// — текст OpenAI на языке провайдера со ссылкой на биллинг. Его никто не писал для игрока.
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
        /// Раньше «Upstream gateway is down.» из сообщения исключения показывалось игроку как есть:
        /// сообщение исключения — транспортный текст адаптера, авторства у него нет.
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

        // ---- Диагностика, просочившаяся в сообщение бэкенда, в ленту не попадает ----

        [Test]
        public void UserMessage_DotNetStackTraceInBackendMessage_IsNotShown()
        {
            // Настоящий фрейм .NET: «\r\n» + ТРИ пробела + «at ». Старая проверка искала «\n at » с одним.
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
                "Слово «Error» в обычной фразе — не строка исключения.");
            Assert.IsTrue(LlmErrorPresentation.IsPresentableToPlayer("We are at capacity right now."),
                "«at» внутри предложения — не фрейм стека.");
            Assert.IsFalse(LlmErrorPresentation.IsPresentableToPlayer("boom\n    at fetch (app.js:12:3)"));
            Assert.IsFalse(LlmErrorPresentation.IsPresentableToPlayer("{\"error\":1}"));
            Assert.IsFalse(LlmErrorPresentation.IsPresentableToPlayer(new string('x', LlmErrorPresentation.MaxUserMessageLength + 1)));
            Assert.IsFalse(LlmErrorPresentation.IsPresentableToPlayer(""));
            Assert.IsFalse(LlmErrorPresentation.IsPresentableToPlayer(null));
        }

        // ---- 401: каждый признак по отдельности ----

        [Test]
        public void UserMessage_AuthByStatusAlone_NeverEchoesBody()
        {
            // Адаптер передал статус 401, но классифицировал под другим кодом.
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
            // Код AuthExpired без HTTP-статуса (например, из потока или из локальной классификации).
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
                "{\"error\":{\"message\":\"provider text\"}}"), "Голый error.message — форма любого провайдера.");
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
