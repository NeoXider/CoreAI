using System;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai.Logging;
using CoreAI.Mcp.Protocol;
using Newtonsoft.Json.Linq;

namespace CoreAI.Mcp.Tools
{
    /// <summary>
    /// MCP <c>get_mod_logs</c> tool: reads Lua mod output (print/warn/error/runtime-error) captured
    /// independently of the Unity console, via the existing <see cref="GetModLogsLlmTool"/> over
    /// <see cref="ILuaLogService"/>. Read-only; safe to marshal to the main thread but does no game
    /// mutation.
    /// </summary>
    public sealed class GetModLogsMcpTool : IMcpTool
    {
        private readonly GetModLogsLlmTool _inner;

        /// <param name="logService">The Lua log ring-buffer service.</param>
        public GetModLogsMcpTool(ILuaLogService logService)
        {
            if (logService == null)
            {
                throw new ArgumentNullException(nameof(logService));
            }

            _inner = new GetModLogsLlmTool(logService);
        }

        /// <inheritdoc />
        public string Name => "get_mod_logs";

        /// <inheritdoc />
        public string Description =>
            "Read Lua mod logs (print/warn/error/runtime-error) captured independently of the Unity " +
            "console, to see what a mod printed or which error it threw during play and repair it. " +
            "Read-only. Entries carry a monotonically increasing sequence number and are returned " +
            "oldest-first; pass since_sequence to page only newer entries. When the character budget is exceeded, " +
            "the newest entries are kept and a leading '...(+N more)' marker counts omitted entries. Params: mod_id (optional filter), " +
            "level (minimum severity: print, warn, error, runtime_error), since_sequence, max_entries (default 50).";

        /// <inheritdoc />
        public string InputSchemaJson => _inner.ParametersSchema;

        /// <inheritdoc />
        public async Task<McpToolResult> InvokeAsync(JObject arguments, CancellationToken cancellationToken)
        {
            string modId = McpArguments.String(arguments, "mod_id");
            string level = McpArguments.String(arguments, "level");
            long since = McpArguments.Long(arguments, "since_sequence", 0);
            int max = McpArguments.Int(arguments, "max_entries", GetModLogsLlmTool.DefaultMaxEntries);

            string json = await _inner
                .ExecuteAsync(modId, level, since, max, cancellationToken)
                .ConfigureAwait(false);

            JToken success = JObject.Parse(json)["success"];
            bool isError = success?.Type == JTokenType.Boolean && !success.Value<bool>();
            return new McpToolResult(new[] { McpContent.CreateText(json) }, isError);
        }
    }
}
