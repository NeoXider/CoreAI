namespace CoreAI.Ai
{
    /// <summary>
    /// Политика валидации ответов свободного игрового чата (<see cref="BuiltInAgentRoleIds.PlainChat"/>, <see cref="BuiltInAgentRoleIds.SmartChat"/>): без схемной валидации.
    /// </summary>
    public sealed class PlayerChatResponsePolicy : IRoleStructuredResponsePolicy
    {
        /// <inheritdoc />
        public bool ShouldValidate(string roleId)
        {
            // Свободный чат не требует схемной валидации — всегда false.
            return false;
        }

        /// <inheritdoc />
        public bool TryValidate(string roleId, string rawContent, out string failureReason)
        {
            // Свободный текст разрешён — без structured validation.
            failureReason = "";
            return true;
        }
    }
}