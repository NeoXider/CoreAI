namespace CoreAI.Ai
{
    /// <summary>
    /// Политика валидации ответов PlayerChat: без валидации (свободный текст разрешён).
    /// </summary>
    public sealed class PlayerChatResponsePolicy : IRoleStructuredResponsePolicy
    {
        /// <inheritdoc />
        public bool ShouldValidate(string roleId)
        {
            // PlayerChat не требует валидации - всегда возвращаем false
            return false;
        }

        /// <inheritdoc />
        public bool TryValidate(string roleId, string rawContent, out string failureReason)
        {
            // PlayerChat может отвечать свободно — никакой валидации
            failureReason = "";
            return true;
        }
    }
}
