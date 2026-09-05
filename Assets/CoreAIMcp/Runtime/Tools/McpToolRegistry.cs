using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CoreAI.Mcp.Tools
{
    /// <summary>
    /// Immutable set of the tools present in the current composition. Built once from whatever CoreAI
    /// services resolved (see <c>CoreAiMcpToolProvider</c>), then queried by the dispatcher for
    /// <c>tools/list</c> and <c>tools/call</c>. Engine-free and trivially testable with fake tools.
    /// <para>
    /// Residency is snapshotted at construction: each tool is classified Native (listed) or Dynamic
    /// (hidden from the listing, served through the broker) exactly once, so the registry stays
    /// immutable and the policy is never re-consulted per request.
    /// </para>
    /// </summary>
    public sealed class McpToolRegistry
    {
        private readonly Dictionary<string, IMcpTool> _byName;
        private readonly List<IMcpTool> _ordered;
        private readonly List<IMcpTool> _native;
        private readonly List<IMcpTool> _dynamic;
        private readonly Dictionary<string, McpToolResidency> _residency;

        /// <param name="tools">Tools to expose. Null or duplicate-named entries are ignored (first wins).</param>
        public McpToolRegistry(IEnumerable<IMcpTool> tools)
            : this(tools, null, false)
        {
        }

        /// <param name="tools">Tools to expose. Null or duplicate-named entries are ignored (first wins).</param>
        /// <param name="residencyPolicy">Host residency policy. Null means the default: every tool Native.</param>
        /// <param name="alwaysIncludeBroker">Append the broker even when no tool is Dynamic.</param>
        public McpToolRegistry(
            IEnumerable<IMcpTool> tools,
            IMcpToolResidencyPolicy residencyPolicy,
            bool alwaysIncludeBroker = false)
        {
            _byName = new Dictionary<string, IMcpTool>();
            _ordered = new List<IMcpTool>();
            _native = new List<IMcpTool>();
            _dynamic = new List<IMcpTool>();
            _residency = new Dictionary<string, McpToolResidency>();
            if (tools != null)
            {
                foreach (IMcpTool tool in tools)
                {
                    if (tool == null || string.IsNullOrEmpty(tool.Name) || _byName.ContainsKey(tool.Name))
                    {
                        continue;
                    }

                    _byName.Add(tool.Name, tool);
                    _ordered.Add(tool);
                    // WHY: the broker is always Native — a host policy must not be able to hide the
                    // very tool clients use to discover hidden tools.
                    McpToolResidency residency = string.Equals(
                        tool.Name, CoreAiToolsBrokerMcpTool.ToolName, System.StringComparison.Ordinal)
                        ? McpToolResidency.Native
                        : residencyPolicy?.ResolveFor(tool) ?? McpToolResidency.Native;
                    _residency.Add(tool.Name, residency);
                    (residency == McpToolResidency.Dynamic ? _dynamic : _native).Add(tool);
                }
            }

            if ((_dynamic.Count > 0 || alwaysIncludeBroker) && !_byName.ContainsKey(CoreAiToolsBrokerMcpTool.ToolName))
            {
                Dictionary<string, IMcpTool> byName = _byName;
                List<IMcpTool> dynamic = _dynamic;
                IMcpTool broker = new CoreAiToolsBrokerMcpTool(
                    name => name != null && byName.TryGetValue(name, out IMcpTool found) ? found : null,
                    () => dynamic.AsReadOnly());
                _byName.Add(broker.Name, broker);
                _ordered.Add(broker);
                _native.Add(broker);
                _residency.Add(broker.Name, McpToolResidency.Native);
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
        /// <remarks>
        /// WHY: hiding a Dynamic tool from the listing is a context optimisation, not access
        /// control — a client that already knows the name can still call it directly, so this
        /// resolves native, dynamic, and broker tools alike.
        /// </remarks>
        public IMcpTool Find(string name)
        {
            if (name != null && _byName.TryGetValue(name, out IMcpTool tool))
            {
                return tool;
            }

            return null;
        }

        /// <summary>Residency of the named tool, or Native when absent.</summary>
        public McpToolResidency ResidencyOf(string name)
        {
            if (name != null && _residency.TryGetValue(name, out McpToolResidency residency))
            {
                return residency;
            }

            return McpToolResidency.Native;
        }

        /// <summary>Dynamic tools in registration order: named by the broker, never listed.</summary>
        public IReadOnlyList<IMcpTool> DynamicTools => _dynamic.AsReadOnly();

        /// <summary>Builds the <c>tools</c> array for a <c>tools/list</c> result: Native tools plus the broker.</summary>
        public JArray ToListJson()
        {
            JArray array = new();
            foreach (IMcpTool tool in _native)
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
