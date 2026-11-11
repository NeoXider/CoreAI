using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CoreAI.Decisions
{
    /// <summary>What a model list says about one model.</summary>
    public sealed class DecisionModelInfo
    {
        /// <summary>Creates a model entry.</summary>
        public DecisionModelInfo(string id, IReadOnlyList<string> outputModalities,
            IReadOnlyList<string> supportedParameters, bool supportedParametersListed)
        {
            Id = id ?? "";
            OutputModalities = outputModalities ?? Array.Empty<string>();
            SupportedParameters = supportedParameters ?? Array.Empty<string>();
            SupportedParametersListed = supportedParametersListed;
        }

        /// <summary>Model id.</summary>
        public string Id { get; }

        /// <summary><c>architecture.output_modalities</c> (or a top-level <c>output_modalities</c>).</summary>
        public IReadOnlyList<string> OutputModalities { get; }

        /// <summary><c>supported_parameters</c> (OpenRouter lists them per model).</summary>
        public IReadOnlyList<string> SupportedParameters { get; }

        /// <summary>True when the server listed supported parameters at all; llama.cpp does not.</summary>
        public bool SupportedParametersListed { get; }

        /// <summary>True when <see cref="DecisionModelListWire.DecisionsModality"/> is an output modality.</summary>
        public bool AdvertisesDecisions => Contains(OutputModalities, DecisionModelListWire.DecisionsModality);

        /// <summary>True when <c>logprobs</c> is a supported parameter.</summary>
        public bool AdvertisesLogprobs => Contains(SupportedParameters, "logprobs");

        private static bool Contains(IReadOnlyList<string> values, string value)
        {
            foreach (string item in values)
            {
                if (string.Equals(item, value, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// The model-list wire for the capability check: <c>GET {base}/models</c> returns
    /// <c>{"data": [{"id", "architecture": {"output_modalities": ["decisions"]}, "supported_parameters": [...]}]}</c>
    /// on llama.cpp and OpenRouter; a native decision model lists <c>"decisions"</c>.
    /// </summary>
    public static class DecisionModelListWire
    {
        /// <summary>The output modality of a decision model.</summary>
        public const string DecisionsModality = "decisions";

        /// <summary>The model-list URL for an OpenAI-style base URL.</summary>
        public static string BuildUrl(string baseUrl)
        {
            return DecisionUrls.Append(baseUrl, "models");
        }

        /// <summary>Parses a model list; a malformed body yields an empty list.</summary>
        public static IReadOnlyList<DecisionModelInfo> Parse(string json)
        {
            List<DecisionModelInfo> models = new();
            JToken root;
            try
            {
                root = JToken.Parse(json ?? "");
            }
            catch (JsonException)
            {
                return models;
            }

            JArray items = root as JArray ?? root?["data"] as JArray ?? root?["models"] as JArray;
            if (items == null)
            {
                return models;
            }

            foreach (JToken item in items)
            {
                if (!(item is JObject model))
                {
                    continue;
                }

                string id = model["id"]?.ToString() ?? model["name"]?.ToString() ?? model["model"]?.ToString() ?? "";
                JToken modalities = model["architecture"]?["output_modalities"] ?? model["output_modalities"];
                JToken parameters = model["supported_parameters"];
                models.Add(new DecisionModelInfo(id.Trim(), Strings(modalities), Strings(parameters),
                    parameters is JArray));
            }

            return models;
        }

        /// <summary>
        /// The entry for <paramref name="modelId"/>; when the server lists exactly one model (a single-model
        /// <c>llama-server</c> ignores the requested id) that one; otherwise null.
        /// </summary>
        public static DecisionModelInfo Find(IReadOnlyList<DecisionModelInfo> models, string modelId)
        {
            if (models == null || models.Count == 0)
            {
                return null;
            }

            string wanted = (modelId ?? "").Trim();
            foreach (DecisionModelInfo model in models)
            {
                if (string.Equals(model.Id, wanted, StringComparison.Ordinal))
                {
                    return model;
                }
            }

            return models.Count == 1 ? models[0] : null;
        }

        private static IReadOnlyList<string> Strings(JToken token)
        {
            List<string> values = new();
            if (token is JArray array)
            {
                foreach (JToken item in array)
                {
                    if (item != null && item.Type == JTokenType.String)
                    {
                        values.Add(((string)item).Trim());
                    }
                }
            }

            return values;
        }
    }
}
