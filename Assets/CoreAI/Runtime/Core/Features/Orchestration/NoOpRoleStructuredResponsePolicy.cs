namespace CoreAI.Ai
{
    /// <summary>Политика по умолчанию: не валидировать ответы, без повторных запросов.</summary>
    public sealed class NoOpRoleStructuredResponsePolicy : IRoleStructuredResponsePolicy
    {
        /// <inheritdoc />
        public bool ShouldValidate(string roleId)
        {
            return false;
        }

        /// <inheritdoc />
        public bool TryValidate(string roleId, string rawContent, out string failureReason)
        {
            failureReason = "";
            return true;
        }
    }
}