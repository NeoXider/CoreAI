namespace CoreAI.Ai
{
    /// <summary>
    /// Проверка сырого ответа LLM для роли; при неудаче оркестратор делает один повтор с подсказкой в hint.
    /// </summary>
    public interface IRoleStructuredResponsePolicy
    {
        /// <summary>Нужно ли проверять <paramref name="rawContent"/> для данной роли.</summary>
        bool ShouldValidate(string roleId);

        /// <summary>Проверка формата ответа; при <c>false</c> заполняется <paramref name="failureReason"/> для повторного запроса.</summary>
        bool TryValidate(string roleId, string rawContent, out string failureReason);
    }
}
