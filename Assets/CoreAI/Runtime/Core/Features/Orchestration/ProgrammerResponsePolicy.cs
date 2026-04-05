using System.Text.RegularExpressions;

namespace CoreAI.Ai
{
    /// <summary>
    /// Политика валидации ответов Programmer: требует Lua код или JSON с execute_lua.
    /// </summary>
    public sealed class ProgrammerResponsePolicy : IRoleStructuredResponsePolicy
    {
        private static readonly Regex LuaCodeBlockRegex = new(
            @"```(?:lua)?\s*\n([\s\S]+?)```",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <inheritdoc />
        public bool ShouldValidate(string roleId)
        {
            return roleId == BuiltInAgentRoleIds.Programmer;
        }

        /// <inheritdoc />
        public bool TryValidate(string roleId, string rawContent, out string failureReason)
        {
            if (string.IsNullOrWhiteSpace(rawContent))
            {
                failureReason = "Response is empty or whitespace.";
                return false;
            }

            // Вариант 1: Markdown code block с Lua
            if (LuaCodeBlockRegex.IsMatch(rawContent))
            {
                failureReason = "";
                return true;
            }

            // Вариант 2: JSON с execute_lua
            if (rawContent.Contains("execute_lua") && rawContent.Contains("{") && rawContent.Contains("}"))
            {
                failureReason = "";
                return true;
            }

            // Вариант 3: Простой Lua код (без markdown, но с lua-ключевыми словами)
            var trimmed = rawContent.Trim();
            if ((trimmed.StartsWith("function") || trimmed.StartsWith("local ") || trimmed.Contains("return ")) &&
                !trimmed.StartsWith("{"))
            {
                failureReason = "";
                return true;
            }

            failureReason = "Expected Lua code block (```lua ... ```) or JSON with 'execute_lua' field. " +
                            "Got plain text instead.";
            return false;
        }
    }
}
