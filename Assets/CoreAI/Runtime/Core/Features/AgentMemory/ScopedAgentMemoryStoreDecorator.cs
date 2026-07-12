using System.Security.Cryptography;
using System.Text;

namespace CoreAI.Ai
{
    /// <summary>
    /// Decorates an existing memory store and maps role ids to scoped keys.
    /// </summary>
    public sealed class ScopedAgentMemoryStoreDecorator : IAgentMemoryStore
    {
        private readonly IAgentMemoryStore _inner;
        private readonly IAgentMemoryScopeProvider _scopeProvider;

        /// <summary>
        /// Creates a scoped memory store wrapper.
        /// </summary>
        public ScopedAgentMemoryStoreDecorator(
            IAgentMemoryStore inner,
            IAgentMemoryScopeProvider scopeProvider)
        {
            _inner = inner ?? new NullAgentMemoryStore();
            _scopeProvider = scopeProvider ?? new DefaultAgentMemoryScopeProvider();
        }

        /// <inheritdoc />
        public bool TryLoad(string roleId, out AgentMemoryState state)
        {
            return _inner.TryLoad(ToScopedKey(roleId), out state);
        }

        /// <inheritdoc />
        public void Save(string roleId, AgentMemoryState state)
        {
            _inner.Save(ToScopedKey(roleId), state);
        }

        /// <inheritdoc />
        public void Clear(string roleId)
        {
            _inner.Clear(ToScopedKey(roleId));
        }

        /// <inheritdoc />
        public void ClearChatHistory(string roleId)
        {
            _inner.ClearChatHistory(ToScopedKey(roleId));
        }

        /// <inheritdoc />
        public void AppendChatMessage(string roleId, string role, string content, bool persistToDisk = true)
        {
            _inner.AppendChatMessage(ToScopedKey(roleId), role, content, persistToDisk);
        }

        /// <inheritdoc />
        public ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0)
        {
            return _inner.GetChatHistory(ToScopedKey(roleId), maxMessages);
        }

        private string ToScopedKey(string roleId)
        {
            roleId = string.IsNullOrWhiteSpace(roleId) ? BuiltInAgentRoleIds.Creator : roleId.Trim();
            AgentMemoryScope scope = _scopeProvider.GetScope(roleId);
            if (string.IsNullOrWhiteSpace(scope.TenantId) &&
                string.IsNullOrWhiteSpace(scope.UserId) &&
                string.IsNullOrWhiteSpace(scope.SessionId) &&
                string.IsNullOrWhiteSpace(scope.TopicId))
            {
                return roleId;
            }

            StringBuilder sb = new(128);
            AppendPart(sb, scope.TenantId);
            AppendPart(sb, scope.UserId);
            AppendPart(sb, scope.SessionId);
            AppendPart(sb, scope.TopicId);
            AppendPart(sb, roleId);
            return sb.ToString();
        }

        /// <summary>
        /// Keeps the legacy length-prefixed mapping for values containing only safe characters. Values
        /// changed by sanitization gain a stable hash of the original raw text, so their new keys cannot
        /// collide; existing lossy keys are intentionally not reused and require host-managed migration.
        /// </summary>
        private static void AppendPart(StringBuilder sb, string value)
        {
            if (sb.Length > 0)
            {
                sb.Append("__");
            }

            string sanitized = Sanitize(value);
            string raw = value?.Trim() ?? "";
            // WHY: an unset (null/whitespace) segment is the default in every existing install - it must
            // keep the legacy "_" key, or upgrading orphans all previously saved memory. The legacy
            // empty-vs-literal-"_" overlap is retained knowingly; the hash suffix only has to separate
            // genuinely distinct raw values that sanitize to the same text (the cross-user leak).
            bool lossless = string.Equals(raw, sanitized, System.StringComparison.Ordinal);
            string encoded = raw.Length == 0 || lossless && !HasHashLikeSuffix(sanitized)
                ? sanitized
                : sanitized + "-" + StableHash(value ?? "");
            sb.Append(encoded.Length).Append(':').Append(encoded);
        }

        private static bool HasHashLikeSuffix(string value)
        {
            int suffixStart = value.Length - 12;
            if (suffixStart <= 0 || value[suffixStart - 1] != '-')
            {
                return false;
            }

            for (int i = suffixStart; i < value.Length; i++)
            {
                char ch = value[i];
                if (!((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f')))
                {
                    return false;
                }
            }

            return true;
        }

        private static string StableHash(string value)
        {
            byte[] digest;
            using (SHA256 sha = SHA256.Create())
            {
                digest = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
            }

            StringBuilder sb = new(12);
            for (int i = 0; i < 6; i++)
            {
                sb.Append(digest[i].ToString("x2"));
            }

            return sb.ToString();
        }

        private static string Sanitize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "_";
            }

            StringBuilder sb = new(value.Length);
            foreach (char ch in value.Trim())
            {
                sb.Append(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' ? ch : '_');
            }

            return sb.ToString();
        }
    }
}
