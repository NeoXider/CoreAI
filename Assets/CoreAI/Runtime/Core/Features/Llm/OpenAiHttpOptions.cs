using System;
using System.Collections.Generic;
using CoreAI.Ai;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Mutable Unity-free OpenAI-compatible HTTP settings.
    /// </summary>
    public sealed class OpenAiHttpOptions : IOpenAiHttpSettings
    {
        public bool UseOpenAiCompatibleHttp { get; set; }
        public LlmExecutionMode ExecutionMode { get; set; } = LlmExecutionMode.ClientOwnedApi;
        public string ApiBaseUrl { get; set; } = OpenAiHttpConstants.DefaultApiBaseUrl;
        public string ApiKey { get; set; } = "";
        public string AuthorizationHeader { get; set; } = "";

        /// <summary>
        /// Provider model id. Empty is "not configured" — legal only under
        /// <see cref="LlmExecutionMode.ServerManagedApi"/>, where the backend picks the model. There is no
        /// built-in default: a silent one bills a model nobody selected and then lies about it in logs.
        /// </summary>
        public string Model { get; set; } = "";

        public float Temperature { get; set; } = 0.2f;
        public int RequestTimeoutSeconds { get; set; } = 120;
        public int MaxTokens { get; set; } = 128000;
        public string ExtraBodyJson { get; set; } = "";
        public LlmReasoningMode ReasoningMode { get; set; } = LlmReasoningMode.ProviderDefault;
        public int ThinkingBudgetTokens { get; set; }
        public int MaxRequestsPerSession { get; set; }
        public int MaxPromptChars { get; set; }
        public bool LogLlmInput { get; set; } = true;
        public bool LogLlmOutput { get; set; } = true;
        public bool EnableHttpDebugLogging { get; set; }
        public IRequestHeaderProvider HeaderProvider { get; set; }

        /// <summary>
        /// Safely sets a provider-specific top-level request-body parameter. Nested <see cref="JObject"/> and
        /// <see cref="JArray"/> values are supported. Passing C# <see langword="null"/> removes the parameter;
        /// use <see cref="JValue.CreateNull"/> to send JSON <c>null</c>.
        /// </summary>
        /// <remarks>
        /// The resulting <see cref="ExtraBodyJson"/> is compact and recursively key-sorted. CoreAI-owned fields
        /// such as <c>messages</c>, <c>model</c>, <c>stream</c>, and <c>tools</c> are rejected. Assignment is
        /// atomic: invalid input leaves <see cref="ExtraBodyJson"/> unchanged.
        /// </remarks>
        public void SetProviderBodyParameter(string key, JToken value)
        {
            string normalized = OpenAiProviderBodyParameters.Set(ExtraBodyJson, key, value);
            ExtraBodyJson = normalized;
        }

        /// <summary>
        /// Safely removes a provider-specific top-level request-body parameter. Assignment is atomic.
        /// </summary>
        public void RemoveProviderBodyParameter(string key)
        {
            string normalized = OpenAiProviderBodyParameters.Remove(ExtraBodyJson, key);
            ExtraBodyJson = normalized;
        }

        public static OpenAiHttpOptions From(IOpenAiHttpSettings source)
        {
            if (source == null)
            {
                return new OpenAiHttpOptions();
            }

            bool useOpenAiCompatibleHttp = true;
            LlmExecutionMode executionMode = LlmExecutionMode.ClientOwnedApi;
            int maxRequests = 0;
            int maxPromptChars = 0;
            if (source is OpenAiHttpOptions options)
            {
                useOpenAiCompatibleHttp = options.UseOpenAiCompatibleHttp;
                executionMode = options.ExecutionMode;
                maxRequests = options.MaxRequestsPerSession;
                maxPromptChars = options.MaxPromptChars;
            }

            return new OpenAiHttpOptions
            {
                UseOpenAiCompatibleHttp = useOpenAiCompatibleHttp,
                ExecutionMode = executionMode,
                ApiBaseUrl = source.ApiBaseUrl,
                ApiKey = source.ApiKey,
                AuthorizationHeader = source.AuthorizationHeader,
                Model = source.Model,
                Temperature = source.Temperature,
                RequestTimeoutSeconds = source.RequestTimeoutSeconds,
                MaxTokens = source.MaxTokens,
                ExtraBodyJson = source.ExtraBodyJson,
                ReasoningMode = source.ReasoningMode,
                ThinkingBudgetTokens = source.ThinkingBudgetTokens,
                MaxRequestsPerSession = maxRequests,
                MaxPromptChars = maxPromptChars,
                LogLlmInput = source.LogLlmInput,
                LogLlmOutput = source.LogLlmOutput,
                EnableHttpDebugLogging = source.EnableHttpDebugLogging,
                HeaderProvider = source.HeaderProvider
            };
        }
    }

    /// <summary>
    /// Safe, deterministic helpers for provider-specific fields in an OpenAI-compatible request body.
    /// </summary>
    /// <remarks>
    /// Values use <see cref="JToken"/> so nested objects and arrays work without reflection or <c>dynamic</c>.
    /// CoreAI-owned structural fields are rejected. Raw <see cref="IOpenAiHttpSettings.ExtraBodyJson"/> remains
    /// an advanced, backwards-compatible escape hatch that can override those fields.
    /// </remarks>
    public static class OpenAiProviderBodyParameters
    {
        private static readonly HashSet<string> ReservedKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "messages",
            "model",
            "stream",
            "stream_options",
            "temperature",
            "max_tokens",
            "enable_thinking",
            "thinking_budget",
            "chat_template_kwargs",
            "tools",
            "tool_choice"
        };

        /// <summary>
        /// Returns compact, recursively key-sorted JSON with <paramref name="key"/> set to
        /// <paramref name="value"/>. C# <see langword="null"/> removes the key; use
        /// <see cref="JValue.CreateNull"/> to send JSON <c>null</c>.
        /// </summary>
        /// <example>
        /// <code>
        /// options.SetProviderBodyParameter("provider", new JObject
        /// {
        ///     ["order"] = new JArray("cloudflare/fp8"),
        ///     ["allow_fallbacks"] = false
        /// });
        /// options.SetProviderBodyParameter("session_id", "coreai-teacher-v3");
        /// </code>
        /// </example>
        /// <exception cref="ArgumentException">
        /// The key is empty/reserved, or the existing JSON is not one valid object without duplicate keys.
        /// Exception text never includes parameter values or the existing JSON body.
        /// </exception>
        public static string Set(string extraBodyJson, string key, JToken value)
        {
            ValidateWritableKey(key);
            JObject root = ParseObject(extraBodyJson);

            if (value == null)
            {
                root.Remove(key);
            }
            else
            {
                root[key] = value.DeepClone();
            }

            return SortRecursively(root).ToString(Formatting.None);
        }

        /// <summary>Returns compact, recursively key-sorted JSON with <paramref name="key"/> removed.</summary>
        public static string Remove(string extraBodyJson, string key)
        {
            ValidateWritableKey(key);
            JObject root = ParseObject(extraBodyJson);
            root.Remove(key);
            return SortRecursively(root).ToString(Formatting.None);
        }

        /// <summary>
        /// Validates and normalizes arbitrary provider-body JSON. CoreAI-owned keys are intentionally allowed
        /// here because this method also validates the legacy raw escape hatch.
        /// </summary>
        public static string NormalizeRawJson(string extraBodyJson)
        {
            return SortRecursively(ParseObject(extraBodyJson)).ToString(Formatting.None);
        }

        /// <summary>Returns whether <paramref name="key"/> is constructed and controlled by CoreAI.</summary>
        public static bool IsReserved(string key)
        {
            return !string.IsNullOrWhiteSpace(key) && ReservedKeys.Contains(key.Trim());
        }

        private static void ValidateWritableKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("Provider body parameter key must be non-empty.", nameof(key));
            }

            string trimmed = key.Trim();
            if (!string.Equals(key, trimmed, StringComparison.Ordinal))
            {
                throw new ArgumentException("Provider body parameter key must not have surrounding whitespace.",
                    nameof(key));
            }

            if (ReservedKeys.Contains(key))
            {
                throw new ArgumentException(
                    $"Provider body parameter key '{SafeKeyForMessage(key)}' is controlled by CoreAI. " +
                    "Use the corresponding CoreAI setting instead.",
                    nameof(key));
            }
        }

        private static JObject ParseObject(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new JObject();
            }

            try
            {
                JsonLoadSettings loadSettings = new()
                {
                    DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
                    CommentHandling = CommentHandling.Ignore,
                    LineInfoHandling = LineInfoHandling.Ignore
                };
                return JObject.Parse(json, loadSettings);
            }
            catch (Exception ex) when (ex is JsonException || ex is ArgumentException)
            {
                throw new ArgumentException(
                    "ExtraBodyJson must be one valid JSON object without duplicate property names.",
                    nameof(json));
            }
        }

        private static JToken SortRecursively(JToken token)
        {
            if (token is JObject obj)
            {
                List<JProperty> properties = new(obj.Properties());
                properties.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));
                JObject sorted = new();
                foreach (JProperty property in properties)
                {
                    sorted.Add(property.Name, SortRecursively(property.Value));
                }

                return sorted;
            }

            if (token is JArray array)
            {
                JArray sorted = new();
                foreach (JToken item in array)
                {
                    sorted.Add(SortRecursively(item));
                }

                return sorted;
            }

            return token.DeepClone();
        }

        private static string SafeKeyForMessage(string key)
        {
            const int maxLength = 64;
            char[] chars = key.Length > maxLength ? key.Substring(0, maxLength).ToCharArray() : key.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (char.IsControl(chars[i]))
                {
                    chars[i] = '?';
                }
            }

            return new string(chars) + (key.Length > maxLength ? "..." : "");
        }
    }
}
