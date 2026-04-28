using System;
using System.Collections.Generic;
using System.Linq;

namespace CoreAI.Ai
{
    /// <summary>
    /// Portable route table containing profiles and role rules without Unity dependencies.
    /// </summary>
    public sealed class LlmRouteTable
    {
        /// <summary>Named profiles available to route rules.</summary>
        public IReadOnlyList<LlmRouteProfile> Profiles { get; set; } = Array.Empty<LlmRouteProfile>();

        /// <summary>Role matching rules.</summary>
        public IReadOnlyList<LlmRouteRule> Rules { get; set; } = Array.Empty<LlmRouteRule>();

        /// <summary>Returns validation errors for duplicate profiles and missing profile references.</summary>
        public IReadOnlyList<string> Validate()
        {
            List<string> errors = new();
            HashSet<string> profileIds = new(StringComparer.Ordinal);
            foreach (LlmRouteProfile profile in Profiles ?? Array.Empty<LlmRouteProfile>())
            {
                if (profile == null || string.IsNullOrWhiteSpace(profile.ProfileId))
                {
                    errors.Add("Route profile id is empty.");
                    continue;
                }

                if (!profileIds.Add(profile.ProfileId.Trim()))
                {
                    errors.Add($"Duplicate route profile id: {profile.ProfileId.Trim()}.");
                }
            }

            foreach (LlmRouteRule rule in Rules ?? Array.Empty<LlmRouteRule>())
            {
                if (rule == null)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(rule.RolePattern))
                {
                    errors.Add("Route rule role pattern is empty.");
                }

                string profileId = rule.ProfileId?.Trim();
                if (string.IsNullOrWhiteSpace(profileId))
                {
                    errors.Add($"Route rule '{rule.RolePattern}' has empty profile id.");
                }
                else if (!profileIds.Contains(profileId))
                {
                    errors.Add($"Route rule '{rule.RolePattern}' references missing profile '{profileId}'.");
                }
            }

            return errors;
        }

        /// <summary>Creates a dictionary of profiles by id.</summary>
        public IReadOnlyDictionary<string, LlmRouteProfile> ProfilesById()
        {
            return (Profiles ?? Array.Empty<LlmRouteProfile>())
                .Where(p => p != null && !string.IsNullOrWhiteSpace(p.ProfileId))
                .GroupBy(p => p.ProfileId.Trim(), StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        }
    }
}
