using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CoreAI.Mcp.Tools
{
    /// <summary>
    /// Immutable set of the tools present in the current composition. Built once from whatever CoreAI
    /// services resolved (see <c>CoreAiMcpToolProvider</c>), then queried by the dispatcher for
    /// <c>tools/list</c> and <c>tools/call</c>. Engine-free and trivially testable with fake tools.
    /// </summary>
    public sealed class McpToolRegistry
    {
        private readonly Dictionary<string, IMcpTool> _byName;
        private readonly List<IMcpTool> _ordered;

        /// <param name="tools">Tools to expose. Null or duplicate-named entries are ignored (first wins).</param>
        public McpToolRegistry(IEnumerable<IMcpTool> tools)
        {
            _byName = new Dictionary<string, IMcpTool>();
            _ordered = new List<IMcpTool>();
            if (tools == null)
            {
                return;
            }

            foreach (IMcpTool tool in tools)
            {
                if (tool == null || string.IsNullOrEmpty(tool.Name) || _byName.ContainsKey(tool.Name))
                {
                    continue;
                }

                _byName.Add(tool.Name, tool);
                _ordered.Add(tool);
            }
        }

        /// <summary>Number of registered tools.</summary>
        public int Count => _ordered.Count;

        /// <summary>True when a tool with <paramref name="name"/> is registered.</summary>
        public bool Contains(string name)
        {
            return name != null && _byName.ContainsKey(name);
        }

        /// <summary>Resolves a tool by name, or null when absent.</summary>
        public IMcpTool Find(string name)
        {
            if (name != null && _byName.TryGetValue(name, out IMcpTool tool))
            {
                return tool;
            }

            return null;
        }

        /// <summary>Builds the <c>tools</c> array for a <c>tools/list</c> result.</summary>
        public JArray ToListJson()
        {
            JArray array = new();
            foreach (IMcpTool tool in _ordered)
            {
                JObject schema;
                try
                {
                    schema = JObject.Parse(string.IsNullOrWhiteSpace(tool.InputSchemaJson)
                        ? "{\"type\":\"object\"}"
                        : tool.InputSchemaJson);
                }
                catch (JsonException)
                {
                    // WHY: A tool with a malformed schema must not break the whole list; fall back to a
                    // permissive object schema so the tool is still callable.
                    schema = new JObject { ["type"] = "object" };
                }

                array.Add(new JObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description ?? "",
                    ["inputSchema"] = schema
                });
            }

            return array;
        }
    }
}
