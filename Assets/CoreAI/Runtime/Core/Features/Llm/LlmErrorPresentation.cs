using System;
using Newtonsoft.Json.Linq;

namespace CoreAI.Ai
{
    /// <summary>
    /// Turns an LLM failure into two different strings: one for the PLAYER and one for the LOG.
    ///
    /// WHY: chat UIs used to paste <c>exception.Message</c> straight into the transcript, so a player
    /// saw <c>HTTP error 403: {"error":{"message":"..."}}</c> — technical, often truncated, and useless
    /// to them — while the log got the same short string and lost the provider body. This type splits
    /// the two audiences:
    /// <list type="bullet">
    /// <item><see cref="ToUserMessage(Exception, string)"/> — one readable sentence. A server-authored
    ///   message wins (a gateway that already says "the teacher is unavailable, try again in a minute"
    ///   knows the product better than this library); otherwise a per-<see cref="LlmErrorCode"/>
    ///   phrase is used.</item>
    /// <item><see cref="ToDiagnosticText"/> — everything worth keeping: error code, HTTP status,
    ///   retry hint and the raw provider body.</item>
    /// </list>
    /// Portable core: no Unity types, so the same mapping is available to any host or headless test.
    /// </summary>
    public static class LlmErrorPresentation
    {
        /// <summary>Fallback shown when nothing more specific is known.</summary>
        public const string DefaultUserMessage = "The assistant is unavailable right now. Please try again in a moment.";

        /// <summary>Longest server-authored message that is shown to the player as-is.</summary>
        public const int MaxUserMessageLength = 400;

        /// <summary>One readable sentence for the chat bubble. Never returns null or empty.</summary>
        public static string ToUserMessage(Exception exception, string fallback = null)
        {
            if (exception is LlmClientException llmException)
            {
                return ToUserMessage(llmException, fallback);
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

            // WHY: a 401 body can echo the submitted key/token back (providers do this), so its text
            // never reaches the transcript — the player gets the "sign in again" phrase instead.
            // Same rule as the redaction in the HTTP adapters; see MeaiOpenAiChatClient.BuildHttpException.
            if (IsAuthFailure(exception))
            {
                return Coalesce(fallback, ForErrorCode(LlmErrorCode.AuthExpired));
            }

            // A gateway/provider message aimed at the player wins over any built-in phrase.
            string authored = ExtractProviderMessage(exception.ProviderErrorBody);
            if (string.IsNullOrWhiteSpace(authored))
            {
                authored = StripHttpErrorPrefix(exception.Message);
            }

            if (IsPresentableToPlayer(authored))
            {
                return authored.Trim();
            }

            return Coalesce(fallback, ForErrorCode(exception.ErrorCode, exception.RetryAfterSeconds));
        }

        /// <summary>Built-in phrase for a failure category; used when nobody authored a better one.</summary>
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
        /// Everything worth writing to the log: category, HTTP status, retry hint and the raw provider
        /// body. Callers should log this ALONGSIDE the exception itself (which carries the stack trace).
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
            // Same redaction rule as the HTTP adapters: an auth-failure body can contain the key
            // that was just sent, and logs travel further than the process.
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

            return $"code={llmException.ErrorCode}{status}{retry} message={llmException.Message}{body}";
        }

        /// <summary>Reads <c>error.message</c> (OpenAI-compatible shape) out of a raw provider body.</summary>
        public static string ExtractProviderMessage(string providerErrorBody)
        {
            if (string.IsNullOrWhiteSpace(providerErrorBody))
            {
                return "";
            }

            try
            {
                JObject parsed = JObject.Parse(providerErrorBody);
                string message = parsed["error"]?["message"]?.ToString();
                if (string.IsNullOrWhiteSpace(message))
                {
                    message = parsed["message"]?.ToString() ?? parsed["detail"]?.ToString();
                }

                return string.IsNullOrWhiteSpace(message) ? "" : message.Trim();
            }
            catch (Exception)
            {
                // Not JSON (HTML error page, proxy text, truncated body) — the caller falls back to
                // the exception message, so a parse failure must never surface as a second error.
                return "";
            }
        }

        /// <summary>
        /// Drops the <c>HTTP error 403: </c> prefix that HTTP adapters put in front of the provider text,
        /// so a message authored for the player is not shown with transport noise in front of it.
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
        /// Is this string something a player should read? JSON dumps, stack traces and novel-length
        /// bodies are diagnostics, not UI text — those fall back to the built-in phrase.
        /// </summary>
        private static bool IsPresentableToPlayer(string message)
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

            return !trimmed.Contains("\n at ", StringComparison.Ordinal)
                   && !trimmed.Contains("Exception:", StringComparison.Ordinal);
        }

        /// <summary>401-class failure: its body is treated as secret-bearing everywhere.</summary>
        private static bool IsAuthFailure(LlmClientException exception) =>
            exception.HttpStatus == 401 || exception.ErrorCode == LlmErrorCode.AuthExpired;

        private static string Coalesce(string preferred, string fallback) =>
            string.IsNullOrWhiteSpace(preferred) ? fallback : preferred.Trim();
    }
}
