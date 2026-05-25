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
