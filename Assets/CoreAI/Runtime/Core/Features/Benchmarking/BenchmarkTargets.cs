using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace CoreAI.Benchmarking
{
    /// <summary>
    /// One model endpoint of a multi-target benchmark run: a model id served by an OpenAI-compatible base URL,
    /// an optional API key and an optional display label. The label names the target in reports, progress and
    /// artifact file names; without one the model id does.
    /// </summary>
    public sealed class BenchmarkTarget
    {
        /// <summary>
        /// Prefix that turns <see cref="ApiKey"/> into a reference to an environment variable
        /// (<c>env:OPENROUTER_API_KEY</c>), so a target list can be stored or logged without the secret.
        /// </summary>
        public const string ApiKeyEnvPrefix = "env:";

        /// <param name="model">Model id sent to the provider.</param>
        /// <param name="baseUrl">OpenAI-compatible base URL.</param>
        /// <param name="apiKey">Literal key, <c>env:VAR</c> reference, or empty.</param>
        /// <param name="label">Optional display label.</param>
        /// <param name="reasoningEffort">
        /// Optional <c>reasoning_effort</c>, stored lower-case; validate it with
        /// <see cref="BenchmarkTargetList.TryNormalizeReasoningEffort"/> first.
        /// </param>
        /// <param name="extraBodyJson">Optional provider body fields for this target: the text of one JSON object.</param>
        public BenchmarkTarget(string model, string baseUrl, string apiKey = "", string label = "",
            string reasoningEffort = "", string extraBodyJson = "", float? temperature = null)
        {
            if (temperature.HasValue && (float.IsNaN(temperature.Value) ||
                float.IsInfinity(temperature.Value) || temperature.Value < 0f || temperature.Value > 2f))
            {
                throw new ArgumentOutOfRangeException(nameof(temperature), "Temperature must be a finite number from 0 to 2.");
            }

            Model = (model ?? "").Trim();
            BaseUrl = BenchmarkTargetList.NormalizeBaseUrl(baseUrl);
            ApiKey = (apiKey ?? "").Trim();
            Label = (label ?? "").Trim();
            ReasoningEffort = (reasoningEffort ?? "").Trim().ToLowerInvariant();
            ExtraBodyJson = (extraBodyJson ?? "").Trim();
            Temperature = temperature;
        }

        /// <summary>Model id sent to the provider.</summary>
        public string Model { get; }

        /// <summary>Absolute http(s) base URL without a trailing slash.</summary>
        public string BaseUrl { get; }

        /// <summary>Literal key, an <see cref="ApiKeyEnvPrefix"/> reference, or empty for keyless servers.</summary>
        public string ApiKey { get; }

        /// <summary>Optional display label; empty when the model id names the target.</summary>
        public string Label { get; }

        /// <summary>This target's own <c>reasoning_effort</c>; empty to use the suite default.</summary>
        public string ReasoningEffort { get; }

        /// <summary>
        /// Provider body fields for this target only (one JSON object, empty when none). The suite validates them
        /// with the provider-body rules, merges them over the shared body and sets the effort on top.
        /// </summary>
        public string ExtraBodyJson { get; }

        /// <summary>Optional sampling temperature (0..2); null leaves the provider/model default unchanged.</summary>
        public float? Temperature { get; }

        /// <summary>The name reports, progress lines and model cards use for this target.</summary>
        public string DisplayName => Label.Length > 0 ? Label : Model;

        /// <summary>True when <see cref="ApiKey"/> names an environment variable instead of holding the key.</summary>
        public bool HasKeyReference => ApiKey.StartsWith(ApiKeyEnvPrefix, StringComparison.OrdinalIgnoreCase);

        /// <summary>True when the label adds information the model id does not carry.</summary>
        public bool HasDistinctLabel => Label.Length > 0 && !string.Equals(Label, Model, StringComparison.Ordinal);

        /// <summary>The key to send: a literal key as is, an <c>env:</c> reference read from the environment.</summary>
        public string ResolveApiKey()
        {
            if (!HasKeyReference)
            {
                return ApiKey;
            }

            string variable = ApiKey.Substring(ApiKeyEnvPrefix.Length).Trim();
            return variable.Length == 0 ? "" : Environment.GetEnvironmentVariable(variable)?.Trim() ?? "";
        }

        /// <summary>This target's effort, else <paramref name="defaultEffort"/> (the suite-wide default).</summary>
        public string EffectiveReasoningEffort(string defaultEffort)
        {
            return ReasoningEffort.Length > 0 ? ReasoningEffort : (defaultEffort ?? "").Trim().ToLowerInvariant();
        }

        internal BenchmarkTarget WithLabel(string label)
        {
            return new BenchmarkTarget(Model, BaseUrl, ApiKey, label, ReasoningEffort, ExtraBodyJson, Temperature);
        }
    }

    /// <summary>
    /// Parses and serializes the target list of a multi-target benchmark run
    /// (<see cref="EnvTargets"/>, the batchmode <c>-coreAiBenchmarkTargets</c> argument and the benchmark window).
    /// <para>Accepted forms:</para>
    /// <list type="bullet">
    ///   <item>JSON array: <c>[{"label":"q4","model":"qwen3.5-4b","baseUrl":"http://127.0.0.1:8831/v1","apiKey":"env:KEY",
    ///   "reasoningEffort":"high","extraBody":{"top_k":20}}]</c>. Keys are case-insensitive; <c>model</c> and <c>baseUrl</c>
    ///   are required, <c>"enabled": false</c> skips an entry. <c>extraBody</c> is a JSON object (or a string holding
    ///   one). Optional <c>temperature</c> (alias <c>temp</c>) is a number from 0 to 2, JSON only.</item>
    ///   <item>Compact: entries separated by <c>;</c> or new lines, each <c>label|model|baseUrl|apiKey|effort</c>. The base
    ///   URL is found by its <c>http://</c>/<c>https://</c> scheme; one field before it is the model, two are label and
    ///   model; after it come the key and then the effort (<c>baseUrl||high</c> sets an effort without a key). Blank
    ///   entries and lines starting with <c>#</c> are ignored.</item>
    ///   <item><c>@path</c>: either form read from a file.</item>
    /// </list>
    /// Targets that end up with the same display name get a <c>#2</c>, <c>#3</c>… label so every report stays distinct.
    /// </summary>
    public static class BenchmarkTargetList
    {
        /// <summary>Env var holding the target list. Empty or unset means the classic single-model run.</summary>
        public const string EnvTargets = "COREAI_BENCHMARK_TARGETS";

        /// <summary>Env var capping how many targets run at the same time. Empty, 0 or invalid means all of them.</summary>
        public const string EnvParallel = "COREAI_BENCHMARK_PARALLEL";

        /// <summary>
        /// Env var with the suite-wide <c>reasoning_effort</c> for every target without its own and for the
        /// single-model run. Empty means the provider default.
        /// </summary>
        public const string EnvReasoningEffort = "COREAI_BENCHMARK_REASONING_EFFORT";

        /// <summary>The provider body parameter an effort becomes.</summary>
        public const string ReasoningEffortBodyKey = "reasoning_effort";

        /// <summary>Accepted <c>reasoning_effort</c> values, lowest first.</summary>
        public static readonly IReadOnlyList<string> ReasoningEfforts = new[] { "none", "low", "medium", "high", "xhigh" };

        /// <summary>
        /// Validates an effort: empty means "not set" and is accepted as <c>""</c>; otherwise one of
        /// <see cref="ReasoningEfforts"/>, case-insensitive, returned lower-case.
        /// </summary>
        public static bool TryNormalizeReasoningEffort(string raw, out string effort)
        {
            string trimmed = (raw ?? "").Trim().ToLowerInvariant();
            foreach (string allowed in ReasoningEfforts)
            {
                if (trimmed.Length == 0 || trimmed == allowed)
                {
                    effort = trimmed;
                    return true;
                }
            }

            effort = "";
            return false;
        }

        /// <summary>The message for an effort that <see cref="TryNormalizeReasoningEffort"/> rejected.</summary>
        public static string ReasoningEffortError(string raw)
        {
            return $"reasoning effort '{(raw ?? "").Trim()}' must be one of {string.Join(", ", ReasoningEfforts)}";
        }

        /// <summary>Parses an optional temperature for a target row; empty means provider default.</summary>
        public static bool TryParseTemperature(string raw, out float? temperature)
        {
            temperature = null;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return true;
            }

            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ||
                double.IsNaN(value) || double.IsInfinity(value) || value < 0 || value > 2)
            {
                return false;
            }

            temperature = (float)value;
            return true;
        }

        /// <summary>Parses <paramref name="spec"/>; an empty spec yields an empty list.</summary>
        /// <exception cref="FormatException">The spec is malformed; the message names every bad entry.</exception>
        public static List<BenchmarkTarget> Parse(string spec)
        {
            if (!TryParse(spec, out List<BenchmarkTarget> targets, out string error))
            {
                throw new FormatException(error);
            }

            return targets;
        }

        /// <summary>Parses <paramref name="spec"/> without throwing; <paramref name="error"/> lists every problem found.</summary>
        public static bool TryParse(string spec, out List<BenchmarkTarget> targets, out string error)
        {
            targets = new List<BenchmarkTarget>();
            error = null;
            string text = (spec ?? "").Trim();
            if (text.Length == 0)
            {
                return true;
            }

            if (text[0] == '@')
            {
                string path = text.Substring(1).Trim().Trim('"');
                try
                {
                    text = File.ReadAllText(path).Trim();
                }
                catch (Exception ex)
                {
                    error = $"cannot read the target file '{path}': {ex.Message}";
                    return false;
                }

                if (text.Length == 0)
                {
                    return true;
                }
            }

            List<string> problems = new();
            List<BenchmarkTarget> parsed = text[0] == '[' ? ParseJson(text, problems) : ParseCompact(text, problems);
            if (problems.Count > 0)
            {
                error = "Invalid benchmark target list: " + string.Join("; ", problems);
                return false;
            }

            targets = DisambiguateDisplayNames(parsed);
            return true;
        }

        /// <summary>
        /// Serializes <paramref name="targets"/> as the JSON form <see cref="Parse"/> reads back. A literal key is
        /// written only when <paramref name="includeLiteralKeys"/> is true; an <c>env:</c> reference always is.
        /// </summary>
        public static string ToJson(IReadOnlyList<BenchmarkTarget> targets, bool includeLiteralKeys)
        {
            StringBuilder sb = new();
            sb.Append('[');
            for (int i = 0; i < (targets?.Count ?? 0); i++)
            {
                BenchmarkTarget t = targets[i];
                if (i > 0)
                {
                    sb.Append(',');
                }

                sb.Append("{\"model\":").Append(Quote(t.Model)).Append(",\"baseUrl\":").Append(Quote(t.BaseUrl));
                if (t.Label.Length > 0)
                {
                    sb.Append(",\"label\":").Append(Quote(t.Label));
                }

                if (t.ApiKey.Length > 0 && (includeLiteralKeys || t.HasKeyReference))
                {
                    sb.Append(",\"apiKey\":").Append(Quote(t.ApiKey));
                }

                if (t.ReasoningEffort.Length > 0)
                {
                    sb.Append(",\"reasoningEffort\":").Append(Quote(t.ReasoningEffort));
                }

                if (t.ExtraBodyJson.Length > 0)
                {
                    sb.Append(",\"extraBody\":").Append(t.ExtraBodyJson);
                }

                if (t.Temperature.HasValue)
                {
                    sb.Append(",\"temperature\":").Append(t.Temperature.Value.ToString("R", CultureInfo.InvariantCulture));
                }

                sb.Append('}');
            }

            return sb.Append(']').ToString();
        }

        /// <summary>
        /// How many targets run at the same time: <paramref name="raw"/> clamped to 1..<paramref name="targetCount"/>;
        /// empty, 0 or anything unparsable means all targets.
        /// </summary>
        public static int ResolveParallelism(string raw, int targetCount)
        {
            if (targetCount <= 0)
            {
                return 0;
            }

            if (int.TryParse((raw ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                && value >= 1)
            {
                return Math.Min(value, targetCount);
            }

            return targetCount;
        }

        /// <summary>Trims the URL and drops trailing slashes; returns "" for a blank value.</summary>
        public static string NormalizeBaseUrl(string baseUrl)
        {
            return string.IsNullOrWhiteSpace(baseUrl) ? "" : baseUrl.Trim().TrimEnd('/');
        }

        /// <summary>True for an absolute http or https URL.</summary>
        public static bool IsHttpUrl(string value)
        {
            return Uri.TryCreate((value ?? "").Trim(), UriKind.Absolute, out Uri uri)
                   && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }

        private static List<BenchmarkTarget> ParseCompact(string text, List<string> problems)
        {
            List<BenchmarkTarget> targets = new();
            string[] entries = text.Split(new[] { ';', '\n', '\r' }, StringSplitOptions.None);
            int number = 0;
            foreach (string rawEntry in entries)
            {
                string entry = rawEntry.Trim();
                if (entry.Length == 0 || entry[0] == '#')
                {
                    continue;
                }

                number++;
                string[] fields = entry.Split('|');
                for (int i = 0; i < fields.Length; i++)
                {
                    fields[i] = fields[i].Trim();
                }

                int urlIndex = -1;
                int urlCount = 0;
                for (int i = 0; i < fields.Length; i++)
                {
                    if (IsHttpUrl(fields[i]))
                    {
                        urlIndex = urlIndex < 0 ? i : urlIndex;
                        urlCount++;
                    }
                }

                if (urlCount == 0)
                {
                    problems.Add($"entry {number} '{Redact(entry)}' has no http(s) base URL");
                    continue;
                }

                if (urlCount > 1)
                {
                    problems.Add($"entry {number} '{Redact(entry)}' has more than one base URL");
                    continue;
                }

                int afterUrl = fields.Length - urlIndex - 1;
                if (urlIndex == 0 || urlIndex > 2 || afterUrl > 2)
                {
                    problems.Add($"entry {number} '{Redact(entry)}' must be label|model|baseUrl|apiKey|effort " +
                                 "(label, apiKey and effort optional)");
                    continue;
                }

                string label = urlIndex == 2 ? fields[0] : "";
                string model = fields[urlIndex - 1];
                string key = afterUrl >= 1 ? fields[urlIndex + 1] : "";
                string effort = afterUrl == 2 ? fields[urlIndex + 2] : "";
                // WHY: "baseUrl|high" would silently send "high" as the API key; the effort has its own field.
                if (afterUrl == 1 && key.Length > 0 && IsReasoningEffortWord(key))
                {
                    problems.Add($"entry {number} '{Redact(entry)}' is ambiguous: write baseUrl||{key.ToLowerInvariant()} " +
                                 "for an effort without a key");
                    continue;
                }

                AddValidated(targets, problems, number, model, fields[urlIndex], key, label, effort, null);
            }

            return targets;
        }

        private static List<BenchmarkTarget> ParseJson(string text, List<string> problems)
        {
            List<BenchmarkTarget> targets = new();
            object root;
            MiniJsonReader reader = new(text);
            try
            {
                root = reader.ReadDocument();
            }
            catch (FormatException ex)
            {
                problems.Add("malformed JSON: " + ex.Message);
                return targets;
            }

            if (root is not List<object> array)
            {
                problems.Add("the JSON form must be an array of target objects");
                return targets;
            }

            for (int i = 0; i < array.Count; i++)
            {
                int number = i + 1;
                if (array[i] is not Dictionary<string, object> obj)
                {
                    problems.Add($"entry {number} is not a JSON object");
                    continue;
                }

                string model = null;
                string baseUrl = null;
                string key = null;
                string label = null;
                string effort = null;
                string extraBody = null;
                float? temperature = null;
                bool enabled = true;
                bool valid = true;
                foreach (KeyValuePair<string, object> property in obj)
                {
                    switch (property.Key.ToLowerInvariant())
                    {
                        case "model":
                        case "modelid":
                        case "model_id":
                            valid &= ReadString(property, number, problems, out model);
                            break;
                        case "baseurl":
                        case "base_url":
                        case "url":
                        case "endpoint":
                            valid &= ReadString(property, number, problems, out baseUrl);
                            break;
                        case "apikey":
                        case "api_key":
                        case "key":
                            valid &= ReadString(property, number, problems, out key);
                            break;
                        case "label":
                        case "name":
                            valid &= ReadString(property, number, problems, out label);
                            break;
                        case "reasoningeffort":
                        case "reasoning_effort":
                        case "effort":
                            valid &= ReadString(property, number, problems, out effort);
                            break;
                        case "extrabody":
                        case "extra_body":
                            valid &= ReadExtraBody(property, number, problems, reader, out extraBody);
                            break;
                        case "temperature":
                        case "temp":
                            if (property.Value == null)
                            {
                                temperature = null;
                            }
                            else if (property.Value is double value && !double.IsNaN(value) &&
                                     !double.IsInfinity(value) && value >= 0 && value <= 2)
                            {
                                temperature = (float)value;
                            }
                            else
                            {
                                problems.Add($"entry {number}: '{property.Key}' must be a number from 0 to 2");
                                valid = false;
                            }

                            break;
                        case "enabled":
                            if (property.Value is bool flag)
                            {
                                enabled = flag;
                            }
                            else
                            {
                                problems.Add($"entry {number}: 'enabled' must be true or false");
                                valid = false;
                            }

                            break;
                        default:
                            problems.Add($"entry {number}: unknown property '{property.Key}'");
                            valid = false;
                            break;
                    }
                }

                if (valid && enabled)
                {
                    AddValidated(targets, problems, number, model, baseUrl, key, label, effort, extraBody, temperature);
                }
            }

            return targets;
        }

        /// <summary>
        /// Reads <c>extraBody</c>: a JSON object, or a string holding one (the form a text field produces). The
        /// object's own text is kept so the suite can validate it with the provider-body rules.
        /// </summary>
        private static bool ReadExtraBody(KeyValuePair<string, object> property, int number, List<string> problems,
            MiniJsonReader reader, out string extraBody)
        {
            extraBody = null;
            switch (property.Value)
            {
                case null:
                    extraBody = "";
                    return true;
                case Dictionary<string, object> obj:
                    extraBody = reader.RawTextOf(obj);
                    return true;
                case string text when text.Trim().Length == 0:
                    extraBody = "";
                    return true;
                case string text:
                    if (TryReadJsonObjectText(text, out string objectText))
                    {
                        extraBody = objectText;
                        return true;
                    }

                    break;
            }

            problems.Add($"entry {number}: '{property.Key}' must be a JSON object");
            return false;
        }

        /// <summary>
        /// True when <paramref name="json"/> is one JSON object; <paramref name="objectText"/> is its trimmed text.
        /// Empty input is accepted as "no object".
        /// </summary>
        public static bool TryReadJsonObjectText(string json, out string objectText)
        {
            objectText = "";
            string trimmed = (json ?? "").Trim();
            if (trimmed.Length == 0)
            {
                return true;
            }

            try
            {
                if (new MiniJsonReader(trimmed).ReadDocument() is Dictionary<string, object>)
                {
                    objectText = trimmed;
                    return true;
                }
            }
            catch (FormatException)
            {
                return false;
            }

            return false;
        }

        private static bool IsReasoningEffortWord(string value)
        {
            string trimmed = (value ?? "").Trim();
            return trimmed.Length > 0 && TryNormalizeReasoningEffort(trimmed, out _);
        }

        private static bool ReadString(KeyValuePair<string, object> property, int number, List<string> problems,
            out string value)
        {
            value = property.Value switch
            {
                null => "",
                string s => s,
                _ => null
            };
            if (value != null)
            {
                return true;
            }

            problems.Add($"entry {number}: '{property.Key}' must be a string");
            return false;
        }

        private static void AddValidated(List<BenchmarkTarget> targets, List<string> problems, int number,
            string model, string baseUrl, string key, string label, string effort, string extraBody,
            float? temperature = null)
        {
            if (string.IsNullOrWhiteSpace(model))
            {
                problems.Add($"entry {number} has no model id");
                return;
            }

            if (!IsHttpUrl(baseUrl))
            {
                problems.Add($"entry {number} ({model.Trim()}) needs an absolute http(s) base URL, got " +
                             $"'{(baseUrl ?? "").Trim()}'");
                return;
            }

            if (!TryNormalizeReasoningEffort(effort, out string normalizedEffort))
            {
                problems.Add($"entry {number} ({model.Trim()}): {ReasoningEffortError(effort)}");
                return;
            }

            targets.Add(new BenchmarkTarget(model, baseUrl, key, label, normalizedEffort, extraBody, temperature));
        }

        private static List<BenchmarkTarget> DisambiguateDisplayNames(List<BenchmarkTarget> parsed)
        {
            List<BenchmarkTarget> result = new(parsed.Count);
            HashSet<string> used = new(StringComparer.OrdinalIgnoreCase);
            foreach (BenchmarkTarget target in parsed)
            {
                BenchmarkTarget unique = target;
                for (int n = 2; !used.Add(unique.DisplayName); n++)
                {
                    unique = target.WithLabel($"{target.DisplayName}#{n}");
                }

                result.Add(unique);
            }

            return result;
        }

        /// <summary>Keeps keys out of error messages: anything after the URL field is replaced.</summary>
        private static string Redact(string entry)
        {
            string[] fields = entry.Split('|');
            for (int i = 0; i < fields.Length; i++)
            {
                if (IsHttpUrl(fields[i].Trim()) && i + 1 < fields.Length)
                {
                    return string.Join("|", fields, 0, i + 1) + "|***";
                }
            }

            return entry;
        }

        private static string Quote(string value)
        {
            StringBuilder sb = new("\"");
            foreach (char c in value ?? "")
            {
                switch (c)
                {
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    default:
                        if (c < ' ')
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }

                        break;
                }
            }

            return sb.Append('"').ToString();
        }

        /// <summary>
        /// Minimal JSON reader for the target list. WHY: this assembly has no engine or package references,
        /// so it cannot use Newtonsoft or System.Text.Json; the list is small and only needs objects, arrays,
        /// strings, numbers, booleans and null.
        /// </summary>
        private sealed class MiniJsonReader
        {
            private readonly string _text;
            private readonly Dictionary<object, string> _objectText = new();
            private int _pos;

            public MiniJsonReader(string text)
            {
                _text = text;
            }

            /// <summary>The source text of an object this reader produced.</summary>
            public string RawTextOf(Dictionary<string, object> obj)
            {
                return obj != null && _objectText.TryGetValue(obj, out string text) ? text : "";
            }

            public object ReadDocument()
            {
                object value = ReadValue();
                SkipWhitespace();
                if (_pos < _text.Length)
                {
                    throw Error("unexpected text after the document");
                }

                return value;
            }

            private object ReadValue()
            {
                SkipWhitespace();
                if (_pos >= _text.Length)
                {
                    throw Error("unexpected end of input");
                }

                char c = _text[_pos];
                switch (c)
                {
                    case '{':
                        return ReadObject();
                    case '[':
                        return ReadArray();
                    case '"':
                        return ReadString();
                    case 't':
                        Expect("true");
                        return true;
                    case 'f':
                        Expect("false");
                        return false;
                    case 'n':
                        Expect("null");
                        return null;
                    default:
                        if (c == '-' || (c >= '0' && c <= '9'))
                        {
                            return ReadNumber();
                        }

                        throw Error($"unexpected character '{c}'");
                }
            }

            private Dictionary<string, object> ReadObject()
            {
                int start = _pos;
                Dictionary<string, object> obj = ReadObjectBody();
                _objectText[obj] = _text.Substring(start, _pos - start);
                return obj;
            }

            private Dictionary<string, object> ReadObjectBody()
            {
                Dictionary<string, object> obj = new(StringComparer.Ordinal);
                _pos++;
                SkipWhitespace();
                if (Peek() == '}')
                {
                    _pos++;
                    return obj;
                }

                while (true)
                {
                    SkipWhitespace();
                    if (Peek() != '"')
                    {
                        throw Error("expected a property name");
                    }

                    string name = ReadString();
                    SkipWhitespace();
                    if (Peek() != ':')
                    {
                        throw Error("expected ':'");
                    }

                    _pos++;
                    if (obj.ContainsKey(name))
                    {
                        throw Error($"duplicate property '{name}'");
                    }

                    obj[name] = ReadValue();
                    SkipWhitespace();
                    char next = Peek();
                    _pos++;
                    if (next == '}')
                    {
                        return obj;
                    }

                    if (next != ',')
                    {
                        throw Error("expected ',' or '}'");
                    }
                }
            }

            private List<object> ReadArray()
            {
                List<object> list = new();
                _pos++;
                SkipWhitespace();
                if (Peek() == ']')
                {
                    _pos++;
                    return list;
                }

                while (true)
                {
                    list.Add(ReadValue());
                    SkipWhitespace();
                    char next = Peek();
                    _pos++;
                    if (next == ']')
                    {
                        return list;
                    }

                    if (next != ',')
                    {
                        throw Error("expected ',' or ']'");
                    }
                }
            }

            private string ReadString()
            {
                StringBuilder sb = new();
                _pos++;
                while (_pos < _text.Length)
                {
                    char c = _text[_pos++];
                    if (c == '"')
                    {
                        return sb.ToString();
                    }

                    if (c != '\\')
                    {
                        sb.Append(c);
                        continue;
                    }

                    if (_pos >= _text.Length)
                    {
                        break;
                    }

                    char escape = _text[_pos++];
                    switch (escape)
                    {
                        case '"':
                        case '\\':
                        case '/':
                            sb.Append(escape);
                            break;
                        case 'b':
                            sb.Append('\b');
                            break;
                        case 'f':
                            sb.Append('\f');
                            break;
                        case 'n':
                            sb.Append('\n');
                            break;
                        case 'r':
                            sb.Append('\r');
                            break;
                        case 't':
                            sb.Append('\t');
                            break;
                        case 'u':
                            if (_pos + 4 > _text.Length || !int.TryParse(_text.Substring(_pos, 4),
                                    NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code))
                            {
                                throw Error("bad \\u escape");
                            }

                            sb.Append((char)code);
                            _pos += 4;
                            break;
                        default:
                            throw Error($"bad escape '\\{escape}'");
                    }
                }

                throw Error("unterminated string");
            }

            private double ReadNumber()
            {
                int start = _pos;
                while (_pos < _text.Length && "+-0123456789.eE".IndexOf(_text[_pos]) >= 0)
                {
                    _pos++;
                }

                if (!double.TryParse(_text.Substring(start, _pos - start), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out double number))
                {
                    throw Error("bad number");
                }

                return number;
            }

            private void Expect(string literal)
            {
                if (string.CompareOrdinal(_text, _pos, literal, 0, literal.Length) != 0)
                {
                    throw Error($"expected '{literal}'");
                }

                _pos += literal.Length;
            }

            private char Peek()
            {
                return _pos < _text.Length ? _text[_pos] : '\0';
            }

            private void SkipWhitespace()
            {
                while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos]))
                {
                    _pos++;
                }
            }

            private FormatException Error(string message)
            {
                return new FormatException($"{message} at position {_pos}");
            }
        }
    }
}
