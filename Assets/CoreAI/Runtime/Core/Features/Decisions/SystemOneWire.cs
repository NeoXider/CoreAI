using System;
using System.Collections.Generic;
using System.Globalization;
using CoreAI.Ai;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CoreAI.Decisions
{
    /// <summary>How a System One server takes images. The servers agree on everything else and differ here.</summary>
    public enum SystemOneImageEncoding
    {
        /// <summary>
        /// TypeSafe and OpenRouter: <c>"assets": {"image_1": {"type": "image", "format": "png", "data_base64":
        /// ...}}</c>, each asset referenced by its key somewhere in the state. PNG and JPEG only.
        /// </summary>
        Assets = 0,

        /// <summary>llama.cpp (and LM Studio, which follows it): <c>"files": ["data:image/png;base64,..."]</c>.</summary>
        DataUrlFiles = 1,

        /// <summary>Ollama: <c>"images": ["&lt;raw base64&gt;"]</c>; data URLs are rejected. PNG, JPEG and WebP.</summary>
        Base64Images = 2
    }

    /// <summary>The per-server differences of the System One wire.</summary>
    public sealed class SystemOneDialect
    {
        /// <summary>Short name for diagnostics.</summary>
        public string Name { get; set; } = "llama.cpp";

        /// <summary>How images travel.</summary>
        public SystemOneImageEncoding ImageEncoding { get; set; } = SystemOneImageEncoding.DataUrlFiles;

        /// <summary>Send TypeSafe's opt-in header (<see cref="SystemOneWire.ImageOptInHeader"/>) when images are attached.</summary>
        public bool SendImageOptInHeader { get; set; }

        /// <summary>llama.cpp <c>llama-server</c>.</summary>
        public static SystemOneDialect LlamaCpp() => new() { Name = "llama.cpp", ImageEncoding = SystemOneImageEncoding.DataUrlFiles };

        /// <summary>LM Studio (images as data URLs, like llama.cpp).</summary>
        public static SystemOneDialect LmStudio() => new() { Name = "lmstudio", ImageEncoding = SystemOneImageEncoding.DataUrlFiles };

        /// <summary>Ollama 0.35+.</summary>
        public static SystemOneDialect Ollama() => new() { Name = "ollama", ImageEncoding = SystemOneImageEncoding.Base64Images };

        /// <summary>TypeSafe's hosted API.</summary>
        public static SystemOneDialect TypeSafe() => new()
        {
            Name = "typesafe", ImageEncoding = SystemOneImageEncoding.Assets, SendImageOptInHeader = true
        };

        /// <summary>OpenRouter (both of its decision routes).</summary>
        public static SystemOneDialect OpenRouter() => new() { Name = "openrouter", ImageEncoding = SystemOneImageEncoding.Assets };

        /// <summary>
        /// Picks a dialect from the base URL: the TypeSafe and OpenRouter hosts, Ollama's host or port 11434,
        /// LM Studio's port 1234, and llama.cpp for everything else.
        /// </summary>
        public static SystemOneDialect ForBaseUrl(string baseUrl)
        {
            if (!Uri.TryCreate((baseUrl ?? "").Trim(), UriKind.Absolute, out Uri uri))
            {
                return LlamaCpp();
            }

            string host = uri.Host.ToLowerInvariant();
            if (host.Contains("typesafe"))
            {
                return TypeSafe();
            }

            if (host.Contains("openrouter"))
            {
                return OpenRouter();
            }

            if (host.Contains("ollama") || uri.Port == 11434)
            {
                return Ollama();
            }

            return uri.Port == 1234 ? LmStudio() : LlamaCpp();
        }
    }

    /// <summary>URL building shared by the decision routes.</summary>
    public static class DecisionUrls
    {
        /// <summary>
        /// Appends <paramref name="route"/> to an OpenAI-style base URL, the same convention as chat
        /// (<c>{base}/chat/completions</c>), so <c>http://127.0.0.1:8080/v1</c> gives
        /// <c>http://127.0.0.1:8080/v1/systemone</c>. A base with no path at all gets <c>/v1</c> first.
        /// </summary>
        public static string Append(string baseUrl, string route)
        {
            string trimmed = (baseUrl ?? "").Trim().TrimEnd('/');
            if (trimmed.Length == 0)
            {
                return "";
            }

            if (Uri.TryCreate(trimmed, UriKind.Absolute, out Uri uri) &&
                (uri.AbsolutePath.Length == 0 || uri.AbsolutePath == "/"))
            {
                return trimmed + "/v1/" + route;
            }

            return trimmed + "/" + route;
        }

        /// <summary>
        /// OpenRouter's alpha decisions route, <c>https://openrouter.ai/api/alpha/decisions</c>, derived from its
        /// usual base <c>https://openrouter.ai/api/v1</c>.
        /// </summary>
        public static string OpenRouterAlphaDecisions(string baseUrl)
        {
            string trimmed = (baseUrl ?? "").Trim().TrimEnd('/');
            if (trimmed.Length == 0)
            {
                return "";
            }

            if (Uri.TryCreate(trimmed, UriKind.Absolute, out Uri uri) &&
                (uri.AbsolutePath.Length == 0 || uri.AbsolutePath == "/"))
            {
                return trimmed + "/api/alpha/decisions";
            }

            if (trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed.Substring(0, trimmed.Length - 3);
            }

            return trimmed + "/alpha/decisions";
        }
    }

    /// <summary>
    /// The System One wire in one place: request body, response parsing and URL. Spoken by llama.cpp
    /// (<c>POST /v1/systemone</c>), Ollama, LM Studio, TypeSafe and OpenRouter.
    /// <para>
    /// Request: <c>{model, state, questions: {id: {type, instructions?, criteria?}}, assets? | files? |
    /// images?}</c>. <c>noul</c> criteria is <c>{"true": ..., "false": ...}</c>, <c>choice</c> criteria maps each
    /// option name to its description (or null), <c>score</c> criteria is an array of level descriptions, lowest
    /// first, scored from 0. Response: <c>{model, answers: {id: answer}, usage: {input_tokens,
    /// output_tokens}}</c> where a <c>noul</c> answer is the probability of true, a <c>choice</c> answer has
    /// <c>choice</c>, <c>probabilities</c> keyed by option and <c>confidence</c>, a <c>score</c> answer has the
    /// expected level <c>score</c>, <c>probabilities</c> keyed by level and <c>confidence</c>, and OpenRouter may
    /// answer <c>{"type": "refusal"}</c> for one question while answering the rest.
    /// </para>
    /// </summary>
    public static class SystemOneWire
    {
        /// <summary>Route appended to the base URL.</summary>
        public const string Route = "systemone";

        /// <summary>TypeSafe's opt-in header for image assets.</summary>
        public const string ImageOptInHeader = "TypeSafe-Danger-Zone-Images";

        /// <summary>Wire name of <see cref="DecisionQuestionKind.YesNo"/>.</summary>
        public const string TypeNoul = "noul";

        /// <summary>Wire name of <see cref="DecisionQuestionKind.Choice"/>.</summary>
        public const string TypeChoice = "choice";

        /// <summary>Wire name of <see cref="DecisionQuestionKind.Score"/>.</summary>
        public const string TypeScore = "score";

        /// <summary>OpenRouter's per-question refusal.</summary>
        public const string TypeRefusal = "refusal";

        /// <summary>Key prefix of image assets: <c>image_1</c>, <c>image_2</c>, ...</summary>
        public const string AssetKeyPrefix = "image_";

        /// <summary>The decision URL for an OpenAI-style base URL.</summary>
        public static string BuildUrl(string baseUrl)
        {
            return DecisionUrls.Append(baseUrl, Route);
        }

        /// <summary>Wire name of a question kind.</summary>
        public static string WireType(DecisionQuestionKind kind)
        {
            switch (kind)
            {
                case DecisionQuestionKind.YesNo:
                    return TypeNoul;
                case DecisionQuestionKind.Score:
                    return TypeScore;
                default:
                    return TypeChoice;
            }
        }

        /// <summary>
        /// Builds the request body. Returns null with <paramref name="error"/> set when an image cannot travel in
        /// this dialect. The request must already be valid (<see cref="DecisionRequestValidator"/>).
        /// </summary>
        public static JObject BuildRequestBody(DecisionRequest request, string model, SystemOneDialect dialect,
            out DecisionValidationError error)
        {
            error = null;
            SystemOneDialect wire = dialect ?? SystemOneDialect.LlamaCpp();
            JObject questions = new();
            foreach (DecisionQuestion question in request.Questions)
            {
                questions[question.Id] = BuildQuestion(question);
            }

            string state = request.State;
            JToken images = null;
            string imagesField = "";
            if (request.Images.Count > 0)
            {
                images = BuildImages(request.Images, wire, ref state, out imagesField, out error);
                if (error != null)
                {
                    return null;
                }
            }

            JObject body = new();
            if (!string.IsNullOrWhiteSpace(model))
            {
                body["model"] = model.Trim();
            }

            body["state"] = state;
            body["questions"] = questions;
            if (images != null)
            {
                body[imagesField] = images;
            }

            return body;
        }

        /// <summary>Compact JSON of <see cref="BuildRequestBody"/>, or null with <paramref name="error"/> set.</summary>
        public static string BuildRequestJson(DecisionRequest request, string model, SystemOneDialect dialect,
            out DecisionValidationError error)
        {
            JObject body = BuildRequestBody(request, model, dialect, out error);
            return body?.ToString(Formatting.None);
        }

        /// <summary>
        /// Parses a 200 response into answers aligned with <paramref name="request"/>. A missing answer, a type
        /// mismatch or an answer without any probability is a failure; probabilities are renormalised to sum to 1.
        /// </summary>
        public static DecisionResult ParseResponse(string json, DecisionRequest request, string backend)
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
                return Malformed("The decision response is not a JSON object.", backend);
            }

            if (!(root["answers"] is JObject answers))
            {
                return Malformed("The decision response has no 'answers' object.", backend);
            }

            List<DecisionAnswer> parsed = new();
            foreach (DecisionQuestion question in request.Questions)
            {
                if (!(answers[question.Id] is JObject answer))
                {
                    return DecisionResult.Failure(LlmErrorCode.EmptyResponse, DecisionErrorReasons.MissingAnswer,
                        $"The decision response has no answer for question '{question.Id}'.", backend);
                }

                string type = answer["type"]?.ToString() ?? "";
                if (string.Equals(type, TypeRefusal, StringComparison.Ordinal))
                {
                    parsed.Add(DecisionAnswer.Refusal(question));
                    continue;
                }

                string expected = WireType(question.Kind);
                if (type.Length > 0 && !string.Equals(type, expected, StringComparison.Ordinal))
                {
                    return Malformed($"Answer '{question.Id}' has type '{type}' but the question is '{expected}'.",
                        backend);
                }

                DecisionAnswer result = question.Kind switch
                {
                    DecisionQuestionKind.YesNo => ParseNoul(question, answer),
                    DecisionQuestionKind.Score => ParseScore(question, answer),
                    _ => ParseChoice(question, answer)
                };

                if (result == null)
                {
                    return Malformed($"Answer '{question.Id}' carries no usable probability.", backend);
                }

                parsed.Add(result);
            }

            string model = root["model"]?.ToString() ?? "";
            LlmUsageRecord usage = ParseUsage(root["usage"]);
            usage.Model = model;
            usage.RoleId = request.ConsumerId ?? "";
            usage.TraceId = request.TraceId ?? "";
            return DecisionResult.Success(parsed, usage, model, backend);
        }

        /// <summary>Reads <c>usage</c> (<c>input_tokens</c>/<c>output_tokens</c>, OpenAI names as a fallback).</summary>
        public static LlmUsageRecord ParseUsage(JToken usageToken)
        {
            LlmUsageRecord usage = new() { Streaming = false };
            if (!(usageToken is JObject obj))
            {
                return usage;
            }

            usage.PromptTokens = ReadInt(obj["input_tokens"]) ?? ReadInt(obj["prompt_tokens"]) ?? 0;
            usage.CompletionTokens = ReadInt(obj["output_tokens"]) ?? ReadInt(obj["completion_tokens"]) ?? 0;
            usage.TotalTokens = ReadInt(obj["total_tokens"]) ?? usage.PromptTokens + usage.CompletionTokens;
            usage.CacheReadTokens = ReadInt(obj["input_tokens_details"]?["cached_tokens"]) ?? 0;
            return usage;
        }

        private static JObject BuildQuestion(DecisionQuestion question)
        {
            JObject wire = new() { ["type"] = WireType(question.Kind) };
            if (question.Instructions.Length > 0)
            {
                wire["instructions"] = question.Instructions;
            }

            switch (question.Kind)
            {
                case DecisionQuestionKind.YesNo:
                {
                    JObject criteria = new();
                    if (question.Options[0].Description.Length > 0)
                    {
                        criteria["true"] = question.Options[0].Description;
                    }

                    if (question.Options[1].Description.Length > 0)
                    {
                        criteria["false"] = question.Options[1].Description;
                    }

                    if (criteria.Count > 0)
                    {
                        wire["criteria"] = criteria;
                    }

                    break;
                }
                case DecisionQuestionKind.Score:
                {
                    JArray levels = new();
                    foreach (DecisionOption level in question.Options)
                    {
                        levels.Add(level.Description.Length > 0 ? level.Description : level.Id);
                    }

                    wire["criteria"] = levels;
                    break;
                }
                default:
                {
                    JObject options = new();
                    foreach (DecisionOption option in question.Options)
                    {
                        options[option.Id] = option.Description.Length > 0
                            ? new JValue(option.Description)
                            : JValue.CreateNull();
                    }

                    wire["criteria"] = options;
                    break;
                }
            }

            return wire;
        }

        private static JToken BuildImages(IReadOnlyList<AiAttachment> images, SystemOneDialect dialect,
            ref string state, out string field, out DecisionValidationError error)
        {
            error = null;
            switch (dialect.ImageEncoding)
            {
                case SystemOneImageEncoding.Assets:
                {
                    field = "assets";
                    JObject assets = new();
                    List<string> keys = new();
                    for (int i = 0; i < images.Count; i++)
                    {
                        string mediaType = images[i].ResolvedMediaType;
                        string format = mediaType == "image/png" ? "png" : mediaType == "image/jpeg" ? "jpg" : "";
                        if (format.Length == 0)
                        {
                            error = UnsupportedFormat(mediaType, dialect, "PNG or JPEG");
                            return null;
                        }

                        string key = AssetKeyPrefix + (i + 1).ToString(CultureInfo.InvariantCulture);
                        keys.Add(key);
                        assets[key] = new JObject
                        {
                            ["type"] = "image",
                            ["format"] = format,
                            ["data_base64"] = Convert.ToBase64String(images[i].Data)
                        };
                    }

                    // WHY: TypeSafe and OpenRouter refuse (HTTP 400) an asset that nothing references, so the
                    // state names every key.
                    state = (state.Length > 0 ? state + "\n\n" : "") + "Attached images: " + string.Join(", ", keys) + ".";
                    return assets;
                }
                case SystemOneImageEncoding.Base64Images:
                {
                    field = "images";
                    JArray list = new();
                    foreach (AiAttachment image in images)
                    {
                        string mediaType = image.ResolvedMediaType;
                        if (mediaType != "image/png" && mediaType != "image/jpeg" && mediaType != "image/webp")
                        {
                            error = UnsupportedFormat(mediaType, dialect, "PNG, JPEG or WebP");
                            return null;
                        }

                        list.Add(Convert.ToBase64String(image.Data));
                    }

                    return list;
                }
                default:
                {
                    field = "files";
                    JArray list = new();
                    foreach (AiAttachment image in images)
                    {
                        list.Add("data:" + image.ResolvedMediaType + ";base64," + Convert.ToBase64String(image.Data));
                    }

                    return list;
                }
            }
        }

        private static DecisionValidationError UnsupportedFormat(string mediaType, SystemOneDialect dialect,
            string accepted)
        {
            return new DecisionValidationError(DecisionErrorReasons.UnsupportedImageFormat, "",
                $"The {dialect.Name} decision wire takes {accepted} images; '{mediaType}' cannot be sent.");
        }

        private static DecisionAnswer ParseNoul(DecisionQuestion question, JObject answer)
        {
            double? yes = ReadNumber(answer["noul"]);
            if (!yes.HasValue && answer["probabilities"] is JObject probabilities)
            {
                yes = ReadNumber(probabilities["true"]) ?? ReadNumber(probabilities["yes"]);
                if (!yes.HasValue)
                {
                    double? no = ReadNumber(probabilities["false"]) ?? ReadNumber(probabilities["no"]);
                    yes = no.HasValue ? 1d - no.Value : (double?)null;
                }
            }

            if (!yes.HasValue)
            {
                return null;
            }

            double p = Math.Max(0d, Math.Min(1d, yes.Value));
            double[] vector = { p, 1d - p };
            int chosen = p >= 0.5d ? 0 : 1;
            // WHY: TypeSafe's noul answer has no confidence field (OpenRouter's has one); the distance from a
            // coin flip is the honest stand-in, not a constant.
            double confidence = ReadNumber(answer["confidence"]) ?? Math.Max(p, 1d - p);
            return new DecisionAnswer(question.Id, question.Kind, OptionIds(question), vector, chosen, confidence,
                true);
        }

        private static DecisionAnswer ParseChoice(DecisionQuestion question, JObject answer)
        {
            double[] probabilities = DecisionProbabilityMath.Renormalize(ReadAligned(answer["probabilities"], question));
            int chosen = question.IndexOf(answer["choice"]?.ToString());
            bool calibrated = true;
            if (probabilities == null)
            {
                if (chosen < 0)
                {
                    return null;
                }

                probabilities = OneHot(question.Options.Count, chosen);
                calibrated = false;
            }

            if (chosen < 0)
            {
                chosen = DecisionProbabilityMath.ArgMax(probabilities);
            }

            double confidence = ReadNumber(answer["confidence"]) ?? probabilities[chosen];
            return new DecisionAnswer(question.Id, question.Kind, OptionIds(question), probabilities, chosen,
                confidence, calibrated);
        }

        private static DecisionAnswer ParseScore(DecisionQuestion question, JObject answer)
        {
            double[] probabilities = DecisionProbabilityMath.Renormalize(ReadAligned(answer["probabilities"], question));
            double? score = ReadNumber(answer["score"]);
            bool calibrated = true;
            if (probabilities == null)
            {
                if (!score.HasValue)
                {
                    return null;
                }

                int level = (int)Math.Round(Math.Max(0d, Math.Min(question.Options.Count - 1, score.Value)));
                probabilities = OneHot(question.Options.Count, level);
                calibrated = false;
            }

            int chosen = DecisionProbabilityMath.ArgMax(probabilities);
            double expected = score ?? DecisionProbabilityMath.ExpectedIndex(probabilities);
            double confidence = ReadNumber(answer["confidence"]) ?? probabilities[chosen];
            return new DecisionAnswer(question.Id, question.Kind, OptionIds(question), probabilities, chosen,
                confidence, calibrated, expected);
        }

        private static double[] ReadAligned(JToken token, DecisionQuestion question)
        {
            int count = question.Options.Count;
            if (token is JObject byKey)
            {
                double[] values = new double[count];
                bool any = false;
                for (int i = 0; i < count; i++)
                {
                    double? value = ReadNumber(byKey[question.Options[i].Id]);
                    if (value.HasValue)
                    {
                        values[i] = value.Value;
                        any = true;
                    }
                }

                return any ? values : null;
            }

            if (token is JArray list && list.Count == count)
            {
                double[] values = new double[count];
                for (int i = 0; i < count; i++)
                {
                    values[i] = ReadNumber(list[i]) ?? 0d;
                }

                return values;
            }

            return null;
        }

        private static double[] OneHot(int count, int index)
        {
            double[] values = new double[count];
            values[index] = 1d;
            return values;
        }

        private static IReadOnlyList<string> OptionIds(DecisionQuestion question)
        {
            string[] ids = new string[question.Options.Count];
            for (int i = 0; i < ids.Length; i++)
            {
                ids[i] = question.Options[i].Id;
            }

            return ids;
        }

        private static DecisionResult Malformed(string message, string backend)
        {
            return DecisionResult.Failure(LlmErrorCode.EmptyResponse, DecisionErrorReasons.MalformedResponse,
                message, backend);
        }

        internal static double? ReadNumber(JToken token)
        {
            if (token == null)
            {
                return null;
            }

            double value;
            if (token.Type == JTokenType.Float || token.Type == JTokenType.Integer)
            {
                value = token.Value<double>();
            }
            else if (token.Type == JTokenType.String &&
                     double.TryParse((string)token, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            {
                value = parsed;
            }
            else
            {
                return null;
            }

            return double.IsNaN(value) || double.IsInfinity(value) ? (double?)null : value;
        }

        private static int? ReadInt(JToken token)
        {
            double? value = ReadNumber(token);
            return value.HasValue ? (int)Math.Max(0d, Math.Min(int.MaxValue, value.Value)) : (int?)null;
        }
    }
}
