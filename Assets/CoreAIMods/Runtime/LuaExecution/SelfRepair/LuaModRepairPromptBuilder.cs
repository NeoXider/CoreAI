using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CoreAI.Ai.Logging;

namespace CoreAI.Ai.SelfRepair
{
    /// <summary>What the repair request is built from.</summary>
    public sealed class LuaModRepairContext
    {
        /// <summary>The failing mod.</summary>
        public string ModId { get; set; } = "";

        /// <summary>1-based attempt number.</summary>
        public int Attempt { get; set; } = 1;

        /// <summary>Attempts the incident has.</summary>
        public int MaxAttempts { get; set; } = LuaModRuntimeRepairOptions.DefaultMaxAttemptsPerIncident;

        /// <summary>Why the incident opened.</summary>
        public LuaModRepairTrigger Trigger { get; set; }

        /// <summary>The newest error message.</summary>
        public string Error { get; set; } = "";

        /// <summary>Where it came from.</summary>
        public LuaModErrorSite Site { get; set; }

        /// <summary>The source the mod runs now; empty when unknown.</summary>
        public string Source { get; set; } = "";

        /// <summary>The mod's newest log entries, oldest first.</summary>
        public IReadOnlyList<LuaLogEntry> RecentLogs { get; set; } = Array.Empty<LuaLogEntry>();

        /// <summary>The player's request that produced the mod; empty when unknown.</summary>
        public string PlayerRequest { get; set; } = "";

        /// <summary>From the second attempt on: whether the previous attempt reloaded the mod.</summary>
        public bool PreviousReloaded { get; set; }

        /// <summary>From the second attempt on: the model's own summary of the previous attempt; empty when it gave none.</summary>
        public string PreviousSummary { get; set; } = "";

        /// <summary>From the second attempt on: the error the previous attempt was asked to fix.</summary>
        public string PreviousError { get; set; } = "";

        /// <summary>From the second attempt on: where that error came from.</summary>
        public LuaModErrorSite PreviousSite { get; set; }
    }

    /// <summary>
    /// Builds the one message a repair turn sends: what failed and where (author file and line), the mod's last log
    /// lines, the source around the line with line numbers, what the player asked for, and the three steps the model
    /// takes with the tools it already has (<c>manage_mods reload</c>, then <c>get_mod_logs</c> with
    /// <c>since_reload</c> and <c>wait_seconds</c>). It uses the <c>lua_error</c> / <c>fix_this_lua</c> fields the
    /// Programmer prompt already answers. Bounded to <see cref="MaximumCharacters"/>.
    /// </summary>
    public static class LuaModRepairPromptBuilder
    {
        /// <summary>Hard cap on the whole message.</summary>
        public const int MaximumCharacters = 3200;

        /// <summary>Cap on the log tail.</summary>
        public const int MaximumLogCharacters = 900;

        /// <summary>Cap on the source excerpt.</summary>
        public const int MaximumSourceCharacters = 1500;

        /// <summary>Cap on the player's request.</summary>
        public const int MaximumRequestCharacters = 300;

        /// <summary>Lines of source shown before and after the error line.</summary>
        public const int ContextLines = 12;

        /// <summary>Builds the message.</summary>
        public static string Build(LuaModRepairContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            string id = (context.ModId ?? "").Trim();
            string where = context.Site.Describe(id);
            StringBuilder text = new();
            text.Append("Runtime repair, attempt ").Append(context.Attempt.ToString(CultureInfo.InvariantCulture))
                .Append(" of ").Append(Math.Max(context.Attempt, context.MaxAttempts).ToString(CultureInfo.InvariantCulture))
                .Append(". The Lua mod '").Append(id).Append("' loaded fine but now fails while the game runs (")
                .Append(Describe(context.Trigger)).Append(where.Length > 0 ? " at " + where : "")
                .Append("). Fix it now without asking the player:\n")
                .Append("1. Find the cause in fix_this_lua below; call manage_mods get_source mod_id='").Append(id)
                .Append("' if you need more of the file (it comes in pages: follow next_from_line).\n")
                .Append("2. Apply the fix under the same id: manage_mods edit mod_id='").Append(id)
                .Append("' with old_text copied exactly from the source (without the 'N| ' prefixes) and new_text, or ")
                .Append("manage_mods reload mod_id='").Append(id)
                .Append("' with the whole corrected source. Keep its intent, id and header and every feature the ")
                .Append("player asked for; do not delete a handler to silence the error; do not create a new mod.\n")
                .Append("3. Check it with get_mod_logs mod_id='").Append(id)
                .Append("' since_reload=true wait_seconds=5 level='warn'; if it still fails, fix and reload again.\n")
                .Append("Then reply with one line that starts with '").Append(LuaModRepairChangeReport.SummaryMarker)
                .Append("' and tells the player in plain words, in one or two short sentences, what you changed.\n");

            text.Append("lua_error=").Append(Clip(SingleLine(context.Error), 400));
            if (where.Length > 0)
            {
                text.Append(" (").Append(where).Append(')');
            }

            text.Append('\n');
            if (context.Attempt > 1)
            {
                text.Append(PreviousAttempt(context, id)).Append('\n');
            }
            string request = SingleLine(context.PlayerRequest);
            if (request.Length > 0)
            {
                text.Append("player_request=").Append(Clip(request, MaximumRequestCharacters)).Append('\n');
            }

            string logs = LuaLogFormatter.ToPromptText(context.RecentLogs ?? Array.Empty<LuaLogEntry>(), MaximumLogCharacters);
            if (logs.Length > 0)
            {
                text.Append("recent_logs:\n").Append(logs).Append('\n');
            }

            text.Append(SourceExcerpt(context.Source, context.Site.Line));
            return text.Length <= MaximumCharacters ? text.ToString() : text.ToString(0, MaximumCharacters);
        }

        /// <summary>
        /// The numbered source lines around <paramref name="line"/> (the head of the file without a line), headed
        /// <c>fix_this_lua (lines a-b of n):</c>; bounded to <see cref="MaximumSourceCharacters"/>.
        /// </summary>
        public static string SourceExcerpt(string source, int line)
        {
            if (string.IsNullOrEmpty(source))
            {
                return "fix_this_lua: (source not available; call manage_mods get_source first)\n";
            }

            string[] lines = source.Replace("\r\n", "\n").Split('\n');
            int first = line > 0 ? Math.Max(1, line - ContextLines) : 1;
            int last = line > 0 ? Math.Min(lines.Length, line + ContextLines) : Math.Min(lines.Length, 2 * ContextLines);
            StringBuilder body = new();
            int shownLast = first - 1;
            for (int number = first; number <= last; number++)
            {
                string row = (number == line ? ">" : " ") + number.ToString(CultureInfo.InvariantCulture) + "| " +
                             lines[number - 1];
                if (body.Length + row.Length + 1 > MaximumSourceCharacters)
                {
                    break;
                }

                body.Append(row).Append('\n');
                shownLast = number;
            }

            return "fix_this_lua (lines " + first.ToString(CultureInfo.InvariantCulture) + "-" +
                   shownLast.ToString(CultureInfo.InvariantCulture) + " of " +
                   lines.Length.ToString(CultureInfo.InvariantCulture) + "):\n" + body;
        }

        /// <summary>
        /// What the previous attempt did and what came of it, so a further attempt is a new question: whether it
        /// reloaded the mod and its own summary of the change, and whether the mod now fails the same way.
        /// </summary>
        public static string PreviousAttempt(LuaModRepairContext context, string modId)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            StringBuilder text = new("previous_attempt: ");
            if (!context.PreviousReloaded)
            {
                text.Append("you did not reload the mod, so it still runs the failing code; call manage_mods reload this time.");
                return text.ToString();
            }

            text.Append("you reloaded it");
            string summary = SingleLine(context.PreviousSummary);
            if (summary.Length > 0)
            {
                text.Append(" (you said: \"").Append(Clip(summary, LuaModRepairChangeReport.MaximumSummaryCharacters))
                    .Append("\")");
            }

            bool sameError = string.Equals(SingleLine(context.PreviousError), SingleLine(context.Error),
                StringComparison.Ordinal);
            bool sameSite = string.Equals(context.PreviousSite.Describe(modId), context.Site.Describe(modId),
                StringComparison.Ordinal);
            string where = context.Site.Describe(modId);
            text.Append(sameError && sameSite
                ? ", but it fails with the same error at the same line: that change did not reach the cause."
                : sameError
                    ? ", but it still fails with the same error, now at " + (where.Length > 0 ? where : "another line") + "."
                    : ", and it now fails with the error above instead of \"" +
                      Clip(SingleLine(context.PreviousError), 160) + "\".");
            text.Append(" The source below is your last version; try a different fix.");
            return text.ToString();
        }

        /// <summary>The trigger in a few words, for the model and the player.</summary>
        public static string Describe(LuaModRepairTrigger trigger)
        {
            switch (trigger)
            {
                case LuaModRepairTrigger.Streak:
                    return "it fails on every call";
                case LuaModRepairTrigger.BudgetTrips:
                    return "it runs past its time or instruction budget";
                case LuaModRepairTrigger.Quarantined:
                    return "it failed so often that its handlers are paused";
                default:
                    return "the same error repeats";
            }
        }

        private static string SingleLine(string text)
        {
            return (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        }

        private static string Clip(string text, int length)
        {
            return text.Length <= length ? text : text.Substring(0, length) + "...";
        }
    }
}
