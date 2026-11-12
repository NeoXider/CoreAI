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
    /// The parts a <see cref="GameStatusMcpTool"/> reports. Every part is optional: a part that is null is left out
    /// of the result, and a part that throws is reported as <c>{"error": ...}</c> for that section only, so one broken
    /// service never hides the rest of the overview.
    /// </summary>
    public sealed class GameStatusSources
    {
        /// <summary>Platform, build, Unity version, scenes (the Unity host supplies it).</summary>
        public Func<JObject> Engine { get; set; }

        /// <summary>Loaded mods with their state and error counts.</summary>
        public Func<JToken> Mods { get; set; }

        /// <summary>Cheap world facts such as the instance count.</summary>
        public Func<JObject> World { get; set; }

        /// <summary>The MCP transport serving this call.</summary>
        public Func<JObject> Server { get; set; }

        /// <summary>Frame rate source for the five-second summary.</summary>
        public IRuntimePerformanceSource Performance { get; set; }

        /// <summary>Engine log for the totals per severity.</summary>
        public IRuntimeLogSource Logs { get; set; }
    }

    /// <summary>
    /// MCP <c>game_status</c>: one call that says what is running - platform and build, versions, scenes, frame
    /// rate, log totals, mods and world size - so a client checking a build starts from facts. Read-only; runs on
    /// the game thread.
    /// </summary>
    public sealed class GameStatusMcpTool : IMcpTool
    {
        /// <summary>Seconds of frames the status summarizes.</summary>
        public const double FrameRateWindowSeconds = 5d;

        private readonly GameStatusSources _sources;

        /// <param name="sources">The parts to report; null reports only the versions.</param>
        public GameStatusMcpTool(GameStatusSources sources)
        {
            _sources = sources ?? new GameStatusSources();
        }

        /// <inheritdoc />
        public string Name => "game_status";

        /// <inheritdoc />
        public string Description =>
            "Overview of the running game: platform and build type, Unity and CoreAI versions, scenes, fps, log " +
            "totals, mods with state and error counts, world instance count. Call it first when checking a build.";

        /// <inheritdoc />
        public string InputSchemaJson => "{\"type\":\"object\",\"properties\":{}}";

        /// <inheritdoc />
        public Task<McpToolResult> InvokeAsync(JObject arguments, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            JObject result = new()
            {
                ["success"] = true,
                ["coreai_version"] = McpServerInfo.Version
            };
            Add(result, "engine", _sources.Engine);
            if (_sources.Performance != null)
            {
                IRuntimePerformanceSource performance = _sources.Performance;
                Add(result, "fps", () => RuntimeDiagnosticsJson.FrameRate(performance.GetFrameRate(FrameRateWindowSeconds)));
            }

            if (_sources.Logs != null)
            {
                IRuntimeLogSource logs = _sources.Logs;
                Add(result, "logs", () =>
                {
                    RuntimeLogCounts counts = logs.Counts;
                    return new JObject
                    {
                        ["warnings"] = counts.Warnings,
                        ["errors"] = counts.Errors,
                        ["exceptions"] = counts.Exceptions,
                        ["latest_sequence"] = logs.LatestSequence
                    };
                });
            }

            Add(result, "mods", _sources.Mods);
            Add(result, "world", _sources.World);
            Add(result, "mcp", _sources.Server);
            return Task.FromResult(McpToolResult.Text(result.ToString(Formatting.None)));
        }

        private static void Add<T>(JObject result, string key, Func<T> part) where T : JToken
        {
            if (part == null)
            {
                return;
            }

            try
            {
                result[key] = (JToken)part() ?? JValue.CreateNull();
            }
            catch (Exception ex)
            {
                result[key] = new JObject { ["error"] = ex.GetType().Name + ": " + ex.Message };
            }
        }
    }
}
