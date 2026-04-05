using System;
using System.Collections.Generic;
using System.Linq;

namespace CoreAI.Config
{
    /// <summary>
    /// Политика доступа к конфигам: какие роли могут читать/менять какие ключи.
    /// Настраивается на игру: по умолчанию все роли имеют доступ ко всем ключам.
    /// </summary>
    public class GameConfigPolicy
    {
        private readonly Dictionary<string, RoleConfigAccess> _roleAccess = new();
        private string[] _allKnownKeys = Array.Empty<string>();

        /// <summary>
        /// Конфигурация доступа для одной роли.
        /// </summary>
        public sealed class RoleConfigAccess
        {
            /// <summary>Ключи которые роль может читать.</summary>
            public HashSet<string> ReadKeys { get; set; } = new();

            /// <summary>Ключи которые роль может изменять.</summary>
            public HashSet<string> WriteKeys { get; set; } = new();

            /// <summary>Разрешить все ключи (для чтения).</summary>
            public bool CanReadAll { get; set; }

            /// <summary>Разрешить все ключи (для записи).</summary>
            public bool CanWriteAll { get; set; }
        }

        /// <summary>
        /// Устанавливает список всех известных ключей конфигов.
        /// </summary>
        public void SetKnownKeys(string[] keys)
        {
            _allKnownKeys = keys ?? Array.Empty<string>();
        }

        /// <summary>
        /// Настраивает доступ для роли.
        /// </summary>
        /// <param name="roleId">Роль агента.</param>
        /// <param name="readKeys">Ключи для чтения (null = все).</param>
        /// <param name="writeKeys">Ключи для записи (null = все).</param>
        public void ConfigureRole(string roleId, string[] readKeys = null, string[] writeKeys = null)
        {
            var access = new RoleConfigAccess();
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
        /// Разрешает роли доступ ко всем конфигам.
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
        /// Запрещает роли доступ к конфигам.
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
        /// Получает ключи доступные роли для чтения и записи.
        /// </summary>
        public string[] GetAllowedKeys(string roleId)
        {
            if (_roleAccess.TryGetValue(roleId, out var access))
            {
                if (access.CanWriteAll)
                {
                    return _allKnownKeys;
                }
                return access.WriteKeys.ToArray();
            }

            // По умолчанию: нет доступа
            return Array.Empty<string>();
        }

        /// <summary>
        /// Проверяет может ли роль читать конфиг.
        /// </summary>
        public bool CanRead(string roleId, string key)
        {
            if (!_roleAccess.TryGetValue(roleId, out var access)) return false;
            return access.CanReadAll || access.ReadKeys.Contains(key);
        }

        /// <summary>
        /// Проверяет может ли роль изменять конфиг.
        /// </summary>
        public bool CanWrite(string roleId, string key)
        {
            if (!_roleAccess.TryGetValue(roleId, out var access)) return false;
            return access.CanWriteAll || access.WriteKeys.Contains(key);
        }

        /// <summary>
        /// Пытается применить изменения из JSON.
        /// По умолчанию — просто сохраняет JSON для первого разрешённого ключа.
        /// Переопределите для сложной логики (частичное обновление, валидация и т.д.).
        /// </summary>
        public virtual bool TryApplyChanges(string roleId, string json, out string[] appliedKeys, out string error)
        {
            appliedKeys = Array.Empty<string>();
            error = "";
            return false; // Fallback to simple save
        }

        /// <summary>
        /// Создаёт ILlmTool обёртку для использования в оркестраторе.
        /// </summary>
        public GameConfigLlmTool CreateLlmTool(IGameConfigStore store, string roleId)
        {
            return new GameConfigLlmTool(store, this, roleId);
        }
    }
}
