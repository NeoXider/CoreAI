using System;
using System.Collections.Generic;

namespace CoreAI.Mcp.Tools
{
    /// <summary>
    /// Factories combining the host policy with the environment-variable override.
    /// FOR: the variable override channel — comma-separated tool names read at composition
    /// time, so operators can move tools without recompiling.
    /// </summary>
    public static class McpToolResidencyPolicies
    {
        /// <summary>Variable holding a comma-separated list of tools forced Dynamic.</summary>
        public const string DynamicVariableName = "COREAI_MCP_DYNAMIC";

        /// <summary>Variable holding a comma-separated list of tools forced Native. Wins over dynamic.</summary>
        public const string NativeVariableName = "COREAI_MCP_NATIVE";

        /// <summary>An <see cref="IMcpToolResidencyPolicy"/> that resolves every tool Native. The default.</summary>
        public static IMcpToolResidencyPolicy Default { get; } = new FuncMcpToolResidencyPolicy(null);

        /// <summary>
        /// Tools that stay resident under <see cref="LeanDefault"/>: the ones a model needs in order to
        /// find everything else.
        /// </summary>
        /// <remarks>
        /// WHY these two and no more: <c>read_skill</c> IS the discovery path — the skill catalog in the
        /// system prompt tells the model to read a skill, so hiding that tool would contradict the
        /// prompt and burn a turn before anything could start. <c>memory</c> is listed because a role
        /// that has one consults it before it knows what it is doing, which is the same argument; it is
        /// simply absent from compositions that do not register it, and an absent name costs nothing.
        /// Everything else is reachable through <c>coreai_tools</c> and should not be paid for on turns
        /// that never touch it.
        /// </remarks>
        public static readonly string[] DiscoveryToolNames = { "read_skill", "memory" };

        /// <summary>
        /// Everything Dynamic except the discovery tools — the recommended default once a host has
        /// decided it wants the smaller prompt.
        /// </summary>
        /// <remarks>
        /// WHY `read_skill` and nothing else: it IS the discovery path. A skill catalog sits in the
        /// system prompt telling the model to read a skill, so hiding that tool behind the broker
        /// would contradict the prompt and cost a turn before anything could happen. Every other tool
        /// is reachable through <c>coreai_tools</c> without being paid for on turns that never touch
        /// it. Measured on the real six-tool composition: 9,324 B all-Native versus 768 B here.
        /// This is NOT the library default — <see cref="Default"/> stays all-Native so an existing
        /// composition is unchanged — it is what a host opts into.
        /// </remarks>
        public static IMcpToolResidencyPolicy LeanDefault { get; } = Lean();

        /// <summary>
        /// Everything Dynamic except the discovery tools plus <paramref name="alsoNative"/> — the tools
        /// THIS role reaches for on nearly every turn.
        /// </summary>
        /// <remarks>
        /// WHY a role decides this and not the library: residency is about what a particular agent uses
        /// constantly, and that differs per role. A programmer agent runs Lua on nearly every turn, so
        /// paying for `execute_lua`'s schema up front is cheaper than a broker round trip each time; a
        /// narrator that touches the world twice an hour should not carry `world_command` at all. The
        /// broker never disappears, so a wrong guess here costs one extra call, not a capability.
        /// <code>
        /// // programmer agent: Lua is its bread and butter
        /// McpToolResidencyPolicies.Lean("execute_lua", "manage_mods")
        /// </code>
        /// </remarks>
        /// <param name="alsoNative">Extra tool names to keep resident; null and blank entries are ignored.</param>
        public static IMcpToolResidencyPolicy Lean(params string[] alsoNative)
        {
            HashSet<string> resident = new(StringComparer.OrdinalIgnoreCase);
            foreach (string name in DiscoveryToolNames)
            {
                resident.Add(name);
            }

            if (alsoNative != null)
            {
                foreach (string name in alsoNative)
                {
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        resident.Add(name.Trim());
                    }
                }
            }

            return new FuncMcpToolResidencyPolicy(
                tool => tool?.Name != null && resident.Contains(tool.Name)
                    ? McpToolResidency.Native
                    : McpToolResidency.Dynamic);
        }

        /// <summary>True when the tool must stay listed for the model to discover the rest.</summary>
        public static bool IsDiscoveryTool(string toolName)
        {
            if (string.IsNullOrWhiteSpace(toolName))
            {
                return false;
            }

            foreach (string name in DiscoveryToolNames)
            {
                if (string.Equals(name, toolName.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Combines the explicit variable lists with a host policy.
        /// Precedence: explicit NATIVE entry &gt; explicit DYNAMIC entry &gt; host policy &gt; Native default.
        /// A name listed in neither variable falls back to <paramref name="hostPolicy"/> (null means
        /// all Native). Unknown names — listed in a variable but absent from
        /// <paramref name="knownToolNames"/> — are ignored with ONE warning each via
        /// <paramref name="warn"/> (null sinks to <see cref="Console.Error"/>), so a typo never
        /// silently does nothing.
        /// </summary>
        public static IMcpToolResidencyPolicy FromEnvironment(
            IReadOnlyCollection<string> knownToolNames,
            IMcpToolResidencyPolicy hostPolicy = null,
            Func<string, string> readVariable = null,
            Action<string> warn = null)
        {
            Func<string, string> read = readVariable ?? Environment.GetEnvironmentVariable;
            Action<string> warnSink = warn ?? (static message => Console.Error.WriteLine(message));
            HashSet<string> dynamic = ParseList(read(DynamicVariableName));
            HashSet<string> native = ParseList(read(NativeVariableName));
            HashSet<string> known = new(knownToolNames ?? Array.Empty<string>(), StringComparer.Ordinal);

            foreach (string name in native)
            {
                if (!known.Contains(name))
                {
                    warnSink($"[CoreAI MCP] {NativeVariableName} names unknown tool '{name}'; ignoring it.");
                }
            }

            foreach (string name in dynamic)
            {
                if (!known.Contains(name))
                {
                    warnSink($"[CoreAI MCP] {DynamicVariableName} names unknown tool '{name}'; ignoring it.");
                }
            }

            return new FuncMcpToolResidencyPolicy(tool =>
            {
                if (tool == null || string.IsNullOrEmpty(tool.Name))
                {
                    return McpToolResidency.Native;
                }

                // WHY: explicit NATIVE wins over explicit DYNAMIC — pinning a tool resident must
                // always be expressible even when a broad DYNAMIC list names it too.
                if (native.Contains(tool.Name))
                {
                    return McpToolResidency.Native;
                }

                if (dynamic.Contains(tool.Name))
                {
                    return McpToolResidency.Dynamic;
                }

                return hostPolicy?.ResolveFor(tool) ?? McpToolResidency.Native;
            });
        }

        private static HashSet<string> ParseList(string raw)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return names;
            }

            foreach (string part in raw.Split(','))
            {
                string trimmed = part.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                {
                    names.Add(trimmed);
                }
            }

            return names;
        }
    }
}
