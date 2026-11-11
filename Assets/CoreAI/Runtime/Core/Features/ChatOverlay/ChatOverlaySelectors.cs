using System;
using System.Collections.Generic;

namespace CoreAI.ChatOverlay
{
    /// <summary>Which agent role answers the in-world chat; <c>/agent</c> switches it.</summary>
    public interface IChatAgentSelector
    {
        /// <summary>The role that answers the next message.</summary>
        string ActiveRoleId { get; }

        /// <summary>Roles <see cref="TrySelect"/> accepts; empty means any non-empty role id.</summary>
        IReadOnlyList<string> AvailableRoles { get; }

        /// <summary>Raised with the new role after a successful switch.</summary>
        event Action<string> ActiveRoleChanged;

        /// <summary>
        /// Switches to <paramref name="roleId"/>, matched without regard to case against
        /// <see cref="AvailableRoles"/>; <paramref name="selectedRoleId"/> is the role as it is spelled there.
        /// </summary>
        bool TrySelect(string roleId, out string selectedRoleId);
    }

    /// <summary>Default <see cref="IChatAgentSelector"/> over a role list the host supplies.</summary>
    public sealed class ChatAgentSelector : IChatAgentSelector
    {
        private readonly Func<IReadOnlyList<string>> _availableRoles;
        private string _activeRoleId;

        /// <summary>
        /// Creates a selector starting at <paramref name="initialRoleId"/>. <paramref name="availableRoles"/> is
        /// read on every call so roles registered later appear; null accepts any role id.
        /// </summary>
        public ChatAgentSelector(string initialRoleId, Func<IReadOnlyList<string>> availableRoles = null)
        {
            if (string.IsNullOrWhiteSpace(initialRoleId))
            {
                throw new ArgumentException("The chat needs an initial agent role.", nameof(initialRoleId));
            }

            _activeRoleId = initialRoleId.Trim();
            _availableRoles = availableRoles;
        }

        /// <inheritdoc />
        public event Action<string> ActiveRoleChanged;

        /// <inheritdoc />
        public string ActiveRoleId => _activeRoleId;

        /// <inheritdoc />
        public IReadOnlyList<string> AvailableRoles => _availableRoles?.Invoke() ?? Array.Empty<string>();

        /// <inheritdoc />
        public bool TrySelect(string roleId, out string selectedRoleId)
        {
            selectedRoleId = "";
            string wanted = (roleId ?? "").Trim();
            if (wanted.Length == 0)
            {
                return false;
            }

            IReadOnlyList<string> available = AvailableRoles;
            if (available.Count == 0)
            {
                selectedRoleId = wanted;
            }
            else
            {
                foreach (string candidate in available)
                {
                    if (string.Equals(candidate, wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        selectedRoleId = candidate;
                        break;
                    }
                }

                if (selectedRoleId.Length == 0)
                {
                    return false;
                }
            }

            bool changed = !string.Equals(_activeRoleId, selectedRoleId, StringComparison.Ordinal);
            _activeRoleId = selectedRoleId;
            if (changed)
            {
                ActiveRoleChanged?.Invoke(selectedRoleId);
            }

            return true;
        }
    }

    /// <summary>One model routing profile the player can pick with <c>/model</c>.</summary>
    public readonly struct ChatModelProfile
    {
        private readonly string _id;
        private readonly string _displayName;
        private readonly string _status;

        /// <summary>Creates a profile entry.</summary>
        public ChatModelProfile(string id, string displayName, string status = "")
        {
            _id = id ?? "";
            _displayName = displayName ?? "";
            _status = status ?? "";
        }

        /// <summary>Stable profile id.</summary>
        public string Id => _id ?? "";

        /// <summary>Name shown to the player; falls back to <see cref="Id"/>.</summary>
        public string DisplayName => string.IsNullOrWhiteSpace(_displayName) ? Id : _displayName;

        /// <summary>Endpoint state such as <c>Ready</c>; may be empty.</summary>
        public string Status => _status ?? "";
    }

    /// <summary>
    /// Model routing seen by <c>/model</c>: the same per-role profile assignment the Hub Settings page edits.
    /// An empty profile id means automatic routing (the role's configured default).
    /// </summary>
    public interface IChatModelSelector
    {
        /// <summary>False when no routing is configured; <c>/model</c> then says so.</summary>
        bool IsAvailable { get; }

        /// <summary>Profiles the player can choose.</summary>
        IReadOnlyList<ChatModelProfile> GetProfiles();

        /// <summary>The profile assigned to <paramref name="roleId"/>; empty for automatic.</summary>
        string GetProfileForRole(string roleId);

        /// <summary>Assigns <paramref name="profileId"/> (empty for automatic) to <paramref name="roleId"/>.</summary>
        bool TryAssign(string roleId, string profileId, out string message);
    }
}
