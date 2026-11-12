using System.Collections.Generic;
using CoreAI.Diagnostics;

namespace CoreAI.Mcp.Tools
{
    /// <summary>
    /// Builds the MCP debug tools over the engine-free readers: <c>game_status</c> always, <c>get_unity_logs</c> when a
    /// log source exists and <c>get_performance</c> when a performance source exists. They are meant to be Native: a
    /// debugging client wants them listed.
    /// </summary>
    public static class McpDiagnosticsTools
    {
        /// <param name="logs">The engine log; null omits <c>get_unity_logs</c> and the status log totals.</param>
        /// <param name="performance">The performance monitor; null omits <c>get_performance</c> and the status fps.</param>
        /// <param name="status">Further status parts (engine, mods, world, transport); null reports versions only.</param>
        public static IReadOnlyList<IMcpTool> Create(IRuntimeLogSource logs, IRuntimePerformanceSource performance,
            GameStatusSources status)
        {
            GameStatusSources sources = status ?? new GameStatusSources();
            sources.Logs ??= logs;
            sources.Performance ??= performance;
            List<IMcpTool> tools = new() { new GameStatusMcpTool(sources) };
            if (logs != null) tools.Add(new GetUnityLogsMcpTool(logs));
            if (performance != null) tools.Add(new GetPerformanceMcpTool(performance));
            return tools;
        }
    }
}
