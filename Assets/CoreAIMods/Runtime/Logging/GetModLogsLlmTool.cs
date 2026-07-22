using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Newtonsoft.Json;

namespace CoreAI.Ai.Logging
{
    /// <summary>
    /// Read-only LLM tool (<c>get_mod_logs</c>) exposing <see cref="ILuaLogService"/> so an in-game
    /// agent can read <c>print</c>/<c>warn</c>/<c>error</c>/runtime-error output from Lua mods during
    /// play — independent of the Unity console — to diagnose and self-repair a misbehaving mod.
    /// <para>
    /// TODO: wire into the tool registry (e.g. <c>CoreAiModsInstaller.RegisterCoreAiMods</c>, alongside
    /// <c>execute_lua</c>/<c>manage_mods</c>) once <see cref="ILuaLogService"/> is threaded through the
    /// mod runtime's Append calls; this class is currently unregistered and unused.
    /// </para>
    /// </summary>
    public sealed class GetModLogsLlmTool : IAIFunctionLlmTool
    {
        /// <summary>Public tool name.</summary>
        public const string ToolName = "get_mod_logs";

        /// <summary>Default value of the <c>max_entries</c> parameter when omitted.</summary>
        public const int DefaultMaxEntries = 50;

        /// <summary>Character budget passed to <see cref="LuaLogFormatter.ToPromptText"/>.</summary>
        public const int MaxPromptChars = 6000;

        private readonly ILuaLogService _logService;

        public GetModLogsLlmTool(ILuaLogService logService)
        {
            _logService = logService ?? throw new ArgumentNullException(nameof(logService));
        }

        /// <inheritdoc />
        public string Name => ToolName;

        /// <inheritdoc />
        // WHY: A read-only query is meaningful to repeat (logs change between calls), so cross-turn
        // duplicate suppression must not apply.
        public bool AllowDuplicates => true;

        /// <inheritdoc />
        public string Description =>
            "Read Lua mod logs (print/warn/error/runtime-error) captured independently of the Unity " +
            "console, so you can inspect what a mod printed or which error it threw during play and " +
            "repair it. Read-only. Params: mod_id (optional filter), level (optional minimum severity: " +
            "print, warn, error, runtime_error), since_sequence (optional, only entries newer than this " +
            "sequence number), max_entries (default 50).";

        /// <inheritdoc />
        public string ParametersSchema =>
            "{" +
            "\"type\":\"object\"," +
            "\"properties\":{" +
            "\"mod_id\":{\"type\":\"string\",\"description\":\"Only return logs from this mod id.\"}," +
            "\"level\":{\"type\":\"string\",\"description\":\"Minimum severity: print, warn, error, runtime_error.\"}," +
            "\"since_sequence\":{\"type\":\"integer\",\"description\":\"Only return entries with a sequence number greater than this.\"}," +
            "\"max_entries\":{\"type\":\"integer\",\"description\":\"Maximum number of entries to return (default 50).\"}" +
            "}}";

        /// <summary>Creates the MEAI function surface for <c>get_mod_logs</c>.</summary>
        public AIFunction CreateAIFunction()
        {
            Func<string, string, long, int, CancellationToken, Task<string>> func = ExecuteAsync;
            AIFunctionFactoryOptions options = new()
            {
                Name = Name,
                Description = Description
            };
            return AIFunctionFactory.Create(func, options);
        }

        /// <summary>Executes a <c>get_mod_logs</c> query and returns a JSON result for the model.</summary>
        public Task<string> ExecuteAsync(
            [Description("Only return logs from this mod id.")]
            string mod_id = null,
            [Description("Minimum severity: print, warn, error, runtime_error.")]
            string level = null,
            [Description("Only return entries with a sequence number greater than this.")]
            long since_sequence = 0,
            [Description("Maximum number of entries to return (default 50).")]
            int max_entries = DefaultMaxEntries,
            CancellationToken cancellationToken = default)
        {
            LuaLogLevel? minLevel = null;
            if (!string.IsNullOrWhiteSpace(level))
            {
                if (!Enum.TryParse(level.Trim(), true, out LuaLogLevel parsed))
                {
                    return Task.FromResult(Fail(
                        $"Unknown level '{level}'. Valid: print, warn, error, runtime_error."));
                }

                minLevel = parsed;
            }

            LuaLogQuery query = new()
            {
                ModId = string.IsNullOrWhiteSpace(mod_id) ? null : mod_id.Trim(),
                MinLevel = minLevel,
                SinceSequence = since_sequence,
                MaxCount = max_entries > 0 ? max_entries : DefaultMaxEntries
            };

            IReadOnlyList<LuaLogEntry> entries = _logService.Query(query);
            string promptText = LuaLogFormatter.ToPromptText(entries, MaxPromptChars);

            return Task.FromResult(JsonConvert.SerializeObject(new
            {
                success = true,
                count = entries.Count,
                logs = promptText
            }));
        }

        private static string Fail(string message)
        {
            return JsonConvert.SerializeObject(new { success = false, message });
        }
    }
}
