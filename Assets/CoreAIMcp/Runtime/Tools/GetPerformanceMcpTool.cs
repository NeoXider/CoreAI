using System;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Diagnostics;
using CoreAI.Mcp.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CoreAI.Mcp.Tools
{
    /// <summary>
    /// MCP <c>get_performance</c>: the frame rate over a recent window and the engine counters the running build
    /// really provides, from an <see cref="IRuntimePerformanceSource"/>. A counter the build lacks is listed in
    /// <c>unavailable</c>, never reported as zero. Read-only; runs on the game thread.
    /// </summary>
    public sealed class GetPerformanceMcpTool : IMcpTool
    {
        /// <summary>Default <c>window_seconds</c>.</summary>
        public const double DefaultWindowSeconds = 5d;

        /// <summary>Upper bound of <c>window_seconds</c>.</summary>
        public const double MaxWindowSeconds = 60d;

        private readonly IRuntimePerformanceSource _source;

        /// <param name="source">The performance monitor.</param>
        public GetPerformanceMcpTool(IRuntimePerformanceSource source)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
        }

        /// <inheritdoc />
        public string Name => "get_performance";

        /// <inheritdoc />
        public string Description =>
            "Frame rate over the last window_seconds (default 5, max 60): fps_avg, fps_min (slowest frame), frame " +
            "times, slow frames over 50 ms, plus the engine counters this build provides (draw calls, batches, " +
            "memory). Counters the build lacks are listed in unavailable.";

        /// <inheritdoc />
        public string InputSchemaJson =>
            "{\"type\":\"object\",\"properties\":{" +
            "\"window_seconds\":{\"type\":\"number\",\"description\":\"Seconds of recent frames (default 5, max 60).\"}," +
            "\"counters\":{\"type\":\"boolean\",\"description\":\"Include engine counters (default true).\"}}}";

        /// <inheritdoc />
        public Task<McpToolResult> InvokeAsync(JObject arguments, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double window = McpArguments.Float(arguments, "window_seconds", (float)DefaultWindowSeconds);
            window = window <= 0d ? DefaultWindowSeconds : Math.Min(window, MaxWindowSeconds);
            JObject result = new()
            {
                ["success"] = true,
                ["fps"] = RuntimeDiagnosticsJson.FrameRate(_source.GetFrameRate(window))
            };
            if (McpArguments.Bool(arguments, "counters", true))
            {
                result["counters"] = RuntimeDiagnosticsJson.Counters(_source.GetCounters());
            }

            return Task.FromResult(McpToolResult.Text(result.ToString(Formatting.None)));
        }
    }
}
