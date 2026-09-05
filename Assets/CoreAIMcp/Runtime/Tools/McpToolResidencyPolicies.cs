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
