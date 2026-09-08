using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace CoreAI.Mcp.Tools
{
    /// <summary>Live tool catalog with atomic, validated snapshots. Residency controls disclosure, not availability.</summary>
    public sealed class McpToolRegistry
    {
        private readonly object _gate = new();
        private readonly IMcpToolResidencyPolicy _policy;
        private readonly bool _alwaysIncludeBroker;
        private readonly Entry _broker;
        private Snapshot _snapshot;

        /// <summary>Creates an all-Native catalog. Duplicate names and invalid schemas are rejected.</summary>
        public McpToolRegistry(IEnumerable<IMcpTool> tools) : this(tools, null) { }

        /// <summary>Creates a catalog with the supplied disclosure policy; null tools means an empty set.</summary>
        public McpToolRegistry(IEnumerable<IMcpTool> tools, IMcpToolResidencyPolicy residencyPolicy,
            bool alwaysIncludeBroker = false)
        {
            _policy = residencyPolicy;
            _alwaysIncludeBroker = alwaysIncludeBroker;
            _broker = new Entry(new CoreAiToolsBrokerMcpTool(Find, () => DynamicTools), McpToolResidency.Native);
            _snapshot = Build(ReadEntries(tools), 0);
        }

        /// <summary>Signals a committed revision. Consumers read Revision to coalesce concurrent publications.</summary>
        internal event Action Changed;

        /// <summary>Monotonically increasing revision of the published catalog.</summary>
        public long Revision => Volatile.Read(ref _snapshot).Revision;

        /// <summary>Number of available tools, including the discovery broker when present.</summary>
        public int Count => Volatile.Read(ref _snapshot).ByName.Count;

        /// <summary>Adds a new binding or atomically replaces the named binding and schema.</summary>
        public void AddOrReplace(IMcpTool tool, McpToolResidency? residency = null)
        {
            Entry entry = CreateEntry(tool, residency);
            lock (_gate)
            {
                List<Entry> entries = new(_snapshot.Entries);
                int index = entries.FindIndex(existing => existing.Name == entry.Name);
                if (index < 0) entries.Add(entry);
                else entries[index] = entry;
                Volatile.Write(ref _snapshot, Build(entries, checked(_snapshot.Revision + 1)));
            }
            Changed?.Invoke();
        }

        /// <summary>Removes a binding. Already admitted calls may finish; future admission fails.</summary>
        public bool Remove(string name)
        {
            if (name == CoreAiToolsBrokerMcpTool.ToolName)
                throw new ArgumentException("The discovery broker is managed by the registry.", nameof(name));
            lock (_gate)
            {
                List<Entry> entries = new(_snapshot.Entries);
                if (entries.RemoveAll(entry => entry.Name == name) == 0) return false;
                Volatile.Write(ref _snapshot, Build(entries, checked(_snapshot.Revision + 1)));
            }
            Changed?.Invoke();
            return true;
        }

        /// <summary>Validates then publishes a complete host tool set in one operation. Failure leaves the old set intact.</summary>
        public void Replace(IEnumerable<IMcpTool> tools)
        {
            List<Entry> entries = ReadEntries(tools);
            lock (_gate)
                Volatile.Write(ref _snapshot, Build(entries, checked(_snapshot.Revision + 1)));
            Changed?.Invoke();
        }

        /// <summary>True when a tool is currently available.</summary>
        public bool Contains(string name) => Find(name) != null;

        /// <summary>Gets a frozen descriptor and binding from the current snapshot, or null.</summary>
        public IMcpTool Find(string name)
        {
            Snapshot snapshot = Volatile.Read(ref _snapshot);
            return name != null && snapshot.ByName.TryGetValue(name, out Entry entry) ? entry : null;
        }

        /// <summary>Tests whether a previously selected binding is still current at execution admission.</summary>
        public bool IsCurrent(IMcpTool binding) => binding != null && ReferenceEquals(Find(binding.Name), binding);

        /// <summary>Captures one request's bindings and arguments from one catalog revision before queueing.</summary>
        internal InvocationPlan CaptureInvocation(string name, JObject arguments)
        {
            Snapshot snapshot = Volatile.Read(ref _snapshot);
            if (name == null || !snapshot.ByName.TryGetValue(name, out Entry outer)) return null;
            JObject frozenArguments = arguments == null ? new JObject() : (JObject)arguments.DeepClone();
            CoreAiToolsBrokerMcpTool.CallBinding inner = null;
            if (name == CoreAiToolsBrokerMcpTool.ToolName &&
                string.Equals(McpArguments.String(frozenArguments, "action", null)?.Trim(), "call", StringComparison.OrdinalIgnoreCase))
            {
                inner = CoreAiToolsBrokerMcpTool.PrepareCall(
                    McpArguments.String(frozenArguments, "tool", null),
                    McpArguments.String(frozenArguments, "arguments_json", null),
                    target => target != null && snapshot.ByName.TryGetValue(target, out Entry entry) ? entry : null,
                    snapshot.Dynamic);
            }
            return new InvocationPlan(this, outer, frozenArguments, inner);
        }

        /// <summary>One-shot invocation with frozen data; admission atomically validates outer and broker target identities.</summary>
        internal sealed class InvocationPlan
        {
            private readonly McpToolRegistry _registry;
            private readonly IMcpTool _outer;
            private readonly JObject _arguments;
            private readonly CoreAiToolsBrokerMcpTool.CallBinding _inner;
            private bool _admitted;
            private int _invoked;

            internal InvocationPlan(McpToolRegistry registry, IMcpTool outer, JObject arguments,
                CoreAiToolsBrokerMcpTool.CallBinding inner)
            { _registry = registry; _outer = outer; _arguments = arguments; _inner = inner; }

            internal bool TryAdmit()
            {
                lock (_registry._gate)
                {
                    if (_admitted || !_registry.IsCurrent(_outer)) return false;
                    if (_inner?.TargetName != null &&
                        !ReferenceEquals(_inner.Tool, _registry.Find(_inner.TargetName))) return false;
                    _admitted = true;
                    return true;
                }
            }

            internal Task<Protocol.McpToolResult> InvokeAsync(CancellationToken cancellationToken)
            {
                // WHY: the registry lock protects admission only; arbitrary host callbacks run outside it.
                lock (_registry._gate)
                    if (!_admitted) throw new InvalidOperationException("The tool invocation was not admitted.");
                if (Interlocked.Exchange(ref _invoked, 1) != 0)
                    throw new InvalidOperationException("A captured tool invocation can run only once.");
                cancellationToken.ThrowIfCancellationRequested();
                return _inner != null ? _inner.InvokeAsync(cancellationToken) : _outer.InvokeAsync(_arguments, cancellationToken);
            }
        }

        /// <summary>Residency of the named binding; Native when absent.</summary>
        public McpToolResidency ResidencyOf(string name)
        {
            return Find(name) is Entry entry ? entry.Residency : McpToolResidency.Native;
        }

        /// <summary>Frozen Dynamic descriptors in registration order.</summary>
        public IReadOnlyList<IMcpTool> DynamicTools => Volatile.Read(ref _snapshot).Dynamic;

        /// <summary>Builds an independent tools/list payload from one published snapshot.</summary>
        public JArray ToListJson()
        {
            Snapshot snapshot = Volatile.Read(ref _snapshot);
            JArray result = new();
            foreach (Entry entry in snapshot.ByName.Values)
                if (entry.Residency == McpToolResidency.Native)
                    result.Add(new JObject { ["name"] = entry.Name, ["description"] = entry.Description,
                        ["inputSchema"] = JObject.Parse(entry.InputSchemaJson) });
            return result;
        }

        private Entry CreateEntry(IMcpTool tool, McpToolResidency? residency = null)
        {
            if (tool == null) throw new ArgumentNullException(nameof(tool));
            if (tool.Name == CoreAiToolsBrokerMcpTool.ToolName)
                throw new ArgumentException("The discovery broker name is reserved.", nameof(tool));
            return new Entry(tool, residency ?? _policy?.ResolveFor(tool) ?? McpToolResidency.Native);
        }

        private List<Entry> ReadEntries(IEnumerable<IMcpTool> tools)
        {
            List<Entry> entries = new();
            HashSet<string> names = new(StringComparer.Ordinal);
            if (tools != null)
                foreach (IMcpTool tool in tools)
                {
                    Entry entry = CreateEntry(tool);
                    if (!names.Add(entry.Name)) throw new ArgumentException($"Duplicate tool name '{entry.Name}'.", nameof(tools));
                    entries.Add(entry);
                }
            return entries;
        }

        private Snapshot Build(List<Entry> entries, long revision)
        {
            Dictionary<string, Entry> byName = new(StringComparer.Ordinal);
            List<IMcpTool> dynamic = new();
            foreach (Entry entry in entries)
            {
                byName.Add(entry.Name, entry);
                if (entry.Residency == McpToolResidency.Dynamic) dynamic.Add(entry);
            }
            if (dynamic.Count > 0 || _alwaysIncludeBroker) byName.Add(_broker.Name, _broker);
            return new Snapshot(entries, byName, dynamic.AsReadOnly(), revision);
        }

        private sealed class Snapshot
        {
            public Snapshot(List<Entry> entries, Dictionary<string, Entry> byName,
                IReadOnlyList<IMcpTool> dynamic, long revision)
            { Entries = entries; ByName = byName; Dynamic = dynamic; Revision = revision; }
            public List<Entry> Entries { get; }
            public Dictionary<string, Entry> ByName { get; }
            public IReadOnlyList<IMcpTool> Dynamic { get; }
            public long Revision { get; }
        }

        private sealed class Entry : IMcpTool
        {
            private readonly IMcpTool _body;
            public Entry(IMcpTool body, McpToolResidency residency)
            {
                Name = body.Name;
                if (string.IsNullOrWhiteSpace(Name) || Name != Name.Trim())
                    throw new ArgumentException("Tool names must be nonempty and have no surrounding whitespace.", nameof(body));
                if (residency != McpToolResidency.Native && residency != McpToolResidency.Dynamic)
                    throw new ArgumentOutOfRangeException(nameof(residency));
                JObject schema = JObject.Parse(string.IsNullOrWhiteSpace(body.InputSchemaJson)
                    ? "{\"type\":\"object\"}" : body.InputSchemaJson);
                if ((string)schema["type"] != "object")
                    throw new ArgumentException($"Tool '{Name}' requires an object input schema.", nameof(body));
                InputSchemaJson = schema.ToString(Newtonsoft.Json.Formatting.None);
                Description = body.Description ?? "";
                Residency = residency;
                _body = body;
            }
            public string Name { get; }
            public string Description { get; }
            public string InputSchemaJson { get; }
            public McpToolResidency Residency { get; }
            public Task<Protocol.McpToolResult> InvokeAsync(JObject arguments, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return _body.InvokeAsync(arguments, cancellationToken);
            }
        }
    }
}
