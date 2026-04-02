using System;

namespace CoreAI.Ai
{
    /// <summary>
    /// Извлекает Lua из ответа LLM (fenced-блок с маркером lua). MVP до появления полноценного JSON-конверта для Programmer.
    /// </summary>
    public static class ProgrammerLuaResponseParser
    {
        /// <summary>Вырезать код из fenced markdown-блока с меткой lua (тройные обратные кавычки).</summary>
        public static bool TryExtractLuaCode(string content, out string luaCode)
        {
            luaCode = null;
            if (string.IsNullOrEmpty(content))
                return false;

            var start = IndexOfFencedBlock(content, "```lua", StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                start = IndexOfFencedBlock(content, "```Lua", StringComparison.Ordinal);
            if (start < 0)
                return false;

            var end = content.IndexOf("```", start, StringComparison.Ordinal);
            if (end < 0)
                return false;

            luaCode = content.Substring(start, end - start).Trim();
            return !string.IsNullOrEmpty(luaCode);
        }

        private static int IndexOfFencedBlock(string content, string fenceOpen, StringComparison comparison)
        {
            var i = content.IndexOf(fenceOpen, comparison);
            if (i < 0)
                return -1;
            var lineBreak = content.IndexOf('\n', i + fenceOpen.Length);
            if (lineBreak < 0)
                return -1;
            return lineBreak + 1;
        }
    }

    /// <summary>
    /// Защита от типичных артефактов ответов LLM: внешние markdown-ограждения и преамбула до первого JSON-объекта.
    /// Используйте перед <c>JsonUtility.FromJson</c> или эвристиками по полям.
    /// </summary>
    public static class LlmResponseSanitizer
    {
        /// <summary>Снимает внешний блок <c>```</c> / <c>```json</c> … <c>```</c> (один проход с каждого края).</summary>
        public static string StripMarkdownCodeFences(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return s;
            var t = s.Trim();
            if (!t.StartsWith("```", StringComparison.Ordinal))
                return t;
            var afterOpen = t.IndexOf('\n');
            if (afterOpen < 0)
                afterOpen = t.IndexOf('\r');
            if (afterOpen >= 0)
                t = t.Substring(afterOpen + 1).TrimStart('\r', '\n');
            else
                t = t.Substring(3).TrimStart();
            var close = t.LastIndexOf("```", StringComparison.Ordinal);
            if (close >= 0)
                t = t.Substring(0, close);
            return t.Trim();
        }

        /// <summary>
        /// Несколько внешних fence подряд (модель иногда дублирует) + первый сбалансированный <c>{ ... }</c>
        /// с учётом строк в кавычках.
        /// </summary>
        public static bool TryPrepareJsonObject(string raw, out string jsonObject)
        {
            jsonObject = null;
            if (string.IsNullOrWhiteSpace(raw))
                return false;
            var work = raw.Trim();
            for (var pass = 0; pass < 6 && work.StartsWith("```", StringComparison.Ordinal); pass++)
                work = StripMarkdownCodeFences(work);
            return TryExtractFirstBalancedJsonObject(work, out jsonObject);
        }

        private static bool TryExtractFirstBalancedJsonObject(string s, out string json)
        {
            json = null;
            if (string.IsNullOrEmpty(s))
                return false;
            var start = s.IndexOf('{');
            if (start < 0)
                return false;
            var depth = 0;
            var inString = false;
            var escape = false;
            for (var i = start; i < s.Length; i++)
            {
                var c = s[i];
                if (escape)
                {
                    escape = false;
                    continue;
                }

                if (inString)
                {
                    if (c == '\\')
                        escape = true;
                    else if (c == '"')
                        inString = false;
                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                    continue;
                }

                if (c == '{')
                    depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        json = s.Substring(start, i - start + 1);
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
