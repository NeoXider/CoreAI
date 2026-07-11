using System;
using System.Collections.Generic;
using System.Linq;

namespace CoreAI.Config
{
    /// <summary>
    /// Defines per-role read and write access for game configuration keys.
    /// </summary>
    public class GameConfigPolicy
    {
        private readonly Dictionary<string, RoleConfigAccess> _roleAccess = new();
        private string[] _allKnownKeys = Array.Empty<string>();

        /// <summary>
        /// Per-role configuration access rules used by GameConfigPolicy.
        /// </summary>
        public sealed class RoleConfigAccess
        {
            /// <summary>Config keys this role may read explicitly.</summary>
            public HashSet<string> ReadKeys { get; set; } = new();

            /// <summary>Config keys this role may write explicitly.</summary>
            public HashSet<string> WriteKeys { get; set; } = new();

            /// <summary>Whether this role may read all config keys.</summary>
            public bool CanReadAll { get; set; }

            /// <summary>Whether this role may write all config keys.</summary>
            public bool CanWriteAll { get; set; }
        }

        /// <summary>
        /// Replaces the known configuration-key catalog used when a role has full access.
        /// </summary>
        public void SetKnownKeys(string[] keys)
        {
            _allKnownKeys = keys ?? Array.Empty<string>();
        }

        /// <summary>
        /// Configures explicit read/write key allowlists for a role.
        /// </summary>
        /// <param name="roleId">Agent role id.</param>
        /// <param name="readKeys">Keys the role may read; <c>null</c> grants read-all access.</param>
        /// <param name="writeKeys">Keys the role may write; <c>null</c> grants write-all access.</param>
        public void ConfigureRole(string roleId, string[] readKeys = null, string[] writeKeys = null)
        {
            RoleConfigAccess access = new();
            if (readKeys == null)
            {
                access.CanReadAll = true;
            }
            else
            {
                access.ReadKeys = new HashSet<string>(readKeys);
            }

            if (writeKeys == null)
            {
                access.CanWriteAll = true;
            }
            else
            {
                access.WriteKeys = new HashSet<string>(writeKeys);
            }

            _roleAccess[roleId] = access;
        }

        /// <summary>
        /// Grants read and write access to every known configuration key for a role.
        /// </summary>
        public void GrantFullAccess(string roleId)
        {
            _roleAccess[roleId] = new RoleConfigAccess
            {
                CanReadAll = true,
                CanWriteAll = true
            };
        }

        /// <summary>
        /// Revokes all configuration read/write access for a role.
        /// </summary>
        public void RevokeAccess(string roleId)
        {
            _roleAccess[roleId] = new RoleConfigAccess
            {
                CanReadAll = false,
                CanWriteAll = false
            };
        }

        /// <summary>
        /// Returns keys the role may write, expanding full-write access to the known key set.
        /// </summary>
        public string[] GetAllowedKeys(string roleId)
        {
            if (_roleAccess.TryGetValue(roleId, out RoleConfigAccess access))
            {
                if (access.CanWriteAll)
                {
                    return _allKnownKeys;
                }

                return access.WriteKeys.ToArray();
            }

            return Array.Empty<string>();
        }

        /// <summary>
        /// Returns whether a role may read a configuration key.
        /// </summary>
        public bool CanRead(string roleId, string key)
        {
            if (!_roleAccess.TryGetValue(roleId, out RoleConfigAccess access))
            {
                return false;
            }

            return access.CanReadAll || access.ReadKeys.Contains(key);
        }

        /// <summary>
        /// Returns whether a role may write a configuration key.
        /// </summary>
        public bool CanWrite(string roleId, string key)
        {
            if (!_roleAccess.TryGetValue(roleId, out RoleConfigAccess access))
            {
                return false;
            }

            return access.CanWriteAll || access.WriteKeys.Contains(key);
        }

        /// <summary>
        /// Allows subclasses to apply structured changes across one or more config keys.
        /// </summary>
        public virtual bool TryApplyChanges(string roleId, string json, out string[] appliedKeys, out string error)
        {
            appliedKeys = Array.Empty<string>();
            error = "";
            return false; // Fallback to simple save
        }

        /// <summary>
        /// Creates an LLM tool bound to this policy, store, and role.
        /// </summary>
        public GameConfigLlmTool CreateLlmTool(IGameConfigStore store, string roleId)
        {
            return new GameConfigLlmTool(store, this, roleId);
        }
    }
}
