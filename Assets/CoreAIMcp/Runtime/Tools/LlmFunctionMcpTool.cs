using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Mcp.Protocol;
using Microsoft.Extensions.AI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CoreAI.Mcp.Tools
{
    /// <summary>
    /// One function of an in-game agent's tool (<see cref="ILlmTool"/>) offered over MCP unchanged: the same name,
    /// description and JSON schema the model sees, bound through the same MEAI function, so an external harness
    /// calls it with the same arguments and gets the same result. A result with images
    /// (<see cref="LlmToolImageResult"/>, the camera) becomes MCP image content after its text; a JSON result whose
    /// <c>success</c> or <c>ok</c> is false is an <c>isError</c> result with its text unchanged.
    /// </summary>
    public sealed class LlmFunctionMcpTool : IMcpTool
    {
        private readonly AIFunction _function;
        private readonly IJsonInvocableLlmTool _jsonTool;
        private readonly string _name;
        private readonly string _description;
        private readonly string _schema;

        /// <param name="function">The MEAI function to call; null with <paramref name="jsonTool"/> calls that instead.</param>
        /// <param name="jsonTool">A tool invoked with the arguments' JSON text when it has no MEAI function.</param>
        public LlmFunctionMcpTool(AIFunction function, IJsonInvocableLlmTool jsonTool = null)
        {
            if (function == null && jsonTool == null)
            {
                throw new ArgumentNullException(nameof(function));
            }

            _function = function;
            _jsonTool = function == null ? jsonTool : null;
            _name = function?.Name ?? jsonTool.Name;
            _description = function?.Description ?? jsonTool.Description ?? "";
            _schema = ObjectSchema(function != null ? function.JsonSchema.GetRawText() : jsonTool.ParametersSchema);
        }

        // WHY: MCP requires an object input schema, and one malformed in-game schema must not reject the whole
        // catalog; an unusable schema becomes the empty object schema and the tool's own validation still applies.
        internal static string ObjectSchema(string schema)
        {
            const string empty = "{\"type\":\"object\"}";
            if (string.IsNullOrWhiteSpace(schema))
            {
                return empty;
            }

            try
            {
                if (!(JToken.Parse(schema) is JObject parsed))
                {
                    return empty;
                }

                JToken type = parsed["type"];
                if (type == null)
                {
                    parsed["type"] = "object";
                }
                else if (type.Type != JTokenType.String || (string)type != "object")
                {
                    return empty;
                }

                return parsed.ToString(Formatting.None);
            }
            catch (Newtonsoft.Json.JsonException)
            {
                return empty;
            }
        }

        /// <inheritdoc />
        public string Name => _name;

        /// <inheritdoc />
        public string Description => _description;

        /// <inheritdoc />
        public string InputSchemaJson => _schema;

        /// <inheritdoc />
        // WHY no ConfigureAwait(false): in-game tools resume on the game thread's context (the camera, the world), and
        // in a WebGL player a context-free continuation would go to a thread pool that is not there.
        public async Task<McpToolResult> InvokeAsync(JObject arguments, CancellationToken cancellationToken)
        {
            object result = _function != null
                ? await _function.InvokeAsync(ToArguments(arguments), cancellationToken)
                : await _jsonTool.InvokeJsonAsync((arguments ?? new JObject()).ToString(Formatting.None),
                    cancellationToken);
            return ToResult(result);
        }

        /// <summary>The MEAI arguments for an MCP arguments object: every value as a JSON element, as a model sends it.</summary>
        internal static AIFunctionArguments ToArguments(JObject arguments)
        {
            AIFunctionArguments converted = new();
            if (arguments == null)
            {
                return converted;
            }

            foreach (JProperty property in arguments.Properties())
            {
                using JsonDocument document = JsonDocument.Parse(property.Value.ToString(Formatting.None));
                converted[property.Name] = document.RootElement.Clone();
            }

            return converted;
        }

        /// <summary>The MCP result for what an in-game function returned.</summary>
        internal static McpToolResult ToResult(object result)
        {
            if (result is LlmToolImageResult images)
            {
                List<McpContent> content = new() { McpContent.CreateText(images.Text) };
                foreach (AiAttachment image in images.Images)
                {
                    byte[] data = image?.Data;
                    if (data != null && data.Length > 0)
                    {
                        content.Add(McpContent.CreateImage(Convert.ToBase64String(data),
                            string.IsNullOrEmpty(image.MediaType) ? "image/png" : image.MediaType));
                    }
                }

                return new McpToolResult(content, ReportsFailure(images.Text));
            }

            string text = result switch
            {
                null => "",
                string s => s,
                JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
                JsonElement element => element.GetRawText(),
                _ => JsonConvert.SerializeObject(result)
            };
            return new McpToolResult(new[] { McpContent.CreateText(text) }, ReportsFailure(text));
        }

        // WHY both names: the in-game tools answer either {"success": false, ...} or {"ok": false, ...}.
        private static bool ReportsFailure(string text)
        {
            string trimmed = text?.TrimStart() ?? "";
            if (trimmed.Length == 0 || trimmed[0] != '{')
            {
                return false;
            }

            try
            {
                JObject json = JObject.Parse(text);
                return IsFalse(json["success"]) || IsFalse(json["ok"]);
            }
            catch (Newtonsoft.Json.JsonException)
            {
                return false;
            }
        }

        private static bool IsFalse(JToken token) => token?.Type == JTokenType.Boolean && !(bool)token;
    }
}
