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
        /// changed by sanitization gain a stable hash of the original raw text and use the raw length as
        /// their prefix, so a literal hash-suffixed value cannot collide with them.
        /// </summary>
        private static void AppendPart(StringBuilder sb, string value)
        {
            if (sb.Length > 0)
            {
                sb.Append("__");
            }

            string sanitized = Sanitize(value);
            string raw = value?.Trim() ?? "";
            // WHY: An unset segment keeps the legacy "_" key. Lossy values already remapped when hashing
            // was introduced; their raw-length prefix now disambiguates literal hash-suffixed values while
            // every lossless value, including a lowercase GUID, keeps its legacy encoding.
            bool lossless = string.Equals(raw, sanitized, System.StringComparison.Ordinal);
            string encoded = raw.Length == 0 || lossless
                ? sanitized
                : sanitized + "-" + StableHash(value ?? "");
            int lengthPrefix = raw.Length == 0 ? encoded.Length : raw.Length;
            sb.Append(lengthPrefix).Append(':').Append(encoded);
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
