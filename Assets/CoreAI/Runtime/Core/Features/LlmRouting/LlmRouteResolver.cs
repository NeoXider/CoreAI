using System;
using System.Collections.Generic;
using System.Linq;

namespace CoreAI.Ai
{
    /// <summary>
    /// Default portable route resolver. Exact role matches beat wildcard matches at the same sort order.
    /// </summary>
    public sealed class LlmRouteResolver : ILlmRouteResolver
    {
        private readonly IReadOnlyDictionary<string, LlmRouteProfile> _profiles;
        private readonly List<LlmRouteRule> _rules;

        /// <summary>Creates a resolver over a portable route table.</summary>
        public LlmRouteResolver(LlmRouteTable table)
        {
            table ??= new LlmRouteTable();
            _profiles = table.ProfilesById();
            _rules = (table.Rules ?? Array.Empty<LlmRouteRule>())
                .Where(r => r != null && !string.IsNullOrWhiteSpace(r.RolePattern) && !string.IsNullOrWhiteSpace(r.ProfileId))
                .OrderBy(r => r.SortOrder)
                .ThenBy(r => Specificity(r.RolePattern))
                .ToList();
        }

        /// <inheritdoc />
        public LlmRouteResolution Resolve(string roleId)
        {
            string role = string.IsNullOrWhiteSpace(roleId) ? BuiltInAgentRoleIds.Creator : roleId.Trim();
            foreach (LlmRouteRule rule in _rules)
            {
                if (!RoleMatches(rule.RolePattern, role))
                {
                    continue;
                }

                string profileId = rule.ProfileId.Trim();
                if (_profiles.TryGetValue(profileId, out LlmRouteProfile profile))
                {
                    return new LlmRouteResolution(role, profile, rule);
                }
            }

            return new LlmRouteResolution(role, null, null);
        }

        /// <summary>Returns whether a role pattern matches a role id.</summary>
        public static bool RoleMatches(string pattern, string roleId)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                return false;
            }

            pattern = pattern.Trim();
            roleId = string.IsNullOrWhiteSpace(roleId) ? BuiltInAgentRoleIds.Creator : roleId.Trim();
            if (pattern == "*")
            {
                return true;
            }

            if (pattern.EndsWith("*", StringComparison.Ordinal) && pattern.Length > 1)
            {
                return roleId.StartsWith(pattern.Substring(0, pattern.Length - 1), StringComparison.Ordinal);
            }

            return string.Equals(pattern, roleId, StringComparison.Ordinal);
        }

        private static int Specificity(string pattern)
        {
            if (pattern == "*")
            {
                return 2;
            }

            return pattern != null && pattern.EndsWith("*", StringComparison.Ordinal) ? 1 : 0;
        }
    }
}
