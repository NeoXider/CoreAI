using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
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
        public void LlmCancellation_CallerCancelledToken_IsCancellationEvenForALibraryTimeout()
        {
            using System.Threading.CancellationTokenSource caller = new();
            caller.Cancel();

            Assert.AreEqual(LlmErrorCode.Cancelled,
                LlmCancellation.Classify(new LlmOperationTimeoutException(), caller.Token),
                "The caller asked to stop; a timer that raced it must not turn the stop into a timeout.");
            Assert.AreEqual(LlmErrorCode.Cancelled,
                LlmCancellation.Classify(new OperationCanceledException(), caller.Token));
            Assert.IsTrue(LlmCancellation.IsCancellation(new LlmOperationTimeoutException(), caller.Token));
            Assert.IsFalse(LlmCancellation.IsTimeout(new LlmOperationTimeoutException(), caller.Token));
        }

        [Test]
        public void LlmCancellation_LiveToken_TimeoutOnlyForTheLibraryTimeoutType()
        {
            System.Threading.CancellationToken live = System.Threading.CancellationToken.None;

            Assert.AreEqual(LlmErrorCode.Timeout,
                LlmCancellation.Classify(new LlmOperationTimeoutException(), live));
            Assert.AreEqual(LlmErrorCode.Timeout,
                LlmCancellation.Classify(new AggregateException(new LlmOperationTimeoutException()), live),
                "A timeout wrapped by a Task continuation is still a timeout.");
            Assert.AreEqual(LlmErrorCode.Cancelled,
                LlmCancellation.Classify(new OperationCanceledException(), live),
                "A cancellation nobody reported as a timeout (StopAgent, a scope) must not be shown as one.");
            Assert.AreEqual(LlmErrorCode.None,
                LlmCancellation.Classify(new InvalidOperationException("boom"), live));
        }

        [TestCase(LlmErrorCode.Timeout)]
        [TestCase(LlmErrorCode.ProviderError)]
        [TestCase(LlmErrorCode.BackendUnavailable)]
        [TestCase(LlmErrorCode.AuthExpired)]
        [TestCase(LlmErrorCode.PaymentRequired)]
        [TestCase(LlmErrorCode.RateLimited)]
        [TestCase(LlmErrorCode.Cancelled)]
        public void LlmCancellation_ClassifyCode_AnyFailureAfterCallerStopBecomesCancellation(LlmErrorCode code)
        {
            using CancellationTokenSource caller = new();
            Assert.AreEqual(code, LlmCancellation.ClassifyCode(code, caller.Token),
                "While the caller listens a failure keeps its own category.");

            caller.Cancel();
            Assert.AreEqual(LlmErrorCode.Cancelled, LlmCancellation.ClassifyCode(code, caller.Token),
                "After the stop a returned failure is the stop, exactly as a thrown one is (Classify).");
        }

        [Test]
        public void LlmCancellation_ClassifyCode_NoFailurePassesThroughEvenAfterTheStop()
        {
            using CancellationTokenSource caller = new();
            caller.Cancel();

            Assert.AreEqual(LlmErrorCode.None, LlmCancellation.ClassifyCode(LlmErrorCode.None, caller.Token));
            Assert.AreEqual(LlmErrorCode.None,
                LlmCancellation.ClassifyCode(LlmErrorCode.None, caller.Token, CancellationToken.None));
        }

        [Test]
        public void LlmCancellation_IsReportedCallerStop_CoversCodelessFailuresAndSkipsAlreadyCancelled()
        {
            using CancellationTokenSource caller = new();
            Assert.IsFalse(LlmCancellation.IsReportedCallerStop(LlmErrorCode.ProviderError, caller.Token));
            Assert.IsFalse(LlmCancellation.IsReportedCallerStop(LlmErrorCode.None, caller.Token));
            Assert.IsFalse(LlmCancellation.IsReportedCallerStop(LlmErrorCode.Cancelled, caller.Token));

            caller.Cancel();
            Assert.IsTrue(LlmCancellation.IsReportedCallerStop(LlmErrorCode.AuthExpired, caller.Token));
            Assert.IsTrue(LlmCancellation.IsReportedCallerStop(LlmErrorCode.None, caller.Token),
                "A failure reported without a code is still a failure, and after the stop it is the stop.");
            Assert.IsFalse(LlmCancellation.IsReportedCallerStop(LlmErrorCode.Cancelled, caller.Token),
                "An item that already reports the cancellation keeps its own text.");
        }

        [Test]
        public void LlmCancellation_CallerCancelled_OwnsEveryFault_TypedOrNot()
        {
            using CancellationTokenSource caller = new();
            caller.Cancel();

            Assert.AreEqual(LlmErrorCode.Cancelled,
                LlmCancellation.Classify(new IOException("socket closed"), caller.Token),
                "A transport torn down by the stop reports the fallout, not the cause.");
            Assert.AreEqual(LlmErrorCode.Cancelled,
                LlmCancellation.Classify(new ObjectDisposedException("HttpClient"), caller.Token));
            Assert.AreEqual(LlmErrorCode.Cancelled,
                LlmCancellation.Classify(
                    new LlmClientException("503", LlmErrorCode.BackendUnavailable, 503), caller.Token),
                "A typed fault after the stop is still the stop for the caller; endpoint health reads the type itself.");
            Assert.AreEqual(LlmErrorCode.Cancelled, LlmCancellation.Classify(null, caller.Token));
            Assert.IsTrue(LlmCancellation.IsCancellation(new IOException("socket closed"), caller.Token));
            Assert.IsFalse(LlmCancellation.IsTimeout(new IOException("socket closed"), caller.Token));
        }

        [Test]
        public void LlmCancellation_LiveCaller_LeavesFaultsUnclassified()
        {
            CancellationToken live = CancellationToken.None;

            Assert.AreEqual(LlmErrorCode.None, LlmCancellation.Classify(new IOException("socket closed"), live));
            Assert.AreEqual(LlmErrorCode.None, LlmCancellation.Classify(new ObjectDisposedException("HttpClient"), live));
            Assert.AreEqual(LlmErrorCode.None,
                LlmCancellation.Classify(new LlmClientException("503", LlmErrorCode.BackendUnavailable, 503), live),
                "With the caller alive a typed fault keeps its own code; the rule has nothing to say about it.");
            Assert.AreEqual(LlmErrorCode.None, LlmCancellation.Classify(null, live));
            Assert.AreEqual(LlmErrorCode.None,
                LlmCancellation.Classify(new IOException("x", new TaskCanceledException()), live),
                "A nested TaskCanceledException inside a transport error is a failure, not a stop, while the caller listens.");
            Assert.AreEqual(LlmErrorCode.Cancelled,
                LlmCancellation.Classify(new AggregateException(new OperationCanceledException()), live),
                "A cancellation a Task continuation wrapped is still a cancellation.");
        }

        [Test]
        public void LlmCancellation_DeadlineOverloads_HostDeadlineIsATimeoutUnlessTheCallerStopped()
        {
            using CancellationTokenSource caller = new();
            using CancellationTokenSource deadline = new();
            deadline.Cancel();

            Assert.AreEqual(LlmErrorCode.Timeout,
                LlmCancellation.Classify(new OperationCanceledException(deadline.Token), caller.Token, deadline.Token),
                "A cancellation observed while the host deadline had fired and the caller was alive is that host's timeout.");
            Assert.AreEqual(LlmErrorCode.Timeout,
                LlmCancellation.ClassifyCode(LlmErrorCode.Cancelled, caller.Token, deadline.Token));
            Assert.AreEqual(LlmErrorCode.ProviderError,
                LlmCancellation.ClassifyCode(LlmErrorCode.ProviderError, caller.Token, deadline.Token),
                "The deadline only resolves the cancel/timeout ambiguity; a provider error stays one.");

            caller.Cancel();
            Assert.AreEqual(LlmErrorCode.Cancelled,
                LlmCancellation.Classify(new OperationCanceledException(deadline.Token), caller.Token, deadline.Token),
                "Both fired: the caller wins.");
            Assert.AreEqual(LlmErrorCode.Cancelled,
                LlmCancellation.ClassifyCode(LlmErrorCode.Cancelled, caller.Token, deadline.Token));
            Assert.AreEqual(LlmErrorCode.Cancelled,
                LlmCancellation.ClassifyCode(LlmErrorCode.Timeout, caller.Token, deadline.Token));
            Assert.AreEqual(LlmErrorCode.Cancelled,
                LlmCancellation.ClassifyCode(LlmErrorCode.ProviderError, caller.Token, deadline.Token),
                "Both fired: the caller owns the returned failure too.");
            Assert.AreEqual(LlmErrorCode.Cancelled,
                LlmCancellation.Classify(new IOException("socket closed"), caller.Token, deadline.Token));
        }

        [Test]
        public void LlmCancellation_FindClientException_LooksThroughWrappersAndAggregates()
        {
            LlmClientException refusal = new("401", LlmErrorCode.AuthExpired, 401);

            Assert.AreSame(refusal, LlmCancellation.FindClientException(refusal));
            Assert.AreSame(refusal, LlmCancellation.FindClientException(
                new OperationCanceledException("wrapped by a decorator", refusal)));
            Assert.AreSame(refusal, LlmCancellation.FindClientException(
                new AggregateException(new IOException("x"), new InvalidOperationException("y", refusal))));
            Assert.IsNull(LlmCancellation.FindClientException(new IOException("x")));
            Assert.IsNull(LlmCancellation.FindClientException(null));
        }

        [Test]
        public void LlmCancellation_WrapAsCancellation_KeepsTheFaultAttached()
        {
            using CancellationTokenSource caller = new();
            caller.Cancel();
            IOException fault = new("socket closed");

            OperationCanceledException wrapped = LlmCancellation.WrapAsCancellation(fault, caller.Token, "the primary");

            Assert.AreSame(fault, wrapped.InnerException);
            Assert.AreEqual(caller.Token, wrapped.CancellationToken);
            StringAssert.Contains("the primary", wrapped.Message);
            StringAssert.Contains(nameof(IOException), wrapped.Message);
            Assert.AreEqual(LlmErrorCode.Cancelled, LlmCancellation.Classify(wrapped, caller.Token));
        }

        [Test]
        public void LlmCompletionResult_WithError_IsACopyThatLeavesTheSourceUntouched()
        {
            LlmToolCallTrace[] traces = { new("tool", true, 1d, "native") };
            LlmCompletionResult source = new()
            {
                Ok = false,
                Content = "partial",
                ReasoningContent = "thinking",
                Error = "LLM request timed out.",
                ErrorCode = LlmErrorCode.Timeout,
                HttpStatus = 504,
                RetryAfterSeconds = 3,
                ProviderErrorBody = "{}",
                Model = "m",
                PromptTokens = 10,
                LastRoundtripPromptTokens = 4,
                CompletionTokens = 5,
                TotalTokens = 15,
                CacheReadTokens = 2,
                CacheWriteTokens = 1,
                ExecutedToolCalls = traces
            };

            LlmCompletionResult copy = source.WithError(LlmCancellation.CancelledErrorText, LlmErrorCode.Cancelled);

            Assert.AreNotSame(source, copy);
            Assert.AreEqual(LlmErrorCode.Timeout, source.ErrorCode, "The inner client's instance is not mutated.");
            Assert.AreEqual("LLM request timed out.", source.Error);
            Assert.AreEqual(LlmErrorCode.Cancelled, copy.ErrorCode);
            Assert.AreEqual(LlmCancellation.CancelledErrorText, copy.Error);
            Assert.IsFalse(copy.Ok);
            Assert.AreEqual("partial", copy.Content);
            Assert.AreEqual("thinking", copy.ReasoningContent);
            Assert.AreEqual(504, copy.HttpStatus);
            Assert.AreEqual(3, copy.RetryAfterSeconds);
            Assert.AreEqual("{}", copy.ProviderErrorBody);
            Assert.AreEqual("m", copy.Model);
            Assert.AreEqual(10, copy.PromptTokens);
            Assert.AreEqual(4, copy.LastRoundtripPromptTokens);
            Assert.AreEqual(5, copy.CompletionTokens);
            Assert.AreEqual(15, copy.TotalTokens);
            Assert.AreEqual(2, copy.CacheReadTokens);
            Assert.AreEqual(1, copy.CacheWriteTokens);
            Assert.AreSame(traces, copy.ExecutedToolCalls);
        }

        [Test]
        public void LlmStreamChunk_WithError_IsACopyThatLeavesTheSourceUntouched()
        {
            LlmToolCallTrace[] traces = { new("tool", true, 1d, "native") };
            LlmStreamChunk source = new()
            {
                Text = "tail",
                StartsNewMessage = true,
                ReasoningText = "thinking",
                IsDone = true,
                Error = "LLM request timed out.",
                ErrorCode = LlmErrorCode.Timeout,
                HttpStatus = 504,
                RetryAfterSeconds = 3,
                Model = "m",
                PromptTokens = 10,
                LastRoundtripPromptTokens = 4,
                CompletionTokens = 5,
                TotalTokens = 15,
                CacheReadTokens = 2,
                CacheWriteTokens = 1,
                ExecutedToolCalls = traces,
                BufferedStreamingUseToolProgressHint = true,
                BufferedStreamingNoToolBinding = true
            };

            LlmStreamChunk copy = source.WithError(LlmCancellation.CancelledErrorText, LlmErrorCode.Cancelled);

            Assert.AreNotSame(source, copy);
            Assert.AreEqual(LlmErrorCode.Timeout, source.ErrorCode, "The inner client's instance is not mutated.");
            Assert.AreEqual(LlmErrorCode.Cancelled, copy.ErrorCode);
            Assert.AreEqual(LlmCancellation.CancelledErrorText, copy.Error);
            Assert.AreEqual("tail", copy.Text);
            Assert.IsTrue(copy.StartsNewMessage);
            Assert.AreEqual("thinking", copy.ReasoningText);
            Assert.IsTrue(copy.IsDone);
            Assert.AreEqual(504, copy.HttpStatus);
            Assert.AreEqual(3, copy.RetryAfterSeconds);
            Assert.AreEqual("m", copy.Model);
            Assert.AreEqual(10, copy.PromptTokens);
            Assert.AreEqual(4, copy.LastRoundtripPromptTokens);
            Assert.AreEqual(5, copy.CompletionTokens);
            Assert.AreEqual(15, copy.TotalTokens);
            Assert.AreEqual(2, copy.CacheReadTokens);
            Assert.AreEqual(1, copy.CacheWriteTokens);
            Assert.AreSame(traces, copy.ExecutedToolCalls);
            Assert.IsTrue(copy.BufferedStreamingUseToolProgressHint);
            Assert.IsTrue(copy.BufferedStreamingNoToolBinding);
        }

        [Test]
        public void LlmCompletionResult_WithError_CopiesEveryOtherSettableProperty()
        {
            AssertWithErrorCopiesEveryProperty<LlmCompletionResult>(
                source => source.WithError(LlmCancellation.CancelledErrorText, LlmErrorCode.Cancelled));
        }

        [Test]
        public void LlmStreamChunk_WithError_CopiesEveryOtherSettableProperty()
        {
            AssertWithErrorCopiesEveryProperty<LlmStreamChunk>(
                source => source.WithError(LlmCancellation.CancelledErrorText, LlmErrorCode.Cancelled));
        }

        /// <summary>
        /// WHY by reflection: <c>WithError</c> lists the fields it carries over by hand, so a field added to the
        /// type later is silently dropped by every layer that re-classifies a failure. Every public settable
        /// property except the failure itself is set to a non-default value and must survive the copy.
        /// </summary>
        private static void AssertWithErrorCopiesEveryProperty<T>(Func<T, T> withError) where T : new()
        {
            string[] rewritten = { "Error", "ErrorCode", "Ok" };
            T pristine = new();
            T source = new();
            List<PropertyInfo> carried = new();
            foreach (PropertyInfo property in typeof(T).GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!property.CanRead || property.GetSetMethod() == null ||
                    Array.IndexOf(rewritten, property.Name) >= 0)
                {
                    continue;
                }

                property.SetValue(source, NonDefaultValue(property.PropertyType, property.Name));
                Assert.IsFalse(Equals(property.GetValue(pristine), property.GetValue(source)),
                    $"precondition: {typeof(T).Name}.{property.Name} differs from a new instance.");
                carried.Add(property);
            }

            Assert.IsNotEmpty(carried, "precondition: the type has properties to carry over.");
            T copy = withError(source);

            Assert.AreNotSame(source, copy);
            foreach (PropertyInfo property in carried)
            {
                object expected = property.GetValue(source);
                object actual = property.GetValue(copy);
                if (expected != null && !(expected is string) && !expected.GetType().IsValueType)
                {
                    Assert.AreSame(expected, actual, $"{typeof(T).Name}.WithError dropped {property.Name}.");
                }
                else
                {
                    Assert.AreEqual(expected, actual, $"{typeof(T).Name}.WithError dropped {property.Name}.");
                }
            }
        }

        private static object NonDefaultValue(Type type, string name)
        {
            Type underlying = Nullable.GetUnderlyingType(type) ?? type;
            if (underlying == typeof(string))
            {
                return "value-of-" + name;
            }

            if (underlying == typeof(bool))
            {
                return true;
            }

            if (underlying == typeof(int))
            {
                return 7;
            }

            if (underlying.IsEnum)
            {
                Array values = Enum.GetValues(underlying);
                return values.GetValue(values.Length - 1);
            }

            if (type == typeof(IReadOnlyList<LlmToolCallTrace>))
            {
                return new[] { new LlmToolCallTrace("tool", true, 1d, "native") };
            }

            Assert.Fail($"No non-default value for {type.Name} {name}: teach this ratchet the new property type.");
            return null;
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
