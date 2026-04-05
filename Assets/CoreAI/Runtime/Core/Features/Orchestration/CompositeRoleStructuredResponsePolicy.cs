using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>
    /// Композитная политика: маршрутизирует валидацию к специализированной политике по roleId.
    /// </summary>
    public sealed class CompositeRoleStructuredResponsePolicy : IRoleStructuredResponsePolicy
    {
        private readonly Dictionary<string, IRoleStructuredResponsePolicy> _policies;
        private readonly IRoleStructuredResponsePolicy _fallback;

        /// <summary>
        /// Создаёт композитную политику со всеми встроенными правилами.
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
                { BuiltInAgentRoleIds.PlayerChat, new PlayerChatResponsePolicy() }
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

            return _policies.TryGetValue(roleId, out var policy)
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

            if (_policies.TryGetValue(roleId, out var policy))
            {
                return policy.TryValidate(roleId, rawContent, out failureReason);
            }

            return _fallback.TryValidate(roleId, rawContent, out failureReason);
        }

        /// <summary>
        /// Получает специализированную политику для роли.
        /// </summary>
        public IRoleStructuredResponsePolicy GetPolicy(string roleId)
        {
            return _policies.TryGetValue(roleId, out var policy) ? policy : _fallback;
        }

        /// <summary>
        /// Регистрирует кастомную политику для роли.
        /// </summary>
        public void RegisterPolicy(string roleId, IRoleStructuredResponsePolicy policy)
        {
            _policies[roleId] = policy;
        }
    }
}
