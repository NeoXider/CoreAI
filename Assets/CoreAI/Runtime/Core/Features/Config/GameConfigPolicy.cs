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
/// Executes SetKnownKeys API operation.
        /// </summary>
        public void SetKnownKeys(string[] keys)
        {
            _allKnownKeys = keys ?? Array.Empty<string>();
        }

        /// <summary>
/// Executes ConfigureRole API operation.
        /// </summary>
        /// <param name="roleId">The role id value.</param>
        /// <param name="readKeys">The read keys value.</param>
        /// <param name="writeKeys">The write keys value.</param>
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
/// Executes GrantFullAccess API operation.
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
/// Executes RevokeAccess API operation.
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
/// Executes GetAllowedKeys API operation.
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

            /* Implementation note in English. */
            return Array.Empty<string>();
        }

        /// <summary>
/// Executes CanRead API operation.
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
/// Executes CanWrite API operation.
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
/// Executes TryApplyChanges API operation.
        ///
        ///
        /// </summary>
        public virtual bool TryApplyChanges(string roleId, string json, out string[] appliedKeys, out string error)
        {
            appliedKeys = Array.Empty<string>();
            error = "";
            return false; // Fallback to simple save
        }

        /// <summary>
/// Executes CreateLlmTool API operation.
        /// </summary>
        public GameConfigLlmTool CreateLlmTool(IGameConfigStore store, string roleId)
        {
            return new GameConfigLlmTool(store, this, roleId);
        }
    }
}
