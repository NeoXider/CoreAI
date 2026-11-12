using System;
using System.Collections.Generic;
using CoreAI.Ai;
using Microsoft.Extensions.AI;

namespace CoreAI.Mcp.Tools
{
    /// <summary>One function an in-game tool publishes to its model.</summary>
    public readonly struct InGameFunction
    {
        /// <param name="name">The function name the model calls.</param>
        /// <param name="function">The MEAI function, or null.</param>
        /// <param name="jsonTool">The JSON-invocable tool when there is no MEAI function, or null.</param>
        public InGameFunction(string name, AIFunction function, IJsonInvocableLlmTool jsonTool)
        {
            Name = name ?? "";
            Function = function;
            JsonTool = jsonTool;
        }

        /// <summary>The function name the model calls.</summary>
        public string Name { get; }

        /// <summary>The MEAI function, or null.</summary>
        public AIFunction Function { get; }

        /// <summary>The JSON-invocable tool when there is no MEAI function, or null.</summary>
        public IJsonInvocableLlmTool JsonTool { get; }

        /// <summary>True when MCP can call it.</summary>
        public bool CanInvoke => Function != null || JsonTool != null;
    }

    /// <summary>
    /// What makes MCP at least as capable as an in-game CoreAI agent: every function an in-game agent can be given is
    /// either offered over MCP (through a dedicated adapter, or unchanged through <see cref="LlmFunctionMcpTool"/>
    /// behind <c>coreai_tools</c>) or listed in <see cref="NotExposed"/> with the reason. The parity gate test fails on
    /// any other function, so a tool added later cannot silently stay out of reach. Engine-free.
    /// </summary>
    public static class McpInGameTools
    {
        /// <summary>
        /// In-game tool names that have an MCP adapter of their own (authority rules, MCP-shaped arguments), so the
        /// in-game copy is never wrapped: <c>manage_mods</c> and <c>save_world</c> stay behind the explicit host-admin
        /// admission even though the in-game Programmer holds them.
        /// </summary>
        public static IReadOnlyCollection<string> DedicatedAdapterNames => Dedicated;

        /// <summary>True when <paramref name="name"/> has an MCP adapter of its own.</summary>
        public static bool HasDedicatedAdapter(string name) => name != null && Dedicated.Contains(name);

        private static readonly HashSet<string> Dedicated = new(StringComparer.Ordinal)
        {
            "execute_lua",
            "manage_mods",
            "get_mod_logs",
            "world_command",
            "read_skill",
            "screenshot",
            "save_world",
            "load_world",
            "list_autosaves",
            "load_autosave"
        };

        /// <summary>In-game tools deliberately not offered over MCP, with the reason a harness does not need them.</summary>
        public static IReadOnlyDictionary<string, string> NotExposed { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["memory"] = "the in-game model's own notebook between its turns; a harness keeps its own memory",
            ["wait"] = "pauses the in-game model's own turn; a harness waits on its side, and get_mod_logs wait_seconds " +
                       "waits for the game",
            ["call_skill_tool"] = "the in-game model's door to its skills' tools; MCP offers each skill tool under its own " +
                                  "name through coreai_tools",
            ["manage_skills"] = "writes the skills of the in-game agent that authors them; a harness has its own skills",
            ["declare_tasks"] = "plans the in-game orchestrator's sub-agent tasks; a harness runs its own sub-agents",
            ["delegate_task"] = "hands a task to an in-game sub-agent; a harness runs its own sub-agents",
            ["agent_start"] = "starts a sub-agent of the in-game orchestrator's team; a harness runs its own sub-agents",
            ["agent_send"] = "messages a sub-agent of the in-game orchestrator's team; a harness runs its own sub-agents",
            ["agent_read"] = "reads a sub-agent of the in-game orchestrator's team; a harness runs its own sub-agents",
            ["agent_stop"] = "stops a sub-agent of the in-game orchestrator's team; a harness runs its own sub-agents"
        };

        /// <summary>Every function <paramref name="tool"/> publishes, as the model receives them.</summary>
        public static IEnumerable<InGameFunction> Expand(ILlmTool tool)
        {
            switch (tool)
            {
                case null:
                    yield break;
                case IAIFunctionsLlmTool many:
                    foreach (AIFunction function in many.CreateAIFunctions())
                    {
                        if (function != null) yield return new InGameFunction(function.Name, function, null);
                    }

                    yield break;
                case IAIFunctionLlmTool one:
                    AIFunction single = one.CreateAIFunction();
                    yield return new InGameFunction(single?.Name ?? tool.Name, single,
                        single == null ? tool as IJsonInvocableLlmTool : null);
                    yield break;
                case IJsonInvocableLlmTool json:
                    yield return new InGameFunction(json.Name, null, json);
                    yield break;
                default:
                    yield return new InGameFunction(tool.Name, null, null);
                    yield break;
            }
        }

        /// <summary>
        /// MCP tools for the functions of <paramref name="tools"/>, in order (the first tool with a name wins), leaving
        /// out the <see cref="DedicatedAdapterNames"/>, the <see cref="NotExposed"/> names, the names in
        /// <paramref name="taken"/> and functions MCP cannot call. A tool whose functions cannot be built is skipped and
        /// reported to <paramref name="warning"/>.
        /// </summary>
        public static IReadOnlyList<IMcpTool> Wrap(IEnumerable<ILlmTool> tools, IEnumerable<string> taken,
            Action<string> warning = null)
        {
            HashSet<string> used = new(taken ?? Array.Empty<string>(), StringComparer.Ordinal);
            List<IMcpTool> wrapped = new();
            if (tools == null)
            {
                return wrapped;
            }

            foreach (ILlmTool tool in tools)
            {
                List<InGameFunction> functions = new();
                try
                {
                    functions.AddRange(Expand(tool));
                }
                catch (Exception ex)
                {
                    warning?.Invoke($"[CoreAI MCP] In-game tool '{tool?.Name}' left out of MCP: {ex.Message}");
                    continue;
                }

                foreach (InGameFunction function in functions)
                {
                    if (!function.CanInvoke || function.Name.Length == 0 ||
                        HasDedicatedAdapter(function.Name) || NotExposed.ContainsKey(function.Name) ||
                        !used.Add(function.Name))
                    {
                        continue;
                    }

                    wrapped.Add(new LlmFunctionMcpTool(function.Function, function.JsonTool));
                }
            }

            return wrapped;
        }
    }
}
