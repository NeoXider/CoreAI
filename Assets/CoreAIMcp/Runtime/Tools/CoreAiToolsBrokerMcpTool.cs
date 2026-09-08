using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Mcp.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CoreAI.Mcp.Tools
{
    /// <summary>
    /// The always-Native broker over Dynamic tools: lists their names plus one-line descriptions
    /// (no schemas), returns one tool's full JSON Schema, and forwards calls.
    /// FOR: paying a tool's full schema cost only on the turns that touch it, instead of on every
    /// <c>tools/list</c>. A call naming a Native tool still works — see the WHY below.
    /// </summary>
    public sealed class CoreAiToolsBrokerMcpTool : IMcpTool
    {
        /// <summary>Tool name exposed in <c>tools/list</c> and matched by <c>tools/call</c>.</summary>
        public const string ToolName = "coreai_tools";

        private readonly Func<string, IMcpTool> _find;
        private readonly Func<IReadOnlyList<IMcpTool>> _listDynamic;

        /// <param name="find">Resolves any registered tool by name (native or dynamic).</param>
        /// <param name="listDynamic">Lists the currently dynamic tools, in registration order.</param>
        public CoreAiToolsBrokerMcpTool(
            Func<string, IMcpTool> find,
            Func<IReadOnlyList<IMcpTool>> listDynamic)
        {
            _find = find;
            _listDynamic = listDynamic;
        }

        /// <inheritdoc />
        public string Name => ToolName;

        /// <inheritdoc />
        public string Description =>
            "Reach the tools hidden from tools/list to save context. " +
            "action='list' names them (optional 'query' filters by substring over name and description); " +
            "action='describe' returns one tool's full JSON Schema (pass 'tool'); " +
            "action='call' invokes one (pass 'tool' and 'arguments_json', a JSON object string).";

        /// <inheritdoc />
        public string InputSchemaJson =>
            "{\"type\":\"object\"," +
            "\"properties\":{" +
            "\"action\":{\"type\":\"string\",\"description\":\"One of: list, describe, call.\"}," +
            "\"query\":{\"type\":\"string\",\"description\":\"Optional substring filter for action=list.\"}," +
            "\"tool\":{\"type\":\"string\",\"description\":\"Tool name for action=describe and action=call.\"}," +
            "\"arguments_json\":{\"type\":\"string\",\"description\":\"JSON object string with the tool arguments for action=call.\"}}," +
            "\"required\":[\"action\"]}";

        /// <inheritdoc />
        public async Task<McpToolResult> InvokeAsync(JObject arguments, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string action = McpArguments.String(arguments, "action", null)?.Trim().ToLowerInvariant();
            switch (action)
            {
                case "list":
                    return List(McpArguments.String(arguments, "query", null));
                case "describe":
                    return Describe(McpArguments.String(arguments, "tool", null));
                case "call":
                    return await CallAsync(
                        McpArguments.String(arguments, "tool", null),
                        McpArguments.String(arguments, "arguments_json", null),
                        cancellationToken).ConfigureAwait(false);
                default:
                    return McpToolResult.Failure(JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "action is required and must be one of: list, describe, call.",
                    }));
            }
        }

        private McpToolResult List(string query)
        {
            List<object> tools = new();
            foreach (IMcpTool tool in _listDynamic?.Invoke() ?? Array.Empty<IMcpTool>())
            {
                if (tool == null || string.IsNullOrEmpty(tool.Name))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(query) &&
                    (tool.Name.IndexOf(query.Trim(), StringComparison.OrdinalIgnoreCase) < 0 &&
                     (tool.Description ?? "").IndexOf(query.Trim(), StringComparison.OrdinalIgnoreCase) < 0))
                {
                    continue;
                }

                tools.Add(new { name = tool.Name, description = tool.Description ?? "" });
            }

            return McpToolResult.Text(JsonConvert.SerializeObject(new
            {
                success = true,
                tools,
            }));
        }

        private McpToolResult Describe(string toolName)
        {
            IMcpTool tool = string.IsNullOrWhiteSpace(toolName) ? null : _find?.Invoke(toolName.Trim());
            if (tool == null)
            {
                return UnknownTool(toolName);
            }

            JObject schema;
            try
            {
                schema = JObject.Parse(string.IsNullOrWhiteSpace(tool.InputSchemaJson)
                    ? "{\"type\":\"object\"}"
                    : tool.InputSchemaJson);
            }
            catch (JsonException)
            {
                // WHY: mirror the registry's leniency — a malformed schema must not break discovery.
                schema = new JObject { ["type"] = "object" };
            }

            return McpToolResult.Text(JsonConvert.SerializeObject(new
            {
                success = true,
                name = tool.Name,
                description = tool.Description ?? "",
                inputSchema = schema,
            }));
        }

        private async Task<McpToolResult> CallAsync(
            string toolName, string argumentsJson, CancellationToken cancellationToken)
        {
            CallBinding binding = PrepareCall(toolName, argumentsJson, _find, _listDynamic?.Invoke());
            return await binding.InvokeAsync(cancellationToken).ConfigureAwait(false);
        }

        internal static CallBinding PrepareCall(string toolName, string argumentsJson,
            Func<string, IMcpTool> find, IReadOnlyList<IMcpTool> dynamicTools)
        {
            if (string.Equals(toolName?.Trim(), ToolName, StringComparison.Ordinal))
                return new CallBinding(null, null, null, McpToolResult.Failure("The discovery broker cannot call itself."));

            // WHY: a Native tool called through the broker still runs. The model generalises by
            // analogy and wraps known tools in the broker; refusing is pedantry that costs a turn.
            // Unknown names fail listing the available dynamic names so the model can recover.
            JObject parsed;
            try
            {
                parsed = string.IsNullOrWhiteSpace(argumentsJson)
                    ? new JObject()
                    : JObject.Parse(argumentsJson);
            }
            catch (JsonException ex)
            {
                return new CallBinding(null, null, null, McpToolResult.Failure(JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = $"arguments_json is not a JSON object: {ex.Message}.",
                })));
            }

            string name = toolName?.Trim();
            IMcpTool tool = string.IsNullOrWhiteSpace(name) ? null : find?.Invoke(name);
            return new CallBinding(name, tool, parsed, tool == null ? UnknownTool(name, dynamicTools) : null);
        }

        internal sealed class CallBinding
        {
            private readonly JObject _arguments;
            private readonly McpToolResult _failure;
            internal CallBinding(string name, IMcpTool tool, JObject arguments, McpToolResult failure)
            { TargetName = name; Tool = tool; _arguments = arguments; _failure = failure; }
            internal string TargetName { get; }
            internal IMcpTool Tool { get; }
            internal async Task<McpToolResult> InvokeAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_failure != null) return _failure;
                McpToolResult result = await Tool.InvokeAsync(_arguments, cancellationToken).ConfigureAwait(false);
                return result ?? McpToolResult.Failure($"Tool '{Tool.Name}' returned no result.");
            }
        }

        private McpToolResult UnknownTool(string toolName) => UnknownTool(toolName, _listDynamic?.Invoke());

        private static McpToolResult UnknownTool(string toolName, IReadOnlyList<IMcpTool> dynamicTools)
        {
            List<string> available = new();
            foreach (IMcpTool tool in dynamicTools ?? Array.Empty<IMcpTool>())
            {
                if (tool != null && !string.IsNullOrEmpty(tool.Name))
                {
                    available.Add(tool.Name);
                }
            }

            return McpToolResult.Failure(JsonConvert.SerializeObject(new
            {
                success = false,
                error = $"Tool '{(toolName ?? "").Trim()}' not found.",
                available,
            }));
        }
    }
}
