using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.AI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CoreAI.Ai
{
    /// <summary>
    /// Counts what prompt text, skills and tool contracts cost in tokens with CoreAI's own counter
    /// (<see cref="BpeTokenCounter"/>), so a cost table is reproducible instead of estimated by hand. A tool is
    /// measured as the function definitions a provider receives in the request's <c>tools</c> field.
    /// </summary>
    /// <remarks>
    /// Without tokenizer rank data (<see cref="IBpeRanksProvider"/>) the counter falls back to its heuristic
    /// estimator; <see cref="IsExact"/> says which one produced the numbers. Provider-reported usage
    /// (<see cref="LlmUsageRecord"/>) remains the ground truth for a given model.
    /// </remarks>
    public sealed class PromptTokenCostMeter
    {
        /// <summary>Model whose tokenizer the default meter asks for.</summary>
        public const string DefaultModel = "gpt-4o";

        private readonly ITokenCounter _counter;
        private readonly string _model;

        /// <summary>Creates a meter over <paramref name="counter"/> (default: <see cref="BpeTokenCounter"/> with no rank data).</summary>
        public PromptTokenCostMeter(ITokenCounter counter = null, string model = DefaultModel)
        {
            _counter = counter ?? new BpeTokenCounter();
            _model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model;
        }

        /// <summary>Model the counts are taken for.</summary>
        public string Model => _model;

        /// <summary>
        /// True when the counter tokenized with real rank data; false when it fell back to its estimator.
        /// </summary>
        public bool IsExact
        {
            get
            {
                const string probe = "tokenizer probe";
                int counted = _counter.CountTokens(probe, _model);
                return !(_counter is BpeTokenCounter bpe) || counted != bpe.Fallback.EstimateText(probe);
            }
        }

        /// <summary>Tokens of plain text.</summary>
        public int CountText(string text)
        {
            return _counter.CountTokens(text ?? "", _model);
        }

        /// <summary>Tokens of the function definitions <paramref name="tool"/> adds to a request.</summary>
        public int CountTool(ILlmTool tool)
        {
            return CountText(ToProviderToolsJson(tool));
        }

        /// <summary>
        /// The tool as OpenAI-compatible function definitions (a JSON array, one entry per function the tool
        /// publishes), compact, as a provider receives it in the <c>tools</c> field.
        /// </summary>
        public static string ToProviderToolsJson(ILlmTool tool)
        {
            if (tool == null)
            {
                throw new ArgumentNullException(nameof(tool));
            }

            return ToJson(FunctionContracts(tool));
        }

        /// <summary>One MEAI function as a provider receives it in the <c>tools</c> field (a one-entry JSON array).</summary>
        public static string ToProviderToolsJson(AIFunction function)
        {
            if (function == null)
            {
                throw new ArgumentNullException(nameof(function));
            }

            return ToJson(new[] { (function.Name, function.Description, function.JsonSchema.GetRawText()) });
        }

        private static string ToJson(IEnumerable<(string Name, string Description, string Schema)> contracts)
        {
            JArray definitions = new();
            foreach ((string name, string description, string schema) in contracts)
            {
                JToken parameters;
                try
                {
                    parameters = string.IsNullOrWhiteSpace(schema) ? new JObject() : JToken.Parse(schema);
                }
                catch (JsonException)
                {
                    parameters = new JValue(schema);
                }

                definitions.Add(new JObject
                {
                    ["type"] = "function",
                    ["function"] = new JObject
                    {
                        ["name"] = name ?? "",
                        ["description"] = description ?? "",
                        ["parameters"] = parameters
                    }
                });
            }

            return definitions.ToString(Formatting.None);
        }

        private static IEnumerable<(string Name, string Description, string Schema)> FunctionContracts(ILlmTool tool)
        {
            switch (tool)
            {
                case IAIFunctionsLlmTool many:
                    foreach (AIFunction function in many.CreateAIFunctions())
                    {
                        yield return (function.Name, function.Description, function.JsonSchema.GetRawText());
                    }

                    yield break;
                case IAIFunctionLlmTool one:
                    AIFunction single = one.CreateAIFunction();
                    yield return (single.Name, single.Description, single.JsonSchema.GetRawText());
                    yield break;
                default:
                    yield return (tool.Name, tool.Description, tool.ParametersSchema);
                    yield break;
            }
        }

        /// <summary>Formats one Markdown table row: label, characters, UTF-8 bytes, tokens.</summary>
        public string FormatTextRow(string label, string text)
        {
            text ??= "";
            return "| " + label + " | " + text.Length + " | " + Encoding.UTF8.GetByteCount(text) + " | " +
                   CountText(text) + " |";
        }
    }
}
