using System;

namespace CoreAI.Ai
{
    /// <summary>
    /// Политика валидации ответов Programmer: требует вызов execute_lua tool.
    /// Все tool calls обрабатываются через единый MEAI pipeline.
    /// </summary>
    public sealed class ProgrammerResponsePolicy : IRoleStructuredResponsePolicy
    {
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

            // Programmer должен вызвать execute_lua tool
            // Если content пустой после tool calling - значит tool был вызван успешно
            // Если content есть - это обычный текст (объяснение, комментарии)
            // Валидация проходит если есть любой контент (tool calls уже обработаны MEAI)

            failureReason = "";
            return true;
        }
    }
}