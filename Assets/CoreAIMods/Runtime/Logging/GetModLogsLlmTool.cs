using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
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
    /// It is also the watch surface after a change: every result carries <c>next_sequence</c>, the cursor for the next
    /// call; <c>since_reload</c> starts after the mod's latest load or reload (<see cref="LuaModLifecycleLog"/>); and
    /// <c>wait_seconds</c> (at most <see cref="MaxWaitSeconds"/>) waits for a new warning or error and answers as soon as
    /// one arrives. The wait is scheduled by the host's <see cref="ILlmAsyncMarshaler.DelayAsync"/>, which a Unity
    /// host drives from its frame loop, so it never blocks and runs in a WebGL player; its length is measured in
    /// time, so a host that runs few frames (a hidden browser tab) still answers after <c>wait_seconds</c>.
    /// </para>
    /// <para>
    /// Production wiring: <c>CoreAiModsInstaller.RegisterCoreAiMods</c> registers the
    /// <see cref="ILuaLogService"/> singleton, threads it through <c>LuaCsModStackOptions</c> into the
    /// mod runtime's Append calls, and attaches this tool to the built-in Programmer role alongside
    /// <c>execute_lua</c>/<c>manage_mods</c>; the MCP server resolves the same singleton for its
    /// <c>get_mod_logs</c> tool.
    /// </para>
    /// </summary>
    public sealed class GetModLogsLlmTool : IAIFunctionLlmTool, ILlmToolResultCutAdvice
    {
        /// <inheritdoc />
        public string AdviseOnCut(string functionName, IDictionary<string, object> arguments)
        {
            return "Ask for fewer entries: level='warn', a smaller max_entries, or since_sequence for newer ones only.";
        }

        /// <summary>Public tool name.</summary>
        public const string ToolName = "get_mod_logs";

        /// <summary>Default value of the <c>max_entries</c> parameter when omitted.</summary>
        public const int DefaultMaxEntries = 50;

        /// <summary>Character budget passed to <see cref="LuaLogFormatter.ToPromptText"/>.</summary>
        public const int MaxPromptChars = 6000;

        /// <summary>Upper bound of <c>wait_seconds</c>.</summary>
        public const int MaxWaitSeconds = 10;

        /// <summary>How often a wait checks whether a warning or error arrived.</summary>
        public const int WaitStepMilliseconds = 100;

        private readonly ILuaLogService _logService;
        private readonly ILlmAsyncMarshaler _asyncMarshaler;
        private readonly LuaModLifecycleLog _lifecycle;
        private readonly Func<double> _elapsedMilliseconds;

        public GetModLogsLlmTool(ILuaLogService logService)
            : this(logService, null, null)
        {
        }

        /// <param name="logService">The mod log.</param>
        /// <param name="asyncMarshaler">
        /// Schedules <c>wait_seconds</c>; null answers at once instead of waiting. A Unity host passes its frame-driven
        /// marshaler (<c>ICoreAISettings.ToolInvocationMarshaler</c>); a host with a thread pool may pass
        /// <see cref="PassThroughLlmAsyncMarshaler.Instance"/>, whose delay is <c>Task.Delay</c>.
        /// </param>
        /// <param name="lifecycle">The recorded load of every mod for <c>since_reload</c>; null scans the log for the load line.</param>
        public GetModLogsLlmTool(ILuaLogService logService, ILlmAsyncMarshaler asyncMarshaler,
            LuaModLifecycleLog lifecycle = null)
            : this(logService, asyncMarshaler, lifecycle, null)
        {
        }

        /// <param name="elapsedMilliseconds">A monotonic clock in milliseconds that measures a wait; null uses a stopwatch.</param>
        internal GetModLogsLlmTool(ILuaLogService logService, ILlmAsyncMarshaler asyncMarshaler,
            LuaModLifecycleLog lifecycle, Func<double> elapsedMilliseconds)
        {
            _logService = logService ?? throw new ArgumentNullException(nameof(logService));
            // WHY: no default scheduler: the pass-through delay is Task.Delay, which never fires in a WebGL player, so
            // a tool built without one must not wait at all rather than hang the model turn.
            _asyncMarshaler = asyncMarshaler;
            _lifecycle = lifecycle;
            if (elapsedMilliseconds == null)
            {
                Stopwatch watch = Stopwatch.StartNew();
                elapsedMilliseconds = () => watch.Elapsed.TotalMilliseconds;
            }

            _elapsedMilliseconds = elapsedMilliseconds;
        }

        /// <inheritdoc />
        public string Name => ToolName;

        /// <inheritdoc />
        // WHY: A read-only query is meaningful to repeat (logs change between calls), so cross-turn
        // duplicate suppression must not apply.
        public bool AllowDuplicates => true;

        /// <inheritdoc />
        public string Description =>
            "Read Lua mod logs captured during play: what a mod printed and which error it threw, with " +
            "script:line. Read-only. Each result has next_sequence; pass it as since_sequence for newer entries " +
            "only. To check a change, call right after manage_mods reload with mod_id, since_reload=true, " +
            "wait_seconds=5: it answers once a warning or error arrives, else after the wait.";

        /// <inheritdoc />
        public string ParametersSchema =>
            "{" +
            "\"type\":\"object\"," +
            "\"properties\":{" +
            "\"mod_id\":{\"type\":\"string\",\"description\":\"Only this mod.\"}," +
            "\"level\":{\"type\":\"string\",\"description\":\"Minimum: print, warn, error, runtime_error.\"}," +
            "\"since_sequence\":{\"type\":\"integer\",\"description\":\"Only entries after this sequence.\"}," +
            "\"max_entries\":{\"type\":\"integer\",\"description\":\"Most entries (default 50).\"}," +
            "\"since_reload\":{\"type\":\"boolean\",\"description\":\"With mod_id: only entries since its latest load or reload.\"}," +
            "\"wait_seconds\":{\"type\":\"integer\",\"description\":\"Wait up to N seconds (max 10) for a new warning or error.\"}" +
            "}}";

        /// <summary>Creates the MEAI function surface for <c>get_mod_logs</c>.</summary>
        public AIFunction CreateAIFunction()
        {
            // WHY: System.Threading.Tasks.Task is banned by ARCHITECTURE_RULES §3, but this is a
            // framework-boundary exception — MEAI's AIFunctionFactory.Create binds a delegate whose
            // contract is Task<string>; it does not accept UniTask.
            Func<string, string, long, int, bool, int, CancellationToken, Task<string>> func = ExecuteAsync;
            AIFunctionFactoryOptions options = new()
            {
                Name = Name,
                Description = Description
            };
            return AIFunctionFactory.Create(func, options);
        }

        /// <summary>Executes a <c>get_mod_logs</c> query without waiting and returns a JSON result for the model.</summary>
        public Task<string> ExecuteAsync(
            string mod_id,
            string level,
            long since_sequence,
            int max_entries,
            CancellationToken cancellationToken)
        {
            return ExecuteAsync(mod_id, level, since_sequence, max_entries, false, 0, cancellationToken);
        }

        /// <summary>Executes a <c>get_mod_logs</c> query, optionally waiting for a warning or error.</summary>
        // WHY: MEAI awaits this task with ConfigureAwait(false) inside its binary. In the WebGL player
        // the result must be published with the host SynchronizationContext cleared, or the model turn
        // never resumes after an asynchronously completed body (see MeaiToolTaskBridge).
        public Task<string> ExecuteAsync(
            [Description("Only this mod.")]
            string mod_id = null,
            [Description("Minimum: print, warn, error, runtime_error.")]
            string level = null,
            [Description("Only entries after this sequence.")]
            long since_sequence = 0,
            [Description("Most entries (default 50).")]
            int max_entries = DefaultMaxEntries,
            [Description("With mod_id: only entries since its latest load or reload.")]
            bool since_reload = false,
            [Description("Wait up to N seconds (max 10) for a new warning or error.")]
            int wait_seconds = 0,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

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

            string modId = string.IsNullOrWhiteSpace(mod_id) ? null : mod_id.Trim();
            if (since_reload && modId == null)
            {
                return Task.FromResult(Fail("since_reload needs mod_id."));
            }

            long since = since_reload
                ? Math.Max(since_sequence, LuaModLifecycleLog.SinceLatestLoad(_logService, _lifecycle, modId))
                : since_sequence;
            LuaLogQuery query = new()
            {
                ModId = modId,
                MinLevel = minLevel,
                SinceSequence = since,
                MaxCount = max_entries > 0 ? max_entries : DefaultMaxEntries
            };

            int wait = Math.Max(0, Math.Min(MaxWaitSeconds, wait_seconds));
            if (wait == 0)
            {
                return Task.FromResult(Format(query, -1));
            }

            if (_asyncMarshaler == null)
            {
                return Task.FromResult(Format(query, 0));
            }

            return MeaiToolTaskBridge.Publish(WaitAndFormatAsync(query, wait, cancellationToken));
        }

        private async Task<string> WaitAndFormatAsync(LuaLogQuery query, int waitSeconds, CancellationToken cancellationToken)
        {
            LuaLogLevel alert = query.MinLevel.HasValue && query.MinLevel.Value > LuaLogLevel.Warn
                ? query.MinLevel.Value
                : LuaLogLevel.Warn;
            if (HasAlert(_logService.Query(query), alert))
            {
                return Format(query, 0);
            }

            int arrived = 0;
            string modId = query.ModId;
            long since = query.SinceSequence;
            Action<LuaLogEntry> watch = entry =>
            {
                if (entry != null && entry.Level >= alert && entry.Sequence > since &&
                    (modId == null || string.Equals(entry.ModId, modId, StringComparison.Ordinal)))
                {
                    Volatile.Write(ref arrived, 1);
                }
            };

            // WHY: a flag polled in short host-scheduled steps instead of a completion source: the event is raised
            // inside the mod's own print or error call, and completing the tool from there would run the model
            // turn's continuation in the middle of that mod's code.
            // WHY the clock as well as the steps: a host-scheduled step lasts at least one frame, and a hidden
            // browser tab runs one or two frames a second, so counting steps made a 5 s wait last 50 frames
            // (measured: 31 s, past the 30 s tool timeout). The wait ends when either the measured time or the
            // steps' own lengths reach the window.
            double waitedMilliseconds = 0d;
            double steppedMilliseconds = 0d;
            double startedAt = _elapsedMilliseconds();
            _logService.EntryAppended += watch;
            try
            {
                double limit = waitSeconds * 1000d;
                while (waitedMilliseconds < limit && Volatile.Read(ref arrived) == 0)
                {
                    await _asyncMarshaler.DelayAsync(WaitStepMilliseconds, cancellationToken);
                    steppedMilliseconds += WaitStepMilliseconds;
                    waitedMilliseconds = Math.Max(steppedMilliseconds, _elapsedMilliseconds() - startedAt);
                }
            }
            finally
            {
                _logService.EntryAppended -= watch;
            }

            return Format(query, waitedMilliseconds / 1000d);
        }

        private string Format(LuaLogQuery query, double waitedSeconds)
        {
            IReadOnlyList<LuaLogEntry> entries = _logService.Query(query);
            string promptText = LuaLogFormatter.ToPromptText(entries, MaxPromptChars);
            long next = Math.Max(query.SinceSequence, LuaModLifecycleLog.LatestSequence(_logService));
            if (waitedSeconds < 0)
            {
                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = entries.Count,
                    next_sequence = next,
                    logs = promptText
                });
            }

            return JsonConvert.SerializeObject(new
            {
                success = true,
                count = entries.Count,
                next_sequence = next,
                waited_seconds = Math.Round(waitedSeconds, 1),
                logs = promptText
            });
        }

        private static bool HasAlert(IReadOnlyList<LuaLogEntry> entries, LuaLogLevel alert)
        {
            for (int index = 0; index < entries.Count; index++)
            {
                if (entries[index].Level >= alert)
                {
                    return true;
                }
            }

            return false;
        }

        private static string Fail(string message)
        {
            return JsonConvert.SerializeObject(new { success = false, message });
        }
    }
}
