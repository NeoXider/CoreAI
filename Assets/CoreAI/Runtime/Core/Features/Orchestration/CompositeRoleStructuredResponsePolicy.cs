using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>
    /// Delegates structured-response validation to role-specific policies.
    /// </summary>
    public sealed class CompositeRoleStructuredResponsePolicy : IRoleStructuredResponsePolicy
    {
        private readonly Dictionary<string, IRoleStructuredResponsePolicy> _policies;
        private readonly IRoleStructuredResponsePolicy _fallback;

        /// <summary>
        /// Initializes a new instance of CompositeRoleStructuredResponsePolicy.
        /// </summary>
        public CompositeRoleStructuredResponsePolicy()
        {
            _policies = new Dictionary<string, IRoleStructuredResponsePolicy>
            {
                { BuiltInAgentRoleIds.Programmer, new ProgrammerResponsePolicy() },
                { BuiltInAgentRoleIds.CoreMechanic, new CoreMechanicResponsePolicy() },
                { BuiltInAgentRoleIds.Creator, new CreatorResponsePolicy() },
                { BuiltInAgentRoleIds.Analyzer, new AnalyzerResponsePolicy() },
                { BuiltInAgentRoleIds.AiNpc, new AINpcResponsePolicy() },
                { BuiltInAgentRoleIds.PlainChat, new PlayerChatResponsePolicy() },
                { BuiltInAgentRoleIds.SmartChat, new PlayerChatResponsePolicy() }
            };
            _fallback = new NoOpRoleStructuredResponsePolicy();
        }

        /// <inheritdoc />
        public bool ShouldValidate(string roleId)
        {
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return _fallback.ShouldValidate(roleId);
            }

            return _policies.TryGetValue(roleId, out IRoleStructuredResponsePolicy policy)
                ? policy.ShouldValidate(roleId)
                : _fallback.ShouldValidate(roleId);
        }

        /// <inheritdoc />
        public bool TryValidate(string roleId, string rawContent, out string failureReason)
        {
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return _fallback.TryValidate(roleId, rawContent, out failureReason);
            }

            if (_policies.TryGetValue(roleId, out IRoleStructuredResponsePolicy policy))
            {
                return policy.TryValidate(roleId, rawContent, out failureReason);
            }

            return _fallback.TryValidate(roleId, rawContent, out failureReason);
        }

        /// <summary>
        /// Returns the structured-response policy registered for a role, or the fallback policy.
        /// </summary>
        public IRoleStructuredResponsePolicy GetPolicy(string roleId)
        {
            return _policies.TryGetValue(roleId, out IRoleStructuredResponsePolicy policy) ? policy : _fallback;
        }

        /// <summary>
        /// Registers or replaces the structured-response policy for a role.
        /// </summary>
        public void RegisterPolicy(string roleId, IRoleStructuredResponsePolicy policy)
        {
            _policies[roleId] = policy;
        }
    }
}