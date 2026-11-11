using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CoreAI.Ai;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CoreAI.Decisions
{
    /// <summary>How the logprob fallback forces the model to answer with exactly one label token.</summary>
    public enum LogprobLabelForcing
    {
        /// <summary>
        /// <c>response_format: json_schema</c> with an <c>enum</c> of the labels, honoured by OpenAI, OpenRouter
        /// (per model), llama.cpp and LM Studio. The label is found inside <c>{"answer": "B"}</c>.
        /// </summary>
        JsonSchemaEnum = 0,

        /// <summary>A GBNF grammar (<c>grammar: root ::= "A" | "B"</c>), accepted by llama.cpp's chat route.</summary>
        Grammar = 1,

        /// <summary>Instructions only; the weakest option, for servers that accept neither.</summary>
        PromptOnly = 2
    }

    /// <summary>Options of the logprob fallback.</summary>
    public sealed class LogprobDecisionOptions
    {
        /// <summary>The default system prompt.</summary>
        public const string DefaultSystemPrompt =
            "You are a decision function inside a game. Read the state, answer the question with exactly one " +
            "of the listed labels and nothing else.";

        /// <summary>How the label is forced.</summary>
        public LogprobLabelForcing Forcing { get; set; } = LogprobLabelForcing.JsonSchemaEnum;

        /// <summary>
        /// <c>top_logprobs</c> requested. It caps the number of labels: a label outside the returned top list would
        /// silently get probability 0, so a question with more options than this is refused. OpenAI's maximum is 20.
        /// </summary>
        public int TopLogprobs { get; set; } = 20;

        /// <summary>System prompt sent before the question.</summary>
        public string SystemPrompt { get; set; } = DefaultSystemPrompt;
    }

    /// <summary>What <see cref="LogprobChatWire.ScanLabels"/> found in a chat completion.</summary>
    public sealed class LogprobLabelScan
    {
        /// <summary>True when a label position was found.</summary>
        public bool Ok { get; set; }

        /// <summary>Failure category when <see cref="Ok"/> is false.</summary>
        public LlmErrorCode ErrorCode { get; set; }

        /// <summary>Named reason when <see cref="Ok"/> is false.</summary>
        public string Reason { get; set; } = "";

        /// <summary>Failure description when <see cref="Ok"/> is false.</summary>
        public string Message { get; set; } = "";

        /// <summary>Merged natural-log probability per label; negative infinity for a label the server did not list.</summary>
        public double[] Logprobs { get; set; } = Array.Empty<double>();

        /// <summary>Usage of the completion.</summary>
        public LlmUsageRecord Usage { get; set; } = new();

        /// <summary>Model that answered.</summary>
        public string Model { get; set; } = "";
    }

    /// <summary>
    /// The chat-logprob fallback wire in one place: single-token labels per question, the OpenAI-compatible request
    /// (<c>logprobs: true</c>, <c>top_logprobs</c>, a forced label) and the scan of
    /// <c>choices[0].logprobs.content[].top_logprobs[]</c> for the label position.
    /// </summary>
    public static class LogprobChatWire
    {
        /// <summary>Route appended to the base URL.</summary>
        public const string Route = "chat/completions";

        /// <summary>Property of the JSON-schema answer object.</summary>
        public const string AnswerField = "answer";

        /// <summary>Letters used as choice labels; their count caps a choice question in this backend.</summary>
        public const string ChoiceLetters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";

        private static readonly char[] LabelTrim =
            { ' ', '\t', '\r', '\n', '"', '\'', '`', '{', '}', '[', ']', '(', ')', ':', ',', '.' };

        /// <summary>The chat URL for an OpenAI-style base URL.</summary>
        public static string BuildUrl(string baseUrl)
        {
            return DecisionUrls.Append(baseUrl, Route);
        }

        /// <summary>
        /// Single-token labels for a question: <c>yes</c>/<c>no</c>, letters for a choice, digits for a score.
        /// Returns null when a choice has more options than there are letters.
        /// </summary>
        public static IReadOnlyList<string> LabelsFor(DecisionQuestion question)
        {
            int count = question.Options.Count;
            if (question.Kind == DecisionQuestionKind.YesNo)
            {
                return new[] { DecisionQuestion.YesOptionId, DecisionQuestion.NoOptionId };
            }

            if (question.Kind == DecisionQuestionKind.Score)
            {
                string[] digits = new string[count];
                for (int i = 0; i < count; i++)
                {
                    digits[i] = i.ToString(CultureInfo.InvariantCulture);
                }

                return digits;
            }

            if (count > ChoiceLetters.Length)
            {
                return null;
            }

            string[] letters = new string[count];
            for (int i = 0; i < count; i++)
            {
                letters[i] = ChoiceLetters[i].ToString();
            }

            return letters;
        }

        /// <summary>The chat request for one question.</summary>
        public static JObject BuildRequestBody(DecisionRequest request, DecisionQuestion question,
            IReadOnlyList<string> labels, string model, LogprobDecisionOptions options)
        {
            LogprobDecisionOptions settings = options ?? new LogprobDecisionOptions();
            string prompt = BuildPrompt(request.State, question, labels, settings.Forcing);
            JToken userContent = prompt;
            if (request.Images.Count > 0)
            {
                JArray parts = new() { new JObject { ["type"] = "text", ["text"] = prompt } };
                foreach (AiAttachment image in request.Images)
                {
                    parts.Add(new JObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JObject
                        {
                            ["url"] = "data:" + image.ResolvedMediaType + ";base64," + Convert.ToBase64String(image.Data)
                        }
                    });
                }

                userContent = parts;
            }

            JObject body = new();
            if (!string.IsNullOrWhiteSpace(model))
            {
                body["model"] = model.Trim();
            }

            body["messages"] = new JArray
            {
                new JObject { ["role"] = "system", ["content"] = settings.SystemPrompt ?? "" },
                new JObject { ["role"] = "user", ["content"] = userContent }
            };
            body["temperature"] = 0;
            body["stream"] = false;
            body["logprobs"] = true;
            body["top_logprobs"] = settings.TopLogprobs;
            switch (settings.Forcing)
            {
                case LogprobLabelForcing.JsonSchemaEnum:
                    body["max_tokens"] = 16;
                    body["response_format"] = new JObject
                    {
                        ["type"] = "json_schema",
                        ["json_schema"] = new JObject
                        {
                            ["name"] = "decision",
                            ["strict"] = true,
                            ["schema"] = new JObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JObject
                                {
                                    [AnswerField] = new JObject { ["type"] = "string", ["enum"] = new JArray(labels) }
                                },
                                ["required"] = new JArray(AnswerField),
                                ["additionalProperties"] = false
                            }
                        }
                    };
                    break;
                case LogprobLabelForcing.Grammar:
                    body["max_tokens"] = 4;
                    body["grammar"] = BuildGrammar(labels);
                    break;
                default:
                    body["max_tokens"] = 3;
                    break;
            }

            return body;
        }

        /// <summary>GBNF that admits exactly one of <paramref name="labels"/>.</summary>
        public static string BuildGrammar(IReadOnlyList<string> labels)
        {
            StringBuilder grammar = new("root ::= ");
            for (int i = 0; i < labels.Count; i++)
            {
                if (i > 0)
                {
                    grammar.Append(" | ");
                }

                grammar.Append('"').Append(labels[i]).Append('"');
            }

            return grammar.ToString();
        }

        /// <summary>
        /// Finds the first generated token that is a label and merges the logprobs of every listed alternative
        /// that is a label too (case-insensitive, quotes and punctuation trimmed, so <c>"B</c>, <c> b</c> and
        /// <c>B</c> are one label).
        /// </summary>
        public static LogprobLabelScan ScanLabels(string json, IReadOnlyList<string> labels)
        {
            JObject root;
            try
            {
                root = JToken.Parse(json ?? "") as JObject;
            }
            catch (JsonException)
            {
                root = null;
            }

            if (root == null)
            {
                return Fail(LlmErrorCode.EmptyResponse, DecisionErrorReasons.MalformedResponse,
                    "The chat response is not a JSON object.");
            }

            LlmUsageRecord usage = SystemOneWire.ParseUsage(root["usage"]);
            string model = root["model"]?.ToString() ?? "";
            usage.Model = model;
            JToken choice = root["choices"] is JArray choices && choices.Count > 0 ? choices[0] : null;
            if (!(choice?["logprobs"]?["content"] is JArray content) || content.Count == 0)
            {
                LogprobLabelScan missing = Fail(LlmErrorCode.PermanentProviderError, DecisionErrorReasons.LogprobsMissing,
                    "The chat endpoint returned no logprobs; this server or model cannot serve the logprob fallback.");
                missing.Usage = usage;
                return missing;
            }

            foreach (JToken entry in content)
            {
                string token = entry?["token"]?.ToString() ?? "";
                if (MatchLabel(token, labels) < 0)
                {
                    continue;
                }

                double[] merged = new double[labels.Count];
                for (int i = 0; i < merged.Length; i++)
                {
                    merged[i] = double.NegativeInfinity;
                }

                Dictionary<string, double> alternatives = ReadAlternatives(entry["top_logprobs"]);
                double? chosenLogprob = SystemOneWire.ReadNumber(entry["logprob"]);
                if (chosenLogprob.HasValue && !alternatives.ContainsKey(token))
                {
                    alternatives[token] = chosenLogprob.Value;
                }

                foreach (KeyValuePair<string, double> alternative in alternatives)
                {
                    int label = MatchLabel(alternative.Key, labels);
                    if (label >= 0)
                    {
                        merged[label] = DecisionProbabilityMath.LogSumExp(merged[label], alternative.Value);
                    }
                }

                return new LogprobLabelScan { Ok = true, Logprobs = merged, Usage = usage, Model = model };
            }

            LogprobLabelScan none = Fail(LlmErrorCode.EmptyResponse, DecisionErrorReasons.NoLabelToken,
                "The chat answer contains none of the label tokens.");
            none.Usage = usage;
            none.Model = model;
            return none;
        }

        /// <summary>Index of the label <paramref name="token"/> spells, or -1.</summary>
        public static int MatchLabel(string token, IReadOnlyList<string> labels)
        {
            string normalized = (token ?? "").Trim(LabelTrim);
            if (normalized.Length == 0)
            {
                return -1;
            }

            for (int i = 0; i < labels.Count; i++)
            {
                if (string.Equals(normalized, labels[i], StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }

        private static string BuildPrompt(string state, DecisionQuestion question, IReadOnlyList<string> labels,
            LogprobLabelForcing forcing)
        {
            StringBuilder prompt = new();
            prompt.Append("State:\n").Append(state.Length > 0 ? state : "(none)").Append("\n\n");
            string instructions = question.Instructions.Length > 0 ? question.Instructions : "Pick the best option.";
            prompt.Append("Question: ").Append(instructions).Append('\n');
            switch (question.Kind)
            {
                case DecisionQuestionKind.YesNo:
                    if (question.Options[0].Description.Length > 0)
                    {
                        prompt.Append("Answer yes if: ").Append(question.Options[0].Description).Append('\n');
                    }

                    if (question.Options[1].Description.Length > 0)
                    {
                        prompt.Append("Answer no if: ").Append(question.Options[1].Description).Append('\n');
                    }

                    break;
                case DecisionQuestionKind.Score:
                    prompt.Append("Levels, lowest first:\n");
                    for (int i = 0; i < labels.Count; i++)
                    {
                        prompt.Append(labels[i]).Append(" = ").Append(question.Options[i].Description).Append('\n');
                    }

                    break;
                default:
                    prompt.Append("Options:\n");
                    for (int i = 0; i < labels.Count; i++)
                    {
                        DecisionOption option = question.Options[i];
                        prompt.Append(labels[i]).Append(" = ").Append(option.Id);
                        if (option.Description.Length > 0)
                        {
                            prompt.Append(": ").Append(option.Description);
                        }

                        prompt.Append('\n');
                    }

                    break;
            }

            prompt.Append("Answer with exactly one label: ").Append(string.Join(", ", labels)).Append('.');
            if (forcing == LogprobLabelForcing.JsonSchemaEnum)
            {
                prompt.Append(" Reply as JSON: {\"").Append(AnswerField).Append("\": \"<label>\"}.");
            }
            else
            {
                prompt.Append(" Reply with the label only.");
            }

            return prompt.ToString();
        }

        private static Dictionary<string, double> ReadAlternatives(JToken topLogprobs)
        {
            Dictionary<string, double> alternatives = new(StringComparer.Ordinal);
            if (topLogprobs is JArray list)
            {
                foreach (JToken item in list)
                {
                    string token = item?["token"]?.ToString();
                    double? logprob = SystemOneWire.ReadNumber(item?["logprob"]);
                    if (token != null && logprob.HasValue && !alternatives.ContainsKey(token))
                    {
                        alternatives[token] = logprob.Value;
                    }
                }
            }
            else if (topLogprobs is JObject map)
            {
                foreach (JProperty property in map.Properties())
                {
                    double? logprob = SystemOneWire.ReadNumber(property.Value);
                    if (logprob.HasValue)
                    {
                        alternatives[property.Name] = logprob.Value;
                    }
                }
            }

            return alternatives;
        }

        private static LogprobLabelScan Fail(LlmErrorCode code, string reason, string message)
        {
            return new LogprobLabelScan { Ok = false, ErrorCode = code, Reason = reason, Message = message };
        }
    }
}
