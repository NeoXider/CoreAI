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
}
