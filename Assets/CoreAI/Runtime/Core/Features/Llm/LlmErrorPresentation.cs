using System;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace CoreAI.Ai
{
    /// <summary>
    /// Turns an LLM failure into two different strings: one for the PLAYER, one for the LOG.
    ///
    /// WHY: the chat UI once pasted <c>exception.Message</c> straight into the feed, and the player saw
    /// <c>HTTP error 403: {"error":{"message":"..."}}</c> - technical, usually truncated and useless -
    /// while the log got the same short string and lost the provider's response body. This type splits
    /// the two audiences:
    /// <list type="bullet">
    /// <item><see cref="ToUserMessage(Exception, string)"/> - one readable sentence. The phrase OUR
    ///   backend wrote for the player wins (a gateway that already said "the teacher is unavailable, try
    ///   again in a minute" knows the product better than a library does); otherwise the built-in phrase
    ///   for the <see cref="LlmErrorCode"/>.</item>
    /// <item><see cref="ToDiagnosticText"/> - everything worth keeping: the error code, the HTTP status,
    ///   the retry hint and the raw provider response body.</item>
    /// </list>
    /// <para>
    /// <b>Authorship is verified, not assumed.</b> Text is shown to the player only when the error body
    /// carries the canonical backend envelope - the top-level string fields <c>error_code</c>,
    /// <c>request_id</c> and <c>message</c> (the <c>error.message</c> mirror is accepted as the carrier of
    /// the text). A raw provider (OpenAI, Anthropic, Groq, OpenRouter, a proxy's HTML page) does not emit
    /// such an envelope, so its <c>error.message</c> - in the provider's language, with model names,
    /// organisation ids and billing links - is never mistaken for a player-facing phrase. A bare
    /// <c>HTTP error 503: ...</c> from an exception message is transport text and is not shown either.
    /// Even a message from the envelope passes through the noise filter
    /// (<see cref="IsPresentableToPlayer"/>): a stack trace or traceback that leaked into the backend's
    /// message never reaches the feed.
    /// </para>
    /// Portable core: no Unity types, so the same presentation is available to any host and headless test.
    /// </summary>
    public static class LlmErrorPresentation
    {
        /// <summary>Fallback phrase used when nothing more specific is known.</summary>
        public const string DefaultUserMessage =
            "The assistant is unavailable right now. Please try again in a moment.";

        /// <summary>The longest backend message that is shown to the player as-is.</summary>
        public const int MaxUserMessageLength = 400;

        // WHY: real diagnostics arrive in several shapes, and none of them is a single-space "\n at ":
        //  - .NET frames: "\r\n   at Namespace.Type.Method(...)", THREE spaces after the line break;
        //  - JavaScript frames: "\n    at fn (file.js:12:3)";
        //  - Python tracebacks: "Traceback (most recent call last):" and 'File "x.py", line 3';
        //  - the exception line itself: "ValueError: ...", "TypeError: Failed to fetch",
        //    "System.Net.Http.HttpRequestException: ...".
        // Any of these markers means the text was written for an engineer, not for the player.
        private static readonly Regex StackFramePattern = new(
            @"(?:^|[\r\n])[ \t]*at[ \t]+\S",
            RegexOptions.CultureInvariant);

        private static readonly Regex PythonTracebackPattern = new(
            @"Traceback \(most recent call last\)|File ""[^""]*"", line \d+",
            RegexOptions.CultureInvariant);

        private static readonly Regex ExceptionLinePattern = new(
            @"(?:^|[\s(\[])[A-Za-z_][\w.]*(?:Exception|Error)\s*:",
            RegexOptions.CultureInvariant);

        /// <summary>One readable sentence for the chat bubble. Never returns null or empty.</summary>
        public static string ToUserMessage(Exception exception, string fallback = null)
        {
            if (exception is LlmClientException llmException)
            {
                return ToUserMessage(llmException, fallback);
            }

            if (exception is LlmOperationTimeoutException)
            {
                return ForErrorCode(LlmErrorCode.Timeout);
            }

            if (exception is OperationCanceledException)
            {
                return ForErrorCode(LlmErrorCode.Cancelled);
            }

            return Coalesce(fallback, DefaultUserMessage);
        }

        /// <summary>One readable sentence for the chat bubble, from a typed LLM failure.</summary>
        public static string ToUserMessage(LlmClientException exception, string fallback = null)
        {
            if (exception == null)
            {
                return Coalesce(fallback, DefaultUserMessage);
            }

            // WHY: the body of a 401 can echo the key/token that was sent back to us (providers do that),
            // so its text never reaches the feed - the player gets the "please sign in again" phrase.
            // Same rule as the redaction in the HTTP adapters; see MeaiOpenAiChatClient.BuildHttpException.
            if (IsAuthFailure(exception))
            {
                return Coalesce(fallback, ForErrorCode(LlmErrorCode.AuthExpired));
            }

            // Only a sentence our backend wrote for the player may beat the built-in phrase.
            string authored = ExtractBackendAuthoredMessage(exception.ProviderErrorBody);
            if (IsPresentableToPlayer(authored))
            {
                return authored.Trim();
            }

            return Coalesce(fallback, ForErrorCode(exception.ErrorCode, exception.RetryAfterSeconds));
        }

        /// <summary>Built-in phrase for a failure category; used when nobody wrote a better one.</summary>
        public static string ForErrorCode(LlmErrorCode errorCode, int? retryAfterSeconds = null)
        {
            string retryHint = retryAfterSeconds.HasValue && retryAfterSeconds.Value > 0
                ? $" Try again in {retryAfterSeconds.Value} s."
                : "";

            switch (errorCode)
            {
                case LlmErrorCode.Timeout:
                    return "The assistant took too long to answer. Please try again.";
                case LlmErrorCode.Cancelled:
                    return "The request was stopped.";
                case LlmErrorCode.EmptyResponse:
                    return "The assistant returned an empty answer. Please try again.";
                case LlmErrorCode.AuthExpired:
                    return "The session has expired. Please sign in again.";
                case LlmErrorCode.QuotaExceeded:
                    return "The assistant quota for this account is used up.";
                case LlmErrorCode.ClientLimitExceeded:
                    return "This session has reached its local assistant limit: too many requests, or a message that is too long.";
                case LlmErrorCode.PaymentRequired:
                    return "The assistant account has run out of credit. Please tell the maintainer.";
                case LlmErrorCode.PermanentProviderError:
                    return "The assistant provider refused this request. Please tell the maintainer.";
                case LlmErrorCode.RateLimited:
                    return "Too many requests to the assistant right now." + retryHint;
                case LlmErrorCode.BackendUnavailable:
                    return "The assistant is unavailable right now. Please try again in a moment.";
                case LlmErrorCode.InvalidRequest:
                    return "The assistant could not process this request.";
                case LlmErrorCode.RoutingError:
                    return "No assistant backend is configured. Please tell the maintainer.";
                case LlmErrorCode.ContextLengthExceeded:
                    return "The conversation got too long for the model. Start a new chat or shorten the message.";
                default:
                    return DefaultUserMessage;
            }
        }

        /// <summary>
        /// Everything worth writing to the log: the category, the HTTP status, the retry hint and the raw
        /// provider response body. Log it TOGETHER with the exception itself (that is where the stack is).
        /// </summary>
        public static string ToDiagnosticText(Exception exception)
        {
            if (!(exception is LlmClientException llmException))
            {
                return exception == null ? "" : exception.ToString();
            }

            string status = llmException.HttpStatus.HasValue ? $" http={llmException.HttpStatus.Value}" : "";
            string retry = llmException.RetryAfterSeconds.HasValue
                ? $" retryAfter={llmException.RetryAfterSeconds.Value}s"
                : "";
            // Same redaction rule as in the HTTP adapters: the body of an auth error may contain the key
            // that was just sent, and logs travel beyond this process.
            string body;
            if (string.IsNullOrWhiteSpace(llmException.ProviderErrorBody))
            {
                body = "";
            }
            else if (IsAuthFailure(llmException))
            {
                body = " body=[redacted auth error body]";
            }
            else
            {
                body = $" body={llmException.ProviderErrorBody}";
            }

            string message = IsAuthFailure(llmException) ? "[redacted auth error message]" : llmException.Message;
            return $"code={llmException.ErrorCode}{status}{retry} message={message}{body}";
        }

        /// <summary>
        /// The sentence our backend wrote for the player, or "" when the body is not the canonical backend
        /// envelope. The envelope is recognised by the top-level string fields <c>error_code</c>,
        /// <c>request_id</c> and <c>message</c> (the OpenAI-like <c>error.message</c> mirror is accepted as
        /// the carrier of the text too). A bare <c>{"error":{"message":...}}</c> is returned by any
        /// provider and says nothing about authorship - for it the result is "".
        /// </summary>
        public static string ExtractBackendAuthoredMessage(string providerErrorBody)
        {
            if (string.IsNullOrWhiteSpace(providerErrorBody))
            {
                return "";
            }

            try
            {
                JObject parsed = JObject.Parse(providerErrorBody);
                if (!IsNonEmptyString(parsed["error_code"]) || !IsNonEmptyString(parsed["request_id"]))
                {
                    return "";
                }

                JToken message = parsed["message"];
                if (!IsNonEmptyString(message))
                {
                    message = parsed["error"]?["message"];
                }

                return IsNonEmptyString(message) ? message.ToString().Trim() : "";
            }
            catch (Exception)
            {
                // Not JSON (an HTML error page, proxy text, a truncated body): nobody wrote anything for
                // the player, and a parse failure must not surface as a second error.
                return "";
            }
        }

        /// <summary>
        /// Strips the <c>HTTP error 403: </c> prefix that the HTTP adapters put in front of the provider's
        /// text. A diagnostic helper: what remains is provider text and is not player-facing on its own.
        /// </summary>
        public static string StripHttpErrorPrefix(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return "";
            }

            const string marker = "HTTP error ";
            if (!message.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
            {
                return message.Trim();
            }

            int separator = message.IndexOf(':', marker.Length);
            return separator < 0 || separator + 1 >= message.Length
                ? message.Trim()
                : message.Substring(separator + 1).Trim();
        }

        /// <summary>
        /// May this be shown to the player? JSON dumps, stack traces, tracebacks, exception lines and
        /// novel-sized bodies are diagnostics, not UI copy; for those the built-in phrase is used.
        /// Public so that hosts rendering errors themselves apply the same filter.
        /// </summary>
        public static bool IsPresentableToPlayer(string message)
        {
            if (string.IsNullOrWhiteSpace(message) || message.Length > MaxUserMessageLength)
            {
                return false;
            }

            string trimmed = message.Trim();
            char first = trimmed[0];
            if (first == '{' || first == '[' || first == '<')
            {
                return false;
            }

            return !StackFramePattern.IsMatch(trimmed)
                   && !PythonTracebackPattern.IsMatch(trimmed)
                   && !ExceptionLinePattern.IsMatch(trimmed);
        }

        /// <summary>
        /// A 401-class failure: its body is treated everywhere as carrying a secret. EITHER of the two
        /// markers is enough - an adapter may classify a 401 as <see cref="LlmErrorCode.AuthExpired"/>
        /// without a status, or pass the status along under a different code.
        /// </summary>
        private static bool IsAuthFailure(LlmClientException exception)
        {
            return exception.HttpStatus == 401 || exception.ErrorCode == LlmErrorCode.AuthExpired;
        }

        private static bool IsNonEmptyString(JToken token)
        {
            return token != null && token.Type == JTokenType.String &&
                   !string.IsNullOrWhiteSpace(token.ToString());
        }

        private static string Coalesce(string preferred, string fallback)
        {
            return string.IsNullOrWhiteSpace(preferred) ? fallback : preferred.Trim();
        }
    }
}
