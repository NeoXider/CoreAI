using System;
using System.Text;

namespace CoreAI.Ai
{
    /// <summary>
    /// Достаёт исполняемый Lua из ответа LLM: сначала MEAI tool call, затем markdown fenced lua, затем JSON ExecuteLua.
    /// </summary>
    public static class AiLuaPayloadParser
    {
        /// <summary>Вернуть исполняемый Lua из текста конверта (fenced или JSON <c>ExecuteLua</c>).</summary>
        public static bool TryGetExecutableLua(string payload, out string luaCode)
        {
            luaCode = null;
            if (string.IsNullOrWhiteSpace(payload))
            {
                return false;
            }

            if (ProgrammerLuaResponseParser.TryExtractLuaCode(payload, out luaCode))
            {
                return true;
            }

            if (LlmResponseSanitizer.TryPrepareJsonObject(payload, out string jsonBody) &&
                TryExtractExecuteLuaJson(jsonBody, out luaCode))
            {
                return true;
            }

            return TryExtractExecuteLuaJson(payload, out luaCode);
        }

        private static bool TryExtractExecuteLuaJson(string s, out string code)
        {
            code = null;
            int anchor = IndexOfCommandType(s, "ExecuteLua");
            if (anchor < 0)
            {
                return false;
            }

            return TryReadJsonStringProperty(s, "code", anchor, out code) && !string.IsNullOrWhiteSpace(code);
        }

        private static int IndexOfCommandType(string s, string typeValue)
        {
            const string pat = "\"commandType\"";
            int i = 0;
            while (i < s.Length)
            {
                int p = s.IndexOf(pat, i, StringComparison.OrdinalIgnoreCase);
                if (p < 0)
                {
                    return -1;
                }

                int colon = s.IndexOf(':', p);
                if (colon < 0)
                {
                    return -1;
                }

                int startQ = s.IndexOf('"', colon);
                if (startQ < 0)
                {
                    return -1;
                }

                int endQ = FindClosingJsonStringEnd(s, startQ + 1);
                if (endQ < 0)
                {
                    return -1;
                }

                string val = s.Substring(startQ + 1, endQ - startQ - 1);
                if (val.Equals(typeValue, StringComparison.OrdinalIgnoreCase))
                {
                    return p;
                }

                i = endQ + 1;
            }

            return -1;
        }

        private static int FindClosingJsonStringEnd(string s, int from)
        {
            for (int i = from; i < s.Length; i++)
            {
                if (s[i] == '\\')
                {
                    i++;
                    continue;
                }

                if (s[i] == '"')
                {
                    return i;
                }
            }

            return -1;
        }

        private static bool TryReadJsonStringProperty(string s, string prop, int searchFrom, out string value)
        {
            value = null;
            string key = "\"" + prop + "\"";
            int k = s.IndexOf(key, searchFrom, StringComparison.OrdinalIgnoreCase);
            if (k < 0)
            {
                return false;
            }

            int colon = s.IndexOf(':', k + key.Length);
            if (colon < 0)
            {
                return false;
            }

            int i = colon + 1;
            while (i < s.Length && char.IsWhiteSpace(s[i]))
            {
                i++;
            }

            if (i >= s.Length || s[i] != '"')
            {
                return false;
            }

            i++;
            StringBuilder sb = new();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '\\')
                {
                    if (i >= s.Length)
                    {
                        return false;
                    }

                    char e = s[i++];
                    switch (e)
                    {
                        case 'n':
                            sb.Append('\n');
                            break;
                        case 'r':
                            sb.Append('\r');
                            break;
                        case 't':
                            sb.Append('\t');
                            break;
                        case '"':
                            sb.Append('"');
                            break;
                        case '\\':
                            sb.Append('\\');
                            break;
                        default:
                            sb.Append(e);
                            break;
                    }

                    continue;
                }

                if (c == '"')
                {
                    value = sb.ToString();
                    return true;
                }

                sb.Append(c);
            }

            return false;
        }
    }
}