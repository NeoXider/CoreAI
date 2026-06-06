using System;
using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>
    /// Defines the contract for prompt version registry implementations.
    /// </summary>
    public interface IPromptVersionRegistry
    {
        /// <summary>Registers a new value or callback with the target runtime registry.</summary>
        string Register(string roleId, string promptText, string label = null);

        /// <summary>Active prompt version for the requested role.</summary>
        string GetActive(string roleId);

        /// <summary>Rolls the requested role back to its previous prompt version when available.</summary>
        bool Rollback(string roleId);

        /// <summary>Recorded prompt version history for the requested role.</summary>
        IReadOnlyList<PromptVersion> GetHistory(string roleId);

        /// <summary>Resolves the requested prompt variant, falling back to the active prompt when needed.</summary>
        string ResolveVariant(string roleId, string variantName = null);

        /// <summary>Adds or replaces a named prompt variant for the requested role.</summary>
        void AddVariant(string roleId, string variantName, string promptText);
    }

    /// <summary>Snapshot of one prompt version stored for a role.</summary>
    public sealed class PromptVersion
    {
        /// <summary>Unique identifier for this prompt version.</summary>
        public string VersionId { get; set; }

        /// <summary>Prompt text stored in this version.</summary>
        public string Text { get; set; }

        /// <summary>An optional label that describes this prompt version.</summary>
        public string Label { get; set; }

        /// <summary>UTC timestamp when this prompt version was created.</summary>
        public DateTime CreatedUtc { get; set; }

        /// <summary>Whether this prompt version is the active version.</summary>
        public bool IsActive { get; set; }
    }

    /// <summary>
    /// In-memory registry for prompt versions and named prompt variants.
    /// </summary>
    public sealed class InMemoryPromptVersionRegistry : IPromptVersionRegistry
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, List<PromptVersion>> _history = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _activeIndex = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, string>> _variants = new(StringComparer.Ordinal);

        /// <inheritdoc />
        public string Register(string roleId, string promptText, string label = null)
        {
            if (string.IsNullOrEmpty(roleId))
            {
                throw new ArgumentException("roleId required", nameof(roleId));
            }

            if (string.IsNullOrEmpty(promptText))
            {
                throw new ArgumentException("promptText required", nameof(promptText));
            }

            lock (_lock)
            {
                if (!_history.TryGetValue(roleId, out List<PromptVersion> list))
                {
                    list = new List<PromptVersion>();
                    _history[roleId] = list;
                }

                foreach (PromptVersion pv in list)
                {
                    pv.IsActive = false;
                }

                string versionId = ComputeHash(promptText);
                PromptVersion version = new()
                {
                    VersionId = versionId,
                    Text = promptText,
                    Label = label ?? $"v{list.Count + 1}",
                    CreatedUtc = DateTime.UtcNow,
                    IsActive = true
                };

                list.Add(version);
                _activeIndex[roleId] = list.Count - 1;
                return versionId;
            }
        }

        /// <inheritdoc />
        public string GetActive(string roleId)
        {
            lock (_lock)
            {
                if (_history.TryGetValue(roleId, out List<PromptVersion> list) &&
                    _activeIndex.TryGetValue(roleId, out int idx) &&
                    idx >= 0 && idx < list.Count)
                {
                    return list[idx].Text;
                }

                return null;
            }
        }

        /// <inheritdoc />
        public bool Rollback(string roleId)
        {
            lock (_lock)
            {
                if (!_history.TryGetValue(roleId, out List<PromptVersion> list) ||
                    !_activeIndex.TryGetValue(roleId, out int idx) ||
                    idx <= 0)
                {
                    return false;
                }

                list[idx].IsActive = false;
                idx--;
                list[idx].IsActive = true;
                _activeIndex[roleId] = idx;
                return true;
            }
        }

        /// <inheritdoc />
        public IReadOnlyList<PromptVersion> GetHistory(string roleId)
        {
            lock (_lock)
            {
                if (_history.TryGetValue(roleId, out List<PromptVersion> list))
                {
                    return new List<PromptVersion>(list);
                }

                return Array.Empty<PromptVersion>();
            }
        }

        /// <inheritdoc />
        public string ResolveVariant(string roleId, string variantName = null)
        {
            if (string.IsNullOrEmpty(variantName))
            {
                return GetActive(roleId);
            }

            lock (_lock)
            {
                if (_variants.TryGetValue(roleId, out Dictionary<string, string> vars) &&
                    vars.TryGetValue(variantName, out string promptText))
                {
                    return promptText;
                }

                return GetActive(roleId);
            }
        }

        /// <inheritdoc />
        public void AddVariant(string roleId, string variantName, string promptText)
        {
            if (string.IsNullOrEmpty(roleId))
            {
                throw new ArgumentException("roleId required", nameof(roleId));
            }

            if (string.IsNullOrEmpty(variantName))
            {
                throw new ArgumentException("variantName required", nameof(variantName));
            }

            lock (_lock)
            {
                if (!_variants.TryGetValue(roleId, out Dictionary<string, string> vars))
                {
                    vars = new Dictionary<string, string>(StringComparer.Ordinal);
                    _variants[roleId] = vars;
                }

                vars[variantName] = promptText;
            }
        }

        private static string ComputeHash(string input)
        {
            // Simple deterministic hash for version tracking
            unchecked
            {
                int hash = 17;
                foreach (char c in input)
                {
                    hash = hash * 31 + c;
                }

                return hash.ToString("x8");
            }
        }
    }
}