using System;
using System.Globalization;

namespace CoreAI.Ai.SelfRepair
{
    /// <summary>
    /// Where a mod's runtime error came from, read from its message: the script and author line of the
    /// structured Roblox-API prefix (<c>[mod:id script:path line:n]</c>), of the VM's chunk prefix
    /// (<c>[string "name"]:12:</c> or <c>name:12:</c>), or of a budget trip (<c>at line 12</c>). The key groups
    /// repeats of one failure: the script and line when known, otherwise the message without its location.
    /// </summary>
    public readonly struct LuaModErrorSite
    {
        private const string EnginePrefix = "Lua-CSharp: ";
        private const string ModChunkPrefix = "mod:";
        private const int MaximumKeyMessageLength = 120;

        private LuaModErrorSite(string script, int line, string message, string key)
        {
            Script = script ?? "";
            Line = line;
            Message = message ?? "";
            Key = key ?? "";
        }

        /// <summary>The script or chunk name the message names; empty when it names none.</summary>
        public string Script { get; }

        /// <summary>The author line; 0 when the message names none.</summary>
        public int Line { get; }

        /// <summary>The message without the engine and location prefixes.</summary>
        public string Message { get; }

        /// <summary>What repeats of the same failure share.</summary>
        public string Key { get; }

        /// <summary>True when the message names a line.</summary>
        public bool HasLine => Line > 0;

        /// <summary>
        /// The location for a player or a model: <c>modId:12</c> for the mod's own chunk, <c>script:12</c> for a
        /// named script; empty without a line.
        /// </summary>
        public string Describe(string modId)
        {
            if (!HasLine)
            {
                return "";
            }

            string script = Script.Length == 0 || IsInternalChunkName(Script) ? (modId ?? "") : Script;
            // WHY: a mod's chunk is named mod:<id> (MVP4 SC-19); the player reads the mod id, as for the internal name.
            if (script.Length > ModChunkPrefix.Length && script.StartsWith(ModChunkPrefix, StringComparison.Ordinal))
            {
                script = script.Substring(ModChunkPrefix.Length);
            }

            return script + ":" + Line.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Reads the site of <paramref name="message"/>; never throws.</summary>
        public static LuaModErrorSite Parse(string message)
        {
            string text = (message ?? "").Trim();
            if (text.StartsWith(EnginePrefix, StringComparison.Ordinal))
            {
                text = text.Substring(EnginePrefix.Length);
            }

            string script = "";
            int line = 0;
            string body = text;
            if (TryReadStructuredPrefix(text, out string structuredScript, out int structuredLine, out string rest))
            {
                script = structuredScript;
                line = structuredLine;
                body = rest;
            }

            if (line == 0 && TryReadChunkPrefix(body, out string chunkScript, out int chunkLine, out string chunkRest))
            {
                script = chunkScript;
                line = chunkLine;
                body = chunkRest;
            }
            else if (line == 0)
            {
                TryReadChunkPrefixAnywhere(body, out script, out line);
            }

            if (line == 0)
            {
                line = ReadAtLine(body);
            }

            string key = line > 0
                ? (IsInternalChunkName(script) ? "" : script) + ":" + line.ToString(CultureInfo.InvariantCulture)
                : Truncate(body, MaximumKeyMessageLength);
            return new LuaModErrorSite(script, line, body, key);
        }

        private static bool IsInternalChunkName(string script)
        {
            return script.Length == 0 || string.Equals(script, "sandbox_chunk", StringComparison.Ordinal);
        }

        private static bool TryReadStructuredPrefix(string text, out string script, out int line, out string rest)
        {
            script = "";
            line = 0;
            rest = text;
            if (!text.StartsWith("[mod:", StringComparison.Ordinal))
            {
                return false;
            }

            int close = text.IndexOf("] ", StringComparison.Ordinal);
            if (close < 0)
            {
                return false;
            }

            string header = text.Substring(5, close - 5);
            int scriptAt = header.IndexOf(" script:", StringComparison.Ordinal);
            int lineAt = header.LastIndexOf(" line:", StringComparison.Ordinal);
            if (scriptAt < 0 || lineAt < scriptAt + 8)
            {
                return false;
            }

            script = header.Substring(scriptAt + 8, lineAt - scriptAt - 8);
            if (script == "?")
            {
                script = "";
            }

            int.TryParse(header.Substring(lineAt + 6), NumberStyles.None, CultureInfo.InvariantCulture, out line);
            rest = text.Substring(close + 2);
            return true;
        }

        private static bool TryReadChunkPrefix(string text, out string script, out int line, out string rest)
        {
            script = "";
            line = 0;
            rest = text;
            int nameEnd;
            if (text.StartsWith("[string \"", StringComparison.Ordinal))
            {
                int quote = text.IndexOf("\"]", 9, StringComparison.Ordinal);
                if (quote < 0)
                {
                    return false;
                }

                script = text.Substring(9, quote - 9);
                nameEnd = quote + 2;
            }
            else
            {
                int colon = text.IndexOf(':');
                if (colon <= 0 || colon > 200 || text.IndexOf(' ', 0, colon) >= 0)
                {
                    return false;
                }

                script = text.Substring(0, colon);
                nameEnd = colon;
            }

            if (nameEnd >= text.Length || text[nameEnd] != ':')
            {
                script = "";
                return false;
            }

            int digitsStart = nameEnd + 1;
            int digitsEnd = digitsStart;
            while (digitsEnd < text.Length && char.IsDigit(text[digitsEnd]))
            {
                digitsEnd++;
            }

            if (digitsEnd == digitsStart || digitsEnd >= text.Length || text[digitsEnd] != ':' ||
                !int.TryParse(text.Substring(digitsStart, digitsEnd - digitsStart), NumberStyles.None,
                    CultureInfo.InvariantCulture, out line))
            {
                script = "";
                line = 0;
                return false;
            }

            rest = text.Substring(digitsEnd + 1).TrimStart();
            return true;
        }

        private static void TryReadChunkPrefixAnywhere(string text, out string script, out int line)
        {
            script = "";
            line = 0;
            int at = text.IndexOf("[string \"", StringComparison.Ordinal);
            if (at > 0 && TryReadChunkPrefix(text.Substring(at), out string found, out int foundLine, out _))
            {
                script = found;
                line = foundLine;
            }
        }

        private static int ReadAtLine(string text)
        {
            int at = text.IndexOf("at line ", StringComparison.Ordinal);
            if (at < 0)
            {
                return 0;
            }

            int start = at + 8;
            int end = start;
            while (end < text.Length && char.IsDigit(text[end]))
            {
                end++;
            }

            return end > start && int.TryParse(text.Substring(start, end - start), NumberStyles.None,
                CultureInfo.InvariantCulture, out int line)
                ? line
                : 0;
        }

        private static string Truncate(string text, int length)
        {
            return text.Length <= length ? text : text.Substring(0, length);
        }
    }
}
