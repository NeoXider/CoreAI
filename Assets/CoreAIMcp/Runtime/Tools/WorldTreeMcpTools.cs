using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Mcp.Protocol;
using CoreAI.Mods.Rbx.Instances;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CoreAI.Mcp.Tools
{
    /// <summary>
    /// MCP <c>world_tree</c> and <c>instance_get</c>: the running world's instance tree (Roblox Explorer's view) in
    /// pages, filtered by path, class and name, and one instance's properties, attributes and tags. Read-only; reads
    /// the live <see cref="RbxDataModel"/> on the game thread.
    /// </summary>
    public static class WorldTreeMcpTools
    {
        /// <summary>Builds both tools over the world <paramref name="game"/> returns; null returns none.</summary>
        /// <param name="game">The running world's DataModel, read at every call (null while no world runs).</param>
        /// <param name="registry">The world's registry, for id lookup and the owning mod; may return null.</param>
        public static IReadOnlyList<IMcpTool> Create(Func<RbxDataModel> game, Func<InstanceRegistry> registry = null)
        {
            if (game == null)
            {
                return Array.Empty<IMcpTool>();
            }

            return new IMcpTool[] { new WorldTreeMcpTool(game, registry), new InstanceGetMcpTool(game, registry) };
        }

        internal const string NoWorld = "No world runs here (no RbxWorldHost has a DataModel).";

        /// <summary>
        /// Finds an instance by its full name (<c>Workspace.Model.Part</c>, as <c>GetFullName</c> writes it; <c>game</c> or
        /// empty is the DataModel) or by id (<c>id:123</c>). A name with dots is found when no shorter split matches.
        /// </summary>
        public static RbxInstance Resolve(RbxDataModel game, InstanceRegistry registry, string path, out string error)
        {
            error = "";
            string text = (path ?? "").Trim();
            if (text.Length == 0 || text == "game" || text == "Game")
            {
                return game;
            }

            if (text.StartsWith("id:", StringComparison.OrdinalIgnoreCase))
            {
                if (registry != null &&
                    ulong.TryParse(text.Substring(3), NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong raw) &&
                    registry.TryGet(new InstanceId(raw), out RbxInstance byId) && byId != null && !byId.IsDestroyed)
                {
                    return byId;
                }

                error = $"No instance with {text}.";
                return null;
            }

            if (text.StartsWith("game.", StringComparison.Ordinal)) text = text.Substring(5);
            RbxInstance found = Walk(game, text);
            if (found == null)
            {
                error = $"No instance at '{path}'. Paths are GetFullName() names (Workspace.Model.Part) or id:<n>.";
            }

            return found;
        }

        private static RbxInstance Walk(RbxInstance from, string rest)
        {
            if (rest.Length == 0)
            {
                return from;
            }

            // WHY longest-first: a name may itself contain dots; "A.B" as one child wins over A then B only when
            // no child is named "A".
            int dot = rest.IndexOf('.');
            while (true)
            {
                string head = dot < 0 ? rest : rest.Substring(0, dot);
                RbxInstance child = from.FindFirstChild(head);
                if (child != null)
                {
                    RbxInstance deeper = Walk(child, dot < 0 ? "" : rest.Substring(dot + 1));
                    if (deeper != null) return deeper;
                }

                if (dot < 0) return null;
                dot = rest.IndexOf('.', dot + 1);
            }
        }

        /// <summary>The path a reader passes back: the full name, or <c>game</c> for the DataModel.</summary>
        public static string PathOf(RbxInstance instance)
        {
            if (instance == null) return "";
            if (instance is RbxDataModel) return "game";
            try
            {
                return instance.GetFullName();
            }
            catch (Exception)
            {
                return instance.Name;
            }
        }

        internal static McpToolResult Fail(string message)
        {
            return McpToolResult.Failure(JsonConvert.SerializeObject(new { success = false, message }));
        }

        /// <summary>Property names shown in a tree row when the class has them.</summary>
        public static readonly string[] RowProperties =
        {
            "Position", "Size", "Anchored", "Transparency", "Enabled", "Disabled", "Visible", "Text", "Value",
            "Health", "Playing"
        };

        private static readonly Dictionary<Type, PropertyInfo[]> PropertyCache = new();

        /// <summary>
        /// Readable public properties of <paramref name="instance"/>'s class and its Roblox ancestors (signals,
        /// collections and delegates left out), each as short text; at most <paramref name="max"/>.
        /// </summary>
        public static JObject Properties(RbxInstance instance, IReadOnlyCollection<string> only, int max, int valueChars)
        {
            JObject properties = new();
            int count = 0;
            foreach (PropertyInfo property in PropertiesOf(instance.GetType()))
            {
                if (count >= max) break;
                if (only != null && !Contains(only, property.Name)) continue;
                string text;
                try
                {
                    text = Format(property.GetValue(instance), valueChars);
                }
                catch (Exception)
                {
                    continue;
                }

                if (text == null) continue;
                properties[property.Name] = text;
                count++;
            }

            return properties;
        }

        private static bool Contains(IReadOnlyCollection<string> names, string name)
        {
            foreach (string candidate in names)
            {
                if (string.Equals(candidate, name, StringComparison.Ordinal)) return true;
            }

            return false;
        }

        private static PropertyInfo[] PropertiesOf(Type type)
        {
            lock (PropertyCache)
            {
                if (PropertyCache.TryGetValue(type, out PropertyInfo[] cached)) return cached;
                List<PropertyInfo> list = new();
                foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
                {
                    if (!property.CanRead || property.GetIndexParameters().Length > 0) continue;
                    if (property.GetGetMethod() == null) continue;
                    Type valueType = property.PropertyType;
                    if (typeof(Delegate).IsAssignableFrom(valueType)) continue;
                    if (valueType != typeof(string) && typeof(IEnumerable).IsAssignableFrom(valueType)) continue;
                    if (valueType.Name.IndexOf("Signal", StringComparison.Ordinal) >= 0) continue;
                    if (property.Name == "Id" || property.Name == "IsDestroyed") continue;
                    list.Add(property);
                }

                list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                PropertyInfo[] result = list.ToArray();
                PropertyCache[type] = result;
                return result;
            }
        }

        private static string Format(object value, int chars)
        {
            string text;
            switch (value)
            {
                case null:
                    return "nil";
                case string s:
                    text = s;
                    break;
                case bool b:
                    return b ? "true" : "false";
                case RbxInstance other:
                    return other.IsDestroyed ? "(destroyed)" : PathOf(other);
                case IFormattable formattable:
                    text = formattable.ToString(null, CultureInfo.InvariantCulture);
                    break;
                default:
                    Type type = value.GetType();
                    if (!type.IsValueType && !type.IsEnum && type.Namespace != null &&
                        !type.Namespace.StartsWith("CoreAI.Mods.Rbx", StringComparison.Ordinal))
                    {
                        return null;
                    }

                    text = value.ToString();
                    break;
            }

            text ??= "";
            return text.Length <= chars ? text : text.Substring(0, chars) + "...";
        }
    }

    /// <summary>MCP <c>world_tree</c>: the world's instances in depth-first pages, filtered by path, class and name.</summary>
    public sealed class WorldTreeMcpTool : IMcpTool
    {
        /// <summary>Default and upper bound of rows per page.</summary>
        public const int DefaultMax = 100, MaxLimit = 500;

        /// <summary>Default and upper bound of the depth below the root.</summary>
        public const int DefaultDepth = 3, MaxDepth = 32;

        /// <summary>Instances one call walks at most (filters skip rows, not the walk).</summary>
        public const int WalkLimit = 50000;

        private readonly Func<RbxDataModel> _game;
        private readonly Func<InstanceRegistry> _registry;

        internal WorldTreeMcpTool(Func<RbxDataModel> game, Func<InstanceRegistry> registry)
        {
            _game = game;
            _registry = registry;
        }

        /// <inheritdoc />
        public string Name => "world_tree";

        /// <inheritdoc />
        public string Description =>
            "List the running world's instances like Roblox Explorer: path, class, name, child count and key " +
            "properties, depth-first from root (default game) down to depth. Filters: class (IsA), name (contains). " +
            "Pages: pass next_cursor as cursor. Read one instance with instance_get.";

        /// <inheritdoc />
        public string InputSchemaJson =>
            "{\"type\":\"object\",\"properties\":{" +
            "\"root\":{\"type\":\"string\",\"description\":\"Start path (Workspace.Model) or id:<n>; default game.\"}," +
            "\"depth\":{\"type\":\"integer\",\"description\":\"Levels below root (default 3, max 32).\"}," +
            "\"class\":{\"type\":\"string\",\"description\":\"Only instances that IsA this class.\"}," +
            "\"name\":{\"type\":\"string\",\"description\":\"Only names containing this (case-insensitive).\"}," +
            "\"properties\":{\"type\":\"boolean\",\"description\":\"Add key properties (default true).\"}," +
            "\"cursor\":{\"type\":\"integer\",\"description\":\"From a previous next_cursor.\"}," +
            "\"max\":{\"type\":\"integer\",\"description\":\"Rows per page (default 100, max 500).\"}}}";

        /// <inheritdoc />
        public Task<McpToolResult> InvokeAsync(JObject arguments, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RbxDataModel game = _game();
            if (game == null)
            {
                return Task.FromResult(WorldTreeMcpTools.Fail(WorldTreeMcpTools.NoWorld));
            }

            InstanceRegistry registry = _registry?.Invoke();
            RbxInstance root = WorldTreeMcpTools.Resolve(game, registry, McpArguments.String(arguments, "root"),
                out string error);
            if (root == null)
            {
                return Task.FromResult(WorldTreeMcpTools.Fail(error));
            }

            int depthLimit = McpArguments.Int(arguments, "depth", DefaultDepth);
            depthLimit = depthLimit < 0 ? DefaultDepth : Math.Min(depthLimit, MaxDepth);
            int max = McpArguments.Int(arguments, "max", DefaultMax);
            max = max <= 0 ? DefaultMax : Math.Min(max, MaxLimit);
            long cursor = Math.Max(0, McpArguments.Long(arguments, "cursor", 0));
            string className = McpArguments.String(arguments, "class")?.Trim();
            string name = McpArguments.String(arguments, "name")?.Trim();
            bool withProperties = McpArguments.Bool(arguments, "properties", true);

            JArray rows = new();
            long matched = 0;
            long walked = 0;
            bool more = false;
            bool truncated = false;
            Stack<(RbxInstance Instance, int Depth)> stack = new();
            stack.Push((root, 0));
            while (stack.Count > 0)
            {
                (RbxInstance node, int depth) = stack.Pop();
                if (node == null || node.IsDestroyed) continue;
                if (++walked > WalkLimit)
                {
                    truncated = true;
                    break;
                }

                IReadOnlyList<RbxInstance> children = node.GetChildren();
                if (Matches(node, className, name))
                {
                    if (matched >= cursor)
                    {
                        if (rows.Count >= max)
                        {
                            more = true;
                            break;
                        }

                        JObject row = new()
                        {
                            ["path"] = WorldTreeMcpTools.PathOf(node),
                            ["class"] = node.ClassName,
                            ["name"] = node.Name,
                            ["depth"] = depth,
                            ["children"] = children.Count
                        };
                        if (withProperties && !(node is RbxDataModel))
                        {
                            JObject props = WorldTreeMcpTools.Properties(node, WorldTreeMcpTools.RowProperties, 6, 60);
                            if (props.Count > 0) row["props"] = props;
                        }

                        rows.Add(row);
                    }

                    matched++;
                }

                if (depth >= depthLimit) continue;
                for (int i = children.Count - 1; i >= 0; i--) stack.Push((children[i], depth + 1));
            }

            JObject result = new()
            {
                ["success"] = true,
                ["root"] = WorldTreeMcpTools.PathOf(root),
                ["count"] = rows.Count,
                ["instances"] = rows,
                ["next_cursor"] = more ? cursor + rows.Count : -1
            };
            if (truncated) result["note"] = $"Stopped after walking {WalkLimit} instances; narrow root or depth.";
            if (registry != null) result["world_instances"] = registry.Count;
            return Task.FromResult(McpToolResult.Text(result.ToString(Formatting.None)));
        }

        private static bool Matches(RbxInstance node, string className, string name)
        {
            if (!string.IsNullOrEmpty(className) && !node.IsA(className)) return false;
            return string.IsNullOrEmpty(name) || node.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    /// <summary>MCP <c>instance_get</c>: one instance's class, path, properties, attributes, tags, children and owner.</summary>
    public sealed class InstanceGetMcpTool : IMcpTool
    {
        /// <summary>Properties returned at most.</summary>
        public const int MaxProperties = 80;

        /// <summary>Characters of one property value.</summary>
        public const int ValueChars = 300;

        /// <summary>Children names listed at most.</summary>
        public const int MaxChildren = 50;

        private readonly Func<RbxDataModel> _game;
        private readonly Func<InstanceRegistry> _registry;

        internal InstanceGetMcpTool(Func<RbxDataModel> game, Func<InstanceRegistry> registry)
        {
            _game = game;
            _registry = registry;
        }

        /// <inheritdoc />
        public string Name => "instance_get";

        /// <inheritdoc />
        public string Description =>
            "Read one world instance by path (Workspace.Model.Part, as world_tree lists it) or id:<n>: class, " +
            "properties, attributes, tags, parent, children and the mod that owns it. Read-only; change it with " +
            "execute_lua or a mod.";

        /// <inheritdoc />
        public string InputSchemaJson =>
            "{\"type\":\"object\",\"properties\":{" +
            "\"path\":{\"type\":\"string\",\"description\":\"Full name or id:<n>.\"}," +
            "\"properties\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"Only these properties.\"}}," +
            "\"required\":[\"path\"]}";

        /// <inheritdoc />
        public Task<McpToolResult> InvokeAsync(JObject arguments, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RbxDataModel game = _game();
            if (game == null)
            {
                return Task.FromResult(WorldTreeMcpTools.Fail(WorldTreeMcpTools.NoWorld));
            }

            InstanceRegistry registry = _registry?.Invoke();
            RbxInstance instance = WorldTreeMcpTools.Resolve(game, registry, McpArguments.String(arguments, "path"),
                out string error);
            if (instance == null)
            {
                return Task.FromResult(WorldTreeMcpTools.Fail(error));
            }

            List<string> only = null;
            if (arguments?["properties"] is JArray wanted && wanted.Count > 0)
            {
                only = new List<string>();
                foreach (JToken token in wanted)
                {
                    if (token.Type == JTokenType.String) only.Add(token.ToString());
                }
            }

            IReadOnlyList<RbxInstance> children = instance.GetChildren();
            JArray childList = new();
            for (int i = 0; i < children.Count && i < MaxChildren; i++)
            {
                childList.Add(children[i].Name + " (" + children[i].ClassName + ")");
            }

            JObject attributes = new();
            foreach (KeyValuePair<string, object> pair in instance.GetAttributes())
            {
                attributes[pair.Key] = pair.Value == null ? null : JToken.FromObject(pair.Value.ToString());
            }

            JObject result = new()
            {
                ["success"] = true,
                ["path"] = WorldTreeMcpTools.PathOf(instance),
                ["id"] = "id:" + instance.Id.Value.ToString(CultureInfo.InvariantCulture),
                ["class"] = instance.ClassName,
                ["name"] = instance.Name,
                ["parent"] = instance.Parent == null ? "" : WorldTreeMcpTools.PathOf(instance.Parent),
                ["children"] = children.Count,
                ["child_list"] = childList,
                ["properties"] = instance is RbxDataModel
                    ? new JObject()
                    : WorldTreeMcpTools.Properties(instance, only, MaxProperties, ValueChars),
                ["attributes"] = attributes,
                ["tags"] = new JArray(instance.GetTags())
            };
            if (registry != null && registry.TryGetRecord(instance.Id, out InstanceRecord record) && record != null)
            {
                if (!string.IsNullOrEmpty(record.OwnerModId)) result["owner_mod"] = record.OwnerModId;
                if (!string.IsNullOrEmpty(record.OriginTag)) result["origin"] = record.OriginTag;
                if (record.IsRuntimeInfrastructure) result["runtime_infrastructure"] = true;
            }

            return Task.FromResult(McpToolResult.Text(result.ToString(Formatting.None)));
        }
    }
}
