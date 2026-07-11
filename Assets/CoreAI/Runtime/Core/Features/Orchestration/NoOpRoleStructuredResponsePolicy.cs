namespace CoreAI.Ai
{
    /// <summary>Structured-response policy that accepts all role output.</summary>
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
