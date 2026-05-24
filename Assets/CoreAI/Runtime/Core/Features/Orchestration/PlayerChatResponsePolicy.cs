namespace CoreAI.Ai
{
    /// <summary>
    /// Structured-response policy for player chat roles.
    /// </summary>
    public sealed class PlayerChatResponsePolicy : IRoleStructuredResponsePolicy
    {
        /// <inheritdoc />
        public bool ShouldValidate(string roleId)
        {
            /* Implementation note in English. */
            return false;
        }

        /// <inheritdoc />
        public bool TryValidate(string roleId, string rawContent, out string failureReason)
        {
            /* Implementation note in English. */
            failureReason = "";
            return true;
        }
    }
}
