using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Diagnostics;
using CoreAI.Mcp.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CoreAI.Mcp.Tools
{
    /// <summary>
    /// MCP <c>get_unity_logs</c>: reads the engine log (every <c>Debug.Log</c>, warning, error and exception, not
    /// only what mods print) from an <see cref="IRuntimeLogSource"/> with a cursor, so a debugging client reads
    /// only what is new. Read-only.
    /// </summary>
    public sealed class GetUnityLogsMcpTool : IMcpTool
    {
        /// <summary>Default <c>max_entries</c>.</summary>
        public const int DefaultMaxEntries = 50;

        /// <summary>Upper bound of <c>max_entries</c>.</summary>
        public const int MaxEntriesLimit = 500;

        /// <summary>Character budget of the <c>logs</c> text.</summary>
        public const int MaxLogChars = 12000;

        /// <summary>Stack frames kept per entry when <c>stack</c> is true.</summary>
        public const int StackFrames = 8;

        private readonly IRuntimeLogSource _source;

        /// <param name="source">The engine log.</param>
        public GetUnityLogsMcpTool(IRuntimeLogSource source)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
        }

        /// <inheritdoc />
        public string Name => "get_unity_logs";

        /// <inheritdoc />
        public string Description =>
            "Read the Unity log (all Debug.Log output, warnings, errors, exceptions; not only mods). Pass the " +
            "returned next_sequence as since_sequence to get only newer entries. Repeats are folded (xN).";

        /// <inheritdoc />
        public string InputSchemaJson =>
            "{\"type\":\"object\",\"properties\":{" +
            "\"since_sequence\":{\"type\":\"integer\",\"description\":\"Only entries after this sequence.\"}," +
            "\"level\":{\"type\":\"string\",\"enum\":[\"info\",\"warning\",\"error\",\"exception\"]," +
            "\"description\":\"Minimum severity (default info).\"}," +
            "\"contains\":{\"type\":\"string\",\"description\":\"Only messages containing this text.\"}," +
            "\"max_entries\":{\"type\":\"integer\",\"description\":\"Newest entries kept (default 50, max 500).\"}," +
            "\"stack\":{\"type\":\"boolean\",\"description\":\"Add stack traces of warnings and errors.\"}}}";

        /// <inheritdoc />
        public Task<McpToolResult> InvokeAsync(JObject arguments, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string levelText = McpArguments.String(arguments, "level");
            if (!RuntimeLogLevels.TryParse(levelText, RuntimeLogLevel.Info, out RuntimeLogLevel level))
            {
                return Task.FromResult(McpToolResult.Failure(JsonConvert.SerializeObject(new
                {
                    success = false,
                    message = $"Unknown level '{levelText}'. Valid: {RuntimeLogLevels.Names}."
                })));
            }

            long since = Math.Max(0, McpArguments.Long(arguments, "since_sequence", 0));
            int max = McpArguments.Int(arguments, "max_entries", DefaultMaxEntries);
            max = max <= 0 ? DefaultMaxEntries : Math.Min(max, MaxEntriesLimit);
            bool stack = McpArguments.Bool(arguments, "stack");

            long latest = _source.LatestSequence;
            long oldest = _source.OldestRetainedSequence;
            // WHY: a cursor from an earlier game process points past this log; read it from the start instead
            // of answering nothing until the new log catches up.
            bool cursorReset = since > latest;
            if (cursorReset) since = 0;
            IReadOnlyList<RuntimeLogEntry> entries = _source.Read(new RuntimeLogQuery
            {
                SinceSequence = since,
                MinLevel = level,
                MaxCount = max,
                Contains = McpArguments.String(arguments, "contains")
            });
            RuntimeLogCounts totals = _source.Counts;
            JObject result = new()
            {
                ["success"] = true,
                ["count"] = entries.Count,
                ["next_sequence"] = Math.Max(since, latest),
                ["missed"] = Math.Max(0, oldest - 1 - since),
                ["totals"] = new JObject
                {
                    ["warnings"] = totals.Warnings,
                    ["errors"] = totals.Errors,
                    ["exceptions"] = totals.Exceptions
                },
                ["logs"] = RuntimeLogFormatter.Format(entries, MaxLogChars, stack ? StackFrames : 0)
            };
            if (cursorReset) result["cursor_reset"] = true;
            return Task.FromResult(McpToolResult.Text(result.ToString(Formatting.None)));
        }
    }
}
