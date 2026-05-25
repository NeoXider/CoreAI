using System;

namespace CoreAI.Ai
{
    /// <summary>
    /// Structured-response policy for programmer roles.
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


            failureReason = "";
            return true;
        }
    }
}
