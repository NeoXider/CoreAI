using System.Collections.Generic;
using System.Text;
using CoreAI.Ai;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CoreAI.Decisions
{
    /// <summary>
    /// Maps an HTTP failure of a decision endpoint onto <see cref="LlmErrorCode"/> and a named reason, and
    /// extracts a short provider message. Error bodies seen in the wild: TypeSafe <c>{"detail": ...}</c> (400) and
    /// <c>{"detail": [{"loc", "msg", "type"}]}</c> (422); OpenRouter and llama.cpp <c>{"error": {"code",
    /// "message"}}</c>. A body is never copied whole into a message, and a 401 body never at all.
    /// </summary>
    public static class DecisionErrorMapper
    {
        private const int MaxMessageChars = 300;

        /// <summary>Error category for an HTTP status and body.</summary>
        public static LlmErrorCode MapStatus(int status, string body)
        {
            string text = (body ?? "").ToLowerInvariant();
            if (status == 401 || status == 403)
            {
                return LlmErrorCode.AuthExpired;
            }

            if (status == 402)
            {
                return LlmErrorCode.PaymentRequired;
            }

            if (status == 429 || text.Contains("rate limit") || text.Contains("rate_limit"))
            {
                return LlmErrorCode.RateLimited;
            }

            if (status == 413 || IsContextOverflow(text))
            {
                return LlmErrorCode.ContextLengthExceeded;
            }

            if (status == 400 || status == 422)
            {
                return LlmErrorCode.InvalidRequest;
            }

            if (status == 408)
            {
                return LlmErrorCode.Timeout;
            }

            // WHY: llama-server answers 501 when the loaded model is not a decision model or cannot take images.
            // That is a property of the deployment; a replay returns the same 501, so it must not land in the
            // transient BackendUnavailable bucket that the 5xx sweep below would put it in.
            if (status == 501)
            {
                return LlmErrorCode.PermanentProviderError;
            }

            if (status == 0 || status >= 500)
            {
                return LlmErrorCode.BackendUnavailable;
            }

            if (status >= 400)
            {
                return LlmErrorCode.PermanentProviderError;
            }

            return LlmErrorCode.ProviderError;
        }

        /// <summary>Named reason for an HTTP status and body.</summary>
        public static string ReasonFor(int status, string body)
        {
            string text = (body ?? "").ToLowerInvariant();
            if (status == 501)
            {
                return DecisionErrorReasons.NotADecisionModel;
            }

            if (status == 400 || status == 422)
            {
                if ((text.Contains("too many") || text.Contains("at most") || text.Contains("maximum")) &&
                    (text.Contains("option") || text.Contains("criteria") || text.Contains("choice")))
                {
                    return DecisionErrorReasons.TooManyOptions;
                }

                if (text.Contains("unsupported") || text.Contains("not supported") || text.Contains("unknown type") ||
                    text.Contains("union_tag_invalid"))
                {
                    return DecisionErrorReasons.UnsupportedQuestionType;
                }
            }

            return DecisionErrorReasons.HttpError;
        }

        /// <summary>A failed result for an HTTP error response.</summary>
        /// <param name="status">HTTP status.</param>
        /// <param name="body">Response body.</param>
        /// <param name="backend">Backend name recorded on the result.</param>
        /// <param name="secret">The credential that was sent, scrubbed from the message if a body echoes it.</param>
        public static DecisionResult FromHttpFailure(int status, string body, string backend, string secret = "")
        {
            string providerMessage = status == 401 ? "[redacted auth error body]" : ExtractMessage(body);
            string message = Redact($"Decision endpoint returned HTTP {status}" +
                                    (providerMessage.Length > 0 ? ": " + providerMessage : "."), secret);
            return DecisionResult.Failure(MapStatus(status, body), ReasonFor(status, body), message, backend,
                null, status);
        }

        /// <summary>Short provider message from a decision error body; empty when there is none.</summary>
        public static string ExtractMessage(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return "";
            }

            string message;
            try
            {
                JToken root = JToken.Parse(body);
                message = root is JObject obj ? ExtractFromObject(obj) : "";
            }
            catch (JsonException)
            {
                message = "";
            }

            if (message.Length == 0)
            {
                message = body.Trim();
            }

            return Truncate(message.Replace('\n', ' ').Replace('\r', ' '));
        }

        /// <summary>Replaces every occurrence of <paramref name="secret"/> in <paramref name="text"/>.</summary>
        public static string Redact(string text, string secret)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(secret) || secret.Length < 4)
            {
                return text ?? "";
            }

            return text.Replace(secret, "[redacted]");
        }

        private static string ExtractFromObject(JObject root)
        {
            JToken detail = root["detail"];
            if (detail is JValue detailValue && detailValue.Type == JTokenType.String)
            {
                return (string)detailValue ?? "";
            }

            if (detail is JArray detailItems)
            {
                List<string> parts = new();
                foreach (JToken item in detailItems)
                {
                    string msg = item?["msg"]?.ToString() ?? "";
                    string loc = item?["loc"] is JArray locArray ? string.Join(".", locArray) : "";
                    string type = item?["type"]?.ToString() ?? "";
                    StringBuilder part = new();
                    if (loc.Length > 0)
                    {
                        part.Append(loc).Append(": ");
                    }

                    part.Append(msg);
                    if (type.Length > 0)
                    {
                        part.Append(" (").Append(type).Append(')');
                    }

                    parts.Add(part.ToString());
                }

                return string.Join("; ", parts);
            }

            if (detail is JObject detailObject)
            {
                return detailObject.ToString(Formatting.None);
            }

            JToken error = root["error"];
            if (error is JObject errorObject)
            {
                return errorObject["message"]?.ToString() ?? "";
            }

            if (error is JValue errorValue && errorValue.Type == JTokenType.String)
            {
                return (string)errorValue ?? "";
            }

            return root["message"]?.ToString() ?? "";
        }

        private static bool IsContextOverflow(string lowerText)
        {
            return lowerText.Contains("exceed_context_size") || lowerText.Contains("context length") ||
                   lowerText.Contains("context window") || lowerText.Contains("exceeds the available context") ||
                   lowerText.Contains("too many tokens") || lowerText.Contains("context_length_exceeded");
        }

        private static string Truncate(string text)
        {
            return text.Length <= MaxMessageChars ? text : text.Substring(0, MaxMessageChars) + "...";
        }
    }
}
